using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JustEnoughTools.Protection;
using JustEnoughTools.Updates;
using Microsoft.Win32.SafeHandles;

namespace JustEnoughTools
{
    // This coordinator only transports the fixed configuration request. It does not
    // accept paths, executable names or arbitrary commands from its peer.
    internal static class ProtectionCoordinator
    {
        private const string WorkerArgument = "--configuration-worker";
        private const int ConnectTimeoutMilliseconds = 45000;
        private const int HandshakeTimeoutMilliseconds = 10000;
        private const int ProgressTimeoutMilliseconds = 10 * 60 * 1000;
        internal const int MaximumFrameBytes = 8192;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal enum MessageKind : byte { Hello = 1, Accepted = 2, Progress = 3, Result = 4 }

        internal sealed class Frame
        {
            internal MessageKind Kind;
            internal byte[] Payload;
        }

        internal sealed class Outcome
        {
            internal ConfigurationStatus Status;
            internal bool PerformedChanges;
            internal bool ChangesMayHaveOccurred;
            internal bool EngineOffVerified;
            internal bool VirusUiHiddenVerified;
            internal bool PersistenceVerified;
            internal string Message;
            internal bool Succeeded { get { return Status == ConfigurationStatus.Verified && EngineOffVerified && VirusUiHiddenVerified && PersistenceVerified; } }
        }

        internal static Task<string> RemoveAsync(IProgress<string> progress) { return ConfigureAsync(progress); }

        internal static async Task<string> ConfigureAsync(IProgress<string> progress)
        {
            var preflight = SupportedConfiguration.Check(SupportedProtectionContext.Inspect());
            if (!preflight.CanApply) throw new ConfigurationException(preflight);

            string pipeName = "JustEnoughTools.Protection." + Guid.NewGuid().ToString("N");
            string nonce = CreateNonce();
            using (var server = CreateServer(pipeName))
            using (var parent = Process.GetCurrentProcess())
            // Keep this medium-created, non-owned object alive through worker
            // handoff. The worker acquires ownership itself on its entry thread.
            using (var leaseHandle = UpdateService.CreateOperationMutex(parent.MainModule.FileName))
            {
                Process worker;
                try
                {
                    var start = new ProcessStartInfo(parent.MainModule.FileName,
                        WorkerArgument + " " + pipeName + " " + nonce + " " +
                        parent.Id.ToString(CultureInfo.InvariantCulture) + " " +
                        parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture))
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        WorkingDirectory = Path.GetDirectoryName(parent.MainModule.FileName)
                    };
                    worker = Process.Start(start);
                    if (worker == null) throw new InvalidOperationException("Windows не вернула рабочий процесс.");
                }
                catch (Win32Exception error) when (error.NativeErrorCode == 1223)
                {
                    throw new InvalidOperationException("Запрос прав администратора отменён. Настройка не выполнялась.", error);
                }

                // Never terminate an elevated worker: once accepted, it may be
                // inside an operation that must reach a safe boundary itself.
                using (worker)
                {
                    if (progress != null) progress.Report("Ожидание рабочего процесса…");
                    await WaitForConnectionAsync(server, worker).ConfigureAwait(false);
                    uint clientPid;
                    if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out clientPid) ||
                        clientPid != unchecked((uint)worker.Id) || worker.HasExited)
                        throw new InvalidOperationException("Не удалось подтвердить рабочий процесс. Настройка не начата.");

                    var hello = await ReadWithTimeoutAsync(server, HandshakeTimeoutMilliseconds).ConfigureAwait(false);
                    if (hello.Kind != MessageKind.Hello || !SameNonce(hello.Payload, nonce))
                        throw new InvalidDataException("Рабочий процесс не подтвердил запрос. Настройка не начата.");
                    await WriteWithTimeoutAsync(server, MessageKind.Accepted, Encoding.ASCII.GetBytes("JET-CONFIGURATION-2")).ConfigureAwait(false);

                    while (true)
                    {
                        var frame = await ReadWithTimeoutAsync(server, ProgressTimeoutMilliseconds).ConfigureAwait(false);
                        if (frame.Kind == MessageKind.Progress)
                        {
                            if (progress != null) progress.Report(DecodeText(frame.Payload));
                            continue;
                        }
                        if (frame.Kind != MessageKind.Result) throw new InvalidDataException("Получен неожиданный ответ рабочего процесса.");
                        var result = DecodeOutcome(frame.Payload);
                        if (!result.Succeeded) throw new ConfigurationException(ToBackendResult(result));
                        return string.IsNullOrWhiteSpace(result.Message) ? "Настройка подтверждена." : result.Message;
                    }
                }
            }
        }

        internal static bool TryRunWorker(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args == null || args.Length == 0 || args[0] != WorkerArgument) return false;
            string pipeName;
            string nonce;
            int parentPid;
            long parentStart;
            if (!TryParseWorkerArguments(args, out pipeName, out nonce, out parentPid, out parentStart))
            {
                exitCode = 2;
                return true;
            }
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    {
                        exitCode = 5;
                        return true;
                    }
                }
                using (var parent = Process.GetProcessById(parentPid))
                using (var current = Process.GetCurrentProcess())
                {
                    if (parent.StartTime.ToUniversalTime().Ticks != parentStart ||
                        !string.Equals(Path.GetFullPath(parent.MainModule.FileName), Path.GetFullPath(current.MainModule.FileName), StringComparison.OrdinalIgnoreCase))
                    {
                        exitCode = 5;
                        return true;
                    }
                    using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                               PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
                    {
                        client.Connect(ConnectTimeoutMilliseconds);
                        uint serverPid;
                        if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out serverPid) || serverPid != unchecked((uint)parentPid))
                            throw new InvalidDataException("Unexpected controller process.");
                        WriteWithTimeoutAsync(client, MessageKind.Hello, Encoding.ASCII.GetBytes(nonce)).GetAwaiter().GetResult();
                        var accepted = ReadWithTimeoutAsync(client, HandshakeTimeoutMilliseconds).GetAwaiter().GetResult();
                        if (accepted.Kind != MessageKind.Accepted || DecodeText(accepted.Payload) != "JET-CONFIGURATION-2")
                            throw new InvalidDataException("Controller did not accept the operation.");
                        var progress = new WorkerProgress(message =>
                        {
                            WriteWithTimeoutAsync(client, MessageKind.Progress, EncodeText(message)).GetAwaiter().GetResult();
                        });
                        Outcome outcome;
                        try
                        {
                            // Mutex ownership is thread-affine. This worker's entry
                            // thread waits synchronously and releases on the same
                            // thread even if the backend performs asynchronous work.
                            using (var operation = UpdateService.CreateOperationMutex(current.MainModule.FileName))
                            {
                                bool owned;
                                try { owned = operation.WaitOne(ConnectTimeoutMilliseconds); }
                                catch (AbandonedMutexException) { owned = true; }
                                if (!owned) throw new InvalidOperationException("Установка обновления занята. Настройка не начиналась.");
                                try
                                {
                                    // Re-evaluate under the operation lease; the
                                    // controller's earlier context can be stale.
                                    var result = SupportedConfiguration.ConfigureAsync(progress).GetAwaiter().GetResult();
                                    outcome = FromBackend(result);
                                }
                                finally { operation.ReleaseMutex(); }
                            }
                        }
                        catch (Exception error)
                        {
                            outcome = new Outcome { Status = ConfigurationStatus.Unverified, Message = "Не удалось завершить настройку. " + error.Message };
                        }
                        exitCode = outcome.Succeeded ? 0 : 1;
                        progress.TrySendFinal(() => WriteWithTimeoutAsync(client, MessageKind.Result, EncodeOutcome(outcome)).GetAwaiter().GetResult());
                    }
                }
            }
            catch
            {
                // The private worker has no GUI or disk logger. The controller
                // reports a disconnected/failed operation and never claims success.
                exitCode = 1;
            }
            return true;
        }

        internal static Outcome FromBackend(ConfigurationResult result)
        {
            if (result == null) return new Outcome { Status = ConfigurationStatus.Unverified, Message = "Рабочий процесс не вернул результат настройки." };
            return new Outcome
            {
                Status = result.Status == ConfigurationStatus.Verified && !result.Succeeded ? ConfigurationStatus.Unverified : result.Status,
                PerformedChanges = result.PerformedChanges,
                ChangesMayHaveOccurred = result.ChangesMayHaveOccurred,
                EngineOffVerified = result.EngineOffVerified,
                VirusUiHiddenVerified = result.VirusUiHiddenVerified,
                PersistenceVerified = result.PersistenceVerified,
                Message = result.Message
            };
        }

        private static ConfigurationResult ToBackendResult(Outcome outcome)
        {
            return new ConfigurationResult
            {
                Status = outcome.Status, PerformedChanges = outcome.PerformedChanges,
                ChangesMayHaveOccurred = outcome.ChangesMayHaveOccurred, EngineOffVerified = outcome.EngineOffVerified,
                VirusUiHiddenVerified = outcome.VirusUiHiddenVerified, PersistenceVerified = outcome.PersistenceVerified,
                Message = string.IsNullOrWhiteSpace(outcome.Message) ? "Настройка не подтверждена." : outcome.Message
            };
        }

        internal static bool TryParseWorkerArguments(string[] args, out string pipeName, out string nonce, out int parentPid, out long parentStart)
        {
            pipeName = null; nonce = null; parentPid = 0; parentStart = 0;
            if (args == null || args.Length != 5 || args[0] != WorkerArgument ||
                !Regex.IsMatch(args[1] ?? "", @"\AJustEnoughTools\.Protection\.[a-f0-9]{32}\z", RegexOptions.CultureInvariant) ||
                !Regex.IsMatch(args[2] ?? "", @"\A[a-f0-9]{64}\z", RegexOptions.CultureInvariant) ||
                !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out parentPid) || parentPid <= 0 ||
                !long.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out parentStart) ||
                parentStart <= 0 || parentStart > DateTime.MaxValue.Ticks) return false;
            pipeName = args[1]; nonce = args[2]; return true;
        }

        private static NamedPipeServerStream CreateServer(string pipeName)
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            using (var identity = WindowsIdentity.GetCurrent())
                security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 4096, 4096, security);
        }

        private static async Task WaitForConnectionAsync(NamedPipeServerStream server, Process worker)
        {
            using (var timeout = new CancellationTokenSource(ConnectTimeoutMilliseconds))
            using (timeout.Token.Register(() => CloseStreamQuietly(server)))
            {
                Task connection = server.WaitForConnectionAsync();
                while (!connection.IsCompleted)
                {
                    if (worker.HasExited) throw new InvalidOperationException("Рабочий процесс завершился до подключения. Настройка не начата.");
                    await Task.WhenAny(connection, Task.Delay(100)).ConfigureAwait(false);
                }
                try { await connection.ConfigureAwait(false); }
                catch (Exception) when (timeout.IsCancellationRequested) { throw new TimeoutException("Рабочий процесс не подключился вовремя. Настройка не начата."); }
            }
        }

        private static async Task<Frame> ReadWithTimeoutAsync(Stream stream, int milliseconds)
        {
            using (var timeout = new CancellationTokenSource(milliseconds))
            {
                try { return await ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false); }
                catch (Exception) when (timeout.IsCancellationRequested) { throw new TimeoutException("Рабочий процесс не ответил вовремя; завершение настройки не подтверждено."); }
            }
        }

        private static async Task WriteWithTimeoutAsync(Stream stream, MessageKind kind, byte[] payload)
        {
            using (var timeout = new CancellationTokenSource(HandshakeTimeoutMilliseconds))
                await WriteFrameAsync(stream, kind, payload, timeout.Token).ConfigureAwait(false);
        }

        internal static async Task<Frame> ReadFrameAsync(Stream stream, CancellationToken token)
        {
            using (token.Register(() => CloseStreamQuietly(stream)))
            {
                try
                {
                    var header = new byte[5];
                    await ReadExactlyAsync(stream, header, token).ConfigureAwait(false);
                    var kind = (MessageKind)header[0];
                    if (kind < MessageKind.Hello || kind > MessageKind.Result) throw new InvalidDataException("Unknown IPC message.");
                    int size = BitConverter.ToInt32(header, 1);
                    if (size < 0 || size > MaximumFrameBytes) throw new InvalidDataException("IPC message exceeds its limit.");
                    var payload = new byte[size];
                    await ReadExactlyAsync(stream, payload, token).ConfigureAwait(false);
                    return new Frame { Kind = kind, Payload = payload };
                }
                catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
            }
        }

        internal static async Task WriteFrameAsync(Stream stream, MessageKind kind, byte[] payload, CancellationToken token)
        {
            if (kind < MessageKind.Hello || kind > MessageKind.Result || payload == null || payload.Length > MaximumFrameBytes)
                throw new InvalidDataException("Invalid IPC message.");
            var header = new byte[5]; header[0] = (byte)kind;
            Buffer.BlockCopy(BitConverter.GetBytes(payload.Length), 0, header, 1, 4);
            using (token.Register(() => CloseStreamQuietly(stream)))
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    await stream.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
                    await stream.WriteAsync(payload, 0, payload.Length, token).ConfigureAwait(false);
                    await stream.FlushAsync(token).ConfigureAwait(false);
                }
                catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
            }
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] bytes, CancellationToken token)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = await stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Рабочий процесс завершился; настройка не подтверждена.");
                offset += read;
            }
        }

        private static void CloseStreamQuietly(Stream stream)
        {
            try { stream.Dispose(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        internal static byte[] EncodeOutcome(Outcome outcome)
        {
            if (outcome == null) throw new ArgumentNullException(nameof(outcome));
            byte[] text = EncodeText(outcome.Message, MaximumFrameBytes - 2);
            var bytes = new byte[text.Length + 2];
            bytes[0] = (byte)outcome.Status;
            bytes[1] = (byte)((outcome.PerformedChanges ? 1 : 0) | (outcome.ChangesMayHaveOccurred ? 2 : 0) |
                (outcome.EngineOffVerified ? 4 : 0) | (outcome.VirusUiHiddenVerified ? 8 : 0) | (outcome.PersistenceVerified ? 16 : 0));
            Buffer.BlockCopy(text, 0, bytes, 2, text.Length);
            DecodeOutcome(bytes); // Reject internally inconsistent evidence too.
            return bytes;
        }

        internal static Outcome DecodeOutcome(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 2 || bytes.Length > MaximumFrameBytes || bytes[0] > (byte)ConfigurationStatus.Verified || bytes[1] > 31)
                throw new InvalidDataException("Invalid IPC result.");
            var result = new Outcome
            {
                Status = (ConfigurationStatus)bytes[0], PerformedChanges = (bytes[1] & 1) != 0,
                ChangesMayHaveOccurred = (bytes[1] & 2) != 0, EngineOffVerified = (bytes[1] & 4) != 0,
                VirusUiHiddenVerified = (bytes[1] & 8) != 0, PersistenceVerified = (bytes[1] & 16) != 0,
                Message = StrictUtf8.GetString(bytes, 2, bytes.Length - 2)
            };
            if ((result.Status == ConfigurationStatus.Verified && !result.Succeeded) ||
                (result.PersistenceVerified && (!result.EngineOffVerified || !result.VirusUiHiddenVerified)) ||
                (result.VirusUiHiddenVerified && !result.EngineOffVerified))
                throw new InvalidDataException("An unverified configuration cannot report success.");
            return result;
        }

        internal static byte[] EncodeText(string text, int maximumBytes = MaximumFrameBytes)
        {
            if (maximumBytes < 3 || maximumBytes > MaximumFrameBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            text = text ?? string.Empty;
            if (Encoding.UTF8.GetByteCount(text) <= maximumBytes) return Encoding.UTF8.GetBytes(text);
            int low = 0, high = Math.Min(text.Length, maximumBytes);
            while (low < high)
            {
                int middle = low + (high - low + 1) / 2;
                if (Encoding.UTF8.GetByteCount(text.Substring(0, middle)) <= maximumBytes - 3) low = middle;
                else high = middle - 1;
            }
            if (low > 0 && char.IsHighSurrogate(text[low - 1])) low--;
            return Encoding.UTF8.GetBytes(text.Substring(0, low) + "…");
        }

        internal static string DecodeText(byte[] bytes)
        {
            if (bytes == null || bytes.Length > MaximumFrameBytes) throw new InvalidDataException("Invalid IPC text.");
            return StrictUtf8.GetString(bytes);
        }

        private static bool SameNonce(byte[] bytes, string nonce)
        {
            byte[] expected = Encoding.ASCII.GetBytes(nonce);
            if (bytes == null || bytes.Length != expected.Length) return false;
            int difference = 0;
            for (int index = 0; index < expected.Length; index++) difference |= bytes[index] ^ expected[index];
            return difference == 0;
        }

        private static string CreateNonce()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        internal sealed class WorkerProgress : IProgress<string>
        {
            private readonly Action<string> report;
            private readonly object gate = new object();
            private bool disconnected;
            internal WorkerProgress(Action<string> report) { this.report = report; }
            public void Report(string value)
            {
                lock (gate)
                {
                    if (disconnected) return;
                    try { report(value); }
                    catch (Exception error) when (IsTransportFailure(error)) { disconnected = true; }
                }
            }

            internal void TrySendFinal(Action send)
            {
                lock (gate)
                {
                    if (disconnected) return;
                    try { send(); }
                    catch (Exception error) when (IsTransportFailure(error)) { }
                    finally { disconnected = true; }
                }
            }

            private static bool IsTransportFailure(Exception error)
            {
                return error is IOException || error is ObjectDisposedException || error is OperationCanceledException ||
                    error is TimeoutException || error is InvalidOperationException || error is Win32Exception;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientPid);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverPid);
    }
}

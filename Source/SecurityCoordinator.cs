using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JustEnoughTools.Security;
using JustEnoughTools.Updates;
using Microsoft.Win32.SafeHandles;

namespace JustEnoughTools
{
    // Only the fixed action catalog can cross this boundary. No commands,
    // registry paths or caller-selected profiles are accepted by the worker.
    internal static class SecurityCoordinator
    {
        private const string WorkerArgument = "--security-worker";
        private const string Protocol = "JET-SECURITY-1";
        private const int ConnectTimeoutMilliseconds = 45000;
        private const int HandshakeTimeoutMilliseconds = 10000;
        private const int ResultTimeoutMilliseconds = 10 * 60 * 1000;
        internal const int MaximumFrameBytes = 131072;

        internal enum MessageKind : byte { Hello = 1, Accepted = 2, Result = 3 }

        internal sealed class Frame
        {
            internal MessageKind Kind;
            internal byte[] Payload;
        }

        internal static async Task<SecurityReport> ConfigureAsync()
        {
            // Read-only discovery happens before creating a worker or asking
            // for another token. A repeated click must not prompt for UAC when
            // all readable settings are already in the requested state.
            SecuritySnapshot snapshot = await Task.Run(() => SecurityEngine.Inspect()).ConfigureAwait(false);
            if (snapshot == null) throw new InvalidDataException("Не получено состояние настроек. Изменения не начинались.");
            if (!snapshot.HasChanges) return snapshot.ToReport();
            string pipeName = "JustEnoughTools.Security." + Guid.NewGuid().ToString("N");
            string nonce = CreateNonce();
            using (var server = CreateServer(pipeName))
            using (var parent = Process.GetCurrentProcess())
            // Keep the medium-created kernel object alive during elevation.
            // Ownership is acquired and released by the worker's entry thread.
            using (var leaseHandle = UpdateService.CreateOperationMutex(parent.MainModule.FileName))
            {
                Process worker;
                try
                {
                    worker = Process.Start(CreateWorkerStartInfo(parent.MainModule.FileName,
                        WorkerArgument + " " + pipeName + " " + nonce + " " +
                        parent.Id.ToString(CultureInfo.InvariantCulture) + " " +
                        parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                        IsElevated(), snapshot.RequiresElevation));
                    if (worker == null) throw new InvalidOperationException("Windows не вернула рабочий процесс.");
                }
                catch (Win32Exception error) when (error.NativeErrorCode == 1223)
                {
                    throw new InvalidOperationException("Запрос прав администратора отменён. Изменения не выполнялись.", error);
                }

                // Do not kill a worker after acceptance: an in-flight system
                // operation must be allowed to finish even if its UI disconnects.
                using (worker)
                {
                    await WaitForConnectionAsync(server, worker).ConfigureAwait(false);
                    uint clientPid;
                    if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out clientPid) ||
                        clientPid != unchecked((uint)worker.Id) || worker.HasExited)
                        throw new InvalidOperationException("Не удалось подтвердить рабочий процесс. Изменения не начинались.");
                    Frame hello = await ReadWithTimeoutAsync(server, HandshakeTimeoutMilliseconds).ConfigureAwait(false);
                    if (hello.Kind != MessageKind.Hello || !SameNonce(hello.Payload, nonce))
                        throw new InvalidDataException("Рабочий процесс не подтвердил запрос. Изменения не начинались.");
                    await WriteWithTimeoutAsync(server, MessageKind.Accepted, Encoding.ASCII.GetBytes(Protocol)).ConfigureAwait(false);
                    Frame final = await ReadWithTimeoutAsync(server, ResultTimeoutMilliseconds).ConfigureAwait(false);
                    if (final.Kind != MessageKind.Result) throw new InvalidDataException("Получен неожиданный ответ рабочего процесса.");
                    return DecodeReport(final.Payload);
                }
            }
        }

        internal static ProcessStartInfo CreateWorkerStartInfo(string executable, string arguments,
            bool alreadyElevated, bool requiresElevation)
        {
            bool requestElevation = requiresElevation && !alreadyElevated;
            return new ProcessStartInfo(executable, arguments)
            {
                // Request a different token only for a known pending action
                // that needs it; otherwise inherit the constructor's token.
                UseShellExecute = requestElevation,
                Verb = requestElevation ? "runas" : string.Empty,
                CreateNoWindow = !requestElevation,
                WorkingDirectory = Path.GetDirectoryName(executable)
            };
        }

        private static bool IsElevated()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        internal static bool TryRunWorker(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args == null || args.Length == 0 || args[0] != WorkerArgument) return false;
            string pipeName, nonce;
            int parentPid;
            long parentStart;
            if (!TryParseWorkerArguments(args, out pipeName, out nonce, out parentPid, out parentStart))
            {
                exitCode = 2;
                return true;
            }
            try
            {
                using (var parent = Process.GetProcessById(parentPid))
                using (var current = Process.GetCurrentProcess())
                {
                    if (parent.StartTime.ToUniversalTime().Ticks != parentStart ||
                        !string.Equals(Path.GetFullPath(parent.MainModule.FileName), Path.GetFullPath(current.MainModule.FileName),
                            StringComparison.OrdinalIgnoreCase))
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
                        Frame accepted = ReadWithTimeoutAsync(client, HandshakeTimeoutMilliseconds).GetAwaiter().GetResult();
                        if (accepted.Kind != MessageKind.Accepted || !SameAscii(accepted.Payload, Protocol))
                            throw new InvalidDataException("Controller did not accept the fixed operation.");

                        SecurityReport report;
                        bool executionStarted = false;
                        try
                        {
                            using (var operation = UpdateService.CreateOperationMutex(current.MainModule.FileName))
                            {
                                bool owned;
                                try { owned = operation.WaitOne(ConnectTimeoutMilliseconds); }
                                catch (AbandonedMutexException) { owned = true; }
                                if (!owned) throw new InvalidOperationException("Установка обновления занята. Изменения не начинались.");
                                try
                                {
                                    executionStarted = true;
                                    report = SecurityEngine.Run();
                                }
                                finally { operation.ReleaseMutex(); }
                            }
                        }
                        catch (Exception error)
                        {
                            report = FailureReport((executionStarted
                                ? "Выполнение прервано; часть настроек могла измениться. "
                                : "Изменения не начинались. ") + error.Message);
                        }
                        if (report == null) report = FailureReport("Рабочий модуль не вернул результат. Завершение изменений не подтверждено.");
                        exitCode = report.Succeeded ? 0 : 2;
                        // Losing the controller after completion must not change
                        // the completed operation or create a separate error window.
                        try { WriteWithTimeoutAsync(client, MessageKind.Result, EncodeReport(report)).GetAwaiter().GetResult(); }
                        catch (Exception error) when (IsTransportFailure(error)) { }
                    }
                }
            }
            catch { exitCode = 1; }
            return true;
        }

        internal static SecurityReport FailureReport(string reason)
        {
            var report = new SecurityReport();
            report.Results.Add(new ActionResult
            {
                Id = "operation",
                Title = "Применение настройки",
                Outcome = ActionOutcome.Failed,
                Reason = reason,
                Before = string.Empty,
                After = string.Empty
            });
            return report;
        }

        internal static bool TryParseWorkerArguments(string[] args, out string pipeName, out string nonce, out int parentPid, out long parentStart)
        {
            pipeName = null; nonce = null; parentPid = 0; parentStart = 0;
            if (args == null || args.Length != 5 || args[0] != WorkerArgument ||
                !Regex.IsMatch(args[1] ?? "", @"\AJustEnoughTools\.Security\.[a-f0-9]{32}\z", RegexOptions.CultureInvariant) ||
                !Regex.IsMatch(args[2] ?? "", @"\A[a-f0-9]{64}\z", RegexOptions.CultureInvariant) ||
                !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out parentPid) || parentPid <= 0 ||
                !long.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out parentStart) ||
                parentStart <= 0 || parentStart > DateTime.MaxValue.Ticks) return false;
            pipeName = args[1]; nonce = args[2];
            return true;
        }

        private static NamedPipeServerStream CreateServer(string pipeName)
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            using (var identity = WindowsIdentity.GetCurrent())
                security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            // Also supports credential elevation from a standard user account.
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
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
                    if (worker.HasExited) throw new InvalidOperationException("Рабочий процесс завершился до подключения. Изменения не начинались.");
                    await Task.WhenAny(connection, Task.Delay(100)).ConfigureAwait(false);
                }
                try { await connection.ConfigureAwait(false); }
                catch (Exception) when (timeout.IsCancellationRequested)
                { throw new TimeoutException("Рабочий процесс не подключился вовремя. Изменения не начинались."); }
            }
        }

        private static async Task<Frame> ReadWithTimeoutAsync(Stream stream, int milliseconds)
        {
            using (var timeout = new CancellationTokenSource(milliseconds))
            {
                try { return await ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false); }
                catch (Exception) when (timeout.IsCancellationRequested)
                { throw new TimeoutException("Рабочий процесс не ответил вовремя. Завершение изменений не подтверждено."); }
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
                    if (!IsMessageKind(kind)) throw new InvalidDataException("Unknown IPC message.");
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
            if (!IsMessageKind(kind) || payload == null || payload.Length > MaximumFrameBytes)
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

        internal static byte[] EncodeReport(SecurityReport report)
        {
            ValidateReport(report);
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(SecurityReport)).WriteObject(stream, report);
                if (stream.Length > MaximumFrameBytes) throw new InvalidDataException("Security report exceeds its limit.");
                return stream.ToArray();
            }
        }

        internal static SecurityReport DecodeReport(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumFrameBytes)
                throw new InvalidDataException("Invalid security report.");
            try
            {
                using (var stream = new MemoryStream(bytes, false))
                {
                    var report = new DataContractJsonSerializer(typeof(SecurityReport)).ReadObject(stream) as SecurityReport;
                    ValidateReport(report);
                    return report;
                }
            }
            catch (SerializationException error) { throw new InvalidDataException("Invalid security report.", error); }
        }

        private static void ValidateReport(SecurityReport report)
        {
            if (report == null || report.Results == null || report.Results.Count < 1 || report.Results.Count > 64)
                throw new InvalidDataException("Security report has no valid action list.");
            var ids = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (ActionResult result in report.Results)
            {
                if (result == null || string.IsNullOrWhiteSpace(result.Id) || result.Id.Length > 128 ||
                    !ids.Add(result.Id) || string.IsNullOrWhiteSpace(result.Title) || result.Title.Length > 256 ||
                    !Enum.IsDefined(typeof(ActionOutcome), result.Outcome) || !Fits(result.Reason, 8192) ||
                    !Fits(result.Before, 2048) || !Fits(result.After, 2048))
                    throw new InvalidDataException("Security report contains an invalid action.");
            }
        }

        private static bool Fits(string value, int maximumLength) { return value == null || value.Length <= maximumLength; }
        private static bool IsMessageKind(MessageKind kind) { return kind >= MessageKind.Hello && kind <= MessageKind.Result; }

        private static async Task ReadExactlyAsync(Stream stream, byte[] bytes, CancellationToken token)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = await stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Связь с рабочим процессом прервана. Завершение изменений не подтверждено.");
                offset += read;
            }
        }

        private static void CloseStreamQuietly(Stream stream)
        {
            try { stream.Dispose(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        private static bool SameAscii(byte[] bytes, string expectedText)
        {
            byte[] expected = Encoding.ASCII.GetBytes(expectedText);
            if (bytes == null || bytes.Length != expected.Length) return false;
            int difference = 0;
            for (int index = 0; index < expected.Length; index++) difference |= bytes[index] ^ expected[index];
            return difference == 0;
        }

        private static bool SameNonce(byte[] bytes, string nonce) { return SameAscii(bytes, nonce); }

        private static string CreateNonce()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        private static bool IsTransportFailure(Exception error)
        {
            return error is IOException || error is ObjectDisposedException || error is OperationCanceledException ||
                error is TimeoutException || error is InvalidOperationException || error is Win32Exception;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientPid);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverPid);
    }
}

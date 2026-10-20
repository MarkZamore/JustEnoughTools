using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using JustEnoughTools.Protection;

namespace JustEnoughTools.Tests
{
    // No elevation, worker launch or configuration call occurs in these tests.
    public static class CoordinatorTests
    {
        public static int RunAll()
        {
            int checks = 0;
            string pipe, nonce;
            int pid;
            long started;
            var valid = new[] { "--configuration-worker", "JustEnoughTools.Protection." + new string('a', 32), new string('b', 64), "1234", "638000000000000000" };
            Require(ProtectionCoordinator.TryParseWorkerArguments(valid, out pipe, out nonce, out pid, out started), "Valid worker fields rejected."); checks++;
            Require(pid == 1234 && nonce.Length == 64, "Parsed identity changed."); checks++;
            foreach (var changed in new[]
            {
                new[] { "--configuration-worker" },
                new[] { valid[0], "\\\\remote\\pipe\\test", valid[2], valid[3], valid[4] },
                new[] { valid[0], valid[1], valid[2] + " other-command", valid[3], valid[4] },
                new[] { valid[0], valid[1], valid[2], "-1", valid[4] },
                new[] { valid[0], valid[1], valid[2], valid[3], "9223372036854775807" },
                new[] { valid[0], valid[1], valid[2], valid[3], valid[4], "extra" }
            })
            {
                Require(!ProtectionCoordinator.TryParseWorkerArguments(changed, out pipe, out nonce, out pid, out started), "Unsafe worker fields accepted."); checks++;
            }
            int exitCode;
            Require(!ProtectionCoordinator.TryRunWorker(new[] { "--other" }, out exitCode), "Unrelated invocation was intercepted."); checks++;
            Require(ProtectionCoordinator.TryRunWorker(new[] { "--configuration-worker" }, out exitCode) && exitCode == 2, "Malformed private invocation did not fail safely."); checks++;

            foreach (var kind in new[] { ProtectionCoordinator.MessageKind.Hello, ProtectionCoordinator.MessageKind.Accepted, ProtectionCoordinator.MessageKind.Progress, ProtectionCoordinator.MessageKind.Result })
            {
                using (var stream = new MemoryStream())
                {
                    byte[] payload = Encoding.UTF8.GetBytes("Проверка 🛠");
                    ProtectionCoordinator.WriteFrameAsync(stream, kind, payload, CancellationToken.None).GetAwaiter().GetResult();
                    stream.Position = 0;
                    var frame = ProtectionCoordinator.ReadFrameAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
                    Require(frame.Kind == kind && ProtectionCoordinator.DecodeText(frame.Payload) == "Проверка 🛠", "Protocol frame changed."); checks++;
                }
            }
            foreach (var header in new[]
            {
                new byte[] { 99, 0, 0, 0, 0 },
                new byte[] { 3, 255, 255, 255, 255 },
                new byte[] { 3, 1, 32, 0, 0 },
                new byte[] { 3, 5, 0, 0, 0, 1, 2 }
            })
            {
                ExpectFailure(() => { using (var input = new MemoryStream(header)) ProtectionCoordinator.ReadFrameAsync(input, CancellationToken.None).GetAwaiter().GetResult(); }); checks++;
            }
            using (var output = new MemoryStream())
                ExpectFailure(() => ProtectionCoordinator.WriteFrameAsync(output, ProtectionCoordinator.MessageKind.Progress, new byte[ProtectionCoordinator.MaximumFrameBytes + 1], CancellationToken.None).GetAwaiter().GetResult());
            checks++;

            var failure = new ProtectionCoordinator.Outcome { Status = ConfigurationStatus.Pending, PerformedChanges = true, EngineOffVerified = true, VirusUiHiddenVerified = true, Message = "Настройка не завершена." };
            var decoded = ProtectionCoordinator.DecodeOutcome(ProtectionCoordinator.EncodeOutcome(failure));
            Require(!decoded.Succeeded && decoded.PerformedChanges && !decoded.PersistenceVerified && decoded.Status == ConfigurationStatus.Pending && decoded.Message == failure.Message, "Partial failure became success."); checks++;
            var success = new ProtectionCoordinator.Outcome { Status = ConfigurationStatus.Verified, PerformedChanges = true, EngineOffVerified = true, VirusUiHiddenVerified = true, PersistenceVerified = true, Message = "Проверено." };
            Require(ProtectionCoordinator.DecodeOutcome(ProtectionCoordinator.EncodeOutcome(success)).Succeeded, "Verified result rejected."); checks++;
            foreach (byte flags in new byte[] { 0, 4, 8, 12, 16, 20, 24 })
            {
                ExpectFailure(() => ProtectionCoordinator.DecodeOutcome(new byte[] { (byte)ConfigurationStatus.Verified, flags })); checks++;
            }
            ExpectFailure(() => ProtectionCoordinator.DecodeOutcome(new byte[] { 255, 0 })); checks++;
            ExpectFailure(() => ProtectionCoordinator.DecodeOutcome(new byte[] { 2, 32 })); checks++;
            ExpectFailure(() => ProtectionCoordinator.DecodeOutcome(new byte[] { 2, 8 })); checks++;
            ExpectFailure(() => ProtectionCoordinator.DecodeText(new byte[] { 255, 255 })); checks++;
            byte[] clipped = ProtectionCoordinator.EncodeText(new string('Ж', 10000) + "😀");
            Require(clipped.Length <= ProtectionCoordinator.MaximumFrameBytes && ProtectionCoordinator.DecodeText(clipped).EndsWith("…", StringComparison.Ordinal), "Progress clipping is invalid."); checks++;
            byte[] clippedEmoji = ProtectionCoordinator.EncodeText(string.Concat(System.Linq.Enumerable.Repeat("😀", 10000)));
            Require(clippedEmoji.Length <= ProtectionCoordinator.MaximumFrameBytes && ProtectionCoordinator.DecodeText(clippedEmoji).EndsWith("…", StringComparison.Ordinal), "Surrogate clipping produced invalid UTF8."); checks++;
            Require(!ProtectionCoordinator.FromBackend(null).Succeeded, "Missing backend result became success."); checks++;
            var unsupported = new ConfigurationResult { Status = ConfigurationStatus.Unsupported, Message = "Профиль не проверен." };
            Require(!ProtectionCoordinator.FromBackend(unsupported).Succeeded && !ProtectionCoordinator.FromBackend(unsupported).PersistenceVerified, "Unverified backend result became success."); checks++;
            var incomplete = new ConfigurationResult { Status = ConfigurationStatus.Verified, EngineOffVerified = true };
            Require(!ProtectionCoordinator.FromBackend(incomplete).Succeeded && ProtectionCoordinator.FromBackend(incomplete).Status == ConfigurationStatus.Unverified, "Incomplete backend evidence became success."); checks++;
            var uncertain = new ConfigurationResult { Status = ConfigurationStatus.Unverified, ChangesMayHaveOccurred = true };
            Require(ProtectionCoordinator.DecodeOutcome(ProtectionCoordinator.EncodeOutcome(ProtectionCoordinator.FromBackend(uncertain))).ChangesMayHaveOccurred,
                "Possible partial change was lost in IPC."); checks++;
            int sends = 0;
            var detached = new ProtectionCoordinator.WorkerProgress(message => { sends++; throw new IOException("Controller exited."); });
            detached.Report("До отключения");
            detached.Report("После отключения");
            detached.TrySendFinal(() => { sends++; throw new InvalidOperationException("Should not send after disconnection."); });
            Require(sends == 1, "Broken progress transport interrupted the backend or kept writing."); checks++;
            var completed = new ProtectionCoordinator.WorkerProgress(message => { sends++; });
            completed.TrySendFinal(() => { sends++; throw new ObjectDisposedException("Controller exited after completion."); });
            completed.Report("Позднее сообщение");
            Require(sends == 2, "Final transport failure was not isolated from completion."); checks++;
            var canceled = new ProtectionCoordinator.WorkerProgress(message => { throw new OperationCanceledException(); });
            canceled.Report("Отмена транспортного ожидания");
            checks++;
            // A real pending Framework pipe read must stop when the controller
            // times out. These are kernel objects; no helper, disk file or backend.
            string cancellationPipe = "JET.Coordinator.Test." + Guid.NewGuid().ToString("N");
            using (var server = new NamedPipeServerStream(cancellationPipe, PipeDirection.InOut,
                       1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            using (var client = new NamedPipeClientStream(".", cancellationPipe, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                var connection = server.WaitForConnectionAsync();
                client.Connect(3000);
                connection.GetAwaiter().GetResult();
                using (var timeout = new CancellationTokenSource(100))
                {
                    bool interrupted = false;
                    var read = ProtectionCoordinator.ReadFrameAsync(server, timeout.Token);
                    Require(System.Threading.Tasks.Task.WhenAny(read, System.Threading.Tasks.Task.Delay(3000)).GetAwaiter().GetResult() == read,
                        "Timed-out pipe read did not stop.");
                    try { read.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { interrupted = true; }
                    Require(interrupted, "Timed-out pipe read did not report cancellation."); checks++;
                }
            }
            return checks;
        }

        private static void ExpectFailure(Action action)
        {
            bool failed = false;
            try { action(); }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is ArgumentException) { failed = true; }
            Require(failed, "Malformed protocol message was accepted.");
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}

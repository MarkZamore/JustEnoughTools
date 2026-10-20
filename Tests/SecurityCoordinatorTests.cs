using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JustEnoughTools.Security;

namespace JustEnoughTools.Tests
{
    // Exercises only the new authenticated transport's data boundary. No valid
    // worker is launched, no UAC prompt is shown and no system setter is called.
    public static class SecurityCoordinatorTests
    {
        public static int RunAll()
        {
            int checks = 0;
            string pipe, nonce;
            int pid;
            long started;
            var valid = new[] { "--security-worker", "JustEnoughTools.Security." + new string('a', 32),
                new string('b', 64), "1234", "638000000000000000" };
            Require(SecurityCoordinator.TryParseWorkerArguments(valid, out pipe, out nonce, out pid, out started) &&
                pid == 1234 && started == 638000000000000000, "Worker identity fields changed.");
            checks++;
            foreach (var invalid in new[]
            {
                new[] { "--security-worker" },
                new[] { valid[0], @"\\remote\pipe\other", valid[2], valid[3], valid[4] },
                new[] { valid[0], valid[1], valid[2] + " command", valid[3], valid[4] },
                new[] { valid[0], valid[1], valid[2], "-1", valid[4] },
                new[] { valid[0], valid[1], valid[2], valid[3], "9223372036854775807" },
                new[] { valid[0], valid[1], valid[2], valid[3], valid[4], "extra" },
                new[] { "--other-worker", valid[1], valid[2], valid[3], valid[4] }
            })
            {
                Require(!SecurityCoordinator.TryParseWorkerArguments(invalid, out pipe, out nonce, out pid, out started),
                    "Invalid private invocation accepted.");
                checks++;
            }
            int exit;
            Require(SecurityCoordinator.TryRunWorker(new[] { "--security-worker" }, out exit) && exit == 2,
                "Malformed invocation did not stop before elevation or actions.");
            checks++;
            Require(!SecurityCoordinator.TryRunWorker(new[] { "--other-worker" }, out exit),
                "Unrelated invocation was intercepted.");
            checks++;
            var inherit = SecurityCoordinator.CreateWorkerStartInfo(TestContext.ExecutablePath, "fixed arguments", true, true);
            var elevate = SecurityCoordinator.CreateWorkerStartInfo(TestContext.ExecutablePath, "fixed arguments", false, true);
            Require(!inherit.UseShellExecute && inherit.Verb == string.Empty && inherit.CreateNoWindow &&
                inherit.Arguments == "fixed arguments", "An elevated constructor requested another UAC transition.");
            checks++;
            Require(elevate.UseShellExecute && elevate.Verb == "runas" && elevate.Arguments == "fixed arguments",
                "A medium-integrity constructor did not request UAC for the machine settings.");
            checks++;
            var noElevationNeeded = SecurityCoordinator.CreateWorkerStartInfo(TestContext.ExecutablePath, "fixed arguments", false, false);
            Require(!noElevationNeeded.UseShellExecute && noElevationNeeded.Verb == string.Empty && noElevationNeeded.CreateNoWindow,
                "A pending user-level action requested unnecessary UAC.");
            checks++;

            var partial = new SecurityReport();
            partial.Results.Add(Result("firewall-private", ActionOutcome.Applied, "Подтверждено сейчас."));
            partial.Results.Add(Result("defender-rtp", ActionOutcome.Blocked, "Изменение заблокировано политикой."));
            var decoded = SecurityCoordinator.DecodeReport(SecurityCoordinator.EncodeReport(partial));
            Require(decoded.Results.Count == 2 && !decoded.Succeeded &&
                decoded.Results[0].Outcome == ActionOutcome.Applied &&
                decoded.Results[1].Reason == partial.Results[1].Reason,
                "Partial application or exact blocking reason was lost.");
            checks++;
            var complete = new SecurityReport();
            complete.Results.Add(Result("test", ActionOutcome.AlreadySet, "Уже установлено."));
            Require(SecurityCoordinator.DecodeReport(SecurityCoordinator.EncodeReport(complete)).Succeeded,
                "Fully verified result did not survive IPC.");
            checks++;
            foreach (ActionOutcome outcome in new[] { ActionOutcome.Unavailable, ActionOutcome.Unverified, ActionOutcome.Failed })
            {
                complete.Results[0].Outcome = outcome;
                Require(!SecurityCoordinator.DecodeReport(SecurityCoordinator.EncodeReport(complete)).Succeeded,
                    "Unsupported or unverified action became success.");
                checks++;
            }

            foreach (SecurityCoordinator.MessageKind kind in Enum.GetValues(typeof(SecurityCoordinator.MessageKind)))
            {
                using (var stream = new MemoryStream())
                {
                    byte[] payload = Encoding.UTF8.GetBytes("Результат 🛠");
                    SecurityCoordinator.WriteFrameAsync(stream, kind, payload, CancellationToken.None).GetAwaiter().GetResult();
                    stream.Position = 0;
                    var frame = SecurityCoordinator.ReadFrameAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
                    Require(frame.Kind == kind && payload.SequenceEqual(frame.Payload), "A bounded frame changed.");
                }
                checks++;
            }
            foreach (var bytes in new[]
            {
                new byte[] { 0, 0, 0, 0, 0 },
                new byte[] { 4, 0, 0, 0, 0 },
                new byte[] { 3, 255, 255, 255, 255 },
                new byte[] { 3, 1, 0, 2, 0 },
                new byte[] { 3, 5, 0, 0, 0, 1, 2 }
            })
            {
                ExpectInvalid(() =>
                {
                    using (var input = new MemoryStream(bytes))
                        SecurityCoordinator.ReadFrameAsync(input, CancellationToken.None).GetAwaiter().GetResult();
                });
                checks++;
            }
            using (var output = new MemoryStream())
                ExpectInvalid(() => SecurityCoordinator.WriteFrameAsync(output, SecurityCoordinator.MessageKind.Result,
                    new byte[SecurityCoordinator.MaximumFrameBytes + 1], CancellationToken.None).GetAwaiter().GetResult());
            checks++;

            ExpectInvalid(() => SecurityCoordinator.EncodeReport(new SecurityReport())); checks++;
            complete.Results[0].Outcome = (ActionOutcome)999;
            ExpectInvalid(() => SecurityCoordinator.EncodeReport(complete)); checks++;
            complete.Results[0].Outcome = ActionOutcome.Applied;
            complete.Results.Add(Result("test", ActionOutcome.Applied, "Duplicate identity"));
            ExpectInvalid(() => SecurityCoordinator.EncodeReport(complete)); checks++;
            complete.Results.RemoveAt(1);
            complete.Results[0].Reason = new string('x', 8193);
            ExpectInvalid(() => SecurityCoordinator.EncodeReport(complete)); checks++;
            ExpectInvalid(() => SecurityCoordinator.DecodeReport(Encoding.UTF8.GetBytes("{\"Results\":[]}"))); checks++;
            ExpectInvalid(() => SecurityCoordinator.DecodeReport(Encoding.UTF8.GetBytes("{broken"))); checks++;
            Require(!SecurityCoordinator.FailureReport("Транспорт прерван; результат неизвестен.").Succeeded,
                "Missing worker result became success.");
            checks++;

            // A real asynchronous Framework pipe read must stop on timeout
            // without terminating a worker or writing a cancellation file.
            string name = "JET.Security.Transport.Test." + Guid.NewGuid().ToString("N");
            using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                Task connection = server.WaitForConnectionAsync();
                client.Connect(3000);
                connection.GetAwaiter().GetResult();
                using (var timeout = new CancellationTokenSource(100))
                {
                    Task<SecurityCoordinator.Frame> read = SecurityCoordinator.ReadFrameAsync(server, timeout.Token);
                    Require(Task.WhenAny(read, Task.Delay(3000)).GetAwaiter().GetResult() == read, "Pipe cancellation remained blocked.");
                    bool canceled = false;
                    try { read.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { canceled = true; }
                    Require(canceled, "Cancellation was not reported.");
                }
            }
            checks++;
            return checks;
        }

        private static ActionResult Result(string id, ActionOutcome outcome, string reason)
        {
            return new ActionResult { Id = id, Title = id, Outcome = outcome, Reason = reason, Before = "On", After = "Off" };
        }

        private static void ExpectInvalid(Action action)
        {
            bool failed = false;
            try { action(); }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is ArgumentException) { failed = true; }
            Require(failed, "Malformed IPC data was accepted.");
        }

        private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    }
}

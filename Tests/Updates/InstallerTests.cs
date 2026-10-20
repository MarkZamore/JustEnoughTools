using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using JustEnoughTools.Updates;

namespace JustEnoughTools.Tests
{
    /// <summary>Real helpers operate only on compiled benign executables in a disposable directory.</summary>
    internal static class InstallerTests
    {
        private static int _assertions;

        internal static int RunAll()
        {
            _assertions = 0;
            Scenario("normal exit without restart", NormalExitInstallsWithoutRestart);
            Scenario("explicit restart", ExplicitRestartStartsTheReplacement);
            Scenario("active restart requests and serialization", ActiveRequestsUseTheExistingInstaller);
            Scenario("external operation lease blocks installation", ExternalOperationLeaseBlocksInstallation);
            Scenario("helper lease excludes competing operations", HelperLeaseExcludesCompetingOperations);
            Scenario("transient target lock", TransientTargetLockIsRetried);
            Scenario("missing target recovery", MissingTargetIsRecovered);
            Scenario("cancelled handoff", CancelledHandoffNeverMutates);
            Scenario("sole payload preservation after failure", FailurePreservesTheOnlyValidPayload);
            return _assertions;
        }

        private static void Scenario(string name, Action test)
        {
            try { test(); }
            catch (Exception failure)
            { throw new InvalidOperationException("Installer scenario '" + name + "': " + failure.Message, failure); }
        }

        private static void NormalExitInstallsWithoutRestart()
        {
            using (var fixture = new Fixture())
            {
                var child = fixture.Schedule(false);
                Check(UpdatePackage.HashFile(fixture.Target) == fixture.OldHash, "The helper replaced a file before its caller exited.");
                Check(File.Exists(fixture.Package), "The prepared package disappeared before exit.");
                child.AllowExit();
                fixture.AwaitInstalled();
                Check(!fixture.RestartSignal.WaitOne(500), "Install on exit unexpectedly restarted the application.");
                fixture.CheckCleanAppDirectory();
            }
        }

        private static void ExplicitRestartStartsTheReplacement()
        {
            using (var fixture = new Fixture())
            {
                fixture.Schedule(true).AllowExit();
                Check(fixture.RestartSignal.WaitOne(25000), "The updated executable did not restart.");
                fixture.AwaitInstalled();
                Check(!fixture.RestartSignal.WaitOne(500), "A single restart request launched more than one replacement.");
                Check((File.GetAttributes(fixture.Target) & FileAttributes.Hidden) == 0, "The installed EXE inherited the hidden package flag.");
                fixture.CheckCleanAppDirectory();
            }
        }

        private static void ActiveRequestsUseTheExistingInstaller()
        {
            using (var fixture = new Fixture())
            {
                var first = fixture.Schedule(false);
                var second = fixture.Schedule(true);
                second.AllowExit();
                second.AwaitExit();
                var request = fixture.RequestRestart();
                request.AllowExit();
                request.AwaitExit();
                Check(UpdateInstaller.IsActive(fixture.Target), "The first installer lost coordination while its caller was alive.");
                Check(UpdatePackage.HashFile(fixture.Target) == fixture.OldHash, "A second schedule replaced a file while the original caller was alive.");
                first.AllowExit();
                Check(fixture.RestartSignal.WaitOne(25000), "An active installer did not accept the restart request.");
                fixture.AwaitInstalled();
                Check(!fixture.RestartSignal.WaitOne(750), "Concurrent restart requests launched duplicate replacements.");
                fixture.CheckCleanAppDirectory();
            }
        }

        private static void TransientTargetLockIsRetried()
        {
            using (var fixture = new Fixture())
            {
                var child = fixture.Schedule(false);
                using (var locked = new FileStream(fixture.Target, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    child.AllowExit();
                    child.AwaitExit();
                    WaitUntil(() => File.Exists(fixture.Package) && new FileInfo(fixture.Package).Length == fixture.Payload.Length,
                        12000, "The helper did not prepare its single replacement file.");
                    Thread.Sleep(500);
                    Check(UpdatePackage.HashFile(fixture.Target) == fixture.OldHash, "A locked original was unexpectedly replaced.");
                    Check(UpdateInstaller.IsActive(fixture.Target), "A transient lock prematurely stopped the helper.");
                    Check(UpdateService.IsOperationInProgress(fixture.Target), "The helper released its operation lease before replacement completed.");
                }
                fixture.AwaitInstalled();
                Check(!fixture.RestartSignal.WaitOne(250), "Retrying a lock changed the restart mode.");
                fixture.CheckCleanAppDirectory();
            }
        }

        private static void ExternalOperationLeaseBlocksInstallation()
        {
            using (var fixture = new Fixture())
            {
                var holder = fixture.HoldOperation();
                string packageHash = UpdatePackage.HashFile(fixture.Package);
                Check(UpdateService.IsOperationInProgress(fixture.Target), "The external worker-like lease was not observed.");
                string error;
                Check(!UpdateInstaller.TrySchedule(fixture.Target, fixture.Package, 1, false, out error) && !string.IsNullOrWhiteSpace(error),
                    "The installer accepted a schedule while an operation owned the lease.");
                using (var service = new UpdateService(fixture.Target, 1))
                    Check(!service.TryScheduleInstall(false, out error) && !string.IsNullOrWhiteSpace(error),
                        "The public schedule accepted an active operation.");
                Check(!UpdateService.TryApplyPreparedBeforeStartup(fixture.Target, 1),
                    "Startup installation ignored an active operation lease.");
                Check(UpdatePackage.HashFile(fixture.Target) == fixture.OldHash && UpdatePackage.HashFile(fixture.Package) == packageHash,
                    "A blocked schedule or startup changed the executable or package.");
                ProbeUncommittedHelper(fixture, false); // Bypass the C# precheck to exercise the helper's atomic acquisition.
                holder.AllowExit();
                holder.AwaitExit();
                Check(!UpdateService.IsOperationInProgress(fixture.Target), "The benign external lease was not released.");
            }
        }

        private static void HelperLeaseExcludesCompetingOperations()
        {
            using (var fixture = new Fixture())
            {
                var caller = fixture.Schedule(false);
                using (var operation = UpdateService.CreateOperationMutex(fixture.Target))
                {
                    bool acquired = TryAcquire(operation);
                    if (acquired) operation.ReleaseMutex();
                    Check(!acquired, "A competing worker-like acquisition overlapped the helper's source-wait lease.");
                    Check(UpdateService.IsOperationInProgress(fixture.Target), "A staged helper did not hold the common operation lease.");
                    caller.AllowExit();
                    fixture.AwaitInstalled();
                    acquired = TryAcquire(operation);
                    if (acquired) operation.ReleaseMutex();
                    Check(acquired, "The helper retained the operation lease after installing.");
                }
                fixture.CheckCleanAppDirectory();
            }
        }

        private static bool TryAcquire(Mutex mutex)
        {
            try { return mutex.WaitOne(0); }
            catch (AbandonedMutexException) { return true; }
        }

        private static void MissingTargetIsRecovered()
        {
            using (var fixture = new Fixture())
            {
                var child = fixture.Schedule(false);
                File.Delete(fixture.Target);
                child.AllowExit();
                fixture.AwaitInstalled();
                Check(UpdatePackage.IsViableExecutable(fixture.Target), "The sole valid replacement was not moved back to the missing target.");
                fixture.CheckCleanAppDirectory();
            }
        }

        private static void CancelledHandoffNeverMutates()
        {
            using (var fixture = new Fixture())
                ProbeUncommittedHelper(fixture, true);
        }

        private static void ProbeUncommittedHelper(Fixture fixture, bool shouldStage)
        {
            using (var source = Process.GetCurrentProcess())
            using (var operation = UpdateService.CreateOperationMutex(fixture.Target))
            {
                string key = @"Local\JET.InstallerTest.Cancel." + Guid.NewGuid().ToString("N");
                using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, key + ".Ready"))
                using (var intent = new EventWaitHandle(false, EventResetMode.ManualReset, key + ".Intent"))
                using (var cancel = new EventWaitHandle(false, EventResetMode.ManualReset, key + ".Cancel"))
                {
                    var method = typeof(UpdateInstaller).GetMethod("BuildScript", BindingFlags.NonPublic | BindingFlags.Static);
                    string script = (string)method.Invoke(null, new object[] { fixture.Target, fixture.Package,
                        @"Local\JustEnoughTools.Update." + UpdateService.GetInstanceKey(fixture.Target), key + ".Ready", key + ".Intent", key + ".Cancel",
                        source.Id, source.StartTime.ToUniversalTime().Ticks, (long)fixture.Payload.Length,
                        fixture.NewHash, 2, 1, fixture.OldHash, false });
                    script = Regex.Replace(script, @"(?m)^[ \t]+", string.Empty);
                    string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
                    using (var helper = Process.Start(new ProcessStartInfo(shell,
                        "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                    {
                        try
                        {
                            if (shouldStage) Check(ready.WaitOne(15000), "The cancellation probe never staged the helper.");
                            else
                            {
                                Check(helper.WaitForExit(5000), "A helper blocked by an operation lease did not exit promptly.");
                                Check(!ready.WaitOne(0), "A helper signalled handoff readiness without acquiring the operation lease.");
                            }
                            cancel.Set(); // The caller never commits intent, as on a handoff timeout.
                            Check(helper.WaitForExit(5000), "An uncommitted helper continued waiting after cancellation.");
                            Check(UpdatePackage.HashFile(fixture.Target) == fixture.OldHash, "A cancelled handoff changed the original.");
                            PreparedUpdate prepared;
                            string error;
                            Check(UpdatePackage.TryRead(fixture.Package, 1, out prepared, out error), "A cancelled handoff truncated or removed its package: " + error);
                            Check(!UpdateInstaller.IsActive(fixture.Target), "A cancelled helper retained installer coordination.");
                            Check(Directory.GetFiles(fixture.AppDirectory).Length == 2 && Directory.GetDirectories(fixture.AppDirectory).Length == 0,
                                "A cancelled helper created an unexpected artifact.");
                        }
                        finally
                        {
                            cancel.Set();
                            if (!helper.HasExited) { helper.Kill(); helper.WaitForExit(); }
                        }
                    }
                }
            }
        }

        private static void FailurePreservesTheOnlyValidPayload()
        {
            using (var fixture = new Fixture())
            {
                var child = fixture.Schedule(false);
                File.Delete(fixture.Target);
                Directory.CreateDirectory(fixture.Target); // A disposable obstruction; replacement must fail.
                child.AllowExit();
                child.AwaitExit();
                WaitUntil(() => !UpdateInstaller.IsActive(fixture.Target), 35000, "The failed helper did not release coordination.");
                Check(!File.Exists(fixture.Target), "The fixture unexpectedly has an executable target.");
                Check(File.Exists(fixture.Package), "The helper deleted the only surviving payload after failure.");
                Check(new FileInfo(fixture.Package).Length == fixture.Payload.Length, "The failed replacement did not retain the raw payload.");
                Check(UpdatePackage.HashFile(fixture.Package) == fixture.NewHash, "The surviving replacement bytes changed.");
                Check(UpdatePackage.IsViableExecutable(fixture.Package), "The surviving replacement is not a viable EXE.");
                Check(Directory.GetFiles(fixture.AppDirectory).Select(Path.GetFileName).SequenceEqual(new[] { "JustEnoughTools.update" }),
                    "A failed helper created a script, log, journal or backup.");
            }
        }

        private static void WaitUntil(Func<bool> condition, int milliseconds, string failure)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < milliseconds)
            {
                if (condition()) return;
                Thread.Sleep(50);
            }
            throw new InvalidOperationException(failure);
        }

        private static void Check(bool value, string failure)
        {
            if (!value) throw new InvalidOperationException(failure);
            _assertions++;
        }

        private static string Quote(string value) => "\"" + value + "\"";

        private sealed class Fixture : IDisposable
        {
            private readonly string _root;
            private readonly string _controller;
            private readonly string _signalName;
            private readonly List<Child> _children = new List<Child>();
            internal readonly string AppDirectory;
            internal readonly string Target;
            internal readonly string Package;
            internal readonly byte[] Payload;
            internal readonly string OldHash;
            internal readonly string NewHash;
            internal readonly Semaphore RestartSignal;

            internal Fixture()
            {
                _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "JET-installer-tests-" + Guid.NewGuid().ToString("N")));
                AppDirectory = Path.Combine(_root, "App");
                var support = Path.Combine(_root, "Support");
                Directory.CreateDirectory(AppDirectory);
                Directory.CreateDirectory(support);
                _controller = Path.Combine(support, "Controller.exe");
                var updated = Path.Combine(support, "Updated.exe");
                Compile(support, _controller, 1);
                Compile(support, updated, 2);
                Target = Path.Combine(AppDirectory, "Target.exe");
                File.Copy(_controller, Target);
                Payload = File.ReadAllBytes(updated);
                OldHash = UpdatePackage.HashFile(Target);
                NewHash = UpdatePackage.HashBytes(Payload);
                Check(OldHash != NewHash, "Benign test builds must be distinguishable.");
                Package = UpdatePackage.GetPath(Target);
                UpdatePackage.Write(Package, Payload, new UpdateManifest
                {
                    SchemaVersion = 2, ReleaseNumber = 2, Version = "2", AssetName = "JustEnoughTools.exe",
                    Sha256 = NewHash, SizeBytes = Payload.Length, DeltaPatches = new List<DeltaManifest>()
                });
                _signalName = @"Local\JET.InstallerTest.Started." + Guid.NewGuid().ToString("N");
                RestartSignal = new Semaphore(0, 10, _signalName);
            }

            internal Child Schedule(bool restart) => StartChild("schedule", restart);
            internal Child RequestRestart() => StartChild("request", false);
            internal Child HoldOperation() => StartChild("lease", false);

            private Child StartChild(string operation, bool restart)
            {
                var child = new Child(_controller, operation,
                    TestContext.ExecutablePath ?? typeof(UpdateService).Assembly.Location, Target, restart, _signalName);
                _children.Add(child);
                child.AwaitScheduled();
                _assertions++;
                return child;
            }

            internal void AwaitInstalled()
            {
                try { WaitUntil(() =>
                {
                    try { return File.Exists(Target) && UpdatePackage.HashFile(Target) == NewHash &&
                        !File.Exists(Package) && !UpdateInstaller.IsActive(Target); }
                    catch (IOException) { return false; }
                    catch (UnauthorizedAccessException) { return false; }
                }, 25000, "The benign update was not installed and cleaned up."); }
                catch (InvalidOperationException failure)
                {
                    string state = " Target=" + (File.Exists(Target) ? UpdatePackage.HashFile(Target) : "missing") +
                        "; package=" + (File.Exists(Package) ? new FileInfo(Package).Length.ToString() : "missing") +
                        "; active=" + UpdateInstaller.IsActive(Target);
                    foreach (var child in _children) state += " " + child.Diagnostics();
                    throw new InvalidOperationException(failure.Message + state, failure);
                }
                _assertions++;
            }

            internal void CheckCleanAppDirectory()
            {
                Check(Directory.GetDirectories(AppDirectory).Length == 0, "The updater created its own directory.");
                Check(Directory.GetFiles(AppDirectory).Select(Path.GetFileName).SequenceEqual(new[] { "Target.exe" }),
                    "The updater left a package, script, journal, log or backup after success.");
            }

            public void Dispose()
            {
                foreach (var child in _children) child.AllowExit();
                foreach (var child in _children) child.Dispose();
                WaitUntil(() => !UpdateInstaller.IsActive(Target), 35000, "A test helper is still active during cleanup.");
                RestartSignal.Dispose();
                var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!_root.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(_root).StartsWith("JET-installer-tests-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Unsafe test cleanup path.");
                for (var attempt = 0; ; attempt++)
                {
                    try { Directory.Delete(_root, true); break; }
                    catch (IOException) when (attempt < 20) { Thread.Sleep(100); }
                    catch (UnauthorizedAccessException) when (attempt < 20) { Thread.Sleep(100); }
                }
            }

            private static void Compile(string support, string output, int marker)
            {
                var source = Path.Combine(support, "Harness-" + marker + ".cs");
                File.WriteAllText(source, HarnessSource.Replace("__MARKER__", marker.ToString()), new UTF8Encoding(false));
                var compiler = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "csc.exe");
                if (!File.Exists(compiler)) throw new FileNotFoundException("The .NET Framework test compiler is unavailable.", compiler);
                var start = new ProcessStartInfo(compiler,
                    "/nologo /target:winexe /platform:x64 /out:" + Quote(output) + " " + Quote(source))
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = support
                };
                using (var process = Process.Start(start))
                {
                    if (!process.WaitForExit(30000)) { process.Kill(); throw new InvalidOperationException("The benign fixture compilation timed out."); }
                    var diagnostic = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                    if (process.ExitCode != 0) throw new InvalidOperationException("Benign fixture compilation failed: " + diagnostic);
                }
            }
        }

        private sealed class Child : IDisposable
        {
            private readonly EventWaitHandle _scheduled;
            private readonly EventWaitHandle _exit;
            private readonly Process _process;
            internal Child(string controller, string operation, string application, string target, bool restart, string signalName)
            {
                var key = Guid.NewGuid().ToString("N");
                var scheduledName = @"Local\JET.InstallerTest.Scheduled." + key;
                var exitName = @"Local\JET.InstallerTest.Exit." + key;
                _scheduled = new EventWaitHandle(false, EventResetMode.ManualReset, scheduledName);
                _exit = new EventWaitHandle(false, EventResetMode.ManualReset, exitName);
                var start = new ProcessStartInfo(controller, operation + " " + Quote(application) + " " + Quote(target) + " " +
                    Quote(scheduledName) + " " + Quote(exitName) + " " + (restart ? "1" : "0"))
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(controller)
                };
                start.EnvironmentVariables["JET_INSTALLER_TEST_SIGNAL"] = signalName;
                _process = Process.Start(start);
            }

            internal void AwaitScheduled()
            {
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 25000)
                {
                    if (_scheduled.WaitOne(50)) return;
                    if (_process.HasExited) throw new InvalidOperationException("The scheduling child failed: " +
                        _process.StandardError.ReadToEnd() + _process.StandardOutput.ReadToEnd());
                }
                throw new InvalidOperationException("The scheduling child did not report readiness.");
            }

            internal void AllowExit() { _exit.Set(); }
            internal string Diagnostics()
            {
                return _process.HasExited ? _process.StandardError.ReadToEnd() + _process.StandardOutput.ReadToEnd() : "caller still running";
            }
            internal void AwaitExit()
            {
                if (!_process.WaitForExit(15000)) throw new InvalidOperationException("The scheduling child did not exit.");
                if (_process.ExitCode != 0)
                    throw new InvalidOperationException("The scheduling child reported an error: " + _process.StandardError.ReadToEnd());
                _assertions++;
            }

            public void Dispose()
            {
                _exit.Set();
                if (!_process.WaitForExit(15000)) { _process.Kill(); _process.WaitForExit(); }
                _process.Dispose();
                _scheduled.Dispose();
                _exit.Dispose();
            }
        }

        private const string HarnessSource = @"
using System;
using System.Reflection;
using System.Threading;
internal static class Harness
{
    private const int Marker = __MARKER__;
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                if (Marker == 2)
                {
                    string name = Environment.GetEnvironmentVariable(""JET_INSTALLER_TEST_SIGNAL"");
                    if (!string.IsNullOrEmpty(name)) using (Semaphore signal = Semaphore.OpenExisting(name)) signal.Release();
                }
                return 0;
            }
            Assembly application = Assembly.LoadFrom(args[1]);
            Type bootstrap = application.GetType(""JustEnoughTools.DependencyBootstrap"", true);
            bootstrap.GetMethod(""Initialize"", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            Type updates = application.GetType(""JustEnoughTools.Updates.UpdateService"", true);
            if (args[0] == ""lease"") return HoldLease(updates, args);
            object service = null;
            bool okay;
            if (args[0] == ""request"")
                okay = (bool)updates.GetMethod(""TryRequestActiveInstallationRestart"").Invoke(null, new object[] { args[2] });
            else
            {
                service = Activator.CreateInstance(updates, new object[] { args[2], 1 });
                object[] call = new object[] { args[5] == ""1"", """" };
                okay = (bool)updates.GetMethod(""TryScheduleInstall"").Invoke(service, call);
                if (!okay) Console.Error.WriteLine(call[1]);
            }
            if (!okay) return 3;
            using (EventWaitHandle scheduled = EventWaitHandle.OpenExisting(args[3])) scheduled.Set();
            using (EventWaitHandle exit = EventWaitHandle.OpenExisting(args[4]))
                if (!exit.WaitOne(60000)) return 4;
            if (service != null) ((IDisposable)service).Dispose();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static int HoldLease(Type updates, string[] args)
    {
        using (Mutex lease = (Mutex)updates.GetMethod(""CreateOperationMutex"").Invoke(null, new object[] { args[2] }))
        {
            bool owned;
            try { owned = lease.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) return 6;
            try
            {
                using (EventWaitHandle scheduled = EventWaitHandle.OpenExisting(args[3])) scheduled.Set();
                using (EventWaitHandle exit = EventWaitHandle.OpenExisting(args[4])) return exit.WaitOne(60000) ? 0 : 4;
            }
            finally { lease.ReleaseMutex(); }
        }
    }
}
";
    }
}

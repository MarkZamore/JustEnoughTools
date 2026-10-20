using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace JustEnoughTools.Tests
{
    // Exercise the supplied distributable, not the project-reference assembly.
    // Neither diagnostic flag requests elevation or applies configuration.
    public static class ConfigurationCommandTests
    {
        public static int RunAll()
        {
            string artifacts = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "Artifacts", "Portability"));
            string session = Path.Combine(artifacts, "Configuration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(session);
            string copy = Path.Combine(session, "JustEnoughTools.exe");
            byte[] original = File.ReadAllBytes(TestContext.ExecutablePath);
            File.WriteAllBytes(copy, original);
            var events = new List<string>();
            object gate = new object();
            int observerErrors = 0;
            try
            {
                Require(Directory.GetFileSystemEntries(session).SequenceEqual(new[] { copy }),
                    "Diagnostic fixture must contain only the supplied root EXE.");
                using (var watcher = new FileSystemWatcher(session))
                {
                    watcher.IncludeSubdirectories = true;
                    watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                        NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes;
                    watcher.InternalBufferSize = 65536;
                    FileSystemEventHandler change = (sender, args) =>
                    { lock (gate) events.Add(args.ChangeType + ":" + args.Name); };
                    watcher.Created += change;
                    watcher.Changed += change;
                    watcher.Deleted += change;
                    watcher.Renamed += (sender, args) => { lock (gate) events.Add("Renamed:" + args.Name); };
                    watcher.Error += (sender, args) => Interlocked.Increment(ref observerErrors);
                    watcher.EnableRaisingEvents = true;

                    var snapshot = RunCommand(copy, "--inspect-configuration");
                    ValidateSnapshot(snapshot);
                    var result = RunCommand(copy, "--check-configuration");
                    ValidateResult(result, snapshot);

                    Thread.Sleep(100);
                    watcher.EnableRaisingEvents = false;
                }
                Require(observerErrors == 0, "Diagnostic folder observer failed; its no-write result is inconclusive.");
                lock (gate) Require(events.Count == 0, "Readonly diagnostics modified their directory: " + string.Join(", ", events));
                Require(Directory.GetFileSystemEntries(session).SequenceEqual(new[] { copy }) &&
                    File.ReadAllBytes(copy).SequenceEqual(original), "Readonly diagnostics changed their EXE or created a companion file.");
                return 8;
            }
            finally
            {
                string checkedSession = Path.GetFullPath(session);
                string checkedRoot = Path.GetFullPath(artifacts).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                Guid identifier;
                string name = Path.GetFileName(checkedSession);
                if (!checkedSession.StartsWith(checkedRoot, StringComparison.OrdinalIgnoreCase) ||
                    !name.StartsWith("Configuration-", StringComparison.Ordinal) ||
                    !Guid.TryParseExact(name.Substring("Configuration-".Length), "N", out identifier))
                    throw new InvalidOperationException("Refusing to clean an unverified diagnostic-fixture path.");
                if (Directory.Exists(checkedSession) && (File.GetAttributes(checkedSession) & FileAttributes.ReparsePoint) == 0)
                    Directory.Delete(checkedSession, true);
            }
        }

        private static IDictionary<string, object> RunCommand(string executable, string argument)
        {
            var start = new ProcessStartInfo(executable, argument)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.EnvironmentVariables.Remove("DEVPATH");
            start.EnvironmentVariables.Remove("APP_CONFIG_FILE");
            start.EnvironmentVariables.Remove("COMPLUS_Version");
            start.EnvironmentVariables["DOTNET_ROOT"] = Path.Combine(start.WorkingDirectory, "dotnet-not-installed");
            start.EnvironmentVariables["DOTNET_MULTILEVEL_LOOKUP"] = "0";
            start.EnvironmentVariables["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
            using (var process = Process.Start(start))
            {
                Require(process != null, argument + " did not start directly without shell elevation.");
                // Snapshot JSON may exceed a pipe buffer; drain both streams
                // before waiting so a healthy diagnostic process cannot deadlock.
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(45000))
                {
                    process.Kill();
                    process.WaitForExit(5000);
                    throw new InvalidOperationException(argument + " did not finish as a readonly CLI command.");
                }
                string output = outputTask.GetAwaiter().GetResult().Trim().TrimStart('\ufeff');
                string error = errorTask.GetAwaiter().GetResult();
                Require(process.ExitCode == 0 && string.IsNullOrWhiteSpace(error), argument + " failed: " + error);
                Require(output.Length > 2, argument + " returned no JSON result.");
                object decoded;
                try { decoded = new JavaScriptSerializer().DeserializeObject(output); }
                catch (ArgumentException failure) { throw new InvalidOperationException(argument + " returned invalid JSON.", failure); }
                var result = Map(decoded, argument);
                Require(result.Count > 0, argument + " returned an empty JSON object.");
                return result;
            }
        }

        private static void ValidateSnapshot(IDictionary<string, object> snapshot)
        {
            foreach (string property in new[] { "CollectedAtUtc", "Windows", "Defender", "Mde", "SmartAppControl", "AntivirusProducts", "AntivirusHealth", "ThirdPartyAntivirusConfirmed" })
                Require(snapshot.ContainsKey(property) && snapshot[property] != null, "Snapshot is missing " + property + ".");
            Require(ValidateObservations(snapshot) >= 16, "Snapshot omitted its typed status observations.");

            var windows = Map(snapshot["Windows"], "Windows");
            foreach (string property in new[] { "Edition", "DisplayVersion", "Build", "Architecture" })
                ValidateTextObservation(Observation(windows, property), "Windows." + property);
            var defender = Map(snapshot["Defender"], "Defender");
            foreach (string property in new[] { "PlatformVersion", "RunningMode", "TamperProtectionSource" })
                ValidateTextObservation(Observation(defender, property), "Defender." + property);
            foreach (string property in new[] { "AntimalwareServiceEnabled", "AntivirusEnabled", "RealTimeProtectionEnabled", "OnAccessProtectionEnabled", "IoavProtectionEnabled", "BehaviorMonitorEnabled", "TamperProtectionEnabled" })
                ValidateBooleanObservation(Observation(defender, property), "Defender." + property);
            ValidateEnumObservation(Observation(Map(snapshot["Mde"], "Mde"), "OnboardingState"), 1, "Mde.OnboardingState");
            var sac = Map(snapshot["SmartAppControl"], "SmartAppControl");
            ValidateTextObservation(Observation(sac, "ReportedState"), "SmartAppControl.ReportedState");
            ValidateEnumObservation(Observation(sac, "PolicyState"), 2, "SmartAppControl.PolicyState");
            ValidateEnumObservation(Map(snapshot["AntivirusHealth"], "AntivirusHealth"), 3, "AntivirusHealth");
            ValidateBooleanObservation(Map(snapshot["ThirdPartyAntivirusConfirmed"], "ThirdPartyAntivirusConfirmed"), "ThirdPartyAntivirusConfirmed");

            var products = Map(snapshot["AntivirusProducts"], "AntivirusProducts");
            if (!(bool)products["IsKnown"]) return;
            var list = products["Value"] as object[];
            Require(list != null, "Known AntivirusProducts must serialize as an array.");
            foreach (object item in list)
            {
                var product = Map(item, "AntivirusProducts.Value item");
                ValidateTextObservation(Observation(product, "Name"), "Product.Name");
                var state = Observation(product, "State");
                var signatures = Observation(product, "SignatureStatus");
                var health = Observation(product, "ReportedHealthy");
                ValidateEnumObservation(state, 3, "Product.State");
                ValidateEnumObservation(signatures, 1, "Product.SignatureStatus");
                ValidateBooleanObservation(health, "Product.ReportedHealthy");
                if (!(bool)state["IsKnown"] || !(bool)signatures["IsKnown"])
                    Require(!(bool)health["IsKnown"] && health["Value"] == null,
                        "Unknown product state/signature status became a known healthy result.");
            }
        }

        private static void ValidateResult(IDictionary<string, object> result, IDictionary<string, object> snapshot)
        {
            foreach (string property in new[] { "Succeeded", "CanApply", "PerformedChanges", "ChangesMayHaveOccurred", "EngineOffVerified", "VirusUiHiddenVerified", "PersistenceVerified" })
                Require(result.ContainsKey(property) && result[property] is bool && !(bool)result[property],
                    "Readonly configuration check must explicitly return " + property + "=false without a verified application profile.");
            Require(result.ContainsKey("Status") && result["Status"] is int &&
                (int)result["Status"] >= 0 && (int)result["Status"] <= 2,
                "Readonly status must be Unsupported, Blocked or Unverified; undefined, Pending and Verified values are invalid.");
            object rawMessage;
            Require(result.TryGetValue("Message", out rawMessage) && rawMessage is string, "Configuration check omitted its explanation.");
            string message = (string)rawMessage;
            Require(!string.IsNullOrWhiteSpace(message) && message.Length > 40 &&
                message.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length >= 5 &&
                message.IndexOf("Удаление ещё не прошло проверку", StringComparison.OrdinalIgnoreCase) < 0,
                "Configuration check must explain an observed compatibility obstacle or missing profile, not the former removal placeholder.");
            var edition = Observation(Map(snapshot["Windows"], "Windows"), "Edition");
            if ((bool)edition["IsKnown"] && ((string)edition["Value"]).StartsWith("Core", StringComparison.OrdinalIgnoreCase))
            {
                Require((int)result["Status"] == 0 &&
                    (message.IndexOf("Home", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf((string)edition["Value"], StringComparison.OrdinalIgnoreCase) >= 0),
                    "A Windows Home/Core snapshot must have an unsupported result that identifies its edition.");
            }
        }

        private static int ValidateObservations(object value)
        {
            var fields = value as IDictionary<string, object>;
            if (fields != null)
            {
                int count = 0;
                if (fields.ContainsKey("IsKnown"))
                {
                    Require(fields["IsKnown"] is bool && fields.ContainsKey("Value"), "Status observation lost its typed known/value fields.");
                    bool known = (bool)fields["IsKnown"];
                    Require(known ? fields["Value"] != null : fields["Value"] == null,
                        "Unknown evidence must serialize as null rather than a default false/zero or a verified value.");
                    foreach (string property in new[] { "Source", "Details" })
                        Require(fields.ContainsKey(property) && fields[property] is string && !string.IsNullOrWhiteSpace((string)fields[property]),
                            "Status observation omitted " + property + ".");
                    count++;
                }
                foreach (object child in fields.Values) count += ValidateObservations(child);
                return count;
            }
            var items = value as IEnumerable;
            if (items == null || value is string) return 0;
            int total = 0;
            foreach (object child in items) total += ValidateObservations(child);
            return total;
        }

        private static IDictionary<string, object> Observation(IDictionary<string, object> parent, string property)
        {
            Require(parent.ContainsKey(property), "Missing observation " + property + ".");
            return Map(parent[property], property);
        }

        private static void ValidateBooleanObservation(IDictionary<string, object> observation, string path)
        {
            if ((bool)observation["IsKnown"]) Require(observation["Value"] is bool, path + " must retain a JSON boolean.");
        }

        private static void ValidateTextObservation(IDictionary<string, object> observation, string path)
        {
            if ((bool)observation["IsKnown"]) Require(observation["Value"] is string && !string.IsNullOrWhiteSpace((string)observation["Value"]), path + " must retain nonempty known text.");
        }

        private static void ValidateEnumObservation(IDictionary<string, object> observation, int maximum, string path)
        {
            if (!(bool)observation["IsKnown"]) return;
            Require(observation["Value"] is int && (int)observation["Value"] >= 0 && (int)observation["Value"] <= maximum,
                path + " contains an undefined enum state or an unexpected JSON type.");
        }

        private static IDictionary<string, object> Map(object value, string path)
        {
            var result = value as IDictionary<string, object>;
            Require(result != null, path + " must be a JSON object.");
            return result;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}

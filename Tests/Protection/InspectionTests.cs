using System;
using System.Diagnostics;
using System.Linq;
using JustEnoughTools.Protection;

namespace JustEnoughTools.Tests
{
    public static class ProtectionInspectionTests
    {
        public static int RunAll()
        {
            var snapshot = ProtectionService.Inspect();
            Require(snapshot.Components != null && snapshot.Components.Count >= 10, "Protection inventory is incomplete.");
            Require(snapshot.Components.Select(component => component.Id).Distinct(StringComparer.Ordinal).Count() == snapshot.Components.Count,
                "Protection inventory must not contain ambiguous duplicate IDs.");
            Require(snapshot.Components.All(component => !string.IsNullOrWhiteSpace(component.DisplayName) && !string.IsNullOrWhiteSpace(component.Details)),
                "Every protection query must identify its scope and result.");

            // Compare a real query through the system service tool with the native
            // implementation. A stopped service is still installed, not absent.
            using (var query = Process.Start(new ProcessStartInfo("sc.exe", "query WinDefend")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }))
            {
                if (!query.WaitForExit(10000))
                {
                    query.Kill();
                    throw new InvalidOperationException("The independent Defender service query timed out.");
                }
                query.StandardOutput.ReadToEnd();
                query.StandardError.ReadToEnd();
                var component = snapshot.Components.Single(item => item.Id == "Service.WinDefend");
                if (query.ExitCode == 0)
                    Require(component.State == ProtectionComponentState.Present, "An installed Defender service was misreported as absent.");
                else if (query.ExitCode == 1060)
                    Require(component.State == ProtectionComponentState.Absent, "A missing Defender service was not distinguished from an unknown state.");
            }

            // No ordinary test invokes a removal entrypoint. That remains true
            // even if a future profile matches the developer's host OS.
            return 3;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}

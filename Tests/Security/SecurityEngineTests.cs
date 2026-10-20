using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using JustEnoughTools.Security;

namespace JustEnoughTools.Tests
{
    // Only fake backends are exercised here. These tests never call Run(), start
    // PowerShell, inspect protection, elevate, or write a Windows setting.
    public static class SecurityEngineTests
    {
        private sealed class FakeBackend : ISecurityBackend
        {
            internal Func<SecurityAction, ActionResult> Handler;
            internal Func<SecuritySnapshot> SnapshotFactory;
            internal readonly List<string> Calls = new List<string>();
            public SecuritySnapshot Inspect() { return SnapshotFactory == null ? Snapshot(ActionStateKind.NeedsChange) : SnapshotFactory(); }
            public ActionResult Execute(SecurityAction action)
            {
                Calls.Add(action.Id);
                return Handler == null ? SecurityEngine.Result(action, ActionOutcome.Applied, "Подтверждено.") : Handler(action);
            }
        }

        public static int RunAll()
        {
            int checks = 0;
            var all = new FakeBackend();
            SecurityReport report = SecurityEngine.ExecuteCatalogue(all);
            Require(report.Succeeded && report.Results.Count == SecurityEngine.Catalogue.Count, "A fully verified catalogue did not succeed."); checks++;
            Require(all.Calls.SequenceEqual(SecurityEngine.Catalogue.Select(action => action.Id)), "Fixed catalogue order changed during execution."); checks++;
            Require(all.Calls.Distinct(StringComparer.Ordinal).Count() == all.Calls.Count, "A setting is executed more than once."); checks++;

            var blocked = new FakeBackend { Handler = action =>
            {
                if (action.Id == "defender.realtime") throw new UnauthorizedAccessException("Блокировка тестового провайдера.");
                return SecurityEngine.Result(action, ActionOutcome.Applied, "Подтверждено.");
            }};
            report = SecurityEngine.ExecuteCatalogue(blocked);
            Require(!report.Succeeded && report.Results[0].Outcome == ActionOutcome.Blocked, "Access denial was represented as success."); checks++;
            Require(blocked.Calls.Contains("firewall.public") && blocked.Calls.Count == SecurityEngine.Catalogue.Count,
                "A blocked Defender operation prevented an independent firewall operation."); checks++;
            Require(report.FormatProblems().Contains("Блокировка тестового провайдера") && report.FormatProblems().Contains("17 из 18"),
                "The final problem report lost the precise reason or partial success."); checks++;

            foreach (ActionOutcome problem in new[] { ActionOutcome.Blocked, ActionOutcome.Unavailable, ActionOutcome.Unverified, ActionOutcome.Failed })
            {
                var fake = new FakeBackend { Handler = action => SecurityEngine.Result(action,
                    action.Id == "firewall.public" ? problem : ActionOutcome.AlreadySet, "Эффективное состояние не подтверждено.") };
                report = SecurityEngine.ExecuteCatalogue(fake);
                Require(!report.Succeeded && report.Results.Last(item => item.Id == "firewall.public").Outcome == problem,
                    "An incomplete effective state was promoted to success."); checks++;
            }

            var invalid = new FakeBackend { Handler = action => action.Id == "defender.cloud" ? null :
                SecurityEngine.Result(action, ActionOutcome.AlreadySet, "Уже установлено.") };
            report = SecurityEngine.ExecuteCatalogue(invalid);
            Require(!report.Succeeded && report.Results[1].Outcome == ActionOutcome.Unverified && invalid.Calls.Count == 18,
                "A missing result skipped the remainder of the catalogue."); checks++;

            invalid.Handler = action => new ActionResult { Id = "foreign.action", Title = "Другой заголовок", Outcome = ActionOutcome.Applied };
            report = SecurityEngine.ExecuteCatalogue(invalid);
            Require(!report.Succeeded && report.Results.All(item => item.Outcome == ActionOutcome.Unverified),
                "A result for another action was accepted."); checks++;

            invalid.Handler = action => new ActionResult { Id = action.Id, Title = "Недоверенный заголовок", Outcome = (ActionOutcome)999 };
            report = SecurityEngine.ExecuteCatalogue(invalid);
            Require(!report.Succeeded && report.Results.All(item => item.Outcome == ActionOutcome.Unverified), "An undefined enum value was accepted."); checks++;

            report = SecurityEngine.ExecuteCatalogue(new FakeBackend());
            var empty = new SecurityReport();
            Require(!empty.Succeeded && empty.FormatProblems().Contains("не вернул"), "An empty report claimed success."); checks++;
            report.Results.Add(null);
            Require(!report.Succeeded && report.FormatProblems().Contains("Отсутствует результат"), "A null result was ignored."); checks++;

            Require(!WindowsSecurityBackend.SupportsWindowsPolicies("Core") && !WindowsSecurityBackend.SupportsWindowsPolicies("CoreSingleLanguage") &&
                !WindowsSecurityBackend.SupportsWindowsPolicies(null) && !WindowsSecurityBackend.SupportsWindowsPolicies("ServerStandard") &&
                !WindowsSecurityBackend.SupportsWindowsPolicies("EnterpriseUnknown"),
                "Unsupported/unknown editions accepted enterprise policy applicability."); checks++;
            Require(WindowsSecurityBackend.SupportsWindowsPolicies("Professional") && WindowsSecurityBackend.SupportsWindowsPolicies("EnterpriseS") &&
                WindowsSecurityBackend.SupportsWindowsPolicies("Education") && WindowsSecurityBackend.SupportsWindowsPolicies("IoTEnterprise"),
                "Documented client policy editions were rejected."); checks++;

            SecuritySnapshot snapshot = Snapshot(ActionStateKind.AlreadyDesired);
            Require(snapshot.IsComplete && !snapshot.HasChanges && !snapshot.HasUnknown && !snapshot.RequiresElevation && snapshot.ToReport().Succeeded,
                "Already disabled settings require another operation/elevation."); checks++;
            var noChanges = new FakeBackend { SnapshotFactory = () => snapshot };
            report = SecurityEngine.ExecuteCatalogue(noChanges);
            Require(report.Succeeded && noChanges.Calls.Count == 0, "The engine rewrote settings which are already disabled."); checks++;

            snapshot = Snapshot(ActionStateKind.Unavailable);
            Require(snapshot.IsComplete && !snapshot.HasChanges && !snapshot.RequiresElevation && snapshot.ToReport().Succeeded &&
                snapshot.ToReport().Results[0].Reason.Contains("не утверждение"), "Manual-only switches cause perpetual UAC or a false all-protection-off claim."); checks++;
            snapshot = Snapshot(ActionStateKind.Unknown);
            Require(snapshot.HasUnknown && !snapshot.HasChanges && !snapshot.IsComplete && !snapshot.RequiresElevation && !snapshot.ToReport().Succeeded,
                "Unknown state was treated as enabled or as requiring elevation."); checks++;

            foreach (SecuritySnapshot malformed in new[] { new SecuritySnapshot(), new SecuritySnapshot { Actions = null } })
            {
                Require(malformed.HasUnknown && !malformed.IsComplete && !malformed.HasChanges && !malformed.RequiresElevation && !malformed.ToReport().Succeeded,
                    "Empty/missing snapshot was treated as a completed setting."); checks++;
            }
            snapshot = Snapshot(ActionStateKind.NeedsChange);
            snapshot.Actions.RemoveAt(0);
            Require(snapshot.HasUnknown && !snapshot.HasChanges && !snapshot.RequiresElevation && !snapshot.ToReport().Succeeded,
                "A missing catalogue item was accepted for execution/elevation."); checks++;

            snapshot = Snapshot(ActionStateKind.AlreadyDesired);
            snapshot.Actions.Find(item => item.Id == "firewall.public").State = ActionStateKind.NeedsChange;
            var onlyPending = new FakeBackend { SnapshotFactory = () => snapshot };
            report = SecurityEngine.ExecuteCatalogue(onlyPending);
            Require(snapshot.HasChanges && snapshot.RequiresElevation && !snapshot.IsComplete && report.Succeeded &&
                onlyPending.Calls.SequenceEqual(new[] { "firewall.public" }), "The engine changed more than the known enabled setting."); checks++;

            snapshot.Actions.Find(item => item.Id == "security.sac").State = ActionStateKind.Unavailable;
            report = SecurityEngine.ExecuteCatalogue(onlyPending);
            Require(!report.Succeeded && report.Results.Any(item => item.Id == "security.sac" && item.Outcome == ActionOutcome.Unavailable),
                "A real partial execution hid the unavailable switch reason."); checks++;

            snapshot = Snapshot(ActionStateKind.AlreadyDesired);
            var race = new FakeBackend { SnapshotFactory = () => snapshot };
            report = SecurityEngine.ExecuteCatalogue(race);
            Require(race.Calls.Count == 0 && report.Succeeded, "The execution probe did not skip an action disabled before acquiring the operation lease."); checks++;

            Require(WindowsSecurityBackend.ClassifyPuaProtection(0) == ActionStateKind.AlreadyDesired,
                "Disabled PUA blocking required another administrative operation."); checks++;
            Require(WindowsSecurityBackend.ClassifyPuaProtection(1) == ActionStateKind.NeedsChange,
                "Enabled PUA blocking was treated as disabled."); checks++;
            Require(WindowsSecurityBackend.ClassifyPuaProtection(2) == ActionStateKind.AlreadyDesired,
                "Audit-only PUA mode was treated as active blocking."); checks++;
            foreach (int? unknown in new int?[] { null, -1, 3, Int32.MaxValue })
            {
                Require(WindowsSecurityBackend.ClassifyPuaProtection(unknown) == ActionStateKind.Unknown,
                    "An unknown PUA mode was interpreted as enabled or disabled."); checks++;
            }
            snapshot = Snapshot(ActionStateKind.AlreadyDesired);
            snapshot.Actions.Find(item => item.Id == "defender.pua").State = WindowsSecurityBackend.ClassifyPuaProtection(2);
            var auditOnly = new FakeBackend { SnapshotFactory = () => snapshot };
            report = SecurityEngine.ExecuteCatalogue(auditOnly);
            Require(snapshot.IsComplete && !snapshot.HasChanges && !snapshot.RequiresElevation && auditOnly.Calls.Count == 0 && report.Succeeded,
                "PUA audit mode triggered repeat execution or elevation."); checks++;

            var pua = new ActionResult { Id = "defender.pua", Outcome = ActionOutcome.Unverified, Before = "1", After = "2" };
            pua = WindowsSecurityBackend.InterpretPuaResult(pua);
            Require(pua.Outcome == ActionOutcome.Applied && pua.After == "2" && pua.Reason.Contains("аудит сохраняется") &&
                pua.Reason.Contains("полное отключение обнаружения PUA не подтверждается"), "PUA audit readback lost its remaining audit limitation."); checks++;
            pua = new ActionResult { Id = "defender.pua", Outcome = ActionOutcome.Unverified, Before = "1", After = "1" };
            Require(WindowsSecurityBackend.InterpretPuaResult(pua).Outcome == ActionOutcome.Unverified,
                "Remaining enabled blocking was promoted to applied."); checks++;
            pua = new ActionResult { Id = "defender.pua", Outcome = ActionOutcome.Failed, Before = "1", After = "2" };
            Require(WindowsSecurityBackend.InterpretPuaResult(pua).Outcome == ActionOutcome.Failed,
                "A failed provider command was hidden by audit-mode interpretation."); checks++;
            pua = new ActionResult { Id = "defender.pua", Outcome = ActionOutcome.AlreadySet, Before = "2", After = "2" };
            pua = WindowsSecurityBackend.InterpretPuaResult(pua);
            Require(pua.Outcome == ActionOutcome.AlreadySet && pua.Reason.Contains("аудит сохраняется") && pua.Reason.Contains("не выполнялась"),
                "An already-audit result failed to preserve the no-write explanation."); checks++;

            string probeScript = (string)typeof(WindowsSecurityBackend).GetField("SnapshotScript", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
            Require(Convert.ToBase64String(Encoding.Unicode.GetBytes(probeScript)).Length <= 28000,
                "Trusted module imports caused the read-only command to exceed the process argument limit."); checks++;
            Require(probeScript.Contains("$env:PSModulePath = [IO.Path]::Combine($PSHOME, 'Modules')") &&
                probeScript.Contains("Microsoft.PowerShell.Core\\Import-Module") && probeScript.Contains("Defender\\Get-MpPreference") &&
                probeScript.IndexOf("Set-MpPreference", StringComparison.OrdinalIgnoreCase) < 0 &&
                probeScript.IndexOf("Set-NetFirewallProfile", StringComparison.OrdinalIgnoreCase) < 0 &&
                probeScript.IndexOf("Disable-ScheduledTask", StringComparison.OrdinalIgnoreCase) < 0,
                "The terminable read-only probe lost trusted module isolation or contains a mutating command."); checks++;
            return checks;
        }

        private static SecuritySnapshot Snapshot(ActionStateKind state)
        {
            return new SecuritySnapshot { Actions = SecurityEngine.Catalogue.Select(action => new ActionState
            {
                Id = action.Id, Title = action.Title, State = state, RequiresElevation = true,
                Reason = "Тестовый снимок состояния."
            }).ToList() };
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}

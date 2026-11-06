using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using JustEnoughTools.Platform;
using JustEnoughTools.Security;

namespace JustEnoughTools.Tests
{
    // Only fake backends/providers are exercised here. The RTP script tests run
    // with in-memory provider functions in place of every Windows security module.
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

            blocked.Handler = action =>
            {
                if (action.Id == "defender.realtime")
                    throw new InvalidOperationException("Запуск настройки защиты: отказано в доступе.", new Win32Exception(5));
                return SecurityEngine.Result(action, ActionOutcome.Applied, "Подтверждено.");
            };
            report = SecurityEngine.ExecuteCatalogue(blocked);
            Require(report.Results[0].Outcome == ActionOutcome.Blocked && report.Results[0].Reason.Contains("Win32=5") &&
                report.Results[0].Reason.Contains("Запуск настройки защиты") && report.Results.Single(item => item.Id == "firewall.public").Outcome == ActionOutcome.Applied,
                "A native launch denial lost its stage/code or blocked independent actions."); checks++;

            int probeCalls = 0, setterCalls = 0;
            var deniedProbe = new WindowsSecurityBackend((script, readOnly) =>
            {
                if (readOnly) probeCalls++; else setterCalls++;
                throw new InvalidOperationException("Запуск проверки защиты: отказано в доступе.", new Win32Exception(5));
            }, "Core", 0);
            SecuritySnapshot deniedSnapshot = deniedProbe.Inspect();
            Require(!deniedSnapshot.HasChanges && deniedSnapshot.HasUnknown && !deniedSnapshot.RequiresElevation &&
                deniedSnapshot.Actions.Where(item => item.State == ActionStateKind.Unknown).All(item => item.Reason.Contains("Win32=5")),
                "A denied read-only launch became an enabled setting/elevation reason or lost the native error."); checks++;
            report = SecurityEngine.ExecuteCatalogue(deniedProbe);
            Require(!report.Succeeded && probeCalls == 2 && setterCalls == 0,
                "A denied snapshot invoked a setter or falsely succeeded."); checks++;

            var independentProbe = new WindowsSecurityBackend((script, readOnly) =>
            {
                Require(readOnly, "The fake provider must not execute a setter.");
                return new WindowsSecurityBackend.ProcessOutput { ExitCode = 0, Errors = "", Output =
                    "{\"Actions\":[{\"Id\":\"defender.realtime\",\"State\":3,\"Reason\":\"Отказ тестового Defender (Win32 5).\",\"RequiresElevation\":true}," +
                    "{\"Id\":\"firewall.public\",\"State\":0,\"Reason\":\"Профиль выключен.\",\"RequiresElevation\":true}],\"PuaProtection\":2}" };
            }, "Core", 0);
            SecuritySnapshot independent = independentProbe.Inspect();
            Require(independent.Actions.Single(item => item.Id == "defender.realtime").State == ActionStateKind.Unknown &&
                independent.Actions.Single(item => item.Id == "firewall.public").State == ActionStateKind.AlreadyDesired &&
                independent.Actions.Single(item => item.Id == "defender.pua").State == ActionStateKind.AlreadyDesired &&
                !independent.RequiresElevation,
                "A single provider denial replaced independently readable states or requested UAC."); checks++;

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
            const string setterMetadataRead = @"Microsoft.PowerShell.Core\Get-Command Defender\Set-MpPreference -ErrorAction Stop";
            Require(probeScript.Contains("$env:PSModulePath = [IO.Path]::Combine($PSHOME, 'Modules')") &&
                probeScript.Contains("Microsoft.PowerShell.Core\\Import-Module") && probeScript.Contains("Defender\\Get-MpPreference") &&
                probeScript.Contains(setterMetadataRead) && probeScript.Replace(setterMetadataRead, "").IndexOf("Set-MpPreference", StringComparison.OrdinalIgnoreCase) < 0 &&
                probeScript.IndexOf("Set-NetFirewallProfile", StringComparison.OrdinalIgnoreCase) < 0 &&
                probeScript.IndexOf("Disable-ScheduledTask", StringComparison.OrdinalIgnoreCase) < 0,
                "The terminable read-only probe lost trusted module isolation or contains a mutating command."); checks++;
            checks += CheckRealtimeProtection();
            return checks;
        }

        private static int CheckRealtimeProtection()
        {
            const string active = "[pscustomobject]@{RealTimeProtectionEnabled=$true;IsTamperProtected=$false;SmartAppControlState='Off'}";
            const string protectedActive = "[pscustomobject]@{RealTimeProtectionEnabled=$true;IsTamperProtected=$true;SmartAppControlState='Off'}";
            const string protectedOff = "[pscustomobject]@{RealTimeProtectionEnabled=$false;IsTamperProtected=$true;SmartAppControlState='Off'}";
            const string unknownRuntime = "[pscustomobject]@{RealTimeProtectionEnabled='False';IsTamperProtected=$true;SmartAppControlState='Off'}";
            const string unknownTamper = "[pscustomobject]@{RealTimeProtectionEnabled=$true;IsTamperProtected='True';SmartAppControlState='Off'}";
            const string unknownRead = "throw [ComponentModel.Win32Exception]::new(5, 'Fake runtime read denied')";
            const string deniedWrite = "throw [ComponentModel.Win32Exception]::new(5, 'Fake RTP write denied')";
            const string desiredPref = "[pscustomobject]@{DisableRealtimeMonitoring=$true;MAPSReporting=0;SubmitSamplesConsent=2;PUAProtection=2;EnableControlledFolderAccess=0}";
            const string retainedPref = "[pscustomobject]@{DisableRealtimeMonitoring=$false}";
            const string deniedPref = "throw [ComponentModel.Win32Exception]::new(5, 'Fake preference read denied')";
            SecurityAction rtp = SecurityEngine.Catalogue.Single(action => action.Id == SecurityEngine.RealtimeActionId);
            int checks = 0;

            var blockedBackend = FakeRealtimeProvider(protectedActive, protectedActive, desiredPref, "", true, false);
            SecuritySnapshot blocked = blockedBackend.Inspect();
            Require(!blocked.HasChanges && !blocked.RequiresElevation && !blocked.HasUnknown && !blocked.IsComplete &&
                blocked.Actions.Single(item => item.Id == rtp.Id).State == ActionStateKind.Blocked &&
                blocked.ToReport().Results.Single(item => item.Id == rtp.Id).Outcome == ActionOutcome.TamperProtected,
                "Confirmed active RTP and Tamper Protection must report a blocker without requesting UAC or hiding it."); checks++;
            SecurityReport blockedReport = blocked.ToReport();
            Require(!blockedReport.Succeeded && blockedReport.FormatProblems(includeTamperInstructions: true).Contains(SecurityEngine.TamperProtectionInstructions) &&
                !blockedReport.FormatProblems().Contains(SecurityEngine.TamperProtectionInstructions),
                "The Runner problem report must include instructions, while the main window owns its own help panel."); checks++;
            blocked.Actions.Single(item => item.Id == "firewall.public").State = ActionStateKind.NeedsChange;
            var otherActions = new FakeBackend { SnapshotFactory = () => blocked };
            SecurityReport partial = SecurityEngine.ExecuteCatalogue(otherActions);
            Require(blocked.HasChanges && blocked.RequiresElevation && otherActions.Calls.SequenceEqual(new[] { "firewall.public" }) &&
                partial.Results.Single(item => item.Id == rtp.Id).Outcome == ActionOutcome.TamperProtected,
                "The RTP blocker must not block independent actions or be passed to a setter."); checks++;

            foreach (var pair in new[] { Tuple.Create(protectedOff, ActionStateKind.AlreadyDesired),
                Tuple.Create(unknownRuntime, ActionStateKind.Unknown), Tuple.Create(unknownTamper, ActionStateKind.Unknown),
                Tuple.Create(active, ActionStateKind.NeedsChange) })
            {
                SecuritySnapshot state = FakeRealtimeProvider(pair.Item1, pair.Item1, desiredPref, "", true, false).Inspect();
                Require(state.Actions.Single(item => item.Id == rtp.Id).State == pair.Item2 &&
                    state.RequiresElevation == (pair.Item2 == ActionStateKind.NeedsChange),
                    "The read-only RTP probe conflated active, already off, or malformed provider values."); checks++;
            }

            foreach (var pair in new[] { Tuple.Create(protectedOff, ActionOutcome.AlreadySet),
                Tuple.Create(protectedActive, ActionOutcome.TamperProtected), Tuple.Create(unknownRuntime, ActionOutcome.Unverified),
                Tuple.Create(unknownTamper, ActionOutcome.Unverified) })
            {
                ActionResult result = FakeRealtimeProvider(pair.Item1, pair.Item1, deniedPref, deniedWrite, false, false).Execute(rtp);
                Require(result.Outcome == pair.Item2, "The worker did not recheck actual RTP and Tamper Protection before the setter."); checks++;
            }

            SecuritySnapshot unsupported = FakeRealtimeProvider(active, active, desiredPref, "", true, false, false).Inspect();
            Require(unsupported.Actions.Single(item => item.Id == rtp.Id).State == ActionStateKind.Unavailable &&
                !unsupported.HasChanges && !unsupported.RequiresElevation,
                "An unsupported RTP setter parameter was still an elevation reason."); checks++;
            ActionResult unavailable = FakeRealtimeProvider(active, active, desiredPref, "", false, false, false).Execute(rtp);
            Require(unavailable.Outcome == ActionOutcome.Unavailable, "The worker ignored a missing RTP setter parameter."); checks++;

            ActionResult mismatch = FakeRealtimeProvider(active, protectedOff, retainedPref, "", false, true).Execute(rtp);
            Require(mismatch.Outcome == ActionOutcome.Applied && mismatch.After.Contains("DisableRealtimeMonitoring=False") &&
                mismatch.After.Contains("RealTimeProtectionEnabled=False"), "A preference mismatch prevented final actual RTP verification."); checks++;

            ActionResult accepted = FakeRealtimeProvider(active, active, desiredPref, "", false, true).Execute(rtp);
            Require(accepted.Outcome == ActionOutcome.Unverified && accepted.After.Contains("DisableRealtimeMonitoring=True") &&
                accepted.After.Contains("RealTimeProtectionEnabled=True"), "An accepted preference write was treated as proof of RTP being off."); checks++;

            ActionResult race = FakeRealtimeProvider(active, protectedActive, retainedPref, "", false, true).Execute(rtp);
            Require(race.Outcome == ActionOutcome.TamperProtected && race.After.Contains("RealTimeProtectionEnabled=True"),
                "A confirmed Tamper change after the attempt lost its typed blocker or runtime evidence."); checks++;

            ActionResult errors = FakeRealtimeProvider(active, protectedOff, deniedPref, deniedWrite, false, true).Execute(rtp);
            Require(errors.Outcome == ActionOutcome.Applied && errors.After.Contains("RealTimeProtectionEnabled=False") &&
                errors.Reason.Contains("Чтение предпочтения: Fake preference read denied (Win32 5)") &&
                errors.Reason.Contains("Запись предпочтения: Fake RTP write denied (Win32 5)"),
                "Setter/readback errors hid independently confirmed runtime Off or lost native diagnostics."); checks++;

            ActionResult unreadable = FakeRealtimeProvider(active, unknownRead, desiredPref, "", false, true).Execute(rtp);
            Require(unreadable.Outcome == ActionOutcome.Unverified && unreadable.After.Contains("RealTimeProtectionEnabled=Unknown") &&
                unreadable.Reason.Contains("Чтение фактического состояния RTP: Fake runtime read denied (Win32 5)"),
                "An unreadable final RTP state was treated as success or lost the native error."); checks++;
            return checks;
        }

        private static WindowsSecurityBackend FakeRealtimeProvider(string initial, string final, string preference, string setter,
            bool expectReadOnly, bool expectSetter, bool supportsParameter = true)
        {
            const string marker = "TEST-RTP-SETTER";
            string provider = @"
$script:statusCalls = 0
function Read-TestStatus {
    [CmdletBinding()] param()
    $script:statusCalls++
    if ($script:statusCalls -eq 1) { " + initial + @"; return }
    " + final + @"
}
function Read-TestPreference { [CmdletBinding()] param(); " + preference + @" }
function Set-TestPreference {
    [CmdletBinding()] param([bool]$" + (supportsParameter ? "DisableRealtimeMonitoring" : "OtherPreference") + @")
    [Console]::Error.Write('" + marker + @"')
    if (-not $DisableRealtimeMonitoring) { throw 'Unexpected fake setter value.' }
    " + setter + @"
}
function Read-TestScheduledTask {
    [CmdletBinding()] param([string]$TaskPath, [string]$TaskName)
    [pscustomobject]@{Settings=[pscustomobject]@{Enabled=$false}}
}
function Read-TestFirewallProfile {
    [CmdletBinding()] param([string]$PolicyStore)
    foreach ($name in @('Domain','Private','Public')) { [pscustomobject]@{Name=$name;Enabled=0} }
}
";
            return new WindowsSecurityBackend((original, readOnly) =>
            {
                Require(readOnly == expectReadOnly, "The backend selected the wrong read-only/worker path.");
                string script = provider + original.Replace("Import-SystemModule 'Defender'", "$null = 0")
                    .Replace("Import-SystemModule 'ScheduledTasks'", "$null = 0")
                    .Replace("Import-SystemModule 'NetSecurity'", "$null = 0")
                    .Replace(@"Defender\Get-MpComputerStatus", "Read-TestStatus")
                    .Replace(@"Defender\Get-MpPreference", "Read-TestPreference")
                    .Replace(@"Defender\Set-MpPreference", "Set-TestPreference")
                    .Replace(@"ScheduledTasks\Get-ScheduledTask", "Read-TestScheduledTask")
                    .Replace(@"'\Microsoft\Windows\Windows Defender\'", "'TEST-SCHEDULED-TASK'")
                    .Replace(@"NetSecurity\Get-NetFirewallProfile", "Read-TestFirewallProfile")
                    .Replace("Start-Sleep -Milliseconds 300", "$null = 0");
                Require(!new[] { @"Defender\", @"ScheduledTasks\", @"NetSecurity\", "'Defender'", "'ScheduledTasks'", "'NetSecurity'",
                    "Get-MpComputerStatus", "Get-MpPreference", "Set-MpPreference", "Disable-ScheduledTask", "Set-NetFirewallProfile" }
                    .Any(script.Contains), "A production provider reached the fake PowerShell test.");
                using (Process child = WindowsPowerShell.Start(script, null, true))
                {
                    try
                    {
                        Task<string> output = child.StandardOutput.ReadToEndAsync();
                        Task<string> error = child.StandardError.ReadToEndAsync();
                        Require(WindowsPowerShell.SendScriptAsync(child, script).Wait(15000) && child.WaitForExit(15000),
                            "The harmless fake provider child timed out.");
                        string stderr = error.GetAwaiter().GetResult();
                        Require(child.ExitCode == 0 && stderr == (expectSetter ? marker : ""), "Unexpected fake PowerShell outcome: " + stderr);
                        return new WindowsSecurityBackend.ProcessOutput { ExitCode = child.ExitCode, Output = output.GetAwaiter().GetResult(), Errors = "" };
                    }
                    finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(); } }
                }
            }, "Core", 0);
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

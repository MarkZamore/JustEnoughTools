using System;
using System.Collections.Generic;
using JustEnoughTools.Protection;

namespace JustEnoughTools.Tests
{
    // Every context and write/evidence provider here is in-memory. No host query,
    // policy mutation, elevation, service operation or reboot is performed.
    public static class ConfigurationTests
    {
        public static int RunAll()
        {
            int checks = 0;
            var baseline = Context();
            var publicCheck = SupportedConfiguration.Check(baseline);
            Require(!publicCheck.CanApply && publicCheck.Status == ConfigurationStatus.Unverified &&
                publicCheck.Message.Contains("Профиль применения не проверен"), "Production unexpectedly ships a verified application profile."); checks++;
            var home = Context(); home.Windows.Edition = Text("Core");
            Require(SupportedConfiguration.Check(home).Status == ConfigurationStatus.Unsupported, "Home was not explicitly unsupported."); checks++;
            Require(SupportedConfiguration.Check(null).Status == ConfigurationStatus.Unverified, "Missing context became eligible."); checks++;
            var unknownEdition = Context(); unknownEdition.Windows.Edition = ContextValue<string>.Unknown("fake", "denied");
            Require(SupportedConfiguration.Check(unknownEdition).Status == ConfigurationStatus.Unverified, "Unknown edition became Home/off."); checks++;
            var x86 = Context(); x86.Windows.Architecture = Text("x86");
            Require(SupportedConfiguration.Check(x86).Status == ConfigurationStatus.Unsupported, "Unsupported architecture accepted."); checks++;
            var oldBuild = Context(); oldBuild.Windows.Build = Text("19045.5000");
            Require(SupportedConfiguration.Check(oldBuild).Status == ConfigurationStatus.Unverified, "Documented Windows10 build was called unsupported."); checks++;
            var unsupportedBuild = Context(); unsupportedBuild.Windows.Build = Text("19041.1000");
            Require(SupportedConfiguration.Check(unsupportedBuild).Status == ConfigurationStatus.Unsupported, "Build before documented policy applicability accepted."); checks++;
            var missingUbr = Context(); missingUbr.Windows.Build = Text("26100");
            Require(SupportedConfiguration.Check(missingUbr).Status == ConfigurationStatus.Unverified, "Build without UBR accepted."); checks++;
            var missingPlatform = Context(); missingPlatform.Defender.PlatformVersion = ContextValue<string>.Unknown("fake", "missing");
            Require(SupportedConfiguration.Check(missingPlatform).Status == ConfigurationStatus.Unverified, "Unknown platform accepted."); checks++;

            foreach (string blocker in new[] { "tamper", "mde", "sac-policy", "sac-runtime", "sac-evaluation" })
            {
                var context = Context();
                if (blocker == "tamper") context.Defender.TamperProtectionEnabled = Bool(true);
                if (blocker == "mde") context.Mde.OnboardingState = ContextValue<MdeOnboardingState?>.Known(MdeOnboardingState.Onboarded, "fake");
                if (blocker == "sac-policy") context.SmartAppControl.PolicyState = ContextValue<SmartAppControlPolicyState?>.Known(SmartAppControlPolicyState.Enforcement, "fake");
                if (blocker == "sac-runtime") context.SmartAppControl.ReportedState = Text("On");
                if (blocker == "sac-evaluation") context.SmartAppControl.ReportedState = Text("Evaluation");
                var fake = new Backend { Context = context };
                var result = SupportedConfiguration.ExecuteCandidate(fake, Profile(), null);
                Require(result.Status == ConfigurationStatus.Blocked && fake.Writes == 0 && !result.PerformedChanges, "Known blocker was ignored: " + blocker); checks++;
            }
            foreach (string unknown in new[] { "tamper", "mde", "sac-policy", "sac-runtime", "sac-unrecognized" })
            {
                var context = Context();
                if (unknown == "tamper") context.Defender.TamperProtectionEnabled = ContextValue<bool?>.Unknown("fake", "denied");
                if (unknown == "mde") context.Mde.OnboardingState = ContextValue<MdeOnboardingState?>.Unknown("fake", "absent");
                if (unknown == "sac-policy") context.SmartAppControl.PolicyState = ContextValue<SmartAppControlPolicyState?>.Unknown("fake", "absent");
                if (unknown == "sac-runtime") context.SmartAppControl.ReportedState = ContextValue<string>.Unknown("fake", "unavailable");
                if (unknown == "sac-unrecognized") context.SmartAppControl.ReportedState = Text("not-an-off-state");
                var fake = new Backend { Context = context };
                var result = SupportedConfiguration.ExecuteCandidate(fake, Profile(), null);
                Require(result.Status == ConfigurationStatus.Unverified && fake.Writes == 0, "Unknown became off: " + unknown); checks++;
            }
            foreach (string mismatch in new[] { "edition", "ubr", "platform", "architecture", "unverified-profile" })
            {
                var profile = Profile();
                if (mismatch == "edition") profile.Edition = "Enterprise";
                if (mismatch == "ubr") profile.Build = "26100.9999";
                if (mismatch == "platform") profile.PlatformVersion = "4.18.99999.1";
                if (mismatch == "architecture") profile.Architecture = "arm64";
                if (mismatch == "unverified-profile") profile.VmApplicationVerified = false;
                var fake = new Backend();
                Require(SupportedConfiguration.ExecuteCandidate(fake, profile, null).Status == ConfigurationStatus.Unverified && fake.Writes == 0,
                    "Mismatched profile became applicable: " + mismatch); checks++;
            }

            var parentCheck = SupportedConfiguration.Check(Context(), Profile());
            var changedBeforeWorker = new Backend(); changedBeforeWorker.Context.Defender.TamperProtectionEnabled = Bool(true);
            Require(parentCheck.CanApply && SupportedConfiguration.ExecuteCandidate(changedBeforeWorker, Profile(), null).Status == ConfigurationStatus.Blocked && changedBeforeWorker.Writes == 0,
                "Worker relied on stale parent preflight."); checks++;
            foreach (string ignored in new[] { "policy-readback-false", "policy-readback-unknown", "engine-on", "engine-unknown", "engine-no-source", "write-rejected" })
            {
                var fake = new Backend();
                if (ignored == "policy-readback-false") fake.EnginePolicy = false;
                if (ignored == "policy-readback-unknown") fake.EnginePolicy = null;
                if (ignored == "engine-on") fake.Engine.Confirmed = false;
                if (ignored == "engine-unknown") fake.Engine.Confirmed = null;
                if (ignored == "engine-no-source") fake.Engine.Source = null;
                if (ignored == "write-rejected") fake.EngineWrite.Accepted = false;
                var result = SupportedConfiguration.ExecuteCandidate(fake, Profile(), null);
                Require(!result.Succeeded && !result.EngineOffVerified && fake.UiWrites == 0,
                    "Unverified engine caused UI hiding: " + ignored); checks++;
            }
            foreach (string uiFailure in new[] { "write-rejected", "readback-false", "readback-unknown", "runtime-false", "runtime-unknown", "runtime-no-source" })
            {
                var fake = new Backend();
                if (uiFailure == "write-rejected") fake.UiWrite.Accepted = false;
                if (uiFailure == "readback-false") fake.UiPolicy = false;
                if (uiFailure == "readback-unknown") fake.UiPolicy = null;
                if (uiFailure == "runtime-false") fake.Ui.Confirmed = false;
                if (uiFailure == "runtime-unknown") fake.Ui.Confirmed = null;
                if (uiFailure == "runtime-no-source") fake.Ui.Source = "";
                var result = SupportedConfiguration.ExecuteCandidate(fake, Profile(), null);
                Require(!result.Succeeded && result.EngineOffVerified && !result.VirusUiHiddenVerified && fake.PersistenceReads == 0,
                    "UI failure became completed configuration: " + uiFailure); checks++;
            }
            var pending = SupportedConfiguration.ExecuteCandidate(new Backend(), Profile(), null);
            Require(pending.Status == ConfigurationStatus.Pending && pending.PerformedChanges && pending.EngineOffVerified && pending.VirusUiHiddenVerified && !pending.Succeeded,
                "Same-boot observations became persistent success."); checks++;
            foreach (string missing in new[] { "profile", "reboot", "refresh", "engine", "ui", "source" })
            {
                var fake = new Backend(); fake.Persistence = Persistent(Profile());
                if (missing == "profile") fake.Persistence.ProfileKey = "other-profile";
                if (missing == "reboot") fake.Persistence.RebootObserved = false;
                if (missing == "refresh") fake.Persistence.PolicyRefreshObserved = false;
                if (missing == "engine") fake.Persistence.EngineOffConfirmed = null;
                if (missing == "ui") fake.Persistence.VirusUiHiddenConfirmed = false;
                if (missing == "source") fake.Persistence.Source = null;
                Require(SupportedConfiguration.ExecuteCandidate(fake, Profile(), null).Status == ConfigurationStatus.Pending,
                    "Incomplete persistence evidence accepted: " + missing); checks++;
            }
            var complete = new Backend(); complete.Persistence = Persistent(Profile());
            var verified = SupportedConfiguration.ExecuteCandidate(complete, Profile(), null);
            Require(verified.Succeeded && verified.Status == ConfigurationStatus.Verified && verified.PersistenceVerified &&
                string.Join(",", complete.Calls) == "context,engine-write,engine-read,engine-runtime,ui-write,ui-read,ui-runtime,persistence",
                "Verified fake evidence did not complete in the required order."); checks++;
            foreach (string stage in new[] { "context", "engine-write", "engine-read", "engine-runtime", "ui-write", "ui-read", "ui-runtime", "persistence" })
            {
                var fake = new Backend { FailAt = stage };
                var result = SupportedConfiguration.ExecuteCandidate(fake, Profile(), null);
                Require(result.Status == ConfigurationStatus.Unverified && !result.Succeeded &&
                    result.ChangesMayHaveOccurred == (stage == "engine-write" || stage == "ui-write"), "Exception lost partial-change semantics: " + stage); checks++;
            }
            Require(SupportedConfiguration.DefenderPolicyKey == @"SOFTWARE\Policies\Microsoft\Windows Defender" &&
                SupportedConfiguration.DefenderPolicyValue == "DisableAntiSpyware" && SupportedConfiguration.VirusUiPolicyValue == "UILockdown" &&
                SupportedConfiguration.VirusUiPolicyKey.EndsWith(@"\Virus and threat protection", StringComparison.Ordinal),
                "Candidate policy mapping changed."); checks++;
            return checks;
        }

        private static ContextValue<string> Text(string value) { return ContextValue<string>.Known(value, "fake"); }
        private static ContextValue<bool?> Bool(bool value) { return ContextValue<bool?>.Known(value, "fake"); }
        private static SupportedProtectionSnapshot Context()
        {
            var context = SupportedProtectionContext.CreateUnknownSnapshot("fake only");
            context.Windows.Edition = Text("Professional"); context.Windows.Build = Text("26100.1000"); context.Windows.Architecture = Text("x64");
            context.Defender.PlatformVersion = Text("4.18.26090.1"); context.Defender.TamperProtectionEnabled = Bool(false);
            context.Mde.OnboardingState = ContextValue<MdeOnboardingState?>.Known(MdeOnboardingState.NotOnboarded, "fake");
            context.SmartAppControl.PolicyState = ContextValue<SmartAppControlPolicyState?>.Known(SmartAppControlPolicyState.Off, "fake");
            context.SmartAppControl.ReportedState = Text("Off"); return context;
        }
        private static SupportedConfiguration.CandidateProfile Profile()
        {
            return new SupportedConfiguration.CandidateProfile { Edition = "Professional", Build = "26100.1000", PlatformVersion = "4.18.26090.1", Architecture = "x64", VmApplicationVerified = true };
        }
        private static SupportedConfiguration.PersistenceEvidence Persistent(SupportedConfiguration.CandidateProfile profile)
        {
            return new SupportedConfiguration.PersistenceEvidence { ProfileKey = profile.Key, Source = "fake post-reboot and policy-refresh evidence", RebootObserved = true,
                PolicyRefreshObserved = true, EngineOffConfirmed = true, VirusUiHiddenConfirmed = true };
        }
        private sealed class Backend : SupportedConfiguration.ICandidateBackend
        {
            internal SupportedProtectionSnapshot Context = ConfigurationTests.Context();
            internal string FailAt;
            internal int Writes, UiWrites, PersistenceReads;
            internal readonly List<string> Calls = new List<string>();
            internal SupportedConfiguration.PolicyWriteResult EngineWrite = new SupportedConfiguration.PolicyWriteResult { Accepted = true, Changed = true };
            internal SupportedConfiguration.PolicyWriteResult UiWrite = new SupportedConfiguration.PolicyWriteResult { Accepted = true, Changed = true };
            internal bool? EnginePolicy = true, UiPolicy = true;
            internal SupportedConfiguration.RuntimeEvidence Engine = new SupportedConfiguration.RuntimeEvidence { Confirmed = true, Source = "fake independently validated runtime" };
            internal SupportedConfiguration.RuntimeEvidence Ui = new SupportedConfiguration.RuntimeEvidence { Confirmed = true, Source = "fake observed virus section" };
            internal SupportedConfiguration.PersistenceEvidence Persistence;
            private void Step(string stage) { Calls.Add(stage); if (FailAt == stage) throw new InvalidOperationException("Fake failure: " + stage); }
            public SupportedProtectionSnapshot ReadContext() { Step("context"); return Context; }
            public SupportedConfiguration.PolicyWriteResult ApplyDefenderOffPolicy() { Writes++; Step("engine-write"); return EngineWrite; }
            public bool? ReadDefenderOffPolicy() { Step("engine-read"); return EnginePolicy; }
            public SupportedConfiguration.RuntimeEvidence VerifyEngineOff() { Step("engine-runtime"); return Engine; }
            public SupportedConfiguration.PolicyWriteResult ApplyVirusUiHidePolicy() { Writes++; UiWrites++; Step("ui-write"); return UiWrite; }
            public bool? ReadVirusUiHidePolicy() { Step("ui-read"); return UiPolicy; }
            public SupportedConfiguration.RuntimeEvidence VerifyVirusUiHidden() { Step("ui-runtime"); return Ui; }
            public SupportedConfiguration.PersistenceEvidence VerifyPersistence() { PersistenceReads++; Step("persistence"); return Persistence; }
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}

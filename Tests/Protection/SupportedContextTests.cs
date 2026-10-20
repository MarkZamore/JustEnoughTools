using System;
using JustEnoughTools.Protection;

namespace JustEnoughTools.Tests
{
    public static class SupportedContextTests
    {
        public static int RunAll()
        {
            int passed = 0;
            var missing = SupportedProtectionContext.CreateUnknownSnapshot("Synthetic unavailable provider.");
            Require(!missing.Defender.AntivirusEnabled.IsKnown && missing.Defender.AntivirusEnabled.Value == null, "Unknown antivirus status became false."); passed++;
            Require(!missing.Defender.TamperProtectionEnabled.IsKnown && missing.Defender.TamperProtectionEnabled.Value == null, "Unknown tamper state became false."); passed++;
            Require(!missing.Mde.OnboardingState.IsKnown && missing.Mde.OnboardingState.Value == null, "Unknown onboarding became not onboarded."); passed++;
            Require(!missing.SmartAppControl.PolicyState.IsKnown && missing.SmartAppControl.PolicyState.Value == null, "Unknown SAC state became Off."); passed++;
            Require(!missing.AntivirusProducts.IsKnown && missing.AntivirusProducts.Value == null, "Failed AV enumeration became an empty product list."); passed++;
            Require(!missing.ThirdPartyAntivirusConfirmed.IsKnown && missing.ThirdPartyAntivirusConfirmed.Value == null, "Unknown vendor identity became trusted third-party registration."); passed++;

            var falseValue = SupportedProtectionContext.ParseBoolean(false, "fixture");
            Require(falseValue.IsKnown && falseValue.Value == false, "A real false status must remain distinct from unknown."); passed++;
            foreach (var value in new object[] { null, "False", 0, 1, DBNull.Value })
            { Require(!SupportedProtectionContext.ParseBoolean(value, "fixture").IsKnown, "An unexpected boolean type was coerced."); passed++; }
            foreach (var value in new object[] { null, "0", 3, -1, uint.MaxValue })
            { Require(!SupportedProtectionContext.ParseEnum<MdeOnboardingState>(value, "fixture").IsKnown, "Invalid MDE registry data became known state."); passed++; }
            Require(SupportedProtectionContext.ParseEnum<MdeOnboardingState>(0, "fixture").Value == MdeOnboardingState.NotOnboarded, "Known onboarding zero was not preserved."); passed++;
            Require(SupportedProtectionContext.ParseEnum<SmartAppControlPolicyState>(2, "fixture").Value == SmartAppControlPolicyState.Evaluation, "SAC evaluation mode was misread."); passed++;
            Require(!SupportedProtectionContext.ParseWscHealth(1, 2).IsKnown, "WSC S_FALSE must not claim valid poor health."); passed++;
            Require(!SupportedProtectionContext.ParseWscHealth(unchecked((int)0x80070005), 0).IsKnown, "A failed WSC query must not claim good health."); passed++;
            Require(!SupportedProtectionContext.ParseWscHealth(0, 99).IsKnown, "Undefined WSC health became known."); passed++;
            Require(SupportedProtectionContext.ParseWscHealth(0, 0).Value == WscSecurityProviderHealth.Good, "Successful WSC good health was lost."); passed++;
            var on = SupportedProtectionContext.ParseEnum<WscAntivirusProductState>(0, "fixture");
            var off = SupportedProtectionContext.ParseEnum<WscAntivirusProductState>(1, "fixture");
            var current = SupportedProtectionContext.ParseEnum<WscAntivirusSignatureStatus>(1, "fixture");
            var stale = SupportedProtectionContext.ParseEnum<WscAntivirusSignatureStatus>(0, "fixture");
            var unknown = SupportedProtectionContext.ParseEnum<WscAntivirusSignatureStatus>(null, "fixture");
            Require(SupportedProtectionContext.EvaluateProductHealth(on, current).Value == true, "Known healthy registration was lost."); passed++;
            Require(SupportedProtectionContext.EvaluateProductHealth(off, current).Value == false, "A registered but off product was treated as healthy."); passed++;
            Require(SupportedProtectionContext.EvaluateProductHealth(on, stale).Value == false, "A stale product was treated as healthy."); passed++;
            Require(!SupportedProtectionContext.EvaluateProductHealth(on, unknown).IsKnown, "Missing signature status was treated as a healthy registration."); passed++;

            // The only real-system operation in this suite is the read-only Inspect.
            // It never invokes a setting method, registration method, elevation or removal.
            var actual = SupportedProtectionContext.Inspect();
            Require(actual.Windows != null && actual.Defender != null && actual.Mde != null && actual.SmartAppControl != null, "Real inventory omitted a status category."); passed++;
            Require(actual.AntivirusHealth != null && actual.AntivirusProducts != null && !actual.ThirdPartyAntivirusConfirmed.IsKnown, "Reported WSC data was mistaken for vendor/scanner attestation."); passed++;
            if (actual.AntivirusProducts.IsKnown)
                foreach (var item in actual.AntivirusProducts.Value)
                    Require(item.Name != null && item.State != null && item.SignatureStatus != null && item.ReportedHealthy != null, "A registered product omitted query evidence.");
            return passed;
        }

        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}

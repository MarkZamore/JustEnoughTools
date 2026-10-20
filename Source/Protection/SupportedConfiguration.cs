using System;
using System.Collections.Generic;
using System.Security;
using System.Threading.Tasks;

namespace JustEnoughTools.Protection
{
    public enum ConfigurationStatus { Unsupported = 0, Blocked = 1, Unverified = 2, Pending = 3, Verified = 4 }

    public sealed class ConfigurationResult
    {
        public ConfigurationStatus Status { get; internal set; }
        public bool CanApply { get; internal set; }
        public bool PerformedChanges { get; internal set; }
        public bool ChangesMayHaveOccurred { get; internal set; }
        public bool EngineOffVerified { get; internal set; }
        public bool VirusUiHiddenVerified { get; internal set; }
        public bool PersistenceVerified { get; internal set; }
        public string Message { get; internal set; }
        public bool Succeeded
        {
            get { return Status == ConfigurationStatus.Verified && EngineOffVerified && VirusUiHiddenVerified && PersistenceVerified; }
        }
    }

    public sealed class ConfigurationException : InvalidOperationException
    {
        public ConfigurationResult Result { get; private set; }
        internal ConfigurationException(ConfigurationResult result)
            : base(result == null ? "Результат настройки не получен." : result.Message) { Result = result; }
    }

    /// <summary>
    /// Compatibility assessment only in the shipped application. No verified
    /// application profiles or live policy writer are included in this release.
    /// The injected candidate below is exercised exclusively with fake backends.
    /// </summary>
    public static class SupportedConfiguration
    {
        // ADMX applicability is not a guarantee that this legacy policy works.
        // Microsoft recommends leaving it unconfigured and documents TP/MDE
        // limitations. A DWORD readback never establishes engine shutdown.
        // https://learn.microsoft.com/windows/client-management/mdm/policy-csp-admx-microsoftdefenderantivirus#disableantispywaredefender
        // https://learn.microsoft.com/windows-hardware/customize/desktop/unattend/security-malware-windows-defender-disableantispyware
        internal const string DefenderPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows Defender";
        internal const string DefenderPolicyValue = "DisableAntiSpyware";
        internal const string VirusUiPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows Defender Security Center\Virus and threat protection";
        internal const string VirusUiPolicyValue = "UILockdown";

        private static readonly HashSet<string> SupportedEditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Professional", "ProfessionalN", "ProfessionalEducation", "ProfessionalEducationN",
            "ProfessionalWorkstation", "ProfessionalWorkstationN", "Enterprise", "EnterpriseN", "Education", "EducationN"
        };

        public static ConfigurationResult Check(SupportedProtectionSnapshot context) { return Check(context, null); }

        public static Task<ConfigurationResult> ConfigureAsync(IProgress<string> progress)
        {
            // Deliberately no live writer: every shipped profile remains
            // unavailable until separate VM application/reboot tests exist.
            var result = Check(SupportedProtectionContext.Inspect());
            if (progress != null) progress.Report(result.Message);
            return Task.FromResult(result);
        }

        internal static ConfigurationResult Check(SupportedProtectionSnapshot context, CandidateProfile profile)
        {
            if (context == null || context.Windows == null)
                return Result(ConfigurationStatus.Unverified, "Не удалось определить редакцию и сборку Windows. Изменения не выполнялись.");
            string edition = Text(context.Windows.Edition);
            if (edition == null) return Result(ConfigurationStatus.Unverified, "Редакция Windows неизвестна. Изменения не выполнялись.");
            if (edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase))
                return Result(ConfigurationStatus.Unsupported, "Windows Home (" + edition + ")" +
                    (Text(context.Windows.Build) == null ? "" : ", сборка " + Text(context.Windows.Build)) +
                    ": редакция отсутствует в документированном списке применимости политик отключения Defender и скрытия раздела. Изменения не выполнялись.");
            if (!SupportedEditions.Contains(edition))
                return Result(ConfigurationStatus.Unverified, "Профиль применения не проверен для редакции Windows " + edition + ". Изменения не выполнялись.");
            string architecture = Text(context.Windows.Architecture);
            if (architecture == null) return Result(ConfigurationStatus.Unverified, "Архитектура Windows неизвестна. Изменения не выполнялись.");
            if (!string.Equals(architecture, "x64", StringComparison.OrdinalIgnoreCase))
                return Result(ConfigurationStatus.Unsupported, "Поддерживается только Windows 11 x64. Изменения не выполнялись.");
            Version build;
            string buildText = Text(context.Windows.Build);
            if (buildText == null || !Version.TryParse(buildText, out build) || build.Build != -1 || build.Minor < 0)
                return Result(ConfigurationStatus.Unverified, "Полный номер сборки Windows и UBR неизвестны. Изменения не выполнялись.");
            if (build.Major < 19041 || (build.Major <= 19043 && build.Minor < 1202))
                return Result(ConfigurationStatus.Unsupported, "Сборка Windows " + buildText + " предшествует документированной применимости политики. Изменения не выполнялись.");
            if (build.Major < 22000)
                return Result(ConfigurationStatus.Unverified, "Профиль применения не проверен для сборки Windows " + buildText + ". Приложение предназначено для Windows 11 x64. Изменения не выполнялись.");

            if (context.Defender == null || context.Mde == null || context.SmartAppControl == null)
                return Result(ConfigurationStatus.Unverified, "Не удалось проверить Tamper Protection, MDE и Smart App Control. Изменения не выполнялись.");
            bool? tamper = Boolean(context.Defender.TamperProtectionEnabled);
            MdeOnboardingState? mde = Known(context.Mde.OnboardingState);
            SmartAppControlPolicyState? sacPolicy = Known(context.SmartAppControl.PolicyState);
            string sacRuntime = Text(context.SmartAppControl.ReportedState);
            if (tamper == true) return Result(ConfigurationStatus.Blocked, "Включена Tamper Protection: политика не применяется. Приложение не отключает и не обходит её. Изменения не выполнялись.");
            if (mde == MdeOnboardingState.Onboarded)
                return Result(ConfigurationStatus.Blocked, "Устройство подключено к Microsoft Defender for Endpoint: DisableAntiSpyware может игнорироваться. Изменения не выполнялись.");
            if ((sacPolicy.HasValue && sacPolicy != SmartAppControlPolicyState.Off) ||
                string.Equals(sacRuntime, "On", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sacRuntime, "Evaluation", StringComparison.OrdinalIgnoreCase))
                return Result(ConfigurationStatus.Blocked, "Smart App Control включён или оценивает приложения. Его состояние не изменяется. Изменения не выполнялись.");
            if (tamper != false || mde != MdeOnboardingState.NotOnboarded || sacPolicy != SmartAppControlPolicyState.Off ||
                !string.Equals(sacRuntime, "Off", StringComparison.OrdinalIgnoreCase))
                return Result(ConfigurationStatus.Unverified, "Состояние Tamper Protection, MDE или Smart App Control неизвестно либо противоречиво. Изменения не выполнялись.");
            Version platform;
            string platformText = Text(context.Defender.PlatformVersion);
            if (platformText == null || !Version.TryParse(platformText, out platform) || platform.Revision < 0)
                return Result(ConfigurationStatus.Unverified, "Версия платформы Defender неизвестна. Legacy-политику нельзя считать применимой. Изменения не выполнялись.");
            if (profile == null || !profile.Matches(context))
                return Result(ConfigurationStatus.Unverified, "Профиль применения не проверен для этой редакции, сборки Windows и платформы Defender. Документация предупреждает о неподдерживаемом поведении и игнорировании legacy-политики. Изменения не выполнялись.");
            var ready = Result(ConfigurationStatus.Unverified, "Проверенный профиль совпал; применение ещё не выполнялось.");
            ready.CanApply = true;
            return ready;
        }

        // There is intentionally no implementation that writes to the host.
        // Fixed operations prohibit arbitrary registry paths/commands and do not
        // include services, drivers, protection bypasses or global notifications.
        internal interface ICandidateBackend
        {
            SupportedProtectionSnapshot ReadContext();
            PolicyWriteResult ApplyDefenderOffPolicy();
            bool? ReadDefenderOffPolicy();
            RuntimeEvidence VerifyEngineOff();
            PolicyWriteResult ApplyVirusUiHidePolicy();
            bool? ReadVirusUiHidePolicy();
            RuntimeEvidence VerifyVirusUiHidden();
            PersistenceEvidence VerifyPersistence();
        }

        internal sealed class CandidateProfile
        {
            internal string Edition, Build, PlatformVersion, Architecture;
            internal bool VmApplicationVerified;
            internal string Key { get { return Edition + "|" + Build + "|" + PlatformVersion + "|" + Architecture; } }
            internal bool Matches(SupportedProtectionSnapshot context)
            {
                return VmApplicationVerified && string.Equals(Edition, Text(context.Windows.Edition), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Build, Text(context.Windows.Build), StringComparison.Ordinal) &&
                    string.Equals(PlatformVersion, Text(context.Defender.PlatformVersion), StringComparison.Ordinal) &&
                    string.Equals(Architecture, Text(context.Windows.Architecture), StringComparison.OrdinalIgnoreCase);
            }
        }

        internal sealed class PolicyWriteResult { internal bool Accepted, Changed; internal string Message; }
        internal sealed class RuntimeEvidence { internal bool? Confirmed; internal string Source; }
        internal sealed class PersistenceEvidence
        {
            internal string ProfileKey, Source;
            internal bool RebootObserved, PolicyRefreshObserved;
            internal bool? EngineOffConfirmed, VirusUiHiddenConfirmed;
        }

        internal static ConfigurationResult ExecuteCandidate(ICandidateBackend backend, CandidateProfile profile, IProgress<string> progress)
        {
            if (backend == null) throw new ArgumentNullException(nameof(backend));
            ConfigurationResult result;
            try { result = Check(backend.ReadContext(), profile); }
            catch (Exception error) when (IsExpectedFailure(error))
            { return Result(ConfigurationStatus.Unverified, "Не удалось повторно проверить состояние перед настройкой. " + error.Message); }
            if (!result.CanApply) return result;
            result.CanApply = false;
            bool writing = false;
            try
            {
                Report(progress, "Проверка применения документированной политики Defender…");
                writing = true;
                var engineWrite = backend.ApplyDefenderOffPolicy();
                writing = false;
                if (engineWrite == null) { result.ChangesMayHaveOccurred = true; return Incomplete(result, ConfigurationStatus.Unverified, "Нет результата записи политики. Скрытие интерфейса не выполнялось."); }
                result.PerformedChanges |= engineWrite.Changed;
                if (!engineWrite.Accepted) return Incomplete(result, ConfigurationStatus.Blocked, "Политика Defender отклонена. Скрытие интерфейса не выполнялось. " + engineWrite.Message);
                if (backend.ReadDefenderOffPolicy() != true)
                    return Incomplete(result, ConfigurationStatus.Unverified, "Запись политики Defender не подтверждена. Скрытие интерфейса не выполнялось.");
                var engine = backend.VerifyEngineOff();
                if (!Confirmed(engine))
                    return Incomplete(result, engine != null && engine.Confirmed == false ? ConfigurationStatus.Blocked : ConfigurationStatus.Unverified,
                        "Отключение движка Defender не подтверждено независимой проверкой. Значение политики не доказывает отключение. Скрытие интерфейса не выполнялось.");
                result.EngineOffVerified = true;

                Report(progress, "Движок проверен; проверка скрытия раздела «Защита от вирусов и угроз»…");
                writing = true;
                var uiWrite = backend.ApplyVirusUiHidePolicy();
                writing = false;
                if (uiWrite == null) { result.ChangesMayHaveOccurred = true; return Incomplete(result, ConfigurationStatus.Unverified, "Нет результата записи политики интерфейса."); }
                result.PerformedChanges |= uiWrite.Changed;
                if (!uiWrite.Accepted) return Incomplete(result, ConfigurationStatus.Blocked, "Политика скрытия раздела отклонена. " + uiWrite.Message);
                if (backend.ReadVirusUiHidePolicy() != true || !Confirmed(backend.VerifyVirusUiHidden()))
                    return Incomplete(result, ConfigurationStatus.Unverified, "Скрытие раздела не подтверждено. Отключение движка и скрытие интерфейса проверяются отдельно.");
                result.VirusUiHiddenVerified = true;
                var persistence = backend.VerifyPersistence();
                result.PersistenceVerified = persistence != null && persistence.ProfileKey == profile.Key &&
                    !string.IsNullOrWhiteSpace(persistence.Source) && persistence.RebootObserved && persistence.PolicyRefreshObserved &&
                    persistence.EngineOffConfirmed == true && persistence.VirusUiHiddenConfirmed == true;
                if (!result.PersistenceVerified)
                    return Incomplete(result, ConfigurationStatus.Pending, "Текущее состояние подтверждено. Устойчивость после перезагрузки и обновления политик не подтверждена; настройка не считается завершённой.");
                return Incomplete(result, ConfigurationStatus.Verified, "Отключение движка Defender и скрытие раздела подтверждены, включая перезагрузку и обновление политик. Антивирус и приложение «Безопасность Windows» не удалялись.");
            }
            catch (Exception error) when (IsExpectedFailure(error))
            {
                result.ChangesMayHaveOccurred |= writing;
                return Incomplete(result, ConfigurationStatus.Unverified, "Настройка не подтверждена; после начатой записи возможны частичные изменения. " + error.Message);
            }
        }

        private static bool Confirmed(RuntimeEvidence evidence) { return evidence != null && evidence.Confirmed == true && !string.IsNullOrWhiteSpace(evidence.Source); }
        private static void Report(IProgress<string> progress, string message) { if (progress != null) progress.Report(message); }
        private static bool IsExpectedFailure(Exception error)
        {
            return error is InvalidOperationException || error is UnauthorizedAccessException || error is SecurityException ||
                error is System.IO.IOException || error is System.ComponentModel.Win32Exception || error is TimeoutException;
        }
        private static string Text(ContextValue<string> value) { return value != null && value.IsKnown && !string.IsNullOrWhiteSpace(value.Value) ? value.Value : null; }
        private static bool? Boolean(ContextValue<bool?> value) { return value != null && value.IsKnown ? value.Value : null; }
        private static T? Known<T>(ContextValue<T?> value) where T : struct { return value != null && value.IsKnown ? value.Value : null; }
        private static ConfigurationResult Result(ConfigurationStatus status, string message) { return new ConfigurationResult { Status = status, Message = message }; }
        private static ConfigurationResult Incomplete(ConfigurationResult result, ConfigurationStatus status, string message) { result.Status = status; result.Message = message; return result; }
    }
}

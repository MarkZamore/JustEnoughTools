using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace JustEnoughTools.Security
{
    // This adapter is instantiated only after elevation and acquisition of the
    // action mutex. All commands are fixed source text, never user input.
    internal sealed class WindowsSecurityBackend : ISecurityBackend
    {
        private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        private readonly string edition;
        private readonly int build;

        internal WindowsSecurityBackend()
        {
            try
            {
                using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey key = machine.OpenSubKey(CurrentVersion, false))
                {
                    if (key != null)
                    {
                        edition = key.GetValue("EditionID") as string;
                        int.TryParse(Convert.ToString(key.GetValue("CurrentBuildNumber"), CultureInfo.InvariantCulture), out build);
                    }
                }
            }
            catch { /* A missing edition blocks only the policies which require it. */ }
        }

        public ActionResult Execute(SecurityAction action)
        {
            switch (action.Id)
            {
                case "defender.realtime": return Preference(action, "DisableRealtimeMonitoring", "$true", "True", true);
                case "defender.cloud": return Preference(action, "MAPSReporting", "0", "0", false);
                case "defender.samples": return Preference(action, "SubmitSamplesConsent", "2", "2", false);
                case "defender.pua": return InterpretPuaResult(Preference(action, "PUAProtection", "0", "0", false));
                case "defender.cfa": return Preference(action, "EnableControlledFolderAccess", "0", "0", false);
                case "defender.scheduled": return RunPowerShell(action, ScheduledScan);
                case "firewall.domain": return Firewall(action, "Domain");
                case "firewall.private": return Firewall(action, "Private");
                case "firewall.public": return Firewall(action, "Public");
                case "smartscreen.shell":
                    return WindowsPolicy(action, 15063, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen", 0);
                case "smartscreen.edge": return EdgePolicy(action, "SmartScreenEnabled", 77);
                case "smartscreen.edge-pua": return EdgePolicy(action, "SmartScreenPuaEnabled", 80);
                case "smartscreen.phishing":
                    return WindowsPolicy(action, 22621, @"SOFTWARE\Policies\Microsoft\Windows\WTDS\Components", "ServiceEnabled", 0);
                case "security.virus-ui":
                    return WindowsPolicy(action, 16299, @"SOFTWARE\Policies\Microsoft\Windows Defender Security Center\Virus and threat protection", "UILockdown", 1);
                default: throw new InvalidOperationException("Действие отсутствует в фиксированном каталоге.");
            }
        }

        public SecuritySnapshot Inspect()
        {
            ProbeSnapshot observed = null;
            string failure = null;
            try
            {
                ProcessOutput output = RunRawScript(SnapshotScript, true);
                if (output.ExitCode != 0) throw new InvalidOperationException("PowerShell: " + output.Errors);
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(output.Output.Trim().TrimStart('\uFEFF'))))
                    observed = (ProbeSnapshot)new DataContractJsonSerializer(typeof(ProbeSnapshot)).ReadObject(stream);
            }
            catch (Exception error) { failure = SecurityEngine.Explain(error); }

            var snapshot = new SecuritySnapshot();
            foreach (SecurityAction action in SecurityEngine.Catalogue)
            {
                ActionState state;
                switch (action.Id)
                {
                    case "defender.pua":
                        int? pua = observed == null ? null : observed.PuaProtection;
                        state = State(action, ClassifyPuaProtection(pua), PuaReason(pua) +
                            (pua.HasValue ? "" : " " + (failure ?? (observed == null ? "" : observed.PuaError))), true);
                        break;
                    case "smartscreen.shell": state = ReadPolicy(action, 15063, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen", 0, false); break;
                    case "smartscreen.phishing": state = ReadPolicy(action, 22621, @"SOFTWARE\Policies\Microsoft\Windows\WTDS\Components", "ServiceEnabled", 0, false); break;
                    case "security.virus-ui": state = ReadPolicy(action, 16299, @"SOFTWARE\Policies\Microsoft\Windows Defender Security Center\Virus and threat protection", "UILockdown", 1, false); break;
                    case "smartscreen.edge": state = ReadPolicy(action, 0, @"SOFTWARE\Policies\Microsoft\Edge", "SmartScreenEnabled", 0, true); break;
                    case "smartscreen.edge-pua": state = ReadPolicy(action, 0, @"SOFTWARE\Policies\Microsoft\Edge", "SmartScreenPuaEnabled", 0, true); break;
                    case "defender.cancel": state = State(action, ActionStateKind.Unavailable,
                        "Активное быстрое/полное сканирование не подтверждено однозначным документированным API. Без подтверждения запущенного сканирования отмена не выполняется.", false); break;
                    case "smartscreen.store": state = State(action, ActionStateKind.Unavailable,
                        "Документированный программный переключатель и однозначная проверка общего состояния SmartScreen приложений Store не подтверждены.", false); break;
                    default:
                        state = observed == null || observed.Actions == null ? null : observed.Actions.Find(item => item != null && item.Id == action.Id);
                        if (state == null || !Enum.IsDefined(typeof(ActionStateKind), state.State))
                            state = State(action, ActionStateKind.Unknown, failure ?? "Провайдер не вернул состояние этого действия.", true);
                        break;
                }
                state.Title = action.Title;
                state.Reason = SecurityEngine.Limit(state.Reason);
                snapshot.Actions.Add(state);
            }
            return snapshot;
        }

        // PUA blocking is distinct from detection/audit. Microsoft's documented
        // AuditMode (2) detects and logs PUA but takes no blocking action:
        // https://learn.microsoft.com/defender-endpoint/detect-block-potentially-unwanted-apps-microsoft-defender-antivirus
        internal static ActionStateKind ClassifyPuaProtection(int? value)
        {
            if (value == 0 || value == 2) return ActionStateKind.AlreadyDesired;
            return value == 1 ? ActionStateKind.NeedsChange : ActionStateKind.Unknown;
        }

        private static string PuaReason(int? value)
        {
            if (value == 0) return "PUAProtection=Disabled (0): блокировка нежелательных приложений выключена.";
            if (value == 2) return "PUAProtection=AuditMode (2): блокировка неактивна, аудит сохраняется.";
            if (value == 1) return "PUAProtection=Enabled (1): блокировка нежелательных приложений включена.";
            return "Значение PUAProtection не подтверждено: " + (value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "Unknown") + ". Изменение не выполняется.";
        }

        internal static ActionResult InterpretPuaResult(ActionResult result)
        {
            if (result == null || result.Id != "defender.pua") return result;
            int before, after;
            if (!int.TryParse(result.Before, out before) || !int.TryParse(result.After, out after)) return result;
            if (before == 1 && after == 2 && result.Outcome == ActionOutcome.Unverified)
            {
                result.Outcome = ActionOutcome.Applied;
                result.Reason = "Блокировка нежелательных приложений неактивна. Запрошен Disabled (0), Windows вернула AuditMode (2): аудит сохраняется; полное отключение обнаружения PUA не подтверждается.";
            }
            else if (after == 2 && result.Outcome == ActionOutcome.AlreadySet)
                result.Reason = "Блокировка уже неактивна: AuditMode (2), аудит сохраняется. Повторная запись не выполнялась.";
            else if (after == 0 && (result.Outcome == ActionOutcome.Applied || result.Outcome == ActionOutcome.AlreadySet))
                result.Reason = "Подтверждён PUAProtection=Disabled (0): блокировка нежелательных приложений выключена.";
            return result;
        }

        private ActionState ReadPolicy(SecurityAction action, int minimumBuild, string path, string name, int desired, bool edge)
        {
            if (!SupportsWindowsPolicies(edition)) return State(action, ActionStateKind.Unavailable,
                "Редакция " + (edition ?? "не определена") + " не входит в подтверждённую применимость политики Microsoft.", false);
            if (build < minimumBuild) return State(action, ActionStateKind.Unavailable,
                "Сборка Windows не соответствует применимости политики: " + build.ToString(CultureInfo.InvariantCulture) + ".", false);
            try
            {
                if (edge)
                {
                    string browser = FindEdge();
                    if (browser == null) return State(action, ActionStateKind.Unavailable, "Установленный Microsoft Edge не найден.", false);
                    int major;
                    string version = FileVersionInfo.GetVersionInfo(browser).FileVersion;
                    int minimum = action.Id == "smartscreen.edge-pua" ? 80 : 77;
                    if (String.IsNullOrEmpty(version) || !int.TryParse(version.Split('.')[0], out major) || major < minimum)
                        return State(action, ActionStateKind.Unavailable, "Версия Edge не соответствует политике: " + version + ".", false);
                    bool? domain = DomainJoined();
                    bool? managed = MdmRegistered();
                    bool mdmEdition = edition.StartsWith("Professional", StringComparison.OrdinalIgnoreCase) || edition.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase);
                    if (domain != true && !(managed == true && mdmEdition)) return State(action, ActionStateKind.Unavailable,
                        "Управляемость для политики Edge не подтверждена: DomainJoined=" + Known(domain) + ", MDM=" + Known(managed) + ".", false);
                }
                using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey key = machine.OpenSubKey(path, false))
                {
                    object current = key == null ? null : key.GetValue(name);
                    if (current == null) return State(action, ActionStateKind.Unavailable,
                        "Явная политика отсутствует. Состояние пользовательского переключателя не подтверждено программным API; предположение о включённой защите не используется.", false);
                    int value;
                    if (key.GetValueKind(name) != RegistryValueKind.DWord || !int.TryParse(Convert.ToString(current, CultureInfo.InvariantCulture), out value) || (value != 0 && value != 1))
                        return State(action, ActionStateKind.Unknown, "Политика возвращает неизвестное значение; изменение не выполняется.", true);
                    return State(action, value == desired ? ActionStateKind.AlreadyDesired : ActionStateKind.NeedsChange,
                        value == desired ? "Документированная политика уже настроена. Это проверка конфигурации, а не доказательство состояния движка или окна." :
                        "Явное значение документированной политики отличается от запрошенного; требуется изменение конфигурации.", true);
                }
            }
            catch (Exception error) { return State(action, ActionStateKind.Unknown, SecurityEngine.Explain(error), true); }
        }

        private static ActionState State(SecurityAction action, ActionStateKind state, string reason, bool elevation)
        {
            return new ActionState { Id = action.Id, Title = action.Title, State = state, Reason = reason, RequiresElevation = elevation };
        }

        private static ActionResult Preference(SecurityAction action, string property, string value, string target, bool realtime)
        {
            return RunPowerShell(action, BuildPreferenceScript(property, value, target, realtime));
        }

        internal static string BuildPreferenceScript(string property, string value, string target, bool realtime)
        {
            string script = @"
$property = 'PROPERTY'
$target = 'TARGET'
$command = Get-Command Defender\Set-MpPreference -ErrorAction Stop
if (-not $command.Parameters.ContainsKey($property)) { Finish 3 ('Провайдер Defender не поддерживает параметр ' + $property + '.') }
$pref = Defender\Get-MpPreference -ErrorAction Stop
if ($null -eq $pref.PSObject.Properties[$property]) { Finish 3 ('Провайдер Defender не возвращает параметр ' + $property + '.') }
function PreferenceValue($obj) {
    $v = $obj.$property
    if ($v -is [bool]) { return [string]$v }
    return [string][int]$v
}
$r.Before = PreferenceValue $pref
$unchanged = $r.Before -eq $target
PRECHECK
if (-not $unchanged) {
    $parameters = @{ ErrorAction = 'Stop' }
    $parameters[$property] = VALUE
    Defender\Set-MpPreference @parameters | Out-Null
}
$r.After = PreferenceValue (Defender\Get-MpPreference -ErrorAction Stop)
if ($r.After -ne $target) { Finish 4 ('Windows не подтвердила значение ' + $property + ': ожидалось ' + $target + ', получено ' + $r.After + '.' + (TamperHint)) }
RUNTIME
if ($unchanged) { Finish 1 'Запрошенное состояние уже установлено; повторное чтение выполнено.' }
Finish 0 'Значение подтверждено повторным чтением. Windows может позднее изменить его.'
";
            string runtime = realtime ? @"
$runtime = Defender\Get-MpComputerStatus -ErrorAction Stop
for ($attempt = 0; $attempt -lt 5 -and $runtime.RealTimeProtectionEnabled -ne $false; $attempt++) {
    Start-Sleep -Milliseconds 300
    $runtime = Defender\Get-MpComputerStatus -ErrorAction Stop
}
if ($runtime.RealTimeProtectionEnabled -ne $false) {
    $r.After += '; RealTimeProtectionEnabled=' + [string]$runtime.RealTimeProtectionEnabled
    Finish 4 ('Предпочтение сохранено, но выключение защиты в реальном времени не подтверждено фактическим статусом.' + (TamperHint))
}
$r.After += '; RealTimeProtectionEnabled=False'
" : "";
            string precheck = realtime ? @"
$initialRuntime = Defender\Get-MpComputerStatus -ErrorAction Stop
if ($initialRuntime.RealTimeProtectionEnabled -eq $false) { Finish 1 'Фактическая защита RTP уже выключена; предпочтение не изменялось.' }
if ($initialRuntime.RealTimeProtectionEnabled -ne $true) { Finish 4 'Не удалось подтвердить включённое состояние RTP; изменение не выполнялось.' }
$unchanged = $false
" : property == "SubmitSamplesConsent" ? @"
if ([int]$pref.SubmitSamplesConsent -eq 0) { Finish 1 'Автоматическая отправка уже выключена (AlwaysPrompt); ручное подтверждение не изменялось.' }
" : property == "PUAProtection" ? @"
if ([int]$pref.PUAProtection -eq 2) {
    $r.After = $r.Before
    Finish 1 'PUAProtection=AuditMode (2): блокировка неактивна, аудит сохраняется. Повторная запись не выполнялась.'
}
if ([int]$pref.PUAProtection -ne 0 -and [int]$pref.PUAProtection -ne 1) { Finish 4 'Неизвестное значение PUAProtection; изменение не выполнялось.' }
" : "";
            return script.Replace("PROPERTY", property).Replace("TARGET", target)
                .Replace("VALUE", value).Replace("PRECHECK", precheck).Replace("RUNTIME", runtime);
        }

        private static ActionResult Firewall(SecurityAction action, string profile)
        {
            const string template = @"
$name = 'PROFILE'
$local = NetSecurity\Get-NetFirewallProfile -PolicyStore PersistentStore -Name $name -ErrorAction Stop
$effective = NetSecurity\Get-NetFirewallProfile -PolicyStore ActiveStore -Name $name -ErrorAction Stop
$r.Before = 'Local=' + [string]$local.Enabled + '; Effective=' + [string]$effective.Enabled
if ([int]$effective.Enabled -eq 0) { Finish 1 'Эффективный профиль уже выключен; локальная настройка не изменялась.' }
if ([int]$effective.Enabled -ne 1) { Finish 4 'Не подтверждено включённое состояние эффективного профиля; изменение не выполнялось.' }
NetSecurity\Set-NetFirewallProfile -PolicyStore PersistentStore -Profile $name -Enabled False -ErrorAction Stop | Out-Null
$local = NetSecurity\Get-NetFirewallProfile -PolicyStore PersistentStore -Name $name -ErrorAction Stop
$effective = NetSecurity\Get-NetFirewallProfile -PolicyStore ActiveStore -Name $name -ErrorAction Stop
$r.After = 'Local=' + [string]$local.Enabled + '; Effective=' + [string]$effective.Enabled
if ([int]$effective.Enabled -ne 0) {
    Finish 2 ('Эффективная политика брандмауэра остаётся включённой: ' + $r.After + '. Локальная запись не считается отключением.')
}
if ([int]$local.Enabled -ne 0) { Finish 4 ('Локальное сохранение настройки не подтверждено: ' + $r.After) }
Finish 0 'Локальная и эффективная настройки профиля подтверждены; службы и правила не изменялись.'
";
            return RunPowerShell(action, template.Replace("PROFILE", profile));
        }

        private ActionResult WindowsPolicy(SecurityAction action, int minimumBuild, string path, string name, int desired)
        {
            if (!SupportsWindowsPolicies(edition))
                return SecurityEngine.Result(action, ActionOutcome.Unavailable,
                    "Редакция " + (edition ?? "Windows не определена") + " отсутствует в подтверждённой применимости этой политики Microsoft. На Home корпоративная политика не используется как замена пользовательского переключателя.");
            if (build < minimumBuild)
                return SecurityEngine.Result(action, ActionOutcome.Unavailable,
                    "Для этой политики требуется сборка Windows не ниже " + minimumBuild.ToString(CultureInfo.InvariantCulture) + "; обнаружена " + build.ToString(CultureInfo.InvariantCulture) + ".");
            return WritePolicy(action, path, name, desired);
        }

        private ActionResult EdgePolicy(SecurityAction action, string name, int minimumVersion)
        {
            string edge = FindEdge();
            if (edge == null) return SecurityEngine.Result(action, ActionOutcome.Unavailable, "Установленный Microsoft Edge не найден в штатных каталогах.");
            int major;
            string version = FileVersionInfo.GetVersionInfo(edge).FileVersion;
            if (String.IsNullOrEmpty(version) || !int.TryParse(version.Split('.')[0], out major) || major < minimumVersion)
                return SecurityEngine.Result(action, ActionOutcome.Unavailable, "Версия Edge не соответствует применимости политики: " + (version ?? "неизвестна") + ".");
            bool? domain = DomainJoined();
            bool? management = MdmRegistered();
            bool eligibleMdmEdition = !String.IsNullOrEmpty(edition) &&
                (edition.StartsWith("Professional", StringComparison.OrdinalIgnoreCase) || edition.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase));
            if (domain != true && !(management == true && eligibleMdmEdition))
                return SecurityEngine.Result(action, ActionOutcome.Unavailable,
                    "Применимость политики Edge не подтверждена: требуется Active Directory либо поддерживаемая Pro/Enterprise с регистрацией MDM. DomainJoined=" + Known(domain) + ", MDM=" + Known(management) + ".");
            return WritePolicy(action, @"SOFTWARE\Policies\Microsoft\Edge", name, 0);
        }

        private static ActionResult WritePolicy(SecurityAction action, string path, string name, int desired)
        {
            // Registry paths are Microsoft's documented ADMX mappings. Readback
            // confirms configuration only, never effective engine/browser/UI state.
            string before = null, after = null;
            using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                using (RegistryKey key = machine.OpenSubKey(path, false))
                    before = key == null ? null : Convert.ToString(key.GetValue(name), CultureInfo.InvariantCulture);
                if (before == desired.ToString(CultureInfo.InvariantCulture))
                    return SecurityEngine.Result(action, ActionOutcome.AlreadySet,
                        "Политика уже настроена; повторная запись не выполнялась. Это проверка конфигурации, а не доказательство состояния компонента.", before, before);
                if (before != desired.ToString(CultureInfo.InvariantCulture))
                {
                    using (RegistryKey key = machine.CreateSubKey(path, true))
                    {
                        if (key == null) throw new UnauthorizedAccessException("Не удалось открыть документированный раздел политики.");
                        key.SetValue(name, desired, RegistryValueKind.DWord);
                        key.Flush();
                    }
                }
                using (RegistryKey key = machine.OpenSubKey(path, false))
                    after = key == null ? null : Convert.ToString(key.GetValue(name), CultureInfo.InvariantCulture);
            }
            if (after != desired.ToString(CultureInfo.InvariantCulture))
                return SecurityEngine.Result(action, ActionOutcome.Unverified, "Повторное чтение не подтвердило запрошенное значение политики.", before, after);
            return SecurityEngine.Result(action, ActionOutcome.Unverified,
                "Политика настроена и прочитана повторно. Фактическое применение к компоненту не подтверждено доступным программным API; интерфейс и браузер не открывались. Запись политики не считается доказательством отключения.", before, after);
        }

        internal static bool SupportsWindowsPolicies(string value)
        {
            return !String.IsNullOrEmpty(value) && new[]
            {
                "Professional", "ProfessionalN", "ProfessionalEducation", "ProfessionalEducationN",
                "ProfessionalWorkstation", "ProfessionalWorkstationN", "Enterprise", "EnterpriseN",
                "EnterpriseS", "EnterpriseSN", "Education", "EducationN", "IoTEnterprise", "IoTEnterpriseS"
            }.Contains(value, StringComparer.OrdinalIgnoreCase);
        }

        private static string FindEdge()
        {
            foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (String.IsNullOrEmpty(root)) continue;
                string candidate = Path.Combine(root, @"Microsoft\Edge\Application\msedge.exe");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static bool? DomainJoined()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"),
                    new ObjectQuery("SELECT PartOfDomain FROM Win32_ComputerSystem"), new EnumerationOptions { Timeout = TimeSpan.FromSeconds(10) }))
                using (var values = searcher.Get())
                    foreach (ManagementObject item in values)
                        using (item) return item["PartOfDomain"] == null ? (bool?)null : Convert.ToBoolean(item["PartOfDomain"], CultureInfo.InvariantCulture);
            }
            catch { }
            return null;
        }

        private static bool? MdmRegistered()
        {
            try
            {
                bool registered;
                return IsDeviceRegisteredWithManagement(out registered, 0, IntPtr.Zero) == 0 ? (bool?)registered : null;
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
        }

        private static string Known(bool? value) { return value.HasValue ? value.Value.ToString() : "Unknown"; }

        [DllImport("MDMRegistration.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int IsDeviceRegisteredWithManagement([MarshalAs(UnmanagedType.Bool)] out bool registered, uint length, IntPtr userPrincipalName);

        private const string Preamble = @"
$ErrorActionPreference = 'Stop'
$WarningPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
$InformationPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$env:PSModulePath = [IO.Path]::Combine($PSHOME, 'Modules')
function Import-SystemModule([string]$name) {
    Microsoft.PowerShell.Core\Import-Module -Name ([IO.Path]::Combine($PSHOME, 'Modules', $name, $name + '.psd1')) -Global -ErrorAction Stop
}
Import-SystemModule 'Microsoft.PowerShell.Utility'
$r = [ordered]@{ Id = 'ACTION'; Title = ''; Outcome = 4; Reason = ''; Before = ''; After = '' }
function Finish([int]$outcome, [string]$reason) {
    $r.Outcome = $outcome
    $r.Reason = $reason
    $r | Microsoft.PowerShell.Utility\ConvertTo-Json -Compress -Depth 3
    exit 0
}
function TamperHint {
    try {
        $s = Defender\Get-MpComputerStatus -ErrorAction Stop
        if ($s.IsTamperProtected -eq $true) { return ' Защита от изменений включена; приложение её не отключает.' }
        if ($s.IsTamperProtected -eq $false) { return ' Защита от изменений сообщает состояние Off.' }
    } catch { }
    return ' Состояние защиты от изменений неизвестно.'
}
try {
BODY
} catch {
    $status = 5
    if ($_.CategoryInfo.Category -eq 'PermissionDenied' -or $_.CategoryInfo.Category -eq 'SecurityError' -or $_.Exception -is [UnauthorizedAccessException]) { $status = 2 }
    if ($_.CategoryInfo.Category -eq 'ObjectNotFound' -or $_.CategoryInfo.Category -eq 'NotImplemented') { $status = 3 }
    $reason = $_.Exception.Message + ' (0x' + $_.Exception.HResult.ToString('X8') + ').'
    if ($r.Id.StartsWith('defender.')) { $reason += TamperHint }
    Finish $status $reason
}
";

        internal static string ScriptFor(SecurityAction action, string body)
        {
            string module = action.Id.StartsWith("firewall.", StringComparison.Ordinal) ? "NetSecurity" :
                action.Id == "defender.scheduled" ? "ScheduledTasks" : "Defender";
            return Preamble.Replace("ACTION", action.Id).Replace("BODY", "Import-SystemModule '" + module + "'\n" + body);
        }

        private static ActionResult RunPowerShell(SecurityAction action, string body)
        {
            ProcessOutput output;
            try { output = RunRawScript(ScriptFor(action, body)); }
            catch (FileNotFoundException error) { return SecurityEngine.Result(action, ActionOutcome.Unavailable, error.Message); }
            if (output.ExitCode != 0)
                return SecurityEngine.Result(action, ActionOutcome.Failed, "PowerShell завершился с кодом " + output.ExitCode.ToString(CultureInfo.InvariantCulture) + ": " + SecurityEngine.Limit(output.Errors));
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(output.Output.Trim().TrimStart('\uFEFF'))))
                {
                    var serializer = new DataContractJsonSerializer(typeof(ActionResult));
                    var result = (ActionResult)serializer.ReadObject(stream);
                    if (result == null || result.Id != action.Id || !Enum.IsDefined(typeof(ActionOutcome), result.Outcome))
                        throw new InvalidDataException("Некорректный ответ провайдера.");
                    result.Reason = SecurityEngine.Limit(result.Reason);
                    result.Before = SecurityEngine.Limit(result.Before);
                    result.After = SecurityEngine.Limit(result.After);
                    return result;
                }
            }
            catch (Exception error)
            {
                return SecurityEngine.Result(action, ActionOutcome.Unverified,
                    "Не удалось прочитать результат провайдера. Возможные изменения не подтверждены. " + SecurityEngine.Explain(error) + " " + SecurityEngine.Limit(output.Errors));
            }
        }

        private sealed class ProcessOutput { internal int ExitCode; internal string Output, Errors; }

        [DataContract]
        private sealed class ProbeSnapshot
        {
            [DataMember] internal List<ActionState> Actions { get; set; }
            [DataMember] internal int? PuaProtection { get; set; }
            [DataMember] internal string PuaError { get; set; }
        }

        private static ProcessOutput RunRawScript(string script, bool readOnly = false)
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string executable = Path.Combine(windows, @"System32\WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("Системный Windows PowerShell не найден.");
            string command = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            if (command.Length > 28000) throw new InvalidOperationException("Встроенная команда превышает предел процесса.");
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(executable, "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + command)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                    WorkingDirectory = windows
                };
                if (!process.Start()) throw new InvalidOperationException("Не удалось запустить системный PowerShell.");
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> errors = process.StandardError.ReadToEndAsync();
                // Never kill a process in a setter or release the operation mutex
                // while it can still mutate a setting. The worker owns the lease.
                if (readOnly)
                {
                    if (!process.WaitForExit(45000))
                    {
                        // This child contains only the fixed read-only probe.
                        // Mutating children never enter this branch.
                        try { process.Kill(); } catch { }
                        throw new TimeoutException("Проверка состояния не завершилась за 45 секунд; состояние неизвестно, изменения не начинались.");
                    }
                }
                else process.WaitForExit();
                string stdout = output.GetAwaiter().GetResult();
                string stderr = errors.GetAwaiter().GetResult();
                return new ProcessOutput { ExitCode = process.ExitCode, Output = stdout, Errors = stderr };
            }
        }

        private const string ScheduledScan = @"
$task = ScheduledTasks\Get-ScheduledTask -TaskPath '\Microsoft\Windows\Windows Defender\' -TaskName 'Windows Defender Scheduled Scan' -ErrorAction Stop
$r.Before = 'Enabled=' + [string]$task.Settings.Enabled + '; State=' + [string]$task.State
$unchanged = $task.Settings.Enabled -eq $false
if (-not $unchanged) { ScheduledTasks\Disable-ScheduledTask -InputObject $task -ErrorAction Stop | Out-Null }
$task = ScheduledTasks\Get-ScheduledTask -TaskPath '\Microsoft\Windows\Windows Defender\' -TaskName 'Windows Defender Scheduled Scan' -ErrorAction Stop
$r.After = 'Enabled=' + [string]$task.Settings.Enabled + '; State=' + [string]$task.State
if ($task.Settings.Enabled -ne $false) { Finish 4 'Задача не подтвердила отключение. ACL, владелец и TaskCache не изменялись.' }
if ($unchanged) { Finish 1 'Штатная задача уже выключена. Другие источники сканирования не изменялись.' }
Finish 0 'Штатная задача выключена; это относится к будущим плановым запускам, а не ко всем источникам сканирования.'
";

        // One read-only PowerShell process gathers the provider values shared by
        // all actions. No setter, registry write, elevation or UI is used here.
        private const string SnapshotScript = @"
$ErrorActionPreference = 'Stop'
$WarningPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
$InformationPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$env:PSModulePath = [IO.Path]::Combine($PSHOME, 'Modules')
function Import-SystemModule([string]$name) {
    Microsoft.PowerShell.Core\Import-Module -Name ([IO.Path]::Combine($PSHOME, 'Modules', $name, $name + '.psd1')) -Global -ErrorAction Stop
}
Import-SystemModule 'Microsoft.PowerShell.Utility'
$script:items = @()
function AddState([string]$id, [int]$state, [string]$reason, [bool]$admin = $true) {
    $script:items += @{ Id=$id; Title=''; State=$state; Reason=$reason; RequiresElevation=$admin }
}
$pref = $null
$status = $null
$prefError = ''
$statusError = ''
try { Import-SystemModule 'Defender'; $pref = Defender\Get-MpPreference -ErrorAction Stop } catch { $prefError = $_.Exception.Message }
try { Import-SystemModule 'Defender'; $status = Defender\Get-MpComputerStatus -ErrorAction Stop } catch { $statusError = $_.Exception.Message }
function AddPreference([string]$id, [string]$name, [string]$target, [bool]$rt = $false) {
    if ($rt) {
        if ($null -eq $status -or $null -eq $status.PSObject.Properties['RealTimeProtectionEnabled']) {
            AddState $id 3 ('Фактическое состояние RTP не прочитано: ' + $statusError); return
        }
        if ($status.RealTimeProtectionEnabled -eq $false) { AddState $id 0 'Фактическая защита RTP уже выключена; изменение предпочтения не требуется.'; return }
        if ($status.RealTimeProtectionEnabled -eq $true) { AddState $id 1 'Фактическая защита RTP включена.'; return }
        AddState $id 3 'Провайдер не вернул однозначное фактическое состояние RTP.'; return
    }
    if ($null -eq $pref) { AddState $id 3 ('Не удалось прочитать предпочтения Defender: ' + $prefError); return }
    if ($null -eq $pref.PSObject.Properties[$name]) { AddState $id 2 ('Параметр ' + $name + ' не поддерживается провайдером.'); return }
    try {
        $raw = $pref.$name
        if ($null -eq $raw) { AddState $id 3 ('Параметр ' + $name + ' не имеет значения.'); return }
        $value = if ($raw -is [bool]) { [string]$raw } else { [string][int]$raw }
        if ($name -eq 'SubmitSamplesConsent' -and $value -eq '0') { AddState $id 0 'Автоматическая отправка уже выключена (AlwaysPrompt); ручное подтверждение сохраняется.'; return }
        if ($value -ne $target) { AddState $id 1 ($name + '=' + $value + '; требуется ' + $target + '.'); return }
        AddState $id 0 ($name + '=' + $value + '; предпочтение уже настроено.')
    } catch { AddState $id 3 $_.Exception.Message }
}
AddPreference 'defender.realtime' 'DisableRealtimeMonitoring' 'True' $true
AddPreference 'defender.cloud' 'MAPSReporting' '0'
AddPreference 'defender.samples' 'SubmitSamplesConsent' '2'
AddPreference 'defender.cfa' 'EnableControlledFolderAccess' '0'
try {
    Import-SystemModule 'ScheduledTasks'
    $task = ScheduledTasks\Get-ScheduledTask -TaskPath '\Microsoft\Windows\Windows Defender\' -TaskName 'Windows Defender Scheduled Scan' -ErrorAction Stop
    if ($task.Settings.Enabled -eq $false) { AddState 'defender.scheduled' 0 'Штатная задача уже выключена.' }
    elseif ($task.Settings.Enabled -eq $true) { AddState 'defender.scheduled' 1 'Штатная задача включена.' }
    else { AddState 'defender.scheduled' 3 'Задача не вернула однозначное значение Enabled.' }
} catch { AddState 'defender.scheduled' 3 ('Не удалось прочитать задачу: ' + $_.Exception.Message) }
$localProfiles = $null
$activeProfiles = $null
$firewallError = ''
try {
    Import-SystemModule 'NetSecurity'
    $localProfiles = @(NetSecurity\Get-NetFirewallProfile -PolicyStore PersistentStore -ErrorAction Stop)
    $activeProfiles = @(NetSecurity\Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction Stop)
} catch { $firewallError = $_.Exception.Message }
foreach ($name in @('Domain','Private','Public')) {
    $id = 'firewall.' + $name.ToLowerInvariant()
    try {
        $local = @($localProfiles | Where-Object { [string]$_.Name -eq $name })
        $active = @($activeProfiles | Where-Object { [string]$_.Name -eq $name })
        if ($local.Count -ne 1 -or $active.Count -ne 1) { AddState $id 3 ('Не удалось прочитать профиль: ' + $firewallError); continue }
        $configured = [int]$local[0].Enabled
        $effective = [int]$active[0].Enabled
        if ($effective -eq 0) { AddState $id 0 ('Эффективный профиль уже выключен; local=' + $configured + '.'); continue }
        if ($effective -eq 1) { AddState $id 1 ('Эффективный профиль включён; local=' + $configured + '.'); continue }
        AddState $id 3 ('Неизвестное эффективное значение Enabled=' + $effective + '.')
    } catch { AddState $id 3 $_.Exception.Message }
}
if ($null -ne $status -and $null -ne $status.PSObject.Properties['IsTamperProtected'] -and $status.IsTamperProtected -eq $false) {
    AddState 'defender.tamper' 0 'Провайдер сообщает Off; защита от изменений не изменяется.' $false
} else {
    AddState 'defender.tamper' 2 'Программное отключение защиты от изменений для локального приложения не подтверждено; переключатель не автоматизируется.' $false
}
if ($null -ne $status -and $null -ne $status.PSObject.Properties['SmartAppControlState'] -and [string]$status.SmartAppControlState -eq 'Off') {
    AddState 'security.sac' 0 'Провайдер сообщает Smart App Control Off; Code Integrity не изменяется.' $false
} else {
    AddState 'security.sac' 2 'Документированный production setter Smart App Control без перезагрузки не подтверждён.' $false
}
$puaValue = $null
$puaError = $prefError
try {
    if ($null -ne $pref -and $null -ne $pref.PSObject.Properties['PUAProtection'] -and $null -ne $pref.PUAProtection) { $puaValue = [int]$pref.PUAProtection }
    else { $puaError = 'Провайдер не вернул PUAProtection.' }
} catch { $puaError = $_.Exception.Message }
@{ Actions=@($script:items); PuaProtection=$puaValue; PuaError=$puaError } | Microsoft.PowerShell.Utility\ConvertTo-Json -Compress -Depth 4
";

    }
}

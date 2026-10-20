using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace JustEnoughTools.Protection
{
    public enum ProtectionComponentState { Present, Absent, Unknown }

    public sealed class ProtectionComponent
    {
        public string Id { get; private set; }
        public string DisplayName { get; private set; }
        public ProtectionComponentState State { get; private set; }
        public string Details { get; private set; }

        internal ProtectionComponent(string id, string displayName, ProtectionComponentState state, string details)
        {
            Id = id;
            DisplayName = displayName;
            State = state;
            Details = details;
        }
    }

    public sealed class ProtectionSnapshot
    {
        public string OSDescription { get; internal set; }
        public string Build { get; internal set; }
        public bool? IsVirtualMachine { get; internal set; }
        public string VirtualMachineDetails { get; internal set; }
        public IList<ProtectionComponent> Components { get; internal set; }

        // A read-only inventory is never evidence that the full removal path was tested.
        public bool RemovalVerified { get { return false; } }
        public string Summary
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "Обнаружено компонентов: {0}; отсутствие подтверждено для {1}; состояние неизвестно для {2}. Удаление не выполнялось.",
                    Components.Count(c => c.State == ProtectionComponentState.Present),
                    Components.Count(c => c.State == ProtectionComponentState.Absent),
                    Components.Count(c => c.State == ProtectionComponentState.Unknown));
            }
        }
    }

    public sealed class ProtectionOperationResult
    {
        public bool Succeeded { get { return false; } }
        public bool PerformedChanges { get { return false; } }
        public string Message { get; internal set; }
        public ProtectionSnapshot Snapshot { get; internal set; }
    }

    /// <summary>
    /// Read-only inventory. No method in this class changes a service, registry value,
    /// file, driver, scheduled task or security policy. Missing/denied query results
    /// are not treated as proof that Windows has no security capabilities.
    /// </summary>
    public static class ProtectionService
    {
        // Preflight availability is independent of inventory and elevation. An
        // unavailable operation must not prompt for administrator rights.
        public static bool IsRemovalAvailable { get { return false; } }
        public const string UnsupportedRemovalReason = "Удаление не выполнялось: проверенного способа полного удаления Defender Antivirus нет. Штатное удаление антивируса на клиентской Windows не поддерживается.";

        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorServiceDoesNotExist = 1060;
        private const uint InvalidFileAttributes = 0xffffffff;

        public static ProtectionSnapshot Inspect()
        {
            var components = new List<ProtectionComponent>();
            var snapshot = new ProtectionSnapshot
            {
                OSDescription = "Windows",
                Build = "Не определена",
                VirtualMachineDetails = "Среда не определена",
                Components = components.AsReadOnly()
            };
            InspectOperatingSystem(snapshot);
            InspectMachine(snapshot);

            AddService(components, "WinDefend", "Microsoft Defender Antivirus");
            AddService(components, "MdCoreSvc", "Microsoft Defender Core");
            AddService(components, "WdNisSvc", "Проверка сети Microsoft Defender");
            AddService(components, "WdFilter", "Драйвер фильтрации Microsoft Defender");
            AddService(components, "WdBoot", "Загрузочный драйвер Microsoft Defender");
            AddService(components, "WdNisDrv", "Сетевой драйвер Microsoft Defender");
            AddService(components, "SecurityHealthService", "Служба состояния безопасности Windows");
            AddService(components, "wscsvc", "Центр обеспечения безопасности Windows");
            AddService(components, "Sense", "Microsoft Defender for Endpoint");

            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            components.Add(InspectFile("SmartScreen.File", "Файл Windows SmartScreen", Path.Combine(systemDirectory, "smartscreen.exe")));
            components.Add(InspectFile("MRT.File", "Средство удаления вредоносных программ MRT", Path.Combine(systemDirectory, "MRT.exe")));
            components.Add(InspectFile("Defender.Platform", "Каталог платформы Microsoft Defender",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender", "Platform")));
            components.Add(InspectSecurityUiRegistration());
            components.Add(InspectSmartAppControl());
            components.Add(InspectAmsiProviders());
            return snapshot;
        }

        public static Task<ProtectionOperationResult> RequestRemovalAsync(IProgress<string> progress)
        {
            if (progress != null) progress.Report(UnsupportedRemovalReason);
            return Task.FromResult(new ProtectionOperationResult
            {
                Message = UnsupportedRemovalReason,
                Snapshot = Inspect()
            });
        }

        private static void InspectOperatingSystem(ProtectionSnapshot snapshot)
        {
            try
            {
                using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false))
                {
                    if (key == null) return;
                    var build = Convert.ToString(key.GetValue("CurrentBuildNumber"), CultureInfo.InvariantCulture);
                    var revision = Convert.ToString(key.GetValue("UBR"), CultureInfo.InvariantCulture);
                    var version = Convert.ToString(key.GetValue("DisplayVersion"), CultureInfo.InvariantCulture);
                    int numericBuild;
                    var family = int.TryParse(build, out numericBuild) && numericBuild >= 22000 ? "Windows 11" : "Windows";
                    snapshot.Build = string.IsNullOrEmpty(build) ? "Не определена" : build + (string.IsNullOrEmpty(revision) ? "" : "." + revision);
                    snapshot.OSDescription = (family + " " + version + (Environment.Is64BitOperatingSystem ? " x64" : " x86")).Trim();
                }
            }
            catch (Exception ex) when (IsExpectedQueryFailure(ex))
            {
                snapshot.OSDescription = "Windows (не удалось прочитать версию)";
            }
        }

        private static void InspectMachine(ProtectionSnapshot snapshot)
        {
            try
            {
                using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = machine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS", false))
                {
                    if (key == null) return;
                    var manufacturer = Convert.ToString(key.GetValue("SystemManufacturer"), CultureInfo.InvariantCulture);
                    var product = Convert.ToString(key.GetValue("SystemProductName"), CultureInfo.InvariantCulture);
                    var combined = (manufacturer + " " + product).Trim();
                    snapshot.VirtualMachineDetails = string.IsNullOrWhiteSpace(combined) ? "Данные BIOS отсутствуют" : combined;
                    if (combined.Length == 0) return;
                    var knownVirtual = new[] { "QEMU", "KVM", "VMware", "VirtualBox", "Virtual Machine", "Xen", "Parallels", "Bochs", "BHYVE" };
                    snapshot.IsVirtualMachine = knownVirtual.Any(value => combined.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
                }
            }
            catch (Exception ex) when (IsExpectedQueryFailure(ex))
            {
                snapshot.VirtualMachineDetails = "Не удалось прочитать данные BIOS: " + ex.Message;
            }
        }

        private static void AddService(ICollection<ProtectionComponent> components, string name, string displayName)
        {
            var manager = OpenSCManager(null, null, 0x0001);
            if (manager == IntPtr.Zero)
            {
                components.Add(Unknown("Service." + name, displayName, "Не удалось открыть диспетчер служб", Marshal.GetLastWin32Error()));
                return;
            }
            try
            {
                var service = OpenService(manager, name, 0x0004);
                if (service == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    components.Add(error == ErrorServiceDoesNotExist
                        ? new ProtectionComponent("Service." + name, displayName, ProtectionComponentState.Absent, "Запись службы отсутствует")
                        : Unknown("Service." + name, displayName, "Не удалось запросить службу", error));
                    return;
                }
                try
                {
                    ServiceStatusProcess status;
                    int bytesNeeded;
                    if (!QueryServiceStatusEx(service, 0, out status, Marshal.SizeOf(typeof(ServiceStatusProcess)), out bytesNeeded))
                    {
                        components.Add(Unknown("Service." + name, displayName, "Запись службы есть; её состояние недоступно", Marshal.GetLastWin32Error()));
                        return;
                    }
                    var state = status.CurrentState == 4 ? "работает" : status.CurrentState == 1 ? "остановлена" : "состояние " + status.CurrentState.ToString(CultureInfo.InvariantCulture);
                    components.Add(new ProtectionComponent("Service." + name, displayName, ProtectionComponentState.Present, "Запись службы есть, " + state));
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }

        private static ProtectionComponent InspectFile(string id, string displayName, string path)
        {
            var attributes = GetFileAttributes(path);
            if (attributes != InvalidFileAttributes)
                return new ProtectionComponent(id, displayName, ProtectionComponentState.Present, "Объект существует");
            var error = Marshal.GetLastWin32Error();
            return error == ErrorFileNotFound || error == ErrorPathNotFound
                ? new ProtectionComponent(id, displayName, ProtectionComponentState.Absent, "Объект по проверяемому пути отсутствует")
                : Unknown(id, displayName, "Не удалось проверить объект", error);
        }

        private static ProtectionComponent InspectSecurityUiRegistration()
        {
            const string id = "SecurityUI.Registration";
            const string name = "Регистрация интерфейса безопасности Windows";
            try
            {
                using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = machine.OpenSubKey(@"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages", false))
                {
                    if (key == null) return new ProtectionComponent(id, name, ProtectionComponentState.Unknown, "Реестр пакетов недоступен; отсутствие интерфейса не подтверждено");
                    var found = key.GetSubKeyNames().Any(value => value.StartsWith("Microsoft.SecHealthUI_", StringComparison.OrdinalIgnoreCase) || value.StartsWith("Microsoft.Windows.SecHealthUI_", StringComparison.OrdinalIgnoreCase));
                    return new ProtectionComponent(id, name, found ? ProtectionComponentState.Present : ProtectionComponentState.Unknown,
                        found ? "Пакет интерфейса зарегистрирован" : "В проверенном разделе пакет не найден; отсутствие интерфейса не подтверждено");
                }
            }
            catch (Exception ex) when (IsExpectedQueryFailure(ex)) { return new ProtectionComponent(id, name, ProtectionComponentState.Unknown, ex.Message); }
        }

        private static ProtectionComponent InspectSmartAppControl()
        {
            const string id = "SmartAppControl.Policy";
            const string name = "Smart App Control";
            try
            {
                using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Policy", false))
                {
                    var state = key == null ? null : key.GetValue("VerifiedAndReputablePolicyState");
                    if (state == null) return new ProtectionComponent(id, name, ProtectionComponentState.Unknown, "Политика не задана; отсутствие механизма этим не подтверждается");
                    return new ProtectionComponent(id, name, ProtectionComponentState.Present, "Значение политики: " + Convert.ToString(state, CultureInfo.InvariantCulture) + ". Выключенная политика не означает удаления компонента.");
                }
            }
            catch (Exception ex) when (IsExpectedQueryFailure(ex)) { return new ProtectionComponent(id, name, ProtectionComponentState.Unknown, ex.Message); }
        }

        private static ProtectionComponent InspectAmsiProviders()
        {
            const string id = "AMSI.Providers";
            const string name = "Зарегистрированные провайдеры AMSI";
            try
            {
                using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\AMSI\Providers", false))
                {
                    var count = key == null ? 0 : key.SubKeyCount;
                    return new ProtectionComponent(id, name, count > 0 ? ProtectionComponentState.Present : ProtectionComponentState.Absent,
                        "Зарегистрировано провайдеров: " + count.ToString(CultureInfo.InvariantCulture) + ". Это не проверка всех механизмов обнаружения угроз.");
                }
            }
            catch (Exception ex) when (IsExpectedQueryFailure(ex)) { return new ProtectionComponent(id, name, ProtectionComponentState.Unknown, ex.Message); }
        }

        private static bool IsExpectedQueryFailure(Exception ex)
        {
            return ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is IOException || ex is ArgumentException;
        }

        private static ProtectionComponent Unknown(string id, string displayName, string message, int error)
        {
            return new ProtectionComponent(id, displayName, ProtectionComponentState.Unknown,
                message + ": " + new Win32Exception(error).Message + " (" + error.ToString(CultureInfo.InvariantCulture) + ")");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatusProcess
        {
            public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string machineName, string databaseName, uint desiredAccess);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, out ServiceStatusProcess status, int bufferSize, out int bytesNeeded);
        [DllImport("advapi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr service);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributes(string path);
    }
}

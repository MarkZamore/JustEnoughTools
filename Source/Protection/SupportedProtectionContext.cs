using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace JustEnoughTools.Protection
{
    /// <summary>An unavailable query has no value. Boolean and numeric observations use nullable types.</summary>
    public sealed class ContextValue<T>
    {
        public bool IsKnown { get; private set; }
        public T Value { get; private set; }
        public string Source { get; private set; }
        public string Details { get; private set; }

        internal static ContextValue<T> Known(T value, string source)
        {
            if (ReferenceEquals(value, null)) throw new ArgumentNullException("value");
            return new ContextValue<T> { IsKnown = true, Value = value, Source = source, Details = "Запрос выполнен." };
        }

        internal static ContextValue<T> Unknown(string source, string details)
        {
            // All value-type observations below are Nullable<T>: unknown is null, not false/zero.
            if (typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) == null)
                throw new InvalidOperationException("Context value types must be nullable.");
            return new ContextValue<T> { Source = source, Details = details };
        }
    }

    public enum MdeOnboardingState { NotOnboarded = 0, Onboarded = 1 }
    public enum SmartAppControlPolicyState { Off = 0, Enforcement = 1, Evaluation = 2 }
    public enum WscAntivirusProductState { On = 0, Off = 1, Snoozed = 2, Expired = 3 }
    public enum WscAntivirusSignatureStatus { OutOfDate = 0, UpToDate = 1 }
    public enum WscSecurityProviderHealth { Good = 0, NotMonitored = 1, Poor = 2, Snooze = 3 }

    public sealed class SupportedWindowsContext
    {
        public ContextValue<string> Edition { get; internal set; }
        public ContextValue<string> DisplayVersion { get; internal set; }
        public ContextValue<string> Build { get; internal set; }
        public ContextValue<string> Architecture { get; internal set; }
    }

    public sealed class SupportedDefenderContext
    {
        public ContextValue<string> PlatformVersion { get; internal set; }
        public ContextValue<string> RunningMode { get; internal set; }
        public ContextValue<bool?> AntimalwareServiceEnabled { get; internal set; }
        public ContextValue<bool?> AntivirusEnabled { get; internal set; }
        public ContextValue<bool?> RealTimeProtectionEnabled { get; internal set; }
        public ContextValue<bool?> OnAccessProtectionEnabled { get; internal set; }
        public ContextValue<bool?> IoavProtectionEnabled { get; internal set; }
        public ContextValue<bool?> BehaviorMonitorEnabled { get; internal set; }
        public ContextValue<bool?> TamperProtectionEnabled { get; internal set; }
        public ContextValue<string> TamperProtectionSource { get; internal set; }
    }

    public sealed class SupportedMdeContext
    {
        public ContextValue<MdeOnboardingState?> OnboardingState { get; internal set; }
    }

    public sealed class SupportedSmartAppControlContext
    {
        // Defender's reported runtime state and the registry's configured policy
        // are separate evidence. A registry value alone is not enforcement proof.
        public ContextValue<string> ReportedState { get; internal set; }
        public ContextValue<SmartAppControlPolicyState?> PolicyState { get; internal set; }
    }

    public sealed class RegisteredAntivirusProduct
    {
        public ContextValue<string> Name { get; internal set; }
        public ContextValue<WscAntivirusProductState?> State { get; internal set; }
        public ContextValue<WscAntivirusSignatureStatus?> SignatureStatus { get; internal set; }
        public ContextValue<bool?> ReportedHealthy { get; internal set; }
    }

    public sealed class SupportedProtectionSnapshot
    {
        public DateTime CollectedAtUtc { get; internal set; }
        public SupportedWindowsContext Windows { get; internal set; }
        public SupportedDefenderContext Defender { get; internal set; }
        public SupportedMdeContext Mde { get; internal set; }
        public SupportedSmartAppControlContext SmartAppControl { get; internal set; }
        public ContextValue<IList<RegisteredAntivirusProduct>> AntivirusProducts { get; internal set; }
        public ContextValue<WscSecurityProviderHealth?> AntivirusHealth { get; internal set; }
        public ContextValue<bool?> ThirdPartyAntivirusConfirmed { get; internal set; }
    }

    /// <summary>
    /// Reads status only. It never invokes WMI methods, registers a security product,
    /// changes settings, starts PowerShell, or writes files/registry values.
    /// WSC product information is reported health, not an attestation of a scanner.
    /// </summary>
    public static class SupportedProtectionContext
    {
        private const string WindowsSource = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion (64-bit, read-only)";
        private const string DefenderSource = @"ROOT\Microsoft\Windows\Defender:MSFT_MpComputerStatus (read-only WMI)";
        private const string MdeSource = @"HKLM\SOFTWARE\Microsoft\Windows Advanced Threat Protection\Status\OnboardingState (read-only)";
        private const string SacSource = @"HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy\VerifiedAndReputablePolicyState (read-only)";
        private const string WscSource = "Windows Security Center IWSCProductList / IWscProduct, ANTIVIRUS";
        private const string WscHealthSource = "Windows Security Center WscGetSecurityProviderHealth(ANTIVIRUS)";
        private const uint AntivirusProvider = 4;

        public static SupportedProtectionSnapshot Inspect()
        {
            var snapshot = CreateUnknownSnapshot("Запрос ещё не выполнен.");
            InspectWindows(snapshot.Windows);
            InspectDefender(snapshot);
            snapshot.Mde.OnboardingState = ReadRegistryEnum<MdeOnboardingState>(
                @"SOFTWARE\Microsoft\Windows Advanced Threat Protection\Status", "OnboardingState", MdeSource);
            snapshot.SmartAppControl.PolicyState = ReadRegistryEnum<SmartAppControlPolicyState>(
                @"SYSTEM\CurrentControlSet\Control\CI\Policy", "VerifiedAndReputablePolicyState", SacSource);
            InspectWsc(snapshot);
            return snapshot;
        }

        internal static SupportedProtectionSnapshot CreateUnknownSnapshot(string details)
        {
            return new SupportedProtectionSnapshot
            {
                CollectedAtUtc = DateTime.UtcNow,
                Windows = new SupportedWindowsContext
                {
                    Edition = ContextValue<string>.Unknown(WindowsSource, details),
                    DisplayVersion = ContextValue<string>.Unknown(WindowsSource, details),
                    Build = ContextValue<string>.Unknown(WindowsSource, details),
                    Architecture = ContextValue<string>.Unknown("Windows runtime", details)
                },
                Defender = UnknownDefender(details),
                Mde = new SupportedMdeContext { OnboardingState = ContextValue<MdeOnboardingState?>.Unknown(MdeSource, details) },
                SmartAppControl = new SupportedSmartAppControlContext
                {
                    ReportedState = ContextValue<string>.Unknown(DefenderSource + ".SmartAppControlState", details),
                    PolicyState = ContextValue<SmartAppControlPolicyState?>.Unknown(SacSource, details)
                },
                AntivirusProducts = ContextValue<IList<RegisteredAntivirusProduct>>.Unknown(WscSource, details),
                AntivirusHealth = ContextValue<WscSecurityProviderHealth?>.Unknown(WscHealthSource, details),
                ThirdPartyAntivirusConfirmed = ContextValue<bool?>.Unknown(WscSource,
                    "WSC сообщает регистрацию и состояние продукта, но не удостоверяет его производителя или фактическое сканирование. Имя продукта не используется как доказательство стороннего антивируса.")
            };
        }

        private static SupportedDefenderContext UnknownDefender(string details)
        {
            return new SupportedDefenderContext
            {
                PlatformVersion = ContextValue<string>.Unknown(DefenderSource + ".AMProductVersion", details),
                RunningMode = ContextValue<string>.Unknown(DefenderSource + ".AMRunningMode", details),
                AntimalwareServiceEnabled = ContextValue<bool?>.Unknown(DefenderSource + ".AMServiceEnabled", details),
                AntivirusEnabled = ContextValue<bool?>.Unknown(DefenderSource + ".AntivirusEnabled", details),
                RealTimeProtectionEnabled = ContextValue<bool?>.Unknown(DefenderSource + ".RealTimeProtectionEnabled", details),
                OnAccessProtectionEnabled = ContextValue<bool?>.Unknown(DefenderSource + ".OnAccessProtectionEnabled", details),
                IoavProtectionEnabled = ContextValue<bool?>.Unknown(DefenderSource + ".IoavProtectionEnabled", details),
                BehaviorMonitorEnabled = ContextValue<bool?>.Unknown(DefenderSource + ".BehaviorMonitorEnabled", details),
                TamperProtectionEnabled = ContextValue<bool?>.Unknown(DefenderSource + ".IsTamperProtected", details),
                TamperProtectionSource = ContextValue<string>.Unknown(DefenderSource + ".TamperProtectionSource", details)
            };
        }

        private static void InspectWindows(SupportedWindowsContext result)
        {
            try
            {
                NativeSystemInfo info;
                GetNativeSystemInfo(out info);
                var architecture = info.ProcessorArchitecture == 9 ? "x64" : info.ProcessorArchitecture == 12 ? "arm64" : info.ProcessorArchitecture == 0 ? "x86" : null;
                result.Architecture = architecture == null
                    ? ContextValue<string>.Unknown("GetNativeSystemInfo", "Архитектура не распознана.")
                    : ContextValue<string>.Known(architecture, "GetNativeSystemInfo");
            }
            catch (Exception error) when (IsQueryFailure(error))
            { result.Architecture = ContextValue<string>.Unknown("GetNativeSystemInfo", Describe(error)); }
            try
            {
                using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false))
                {
                    if (key == null) throw new InvalidOperationException("Раздел версии Windows отсутствует.");
                    result.Edition = ParseText(key.GetValue("EditionID"), WindowsSource + ".EditionID");
                    result.DisplayVersion = ParseText(key.GetValue("DisplayVersion"), WindowsSource + ".DisplayVersion");
                    var build = ParseText(key.GetValue("CurrentBuildNumber"), WindowsSource + ".CurrentBuildNumber");
                    var revision = key.GetValue("UBR");
                    result.Build = build.IsKnown && revision is int && (int)revision >= 0
                        ? ContextValue<string>.Known(build.Value + "." + ((int)revision).ToString(CultureInfo.InvariantCulture), WindowsSource)
                        : ContextValue<string>.Unknown(WindowsSource, "Номер сборки или UBR недоступен.");
                }
            }
            catch (Exception error) when (IsQueryFailure(error))
            {
                var details = Describe(error);
                result.Edition = ContextValue<string>.Unknown(WindowsSource, details);
                result.DisplayVersion = ContextValue<string>.Unknown(WindowsSource, details);
                result.Build = ContextValue<string>.Unknown(WindowsSource, details);
            }
        }

        // The documented MSFT_MpComputerStatus status provider is the source used
        // by Get-MpComputerStatus. Optional, newer properties are checked separately.
        // https://learn.microsoft.com/previous-versions/windows/desktop/defender/msft-mpcomputerstatus
        private static void InspectDefender(SupportedProtectionSnapshot snapshot)
        {
            try
            {
                var options = new EnumerationOptions { ReturnImmediately = true, Timeout = TimeSpan.FromSeconds(5) };
                var scope = new ManagementScope(@"\\.\ROOT\Microsoft\Windows\Defender",
                    new ConnectionOptions { Timeout = TimeSpan.FromSeconds(5) });
                using (var query = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_MpComputerStatus"), options))
                using (var items = query.Get())
                {
                    bool found = false;
                    foreach (ManagementObject item in items)
                    using (item)
                    {
                        if (found) throw new InvalidOperationException("Провайдер вернул несколько состояний компьютера.");
                        found = true;
                        snapshot.Defender.PlatformVersion = ReadWmiText(item, "AMProductVersion");
                        snapshot.Defender.RunningMode = ReadWmiText(item, "AMRunningMode");
                        snapshot.Defender.AntimalwareServiceEnabled = ReadWmiBoolean(item, "AMServiceEnabled");
                        snapshot.Defender.AntivirusEnabled = ReadWmiBoolean(item, "AntivirusEnabled");
                        snapshot.Defender.RealTimeProtectionEnabled = ReadWmiBoolean(item, "RealTimeProtectionEnabled");
                        snapshot.Defender.OnAccessProtectionEnabled = ReadWmiBoolean(item, "OnAccessProtectionEnabled");
                        snapshot.Defender.IoavProtectionEnabled = ReadWmiBoolean(item, "IoavProtectionEnabled");
                        snapshot.Defender.BehaviorMonitorEnabled = ReadWmiBoolean(item, "BehaviorMonitorEnabled");
                        snapshot.Defender.TamperProtectionEnabled = ReadWmiBoolean(item, "IsTamperProtected");
                        snapshot.Defender.TamperProtectionSource = ReadWmiText(item, "TamperProtectionSource");
                        snapshot.SmartAppControl.ReportedState = ReadWmiText(item, "SmartAppControlState");
                    }
                    if (!found) throw new InvalidOperationException("Провайдер не вернул состояние компьютера.");
                }
            }
            catch (Exception error) when (IsQueryFailure(error))
            {
                snapshot.Defender = UnknownDefender(Describe(error));
                snapshot.SmartAppControl.ReportedState = ContextValue<string>.Unknown(DefenderSource + ".SmartAppControlState", Describe(error));
            }
        }

        private static ContextValue<string> ReadWmiText(ManagementBaseObject item, string property)
        {
            var source = DefenderSource + "." + property;
            try { return ParseText(item.Properties[property] == null ? null : item.Properties[property].Value, source); }
            catch (Exception error) when (IsQueryFailure(error)) { return ContextValue<string>.Unknown(source, Describe(error)); }
        }

        private static ContextValue<bool?> ReadWmiBoolean(ManagementBaseObject item, string property)
        {
            var source = DefenderSource + "." + property;
            try { return ParseBoolean(item.Properties[property] == null ? null : item.Properties[property].Value, source); }
            catch (Exception error) when (IsQueryFailure(error)) { return ContextValue<bool?>.Unknown(source, Describe(error)); }
        }

        internal static ContextValue<string> ParseText(object value, string source)
        {
            var text = value as string;
            return !string.IsNullOrWhiteSpace(text) ? ContextValue<string>.Known(text, source)
                : ContextValue<string>.Unknown(source, "Свойство отсутствует, пусто или имеет неожиданный тип.");
        }

        internal static ContextValue<bool?> ParseBoolean(object value, string source)
        {
            return value is bool ? ContextValue<bool?>.Known((bool)value, source)
                : ContextValue<bool?>.Unknown(source, "Логическое свойство отсутствует или имеет неожиданный тип.");
        }

        internal static ContextValue<T?> ParseEnum<T>(object value, string source) where T : struct
        {
            // Registry policy values are DWORD; strings and signed-overflow values
            // must not silently turn into known states or a default zero value.
            if (!(value is int) || !Enum.IsDefined(typeof(T), (int)value))
                return ContextValue<T?>.Unknown(source, "Значение отсутствует, имеет неожиданный тип или не определено документацией.");
            return ContextValue<T?>.Known((T)Enum.ToObject(typeof(T), (int)value), source);
        }

        private static ContextValue<T?> ReadRegistryEnum<T>(string path, string name, string source) where T : struct
        {
            try
            {
                using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = machine.OpenSubKey(path, false))
                    return ParseEnum<T>(key == null ? null : key.GetValue(name), source);
            }
            catch (Exception error) when (IsQueryFailure(error)) { return ContextValue<T?>.Unknown(source, Describe(error)); }
        }

        // Documented WSC COM interfaces inherit IDispatch. InterfaceIsDual supplies
        // its four vtable slots before Initialize/get_Count/get_Item and properties.
        // https://learn.microsoft.com/windows/win32/api/iwscapi/nn-iwscapi-iwscproductlist
        private static void InspectWsc(SupportedProtectionSnapshot snapshot)
        {
            try
            {
                int health;
                int status = WscGetSecurityProviderHealth(AntivirusProvider, out health);
                snapshot.AntivirusHealth = ParseWscHealth(status, health);
            }
            catch (Exception error) when (IsQueryFailure(error))
            {
                snapshot.AntivirusHealth = ContextValue<WscSecurityProviderHealth?>.Unknown(WscHealthSource, Describe(error));
            }

            object instance = null;
            try
            {
                instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("17072F7B-9ABE-4A74-A261-1EB76B55107A"), true));
                var list = (IWscProductList)instance;
                ThrowUnlessSuccess(list.Initialize(AntivirusProvider));
                int count;
                ThrowUnlessSuccess(list.GetCount(out count));
                if (count < 0 || count > 1024) throw new InvalidOperationException("WSC вернул некорректное число продуктов.");
                var products = new List<RegisteredAntivirusProduct>();
                for (uint index = 0; index < (uint)count; index++)
                {
                    IWscProduct product = null;
                    try
                    {
                        ThrowUnlessSuccess(list.GetItem(index, out product));
                        if (product == null) throw new InvalidOperationException("WSC вернул пустой продукт.");
                        string name; int state; int signatures;
                        int nameStatus = product.GetProductName(out name);
                        int stateStatus = product.GetProductState(out state);
                        int signatureStatus = product.GetSignatureStatus(out signatures);
                        var item = new RegisteredAntivirusProduct
                        {
                            Name = nameStatus == 0 ? ParseText(name, WscSource + ".ProductName") : ContextValue<string>.Unknown(WscSource, FormatHresult(nameStatus)),
                            State = stateStatus == 0 ? ParseEnum<WscAntivirusProductState>(state, WscSource + ".ProductState") : ContextValue<WscAntivirusProductState?>.Unknown(WscSource, FormatHresult(stateStatus)),
                            SignatureStatus = signatureStatus == 0 ? ParseEnum<WscAntivirusSignatureStatus>(signatures, WscSource + ".SignatureStatus") : ContextValue<WscAntivirusSignatureStatus?>.Unknown(WscSource, FormatHresult(signatureStatus))
                        };
                        item.ReportedHealthy = EvaluateProductHealth(item.State, item.SignatureStatus);
                        products.Add(item);
                    }
                    finally { if (product != null && Marshal.IsComObject(product)) Marshal.FinalReleaseComObject(product); }
                }
                snapshot.AntivirusProducts = ContextValue<IList<RegisteredAntivirusProduct>>.Known(products.AsReadOnly(), WscSource);
            }
            catch (Exception error) when (IsQueryFailure(error))
            {
                // A partial enumeration is not evidence that omitted AV products do not exist.
                snapshot.AntivirusProducts = ContextValue<IList<RegisteredAntivirusProduct>>.Unknown(WscSource, Describe(error));
            }
            finally { if (instance != null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance); }
        }

        internal static ContextValue<WscSecurityProviderHealth?> ParseWscHealth(int status, int value)
        {
            // S_FALSE means the WSC service is not running; its output POOR is not
            // a successful health observation. Only S_OK yields a known value.
            return status == 0 ? ParseEnum<WscSecurityProviderHealth>(value, WscHealthSource)
                : ContextValue<WscSecurityProviderHealth?>.Unknown(WscHealthSource, FormatHresult(status));
        }

        internal static ContextValue<bool?> EvaluateProductHealth(ContextValue<WscAntivirusProductState?> state,
            ContextValue<WscAntivirusSignatureStatus?> signatures)
        {
            if (!state.IsKnown || !signatures.IsKnown)
                return ContextValue<bool?>.Unknown(WscSource, "Состояние продукта или актуальность сигнатур неизвестны.");
            return ContextValue<bool?>.Known(state.Value == WscAntivirusProductState.On &&
                signatures.Value == WscAntivirusSignatureStatus.UpToDate, WscSource + " (reported state/signatures)");
        }

        private static void ThrowUnlessSuccess(int status)
        {
            if (status != 0) throw new InvalidOperationException(FormatHresult(status));
        }

        private static string FormatHresult(int value) { return "Запрос WSC не завершён успешно: HRESULT 0x" + value.ToString("X8", CultureInfo.InvariantCulture) + "."; }
        private static string Describe(Exception error) { return error.GetType().Name + ": " + error.Message; }
        private static bool IsQueryFailure(Exception error)
        {
            return error is ManagementException || error is COMException || error is UnauthorizedAccessException ||
                error is SecurityException || error is System.IO.IOException || error is InvalidOperationException ||
                error is ArgumentException || error is NotSupportedException || error is DllNotFoundException ||
                error is EntryPointNotFoundException || error is TypeLoadException;
        }

        [DllImport("wscapi.dll", ExactSpelling = true)]
        private static extern int WscGetSecurityProviderHealth(uint providers, out int health);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern void GetNativeSystemInfo(out NativeSystemInfo info);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSystemInfo
        {
            public ushort ProcessorArchitecture;
            public ushort Reserved;
            public uint PageSize;
            public IntPtr MinimumApplicationAddress;
            public IntPtr MaximumApplicationAddress;
            public UIntPtr ActiveProcessorMask;
            public uint NumberOfProcessors;
            public uint ProcessorType;
            public uint AllocationGranularity;
            public ushort ProcessorLevel;
            public ushort ProcessorRevision;
        }

        [ComImport, Guid("722A338C-6E8E-4E72-AC27-1417FB0C81C2"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IWscProductList
        {
            [PreserveSig] int Initialize(uint provider);
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetItem(uint index, [MarshalAs(UnmanagedType.Interface)] out IWscProduct product);
        }

        [ComImport, Guid("8C38232E-3A45-4A27-92B0-1A16A975F669"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IWscProduct
        {
            [PreserveSig] int GetProductName([MarshalAs(UnmanagedType.BStr)] out string name);
            [PreserveSig] int GetProductState(out int state);
            [PreserveSig] int GetSignatureStatus(out int status);
            [PreserveSig] int GetRemediationPath([MarshalAs(UnmanagedType.BStr)] out string path);
            [PreserveSig] int GetProductStateTimestamp([MarshalAs(UnmanagedType.BStr)] out string timestamp);
        }
    }
}

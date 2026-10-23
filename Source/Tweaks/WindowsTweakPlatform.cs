using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using WindowsPreferences;

namespace JustEnoughTools.Tweaks
{
    internal sealed partial class WindowsTweakPlatform : ITweakPlatform
    {
        readonly string build;
        readonly bool machineOnly;
        internal WindowsTweakPlatform() : this(false) { }
        internal WindowsTweakPlatform(bool machineOnly)
        {
            this.machineOnly = machineOnly;
            build = Environment.OSVersion.Version.Build.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".unknown";
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    int number, revision;
                    if (key != null && int.TryParse(Convert.ToString(key.GetValue("CurrentBuildNumber")), out number) &&
                        int.TryParse(Convert.ToString(key.GetValue("UBR")), out revision))
                        build = number.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch (Exception error) when (error is UnauthorizedAccessException || error is System.Security.SecurityException || error is IOException) { }
        }
        internal string Build { get { return build; } }
        public bool IsWindows11 { get { int number; return int.TryParse(build.Split('.')[0], out number) && number >= 22000; } }
        // Exact build/capability pairs only. An untested feature is never enabled
        // merely because a similar registry name exists on the host.
        public bool Certified(string capability) { return TweakCompatibility.Allows(build, capability); }
        public bool HasBattery
        {
            get { PowerStatus status; return GetSystemPowerStatus(out status) && status.BatteryFlag != 255 && (status.BatteryFlag & 128) == 0; }
        }
        public bool UpdateManaged
        {
            get
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate"))
                    return key != null && (key.ValueCount > 0 || key.SubKeyCount > 0);
            }
        }
        public RegistryDatum Read(bool machine, string path, string name)
        {
            using (var root = RegistryKey.OpenBaseKey(machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64))
            using (var key = root.OpenSubKey(path))
            {
                if (key == null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return new RegistryDatum();
                return new RegistryDatum { Exists = true, Value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), Kind = key.GetValueKind(name) };
            }
        }
        public void Write(bool machine, string path, string name, object value, RegistryValueKind kind)
        {
            using (var root = RegistryKey.OpenBaseKey(machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64))
            using (var key = root.CreateSubKey(path)) key.SetValue(name, value, kind);
        }
        public TrayPreferenceSnapshot ReadTrayPreference(TrayPreferenceKind kind)
        {
            using (var setting = NativeTrayPreferences.Open(kind)) return setting.Read();
        }
        public void SetTrayPreference(TrayPreferenceKind kind, bool enabled)
        {
            using (var setting = NativeTrayPreferences.Open(kind)) setting.Set(enabled);
        }
        public WindowsPreferenceSnapshot ReadWindowsPreference(WindowsPreferenceId id)
        {using(var setting=NativeWindowsPreferences.Open(id))return setting.Read();}
        public void SetWindowsPreference(WindowsPreferenceId id,object value)
        {using(var setting=NativeWindowsPreferences.Open(id))setting.Set(value);}
        const uint StickyMask = 1 | 4 | 8 | 16 | 32 | 64 | 128 | 256;
        public int ReadNative(string key)
        {
            uint number = 0;
            switch (key)
            {
                case "indexing": return (File.GetAttributes(@"C:\") & FileAttributes.NotContentIndexed) != 0 ? 1 : 0;
                case "bitlocker": return ReadBitLocker();
                case "keyboard-delay": Check(SystemParametersInfo(0x16, 0, ref number, 0)); return number == 0 ? 1 : 0;
                case "keyboard-speed": Check(SystemParametersInfo(0x0A, 0, ref number, 0)); return number == 31 ? 1 : 0;
                case "caret":
                    var persisted = Read(false, @"Control Panel\Desktop", "CursorBlinkRate");
                    return GetCaretBlinkTime() == 1200 && Equals(persisted.Value, "1200") ? 1 : 0;
                case "sticky":
                    var sticky = new Sticky { Size = (uint)Marshal.SizeOf(typeof(Sticky)) };
                    Check(StickyParameters(0x3A, sticky.Size, ref sticky, 0));
                    return (sticky.Flags & 2) == 0 ? 2 : (sticky.Flags & StickyMask) == 0 ? 1 : 0;
                case "mouse": var mouse = new int[3]; Check(MouseParameters(3, 0, mouse, 0)); return mouse[2] == 0 ? 1 : 0;
                case "autohide":
                    IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
                    if (taskbar == IntPtr.Zero) return -1;
                    var appbar = new AppBar { Size = (uint)Marshal.SizeOf(typeof(AppBar)), Window = taskbar }; return (SHAppBarMessage(4, ref appbar).ToUInt64() & 1) != 0 ? 1 : 0;
                case "theme": return !File.Exists(DarkThemePath) ? 2 : ThemeIsDark() ? 1 : 0;
                case "microsoft-update": return UpdateManaged ? 2 : MicrosoftUpdateState();
                case "search-updates": return Type.GetTypeFromProgID("Microsoft.Update.Session", false) == null ? 2 : 0;
                default: throw new ArgumentOutOfRangeException(nameof(key));
            }
        }
        public string ApplyNative(string key)
        {
            uint value;
            switch (key)
            {
                case "indexing": return IndexingOperation.Run();
                case "bitlocker": return StartDecryption();
                case "keyboard-delay": value = 0; Check(SystemParametersInfo(0x17, 0, ref value, 3)); return "Задержка повтора: минимальная.";
                case "keyboard-speed": value = 31; Check(SystemParametersInfo(0x0B, 31, ref value, 3)); return "Скорость повтора: максимальная.";
                case "caret":
                    Check(SetCaretBlinkTime(1200)); Write(false, @"Control Panel\Desktop", "CursorBlinkRate", "1200", RegistryValueKind.String);
                    return "Мерцание курсора: 1200 мс.";
                case "sticky":
                    var sticky = new Sticky { Size = (uint)Marshal.SizeOf(typeof(Sticky)) };
                    Check(StickyParameters(0x3A, sticky.Size, ref sticky, 0)); sticky.Flags &= ~StickyMask;
                    Check(StickyParameters(0x3B, sticky.Size, ref sticky, 3)); return "Залипание клавиш и его переключатели выключены.";
                case "mouse":
                    var mouse = new int[3]; Check(MouseParameters(3, 0, mouse, 0)); mouse[2] = 0;
                    Check(MouseParameters(4, 0, mouse, 3)); return "Ускорение мыши выключено; скорость сохранена.";
                case "autohide":
                    var appbar = new AppBar { Size = (uint)Marshal.SizeOf(typeof(AppBar)), Window = FindWindow("Shell_TrayWnd", null) };
                    if (appbar.Window == IntPtr.Zero) throw new InvalidOperationException("Панель задач недоступна в текущем сеансе.");
                    ulong state = SHAppBarMessage(4, ref appbar).ToUInt64(); appbar.Parameter = new IntPtr(unchecked((long)(state | 1)));
                    SHAppBarMessage(10, ref appbar); return "Автоскрытие панели задач включено.";
                case "theme": ApplyDarkTheme(); return "Применена тема «Windows (тёмная)».";
                case "search-updates": return SearchUpdates();
                case "microsoft-update": RegisterMicrosoftUpdate(); return MicrosoftUpdateState() == 3 ? "Регистрация Microsoft Update ожидает связи со службой." : "Получение обновлений продуктов Microsoft включено.";
                default: throw new ArgumentOutOfRangeException(nameof(key));
            }
        }
        int ReadBitLocker()
        {
            object shell = null, folder = null, item = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true));
                folder = shell.GetType().InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { @"C:\" });
                if (folder == null) return -1;
                item = folder.GetType().InvokeMember("Self", System.Reflection.BindingFlags.GetProperty, null, folder, null);
                object status = item.GetType().InvokeMember("ExtendedProperty", System.Reflection.BindingFlags.InvokeMethod, null, item, new object[] { "System.Volume.BitLockerProtection" });
                return BitLockerState(status, Certified("bitlocker-preprovisioned"));
            }
            finally { Release(item); Release(folder); Release(shell); }
        }
        internal static int BitLockerState(object status, bool preprovisionedVerified)
        {
            if (!(status is byte || status is sbyte || status is short || status is ushort || status is int || status is uint || status is long || status is ulong)) return -1;
            long number;
            try { number = Convert.ToInt64(status); } catch (OverflowException) { return -1; }
            switch (number)
            {
                case 2: return 1;
                case 1: case 3: case 5: return 0;
                case 8: return preprovisionedVerified ? 0 : -1;
                case 4: return 3;
                case 6: return 2;
                default: return -1;
            }
        }
        internal static readonly string DecryptionScript = @"
$ErrorActionPreference = 'Stop'
try {
 $v = Get-WmiObject -Namespace 'root\CIMV2\Security\MicrosoftVolumeEncryption' -Class Win32_EncryptableVolume -Filter 'DriveLetter=""C:""'
 if ($null -eq $v) { throw 'BitLocker volume C: is unavailable.' }
 $s = $v.GetConversionStatus(0)
 if ($s.ReturnValue -ne 0) { throw ('Cannot read conversion state: '+$s.ReturnValue) }
 if ($s.ConversionStatus -ne 0) {
   $r = $v.Decrypt()
   if ($r.ReturnValue -ne 0) { throw ('Cannot decrypt C: '+$r.ReturnValue+'; automatic-unlock keys were not removed.') }
 }
 do {
   $s = $v.GetConversionStatus(0)
   if ($s.ReturnValue -ne 0) { throw ('Cannot read conversion state: '+$s.ReturnValue) }
   $p = [Math]::Max(0,[Math]::Min(100,100-[int]$s.EncryptionPercentage))
   [Console]::Write((""`rDecrypting C: {0,3}%   "" -f $p))
   Write-Progress -Activity 'Decrypting C:' -Status ($p.ToString()+'%') -PercentComplete $p
   if ($s.ConversionStatus -eq 0) { break }
   Start-Sleep -Seconds 1
 } while ($true)
 Write-Progress -Activity 'Decrypting C:' -Completed
 exit 0
} catch { Write-Error $_ -ErrorAction Continue; exit 1 }
";
        static string StartDecryption()
        {
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\WindowsPowerShell\v1.0\powershell.exe");
            var start = new ProcessStartInfo(powershell, "-NoLogo -NoProfile -NonInteractive -OutputFormat Text -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(DecryptionScript)))
                { UseShellExecute = false, CreateNoWindow = false, WindowStyle = ProcessWindowStyle.Normal, RedirectStandardError = true };
            start.EnvironmentVariables["PSModulePath"] = Path.Combine(Path.GetDirectoryName(powershell), "Modules");
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("Не удалось открыть PowerShell.");
                var errors = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                string error = errors.GetAwaiter().GetResult();
                if (process.ExitCode != 0) throw new InvalidOperationException("Окно PowerShell закрыто или расшифровка завершилась с ошибкой. Начатую Windows расшифровку это не отменяет." +
                    (string.IsNullOrWhiteSpace(error) ? "" : "\n" + error.Substring(0, Math.Min(4096, error.Length))));
            }
            return "Диск C: полностью расшифрован.";
        }
        bool ThemeIsDark()
        {
            var current = Read(false, @"Software\Microsoft\Windows\CurrentVersion\Themes", "CurrentTheme");
            string path = Convert.ToString(current.Value);
            string expectedWallpaper = null;
            bool desktop = false;
            if (!File.Exists(DarkThemePath)) return false;
            foreach (string line in File.ReadLines(DarkThemePath))
            {
                string text = line.Trim();
                if (text.StartsWith("[", StringComparison.Ordinal)) desktop = text.Equals(@"[Control Panel\Desktop]", StringComparison.OrdinalIgnoreCase);
                if (desktop && text.StartsWith("Wallpaper=", StringComparison.OrdinalIgnoreCase)) expectedWallpaper = Environment.ExpandEnvironmentVariables(text.Substring(10).Trim());
            }
            string actualWallpaper = Environment.ExpandEnvironmentVariables(Convert.ToString(Read(false, @"Control Panel\Desktop", "WallPaper").Value));
            return string.Equals(Environment.ExpandEnvironmentVariables(path), DarkThemePath, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(expectedWallpaper) && string.Equals(expectedWallpaper, actualWallpaper, StringComparison.OrdinalIgnoreCase) &&
                Equals(Read(false, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme").Value, 0) &&
                Equals(Read(false, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme").Value, 0);
        }
        static string DarkThemePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Resources\Themes\dark.theme"); } }
        static void ApplyDarkTheme()
        {
            if (!File.Exists(DarkThemePath)) throw new FileNotFoundException("Встроенная тёмная тема не найдена.");
            RunSta(delegate
            {
                object instance = null;
                try
                {
                    instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("C04B329E-5823-4415-9C93-BA44688947B0"), true));
                    ((IThemeManager)instance).ApplyTheme(DarkThemePath);
                }
                finally { Release(instance); }
            });
        }
        static string SearchUpdates()
        {
            using (var updates = new ComWindowsUpdateBatch())
            {
                var result = WindowsUpdateOperation.Run(updates);
                string message = result.Describe();
                if (result.Errors.Count != 0) throw new InvalidOperationException(message);
                return message;
            }
        }
        const string MicrosoftUpdateId = "7971f918-a847-4430-9279-4a52d1efe18d";
        static int MicrosoftUpdateState()
        {
            object manager = null, services = null, registration = null;
            try
            {
                manager = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.ServiceManager", true));
                services = ((dynamic)manager).Services;
                for (int index = 0; index < (int)((dynamic)services).Count; index++)
                {
                    object service = ((dynamic)services).Item(index);
                    try
                    {
                        if (string.Equals((string)((dynamic)service).ServiceID, MicrosoftUpdateId, StringComparison.OrdinalIgnoreCase))
                            if ((bool)((dynamic)service).IsRegisteredWithAU) return 1;
                    }
                    finally { Release(service); }
                }
                try
                {
                    registration = ((dynamic)manager).QueryServiceRegistration(MicrosoftUpdateId);
                    return (int)((dynamic)registration).RegistrationState == 2 ? 3 : 0;
                }
                catch (COMException error) when (unchecked((uint)error.ErrorCode) == 0x80248014) { return 0; }
            }
            finally { Release(registration); Release(services); Release(manager); }
        }
        static void RegisterMicrosoftUpdate()
        {
            object manager = null, registration = null;
            try
            {
                manager = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.ServiceManager", true));
                ((dynamic)manager).ClientApplicationID = "JustEnoughTools";
                registration = ((dynamic)manager).AddService2(MicrosoftUpdateId, 7, "");
                int state = MicrosoftUpdateState();
                if (state != 1 && state != 3) throw new InvalidOperationException("Регистрация Microsoft Update пока не подтверждена.");
            }
            finally { Release(registration); Release(manager); }
        }
        internal static void RunSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            if (failure != null) throw new InvalidOperationException(failure.Message, failure);
        }
        internal static void Release(object instance) { if (instance != null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance); }
        static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        [StructLayout(LayoutKind.Sequential)] struct Sticky { internal uint Size, Flags; }
        [StructLayout(LayoutKind.Sequential)] struct PowerStatus { internal byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; internal uint BatteryLifeTime, BatteryFullLifeTime; }
        [StructLayout(LayoutKind.Sequential)] struct AppBar { internal uint Size; internal IntPtr Window; internal uint Callback, Edge; internal int Left, Top, Right, Bottom; internal IntPtr Parameter; }
        [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint parameter, ref uint value, uint flags);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)] static extern bool StickyParameters(uint action, uint parameter, ref Sticky value, uint flags);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)] static extern bool MouseParameters(uint action, uint parameter, [In, Out] int[] value, uint flags);
        [DllImport("user32.dll")] static extern uint GetCaretBlinkTime();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetCaretBlinkTime(uint value);
        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out PowerStatus status);
        [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint message, ref AppBar data);
        [ComImport, Guid("0646EBBE-C1B7-4045-8FD0-FFD65D3FC792"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IThemeManager
        {
            [return: MarshalAs(UnmanagedType.Interface)] object GetCurrentTheme();
            void ApplyTheme([MarshalAs(UnmanagedType.BStr)] string path);
        }
    }

    // WUA runs synchronously on the tweak's worker. Only this fixed operation is
    // exposed to the administrative worker; no update IDs or commands cross IPC.
    internal interface IWindowsUpdateItem
    {
        string Title { get; }
        bool Downloaded { get; }
        bool EulaAccepted { get; }
        void AcceptEula();
    }
    internal sealed class WindowsUpdateStepResult
    {
        internal int Code, HResult;
        internal bool RebootRequired;
        internal bool Succeeded { get { return Code == 2 && HResult >= 0; } }
    }
    internal interface IWindowsUpdateBatch : IDisposable
    {
        IList<IWindowsUpdateItem> Search();
        WindowsUpdateStepResult Download(IWindowsUpdateItem update);
        WindowsUpdateStepResult Install(IWindowsUpdateItem update);
    }
    internal sealed class WindowsUpdateRunResult
    {
        internal int Found, Downloaded, Installed;
        internal bool RebootRequired;
        internal readonly List<string> Errors = new List<string>();
        internal string Describe()
        {
            string text = Found == 0 ? "Доступных обновлений нет." :
                "Обновлений найдено: " + Found + "; загружено: " + Downloaded + "; установлено: " + Installed + ".";
            if (RebootRequired) text += " Для завершения требуется перезагрузка; автоматически она не запускается.";
            if (Errors.Count != 0) text += " Ошибок: " + Errors.Count + ". " + string.Join("; ", Errors.Take(5));
            return text;
        }
    }
    internal static class WindowsUpdateOperation
    {
        internal const string Criteria = "IsInstalled=0 and IsHidden=0";
        static string Failure(IWindowsUpdateItem update,Exception error)
        {
            string title;
            try{title=update.Title;}catch{title="Обновление";}
            string text=title+": "+error.Message;
            return text.Length>1000?text.Substring(0,1000):text;
        }
        internal static WindowsUpdateRunResult Run(IWindowsUpdateBatch batch)
        {
            var result = new WindowsUpdateRunResult();
            // No Type, BrowseOnly, IsAssigned or AutoSelect filter: optional
            // software and driver updates offered by the configured service count.
            var updates = batch.Search(); result.Found = updates.Count;
            var ready = new List<IWindowsUpdateItem>();
            foreach (var update in updates)
            {
                try
                {
                    if (!update.EulaAccepted) update.AcceptEula();
                    if (!update.Downloaded)
                    {
                        var step = batch.Download(update);
                        if (!step.Succeeded || !update.Downloaded)
                            throw new InvalidOperationException("Ошибка загрузки (0x" + unchecked((uint)step.HResult).ToString("X8") + ").");
                    }
                    result.Downloaded++; ready.Add(update);
                }
                catch (Exception error) { result.Errors.Add(Failure(update,error)); }
            }
            // Install separately so an exclusive update or one failed package
            // cannot prevent every other downloaded package being attempted.
            foreach (var update in ready)
            {
                try
                {
                    var step = batch.Install(update); result.RebootRequired |= step.RebootRequired;
                    if (!step.Succeeded)
                        throw new InvalidOperationException("Ошибка установки (0x" + unchecked((uint)step.HResult).ToString("X8") + ").");
                    result.Installed++;
                }
                catch (Exception error) { result.Errors.Add(Failure(update,error)); }
            }
            return result;
        }
    }
    internal sealed class ComWindowsUpdateBatch : IWindowsUpdateBatch
    {
        object session, searcher, searchResult, collection;
        readonly List<ComUpdate> owned = new List<ComUpdate>();
        internal ComWindowsUpdateBatch()
        {
            session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session", true));
            ((dynamic)session).ClientApplicationID = "JustEnoughTools";
        }
        public IList<IWindowsUpdateItem> Search()
        {
            searcher = ((dynamic)session).CreateUpdateSearcher();
            ((dynamic)searcher).Online = true;
            searchResult = ((dynamic)searcher).Search(WindowsUpdateOperation.Criteria);
            if ((int)((dynamic)searchResult).ResultCode != 2)
                throw new InvalidOperationException("Проверка обновлений Windows не завершилась успешно.");
            collection = ((dynamic)searchResult).Updates;
            var result = new List<IWindowsUpdateItem>();
            for (int index = 0; index < (int)((dynamic)collection).Count; index++)
            {
                var update = new ComUpdate(((dynamic)collection).Item(index));
                owned.Add(update); result.Add(update);
            }
            return result;
        }
        public WindowsUpdateStepResult Download(IWindowsUpdateItem update) { return Execute((ComUpdate)update, false); }
        public WindowsUpdateStepResult Install(IWindowsUpdateItem update) { return Execute((ComUpdate)update, true); }
        WindowsUpdateStepResult Execute(ComUpdate update, bool install)
        {
            object group = null, worker = null, result = null, detail = null;
            try
            {
                group = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl", true));
                ((dynamic)group).Add(update.Value);
                worker = install ? ((dynamic)session).CreateUpdateInstaller() : ((dynamic)session).CreateUpdateDownloader();
                if (install)
                {
                    // Never invoke RunWizard, restart the machine or force an
                    // unavailable update. Unsupported silent packages report an error.
                    ((dynamic)worker).AllowSourcePrompts = false;
                    ((dynamic)worker).ForceQuiet = true;
                    ((dynamic)worker).AttemptCloseAppsIfNecessary = false;
                    if ((bool)((dynamic)worker).IsBusy)
                        throw new InvalidOperationException("Windows уже устанавливает обновление. Повторите позже.");
                    if ((bool)((dynamic)worker).RebootRequiredBeforeInstallation)
                        return new WindowsUpdateStepResult { Code = 4, HResult = unchecked((int)0x80240016), RebootRequired = true };
                }
                ((dynamic)worker).Updates = group;
                result = install ? ((dynamic)worker).Install() : ((dynamic)worker).Download();
                detail = ((dynamic)result).GetUpdateResult(0);
                return new WindowsUpdateStepResult
                {
                    Code = (int)((dynamic)detail).ResultCode, HResult = (int)((dynamic)detail).HResult,
                    RebootRequired = install && ((bool)((dynamic)result).RebootRequired || (bool)((dynamic)detail).RebootRequired)
                };
            }
            finally { WindowsTweakPlatform.Release(detail); WindowsTweakPlatform.Release(result); WindowsTweakPlatform.Release(worker); WindowsTweakPlatform.Release(group); }
        }
        public void Dispose()
        {
            foreach (var item in owned) WindowsTweakPlatform.Release(item.Value);
            owned.Clear(); WindowsTweakPlatform.Release(collection); WindowsTweakPlatform.Release(searchResult);
            WindowsTweakPlatform.Release(searcher); WindowsTweakPlatform.Release(session);
            collection = searchResult = searcher = session = null;
        }
        sealed class ComUpdate : IWindowsUpdateItem
        {
            internal readonly object Value;
            internal ComUpdate(object value) { Value = value; }
            public string Title { get { return (string)((dynamic)Value).Title; } }
            public bool Downloaded { get { return (bool)((dynamic)Value).IsDownloaded; } }
            public bool EulaAccepted { get { return (bool)((dynamic)Value).EulaAccepted; } }
            public void AcceptEula() { ((dynamic)Value).AcceptEula(); }
        }
    }

    internal static class TweakCompatibility
    {
        // Verified Windows mappings; guest and explicitly approved host evidence are separated in Tests/TWEAKER.md.
        static readonly HashSet<string> Verified = new HashSet<string>(StringComparer.Ordinal)
        {
            "theme", "clipboard:EnableClipboardHistory", "new-start:ShowRecentList",
            "start:Start_TrackDocs", "start:Start_IrisRecommendations", "start:Start_AccountNotifications", "start:VisiblePlaces",
            "taskbar:ShowTaskViewButton", "taskbar:SearchboxTaskbarMode", "emoji:EmojiAndMoreIconVisibilityState",
            "touch-keyboard:TipbandDesiredVisibility",
            "clock:ShowClockInNotificationCenter",
            "windows-update:IsContinuousInnovationOptedIn", "windows-update:AllowMUUpdateService",
            "windows-update:IsExpedited", "windows-update:AllowAutoWindowsUpdateDownloadOverMeteredNetwork",
            "windows-update:RestartNotificationsAllowed2"
        };
        static readonly HashSet<string> Verified9711 = new HashSet<string>(StringComparer.Ordinal)
        {
            "theme", "startup", "bitlocker-preprovisioned", "clipboard:EnableClipboardHistory", "new-start:ShowRecentList",
            "new-start:ShowPinnedSection", "new-start:ShowRecentSection", "new-start:ShowAllAppsSection",
            "new-start:StartMenuSize", "new-start:HideUserInformation",
            "start:Start_TrackDocs", "start:Start_IrisRecommendations", "start:Start_AccountNotifications", "start:VisiblePlaces",
            "taskbar:ShowTaskViewButton", "taskbar:SearchboxTaskbarMode", "taskbar:TaskbarAl",
            "taskbar:TaskbarBadges", "taskbar:TaskbarFlashing", "taskbar:MMTaskbarMode",
            "taskbar:TaskbarGlomLevel", "taskbar:MMTaskbarGlomLevel", "taskbar:TaskbarSn", "taskbar:TaskbarSd",
            "taskbar:MMTaskbarEnabled", "taskbar:ExpandableTaskbar", "taskbar:TouchpadDesiredVisibility", "tray:SystemTrayChevronVisibility",
            "new-taskbar:TaskbarLocation", "new-taskbar:TaskbarSize", "new-taskbar:IconSizePreference",
            "resume:IsEnabled",
            "battery:IsBatteryPercentageEnabled",
            "emoji:EmojiAndMoreIconVisibilityState", "taskbar:PenWorkspaceButtonDesiredVisibility",
            "touch-keyboard:TipbandDesiredVisibility", "clock:ShowSystrayDateTimeValueName", "clock:ShowSecondsInSystemClock", "clock:ShowClockInNotificationCenter", "clock:Enable",
            "windows-update:IsContinuousInnovationOptedIn", "windows-update:AllowMUUpdateService",
            "windows-update:IsExpedited", "windows-update:AllowAutoWindowsUpdateDownloadOverMeteredNetwork",
            "windows-update:RestartNotificationsAllowed2"
        };
        internal static bool Allows(string build, string capability)
        {
            WindowsPreferenceId id;
            if(build=="26300.9711"&&capability.StartsWith("broker:",StringComparison.Ordinal)&&
                Enum.TryParse(capability.Substring(7),out id)&&Enum.IsDefined(typeof(WindowsPreferenceId),id))return true;
            return build == "26300.9457" ? Verified.Contains(capability) : build == "26300.9711" && Verified9711.Contains(capability);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JustEnoughTools.Tweaks;
using Microsoft.Win32;
using WindowsPreferences;

namespace JustEnoughTools.Tests
{
    public static class TweakTests
    {
        static int passed;
        public static int RunAll()
        {
            passed = 0;
            Check(WindowsTweakPlatform.BitLockerState(8, true) == 0 && WindowsTweakPlatform.BitLockerState(8, false) == -1,
                "A verified pre-provisioned encrypted volume needs decryption; an unverified interpretation must stay unknown.");
            Check(WindowsTweakPlatform.BitLockerState(null, true) == -1 && WindowsTweakPlatform.BitLockerState(0, true) == -1 &&
                WindowsTweakPlatform.BitLockerState(99, true) == -1 && WindowsTweakPlatform.BitLockerState("2", true) == -1 &&
                WindowsTweakPlatform.BitLockerState(1.5, true) == -1 && WindowsTweakPlatform.BitLockerState(true, true) == -1 &&
                WindowsTweakPlatform.BitLockerState(ulong.MaxValue, true) == -1,
                "Missing, unrecognised and unexpected property types must never mean fully decrypted.");
            Check(WindowsTweakPlatform.BitLockerState(2, true) == 1 && WindowsTweakPlatform.BitLockerState(4, true) == 3 &&
                WindowsTweakPlatform.BitLockerState(6, true) == 2,
                "Fully decrypted, converting and locked volumes must retain distinct states.");
            Check(TweakCompatibility.Allows("26300.9711", "new-taskbar:IconSizePreference") &&
                !TweakCompatibility.Allows("26300.9712", "new-taskbar:IconSizePreference") &&
                !TweakCompatibility.Allows("26300.9711", "resume:IsResumeAllowed"),
                "Verification evidence must allow only the exact verified build and capability.");
            Check(new[] { "new-start:ShowPinnedSection", "new-start:ShowRecentSection", "new-start:ShowAllAppsSection",
                "new-start:StartMenuSize", "new-start:HideUserInformation", "taskbar:MMTaskbarEnabled",
                "taskbar:ExpandableTaskbar", "taskbar:TouchpadDesiredVisibility", "tray:SystemTrayChevronVisibility" }
                .All(capability => TweakCompatibility.Allows("26300.9711", capability) && !TweakCompatibility.Allows("26300.9457", capability)) &&
                TweakCompatibility.Allows("26300.9711", "battery:IsBatteryPercentageEnabled"),
                "Approved host evidence must enable only confirmed build and capability mappings.");
            var tipRow = new TweakRow { Id = TweakId.Clock };
            tipRow.Parts.Add(new TweakPart("long-internal-key", TweakState.Unavailable, reason: "Внутренняя поднастройка: не проверено для этой сборки Windows. " + new string('x', 500)));
            string tooltip = TweakUi.BlockedTooltip(tipRow, true);
            Check(tooltip.Length < 100 && tooltip.Contains("сборки Windows") && !tooltip.Contains("long-internal-key"),
                "Blocked tooltips must explain the cause concisely without dumping subaction details.");
            tipRow.Parts.Clear(); tipRow.Parts.Add(new TweakPart("done", TweakState.Applied));
            Check(TweakUi.BlockedTooltip(tipRow, true).Contains("Уже настроено") &&
                TweakUi.BlockedTooltip(tipRow, false).Contains("другая операция"),
                "Tooltips must change with the current setting state and application operation state.");
            Check(TweakUi.SettingHint(TweakId.Mouse, tipRow).Contains("Для шутеров") &&
                TweakUi.BlockedTooltip(null, true).Contains("Проверка"),
                "Useful hints and the initial disabled reason must remain available without row statuses.");
            using (TweakActionLease.Take(TweakId.Indexing))
                Check(Task.Run(() => { try { using (TweakActionLease.Take(TweakId.Indexing)) return false; } catch (InvalidOperationException) { return true; } }).GetAwaiter().GetResult(),
                    "Conflicting instances must not mutate Windows concurrently.");
            Check(Task.Run(() => { using (TweakActionLease.Take(TweakId.Indexing)) return true; }).GetAwaiter().GetResult(),
                "The action lease must be released after completion.");
            var backend = new FakeBackend();
            var engine = new TweakEngine(backend);
            var snapshot = engine.InspectAll();
            Check(snapshot.Rows.Count == 13 && snapshot.CanApply, "All fixed rows must be independently inspected.");
            backend.States[TweakId.Keyboard] = new[] { new TweakPart("ready", TweakState.Applied), new TweakPart("unknown", TweakState.Unknown), new TweakPart("absent", TweakState.Unavailable), new TweakPart("running", TweakState.Running, true) };
            engine.Apply(new[] { TweakId.Keyboard }, TweakScope.User);
            Check(backend.Applied.Count == 0 && !engine.Inspect(TweakId.Keyboard).CanApply, "Ready, unknown and absent parts must never be applied.");
            backend.States[TweakId.Keyboard] = new[] { new TweakPart("bad", TweakState.Pending), new TweakPart("good", TweakState.Pending), new TweakPart("admin", TweakState.Pending, true) };
            backend.Throw = "bad";
            var result = engine.Apply(new[] { TweakId.Keyboard }, TweakScope.User);
            Check(result[0].Parts.Count == 2 && result[0].Parts[0].State == TweakState.Failed && backend.Applied.Contains("good") && !backend.Applied.Contains("admin"), "Failure must not stop independent user parts or cross the elevation boundary.");
            backend.Applied.Clear();
            engine.Apply(new[] { TweakId.Keyboard }, TweakScope.Administrator);
            Check(backend.Applied.SequenceEqual(new[] { "admin" }), "Machine phase must not change another administrator's user settings.");
            int elevations = 0;
            backend = new FakeBackend(); engine = new TweakEngine(backend);
            backend.States[TweakId.Indexing] = new[] { new TweakPart("disk", TweakState.Pending, true) };
            backend.States[TweakId.BitLocker] = new[] { new TweakPart("decrypt", TweakState.Pending, true) };
            var service = new TweakService(TestContext.ExecutablePath, engine, ids =>
            { elevations++; Check(ids.SequenceEqual(new[] { TweakId.Indexing, TweakId.BitLocker }), "One request must contain the whole pending administrative batch."); return new List<TweakRow>(); });
            service.ApplyAsync(new[] { TweakId.Indexing, TweakId.BitLocker, TweakId.Mouse }).GetAwaiter().GetResult();
            Check(elevations == 1 && backend.Applied.Contains("user"), "Batch must elevate once and apply user settings locally.");
            backend.States[TweakId.Indexing] = new[] { new TweakPart("unknown", TweakState.Unknown, true) };
            service.ApplyAsync(new[] { TweakId.Indexing }).GetAwaiter().GetResult();
            Check(elevations == 1, "Unreadable state must never cause elevation.");
            service = new TweakService(TestContext.ExecutablePath, engine, ids => { throw new InvalidOperationException("cancelled"); });
            var cancelled = service.ApplyAsync(new[] { TweakId.BitLocker, TweakId.Mouse }).GetAwaiter().GetResult();
            Check(cancelled.Message == "cancelled" && cancelled.Snapshot != null, "UAC cancellation must preserve local results and refresh state.");

            var updateBackend=new FakeBackend();
            updateBackend.States[TweakId.WindowsUpdate]=new[]{new TweakPart("preference",TweakState.Pending),new TweakPart("search-updates",TweakState.Pending,true)};
            var updateEngine=new TweakEngine(updateBackend);
            updateEngine.Apply(new[]{TweakId.WindowsUpdate},TweakScope.User,true);
            updateEngine.Apply(new[]{TweakId.WindowsUpdate},TweakScope.Administrator,updateSearchOnly:true);
            Check(updateBackend.Applied.Count(p=>p=="preference")==1&&updateBackend.Applied.Count(p=>p=="search-updates")==1,
                "The elevated installation phase must not replay user preferences.");

            var platform = new FakePlatform(); var windows = new WindowsTweakBackend(platform);
            var keyboard = windows.Inspect(TweakId.Keyboard);
            Check(keyboard.Count == 3 && keyboard.All(p => p.State == TweakState.Pending && !p.Administrator), "Input settings are independent user actions.");
            foreach (var p in keyboard) windows.Apply(TweakId.Keyboard, p.Key);
            Check(windows.Inspect(TweakId.Keyboard).All(p => p.State == TweakState.Applied), "Native setters require actual readback.");
            Check(windows.Inspect(TweakId.Keyboard).All(p => p.Reason.IndexOf("уже настроено",StringComparison.OrdinalIgnoreCase)>=0),
                "Native actions must explain their disabled state in tooltips after readback.");
            platform.Lie = true;
            MustFail(() => windows.Apply(TweakId.Mouse, "mouse"), "A setter that did not change effective input state must fail.");
            platform.Lie = false;
            windows.Apply(TweakId.WindowsUpdate, "search-updates");
            windows.Apply(TweakId.WindowsUpdate, "search-updates");
            Check(platform.Searches == 1 && windows.Inspect(TweakId.WindowsUpdate).Single(p => p.Key == "search-updates").State == TweakState.Applied, "Windows Update search runs once per session after success.");
            platform.CertifiedValue = false;
            Check(windows.Inspect(TweakId.Start).All(p => p.State == TweakState.Unavailable), "Unverified registry adapters must never become available.");
            int storedCount = platform.Values.Count;
            Check(windows.Apply(TweakId.Start, "VisiblePlaces").State == TweakState.Unavailable && platform.Values.Count == storedCount,
                "Unverified adapters must refuse their setter even when called directly through the engine.");
            platform.CertifiedValue = true;
            platform.Values[TweakSettings.Advanced + "/TaskbarAl"] = new RegistryDatum { Exists = true, Value = "1", Kind = RegistryValueKind.String };
            platform.Preferences[WindowsPreferenceId.TaskbarAlignment]=new WindowsPreferenceSnapshot("unknown",true,true,false,false);
            Check(windows.Apply(TweakId.Taskbar, "TaskbarAl").State == TweakState.Unknown && Equals(platform.Values[TweakSettings.Advanced + "/TaskbarAl"].Value, "1"),
                "An unexpected broker type must stay unknown; a legacy preference must not be used as a fallback.");
            Check(windows.Inspect(TweakId.WindowsUpdate).Single(p => p.Key == "IsExpedited").State == TweakState.Applied,
                "A known disabled Windows default must not cause an unnecessary administrative write.");
            var start = windows.Inspect(TweakId.Start);
            var folderSettings=TweakSettings.All.Where(s=>s.Id==TweakId.Start&&s.Key.StartsWith("place-")).ToArray();
            foreach(var folder in folderSettings)windows.Apply(TweakId.Start,folder.Key);
            Check(folderSettings.Length==9&&folderSettings.Count(s=>(bool)s.PreferenceTarget)==2&&
                folderSettings.All(s=>Equals(platform.ReadWindowsPreference(s.Preference.Value).Value,s.PreferenceTarget))&&platform.Writes==0,
                "Start folders must use nine independent broker switches, with only Settings and Explorer enabled.");
            platform.UpdateManagedValue = true;
            Check(windows.Inspect(TweakId.WindowsUpdate).Where(p => p.Key != "search-updates" && p.Key != "microsoft-update").All(p => p.State == TweakState.Unavailable), "Managed update preferences must not be overwritten.");
            platform.Values[@"Software\Policies\Microsoft\Windows\System/AllowClipboardHistory"] = new RegistryDatum { Exists = true, Value = 0, Kind = RegistryValueKind.DWord };
            Check(!new TweakEngine(windows).Inspect(TweakId.Clipboard).CanApply, "Clipboard policy must be respected.");

            var trayPlatform = new FakePlatform(); var trayBackend = new WindowsTweakBackend(trayPlatform);
            Check(trayBackend.Apply(TweakId.Battery, "IsBatteryPercentageEnabled").State == TweakState.Unavailable &&
                trayPlatform.Writes == 0 && trayPlatform.TrayWrites == 0 && trayPlatform.TrayReads == 0,
                "A device without a battery must not query or change its broker preference.");
            trayPlatform.HasBatteryValue = true;
            trayPlatform.Values[TweakSettings.Advanced + "/IsBatteryPercentageEnabled"] = new RegistryDatum { Exists = true, Value = 1, Kind = RegistryValueKind.DWord };
            Check(trayBackend.Inspect(TweakId.Battery).Single().State == TweakState.Pending,
                "A matching legacy registry preference must never override the actual broker state.");
            var batteryResult = trayBackend.Apply(TweakId.Battery, "IsBatteryPercentageEnabled");
            Check(batteryResult.State == TweakState.Applied && !batteryResult.Administrator && trayPlatform.TrayWrites == 1 && trayPlatform.Writes == 0,
                "Battery state must be set and verified through the broker without a registry write or elevation.");
            trayBackend.Apply(TweakId.Battery, "IsBatteryPercentageEnabled");
            Check(trayPlatform.TrayWrites == 1, "A confirmed broker state must not be applied again.");
            var secondsResult = trayBackend.Apply(TweakId.Clock, "ShowSecondsInSystemClock");
            trayBackend.Apply(TweakId.Clock, "ShowSecondsInSystemClock");
            Check(secondsResult.State == TweakState.Applied && trayPlatform.TrayWrites == 2 && trayPlatform.Writes == 0,
                "Clock seconds must use their separate broker setting without legacy writes or repeated execution.");
            trayPlatform.TrayStates[TrayPreferenceKind.BatteryPercentage] = new TrayPreferenceSnapshot(false, true, false, true, false);
            Check(trayBackend.Apply(TweakId.Battery, "IsBatteryPercentageEnabled").State == TweakState.Unavailable && trayPlatform.TrayWrites == 2,
                "A broker policy must prevent the setter even when the old registry suggests availability.");
            trayPlatform.TrayStates[TrayPreferenceKind.BatteryPercentage] = new TrayPreferenceSnapshot(false, false, true, false, false);
            Check(trayBackend.Inspect(TweakId.Battery).Single().State == TweakState.Unavailable,
                "An inapplicable broker feature must remain unavailable.");
            trayPlatform.TrayStates[TrayPreferenceKind.BatteryPercentage] = new TrayPreferenceSnapshot(false, true, true, false, true);
            Check(trayBackend.Apply(TweakId.Battery, "IsBatteryPercentageEnabled").State == TweakState.Running && trayPlatform.TrayWrites == 2,
                "An updating preference must not be applied concurrently.");
            trayPlatform.TrayReadFails = true;
            Check(trayBackend.Apply(TweakId.Battery, "IsBatteryPercentageEnabled").State == TweakState.Unknown && trayPlatform.TrayWrites == 2,
                "Broker read failures must stay unknown and never fall back to registry or elevation.");
            trayPlatform.TrayReadFails = false; trayPlatform.IgnoreTrayWrites = true;
            trayPlatform.TrayStates[TrayPreferenceKind.BatteryPercentage] = new TrayPreferenceSnapshot(false, true, true, false, false);
            MustFail(() => trayBackend.Apply(TweakId.Battery, "IsBatteryPercentageEnabled"), "A broker setter without effective readback must fail.");
            trayPlatform.TrayStates[TrayPreferenceKind.PenWorkspace] = new TrayPreferenceSnapshot(true, true, true, false, false);
            Check(trayBackend.Inspect(TweakId.Taskbar).Single(part => part.Key == "PenWorkspaceButtonDesiredVisibility").State == TweakState.Applied,
                "An absent legacy pen value must not override the broker's actual enabled hardware-dependent default.");

            var modePlatform=new FakePlatform();var modeBackend=new WindowsTweakBackend(modePlatform);
            Check(modeBackend.Apply(TweakId.Taskbar,"MMTaskbarMode").State==TweakState.Applied&&Equals(modePlatform.ReadWindowsPreference(WindowsPreferenceId.TaskbarMultiMonitorMode).Value,"window"),
                "Taskbar mode must use the current string model and its third choice, not an index from the deprecated handler.");
            var legacyPlatform=new FakePlatform {BrokerCertifiedValue=false};var legacyBackend=new WindowsTweakBackend(legacyPlatform);
            Check(legacyBackend.Apply(TweakId.Keyboard,"keyboard-delay").State==TweakState.Applied&&legacyBackend.Apply(TweakId.Mouse,"mouse").State==TweakState.Applied,
                "Documented SPI settings must remain available when the new broker mapping is not certified.");
            Check(legacyBackend.Apply(TweakId.StickyKeys,"sticky").State==TweakState.Applied,"An empty broker child set must not falsely mark native Sticky Keys applied.");
            var legacyClipboard=legacyBackend.Apply(TweakId.Clipboard,"EnableClipboardHistory");
            Check(legacyClipboard.State==TweakState.Deferred&&legacyPlatform.Writes==1&&legacyPlatform.PreferenceWrites==0,
                "A previously certified legacy preference must survive migration without enabling an unverified broker.");
            Check(legacyBackend.Inspect(TweakId.Start).Any(p=>p.Key=="VisiblePlaces"),"Legacy Start folders must retain their certified fixed format.");
            var legacyUpdate=legacyBackend.Inspect(TweakId.WindowsUpdate).Single(p=>p.Key=="IsContinuousInnovationOptedIn");
            Check(legacyUpdate.Administrator&&legacyUpdate.State==TweakState.Pending,"Legacy HKLM update preferences must preserve their administrative scope.");

            var brokerPlatform=new FakePlatform();var brokerBackend=new WindowsTweakBackend(brokerPlatform);
            Check(brokerBackend.Apply(TweakId.Taskbar,"widgets").State==TweakState.Applied && brokerPlatform.PreferenceWrites==1 && brokerPlatform.Writes==0,
                "Widgets must use its broker, without protected registry writes or policies.");
            brokerBackend.Apply(TweakId.Taskbar,"widgets");Check(brokerPlatform.PreferenceWrites==1,"Confirmed Widgets must not be applied again.");
            brokerPlatform.Preferences[WindowsPreferenceId.TaskbarBadges]=new WindowsPreferenceSnapshot(false,true,true,true,false);
            Check(brokerBackend.Apply(TweakId.Taskbar,"TaskbarBadges").State==TweakState.Unavailable&&brokerPlatform.PreferenceWrites==1,
                "A taskbar policy must never fall back to the old native setter.");
            brokerPlatform.Preferences[WindowsPreferenceId.TaskbarSearch]=new WindowsPreferenceSnapshot(2,true,true,false,true);
            Check(brokerBackend.Apply(TweakId.Taskbar,"SearchboxTaskbarMode").State==TweakState.Running&&brokerPlatform.PreferenceWrites==1,
                "An updating list cannot be changed concurrently.");
            brokerPlatform.Preferences[WindowsPreferenceId.StartSettings]=new WindowsPreferenceSnapshot(null,false,false,false,false);
            Check(brokerBackend.Inspect(TweakId.Start).Single(p=>p.Key=="place-StartSettings").State==TweakState.Unavailable,
                "An unavailable child must be skipped independently of other Start folders.");
            foreach(var setting in TweakSettings.All.Where(s=>s.Preference.HasValue&&!s.PreferenceChoice.HasValue))WindowsPreferenceCatalog.Get(setting.Preference.Value).Validate(setting.PreferenceTarget);
            Check(TweakSettings.All.Where(s=>s.Id==TweakId.WindowsUpdate).All(s=>!s.Administrator),"User update broker settings must stay in the original user process.");
            Check(!TweakCompatibility.Allows("26300.9711","broker:9999")&&!TweakCompatibility.Allows("26300.9712","broker:Widgets"),"Unknown identifiers/builds must not gain broker certification.");

            foreach (var ids in new[] { new[] { TweakId.Indexing }, new[] { TweakId.Indexing, TweakId.BitLocker, TweakId.WindowsUpdate, TweakId.Startup } })
                Check(TweakWorker.DecodeRequest(TweakWorker.EncodeRequest(ids)).SequenceEqual(ids), "Fixed worker requests must round trip.");
            MustFail(() => TweakWorker.EncodeRequest(new[] { TweakId.Mouse }), "Administrative IPC must reject user operations.");
            MustFail(() => TweakWorker.EncodeRequest(new[] { TweakId.Indexing, TweakId.Indexing }), "Administrative IPC must reject duplicate operations.");
            MustFail(() => TweakWorker.DecodeRequest(System.Text.Encoding.ASCII.GetBytes("JET-TWEAKS-1|999")), "Unknown identifiers must not reach a backend.");
            MustFail(() => TweakWorker.DecodeRequest(new byte[129]), "Oversized requests must fail before execution.");
            var rows = new List<TweakRow> { new TweakRow { Id = TweakId.Indexing, Parts = new List<TweakPart> { new TweakPart("indexing", TweakState.Applied, true, "done") } } };
            Check(TweakWorker.DecodeResult(TweakWorker.EncodeResult(rows), new[] { TweakId.Indexing })[0].Parts[0].State == TweakState.Applied, "Worker results must validate and round trip.");
            MustFail(() => TweakWorker.DecodeResult(TweakWorker.EncodeResult(rows), new[] { TweakId.BitLocker }), "Unexpected report actions must be rejected.");

            foreach (uint code in new uint[] { 2, 6 })
            {
                var bytes = new byte[12]; Buffer.BlockCopy(BitConverter.GetBytes(code), 0, bytes, 0, 4);
                var value = new RegistryDatum { Exists = true, Value = bytes, Kind = RegistryValueKind.Binary };
                var disabled = WindowsTweakPlatform.DisabledStartupBytes(value, 123456);
                Check(BitConverter.ToUInt32(disabled, 0) == code + 1 && BitConverter.ToInt64(disabled, 4) == 123456 && BitConverter.ToUInt32(bytes, 0) == code,
                    "Startup disable must preserve state family, record time and not mutate the old value.");
            }
            Check(WindowsTweakPlatform.ClassicStartupState(new RegistryDatum { Exists = true, Value = new byte[3], Kind = RegistryValueKind.Binary }) == TweakState.Unknown, "Malformed approval values are not enabled or disabled.");
            var files = new FakeFiles(); var progress = new FakeProgress();
            string indexing = IndexingOperation.Process(files, progress);
            Check(progress.Disposed && files.Changed.Last() == @"C:\" && files.Changed.Contains(@"C:\ok") && !files.Changed.Contains(@"C:\link"), "Traversal must close progress, skip reparse points, preserve attributes and commit root last.");
            Check(indexing.Contains("пропущено: 2"), "Per-file failures must be skipped without stopping traversal.");
            files = new FakeFiles(); progress = new FakeProgress { Stop = true };
            MustFail(() => IndexingOperation.Process(files, progress), "Cancelled traversal must not claim completion.");
            Check(progress.Disposed && !files.Changed.Contains(@"C:\"), "Cancellation must close progress without setting the root indicator.");
            Check(WindowsTweakPlatform.DecryptionScript.Contains("$s.ConversionStatus -eq 0") && !WindowsTweakPlatform.DecryptionScript.Contains("ClearAllAutoUnlockKeys") && !WindowsTweakPlatform.DecryptionScript.Contains("Remove-"), "Decryption must use conversion state and never remove other-drive unlock keys.");
            return passed;
        }
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); passed++; }
        static void MustFail(Action action, string message) { bool failed = false; try { action(); } catch { failed = true; } Check(failed, message); }
        sealed class FakeBackend : ITweakBackend
        {
            internal Dictionary<TweakId, TweakPart[]> States = new Dictionary<TweakId, TweakPart[]>();
            internal List<string> Applied = new List<string>(); internal string Throw;
            public IReadOnlyList<TweakPart> Inspect(TweakId id) { return States.ContainsKey(id) ? States[id] : new[] { new TweakPart("user", TweakState.Pending) }; }
            public TweakPart Apply(TweakId id, string part) { if (part == Throw) throw new IOException("test"); Applied.Add(part); return new TweakPart(part, TweakState.Applied); }
        }
        sealed class FakePlatform : ITweakPlatform
        {
            internal bool BrokerCertifiedValue=true;
            internal bool CertifiedValue = true, UpdateManagedValue, Lie, HasBatteryValue, TrayReadFails, IgnoreTrayWrites;
            internal int Searches, Writes, TrayWrites, TrayReads, PreferenceWrites;
            internal Dictionary<TrayPreferenceKind, TrayPreferenceSnapshot> TrayStates = new Dictionary<TrayPreferenceKind, TrayPreferenceSnapshot>();
            internal Dictionary<WindowsPreferenceId,WindowsPreferenceSnapshot> Preferences=new Dictionary<WindowsPreferenceId,WindowsPreferenceSnapshot>();
            internal Dictionary<string, RegistryDatum> Values = new Dictionary<string, RegistryDatum>();
            readonly HashSet<string> applied = new HashSet<string>();
            public bool Certified(string feature) { return CertifiedValue&&(!feature.StartsWith("broker:")||BrokerCertifiedValue); }
            public bool IsWindows11 { get { return true; } }
            public bool HasBattery { get { return HasBatteryValue; } }
            public bool UpdateManaged { get { return UpdateManagedValue; } }
            public RegistryDatum Read(bool machine, string path, string name) { RegistryDatum value; return Values.TryGetValue(path + "/" + name, out value) ? value : new RegistryDatum(); }
            public void Write(bool machine, string path, string name, object value, RegistryValueKind kind) { Writes++; Values[path + "/" + name] = new RegistryDatum { Exists = true, Value = value, Kind = kind }; }
            public TrayPreferenceSnapshot ReadTrayPreference(TrayPreferenceKind kind) { TrayReads++; if (TrayReadFails) throw new InvalidOperationException("broker unavailable"); TrayPreferenceSnapshot value; return TrayStates.TryGetValue(kind, out value) ? value : new TrayPreferenceSnapshot(false, true, true, false, false); }
            public void SetTrayPreference(TrayPreferenceKind kind, bool value) { TrayWrites++; if (!IgnoreTrayWrites) TrayStates[kind] = new TrayPreferenceSnapshot(value, true, true, false, false); }
            public WindowsPreferenceSnapshot ReadWindowsPreference(WindowsPreferenceId id) {
                WindowsPreferenceSnapshot state;if(Preferences.TryGetValue(id,out state))return state;
                var definition=WindowsPreferenceCatalog.Get(id);
                if(definition.PropertyType==12)return new WindowsPreferenceSnapshot("all",true,true,false,false,new[]{"all","main","window"});
                object value=definition.PropertyType==5?(object)(id==WindowsPreferenceId.KeyboardDelay?3u:0u):definition.PropertyType==4?(object)0:
                    id==WindowsPreferenceId.MouseAcceleration||id==WindowsPreferenceId.Widgets?(object)true:false;
                return new WindowsPreferenceSnapshot(value,true,true,false,false);
            }
            public void SetWindowsPreference(WindowsPreferenceId id,object value){PreferenceWrites++;if(!Lie)Preferences[id]=new WindowsPreferenceSnapshot(value,true,true,false,false,ReadWindowsPreference(id).Choices);}
            public int ReadNative(string key) { return applied.Contains(key) ? 1 : 0; }
            public string ApplyNative(string key) { if (!Lie) applied.Add(key); if (key == "search-updates") Searches++; return "test"; }
            public IReadOnlyList<TweakPart> InspectStartup() { return new[] { new TweakPart("startup", TweakState.Applied) }; }
            public TweakPart ApplyStartup(string key) { throw new InvalidOperationException("No startup action expected."); }
        }
        sealed class FakeFiles : IIndexingFiles
        {
            internal readonly List<string> Changed = new List<string>();
            public IEnumerable<string> Children(string path) { return new[] { @"C:\ok", @"C:\denied", @"C:\link" }; }
            public FileAttributes Attributes(string path) { return path.EndsWith("link") ? FileAttributes.ReparsePoint | FileAttributes.Directory : FileAttributes.Archive; }
            public void SetAttributes(string path, FileAttributes value)
            {
                if (path.EndsWith("denied")) throw new UnauthorizedAccessException("test");
                if ((value & FileAttributes.Archive) == 0 || (value & FileAttributes.NotContentIndexed) == 0) throw new InvalidOperationException("Existing attributes must survive.");
                Changed.Add(path);
            }
        }
        sealed class FakeProgress : IIndexingProgress
        {
            internal bool Disposed, Stop;
            public void Open() { }
            public bool Cancelled { get { return Stop; } }
            public void Update(string path, int processed, int skipped) { }
            public void Dispose() { Disposed = true; }
        }
    }
}

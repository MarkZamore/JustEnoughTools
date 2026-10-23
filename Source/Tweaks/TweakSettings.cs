using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using WindowsPreferences;

namespace JustEnoughTools.Tweaks
{
    internal sealed class TweakSetting
    {
        internal TweakId Id;
        internal string Key, Path, Name, Capability, Title;
        internal object Target;
        internal object DefaultValue;
        internal RegistryValueKind Kind;
        internal bool Administrator;
        internal WindowsPreferenceId? Preference;
        internal object PreferenceTarget;
        internal int? PreferenceChoice;
        internal TweakSetting Legacy() {var copy=(TweakSetting)MemberwiseClone();copy.Preference=null;copy.Administrator=Id==TweakId.WindowsUpdate;return copy;}
    }

    internal static class TweakSettings
    {
        internal const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        internal const string Start = @"Software\Microsoft\Windows\CurrentVersion\Start";
        internal const string Update = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
        static readonly Lazy<TweakSetting[]> Catalog = new Lazy<TweakSetting[]>(() => Create().ToArray());
        internal static TweakSetting[] All { get { return Catalog.Value; } }
        static IEnumerable<TweakSetting> Create()
        {
            yield return S(TweakId.Clipboard, @"Software\Microsoft\Clipboard", "EnableClipboardHistory", 1, "clipboard");
            foreach (var pair in new[] { "ShowPinnedSection=1", "ShowRecentSection=1", "ShowAllAppsSection=1", "ShowRecentList=1", "StartMenuSize=0", "HideUserInformation=0" })
                yield return Pair(TweakId.Start, Start, pair, "new-start");
            foreach (var pair in new[] { "Start_TrackDocs=0", "Start_IrisRecommendations=0", "Start_AccountNotifications=0" })
                yield return Pair(TweakId.Start, Advanced, pair, "start");
            yield return B(TweakId.Start, "jump-lists", WindowsPreferenceId.StartJumpLists, false, "Скрыть недавние элементы списков переходов");
            yield return B(TweakId.Start, "recent-items", WindowsPreferenceId.StartRecentItems, false, "Скрыть недавние элементы Проводника");
            foreach(var place in new[] { WindowsPreferenceId.StartSettings,WindowsPreferenceId.StartExplorer,WindowsPreferenceId.StartDocuments,
                WindowsPreferenceId.StartDownloads,WindowsPreferenceId.StartMusic,WindowsPreferenceId.StartPictures,WindowsPreferenceId.StartVideos,
                WindowsPreferenceId.StartNetwork,WindowsPreferenceId.StartUserFolder })
                yield return B(TweakId.Start,"place-"+place,place,place==WindowsPreferenceId.StartSettings||place==WindowsPreferenceId.StartExplorer,"Папки рядом с питанием");
            foreach (var pair in new[] { "TaskbarAl=0", "ShowTaskViewButton=0", "TaskbarBadges=1", "TaskbarFlashing=1", "MMTaskbarEnabled=1", "MMTaskbarMode=2", "TaskbarGlomLevel=0", "MMTaskbarGlomLevel=0", "TaskbarSn=1", "TaskbarSd=1", "ExpandableTaskbar=0" })
                yield return Pair(TweakId.Taskbar, Advanced, pair, "taskbar");
            foreach (var pair in new[] { "TaskbarLocation=3", "TaskbarSize=0", "IconSizePreference=2" })
                yield return Pair(TweakId.Taskbar, Advanced, pair, "new-taskbar");
            yield return S(TweakId.Taskbar, @"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 0, "taskbar");
            yield return S(TweakId.Taskbar, @"Software\Microsoft\TabletTip\1.7", "EmojiAndMoreIconVisibilityState", 2, "emoji");
            yield return S(TweakId.Taskbar, @"Software\Microsoft\TabletTip\1.7", "TipbandDesiredVisibility", 2, "touch-keyboard");
            yield return S(TweakId.Taskbar, @"Software\Microsoft\Touchpad", "TouchpadDesiredVisibility", 0, "taskbar");
            yield return S(TweakId.Taskbar, @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify", "SystemTrayChevronVisibility", 1, "tray");
            yield return S(TweakId.Taskbar, Advanced, "IsEnabled", 0, "resume");
            yield return B(TweakId.Taskbar,"widgets",WindowsPreferenceId.Widgets,false,"Скрыть мини-приложения");
            yield return B(TweakId.Keyboard,"keyboard-delay",WindowsPreferenceId.KeyboardDelay,0u,"Минимальная задержка повтора клавиш");
            yield return B(TweakId.Keyboard,"keyboard-speed",WindowsPreferenceId.KeyboardRepeatRate,31u,"Максимальная скорость повтора клавиш");
            yield return B(TweakId.Mouse,"mouse",WindowsPreferenceId.MouseAcceleration,false,"Отключить ускорение мыши");
            foreach(var sticky in new[]{WindowsPreferenceId.StickyHotkey,WindowsPreferenceId.StickyIndicator,WindowsPreferenceId.StickyLock,
                WindowsPreferenceId.StickyTwoKeysOff,WindowsPreferenceId.StickySound,WindowsPreferenceId.StickyEnabled})
                yield return B(TweakId.StickyKeys,"sticky-"+sticky,sticky,false,"Отключить залипание клавиш и его переключатели");
            foreach (var pair in new[] { "ShowSystrayDateTimeValueName=1", "ShowClockInNotificationCenter=1" })
                yield return Pair(TweakId.Clock, Advanced, pair, "clock");
            foreach (var number in new[] { "1", "2" })
                yield return S(TweakId.Clock, @"Control Panel\TimeDate\AdditionalClocks\" + number, "Enable", 0, "clock");
            foreach (var pair in new[] { "IsContinuousInnovationOptedIn=1", "AllowMUUpdateService=1", "IsExpedited=0", "AllowAutoWindowsUpdateDownloadOverMeteredNetwork=1", "RestartNotificationsAllowed2=1" })
            {
                var setting = Pair(TweakId.WindowsUpdate, Update, pair, "windows-update"); yield return setting;
            }
        }
        internal static TweakSetting LegacyFolders() {return S(TweakId.Start,Start,"VisiblePlaces",
            new Guid("52730886-51AA-4243-9F7B-2776584659D4").ToByteArray().Concat(new Guid("148A24BC-D60C-4289-A080-6ED9BBA24882").ToByteArray()).ToArray(),"start");}
        static TweakSetting Pair(TweakId id, string path, string value, string capability)
        { var pair = value.Split('='); return S(id, path, pair[0], int.Parse(pair[1]), capability); }
        static TweakSetting S(TweakId id, string path, string name, object value, string capability)
        {
            var setting = new TweakSetting { Id = id, Key = name + (path.StartsWith("Control Panel") ? path.Substring(path.Length - 1) : ""),
                Path = path, Name = name, Title = Title(name) + (name == "Enable" ? " " + path.Substring(path.Length - 1) : ""),
                Target = value, DefaultValue = Default(name), Kind = value is byte[] ? RegistryValueKind.Binary : RegistryValueKind.DWord, Capability = capability };
            WindowsPreferenceId preference;
            if(BrokerMapping.TryGetValue(name,out preference)) {
                setting.Preference=preference;
                int target=Convert.ToInt32(value);
                if(WindowsPreferenceCatalog.Get(preference).PropertyType==12)setting.PreferenceChoice=target;
                setting.PreferenceTarget=WindowsPreferenceCatalog.Get(preference).PropertyType==11?(object)(target!=0):
                    name=="TaskbarLocation"?0:name=="IconSizePreference"?1:target;
            }
            return setting;
        }
        static TweakSetting B(TweakId id,string key,WindowsPreferenceId preference,object target,string title)
        {return new TweakSetting {Id=id,Key=key,Name=key,Title=title,Preference=preference,PreferenceTarget=target};}
        static readonly Dictionary<string,WindowsPreferenceId> BrokerMapping = new Dictionary<string,WindowsPreferenceId>(StringComparer.Ordinal)
        {
            {"EnableClipboardHistory",WindowsPreferenceId.ClipboardHistory},{"ShowPinnedSection",WindowsPreferenceId.StartPinned},
            {"ShowRecentSection",WindowsPreferenceId.StartRecent},{"ShowAllAppsSection",WindowsPreferenceId.StartAll},
            {"ShowRecentList",WindowsPreferenceId.StartRecentApps},{"StartMenuSize",WindowsPreferenceId.StartSize},
            {"HideUserInformation",WindowsPreferenceId.StartHideUser},{"Start_TrackDocs",WindowsPreferenceId.StartSuggestedFiles},
            {"Start_IrisRecommendations",WindowsPreferenceId.StartRecommendations},{"Start_AccountNotifications",WindowsPreferenceId.StartAccountNotifications},
            {"TaskbarAl",WindowsPreferenceId.TaskbarAlignment},{"ShowTaskViewButton",WindowsPreferenceId.TaskView},
            {"TaskbarBadges",WindowsPreferenceId.TaskbarBadges},{"TaskbarFlashing",WindowsPreferenceId.TaskbarFlashing},
            {"MMTaskbarEnabled",WindowsPreferenceId.TaskbarMultiMonitor},{"MMTaskbarMode",WindowsPreferenceId.TaskbarMultiMonitorMode},
            {"TaskbarGlomLevel",WindowsPreferenceId.TaskbarGrouping},{"MMTaskbarGlomLevel",WindowsPreferenceId.TaskbarSecondaryGrouping},
            {"TaskbarSn",WindowsPreferenceId.TaskbarShareWindow},{"TaskbarSd",WindowsPreferenceId.TaskbarDesktopCorner},
            {"ExpandableTaskbar",WindowsPreferenceId.TabletTaskbar},{"TaskbarLocation",WindowsPreferenceId.TaskbarPosition},
            {"TaskbarSize",WindowsPreferenceId.TaskbarSize},{"IconSizePreference",WindowsPreferenceId.TaskbarSmallIcons},
            {"SearchboxTaskbarMode",WindowsPreferenceId.TaskbarSearch},{"EmojiAndMoreIconVisibilityState",WindowsPreferenceId.EmojiPanel},
            {"TipbandDesiredVisibility",WindowsPreferenceId.TouchKeyboard},{"TouchpadDesiredVisibility",WindowsPreferenceId.VirtualTouchpad},
            {"SystemTrayChevronVisibility",WindowsPreferenceId.HiddenIcons},{"IsEnabled",WindowsPreferenceId.Resume},
            {"ShowSystrayDateTimeValueName",WindowsPreferenceId.ClockVisible},{"ShowClockInNotificationCenter",WindowsPreferenceId.NotificationClock},
            {"IsContinuousInnovationOptedIn",WindowsPreferenceId.UpdateEarly},{"AllowMUUpdateService",WindowsPreferenceId.UpdateMicrosoft},
            {"IsExpedited",WindowsPreferenceId.UpdateExpedited},{"AllowAutoWindowsUpdateDownloadOverMeteredNetwork",WindowsPreferenceId.UpdateMetered},
            {"RestartNotificationsAllowed2",WindowsPreferenceId.UpdateRestartNotifications}
        };
        static string Title(string name)
        {
            switch (name)
            {
                case "EnableClipboardHistory": return "Включить историю буфера";
                case "ShowPinnedSection": return "Показывать закреплённое";
                case "ShowRecentSection": return "Показывать раздел недавнего";
                case "ShowAllAppsSection": return "Показывать все приложения";
                case "ShowRecentList": return "Показывать недавно установленные приложения";
                case "StartMenuSize": return "Автоматический размер Пуска";
                case "HideUserInformation": return "Показывать имя и аватар";
                case "Start_TrackDocs": return "Скрыть недавние и рекомендуемые файлы";
                case "Start_IrisRecommendations": return "Отключить советы и рекомендации";
                case "Start_AccountNotifications": return "Отключить уведомления учётной записи";
                case "VisiblePlaces": return "Папки рядом с питанием: Настройки и Проводник";
                case "TaskbarAl": return "Выравнивание значков слева";
                case "ShowTaskViewButton": return "Скрыть представление задач";
                case "TaskbarBadges": return "Показывать эмблемы приложений";
                case "TaskbarFlashing": return "Разрешить мигание значков";
                case "MMTaskbarEnabled": return "Показывать панель на всех дисплеях";
                case "MMTaskbarMode": return "Приложение на панели дисплея с его окном";
                case "TaskbarGlomLevel": return "Всегда объединять кнопки";
                case "MMTaskbarGlomLevel": return "Всегда объединять кнопки на других дисплеях";
                case "TaskbarSn": return "Разрешить предоставление доступа к окнам";
                case "TaskbarSd": return "Разрешить показ рабочего стола из угла панели";
                case "ExpandableTaskbar": return "Отключить оптимизацию для планшета";
                case "TaskbarLocation": return "Панель задач снизу";
                case "TaskbarSize": return "Размер панели по умолчанию";
                case "IconSizePreference": return "Уменьшать значки при переполнении";
                case "SearchboxTaskbarMode": return "Скрыть поиск";
                case "EmojiAndMoreIconVisibilityState": return "Всегда показывать значок эмодзи";
                case "PenWorkspaceButtonDesiredVisibility": return "Показывать меню пера";
                case "TipbandDesiredVisibility": return "Сенсорная клавиатура без физической";
                case "TouchpadDesiredVisibility": return "Скрыть виртуальную сенсорную панель";
                case "SystemTrayChevronVisibility": return "Показывать меню скрытых значков";
                case "IsEnabled": return "Отключить значок возобновления";
                case "ShowSecondsInSystemClock": return "Показывать секунды";
                case "ShowSystrayDateTimeValueName": return "Показывать дату и время";
                case "ShowClockInNotificationCenter": return "Показывать часы в центре уведомлений";
                case "Enable": return "Отключить дополнительные часы";
                case "IsBatteryPercentageEnabled": return "Показывать процент заряда";
                case "IsContinuousInnovationOptedIn": return "Получать последние обновления раньше";
                case "AllowMUUpdateService": return "Обновлять продукты Microsoft";
                case "IsExpedited": return "Отключить ускоренный перезапуск";
                case "AllowAutoWindowsUpdateDownloadOverMeteredNetwork": return "Загружать обновления через лимитные подключения";
                case "RestartNotificationsAllowed2": return "Уведомлять о необходимости перезагрузки";
                default: return "Параметр Windows";
            }
        }
        static object Default(string name)
        {
            switch (name)
            {
                case "VisiblePlaces": return new byte[0];
                case "EnableClipboardHistory": case "StartMenuSize": case "HideUserInformation":
                case "TaskbarGlomLevel": case "MMTaskbarGlomLevel": case "MMTaskbarMode":
                case "ShowSecondsInSystemClock": case "Enable":
                case "TaskbarSize": case "TouchpadDesiredVisibility":
                case "IsContinuousInnovationOptedIn": case "AllowMUUpdateService": case "IsExpedited":
                case "AllowAutoWindowsUpdateDownloadOverMeteredNetwork": case "RestartNotificationsAllowed2":
                case "PenWorkspaceButtonDesiredVisibility": case "EmojiAndMoreIconVisibilityState": case "ShowClockInNotificationCenter":
                    return 0;
                case "SearchboxTaskbarMode": case "TipbandDesiredVisibility": case "IconSizePreference": return 2;
                case "TaskbarLocation": return 3;
                case "ShowPinnedSection": case "ShowRecentSection": case "ShowAllAppsSection":
                case "ShowRecentList": case "Start_TrackDocs": case "Start_IrisRecommendations": case "Start_AccountNotifications":
                case "TaskbarAl": case "ShowTaskViewButton": case "TaskbarBadges": case "TaskbarFlashing": case "MMTaskbarEnabled": case "ShowSystrayDateTimeValueName": case "IsEnabled":
                    return 1;
                default: return null;
            }
        }
        internal static bool Equal(object left, object right)
        {
            if (left is byte[] && right is byte[]) return ((byte[])left).SequenceEqual((byte[])right);
            return Equals(left, right);
        }
    }

    internal sealed class RegistryDatum
    {
        internal bool Exists;
        internal object Value;
        internal RegistryValueKind Kind;
    }

    internal interface ITweakPlatform
    {
        bool Certified(string capability);
        bool IsWindows11 { get; }
        bool HasBattery { get; }
        bool UpdateManaged { get; }
        RegistryDatum Read(bool machine, string path, string name);
        void Write(bool machine, string path, string name, object value, RegistryValueKind kind);
        TrayPreferenceSnapshot ReadTrayPreference(TrayPreferenceKind kind);
        void SetTrayPreference(TrayPreferenceKind kind, bool enabled);
        WindowsPreferenceSnapshot ReadWindowsPreference(WindowsPreferenceId id);
        void SetWindowsPreference(WindowsPreferenceId id, object value);
        int ReadNative(string key);
        string ApplyNative(string key);
        IReadOnlyList<TweakPart> InspectStartup();
        TweakPart ApplyStartup(string key);
    }

    internal sealed class WindowsTweakBackend : ITweakBackend
    {
        readonly ITweakPlatform system;
        volatile bool searched, nativeStarted;
        internal WindowsTweakBackend(ITweakPlatform system) { this.system = system; }
        IEnumerable<TweakSetting> EffectiveSettings(TweakId id)
        {
            foreach(var setting in TweakSettings.All.Where(s=>s.Id==id)) {
                if(setting.Preference.HasValue&&!system.Certified("broker:"+setting.Preference.Value)) {
                    if(id==TweakId.Keyboard||id==TweakId.Mouse||id==TweakId.StickyKeys||setting.Key.StartsWith("place-",StringComparison.Ordinal))continue;
                    if(setting.Path!=null){yield return setting.Legacy();continue;}
                }
                yield return setting;
            }
            if(id==TweakId.Start&&!system.Certified("broker:"+WindowsPreferenceId.StartSettings))yield return TweakSettings.LegacyFolders();
        }
        public IReadOnlyList<TweakPart> Inspect(TweakId id)
        {
            var result = new List<TweakPart>();
            if (!system.IsWindows11) return new[] { new TweakPart("os", TweakState.Unavailable, reason: "Требуется Windows 11.") };
            foreach (var setting in EffectiveSettings(id))
            {
                try
                {
                    if(setting.Preference.HasValue) {result.Add(InspectPreference(setting));continue;}
                    if (!system.Certified(setting.Capability + ":" + setting.Name)) { result.Add(Unverified(setting.Key, setting.Title)); continue; }
                    if (id == TweakId.Battery && !system.HasBattery) { result.Add(new TweakPart(setting.Key, TweakState.Unavailable, reason: "Батарея не обнаружена.")); continue; }
                    if (id == TweakId.WindowsUpdate && system.UpdateManaged) { result.Add(new TweakPart(setting.Key, TweakState.Unavailable, reason: "Параметры обновления управляются политикой.")); continue; }
                    if (id == TweakId.Clipboard && BlockedClipboard()) { result.Add(new TweakPart(setting.Key, TweakState.Unavailable, reason: "История буфера заблокирована политикой.")); continue; }
                    if (ShellPolicyBlocks(setting)) { result.Add(new TweakPart(setting.Key, TweakState.Unavailable, reason: setting.Title + ": действует политика Windows.")); continue; }
                    var current = system.Read(setting.Administrator, setting.Path, setting.Name);
                    if (current.Exists && current.Kind != setting.Kind) { result.Add(new TweakPart(setting.Key, TweakState.Unknown, reason: setting.Title + ": неизвестный формат настройки.")); continue; }
                    if (!current.Exists && setting.DefaultValue == null) { result.Add(new TweakPart(setting.Key, TweakState.Unknown, reason: setting.Title + ": исходное значение не определено.")); continue; }
                    bool matches = TweakSettings.Equal(current.Exists ? current.Value : setting.DefaultValue, setting.Target);
                    result.Add(new TweakPart(setting.Key, matches ? TweakState.Applied : TweakState.Pending,
                        setting.Administrator, setting.Title + ": " + (matches ? "уже настроено." : "можно применить.")));
                }
                catch (Exception error) { result.Add(new TweakPart(setting.Key, TweakState.Unknown, reason: setting.Title + ": " + error.Message)); }
            }
            if (id == TweakId.Startup)
            {
                if (system.Certified("startup")) result.AddRange(system.InspectStartup()); else result.Add(Unverified("startup", "Отключение автозагрузки"));
            }
            foreach (var key in NativeKeys(id))
            {
                if(id == TweakId.WindowsUpdate && key == "microsoft-update" &&
                    (nativeStarted || system.ReadNative("search-updates") >= 3)) continue;
                if(result.Any(p=>p.Key==key))continue;
                try
                {
                    if(key=="sticky") {
                        if(result.Count!=0&&result.All(p=>p.State==TweakState.Applied)) {result.Add(new TweakPart(key,TweakState.Applied,reason:"Уже настроено."));continue;}
                        if(result.Any(p=>p.State==TweakState.Unknown||(p.Reason??"").Contains("политик"))) {
                            result.Add(new TweakPart(key,TweakState.Unavailable,reason:"Не удалось подтвердить доступность настройки."));continue;
                        }
                    }
                    if (key == "IsBatteryPercentageEnabled" || key == "ShowSecondsInSystemClock" || key == "PenWorkspaceButtonDesiredVisibility")
                    {
                        string capability = key == "IsBatteryPercentageEnabled" ? "battery:" : key == "ShowSecondsInSystemClock" ? "clock:" : "taskbar:";
                        if (!system.Certified(capability + key)) { result.Add(Unverified(key, "Настройка панели задач")); continue; }
                        if (key == "IsBatteryPercentageEnabled" && !system.HasBattery) { result.Add(new TweakPart(key, TweakState.Unavailable, reason: "Батарея не обнаружена.")); continue; }
                        var tray = system.ReadTrayPreference(TrayKind(key));
                        if (!tray.IsApplicable || !tray.IsEnabled || tray.IsSetByGroupPolicy)
                        { result.Add(new TweakPart(key, TweakState.Unavailable, reason: tray.IsSetByGroupPolicy ? "Настройка управляется политикой Windows." : "Настройка недоступна в этой системе.")); continue; }
                        result.Add(new TweakPart(key, tray.IsUpdating ? TweakState.Running : tray.Value ? TweakState.Applied : TweakState.Pending,
                            reason: NativeReason(key, tray.IsUpdating ? TweakState.Running : tray.Value ? TweakState.Applied : TweakState.Pending)));
                        continue;
                    }
                    if (key == "theme" && !system.Certified("theme")) { result.Add(Unverified(key, "Тёмная тема Windows")); continue; }
                    if (key == "search-updates")
                    {
                        if(system.UpdateManaged){result.Add(new TweakPart(key,TweakState.Unavailable,reason:"Обновления управляются политикой Windows."));continue;}
                        int available = nativeStarted ? 4 : searched ? 1 : system.ReadNative(key);
                        bool native = available == 3 || available == 4;
                        var updateState = nativeStarted || searched ? TweakState.Applied : available == 3 ? TweakState.Running :
                            available == 2 ? TweakState.Unavailable : available < 0 ? TweakState.Unknown : TweakState.Pending;
                        result.Add(new TweakPart(key, updateState, !native,
                            reason: nativeStarted ? "Проверка запущена средствами Windows." : searched ? "Обновления обработаны в этом запуске." :
                            available == 3 ? "Windows уже проверяет или применяет обновления." : available == 4 ? "Запустить проверку средствами Windows." :
                            available == 2 ? "Поиск обновлений недоступен." : available < 0 ? "Не удалось проверить доступность поиска обновлений." :
                            "Скачать и установить доступные обновления резервным способом, включая необязательные.")); continue;
                    }
                    int value = system.ReadNative(key);
                    TweakState state = value < 0 ? TweakState.Unknown : value == 1 ? TweakState.Applied : value == 2 ? TweakState.Unavailable : value == 3 ? TweakState.Running : TweakState.Pending;
                    result.Add(new TweakPart(key, state, key == "indexing" || key == "bitlocker" || key == "microsoft-update",
                        NativeReason(key, state)));
                }
                catch (Exception error) { result.Add(new TweakPart(key, TweakState.Unknown, reason: error.Message)); }
            }
            return result;
        }
        TweakPart InspectPreference(TweakSetting setting)
        {
            if(!system.Certified("broker:"+setting.Preference.Value))return Unverified(setting.Key,setting.Title);
            if(setting.Id==TweakId.Clipboard&&BlockedClipboard()||ShellPolicyBlocks(setting)||setting.Id==TweakId.WindowsUpdate&&system.UpdateManaged)
                return new TweakPart(setting.Key,TweakState.Unavailable,reason:"Настройка управляется политикой Windows.");
            var state=system.ReadWindowsPreference(setting.Preference.Value);
            if(!state.IsApplicable)return new TweakPart(setting.Key,TweakState.Unavailable,reason:"Настройка недоступна в этой системе.");
            WindowsPreferenceCatalog.Get(setting.Preference.Value).Validate(state.Value);
            var result=state.IsUpdating?TweakState.Running:Equals(state.Value,PreferenceValue(setting,state))?TweakState.Applied:
                state.IsSetByGroupPolicy||!state.IsEnabled?TweakState.Unavailable:TweakState.Pending;
            return new TweakPart(setting.Key,result,setting.Administrator,
                state.IsSetByGroupPolicy?"Настройка управляется политикой Windows.":result==TweakState.Applied?"Уже настроено.":
                result==TweakState.Running?"Настройка изменяется Windows.":result==TweakState.Pending?"Можно применить.":"Настройка временно недоступна.");
        }
        static object PreferenceValue(TweakSetting setting,WindowsPreferenceSnapshot state)
        {
            if(!setting.PreferenceChoice.HasValue)return setting.PreferenceTarget;
            if(state.Choices.Length!=3||state.Choices.Any(string.IsNullOrEmpty)||state.Choices.Distinct(StringComparer.Ordinal).Count()!=3||
                !(state.Value is string)||!state.Choices.Contains((string)state.Value))
                throw new InvalidOperationException("Windows вернула неподтверждённый список режимов панели задач.");
            return state.Choices[setting.PreferenceChoice.Value];
        }
        bool BlockedClipboard()
        {
            foreach (bool machine in new[] { false, true })
            {
                var policy = system.Read(machine, @"Software\Policies\Microsoft\Windows\System", "AllowClipboardHistory");
                if (policy.Exists && !Equals(policy.Value, 1)) return true;
            }
            return false;
        }
        bool ShellPolicyBlocks(TweakSetting setting)
        {
            string[] policies;
            switch (setting.Name)
            {
                case "VisiblePlaces": policies = new[] { "SettingsPageVisibility" }; break;
                case "ShowRecentList": case "Start_TrackProgs": policies = new[] { "HideRecentlyAddedApps", "NoStartMenuMFUprogramsList" }; break;
                case "Start_TrackDocs": policies = new[] { "NoRecentDocsHistory", "HideRecentJumplists" }; break;
                case "Start_IrisRecommendations": policies = new[] { "HideRecommendedSection", "HideRecommendedPersonalizedSites" }; break;
                case "ShowPinnedSection": policies = new[] { "NoStartMenuPinnedList" }; break;
                case "ShowTaskViewButton": policies = new[] { "HideTaskView" }; break;
                case "SearchboxTaskbarMode": policies = new[] { "ConfigureSearchOnTaskbarMode" }; break;
                case "ShowSystrayDateTimeValueName": policies = new[] { "HideClock" }; break;
                default: return false;
            }
            foreach (bool machine in new[] { false, true })
                foreach (string path in new[] { @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", @"Software\Policies\Microsoft\Windows\Explorer", @"Software\Policies\Microsoft\Windows\Windows Search" })
                    foreach (string name in policies)
                    {
                        var policy = system.Read(machine, path, name);
                        if (policy.Exists && !Equals(policy.Value, 0)) return true;
                    }
            return false;
        }
        static TweakPart Unverified(string key, string title) { return new TweakPart(key, TweakState.Unavailable, reason: title + ": не проверено для этой сборки Windows."); }
        static string NativeReason(string key, TweakState state)
        {
            string title;
            switch (key)
            {
                case "keyboard-delay": title = "Задержка повтора клавиш"; break;
                case "keyboard-speed": title = "Скорость повтора клавиш"; break;
                case "caret": title = "Мерцание курсора"; break;
                case "sticky": title = "Залипание клавиш"; break;
                case "mouse": title = "Ускорение мыши"; break;
                case "indexing": title = "Индексирование C:"; break;
                case "bitlocker": title = "BitLocker C:"; break;
                case "theme": title = "Тема Windows (тёмная)"; break;
                case "autohide": title = "Автоскрытие панели задач"; break;
                case "microsoft-update": title = "Обновления продуктов Microsoft"; break;
                case "IsBatteryPercentageEnabled": title = "Процент заряда батареи"; break;
                case "ShowSecondsInSystemClock": title = "Секунды часов"; break;
                case "PenWorkspaceButtonDesiredVisibility": title = "Меню пера"; break;
                default: title = "Настройка"; break;
            }
            string reason = state == TweakState.Applied ? "уже настроено." :
                state == TweakState.Unknown ? "состояние недоступно; повышение прав для проверки не запрашивается." :
                state == TweakState.Unavailable ? "функция недоступна." :
                state == TweakState.Running ? (key == "bitlocker" ? "Windows уже расшифровывает диск." : "операция уже выполняется.") : "можно применить.";
            return title + ": " + reason + (key == "indexing" ? " Проверяется только признак корня C:, без обхода файлов." : "");
        }
        internal static IEnumerable<string> NativeKeys(TweakId id)
        {
            switch (id)
            {
                case TweakId.Indexing: return new[] { "indexing" };
                case TweakId.BitLocker: return new[] { "bitlocker" };
                case TweakId.Keyboard: return new[] { "keyboard-delay", "keyboard-speed", "caret" };
                case TweakId.Mouse: return new[] { "mouse" };
                case TweakId.StickyKeys: return new[] { "sticky" };
                case TweakId.Theme: return new[] { "theme" };
                case TweakId.Taskbar: return new[] { "autohide", "PenWorkspaceButtonDesiredVisibility" };
                case TweakId.Battery: return new[] { "IsBatteryPercentageEnabled" };
                case TweakId.Clock: return new[] { "ShowSecondsInSystemClock" };
                case TweakId.WindowsUpdate: return new[] { "microsoft-update", "search-updates" };
                default: return new string[0];
            }
        }
        public TweakPart Apply(TweakId id, string part)
        {
            // Reinspect just before mutation, including capability and policy gates.
            var before = Inspect(id).SingleOrDefault(p => p.Key == part);
            if (before == null) throw new ArgumentOutOfRangeException(nameof(part));
            if (before.State != TweakState.Pending) return before;
            if (id == TweakId.Startup) return system.ApplyStartup(part);
            var setting = EffectiveSettings(id).SingleOrDefault(s => s.Key == part);
            if (setting != null)
            {
                if (setting.Name == "MMTaskbarMode")
                {
                    using (var taskbar = new System.Threading.Mutex(false, @"Local\JustEnoughTools.TaskbarRepair"))
                    {
                        bool owned;
                        try { owned = taskbar.WaitOne(0); } catch (System.Threading.AbandonedMutexException) { owned = true; }
                        if (!owned) throw new InvalidOperationException("Исправление панели задач ещё выполняется.");
                        try { return Store(setting); }
                        finally { taskbar.ReleaseMutex(); }
                    }
                }
                return Store(setting);
            }
            if (!NativeKeys(id).Contains(part)) throw new ArgumentOutOfRangeException(nameof(part));
            string message;
            if (part == "IsBatteryPercentageEnabled" || part == "ShowSecondsInSystemClock" || part == "PenWorkspaceButtonDesiredVisibility")
            {
                system.SetTrayPreference(TrayKind(part), true);
                message = "Настройка применена через брокер Windows.";
            }
            else message = system.ApplyNative(part);
            if (part == "search-updates")
            {
                if (!before.Administrator)
                {
                    nativeStarted = true;
                    return new TweakPart(part, TweakState.Applied, false, "Проверка запущена средствами Windows.");
                }
                searched = true;
            }
            if (part == "bitlocker")
            {
                var state = Inspect(id).Single(p => p.Key == part).State;
                if (state != TweakState.Applied && state != TweakState.Running) throw new InvalidOperationException("Завершение расшифровки не подтверждено.");
                return new TweakPart(part, state, true, message);
            }
            var checkedPart = Inspect(id).Single(p => p.Key == part);
            if (checkedPart.State == TweakState.Running) return new TweakPart(part, TweakState.Running, before.Administrator, message);
            if (checkedPart.State != TweakState.Applied) throw new InvalidOperationException(message ?? "Результат изменения не подтверждён.");
            return new TweakPart(part, TweakState.Applied, before.Administrator, message);
        }
        static TrayPreferenceKind TrayKind(string key)
        {
            if (key == "IsBatteryPercentageEnabled") return TrayPreferenceKind.BatteryPercentage;
            if (key == "ShowSecondsInSystemClock") return TrayPreferenceKind.ClockSeconds;
            if (key == "PenWorkspaceButtonDesiredVisibility") return TrayPreferenceKind.PenWorkspace;
            throw new ArgumentOutOfRangeException(nameof(key));
        }
        TweakPart Store(TweakSetting setting)
        {
                if(setting.Preference.HasValue) {
                    system.SetWindowsPreference(setting.Preference.Value,PreferenceValue(setting,system.ReadWindowsPreference(setting.Preference.Value)));
                    var verified=InspectPreference(setting);
                    if(verified.State!=TweakState.Applied)throw new InvalidOperationException("Windows не подтвердила изменение настройки.");
                    return new TweakPart(setting.Key,TweakState.Applied,setting.Administrator,"Настройка применена и проверена.");
                }
                system.Write(setting.Administrator, setting.Path, setting.Name, setting.Target, setting.Kind);
                var after = system.Read(setting.Administrator, setting.Path, setting.Name);
                if (!after.Exists || after.Kind != setting.Kind || !TweakSettings.Equal(after.Value, setting.Target))
                    throw new InvalidOperationException(setting.Title + ": сохранение не подтверждено.");
                return new TweakPart(setting.Key, TweakState.Deferred, setting.Administrator, setting.Title + ": сохранено; Windows может применить после следующего входа или перезагрузки.");
        }
    }
}

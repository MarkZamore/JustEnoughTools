using System;
using System.Collections.Generic;

namespace WindowsPreferences
{
    internal enum WindowsPreferenceId
    {
        BatteryPercentage, ClockSeconds, PenWorkspace, NightLightEnabled, NightLightStrength,
        Widgets, ClipboardHistory, KeyboardDelay, KeyboardRepeatRate, MouseAcceleration,
        StickyEnabled, StickyHotkey, StickyIndicator, StickyLock, StickyTwoKeysOff, StickySound,
        StartPinned, StartRecent, StartAll, StartRecentApps, StartSuggestedFiles, StartRecommendations,
        StartAccountNotifications, StartJumpLists, StartRecentItems, StartHideUser, StartSize,
        StartSettings, StartExplorer, StartDocuments, StartDownloads, StartMusic, StartPictures, StartVideos, StartNetwork, StartUserFolder,
        TaskbarAlignment, TaskbarSearch, TaskView, TabletTaskbar, TaskbarBadges, TaskbarFlashing,
        TaskbarMultiMonitor, TaskbarMultiMonitorMode, TaskbarGrouping, TaskbarSecondaryGrouping,
        TaskbarShareWindow, TaskbarDesktopCorner, TaskbarPosition, TaskbarSize, TaskbarSmallIcons,
        TouchKeyboard, VirtualTouchpad, EmojiPanel, HiddenIcons, Resume, ClockVisible, NotificationClock,
        UpdateEarly, UpdateMicrosoft, UpdateExpedited, UpdateMetered, UpdateRestartNotifications,
        EnergySaver, PowerModeAC, PowerModeDC, SleepAC, SleepDC, ScreenAC, ScreenDC,
        DarkColorMode, AdaptiveBrightness, PresenceSleep, PresenceDim, PresenceWake, PresenceOnlooker, UsbPower
    }

    internal sealed class WindowsPreferenceDefinition
    {
        internal readonly string SettingId;
        internal readonly int PropertyType;
        internal readonly int Minimum, Maximum;
        internal WindowsPreferenceDefinition(string id, int type, int minimum = 0, int maximum = 0)
        { SettingId=id; PropertyType=type; Minimum=minimum; Maximum=maximum; }
        internal void Validate(object value)
        {
            if (PropertyType==11 && value is bool) return;
            if (PropertyType==4 && value is int && (int)value>=Minimum && (int)value<=Maximum) return;
            if (PropertyType==5 && value is uint && (uint)value>=Minimum && (uint)value<=Maximum) return;
            if (PropertyType==12 && value is string && ((string)value).Length>0 && ((string)value).Length<=512) return;
            throw new ArgumentException("Unsupported Windows preference value.");
        }
    }

    // Closed identifiers and scalar types. No caller-provided setting IDs,
    // arbitrary property names, UI actions, policies or other-user databases.
    internal static class WindowsPreferenceCatalog
    {
        static readonly Dictionary<WindowsPreferenceId,WindowsPreferenceDefinition> All=Create();
        internal static WindowsPreferenceDefinition Get(WindowsPreferenceId id)
        { WindowsPreferenceDefinition value;if(!All.TryGetValue(id,out value))throw new ArgumentOutOfRangeException("id");return value; }
        static Dictionary<WindowsPreferenceId,WindowsPreferenceDefinition> Create()
        {
            var d=new Dictionary<WindowsPreferenceId,WindowsPreferenceDefinition>();
            Action<WindowsPreferenceId,string> b=(key,id)=>d.Add(key,new WindowsPreferenceDefinition("SystemSettings_"+id,11));
            Action<WindowsPreferenceId,string,int> i=(key,id,max)=>d.Add(key,new WindowsPreferenceDefinition("SystemSettings_"+id,4,0,max));
            Action<WindowsPreferenceId,string,int> u=(key,id,max)=>d.Add(key,new WindowsPreferenceDefinition("SystemSettings_"+id,5,0,max));
            Action<WindowsPreferenceId,string> s=(key,id)=>d.Add(key,new WindowsPreferenceDefinition("SystemSettings_"+id,12));
            b(WindowsPreferenceId.BatteryPercentage,"PowerAndBattery_BatteryPercentageToggle");
            b(WindowsPreferenceId.ClockSeconds,"DesktopTaskbar_SecondsInSystrayClock");
            b(WindowsPreferenceId.PenWorkspace,"DesktopTaskbar_InkWorkspace");
            b(WindowsPreferenceId.NightLightEnabled,"Display_BlueLight_ManualToggleQuickAction");
            u(WindowsPreferenceId.NightLightStrength,"Display_BlueLight_ColorTemperature",100);
            b(WindowsPreferenceId.Widgets,"DesktopTaskbar_Da");
            b(WindowsPreferenceId.ClipboardHistory,"Clipboard_IsSaveClipboardItemsEnabled");
            u(WindowsPreferenceId.KeyboardDelay,"Accessibility_Keyboard_CharacterRepeatDelay",3);
            u(WindowsPreferenceId.KeyboardRepeatRate,"Accessibility_Keyboard_CharacterRepeatRate",31);
            b(WindowsPreferenceId.MouseAcceleration,"Input_Mouse_Acceleration2");
            b(WindowsPreferenceId.StickyEnabled,"EaseOfAccess_Keyboard_StickyKeys_IsEnabled");
            b(WindowsPreferenceId.StickyHotkey,"EaseOfAccess_Keyboard_StickyKeys_IsHotkeyEnabled");
            b(WindowsPreferenceId.StickyIndicator,"EaseOfAccess_Keyboard_StickyKeys_IsIndicatorEnabled");
            b(WindowsPreferenceId.StickyLock,"EaseOfAccess_Keyboard_StickyKeys_IsTriStateEnabled");
            b(WindowsPreferenceId.StickyTwoKeysOff,"EaseOfAccess_Keyboard_StickyKeys_IsTwoKeysOffEnabled");
            b(WindowsPreferenceId.StickySound,"EaseOfAccess_Keyboard_StickyKeys_IsAudibleFeedbackEnabled");
            b(WindowsPreferenceId.StartPinned,"Start_LayoutOptions_Pinned");
            b(WindowsPreferenceId.StartRecent,"Start_LayoutOptions_Recent");
            b(WindowsPreferenceId.StartAll,"Start_LayoutOptions_All");
            b(WindowsPreferenceId.StartRecentApps,"Start_ShowRecentlyAddedAppsGroup");
            b(WindowsPreferenceId.StartSuggestedFiles,"Start_ShowSuggestedFiles");
            b(WindowsPreferenceId.StartRecommendations,"Start_IrisRecommendations");
            b(WindowsPreferenceId.StartAccountNotifications,"Start_AccountNotifications");
            b(WindowsPreferenceId.StartJumpLists,"Start_ShowJumpLists");
            b(WindowsPreferenceId.StartRecentItems,"Start_StoreRecentlyOpenedItems");
            b(WindowsPreferenceId.StartHideUser,"Start_HideUserInformation");
            i(WindowsPreferenceId.StartSize,"Start_StartMenuSize",2);
            b(WindowsPreferenceId.StartSettings,"Start_PlacesSettings");
            b(WindowsPreferenceId.StartExplorer,"Start_PlacesFileExplorer");
            b(WindowsPreferenceId.StartDocuments,"Start_PlacesDocuments");
            b(WindowsPreferenceId.StartDownloads,"Start_PlacesDownloads");
            b(WindowsPreferenceId.StartMusic,"Start_PlacesMusic");
            b(WindowsPreferenceId.StartPictures,"Start_PlacesPictures");
            b(WindowsPreferenceId.StartVideos,"Start_PlacesVideos");
            b(WindowsPreferenceId.StartNetwork,"Start_PlacesNetwork");
            b(WindowsPreferenceId.StartUserFolder,"Start_PlacesUserProfile");
            i(WindowsPreferenceId.TaskbarAlignment,"DesktopTaskbar_Al",1);
            i(WindowsPreferenceId.TaskbarSearch,"DesktopTaskbar_Search",3);
            b(WindowsPreferenceId.TaskView,"DesktopTaskbar_TaskView");
            b(WindowsPreferenceId.TabletTaskbar,"DesktopTaskbar_ExpandableTaskbar");
            b(WindowsPreferenceId.TaskbarBadges,"DesktopTaskbar_Badging");
            b(WindowsPreferenceId.TaskbarFlashing,"DesktopTaskbar_Flashing");
            b(WindowsPreferenceId.TaskbarMultiMonitor,"DesktopTaskbar_MultiMon");
            i(WindowsPreferenceId.TaskbarMultiMonitorMode,"DesktopTaskbar_MultiMonTaskbarMode",2);
            i(WindowsPreferenceId.TaskbarGrouping,"DesktopTaskbar_GroupingMode",2);
            i(WindowsPreferenceId.TaskbarSecondaryGrouping,"DesktopTaskbar_SecondaryMonitorsGroupingMode",2);
            b(WindowsPreferenceId.TaskbarShareWindow,"DesktopTaskbar_Sn");
            b(WindowsPreferenceId.TaskbarDesktopCorner,"DesktopTaskbar_Sd");
            i(WindowsPreferenceId.TaskbarPosition,"DesktopTaskbar_Lo",3);
            i(WindowsPreferenceId.TaskbarSize,"DesktopTaskbar_TaskbarSize",1);
            i(WindowsPreferenceId.TaskbarSmallIcons,"DesktopTaskbar_IconSizePreference",2);
            i(WindowsPreferenceId.TouchKeyboard,"Notifications_SystemIconList_TouchKeyboard",2);
            b(WindowsPreferenceId.VirtualTouchpad,"DesktopTaskbar_Touchpad");
            i(WindowsPreferenceId.EmojiPanel,"Notifications_SystemIconList_EIPanel",2);
            b(WindowsPreferenceId.HiddenIcons,"SysTray_Chevron_Toggle");
            b(WindowsPreferenceId.Resume,"Toggle_Resume");
            b(WindowsPreferenceId.ClockVisible,"DesktopTaskbar_HideSystemTrayDateTime");
            b(WindowsPreferenceId.NotificationClock,"DesktopTaskbar_ClockInNotificationCenter");
            b(WindowsPreferenceId.UpdateEarly,"MusUpdate_ContinuousInnovationOptin2");
            b(WindowsPreferenceId.UpdateMicrosoft,"MusUpdate_IsMuOptInEnabled2");
            b(WindowsPreferenceId.UpdateExpedited,"MusUpdate_IsExpeditedEnabled");
            b(WindowsPreferenceId.UpdateMetered,"MusUpdate_AllowAutoWindowsUpdateDownloadOverMeteredNetwork");
            b(WindowsPreferenceId.UpdateRestartNotifications,"MusUpdate_RestartNotifications");
            b(WindowsPreferenceId.EnergySaver,"BatterySaver_LandingPage_OverrideControl");
            s(WindowsPreferenceId.PowerModeAC,"PowerAndBattery_PowerModes_AC");
            s(WindowsPreferenceId.PowerModeDC,"PowerAndBattery_PowerModes_DC");
            s(WindowsPreferenceId.SleepAC,"PowerTimeouts_Sleep_AC");
            s(WindowsPreferenceId.SleepDC,"PowerTimeouts_Sleep_DC");
            s(WindowsPreferenceId.ScreenAC,"PowerTimeouts_DisplayOff_AC");
            s(WindowsPreferenceId.ScreenDC,"PowerTimeouts_DisplayOff_DC");
            s(WindowsPreferenceId.DarkColorMode,"Personalize_Color_ColorMode");
            b(WindowsPreferenceId.AdaptiveBrightness,"Display_IsAutoBrightnessEnabled");
            b(WindowsPreferenceId.PresenceSleep,"PowerAndSleep_HumanPresence_LockOnSwitch");
            b(WindowsPreferenceId.PresenceDim,"PowerAndSleep_HumanPresence_DimSwitch");
            b(WindowsPreferenceId.PresenceWake,"PowerAndSleep_HumanPresence_WakeOnSwitch");
            b(WindowsPreferenceId.PresenceOnlooker,"PowerAndSleep_HumanPresence_OnlookerSwitch");
            b(WindowsPreferenceId.UsbPower,"Usb_AttemptRecoveryFromPowerDrain");
            return d;
        }
    }
}

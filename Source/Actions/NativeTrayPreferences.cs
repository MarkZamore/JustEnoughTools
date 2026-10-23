using System;

namespace WindowsPreferences
{
    internal enum TrayPreferenceKind { BatteryPercentage, ClockSeconds, PenWorkspace, NightLightEnabled }
    internal sealed class TrayPreferenceSnapshot
    {
        internal readonly bool Value,IsApplicable,IsEnabled,IsSetByGroupPolicy,IsUpdating;
        internal TrayPreferenceSnapshot(bool value,bool applicable,bool enabled,bool policy,bool updating)
        {Value=value;IsApplicable=applicable;IsEnabled=enabled;IsSetByGroupPolicy=policy;IsUpdating=updating;}
    }
    internal interface ITrayPreferenceSession:IDisposable
    {
        TrayPreferenceSnapshot Read();
        void Set(bool value);
    }
    internal sealed class UInt32PreferenceSnapshot
    {
        internal readonly uint Value;
        internal readonly bool IsApplicable,IsEnabled,IsSetByGroupPolicy,IsUpdating;
        internal UInt32PreferenceSnapshot(uint value,bool applicable,bool enabled,bool policy,bool updating)
        {Value=value;IsApplicable=applicable;IsEnabled=enabled;IsSetByGroupPolicy=policy;IsUpdating=updating;}
    }
    internal interface IUInt32PreferenceSession:IDisposable
    {
        UInt32PreferenceSnapshot Read();
        void Set(uint value);
    }
    // Preserve existing consumers and fake-session contracts while sharing the
    // typed Windows broker implementation with the remaining preferences.
    internal static class NativeTrayPreferences
    {
        internal static ITrayPreferenceSession Open(TrayPreferenceKind kind)
        {
            WindowsPreferenceId id;
            switch(kind){
                case TrayPreferenceKind.BatteryPercentage:id=WindowsPreferenceId.BatteryPercentage;break;
                case TrayPreferenceKind.ClockSeconds:id=WindowsPreferenceId.ClockSeconds;break;
                case TrayPreferenceKind.PenWorkspace:id=WindowsPreferenceId.PenWorkspace;break;
                case TrayPreferenceKind.NightLightEnabled:id=WindowsPreferenceId.NightLightEnabled;break;
                default:throw new ArgumentOutOfRangeException("kind");
            }
            return new BooleanSession(NativeWindowsPreferences.Open(id));
        }
        internal static IUInt32PreferenceSession OpenNightLightStrength()
        {return new UnsignedSession(NativeWindowsPreferences.Open(WindowsPreferenceId.NightLightStrength));}
        sealed class BooleanSession:ITrayPreferenceSession
        {
            readonly IWindowsPreferenceSession session;
            internal BooleanSession(IWindowsPreferenceSession session){this.session=session;}
            public TrayPreferenceSnapshot Read(){var s=session.Read();return new TrayPreferenceSnapshot(s.Value is bool&&(bool)s.Value,s.IsApplicable,s.IsEnabled,s.IsSetByGroupPolicy,s.IsUpdating);}
            public void Set(bool value){session.Set(value);}
            public void Dispose(){session.Dispose();}
        }
        sealed class UnsignedSession:IUInt32PreferenceSession
        {
            readonly IWindowsPreferenceSession session;
            internal UnsignedSession(IWindowsPreferenceSession session){this.session=session;}
            public UInt32PreferenceSnapshot Read(){var s=session.Read();return new UInt32PreferenceSnapshot(s.Value is uint?(uint)s.Value:0,s.IsApplicable,s.IsEnabled,s.IsSetByGroupPolicy,s.IsUpdating);}
            public void Set(uint value){session.Set(value);}
            public void Dispose(){session.Dispose();}
        }
    }
    internal static class NativeNightLightStrength
    {
        internal static IUInt32PreferenceSession Open(){return NativeTrayPreferences.OpenNightLightStrength();}
    }
}

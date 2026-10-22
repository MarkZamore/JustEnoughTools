using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsPreferences
{
    internal enum TrayPreferenceKind { BatteryPercentage, ClockSeconds, PenWorkspace, NightLightEnabled }

    internal sealed class TrayPreferenceSnapshot
    {
        internal readonly bool Value, IsApplicable, IsEnabled, IsSetByGroupPolicy, IsUpdating;
        internal TrayPreferenceSnapshot(bool value, bool applicable, bool enabled, bool setByGroupPolicy, bool updating)
        {
            Value = value; IsApplicable = applicable; IsEnabled = enabled;
            IsSetByGroupPolicy = setByGroupPolicy; IsUpdating = updating;
        }
    }

    internal interface ITrayPreferenceSession : IDisposable
    {
        TrayPreferenceSnapshot Read();
        void Set(bool value);
    }

    internal sealed class UInt32PreferenceSnapshot
    {
        internal readonly uint Value;
        internal readonly bool IsApplicable, IsEnabled, IsSetByGroupPolicy, IsUpdating;
        internal UInt32PreferenceSnapshot(uint value, bool applicable, bool enabled, bool setByGroupPolicy, bool updating)
        {
            Value = value; IsApplicable = applicable; IsEnabled = enabled;
            IsSetByGroupPolicy = setByGroupPolicy; IsUpdating = updating;
        }
    }

    internal interface IUInt32PreferenceSession : IDisposable
    {
        UInt32PreferenceSnapshot Read();
        void Set(uint value);
    }

    internal static class NativeNightLightStrength
    {
        internal static IUInt32PreferenceSession Open() { return NativeTrayPreferences.OpenNightLightStrength(); }
    }

    // Fixed current-user settings only. The broker returns the same setting model
    // used by Windows Settings; direct registry writes are not an equivalent API.
    internal static class NativeTrayPreferences
    {
        internal static ITrayPreferenceSession Open(TrayPreferenceKind kind) { return new SessionHandle(kind, false); }
        internal static IUInt32PreferenceSession OpenNightLightStrength() { return new SessionHandle(TrayPreferenceKind.BatteryPercentage, true); }

        // Keep native objects wholly within a transaction on an MTA thread. In
        // particular, an STA caller must not block the model's COM completion.
        sealed class SessionHandle : ITrayPreferenceSession, IUInt32PreferenceSession
        {
            readonly TrayPreferenceKind kind;
            readonly bool unsigned;
            readonly int thread = Thread.CurrentThread.ManagedThreadId;
            bool disposed;

            internal SessionHandle(TrayPreferenceKind kind, bool unsigned)
            {
                if (!unsigned && !Enum.IsDefined(typeof(TrayPreferenceKind), kind))
                    throw new ArgumentOutOfRangeException("kind");
                this.kind = kind; this.unsigned = unsigned;
            }

            T Execute<T>(bool expectUnsigned, Func<Session, T> action)
            {
                EnsureUsable();
                if (unsigned != expectUnsigned) throw new InvalidOperationException("Incorrect Windows setting value type.");
                Func<T> transaction = delegate
                {
                    using (var native = new Session(kind, unsigned)) return action(native);
                };
                if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA) return transaction();
                return Task.Run(transaction).GetAwaiter().GetResult();
            }

            public TrayPreferenceSnapshot Read()
            {
                return Execute(false, delegate(Session native) { return native.Read(); });
            }
            UInt32PreferenceSnapshot IUInt32PreferenceSession.Read()
            {
                return Execute(true, delegate(Session native) { return ((IUInt32PreferenceSession)native).Read(); });
            }
            public void Set(bool value)
            {
                Execute(false, delegate(Session native) { native.Set(value); return true; });
            }
            public void Set(uint value)
            {
                if (value > 100) throw new ArgumentOutOfRangeException("value");
                Execute(true, delegate(Session native) { native.Set(value); return true; });
            }
            void EnsureUsable()
            {
                if (Thread.CurrentThread.ManagedThreadId != thread)
                    throw new InvalidOperationException("The Windows setting session must remain on its creating thread.");
                if (disposed) throw new ObjectDisposedException("NativeTrayPreferences");
            }
            public void Dispose()
            {
                if (Thread.CurrentThread.ManagedThreadId != thread)
                    throw new InvalidOperationException("The Windows setting session must remain on its creating thread.");
                disposed = true;
            }
        }

        sealed class Session : ITrayPreferenceSession, IUInt32PreferenceSession
        {
            static readonly Guid BrokerId = new Guid("07353932-5353-4df4-94a3-e45d56087018");
            static readonly Guid DatabaseId = new Guid("d68a97b7-4f47-4cb6-9e1f-a01c1232f755");
            static readonly Guid SettingId = new Guid("40c037cc-d8bf-489e-8697-d66baa3221bf");
            static readonly Guid PropertyStaticsId = new Guid("629bdbc8-d932-4ff4-96b9-8d96c5c1e858");
            static readonly Guid PropertyId = new Guid("4bd682dd-7554-40e9-9a9b-82654ede7e62");
            readonly int thread = Thread.CurrentThread.ManagedThreadId;
            readonly TrayPreferenceKind kind;
            readonly bool unsigned;
            IntPtr broker, database, item, valueName, propertyFactory;
            bool initialized, disposed, modelInitialized;

            internal Session(TrayPreferenceKind kind) : this(kind, false) { }
            internal Session(TrayPreferenceKind kind, bool nightLightStrength)
            {
                string id;
                this.kind = kind;
                unsigned = nightLightStrength;
                if (nightLightStrength) id = "SystemSettings_Display_BlueLight_ColorTemperature";
                else switch (kind)
                {
                    case TrayPreferenceKind.BatteryPercentage: id = "SystemSettings_PowerAndBattery_BatteryPercentageToggle"; break;
                    case TrayPreferenceKind.ClockSeconds: id = "SystemSettings_DesktopTaskbar_SecondsInSystrayClock"; break;
                    case TrayPreferenceKind.PenWorkspace: id = "SystemSettings_DesktopTaskbar_InkWorkspace"; break;
                    case TrayPreferenceKind.NightLightEnabled: id = "SystemSettings_Display_BlueLight_ManualToggleQuickAction"; break;
                    default: throw new ArgumentOutOfRangeException("kind");
                }
                IntPtr brokerName = IntPtr.Zero, settingName = IntPtr.Zero, propertyName = IntPtr.Zero, raw = IntPtr.Zero;
                try
                {
                    int result = RoInitialize(1);
                    if (result >= 0) initialized = true;
                    else if (result != unchecked((int)0x80010106)) Check(result); // RPC_E_CHANGED_MODE: already initialized STA.
                    brokerName = String("SystemSettings.DataModel.SettingsBroker");
                    Check(RoActivateInstance(brokerName, out raw));
                    broker = Query(raw, BrokerId);
                    Release(ref raw);
                    // ISettingsBroker.GetSettingsDatabase: current user, never the
                    // system database or another user's setting model.
                    Check(Method<GetObject>(broker, 6)(broker, out raw));
                    database = Query(raw, DatabaseId);
                    Release(ref raw);
                    settingName = String(id);
                    Check(Method<GetValue>(database, 6)(database, settingName, out raw));
                    item = Query(raw, SettingId);
                    valueName = String("Value");
                    propertyName = String("Windows.Foundation.PropertyValue");
                    Guid iid = PropertyStaticsId;
                    Check(RoGetActivationFactory(propertyName, ref iid, out propertyFactory));
                }
                catch { Release(ref raw); Dispose(); throw; }
                finally
                {
                    Release(ref raw);
                    DeleteString(ref brokerName); DeleteString(ref settingName); DeleteString(ref propertyName);
                }
            }

            public TrayPreferenceSnapshot Read()
            {
                CheckType(false);
                var state = ReadCore();
                return new TrayPreferenceSnapshot((bool)state.Value, state.Applicable, state.Enabled, state.Managed, state.Updating);
            }

            UInt32PreferenceSnapshot IUInt32PreferenceSession.Read()
            {
                CheckType(true);
                var state = ReadCore();
                return new UInt32PreferenceSnapshot((uint)state.Value, state.Applicable, state.Enabled, state.Managed, state.Updating);
            }

            sealed class Snapshot
            {
                internal object Value;
                internal bool Applicable, Enabled, Managed, Updating;
                internal int ValueResult;
            }

            Snapshot ReadCore()
            {
                EnsureUsable();
                // Some models initialize after GetValue without IsUpdating. A
                // first read must hydrate a stable value as well as its flags.
                var elapsed = Stopwatch.StartNew();
                long stableSince = 0;
                Snapshot state, previous = null;
                do
                {
                    state = ReadOnce();
                    if (modelInitialized) break;
                    if (previous == null || !SameState(previous, state)) stableSince = elapsed.ElapsedMilliseconds;
                    previous = state;
                    if (state.ValueResult >= 0 && !state.Updating &&
                        (state.Managed || (state.Applicable && state.Enabled)) &&
                        elapsed.ElapsedMilliseconds - stableSince >= 100) break;
                    if (elapsed.ElapsedMilliseconds >= 1000) break;
                    Thread.Sleep(25);
                } while (true);
                modelInitialized = true;
                if (state.Applicable) Check(state.ValueResult);
                return state;
            }

            static bool SameState(Snapshot a, Snapshot b)
            {
                return a.ValueResult == b.ValueResult && Equals(a.Value, b.Value) &&
                    a.Applicable == b.Applicable && a.Enabled == b.Enabled &&
                    a.Managed == b.Managed && a.Updating == b.Updating;
            }

            Snapshot ReadOnce()
            {
                var state = new Snapshot { Value = unsigned ? (object)0u : false };
                IntPtr raw = IntPtr.Zero, property = IntPtr.Zero;
                try
                {
                    // Request the value before flags to initialize lazy models.
                    state.ValueResult = Method<GetValue>(item, 13)(item, valueName, out raw);
                    if (state.ValueResult >= 0)
                    {
                        property = Query(raw, PropertyId);
                        int type;
                        Check(Method<GetTypeValue>(property, 6)(property, out type));
                        if (type != (unsigned ? 5 : 11)) throw new InvalidOperationException("Windows returned an unexpected setting type.");
                        if (unsigned)
                        {
                            uint value;
                            Check(Method<GetUInt32>(property, 12)(property, out value));
                            if (value > 100) throw new InvalidOperationException("Windows returned an out-of-range setting value.");
                            state.Value = value;
                        }
                        else state.Value = Boolean(property, 18);
                    }
                }
                finally { Release(ref property); Release(ref raw); }
                // ISettingItem ABI and these flags are defined by the installed
                // SystemSettings.DataModel metadata. WinRT boolean is one byte.
                state.Managed = Boolean(item, 8); state.Enabled = Boolean(item, 9);
                state.Applicable = Boolean(item, 10); state.Updating = Boolean(item, 12);
                return state;
            }

            public void Set(bool value)
            {
                CheckType(false);
                SetCore(value);
            }

            public void Set(uint value)
            {
                CheckType(true);
                if (value > 100) throw new ArgumentOutOfRangeException("value");
                SetCore(value);
            }

            void CheckType(bool expectUnsigned)
            {
                EnsureUsable();
                if (unsigned != expectUnsigned) throw new InvalidOperationException("Incorrect Windows setting value type.");
            }

            void SetCore(object value)
            {
                EnsureUsable();
                var before = ReadCore();
                if (before.Updating) throw new InvalidOperationException("The Windows setting is already updating.");
                if (!before.Applicable || !before.Enabled || before.Managed)
                    throw new InvalidOperationException("The Windows setting cannot be changed in this context.");
                // An explicit Set must issue the target even when this model
                // agrees: a previous asynchronous write may still be pending.
                IntPtr property = IntPtr.Zero;
                try
                {
                    // IPropertyValueStatics scalar methods: documented Windows SDK ABI.
                    if (unsigned) Check(Method<CreateUInt32>(propertyFactory, 11)(propertyFactory, (uint)value, out property));
                    else Check(Method<CreateBoolean>(propertyFactory, 17)(propertyFactory, (bool)value ? (byte)1 : (byte)0, out property));
                    if (property == IntPtr.Zero) throw new InvalidOperationException("Windows returned no property value.");
                    Check(Method<SetValue>(item, 14)(item, valueName, property));
                }
                finally { Release(ref property); }

                // Same-item readback can expose optimistic values while the
                // Windows change is still pending. Confirm through independent
                // broker models over a bounded stability window instead.
                var elapsed = Stopwatch.StartNew();
                long matchingSince = -1;
                int matchingViews = 0;
                do
                {
                    Snapshot after;
                    using (var observer = new Session(kind, unsigned)) after = observer.ReadCore();
                    if (!after.Applicable || !after.Enabled || after.Managed)
                        throw new IOException("Windows could not confirm the setting in this context.");
                    if (!after.Updating && Equals(after.Value, value))
                    {
                        if (matchingSince < 0) matchingSince = elapsed.ElapsedMilliseconds;
                        matchingViews++;
                        if (matchingViews >= 3 && elapsed.ElapsedMilliseconds - matchingSince >= 500) return;
                    }
                    else { matchingSince = -1; matchingViews = 0; }
                    Thread.Sleep(25);
                } while (elapsed.ElapsedMilliseconds < 5000);
                throw new TimeoutException("Windows did not confirm a stable requested setting.");
            }

            void EnsureUsable()
            {
                EnsureThread();
                if (disposed) throw new ObjectDisposedException("NativeTrayPreferences");
            }
            void EnsureThread()
            {
                if (Thread.CurrentThread.ManagedThreadId != thread)
                    throw new InvalidOperationException("The Windows setting session must remain on its creating thread.");
            }
            public void Dispose()
            {
                EnsureThread();
                if (disposed) return;
                disposed = true;
                Release(ref propertyFactory); Release(ref item); Release(ref database); Release(ref broker);
                DeleteString(ref valueName);
                if (initialized) { initialized = false; RoUninitialize(); }
            }
        }

        static IntPtr Query(IntPtr value, Guid iid)
        {
            if (value == IntPtr.Zero) throw new InvalidOperationException("Windows returned no setting object.");
            IntPtr result;
            Check(Method<QueryInterface>(value, 0)(value, ref iid, out result));
            if (result == IntPtr.Zero) throw new InvalidOperationException("Windows returned no setting interface.");
            return result;
        }
        static bool Boolean(IntPtr value, int slot)
        {
            byte result;
            Check(Method<GetBoolean>(value, slot)(value, out result));
            return result != 0;
        }
        static T Method<T>(IntPtr value, int slot) where T : class
        {
            return (T)(object)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(value), checked(slot * IntPtr.Size)), typeof(T));
        }
        static IntPtr String(string value)
        {
            IntPtr result;
            Check(WindowsCreateString(value, value.Length, out result));
            return result;
        }
        static void DeleteString(ref IntPtr value)
        {
            if (value == IntPtr.Zero) return;
            IntPtr old = value; value = IntPtr.Zero; WindowsDeleteString(old);
        }
        static void Release(ref IntPtr value)
        {
            if (value == IntPtr.Zero) return;
            IntPtr old = value; value = IntPtr.Zero; Marshal.Release(old);
        }
        static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int QueryInterface(IntPtr self, ref Guid iid, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetObject(IntPtr self, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetValue(IntPtr self, IntPtr name, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetValue(IntPtr self, IntPtr name, IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetBoolean(IntPtr self, out byte value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetTypeValue(IntPtr self, out int type);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetUInt32(IntPtr self, out uint value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateBoolean(IntPtr self, byte value, out IntPtr property);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateUInt32(IntPtr self, uint value, out IntPtr property);
        [DllImport("combase.dll")] static extern int RoInitialize(uint initialization);
        [DllImport("combase.dll")] static extern void RoUninitialize();
        [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string value, int length, out IntPtr result);
        [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr value);
        [DllImport("combase.dll")] static extern int RoActivateInstance(IntPtr name, out IntPtr value);
        [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr name, ref Guid iid, out IntPtr value);
    }
}

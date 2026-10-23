using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsPreferences
{
    internal sealed class WindowsPreferenceSnapshot
    {
        internal readonly object Value;
        internal readonly bool IsApplicable,IsEnabled,IsSetByGroupPolicy,IsUpdating;
        internal readonly string[] Choices;
        internal WindowsPreferenceSnapshot(object value,bool applicable,bool enabled,bool policy,bool updating,string[] choices=null)
        {Value=value;IsApplicable=applicable;IsEnabled=enabled;IsSetByGroupPolicy=policy;IsUpdating=updating;Choices=choices??new string[0];}
        internal bool Writable {get{return IsApplicable&&IsEnabled&&!IsSetByGroupPolicy&&!IsUpdating;}}
    }
    internal interface IWindowsPreferenceSession:IDisposable
    {
        WindowsPreferenceSnapshot Read();
        void Set(object value);
    }
    internal static class NativeWindowsPreferences
    {
        internal static IWindowsPreferenceSession Open(WindowsPreferenceId id) {WindowsPreferenceCatalog.Get(id);return new Handle(id);}
        internal static bool IsConfirmedTaskbarMode(object stored,object value,string[] choices)
        {
            return stored is int && (int)stored>=0 && (int)stored<=2 && choices!=null && choices.Length==3 &&
                !string.IsNullOrEmpty(choices[0]) && !string.IsNullOrEmpty(choices[1]) && !string.IsNullOrEmpty(choices[2]) &&
                choices[0]!=choices[1] && choices[0]!=choices[2] && choices[1]!=choices[2] && Equals(value,choices[(int)stored]);
        }
        sealed class Handle:IWindowsPreferenceSession
        {
            readonly WindowsPreferenceId id;
            readonly int thread=Thread.CurrentThread.ManagedThreadId;
            bool disposed;
            internal Handle(WindowsPreferenceId id){this.id=id;}
            T Execute<T>(Func<Session,T> action)
            {
                CheckThread();if(disposed)throw new ObjectDisposedException("NativeWindowsPreferences");
                Func<T> call=delegate{using(var session=new Session(id))return action(session);};
                return Thread.CurrentThread.GetApartmentState()==ApartmentState.MTA?call():Task.Run(call).GetAwaiter().GetResult();
            }
            public WindowsPreferenceSnapshot Read(){return Execute(s=>s.Read());}
            public void Set(object value){Execute(s=>{s.Set(value);return true;});}
            void CheckThread(){if(Thread.CurrentThread.ManagedThreadId!=thread)throw new InvalidOperationException("Windows preference sessions must remain on their creating thread.");}
            public void Dispose(){CheckThread();disposed=true;}
        }

        sealed class Session : IDisposable
        {
            static readonly Guid BrokerId = new Guid("07353932-5353-4df4-94a3-e45d56087018");
            static readonly Guid DatabaseId = new Guid("d68a97b7-4f47-4cb6-9e1f-a01c1232f755");
            static readonly Guid SettingId = new Guid("40c037cc-d8bf-489e-8697-d66baa3221bf");
            static readonly Guid PropertyStaticsId = new Guid("629bdbc8-d932-4ff4-96b9-8d96c5c1e858");
            static readonly Guid PropertyId = new Guid("4bd682dd-7554-40e9-9a9b-82654ede7e62");
            readonly int thread = Thread.CurrentThread.ManagedThreadId;
            readonly WindowsPreferenceId kind;
            readonly WindowsPreferenceDefinition definition;
            IntPtr broker, database, item, valueName, propertyFactory;
            bool initialized, disposed, modelInitialized;

            internal Session(WindowsPreferenceId kind)
            {
                this.kind=kind; definition=WindowsPreferenceCatalog.Get(kind);
                string id=definition.SettingId;
                IntPtr brokerName = IntPtr.Zero, settingName = IntPtr.Zero, propertyName = IntPtr.Zero, raw = IntPtr.Zero;
                try
                {
                    int result = RoInitialize(1);
                    if (result >= 0) initialized = true;
                    else if (result != unchecked((int)0x80010106)) Check(result); // RPC_E_CHANGED_MODE: already initialized STA.
                    // The remote broker model accepts this list into its cache
                    // without calling the taskbar handler. Activate the same
                    // current-user database locally for the verified mode action.
                    if (kind == WindowsPreferenceId.TaskbarMultiMonitorMode)
                    {
                        brokerName = String("SystemSettings.DataModel.SettingsDatabase");
                        Check(RoActivateInstance(brokerName, out raw));
                    }
                    else
                    {
                        brokerName = String("SystemSettings.DataModel.SettingsBroker");
                        Check(RoActivateInstance(brokerName, out raw));
                        broker = Query(raw, BrokerId);
                        Release(ref raw);
                        Check(Method<GetObject>(broker, 6)(broker, out raw));
                    }
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

            internal WindowsPreferenceSnapshot Read()
            {
                var state=ReadCore();
                var choices=definition.PropertyType==12 && state.Applicable ? ReadChoices() : new string[0];
                if(kind==WindowsPreferenceId.TaskbarMultiMonitorMode && state.Applicable && !NativeModeMatches(state.Value,choices))
                    throw new IOException("Windows не подтвердила системный режим панели задач.");
                return new WindowsPreferenceSnapshot(state.Value,state.Applicable,state.Enabled,state.Managed,state.Updating,choices);
            }

            static bool NativeModeMatches(object value,string[] choices)
            {
                if(choices.Length!=3)return false;
                using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                {
                    object stored=key==null?null:key.GetValue("MMTaskbarMode",null);
                    return IsConfirmedTaskbarMode(stored,value,choices);
                }
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
                if (state.Applicable) definition.Validate(state.Value);
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
                var state = new Snapshot();
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
                        if(type!=definition.PropertyType)throw new InvalidOperationException("Windows returned an unexpected setting type.");
                        switch(type) {
                            case 11: state.Value=Boolean(property,18);break;
                            case 4: int i;Check(Method<GetInt32>(property,11)(property,out i));state.Value=i;break;
                            case 5: uint u;Check(Method<GetUInt32>(property,12)(property,out u));state.Value=u;break;
                            case 12: state.Value=ReadStringProperty(property);break;
                            default:throw new InvalidOperationException("Unsupported Windows setting type.");
                        }
                    }
                }
                finally { Release(ref property); Release(ref raw); }
                // ISettingItem ABI and these flags are defined by the installed
                // SystemSettings.DataModel metadata. WinRT boolean is one byte.
                state.Managed = Boolean(item, 8); state.Enabled = Boolean(item, 9);
                state.Applicable = Boolean(item, 10); state.Updating = Boolean(item, 12);
                return state;
            }

            internal void Set(object value)
            {
                definition.Validate(value);SetCore(value);
            }

            void SetCore(object value)
            {
                EnsureUsable();
                var before = ReadCore();
                if (before.Updating) throw new InvalidOperationException("The Windows setting is already updating.");
                if (!before.Applicable || !before.Enabled || before.Managed)
                    throw new InvalidOperationException("The Windows setting cannot be changed in this context.");
                if(definition.PropertyType==12 && Array.IndexOf(ReadChoices(),(string)value)<0)
                    throw new InvalidOperationException("The requested Windows choice is unavailable.");
                // An explicit Set must issue the target even when this model
                // agrees: a previous asynchronous write may still be pending.
                IntPtr property = IntPtr.Zero;
                try
                {
                    // IPropertyValueStatics scalar methods: documented Windows SDK ABI.
                    switch(definition.PropertyType) {
                        case 11: Check(Method<CreateBoolean>(propertyFactory,17)(propertyFactory,(bool)value?(byte)1:(byte)0,out property));break;
                        case 4: Check(Method<CreateInt32>(propertyFactory,10)(propertyFactory,(int)value,out property));break;
                        case 5: Check(Method<CreateUInt32>(propertyFactory,11)(propertyFactory,(uint)value,out property));break;
                        case 12: IntPtr text=String((string)value);try{Check(Method<CreateString>(propertyFactory,18)(propertyFactory,text,out property));}finally{DeleteString(ref text);}break;
                    }
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
                    using (var observer = new Session(kind)) after = observer.ReadCore();
                    if (!after.Applicable || !after.Enabled || after.Managed)
                        throw new IOException("Windows could not confirm the setting in this context.");
                    if (!after.Updating && Equals(after.Value, value) &&
                        (kind!=WindowsPreferenceId.TaskbarMultiMonitorMode || NativeModeMatches(value,ReadChoices())))
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

            string[] ReadChoices()
            {
                IntPtr name=String("PossibleValues"),raw=IntPtr.Zero,vector=IntPtr.Zero;
                try {
                    Check(Method<GetValue>(item,13)(item,name,out raw));
                    bool strings=false;
                    Guid iid=new Guid("b32bdca4-5e52-5b27-bc5d-d66a1a268c2a");
                    int hr=Marshal.QueryInterface(raw,ref iid,out vector);
                    if(hr==unchecked((int)0x80004002)) {iid=new Guid("98b9acc1-4b56-532e-ac73-03d5291cca90");Check(Marshal.QueryInterface(raw,ref iid,out vector));strings=true;}
                    else Check(hr);
                    uint count;Check(Method<GetUInt32>(vector,7)(vector,out count));
                    if(count==0||count>64)throw new InvalidOperationException("Windows returned an unexpected choice list.");
                    var choices=new string[count];
                    for(uint index=0;index<count;index++) {
                        IntPtr entry=IntPtr.Zero,p=IntPtr.Zero;
                        try {
                            Check(Method<GetAt>(vector,6)(vector,index,out entry));
                            if(strings)choices[index]=HString(entry);
                            else {p=Query(entry,PropertyId);int type;Check(Method<GetTypeValue>(p,6)(p,out type));if(type!=12)throw new InvalidOperationException("Windows returned an unexpected choice type.");choices[index]=ReadStringProperty(p);}
                            if(string.IsNullOrEmpty(choices[index])||choices[index].Length>512)throw new InvalidOperationException("Invalid Windows choice.");
                        } finally {Release(ref p);if(strings)DeleteString(ref entry);else Release(ref entry);}
                    }
                    return choices;
                } finally {Release(ref vector);Release(ref raw);DeleteString(ref name);}
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

        static string HString(IntPtr value) { uint length;IntPtr text=WindowsGetStringRawBuffer(value,out length);return Marshal.PtrToStringUni(text,(int)length); }
        static string ReadStringProperty(IntPtr property) {IntPtr value;Check(Method<GetObject>(property,19)(property,out value));try{return HString(value);}finally{DeleteString(ref value);}}
        [DllImport("combase.dll")] static extern IntPtr WindowsGetStringRawBuffer(IntPtr value,out uint length);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetInt32(IntPtr self,out int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateInt32(IntPtr self,int value,out IntPtr property);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateString(IntPtr self,IntPtr value,out IntPtr property);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetAt(IntPtr self,uint index,out IntPtr value);
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

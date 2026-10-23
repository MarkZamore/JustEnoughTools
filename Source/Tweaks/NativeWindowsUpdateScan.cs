using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace JustEnoughTools.Tweaks
{
    internal sealed class WindowsUpdateScanSnapshot
    {
        public bool IsScanning;
        public bool IsWorking;
    }

    internal interface IWindowsUpdateScanSession : IDisposable
    {
        WindowsUpdateScanSnapshot Read();
        void StartScan(bool userInitiated);
    }

    // This is the public Windows.Management.Update API, not the IoT-only
    // Windows.System.Update API. It neither registers an update administrator
    // nor installs an app package. StartScan(true) requests the same scan as
    // Settings' Check for updates; it does not promise optional-update approval.
    internal static class NativeWindowsUpdateScan
    {
        internal static bool IsAvailable()
        {
            return InMta(() => IsAvailable(() => new Session()));
        }

        internal static bool IsAvailable(Func<IWindowsUpdateScanSession> open)
        {
            try { using (var session = open()) return session.Read() != null; }
            catch { return false; }
        }

        internal static WindowsUpdateScanSnapshot Read()
        {
            return InMta(() => { using (var session = new Session()) return session.Read(); });
        }

        internal static string Start()
        {
            return InMta(() => { using (var session = new Session()) return Start(session); });
        }

        internal static string Start(IWindowsUpdateScanSession session)
        {
            if (session == null) throw new ArgumentNullException("session");
            var before = session.Read();
            if (before == null) throw new InvalidOperationException("Не удалось проверить состояние обновлений Windows.");
            if (before.IsScanning) return "Windows уже проверяет обновления.";
            session.StartScan(true);
            // A successful void request is an accepted launch, not proof that
            // a scan or download/install has completed. Windows owns the work.
            return "Проверка обновлений запущена средствами Windows.";
        }

        static T InMta<T>(Func<T> action)
        {
            return Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA
                ? action() : Task.Run(action).GetAwaiter().GetResult();
        }

        sealed class Session : IWindowsUpdateScanSession
        {
            IntPtr manager;
            bool initialized;

            internal Session()
            {
                IntPtr name = IntPtr.Zero, client = IntPtr.Zero, factory = IntPtr.Zero, instance = IntPtr.Zero;
                try
                {
                    Check(RoInitialize(1)); initialized = true;
                    name = HString("Windows.Management.Update.WindowsUpdateManager");
                    var factoryId = new Guid("1b394df8-decb-5f44-b47c-6ccf3bcfdb37");
                    Check(RoGetActivationFactory(name, ref factoryId, out factory));
                    client = HString("JustEnoughTools");
                    Check(Method<Create>(factory, 6)(factory, client, out instance));
                    var managerId = new Guid("5dd966c0-a71a-5602-bbd0-09a70e4573fa");
                    Check(Marshal.QueryInterface(instance, ref managerId, out manager));
                }
                finally
                {
                    Release(ref instance); Release(ref factory);
                    if (client != IntPtr.Zero) WindowsDeleteString(client);
                    if (name != IntPtr.Zero) WindowsDeleteString(name);
                    if (manager == IntPtr.Zero) Dispose();
                }
            }

            public WindowsUpdateScanSnapshot Read()
            {
                EnsureAlive(); byte scanning, working;
                Check(Method<GetBoolean>(manager, 18)(manager, out scanning));
                Check(Method<GetBoolean>(manager, 19)(manager, out working));
                return new WindowsUpdateScanSnapshot { IsScanning = scanning != 0, IsWorking = working != 0 };
            }

            public void StartScan(bool userInitiated)
            {
                EnsureAlive();
                Check(Method<StartScanCall>(manager, 24)(manager, userInitiated ? (byte)1 : (byte)0));
            }

            void EnsureAlive() { if (manager == IntPtr.Zero) throw new ObjectDisposedException("WindowsUpdateManager"); }
            public void Dispose()
            {
                Release(ref manager);
                if (initialized) { initialized = false; RoUninitialize(); }
            }
        }

        static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
        static IntPtr HString(string value)
        {
            IntPtr result; Check(WindowsCreateString(value, value.Length, out result)); return result;
        }
        static void Release(ref IntPtr value) { if (value != IntPtr.Zero) { Marshal.Release(value); value = IntPtr.Zero; } }
        static T Method<T>(IntPtr instance, int slot) where T : class
        {
            return (T)(object)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size), typeof(T));
        }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Create(IntPtr self, IntPtr clientId, out IntPtr instance);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetBoolean(IntPtr self, out byte value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int StartScanCall(IntPtr self, byte userInitiated);
        [DllImport("combase.dll")] static extern int RoInitialize(uint initType);
        [DllImport("combase.dll")] static extern void RoUninitialize();
        [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr className, ref Guid interfaceId, out IntPtr factory);
        [DllImport("combase.dll")] static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string value, int length, out IntPtr result);
        [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr value);
    }
}

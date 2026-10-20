using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using JustEnoughTools.Security;
using JustEnoughTools.Updates;

namespace JustEnoughTools
{
    internal static class Program
    {
        internal static int ReleaseNumber
        {
            get
            {
                var value = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(attribute => attribute.Key == "ReleaseNumber");
                int number;
                return value != null && int.TryParse(value.Value, out number) ? number : 1;
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            DependencyBootstrap.Initialize();
            AppContext.SetSwitch("Switch.System.Windows.Media.MediaContext.DisableDirtyRectangles", true);
            if (args.Length == 1 && args[0] == "--self-test")
            {
                try { EmbeddedDependencyProbe.Run(); Console.WriteLine("Embedded dependencies OK"); return 0; }
                catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            }
            int workerExitCode;
            if (SecurityCoordinator.TryRunWorker(args, out workerExitCode)) return workerExitCode;
            // Unknown/private legacy arguments must never launch a window or
            // accidentally become an instruction to change Windows settings.
            if (args.Length != 0) return 2;
            string path = Process.GetCurrentProcess().MainModule.FileName;
            try
            {
                if (UpdateService.TryRequestActiveInstallationRestart(path)) return 0;
                using (var instance = new ApplicationInstance(path))
                {
                    if (!instance.IsPrimary) { instance.ActivateExisting(); return 0; }
                    if (UpdateService.TryApplyPreparedBeforeStartup(path, ReleaseNumber)) return 0;
                    var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    var controller = new MainWindowController(ReleaseNumber, path, SecurityCoordinator.ConfigureAsync);
                    app.MainWindow = controller.Window;
                    instance.OnActivate(() => app.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (controller.Window.WindowState == WindowState.Minimized) controller.Window.WindowState = WindowState.Normal;
                        controller.Window.Show();
                        controller.Window.Activate();
                    })));
                    return app.Run(controller.Window);
                }
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "JustEnoughTools", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

    }

    internal sealed class ApplicationInstance : IDisposable
    {
        private readonly Mutex mutex;
        private readonly EventWaitHandle activation;
        private RegisteredWaitHandle wait;
        internal bool IsPrimary { get; private set; }
        internal ApplicationInstance(string path)
        {
            string key = UpdateService.GetInstanceKey(path);
            mutex = new Mutex(false, "Local\\JustEnoughTools.Window." + key);
            try { IsPrimary = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { IsPrimary = true; }
            activation = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\JustEnoughTools.Activate." + key);
        }
        internal void ActivateExisting() { activation.Set(); }
        internal void OnActivate(Action callback)
        {
            wait = ThreadPool.RegisterWaitForSingleObject(activation, (state, timedOut) => callback(), null, Timeout.Infinite, false);
        }
        public void Dispose()
        {
            if (wait != null) wait.Unregister(null);
            activation.Dispose();
            if (IsPrimary) mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}

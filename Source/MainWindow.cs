using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JustEnoughTools.Updates;

namespace JustEnoughTools
{
    public sealed class MainWindowController
    {
        readonly Func<IProgress<string>, Task<string>> removeProtection;
        readonly UpdateService updates;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly Button deleteButton;
        readonly Button updateButton;
        readonly TextBlock protectionStatus;
        readonly TextBlock updateText;
        readonly ProgressBar updateProgress;
        Task updateTask;
        bool deleting;
        bool deleted;
        bool closing;
        bool closeAllowed;
        bool restartAfterUpdate;
        bool installScheduleFailed;
        bool disposed;
        bool updateReady;

        public Window Window { get; private set; }

        public MainWindowController(int releaseNumber, string executablePath,
            Func<IProgress<string>, Task<string>> removeProtection = null)
        {
            this.removeProtection = removeProtection;
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream theme = RequiredResource(assembly, "Theme"))
                Application.Current.Resources = (ResourceDictionary)XamlReader.Load(theme);
            WindowsTheme.Initialize(Application.Current);
            using (Stream content = RequiredResource(assembly, "Window"))
                Window = (Window)XamlReader.Load(content);
            using (Stream icon = assembly.GetManifestResourceStream("AppIcon"))
            {
                if (icon != null)
                    Window.Icon = BitmapFrame.Create(icon, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            }
            WindowsTheme.Attach(Window);
            ControlMotion.AttachCategory((Expander)Window.FindName("WalkieTalkieSection"));
            ControlMotion.AttachCategory((Expander)Window.FindName("ClickerSection"));
            deleteButton = (Button)Window.FindName("DeleteProtectionButton");
            updateButton = (Button)Window.FindName("UpdateButton");
            protectionStatus = (TextBlock)Window.FindName("ProtectionStatus");
            updateText = (TextBlock)Window.FindName("UpdateProgressText");
            updateProgress = (ProgressBar)Window.FindName("UpdateProgressBar");
            deleteButton.IsEnabled = removeProtection != null;
            deleteButton.Click += DeleteProtection;
            updateButton.Click += UpdateAndRestart;
            updates = new UpdateService(executablePath, releaseNumber);
            updates.StatusChanged += OnUpdateStatus;
            Window.Loaded += StartUpdateCheck;
            Window.Closing += Closing;
            Window.Closed += Closed;
        }

        static Stream RequiredResource(Assembly assembly, string name)
        {
            Stream stream = assembly.GetManifestResourceStream(name);
            if (stream == null) throw new InvalidOperationException("Не найден встроенный ресурс: " + name);
            return stream;
        }

        public void Show() { Window.Show(); }

        async void StartUpdateCheck(object sender, RoutedEventArgs args)
        {
            Window.Loaded -= StartUpdateCheck;
            updateTask = Task.Run(delegate { return updates.CheckAndPrepareAsync(lifetime.Token); });
            try { await updateTask; }
            catch (OperationCanceledException) { }
            catch
            {
                if (!closing && !disposed) SetUpdateStatus("Не удалось проверить обновления", null, false);
            }
        }

        void OnUpdateStatus(UpdateStatus status)
        {
            if (disposed || Window.Dispatcher.HasShutdownStarted) return;
            Action apply = delegate
            {
                if (!disposed && !closing) SetUpdateStatus(status.Text, status.Fraction, status.Ready);
            };
            if (Window.Dispatcher.CheckAccess()) apply();
            else Window.Dispatcher.BeginInvoke(apply);
        }

        void SetUpdateStatus(string text, double? fraction, bool ready)
        {
            updateReady = ready;
            updateText.Text = text;
            updateProgress.IsIndeterminate = !fraction.HasValue;
            if (fraction.HasValue) updateProgress.Value = Math.Max(0, Math.Min(100, fraction.Value * 100));
            updateProgress.Foreground = (Brush)Window.FindResource(
                !ready && (!fraction.HasValue || (fraction.Value > 0 && fraction.Value < 1))
                    ? "Brush.ProgressFill" : "Brush.ProgressTrack");
            RefreshButtons();
        }

        void RefreshButtons()
        {
            deleteButton.IsEnabled = removeProtection != null && !deleting && !deleted && !closing;
            updateButton.IsEnabled = updateReady && updates.HasPreparedUpdate && !deleting && !closing;
        }

        async void DeleteProtection(object sender, RoutedEventArgs args)
        {
            if (deleting || deleted || closing || removeProtection == null) return;
            deleting = true;
            protectionStatus.Text = "Проверка возможности штатного отключения Defender…";
            RefreshButtons();
            IProgress<string> progress = new Progress<string>(delegate(string text)
            {
                if (!disposed) protectionStatus.Text = text;
            });
            try
            {
                string result = await Task.Run(delegate { return removeProtection(progress); });
                deleted = true;
                protectionStatus.Text = String.IsNullOrWhiteSpace(result) ? "Настройка подтверждена." : result;
            }
            catch (Exception error)
            {
                protectionStatus.Text = "Настройка не подтверждена. " + error.Message;
            }
            finally
            {
                deleting = false;
                RefreshButtons();
            }
        }

        void UpdateAndRestart(object sender, RoutedEventArgs args)
        {
            if (closing || deleting || !updates.HasPreparedUpdate) return;
            restartAfterUpdate = true;
            Window.Close();
        }

        async void Closing(object sender, CancelEventArgs args)
        {
            if (closeAllowed) return;
            args.Cancel = true;
            if (closing) return;
            if (deleting)
            {
                protectionStatus.Text = "Дождитесь завершения настройки.";
                return;
            }
            closing = true;
            RefreshButtons();
            lifetime.Cancel();
            try
            {
                if (updateTask != null) await updateTask;
            }
            catch { }
            if (updates.HasPreparedUpdate && (!installScheduleFailed || restartAfterUpdate))
            {
                string error = null;
                bool scheduled;
                try { scheduled = updates.TryScheduleInstall(restartAfterUpdate, out error); }
                catch (Exception failure) { scheduled = false; error = failure.Message; }
                if (!scheduled)
                {
                    installScheduleFailed = true;
                    closing = false;
                    restartAfterUpdate = false;
                    updateText.Text = String.IsNullOrWhiteSpace(error) ? "Не удалось установить обновление" : error;
                    RefreshButtons();
                    return;
                }
            }
            closeAllowed = true;
            _ = Window.Dispatcher.BeginInvoke(new Action(Window.Close));
        }

        void Closed(object sender, EventArgs args)
        {
            disposed = true;
            updates.StatusChanged -= OnUpdateStatus;
            updates.Dispose();
            lifetime.Dispose();
        }
    }
}

using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JustEnoughTools.Updates;
using JustEnoughTools.Security;
using JustEnoughTools.Export;
using JustEnoughTools.Actions;
using JustEnoughTools.Tweaks;

namespace JustEnoughTools
{
    public sealed class MainWindowController
    {
        readonly Func<Task<SecurityReport>> configureSecurity;
        readonly Func<SecuritySnapshot> inspectSecurity;
        readonly UpdateService updates;
        readonly string executablePath;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly Button configureButton;
        readonly Button saveButton;
        readonly Button iconButton;
        readonly CheckBox addToStartMenu;
        readonly Popup iconPopup;
        readonly SettingExportUi securityExport, taskbarExport;
        readonly Button applyTaskbarButton;
        readonly ITweakService tweakService;
        readonly TweakUi tweakUi;
        readonly Button updateButton;
        readonly TextBlock updateText;
        readonly TextBlock protectionDescription;
        readonly ProgressBar updateProgress;
        Task updateTask;
        bool configuring;
        bool saving;
        bool closeAfterAction;
        bool closing;
        bool closeAllowed;
        bool restartAfterUpdate;
        bool installScheduleFailed;
        bool disposed;
        bool updateReady;
        bool securityChecked;
        bool checkingSecurity;
        bool securityNeedsChanges;
        IDisposable shortcutMaintenance;

        public Window Window { get; private set; }

        public MainWindowController(int releaseNumber, string executablePath,
            Func<Task<SecurityReport>> configureSecurity = null,
            Func<SecuritySnapshot> inspectSecurity = null)
            : this(releaseNumber, executablePath, configureSecurity, inspectSecurity, null) { }

        internal MainWindowController(int releaseNumber, string executablePath,
            Func<Task<SecurityReport>> configureSecurity, Func<SecuritySnapshot> inspectSecurity, ITweakService tweakService)
        {
            this.executablePath = executablePath;
            this.configureSecurity = configureSecurity;
            this.inspectSecurity = inspectSecurity ?? SecurityEngine.Inspect;
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
            SmoothScrollingController.Attach((ScrollViewer)Window.FindName("SettingsScroll"));
            ControlMotion.AttachCategory((Expander)Window.FindName("ClickerSection"));
            ControlMotion.AttachCategory((Expander)Window.FindName("WindowsTweakerSection"));
            this.tweakService = tweakService ?? new TweakService(executablePath);
            tweakUi = new TweakUi(Window);
            tweakUi.Apply = ApplyTweaks;
            configureButton = (Button)Window.FindName("ConfigureSecurityButton");
            securityExport = new SettingExportUi(Window, "", "SecurityIcon", ShowProblem);
            taskbarExport = new SettingExportUi(Window, "Taskbar", "TaskbarIcon", ShowProblem);
            saveButton = securityExport.Save; iconButton = securityExport.Icon;
            addToStartMenu = securityExport.StartMenu; iconPopup = securityExport.Popup;
            applyTaskbarButton = (Button)Window.FindName("ApplyTaskbarButton");
            applyTaskbarButton.Click += ApplyTaskbar;
            taskbarExport.Save.Click += SaveExecutable;
            updateButton = (Button)Window.FindName("UpdateButton");
            updateText = (TextBlock)Window.FindName("UpdateProgressText");
            protectionDescription = (TextBlock)Window.FindName("ProtectionDescription");
            updateProgress = (ProgressBar)Window.FindName("UpdateProgressBar");
            configureButton.IsEnabled = false;
            protectionDescription.Visibility = Visibility.Collapsed;
            configureButton.Click += ConfigureSecurity;
            saveButton.Click += SaveExecutable;
            updateButton.Click += UpdateAndRestart;
            updates = new UpdateService(executablePath, releaseNumber);
            updates.StatusChanged += OnUpdateStatus;
            Window.Loaded += StartUpdateCheck;
            Window.Loaded += InspectSecurityAtStartup;
            Window.Loaded += InspectTweaksAtStartup;
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

        async void InspectTweaksAtStartup(object sender, RoutedEventArgs args)
        {
            Window.Loaded -= InspectTweaksAtStartup;
            try { var snapshot = await tweakService.InspectAsync(); if (!disposed && !closing) tweakUi.Snapshot = snapshot; }
            catch (Exception error) { if (!disposed && !closing) tweakUi.SetReport("Не удалось проверить настройки: " + error.Message); }
            if (!disposed && !closing) RefreshButtons();
        }

        async void ApplyTweaks(TweakId[] ids)
        {
            if (configuring || saving || closing || tweakUi.Snapshot == null) return;
            configuring = true;
            iconPopup.IsOpen = false; taskbarExport.Popup.IsOpen = false;
            tweakUi.SetReport("Выполняются настройки…");
            RefreshButtons();
            try
            {
                var result = await tweakService.ApplyAsync(ids);
                tweakUi.Snapshot = result.Snapshot;
                tweakUi.SetReport(result.Format());
            }
            catch (Exception error) { tweakUi.SetReport(error.Message); }
            finally
            {
                configuring = false; RefreshButtons();
                if (closeAfterAction) Window.Close();
            }
        }

        async void InspectSecurityAtStartup(object sender, RoutedEventArgs args)
        {
            Window.Loaded -= InspectSecurityAtStartup;
            if (!securityChecked && !checkingSecurity) await RefreshSecurityStateAsync();
        }

        async Task RefreshSecurityStateAsync()
        {
            if (disposed || closing || checkingSecurity) return;
            checkingSecurity = true;
            RefreshButtons();
            try
            {
                SecuritySnapshot snapshot = await Task.Run(inspectSecurity);
                if (disposed || closing) return;
                if (snapshot == null) throw new InvalidOperationException("Не удалось прочитать состояние защиты Windows.");
                securityNeedsChanges = snapshot.HasChanges || snapshot.HasUnknown;
                protectionDescription.Visibility = snapshot.RequiresElevation ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception)
            {
                if (disposed || closing) return;
                // Unknown is not proof that the switches are off. A user may
                // request an explicit recheck through the action, without a
                // startup dialog or a background polling process.
                securityNeedsChanges = true;
                protectionDescription.Visibility = Visibility.Collapsed;
            }
            finally
            {
                securityChecked = true;
                checkingSecurity = false;
                if (!disposed && !closing) RefreshButtons();
            }
        }

        async void StartUpdateCheck(object sender, RoutedEventArgs args)
        {
            Window.Loaded -= StartUpdateCheck;
            // Shell maintenance is best effort and must never prevent the
            // portable UI or its updater from starting.
            try { shortcutMaintenance = StartMenuShortcuts.StartMaintenance(); }
            catch { }
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
            bool available = !configuring && !saving && !closing;
            tweakUi.Refresh(available);
            configureButton.IsEnabled = configureSecurity != null && available && securityChecked &&
                !checkingSecurity && securityNeedsChanges;
            saveButton.IsEnabled = available;
            iconButton.IsEnabled = available;
            addToStartMenu.IsEnabled = available;
            applyTaskbarButton.IsEnabled = available;
            taskbarExport.Save.IsEnabled = available;
            taskbarExport.Icon.IsEnabled = available;
            taskbarExport.StartMenu.IsEnabled = available;
            updateButton.IsEnabled = updateReady && updates.HasPreparedUpdate && available;
        }

        async void ConfigureSecurity(object sender, RoutedEventArgs args)
        {
            if (configuring || saving || closing || configureSecurity == null || !configureButton.IsEnabled) return;
            configuring = true;
            iconPopup.IsOpen = false;
            RefreshButtons();
            try
            {
                SecurityReport result = await configureSecurity();
                if (result == null) throw new InvalidOperationException("Рабочий процесс не вернул результат настройки.");
                if (!result.Succeeded) ShowProblem(result.FormatProblems());
            }
            catch (Exception error)
            {
                ShowProblem(error.Message);
            }
            finally
            {
                await RefreshSecurityStateAsync();
                configuring = false;
                RefreshButtons();
                if (closeAfterAction) Window.Close();
            }
        }

        void ShowProblem(string message)
        {
            if (disposed) return;
            Window dialog = CreateProblemDialog(message);
            dialog.Owner = Window;
            WindowsTheme.Attach(dialog);
            dialog.ShowDialog();
        }

        Window CreateProblemDialog(string message)
        {
            // Short reports size to their text. Long reports retain a visible
            // acknowledgement button and scroll within the monitor work area.
            Thickness frame = SystemParameters.WindowNonClientFrameThickness;
            double maximumWidth = Math.Min(680, SystemParameters.WorkArea.Width - 32);
            double maximumHeight = Math.Min(640, SystemParameters.WorkArea.Height - 32);
            double textWidth = Math.Max(160, maximumWidth - frame.Left - frame.Right - 48);
            double bodyHeight = Math.Max(60, maximumHeight - frame.Top - frame.Bottom - 48 - 36 - 16);
            var dialog = new Window
            {
                Title = "JustEnoughTools",
                SizeToContent = SizeToContent.WidthAndHeight,
                MinWidth = Math.Min(260, maximumWidth),
                MaxWidth = maximumWidth,
                MaxHeight = maximumHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Icon = Window.Icon,
                Background = Window.Background,
                Foreground = Window.Foreground,
                FontFamily = Window.FontFamily,
                FontSize = Window.FontSize,
                ShowInTaskbar = false,
                UseLayoutRounding = true
            };
            var layout = new Grid { Margin = new Thickness(24) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var details = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = textWidth,
                FontFamily = Window.FontFamily,
                FontSize = Window.FontSize
            };
            details.Measure(new Size(textWidth, Double.PositiveInfinity));
            bool requiresScrolling = details.DesiredSize.Height > bodyHeight;
            if (requiresScrolling) details.MaxWidth = textWidth - 20;
            var scroll = new ScrollViewer
            {
                Content = details,
                MaxWidth = textWidth,
                MaxHeight = bodyHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, requiresScrolling ? 20 : 0, 0)
            };
            SmoothScrollingController.Attach(scroll);
            layout.Children.Add(scroll);
            var dismiss = new Button
            {
                Content = "ОК",
                Width = 96,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
                IsDefault = true,
                IsCancel = true
            };
            dismiss.Click += delegate { dialog.Close(); };
            Grid.SetRow(dismiss, 1);
            layout.Children.Add(dismiss);
            dialog.Content = layout;
            return dialog;
        }

        async void ApplyTaskbar(object sender, RoutedEventArgs args)
        {
            if (configuring || saving || closing) return;
            configuring = true;
            iconPopup.IsOpen = false; taskbarExport.Popup.IsOpen = false;
            RefreshButtons();
            try
            {
                TaskbarResult result = await Task.Run(delegate
                {
                    using (var mutex = UpdateService.CreateOperationMutex(executablePath))
                    {
                        bool acquired;
                        try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                        if (!acquired) throw new InvalidOperationException("Другая операция приложения ещё выполняется.");
                        try { return TaskbarRepair.Apply(); }
                        finally { mutex.ReleaseMutex(); }
                    }
                });
                if (!result.Succeeded) ShowProblem(result.Reason);
            }
            catch (Exception error) { ShowProblem(error.Message); }
            finally
            {
                try { tweakUi.Snapshot = await tweakService.InspectAsync(); }
                catch { }
                configuring = false; RefreshButtons(); if (closeAfterAction) Window.Close();
            }
        }

        void SaveExecutable(object sender, RoutedEventArgs args)
        {
            if (configuring || saving || closing) return;
            bool taskbar = ReferenceEquals(sender, taskbarExport.Save);
            SettingExportUi export = taskbar ? taskbarExport : securityExport;
            FixedAction action = taskbar ? FixedAction.TaskbarMode2 : FixedAction.Security;
            saving = true;
            iconPopup.IsOpen = false; taskbarExport.Popup.IsOpen = false;
            RefreshButtons();
            try
            {
                string destination = ExportDialogs.SaveExecutable(new WindowInteropHelper(Window).Handle, taskbar ? "Исправление панели задач.exe" : "Отключить защиту Windows.exe");
                if (destination == null) return;
                ExecutableExporter.Save(destination, export.Selection, export.StartMenu.IsChecked == true, action);
                string warning = export.Selection.LastWarning;
                export.Refresh();
                if (!String.IsNullOrWhiteSpace(warning)) ShowProblem(warning);
            }
            catch (Exception error) { ShowProblem(error.Message); }
            finally
            {
                saving = false;
                RefreshButtons();
                if (closeAfterAction) Window.Close();
            }
        }

        void UpdateAndRestart(object sender, RoutedEventArgs args)
        {
            if (closing || configuring || saving || !updates.HasPreparedUpdate) return;
            restartAfterUpdate = true;
            Window.Close();
        }

        async void Closing(object sender, CancelEventArgs args)
        {
            if (closeAllowed) return;
            args.Cancel = true;
            if (closing) return;
            if (configuring || saving)
            {
                closeAfterAction = true;
                return;
            }
            closing = true;
            iconPopup.IsOpen = false; taskbarExport.Popup.IsOpen = false;
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
            if (shortcutMaintenance != null) shortcutMaintenance.Dispose();
            updates.StatusChanged -= OnUpdateStatus;
            updates.Dispose();
            lifetime.Dispose();
        }
    }
}

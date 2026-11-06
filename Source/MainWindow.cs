using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
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
using JustEnoughTools.Platform;

namespace JustEnoughTools
{
    internal delegate bool ScheduleUpdate(bool restart, out string error);

    // UI tests supply fixed results without opening file dialogs, running system
    // setters or handing an update to a real child process.
    internal sealed class MainWindowOperations
    {
        internal Func<Task<TaskbarResult>> ApplyTaskbar;
        internal Func<bool, string> SelectExportPath;
        internal Action<string, IconSelection, bool, FixedAction> SaveExport;
        internal Func<bool> HasPreparedUpdate;
        internal Func<bool> IsOperationInProgress;
        internal ScheduleUpdate ScheduleInstall;
    }

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
        readonly Grid protectionTamperHelp;
        readonly TextBlock protectionTamperHint;
        readonly Button refreshProtection;
        readonly ProgressBar updateProgress;
        readonly MainWindowOperations operations;
        readonly TextBlock protectionReport, protectionExportReport, taskbarReport, taskbarExportReport, updateReport;
        readonly ScrollViewer updateReportScroll;
        Task updateTask;
        readonly HashSet<TweakId> busyTweaks = new HashSet<TweakId>();
        bool securityBusy, taskbarBusy;
        bool securitySaving, taskbarSaving;
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
            Func<Task<SecurityReport>> configureSecurity, Func<SecuritySnapshot> inspectSecurity, ITweakService tweakService,
            MainWindowOperations operations = null)
        {
            this.operations = operations ?? new MainWindowOperations();
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
            this.tweakService.StateChanged += OnTweakServiceStateChanged;
            tweakUi = new TweakUi(Window);
            tweakUi.Apply = ApplyTweaks;
            protectionReport = (TextBlock)Window.FindName("ProtectionReport");
            protectionExportReport = (TextBlock)Window.FindName("ProtectionExportReport");
            taskbarReport = (TextBlock)Window.FindName("TaskbarReport");
            taskbarExportReport = (TextBlock)Window.FindName("TaskbarExportReport");
            updateReport = (TextBlock)Window.FindName("UpdateReport");
            updateReportScroll = (ScrollViewer)Window.FindName("UpdateReportScroll");
            SmoothScrollingController.Attach(updateReportScroll);
            configureButton = (Button)Window.FindName("ConfigureSecurityButton");
            securityExport = new SettingExportUi(Window, "", "SecurityIcon", message => SetExportReport(false, message));
            taskbarExport = new SettingExportUi(Window, "Taskbar", "TaskbarIcon", message => SetExportReport(true, message));
            saveButton = securityExport.Save; iconButton = securityExport.Icon;
            addToStartMenu = securityExport.StartMenu; iconPopup = securityExport.Popup;
            applyTaskbarButton = (Button)Window.FindName("ApplyTaskbarButton");
            applyTaskbarButton.Click += ApplyTaskbar;
            taskbarExport.Save.Click += SaveExecutable;
            updateButton = (Button)Window.FindName("UpdateButton");
            updateText = (TextBlock)Window.FindName("UpdateProgressText");
            protectionDescription = (TextBlock)Window.FindName("ProtectionDescription");
            protectionTamperHelp = (Grid)Window.FindName("ProtectionTamperHelp");
            protectionTamperHint = (TextBlock)Window.FindName("ProtectionTamperHint");
            refreshProtection = (Button)Window.FindName("RefreshProtectionButton");
            updateProgress = (ProgressBar)Window.FindName("UpdateProgressBar");
            configureButton.IsEnabled = false;
            protectionDescription.Visibility = Visibility.Collapsed;
            configureButton.Click += ConfigureSecurity;
            refreshProtection.Click += RecheckProtection;
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
            if (closing || closeAfterAction || tweakUi.Snapshot == null || ids == null) return;
            var requested = ids.Where(id => !busyTweaks.Contains(id) &&
                tweakUi.Snapshot.Rows.Any(row => row.Id == id && row.CanApply)).Distinct().ToArray();
            if (requested.Length == 0) return;
            foreach (var id in requested) busyTweaks.Add(id);
            tweakUi.SetReport("Выполняются настройки…");
            RefreshButtons();
            try
            {
                var result = await tweakService.ApplyAsync(requested);
                if (disposed || closing) return;
                // Concurrent actions return independent snapshots. A completion
                // must not replace newer states of rows owned by another action.
                if (result.Snapshot != null)
                    foreach (var row in result.Snapshot.Rows.Where(row => requested.Contains(row.Id)))
                    {
                        tweakUi.Snapshot.Rows.RemoveAll(previous => previous.Id == row.Id);
                        tweakUi.Snapshot.Rows.Add(row);
                    }
                tweakUi.SetReport(result.Format());
            }
            catch (Exception error) { if (!disposed && !closing) tweakUi.SetReport(error.Message); }
            finally
            {
                foreach (var id in requested) busyTweaks.Remove(id);
                if (!disposed && !closing)
                {
                    RefreshButtons();
                    CloseWhenIdle();
                }
            }
        }

        bool HasActiveOperations
        {
            get { return busyTweaks.Count != 0 || securityBusy || taskbarBusy || securitySaving || taskbarSaving; }
        }

        bool HasForegroundOperations
        {
            get { return tweakService.HasForegroundWork || securityBusy || taskbarBusy || securitySaving || taskbarSaving; }
        }

        void OnTweakServiceStateChanged()
        {
            if (disposed || Window.Dispatcher.HasShutdownStarted) return;
            Action changed = delegate
            {
                if (disposed || closing) return;
                RefreshButtons();
                CloseWhenIdle();
            };
            if (Window.Dispatcher.CheckAccess()) changed();
            else Window.Dispatcher.BeginInvoke(changed);
        }

        void CloseWhenIdle()
        {
            if (!disposed && !closing && closeAfterAction && !HasForegroundOperations) Window.Close();
        }

        async void InspectSecurityAtStartup(object sender, RoutedEventArgs args)
        {
            Window.Loaded -= InspectSecurityAtStartup;
            if (!securityChecked && !checkingSecurity) await RefreshSecurityStateAsync();
        }

        async void RecheckProtection(object sender, RoutedEventArgs args)
        {
            if (!refreshProtection.IsEnabled || protectionTamperHelp.Visibility != Visibility.Visible) return;
            await RefreshSecurityStateAsync(manual: true);
        }

        async Task RefreshSecurityStateAsync(bool manual = false)
        {
            if (disposed || closing || checkingSecurity) return;
            checkingSecurity = true;
            if (manual) SetReport(protectionReport, "Проверяется состояние защиты…");
            RefreshButtons();
            try
            {
                SecuritySnapshot snapshot = await Task.Run(inspectSecurity);
                if (disposed || closing) return;
                if (snapshot == null) throw new InvalidOperationException("Не удалось прочитать состояние защиты Windows.");
                securityNeedsChanges = snapshot.HasChanges || snapshot.HasUnknown;
                protectionDescription.Visibility = snapshot.RequiresElevation ? Visibility.Visible : Visibility.Collapsed;
                ActionState realtime = snapshot.RealtimeProtection;
                if (realtime == null || realtime.State == ActionStateKind.Unknown) KeepProtectionRecheck();
                else SetProtectionHelp(realtime.State == ActionStateKind.Blocked ? TamperHelpText : null);
                if (!securityBusy)
                {
                    string report = snapshot.HasUnknown ? snapshot.ToReport().FormatProblems() : null;
                    if (report == null && realtime != null && (realtime.State == ActionStateKind.Unavailable ||
                        manual && realtime.State != ActionStateKind.Blocked))
                        report = realtime.Reason;
                    if (manual && realtime != null && realtime.State == ActionStateKind.NeedsChange)
                        report += " Нажмите «Отключить», чтобы применить настройку.";
                    SetReport(protectionReport, report);
                }
            }
            catch (Exception error)
            {
                if (disposed || closing) return;
                // Unknown is not proof that the switches are off. A user may
                // request an explicit recheck through the action, without a
                // startup dialog or a background polling process.
                securityNeedsChanges = true;
                protectionDescription.Visibility = Visibility.Collapsed;
                KeepProtectionRecheck();
                if (!securityBusy) SetReport(protectionReport, WindowsPowerShell.Explain(error, "Проверка защиты"));
            }
            finally
            {
                securityChecked = true;
                checkingSecurity = false;
                if (!disposed && !closing) RefreshButtons();
            }
        }

        const string TamperHelpText = "«Защита от изменений» блокирует отключение защиты в реальном времени.\n" +
            SecurityEngine.TamperProtectionInstructions + " Затем нажмите «Проверить снова».";

        void SetProtectionHelp(string message)
        {
            if (disposed || closing) return;
            protectionTamperHint.Text = message ?? "";
            protectionTamperHelp.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        }

        void KeepProtectionRecheck()
        {
            if (protectionTamperHelp.Visibility == Visibility.Visible)
                SetProtectionHelp("Не удалось подтвердить состояние защиты в реальном времени. Попробуйте проверить снова.");
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
            catch (Exception error)
            {
                if (!closing && !disposed)
                {
                    SetUpdateStatus("Не удалось проверить обновления", null, false);
                    SetUpdateReport(WindowsPowerShell.Explain(error, "Проверка обновления"));
                }
            }
        }

        void OnUpdateStatus(UpdateStatus status)
        {
            if (disposed || Window.Dispatcher.HasShutdownStarted) return;
            Action apply = delegate
            {
                if (!disposed && !closing)
                {
                    SetUpdateStatus(status.Text, status.Fraction, status.Ready);
                    SetUpdateReport(status.Details);
                }
            };
            if (Window.Dispatcher.CheckAccess()) apply();
            else Window.Dispatcher.BeginInvoke(apply);
        }

        void SetUpdateStatus(string text, double? fraction, bool ready)
        {
            updateReady = ready;
            SetUpdateReport(null);
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
            bool available = !closing && !closeAfterAction;
            tweakUi.Refresh(available, busyTweaks);
            configureButton.IsEnabled = configureSecurity != null && available && !securityBusy && securityChecked &&
                !checkingSecurity && securityNeedsChanges;
            refreshProtection.IsEnabled = available && !securityBusy && !checkingSecurity;
            saveButton.IsEnabled = available && !securitySaving;
            iconButton.IsEnabled = available && !securitySaving;
            addToStartMenu.IsEnabled = available && !securitySaving;
            applyTaskbarButton.IsEnabled = available && !taskbarBusy;
            taskbarExport.Save.IsEnabled = available && !taskbarSaving;
            taskbarExport.Icon.IsEnabled = available && !taskbarSaving;
            taskbarExport.StartMenu.IsEnabled = available && !taskbarSaving;
            updateButton.IsEnabled = updateReady && HasPreparedUpdate && available && !HasActiveOperations;
        }

        async void ConfigureSecurity(object sender, RoutedEventArgs args)
        {
            if (securityBusy || closing || closeAfterAction || configureSecurity == null || !configureButton.IsEnabled) return;
            securityBusy = true;
            iconPopup.IsOpen = false;
            SetReport(protectionReport, "Выполняются настройки защиты…");
            RefreshButtons();
            try
            {
                SecurityReport result = await configureSecurity();
                if (result == null) throw new InvalidOperationException("Рабочий процесс не вернул результат настройки.");
                SetReport(protectionReport, result.FormatProblems());
                if (result.RealtimeBlockedByTamper) SetProtectionHelp(TamperHelpText);
            }
            catch (Exception error)
            {
                SetReport(protectionReport, WindowsPowerShell.Explain(error, "Настройка защиты"));
            }
            finally
            {
                await RefreshSecurityStateAsync();
                securityBusy = false;
                if (!disposed) { RefreshButtons(); CloseWhenIdle(); }
            }
        }

        void SetReport(TextBlock target, string message)
        {
            if (disposed || closing) return;
            target.Text = message ?? "";
            target.Visibility = String.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        void SetExportReport(bool taskbar, string message)
        {
            SetReport(taskbar ? taskbarExportReport : protectionExportReport, message);
        }

        void SetUpdateReport(string message)
        {
            SetReport(updateReport, message);
            if (!disposed && !closing) updateReportScroll.Visibility = updateReport.Visibility;
        }

        bool HasPreparedUpdate
        {
            get { return operations.HasPreparedUpdate == null ? updates.HasPreparedUpdate : operations.HasPreparedUpdate(); }
        }

        void ReportUpdateFailure(string message)
        {
            installScheduleFailed = true;
            closing = false;
            closeAfterAction = false;
            restartAfterUpdate = false;
            updateReady = HasPreparedUpdate;
            updateText.Text = "Не удалось установить обновление";
            SetUpdateReport(String.IsNullOrWhiteSpace(message) ? "Установщик не подтвердил готовность. Обновление сохранено." : message);
            RefreshButtons();
        }

        async void ApplyTaskbar(object sender, RoutedEventArgs args)
        {
            if (taskbarBusy || closing || closeAfterAction) return;
            taskbarBusy = true;
            SetReport(taskbarReport, "Выполняется исправление панели задач…");
            RefreshButtons();
            try
            {
                TaskbarResult result = operations.ApplyTaskbar == null ? await Task.Run(delegate
                {
                    using (UpdateService.AcquireOperationLease(executablePath, "Taskbar"))
                        return TaskbarRepair.Apply();
                }) : await operations.ApplyTaskbar();
                if (result == null) throw new InvalidOperationException("Не получен результат исправления панели задач.");
                SetReport(taskbarReport, String.IsNullOrWhiteSpace(result.Reason)
                    ? (result.Succeeded ? "Исправление панели задач подтверждено." : "Результат исправления панели задач не подтверждён.")
                    : result.Reason);
            }
            catch (Exception error) { SetReport(taskbarReport, WindowsPowerShell.Explain(error, "Исправление панели задач")); }
            finally
            {
                try
                {
                    var snapshot = await tweakService.InspectAsync();
                    var taskbar = snapshot.Rows.FirstOrDefault(row => row.Id == TweakId.Taskbar);
                    if (!disposed && !closing && taskbar != null && !busyTweaks.Contains(TweakId.Taskbar) && tweakUi.Snapshot != null)
                    {
                        tweakUi.Snapshot.Rows.RemoveAll(row => row.Id == TweakId.Taskbar);
                        tweakUi.Snapshot.Rows.Add(taskbar);
                    }
                }
                catch { }
                taskbarBusy = false;
                if (!disposed) { RefreshButtons(); CloseWhenIdle(); }
            }
        }

        void SaveExecutable(object sender, RoutedEventArgs args)
        {
            bool taskbar = ReferenceEquals(sender, taskbarExport.Save);
            if (closing || closeAfterAction || (taskbar ? taskbarSaving : securitySaving)) return;
            SettingExportUi export = taskbar ? taskbarExport : securityExport;
            FixedAction action = taskbar ? FixedAction.TaskbarMode2 : FixedAction.Security;
            if (taskbar) taskbarSaving = true; else securitySaving = true;
            export.Popup.IsOpen = false;
            RefreshButtons();
            try
            {
                string destination = operations.SelectExportPath == null
                    ? ExportDialogs.SaveExecutable(new WindowInteropHelper(Window).Handle, taskbar ? "Исправление панели задач.exe" : "Отключить защиту Windows.exe")
                    : operations.SelectExportPath(taskbar);
                if (destination == null) return;
                if (operations.SaveExport == null) ExecutableExporter.Save(destination, export.Selection, export.StartMenu.IsChecked == true, action);
                else operations.SaveExport(destination, export.Selection, export.StartMenu.IsChecked == true, action);
                string warning = export.Selection.LastWarning;
                export.Refresh();
                SetExportReport(taskbar, "Автономный EXE сохранён." + (String.IsNullOrWhiteSpace(warning) ? "" : "\n" + warning));
            }
            catch (Exception error) { SetExportReport(taskbar, WindowsPowerShell.Explain(error, "Сохранение автономного EXE")); }
            finally
            {
                if (taskbar) taskbarSaving = false; else securitySaving = false;
                RefreshButtons();
                CloseWhenIdle();
            }
        }

        void UpdateAndRestart(object sender, RoutedEventArgs args)
        {
            if (closing || closeAfterAction || HasActiveOperations || !HasPreparedUpdate) return;
            restartAfterUpdate = true;
            Window.Close();
        }

        async void Closing(object sender, CancelEventArgs args)
        {
            if (closeAllowed) return;
            args.Cancel = true;
            if (closing) return;
            if (HasForegroundOperations)
            {
                closeAfterAction = true;
                RefreshButtons();
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
            // A detached worker owns the same EXE and its operation leases.
            // Leave the prepared package untouched for a later safe install.
            bool detachedWork = busyTweaks.Count != 0;
            try { detachedWork |= operations.IsOperationInProgress == null ? UpdateService.IsOperationInProgress(executablePath) : operations.IsOperationInProgress(); }
            catch { detachedWork = true; }
            if (!detachedWork && HasPreparedUpdate && (!installScheduleFailed || restartAfterUpdate))
            {
                string error = null;
                bool scheduled;
                try { scheduled = operations.ScheduleInstall == null ? updates.TryScheduleInstall(restartAfterUpdate, out error) : operations.ScheduleInstall(restartAfterUpdate, out error); }
                catch (Exception failure) { scheduled = false; error = WindowsPowerShell.Explain(failure, "Подготовка установки обновления"); }
                if (!scheduled)
                {
                    ReportUpdateFailure(error);
                    return;
                }
            }
            closeAllowed = true;
            _ = Window.Dispatcher.BeginInvoke(new Action(Window.Close));
        }

        void Closed(object sender, EventArgs args)
        {
            disposed = true;
            tweakService.StateChanged -= OnTweakServiceStateChanged;
            if (shortcutMaintenance != null) shortcutMaintenance.Dispose();
            updates.StatusChanged -= OnUpdateStatus;
            updates.Dispose();
            lifetime.Dispose();
        }
    }
}

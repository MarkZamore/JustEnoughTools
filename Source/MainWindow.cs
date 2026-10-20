using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text;
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

namespace JustEnoughTools
{
    public sealed class MainWindowController
    {
        readonly Func<Task<SecurityReport>> configureSecurity;
        readonly Func<SecuritySnapshot> inspectSecurity;
        readonly UpdateService updates;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly Button configureButton;
        readonly Button saveButton;
        readonly Button iconButton;
        readonly Button defaultIconButton;
        readonly Button customIconButton;
        readonly CheckBox addToStartMenu;
        readonly Image currentIcon;
        readonly Popup iconPopup;
        readonly IconSelection iconSelection = new IconSelection();
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
        {
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
            ControlMotion.AttachCategory((Expander)Window.FindName("WalkieTalkieSection"));
            ControlMotion.AttachCategory((Expander)Window.FindName("ClickerSection"));
            configureButton = (Button)Window.FindName("ConfigureSecurityButton");
            saveButton = (Button)Window.FindName("SaveButton");
            iconButton = (Button)Window.FindName("IconButton");
            defaultIconButton = (Button)Window.FindName("DefaultIconButton");
            customIconButton = (Button)Window.FindName("CustomIconButton");
            addToStartMenu = (CheckBox)Window.FindName("AddToStartMenu");
            currentIcon = (Image)Window.FindName("CurrentIcon");
            iconPopup = (Popup)Window.FindName("IconPopup");
            updateButton = (Button)Window.FindName("UpdateButton");
            updateText = (TextBlock)Window.FindName("UpdateProgressText");
            protectionDescription = (TextBlock)Window.FindName("ProtectionDescription");
            updateProgress = (ProgressBar)Window.FindName("UpdateProgressBar");
            configureButton.IsEnabled = false;
            protectionDescription.Visibility = Visibility.Collapsed;
            configureButton.Click += ConfigureSecurity;
            saveButton.Click += SaveExecutable;
            iconButton.Click += ToggleIconPicker;
            defaultIconButton.Click += SelectDefaultIcon;
            customIconButton.Click += SelectCustomIcon;
            Window.PreviewKeyDown += IconPickerKeyDown;
            ((StackPanel)Window.FindName("IconChoices")).PreviewKeyDown += IconPickerKeyDown;
            ((Image)Window.FindName("DefaultIconPreview")).Source = Window.Icon;
            RefreshIconPreview();
            updateButton.Click += UpdateAndRestart;
            updates = new UpdateService(executablePath, releaseNumber);
            updates.StatusChanged += OnUpdateStatus;
            Window.Loaded += StartUpdateCheck;
            Window.Loaded += InspectSecurityAtStartup;
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
                configureButton.ToolTip = CreateSecurityToolTip(snapshot);
            }
            catch (Exception error)
            {
                if (disposed || closing) return;
                // Unknown is not proof that the switches are off. A user may
                // request an explicit recheck through the action, without a
                // startup dialog or a background polling process.
                securityNeedsChanges = true;
                protectionDescription.Visibility = Visibility.Collapsed;
                configureButton.ToolTip = ReadOnlyToolTip("Состояние не подтверждено: " + error.Message);
            }
            finally
            {
                securityChecked = true;
                checkingSecurity = false;
                if (!disposed && !closing) RefreshButtons();
            }
        }

        static ToolTip CreateSecurityToolTip(SecuritySnapshot snapshot)
        {
            var text = new StringBuilder(snapshot.HasChanges
                ? "Доступные параметры требуют изменения."
                : snapshot.HasUnknown
                    ? "Состояние части доступных параметров не подтверждено."
                    : "Доступных изменений сейчас нет. Повторная проверка — при следующем запуске приложения.");
            bool headingAdded = false;
            if (snapshot.Actions != null)
                foreach (ActionState action in snapshot.Actions)
                {
                    if (action == null || action.State != ActionStateKind.Unavailable) continue;
                    if (!headingAdded)
                    {
                        text.AppendLine().AppendLine().AppendLine("Недоступные действия:");
                        headingAdded = true;
                    }
                    text.Append("• ").Append(action.Title ?? action.Id ?? "Действие").Append(": ")
                        .AppendLine(String.IsNullOrWhiteSpace(action.Reason)
                            ? "Программный способ для этой системы не подтверждён."
                            : action.Reason);
                }
            return ReadOnlyToolTip(text.ToString().TrimEnd());
        }

        static ToolTip ReadOnlyToolTip(string text)
        {
            return new ToolTip
            {
                MaxWidth = 560,
                Content = new ScrollViewer
                {
                    MaxHeight = Math.Max(120, Math.Min(480, SystemParameters.WorkArea.Height - 80)),
                    MaxWidth = 520,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Padding = new Thickness(0, 0, 20, 0),
                    Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }
                }
            };
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
            configureButton.IsEnabled = configureSecurity != null && available && securityChecked &&
                !checkingSecurity && securityNeedsChanges;
            saveButton.IsEnabled = available;
            iconButton.IsEnabled = available;
            addToStartMenu.IsEnabled = available;
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

        void RefreshIconPreview()
        {
            using (var stream = new MemoryStream(iconSelection.Read(), false))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapFrame selected = decoder.Frames[0];
                foreach (BitmapFrame frame in decoder.Frames)
                    if (frame.PixelWidth >= 24 && (selected.PixelWidth < 24 || frame.PixelWidth < selected.PixelWidth)) selected = frame;
                selected.Freeze();
                currentIcon.Source = selected;
            }
        }

        void ToggleIconPicker(object sender, RoutedEventArgs args)
        {
            if (configuring || saving || closing) return;
            iconPopup.IsOpen = !iconPopup.IsOpen;
            if (iconPopup.IsOpen) defaultIconButton.Focus();
        }

        void SelectDefaultIcon(object sender, RoutedEventArgs args)
        {
            iconSelection.Reset();
            RefreshIconPreview();
            iconPopup.IsOpen = false;
            iconButton.Focus();
        }

        void SelectCustomIcon(object sender, RoutedEventArgs args)
        {
            iconPopup.IsOpen = false;
            try
            {
                string path = ExportDialogs.SelectIcon(new WindowInteropHelper(Window).Handle);
                if (path != null)
                {
                    iconSelection.SelectCustom(path);
                    RefreshIconPreview();
                }
            }
            catch (Exception error) { ShowProblem(error.Message); }
            iconButton.Focus();
        }

        void IconPickerKeyDown(object sender, KeyEventArgs args)
        {
            if (args.Key == Key.Escape && iconPopup.IsOpen)
            {
                iconPopup.IsOpen = false;
                iconButton.Focus();
                args.Handled = true;
            }
        }

        void SaveExecutable(object sender, RoutedEventArgs args)
        {
            if (configuring || saving || closing) return;
            saving = true;
            iconPopup.IsOpen = false;
            RefreshButtons();
            try
            {
                string destination = ExportDialogs.SaveExecutable(new WindowInteropHelper(Window).Handle);
                if (destination == null) return;
                ExecutableExporter.Save(destination, iconSelection, addToStartMenu.IsChecked == true);
                string warning = iconSelection.LastWarning;
                RefreshIconPreview();
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
            iconPopup.IsOpen = false;
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

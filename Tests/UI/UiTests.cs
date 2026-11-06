using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.Generic;
using System.Linq;
using JustEnoughTools.Tweaks;
using JustEnoughTools.Security;
using JustEnoughTools.Actions;
using JustEnoughTools.Updates;
using System.Windows.Threading;

namespace JustEnoughTools.Tests
{
    public static class UiTests
    {
        // These are real WPF controls, measured without showing their Window.
        // Rendering at several pixel densities checks the offscreen output;
        // it does not simulate monitor changes or WM_DPICHANGED messages.
        public static int RunAll()
        {
            Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "UI tests require an STA thread.");
            if (Application.Current == null)
                new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            bool actionInvoked = false;
            int inspections = 0;
            Assembly applicationAssembly = typeof(MainWindowController).Assembly;
            var tweaks = new FakeTweaks();
            var controller = new MainWindowController(1, applicationAssembly.Location,
                delegate
                {
                    actionInvoked = true;
                    throw new InvalidOperationException("A UI test must not change security settings.");
                },
                delegate
                {
                    inspections++;
                    throw new InvalidOperationException("Offscreen UI measurement must not inspect Windows.");
                }, tweaks);
            Window window = controller.Window;
            Grid root = Find<Grid>(window, "RootGrid");
            int passed = 0;

            Require(window.Title == "JustEnoughTools", "Unexpected window title.");
            Require(window.Width == 820 && window.Height == 560 && window.MinWidth == 820 && window.MinHeight == 560,
                "The initial and minimum window size must be 820 x 560 DIP.");
            AssertEmptyCategory(window, "ClickerSection");
            Require(!Find<Expander>(window, "WindowsTweakerSection").IsExpanded &&
                Find<Grid>(window, "TweaksGrid").Children.OfType<Grid>().Count() == 13 &&
                !Find<Button>(window, "ApplyAllTweaks").IsEnabled,
                "The tweaker must start collapsed with 13 unavailable-until-checked rows.");
            passed++;

            Button configure = Find<Button>(window, "ConfigureSecurityButton");
            Button save = Find<Button>(window, "SaveButton");
            Button icon = Find<Button>(window, "IconButton");
            Button update = Find<Button>(window, "UpdateButton");
            Find<Border>(window, "ProtectionCard");
            Require(Find<TextBlock>(window, "ProtectionTitle").Text == "Отключить защиту Windows (временно)" &&
                Find<TextBlock>(window, "ProtectionDescription").Text == "Требуются права администратора",
                "The card must identify the setting and required administrator rights.");
            Require(Equals(configure.Content, "Отключить") && !configure.IsEnabled,
                "The configuration button must await the one-time startup inspection.");
            Require(Equals(save.Content, "Сохранить") && save.IsEnabled && icon.IsEnabled && Double.IsNaN(save.Width),
                "Export and icon selection must be available; Save must size to its content.");
            Require(Find<CheckBox>(window, "AddToStartMenu").IsChecked == true,
                "The Start menu option must be enabled by default.");
            Require(!Find<Popup>(window, "IconPopup").IsOpen &&
                Find<UniformGrid>(window, "IconChoices").Children.Count == 3 &&
                Find<UniformGrid>(window, "TaskbarIconChoices").Children.Count == 3,
                "Both icon pickers must offer two built-in tiles and a custom ICO.");
            Require(window.FindName("TaskbarModeSwitch") == null &&
                Equals(Find<Button>(window, "ApplyTaskbarButton").Content, "Применить") &&
                Find<CheckBox>(window, "TaskbarAddToStartMenu").IsChecked == true &&
                Equals(Find<TextBlock>(window, "TaskbarTitle").ToolTip,
                    "Вернуть отображение: \"Панель задач, в которой открыто окно\""),
                "Taskbar repair must expose a single fixed action and describe its final display setting.");
            var securityPreview = Find<Image>(window, "CurrentIcon");
            var taskbarPreview = Find<Image>(window, "TaskbarCurrentIcon");
            ImageSource taskbarBefore = taskbarPreview.Source;
            ((Button)Find<UniformGrid>(window, "IconChoices").Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(ReferenceEquals(taskbarBefore, taskbarPreview.Source), "Choosing a protection export icon must not change the taskbar selection.");
            ((Button)Find<UniformGrid>(window, "IconChoices").Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ImageSource securityBefore = securityPreview.Source;
            ((Button)Find<UniformGrid>(window, "TaskbarIconChoices").Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(ReferenceEquals(securityBefore, securityPreview.Source), "Each setting must retain its own icon selection.");
            ((Button)Find<UniformGrid>(window, "TaskbarIconChoices").Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(Equals(update.Content, "Обновить") && !update.IsEnabled, "The update button must start disabled.");
            Require(update.Width == 96, "The update button must be 96 DIP wide.");
            Require(Find<ProgressBar>(window, "UpdateProgressBar").MinHeight == 36, "The update bar must be at least 36 DIP high.");
            Require(Find<TextBlock>(window, "UpdateProgressText").Text == "Проверка обновлений", "The initial update text must not claim the version was checked.");
            passed++;

            AssertEmbeddedIcon(window, applicationAssembly);
            passed++;

            foreach (Size outerSize in new[] { new Size(820, 560), new Size(1100, 760) })
            {
                MeasureClient(window, root, outerSize);
                AssertLayout(window, root);
                foreach (double scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
                    AssertScaledRendering(root, scale);
                passed++;
            }

            Find<TextBlock>(window, "UpdateProgressText").Text =
                "Обновление 65534: 100 МБ / 120 МБ (2,4 МБ/с, осталось 8 с)";
            Find<TextBlock>(window, "ProtectionDescription").Visibility = Visibility.Visible;
            MeasureClient(window, root, new Size(820, 560));
            AssertLayout(window, root);
            passed++;

            typeof(MainWindowController).GetMethod("InspectTweaksAtStartup", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(controller, new object[] { window, new RoutedEventArgs() });
            Require(tweaks.Inspections == 1 && Find<Button>(window, "ApplyAllTweaks").IsEnabled,
                "Startup inspection must enable only confirmed pending tweaks.");
            var category = Find<Expander>(window, "WindowsTweakerSection");
            foreach (Size outerSize in new[] { new Size(820, 560), new Size(1100, 760) })
            {
                category.IsExpanded = true;
                MeasureClient(window, root, outerSize);
                Find<ScrollViewer>(window, "SettingsScroll").ScrollToTop();
                root.UpdateLayout();
                Grid cells = Find<Grid>(window, "TweaksGrid");
                for (int index = 1; index <= 13; index++)
                {
                    Grid cell = Find<Grid>(window, "TweakCell" + index);
                    Require(Grid.GetRow(cell) == (index - 1) / 2 && Grid.GetColumn(cell) == (index - 1) % 2 * 2,
                        "Tweaks must follow the requested row-major zigzag order.");
                    AssertInside(Find<Button>(window, "TweakApply" + index), cell, "Each Apply button must fit its own column.");
                    Require(cell.Children.Count == 2 && cell.Children.OfType<TextBlock>().Count() == 1 && cell.RowDefinitions.Count == 1,
                        "Tweaks must contain only a title and button, with no status row for any state.");
                    Require(cell.ToolTip == null &&
                        ToolTipService.GetShowOnDisabled(Find<Button>(window, "TweakApply" + index)),
                        "Disabled-button explanations must stay on buttons instead of covering the whole row.");
                    Require(Find<Button>(window, "TweakApply" + index).ToolTip == null,
                        "Enabled Apply buttons must not show redundant tooltips.");
                    if (index == 6) Require(((TextBlock)cell.Children[0]).ToolTip.ToString().Contains("Для шутеров"),
                        "General hints must remain on setting titles.");
                    if (index > 2)
                    {
                        Rect previous = Bounds(Find<Grid>(window, "TweakCell" + (index - 2)), cells);
                        Require(Bounds(cell, cells).Top - previous.Bottom <= 6.75,
                            "Tweaker rows must retain only a compact gap between controls.");
                    }
                    if (index % 2 == 0)
                    {
                        Grid left = Find<Grid>(window, "TweakCell" + (index - 1));
                        Rect leftTitle = Bounds((TextBlock)left.Children[0], cells), rightTitle = Bounds((TextBlock)cell.Children[0], cells);
                        Require(Math.Abs(leftTitle.Top + leftTitle.Height / 2 - rightTitle.Top - rightTitle.Height / 2) < 0.75,
                            "Titles in both columns must share the same vertical centre, including wrapped titles.");
                    }
                }
                Require(Math.Abs(cells.ColumnDefinitions[0].ActualWidth - cells.ColumnDefinitions[2].ActualWidth) < 0.75 &&
                    Find<Border>(window, "TweaksDivider").ActualHeight > 0,
                    "Tweaker columns must be equal and separated by a visible divider.");
                double applyRight = Bounds(Find<Button>(window, "ApplyAllTweaks"), root).Right;
                double saveRight = Bounds(Find<Button>(window, "SaveButton"), root).Right;
                Require(Math.Abs(applyRight - saveRight) < 0.75, "Apply all must align exactly with the Save right edge.");
                foreach (double scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 }) AssertScaledRendering(root, scale);
                string previewDirectory = Path.GetFullPath(Path.Combine("Tests", "Artifacts", "Tweaker"));
                Directory.CreateDirectory(previewDirectory);
                var canvas = (FrameworkElement)root.Parent;
                var preview = new RenderTargetBitmap((int)Math.Ceiling(canvas.ActualWidth), (int)Math.Ceiling(canvas.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                var backdrop = new DrawingVisual();
                using (var drawing = backdrop.RenderOpen())
                {
                    var bounds = new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight);
                    drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 32, 32)), null, bounds);
                    drawing.DrawRectangle(window.Background, null, bounds);
                }
                preview.Render(backdrop);
                preview.Render(canvas);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(preview));
                using (var output = File.Create(Path.Combine(previewDirectory, "window-" + outerSize.Width + ".png"))) encoder.Save(output);
            }
            category.IsExpanded = false;
            Find<Button>(window, "ApplyAllTweaks").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!category.IsExpanded && tweaks.Applied.Count == 13, "Apply all must not toggle the category and must include every row.");
            Find<Button>(window, "TweakApply6").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(tweaks.Applied.SequenceEqual(new[] { TweakId.Mouse }) && tweaks.Inspections == 1,
                "Individual actions must stay independent; expanding and applying must not start polling.");
            Require(Find<TextBlock>(window, "TweaksReport").Visibility == Visibility.Visible,
                "Tweak results must be displayed inline.");
            passed++;

            var tweakUi = (TweakUi)typeof(MainWindowController).GetField("tweakUi", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
            var mouseRow = tweakUi.Snapshot.Rows.Single(row => row.Id == TweakId.Mouse);
            mouseRow.Parts[0] = new TweakPart("test", TweakState.Applied);
            tweakUi.Refresh(true);
            var mouseCell = Find<Grid>(window, "TweakCell6");
            Require(!Find<Button>(window, "TweakApply6").IsEnabled &&
                Find<Button>(window, "TweakApply6").ToolTip.ToString() == "Уже настроено." &&
                ((TextBlock)mouseCell.Children[0]).ToolTip.ToString() == "Для шутеров." && mouseCell.ToolTip == null,
                "A disabled button must explain its state while the title retains only the general hint.");
            mouseRow.Parts[0] = new TweakPart("test", TweakState.Pending);
            tweakUi.Refresh(true);
            Require(Find<Button>(window, "TweakApply6").IsEnabled && Find<Button>(window, "TweakApply6").ToolTip == null,
                "The disabled reason must disappear when the action becomes available.");
            passed++;

            // Hold fake tasks open to exercise real event-handler lifetimes.
            // No native action, process, or Windows preference is invoked.
            tweaks.Hold = true;
            Find<Button>(window, "TweakApply1").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!Find<Button>(window, "TweakApply1").IsEnabled &&
                Find<Button>(window, "TweakApply1").ToolTip.ToString() == "Операция уже выполняется." &&
                Find<Button>(window, "TweakApply6").IsEnabled && Find<Button>(window, "ApplyAllTweaks").IsEnabled &&
                Find<Button>(window, "ApplyTaskbarButton").IsEnabled && save.IsEnabled && icon.IsEnabled &&
                Find<Button>(window, "TaskbarSaveButton").IsEnabled && Find<Expander>(window, "ClickerSection").IsEnabled,
                "A long tweak must lock only its own button, leaving categories and independent actions usable.");
            Find<Button>(window, "TweakApply6").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(tweaks.Pending.Count == 2 && !Find<Button>(window, "TweakApply6").IsEnabled &&
                Find<Button>(window, "TweakApply3").IsEnabled,
                "An independent tweak must start while another is running.");
            Find<Button>(window, "ApplyAllTweaks").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(tweaks.Pending.Count == 3 && tweaks.Applied.Count == 11 &&
                !tweaks.Applied.Contains(TweakId.Indexing) && !tweaks.Applied.Contains(TweakId.Mouse) &&
                !Find<Button>(window, "ApplyAllTweaks").IsEnabled && Find<Button>(window, "ApplyTaskbarButton").IsEnabled,
                "Apply all must start only the remaining pending rows and never duplicate a running action.");
            tweaks.Complete(1);
            Require(!Find<Button>(window, "TweakApply1").IsEnabled && !Find<Button>(window, "TweakApply6").IsEnabled,
                "Completing one action must not unlock another running action or its own already-applied row.");
            tweaks.Complete(0);
            Require(Find<Button>(window, "TweakApply6").ToolTip.ToString() == "Уже настроено." &&
                !Find<Button>(window, "TweakApply3").IsEnabled,
                "A stale completion snapshot must not overwrite another action's completed or running rows.");
            tweaks.Complete(2);
            Require(!Find<Button>(window, "ApplyAllTweaks").IsEnabled && save.IsEnabled &&
                Find<Button>(window, "ApplyTaskbarButton").IsEnabled,
                "Completed actions must preserve their states and leave unrelated settings available.");
            passed += 6;

            tweakUi.Snapshot = tweaks.FreshSnapshot();
            SetControllerFlag(controller, "securityChecked", true);
            SetControllerFlag(controller, "securityNeedsChanges", true);
            foreach (string busyFlag in new[] { "securityBusy", "taskbarBusy", "securitySaving", "taskbarSaving" })
            {
                SetControllerFlag(controller, busyFlag, true);
                typeof(MainWindowController).GetMethod("RefreshButtons", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(controller, null);
                Require(configure.IsEnabled == (busyFlag != "securityBusy") &&
                    Find<Button>(window, "ApplyTaskbarButton").IsEnabled == (busyFlag != "taskbarBusy") &&
                    save.IsEnabled == (busyFlag != "securitySaving") &&
                    Find<Button>(window, "TaskbarSaveButton").IsEnabled == (busyFlag != "taskbarSaving") &&
                    Find<Button>(window, "TweakApply1").IsEnabled && Find<Button>(window, "ApplyAllTweaks").IsEnabled &&
                    Find<Expander>(window, "ClickerSection").IsEnabled,
                    "Each action/export must disable only its own controls, never independent categories or actions: " + busyFlag);
                SetControllerFlag(controller, busyFlag, false);
                passed++;
            }
            typeof(MainWindowController).GetMethod("RefreshButtons", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(controller, null);

            Require(!window.IsVisible && !window.IsLoaded && !actionInvoked && inspections == 0,
                "UI tests must not show the window, start maintenance/network checks, or invoke security actions.");
            Find<Button>(window, "TweakApply12").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var foreground = typeof(MainWindowController).GetProperty("HasForegroundOperations", BindingFlags.Instance | BindingFlags.NonPublic);
            Require((bool)foreground.GetValue(controller, null),
                "The window must await preferences and worker authentication before allowing a background close.");
            tweaks.Detach();
            Require(!(bool)foreground.GetValue(controller, null) && !Find<Button>(window, "TweakApply12").IsEnabled &&
                Find<Button>(window, "TweakApply1").IsEnabled,
                "An acknowledged detached worker must allow closing while only its own row stays blocked.");
            string reportBeforeClosing = Find<TextBlock>(window, "TweaksReport").Text;
            typeof(MainWindowController).GetMethod("Closed", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(controller, new object[] { window, EventArgs.Empty });
            tweaks.Complete(3);
            Require(Find<TextBlock>(window, "TweaksReport").Text == reportBeforeClosing,
                "Late completion after the controller closes must not update disposed UI controls.");
            passed += 3;
            // Do not call Close: its production handler may schedule a prepared
            // update. This unshown window has no active task or dispatcher loop.
            return passed + TestInlineReports(applicationAssembly);
        }

        static void SetControllerFlag(MainWindowController controller, string name, bool value)
        {
            typeof(MainWindowController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(controller, value);
        }

        static int TestInlineReports(Assembly applicationAssembly)
        {
            SynchronizationContext previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var tweaks = new FakeTweaks();
            var securityCompletion = new TaskCompletionSource<SecurityReport>();
            Func<Task<SecurityReport>> securityAction = () => securityCompletion.Task;
            var snapshot = new SecuritySnapshot();
            foreach (var action in SecurityEngine.Catalogue)
                snapshot.Actions.Add(new ActionState { Id = action.Id, Title = action.Title,
                    State = action.Id == "defender.realtime" ? ActionStateKind.NeedsChange : ActionStateKind.AlreadyDesired,
                    Reason = "Проверено тестовым адаптером." });
            bool prepared = true;
            int installAttempts = 0, saves = 0, inspections = 0, securityRuns = 0;
            bool saveFails = true;
            bool inspectFails = false;
            var operations = new MainWindowOperations
            {
                ApplyTaskbar = () => Task.FromResult(new TaskbarResult { Succeeded = true, Reason = "Режим панели задач подтверждён." }),
                SelectExportPath = taskbar => taskbar ? "Taskbar.exe" : "Security.exe",
                SaveExport = (path, selection, startMenu, action) =>
                {
                    saves++;
                    if (saveFails) throw new UnauthorizedAccessException("Тестовый отказ сохранения.");
                },
                HasPreparedUpdate = () => prepared,
                IsOperationInProgress = () => false,
                ScheduleInstall = delegate(bool restart, out string error)
                {
                    installAttempts++;
                    error = "Запуск установщика: отказано в доступе, Win32 5.";
                    return false;
                }
            };
            var controller = new MainWindowController(1, applicationAssembly.Location,
                () => { securityRuns++; return securityAction(); }, () =>
                {
                    inspections++;
                    if (inspectFails) throw new UnauthorizedAccessException("Тестовый отказ проверки.");
                    return snapshot;
                }, tweaks, operations);
            Window window = controller.Window;
            Grid root = Find<Grid>(window, "RootGrid");
            int passed = 0;
            try
            {
                foreach (string name in new[] { "ProtectionReport", "ProtectionExportReport", "TaskbarReport", "TaskbarExportReport", "UpdateReport" })
                    Require(Find<TextBlock>(window, name).Visibility == Visibility.Collapsed, "Reports must start collapsed: " + name);
                Require(typeof(MainWindowController).GetMethod("CreateProblemDialog", BindingFlags.Instance | BindingFlags.NonPublic) == null,
                    "The main application must not construct modal information dialogs.");
                passed++;

                WaitTask((Task)Invoke(controller, "RefreshSecurityStateAsync", false));
                Button configure = Find<Button>(window, "ConfigureSecurityButton");
                Require(configure.IsEnabled && inspections == 1, "A fixed startup snapshot must enable a pending action without polling.");
                configure.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(!configure.IsEnabled && Find<TextBlock>(window, "ProtectionReport").Text.Contains("Выполняются"),
                    "A running security action must report progress inline.");
                Find<Button>(window, "SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(saves == 1 && Find<TextBlock>(window, "ProtectionExportReport").Text.Contains("Тестовый отказ") &&
                    Find<TextBlock>(window, "ProtectionReport").Text.Contains("Выполняются"),
                    "A simultaneous export failure must retain the independent action report.");
                var partial = new SecurityReport();
                partial.Results.Add(new ActionResult { Id = "defender.realtime", Title = "Защита в реальном времени",
                    Outcome = ActionOutcome.Blocked, Reason = "Windows не подтвердила изменение." });
                partial.Results.Add(new ActionResult { Id = "firewall.private", Outcome = ActionOutcome.Applied });
                securityCompletion.SetResult(partial);
                PumpUntil(() => configure.IsEnabled);
                Require(Find<TextBlock>(window, "ProtectionReport").Text.Contains("1 из 2") &&
                    Find<TextBlock>(window, "ProtectionReport").Text.Contains("Windows не подтвердила") && inspections == 2,
                    "A partial report must survive the post-operation inspection and retain its counts and reasons.");
                Require(Find<TextBlock>(window, "ProtectionExportReport").Text.Contains("Тестовый отказ"),
                    "Action completion must not erase export errors.");
                passed += 4;

                securityAction = () => { throw new InvalidOperationException("Запрос прав администратора отменён. Изменения не выполнялись."); };
                configure.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => configure.IsEnabled);
                Require(Find<TextBlock>(window, "ProtectionReport").Text.Contains("отменён"), "Cancelled UAC must remain inside the protection card.");
                var success = new SecurityReport();
                success.Results.Add(new ActionResult { Id = SecurityEngine.RealtimeActionId, Title = "Защита в реальном времени",
                    Outcome = ActionOutcome.Applied, Reason = "Защита в реальном времени сейчас выключена." });
                securityAction = () => Task.FromResult(success);
                configure.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => configure.IsEnabled);
                Require(Find<TextBlock>(window, "ProtectionReport").Text.Contains("1 из 1") &&
                    Find<TextBlock>(window, "ProtectionReport").Text.Contains("сейчас выключена"),
                    "Successful security execution must display the confirmed RTP state inline.");
                passed += 2;

                snapshot.Actions[0].State = ActionStateKind.Unknown;
                snapshot.Actions[0].Reason = "Провайдер не подтвердил состояние.";
                WaitTask((Task)Invoke(controller, "RefreshSecurityStateAsync", false));
                Require(Find<TextBlock>(window, "ProtectionReport").Text.Contains("Провайдер не подтвердил") && configure.IsEnabled &&
                    Find<TextBlock>(window, "ProtectionDescription").Visibility == Visibility.Collapsed,
                    "An unknown initial state must expose its reason and allow rechecking without assuming an elevation requirement.");
                inspectFails = true;
                WaitTask((Task)Invoke(controller, "RefreshSecurityStateAsync", false));
                Require(Find<TextBlock>(window, "ProtectionReport").Text.Contains("Тестовый отказ проверки") && configure.IsEnabled,
                    "A failed initial inspection must report its error inline without treating protection as disabled.");
                inspectFails = false;
                passed += 2;

                Grid tamperHelp = Find<Grid>(window, "ProtectionTamperHelp");
                TextBlock tamperHint = Find<TextBlock>(window, "ProtectionTamperHint");
                Button recheck = Find<Button>(window, "RefreshProtectionButton");
                Require(tamperHelp.Visibility == Visibility.Collapsed, "Unknown RTP alone must not display Tamper Protection instructions.");
                ActionState realtime = snapshot.Actions[0];
                realtime.State = ActionStateKind.Blocked;
                realtime.Reason = "Защита от изменений блокирует изменение RTP.";
                WaitTask((Task)Invoke(controller, "RefreshSecurityStateAsync", false));
                Require(tamperHelp.Visibility == Visibility.Visible && recheck.IsEnabled && !configure.IsEnabled &&
                    (string)recheck.Content == "Проверить снова" && tamperHint.Text.Contains("Управление настройками") &&
                    Find<TextBlock>(window, "ProtectionDescription").Visibility == Visibility.Collapsed,
                    "A known Tamper blocker must expose manual instructions and read-only recheck, without enabling apply or an elevation hint.");
                int readsBeforeRecheck = inspections;
                int runsBeforeRecheck = securityRuns;
                recheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(!recheck.IsEnabled && !configure.IsEnabled && Find<TextBlock>(window, "ProtectionReport").Text.Contains("Проверяется"),
                    "During recheck the protection card must prevent overlapping probes or writes.");
                recheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => recheck.IsEnabled);
                Require(inspections == readsBeforeRecheck + 1 && securityRuns == runsBeforeRecheck && !configure.IsEnabled &&
                    tamperHelp.Visibility == Visibility.Visible, "Recheck must only read once and keep the known blocker if it persists.");
                inspectFails = true;
                recheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => recheck.IsEnabled);
                Require(tamperHelp.Visibility == Visibility.Visible && !tamperHint.Text.Contains("блокирует") &&
                    Find<TextBlock>(window, "ProtectionReport").Text.Contains("Тестовый отказ проверки"),
                    "A failed recheck must permit retry without presenting a stale blocker as current.");
                inspectFails = false;
                realtime.State = ActionStateKind.NeedsChange;
                realtime.RequiresElevation = true;
                realtime.Reason = "Защита в реальном времени включена.";
                recheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => configure.IsEnabled);
                Require(tamperHelp.Visibility == Visibility.Collapsed && securityRuns == runsBeforeRecheck &&
                    Find<TextBlock>(window, "ProtectionDescription").Visibility == Visibility.Visible &&
                    Find<TextBlock>(window, "ProtectionReport").Text.Contains("Нажмите «Отключить»") &&
                    Find<TextBlock>(window, "ProtectionExportReport").Text.Contains("Тестовый отказ"),
                    "After unblocking, recheck must expose a separate apply action and leave the export report intact.");
                var workerBlocked = new SecurityReport();
                workerBlocked.Results.Add(new ActionResult { Id = realtime.Id, Title = realtime.Title,
                    Outcome = ActionOutcome.TamperProtected, Reason = "Блокировка появилась во время применения." });
                securityAction = () =>
                {
                    realtime.State = ActionStateKind.Blocked;
                    return Task.FromResult(workerBlocked);
                };
                configure.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => recheck.IsEnabled);
                Require(tamperHelp.Visibility == Visibility.Visible && !configure.IsEnabled &&
                    Find<TextBlock>(window, "ProtectionReport").Text.Contains("Блокировка появилась"),
                    "A blocker reported by the worker must show recheck alongside the action result.");
                realtime.State = ActionStateKind.AlreadyDesired;
                realtime.Reason = "Защита в реальном времени уже выключена.";
                recheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => recheck.IsEnabled);
                Require(tamperHelp.Visibility == Visibility.Collapsed && !configure.IsEnabled &&
                    Find<TextBlock>(window, "ProtectionReport").Text == realtime.Reason && securityRuns == runsBeforeRecheck + 1,
                    "Recheck must show when RTP is already off without starting another write.");
                realtime.State = ActionStateKind.NeedsChange;
                WaitTask((Task)Invoke(controller, "RefreshSecurityStateAsync", false));
                passed += 8;

                Find<Button>(window, "ApplyTaskbarButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(Find<TextBlock>(window, "TaskbarReport").Text == "Режим панели задач подтверждён.", "Taskbar success must be inline.");
                operations.ApplyTaskbar = () => Task.FromResult(new TaskbarResult { Reason = "Windows не подтвердила режим; исходное значение восстановлено." });
                Find<Button>(window, "ApplyTaskbarButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(Find<TextBlock>(window, "TaskbarReport").Text.Contains("восстановлено"), "Taskbar failure and rollback details must remain inline.");
                Find<Button>(window, "TaskbarSaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(Find<TextBlock>(window, "TaskbarExportReport").Text.Contains("Тестовый отказ") &&
                    Find<TextBlock>(window, "TaskbarReport").Text.Contains("восстановлено"), "Taskbar export errors must keep the independent repair result.");
                saveFails = false;
                Find<Button>(window, "SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(Find<TextBlock>(window, "ProtectionExportReport").Text == "Автономный EXE сохранён.", "A successful save must replace the earlier save failure.");
                passed += 4;

                Invoke(controller, "OnUpdateStatus", new UpdateStatus("Не удалось проверить обновления", null, false,
                    "Проверка манифеста: сервер недоступен, HRESULT=0x80004005."));
                Require(Find<TextBlock>(window, "UpdateReport").Visibility == Visibility.Visible &&
                    Find<TextBlock>(window, "UpdateReport").Text.Contains("Проверка манифеста") &&
                    Find<TextBlock>(window, "UpdateProgressText").Text == "Не удалось проверить обновления",
                    "Startup update service failures must deliver their details to the inline report.");
                Invoke(controller, "OnUpdateStatus", new UpdateStatus("Обновление готово", 1, true));
                Require(Find<TextBlock>(window, "UpdateReport").Visibility == Visibility.Collapsed &&
                    Find<ScrollViewer>(window, "UpdateReportScroll").Visibility == Visibility.Collapsed,
                    "A healthy update status must clear obsolete diagnostic details.");
                passed += 2;

                Invoke(controller, "SetUpdateStatus", "Обновление готово", (double?)1, true);
                SetControllerFlag(controller, "updateReady", false);
                SetControllerFlag(controller, "closeAfterAction", true);
                Invoke(controller, "Closing", window, new System.ComponentModel.CancelEventArgs());
                Require(installAttempts == 1 && prepared && Find<Button>(window, "UpdateButton").IsEnabled && configure.IsEnabled,
                    "A failed installation must preserve the prepared update and restore usable controls even before a ready notification or after deferred closing.");
                Require(Find<TextBlock>(window, "UpdateProgressText").Text == "Не удалось установить обновление" &&
                    Find<TextBlock>(window, "UpdateReport").Text.Contains("Win32 5"), "Update errors must separate a short bar status from detailed inline diagnostics.");
                SetControllerFlag(controller, "restartAfterUpdate", true);
                Invoke(controller, "Closing", window, new System.ComponentModel.CancelEventArgs());
                Require(installAttempts == 2 && prepared && Find<Button>(window, "UpdateButton").IsEnabled,
                    "An explicit retry must re-enter scheduling without discarding the prepared package.");
                passed += 3;

                var longText = new System.Text.StringBuilder("Подтверждено сейчас: 6 из 18.\n");
                for (int index = 0; index < 18; index++)
                    longText.Append("\n• Параметр ").Append(index + 1).Append(": документированный программный способ недоступен; результат не подтверждён.");
                realtime.State = ActionStateKind.Blocked;
                WaitTask((Task)Invoke(controller, "RefreshSecurityStateAsync", false));
                Invoke(controller, "SetReport", Find<TextBlock>(window, "ProtectionReport"), longText.ToString());
                Invoke(controller, "SetUpdateReport", longText.ToString());
                foreach (Size outerSize in new[] { new Size(820, 560), new Size(1100, 760) })
                {
                    MeasureClient(window, root, outerSize);
                    ScrollViewer details = Find<ScrollViewer>(window, "UpdateReportScroll");
                    Require(details.ActualHeight <= 96.5 && details.ScrollableHeight > 0.5,
                        "Long update details must stay within their footer limit and remain scrollable.");
                    AssertInside(Find<Grid>(window, "UpdateArea"), root, "A long report must keep the update footer inside the window.");
                    AssertInside(Find<Button>(window, "UpdateButton"), root, "A long report must keep the update button accessible.");
                    ScrollViewer settings = Find<ScrollViewer>(window, "SettingsScroll");
                    Require(settings.ActualHeight > 100 && settings.ScrollableHeight > 0.5, "Long card reports must use the main settings scrollbar.");
                    TextBlock report = Find<TextBlock>(window, "ProtectionReport");
                    Border card = Find<Border>(window, "ProtectionCard");
                    AssertInside(report, card, "The full wrapped protection report must remain inside its expanding card.");
                    AssertInside(tamperHint, card, "The wrapped Tamper instructions must remain within the protection card.");
                    AssertInside(recheck, card, "The recheck button must remain within the protection card.");
                    Require(Bounds(tamperHint, card).Right <= Bounds(recheck, card).Left,
                        "The instructions must wrap without overlapping their recheck button.");
                    foreach (double scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 }) AssertScaledRendering(root, scale);
                    SaveReportPreview(window, root, outerSize);
                }
                passed++;
                Require(!window.IsLoaded && !window.IsVisible, "Inline UI tests must not show windows or trigger production startup.");
            }
            finally
            {
                Invoke(controller, "Closed", window, EventArgs.Empty);
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
            return passed;
        }

        static object Invoke(MainWindowController controller, string method, params object[] args)
        {
            return typeof(MainWindowController).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(controller, args);
        }

        static void WaitTask(Task task)
        {
            PumpUntil(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
        }

        static void PumpUntil(Func<bool> completed)
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!completed())
            {
                Require(deadline.Elapsed < TimeSpan.FromSeconds(10), "A fixed UI operation did not complete.");
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(1);
            }
        }

        sealed class FakeTweaks : ITweakService
        {
            internal int Inspections;
            internal List<TweakId> Applied = new List<TweakId>();
            internal bool Hold;
            internal bool Detached;
            public bool HasForegroundWork { get { return !Detached && Pending.Any(item => !item.Item2.Task.IsCompleted); } }
            public event Action StateChanged;
            internal void Detach()
            {
                Detached = true;
                var changed = StateChanged;
                if (changed != null) changed();
            }
            internal readonly List<Tuple<TweakId[], TaskCompletionSource<TweakRun>>> Pending = new List<Tuple<TweakId[], TaskCompletionSource<TweakRun>>>();
            internal void Complete(int index)
            {
                var pending = Pending[index];
                var snapshot = Snapshot();
                foreach (var row in snapshot.Rows.Where(row => pending.Item1.Contains(row.Id)))
                    row.Parts[0].State = TweakState.Applied;
                pending.Item2.SetResult(new TweakRun { Snapshot = snapshot, Message = "Готово" });
            }
            internal TweakSnapshot FreshSnapshot() { return Snapshot(); }
            TweakSnapshot Snapshot()
            {
                var result = new TweakSnapshot();
                foreach (var id in TweakCatalog.All) result.Rows.Add(new TweakRow { Id = id, Parts = new List<TweakPart> { new TweakPart("test", TweakState.Pending) } });
                return result;
            }
            public Task<TweakSnapshot> InspectAsync() { Inspections++; return Task.FromResult(Snapshot()); }
            public Task<TweakRun> ApplyAsync(TweakId[] ids)
            {
                Applied = ids.ToList();
                if (!Hold) return Task.FromResult(new TweakRun { Snapshot = Snapshot(), Message = "Готово" });
                var completion = new TaskCompletionSource<TweakRun>();
                Pending.Add(Tuple.Create(ids, completion));
                return completion.Task;
            }
        }

        static T Find<T>(Window window, string name) where T : FrameworkElement
        {
            T element = window.FindName(name) as T;
            Require(element != null, "Missing UI element: " + name);
            return element;
        }

        static void AssertEmptyCategory(Window window, string name)
        {
            Expander category = Find<Expander>(window, name);
            Grid content = category.Content as Grid;
            Require(!category.IsExpanded && category.IsEnabled && content != null && content.Children.Count == 0,
                name + " must be an available, collapsed, empty category.");
        }

        static void AssertEmbeddedIcon(Window window, Assembly assembly)
        {
            BitmapSource actual = window.Icon as BitmapSource;
            Require(actual != null, "The window must have its embedded application icon.");
            using (Stream stream = assembly.GetManifestResourceStream("AppIcon"))
            {
                Require(stream != null, "AppIcon must be embedded in the executable.");
                BitmapSource expected = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Require(actual.PixelWidth == expected.PixelWidth && actual.PixelHeight == expected.PixelHeight,
                    "The window icon must keep its embedded pixel dimensions.");
                var actualPixels = new FormatConvertedBitmap(actual, PixelFormats.Bgra32, null, 0);
                var expectedPixels = new FormatConvertedBitmap(expected, PixelFormats.Bgra32, null, 0);
                int stride = expected.PixelWidth * 4;
                byte[] actualBytes = new byte[stride * expected.PixelHeight];
                byte[] expectedBytes = new byte[actualBytes.Length];
                actualPixels.CopyPixels(actualBytes, stride, 0);
                expectedPixels.CopyPixels(expectedBytes, stride, 0);
                for (int index = 0; index < actualBytes.Length; index++)
                    Require(actualBytes[index] == expectedBytes[index], "The window icon differs from its embedded asset.");
            }
        }

        static void MeasureClient(Window window, Grid root, Size outerSize)
        {
            Thickness frame = SystemParameters.WindowNonClientFrameThickness;
            var client = new Size(outerSize.Width - frame.Left - frame.Right, outerSize.Height - frame.Top - frame.Bottom);
            // An unshown Window may remeasure its content to its desired size
            // during UpdateLayout. A fixed offscreen host gives the content
            // the real client area without starting Window.Loaded or a HWND.
            if (ReferenceEquals(window.Content, root)) window.Content = null;
            Border previous = root.Parent as Border;
            if (previous != null) previous.Child = null;
            var host = new Border
            {
                Width = client.Width,
                Height = client.Height,
                Background = window.Background,
                Child = root
            };
            NameScope.SetNameScope(host, NameScope.GetNameScope(window));
            System.Windows.Documents.TextElement.SetForeground(host, window.Foreground);
            System.Windows.Documents.TextElement.SetFontFamily(host, window.FontFamily);
            System.Windows.Documents.TextElement.SetFontSize(host, window.FontSize);
            host.Measure(client);
            host.Arrange(new Rect(new Point(), client));
            host.UpdateLayout();
            Require(root.ActualWidth > 0 && root.ActualHeight > 0, "The real content must receive a nonzero client layout.");
            Require(Math.Abs(root.ActualWidth - client.Width + root.Margin.Left + root.Margin.Right) <= 0.75,
                "The offscreen content must use the requested client width.");
            Require(!window.IsLoaded, "Offscreen measurement must not trigger the Loaded network check.");
        }

        static void AssertLayout(Window window, Grid root)
        {
            ScrollViewer settings = Find<ScrollViewer>(window, "SettingsScroll");
            settings.ScrollToTop();
            settings.UpdateLayout();
            AssertInside(Find<Expander>(window, "WindowsTweakerSection"), settings,
                "The Windows tweaker category must fit inside the initial settings viewport.");
            settings.ScrollToBottom();
            settings.UpdateLayout();
            Grid footer = Find<Grid>(window, "UpdateArea");
            Button configure = Find<Button>(window, "ConfigureSecurityButton");
            Button save = Find<Button>(window, "SaveButton");
            Button icon = Find<Button>(window, "IconButton");
            CheckBox startMenu = Find<CheckBox>(window, "AddToStartMenu");
            Button update = Find<Button>(window, "UpdateButton");
            ProgressBar bar = Find<ProgressBar>(window, "UpdateProgressBar");
            AssertInside(footer, root, "The update footer must fit inside the client area.");
            AssertInside(update, root, "The update button must not be clipped by the window.");
            AssertInside(configure, settings, "The configuration button must fit inside the settings viewport.");
            AssertInside(save, settings, "The Save button must fit inside the settings viewport.");
            AssertInside(icon, settings, "The icon button must fit inside the settings viewport.");
            AssertInside(startMenu, settings, "The Start menu checkbox must fit inside the settings viewport.");
            AssertInside(bar, footer, "The update progress bar must fit inside the footer.");
            AssertInside(Find<TextBlock>(window, "UpdateProgressText"), bar, "The progress text must fit inside its bar.");
            Rect barBounds = Bounds(bar, footer);
            Rect buttonBounds = Bounds(update, footer);
            Require(buttonBounds.Left - barBounds.Right >= 7.5, "The update bar must not overlap the button's 8 DIP gap.");
            Require(settings.ActualHeight > 0 && settings.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                "The tools must have a usable viewport with automatic scrolling as categories grow.");
            Require(configure.ActualWidth >= 96 && update.ActualWidth >= 95.5 && update.ActualHeight >= 35.5,
                "Action buttons must retain their intended hit area.");
            Require(Math.Abs(icon.ActualWidth - save.ActualHeight) <= 0.5 &&
                Math.Abs(icon.ActualHeight - save.ActualHeight) <= 0.5,
                "The icon selector must be square and match the Save button height.");
            Rect configureBounds = Bounds(configure, settings), saveBounds = Bounds(save, settings), iconBounds = Bounds(icon, settings);
            Require(configureBounds.Right + 7.5 <= saveBounds.Left && saveBounds.Right + 7.5 <= iconBounds.Left,
                "The action buttons must appear in Disable, Save, icon order with their gaps.");
            Rect titleBounds = Bounds(Find<TextBlock>(window, "ProtectionTitle"), settings);
            Require(Math.Abs(configureBounds.Top + configureBounds.Height / 2 - titleBounds.Top - titleBounds.Height / 2) <= 0.75,
                "The action buttons must be aligned vertically with the title.");
            Require(Bounds(startMenu, settings).Top >= titleBounds.Bottom + 7.5,
                "The Start menu checkbox must sit immediately below the title.");
            Border card = Find<Border>(window, "ProtectionCard");
            Require(Math.Abs(Bounds(startMenu, settings).Left - Bounds(card, settings).Left -
                card.BorderThickness.Left - card.Padding.Left) <= 0.75,
                "The Start menu checkbox must align with the card's inner left edge.");
            Require(Math.Abs(Bounds(startMenu, settings).Left -
                Bounds(Find<FrameworkElement>(window, "ProtectionIcon"), settings).Left) <= 0.75,
                "The Start menu checkbox must begin directly under the setting icon, without a title-text indent.");
            foreach (string name in new[] { "ApplyTaskbarButton", "TaskbarSaveButton", "TaskbarIconButton", "TaskbarAddToStartMenu" })
                AssertInside(Find<FrameworkElement>(window, name), settings, "Taskbar controls must fit the minimum viewport: " + name);
            Require(Math.Abs(Bounds(Find<CheckBox>(window, "TaskbarAddToStartMenu"), settings).Left -
                Bounds(Find<FrameworkElement>(window, "TaskbarSettingIcon"), settings).Left) <= 0.75,
                "Taskbar export checkbox must begin under its icon.");
            TextBlock administratorNote = Find<TextBlock>(window, "ProtectionDescription");
            if (administratorNote.Visibility == Visibility.Visible)
            {
                AssertInside(administratorNote, settings, "The administrator note must fit below the action buttons.");
                Require(Bounds(administratorNote, settings).Top >= configureBounds.Bottom + 7.5,
                    "The administrator note must appear under the action buttons.");
            }
        }

        static void AssertScaledRendering(Grid root, double scale)
        {
            int width = (int)Math.Ceiling(root.ActualWidth * scale);
            int height = (int)Math.Ceiling(root.ActualHeight * scale);
            var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root);
            Require(bitmap.PixelWidth == width && bitmap.PixelHeight == height,
                "The offscreen renderer must retain the complete scaled client canvas.");
        }

        static void SaveReportPreview(Window window, Grid root, Size outerSize)
        {
            string directory = Path.GetFullPath(Path.Combine("Tests", "Artifacts", "Reports"));
            Directory.CreateDirectory(directory);
            var canvas = (FrameworkElement)root.Parent;
            var preview = new RenderTargetBitmap((int)Math.Ceiling(canvas.ActualWidth), (int)Math.Ceiling(canvas.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            var backdrop = new DrawingVisual();
            using (var drawing = backdrop.RenderOpen())
            {
                var bounds = new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight);
                drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 32, 32)), null, bounds);
                drawing.DrawRectangle(window.Background, null, bounds);
            }
            preview.Render(backdrop);
            preview.Render(canvas);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(preview));
            using (var output = File.Create(Path.Combine(directory, "window-" + outerSize.Width + ".png"))) encoder.Save(output);
        }

        static Rect Bounds(FrameworkElement element, FrameworkElement relativeTo)
        {
            return element.TransformToVisual(relativeTo).TransformBounds(new Rect(element.RenderSize));
        }

        static void AssertInside(FrameworkElement element, FrameworkElement container, string message)
        {
            Rect bounds = Bounds(element, container);
            const double tolerance = 0.75;
            Require(element.ActualWidth > 0 && element.ActualHeight > 0 &&
                bounds.Left >= -tolerance && bounds.Top >= -tolerance &&
                bounds.Right <= container.ActualWidth + tolerance && bounds.Bottom <= container.ActualHeight + tolerance, message);
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}

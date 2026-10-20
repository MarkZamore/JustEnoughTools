using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

            bool removalInvoked = false;
            Assembly applicationAssembly = typeof(MainWindowController).Assembly;
            var controller = new MainWindowController(1, applicationAssembly.Location,
                delegate(IProgress<string> progress)
                {
                    removalInvoked = true;
                    throw new InvalidOperationException("A UI test must not invoke deletion.");
                });
            Window window = controller.Window;
            Grid root = Find<Grid>(window, "RootGrid");
            int passed = 0;

            Require(window.Title == "JustEnoughTools", "Unexpected window title.");
            Require(window.Width == 820 && window.Height == 560 && window.MinWidth == 820 && window.MinHeight == 560,
                "The initial and minimum window size must be 820 x 560 DIP.");
            AssertEmptyCategory(window, "WalkieTalkieSection");
            AssertEmptyCategory(window, "ClickerSection");
            passed++;

            Button remove = Find<Button>(window, "DeleteProtectionButton");
            Button update = Find<Button>(window, "UpdateButton");
            Find<Border>(window, "ProtectionCard");
            Require(!String.IsNullOrWhiteSpace(Find<TextBlock>(window, "ProtectionTitle").Text) &&
                !String.IsNullOrWhiteSpace(Find<TextBlock>(window, "ProtectionDescription").Text),
                "The protection action must have a visible title and description.");
            Require(Equals(remove.Content, "Отключить") && remove.IsEnabled, "The configuration button must be available with its supplied action.");
            Require(Equals(update.Content, "Обновить") && !update.IsEnabled, "The update button must start disabled.");
            Require(update.Width == 96, "The update button must be 96 DIP wide.");
            Require(Find<ProgressBar>(window, "UpdateProgressBar").MinHeight == 36, "The update bar must be at least 36 DIP high.");
            Require(Find<TextBlock>(window, "UpdateProgressText").Text == "Проверка обновлений", "The initial update text must not claim the version was checked.");
            Require(Find<TextBlock>(window, "ProtectionStatus").Visibility == Visibility.Collapsed, "An empty action result must not occupy a row.");
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

            Find<TextBlock>(window, "ProtectionStatus").Text =
                "Не удалось завершить удаление. Доступ к защищённому компоненту запрещён; проверьте результат и повторите действие.";
            Find<TextBlock>(window, "UpdateProgressText").Text =
                "Обновление 65534: 100 МБ / 120 МБ (2,4 МБ/с, осталось 8 с)";
            MeasureClient(window, root, new Size(820, 560));
            AssertLayout(window, root);
            Require(Find<TextBlock>(window, "ProtectionStatus").ActualHeight > 0, "A result message must be visible.");
            passed++;

            Require(!window.IsVisible && !window.IsLoaded && !removalInvoked,
                "UI tests must not show the window, run Loaded checks, or invoke deletion.");
            // Do not call Close: its production handler may schedule a prepared
            // update. This unshown window has no active task or dispatcher loop.
            return passed;
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
            root.InvalidateMeasure();
            root.Measure(client);
            root.Arrange(new Rect(new Point(), client));
            root.UpdateLayout();
            Require(root.ActualWidth > 0 && root.ActualHeight > 0, "The real content must receive a nonzero client layout.");
            Require(!window.IsLoaded, "Offscreen measurement must not trigger the Loaded network check.");
        }

        static void AssertLayout(Window window, Grid root)
        {
            ScrollViewer settings = Find<ScrollViewer>(window, "SettingsScroll");
            Grid footer = Find<Grid>(window, "UpdateArea");
            Button remove = Find<Button>(window, "DeleteProtectionButton");
            Button update = Find<Button>(window, "UpdateButton");
            ProgressBar bar = Find<ProgressBar>(window, "UpdateProgressBar");
            AssertInside(footer, root, "The update footer must fit inside the client area.");
            AssertInside(update, root, "The update button must not be clipped by the window.");
            AssertInside(remove, settings, "The deletion button must fit inside the settings viewport.");
            AssertInside(bar, footer, "The update progress bar must fit inside the footer.");
            AssertInside(Find<TextBlock>(window, "UpdateProgressText"), bar, "The progress text must fit inside its bar.");
            Rect barBounds = Bounds(bar, footer);
            Rect buttonBounds = Bounds(update, footer);
            Require(buttonBounds.Left - barBounds.Right >= 7.5, "The update bar must not overlap the button's 8 DIP gap.");
            Require(settings.ActualHeight > 0 && settings.ExtentHeight <= settings.ViewportHeight + 0.5,
                "The initial tools and result message must fit the minimum settings viewport.");
            Require(remove.ActualWidth >= 96 && update.ActualWidth >= 95.5 && update.ActualHeight >= 35.5,
                "Action buttons must retain their intended hit area.");
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

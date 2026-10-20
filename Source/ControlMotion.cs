using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace JustEnoughTools
{
    public static class ControlMotion
    {
        public const double DurationMilliseconds = 120;

        public static void AttachTaskbarSwitch(CheckBox control)
        {
            System.Windows.Automation.AutomationProperties.SetHelpText(control, "Слева — режим 1, справа — режим 2. Пробел переключает режим.");
            control.PreviewKeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs args)
            {
                if (args.Key == System.Windows.Input.Key.Left || args.Key == System.Windows.Input.Key.Home) control.IsChecked = false;
                else if (args.Key == System.Windows.Input.Key.Right || args.Key == System.Windows.Input.Key.End) control.IsChecked = true;
                else return;
                args.Handled = true;
            };
            RoutedEventHandler changed = delegate { UpdateTaskbarSwitch(control, true); };
            control.Checked += changed; control.Unchecked += changed;
            control.Loaded += delegate { UpdateTaskbarSwitch(control, false); };
            control.PreviewMouseLeftButtonDown += delegate(object sender, System.Windows.Input.MouseButtonEventArgs args)
            {
                control.ApplyTemplate();
                var track = control.Template.FindName("SwitchTrack", control) as Border;
                if (track == null || !track.IsMouseOver) return;
                bool target = args.GetPosition(track).X >= track.ActualWidth / 2;
                control.IsChecked = control.IsChecked == target ? !target : target;
                control.Focus(); args.Handled = true;
            };
            UpdateTaskbarSwitch(control, false);
        }
        static void UpdateTaskbarSwitch(CheckBox control, bool animate)
        {
            control.ApplyTemplate();
            var label = control.Template.FindName("SwitchState", control) as TextBlock;
            var thumb = control.Template.FindName("SwitchThumb", control) as Ellipse;
            if (label != null) label.Text = control.IsChecked == true ? "Режим 2" : "Режим 1";
            if (thumb == null) return;
            var transform = thumb.RenderTransform as TranslateTransform;
            if (transform == null || transform.IsFrozen) { transform = new TranslateTransform(); thumb.RenderTransform = transform; }
            double before = transform.X, target = control.IsChecked == true ? 46 : 0;
            transform.BeginAnimation(TranslateTransform.XProperty, null); transform.X = target;
            if (animate && control.IsLoaded && SystemParameters.ClientAreaAnimation && Math.Abs(before-target) > 0.001)
                transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(before, target, TimeSpan.FromMilliseconds(DurationMilliseconds)) {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }

        public static void AttachCategory(Expander category)
        {
            RoutedEventHandler changed = delegate(object sender, RoutedEventArgs args)
            {
                if (args.OriginalSource == category) UpdateArrow(category, true);
            };
            category.Expanded += changed;
            category.Collapsed += changed;
            category.Loaded += delegate { UpdateArrow(category, false); };
        }

        static void UpdateArrow(Expander category, bool requested)
        {
            category.ApplyTemplate();
            ToggleButton header = category.Template.FindName("HeaderToggle", category) as ToggleButton;
            if (header == null) return;
            header.ApplyTemplate();
            Path chevron = header.Template.FindName("Chevron", header) as Path;
            if (chevron == null) return;
            RotateTransform rotation = chevron.RenderTransform as RotateTransform;
            if (rotation == null || rotation.IsFrozen)
            {
                rotation = rotation == null ? new RotateTransform() : rotation.Clone();
                chevron.RenderTransform = rotation;
            }
            double target = category.IsExpanded ? 0 : -90;
            double start = rotation.Angle;
            rotation.BeginAnimation(RotateTransform.AngleProperty, null);
            rotation.Angle = target;
            if (!requested || !category.IsLoaded || !SystemParameters.ClientAreaAnimation || Math.Abs(start - target) < 0.001) return;
            rotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(start, target, TimeSpan.FromMilliseconds(DurationMilliseconds))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop
                });
        }
    }
}

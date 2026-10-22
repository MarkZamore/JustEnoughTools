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

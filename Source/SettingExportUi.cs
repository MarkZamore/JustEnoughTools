using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JustEnoughTools.Export;

namespace JustEnoughTools
{
    internal sealed class SettingExportUi
    {
        readonly Window owner;
        readonly UniformGrid choices;
        readonly Image preview;
        readonly Action<string> showProblem;
        internal readonly Button Save, Icon;
        internal readonly CheckBox StartMenu;
        internal readonly Popup Popup;
        internal readonly IconSelection Selection;
        static readonly string[] Resources = { "SecurityIcon", "TaskbarIcon" };
        static readonly string[] Names = { "Защита Windows", "Панель задач", "Выбрать свою иконку" };

        internal SettingExportUi(Window owner, string prefix, string defaultIcon, Action<string> showProblem)
        {
            this.owner = owner; this.showProblem = showProblem;
            Selection = new IconSelection(defaultIcon);
            Save = (Button)owner.FindName(prefix + "SaveButton");
            Icon = (Button)owner.FindName(prefix + "IconButton");
            StartMenu = (CheckBox)owner.FindName(prefix + "AddToStartMenu");
            Popup = (Popup)owner.FindName(prefix + "IconPopup");
            choices = (UniformGrid)owner.FindName(prefix + "IconChoices");
            preview = (Image)owner.FindName(prefix + "CurrentIcon");
            Icon.Click += delegate { Popup.IsOpen = !Popup.IsOpen; };
            Popup.Opened += delegate { ((Button)choices.Children[SelectedIndex]).Focus(); };
            choices.PreviewKeyDown += KeyDown;
            owner.PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Escape && Popup.IsOpen) { Popup.IsOpen = false; Icon.Focus(); args.Handled = true; }
            };
            Refresh();
        }
        int SelectedIndex { get { return Selection.IsCustom ? 2 : Selection.BuiltInResource == Resources[0] ? 0 : 1; } }
        internal static BitmapSource ImageSource(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes, false))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapFrame chosen = decoder.Frames[0];
                foreach (BitmapFrame frame in decoder.Frames)
                    if (frame.PixelWidth >= 32 && (chosen.PixelWidth < 32 || frame.PixelWidth < chosen.PixelWidth)) chosen = frame;
                chosen.Freeze(); return chosen;
            }
        }
        internal void Refresh()
        {
            byte[] selected = Selection.Read();
            preview.Source = ImageSource(selected);
            choices.Children.Clear();
            for (int index = 0; index < 3; index++)
            {
                int choice = index;
                var button = new Button { Height = 64, Margin = new Thickness(4), ToolTip = Names[index], Padding = new Thickness(4) };
                System.Windows.Automation.AutomationProperties.SetName(button, Names[index]);
                if (SelectedIndex == index) button.Background = (Brush)owner.FindResource("Brush.SurfaceHover");
                if (index < 2 || Selection.IsCustom)
                    button.Content = new Image { Width = 32, Height = 32,
                        Source = ImageSource(index < 2 ? EmbeddedAssets.Read(Resources[index]) : selected) };
                else
                {
                    var empty = new Grid { Width = 32, Height = 32 };
                    empty.Children.Add(new System.Windows.Shapes.Rectangle { Stroke = (Brush)owner.FindResource("Brush.TextMuted"),
                        StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 2 }, RadiusX = 4, RadiusY = 4 });
                    empty.Children.Add(new TextBlock { Text = "?", FontSize = 20, Foreground = (Brush)owner.FindResource("Brush.TextMuted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                    button.Content = empty;
                }
                button.Click += delegate
                {
                    Popup.IsOpen = false;
                    try
                    {
                        if (choice < 2) Selection.SelectBuiltIn(Resources[choice]);
                        else
                        {
                            string path = ExportDialogs.SelectIcon(new WindowInteropHelper(owner).Handle);
                            if (path == null) { Icon.Focus(); return; }
                            Selection.SelectCustom(path);
                        }
                        Refresh();
                    }
                    catch (Exception error) { showProblem(error.Message); }
                    Icon.Focus();
                };
                choices.Children.Add(button);
            }
        }
        void KeyDown(object sender, KeyEventArgs args)
        {
            if (args.Key == Key.Escape) { Popup.IsOpen = false; Icon.Focus(); args.Handled = true; return; }
            var focused = Keyboard.FocusedElement as Button;
            int index = focused == null ? -1 : choices.Children.IndexOf(focused), next = index;
            if (index < 0) return;
            if (args.Key == Key.Left || args.Key == Key.Up) next = (index + 2) % 3;
            else if (args.Key == Key.Right || args.Key == Key.Down) next = (index + 1) % 3;
            else if (args.Key == Key.Home) next = 0;
            else if (args.Key == Key.End) next = 2;
            else return;
            ((Button)choices.Children[next]).Focus(); args.Handled = true;
        }
    }
}

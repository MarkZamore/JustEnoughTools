using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace JustEnoughTools.Tweaks
{
    internal sealed class TweakUi
    {
        readonly Window window;
        readonly Dictionary<TweakId, Button> buttons = new Dictionary<TweakId, Button>();
        readonly Dictionary<TweakId, TextBlock> labels = new Dictionary<TweakId, TextBlock>();
        internal readonly Button All;
        internal readonly TextBlock Report;
        internal TweakSnapshot Snapshot;
        internal Action<TweakId[]> Apply;
        internal TweakUi(Window window)
        {
            this.window = window;
            All = (Button)window.FindName("ApplyAllTweaks");
            Report = (TextBlock)window.FindName("TweaksReport");
            var grid = (Grid)window.FindName("TweaksGrid");
            Grid.SetIsSharedSizeScope(grid, true);
            for (int row = 0; row < 7; row++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            foreach (var id in TweakCatalog.All)
            {
                var cell = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = 36, SharedSizeGroup = "TweakTitleRow" + (((int)id - 1) / 2) });
                cell.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetRow(cell, ((int)id - 1) / 2); Grid.SetColumn(cell, ((int)id - 1) % 2 * 2);
                var label = new TextBlock { Text = TweakCatalog.Titles[(int)id - 1], TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                labels[id] = label;
                var button = new Button { Content = "Применить", IsEnabled = false, MinWidth = 96, VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(button, "Применить: " + TweakCatalog.Titles[(int)id - 1]);
                ToolTipService.SetShowOnDisabled(button, true);
                button.Click += delegate { if (Apply != null) Apply(new[] { id }); };
                buttons[id] = button;
                window.RegisterName("TweakCell" + (int)id, cell);
                window.RegisterName("TweakApply" + (int)id, button);
                Grid.SetColumn(button, 1); cell.Children.Add(label); cell.Children.Add(button); grid.Children.Add(cell);
            }
            All.Click += delegate { if (Apply != null) Apply(buttons.Where(pair => pair.Value.IsEnabled).Select(pair => pair.Key).ToArray()); };
        }
        internal void Refresh(bool available, ISet<TweakId> busy = null)
        {
            All.IsEnabled = available && Snapshot != null && Snapshot.Rows.Any(row => row.CanApply && (busy == null || !busy.Contains(row.Id)));
            foreach (var id in TweakCatalog.All)
            {
                var row = Snapshot == null ? null : Snapshot.Rows.FirstOrDefault(r => r.Id == id);
                bool running = busy != null && busy.Contains(id);
                buttons[id].IsEnabled = available && !running && row != null && row.CanApply;
                buttons[id].ToolTip = running ? "Операция уже выполняется." : buttons[id].IsEnabled ? null : BlockedTooltip(row, available);
                labels[id].ToolTip = SettingHint(id, row);
            }
        }
        internal static string BlockedTooltip(TweakRow row, bool available)
        {
            if (row == null) return "Проверка при запуске ещё не завершена.";
            if (!available) return "Выполняется другая операция.";
            string reason = null;
            if (row.CanApply) return null;
            else if (row.Parts.Any(p => p.State == TweakState.Running)) reason = "Операция уже выполняется.";
            else if (row.Parts.Any(p => p.State == TweakState.Failed)) reason = "Не удалось применить настройку.";
            else if (row.Parts.Any(p => p.State == TweakState.Unknown)) reason = "Не удалось проверить состояние.";
            else
            {
                var skipped = row.Parts.Where(p => p.State == TweakState.Unavailable).ToArray();
                if (skipped.Length != 0)
                {
                    if (skipped.All(p => (p.Reason ?? "").Contains("не проверено для этой сборки"))) reason = "Не проверено для этой сборки Windows.";
                    else if (skipped.All(p => (p.Reason ?? "").Contains("политик"))) reason = "Заблокировано политикой Windows.";
                    else if (skipped.Any(p => (p.Reason ?? "").Contains("Батарея не обнаружена"))) reason = "Батарея не обнаружена.";
                    else reason = "Настройка недоступна в этой системе.";
                    if (row.Parts.Any(p => p.State == TweakState.Applied || p.State == TweakState.Deferred))
                        reason = "Доступные параметры уже настроены. " + reason;
                }
                else reason = row.Parts.Any(p => p.State == TweakState.Deferred) ? "Сохранено; Windows применит позже." : "Уже настроено.";
            }
            return reason;
        }
        internal static string SettingHint(TweakId id, TweakRow row)
        {
            string hint = id == TweakId.Indexing ? "Диск C:. Для SSD обычно не нужно." :
                id == TweakId.BitLocker ? "Полная расшифровка C:." : id == TweakId.Mouse ? "Для шутеров." : null;
            if (row != null && row.CanApply && row.Parts.Any(p => p.State == TweakState.Unknown || p.State == TweakState.Unavailable))
                return (hint == null ? "" : hint + " ") + "Недоступные параметры будут пропущены.";
            return hint;
        }
        internal void SetReport(string text)
        {
            Report.Text = text ?? "";
            Report.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}

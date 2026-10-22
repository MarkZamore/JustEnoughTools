using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Windows.Forms;
using JustEnoughTools.Security;
using JustEnoughTools.Actions;

namespace JustEnoughTools.Runner
{
    internal static class Program
    {
        private const string ElevatedArgument = "--run-security-elevated";

        [STAThread]
        private static int Main(string[] args)
        {
            // The native manifest is asInvoker. Read-only discovery must be
            // possible without UAC when there is nothing left to change.
            // No constructor, updater, shortcuts or file reports are started.
            if (args.Length == 1 && args[0] == "--self-test")
                return typeof(SecurityEngine).GetMethod("Run", BindingFlags.Public | BindingFlags.Static,
                    null, Type.EmptyTypes, null) == null ? 1 : 0;
            bool elevatedInvocation = args.Length == 1 && args[0] == ElevatedArgument;
            if (args.Length != 0 && !elevatedInvocation) return 2;
            bool executionStarted = false;
            try
            {
                FixedAction action = FixedActionResource.Read();
                if (action != FixedAction.Security)
                {
                    if (elevatedInvocation) throw new InvalidOperationException("Для исправления панели задач служебное повышение прав не используется.");
                    TaskbarResult result = TaskbarRepair.Apply();
                    if (!result.Succeeded) ShowProblems(result.Reason);
                    return result.Succeeded ? 0 : 2;
                }
                bool elevated = IsElevated();
                // A command-line flag never grants a token or bypasses UAC.
                if (elevatedInvocation && !elevated)
                    throw new InvalidOperationException("Служебный запуск не получил права администратора. Изменения не начинались.");
                SecuritySnapshot snapshot = SecurityEngine.Inspect();
                if (snapshot == null) throw new InvalidOperationException("Не получено состояние настроек. Изменения не начинались.");
                if (!snapshot.HasChanges) return Finish(snapshot.ToReport());
                if (snapshot.RequiresElevation && !elevated)
                {
                    string executable;
                    using (var current = Process.GetCurrentProcess()) executable = current.MainModule.FileName;
                    Process child;
                    try
                    {
                        child = Process.Start(new ProcessStartInfo(executable, ElevatedArgument)
                        {
                            UseShellExecute = true,
                            Verb = "runas",
                            WorkingDirectory = Path.GetDirectoryName(executable)
                        });
                    }
                    catch (Win32Exception error) when (error.NativeErrorCode == 1223)
                    {
                        ShowProblems("Запрос прав администратора отменён. Изменения не выполнялись.");
                        return 2;
                    }
                    if (child == null) throw new InvalidOperationException("Windows не вернула рабочий процесс. Изменения не начинались.");
                    using (child)
                    {
                        // The child owns the final report. The original process
                        // neither runs the catalogue again nor repeats its dialog.
                        child.WaitForExit();
                        return child.ExitCode;
                    }
                }
                executionStarted = true;
                SecurityReport report = SecurityEngine.Run();
                return Finish(report);
            }
            catch (Exception error)
            {
                ShowProblems((executionStarted
                    ? "Выполнение прервано; часть настроек могла измениться.\n\n"
                    : "Изменения не начинались.\n\n") + error.Message);
                return 1;
            }
        }

        private static int Finish(SecurityReport report)
        {
            if (report == null) throw new InvalidOperationException("Не получен результат выполнения.");
            if (report.Succeeded) return 0;
            ShowProblems(report.FormatProblems());
            return 2;
        }

        private static void ShowProblems(string message)
        {
            Application.EnableVisualStyles();
            using (var font = new Font("Segoe UI", 10.5f))
            using (var dialog = CreateProblemDialog(message, font))
                dialog.ShowDialog();
        }

        private static Form CreateProblemDialog(string message, Font font)
        {
            Rectangle initialWork = Screen.FromPoint(Cursor.Position).WorkingArea;
            var dialog = new Form
            {
                Text = "JustEnoughTools",
                StartPosition = FormStartPosition.Manual,
                // Select the destination monitor before creating its DPI-aware
                // handle; Load measures and centres the final content size.
                Location = new Point(initialWork.Left + initialWork.Width / 2,
                    initialWork.Top + initialWork.Height / 2),
                MinimizeBox = false,
                ShowInTaskbar = true,
                Font = font,
                AutoScaleMode = AutoScaleMode.Dpi,
                Padding = new Padding(16),
                BackColor = Color.FromArgb(28, 32, 40),
                ForeColor = Color.FromArgb(237, 240, 245)
            };
            try
            {
                using (var current = Process.GetCurrentProcess())
                    dialog.Icon = Icon.ExtractAssociatedIcon(current.MainModule.FileName);
            }
            catch { }
            var text = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                WordWrap = true,
                ScrollBars = ScrollBars.None,
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(35, 40, 49),
                ForeColor = dialog.ForeColor,
                Text = message ?? "Результат не подтверждён.",
                SelectionStart = 0,
                SelectionLength = 0
            };
            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 48,
                Padding = new Padding(0, 12, 0, 0)
            };
            var close = new Button
            {
                Text = "ОК",
                DialogResult = DialogResult.OK,
                Size = new Size(96, 32),
                Margin = Padding.Empty,
                ForeColor = SystemColors.ControlText,
                UseVisualStyleBackColor = true
            };
            footer.Controls.Add(close);
            dialog.Controls.Add(text);
            dialog.Controls.Add(footer);
            dialog.AcceptButton = close;
            dialog.CancelButton = close;
            bool layoutReady = false;
            bool updatingScroll = false;
            Action updateScroll = delegate
            {
                // Recreating the edit handle to change ScrollBars while its
                // first native handle is still being created can fail.
                if (!layoutReady || updatingScroll || !text.IsHandleCreated || text.ClientSize.Height <= 0) return;
                updatingScroll = true;
                try
                {
                    using (Graphics graphics = text.CreateGraphics())
                    {
                        // Measure as though no scrollbar existed, so it can
                        // disappear again after the user enlarges the form.
                        int inset = Math.Max(4, (int)Math.Ceiling(6 * graphics.DpiX / 96f));
                        int width = Math.Max(1, text.Width - inset);
                        Size measured = MeasureWrapped(graphics, text.Text, text.Font, width);
                        text.ScrollBars = measured.Height + inset > text.ClientSize.Height
                            ? ScrollBars.Vertical : ScrollBars.None;
                    }
                }
                finally { updatingScroll = false; }
            };
            text.SizeChanged += delegate { updateScroll(); };
            dialog.Load += delegate
            {
                SizeToMessage(dialog, text, footer, close);
                layoutReady = true;
                updateScroll();
                text.SelectionStart = 0;
                text.SelectionLength = 0;
            };
            return dialog;
        }

        private static readonly TextFormatFlags WrappedTextFlags = TextFormatFlags.WordBreak |
            TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.ExpandTabs;

        private static Size MeasureWrapped(Graphics graphics, string message, Font font, int width)
        {
            return TextRenderer.MeasureText(graphics, message, font,
                new Size(Math.Max(1, width), int.MaxValue), WrappedTextFlags);
        }

        private static void SizeToMessage(Form dialog, TextBox text, FlowLayoutPanel footer, Button close)
        {
            using (Graphics graphics = dialog.CreateGraphics())
            {
                float scale = graphics.DpiX / 96f;
                int padding = Math.Max(8, (int)Math.Ceiling(16 * scale));
                int gap = Math.Max(6, (int)Math.Ceiling(12 * scale));
                int inset = Math.Max(4, (int)Math.Ceiling(6 * scale));
                Rectangle work = Screen.FromControl(dialog).WorkingArea;
                Size frame = new Size(dialog.Width - dialog.ClientSize.Width, dialog.Height - dialog.ClientSize.Height);
                int maxClientWidth = Math.Max(1, work.Width - 2 * padding - frame.Width);
                int maxClientHeight = Math.Max(1, work.Height - 2 * padding - frame.Height);
                int maxTextWidth = Math.Max(1, Math.Min((int)Math.Ceiling(760 * scale), maxClientWidth - 2 * padding));
                int minTextWidth = Math.Min(maxTextWidth, (int)Math.Ceiling(220 * scale));
                Size natural = TextRenderer.MeasureText(graphics, text.Text, text.Font,
                    new Size(16384, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl |
                    TextFormatFlags.ExpandTabs);
                int textWidth = Math.Max(minTextWidth, Math.Min(maxTextWidth, natural.Width + inset));
                int lineHeight = TextRenderer.MeasureText(graphics, "Ag", text.Font).Height + inset;
                close.Size = new Size(Math.Max(64, (int)Math.Ceiling(96 * scale)),
                    Math.Max(24, (int)Math.Ceiling(32 * scale)));
                footer.Padding = new Padding(0, gap, 0, 0);
                footer.Height = close.Height + gap;
                dialog.Padding = new Padding(padding);
                Size wrapped = MeasureWrapped(graphics, text.Text, text.Font, Math.Max(1, textWidth - inset));
                int maxTextHeight = Math.Max(1, maxClientHeight - 2 * padding - footer.Height);
                int textHeight = Math.Min(maxTextHeight, Math.Max(lineHeight, wrapped.Height + inset));
                dialog.MaximumSize = new Size(work.Width - 2 * padding, work.Height - 2 * padding);
                dialog.MinimumSize = new Size(Math.Min(dialog.MaximumSize.Width,
                    minTextWidth + 2 * padding + frame.Width), Math.Min(dialog.MaximumSize.Height,
                    lineHeight + footer.Height + 2 * padding + frame.Height));
                dialog.ClientSize = new Size(textWidth + 2 * padding, textHeight + footer.Height + 2 * padding);
                dialog.Location = new Point(work.Left + (work.Width - dialog.Width) / 2,
                    work.Top + (work.Height - dialog.Height) / 2);
            }
        }

        private static bool IsElevated()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}

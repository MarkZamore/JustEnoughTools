using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace JustEnoughTools.Tweaks
{
    internal static class IndexingOperation
    {
        internal static string Run()
        {
            string result = null;
            WindowsTweakPlatform.RunSta(() => result = Process(new IndexingFiles(), new IndexingProgress()));
            return result;
        }
        internal static string Process(IIndexingFiles files, IIndexingProgress progress)
        {
            int processed = 0, skipped = 0;
            try
            {
                progress.Open();
                var directories = new Stack<string>(); directories.Push(@"C:\");
                while (directories.Count != 0)
                {
                    if (progress.Cancelled) throw new OperationCanceledException("Индексирование: обработка отменена; ранее изменённые файлы сохранены.");
                    string directory = directories.Pop();
                    try
                    {
                        using (var enumerator = files.Children(directory).GetEnumerator())
                            while (true)
                            {
                                string path;
                                try { if (!enumerator.MoveNext()) break; path = enumerator.Current; }
                                catch (Exception error) when (FileFailure(error)) { skipped++; break; }
                                if (progress.Cancelled) throw new OperationCanceledException("Индексирование: обработка отменена; признак корня C: не изменён.");
                                try
                                {
                                    FileAttributes attributes = files.Attributes(path);
                                    if ((attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                                    if ((attributes & FileAttributes.Directory) != 0) directories.Push(path);
                                    files.SetAttributes(path, attributes | FileAttributes.NotContentIndexed); processed++;
                                }
                                catch (Exception error) when (FileFailure(error)) { skipped++; }
                                progress.Update(path, processed, skipped);
                            }
                    }
                    catch (Exception error) when (FileFailure(error)) { skipped++; }
                }
                if (progress.Cancelled) throw new OperationCanceledException("Индексирование: обработка отменена.");
                // Commit the Explorer root indicator last. A cancelled traversal
                // must not make the next startup mistake a partial run for completion.
                files.SetAttributes(@"C:\", files.Attributes(@"C:\") | FileAttributes.NotContentIndexed);
                return "Индексирование C: отключено. Обработано: " + processed + "; пропущено: " + skipped + ".";
            }
            finally { progress.Dispose(); }
        }
        static bool FileFailure(Exception error) { return error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException; }
    }
    internal interface IIndexingFiles
    {
        IEnumerable<string> Children(string directory);
        FileAttributes Attributes(string path);
        void SetAttributes(string path, FileAttributes attributes);
    }
    internal interface IIndexingProgress : IDisposable
    {
        void Open(); bool Cancelled { get; }
        void Update(string path, int processed, int skipped);
    }
    internal sealed class IndexingFiles : IIndexingFiles
    {
        public IEnumerable<string> Children(string path) { return Directory.EnumerateFileSystemEntries(path); }
        public FileAttributes Attributes(string path) { return File.GetAttributes(path); }
        public void SetAttributes(string path, FileAttributes attributes) { File.SetAttributes(path, attributes); }
    }
    internal sealed class IndexingProgress : IIndexingProgress
    {
        IProgressDialog native;
        Window fallback;
        TextBlock text;
        bool cancelled;
        DateTime last;
        public void Open()
        {
            try
            {
                native = (IProgressDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("F8383852-FCD3-11D1-A6B9-006097DF5BD4"), true));
                native.SetTitle("Отключение индексирования C:"); native.StartProgressDialog(IntPtr.Zero, null, 0x20 | 0x08, IntPtr.Zero);
            }
            catch
            {
                if (native != null) { try { native.StopProgressDialog(); } catch { } WindowsTweakPlatform.Release(native); native = null; }
                text = new TextBlock { Text = "Обработка файлов C:…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
                var panel = new StackPanel { Margin = new Thickness(20) }; panel.Children.Add(text);
                panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 20 });
                fallback = new Window { Title = "Отключение индексирования C:", Width = 460, SizeToContent = SizeToContent.Height, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Theme"))
                    fallback.Resources = (ResourceDictionary)XamlReader.Load(stream);
                fallback.Background = new SolidColorBrush(Color.FromRgb(32, 32, 32));
                fallback.Foreground = (Brush)fallback.Resources["Brush.Text"];
                fallback.FontFamily = (FontFamily)fallback.Resources["Font.Family"];
                fallback.FontSize = (double)fallback.Resources["Font.Body"];
                fallback.Closing += delegate { cancelled = true; }; fallback.Show();
            }
        }
        public bool Cancelled { get { Pump(); return cancelled || native != null && native.HasUserCancelled(); } }
        public void Update(string path, int processed, int skipped)
        {
            if ((DateTime.UtcNow - last).TotalMilliseconds < 100) return;
            last = DateTime.UtcNow;
            string count = "Обработано: " + processed + "; пропущено: " + skipped;
            if (native != null) { native.SetLine(1, path, true, IntPtr.Zero); native.SetLine(2, count, false, IntPtr.Zero); }
            else { text.Text = count + "\n" + path; Pump(); }
        }
        void Pump()
        {
            if (fallback == null) return;
            var frame = new DispatcherFrame();
            fallback.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
        }
        public void Dispose()
        {
            try { if (native != null) native.StopProgressDialog(); if (fallback != null) fallback.Close(); }
            finally { WindowsTweakPlatform.Release(native); native = null; fallback = null; }
        }
        [ComImport, Guid("EBBC7C04-315E-11D2-B62F-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IProgressDialog
        {
            void StartProgressDialog(IntPtr parent, [MarshalAs(UnmanagedType.IUnknown)] object enableModeless, uint flags, IntPtr reserved);
            void StopProgressDialog();
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetAnimation(IntPtr instance, uint resource);
            [PreserveSig]
            [return: MarshalAs(UnmanagedType.Bool)] bool HasUserCancelled();
            void SetProgress(uint complete, uint total);
            void SetProgress64(ulong complete, ulong total);
            void SetLine(uint line, [MarshalAs(UnmanagedType.LPWStr)] string text, [MarshalAs(UnmanagedType.Bool)] bool compact, IntPtr reserved);
            void SetCancelMsg([MarshalAs(UnmanagedType.LPWStr)] string message, IntPtr reserved);
            void Timer(uint action, IntPtr reserved);
        }
    }
}

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace JustEnoughTools.Actions
{
    public sealed class TaskbarResult
    {
        public bool Succeeded { get; internal set; }
        public string Reason { get; internal set; }
    }
    internal interface ITaskbarSettings : IDisposable
    {
        object Read(out RegistryValueKind kind);
        void Write(object value, RegistryValueKind kind);
        void Delete();
        bool Notify();
        void Pause();
    }
    public static class TaskbarRepair
    {
        public static TaskbarResult Apply(int mode)
        {
            if (mode != 1 && mode != 2) return Failure("Режим исправления должен быть 1 или 2.");
            try
            {
                using (var mutex = new Mutex(false, "Local\\JustEnoughTools.TaskbarRepair"))
                {
                    bool acquired;
                    try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) return Failure("Исправление панели задач уже выполняется.");
                    try { using (var backend = new WindowsTaskbarSettings()) return Apply(mode, backend); }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception error) { return Failure("Исправление панели задач недоступно: " + error.Message); }
        }
        internal static TaskbarResult Apply(int mode, ITaskbarSettings backend)
        {
            if (mode != 1 && mode != 2) return Failure("Режим исправления должен быть 1 или 2.");
            RegistryValueKind kind;
            object old;
            try { old = backend.Read(out kind); }
            catch (Exception error) { return Failure("Не удалось прочитать исходный режим: " + error.Message); }
            string failure;
            try
            {
                backend.Write(0, RegistryValueKind.DWord);
                if (!backend.Notify()) throw new InvalidOperationException("Панель задач не ответила на начальное уведомление.");
                backend.Pause();
                backend.Write(mode, RegistryValueKind.DWord);
                if (!backend.Notify()) throw new InvalidOperationException("Панель задач не подтвердила конечный режим.");
                RegistryValueKind actualKind;
                if (!Object.Equals(backend.Read(out actualKind), mode) || actualKind != RegistryValueKind.DWord)
                    throw new InvalidOperationException("Повторная проверка не подтвердила выбранный режим.");
                return new TaskbarResult { Succeeded = true, Reason = "Исправление панели задач: режим " + mode + "." };
            }
            catch (Exception error) { failure = error.Message; }
            try
            {
                if (old == null) backend.Delete(); else backend.Write(old, kind);
                backend.Notify();
            }
            catch (Exception error) { failure += " Не удалось восстановить исходный параметр: " + error.Message; }
            return Failure(failure);
        }
        static TaskbarResult Failure(string reason) { return new TaskbarResult { Reason = reason }; }

        sealed class WindowsTaskbarSettings : ITaskbarSettings
        {
            const string Path = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
            readonly RegistryKey key;
            readonly IntPtr window;
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr parameter, IntPtr data, uint flags, uint timeout, out UIntPtr result);
            internal WindowsTaskbarSettings()
            {
                window = FindWindow("Shell_TrayWnd", null);
                if (window == IntPtr.Zero) throw new InvalidOperationException("Панель задач недоступна.");
                key = Registry.CurrentUser.OpenSubKey(Path, true);
                if (key == null) throw new InvalidOperationException("Настройки панели задач недоступны.");
            }
            public object Read(out RegistryValueKind kind)
            {
                object value = key.GetValue("MMTaskbarMode", null);
                kind = value == null ? RegistryValueKind.DWord : key.GetValueKind("MMTaskbarMode");
                return value;
            }
            public void Write(object value, RegistryValueKind kind) { key.SetValue("MMTaskbarMode", value, kind); }
            public void Delete() { key.DeleteValue("MMTaskbarMode", false); }
            public bool Notify()
            {
                IntPtr text = Marshal.StringToHGlobalUni("TraySettings");
                try { UIntPtr result; return SendMessageTimeout(window, 0x1A, UIntPtr.Zero, text, 2, 500, out result) != IntPtr.Zero; }
                finally { Marshal.FreeHGlobal(text); }
            }
            public void Pause() { Thread.Sleep(60); }
            public void Dispose() { key.Dispose(); }
        }
    }
}

using System;
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
        public static TaskbarResult Apply()
        {
            try
            {
                using (var mutex = new Mutex(false, "Local\\JustEnoughTools.TaskbarRepair"))
                {
                    bool acquired;
                    try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) return Failure("Исправление панели задач уже выполняется.");
                    try { using (var backend = new WindowsTaskbarSettings()) return Apply(backend); }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception error) { return Failure("Исправление панели задач недоступно: " + error.Message); }
        }
        internal static TaskbarResult Apply(ITaskbarSettings backend)
        {
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
                backend.Write(2, RegistryValueKind.DWord);
                if (!backend.Notify()) throw new InvalidOperationException("Панель задач не подтвердила конечный режим.");
                RegistryValueKind actualKind;
                if (!Object.Equals(backend.Read(out actualKind), 2) || actualKind != RegistryValueKind.DWord)
                    throw new InvalidOperationException("Повторная проверка не подтвердила конечный режим.");
                return new TaskbarResult { Succeeded = true, Reason = "Возвращено отображение: Панель задач, в которой открыто окно." };
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
            internal WindowsTaskbarSettings()
            {
                window = TraySettingsNotification.FindTaskbar();
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
            public bool Notify() { return TraySettingsNotification.Notify(window); }
            public void Pause() { Thread.Sleep(60); }
            public void Dispose() { key.Dispose(); }
        }
    }
}

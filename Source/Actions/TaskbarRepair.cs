using System;
using System.Linq;
using System.Threading;
using WindowsPreferences;
using Microsoft.Win32;

namespace JustEnoughTools.Actions
{
    public sealed class TaskbarResult
    {
        public bool Succeeded { get; internal set; }
        public string Reason { get; internal set; }
    }
    public static class TaskbarRepair
    {
        public static TaskbarResult Apply()
        {
            try
            {
                using(var version=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    if(version==null||Convert.ToString(version.GetValue("CurrentBuildNumber"))!="26300"||!Equals(version.GetValue("UBR"),9711))
                        return Failure("Брокер исправления панели задач не проверен для этой сборки Windows.");
                using (var mutex = new Mutex(false, @"Local\JustEnoughTools.TaskbarRepair"))
                {
                    bool acquired;
                    try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) return Failure("Исправление панели задач уже выполняется.");
                    try { using (var backend = NativeWindowsPreferences.Open(WindowsPreferenceId.TaskbarMultiMonitorMode)) return Apply(backend); }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception error) { return Failure("Исправление панели задач недоступно: " + error.Message); }
        }
        internal static TaskbarResult Apply(IWindowsPreferenceSession backend)
        {
            WindowsPreferenceSnapshot original;
            try { original = ReadReady(backend); }
            catch (Exception error) { return Failure(error.Message); }
            string failure;
            try
            {
                // Always cycle, including when the final mode already matches.
                // Set verifies fresh models and the native taskbar value.
                backend.Set(original.Choices[0]);
                if (!Matches(ReadReady(backend),original,original.Choices[0])) throw new InvalidOperationException("Windows не подтвердила режим «Все панели».");
                backend.Set(original.Choices[2]);
                if (!Matches(ReadReady(backend),original,original.Choices[2])) throw new InvalidOperationException("Windows не подтвердила панель с открытым окном.");
                return new TaskbarResult { Succeeded = true, Reason = "Возвращено отображение: Панель задач, в которой открыто окно." };
            }
            catch (Exception error) { failure = error.Message; }
            try
            {
                ReadReady(backend); // A new policy/unavailable state must not be bypassed during rollback.
                backend.Set(original.Value);
                if (!Matches(ReadReady(backend),original,original.Value)) throw new InvalidOperationException("Исходный режим не подтверждён.");
            }
            catch (Exception error) { failure += " Не удалось восстановить исходный режим: " + error.Message; }
            return Failure(failure);
        }
        static bool Matches(WindowsPreferenceSnapshot actual,WindowsPreferenceSnapshot initial,object target)
        {return Equals(actual.Value,target)&&actual.Choices.SequenceEqual(initial.Choices);}
        static WindowsPreferenceSnapshot ReadReady(IWindowsPreferenceSession backend)
        {
            var state = backend.Read();
            if (state == null) throw new InvalidOperationException("Не удалось прочитать режим панели задач.");
            if (state.IsSetByGroupPolicy) throw new InvalidOperationException("Режим панели задач управляется политикой Windows.");
            if (!state.IsApplicable || !state.IsEnabled) throw new InvalidOperationException("Режим нескольких панелей задач недоступен в текущей конфигурации экранов.");
            if (state.IsUpdating) throw new InvalidOperationException("Режим панели задач ещё изменяется Windows.");
            if (!(state.Value is string) || state.Choices.Length!=3 || state.Choices.Any(string.IsNullOrEmpty) || state.Choices.Distinct(StringComparer.Ordinal).Count()!=3 || !state.Choices.Contains((string)state.Value))
                throw new InvalidOperationException("Windows вернула неподтверждённый список режимов панели задач.");
            return state;
        }
        static TaskbarResult Failure(string reason) { return new TaskbarResult { Reason = reason }; }
    }
}

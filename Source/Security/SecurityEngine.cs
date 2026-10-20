using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace JustEnoughTools.Security
{
    // Only this fixed catalogue is executable. No path, script or registry value
    // received through IPC or an exported executable is an instruction source.
    internal sealed class SecurityAction
    {
        internal readonly string Id, Title;
        internal SecurityAction(string id, string title) { Id = id; Title = title; }
    }

    internal interface ISecurityBackend
    {
        SecuritySnapshot Inspect();
        ActionResult Execute(SecurityAction action);
    }

    public static class SecurityEngine
    {
        internal const string MutexName = @"Global\JustEnoughTools.SecurityActions.v1";
        internal static readonly ReadOnlyCollection<SecurityAction> Catalogue = Array.AsReadOnly(new[]
        {
            new SecurityAction("defender.realtime", "Защита Defender в реальном времени"),
            new SecurityAction("defender.cloud", "Облачная защита Defender"),
            new SecurityAction("defender.samples", "Автоматическая отправка образцов"),
            new SecurityAction("defender.pua", "Блокировка нежелательных приложений Defender"),
            new SecurityAction("defender.cfa", "Контролируемый доступ к папкам"),
            new SecurityAction("defender.scheduled", "Плановое сканирование Defender"),
            new SecurityAction("defender.cancel", "Текущее быстрое или полное сканирование"),
            new SecurityAction("firewall.domain", "Брандмауэр: доменный профиль"),
            new SecurityAction("firewall.private", "Брандмауэр: частный профиль"),
            new SecurityAction("firewall.public", "Брандмауэр: общедоступный профиль"),
            new SecurityAction("smartscreen.shell", "SmartScreen: приложения и файлы"),
            new SecurityAction("smartscreen.edge", "SmartScreen Microsoft Edge"),
            new SecurityAction("smartscreen.edge-pua", "Блокировка нежелательных загрузок Edge"),
            new SecurityAction("smartscreen.phishing", "Усиленная защита от фишинга"),
            new SecurityAction("security.virus-ui", "Антивирусный раздел Windows Security"),
            new SecurityAction("defender.tamper", "Защита от изменений"),
            new SecurityAction("security.sac", "Smart App Control"),
            new SecurityAction("smartscreen.store", "SmartScreen приложений Microsoft Store")
        });

        public static SecurityReport Run()
        {
            if (!Environment.Is64BitProcess || Environment.OSVersion.Platform != PlatformID.Win32NT)
                return SingleProblem("execution.platform", "Операционная система", ActionOutcome.Unavailable,
                    "Требуется 64-разрядная Windows с .NET Framework 4.8. Настройки не изменялись.");
            try
            {
                var backend = new WindowsSecurityBackend();
                SecuritySnapshot initial = backend.Inspect();
                if (!initial.HasChanges) return initial.ToReport();
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    if (initial.RequiresElevation && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                        return SingleProblem("execution.elevation", "Права администратора", ActionOutcome.Blocked,
                            "Обнаружены включённые настройки, для изменения которых нужны права администратора. Изменения не начинались.");
                using (Mutex mutex = OpenActionMutex())
                {
                    bool acquired = false;
                    try
                    {
                        try { acquired = mutex.WaitOne(0); }
                        catch (AbandonedMutexException) { acquired = true; }
                        if (!acquired) return SingleProblem("execution.busy", "Выполнение настройки", ActionOutcome.Blocked,
                            "Другой экземпляр уже выполняет настройку. Новые изменения не начинались.");
                        // State can change between the UI probe, UAC and acquiring
                        // this mutex. Read it again before selecting any setters.
                        return ExecuteCatalogue(backend);
                    }
                    finally { if (acquired) mutex.ReleaseMutex(); }
                }
            }
            catch (Exception error)
            {
                return SingleProblem("execution.failure", "Выполнение настройки", ActionOutcome.Failed, Explain(error));
            }
        }

        public static SecuritySnapshot Inspect()
        {
            try { return new WindowsSecurityBackend().Inspect(); }
            catch (Exception error)
            {
                var snapshot = new SecuritySnapshot();
                foreach (SecurityAction action in Catalogue)
                    snapshot.Actions.Add(new ActionState { Id = action.Id, Title = action.Title,
                        State = ActionStateKind.Unknown, Reason = Explain(error), RequiresElevation = true });
                return snapshot;
            }
        }

        private static Mutex OpenActionMutex()
        {
            try { return Mutex.OpenExisting(MutexName, MutexRights.Synchronize | MutexRights.Modify); }
            catch (WaitHandleCannotBeOpenedException) { }
            var security = new MutexSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                MutexRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                MutexRights.FullControl, AccessControlType.Allow));
            bool created;
            try { return new Mutex(false, MutexName, out created, security); }
            catch (UnauthorizedAccessException) { return Mutex.OpenExisting(MutexName, MutexRights.Synchronize | MutexRights.Modify); }
        }

        internal static SecurityReport ExecuteCatalogue(ISecurityBackend backend)
        {
            if (backend == null) throw new ArgumentNullException("backend");
            var report = new SecurityReport();
            SecuritySnapshot snapshot = backend.Inspect();
            if (snapshot == null) return SingleProblem("execution.inspect", "Проверка состояния", ActionOutcome.Unverified,
                "Провайдер не вернул состояние. Изменения не начинались.");
            if (!snapshot.HasChanges) return snapshot.ToReport();
            foreach (SecurityAction action in Catalogue)
            {
                ActionState state = snapshot.Actions == null ? null : snapshot.Actions.Find(item => item != null && item.Id == action.Id);
                if (state != null && state.State == ActionStateKind.Unavailable)
                {
                    report.Results.Add(Result(action, ActionOutcome.Unavailable, state.Reason));
                    continue;
                }
                if (state == null || state.State == ActionStateKind.Unknown)
                {
                    report.Results.Add(Result(action, ActionOutcome.Unverified, state == null ? "Состояние действия не получено; изменение не выполнялось." : state.Reason));
                    continue;
                }
                if (state.State == ActionStateKind.AlreadyDesired)
                {
                    report.Results.Add(Result(action, ActionOutcome.AlreadySet, state.Reason));
                    continue;
                }
                try
                {
                    ActionResult result = backend.Execute(action);
                    if (result == null || result.Id != action.Id || !Enum.IsDefined(typeof(ActionOutcome), result.Outcome))
                        result = Result(action, ActionOutcome.Unverified, "Провайдер не вернул корректный результат этого действия.");
                    // A backend cannot change the identity displayed for a fixed action.
                    result.Title = action.Title;
                    report.Results.Add(result);
                }
                catch (Exception error)
                {
                    report.Results.Add(Result(action,
                        error is UnauthorizedAccessException || error is SecurityException ? ActionOutcome.Blocked : ActionOutcome.Failed,
                        Explain(error)));
                }
            }
            if (report.Results.Count == 0) return snapshot.ToReport();
            return report;
        }

        internal static ActionResult Result(SecurityAction action, ActionOutcome outcome, string reason,
            string before = null, string after = null)
        {
            return new ActionResult { Id = action.Id, Title = action.Title, Outcome = outcome,
                Reason = Limit(reason), Before = Limit(before), After = Limit(after) };
        }

        private static SecurityReport SingleProblem(string id, string title, ActionOutcome outcome, string reason)
        {
            var report = new SecurityReport();
            report.Results.Add(new ActionResult { Id = id, Title = title, Outcome = outcome, Reason = Limit(reason) });
            return report;
        }

        internal static string Explain(Exception error)
        {
            return Limit(error.Message + " (0x" + error.HResult.ToString("X8") + ").");
        }

        internal static string Limit(string value)
        {
            if (String.IsNullOrEmpty(value)) return value;
            return value.Length <= 1500 ? value : value.Substring(0, 1500) + "…";
        }
    }
}

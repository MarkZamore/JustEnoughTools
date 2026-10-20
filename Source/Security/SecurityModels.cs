using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;

namespace JustEnoughTools.Security
{
    [DataContract]
    public enum ActionStateKind
    {
        [EnumMember] AlreadyDesired,
        [EnumMember] NeedsChange,
        [EnumMember] Unavailable,
        [EnumMember] Unknown
    }

    [DataContract]
    public sealed class ActionState
    {
        [DataMember(Order = 1)] public string Id { get; set; }
        [DataMember(Order = 2)] public string Title { get; set; }
        [DataMember(Order = 3)] public ActionStateKind State { get; set; }
        [DataMember(Order = 4)] public string Reason { get; set; }
        [DataMember(Order = 5)] public bool RequiresElevation { get; set; }
    }

    [DataContract]
    public sealed class SecuritySnapshot
    {
        public SecuritySnapshot() { Actions = new List<ActionState>(); }
        [DataMember(Order = 1)] public List<ActionState> Actions { get; set; }
        public bool HasChanges { get { return IsWellFormed() && Actions.Any(item => item.State == ActionStateKind.NeedsChange); } }
        public bool HasUnknown { get { return !IsWellFormed() || Actions.Any(item => item.State == ActionStateKind.Unknown); } }
        public bool RequiresElevation { get { return IsWellFormed() && Actions.Any(item => item.State == ActionStateKind.NeedsChange && item.RequiresElevation); } }
        public bool IsComplete { get { return IsWellFormed() && !HasChanges && !HasUnknown; } }

        private bool IsWellFormed()
        {
            return Actions != null && Actions.Count == SecurityEngine.Catalogue.Count &&
                Actions.All(item => item != null && Enum.IsDefined(typeof(ActionStateKind), item.State)) &&
                Actions.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == Actions.Count &&
                SecurityEngine.Catalogue.All(action => Actions.Any(item => item.Id == action.Id));
        }

        public SecurityReport ToReport()
        {
            var report = new SecurityReport();
            if (!IsWellFormed())
            {
                report.Results.Add(new ActionResult { Id = "execution.inspect", Title = "Проверка состояния", Outcome = ActionOutcome.Unverified,
                    Reason = "Не получен полный корректный снимок доступных настроек. Изменения не начинались." +
                        (Actions == null ? "" : " " + String.Join(" ", Actions.Where(item => item != null && item.State == ActionStateKind.Unknown).Select(item => item.Reason).Take(1))) });
                return report;
            }
            foreach (ActionState item in Actions)
            {
                // Manual-only/unsupported switches are information, not a reason
                // to keep requesting UAC when all actionable settings are off.
                if (item != null && item.State == ActionStateKind.Unavailable) continue;
                report.Results.Add(item == null ? new ActionResult { Id = "execution.inspect", Title = "Проверка состояния",
                    Outcome = ActionOutcome.Unverified, Reason = "Отсутствует состояние действия." } : new ActionResult
                {
                    Id = item.Id, Title = item.Title,
                    Outcome = item.State == ActionStateKind.AlreadyDesired ? ActionOutcome.AlreadySet : ActionOutcome.Unverified,
                    Reason = item.Reason ?? "Состояние действия не подтверждено."
                });
            }
            if (report.Results.Count == 0)
                report.Results.Add(new ActionResult { Id = "execution.no-changes", Title = "Доступные настройки", Outcome = ActionOutcome.AlreadySet,
                    Reason = "Доступных изменений нет. Это не утверждение об отключении всех средств защиты Windows." });
            return report;
        }
    }

    [DataContract]
    public enum ActionOutcome
    {
        [EnumMember] Applied,
        [EnumMember] AlreadySet,
        [EnumMember] Blocked,
        [EnumMember] Unavailable,
        [EnumMember] Unverified,
        [EnumMember] Failed
    }

    [DataContract]
    public sealed class ActionResult
    {
        [DataMember(Order = 1)] public string Id { get; set; }
        [DataMember(Order = 2)] public string Title { get; set; }
        [DataMember(Order = 3)] public ActionOutcome Outcome { get; set; }
        [DataMember(Order = 4)] public string Reason { get; set; }
        [DataMember(Order = 5)] public string Before { get; set; }
        [DataMember(Order = 6)] public string After { get; set; }
    }

    [DataContract]
    public sealed class SecurityReport
    {
        public SecurityReport() { Results = new List<ActionResult>(); }
        [DataMember(Order = 1)] public List<ActionResult> Results { get; set; }
        public bool Succeeded
        {
            get
            {
                return Results != null && Results.Count > 0 && Results.All(result => result != null &&
                    (result.Outcome == ActionOutcome.Applied || result.Outcome == ActionOutcome.AlreadySet));
            }
        }

        public string FormatProblems()
        {
            if (Results == null || Results.Count == 0) return "Рабочий процесс не вернул результатов. Изменения не подтверждены.";
            int confirmed = Results.Count(item => item != null &&
                (item.Outcome == ActionOutcome.Applied || item.Outcome == ActionOutcome.AlreadySet));
            var text = new StringBuilder();
            text.Append("Подтверждено сейчас: ").Append(confirmed).Append(" из ").Append(Results.Count).AppendLine(".");
            foreach (ActionResult result in Results)
            {
                if (result == null) { text.AppendLine("• Отсутствует результат действия."); continue; }
                if (result.Outcome == ActionOutcome.Applied || result.Outcome == ActionOutcome.AlreadySet) continue;
                text.Append("• ").Append(result.Title ?? result.Id ?? "Действие").Append(": ")
                    .AppendLine(String.IsNullOrWhiteSpace(result.Reason) ? "Результат не подтверждён." : result.Reason);
            }
            return text.ToString().TrimEnd();
        }
    }
}

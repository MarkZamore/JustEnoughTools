using System;
using System.Collections.Generic;
using System.Linq;

namespace JustEnoughTools.Tweaks
{
    internal enum TweakId { Indexing = 1, BitLocker, Keyboard, StickyKeys, Clipboard, Mouse, Theme, Start, Taskbar, Clock, Battery, WindowsUpdate, Startup }
    internal enum TweakState { Pending, Applied, Unavailable, Unknown, Deferred, Running, Failed }
    internal enum TweakScope { User, Administrator }

    internal sealed class TweakPart
    {
        internal string Key, Reason;
        internal TweakState State;
        internal bool Administrator;
        internal TweakPart(string key, TweakState state, bool administrator = false, string reason = null)
        { Key = key; State = state; Administrator = administrator; Reason = reason; }
    }

    internal sealed class TweakRow
    {
        internal TweakId Id;
        internal List<TweakPart> Parts = new List<TweakPart>();
        internal bool CanApply { get { return Parts.Any(p => p.State == TweakState.Pending); } }
        internal bool NeedsAdministrator { get { return Parts.Any(p => p.State == TweakState.Pending && p.Administrator); } }
        internal string Details { get { return string.Join("\n", Parts.Where(p => !string.IsNullOrEmpty(p.Reason)).Select(p => p.Reason).Distinct()); } }
    }

    internal sealed class TweakSnapshot
    {
        internal readonly List<TweakRow> Rows = new List<TweakRow>();
        internal bool CanApply { get { return Rows.Any(r => r.CanApply); } }
    }

    internal static class TweakCatalog
    {
        internal static readonly string[] Titles = {
            "Отключить индексирование", "Отключить BitLocker", "Настроить клавиатуру",
            "Отключить залипание клавиш", "Включить историю буфера", "Отключить ускорение мыши",
            "Тема Windows (тёмная)", "Настроить меню Пуск", "Настроить панель задач",
            "Настроить часы", "Показывать заряд батареи", "Настроить Windows Update", "Отключить автозагрузку" };
        internal static readonly TweakId[] Order = { TweakId.Keyboard, TweakId.StickyKeys, TweakId.Clipboard,
            TweakId.Mouse, TweakId.Theme, TweakId.Start, TweakId.Taskbar, TweakId.Clock, TweakId.Battery,
            TweakId.Startup, TweakId.WindowsUpdate, TweakId.Indexing, TweakId.BitLocker };
        internal static IEnumerable<TweakId> All { get { return Enumerable.Range(1, Titles.Length).Select(n => (TweakId)n); } }
        internal static bool IsValid(TweakId id) { return id >= TweakId.Indexing && id <= TweakId.Startup; }
        internal static string Hint(TweakId id)
        {
            if (id == TweakId.Indexing) return "Диск C:. Для SSD лишнее — он и так быстрый. Недоступные файлы пропускаются.";
            if (id == TweakId.Mouse) return "Для шутеров. Скорость движения указателя сохраняется.";
            if (id == TweakId.WindowsUpdate) return "Настроить параметры и проверить обновления один раз за запуск. Без установки и перезагрузки.";
            if (id == TweakId.BitLocker) return "Полностью расшифровать C:. PowerShell показывает процент и закрывается по завершении.";
            return Titles[(int)id - 1];
        }
    }

    internal interface ITweakBackend
    {
        IReadOnlyList<TweakPart> Inspect(TweakId id);
        TweakPart Apply(TweakId id, string part);
    }

    // Inspection and execution are deliberately separate. Unknown and unsupported
    // parts never become commands or reasons to elevate.
    internal sealed class TweakEngine
    {
        readonly ITweakBackend backend;
        internal TweakEngine(ITweakBackend backend) { this.backend = backend; }
        internal TweakRow Inspect(TweakId id)
        {
            if (!TweakCatalog.IsValid(id)) throw new ArgumentOutOfRangeException(nameof(id));
            var row = new TweakRow { Id = id };
            try { row.Parts.AddRange(backend.Inspect(id)); }
            catch (Exception error) { row.Parts.Add(new TweakPart("inspection", TweakState.Unknown, reason: error.Message)); }
            if (row.Parts.Count == 0) row.Parts.Add(new TweakPart("unsupported", TweakState.Unavailable, reason: "Нет доступных действий."));
            return row;
        }
        internal TweakSnapshot InspectAll()
        {
            var snapshot = new TweakSnapshot();
            foreach (var id in TweakCatalog.All) snapshot.Rows.Add(Inspect(id));
            return snapshot;
        }
        internal List<TweakRow> Apply(IEnumerable<TweakId> requested, TweakScope scope)
        {
            var ids = new HashSet<TweakId>(requested);
            if (ids.Any(id => !TweakCatalog.IsValid(id))) throw new ArgumentOutOfRangeException(nameof(requested));
            var result = new List<TweakRow>();
            foreach (var id in TweakCatalog.Order.Where(ids.Contains))
            {
                var row = new TweakRow { Id = id };
                foreach (var part in Inspect(id).Parts)
                {
                    if (part.State != TweakState.Pending || part.Administrator != (scope == TweakScope.Administrator)) continue;
                    try { row.Parts.Add(backend.Apply(id, part.Key)); }
                    catch (Exception error) { row.Parts.Add(new TweakPart(part.Key, TweakState.Failed, part.Administrator, error.Message)); }
                }
                result.Add(row);
            }
            return result;
        }
    }
}

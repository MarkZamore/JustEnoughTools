using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JustEnoughTools.Updates;

namespace JustEnoughTools.Tweaks
{
    internal interface ITweakService
    {
        Task<TweakSnapshot> InspectAsync();
        Task<TweakRun> ApplyAsync(TweakId[] ids);
    }
    internal sealed class TweakRun
    {
        internal TweakSnapshot Snapshot;
        internal readonly List<TweakRow> Results = new List<TweakRow>();
        internal string Message;
        internal string Format()
        {
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(Message)) lines.Add(Message);
            foreach (var group in Results.Where(r => r.Parts.Count != 0).GroupBy(r => r.Id))
            {
                var parts = group.SelectMany(r => r.Parts).ToArray();
                var summary = new List<string>();
                int applied = parts.Count(p => p.State == TweakState.Applied), stored = parts.Count(p => p.State == TweakState.Deferred);
                if (applied != 0) summary.Add("применено: " + applied);
                if (stored != 0) summary.Add("сохранено: " + stored);
                if (parts.Any(p => p.State == TweakState.Running)) summary.Add("выполняется");
                foreach (var part in parts.Where(p => p.State == TweakState.Failed || p.Key == "indexing" || p.Key == "search-updates" || p.Key == "bitlocker"))
                    if (!string.IsNullOrWhiteSpace(part.Reason)) summary.Add(part.Reason);
                if (summary.Count == 0) summary.Add("изменения не требуются");
                lines.Add(TweakCatalog.Titles[(int)group.Key - 1] + ": " + string.Join("; ", summary) + ".");
            }
            return string.Join("\n", lines);
        }
    }
    internal sealed class TweakService : ITweakService
    {
        readonly TweakEngine engine;
        readonly Func<TweakId[], List<TweakRow>> elevate;
        readonly string executable;
        internal TweakService(string executable) : this(executable,
            new TweakEngine(new WindowsTweakBackend(new WindowsTweakPlatform())), ids => TweakWorker.Apply(ids)) { }
        internal TweakService(string executable, TweakEngine engine, Func<TweakId[], List<TweakRow>> elevate)
        { this.executable = executable; this.engine = engine; this.elevate = elevate; }
        public Task<TweakSnapshot> InspectAsync() { return Task.Run(() => engine.InspectAll()); }
        public Task<TweakRun> ApplyAsync(TweakId[] ids)
        {
            if (ids == null || ids.Length == 0 || ids.Length > 13 || ids.Distinct().Count() != ids.Length || ids.Any(id => !TweakCatalog.IsValid(id)))
                throw new ArgumentException("Некорректный список твиков.");
            return Task.Run(() =>
            {
                var run = new TweakRun();
                // Mutex ownership stays on this thread. The worker obtains the
                // same lease for its own modifications after this phase releases it.
                using (var lease = UpdateService.CreateOperationMutex(executable))
                {
                    bool owned;
                    try { owned = lease.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
                    if (!owned) throw new InvalidOperationException("Другая операция приложения ещё выполняется.");
                    try { using (TweakActionLease.Take()) run.Results.AddRange(engine.Apply(ids.Where(id => id != TweakId.WindowsUpdate), TweakScope.User)); }
                    finally { lease.ReleaseMutex(); }
                    var administrators = ids.Where(id => engine.Inspect(id).NeedsAdministrator).ToArray();
                    if (administrators.Length != 0)
                    {
                        try { run.Results.AddRange(elevate(administrators)); }
                        catch (Exception error) { run.Message = error.Message; }
                    }
                    try { owned = lease.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
                    if (!owned) throw new InvalidOperationException("Изменения ещё выполняются; повторная проверка отложена.");
                    try
                    {
                      using (TweakActionLease.Take())
                      {
                        if (ids.Contains(TweakId.WindowsUpdate)) run.Results.AddRange(engine.Apply(new[] { TweakId.WindowsUpdate }, TweakScope.User));
                        run.Snapshot = engine.InspectAll();
                        foreach (var result in run.Results)
                            foreach (var part in result.Parts.Where(p => p.State == TweakState.Deferred))
                            {
                                var stored = run.Snapshot.Rows.Single(r => r.Id == result.Id).Parts.FirstOrDefault(p => p.Key == part.Key && p.State == TweakState.Applied);
                                if (stored != null) { stored.State = TweakState.Deferred; stored.Reason = part.Reason; }
                            }
                      }
                    }
                    finally { lease.ReleaseMutex(); }
                }
                return run;
            });
        }
    }
}

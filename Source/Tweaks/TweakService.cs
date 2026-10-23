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
        bool HasForegroundWork { get; }
        event Action StateChanged;
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
                lines.Add(TweakCatalog.Titles[(int)group.Key - 1] + ": " + string.Join("; ", summary.Distinct()) + ".");
            }
            return string.Join("\n", lines);
        }
    }
    internal sealed class TweakService : ITweakService
    {
        readonly TweakEngine engine;
        readonly Func<TweakId[], Action, List<TweakRow>> elevate;
        readonly string executable;
        readonly object sync = new object();
        readonly HashSet<TweakId> running = new HashSet<TweakId>();
        int foregroundRuns;
        public bool HasForegroundWork { get { lock (sync) return foregroundRuns != 0; } }
        public event Action StateChanged;
        internal TweakService(string executable) : this(executable,
            new TweakEngine(new WindowsTweakBackend(new WindowsTweakPlatform())), (ids, started) => TweakWorker.Apply(ids, started)) { }
        internal TweakService(string executable, TweakEngine engine, Func<TweakId[], List<TweakRow>> elevate)
            : this(executable, engine, (ids, started) => elevate(ids)) { }
        internal TweakService(string executable, TweakEngine engine, Func<TweakId[], Action, List<TweakRow>> elevate)
        { this.executable = executable; this.engine = engine; this.elevate = elevate; }
        void NotifyStateChanged()
        {
            var changed = StateChanged;
            if (changed == null) return;
            foreach (Action subscriber in changed.GetInvocationList())
                try { subscriber(); } catch { }
        }
        TweakSnapshot InspectSnapshot()
        {
            var snapshot = engine.InspectAll();
            // Kernel leases survive the UI process. A newly opened window can
            // identify an existing worker without files, registry or polling.
            foreach (var row in snapshot.Rows)
                if (UpdateService.IsOperationScopeInProgress(executable, "Tweak" + (int)row.Id))
                    foreach (var part in row.Parts.Where(part => part.State == TweakState.Pending))
                    {
                        part.State = TweakState.Running;
                        part.Reason = "Операция уже выполняется.";
                    }
            return snapshot;
        }
        public Task<TweakSnapshot> InspectAsync() { return Task.Run(() => InspectSnapshot()); }
        public Task<TweakRun> ApplyAsync(TweakId[] ids)
        {
            if (ids == null || ids.Length == 0 || ids.Length > 13 || ids.Distinct().Count() != ids.Length || ids.Any(id => !TweakCatalog.IsValid(id)))
                throw new ArgumentException("Некорректный список твиков.");
            lock(sync)
            {
                if(ids.Any(running.Contains))throw new InvalidOperationException("Эта настройка уже выполняется.");
                foreach(var id in ids)running.Add(id);
                foregroundRuns++;
            }
            NotifyStateChanged();
            int foreground = 1;
            Action backgroundStarted = () =>
            {
                // Only an authenticated worker which already owns its leases
                // can detach this request from the lifetime of the UI process.
                if (Interlocked.Exchange(ref foreground, 0) == 0) return;
                lock (sync) foregroundRuns--;
                NotifyStateChanged();
            };
            return Task.Run(() =>
            {
                var run = new TweakRun();
                try
                {
                    // Independent rows take independent leases. User changes
                    // always execute here, never under another admin's HKCU.
                    foreach(var id in TweakCatalog.Order.Where(ids.Contains))
                    {
                        try
                        {
                            using(UpdateService.AcquireOperationLease(executable,"Tweak"+(int)id))
                            using(TweakActionLease.Take(id))
                                run.Results.AddRange(engine.Apply(new[]{id},TweakScope.User,true));
                        }
                        catch(Exception error)
                        {
                            run.Results.Add(new TweakRow {Id=id,Parts=new List<TweakPart>{new TweakPart("operation",TweakState.Failed,reason:error.Message)}});
                        }
                    }
                    var administrators=ids.Where(id=>engine.Inspect(id).NeedsAdministrator).ToArray();
                    if(administrators.Length!=0)
                    {
                        try {run.Results.AddRange(elevate(administrators, backgroundStarted));}
                        catch(Exception error){run.Message=error.Message;}
                    }
                    engine.RememberCompleted(run.Results);
                    run.Snapshot=InspectSnapshot();
                    foreach(var result in run.Results)
                        foreach(var part in result.Parts.Where(p=>p.State==TweakState.Deferred))
                        {
                            var stored=run.Snapshot.Rows.Single(r=>r.Id==result.Id).Parts.FirstOrDefault(p=>p.Key==part.Key&&p.State==TweakState.Applied);
                            if(stored!=null){stored.State=TweakState.Deferred;stored.Reason=part.Reason;}
                        }
                    return run;
                }
                finally
                {
                    backgroundStarted();
                    lock (sync) foreach (var id in ids) running.Remove(id);
                    NotifyStateChanged();
                }
            });
        }
    }
}

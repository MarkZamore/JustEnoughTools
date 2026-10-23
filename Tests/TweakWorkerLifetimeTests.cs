using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JustEnoughTools.Tweaks;

namespace JustEnoughTools.Tests
{
    public static class TweakWorkerLifetimeTests
    {
        static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        public static int RunAll()
        {
            int count = 0;
            TweakWorker.ValidateStarted(SecurityCoordinator.MessageKind.Accepted, TweakWorker.EncodeStarted()); count++;
            foreach (var bad in new[] { new byte[0], System.Text.Encoding.ASCII.GetBytes("JET-TWEAKS-1|STARTEDextra"), null })
            {
                bool rejected = false;
                try { TweakWorker.ValidateStarted(SecurityCoordinator.MessageKind.Accepted, bad); } catch (InvalidDataException) { rejected = true; }
                Require(rejected, "An invalid start acknowledgement must not permit UI detachment."); count++;
            }
            bool wrongKind = false;
            try { TweakWorker.ValidateStarted(SecurityCoordinator.MessageKind.Result, TweakWorker.EncodeStarted()); } catch (InvalidDataException) { wrongKind = true; }
            Require(wrongKind, "A result cannot impersonate the start acknowledgement."); count++;
            var elevated = SecurityCoordinator.CreateWorkerStartInfo(TestContext.ExecutablePath, "fixed", true, true);
            Require(!elevated.UseShellExecute && elevated.Verb == "" && elevated.CreateNoWindow, "An elevated UI starts a separate child with its token and no further UAC."); count++;
            var normal = SecurityCoordinator.CreateWorkerStartInfo(TestContext.ExecutablePath, "fixed", false, true);
            Require(normal.UseShellExecute && normal.Verb == "runas", "A standard UI requests one elevation for the child batch."); count++;
            var leases = new List<FakeLease>(); bool acknowledged = false; int actions = 0;
            var rows = TweakWorker.RunReservedBatch(new[] { TweakId.WindowsUpdate, TweakId.Indexing }, id =>
            {
                Require(!acknowledged, "Every lease is acquired before acknowledgement.");
                if (id == TweakId.Indexing) throw new InvalidOperationException("busy");
                var lease = new FakeLease(); leases.Add(lease); return lease;
            }, () => { Require(leases.Count == 1 && !leases[0].Disposed, "The worker owns eligible leases before acknowledging."); acknowledged = true; },
            (id, phase) => { Require(acknowledged && leases.All(l => !l.Disposed), "Work runs only after acknowledgement under persistent leases."); actions++; return Row(id); });
            Require(actions == 2 && rows.Any(r => r.Id == TweakId.Indexing && r.Parts.Any(p => p.State == TweakState.Failed)), "A reservation failure must preserve another row's preparation and install phases."); count++;
            Require(leases.All(l => l.Disposed), "All leases must be released on their acquiring thread after the batch."); count++;
            actions = 0; var abortedLease = new FakeLease(); bool failedAck = false;
            try { TweakWorker.RunReservedBatch(new[] { TweakId.WindowsUpdate }, id => abortedLease, () => { throw new IOException("parent left before acknowledgement"); },
                (id, phase) => { actions++; return Row(id); }); } catch (IOException) { failedAck = true; }
            Require(failedAck && actions == 0 && abortedLease.Disposed, "A failed start handshake cannot initiate changes."); count++;
            using (var acknowledgedEvent = new ManualResetEventSlim())
            using (var continueWork = new ManualResetEventSlim())
            {
                bool foregroundAlive = true; int phases = 0;
                var worker = Task.Run(() => TweakWorker.RunReservedBatch(new[] { TweakId.WindowsUpdate }, id => new FakeLease(),
                    () => { acknowledgedEvent.Set(); }, (id, phase) =>
                    {
                        if (!continueWork.Wait(10000)) throw new TimeoutException("Fake work not released.");
                        Require(!foregroundAlive, "This fake task must outlive the foreground endpoint.");
                        Interlocked.Increment(ref phases); return Row(id);
                    }));
                try
                {
                    Require(acknowledgedEvent.Wait(10000) && !worker.IsCompleted, "Foreground can close while the acknowledged worker is active."); count++;
                    foregroundAlive = false;
                }
                finally { continueWork.Set(); }
                Require(worker.GetAwaiter().GetResult().Count == 2 && phases == 2, "Losing the foreground after acknowledgement cannot cancel remaining worker phases."); count++;
            }
            TweakWorker.DeliverResult(() => { throw new IOException("parent pipe closed"); }); count++;
            TweakWorker.DeliverResult(() => { throw new ObjectDisposedException("parent pipe closed"); }); count++;
            bool delivered = false; TweakWorker.DeliverResult(() => delivered = true);
            Require(delivered, "A live foreground must still receive the final result."); count++;
            return count;
        }
        static List<TweakRow> Row(TweakId id) { return new List<TweakRow> { new TweakRow { Id = id, Parts = new List<TweakPart> { new TweakPart("fake", TweakState.Applied, true) } } }; }
        sealed class FakeLease : IDisposable
        {
            readonly int owner = Thread.CurrentThread.ManagedThreadId;
            internal bool Disposed;
            public void Dispose() { Require(owner == Thread.CurrentThread.ManagedThreadId, "Mutex leases must be released on the acquiring thread."); Disposed = true; }
        }
    }
}

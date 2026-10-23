using System;
using System.Collections.Generic;
using System.Threading;
using JustEnoughTools.Tweaks;

namespace JustEnoughTools.Tests
{
    public static class TweakDetachServiceTests
    {
        static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        public static int RunAll()
        {
            int checks = 0;
            using (var entered = new ManualResetEventSlim())
            using (var acknowledge = new ManualResetEventSlim())
            using (var acknowledged = new ManualResetEventSlim())
            using (var finish = new ManualResetEventSlim())
            {
                int events = 0;
                var service = new TweakService(TestContext.ExecutablePath, new TweakEngine(new Backend()), (ids, ready) =>
                {
                    entered.Set();
                    if (!acknowledge.Wait(15000)) throw new TimeoutException("Readiness was not released.");
                    ready(); ready(); // A transport retry must not release another operation's reservation.
                    acknowledged.Set();
                    if (!finish.Wait(15000)) throw new TimeoutException("Detached work was not released.");
                    return new List<TweakRow>();
                });
                service.StateChanged += () => Interlocked.Increment(ref events);
                var job = service.ApplyAsync(new[] { TweakId.Indexing });
                try
                {
                    Require(entered.Wait(10000) && service.HasForegroundWork, "Close must wait for authenticated worker readiness."); checks++;
                    acknowledge.Set();
                    Require(acknowledged.Wait(10000) && !service.HasForegroundWork && !job.IsCompleted,
                        "Acknowledged independent work must permit closing while it continues."); checks++;
                }
                finally { acknowledge.Set(); finish.Set(); job.GetAwaiter().GetResult(); }
                Require(!service.HasForegroundWork && Volatile.Read(ref events) >= 2,
                    "Readiness and completion must expose state changes without keeping a foreground reservation."); checks++;
            }
            using (var detachedEntered = new ManualResetEventSlim())
            using (var foregroundEntered = new ManualResetEventSlim())
            using (var acknowledgeSecond = new ManualResetEventSlim())
            using (var secondAcknowledged = new ManualResetEventSlim())
            using (var finishFirst = new ManualResetEventSlim())
            using (var finishSecond = new ManualResetEventSlim())
            {
                var service = new TweakService(TestContext.ExecutablePath, new TweakEngine(new Backend()), (ids, ready) =>
                {
                    if (ids[0] == TweakId.Indexing)
                    {
                        ready(); detachedEntered.Set();
                        if (!finishFirst.Wait(15000)) throw new TimeoutException("First work was not released.");
                    }
                    else
                    {
                        foregroundEntered.Set();
                        if (!acknowledgeSecond.Wait(15000)) throw new TimeoutException("Second readiness was not released.");
                        ready(); secondAcknowledged.Set();
                        if (!finishSecond.Wait(15000)) throw new TimeoutException("Second work was not released.");
                    }
                    return new List<TweakRow>();
                });
                var first = service.ApplyAsync(new[] { TweakId.Indexing });
                Require(detachedEntered.Wait(10000), "The first fake worker did not acknowledge readiness."); checks++;
                var second = service.ApplyAsync(new[] { TweakId.WindowsUpdate });
                try
                {
                    Require(foregroundEntered.Wait(10000) && service.HasForegroundWork,
                        "Detached work must not release another action's foreground reservation."); checks++;
                    finishFirst.Set(); first.GetAwaiter().GetResult();
                    Require(service.HasForegroundWork && !second.IsCompleted,
                        "Completing a detached action must not permit close before another worker acknowledges."); checks++;
                    acknowledgeSecond.Set();
                    Require(secondAcknowledged.Wait(10000) && !service.HasForegroundWork && !second.IsCompleted,
                        "The final readiness acknowledgement must release only its own foreground reservation."); checks++;
                }
                finally { finishFirst.Set(); acknowledgeSecond.Set(); finishSecond.Set(); first.GetAwaiter().GetResult(); second.GetAwaiter().GetResult(); }
            }
            var failing = new TweakService(TestContext.ExecutablePath, new TweakEngine(new Backend()), (ids, ready) =>
            { throw new InvalidOperationException("Fake launch failure"); });
            var failure = failing.ApplyAsync(new[] { TweakId.Indexing }).GetAwaiter().GetResult();
            Require(!failing.HasForegroundWork && !string.IsNullOrEmpty(failure.Message),
                "A failed worker launch must release its reservation and preserve the failure result."); checks++;
            return checks;
        }
        sealed class Backend : ITweakBackend
        {
            public IReadOnlyList<TweakPart> Inspect(TweakId id)
            { return new[] { new TweakPart(id == TweakId.WindowsUpdate ? "search-updates" : "test", TweakState.Pending, id == TweakId.Indexing || id == TweakId.WindowsUpdate) }; }
            public TweakPart Apply(TweakId id, string part) { return new TweakPart(part, TweakState.Applied); }
        }
    }
}

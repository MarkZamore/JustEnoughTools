using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JustEnoughTools.Tweaks;

namespace JustEnoughTools.Tests
{
    public static class TweakConcurrencyTests
    {
        static void Require(bool condition,string reason){if(!condition)throw new InvalidOperationException(reason);}
        public static int RunAll()
        {
            int checks=0;
            using(TweakActionLease.Take(TweakId.Indexing))
                Require(Task.Run(()=>{using(TweakActionLease.Take(TweakId.Keyboard))return true;}).GetAwaiter().GetResult(),"A long disk operation must not exclude another tweak.");
            checks++;
            using(var started=new CountdownEvent(3))
            using(var release=new ManualResetEventSlim())
            {
                var order=new List<string>();
                var batch=Task.Run(()=>TweakWorker.RunBatch(new[]{TweakId.Indexing,TweakId.BitLocker,TweakId.WindowsUpdate,TweakId.Startup},(id,phase)=>
                {
                    lock(order)order.Add(id+":"+phase);
                    if(id!=TweakId.Startup&&phase!=TweakWorker.Phase.PrepareUpdates)
                    {
                        started.Signal();
                        if(!release.Wait(15000))throw new TimeoutException("Test jobs did not release.");
                    }
                    if(id==TweakId.BitLocker)throw new InvalidOperationException("one job failed");
                    return new List<TweakRow>{new TweakRow{Id=id,Parts=new List<TweakPart>{new TweakPart("test-"+phase,TweakState.Applied,true)}}};
                }));
                bool concurrent;
                try{concurrent=started.Wait(10000);}finally{release.Set();}
                var rows=batch.GetAwaiter().GetResult();
                Require(concurrent,"Updates, indexing and decryption must start without waiting for each other to finish.");checks++;
                Require(order[0]=="Startup:All"&&order[1]=="WindowsUpdate:PrepareUpdates","Quick machine preferences must finish before long tasks start.");checks++;
                Require(rows.Any(r=>r.Id==TweakId.BitLocker&&r.Parts.Any(p=>p.State==TweakState.Failed))&&
                    rows.Any(r=>r.Id==TweakId.Indexing&&r.Parts.Any(p=>p.State==TweakState.Applied))&&
                    rows.Any(r=>r.Id==TweakId.WindowsUpdate&&r.Parts.Any(p=>p.State==TweakState.Applied)),"One failed long task must preserve independent task results.");checks++;
            }
            using(var entered=new ManualResetEventSlim())
            using(var release=new ManualResetEventSlim())
            {
                var service=new TweakService(TestContext.ExecutablePath,new TweakEngine(new Backend()),ids=>
                {
                    entered.Set();if(!release.Wait(15000))throw new TimeoutException("Fake administrative task timed out.");
                    return new List<TweakRow>();
                });
                var disk=service.ApplyAsync(new[]{TweakId.Indexing});
                try
                {
                    Require(entered.Wait(10000),"The administrative batch must start.");checks++;
                    bool duplicateRejected=false;
                    try{service.ApplyAsync(new[]{TweakId.Indexing});}catch(InvalidOperationException){duplicateRejected=true;}
                    Require(duplicateRejected,"A running tweak must reject duplicate application.");checks++;
                    var keyboard=service.ApplyAsync(new[]{TweakId.Keyboard});
                    Require(keyboard.Wait(10000)&&keyboard.Result.Snapshot!=null&&!disk.IsCompleted,"Another row must finish while a long operation is still active.");checks++;
                }
                finally{release.Set();disk.GetAwaiter().GetResult();}
                Require(service.ApplyAsync(new[]{TweakId.Keyboard}).GetAwaiter().GetResult().Snapshot!=null,"Completed rows must release their reservations.");checks++;
            }
            var engine=new TweakEngine(new Backend());
            engine.RememberCompleted(new[]{new TweakRow{Id=TweakId.WindowsUpdate,Parts=new List<TweakPart>{new TweakPart("search-updates",TweakState.Applied,true)}}});
            Require(engine.Inspect(TweakId.WindowsUpdate).Parts.Single(p=>p.Key=="search-updates").State==TweakState.Applied,"An elevated successful update run must be remembered in the original UI process.");checks++;
            var failedEngine=new TweakEngine(new Backend());
            failedEngine.RememberCompleted(new[]{new TweakRow{Id=TweakId.WindowsUpdate,Parts=new List<TweakPart>{new TweakPart("search-updates",TweakState.Failed,true)}}});
            Require(failedEngine.Inspect(TweakId.WindowsUpdate).CanApply,"A failed update run must remain retryable.");checks++;
            return checks;
        }
        sealed class Backend:ITweakBackend
        {
            public IReadOnlyList<TweakPart> Inspect(TweakId id){return new[]{new TweakPart(id==TweakId.WindowsUpdate?"search-updates":"test",TweakState.Pending,id==TweakId.Indexing||id==TweakId.WindowsUpdate)};}
            public TweakPart Apply(TweakId id,string part){return new TweakPart(part,TweakState.Applied);}
        }
    }
}

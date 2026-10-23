using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using WindowsPreferences;
using JustEnoughTools.Actions;
using JustEnoughTools.Export;

namespace JustEnoughTools.Tests
{
    public static class TaskbarTests
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
        public static int RunAll()
        {
            int checks = 0;
            var choices=new[]{Choice(0),Choice(1),Choice(2)};
            Require(NativeWindowsPreferences.IsConfirmedTaskbarMode(0,Choice(0),choices),"The native value must confirm the first broker transition.");checks++;
            Require(!NativeWindowsPreferences.IsConfirmedTaskbarMode(2,Choice(0),choices),"An optimistic broker cache must not count as native application.");checks++;
            foreach(object invalid in new object[]{null,"0",-1,3}) {
                Require(!NativeWindowsPreferences.IsConfirmedTaskbarMode(invalid,Choice(0),choices),"Unknown or malformed native state cannot confirm a broker write.");checks++;
            }
            Require(!NativeWindowsPreferences.IsConfirmedTaskbarMode(0,Choice(0),new[]{Choice(0),Choice(0),Choice(2)}),"Ambiguous choice mappings cannot confirm a native state.");checks++;
            foreach (int initial in new[] { 0, 1, 2 })
            {
                var backend = new Fake { Value = Choice(initial) };
                Require(TaskbarRepair.Apply(backend).Succeeded && Object.Equals(backend.Value, Choice(2)) &&
                    String.Join(",", backend.Writes) == "0,2" && backend.Reads >= 3,
                    "The repair must toggle all taskbars then the fixed final mode, even when that mode was already set."); checks++;
                byte[] image = PortableResources.CreateExecutable(EmbeddedAssets.Read("RunnerTemplate"),
                    EmbeddedAssets.Read("TaskbarIcon"), FixedAction.TaskbarMode2);
                Require(PortableResources.ReadAction(image) == FixedAction.TaskbarMode2,
                    "The fixed action must survive PE icon/resource rewriting without a sidecar."); checks++;
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "JET.Action." + Guid.NewGuid().ToString("N") + ".exe");
                try
                {
                    System.IO.File.WriteAllBytes(path, image);
                    // Data/image-resource mapping never executes the PE entrypoint.
                    IntPtr module = LoadLibraryEx(path, IntPtr.Zero, 0x22);
                    if (module == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
                    try { Require(FixedActionResource.Read(module) == FixedAction.TaskbarMode2, "Windows must load the same fixed action that the managed PE parser checked."); checks++; }
                    finally { FreeLibrary(module); }
                }
                finally { System.IO.File.Delete(path); }
            }
            foreach (int failureAt in new[] { 1, 2 })
            {
                var backend = new Fake { Value = Choice(1), FailSetAt = failureAt };
                Require(!TaskbarRepair.Apply(backend).Succeeded && Equals(backend.Value,Choice(1)) && backend.Writes.Count==failureAt+1,
                    "A failed broker setter must restore and verify the original mode.");checks++;
            }
            var ignored = new Fake {Value=Choice(1),IgnoreFinal=true};
            Require(!TaskbarRepair.Apply(ignored).Succeeded && Equals(ignored.Value,Choice(1)) && String.Join(",",ignored.Writes)=="0,2,1",
                "An ignored final write must fail and restore the original mode.");checks++;
            foreach(var backend in new[] {
                new Fake{Value=Choice(2),Applicable=false},new Fake{Value=Choice(2),Enabled=false},new Fake{Value=Choice(2),Policy=true},new Fake{Value=Choice(2),Updating=true},
                new Fake{Value="2"},new Fake{Value=3},new Fake{Value=-1},new Fake{Value=null},new Fake{Value=Choice(2),Choices=new string[0]},
                new Fake{Value=Choice(2),Choices=new[]{Choice(0),Choice(1),Choice(1)}} }) {
                Require(!TaskbarRepair.Apply(backend).Succeeded&&backend.Writes.Count==0,"Unknown, unavailable, blocked and malformed models must not be changed.");checks++;
            }
            var blockedDuringRepair=new Fake{Value=Choice(1),PolicyAfterFirst=true};
            Require(!TaskbarRepair.Apply(blockedDuringRepair).Succeeded&&blockedDuringRepair.Writes.Count==1,
                "A policy introduced during execution must stop subsequent writes and rollback.");checks++;
            var failedRead=new Fake{Value=Choice(1),FailRead=true};
            Require(!TaskbarRepair.Apply(failedRead).Succeeded&&failedRead.Writes.Count==0,"Failed initial discovery must not mutate.");checks++;
            var ignoredFirst=new Fake{Value=Choice(2),IgnoreFirst=true};
            Require(!TaskbarRepair.Apply(ignoredFirst).Succeeded&&String.Join(",",ignoredFirst.Writes)=="0,2"&&Equals(ignoredFirst.Value,Choice(2)),
                "The final transition must not run without confirmed intermediate mode; only rollback may follow.");checks++;
            foreach (FixedAction action in new[] { FixedAction.Security, FixedAction.TaskbarMode1, FixedAction.TaskbarMode2 })
            {
                Require(FixedActionResource.Decode(FixedActionResource.Encode(action)) == action, "Action codec mismatch."); checks++;
            }
            bool rejected = false;
            try { FixedActionResource.Decode(new byte[] { 74,69,84,49,3,0,0,0 }); } catch (System.IO.InvalidDataException) { rejected = true; }
            Require(rejected, "Unknown resource actions must be rejected before execution."); checks++;
            return checks;
        }
        static string Choice(int index) {return new[]{"all","main","window"}[index];}
        static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        sealed class Fake : IWindowsPreferenceSession
        {
            internal object Value;
            internal string[] Choices=new[]{Choice(0),Choice(1),Choice(2)};
            internal bool Applicable=true,Enabled=true,Policy,Updating,IgnoreFinal,IgnoreFirst,PolicyAfterFirst,FailRead;
            internal int FailSetAt,Reads;
            internal readonly List<int> Writes=new List<int>();
            public WindowsPreferenceSnapshot Read() {
                Reads++;if(FailRead)throw new InvalidOperationException("read failed");
                return new WindowsPreferenceSnapshot(Value,Applicable,Enabled,Policy||PolicyAfterFirst&&Writes.Count>0,Updating,Choices);
            }
            public void Set(object value) {
                Writes.Add(Array.IndexOf(Choices,(string)value));
                if(Writes.Count==FailSetAt)throw new InvalidOperationException("set failed");
                if(IgnoreFirst&&Writes.Count==1||IgnoreFinal&&Writes.Count==2)return;
                Value=value;
            }
            public void Dispose() { }
        }
    }
}

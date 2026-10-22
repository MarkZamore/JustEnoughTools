using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;
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
            foreach (int initial in new[] { 0, 1, 2 })
            {
                var backend = new Fake { Value = initial };
                Require(TaskbarRepair.Apply(backend).Succeeded && Object.Equals(backend.Value, 2) &&
                    String.Join(",", backend.Writes) == "0,2" && backend.Notifications == 2 && backend.Pauses == 1,
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
                var backend = new Fake { Value = "old", Kind = RegistryValueKind.String, FailNotification = failureAt };
                Require(!TaskbarRepair.Apply(backend).Succeeded && Object.Equals(backend.Value, "old") &&
                    backend.Kind == RegistryValueKind.String, "A failed notification must restore the previous value and registry type."); checks++;
            }
            var missing = new Fake { Value = null, FailNotification = 1 };
            Require(!TaskbarRepair.Apply(missing).Succeeded && missing.Value == null && missing.Deleted,
                "Rollback must delete a value that did not originally exist."); checks++;
            var blocked = new Fake { Value = 1, BlockReadback = true };
            Require(!TaskbarRepair.Apply(blocked).Succeeded && Object.Equals(blocked.Value, 1),
                "A readback mismatch must fail and restore the original mode."); checks++;
            foreach (FixedAction action in new[] { FixedAction.Security, FixedAction.TaskbarMode1, FixedAction.TaskbarMode2 })
            {
                Require(FixedActionResource.Decode(FixedActionResource.Encode(action)) == action, "Action codec mismatch."); checks++;
            }
            bool rejected = false;
            try { FixedActionResource.Decode(new byte[] { 74,69,84,49,3,0,0,0 }); } catch (System.IO.InvalidDataException) { rejected = true; }
            Require(rejected, "Unknown resource actions must be rejected before execution."); checks++;
            return checks;
        }
        static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        sealed class Fake : ITaskbarSettings
        {
            internal object Value;
            internal RegistryValueKind Kind = RegistryValueKind.DWord;
            internal readonly List<string> Writes = new List<string>();
            internal int Notifications, Pauses, FailNotification;
            internal bool Deleted, BlockReadback;
            public object Read(out RegistryValueKind kind) { kind = Kind; return BlockReadback && Writes.Count == 2 ? (object)0 : Value; }
            public void Write(object value, RegistryValueKind kind) { Writes.Add(Convert.ToString(value)); Value = value; Kind = kind; }
            public void Delete() { Value = null; Deleted = true; }
            public bool Notify() { return ++Notifications != FailNotification; }
            public void Pause() { Pauses++; }
            public void Dispose() { }
        }
    }
}

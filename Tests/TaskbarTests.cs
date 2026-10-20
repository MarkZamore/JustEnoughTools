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
            foreach (int mode in new[] { 1, 2 })
            {
                var backend = new Fake { Value = 2 };
                Require(TaskbarRepair.Apply(mode, backend).Succeeded && Object.Equals(backend.Value, mode) &&
                    String.Join(",", backend.Writes) == "0," + mode && backend.Notifications == 2 && backend.Pauses == 1,
                    "The repair must toggle all taskbars then the exact selected mode, even when that mode was already set."); checks++;
                byte[] image = PortableResources.CreateExecutable(EmbeddedAssets.Read("RunnerTemplate"),
                    EmbeddedAssets.Read("TaskbarIcon"), (FixedAction)mode);
                Require(PortableResources.ReadAction(image) == (FixedAction)mode,
                    "The selected mode must survive PE icon/resource rewriting without a sidecar."); checks++;
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "JET.Action." + Guid.NewGuid().ToString("N") + ".exe");
                try
                {
                    System.IO.File.WriteAllBytes(path, image);
                    // Data/image-resource mapping never executes the PE entrypoint.
                    IntPtr module = LoadLibraryEx(path, IntPtr.Zero, 0x22);
                    if (module == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
                    try { Require(FixedActionResource.Read(module) == (FixedAction)mode, "Windows must load the same fixed action that the managed PE parser checked."); checks++; }
                    finally { FreeLibrary(module); }
                }
                finally { System.IO.File.Delete(path); }
            }
            foreach (int failureAt in new[] { 1, 2 })
            {
                var backend = new Fake { Value = "old", Kind = RegistryValueKind.String, FailNotification = failureAt };
                Require(!TaskbarRepair.Apply(2, backend).Succeeded && Object.Equals(backend.Value, "old") &&
                    backend.Kind == RegistryValueKind.String, "A failed notification must restore the previous value and registry type."); checks++;
            }
            var missing = new Fake { Value = null, FailNotification = 1 };
            Require(!TaskbarRepair.Apply(1, missing).Succeeded && missing.Value == null && missing.Deleted,
                "Rollback must delete a value that did not originally exist."); checks++;
            var blocked = new Fake { Value = 1, BlockReadback = true };
            Require(!TaskbarRepair.Apply(2, blocked).Succeeded && Object.Equals(blocked.Value, 1),
                "A readback mismatch must fail and restore the original mode."); checks++;
            var invalid = new Fake();
            Require(!TaskbarRepair.Apply(0, invalid).Succeeded && invalid.Writes.Count == 0,
                "No unchanged mode or unspecified action may write settings."); checks++;
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

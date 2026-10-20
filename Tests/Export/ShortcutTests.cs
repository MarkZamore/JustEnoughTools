using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using JustEnoughTools.Export;

namespace JustEnoughTools.Tests
{
    public static class ShortcutTests
    {
        private static int checks;
        private static readonly Guid ShellLinkClass = new Guid("00021401-0000-0000-C000-000000000046");
        [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
        [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant
        { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Text; }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint count); void GetAt(uint index, out PropertyKey key);
            void GetValue(ref PropertyKey key, out PropVariant value); void SetValue(ref PropertyKey key, ref PropVariant value); void Commit();
        }
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);

        public static int RunAll()
        {
            checks = 0;
            string root = Path.Combine(Path.GetTempPath(), "JustEnoughTools.ShortcutTests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string executable = Path.Combine(root, "Action.exe"), folder = Path.Combine(root, "ProgramsFixture");
                File.WriteAllBytes(executable, new byte[] { 77, 90, 1 });
                Directory.CreateDirectory(folder);
                string foreign = Path.Combine(folder, "Action.lnk"); File.WriteAllText(foreign, "foreign shortcut");
                string link = StartMenuShortcuts.CreateInFolder(executable, folder);
                Require(link != foreign && File.ReadAllText(foreign) == "foreign shortcut", "Export overwrote a foreign shortcut name.");
                Require(StartMenuShortcuts.CreateInFolder(executable, folder) == link && Directory.GetFiles(folder).Length == 2, "Repeated export duplicated its own shortcut.");
                Edit(link, "SetWorkingDirectory", new object[] { root });
                Edit(link, "SetShowCmd", new object[] { 7 });
                Edit(link, "SetHotkey", new object[] { (short)321 });
                string customIcon = Path.Combine(root, "User.ico");
                Edit(link, "SetIconLocation", new object[] { customIcon, 0 });
                AppId(link, "JustEnoughTools.ShortcutRegression");
                StartMenuShortcuts.CreateInFolder(executable, folder);
                Require(Text(link, "GetWorkingDirectory") == root && AppId(link, null) == "JustEnoughTools.ShortcutRegression", "Updating an owned link lost its working directory or AUMID.");
                var icon = new StringBuilder(32768);
                Read(link, "GetIconLocation", new object[] { icon, icon.Capacity, 0 });
                Require(icon.ToString() == customIcon, "Updating a link replaced its user-selected external icon.");
                Require((short)Read(link, "GetHotkey", new object[] { (short)0 })[0] == 321 && (int)Read(link, "GetShowCmd", new object[] { 0 })[0] == 7, "Updating an owned link lost launch properties.");

                // Relocate by exact bytes on the SAME volume, with native Shell
                // tracking disabled so the formerly skipped fallback is exercised.
                string relocation = Path.Combine(root, "Relocation"); Directory.CreateDirectory(relocation);
                string before = Path.Combine(relocation, "Before.exe"), after = Path.Combine(relocation, "After.exe");
                File.WriteAllBytes(before, new byte[] { 77, 90, 2 });
                string repairFolder = Path.Combine(relocation, "Links"), repairLink = StartMenuShortcuts.CreateInFolder(before, repairFolder);
                File.Copy(before, after); File.Delete(before);
                var sameVolume = new StartMenuShortcuts.SearchContext(new[] { relocation }) { UseNativeTracking = false };
                Require(StartMenuShortcuts.CleanupInFolder(repairFolder, false, path => { throw new InvalidOperationException("Repair must happen before unpin."); }, sameVolume) == 0,
                    "Relocation incorrectly removed its shortcut.");
                Require(Target(repairLink) == after, "The same-volume fingerprint fallback failed to repair a moved target.");

                string ambiguity = Path.Combine(root, "Ambiguity"); Directory.CreateDirectory(ambiguity);
                string missing = Path.Combine(ambiguity, "Missing.exe"); File.WriteAllBytes(missing, new byte[] { 77, 90, 3 });
                string ambiguousFolder = Path.Combine(ambiguity, "Links"), ambiguousLink = StartMenuShortcuts.CreateInFolder(missing, ambiguousFolder);
                File.Copy(missing, Path.Combine(ambiguity, "First.exe")); File.Copy(missing, Path.Combine(ambiguity, "Second.exe")); File.Delete(missing);
                int unpinCalls = 0;
                Require(StartMenuShortcuts.CleanupInFolder(ambiguousFolder, false, path => { unpinCalls++; return true; },
                    new StartMenuShortcuts.SearchContext(new[] { ambiguity }) { UseNativeTracking = false }) == 0 && File.Exists(ambiguousLink) && unpinCalls == 0,
                    "Ambiguous copies must retain the original link.");

                string cancelled = Path.Combine(root, "Cancelled"); Directory.CreateDirectory(cancelled);
                string removed = Path.Combine(cancelled, "Deleted.exe"); File.WriteAllBytes(removed, new byte[] { 77, 90, 4 });
                string cancelledFolder = Path.Combine(cancelled, "Links"), cancelledLink = StartMenuShortcuts.CreateInFolder(removed, cancelledFolder); File.Delete(removed);
                var context = new StartMenuShortcuts.SearchContext(new[] { cancelled }) { UseNativeTracking = false, CancelRequested = () => true };
                Require(StartMenuShortcuts.CleanupInFolder(cancelledFolder, false, path => { unpinCalls++; return true; }, context) == 0 && File.Exists(cancelledLink), "A cancelled search deleted a link.");
                int unpinObserved = 0;
                Require(StartMenuShortcuts.CleanupInFolder(cancelledFolder, false, path => { Require(File.Exists(path), "Link was deleted before unpin."); unpinObserved++; return false; },
                    new StartMenuShortcuts.SearchContext(new[] { cancelled }) { UseNativeTracking = false }) == 0 && File.Exists(cancelledLink) && unpinObserved == 1,
                    "A failed unpin must preserve the shortcut.");
                Require(StartMenuShortcuts.CleanupInFolder(cancelledFolder, false, path => true,
                    new StartMenuShortcuts.SearchContext(new[] { cancelled }) { UseNativeTracking = false }) == 1 && !File.Exists(cancelledLink), "A conclusively stale owned shortcut was not cleaned up.");

                string incompleteTarget = Path.Combine(cancelled, "Incomplete.exe"); File.WriteAllBytes(incompleteTarget, new byte[] { 77, 90, 5 });
                string incompleteLink = StartMenuShortcuts.CreateInFolder(incompleteTarget, cancelledFolder); File.Delete(incompleteTarget);
                Require(StartMenuShortcuts.CleanupInFolder(cancelledFolder, false, path => { throw new InvalidOperationException("Incomplete search must not unpin."); },
                    new StartMenuShortcuts.SearchContext(new[] { Path.Combine(root, "UnavailableRoot") }) { UseNativeTracking = false }) == 0 && File.Exists(incompleteLink),
                    "An incomplete search deleted a shortcut.");
            }
            finally
            {
                string resolved = Path.GetFullPath(root);
                if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(resolved).StartsWith("JustEnoughTools.ShortcutTests.", StringComparison.Ordinal))
                    throw new InvalidOperationException("Unexpected shortcut fixture cleanup path.");
                Directory.Delete(resolved, true);
            }
            return checks;
        }

        private static object[] Read(string path, string method, object[] values)
        {
            object link = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClass));
            try
            {
                ((IPersistFile)link).Load(path, 0);
                typeof(StartMenuShortcuts).GetNestedType("IShellLink", BindingFlags.NonPublic).GetMethod(method).Invoke(link, values);
                return values;
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }
        private static void Edit(string path, string method, object[] values)
        {
            object link = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClass));
            try
            {
                ((IPersistFile)link).Load(path, 2);
                typeof(StartMenuShortcuts).GetNestedType("IShellLink", BindingFlags.NonPublic).GetMethod(method).Invoke(link, values);
                ((IPersistFile)link).Save(path, true);
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }
        private static string Text(string path, string method)
        {
            var value = new StringBuilder(32768); Read(path, method, new object[] { value, value.Capacity }); return value.ToString();
        }
        private static string Target(string path)
        {
            var value = new StringBuilder(32768); Read(path, "GetPath", new object[] { value, value.Capacity, IntPtr.Zero, (uint)4 }); return value.ToString();
        }
        private static string AppId(string path, string set)
        {
            object link = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClass));
            try
            {
                ((IPersistFile)link).Load(path, set == null ? 0 : 2);
                var store = (IPropertyStore)link;
                var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
                if (set != null)
                {
                    var input = new PropVariant { Type = 31, Text = Marshal.StringToCoTaskMemUni(set) };
                    try { store.SetValue(ref key, ref input); store.Commit(); ((IPersistFile)link).Save(path, true); }
                    finally { PropVariantClear(ref input); }
                }
                PropVariant result; store.GetValue(ref key, out result);
                try { return result.Type == 31 ? Marshal.PtrToStringUni(result.Text) : null; }
                finally { PropVariantClear(ref result); }
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }
        private static void Require(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
    }
}

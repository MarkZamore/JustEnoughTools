using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml;

namespace JustEnoughTools.Tests
{
    // This suite observes only an isolated application directory. It does not
    // claim to replace a whole-system file/registry trace of a clean guest.
    public static class PortabilityTests
    {
        public static int RunAll()
        {
            string executable = TestContext.ExecutablePath;
            CheckPeAndManifest(executable);
            CheckStandaloneProbe(executable);
            return 12;
        }

        private static void CheckPeAndManifest(string path)
        {
            using (var input = File.OpenRead(path))
            using (var reader = new BinaryReader(input))
            {
                Require(reader.ReadUInt16() == 0x5a4d, "Portable application has no MZ header.");
                input.Position = 0x3c;
                int pe = reader.ReadInt32();
                Require(pe >= 0x40 && pe < input.Length - 24, "Portable application has invalid PE bounds.");
                input.Position = pe;
                Require(reader.ReadUInt32() == 0x4550 && reader.ReadUInt16() == 0x8664,
                    "Portable application must be an x64 PE.");
            }

            string manifest = ReadManifest(path);
            var document = new XmlDocument { XmlResolver = null };
            document.LoadXml(manifest);
            var names = new XmlNamespaceManager(document.NameTable);
            names.AddNamespace("asm3", "urn:schemas-microsoft-com:asm.v3");
            names.AddNamespace("dpi", "http://schemas.microsoft.com/SMI/2016/WindowsSettings");
            var level = document.SelectSingleNode("//asm3:requestedExecutionLevel", names) as XmlElement;
            Require(level != null && level.GetAttribute("level") == "asInvoker" && level.GetAttribute("uiAccess") == "false",
                "Ordinary launch must be asInvoker without UIAccess.");
            var dpi = document.SelectSingleNode("//dpi:dpiAwareness", names);
            Require(dpi != null && dpi.InnerText.Trim() == "PerMonitorV2", "Manifest must preserve PerMonitorV2 DPI awareness.");
        }

        private static void CheckStandaloneProbe(string original)
        {
            string artifacts = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "Artifacts", "Portability"));
            string session = Path.Combine(artifacts, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(session);
            string copy = Path.Combine(session, "JustEnoughTools.exe");
            File.Copy(original, copy, false);
            var events = new List<string>();
            object gate = new object();
            int overflows = 0;
            try
            {
                Require(Directory.GetFileSystemEntries(session).Length == 1, "Standalone fixture must start with exactly one EXE.");
                byte[] before = File.ReadAllBytes(copy);
                using (var watcher = new FileSystemWatcher(session))
                {
                    watcher.IncludeSubdirectories = true;
                    watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                        NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes;
                    watcher.InternalBufferSize = 65536;
                    FileSystemEventHandler change = (sender, args) =>
                    {
                        lock (gate) events.Add(args.ChangeType + ":" + args.Name);
                    };
                    watcher.Created += change;
                    watcher.Changed += change;
                    watcher.Deleted += change;
                    watcher.Renamed += (sender, args) => { lock (gate) events.Add("Renamed:" + args.Name); };
                    watcher.Error += (sender, args) => Interlocked.Increment(ref overflows);
                    watcher.EnableRaisingEvents = true;

                    var start = new ProcessStartInfo(copy, "--self-test")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = session
                    };
                    // These changes affect only the child. A framework EXE must
                    // not need a dotnet host, developer probing path or sidecar.
                    start.EnvironmentVariables.Remove("DEVPATH");
                    start.EnvironmentVariables.Remove("APP_CONFIG_FILE");
                    start.EnvironmentVariables.Remove("COMPLUS_Version");
                    start.EnvironmentVariables["DOTNET_ROOT"] = Path.Combine(session, "dotnet-not-installed");
                    start.EnvironmentVariables["DOTNET_MULTILEVEL_LOOKUP"] = "0";
                    start.EnvironmentVariables["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    using (var readOnly = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var process = Process.Start(start))
                    {
                        Require(process != null, "Standalone probe process did not start.");
                        if (!process.WaitForExit(30000))
                        {
                            process.Kill();
                            throw new InvalidOperationException("Standalone portability probe timed out.");
                        }
                        string output = process.StandardOutput.ReadToEnd();
                        string error = process.StandardError.ReadToEnd();
                        Require(process.ExitCode == 0 && output.Contains("Embedded dependencies OK"),
                            "Standalone EXE failed without companion DLL/config/runtime: " + error + output);
                    }
                    // Allow the watcher to deliver already-queued notifications.
                    Thread.Sleep(100);
                    watcher.EnableRaisingEvents = false;
                }
                Require(overflows == 0, "Folder observer overflowed; no-write result would be inconclusive.");
                lock (gate) Require(events.Count == 0, "Standalone probe modified its directory: " + string.Join(", ", events));
                Require(File.ReadAllBytes(copy).SequenceEqual(before) &&
                    Directory.GetFileSystemEntries(session).SequenceEqual(new[] { copy }),
                    "Standalone probe changed its EXE or left an additional file/directory.");
            }
            finally
            {
                // Only remove this test-created GUID child, never a caller path.
                string checkedSession = Path.GetFullPath(session);
                string checkedRoot = Path.GetFullPath(artifacts).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!checkedSession.StartsWith(checkedRoot, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(checkedSession).Length != 32)
                    throw new InvalidOperationException("Refusing to clean an unverified portability-test path.");
                if (Directory.Exists(checkedSession) &&
                    (File.GetAttributes(checkedSession) & FileAttributes.ReparsePoint) == 0)
                    Directory.Delete(checkedSession, true);
            }
        }

        private static string ReadManifest(string path)
        {
            IntPtr module = LoadLibraryEx(path, IntPtr.Zero, 0x2 | 0x20);
            if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                IntPtr resource = FindResource(module, new IntPtr(1), new IntPtr(24));
                if (resource == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Missing embedded application manifest.");
                uint size = SizeofResource(module, resource);
                Require(size > 0 && size <= 65536, "Application manifest has invalid bounds.");
                IntPtr loaded = LoadResource(module, resource);
                IntPtr address = LockResource(loaded);
                if (address == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                var bytes = new byte[size];
                Marshal.Copy(address, bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes).TrimStart('\ufeff').TrimEnd('\0');
            }
            finally { FreeLibrary(module); }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", EntryPoint = "FindResourceW", SetLastError = true)]
        private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SizeofResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr LockResource(IntPtr resource);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeLibrary(IntPtr module);
    }
}

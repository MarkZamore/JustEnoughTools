using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using JustEnoughTools.Export;

namespace JustEnoughTools.Tests
{
    public static class ExportTests
    {
        private static int checks;
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string path, int index, out IntPtr large, out IntPtr small, uint count);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);

        public static int RunAll()
        {
            checks = 0;
            string root = Path.Combine(Path.GetTempPath(), "JustEnoughTools.ExportTests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                byte[] template = EmbeddedAssets.Read("RunnerTemplate"), originalIcon = EmbeddedAssets.Read("AppIcon");
                byte[] image = PortableResources.CreateExecutable(template, originalIcon);
                PortableResources.AssertIcon(image, originalIcon);
                Require(PortableResources.PinnedIconIndex(image) < -1, "The export must contain a content-specific icon alias.");
                byte[] secondIcon = SmallIcon();
                byte[] different = PortableResources.CreateExecutable(template, secondIcon);
                Require(!PortableResources.HaveSameIcon(image, different), "Custom icon frames must replace the template artwork.");
                Require(PortableResources.HaveSameIcon(image, PortableResources.CreateExecutable(template, originalIcon)), "The same icon must retain its resource identity.");
                Reject(() => PortableResources.CreateExecutable(new byte[512], originalIcon), "A damaged template was accepted.");
                Reject(() => IconFile.Validate(new byte[22]), "A damaged ICO was accepted.");
                byte[] invalidOffset = (byte[])secondIcon.Clone();
                Buffer.BlockCopy(BitConverter.GetBytes(Int32.MaxValue), 0, invalidOffset, 18, 4);
                Reject(() => IconFile.Validate(invalidOffset), "An ICO frame beyond the file was accepted.");

                var selection = new IconSelection();
                string custom = Path.Combine(root, "Custom.ico");
                File.WriteAllBytes(custom, secondIcon);
                selection.SelectCustom(custom);
                Require(selection.IsCustom && ExecutableExporter.Equal(selection.Read(), secondIcon), "Custom icon selection was not retained in memory.");
                File.Delete(custom);
                Require(ExecutableExporter.Equal(selection.Read(), originalIcon) && !selection.IsCustom && !String.IsNullOrEmpty(selection.LastWarning), "A missing custom icon must fall back with an explicit warning.");
                selection.Reset();
                Require(selection.LastWarning == null, "An explicit default selection must clear its old warning.");

                string destination = Path.Combine(root, "Saved.exe");
                ExecutableExporter.WriteVerified(destination, image);
                Require(ExecutableExporter.Equal(File.ReadAllBytes(destination), image), "The saved bytes differ from the verified image.");
                AssertNativeIcon(destination, PortableResources.PinnedIconIndex(image));
                using (var locked = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Reject(() => ExecutableExporter.WriteVerified(destination, different), "Replacing a locked EXE must fail.");
                Require(ExecutableExporter.Equal(File.ReadAllBytes(destination), image), "A failed replacement destroyed the original EXE.");
                Require(Directory.GetFiles(root, ".jet-*.tmp").Length == 0, "A failed replacement left its transient file.");

                var created = new List<string>();
                using (var watcher = new FileSystemWatcher(root))
                {
                    watcher.Created += (sender, args) => { lock (created) created.Add(Path.GetFileName(args.FullPath)); };
                    watcher.EnableRaisingEvents = true;
                    ExecutableExporter.WriteVerified(destination, different);
                    Thread.Sleep(100);
                }
                Require(ExecutableExporter.Equal(File.ReadAllBytes(destination), different), "A successful replacement did not install the new image.");
                lock (created) Require(created.All(name => name == "Saved.exe" || name.StartsWith(".jet-", StringComparison.Ordinal)), "Replacement created an unexpected additional file.");
                Require(Directory.GetFiles(root).Length == 1, "Export must leave only its selected EXE.");
                Reject(() => ExecutableExporter.Save(typeof(ExecutableExporter).Assembly.Location, selection, false), "Export must not overwrite the running constructor.");
            }
            finally
            {
                string resolved = Path.GetFullPath(root);
                if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(resolved).StartsWith("JustEnoughTools.ExportTests.", StringComparison.Ordinal))
                    throw new InvalidOperationException("Unexpected fixture cleanup path.");
                Directory.Delete(resolved, true);
            }
            return checks;
        }

        internal static byte[] SmallIcon()
        {
            const int side = 16, frameSize = 40 + side * side * 4 + side * 4;
            using (var memory = new MemoryStream())
            using (var writer = new BinaryWriter(memory))
            {
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
                writer.Write((byte)side); writer.Write((byte)side); writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(frameSize); writer.Write(22);
                writer.Write(40); writer.Write(side); writer.Write(side * 2); writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(0); writer.Write(side * side * 4); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                for (int pixel = 0; pixel < side * side; pixel++) writer.Write(0xff20a0e0u);
                writer.Write(new byte[side * 4]);
                return memory.ToArray();
            }
        }
        private static void AssertNativeIcon(string path, int index)
        {
            IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
            try { Require(ExtractIconEx(path, index, out large, out small, 1) > 0 && large != IntPtr.Zero && small != IntPtr.Zero, "Windows cannot read the exported icon alias."); }
            finally { if (large != IntPtr.Zero) DestroyIcon(large); if (small != IntPtr.Zero) DestroyIcon(small); }
        }
        private static void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); } catch (IOException) { rejected = true; } catch (InvalidDataException) { rejected = true; } catch (ArgumentException) { rejected = true; }
            Require(rejected, message);
        }
        private static void Require(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
    }
}

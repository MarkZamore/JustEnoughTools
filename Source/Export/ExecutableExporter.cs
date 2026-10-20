using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace JustEnoughTools.Export
{
    internal static class EmbeddedAssets
    {
        internal static byte[] Read(string name)
        {
            using (Stream stream = typeof(ExecutableExporter).Assembly.GetManifestResourceStream(name))
            {
                if (stream == null) throw new InvalidDataException("Не найден встроенный ресурс: " + name);
                using (var output = new MemoryStream()) { stream.CopyTo(output); return output.ToArray(); }
            }
        }
    }

    public sealed class IconSelection
    {
        private string customPath;
        public bool IsCustom { get { return customPath != null; } }
        public string LastWarning { get; private set; }
        public void SelectCustom(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Не указан файл иконки.", "path");
            IconFile.Read(path);
            customPath = Path.GetFullPath(path);
            LastWarning = null;
        }
        public void Reset() { customPath = null; LastWarning = null; }
        internal void BeginSave() { LastWarning = null; }
        internal void Saved() { customPath = null; }
        public byte[] Read()
        {
            if (customPath != null)
            {
                try { return IconFile.Read(customPath); }
                catch (Exception error)
                {
                    if (!(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
                        error is ArgumentException || error is System.Runtime.InteropServices.ExternalException)) throw;
                    customPath = null;
                    LastWarning = "Выбранная иконка недоступна или повреждена. Использована встроенная иконка.";
                }
            }
            return IconFile.Validate(EmbeddedAssets.Read("AppIcon"));
        }
    }

    internal static class IconFile
    {
        internal static byte[] Read(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length < 22 || file.Length > 16 * 1024 * 1024)
                    throw new InvalidDataException("Некорректный размер файла ICO.");
                byte[] bytes = new byte[(int)file.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = file.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) throw new InvalidDataException("Файл ICO изменился во время чтения.");
                    offset += read;
                }
                if (file.ReadByte() != -1) throw new InvalidDataException("Файл ICO изменился во время чтения.");
                return Validate(bytes);
            }
        }
        internal static byte[] Validate(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 22 || bytes.Length > 16 * 1024 * 1024)
                throw new InvalidDataException("Некорректный файл ICO.");
            using (var reader = new BinaryReader(new MemoryStream(bytes)))
            {
                if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1)
                    throw new InvalidDataException("Нужен файл иконки ICO.");
                int count = reader.ReadUInt16();
                if (count < 1 || count > 256 || bytes.Length < 6 + 16 * count)
                    throw new InvalidDataException("Некорректная таблица ICO.");
                for (int index = 0; index < count; index++)
                {
                    reader.BaseStream.Position = 6 + 16 * index + 8;
                    uint size = reader.ReadUInt32(), offset = reader.ReadUInt32();
                    if (size < 4 || offset < 6 + 16 * count || (long)offset + size > bytes.Length)
                        throw new InvalidDataException("Повреждённое изображение ICO.");
                }
            }
            using (var stream = new MemoryStream(bytes))
            using (var icon = new System.Drawing.Icon(stream))
                if (icon.Width == 0) throw new InvalidDataException("Пустая иконка.");
            return bytes;
        }
    }

    internal static class ShellIconRefresh
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(uint change, uint flags, string first, IntPtr second);
        internal sealed class IconChange { internal bool Required; internal int PreviousIndex = -1, NewIndex = -1; }
        internal static IconChange Compare(string path, byte[] replacement)
        {
            var change = new IconChange { NewIndex = PortableResources.PinnedIconIndex(replacement) };
            if (!File.Exists(path)) return change;
            try
            {
                byte[] previous = File.ReadAllBytes(path);
                change.Required = !PortableResources.HaveSameIcon(previous, replacement);
                change.PreviousIndex = PortableResources.PinnedIconIndex(previous);
            }
            catch { change.Required = true; }
            return change;
        }
        internal static void Changed(string path, IconChange change)
        {
            try
            {
                StartMenuShortcuts.RememberTarget(path);
                if (change.Required)
                {
                    StartMenuShortcuts.RefreshIcons(path, change.PreviousIndex, change.NewIndex);
                    SHChangeNotify(0x08000000, 0x1000, null, IntPtr.Zero);
                }
                SHChangeNotify(0x2000, 0x1005, path, IntPtr.Zero);
                SHChangeNotify(0x1000, 0x1005, Path.GetDirectoryName(path), IntPtr.Zero);
            }
            catch { /* Shell caches cannot invalidate a verified export. */ }
        }
    }

    public static class ExecutableExporter
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existing, string replacement, uint flags);

        public static void Save(string destination, IconSelection icon, bool addToStartMenu)
        {
            if (icon == null) throw new ArgumentNullException("icon");
            icon.BeginSave();
            destination = Path.GetFullPath(destination);
            if (!String.Equals(Path.GetExtension(destination), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Файл должен иметь расширение .exe.", "destination");
            if (String.Equals(destination, typeof(ExecutableExporter).Assembly.Location, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Нельзя заменить работающий JustEnoughTools.");
            string directory = Path.GetDirectoryName(destination);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Выбранная папка недоступна.");
            byte[] image = PortableResources.CreateExecutable(EmbeddedAssets.Read("RunnerTemplate"), icon.Read());
            var iconChange = ShellIconRefresh.Compare(destination, image);
            WriteVerified(destination, image);
            icon.Saved();
            ShellIconRefresh.Changed(destination, iconChange);
            if (addToStartMenu)
            {
                try { StartMenuShortcuts.CreateWithIcon(destination, iconChange.NewIndex); }
                catch (Exception error)
                {
                    throw new IOException("EXE сохранён, но ярлык меню «Пуск» не создан: " + error.Message, error);
                }
            }
        }

        // The only own transient file is adjacent to the destination, so the final
        // move stays on one volume. File.Replace is deliberately avoided: it can
        // create an additional system ~RF temporary file.
        internal static void WriteVerified(string destination, byte[] image)
        {
            string temporary = Path.Combine(Path.GetDirectoryName(destination), ".jet-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    65536, FileOptions.WriteThrough))
                {
                    output.Write(image, 0, image.Length);
                    output.Flush(true);
                }
                byte[] recorded = File.ReadAllBytes(temporary);
                using (var hash = SHA256.Create())
                {
                    if (recorded.Length != image.Length || !Equal(hash.ComputeHash(image), hash.ComputeHash(recorded)))
                        throw new IOException("Не удалось проверить записанный EXE. Существующий файл сохранён.");
                }
                PortableResources.Validate(recorded);
                for (int attempt = 0; ; attempt++)
                {
                    if (MoveFileEx(temporary, destination, 0x1 | 0x8)) break;
                    int code = Marshal.GetLastWin32Error();
                    if (attempt >= 5 || (code != 32 && code != 33))
                        throw new IOException("Не удалось заменить EXE. Существующий файл сохранён: " + new Win32Exception(code).Message,
                            new Win32Exception(code));
                    Thread.Sleep(40);
                }
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { /* A crash or external lock can leave the one allowed transient. */ }
            }
        }
        internal static bool Equal(byte[] first, byte[] second)
        {
            if (first == null || second == null || first.Length != second.Length) return false;
            for (int index = 0; index < first.Length; index++) if (first[index] != second[index]) return false;
            return true;
        }
    }
}

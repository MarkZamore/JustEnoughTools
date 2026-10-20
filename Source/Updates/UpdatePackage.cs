using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace JustEnoughTools.Updates
{
    // The only application-created update file. The payload comes first so the
    // same file can become the replacement after its footer has been removed.
    internal static class UpdatePackage
    {
        internal const int MaxPayloadBytes = 64 * 1024 * 1024;
        internal const int MaxManifestBytes = 64 * 1024;
        internal const int FooterLength = 24;
        internal const string FileName = "JustEnoughTools.update";
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("JETUPD02");

        internal static string GetPath(string executablePath)
        {
            string fullPath = Path.GetFullPath(executablePath);
            string directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("У программы нет каталога.", nameof(executablePath));
            return Path.Combine(directory, FileName);
        }

        internal static byte[] SerializeManifest(UpdateManifest manifest)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(UpdateManifest)).WriteObject(stream, manifest);
                if (stream.Length > MaxManifestBytes) throw new InvalidDataException("Манифест обновления слишком большой.");
                return stream.ToArray();
            }
        }

        internal static UpdateManifest DeserializeManifest(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxManifestBytes)
                throw new InvalidDataException("Недопустимый размер манифеста.");
            using (var stream = new MemoryStream(bytes, false))
            {
                var manifest = new DataContractJsonSerializer(typeof(UpdateManifest)).ReadObject(stream) as UpdateManifest;
                if (manifest == null) throw new InvalidDataException("Пустой манифест обновления.");
                UpdateService.ValidateManifest(manifest);
                return manifest;
            }
        }

        internal static void Write(string packagePath, byte[] payload, UpdateManifest manifest)
        {
            UpdateService.ValidateManifest(manifest);
            ValidatePayload(payload, manifest);
            byte[] metadata = SerializeManifest(manifest);
            RejectReparsePoint(packagePath);
            using (var stream = new FileStream(packagePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
                       65536, FileOptions.WriteThrough))
            {
                // Set Hidden while the file is open, before the potentially long write.
                File.SetAttributes(packagePath, File.GetAttributes(packagePath) | FileAttributes.Hidden);
                stream.SetLength(0);
                stream.Write(payload, 0, payload.Length);
                stream.Write(metadata, 0, metadata.Length);
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(Magic);
                    writer.Write((long)payload.Length);
                    writer.Write(metadata.Length);
                    writer.Write(2);
                }
                stream.Flush(true);
            }
        }

        internal static bool TryRead(string packagePath, int currentRelease, out PreparedUpdate prepared, out string error)
        {
            prepared = null;
            error = "";
            if (!File.Exists(packagePath)) return false;
            try
            {
                RejectReparsePoint(packagePath);
                using (var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                           65536, FileOptions.SequentialScan))
                {
                    if (stream.Length < FooterLength || stream.Length > (long)MaxPayloadBytes + MaxManifestBytes + FooterLength)
                        throw new InvalidDataException("Недопустимый размер пакета обновления.");
                    stream.Position = stream.Length - FooterLength;
                    long payloadLength;
                    int metadataLength;
                    using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
                    {
                        byte[] magic = reader.ReadBytes(Magic.Length);
                        for (int i = 0; i < Magic.Length; i++)
                            if (magic.Length != Magic.Length || magic[i] != Magic[i]) throw new InvalidDataException("Пакет обновления не завершён.");
                        payloadLength = reader.ReadInt64();
                        metadataLength = reader.ReadInt32();
                        if (reader.ReadInt32() != 2) throw new InvalidDataException("Неизвестный формат пакета обновления.");
                    }
                    if (payloadLength <= 0 || payloadLength > MaxPayloadBytes || metadataLength <= 0 || metadataLength > MaxManifestBytes ||
                        stream.Length != payloadLength + metadataLength + FooterLength)
                        throw new InvalidDataException("Неверные границы пакета обновления.");
                    stream.Position = payloadLength;
                    byte[] metadata = new byte[metadataLength];
                    ReadExactly(stream, metadata, metadata.Length);
                    UpdateManifest manifest = DeserializeManifest(metadata);
                    if (manifest.ReleaseNumber <= currentRelease) throw new InvalidDataException("Пакет относится к установленной или более старой версии.");
                    if (manifest.SizeBytes != payloadLength) throw new InvalidDataException("Размер EXE не совпадает с манифестом.");
                    stream.Position = 0;
                    if (!IsViableExecutable(stream, payloadLength)) throw new InvalidDataException("Пакет не содержит корректный EXE.");
                    stream.Position = 0;
                    string hash = HashStream(stream, payloadLength);
                    if (!string.Equals(hash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Контрольная сумма обновления не совпала.");
                    prepared = new PreparedUpdate(packagePath, manifest, payloadLength);
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is System.Runtime.Serialization.SerializationException || ex is ArgumentException || ex is System.Xml.XmlException || ex is OverflowException)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static void ValidatePayload(byte[] payload, UpdateManifest manifest)
        {
            if (payload == null || payload.Length == 0 || payload.Length > MaxPayloadBytes || payload.LongLength != manifest.SizeBytes)
                throw new InvalidDataException("Размер обновления не совпадает с манифестом.");
            using (var stream = new MemoryStream(payload, false))
            {
                if (!IsViableExecutable(stream, payload.Length)) throw new InvalidDataException("Получен некорректный EXE.");
            }
            if (!string.Equals(HashBytes(payload), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма обновления не совпала.");
        }

        internal static bool IsViableExecutable(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (!IsViableExecutable(stream, stream.Length)) return false;
                    // This product is a managed Framework executable. PE magic
                    // alone also accepts a truncated file with a surviving header.
                    return AssemblyName.GetAssemblyName(path).Name != null;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is BadImageFormatException)
            {
                return false;
            }
        }

        private static bool IsViableExecutable(Stream stream, long length)
        {
            if (length < 128) return false;
            stream.Position = 0;
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (reader.ReadUInt16() != 0x5a4d) return false;
                stream.Position = 0x3c;
                int header = reader.ReadInt32();
                if (header < 64 || header > length - 24) return false;
                stream.Position = header;
                return reader.ReadUInt32() == 0x00004550;
            }
        }

        internal static string HashBytes(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return ToHex(sha.ComputeHash(bytes));
        }

        internal static string HashFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return HashStream(stream, stream.Length);
        }

        internal static string HashStream(Stream stream, long length)
        {
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[65536];
                long remaining = length;
                while (remaining > 0)
                {
                    int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0) throw new EndOfStreamException("Неполный файл обновления.");
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                    remaining -= read;
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return ToHex(sha.Hash);
            }
        }

        internal static string ToHex(byte[] bytes)
        {
            var result = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes) result.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return result.ToString();
        }

        internal static void DeleteInvalidIfSafe(string packagePath, string executablePath)
        {
            try
            {
                // Keep the original pinned against replacement/deletion until
                // cleanup is done. A valid header at a vanished path is not a
                // safe reason to discard the pending copy.
                using (var original = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (!IsViableExecutable(original, original.Length) || AssemblyName.GetAssemblyName(executablePath).Name == null) return;
                    RejectReparsePoint(packagePath);
                    if (File.Exists(packagePath)) File.Delete(packagePath);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is BadImageFormatException) { }
        }

        private static void RejectReparsePoint(string path)
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Пакет обновления не может быть ссылкой на другой файл.");
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
        }
    }
}

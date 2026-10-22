using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Microsoft.Win32;

namespace JustEnoughTools.Tweaks
{
    internal sealed class StartupEntry
    {
        internal string Key, Path, Name, Error;
        internal bool Machine, Package;
    }

    internal sealed partial class WindowsTweakPlatform
    {
        const string Approval = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        const string AppModel = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\";
        IEnumerable<StartupEntry> StartupEntries()
        {
            var entries = new List<StartupEntry>();
            foreach (var source in new[] {
                new { Machine = false, Run = @"Software\Microsoft\Windows\CurrentVersion\Run", Kind = "Run" },
                new { Machine = true, Run = @"Software\Microsoft\Windows\CurrentVersion\Run", Kind = "Run" },
                new { Machine = true, Run = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", Kind = "Run32" } })
            {
                if (machineOnly && !source.Machine) continue;
                try
                {
                    using (var root = RegistryKey.OpenBaseKey(source.Machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64))
                    using (var run = root.OpenSubKey(source.Run))
                        if (run != null)
                            foreach (string name in run.GetValueNames())
                                entries.Add(new StartupEntry { Key = (source.Machine ? "machine/" : "user/") + source.Kind + "/" + name,
                                    Machine = source.Machine, Path = Approval + source.Kind, Name = name });
                }
                catch (Exception error) { entries.Add(new StartupEntry { Key = "unreadable/" + source.Machine + source.Kind, Error = error.Message }); }
            }
            foreach (bool machine in new[] { false, true })
            {
                if (machineOnly && !machine) continue;
                try
                {
                    string folder = Environment.GetFolderPath(machine ? Environment.SpecialFolder.CommonStartup : Environment.SpecialFolder.Startup);
                    if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                    foreach (string file in Directory.EnumerateFiles(folder))
                    {
                        if (string.Equals(Path.GetFileName(file), "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        entries.Add(new StartupEntry { Key = (machine ? "machine/" : "user/") + "StartupFolder/" + Path.GetFileName(file),
                            Machine = machine, Path = Approval + "StartupFolder", Name = Path.GetFileName(file) });
                    }
                }
                catch (Exception error) { entries.Add(new StartupEntry { Key = "unreadable/" + machine + "StartupFolder", Error = error.Message }); }
            }
            // Confirm package identity and task IDs against the installed manifest.
            // No heuristic writes to arbitrary SystemAppData children.
            if (machineOnly) return entries;
            try
            {
              using (var packages = Registry.CurrentUser.OpenSubKey(AppModel + @"Repository\Packages"))
              {
                if (packages != null) foreach (string fullName in packages.GetSubKeyNames())
                {
                    var name = fullName.Split('_');
                    if (name.Length < 5) continue;
                    string family = name[0] + "_" + name[name.Length - 1];
                    try
                    {
                        string location;
                        using (var package = packages.OpenSubKey(fullName)) location = Convert.ToString(package.GetValue("PackageRootFolder"));
                        foreach (string task in ReadStartupTasks(location))
                            entries.Add(new StartupEntry { Key = "package/" + family + "/" + task, Package = true,
                                Path = AppModel + @"SystemAppData\" + family + "\\" + task, Name = "State" });
                    }
                    catch (Exception error) { entries.Add(new StartupEntry { Key = "unreadable/package/" + fullName, Error = error.Message }); }
                }
              }
            }
            catch (Exception error) { entries.Add(new StartupEntry { Key = "unreadable/packages", Error = error.Message }); }
            return entries;
        }
        internal static IEnumerable<string> ReadStartupTasks(string location)
        {
            if (string.IsNullOrEmpty(location)) return new string[0];
            if (!Path.IsPathRooted(location) || location.StartsWith(@"\\", StringComparison.Ordinal) || new Uri(Path.GetFullPath(location)).IsUnc)
                throw new InvalidDataException("Пакет автозагрузки не имеет локального пути.");
            string path = Path.Combine(location, "AppxManifest.xml");
            if (!File.Exists(path)) return new string[0];
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(path, settings)) document.Load(reader);
            var tasks = document.SelectNodes("//*[local-name()='Extension' and @Category='windows.startupTask']/*[local-name()='StartupTask']")
                .Cast<XmlElement>().Select(e => e.GetAttribute("TaskId")).ToArray();
            if (tasks.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 256 || s.IndexOfAny(new[] { '\\', '/', '\0' }) >= 0))
                throw new InvalidDataException("Пакет содержит неизвестный идентификатор автозагрузки.");
            return tasks.Distinct().ToArray();
        }
        public IReadOnlyList<TweakPart> InspectStartup()
        {
            var result = new List<TweakPart>();
            try
            {
                foreach (var entry in StartupEntries().GroupBy(e => e.Key).Select(g => g.First()))
                {
                    try { result.Add(InspectStartupEntry(entry)); }
                    catch (Exception error) { result.Add(new TweakPart(entry.Key, TweakState.Unknown, reason: entry.Name + ": " + error.Message)); }
                }
            }
            catch (Exception error) { result.Add(new TweakPart("enumeration", TweakState.Unknown, reason: "Перечень автозагрузки прочитан не полностью: " + error.Message)); }
            if (result.Count == 0) result.Add(new TweakPart("empty", TweakState.Applied, reason: "Доступных приложений автозагрузки нет."));
            return result;
        }
        TweakPart InspectStartupEntry(StartupEntry entry)
        {
            if (entry.Error != null) return new TweakPart(entry.Key, TweakState.Unknown, reason: "Запись автозагрузки недоступна: " + entry.Error);
            var datum = Read(entry.Machine, entry.Path, entry.Name);
            TweakState state;
            if (entry.Package)
            {
                // Missing package state is not a documented enabled default.
                if (!datum.Exists || datum.Kind != RegistryValueKind.DWord) state = TweakState.Unknown;
                else switch ((int)datum.Value)
                {
                    case 0: case 1: case 3: state = TweakState.Applied; break;
                    case 2: state = TweakState.Pending; break;
                    case 4: state = TweakState.Unavailable; break;
                    default: state = TweakState.Unknown; break;
                }
            }
            else state = ClassicStartupState(datum);
            return new TweakPart(entry.Key, state, entry.Machine, entry.Key + ": " +
                (state == TweakState.Applied ? "отключено." : state == TweakState.Pending ? "можно отключить." : "неизвестное состояние или ограничение политики."));
        }
        internal static TweakState ClassicStartupState(RegistryDatum value)
        {
            if (!value.Exists) return TweakState.Pending;
            var bytes = value.Value as byte[];
            if (value.Kind != RegistryValueKind.Binary || bytes == null || bytes.Length != 12) return TweakState.Unknown;
            uint code = BitConverter.ToUInt32(bytes, 0);
            return code == 2 || code == 6 ? TweakState.Pending : code == 3 || code == 7 ? TweakState.Applied : TweakState.Unknown;
        }
        internal static byte[] DisabledStartupBytes(RegistryDatum before, long fileTime)
        {
            if (ClassicStartupState(before) != TweakState.Pending) throw new InvalidOperationException("Неизвестное состояние автозагрузки.");
            byte[] result = before.Exists ? (byte[])((byte[])before.Value).Clone() : new byte[12];
            Buffer.BlockCopy(BitConverter.GetBytes(before.Exists && BitConverter.ToUInt32(result, 0) == 6 ? 7U : 3U), 0, result, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(fileTime), 0, result, 4, 8);
            return result;
        }
        public TweakPart ApplyStartup(string key)
        {
            var entry = StartupEntries().GroupBy(e => e.Key).Select(g => g.First()).SingleOrDefault(e => e.Key == key);
            if (entry == null) return new TweakPart(key, TweakState.Unavailable, reason: "Запись автозагрузки исчезла.");
            var before = InspectStartupEntry(entry);
            if (before.State != TweakState.Pending) return before;
            var datum = Read(entry.Machine, entry.Path, entry.Name);
            if (entry.Package)
            {
                Write(false, entry.Path, "State", 1, RegistryValueKind.DWord);
                Write(false, entry.Path, "LastDisabledTime", DateTime.UtcNow.ToFileTimeUtc(), RegistryValueKind.QWord);
            }
            else Write(entry.Machine, entry.Path, entry.Name, DisabledStartupBytes(datum, DateTime.UtcNow.ToFileTimeUtc()), RegistryValueKind.Binary);
            if (InspectStartupEntry(entry).State != TweakState.Applied) throw new InvalidOperationException("Отключение автозагрузки не подтверждено: " + key);
            return new TweakPart(key, TweakState.Deferred, entry.Machine, key + ": автозагрузка отключена; запись сохранена.");
        }
    }
}

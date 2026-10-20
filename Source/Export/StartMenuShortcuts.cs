// Adapted from the user-owned Autonomy&Performance project; resource and Shell fixes retained.
namespace JustEnoughTools.Export {
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;

// Startup repair/cleanup manages only marked links in our Programs folder and Taskbar copies. Icon refresh is
// limited to existing links to the exact exported EXE, keeping their other properties.
// No registration, scheduled task, background process or target execution is used.
public static class StartMenuShortcuts
{
    const string Marker = "JustEnoughTools.ScriptShortcut.v1";
    static readonly Guid ShellLinkClass = new Guid("00021401-0000-0000-C000-000000000046");
    static volatile bool stopping;
    static Thread cleanupWorker;
    static readonly object LinkGate=new object();

    public static IDisposable StartMaintenance()
    {
        Cleanup();
        return new MaintenanceLifetime();
    }
    sealed class MaintenanceLifetime : IDisposable
    {
        int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) StopCleanup(); }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, IntPtr data, uint flags);
        void GetIDList(out IntPtr value);
        void SetIDList(IntPtr value);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int length);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetHotkey(out short value);
        void SetHotkey(short value);
        void GetShowCmd(out int value);
        void SetShowCmd(int value);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    static string Folder()
    {
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (String.IsNullOrEmpty(programs)) throw new DirectoryNotFoundException();
        return Path.Combine(programs, "JustEnoughTools");
    }

    public static string Create(string executable) { return CreateInFolder(executable, Folder()); }
    internal static string CreateWithIcon(string executable,int iconIndex) { return CreateInFolderWithIcon(executable, Folder(),iconIndex); }

    internal static string CreateInFolder(string executable, string folder)
    { return CreateInFolderWithIcon(executable,folder,0); }

    static string CreateInFolderWithIcon(string executable, string folder,int iconIndex)
    { lock(LinkGate)return CreateInFolderWithIconCore(executable,folder,iconIndex); }
    static string CreateInFolderWithIconCore(string executable,string folder,int iconIndex)
    {
        executable = Path.GetFullPath(executable);
        if (!String.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException();
        if (!File.Exists(executable)) throw new FileNotFoundException();
        if (!Vacant(folder) && IsReparsePoint(folder)) throw new IOException();
        Directory.CreateDirectory(folder);
        string destination = null;
        foreach (string path in Directory.GetFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly)) {
            string target;
            if (ReadOwnedTarget(path, out target) && String.Equals(target, executable, StringComparison.OrdinalIgnoreCase)) { destination = path; break; }
        }
        if (destination == null) {
            string name = Path.GetFileNameWithoutExtension(executable);
            for (int number = 1; number <= 10000; number++) {
                string candidate = Path.Combine(folder, name + (number == 1 ? "" : " (" + number + ")") + ".lnk");
                if (Vacant(candidate)) { destination = candidate; break; }
            }
            if (destination == null) throw new IOException();
        }
        object instance = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClass));
        try {
            IShellLink link = (IShellLink)instance;
            bool existing=File.Exists(destination);
            if(existing)((IPersistFile)instance).Load(destination,2);
            link.SetPath(executable);
            if(!existing)link.SetWorkingDirectory(Path.GetDirectoryName(executable));
            link.SetArguments("");
            link.SetDescription(Marker);
            bool preserveIcon=false;
            if(existing){StringBuilder originalIcon=new StringBuilder(32768);int originalIndex;link.GetIconLocation(originalIcon,originalIcon.Capacity,out originalIndex);
                preserveIcon=(originalIcon.Length!=0&&!String.Equals(originalIcon.ToString(),executable,StringComparison.OrdinalIgnoreCase))
                    ||(originalIndex!=0&&originalIndex!=-1&&originalIndex!=iconIndex);}
            if(!preserveIcon)link.SetIconLocation(executable, iconIndex);
            if(!existing)link.SetShowCmd(1);
            SetFingerprint(instance,executable,Fingerprint(executable));
            ((IPersistFile)instance).Save(destination, true);
        } finally { Marshal.FinalReleaseComObject(instance); }
        return destination;
    }

    public static void Cleanup()
    {
        // Disk enumeration must not delay opening the window. This thread belongs
        // to the constructor and ends with it; there is no installed watcher.
        stopping=false;
        Thread worker=new Thread(delegate(){
            SearchContext context=new SearchContext(LocalRoots());context.CancelRequested=delegate{return stopping;};
            string programs=null,taskbar=null;
            try{programs=Folder();context.Seed(programs);}catch{ }
            try{string roaming=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);if(!String.IsNullOrEmpty(roaming)){taskbar=Path.Combine(roaming,@"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");context.Seed(taskbar);}}catch{ }
            try { if(programs!=null)CleanupInFolder(programs,true,ShellUnpin.TryUnpin,context); } catch { }
            try { if(taskbar!=null)CleanupInFolder(taskbar,false,ShellUnpin.TryUnpin,context); }catch{ }
        });
        cleanupWorker=worker;worker.IsBackground=true;worker.SetApartmentState(ApartmentState.STA);worker.Start();
    }
    internal static void StopCleanup(){stopping=true;Thread worker=cleanupWorker;if(worker!=null&&worker!=Thread.CurrentThread)worker.Join(2000);}

    internal static void RefreshIcons(string executable,int previousIndex,int newIndex)
    {
        try { RefreshIconsInFolder(executable, Folder(),previousIndex,newIndex); } catch { }
        try {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!String.IsNullOrEmpty(roaming)) RefreshIconsInFolder(executable, Path.Combine(roaming, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),previousIndex,newIndex);
        } catch { }
    }

    [DllImport("shell32.dll", CharSet=CharSet.Unicode)]
    static extern void SHChangeNotify(uint change, uint flags, string first, IntPtr second);

    internal static int RefreshIconsInFolder(string executable, string folder,int previousIndex=-1,int newIndex=-1)
    { lock(LinkGate)return RefreshIconsCore(executable,folder,previousIndex,newIndex); }
    static int RefreshIconsCore(string executable,string folder,int previousIndex,int newIndex)
    {
        int refreshed = 0;
        executable = Path.GetFullPath(executable);
        if (!Directory.Exists(folder) || IsReparsePoint(folder)) return refreshed;
        foreach (string path in Directory.GetFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly)) {
            object instance = null;
            try {
                if (IsReparsePoint(path)) continue;
                instance = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClass));
                ((IPersistFile)instance).Load(path, 2);
                IShellLink link = (IShellLink)instance;
                string target;
                if (!ReadTarget(link, false, out target) || !String.Equals(target, executable, StringComparison.OrdinalIgnoreCase)) continue;
                StringBuilder icon = new StringBuilder(32768); int index;
                link.GetIconLocation(icon, icon.Capacity, out index);
                // Preserve a user-selected external icon or another resource index.
                if ((icon.Length != 0 && !String.Equals(icon.ToString(), executable, StringComparison.OrdinalIgnoreCase)) || (index != 0 && index != -1 && index != previousIndex)) continue;
                // Re-save the LOADED link: arguments, AppUserModelID, description,
                // working directory and other Windows pin metadata are preserved.
                // The alias has the same artwork as the default icon, with an ID derived
                // from its content. Reusing an icon therefore reuses the correct cached art.
                link.SetIconLocation(executable, newIndex);
                ((IPersistFile)instance).Save(path, true);
                SHChangeNotify(0x2000, 0x1005, path, IntPtr.Zero);
                refreshed++;
            } catch { }
            finally { if (instance != null) Marshal.FinalReleaseComObject(instance); }
        }
        if (refreshed > 0) SHChangeNotify(0x1000, 0x1005, folder, IntPtr.Zero);
        return refreshed;
    }

    internal static int CleanupInFolder(string folder)
    { return CleanupInFolder(folder,true,ShellUnpin.TryUnpin); }

    internal static int CleanupInFolder(string folder,bool removeEmptyFolder,Func<string,bool> unpin)
    { return CleanupInFolder(folder,removeEmptyFolder,unpin,new SearchContext(LocalRoots())); }

    internal static int CleanupInFolder(string folder,bool removeEmptyFolder,Func<string,bool> unpin,SearchContext context)
    {
        int removed = 0;
        if (!Directory.Exists(folder) || IsReparsePoint(folder)) return removed;
        foreach (string path in Directory.GetFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly)) {
            if(context.Cancelled)return removed;
            try {
                string target;
                if (ReadCleanupTarget(path, out target) && !File.Exists(target) && !target.StartsWith(@"\\",StringComparison.Ordinal)) {
                    // Let Windows' identity-based link tracker find a renamed/moved file
                    // before considering this shortcut deleted. Never use fuzzy searches.
                    if (context.UseNativeTracking&&DefinitelyMissing(target)&&TryRelocate(path,target)) continue;
                    string recordedFingerprint=ReadFingerprint(path,target),fingerprint=recordedFingerprint??context.Known(target);
                    if(fingerprint==null)continue;
                    if(fingerprint!=null){
                        SearchResult result=context.Find(target,fingerprint);
                        if(context.Cancelled)return removed;
                        // Ambiguous/incomplete searches cannot establish deletion.
                        if(result.Path!=null){RepairPath(path,target,result.Path,fingerprint);continue;}
                        if(!result.Complete||result.Ambiguous)continue;
                    }
                    lock(LinkGate){
                    if(context.Cancelled)return removed;
                    if(!DefinitelyMissing(target))continue;
                    string current;
                    if(!ReadCleanupTarget(path,out current)||!String.Equals(current,target,StringComparison.OrdinalIgnoreCase)||ReadFingerprint(path,target)!=recordedFingerprint)continue;
                    // Unpin BEFORE deleting the shortcut, as required by Windows. Keep it
                    // for a later retry if the Shell cannot perform its part of cleanup.
                    if(!unpin(path))continue;
                    // Recheck after the Shell call: a disconnected/restored target must
                    // never be removed merely because an earlier observation was stale.
                    if(!ReadCleanupTarget(path,out current)||!String.Equals(current,target,StringComparison.OrdinalIgnoreCase)||!DefinitelyMissing(target)||ReadFingerprint(path,target)!=recordedFingerprint)continue;
                    File.Delete(path);removed++;
                    SHChangeNotify(4,0x1005,path,IntPtr.Zero);
                    }
                }
                else if(target!=null&&File.Exists(target))RememberLink(path,target);
            } catch { }
        }
        if(removed>0){SHChangeNotify(0x1000,0x1005,folder,IntPtr.Zero);if(removeEmptyFolder){try{Directory.Delete(folder,false);SHChangeNotify(0x10,0x1005,folder,IntPtr.Zero);}catch{ }}}
        return removed;
    }

    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit,Size=24)] struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Text; }
    [ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore {
        void GetCount(out uint count);void GetAt(uint index,out PropertyKey key);
        void GetValue(ref PropertyKey key,out PropVariant value);void SetValue(ref PropertyKey key,ref PropVariant value);void Commit();
    }
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);
    static PropertyKey FingerprintKey(){return new PropertyKey{Format=new Guid("76BEF76B-607B-4469-83BA-0697D846537B"),Id=2};}
    static string Fingerprint(string path)
    {
        using(FileStream file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))
        using(SHA256 hash=SHA256.Create()){
            long size=file.Length;if(size<1||size>128*1024*1024)return null;
            return size.ToString(System.Globalization.CultureInfo.InvariantCulture)+":"+BitConverter.ToString(hash.ComputeHash(file)).Replace("-","");
        }
    }
    static bool ValidFingerprint(string value)
    {
        if(value==null)return false;int colon=value.IndexOf(':');long size;
        if(colon<1||value.Length-colon!=65||!Int64.TryParse(value.Substring(0,colon),out size)||size<1||size>128*1024*1024)return false;
        for(int i=colon+1;i<value.Length;i++)if(!((value[i]>='0'&&value[i]<='9')||(value[i]>='A'&&value[i]<='F')))return false;
        return true;
    }
    static void SetFingerprint(object instance,string target,string value)
    {
        if(!ValidFingerprint(value))return;
        PropertyKey key=FingerprintKey();PropVariant input=new PropVariant{Type=31,Text=Marshal.StringToCoTaskMemUni(target+"|"+value)};
        try{IPropertyStore store=(IPropertyStore)instance;store.SetValue(ref key,ref input);store.Commit();}
        finally{PropVariantClear(ref input);}
    }
    static string GetFingerprint(object instance,string target)
    {
        PropertyKey key=FingerprintKey();PropVariant value;((IPropertyStore)instance).GetValue(ref key,out value);
        try{
            string raw=value.Type==31?Marshal.PtrToStringUni(value.Text):null;
            if(raw==null)return null;int split=raw.IndexOf('|');
            if(split<0||!String.Equals(raw.Substring(0,split),target,StringComparison.OrdinalIgnoreCase))return null;
            string fingerprint=raw.Substring(split+1);return ValidFingerprint(fingerprint)?fingerprint:null;
        }finally{PropVariantClear(ref value);}
    }
    static string ReadFingerprint(string path,string target)
    {
        object instance=null;
        try{instance=Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClass));((IPersistFile)instance).Load(path,0);return GetFingerprint(instance,target);}
        catch{return null;}finally{if(instance!=null)Marshal.FinalReleaseComObject(instance);}
    }
    static void RememberLink(string path,string target,bool replace=false)
    { lock(LinkGate)RememberLinkCore(path,target,replace); }
    static void RememberLinkCore(string path,string target,bool replace)
    {
        object instance=null;
        try{
            string owned;if(!ReadCleanupTarget(path,out owned)||!String.Equals(owned,target,StringComparison.OrdinalIgnoreCase)||IsReparsePoint(target))return;
            if(!replace&&ReadFingerprint(path,target)!=null)return;
            string fingerprint=Fingerprint(target);if(fingerprint==null||ReadFingerprint(path,target)==fingerprint)return;
            instance=Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClass));((IPersistFile)instance).Load(path,2);
            string current;if(!ReadTarget((IShellLink)instance,true,out current)||!String.Equals(current,target,StringComparison.OrdinalIgnoreCase))return;
            SetFingerprint(instance,target,fingerprint);((IPersistFile)instance).Save(path,true);
        }catch{ }finally{if(instance!=null)Marshal.FinalReleaseComObject(instance);}
    }
    internal static void RememberTarget(string target)
    {
        try{
            string roaming=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach(string folder in new string[]{Folder(),Path.Combine(roaming,@"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar")}){
                if(!Directory.Exists(folder)||IsReparsePoint(folder))continue;
                foreach(string path in Directory.GetFiles(folder,"*.lnk")){
                    string owned;if(ReadCleanupTarget(path,out owned)&&String.Equals(owned,target,StringComparison.OrdinalIgnoreCase))RememberLink(path,target,true);
                }
            }
        }catch{ }
    }
    internal sealed class SearchResult { internal string Path;internal bool Complete=true,Ambiguous; }
    internal sealed class SearchContext
    {
        readonly string[] roots;
        readonly System.Diagnostics.Stopwatch budget=System.Diagnostics.Stopwatch.StartNew();
        internal bool UseNativeTracking=true;
        internal Func<bool> CancelRequested;
        internal bool Cancelled{get{return budget.ElapsedMilliseconds>60000||(CancelRequested!=null&&CancelRequested());}}
        readonly Dictionary<string,SearchResult> results=new Dictionary<string,SearchResult>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,string> known=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        internal SearchContext(string[] roots){this.roots=roots;}
        internal string Known(string target){string value;return known.TryGetValue(target,out value)?value:null;}
        internal void Seed(string folder)
        {
            if(!Directory.Exists(folder)||IsReparsePoint(folder))return;
            foreach(string path in Directory.GetFiles(folder,"*.lnk"))try{
                if(Cancelled)return;
                string target;if(!ReadCleanupTarget(path,out target))continue;
                if(File.Exists(target))RememberLink(path,target);
                string fingerprint=ReadFingerprint(path,target);if(fingerprint==null)continue;
                string existing;if(known.TryGetValue(target,out existing)&&existing!=fingerprint)known[target]=null;else known[target]=fingerprint;
            }catch{ }
        }
        internal SearchResult Find(string previous,string fingerprint)
        {
            string key=System.IO.Path.GetPathRoot(previous)+"|"+fingerprint;SearchResult result;
            if(results.TryGetValue(key,out result))return result;
            result=new SearchResult();long size=Int64.Parse(fingerprint.Substring(0,fingerprint.IndexOf(':')),System.Globalization.CultureInfo.InvariantCulture);
            foreach(string root in roots){
                Stack<string> directories=new Stack<string>();directories.Push(root);
                while(directories.Count>0){
                    if(Cancelled){result.Path=null;result.Complete=false;return result;}
                    string directory=directories.Pop();
                    try{
                        if(IsReparsePoint(directory))continue;
                        foreach(string path in Directory.EnumerateFiles(directory,"*.exe",SearchOption.TopDirectoryOnly)){
                            if(Cancelled){result.Path=null;result.Complete=false;return result;}
                            try{
                                if(IsReparsePoint(path)||new FileInfo(path).Length!=size||Fingerprint(path)!=fingerprint)continue;
                                if(result.Path!=null&&!String.Equals(result.Path,path,StringComparison.OrdinalIgnoreCase)){result.Ambiguous=true;result.Path=null;results[key]=result;return result;}
                                result.Path=System.IO.Path.GetFullPath(path);
                            }catch{result.Complete=false;}
                        }
                        foreach(string child in Directory.EnumerateDirectories(directory)){if(Cancelled){result.Path=null;result.Complete=false;return result;}directories.Push(child);}
                    }catch{result.Complete=false;}
                }
            }
            // An unreadable location prevents deletion. A byte-for-byte match in
            // an accessible location can still repair the link; never guess by name.
            results[key]=result;return result;
        }
    }
    static string[] LocalRoots()
    {
        List<string> roots=new List<string>();
        // Include unavailable local drives in the search: their failure marks it
        // incomplete, so a temporarily disconnected copy cannot cause deletion.
        foreach(DriveInfo drive in DriveInfo.GetDrives())try{if(drive.DriveType==DriveType.Fixed||drive.DriveType==DriveType.Removable)roots.Add(drive.Name);}catch{ }
        return roots.ToArray();
    }
    static bool RepairPath(string path,string previous,string current,string fingerprint)
    { lock(LinkGate)return RepairPathCore(path,previous,current,fingerprint); }
    static bool RepairPathCore(string path,string previous,string current,string fingerprint)
    {
        object instance=null;
        try{
            string owned;
            if(!ReadCleanupTarget(path,out owned)||!String.Equals(owned,previous,StringComparison.OrdinalIgnoreCase)||File.Exists(previous)
                ||IsReparsePoint(current)||Fingerprint(current)!=fingerprint)return false;
            string recorded=ReadFingerprint(path,previous);if(recorded!=null&&recorded!=fingerprint)return false;
            instance=Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClass));((IPersistFile)instance).Load(path,2);IShellLink link=(IShellLink)instance;
            StringBuilder icon=new StringBuilder(32768),work=new StringBuilder(32768);int index;link.GetIconLocation(icon,icon.Capacity,out index);link.GetWorkingDirectory(work,work.Capacity);
            link.SetPath(current);link.SetIconLocation(current,index);
            link.SetWorkingDirectory(String.Equals(work.ToString(),Path.GetDirectoryName(previous),StringComparison.OrdinalIgnoreCase)?Path.GetDirectoryName(current):work.ToString());
            SetFingerprint(instance,current,fingerprint);((IPersistFile)instance).Save(path,true);
            SHChangeNotify(0x2000,0x1005,path,IntPtr.Zero);SHChangeNotify(0x1000,0x1005,Path.GetDirectoryName(path),IntPtr.Zero);return true;
        }catch{return false;}finally{if(instance!=null)Marshal.FinalReleaseComObject(instance);}
    }

    static bool TryRelocate(string path,string previous)
    { lock(LinkGate)return TryRelocateCore(path,previous); }
    static bool TryRelocateCore(string path,string previous)
    {
        object instance=null;bool located=false;
        try {
            string owned;
            if(!ReadCleanupTarget(path,out owned)||!String.Equals(owned,previous,StringComparison.OrdinalIgnoreCase))return false;
            instance=Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClass));
            ((IPersistFile)instance).Load(path,2);
            IShellLink link=(IShellLink)instance;
            StringBuilder icon=new StringBuilder(32768),work=new StringBuilder(32768);int index;
            link.GetIconLocation(icon,icon.Capacity,out index);
            link.GetWorkingDirectory(work,work.Capacity);
            // NO_UI, NOSEARCH and a one-second timeout. Do not set UPDATE: only
            // persist after validating the result; do not invoke MSI or the target.
            link.Resolve(IntPtr.Zero,(1000u<<16)|0x11u);
            string current;
            if(!ReadTarget(link,true,out current)||String.Equals(current,previous,StringComparison.OrdinalIgnoreCase))return false;
            // A located but temporarily inaccessible target, or a failed save, is
            // not deletion. Retain the original shortcut for a later retry.
            located=true;
            if(current.StartsWith(@"\\",StringComparison.Ordinal)||!File.Exists(current)||IsReparsePoint(current))return true;
            string stillOwned;
            if(!ReadCleanupTarget(path,out stillOwned)||!String.Equals(stillOwned,previous,StringComparison.OrdinalIgnoreCase))return false;
            link.SetPath(current);
            link.SetIconLocation(current,index);
            SetFingerprint(instance,current,Fingerprint(current));
            link.SetWorkingDirectory(String.Equals(work.ToString(),Path.GetDirectoryName(previous),StringComparison.OrdinalIgnoreCase)?Path.GetDirectoryName(current):work.ToString());
            ((IPersistFile)instance).Save(path,true);
            SHChangeNotify(0x2000,0x1005,path,IntPtr.Zero);
            SHChangeNotify(0x1000,0x1005,Path.GetDirectoryName(path),IntPtr.Zero);
            return true;
        }catch{return located;}
        finally{if(instance!=null)Marshal.FinalReleaseComObject(instance);}
    }

    static bool ReadOwnedTarget(string path, out string target)
    { return ReadLinkTarget(path,false,out target); }

    static bool ReadCleanupTarget(string path,out string target)
    { return ReadLinkTarget(path,true,out target); }

    static bool ReadLinkTarget(string path,bool checkIcon,out string target)
    {
        target = null;
        object instance = null;
        try {
            if (IsReparsePoint(path)) return false;
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClass));
            ((IPersistFile)instance).Load(path, 0);
            IShellLink link=(IShellLink)instance;
            if(!ReadTarget(link,true,out target))return false;
            if(checkIcon){StringBuilder icon=new StringBuilder(32768);int index;link.GetIconLocation(icon,icon.Capacity,out index);if(!String.Equals(icon.ToString(),target,StringComparison.OrdinalIgnoreCase)||index>0||index < -65535)return false;}
            return true;
        } catch { return false; }
        finally { if (instance != null) Marshal.FinalReleaseComObject(instance); }
    }

    static bool ReadTarget(IShellLink link, bool requireMarker, out string target)
    {
        target = null;
        StringBuilder description = new StringBuilder(256), arguments = new StringBuilder(32768), value = new StringBuilder(32768);
        link.GetDescription(description, description.Capacity);
        if (requireMarker && description.ToString() != Marker) return false;
        link.GetArguments(arguments, arguments.Capacity);
        if (arguments.Length != 0) return false;
        // GetPath alone never resolves or launches the shortcut's target.
        link.GetPath(value, value.Capacity, IntPtr.Zero, 4);
        string raw = value.ToString();
        if (String.IsNullOrEmpty(raw) || !Path.IsPathRooted(raw) || raw.IndexOf('%') >= 0 || !String.Equals(Path.GetExtension(raw), ".exe", StringComparison.OrdinalIgnoreCase)) return false;
        target = Path.GetFullPath(raw);
        return true;
    }

    static bool DefinitelyMissing(string target)
    {
        // A disconnected drive or inaccessible share does not prove deletion.
        if (target.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        try {
            DriveInfo drive = new DriveInfo(Path.GetPathRoot(target));
            if (!drive.IsReady || drive.DriveType == DriveType.Network) return false;
            try { File.GetAttributes(target); return false; }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
        } catch { return false; }
    }

    static bool IsReparsePoint(string path) { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
    static bool Vacant(string path)
    {
        try { File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }
}

// Windows' supported shortcut-uninstallation API, without invoking shortcut verbs.
internal static class ShellUnpin
{
    [ComImport,Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem { }
    [ComImport,Guid("4CD19ADA-25A5-4A32-B3B7-347BEE5BE36B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IStartMenuPinnedList { void RemoveFromList(IShellItem item); }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=false)]
    static extern void SHCreateItemFromParsingName(string path,IntPtr context,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out IShellItem item);
    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved,uint flags);
    [DllImport("ole32.dll")] static extern void CoUninitialize();
    internal static bool TryUnpin(string path)
    {
        IShellItem item=null;object instance=null;int initialized=CoInitializeEx(IntPtr.Zero,2);
        try {
            Guid id=typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(path,IntPtr.Zero,ref id,out item);
            instance=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("A2A9545D-A0C2-42B4-9708-A0B2BADD77C8")));
            ((IStartMenuPinnedList)instance).RemoveFromList(item);return true;
        }catch{return false;}
        finally{if(instance!=null)Marshal.FinalReleaseComObject(instance);if(item!=null)Marshal.FinalReleaseComObject(item);if(initialized>=0)CoUninitialize();}
    }
}

}

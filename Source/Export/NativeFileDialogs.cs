// Adapted from the user-owned Autonomy&Performance project; resource and Shell fixes retained.
namespace JustEnoughTools.Export {
using System;
using System.IO;
using System.Runtime.InteropServices;

// Use the current Windows file picker directly so the flags that suppress
// recent-document entries and save probes are available on .NET Framework 4.8.
// Windows can still maintain its own shell caches; these flags are not a
// promise that the operating system makes no registry writes.
public static class ExportDialogs
{
    const int Cancelled = unchecked((int)0x800704C7);
    const uint NoChangeDirectory = 0x8;
    const uint ForceFileSystem = 0x40;
    const uint PathMustExist = 0x800;
    const uint FileMustExist = 0x1000;
    const uint OverwritePrompt = 0x2;
    const uint StrictFileTypes = 0x4;
    const uint NoTestFileCreate = 0x10000;
    const uint DontAddToRecent = 0x02000000;
    const uint FileSystemPath = 0x80058000;

    static readonly Guid OpenDialogClass = new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    static readonly Guid SaveDialogClass = new Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");
    static readonly Guid ShellItemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    public static string SaveExecutable(IntPtr owner, string fileName = "Отключить защиту Windows.exe")
    {
        return Show(owner, true, fileName);
    }

    public static string SelectIcon(IntPtr owner)
    {
        return Show(owner, false);
    }

    static IFileDialog CreateDialog(bool save, string fileName)
    {
        IFileDialog dialog = (IFileDialog)Activator.CreateInstance(
            Type.GetTypeFromCLSID(save ? SaveDialogClass : OpenDialogClass, true));
        try
        {
            uint options;
            dialog.GetOptions(out options);
            options |= NoChangeDirectory | ForceFileSystem | PathMustExist | DontAddToRecent;
            if (save) options |= OverwritePrompt | StrictFileTypes | NoTestFileCreate;
            else options |= FileMustExist;
            dialog.SetOptions(options);
            dialog.SetTitle(save ? "Сохранить EXE" : "Выберите иконку");
            dialog.SetFileTypes(1, new[] { new FilterSpec {
                Name = save ? "Программа Windows (*.exe)" : "Иконка (*.ico)",
                Pattern = save ? "*.exe" : "*.ico"
            } });
            dialog.SetFileTypeIndex(1);
            dialog.SetDefaultExtension(save ? "exe" : "ico");
            if (save)
            {
                dialog.SetFileName(fileName);
                // Every save starts on the desktop, as requested. SetFolder
                // intentionally overrides any shell MRU, without erasing it.
                SetInitialFolder(dialog, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            }
            return dialog;
        }
        catch
        {
            Marshal.FinalReleaseComObject(dialog);
            throw;
        }
    }

    static void SetInitialFolder(IFileDialog dialog, string path)
    {
        if (String.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        IShellItem folder = null;
        try
        {
            Guid iid = ShellItemId;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out folder));
            dialog.SetFolder(folder);
        }
        finally
        {
            if (folder != null) Marshal.FinalReleaseComObject(folder);
        }
    }

    static string Show(IntPtr owner, bool save, string fileName = "Отключить защиту Windows.exe")
    {
        IFileDialog dialog = CreateDialog(save, fileName);
        IShellItem result = null;
        IntPtr name = IntPtr.Zero;
        try
        {
            int hr = dialog.Show(owner);
            if (hr == Cancelled) return null;
            Marshal.ThrowExceptionForHR(hr);
            dialog.GetResult(out result);
            result.GetDisplayName(FileSystemPath, out name);
            string path = Marshal.PtrToStringUni(name);
            if (String.IsNullOrEmpty(path)) throw new IOException("Не удалось получить путь выбранного файла.");
            return Path.GetFullPath(path);
        }
        finally
        {
            if (name != IntPtr.Zero) Marshal.FreeCoTaskMem(name);
            if (result != null) Marshal.FinalReleaseComObject(result);
            Marshal.FinalReleaseComObject(dialog);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr bindingContext,
        ref Guid interfaceId, out IShellItem item);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Pattern;
    }

    // The methods retain their native vtable order, including IModalWindow.Show.
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] FilterSpec[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int location);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid client);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid interfaceId, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
}

}

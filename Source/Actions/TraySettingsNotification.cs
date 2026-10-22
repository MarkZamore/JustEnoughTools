using System;
using System.Runtime.InteropServices;

namespace JustEnoughTools.Actions
{
    // Same fixed notification used by Autonomy&Performance's TaskbarControl.
    // An acknowledgement means delivery, not proof of the rendered preference.
    internal static class TraySettingsNotification
    {
        internal static IntPtr FindTaskbar() { return FindWindow("Shell_TrayWnd", null); }
        internal static bool Notify() { return Notify(FindTaskbar()); }
        internal static bool Notify(IntPtr window)
        {
            if (window == IntPtr.Zero) return false;
            IntPtr text = Marshal.StringToHGlobalUni("TraySettings");
            try { UIntPtr result; return SendMessageTimeout(window, 0x1A, UIntPtr.Zero, text, 2, 500, out result) != IntPtr.Zero; }
            finally { Marshal.FreeHGlobal(text); }
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr parameter, IntPtr data, uint flags, uint timeout, out UIntPtr result);
    }
}

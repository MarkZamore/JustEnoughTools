using System;
using System.IO;
using System.Runtime.InteropServices;

namespace JustEnoughTools.Actions
{
    public enum FixedAction { Security = 0, TaskbarMode1 = 1, TaskbarMode2 = 2 }

    internal static class FixedActionResource
    {
        internal const int ResourceId = 301;
        internal static byte[] Encode(FixedAction action)
        {
            if (action < FixedAction.Security || action > FixedAction.TaskbarMode2)
                throw new InvalidDataException("Неизвестное действие EXE.");
            return new byte[] { 74, 69, 84, 49, (byte)action, 0, 0, 0 };
        }
        internal static FixedAction Decode(byte[] value)
        {
            if (value == null || value.Length != 8 || value[0] != 74 || value[1] != 69 || value[2] != 84 ||
                value[3] != 49 || value[4] > 2 || value[5] != 0 || value[6] != 0 || value[7] != 0)
                throw new InvalidDataException("Повреждено встроенное действие EXE.");
            return (FixedAction)value[4];
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
        [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr resource);
        internal static FixedAction Read()
        {
            return Read(GetModuleHandle(null));
        }
        internal static FixedAction Read(IntPtr module)
        {
            IntPtr resource = FindResource(module, (IntPtr)ResourceId, (IntPtr)10);
            if (resource == IntPtr.Zero) throw new InvalidDataException("Во встроенных ресурсах EXE отсутствует действие.");
            if (SizeofResource(module, resource) != 8) throw new InvalidDataException("Некорректный размер действия EXE.");
            IntPtr data = LockResource(LoadResource(module, resource));
            if (data == IntPtr.Zero) throw new InvalidDataException("Действие EXE недоступно.");
            byte[] bytes = new byte[8]; Marshal.Copy(data, bytes, 0, bytes.Length);
            return Decode(bytes);
        }
    }
}

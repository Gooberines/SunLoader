using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace SunLoader
{
    // Reads IL2CPP object fields without trusting the pointer inside. Some game fields hold
    // stale pointers, and the normal interop getters crash the game when they touch one.
    static class SafeRead
    {
        // Returns the field as T, or null if it is null, unreadable, or not a T.
        public static T Field<T>(Il2CppObjectBase owner, string fieldName) where T : Il2CppObjectBase
        {
            var ptr = FieldPointer(owner, fieldName);
            return IsObjectOf<T>(ptr) ? (T)Activator.CreateInstance(typeof(T), ptr) : null;
        }

        public static string String(Il2CppObjectBase owner, string fieldName)
        {
            var ptr = FieldPointer(owner, fieldName);
            // Il2CppInterop doesn't fill in the class pointer for Il2CppSystem.String, so
            // IsObjectOf can't be used here; match the class name instead.
            return ClassName(ptr) == "String" ? IL2CPP.Il2CppStringToManaged(ptr) : null;
        }

        // Reads element i of a List<T> straight from its backing array (_items).
        public static IntPtr ListItemPointer(Il2CppObjectBase list, int index)
        {
            var items = FieldPointer(list, "_items");
            // IL2CPP array layout on x64: klass, monitor, bounds, max_length, then elements.
            const int LengthOffset = 24, DataOffset = 32;
            if (!IsReadable(items, DataOffset)) return IntPtr.Zero;
            var length = Marshal.ReadInt64(items + LengthOffset);
            if (index < 0 || index >= length) return IntPtr.Zero;
            var slot = items + DataOffset + index * IntPtr.Size;
            return IsReadable(slot, IntPtr.Size) ? Marshal.ReadIntPtr(slot) : IntPtr.Zero;
        }

        public static IntPtr FieldPointer(Il2CppObjectBase owner, string fieldName)
        {
            if (owner == null || !IsReadable(owner.Pointer, 16)) return IntPtr.Zero;
            var klass = Marshal.ReadIntPtr(owner.Pointer);
            var field = IntPtr.Zero;
            // Walk up the class chain: the field may be declared on a parent class.
            for (var k = klass; k != IntPtr.Zero && field == IntPtr.Zero; k = IL2CPP.il2cpp_class_get_parent(k))
                field = IL2CPP.il2cpp_class_get_field_from_name(k, fieldName);
            if (field == IntPtr.Zero)
            {
                Plugin.Logger.LogWarning($"No field '{fieldName}' on {owner.GetType().Name}");
                return IntPtr.Zero;
            }
            var slot = owner.Pointer + (int)IL2CPP.il2cpp_field_get_offset(field);
            return IsReadable(slot, IntPtr.Size) ? Marshal.ReadIntPtr(slot) : IntPtr.Zero;
        }

        public static bool IsObjectOf<T>(IntPtr ptr) where T : Il2CppObjectBase
        {
            if (ptr == IntPtr.Zero || !IsReadable(ptr, 16)) return false;
            var klass = Marshal.ReadIntPtr(ptr);
            if (klass == IntPtr.Zero || !IsReadable(klass, 64)) return false;
            var expected = Il2CppClassPointerStore<T>.NativeClassPtr;
            return expected != IntPtr.Zero && IL2CPP.il2cpp_class_is_assignable_from(expected, klass);
        }

        // Class name of the object at ptr, for debug logs. Never throws.
        public static string ClassName(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return "null";
            if (!IsReadable(ptr, 16)) return "unreadable";
            var klass = Marshal.ReadIntPtr(ptr);
            if (!IsReadable(klass, 64)) return "bad class";
            var name = IL2CPP.il2cpp_class_get_name(klass);
            return name == IntPtr.Zero ? "?" : Marshal.PtrToStringAnsi(name);
        }

        public static bool IsReadable(IntPtr ptr, int size)
        {
            if (ptr == IntPtr.Zero) return false;
            if (VirtualQuery(ptr, out var info, (UIntPtr)Marshal.SizeOf<MemoryInfo>()) == UIntPtr.Zero) return false;
            const uint Commit = 0x1000, NoAccess = 0x01, Guard = 0x100;
            const uint Readable = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;
            if (info.State != Commit || (info.Protect & (NoAccess | Guard)) != 0 || (info.Protect & Readable) == 0) return false;
            var end = (long)info.BaseAddress + (long)info.RegionSize;
            return (long)ptr + size <= end;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MemoryInfo
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll")]
        static extern UIntPtr VirtualQuery(IntPtr address, out MemoryInfo info, UIntPtr length);
    }
}

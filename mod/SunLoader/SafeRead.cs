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

        // ---------- pointer-only reads ----------
        // These work on raw object pointers and never create interop wrappers or call game code,
        // for reading thousands of objects at once (a level's attacks) without any churn.

        static readonly System.Collections.Generic.Dictionary<(IntPtr klass, string name), int> offsets = new();

        // Byte offset of a field in the object's class (or a parent class), or -1.
        static int Offset(IntPtr obj, string fieldName)
        {
            if (!IsReadable(obj, 16)) return -1;
            var klass = Marshal.ReadIntPtr(obj);
            if (klass == IntPtr.Zero) return -1;
            if (offsets.TryGetValue((klass, fieldName), out var offset)) return offset;
            if (!IsReadable(klass, 64)) return -1;
            var field = IntPtr.Zero;
            for (var k = klass; k != IntPtr.Zero && field == IntPtr.Zero; k = IL2CPP.il2cpp_class_get_parent(k))
                field = IL2CPP.il2cpp_class_get_field_from_name(k, fieldName);
            offset = field == IntPtr.Zero ? -1 : (int)IL2CPP.il2cpp_field_get_offset(field);
            offsets[(klass, fieldName)] = offset;
            return offset;
        }

        static IntPtr Slot(IntPtr obj, string fieldName, int size)
        {
            var offset = Offset(obj, fieldName);
            if (offset < 0) return IntPtr.Zero;
            var slot = obj + offset;
            return IsReadable(slot, size) ? slot : IntPtr.Zero;
        }

        public static IntPtr Pointer(IntPtr obj, string fieldName)
        {
            var slot = Slot(obj, fieldName, IntPtr.Size);
            return slot == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(slot);
        }

        public static string String(IntPtr obj, string fieldName)
        {
            var ptr = Pointer(obj, fieldName);
            return ClassName(ptr) == "String" ? IL2CPP.Il2CppStringToManaged(ptr) : null;
        }

        public static bool Bool(IntPtr obj, string fieldName)
        {
            var slot = Slot(obj, fieldName, 1);
            return slot != IntPtr.Zero && Marshal.ReadByte(slot) != 0;
        }

        public static float Float(IntPtr obj, string fieldName)
        {
            var slot = Slot(obj, fieldName, 4);
            return slot == IntPtr.Zero ? 0 : BitConverter.Int32BitsToSingle(Marshal.ReadInt32(slot));
        }

        public static int Int(IntPtr obj, string fieldName)
        {
            var slot = Slot(obj, fieldName, 4);
            return slot == IntPtr.Zero ? -1 : Marshal.ReadInt32(slot);
        }

        // A List<T>'s length and items, read from its _size and _items fields.
        public static int ListCount(IntPtr list) => ClassName(list).StartsWith("List") ? Int(list, "_size") : -1;

        public static IntPtr ListItem(IntPtr list, int index)
        {
            var items = Pointer(list, "_items");
            const int LengthOffset = 24, DataOffset = 32;
            if (!IsReadable(items, DataOffset)) return IntPtr.Zero;
            var length = Marshal.ReadInt64(items + LengthOffset);
            if (index < 0 || index >= length) return IntPtr.Zero;
            var slot = items + DataOffset + index * IntPtr.Size;
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

        // Asking Windows about memory is a system call, too slow for the ~100,000 reads of a big
        // level. During a batch (one synchronous read of a level on the main thread, when nothing
        // frees memory), readable regions already checked are remembered.
        static readonly (long start, long end)[] regions = new (long, long)[32];
        static int regionCount, regionNext;
        static bool batching;

        public static void BeginBatch() { batching = true; regionCount = regionNext = 0; }
        public static void EndBatch() { batching = false; regionCount = regionNext = 0; }

        public static bool IsReadable(IntPtr ptr, int size)
        {
            if (ptr == IntPtr.Zero) return false;
            long p = (long)ptr, pEnd = p + size;
            if (batching)
                for (int i = 0; i < regionCount; i++)
                    if (p >= regions[i].start && pEnd <= regions[i].end) return true;
            if (VirtualQuery(ptr, out var info, (UIntPtr)Marshal.SizeOf<MemoryInfo>()) == UIntPtr.Zero) return false;
            const uint Commit = 0x1000, NoAccess = 0x01, Guard = 0x100;
            const uint Readable = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;
            if (info.State != Commit || (info.Protect & (NoAccess | Guard)) != 0 || (info.Protect & Readable) == 0) return false;
            var end = (long)info.BaseAddress + (long)info.RegionSize;
            if (batching)
            {
                regions[regionNext] = ((long)info.BaseAddress, end);
                regionNext = (regionNext + 1) % regions.Length;
                regionCount = Math.Max(regionCount, regionNext == 0 ? regions.Length : regionNext);
            }
            return pEnd <= end;
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

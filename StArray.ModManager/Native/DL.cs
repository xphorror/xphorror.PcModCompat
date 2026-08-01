using System.Globalization;
using System.Runtime.InteropServices;

namespace StArray.ModManager.Native;

/// <summary>跨 Android/Linux 的动态库查询 facade。</summary>
public static class DL
{
    [DllImport("dl", EntryPoint = "dlopen")]
    private static extern IntPtr OpenRaw(string fileName, RTLDFlags flags);
    [DllImport("dl", EntryPoint = "dlsym")]
    private static extern IntPtr SymbolRaw(IntPtr handle, string symbol);
    [DllImport("dl", EntryPoint = "dlclose")]
    private static extern int CloseRaw(IntPtr handle);
    [DllImport("dl", EntryPoint = "dlerror")]
    private static extern IntPtr ErrorRaw();

    [DllImport("dl", EntryPoint = "dladdr")]
    private static extern int AddrRaw(IntPtr address, ref DlInfo info);

    [DllImport("dl", EntryPoint = "dl_iterate_phdr")]
    private static extern int IterateRaw(IterateCallback callback, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int IterateCallback(IntPtr info, int size, IntPtr data);

    public static IntPtr Open(string fileName, RTLDFlags flags)
    {
        var loaded = GetBaseAddress(fileName);
        return loaded != IntPtr.Zero ? loaded : OpenRaw(fileName, flags);
    }

    public static IntPtr Symbol(IntPtr handle, string symbol) => SymbolRaw(handle, symbol);
    public static int Close(IntPtr handle) => CloseRaw(handle);
    public static IntPtr Error() => ErrorRaw();
    public static int Addr(IntPtr address, ref DlInfo info) => AddrRaw(address, ref info);

    public static IntPtr GetBaseAddress(string library)
    {
        if (!OperatingSystem.IsAndroid() && !OperatingSystem.IsLinux())
            return IntPtr.Zero;
        var name = library.EndsWith(".so", StringComparison.Ordinal) ? library : library + ".so";
        foreach (var line in File.ReadLines("/proc/self/maps"))
        {
            if (!line.EndsWith(name, StringComparison.Ordinal)) continue;
            var dash = line.IndexOf('-');
            if (dash >= 0 && long.TryParse(line.AsSpan(0, dash), NumberStyles.HexNumber, null, out var address))
                return (IntPtr)address;
        }
        return IntPtr.Zero;
    }

    public static IntPtr IteratePhdr(string library)
    {
        if (!OperatingSystem.IsAndroid() && !OperatingSystem.IsLinux())
            return IntPtr.Zero;
        var name = library.EndsWith(".so", StringComparison.Ordinal) ? library : library + ".so";
        IntPtr found = IntPtr.Zero;
        IterateRaw((info, _, _) =>
        {
            var namePtr = Marshal.ReadIntPtr(info, IntPtr.Size);
            var loadedName = Marshal.PtrToStringAnsi(namePtr);
            if (loadedName != null && loadedName.EndsWith(name, StringComparison.Ordinal))
            {
                found = Marshal.ReadIntPtr(info);
                return 1;
            }
            return 0;
        }, IntPtr.Zero);
        return found;
    }

    public static bool IteratePhdr(string library, Func<IntPtr, IntPtr, int, bool> onMatch)
    {
        if (!OperatingSystem.IsAndroid() && !OperatingSystem.IsLinux()) return false;
        var name = library.EndsWith(".so", StringComparison.Ordinal) ? library : library + ".so";
        var matched = false;
        IterateRaw((info, _, _) =>
        {
            var namePtr = Marshal.ReadIntPtr(info, IntPtr.Size);
            var loadedName = Marshal.PtrToStringAnsi(namePtr);
            if (loadedName == null || !loadedName.EndsWith(name, StringComparison.Ordinal)) return 0;
            var baseAddress = Marshal.ReadIntPtr(info);
            var phdr = Marshal.ReadIntPtr(info, IntPtr.Size * 2);
            var phnum = Marshal.ReadInt16(info, IntPtr.Size * 2 + 8);
            if (!onMatch(baseAddress, phdr, phnum)) return 0;
            matched = true;
            return 1;
        }, IntPtr.Zero);
        return matched;
    }

    public static long FindLoadVaddr(IntPtr phdr, int phnum)
    {
        for (var i = 0; i < phnum; i++)
        {
            var offset = i * 56;
            if (Marshal.ReadInt32(phdr, offset) == 1)
                return Marshal.ReadInt64(phdr, offset + 16);
        }
        return -1;
    }

    [Flags]
    public enum RTLDFlags
    {
        RTLD_LOCAL = 0,
        RTLD_LAZY = 1,
        RTLD_NOW = 2,
        RTLD_NOLOAD = 4,
        RTLD_GLOBAL = 0x100,
    }

    public struct DlInfo
    {
        public IntPtr dli_fname;
        public IntPtr dli_fbase;
        public IntPtr dli_sname;
        public IntPtr dli_saddr;
    }
}

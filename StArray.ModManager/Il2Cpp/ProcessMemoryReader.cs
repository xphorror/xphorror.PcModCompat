using System.Runtime.InteropServices;

namespace StArray.ModManager.Il2Cpp;

internal interface IProcessMemoryReader
{
    bool TryRead(nint address, Span<byte> destination);
    bool TryReadPointer(nint address, out nint value);
    bool IsReadable(nint address, nuint size);
}

internal sealed unsafe class NativeProcessMemoryReader : IProcessMemoryReader
{
    internal static IProcessMemoryReader Instance { get; } = new NativeProcessMemoryReader();

    private NativeProcessMemoryReader()
    {
    }

    public bool TryRead(nint address, Span<byte> destination)
    {
        if (address == nint.Zero || destination.IsEmpty)
            return false;
        fixed (byte* output = destination)
            return TryReadNative(address, output, (nuint)destination.Length) != 0;
    }

    public bool TryReadPointer(nint address, out nint value)
    {
        nint local = nint.Zero;
        var result = TryReadNative(address, &local, (nuint)nint.Size) != 0;
        value = local;
        return result;
    }

    public bool IsReadable(nint address, nuint size)
    {
        if (size == 0 || size > 64)
            return false;
        var output = stackalloc byte[checked((int)size)];
        return TryReadNative(address, output, size) != 0;
    }

    [DllImport(
        "starray_modmanager",
        EntryPoint = "modmanager_try_read_process_memory",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int TryReadNative(nint address, void* output, nuint size);
}

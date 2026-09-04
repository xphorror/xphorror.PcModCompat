using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace StArray.ModManager.Il2Cpp;

internal static class Il2CppStringReader
{
    private const int MaximumLength = 1_048_576;

    internal static bool TryRead(nint stringObject, out string value)
    {
        try
        {
            return TryRead(stringObject, NativeProcessMemoryReader.Instance, out value);
        }
        catch (Exception exception) when (exception is
            DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            OverflowException)
        {
            value = string.Empty;
            return false;
        }
    }

    internal static bool TryRead(
        nint stringObject,
        IProcessMemoryReader reader,
        out string value)
    {
        value = string.Empty;
        if (!TryReadMetadata(stringObject, reader, out var length, out var charsAddress))
            return false;
        if (length == 0)
            return true;

        int byteCount;
        try
        {
            byteCount = checked(length * sizeof(char));
        }
        catch (OverflowException)
        {
            return false;
        }

        var bytes = new byte[byteCount];
        if (!reader.TryRead(charsAddress, bytes))
            return false;
        value = new string(MemoryMarshal.Cast<byte, char>(bytes));
        return true;
    }

    internal static bool TryReadLength(nint stringObject, out int length)
    {
        try
        {
            return TryReadMetadata(
                stringObject,
                NativeProcessMemoryReader.Instance,
                out length,
                out _);
        }
        catch (Exception exception) when (exception is
            DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            OverflowException)
        {
            length = 0;
            return false;
        }
    }

    internal static bool TryGetCharsAddress(nint stringObject, out nint charsAddress)
    {
        try
        {
            return TryReadMetadata(
                stringObject,
                NativeProcessMemoryReader.Instance,
                out _,
                out charsAddress);
        }
        catch (Exception exception) when (exception is
            DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            OverflowException)
        {
            charsAddress = nint.Zero;
            return false;
        }
    }

    private static bool TryReadMetadata(
        nint stringObject,
        IProcessMemoryReader reader,
        out int length,
        out nint charsAddress)
    {
        length = 0;
        charsAddress = nint.Zero;
        var lengthOffset = nint.Size * 2;
        var charsOffset = lengthOffset + sizeof(int);
        if (stringObject <= nint.Zero ||
            !IsAligned(stringObject, nint.Size) ||
            !TryAdd(stringObject, charsOffset, out charsAddress))
        {
            charsAddress = nint.Zero;
            return false;
        }

        Span<byte> header = stackalloc byte[charsOffset];
        if (!reader.TryRead(stringObject, header))
            return false;

        var klass = nint.Size == sizeof(long)
            ? unchecked((nint)BinaryPrimitives.ReadInt64LittleEndian(header))
            : unchecked((nint)BinaryPrimitives.ReadInt32LittleEndian(header));
        if (klass <= nint.Zero ||
            !IsAligned(klass, nint.Size))
        {
            return false;
        }

        length = BinaryPrimitives.ReadInt32LittleEndian(header[lengthOffset..]);
        if (length is < 0 or > MaximumLength)
        {
            length = 0;
            return false;
        }
        return true;
    }

    private static bool TryAdd(nint address, int offset, out nint result)
    {
        try
        {
            result = checked(address + offset);
            return result > address;
        }
        catch (OverflowException)
        {
            result = nint.Zero;
            return false;
        }
    }

    private static bool IsAligned(nint value, int alignment)
        => ((nuint)value & (nuint)(alignment - 1)) == 0;
}

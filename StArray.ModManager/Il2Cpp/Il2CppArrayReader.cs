using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace StArray.ModManager.Il2Cpp;

internal static class Il2CppArrayReader
{
    internal static bool TryReadLength(nint array, out int length)
    {
        length = 0;
        try
        {
            return TryReadMetadata(
                array,
                NativeProcessMemoryReader.Instance,
                out length,
                out _);
        }
        catch (Exception exception) when (IsNativeBoundaryException(exception))
        {
            length = 0;
            return false;
        }
    }

    internal static bool TryGetDataAddress(nint array, out nint dataAddress)
    {
        dataAddress = nint.Zero;
        try
        {
            return TryReadMetadata(
                array,
                NativeProcessMemoryReader.Instance,
                out _,
                out dataAddress);
        }
        catch (Exception exception) when (IsNativeBoundaryException(exception))
        {
            dataAddress = nint.Zero;
            return false;
        }
    }

    internal static bool TryReadPointerElement(nint array, int index, out nint value)
    {
        value = nint.Zero;
        try
        {
            return TryReadPointerElement(
                array,
                index,
                NativeProcessMemoryReader.Instance,
                out value);
        }
        catch (Exception exception) when (IsNativeBoundaryException(exception))
        {
            value = nint.Zero;
            return false;
        }
    }

    internal static bool TryReadPointerElement(
        nint array,
        int index,
        IProcessMemoryReader reader,
        out nint value)
    {
        value = nint.Zero;
        if (!TryGetElementAddress(array, index, nint.Size, reader, out var address))
            return false;
        return reader.TryReadPointer(address, out value);
    }

    internal static bool TryReadValueElement<T>(nint array, int index, out T value)
        where T : unmanaged
    {
        value = default;
        try
        {
            var reader = NativeProcessMemoryReader.Instance;
            if (!TryGetElementAddress(array, index, Unsafe.SizeOf<T>(), reader, out var address))
                return false;
            Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<T>()];
            if (!reader.TryRead(address, bytes))
                return false;
            value = MemoryMarshal.Read<T>(bytes);
            return true;
        }
        catch (Exception exception) when (IsNativeBoundaryException(exception))
        {
            value = default;
            return false;
        }
    }

    internal static bool TryReadMetadata(
        nint array,
        IProcessMemoryReader reader,
        out int length,
        out nint dataAddress)
    {
        length = 0;
        dataAddress = nint.Zero;
        var boundsOffset = nint.Size * 2;
        var lengthOffset = nint.Size * 3;
        var dataOffset = nint.Size * 4;
        if (array <= nint.Zero ||
            !IsAligned(array, nint.Size) ||
            !TryAdd(array, dataOffset, out dataAddress))
        {
            dataAddress = nint.Zero;
            return false;
        }

        Span<byte> header = stackalloc byte[dataOffset];
        if (!reader.TryRead(array, header))
            return false;
        var klass = ReadPointer(header);
        var bounds = ReadPointer(header[boundsOffset..]);
        if (klass <= nint.Zero ||
            !IsAligned(klass, nint.Size) ||
            (bounds != nint.Zero && !IsAligned(bounds, nint.Size)))
        {
            return false;
        }

        var nativeLength = nint.Size == sizeof(long)
            ? BinaryPrimitives.ReadUInt64LittleEndian(header[lengthOffset..])
            : BinaryPrimitives.ReadUInt32LittleEndian(header[lengthOffset..]);
        if (nativeLength > int.MaxValue)
            return false;
        length = (int)nativeLength;
        return true;
    }

    private static bool TryGetElementAddress(
        nint array,
        int index,
        int elementSize,
        IProcessMemoryReader reader,
        out nint address)
    {
        address = nint.Zero;
        if (index < 0 || elementSize <= 0 ||
            !TryReadMetadata(array, reader, out var length, out var dataAddress) ||
            index >= length)
        {
            return false;
        }
        try
        {
            var offset = checked(index * elementSize);
            return TryAdd(dataAddress, offset, out address);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static nint ReadPointer(ReadOnlySpan<byte> bytes)
        => nint.Size == sizeof(long)
            ? unchecked((nint)BinaryPrimitives.ReadInt64LittleEndian(bytes))
            : unchecked((nint)BinaryPrimitives.ReadInt32LittleEndian(bytes));

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

    private static bool IsNativeBoundaryException(Exception exception)
        => exception is DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            OverflowException;
}

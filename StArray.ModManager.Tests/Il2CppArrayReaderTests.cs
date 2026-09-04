using System.Buffers.Binary;
using StArray.ModManager.Il2Cpp;

namespace StArray.ModManager.Tests;

public sealed class Il2CppArrayReaderTests
{
    [Test]
    public void ReadsLengthAndPointerElementsThroughTheSafeReader()
    {
        var reader = CreateArray(2);
        var array = (nint)0x1000;
        reader.WritePointer(array + nint.Size * 4, (nint)0x5000);
        reader.WritePointer(array + nint.Size * 5, (nint)0x6000);

        Assert.Multiple(() =>
        {
            Assert.That(
                Il2CppArrayReader.TryReadMetadata(array, reader, out var length, out var data),
                Is.True);
            Assert.That(length, Is.EqualTo(2));
            Assert.That(data, Is.EqualTo(array + nint.Size * 4));
            Assert.That(
                Il2CppArrayReader.TryReadPointerElement(array, 1, reader, out var value),
                Is.True);
            Assert.That(value, Is.EqualTo((nint)0x6000));
        });
    }

    [Test]
    public void RejectsUnreadableAndOversizedArrays()
    {
        var unreadable = new FakeMemoryReader();
        var oversized = CreateArray((ulong)int.MaxValue + 1);

        Assert.Multiple(() =>
        {
            Assert.That(Il2CppArrayReader.TryReadMetadata(
                (nint)0x1000, unreadable, out _, out _), Is.False);
            Assert.That(Il2CppArrayReader.TryReadMetadata(
                (nint)0x1000, oversized, out _, out _), Is.False);
        });
    }

    private static FakeMemoryReader CreateArray(ulong length)
    {
        var reader = new FakeMemoryReader();
        var array = (nint)0x1000;
        reader.WritePointer(array, (nint)0x4000);
        reader.WritePointer(array + nint.Size, nint.Zero);
        reader.WritePointer(array + nint.Size * 2, nint.Zero);
        reader.WriteNativeUInt(array + nint.Size * 3, length);
        return reader;
    }

    private sealed class FakeMemoryReader : IProcessMemoryReader
    {
        private readonly Dictionary<nint, byte> _memory = [];

        public bool TryRead(nint address, Span<byte> destination)
        {
            for (var index = 0; index < destination.Length; ++index)
            {
                if (!_memory.TryGetValue(address + index, out destination[index]))
                    return false;
            }
            return destination.Length > 0;
        }

        public bool TryReadPointer(nint address, out nint value)
        {
            Span<byte> bytes = stackalloc byte[nint.Size];
            if (!TryRead(address, bytes))
            {
                value = nint.Zero;
                return false;
            }
            value = nint.Size == sizeof(long)
                ? unchecked((nint)BinaryPrimitives.ReadInt64LittleEndian(bytes))
                : unchecked((nint)BinaryPrimitives.ReadInt32LittleEndian(bytes));
            return true;
        }

        public bool IsReadable(nint address, nuint size) => false;

        internal void WritePointer(nint address, nint value)
            => WriteNativeUInt(address, unchecked((nuint)value));

        internal void WriteNativeUInt(nint address, nuint value)
            => WriteNativeUInt(address, (ulong)value);

        internal void WriteNativeUInt(nint address, ulong value)
        {
            Span<byte> bytes = stackalloc byte[nint.Size];
            if (nint.Size == sizeof(long))
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            else
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)value));
            for (var index = 0; index < bytes.Length; ++index)
                _memory[address + index] = bytes[index];
        }
    }
}

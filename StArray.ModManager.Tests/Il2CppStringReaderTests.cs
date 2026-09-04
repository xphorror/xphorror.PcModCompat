using System.Buffers.Binary;
using System.Text;
using StArray.ModManager.Il2Cpp;

namespace StArray.ModManager.Tests;

public sealed class Il2CppStringReaderTests
{
    [Test]
    public void CopiesAValidIl2CppStringWithoutDereferencingManagedPointers()
    {
        var reader = new FakeMemoryReader();
        var stringObject = (nint)0x1000;
        var klass = (nint)0x4000;
        reader.WritePointer(stringObject, klass);
        reader.WritePointer(stringObject + nint.Size, nint.Zero);
        reader.WriteInt32(stringObject + nint.Size * 2, 6);
        reader.WriteBytes(
            stringObject + nint.Size * 2 + sizeof(int),
            Encoding.Unicode.GetBytes("Replay"));
        reader.WriteBytes(klass, new byte[nint.Size]);

        Assert.That(
            Il2CppStringReader.TryRead(stringObject, reader, out var value),
            Is.True);
        Assert.That(value, Is.EqualTo("Replay"));
    }

    [Test]
    public void RejectsAnUnreadableCharacterRange()
    {
        var reader = CreateHeader(length: 8);

        Assert.That(
            Il2CppStringReader.TryRead((nint)0x1000, reader, out var value),
            Is.False);
        Assert.That(value, Is.Empty);
    }

    [TestCase(-1)]
    [TestCase(1_048_577)]
    public void RejectsInvalidLengthsBeforeAllocating(int length)
    {
        var reader = CreateHeader(length);

        Assert.That(
            Il2CppStringReader.TryRead((nint)0x1000, reader, out var value),
            Is.False);
        Assert.That(value, Is.Empty);
    }

    [Test]
    public void RejectsAStaleOrUnalignedClassPointer()
    {
        var reader = new FakeMemoryReader();
        var stringObject = (nint)0x1000;
        reader.WritePointer(stringObject, (nint)0x4001);
        reader.WriteInt32(stringObject + nint.Size * 2, 0);

        Assert.That(
            Il2CppStringReader.TryRead(stringObject, reader, out var value),
            Is.False);
        Assert.That(value, Is.Empty);
    }

    [Test]
    public void RejectsAnAddressWhoseHeaderWouldOverflow()
    {
        var reader = new FakeMemoryReader();
        var address = nint.MaxValue - 7;

        Assert.That(
            Il2CppStringReader.TryRead(address, reader, out var value),
            Is.False);
        Assert.That(value, Is.Empty);
    }

    private static FakeMemoryReader CreateHeader(int length)
    {
        var reader = new FakeMemoryReader();
        var stringObject = (nint)0x1000;
        var klass = (nint)0x4000;
        reader.WritePointer(stringObject, klass);
        reader.WritePointer(stringObject + nint.Size, nint.Zero);
        reader.WriteInt32(stringObject + nint.Size * 2, length);
        reader.WriteBytes(klass, new byte[nint.Size]);
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
                ? (nint)BinaryPrimitives.ReadInt64LittleEndian(bytes)
                : (nint)BinaryPrimitives.ReadInt32LittleEndian(bytes);
            return true;
        }

        public bool IsReadable(nint address, nuint size)
        {
            if (size == 0 || size > int.MaxValue)
                return false;
            Span<byte> destination = stackalloc byte[checked((int)size)];
            return TryRead(address, destination);
        }

        internal void WritePointer(nint address, nint value)
        {
            Span<byte> bytes = stackalloc byte[nint.Size];
            if (nint.Size == sizeof(long))
                BinaryPrimitives.WriteInt64LittleEndian(bytes, (long)value);
            else
                BinaryPrimitives.WriteInt32LittleEndian(bytes, (int)value);
            WriteBytes(address, bytes);
        }

        internal void WriteInt32(nint address, int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            WriteBytes(address, bytes);
        }

        internal void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            for (var index = 0; index < bytes.Length; ++index)
                _memory[address + index] = bytes[index];
        }
    }
}

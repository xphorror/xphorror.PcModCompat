using System.Buffers.Binary;

namespace StArray.ModManager.Runtime;

internal readonly record struct NativeModElfIdentity(
    byte ElfClass,
    byte DataEncoding,
    byte OsAbi,
    byte AbiVersion,
    ushort Machine,
    string BuildId);

internal readonly record struct NativeModUnmanagedLibraryIdentity(
    string ModId,
    long LoadGeneration,
    string RequestedName,
    string CanonicalPath,
    nint DlopenHandle,
    nint BaseAddress,
    NativeModElfIdentity Elf,
    bool ObservedOutOfBand,
    bool ContextRetired);

internal readonly record struct NativeModMappedLibrary(
    string CanonicalPath,
    nint BaseAddress);

internal static class NativeModProcessMapReader
{
    private const string DeletedSuffix = " (deleted)";

    internal static IReadOnlyList<NativeModMappedLibrary> ReadUnder(string rootDirectory)
    {
        if (!File.Exists("/proc/self/maps"))
            return Array.Empty<NativeModMappedLibrary>();
        try
        {
            using var reader = new StreamReader(
                "/proc/self/maps",
                System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096);
            return Parse(reader, rootDirectory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or
            System.Security.SecurityException)
        {
            return Array.Empty<NativeModMappedLibrary>();
        }
    }

    internal static IReadOnlyList<NativeModMappedLibrary> Parse(
        TextReader reader,
        string rootDirectory)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var root = Path.GetFullPath(rootDirectory);
        var mappings = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!TryParseLine(line, root, out var path, out var baseAddress))
                continue;
            if (!mappings.TryGetValue(path, out var current) || baseAddress < current)
                mappings[path] = baseAddress;
        }
        return mappings
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new NativeModMappedLibrary(
                pair.Key,
                checked((nint)pair.Value)))
            .ToArray();
    }

    private static bool TryParseLine(
        string line,
        string root,
        out string canonicalPath,
        out ulong baseAddress)
    {
        canonicalPath = string.Empty;
        baseAddress = 0;
        var offset = 0;
        if (!TryReadToken(line, ref offset, out var range) ||
            !TryReadToken(line, ref offset, out _) ||
            !TryReadToken(line, ref offset, out var fileOffsetText) ||
            !TryReadToken(line, ref offset, out _) ||
            !TryReadToken(line, ref offset, out _))
        {
            return false;
        }
        while (offset < line.Length && char.IsWhiteSpace(line[offset]))
            offset++;
        if (offset >= line.Length)
            return false;

        var path = line[offset..].Trim();
        if (path.EndsWith(DeletedSuffix, StringComparison.Ordinal))
            path = path[..^DeletedSuffix.Length];
        var dash = range.IndexOf('-');
        if (dash <= 0 || dash == range.Length - 1 ||
            !ulong.TryParse(
                range.AsSpan(0, dash),
                System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture,
                out var start) ||
            !ulong.TryParse(
                fileOffsetText,
                System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture,
                out var fileOffset) ||
            start < fileOffset)
        {
            return false;
        }

        try
        {
            canonicalPath = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }
        if (!IsPathWithinRoot(canonicalPath, root) ||
            Path.GetFileName(canonicalPath).IndexOf(".so", StringComparison.OrdinalIgnoreCase) < 0)
        {
            canonicalPath = string.Empty;
            return false;
        }
        baseAddress = start - fileOffset;
        return baseAddress != 0;
    }

    private static bool TryReadToken(
        string line,
        ref int offset,
        out string token)
    {
        while (offset < line.Length && char.IsWhiteSpace(line[offset]))
            offset++;
        var start = offset;
        while (offset < line.Length && !char.IsWhiteSpace(line[offset]))
            offset++;
        token = line[start..offset];
        return token.Length != 0;
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}

internal static class NativeModElfIdentityReader
{
    private const byte ElfClass64 = 2;
    private const byte ElfDataLittleEndian = 1;
    private const uint ProgramHeaderNote = 4;
    private const uint GnuBuildIdNote = 3;
    private const int Elf64HeaderSize = 64;
    private const int Elf64ProgramHeaderSize = 56;
    private const int MaxProgramHeaderEntrySize = 4096;
    private const int MaxProgramHeaderCount = 4096;
    private const int MaxNoteSegmentSize = 1024 * 1024;
    private const int MaxTotalNoteBytes = 4 * 1024 * 1024;

    public static bool TryRead(string path, out NativeModElfIdentity identity)
    {
        identity = default;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            Span<byte> header = stackalloc byte[Elf64HeaderSize];
            if (!ReadExactly(stream, header) ||
                header[0] != 0x7f || header[1] != (byte)'E' ||
                header[2] != (byte)'L' || header[3] != (byte)'F' ||
                header[4] != ElfClass64 ||
                header[5] != ElfDataLittleEndian)
            {
                return false;
            }

            var programHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[32..40]);
            var programHeaderEntrySize = BinaryPrimitives.ReadUInt16LittleEndian(header[54..56]);
            var programHeaderCount = BinaryPrimitives.ReadUInt16LittleEndian(header[56..58]);
            if (programHeaderEntrySize < Elf64ProgramHeaderSize ||
                programHeaderEntrySize > MaxProgramHeaderEntrySize ||
                programHeaderCount == 0 ||
                programHeaderCount > MaxProgramHeaderCount ||
                programHeaderOffset > (ulong)stream.Length)
            {
                return false;
            }

            var buildId = string.Empty;
            var programHeader = new byte[programHeaderEntrySize];
            var remainingNoteBytes = MaxTotalNoteBytes;
            for (var index = 0; index < programHeaderCount; ++index)
            {
                var entryOffset = checked(
                    programHeaderOffset + (ulong)index * programHeaderEntrySize);
                if (entryOffset > (ulong)stream.Length ||
                    (ulong)programHeaderEntrySize > (ulong)stream.Length - entryOffset)
                {
                    return false;
                }

                stream.Position = checked((long)entryOffset);
                if (!ReadExactly(stream, programHeader))
                    return false;
                if (BinaryPrimitives.ReadUInt32LittleEndian(programHeader.AsSpan(0, 4)) !=
                    ProgramHeaderNote)
                {
                    continue;
                }

                var noteOffset = BinaryPrimitives.ReadUInt64LittleEndian(
                    programHeader.AsSpan(8, 8));
                var noteSize = BinaryPrimitives.ReadUInt64LittleEndian(
                    programHeader.AsSpan(32, 8));
                if (noteSize == 0 || noteSize > MaxNoteSegmentSize ||
                    noteOffset > (ulong)stream.Length ||
                    noteSize > (ulong)stream.Length - noteOffset)
                {
                    continue;
                }
                if (noteSize > (ulong)remainingNoteBytes)
                    break;
                remainingNoteBytes -= checked((int)noteSize);

                var notes = new byte[checked((int)noteSize)];
                stream.Position = checked((long)noteOffset);
                if (!ReadExactly(stream, notes))
                    continue;
                if (TryReadGnuBuildId(notes, out buildId))
                    break;
            }

            identity = new NativeModElfIdentity(
                header[4],
                header[5],
                header[7],
                header[8],
                BinaryPrimitives.ReadUInt16LittleEndian(header[18..20]),
                buildId);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or OverflowException or
            System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool TryReadGnuBuildId(ReadOnlySpan<byte> notes, out string buildId)
    {
        buildId = string.Empty;
        var offset = 0;
        while (notes.Length - offset >= 12)
        {
            var nameSize = BinaryPrimitives.ReadUInt32LittleEndian(notes.Slice(offset, 4));
            var descriptorSize = BinaryPrimitives.ReadUInt32LittleEndian(notes.Slice(offset + 4, 4));
            var type = BinaryPrimitives.ReadUInt32LittleEndian(notes.Slice(offset + 8, 4));
            offset += 12;

            if (!TryAlign4(nameSize, out var alignedNameSize) ||
                !TryAlign4(descriptorSize, out var alignedDescriptorSize) ||
                alignedNameSize > (uint)(notes.Length - offset))
            {
                return false;
            }

            var name = notes.Slice(offset, checked((int)nameSize));
            offset += checked((int)alignedNameSize);
            if (alignedDescriptorSize > (uint)(notes.Length - offset))
                return false;
            var descriptor = notes.Slice(offset, checked((int)descriptorSize));
            offset += checked((int)alignedDescriptorSize);

            if (type == GnuBuildIdNote &&
                name.Length >= 3 &&
                name[0] == (byte)'G' &&
                name[1] == (byte)'N' &&
                name[2] == (byte)'U' &&
                !descriptor.IsEmpty)
            {
                buildId = Convert.ToHexString(descriptor).ToLowerInvariant();
                return true;
            }
        }
        return false;
    }

    private static bool TryAlign4(uint value, out uint aligned)
    {
        if (value > uint.MaxValue - 3)
        {
            aligned = 0;
            return false;
        }
        aligned = (value + 3) & ~3u;
        return true;
    }

    private static bool ReadExactly(Stream stream, Span<byte> destination)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = stream.Read(destination[read..]);
            if (count == 0)
                return false;
            read += count;
        }
        return true;
    }
}

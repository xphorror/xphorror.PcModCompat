using System.Text;
using StArray.ModManager.Native;

namespace StArray.ModManager.Runtime;

public delegate bool MatchValidator(int offsetInText, byte[] textData, long textAddr);

/// <summary>ELF 导出符号和特征码解析器，上游 Android API 兼容实现。</summary>
public class NativeFuncResolver
{
    private readonly byte[] _fileData;
    private readonly long _textAddr;
    private readonly long _textOffset;
    private readonly long _textSize;
    private readonly long _dynSymOffset;
    private readonly long _dynSymSize;
    private readonly long _dynStrOffset;
    private readonly long _dynStrSize;
    private byte[]? _textBytes;
    private IntPtr _loadedHandle;

    public string FilePath { get; }
    public bool IsLoaded => _loadedHandle != IntPtr.Zero;
    public IntPtr LoadedHandle => _loadedHandle;
    public byte[] TextBytes => _textBytes ??= _fileData.AsSpan((int)_textOffset, (int)_textSize).ToArray();
    public long TextBaseAddress => _textAddr;

    public NativeFuncResolver(string elfPath)
    {
        FilePath = elfPath;
        _fileData = File.ReadAllBytes(elfPath);
        ParseElfHeaders(_fileData, out _textAddr, out _textOffset, out _textSize,
            out _dynSymOffset, out _dynSymSize, out _dynStrOffset, out _dynStrSize);
    }

    public long FindRva(string symbol, byte?[]? fallbackPattern = null)
    {
        var rva = FindSymbolRva(symbol);
        if (rva >= 0) return rva;
        if (fallbackPattern != null) return FindRva(fallbackPattern);
        throw new KeyNotFoundException($"Symbol '{symbol}' not found and no fallback signature was supplied.");
    }

    public IntPtr Resolve(string symbol, byte?[]? fallbackPattern = null)
    {
        var rva = FindRva(symbol, fallbackPattern);
        Load();
        return GetFuncPtr(rva);
    }

    public long FindSymbolRva(string symbolName)
    {
        var symbols = _fileData.AsSpan((int)_dynSymOffset, (int)_dynSymSize);
        var strings = _fileData.AsSpan((int)_dynStrOffset, (int)_dynStrSize);
        for (var offset = 0; offset + 24 <= symbols.Length; offset += 24)
        {
            var nameOffset = BitConverter.ToInt32(symbols[offset..]);
            var section = BitConverter.ToInt16(symbols[(offset + 6)..]);
            var value = BitConverter.ToInt64(symbols[(offset + 8)..]);
            if (section == 0 || value == 0 || nameOffset < 0 || nameOffset >= strings.Length) continue;
            var end = nameOffset;
            while (end < strings.Length && strings[end] != 0) end++;
            if (Encoding.ASCII.GetString(strings[nameOffset..end]) == symbolName) return value;
        }
        return -1;
    }

    public long[] FindSymbolsByPattern(string pattern)
    {
        var result = new List<long>();
        var symbols = _fileData.AsSpan((int)_dynSymOffset, (int)_dynSymSize);
        var strings = _fileData.AsSpan((int)_dynStrOffset, (int)_dynStrSize);
        for (var offset = 0; offset + 24 <= symbols.Length; offset += 24)
        {
            var nameOffset = BitConverter.ToInt32(symbols[offset..]);
            var section = BitConverter.ToInt16(symbols[(offset + 6)..]);
            var value = BitConverter.ToInt64(symbols[(offset + 8)..]);
            if (section == 0 || value == 0 || nameOffset < 0 || nameOffset >= strings.Length) continue;
            var end = nameOffset;
            while (end < strings.Length && strings[end] != 0) end++;
            var name = Encoding.ASCII.GetString(strings[nameOffset..end]);
            if (GlobMatch(name, pattern)) result.Add(value);
        }
        return result.ToArray();
    }

    public long FindRva(params byte?[] pattern)
    {
        var offset = Search(TextBytes, pattern);
        if (offset < 0) throw new KeyNotFoundException("Signature not found in .text section.");
        return _textAddr + offset;
    }

    public long FindRva(byte[] pattern) => FindRva(pattern.Select(static b => (byte?)b).ToArray());

    public long[] FindAllRva(params byte?[] pattern)
    {
        var result = new List<long>();
        for (var offset = 0; (offset = Search(TextBytes, pattern, offset)) >= 0; offset++)
            result.Add(_textAddr + offset);
        return result.ToArray();
    }

    public long[] FindAllRva(byte?[] pattern, MatchValidator validator)
    {
        var result = new List<long>();
        for (var offset = 0; (offset = Search(TextBytes, pattern, offset)) >= 0; offset++)
            if (validator(offset, _fileData, _textAddr)) result.Add(_textAddr + offset);
        return result.ToArray();
    }

    public void Load()
    {
        if (_loadedHandle == IntPtr.Zero)
            _loadedHandle = DL.Open(FilePath, DL.RTLDFlags.RTLD_NOW | DL.RTLDFlags.RTLD_LOCAL);
    }

    public IntPtr GetFuncPtr(long rva)
    {
        if (_loadedHandle == IntPtr.Zero) throw new InvalidOperationException("Library not loaded.");
        return IntPtr.Add(_loadedHandle, checked((int)rva));
    }

    public static byte?[] ParseHexPattern(string hex) => hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(static token => token == "??" ? (byte?)null : Convert.ToByte(token, 16)).ToArray();

    public static int Search(ReadOnlySpan<byte> data, byte?[] pattern, int start = 0)
    {
        for (var i = start; i <= data.Length - pattern.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < pattern.Length; j++)
                if (pattern[j].HasValue && data[i + j] != pattern[j]!.Value) { matched = false; break; }
            if (matched) return i;
        }
        return -1;
    }

    private static bool GlobMatch(string value, string pattern)
    {
        if (!pattern.Contains('*')) return value == pattern;
        var parts = pattern.Split('*');
        var position = 0;
        foreach (var part in parts)
        {
            if (part.Length == 0) continue;
            var found = value.IndexOf(part, position, StringComparison.Ordinal);
            if (found < 0) return false;
            position = found + part.Length;
        }
        return !pattern.StartsWith('*') ? value.StartsWith(parts[0], StringComparison.Ordinal) :
            !pattern.EndsWith('*') && parts[^1].Length > 0 ? value.EndsWith(parts[^1], StringComparison.Ordinal) : true;
    }

    private static void ParseElfHeaders(byte[] data,
        out long textAddr, out long textOffset, out long textSize,
        out long dynSymOffset, out long dynSymSize, out long dynStrOffset, out long dynStrSize)
    {
        if (data.Length < 0x40 || data[0] != 0x7f || data[1] != (byte)'E' || data[2] != (byte)'L' || data[3] != (byte)'F')
            throw new InvalidDataException("Not an ELF file.");
        var sectionOffset = checked((int)BitConverter.ToInt64(data, 0x28));
        var entrySize = BitConverter.ToInt16(data, 0x3a);
        var sectionCount = BitConverter.ToInt16(data, 0x3c);
        var namesIndex = BitConverter.ToInt16(data, 0x3e);
        int Field(int index, int offset) => sectionOffset + index * entrySize + offset;
        var namesOffset = checked((int)BitConverter.ToInt64(data, Field(namesIndex, 0x18)));
        string SectionName(int index)
        {
            var nameOffset = BitConverter.ToInt32(data, Field(index, 0));
            var end = nameOffset;
            while (data[namesOffset + end] != 0) end++;
            return Encoding.ASCII.GetString(data, namesOffset + nameOffset, end - nameOffset);
        }

        textAddr = textOffset = textSize = dynSymOffset = dynSymSize = dynStrOffset = dynStrSize = 0;
        for (var i = 0; i < sectionCount; i++)
        {
            var name = SectionName(i);
            var address = BitConverter.ToInt64(data, Field(i, 0x10));
            var offset = BitConverter.ToInt64(data, Field(i, 0x18));
            var size = BitConverter.ToInt64(data, Field(i, 0x20));
            if (name == ".text") { textAddr = address; textOffset = offset; textSize = size; }
            else if (name == ".dynsym") { dynSymOffset = offset; dynSymSize = size; }
            else if (name == ".dynstr") { dynStrOffset = offset; dynStrSize = size; }
        }
        if (textSize == 0 || dynSymSize == 0 || dynStrSize == 0)
            throw new InvalidDataException("Required ELF section is missing.");
    }
}

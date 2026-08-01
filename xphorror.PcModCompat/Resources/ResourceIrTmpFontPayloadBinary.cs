using System.Buffers.Binary;
using System.Text;

namespace Xphorror.PcModCompat.Resources;

public readonly record struct ResourceIrTmpFontGlyph(
    uint Index,
    float Width,
    float Height,
    float HorizontalBearingX,
    float HorizontalBearingY,
    float HorizontalAdvance,
    int RectX,
    int RectY,
    int RectWidth,
    int RectHeight,
    float Scale,
    int AtlasIndex,
    int ClassDefinitionType);

public readonly record struct ResourceIrTmpFontCharacter(
    uint Unicode,
    uint GlyphIndex,
    float Scale,
    int ElementType);

public sealed record ResourceIrTmpFontPayload(
    IReadOnlyList<ResourceIrTmpFontGlyph> Glyphs,
    IReadOnlyList<ResourceIrTmpFontCharacter> Characters);

public static class ResourceIrTmpFontPayloadBinary
{
    public const string PayloadKind = "tmp-font-static-v1";
    public const ushort SchemaVersion = 1;
    private const int HeaderSize = 32;
    private const int GlyphRecordSize = 56;
    private const int CharacterRecordSize = 16;
    private const int MaximumRecordCount = 262_144;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("XPHTMPF1");

    public static byte[] Write(
        IReadOnlyList<ResourceIrTmpFontGlyph> glyphs,
        IReadOnlyList<ResourceIrTmpFontCharacter> characters)
    {
        ArgumentNullException.ThrowIfNull(glyphs);
        ArgumentNullException.ThrowIfNull(characters);
        ValidateCounts(glyphs.Count, characters.Count);
        ValidateRecords(glyphs, characters);
        var length = checked(
            HeaderSize + glyphs.Count * GlyphRecordSize + characters.Count * CharacterRecordSize);
        var bytes = new byte[length];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), SchemaVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10, 2), HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), checked((uint)glyphs.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), checked((uint)characters.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), GlyphRecordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), CharacterRecordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), checked((uint)length));

        var offset = HeaderSize;
        foreach (var glyph in glyphs)
        {
            var destination = bytes.AsSpan(offset, GlyphRecordSize);
            WriteUInt32(destination, 0, glyph.Index);
            WriteSingle(destination, 4, glyph.Width);
            WriteSingle(destination, 8, glyph.Height);
            WriteSingle(destination, 12, glyph.HorizontalBearingX);
            WriteSingle(destination, 16, glyph.HorizontalBearingY);
            WriteSingle(destination, 20, glyph.HorizontalAdvance);
            WriteInt32(destination, 24, glyph.RectX);
            WriteInt32(destination, 28, glyph.RectY);
            WriteInt32(destination, 32, glyph.RectWidth);
            WriteInt32(destination, 36, glyph.RectHeight);
            WriteSingle(destination, 40, glyph.Scale);
            WriteInt32(destination, 44, glyph.AtlasIndex);
            WriteInt32(destination, 48, glyph.ClassDefinitionType);
            offset += GlyphRecordSize;
        }
        foreach (var character in characters)
        {
            var destination = bytes.AsSpan(offset, CharacterRecordSize);
            WriteUInt32(destination, 0, character.Unicode);
            WriteUInt32(destination, 4, character.GlyphIndex);
            WriteSingle(destination, 8, character.Scale);
            WriteInt32(destination, 12, character.ElementType);
            offset += CharacterRecordSize;
        }
        return bytes;
    }

    public static ResourceIrTmpFontPayload Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || !bytes[..8].SequenceEqual(Magic))
            throw new InvalidDataException("TMP font payload header is invalid.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(8, 2)) != SchemaVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(10, 2)) != HeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(20, 4)) != GlyphRecordSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(24, 4)) != CharacterRecordSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(28, 4)) != bytes.Length)
            throw new InvalidDataException("TMP font payload schema or length is invalid.");
        var glyphCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(12, 4)));
        var characterCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(16, 4)));
        ValidateCounts(glyphCount, characterCount);
        var expectedLength = checked(
            HeaderSize + glyphCount * GlyphRecordSize + characterCount * CharacterRecordSize);
        if (expectedLength != bytes.Length)
            throw new InvalidDataException("TMP font payload record length is invalid.");

        var glyphs = new ResourceIrTmpFontGlyph[glyphCount];
        var offset = HeaderSize;
        for (var index = 0; index < glyphCount; index++)
        {
            var source = bytes.Slice(offset, GlyphRecordSize);
            glyphs[index] = new ResourceIrTmpFontGlyph(
                ReadUInt32(source, 0),
                ReadSingle(source, 4),
                ReadSingle(source, 8),
                ReadSingle(source, 12),
                ReadSingle(source, 16),
                ReadSingle(source, 20),
                ReadInt32(source, 24),
                ReadInt32(source, 28),
                ReadInt32(source, 32),
                ReadInt32(source, 36),
                ReadSingle(source, 40),
                ReadInt32(source, 44),
                ReadInt32(source, 48));
            offset += GlyphRecordSize;
        }
        var characters = new ResourceIrTmpFontCharacter[characterCount];
        for (var index = 0; index < characterCount; index++)
        {
            var source = bytes.Slice(offset, CharacterRecordSize);
            characters[index] = new ResourceIrTmpFontCharacter(
                ReadUInt32(source, 0),
                ReadUInt32(source, 4),
                ReadSingle(source, 8),
                ReadInt32(source, 12));
            offset += CharacterRecordSize;
        }
        ValidateRecords(glyphs, characters);
        return new ResourceIrTmpFontPayload(glyphs, characters);
    }

    private static void ValidateCounts(int glyphCount, int characterCount)
    {
        if (glyphCount is <= 0 or > MaximumRecordCount ||
            characterCount is <= 0 or > MaximumRecordCount)
            throw new InvalidDataException("TMP font payload record count is outside limits.");
    }

    private static void ValidateRecords(
        IReadOnlyList<ResourceIrTmpFontGlyph> glyphs,
        IReadOnlyList<ResourceIrTmpFontCharacter> characters)
    {
        var indices = new HashSet<uint>();
        foreach (var glyph in glyphs)
        {
            if (!indices.Add(glyph.Index) ||
                !float.IsFinite(glyph.Width) || !float.IsFinite(glyph.Height) ||
                !float.IsFinite(glyph.HorizontalBearingX) ||
                !float.IsFinite(glyph.HorizontalBearingY) ||
                !float.IsFinite(glyph.HorizontalAdvance) || !float.IsFinite(glyph.Scale) ||
                glyph.RectX < 0 || glyph.RectY < 0 || glyph.RectWidth < 0 || glyph.RectHeight < 0 ||
                glyph.AtlasIndex < 0 || glyph.ClassDefinitionType is < 0 or > 4)
                throw new InvalidDataException("TMP font glyph record is invalid.");
        }
        var unicodes = new HashSet<uint>();
        foreach (var character in characters)
        {
            if (!unicodes.Add(character.Unicode) || !indices.Contains(character.GlyphIndex) ||
                !float.IsFinite(character.Scale) || character.ElementType != 1)
                throw new InvalidDataException("TMP font character record is invalid.");
        }
    }

    private static void WriteUInt32(Span<byte> target, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(offset, 4), value);

    private static void WriteInt32(Span<byte> target, int offset, int value)
        => BinaryPrimitives.WriteInt32LittleEndian(target.Slice(offset, 4), value);

    private static void WriteSingle(Span<byte> target, int offset, float value)
        => WriteInt32(target, offset, BitConverter.SingleToInt32Bits(value));

    private static uint ReadUInt32(ReadOnlySpan<byte> source, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, 4));

    private static int ReadInt32(ReadOnlySpan<byte> source, int offset)
        => BinaryPrimitives.ReadInt32LittleEndian(source.Slice(offset, 4));

    private static float ReadSingle(ReadOnlySpan<byte> source, int offset)
        => BitConverter.Int32BitsToSingle(ReadInt32(source, offset));
}

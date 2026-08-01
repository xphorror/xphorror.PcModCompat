namespace Xphorror.PcModCompat.Resources;

public static class TextureRgbaDecoder
{
    public static byte[] Decode(int textureFormat, int width, int height, ReadOnlySpan<byte> source)
    {
        if (width is <= 0 or > 16_384 || height is <= 0 or > 16_384)
            throw new InvalidDataException($"Texture dimensions are invalid: {width}x{height}");
        var pixelCount = checked(width * height);
        return textureFormat switch
        {
            1 => DecodeAlpha8(source, pixelCount),
            3 => DecodeRgb24(source, pixelCount),
            4 => CopyExact(source, checked(pixelCount * 4)),
            5 => DecodeArgb32(source, pixelCount),
            7 => DecodeRgb565(source, pixelCount),
            10 => DecodeDxt1(source, width, height),
            12 => DecodeDxt5(source, width, height),
            13 => DecodeRgba4444(source, pixelCount),
            14 => DecodeBgra32(source, pixelCount),
            _ => throw new NotSupportedException($"Unity TextureFormat {textureFormat} is not supported by RGBA32 v1.")
        };
    }

    private static byte[] DecodeAlpha8(ReadOnlySpan<byte> source, int pixels)
    {
        RequireLength(source, pixels);
        var result = new byte[checked(pixels * 4)];
        for (var pixel = 0; pixel < pixels; pixel++)
        {
            var offset = pixel * 4;
            result[offset] = 255;
            result[offset + 1] = 255;
            result[offset + 2] = 255;
            result[offset + 3] = source[pixel];
        }
        return result;
    }

    private static byte[] CopyExact(ReadOnlySpan<byte> source, int length)
    {
        RequireLength(source, length);
        return source[..length].ToArray();
    }

    private static byte[] DecodeRgb24(ReadOnlySpan<byte> source, int pixels)
    {
        RequireLength(source, checked(pixels * 3));
        var result = new byte[checked(pixels * 4)];
        for (var pixel = 0; pixel < pixels; pixel++)
        {
            result[pixel * 4] = source[pixel * 3];
            result[pixel * 4 + 1] = source[pixel * 3 + 1];
            result[pixel * 4 + 2] = source[pixel * 3 + 2];
            result[pixel * 4 + 3] = 255;
        }
        return result;
    }

    private static byte[] DecodeArgb32(ReadOnlySpan<byte> source, int pixels)
    {
        RequireLength(source, checked(pixels * 4));
        var result = new byte[checked(pixels * 4)];
        for (var pixel = 0; pixel < pixels; pixel++)
        {
            var offset = pixel * 4;
            result[offset] = source[offset + 1];
            result[offset + 1] = source[offset + 2];
            result[offset + 2] = source[offset + 3];
            result[offset + 3] = source[offset];
        }
        return result;
    }

    private static byte[] DecodeBgra32(ReadOnlySpan<byte> source, int pixels)
    {
        RequireLength(source, checked(pixels * 4));
        var result = new byte[checked(pixels * 4)];
        for (var pixel = 0; pixel < pixels; pixel++)
        {
            var offset = pixel * 4;
            result[offset] = source[offset + 2];
            result[offset + 1] = source[offset + 1];
            result[offset + 2] = source[offset];
            result[offset + 3] = source[offset + 3];
        }
        return result;
    }

    private static byte[] DecodeRgb565(ReadOnlySpan<byte> source, int pixels)
    {
        RequireLength(source, checked(pixels * 2));
        var result = new byte[checked(pixels * 4)];
        for (var pixel = 0; pixel < pixels; pixel++)
        {
            var value = ReadUInt16(source, pixel * 2);
            var offset = pixel * 4;
            result[offset] = Expand5((value >> 11) & 31);
            result[offset + 1] = Expand6((value >> 5) & 63);
            result[offset + 2] = Expand5(value & 31);
            result[offset + 3] = 255;
        }
        return result;
    }

    private static byte[] DecodeRgba4444(ReadOnlySpan<byte> source, int pixels)
    {
        RequireLength(source, checked(pixels * 2));
        var result = new byte[checked(pixels * 4)];
        for (var pixel = 0; pixel < pixels; pixel++)
        {
            var value = ReadUInt16(source, pixel * 2);
            var offset = pixel * 4;
            result[offset] = (byte)(((value >> 12) & 15) * 17);
            result[offset + 1] = (byte)(((value >> 8) & 15) * 17);
            result[offset + 2] = (byte)(((value >> 4) & 15) * 17);
            result[offset + 3] = (byte)((value & 15) * 17);
        }
        return result;
    }

    private static byte[] DecodeDxt1(ReadOnlySpan<byte> source, int width, int height)
    {
        var blocksX = (width + 3) / 4;
        var blocksY = (height + 3) / 4;
        RequireLength(source, checked(blocksX * blocksY * 8));
        var result = new byte[checked(width * height * 4)];
        Span<byte> colors = stackalloc byte[16];
        for (var blockY = 0; blockY < blocksY; blockY++)
        for (var blockX = 0; blockX < blocksX; blockX++)
        {
            var block = source.Slice((blockY * blocksX + blockX) * 8, 8);
            BuildColorTable(block, colors, allowTransparent: true);
            var indices = ReadUInt32(block, 4);
            WriteColorBlock(result, width, height, blockX, blockY, colors, indices, default, 0, false);
        }
        return result;
    }

    private static byte[] DecodeDxt5(ReadOnlySpan<byte> source, int width, int height)
    {
        var blocksX = (width + 3) / 4;
        var blocksY = (height + 3) / 4;
        RequireLength(source, checked(blocksX * blocksY * 16));
        var result = new byte[checked(width * height * 4)];
        Span<byte> alpha = stackalloc byte[8];
        Span<byte> colors = stackalloc byte[16];
        for (var blockY = 0; blockY < blocksY; blockY++)
        for (var blockX = 0; blockX < blocksX; blockX++)
        {
            var block = source.Slice((blockY * blocksX + blockX) * 16, 16);
            BuildAlphaTable(block[0], block[1], alpha);
            ulong alphaIndices = 0;
            for (var index = 0; index < 6; index++)
                alphaIndices |= (ulong)block[2 + index] << (8 * index);
            BuildColorTable(block[8..], colors, allowTransparent: false);
            var colorIndices = ReadUInt32(block, 12);
            WriteColorBlock(result, width, height, blockX, blockY, colors, colorIndices, alpha, alphaIndices, true);
        }
        return result;
    }

    private static void BuildColorTable(ReadOnlySpan<byte> block, Span<byte> colors, bool allowTransparent)
    {
        var color0 = ReadUInt16(block, 0);
        var color1 = ReadUInt16(block, 2);
        WriteRgb565(colors, 0, color0, 255);
        WriteRgb565(colors, 4, color1, 255);
        if (!allowTransparent || color0 > color1)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                colors[8 + channel] = (byte)((2 * colors[channel] + colors[4 + channel]) / 3);
                colors[12 + channel] = (byte)((colors[channel] + 2 * colors[4 + channel]) / 3);
            }
            colors[11] = colors[15] = 255;
        }
        else
        {
            for (var channel = 0; channel < 3; channel++)
                colors[8 + channel] = (byte)((colors[channel] + colors[4 + channel]) / 2);
            colors[11] = 255;
            colors[12] = colors[13] = colors[14] = colors[15] = 0;
        }
    }

    private static void BuildAlphaTable(byte alpha0, byte alpha1, Span<byte> alpha)
    {
        alpha[0] = alpha0;
        alpha[1] = alpha1;
        if (alpha0 > alpha1)
        {
            for (var index = 1; index <= 6; index++)
                alpha[index + 1] = (byte)(((7 - index) * alpha0 + index * alpha1) / 7);
        }
        else
        {
            for (var index = 1; index <= 4; index++)
                alpha[index + 1] = (byte)(((5 - index) * alpha0 + index * alpha1) / 5);
            alpha[6] = 0;
            alpha[7] = 255;
        }
    }

    private static void WriteColorBlock(
        byte[] destination,
        int width,
        int height,
        int blockX,
        int blockY,
        ReadOnlySpan<byte> colors,
        uint colorIndices,
        ReadOnlySpan<byte> alphaTable,
        ulong alphaIndices,
        bool hasAlpha)
    {
        for (var pixel = 0; pixel < 16; pixel++)
        {
            var x = blockX * 4 + pixel % 4;
            var y = blockY * 4 + pixel / 4;
            if (x >= width || y >= height)
                continue;
            var colorIndex = (int)((colorIndices >> (pixel * 2)) & 3);
            var sourceOffset = colorIndex * 4;
            var destinationOffset = (y * width + x) * 4;
            destination[destinationOffset] = colors[sourceOffset];
            destination[destinationOffset + 1] = colors[sourceOffset + 1];
            destination[destinationOffset + 2] = colors[sourceOffset + 2];
            destination[destinationOffset + 3] = hasAlpha
                ? alphaTable[(int)((alphaIndices >> (pixel * 3)) & 7)]
                : colors[sourceOffset + 3];
        }
    }

    private static void WriteRgb565(Span<byte> destination, int offset, ushort value, byte alpha)
    {
        destination[offset] = Expand5((value >> 11) & 31);
        destination[offset + 1] = Expand6((value >> 5) & 63);
        destination[offset + 2] = Expand5(value & 31);
        destination[offset + 3] = alpha;
    }

    private static byte Expand5(int value) => (byte)((value << 3) | (value >> 2));
    private static byte Expand6(int value) => (byte)((value << 2) | (value >> 4));

    private static ushort ReadUInt16(ReadOnlySpan<byte> source, int offset)
        => (ushort)(source[offset] | source[offset + 1] << 8);

    private static uint ReadUInt32(ReadOnlySpan<byte> source, int offset)
        => (uint)(source[offset] | source[offset + 1] << 8 | source[offset + 2] << 16 | source[offset + 3] << 24);

    private static void RequireLength(ReadOnlySpan<byte> source, int required)
    {
        if (source.Length < required)
            throw new InvalidDataException($"Texture payload is truncated: required={required} actual={source.Length}");
    }
}

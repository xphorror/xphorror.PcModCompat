using System.Runtime.InteropServices;
using ImGuiNET;
using StArray.ModManager.Resources;

namespace StArray.ModManager.UI;

internal static unsafe class ImGuiTextGlyphRanges
{
    internal static nint Create(ImGuiIOPtr io)
        => Create(io, out _, out _);

    internal static nint Create(
        ImGuiIOPtr io,
        out int codepointCount,
        out int rangeCount)
    {
        codepointCount = 0;
        rangeCount = 0;
        var codepoints = new SortedSet<int>();
        for (var codepoint = 0x20; codepoint <= 0xff; codepoint++)
            codepoints.Add(codepoint);
        AddGlyphRanges(codepoints, io.Fonts.GetGlyphRangesChineseSimplifiedCommon());
        AddGlyphRanges(codepoints, io.Fonts.GetGlyphRangesKorean());
        foreach (var codepoint in L10n.GetRequiredFontGlyphCodepoints())
        {
            if (codepoint is >= 0x20 and <= ushort.MaxValue)
                codepoints.Add(codepoint);
        }

        var ranges = new List<ushort>();
        var start = -1;
        var previous = -1;
        foreach (var codepoint in codepoints)
        {
            if (start < 0)
            {
                start = previous = codepoint;
                continue;
            }

            if (codepoint == previous + 1)
            {
                previous = codepoint;
                continue;
            }

            ranges.Add((ushort)start);
            ranges.Add((ushort)previous);
            start = previous = codepoint;
        }

        if (start >= 0)
        {
            ranges.Add((ushort)start);
            ranges.Add((ushort)previous);
        }
        ranges.Add(0);
        codepointCount = codepoints.Count;
        rangeCount = (ranges.Count - 1) / 2;

        var values = ranges.ToArray();
        var bytes = values.Length * sizeof(ushort);
        var pointer = Marshal.AllocHGlobal(bytes);
        fixed (ushort* source = values)
            Buffer.MemoryCopy(source, (void*)pointer, bytes, bytes);
        return pointer;
    }

    private static void AddGlyphRanges(SortedSet<int> codepoints, nint ranges)
    {
        if (ranges == 0)
            return;

        var cursor = (ushort*)ranges;
        while (cursor[0] != 0)
        {
            var start = cursor[0];
            var end = cursor[1];
            for (var codepoint = (int)start; codepoint <= end; codepoint++)
                codepoints.Add(codepoint);
            cursor += 2;
        }
    }
}

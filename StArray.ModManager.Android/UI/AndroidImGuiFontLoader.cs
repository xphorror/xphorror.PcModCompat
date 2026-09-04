using System.Buffers;
using System.Runtime.InteropServices;
using ImGuiNET;
using StArray.ModManager.Android.Native;
using StArray.ModManager.Manager;
using StArray.ModManager.Resources;
using StArray.ModManager.UI;

namespace StArray.ModManager.Android.UI;

internal static unsafe class AndroidImGuiFontLoader
{
    private const string CjkResource = "StArray.ModManager.Resources.NotoSansCJK-Regular.otf";
    private const string IconResource = "StArray.ModManager.Resources.fa-solid-900.ttf";
    private const float FontSize = 16f;
    private const string SystemCjkFont = "/system/fonts/NotoSansCJK-Regular.ttc";

    private static readonly object Sync = new();
    private static nint _initializedAtlas;
    private static long _loadedGlyphRevision = -1;
    private static long _loadCount;
    private static int _lastLoggedMissingCount = -1;
    private static string? _lastLoggedSource;
    private static int _observedTextProbeState;
    private static long _observedTextBatchCount;
    private static long _observedTextNewGlyphCount;

    public static string LastStatus { get; private set; } = "not-initialized";

    public static bool EnsureLoaded(ImGuiIOPtr io, Func<bool>? recreateFontTexture = null)
    {
        DrainObservedTextGlyphs();
        var atlas = (nint)io.Fonts.NativePtr;
        if (atlas == 0)
        {
            LastStatus = "failed:no-font-atlas";
            Logger.Error(nameof(AndroidImGuiFontLoader), LastStatus);
            return false;
        }

        lock (Sync)
        {
            var glyphRevision = L10n.DynamicGlyphRevision;
            if (_initializedAtlas == atlas && _loadedGlyphRevision == glyphRevision)
                return !LastStatus.StartsWith("failed:", StringComparison.Ordinal);

            var rebuildingExistingAtlas = _initializedAtlas == atlas && _initializedAtlas != 0;
            if (rebuildingExistingAtlas && recreateFontTexture == null)
            {
                LastStatus = "deferred:dynamic-font-atlas-needs-render-backend";
                Logger.Warn(nameof(AndroidImGuiFontLoader), LastStatus);
                return false;
            }

            var loadNumber = ++_loadCount;
            var loaded = Load(io, atlas, glyphRevision, loadNumber);
            // Registration can occur on another managed callback while the
            // atlas is being rebuilt. Keep the exact revision that was
            // materialized; a newer revision will be picked up next frame.
            _loadedGlyphRevision = L10n.DynamicGlyphRevision;

            if (rebuildingExistingAtlas && recreateFontTexture != null)
            {
                try
                {
                    if (!recreateFontTexture())
                    {
                        LastStatus = "failed:font-texture-recreate";
                        Logger.Error(nameof(AndroidImGuiFontLoader), LastStatus);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    LastStatus =
                        $"failed:font-texture-recreate:{ex.GetType().Name}:{SingleLine(ex.Message)}";
                    Logger.Error(nameof(AndroidImGuiFontLoader), LastStatus);
                    return false;
                }
            }

            return loaded;
        }
    }

    private static bool Load(
        ImGuiIOPtr io,
        nint atlas,
        long requestedRevision,
        long loadNumber)
    {
        nint cjkData = 0;
        nint iconData = 0;
        nint textRanges = 0;
        nint iconRanges = 0;
        ImFontPtr baseFont = default;
        var cjkSource = "none";
        var iconLoaded = false;
        var glyphCount = 0;
        var rangeCount = 0;

        try
        {
            // ClearFonts() leaves the previous ConfigData entries behind.
            // A dynamic rebuild must discard those source-font records too;
            // otherwise Build() mixes stale DstFont pointers with the new atlas.
            io.Fonts.Clear();
            textRanges = ImGuiTextGlyphRanges.Create(io, out glyphCount, out rangeCount);

            if (TryCopyEmbeddedFont(CjkResource, out cjkData, out var cjkLength, out var cjkError))
            {
                baseFont = AddMemoryFont(
                    io,
                    cjkData,
                    cjkLength,
                    merge: false,
                    textRanges);
                cjkSource = "embedded";
            }
            else if (File.Exists(SystemCjkFont))
            {
                baseFont = io.Fonts.AddFontFromFileTTF(
                    SystemCjkFont,
                    FontSize,
                    null,
                    textRanges);
                cjkSource = "system";
                Logger.Warn(nameof(AndroidImGuiFontLoader),
                    $"embedded CJK unavailable ({cjkError}); using {SystemCjkFont}");
            }
            else
            {
                baseFont = io.Fonts.AddFontDefault();
                cjkSource = "default";
                Logger.Warn(nameof(AndroidImGuiFontLoader),
                    $"embedded CJK unavailable ({cjkError}); system fallback missing");
            }

            if ((nint)baseFont.NativePtr == 0)
                throw new InvalidOperationException("base font registration returned null");

            if (TryCopyEmbeddedFont(IconResource, out iconData, out var iconLength, out var iconError))
            {
                iconRanges = CreateIconGlyphRanges();
                var iconFont = AddMemoryFont(io, iconData, iconLength, merge: true, iconRanges);
                iconLoaded = (nint)iconFont.NativePtr != 0;
                if (!iconLoaded)
                    Logger.Warn(nameof(AndroidImGuiFontLoader), "FontAwesome registration returned null");
            }
            else
            {
                Logger.Warn(nameof(AndroidImGuiFontLoader),
                    $"FontAwesome unavailable ({iconError}); text controls remain usable");
            }

            if (!io.Fonts.Build())
                throw new InvalidOperationException("font atlas build returned false");

            var missing = ValidateFixedGlyphs(baseFont, iconLoaded, out var missingCount);
            LastStatus =
                $"ready:cjk={cjkSource},icons={(iconLoaded ? "embedded" : "missing")}," +
                $"glyphs={glyphCount},ranges={rangeCount},revision={requestedRevision}," +
                $"load={loadNumber},missingCount={missingCount}";
            _initializedAtlas = atlas;

            // Dynamic strings may arrive in bursts. Keep the diagnostic
            // available in LastStatus, but sample normal rebuild logs so a
            // chatty MOD cannot turn atlas maintenance into log spam.
            if (ShouldLogLoad(loadNumber, cjkSource, missingCount))
            {
                if (missing.Length == 0)
                    Logger.Info(nameof(AndroidImGuiFontLoader), LastStatus);
                else
                    Logger.Warn(nameof(AndroidImGuiFontLoader), $"{LastStatus},missing={missing}");
            }

            return true;
        }
        catch (Exception ex)
        {
            LastStatus = $"failed:{ex.GetType().Name}:{SingleLine(ex.Message)}";
            Logger.Error(nameof(AndroidImGuiFontLoader), LastStatus);

            try
            {
                io.Fonts.Clear();
                io.Fonts.AddFontDefault();
                io.Fonts.Build();
                _initializedAtlas = atlas;
            }
            catch (Exception fallbackEx)
            {
                Logger.Error(nameof(AndroidImGuiFontLoader),
                    $"default font fallback failed:{fallbackEx.GetType().Name}:{SingleLine(fallbackEx.Message)}");
            }

            return false;
        }
        finally
        {
            if (cjkData != 0)
                Marshal.FreeHGlobal(cjkData);
            if (iconData != 0)
                Marshal.FreeHGlobal(iconData);
            if (textRanges != 0)
                Marshal.FreeHGlobal(textRanges);
            if (iconRanges != 0)
                Marshal.FreeHGlobal(iconRanges);
        }
    }

    private static ImFontPtr AddMemoryFont(
        ImGuiIOPtr io,
        nint data,
        int length,
        bool merge,
        nint glyphRanges)
    {
        var config = ImGuiNative.ImFontConfig_ImFontConfig();
        if (config == null)
            throw new InvalidOperationException("ImFontConfig allocation returned null");

        try
        {
            config->MergeMode = merge ? (byte)1 : (byte)0;
            config->FontDataOwnedByAtlas = 0;
            return io.Fonts.AddFontFromMemoryTTF(data, length, FontSize, config, glyphRanges);
        }
        finally
        {
            ImGuiNative.ImFontConfig_destroy(config);
        }
    }

    private static bool TryCopyEmbeddedFont(
        string resourceName,
        out nint data,
        out int length,
        out string error)
    {
        data = 0;
        length = 0;
        error = "none";

        try
        {
            using var stream = typeof(IImGuiRenderer).Assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                error = "resource-not-found";
                return false;
            }

            if (stream.Length <= 0 || stream.Length > int.MaxValue)
            {
                error = $"invalid-length:{stream.Length}";
                return false;
            }

            length = checked((int)stream.Length);
            data = Marshal.AllocHGlobal(length);
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                var offset = 0;
                while (offset < length)
                {
                    var read = stream.Read(buffer, 0, Math.Min(buffer.Length, length - offset));
                    if (read == 0)
                        throw new EndOfStreamException($"unexpected EOF at {offset}/{length}");
                    Marshal.Copy(buffer, 0, data + offset, read);
                    offset += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return true;
        }
        catch (Exception ex)
        {
            if (data != 0)
            {
                Marshal.FreeHGlobal(data);
                data = 0;
            }
            length = 0;
            error = $"{ex.GetType().Name}:{SingleLine(ex.Message)}";
            return false;
        }
    }

    private static nint CreateIconGlyphRanges()
    {
        // All FontAwesome symbols currently referenced by ModManager and PcCompat UI.
        ReadOnlySpan<ushort> values =
        [
            0xe2ca, 0xe2ca,
            0xe473, 0xe473,
            0xf002, 0xf002,
            0xf00d, 0xf00d,
            0xf013, 0xf013,
            0xf021, 0xf021,
            0xf03a, 0xf03a,
            0xf04b, 0xf04b,
            0xf04d, 0xf04d,
            0xf057, 0xf058,
            0xf05a, 0xf05a,
            0xf078, 0xf078,
            0xf08e, 0xf08e,
            0xf0ae, 0xf0ae,
            0xf0c7, 0xf0c7,
            0xf110, 0xf111,
            0xf11c, 0xf11c,
            0xf1c6, 0xf1c6,
            0xf1de, 0xf1de,
            0xf1f8, 0xf1f8,
            0xf2ea, 0xf2ea,
            0xf2f1, 0xf2f1,
            0xf4fe, 0xf4fe,
            0xf53f, 0xf53f,
            0xf56e, 0xf56f,
            0xf7d9, 0xf7d9,
            0
        ];

        var bytes = values.Length * sizeof(ushort);
        var ptr = Marshal.AllocHGlobal(bytes);
        fixed (ushort* source = values)
            Buffer.MemoryCopy(source, (void*)ptr, bytes, bytes);
        return ptr;
    }

    private static string ValidateFixedGlyphs(
        ImFontPtr font,
        bool includeIcons,
        out int missingCount)
    {
        var required = new SortedSet<ushort> { 'A', '0' };
        foreach (var codepoint in L10n.GetRequiredFontGlyphCodepoints())
        {
            if (codepoint is >= 0x20 and <= ushort.MaxValue)
                required.Add((ushort)codepoint);
        }
        required.Add('编');
        required.Add('辑');
        required.Add('器');
        required.Add('타');
        required.Add('일');
        required.Add('체');
        required.Add('감');

        if (includeIcons)
        {
            required.Add(0xf00d);
            required.Add(0xf013);
            required.Add(0xf04b);
            required.Add(0xf0c7);
        }

        var missing = new List<string>();
        foreach (var codepoint in required)
        {
            if ((nint)font.FindGlyphNoFallback(codepoint).NativePtr == 0)
                missing.Add($"U+{codepoint:X4}");
        }

        missingCount = missing.Count;

        const int reportLimit = 12;
        var report = string.Join(',', missing.Take(reportLimit));
        return missing.Count <= reportLimit
            ? report
            : $"{report},+{missing.Count - reportLimit} more";
    }

    private static string SingleLine(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool ShouldLogLoad(long loadNumber, string source, int missingCount)
    {
        var sampled = loadNumber <= 8 || (loadNumber > 0 && (loadNumber & (loadNumber - 1)) == 0);
        var stateChanged = !string.Equals(_lastLoggedSource, source, StringComparison.Ordinal) ||
                           _lastLoggedMissingCount != missingCount;
        if (!sampled && !stateChanged)
            return false;

        _lastLoggedSource = source;
        _lastLoggedMissingCount = missingCount;
        return true;
    }

    private static void DrainObservedTextGlyphs()
    {
        if (Volatile.Read(ref _observedTextProbeState) < 0)
            return;

        Span<ushort> codepoints = stackalloc ushort[4096];
        int count;
        try
        {
            count = ImGuiImplAndroid.DrainObservedTextGlyphs(codepoints);
            Volatile.Write(ref _observedTextProbeState, 1);
        }
        catch (DllNotFoundException)
        {
            Volatile.Write(ref _observedTextProbeState, -1);
            return;
        }
        catch (EntryPointNotFoundException)
        {
            // An old native library can still run the managed host. Disable this
            // optional probe once and retain the managed registration paths.
            Volatile.Write(ref _observedTextProbeState, -1);
            return;
        }

        if (count <= 0)
            return;

        count = Math.Min(count, codepoints.Length);
        var added = L10n.RegisterDynamicGlyphCodepoints(codepoints[..count]);
        var batch = Interlocked.Increment(ref _observedTextBatchCount);
        Interlocked.Add(ref _observedTextNewGlyphCount, added);

        // The observer sees ordinary ASCII on every frame. Only log batches that
        // changed the atlas, and sample those changes to keep UnityMain cheap.
        if (added > 0 && (batch <= 8 || (batch & (batch - 1)) == 0))
        {
            Logger.Info(nameof(AndroidImGuiFontLoader),
                $"observed-text-glyphs batch={batch},count={count},new={added}," +
                $"totalNew={Volatile.Read(ref _observedTextNewGlyphCount)}");
        }
    }
}

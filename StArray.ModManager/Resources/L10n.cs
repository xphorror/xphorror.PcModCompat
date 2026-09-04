using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text;

namespace StArray.ModManager.Resources;

/// <summary>基于 resx 的轻量本地化</summary>
public static class L10n
{
    public const string ChineseLanguage = "zh-CN";
    public const string EnglishLanguage = "en";

    private static readonly ResourceManager _rm = new(
        "StArray.ModManager.Resources.Localization",
        typeof(L10n).Assembly);
    private static readonly object DynamicGlyphSync = new();
    private static readonly SortedSet<int> DynamicGlyphCodepoints = new();
    private static readonly HashSet<string> DynamicGlyphTextCache = new(StringComparer.Ordinal);
    private const int DynamicGlyphTextCacheLimit = 4096;
    private static long _dynamicGlyphRevision;
    private static CultureInfo _culture = CultureInfo.GetCultureInfo(ChineseLanguage);

    public static string CurrentLanguage => _culture.Name;

    internal static long DynamicGlyphRevision
        => Volatile.Read(ref _dynamicGlyphRevision);

    public static string NormalizeLanguage(string? language)
        => string.Equals(language, EnglishLanguage, StringComparison.OrdinalIgnoreCase) ||
           language?.StartsWith("en-", StringComparison.OrdinalIgnoreCase) == true
            ? EnglishLanguage
            : ChineseLanguage;

    public static bool SetLanguage(string? language)
    {
        var normalized = NormalizeLanguage(language);
        if (string.Equals(_culture.Name, normalized, StringComparison.OrdinalIgnoreCase))
            return false;

        _culture = CultureInfo.GetCultureInfo(normalized);
        return true;
    }

    /// <summary>获取本地化字符串，支持 {0} 占位</summary>
    public static string Get(string key, params object[] args)
    {
        var culture = _culture;
        var s = _rm.GetString(key, culture) ?? key;
        var result = args.Length > 0 ? string.Format(culture, s, args) : s;
        // A resource can contain a culture-specific or fallback value that is
        // not present in the startup resource scan. Register every value at its
        // actual use site, including non-formatted strings.
        RegisterDynamicGlyphText(result);
        return result;
    }

    /// <summary>获取当前资源集实际使用的 BMP 字符，用于构建受限字体图集。</summary>
    public static IReadOnlyList<int> GetRequiredGlyphCodepoints(CultureInfo? culture = null)
    {
        var resourceSet = _rm.GetResourceSet(
            culture ?? CultureInfo.CurrentUICulture,
            createIfNotExists: true,
            tryParents: true);
        if (resourceSet == null)
            return Array.Empty<int>();

        var codepoints = new SortedSet<int>();
        foreach (DictionaryEntry entry in resourceSet)
        {
            if (entry.Value is not string value)
                continue;

            foreach (var rune in value.EnumerateRunes())
            {
                if (rune.Value is >= 0x20 and <= ushort.MaxValue)
                    codepoints.Add(rune.Value);
            }
        }

        return codepoints.ToArray();
    }

    /// <summary>登记启动时已知的 MOD 名称、描述和状态文本。</summary>
    public static void RegisterDynamicGlyphText(params string?[] values)
    {
        var changed = false;
        lock (DynamicGlyphSync)
        {
            foreach (var value in values)
            {
                if (string.IsNullOrEmpty(value))
                    continue;

                // UI values are often offered on every frame. Avoid rescanning the
                // same string while retaining a bounded cache for unbounded logs.
                if (DynamicGlyphTextCache.Count < DynamicGlyphTextCacheLimit)
                {
                    if (!DynamicGlyphTextCache.Add(value))
                        continue;
                }
                else if (DynamicGlyphTextCache.Contains(value))
                {
                    continue;
                }

                foreach (var rune in value.EnumerateRunes())
                {
                    if (rune.Value is >= 0x20 and <= ushort.MaxValue)
                        changed |= DynamicGlyphCodepoints.Add(rune.Value);
                }
            }
        }

        if (changed)
            Interlocked.Increment(ref _dynamicGlyphRevision);
    }

    /// <summary>
    /// 将 native ImGui 文本提交边界观察到的 BMP codepoint 合并到动态图集需求中。
    /// </summary>
    internal static int RegisterDynamicGlyphCodepoints(ReadOnlySpan<ushort> codepoints)
    {
        var added = 0;
        lock (DynamicGlyphSync)
        {
            foreach (var codepoint in codepoints)
            {
                if (codepoint < 0x20 || !DynamicGlyphCodepoints.Add(codepoint))
                    continue;
                ++added;
            }
        }

        if (added > 0)
            Interlocked.Increment(ref _dynamicGlyphRevision);
        return added;
    }

    /// <summary>获取中英文资源和启动时动态文本所需的全部 BMP 字符。</summary>
    public static IReadOnlyList<int> GetRequiredFontGlyphCodepoints()
    {
        var codepoints = new SortedSet<int>();
        foreach (var culture in new[]
                 {
                     CultureInfo.InvariantCulture,
                     CultureInfo.GetCultureInfo(EnglishLanguage),
                     _culture
                 })
        {
            codepoints.UnionWith(GetRequiredGlyphCodepoints(culture));
        }

        lock (DynamicGlyphSync)
            codepoints.UnionWith(DynamicGlyphCodepoints);
        return codepoints.ToArray();
    }
}

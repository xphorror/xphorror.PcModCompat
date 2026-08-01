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
    private static CultureInfo _culture = CultureInfo.GetCultureInfo(ChineseLanguage);

    public static string CurrentLanguage => _culture.Name;

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
        return args.Length > 0 ? string.Format(culture, s, args) : s;
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
        lock (DynamicGlyphSync)
        {
            foreach (var value in values)
            {
                if (string.IsNullOrEmpty(value))
                    continue;

                foreach (var rune in value.EnumerateRunes())
                {
                    if (rune.Value is >= 0x20 and <= ushort.MaxValue)
                        DynamicGlyphCodepoints.Add(rune.Value);
                }
            }
        }
    }

    /// <summary>获取中英文资源和启动时动态文本所需的全部 BMP 字符。</summary>
    public static IReadOnlyList<int> GetRequiredFontGlyphCodepoints()
    {
        var codepoints = new SortedSet<int>();
        foreach (var culture in new[]
                 {
                     CultureInfo.InvariantCulture,
                     CultureInfo.GetCultureInfo(EnglishLanguage)
                 })
        {
            codepoints.UnionWith(GetRequiredGlyphCodepoints(culture));
        }

        lock (DynamicGlyphSync)
            codepoints.UnionWith(DynamicGlyphCodepoints);
        return codepoints.ToArray();
    }
}

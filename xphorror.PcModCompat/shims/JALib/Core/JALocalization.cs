using Newtonsoft.Json.Linq;
using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using UnityEngine;

namespace JALib.Core;

public class JALocalization
{
    private readonly JAMod? _mod;
    private IReadOnlyDictionary<string, string> _localizations =
        FrozenDictionary<string, string>.Empty;
    private string? _loadedPath;
    private string? _selectedLanguage;

    public JALocalization()
        : this(null)
    {
    }

    internal JALocalization(JAMod? mod)
    {
        _mod = mod;
    }

    public string? LoadedPath => Volatile.Read(ref _loadedPath);
    public string? SelectedLanguage => Volatile.Read(ref _selectedLanguage);

    public string this[string key] => Get(key);

    public string Get(string key)
        => TryGet(key, out var value) ? value : key;

    public bool TryGet(string key, out string value)
    {
        if (Volatile.Read(ref _localizations).TryGetValue(key, out value!))
            return true;
        value = key;
        return false;
    }

    internal void Load(string modPath)
    {
        Volatile.Write(
            ref _localizations,
            FrozenDictionary<string, string>.Empty);
        Volatile.Write(ref _loadedPath, null);
        Volatile.Write(ref _selectedLanguage, null);
        var localizationRoot = System.IO.Path.Combine(modPath, "localization");
        foreach (var language in GetLanguageCandidates(
                     CultureInfo.CurrentUICulture,
                     _mod?.CustomLanguage))
        {
            var path = System.IO.Path.Combine(localizationRoot, language + ".json");
            if (!File.Exists(path))
                continue;
            try
            {
                var document = JObject.Parse(File.ReadAllText(path));
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in document.Properties())
                {
                    if (property.Value.Type == JTokenType.String)
                        values[property.Name] = property.Value.Value<string>() ?? property.Name;
                }
                Apply(values);
                Volatile.Write(ref _loadedPath, path);
                Volatile.Write(ref _selectedLanguage, language);
                break;
            }
            catch (Exception exception)
            {
                _mod?.Warning($"Failed to load localization file '{path}': {exception.GetBaseException().Message}");
            }
        }
    }

    public void LoadOnFile(Task<string> task)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(task);
            var document = JObject.Parse(task.GetAwaiter().GetResult());
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.Properties())
            {
                if (property.Value.Type == JTokenType.String)
                    values[property.Name] = property.Value.Value<string>() ?? property.Name;
            }
            Apply(values);
            _mod?.CompatLocalizationUpdated();
        }
        catch (Exception exception)
        {
            _mod?.LogReportException("Failed to load localization data.", exception);
        }
    }

    private void Apply(IReadOnlyDictionary<string, string> values)
    {
        var snapshot = values.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        Volatile.Write(ref _localizations, snapshot);
    }

    private static IReadOnlyList<string> GetLanguageCandidates(
        CultureInfo culture,
        SystemLanguage? customLanguage)
    {
        var result = new List<string>(4);
        AddCandidate(result, LanguageName(customLanguage));
        AddCandidate(result, TryGetGameLanguage());
        AddCandidate(result, GetCultureLanguage(culture));
        AddCandidate(result, "English");
        AddCandidate(result, "Korean");
        return result;
    }

    private static string? LanguageName(SystemLanguage? language)
        => language switch
        {
            null or SystemLanguage.Unknown => null,
            SystemLanguage.Chinese => nameof(SystemLanguage.ChineseSimplified),
            _ => language.Value.ToString()
        };

    private static string GetCultureLanguage(CultureInfo culture)
        => culture.TwoLetterISOLanguageName.ToLowerInvariant() switch
        {
            "zh" when culture.Name.Contains("TW", StringComparison.OrdinalIgnoreCase) ||
                      culture.Name.Contains("HK", StringComparison.OrdinalIgnoreCase) ||
                      culture.Name.Contains("MO", StringComparison.OrdinalIgnoreCase) ||
                      culture.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                => "ChineseTraditional",
            "zh" => "ChineseSimplified",
            "ko" => "Korean",
            "ja" => "Japanese",
            "fr" => "French",
            "de" => "German",
            "es" => "Spanish",
            "it" => "Italian",
            "pt" => "Portuguese",
            "ru" => "Russian",
            "pl" => "Polish",
            "tr" => "Turkish",
            "vi" => "Vietnamese",
            "en" => "English",
            _ => culture.EnglishName.Split(' ', '(', StringSplitOptions.RemoveEmptyEntries)[0]
        };

    private static string? TryGetGameLanguage()
    {
        try
        {
            var ownerContext = AssemblyLoadContext.GetLoadContext(typeof(JALocalization).Assembly);
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => AssemblyLoadContext.GetLoadContext(assembly) == ownerContext)
                .ToList();
            if (ownerContext != null &&
                assemblies.All(assembly =>
                    !string.Equals(
                        assembly.GetName().Name,
                        "Assembly-CSharp",
                        StringComparison.OrdinalIgnoreCase)))
            {
                assemblies.Add(ownerContext.LoadFromAssemblyName(
                    new AssemblyName("Assembly-CSharp")));
            }

            foreach (var assembly in assemblies)
            {
                var rdString = assembly.GetType("RDString", throwOnError: false);
                if (rdString == null)
                    continue;
                rdString.GetMethod(
                    "Setup",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null)?.Invoke(null, null);
                var language = rdString.GetField(
                                   "language",
                                   BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ??
                               rdString.GetProperty(
                                   "language",
                                   BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ??
                               rdString.GetMethod(
                                   "get_language",
                                   BindingFlags.Public | BindingFlags.Static,
                                   binder: null,
                                   types: Type.EmptyTypes,
                                   modifiers: null)?.Invoke(null, null);
                var languageName = language?.ToString();
                return string.Equals(languageName, "Chinese", StringComparison.OrdinalIgnoreCase)
                    ? "ChineseSimplified"
                    : languageName;
            }
        }
        catch
        {
            // The game proxy may not be initialized yet; culture fallback stays valid.
        }
        return null;
    }

    private static void AddCandidate(List<string> result, string? language)
    {
        if (string.IsNullOrWhiteSpace(language) ||
            result.Contains(language, StringComparer.OrdinalIgnoreCase))
            return;
        result.Add(language);
    }
}

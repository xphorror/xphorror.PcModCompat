using StArray.ModManager.Manager;

namespace StArray.ModManager;

/// <summary>
/// Compatibility surface for upstream runtime assembly generation.
/// </summary>
public static class AssemblyEmitter
{
    public const string OutputName = "UnmanagedTypeAssembly";

    public static string? GenerateToMods(string modsDirectory)
    {
        var modDirectory = Path.Combine(modsDirectory, OutputName);
        return GenerateToDir(modDirectory, asModDll: true) != null ? modDirectory : null;
    }

    public static string? GenerateToDir(string outputDir, bool asModDll = false)
    {
        Logger.Warn(nameof(AssemblyEmitter),
            "Runtime assembly emission is unavailable in the Android-only runtime; use generated proxies.");
        return null;
    }
}

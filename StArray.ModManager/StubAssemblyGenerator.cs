using StArray.ModManager.Manager;

namespace StArray.ModManager;

/// <summary>
/// Compatibility surface for the upstream developer-time stub generator.
/// </summary>
public static class StubAssemblyGenerator
{
    public static void GenerateToDir(string outputDir)
    {
        Logger.Warn(nameof(StubAssemblyGenerator),
            "Runtime stub generation is unavailable in the Android-only runtime; use the proxy toolchain.");
    }
}

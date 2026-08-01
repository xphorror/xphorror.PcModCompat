namespace Xphorror.PcModCompat;

public sealed class PcCompatManagedAssemblyBundleInfo
{
    public required string CacheKey { get; init; }
    public required string BundleDirectory { get; init; }
    public required string InputAssemblyPath { get; init; }
    public required string RewrittenAssemblyPath { get; init; }
    public required string ReportPath { get; init; }
    public required string CompleteMarkerPath { get; init; }
    public required bool CacheHit { get; init; }
    public required int RewrittenInstructions { get; init; }
    public required int PassthroughInstructions { get; init; }
    public int ManagedBridgeRewrites { get; init; }
    public IReadOnlyDictionary<string, string> InputAssemblyPaths { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> RewrittenAssemblyPaths { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string? BootstrapAssemblyName { get; init; }
    public string? KeyViewerAdapterPath { get; init; }
    public string? KeyViewerScanIssuesPath { get; init; }
}

public static class PcCompatManagedAssemblyRewrite
{
    private static Func<
        PcModManifest,
        PcCompatStaticPatchScanReport,
        CancellationToken,
        PcCompatManagedAssemblyBundleInfo>? s_provider;

    public static void RegisterProvider(
        Func<
            PcModManifest,
            PcCompatStaticPatchScanReport,
            CancellationToken,
            PcCompatManagedAssemblyBundleInfo>? provider)
        => Volatile.Write(ref s_provider, provider);

    public static bool IsProviderRegistered
        => Volatile.Read(ref s_provider) != null;

    public static string ResolveRuntimeAssemblyPath(
        string? reportedLocation,
        string assemblySimpleName,
        string runtimeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblySimpleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeDirectory);

        if (!string.IsNullOrWhiteSpace(reportedLocation))
        {
            var reportedPath = Path.GetFullPath(reportedLocation);
            if (File.Exists(reportedPath))
                return reportedPath;
        }

        var runtimePath = Path.GetFullPath(Path.Combine(
            runtimeDirectory,
            assemblySimpleName + ".dll"));
        if (File.Exists(runtimePath))
            return runtimePath;

        throw new FileNotFoundException(
            $"Managed runtime assembly was not found: {assemblySimpleName}",
            runtimePath);
    }

    public static PcCompatManagedAssemblyBundleInfo? Prepare(
        PcModManifest manifest,
        PcCompatStaticPatchScanReport staticScan,
        CancellationToken cancellationToken = default)
    {
        var provider = Volatile.Read(ref s_provider);
        return provider?.Invoke(manifest, staticScan, cancellationToken);
    }
}

namespace Xphorror.PcModCompat;

public sealed class PcCompatResourceCompileInfo
{
    public required string RecipePath { get; init; }
    public required string ResourceIrPath { get; init; }
    public required string ResourceIrPayloadDirectory { get; init; }
    public required string ReportPath { get; init; }
    public required string Compatibility { get; init; }
    public required bool CacheHit { get; init; }
    public required int CandidateCount { get; init; }
    public required int FeatureGroupCount { get; init; }
    public required int BindingCount { get; init; }
    public required int IrBundleCount { get; init; }
    public required int IrAssetCount { get; init; }
    public required int IrRequiredAssetCount { get; init; }
}

public static class PcCompatResourceAssemblyCompile
{
    private static Func<
        PcModManifest,
        CancellationToken,
        PcCompatResourceCompileInfo>? s_provider;

    public static void RegisterProvider(
        Func<
            PcModManifest,
            CancellationToken,
            PcCompatResourceCompileInfo>? provider)
        => Volatile.Write(ref s_provider, provider);

    public static bool IsProviderRegistered
        => Volatile.Read(ref s_provider) != null;

    public static PcCompatResourceCompileInfo? Prepare(
        PcModManifest manifest,
        CancellationToken cancellationToken = default)
    {
        var provider = Volatile.Read(ref s_provider);
        return provider?.Invoke(manifest, cancellationToken);
    }
}

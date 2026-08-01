namespace Xphorror.PcModCompat;

public sealed class PcCompatLoadOptions
{
    public string? ShimFolder { get; init; }
    public string? TargetAssemblyPath { get; init; }
    public string? BootstrapAssemblyPath { get; init; }
    public IReadOnlyDictionary<string, string>? RewrittenAssemblyPaths { get; init; }
    public string? ProxyFolder { get; init; }
    public bool AllowLegacyStubExecution { get; init; }
    public bool TryBootstrap { get; init; } = true;
    public bool Enable { get; init; }
}

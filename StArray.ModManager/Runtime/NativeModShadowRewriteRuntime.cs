namespace StArray.ModManager.Runtime;

public sealed record NativeModShadowRewriteRequest(
    string InputAssemblyPath,
    string OutputAssemblyPath,
    IReadOnlyDictionary<string, string>? PrivateAssemblyPaths = null);

public sealed record NativeModShadowStaticSlotRecord(
    int StaticSlotId,
    string MemberIdentity);

public sealed record NativeModShadowAsyncRewriteRecord(
    string MemberIdentity,
    string Kind,
    int RewriteCount);

public sealed record NativeModShadowFileRewriteRecord(
    string MemberIdentity,
    string Kind,
    int RewriteCount);

public sealed record NativeModShadowNetworkRewriteRecord(
    string MemberIdentity,
    string Kind,
    int RewriteCount);

public sealed record NativeModShadowRewriteResult(
    int RewrittenInstructions,
    IReadOnlyList<string> Issues)
{
    public IReadOnlyList<NativeModShadowStaticSlotRecord> StaticSlots { get; init; } = [];
    public IReadOnlyList<NativeModShadowAsyncRewriteRecord> AsyncRewrites { get; init; } = [];
    public IReadOnlyList<NativeModShadowFileRewriteRecord> FileRewrites { get; init; } = [];
    public IReadOnlyList<NativeModShadowNetworkRewriteRecord> NetworkRewrites { get; init; } = [];
}

public delegate NativeModShadowRewriteResult NativeModShadowRewriteProvider(
    NativeModShadowRewriteRequest request);

public static class NativeModShadowRewriteRuntime
{
    private static readonly object Sync = new();
    private static string _abi = "starray-native-shadow-copy-v1";
    private static NativeModShadowRewriteProvider? _provider;
    private static bool _disabled;

    /// <summary>
    /// Whether production Native MOD loading may prepare a shadow package. The Android host
    /// disables this explicitly when the current rewrite contract is not usable; keeping the
    /// switch here makes discovery, initial load, and reload observe one decision.
    /// </summary>
    public static bool IsEnabled
    {
        get
        {
            lock (Sync)
                return !_disabled;
        }
    }

    public static void RegisterProvider(
        string? abi,
        NativeModShadowRewriteProvider? provider)
    {
        lock (Sync)
        {
            if (_disabled)
                return;

            _abi = provider == null
                ? "starray-native-shadow-copy-v1"
                : string.IsNullOrWhiteSpace(abi)
                    ? throw new ArgumentException("Shadow rewrite ABI cannot be empty.", nameof(abi))
                    : abi.Trim();
            _provider = provider;
        }
    }

    /// <summary>
    /// Disables production shadow preparation for the current host process. The package utility
    /// remains available to its isolated tests; the loader uses the original Native MOD assembly.
    /// </summary>
    internal static void Disable()
    {
        lock (Sync)
        {
            _disabled = true;
            _abi = "starray-native-shadow-disabled-v1";
            _provider = null;
        }
    }

    internal static (string Abi, NativeModShadowRewriteProvider? Provider) Snapshot()
    {
        lock (Sync)
            return (_abi, _provider);
    }
}

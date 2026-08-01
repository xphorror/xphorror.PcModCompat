namespace Xphorror.PcModCompat;

public sealed record PcCompatManagedAssetBundleRequest(
    string ModId,
    long SessionGeneration,
    string RequestedPath);

public sealed record PcCompatManagedAssetBundleRelease(
    string ModId,
    long SessionGeneration,
    object Bundle,
    bool UnloadAllLoadedObjects);

public sealed record PcCompatManagedCapabilityAssetRequest(
    string ModId,
    long SessionGeneration,
    string StableId,
    string ExpectedType);

/// <summary>
/// Owner-aware facade used by rewritten PC MOD AssetBundle callsites. The host
/// remains the sole owner of verification, loading, rooting and final unload.
/// </summary>
public static class PcCompatManagedResourceBridge
{
    private sealed record Provider(
        Func<PcCompatManagedAssetBundleRequest, object?> Acquire,
        Action<PcCompatManagedAssetBundleRelease> Release);

    private sealed record CapabilityProvider(
        Func<PcCompatManagedCapabilityAssetRequest, object?> Acquire);

    private static Provider? s_provider;
    private static CapabilityProvider? s_capabilityProvider;

    public static void RegisterAssetBundleProvider(
        Func<PcCompatManagedAssetBundleRequest, object?> acquire,
        Action<PcCompatManagedAssetBundleRelease> release)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        ArgumentNullException.ThrowIfNull(release);
        Volatile.Write(ref s_provider, new Provider(acquire, release));
    }

    public static void ClearAssetBundleProvider()
        => Volatile.Write(ref s_provider, null);

    public static void RegisterCapabilityAssetProvider(
        Func<PcCompatManagedCapabilityAssetRequest, object?> acquire)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        Volatile.Write(ref s_capabilityProvider, new CapabilityProvider(acquire));
    }

    public static void ClearCapabilityAssetProvider()
        => Volatile.Write(ref s_capabilityProvider, null);

    // Return object intentionally: the assembly rewriter inserts a cast to the
    // generated UnityEngine.AssetBundle proxy without making this core assembly
    // depend on a build-specific proxy DLL.
    public static object LoadAssetBundleFromFile(string path)
    {
        var execution = RequireExecutionContext();
        if (execution.Phase is not (
                PcCompatManagedExecutionPhase.Enable or
                PcCompatManagedExecutionPhase.Update))
        {
            throw new InvalidOperationException(
                $"AssetBundle acquisition is not allowed during {execution.Phase}.");
        }

        try
        {
            return PcCompatVirtualBundleRegistry.Acquire(
                execution.ModId,
                execution.ResourceSessionGeneration,
                path ?? string.Empty);
        }
        catch when (Volatile.Read(ref s_provider) is not null)
        {
            var provider = Volatile.Read(ref s_provider)!;
            return provider.Acquire(new PcCompatManagedAssetBundleRequest(
                       execution.ModId,
                       execution.ResourceSessionGeneration,
                       path ?? string.Empty))
                   ?? throw new InvalidOperationException(
                       $"No legacy AssetBundle belongs to mod={execution.ModId} " +
                       $"generation={execution.ResourceSessionGeneration} path={path}.");
        }
    }

    public static object LoadAssetFromBundle(object bundle, string assetName)
        => PcCompatVirtualBundleRegistry.LoadAsset(
            RequireVirtualHandle(bundle),
            assetName);

    public static object LoadAssetFromBundleWithType(object bundle, string assetName, Type expectedType)
    {
        ArgumentNullException.ThrowIfNull(expectedType);
        return PcCompatVirtualBundleRegistry.LoadAsset(
            RequireVirtualHandle(bundle),
            assetName,
            expectedType.FullName ?? expectedType.Name);
    }

    public static object LoadAssetFromBundleGeneric<T>(object bundle, string assetName)
        => PcCompatVirtualBundleRegistry.LoadAsset(
            RequireVirtualHandle(bundle),
            assetName,
            typeof(T).FullName ?? typeof(T).Name);

    public static object LoadAllAssetsFromBundle(object bundle)
        => PcCompatVirtualBundleRegistry.LoadAllAssets(
            RequireVirtualHandle(bundle));

    public static object LoadAllAssetsFromBundleWithType(object bundle, Type expectedType)
    {
        ArgumentNullException.ThrowIfNull(expectedType);
        return PcCompatVirtualBundleRegistry.LoadAllAssets(
            RequireVirtualHandle(bundle),
            expectedType.FullName ?? expectedType.Name);
    }

    public static object LoadAllAssetsFromBundleGeneric<T>(object bundle)
        => PcCompatVirtualBundleRegistry.LoadAllAssets(
            RequireVirtualHandle(bundle),
            typeof(T).FullName ?? typeof(T).Name);

    public static void ReleaseAssetBundle(object bundle, bool unloadAllLoadedObjects)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var execution = RequireExecutionContext();
        if (bundle is PcCompatVirtualBundleHandle virtualBundle)
        {
            if (!virtualBundle.ModId.Equals(execution.ModId, StringComparison.OrdinalIgnoreCase) ||
                virtualBundle.SessionGeneration != execution.ResourceSessionGeneration)
                throw new InvalidOperationException("VirtualBundle release owner mismatch.");
            PcCompatVirtualBundleRegistry.Release(virtualBundle);
            return;
        }
        var provider = Volatile.Read(ref s_provider)
            ?? throw new InvalidOperationException("Legacy managed AssetBundle provider is not registered.");
        provider.Release(new PcCompatManagedAssetBundleRelease(
            execution.ModId,
            execution.ResourceSessionGeneration,
            bundle,
            unloadAllLoadedObjects));
    }

    public static object LoadCapabilityAsset(string stableId, string expectedType)
    {
        if (string.IsNullOrWhiteSpace(stableId))
            throw new ArgumentException("Capability stable id is empty.", nameof(stableId));
        if (string.IsNullOrWhiteSpace(expectedType))
            throw new ArgumentException("Capability expected type is empty.", nameof(expectedType));
        var execution = RequireExecutionContext();
        if (execution.Phase is not (
                PcCompatManagedExecutionPhase.Setup or
                PcCompatManagedExecutionPhase.Enable or
                PcCompatManagedExecutionPhase.Update))
        {
            throw new InvalidOperationException(
                $"Capability asset acquisition is not allowed during {execution.Phase}.");
        }

        var provider = Volatile.Read(ref s_capabilityProvider)
            ?? throw new InvalidOperationException("Managed capability asset provider is not registered.");
        return provider.Acquire(new PcCompatManagedCapabilityAssetRequest(
                   execution.ModId,
                   execution.ResourceSessionGeneration,
                   stableId,
                   expectedType))
               ?? throw new InvalidOperationException(
                   $"Capability asset is not ready or unavailable id={stableId} " +
                   $"type={expectedType} mod={execution.ModId}.");
    }

    private static PcCompatManagedExecutionState RequireExecutionContext()
        => PcCompatManagedExecutionContext.Current
           ?? throw new InvalidOperationException(
                "Managed AssetBundle access occurred outside an owner-scoped MOD callback.");

    private static PcCompatVirtualBundleHandle RequireVirtualHandle(object bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var execution = RequireExecutionContext();
        if (bundle is not PcCompatVirtualBundleHandle handle)
            throw new InvalidOperationException("Rewritten AssetBundle call received a non-virtual handle.");
        if (!handle.ModId.Equals(execution.ModId, StringComparison.OrdinalIgnoreCase) ||
            handle.SessionGeneration != execution.ResourceSessionGeneration)
            throw new InvalidOperationException("VirtualBundle access owner mismatch.");
        return handle;
    }
}

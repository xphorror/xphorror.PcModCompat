using System.Runtime.CompilerServices;

namespace Xphorror.PcModCompat;

public enum PcCompatVirtualAssetResolveStatus
{
    Ready = 0,
    Pending = 1,
    Unsupported = 2,
    Failed = 3
}

public sealed class PcCompatVirtualAssetPendingException(string message) : Exception(message);

public sealed record PcCompatVirtualAssetResolveRequest(
    string ModId,
    long SessionGeneration,
    string BundleId,
    string CandidateSha256Hex,
    string ResourceIrRoot,
    PcCompatResourceIrAsset Asset,
    PcCompatResourceIrPayload? Payload);

public sealed record PcCompatVirtualAssetResolveResult(
    PcCompatVirtualAssetResolveStatus Status,
    object? Asset,
    string? Error = null,
    bool ReleaseWithSession = false);

public sealed record PcCompatVirtualAssetProjectionRequest(
    string ModId,
    long SessionGeneration,
    string BundleId,
    string CandidateSha256Hex,
    string ResourceIrRoot,
    PcCompatResourceIrAsset SourceAsset,
    PcCompatResourceIrPayload? Payload,
    string ExpectedType);

public sealed record PcCompatVirtualAssetReleaseBatch(
    string ModId,
    long SessionGeneration,
    IReadOnlyList<object> Assets);

public sealed record PcCompatVirtualAssetLeaseSnapshot(
    string ModId,
    long SessionGeneration,
    string TypeName,
    int ClaimCount);

public sealed class PcCompatVirtualBundleHandle
{
    internal PcCompatVirtualBundleHandle(
        long token,
        string modId,
        long sessionGeneration,
        string bundleId,
        string candidateSha256Hex)
    {
        Token = token;
        ModId = modId;
        SessionGeneration = sessionGeneration;
        BundleId = bundleId;
        CandidateSha256Hex = candidateSha256Hex;
    }

    internal long Token { get; }
    public string ModId { get; }
    public long SessionGeneration { get; }
    public string BundleId { get; }
    public string CandidateSha256Hex { get; }

    public override string ToString()
        => $"VirtualBundle({ModId},generation={SessionGeneration},bundle={BundleId})";
}

public sealed record PcCompatVirtualBundleRegistrySnapshot(
    int SessionCount,
    int BundleCount,
    int AssetCount,
    int ReadyAssetCount,
    int OpenHandleCount,
    int ReleaseLeaseCount);

public sealed record PcCompatVirtualBundleSessionReadiness(
    string ModId,
    long SessionGeneration,
    bool SessionPresent,
    int BundleCount,
    int RequiredAssetCount,
    int RequiredReadyCount,
    int RequiredPendingCount,
    int RequiredUnsupportedCount,
    int RequiredFailedCount,
    int OptionalAssetCount,
    int OptionalReadyCount,
    int OptionalPendingCount,
    int OptionalUnsupportedCount,
    int OptionalFailedCount,
    string? LastError)
{
    public bool IsReady =>
        SessionPresent &&
        RequiredReadyCount == RequiredAssetCount &&
        RequiredPendingCount == 0 &&
        RequiredUnsupportedCount == 0 &&
        RequiredFailedCount == 0;
}

/// <summary>
/// Owner-scoped registry behind rewritten AssetBundle calls. Imported desktop
/// bundles are descriptors only; published values are real generated Unity proxies.
/// </summary>
public static class PcCompatVirtualBundleRegistry
{
    private sealed class AssetEntry
    {
        public required PcCompatResourceIrAsset Descriptor { get; init; }
        public object? Proxy { get; set; }
        public PcCompatVirtualAssetResolveStatus Status { get; set; } = PcCompatVirtualAssetResolveStatus.Pending;
        public string? Error { get; set; }
        public bool Resolving { get; set; }
        public bool ReleaseWithSession { get; set; }
        public Dictionary<string, ProjectionEntry> Projections { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ProjectionEntry
    {
        public object? Proxy { get; set; }
        public PcCompatVirtualAssetResolveStatus Status { get; set; } = PcCompatVirtualAssetResolveStatus.Pending;
        public string? Error { get; set; }
        public bool Resolving { get; set; }
        public bool ReleaseWithSession { get; set; }
    }

    private sealed class BundleEntry
    {
        public required string Key { get; init; }
        public required string ModId { get; init; }
        public required long SessionGeneration { get; init; }
        public required string ResourceIrRoot { get; init; }
        public required PcCompatResourceIrBundle Descriptor { get; init; }
        public required IReadOnlyList<AssetEntry> OrderedAssets { get; init; }
        public required Dictionary<string, AssetEntry> AssetsById { get; init; }
        public required Dictionary<string, IReadOnlyList<AssetEntry>> AssetsByName { get; init; }
        public required Dictionary<string, PcCompatResourceIrPayload> PayloadsById { get; init; }
        public bool Unloading { get; set; }
    }

    private readonly record struct RuntimeAssetEntry(
        BundleEntry Bundle,
        AssetEntry Asset);

    private sealed class SessionEntry
    {
        public required string Key { get; init; }
        public required string ModId { get; init; }
        public required long Generation { get; init; }
        public required string ModFolder { get; init; }
        public required Dictionary<string, BundleEntry> BundlesById { get; init; }
        public required Dictionary<string, BundleEntry> BundlesByRelativePath { get; init; }
        public required Dictionary<string, BundleEntry> BundlesByUniqueFileName { get; init; }
        public required Dictionary<string, BundleEntry> PreferredBundlesByFileName { get; init; }
        public required IReadOnlyList<BundleEntry> RuntimeBundles { get; init; }
        public required IReadOnlyList<RuntimeAssetEntry> RuntimeAssets { get; init; }
        public required Dictionary<string, IReadOnlyList<RuntimeAssetEntry>> RuntimeAssetsByName { get; init; }
    }

    private sealed class HandleEntry
    {
        public required PcCompatVirtualBundleHandle Handle { get; init; }
        public required BundleEntry Bundle { get; init; }
    }

    private sealed class ReleaseLeaseEntry
    {
        public required object Asset { get; init; }
        public required string ModId { get; init; }
        public required long SessionGeneration { get; init; }
        public HashSet<string> Claims { get; } = new(StringComparer.Ordinal);
    }

    private sealed class AssetUseEntry
    {
        public required object Asset { get; init; }
        public List<AssetUseClaim> Claims { get; } = [];
    }

    private sealed record AssetUseClaim(
        string ModId,
        long SessionGeneration,
        string BundleKey,
        bool ReleaseWithSession,
        string Claim);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, SessionEntry> Sessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<long, HandleEntry> Handles = new();
    private static readonly Dictionary<object, ReleaseLeaseEntry> ReleaseLeases =
        new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<object, AssetUseEntry> AssetUses =
        new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<string, object> ModLifecycleLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private static Func<PcCompatVirtualAssetResolveRequest, PcCompatVirtualAssetResolveResult>? s_resolver;
    private static Func<PcCompatVirtualAssetProjectionRequest, PcCompatVirtualAssetResolveResult>?
        s_projectionResolver;
    private static Func<object, bool>? s_assetLivenessProbe;
    private static Func<string, IReadOnlyList<object>, object>? s_arrayFactory;
    private static Action<PcCompatVirtualAssetReleaseBatch>? s_releaseSink;
    private static long s_nextToken;

    public static void RegisterAssetResolver(
        Func<PcCompatVirtualAssetResolveRequest, PcCompatVirtualAssetResolveResult>? resolver)
        => Volatile.Write(ref s_resolver, resolver);

    public static void RegisterAssetProjectionResolver(
        Func<PcCompatVirtualAssetProjectionRequest, PcCompatVirtualAssetResolveResult>? resolver)
        => Volatile.Write(ref s_projectionResolver, resolver);

    public static void RegisterAssetLivenessProbe(Func<object, bool>? probe)
        => Volatile.Write(ref s_assetLivenessProbe, probe);

    public static void RegisterArrayFactory(
        Func<string, IReadOnlyList<object>, object>? arrayFactory)
        => Volatile.Write(ref s_arrayFactory, arrayFactory);

    public static void RegisterAssetReleaseSink(Action<PcCompatVirtualAssetReleaseBatch>? releaseSink)
        => Volatile.Write(ref s_releaseSink, releaseSink);

    public static bool HasSession(string modId, long sessionGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        if (sessionGeneration <= 0)
            return false;
        lock (Gate)
            return Sessions.ContainsKey(MakeSessionKey(modId, sessionGeneration));
    }

    public static bool TryPrepareRequiredAssets(
        string modId,
        long sessionGeneration,
        out string? pendingReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        if (sessionGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(sessionGeneration));

        IReadOnlyList<BundleEntry> bundles;
        lock (Gate)
        {
            if (!Sessions.TryGetValue(
                    MakeSessionKey(modId, sessionGeneration),
                    out var session))
            {
                throw new InvalidOperationException(
                    $"VirtualBundle session is unavailable mod={modId} generation={sessionGeneration}.");
            }
            bundles = session.RuntimeBundles;
        }

        foreach (var bundle in bundles)
        {
            foreach (var asset in bundle.OrderedAssets)
            {
                if (!asset.Descriptor.RequiredByMod)
                    continue;
                if (asset.Descriptor.MaterializationKind is
                    PcCompatResourceIrMaterializationKind.MetadataOnly or
                    PcCompatResourceIrMaterializationKind.Unsupported)
                {
                    throw new InvalidOperationException(
                        $"Required VirtualBundle asset has no materializer id={asset.Descriptor.Id} " +
                        $"name={asset.Descriptor.Name} type={asset.Descriptor.ExpectedType}.");
                }
                try
                {
                    PcCompatDeepDebug.WriteSampled(
                        "virtual-required",
                        modId + "\0" + sessionGeneration + "\0" + asset.Descriptor.Id,
                        count =>
                            $"phase=prepare-begin count={count} mod={modId} generation={sessionGeneration} " +
                            $"bundle={bundle.Descriptor.Id} id={asset.Descriptor.Id} " +
                            $"name={PcCompatDeepDebug.Sanitize(asset.Descriptor.Name)} " +
                            $"type={asset.Descriptor.ExpectedType} kind={asset.Descriptor.MaterializationKind} " +
                            $"status={asset.Status} proxy={PcCompatDeepDebug.DescribeObject(asset.Proxy)}",
                        periodic: 64);
                    var prepared = RequireLiveAsset(bundle, asset, ResolveAsset(bundle, asset));
                    PcCompatDeepDebug.WriteSampled(
                        "virtual-required",
                        modId + "\0" + sessionGeneration + "\0" + asset.Descriptor.Id + "\0ready",
                        count =>
                            $"phase=prepare-ready count={count} mod={modId} generation={sessionGeneration} " +
                            $"bundle={bundle.Descriptor.Id} id={asset.Descriptor.Id} " +
                            $"name={PcCompatDeepDebug.Sanitize(asset.Descriptor.Name)} " +
                            $"status={asset.Status} releaseWithSession={asset.ReleaseWithSession} " +
                            $"proxy={PcCompatDeepDebug.DescribeObject(prepared)}",
                        periodic: 64);
                }
                catch (PcCompatVirtualAssetPendingException exception)
                {
                    PcCompatDeepDebug.Write(
                        "virtual-required",
                        $"phase=prepare-pending mod={modId} generation={sessionGeneration} " +
                        $"bundle={bundle.Descriptor.Id} id={asset.Descriptor.Id} " +
                        $"name={PcCompatDeepDebug.Sanitize(asset.Descriptor.Name)} " +
                        $"status={asset.Status} error={PcCompatDeepDebug.Sanitize(exception.Message)}");
                    pendingReason = exception.Message;
                    return false;
                }
            }
        }

        pendingReason = null;
        return true;
    }

    public static void RegisterSession(
        string modId,
        long sessionGeneration,
        string modFolder,
        string resourceIrPath,
        PcCompatResourceIrDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceIrPath);
        ArgumentNullException.ThrowIfNull(document);
        if (sessionGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(sessionGeneration));
        if (!PcCompatResourceIr.TryValidateDocument(document, modId, out var error))
            throw new InvalidDataException("VirtualBundle rejected Resource IR: " + error);

        var normalizedModFolder = Path.GetFullPath(modFolder);
        var irRoot = Path.GetDirectoryName(Path.GetFullPath(resourceIrPath))
                     ?? throw new InvalidDataException("Resource IR has no parent directory.");
        var assetsByBundle = document.Assets
            .GroupBy(asset => asset.BundleId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var payloadsById = document.Payloads.ToDictionary(payload => payload.Id, StringComparer.Ordinal);
        var bundles = new Dictionary<string, BundleEntry>(StringComparer.Ordinal);
        var relativePaths = new Dictionary<string, BundleEntry>(StringComparer.OrdinalIgnoreCase);
        var fileGroups = new Dictionary<string, List<BundleEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in document.Bundles)
        {
            var assetDescriptors = assetsByBundle.TryGetValue(descriptor.Id, out var values)
                ? values
                : Array.Empty<PcCompatResourceIrAsset>();
            var ordered = assetDescriptors.Select(asset => new AssetEntry { Descriptor = asset }).ToArray();
            var byId = ordered.ToDictionary(asset => asset.Descriptor.Id, StringComparer.Ordinal);
            var byName = ordered.GroupBy(asset => asset.Descriptor.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<AssetEntry>)group.ToArray(), StringComparer.Ordinal);
            var bundle = new BundleEntry
            {
                Key = MakeBundleKey(modId, sessionGeneration, descriptor.Id),
                ModId = modId,
                SessionGeneration = sessionGeneration,
                ResourceIrRoot = irRoot,
                Descriptor = descriptor,
                OrderedAssets = ordered,
                AssetsById = byId,
                AssetsByName = byName,
                PayloadsById = payloadsById
            };
            if (!bundles.TryAdd(descriptor.Id, bundle))
                throw new InvalidDataException($"Duplicate VirtualBundle id: {descriptor.Id}");
            var relative = NormalizeRelativePath(descriptor.SourceRelativePath);
            if (!relativePaths.TryAdd(relative, bundle))
                throw new InvalidDataException($"Duplicate VirtualBundle relative path: {relative}");
            if (!fileGroups.TryGetValue(descriptor.SourceFileName, out var sameName))
                fileGroups[descriptor.SourceFileName] = sameName = new List<BundleEntry>();
            sameName.Add(bundle);
        }
        var uniqueFiles = fileGroups
            .Where(pair => pair.Value.Count == 1)
            .ToDictionary(pair => pair.Key, pair => pair.Value[0], StringComparer.OrdinalIgnoreCase);
        var preferredFiles = fileGroups
            .Select(pair => new
            {
                pair.Key,
                Values = pair.Value.Where(bundle => bundle.Descriptor.SelectedForRuntime).ToArray()
            })
            .Where(pair => pair.Values.Length == 1)
            .ToDictionary(pair => pair.Key, pair => pair.Values[0], StringComparer.OrdinalIgnoreCase);
        var runtimeBundles = bundles.Values
            .Where(bundle => bundle.Descriptor.SelectedForRuntime)
            .OrderBy(bundle => bundle.Descriptor.SourceRelativePath, StringComparer.Ordinal)
            .ToArray();
        if (runtimeBundles.Length == 0)
        {
            runtimeBundles = bundles.Values
                .OrderBy(bundle => bundle.Descriptor.SourceRelativePath, StringComparer.Ordinal)
                .ToArray();
        }
        var runtimeAssets = runtimeBundles
            .SelectMany(bundle => bundle.OrderedAssets.Select(asset =>
                new RuntimeAssetEntry(bundle, asset)))
            .ToArray();
        var runtimeAssetsByName = runtimeAssets
            .GroupBy(value => value.Asset.Descriptor.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<RuntimeAssetEntry>)group.ToArray(),
                StringComparer.Ordinal);
        var session = new SessionEntry
        {
            Key = MakeSessionKey(modId, sessionGeneration),
            ModId = modId,
            Generation = sessionGeneration,
            ModFolder = normalizedModFolder,
            BundlesById = bundles,
            BundlesByRelativePath = relativePaths,
            BundlesByUniqueFileName = uniqueFiles,
            PreferredBundlesByFileName = preferredFiles,
            RuntimeBundles = runtimeBundles,
            RuntimeAssets = runtimeAssets,
            RuntimeAssetsByName = runtimeAssetsByName
        };

        var lifecycleLock = GetModLifecycleLock(modId);
        lock (lifecycleLock)
        {
            RemoveModCore(modId);
            lock (Gate)
                Sessions.Add(session.Key, session);
        }
        PcCompatDeepDebug.Write(
            "virtual-session",
            $"action=register mod={modId} generation={sessionGeneration} " +
            $"irRoot={PcCompatDeepDebug.Sanitize(irRoot)} bundles={runtimeBundles.Length} " +
            $"assets={runtimeAssets.Length} required={runtimeAssets.Count(value => value.Asset.Descriptor.RequiredByMod)}");
        foreach (var runtimeAsset in runtimeAssets)
        {
            var descriptor = runtimeAsset.Asset.Descriptor;
            var payload = string.IsNullOrWhiteSpace(descriptor.PayloadId)
                ? null
                : runtimeAsset.Bundle.PayloadsById.GetValueOrDefault(descriptor.PayloadId);
            PcCompatDeepDebug.Write(
                "virtual-session-asset",
                $"mod={modId} generation={sessionGeneration} bundle={runtimeAsset.Bundle.Descriptor.Id} " +
                $"selected={runtimeAsset.Bundle.Descriptor.SelectedForRuntime} id={descriptor.Id} " +
                $"name={PcCompatDeepDebug.Sanitize(descriptor.Name)} sourceType={descriptor.SourceType} " +
                $"expectedType={descriptor.ExpectedType} required={descriptor.RequiredByMod} " +
                $"kind={descriptor.MaterializationKind} compatibility={descriptor.Compatibility} " +
                $"capability={PcCompatDeepDebug.Sanitize(descriptor.CapabilityStableId)} " +
                $"cloneCapability={descriptor.CloneCapabilityAsset} dependencies=[{string.Join(',', descriptor.DependencyIds)}] " +
                $"payloadId={descriptor.PayloadId} payloadKind={payload?.Kind ?? "<none>"} " +
                $"payloadPath={PcCompatDeepDebug.Sanitize(payload?.RelativePath)} " +
                $"payloadLength={payload?.Length ?? 0} payloadSha={payload?.Sha256Hex ?? "<none>"}");
        }
    }

    public static PcCompatVirtualBundleHandle Acquire(
        string modId,
        long sessionGeneration,
        string requestedPath)
    {
        lock (Gate)
        {
            if (!Sessions.TryGetValue(MakeSessionKey(modId, sessionGeneration), out var session))
                throw new InvalidOperationException(
                    $"VirtualBundle session is unavailable mod={modId} generation={sessionGeneration}.");
            var bundle = ResolveBundleLocked(session, requestedPath)
                         ?? throw new InvalidOperationException(
                             $"VirtualBundle path is missing or ambiguous mod={modId} " +
                             $"generation={sessionGeneration} path={requestedPath}.");
            if (bundle.Unloading)
                throw new InvalidOperationException(
                    $"VirtualBundle is unloading mod={modId} generation={sessionGeneration} " +
                    $"bundle={bundle.Descriptor.Id}.");
            var token = NextTokenLocked();
            var handle = new PcCompatVirtualBundleHandle(
                token,
                modId,
                sessionGeneration,
                bundle.Descriptor.Id,
                bundle.Descriptor.CandidateSha256Hex);
            Handles.Add(token, new HandleEntry { Handle = handle, Bundle = bundle });
            return handle;
        }
    }

    public static object LoadAsset(
        PcCompatVirtualBundleHandle handle,
        string assetName,
        string? expectedType = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);
        var bundle = RequireOwnedBundle(handle);
        AssetEntry asset;
        lock (Gate)
        {
            if (!bundle.AssetsByName.TryGetValue(assetName, out var named))
                throw new InvalidOperationException($"VirtualBundle asset was not found: {assetName}");
            AssetEntry? match = null;
            var matchCount = 0;
            foreach (var candidate in named)
            {
                if (!string.IsNullOrWhiteSpace(expectedType) &&
                    !TypeMatches(candidate.Descriptor.ExpectedType, expectedType!))
                    continue;
                match = candidate;
                matchCount++;
            }
            if (matchCount != 1)
                throw new InvalidOperationException(
                    $"VirtualBundle asset lookup is ambiguous or type-incompatible name={assetName} " +
                    $"type={expectedType ?? "<any>"} matches={matchCount}.");
            asset = match!;
        }
        PcCompatDeepDebug.WriteSampled(
            "virtual-load",
            handle.ModId + "\0" + handle.SessionGeneration + "\0" + asset.Descriptor.Id,
            count =>
                $"phase=begin count={count} mod={handle.ModId} generation={handle.SessionGeneration} " +
                $"handle={handle.Token} bundle={bundle.Descriptor.Id} requestName={PcCompatDeepDebug.Sanitize(assetName)} " +
                $"requestType={expectedType ?? "<any>"} selectedId={asset.Descriptor.Id} " +
                $"selectedType={asset.Descriptor.ExpectedType} required={asset.Descriptor.RequiredByMod} " +
                $"kind={asset.Descriptor.MaterializationKind} status={asset.Status} " +
                $"cachedProxy={PcCompatDeepDebug.DescribeObject(asset.Proxy)}",
            periodic: 256);
        var resolved = RequireLiveAsset(bundle, asset, ResolveAsset(bundle, asset));
        PcCompatDeepDebug.WriteSampled(
            "virtual-load",
            handle.ModId + "\0" + handle.SessionGeneration + "\0" + asset.Descriptor.Id + "\0return",
            count =>
                $"phase=return count={count} mod={handle.ModId} generation={handle.SessionGeneration} " +
                $"handle={handle.Token} bundle={bundle.Descriptor.Id} selectedId={asset.Descriptor.Id} " +
                $"status={asset.Status} releaseWithSession={asset.ReleaseWithSession} " +
                $"returned={PcCompatDeepDebug.DescribeObject(resolved)}",
            periodic: 256);
        return resolved;
    }

    public static object LoadAllAssets(
        PcCompatVirtualBundleHandle handle,
        string expectedType = "UnityEngine.Object")
    {
        ArgumentNullException.ThrowIfNull(handle);
        var bundle = RequireOwnedBundle(handle);
        var values = new List<object>();
        foreach (var asset in bundle.OrderedAssets)
        {
            if (!TypeMatches(asset.Descriptor.ExpectedType, expectedType))
                continue;
            if (asset.Descriptor.MaterializationKind is
                PcCompatResourceIrMaterializationKind.MetadataOnly or
                PcCompatResourceIrMaterializationKind.Unsupported)
            {
                if (asset.Descriptor.RequiredByMod)
                {
                    throw new InvalidOperationException(
                        $"Required VirtualBundle asset has no materializer id={asset.Descriptor.Id} " +
                        $"name={asset.Descriptor.Name} type={asset.Descriptor.ExpectedType}.");
                }
                continue;
            }
            try
            {
                values.Add(RequireLiveAsset(bundle, asset, ResolveAsset(bundle, asset)));
            }
            catch when (!asset.Descriptor.RequiredByMod)
            {
                // Optional source assets are omitted when the MOD never proved a use.
            }
        }
        var factory = Volatile.Read(ref s_arrayFactory)
                      ?? throw new InvalidOperationException("VirtualBundle Unity array factory is not registered.");
        return factory(expectedType, values);
    }

    public static object ResolveAssetById(
        string modId,
        long sessionGeneration,
        string bundleId,
        string assetId)
    {
        BundleEntry bundle;
        AssetEntry asset;
        lock (Gate)
        {
            if (!Sessions.TryGetValue(MakeSessionKey(modId, sessionGeneration), out var session) ||
                !session.BundlesById.TryGetValue(bundleId, out bundle!) ||
                !bundle.AssetsById.TryGetValue(assetId, out asset!))
                throw new InvalidOperationException($"VirtualBundle dependency is unavailable: {assetId}");
        }
        return RequireLiveAsset(bundle, asset, ResolveAsset(bundle, asset));
    }

    public static PcCompatVirtualAssetResolveResult ResolvePreferredAsset(
        string modId,
        long sessionGeneration,
        string expectedType,
        params string[] projectionSourceTypes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedType);
        if (sessionGeneration <= 0)
        {
            return new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Unsupported,
                null,
                "VirtualBundle session generation is unavailable.");
        }

        BundleEntry? bundle;
        AssetEntry? asset;
        string? error;
        lock (Gate)
        {
            if (!Sessions.TryGetValue(MakeSessionKey(modId, sessionGeneration), out var session))
            {
                return new PcCompatVirtualAssetResolveResult(
                    PcCompatVirtualAssetResolveStatus.Unsupported,
                    null,
                    $"VirtualBundle session is unavailable mod={modId} generation={sessionGeneration}.");
            }

            (bundle, asset, error) = SelectUniqueAssetLocked(session, expectedType);
            if (asset == null && error == null)
            {
                foreach (var sourceType in projectionSourceTypes)
                {
                    if (string.IsNullOrWhiteSpace(sourceType))
                        continue;
                    (bundle, asset, error) = SelectUniqueAssetLocked(session, sourceType);
                    if (asset != null || error != null)
                        break;
                }
            }
        }

        if (error != null)
        {
            return new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Failed,
                null,
                error);
        }
        if (bundle == null || asset == null)
        {
            return new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Unsupported,
                null,
                $"VirtualBundle has no unique asset for type {expectedType}.");
        }

        try
        {
            return TypeMatches(asset.Descriptor.ExpectedType, expectedType)
                ? new PcCompatVirtualAssetResolveResult(
                    PcCompatVirtualAssetResolveStatus.Ready,
                    RequireLiveAsset(bundle, asset, ResolveAsset(bundle, asset)))
                : ResolveProjection(bundle, asset, expectedType);
        }
        catch (Exception exception)
        {
            return new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Failed,
                null,
                exception.GetBaseException().Message);
        }
    }

    public static PcCompatVirtualAssetResolveResult ResolveNamedAsset(
        string modId,
        long sessionGeneration,
        string assetName,
        string expectedType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedType);

        BundleEntry? bundle;
        AssetEntry? asset;
        lock (Gate)
        {
            if (!Sessions.TryGetValue(MakeSessionKey(modId, sessionGeneration), out var session))
            {
                return new PcCompatVirtualAssetResolveResult(
                    PcCompatVirtualAssetResolveStatus.Unsupported,
                    null,
                    $"VirtualBundle session is unavailable mod={modId} generation={sessionGeneration}.");
            }

            if (!session.RuntimeAssetsByName.TryGetValue(assetName, out var named))
            {
                return new PcCompatVirtualAssetResolveResult(
                    PcCompatVirtualAssetResolveStatus.Unsupported,
                    null,
                    $"VirtualBundle has no asset name={assetName} type={expectedType}.");
            }
            RuntimeAssetEntry? match = null;
            var matchCount = 0;
            foreach (var candidate in named)
            {
                if (!TypeMatches(candidate.Asset.Descriptor.ExpectedType, expectedType) ||
                    candidate.Asset.Descriptor.MaterializationKind is
                        PcCompatResourceIrMaterializationKind.MetadataOnly or
                        PcCompatResourceIrMaterializationKind.Unsupported)
                    continue;
                match = candidate;
                matchCount++;
            }
            if (matchCount != 1)
            {
                return new PcCompatVirtualAssetResolveResult(
                    matchCount == 0
                        ? PcCompatVirtualAssetResolveStatus.Unsupported
                        : PcCompatVirtualAssetResolveStatus.Failed,
                    null,
                    matchCount == 0
                        ? $"VirtualBundle has no asset name={assetName} type={expectedType}."
                        : $"VirtualBundle asset selection is ambiguous name={assetName} " +
                          $"type={expectedType} matches={matchCount}.");
            }
            bundle = match!.Value.Bundle;
            asset = match.Value.Asset;
        }

        try
        {
            return new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Ready,
                RequireLiveAsset(bundle, asset, ResolveAsset(bundle, asset)));
        }
        catch (Exception exception)
        {
            return new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Failed,
                null,
                exception.GetBaseException().Message);
        }
    }

    public static void Release(PcCompatVirtualBundleHandle handle)
        => Release(handle, unloadAllLoadedObjects: false);

    public static void Release(
        PcCompatVirtualBundleHandle handle,
        bool unloadAllLoadedObjects)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!unloadAllLoadedObjects)
        {
            lock (Gate)
            {
                if (!Handles.TryGetValue(handle.Token, out var entry) ||
                    !ReferenceEquals(entry.Handle, handle))
                {
                    throw new InvalidOperationException(
                        "VirtualBundle handle is stale or does not belong to this registry.");
                }
                Handles.Remove(handle.Token);
            }
            return;
        }

        var lifecycleLock = GetModLifecycleLock(handle.ModId);
        lock (lifecycleLock)
            ReleaseBundleAndLoadedAssets(handle);
    }

    private static void ReleaseBundleAndLoadedAssets(PcCompatVirtualBundleHandle handle)
    {
        BundleEntry bundle;
        PcCompatVirtualAssetReleaseBatch release;
        lock (Gate)
        {
            if (!Handles.TryGetValue(handle.Token, out var entry) || !ReferenceEquals(entry.Handle, handle))
                throw new InvalidOperationException("VirtualBundle handle is stale or does not belong to this registry.");
            bundle = entry.Bundle;
            if (bundle.Unloading)
                throw new InvalidOperationException("VirtualBundle is already unloading.");
            if (bundle.OrderedAssets.Any(asset =>
                    asset.Resolving || asset.Projections.Values.Any(projection => projection.Resolving)))
            {
                throw new InvalidOperationException(
                    "VirtualBundle cannot unload while asset materialization is in progress.");
            }
            Handles.Remove(handle.Token);
            bundle.Unloading = true;
            release = BuildReleaseBatchLocked(
                bundle.ModId,
                bundle.SessionGeneration,
                [bundle]);
            ResetMaterializedAssetsLocked(bundle);
        }

        try
        {
            if (release.Assets.Count != 0)
                Volatile.Read(ref s_releaseSink)?.Invoke(release);
        }
        finally
        {
            lock (Gate)
            {
                RemoveBundleClaimsLocked(bundle);
                bundle.Unloading = false;
            }
        }
    }

    public static void RemoveMod(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        var lifecycleLock = GetModLifecycleLock(modId);
        lock (lifecycleLock)
            RemoveModCore(modId);
    }

    private static void RemoveModCore(string modId)
    {
        IReadOnlyList<PcCompatVirtualAssetReleaseBatch> releases;
        lock (Gate)
            releases = RemoveModLocked(modId);
        try
        {
            var sink = Volatile.Read(ref s_releaseSink);
            if (sink != null)
            {
                foreach (var release in releases)
                    sink(release);
            }
        }
        finally
        {
            lock (Gate)
            {
                foreach (var asset in AssetUses.Keys.ToArray())
                {
                    if (!AssetUses[asset].Claims.Any(claim =>
                            claim.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var remaining = AssetUses[asset].Claims
                        .Where(claim => !claim.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    if (remaining.Length == 0)
                        AssetUses.Remove(asset);
                    else
                    {
                        AssetUses[asset].Claims.Clear();
                        AssetUses[asset].Claims.AddRange(remaining);
                    }
                }
                foreach (var asset in ReleaseLeases
                             .Where(pair => pair.Value.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase))
                             .Select(pair => pair.Key)
                             .ToArray())
                    ReleaseLeases.Remove(asset);
            }
        }
    }

    public static IReadOnlyList<PcCompatVirtualAssetLeaseSnapshot> SnapshotReleaseLeases(
        string modId,
        long sessionGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        if (sessionGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(sessionGeneration));
        lock (Gate)
        {
            return ReleaseLeases.Values
                .Where(lease =>
                    lease.SessionGeneration == sessionGeneration &&
                    lease.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase))
                .Select(lease => new PcCompatVirtualAssetLeaseSnapshot(
                    lease.ModId,
                    lease.SessionGeneration,
                    lease.Asset.GetType().FullName ?? lease.Asset.GetType().Name,
                    lease.Claims.Count))
                .OrderBy(lease => lease.TypeName, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public static PcCompatVirtualBundleRegistrySnapshot GetSnapshot()
    {
        lock (Gate)
        {
            var bundles = Sessions.Values.SelectMany(session => session.BundlesById.Values).ToArray();
            var assets = bundles.SelectMany(bundle => bundle.OrderedAssets).ToArray();
            return new PcCompatVirtualBundleRegistrySnapshot(
                Sessions.Count,
                bundles.Length,
                assets.Length,
                assets.Count(asset => asset.Status == PcCompatVirtualAssetResolveStatus.Ready),
                Handles.Count,
                ReleaseLeases.Count);
        }
    }

    public static PcCompatVirtualBundleSessionReadiness GetSessionReadiness(
        string modId,
        long sessionGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        if (sessionGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(sessionGeneration));

        lock (Gate)
        {
            if (!Sessions.TryGetValue(MakeSessionKey(modId, sessionGeneration), out var session))
            {
                return new PcCompatVirtualBundleSessionReadiness(
                    modId,
                    sessionGeneration,
                    SessionPresent: false,
                    BundleCount: 0,
                    RequiredAssetCount: 0,
                    RequiredReadyCount: 0,
                    RequiredPendingCount: 0,
                    RequiredUnsupportedCount: 0,
                    RequiredFailedCount: 0,
                    OptionalAssetCount: 0,
                    OptionalReadyCount: 0,
                    OptionalPendingCount: 0,
                    OptionalUnsupportedCount: 0,
                    OptionalFailedCount: 0,
                    LastError: "VirtualBundle session is unavailable.");
            }

            var requiredReady = 0;
            var requiredPending = 0;
            var requiredUnsupported = 0;
            var requiredFailed = 0;
            var optionalReady = 0;
            var optionalPending = 0;
            var optionalUnsupported = 0;
            var optionalFailed = 0;
            string? lastError = null;
            foreach (var runtimeAsset in session.RuntimeAssets)
            {
                var asset = runtimeAsset.Asset;
                var status = EffectiveReadinessStatus(asset);
                if (asset.Descriptor.RequiredByMod)
                {
                    IncrementStatus(
                        status,
                        ref requiredReady,
                        ref requiredPending,
                        ref requiredUnsupported,
                        ref requiredFailed);
                    if (lastError == null && status is
                        PcCompatVirtualAssetResolveStatus.Unsupported or
                        PcCompatVirtualAssetResolveStatus.Failed)
                    {
                        lastError = asset.Error ??
                                    $"Required VirtualBundle asset is {status}: " +
                                    $"{asset.Descriptor.Name} ({asset.Descriptor.Id}).";
                    }
                }
                else
                {
                    IncrementStatus(
                        status,
                        ref optionalReady,
                        ref optionalPending,
                        ref optionalUnsupported,
                        ref optionalFailed);
                }
            }

            return new PcCompatVirtualBundleSessionReadiness(
                session.ModId,
                session.Generation,
                SessionPresent: true,
                BundleCount: session.RuntimeBundles.Count,
                RequiredAssetCount:
                    requiredReady + requiredPending + requiredUnsupported + requiredFailed,
                RequiredReadyCount: requiredReady,
                RequiredPendingCount: requiredPending,
                RequiredUnsupportedCount: requiredUnsupported,
                RequiredFailedCount: requiredFailed,
                OptionalAssetCount:
                    optionalReady + optionalPending + optionalUnsupported + optionalFailed,
                OptionalReadyCount: optionalReady,
                OptionalPendingCount: optionalPending,
                OptionalUnsupportedCount: optionalUnsupported,
                OptionalFailedCount: optionalFailed,
                LastError: lastError);
        }
    }

    private static PcCompatVirtualAssetResolveStatus EffectiveReadinessStatus(AssetEntry asset)
    {
        if (asset.Status != PcCompatVirtualAssetResolveStatus.Pending)
            return asset.Status;
        return asset.Descriptor.MaterializationKind is
            PcCompatResourceIrMaterializationKind.MetadataOnly or
            PcCompatResourceIrMaterializationKind.Unsupported
                ? PcCompatVirtualAssetResolveStatus.Unsupported
                : PcCompatVirtualAssetResolveStatus.Pending;
    }

    private static void IncrementStatus(
        PcCompatVirtualAssetResolveStatus status,
        ref int ready,
        ref int pending,
        ref int unsupported,
        ref int failed)
    {
        switch (status)
        {
            case PcCompatVirtualAssetResolveStatus.Ready: ++ready; break;
            case PcCompatVirtualAssetResolveStatus.Pending: ++pending; break;
            case PcCompatVirtualAssetResolveStatus.Unsupported: ++unsupported; break;
            case PcCompatVirtualAssetResolveStatus.Failed: ++failed; break;
            default: throw new ArgumentOutOfRangeException(nameof(status), status, null);
        }
    }

    private static BundleEntry RequireOwnedBundle(PcCompatVirtualBundleHandle handle)
    {
        lock (Gate)
        {
            if (!Handles.TryGetValue(handle.Token, out var entry) || !ReferenceEquals(entry.Handle, handle))
                throw new InvalidOperationException("VirtualBundle handle is stale or released.");
            if (entry.Bundle.Unloading)
                throw new InvalidOperationException("VirtualBundle is unloading.");
            return entry.Bundle;
        }
    }

    private static object ResolveAsset(BundleEntry bundle, AssetEntry asset)
    {
        Func<PcCompatVirtualAssetResolveRequest, PcCompatVirtualAssetResolveResult> resolver;
        lock (Gate)
        {
            if (!Sessions.ContainsKey(MakeSessionKey(bundle.ModId, bundle.SessionGeneration)))
                throw new InvalidOperationException("VirtualBundle session was retired during asset resolution.");
            if (bundle.Unloading)
                throw new InvalidOperationException("VirtualBundle is unloading during asset resolution.");
            if (asset.Status == PcCompatVirtualAssetResolveStatus.Ready && asset.Proxy != null)
                return asset.Proxy;
            if (asset.Status is PcCompatVirtualAssetResolveStatus.Unsupported or PcCompatVirtualAssetResolveStatus.Failed)
                throw new InvalidOperationException(asset.Error ?? "VirtualBundle asset materialization failed.");
            if (asset.Resolving)
                throw new InvalidOperationException($"VirtualBundle asset resolver re-entered id={asset.Descriptor.Id}.");
            resolver = Volatile.Read(ref s_resolver)
                       ?? throw new InvalidOperationException("VirtualBundle asset resolver is not registered.");
            asset.Resolving = true;
        }

        var request = new PcCompatVirtualAssetResolveRequest(
            bundle.ModId,
            bundle.SessionGeneration,
            bundle.Descriptor.Id,
            bundle.Descriptor.CandidateSha256Hex,
            bundle.ResourceIrRoot,
            asset.Descriptor,
            string.IsNullOrWhiteSpace(asset.Descriptor.PayloadId)
                ? null
                : bundle.PayloadsById.GetValueOrDefault(asset.Descriptor.PayloadId));
        PcCompatDeepDebug.Write(
            "virtual-materialize",
            $"phase=resolver-begin mod={bundle.ModId} generation={bundle.SessionGeneration} " +
            $"bundle={bundle.Descriptor.Id} id={asset.Descriptor.Id} " +
            $"name={PcCompatDeepDebug.Sanitize(asset.Descriptor.Name)} " +
            $"sourceType={asset.Descriptor.SourceType} expectedType={asset.Descriptor.ExpectedType} " +
            $"required={asset.Descriptor.RequiredByMod} kind={asset.Descriptor.MaterializationKind} " +
            $"payloadId={request.Payload?.Id ?? "<none>"} payloadKind={request.Payload?.Kind ?? "<none>"} " +
            $"payloadPath={PcCompatDeepDebug.Sanitize(request.Payload?.RelativePath)} " +
            $"payloadLength={request.Payload?.Length ?? 0} dependencies=[{string.Join(',', asset.Descriptor.DependencyIds)}]");
        PcCompatVirtualAssetResolveResult result;
        try
        {
            result = resolver(request);
        }
        catch (PcCompatVirtualAssetPendingException exception)
        {
            result = new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Pending,
                null,
                exception.Message);
        }
        catch (Exception ex)
        {
            result = new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Failed,
                null,
                PcCompatVirtualAssetDiagnostics.FormatResolverFailure(request, ex));
        }

        PcCompatDeepDebug.Write(
            "virtual-materialize",
            $"phase=resolver-result mod={bundle.ModId} generation={bundle.SessionGeneration} " +
            $"bundle={bundle.Descriptor.Id} id={asset.Descriptor.Id} status={result.Status} " +
            $"releaseWithSession={result.ReleaseWithSession} asset={PcCompatDeepDebug.DescribeObject(result.Asset)} " +
            $"error={PcCompatDeepDebug.Sanitize(result.Error)}");

        PcCompatVirtualAssetResolveStatus finalStatus;
        object? finalProxy;
        string? finalError;
        bool finalReleaseWithSession;
        int useClaimCount;
        int releaseClaimCount;
        lock (Gate)
        {
            asset.Resolving = false;
            if (!Sessions.ContainsKey(MakeSessionKey(bundle.ModId, bundle.SessionGeneration)))
                throw new InvalidOperationException("VirtualBundle session was retired during asset resolution.");
            if (result.Status == PcCompatVirtualAssetResolveStatus.Ready && result.Asset == null)
                result = result with { Status = PcCompatVirtualAssetResolveStatus.Failed, Error = "resolver returned Ready with null" };
            if (result is
                {
                    Status: PcCompatVirtualAssetResolveStatus.Ready,
                    Asset: not null
                })
            {
                var leaseError = TryClaimAssetUseLocked(
                    bundle,
                    result.Asset,
                    "asset:" + bundle.Descriptor.Id + ":" + asset.Descriptor.Id,
                    result.ReleaseWithSession);
                if (leaseError != null)
                {
                    result = result with
                    {
                        Status = PcCompatVirtualAssetResolveStatus.Failed,
                        Asset = null,
                        Error = leaseError,
                        ReleaseWithSession = false
                    };
                }
            }
            asset.Status = result.Status;
            asset.Proxy = result.Status == PcCompatVirtualAssetResolveStatus.Ready ? result.Asset : null;
            asset.ReleaseWithSession = result.Status == PcCompatVirtualAssetResolveStatus.Ready &&
                                       result.ReleaseWithSession;
            asset.Error = result.Error;
            finalStatus = asset.Status;
            finalProxy = asset.Proxy;
            finalError = asset.Error;
            finalReleaseWithSession = asset.ReleaseWithSession;
            useClaimCount = finalProxy != null && AssetUses.TryGetValue(finalProxy, out var uses)
                ? uses.Claims.Count
                : 0;
            releaseClaimCount = finalProxy != null && ReleaseLeases.TryGetValue(finalProxy, out var lease)
                ? lease.Claims.Count
                : 0;
        }
        PcCompatDeepDebug.Write(
            "virtual-materialize",
            $"phase=final mod={bundle.ModId} generation={bundle.SessionGeneration} " +
            $"bundle={bundle.Descriptor.Id} id={asset.Descriptor.Id} status={finalStatus} " +
            $"releaseWithSession={finalReleaseWithSession} useClaims={useClaimCount} " +
            $"releaseClaims={releaseClaimCount} proxy={PcCompatDeepDebug.DescribeObject(finalProxy)} " +
            $"error={PcCompatDeepDebug.Sanitize(finalError)}");
        if (finalStatus == PcCompatVirtualAssetResolveStatus.Ready)
            return finalProxy!;
        if (finalStatus == PcCompatVirtualAssetResolveStatus.Pending)
        {
            throw new PcCompatVirtualAssetPendingException(
                finalError ?? $"VirtualBundle asset is pending: {asset.Descriptor.Id}");
        }
        throw new InvalidOperationException(
            finalError ?? $"VirtualBundle asset is {finalStatus}: {asset.Descriptor.Id}");
    }

    private static object RequireLiveAsset(BundleEntry bundle, AssetEntry asset, object proxy)
    {
        var probe = Volatile.Read(ref s_assetLivenessProbe);
        if (probe == null)
        {
            TraceLiveAssetReturn(bundle, asset, proxy, "unprobed", live: true);
            return proxy;
        }

        string error;
        var destroyed = false;
        try
        {
            if (probe(proxy))
            {
                TraceLiveAssetReturn(bundle, asset, proxy, "probe-accepted", live: true);
                return proxy;
            }
            error = "resolved to a destroyed Unity object";
            destroyed = true;
        }
        catch (Exception exception)
        {
            error = "liveness probe failed: " + exception.GetBaseException().Message;
        }

        var message = $"VirtualBundle asset {error} mod={bundle.ModId} " +
                      $"generation={bundle.SessionGeneration} id={asset.Descriptor.Id} " +
                      $"name={asset.Descriptor.Name} type={asset.Descriptor.ExpectedType} " +
                      $"kind={asset.Descriptor.MaterializationKind} bundle={bundle.Descriptor.Id} " +
                      $"source={bundle.Descriptor.SourceRelativePath} " +
                      $"selected={bundle.Descriptor.SelectedForRuntime}.";
        lock (Gate)
        {
            if (destroyed && ReferenceEquals(asset.Proxy, proxy))
            {
                asset.Status = PcCompatVirtualAssetResolveStatus.Failed;
                asset.Proxy = null;
                asset.ReleaseWithSession = false;
                asset.Error = message;
            }
        }
        PcCompatDeepDebug.Write(
            "virtual-liveness",
            $"outcome=rejected mod={bundle.ModId} generation={bundle.SessionGeneration} " +
            $"bundle={bundle.Descriptor.Id} id={asset.Descriptor.Id} " +
            $"name={PcCompatDeepDebug.Sanitize(asset.Descriptor.Name)} " +
            $"proxy={PcCompatDeepDebug.DescribeObject(proxy)} destroyed={destroyed} " +
            $"error={PcCompatDeepDebug.Sanitize(error)}");
        throw new InvalidOperationException(message);
    }

    private static void TraceLiveAssetReturn(
        BundleEntry bundle,
        AssetEntry asset,
        object proxy,
        string probeResult,
        bool live)
    {
        int useClaims;
        int releaseClaims;
        lock (Gate)
        {
            useClaims = AssetUses.TryGetValue(proxy, out var uses) ? uses.Claims.Count : 0;
            releaseClaims = ReleaseLeases.TryGetValue(proxy, out var lease) ? lease.Claims.Count : 0;
        }
        PcCompatDeepDebug.WriteSampled(
            "virtual-liveness",
            bundle.ModId + "\0" + bundle.SessionGeneration + "\0" + asset.Descriptor.Id + "\0" +
            RuntimeHelpers.GetHashCode(proxy),
            count =>
                $"outcome=accepted count={count} mod={bundle.ModId} generation={bundle.SessionGeneration} " +
                $"bundle={bundle.Descriptor.Id} id={asset.Descriptor.Id} " +
                $"name={PcCompatDeepDebug.Sanitize(asset.Descriptor.Name)} " +
                $"type={asset.Descriptor.ExpectedType} required={asset.Descriptor.RequiredByMod} " +
                $"kind={asset.Descriptor.MaterializationKind} status={asset.Status} live={live} " +
                $"probe={probeResult} releaseWithSession={asset.ReleaseWithSession} " +
                $"useClaims={useClaims} releaseClaims={releaseClaims} " +
                $"proxy={PcCompatDeepDebug.DescribeObject(proxy)}",
            periodic: 256);
    }

    private static PcCompatVirtualAssetResolveResult ResolveProjection(
        BundleEntry bundle,
        AssetEntry asset,
        string expectedType)
    {
        var projectionKey = NormalizeType(expectedType);
        Func<PcCompatVirtualAssetProjectionRequest, PcCompatVirtualAssetResolveResult> resolver;
        ProjectionEntry projection;
        lock (Gate)
        {
            if (!Sessions.ContainsKey(MakeSessionKey(bundle.ModId, bundle.SessionGeneration)))
                throw new InvalidOperationException("VirtualBundle session was retired during asset projection.");
            if (bundle.Unloading)
                throw new InvalidOperationException("VirtualBundle is unloading during asset projection.");
            if (!asset.Projections.TryGetValue(projectionKey, out projection!))
            {
                projection = new ProjectionEntry();
                asset.Projections.Add(projectionKey, projection);
            }
            if (projection.Status == PcCompatVirtualAssetResolveStatus.Ready && projection.Proxy != null)
            {
                return new PcCompatVirtualAssetResolveResult(
                    PcCompatVirtualAssetResolveStatus.Ready,
                    projection.Proxy);
            }
            if (projection.Status is PcCompatVirtualAssetResolveStatus.Unsupported or
                PcCompatVirtualAssetResolveStatus.Failed)
            {
                return new PcCompatVirtualAssetResolveResult(
                    projection.Status,
                    null,
                    projection.Error);
            }
            if (projection.Resolving)
                throw new InvalidOperationException($"VirtualBundle projection re-entered id={asset.Descriptor.Id}.");
            resolver = Volatile.Read(ref s_projectionResolver)
                       ?? throw new InvalidOperationException(
                           "VirtualBundle asset projection resolver is not registered.");
            projection.Resolving = true;
        }

        var request = new PcCompatVirtualAssetProjectionRequest(
            bundle.ModId,
            bundle.SessionGeneration,
            bundle.Descriptor.Id,
            bundle.Descriptor.CandidateSha256Hex,
            bundle.ResourceIrRoot,
            asset.Descriptor,
            string.IsNullOrWhiteSpace(asset.Descriptor.PayloadId)
                ? null
                : bundle.PayloadsById.GetValueOrDefault(asset.Descriptor.PayloadId),
            projectionKey);
        PcCompatDeepDebug.Write(
            "virtual-projection",
            $"phase=resolver-begin mod={bundle.ModId} generation={bundle.SessionGeneration} " +
            $"bundle={bundle.Descriptor.Id} sourceId={asset.Descriptor.Id} " +
            $"sourceName={PcCompatDeepDebug.Sanitize(asset.Descriptor.Name)} " +
            $"sourceType={asset.Descriptor.ExpectedType} targetType={projectionKey} " +
            $"required={asset.Descriptor.RequiredByMod} kind={asset.Descriptor.MaterializationKind} " +
            $"sourceProxy={PcCompatDeepDebug.DescribeObject(asset.Proxy)}");
        PcCompatVirtualAssetResolveResult result;
        try
        {
            result = resolver(request);
        }
        catch (PcCompatVirtualAssetPendingException exception)
        {
            result = new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Pending,
                null,
                exception.Message);
        }
        catch (Exception exception)
        {
            result = new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Failed,
                null,
                PcCompatVirtualAssetDiagnostics.FormatProjectionFailure(request, exception));
        }

        PcCompatDeepDebug.Write(
            "virtual-projection",
            $"phase=resolver-result mod={bundle.ModId} generation={bundle.SessionGeneration} " +
            $"bundle={bundle.Descriptor.Id} sourceId={asset.Descriptor.Id} targetType={projectionKey} " +
            $"status={result.Status} releaseWithSession={result.ReleaseWithSession} " +
            $"asset={PcCompatDeepDebug.DescribeObject(result.Asset)} " +
            $"error={PcCompatDeepDebug.Sanitize(result.Error)}");

        lock (Gate)
        {
            projection.Resolving = false;
            if (!Sessions.ContainsKey(MakeSessionKey(bundle.ModId, bundle.SessionGeneration)))
                throw new InvalidOperationException("VirtualBundle session was retired during asset projection.");
            if (result.Status == PcCompatVirtualAssetResolveStatus.Ready && result.Asset == null)
                result = result with { Status = PcCompatVirtualAssetResolveStatus.Failed, Error = "projection returned Ready with null" };
            if (result is
                {
                    Status: PcCompatVirtualAssetResolveStatus.Ready,
                    Asset: not null
                })
            {
                var leaseError = TryClaimAssetUseLocked(
                    bundle,
                    result.Asset,
                    "projection:" + bundle.Descriptor.Id + ":" +
                    asset.Descriptor.Id + ":" + projectionKey,
                    result.ReleaseWithSession);
                if (leaseError != null)
                {
                    result = result with
                    {
                        Status = PcCompatVirtualAssetResolveStatus.Failed,
                        Asset = null,
                        Error = leaseError,
                        ReleaseWithSession = false
                    };
                }
            }
            projection.Status = result.Status;
            projection.Proxy = result.Status == PcCompatVirtualAssetResolveStatus.Ready ? result.Asset : null;
            projection.ReleaseWithSession = result.Status == PcCompatVirtualAssetResolveStatus.Ready &&
                                            result.ReleaseWithSession;
            projection.Error = result.Error;
            return result.Status == PcCompatVirtualAssetResolveStatus.Ready
                ? result with { Asset = projection.Proxy }
                : result;
        }
    }

    private static (BundleEntry? Bundle, AssetEntry? Asset, string? Error) SelectUniqueAssetLocked(
        SessionEntry session,
        string expectedType)
    {
        RuntimeAssetEntry? selected = null;
        var matchCount = 0;
        RuntimeAssetEntry? requiredSelected = null;
        var requiredCount = 0;
        foreach (var candidate in session.RuntimeAssets)
        {
            if (!TypeMatches(candidate.Asset.Descriptor.ExpectedType, expectedType) ||
                candidate.Asset.Descriptor.MaterializationKind is
                    PcCompatResourceIrMaterializationKind.MetadataOnly or
                    PcCompatResourceIrMaterializationKind.Unsupported)
                continue;
            selected = candidate;
            matchCount++;
            if (candidate.Asset.Descriptor.RequiredByMod)
            {
                requiredSelected = candidate;
                requiredCount++;
            }
        }
        if (requiredCount != 0)
        {
            selected = requiredSelected;
            matchCount = requiredCount;
        }
        return matchCount switch
        {
            0 => (null, null, null),
            1 => (selected!.Value.Bundle, selected.Value.Asset, null),
            _ => (null, null,
                $"VirtualBundle asset selection is ambiguous type={expectedType} matches={matchCount}.")
        };
    }

    private static BundleEntry? ResolveBundleLocked(SessionEntry session, string requestedPath)
    {
        var normalized = NormalizeRequestedPath(session.ModFolder, requestedPath);
        var fileName = normalized == null ? string.Empty : Path.GetFileName(normalized);
        if (!string.IsNullOrWhiteSpace(fileName) &&
            session.PreferredBundlesByFileName.TryGetValue(fileName, out var preferred))
            return preferred;
        if (normalized != null && session.BundlesByRelativePath.TryGetValue(normalized, out var relative))
            return relative;
        if (!string.IsNullOrWhiteSpace(fileName) &&
            session.BundlesByUniqueFileName.TryGetValue(fileName, out var byFile))
            return byFile;
        return session.BundlesById.Count == 1 ? session.BundlesById.Values.Single() : null;
    }

    private static string? NormalizeRequestedPath(string modFolder, string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            return null;
        try
        {
            var relative = Path.IsPathRooted(requestedPath)
                ? Path.GetRelativePath(modFolder, Path.GetFullPath(requestedPath))
                : requestedPath;
            relative = NormalizeRelativePath(relative);
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
                return null;
            return relative;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeRelativePath(string path)
        => path.Replace('\\', '/').TrimStart('/');

    private static bool TypeMatches(string descriptorType, string requestedType)
    {
        var expected = NormalizeType(descriptorType);
        var requested = NormalizeType(requestedType);
        return requested == "UnityEngine.Object" || expected == requested ||
               expected.EndsWith('.' + requested, StringComparison.Ordinal) ||
               requested.EndsWith('.' + expected, StringComparison.Ordinal);
    }

    private static string NormalizeType(string value)
        => value.Trim() switch
        {
            "Object" => "UnityEngine.Object",
            "GameObject" => "UnityEngine.GameObject",
            "Texture" => "UnityEngine.Texture",
            "Texture2D" => "UnityEngine.Texture2D",
            "Sprite" => "UnityEngine.Sprite",
            "Material" => "UnityEngine.Material",
            "Shader" => "UnityEngine.Shader",
            "Font" => "UnityEngine.Font",
            "TMP_FontAsset" => "TMPro.TMP_FontAsset",
            var identity => identity
        };

    private static string? TryClaimReleaseLeaseLocked(
        BundleEntry bundle,
        object asset,
        string claim)
    {
        if (ReleaseLeases.TryGetValue(asset, out var existing))
        {
            if (!existing.ModId.Equals(bundle.ModId, StringComparison.OrdinalIgnoreCase) ||
                existing.SessionGeneration != bundle.SessionGeneration)
            {
                return "VirtualBundle release lease collision: proxy is owned by " +
                       $"mod={existing.ModId} generation={existing.SessionGeneration} and cannot be " +
                       $"claimed by mod={bundle.ModId} generation={bundle.SessionGeneration}.";
            }
            existing.Claims.Add(claim);
            return null;
        }

        var lease = new ReleaseLeaseEntry
        {
            Asset = asset,
            ModId = bundle.ModId,
            SessionGeneration = bundle.SessionGeneration
        };
        lease.Claims.Add(claim);
        ReleaseLeases.Add(asset, lease);
        return null;
    }

    private static string? TryClaimAssetUseLocked(
        BundleEntry bundle,
        object asset,
        string claim,
        bool releaseWithSession)
    {
        if (!AssetUses.TryGetValue(asset, out var uses))
        {
            uses = new AssetUseEntry { Asset = asset };
            AssetUses.Add(asset, uses);
        }

        var incompatible = uses.Claims.FirstOrDefault(existing =>
            (existing.ReleaseWithSession || releaseWithSession) &&
            (!existing.ModId.Equals(bundle.ModId, StringComparison.OrdinalIgnoreCase) ||
             existing.SessionGeneration != bundle.SessionGeneration ||
             !existing.BundleKey.Equals(bundle.Key, StringComparison.Ordinal)));
        if (incompatible != null)
        {
            return "VirtualBundle release lease collision: proxy is used by " +
                   $"mod={incompatible.ModId} generation={incompatible.SessionGeneration} " +
                   $"bundle={incompatible.BundleKey} " +
                   $"(release={incompatible.ReleaseWithSession.ToString().ToLowerInvariant()}) and cannot be " +
                   $"claimed by mod={bundle.ModId} generation={bundle.SessionGeneration} " +
                   $"bundle={bundle.Key} " +
                   $"(release={releaseWithSession.ToString().ToLowerInvariant()}).";
        }

        uses.Claims.Add(new AssetUseClaim(
            bundle.ModId,
            bundle.SessionGeneration,
            bundle.Key,
            releaseWithSession,
            claim));
        if (releaseWithSession)
        {
            var leaseError = TryClaimReleaseLeaseLocked(bundle, asset, claim);
            if (leaseError != null)
            {
                uses.Claims.RemoveAt(uses.Claims.Count - 1);
                if (uses.Claims.Count == 0)
                    AssetUses.Remove(asset);
                return leaseError;
            }
        }
        return null;
    }

    private static IReadOnlyList<PcCompatVirtualAssetReleaseBatch> RemoveModLocked(string modId)
    {
        var retiredSessions = Sessions.Values
            .Where(session => session.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(session => session.Generation)
            .ToArray();
        var releases = retiredSessions
            .Select(BuildReleaseBatchLocked)
            .Where(batch => batch.Assets.Count != 0)
            .ToArray();
        foreach (var key in retiredSessions.Select(session => session.Key))
            Sessions.Remove(key);
        foreach (var token in Handles
                     .Where(pair => pair.Value.Handle.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Key)
                     .ToArray())
            Handles.Remove(token);
        return releases;
    }

    private static object GetModLifecycleLock(string modId)
    {
        lock (Gate)
        {
            if (!ModLifecycleLocks.TryGetValue(modId, out var lifecycleLock))
            {
                lifecycleLock = new object();
                ModLifecycleLocks.Add(modId, lifecycleLock);
            }
            return lifecycleLock;
        }
    }

    private static PcCompatVirtualAssetReleaseBatch BuildReleaseBatchLocked(SessionEntry session)
        => BuildReleaseBatchLocked(
            session.ModId,
            session.Generation,
            session.BundlesById.Values);

    private static PcCompatVirtualAssetReleaseBatch BuildReleaseBatchLocked(
        string modId,
        long sessionGeneration,
        IEnumerable<BundleEntry> bundles)
    {
        var selectedBundles = bundles.ToArray();
        var projectedAssets = selectedBundles
            .SelectMany(bundle => bundle.OrderedAssets)
            .SelectMany(asset => asset.Projections.Values)
            .Where(projection => projection.ReleaseWithSession && projection.Proxy != null)
            .Select(projection => projection.Proxy!)
            .ToArray();
        var releaseAssets = selectedBundles
            .SelectMany(bundle => bundle.OrderedAssets)
            .Where(asset => asset.ReleaseWithSession && asset.Proxy != null)
            .ToArray();
        var ordinals = releaseAssets
            .Select((asset, ordinal) => (asset, ordinal))
            .ToDictionary(pair => pair.asset, pair => pair.ordinal);
        var byId = releaseAssets
            .GroupBy(asset => asset.Descriptor.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var edges = releaseAssets.ToDictionary(
            asset => asset,
            _ => new List<AssetEntry>());
        var indegrees = releaseAssets.ToDictionary(asset => asset, _ => 0);
        foreach (var asset in releaseAssets)
        foreach (var dependencyId in asset.Descriptor.DependencyIds)
        {
            if (!byId.TryGetValue(dependencyId, out var dependency) ||
                ReferenceEquals(asset, dependency) || edges[asset].Contains(dependency))
                continue;
            edges[asset].Add(dependency);
            indegrees[dependency]++;
        }
        var pending = new HashSet<AssetEntry>(releaseAssets);
        var orderedAssets = new List<AssetEntry>(releaseAssets.Length);
        while (pending.Count != 0)
        {
            var next = pending
                .Where(asset => indegrees[asset] == 0)
                .OrderBy(asset => ReleasePriority(asset.Descriptor))
                .ThenByDescending(asset => ordinals[asset])
                .FirstOrDefault();
            if (next == null)
            {
                // A ready runtime graph cannot contain a dependency cycle, but
                // retain deterministic teardown if a stale/corrupt session did.
                next = pending
                    .OrderBy(asset => ReleasePriority(asset.Descriptor))
                    .ThenByDescending(asset => ordinals[asset])
                    .First();
            }
            pending.Remove(next);
            orderedAssets.Add(next);
            foreach (var dependency in edges[next])
                indegrees[dependency]--;
        }
        var released = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var releases = projectedAssets
            .Concat(orderedAssets.Select(asset => asset.Proxy!))
            .Where(released.Add)
            .Where(asset =>
                ReleaseLeases.TryGetValue(asset, out var lease) &&
                lease.SessionGeneration == sessionGeneration &&
                lease.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return new PcCompatVirtualAssetReleaseBatch(
            modId,
            sessionGeneration,
            releases);
    }

    private static void ResetMaterializedAssetsLocked(BundleEntry bundle)
    {
        foreach (var asset in bundle.OrderedAssets)
        {
            if (asset.Proxy != null)
            {
                asset.Proxy = null;
                asset.Status = PcCompatVirtualAssetResolveStatus.Pending;
                asset.Error = null;
                asset.ReleaseWithSession = false;
            }
            asset.Projections.Clear();
        }
    }

    private static void RemoveBundleClaimsLocked(BundleEntry bundle)
    {
        foreach (var asset in AssetUses.Keys.ToArray())
        {
            var uses = AssetUses[asset];
            var removed = uses.Claims
                .Where(claim => claim.BundleKey.Equals(bundle.Key, StringComparison.Ordinal))
                .ToArray();
            if (removed.Length == 0)
                continue;

            uses.Claims.RemoveAll(claim =>
                claim.BundleKey.Equals(bundle.Key, StringComparison.Ordinal));
            if (ReleaseLeases.TryGetValue(asset, out var lease))
            {
                foreach (var claim in removed.Where(claim => claim.ReleaseWithSession))
                    lease.Claims.Remove(claim.Claim);
                if (lease.Claims.Count == 0)
                    ReleaseLeases.Remove(asset);
            }
            if (uses.Claims.Count == 0)
                AssetUses.Remove(asset);
        }
    }

    private static int ReleasePriority(PcCompatResourceIrAsset asset)
    {
        var type = NormalizeType(asset.ExpectedType);
        if (type == "UnityEngine.Sprite")
            return 0;
        if (type is "UnityEngine.Texture" or "UnityEngine.Texture2D")
            return 2;
        return 1;
    }

    private static long NextTokenLocked()
    {
        do
        {
            s_nextToken++;
            if (s_nextToken <= 0)
                s_nextToken = 1;
        } while (Handles.ContainsKey(s_nextToken));
        return s_nextToken;
    }

    private static string MakeSessionKey(string modId, long generation)
        => modId + "\0" + generation;

    private static string MakeBundleKey(string modId, long generation, string bundleId)
        => MakeSessionKey(modId, generation) + "\0" + bundleId;
}

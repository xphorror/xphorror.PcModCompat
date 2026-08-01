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
    int OpenHandleCount);

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
    }

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
    }

    private sealed class HandleEntry
    {
        public required PcCompatVirtualBundleHandle Handle { get; init; }
        public required BundleEntry Bundle { get; init; }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, SessionEntry> Sessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<long, HandleEntry> Handles = new();
    private static Func<PcCompatVirtualAssetResolveRequest, PcCompatVirtualAssetResolveResult>? s_resolver;
    private static Func<PcCompatVirtualAssetProjectionRequest, PcCompatVirtualAssetResolveResult>?
        s_projectionResolver;
    private static Func<object, bool>? s_assetLivenessProbe;
    private static Func<string, IReadOnlyList<object>, object>? s_arrayFactory;
    private static Action<IReadOnlyList<object>>? s_releaseSink;
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

    public static void RegisterAssetReleaseSink(Action<IReadOnlyList<object>>? releaseSink)
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

        BundleEntry[] bundles;
        lock (Gate)
        {
            if (!Sessions.TryGetValue(
                    MakeSessionKey(modId, sessionGeneration),
                    out var session))
            {
                throw new InvalidOperationException(
                    $"VirtualBundle session is unavailable mod={modId} generation={sessionGeneration}.");
            }
            bundles = session.BundlesById.Values
                .Where(bundle => bundle.Descriptor.SelectedForRuntime)
                .OrderBy(bundle => bundle.Descriptor.SourceRelativePath, StringComparer.Ordinal)
                .ToArray();
            if (bundles.Length == 0)
            {
                bundles = session.BundlesById.Values
                    .OrderBy(bundle => bundle.Descriptor.SourceRelativePath, StringComparer.Ordinal)
                    .ToArray();
            }
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
                    RequireLiveAsset(bundle, asset, ResolveAsset(bundle, asset));
                }
                catch (PcCompatVirtualAssetPendingException exception)
                {
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
        var session = new SessionEntry
        {
            Key = MakeSessionKey(modId, sessionGeneration),
            ModId = modId,
            Generation = sessionGeneration,
            ModFolder = normalizedModFolder,
            BundlesById = bundles,
            BundlesByRelativePath = relativePaths,
            BundlesByUniqueFileName = uniqueFiles,
            PreferredBundlesByFileName = preferredFiles
        };

        RemoveMod(modId);
        lock (Gate)
            Sessions.Add(session.Key, session);
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
            var matches = string.IsNullOrWhiteSpace(expectedType)
                ? named.ToArray()
                : named.Where(item => TypeMatches(item.Descriptor.ExpectedType, expectedType!)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(
                    $"VirtualBundle asset lookup is ambiguous or type-incompatible name={assetName} " +
                    $"type={expectedType ?? "<any>"} matches={matches.Length}.");
            asset = matches[0];
        }
        return RequireLiveAsset(bundle, asset, ResolveAsset(bundle, asset));
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

            var runtimeBundles = session.BundlesById.Values
                .Where(candidate => candidate.Descriptor.SelectedForRuntime)
                .ToArray();
            if (runtimeBundles.Length == 0)
                runtimeBundles = session.BundlesById.Values.ToArray();
            var matches = runtimeBundles
                .SelectMany(candidate => candidate.OrderedAssets.Select(value =>
                    (Bundle: candidate, Asset: value)))
                .Where(value => value.Asset.Descriptor.Name.Equals(assetName, StringComparison.Ordinal))
                .Where(value => TypeMatches(value.Asset.Descriptor.ExpectedType, expectedType))
                .Where(value => value.Asset.Descriptor.MaterializationKind is not
                    PcCompatResourceIrMaterializationKind.MetadataOnly and not
                    PcCompatResourceIrMaterializationKind.Unsupported)
                .ToArray();
            if (matches.Length != 1)
            {
                return new PcCompatVirtualAssetResolveResult(
                    matches.Length == 0
                        ? PcCompatVirtualAssetResolveStatus.Unsupported
                        : PcCompatVirtualAssetResolveStatus.Failed,
                    null,
                    matches.Length == 0
                        ? $"VirtualBundle has no asset name={assetName} type={expectedType}."
                        : $"VirtualBundle asset selection is ambiguous name={assetName} " +
                          $"type={expectedType} matches={matches.Length}.");
            }
            (bundle, asset) = matches[0];
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
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (Gate)
        {
            if (!Handles.TryGetValue(handle.Token, out var entry) || !ReferenceEquals(entry.Handle, handle))
                throw new InvalidOperationException("VirtualBundle handle is stale or does not belong to this registry.");
            Handles.Remove(handle.Token);
        }
    }

    public static void RemoveMod(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        IReadOnlyList<object> releases;
        lock (Gate)
            releases = RemoveModLocked(modId);
        if (releases.Count == 0)
            return;
        var sink = Volatile.Read(ref s_releaseSink);
        if (sink == null)
            return;
        sink(releases);
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
                Handles.Count);
        }
    }

    private static BundleEntry RequireOwnedBundle(PcCompatVirtualBundleHandle handle)
    {
        lock (Gate)
        {
            if (!Handles.TryGetValue(handle.Token, out var entry) || !ReferenceEquals(entry.Handle, handle))
                throw new InvalidOperationException("VirtualBundle handle is stale or released.");
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

        lock (Gate)
        {
            asset.Resolving = false;
            if (!Sessions.ContainsKey(MakeSessionKey(bundle.ModId, bundle.SessionGeneration)))
                throw new InvalidOperationException("VirtualBundle session was retired during asset resolution.");
            if (result.Status == PcCompatVirtualAssetResolveStatus.Ready && result.Asset == null)
                result = result with { Status = PcCompatVirtualAssetResolveStatus.Failed, Error = "resolver returned Ready with null" };
            asset.Status = result.Status;
            asset.Proxy = result.Status == PcCompatVirtualAssetResolveStatus.Ready ? result.Asset : null;
            asset.ReleaseWithSession = result.Status == PcCompatVirtualAssetResolveStatus.Ready &&
                                       result.ReleaseWithSession;
            asset.Error = result.Error;
            if (asset.Status == PcCompatVirtualAssetResolveStatus.Ready)
                return asset.Proxy!;
            if (asset.Status == PcCompatVirtualAssetResolveStatus.Pending)
            {
                throw new PcCompatVirtualAssetPendingException(
                    result.Error ?? $"VirtualBundle asset is pending: {asset.Descriptor.Id}");
            }
            throw new InvalidOperationException(
                result.Error ?? $"VirtualBundle asset is {result.Status}: {asset.Descriptor.Id}");
        }
    }

    private static object RequireLiveAsset(BundleEntry bundle, AssetEntry asset, object proxy)
    {
        var probe = Volatile.Read(ref s_assetLivenessProbe);
        if (probe == null)
            return proxy;

        string error;
        var destroyed = false;
        try
        {
            if (probe(proxy))
                return proxy;
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
        throw new InvalidOperationException(message);
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

        lock (Gate)
        {
            projection.Resolving = false;
            if (!Sessions.ContainsKey(MakeSessionKey(bundle.ModId, bundle.SessionGeneration)))
                throw new InvalidOperationException("VirtualBundle session was retired during asset projection.");
            if (result.Status == PcCompatVirtualAssetResolveStatus.Ready && result.Asset == null)
                result = result with { Status = PcCompatVirtualAssetResolveStatus.Failed, Error = "projection returned Ready with null" };
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
        var runtimeBundles = session.BundlesById.Values
            .Where(bundle => bundle.Descriptor.SelectedForRuntime)
            .ToArray();
        if (runtimeBundles.Length == 0)
            runtimeBundles = session.BundlesById.Values.ToArray();
        var candidates = runtimeBundles
            .SelectMany(bundle => bundle.OrderedAssets.Select(asset => (Bundle: bundle, Asset: asset)))
            .Where(value => TypeMatches(value.Asset.Descriptor.ExpectedType, expectedType))
            .Where(value => value.Asset.Descriptor.MaterializationKind is not
                PcCompatResourceIrMaterializationKind.MetadataOnly and not
                PcCompatResourceIrMaterializationKind.Unsupported)
            .ToArray();
        var required = candidates.Where(value => value.Asset.Descriptor.RequiredByMod).ToArray();
        if (required.Length != 0)
            candidates = required;
        return candidates.Length switch
        {
            0 => (null, null, null),
            1 => (candidates[0].Bundle, candidates[0].Asset, null),
            _ => (null, null,
                $"VirtualBundle asset selection is ambiguous type={expectedType} matches={candidates.Length}.")
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

    private static IReadOnlyList<object> RemoveModLocked(string modId)
    {
        var projectedAssets = Sessions.Values
            .Where(session => session.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(session => session.BundlesById.Values)
            .SelectMany(bundle => bundle.OrderedAssets)
            .SelectMany(asset => asset.Projections.Values)
            .Where(projection => projection.ReleaseWithSession && projection.Proxy != null)
            .Select(projection => projection.Proxy!)
            .ToArray();
        var releaseAssets = Sessions.Values
            .Where(session => session.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(session => session.BundlesById.Values)
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
            .ToArray();
        var retired = Sessions.Values
            .Where(session => session.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase))
            .Select(session => session.Key)
            .ToArray();
        foreach (var key in retired)
            Sessions.Remove(key);
        foreach (var token in Handles
                     .Where(pair => pair.Value.Handle.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Key)
                     .ToArray())
            Handles.Remove(token);
        return releases;
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

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using StArray.ModManager.Manager;
using Xphorror.PcModCompat;
using Xphorror.PcModCompat.Resources;

namespace StArray.ModManager.Android.PcCompat;

public enum PcCompatResourceAssetStatus
{
    Unavailable = 0,
    BundlePending = 1,
    Queued = 2,
    Loading = 3,
    Ready = 4,
    Failed = 5
}

/// <summary>
/// UnityMain AssetBundle and asset cache for verified resource recipes.
/// Unity 6 exposes only asynchronous bundle/asset loading, so every request is
/// rooted and advanced one step per proven UnityMain presentation opportunity.
/// </summary>
public static unsafe class PcCompatResourceBundleLoader
{
    private const string NativeLibrary = "starray_modmanager";
    private const string LogTag = "PcCompatResourceBundle";
    private const int WorkQueueCapacity = 64;
    private const int MaxWorkItemsPerUnityMainPass = 1;
    private const int ContinuationQueueCapacity = 2048;
    private const int MaxContinuationsPerUnityMainPass = 16;
    private static readonly object Gate = new();
    private static readonly object UnityApiGate = new();
    private static readonly Dictionary<string, LoadedBundle> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PendingBundleLoad> PendingBundles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<object, PublishedResourceChangerSprite> PublishedResourceChangerSprites =
        new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<string> PublishedResourceChangerSpriteKeys =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PendingResourceChangerSprites =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly PcCompatUnityMainWorkQueue WorkQueue = new(
        WorkQueueCapacity,
        RequestUnityMainWork);
    private static readonly PcCompatUnityMainWorkQueue ContinuationQueue = new(
        ContinuationQueueCapacity,
        RequestUnityMainWork);
    private static bool s_installed;
    private static bool s_preferPoll;
    private static long s_nextSequence;
    private static long s_pollCursor;
    private static int s_resourceRefreshQueued;
    private static int s_resourceChangerStateApplyQueued;
    private static long s_resourceChangerSpriteRequested;
    private static long s_resourceChangerSpriteResolved;
    private static long s_resourceChangerSpritePublished;
    private static long s_resourceChangerSpriteRetired;
    private static long s_resourceChangerSpriteFailure;
    private static string s_resourceChangerSpriteLastError = "none";
    private static PcCompatGeneratedUnityBundleApi? s_api;
    private static PcCompatGeneratedUnityResourceApi? s_resourceApi;

    private readonly record struct PublishedResourceChangerSprite(
        string ModId,
        long SessionGeneration);

    private sealed class PendingBundleLoad
    {
        public required string Key { get; init; }
        public required PcCompatResourceLoadRequest Request { get; set; }
        public nint RequestObject { get; set; }
        public object? RequestProxy { get; set; }
        public long Sequence { get; set; }
    }

    private sealed class LoadedBundle
    {
        public required string Key { get; init; }
        public required string ModId { get; init; }
        public required string Sha256Hex { get; init; }
        public required string Path { get; set; }
        public required nint Bundle { get; init; }
        public required object BundleProxy { get; init; }
        public required long SessionGeneration { get; set; }
        public bool UnloadRequested { get; set; }
        public bool UnloadInProgress { get; set; }
        public object? ManagedProxy { get; set; }
        public bool ManagedReleaseObserved { get; set; }
        public Dictionary<string, LoadedAsset> Assets { get; } = new(StringComparer.Ordinal);
    }

    private sealed class LoadedAsset
    {
        public required string Key { get; init; }
        public required PcCompatResolvedResourceBinding Binding { get; set; }
        public PcCompatResourceAssetStatus Status { get; set; } = PcCompatResourceAssetStatus.Queued;
        public nint RequestObject { get; set; }
        public object? RequestProxy { get; set; }
        public nint Asset { get; set; }
        public object? AssetProxy { get; set; }
        public string? Error { get; set; }
        public long Sequence { get; set; }
    }

    private readonly record struct PollTarget(
        long Sequence,
        PendingBundleLoad? BundleLoad,
        LoadedBundle? Bundle,
        LoadedAsset? Asset);

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_set_unity_main_work_callback")]
    private static extern void SetUnityMainWorkCallback(nint callback);

    [DllImport(
        NativeLibrary,
        EntryPoint = "modmanager_pccompat_request_presentation_sink_install")]
    private static extern void RequestPresentationSinkInstallNative();

    [DllImport(
        NativeLibrary,
        EntryPoint = "modmanager_pccompat_is_presentation_sink_installed")]
    private static extern int IsPresentationSinkInstalledNative();

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_request_unity_main_work")]
    private static extern int RequestUnityMainWorkNative();

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_set_ui_resource_resolver")]
    private static extern void SetUiResourceResolverNative(nint callback);

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_refresh_ui_resources")]
    private static extern void RefreshUiResourcesNative();

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_clear_ui_resources_for_mod")]
    private static extern void ClearUiResourcesForModNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modId);

    public static void Install()
    {
        lock (Gate)
        {
            if (s_installed)
                return;
            s_installed = true;
        }

        try
        {
            var callback = (nint)(delegate* unmanaged[Cdecl]<void>)&OnUnityMainWork;
            var resourceResolver = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint*, int>)&OnResolveUiResource;
            SetUnityMainWorkCallback(callback);
            SetUiResourceResolverNative(resourceResolver);
            PcCompatResourceRecipeRuntime.RegisterBundleLoadSink(ScheduleLoad, ScheduleUnload);
            PcCompatResourceRecipeRuntime.RegisterResourceConsumerRefreshSink(ScheduleResourceConsumerRefresh);
            PcCompatVirtualBundleRegistry.RegisterAssetResolver(ResolveVirtualAsset);
            PcCompatVirtualBundleRegistry.RegisterAssetProjectionResolver(ResolveVirtualAssetProjection);
            PcCompatVirtualBundleRegistry.RegisterAssetLivenessProbe(IsVirtualAssetAlive);
            PcCompatVirtualBundleRegistry.RegisterArrayFactory(CreateVirtualAssetArray);
            PcCompatVirtualBundleRegistry.RegisterAssetReleaseSink(ReleaseVirtualAssets);
            PcCompatRuntime.RegisterManagedInstallContextProbe(
                () => PcCompatUnityMainExecutionContext.IsActive);
            PcCompatRuntime.RegisterUnityMainThreadProbe(
                PcCompatManagedSelfRenderBridge.IsCurrentUnityMainThread);
            PcCompatRuntime.RegisterUnityMainWorkScheduler(TryScheduleUnityMainWork);
            PcCompatUnityMainExecutionContext.RegisterContinuationScheduler(
                TryScheduleUnityMainContinuation);
            Logger.Info(
                LogTag,
                "Unity 6 async queues registered (resourceCapacity=" + WorkQueueCapacity +
                "; continuationCapacity=" + ContinuationQueueCapacity + "; " +
                PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable +
                "=" +
                (PcCompatResourceRecipeRuntime.IsRuntimeLoadEnabled() ? "1" : "0") +
                ")");
        }
        catch (Exception ex)
        {
            lock (Gate)
                s_installed = false;
            try { SetUiResourceResolverNative(nint.Zero); } catch { }
            PcCompatResourceRecipeRuntime.ClearBundleLoadSink();
            PcCompatResourceRecipeRuntime.ClearResourceConsumerRefreshSink();
            PcCompatVirtualBundleRegistry.RegisterAssetResolver(null);
            PcCompatVirtualBundleRegistry.RegisterAssetProjectionResolver(null);
            PcCompatVirtualBundleRegistry.RegisterAssetLivenessProbe(null);
            PcCompatVirtualBundleRegistry.RegisterArrayFactory(null);
            PcCompatVirtualBundleRegistry.RegisterAssetReleaseSink(null);
            PcCompatRuntime.RegisterManagedInstallContextProbe(null);
            PcCompatRuntime.RegisterUnityMainThreadProbe(null);
            PcCompatRuntime.RegisterUnityMainWorkScheduler(null);
            PcCompatUnityMainExecutionContext.RegisterContinuationScheduler(null);
            Logger.Error(LogTag, "AssetBundle UnityMain queue registration failed closed: " + ex);
        }
    }

    public static void ScheduleResourceChangerSprite(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        Interlocked.Increment(ref s_resourceChangerSpriteRequested);
        if (!PcCompatResourceRecipeRuntime.TryGetSessionGeneration(modId, out var generation) ||
            generation <= 0 ||
            !PcCompatVirtualBundleRegistry.HasSession(modId, generation))
        {
            SetResourceChangerSpriteLastError(
                $"deferred: VirtualBundle session unavailable mod={modId} generation={generation}");
            return;
        }

        var key = modId + "\n" + generation;
        lock (Gate)
        {
            if (PublishedResourceChangerSpriteKeys.Contains(key))
            {
                SetResourceChangerSpriteLastError("none");
                return;
            }
            if (!PendingResourceChangerSprites.Add(key))
                return;
        }

        try
        {
            if (!PresentationSinkReadyOrRequested() ||
                !WorkQueue.TryEnqueue(() => PublishResourceChangerSpriteOnUnityMain(
                    modId,
                    generation,
                    key)))
            {
                lock (Gate)
                    PendingResourceChangerSprites.Remove(key);
                var error = $"cannot queue ResourceChanger Auto sprite mod={modId} generation={generation}";
                RecordResourceChangerSpriteFailure(error);
                Logger.Warn(LogTag, error);
            }
        }
        catch (Exception exception)
        {
            lock (Gate)
                PendingResourceChangerSprites.Remove(key);
            RecordResourceChangerSpriteFailure(exception.Message);
            throw;
        }
    }

    public static void ScheduleResourceChangerStateApply()
    {
        if (Interlocked.Exchange(ref s_resourceChangerStateApplyQueued, 1) != 0)
            return;
        try
        {
            if (!PresentationSinkReadyOrRequested() ||
                !WorkQueue.TryEnqueue(ApplyPendingResourceChangerStateOnUnityMain))
            {
                Volatile.Write(ref s_resourceChangerStateApplyQueued, 0);
                Logger.Warn(LogTag, "cannot queue pending ResourceChanger state apply");
            }
        }
        catch
        {
            Volatile.Write(ref s_resourceChangerStateApplyQueued, 0);
            throw;
        }
    }

    private static void ApplyPendingResourceChangerStateOnUnityMain()
    {
        Volatile.Write(ref s_resourceChangerStateApplyQueued, 0);
        var result = PcCompatNativeHookRules.ApplyPendingResourceChangerState();
        if (result < 0)
            Logger.Warn(LogTag, $"pending ResourceChanger state apply failed code={result}");
    }

    private static void PublishResourceChangerSpriteOnUnityMain(
        string modId,
        long generation,
        string key)
    {
        try
        {
            var result = PcCompatVirtualBundleRegistry.ResolveNamedAsset(
                modId,
                generation,
                "Auto",
                "UnityEngine.Sprite");
            if (result.Status != PcCompatVirtualAssetResolveStatus.Ready || result.Asset == null)
            {
                var error =
                    $"ResourceChanger Auto VirtualBundle resolve failed mod={modId} " +
                    $"generation={generation}: {result.Error ?? result.Status.ToString()}";
                RecordResourceChangerSpriteFailure(error);
                Logger.Warn(LogTag, error);
                return;
            }
            Interlocked.Increment(ref s_resourceChangerSpriteResolved);

            lock (Gate)
            {
                if (PublishedResourceChangerSpriteKeys.Contains(key))
                {
                    SetResourceChangerSpriteLastError("none");
                    return;
                }
            }

            nint pointer;
            lock (UnityApiGate)
            {
                pointer = PcCompatGeneratedUnityResourceApi.GetNativePointer(
                    result.Asset,
                    "ResourceChanger Auto Sprite");
                if (PcCompatNativeHookRules.PublishResourceChangerSprite(
                        modId,
                        generation,
                        pointer) <= 0)
                {
                    throw new InvalidOperationException(
                        "native ResourceChanger rejected the VirtualBundle Auto Sprite");
                }
            }
            lock (Gate)
            {
                if (PublishedResourceChangerSprites.TryGetValue(result.Asset, out var previous))
                {
                    PublishedResourceChangerSpriteKeys.Remove(
                        ResourceChangerSpriteKey(previous.ModId, previous.SessionGeneration));
                }
                PublishedResourceChangerSprites[result.Asset] =
                    new PublishedResourceChangerSprite(modId, generation);
                PublishedResourceChangerSpriteKeys.Add(key);
            }
            Interlocked.Increment(ref s_resourceChangerSpritePublished);
            SetResourceChangerSpriteLastError("none");
            Logger.Info(
                LogTag,
                $"ResourceChanger Auto Sprite published from VirtualBundle mod={modId} " +
                $"generation={generation}");
        }
        catch (Exception exception)
        {
            RecordResourceChangerSpriteFailure(exception.Message);
            Logger.Warn(LogTag, $"ResourceChanger Auto publish failed mod={modId}: {exception.Message}");
        }
        finally
        {
            lock (Gate)
                PendingResourceChangerSprites.Remove(key);
        }
    }

    private static PcCompatVirtualAssetResolveResult ResolveVirtualAsset(
        PcCompatVirtualAssetResolveRequest request)
    {
        if (!PcCompatUnityMainExecutionContext.IsActive)
        {
            return new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Failed,
                null,
                "VirtualBundle Unity object materialization was requested outside UnityMain.");
        }
        try
        {
            switch (request.Asset.MaterializationKind)
            {
                case PcCompatResourceIrMaterializationKind.CapabilityReference:
                    return ResolveCapabilityAsset(request);
                case PcCompatResourceIrMaterializationKind.TextureRgba32:
                case PcCompatResourceIrMaterializationKind.TextureAlpha8:
                {
                    var expectedPayloadKind = request.Asset.MaterializationKind ==
                                              PcCompatResourceIrMaterializationKind.TextureAlpha8
                        ? "alpha8"
                        : "rgba32";
                    if (request.Payload?.Kind != expectedPayloadKind)
                        throw new InvalidDataException("Texture payload kind is invalid.");
                    var bytes = ReadVerifiedPayload(request);
                    object texture;
                    lock (UnityApiGate)
                        texture = EnsureResourceApi().CreateTexture(request.Asset, bytes);
                    return new PcCompatVirtualAssetResolveResult(
                        PcCompatVirtualAssetResolveStatus.Ready,
                        texture,
                        ReleaseWithSession: true);
                }
                case PcCompatResourceIrMaterializationKind.SpriteFromTexture:
                {
                    var spriteInfo = request.Asset.Sprite
                                     ?? throw new InvalidDataException("Sprite IR metadata is missing.");
                    var texture = PcCompatVirtualBundleRegistry.ResolveAssetById(
                        request.ModId,
                        request.SessionGeneration,
                        request.BundleId,
                        spriteInfo.TextureAssetId);
                    object sprite;
                    lock (UnityApiGate)
                        sprite = EnsureResourceApi().CreateSprite(request.Asset, texture);
                    return new PcCompatVirtualAssetResolveResult(
                        PcCompatVirtualAssetResolveStatus.Ready,
                        sprite,
                        ReleaseWithSession: true);
                }
                case PcCompatResourceIrMaterializationKind.MaterialFromCapabilityShader:
                {
                    var capability = ResolveCapabilityAsset(request, clone: false);
                    if (capability.Status != PcCompatVirtualAssetResolveStatus.Ready)
                        return capability;
                    var materialInfo = request.Asset.Material
                                       ?? throw new InvalidDataException("Material IR metadata is missing.");
                    var textures = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var textureId in materialInfo.Textures
                                 .Select(value => value.TextureAssetId)
                                 .Where(value => value.Length != 0)
                                 .Distinct(StringComparer.Ordinal))
                    {
                        textures.Add(
                            textureId,
                            PcCompatVirtualBundleRegistry.ResolveAssetById(
                                request.ModId,
                                request.SessionGeneration,
                                request.BundleId,
                                textureId));
                    }
                    object material;
                    lock (UnityApiGate)
                    {
                        material = EnsureResourceApi().CreateMaterial(
                            request.Asset,
                            capability.Asset!,
                            textures);
                    }
                    return new PcCompatVirtualAssetResolveResult(
                        PcCompatVirtualAssetResolveStatus.Ready,
                        material,
                        ReleaseWithSession: true);
                }
                case PcCompatResourceIrMaterializationKind.TmpFontFromAtlas:
                {
                    if (request.Payload?.Kind != ResourceIrTmpFontPayloadBinary.PayloadKind)
                        throw new InvalidDataException("TMP font payload kind is invalid.");
                    var fontInfo = request.Asset.TmpFont
                                   ?? throw new InvalidDataException("TMP font IR metadata is missing.");
                    var material = PcCompatVirtualBundleRegistry.ResolveAssetById(
                        request.ModId,
                        request.SessionGeneration,
                        request.BundleId,
                        fontInfo.MaterialAssetId);
                    var atlases = fontInfo.AtlasTextureAssetIds.Select(textureId =>
                            PcCompatVirtualBundleRegistry.ResolveAssetById(
                                request.ModId,
                                request.SessionGeneration,
                                request.BundleId,
                                textureId))
                        .ToArray();
                    var payload = ResourceIrTmpFontPayloadBinary.Read(ReadVerifiedPayload(request));
                    var shell = ResolveCapabilityAsset(request, clone: true);
                    if (shell.Status != PcCompatVirtualAssetResolveStatus.Ready)
                        return shell;
                    object font;
                    lock (UnityApiGate)
                    {
                        var api = EnsureResourceApi();
                        try
                        {
                            font = api.CreateTmpFont(
                                request.Asset,
                                shell.Asset!,
                                material,
                                atlases,
                                payload);
                        }
                        catch
                        {
                            api.Destroy(shell.Asset!);
                            throw;
                        }
                    }
                    return new PcCompatVirtualAssetResolveResult(
                        PcCompatVirtualAssetResolveStatus.Ready,
                        font,
                        ReleaseWithSession: true);
                }
                case PcCompatResourceIrMaterializationKind.PrefabGraph:
                {
                    var dependencies = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var dependencyId in request.Asset.DependencyIds)
                    {
                        dependencies.Add(
                            dependencyId,
                            PcCompatVirtualBundleRegistry.ResolveAssetById(
                                request.ModId,
                                request.SessionGeneration,
                                request.BundleId,
                                dependencyId));
                    }
                    object prefab;
                    lock (UnityApiGate)
                        prefab = EnsureResourceApi().CreatePrefab(request.Asset, dependencies);
                    return new PcCompatVirtualAssetResolveResult(
                        PcCompatVirtualAssetResolveStatus.Ready,
                        prefab,
                        ReleaseWithSession: true);
                }
                default:
                    return new PcCompatVirtualAssetResolveResult(
                        PcCompatVirtualAssetResolveStatus.Unsupported,
                        null,
                        $"materializer is not implemented: {request.Asset.MaterializationKind}");
            }
        }
        catch (PcCompatVirtualAssetPendingException exception)
        {
            return new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Pending,
                null,
                exception.Message);
        }
        catch (Exception ex)
        {
            return new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Failed,
                null,
                PcCompatVirtualAssetDiagnostics.FormatResolverFailure(request, ex));
        }
    }

    private static PcCompatVirtualAssetResolveResult ResolveVirtualAssetProjection(
        PcCompatVirtualAssetProjectionRequest request)
    {
        if (!PcCompatUnityMainExecutionContext.IsActive)
        {
            return new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Failed,
                null,
                "VirtualBundle Unity object projection was requested outside UnityMain.");
        }
        if (!request.ExpectedType.Equals("UnityEngine.Font", StringComparison.Ordinal) ||
            !request.SourceAsset.ExpectedType.Equals("TMPro.TMP_FontAsset", StringComparison.Ordinal) ||
            request.SourceAsset.MaterializationKind != PcCompatResourceIrMaterializationKind.TmpFontFromAtlas)
        {
            return new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Unsupported,
                null,
                $"unsupported VirtualBundle projection {request.SourceAsset.ExpectedType} -> {request.ExpectedType}");
        }

        try
        {
            if (request.Payload?.Kind != ResourceIrTmpFontPayloadBinary.PayloadKind)
                throw new InvalidDataException("TMP font projection payload kind is invalid.");
            var fontInfo = request.SourceAsset.TmpFont
                           ?? throw new InvalidDataException("TMP font projection metadata is missing.");
            var material = PcCompatVirtualBundleRegistry.ResolveAssetById(
                request.ModId,
                request.SessionGeneration,
                request.BundleId,
                fontInfo.MaterialAssetId);
            var atlases = fontInfo.AtlasTextureAssetIds.Select(textureId =>
                    PcCompatVirtualBundleRegistry.ResolveAssetById(
                        request.ModId,
                        request.SessionGeneration,
                        request.BundleId,
                        textureId))
                .ToArray();
            var payload = ResourceIrTmpFontPayloadBinary.Read(ReadVerifiedPayload(
                request.ResourceIrRoot,
                request.SourceAsset,
                request.Payload));
            object font;
            lock (UnityApiGate)
            {
                font = EnsureResourceApi().CreateImGuiFontFromTmpAtlas(
                    request.SourceAsset,
                    material,
                    atlases,
                    payload);
            }
            return new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Ready,
                font,
                ReleaseWithSession: true);
        }
        catch (PcCompatVirtualAssetPendingException exception)
        {
            return new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Pending,
                null,
                exception.Message);
        }
        catch (Exception exception)
        {
            return new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Failed,
                null,
                PcCompatVirtualAssetDiagnostics.FormatProjectionFailure(request, exception));
        }
    }

    private static PcCompatVirtualAssetResolveResult ResolveCapabilityAsset(
        PcCompatVirtualAssetResolveRequest request)
        => ResolveCapabilityAsset(request, request.Asset.CloneCapabilityAsset);

    private static PcCompatVirtualAssetResolveResult ResolveCapabilityAsset(
        PcCompatVirtualAssetResolveRequest request,
        bool clone)
    {
        if (PcCompatCapabilityBundleRegistry.TryGetAsset(
                request.Asset.CapabilityStableId,
                request.Asset.ExpectedType,
                out var proxy,
                out _))
        {
            if (!clone)
                return new PcCompatVirtualAssetResolveResult(PcCompatVirtualAssetResolveStatus.Ready, proxy);
            object clonedAsset;
            lock (UnityApiGate)
                clonedAsset = EnsureResourceApi().Clone(proxy!);
            return new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Ready,
                clonedAsset,
                ReleaseWithSession: true);
        }
        var snapshot = PcCompatCapabilityBundleRegistry.GetSnapshot();
        PcCompatCapabilityBundleRegistry.TryEnsureReady();
        return snapshot.Status == PcCompatCapabilityRegistryStatus.Failed
            ? new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Failed,
                null,
                snapshot.Error ?? "Android capability registry failed")
            : new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Pending,
                null,
                $"capability asset is pending id={request.Asset.CapabilityStableId}");
    }

    private static byte[] ReadVerifiedPayload(PcCompatVirtualAssetResolveRequest request)
        => ReadVerifiedPayload(request.ResourceIrRoot, request.Asset, request.Payload);

    private static byte[] ReadVerifiedPayload(
        string resourceIrRoot,
        PcCompatResourceIrAsset asset,
        PcCompatResourceIrPayload? payloadDescriptor)
    {
        var payload = payloadDescriptor
                      ?? throw new InvalidDataException($"Resource payload descriptor is missing: {asset.Id}");
        var root = Path.GetFullPath(resourceIrRoot);
        var path = Path.GetFullPath(Path.Combine(root, payload.RelativePath));
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative is "." or ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"Resource payload escapes IR root: {payload.Id}");
        var bytes = File.ReadAllBytes(path);
        if (bytes.LongLength != payload.Length)
            throw new InvalidDataException($"Resource payload length mismatch: {payload.Id}");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!sha.Equals(payload.Sha256Hex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Resource payload sha256 mismatch: {payload.Id}");
        return bytes;
    }

    private static void ReleaseVirtualAssets(IReadOnlyList<object> assets)
    {
        if (assets.Count == 0)
            return;
        lock (UnityApiGate)
        {
            var api = EnsureResourceApi();
            foreach (var asset in assets)
            {
                try
                {
                    PublishedResourceChangerSprite published;
                    bool resourceChangerSprite;
                    lock (Gate)
                    {
                        resourceChangerSprite = PublishedResourceChangerSprites.Remove(asset, out published);
                        if (resourceChangerSprite)
                        {
                            PublishedResourceChangerSpriteKeys.Remove(
                                ResourceChangerSpriteKey(
                                    published.ModId,
                                    published.SessionGeneration));
                        }
                    }
                    if (resourceChangerSprite)
                    {
                        var retireResult = PcCompatNativeHookRules.RetireResourceChangerSprite(
                            published.ModId,
                            published.SessionGeneration);
                        if (retireResult < 0)
                        {
                            RecordResourceChangerSpriteFailure(
                                $"native ResourceChanger Sprite retire failed code={retireResult}");
                        }
                        Interlocked.Increment(ref s_resourceChangerSpriteRetired);
                    }
                    api.Destroy(asset);
                }
                catch (Exception ex)
                {
                    Logger.Warn(LogTag, "VirtualBundle Unity object release failed: " + ex.Message);
                }
            }
        }
    }

    private static object CreateVirtualAssetArray(
        string expectedElementType,
        IReadOnlyList<object> assets)
    {
        var elementType = ResolveProxyElementType(expectedElementType, assets)
                          ?? throw new InvalidOperationException(
                              $"VirtualBundle array element proxy is unavailable: {expectedElementType}");
        var array = Array.CreateInstance(elementType, assets.Count);
        for (var index = 0; index < assets.Count; index++)
        {
            if (!elementType.IsInstanceOfType(assets[index]))
            {
                throw new InvalidOperationException(
                    $"VirtualBundle asset type mismatch index={index} expected={elementType.FullName} " +
                    $"actual={assets[index].GetType().FullName}");
            }
            array.SetValue(assets[index], index);
        }
        return array;
    }

    private static bool IsVirtualAssetAlive(object asset)
    {
        lock (UnityApiGate)
            return EnsureResourceApi().IsAlive(asset);
    }

    private static Type? ResolveProxyElementType(
        string expectedElementType,
        IReadOnlyList<object> assets)
    {
        var normalized = expectedElementType switch
        {
            "Object" => "UnityEngine.Object",
            "GameObject" => "UnityEngine.GameObject",
            "Texture2D" => "UnityEngine.Texture2D",
            "Sprite" => "UnityEngine.Sprite",
            "Material" => "UnityEngine.Material",
            "TMP_FontAsset" => "TMPro.TMP_FontAsset",
            var identity => identity
        };
        foreach (var asset in assets)
        {
            for (var type = asset.GetType(); type != null; type = type.BaseType)
            {
                if (type.FullName == normalized)
                    return type;
            }
        }
        return AppDomain.CurrentDomain.GetAssemblies()
            .OrderByDescending(assembly =>
                assembly.GetName().Name?.StartsWith("UnityEngine.", StringComparison.Ordinal) == true ||
                assembly.GetName().Name == "Unity.TextMeshPro")
            .Select(assembly => assembly.GetType(normalized, throwOnError: false))
            .FirstOrDefault(type => type != null);
    }

    /// <summary>
    /// Schedules non-resource managed work on the same proven UnityMain hook.
    /// The caller retains ownership and retries when the queue is unavailable.
    /// </summary>
    public static bool TryScheduleUnityMainWork(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (Gate)
        {
            if (!s_installed)
                return false;
        }

        try
        {
            if (!PresentationSinkReadyOrRequested())
                return false;
            return WorkQueue.TryEnqueue(work);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryScheduleUnityMainContinuation(Action continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        lock (Gate)
        {
            if (!s_installed)
                return false;
        }

        try
        {
            if (!PresentationSinkReadyOrRequested())
                return false;
            return ContinuationQueue.TryEnqueue(continuation);
        }
        catch
        {
            return false;
        }
    }

    internal static string GetUnityMainQueueDiagnostics()
    {
        var resources = WorkQueue.Snapshot();
        var continuations = ContinuationQueue.Snapshot();
        int resourceChangerPending;
        int resourceChangerTracked;
        lock (Gate)
        {
            resourceChangerPending = PendingResourceChangerSprites.Count;
            resourceChangerTracked = PublishedResourceChangerSpriteKeys.Count;
        }
        var lastError = Volatile.Read(ref s_resourceChangerSpriteLastError);
        return
            $"resource[pending={resources.Pending} capacity={resources.Capacity} " +
            $"high={resources.HighWatermark} accepted={resources.Accepted} " +
            $"rejected={resources.Rejected} executed={resources.Executed} " +
            $"failed={resources.Failed}] " +
            $"continuation[pending={continuations.Pending} capacity={continuations.Capacity} " +
            $"high={continuations.HighWatermark} accepted={continuations.Accepted} " +
            $"rejected={continuations.Rejected} executed={continuations.Executed} " +
            $"failed={continuations.Failed}] " +
            $"resourceChangerSprite[pending={resourceChangerPending} tracked={resourceChangerTracked} " +
            $"requested={Volatile.Read(ref s_resourceChangerSpriteRequested)} " +
            $"resolved={Volatile.Read(ref s_resourceChangerSpriteResolved)} " +
            $"published={Volatile.Read(ref s_resourceChangerSpritePublished)} " +
            $"retired={Volatile.Read(ref s_resourceChangerSpriteRetired)} " +
            $"failure={Volatile.Read(ref s_resourceChangerSpriteFailure)} " +
            $"lastError={lastError}]";
    }

    private static string ResourceChangerSpriteKey(string modId, long generation)
        => modId + "\n" + generation;

    private static void RecordResourceChangerSpriteFailure(string error)
    {
        Interlocked.Increment(ref s_resourceChangerSpriteFailure);
        SetResourceChangerSpriteLastError(error);
    }

    private static void SetResourceChangerSpriteLastError(string error)
    {
        const int maxLength = 320;
        var bounded = string.IsNullOrWhiteSpace(error)
            ? "none"
            : error.Length <= maxLength
                ? error
                : error[..maxLength];
        Volatile.Write(ref s_resourceChangerSpriteLastError, bounded);
    }

    private static object AcquireManagedBundleProxy(PcCompatManagedAssetBundleRequest request)
    {
        LoadedBundle bundle;
        lock (Gate)
        {
            var candidates = Loaded.Values
                .Where(candidate =>
                    candidate.ModId.Equals(request.ModId, StringComparison.OrdinalIgnoreCase) &&
                    candidate.SessionGeneration == request.SessionGeneration &&
                    !candidate.UnloadRequested &&
                    !candidate.UnloadInProgress)
                .ToArray();
            bundle = SelectManagedBundle(candidates, request.RequestedPath)
                ?? throw new InvalidOperationException(
                    $"managed AssetBundle lookup is missing or ambiguous mod={request.ModId} " +
                    $"generation={request.SessionGeneration} path={request.RequestedPath} " +
                    $"loadedCandidates={candidates.Length}");
            if (bundle.ManagedProxy != null)
            {
                bundle.ManagedReleaseObserved = false;
                return bundle.ManagedProxy;
            }
        }

        lock (Gate)
        {
            if (!Loaded.TryGetValue(bundle.Key, out var current) ||
                !ReferenceEquals(current, bundle) ||
                bundle.SessionGeneration != request.SessionGeneration ||
                bundle.UnloadRequested ||
                bundle.UnloadInProgress)
            {
                throw new InvalidOperationException(
                    $"managed AssetBundle ownership changed during proxy creation mod={request.ModId}");
            }
            bundle.ManagedReleaseObserved = false;
            return bundle.ManagedProxy ??= bundle.BundleProxy;
        }
    }

    private static void ReleaseManagedBundleProxy(PcCompatManagedAssetBundleRelease request)
    {
        lock (Gate)
        {
            var bundle = Loaded.Values.FirstOrDefault(candidate =>
                candidate.ModId.Equals(request.ModId, StringComparison.OrdinalIgnoreCase) &&
                candidate.SessionGeneration == request.SessionGeneration &&
                ReferenceEquals(candidate.ManagedProxy, request.Bundle));
            if (bundle == null)
            {
                throw new InvalidOperationException(
                    $"managed AssetBundle release rejected for non-owner mod={request.ModId} " +
                    $"generation={request.SessionGeneration}");
            }

            // The MOD relinquishes its view here. The resource session remains
            // authoritative and performs the real Unity unload after Disable.
            bundle.ManagedReleaseObserved = true;
        }
    }

    private static LoadedBundle? SelectManagedBundle(
        IReadOnlyList<LoadedBundle> candidates,
        string requestedPath)
    {
        if (candidates.Count == 0)
            return null;

        var normalizedRequest = NormalizePathForComparison(requestedPath);
        if (normalizedRequest != null)
        {
            var exact = candidates.Where(candidate =>
                    string.Equals(
                        NormalizePathForComparison(candidate.Path),
                        normalizedRequest,
                        StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (exact.Length == 1)
                return exact[0];

            var requestedFile = Path.GetFileName(normalizedRequest);
            var byFile = candidates.Where(candidate =>
                    Path.GetFileName(candidate.Path).Equals(
                        requestedFile,
                        StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (byFile.Length == 1)
                return byFile[0];
        }

        // Platform path rewriting commonly changes Windows/Linux subfolders.
        // A unique owner-scoped loaded candidate is still unambiguous.
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static string? NormalizePathForComparison(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().Replace('\\', '/');
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnResolveUiResource(
        nint modIdUtf8,
        nint featureGroupUtf8,
        nint assetNameUtf8,
        nint expectedTypeUtf8,
        nint* assetOutput)
    {
        if (assetOutput == null)
            return 0;
        *assetOutput = nint.Zero;
        try
        {
            var modId = Marshal.PtrToStringUTF8(modIdUtf8) ?? string.Empty;
            var featureGroup = Marshal.PtrToStringUTF8(featureGroupUtf8) ?? string.Empty;
            var assetName = Marshal.PtrToStringUTF8(assetNameUtf8) ?? string.Empty;
            var expectedType = Marshal.PtrToStringUTF8(expectedTypeUtf8) ?? string.Empty;
            var status = TryGetOrRequestAsset(
                modId,
                featureGroup,
                assetName,
                expectedType,
                out var asset,
                out _);
            if (status == PcCompatResourceAssetStatus.Ready && asset != nint.Zero)
            {
                *assetOutput = asset;
                return 2;
            }
            return status is PcCompatResourceAssetStatus.BundlePending or
                PcCompatResourceAssetStatus.Queued or
                PcCompatResourceAssetStatus.Loading
                ? 1
                : 0;
        }
        catch (Exception ex)
        {
            Logger.Warn(LogTag, "UI resource resolver failed closed: " + ex.Message);
            return 0;
        }
    }

    private static PcCompatResourceLoadResult ScheduleLoad(PcCompatResourceLoadRequest request)
    {
        var key = MakeBundleKey(request.ModId, request.CandidateSha256Hex);
        lock (Gate)
        {
            if (Loaded.TryGetValue(key, out var loaded))
            {
                if (loaded.UnloadInProgress)
                    return Failure(request, "matching AssetBundle unload is in progress", retryable: true);
                PromoteLoadedBundleLocked(loaded, request);
                return LoadedCacheHit(request, loaded.Path);
            }
            if (PendingBundles.TryGetValue(key, out var pending))
            {
                pending.Request = request;
                return Pending(request, cacheHit: true);
            }
        }

        try
        {
            if (!PresentationSinkReadyOrRequested())
                return Failure(request, "UnityMain presentation hook is not ready", retryable: true);

            lock (Gate)
            {
                if (Loaded.TryGetValue(key, out var loaded))
                {
                    if (loaded.UnloadInProgress)
                        return Failure(request, "matching AssetBundle unload is in progress", retryable: true);
                    PromoteLoadedBundleLocked(loaded, request);
                    return LoadedCacheHit(request, loaded.Path);
                }
                if (PendingBundles.TryGetValue(key, out var pending))
                {
                    pending.Request = request;
                    return Pending(request, cacheHit: true);
                }

                var operation = new PendingBundleLoad
                {
                    Key = key,
                    Request = request
                };
                PendingBundles.Add(key, operation);
                try
                {
                    if (!WorkQueue.TryEnqueue(() => StartBundleLoadOnUnityMain(operation)))
                    {
                        PendingBundles.Remove(key);
                        return Failure(request, "UnityMain resource work queue is full", retryable: true);
                    }
                }
                catch
                {
                    PendingBundles.Remove(key);
                    throw;
                }
            }
            return Pending(request, cacheHit: false);
        }
        catch (Exception ex)
        {
            return Failure(request, ex.GetType().Name + ": " + ex.Message, retryable: true);
        }
    }

    private static void ScheduleUnload(PcCompatResourceUnloadRequest request)
    {
        try
        {
            if (!PresentationSinkReadyOrRequested())
            {
                Logger.Warn(
                    LogTag,
                    $"cannot queue unload before UnityMain hook is ready mod={request.ModId} " +
                    $"sha={request.CandidateSha256Hex} generation={request.SessionGeneration}");
                return;
            }
            if (!WorkQueue.TryEnqueue(() => UnloadOnUnityMain(request)))
            {
                Logger.Warn(
                    LogTag,
                    $"UnityMain resource queue full; unload retained mod={request.ModId} " +
                    $"sha={request.CandidateSha256Hex} generation={request.SessionGeneration}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(LogTag, $"queue unload failed mod={request.ModId}: {ex.Message}");
        }
    }

    private static void ScheduleResourceConsumerRefresh()
    {
        try
        {
            if (!PresentationSinkReadyOrRequested() ||
                Interlocked.Exchange(ref s_resourceRefreshQueued, 1) != 0)
                return;
            if (!WorkQueue.TryEnqueue(RefreshResourceConsumersQueuedOnUnityMain))
            {
                Volatile.Write(ref s_resourceRefreshQueued, 0);
                Logger.Warn(LogTag, "UnityMain resource queue full; consumer refresh was not queued");
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref s_resourceRefreshQueued, 0);
            Logger.Warn(LogTag, "queue resource consumer refresh failed: " + ex.Message);
        }
    }

    private static void RefreshResourceConsumersQueuedOnUnityMain()
    {
        // Clear before entering native/managed callbacks. A state change during
        // this pass can then enqueue one follow-up refresh without being lost.
        Volatile.Write(ref s_resourceRefreshQueued, 0);
        RefreshResourceConsumersOnUnityMain();
    }

    /// <summary>
    /// Returns a loaded Unity object or schedules its bundle/asset request. The
    /// returned pointer may only be consumed on UnityMain.
    /// </summary>
    public static PcCompatResourceAssetStatus TryGetOrRequestAsset(
        string modId,
        string featureGroupId,
        string expectedType,
        out nint asset)
        => TryGetOrRequestAssetCore(
            modId,
            featureGroupId,
            assetName: null,
            expectedType,
            out asset,
            out _);

    public static PcCompatResourceAssetStatus TryGetOrRequestAsset(
        string modId,
        string featureGroupId,
        string expectedType,
        out nint asset,
        out PcCompatResolvedResourceBinding binding)
        => TryGetOrRequestAssetCore(
            modId,
            featureGroupId,
            assetName: null,
            expectedType,
            out asset,
            out binding);

    public static PcCompatResourceAssetStatus TryGetOrRequestAsset(
        string modId,
        string featureGroupId,
        string assetName,
        string expectedType,
        out nint asset,
        out PcCompatResolvedResourceBinding binding)
        => TryGetOrRequestAssetCore(
            modId,
            featureGroupId,
            assetName,
            expectedType,
            out asset,
            out binding);

    private static PcCompatResourceAssetStatus TryGetOrRequestAssetCore(
        string modId,
        string featureGroupId,
        string? assetName,
        string expectedType,
        out nint asset,
        out PcCompatResolvedResourceBinding binding)
    {
        asset = nint.Zero;
        binding = null!;
        if (string.IsNullOrWhiteSpace(modId) ||
            string.IsNullOrWhiteSpace(featureGroupId) ||
            (assetName != null && string.IsNullOrWhiteSpace(assetName)) ||
            string.IsNullOrWhiteSpace(expectedType))
            return PcCompatResourceAssetStatus.Unavailable;
        var plan = PcCompatResourceRecipeRuntime.GetPlan(modId);
        if (plan == null || !plan.FeatureGroups.Any(group =>
                group.Id.Equals(featureGroupId, StringComparison.OrdinalIgnoreCase)))
            return PcCompatResourceAssetStatus.Unavailable;

        if (!PcCompatResourceRecipeRuntime.TryResolveLoadedBinding(
                modId,
                featureGroupId,
                assetName,
                expectedType,
                out binding))
        {
            var ensure = PcCompatResourceRecipeRuntime.TryEnsureFeatureGroupLoaded(modId, featureGroupId);
            if (!ensure.Success ||
                !PcCompatResourceRecipeRuntime.TryResolveLoadedBinding(
                    modId,
                    featureGroupId,
                    assetName,
                    expectedType,
                    out binding))
                return ensure.Pending
                    ? PcCompatResourceAssetStatus.BundlePending
                    : PcCompatResourceAssetStatus.Unavailable;
        }

        var bundleKey = MakeBundleKey(binding.ModId, binding.CandidateSha256Hex);
        var assetKey = MakeAssetKey(binding);
        LoadedBundle bundle;
        LoadedAsset entry;
        try
        {
            lock (Gate)
            {
                if (!Loaded.TryGetValue(bundleKey, out bundle!) ||
                bundle.SessionGeneration != binding.SessionGeneration ||
                    bundle.UnloadRequested ||
                    bundle.UnloadInProgress)
                    return PcCompatResourceAssetStatus.BundlePending;

                if (bundle.Assets.TryGetValue(assetKey, out entry!))
                {
                    entry.Binding = binding;
                    if (entry.Status == PcCompatResourceAssetStatus.Ready && entry.Asset != nint.Zero)
                        asset = entry.Asset;
                    return entry.Status;
                }

                entry = new LoadedAsset
                {
                    Key = assetKey,
                    Binding = binding
                };
                bundle.Assets.Add(assetKey, entry);
                try
                {
                    if (!WorkQueue.TryEnqueue(() => StartAssetLoadOnUnityMain(bundle, entry)))
                    {
                        bundle.Assets.Remove(assetKey);
                        return PcCompatResourceAssetStatus.Unavailable;
                    }
                }
                catch
                {
                    bundle.Assets.Remove(assetKey);
                    throw;
                }
            }
            return PcCompatResourceAssetStatus.Queued;
        }
        catch (Exception ex)
        {
            Logger.Warn(LogTag, $"asset queue failed mod={modId} group={featureGroupId}: {ex.Message}");
            return PcCompatResourceAssetStatus.Unavailable;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUnityMainWork()
    {
        using var unityMainScope = PcCompatUnityMainExecutionContext.Enter();
        try
        {
            if (ContinuationQueue.Count > 0)
                ContinuationQueue.Drain(MaxContinuationsPerUnityMainPass);

            var hasPollable = HasPollableOperation();
            if (hasPollable && (s_preferPoll || WorkQueue.Count == 0))
            {
                PollOneOperationOnUnityMain();
                s_preferPoll = false;
            }
            else if (WorkQueue.Count > 0)
            {
                WorkQueue.Drain(MaxWorkItemsPerUnityMainPass);
                s_preferPoll = true;
            }
            else if (hasPollable)
            {
                PollOneOperationOnUnityMain();
                s_preferPoll = false;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(LogTag, "UnityMain resource step failed closed: " + ex);
        }
        finally
        {
            if (ContinuationQueue.Count > 0 ||
                WorkQueue.Count > 0 ||
                HasPollableOperation())
            {
                try { RequestUnityMainWork(); } catch { }
            }
        }
    }

    private static void RequestUnityMainWork()
    {
        var result = RequestUnityMainWorkNative();
        if (result != 1)
            throw new InvalidOperationException("native UnityMain work request failed: " + result);
    }

    private static bool PresentationSinkReadyOrRequested()
    {
        if (IsPresentationSinkInstalledNative() != 0)
            return true;
        RequestPresentationSinkInstallNative();
        return false;
    }

    private static void StartBundleLoadOnUnityMain(PendingBundleLoad operation)
    {
        PcCompatResourceLoadRequest request;
        lock (Gate)
        {
            if (!PendingBundles.TryGetValue(operation.Key, out var current) || !ReferenceEquals(current, operation))
                return;
            request = operation.Request;
        }

        try
        {
            if (!PcCompatResourceRecipe.TryVerifyCandidateFile(
                    request.Path,
                    request.CandidateSha256Hex,
                    request.ExpectedFileSize,
                    out var verifyError))
            {
                CompleteBundleFailure(operation, verifyError ?? "candidate verification failed on UnityMain");
                return;
            }

            object? requestProxy;
            lock (UnityApiGate)
                requestProxy = EnsureApi().LoadFromFileAsync(request.Path);
            var requestObject = PcCompatGeneratedUnityBundleApi.GetPointer(requestProxy);
            if (requestObject == nint.Zero)
            {
                CompleteBundleFailure(operation, "AssetBundle.LoadFromFileAsync returned null");
                return;
            }

            var accepted = false;
            lock (Gate)
            {
                if (PendingBundles.TryGetValue(operation.Key, out var current) && ReferenceEquals(current, operation))
                {
                    operation.RequestObject = requestObject;
                    operation.RequestProxy = requestProxy;
                    operation.Sequence = NextSequenceLocked();
                    accepted = true;
                }
            }
            if (!accepted)
                requestProxy = null;
        }
        catch (Exception ex)
        {
            CompleteBundleFailure(operation, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void PollBundleLoadOnUnityMain(PendingBundleLoad operation)
    {
        object requestProxy;
        lock (Gate)
        {
            if (!PendingBundles.TryGetValue(operation.Key, out var current) ||
                !ReferenceEquals(current, operation) ||
                operation.RequestObject == nint.Zero ||
                operation.RequestProxy == null)
                return;
            requestProxy = operation.RequestProxy;
        }

        PcCompatGeneratedUnityBundleApi api;
        bool done;
        lock (UnityApiGate)
        {
            api = EnsureApi();
            done = api.IsDone(requestProxy);
        }
        if (!done)
            return;

        object? bundleProxy;
        lock (UnityApiGate)
            bundleProxy = api.GetAssetBundle(requestProxy);
        var bundleObject = PcCompatGeneratedUnityBundleApi.GetPointer(bundleProxy);
        ReleaseBundleRequestHandle(operation);
        if (bundleObject == nint.Zero)
        {
            CompleteBundleFailure(operation, "AssetBundleCreateRequest completed without an AssetBundle");
            return;
        }

        PcCompatResourceLoadRequest completionRequest = null!;
        var registered = false;
        lock (Gate)
        {
            if (PendingBundles.TryGetValue(operation.Key, out var current) && ReferenceEquals(current, operation))
            {
                completionRequest = operation.Request;
                PendingBundles.Remove(operation.Key);
                Loaded[operation.Key] = new LoadedBundle
                {
                    Key = operation.Key,
                    ModId = completionRequest.ModId,
                    Sha256Hex = completionRequest.CandidateSha256Hex,
                    Path = completionRequest.Path,
                    Bundle = bundleObject,
                    BundleProxy = bundleProxy!,
                    SessionGeneration = completionRequest.SessionGeneration
                };
                registered = true;
            }
        }
        if (!registered)
        {
            try
            {
                lock (UnityApiGate)
                    api.Unload(bundleProxy!, unloadAllLoadedObjects: true);
            }
            catch { }
            return;
        }

        var result = new PcCompatResourceLoadResult
        {
            Success = true,
            ModId = completionRequest.ModId,
            CandidateSha256Hex = completionRequest.CandidateSha256Hex,
            Path = completionRequest.Path,
            SessionGeneration = completionRequest.SessionGeneration
        };
        var accepted = CompleteBundleLoad(completionRequest, result);
        if (accepted)
        {
            Logger.Info(
                LogTag,
                $"loaded async mod={completionRequest.ModId} " +
                $"sha={ShortSha(completionRequest.CandidateSha256Hex)} path={completionRequest.Path}");
        }
    }

    private static void CompleteBundleFailure(PendingBundleLoad operation, string error)
    {
        PcCompatResourceLoadRequest request;
        lock (Gate)
        {
            if (!PendingBundles.TryGetValue(operation.Key, out var current) || !ReferenceEquals(current, operation))
                return;
            request = operation.Request;
            PendingBundles.Remove(operation.Key);
        }
        ReleaseBundleRequestHandle(operation);
        CompleteBundleLoad(request, Failure(request, error, retryable: false));
        Logger.Warn(LogTag, $"bundle load failed mod={request.ModId} sha={ShortSha(request.CandidateSha256Hex)}: {error}");
    }

    private static bool CompleteBundleLoad(
        PcCompatResourceLoadRequest request,
        PcCompatResourceLoadResult result)
    {
        try
        {
            return PcCompatResourceRecipeRuntime.CompleteBundleLoad(request, result);
        }
        catch (Exception ex)
        {
            Logger.Error(
                LogTag,
                $"resource completion failed mod={request.ModId} sha={request.CandidateSha256Hex}: {ex}");
            if (result.Success)
            {
                ScheduleUnload(new PcCompatResourceUnloadRequest
                {
                    ModId = request.ModId,
                    CandidateSha256Hex = request.CandidateSha256Hex,
                    SessionGeneration = request.SessionGeneration
                });
            }
            return false;
        }
    }

    private static void StartAssetLoadOnUnityMain(LoadedBundle bundle, LoadedAsset entry)
    {
        PcCompatResolvedResourceBinding binding;
        lock (Gate)
        {
            if (!Loaded.TryGetValue(bundle.Key, out var currentBundle) ||
                !ReferenceEquals(currentBundle, bundle) ||
                bundle.UnloadRequested ||
                !bundle.Assets.TryGetValue(entry.Key, out var currentAsset) ||
                !ReferenceEquals(currentAsset, entry) ||
                entry.Status != PcCompatResourceAssetStatus.Queued)
                return;
            binding = entry.Binding;
        }

        try
        {
            object? requestProxy;
            lock (UnityApiGate)
            {
                var api = EnsureApi();
                requestProxy = api.LoadAssetAsync(
                    bundle.BundleProxy,
                    binding.AssetName,
                    binding.ExpectedType);
            }
            var requestObject = PcCompatGeneratedUnityBundleApi.GetPointer(requestProxy);
            if (requestObject == nint.Zero)
            {
                FailAsset(bundle, entry, "AssetBundle.LoadAssetAsync returned null");
                return;
            }

            var accepted = false;
            lock (Gate)
            {
                if (Loaded.TryGetValue(bundle.Key, out var currentBundle) &&
                    ReferenceEquals(currentBundle, bundle) &&
                    bundle.Assets.TryGetValue(entry.Key, out var currentAsset) &&
                    ReferenceEquals(currentAsset, entry))
                {
                    entry.RequestObject = requestObject;
                    entry.RequestProxy = requestProxy;
                    entry.Status = PcCompatResourceAssetStatus.Loading;
                    entry.Sequence = NextSequenceLocked();
                    accepted = true;
                }
            }
            if (!accepted)
                requestProxy = null;
        }
        catch (Exception ex)
        {
            FailAsset(bundle, entry, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void PollAssetLoadOnUnityMain(LoadedBundle bundle, LoadedAsset entry)
    {
        object requestProxy;
        lock (Gate)
        {
            if (!Loaded.TryGetValue(bundle.Key, out var currentBundle) ||
                !ReferenceEquals(currentBundle, bundle) ||
                !bundle.Assets.TryGetValue(entry.Key, out var currentAsset) ||
                !ReferenceEquals(currentAsset, entry) ||
                entry.Status != PcCompatResourceAssetStatus.Loading ||
                entry.RequestObject == nint.Zero ||
                entry.RequestProxy == null)
                return;
            requestProxy = entry.RequestProxy;
        }

        PcCompatGeneratedUnityBundleApi api;
        bool done;
        lock (UnityApiGate)
        {
            api = EnsureApi();
            done = api.IsDone(requestProxy);
        }
        if (!done)
            return;

        object? assetProxy;
        lock (UnityApiGate)
            assetProxy = api.GetAsset(requestProxy);
        var assetObject = PcCompatGeneratedUnityBundleApi.GetPointer(assetProxy);
        ReleaseAssetRequestHandle(entry);
        if (assetObject == nint.Zero)
        {
            FailAsset(bundle, entry, "AssetBundleRequest completed without an asset");
            return;
        }

        var accepted = false;
        var unloadAfterCompletion = false;
        lock (Gate)
        {
            if (Loaded.TryGetValue(bundle.Key, out var currentBundle) &&
                ReferenceEquals(currentBundle, bundle) &&
                bundle.Assets.TryGetValue(entry.Key, out var currentAsset) &&
                ReferenceEquals(currentAsset, entry))
            {
                accepted = true;
                if (bundle.UnloadRequested)
                {
                    bundle.Assets.Remove(entry.Key);
                    unloadAfterCompletion = !HasPendingAssetsLocked(bundle);
                }
                else
                {
                    entry.Asset = assetObject;
                    entry.AssetProxy = assetProxy;
                    entry.Status = PcCompatResourceAssetStatus.Ready;
                    entry.Error = null;
                }
            }
        }
        if (!accepted)
            return;

        if (unloadAfterCompletion)
        {
            assetProxy = null;
            FinalizeBundleUnloadOnUnityMain(bundle);
            return;
        }

        Logger.Info(
            LogTag,
            $"asset ready mod={entry.Binding.ModId} group={entry.Binding.FeatureGroupId} " +
            $"asset={entry.Binding.AssetName} type={entry.Binding.ExpectedType}");
        RefreshResourceConsumersOnUnityMain();
    }

    private static void FailAsset(LoadedBundle bundle, LoadedAsset entry, string error)
    {
        ReleaseAssetRequestHandle(entry);
        var unloadAfterFailure = false;
        lock (Gate)
        {
            if (!Loaded.TryGetValue(bundle.Key, out var currentBundle) ||
                !ReferenceEquals(currentBundle, bundle) ||
                !bundle.Assets.TryGetValue(entry.Key, out var currentAsset) ||
                !ReferenceEquals(currentAsset, entry))
                return;

            entry.RequestObject = nint.Zero;
            entry.Status = PcCompatResourceAssetStatus.Failed;
            entry.Error = error;
            if (bundle.UnloadRequested)
            {
                bundle.Assets.Remove(entry.Key);
                unloadAfterFailure = !HasPendingAssetsLocked(bundle);
            }
        }

        Logger.Warn(
            LogTag,
            $"asset load failed mod={entry.Binding.ModId} group={entry.Binding.FeatureGroupId} " +
            $"asset={entry.Binding.AssetName}: {error}");
        if (unloadAfterFailure)
            FinalizeBundleUnloadOnUnityMain(bundle);
    }

    private static void UnloadOnUnityMain(PcCompatResourceUnloadRequest request)
    {
        LoadedBundle? bundle;
        var waitForPendingAssets = false;
        lock (Gate)
        {
            var key = MakeBundleKey(request.ModId, request.CandidateSha256Hex);
            if (!Loaded.TryGetValue(key, out bundle) ||
                bundle.SessionGeneration != request.SessionGeneration)
                return;

            bundle.UnloadRequested = true;
            foreach (var queued in bundle.Assets.Values
                         .Where(asset => asset.Status == PcCompatResourceAssetStatus.Queued)
                         .ToArray())
                bundle.Assets.Remove(queued.Key);
            if (HasPendingAssetsLocked(bundle))
                waitForPendingAssets = true;
        }

        if (waitForPendingAssets)
        {
            ReleaseResourceConsumersOnUnityMain(bundle);
            return;
        }
        FinalizeBundleUnloadOnUnityMain(bundle);
    }

    private static void FinalizeBundleUnloadOnUnityMain(LoadedBundle bundle)
    {
        lock (Gate)
        {
            if (!Loaded.TryGetValue(bundle.Key, out var current) ||
                !ReferenceEquals(current, bundle) ||
                bundle.UnloadInProgress ||
                HasPendingAssetsLocked(bundle))
                return;
            bundle.UnloadInProgress = true;
        }

        ReleaseResourceConsumersOnUnityMain(bundle);
        try
        {
            lock (UnityApiGate)
                EnsureApi().Unload(bundle.BundleProxy, unloadAllLoadedObjects: true);
        }
        catch (Exception ex)
        {
            lock (Gate)
                bundle.UnloadInProgress = false;
            Logger.Warn(
                LogTag,
                $"Unload failed; retaining ownership mod={bundle.ModId} sha={bundle.Sha256Hex} " +
                $"generation={bundle.SessionGeneration}: {ex.Message}");
            RefreshResourceConsumersOnUnityMain();
            return;
        }

        lock (Gate)
        {
            if (Loaded.TryGetValue(bundle.Key, out var current) && ReferenceEquals(current, bundle))
                Loaded.Remove(bundle.Key);
        }
        foreach (var entry in bundle.Assets.Values)
        {
            ReleaseAssetRequestHandle(entry);
            entry.AssetProxy = null;
            entry.Asset = nint.Zero;
        }
        bundle.Assets.Clear();
        bundle.ManagedProxy = null;
        Logger.Info(
            LogTag,
            $"unloaded mod={bundle.ModId} sha={ShortSha(bundle.Sha256Hex)} " +
            $"managedReleased={(bundle.ManagedReleaseObserved ? 1 : 0)}");
        RefreshResourceConsumersOnUnityMain();
    }

    private static void ReleaseResourceConsumersOnUnityMain(LoadedBundle bundle)
    {
        ClearUiResourcesForModNative(bundle.ModId);
        PcCompatUnityHudBridge.ReleaseResourcesOnUnityMain(
            bundle.ModId,
            bundle.Sha256Hex,
            bundle.SessionGeneration);
    }

    private static void RefreshResourceConsumersOnUnityMain()
    {
        PcCompatUnityHudBridge.RefreshResourcesOnUnityMain();
        RefreshUiResourcesNative();
    }

    private static void PollOneOperationOnUnityMain()
    {
        PollTarget target;
        lock (Gate)
            target = SelectNextPollTargetLocked();
        if (target.Sequence == 0)
            return;
        s_pollCursor = target.Sequence;
        if (target.BundleLoad != null)
            PollBundleLoadOnUnityMain(target.BundleLoad);
        else if (target.Bundle != null && target.Asset != null)
            PollAssetLoadOnUnityMain(target.Bundle, target.Asset);
    }

    private static PollTarget SelectNextPollTargetLocked()
    {
        var after = default(PollTarget);
        var wrap = default(PollTarget);
        foreach (var operation in PendingBundles.Values)
        {
            if (operation.RequestObject == nint.Zero || operation.Sequence == 0)
                continue;
            ConsiderPollTarget(
                new PollTarget(operation.Sequence, operation, null, null),
                ref after,
                ref wrap);
        }
        foreach (var bundle in Loaded.Values)
        {
            foreach (var asset in bundle.Assets.Values)
            {
                if (asset.Status != PcCompatResourceAssetStatus.Loading ||
                    asset.RequestObject == nint.Zero ||
                    asset.Sequence == 0)
                    continue;
                ConsiderPollTarget(
                    new PollTarget(asset.Sequence, null, bundle, asset),
                    ref after,
                    ref wrap);
            }
        }
        return after.Sequence != 0 ? after : wrap;
    }

    private static void ConsiderPollTarget(
        PollTarget candidate,
        ref PollTarget after,
        ref PollTarget wrap)
    {
        if (wrap.Sequence == 0 || candidate.Sequence < wrap.Sequence)
            wrap = candidate;
        if (candidate.Sequence > s_pollCursor &&
            (after.Sequence == 0 || candidate.Sequence < after.Sequence))
            after = candidate;
    }

    private static bool HasPollableOperation()
    {
        lock (Gate)
        {
            if (PendingBundles.Values.Any(operation => operation.RequestObject != nint.Zero))
                return true;
            return Loaded.Values.Any(bundle => bundle.Assets.Values.Any(asset =>
                asset.Status == PcCompatResourceAssetStatus.Loading && asset.RequestObject != nint.Zero));
        }
    }

    private static bool HasPendingAssetsLocked(LoadedBundle bundle)
        => bundle.Assets.Values.Any(asset =>
            asset.Status is PcCompatResourceAssetStatus.Queued or PcCompatResourceAssetStatus.Loading);

    private static void PromoteLoadedBundleLocked(
        LoadedBundle bundle,
        PcCompatResourceLoadRequest request)
    {
        bundle.Path = request.Path;
        bundle.SessionGeneration = request.SessionGeneration;
        bundle.UnloadRequested = false;
        foreach (var failed in bundle.Assets.Values
                     .Where(asset => asset.Status == PcCompatResourceAssetStatus.Failed)
                     .ToArray())
            bundle.Assets.Remove(failed.Key);
        foreach (var asset in bundle.Assets.Values)
            asset.Binding = CloneBindingForGeneration(asset.Binding, request.SessionGeneration);
    }

    private static PcCompatResolvedResourceBinding CloneBindingForGeneration(
        PcCompatResolvedResourceBinding binding,
        long generation)
        => new()
        {
            ModId = binding.ModId,
            FeatureGroupId = binding.FeatureGroupId,
            CandidateSha256Hex = binding.CandidateSha256Hex,
            AssetName = binding.AssetName,
            ExpectedType = binding.ExpectedType,
            Confidence = binding.Confidence,
            SessionGeneration = generation
        };

    private static long NextSequenceLocked()
    {
        s_nextSequence++;
        if (s_nextSequence <= 0)
            s_nextSequence = 1;
        return s_nextSequence;
    }

    private static void ReleaseBundleRequestHandle(PendingBundleLoad operation)
    {
        operation.RequestProxy = null;
        operation.RequestObject = nint.Zero;
    }

    private static void ReleaseAssetRequestHandle(LoadedAsset entry)
    {
        entry.RequestProxy = null;
        entry.RequestObject = nint.Zero;
    }

    public static int GetLoadedCount()
    {
        lock (Gate)
            return Loaded.Count;
    }

    public static int GetLoadedAssetCount()
    {
        lock (Gate)
            return Loaded.Values.Sum(bundle => bundle.Assets.Values.Count(asset =>
                asset.Status == PcCompatResourceAssetStatus.Ready));
    }

    private static PcCompatGeneratedUnityBundleApi EnsureApi()
    {
        if (s_api != null)
            return s_api;
        s_api = new PcCompatGeneratedUnityBundleApi();
        return s_api;
    }

    private static PcCompatGeneratedUnityResourceApi EnsureResourceApi()
    {
        if (s_resourceApi != null)
            return s_resourceApi;
        s_resourceApi = new PcCompatGeneratedUnityResourceApi();
        return s_resourceApi;
    }

    internal static TResult InvokeUnityBundleApi<TResult>(
        Func<PcCompatGeneratedUnityBundleApi, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (UnityApiGate)
            return action(EnsureApi());
    }

    internal static void InvokeUnityBundleApi(Action<PcCompatGeneratedUnityBundleApi> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (UnityApiGate)
            action(EnsureApi());
    }

    private static string MakeBundleKey(string modId, string sha)
        => modId + "\0" + sha;

    private static string MakeAssetKey(PcCompatResolvedResourceBinding binding)
        => binding.FeatureGroupId + "\0" + binding.AssetName + "\0" + binding.ExpectedType;

    private static string ShortSha(string sha)
        => sha[..Math.Min(12, sha.Length)];

    private static PcCompatResourceLoadResult LoadedCacheHit(
        PcCompatResourceLoadRequest request,
        string path)
        => new()
        {
            Success = true,
            ModId = request.ModId,
            CandidateSha256Hex = request.CandidateSha256Hex,
            Path = path,
            CacheHit = true,
            SessionGeneration = request.SessionGeneration
        };

    private static PcCompatResourceLoadResult Pending(
        PcCompatResourceLoadRequest request,
        bool cacheHit)
        => new()
        {
            Success = false,
            Pending = true,
            ModId = request.ModId,
            CandidateSha256Hex = request.CandidateSha256Hex,
            Path = request.Path,
            CacheHit = cacheHit,
            SessionGeneration = request.SessionGeneration
        };

    private static PcCompatResourceLoadResult Failure(
        PcCompatResourceLoadRequest request,
        string error,
        bool retryable)
        => new()
        {
            Success = false,
            Pending = false,
            ModId = request.ModId,
            CandidateSha256Hex = request.CandidateSha256Hex,
            Path = request.Path,
            Error = error,
            Retryable = retryable,
            SessionGeneration = request.SessionGeneration
        };

}

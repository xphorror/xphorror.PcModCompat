using StArray.ModManager.Manager;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Android.PcCompat;

public enum PcCompatCapabilityRegistryStatus
{
    NotInstalled = 0,
    Validated = 1,
    LoadingBundle = 2,
    LoadingManifest = 3,
    LoadingAssets = 4,
    Ready = 5,
    Failed = 6,
}

public sealed record PcCompatCapabilityRegistrySnapshot(
    PcCompatCapabilityRegistryStatus Status,
    int LoadedAssetCount,
    int TotalAssetCount,
    string? Error);

/// <summary>
/// Process-wide registry for the host-owned Android capability bundle. Imported
/// desktop MOD bundles never enter this loader. All Unity API calls are routed
/// through the existing bounded UnityMain queue.
/// </summary>
public static class PcCompatCapabilityBundleRegistry
{
    public const string RuntimeRootEnvironmentVariable = "STARRAY_MODMANAGER_RUNTIME_ROOT";
    private const string LogTag = "PcCompatCapabilities";
    private const int RetryPeriodMilliseconds = 250;
    private const int MaxScheduleFailures = 240;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, LoadedCapabilityAsset> LoadedAssets =
        new(StringComparer.Ordinal);
    private static readonly Queue<PcCompatCapabilityAssetDescriptor> RemainingAssets = new();
    private static PcCompatCapabilityPackage? s_package;
    private static PcCompatCapabilityRegistryStatus s_status;
    private static string? s_error;
    private static bool s_installed;
    private static bool s_stepQueued;
    private static int s_scheduleFailureCount;
    private static Timer? s_retryTimer;
    private static object? s_bundleRequest;
    private static object? s_bundleProxy;
    private static object? s_manifestRequest;
    private static object? s_manifestProxy;
    private static PendingCapabilityAsset? s_pendingAsset;

    private sealed record LoadedCapabilityAsset(
        PcCompatCapabilityAssetDescriptor Descriptor,
        object Proxy,
        nint Pointer);

    private sealed class PendingCapabilityAsset
    {
        public required PcCompatCapabilityAssetDescriptor Descriptor { get; init; }
        public object? RequestProxy { get; set; }
    }

    public static void Install(string? runtimeRoot = null)
    {
        lock (Gate)
        {
            if (s_installed)
                return;
            s_installed = true;
        }

        PcCompatManagedResourceBridge.RegisterCapabilityAssetProvider(AcquireManagedAsset);

        try
        {
            var resolvedRoot = ResolveRuntimeRoot(runtimeRoot);
            var package = PcCompatCapabilityPackageLoader.LoadFromRuntimeRoot(resolvedRoot);
            lock (Gate)
            {
                s_package = package;
                s_status = PcCompatCapabilityRegistryStatus.Validated;
                s_error = null;
                EnsureRetryTimerLocked();
            }
            Logger.Info(
                LogTag,
                $"validated version={package.CapabilityVersion} assets={package.Assets.Count} " +
                $"sha={ShortSha(package.BundleSha256)}");
            RequestStep();
        }
        catch (Exception exception)
        {
            FailWithoutUnity(exception);
        }
    }

    public static PcCompatCapabilityRegistrySnapshot GetSnapshot()
    {
        lock (Gate)
        {
            return new PcCompatCapabilityRegistrySnapshot(
                s_status,
                LoadedAssets.Count,
                s_package?.Assets.Count ?? 0,
                s_error);
        }
    }

    public static bool TryEnsureReady()
    {
        PcCompatCapabilityRegistryStatus status;
        lock (Gate)
            status = s_status;
        if (status is not (PcCompatCapabilityRegistryStatus.Ready or PcCompatCapabilityRegistryStatus.Failed))
            RequestStep();
        return status == PcCompatCapabilityRegistryStatus.Ready;
    }

    public static bool TryGetAsset(
        string stableId,
        string expectedType,
        out object? proxy,
        out nint pointer)
    {
        proxy = null;
        pointer = nint.Zero;
        if (string.IsNullOrWhiteSpace(stableId) || string.IsNullOrWhiteSpace(expectedType))
            return false;

        LoadedCapabilityAsset? loaded;
        lock (Gate)
        {
            if (s_status != PcCompatCapabilityRegistryStatus.Ready)
                return false;
            if (!LoadedAssets.TryGetValue(stableId, out loaded))
                return false;
            if (!ExpectedTypeMatches(loaded.Descriptor.ExpectedType, expectedType))
                return false;
        }
        if (!PcCompatUnityMainExecutionContext.IsActive)
            return false;

        try
        {
            var alive = PcCompatResourceBundleLoader.InvokeUnityBundleApi(
                api => api.IsUnityObjectAlive(loaded.Proxy));
            var currentPointer = alive
                ? PcCompatGeneratedUnityBundleApi.GetPointer(loaded.Proxy)
                : nint.Zero;
            if (currentPointer != nint.Zero && currentPointer == loaded.Pointer)
            {
                proxy = loaded.Proxy;
                pointer = currentPointer;
                return true;
            }
        }
        catch (Exception exception)
        {
            Logger.Warn(
                LogTag,
                $"capability asset liveness check failed id={stableId}: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }

        InvalidateLoadedAsset(stableId, loaded);
        return false;
    }

    private static object? AcquireManagedAsset(PcCompatManagedCapabilityAssetRequest request)
    {
        if (TryGetAsset(request.StableId, request.ExpectedType, out var proxy, out _))
            return proxy;
        TryEnsureReady();
        return null;
    }

    private static void InvalidateLoadedAsset(string stableId, LoadedCapabilityAsset stale)
    {
        var reload = false;
        lock (Gate)
        {
            if (s_status == PcCompatCapabilityRegistryStatus.Ready &&
                LoadedAssets.TryGetValue(stableId, out var current) &&
                ReferenceEquals(current, stale))
            {
                LoadedAssets.Remove(stableId);
                RemainingAssets.Enqueue(stale.Descriptor);
                s_pendingAsset = null;
                s_status = PcCompatCapabilityRegistryStatus.LoadingAssets;
                s_error = $"Capability asset became invalid and is being reloaded: {stableId}";
                EnsureRetryTimerLocked();
                reload = true;
            }
        }

        if (!reload)
            return;
        Logger.Warn(LogTag, $"capability asset invalidated id={stableId}; queued reload");
        RequestStep();
    }

    private static string ResolveRuntimeRoot(string? requestedRoot)
    {
        var root = requestedRoot;
        if (string.IsNullOrWhiteSpace(root))
            root = Environment.GetEnvironmentVariable(RuntimeRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(root))
            root = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(root))
            throw new DirectoryNotFoundException("PcCompat runtime root is unavailable.");
        return Path.GetFullPath(root);
    }

    private static void RequestStep()
    {
        lock (Gate)
        {
            if (!s_installed ||
                s_stepQueued ||
                s_status is PcCompatCapabilityRegistryStatus.NotInstalled or
                    PcCompatCapabilityRegistryStatus.Ready or
                    PcCompatCapabilityRegistryStatus.Failed)
            {
                return;
            }
            s_stepQueued = true;
        }

        if (PcCompatResourceBundleLoader.TryScheduleUnityMainWork(RunStepOnUnityMain))
        {
            lock (Gate)
                s_scheduleFailureCount = 0;
            return;
        }
        var timedOut = false;
        lock (Gate)
        {
            s_stepQueued = false;
            s_scheduleFailureCount++;
            timedOut = s_scheduleFailureCount >= MaxScheduleFailures;
            if (!timedOut)
                EnsureRetryTimerLocked();
        }
        if (timedOut)
        {
            FailWithoutUnity(new TimeoutException(
                "UnityMain presentation hook did not become ready within the capability load budget."));
        }
    }

    private static void RunStepOnUnityMain()
    {
        lock (Gate)
            s_stepQueued = false;
        try
        {
            switch (GetStatus())
            {
                case PcCompatCapabilityRegistryStatus.Validated:
                    StartBundleLoadOnUnityMain();
                    break;
                case PcCompatCapabilityRegistryStatus.LoadingBundle:
                    PollBundleLoadOnUnityMain();
                    break;
                case PcCompatCapabilityRegistryStatus.LoadingManifest:
                    AdvanceManifestLoadOnUnityMain();
                    break;
                case PcCompatCapabilityRegistryStatus.LoadingAssets:
                    AdvanceAssetLoadOnUnityMain();
                    break;
            }
        }
        catch (Exception exception)
        {
            FailOnUnityMain(exception);
            return;
        }
        RequestStep();
    }

    private static void StartBundleLoadOnUnityMain()
    {
        PcCompatCapabilityPackage package;
        lock (Gate)
            package = s_package ?? throw new InvalidOperationException("Capability package is unavailable.");

        // Revalidate immediately before crossing into Unity to close the file
        // replacement window between CoreCLR startup and UnityMain loading.
        var revalidated = PcCompatCapabilityPackageLoader.LoadFromDirectory(package.DirectoryPath);
        if (!revalidated.BundleSha256.Equals(package.BundleSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Capability package identity changed before Unity load.");
        var request = PcCompatResourceBundleLoader.InvokeUnityBundleApi(
            api => api.LoadFromFileAsync(revalidated.BundlePath));
        if (PcCompatGeneratedUnityBundleApi.GetPointer(request) == nint.Zero)
            throw new InvalidOperationException("Host AssetBundle.LoadFromFileAsync returned null.");
        lock (Gate)
        {
            s_package = revalidated;
            s_bundleRequest = request;
            s_status = PcCompatCapabilityRegistryStatus.LoadingBundle;
        }
    }

    private static void PollBundleLoadOnUnityMain()
    {
        object request;
        lock (Gate)
            request = s_bundleRequest ?? throw new InvalidOperationException("Capability bundle request was lost.");
        if (!PcCompatResourceBundleLoader.InvokeUnityBundleApi(api => api.IsDone(request)))
            return;
        var bundle = PcCompatResourceBundleLoader.InvokeUnityBundleApi(api => api.GetAssetBundle(request));
        if (PcCompatGeneratedUnityBundleApi.GetPointer(bundle) == nint.Zero)
            throw new InvalidOperationException("Host capability bundle request completed without a bundle.");
        lock (Gate)
        {
            s_bundleRequest = null;
            s_bundleProxy = bundle;
            s_status = PcCompatCapabilityRegistryStatus.LoadingManifest;
        }
    }

    private static void AdvanceManifestLoadOnUnityMain()
    {
        object bundle;
        object? request;
        lock (Gate)
        {
            bundle = s_bundleProxy ?? throw new InvalidOperationException("Capability bundle proxy was lost.");
            request = s_manifestRequest;
        }
        if (request == null)
        {
            request = PcCompatResourceBundleLoader.InvokeUnityBundleApi(
                api => api.LoadAssetAsync(bundle, "pccompat.manifest", "UnityEngine.TextAsset"));
            if (PcCompatGeneratedUnityBundleApi.GetPointer(request) == nint.Zero)
                throw new InvalidOperationException("Capability internal manifest request returned null.");
            lock (Gate)
                s_manifestRequest = request;
            return;
        }
        if (!PcCompatResourceBundleLoader.InvokeUnityBundleApi(api => api.IsDone(request)))
            return;

        var manifestBaseProxy = PcCompatResourceBundleLoader.InvokeUnityBundleApi(api => api.GetAsset(request));
        if (manifestBaseProxy == null || PcCompatGeneratedUnityBundleApi.GetPointer(manifestBaseProxy) == nint.Zero)
            throw new InvalidOperationException("Capability internal manifest asset is null.");
        var manifestProxy = PcCompatResourceBundleLoader.InvokeUnityBundleApi(
            api => api.WrapAsset(manifestBaseProxy, "UnityEngine.TextAsset"));
        var text = PcCompatResourceBundleLoader.InvokeUnityBundleApi(
            api => api.GetTextAssetText(manifestProxy));
        PcCompatCapabilityPackage package;
        lock (Gate)
            package = s_package ?? throw new InvalidOperationException("Capability package is unavailable.");
        package.ValidateInternalManifest(text ?? string.Empty);

        lock (Gate)
        {
            s_manifestRequest = null;
            s_manifestProxy = manifestProxy;
            RemainingAssets.Clear();
            foreach (var descriptor in package.Assets.Values.OrderBy(asset => asset.Id, StringComparer.Ordinal))
            {
                if (descriptor.Required)
                    RemainingAssets.Enqueue(descriptor);
            }
            s_status = PcCompatCapabilityRegistryStatus.LoadingAssets;
        }
    }

    private static void AdvanceAssetLoadOnUnityMain()
    {
        PendingCapabilityAsset? pending;
        object bundle;
        lock (Gate)
        {
            bundle = s_bundleProxy ?? throw new InvalidOperationException("Capability bundle proxy was lost.");
            pending = s_pendingAsset;
            if (pending == null)
            {
                if (RemainingAssets.Count == 0)
                {
                    CompleteReadyLocked();
                    return;
                }
                pending = new PendingCapabilityAsset { Descriptor = RemainingAssets.Dequeue() };
                s_pendingAsset = pending;
            }
        }

        if (pending.RequestProxy == null)
        {
            var request = PcCompatResourceBundleLoader.InvokeUnityBundleApi(api => api.LoadAssetAsync(
                bundle,
                pending.Descriptor.Address,
                pending.Descriptor.ExpectedType));
            if (PcCompatGeneratedUnityBundleApi.GetPointer(request) == nint.Zero)
                throw new InvalidOperationException(
                    "Capability asset request returned null: " + pending.Descriptor.Id + ".");
            lock (Gate)
                pending.RequestProxy = request;
            return;
        }

        var requestProxy = pending.RequestProxy;
        if (!PcCompatResourceBundleLoader.InvokeUnityBundleApi(api => api.IsDone(requestProxy)))
            return;
        var assetBaseProxy = PcCompatResourceBundleLoader.InvokeUnityBundleApi(api => api.GetAsset(requestProxy));
        if (assetBaseProxy == null || PcCompatGeneratedUnityBundleApi.GetPointer(assetBaseProxy) == nint.Zero)
            throw new InvalidOperationException("Capability asset is null: " + pending.Descriptor.Id + ".");
        var assetProxy = PcCompatResourceBundleLoader.InvokeUnityBundleApi(
            api => api.WrapAsset(assetBaseProxy, pending.Descriptor.ExpectedType));
        var pointer = PcCompatGeneratedUnityBundleApi.GetPointer(assetProxy);
        if (pointer == nint.Zero)
            throw new InvalidOperationException("Capability asset is null: " + pending.Descriptor.Id + ".");
        lock (Gate)
        {
            LoadedAssets.Add(
                pending.Descriptor.Id,
                new LoadedCapabilityAsset(pending.Descriptor, assetProxy, pointer));
            s_pendingAsset = null;
        }
    }

    private static void CompleteReadyLocked()
    {
        var package = s_package ?? throw new InvalidOperationException("Capability package is unavailable.");
        if (LoadedAssets.Count != package.Assets.Values.Count(asset => asset.Required))
            throw new InvalidOperationException("Capability required asset preload count is incomplete.");
        s_status = PcCompatCapabilityRegistryStatus.Ready;
        s_error = null;
        DisposeRetryTimerLocked();
        ThreadPool.QueueUserWorkItem(static state =>
        {
            var completion = ((int Count, string Sha))state!;
            Logger.Info(LogTag, $"ready assets={completion.Count} sha={completion.Sha}");
        }, (LoadedAssets.Count, ShortSha(package.BundleSha256)));
    }

    private static void FailOnUnityMain(Exception exception)
    {
        object? bundle;
        lock (Gate)
            bundle = s_bundleProxy;
        if (bundle != null)
        {
            try
            {
                PcCompatResourceBundleLoader.InvokeUnityBundleApi(
                    api => api.Unload(bundle, unloadAllLoadedObjects: true));
            }
            catch
            {
                // Preserve the original capability failure as the decisive error.
            }
        }
        FailWithoutUnity(exception);
    }

    private static void FailWithoutUnity(Exception exception)
    {
        var message = exception.GetType().Name + ": " + exception.Message;
        lock (Gate)
        {
            s_status = PcCompatCapabilityRegistryStatus.Failed;
            s_error = message;
            s_bundleRequest = null;
            s_bundleProxy = null;
            s_manifestRequest = null;
            s_manifestProxy = null;
            s_pendingAsset = null;
            RemainingAssets.Clear();
            LoadedAssets.Clear();
            DisposeRetryTimerLocked();
        }
        Logger.Error(LogTag, "failed closed: " + message);
    }

    private static void EnsureRetryTimerLocked()
    {
        s_retryTimer ??= new Timer(
            static _ => RequestStep(),
            null,
            RetryPeriodMilliseconds,
            RetryPeriodMilliseconds);
    }

    private static void DisposeRetryTimerLocked()
    {
        var timer = s_retryTimer;
        s_retryTimer = null;
        timer?.Dispose();
    }

    private static PcCompatCapabilityRegistryStatus GetStatus()
    {
        lock (Gate)
            return s_status;
    }

    private static bool ExpectedTypeMatches(string descriptorType, string requestedType)
    {
        var normalized = requestedType.Split(',', 2)[0].Trim().Replace('+', '.');
        if (normalized.Contains('.'))
            return descriptorType.Equals(normalized, StringComparison.Ordinal);
        var simpleName = descriptorType[(descriptorType.LastIndexOf('.') + 1)..];
        return simpleName.Equals(normalized, StringComparison.Ordinal);
    }

    private static string ShortSha(string sha)
        => sha[..Math.Min(12, sha.Length)];
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using StArray.ModManager.Manager;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Android.PcCompat;

public static class PcCompatDobbyBridge
{
    private const string LogTag = "PcCompatDobbyBridge";
    private const int HitMarginsFallbackRefreshIntervalMilliseconds = 100;
    private static readonly long HitMarginsFallbackRefreshIntervalTicks =
        Math.Max(1, Stopwatch.Frequency * HitMarginsFallbackRefreshIntervalMilliseconds / 1000);
    private static readonly object SyncLock = new();
    private static readonly object RuntimeRuleOperationLock = new();

    private static bool s_started;
    private static bool s_syncing;
    private static bool s_syncRequested;
    private static readonly Dictionary<string, HashSet<string>> LoadedRuntimeRulePathsByMod =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<ReversePatchSessionKey, ReversePatchRefreshState>
        ReversePatchStates = new();
    private static readonly ReversePatchRefreshState UnscopedReversePatchState = new();
    private static bool s_hitMarginsStructuralFailure;
    private static long s_hitMarginsRefreshAttempts;
    private static long s_hitMarginsRefreshSuccesses;
    private static long s_hitMarginsRefreshFailures;
    private static long s_hitMarginsRefreshSkips;
    private static long s_hitMarginsRefreshThrottled;

    private readonly record struct ReversePatchSessionKey(
        string ModId,
        long ResourceSessionGeneration);

    private sealed class ReversePatchRefreshState
    {
        public int SnapshotRefreshActive;
        public uint SnapshotGeneration;
        public int SnapshotRefreshFailed;
        public uint HitMarginsNativeGeneration;
        public nint HitMarginsTrackerPointer;
        public long HitMarginsLastSuccessTimestamp;
        public long HitMarginsNextFallbackTimestamp;
        public int HitMarginsLastLength;
        public int HitMarginsLastChecksum;
        public string HitMarginsLastIssue = "none";
    }

    private static ReversePatchRefreshState GetReversePatchState(out string? modId)
    {
        var execution = PcCompatManagedExecutionContext.Current;
        if (execution is null || string.IsNullOrWhiteSpace(execution.ModId))
        {
            modId = null;
            return UnscopedReversePatchState;
        }

        modId = execution.ModId;
        return ReversePatchStates.GetOrAdd(
            new ReversePatchSessionKey(
                execution.ModId,
                execution.ResourceSessionGeneration),
            static _ => new ReversePatchRefreshState());
    }

    public static void Install()
    {
        lock (SyncLock)
        {
            if (s_started)
                return;
            s_started = true;
        }

        PcCompatOverlayRuntime.RegisterProvider(
            PcCompatNativeHookRules.GetOverlaySnapshot,
            PcCompatNativeHookRules.GetOverlayVisible,
            ownerProvider: PcCompatNativeHookRules.GetOverlaySnapshot);
        PcCompatInputHudRuntime.RegisterProvider(PcCompatNativeHookRules.GetInputHudSnapshot);
        PcCompatInputOriginRuntime.RegisterProvider(PcCompatNativeHookRules.GetInputOrigin);
        PcCompatKeyViewerEventRuntime.RegisterProvider(PcCompatNativeHookRules.ReadRawInputEvents);
        PcCompatKeyViewerEventRuntime.RegisterWakeProvider(
            PcCompatNativeHookRules.WaitForRawInputChange,
            PcCompatNativeHookRules.InterruptRawInputWait);
        PcCompatExternalInputDeviceRuntime.RegisterProvider(
            PcCompatNativeHookRules.GetExternalInputDevices);
        PcCompatKeyViewerPreviewRuntime.RefreshWakeProvider();
        PcCompatClockAnchorRuntime.RegisterProvider(PcCompatNativeHookRules.GetClockAnchorSnapshot);
        PcCompatClockAnchorRuntime.RegisterMonotonicProvider(
            PcCompatNativeHookRules.GetMonotonicClockSnapshot);
        PcCompatLevelIdentityRuntime.RegisterProvider(PcCompatNativeHookRules.GetLevelIdentity);
        InitializeHitMarginsCountLayout();
        PcCompatReversePatchBridge.RegisterSnapshotRefresh(RefreshReversePatchSnapshot);
        PcCompatReversePatchBridge.RegisterHitMarginsCountRefresh(RefreshHitMarginsCount);
        PcCompatDiagnosticsRuntime.RegisterProvider(
            PcCompatNativeHookRules.GetDiagnosticsSnapshot,
            PcCompatNativeHookRules.GetSlotSummaryForMod,
            ExecuteDiagnosticsCommand);
        PcCompatDiagnosticsRuntime.RegisterManagedEventStatsProvider(
            PcCompatNativeHookRules.GetManagedEventStats);
        PcCompatDiagnosticsRuntime.RegisterPlatformRuntimeStatsProvider(BuildPlatformRuntimeStats);
        PcCompatResourceChangerRuntime.RegisterSettingsSink(state =>
        {
            PcCompatNativeHookRules.SetResourceChangerState(state);
            PcCompatResourceBundleLoader.ScheduleResourceChangerSprite(state.ModId);
            PcCompatResourceBundleLoader.ScheduleResourceChangerStateApply();
        });
        PcCompatRuntime.RegisterNativeRuleBundleRetireSink(RetireRuntimeRuleBundles);
        PcCompatRuntime.RegistryChanged += Synchronize;
        Logger.Info(LogTag, "native slot bridge registered; managed game-method detours disabled");
        Synchronize();
    }

    private static int ExecuteDiagnosticsCommand(PcCompatDiagnosticsCommand command)
    {
        lock (SyncLock)
        {
            if (s_syncing)
            {
                Logger.Warn(LogTag, $"diagnostics command rejected while synchronize is active: {command}");
                return -2;
            }
            s_syncing = true;
        }

        try
        {
            lock (RuntimeRuleOperationLock)
            {
                switch (command)
                {
                    case PcCompatDiagnosticsCommand.Resolve:
                        return PcCompatNativeHookRules.ResolvePendingSlots();
                    case PcCompatDiagnosticsCommand.Prepare:
                        return PcCompatNativeHookRules.PrepareInstallPlan();
                    case PcCompatDiagnosticsCommand.Install:
                        return PcCompatNativeHookRules.InstallPlannedSlots();
                    case PcCompatDiagnosticsCommand.ClearRules:
                        PcCompatNativeHookRules.Clear();
                        lock (SyncLock)
                            LoadedRuntimeRulePathsByMod.Clear();
                        Logger.Info(LogTag, "runtime rules disabled; persistent Dobby bindings preserved");
                        return 0;
                    case PcCompatDiagnosticsCommand.ReloadRules:
                        PcCompatNativeHookRules.Clear();
                        lock (SyncLock)
                            LoadedRuntimeRulePathsByMod.Clear();
                        SynchronizeRuntimeRuleBundles();
                        return PcCompatNativeHookRules.GetInstalledSlotCount();
                    default:
                        return -1;
                }
            }
        }
        finally
        {
            var synchronizeAgain = false;
            lock (SyncLock)
            {
                s_syncing = false;
                synchronizeAgain = s_syncRequested;
                s_syncRequested = false;
            }
            if (synchronizeAgain)
                Synchronize();
        }
    }

    public static void Synchronize()
    {
        if (!EnvEnabled("STARRAY_PCMOD_COMPAT_ENABLE_DOBBY", true))
        {
            Logger.Info(LogTag, "disabled by STARRAY_PCMOD_COMPAT_ENABLE_DOBBY");
            return;
        }

        lock (SyncLock)
        {
            if (s_syncing)
            {
                s_syncRequested = true;
                return;
            }
            s_syncing = true;
        }

        try
        {
            lock (RuntimeRuleOperationLock)
            {
                SynchronizeRuntimeRuleBundles();

                var patches = PcCompatRuntime.PatchRegistry.Snapshot();
                foreach (var patch in patches)
                    SynchronizePatch(patch);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(LogTag, $"synchronize failed: {ex}");
        }
        finally
        {
            var synchronizeAgain = false;
            lock (SyncLock)
            {
                s_syncing = false;
                synchronizeAgain = s_syncRequested;
                s_syncRequested = false;
            }
            if (synchronizeAgain)
                Synchronize();
        }
    }

    private static void SynchronizeRuntimeRuleBundles()
    {
        var bundles = PcCompatRuntime.SnapshotRecipeBundles();
        var desiredMods = bundles
            .Select(bundle => bundle.ModId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] staleMods;
        lock (SyncLock)
        {
            staleMods = LoadedRuntimeRulePathsByMod.Keys
                .Where(modId => !desiredMods.Contains(modId))
                .ToArray();
        }
        foreach (var staleMod in staleMods)
            RetireRuntimeRuleBundlesCore(staleMod);

        foreach (var bundle in bundles)
        {
            if (!PcCompatRuntime.IsPcModSessionActive(bundle.ModId))
            {
                RetireRuntimeRuleBundlesCore(bundle.ModId);
                Logger.Warn(
                    LogTag,
                    $"runtime rule bundle blocked by inactive PC MOD session mod={bundle.ModId}");
                continue;
            }
            if (string.IsNullOrWhiteSpace(bundle.RecipePath) && string.IsNullOrWhiteSpace(bundle.RulesPath))
                continue;

            var binaryPath = bundle.RecipePath;
            var jsonPath = bundle.RulesPath;
            var hasBinary = File.Exists(binaryPath);
            var hasJson = File.Exists(jsonPath);
            var loadPath = hasBinary ? binaryPath : jsonPath;
            if (string.IsNullOrWhiteSpace(loadPath) || (!hasBinary && !hasJson))
            {
                RetireRuntimeRuleBundlesCore(bundle.ModId);
                Logger.Warn(LogTag, $"runtime rule bundle missing mod={bundle.ModId} binary={binaryPath} json={jsonPath}");
                continue;
            }

            var alreadyLoaded = false;
            var hasDifferentBundle = false;
            lock (SyncLock)
            {
                if (LoadedRuntimeRulePathsByMod.TryGetValue(bundle.ModId, out var paths))
                {
                    alreadyLoaded = paths.Contains(loadPath);
                    hasDifferentBundle = paths.Count != 0 && !alreadyLoaded;
                }
            }
            if (hasDifferentBundle && !RetireRuntimeRuleBundlesCore(bundle.ModId))
                continue;

            if (!alreadyLoaded)
            {
                var loaded = hasBinary && PcCompatNativeHookRules.TryLoadBinary(binaryPath);
                if (!loaded && hasJson)
                {
                    if (hasBinary)
                        Logger.Warn(LogTag, $"binary recipe rejected; falling back to audit JSON mod={bundle.ModId} binary={binaryPath}");
                    loaded = PcCompatNativeHookRules.TryLoad(jsonPath);
                    if (loaded)
                        loadPath = jsonPath;
                }
                if (!loaded)
                    continue;

                lock (SyncLock)
                {
                    if (!LoadedRuntimeRulePathsByMod.TryGetValue(bundle.ModId, out var paths))
                    {
                        paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        LoadedRuntimeRulePathsByMod.Add(bundle.ModId, paths);
                    }
                    paths.Add(loadPath);
                    if (hasBinary)
                        paths.Add(binaryPath);
                }
            }
        }

        // Native HookManager owns metadata-readiness probing and installation.
        // Loading a new bundle wakes its 500 ms coordinator; UI visibility and
        // Unity render callbacks are not part of the hook installation path.
    }

    private static bool RetireRuntimeRuleBundles(string modId)
    {
        lock (RuntimeRuleOperationLock)
            return RetireRuntimeRuleBundlesCore(modId);
    }

    private static bool RetireRuntimeRuleBundlesCore(string modId)
    {
        if (!PcCompatNativeHookRules.TryUnloadMod(modId, out _))
            return false;
        lock (SyncLock)
            LoadedRuntimeRulePathsByMod.Remove(modId);
        return true;
    }

    private static void SynchronizePatch(PcCompatPatchDescriptor patch)
    {
        if (patch.Kind == PcCompatPatchKind.ReversePatch)
        {
            if (!PcCompatReversePatchBridge.TryFindHandler(patch.TargetType, patch.TargetMethod, out _))
            {
                PcCompatRuntime.PatchRegistry.UpdateStatus(
                    patch.ModId,
                    patch.CallbackType,
                    patch.CallbackMethod,
                    PcCompatPatchStatus.Unsupported,
                    "no managed reverse-patch bridge handler");
                return;
            }

            var nativePublisherRules = PcCompatNativeHookRules.GetRuleCountForTarget(
                "scrMarginTracker",
                "CalculatePercentAcc",
                0);
            var reason = nativePublisherRules > 0
                ? "managed reverse-patch handler registered; native fixed-op state publisher active; method body replacement pending"
                : "managed reverse-patch handler registered; native fixed-op state publisher unavailable; method body replacement pending";

            PcCompatRuntime.PatchRegistry.UpdateStatus(
                patch.ModId,
                patch.CallbackType,
                patch.CallbackMethod,
                PcCompatPatchStatus.RegisteredOnly,
                reason);
            return;
        }

        var parameterCount = patch.ArgumentTypeNames.Count > 0
            ? patch.ArgumentTypeNames.Count
            : (int?)null;
        var nativeRuleCount = PcCompatNativeHookRules.GetRuleCountForModTarget(
            patch.ModId,
            patch.TargetType,
            patch.TargetMethod,
            parameterCount);
        PcCompatRuntime.PatchRegistry.UpdateStatus(
            patch.ModId,
            patch.CallbackType,
            patch.CallbackMethod,
            nativeRuleCount > 0
                ? PcCompatPatchStatus.Supported
                : PcCompatPatchStatus.RegisteredOnly,
            nativeRuleCount > 0
                ? $"native rule count={nativeRuleCount}; metadata resolution and permanent HookSlot are native-owned"
                : "descriptor retained; no translated native rule owns this callback");
    }

    private static void RefreshReversePatchSnapshot()
    {
        var state = GetReversePatchState(out var modId);
        if (Volatile.Read(ref state.SnapshotRefreshFailed) != 0 ||
            Interlocked.Exchange(ref state.SnapshotRefreshActive, 1) != 0)
            return;

        try
        {
            // Reverse patches expose official game facts. Do not read the current
            // MOD's HUD reducer here: a consumer may have no visible overlay, and
            // borrowing another owner would leak its private presentation state.
            var overlay = PcCompatNativeHookRules.GetSharedGameSnapshot();
            if (!overlay.ProviderAvailable)
                return;

            var previousGeneration = Volatile.Read(ref state.SnapshotGeneration);
            if (previousGeneration == overlay.Generation)
                return;

            RefreshHitMarginsCountThrottled(state);
            var tracker = state.HitMarginsTrackerPointer;

            if (tracker != nint.Zero)
            {
                PcCompatInteropReadAudit.CompareMarginTracker(
                    tracker,
                    overlay.PercentAcc,
                    overlay.PercentXAcc);
            }

            var execution = PcCompatManagedExecutionContext.Current;
            if (execution is null || execution.ResourceSessionGeneration <= 0)
                return;
            var snapshot = PcCompatGameSnapshot.FromOverlay(
                overlay,
                execution.ResourceSessionGeneration);
            if (snapshot.ValidFields == PcCompatGameSnapshotFields.None)
                return;
            PcCompatReversePatchBridge.PublishSnapshot(snapshot);
            Volatile.Write(ref state.SnapshotGeneration, overlay.Generation);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref state.SnapshotRefreshFailed, 1);
            Logger.Error(
                LogTag,
                "native reverse-patch snapshot refresh disabled after failure " +
                $"mod={modId ?? "<unscoped>"}: {ex}");
        }
        finally
        {
            Volatile.Write(ref state.SnapshotRefreshActive, 0);
        }
    }

    // The MOD retains one managed int[] identity. Native resolves the current player-0
    // tracker and bulk-copies its IL2CPP array; managed only touches the stable array when
    // native generation changes. This keeps checkpoint mutations visible without proxy
    // construction, reflection, boxing, or per-element object[] allocations.
    private static void RefreshHitMarginsCount()
        => RefreshHitMarginsCount(GetReversePatchState(out _));

    private static void RefreshHitMarginsCount(ReversePatchRefreshState state)
    {
        Interlocked.Increment(ref s_hitMarginsRefreshAttempts);
        if (s_hitMarginsStructuralFailure)
        {
            Interlocked.Increment(ref s_hitMarginsRefreshSkips);
            Volatile.Write(ref state.HitMarginsLastIssue, "native-abi-fuse");
            return;
        }

        try
        {
            Span<int> counts = stackalloc int[PcCompatNativeHookRules.HitMarginSnapshotMaxCounts];
            if (!PcCompatNativeHookRules.TryReadHitMarginSnapshot(
                    Volatile.Read(ref state.HitMarginsNativeGeneration),
                    counts,
                    out var generation,
                    out var changed,
                    out var valid,
                    out var length,
                    out var checksum,
                    out var tracker))
            {
                Interlocked.Increment(ref s_hitMarginsRefreshFailures);
                Volatile.Write(ref state.HitMarginsLastIssue, "native-read-unavailable");
                return;
            }

            state.HitMarginsTrackerPointer = tracker;
            if (changed)
            {
                Volatile.Write(ref state.HitMarginsNativeGeneration, generation);
                if (!valid)
                {
                    PcCompatReversePatchBridge.ClearHitMarginsCount();
                    Volatile.Write(ref state.HitMarginsLastLength, 0);
                    Volatile.Write(ref state.HitMarginsLastChecksum, 0);
                }
                else
                {
                    PcCompatReversePatchBridge.PublishHitMarginsCount(counts[..length]);
                    Volatile.Write(ref state.HitMarginsLastLength, length);
                    Volatile.Write(ref state.HitMarginsLastChecksum, checksum);
                }
            }
            Interlocked.Increment(ref s_hitMarginsRefreshSuccesses);
            var now = Stopwatch.GetTimestamp();
            Volatile.Write(ref state.HitMarginsLastSuccessTimestamp, now);
            Volatile.Write(
                ref state.HitMarginsNextFallbackTimestamp,
                now + HitMarginsFallbackRefreshIntervalTicks);
            Volatile.Write(ref state.HitMarginsLastIssue, "none");
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref s_hitMarginsRefreshFailures);
            state.HitMarginsTrackerPointer = nint.Zero;
            s_hitMarginsStructuralFailure = ex is EntryPointNotFoundException or MarshalDirectiveException;
            Volatile.Write(ref state.HitMarginsLastIssue, $"{ex.GetType().Name}:{ex.Message}");
            Logger.Warn(LogTag, $"native hit-margin snapshot read failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void RefreshHitMarginsCountThrottled(ReversePatchRefreshState state)
    {
        var now = Stopwatch.GetTimestamp();
        while (true)
        {
            var next = Volatile.Read(ref state.HitMarginsNextFallbackTimestamp);
            if (now < next)
            {
                Interlocked.Increment(ref s_hitMarginsRefreshThrottled);
                return;
            }
            if (Interlocked.CompareExchange(
                    ref state.HitMarginsNextFallbackTimestamp,
                    now + HitMarginsFallbackRefreshIntervalTicks,
                    next) == next)
                break;
        }

        RefreshHitMarginsCount(state);
    }

    private static string BuildPlatformRuntimeStats()
    {
        var state = GetReversePatchState(out var modId);
        var now = Stopwatch.GetTimestamp();
        var lastSuccess = Volatile.Read(ref state.HitMarginsLastSuccessTimestamp);
        var successAgeMs = lastSuccess == 0
            ? -1
            : Math.Max(0, (long)((now - lastSuccess) * 1000d / Stopwatch.Frequency));
        var overlay = modId is null
            ? PcCompatNativeHookRules.GetOverlaySnapshot()
            : PcCompatNativeHookRules.GetOverlaySnapshot(modId);
        var counts = PcCompatReversePatchBridge.SnapshotHitMarginsCount();
        return
            $"frame[{PcCompatManagedSelfRenderBridge.GetDiagnostics()}] " +
            $"unityMainQueues[{PcCompatResourceBundleLoader.GetUnityMainQueueDiagnostics()}] " +
            $"overlay[available={overlay.ProviderAvailable} visible={overlay.Visible} " +
            $"generation={overlay.Generation} show={overlay.ShowCount} hide={overlay.HideCount} " +
            $"lastOp={overlay.LastOpName} hits={overlay.JudgementHitCount} " +
            $"resets={overlay.JudgementResetCount}] " +
            $"hitMirror[attempts={Interlocked.Read(ref s_hitMarginsRefreshAttempts)} " +
            $"success={Interlocked.Read(ref s_hitMarginsRefreshSuccesses)} " +
            $"failures={Interlocked.Read(ref s_hitMarginsRefreshFailures)} " +
            $"skips={Interlocked.Read(ref s_hitMarginsRefreshSkips)} " +
            $"throttled={Interlocked.Read(ref s_hitMarginsRefreshThrottled)} " +
            $"lastSuccessAgeMs={successAgeMs} generation={Volatile.Read(ref state.HitMarginsNativeGeneration)} " +
            $"tracker=0x{state.HitMarginsTrackerPointer:x} " +
            $"length={Volatile.Read(ref state.HitMarginsLastLength)} " +
            $"checksum={Volatile.Read(ref state.HitMarginsLastChecksum)} " +
            $"counts={string.Join(',', counts)} " +
            $"issue={Volatile.Read(ref state.HitMarginsLastIssue)}]";
    }

    private static void InitializeHitMarginsCountLayout()
    {
        try
        {
            if (!PcCompatIl2CppInteropBootstrap.TryGetProxyType(
                    "Assembly-CSharp",
                    "HitMargin",
                    out var hitMarginType) ||
                !hitMarginType.IsEnum)
            {
                Logger.Warn(LogTag, "HitMargin proxy enum unavailable; live array layout will be discovered late");
                return;
            }

            var slotCount = Enum.GetValues(hitMarginType).Length;
            PcCompatReversePatchBridge.InitializeHitMarginsCountLayout(slotCount);
            Logger.Info(LogTag, $"hit-margin stable array initialized slots={slotCount}");
        }
        catch (Exception ex)
        {
            s_hitMarginsStructuralFailure = true;
            Logger.Error(LogTag, $"hit-margin stable array initialization failed: {ex}");
        }
    }

    private static bool EnvEnabled(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}

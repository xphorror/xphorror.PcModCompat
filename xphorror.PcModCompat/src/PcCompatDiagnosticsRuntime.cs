namespace Xphorror.PcModCompat;

public enum PcCompatDiagnosticsCommand
{
    Resolve,
    Prepare,
    Install,
    ClearRules,
    ReloadRules
}

public sealed class PcCompatVmFaultSnapshot
{
    public static PcCompatVmFaultSnapshot Unavailable { get; } = new();

    public bool ProviderAvailable { get; init; }
    public ulong Cursor { get; init; }
    public ulong Sequence { get; init; }
    public long TimestampNs { get; init; }
    public uint RuleId { get; init; }
    public uint Code { get; init; }
    public uint Pc { get; init; }
    public uint Opcode { get; init; }
    public uint Count { get; init; }
    public ulong DroppedBeforeCursor { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class PcCompatDiagnosticsSnapshot
{
    public static PcCompatDiagnosticsSnapshot Unavailable { get; } = new();

    public bool ProviderAvailable { get; init; }
    public int LoadedBundles { get; init; }
    public int LoadedTargets { get; init; }
    public int LoadedRules { get; init; }
    public int LoadedUiLifecyclePrograms { get; init; }
    public int LoadedUiObjectNodes { get; init; }
    public int LoadedUiComponentOps { get; init; }
    public int LoadedUiResourceBindings { get; init; }
    public int LoadedUiBytecodeInstructions { get; init; }
    public PcCompatPresentationSnapshot Presentation { get; init; } =
        PcCompatPresentationSnapshot.Unavailable;
    public PcCompatPresentationSinkStats PresentationSink { get; init; } =
        PcCompatPresentationSinkStats.Unavailable;
    public int MergedSlots { get; init; }
    public int PendingSlots { get; init; }
    public int ResolvedSlots { get; init; }
    public int FailedSlots { get; init; }
    public int InstallableSlots { get; init; }
    public int InstallBlockedSlots { get; init; }
    public int InstalledSlots { get; init; }
    public int DispatcherReadySlots { get; init; }
    public int BoundDispatcherSlots { get; init; }
    public int DispatcherCapacity { get; init; }
    public int DispatcherRequiredSlots { get; init; }
    public int DispatcherNewSlots { get; init; }
    public int DispatcherAllocatedSlots { get; init; }
    public int DispatcherRemainingSlots { get; init; }
    public int DispatcherBlockedSlots { get; init; }
    public int SlotRules { get; init; }
    public int EnabledSlotRules { get; init; }
    public int DisabledSlotRules { get; init; }
    public ulong ApprovedCapabilities { get; init; }
    public string SlotSummary { get; init; } = string.Empty;
    public string LastError { get; init; } = string.Empty;
    public PcCompatVmFaultSnapshot LatestVmFault { get; init; } = PcCompatVmFaultSnapshot.Unavailable;
}

public sealed class PcCompatDiagnosticsOperationResult
{
    public required PcCompatDiagnosticsCommand Command { get; init; }
    public bool Succeeded { get; init; }
    public int NativeResult { get; init; }
    public string Message { get; init; } = string.Empty;
    public PcCompatDiagnosticsSnapshot Snapshot { get; init; } = PcCompatDiagnosticsSnapshot.Unavailable;
}

/// <summary>Per-MOD native managed-event ring counters (diagnostics export).</summary>
public sealed record PcCompatManagedEventNativeStats(
    bool ProviderAvailable,
    int Rings,
    int EnabledRings,
    ulong PushedTotal,
    ulong QueuedCurrent,
    ulong DroppedTotal)
{
    public static PcCompatManagedEventNativeStats Unavailable { get; } = new(false, 0, 0, 0, 0, 0);
}

public static class PcCompatDiagnosticsRuntime
{
    private const long CacheDurationMilliseconds = 500;
    private static readonly object SyncLock = new();
    private static Func<PcCompatDiagnosticsSnapshot>? s_snapshotProvider;
    private static Func<string, string>? s_modSlotSummaryProvider;
    private static Func<PcCompatDiagnosticsCommand, int>? s_commandExecutor;
    private static Func<string, PcCompatManagedEventNativeStats>? s_managedEventStatsProvider;
    private static Func<string>? s_platformRuntimeStatsProvider;
    private static PcCompatDiagnosticsSnapshot s_cached = PcCompatDiagnosticsSnapshot.Unavailable;
    private static long s_cachedAt;
    private static readonly Dictionary<string, (string Summary, long CachedAt)> ModSlotSummaryCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static void RegisterProvider(
        Func<PcCompatDiagnosticsSnapshot> snapshotProvider,
        Func<string, string> modSlotSummaryProvider,
        Func<PcCompatDiagnosticsCommand, int> commandExecutor)
    {
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        ArgumentNullException.ThrowIfNull(modSlotSummaryProvider);
        ArgumentNullException.ThrowIfNull(commandExecutor);

        lock (SyncLock)
        {
            s_snapshotProvider = snapshotProvider;
            s_modSlotSummaryProvider = modSlotSummaryProvider;
            s_commandExecutor = commandExecutor;
            InvalidateCacheLocked();
        }
    }

    public static void ClearProvider()
    {
        lock (SyncLock)
        {
            s_snapshotProvider = null;
            s_modSlotSummaryProvider = null;
            s_commandExecutor = null;
            InvalidateCacheLocked();
        }
    }

    public static void RegisterManagedEventStatsProvider(
        Func<string, PcCompatManagedEventNativeStats>? provider)
    {
        lock (SyncLock)
            s_managedEventStatsProvider = provider;
    }

    public static PcCompatManagedEventNativeStats GetManagedEventStats(string modId)
    {
        Func<string, PcCompatManagedEventNativeStats>? provider;
        lock (SyncLock)
            provider = s_managedEventStatsProvider;
        if (provider == null)
            return PcCompatManagedEventNativeStats.Unavailable;
        try
        {
            return provider(modId) ?? PcCompatManagedEventNativeStats.Unavailable;
        }
        catch
        {
            return PcCompatManagedEventNativeStats.Unavailable;
        }
    }

    public static void RegisterPlatformRuntimeStatsProvider(Func<string>? provider)
    {
        lock (SyncLock)
            s_platformRuntimeStatsProvider = provider;
    }

    public static string GetPlatformRuntimeStats()
    {
        Func<string>? provider;
        lock (SyncLock)
            provider = s_platformRuntimeStatsProvider;
        if (provider == null)
            return "unavailable";
        try
        {
            return provider() ?? "unavailable";
        }
        catch (Exception exception)
        {
            return $"error:{exception.GetType().Name}:{exception.Message}";
        }
    }

    public static string GetSlotSummaryForMod(string modId, bool forceRefresh = false)
    {
        Func<string, string>? provider;
        var now = Environment.TickCount64;
        lock (SyncLock)
        {
            provider = s_modSlotSummaryProvider;
            if (!forceRefresh &&
                ModSlotSummaryCache.TryGetValue(modId, out var cached) &&
                now - cached.CachedAt < CacheDurationMilliseconds)
                return cached.Summary;
        }
        if (provider == null)
            return string.Empty;

        try
        {
            var summary = provider(modId) ?? string.Empty;
            lock (SyncLock)
            {
                if (ReferenceEquals(provider, s_modSlotSummaryProvider))
                    ModSlotSummaryCache[modId] = (summary, now);
            }
            return summary;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static PcCompatDiagnosticsSnapshot Snapshot(bool forceRefresh = false)
    {
        Func<PcCompatDiagnosticsSnapshot>? provider;
        var now = Environment.TickCount64;
        lock (SyncLock)
        {
            provider = s_snapshotProvider;
            if (provider == null)
                return PcCompatDiagnosticsSnapshot.Unavailable;

            if (!forceRefresh && s_cached.ProviderAvailable && now - s_cachedAt < CacheDurationMilliseconds)
                return s_cached;
        }

        PcCompatDiagnosticsSnapshot snapshot;
        try
        {
            snapshot = provider() ?? PcCompatDiagnosticsSnapshot.Unavailable;
        }
        catch
        {
            snapshot = PcCompatDiagnosticsSnapshot.Unavailable;
        }

        lock (SyncLock)
        {
            if (ReferenceEquals(provider, s_snapshotProvider))
            {
                s_cached = snapshot;
                s_cachedAt = now;
            }
        }

        return snapshot;
    }

    public static PcCompatDiagnosticsOperationResult Execute(PcCompatDiagnosticsCommand command)
    {
        Func<PcCompatDiagnosticsCommand, int>? executor;
        lock (SyncLock)
            executor = s_commandExecutor;

        if (executor == null)
        {
            return new PcCompatDiagnosticsOperationResult
            {
                Command = command,
                Succeeded = false,
                NativeResult = -1,
                Message = "Android native diagnostics provider is unavailable."
            };
        }

        try
        {
            var result = executor(command);
            lock (SyncLock)
                InvalidateCacheLocked();
            var snapshot = Snapshot(forceRefresh: true);
            var succeeded = result >= 0;
            var message = succeeded
                ? $"{command} completed (result={result})."
                : !string.IsNullOrWhiteSpace(snapshot.LastError)
                    ? $"{command} failed (result={result}): {snapshot.LastError}"
                    : $"{command} failed (result={result}).";

            return new PcCompatDiagnosticsOperationResult
            {
                Command = command,
                Succeeded = succeeded,
                NativeResult = result,
                Message = message,
                Snapshot = snapshot
            };
        }
        catch (Exception ex)
        {
            return new PcCompatDiagnosticsOperationResult
            {
                Command = command,
                Succeeded = false,
                NativeResult = -1,
                Message = $"{command} threw: {ex.Message}",
                Snapshot = Snapshot(forceRefresh: true)
            };
        }
    }

    private static void InvalidateCacheLocked()
    {
        s_cached = PcCompatDiagnosticsSnapshot.Unavailable;
        s_cachedAt = 0;
        ModSlotSummaryCache.Clear();
    }
}

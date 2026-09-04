using StArray.ModManager.Interop;

namespace Xphorror.PcModCompat;

public readonly record struct PcCompatKeyViewerPreviewTransition(
    ulong Sequence,
    long RawNs,
    string FeatureId,
    PcCompatKeyViewerInputOrigin Origin,
    PcCompatKeyViewerRawSource Source,
    PcCompatKeyViewerRawPhase Phase,
    int SourceCode,
    int Lane,
    string LaneIdentity);

public sealed class PcCompatKeyViewerPreviewFeatureSnapshot
{
    public required string FeatureId { get; init; }
    public required PcCompatKeyViewerInputMode RequestedInputMode { get; init; }
    public required PcCompatKeyViewerInputMode InputMode { get; init; }
    public bool SessionModeFrozen { get; init; }
    public uint FrozenSessionGeneration { get; init; }
    public PcCompatExternalInputDeviceFlags SessionDeviceFlags { get; init; }
    public string? SessionModeReason { get; init; }
    public int LaneCount { get; init; }
    public PcCompatTouchLaneMappingMode TouchLaneMappingMode { get; init; }
    public int TouchContactReuseDelayMilliseconds { get; init; }
    public uint HeldMask { get; init; }
    public ulong TransitionCount { get; init; }
    public ulong UnmappedEventCount { get; init; }
    public PcCompatKeyViewerConsumerQualification ConsumerQualification { get; init; }
    public bool ConsumerActive { get; init; }
    public string? ConsumerReason { get; init; }
    public int ConsumerMappedIdentityCount { get; init; }
    public IReadOnlyList<ulong> DownOrdinals { get; init; } = Array.Empty<ulong>();
    public IReadOnlyList<ulong> UpOrdinals { get; init; } = Array.Empty<ulong>();
    public IReadOnlyList<PcCompatKeyViewerRainPulse> RainPulses { get; init; } =
        Array.Empty<PcCompatKeyViewerRainPulse>();
    public PcCompatKeyViewerPreviewTransition? LastTransition { get; init; }
}

public readonly record struct PcCompatKeyViewerRainPulse(
    int Lane,
    long DownRawNs,
    long UpRawNs);

internal readonly record struct PcCompatKeyViewerFallbackFeatureState(
    bool Faulted,
    bool ConsumerActive,
    PcCompatKeyViewerInputMode InputMode,
    int LaneCount,
    uint HeldMask,
    long LatestEventRawNs);

internal sealed class PcCompatKeyViewerFallbackFeatureBuffer(
    string featureId,
    int laneCount)
{
    public string FeatureId { get; } = featureId;
    public ulong[] DownOrdinals { get; } = new ulong[laneCount];
    public List<PcCompatKeyViewerRainPulse> RainPulses { get; } = new(256);
    public PcCompatKeyViewerFallbackFeatureState State { get; set; }
    public bool Captured { get; set; }

    public void Clear()
    {
        Array.Clear(DownOrdinals);
        RainPulses.Clear();
        State = default;
        Captured = false;
    }
}

public sealed class PcCompatKeyViewerPreviewSnapshot
{
    public static PcCompatKeyViewerPreviewSnapshot Unregistered { get; } = new();

    public bool Registered { get; init; }
    public bool CursorInitialized { get; init; }
    public bool Faulted { get; init; }
    public string? Fault { get; init; }
    public ulong StartCursor { get; init; }
    public ulong Cursor { get; init; }
    public ulong EventCount { get; init; }
    public ulong DroppedEventCount { get; init; }
    public uint SessionGeneration { get; init; }
    public uint ProducerEpoch { get; init; }
    public PcCompatKeyViewerInputOrigin Origin { get; init; }
    public ulong TouchDownEventCount { get; init; }
    public ulong TouchUpEventCount { get; init; }
    public ulong TouchCancelEventCount { get; init; }
    public IReadOnlyList<PcCompatKeyViewerRawEvent> RecentTouchEvents { get; init; } =
        Array.Empty<PcCompatKeyViewerRawEvent>();
    public IReadOnlyList<PcCompatKeyViewerRawEvent> LastTouchCancelContext { get; init; } =
        Array.Empty<PcCompatKeyViewerRawEvent>();
    public PcCompatModActorSnapshot Actor { get; init; } = new();
    public IReadOnlyList<PcCompatKeyViewerPreviewFeatureSnapshot> Features { get; init; } =
        Array.Empty<PcCompatKeyViewerPreviewFeatureSnapshot>();
}

/// <summary>
/// Observe-only KeyViewer input projection. It consumes the ordered native raw-event
/// journal but never writes MOD state. UnityMain only requests a coalesced pump.
/// Native reads and all state-only
/// projection run on the shared ModActor worker pool; Unity APIs are forbidden.
/// </summary>
public static class PcCompatKeyViewerPreviewRuntime
{
    private const string InputDiagnosticPrefix = "[DEBUG-kv-input-v3]";
    private const int ReadCapacity = 256;
    private static readonly object RegistrationLock = new();
    private static readonly object WakeLock = new();
    private static readonly object PumpOrderingLock = new();
    private static readonly AutoResetEvent PumpProgress = new(false);
    private static readonly Dictionary<string, Registration> Registrations =
        new(StringComparer.OrdinalIgnoreCase);
    private static Registration[] s_dispatchRegistrations = Array.Empty<Registration>();
    private static Action? s_demandChanged;
    private static readonly PcCompatModActorRuntime.PcCompatModActorHandle PumpActor =
        PcCompatModActorRuntime.Register("pccompat:keyviewer:raw-pump");
    private static int s_pumpQueued;
    private static long s_consumerGeneration;
    private static long s_dispatchToken;
    private static Thread? s_wakeThread;
    private static int s_wakeStop = 1;

    private static void WriteInputDiagnostic(string message)
    {
        try
        {
            StArray.ModManager.Manager.Logger.Info(
                nameof(PcCompatKeyViewerPreviewRuntime),
                $"{InputDiagnosticPrefix} {message}");
        }
        catch
        {
            // Diagnostics must never affect the input projection actor.
        }
    }

    public static bool HasRegistrations
        => Volatile.Read(ref s_dispatchRegistrations).Length != 0;

    public static bool HasPumpDemand
        => Volatile.Read(ref s_dispatchRegistrations).Any(value => value.IsPumpActive);

    public static void RegisterDemandChangedSink(Action? sink)
        => Volatile.Write(ref s_demandChanged, sink);

    public static void RefreshWakeProvider()
        => UpdateWakeLoop();

    internal static void ApplyTouchLaneMappingModeTransition(
        PcCompatTouchLaneMappingMode mode,
        long rawNs,
        Action? applyNativeMode)
    {
        lock (PumpOrderingLock)
        {
            foreach (var registration in Volatile.Read(ref s_dispatchRegistrations))
                registration.PostTouchLaneMappingModeChanged(mode, rawNs);
            applyNativeMode?.Invoke();
        }
    }

    internal static void ApplyTouchContactReuseDelayTransition(
        int milliseconds,
        Action? applyNativeDelay)
    {
        lock (PumpOrderingLock)
        {
            foreach (var registration in Volatile.Read(ref s_dispatchRegistrations))
                registration.PostTouchContactReuseDelayChanged(milliseconds);
            applyNativeDelay?.Invoke();
        }
    }

    public static bool RegisterOrUpdate(
        string modId,
        PcCompatKeyViewerAdapterDocument adapter,
        PcCompatKeyViewerOverrideDocument overrides,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(overrides);
        PcCompatVirtualInputAdapterHub.EnsureRegistered();

        var adapterValidation = PcCompatKeyViewerAdapterValidator.Validate(adapter);
        if (!adapterValidation.IsValid)
        {
            error = string.Join("; ", adapterValidation.Errors);
            Unregister(modId);
            return false;
        }
        var overrideValidation = PcCompatKeyViewerOverrideStore.Validate(overrides, adapter);
        if (!overrideValidation.IsValid)
        {
            error = string.Join("; ", overrideValidation.Errors);
            Unregister(modId);
            return false;
        }

        var features = new List<FeatureState>();
        foreach (var featureOverride in overrides.Features.Where(value => value.Enabled))
        {
            var feature = adapter.Features.FirstOrDefault(value =>
                string.Equals(value.Id, featureOverride.FeatureId, StringComparison.Ordinal));
            if (feature == null)
                continue;
            features.Add(new FeatureState(modId, feature, featureOverride));
        }
        if (features.Count == 0)
        {
            error = null;
            Unregister(modId);
            return true;
        }

        Registration? previous;
        var registration = new Registration(modId, features.ToArray());
        lock (RegistrationLock)
        {
            Registrations.TryGetValue(modId, out previous);
            Registrations[modId] = registration;
            PublishRegistrationsLocked();
        }
        previous?.Dispose();
        registration.Activate();
        SyncVirtualInputRegistration(registration);
        NotifyDemandChanged();
        var snapshot = registration.Snapshot();
        PcCompatDeepDebug.Write(
            "touch-registration",
            $"action=register mod={modId} features={snapshot.Features.Count} " +
            $"cursorInitialized={snapshot.CursorInitialized} cursor={snapshot.Cursor} faulted={snapshot.Faulted} " +
            $"featureState=[{string.Join(" | ", snapshot.Features.Select(feature =>
                $"id={feature.FeatureId}/requested={feature.RequestedInputMode}/mode={feature.InputMode}/" +
                $"consumer={feature.ConsumerQualification}/identities={feature.ConsumerMappedIdentityCount}/" +
                $"lanes={feature.LaneCount}/held=0x{feature.HeldMask:X}/reason={PcCompatDeepDebug.Sanitize(feature.ConsumerReason)}"))}]");
        error = null;
        return true;
    }

    public static void Unregister(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
            return;
        Registration? removed = null;
        lock (RegistrationLock)
        {
            if (Registrations.Remove(modId, out removed))
                PublishRegistrationsLocked();
        }
        if (removed != null)
        {
            var snapshot = removed.Snapshot();
            removed.Dispose();
            NotifyDemandChanged();
            PcCompatDeepDebug.Write(
                "touch-registration",
                $"action=unregister mod={modId} cursor={snapshot.Cursor} events={snapshot.EventCount} " +
                $"touchDown={snapshot.TouchDownEventCount} touchUp={snapshot.TouchUpEventCount} " +
                $"touchCancel={snapshot.TouchCancelEventCount} faulted={snapshot.Faulted} " +
                $"fault={PcCompatDeepDebug.Sanitize(snapshot.Fault)}");
        }
    }

    public static PcCompatKeyViewerPreviewSnapshot Snapshot(string modId)
    {
        Registration? registration;
        lock (RegistrationLock)
            Registrations.TryGetValue(modId, out registration);
        return registration?.Snapshot() ?? PcCompatKeyViewerPreviewSnapshot.Unregistered;
    }

    internal static void CopyFallbackFeatures(
        string modId,
        PcCompatKeyViewerFallbackFeatureBuffer[] buffers)
    {
        Registration? registration;
        lock (RegistrationLock)
            Registrations.TryGetValue(modId, out registration);
        if (registration != null)
        {
            registration.CopyFallbackFeatures(buffers);
            return;
        }
        foreach (var buffer in buffers)
            buffer.Clear();
    }

    internal static bool TryGetFeatureInputMode(
        string modId,
        string featureId,
        out PcCompatKeyViewerInputMode inputMode)
    {
        Registration? registration;
        lock (RegistrationLock)
            Registrations.TryGetValue(modId, out registration);
        if (registration != null && registration.TryGetFeatureInputMode(featureId, out inputMode))
            return true;
        inputMode = PcCompatKeyViewerInputMode.Auto;
        return false;
    }

    public static void DispatchFrame()
    {
        if (!HasPumpDemand || Interlocked.CompareExchange(ref s_pumpQueued, 1, 0) != 0)
            return;
        if (!PcCompatModActorRuntime.TryPost(PumpActor, PumpOnce))
            Interlocked.Exchange(ref s_pumpQueued, 0);
    }

    internal static void DispatchVirtualInput(VirtualInputBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        foreach (var registration in Volatile.Read(ref s_dispatchRegistrations))
            registration.PostVirtualInput(batch);
    }

    private static void SyncVirtualInputRegistration(Registration registration)
        => PcCompatVirtualInputAdapterHub.Synchronize(registration.PostVirtualInput);

    public static bool WaitForIdle(TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        if (!PcCompatModActorRuntime.WaitForIdle(PumpActor, timeout))
            return false;
        foreach (var registration in Volatile.Read(ref s_dispatchRegistrations))
        {
            var remaining = timeout == Timeout.InfiniteTimeSpan
                ? timeout
                : timeout - stopwatch.Elapsed;
            if (remaining < TimeSpan.Zero || !registration.WaitForIdle(remaining))
                return false;
        }
        return Volatile.Read(ref s_pumpQueued) == 0;
    }

    private static void PumpOnce()
    {
        try
        {
            PumpOnceCore();
        }
        finally
        {
            Interlocked.Exchange(ref s_pumpQueued, 0);
            PumpProgress.Set();
        }
    }

    private static void PumpOnceCore()
    {
        lock (PumpOrderingLock)
            PumpOnceCoreOrdered();
    }

    private static void PumpOnceCoreOrdered()
    {
        var registrations = Volatile.Read(ref s_dispatchRegistrations);
        var needsOpen = false;
        foreach (var registration in registrations)
            needsOpen |= registration.NeedsOpen;
        if (needsOpen)
        {
            var opened = PcCompatKeyViewerEventRuntime.OpenAtTail();
            foreach (var registration in registrations)
                registration.TryOpen(opened);
        }

        // Registrations normally share one cursor, so one native read fans out to
        // every MOD. Reserving one in-flight batch per registration prevents a
        // later frame from reading the same cursor before its actor advances.
        var dispatchToken = Interlocked.Increment(ref s_dispatchToken);
        for (var index = 0; index < registrations.Length; ++index)
        {
            if (!registrations[index].TryReserveCursor(dispatchToken, out var cursor))
                continue;

            var batch = PcCompatKeyViewerEventRuntime.Read(cursor, ReadCapacity);
            registrations[index].Post(batch);
            for (var target = index + 1; target < registrations.Length; ++target)
            {
                if (registrations[target].TryReserveCursor(dispatchToken, cursor))
                    registrations[target].Post(batch);
            }
        }
    }

    private static void PublishRegistrationsLocked()
        => Volatile.Write(ref s_dispatchRegistrations, Registrations.Values.ToArray());

    private static void NotifyDemandChanged()
    {
        UpdateWakeLoop();
        try
        {
            Volatile.Read(ref s_demandChanged)?.Invoke();
        }
        catch
        {
            // Registration remains valid; the host gate reports its own failure.
        }
    }

    private static void UpdateWakeLoop()
    {
        var shouldRun = HasPumpDemand && PcCompatKeyViewerEventRuntime.HasWakeProvider;
        lock (WakeLock)
        {
            if (shouldRun)
            {
                Volatile.Write(ref s_wakeStop, 0);
                if (s_wakeThread is { IsAlive: true })
                {
                    PcCompatKeyViewerEventRuntime.InterruptWait();
                    return;
                }
                s_wakeThread = new Thread(WakeLoop)
                {
                    IsBackground = true,
                    Name = "PcCompat KeyViewer native wake",
                    Priority = ThreadPriority.BelowNormal
                };
                s_wakeThread.Start();
                return;
            }
            Volatile.Write(ref s_wakeStop, 1);
            PcCompatKeyViewerEventRuntime.InterruptWait();
        }
    }

    private static void WakeLoop()
    {
        try
        {
            while (Volatile.Read(ref s_wakeStop) == 0 && HasPumpDemand &&
                   PcCompatKeyViewerEventRuntime.HasWakeProvider)
            {
                DispatchFrame();
                if (!TryGetWakeCursor(out var cursor, out var batchInFlight))
                {
                    PumpProgress.WaitOne(batchInFlight ? 25 : 100);
                    continue;
                }
                if (PcCompatKeyViewerEventRuntime.WaitForChange(cursor, 250))
                    DispatchFrame();
            }
        }
        finally
        {
            var restart = false;
            lock (WakeLock)
            {
                if (ReferenceEquals(s_wakeThread, Thread.CurrentThread))
                    s_wakeThread = null;
                restart = Volatile.Read(ref s_wakeStop) == 0 &&
                          HasPumpDemand &&
                          PcCompatKeyViewerEventRuntime.HasWakeProvider;
            }
            if (restart)
                UpdateWakeLoop();
        }
    }

    private static bool TryGetWakeCursor(out ulong cursor, out bool batchInFlight)
    {
        cursor = ulong.MaxValue;
        batchInFlight = false;
        var found = false;
        foreach (var registration in Volatile.Read(ref s_dispatchRegistrations))
        {
            if (!registration.TryGetWakeCursor(out var candidate, out var registrationInFlight))
            {
                batchInFlight |= registrationInFlight;
                continue;
            }
            cursor = found ? Math.Min(cursor, candidate) : candidate;
            found = true;
        }
        return found;
    }

    private sealed class Registration
    {
        private const int RecentTouchEventCapacity = 16;
        private const int TouchCancelContextBeforeCapacity = 8;
        private const int TouchCancelContextAfterCapacity = 8;
        private const int TouchCancelContextCapacity =
            TouchCancelContextBeforeCapacity + 1 + TouchCancelContextAfterCapacity;
        private readonly object _lock = new();
        private readonly string _modId;
        private readonly FeatureState[] _features;
        private readonly PcCompatModActorRuntime.PcCompatModActorHandle _actor;
        private long _consumerGeneration;
        private readonly bool _hasConsumer;
        private bool _cursorInitialized;
        private bool _batchInFlight;
        private bool _disposed;
        private long _lastDispatchToken;
        private bool _faulted;
        private string? _fault;
        private ulong _startCursor;
        private ulong _cursor;
        private ulong _eventCount;
        private ulong _droppedEventCount;
        private ulong _touchDownEventCount;
        private ulong _touchUpEventCount;
        private ulong _touchCancelEventCount;
        private readonly PcCompatKeyViewerRawEvent[] _recentTouchEvents =
            new PcCompatKeyViewerRawEvent[RecentTouchEventCapacity];
        private int _recentTouchEventNext;
        private int _recentTouchEventCount;
        private readonly PcCompatKeyViewerRawEvent[] _lastTouchCancelContext =
            new PcCompatKeyViewerRawEvent[TouchCancelContextCapacity];
        private int _lastTouchCancelContextCount;
        private int _touchCancelContextPostRemaining;
        private uint _sessionGeneration;
        private uint _producerEpoch;
        private uint _stateGeneration;
        private PcCompatKeyViewerInputOrigin _origin;
        private ulong _anyUnityDownOrdinal;
        private int _pumpActive = 1;
        private bool _virtualInputActive;
        private long _virtualSessionGeneration;
        private ulong _virtualSequence;
        private PcCompatKeyViewerStatisticsTransaction? _statisticsTransaction;
        private int _openUnavailableDiagnosticLogsRemaining = 1;
        private int _openSuccessDiagnosticLogsRemaining = 1;

        public Registration(string modId, FeatureState[] features)
        {
            _modId = modId;
            _features = features;
            _consumerGeneration = Interlocked.Increment(ref s_consumerGeneration);
            _hasConsumer = features.Any(feature => feature.ConsumerActive);
            _actor = PcCompatModActorRuntime.Register(
                $"pccompat:keyviewer:mod:{modId}",
                failure => Fault("ModActor failed: " + failure));
        }

        public void Activate()
        {
            lock (_lock)
            {
                if (!_disposed && !_faulted && _hasConsumer)
                    PublishConsumerState();
            }
        }

        public void PostTouchLaneMappingModeChanged(
            PcCompatTouchLaneMappingMode mode,
            long rawNs)
        {
            lock (_lock)
            {
                if (_disposed || _faulted)
                    return;
            }
            if (PcCompatModActorRuntime.TryPost(
                    _actor,
                    () => ApplyTouchLaneMappingMode(mode, rawNs)))
                return;
            Fault("ModActor rejected touch lane mapping mode change");
        }

        public void PostTouchContactReuseDelayChanged(int milliseconds)
        {
            lock (_lock)
            {
                if (_disposed || _faulted)
                    return;
            }
            if (PcCompatModActorRuntime.TryPost(
                    _actor,
                    () => ApplyTouchContactReuseDelay(milliseconds)))
                return;
            Fault("ModActor rejected touch contact reuse delay change");
        }

        public bool IsPumpActive => Volatile.Read(ref _pumpActive) != 0;

        public bool NeedsOpen
        {
            get
            {
                lock (_lock)
                    return !_disposed && !_faulted && !_cursorInitialized;
            }
        }

        public void TryOpen(PcCompatKeyViewerEventBatch opened)
        {
            string? diagnostic = null;
            lock (_lock)
            {
                if (_disposed || _faulted || _cursorInitialized)
                    return;
                if (!opened.ProviderAvailable)
                {
                    if (_openUnavailableDiagnosticLogsRemaining-- > 0)
                    {
                        diagnostic = $"boundary=open mod={_modId} result=provider-unavailable " +
                                     $"consumer={(_hasConsumer ? 1 : 0)} " +
                                     $"registration={_consumerGeneration}";
                    }
                }
                else
                {
                    if (opened.DroppedBeforeCursor != 0 || opened.Events.Count != 0)
                    {
                        Fault(
                            "raw event provider violated OpenAtTail contract " +
                            $"events={opened.Events.Count} dropped={opened.DroppedBeforeCursor}");
                        return;
                    }
                    _cursor = opened.Cursor;
                    _startCursor = opened.Cursor;
                    _cursorInitialized = true;
                    if (_openSuccessDiagnosticLogsRemaining-- > 0)
                    {
                        diagnostic = $"boundary=open mod={_modId} result=ready " +
                                     $"cursor={opened.Cursor} consumer={(_hasConsumer ? 1 : 0)} " +
                                     $"registration={_consumerGeneration}";
                    }
                }
            }
            if (diagnostic != null)
                WriteInputDiagnostic(diagnostic);
        }

        public bool TryReserveCursor(long dispatchToken, out ulong cursor)
        {
            lock (_lock)
            {
                cursor = _cursor;
                if (_disposed || _faulted || !_cursorInitialized || _batchInFlight ||
                    _lastDispatchToken == dispatchToken)
                    return false;
                _lastDispatchToken = dispatchToken;
                _batchInFlight = true;
                return true;
            }
        }

        public bool TryGetWakeCursor(out ulong cursor, out bool batchInFlight)
        {
            lock (_lock)
            {
                cursor = _cursor;
                batchInFlight = !_disposed && !_faulted && _batchInFlight;
                return !_disposed && !_faulted && _cursorInitialized && !_batchInFlight;
            }
        }

        public bool TryReserveCursor(long dispatchToken, ulong expectedCursor)
        {
            lock (_lock)
            {
                if (_disposed || _faulted || !_cursorInitialized || _batchInFlight ||
                    _lastDispatchToken == dispatchToken ||
                    _cursor != expectedCursor)
                    return false;
                _lastDispatchToken = dispatchToken;
                _batchInFlight = true;
                return true;
            }
        }

        public void Post(PcCompatKeyViewerEventBatch batch)
        {
            lock (_lock)
            {
                if (_disposed || _faulted)
                {
                    _batchInFlight = false;
                    PumpProgress.Set();
                    return;
                }
                if (!batch.ProviderAvailable ||
                    (batch.DroppedBeforeCursor == 0 &&
                     batch.Events.Count == 0 &&
                     batch.Cursor == _cursor))
                {
                    _batchInFlight = false;
                    PumpProgress.Set();
                    return;
                }
            }
            if (PcCompatModActorRuntime.TryPost(_actor, () => Apply(batch)))
                return;
            lock (_lock)
                _batchInFlight = false;
            PumpProgress.Set();
            Fault("ModActor rejected raw input batch");
        }

        public void PostVirtualInput(VirtualInputBatch batch)
        {
            lock (_lock)
            {
                if (_disposed || _faulted)
                    return;
            }
            if (PcCompatModActorRuntime.TryPost(_actor, () => ApplyVirtualInput(batch)))
                return;
            Fault("ModActor rejected virtual input batch");
        }

        public bool WaitForIdle(TimeSpan timeout)
            => PcCompatModActorRuntime.WaitForIdle(_actor, timeout);

        public void Dispose()
        {
            PcCompatKeyViewerStatisticsTransaction? statisticsTransaction;
            lock (_lock)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _batchInFlight = false;
                Volatile.Write(ref _pumpActive, 0);
                foreach (var feature in _features)
                    feature.ResetHeld();
                statisticsTransaction = _statisticsTransaction;
                _statisticsTransaction = null;
            }
            RestoreStatisticsTransaction(statisticsTransaction, "adapter disposal");
            PumpProgress.Set();
            PcCompatKeyViewerConsumerRuntime.Remove(_modId, _consumerGeneration);
            PcCompatModActorRuntime.Unregister(_actor);
        }

        private void Apply(PcCompatKeyViewerEventBatch batch)
        {
            lock (_lock)
            {
                if (_disposed || _faulted || !_cursorInitialized || _virtualInputActive)
                {
                    _batchInFlight = false;
                    PumpProgress.Set();
                    return;
                }
                if (!batch.ProviderAvailable)
                {
                    _batchInFlight = false;
                    PumpProgress.Set();
                    return;
                }
                if (batch.DroppedBeforeCursor != 0)
                {
                    _droppedEventCount += batch.DroppedBeforeCursor;
                    _batchInFlight = false;
                    PumpProgress.Set();
                    Fault(
                        $"raw event ring overflow cursor={_cursor} " +
                        $"dropped={batch.DroppedBeforeCursor}");
                    return;
                }

                foreach (var inputEvent in batch.Events)
                {
                    if (!ValidateAndAdvance(inputEvent))
                    {
                        _batchInFlight = false;
                        return;
                    }
                    RecordTouchDiagnostics(inputEvent);
                    var anyUnityDown = false;
                    foreach (var feature in _features)
                        anyUnityDown |= feature.Observe(inputEvent);
                    if (anyUnityDown)
                        ++_anyUnityDownOrdinal;
                    ++_eventCount;
                }
                if (batch.Cursor != _cursor)
                {
                    Fault(
                        $"raw event cursor mismatch expected={_cursor} actual={batch.Cursor}");
                }
                else if (_hasConsumer)
                {
                    PublishConsumerState();
                }
                _batchInFlight = false;
                PumpProgress.Set();
            }
        }

        private void ApplyVirtualInput(VirtualInputBatch batch)
        {
            var demandChanged = false;
            lock (_lock)
            {
                if (_disposed || _faulted)
                    return;
                if (batch.Kind == VirtualInputBatchKind.Started)
                {
                    if (_virtualInputActive &&
                        _virtualSessionGeneration == batch.SessionGeneration)
                        return;
                    if (_virtualInputActive && !EndVirtualInput())
                        return;
                    if (BeginVirtualInput(batch.SessionGeneration))
                        demandChanged = true;
                }
                else if (!_virtualInputActive ||
                         batch.SessionGeneration != _virtualSessionGeneration)
                {
                    return;
                }
                else if (batch.Kind is VirtualInputBatchKind.Events or
                         VirtualInputBatchKind.Snapshot or
                         VirtualInputBatchKind.Cancelled)
                {
                    var anyUnityDown = false;
                    foreach (var input in batch.Events)
                    {
                        if (input.Sequence <= _virtualSequence &&
                            batch.Kind != VirtualInputBatchKind.Snapshot)
                        {
                            Fault(
                                $"virtual input sequence regressed previous={_virtualSequence} " +
                                $"actual={input.Sequence}");
                            return;
                        }
                        foreach (var feature in _features)
                            anyUnityDown |= feature.ObserveVirtual(
                                input,
                                batch.Kind == VirtualInputBatchKind.Snapshot,
                                batch.SessionGeneration);
                        _virtualSequence = Math.Max(_virtualSequence, input.Sequence);
                        ++_eventCount;
                    }
                    if (anyUnityDown)
                        ++_anyUnityDownOrdinal;
                    if (_hasConsumer)
                        PublishConsumerState();
                }
                else if (batch.Kind == VirtualInputBatchKind.Ended)
                {
                    if (EndVirtualInput())
                        demandChanged = true;
                }
            }
            if (demandChanged)
                NotifyDemandChanged();
        }

        private bool BeginVirtualInput(long sessionGeneration)
        {
            if (!PcCompatRuntime.TryBeginKeyViewerStatisticsTransaction(
                    _modId,
                    _features.Select(feature => feature.StatisticsFeature).ToArray(),
                    out var statisticsTransaction,
                    out var statisticsError))
            {
                try
                {
                    StArray.ModManager.Manager.Logger.Warn(
                        nameof(PcCompatKeyViewerPreviewRuntime),
                        $"mod={_modId} virtual input session={sessionGeneration} rejected " +
                        $"without faulting adapter: {statisticsError}");
                }
                catch
                {
                }
                return false;
            }
            PcCompatKeyViewerConsumerRuntime.Remove(_modId, _consumerGeneration);
            _statisticsTransaction = statisticsTransaction;
            foreach (var feature in _features)
                feature.BeginVirtualSession();
            _virtualInputActive = true;
            _virtualSessionGeneration = sessionGeneration;
            _virtualSequence = 0;
            _cursorInitialized = false;
            _batchInFlight = false;
            _cursor = 0;
            _startCursor = 0;
            _sessionGeneration = unchecked((uint)Math.Clamp(sessionGeneration, 1, uint.MaxValue));
            _producerEpoch = 0;
            _stateGeneration = 0;
            _origin = PcCompatKeyViewerInputOrigin.ReplayVirtual;
            _anyUnityDownOrdinal = 0;
            _consumerGeneration = Interlocked.Increment(ref s_consumerGeneration);
            Volatile.Write(ref _pumpActive, 0);
            if (_hasConsumer)
                PublishConsumerState();
            return true;
        }

        private bool EndVirtualInput()
        {
            PcCompatKeyViewerConsumerRuntime.Remove(_modId, _consumerGeneration);
            var statisticsTransaction = _statisticsTransaction;
            _statisticsTransaction = null;
            if (!RestoreStatisticsTransaction(statisticsTransaction, "virtual input end"))
            {
                Fault("virtual input statistics restore failed");
                return false;
            }
            foreach (var feature in _features)
                feature.EndVirtualSession();
            _virtualInputActive = false;
            _virtualSessionGeneration = 0;
            _virtualSequence = 0;
            _cursorInitialized = false;
            _batchInFlight = false;
            _consumerGeneration = Interlocked.Increment(ref s_consumerGeneration);
            _anyUnityDownOrdinal = 0;
            Volatile.Write(ref _pumpActive, 1);
            if (_hasConsumer)
                PublishConsumerState();
            return true;
        }

        private void ApplyTouchLaneMappingMode(
            PcCompatTouchLaneMappingMode mode,
            long rawNs)
        {
            lock (_lock)
            {
                if (_disposed || _faulted)
                    return;
                var changed = false;
                foreach (var feature in _features)
                    changed |= feature.SetTouchLaneMappingMode(mode, rawNs);
                if (changed && _hasConsumer)
                    PublishConsumerState();
            }
        }

        private void ApplyTouchContactReuseDelay(int milliseconds)
        {
            lock (_lock)
            {
                if (_disposed || _faulted)
                    return;
                foreach (var feature in _features)
                    feature.SetTouchContactReuseDelayMilliseconds(milliseconds);
            }
        }

        public PcCompatKeyViewerPreviewSnapshot Snapshot()
        {
            lock (_lock)
            {
                return new PcCompatKeyViewerPreviewSnapshot
                {
                    Registered = true,
                    CursorInitialized = _cursorInitialized,
                    Faulted = _faulted,
                    Fault = _fault,
                    StartCursor = _startCursor,
                    Cursor = _cursor,
                    EventCount = _eventCount,
                    DroppedEventCount = _droppedEventCount,
                    SessionGeneration = _sessionGeneration,
                    ProducerEpoch = _producerEpoch,
                    Origin = _origin,
                    TouchDownEventCount = _touchDownEventCount,
                    TouchUpEventCount = _touchUpEventCount,
                    TouchCancelEventCount = _touchCancelEventCount,
                    RecentTouchEvents = CopyRecentTouchEvents(),
                    LastTouchCancelContext = CopyLastTouchCancelContext(),
                    Actor = PcCompatModActorRuntime.Snapshot(_actor),
                    Features = _features.Select(value => value.Snapshot()).ToArray()
                };
            }
        }

        private void RecordTouchDiagnostics(PcCompatKeyViewerRawEvent inputEvent)
        {
            if (inputEvent.Source != PcCompatKeyViewerRawSource.Touch)
                return;
            switch (inputEvent.Phase)
            {
                case PcCompatKeyViewerRawPhase.Down:
                    ++_touchDownEventCount;
                    break;
                case PcCompatKeyViewerRawPhase.Up:
                    ++_touchUpEventCount;
                    break;
                case PcCompatKeyViewerRawPhase.Cancel:
                    ++_touchCancelEventCount;
                    CaptureTouchCancelContext(inputEvent);
                    break;
            }
            if (inputEvent.Phase != PcCompatKeyViewerRawPhase.Cancel &&
                _touchCancelContextPostRemaining > 0)
            {
                _lastTouchCancelContext[_lastTouchCancelContextCount++] = inputEvent;
                --_touchCancelContextPostRemaining;
            }
            _recentTouchEvents[_recentTouchEventNext] = inputEvent;
            _recentTouchEventNext = (_recentTouchEventNext + 1) % RecentTouchEventCapacity;
            _recentTouchEventCount = Math.Min(
                _recentTouchEventCount + 1,
                RecentTouchEventCapacity);
        }

        private void CaptureTouchCancelContext(PcCompatKeyViewerRawEvent cancelEvent)
        {
            var beforeCount = Math.Min(
                _recentTouchEventCount,
                TouchCancelContextBeforeCapacity);
            var start = (_recentTouchEventNext - beforeCount +
                         RecentTouchEventCapacity) % RecentTouchEventCapacity;
            for (var index = 0; index < beforeCount; ++index)
            {
                _lastTouchCancelContext[index] = _recentTouchEvents[
                    (start + index) % RecentTouchEventCapacity];
            }
            _lastTouchCancelContext[beforeCount] = cancelEvent;
            _lastTouchCancelContextCount = beforeCount + 1;
            _touchCancelContextPostRemaining = TouchCancelContextAfterCapacity;
        }

        private PcCompatKeyViewerRawEvent[] CopyRecentTouchEvents()
        {
            if (_recentTouchEventCount == 0)
                return Array.Empty<PcCompatKeyViewerRawEvent>();
            var result = new PcCompatKeyViewerRawEvent[_recentTouchEventCount];
            var start = (_recentTouchEventNext - _recentTouchEventCount +
                         RecentTouchEventCapacity) % RecentTouchEventCapacity;
            for (var index = 0; index < result.Length; ++index)
            {
                result[index] = _recentTouchEvents[
                    (start + index) % RecentTouchEventCapacity];
            }
            return result;
        }

        private PcCompatKeyViewerRawEvent[] CopyLastTouchCancelContext()
        {
            if (_lastTouchCancelContextCount == 0)
                return Array.Empty<PcCompatKeyViewerRawEvent>();
            var result = new PcCompatKeyViewerRawEvent[_lastTouchCancelContextCount];
            Array.Copy(_lastTouchCancelContext, result, result.Length);
            return result;
        }

        public void CopyFallbackFeatures(
            PcCompatKeyViewerFallbackFeatureBuffer[] buffers)
        {
            lock (_lock)
            {
                foreach (var buffer in buffers)
                    buffer.Clear();
                if (_disposed)
                    return;
                foreach (var buffer in buffers)
                {
                    foreach (var feature in _features)
                    {
                        if (!string.Equals(
                                feature.FeatureId,
                                buffer.FeatureId,
                                StringComparison.Ordinal))
                        {
                            continue;
                        }
                        feature.CopyFallbackState(
                            buffer.DownOrdinals,
                            buffer.RainPulses,
                            out var inputMode,
                            out var laneCount,
                            out var heldMask,
                            out var consumerActive,
                            out var latestEventRawNs);
                        buffer.State = new PcCompatKeyViewerFallbackFeatureState(
                            _faulted,
                            consumerActive,
                            inputMode,
                            laneCount,
                            heldMask,
                            latestEventRawNs);
                        buffer.Captured = true;
                        break;
                    }
                }
            }
        }

        public bool TryGetFeatureInputMode(
            string featureId,
            out PcCompatKeyViewerInputMode inputMode)
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    foreach (var feature in _features)
                    {
                        if (!string.Equals(
                                feature.FeatureId,
                                featureId,
                                StringComparison.Ordinal))
                        {
                            continue;
                        }
                        inputMode = feature.InputMode;
                        return true;
                    }
                }
            }
            inputMode = PcCompatKeyViewerInputMode.Auto;
            return false;
        }

        private bool ValidateAndAdvance(PcCompatKeyViewerRawEvent inputEvent)
        {
            var expected = _cursor + 1;
            if (inputEvent.Sequence != expected)
            {
                Fault(
                    $"raw event sequence gap expected={expected} actual={inputEvent.Sequence}");
                return false;
            }

            if (_eventCount != 0)
            {
                if (inputEvent.SessionGeneration != _sessionGeneration &&
                    (inputEvent.Phase != PcCompatKeyViewerRawPhase.Reset ||
                     inputEvent.SessionGeneration != unchecked(_sessionGeneration + 1)))
                {
                    Fault(
                        $"invalid session generation transition previous={_sessionGeneration} " +
                        $"actual={inputEvent.SessionGeneration} phase={inputEvent.Phase}");
                    return false;
                }
                if (inputEvent.ProducerEpoch != _producerEpoch &&
                    (inputEvent.Phase != PcCompatKeyViewerRawPhase.ProducerChanged ||
                     inputEvent.ProducerEpoch != unchecked(_producerEpoch + 1)))
                {
                    Fault(
                        $"invalid producer epoch transition previous={_producerEpoch} " +
                        $"actual={inputEvent.ProducerEpoch} phase={inputEvent.Phase}");
                    return false;
                }
                if (inputEvent.StateGeneration < _stateGeneration)
                {
                    Fault(
                        $"state generation regressed previous={_stateGeneration} " +
                        $"actual={inputEvent.StateGeneration}");
                    return false;
                }
            }

            _cursor = inputEvent.Sequence;
            _sessionGeneration = inputEvent.SessionGeneration;
            _producerEpoch = inputEvent.ProducerEpoch;
            _stateGeneration = inputEvent.StateGeneration;
            _origin = inputEvent.Origin;
            return true;
        }

        private void PublishConsumerState()
        {
            var features = _features.Select(feature => feature.Publish(
                _cursor,
                _sessionGeneration,
                _producerEpoch,
                _consumerGeneration)).ToArray();
            PcCompatKeyViewerConsumerRuntime.Publish(
                _modId,
                features,
                _cursor,
                _anyUnityDownOrdinal);
        }

        private void Fault(string reason)
        {
            PcCompatKeyViewerStatisticsTransaction? statisticsTransaction;
            lock (_lock)
            {
                if (_disposed || _faulted)
                    return;
                _faulted = true;
                _batchInFlight = false;
                Volatile.Write(ref _pumpActive, 0);
                _fault = $"mod={_modId}: {reason}";
                foreach (var feature in _features)
                    feature.ResetHeld();
                statisticsTransaction = _statisticsTransaction;
                _statisticsTransaction = null;
            }
            RestoreStatisticsTransaction(statisticsTransaction, "adapter fault");
            PumpProgress.Set();
            PcCompatKeyViewerConsumerRuntime.Remove(_modId, _consumerGeneration);
            NotifyDemandChanged();
        }

        private bool RestoreStatisticsTransaction(
            PcCompatKeyViewerStatisticsTransaction? transaction,
            string reason)
        {
            if (transaction == null || transaction.TryRestore(out var error))
                return true;
            lock (_lock)
            {
                if (!_disposed && _statisticsTransaction == null)
                    _statisticsTransaction = transaction;
            }
            try
            {
                StArray.ModManager.Manager.Logger.Warn(
                    nameof(PcCompatKeyViewerPreviewRuntime),
                    $"mod={_modId} {reason} statistics restore failed: {error}");
            }
            catch
            {
            }
            return false;
        }
    }

    private sealed class FeatureState
    {
        private const int MaxTouchSlots = 32;
        private const int MaxRainPulses = 2048;
        private readonly string _modId;
        private readonly string _featureId;
        private readonly PcCompatKeyViewerFeatureAdapter _adapter;
        private readonly PcCompatKeyViewerFeatureOverride _override;
        private readonly PcCompatKeyViewerInputMode _requestedInputMode;
        private readonly int _laneCount;
        private readonly int[] _touchSlotLanes = Enumerable.Repeat(-1, MaxTouchSlots).ToArray();
        private readonly int[] _touchLaneHeldCounts;
        private readonly long[] _touchLaneLastUpRawNs;
        private readonly int[] _externalLaneHeldCounts;
        private readonly Dictionary<ExternalContact, uint> _externalContacts = [];
        private readonly Dictionary<string, uint> _virtualKeyboardContacts =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, int> _virtualPointerSlots = [];
        private readonly HashSet<string> _loggedUnmappedVirtualKeys =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<PcCompatCanonicalInputIdentity, uint> _consumerLaneMasks = [];
        private readonly PcCompatKeyViewerConsumerQualification _consumerQualification;
        private readonly string? _consumerReason;
        private readonly PcCompatKeyViewerPublishedIdentity[] _consumerIdentities;
        private readonly ulong[] _downOrdinals;
        private readonly ulong[] _upOrdinals;
        private readonly uint _unityConsumerLaneMask;
        private readonly PcCompatExternalInputDeviceFlags _externalDeviceInterest;
        private PcCompatTouchLaneMappingMode _touchLaneMappingMode;
        private int _touchContactReuseDelayMilliseconds;
        private uint _ignoredTouchUpSlots;
        private uint _heldMask;
        private ulong _transitionCount;
        private ulong _unmappedEventCount;
        private PcCompatKeyViewerPreviewTransition? _lastTransition;
        private PcCompatKeyViewerInputMode _inputMode;
        private bool _sessionModeFrozen;
        private uint _frozenSessionGeneration;
        private PcCompatExternalInputDeviceFlags _sessionDeviceFlags;
        private string? _sessionModeReason;
        private PcCompatKeyViewerInputMode _preVirtualInputMode;
        private bool _preVirtualSessionModeFrozen;
        private uint _preVirtualFrozenSessionGeneration;
        private PcCompatExternalInputDeviceFlags _preVirtualSessionDeviceFlags;
        private string? _preVirtualSessionModeReason;
        private readonly List<PcCompatKeyViewerRainPulse> _rainPulses = [];
        private long _latestRainRawNs;

        public FeatureState(
            string modId,
            PcCompatKeyViewerFeatureAdapter feature,
            PcCompatKeyViewerFeatureOverride featureOverride)
        {
            _modId = modId;
            _adapter = feature;
            _override = featureOverride;
            _featureId = feature.Id;
            _requestedInputMode = featureOverride.InputMode;
            _inputMode = featureOverride.InputMode;
            _laneCount = Math.Clamp(featureOverride.TouchLaneCount, 2, 10);
            _touchLaneMappingMode = PcCompatTouchLaneMappingRuntime.Current;
            _touchContactReuseDelayMilliseconds =
                PcCompatTouchLaneMappingRuntime.TouchContactReuseDelayMilliseconds;
            _downOrdinals = new ulong[_laneCount];
            _upOrdinals = new ulong[_laneCount];
            _touchLaneHeldCounts = new int[_laneCount];
            _touchLaneLastUpRawNs = new long[_laneCount];
            _externalLaneHeldCounts = new int[_laneCount];
            (_consumerQualification, _consumerReason, _consumerIdentities) =
                CompileConsumer(modId, feature, featureOverride, _laneCount);
            foreach (var identity in _consumerIdentities)
            {
                var canonical = new PcCompatCanonicalInputIdentity(identity.Kind, identity.Value);
                _consumerLaneMasks.TryGetValue(canonical, out var lanes);
                _consumerLaneMasks[canonical] = lanes | (1u << identity.Lane);
                if (identity.Kind == PcCompatInputIdentityKind.UnityKeyCode)
                    _unityConsumerLaneMask |= 1u << identity.Lane;
            }
            _externalDeviceInterest = ResolveExternalDeviceInterest(_consumerIdentities);
            if (_requestedInputMode == PcCompatKeyViewerInputMode.Auto)
            {
                var devices = PcCompatExternalInputDeviceRuntime.Snapshot();
                _sessionDeviceFlags = devices.Flags;
                _inputMode = HasRelevantExternalDevice(devices.Flags)
                    ? PcCompatKeyViewerInputMode.External
                    : PcCompatKeyViewerInputMode.Touch;
                _sessionModeReason = devices.Available
                    ? "Auto provisional mode from current device snapshot; awaiting session boundary"
                    : "Auto device snapshot unavailable; provisional Touch mode";
            }
            else
            {
                _sessionModeFrozen = true;
                _sessionModeReason = "explicit mode override";
            }
        }

        public bool ConsumerActive
            => _consumerQualification != PcCompatKeyViewerConsumerQualification.None;

        public string FeatureId => _featureId;
        public PcCompatKeyViewerInputMode InputMode => _inputMode;
        public uint HeldMask => _heldMask;
        public PcCompatKeyViewerStatisticsFeature StatisticsFeature
            => new(_adapter, _override);

        public bool Observe(PcCompatKeyViewerRawEvent inputEvent)
        {
            FreezeInputMode(inputEvent);
            bool consumed;
            if (inputEvent.Phase is PcCompatKeyViewerRawPhase.Reset or
                PcCompatKeyViewerRawPhase.ProducerChanged)
            {
                ResetHeld(publishRelease: true, inputEvent.RawNs);
                consumed = false;
            }
            else if (inputEvent.Source == PcCompatKeyViewerRawSource.Touch)
            {
                if (_inputMode is PcCompatKeyViewerInputMode.External)
                    consumed = false;
                else
                    consumed = ObserveTouch(inputEvent);
            }
            else if (_inputMode is PcCompatKeyViewerInputMode.Touch)
            {
                consumed = false;
            }
            else
            {
                consumed = ObserveExternal(inputEvent);
            }

            PcCompatDeepDebug.WriteState(
                "touch-event",
                _modId + "\0" + _featureId,
                inputEvent.Sequence + ":" + inputEvent.SessionGeneration + ":" +
                inputEvent.ProducerEpoch + ":" + _heldMask + ":" +
                string.Join(',', _downOrdinals) + ":" + string.Join(',', _upOrdinals),
                $"mod={_modId} feature={_featureId} sequence={inputEvent.Sequence} " +
                $"rawNs={inputEvent.RawNs} stateGeneration={inputEvent.StateGeneration} " +
                $"sessionGeneration={inputEvent.SessionGeneration} producerEpoch={inputEvent.ProducerEpoch} " +
                $"origin={inputEvent.Origin} source={inputEvent.Source} phase={inputEvent.Phase} " +
                $"code={inputEvent.Code} sourceCode={inputEvent.SourceCode} scanCode={inputEvent.ScanCode} " +
                $"slot={inputEvent.Slot} pointerCount={inputEvent.PointerCount} device={inputEvent.DeviceId} " +
                $"viewport={inputEvent.ViewportWidth}x{inputEvent.ViewportHeight} x={inputEvent.X:F2} y={inputEvent.Y:F2} " +
                $"requestedMode={_requestedInputMode} mode={_inputMode} modeFrozen={_sessionModeFrozen} " +
                $"modeReason={PcCompatDeepDebug.Sanitize(_sessionModeReason)} consumed={consumed} " +
                $"consumer={_consumerQualification} identities=[{string.Join(',', _consumerIdentities.Select(identity =>
                    $"{identity.Kind}:{identity.Value}->lane{identity.Lane}"))}] " +
                $"held=0x{_heldMask:X} down=[{string.Join(',', _downOrdinals)}] " +
                $"up=[{string.Join(',', _upOrdinals)}] transitionCount={_transitionCount} " +
                $"unmapped={_unmappedEventCount} last={_lastTransition}");
            return consumed;
        }

        public void BeginVirtualSession()
        {
            _preVirtualInputMode = _inputMode;
            _preVirtualSessionModeFrozen = _sessionModeFrozen;
            _preVirtualFrozenSessionGeneration = _frozenSessionGeneration;
            _preVirtualSessionDeviceFlags = _sessionDeviceFlags;
            _preVirtualSessionModeReason = _sessionModeReason;
            ResetHeld();
            Array.Clear(_downOrdinals);
            Array.Clear(_upOrdinals);
            _rainPulses.Clear();
            _latestRainRawNs = 0;
            _loggedUnmappedVirtualKeys.Clear();
            _inputMode = PcCompatKeyViewerInputMode.Touch;
            _sessionModeFrozen = true;
            _frozenSessionGeneration = 0;
            _sessionModeReason = "VirtualInput V2 exclusive playback";
        }

        public void EndVirtualSession()
        {
            ResetHeld();
            Array.Clear(_downOrdinals);
            Array.Clear(_upOrdinals);
            _rainPulses.Clear();
            _latestRainRawNs = 0;
            _loggedUnmappedVirtualKeys.Clear();
            _inputMode = _preVirtualInputMode;
            _sessionModeFrozen = _preVirtualSessionModeFrozen;
            _frozenSessionGeneration = _preVirtualFrozenSessionGeneration;
            _sessionDeviceFlags = _preVirtualSessionDeviceFlags;
            _sessionModeReason = _preVirtualSessionModeReason;
        }

        public bool ObserveVirtual(
            VirtualInputEvent input,
            bool snapshot,
            long sessionGeneration)
        {
            var downOrdinals = snapshot ? (ulong[])_downOrdinals.Clone() : null;
            var upOrdinals = snapshot ? (ulong[])_upOrdinals.Clone() : null;
            var transitionCount = _transitionCount;
            var lastTransition = _lastTransition;
            var rainCount = _rainPulses.Count;
            var latestRainRawNs = _latestRainRawNs;
            var anyUnityDown = input.Device == VirtualInputDevice.Touch
                ? ObserveVirtualTouch(input, sessionGeneration)
                : ObserveVirtualKeyboard(input, sessionGeneration);
            if (snapshot)
            {
                Array.Copy(downOrdinals!, _downOrdinals, _downOrdinals.Length);
                Array.Copy(upOrdinals!, _upOrdinals, _upOrdinals.Length);
                if (_rainPulses.Count > rainCount)
                    _rainPulses.RemoveRange(rainCount, _rainPulses.Count - rainCount);
                _latestRainRawNs = latestRainRawNs;
                _transitionCount = transitionCount;
                _lastTransition = lastTransition;
                anyUnityDown = false;
            }
            return anyUnityDown;
        }

        private bool ObserveVirtualTouch(VirtualInputEvent input, long sessionGeneration)
        {
            if (input.Phase == VirtualInputPhase.Move)
                return false;
            var slot = ResolveVirtualPointerSlot(input);
            if (slot < 0)
                return false;
            var raw = new PcCompatKeyViewerRawEvent(
                input.Sequence,
                ToRawNanoseconds(input.OffsetMicroseconds),
                0,
                unchecked((uint)Math.Clamp(sessionGeneration, 1, uint.MaxValue)),
                0,
                PcCompatKeyViewerInputOrigin.ReplayVirtual,
                PcCompatKeyViewerRawSource.Touch,
                ToRawPhase(input.Phase),
                0,
                slot,
                Math.Max(1, _virtualPointerSlots.Count),
                0,
                0,
                -1,
                input.RepeatCount,
                0,
                0,
                ToViewportDimension(input.ViewportWidth),
                ToViewportDimension(input.ViewportHeight),
                input.X,
                input.Y,
                0);
            var result = ObserveTouch(raw);
            if (input.Phase is VirtualInputPhase.Up or VirtualInputPhase.Cancel)
                _virtualPointerSlots.Remove(input.PointerId);
            return result;
        }

        private bool ObserveVirtualKeyboard(VirtualInputEvent input, long sessionGeneration)
        {
            var key = input.CanonicalKey?.Trim();
            if (string.IsNullOrWhiteSpace(key))
                return false;
            if (input.Phase == VirtualInputPhase.Down)
            {
                if (_virtualKeyboardContacts.ContainsKey(key))
                    return false;
                var mappedIdentities = PcCompatVirtualInputIdentityMapper.Map(key);
                uint matchedLanes = 0;
                foreach (var identity in mappedIdentities)
                {
                    if (_consumerLaneMasks.TryGetValue(identity, out var lanes))
                        matchedLanes |= lanes;
                }
                if (matchedLanes == 0)
                {
                    ++_unmappedEventCount;
                    _lastTransition = CreateVirtualTransition(input, -1, key);
                    LogUnmappedVirtualKey(key, mappedIdentities);
                    return false;
                }
                _virtualKeyboardContacts[key] = matchedLanes;
                var anyUnityDown = false;
                for (var lane = 0; lane < _laneCount; ++lane)
                {
                    var bit = 1u << lane;
                    if ((matchedLanes & bit) == 0)
                        continue;
                    var wasHeld = (_heldMask & bit) != 0;
                    ++_externalLaneHeldCounts[lane];
                    _heldMask |= bit;
                    if (!wasHeld)
                    {
                        ++_downOrdinals[lane];
                        RecordRainDown(lane, ToRawNanoseconds(input.OffsetMicroseconds));
                        anyUnityDown |= (_unityConsumerLaneMask & bit) != 0;
                    }
                }
                ++_transitionCount;
                _lastTransition = CreateVirtualTransition(input, FirstLane(matchedLanes), key);
                return anyUnityDown;
            }

            if (input.Phase is not (VirtualInputPhase.Up or VirtualInputPhase.Cancel) ||
                !_virtualKeyboardContacts.Remove(key, out var releasedLanes))
                return false;
            for (var lane = 0; lane < _laneCount; ++lane)
            {
                var bit = 1u << lane;
                if ((releasedLanes & bit) == 0)
                    continue;
                if (_externalLaneHeldCounts[lane] > 0)
                    --_externalLaneHeldCounts[lane];
                if (_externalLaneHeldCounts[lane] == 0 && !IsLaneHeldByTouch(lane))
                {
                    _heldMask &= ~bit;
                    ++_upOrdinals[lane];
                    RecordRainUp(lane, ToRawNanoseconds(input.OffsetMicroseconds));
                }
            }
            ++_transitionCount;
            _lastTransition = CreateVirtualTransition(input, FirstLane(releasedLanes), key);
            return false;
        }

        private void LogUnmappedVirtualKey(
            string key,
            IReadOnlyList<PcCompatCanonicalInputIdentity> mappedIdentities)
        {
            if (!_loggedUnmappedVirtualKeys.Add(key))
                return;
            var mapped = mappedIdentities.Count == 0
                ? "none"
                : string.Join(',', mappedIdentities.Select(value => $"{value.Kind}:{value.Value}"));
            var consumer = _consumerIdentities.Length == 0
                ? "none"
                : string.Join(',', _consumerIdentities.Select(value =>
                    $"lane{value.Lane}/{value.Kind}:{value.Value}"));
            try
            {
                StArray.ModManager.Manager.Logger.Warn(
                    nameof(PcCompatKeyViewerPreviewRuntime),
                    $"mod={_modId} feature={_featureId} virtual key rejected " +
                    $"key='{key}' mapped=[{mapped}] consumer=[{consumer}]");
            }
            catch
            {
            }
        }

        public void ResetHeld(bool publishRelease = false, long rawNs = 0)
        {
            if (publishRelease)
            {
                for (var lane = 0; lane < _laneCount; ++lane)
                {
                    if ((_heldMask & (1u << lane)) != 0)
                        ++_upOrdinals[lane];
                    if ((_heldMask & (1u << lane)) != 0)
                        RecordRainUp(lane, rawNs);
                }
            }
            _heldMask = 0;
            Array.Fill(_touchSlotLanes, -1);
            Array.Clear(_touchLaneHeldCounts);
            Array.Clear(_touchLaneLastUpRawNs);
            _ignoredTouchUpSlots = 0;
            Array.Clear(_externalLaneHeldCounts);
            _externalContacts.Clear();
            _virtualKeyboardContacts.Clear();
            _virtualPointerSlots.Clear();
        }

        public bool SetTouchLaneMappingMode(
            PcCompatTouchLaneMappingMode mode,
            long rawNs)
        {
            mode = PcCompatTouchLaneMappingRuntime.Normalize(mode);
            if (_touchLaneMappingMode == mode)
                return false;
            ResetTouchHeld(publishRelease: true, rawNs);
            _touchLaneMappingMode = mode;
            return true;
        }

        public void SetTouchContactReuseDelayMilliseconds(int milliseconds)
            => _touchContactReuseDelayMilliseconds =
                PcCompatTouchLaneMappingRuntime
                    .NormalizeTouchContactReuseDelayMilliseconds(milliseconds);

        private void ResetTouchHeld(bool publishRelease, long rawNs)
        {
            uint touchedLanes = 0;
            for (var slot = 0; slot < _touchSlotLanes.Length; ++slot)
            {
                var lane = _touchSlotLanes[slot];
                if (lane >= 0 && lane < _laneCount)
                {
                    touchedLanes |= 1u << lane;
                    _ignoredTouchUpSlots |= 1u << slot;
                }
            }
            Array.Fill(_touchSlotLanes, -1);
            Array.Clear(_touchLaneHeldCounts);
            Array.Clear(_touchLaneLastUpRawNs);
            for (var lane = 0; lane < _laneCount; ++lane)
            {
                var laneBit = 1u << lane;
                if ((touchedLanes & laneBit) == 0 || _externalLaneHeldCounts[lane] != 0)
                    continue;
                if ((_heldMask & laneBit) == 0)
                    continue;
                _heldMask &= ~laneBit;
                if (publishRelease)
                {
                    ++_upOrdinals[lane];
                    RecordRainUp(lane, rawNs);
                }
            }
        }

        public PcCompatKeyViewerPreviewFeatureSnapshot Snapshot()
            => new()
            {
                FeatureId = _featureId,
                RequestedInputMode = _requestedInputMode,
                InputMode = _inputMode,
                SessionModeFrozen = _sessionModeFrozen,
                FrozenSessionGeneration = _frozenSessionGeneration,
                SessionDeviceFlags = _sessionDeviceFlags,
                SessionModeReason = _sessionModeReason,
                LaneCount = _laneCount,
                TouchLaneMappingMode = _touchLaneMappingMode,
                TouchContactReuseDelayMilliseconds =
                    _touchContactReuseDelayMilliseconds,
                HeldMask = _heldMask,
                TransitionCount = _transitionCount,
                UnmappedEventCount = _unmappedEventCount,
                ConsumerQualification = _consumerQualification,
                ConsumerActive = _consumerQualification !=
                    PcCompatKeyViewerConsumerQualification.None,
                ConsumerReason = _consumerReason,
                ConsumerMappedIdentityCount = _consumerIdentities.Length,
                DownOrdinals = (ulong[])_downOrdinals.Clone(),
                UpOrdinals = (ulong[])_upOrdinals.Clone(),
                RainPulses = _rainPulses.ToArray(),
                LastTransition = _lastTransition
            };

        public void CopyFallbackState(
            ulong[] downOrdinals,
            List<PcCompatKeyViewerRainPulse> rainPulses,
            out PcCompatKeyViewerInputMode inputMode,
            out int laneCount,
            out uint heldMask,
            out bool consumerActive,
            out long latestEventRawNs)
        {
            inputMode = _inputMode;
            laneCount = _laneCount;
            heldMask = _heldMask;
            consumerActive = ConsumerActive;
            latestEventRawNs = _latestRainRawNs;
            Array.Copy(
                _downOrdinals,
                downOrdinals,
                Math.Min(_downOrdinals.Length, downOrdinals.Length));
            var start = Math.Max(0, _rainPulses.Count - 256);
            for (var index = start; index < _rainPulses.Count; ++index)
                rainPulses.Add(_rainPulses[index]);
        }

        private void FreezeInputMode(PcCompatKeyViewerRawEvent inputEvent)
        {
            if (_requestedInputMode != PcCompatKeyViewerInputMode.Auto)
            {
                _inputMode = _requestedInputMode;
                _sessionModeFrozen = true;
                _frozenSessionGeneration = inputEvent.SessionGeneration;
                return;
            }
            if (_sessionModeFrozen &&
                _frozenSessionGeneration == inputEvent.SessionGeneration)
                return;

            PcCompatExternalInputDeviceSnapshot devices;
            if (inputEvent.Phase == PcCompatKeyViewerRawPhase.Reset)
            {
                const PcCompatExternalInputDeviceFlags known =
                    PcCompatExternalInputDeviceFlags.Keyboard |
                    PcCompatExternalInputDeviceFlags.Controller |
                    PcCompatExternalInputDeviceFlags.Mouse;
                devices = new PcCompatExternalInputDeviceSnapshot(
                    true,
                    0,
                    (PcCompatExternalInputDeviceFlags)inputEvent.Flags & known);
            }
            else
            {
                devices = PcCompatExternalInputDeviceRuntime.Snapshot();
            }

            ResetHeld(publishRelease: true, inputEvent.RawNs);
            _sessionDeviceFlags = devices.Flags;
            _inputMode = HasRelevantExternalDevice(devices.Flags)
                ? PcCompatKeyViewerInputMode.External
                : PcCompatKeyViewerInputMode.Touch;
            _sessionModeReason = devices.Available
                ? HasRelevantExternalDevice(devices.Flags)
                    ? "Auto frozen to External from session device snapshot"
                    : "Auto frozen to Touch; no relevant external input device was present"
                : "Auto device snapshot unavailable; frozen to Touch";
            _frozenSessionGeneration = inputEvent.SessionGeneration;
            _sessionModeFrozen = true;
        }

        private bool HasRelevantExternalDevice(PcCompatExternalInputDeviceFlags flags)
            => (flags & _externalDeviceInterest) != 0;

        private static PcCompatExternalInputDeviceFlags ResolveExternalDeviceInterest(
            IEnumerable<PcCompatKeyViewerPublishedIdentity> identities)
        {
            var result = PcCompatExternalInputDeviceFlags.None;
            foreach (var identity in identities)
            {
                result |= identity.Kind switch
                {
                    PcCompatInputIdentityKind.MouseButton =>
                        PcCompatExternalInputDeviceFlags.Mouse,
                    PcCompatInputIdentityKind.ControllerControl =>
                        PcCompatExternalInputDeviceFlags.Controller,
                    PcCompatInputIdentityKind.UnityKeyCode or
                        PcCompatInputIdentityKind.WindowsVirtualKey =>
                        PcCompatExternalInputDeviceFlags.Keyboard,
                    _ => PcCompatExternalInputDeviceFlags.None
                };
            }
            return result == PcCompatExternalInputDeviceFlags.None
                ? PcCompatExternalInputDeviceFlags.Keyboard
                : result;
        }

        public PcCompatKeyViewerPublishedFeature Publish(
            ulong sequence,
            uint sessionGeneration,
            uint producerEpoch,
            long registrationGeneration)
            => new()
            {
                FeatureId = _featureId,
                Qualification = _consumerQualification,
                Active = _consumerQualification != PcCompatKeyViewerConsumerQualification.None,
                Reason = _consumerReason,
                Mode = _inputMode,
                Identities = _consumerIdentities,
                HeldMask = _heldMask,
                DownOrdinals = (ulong[])_downOrdinals.Clone(),
                UpOrdinals = (ulong[])_upOrdinals.Clone(),
                SourceSequence = sequence,
                SessionGeneration = sessionGeneration,
                ProducerEpoch = producerEpoch,
                RegistrationGeneration = registrationGeneration
            };

        private bool ObserveTouch(PcCompatKeyViewerRawEvent inputEvent)
        {
            if (inputEvent.Phase == PcCompatKeyViewerRawPhase.Cancel)
            {
                var released = _heldMask;
                ResetHeld(publishRelease: true, inputEvent.RawNs);
                if (released != 0)
                {
                    ++_transitionCount;
                    _lastTransition = CreateTransition(inputEvent, -1, "TouchLane:cancel");
                }
                return false;
            }
            if (inputEvent.Slot < 0 || inputEvent.Slot >= _touchSlotLanes.Length)
            {
                ++_unmappedEventCount;
                return false;
            }

            if (inputEvent.Phase == PcCompatKeyViewerRawPhase.Down)
            {
                var lane = MapTouchLane(inputEvent, _laneCount, _touchLaneMappingMode);
                if (lane < 0)
                {
                    _ignoredTouchUpSlots |= 1u << inputEvent.Slot;
                    ++_unmappedEventCount;
                    return false;
                }
                _ignoredTouchUpSlots &= ~(1u << inputEvent.Slot);
                var wasHeld = (_heldMask & (1u << lane)) != 0;
                _touchSlotLanes[inputEvent.Slot] = lane;
                ++_touchLaneHeldCounts[lane];
                _heldMask |= 1u << lane;
                if (!wasHeld)
                {
                    ++_downOrdinals[lane];
                    RecordRainDown(lane, inputEvent.RawNs);
                }
                ++_transitionCount;
                _lastTransition = CreateTransition(inputEvent, lane, $"TouchLane:T{lane + 1}");
                return !wasHeld && (_unityConsumerLaneMask & (1u << lane)) != 0;
            }
            if (inputEvent.Phase == PcCompatKeyViewerRawPhase.Up)
            {
                var lane = _touchSlotLanes[inputEvent.Slot];
                if (lane < 0)
                {
                    var slotBit = 1u << inputEvent.Slot;
                    if ((_ignoredTouchUpSlots & slotBit) != 0)
                    {
                        _ignoredTouchUpSlots &= ~slotBit;
                        return false;
                    }
                    ++_unmappedEventCount;
                    return false;
                }
                _touchSlotLanes[inputEvent.Slot] = -1;
                if (_touchLaneHeldCounts[lane] > 0)
                    --_touchLaneHeldCounts[lane];
                if (!IsLaneHeldByTouch(lane))
                {
                    _touchLaneLastUpRawNs[lane] = inputEvent.RawNs;
                    if (_externalLaneHeldCounts[lane] == 0)
                    {
                        _heldMask &= ~(1u << lane);
                        ++_upOrdinals[lane];
                        RecordRainUp(lane, inputEvent.RawNs);
                    }
                }
                ++_transitionCount;
                _lastTransition = CreateTransition(inputEvent, lane, $"TouchLane:T{lane + 1}");
            }
            return false;
        }

        private bool ObserveExternal(PcCompatKeyViewerRawEvent inputEvent)
        {
            if (inputEvent.Phase is not (PcCompatKeyViewerRawPhase.Down or
                PcCompatKeyViewerRawPhase.Up or PcCompatKeyViewerRawPhase.Cancel))
                return false;

            var contact = new ExternalContact(
                inputEvent.Source,
                inputEvent.Code,
                inputEvent.ScanCode,
                inputEvent.DeviceId);
            if (inputEvent.Phase == PcCompatKeyViewerRawPhase.Down &&
                _externalContacts.ContainsKey(contact))
                return false;

            uint matchedLanes = 0;
            string? identityLabel = null;
            foreach (var identity in PcCompatExternalInputIdentityMapper.Map(inputEvent))
            {
                if (!_consumerLaneMasks.TryGetValue(identity, out var lanes))
                    continue;
                matchedLanes |= lanes;
                identityLabel ??= $"{identity.Kind}:{identity.Value}";
            }

            if (inputEvent.Phase == PcCompatKeyViewerRawPhase.Down)
            {
                if (matchedLanes == 0)
                    return RecordUnmappedExternal(inputEvent);
                _externalContacts[contact] = matchedLanes;
                var anyUnityDown = false;
                for (var lane = 0; lane < _laneCount; ++lane)
                {
                    var laneBit = 1u << lane;
                    if ((matchedLanes & laneBit) == 0)
                        continue;
                    var wasHeld = (_heldMask & laneBit) != 0;
                    ++_externalLaneHeldCounts[lane];
                    _heldMask |= laneBit;
                    if (!wasHeld)
                    {
                        ++_downOrdinals[lane];
                        RecordRainDown(lane, inputEvent.RawNs);
                        anyUnityDown |= (_unityConsumerLaneMask & laneBit) != 0;
                    }
                }
                ++_transitionCount;
                _lastTransition = CreateTransition(inputEvent, FirstLane(matchedLanes),
                    identityLabel ?? $"{inputEvent.Source}:{inputEvent.Code}");
                return anyUnityDown;
            }

            if (!_externalContacts.Remove(contact, out matchedLanes))
                return RecordUnmappedExternal(inputEvent);
            for (var lane = 0; lane < _laneCount; ++lane)
            {
                var laneBit = 1u << lane;
                if ((matchedLanes & laneBit) == 0)
                    continue;
                if (_externalLaneHeldCounts[lane] > 0)
                    --_externalLaneHeldCounts[lane];
                if (_externalLaneHeldCounts[lane] == 0 && !IsLaneHeldByTouch(lane))
                {
                    _heldMask &= ~laneBit;
                    ++_upOrdinals[lane];
                    RecordRainUp(lane, inputEvent.RawNs);
                }
            }
            ++_transitionCount;
            _lastTransition = CreateTransition(inputEvent, FirstLane(matchedLanes),
                identityLabel ?? $"{inputEvent.Source}:{inputEvent.Code}");
            return false;
        }

        private void RecordRainDown(int lane, long rawNs)
        {
            if (rawNs <= 0)
                return;
            if (_rainPulses.Count == MaxRainPulses)
                _rainPulses.RemoveRange(0, MaxRainPulses / 4);
            _rainPulses.Add(new PcCompatKeyViewerRainPulse(lane, rawNs, 0));
            _latestRainRawNs = Math.Max(_latestRainRawNs, rawNs);
        }

        private void RecordRainUp(int lane, long rawNs)
        {
            if (rawNs <= 0)
                return;
            for (var index = _rainPulses.Count - 1; index >= 0; --index)
            {
                var pulse = _rainPulses[index];
                if (pulse.Lane != lane || pulse.UpRawNs != 0)
                    continue;
                var upRawNs = Math.Max(rawNs, pulse.DownRawNs);
                _rainPulses[index] = pulse with { UpRawNs = upRawNs };
                _latestRainRawNs = Math.Max(_latestRainRawNs, upRawNs);
                return;
            }
        }

        private bool RecordUnmappedExternal(PcCompatKeyViewerRawEvent inputEvent)
        {
            ++_unmappedEventCount;
            _lastTransition = CreateTransition(
                inputEvent,
                -1,
                $"{inputEvent.Source}:{inputEvent.Code}");
            return false;
        }

        private bool IsLaneHeldByTouch(int lane)
            => _touchLaneHeldCounts[lane] != 0;

        private static int FirstLane(uint lanes)
            => lanes == 0 ? -1 : System.Numerics.BitOperations.TrailingZeroCount(lanes);

        private PcCompatKeyViewerPreviewTransition CreateTransition(
            PcCompatKeyViewerRawEvent inputEvent,
            int lane,
            string identity)
            => new(
                inputEvent.Sequence,
                inputEvent.RawNs,
                _featureId,
                inputEvent.Origin,
                inputEvent.Source,
                inputEvent.Phase,
                inputEvent.Code,
                lane,
                identity);

        private int MapTouchLane(
            PcCompatKeyViewerRawEvent inputEvent,
            int laneCount,
            PcCompatTouchLaneMappingMode mode)
        {
            if (mode == PcCompatTouchLaneMappingMode.TouchContacts)
                return MapTouchContactLane(inputEvent, laneCount);
            if (inputEvent.ViewportWidth <= 0 || !float.IsFinite(inputEvent.X))
                return -1;
            var scaled = inputEvent.X * laneCount / inputEvent.ViewportWidth;
            return Math.Clamp((int)MathF.Floor(scaled), 0, laneCount - 1);
        }

        private int MapTouchContactLane(
            PcCompatKeyViewerRawEvent inputEvent,
            int laneCount)
        {
            var preferredLane = inputEvent.Slot >= 0 && inputEvent.Slot < laneCount
                ? inputEvent.Slot
                : 0;
            var delayNs = checked(
                (long)_touchContactReuseDelayMilliseconds * 1_000_000L);

            for (var offset = 0; offset < laneCount; ++offset)
            {
                var lane = (preferredLane + offset) % laneCount;
                if (IsLaneHeldByTouch(lane) ||
                    IsTouchLaneCoolingDown(lane, inputEvent.RawNs, delayNs))
                    continue;
                return lane;
            }

            var earliestReleasedLane = -1;
            var earliestReleaseRawNs = long.MaxValue;
            for (var offset = 0; offset < laneCount; ++offset)
            {
                var lane = (preferredLane + offset) % laneCount;
                if (IsLaneHeldByTouch(lane))
                    continue;
                var lastUpRawNs = _touchLaneLastUpRawNs[lane];
                if (lastUpRawNs >= earliestReleaseRawNs)
                    continue;
                earliestReleasedLane = lane;
                earliestReleaseRawNs = lastUpRawNs;
            }
            return earliestReleasedLane;
        }

        private bool IsTouchLaneCoolingDown(int lane, long rawNs, long delayNs)
        {
            var lastUpRawNs = _touchLaneLastUpRawNs[lane];
            return delayNs > 0 &&
                lastUpRawNs > 0 &&
                rawNs >= lastUpRawNs &&
                   rawNs - lastUpRawNs < delayNs;
        }

        private int ResolveVirtualPointerSlot(VirtualInputEvent input)
        {
            if (input.PointerId < 0)
                return -1;
            if (_virtualPointerSlots.TryGetValue(input.PointerId, out var existing))
                return existing;
            if (input.Phase != VirtualInputPhase.Down)
                return -1;
            for (var slot = 0; slot < MaxTouchSlots; ++slot)
            {
                if (_virtualPointerSlots.ContainsValue(slot))
                    continue;
                _virtualPointerSlots[input.PointerId] = slot;
                return slot;
            }
            ++_unmappedEventCount;
            return -1;
        }

        private PcCompatKeyViewerPreviewTransition CreateVirtualTransition(
            VirtualInputEvent input,
            int lane,
            string identity)
            => new(
                input.Sequence,
                ToRawNanoseconds(input.OffsetMicroseconds),
                _featureId,
                PcCompatKeyViewerInputOrigin.ReplayVirtual,
                input.Device == VirtualInputDevice.Touch
                    ? PcCompatKeyViewerRawSource.Touch
                    : PcCompatKeyViewerRawSource.Keyboard,
                ToRawPhase(input.Phase),
                PcCompatVirtualInputIdentityMapper.TryMapToAndroidKeyCode(
                    input.CanonicalKey,
                    out var keyCode)
                    ? keyCode
                    : 0,
                lane,
                identity);

        private static PcCompatKeyViewerRawPhase ToRawPhase(VirtualInputPhase phase)
            => phase switch
            {
                VirtualInputPhase.Down => PcCompatKeyViewerRawPhase.Down,
                VirtualInputPhase.Up => PcCompatKeyViewerRawPhase.Up,
                VirtualInputPhase.Cancel => PcCompatKeyViewerRawPhase.Cancel,
                _ => PcCompatKeyViewerRawPhase.Reset
            };

        private static long ToRawNanoseconds(long offsetMicroseconds)
            => offsetMicroseconds <= 0
                ? 1
                : offsetMicroseconds >= long.MaxValue / 1_000L
                    ? long.MaxValue
                    : offsetMicroseconds * 1_000L;

        private static int ToViewportDimension(float value)
            => !float.IsFinite(value) || value <= 0
                ? 0
                : value >= int.MaxValue
                    ? int.MaxValue
                    : (int)MathF.Round(value);

        private readonly record struct ExternalContact(
            PcCompatKeyViewerRawSource Source,
            int Code,
            int ScanCode,
            int DeviceId);

        private static (
            PcCompatKeyViewerConsumerQualification Qualification,
            string? Reason,
            PcCompatKeyViewerPublishedIdentity[] Identities) CompileConsumer(
            string modId,
            PcCompatKeyViewerFeatureAdapter feature,
            PcCompatKeyViewerFeatureOverride featureOverride,
            int laneCount)
        {
            if (featureOverride.InputMode is not (PcCompatKeyViewerInputMode.Auto or
                PcCompatKeyViewerInputMode.Touch or PcCompatKeyViewerInputMode.Hybrid))
            {
                return (
                    PcCompatKeyViewerConsumerQualification.None,
                    $"input mode {featureOverride.InputMode} does not require TouchLane consumption",
                    Array.Empty<PcCompatKeyViewerPublishedIdentity>());
            }
            if (PcCompatKeyViewerLoweredConsumerPlanRegistry.TryGet(
                    modId,
                    feature.Id,
                    featureOverride,
                    out var loweredIdentities))
            {
                return (
                    PcCompatKeyViewerConsumerQualification.VerifiedLoweredBinding,
                    null,
                    loweredIdentities);
            }
            if (!PcCompatKeyViewerAdapterValidator.IsCoreReady(feature))
            {
                return (
                    PcCompatKeyViewerConsumerQualification.None,
                    "core capability closure is not Proven; a verified lowered plan is required",
                    Array.Empty<PcCompatKeyViewerPublishedIdentity>());
            }

            foreach (var group in feature.LaneGroups)
            {
                if (group.Lanes.Count != laneCount)
                    continue;
                var identities = new List<PcCompatKeyViewerPublishedIdentity>();
                var valid = true;
                for (var lane = 0; lane < group.Lanes.Count; ++lane)
                {
                    var binding = group.Lanes[lane].Binding;
                    if (binding.Kind is not (PcCompatLaneBindingKind.DirectIdentity or
                        PcCompatLaneBindingKind.AliasSet) || binding.Identities.Count == 0)
                    {
                        valid = false;
                        break;
                    }
                    foreach (var identity in binding.Identities)
                    {
                        if (identity.Kind is not (PcCompatInputIdentityKind.UnityKeyCode or
                            PcCompatInputIdentityKind.WindowsVirtualKey or
                            PcCompatInputIdentityKind.ActionId) ||
                            !int.TryParse(
                                identity.Value,
                                System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var value))
                        {
                            valid = false;
                            break;
                        }
                        identities.Add(new PcCompatKeyViewerPublishedIdentity(
                            identity.Kind,
                            value,
                            lane));
                    }
                    if (!valid)
                        break;
                }
                if (valid && identities.Count != 0)
                {
                    return (
                        PcCompatKeyViewerConsumerQualification.ProvenAdapter,
                        null,
                        identities.ToArray());
                }
            }

            return (
                PcCompatKeyViewerConsumerQualification.None,
                "no exact static UnityKeyCode/WindowsVirtualKey/ActionId lane group matches the Touch lane count",
                Array.Empty<PcCompatKeyViewerPublishedIdentity>());
        }
    }
}

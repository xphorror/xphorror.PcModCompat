using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace StArray.ModManager.Interop;

/// <summary>
/// Android MOD 联动入口。所有调用都必须发生在 ModManager 为当前 MOD 建立的运行时作用域内。
/// Publisher、Subscription 和 Playback lease 都绑定当前 MOD generation，卸载或热更新时会被
/// Broker 自动退役。
/// </summary>
public static class ModInterop
{
    public static bool IsRuntimeAvailable
    {
        get
        {
            var session = HookHelper.CurrentRuntimeSession;
            var key = HookHelper.CurrentRuntimeKey;
            return session != null && key.IsValid && session.CanRegisterOwnedResource(key);
        }
    }

    public static IReadOnlyList<InteropContractDescriptor> DiscoverPublicContracts()
        => TryGetRuntime(out _, out var key, out _)
            ? ModInteropBroker.DiscoverPublic(key)
            : Array.Empty<InteropContractDescriptor>();

    public static bool TryOpenPublisher(
        InteropContractDeclaration declaration,
        out ModInteropPublisher? publisher,
        out InteropError error)
    {
        publisher = null;
        if (!TryGetRuntime(out var session, out var key, out error))
            return false;
        return ModInteropBroker.TryOpenPublisher(session, key, declaration, out publisher, out error);
    }

    public static bool TrySubscribe(
        InteropSubscriptionRequest request,
        InteropMessageHandler handler,
        out ModInteropSubscription? subscription,
        out InteropError error)
    {
        subscription = null;
        ArgumentNullException.ThrowIfNull(handler);
        if (!TryGetRuntime(out var session, out var key, out error))
            return false;
        return ModInteropBroker.TrySubscribe(
            session,
            key,
            request,
            handler,
            out subscription,
            out error);
    }

    public static bool TryOpenVirtualInputPlayback(
        out VirtualInputPlaybackPublisher? publisher,
        out InteropError error)
    {
        publisher = null;
        if (!TryGetRuntime(out var session, out var key, out error))
            return false;
        return ModInteropBroker.TryOpenVirtualInputPublisher(
            session,
            key,
            out publisher,
            out error);
    }

    public static Task<InteropResponse> RequestAsync(
        InteropRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetRuntime(out var session, out var key, out var error))
            return Task.FromResult(InteropResponse.Failure(error.Code, error.Message));
        return ModInteropBroker.RequestAsync(session, key, request, cancellationToken);
    }

    public static Task<InteropFanOutResponse> RequestFanOutAsync(
        InteropRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetRuntime(out var session, out var key, out var error))
            return Task.FromResult(
                new InteropFanOutResponse(
                    [InteropResponse.Failure(error.Code, error.Message)]));
        return ModInteropBroker.RequestFanOutAsync(session, key, request, cancellationToken);
    }

    private static bool TryGetRuntime(
        out ModRuntimeSession session,
        out ModRuntimeKey key,
        out InteropError error)
    {
        session = HookHelper.CurrentRuntimeSession!;
        key = HookHelper.CurrentRuntimeKey;
        if (session == null || !key.IsValid)
        {
            error = new(InteropErrorCode.RuntimeUnavailable,
                "ModInterop must be called from a MOD runtime callback.");
            return false;
        }
        if (!session.CanRegisterOwnedResource(key))
        {
            error = new(InteropErrorCode.GenerationMismatch,
                "The current MOD runtime generation is retired.");
            return false;
        }
        error = InteropError.None;
        return true;
    }
}

public sealed class ModInteropPublisher : IDisposable
{
    private readonly ModInteropBroker.PublisherState _state;
    private int _disposed;

    internal ModInteropPublisher(ModInteropBroker.PublisherState state)
        => _state = state;

    internal ModInteropBroker.PublisherState State => _state;

    public InteropContractDescriptor Contract => _state.Descriptor;
    public bool IsRetired => Volatile.Read(ref _disposed) != 0 || _state.IsRetired;

    public bool TryPublish(ReadOnlySpan<byte> payload, out InteropError error)
    {
        if (IsRetired)
        {
            error = new(InteropErrorCode.PublisherRetired, "Publisher lease is retired.");
            return false;
        }
        return ModInteropBroker.TryPublish(_state, payload, virtualInput: null, out error);
    }

    public bool TrySetRequestHandler(InteropRequestHandler handler, out InteropError error)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (IsRetired)
        {
            error = new(InteropErrorCode.PublisherRetired, "Publisher lease is retired.");
            return false;
        }
        return ModInteropBroker.TrySetRequestHandler(_state, handler, out error);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            ModInteropBroker.DisposePublisher(_state);
    }
}

public sealed class ModInteropSubscription : IDisposable
{
    private readonly ModInteropBroker.SubscriptionState _state;
    private int _disposed;

    internal ModInteropSubscription(ModInteropBroker.SubscriptionState state)
        => _state = state;

    public string ContractId => _state.ContractId;
    public bool IsRetired => Volatile.Read(ref _disposed) != 0 || _state.IsRetired;
    public bool IsCircuitBroken => _state.IsCircuitBroken;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            ModInteropBroker.DisposeSubscription(_state);
    }
}

public sealed class VirtualInputPlaybackPublisher : IDisposable
{
    private readonly ModInteropPublisher _publisher;
    private int _disposed;

    internal VirtualInputPlaybackPublisher(ModInteropPublisher publisher)
        => _publisher = publisher;

    public InteropContractDescriptor Contract => _publisher.Contract;
    public bool IsRetired => Volatile.Read(ref _disposed) != 0 || _publisher.IsRetired;

    public bool TryStart(out VirtualInputPlaybackSession? session, out InteropError error)
    {
        session = null;
        if (IsRetired)
        {
            error = new(InteropErrorCode.PublisherRetired, "Virtual input publisher is retired.");
            return false;
        }
        return ModInteropBroker.TryStartVirtualInput(
            _publisher,
            out session,
            out error);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _publisher.Dispose();
    }
}

public sealed class VirtualInputPlaybackSession : IDisposable
{
    private readonly ModInteropBroker.VirtualInputState _state;
    private int _disposed;

    internal VirtualInputPlaybackSession(ModInteropBroker.VirtualInputState state)
        => _state = state;

    internal ModInteropBroker.VirtualInputState State => _state;

    public long SessionGeneration => _state.SessionGeneration;
    public bool IsActive => Volatile.Read(ref _disposed) == 0 && _state.IsActive;

    public bool TryPublish(
        IReadOnlyList<VirtualInputEvent> events,
        out InteropError error)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (Volatile.Read(ref _disposed) != 0)
        {
            error = new(InteropErrorCode.SessionNotActive, "Virtual input session is disposed.");
            return false;
        }
        return ModInteropBroker.TryPublishVirtualInput(_state, events, out error);
    }

    public bool TryPublish(
        VirtualInputEvent input,
        out InteropError error)
        => TryPublish([input], out error);

    public void Cancel()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            ModInteropBroker.EndVirtualInput(_state, cancelled: true);
    }

    public void Complete()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            ModInteropBroker.EndVirtualInput(_state, cancelled: false);
    }

    public void Dispose() => Cancel();
}

internal static class ModInteropBroker
{
    internal sealed class PublisherState
    {
        internal readonly object Gate = new();
        internal readonly ModRuntimeSession Session;
        internal readonly ModRuntimeKey Key;
        internal readonly InteropContractDeclaration Declaration;
        internal readonly string ContractId;
        internal readonly string PublisherId;
        internal readonly long PublisherGeneration;
        internal readonly List<SubscriptionState> Subscriptions = new();
        internal readonly CancellationTokenSource LifetimeCancellation = new();
        internal InteropRequestHandler? RequestHandler;
        internal IModRuntimeTerminalCleanupRegistration? TerminalCleanup;
        internal bool Retired;

        internal PublisherState(
            ModRuntimeSession session,
            ModRuntimeKey key,
            InteropContractDeclaration declaration,
            string contractId)
        {
            Session = session;
            Key = key;
            Declaration = SnapshotDeclaration(declaration);
            ContractId = contractId;
            PublisherId = key.ModId;
            PublisherGeneration = key.Generation;
        }

        internal bool IsRetired
        {
            get { lock (Gate) return Retired; }
        }

        internal InteropContractDescriptor Descriptor => new(
            ContractId,
            Declaration.MajorVersion,
            Declaration.MinorVersion,
            Declaration.SchemaId,
            Declaration.ContentType,
            Declaration.Visibility,
            Declaration.DeliveryMode,
            Declaration.DispatchContext,
            PublisherId,
            PublisherGeneration,
            RequestHandler != null);
    }

    internal sealed class SubscriptionState
    {
        internal readonly object Gate = new();
        internal readonly ModRuntimeSession Session;
        internal readonly ModRuntimeKey Key;
        internal readonly string ContractId;
        internal InteropMessageHandler? Handler;
        internal readonly InteropSubscriptionRequest Request;
        internal readonly string ResourceIdentity;
        internal readonly Queue<InteropMessage> Queue = new();
        internal IModRuntimeTerminalCleanupRegistration? TerminalCleanup;
        internal bool Retired;
        internal bool Scheduled;
        internal bool Running;
        internal bool CircuitBroken;
        internal InteropMessage? CircuitCancellation;
        internal bool CircuitCancellationDelivered;
        internal int ConsecutiveFailures;
        internal int QueueHighWatermark;
        internal long QueuedBytes;
        internal int QueuedUnits;
        internal ulong Accepted;
        internal ulong Completed;
        internal ulong Dropped;
        internal ulong Faults;

        internal SubscriptionState(
            ModRuntimeSession session,
            ModRuntimeKey key,
            string contractId,
            InteropSubscriptionRequest request,
            InteropMessageHandler handler)
        {
            Session = session;
            Key = key;
            ContractId = contractId;
            Request = request;
            Handler = handler;
            ResourceIdentity = $"interop:subscription:{contractId}:{Guid.NewGuid():N}";
        }

        internal bool IsRetired
        {
            get { lock (Gate) return Retired; }
        }

        internal bool IsCircuitBroken
        {
            get { lock (Gate) return CircuitBroken; }
        }
    }

    internal sealed class VirtualInputState
    {
        internal readonly PublisherState Publisher;
        internal readonly long SessionGeneration;
        internal readonly Dictionary<string, VirtualInputEvent> HeldKeys = new(StringComparer.Ordinal);
        internal readonly Dictionary<int, VirtualInputEvent> HeldPointers = new();
        internal bool IsActive;
        internal long LastOffsetMicroseconds;

        internal VirtualInputState(PublisherState publisher, long sessionGeneration)
        {
            Publisher = publisher;
            SessionGeneration = sessionGeneration;
            IsActive = true;
        }
    }

    private readonly record struct ContractKey(string Id, int Major);
    private readonly record struct RuntimeRequestKey(
        string LoaderKind,
        string ModId,
        long Generation)
    {
        internal static RuntimeRequestKey From(ModRuntimeKey key)
            => new(key.LoaderKind, key.ModId.ToUpperInvariant(), key.Generation);
    }

    private const int WorkerCount = 2;
    private const int MaxWorkPerTurn = 64;
    private static readonly long MaxTurnTicks = Math.Max(1, Stopwatch.Frequency / 250);
    private static readonly object RegistryGate = new();
    private static readonly Dictionary<ContractKey, List<PublisherState>> Publishers = new();
    private static readonly List<SubscriptionState> AllSubscriptions = new();
    private static readonly ConcurrentQueue<SubscriptionState> ReadySubscriptions = new();
    private static readonly SemaphoreSlim ReadySignal = new(0);
    private static readonly Thread[] Workers = StartWorkers();
    private static long _nextSequence;
    private static long _nextVirtualSessionGeneration;
    private static VirtualInputState? _activeVirtualInput;
    private static int _pendingRequests;
    private static readonly ConcurrentDictionary<RuntimeRequestKey, int> PendingRequestsByCaller = new();
    private static long _queuedVirtualBytes;
    private static long _queuedGenericBytes;
    private static Func<VirtualInputBatch, bool>? _virtualInputHostSink;
    private const long QueueBudgetBytes = 16L * 1024L * 1024L;

    internal static bool TryOpenPublisher(
        ModRuntimeSession session,
        ModRuntimeKey key,
        InteropContractDeclaration declaration,
        out ModInteropPublisher? publisher,
        out InteropError error,
        bool allowVirtualInputContract = false)
    {
        publisher = null;
        if (!TryNormalizeDeclaration(key.ModId, declaration, out var contractId, out error))
            return false;
        if (contractId == ModInteropConstants.VirtualInputPlaybackV2 &&
            !allowVirtualInputContract)
        {
            error = new(
                InteropErrorCode.VisibilityDenied,
                "VirtualInput V2 publishers must use TryOpenVirtualInputPlayback.");
            return false;
        }
        var contractKey = new ContractKey(contractId, declaration.MajorVersion);
        var state = new PublisherState(session, key, declaration, contractId);
        SubscriptionState[] waitingSubscriptions;
        lock (RegistryGate)
        {
            if (!session.CanRegisterOwnedResource(key))
            {
                error = new(InteropErrorCode.GenerationMismatch, "MOD runtime generation is not active.");
                return false;
            }
            if (!Publishers.TryGetValue(contractKey, out var entries))
                Publishers[contractKey] = entries = new List<PublisherState>();
            if (!contractId.StartsWith("starray.", StringComparison.Ordinal) &&
                Publishers.Values.SelectMany(values => values).Count(existing =>
                    !existing.IsRetired && existing.Key.Matches(key) &&
                    !existing.ContractId.StartsWith("starray.", StringComparison.Ordinal)) >= 32)
            {
                error = new(InteropErrorCode.QuotaExceeded,
                    "A publisher generation may own at most 32 custom topics.");
                return false;
            }
            if (entries.Any(existing =>
                    !existing.IsRetired &&
                    string.Equals(existing.PublisherId, state.PublisherId, StringComparison.OrdinalIgnoreCase) &&
                    existing.PublisherGeneration == state.PublisherGeneration))
            {
                error = new(InteropErrorCode.ContractAlreadyRegistered,
                    "The same MOD generation already owns this contract.");
                return false;
            }
            entries.Add(state);
            waitingSubscriptions = AllSubscriptions
                .Where(subscription => SubscriptionMatchesPublisher(subscription, state))
                .ToArray();
            lock (state.Gate)
                state.Subscriptions.AddRange(waitingSubscriptions);
        }

        var identity = $"interop:publisher:{contractId}:{declaration.MajorVersion}:{key.Generation}";
        if (!ModOwnedResourceRegistry.TryRegister(
                key,
                ModOwnedResourceKind.Provider,
                identity))
        {
            DisposePublisher(state);
            error = new(InteropErrorCode.QuotaExceeded, "Could not register the publisher resource.");
            return false;
        }
        if (!session.TryRegisterTerminalCleanup(
                key,
                () => DisposePublisher(state),
                out var terminalCleanup) ||
            terminalCleanup == null)
        {
            DisposePublisher(state);
            error = new(InteropErrorCode.GenerationMismatch, "MOD runtime is retiring.");
            return false;
        }
        state.TerminalCleanup = terminalCleanup;
        publisher = new ModInteropPublisher(state);
        error = InteropError.None;
        return true;
    }

    internal static bool TrySubscribe(
        ModRuntimeSession session,
        ModRuntimeKey key,
        InteropSubscriptionRequest request,
        InteropMessageHandler handler,
        out ModInteropSubscription? subscription,
        out InteropError error)
    {
        subscription = null;
        if (!TryNormalizeTopicForSubscription(request.ContractId, out var contractId, out error))
            return false;
        if (request.MajorVersion < 0 || request.MinimumMinorVersion < 0 ||
            request.MaximumMinorVersion < request.MinimumMinorVersion ||
            request.QueueCapacity <= 0)
        {
            error = new(InteropErrorCode.InvalidArgument, "Invalid subscription version or queue capacity.");
            return false;
        }

        var stateSubscription = new SubscriptionState(session, key, contractId, request, handler);
        lock (RegistryGate)
        {
            if (AllSubscriptions.Count(state =>
                    !state.IsRetired &&
                    string.Equals(state.ContractId, contractId, StringComparison.Ordinal)) >= 32)
            {
                error = new(InteropErrorCode.QuotaExceeded, "Contract subscriber quota exceeded.");
                return false;
            }
            AllSubscriptions.Add(stateSubscription);
        }

        if (!ModOwnedResourceRegistry.TryRegister(
                key,
                ModOwnedResourceKind.InputSubscription,
                stateSubscription.ResourceIdentity))
        {
            DisposeSubscription(stateSubscription);
            error = new(InteropErrorCode.QuotaExceeded, "Could not register the subscription resource.");
            return false;
        }
        if (!session.TryRegisterTerminalCleanup(
                key,
                () => DisposeSubscription(stateSubscription),
                out var terminalCleanup) ||
            terminalCleanup == null)
        {
            DisposeSubscription(stateSubscription);
            error = new(InteropErrorCode.GenerationMismatch, "MOD runtime is retiring.");
            return false;
        }
        stateSubscription.TerminalCleanup = terminalCleanup;
        subscription = new ModInteropSubscription(stateSubscription);

        lock (RegistryGate)
        {
            var publishers = Publishers
                .Where(pair => pair.Key.Id == contractId && pair.Key.Major == request.MajorVersion)
                .SelectMany(pair => pair.Value)
                .Where(state => SubscriptionMatchesPublisher(stateSubscription, state))
                .OrderBy(state => state.PublisherId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(state => state.PublisherGeneration)
                .ToArray();
            foreach (var publisherState in publishers)
            {
                lock (publisherState.Gate)
                {
                    if (publisherState.Retired || stateSubscription.Retired)
                        continue;
                    if (contractId == ModInteropConstants.VirtualInputPlaybackV2 &&
                        _activeVirtualInput is { IsActive: true } virtualInput &&
                        ReferenceEquals(virtualInput.Publisher, publisherState))
                    {
                        Enqueue(stateSubscription, CreateVirtualMessage(
                            publisherState,
                            new VirtualInputBatch(
                                VirtualInputBatchKind.Started,
                                virtualInput.SessionGeneration,
                                Array.Empty<VirtualInputEvent>()),
                            isCancellation: false));
                        Enqueue(stateSubscription, CreateVirtualMessage(
                            publisherState,
                            new VirtualInputBatch(
                                VirtualInputBatchKind.Snapshot,
                                virtualInput.SessionGeneration,
                                CreateSnapshotEvents(virtualInput)),
                            isCancellation: false));
                    }
                    publisherState.Subscriptions.Add(stateSubscription);
                }
            }
        }

        error = InteropError.None;
        return true;
    }

    internal static bool TryOpenVirtualInputPublisher(
        ModRuntimeSession session,
        ModRuntimeKey key,
        out VirtualInputPlaybackPublisher? publisher,
        out InteropError error)
    {
        publisher = null;
        var declaration = new InteropContractDeclaration(
            ModInteropConstants.VirtualInputPlaybackV2,
            majorVersion: 2,
            minorVersion: 0,
            schemaId: "starray.virtual-input.v2",
            contentType: "application/x-starray-virtual-input")
        {
            DeliveryMode = InteropDeliveryMode.OrderedLossless,
            DispatchContext = InteropDispatchContext.SerializedWorker,
            Visibility = InteropVisibility.Public,
            MaxPayloadBytes = ModInteropConstants.MaxCustomPayloadBytes
        };
        if (!TryOpenPublisher(
                session,
                key,
                declaration,
                out var basePublisher,
                out error,
                allowVirtualInputContract: true))
            return false;
        publisher = new VirtualInputPlaybackPublisher(basePublisher!);
        return true;
    }

    internal static bool TryStartVirtualInput(
        ModInteropPublisher publisher,
        out VirtualInputPlaybackSession? session,
        out InteropError error)
    {
        session = null;
        if (publisher.IsRetired)
        {
            error = new(InteropErrorCode.PublisherRetired, "Virtual input publisher is retired.");
            return false;
        }
        VirtualInputBatch normalized;
        lock (RegistryGate)
        {
            if (_activeVirtualInput != null)
            {
                error = new(InteropErrorCode.SessionAlreadyActive,
                    "Only one virtual input playback session may be active.");
                return false;
            }
            var generation = Interlocked.Increment(ref _nextVirtualSessionGeneration);
            var virtualInput = new VirtualInputState(publisher.State, generation);
            _activeVirtualInput = virtualInput;
            session = new VirtualInputPlaybackSession(virtualInput);
            if (!TryEnqueueVirtualBatchLocked(
                    publisher.State,
                    new VirtualInputBatch(VirtualInputBatchKind.Started, generation),
                    out normalized,
                    out error))
            {
                virtualInput.IsActive = false;
                _activeVirtualInput = null;
                session = null;
                return false;
            }
        }
        DeliverVirtualBatchToHost(normalized);
        return true;
    }

    internal static bool TryPublishVirtualInput(
        VirtualInputState state,
        IReadOnlyList<VirtualInputEvent> events,
        out InteropError error)
    {
        if (events.Count == 0 || events.Count > ModInteropConstants.VirtualInputMaxBatch)
        {
            error = new(InteropErrorCode.InvalidArgument,
                $"Virtual input batch must contain 1..{ModInteropConstants.VirtualInputMaxBatch} events.");
            return false;
        }
        var snapshot = events.ToArray();
        VirtualInputBatch normalized;
        lock (RegistryGate)
        {
            if (!ReferenceEquals(_activeVirtualInput, state) || !state.IsActive)
            {
                error = new(InteropErrorCode.SessionNotActive, "Virtual input session is not active.");
                return false;
            }
            var lastOffset = state.LastOffsetMicroseconds;
            foreach (var input in snapshot)
            {
                if (!TryValidateVirtualInput(input, lastOffset, out error))
                    return false;
                lastOffset = input.OffsetMicroseconds;
            }
            foreach (var input in snapshot)
                ApplyState(state, input);
            state.LastOffsetMicroseconds = lastOffset;
            if (!TryEnqueueVirtualBatchLocked(
                    state.Publisher,
                    new VirtualInputBatch(
                        VirtualInputBatchKind.Events,
                        state.SessionGeneration,
                        snapshot),
                    out normalized,
                    out error))
                return false;
        }
        DeliverVirtualBatchToHost(normalized);
        return true;
    }

    internal static void EndVirtualInput(VirtualInputState state, bool cancelled)
    {
        var normalizedBatches = new List<VirtualInputBatch>(2);
        lock (RegistryGate)
        {
            if (!ReferenceEquals(_activeVirtualInput, state) || !state.IsActive)
                return;
            state.IsActive = false;
            _activeVirtualInput = null;
            var cancelEvents = CreateCancelEvents(state);
            if (cancelled || cancelEvents.Count != 0)
            {
                if (TryEnqueueVirtualBatchLocked(
                        state.Publisher,
                        new VirtualInputBatch(
                            VirtualInputBatchKind.Cancelled,
                            state.SessionGeneration,
                            cancelEvents),
                        out var normalizedCancelled,
                        out _))
                    normalizedBatches.Add(normalizedCancelled);
            }
            if (TryEnqueueVirtualBatchLocked(
                    state.Publisher,
                    new VirtualInputBatch(
                        VirtualInputBatchKind.Ended,
                        state.SessionGeneration,
                        Array.Empty<VirtualInputEvent>()),
                    out var normalizedEnded,
                    out _))
                normalizedBatches.Add(normalizedEnded);
            state.HeldKeys.Clear();
            state.HeldPointers.Clear();
        }
        foreach (var batch in normalizedBatches)
            DeliverVirtualBatchToHost(batch);
    }

    internal static bool TryPublish(
        PublisherState publisher,
        ReadOnlySpan<byte> payload,
        VirtualInputBatch? virtualInput,
        out InteropError error)
    {
        if (publisher.IsRetired)
        {
            error = new(InteropErrorCode.PublisherRetired, "Publisher lease is retired.");
            return false;
        }
        if (virtualInput == null && payload.Length > publisher.Declaration.MaxPayloadBytes)
        {
            error = new(InteropErrorCode.QuotaExceeded, "Payload exceeds the contract limit.");
            return false;
        }
        var message = CreateMessage(
            publisher,
            payload.ToArray(),
            virtualInput,
            isCancellation: false);
        Dispatch(publisher, message);
        error = InteropError.None;
        return true;
    }

    internal static bool TrySetRequestHandler(
        PublisherState publisher,
        InteropRequestHandler handler,
        out InteropError error)
    {
        lock (publisher.Gate)
        {
            if (publisher.Retired)
            {
                error = new(InteropErrorCode.PublisherRetired, "Publisher lease is retired.");
                return false;
            }
            if (publisher.RequestHandler != null)
            {
                error = new(InteropErrorCode.ContractAlreadyRegistered,
                    "A request handler is already registered for this provider.");
                return false;
            }
            publisher.RequestHandler = handler;
        }
        error = InteropError.None;
        return true;
    }

    internal static Task<InteropResponse> RequestAsync(
        ModRuntimeSession callerSession,
        ModRuntimeKey callerKey,
        InteropRequest request,
        CancellationToken cancellationToken)
        => RequestCoreAsync(callerSession, callerKey, request, cancellationToken);

    internal static Task<InteropFanOutResponse> RequestFanOutAsync(
        ModRuntimeSession callerSession,
        ModRuntimeKey callerKey,
        InteropRequest request,
        CancellationToken cancellationToken)
        => RequestFanOutCoreAsync(callerSession, callerKey, request, cancellationToken);

    internal static IReadOnlyList<InteropContractDescriptor> DiscoverPublic(ModRuntimeKey requester)
    {
        lock (RegistryGate)
        {
            return Publishers.Values
                .SelectMany(entries => entries)
                .Where(state => !state.IsRetired && state.Declaration.Visibility == InteropVisibility.Public)
                .OrderBy(state => state.ContractId, StringComparer.Ordinal)
                .ThenBy(state => state.PublisherId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(state => state.PublisherGeneration)
                .Select(state => state.Descriptor)
                .ToArray();
        }
    }

    internal static void RetireRuntime(ModRuntimeKey key)
    {
        PublisherState[] publishers;
        SubscriptionState[] subscriptions;
        lock (RegistryGate)
        {
            publishers = Publishers.Values
                .SelectMany(entries => entries)
                .Where(state => state.Key.Matches(key))
                .ToArray();
            subscriptions = AllSubscriptions.Where(state => state.Key.Matches(key)).ToArray();
        }
        foreach (var subscription in subscriptions)
            DisposeSubscription(subscription);
        foreach (var publisher in publishers)
            DisposePublisher(publisher);
    }

    internal static void RegisterVirtualInputHostSink(Func<VirtualInputBatch, bool>? sink)
    {
        Volatile.Write(ref _virtualInputHostSink, sink);
        if (sink == null)
            return;
        VirtualInputBatch? started = null;
        VirtualInputBatch? snapshot = null;
        lock (RegistryGate)
        {
            if (_activeVirtualInput is not { IsActive: true } active)
                return;
            started = new VirtualInputBatch(
                VirtualInputBatchKind.Started,
                active.SessionGeneration);
            snapshot = RewriteVirtualBatchSequences(new VirtualInputBatch(
                VirtualInputBatchKind.Snapshot,
                active.SessionGeneration,
                CreateSnapshotEvents(active)));
        }
        if (started == null || snapshot == null)
            return;
        try
        {
            sink(started);
            sink(snapshot);
        }
        catch (Exception exception)
        {
            SafeLog("ModInterop", "virtual input host sink synchronization failed: " + exception.Message);
        }
    }

    internal static void DisposePublisher(PublisherState publisher)
    {
        SubscriptionState[] subscribers;
        IModRuntimeTerminalCleanupRegistration? terminalCleanup;
        var terminalBatches = new List<VirtualInputBatch>(2);
        lock (RegistryGate)
        {
            if (_activeVirtualInput is { } active && ReferenceEquals(active.Publisher, publisher))
            {
                active.IsActive = false;
                _activeVirtualInput = null;
                if (TryEnqueueVirtualBatchLocked(
                        publisher,
                        new VirtualInputBatch(
                            VirtualInputBatchKind.Cancelled,
                            active.SessionGeneration,
                            CreateCancelEvents(active)),
                        out var normalizedCancelled,
                        out _))
                    terminalBatches.Add(normalizedCancelled);
                if (TryEnqueueVirtualBatchLocked(
                        publisher,
                        new VirtualInputBatch(
                            VirtualInputBatchKind.Ended,
                            active.SessionGeneration),
                        out var normalizedEnded,
                        out _))
                    terminalBatches.Add(normalizedEnded);
                active.HeldKeys.Clear();
                active.HeldPointers.Clear();
            }
            lock (publisher.Gate)
            {
                if (publisher.Retired)
                    return;
                publisher.Retired = true;
                publisher.RequestHandler = null;
                terminalCleanup = publisher.TerminalCleanup;
                publisher.TerminalCleanup = null;
                subscribers = publisher.Subscriptions.ToArray();
                publisher.Subscriptions.Clear();
            }
            var contractKey = new ContractKey(
                publisher.ContractId,
                publisher.Declaration.MajorVersion);
            if (Publishers.TryGetValue(contractKey, out var entries))
            {
                entries.RemoveAll(candidate => ReferenceEquals(candidate, publisher));
                if (entries.Count == 0)
                    Publishers.Remove(contractKey);
            }
        }
        foreach (var batch in terminalBatches)
            DeliverVirtualBatchToHost(batch);
        terminalCleanup?.Dispose();
        try
        {
            var pendingCancellation = publisher.LifetimeCancellation.CancelAsync();
            if (!pendingCancellation.IsCompletedSuccessfully)
                _ = ObserveCancellationAsync(pendingCancellation);
        }
        catch (ObjectDisposedException)
        {
        }
        foreach (var subscriber in subscribers)
        {
            RemoveQueuedMessages(subscriber, message =>
                string.Equals(message.PublisherId, publisher.PublisherId, StringComparison.OrdinalIgnoreCase) &&
                message.PublisherGeneration == publisher.PublisherGeneration &&
                message.VirtualInput?.IsTerminal != true);
        }
        ModOwnedResourceRegistry.RetireMatching(
            publisher.Key,
            ModOwnedResourceKind.Provider,
            $"interop:publisher:{publisher.ContractId}:{publisher.Declaration.MajorVersion}:{publisher.Key.Generation}");
    }

    internal static void DisposeSubscription(SubscriptionState subscription)
    {
        IModRuntimeTerminalCleanupRegistration? terminalCleanup;
        lock (RegistryGate)
        {
            lock (subscription.Gate)
            {
                if (subscription.Retired)
                    return;
                subscription.Retired = true;
                ReleaseQueuedBytes(subscription, subscription.QueuedBytes);
                subscription.QueuedBytes = 0;
                subscription.QueuedUnits = 0;
                subscription.Queue.Clear();
                subscription.CircuitCancellation = null;
                subscription.Handler = null;
                terminalCleanup = subscription.TerminalCleanup;
                subscription.TerminalCleanup = null;
            }
            AllSubscriptions.Remove(subscription);
            foreach (var publisher in Publishers.Values.SelectMany(entries => entries))
            {
                lock (publisher.Gate)
                    publisher.Subscriptions.RemoveAll(candidate => ReferenceEquals(candidate, subscription));
            }
        }
        terminalCleanup?.Dispose();
        ModOwnedResourceRegistry.RetireExact(
            subscription.Key,
            ModOwnedResourceKind.InputSubscription,
            subscription.ResourceIdentity);
    }

    private static bool TryEnqueueVirtualBatchLocked(
        PublisherState publisher,
        VirtualInputBatch batch,
        out VirtualInputBatch normalized,
        out InteropError error)
    {
        normalized = null!;
        lock (publisher.Gate)
        {
            if (publisher.Retired)
            {
                error = new(InteropErrorCode.PublisherRetired, "Publisher lease is retired.");
                return false;
            }
            normalized = RewriteVirtualBatchSequences(batch);
            var message = CreateMessage(
                publisher,
                Array.Empty<byte>(),
                normalized,
                isCancellation: false);
            foreach (var subscription in publisher.Subscriptions.ToArray())
            {
                if (MatchesPublisherTarget(publisher, subscription.Request))
                    Enqueue(subscription, message);
            }
        }
        error = InteropError.None;
        return true;
    }

    private static void DeliverVirtualBatchToHost(VirtualInputBatch normalized)
    {
        try
        {
            var sink = Volatile.Read(ref _virtualInputHostSink);
            if (sink != null && !sink(normalized))
                SafeLog("ModInterop", "virtual input host sink rejected a batch");
        }
        catch (Exception exception)
        {
            SafeLog("ModInterop", "virtual input host sink failed: " + exception.Message);
        }
    }

    private static VirtualInputBatch RewriteVirtualBatchSequences(VirtualInputBatch batch)
    {
        var events = batch.Events;
        var rewritten = events.Count == 0
            ? Array.Empty<VirtualInputEvent>()
            : new VirtualInputEvent[events.Count];
        for (var index = 0; index < events.Count; ++index)
            rewritten[index] = events[index] with { Sequence = NextSequence() };
        return new VirtualInputBatch(batch.Kind, batch.SessionGeneration, rewritten);
    }

    private static InteropMessage CreateVirtualMessage(
        PublisherState publisher,
        VirtualInputBatch batch,
        bool isCancellation)
        => CreateMessage(publisher, Array.Empty<byte>(), batch, isCancellation);

    private static InteropMessage CreateMessage(
        PublisherState publisher,
        byte[] payload,
        VirtualInputBatch? virtualInput,
        bool isCancellation)
        => new(
            publisher.Descriptor,
            publisher.PublisherId,
            publisher.PublisherGeneration,
            NextSequence(),
            payload,
            virtualInput,
            isCancellation);

    private static InteropMessage CreateCircuitCancellation(InteropMessage source)
    {
        VirtualInputBatch? virtualInput = null;
        if (source.VirtualInput is { } batch)
        {
            var events = batch.Events
                .Select(input => input with
                {
                    Sequence = NextSequence(),
                    Phase = VirtualInputPhase.Cancel
                })
                .ToArray();
            virtualInput = new VirtualInputBatch(
                VirtualInputBatchKind.Cancelled,
                batch.SessionGeneration,
                events);
        }
        return new InteropMessage(
            source.Contract,
            source.PublisherId,
            source.PublisherGeneration,
            NextSequence(),
            Array.Empty<byte>(),
            virtualInput,
            isCancellation: true);
    }

    private static void Dispatch(PublisherState publisher, InteropMessage message)
    {
        SubscriptionState[] subscriptions;
        lock (publisher.Gate)
            subscriptions = publisher.Subscriptions.ToArray();
        foreach (var subscription in subscriptions)
        {
            if (!MatchesPublisherTarget(publisher, subscription.Request))
                continue;
            Enqueue(subscription, message);
        }
    }

    private static void Enqueue(SubscriptionState subscription, InteropMessage message)
    {
        var schedule = false;
        var trip = false;
        var reserved = false;
        var estimatedBytes = EstimateMessageBytes(message);
        var queueUnits = EstimateQueueUnits(message);
        lock (subscription.Gate)
        {
            if (subscription.Retired || subscription.CircuitBroken)
                return;
            var capacity = Math.Min(
                    Math.Max(1, subscription.Request.QueueCapacity),
                    message.Contract.ContractId == ModInteropConstants.VirtualInputPlaybackV2
                        ? ModInteropConstants.VirtualInputQueueCapacity
                        : 128);
            if (subscription.QueuedUnits > capacity - queueUnits)
            {
                switch (message.Contract.DeliveryMode)
                {
                    case InteropDeliveryMode.LatestState:
                        subscription.Dropped += (ulong)subscription.Queue.Count;
                        ReleaseQueuedBytes(subscription, subscription.QueuedBytes);
                        subscription.QueuedBytes = 0;
                        subscription.QueuedUnits = 0;
                        subscription.Queue.Clear();
                        break;
                    case InteropDeliveryMode.BestEffort:
                        subscription.Dropped++;
                        return;
                    default:
                        trip = true;
                        break;
                }
            }
            if (!trip)
            {
                reserved = TryReserveQueuedBytes(subscription, estimatedBytes);
                if (!reserved)
                {
                    if (message.Contract.DeliveryMode == InteropDeliveryMode.OrderedLossless)
                        trip = true;
                    else
                        subscription.Dropped++;
                }
            }
            if (!trip && reserved)
            {
                subscription.Queue.Enqueue(message);
                subscription.QueuedBytes += estimatedBytes;
                subscription.QueuedUnits += queueUnits;
                subscription.QueueHighWatermark = Math.Max(
                    subscription.QueueHighWatermark,
                    subscription.QueuedUnits);
                subscription.Accepted++;
                if (!subscription.Scheduled && !subscription.Running)
                {
                    subscription.Scheduled = true;
                    schedule = true;
                }
            }
        }
        if (trip)
        {
            BreakCircuit(subscription, "subscriber queue overflow", message);
            return;
        }
        if (schedule)
            Schedule(subscription);
    }

    private static void BreakCircuit(
        SubscriptionState subscription,
        string reason,
        InteropMessage? sourceMessage = null)
    {
        var shouldLog = false;
        var scheduleCancellation = false;
        lock (subscription.Gate)
        {
            if (subscription.Retired || subscription.CircuitBroken)
                return;
            subscription.CircuitBroken = true;
            ReleaseQueuedBytes(subscription, subscription.QueuedBytes);
            subscription.QueuedBytes = 0;
            subscription.QueuedUnits = 0;
            subscription.Queue.Clear();
            if (sourceMessage != null && subscription.Handler != null)
            {
                subscription.CircuitCancellation = CreateCircuitCancellation(sourceMessage);
                if (!subscription.Scheduled && !subscription.Running)
                {
                    subscription.Scheduled = true;
                    scheduleCancellation = true;
                }
            }
            subscription.Faults++;
            shouldLog = true;
        }
        if (scheduleCancellation)
            Schedule(subscription);
        if (shouldLog)
        {
            SafeLog(
                "ModInterop",
                $"subscriber circuit broken contract={subscription.ContractId} " +
                $"owner={subscription.Key.OwnerId} generation={subscription.Key.Generation} reason={reason}");
        }
    }

    private static Thread[] StartWorkers()
    {
        var workers = new Thread[WorkerCount];
        for (var index = 0; index < workers.Length; ++index)
        {
            workers[index] = new Thread(WorkerMain)
            {
                IsBackground = true,
                Name = $"StArray.ModInterop.{index}"
            };
            workers[index].Start();
        }
        return workers;
    }

    private static void WorkerMain()
    {
        while (true)
        {
            ReadySignal.Wait();
            if (ReadySubscriptions.TryDequeue(out var subscription))
                ExecuteTurn(subscription);
        }
    }

    private static void Schedule(SubscriptionState subscription)
    {
        ReadySubscriptions.Enqueue(subscription);
        ReadySignal.Release();
    }

    private static void ExecuteTurn(SubscriptionState subscription)
    {
        InteropMessage? circuitCancellation = null;
        lock (subscription.Gate)
        {
            if (subscription.Retired)
            {
                subscription.Scheduled = false;
                return;
            }
            subscription.Scheduled = false;
            subscription.Running = true;
            if (subscription.CircuitBroken)
            {
                if (!subscription.CircuitCancellationDelivered &&
                    subscription.CircuitCancellation != null)
                {
                    circuitCancellation = subscription.CircuitCancellation;
                    subscription.CircuitCancellation = null;
                    subscription.CircuitCancellationDelivered = true;
                }
                else
                {
                    subscription.Running = false;
                    return;
                }
            }
        }

        if (circuitCancellation != null)
        {
            if (subscription.Request.DispatchContext == InteropDispatchContext.UnityMainBatched)
                ExecuteUnityMainMessages(subscription, [circuitCancellation], circuitControl: true);
            else
            {
                InvokeSubscriptionMessage(subscription, circuitCancellation, circuitControl: true);
                FinishTurn(subscription);
            }
            return;
        }

        if (subscription.Request.DispatchContext == InteropDispatchContext.UnityMainBatched)
        {
            ExecuteUnityMainTurn(subscription);
            return;
        }

        var processed = 0;
        var started = Stopwatch.GetTimestamp();
        while (processed++ < MaxWorkPerTurn && Stopwatch.GetTimestamp() - started < MaxTurnTicks)
        {
            InteropMessage? message;
            lock (subscription.Gate)
            {
                if (subscription.Retired || subscription.CircuitBroken || subscription.Queue.Count == 0)
                    break;
                message = subscription.Queue.Dequeue();
                var released = EstimateMessageBytes(message);
                subscription.QueuedBytes = Math.Max(0, subscription.QueuedBytes - released);
                subscription.QueuedUnits = Math.Max(
                    0,
                    subscription.QueuedUnits - EstimateQueueUnits(message));
                ReleaseQueuedBytes(subscription, released);
            }
            if (!InvokeSubscriptionMessage(subscription, message))
                break;
        }
        FinishTurn(subscription);
    }

    private static void ExecuteUnityMainTurn(SubscriptionState subscription)
    {
        var messages = new List<InteropMessage>(MaxWorkPerTurn);
        lock (subscription.Gate)
        {
            while (!subscription.Retired && !subscription.CircuitBroken &&
                   messages.Count < MaxWorkPerTurn && subscription.Queue.Count != 0)
            {
                var message = subscription.Queue.Dequeue();
                var released = EstimateMessageBytes(message);
                subscription.QueuedBytes = Math.Max(0, subscription.QueuedBytes - released);
                subscription.QueuedUnits = Math.Max(
                    0,
                    subscription.QueuedUnits - EstimateQueueUnits(message));
                ReleaseQueuedBytes(subscription, released);
                messages.Add(message);
            }
        }
        if (messages.Count == 0)
        {
            FinishTurn(subscription);
            return;
        }
        ExecuteUnityMainMessages(subscription, messages, circuitControl: false);
    }

    private static void ExecuteUnityMainMessages(
        SubscriptionState subscription,
        IReadOnlyList<InteropMessage> messages,
        bool circuitControl)
    {
        bool scheduled;
        try
        {
            scheduled = Xphorror.PcModCompat.PcCompatRuntime.TryScheduleInteropOnUnityMain(() =>
            {
                try
                {
                    foreach (var message in messages)
                    {
                        if (!InvokeSubscriptionMessage(subscription, message, circuitControl))
                            break;
                    }
                }
                finally
                {
                    FinishTurn(subscription);
                }
            });
        }
        catch (Exception exception)
        {
            scheduled = false;
            if (!circuitControl)
                RegisterFailure(subscription, exception, messages.LastOrDefault());
        }
        if (scheduled)
            return;
        if (!circuitControl)
        {
            BreakCircuit(
                subscription,
                "UnityMain dispatcher is unavailable or full",
                messages.LastOrDefault());
        }
        FinishTurn(subscription);
    }

    private static bool InvokeSubscriptionMessage(
        SubscriptionState subscription,
        InteropMessage message,
        bool circuitControl = false)
    {
        try
        {
            if (!subscription.Session.TryEnterCallbackFast(subscription.Key))
            {
                if (!circuitControl)
                    BreakCircuit(subscription, "subscriber generation retired", message);
                return false;
            }
            try
            {
                InteropMessageHandler? handler;
                lock (subscription.Gate)
                    handler = subscription.Handler;
                if (handler == null)
                    return false;
                using (HookHelper.EnterOwnerScope(
                           subscription.Key.OwnerId,
                           subscription.Session,
                           subscription.Key))
                    handler(message);
            }
            finally
            {
                subscription.Session.ExitCallbackFast(subscription.Key);
            }
            lock (subscription.Gate)
            {
                subscription.Completed++;
                subscription.ConsecutiveFailures = 0;
            }
            return true;
        }
        catch (Exception exception)
        {
            if (circuitControl)
            {
                lock (subscription.Gate)
                    subscription.Faults++;
                SafeLog(
                    "ModInterop",
                    $"subscriber cancellation callback failed contract={subscription.ContractId} " +
                    $"owner={subscription.Key.OwnerId}: {exception.Message}");
                return false;
            }
            RegisterFailure(subscription, exception, message);
            return !subscription.IsCircuitBroken;
        }
    }

    private static void FinishTurn(SubscriptionState subscription)
    {
        var reschedule = false;
        lock (subscription.Gate)
        {
            subscription.Running = false;
            var hasCircuitCancellation = subscription.CircuitBroken &&
                                         !subscription.CircuitCancellationDelivered &&
                                         subscription.CircuitCancellation != null;
            var hasMessages = !subscription.CircuitBroken && subscription.Queue.Count != 0;
            if (!subscription.Retired &&
                (hasCircuitCancellation || hasMessages) &&
                !subscription.Scheduled)
            {
                subscription.Scheduled = true;
                reschedule = true;
            }
        }
        if (reschedule)
            Schedule(subscription);
    }

    private static void RegisterFailure(
        SubscriptionState subscription,
        Exception exception,
        InteropMessage? sourceMessage)
    {
        var failures = 0;
        lock (subscription.Gate)
        {
            subscription.Faults++;
            failures = ++subscription.ConsecutiveFailures;
        }
        if (failures >= 3)
        {
            BreakCircuit(
                subscription,
                $"callback failed three consecutive times: {exception.Message}",
                sourceMessage);
        }
    }

    private static async Task<InteropResponse> RequestCoreAsync(
        ModRuntimeSession callerSession,
        ModRuntimeKey callerKey,
        InteropRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequest(request, out var error))
            return InteropResponse.Failure(error.Code, error.Message);
        if (request.Selection == InteropProviderSelection.FanOut)
        {
            return InteropResponse.Failure(
                InteropErrorCode.InvalidArgument,
                "FanOut requests must use RequestFanOutAsync.");
        }
        if (!callerSession.TryBeginOwnedOperation(
                callerKey,
                $"interop-rpc:{request.CorrelationId:N}",
                out var callerOperation) ||
            callerOperation == null)
        {
            return InteropResponse.Failure(
                InteropErrorCode.GenerationMismatch,
                "Caller generation is retiring.");
        }
        using (callerOperation)
        using (var callerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken,
                   callerOperation.CancellationToken))
        {
            return await RequestCoreWithQuotaAsync(
                    callerSession,
                    callerKey,
                    request,
                    callerCancellation.Token)
                .ConfigureAwait(false);
        }
    }

    private static async Task<InteropResponse> RequestCoreWithQuotaAsync(
        ModRuntimeSession callerSession,
        ModRuntimeKey callerKey,
        InteropRequest request,
        CancellationToken cancellationToken)
    {
        var callerRequestKey = RuntimeRequestKey.From(callerKey);
        if (PendingRequestsByCaller.AddOrUpdate(
                callerRequestKey,
                1,
                static (_, count) => count + 1) > 16)
        {
            ReleaseRequestSlot(callerRequestKey);
            return InteropResponse.Failure(
                InteropErrorCode.RequestLimitExceeded,
                "Per-MOD pending request quota exceeded.");
        }
        if (Interlocked.Increment(ref _pendingRequests) > 128)
        {
            Interlocked.Decrement(ref _pendingRequests);
            ReleaseRequestSlot(callerRequestKey);
            return InteropResponse.Failure(
                InteropErrorCode.RequestLimitExceeded,
                "Global pending request quota exceeded.");
        }
        try
        {
            var provider = FindProviders(request, callerSession, callerKey).FirstOrDefault();
            if (provider == null)
                return InteropResponse.Failure(InteropErrorCode.ProviderNotFound, "No compatible provider found.");
            return await InvokeProvider(provider, request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingRequests);
            ReleaseRequestSlot(callerRequestKey);
        }
    }

    private static async Task<InteropFanOutResponse> RequestFanOutCoreAsync(
        ModRuntimeSession callerSession,
        ModRuntimeKey callerKey,
        InteropRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequest(request, out var error))
            return new([InteropResponse.Failure(error.Code, error.Message)]);
        if (!callerSession.TryBeginOwnedOperation(
                callerKey,
                $"interop-rpc:{request.CorrelationId:N}",
                out var callerOperation) ||
            callerOperation == null)
        {
            return new([InteropResponse.Failure(
                InteropErrorCode.GenerationMismatch,
                "Caller generation is retiring.")]);
        }
        using (callerOperation)
        using (var callerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken,
                   callerOperation.CancellationToken))
        {
            return await RequestFanOutWithQuotaAsync(
                    callerSession,
                    callerKey,
                    request,
                    callerCancellation.Token)
                .ConfigureAwait(false);
        }
    }

    private static async Task<InteropFanOutResponse> RequestFanOutWithQuotaAsync(
        ModRuntimeSession callerSession,
        ModRuntimeKey callerKey,
        InteropRequest request,
        CancellationToken cancellationToken)
    {
        var callerRequestKey = RuntimeRequestKey.From(callerKey);
        if (PendingRequestsByCaller.AddOrUpdate(
                callerRequestKey,
                1,
                static (_, count) => count + 1) > 16)
        {
            ReleaseRequestSlot(callerRequestKey);
            return new([InteropResponse.Failure(
                InteropErrorCode.RequestLimitExceeded,
                "Per-MOD pending request quota exceeded.")]);
        }
        if (Interlocked.Increment(ref _pendingRequests) > 128)
        {
            Interlocked.Decrement(ref _pendingRequests);
            ReleaseRequestSlot(callerRequestKey);
            return new([InteropResponse.Failure(
                InteropErrorCode.RequestLimitExceeded,
                "Global pending request quota exceeded.")]);
        }
        var providers = FindProviders(request, callerSession, callerKey, forceFanOut: true);
        if (providers.Count == 0)
        {
            Interlocked.Decrement(ref _pendingRequests);
            ReleaseRequestSlot(callerRequestKey);
            return new([InteropResponse.Failure(InteropErrorCode.ProviderNotFound, "No compatible provider found.")]);
        }
        try
        {
            var tasks = providers.Select(provider => InvokeProvider(provider, request, cancellationToken)).ToArray();
            return new(await Task.WhenAll(tasks).ConfigureAwait(false));
        }
        finally
        {
            Interlocked.Decrement(ref _pendingRequests);
            ReleaseRequestSlot(callerRequestKey);
        }
    }

    private static async Task<InteropResponse> InvokeProvider(
        PublisherState provider,
        InteropRequest request,
        CancellationToken cancellationToken)
    {
        InteropRequestHandler? handler;
        CancellationToken providerLifetime;
        lock (provider.Gate)
        {
            handler = provider.Retired ? null : provider.RequestHandler;
            providerLifetime = provider.LifetimeCancellation.Token;
        }
        if (handler == null)
            return InteropResponse.Failure(InteropErrorCode.HandlerUnavailable, "Provider has no request handler.");
        using var timeout = new CancellationTokenSource(
            NormalizeTimeout(request.Timeout));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token,
            cancellationToken,
            providerLifetime);
        try
        {
            if (!provider.Session.TryEnterCallback(provider.Key, out var lease) || lease == null)
                return InteropResponse.Failure(InteropErrorCode.ProviderRetired, "Provider generation retired.");
            try
            {
                using (lease)
                using (HookHelper.EnterOwnerScope(
                           provider.Key.OwnerId,
                           provider.Session,
                           provider.Key))
                {
                    var task = handler(new InteropRequest(
                        request.ContractId,
                        request.MajorVersion,
                        request.Payload.Span,
                        request.Timeout,
                        request.Selection)
                    {
                        CorrelationId = request.CorrelationId,
                        TargetPublisherId = request.TargetPublisherId,
                        TargetPublisherGeneration = request.TargetPublisherGeneration,
                        CancellationToken = linked.Token
                    }).AsTask();
                    var response = await task.WaitAsync(linked.Token).ConfigureAwait(false);
                    if (response == null)
                    {
                        return InteropResponse.Failure(
                            InteropErrorCode.InternalFailure,
                            "Provider returned no response.");
                    }
                    if (response.Payload.Length > Math.Min(
                            provider.Declaration.MaxPayloadBytes,
                            ModInteropConstants.MaxCustomPayloadBytes))
                    {
                        return InteropResponse.Failure(
                            InteropErrorCode.QuotaExceeded,
                            "Provider response exceeds the contract payload limit.");
                    }
                    return response;
                }
            }
            finally
            {
                // The callback lease is disposed by the using above.
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return InteropResponse.Failure(
                providerLifetime.IsCancellationRequested
                    ? InteropErrorCode.ProviderRetired
                    : cancellationToken.IsCancellationRequested
                        ? InteropErrorCode.Cancelled
                        : InteropErrorCode.RequestTimeout,
                "Provider request was cancelled or timed out.");
        }
        catch (Exception exception)
        {
            return InteropResponse.Failure(InteropErrorCode.InternalFailure, exception.Message);
        }
    }

    private static IReadOnlyList<PublisherState> FindProviders(
        InteropRequest request,
        ModRuntimeSession requesterSession,
        ModRuntimeKey requester,
        bool forceFanOut = false)
    {
        lock (RegistryGate)
        {
            var candidates = Publishers
                .Where(pair => pair.Key.Id == request.ContractId && pair.Key.Major == request.MajorVersion)
                .SelectMany(pair => pair.Value)
                .Where(provider => !provider.IsRetired &&
                                   IsVisible(provider, requesterSession, requester) &&
                                   MatchesPublisherTarget(provider, request.TargetPublisherId,
                                       request.TargetPublisherGeneration))
                .OrderBy(provider => provider.PublisherId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(provider => provider.PublisherGeneration)
                .ToList();
            return !forceFanOut && request.Selection == InteropProviderSelection.Single
                ? candidates.Take(1).ToArray()
                : candidates;
        }
    }

    private static bool TryValidateRequest(InteropRequest request, out InteropError error)
    {
        if (request == null || request.CorrelationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.ContractId) || request.MajorVersion < 0 ||
            request.Payload.Length > ModInteropConstants.MaxCustomPayloadBytes)
        {
            error = new(InteropErrorCode.InvalidArgument, "Invalid request or payload size.");
            return false;
        }
        if (request.Timeout < TimeSpan.Zero || request.Timeout > TimeSpan.FromSeconds(30))
        {
            error = new(InteropErrorCode.InvalidArgument, "Request timeout must be between 0ms and 30s.");
            return false;
        }
        if (request.Selection == InteropProviderSelection.Targeted &&
            string.IsNullOrWhiteSpace(request.TargetPublisherId))
        {
            error = new(InteropErrorCode.InvalidArgument,
                "Targeted requests must specify a publisher ID.");
            return false;
        }
        if (request.TargetPublisherGeneration is <= 0)
        {
            error = new(InteropErrorCode.InvalidArgument,
                "Target publisher generation must be positive.");
            return false;
        }
        error = InteropError.None;
        return true;
    }

    private static async Task ObserveCancellationAsync(Task cancellation)
    {
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static TimeSpan NormalizeTimeout(TimeSpan timeout)
        => timeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(5)
            : timeout > TimeSpan.FromSeconds(30)
                ? TimeSpan.FromSeconds(30)
                : timeout;

    private static void ReleaseRequestSlot(RuntimeRequestKey key)
    {
        while (PendingRequestsByCaller.TryGetValue(key, out var count))
        {
            if (count <= 1)
            {
                if (PendingRequestsByCaller.TryRemove(
                        new KeyValuePair<RuntimeRequestKey, int>(key, count)))
                    return;
                continue;
            }
            if (PendingRequestsByCaller.TryUpdate(key, count - 1, count))
                return;
        }
    }

    private static bool MatchesPublisherTarget(PublisherState publisher, InteropSubscriptionRequest request)
        => MatchesPublisherTarget(publisher, request.TargetPublisherId, request.TargetPublisherGeneration);

    private static bool SubscriptionMatchesPublisher(
        SubscriptionState subscription,
        PublisherState publisher)
        => !subscription.IsRetired &&
           string.Equals(subscription.ContractId, publisher.ContractId, StringComparison.Ordinal) &&
           subscription.Request.MajorVersion == publisher.Declaration.MajorVersion &&
           publisher.Declaration.MinorVersion >= subscription.Request.MinimumMinorVersion &&
           publisher.Declaration.MinorVersion <= subscription.Request.MaximumMinorVersion &&
           MatchesPublisherTarget(publisher, subscription.Request) &&
           IsVisible(publisher, subscription.Session, subscription.Key);

    private static bool MatchesPublisherTarget(
        PublisherState publisher,
        string? targetPublisherId,
        long? targetPublisherGeneration)
        => (string.IsNullOrWhiteSpace(targetPublisherId) ||
            string.Equals(targetPublisherId, publisher.PublisherId, StringComparison.OrdinalIgnoreCase)) &&
           (!targetPublisherGeneration.HasValue ||
            targetPublisherGeneration.Value == publisher.PublisherGeneration);

    private static bool IsVisible(
        PublisherState publisher,
        ModRuntimeSession requesterSession,
        ModRuntimeKey requester)
        => publisher.Declaration.Visibility switch
        {
            InteropVisibility.Public => true,
            InteropVisibility.Private => string.Equals(
                publisher.PublisherId, requester.ModId, StringComparison.OrdinalIgnoreCase),
            InteropVisibility.DependenciesOnly => requesterSession.HasTrustedDependency(
                requester,
                publisher.PublisherId),
            InteropVisibility.AllowList => publisher.Declaration.AllowList.Any(id =>
                string.Equals(id, requester.ModId, StringComparison.OrdinalIgnoreCase)),
            _ => false
        };

    private static bool TryNormalizeDeclaration(
        string publisherId,
        InteropContractDeclaration declaration,
        out string contractId,
        out InteropError error)
    {
        contractId = string.Empty;
        if (declaration == null || string.IsNullOrWhiteSpace(declaration.ContractId) ||
            declaration.MajorVersion < 0 || declaration.MinorVersion < 0 ||
            declaration.MaxPayloadBytes <= 0 ||
            declaration.MaxPayloadBytes > ModInteropConstants.MaxCustomPayloadBytes)
        {
            error = new(InteropErrorCode.InvalidArgument, "Invalid contract declaration.");
            return false;
        }
        var source = declaration.ContractId.Trim();
        if (source.StartsWith("starray.", StringComparison.Ordinal))
        {
            if (source != ModInteropConstants.VirtualInputPlaybackV2 &&
                source != ModInteropConstants.RequestResponseV1)
            {
                error = new(InteropErrorCode.ContractNotFound,
                    "The starray global contract is not pre-registered.");
                return false;
            }
            if ((source == ModInteropConstants.VirtualInputPlaybackV2 &&
                 declaration.MajorVersion != 2) ||
                (source == ModInteropConstants.RequestResponseV1 &&
                 declaration.MajorVersion != 1))
            {
                error = new(InteropErrorCode.VersionMismatch,
                    "The standard contract major version is fixed by the Host ABI.");
                return false;
            }
            contractId = source;
        }
        else if (source.StartsWith("mod/", StringComparison.Ordinal))
        {
            error = new(InteropErrorCode.InvalidArgument,
                "MODs must provide a local topic instead of impersonating a namespace.");
            return false;
        }
        else
        {
            var topic = source.Trim('/').Replace('/', '.');
            if (topic.Length == 0 || topic.Length > 96 ||
                topic.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '_' or '-')))
            {
                error = new(InteropErrorCode.InvalidArgument, "Invalid local contract topic.");
                return false;
            }
            contractId = $"mod/{publisherId}/{topic}";
        }
        error = InteropError.None;
        return true;
    }

    private static InteropContractDeclaration SnapshotDeclaration(
        InteropContractDeclaration declaration)
        => new(
            declaration.ContractId,
            declaration.MajorVersion,
            declaration.MinorVersion,
            declaration.SchemaId,
            declaration.ContentType)
        {
            Visibility = declaration.Visibility,
            DeliveryMode = declaration.DeliveryMode,
            DispatchContext = declaration.DispatchContext,
            MaxPayloadBytes = declaration.MaxPayloadBytes,
            AllowList = (declaration.AllowList ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };

    private static bool TryNormalizeTopicForSubscription(
        string source,
        out string contractId,
        out InteropError error)
    {
        contractId = string.Empty;
        if (string.IsNullOrWhiteSpace(source))
        {
            error = new(InteropErrorCode.InvalidArgument, "Contract ID is empty.");
            return false;
        }
        source = source.Trim();
        if (source.StartsWith("starray.", StringComparison.Ordinal) ||
            source.StartsWith("mod/", StringComparison.Ordinal))
            contractId = source;
        else
            contractId = source;
        error = InteropError.None;
        return true;
    }

    private static void ApplyState(VirtualInputState state, VirtualInputEvent input)
    {
        if (input.Device == VirtualInputDevice.Keyboard)
        {
            if (string.IsNullOrWhiteSpace(input.CanonicalKey))
                return;
            if (input.Phase == VirtualInputPhase.Down)
                state.HeldKeys[input.CanonicalKey] = input;
            else if (input.Phase is VirtualInputPhase.Up or VirtualInputPhase.Cancel)
                state.HeldKeys.Remove(input.CanonicalKey);
        }
        else
        {
            if (input.PointerId < 0)
                return;
            if (input.Phase is VirtualInputPhase.Down or VirtualInputPhase.Move)
                state.HeldPointers[input.PointerId] = input;
            else if (input.Phase is VirtualInputPhase.Up or VirtualInputPhase.Cancel)
                state.HeldPointers.Remove(input.PointerId);
        }
    }

    private static bool TryValidateVirtualInput(
        VirtualInputEvent input,
        long previousOffsetMicroseconds,
        out InteropError error)
    {
        if (!Enum.IsDefined(input.Device) || !Enum.IsDefined(input.Phase) ||
            input.OffsetMicroseconds < previousOffsetMicroseconds ||
            input.RepeatCount < 0)
        {
            error = new(InteropErrorCode.InvalidArgument,
                "Virtual input contains an invalid device, phase, time, or repeat count.");
            return false;
        }
        if (input.Device == VirtualInputDevice.Keyboard)
        {
            var key = input.CanonicalKey?.Trim();
            if (input.Phase == VirtualInputPhase.Move ||
                string.IsNullOrWhiteSpace(key) || key.Length > 128)
            {
                error = new(InteropErrorCode.InvalidArgument,
                    "Virtual keyboard input requires a bounded canonical key and a key phase.");
                return false;
            }
        }
        else if (input.PointerId < 0 ||
                 !float.IsFinite(input.X) || !float.IsFinite(input.Y) ||
                 !float.IsFinite(input.ViewportWidth) ||
                 !float.IsFinite(input.ViewportHeight) ||
                 input.ViewportWidth <= 0 || input.ViewportHeight <= 0)
        {
            error = new(InteropErrorCode.InvalidArgument,
                "Virtual touch input requires a pointer and finite positive viewport dimensions.");
            return false;
        }
        error = InteropError.None;
        return true;
    }

    private static IReadOnlyList<VirtualInputEvent> CreateSnapshotEvents(VirtualInputState state)
    {
        var events = new List<VirtualInputEvent>(state.HeldKeys.Count + state.HeldPointers.Count);
        events.AddRange(state.HeldKeys.Values.Select(input =>
            input with { Sequence = 0, Phase = VirtualInputPhase.Down }));
        events.AddRange(state.HeldPointers.Values.Select(input =>
            input with { Sequence = 0, Phase = VirtualInputPhase.Down }));
        return events;
    }

    private static IReadOnlyList<VirtualInputEvent> CreateCancelEvents(VirtualInputState state)
    {
        var events = new List<VirtualInputEvent>(state.HeldKeys.Count + state.HeldPointers.Count);
        events.AddRange(state.HeldKeys.Values.Select(input =>
            input with { Sequence = 0, Phase = VirtualInputPhase.Cancel }));
        events.AddRange(state.HeldPointers.Values.Select(input =>
            input with { Sequence = 0, Phase = VirtualInputPhase.Cancel }));
        return events;
    }

    private static ulong NextSequence()
    {
        var value = Interlocked.Increment(ref _nextSequence);
        return value > 0 ? (ulong)value : (ulong)Interlocked.Exchange(ref _nextSequence, 1);
    }

    private static void SafeLog(string category, string message)
    {
        try { Logger.Warn(category, message); }
        catch { }
    }

    private static long EstimateMessageBytes(InteropMessage message)
    {
        var bytes = 128L + message.Payload.Length;
        if (message.VirtualInput is { } virtualInput)
            bytes += 64L * virtualInput.Events.Count;
        return bytes;
    }

    private static int EstimateQueueUnits(InteropMessage message)
        => message.VirtualInput is { } virtualInput
            ? Math.Max(1, virtualInput.Events.Count)
            : 1;

    private static bool TryReserveQueuedBytes(SubscriptionState subscription, long bytes)
    {
        ref var queued = ref GetQueueBudgetCounter(subscription);
        var total = Interlocked.Add(ref queued, bytes);
        if (total <= QueueBudgetBytes)
            return true;
        Interlocked.Add(ref queued, -bytes);
        return false;
    }

    private static void ReleaseQueuedBytes(SubscriptionState subscription, long bytes)
    {
        if (bytes <= 0)
            return;
        ref var queued = ref GetQueueBudgetCounter(subscription);
        var remaining = Interlocked.Add(ref queued, -bytes);
        if (remaining < 0)
            Interlocked.Exchange(ref queued, 0);
    }

    private static ref long GetQueueBudgetCounter(SubscriptionState subscription)
    {
        if (string.Equals(
                subscription.ContractId,
                ModInteropConstants.VirtualInputPlaybackV2,
                StringComparison.Ordinal))
            return ref _queuedVirtualBytes;
        return ref _queuedGenericBytes;
    }

    private static void RemoveQueuedMessages(
        SubscriptionState subscription,
        Func<InteropMessage, bool> predicate)
    {
        lock (subscription.Gate)
        {
            if (subscription.Queue.Count == 0)
                return;
            var retained = new Queue<InteropMessage>(subscription.Queue.Count);
            long removedBytes = 0;
            var removedUnits = 0;
            while (subscription.Queue.Count != 0)
            {
                var message = subscription.Queue.Dequeue();
                if (predicate(message))
                {
                    removedBytes += EstimateMessageBytes(message);
                    removedUnits += EstimateQueueUnits(message);
                }
                else
                    retained.Enqueue(message);
            }
            while (retained.Count != 0)
                subscription.Queue.Enqueue(retained.Dequeue());
            subscription.QueuedBytes = Math.Max(0, subscription.QueuedBytes - removedBytes);
            subscription.QueuedUnits = Math.Max(0, subscription.QueuedUnits - removedUnits);
            ReleaseQueuedBytes(subscription, removedBytes);
        }
    }
}

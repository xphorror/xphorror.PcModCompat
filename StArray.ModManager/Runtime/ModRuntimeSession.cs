using System.Diagnostics;
using StArray.ModManager.Interop;
using StArray.ModManager.Manager;

namespace StArray.ModManager.Runtime;

internal enum ModRuntimeLifecycleState
{
    New,
    Loading,
    Active,
    Retiring,
    Quiescing,
    Suspended,
    Retired,
    Faulted
}

internal readonly record struct ModRuntimeKey(
    string LoaderKind,
    string ModId,
    long Generation)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(LoaderKind) &&
        !string.IsNullOrWhiteSpace(ModId) &&
        Generation > 0;

    public string OwnerId => ToOwnerId(LoaderKind, ModId);

    public bool Matches(ModRuntimeKey other)
        => Generation == other.Generation &&
           string.Equals(LoaderKind, other.LoaderKind, StringComparison.Ordinal) &&
           string.Equals(ModId, other.ModId, StringComparison.OrdinalIgnoreCase);

    public static string ToOwnerId(string loaderKind, string modId)
        => loaderKind switch
        {
            "StArray.Android.Native" => $"native:{modId}",
            "xphorror.PcModCompat" => $"pccompat:{modId}",
            _ => modId
        };
}

internal readonly record struct ModRuntimeSessionSnapshot(
    ModRuntimeKey Key,
    ModRuntimeLifecycleState State,
    int ActiveCallbacks,
    int ActiveOperations);

internal readonly record struct ModRuntimeOwnedOperationSnapshot(
    long OperationId,
    ModRuntimeKey Key,
    string Name,
    bool CancellationRequested);

internal interface IModRuntimeTerminalCleanupRegistration : IDisposable
{
    bool IsActive { get; }
}

/// <summary>
/// Generation-bearing lifecycle for one ModEntry. The owner string remains stable for the
/// HookBroker ABI; this object supplies the load generation and callback quiescence boundary.
/// </summary>
internal sealed class ModRuntimeSession
{
    private readonly object _sync = new();
    private ModRuntimeKey _key;
    private ModDataDomainToken _domainToken;
    private ModIsolationManifest? _isolationManifest;
    private string? _isolationManifestHash;
    private ModRuntimeLifecycleState _state = ModRuntimeLifecycleState.New;
    private long _lastGeneration;
    private int _activeCallbacks;
    private long _lastOperationId;
    private readonly Dictionary<long, OwnedOperationState> _ownedOperations = new();
    private long _lastTerminalCleanupId;
    private readonly Dictionary<long, TerminalCleanupRegistration> _terminalCleanups = new();
    private Action<ModRuntimeKey>? _ownedResourceAuditor;
    private HashSet<string> _trustedDependencies = new(StringComparer.OrdinalIgnoreCase);

    public ModRuntimeKey CurrentKey
    {
        get
        {
            lock (_sync)
                return _key;
        }
    }

    public long Generation
    {
        get
        {
            lock (_sync)
                return _key.Generation;
        }
    }

    internal ModDataDomainToken DomainToken
    {
        get
        {
            lock (_sync)
                return _domainToken;
        }
    }

    internal (ModIsolationManifest? Manifest, string? Hash) SnapshotIsolationManifest()
    {
        lock (_sync)
            return (_isolationManifest, _isolationManifestHash);
    }

    public ModRuntimeSessionSnapshot Snapshot()
    {
        lock (_sync)
            return new ModRuntimeSessionSnapshot(
                _key,
                _state,
                _activeCallbacks,
                _ownedOperations.Count);
    }

    internal IReadOnlyList<ModRuntimeOwnedOperationSnapshot> SnapshotOwnedOperations(
        ModRuntimeKey key)
    {
        lock (_sync)
        {
            if (!MatchesCurrent(key))
                return Array.Empty<ModRuntimeOwnedOperationSnapshot>();
            return _ownedOperations.Values
                .OrderBy(operation => operation.OperationId)
                .Select(operation => new ModRuntimeOwnedOperationSnapshot(
                    operation.OperationId,
                    operation.Key,
                    operation.Name,
                    operation.Cancellation.IsCancellationRequested))
                .ToArray();
        }
    }

    public ModRuntimeKey BeginLoad(string loaderKind, string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loaderKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);

        lock (_sync)
        {
            if (_state is not (
                    ModRuntimeLifecycleState.New or
                    ModRuntimeLifecycleState.Retired or
                    ModRuntimeLifecycleState.Faulted))
            {
                throw new InvalidOperationException(
                    $"MOD runtime cannot begin a new load while state={_state} key={_key}.");
            }
            if (_activeCallbacks != 0)
                throw new InvalidOperationException("MOD runtime still has active callbacks.");
            if (_ownedOperations.Count != 0)
                throw new InvalidOperationException("MOD runtime still has active operations.");
            if (_terminalCleanups.Count != 0)
                throw new InvalidOperationException("MOD runtime still has terminal cleanup registrations.");
            ValidateIdentity(loaderKind, modId);
            if (_lastGeneration == long.MaxValue)
                throw new InvalidOperationException("MOD runtime generation exhausted.");

            _key = new ModRuntimeKey(loaderKind, modId, ++_lastGeneration);
            _domainToken = ModDataDomainRegistry.Open(this, _key).Token;
            _isolationManifest = null;
            _isolationManifestHash = null;
            _trustedDependencies.Clear();
            _state = ModRuntimeLifecycleState.Loading;
            return _key;
        }
    }

    internal bool TrySetTrustedDependencies(
        ModRuntimeKey key,
        IEnumerable<string> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        var normalized = dependencies
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(257)
            .ToArray();
        if (normalized.Length > 256)
            return false;

        lock (_sync)
        {
            if (!MatchesCurrent(key) || _state != ModRuntimeLifecycleState.Loading)
                return false;
            _trustedDependencies = normalized.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return true;
        }
    }

    internal bool TryBindIsolationManifest(
        ModRuntimeKey key,
        ModIsolationManifest manifest,
        out string? manifestHash)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var normalized = manifest.NormalizeAndValidate();
        if (!string.Equals(normalized.LoaderKind, key.LoaderKind, StringComparison.Ordinal) ||
            !string.Equals(normalized.ModId, key.ModId, StringComparison.OrdinalIgnoreCase))
        {
            manifestHash = null;
            return false;
        }
        var hash = normalized.ComputeManifestHash();
        lock (_sync)
        {
            if (!MatchesCurrent(key) || _state != ModRuntimeLifecycleState.Loading)
            {
                manifestHash = null;
                return false;
            }
            if (_isolationManifestHash != null &&
                !string.Equals(_isolationManifestHash, hash, StringComparison.Ordinal))
            {
                manifestHash = null;
                return false;
            }
            _isolationManifest = normalized;
            _isolationManifestHash = hash;
            manifestHash = hash;
            return true;
        }
    }

    internal bool HasTrustedDependency(ModRuntimeKey key, string dependencyId)
    {
        if (string.IsNullOrWhiteSpace(dependencyId))
            return false;
        lock (_sync)
            return MatchesCurrent(key) && _trustedDependencies.Contains(dependencyId);
    }

    /// <summary>Adopts a test/legacy entry that was inserted in Loaded state.</summary>
    public ModRuntimeKey EnsureActive(string loaderKind, string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loaderKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        lock (_sync)
        {
            ValidateIdentity(loaderKind, modId);
            if (_state == ModRuntimeLifecycleState.New)
            {
                if (_lastGeneration == long.MaxValue)
                    throw new InvalidOperationException("MOD runtime generation exhausted.");
                _key = new ModRuntimeKey(loaderKind, modId, ++_lastGeneration);
                _domainToken = ModDataDomainRegistry.Open(this, _key).Token;
                _isolationManifest = null;
                _isolationManifestHash = null;
                _state = ModRuntimeLifecycleState.Active;
            }
            return _key;
        }
    }

    /// <summary>Adopts a legacy entry inserted directly in Loading state.</summary>
    public ModRuntimeKey EnsureLoading(string loaderKind, string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loaderKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        lock (_sync)
        {
            ValidateIdentity(loaderKind, modId);
            if (_state == ModRuntimeLifecycleState.New)
            {
                if (_lastGeneration == long.MaxValue)
                    throw new InvalidOperationException("MOD runtime generation exhausted.");
                _key = new ModRuntimeKey(loaderKind, modId, ++_lastGeneration);
                _domainToken = ModDataDomainRegistry.Open(this, _key).Token;
                _isolationManifest = null;
                _isolationManifestHash = null;
                _state = ModRuntimeLifecycleState.Loading;
            }
            if (_state != ModRuntimeLifecycleState.Loading)
            {
                throw new InvalidOperationException(
                    $"MOD runtime cannot adopt Loading while state={_state} key={_key}.");
            }
            return _key;
        }
    }

    public bool TryPublishActive(ModRuntimeKey key)
    {
        lock (_sync)
        {
            if (!MatchesCurrent(key) || _state != ModRuntimeLifecycleState.Loading)
                return false;
            _state = ModRuntimeLifecycleState.Active;
            return true;
        }
    }

    public bool TryResume(out ModRuntimeKey key)
    {
        lock (_sync)
        {
            key = _key;
            if (_state != ModRuntimeLifecycleState.Suspended || _activeCallbacks != 0)
                return false;
            _state = ModRuntimeLifecycleState.Active;
            return true;
        }
    }

    public bool TryBeginRetirement(ModRuntimeKey key)
    {
        OwnedOperationState[] operations;
        lock (_sync)
        {
            if (!MatchesCurrent(key) ||
                _state is not (ModRuntimeLifecycleState.Loading or ModRuntimeLifecycleState.Active))
                return false;
            _state = ModRuntimeLifecycleState.Retiring;
            operations = _ownedOperations.Values.ToArray();
        }

        foreach (var operation in operations)
            operation.RequestCancellation();
        ModInteropBroker.RetireRuntime(key);
        return true;
    }

    public bool WaitForQuiescence(ModRuntimeKey key, TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        lock (_sync)
        {
            if (!MatchesCurrent(key) ||
                _state is not (
                    ModRuntimeLifecycleState.Retiring or
                    ModRuntimeLifecycleState.Quiescing))
                return false;

            _state = ModRuntimeLifecycleState.Quiescing;
            if (_activeCallbacks == 0)
                return true;

            var stopwatch = Stopwatch.StartNew();
            while (_activeCallbacks != 0)
            {
                if (timeout == Timeout.InfiniteTimeSpan)
                {
                    Monitor.Wait(_sync);
                    continue;
                }

                var remaining = timeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(_sync, remaining))
                    return _activeCallbacks == 0;
            }
            return true;
        }
    }

    public bool TryCompleteSuspension(ModRuntimeKey key)
    {
        lock (_sync)
        {
            if (!MatchesCurrent(key) || _activeCallbacks != 0 ||
                _state is not (
                    ModRuntimeLifecycleState.Retiring or
                    ModRuntimeLifecycleState.Quiescing))
                return false;
            _state = ModRuntimeLifecycleState.Suspended;
            return true;
        }
    }

    public bool TryCompleteRetirement(ModRuntimeKey key)
    {
        TerminalCleanupRegistration[] terminalCleanups;
        ModDataDomainToken domainToken;
        lock (_sync)
        {
            if (!MatchesCurrent(key) || _activeCallbacks != 0 ||
                _state is not (
                    ModRuntimeLifecycleState.Retiring or
                    ModRuntimeLifecycleState.Quiescing))
                return false;
            _state = ModRuntimeLifecycleState.Retired;
            domainToken = _domainToken;
            _domainToken = default;
            terminalCleanups = DrainTerminalCleanupsLocked();
        }

        RunTerminalCleanups(key, terminalCleanups);
        _ = ModDataDomainRegistry.Close(domainToken);
        return true;
    }

    public bool TryReactivate(ModRuntimeKey key)
    {
        lock (_sync)
        {
            if (!MatchesCurrent(key) ||
                _state is not (
                    ModRuntimeLifecycleState.Retiring or
                    ModRuntimeLifecycleState.Quiescing or
                    ModRuntimeLifecycleState.Suspended))
                return false;
            _state = ModRuntimeLifecycleState.Active;
            return true;
        }
    }

    public bool TryCancelRetirement(
        ModRuntimeKey key,
        ModRuntimeLifecycleState previousState)
    {
        if (previousState is not (
                ModRuntimeLifecycleState.Loading or
                ModRuntimeLifecycleState.Active))
            return false;

        lock (_sync)
        {
            if (!MatchesCurrent(key) ||
                _state is not (
                    ModRuntimeLifecycleState.Retiring or
                    ModRuntimeLifecycleState.Quiescing))
                return false;
            _state = previousState;
            return true;
        }
    }

    public bool TryAbortLoad(ModRuntimeKey key)
    {
        TerminalCleanupRegistration[] terminalCleanups;
        ModDataDomainToken domainToken;
        lock (_sync)
        {
            if (!MatchesCurrent(key) || _state != ModRuntimeLifecycleState.Loading)
                return false;
            _state = ModRuntimeLifecycleState.Faulted;
            domainToken = _domainToken;
            _domainToken = default;
            terminalCleanups = DrainTerminalCleanupsLocked();
        }
        ModInteropBroker.RetireRuntime(key);
        RunTerminalCleanups(key, terminalCleanups);
        _ = ModDataDomainRegistry.Close(domainToken);
        return true;
    }

    public bool CanRegisterOwnedResource(ModRuntimeKey key)
    {
        lock (_sync)
            return MatchesCurrent(key) &&
                   _state is ModRuntimeLifecycleState.Loading or ModRuntimeLifecycleState.Active;
    }

    internal void RegisterOwnedResourceAuditor(Action<ModRuntimeKey>? auditor)
    {
        lock (_sync)
            _ownedResourceAuditor = auditor;
    }

    /// <summary>
    /// Registers host cleanup which must run when this generation reaches a terminal state.
    /// Suspended generations retain the registration so they can resume without rebuilding
    /// process-lifetime resources.
    /// </summary>
    internal bool TryRegisterTerminalCleanup(
        ModRuntimeKey key,
        Action cleanup,
        out IModRuntimeTerminalCleanupRegistration? registration)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        lock (_sync)
        {
            if (!MatchesCurrent(key) ||
                _state is not (ModRuntimeLifecycleState.Loading or ModRuntimeLifecycleState.Active) ||
                _lastTerminalCleanupId == long.MaxValue)
            {
                registration = null;
                return false;
            }

            var id = ++_lastTerminalCleanupId;
            var terminalRegistration = new TerminalCleanupRegistration(
                this,
                key,
                id,
                cleanup);
            _terminalCleanups.Add(id, terminalRegistration);
            registration = terminalRegistration;
            return true;
        }
    }

    internal void AuditOwnedResources(ModRuntimeKey key)
    {
        Action<ModRuntimeKey>? auditor;
        lock (_sync)
        {
            if (!MatchesCurrent(key) ||
                _state is not (
                    ModRuntimeLifecycleState.Loading or
                    ModRuntimeLifecycleState.Active))
            {
                return;
            }
            auditor = _ownedResourceAuditor;
        }
        auditor?.Invoke(key);
    }

    public bool TryEnterCallback(ModRuntimeKey key, out IDisposable? lease)
    {
        if (!TryEnterCallbackFast(key))
        {
            lease = null;
            return false;
        }

        lease = new CallbackLease(this, key);
        return true;
    }

    /// <summary>
    /// Allocation-free callback entry for Host-owned hot paths. Every successful entry must be
    /// paired with <see cref="ExitCallbackFast"/> in a finally block.
    /// </summary>
    internal bool TryEnterCallbackFast(ModRuntimeKey key)
    {
        lock (_sync)
        {
            if (!MatchesCurrent(key) || _state != ModRuntimeLifecycleState.Active)
                return false;
            checked { ++_activeCallbacks; }
            return true;
        }
    }

    internal void ExitCallbackFast(ModRuntimeKey key) => ExitCallback(key);

    /// <summary>
    /// Tracks owner-scoped background work started while a MOD is loading or active.
    /// Retirement rejects new work and waits for existing operations through the same
    /// quiescence counter used by managed callbacks.
    /// </summary>
    public bool TryEnterOwnedOperation(ModRuntimeKey key, out IDisposable? lease)
    {
        var entered = TryBeginOwnedOperation(
            key,
            "host-owned-operation",
            out IModRuntimeOperationLease? operationLease);
        lease = operationLease;
        return entered;
    }

    internal bool TryBeginOwnedOperation(
        ModRuntimeKey key,
        string name,
        out IModRuntimeOperationLease? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        OwnedOperationState operation;
        lock (_sync)
        {
            if (!MatchesCurrent(key) ||
                _state is not (
                    ModRuntimeLifecycleState.Loading or
                    ModRuntimeLifecycleState.Active))
            {
                lease = null;
                return false;
            }
            if (_lastOperationId == long.MaxValue)
            {
                lease = null;
                return false;
            }
            checked { ++_activeCallbacks; }
            operation = new OwnedOperationState(
                ++_lastOperationId,
                key,
                NormalizeOperationName(name));
            _ownedOperations.Add(operation.OperationId, operation);
        }

        var identity = OperationIdentity(operation.OperationId, operation.Name);
        if (!ModOwnedResourceRegistry.TryRegister(
                key,
                ModOwnedResourceKind.AsyncOperation,
                identity))
        {
            TryExitOwnedOperation(key, operation.OperationId);
            lease = null;
            return false;
        }

        lease = new ModRuntimeOperationLease(this, operation);
        return true;
    }

    public bool TryEnterCleanupCallback(ModRuntimeKey key, out IDisposable? lease)
    {
        lock (_sync)
        {
            if (!MatchesCurrent(key) ||
                _state is not (
                    ModRuntimeLifecycleState.Loading or
                    ModRuntimeLifecycleState.Active or
                    ModRuntimeLifecycleState.Retiring or
                    ModRuntimeLifecycleState.Quiescing or
                    ModRuntimeLifecycleState.Faulted))
            {
                lease = null;
                return false;
            }
            checked { ++_activeCallbacks; }
            lease = new CallbackLease(this, key);
            return true;
        }
    }

    private bool MatchesCurrent(ModRuntimeKey key)
        => _key.IsValid && _key.Matches(key);

    private void UnregisterTerminalCleanup(
        ModRuntimeKey key,
        long registrationId,
        TerminalCleanupRegistration registration)
    {
        lock (_sync)
        {
            if (MatchesCurrent(key) &&
                _terminalCleanups.TryGetValue(registrationId, out var current) &&
                ReferenceEquals(current, registration))
            {
                _terminalCleanups.Remove(registrationId);
            }
        }
    }

    private TerminalCleanupRegistration[] DrainTerminalCleanupsLocked()
    {
        if (_terminalCleanups.Count == 0)
            return Array.Empty<TerminalCleanupRegistration>();
        var cleanups = _terminalCleanups
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value)
            .ToArray();
        _terminalCleanups.Clear();
        return cleanups;
    }

    private static void RunTerminalCleanups(
        ModRuntimeKey key,
        IReadOnlyList<TerminalCleanupRegistration> cleanups)
    {
        Exception? firstFailure = null;
        var failureCount = 0;
        foreach (var cleanup in cleanups)
        {
            try
            {
                cleanup.Execute();
            }
            catch (Exception exception)
            {
                firstFailure ??= exception;
                ++failureCount;
            }
        }

        if (firstFailure != null)
        {
            try
            {
                Logger.Error(
                    nameof(ModRuntimeSession),
                    $"terminal cleanup failures={failureCount} owner={key.OwnerId} " +
                    $"generation={key.Generation}: {firstFailure}");
            }
            catch
            {
                // Terminal state has already committed; diagnostics cannot roll it back.
            }
        }
    }

    private void ValidateIdentity(string loaderKind, string modId)
    {
        if (!_key.IsValid)
            return;
        if (!string.Equals(_key.LoaderKind, loaderKind, StringComparison.Ordinal) ||
            !string.Equals(_key.ModId, modId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"MOD runtime identity changed from {_key.LoaderKind}/{_key.ModId} " +
                $"to {loaderKind}/{modId}.");
        }
    }

    private void ExitCallback(ModRuntimeKey key)
    {
        lock (_sync)
        {
            if (!MatchesCurrent(key) || _activeCallbacks <= 0)
                return;
            --_activeCallbacks;
            if (_activeCallbacks == 0)
                Monitor.PulseAll(_sync);
        }
    }

    internal bool TryExitOwnedOperation(ModRuntimeKey key, long operationId)
    {
        OwnedOperationState? operation;
        lock (_sync)
        {
            if (!MatchesCurrent(key) ||
                _activeCallbacks <= 0 ||
                !_ownedOperations.TryGetValue(operationId, out operation) ||
                !operation.Key.Matches(key))
            {
                return false;
            }
            _ownedOperations.Remove(operationId);
            --_activeCallbacks;
            if (_activeCallbacks == 0)
                Monitor.PulseAll(_sync);
        }

        ModOwnedResourceRegistry.RetireExact(
            key,
            ModOwnedResourceKind.AsyncOperation,
            OperationIdentity(operation.OperationId, operation.Name));
        operation.Dispose();
        return true;
    }

    private static string NormalizeOperationName(string name)
    {
        var normalized = name.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return normalized.Length <= 160 ? normalized : normalized[..160];
    }

    private static string OperationIdentity(long operationId, string name)
        => $"operation={operationId};name={name}";

    private sealed class CallbackLease(ModRuntimeSession owner, ModRuntimeKey key) : IDisposable
    {
        private ModRuntimeSession? _owner = owner;
        private readonly ModRuntimeKey _key = key;

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.ExitCallback(_key);
    }

    private sealed class TerminalCleanupRegistration(
        ModRuntimeSession owner,
        ModRuntimeKey key,
        long registrationId,
        Action cleanup) : IModRuntimeTerminalCleanupRegistration
    {
        private ModRuntimeSession? _owner = owner;
        private Action? _cleanup = cleanup;
        private int _completed;

        public bool IsActive => Volatile.Read(ref _completed) == 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;
            Interlocked.Exchange(ref _cleanup, null);
            Interlocked.Exchange(ref _owner, null)?.UnregisterTerminalCleanup(
                key,
                registrationId,
                this);
        }

        internal void Execute()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;
            Interlocked.Exchange(ref _owner, null);
            Interlocked.Exchange(ref _cleanup, null)?.Invoke();
        }
    }

    private sealed class ModRuntimeOperationLease(
        ModRuntimeSession owner,
        OwnedOperationState operation) : IModRuntimeOperationLease
    {
        private ModRuntimeSession? _owner = owner;
        private readonly OwnedOperationState _operation = operation;
        private readonly CancellationToken _cancellationToken = operation.Cancellation.Token;

        public CancellationToken CancellationToken => _cancellationToken;

        public bool IsCancellationRequested => _cancellationToken.IsCancellationRequested;

        public void Dispose()
        {
            var currentOwner = Interlocked.Exchange(ref _owner, null);
            currentOwner?.TryExitOwnedOperation(_operation.Key, _operation.OperationId);
        }
    }

    private sealed class OwnedOperationState(
        long operationId,
        ModRuntimeKey key,
        string name) : IDisposable
    {
        public long OperationId { get; } = operationId;
        public ModRuntimeKey Key { get; } = key;
        public string Name { get; } = name;
        public CancellationTokenSource Cancellation { get; } = new();

        public void RequestCancellation()
        {
            try
            {
                var pending = Cancellation.CancelAsync();
                if (!pending.IsCompletedSuccessfully)
                    _ = ObserveCancellationAsync(pending);
            }
            catch (ObjectDisposedException)
            {
                // The operation completed between the retirement snapshot and cancellation.
            }
        }

        public void Dispose() => Cancellation.Dispose();

        private static async Task ObserveCancellationAsync(Task pending)
        {
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch
            {
                // A MOD cancellation callback cannot break Host retirement state.
            }
        }
    }
}

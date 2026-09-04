namespace StArray.ModManager.Runtime;

/// <summary>
/// A MOD runtime scope captured at the moment a resource was created, plus the checks needed
/// to re-enter it later. Shared by every Host bridge that hands a MOD something outliving the
/// call that created it (scheduled callbacks, HTTP clients, …).
/// </summary>
/// <remarks>
/// Extracted from <see cref="ModRuntimeAsyncBridge"/> so additional bridges reuse the same
/// staleness and cross-owner rules instead of re-deriving them; duplicating these checks is
/// how a bridge silently loses generation binding.
/// </remarks>
internal sealed class ModRuntimeCapturedScope
{
    private readonly ModRuntimeSession _session;
    private readonly ModRuntimeKey _key;

    private ModRuntimeCapturedScope(ModRuntimeSession session, ModRuntimeKey key)
    {
        _session = session;
        _key = key;
    }

    internal ModRuntimeKey Key => _key;
    internal ModRuntimeSession Session => _session;

    /// <summary>
    /// Captures the current scope, rejecting a missing scope or a domain token that no longer
    /// resolves to this owner and generation.
    /// </summary>
    internal static ModRuntimeCapturedScope Capture(string operationDescription)
    {
        var session = HookHelper.CurrentRuntimeSession;
        var key = HookHelper.CurrentRuntimeKey;
        if (session == null || !key.IsValid)
        {
            throw new InvalidOperationException(
                $"{operationDescription} requires an active MOD runtime scope.");
        }
        var token = HookHelper.CurrentDomainToken;
        if (!token.IsValid ||
            !ModDataDomainRegistry.TryGetKey(token, out var domainKey) ||
            !domainKey.Matches(key) ||
            !session.DomainToken.Equals(token))
        {
            throw new InvalidOperationException(
                $"{operationDescription} scope is stale for owner={key.OwnerId} " +
                $"generation={key.Generation}.");
        }
        return new ModRuntimeCapturedScope(session, key);
    }

    internal ModRuntimeOwnedOperation Begin(string name)
    {
        if (!_session.TryBeginOwnedOperation(_key, name, out var lease) || lease == null)
        {
            throw new InvalidOperationException(
                $"MOD runtime operation was rejected for retired owner={_key.OwnerId} " +
                $"generation={_key.Generation}.");
        }
        return new ModRuntimeOwnedOperation(this, lease);
    }

    internal bool TryBegin(string name, out ModRuntimeOwnedOperation? operation)
    {
        operation = null;
        if (!_session.TryBeginOwnedOperation(_key, name, out var lease) || lease == null)
            return false;
        operation = new ModRuntimeOwnedOperation(this, lease);
        return true;
    }

    internal IDisposable EnterScope() =>
        HookHelper.EnterOwnerScope(_key.OwnerId, _session, _key);

    /// <summary>
    /// Rejects use of this resource from a different MOD's scope. A Host thread with no owner
    /// scope is allowed so terminal cleanup can still run.
    /// </summary>
    internal void ValidateCurrentCaller(string resourceDescription)
    {
        var currentOwner = HookHelper.CurrentOwnerId;
        if (currentOwner == null)
            return;
        var currentSession = HookHelper.CurrentRuntimeSession;
        var currentKey = HookHelper.CurrentRuntimeKey;
        if (!string.Equals(currentOwner, _key.OwnerId, StringComparison.Ordinal) ||
            currentSession == null ||
            !ReferenceEquals(currentSession, _session) ||
            !currentKey.Matches(_key))
        {
            throw new InvalidOperationException(
                $"{resourceDescription} belongs to owner={_key.OwnerId} " +
                $"generation={_key.Generation}, not the current MOD scope.");
        }
    }

    internal bool TryRegisterCleanup(
        Action cleanup,
        out IModRuntimeTerminalCleanupRegistration? registration) =>
        _session.TryRegisterTerminalCleanup(_key, cleanup, out registration);
}

/// <summary>Generation-bound operation lease with the scope needed to re-enter its owner.</summary>
internal sealed class ModRuntimeOwnedOperation : IDisposable
{
    private readonly ModRuntimeCapturedScope _scope;
    private IModRuntimeOperationLease? _lease;

    internal ModRuntimeOwnedOperation(
        ModRuntimeCapturedScope scope,
        IModRuntimeOperationLease lease)
    {
        _scope = scope;
        _lease = lease;
    }

    internal CancellationToken CancellationToken =>
        Volatile.Read(ref _lease)?.CancellationToken ?? new CancellationToken(true);

    internal IDisposable EnterScope() => _scope.EnterScope();

    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
}

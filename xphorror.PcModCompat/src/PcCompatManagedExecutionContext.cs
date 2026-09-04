using System.Runtime.Loader;

namespace Xphorror.PcModCompat;

public enum PcCompatManagedExecutionPhase
{
    Bootstrap,
    Setup,
    Enable,
    Update,
    Disable
}

public sealed record PcCompatManagedExecutionState(
    string ModId,
    long ResourceSessionGeneration,
    PcCompatManagedExecutionPhase Phase)
{
    internal SynchronizationContext? UnityMainSynchronizationContext;
    internal AssemblyLoadContext? ManagedLoadContext;
}

/// <summary>
/// Identifies the MOD which currently owns a managed compatibility callback.
/// States are preallocated by each session, so entering the per-frame Update
/// scope does not allocate.
/// </summary>
public static class PcCompatManagedExecutionContext
{
    [ThreadStatic]
    private static PcCompatManagedExecutionState? t_current;

    [ThreadStatic]
    private static PcCompatManagedCallbackContext? t_callback;

    private static readonly AsyncLocal<PcCompatManagedExecutionState?> FlowingCurrent = new();
    private static readonly AsyncLocal<PcCompatManagedCallbackContext?> FlowingCallback = new();

    public static PcCompatManagedExecutionState? Current => t_current ?? FlowingCurrent.Value;

    internal static PcCompatManagedCallbackContext? CurrentCallback
        => t_callback ?? FlowingCallback.Value;

    public static PcCompatManagedExecutionScope Enter(PcCompatManagedExecutionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var previous = t_current;
        var unityMainOwnerScope =
            PcCompatUnityMainExecutionContext.EnterManagedOwner(state);
        t_current = state;
        return new PcCompatManagedExecutionScope(
            state,
            previous,
            unityMainOwnerScope);
    }

    internal static PcCompatManagedExecutionFlowingScope EnterFlowing(
        PcCompatManagedExecutionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var previous = FlowingCurrent.Value;
        FlowingCurrent.Value = state;
        return new PcCompatManagedExecutionFlowingScope(state, previous);
    }

    /// <summary>
    /// Marks the lease owned by an outer managed callback as reusable for nested synchronous
    /// bridge calls on the same thread. A nested call must not re-enter the global session index:
    /// that index may already be publishing another generation while the outer callback is still
    /// quiescing.
    /// </summary>
    internal static PcCompatManagedCallbackScope EnterCallback(
        PcCompatManagedExecutionState state,
        IDisposable callbackLease,
        CancellationToken retirementToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(callbackLease);
        return EnterCallbackCore(state, callbackLease, retirementToken, ownsLease: true);
    }

    /// <summary>Associates a retained collector lease with the current managed dispatch.</summary>
    internal static PcCompatManagedCallbackScope EnterBorrowedCallback(
        PcCompatManagedExecutionState state,
        CancellationToken retirementToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        return EnterCallbackCore(state, callbackLease: null, retirementToken, ownsLease: false);
    }

    internal static bool HasReusableCallback(PcCompatManagedExecutionState owner)
        => TryGetReusableCallback(owner, out _, out _);

    internal static bool TryGetReusableCallback(
        PcCompatManagedExecutionState owner,
        out IDisposable? borrowedLease,
        out CancellationToken retirementToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var callback = t_callback ?? FlowingCallback.Value;
        if (callback == null || !callback.CanReuse(owner))
        {
            borrowedLease = null;
            retirementToken = new CancellationToken(canceled: true);
            return false;
        }

        borrowedLease = PcCompatManagedBorrowedCallbackLease.Instance;
        retirementToken = callback.RetirementToken;
        return true;
    }

    private static PcCompatManagedCallbackScope EnterCallbackCore(
        PcCompatManagedExecutionState state,
        IDisposable? callbackLease,
        CancellationToken retirementToken,
        bool ownsLease)
    {
        var previous = t_callback;
        var previousFlowing = FlowingCallback.Value;
        var callback = new PcCompatManagedCallbackContext(
            state,
            retirementToken,
            Thread.CurrentThread);
        t_callback = callback;
        FlowingCallback.Value = callback;
        return new PcCompatManagedCallbackScope(
            callback,
            previous,
            previousFlowing,
            ownsLease ? callbackLease : null);
    }

    internal static void Exit(
        PcCompatManagedExecutionState entered,
        PcCompatManagedExecutionState? previous,
        PcCompatUnityMainExecutionContext.ManagedOwnerScope unityMainOwnerScope)
    {
        if (!ReferenceEquals(t_current, entered))
            throw new InvalidOperationException("Managed execution scopes must exit in LIFO order.");
        t_current = previous;
        unityMainOwnerScope.Dispose();
    }

    internal static void ExitFlowing(
        PcCompatManagedExecutionState entered,
        PcCompatManagedExecutionState? previous)
    {
        if (!ReferenceEquals(FlowingCurrent.Value, entered))
            throw new InvalidOperationException("Managed flowing scopes must exit in LIFO order.");
        FlowingCurrent.Value = previous;
    }

    internal static void RestoreCallback(
        PcCompatManagedCallbackContext entered,
        PcCompatManagedCallbackContext? previous,
        PcCompatManagedCallbackContext? previousFlowing)
    {
        if (!ReferenceEquals(t_callback, entered) ||
            !ReferenceEquals(FlowingCallback.Value, entered))
        {
            throw new InvalidOperationException(
                "Managed callback scopes must exit in LIFO order on their owning thread.");
        }
        t_callback = previous;
        FlowingCallback.Value = previousFlowing;
    }
}

internal sealed class PcCompatManagedCallbackContext(
    PcCompatManagedExecutionState state,
    CancellationToken retirementToken,
    Thread enteredThread)
{
    private int _active = 1;

    public string ModId { get; } = state.ModId;
    public long ResourceSessionGeneration { get; } = state.ResourceSessionGeneration;
    public CancellationToken RetirementToken { get; } = retirementToken;

    public bool CanReuse(PcCompatManagedExecutionState owner)
        => Volatile.Read(ref _active) != 0 &&
           ReferenceEquals(enteredThread, Thread.CurrentThread) &&
           string.Equals(ModId, owner.ModId, StringComparison.OrdinalIgnoreCase) &&
           ResourceSessionGeneration == owner.ResourceSessionGeneration;

    public void Deactivate() => Volatile.Write(ref _active, 0);
}

internal sealed class PcCompatManagedCallbackScope : IDisposable
{
    private PcCompatManagedCallbackContext? _callback;
    private readonly PcCompatManagedCallbackContext? _previous;
    private readonly PcCompatManagedCallbackContext? _previousFlowing;
    private IDisposable? _ownedLease;
    private int _disposed;

    internal PcCompatManagedCallbackScope(
        PcCompatManagedCallbackContext callback,
        PcCompatManagedCallbackContext? previous,
        PcCompatManagedCallbackContext? previousFlowing,
        IDisposable? ownedLease)
    {
        _callback = callback;
        _previous = previous;
        _previousFlowing = previousFlowing;
        _ownedLease = ownedLease;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var callback = Interlocked.Exchange(ref _callback, null)
                       ?? throw new InvalidOperationException(
                           "Managed callback scope has already exited.");
        if (!ReferenceEquals(PcCompatManagedExecutionContext.CurrentCallback, callback))
            throw new InvalidOperationException(
                "Managed callback scopes must exit in LIFO order on their owning thread.");

        try
        {
            PcCompatManagedExecutionContext.RestoreCallback(
                callback,
                _previous,
                _previousFlowing);
        }
        finally
        {
            callback.Deactivate();
            Interlocked.Exchange(ref _ownedLease, null)?.Dispose();
        }
    }
}

internal sealed class PcCompatManagedBorrowedCallbackLease : IDisposable
{
    public static readonly PcCompatManagedBorrowedCallbackLease Instance = new();

    private PcCompatManagedBorrowedCallbackLease()
    {
    }

    public void Dispose()
    {
    }
}

public readonly struct PcCompatManagedExecutionScope : IDisposable
{
    private readonly PcCompatManagedExecutionState _entered;
    private readonly PcCompatManagedExecutionState? _previous;
    private readonly PcCompatUnityMainExecutionContext.ManagedOwnerScope _unityMainOwnerScope;

    internal PcCompatManagedExecutionScope(
        PcCompatManagedExecutionState entered,
        PcCompatManagedExecutionState? previous,
        PcCompatUnityMainExecutionContext.ManagedOwnerScope unityMainOwnerScope)
    {
        _entered = entered;
        _previous = previous;
        _unityMainOwnerScope = unityMainOwnerScope;
    }

    public void Dispose()
        => PcCompatManagedExecutionContext.Exit(
            _entered,
            _previous,
            _unityMainOwnerScope);
}

internal readonly struct PcCompatManagedExecutionFlowingScope : IDisposable
{
    private readonly PcCompatManagedExecutionState _entered;
    private readonly PcCompatManagedExecutionState? _previous;

    internal PcCompatManagedExecutionFlowingScope(
        PcCompatManagedExecutionState entered,
        PcCompatManagedExecutionState? previous)
    {
        _entered = entered;
        _previous = previous;
    }

    public void Dispose() =>
        PcCompatManagedExecutionContext.ExitFlowing(_entered, _previous);
}

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

    public static PcCompatManagedExecutionState? Current => t_current;

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

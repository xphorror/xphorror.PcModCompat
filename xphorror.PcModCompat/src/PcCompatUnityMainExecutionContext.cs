namespace Xphorror.PcModCompat;

/// <summary>
/// Marks managed callbacks entered through the verified native UnityMain hook.
/// The scope is thread-local and allocation-free so it can wrap frame callbacks.
/// </summary>
public static class PcCompatUnityMainExecutionContext
{
    private sealed record ContinuationHost(Func<Action, bool> Schedule);

    private sealed class UnityMainSynchronizationContext(
        PcCompatManagedExecutionState? owner) : SynchronizationContext
    {
        public PcCompatManagedExecutionState? Owner => owner;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var host = Volatile.Read(ref s_continuationHost);
            if (host == null)
            {
                RejectContinuation(
                    owner,
                    "UnityMain continuation scheduler is unavailable.");
                return;
            }

            try
            {
                if (host.Schedule(() => DispatchContinuation(callback, state, owner)))
                    return;
                RejectContinuation(
                    owner,
                    "UnityMain continuation queue rejected the callback.");
            }
            catch (Exception exception)
            {
                RejectContinuation(
                    owner,
                    "UnityMain continuation scheduler threw " +
                    exception.GetType().Name + ": " + exception.Message);
            }
        }

        public override void Send(SendOrPostCallback callback, object? state)
        {
            ArgumentNullException.ThrowIfNull(callback);
            if (!IsActive)
            {
                throw new InvalidOperationException(
                    "Synchronous dispatch to UnityMain from a worker thread is not supported.");
            }
            callback(state);
        }

        public override SynchronizationContext CreateCopy() => this;
    }

    private static readonly SynchronizationContext UnityMainContext =
        new UnityMainSynchronizationContext(owner: null);
    private static ContinuationHost? s_continuationHost;

    [ThreadStatic]
    private static int t_depth;

    [ThreadStatic]
    private static SynchronizationContext? t_previousSynchronizationContext;

    public static bool IsActive => t_depth > 0;

    public static void RegisterContinuationScheduler(Func<Action, bool>? scheduler)
        => Volatile.Write(
            ref s_continuationHost,
            scheduler is null ? null : new ContinuationHost(scheduler));

    public static Scope Enter()
    {
        if (t_depth == int.MaxValue)
            throw new InvalidOperationException("UnityMain execution context depth overflow.");
        if (t_depth == 0)
        {
            t_previousSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(UnityMainContext);
        }
        ++t_depth;
        return new Scope();
    }

    internal static ManagedOwnerScope EnterManagedOwner(
        PcCompatManagedExecutionState owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!IsActive)
            return default;

        var previous = SynchronizationContext.Current;
        var context = Volatile.Read(ref owner.UnityMainSynchronizationContext);
        while (context is not UnityMainSynchronizationContext ownedContext ||
               !ReferenceEquals(ownedContext.Owner, owner))
        {
            var created = new UnityMainSynchronizationContext(owner);
            var observed = Interlocked.CompareExchange(
                ref owner.UnityMainSynchronizationContext,
                created,
                context);
            context = ReferenceEquals(observed, context) ? created : observed;
        }
        SynchronizationContext.SetSynchronizationContext(context);
        return new ManagedOwnerScope(previous, active: true);
    }

    private static void Leave()
    {
        if (t_depth <= 0)
            return;
        if (--t_depth != 0)
            return;

        SynchronizationContext.SetSynchronizationContext(
            t_previousSynchronizationContext);
        t_previousSynchronizationContext = null;
    }

    private static void DispatchContinuation(
        SendOrPostCallback callback,
        object? state,
        PcCompatManagedExecutionState? owner)
    {
        if (owner != null && !PcCompatRuntime.CanDispatchManagedContinuation(owner))
            return;

        using var unityMainScope = Enter();
        try
        {
            if (owner is null)
            {
                callback(state);
                return;
            }

            using var ownerScope = PcCompatManagedExecutionContext.Enter(owner);
            callback(state);
        }
        catch (Exception exception)
        {
            RejectContinuation(
                owner,
                "UnityMain continuation execution threw " +
                exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static void RejectContinuation(
        PcCompatManagedExecutionState? owner,
        string error)
    {
        try
        {
            PcCompatRuntime.ReportManagedContinuationFailure(owner, error);
        }
        catch
        {
            // SynchronizationContext.Post is frequently entered by a CoreCLR
            // ThreadPool callback. An exception escaping here terminates the
            // process, so diagnostics and MOD fault isolation are best effort.
        }
    }

    internal readonly struct ManagedOwnerScope(
        SynchronizationContext? previous,
        bool active)
    {
        public void Dispose()
        {
            if (active)
                SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    public readonly ref struct Scope
    {
        public void Dispose() => Leave();
    }
}

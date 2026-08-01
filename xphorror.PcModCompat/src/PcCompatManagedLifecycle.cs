using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Xphorror.PcModCompat;

public enum PcCompatManagedLifecycleState
{
    Loaded,
    Enabling,
    Enabled,
    Disabling,
    Disabled,
    Faulted,
    Disposed
}

public sealed class PcCompatManagedLifecycleSnapshot
{
    public required PcCompatManagedLifecycleState State { get; init; }
    public required bool HasUpdate { get; init; }
    public required long UpdateCount { get; init; }
    public required long FaultCount { get; init; }
    public required double TotalUpdateMilliseconds { get; init; }
    public required double MaximumUpdateMilliseconds { get; init; }
    public string? LastError { get; init; }
}

public sealed class PcCompatManagedLifecycleController
{
    private readonly Action? _enable;
    private readonly Action<float>? _update;
    private readonly Action? _disable;
    private int _state = (int)PcCompatManagedLifecycleState.Loaded;
    private int _callbackActive;
    private int _disableInvoked;
    private long _updateCount;
    private long _faultCount;
    private long _totalUpdateTicks;
    private long _maximumUpdateTicks;
    private string? _lastError;

    public PcCompatManagedLifecycleController(object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        _enable = CreateDelegate<Action>(instance, "CompatEnable", Type.EmptyTypes);
        _update = CreateDelegate<Action<float>>(instance, "CompatUpdate", [typeof(float)]);
        _disable = CreateDelegate<Action>(instance, "CompatDisable", Type.EmptyTypes);
    }

    public PcCompatManagedLifecycleState State
        => (PcCompatManagedLifecycleState)Volatile.Read(ref _state);

    public bool RequiresFrameDispatch
        => State == PcCompatManagedLifecycleState.Enabled && _update != null;

    public bool TryEnable(out string? error)
    {
        error = null;
        if (_enable == null)
        {
            error = "CompatEnable() is unavailable.";
            Fault(error);
            return false;
        }

        if (Interlocked.CompareExchange(
                ref _state,
                (int)PcCompatManagedLifecycleState.Enabling,
                (int)PcCompatManagedLifecycleState.Loaded) !=
            (int)PcCompatManagedLifecycleState.Loaded)
        {
            if (State == PcCompatManagedLifecycleState.Enabled)
                return true;
            error = $"Managed lifecycle cannot enable from state {State}.";
            return false;
        }

        try
        {
            InvokeExclusive(_enable);
            Volatile.Write(ref _state, (int)PcCompatManagedLifecycleState.Enabled);
            return true;
        }
        catch (Exception exception)
        {
            error = FormatException(exception);
            Fault(error);
            TryDisableAfterFault();
            return false;
        }
    }

    public bool TryDispatchUpdate(float deltaTime)
    {
        if (_update == null || State != PcCompatManagedLifecycleState.Enabled)
            return State == PcCompatManagedLifecycleState.Enabled;
        if (Volatile.Read(ref _callbackActive) != 0)
        {
            Fault("Managed lifecycle callback re-entry was rejected.");
            return false;
        }
        if (!float.IsFinite(deltaTime) || deltaTime < 0f)
        {
            Fault($"Invalid managed lifecycle deltaTime: {deltaTime}.");
            TryDisableAfterFault();
            return false;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            InvokeUpdateExclusive(deltaTime);
            if (State != PcCompatManagedLifecycleState.Enabled)
            {
                TryDisableAfterFault();
                return false;
            }
            var elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Increment(ref _updateCount);
            Interlocked.Add(ref _totalUpdateTicks, elapsed);
            UpdateMaximum(ref _maximumUpdateTicks, elapsed);
            return true;
        }
        catch (Exception exception)
        {
            Fault(FormatException(exception));
            TryDisableAfterFault();
            return false;
        }
    }

    public void Disable()
    {
        var state = State;
        if (state is PcCompatManagedLifecycleState.Disabled or PcCompatManagedLifecycleState.Disposed)
            return;
        if (state == PcCompatManagedLifecycleState.Loaded)
        {
            Volatile.Write(ref _state, (int)PcCompatManagedLifecycleState.Disabled);
            return;
        }
        if (state != PcCompatManagedLifecycleState.Faulted)
            Volatile.Write(ref _state, (int)PcCompatManagedLifecycleState.Disabling);

        TryInvokeDisable();
        if (State != PcCompatManagedLifecycleState.Faulted)
            Volatile.Write(ref _state, (int)PcCompatManagedLifecycleState.Disabled);
    }

    public void MarkDisposed()
    {
        Disable();
        Volatile.Write(ref _state, (int)PcCompatManagedLifecycleState.Disposed);
    }

    internal void FaultFromChild(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        if (State == PcCompatManagedLifecycleState.Disposed)
            return;
        Fault(error);
        TryDisableAfterFault();
    }

    public PcCompatManagedLifecycleSnapshot Snapshot()
    {
        var totalTicks = Interlocked.Read(ref _totalUpdateTicks);
        var maximumTicks = Interlocked.Read(ref _maximumUpdateTicks);
        return new PcCompatManagedLifecycleSnapshot
        {
            State = State,
            HasUpdate = _update != null,
            UpdateCount = Interlocked.Read(ref _updateCount),
            FaultCount = Interlocked.Read(ref _faultCount),
            TotalUpdateMilliseconds = TicksToMilliseconds(totalTicks),
            MaximumUpdateMilliseconds = TicksToMilliseconds(maximumTicks),
            LastError = Volatile.Read(ref _lastError)
        };
    }

    private static TDelegate? CreateDelegate<TDelegate>(
        object instance,
        string methodName,
        Type[] parameterTypes)
        where TDelegate : Delegate
    {
        var method = instance.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: parameterTypes,
            modifiers: null);
        if (method == null)
            return null;
        TryPrepareMethod(method);
        var callback = method.CreateDelegate<TDelegate>(instance);
        try
        {
            RuntimeHelpers.PrepareDelegate(callback);
        }
        catch
        {
            // Some runtime-generated delegate thunks cannot be eagerly prepared.
        }
        return callback;
    }

    private static void TryPrepareMethod(MethodInfo method)
    {
        if (method.ContainsGenericParameters)
            return;
        try
        {
            RuntimeHelpers.PrepareMethod(method.MethodHandle);
        }
        catch
        {
            // Lazy JIT remains the compatibility fallback.
        }
    }

    private void InvokeExclusive(Action callback)
    {
        if (Interlocked.Exchange(ref _callbackActive, 1) != 0)
            throw new InvalidOperationException("Managed lifecycle callback re-entry was rejected.");
        try
        {
            callback();
        }
        finally
        {
            Volatile.Write(ref _callbackActive, 0);
        }
    }

    private void InvokeUpdateExclusive(float deltaTime)
    {
        if (Interlocked.Exchange(ref _callbackActive, 1) != 0)
            throw new InvalidOperationException("Managed lifecycle callback re-entry was rejected.");
        try
        {
            _update!(deltaTime);
        }
        finally
        {
            Volatile.Write(ref _callbackActive, 0);
        }
    }

    private void TryDisableAfterFault()
    {
        try
        {
            var execution = PcCompatManagedExecutionContext.Current;
            if (execution == null || execution.Phase == PcCompatManagedExecutionPhase.Disable)
            {
                TryInvokeDisable();
                return;
            }

            using var disableScope = PcCompatManagedExecutionContext.Enter(
                execution with { Phase = PcCompatManagedExecutionPhase.Disable });
            TryInvokeDisable();
        }
        catch
        {
            // The original lifecycle error remains authoritative.
        }
    }

    private void TryInvokeDisable()
    {
        if (_disable == null || Interlocked.Exchange(ref _disableInvoked, 1) != 0)
            return;
        try
        {
            InvokeExclusive(_disable);
        }
        catch (Exception exception)
        {
            Fault(FormatException(exception));
        }
    }

    private void Fault(string error)
    {
        Interlocked.Increment(ref _faultCount);
        Volatile.Write(ref _lastError, error);
        Volatile.Write(ref _state, (int)PcCompatManagedLifecycleState.Faulted);
    }

    private static string FormatException(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null } invocation)
            exception = invocation.InnerException!;
        return exception.ToString();
    }

    private static void UpdateMaximum(ref long target, long candidate)
    {
        var current = Volatile.Read(ref target);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private static double TicksToMilliseconds(long ticks)
        => ticks * 1000d / Stopwatch.Frequency;
}

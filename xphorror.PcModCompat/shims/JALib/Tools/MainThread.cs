using System.Collections;
using System.Collections.Concurrent;
using JALib.Core;
using UnityEngine;

namespace JALib.Tools;

public static class MainThread
{
    private const int MaximumActionsPerUpdate = 4096;
    private const int MaximumCoroutineTransitionsPerFrame = 256;
    private const int MaximumNestedCoroutineDepth = 32;
    private static readonly ConcurrentQueue<WorkItem> Queue = new();
    private static readonly ConcurrentQueue<WaitItem> Waiters = new();
    private static readonly ConcurrentDictionary<JAMod, OwnerRegistration> Owners = new();
    private static readonly AsyncLocal<OwnerContext?> AmbientOwner = new();
    private static readonly object CoroutineGate = new();
    private static readonly List<CoroutineRegistration> Coroutines = [];
    private static int s_mainThreadId;
    private static Thread? s_thread;
    private static bool s_isRunningOnMainThreadUpdate;
    private static long s_nextOwnerGeneration;
    private static long s_frameGeneration;
    private static long s_queuedCount;
    private static long s_dequeuedCount;
    private static long s_inlineCount;
    private static long s_executedCount;
    private static long s_failedCount;
    private static long s_inactiveDroppedCount;
    private static long s_drainCount;
    private static long s_maxPendingCount;
    private static long s_startedCoroutineCount;
    private static long s_completedCoroutineCount;
    private static long s_stoppedCoroutineCount;

    public static Thread? Thread => Volatile.Read(ref s_thread);
    public static bool IsRunningOnMainThreadUpdate =>
        Volatile.Read(ref s_isRunningOnMainThreadUpdate) && IsMainThread();
    public static bool IsQueueEmpty => Queue.IsEmpty;
    internal static JAMod? CurrentOwner => AmbientOwner.Value?.Owner;

    public static bool IsMainThread()
        => Volatile.Read(ref s_mainThreadId) == Environment.CurrentManagedThreadId;

    public static Task WaitForMainThread()
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = CaptureOwner(CurrentOwner);
        Waiters.Enqueue(new WaitItem(owner.Owner, owner.Generation, completion));
        return completion.Task;
    }

    public static void Run(JAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        RunCaptured(action.Owner ?? CurrentOwner, action.Invoke);
    }

    public static void Run(object? owner, Action action)
        => RunCaptured(owner as JAMod ?? CurrentOwner, action);

    public static void Run(Action action)
        => RunCaptured(CurrentOwner, action);

    public static void Run(JAMod owner, Action action)
        => RunCaptured(owner, action);

    public static void ForceQueue(JAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ForceQueueCaptured(action.Owner ?? CurrentOwner, action.Invoke);
    }

    public static void ForceQueue(JAMod owner, Action action)
        => ForceQueueCaptured(owner, action);

    public static Coroutine StartCoroutine(IEnumerator routine)
    {
        ArgumentNullException.ThrowIfNull(routine);
        RequireMainThread(nameof(StartCoroutine));
        var owner = CaptureOwner(CurrentOwner);
        var token = CreateCoroutineToken();
        var registration = new CoroutineRegistration(
            token,
            routine,
            owner.Owner,
            owner.Generation,
            Volatile.Read(ref s_frameGeneration));
        lock (CoroutineGate)
            Coroutines.Add(registration);
        Interlocked.Increment(ref s_startedCoroutineCount);
        AdvanceCoroutine(registration, Volatile.Read(ref s_frameGeneration));
        return token;
    }

    public static void StopCoroutine(Coroutine routine)
    {
        ArgumentNullException.ThrowIfNull(routine);
        RequireMainThread(nameof(StopCoroutine));
        lock (CoroutineGate)
        {
            var registration = Coroutines.FirstOrDefault(item =>
                ReferenceEquals(item.Token, routine));
            if (registration != null)
                StopCoroutineLocked(registration);
        }
    }

    public static void StopCoroutine(IEnumerator routine)
    {
        ArgumentNullException.ThrowIfNull(routine);
        RequireMainThread(nameof(StopCoroutine));
        lock (CoroutineGate)
        {
            var registration = Coroutines.FirstOrDefault(item =>
                ReferenceEquals(item.Root, routine));
            if (registration != null)
                StopCoroutineLocked(registration);
        }
    }

    internal static void Register(JAMod owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        BindMainThread();
        Owners.TryAdd(
            owner,
            new OwnerRegistration(Interlocked.Increment(ref s_nextOwnerGeneration)));
    }

    internal static void Activate(JAMod owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        BindMainThread();
        Owners.GetOrAdd(
            owner,
            _ => new OwnerRegistration(Interlocked.Increment(ref s_nextOwnerGeneration)));
    }

    internal static void Deactivate(JAMod owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Owners.TryRemove(owner, out _);
        lock (CoroutineGate)
        {
            foreach (var registration in Coroutines
                         .Where(item => ReferenceEquals(item.Owner, owner))
                         .ToArray())
            {
                StopCoroutineLocked(registration);
            }
        }
    }

    internal static IDisposable EnterOwner(JAMod owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var previous = AmbientOwner.Value;
        var captured = CaptureOwner(owner);
        AmbientOwner.Value = captured.Owner == null
            ? null
            : new OwnerContext(captured.Owner, captured.Generation);
        return new OwnerScope(previous);
    }

    internal static void RunCaptured(JAMod? owner, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var captured = CaptureOwner(owner);
        if (owner != null && captured.Owner == null)
        {
            Interlocked.Increment(ref s_inactiveDroppedCount);
            return;
        }
        if (IsMainThread())
        {
            Interlocked.Increment(ref s_inlineCount);
            Invoke(captured.Owner, captured.Generation, action);
            return;
        }
        Enqueue(captured.Owner, captured.Generation, action);
    }

    internal static void ForceQueueCaptured(JAMod? owner, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var captured = CaptureOwner(owner);
        if (owner != null && captured.Owner == null)
        {
            Interlocked.Increment(ref s_inactiveDroppedCount);
            return;
        }
        Enqueue(captured.Owner, captured.Generation, action);
    }

    internal static void Drain()
    {
        BindMainThread();
        Volatile.Write(ref s_isRunningOnMainThreadUpdate, true);
        Interlocked.Increment(ref s_drainCount);
        var frameGeneration = Interlocked.Increment(ref s_frameGeneration);
        try
        {
            DrainWaiters();
            for (var count = 0;
                 count < MaximumActionsPerUpdate && Queue.TryDequeue(out var work);
                 ++count)
            {
                Interlocked.Increment(ref s_dequeuedCount);
                Invoke(work.Owner, work.OwnerGeneration, work.Action);
            }
            AdvanceCoroutines(frameGeneration);
        }
        finally
        {
            Volatile.Write(ref s_isRunningOnMainThreadUpdate, false);
        }
    }

    public static string GetDiagnosticStatus()
    {
        int coroutineCount;
        lock (CoroutineGate)
            coroutineCount = Coroutines.Count;
        return $"mainThreadId={Volatile.Read(ref s_mainThreadId)}" +
               $" activeOwners={Owners.Count}" +
               $" pending={Queue.Count}" +
               $" waiters={Waiters.Count}" +
               $" queued={Interlocked.Read(ref s_queuedCount)}" +
               $" dequeued={Interlocked.Read(ref s_dequeuedCount)}" +
               $" inline={Interlocked.Read(ref s_inlineCount)}" +
               $" executed={Interlocked.Read(ref s_executedCount)}" +
               $" failed={Interlocked.Read(ref s_failedCount)}" +
               $" inactiveDropped={Interlocked.Read(ref s_inactiveDroppedCount)}" +
               $" drains={Interlocked.Read(ref s_drainCount)}" +
               $" maxPending={Interlocked.Read(ref s_maxPendingCount)}" +
               $" coroutines={coroutineCount}" +
               $" coroutineStarted={Interlocked.Read(ref s_startedCoroutineCount)}" +
               $" coroutineCompleted={Interlocked.Read(ref s_completedCoroutineCount)}" +
               $" coroutineStopped={Interlocked.Read(ref s_stoppedCoroutineCount)}";
    }

    private static void Enqueue(JAMod? owner, long generation, Action action)
    {
        Queue.Enqueue(new WorkItem(owner, generation, action));
        Interlocked.Increment(ref s_queuedCount);
        UpdateMaximumPending(Queue.Count);
    }

    private static void DrainWaiters()
    {
        while (Waiters.TryDequeue(out var waiter))
        {
            if (!IsCurrent(waiter.Owner, waiter.OwnerGeneration))
            {
                Interlocked.Increment(ref s_inactiveDroppedCount);
                waiter.Completion.TrySetCanceled();
                continue;
            }
            waiter.Completion.TrySetResult(true);
        }
    }

    private static void Invoke(JAMod? owner, long ownerGeneration, Action action)
    {
        if (!IsCurrent(owner, ownerGeneration))
        {
            Interlocked.Increment(ref s_inactiveDroppedCount);
            return;
        }
        Interlocked.Increment(ref s_executedCount);
        using var scope = owner == null ? null : EnterOwner(owner);
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref s_failedCount);
            if (owner != null)
                owner.LogReportException("MainThread action failed", exception);
            else
                Console.Error.WriteLine("[PcModCompat][JALib][MainThread] " + exception);
        }
    }

    private static bool IsCurrent(JAMod? owner, long generation)
        => owner == null ||
           Owners.TryGetValue(owner, out var active) && active.Generation == generation;

    private static OwnerCapture CaptureOwner(JAMod? owner)
    {
        if (owner == null)
            return default;
        if (Owners.TryGetValue(owner, out var active))
            return new OwnerCapture(owner, active.Generation);
        return default;
    }

    private static void AdvanceCoroutines(long frameGeneration)
    {
        lock (CoroutineGate)
        {
            foreach (var registration in Coroutines.ToArray())
            {
                if (!IsCurrent(registration.Owner, registration.OwnerGeneration))
                {
                    StopCoroutineLocked(registration);
                    continue;
                }
                AdvanceCoroutine(registration, frameGeneration);
            }
        }
    }

    private static void AdvanceCoroutine(
        CoroutineRegistration registration,
        long frameGeneration)
    {
        if (registration.Completed || frameGeneration < registration.ResumeFrame)
            return;
        using var scope = registration.Owner == null
            ? null
            : EnterOwner(registration.Owner);
        try
        {
            for (var transition = 0;
                 transition < MaximumCoroutineTransitionsPerFrame;
                 ++transition)
            {
                if (registration.Stack.Count == 0)
                {
                    CompleteCoroutineLocked(registration);
                    return;
                }
                var current = registration.Stack.Peek();
                if (!current.MoveNext())
                {
                    DisposeEnumerator(current);
                    registration.Stack.Pop();
                    continue;
                }
                if (current.Current is IEnumerator nested)
                {
                    if (registration.Stack.Count >= MaximumNestedCoroutineDepth)
                    {
                        throw new InvalidOperationException(
                            $"JALib coroutine nested depth exceeded {MaximumNestedCoroutineDepth}.");
                    }
                    registration.Stack.Push(nested);
                    continue;
                }
                registration.ResumeFrame = checked(frameGeneration + 1);
                return;
            }
            throw new InvalidOperationException(
                $"JALib coroutine exceeded {MaximumCoroutineTransitionsPerFrame} transitions in one frame.");
        }
        catch (Exception exception)
        {
            registration.Owner?.LogReportException("JALib coroutine failed", exception);
            CompleteCoroutineLocked(registration);
        }
    }

    private static void StopCoroutineLocked(CoroutineRegistration registration)
    {
        if (!Coroutines.Remove(registration))
            return;
        registration.Completed = true;
        registration.Dispose();
        Interlocked.Increment(ref s_stoppedCoroutineCount);
    }

    private static void CompleteCoroutineLocked(CoroutineRegistration registration)
    {
        if (!Coroutines.Remove(registration))
            return;
        registration.Completed = true;
        registration.Dispose();
        Interlocked.Increment(ref s_completedCoroutineCount);
    }

    private static Coroutine CreateCoroutineToken()
    {
        var pointerConstructor = typeof(Coroutine).GetConstructor([typeof(IntPtr)]);
        if (pointerConstructor != null)
            return (Coroutine)pointerConstructor.Invoke([IntPtr.Zero]);
        return (Coroutine)(Activator.CreateInstance(typeof(Coroutine)) ??
                           throw new InvalidOperationException(
                               "UnityEngine.Coroutine token constructor is unavailable."));
    }

    private static void DisposeEnumerator(IEnumerator enumerator)
    {
        if (enumerator is IDisposable disposable)
            disposable.Dispose();
    }

    private static void RequireMainThread(string operation)
    {
        if (!IsMainThread())
        {
            throw new InvalidOperationException(
                $"JALib MainThread.{operation} must run on PcCompat UnityMain.");
        }
    }

    private static void BindMainThread()
    {
        Volatile.Write(ref s_mainThreadId, Environment.CurrentManagedThreadId);
        Volatile.Write(ref s_thread, System.Threading.Thread.CurrentThread);
    }

    private static void UpdateMaximumPending(long pending)
    {
        var observed = Interlocked.Read(ref s_maxPendingCount);
        while (pending > observed)
        {
            var previous = Interlocked.CompareExchange(
                ref s_maxPendingCount,
                pending,
                observed);
            if (previous == observed)
                return;
            observed = previous;
        }
    }

    private readonly record struct OwnerRegistration(long Generation);
    private readonly record struct OwnerCapture(JAMod? Owner, long Generation);
    private sealed record OwnerContext(JAMod Owner, long Generation);
    private readonly record struct WorkItem(JAMod? Owner, long OwnerGeneration, Action Action);
    private readonly record struct WaitItem(
        JAMod? Owner,
        long OwnerGeneration,
        TaskCompletionSource<bool> Completion);

    private sealed class OwnerScope(OwnerContext? previous) : IDisposable
    {
        private OwnerContext? _previous = previous;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            AmbientOwner.Value = _previous;
            _previous = null;
        }
    }

    private sealed class CoroutineRegistration(
        Coroutine token,
        IEnumerator root,
        JAMod? owner,
        long ownerGeneration,
        long frameGeneration) : IDisposable
    {
        public Coroutine Token { get; } = token;
        public IEnumerator Root { get; } = root;
        public JAMod? Owner { get; } = owner;
        public long OwnerGeneration { get; } = ownerGeneration;
        public Stack<IEnumerator> Stack { get; } = new([root]);
        public long ResumeFrame { get; set; } = frameGeneration;
        public bool Completed { get; set; }

        public void Dispose()
        {
            while (Stack.Count != 0)
                DisposeEnumerator(Stack.Pop());
        }
    }
}

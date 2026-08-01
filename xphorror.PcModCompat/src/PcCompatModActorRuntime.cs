using System.Collections.Concurrent;
using System.Diagnostics;

namespace Xphorror.PcModCompat;

public sealed class PcCompatModActorSnapshot
{
    public bool Registered { get; init; }
    public bool Faulted { get; init; }
    public string? Fault { get; init; }
    public int PendingWork { get; init; }
    public int MailboxCapacity { get; init; }
    public int MailboxHighWatermark { get; init; }
    public ulong AcceptedWork { get; init; }
    public ulong CompletedWork { get; init; }
    public ulong RejectedWork { get; init; }
    public ulong YieldedTurns { get; init; }
}

/// <summary>
/// Process-lifetime fixed worker pool with one serial mailbox per logical MOD.
/// Work for one actor never overlaps; different actors may execute in parallel.
/// Actor callbacks must be state-only and must not call Unity APIs.
/// </summary>
public static class PcCompatModActorRuntime
{
    private const int DefaultWorkerCount = 2;
    private const int DefaultMailboxCapacity = 256;
    private const int MaxWorkPerTurn = 64;
    private static readonly long MaxTurnDurationTicks =
        Math.Max(1, Stopwatch.Frequency / 250); // 4 ms cooperative slice.
    private static readonly object RegistryLock = new();
    private static readonly Dictionary<string, Actor> Actors =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<Actor> ReadyActors = new();
    private static readonly SemaphoreSlim ReadySignal = new(0);
    private static readonly Thread[] Workers = CreateWorkers(DefaultWorkerCount);
    private static long s_generation;

    public static PcCompatModActorHandle Register(
        string actorId,
        Action<string>? faultSink = null,
        int mailboxCapacity = DefaultMailboxCapacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (mailboxCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(mailboxCapacity));
        Actor? previous;
        Actor actor;
        lock (RegistryLock)
        {
            Actors.TryGetValue(actorId, out previous);
            actor = new Actor(
                actorId,
                Interlocked.Increment(ref s_generation),
                faultSink,
                mailboxCapacity);
            Actors[actorId] = actor;
        }
        previous?.Dispose("actor registration was replaced");
        return new PcCompatModActorHandle(actor);
    }

    public static void Unregister(PcCompatModActorHandle? handle)
    {
        if (handle?.Actor is not { } actor)
            return;
        lock (RegistryLock)
        {
            if (Actors.TryGetValue(actor.Id, out var current) &&
                ReferenceEquals(current, actor))
                Actors.Remove(actor.Id);
        }
        actor.Dispose("actor was unregistered");
    }

    public static bool TryPost(PcCompatModActorHandle? handle, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return handle?.Actor?.TryPost(work) == true;
    }

    public static PcCompatModActorSnapshot Snapshot(PcCompatModActorHandle? handle)
        => handle?.Actor?.Snapshot() ?? new PcCompatModActorSnapshot();

    public static bool WaitForIdle(PcCompatModActorHandle? handle, TimeSpan timeout)
        => handle?.Actor?.WaitForIdle(timeout) ?? true;

    private static Thread[] CreateWorkers(int count)
    {
        var workers = new Thread[count];
        for (var index = 0; index < workers.Length; ++index)
        {
            workers[index] = new Thread(WorkerMain)
            {
                IsBackground = true,
                Name = $"PcCompat.ModActor.{index}"
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
            if (!ReadyActors.TryDequeue(out var actor))
                continue;
            actor.ExecuteTurn();
        }
    }

    private static void Schedule(Actor actor)
    {
        ReadyActors.Enqueue(actor);
        ReadySignal.Release();
    }

    public sealed class PcCompatModActorHandle
    {
        internal PcCompatModActorHandle(Actor actor) => Actor = actor;
        internal Actor Actor { get; }
        public string Id => Actor.Id;
        public long Generation => Actor.Generation;
    }

    internal sealed class Actor
    {
        private readonly object _gate = new();
        private readonly Queue<Action> _mailbox = new();
        private readonly ManualResetEventSlim _idle = new(initialState: true);
        private readonly Action<string>? _faultSink;
        private readonly int _mailboxCapacity;
        private bool _scheduled;
        private bool _running;
        private bool _disposed;
        private bool _faulted;
        private string? _fault;
        private ulong _acceptedWork;
        private ulong _completedWork;
        private ulong _rejectedWork;
        private ulong _yieldedTurns;
        private int _mailboxHighWatermark;

        public Actor(
            string id,
            long generation,
            Action<string>? faultSink,
            int mailboxCapacity)
        {
            Id = id;
            Generation = generation;
            _faultSink = faultSink;
            _mailboxCapacity = mailboxCapacity;
        }

        public string Id { get; }
        public long Generation { get; }

        public bool TryPost(Action work)
        {
            var schedule = false;
            lock (_gate)
            {
                if (_disposed || _faulted)
                {
                    ++_rejectedWork;
                    return false;
                }
                if (_mailbox.Count >= _mailboxCapacity)
                {
                    ++_rejectedWork;
                    return false;
                }
                _mailbox.Enqueue(work);
                _mailboxHighWatermark = Math.Max(_mailboxHighWatermark, _mailbox.Count);
                ++_acceptedWork;
                _idle.Reset();
                if (!_scheduled && !_running)
                {
                    _scheduled = true;
                    schedule = true;
                }
            }
            if (schedule)
                Schedule(this);
            return true;
        }

        public void ExecuteTurn()
        {
            lock (_gate)
            {
                if (_disposed || _faulted)
                {
                    _scheduled = false;
                    CompleteIdleLocked();
                    return;
                }
                if (_running)
                    return;
                _scheduled = false;
                _running = true;
            }

            Exception? failure = null;
            var processed = 0;
            var turnStarted = Stopwatch.GetTimestamp();
            while (processed < MaxWorkPerTurn)
            {
                Action? work;
                lock (_gate)
                {
                    if (_disposed || _faulted || _mailbox.Count == 0)
                        break;
                    work = _mailbox.Dequeue();
                }
                try
                {
                    work();
                    lock (_gate)
                        ++_completedWork;
                }
                catch (Exception exception)
                {
                    failure = exception;
                    break;
                }
                ++processed;
                if (Stopwatch.GetTimestamp() - turnStarted >= MaxTurnDurationTicks)
                    break;
            }

            string? fault = null;
            var reschedule = false;
            lock (_gate)
            {
                _running = false;
                if (failure != null && !_disposed && !_faulted)
                {
                    _faulted = true;
                    _fault = failure.ToString();
                    _rejectedWork += (ulong)_mailbox.Count;
                    _mailbox.Clear();
                    fault = _fault;
                }
                if (!_disposed && !_faulted && _mailbox.Count != 0 && !_scheduled)
                {
                    ++_yieldedTurns;
                    _scheduled = true;
                    reschedule = true;
                }
                CompleteIdleLocked();
            }
            if (reschedule)
                Schedule(this);
            if (fault != null)
            {
                try
                {
                    _faultSink?.Invoke(fault);
                }
                catch
                {
                    // Actor isolation must not be broken by diagnostics.
                }
            }
        }

        public PcCompatModActorSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new PcCompatModActorSnapshot
                {
                    Registered = !_disposed,
                    Faulted = _faulted,
                    Fault = _fault,
                    PendingWork = _mailbox.Count + (_running ? 1 : 0),
                    MailboxCapacity = _mailboxCapacity,
                    MailboxHighWatermark = _mailboxHighWatermark,
                    AcceptedWork = _acceptedWork,
                    CompletedWork = _completedWork,
                    RejectedWork = _rejectedWork,
                    YieldedTurns = _yieldedTurns
                };
            }
        }

        public bool WaitForIdle(TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(timeout));
            return _idle.Wait(timeout);
        }

        public void Dispose(string reason)
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _rejectedWork += (ulong)_mailbox.Count;
                _mailbox.Clear();
                _fault ??= reason;
                CompleteIdleLocked();
            }
        }

        private void CompleteIdleLocked()
        {
            if (!_running && !_scheduled && _mailbox.Count == 0)
                _idle.Set();
        }
    }
}

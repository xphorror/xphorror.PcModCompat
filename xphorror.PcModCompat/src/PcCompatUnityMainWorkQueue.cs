namespace Xphorror.PcModCompat;

public readonly record struct PcCompatUnityMainWorkQueueSnapshot(
    int Pending,
    int Capacity,
    int HighWatermark,
    long Accepted,
    long Rejected,
    long Executed,
    long Failed);

/// <summary>
/// Bounded MPSC queue whose consumer is called from a proven UnityMain hook.
/// Producers only request a native wakeup when work transitions from idle.
/// </summary>
public sealed class PcCompatUnityMainWorkQueue
{
    private readonly object _gate = new();
    private readonly Queue<Action> _items = new();
    private readonly Action _requestPump;
    private readonly int _capacity;
    private bool _pumpRequested;
    private int _highWatermark;
    private long _accepted;
    private long _rejected;
    private long _executed;
    private long _failed;

    public PcCompatUnityMainWorkQueue(int capacity, Action requestPump)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        ArgumentNullException.ThrowIfNull(requestPump);
        _capacity = capacity;
        _requestPump = requestPump;
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _items.Count;
        }
    }

    public bool TryEnqueue(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            if (_items.Count >= _capacity)
            {
                ++_rejected;
                return false;
            }
            _items.Enqueue(work);
            if (!_pumpRequested)
            {
                _pumpRequested = true;
                try
                {
                    _requestPump();
                }
                catch
                {
                    // A false->true transition is only possible for an empty
                    // queue, so the newly enqueued item is still the head.
                    _items.Dequeue();
                    _pumpRequested = false;
                    ++_rejected;
                    throw;
                }
            }
            ++_accepted;
            _highWatermark = Math.Max(_highWatermark, _items.Count);
            return true;
        }
    }

    public int Drain(int maxItems)
    {
        if (maxItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxItems));

        var drained = 0;
        Exception? workFailure = null;
        while (drained < maxItems)
        {
            Action? work;
            lock (_gate)
            {
                if (_items.Count == 0)
                {
                    _pumpRequested = false;
                    break;
                }
                work = _items.Dequeue();
            }

            try
            {
                work();
                drained++;
                Interlocked.Increment(ref _executed);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failed);
                workFailure = ex;
                break;
            }
        }

        lock (_gate)
        {
            if (_items.Count == 0)
            {
                _pumpRequested = false;
            }
            else
            {
                // Native clears its requested bit before invoking the callback.
                // Rearm exactly once when bounded draining leaves work behind.
                _pumpRequested = true;
                try
                {
                    _requestPump();
                }
                catch
                {
                    // The caller that owns the current pump retries the native
                    // wakeup. Keep the logical request outstanding so a second
                    // producer cannot overtake the retained queue head.
                    _pumpRequested = true;
                    throw;
                }
            }
        }
        if (workFailure != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(workFailure).Throw();
        return drained;
    }

    public PcCompatUnityMainWorkQueueSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new PcCompatUnityMainWorkQueueSnapshot(
                _items.Count,
                _capacity,
                _highWatermark,
                _accepted,
                _rejected,
                Interlocked.Read(ref _executed),
                Interlocked.Read(ref _failed));
        }
    }
}

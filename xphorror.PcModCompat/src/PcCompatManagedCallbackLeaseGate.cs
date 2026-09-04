namespace Xphorror.PcModCompat;

internal interface IPcCompatManagedCallbackLease : IDisposable
{
    void TransferToCurrentThread();
}

internal sealed class PcCompatManagedCallbackLeaseGate
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _retirement = new();
    private readonly Dictionary<Thread, int> _enteredByThread = new(
        ReferenceEqualityComparer.Instance);
    private bool _accepting = true;
    private int _active;

    public CancellationToken RetirementToken => _retirement.Token;

    public bool TryEnter(out IDisposable? lease)
    {
        lock (_gate)
        {
            if (!_accepting)
            {
                lease = null;
                return false;
            }
            ++_active;
            var thread = Thread.CurrentThread;
            _enteredByThread[thread] = _enteredByThread.GetValueOrDefault(thread) + 1;
            lease = new Lease(this, thread);
            return true;
        }
    }

    public bool RetireAndWait(TimeSpan timeout)
    {
        lock (_gate)
        {
            if (_enteredByThread.ContainsKey(Thread.CurrentThread))
            {
                throw new InvalidOperationException(
                    "PcCompat managed session cannot retire from inside its own callback lease.");
            }
        }
        var deadline = timeout == Timeout.InfiniteTimeSpan
            ? long.MaxValue
            : Environment.TickCount64 + Math.Max(0L, (long)timeout.TotalMilliseconds);
        lock (_gate)
        {
            _accepting = false;
            _retirement.Cancel();
            while (_active != 0)
            {
                if (deadline == long.MaxValue)
                {
                    Monitor.Wait(_gate);
                    continue;
                }
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0 || !Monitor.Wait(_gate, TimeSpan.FromMilliseconds(remaining)))
                    return false;
            }
            return true;
        }
    }

    private void Transfer(Thread previousThread, Thread currentThread)
    {
        if (ReferenceEquals(previousThread, currentThread))
            return;
        lock (_gate)
        {
            RemoveThreadEntry(previousThread);
            _enteredByThread[currentThread] =
                _enteredByThread.GetValueOrDefault(currentThread) + 1;
        }
    }

    private void Exit(Thread enteredThread)
    {
        lock (_gate)
        {
            if (_active <= 0)
                throw new InvalidOperationException("PcCompat callback lease underflow.");
            RemoveThreadEntry(enteredThread);
            if (--_active == 0)
                Monitor.PulseAll(_gate);
        }
    }

    private void RemoveThreadEntry(Thread thread)
    {
        if (!_enteredByThread.TryGetValue(thread, out var depth) || depth <= 0)
            throw new InvalidOperationException("PcCompat callback thread ownership underflow.");
        if (depth == 1)
            _enteredByThread.Remove(thread);
        else
            _enteredByThread[thread] = depth - 1;
    }

    private sealed class Lease(
        PcCompatManagedCallbackLeaseGate owner,
        Thread enteredThread) : IPcCompatManagedCallbackLease
    {
        private readonly object _gate = new();
        private PcCompatManagedCallbackLeaseGate? _owner = owner;
        private Thread _enteredThread = enteredThread;

        public void TransferToCurrentThread()
        {
            lock (_gate)
            {
                if (_owner == null)
                    return;
                var currentThread = Thread.CurrentThread;
                _owner.Transfer(_enteredThread, currentThread);
                _enteredThread = currentThread;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                var owner = _owner;
                if (owner == null)
                    return;
                _owner = null;
                owner.Exit(_enteredThread);
            }
        }
    }
}

using JALib.Tools;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public class PcCompatJaTaskTests
{
    [Test]
    public void YieldContinuationUsesCapturedSynchronizationContext()
    {
        var previous = SynchronizationContext.Current;
        var context = new QueuedSynchronizationContext();
        var callbackThread = 0;
        var completed = false;

        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            Task.Yield().OnCompleted(() =>
            {
                callbackThread = Environment.CurrentManagedThreadId;
                completed = true;
            });

            Assert.That(completed, Is.False);
            Assert.That(context.PendingCount, Is.EqualTo(1));

            var dispatchThread = Environment.CurrentManagedThreadId;
            context.DispatchOne();

            Assert.That(completed, Is.True);
            Assert.That(callbackThread, Is.EqualTo(dispatchThread));
            Assert.That(context.PendingCount, Is.Zero);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Test]
    public void YieldContinuationUsesPcCompatUnityMainScheduler()
    {
        var scheduled = new List<Action>();
        var completed = false;
        var sawUnityMain = false;
        PcCompatUnityMainExecutionContext.RegisterContinuationScheduler(work =>
        {
            scheduled.Add(work);
            return true;
        });

        try
        {
            using (PcCompatUnityMainExecutionContext.Enter())
            {
                Task.Yield().OnCompleted(() =>
                {
                    completed = true;
                    sawUnityMain = PcCompatUnityMainExecutionContext.IsActive;
                });
            }

            Assert.That(completed, Is.False);
            Assert.That(scheduled, Has.Count.EqualTo(1));
            scheduled[0]();
            Assert.That(completed, Is.True);
            Assert.That(sawUnityMain, Is.True);
        }
        finally
        {
            PcCompatUnityMainExecutionContext.RegisterContinuationScheduler(null);
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = new();

        public int PendingCount => _pending.Count;

        public override void Post(SendOrPostCallback callback, object? state)
            => _pending.Enqueue((callback, state));

        public void DispatchOne()
        {
            var pending = _pending.Dequeue();
            pending.Callback(pending.State);
        }
    }
}

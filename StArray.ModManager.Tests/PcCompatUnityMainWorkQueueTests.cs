using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public class PcCompatUnityMainWorkQueueTests
{
    [Test]
    public void RequestsOneWakeupForBurstAndRearmsAfterBoundedDrain()
    {
        var wakeups = 0;
        var executed = new List<int>();
        var queue = new PcCompatUnityMainWorkQueue(4, () => wakeups++);

        Assert.That(queue.TryEnqueue(() => executed.Add(1)), Is.True);
        Assert.That(queue.TryEnqueue(() => executed.Add(2)), Is.True);
        Assert.That(queue.TryEnqueue(() => executed.Add(3)), Is.True);
        Assert.That(wakeups, Is.EqualTo(1));

        Assert.That(queue.Drain(2), Is.EqualTo(2));
        Assert.That(executed, Is.EqualTo(new[] { 1, 2 }));
        Assert.That(queue.Count, Is.EqualTo(1));
        Assert.That(wakeups, Is.EqualTo(2));

        Assert.That(queue.Drain(2), Is.EqualTo(1));
        Assert.That(executed, Is.EqualTo(new[] { 1, 2, 3 }));
        Assert.That(queue.Count, Is.Zero);
        Assert.That(queue.Snapshot(), Is.EqualTo(
            new PcCompatUnityMainWorkQueueSnapshot(
                Pending: 0,
                Capacity: 4,
                HighWatermark: 3,
                Accepted: 3,
                Rejected: 0,
                Executed: 3,
                Failed: 0)));
    }

    [Test]
    public void RejectsWorkAtCapacityWithoutRequestingAnotherWakeup()
    {
        var wakeups = 0;
        var queue = new PcCompatUnityMainWorkQueue(1, () => wakeups++);

        Assert.That(queue.TryEnqueue(() => { }), Is.True);
        Assert.That(queue.TryEnqueue(() => { }), Is.False);
        Assert.That(queue.Count, Is.EqualTo(1));
        Assert.That(wakeups, Is.EqualTo(1));
        Assert.That(queue.Snapshot().Rejected, Is.EqualTo(1));
    }

    [Test]
    public void RollsBackFirstItemWhenNativeWakeupFails()
    {
        var queue = new PcCompatUnityMainWorkQueue(
            2,
            () => throw new InvalidOperationException("native hook unavailable"));

        Assert.Throws<InvalidOperationException>(() => queue.TryEnqueue(() => { }));
        Assert.That(queue.Count, Is.Zero);
        Assert.That(queue.Snapshot().Rejected, Is.EqualTo(1));
    }

    [Test]
    public void WorkFailureRestoresIdleStateForNextProducer()
    {
        var wakeups = 0;
        var queue = new PcCompatUnityMainWorkQueue(2, () => wakeups++);
        Assert.That(queue.TryEnqueue(() => throw new InvalidOperationException("work failed")), Is.True);

        Assert.Throws<InvalidOperationException>(() => queue.Drain(1));
        Assert.That(queue.Count, Is.Zero);
        Assert.That(queue.TryEnqueue(() => { }), Is.True);
        Assert.That(wakeups, Is.EqualTo(2));
        Assert.That(queue.Snapshot().Failed, Is.EqualTo(1));
    }
}

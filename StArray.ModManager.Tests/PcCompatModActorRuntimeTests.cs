using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatModActorRuntimeTests
{
    [Test]
    public void OneActorExecutesItsMailboxInOrderWithoutOverlap()
    {
        var actor = PcCompatModActorRuntime.Register("actor-order-" + Guid.NewGuid());
        try
        {
            var observed = new List<int>();
            var active = 0;
            var maxActive = 0;
            for (var index = 0; index < 200; ++index)
            {
                var captured = index;
                Assert.That(PcCompatModActorRuntime.TryPost(actor, () =>
                {
                    var nowActive = Interlocked.Increment(ref active);
                    maxActive = Math.Max(maxActive, nowActive);
                    observed.Add(captured);
                    Interlocked.Decrement(ref active);
                }), Is.True);
            }

            Assert.That(PcCompatModActorRuntime.WaitForIdle(
                actor, TimeSpan.FromSeconds(2)), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(maxActive, Is.EqualTo(1));
                Assert.That(observed, Is.EqualTo(Enumerable.Range(0, 200)));
                Assert.That(PcCompatModActorRuntime.Snapshot(actor).CompletedWork,
                    Is.EqualTo(200));
            });
        }
        finally
        {
            PcCompatModActorRuntime.Unregister(actor);
        }
    }

    [Test]
    public void DifferentActorsCanRunInParallelOnTheFixedPool()
    {
        var first = PcCompatModActorRuntime.Register("actor-parallel-a-" + Guid.NewGuid());
        var second = PcCompatModActorRuntime.Register("actor-parallel-b-" + Guid.NewGuid());
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        try
        {
            Assert.That(PcCompatModActorRuntime.TryPost(first, Block), Is.True);
            Assert.That(PcCompatModActorRuntime.TryPost(second, Block), Is.True);
            Assert.That(entered.Wait(TimeSpan.FromSeconds(2)), Is.True,
                "both actors must enter before either is released");
            release.Set();
            Assert.That(PcCompatModActorRuntime.WaitForIdle(
                first, TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(PcCompatModActorRuntime.WaitForIdle(
                second, TimeSpan.FromSeconds(2)), Is.True);
        }
        finally
        {
            release.Set();
            PcCompatModActorRuntime.Unregister(first);
            PcCompatModActorRuntime.Unregister(second);
        }
        return;

        void Block()
        {
            entered.Signal();
            release.Wait(TimeSpan.FromSeconds(2));
        }
    }

    [Test]
    public void CallbackFailureFaultsOnlyItsOwnerActor()
    {
        string? failure = null;
        var bad = PcCompatModActorRuntime.Register(
            "actor-fault-a-" + Guid.NewGuid(),
            value => failure = value);
        var good = PcCompatModActorRuntime.Register("actor-fault-b-" + Guid.NewGuid());
        var goodRuns = 0;
        try
        {
            Assert.That(PcCompatModActorRuntime.TryPost(
                bad, () => throw new InvalidOperationException("expected actor failure")), Is.True);
            Assert.That(PcCompatModActorRuntime.TryPost(
                good, () => Interlocked.Increment(ref goodRuns)), Is.True);
            Assert.That(PcCompatModActorRuntime.WaitForIdle(
                bad, TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(PcCompatModActorRuntime.WaitForIdle(
                good, TimeSpan.FromSeconds(2)), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(PcCompatModActorRuntime.Snapshot(bad).Faulted, Is.True);
                Assert.That(failure, Does.Contain("expected actor failure"));
                Assert.That(PcCompatModActorRuntime.TryPost(bad, () => { }), Is.False);
                Assert.That(goodRuns, Is.EqualTo(1));
                Assert.That(PcCompatModActorRuntime.Snapshot(good).Faulted, Is.False);
            });
        }
        finally
        {
            PcCompatModActorRuntime.Unregister(bad);
            PcCompatModActorRuntime.Unregister(good);
        }
    }

    [Test]
    public void FullMailboxRejectsNewWorkWithoutBlockingTheProducer()
    {
        var actor = PcCompatModActorRuntime.Register(
            "actor-capacity-" + Guid.NewGuid(),
            mailboxCapacity: 2);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            Assert.That(PcCompatModActorRuntime.TryPost(actor, () =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(2));
            }), Is.True);
            Assert.That(entered.Wait(TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(PcCompatModActorRuntime.TryPost(actor, () => { }), Is.True);
            Assert.That(PcCompatModActorRuntime.TryPost(actor, () => { }), Is.True);
            Assert.That(PcCompatModActorRuntime.TryPost(actor, () => { }), Is.False);

            var saturated = PcCompatModActorRuntime.Snapshot(actor);
            Assert.Multiple(() =>
            {
                Assert.That(saturated.MailboxCapacity, Is.EqualTo(2));
                Assert.That(saturated.MailboxHighWatermark, Is.EqualTo(2));
                Assert.That(saturated.RejectedWork, Is.EqualTo(1));
            });

            release.Set();
            Assert.That(PcCompatModActorRuntime.WaitForIdle(
                actor, TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(PcCompatModActorRuntime.Snapshot(actor).CompletedWork,
                Is.EqualTo(3));
        }
        finally
        {
            release.Set();
            PcCompatModActorRuntime.Unregister(actor);
        }
    }
}

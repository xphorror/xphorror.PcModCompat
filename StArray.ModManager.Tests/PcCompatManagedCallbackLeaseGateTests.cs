using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[TestFixture]
public sealed class PcCompatManagedCallbackLeaseGateTests
{
    [Test]
    public async Task RetirementWaitsForEnteredCallbackThenRejectsNewCallbacks()
    {
        var gate = new PcCompatManagedCallbackLeaseGate();
        Assert.That(gate.TryEnter(out var lease), Is.True);

        var retirement = Task.Run(() => gate.RetireAndWait(TimeSpan.FromSeconds(2)));
        await Task.Delay(30);
        Assert.That(retirement.IsCompleted, Is.False);

        lease!.Dispose();
        Assert.That(await retirement, Is.True);
        Assert.That(gate.TryEnter(out _), Is.False);
        Assert.That(gate.RetirementToken.IsCancellationRequested, Is.True);
    }

    [Test]
    public void RetirementInsideOwnedCallbackFailsInsteadOfDeadlocking()
    {
        var gate = new PcCompatManagedCallbackLeaseGate();
        Assert.That(gate.TryEnter(out var lease), Is.True);
        using (lease)
        {
            Assert.That(
                () => gate.RetireAndWait(TimeSpan.FromMilliseconds(10)),
                Throws.InvalidOperationException.With.Message.Contains("inside its own callback"));
        }
    }

    [Test]
    public async Task TransferredLeaseMovesRetirementOwnershipAndQuiescesOnDestinationExit()
    {
        var gate = new PcCompatManagedCallbackLeaseGate();
        Assert.That(gate.TryEnter(out var lease), Is.True);
        var transferred = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var destination = Task.Run(async () =>
        {
            ((IPcCompatManagedCallbackLease)lease!).TransferToCurrentThread();
            transferred.SetResult();
            await release.Task;
            lease.Dispose();
        });
        await transferred.Task;

        Assert.That(gate.RetireAndWait(TimeSpan.FromMilliseconds(10)), Is.False);
        release.SetResult();
        await destination;
        Assert.That(gate.RetireAndWait(TimeSpan.FromSeconds(1)), Is.True);
    }

    [Test]
    public void ManagedCallbackScopeIsReusableOnlyWhileTheOuterLeaseIsActive()
    {
        var state = new PcCompatManagedExecutionState(
            "callback-scope-owner",
            4,
            PcCompatManagedExecutionPhase.Update);
        var gate = new PcCompatManagedCallbackLeaseGate();
        Assert.That(gate.TryEnter(out var lease), Is.True);

        using (PcCompatManagedExecutionContext.EnterCallback(
                   state,
                   lease!,
                   CancellationToken.None))
        {
            Assert.That(
                PcCompatManagedExecutionContext.HasReusableCallback(state),
                Is.True);
        }

        Assert.That(
            PcCompatManagedExecutionContext.HasReusableCallback(state),
            Is.False);
        Assert.That(gate.TryEnter(out var afterExit), Is.True);
        afterExit!.Dispose();
    }
}

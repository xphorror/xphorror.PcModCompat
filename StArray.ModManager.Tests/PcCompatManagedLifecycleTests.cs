using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public class PcCompatManagedLifecycleTests
{
    [Test]
    public void DispatchesEnableUpdateDisableExactlyOnce()
    {
        var target = new LifecycleTarget();
        var lifecycle = new PcCompatManagedLifecycleController(target);

        Assert.That(lifecycle.TryEnable(out var error), Is.True, error);
        Assert.That(lifecycle.TryEnable(out error), Is.True, error);
        Assert.That(lifecycle.RequiresFrameDispatch, Is.True);
        Assert.That(lifecycle.TryDispatchUpdate(0.125f), Is.True);
        lifecycle.Disable();
        lifecycle.Disable();

        Assert.That(target.EnableCount, Is.EqualTo(1));
        Assert.That(target.UpdateCount, Is.EqualTo(1));
        Assert.That(target.DisableCount, Is.EqualTo(1));
        Assert.That(target.LastDeltaTime, Is.EqualTo(0.125f));
        Assert.That(lifecycle.Snapshot().State, Is.EqualTo(PcCompatManagedLifecycleState.Disabled));
        Assert.That(lifecycle.Snapshot().UpdateCount, Is.EqualTo(1));
    }

    [Test]
    public void FaultsOnlyTheThrowingLifecycleAndRunsCleanup()
    {
        var target = new LifecycleTarget { ThrowOnUpdate = true };
        var lifecycle = new PcCompatManagedLifecycleController(target);

        Assert.That(lifecycle.TryEnable(out var error), Is.True, error);
        Assert.That(lifecycle.TryDispatchUpdate(0.016f), Is.False);

        var snapshot = lifecycle.Snapshot();
        Assert.That(snapshot.State, Is.EqualTo(PcCompatManagedLifecycleState.Faulted));
        Assert.That(snapshot.FaultCount, Is.EqualTo(1));
        Assert.That(snapshot.LastError, Does.Contain("update failure"));
        Assert.That(target.DisableCount, Is.EqualTo(1));
    }

    [Test]
    public void FaultCleanupRunsInDisableOwnerPhase()
    {
        var target = new LifecycleTarget { ThrowOnUpdate = true };
        var lifecycle = new PcCompatManagedLifecycleController(target);
        var updateContext = new PcCompatManagedExecutionState(
            "test.mod",
            9,
            PcCompatManagedExecutionPhase.Update);

        Assert.That(lifecycle.TryEnable(out var error), Is.True, error);
        using (PcCompatManagedExecutionContext.Enter(updateContext))
            Assert.That(lifecycle.TryDispatchUpdate(0.016f), Is.False);

        Assert.That(target.DisablePhase, Is.EqualTo(PcCompatManagedExecutionPhase.Disable));
        Assert.That(target.DisableModId, Is.EqualTo("test.mod"));
    }

    [Test]
    public void RejectsReentrantLifecycleCallbacks()
    {
        PcCompatManagedLifecycleController? lifecycle = null;
        var target = new LifecycleTarget
        {
            UpdateAction = () => lifecycle!.TryDispatchUpdate(0.01f)
        };
        lifecycle = new PcCompatManagedLifecycleController(target);

        Assert.That(lifecycle.TryEnable(out var error), Is.True, error);
        Assert.That(lifecycle.TryDispatchUpdate(0.016f), Is.False);
        Assert.That(lifecycle.Snapshot().LastError, Does.Contain("re-entry"));
        Assert.That(target.DisableCount, Is.EqualTo(1));
    }

    [Test]
    public void DisableBeforeEnableDoesNotInvokeModCleanup()
    {
        var target = new LifecycleTarget();
        var lifecycle = new PcCompatManagedLifecycleController(target);

        lifecycle.Disable();

        Assert.That(target.DisableCount, Is.Zero);
        Assert.That(lifecycle.Snapshot().State, Is.EqualTo(PcCompatManagedLifecycleState.Disabled));
    }

    [Test]
    public void SteadyStateUpdateDispatchIsAllocationFree()
    {
        var target = new LifecycleTarget();
        var lifecycle = new PcCompatManagedLifecycleController(target);
        Assert.That(lifecycle.TryEnable(out var error), Is.True, error);

        for (var index = 0; index < 200; ++index)
            Assert.That(lifecycle.TryDispatchUpdate(1f / 60f), Is.True);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 2000; ++index)
            lifecycle.TryDispatchUpdate(1f / 60f);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.That(allocated, Is.LessThan(1024));
    }

    private sealed class LifecycleTarget
    {
        public int EnableCount { get; private set; }
        public int UpdateCount { get; private set; }
        public int DisableCount { get; private set; }
        public float LastDeltaTime { get; private set; }
        public bool ThrowOnUpdate { get; init; }
        public Action? UpdateAction { get; init; }
        public PcCompatManagedExecutionPhase? DisablePhase { get; private set; }
        public string? DisableModId { get; private set; }

        public void CompatEnable() => EnableCount++;

        public void CompatUpdate(float deltaTime)
        {
            UpdateCount++;
            LastDeltaTime = deltaTime;
            UpdateAction?.Invoke();
            if (ThrowOnUpdate)
                throw new InvalidOperationException("update failure");
        }

        public void CompatDisable()
        {
            DisableCount++;
            DisablePhase = PcCompatManagedExecutionContext.Current?.Phase;
            DisableModId = PcCompatManagedExecutionContext.Current?.ModId;
        }
    }
}

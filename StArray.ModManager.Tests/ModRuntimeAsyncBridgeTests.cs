using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class ModRuntimeAsyncBridgeTests
{
    [SetUp]
    public void SetUp() => ModOwnedResourceRegistry.ClearForTests();

    [TearDown]
    public void TearDown() => ModOwnedResourceRegistry.ClearForTests();

    [Test]
    public void IncompleteTaskIsTrackedWithoutChangingTaskIdentity()
    {
        var runtime = CreateActiveRuntime("tracked-task");
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task tracked;
        using (HookHelper.EnterOwnerScope(runtime.Key.OwnerId, runtime.Session, runtime.Key))
            tracked = ModRuntimeAsyncBridge.TrackTask(completion.Task, "fixture-task");

        Assert.Multiple(() =>
        {
            Assert.That(tracked, Is.SameAs(completion.Task));
            Assert.That(runtime.Session.Snapshot().ActiveOperations, Is.EqualTo(1));
        });
        Assert.That(runtime.Session.TryBeginRetirement(runtime.Key), Is.True);
        Assert.That(
            runtime.Session.SnapshotOwnedOperations(runtime.Key).Single().CancellationRequested,
            Is.True);
        Assert.That(runtime.Session.WaitForQuiescence(runtime.Key, TimeSpan.Zero), Is.False);

        completion.SetResult();
        Assert.That(
            SpinWait.SpinUntil(
                () => runtime.Session.Snapshot().ActiveOperations == 0,
                TimeSpan.FromSeconds(2)),
            Is.True);
        Assert.That(runtime.Session.WaitForQuiescence(runtime.Key, TimeSpan.Zero), Is.True);
        Assert.That(runtime.Session.TryCompleteRetirement(runtime.Key), Is.True);
    }

    [Test]
    public void TaskRunRestoresOwnerScopeAndRetirementWaitsForCallback()
    {
        var runtime = CreateActiveRuntime("task-run");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        ModRuntimeKey observed = default;
        Task task;
        using (HookHelper.EnterOwnerScope(runtime.Key.OwnerId, runtime.Session, runtime.Key))
        {
            task = ModRuntimeAsyncBridge.RunAction(() =>
            {
                observed = HookHelper.CurrentRuntimeKey;
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            });
        }

        Assert.That(entered.Wait(TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(runtime.Session.TryBeginRetirement(runtime.Key), Is.True);
        Assert.That(runtime.Session.WaitForQuiescence(runtime.Key, TimeSpan.Zero), Is.False);
        release.Set();
        Assert.DoesNotThrow(() => task.GetAwaiter().GetResult());
        Assert.That(runtime.Session.WaitForQuiescence(runtime.Key, TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(observed.Matches(runtime.Key), Is.True);
        Assert.That(runtime.Session.TryCompleteRetirement(runtime.Key), Is.True);
    }

    [Test]
    public void ThreadOnlyBeginsOperationWhenStarted()
    {
        var runtime = CreateActiveRuntime("thread");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        ModRuntimeKey observed = default;
        Thread dormant;
        Thread started;
        using (HookHelper.EnterOwnerScope(runtime.Key.OwnerId, runtime.Session, runtime.Key))
        {
            dormant = ModRuntimeAsyncBridge.CreateThread(() => { });
            started = ModRuntimeAsyncBridge.CreateThread(() =>
            {
                observed = HookHelper.CurrentRuntimeKey;
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            });
        }

        Assert.That(runtime.Session.Snapshot().ActiveOperations, Is.Zero);
        ModRuntimeAsyncBridge.StartThread(started);
        Assert.That(entered.Wait(TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(runtime.Session.Snapshot().ActiveOperations, Is.EqualTo(1));
        Assert.That(runtime.Session.TryBeginRetirement(runtime.Key), Is.True);
        Assert.That(runtime.Session.WaitForQuiescence(runtime.Key, TimeSpan.Zero), Is.False);
        release.Set();
        Assert.That(started.Join(TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(runtime.Session.WaitForQuiescence(runtime.Key, TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(observed.Matches(runtime.Key), Is.True);
        Assert.That(runtime.Session.TryCompleteRetirement(runtime.Key), Is.True);
        Assert.That(dormant.ThreadState, Is.EqualTo(ThreadState.Unstarted));
    }

    [Test]
    public void ThreadPoolCallbackCarriesGenerationScope()
    {
        var runtime = CreateActiveRuntime("thread-pool");
        using var completed = new ManualResetEventSlim();
        ModRuntimeKey observed = default;
        using (HookHelper.EnterOwnerScope(runtime.Key.OwnerId, runtime.Session, runtime.Key))
        {
            Assert.That(ModRuntimeAsyncBridge.QueueWaitCallback(_ =>
            {
                observed = HookHelper.CurrentRuntimeKey;
                completed.Set();
            }), Is.True);
        }

        Assert.That(completed.Wait(TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(
            SpinWait.SpinUntil(
                () => runtime.Session.Snapshot().ActiveOperations == 0,
                TimeSpan.FromSeconds(2)),
            Is.True);
        Assert.That(observed.Matches(runtime.Key), Is.True);
        Retire(runtime);
    }

    [Test]
    public void TimerCallbackIsTrackedAndTerminalRetirementDisposesTimer()
    {
        var runtime = CreateActiveRuntime("timer");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Timer timer;
        using (HookHelper.EnterOwnerScope(runtime.Key.OwnerId, runtime.Session, runtime.Key))
        {
            timer = ModRuntimeAsyncBridge.CreateTimerInt32(
                _ =>
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                },
                null,
                0,
                Timeout.Infinite);
        }

        Assert.That(entered.Wait(TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(runtime.Session.TryBeginRetirement(runtime.Key), Is.True);
        Assert.That(runtime.Session.WaitForQuiescence(runtime.Key, TimeSpan.Zero), Is.False);
        release.Set();
        Assert.That(runtime.Session.WaitForQuiescence(runtime.Key, TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(runtime.Session.TryCompleteRetirement(runtime.Key), Is.True);
        Assert.Throws<InvalidOperationException>(() => ModRuntimeAsyncBridge.DisposeTimer(timer));
    }

    [Test]
    public void CapturedExecutionContextCannotReenterClosedDomain()
    {
        var runtime = CreateActiveRuntime("stale-context");
        ExecutionContext context;
        using (HookHelper.EnterOwnerScope(runtime.Key.OwnerId, runtime.Session, runtime.Key))
            context = ExecutionContext.Capture()!;
        Retire(runtime);

        Exception? failure = null;
        ExecutionContext.Run(
            context,
            _ => failure = Assert.Catch(() => ModRuntimeAsyncBridge.RequireCurrentScope()),
            null);
        Assert.That(failure, Is.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void ThreadAndTimerRejectAnotherOwnerScope()
    {
        var owner = CreateActiveRuntime("owned-scheduler");
        var other = CreateActiveRuntime("foreign-scheduler");
        Thread thread;
        Timer timer;
        using (HookHelper.EnterOwnerScope(owner.Key.OwnerId, owner.Session, owner.Key))
        {
            thread = ModRuntimeAsyncBridge.CreateThread(() => { });
            timer = ModRuntimeAsyncBridge.CreateTimerInt32(
                _ => { },
                null,
                Timeout.Infinite,
                Timeout.Infinite);
        }

        using (HookHelper.EnterOwnerScope(other.Key.OwnerId, other.Session, other.Key))
        {
            Assert.Throws<InvalidOperationException>(() =>
                ModRuntimeAsyncBridge.StartThread(thread));
            Assert.Throws<InvalidOperationException>(() =>
                ModRuntimeAsyncBridge.DisposeTimer(timer));
        }

        Assert.That(thread.ThreadState, Is.EqualTo(ThreadState.Unstarted));
        ModRuntimeAsyncBridge.StartThread(thread);
        Assert.That(thread.Join(TimeSpan.FromSeconds(2)), Is.True);
        ModRuntimeAsyncBridge.DisposeTimer(timer);
        Retire(other);
        Retire(owner);
    }

    [Test]
    public async Task ParallelForRestoresOwnerScopeAndRetirementCancelsTheRange()
    {
        var runtime = CreateActiveRuntime("parallel-for");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var observed = new System.Collections.Concurrent.ConcurrentBag<ModRuntimeKey>();
        var first = 0;
        var cancelled = false;

        var task = Task.Run(() =>
        {
            using (HookHelper.EnterOwnerScope(runtime.Key.OwnerId, runtime.Session, runtime.Key))
            {
                try
                {
                    ModRuntimeAsyncBridge.ParallelFor(
                        0,
                        128,
                        index =>
                        {
                            observed.Add(HookHelper.CurrentRuntimeKey);
                            if (Interlocked.Exchange(ref first, 1) == 0)
                            {
                                entered.Set();
                                release.Wait(TimeSpan.FromSeconds(5));
                            }
                        });
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
            }
        });

        Assert.That(entered.Wait(TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(runtime.Session.Snapshot().ActiveOperations, Is.GreaterThanOrEqualTo(1));
        Assert.That(runtime.Session.TryBeginRetirement(runtime.Key), Is.True);
        Assert.That(runtime.Session.WaitForQuiescence(runtime.Key, TimeSpan.Zero), Is.False);

        release.Set();
        await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(
            SpinWait.SpinUntil(
                () => runtime.Session.Snapshot().ActiveOperations == 0,
                TimeSpan.FromSeconds(2)),
            Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(cancelled, Is.True);
            Assert.That(observed, Is.Not.Empty);
            Assert.That(observed.All(key => key.Matches(runtime.Key)), Is.True);
        });
        Assert.That(runtime.Session.WaitForQuiescence(runtime.Key, TimeSpan.Zero), Is.True);
        Assert.That(runtime.Session.TryCompleteRetirement(runtime.Key), Is.True);
    }

    [Test]
    public void ParallelForPreservesCallerCancellation()
    {
        var runtime = CreateActiveRuntime("parallel-for-cancellation");
        using var cancellation = new CancellationTokenSource();
        try
        {
            using (HookHelper.EnterOwnerScope(runtime.Key.OwnerId, runtime.Session, runtime.Key))
            {
                Assert.Throws<OperationCanceledException>(() =>
                    ModRuntimeAsyncBridge.ParallelForWithOptions(
                        0,
                        128,
                        new ParallelOptions
                        {
                            CancellationToken = cancellation.Token,
                            MaxDegreeOfParallelism = 1
                        },
                        index => cancellation.Cancel()));
            }

            Assert.That(runtime.Session.Snapshot().ActiveOperations, Is.Zero);
        }
        finally
        {
            Retire(runtime);
        }
    }

    [Test]
    public async Task PeriodicTimerWaitIsRetirementCancelableAndOwned()
    {
        var runtime = CreateActiveRuntime("periodic-timer");
        PeriodicTimer timer;
        ValueTask<bool> pending;
        using (HookHelper.EnterOwnerScope(runtime.Key.OwnerId, runtime.Session, runtime.Key))
        {
            timer = ModRuntimeAsyncBridge.CreatePeriodicTimer(TimeSpan.FromHours(1));
            pending = ModRuntimeAsyncBridge.WaitForNextTickAsync(timer);
        }

        Assert.That(runtime.Session.Snapshot().ActiveOperations, Is.EqualTo(1));
        Assert.That(runtime.Session.TryBeginRetirement(runtime.Key), Is.True);
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.That(runtime.Session.WaitForQuiescence(runtime.Key, TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(runtime.Session.TryCompleteRetirement(runtime.Key), Is.True);
        Assert.Throws<InvalidOperationException>(() =>
            ModRuntimeAsyncBridge.DisposePeriodicTimer(timer));
    }

    private static (ModRuntimeSession Session, ModRuntimeKey Key) CreateActiveRuntime(string id)
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, id);
        Assert.That(session.TryPublishActive(key), Is.True);
        return (session, key);
    }

    private static void Retire((ModRuntimeSession Session, ModRuntimeKey Key) runtime)
    {
        Assert.That(runtime.Session.TryBeginRetirement(runtime.Key), Is.True);
        Assert.That(runtime.Session.WaitForQuiescence(runtime.Key, TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(runtime.Session.TryCompleteRetirement(runtime.Key), Is.True);
    }
}

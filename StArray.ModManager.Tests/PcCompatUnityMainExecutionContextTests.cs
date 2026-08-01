using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatUnityMainExecutionContextTests
{
    [Test]
    public void TracksNestedScopesWithoutLeakingAfterDispose()
    {
        Assert.That(PcCompatUnityMainExecutionContext.IsActive, Is.False);
        using (PcCompatUnityMainExecutionContext.Enter())
        {
            Assert.That(PcCompatUnityMainExecutionContext.IsActive, Is.True);
            using (PcCompatUnityMainExecutionContext.Enter())
                Assert.That(PcCompatUnityMainExecutionContext.IsActive, Is.True);
            Assert.That(PcCompatUnityMainExecutionContext.IsActive, Is.True);
        }
        Assert.That(PcCompatUnityMainExecutionContext.IsActive, Is.False);
    }

    [Test]
    public void DoesNotLeakUnityMainScopeToAnotherThread()
    {
        bool? workerActive = null;
        using (PcCompatUnityMainExecutionContext.Enter())
        {
            var worker = new Thread(() =>
                workerActive = PcCompatUnityMainExecutionContext.IsActive);
            worker.Start();
            worker.Join();
            Assert.That(PcCompatUnityMainExecutionContext.IsActive, Is.True);
        }

        Assert.That(workerActive, Is.False);
        Assert.That(PcCompatUnityMainExecutionContext.IsActive, Is.False);
    }

    [Test]
    public void InstallsAndRestoresAUnityMainSynchronizationContext()
    {
        var previous = SynchronizationContext.Current;

        using (PcCompatUnityMainExecutionContext.Enter())
        {
            Assert.That(SynchronizationContext.Current, Is.Not.Null);
            Assert.That(SynchronizationContext.Current, Is.Not.SameAs(previous));
            Assert.That(
                SynchronizationContext.Current!.GetType(),
                Is.Not.EqualTo(typeof(SynchronizationContext)));
        }

        Assert.That(SynchronizationContext.Current, Is.SameAs(previous));
    }

    [Test]
    public void TaskYieldContinuationIsQueuedAndReentersUnityMain()
    {
        var scheduled = new List<Action>();
        var callbackRan = false;
        var callbackSawUnityMain = false;
        PcCompatUnityMainExecutionContext.RegisterContinuationScheduler(work =>
        {
            scheduled.Add(work);
            return true;
        });

        try
        {
            using (PcCompatUnityMainExecutionContext.Enter())
            {
                Task.Yield().GetAwaiter().OnCompleted(() =>
                {
                    callbackRan = true;
                    callbackSawUnityMain = PcCompatUnityMainExecutionContext.IsActive;
                });
            }

            Assert.That(callbackRan, Is.False);
            Assert.That(scheduled, Has.Count.EqualTo(1));
            scheduled[0]();
            Assert.That(callbackRan, Is.True);
            Assert.That(callbackSawUnityMain, Is.True);
            Assert.That(PcCompatUnityMainExecutionContext.IsActive, Is.False);
        }
        finally
        {
            PcCompatUnityMainExecutionContext.RegisterContinuationScheduler(null);
        }
    }

    [Test]
    public void RejectedContinuationDoesNotEscapeSynchronizationContextPost()
    {
        PcCompatUnityMainExecutionContext.RegisterContinuationScheduler(_ => false);
        try
        {
            using var scope = PcCompatUnityMainExecutionContext.Enter();
            Assert.DoesNotThrow(() =>
                Task.Yield().GetAwaiter().OnCompleted(() => { }));
        }
        finally
        {
            PcCompatUnityMainExecutionContext.RegisterContinuationScheduler(null);
        }
    }

    [Test]
    public void ManagedOwnerIsRetainedWhenPostOriginatesOnWorkerThread()
    {
        var scheduled = new List<Action>();
        var callbackRan = false;
        Exception? workerError = null;
        SynchronizationContext? captured = null;
        PcCompatUnityMainExecutionContext.RegisterContinuationScheduler(work =>
        {
            scheduled.Add(work);
            return true;
        });

        try
        {
            var staleOwner = new PcCompatManagedExecutionState(
                "missing.worker.mod",
                456,
                PcCompatManagedExecutionPhase.Update);
            using (PcCompatUnityMainExecutionContext.Enter())
            using (PcCompatManagedExecutionContext.Enter(staleOwner))
                captured = SynchronizationContext.Current;

            Assert.That(captured, Is.Not.Null);
            var worker = new Thread(() =>
            {
                try
                {
                    captured!.Post(_ => callbackRan = true, null);
                }
                catch (Exception exception)
                {
                    workerError = exception;
                }
            });
            worker.Start();
            worker.Join();

            Assert.Multiple(() =>
            {
                Assert.That(workerError, Is.Null);
                Assert.That(scheduled, Has.Count.EqualTo(1));
            });
            scheduled[0]();
            Assert.That(callbackRan, Is.False);
        }
        finally
        {
            PcCompatUnityMainExecutionContext.RegisterContinuationScheduler(null);
        }
    }

    [Test]
    public void MissingSchedulerDoesNotThrowFromWorkerPost()
    {
        Exception? workerError = null;
        SynchronizationContext? captured = null;
        var staleOwner = new PcCompatManagedExecutionState(
            "missing.scheduler.mod",
            789,
            PcCompatManagedExecutionPhase.Update);
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(staleOwner))
            captured = SynchronizationContext.Current;

        Assert.That(captured, Is.Not.Null);
        PcCompatUnityMainExecutionContext.RegisterContinuationScheduler(null);
        var worker = new Thread(() =>
        {
            try
            {
                captured!.Post(_ => { }, null);
            }
            catch (Exception exception)
            {
                workerError = exception;
            }
        });
        worker.Start();
        worker.Join();

        Assert.That(workerError, Is.Null);
    }

    [Test]
    public void ClonedExecutionPhaseGetsItsOwnOwnerBoundContext()
    {
        SynchronizationContext? updateContext;
        SynchronizationContext? disableContext;
        var updateOwner = new PcCompatManagedExecutionState(
            "phase.clone.mod",
            987,
            PcCompatManagedExecutionPhase.Update);

        using (PcCompatUnityMainExecutionContext.Enter())
        {
            using (PcCompatManagedExecutionContext.Enter(updateOwner))
                updateContext = SynchronizationContext.Current;

            var disableOwner = updateOwner with
            {
                Phase = PcCompatManagedExecutionPhase.Disable
            };
            using (PcCompatManagedExecutionContext.Enter(disableOwner))
                disableContext = SynchronizationContext.Current;
        }

        Assert.Multiple(() =>
        {
            Assert.That(updateContext, Is.Not.Null);
            Assert.That(disableContext, Is.Not.Null);
            Assert.That(disableContext, Is.Not.SameAs(updateContext));
        });
    }

    [Test]
    public void ContinuationExceptionIsContainedOnUnityMain()
    {
        var scheduled = new List<Action>();
        PcCompatUnityMainExecutionContext.RegisterContinuationScheduler(work =>
        {
            scheduled.Add(work);
            return true;
        });

        try
        {
            using (PcCompatUnityMainExecutionContext.Enter())
                Task.Yield().GetAwaiter().OnCompleted(
                    () => throw new InvalidOperationException("continuation failed"));

            Assert.That(scheduled, Has.Count.EqualTo(1));
            Assert.DoesNotThrow(scheduled[0].Invoke);
        }
        finally
        {
            PcCompatUnityMainExecutionContext.RegisterContinuationScheduler(null);
        }
    }

    [Test]
    public void DropsContinuationAfterItsManagedOwnerIsGone()
    {
        var scheduled = new List<Action>();
        var callbackRan = false;
        PcCompatUnityMainExecutionContext.RegisterContinuationScheduler(work =>
        {
            scheduled.Add(work);
            return true;
        });

        try
        {
            var staleOwner = new PcCompatManagedExecutionState(
                "missing.mod",
                123,
                PcCompatManagedExecutionPhase.Enable);
            using (PcCompatUnityMainExecutionContext.Enter())
            using (PcCompatManagedExecutionContext.Enter(staleOwner))
                Task.Yield().GetAwaiter().OnCompleted(() => callbackRan = true);

            Assert.That(scheduled, Has.Count.EqualTo(1));
            scheduled[0]();
            Assert.That(callbackRan, Is.False);
        }
        finally
        {
            PcCompatUnityMainExecutionContext.RegisterContinuationScheduler(null);
        }
    }

    [Test]
    public void AndroidUnityMainCallbacksEnterTheSharedScope()
    {
        var root = FindRepositoryRoot();
        var resourceLoader = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatResourceBundleLoader.cs"));
        var selfRender = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatManagedSelfRenderBridge.cs"));

        AssertCallbackEntersScope(resourceLoader, "private static void OnUnityMainWork()");
        AssertCallbackEntersScope(selfRender, "private static void OnManagedFrame()");
        Assert.That(
            resourceLoader,
            Does.Contain("PcCompatUnityMainExecutionContext.RegisterContinuationScheduler(")
                .And.Contain("TryScheduleUnityMainContinuation"));
    }

    private static void AssertCallbackEntersScope(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), signature);
        var nextMethod = source.IndexOf("\n    private static ", start + signature.Length, StringComparison.Ordinal);
        var body = nextMethod < 0 ? source[start..] : source[start..nextMethod];
        Assert.That(
            body,
            Does.Contain("PcCompatUnityMainExecutionContext.Enter()"),
            signature);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "StArray.ModManager.Android")) &&
                Directory.Exists(Path.Combine(directory.FullName, "xphorror.PcModCompat")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate StArray.ModManager repository root.");
    }
}

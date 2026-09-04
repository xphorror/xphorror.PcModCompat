using System.Reflection;
using System.Runtime.Loader;
using StArray.ModManager.Runtime;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[TestFixture]
public sealed class PcCompatManagedDynamicGetterBridgeTests
{
    [Test]
    public void FactoryRejectsCallsOutsideManagedScope()
    {
        Assert.That(
            () => PcCompatManagedDynamicGetterBridge.CreateStaticFieldGetter<int>(
                typeof(StaticSource), nameof(StaticSource.Field)),
            Throws.InvalidOperationException.With.Message.Contains("active managed scope"));
    }

    [Test]
    [NonParallelizable]
    public void FactoryPreservesFieldPropertyStaticAndNullGetterSemantics()
    {
        using var runtime = RegisterLoadedSession("dynamic-getter-semantics", 1);
        var state = runtime.State(PcCompatManagedExecutionPhase.Setup);
        using var unityMain = PcCompatUnityMainExecutionContext.Enter();
        using var scope = PcCompatManagedExecutionContext.Enter(state);

        var staticField = PcCompatManagedDynamicGetterBridge.CreateStaticFieldGetter<int>(
            typeof(StaticSource), nameof(StaticSource.Field));
        var staticProperty = PcCompatManagedDynamicGetterBridge.CreateStaticPropertyGetter<string>(
            typeof(StaticSource), nameof(StaticSource.Property));
        var staticMember = PcCompatManagedDynamicGetterBridge.CreateStaticMemberGetter(
            typeof(StaticSource), nameof(StaticSource.Field));
        var objectMember = PcCompatManagedDynamicGetterBridge.CreateMemberGetter<InstanceSource>(
            nameof(InstanceSource.Field));
        var typedMember = PcCompatManagedDynamicGetterBridge.CreateMemberGetter<InstanceSource, string>(
            nameof(InstanceSource.Property));

        Assert.Multiple(() =>
        {
            Assert.That(staticField(), Is.EqualTo(17));
            Assert.That(staticProperty(), Is.EqualTo("property"));
            Assert.That(staticMember(), Is.EqualTo(17));
            Assert.That(objectMember(new InstanceSource()), Is.EqualTo(23));
            Assert.That(typedMember(new InstanceSource()), Is.EqualTo("instance-property"));
            Assert.That(objectMember(null!), Is.Null);
            Assert.That(typedMember(null!), Is.Null);
        });

        Assert.That(
            () => PcCompatManagedDynamicGetterBridge.CreateStaticFieldGetter<int>(
                typeof(InstanceSource), nameof(InstanceSource.Field)),
            Throws.ArgumentException.With.Message.Contains("not static"));
        Assert.That(
            () => PcCompatManagedDynamicGetterBridge.CreateMemberGetter<InstanceSource>("Missing"),
            Throws.TypeOf<MissingMemberException>());
    }

    [Test]
    [NonParallelizable]
    public void FactoryAcceptsIl2CppInteropFieldPropertiesAndBareGetterMethods()
    {
        using var runtime = RegisterLoadedSession("dynamic-getter-proxy-shapes", 1);
        var state = runtime.State(PcCompatManagedExecutionPhase.Setup);
        using var unityMain = PcCompatUnityMainExecutionContext.Enter();
        using var scope = PcCompatManagedExecutionContext.Enter(state);

        var staticField = PcCompatManagedDynamicGetterBridge.CreateStaticFieldGetter<int>(
            typeof(GeneratedProxyShape), nameof(GeneratedProxyShape.StaticFieldAsProperty));
        var staticProperty = PcCompatManagedDynamicGetterBridge.CreateStaticPropertyGetter<string>(
            typeof(GeneratedProxyShape), "LogicalStaticProperty");
        var staticMember = PcCompatManagedDynamicGetterBridge.CreateStaticMemberGetter(
            typeof(GeneratedProxyShape), "LogicalStaticMember");
        var objectMember = PcCompatManagedDynamicGetterBridge.CreateMemberGetter<GeneratedProxyShape>(
            "LogicalInstanceObject");
        var typedMember = PcCompatManagedDynamicGetterBridge.CreateMemberGetter<GeneratedProxyShape, string>(
            "LogicalInstanceProperty");
        var instance = new GeneratedProxyShape();

        Assert.Multiple(() =>
        {
            Assert.That(staticField(), Is.EqualTo(29));
            Assert.That(staticProperty(), Is.EqualTo("static-property"));
            Assert.That(staticMember(), Is.EqualTo(31));
            Assert.That(objectMember(instance), Is.EqualTo(37));
            Assert.That(typedMember(instance), Is.EqualTo("instance-property"));
        });
    }

    [Test]
    [NonParallelizable]
    public void GetterCacheIsSessionScopedAndGeneratedProxyValuesAreCanonicalized()
    {
        using var firstRuntime = RegisterLoadedSession("dynamic-getter-first", 1);
        using var secondRuntime = RegisterLoadedSession("dynamic-getter-second", 1);

        Func<InstanceSource, object> first;
        Func<InstanceSource, object> firstAgain;
        using (var unityMain = PcCompatUnityMainExecutionContext.Enter())
        using (var scope = PcCompatManagedExecutionContext.Enter(
                   firstRuntime.State(PcCompatManagedExecutionPhase.Setup)))
        {
            first = PcCompatManagedDynamicGetterBridge.CreateMemberGetter<InstanceSource>(
                nameof(InstanceSource.Field));
            firstAgain = PcCompatManagedDynamicGetterBridge.CreateMemberGetter<InstanceSource>(
                nameof(InstanceSource.Field));
        }

        Func<InstanceSource, object> second;
        using (var unityMain = PcCompatUnityMainExecutionContext.Enter())
        using (var scope = PcCompatManagedExecutionContext.Enter(
                   secondRuntime.State(PcCompatManagedExecutionPhase.Setup)))
        {
            second = PcCompatManagedDynamicGetterBridge.CreateMemberGetter<InstanceSource>(
                nameof(InstanceSource.Field));
        }

        Assert.That(firstAgain, Is.SameAs(first));
        Assert.That(second, Is.Not.SameAs(first));

        PcCompatManagedDynamicGetterBridge.RegisterGeneratedProxyTypeProbe(
            static type => type == typeof(FakeGeneratedProxy));
        PcCompatManagedDynamicGetterBridge.RegisterObjectPointerProbe(
            static value => value is FakeGeneratedProxy ? (nint)0x1234 : nint.Zero);
        try
        {
            using var unityMain = PcCompatUnityMainExecutionContext.Enter();
            using var scope = PcCompatManagedExecutionContext.Enter(
                firstRuntime.State(PcCompatManagedExecutionPhase.Setup));
            PcCompatReversePatchBridge.PublishSnapshot(new PcCompatGameSnapshot
            {
                Generation = 1,
                SessionEpoch = 7,
                ResourceSessionGeneration = 1,
                ValidFields = PcCompatGameSnapshotFields.State
            });
            var proxyGetter = PcCompatManagedDynamicGetterBridge
                .CreateMemberGetter<InstanceSource, FakeGeneratedProxy>(
                    nameof(InstanceSource.Proxy));

            var proxy1 = proxyGetter(new InstanceSource());
            var proxy2 = proxyGetter(new InstanceSource());
            Assert.That(proxy2, Is.SameAs(proxy1));

            PcCompatReversePatchBridge.PublishSnapshot(new PcCompatGameSnapshot
            {
                Generation = 2,
                SessionEpoch = 8,
                ResourceSessionGeneration = 1,
                ValidFields = PcCompatGameSnapshotFields.State
            });
            var nextSessionProxy = proxyGetter(new InstanceSource());
            Assert.That(nextSessionProxy, Is.Not.SameAs(proxy1));
        }
        finally
        {
            PcCompatManagedDynamicGetterBridge.RegisterGeneratedProxyTypeProbe(null);
            PcCompatManagedDynamicGetterBridge.RegisterObjectPointerProbe(null);
        }
    }

    [Test]
    [NonParallelizable]
    public void GetterCannotCrossManagedOwnerScopeOrSurviveSessionRetirement()
    {
        using var firstRuntime = RegisterLoadedSession("dynamic-getter-owner", 1);
        using var secondRuntime = RegisterLoadedSession("dynamic-getter-other", 1);

        Func<InstanceSource, object> getter;
        using (var unityMain = PcCompatUnityMainExecutionContext.Enter())
        using (var scope = PcCompatManagedExecutionContext.Enter(
                   firstRuntime.State(PcCompatManagedExecutionPhase.Setup)))
        {
            getter = PcCompatManagedDynamicGetterBridge.CreateMemberGetter<InstanceSource>(
                nameof(InstanceSource.Field));
        }

        using (var unityMain = PcCompatUnityMainExecutionContext.Enter())
        using (var scope = PcCompatManagedExecutionContext.Enter(
                   secondRuntime.State(PcCompatManagedExecutionPhase.Setup)))
        {
            Assert.That(
                () => getter(new InstanceSource()),
                Throws.InvalidOperationException.With.Message.Contains("owner scope mismatch"));
        }

        firstRuntime.RemoveFromRuntime();
        using (var unityMain = PcCompatUnityMainExecutionContext.Enter())
        using (var scope = PcCompatManagedExecutionContext.Enter(
                   firstRuntime.State(PcCompatManagedExecutionPhase.Setup)))
        {
            Assert.That(
                () => getter(new InstanceSource()),
                Throws.InvalidOperationException.With.Message.Contains("lease is retired"));
        }
    }

    [Test]
    [NonParallelizable]
    public void GetterReusesTheActiveOuterCallbackDuringSessionTransition()
    {
        using var runtime = RegisterLoadedSession("dynamic-getter-active-callback", 1);
        Func<InstanceSource, string> getter;
        using (var unityMain = PcCompatUnityMainExecutionContext.Enter())
        using (var scope = PcCompatManagedExecutionContext.Enter(
                   runtime.State(PcCompatManagedExecutionPhase.Setup)))
        {
            getter = PcCompatManagedDynamicGetterBridge.CreateMemberGetter<InstanceSource, string>(
                nameof(InstanceSource.Property));
        }

        var state = runtime.State(PcCompatManagedExecutionPhase.Update);
        Assert.That(runtime.TryEnterRuntimeCallback(out var outerLease), Is.True);
        using (PcCompatManagedExecutionContext.EnterCallback(
                   state,
                   outerLease!,
                   CancellationToken.None))
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(state))
        {
            Assert.That(getter(new InstanceSource()), Is.EqualTo("instance-property"));
        }
    }

    [Test]
    [NonParallelizable]
    public async Task WorkerCanReadImmutableSnapshotWithoutUnityMainDispatch()
    {
        using var runtime = RegisterLoadedSession("dynamic-getter-worker-snapshot", 1);
        Func<InstanceSource, string> getter;
        using (var unityMain = PcCompatUnityMainExecutionContext.Enter())
        using (var scope = PcCompatManagedExecutionContext.Enter(
                   runtime.State(PcCompatManagedExecutionPhase.Setup)))
        {
            getter = PcCompatManagedDynamicGetterBridge.CreateMemberGetter<InstanceSource, string>(
                nameof(InstanceSource.Property));
        }

        var schedulerCalls = 0;
        PcCompatRuntime.RegisterUnityMainWorkScheduler(_ =>
        {
            Interlocked.Increment(ref schedulerCalls);
            return false;
        });
        PcCompatManagedSnapshotScalarBridge.RegisterResolver(
            static (PcCompatManagedExecutionState owner, Type type, string member,
                Type requested, object? instance, out object? value) =>
            {
                _ = owner;
                _ = instance;
                if (type == typeof(InstanceSource) &&
                    member == nameof(InstanceSource.Property) &&
                    requested == typeof(string))
                {
                    value = "snapshot-property";
                    return PcCompatSnapshotScalarResolution.Resolved;
                }
                value = null;
                return PcCompatSnapshotScalarResolution.Unhandled;
            });
        try
        {
            var value = await Task.Run(() =>
            {
                using var scope = PcCompatManagedExecutionContext.Enter(
                    runtime.State(PcCompatManagedExecutionPhase.Setup));
                Assert.That(PcCompatUnityMainExecutionContext.IsActive, Is.False);
                return getter(new InstanceSource());
            });
            Assert.Multiple(() =>
            {
                Assert.That(value, Is.EqualTo("snapshot-property"));
                Assert.That(schedulerCalls, Is.Zero);
            });
        }
        finally
        {
            PcCompatManagedSnapshotScalarBridge.RegisterResolver(null);
            PcCompatRuntime.RegisterUnityMainWorkScheduler(null);
        }
    }

    [Test]
    [NonParallelizable]
    public void SnapshotScalarCanResolveWhenTheObjectGraphRootIsNull()
    {
        using var runtime = RegisterLoadedSession("dynamic-getter-null-root", 1);
        Func<InstanceSource, string> getter;
        using (var unityMain = PcCompatUnityMainExecutionContext.Enter())
        using (var scope = PcCompatManagedExecutionContext.Enter(
                   runtime.State(PcCompatManagedExecutionPhase.Setup)))
        {
            getter = PcCompatManagedDynamicGetterBridge.CreateMemberGetter<InstanceSource, string>(
                nameof(InstanceSource.Property));
        }

        PcCompatManagedSnapshotScalarBridge.RegisterResolver(
            static (PcCompatManagedExecutionState owner, Type type, string member,
                Type requested, object? instance, out object? value) =>
            {
                _ = owner;
                if (type == typeof(InstanceSource) &&
                    member == nameof(InstanceSource.Property) &&
                    requested == typeof(string) &&
                    instance is null)
                {
                    value = "snapshot-without-root";
                    return PcCompatSnapshotScalarResolution.Resolved;
                }
                value = null;
                return PcCompatSnapshotScalarResolution.Unhandled;
            });
        try
        {
            Assert.That(runtime.TryEnable(out var enableError), Is.True, enableError);
            using var scope = PcCompatManagedExecutionContext.Enter(
                runtime.State(PcCompatManagedExecutionPhase.Update));
            Assert.That(getter(null!), Is.EqualTo("snapshot-without-root"));
        }
        finally
        {
            PcCompatManagedSnapshotScalarBridge.RegisterResolver(null);
        }
    }

    [Test]
    [NonParallelizable]
    public async Task WorkerObjectGraphReadUsesBoundedUnityMainScheduler()
    {
        using var runtime = RegisterLoadedSession("dynamic-getter-worker-object", 1);
        Func<InstanceSource, string> getter;
        using (var unityMain = PcCompatUnityMainExecutionContext.Enter())
        using (var scope = PcCompatManagedExecutionContext.Enter(
                   runtime.State(PcCompatManagedExecutionPhase.Setup)))
        {
            getter = PcCompatManagedDynamicGetterBridge.CreateMemberGetter<InstanceSource, string>(
                nameof(InstanceSource.Property));
        }

        var schedulerCalls = 0;
        PcCompatManagedSnapshotScalarBridge.RegisterResolver(null);
        PcCompatRuntime.RegisterUnityMainWorkScheduler(work =>
        {
            Interlocked.Increment(ref schedulerCalls);
            _ = Task.Run(() =>
            {
                using var unityMain = PcCompatUnityMainExecutionContext.Enter();
                work();
            });
            return true;
        });
        try
        {
            var value = await Task.Run(() =>
            {
                using var scope = PcCompatManagedExecutionContext.Enter(
                    runtime.State(PcCompatManagedExecutionPhase.Setup));
                return getter(new InstanceSource());
            });
            Assert.Multiple(() =>
            {
                Assert.That(value, Is.EqualTo("instance-property"));
                Assert.That(schedulerCalls, Is.EqualTo(1));
            });
        }
        finally
        {
            PcCompatRuntime.RegisterUnityMainWorkScheduler(null);
        }
    }

    [Test]
    [NonParallelizable]
    public void ExternalUnityEventCallbackRestoresUnityMainBeforeDynamicGetterRead()
    {
        using var runtime = RegisterLoadedSession("dynamic-getter-external-unity-event", 1);
        Func<InstanceSource, string> getter;
        using (var unityMain = PcCompatUnityMainExecutionContext.Enter())
        using (var scope = PcCompatManagedExecutionContext.Enter(
                   runtime.State(PcCompatManagedExecutionPhase.Setup)))
        {
            getter = PcCompatManagedDynamicGetterBridge.CreateMemberGetter<InstanceSource, string>(
                nameof(InstanceSource.Property));
        }

        using (PcCompatUnityMainExecutionContext.Enter())
            Assert.That(runtime.TryEnable(out var enableError), Is.True, enableError);

        var schedulerCalls = 0;
        PcCompatRuntime.RegisterUnityMainThreadProbe(static () => true);
        PcCompatRuntime.RegisterUnityMainWorkScheduler(_ =>
        {
            Interlocked.Increment(ref schedulerCalls);
            return false;
        });
        try
        {
            Assert.That(PcCompatUnityMainExecutionContext.IsActive, Is.False);
            using (var callbackScope = PcCompatRuntime.TryEnterManagedExternalCallbackScope(
                       runtime.State(PcCompatManagedExecutionPhase.Update)))
            {
                Assert.That(callbackScope, Is.Not.Null);
                Assert.That(PcCompatUnityMainExecutionContext.IsActive, Is.True);
                Assert.That(getter(new InstanceSource()), Is.EqualTo("instance-property"));
                Assert.That(schedulerCalls, Is.Zero);
            }
            Assert.That(PcCompatUnityMainExecutionContext.IsActive, Is.False);
        }
        finally
        {
            PcCompatRuntime.RegisterUnityMainWorkScheduler(null);
            PcCompatRuntime.RegisterUnityMainThreadProbe(null);
        }
    }

    [Test]
    [NonParallelizable]
    public void ExternalWorkerEventCallbackDoesNotClaimUnityMain()
    {
        using var runtime = RegisterLoadedSession("dynamic-getter-external-worker-event", 1);
        using (PcCompatUnityMainExecutionContext.Enter())
            Assert.That(runtime.TryEnable(out var enableError), Is.True, enableError);

        PcCompatRuntime.RegisterUnityMainThreadProbe(static () => false);
        try
        {
            using var callbackScope = PcCompatRuntime.TryEnterManagedExternalCallbackScope(
                runtime.State(PcCompatManagedExecutionPhase.Update));
            Assert.Multiple(() =>
            {
                Assert.That(callbackScope, Is.Not.Null);
                Assert.That(PcCompatManagedExecutionContext.Current?.ModId,
                    Is.EqualTo("dynamic-getter-external-worker-event"));
                Assert.That(PcCompatUnityMainExecutionContext.IsActive, Is.False);
            });
        }
        finally
        {
            PcCompatRuntime.RegisterUnityMainThreadProbe(null);
        }
    }

    [Test]
    [NonParallelizable]
    public void CompatEnableCanCreateGetterBeforeLifecyclePublishesEnabled()
    {
        var fixture = new EnableFactoryFixture();
        using var runtime = RegisterLoadedSession(
            "dynamic-getter-enable-window",
            1,
            fixture);
        using var unityMain = PcCompatUnityMainExecutionContext.Enter();

        Assert.That(runtime.TryEnable(out var error), Is.True, error);
        Assert.That(fixture.Getter, Is.Not.Null);
        Assert.That(fixture.ForgedEnableScopeAccepted, Is.False);
        using var scope = PcCompatManagedExecutionContext.Enter(
            runtime.State(PcCompatManagedExecutionPhase.Update));
        Assert.That(fixture.Getter!(), Is.EqualTo(17));
    }

    [TestCase(SchedulerFailure.Unavailable)]
    [TestCase(SchedulerFailure.Throws)]
    [TestCase(SchedulerFailure.Rejected)]
    [NonParallelizable]
    public void SchedulerFailureReleasesTransferredCallbackLease(SchedulerFailure failure)
    {
        var gate = new PcCompatManagedCallbackLeaseGate();
        Assert.That(gate.TryEnter(out var lease), Is.True);
        PcCompatRuntime.RegisterUnityMainWorkScheduler(failure switch
        {
            SchedulerFailure.Unavailable => null,
            SchedulerFailure.Throws => _ => throw new InvalidOperationException("scheduler fault"),
            SchedulerFailure.Rejected => _ => false,
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        });
        try
        {
            Assert.That(
                () => PcCompatRuntime.InvokeDynamicGetterOnUnityMain(
                    new PcCompatManagedExecutionState(
                        "dynamic-getter-scheduler-failure", 1,
                        PcCompatManagedExecutionPhase.Update),
                    "scheduler failure",
                    static () => 1,
                    CancellationToken.None,
                    lease!,
                    50),
                Throws.Exception);
            Assert.That(gate.RetireAndWait(TimeSpan.FromMilliseconds(50)), Is.True);
        }
        finally
        {
            PcCompatRuntime.RegisterUnityMainWorkScheduler(null);
        }
    }

    [Test]
    [NonParallelizable]
    public async Task PendingWorkIsCanceledByRetirementAndCannotRunLater()
    {
        var gate = new PcCompatManagedCallbackLeaseGate();
        Assert.That(gate.TryEnter(out var lease), Is.True);
        Action? queuedWork = null;
        var retirement = new CancellationTokenSource();
        PcCompatRuntime.RegisterUnityMainWorkScheduler(work =>
        {
            queuedWork = work;
            return true;
        });
        try
        {
            var invocation = Task.Run(() => PcCompatRuntime.InvokeDynamicGetterOnUnityMain(
                new PcCompatManagedExecutionState(
                    "dynamic-getter-retirement", 1,
                    PcCompatManagedExecutionPhase.Update),
                "retirement",
                static () => 1,
                retirement.Token,
                lease!,
                500));
            Assert.That(SpinWait.SpinUntil(() => queuedWork != null, 500), Is.True);
            retirement.Cancel();
            Assert.That(async () => await invocation, Throws.TypeOf<OperationCanceledException>());
            Assert.That(gate.RetireAndWait(TimeSpan.FromMilliseconds(50)), Is.True);

            using var unityMain = PcCompatUnityMainExecutionContext.Enter();
            queuedWork!();
        }
        finally
        {
            retirement.Dispose();
            PcCompatRuntime.RegisterUnityMainWorkScheduler(null);
        }
    }

    [Test]
    [NonParallelizable]
    public async Task StartedWorkKeepsLeaseAfterCallerTimeoutUntilUnityMainCompletes()
    {
        var gate = new PcCompatManagedCallbackLeaseGate();
        Assert.That(gate.TryEnter(out var lease), Is.True);
        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        Task? unityMainWork = null;
        PcCompatRuntime.RegisterUnityMainWorkScheduler(work =>
        {
            unityMainWork = Task.Run(() =>
            {
                using var unityMain = PcCompatUnityMainExecutionContext.Enter();
                work();
            });
            if (!started.Wait(TimeSpan.FromSeconds(1)))
                throw new TimeoutException("test UnityMain work did not start");
            return true;
        });
        try
        {
            Assert.That(
                () => PcCompatRuntime.InvokeDynamicGetterOnUnityMain(
                    new PcCompatManagedExecutionState(
                        "dynamic-getter-started-timeout", 1,
                        PcCompatManagedExecutionPhase.Update),
                    "started timeout",
                    () =>
                    {
                        started.Set();
                        release.Wait();
                        return 1;
                    },
                    CancellationToken.None,
                    lease!,
                    50),
                Throws.TypeOf<TimeoutException>()
                    .With.Message.Contains("execution exceeded"));
            Assert.That(started.IsSet, Is.True);
            Assert.That(gate.RetireAndWait(TimeSpan.FromMilliseconds(10)), Is.False);
            release.Set();
            await unityMainWork!;
            Assert.That(gate.RetireAndWait(TimeSpan.FromSeconds(1)), Is.True);
        }
        finally
        {
            release.Set();
            if (unityMainWork != null)
                await unityMainWork;
            started.Dispose();
            release.Dispose();
            PcCompatRuntime.RegisterUnityMainWorkScheduler(null);
        }
    }

    private static LoadedSession RegisterLoadedSession(
        string modId,
        long generation,
        object? instance = null)
    {
        var manifest = new PcModManifest
        {
            Id = modId,
            DisplayName = modId,
            FolderPath = Path.Combine(Path.GetTempPath(), modId)
        };
        var session = new PcCompatManagedModSession(
            manifest,
            AssemblyLoadContext.Default,
            typeof(PcCompatManagedDynamicGetterBridge).Assembly,
            instance ?? new LifecycleFixture(),
            new object(),
            Array.Empty<PcCompatPatchDescriptor>(),
            bootstrapAttempted: true,
            bootstrapSucceeded: true,
            setupCompleted: true,
            enableCompleted: false,
            resourceSessionGeneration: generation,
            usesRewrittenAssembly: true,
            hasResourceRecipeSession: false,
            runtimeSession: null,
            runtimeKey: default);

        var sessionsField = typeof(PcCompatRuntime).GetField(
            "Sessions",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var sessionLockField = typeof(PcCompatRuntime).GetField(
            "SessionLock",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var sessions = (Dictionary<string, PcCompatManagedModSession>)sessionsField.GetValue(null)!;
        var sessionLock = sessionLockField.GetValue(null)!;
        lock (sessionLock)
            sessions.Add(modId, session);
        RefreshManagedDispatchSnapshot();
        return new LoadedSession(modId, generation, session, sessions, sessionLock);
    }

    private static void RefreshManagedDispatchSnapshot()
    {
        var update = typeof(PcCompatRuntime).GetMethod(
            "UpdateManagedFrameGate",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        update.Invoke(null, null);
    }

    private sealed class LoadedSession(
        string modId,
        long generation,
        PcCompatManagedModSession session,
        Dictionary<string, PcCompatManagedModSession> sessions,
        object sessionLock) : IDisposable
    {
        public PcCompatManagedExecutionState State(PcCompatManagedExecutionPhase phase)
            => new(modId, generation, phase);

        public bool TryEnable(out string? error) => session.TryEnable(out error);

        public bool TryEnterRuntimeCallback(out IDisposable? lease)
            => session.TryEnterRuntimeCallback(out lease);

        public void RemoveFromRuntime()
        {
            lock (sessionLock)
                sessions.Remove(modId);
            RefreshManagedDispatchSnapshot();
        }

        public void Dispose()
        {
            RemoveFromRuntime();
            session.Dispose();
        }
    }

    private sealed class LifecycleFixture
    {
        public void CompatEnable() { }
        public void CompatUpdate(float deltaTime) { }
        public void CompatDisable() { }
    }

    private sealed class EnableFactoryFixture
    {
        public Func<int>? Getter { get; private set; }
        public bool ForgedEnableScopeAccepted { get; private set; }

        public void CompatEnable()
        {
            var current = PcCompatManagedExecutionContext.Current!;
            ForgedEnableScopeAccepted = PcCompatRuntime.CanDispatchManagedContinuation(
                new PcCompatManagedExecutionState(
                    current.ModId,
                    current.ResourceSessionGeneration,
                    PcCompatManagedExecutionPhase.Enable));
            Getter = PcCompatManagedDynamicGetterBridge.CreateStaticFieldGetter<int>(
                typeof(StaticSource), nameof(StaticSource.Field));
        }

        public void CompatUpdate(float deltaTime) { }
        public void CompatDisable() { }
    }

    private sealed class StaticSource
    {
        public static int Field = 17;
        public static string Property => "property";
    }

    private sealed class InstanceSource
    {
        public int Field = 23;
        public string Property => "instance-property";
        public FakeGeneratedProxy Proxy => new();
    }

    private sealed class FakeGeneratedProxy
    {
    }

    private sealed class GeneratedProxyShape
    {
        public static int StaticFieldAsProperty => 29;

        public static string get_LogicalStaticProperty() => "static-property";

        public static int get_LogicalStaticMember() => 31;

        public int get_LogicalInstanceObject() => 37;

        public string get_LogicalInstanceProperty() => "instance-property";
    }

    public enum SchedulerFailure
    {
        Unavailable,
        Throws,
        Rejected
    }
}

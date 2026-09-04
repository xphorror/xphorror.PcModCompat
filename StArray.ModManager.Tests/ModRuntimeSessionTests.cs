using StArray.ModManager.Behaviours;
using StArray.ModManager.Hooks;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;
using StArray.ModManager.RuntimeAbstractions;
using System.Reflection;
using System.Runtime.InteropServices;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class ModRuntimeSessionTests
{
    [SetUp]
    public void SetUp() => ModOwnedResourceRegistry.ClearForTests();

    [TearDown]
    public void TearDown() => ModOwnedResourceRegistry.ClearForTests();

    [Test]
    public void ModScopeCannotReplaceGlobalHookProviderOrRuntimeBackend()
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad("StArray.Android.Native", "global-provider-isolation");
        Assert.That(session.TryPublishActive(key), Is.True);
        var previousHook = HookHelper.Instance;
        var previousBackend = RuntimeManager.Backend;
        var provider = new CountingHookProvider();
        try
        {
            HookHelper.Instance = provider;
            RuntimeManager.SetBackend(RuntimeBackend.Il2Cpp);
            using (HookHelper.EnterOwnerScope(key.OwnerId, session, key))
            {
                Assert.DoesNotThrow(() => HookHelper.Instance = provider);
                Assert.DoesNotThrow(() => RuntimeManager.SetBackend(RuntimeBackend.Il2Cpp));
                Assert.Throws<InvalidOperationException>(() =>
                    HookHelper.Instance = new CountingHookProvider());
                Assert.Throws<InvalidOperationException>(() =>
                    RuntimeManager.SetBackend(RuntimeBackend.Mono));
            }
        }
        finally
        {
            HookHelper.Instance = previousHook;
            RuntimeManager.SetBackend(previousBackend);
        }
    }

    [Test]
    public void TerminalCleanupRunsOnlyAfterTerminalRetirement()
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad("StArray.Android.Native", "terminal-cleanup");
        Assert.That(session.TryPublishActive(key), Is.True);
        var cleanupCalls = 0;
        Assert.That(session.TryRegisterTerminalCleanup(
            key,
            () => ++cleanupCalls,
            out var registration), Is.True);

        Assert.That(session.TryBeginRetirement(key), Is.True);
        Assert.That(session.WaitForQuiescence(key, TimeSpan.Zero), Is.True);
        Assert.That(cleanupCalls, Is.Zero);
        Assert.That(session.TryCompleteRetirement(key), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(cleanupCalls, Is.EqualTo(1));
            Assert.That(registration!.IsActive, Is.False);
        });
    }

    [Test]
    public void SuspensionRetainsTerminalCleanupUntilResumeAndRetirement()
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad("StArray.Android.Native", "terminal-suspend");
        Assert.That(session.TryPublishActive(key), Is.True);
        var cleanupCalls = 0;
        Assert.That(session.TryRegisterTerminalCleanup(
            key,
            () => ++cleanupCalls,
            out var registration), Is.True);

        Assert.That(session.TryBeginRetirement(key), Is.True);
        Assert.That(session.WaitForQuiescence(key, TimeSpan.Zero), Is.True);
        Assert.That(session.TryCompleteSuspension(key), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(cleanupCalls, Is.Zero);
            Assert.That(registration!.IsActive, Is.True);
        });

        Assert.That(session.TryResume(out var resumedKey), Is.True);
        Assert.That(session.TryBeginRetirement(resumedKey), Is.True);
        Assert.That(session.WaitForQuiescence(resumedKey, TimeSpan.Zero), Is.True);
        Assert.That(session.TryCompleteRetirement(resumedKey), Is.True);
        Assert.That(cleanupCalls, Is.EqualTo(1));
    }

    [Test]
    public void DisposedTerminalCleanupDoesNotRun()
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad("StArray.Android.Native", "terminal-dispose");
        Assert.That(session.TryPublishActive(key), Is.True);
        var cleanupCalls = 0;
        Assert.That(session.TryRegisterTerminalCleanup(
            key,
            () => ++cleanupCalls,
            out var registration), Is.True);
        registration!.Dispose();

        Assert.That(session.TryBeginRetirement(key), Is.True);
        Assert.That(session.WaitForQuiescence(key, TimeSpan.Zero), Is.True);
        Assert.That(session.TryCompleteRetirement(key), Is.True);
        Assert.That(cleanupCalls, Is.Zero);
    }

    [Test]
    public void CleanupFailureDoesNotBlockRemainingCleanupOrTerminalState()
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad("StArray.Android.Native", "terminal-failure");
        Assert.That(session.TryPublishActive(key), Is.True);
        var successfulCalls = 0;
        Assert.That(session.TryRegisterTerminalCleanup(
            key,
            () => throw new InvalidOperationException("fixture failure"),
            out _), Is.True);
        Assert.That(session.TryRegisterTerminalCleanup(
            key,
            () => ++successfulCalls,
            out _), Is.True);

        Assert.That(session.TryBeginRetirement(key), Is.True);
        Assert.That(session.WaitForQuiescence(key, TimeSpan.Zero), Is.True);
        Assert.DoesNotThrow(() => session.TryCompleteRetirement(key));
        Assert.Multiple(() =>
        {
            Assert.That(successfulCalls, Is.EqualTo(1));
            Assert.That(session.Snapshot().State, Is.EqualTo(ModRuntimeLifecycleState.Retired));
        });
    }

    [Test]
    public void RetirementRejectsNewCallbacksAndWaitsForInFlightCallback()
    {
        var session = new ModRuntimeSession();
        var first = session.BeginLoad(ModEntry.NativeLoaderKind, "callback-fixture");
        Assert.That(session.TryPublishActive(first), Is.True);
        Assert.That(session.TryEnterCallback(first, out var inFlight), Is.True);
        Assert.That(inFlight, Is.Not.Null);

        Assert.That(session.TryBeginRetirement(first), Is.True);
        var wait = Task.Run(() => session.WaitForQuiescence(first, TimeSpan.FromSeconds(2)));
        Assert.That(
            SpinWait.SpinUntil(
                () => session.Snapshot().State == ModRuntimeLifecycleState.Quiescing,
                TimeSpan.FromSeconds(1)),
            Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(session.TryEnterCallback(first, out _), Is.False);
            Assert.That(wait.IsCompleted, Is.False);
            Assert.That(session.Snapshot().ActiveCallbacks, Is.EqualTo(1));
        });

        inFlight!.Dispose();
        Assert.That(wait.GetAwaiter().GetResult(), Is.True);
        Assert.That(session.TryCompleteRetirement(first), Is.True);

        var second = session.BeginLoad(ModEntry.NativeLoaderKind, "callback-fixture");
        Assert.That(session.TryPublishActive(second), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(second.Generation, Is.EqualTo(first.Generation + 1));
            Assert.That(session.TryEnterCallback(first, out _), Is.False);
            Assert.That(session.TryEnterCallback(second, out var current), Is.True);
            current?.Dispose();
        });
    }

    [Test]
    public void CapturedHookGateRejectsRetiredGeneration()
    {
        var session = new ModRuntimeSession();
        var first = session.BeginLoad(ModEntry.NativeLoaderKind, "hook-fixture");
        Assert.That(session.TryPublishActive(first), Is.True);

        IModRuntimeCallbackGate? gate;
        using (HookHelper.EnterOwnerScope(first.OwnerId, session, first))
            gate = HookHelper.CaptureRuntimeCallbackGate();

        Assert.That(gate, Is.Not.Null);
        Assert.That(gate!.TryEnter(out var active), Is.True);
        active!.Dispose();

        Assert.That(session.TryBeginRetirement(first), Is.True);
        Assert.That(session.WaitForQuiescence(first, TimeSpan.Zero), Is.True);
        Assert.That(session.TryCompleteRetirement(first), Is.True);
        Assert.That(gate.TryEnter(out _), Is.False);
    }

    [Test]
    public void CapturedHookGateEntersAndRestoresTheCapturedDataDomain()
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, "hook-domain-fixture");
        Assert.That(session.TryPublishActive(key), Is.True);

        try
        {
            IModRuntimeCallbackGate? gate;
            using (HookHelper.EnterOwnerScope(key.OwnerId, session, key))
                gate = HookHelper.CaptureRuntimeCallbackGate();

            Assert.That(ModDataDomainRuntime.CurrentToken.IsValid, Is.False);
            Assert.That(gate, Is.Not.Null);
            Assert.That(gate!.TryEnter(out var lease), Is.True);
            Assert.That(lease, Is.Not.Null);
            try
            {
                Assert.Multiple(() =>
                {
                    Assert.That(HookHelper.CurrentOwnerId, Is.EqualTo(key.OwnerId));
                    Assert.That(HookHelper.CurrentRuntimeSession, Is.SameAs(session));
                    Assert.That(HookHelper.CurrentRuntimeKey.Matches(key), Is.True);
                    Assert.That(HookHelper.CurrentDomainToken, Is.EqualTo(session.DomainToken));
                    Assert.That(ModDataDomainRuntime.CurrentToken, Is.EqualTo(session.DomainToken));
                });
            }
            finally
            {
                lease?.Dispose();
            }

            Assert.Multiple(() =>
            {
                Assert.That(HookHelper.CurrentOwnerId, Is.Null);
                Assert.That(HookHelper.CurrentRuntimeSession, Is.Null);
                Assert.That(HookHelper.CurrentRuntimeKey.IsValid, Is.False);
                Assert.That(HookHelper.CurrentDomainToken.IsValid, Is.False);
                Assert.That(ModDataDomainRuntime.CurrentToken.IsValid, Is.False);
            });
        }
        finally
        {
            Assert.That(session.TryBeginRetirement(key), Is.True);
            Assert.That(session.WaitForQuiescence(key, TimeSpan.Zero), Is.True);
            Assert.That(session.TryCompleteRetirement(key), Is.True);
        }
    }

    [Test]
    public void CapturedHookGateRestoresAnExistingOwnerRuntimeScope()
    {
        var callbackSession = new ModRuntimeSession();
        var callbackKey = callbackSession.BeginLoad(
            ModEntry.NativeLoaderKind,
            "nested-callback-owner");
        Assert.That(callbackSession.TryPublishActive(callbackKey), Is.True);
        var callerSession = new ModRuntimeSession();
        var callerKey = callerSession.BeginLoad(
            ModEntry.NativeLoaderKind,
            "nested-caller-owner");
        Assert.That(callerSession.TryPublishActive(callerKey), Is.True);

        try
        {
            IModRuntimeCallbackGate? gate;
            using (HookHelper.EnterOwnerScope(
                       callbackKey.OwnerId,
                       callbackSession,
                       callbackKey))
                gate = HookHelper.CaptureRuntimeCallbackGate();

            using (HookHelper.EnterOwnerScope(callerKey.OwnerId, callerSession, callerKey))
            {
                Assert.That(gate!.TryEnter(out var lease), Is.True);
                try
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(HookHelper.CurrentOwnerId, Is.EqualTo(callbackKey.OwnerId));
                        Assert.That(HookHelper.CurrentRuntimeSession, Is.SameAs(callbackSession));
                        Assert.That(HookHelper.CurrentRuntimeKey.Matches(callbackKey), Is.True);
                        Assert.That(
                            HookHelper.CurrentDomainToken,
                            Is.EqualTo(callbackSession.DomainToken));
                    });
                }
                finally
                {
                    lease?.Dispose();
                }

                Assert.Multiple(() =>
                {
                    Assert.That(HookHelper.CurrentOwnerId, Is.EqualTo(callerKey.OwnerId));
                    Assert.That(HookHelper.CurrentRuntimeSession, Is.SameAs(callerSession));
                    Assert.That(HookHelper.CurrentRuntimeKey.Matches(callerKey), Is.True);
                    Assert.That(
                        HookHelper.CurrentDomainToken,
                        Is.EqualTo(callerSession.DomainToken));
                });
            }
        }
        finally
        {
            Assert.That(callbackSession.TryBeginRetirement(callbackKey), Is.True);
            Assert.That(
                callbackSession.WaitForQuiescence(callbackKey, TimeSpan.Zero),
                Is.True);
            Assert.That(callbackSession.TryCompleteRetirement(callbackKey), Is.True);
            Assert.That(callerSession.TryBeginRetirement(callerKey), Is.True);
            Assert.That(callerSession.WaitForQuiescence(callerKey, TimeSpan.Zero), Is.True);
            Assert.That(callerSession.TryCompleteRetirement(callerKey), Is.True);
        }
    }

    [Test]
    public void RuntimeGatedHookRejectsGateCapturedFromAnotherGeneration()
    {
        var previousHook = HookHelper.Instance;
        var provider = new CountingHookProvider();
        var firstSession = new ModRuntimeSession();
        var first = firstSession.BeginLoad(ModEntry.NativeLoaderKind, "gate-owner-a");
        Assert.That(firstSession.TryPublishActive(first), Is.True);
        IModRuntimeCallbackGate? firstGate;
        using (HookHelper.EnterOwnerScope(first.OwnerId, firstSession, first))
            firstGate = HookHelper.CaptureRuntimeCallbackGate();

        var secondSession = new ModRuntimeSession();
        var second = secondSession.BeginLoad(ModEntry.NativeLoaderKind, "gate-owner-b");
        Assert.That(secondSession.TryPublishActive(second), Is.True);
        try
        {
            HookHelper.Instance = provider;
            using (HookHelper.EnterOwnerScope(second.OwnerId, secondSession, second))
            {
                Assert.That(HookHelper.HookRuntimeGated(
                    (nint)0xA100,
                    (nint)0xA200,
                    firstGate), Is.EqualTo(nint.Zero));
            }
            Assert.That(provider.HookCalls, Is.Zero);
        }
        finally
        {
            HookHelper.Instance = previousHook;
        }
    }

    [Test]
    public void RuntimeGatedRequiredHookRejectsMissingGate()
    {
        var previousHook = HookHelper.Instance;
        var provider = new CountingHookProvider();
        try
        {
            HookHelper.Instance = provider;

            Assert.That(HookHelper.HookRuntimeGatedRequired(
                (nint)0xA300,
                (nint)0xA400,
                runtimeGate: null), Is.EqualTo(nint.Zero));
            Assert.That(provider.HookCalls, Is.Zero);
        }
        finally
        {
            HookHelper.Instance = previousHook;
        }
    }

    [Test]
    public void RuntimeGatedHostHookAllowsMissingModGate()
    {
        var previousHook = HookHelper.Instance;
        var provider = new CountingHookProvider();
        try
        {
            HookHelper.Instance = provider;

            Assert.That(HookHelper.HookRuntimeGated(
                (nint)0xA500,
                (nint)0xA600,
                runtimeGate: null), Is.EqualTo((nint)0xA300));
            Assert.That(provider.HookCalls, Is.EqualTo(1));
        }
        finally
        {
            HookHelper.Instance = previousHook;
        }
    }

    [Test]
    public void LoadingBackgroundOperationParticipatesInRetirementQuiescence()
    {
        ModOwnedResourceRegistry.ClearForTests();
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(PcCompatRuntime.LoaderKind, "async-fixture");
        Assert.That(session.TryBeginOwnedOperation(
            key,
            "prepare-runtime",
            out var operation), Is.True);
        Assert.That(operation, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(session.Snapshot().ActiveOperations, Is.EqualTo(1));
            Assert.That(ModOwnedResourceRegistry.Snapshot(key, includeRetired: false),
                Has.Count.EqualTo(1));
        });

        Assert.That(session.TryBeginRetirement(key), Is.True);
        Assert.That(operation!.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(session.TryBeginOwnedOperation(
            key,
            "rejected-after-retirement",
            out _), Is.False);
        Assert.That(session.WaitForQuiescence(key, TimeSpan.Zero), Is.False);

        operation.Dispose();
        operation.Dispose();
        Assert.That(session.WaitForQuiescence(key, TimeSpan.Zero), Is.True);
        Assert.That(session.TryCompleteRetirement(key), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(session.Snapshot().ActiveOperations, Is.Zero);
            Assert.That(ModOwnedResourceRegistry.Snapshot(key, includeRetired: false), Is.Empty);
        });
        ModOwnedResourceRegistry.ClearForTests();
    }

    [Test]
    public void PublicOperationLeaseRequiresCurrentOwnerScope()
    {
        ModOwnedResourceRegistry.ClearForTests();
        Assert.That(ModRuntimeOperations.TryBegin("outside-scope", out _), Is.False);

        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, "public-operation-fixture");
        Assert.That(session.TryPublishActive(key), Is.True);

        IModRuntimeOperationLease? operation;
        using (HookHelper.EnterOwnerScope(key.OwnerId, session, key))
            Assert.That(ModRuntimeOperations.TryBegin("network-refresh", out operation), Is.True);

        Assert.That(operation, Is.Not.Null);
        Assert.That(session.SnapshotOwnedOperations(key).Single().Name,
            Is.EqualTo("network-refresh"));
        operation!.Dispose();
        Assert.That(session.Snapshot().ActiveCallbacks, Is.Zero);
        ModOwnedResourceRegistry.ClearForTests();
    }

    [Test]
    public void RetirementTimeoutRollbackKeepsGenerationAndRejectsStaleEnd()
    {
        ModOwnedResourceRegistry.ClearForTests();
        var session = new ModRuntimeSession();
        var first = session.BeginLoad(ModEntry.NativeLoaderKind, "rollback-operation-fixture");
        Assert.That(session.TryPublishActive(first), Is.True);
        Assert.That(session.TryBeginOwnedOperation(
            first,
            "slow-operation",
            out var slowOperation), Is.True);
        var slowId = session.SnapshotOwnedOperations(first).Single().OperationId;

        Assert.That(session.TryBeginRetirement(first), Is.True);
        Assert.That(session.WaitForQuiescence(first, TimeSpan.Zero), Is.False);
        Assert.That(session.TryCancelRetirement(
            first,
            ModRuntimeLifecycleState.Active), Is.True);
        Assert.That(slowOperation!.IsCancellationRequested, Is.True);
        Assert.That(session.TryBeginOwnedOperation(
            first,
            "replacement-operation",
            out var replacement), Is.True);
        var replacementId = session.SnapshotOwnedOperations(first)
            .Single(operation => operation.Name == "replacement-operation")
            .OperationId;

        Assert.Multiple(() =>
        {
            Assert.That(session.TryExitOwnedOperation(
                new ModRuntimeKey(first.LoaderKind, first.ModId, first.Generation + 1),
                replacementId), Is.False);
            Assert.That(session.TryExitOwnedOperation(first, slowId + replacementId + 1), Is.False);
            Assert.That(session.Snapshot().ActiveOperations, Is.EqualTo(2));
        });

        slowOperation.Dispose();
        replacement!.Dispose();
        Assert.That(session.Snapshot().ActiveCallbacks, Is.Zero);
        Assert.That(session.TryBeginRetirement(first), Is.True);
        Assert.That(session.WaitForQuiescence(first, TimeSpan.Zero), Is.True);
        Assert.That(session.TryCompleteRetirement(first), Is.True);

        var second = session.BeginLoad(ModEntry.NativeLoaderKind, first.ModId);
        Assert.That(session.TryPublishActive(second), Is.True);
        Assert.That(session.TryBeginOwnedOperation(
            second,
            "second-generation-operation",
            out var secondOperation), Is.True);
        var secondId = session.SnapshotOwnedOperations(second).Single().OperationId;
        Assert.That(session.TryExitOwnedOperation(first, secondId), Is.False);
        Assert.That(session.Snapshot().ActiveOperations, Is.EqualTo(1));
        secondOperation!.Dispose();
        ModOwnedResourceRegistry.ClearForTests();
    }

    [Test]
    public void FaultingCancellationCallbackCannotBreakRetirement()
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, "cancel-callback-fixture");
        Assert.That(session.TryPublishActive(key), Is.True);
        Assert.That(session.TryBeginOwnedOperation(
            key,
            "faulting-cancellation",
            out var operation), Is.True);
        using var callbackEntered = new ManualResetEventSlim();
        using var registration = operation!.CancellationToken.Register(() =>
        {
            callbackEntered.Set();
            throw new InvalidOperationException("expected cancellation callback failure");
        });

        Assert.That(session.TryBeginRetirement(key), Is.True);
        Assert.That(callbackEntered.Wait(TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(session.Snapshot().State, Is.EqualTo(ModRuntimeLifecycleState.Retiring));

        operation.Dispose();
        Assert.That(session.WaitForQuiescence(key, TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(session.TryCompleteRetirement(key), Is.True);
    }

    [Test]
    public void OwnerScopeAuditsResourcesAfterCallbackAndRejectsStaleGeneration()
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, "audit-fixture");
        Assert.That(session.TryPublishActive(key), Is.True);
        var audits = 0;
        session.RegisterOwnedResourceAuditor(candidate =>
        {
            Assert.That(candidate.Matches(key), Is.True);
            audits++;
        });

        using (HookHelper.EnterOwnerScope(key.OwnerId, session, key))
        {
        }
        Assert.That(audits, Is.EqualTo(1));

        Assert.That(session.TryBeginRetirement(key), Is.True);
        using (HookHelper.EnterOwnerScope(key.OwnerId, session, key))
        {
        }
        Assert.That(audits, Is.EqualTo(1));
    }

    [Test]
    public void GeneratedHookWrapperCarriesRuntimeCallbackGate()
    {
        var field = typeof(GeneratedRuntimeGateHookFixture).GetField(
            "_RuntimeGateDetour_runtimeGate",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.Multiple(() =>
        {
            Assert.That(field, Is.Not.Null);
            Assert.That(field!.FieldType, Is.EqualTo(typeof(IModRuntimeCallbackGate)));
        });
    }

    [Test]
    public void StaleOwnerScopeCannotRegisterBehaviourAfterReload()
    {
        var ownerId = $"native:behaviour-generation-{Guid.NewGuid():N}";
        var modId = ownerId["native:".Length..];
        var session = new ModRuntimeSession();
        var first = session.BeginLoad(ModEntry.NativeLoaderKind, modId);
        Assert.That(session.TryPublishActive(first), Is.True);
        var firstBehaviour = new CountingBehaviour();

        try
        {
            using (HookHelper.EnterOwnerScope(ownerId, session, first))
                BehaviourManager.Add(firstBehaviour);
            BehaviourManager.ProcessPending();
            BehaviourManager.Update(1f / 60f);
            Assert.That(firstBehaviour.UpdateCount, Is.EqualTo(1));

            Assert.That(session.TryBeginRetirement(first), Is.True);
            Assert.That(session.WaitForQuiescence(first, TimeSpan.Zero), Is.True);
            BehaviourManager.RetireOwner(ownerId);
            Assert.That(session.TryCompleteRetirement(first), Is.True);

            var second = session.BeginLoad(ModEntry.NativeLoaderKind, modId);
            Assert.That(session.TryPublishActive(second), Is.True);
            var stale = new CountingBehaviour();
            var current = new CountingBehaviour();
            Assert.Throws<InvalidOperationException>(() =>
            {
                using (HookHelper.EnterOwnerScope(ownerId, session, first))
                    BehaviourManager.Add(stale);
            });
            using (HookHelper.EnterOwnerScope(ownerId, session, second))
                BehaviourManager.Add(current);

            BehaviourManager.ProcessPending();
            BehaviourManager.Update(1f / 60f);
            Assert.Multiple(() =>
            {
                Assert.That(stale.IsDestroyed, Is.False);
                Assert.That(stale.UpdateCount, Is.Zero);
                Assert.That(current.IsDestroyed, Is.False);
                Assert.That(current.UpdateCount, Is.EqualTo(1));
            });
        }
        finally
        {
            BehaviourManager.RetireOwner(ownerId);
        }
    }

    [Test]
    public void FailedNativeLoadWithoutRetainedHooksReleasesContext()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "failed-native-load-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var entryPath = typeof(ModRuntimeSessionTests).Assembly.Location;
        var context = new NativeModAssemblyLoadContext("failed-native", entryPath);
        var plugin = new FailingNativePlugin();
        var runtimeSession = new ModRuntimeSession();
        var nativeState = new NativeModLoadState(
            entryPath,
            context,
            typeof(ModRuntimeSessionTests).Assembly,
            plugin,
            runtimeSession);
        var mod = new ModEntry
        {
            Id = plugin.Id,
            Name = plugin.Name,
            FolderPath = root,
            EntryPoint = entryPath,
            PluginInstance = plugin,
            LoaderData = nativeState,
            LoaderKind = ModEntry.NativeLoaderKind,
            RuntimeSession = runtimeSession
        };
        var loader = new ModLoader(root);
        try
        {
            loader.AddMod(mod);
            Assert.That(loader.LoadMod(mod), Is.False);
            var snapshot = runtimeSession.Snapshot();
            Assert.Multiple(() =>
            {
                Assert.That(plugin.LoadCount, Is.Zero);
                Assert.That(plugin.UnloadCount, Is.Zero);
                Assert.That(plugin.DisposeCount, Is.EqualTo(1));
                Assert.That(mod.LoadError, Does.Contain("expected load failure"));
                Assert.That(mod.PluginInstance, Is.Null);
                Assert.That(nativeState.Plugin, Is.Null);
                Assert.That(snapshot.State, Is.EqualTo(ModRuntimeLifecycleState.Retired));
                Assert.That(snapshot.ActiveCallbacks, Is.Zero);
            });
        }
        finally
        {
            context.Unload();
            for (var attempt = 0; attempt < 3; ++attempt)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CountingBehaviour : GameBehaviour
    {
        public int UpdateCount { get; private set; }

        public override void OnUpdate(float delta) => UpdateCount++;
    }

    private sealed class FailingNativePlugin : IModPlugin, IDisposable
    {
        public int LoadCount { get; private set; }
        public int UnloadCount { get; private set; }
        public int DisposeCount { get; private set; }
        public string Id => "failed-native";
        public string Name => "failed-native";
        public string Version => "1.0";
        public string Author => "tests";
        public string Description => "tests";
        public IReadOnlyList<string> Dependencies => Array.Empty<string>();

        public void OnLoad()
        {
            LoadCount++;
            throw new InvalidOperationException("expected load failure");
        }

        public void OnUnload() => UnloadCount++;

        public void Dispose() => DisposeCount++;
    }

    private sealed class CountingHookProvider : IHook
    {
        public int HookCalls { get; private set; }

        public nint Hook(nint target, nint detour)
        {
            HookCalls++;
            return (nint)0xA300;
        }

        public bool Unhook(nint target) => true;

        public nint GetFunction(string library, string name) => nint.Zero;
    }
}

public unsafe partial class GeneratedRuntimeGateHookFixture
{
    [NativeHook(1UL, Convention = CallingConvention.Cdecl)]
    public static int RuntimeGateDetour(int value) => value + 1;
}

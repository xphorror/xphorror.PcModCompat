using System.Collections;
using UnityEngine;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class PcCompatManagedComponentBridgeTests
{
    private const string ModId = "pccompat.component.test";
    private const long Generation = 17;
    private readonly PcCompatManagedExecutionState _enable = new(
        ModId,
        Generation,
        PcCompatManagedExecutionPhase.Enable);
    private readonly PcCompatManagedExecutionState _update = new(
        ModId,
        Generation,
        PcCompatManagedExecutionPhase.Update);
    private readonly PcCompatManagedExecutionState _disable = new(
        ModId,
        Generation,
        PcCompatManagedExecutionPhase.Disable);
    private Dictionary<(FakeOwner Owner, Type Type), object> _nativeComponents = null!;
    private List<object> _persistentObjects = null!;
    private List<object> _destroyedObjects = null!;
    private List<(object Target, float Delay)> _delayedDestroyedObjects = null!;
    private int _destroyFailuresRemaining;
    private float _scaledDeltaTime;

    [SetUp]
    public void SetUp()
    {
        _nativeComponents = new Dictionary<(FakeOwner, Type), object>();
        _persistentObjects = [];
        _destroyedObjects = [];
        _delayedDestroyedObjects = [];
        _destroyFailuresRemaining = 0;
        _scaledDeltaTime = 0.01f;
        PcCompatManagedComponentBridge.RegisterOwnerResolver(source =>
        {
            var owner = source switch
            {
                FakeOwner direct => direct,
                NativeComponent native => native.Owner,
                _ => throw new InvalidOperationException($"Unknown fake owner source: {source.GetType()}")
            };
            return new PcCompatManagedComponentOwnerSnapshot(
                owner.Identity,
                owner,
                owner.Alive,
                owner.Active);
        });
        PcCompatManagedComponentBridge.RegisterHostOperations(
            new PcCompatManagedComponentHostOperations(
                source => ((FakeOwner)source).Transform,
                type => type == typeof(NativeComponent),
                (type, modId) => modId == ModId &&
                                 type != typeof(NativeComponent) &&
                                 type != typeof(ForeignComponent),
                (source, type) =>
                {
                    var owner = (FakeOwner)source;
                    var component = new NativeComponent(owner);
                    _nativeComponents[(owner, type)] = component;
                    return component;
                },
                (source, type) =>
                {
                    var owner = source switch
                    {
                        FakeOwner direct => direct,
                        NativeComponent native => native.Owner,
                        _ => throw new InvalidOperationException()
                    };
                    return _nativeComponents.GetValueOrDefault((owner, type));
                },
                (source, type) =>
                {
                    var owner = source switch
                    {
                        FakeOwner direct => direct,
                        NativeComponent native => native.Owner,
                        _ => throw new InvalidOperationException()
                    };
                    return _nativeComponents.TryGetValue((owner, type), out var component)
                        ? [component]
                        : Array.Empty<object>();
                },
                source => ((NativeComponent)source).enabled,
                (source, enabled) => ((NativeComponent)source).enabled = enabled,
                source => source is FakeOwner,
                source => _persistentObjects.Add(source),
                source =>
                {
                    if (_destroyFailuresRemaining > 0)
                    {
                        _destroyFailuresRemaining--;
                        throw new InvalidOperationException("injected native destroy failure");
                    }
                    _destroyedObjects.Add(source);
                    if (source is FakeOwner owner)
                        owner.Alive = false;
                },
                (source, delay) => _delayedDestroyedObjects.Add((source, delay)),
                yielded => yielded switch
                {
                    FakeScaledWait wait => new PcCompatManagedYieldDelay(
                        PcCompatManagedYieldDelayKind.ScaledSeconds,
                        wait.Seconds),
                    FakeRealtimeWait wait => new PcCompatManagedYieldDelay(
                        PcCompatManagedYieldDelayKind.RealtimeSeconds,
                        wait.Seconds),
                    _ => null
                },
                () => _scaledDeltaTime));
    }

    [TearDown]
    public void TearDown()
    {
        using var unityMain = PcCompatUnityMainExecutionContext.Enter();
        using var execution = PcCompatManagedExecutionContext.Enter(_disable);
        PcCompatManagedComponentBridge.TryClearSession(ModId, Generation, out _);
        PcCompatManagedComponentBridge.RegisterOwnerResolver(null);
        PcCompatManagedComponentBridge.RegisterHostOperations(null);
    }

    [Test]
    public void DispatchesUnityMessagesAndBindsGetComponentToOwner()
    {
        var owner = new FakeOwner(0x1234);
        LifecycleComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
        {
            component = PcCompatManagedComponentBridge.AddComponent<LifecycleComponent>(owner);
            Assert.That(
                PcCompatManagedComponentBridge.GetComponent<LifecycleComponent>(owner),
                Is.SameAs(component));
            Assert.That(
                PcCompatManagedComponentBridge.GetComponent<LifecycleComponent>(component),
                Is.SameAs(component));
        }

        Assert.Multiple(() =>
        {
            Assert.That(component.AwakeCount, Is.EqualTo(1));
            Assert.That(component.EnableCount, Is.EqualTo(1));
            Assert.That(component.StartCount, Is.Zero);
        });

        Assert.That(DispatchFrame(out var error), Is.True, error);
        var firstFrameLifecycle = PcCompatManagedComponentBridge.SnapshotLifecycle(
            ModId,
            Generation);
        Assert.Multiple(() =>
        {
            Assert.That(component.StartCount, Is.EqualTo(1));
            Assert.That(component.UpdateCount, Is.EqualTo(1));
            Assert.That(component.LateUpdateCount, Is.EqualTo(1));
            Assert.That(firstFrameLifecycle.FrameGeneration, Is.EqualTo(1));
            Assert.That(firstFrameLifecycle.Components, Has.Count.EqualTo(1));
            Assert.That(firstFrameLifecycle.Components[0].TypeName, Does.EndWith("LifecycleComponent"));
            Assert.That(firstFrameLifecycle.Components[0].Active, Is.True);
            Assert.That(firstFrameLifecycle.Components[0].Started, Is.True);
            Assert.That(firstFrameLifecycle.Components[0].AwakeCount, Is.EqualTo(1));
            Assert.That(firstFrameLifecycle.Components[0].OnEnableCount, Is.EqualTo(1));
            Assert.That(firstFrameLifecycle.Components[0].StartCount, Is.EqualTo(1));
            Assert.That(firstFrameLifecycle.Components[0].UpdateCount, Is.EqualTo(1));
            Assert.That(firstFrameLifecycle.Components[0].LateUpdateCount, Is.EqualTo(1));
        });

        owner.Active = false;
        Assert.That(DispatchFrame(out error), Is.True, error);
        Assert.That(component.DisableCount, Is.EqualTo(1));
        Assert.That(component.UpdateCount, Is.EqualTo(1));

        owner.Active = true;
        Assert.That(DispatchFrame(out error), Is.True, error);
        Assert.Multiple(() =>
        {
            Assert.That(component.EnableCount, Is.EqualTo(2));
            Assert.That(component.StartCount, Is.EqualTo(1));
            Assert.That(component.UpdateCount, Is.EqualTo(2));
            Assert.That(component.LateUpdateCount, Is.EqualTo(2));
        });

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_disable))
        {
            Assert.That(
                PcCompatManagedComponentBridge.TryClearSession(ModId, Generation, out error),
                Is.True,
                error);
        }
        Assert.Multiple(() =>
        {
            Assert.That(component.DisableCount, Is.EqualTo(2));
            Assert.That(component.DestroyCount, Is.EqualTo(1));
            Assert.That(component.DestroyPhase, Is.EqualTo(PcCompatManagedExecutionPhase.Disable));
            Assert.That(PcCompatManagedComponentBridge.HasComponents(ModId, Generation), Is.False);
        });
    }

    [Test]
    public void OnGUIDemandOnlyTracksComponentsThatDeclareOnGUI()
    {
        var owner = new FakeOwner(0x1a2b);
        ManagedOnGUIComponent onGUIComponent;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
        {
            PcCompatManagedComponentBridge.AddComponent<LifecycleComponent>(owner);
            Assert.That(
                PcCompatManagedComponentBridge.HasOnGUIComponents(ModId, Generation),
                Is.False);

            onGUIComponent =
                PcCompatManagedComponentBridge.AddComponent<ManagedOnGUIComponent>(owner);
            Assert.That(
                PcCompatManagedComponentBridge.HasOnGUIComponents(ModId, Generation),
                Is.True);
        }

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_update))
        {
            Assert.That(
                PcCompatManagedComponentBridge.TryDispatchOnGUI(
                    ModId,
                    Generation,
                    out var error),
                Is.True,
                error);
        }
        Assert.That(onGUIComponent.OnGUICount, Is.EqualTo(1));
    }

    [Test]
    public void SessionTeardownDestroysTrackedPersistentObject()
    {
        var owner = new FakeOwner(0x6d01);
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
        {
            PcCompatManagedComponentBridge.AddComponent<LifecycleComponent>(owner);
            PcCompatManagedComponentBridge.DontDestroyOnLoad(owner);
        }

        bool cleared;
        string? error;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_disable))
            cleared = PcCompatManagedComponentBridge.TryClearSession(ModId, Generation, out error);

        Assert.Multiple(() =>
        {
            Assert.That(cleared, Is.True, error);
            Assert.That(_persistentObjects, Does.Contain(owner));
            Assert.That(_destroyedObjects.Count(item => ReferenceEquals(item, owner)), Is.EqualTo(1));
            Assert.That(owner.Alive, Is.False);
        });
    }

    [Test]
    public void ExplicitDestroyRetiresPersistentObjectBeforeSessionTeardown()
    {
        var owner = new FakeOwner(0x6d02);
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
        {
            PcCompatManagedComponentBridge.AddComponent<LifecycleComponent>(owner);
            PcCompatManagedComponentBridge.DontDestroyOnLoad(owner);
            PcCompatManagedComponentBridge.Destroy(owner);
        }

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_disable))
            Assert.That(
                PcCompatManagedComponentBridge.TryClearSession(ModId, Generation, out var error),
                Is.True,
                error);

        Assert.That(_destroyedObjects.Count(item => ReferenceEquals(item, owner)), Is.EqualTo(1));
    }

    [Test]
    public void FailedPersistentDestroyRemainsRegisteredForTeardownRetry()
    {
        var owner = new FakeOwner(0x6d04);
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
        {
            PcCompatManagedComponentBridge.AddComponent<LifecycleComponent>(owner);
            PcCompatManagedComponentBridge.DontDestroyOnLoad(owner);
        }

        _destroyFailuresRemaining = 1;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_disable))
        {
            Assert.That(
                PcCompatManagedComponentBridge.TryClearSession(ModId, Generation, out var firstError),
                Is.False);
            Assert.That(firstError, Does.Contain("injected native destroy failure"));
            Assert.That(
                PcCompatManagedComponentBridge.TryClearSession(ModId, Generation, out var retryError),
                Is.True,
                retryError);
        }

        Assert.That(_destroyedObjects.Count(item => ReferenceEquals(item, owner)), Is.EqualTo(1));
    }

    [Test]
    public void DelayedDestroyKeepsPersistentObjectOwnedUntilSessionTeardown()
    {
        var owner = new FakeOwner(0x6d05);
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
        {
            PcCompatManagedComponentBridge.AddComponent<LifecycleComponent>(owner);
            PcCompatManagedComponentBridge.DontDestroyOnLoad(owner);
            PcCompatManagedComponentBridge.Destroy(owner, 5f);
        }

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_disable))
        {
            Assert.That(
                PcCompatManagedComponentBridge.TryClearSession(ModId, Generation, out var error),
                Is.True,
                error);
        }

        Assert.Multiple(() =>
        {
            Assert.That(_delayedDestroyedObjects, Does.Contain((owner, 5f)));
            Assert.That(_destroyedObjects.Count(item => ReferenceEquals(item, owner)), Is.EqualTo(1));
        });
    }

    [Test]
    public void DontDestroyWithoutManagedOwnerProofIsForwardedButNotClaimed()
    {
        var owner = new FakeOwner(0x6d03);
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
            PcCompatManagedComponentBridge.DontDestroyOnLoad(owner);

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_disable))
            Assert.That(
                PcCompatManagedComponentBridge.TryClearSession(ModId, Generation, out var error),
                Is.True,
                error);

        Assert.Multiple(() =>
        {
            Assert.That(_persistentObjects, Does.Contain(owner));
            Assert.That(_destroyedObjects, Does.Not.Contain(owner));
            Assert.That(owner.Alive, Is.True);
        });
    }

    [Test]
    public void RespectsManagedBehaviourEnabledState()
    {
        var owner = new FakeOwner(0x2345);
        LifecycleComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
            component = PcCompatManagedComponentBridge.AddComponent<LifecycleComponent>(owner);

        component.enabled = false;
        Assert.That(DispatchFrame(out var error), Is.True, error);
        Assert.Multiple(() =>
        {
            Assert.That(component.DisableCount, Is.EqualTo(1));
            Assert.That(component.StartCount, Is.Zero);
            Assert.That(component.UpdateCount, Is.Zero);
        });
    }

    [Test]
    public void DoesNotInvokeProxyDeclaredEnabledGetter()
    {
        var owner = new FakeOwner(0x2a2b);
        ProxyDeclaredEnabledComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
            component = PcCompatManagedComponentBridge.AddComponent<ProxyDeclaredEnabledComponent>(owner);

        Assert.That(DispatchFrame(out var error), Is.True, error);
        Assert.Multiple(() =>
        {
            Assert.That(component.EnableCount, Is.EqualTo(1));
            Assert.That(component.UpdateCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void EnabledBridgeDispatchesManagedLifecycleImmediately()
    {
        var owner = new FakeOwner(0x2a2c);
        LifecycleComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
        {
            component = PcCompatManagedComponentBridge.AddComponent<LifecycleComponent>(owner);
            Assert.That(PcCompatManagedComponentBridge.GetEnabled(component), Is.True);
            PcCompatManagedComponentBridge.SetEnabled(component, false);
            Assert.That(PcCompatManagedComponentBridge.GetEnabled(component), Is.False);
        }

        Assert.Multiple(() =>
        {
            Assert.That(component.EnableCount, Is.EqualTo(1));
            Assert.That(component.DisableCount, Is.EqualTo(1));
            Assert.That(component.StartCount, Is.Zero);
        });
        Assert.That(DispatchFrame(out var error), Is.True, error);
        Assert.That(component.UpdateCount, Is.Zero);

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_update))
        {
            PcCompatManagedComponentBridge.SetEnabled(component, true);
            PcCompatManagedComponentBridge.SetEnabled(component, true);
        }
        Assert.That(component.EnableCount, Is.EqualTo(2));
    }

    [Test]
    public void EnabledBridgePassesNativeBehaviourThroughHost()
    {
        var owner = new FakeOwner(0x2a2d);
        NativeComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
        {
            component = (NativeComponent)PcCompatManagedComponentBridge.AddComponent(
                owner,
                typeof(NativeComponent));
            Assert.That(PcCompatManagedComponentBridge.GetEnabled(component), Is.True);
            PcCompatManagedComponentBridge.SetEnabled(component, false);
            Assert.That(PcCompatManagedComponentBridge.GetEnabled(component), Is.False);
        }

        Assert.That(component.enabled, Is.False);
    }

    [Test]
    public void DestroyedOwnerRemovesComponentAndRunsDestroy()
    {
        var owner = new FakeOwner(0x3456);
        LifecycleComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
            component = PcCompatManagedComponentBridge.AddComponent<LifecycleComponent>(owner);

        owner.Alive = false;
        Assert.That(DispatchFrame(out var error), Is.True, error);
        Assert.Multiple(() =>
        {
            Assert.That(component.DisableCount, Is.EqualTo(1));
            Assert.That(component.DestroyCount, Is.EqualTo(1));
            Assert.That(component.DestroyPhase, Is.EqualTo(PcCompatManagedExecutionPhase.Disable));
            Assert.That(PcCompatManagedComponentBridge.HasComponents(ModId, Generation), Is.False);
        });
    }

    [Test]
    public void ComponentFailureIsContainedAndReported()
    {
        var owner = new FakeOwner(0x4567);
        ThrowingComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
            component = PcCompatManagedComponentBridge.AddComponent<ThrowingComponent>(owner);

        Assert.That(DispatchFrame(out var error), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(error, Does.Contain(nameof(ThrowingComponent)));
            Assert.That(error, Does.Contain("update failure"));
            Assert.That(component.DestroyCount, Is.EqualTo(1));
            Assert.That(PcCompatManagedComponentBridge.HasComponents(ModId, Generation), Is.False);
        });
    }

    [Test]
    public void RejectsAccessOutsideUnityMain()
    {
        using var execution = PcCompatManagedExecutionContext.Enter(_enable);
        Assert.That(
            () => PcCompatManagedComponentBridge.AddComponent<LifecycleComponent>(new FakeOwner(0x5678)),
            Throws.InvalidOperationException.With.Message.Contains("UnityMain"));
    }

    [Test]
    public void TypeOverloadsRouteManagedAndNativeComponentsSeparately()
    {
        var owner = new FakeOwner(0x6789);
        using var unityMain = PcCompatUnityMainExecutionContext.Enter();
        using var execution = PcCompatManagedExecutionContext.Enter(_enable);

        var managed = PcCompatManagedComponentBridge.AddComponent(
            owner,
            typeof(LifecycleComponent));
        var native = PcCompatManagedComponentBridge.AddComponent(
            owner,
            typeof(NativeComponent));

        Assert.Multiple(() =>
        {
            Assert.That(managed, Is.InstanceOf<LifecycleComponent>());
            Assert.That(
                PcCompatManagedComponentBridge.GetComponent(owner, typeof(LifecycleComponent)),
                Is.SameAs(managed));
            Assert.That(native, Is.InstanceOf<NativeComponent>());
            Assert.That(
                PcCompatManagedComponentBridge.GetComponent(owner, typeof(NativeComponent)),
                Is.SameAs(native));
        });
    }

    [Test]
    public void BatchAndTryQueriesRouteManagedAndNativeComponentsSeparately()
    {
        var owner = new FakeOwner(0x6790);
        using var unityMain = PcCompatUnityMainExecutionContext.Enter();
        using var execution = PcCompatManagedExecutionContext.Enter(_enable);

        var first = PcCompatManagedComponentBridge.AddComponent<LifecycleComponent>(owner);
        var second = PcCompatManagedComponentBridge.AddComponent<DerivedLifecycleComponent>(owner);
        var native = PcCompatManagedComponentBridge.AddComponent(owner, typeof(NativeComponent));

        Assert.Multiple(() =>
        {
            Assert.That(
                PcCompatManagedComponentBridge.GetComponents<LifecycleComponent>(owner),
                Is.EqualTo(new LifecycleComponent[] { first, second }));
            Assert.That(
                (Array)PcCompatManagedComponentBridge.GetComponents(
                    owner,
                    typeof(LifecycleComponent)),
                Has.Length.EqualTo(2));
            Assert.That(
                PcCompatManagedComponentBridge.TryGetComponent<LifecycleComponent>(
                    owner,
                    out var found),
                Is.True);
            Assert.That(found, Is.SameAs(first));
            Assert.That(
                PcCompatManagedComponentBridge.TryGetComponent<LifecycleComponent>(
                    owner,
                    typeof(LifecycleComponent),
                    out var foundByType),
                Is.True);
            Assert.That(foundByType, Is.SameAs(first));
            Assert.That(
                PcCompatManagedComponentBridge.TryGetComponent<MissingManagedComponent>(
                    owner,
                    out var missing),
                Is.False);
            Assert.That(missing, Is.Null);
            Assert.That(
                (Array)PcCompatManagedComponentBridge.GetComponents(owner, typeof(NativeComponent)),
                Has.Length.EqualTo(1));
            Assert.That(
                ((Array)PcCompatManagedComponentBridge.GetComponents(owner, typeof(NativeComponent)))
                    .GetValue(0),
                Is.SameAs(native));
            Assert.That(
                PcCompatManagedComponentBridge.TryGetComponent<NativeComponent>(
                    owner,
                    typeof(NativeComponent),
                    out var nativeByType),
                Is.True);
            Assert.That(nativeByType, Is.SameAs(native));
        });
    }

    [Test]
    public void RejectsManagedComponentTypeOutsideCurrentModOwnership()
    {
        using var unityMain = PcCompatUnityMainExecutionContext.Enter();
        using var execution = PcCompatManagedExecutionContext.Enter(_enable);
        Assert.That(
            () => PcCompatManagedComponentBridge.AddComponent<ForeignComponent>(
                new FakeOwner(0x6f00)),
            Throws.InvalidOperationException.With.Message.Contains("not owned"));
    }

    [Test]
    public void ManagedComponentPropertiesResolveToItsRegistryOwner()
    {
        var owner = new FakeOwner(0x789a);
        using var unityMain = PcCompatUnityMainExecutionContext.Enter();
        using var execution = PcCompatManagedExecutionContext.Enter(_enable);
        var component = PcCompatManagedComponentBridge.AddComponent<LifecycleComponent>(owner);

        Assert.Multiple(() =>
        {
            Assert.That(PcCompatManagedComponentBridge.GetGameObject(component), Is.SameAs(owner));
            Assert.That(PcCompatManagedComponentBridge.GetTransform(component), Is.SameAs(owner.Transform));
        });
    }

    [Test]
    public void DestroyImmediatelyCleansManagedLifecycleBeforeNativePassthrough()
    {
        var owner = new FakeOwner(0x89ab);
        using var unityMain = PcCompatUnityMainExecutionContext.Enter();
        using var execution = PcCompatManagedExecutionContext.Enter(_enable);
        var component = PcCompatManagedComponentBridge.AddComponent<LifecycleComponent>(owner);

        PcCompatManagedComponentBridge.Destroy(owner);

        Assert.Multiple(() =>
        {
            Assert.That(component.DisableCount, Is.EqualTo(1));
            Assert.That(component.DestroyCount, Is.EqualTo(1));
            Assert.That(component.DestroyPhase, Is.EqualTo(PcCompatManagedExecutionPhase.Disable));
            Assert.That(PcCompatManagedComponentBridge.HasComponents(ModId, Generation), Is.False);
            Assert.That(_destroyedObjects, Does.Contain(owner));
        });
    }

    [Test]
    public void DestroyingManagedComponentNeverForwardsCoreClrObjectToNativeDestroy()
    {
        var owner = new FakeOwner(0x89ac);
        using var unityMain = PcCompatUnityMainExecutionContext.Enter();
        using var execution = PcCompatManagedExecutionContext.Enter(_enable);
        var component = PcCompatManagedComponentBridge.AddComponent<LifecycleComponent>(owner);

        PcCompatManagedComponentBridge.Destroy(component);

        Assert.Multiple(() =>
        {
            Assert.That(component.DestroyCount, Is.EqualTo(1));
            Assert.That(_destroyedObjects, Is.Empty);
            Assert.That(PcCompatManagedComponentBridge.HasComponents(ModId, Generation), Is.False);
        });
    }

    [Test]
    public void DelayedDestroyUsesScaledClockAndKeepsNativeSchedulingOfficial()
    {
        var owner = new FakeOwner(0x89ad);
        LifecycleComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
        {
            component = PcCompatManagedComponentBridge.AddComponent<LifecycleComponent>(owner);
            PcCompatManagedComponentBridge.Destroy(owner, 0.025f);
        }

        Assert.That(_delayedDestroyedObjects, Is.EqualTo(new[] { (owner, 0.025f) }));
        Assert.That(DispatchFrame(out var error), Is.True, error);
        Assert.That(DispatchFrame(out error), Is.True, error);
        Assert.Multiple(() =>
        {
            Assert.That(component.DestroyCount, Is.Zero);
            Assert.That(component.UpdateCount, Is.EqualTo(2));
        });

        Assert.That(DispatchFrame(out error), Is.True, error);
        Assert.Multiple(() =>
        {
            Assert.That(component.DisableCount, Is.EqualTo(1));
            Assert.That(component.DestroyCount, Is.EqualTo(1));
            Assert.That(component.UpdateCount, Is.EqualTo(2));
            Assert.That(PcCompatManagedComponentBridge.HasComponents(ModId, Generation), Is.False);
        });
    }

    [Test]
    public void RestrictedStartCoroutineResumesNullAndScaledWaitsOnUnityMainFrames()
    {
        var owner = new FakeOwner(0x9abc);
        CoroutineComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
            component = PcCompatManagedComponentBridge.AddComponent<CoroutineComponent>(owner);

        Assert.That(DispatchFrame(out var error), Is.True, error);
        Assert.That(component.Events, Is.EqualTo(new[] { "start" }));
        Assert.That(DispatchFrame(out error), Is.True, error);
        Assert.That(component.Events, Is.EqualTo(new[] { "start", "after-null" }));
        Assert.That(DispatchFrame(out error), Is.True, error);
        Assert.That(component.Events, Has.Count.EqualTo(2));
        Assert.That(DispatchFrame(out error), Is.True, error);
        Assert.That(component.Events, Is.EqualTo(new[] { "start", "after-null", "after-scaled" }));
    }

    [Test]
    public void UnknownCoroutineYieldFaultsAndDisposesComponent()
    {
        var owner = new FakeOwner(0xabcd);
        UnsupportedCoroutineComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
            component = PcCompatManagedComponentBridge.AddComponent<UnsupportedCoroutineComponent>(owner);

        Assert.That(DispatchFrame(out var error), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(error, Does.Contain("Unsupported managed coroutine yield instruction"));
            Assert.That(component.DestroyCount, Is.EqualTo(1));
            Assert.That(PcCompatManagedComponentBridge.HasComponents(ModId, Generation), Is.False);
        });
    }

    [Test]
    public void RealtimeCoroutineWaitResumesOnNextOpportunityAtZeroDelay()
    {
        var owner = new FakeOwner(0xabce);
        RealtimeCoroutineComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
            component = PcCompatManagedComponentBridge.AddComponent<RealtimeCoroutineComponent>(owner);

        Assert.That(DispatchFrame(out var error), Is.True, error);
        Assert.That(component.Completed, Is.False);
        Assert.That(DispatchFrame(out error), Is.True, error);
        Assert.That(component.Completed, Is.True);
    }

    [Test]
    public void NestedCoroutineRunsChildBeforeResumingParent()
    {
        var owner = new FakeOwner(0xabcf);
        NestedCoroutineComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
            component = PcCompatManagedComponentBridge.AddComponent<NestedCoroutineComponent>(owner);

        Assert.That(DispatchFrame(out var error), Is.True, error);
        Assert.That(component.Events, Is.EqualTo(new[] { "parent-start", "child-start" }));
        Assert.That(DispatchFrame(out error), Is.True, error);
        Assert.That(
            component.Events,
            Is.EqualTo(new[] { "parent-start", "child-start", "child-end", "parent-end" }));
    }

    [Test]
    public void ExcessiveCoroutineNestingFaultsAndCleansComponent()
    {
        var owner = new FakeOwner(0xabd0);
        DeepCoroutineComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
            component = PcCompatManagedComponentBridge.AddComponent<DeepCoroutineComponent>(owner);

        Assert.That(DispatchFrame(out var error), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(error, Does.Contain("nesting exceeds"));
            Assert.That(component.DestroyCount, Is.EqualTo(1));
            Assert.That(PcCompatManagedComponentBridge.HasComponents(ModId, Generation), Is.False);
        });
    }

    [Test]
    public void ExplicitCoroutineApisStartImmediatelyAndStopByRoutineHandleOrOwner()
    {
        var owner = new FakeOwner(0xabd1);
        ExplicitCoroutineComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
            component = PcCompatManagedComponentBridge.AddComponent<ExplicitCoroutineComponent>(owner);

        var completed = component.Run("complete");
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_update))
            PcCompatManagedComponentBridge.StartCoroutine(component, completed);
        Assert.That(component.Events, Is.EqualTo(new[] { "complete-start" }));
        Assert.That(DispatchFrame(out var error), Is.True, error);
        Assert.That(component.Events, Is.EqualTo(new[] { "complete-start", "complete-end" }));

        var stoppedByRoutine = component.Run("routine");
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_update))
        {
            PcCompatManagedComponentBridge.StartCoroutine(component, stoppedByRoutine);
            PcCompatManagedComponentBridge.StopCoroutine(component, stoppedByRoutine);
        }

        var stoppedByHandle = component.Run("handle");
        object handle;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_update))
        {
            handle = PcCompatManagedComponentBridge.StartCoroutine(component, stoppedByHandle);
            PcCompatManagedComponentBridge.StopCoroutine(component, handle);
        }

        var stoppedByOwner = component.Run("all");
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_update))
        {
            PcCompatManagedComponentBridge.StartCoroutine(component, stoppedByOwner);
            PcCompatManagedComponentBridge.StopAllCoroutines(component);
        }

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_update))
        {
            PcCompatManagedComponentBridge.StartCoroutine(component, "Named");
            PcCompatManagedComponentBridge.StartCoroutine(
                component,
                "NamedValue",
                "value");
            PcCompatManagedComponentBridge.StopCoroutine(
                component,
                "NamedValue");
        }

        Assert.That(DispatchFrame(out error), Is.True, error);
        Assert.That(
            component.Events,
            Is.EqualTo(new[]
            {
                "complete-start",
                "complete-end",
                "routine-start",
                "handle-start",
                "all-start",
                "named-start",
                "value-start",
                "named-end"
            }));
    }

    [Test]
    public void SelfDestroyDuringOnEnableDoesNotContinueLifecycle()
    {
        var owner = new FakeOwner(0xbcde);
        SelfDestroyOnEnableComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
            component = PcCompatManagedComponentBridge.AddComponent<SelfDestroyOnEnableComponent>(owner);

        Assert.Multiple(() =>
        {
            Assert.That(component.EnableCount, Is.EqualTo(1));
            Assert.That(component.DisableCount, Is.EqualTo(1));
            Assert.That(component.DestroyCount, Is.EqualTo(1));
            Assert.That(component.UpdateCount, Is.Zero);
            Assert.That(PcCompatManagedComponentBridge.HasComponents(ModId, Generation), Is.False);
        });
    }

    [Test]
    public void DestroyFromOnDisableDoesNotReenterOnDisable()
    {
        var owner = new FakeOwner(0xcdef);
        SelfDestroyOnDisableComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
            component = PcCompatManagedComponentBridge.AddComponent<SelfDestroyOnDisableComponent>(owner);

        owner.Active = false;
        Assert.That(DispatchFrame(out var error), Is.True, error);
        Assert.Multiple(() =>
        {
            Assert.That(component.DisableCount, Is.EqualTo(1));
            Assert.That(component.DestroyCount, Is.EqualTo(1));
            Assert.That(PcCompatManagedComponentBridge.HasComponents(ModId, Generation), Is.False);
        });
    }

    private bool DispatchFrame(out string? error)
    {
        using var unityMain = PcCompatUnityMainExecutionContext.Enter();
        using var execution = PcCompatManagedExecutionContext.Enter(_update);
        return PcCompatManagedComponentBridge.TryDispatchFrame(
            ModId,
            Generation,
            0.016f,
            out error);
    }

    private sealed class FakeOwner(long identity)
    {
        public long Identity { get; } = identity;
        public bool Alive { get; set; } = true;
        public bool Active { get; set; } = true;
        public object Transform { get; } = new();
    }

    private sealed class NativeComponent(FakeOwner owner) : MonoBehaviour
    {
        public FakeOwner Owner { get; } = owner;
    }

    private sealed class ForeignComponent : MonoBehaviour;
    private sealed class MissingManagedComponent : MonoBehaviour;

    private sealed class ManagedOnGUIComponent : MonoBehaviour
    {
        public int OnGUICount { get; private set; }

        private void OnGUI() => OnGUICount++;
    }

    private sealed record FakeScaledWait(float Seconds);
    private sealed record FakeRealtimeWait(float Seconds);

    private sealed class CoroutineComponent : MonoBehaviour
    {
        public List<string> Events { get; } = [];

        private IEnumerator Start()
        {
            Events.Add("start");
            yield return null;
            Events.Add("after-null");
            yield return new FakeScaledWait(0.015f);
            Events.Add("after-scaled");
        }
    }

    private sealed class UnsupportedCoroutineComponent : MonoBehaviour
    {
        public int DestroyCount { get; private set; }

        private IEnumerator Start()
        {
            yield return new object();
        }

        private void OnDestroy() => DestroyCount++;
    }

    private sealed class RealtimeCoroutineComponent : MonoBehaviour
    {
        public bool Completed { get; private set; }

        private IEnumerator Start()
        {
            yield return new FakeRealtimeWait(0f);
            Completed = true;
        }
    }

    private sealed class NestedCoroutineComponent : MonoBehaviour
    {
        public List<string> Events { get; } = [];

        private IEnumerator Start()
        {
            Events.Add("parent-start");
            yield return Child();
            Events.Add("parent-end");
        }

        private IEnumerator Child()
        {
            Events.Add("child-start");
            yield return null;
            Events.Add("child-end");
        }
    }

    private sealed class DeepCoroutineComponent : MonoBehaviour
    {
        public int DestroyCount { get; private set; }

        private IEnumerator Start() => Descend(0);

        private static IEnumerator Descend(int depth)
        {
            yield return Descend(depth + 1);
        }

        private void OnDestroy() => DestroyCount++;
    }

    private sealed class ExplicitCoroutineComponent : MonoBehaviour
    {
        public List<string> Events { get; } = [];

        public IEnumerator Run(string name)
        {
            Events.Add(name + "-start");
            yield return null;
            Events.Add(name + "-end");
        }

        private IEnumerator Named()
        {
            Events.Add("named-start");
            yield return null;
            Events.Add("named-end");
        }

        private IEnumerator NamedValue(object value)
        {
            Events.Add(value + "-start");
            yield return null;
            Events.Add(value + "-end");
        }
    }

    private sealed class SelfDestroyOnEnableComponent : MonoBehaviour
    {
        public int EnableCount { get; private set; }
        public int DisableCount { get; private set; }
        public int DestroyCount { get; private set; }
        public int UpdateCount { get; private set; }

        private void OnEnable()
        {
            EnableCount++;
            PcCompatManagedComponentBridge.Destroy(this);
        }

        private void OnDisable() => DisableCount++;
        private void OnDestroy() => DestroyCount++;
        private void Update() => UpdateCount++;
    }

    private sealed class SelfDestroyOnDisableComponent : MonoBehaviour
    {
        public int DisableCount { get; private set; }
        public int DestroyCount { get; private set; }

        private void OnDisable()
        {
            DisableCount++;
            PcCompatManagedComponentBridge.Destroy(this);
        }

        private void OnDestroy() => DestroyCount++;
    }

    private class LifecycleComponent : MonoBehaviour
    {
        public int AwakeCount { get; private set; }
        public int StartCount { get; private set; }
        public int EnableCount { get; private set; }
        public int UpdateCount { get; private set; }
        public int LateUpdateCount { get; private set; }
        public int DisableCount { get; private set; }
        public int DestroyCount { get; private set; }
        public PcCompatManagedExecutionPhase? DestroyPhase { get; private set; }

        private void Awake() => AwakeCount++;
        private void Start() => StartCount++;
        private void OnEnable() => EnableCount++;
        private void Update() => UpdateCount++;
        private void LateUpdate() => LateUpdateCount++;
        private void OnDisable() => DisableCount++;
        private void OnDestroy()
        {
            DestroyCount++;
            DestroyPhase = PcCompatManagedExecutionContext.Current?.Phase;
        }
    }

    private sealed class DerivedLifecycleComponent : LifecycleComponent;

    private class ProxyLikeBehaviour : MonoBehaviour
    {
        // Simulates an Il2CppInterop generated proxy type: it declares a static
        // NativeClassPtr field and its enabled getter stands in for a native invoke
        // that must never run for surrogate managed components.
        public static nint NativeClassPtr = nint.Zero;

        public new bool enabled => throw new InvalidOperationException("native getter invoked");
    }

    private sealed class ProxyDeclaredEnabledComponent : ProxyLikeBehaviour
    {
        public int EnableCount { get; private set; }
        public int UpdateCount { get; private set; }

        private void OnEnable() => EnableCount++;
        private void Update() => UpdateCount++;
    }

    private sealed class ThrowingComponent : MonoBehaviour
    {
        public int DestroyCount { get; private set; }

        private void Update() => throw new InvalidOperationException("update failure");
        private void OnDestroy() => DestroyCount++;
    }
}

using System.Collections;
using UnityEngine;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class PcCompatManagedComponentBridgeTests
{
    [Test]
    public void DispatchDemandQueriesUsePublishedStateWithoutTakingTheMutationGate()
    {
        var source = File.ReadAllText(Path.Combine(
            FindModManagerRoot(),
            "xphorror.PcModCompat",
            "src",
            "PcCompatManagedComponentBridge.cs"));
        var hasComponentsStart = source.IndexOf(
            "public static bool HasComponents",
            StringComparison.Ordinal);
        var hasComponentsEnd = source.IndexOf(
            "public static IReadOnlyList<PcCompatUnityObjectLeaseSnapshot>",
            hasComponentsStart,
            StringComparison.Ordinal);
        var hasComponents = source.Substring(
            hasComponentsStart,
            hasComponentsEnd - hasComponentsStart);
        var hasOnGuiStart = source.IndexOf(
            "public static bool HasOnGUIComponents",
            StringComparison.Ordinal);
        var hasOnGuiEnd = source.IndexOf(
            "public static IReadOnlyList<object> SnapshotOwnerGameObjects",
            hasOnGuiStart,
            StringComparison.Ordinal);
        var hasOnGui = source.Substring(hasOnGuiStart, hasOnGuiEnd - hasOnGuiStart);

        Assert.Multiple(() =>
        {
            Assert.That(hasComponents, Does.Contain("Volatile.Read(ref s_dispatchStates)"));
            Assert.That(hasOnGui, Does.Contain("Volatile.Read(ref s_dispatchStates)"));
            Assert.That(hasComponents, Does.Not.Contain("lock (Gate)"));
            Assert.That(hasOnGui, Does.Not.Contain("lock (Gate)"));
        });
    }

    private const string ModId = "pccompat.component.test";
    private const string SharedForeignModId = "pccompat.component.shared.foreign";
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
    private int _behaviourWriteFailuresRemaining;
    private int _anchoredWriteFailuresRemaining;
    private float _scaledDeltaTime;
    private int _createFailuresRemaining;
    private long _nextIdentity = 1000;
    private long _nextNativePointer = 0x7000;
    private readonly List<object> _createdObjects = [];
    private readonly Dictionary<object, object> _boundRenderComponents = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, long> _nativePointers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<long, FakeOwner> _fakeOwnersByIdentity = [];
    private readonly Dictionary<FakeOwner, FakeOwner> _ownerResolverOverrides = [];
    private readonly Dictionary<long, NativeComponent> _nativeComponentsByPointer = [];
    private readonly HashSet<(string ModId, long Pointer)> _registeredRenderHosts = [];
    private bool _eraseNativeQueryResultType;
    private bool _eraseNativeAddResultType;

    private sealed record FakeRenderArgument(Type ProxyType, nint Pointer);

    [SetUp]
    public void SetUp()
    {
        _nativeComponents = new Dictionary<(FakeOwner, Type), object>();
        _persistentObjects = [];
        _destroyedObjects = [];
        _delayedDestroyedObjects = [];
        _destroyFailuresRemaining = 0;
        _behaviourWriteFailuresRemaining = 0;
        _anchoredWriteFailuresRemaining = 0;
        _scaledDeltaTime = 0.01f;
        _createFailuresRemaining = 0;
        _createdObjects.Clear();
        _boundRenderComponents.Clear();
        _nativePointers.Clear();
        _fakeOwnersByIdentity.Clear();
        _ownerResolverOverrides.Clear();
        _nativeComponentsByPointer.Clear();
        _registeredRenderHosts.Clear();
        _eraseNativeQueryResultType = false;
        _eraseNativeAddResultType = false;
        PcCompatManagedComponentBridge.RegisterOwnerResolver(source =>
        {
            var owner = source switch
            {
                FakeOwner direct => direct,
                NativeComponent native => native.Owner,
                ErasedNativeComponent erased => erased.Owner,
                _ => throw new InvalidOperationException($"Unknown fake owner source: {source.GetType()}")
            };
            if (_ownerResolverOverrides.TryGetValue(owner, out var replacement))
                owner = replacement;
            if (owner is FakeOwner fakeOwner)
                _fakeOwnersByIdentity[fakeOwner.Identity] = fakeOwner;
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
                    var pointer = Interlocked.Increment(ref _nextNativePointer);
                    _nativePointers[component] = pointer;
                    _nativeComponentsByPointer[pointer] = component;
                    return _eraseNativeAddResultType
                        ? CreateErasedNativeComponent(component)
                        : component;
                },
                (source, type) =>
                {
                    var owner = source switch
                    {
                        FakeOwner direct => direct,
                        NativeComponent native => native.Owner,
                        _ => throw new InvalidOperationException()
                    };
                    var component = _nativeComponents.GetValueOrDefault((owner, type));
                    return _eraseNativeQueryResultType && component is NativeComponent typedComponent
                        ? CreateErasedNativeComponent(typedComponent)
                        : component;
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
                        ? [_eraseNativeQueryResultType && component is NativeComponent typedComponent
                            ? CreateErasedNativeComponent(typedComponent)
                            : component]
                        : Array.Empty<object>();
                },
                source => ((NativeComponent)source).enabled,
                (source, enabled) =>
                {
                    if (_behaviourWriteFailuresRemaining > 0)
                    {
                        _behaviourWriteFailuresRemaining--;
                        throw new InvalidOperationException(
                            "injected Behaviour.enabled write failure");
                    }
                    ((NativeComponent)source).enabled = enabled;
                },
                source => source is FakeOwner,
                source => !_destroyedObjects.Contains(source),
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
                () => _scaledDeltaTime,
                name =>
                {
                    if (_createFailuresRemaining > 0)
                    {
                        _createFailuresRemaining--;
                        throw new InvalidOperationException("injected create failure");
                    }
                    var created = new FakeOwner(Interlocked.Increment(ref _nextIdentity)) { Name = name };
                    _createdObjects.Add(created);
                    return created;
                },
                (original, parent) =>
                {
                    var clone = new FakeOwner(Interlocked.Increment(ref _nextIdentity))
                    {
                        Name = ((FakeOwner)original).Name + "(Clone)",
                        Parent = parent
                    };
                    _createdObjects.Add(clone);
                    return clone;
                },
                source => ((FakeRect)source).AnchoredPosition,
                (source, value) =>
                {
                    if (_anchoredWriteFailuresRemaining > 0)
                    {
                        _anchoredWriteFailuresRemaining--;
                        throw new InvalidOperationException(
                            "injected anchoredPosition write failure");
                    }
                    ((FakeRect)source).AnchoredPosition = (FakeVector2)value;
                },
                // Render-component operations. This fixture has no IL2CPP runtime, so the bind and
                // wrap steps model only what the bridge's own bookkeeping observes: a distinct managed
                // instance, a stable non-zero pointer for it, and the pointer registrations. Whether
                // a real bound instance receives Unity's callback cannot be exercised here.
                (componentType, host) =>
                {
                    var bound = Activator.CreateInstance(componentType, nonPublic: true)
                                ?? throw new InvalidOperationException("bind returned null");
                    _boundRenderComponents[bound] = host;
                    return bound;
                },
                source => source is FakeOwner fakeOwner
                    ? fakeOwner.Identity
                    : _nativePointers.TryGetValue(source, out var nativePointer)
                        ? nativePointer
                        : _boundRenderComponents.ContainsKey(source)
                            ? 0x5000 + _boundRenderComponents.Keys.ToList().IndexOf(source) + 1
                            : 0,
                (proxyType, pointer) =>
                    proxyType == typeof(FakeOwner) &&
                    _fakeOwnersByIdentity.TryGetValue(pointer.ToInt64(), out var fakeOwner)
                        ? fakeOwner
                        : proxyType == typeof(NativeComponent) &&
                          _nativeComponentsByPointer.TryGetValue(pointer.ToInt64(), out var native)
                            ? native
                            : new FakeRenderArgument(proxyType, pointer),
                (source, active) =>
                {
                    ((FakeOwner)source).Active = active;
                },
                (modId, pointer) => _registeredRenderHosts.Add((modId, pointer)),
                (modId, pointer) => _registeredRenderHosts.Remove((modId, pointer)),
                modId => _registeredRenderHosts.RemoveWhere(entry => entry.ModId == modId),
                (left, right) =>
                {
                    if (left is FakeOwner leftOwner && right is FakeOwner rightOwner)
                        return leftOwner.Identity == rightOwner.Identity;
                    return ReferenceEquals(left, right);
                }));
    }

    private ErasedNativeComponent CreateErasedNativeComponent(NativeComponent component)
    {
        var erased = new ErasedNativeComponent(component.Owner);
        _nativePointers[erased] = _nativePointers[component];
        return erased;
    }

    [TearDown]
    public void TearDown()
    {
        using var unityMain = PcCompatUnityMainExecutionContext.Enter();
        using var execution = PcCompatManagedExecutionContext.Enter(_disable);
        PcCompatManagedComponentBridge.TryClearSession(ModId, Generation, out _);
        PcCompatManagedComponentBridge.TryClearSession(
            SharedForeignModId,
            Generation + 1,
            out _);
        PcCompatManagedComponentBridge.TryClearSession(
            ModId,
            Generation + 2,
            out _);
        PcCompatManagedComponentBridge.TryClearSession(
            ModId,
            Generation + 3,
            out _);
        PcCompatManagedComponentBridge.RegisterOwnerResolver(null);
        PcCompatManagedComponentBridge.RegisterHostOperations(null);
        PcCompatManagedComponentBridge.RegisterDemandChangedSink(null);
    }

    [Test]
    public void ComponentDemandChangesNotifyTheHostAfterPublishingTheNewState()
    {
        var owner = new FakeOwner(0x1a29);
        var demand = new List<(bool HasComponents, bool HasOnGUI)>();
        PcCompatManagedComponentBridge.RegisterDemandChangedSink(() =>
            demand.Add((
                PcCompatManagedComponentBridge.HasComponents(ModId, Generation),
                PcCompatManagedComponentBridge.HasOnGUIComponents(ModId, Generation))));

        LifecycleComponent first;
        LifecycleComponent second;
        ManagedOnGUIComponent onGUI;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
        {
            first = PcCompatManagedComponentBridge.AddComponent<LifecycleComponent>(owner);
            second = PcCompatManagedComponentBridge.AddComponent<LifecycleComponent>(owner);
            onGUI = PcCompatManagedComponentBridge.AddComponent<ManagedOnGUIComponent>(owner);
        }

        Assert.That(
            demand,
            Is.EqualTo(new[] { (true, false), (true, true) }),
            "only frame/OnGUI demand edges should refresh the host gate");

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_update))
        {
            PcCompatManagedComponentBridge.Destroy(onGUI);
            PcCompatManagedComponentBridge.Destroy(first);
            PcCompatManagedComponentBridge.Destroy(second);
        }

        Assert.That(
            demand,
            Is.EqualTo(new[]
            {
                (true, false),
                (true, true),
                (true, false),
                (false, false)
            }));
    }

    [Test]
    public void UnityObjectSemanticsTrackManagedComponentRegistrationAndRetirement()
    {
        var owner = new FakeOwner(0x1a2a);
        LifecycleComponent first;
        LifecycleComponent second;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
        {
            first = PcCompatManagedComponentBridge.AddComponent<LifecycleComponent>(owner);
            second = PcCompatManagedComponentBridge.AddComponent<LifecycleComponent>(owner);
        }

        Assert.Multiple(() =>
        {
            Assert.That(PcCompatManagedComponentBridge.ObjectImplicit(first), Is.True);
            Assert.That(PcCompatManagedComponentBridge.ObjectEquals(first, null), Is.False);
            Assert.That(PcCompatManagedComponentBridge.ObjectNotEquals(first, null), Is.True);
            Assert.That(PcCompatManagedComponentBridge.ObjectEquals(first, first), Is.True);
            Assert.That(PcCompatManagedComponentBridge.ObjectEquals(first, second), Is.False);
        });

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_update))
            PcCompatManagedComponentBridge.Destroy(first);

        Assert.Multiple(() =>
        {
            Assert.That(PcCompatManagedComponentBridge.ObjectImplicit(first), Is.False);
            Assert.That(PcCompatManagedComponentBridge.ObjectEquals(first, null), Is.True);
            Assert.That(PcCompatManagedComponentBridge.ObjectNotEquals(first, null), Is.False);
            Assert.That(PcCompatManagedComponentBridge.ObjectEquals(first, first), Is.True);
            Assert.That(PcCompatManagedComponentBridge.ObjectEquals(first, second), Is.False);
        });

        var destroyedNative = new FakeOwner(0x1a2b);
        _destroyedObjects.Add(destroyedNative);
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_update))
            PcCompatManagedComponentBridge.Destroy(second);

        Assert.Multiple(() =>
        {
            Assert.That(PcCompatManagedComponentBridge.ObjectEquals(first, second), Is.True);
            Assert.That(PcCompatManagedComponentBridge.ObjectNotEquals(first, second), Is.False);
            Assert.That(PcCompatManagedComponentBridge.ObjectEquals(first, destroyedNative), Is.True);
            Assert.That(PcCompatManagedComponentBridge.ObjectNotEquals(first, destroyedNative), Is.False);
        });
    }

    [Test]
    public void UnityObjectSemanticsDelegateOrdinaryNativeObjectsToTheHost()
    {
        var first = new FakeOwner(0x1a2c);
        var secondWrapper = new FakeOwner(0x1a2c);
        var other = new FakeOwner(0x1a2d);

        Assert.Multiple(() =>
        {
            Assert.That(PcCompatManagedComponentBridge.ObjectImplicit(first), Is.True);
            Assert.That(PcCompatManagedComponentBridge.ObjectEquals(first, secondWrapper), Is.True);
            Assert.That(PcCompatManagedComponentBridge.ObjectNotEquals(first, secondWrapper), Is.False);
            Assert.That(PcCompatManagedComponentBridge.ObjectEquals(first, other), Is.False);
        });
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
    public void CreatedGameObjectIsOwnedBySessionAndVisibleToOwnerAudit()
    {
        object created;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
            created = PcCompatManagedComponentBridge.CreateGameObject("panel");

        Assert.Multiple(() =>
        {
            Assert.That(_createdObjects, Does.Contain(created));
            // Before this slice a MOD-created host object had no lease and therefore never
            // appeared in the owner audit snapshot.
            Assert.That(
                PcCompatManagedComponentBridge.SnapshotOwnerGameObjects(ModId, Generation),
                Does.Contain(created));
        });
    }

    [Test]
    public void CreatedGameObjectIsDestroyedBySessionTeardown()
    {
        object created;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
            created = PcCompatManagedComponentBridge.CreateGameObject("teardown-target");

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_disable))
            Assert.That(
                PcCompatManagedComponentBridge.TryClearSession(ModId, Generation, out var error),
                Is.True,
                error);

        Assert.That(_destroyedObjects, Does.Contain(created));
    }

    [Test]
    public void InstantiatedCloneIsOwnedWhileThePrototypeIsNot()
    {
        var prototype = new FakeOwner(0x7a01);
        object clone;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
            clone = PcCompatManagedComponentBridge.Instantiate(prototype);

        var owned = PcCompatManagedComponentBridge.SnapshotOwnerGameObjects(ModId, Generation);
        Assert.Multiple(() =>
        {
            Assert.That(clone, Is.Not.SameAs(prototype));
            Assert.That(owned, Does.Contain(clone));
            Assert.That(
                owned,
                Does.Not.Contain(prototype),
                "the prototype is borrowed, so instantiating must not claim ownership of it");
        });
    }

    [Test]
    public void InstantiateWithParentForwardsTheParent()
    {
        var prototype = new FakeOwner(0x7a02);
        var parent = new object();
        object clone;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
            clone = PcCompatManagedComponentBridge.Instantiate(prototype, parent);

        Assert.That(((FakeOwner)clone).Parent, Is.SameAs(parent));
    }

    [Test]
    public void CreationOutsideAModSessionIsRejected()
    {
        using (PcCompatUnityMainExecutionContext.Enter())
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    () => PcCompatManagedComponentBridge.CreateGameObject("orphan"),
                    Throws.Exception);
                Assert.That(
                    () => PcCompatManagedComponentBridge.Instantiate(new FakeOwner(0x7a03)),
                    Throws.Exception);
            });
        }
    }

    [Test]
    public void CreationWhileDisablingIsRejected()
    {
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_disable))
            Assert.That(
                () => PcCompatManagedComponentBridge.CreateGameObject("late"),
                Throws.InvalidOperationException);
    }

    [Test]
    public void FailedRegistrationDestroysTheObjectRatherThanLeakingItUnowned()
    {
        // Create one object, then force the next creation's registration to fail by making the
        // host hand back the very same instance: a second lease on one object is rejected.
        object first;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
        {
            first = PcCompatManagedComponentBridge.CreateGameObject("first");
            Assert.That(
                () => PcCompatManagedComponentBridge.Instantiate(first, null),
                Throws.Nothing,
                "cloning produces a distinct object and must succeed");
        }

        _createFailuresRemaining = 1;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
            Assert.That(
                () => PcCompatManagedComponentBridge.CreateGameObject("doomed"),
                Throws.Exception.With.Message.Contains("injected create failure"));

        Assert.That(
            PcCompatManagedComponentBridge.SnapshotOwnerGameObjects(ModId, Generation),
            Does.Contain(first));
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
    public void SharedNativeBehaviourEnabledContributionsAreOwnerScopedAndRestoreInOrder()
    {
        var owner = new FakeOwner(0x2a33);
        var shared = new NativeComponent(owner);
        _nativeComponents[(owner, typeof(NativeComponent))] = shared;
        var foreign = new PcCompatManagedExecutionState(
            SharedForeignModId,
            Generation + 1,
            PcCompatManagedExecutionPhase.Update);

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_update))
        {
            Assert.That(
                PcCompatManagedComponentBridge.GetComponent(owner, typeof(NativeComponent)),
                Is.SameAs(shared));
            PcCompatManagedComponentBridge.SetEnabled(shared, false);
            Assert.That(PcCompatManagedComponentBridge.GetEnabled(shared), Is.False);
            Assert.That(shared.enabled, Is.False);
        }

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(foreign))
        {
            Assert.That(
                PcCompatManagedComponentBridge.GetComponent(owner, typeof(NativeComponent)),
                Is.SameAs(shared));
            PcCompatManagedComponentBridge.SetEnabled(shared, true);
            Assert.That(PcCompatManagedComponentBridge.GetEnabled(shared), Is.True);
            Assert.That(shared.enabled, Is.True);
        }

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_update))
            Assert.That(PcCompatManagedComponentBridge.GetEnabled(shared), Is.False);

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
                   SharedForeignModId,
                   Generation + 1,
                   PcCompatManagedExecutionPhase.Disable)))
        {
            Assert.That(
                PcCompatManagedComponentBridge.TryClearSession(
                    SharedForeignModId,
                    Generation + 1,
                    out var error),
                Is.True,
                error);
        }
        Assert.That(shared.enabled, Is.False);

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_disable))
        {
            Assert.That(
                PcCompatManagedComponentBridge.TryClearSession(
                    ModId,
                    Generation,
                    out var error),
                Is.True,
                error);
        }
        Assert.That(shared.enabled, Is.True);
    }

    /// <summary>
    /// The real collision this registry exists for. JipperOverlayer's BetaWatermarkCapturePatch
    /// stores the beta watermark's anchoredPosition in BetaWatermarkOriginalPos and restores it on
    /// unload; CheryTools' GameUIManager stores the same rect's anchoredPosition in
    /// ElementState.AnchoredPosition and restores it too. Without arbitration the second MOD to
    /// sample records the first one's offset as the game's original, and the watermark ends up
    /// permanently displaced once both restore.
    /// </summary>
    [Test]
    public void SharedAnchoredPositionKeepsEachModsOriginalAnchoredToTheGameBaseline()
    {
        var gameRect = new FakeRect { AnchoredPosition = new FakeVector2(10, 20) };
        var foreign = new PcCompatManagedExecutionState(
            SharedForeignModId,
            Generation + 1,
            PcCompatManagedExecutionPhase.Update);
        FakeVector2 firstModOriginal;
        FakeVector2 secondModOriginal;

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_update))
        {
            firstModOriginal = (FakeVector2)PcCompatManagedComponentBridge
                .GetAnchoredPosition(gameRect);
            PcCompatManagedComponentBridge.SetAnchoredPosition(
                gameRect,
                new FakeVector2(10, 130));
        }

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(foreign))
        {
            // Samples after the first MOD already moved the rect.
            secondModOriginal = (FakeVector2)PcCompatManagedComponentBridge
                .GetAnchoredPosition(gameRect);
            PcCompatManagedComponentBridge.SetAnchoredPosition(
                gameRect,
                new FakeVector2(300, 130));
        }

        Assert.Multiple(() =>
        {
            Assert.That(
                secondModOriginal,
                Is.EqualTo(firstModOriginal),
                "the second MOD must sample the game's own baseline, not the first MOD's offset");
            Assert.That(
                gameRect.AnchoredPosition,
                Is.EqualTo(new FakeVector2(300, 130)),
                "the most recent contributor projects to native");
        });

        // Each MOD still reads back its own contribution rather than the winner's value, so a
        // per-frame writer cannot make the other MOD "correct" a position it never set.
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_update))
        {
            Assert.That(
                (FakeVector2)PcCompatManagedComponentBridge.GetAnchoredPosition(gameRect),
                Is.EqualTo(new FakeVector2(10, 130)));
        }

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
                   SharedForeignModId,
                   Generation + 1,
                   PcCompatManagedExecutionPhase.Disable)))
        {
            Assert.That(
                PcCompatManagedComponentBridge.TryClearSession(
                    SharedForeignModId,
                    Generation + 1,
                    out var error),
                Is.True,
                error);
        }
        Assert.That(
            gameRect.AnchoredPosition,
            Is.EqualTo(new FakeVector2(10, 130)),
            "unloading the newer contributor falls back to the remaining one, not to the baseline");

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_disable))
        {
            Assert.That(
                PcCompatManagedComponentBridge.TryClearSession(ModId, Generation, out var error),
                Is.True,
                error);
        }
        Assert.That(
            gameRect.AnchoredPosition,
            Is.EqualTo(new FakeVector2(10, 20)),
            "the last MOD to release restores the game's own original position");
    }

    [Test]
    public void SharedAnchoredPositionWriteFailureRollsBackTheContribution()
    {
        var gameRect = new FakeRect { AnchoredPosition = new FakeVector2(4, 5) };

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_update))
        {
            _anchoredWriteFailuresRemaining = 1;
            Assert.Throws<InvalidOperationException>(() =>
                PcCompatManagedComponentBridge.SetAnchoredPosition(
                    gameRect,
                    new FakeVector2(9, 9)));
            _anchoredWriteFailuresRemaining = 0;

            // A failed write must leave no contribution behind, so the MOD still reads the game's
            // value and a later teardown has nothing to restore.
            Assert.That(
                (FakeVector2)PcCompatManagedComponentBridge.GetAnchoredPosition(gameRect),
                Is.EqualTo(new FakeVector2(4, 5)));
        }

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_disable))
        {
            Assert.That(
                PcCompatManagedComponentBridge.TryClearSession(ModId, Generation, out var error),
                Is.True,
                error);
        }
        Assert.That(gameRect.AnchoredPosition, Is.EqualTo(new FakeVector2(4, 5)));
    }

    [Test]
    public void OlderGenerationCannotReassertSharedAnchoredPositionContribution()
    {
        var gameRect = new FakeRect { AnchoredPosition = new FakeVector2(1, 1) };
        var newer = new PcCompatManagedExecutionState(
            ModId,
            Generation + 3,
            PcCompatManagedExecutionPhase.Update);

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(newer))
            PcCompatManagedComponentBridge.SetAnchoredPosition(gameRect, new FakeVector2(2, 2));

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_update))
        {
            // A reloaded MOD's previous generation must not be able to keep steering the rect.
            Assert.Throws<InvalidOperationException>(() =>
                PcCompatManagedComponentBridge.SetAnchoredPosition(
                    gameRect,
                    new FakeVector2(3, 3)));
        }

        Assert.That(gameRect.AnchoredPosition, Is.EqualTo(new FakeVector2(2, 2)));

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
                   ModId,
                   Generation + 3,
                   PcCompatManagedExecutionPhase.Disable)))
        {
            Assert.That(
                PcCompatManagedComponentBridge.TryClearSession(
                    ModId,
                    Generation + 3,
                    out var error),
                Is.True,
                error);
        }
        Assert.That(gameRect.AnchoredPosition, Is.EqualTo(new FakeVector2(1, 1)));
    }

    [Test]
    public void OlderGenerationCannotReassertSharedBehaviourEnabledContribution()
    {
        var owner = new FakeOwner(0x2a34);
        var shared = new NativeComponent(owner);
        _nativeComponents[(owner, typeof(NativeComponent))] = shared;
        var older = new PcCompatManagedExecutionState(
            ModId,
            Generation + 2,
            PcCompatManagedExecutionPhase.Update);
        var newer = older with
        {
            ResourceSessionGeneration = Generation + 3
        };

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(older))
            PcCompatManagedComponentBridge.SetEnabled(shared, false);

        _behaviourWriteFailuresRemaining = 1;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(newer))
        {
            Assert.That(
                () => PcCompatManagedComponentBridge.SetEnabled(shared, true),
                Throws.InvalidOperationException.With.Message.Contains(
                    "Behaviour.enabled write failure"));
            Assert.That(shared.enabled, Is.True);
            PcCompatManagedComponentBridge.SetEnabled(shared, true);
        }

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(older))
        {
            Assert.That(
                () => PcCompatManagedComponentBridge.SetEnabled(shared, false),
                Throws.InvalidOperationException.With.Message.Contains(
                    "older MOD session"));
        }

        Assert.That(shared.enabled, Is.True);
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(newer with
               {
                   Phase = PcCompatManagedExecutionPhase.Disable
               }))
        {
            Assert.That(
                PcCompatManagedComponentBridge.TryClearSession(
                    ModId,
                    Generation + 3,
                    out var error),
                Is.True,
                error);
        }
        Assert.That(shared.enabled, Is.True);
    }

    [Test]
    public void FailedSharedBehaviourEnabledRestoreKeepsContributionForRetry()
    {
        var owner = new FakeOwner(0x2a35);
        var shared = new NativeComponent(owner);
        _nativeComponents[(owner, typeof(NativeComponent))] = shared;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_update))
            PcCompatManagedComponentBridge.SetEnabled(shared, false);

        _behaviourWriteFailuresRemaining = 1;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_disable))
        {
            Assert.That(
                PcCompatManagedComponentBridge.TryClearSession(
                    ModId,
                    Generation,
                    out var firstError),
                Is.False);
            Assert.That(firstError, Does.Contain("injected Behaviour.enabled write failure"));
            Assert.That(shared.enabled, Is.False);
            Assert.That(
                PcCompatManagedComponentBridge.TryClearSession(
                    ModId,
                    Generation,
                    out var retryError),
                Is.True,
                retryError);
        }

        Assert.That(shared.enabled, Is.True);
    }

    [Test]
    public void NativeComponentLeaseIsDestroyedWithItsSession()
    {
        var owner = new FakeOwner(0x2a2e);
        NativeComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
        {
            component = (NativeComponent)PcCompatManagedComponentBridge.AddComponent(
                owner,
                typeof(NativeComponent));
            var leases = PcCompatManagedComponentBridge.SnapshotObjectLeases(
                ModId,
                Generation);
            Assert.Multiple(() =>
            {
                Assert.That(leases, Has.Count.EqualTo(1));
                Assert.That(leases[0].OwnerIdentity, Is.EqualTo(owner.Identity));
                Assert.That(leases[0].Kind, Is.EqualTo("NativeComponent"));
                Assert.That(leases[0].TypeName, Does.EndWith(nameof(NativeComponent)));
                Assert.That(
                    PcCompatManagedComponentBridge.SnapshotOwnerGameObjects(ModId, Generation),
                    Has.One.SameAs(owner));
            });
        }

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_disable))
        {
            Assert.That(
                PcCompatManagedComponentBridge.TryClearSession(
                    ModId,
                    Generation,
                    out var error),
                Is.True,
                error);
        }

        Assert.Multiple(() =>
        {
            Assert.That(_destroyedObjects.Count(item => ReferenceEquals(item, component)), Is.EqualTo(1));
            Assert.That(owner.Alive, Is.True);
            Assert.That(
                PcCompatManagedComponentBridge.SnapshotObjectLeases(ModId, Generation),
                Is.Empty);
        });
    }

    [Test]
    public void NativeComponentLeaseRejectsForeignSessionMutation()
    {
        var owner = new FakeOwner(0x2a2f);
        NativeComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
        {
            component = (NativeComponent)PcCompatManagedComponentBridge.AddComponent(
                owner,
                typeof(NativeComponent));
        }

        var foreign = new PcCompatManagedExecutionState(
            "pccompat.component.foreign",
            Generation + 1,
            PcCompatManagedExecutionPhase.Update);
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(foreign))
        {
            Assert.That(
                () => PcCompatManagedComponentBridge.SetEnabled(component, false),
                Throws.InvalidOperationException.With.Message.Contains("different MOD session"));
            Assert.That(
                () => PcCompatManagedComponentBridge.Destroy(component),
                Throws.InvalidOperationException.With.Message.Contains("different MOD session"));
        }

        Assert.Multiple(() =>
        {
            Assert.That(component.enabled, Is.True);
            Assert.That(_destroyedObjects, Does.Not.Contain(component));
            Assert.That(
                PcCompatManagedComponentBridge.SnapshotObjectLeases(ModId, Generation),
                Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void SharedGameObjectCannotBeDestroyedAcrossNativeLeaseOwners()
    {
        const string foreignModId = "pccompat.component.foreign";
        const long foreignGeneration = Generation + 1;
        var owner = new FakeOwner(0x2a31);
        NativeComponent ownComponent;
        NativeComponent foreignComponent;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
        {
            ownComponent = (NativeComponent)PcCompatManagedComponentBridge.AddComponent(
                owner,
                typeof(NativeComponent));
        }
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
                   foreignModId,
                   foreignGeneration,
                   PcCompatManagedExecutionPhase.Enable)))
        {
            foreignComponent = (NativeComponent)PcCompatManagedComponentBridge.AddComponent(
                owner,
                typeof(NativeComponent));
        }

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
        {
            Assert.That(
                () => PcCompatManagedComponentBridge.Destroy(owner),
                Throws.InvalidOperationException.With.Message.Contains("different MOD session"));
        }

        Assert.Multiple(() =>
        {
            Assert.That(owner.Alive, Is.True);
            Assert.That(_destroyedObjects, Does.Not.Contain(owner));
            Assert.That(_destroyedObjects, Does.Not.Contain(ownComponent));
            Assert.That(_destroyedObjects, Does.Not.Contain(foreignComponent));
        });

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
                   foreignModId,
                   foreignGeneration,
                   PcCompatManagedExecutionPhase.Disable)))
        {
            Assert.That(
                PcCompatManagedComponentBridge.TryClearSession(
                    foreignModId,
                    foreignGeneration,
                    out var error),
                Is.True,
                error);
        }
    }

    [Test]
    public void FailedNativeComponentLeaseDestroyCanBeRetried()
    {
        var owner = new FakeOwner(0x2a30);
        NativeComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
        {
            component = (NativeComponent)PcCompatManagedComponentBridge.AddComponent(
                owner,
                typeof(NativeComponent));
        }

        _destroyFailuresRemaining = 1;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_disable))
        {
            Assert.That(
                PcCompatManagedComponentBridge.TryClearSession(
                    ModId,
                    Generation,
                    out var firstError),
                Is.False);
            Assert.That(firstError, Does.Contain("injected native destroy failure"));
            Assert.That(
                PcCompatManagedComponentBridge.SnapshotObjectLeases(ModId, Generation),
                Has.Count.EqualTo(1));

            Assert.That(
                PcCompatManagedComponentBridge.TryClearSession(
                    ModId,
                    Generation,
                    out var retryError),
                Is.True,
                retryError);
        }

        Assert.That(_destroyedObjects.Count(item => ReferenceEquals(item, component)), Is.EqualTo(1));
    }

    [Test]
    public void AlreadyDestroyedNativeComponentLeaseRetiresWithoutSecondDestroy()
    {
        var owner = new FakeOwner(0x2a32);
        NativeComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
        {
            component = (NativeComponent)PcCompatManagedComponentBridge.AddComponent(
                owner,
                typeof(NativeComponent));
        }
        _destroyedObjects.Add(component);

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_disable))
        {
            Assert.That(
                PcCompatManagedComponentBridge.TryClearSession(
                    ModId,
                    Generation,
                    out var error),
                Is.True,
                error);
        }

        Assert.Multiple(() =>
        {
            Assert.That(_destroyedObjects.Count(item => ReferenceEquals(item, component)), Is.EqualTo(1));
            Assert.That(
                PcCompatManagedComponentBridge.SnapshotObjectLeases(ModId, Generation),
                Is.Empty);
        });
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
    public void GenericQueriesRouteNativeComponentsThroughTheOwnerHost()
    {
        var owner = new FakeOwner(0x6791);
        using var unityMain = PcCompatUnityMainExecutionContext.Enter();
        using var execution = PcCompatManagedExecutionContext.Enter(_enable);
        var native = PcCompatManagedComponentBridge.AddComponent(owner, typeof(NativeComponent));

        Assert.Multiple(() =>
        {
            Assert.That(
                PcCompatManagedComponentBridge.GetComponent<NativeComponent>(owner),
                Is.SameAs(native));
            Assert.That(
                PcCompatManagedComponentBridge.GetComponents<NativeComponent>(owner),
                Is.EqualTo(new[] { native }));
            Assert.That(
                PcCompatManagedComponentBridge.TryGetComponent<NativeComponent>(
                    owner,
                    out var found),
                Is.True);
            Assert.That(found, Is.SameAs(native));
        });
    }

    [Test]
    public void NativeComponentResultsAreRewrappedToTheRequestedProxyType()
    {
        var owner = new FakeOwner(0x6793);
        using var unityMain = PcCompatUnityMainExecutionContext.Enter();
        using var execution = PcCompatManagedExecutionContext.Enter(_enable);
        var native = (NativeComponent)PcCompatManagedComponentBridge.AddComponent(
            owner,
            typeof(NativeComponent));
        _eraseNativeQueryResultType = true;

        Assert.Multiple(() =>
        {
            Assert.That(
                PcCompatManagedComponentBridge.GetComponent<NativeComponent>(owner),
                Is.SameAs(native));
            Assert.That(
                PcCompatManagedComponentBridge.GetComponents<NativeComponent>(owner),
                Is.EqualTo(new[] { native }));
            Assert.That(
                PcCompatManagedComponentBridge.TryGetComponent<NativeComponent>(
                    owner,
                    out var found),
                Is.True);
            Assert.That(found, Is.SameAs(native));
        });
    }

    [Test]
    public void NativeAddComponentResultIsRewrappedBeforeLeaseRegistration()
    {
        var owner = new FakeOwner(0x6794);
        using var unityMain = PcCompatUnityMainExecutionContext.Enter();
        using var execution = PcCompatManagedExecutionContext.Enter(_enable);
        _eraseNativeAddResultType = true;

        var native = PcCompatManagedComponentBridge.AddComponent(
            owner,
            typeof(NativeComponent));

        Assert.Multiple(() =>
        {
            Assert.That(native, Is.InstanceOf<NativeComponent>());
            Assert.That(
                PcCompatManagedComponentBridge.SnapshotObjectLeases(ModId, Generation),
                Has.One.Property(nameof(PcCompatUnityObjectLeaseSnapshot.TypeName))
                    .EqualTo(typeof(NativeComponent).FullName));
        });
    }

    [Test]
    public void AwakeCanQueryANativeComponentThroughItsRegisteredOwner()
    {
        var owner = new FakeOwner(0x6792);
        using var unityMain = PcCompatUnityMainExecutionContext.Enter();
        using var execution = PcCompatManagedExecutionContext.Enter(_enable);
        var native = PcCompatManagedComponentBridge.AddComponent(owner, typeof(NativeComponent));

        var component = PcCompatManagedComponentBridge.AddComponent<NativeLookupAwakeComponent>(owner);

        Assert.Multiple(() =>
        {
            Assert.That(component.AwakeCount, Is.EqualTo(1));
            Assert.That(component.NativeComponent, Is.SameAs(native));
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
    public void GameObjectActivationUsesTheOwnerAwareNativeOperation()
    {
        var owner = new FakeOwner(0x789b);
        using var unityMain = PcCompatUnityMainExecutionContext.Enter();
        using var execution = PcCompatManagedExecutionContext.Enter(_enable);

        PcCompatManagedComponentBridge.SetActive(owner, false);

        Assert.That(owner.Active, Is.False);
        owner.Alive = false;
        Assert.That(
            () => PcCompatManagedComponentBridge.SetActive(owner, true),
            Throws.InvalidOperationException.With.Message.Contains("destroyed"));
    }

    [Test]
    public void NativeLeaseLookupSurvivesASecondWrapperForTheSameNativeIdentity()
    {
        var owner = new FakeOwner(0x789c);
        NativeComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
        {
            component = (NativeComponent)PcCompatManagedComponentBridge.AddComponent(
                owner,
                typeof(NativeComponent));
            var secondWrapper = CreateErasedNativeComponent(component);

            PcCompatManagedComponentBridge.Destroy(secondWrapper);
        }

        Assert.That(_destroyedObjects, Does.Contain(component));
        Assert.That(
            PcCompatManagedComponentBridge.SnapshotObjectLeases(ModId, Generation),
            Is.Empty);
    }

    [Test]
    public void ManagedComponentGameObjectRejectsOwnerIdentityDrift()
    {
        var owner = new FakeOwner(0x789d);
        var replacement = new FakeOwner(0x789e);
        LifecycleComponent component;
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_enable))
            component = PcCompatManagedComponentBridge.AddComponent<LifecycleComponent>(owner);

        _ownerResolverOverrides[owner] = replacement;
        try
        {
            using (PcCompatUnityMainExecutionContext.Enter())
            using (PcCompatManagedExecutionContext.Enter(_update))
            {
                Assert.That(
                    () => PcCompatManagedComponentBridge.GetGameObject(component),
                    Throws.InvalidOperationException.With.Message.Contains("identity changed"));
            }
        }
        finally
        {
            _ownerResolverOverrides.Remove(owner);
        }
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
        public string? Name { get; init; }
        public object? Parent { get; init; }
    }

    private sealed class NativeComponent(FakeOwner owner) : MonoBehaviour
    {
        public FakeOwner Owner { get; } = owner;
    }

    private sealed class ErasedNativeComponent(FakeOwner owner) : MonoBehaviour
    {
        public FakeOwner Owner { get; } = owner;
    }

    private sealed class NativeLookupAwakeComponent : MonoBehaviour
    {
        public int AwakeCount { get; private set; }
        public NativeComponent? NativeComponent { get; private set; }

        private void Awake()
        {
            AwakeCount++;
            NativeComponent = PcCompatManagedComponentBridge.GetComponent<NativeComponent>(this);
        }
    }

    private sealed class ForeignComponent : MonoBehaviour;
    private sealed class MissingManagedComponent : MonoBehaviour;

    /// <summary>
    /// Stand-in for the generated proxy <c>UnityEngine.Vector2</c>: a struct the bridge only ever
    /// stores boxed and hands back, so the test uses its own to prove the registry never has to
    /// know the type.
    /// </summary>
    private readonly record struct FakeVector2(float X, float Y);

    /// <summary>A game-owned RectTransform. Not a MOD-created object, so writes are arbitrated.</summary>
    private sealed class FakeRect
    {
        public FakeVector2 AnchoredPosition { get; set; }
    }

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

    private static string FindModManagerRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "xphorror.PcModCompat")) &&
                Directory.Exists(Path.Combine(directory.FullName, "StArray.ModManager.Android")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("StArray.ModManager root not found.");
    }
}

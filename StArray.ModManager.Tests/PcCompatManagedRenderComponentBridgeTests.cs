using JipperKeyViewer.KeyViewer;
using UnityEngine;
using UnityEngine.UI;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

/// <summary>
/// Covers the component bridge's render-component bookkeeping: the double registration, the native
/// pointer publication, dispatch routing, and teardown.
/// </summary>
/// <remarks>
/// <para>
/// What this fixture can and cannot reach. The bridge's own tables, the ordering of registration and
/// unregistration, and the dispatch decision are all plain managed state and are asserted directly.
/// The three host operations that touch IL2CPP - binding a managed shell to a host pointer, reading
/// that pointer, and wrapping a <c>VertexHelper</c> - are supplied as fakes, so what is proven here is
/// that the bridge sequences them correctly, not that they work. Whether a bound instance actually
/// receives Unity's callback needs a device.
/// </para>
/// <para>
/// The ordering assertions are the ones worth having. Registration publishes the native pointer
/// <em>last</em> and teardown withdraws it <em>first</em>, so there is never a window where native
/// dispatches into a pointer whose managed binding is absent - which would resolve to nothing and let
/// the host draw its own quad over the MOD's.
/// </para>
/// </remarks>
[NonParallelizable]
public sealed class PcCompatManagedRenderComponentBridgeTests
{
    private const string ModId = "JipperKeyViewer";
    private const long Generation = 41;

    private readonly PcCompatManagedExecutionState _enable = new(
        ModId, Generation, PcCompatManagedExecutionPhase.Enable);
    private readonly PcCompatManagedExecutionState _disable = new(
        ModId, Generation, PcCompatManagedExecutionPhase.Disable);

    private List<object> _destroyedObjects = null!;
    private List<string> _nativeCalls = null!;
    private HashSet<long> _nativeHosts = null!;
    private Dictionary<object, long> _pointers = null!;
    private long _nextPointer;
    private long _nextIdentity;

    [SetUp]
    public void SetUp()
    {
        _destroyedObjects = [];
        _nativeCalls = [];
        _nativeHosts = [];
        _pointers = new Dictionary<object, long>(ReferenceEqualityComparer.Instance);
        _nextPointer = 0x7000;
        _nextIdentity = 4000;

        PcCompatManagedComponentBridge.RegisterOwnerResolver(source =>
        {
            var owner = source switch
            {
                FakeGameObject direct => direct,
                RawImage host => _hostOwners[host],
                RainGraphic bound => _hostOwners[(RawImage)_boundHosts[bound]],
                _ => throw new InvalidOperationException($"unknown owner source {source.GetType()}")
            };
            return new PcCompatManagedComponentOwnerSnapshot(
                owner.Identity,
                owner,
                owner.Alive,
                owner.Active);
        });
        PcCompatManagedComponentBridge.RegisterRenderProxyTypeResolver(
            (assembly, typeName) => assembly == "UnityEngine.UI" && typeName == "UnityEngine.UI.RawImage"
                ? typeof(RawImage)
                : null);
        PcCompatManagedComponentBridge.RegisterHostOperations(
            new PcCompatManagedComponentHostOperations(
                _ => new object(),
                type => type == typeof(RawImage),
                (_, modId) => modId == ModId,
                (owner, type) =>
                {
                    Assert.That(type, Is.EqualTo(typeof(RawImage)));
                    var host = new RawImage();
                    _hostOwners[host] = (FakeGameObject)owner;
                    _pointers[host] = Interlocked.Increment(ref _nextPointer);
                    _nativeCalls.Add("AddComponent");
                    return host;
                },
                (_, _) => null,
                (_, _) => Array.Empty<object>(),
                _ => true,
                (_, _) => { },
                source => source is FakeGameObject,
                source => !_destroyedObjects.Contains(source),
                _ => { },
                source =>
                {
                    _nativeCalls.Add("Destroy");
                    _destroyedObjects.Add(source);
                },
                (_, _) => { },
                _ => null,
                () => 0.016f,
                _ => throw new NotSupportedException(),
                (_, _) => throw new NotSupportedException(),
                _ => throw new NotSupportedException(),
                (_, _) => throw new NotSupportedException(),
                (componentType, host) =>
                {
                    // Models InitializerStore's effect without an IL2CPP runtime: a shell of the MOD
                    // type that shares the host's pointer. The real path allocates uninitialized and
                    // grafts a GC handle; both end at "a managed instance whose pointer is the host's".
                    var bound = Activator.CreateInstance(componentType, nonPublic: true)!;
                    _boundHosts[bound] = host;
                    _pointers[bound] = _pointers[host];
                    _nativeCalls.Add("Bind");
                    return bound;
                },
                source => _pointers.GetValueOrDefault(source),
                (proxyType, pointer) =>
                {
                    Assert.That(proxyType, Is.EqualTo(typeof(VertexHelper)));
                    _nativeCalls.Add("Wrap");
                    return new VertexHelper();
                },
                (_, _) => { },
                (modId, pointer) =>
                {
                    Assert.That(modId, Is.EqualTo(ModId));
                    _nativeCalls.Add("RegisterHost");
                    _nativeHosts.Add(pointer);
                },
                (modId, pointer) =>
                {
                    Assert.That(modId, Is.EqualTo(ModId));
                    _nativeCalls.Add("UnregisterHost");
                    _nativeHosts.Remove(pointer);
                },
                modId =>
                {
                    Assert.That(modId, Is.EqualTo(ModId));
                    _nativeCalls.Add("ClearHosts");
                    _nativeHosts.Clear();
                },
                ReferenceEquals));
    }

    private readonly Dictionary<RawImage, FakeGameObject> _hostOwners = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, object> _boundHosts = new(ReferenceEqualityComparer.Instance);

    [TearDown]
    public void TearDown()
    {
        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_disable))
        {
            PcCompatManagedComponentBridge.TryClearSession(ModId, Generation, out _);
        }
        PcCompatManagedComponentBridge.RegisterOwnerResolver(null);
        PcCompatManagedComponentBridge.RegisterHostOperations(null);
        PcCompatManagedComponentBridge.RegisterRenderProxyTypeResolver(null);
        _hostOwners.Clear();
        _boundHosts.Clear();
    }

    /// <summary>
    /// The registered type is accepted, a host component is really added, and the managed instance is
    /// bound to it - none of which the plain managed-component path would do.
    /// </summary>
    [Test]
    public void AddComponentBindsTheRegisteredTypeToAHostComponent()
    {
        var owner = NewOwner();
        var instance = AddRainGraphic(owner);

        Assert.Multiple(() =>
        {
            Assert.That(instance, Is.InstanceOf<RainGraphic>());
            Assert.That(_boundHosts, Does.ContainKey(instance));
            Assert.That(_boundHosts[instance], Is.InstanceOf<RawImage>());
            // Its own constructor ran, so field initializers took effect. In production the base
            // constructor call inside it has been blanked by the rewriter; here there is no proxy base
            // to re-enter, but the assertion pins that the constructor is not skipped wholesale.
            Assert.That(((RainGraphic)instance).renderMain, Is.True);
        });
    }

    /// <summary>
    /// The native pointer is published only after every managed table is consistent, so the hook can
    /// never resolve a pointer whose binding is not yet in place.
    /// </summary>
    [Test]
    public void NativeHostPointerIsPublishedLast()
    {
        AddRainGraphic(NewOwner());

        Assert.Multiple(() =>
        {
            Assert.That(_nativeCalls, Is.EqualTo(new[] { "AddComponent", "Bind", "RegisterHost" }));
            Assert.That(_nativeHosts, Has.Count.EqualTo(1));
        });
    }

    /// <summary>
    /// Both registrations happen: the managed shell as a component entry, the host as a native lease.
    /// The lease is what makes cross-MOD destroy rejection and audit visibility apply to the host
    /// without any of it being reimplemented here.
    /// </summary>
    [Test]
    public void HostAndManagedInstanceAreBothRegistered()
    {
        var owner = NewOwner();
        var instance = AddRainGraphic(owner);

        Assert.Multiple(() =>
        {
            Assert.That(
                PcCompatManagedComponentBridge.HasComponents(ModId, Generation),
                Is.True,
                "the managed shell must be a registered component entry");
            Assert.That(
                PcCompatManagedComponentBridge.CountRenderComponents(ModId, Generation),
                Is.EqualTo(1));
            var leases = PcCompatManagedComponentBridge.SnapshotObjectLeases(ModId, Generation);
            Assert.That(
                leases.Select(lease => lease.TypeName),
                Does.Contain(typeof(RawImage).FullName),
                "the host component must hold a native lease");
            Assert.That(
                PcCompatManagedComponentBridge.SnapshotOwnerGameObjects(ModId, Generation),
                Does.Contain(owner));
            Assert.That(instance, Is.Not.Null);
        });
    }

    /// <summary>
    /// A callback on a bound pointer reaches the MOD's override and reports that the host's own mesh
    /// build must be skipped - the design's complete-replacement rule, which holds because the
    /// override opens with <c>vh.Clear()</c>.
    /// </summary>
    [Test]
    public void DispatchReachesTheOverrideAndSuppressesTheOriginal()
    {
        var instance = (RainGraphic)AddRainGraphic(NewOwner());
        var pointer = _pointers[instance];

        bool consumed;
        using (PcCompatUnityMainExecutionContext.Enter())
            consumed = PcCompatManagedComponentBridge.TryDispatchRenderCallback(pointer, 0x1234);

        Assert.Multiple(() =>
        {
            Assert.That(consumed, Is.True, "a bound instance must consume the callback");
            Assert.That(instance.PopulateCount, Is.EqualTo(1));
            Assert.That(instance.LastArgument, Is.InstanceOf<VertexHelper>());
            Assert.That(_nativeCalls, Does.Contain("Wrap"));
        });
    }

    /// <summary>
    /// An unbound pointer - every one of the game's own <c>RawImage</c> instances - leaves the original
    /// to run.
    /// </summary>
    [Test]
    public void UnboundPointerLeavesTheOriginalAlone()
    {
        AddRainGraphic(NewOwner());

        using var unityMain = PcCompatUnityMainExecutionContext.Enter();
        Assert.That(
            PcCompatManagedComponentBridge.TryDispatchRenderCallback(0xDEAD, 0x1234),
            Is.False);
    }

    /// <summary>
    /// A throwing override does not propagate into the native render callback; it reports "not
    /// consumed" so the host draws instead.
    /// </summary>
    /// <remarks>
    /// The caller is a Unity render callback reached through reverse-P/Invoke. Letting a managed
    /// exception cross that boundary takes the process down, so the trade is a visible artifact - the
    /// host's own quad for one rebuild - against a crash.
    /// </remarks>
    [Test]
    public void ThrowingOverrideIsContainedAndReportsNotConsumed()
    {
        var instance = (RainGraphic)AddRainGraphic(NewOwner());
        instance.ThrowOnPopulate = true;
        var pointer = _pointers[instance];

        bool consumed;
        using (PcCompatUnityMainExecutionContext.Enter())
            consumed = PcCompatManagedComponentBridge.TryDispatchRenderCallback(pointer, 0x1234);

        Assert.That(consumed, Is.False);
    }

    /// <summary>
    /// Session teardown withdraws the native registration and drops the binding, so a callback that
    /// arrives afterwards finds nothing.
    /// </summary>
    [Test]
    public void TeardownWithdrawsTheNativeRegistrationBeforeDroppingBindings()
    {
        var instance = (RainGraphic)AddRainGraphic(NewOwner());
        var pointer = _pointers[instance];
        _nativeCalls.Clear();

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
            // ClearHosts precedes the destroy calls: native stops dispatching before the managed
            // component is torn down, not after.
            Assert.That(_nativeCalls.First(), Is.EqualTo("ClearHosts"));
            Assert.That(_nativeHosts, Is.Empty);
            Assert.That(
                PcCompatManagedComponentBridge.CountRenderComponents(ModId, Generation),
                Is.Zero);
        });

        using var unityMain = PcCompatUnityMainExecutionContext.Enter();
        Assert.That(
            PcCompatManagedComponentBridge.TryDispatchRenderCallback(pointer, 0x1234),
            Is.False,
            "a callback arriving after teardown must find no binding");
        Assert.That(instance.PopulateCount, Is.Zero);
    }

    /// <summary>
    /// Destroying one component withdraws only its own pointer. JipperKeyViewer's rain pool destroys
    /// objects past its 64-drop ceiling continuously, so a leak here would grow for as long as the MOD
    /// runs rather than once.
    /// </summary>
    [Test]
    public void DestroyingOneComponentWithdrawsOnlyItsOwnPointer()
    {
        var first = (RainGraphic)AddRainGraphic(NewOwner());
        var second = (RainGraphic)AddRainGraphic(NewOwner());
        var firstPointer = _pointers[first];
        var secondPointer = _pointers[second];

        using (PcCompatUnityMainExecutionContext.Enter())
        using (PcCompatManagedExecutionContext.Enter(_disable))
        {
            PcCompatManagedComponentBridge.Destroy(first);
        }

        Assert.Multiple(() =>
        {
            Assert.That(_nativeHosts, Does.Not.Contain(firstPointer));
            Assert.That(_nativeHosts, Does.Contain(secondPointer));
            Assert.That(
                PcCompatManagedComponentBridge.CountRenderComponents(ModId, Generation),
                Is.EqualTo(1));
        });

        using var unityMain = PcCompatUnityMainExecutionContext.Enter();
        Assert.Multiple(() =>
        {
            Assert.That(
                PcCompatManagedComponentBridge.TryDispatchRenderCallback(firstPointer, 0x1234),
                Is.False);
            Assert.That(
                PcCompatManagedComponentBridge.TryDispatchRenderCallback(secondPointer, 0x1234),
                Is.True);
        });
    }

    /// <summary>
    /// A MOD that is not the registered owner gets the ordinary managed-component path, which refuses
    /// the type because its base chain leaves the MOD's modules. The registration is per MOD, not per
    /// type name.
    /// </summary>
    [Test]
    public void RegistrationDoesNotApplyToAnotherMod()
    {
        const string otherMod = "SomeOtherMod";
        var owner = NewOwner();

        using var unityMain = PcCompatUnityMainExecutionContext.Enter();
        using var execution = PcCompatManagedExecutionContext.Enter(
            new PcCompatManagedExecutionState(
                otherMod,
                Generation,
                PcCompatManagedExecutionPhase.Enable));

        Assert.That(
            () => PcCompatManagedComponentBridge.AddComponent<RainGraphic>(owner),
            Throws.InstanceOf<InvalidOperationException>());
        Assert.That(_nativeHosts, Is.Empty);
    }

    private FakeGameObject NewOwner() => new(Interlocked.Increment(ref _nextIdentity));

    private object AddRainGraphic(FakeGameObject owner)
    {
        using var unityMain = PcCompatUnityMainExecutionContext.Enter();
        using var execution = PcCompatManagedExecutionContext.Enter(_enable);
        return PcCompatManagedComponentBridge.AddComponent<RainGraphic>(owner);
    }

    private sealed class FakeGameObject(long identity)
    {
        public long Identity { get; } = identity;
        public bool Alive { get; set; } = true;
        public bool Active { get; set; } = true;
    }
}

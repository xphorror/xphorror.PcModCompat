using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace StArray.ModManager.Tests;

/// <summary>
/// Direct Link call gate: a Consumer calling a Provider's API directly must still leave the Host
/// able to attribute the work, and a retired generation must never be entered.
/// </summary>
[NonParallelizable]
public sealed class ModDirectLinkGateTests
{
    [SetUp]
    public void SetUp() => ModDirectLinkGate.ClearForTests();

    [TearDown]
    public void TearDown() => ModDirectLinkGate.ClearForTests();

    [Test]
    public void ProviderCodeRunsInTheProviderScopeAndTheConsumerScopeIsRestored()
    {
        var consumer = CreateRuntime("link.consumer");
        var provider = CreateRuntime("link.provider");
        Assert.That(
            ModDirectLinkGate.TryRegisterLink(consumer.Key, provider.Key, inferred: false),
            Is.True);

        using var consumerScope = HookHelper.EnterOwnerScope(
            consumer.Key.OwnerId, consumer.Session, consumer.Key);

        string? ownerInsideCall = null;
        var result = ModDirectLinkGate.Invoke(
            consumer.Key, consumer.Session, provider.Key, provider.Session,
            () =>
            {
                // Resources the Provider API creates must be attributed to the Provider.
                ownerInsideCall = HookHelper.CurrentRuntimeKey.OwnerId;
                return 42;
            });

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(42));
            Assert.That(ownerInsideCall, Is.EqualTo(provider.Key.OwnerId));
            Assert.That(
                HookHelper.CurrentRuntimeKey.OwnerId,
                Is.EqualTo(consumer.Key.OwnerId),
                "the Consumer scope must be restored after the call returns");
        });
    }

    [Test]
    public void ProviderExceptionPropagatesUnwrappedAndStillRestoresTheConsumerScope()
    {
        var consumer = CreateRuntime("link.exc.consumer");
        var provider = CreateRuntime("link.exc.provider");
        ModDirectLinkGate.TryRegisterLink(consumer.Key, provider.Key, inferred: false);
        using var consumerScope = HookHelper.EnterOwnerScope(
            consumer.Key.OwnerId, consumer.Session, consumer.Key);

        var thrown = new InvalidTimeZoneException("provider blew up");

        // The contract requires the concrete type, identity and inner exception to survive the
        // boundary — no wrapping in a Host exception type.
        var caught = Assert.Throws<InvalidTimeZoneException>(() =>
            ModDirectLinkGate.Invoke<int>(
                consumer.Key, consumer.Session, provider.Key, provider.Session,
                () => throw thrown));

        Assert.Multiple(() =>
        {
            Assert.That(caught, Is.SameAs(thrown), "exception object identity must be preserved");
            Assert.That(
                HookHelper.CurrentRuntimeKey.OwnerId,
                Is.EqualTo(consumer.Key.OwnerId),
                "the Consumer scope must be restored on the exception path too");
        });
    }

    [Test]
    public void ReentrantCallBackIntoTheConsumerRestoresScopesInLifoOrder()
    {
        var a = CreateRuntime("link.a");
        var b = CreateRuntime("link.b");
        ModDirectLinkGate.TryRegisterLink(a.Key, b.Key, inferred: false);
        ModDirectLinkGate.TryRegisterLink(b.Key, a.Key, inferred: false);

        using var outer = HookHelper.EnterOwnerScope(a.Key.OwnerId, a.Session, a.Key);
        var observed = new List<string?>();

        // A -> B -> A on one thread, which the contract explicitly allows.
        ModDirectLinkGate.Invoke(a.Key, a.Session, b.Key, b.Session, () =>
        {
            observed.Add(HookHelper.CurrentRuntimeKey.OwnerId);
            ModDirectLinkGate.Invoke(b.Key, b.Session, a.Key, a.Session, () =>
            {
                observed.Add(HookHelper.CurrentRuntimeKey.OwnerId);
            });
            observed.Add(HookHelper.CurrentRuntimeKey.OwnerId);
        });
        observed.Add(HookHelper.CurrentRuntimeKey.OwnerId);

        Assert.That(observed, Is.EqualTo(new[]
        {
            b.Key.OwnerId, a.Key.OwnerId, b.Key.OwnerId, a.Key.OwnerId
        }));
    }

    [Test]
    public void UnlinkedCallFailsClosedWithAnExplicitDependencyError()
    {
        var consumer = CreateRuntime("link.unlinked.consumer");
        var provider = CreateRuntime("link.unlinked.provider");

        // No placeholder, no copied state: the caller must see the failure.
        Assert.Throws<ModDependencyNotReadyException>(() =>
            ModDirectLinkGate.Invoke<int>(
                consumer.Key, consumer.Session, provider.Key, provider.Session, () => 1));
    }

    [Test]
    public void ReleasingLinksForARetiredProviderRejectsFurtherCalls()
    {
        var consumer = CreateRuntime("link.retire.consumer");
        var provider = CreateRuntime("link.retire.provider");
        ModDirectLinkGate.TryRegisterLink(consumer.Key, provider.Key, inferred: false);

        // A Provider reload lands on a new generation; the old link must stop resolving.
        ModDirectLinkGate.ReleaseLinksFor(provider.Key);

        Assert.Multiple(() =>
        {
            Assert.That(ModDirectLinkGate.SnapshotLinks(), Is.Empty);
            Assert.Throws<ModDependencyNotReadyException>(() =>
                ModDirectLinkGate.Invoke<int>(
                    consumer.Key, consumer.Session, provider.Key, provider.Session, () => 1));
        });
    }

    [Test]
    public void SelfLinkIsRejected()
    {
        var mod = CreateRuntime("link.self");
        Assert.That(
            ModDirectLinkGate.TryRegisterLink(mod.Key, mod.Key, inferred: false),
            Is.False,
            "a MOD calling itself is not a cross-domain link");
    }

    private static BoundRuntime CreateRuntime(string id)
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, id);
        Assert.That(session.TryPublishActive(key), Is.True);
        return new BoundRuntime(session, key);
    }

    private sealed record BoundRuntime(ModRuntimeSession Session, ModRuntimeKey Key);
}

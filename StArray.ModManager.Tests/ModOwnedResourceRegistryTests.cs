using StArray.ModManager.Runtime;

namespace StArray.ModManager.Tests;

public sealed class ModOwnedResourceRegistryTests
{
    [SetUp]
    public void SetUp() => ModOwnedResourceRegistry.ClearForTests();

    [TearDown]
    public void TearDown() => ModOwnedResourceRegistry.ClearForTests();

    [Test]
    public void ResourcesAreSeparatedByGeneration()
    {
        var first = new ModRuntimeKey("StArray.Android.Native", "native-a", 1);
        var second = new ModRuntimeKey("StArray.Android.Native", "native-a", 2);

        Assert.That(ModOwnedResourceRegistry.TryRegister(
            first, ModOwnedResourceKind.NativeLibrary, "/mods/a/libone.so"), Is.True);
        Assert.That(ModOwnedResourceRegistry.TryRegister(
            second, ModOwnedResourceKind.NativeLibrary, "/mods/a/libone.so"), Is.True);
        Assert.That(ModOwnedResourceRegistry.Snapshot(first, includeRetired: false), Has.Count.EqualTo(1));
        Assert.That(ModOwnedResourceRegistry.Snapshot(second, includeRetired: false), Has.Count.EqualTo(1));

        Assert.That(ModOwnedResourceRegistry.Retire(first), Is.EqualTo(1));
        Assert.That(ModOwnedResourceRegistry.Snapshot(first, includeRetired: false), Is.Empty);
        Assert.That(ModOwnedResourceRegistry.Snapshot(second, includeRetired: false), Has.Count.EqualTo(1));
    }

    [Test]
    public void DuplicateResourceRegistrationIsIdempotent()
    {
        var key = new ModRuntimeKey("StArray.Android.Native", "native-b", 3);

        Assert.That(ModOwnedResourceRegistry.TryRegister(
            key, ModOwnedResourceKind.Hook, "target=0x10;detour=0x20"), Is.True);
        Assert.That(ModOwnedResourceRegistry.TryRegister(
            key, ModOwnedResourceKind.Hook, "target=0x10;detour=0x20"), Is.True);
        Assert.That(ModOwnedResourceRegistry.Snapshot(key), Has.Count.EqualTo(1));
    }

    [Test]
    public void MatchingRetirementOnlyTouchesOneResourceKind()
    {
        var key = new ModRuntimeKey("xphorror.PcModCompat", "pc-a", 4);
        Assert.That(ModOwnedResourceRegistry.TryRegister(
            key, ModOwnedResourceKind.Hook, "target=0x10;detour=0x20"), Is.True);
        Assert.That(ModOwnedResourceRegistry.TryRegister(
            key, ModOwnedResourceKind.Hud, "target=0x10"), Is.True);

        Assert.That(ModOwnedResourceRegistry.RetireMatching(
            key, ModOwnedResourceKind.Hook, "target=0x10;"), Is.EqualTo(1));
        var snapshot = ModOwnedResourceRegistry.Snapshot(key, includeRetired: false);
        Assert.That(snapshot, Has.Count.EqualTo(1));
        Assert.That(snapshot[0].Kind, Is.EqualTo(ModOwnedResourceKind.Hud));
    }

    [Test]
    public void AsyncOperationRetirementDoesNotRetireProviderOrResourceSession()
    {
        var key = new ModRuntimeKey("xphorror.PcModCompat", "pc-b", 5);
        Assert.That(ModOwnedResourceRegistry.TryRegister(
            key, ModOwnedResourceKind.AsyncOperation, "prepare-generation=1;"), Is.True);
        Assert.That(ModOwnedResourceRegistry.TryRegister(
            key, ModOwnedResourceKind.Provider, "pcmod-capability-session;"), Is.True);
        Assert.That(ModOwnedResourceRegistry.TryRegister(
            key, ModOwnedResourceKind.Resource, "resource-session;generation=5;"), Is.True);

        Assert.That(ModOwnedResourceRegistry.RetireMatching(
            key,
            ModOwnedResourceKind.AsyncOperation,
            "prepare-generation=1;"), Is.EqualTo(1));

        var active = ModOwnedResourceRegistry.Snapshot(key, includeRetired: false);
        Assert.Multiple(() =>
        {
            Assert.That(active, Has.Count.EqualTo(2));
            Assert.That(active.Select(resource => resource.Kind),
                Is.EquivalentTo(new[]
                {
                    ModOwnedResourceKind.Provider,
                    ModOwnedResourceKind.Resource
                }));
        });
    }

    [Test]
    public void UnityObjectsAreIndependentlyRetiredWithinOneGeneration()
    {
        var key = new ModRuntimeKey("xphorror.PcModCompat", "pc-c", 6);
        Assert.That(ModOwnedResourceRegistry.TryRegister(
            key, ModOwnedResourceKind.UnityObject, "managed-component=0x10;owner=0x20;"), Is.True);
        Assert.That(ModOwnedResourceRegistry.TryRegister(
            key, ModOwnedResourceKind.UnityObject, "native-component=0x30;owner=0x20;"), Is.True);

        Assert.That(ModOwnedResourceRegistry.RetireMatching(
            key,
            ModOwnedResourceKind.UnityObject,
            "managed-component=0x10;"), Is.EqualTo(1));

        var active = ModOwnedResourceRegistry.Snapshot(key, includeRetired: false);
        Assert.Multiple(() =>
        {
            Assert.That(active, Has.Count.EqualTo(1));
            Assert.That(active[0].Identity, Is.EqualTo("native-component=0x30;owner=0x20;"));
        });
    }

    [Test]
    public void AuditSnapshotAggregatesNativeAndPcCompatGenerations()
    {
        var nativeSession = CreateActiveSession("StArray.Android.Native", "native-a");
        var pcCompatSession = CreateActiveSession("xphorror.PcModCompat", "pc-a");
        Assert.That(ModOwnedResourceRegistry.TryRegister(
            nativeSession.CurrentKey,
            ModOwnedResourceKind.NativeLibrary,
            "/mods/native-a/libfeature.so"), Is.True);
        Assert.That(ModOwnedResourceRegistry.TryRegister(
            pcCompatSession.CurrentKey,
            ModOwnedResourceKind.Hud,
            "source=0x10;"), Is.True);

        var audit = ModOwnedResourceRegistry.CreateAuditSnapshot(new[]
        {
            nativeSession.Snapshot(),
            pcCompatSession.Snapshot()
        });

        Assert.Multiple(() =>
        {
            Assert.That(audit.HasLeaks, Is.False);
            Assert.That(audit.ActiveResources, Is.EqualTo(2));
            Assert.That(audit.Generations, Has.Count.EqualTo(2));
            Assert.That(audit.Generations.Select(item => item.Key.LoaderKind),
                Is.EquivalentTo(new[] { "StArray.Android.Native", "xphorror.PcModCompat" }));
            Assert.That(audit.Generations.Single(item =>
                    item.Key.LoaderKind == "StArray.Android.Native").ObservedResources,
                Is.EqualTo(1));
        });
        Assert.DoesNotThrow(audit.AssertNoLeaks);
    }

    [Test]
    public void AuditSnapshotRejectsActiveOrphanGeneration()
    {
        var stale = new ModRuntimeKey("StArray.Android.Native", "native-orphan", 7);
        Assert.That(ModOwnedResourceRegistry.TryRegister(
            stale,
            ModOwnedResourceKind.NativeLibrary,
            "/mods/native-orphan/libstale.so"), Is.True);

        var audit = ModOwnedResourceRegistry.CreateAuditSnapshot(
            Array.Empty<ModRuntimeSessionSnapshot>());

        Assert.Multiple(() =>
        {
            Assert.That(audit.HasLeaks, Is.True);
            Assert.That(audit.Issues, Has.Count.EqualTo(1));
            Assert.That(audit.Issues[0].Kind,
                Is.EqualTo(ModOwnedResourceAuditIssueKind.ActiveResourceWithoutRuntimeSession));
            Assert.That(audit.Issues[0].ResourceSequences, Is.EqualTo(new long[] { 1 }));
        });
        Assert.Throws<InvalidOperationException>(audit.AssertNoLeaks);
    }

    [Test]
    public void SuspendedGenerationOnlyRetainsDeclaredSuspendableResources()
    {
        var session = CreateActiveSession("xphorror.PcModCompat", "pc-suspended");
        var key = session.CurrentKey;
        Assert.That(ModOwnedResourceRegistry.TryRegister(
            key, ModOwnedResourceKind.Hook, "target=0x10;detour=0x20"), Is.True);
        Assert.That(ModOwnedResourceRegistry.TryRegister(
            key, ModOwnedResourceKind.Behaviour, "instance=0x30"), Is.True);
        Assert.That(ModOwnedResourceRegistry.TryRegister(
            key, ModOwnedResourceKind.NativeLibrary, "/mods/pc-suspended/libnative.so"), Is.True);
        Assert.That(ModOwnedResourceRegistry.TryRegister(
            key, ModOwnedResourceKind.Provider, "pcmod-capability-session;"), Is.True);
        Suspend(session, key);

        var rejected = ModOwnedResourceRegistry.CreateAuditSnapshot(new[] { session.Snapshot() });
        Assert.Multiple(() =>
        {
            Assert.That(rejected.Issues, Has.Count.EqualTo(1));
            Assert.That(rejected.Issues[0].Kind,
                Is.EqualTo(ModOwnedResourceAuditIssueKind.MustRetireResourceWhileSuspended));
            Assert.That(rejected.Issues[0].ResourceCount, Is.EqualTo(1));
        });

        Assert.That(ModOwnedResourceRegistry.RetireMatching(
            key, ModOwnedResourceKind.Provider, "pcmod-capability-session;"), Is.EqualTo(1));
        var accepted = ModOwnedResourceRegistry.CreateAuditSnapshot(new[] { session.Snapshot() });
        Assert.Multiple(() =>
        {
            Assert.That(accepted.HasLeaks, Is.False);
            Assert.That(accepted.Generations[0].SuspendRetainedResources, Is.EqualTo(2));
            Assert.That(accepted.Generations[0].ObservedResources, Is.EqualTo(1));
        });
    }

    [Test]
    public void RetiredGenerationCannotKeepActiveRegistryEntries()
    {
        var session = CreateActiveSession("StArray.Android.Native", "native-retired");
        var key = session.CurrentKey;
        Assert.That(ModOwnedResourceRegistry.TryRegister(
            key, ModOwnedResourceKind.UnityObject, "object=0x40;"), Is.True);
        Assert.That(session.TryBeginRetirement(key), Is.True);
        Assert.That(session.WaitForQuiescence(key, TimeSpan.Zero), Is.True);
        Assert.That(session.TryCompleteRetirement(key), Is.True);

        var audit = ModOwnedResourceRegistry.CreateAuditSnapshot(new[] { session.Snapshot() });
        Assert.Multiple(() =>
        {
            Assert.That(audit.HasLeaks, Is.True);
            Assert.That(audit.Issues[0].Kind,
                Is.EqualTo(ModOwnedResourceAuditIssueKind.ActiveResourceAfterTerminalState));
            Assert.That(audit.ToDiagnosticText(), Does.Contain(
                "issue kind=ActiveResourceAfterTerminalState"));
        });
    }

    [Test]
    public void DuplicateRegistrationCannotChangeRetirementPolicy()
    {
        var key = new ModRuntimeKey("StArray.Android.Native", "native-policy", 8);
        const string identity = "target=0x50;detour=0x60";
        Assert.That(ModOwnedResourceRegistry.TryRegister(
            key,
            ModOwnedResourceKind.Hook,
            identity,
            ModOwnedResourceRetirementPolicy.RetainWhileSuspended), Is.True);

        Assert.That(ModOwnedResourceRegistry.TryRegister(
            key,
            ModOwnedResourceKind.Hook,
            identity,
            ModOwnedResourceRetirementPolicy.MustRetire), Is.False);
        Assert.That(ModOwnedResourceRegistry.Snapshot(key), Has.Count.EqualTo(1));
        Assert.That(ModOwnedResourceRegistry.Snapshot(key)[0].RetirementPolicy,
            Is.EqualTo(ModOwnedResourceRetirementPolicy.RetainWhileSuspended));
    }

    [Test]
    public void ScopedAuditOnlyIncludesRequestedRuntimeGeneration()
    {
        var first = CreateActiveSession("xphorror.PcModCompat", "pc-first");
        var second = CreateActiveSession("xphorror.PcModCompat", "pc-second");
        Assert.That(ModOwnedResourceRegistry.TryRegister(
            first.CurrentKey, ModOwnedResourceKind.Hud, "source=0x70;"), Is.True);
        Assert.That(ModOwnedResourceRegistry.TryRegister(
            second.CurrentKey, ModOwnedResourceKind.Hud, "source=0x80;"), Is.True);

        var audit = ModOwnedResourceRegistry.CreateAuditSnapshot(
            new[] { first.Snapshot(), second.Snapshot() },
            first.CurrentKey);

        Assert.Multiple(() =>
        {
            Assert.That(audit.HasLeaks, Is.False);
            Assert.That(audit.Generations, Has.Count.EqualTo(1));
            Assert.That(audit.Generations[0].Key.Matches(first.CurrentKey), Is.True);
            Assert.That(audit.Generations[0].Resources[0].Identity, Is.EqualTo("source=0x70;"));
            Assert.That(audit.ToDiagnosticText(), Does.Not.Contain("pc-second"));
        });
    }

    private static ModRuntimeSession CreateActiveSession(string loaderKind, string modId)
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(loaderKind, modId);
        Assert.That(session.TryPublishActive(key), Is.True);
        return session;
    }

    private static void Suspend(ModRuntimeSession session, ModRuntimeKey key)
    {
        Assert.That(session.TryBeginRetirement(key), Is.True);
        Assert.That(session.WaitForQuiescence(key, TimeSpan.Zero), Is.True);
        Assert.That(session.TryCompleteSuspension(key), Is.True);
    }
}

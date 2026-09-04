using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace StArray.ModManager.Tests;

/// <summary>
/// Per-domain resource budgets: one MOD exhausting a shared Host resource must degrade only
/// itself, and must never reclaim or block another MOD.
/// </summary>
[NonParallelizable]
public sealed class ModResourceBudgetTests
{
    [SetUp]
    public void SetUp() => ModOwnedResourceRegistry.ClearForTests();

    [TearDown]
    public void TearDown() => ModOwnedResourceRegistry.ClearForTests();

    [Test]
    public void HardLimitRefusesOnlyTheOffendingOwner()
    {
        var greedy = CreateKey("budget-greedy");
        var neighbour = CreateKey("budget-neighbour");

        // NativeLibrary has the tightest ceiling (a MOD may only ship managed DLLs, so this is
        // a diagnosable downgrade to begin with).
        var accepted = 0;
        for (var i = 0; i < 32; i++)
        {
            if (ModOwnedResourceRegistry.TryRegister(
                    greedy, ModOwnedResourceKind.NativeLibrary, $"lib-{i}"))
            {
                accepted++;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.EqualTo(4), "registrations stop at the hard ceiling");
            Assert.That(
                ModOwnedResourceRegistry.TryRegister(
                    greedy, ModOwnedResourceKind.NativeLibrary, "one-more"),
                Is.False);

            // The neighbour's budget is untouched: budgets are per domain.
            Assert.That(
                ModOwnedResourceRegistry.TryRegister(
                    neighbour, ModOwnedResourceKind.NativeLibrary, "lib-0"),
                Is.True);

            // And a different kind for the same MOD is unaffected.
            Assert.That(
                ModOwnedResourceRegistry.TryRegister(
                    greedy, ModOwnedResourceKind.Hud, "hud-0"),
                Is.True);
        });
    }

    [Test]
    public void RetiringResourcesFreesBudgetSoAnExhaustedModCanUnloadAndReload()
    {
        var key = CreateKey("budget-retire");
        for (var i = 0; i < 8; i++)
            ModOwnedResourceRegistry.TryRegister(key, ModOwnedResourceKind.NativeLibrary, $"lib-{i}");
        Assert.That(
            ModOwnedResourceRegistry.TryRegister(key, ModOwnedResourceKind.NativeLibrary, "blocked"),
            Is.False,
            "precondition: the owner is at its ceiling");

        // Retirement must always be allowed — a MOD at its ceiling still has to unload cleanly.
        ModOwnedResourceRegistry.Retire(key);

        Assert.That(
            ModOwnedResourceRegistry.TryRegister(key, ModOwnedResourceKind.NativeLibrary, "after-retire"),
            Is.True,
            "freed budget must be reusable");
    }

    [Test]
    public void ProcessLifetimeKindsStayUnbounded()
    {
        var key = CreateKey("budget-hooks");

        // Hooks and code patches are permanent by design: retiring one only flips a logical
        // gate, so refusing a registration would desynchronize the registry from the physical
        // hook chain instead of protecting anything.
        for (var i = 0; i < 5000; i++)
        {
            Assert.That(
                ModOwnedResourceRegistry.TryRegister(key, ModOwnedResourceKind.Hook, $"hook-{i}"),
                Is.True);
        }
    }

    [Test]
    public void BudgetIsScopedToTheGenerationSoAReloadStartsFresh()
    {
        var session = new ModRuntimeSession();
        var first = session.BeginLoad(ModEntry.NativeLoaderKind, "budget-generation");
        Assert.That(session.TryPublishActive(first), Is.True);
        for (var i = 0; i < 8; i++)
            ModOwnedResourceRegistry.TryRegister(first, ModOwnedResourceKind.NativeLibrary, $"lib-{i}");
        Assert.That(
            ModOwnedResourceRegistry.TryRegister(first, ModOwnedResourceKind.NativeLibrary, "blocked"),
            Is.False);

        // A reload lands on a new generation; the previous generation's usage must not follow it.
        ModOwnedResourceRegistry.Retire(first);
        Assert.That(session.TryBeginRetirement(first), Is.True);
        Assert.That(session.TryCompleteRetirement(first), Is.True);
        var second = session.BeginLoad(ModEntry.NativeLoaderKind, "budget-generation");
        Assert.That(session.TryPublishActive(second), Is.True);

        Assert.That(
            ModOwnedResourceRegistry.TryRegister(second, ModOwnedResourceKind.NativeLibrary, "lib-0"),
            Is.True);
    }

    [Test]
    public void UsageDescriptionReportsSoftAndHardCrossings()
    {
        var atSoft = ModResourceBudget.Describe(ModOwnedResourceKind.Hud, 16);
        var belowSoft = ModResourceBudget.Describe(ModOwnedResourceKind.Hud, 1);
        var atHard = ModResourceBudget.Describe(ModOwnedResourceKind.Hud, 64);
        var unbounded = ModResourceBudget.Describe(ModOwnedResourceKind.Hook, 100000);

        Assert.Multiple(() =>
        {
            Assert.That(belowSoft.SoftExceeded, Is.False);
            Assert.That(atSoft.SoftExceeded, Is.True);
            Assert.That(atSoft.HardExceeded, Is.False, "soft is only a warning threshold");
            Assert.That(atHard.HardExceeded, Is.True);
            Assert.That(unbounded.HardExceeded, Is.False);
        });
    }

    private static ModRuntimeKey CreateKey(string id)
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, id);
        Assert.That(session.TryPublishActive(key), Is.True);
        return key;
    }
}

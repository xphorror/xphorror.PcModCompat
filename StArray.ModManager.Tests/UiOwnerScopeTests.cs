using StArray.ModManager.Runtime;

namespace StArray.ModManager.Tests;

/// <summary>
/// Every MOD draws into one shared Host ImGui context, so a MOD must not be able to collide
/// with another MOD's IDs or take the Host's frame down with it.
/// </summary>
[NonParallelizable]
public sealed class UiOwnerScopeTests
{
    private const string OwnerA = "native:ui-owner-a";
    private const string OwnerB = "native:ui-owner-b";

    [SetUp]
    public void SetUp() => UiOwnerScope.ClearForTests();

    [TearDown]
    public void TearDown() => UiOwnerScope.ClearForTests();

    [Test]
    public void SuccessfulDrawReportsSuccessAndKeepsOwnerUsable()
    {
        var ran = 0;
        Assert.Multiple(() =>
        {
            Assert.That(
                UiOwnerScope.TryDraw(OwnerA, 1, "probe", () => ran++),
                Is.True);
            Assert.That(ran, Is.EqualTo(1));
            Assert.That(UiOwnerScope.IsQuarantined(OwnerA, 1), Is.False);
        });
    }

    [Test]
    public void FaultingDrawIsContainedAndDoesNotPropagateToTheHostFrame()
    {
        // The Host draws MODs inside its own Begin/End pair; an exception escaping the MOD
        // callback would skip End and corrupt the whole frame's window stack.
        Assert.Multiple(() =>
        {
            Assert.That(
                () => UiOwnerScope.TryDraw(OwnerA, 1, "probe",
                    () => throw new InvalidOperationException("mod ui blew up")),
                Throws.Nothing,
                "a MOD fault must never reach the Host's draw loop");
            Assert.That(
                UiOwnerScope.TryDraw(OwnerA, 1, "probe", () => { }),
                Is.True,
                "a single fault must not disable the owner");
        });
    }

    [Test]
    public void RepeatedFaultsQuarantineOnlyTheFailingOwnerGeneration()
    {
        for (var i = 0; i < 4; i++)
            UiOwnerScope.TryDraw(OwnerA, 1, "probe", () => throw new InvalidOperationException("boom"));

        var ranAfterQuarantine = 0;
        var otherRan = 0;
        var nextGenerationRan = 0;

        Assert.Multiple(() =>
        {
            Assert.That(UiOwnerScope.IsQuarantined(OwnerA, 1), Is.True);
            Assert.That(
                UiOwnerScope.TryDraw(OwnerA, 1, "probe", () => ranAfterQuarantine++),
                Is.False);
            Assert.That(ranAfterQuarantine, Is.Zero, "a quarantined owner must not be drawn");

            // Another MOD is unaffected.
            Assert.That(UiOwnerScope.TryDraw(OwnerB, 1, "probe", () => otherRan++), Is.True);
            Assert.That(otherRan, Is.EqualTo(1));

            // Quarantine is generation-scoped: a reload gets a clean slate.
            Assert.That(UiOwnerScope.IsQuarantined(OwnerA, 2), Is.False);
            Assert.That(UiOwnerScope.TryDraw(OwnerA, 2, "probe", () => nextGenerationRan++), Is.True);
            Assert.That(nextGenerationRan, Is.EqualTo(1));
        });
    }

    [Test]
    public void InterleavedSuccessResetsTheFaultStreak()
    {
        // Only *consecutive* faults quarantine: a MOD that occasionally throws (a transient
        // scene-dependent draw) must keep working.
        for (var i = 0; i < 6; i++)
        {
            UiOwnerScope.TryDraw(OwnerA, 1, "probe", () => throw new InvalidOperationException("boom"));
            UiOwnerScope.TryDraw(OwnerA, 1, "probe", () => { });
        }

        Assert.That(UiOwnerScope.IsQuarantined(OwnerA, 1), Is.False);
    }

    [Test]
    public void ReleaseClearsQuarantineForThatGenerationOnly()
    {
        for (var i = 0; i < 4; i++)
            UiOwnerScope.TryDraw(OwnerA, 1, "probe", () => throw new InvalidOperationException("boom"));
        for (var i = 0; i < 4; i++)
            UiOwnerScope.TryDraw(OwnerB, 1, "probe", () => throw new InvalidOperationException("boom"));

        UiOwnerScope.Release(OwnerA, 1);

        Assert.Multiple(() =>
        {
            Assert.That(UiOwnerScope.IsQuarantined(OwnerA, 1), Is.False, "unload clears bookkeeping");
            Assert.That(UiOwnerScope.IsQuarantined(OwnerB, 1), Is.True, "another owner is untouched");
        });
    }
}

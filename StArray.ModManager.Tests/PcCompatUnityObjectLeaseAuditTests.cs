using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class PcCompatUnityObjectLeaseAuditTests
{
    [Test]
    public void EmptySessionAuditsAsClear()
    {
        var audit = PcCompatUnityObjectLeaseAudit.Snapshot("audit.empty", 5);

        Assert.Multiple(() =>
        {
            Assert.That(audit.ModId, Is.EqualTo("audit.empty"));
            Assert.That(audit.ResourceSessionGeneration, Is.EqualTo(5));
            Assert.That(audit.IsClear, Is.True, "no backend may hold anything for an unknown session");
        });
    }

    [Test]
    public void HudSurfaceRegistrationIsCountedPerOwnerAndGeneration()
    {
        // Unique owner ids keep this test independent of registry state left by others.
        var ownerId = "audit.hud-" + Guid.NewGuid().ToString("N");
        var otherId = "audit.other-" + Guid.NewGuid().ToString("N");
        var owner = new TestHudSource();
        var other = new TestHudSource();
        PcCompatUnityHudRuntime.RegisterSource(ownerId, 7, owner);
        PcCompatUnityHudRuntime.RegisterSource(otherId, 7, other);
        try
        {
            var matching = PcCompatUnityObjectLeaseAudit.Snapshot(ownerId, 7);
            var wrongGeneration = PcCompatUnityObjectLeaseAudit.Snapshot(ownerId, 8);

            Assert.Multiple(() =>
            {
                Assert.That(matching.HudSurfaces, Is.EqualTo(1));
                Assert.That(matching.IsClear, Is.False);
                // Generation mismatch must not count: a retired generation's surfaces are
                // not this session's lease inventory.
                Assert.That(wrongGeneration.HudSurfaces, Is.EqualTo(0));
                Assert.That(
                    PcCompatUnityObjectLeaseAudit.Snapshot(otherId, 7).HudSurfaces,
                    Is.EqualTo(1),
                    "another owner's surface must not leak into this audit");
            });
        }
        finally
        {
            PcCompatUnityHudRuntime.UnregisterSource(owner);
            PcCompatUnityHudRuntime.UnregisterSource(other);
        }
    }

    private sealed class TestHudSource : IPcCompatUnityHudSource
    {
        public bool TryGetUnityHudFrame(out PcCompatUnityHudFrame frame)
        {
            frame = null!;
            return false;
        }
    }
}

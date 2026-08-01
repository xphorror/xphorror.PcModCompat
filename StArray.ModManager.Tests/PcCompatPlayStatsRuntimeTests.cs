using System.Text.Json;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatPlayStatsRuntimeTests
{
    [Test]
    public void PersistsAttemptsAndBestByLevelStartAndSpeed()
    {
        var root = Path.Combine(Path.GetTempPath(), "pccompat-play-stats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var levelPath = Path.Combine(root, "test.adofai");
        File.WriteAllText(levelPath, "{\"angleData\":[0,90,180]}");
        PcCompatLevelIdentityRuntime.RegisterProvider(() => "path:" + levelPath);

        try
        {
            using (var session = new PcCompatPlayStatsSession(root))
            {
                var first = session.Update(Snapshot(showCount: 1, progress: 0.25f));
                Assert.That(first.Available, Is.True);
                Assert.That(first.Attempts, Is.EqualTo(1));

                var progressed = session.Update(Snapshot(showCount: 1, progress: 0.625f));
                Assert.That(progressed.DisplayBest, Is.EqualTo(0.625f));
                session.Update(Snapshot(showCount: 1, progress: 0.625f, deathCount: 1));
            }

            using (var session = new PcCompatPlayStatsSession(root))
            {
                var second = session.Update(Snapshot(showCount: 2, progress: 0.25f));
                Assert.That(second.Attempts, Is.EqualTo(2));
                Assert.That(second.PreviousBest, Is.EqualTo(0.625f));

                var otherSpeed = session.Update(Snapshot(
                    showCount: 3,
                    progress: 0.25f,
                    speed: 2.0f));
                Assert.That(otherSpeed.Attempts, Is.EqualTo(1));
                Assert.That(otherSpeed.PreviousBest, Is.Zero);
            }

            var storePath = Path.Combine(root, ".pccompat", "mobile_play_stats.json");
            Assert.That(File.Exists(storePath), Is.True);
            using var json = JsonDocument.Parse(File.ReadAllText(storePath));
            Assert.That(json.RootElement.GetProperty("Entries").EnumerateObject().Count(), Is.EqualTo(2));
        }
        finally
        {
            PcCompatLevelIdentityRuntime.ClearProvider();
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void AutoSessionsAndEmptyHidesDoNotPolluteAttemptsOrBest()
    {
        var root = Path.Combine(Path.GetTempPath(), "pccompat-play-stats-auto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        PcCompatLevelIdentityRuntime.RegisterProvider(() => "official:1-X");

        try
        {
            using var session = new PcCompatPlayStatsSession(root);
            var auto = session.Update(Snapshot(showCount: 1, progress: 0.4f, sessionAuto: true));
            Assert.That(auto.Attempts, Is.Zero);
            Assert.That(auto.DisplayBest, Is.Zero);

            var manual = session.Update(Snapshot(showCount: 2, progress: 0.25f));
            Assert.That(manual.Attempts, Is.EqualTo(1));
            var switchedToAuto = session.Update(Snapshot(showCount: 2, progress: 0.25f, sessionAuto: true));
            Assert.That(switchedToAuto.Attempts, Is.Zero);

            var nextManual = session.Update(Snapshot(showCount: 3, progress: 0.25f));
            Assert.That(nextManual.Attempts, Is.EqualTo(1));
            var hidden = session.Update(Snapshot(showCount: 3, progress: 0.25f, visible: false));
            Assert.That(hidden.Attempts, Is.Zero);
        }
        finally
        {
            PcCompatLevelIdentityRuntime.ClearProvider();
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void DefersSessionUntilNativeLevelIdentityIsAvailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "pccompat-play-stats-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var identity = string.Empty;
        PcCompatLevelIdentityRuntime.RegisterProvider(() => identity);

        try
        {
            using var session = new PcCompatPlayStatsSession(root);
            var pending = session.Update(Snapshot(showCount: 1, progress: 0.25f));
            Assert.That(pending.Available, Is.False);

            identity = "official:2-X";
            var ready = session.Update(Snapshot(showCount: 1, progress: 0.25f));
            Assert.That(ready.Available, Is.True);
            Assert.That(ready.Attempts, Is.EqualTo(1));
        }
        finally
        {
            PcCompatLevelIdentityRuntime.ClearProvider();
            Directory.Delete(root, recursive: true);
        }
    }

    private static PcCompatOverlaySnapshot Snapshot(
        uint showCount,
        float progress,
        float speed = 1.5f,
        uint deathCount = 0,
        bool sessionAuto = false,
        bool visible = true)
        => new()
        {
            ProviderAvailable = true,
            Visible = visible,
            ShowCount = showCount,
            Progress = progress,
            StartProgress = 0.25f,
            SpeedMultiplier = speed,
            DeathCount = deathCount,
            SessionAuto = sessionAuto
        };
}

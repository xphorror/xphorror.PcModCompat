using System.Text.Json;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatMobileSettingsTests
{
    [Test]
    public void MobileSettingsRoundTripUsesPcCompatPrivateDirectory()
    {
        var modRoot = Path.Combine(Path.GetTempPath(), "pccompat-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(modRoot);
        try
        {
            var settings = new PcCompatMobileSettings
            {
                ShowAccuracy = false,
                ShowXAccuracy = true,
                ShowLastJudgement = false,
                ShowHitTiming = true,
                ShowPlayerCount = true,
                ShowProgressBar = false,
                ShowMusicTime = false,
                ShowMapTime = true,
                ShowMapTimeIfMusicUnavailable = false,
                ShowCheckpoint = true,
                ShowBest = true,
                ShowKeyViewer = true,
                HudScale = 1.35f,
                PositionX = 41f,
                PositionY = 96f,
                BackgroundOpacity = 0.42f,
                ShowTechnicalDiagnostics = true
            };

            PcCompatMobileSettingsStore.Save(modRoot, settings);
            var restored = PcCompatMobileSettingsStore.Load(modRoot);

            Assert.That(restored.ShowAccuracy, Is.False);
            Assert.That(restored.ShowXAccuracy, Is.True);
            Assert.That(restored.ShowLastJudgement, Is.False);
            Assert.That(restored.ShowHitTiming, Is.True);
            Assert.That(restored.ShowPlayerCount, Is.True);
            Assert.That(restored.ShowProgressBar, Is.False);
            Assert.That(restored.ShowMusicTime, Is.False);
            Assert.That(restored.ShowMapTime, Is.True);
            Assert.That(restored.ShowMapTimeIfMusicUnavailable, Is.False);
            Assert.That(restored.ShowCheckpoint, Is.True);
            Assert.That(restored.ShowBest, Is.True);
            Assert.That(restored.ShowKeyViewer, Is.True);
            Assert.That(restored.HudScale, Is.EqualTo(1.35f));
            Assert.That(restored.PositionX, Is.EqualTo(41f));
            Assert.That(restored.PositionY, Is.EqualTo(96f));
            Assert.That(restored.BackgroundOpacity, Is.EqualTo(0.42f));
            Assert.That(restored.ShowTechnicalDiagnostics, Is.True);
            Assert.That(
                File.Exists(Path.Combine(modRoot, ".pccompat", "mobile_settings.json")),
                Is.True);
        }
        finally
        {
            if (Directory.Exists(modRoot))
                Directory.Delete(modRoot, recursive: true);
        }
    }

    [Test]
    public void MobileSettingsNormalizeClampsHudBounds()
    {
        var settings = new PcCompatMobileSettings
        {
            HudScale = 3.5f,
            PositionX = -10f,
            PositionY = 5000f
        };

        settings.Normalize();

        Assert.That(settings.HudScale, Is.EqualTo(2.5f));
        Assert.That(settings.PositionX, Is.EqualTo(0f));
        Assert.That(settings.PositionY, Is.EqualTo(4096f));
    }

    [Test]
    public void MissingProgressBarSettingDefaultsToEnabled()
    {
        var json = """
                   {
                     "showHud": true,
                     "showProgress": true
                   }
                   """;

        var settings = JsonSerializer.Deserialize<PcCompatMobileSettings>(
            json,
            new JsonSerializerOptions
            {
                IncludeFields = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

        Assert.That(settings, Is.Not.Null);
        Assert.That(settings!.ShowProgressBar, Is.True);
    }
}

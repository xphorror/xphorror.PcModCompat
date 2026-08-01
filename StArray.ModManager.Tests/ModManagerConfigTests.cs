using StArray.ModManager.Manager;
using StArray.ModManager.Resources;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class ModManagerConfigTests
{
    [Test]
    public void ExplicitLanguageSelectsMatchingResourceSet()
    {
        var original = L10n.CurrentLanguage;
        try
        {
            L10n.SetLanguage(L10n.EnglishLanguage);
            Assert.That(L10n.Get("Settings_Language"), Is.EqualTo("Language:"));

            L10n.SetLanguage(L10n.ChineseLanguage);
            Assert.That(L10n.Get("Settings_Language"), Is.EqualTo("界面语言:"));
        }
        finally
        {
            L10n.SetLanguage(original);
        }
    }

    [Test]
    public void LanguageRoundTripsInGlobalConfig()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "modmanager-config-" + Guid.NewGuid().ToString("N"));
        try
        {
            new ModManagerConfig
            {
                Language = L10n.EnglishLanguage
            }.Save(directory);

            var restored = ModManagerConfig.Load(directory);
            Assert.That(restored.Language, Is.EqualTo(L10n.EnglishLanguage));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("fr-FR")]
    public void UnsupportedLanguageFallsBackToChinese(string? language)
    {
        var config = new ModManagerConfig { Language = language! };

        config.Normalize();

        Assert.That(config.Language, Is.EqualTo(L10n.ChineseLanguage));
    }

    [Test]
    public void TouchKeyViewerMappingModeRoundTripsInGlobalConfig()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "modmanager-config-" + Guid.NewGuid().ToString("N"));
        try
        {
            new ModManagerConfig
            {
                TouchKeyViewerMappingMode = PcCompatTouchLaneMappingMode.TouchContacts,
                TouchKeyViewerContactReuseDelayMilliseconds = 125
            }.Save(directory);

            var restored = ModManagerConfig.Load(directory);
            Assert.Multiple(() =>
            {
                Assert.That(restored.TouchKeyViewerMappingMode,
                    Is.EqualTo(PcCompatTouchLaneMappingMode.TouchContacts));
                Assert.That(restored.TouchKeyViewerContactReuseDelayMilliseconds,
                    Is.EqualTo(125));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void InvalidTouchKeyViewerMappingModeFallsBackToScreenRegions()
    {
        var config = new ModManagerConfig
        {
            TouchKeyViewerMappingMode = (PcCompatTouchLaneMappingMode)99
        };

        config.Normalize();

        Assert.That(config.TouchKeyViewerMappingMode,
            Is.EqualTo(PcCompatTouchLaneMappingMode.ScreenRegions));
    }

    [TestCase(-1, 0)]
    [TestCase(501, 500)]
    public void TouchContactReuseDelayIsClamped(int configured, int expected)
    {
        var config = new ModManagerConfig
        {
            TouchKeyViewerContactReuseDelayMilliseconds = configured
        };

        config.Normalize();

        Assert.That(config.TouchKeyViewerContactReuseDelayMilliseconds,
            Is.EqualTo(expected));
    }
}

using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class PcCompatResourceChangerRuntimeTests
{
    [TearDown]
    public void TearDown()
    {
        PcCompatResourceChangerRuntime.ClearSettingsSink();
        PcCompatResourceChangerRuntime.Remove("JipperResourcePack");
        PcCompatResourceChangerRuntime.Remove("SecondResourcePack");
    }

    [Test]
    public void MobileFallbackPublishesJipperDefaultsWithoutFilesystemResourcePath()
    {
        PcCompatResourceChangerState? published = null;
        PcCompatResourceChangerRuntime.RegisterSettingsSink(state => published = state);
        var settings = new PcCompatMobileSettings
        {
            ResourceChangerChangeRabbit = false,
            ResourceChangerChangeBallColor = true,
            ResourceChangerChangeTileColor = false
        };

        Assert.That(
            PcCompatResourceChangerRuntime.TryApply("JipperResourcePack", settings),
            Is.True);

        Assert.That(published, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(published!.ModId, Is.EqualTo("JipperResourcePack"));
            Assert.That(published.SessionGeneration, Is.Zero);
            Assert.That(published.ChangeRabbit, Is.False);
            Assert.That(published.ChangeBallColor, Is.True);
            Assert.That(published.ChangeTileColor, Is.False);
            Assert.That(published.PlanetColor, Is.EqualTo(
                new PcCompatResourceColor(0.8125f, 0.70703125f, 0.96875f, 1f)));
            Assert.That(published.ResourcePackName, Is.EqualTo("Jipper Resource Pack"));
            Assert.That(published.ManagedSource, Is.False);
        });

        Assert.That(PcCompatResourceChangerRuntime.TryDisable("JipperResourcePack"), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(published.ChangeRabbit, Is.False);
            Assert.That(published.ChangeBallColor, Is.False);
            Assert.That(published.ChangeTileColor, Is.False);
        });
    }

    [Test]
    public void SettingsPublishedBeforeSinkRegistrationAreReplayedAndCanBeRepublished()
    {
        var settings = new PcCompatMobileSettings
        {
            ResourceChangerChangeRabbit = true,
            ResourceChangerChangeBallColor = false,
            ResourceChangerChangeTileColor = true
        };
        var published = new List<PcCompatResourceChangerState>();

        Assert.That(PcCompatResourceChangerRuntime.IsSettingsSinkRegistered, Is.False);
        Assert.That(
            PcCompatResourceChangerRuntime.TryApply("JipperResourcePack", settings),
            Is.False);

        PcCompatResourceChangerRuntime.RegisterSettingsSink(published.Add);

        Assert.Multiple(() =>
        {
            Assert.That(PcCompatResourceChangerRuntime.IsSettingsSinkRegistered, Is.True);
            Assert.That(published, Has.Count.EqualTo(1));
            Assert.That(published[0].ChangeRabbit, Is.True);
            Assert.That(published[0].ChangeBallColor, Is.False);
            Assert.That(published[0].ChangeTileColor, Is.True);
        });

        Assert.That(
            PcCompatResourceChangerRuntime.TryRepublish("JipperResourcePack"),
            Is.True);
        Assert.That(published, Has.Count.EqualTo(2));
    }

    [Test]
    public void DisablingOneOwnerDoesNotRemoveAnotherOwnersState()
    {
        var published = new List<PcCompatResourceChangerState>();
        PcCompatResourceChangerRuntime.RegisterSettingsSink(published.Add);

        Assert.That(PcCompatResourceChangerRuntime.TryApply(
            "JipperResourcePack",
            new PcCompatMobileSettings
            {
                ResourceChangerChangeRabbit = true,
                ResourceChangerChangeBallColor = true
            }), Is.True);
        Assert.That(PcCompatResourceChangerRuntime.TryApply(
            "SecondResourcePack",
            new PcCompatMobileSettings
            {
                ResourceChangerChangeTileColor = true
            }), Is.True);

        Assert.That(PcCompatResourceChangerRuntime.TryDisable("SecondResourcePack"), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(published, Has.Count.EqualTo(3));
            Assert.That(published[^1].ModId, Is.EqualTo("SecondResourcePack"));
            Assert.That(published[^1].ChangeRabbit, Is.False);
            Assert.That(published[^1].ChangeBallColor, Is.False);
            Assert.That(published[^1].ChangeTileColor, Is.False);
            Assert.That(
                PcCompatResourceChangerRuntime.TryGetState(
                    "JipperResourcePack",
                    out var jipper),
                Is.True);
            Assert.That(jipper.ChangeRabbit, Is.True);
            Assert.That(jipper.ChangeBallColor, Is.True);
        });
    }
}

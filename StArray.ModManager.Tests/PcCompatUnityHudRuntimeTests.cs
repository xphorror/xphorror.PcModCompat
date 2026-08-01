using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class PcCompatUnityHudRuntimeTests
{
    private readonly List<IPcCompatUnityHudSource> _sources = new();

    private sealed class Source(PcCompatUnityHudFrame frame) : IPcCompatUnityHudSource
    {
        public bool TryGetUnityHudFrame(out PcCompatUnityHudFrame result)
        {
            result = frame;
            return true;
        }
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var source in _sources)
            PcCompatUnityHudRuntime.UnregisterSource(source);
        _sources.Clear();
        PcCompatUnityHudRuntime.RegisterSourcesChangedSink(null);
        PcCompatUnityHudRuntime.MarkRendererFailed();
    }

    [Test]
    public void RegisteredRendererDisablesFallbackUntilFailure()
    {
        PcCompatUnityHudRuntime.MarkRendererFailed();
        Assert.That(PcCompatUnityHudRuntime.RendererAvailable, Is.False);

        PcCompatUnityHudRuntime.RegisterRenderer();
        Assert.That(PcCompatUnityHudRuntime.RendererAvailable, Is.True);

        PcCompatUnityHudRuntime.MarkRendererFailed();
        Assert.That(PcCompatUnityHudRuntime.RendererAvailable, Is.False);
    }

    [Test]
    public void RegisteredSourcePublishesCachedFrameAndCanBeRemoved()
    {
        var expected = new PcCompatUnityHudFrame
        {
            Visible = true,
            OverlayGeneration = 42,
            RichText = "Accuracy 100%",
            ProgressBarVisible = true,
            ProgressBarValue = 0.5f
        };
        var source = new Source(expected);
        _sources.Add(source);

        PcCompatUnityHudRuntime.RegisterSource(source);
        Assert.That(PcCompatUnityHudRuntime.TryGetFrame(out var actual), Is.True);
        Assert.That(actual, Is.SameAs(expected));
        Assert.That(actual.ProgressBarVisible, Is.True);
        Assert.That(actual.ProgressBarValue, Is.EqualTo(0.5f));

        PcCompatUnityHudRuntime.UnregisterSource(source);
        Assert.That(PcCompatUnityHudRuntime.TryGetFrame(out _), Is.False);
    }

    [Test]
    public void RemovingLastSourceNotifiesRendererToHidePersistentCanvas()
    {
        var source = new Source(new PcCompatUnityHudFrame
        {
            ModId = "test",
            Visible = true,
            PlainText = "T1"
        });
        _sources.Add(source);
        var notifications = 0;
        PcCompatUnityHudRuntime.RegisterSourcesChangedSink(
            () => notifications++);

        PcCompatUnityHudRuntime.RegisterSource(source);
        notifications = 0;
        PcCompatUnityHudRuntime.UnregisterSource(source);

        Assert.Multiple(() =>
        {
            Assert.That(notifications, Is.EqualTo(1));
            Assert.That(PcCompatUnityHudRuntime.TryGetFrame(out _), Is.False);
        });
    }

    [Test]
    public void MostRecentlyRegisteredVisibleSourceWinsWithoutIdentityRules()
    {
        var hidden = new Source(new PcCompatUnityHudFrame { Visible = false, PlainText = "hidden" });
        var first = new Source(new PcCompatUnityHudFrame { Visible = true, PlainText = "first" });
        var second = new Source(new PcCompatUnityHudFrame { Visible = true, PlainText = "second" });
        _sources.AddRange(new[] { hidden, first, second });

        PcCompatUnityHudRuntime.RegisterSource(hidden);
        PcCompatUnityHudRuntime.RegisterSource(first);
        PcCompatUnityHudRuntime.RegisterSource(second);

        Assert.That(PcCompatUnityHudRuntime.TryGetFrame(out var frame), Is.True);
        Assert.That(frame.PlainText, Is.EqualTo("second"));

        PcCompatUnityHudRuntime.UnregisterSource(second);
        Assert.That(PcCompatUnityHudRuntime.TryGetFrame(out frame), Is.True);
        Assert.That(frame.PlainText, Is.EqualTo("first"));
    }
}

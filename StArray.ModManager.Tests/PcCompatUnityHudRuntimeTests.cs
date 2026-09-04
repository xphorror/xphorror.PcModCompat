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

    private sealed class ThrowingSource : IPcCompatUnityHudSource
    {
        public bool TryGetUnityHudFrame(out PcCompatUnityHudFrame frame)
        {
            frame = null!;
            throw new InvalidOperationException("source failed");
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
    public void LegacyFrameSelectionKeepsMostRecentlyRegisteredVisibleSource()
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

    [Test]
    public void ExplicitOwnersPublishIndependentSnapshots()
    {
        var first = new Source(new PcCompatUnityHudFrame
        {
            ModId = "first",
            Visible = true,
            PlainText = "A"
        });
        var second = new Source(new PcCompatUnityHudFrame
        {
            ModId = "second",
            Visible = true,
            PlainText = "B"
        });
        _sources.AddRange(new[] { first, second });

        PcCompatUnityHudRuntime.RegisterSource("owner-a", first);
        PcCompatUnityHudRuntime.RegisterSource("owner-b", second);

        var snapshots = PcCompatUnityHudRuntime.SnapshotSources();
        Assert.Multiple(() =>
        {
            Assert.That(snapshots.Select(snapshot => snapshot.OwnerId),
                Is.EqualTo(new[] { "owner-a", "owner-b" }));
            Assert.That(snapshots[0].Frame?.PlainText, Is.EqualTo("A"));
            Assert.That(snapshots[1].Frame?.PlainText, Is.EqualTo("B"));
            Assert.That(snapshots.All(snapshot => snapshot.Error == null), Is.True);
        });
    }

    [Test]
    public void ExplicitOwnerSnapshotCarriesSessionGeneration()
    {
        var source = new Source(new PcCompatUnityHudFrame
        {
            ModId = "owner-a",
            Visible = true
        });
        _sources.Add(source);

        PcCompatUnityHudRuntime.RegisterSource("owner-a", 37, source);

        var snapshot = PcCompatUnityHudRuntime.SnapshotSources().Single();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.OwnerId, Is.EqualTo("owner-a"));
            Assert.That(snapshot.SessionGeneration, Is.EqualTo(37));
        });
    }

    [Test]
    public void SourceFailureDoesNotBlockAnotherOwner()
    {
        var failed = new ThrowingSource();
        var healthy = new Source(new PcCompatUnityHudFrame
        {
            Visible = true,
            PlainText = "healthy"
        });
        _sources.AddRange(new IPcCompatUnityHudSource[] { failed, healthy });

        PcCompatUnityHudRuntime.RegisterSource("failed", failed);
        PcCompatUnityHudRuntime.RegisterSource("healthy", healthy);

        var snapshots = PcCompatUnityHudRuntime.SnapshotSources();
        Assert.Multiple(() =>
        {
            Assert.That(snapshots[0].OwnerId, Is.EqualTo("failed"));
            Assert.That(snapshots[0].Error, Is.TypeOf<InvalidOperationException>());
            Assert.That(snapshots[1].OwnerId, Is.EqualTo("healthy"));
            Assert.That(snapshots[1].Frame?.PlainText, Is.EqualTo("healthy"));
        });
    }

    [Test]
    public void DuplicateOwnerRegistrationIsRejected()
    {
        var first = new Source(new PcCompatUnityHudFrame());
        var second = new Source(new PcCompatUnityHudFrame());
        _sources.AddRange(new[] { first, second });

        PcCompatUnityHudRuntime.RegisterSource("same-owner", first);

        Assert.That(
            () => PcCompatUnityHudRuntime.RegisterSource("SAME-OWNER", second),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(PcCompatUnityHudRuntime.SnapshotSources(), Has.Count.EqualTo(1));
    }

    [Test]
    public void RendererFailureIsQuarantinedPerOwner()
    {
        var first = new Source(new PcCompatUnityHudFrame());
        var second = new Source(new PcCompatUnityHudFrame());
        _sources.AddRange(new[] { first, second });
        PcCompatUnityHudRuntime.RegisterSource("owner-a", first);
        PcCompatUnityHudRuntime.RegisterSource("owner-b", second);
        PcCompatUnityHudRuntime.RegisterRenderer();

        PcCompatUnityHudRuntime.MarkSourceRendererFailed("owner-a");

        Assert.Multiple(() =>
        {
            Assert.That(PcCompatUnityHudRuntime.RendererAvailableFor("owner-a"), Is.False);
            Assert.That(PcCompatUnityHudRuntime.RendererAvailableFor("owner-b"), Is.True);
        });

        PcCompatUnityHudRuntime.ClearSourceRendererFailure("owner-a");
        Assert.That(PcCompatUnityHudRuntime.RendererAvailableFor("owner-a"), Is.True);
    }

    [Test]
    public void UnregisterRemovesOnlyMatchingOwner()
    {
        var first = new Source(new PcCompatUnityHudFrame { PlainText = "A" });
        var second = new Source(new PcCompatUnityHudFrame { PlainText = "B" });
        _sources.AddRange(new[] { first, second });
        PcCompatUnityHudRuntime.RegisterSource("owner-a", first);
        PcCompatUnityHudRuntime.RegisterSource("owner-b", second);

        PcCompatUnityHudRuntime.UnregisterSource(first);

        var remaining = PcCompatUnityHudRuntime.SnapshotSources();
        Assert.Multiple(() =>
        {
            Assert.That(remaining, Has.Count.EqualTo(1));
            Assert.That(remaining[0].OwnerId, Is.EqualTo("owner-b"));
            Assert.That(remaining[0].Frame?.PlainText, Is.EqualTo("B"));
        });
    }
}

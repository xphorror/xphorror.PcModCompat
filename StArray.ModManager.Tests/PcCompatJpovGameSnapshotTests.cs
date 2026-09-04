using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatJpovGameSnapshotTests
{
    [TearDown]
    public void TearDown()
        => StArray.ModManager.Android.PcCompat.PcCompatDynamicGetterSnapshotHost
            .ClearPublishedSnapshotForTests();

    [Test]
    public void RealJpovRecipePublishesSharedGameplayTelemetry()
    {
        var modDirectory = Path.Combine(FindRepoRoot(), "JipperOverlayer-UMM");
        Assume.That(Directory.Exists(modDirectory), Is.True,
            $"missing JPOV release directory: {modDirectory}");
        Assert.That(
            PcModManifestReader.TryRead(modDirectory, out var manifest, out var manifestError),
            Is.True,
            manifestError);

        var scan = PcCompatStaticPatchScanner.Scan(manifest, targetGameRevision: 143);
        var translation = PcCompatCallbackTranslator.Translate(manifest, scan);
        Assert.That(
            PcCompatRecipeCompiler.TryCompile(
                manifest,
                scan,
                translation,
                out var recipe,
                out var recipeError),
            Is.True,
            recipeError);

        var telemetry = recipe.Rules.Single(rule =>
            rule.TargetType == "scrController" &&
            rule.TargetMethod == "PlayerControl_Update" &&
            rule.Op == PcCompatRuleOp.OverlayPollTelemetry);
        Assert.Multiple(() =>
        {
            Assert.That(telemetry.DefaultEnabled, Is.True);
            Assert.That(
                telemetry.RequiredCapabilities,
                Is.EqualTo(
                    PcCompatCapability.AfterOriginalObserve |
                    PcCompatCapability.ReadIl2CppField |
                    PcCompatCapability.CallIl2CppGetter));
        });
    }

    [Test]
    public void OverlayConversionPublishesExplicitValidityAndSessionGeneration()
    {
        var overlay = new PcCompatOverlaySnapshot
        {
            ProviderAvailable = true,
            Generation = 17,
            SessionEpoch = 4,
            HasExplicitGameSnapshotValidity = true,
            ValidGameSnapshotFields = PcCompatGameSnapshotFields.State |
                                      PcCompatGameSnapshotFields.Progress,
            AccuracySnapshotCount = 2,
            BpmSnapshotCount = 0,
            TimelineSnapshotCount = 3,
            PercentAcc = 0.99f,
            CurrentSeqId = 31,
            IsPaused = true,
            ControllerPointer = 0x1000,
            ConductorPointer = 0x2000,
            LevelMakerPointer = 0x3000,
            CurrentFloorPointer = 0x4000,
            FirstFloorPointer = 0x5000,
            SongPointer = 0x6000,
            PlanetarySystemPointer = 0x7000
        };

        var snapshot = PcCompatGameSnapshot.FromOverlay(overlay, 9);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Has(PcCompatGameSnapshotFields.Accuracy, 9), Is.False);
            Assert.That(snapshot.Has(PcCompatGameSnapshotFields.Progress, 9), Is.True);
            Assert.That(snapshot.Has(PcCompatGameSnapshotFields.Timeline, 9), Is.False);
            Assert.That(snapshot.Has(PcCompatGameSnapshotFields.State, 9), Is.True);
            Assert.That(snapshot.Has(PcCompatGameSnapshotFields.Bpm, 9), Is.False);
            Assert.That(snapshot.Has(PcCompatGameSnapshotFields.Accuracy, 10), Is.False);
            Assert.That(snapshot.Generation, Is.EqualTo(17));
            Assert.That(snapshot.SessionEpoch, Is.EqualTo(4));
            Assert.That(snapshot.ResourceSessionGeneration, Is.EqualTo(9));
            Assert.That(snapshot.ControllerPointer, Is.EqualTo(0x1000));
            Assert.That(snapshot.ConductorPointer, Is.EqualTo(0x2000));
            Assert.That(snapshot.LevelMakerPointer, Is.EqualTo(0x3000));
            Assert.That(snapshot.CurrentFloorPointer, Is.EqualTo(0x4000));
            Assert.That(snapshot.FirstFloorPointer, Is.EqualTo(0x5000));
            Assert.That(snapshot.SongPointer, Is.EqualTo(0x6000));
            Assert.That(snapshot.PlanetarySystemPointer, Is.EqualTo(0x7000));
        });
    }

    [Test]
    [NonParallelizable]
    public void TelemetryFieldsSurviveAccuracyRefreshAndRemainAvailableThroughBridgeGetters()
    {
        var execution = new PcCompatManagedExecutionState(
            "JipperOverlayer-snapshot-" + Guid.NewGuid().ToString("N"),
            1,
            PcCompatManagedExecutionPhase.Update);

        using (PcCompatManagedExecutionContext.Enter(execution))
        {
            PcCompatReversePatchBridge.RegisterSnapshotRefresh(null);
            PcCompatReversePatchBridge.PublishSnapshot(new PcCompatGameSnapshot
            {
                Generation = 9,
                ResourceSessionGeneration = 1,
                ValidFields = PcCompatGameSnapshotFields.All,
                AccuracySnapshotCount = 4,
                PercentAcc = 0.991f,
                PercentXAcc = 0.982f,
                Progress = 0.625f,
                CurrentSeqId = 37,
                CheckpointsUsed = 2,
                CurrentCheckpoint = 3,
                TotalCheckpoints = 5,
                FloorCount = 120,
                StartProgress = 0.125f,
                SpeedMultiplier = 1.25f,
                MusicTime = 12.5f,
                MusicTotalTime = 98.5f,
                MapTime = 11.75f,
                MapTotalTime = 101.25f,
                BpmSnapshotCount = 7,
                TileBpm = 240f,
                Kps = 4f,
                TimelineSnapshotCount = 9,
                PlanetSpeed = 1.1d,
                PlayerCount = 1
            });

            // Accuracy is published independently by the native margin path. It must
            // update only accuracy and retain the rest of the same game snapshot.
            PcCompatReversePatchBridge.PublishAccuracySnapshot(0.995f, 0.99f);

            var snapshot = PcCompatReversePatchBridge.Snapshot();
            Assert.Multiple(() =>
            {
                Assert.That(snapshot.AccuracySnapshotCount, Is.EqualTo(4));
                Assert.That(snapshot.Generation, Is.EqualTo(9));
                Assert.That(snapshot.ResourceSessionGeneration, Is.EqualTo(1));
                Assert.That(snapshot.ValidFields, Is.EqualTo(PcCompatGameSnapshotFields.All));
                Assert.That(snapshot.PercentAcc, Is.EqualTo(0.995f));
                Assert.That(snapshot.PercentXAcc, Is.EqualTo(0.99f));
                Assert.That(snapshot.CurrentCheckpoint, Is.EqualTo(3));
                Assert.That(snapshot.TotalCheckpoints, Is.EqualTo(5));
                Assert.That(snapshot.FloorCount, Is.EqualTo(120));
                Assert.That(snapshot.StartProgress, Is.EqualTo(0.125f));
                Assert.That(snapshot.SpeedMultiplier, Is.EqualTo(1.25f));
                Assert.That(snapshot.MusicTime, Is.EqualTo(12.5f));
                Assert.That(snapshot.MusicTotalTime, Is.EqualTo(98.5f));
                Assert.That(snapshot.MapTime, Is.EqualTo(11.75f));
                Assert.That(snapshot.MapTotalTime, Is.EqualTo(101.25f));
                Assert.That(snapshot.BpmSnapshotCount, Is.EqualTo(7));
                Assert.That(snapshot.TileBpm, Is.EqualTo(240f));
                Assert.That(snapshot.Kps, Is.EqualTo(4f));
                Assert.That(snapshot.TimelineSnapshotCount, Is.EqualTo(9));
                Assert.That(PcCompatReversePatchBridge.GetMusicTime(), Is.EqualTo(12.5f));
                Assert.That(PcCompatReversePatchBridge.GetMusicTotalTime(), Is.EqualTo(98.5f));
                Assert.That(PcCompatReversePatchBridge.GetMapTime(), Is.EqualTo(11.75f));
                Assert.That(PcCompatReversePatchBridge.GetMapTotalTime(), Is.EqualTo(101.25f));
                Assert.That(PcCompatReversePatchBridge.GetCurrentCheckpoint(), Is.EqualTo(3));
                Assert.That(PcCompatReversePatchBridge.GetTotalCheckpoints(), Is.EqualTo(5));
                Assert.That(PcCompatReversePatchBridge.GetFloorCount(), Is.EqualTo(120));
                Assert.That(PcCompatReversePatchBridge.GetStartProgress(), Is.EqualTo(0.125f));
                Assert.That(PcCompatReversePatchBridge.GetSpeedMultiplier(), Is.EqualTo(1.25f));
                Assert.That(PcCompatReversePatchBridge.GetTileBpm(), Is.EqualTo(240f));
                Assert.That(PcCompatReversePatchBridge.GetKps(), Is.EqualTo(4f));
            });
        }
    }

    [Test]
    public void SnapshotObjectRootsAreMappedByGameTypeAndMemberSemantics()
    {
        var snapshot = new PcCompatGameSnapshot
        {
            ControllerPointer = 0x1000,
            ConductorPointer = 0x2000,
            LevelMakerPointer = 0x3000,
            CurrentFloorPointer = 0x4000,
            FirstFloorPointer = 0x5000,
            SongPointer = 0x6000,
            PlanetarySystemPointer = 0x7000
        };
        var cases = new (Type Type, string Member, long Pointer, string Assembly, string Proxy)[]
        {
            (typeof(ADOBase), "controller", 0x1000, "Assembly-CSharp", "scrController"),
            (typeof(ADOBase), "conductor", 0x2000, "Assembly-CSharp", "scrConductor"),
            (typeof(ADOBase), "lm", 0x3000, "Assembly-CSharp", "scrLevelMaker"),
            (typeof(scrController), "currFloor", 0x4000, "Assembly-CSharp", "scrFloor"),
            (typeof(scrController), "firstFloor", 0x5000, "Assembly-CSharp", "scrFloor"),
            (typeof(scrConductor), "song", 0x6000, "UnityEngine.AudioModule", "UnityEngine.AudioSource"),
            (typeof(scrController), "planetarySystem", 0x7000, "Assembly-CSharp", "PlanetarySystem")
        };

        Assert.Multiple(() =>
        {
            foreach (var item in cases)
            {
                Assert.That(
                    StArray.ModManager.Android.PcCompat.PcCompatDynamicGetterSnapshotHost.TryResolveObjectRoot(
                        item.Type,
                        item.Member,
                        snapshot,
                        out var pointer,
                        out var assembly,
                        out var proxy),
                    Is.True,
                    $"{item.Type.Name}.{item.Member}");
                Assert.That(pointer, Is.EqualTo(item.Pointer));
                Assert.That(assembly, Is.EqualTo(item.Assembly));
                Assert.That(proxy, Is.EqualTo(item.Proxy));
            }
        });
    }

    [Test]
    public void FrameSnapshotPublicationReusesOneConversionPerResourceGeneration()
    {
        var host = typeof(StArray.ModManager.Android.PcCompat.PcCompatDynamicGetterSnapshotHost);
        var overlay = new PcCompatOverlaySnapshot
        {
            ProviderAvailable = true,
            Generation = 31,
            SessionEpoch = 7,
            HasExplicitGameSnapshotValidity = true,
            ValidGameSnapshotFields = PcCompatGameSnapshotFields.All,
            Progress = 0.5f
        };
        StArray.ModManager.Android.PcCompat.PcCompatDynamicGetterSnapshotHost
            .PublishOverlaySnapshot(overlay);

        var first = StArray.ModManager.Android.PcCompat.PcCompatDynamicGetterSnapshotHost
            .GetPublishedSnapshotForTests(4);
        var repeated = StArray.ModManager.Android.PcCompat.PcCompatDynamicGetterSnapshotHost
            .GetPublishedSnapshotForTests(4);
        var otherOwner = StArray.ModManager.Android.PcCompat.PcCompatDynamicGetterSnapshotHost
            .GetPublishedSnapshotForTests(5);

        Assert.Multiple(() =>
        {
            Assert.That(host, Is.Not.Null);
            Assert.That(repeated, Is.SameAs(first));
            Assert.That(otherOwner, Is.Not.SameAs(first));
            Assert.That(first.Generation, Is.EqualTo(31));
            Assert.That(first.SessionEpoch, Is.EqualTo(7));
            Assert.That(first.ResourceSessionGeneration, Is.EqualTo(4));
        });

        StArray.ModManager.Android.PcCompat.PcCompatDynamicGetterSnapshotHost
            .PublishOverlaySnapshot(new PcCompatOverlaySnapshot
            {
                ProviderAvailable = true,
                Generation = 32,
                SessionEpoch = 8,
                HasExplicitGameSnapshotValidity = true,
                ValidGameSnapshotFields = PcCompatGameSnapshotFields.All
            });
        var next = StArray.ModManager.Android.PcCompat.PcCompatDynamicGetterSnapshotHost
            .GetPublishedSnapshotForTests(4);
        Assert.That(next, Is.Not.SameAs(first));
    }

    private sealed class ADOBase;
    private sealed class scrController;
    private sealed class scrConductor;

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "xphorror.PcModCompat")) &&
                Directory.Exists(Path.Combine(directory.FullName, "StArray.ModManager.Tests")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate StArray.ModManager repository root.");
    }
}

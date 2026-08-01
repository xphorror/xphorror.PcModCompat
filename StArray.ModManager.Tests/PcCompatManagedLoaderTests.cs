using Xphorror.PcModCompat;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace StArray.ModManager.Tests;

public class PcCompatManagedLoaderTests
{
    [Test]
    public void HostRuntimeAssemblyCannotBeShadowedByModFolderCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pccompat-shared-alc-{Guid.NewGuid():N}");
        var modDir = Path.Combine(root, "mod");
        var shimDir = Path.Combine(root, "shims");
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(shimDir);
        var shared = typeof(PcCompatManagedLoader).Assembly;
        var duplicate = Path.Combine(modDir, shared.GetName().Name + ".dll");
        File.Copy(shared.Location, duplicate);

        var context = new PcCompatAssemblyLoadContext("shared-alc-test", modDir, shimDir);
        try
        {
            var resolved = context.LoadFromAssemblyName(shared.GetName());
            Assert.That(resolved, Is.SameAs(shared));
            Assert.That(AssemblyLoadContext.GetLoadContext(resolved), Is.SameAs(AssemblyLoadContext.Default));
        }
        finally
        {
            context.Unload();
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void DefaultShimResolutionFindsRepositoryBuildOutput()
    {
        var repoRoot = FindRepoRoot();
        var expected = Path.Combine(repoRoot, "xphorror.PcModCompat", "out", "shims");
        Assume.That(Directory.Exists(expected), Is.True, $"missing shim dir: {expected}");
        var manifest = new PcModManifest
        {
            Id = "shim-resolution-fixture",
            DisplayName = "shim-resolution-fixture",
            FolderPath = Path.Combine(Path.GetTempPath(), "shim-resolution-fixture"),
        };

        var resolved = PcCompatManagedLoader.ResolveShimFolder(manifest);

        Assert.That(resolved, Is.EqualTo(Path.GetFullPath(expected)));
    }

    [Test]
    public void RejectsDirectPcAssemblyExecutionWithoutExplicitLegacyTestGate()
    {
        var repoRoot = FindRepoRoot();
        var modDir = Path.Combine(repoRoot, "JipperResourcePack_release");
        var shimDir = Path.Combine(repoRoot, "xphorror.PcModCompat", "out", "shims");

        Assume.That(Directory.Exists(modDir), Is.True, $"missing sample mod dir: {modDir}");
        Assume.That(Directory.Exists(shimDir), Is.True, $"missing shim dir: {shimDir}");
        Assert.That(PcModManifestReader.TryRead(modDir, out var manifest, out var error), Is.True, error);

        Assert.That(
            () => PcCompatManagedLoader.Load(manifest, new PcCompatLoadOptions
            {
                ShimFolder = shimDir,
                TryBootstrap = false
            }),
            Throws.TypeOf<NotSupportedException>()
                .With.Message.Contains("generated Il2CppInterop proxies"));
    }

    [Test]
    public void LoadsJipperResourcePackToRegisteredPatchSnapshot()
    {
        var repoRoot = FindRepoRoot();
        var modDir = Path.Combine(repoRoot, "JipperResourcePack_release");
        var shimDir = Path.Combine(repoRoot, "xphorror.PcModCompat", "out", "shims");

        Assume.That(Directory.Exists(modDir), Is.True, $"missing sample mod dir: {modDir}");
        Assume.That(Directory.Exists(shimDir), Is.True, $"missing shim dir: {shimDir}");

        var ok = PcModManifestReader.TryRead(modDir, out var manifest, out var error);
        Assert.That(ok, Is.True, error);

        using var session = PcCompatManagedLoader.Load(manifest, new PcCompatLoadOptions
        {
            ShimFolder = shimDir,
            AllowLegacyStubExecution = true,
            TryBootstrap = true,
            Enable = false
        });

        var oracle = session.RegisteredPatches.Where(patch => patch.Source == "managed_oracle").ToArray();

        Assert.That(session.SetupCompleted, Is.True);
        Assert.That(session.EnableCompleted, Is.False);
        // Counted per source, because the snapshot now merges both logical registries and a bare total
        // could no longer say which side changed.
        Assert.That(oracle, Has.Length.EqualTo(16));
        Assert.That(oracle.Count(patch => patch.Kind == PcCompatPatchKind.ReversePatch), Is.EqualTo(9));
        Assert.That(oracle.Count(patch => patch.Kind == PcCompatPatchKind.Postfix), Is.EqualTo(7));
        Assert.That(session.RegisteredPatches.All(patch => patch.Status == PcCompatPatchStatus.RegisteredOnly), Is.True);
        Assert.That(oracle.Select(patch => patch.TargetMethod), Does.Contain("GetHitMarginsCount"));
        Assert.That(oracle.Select(patch => patch.TargetType + "." + patch.TargetMethod), Does.Contain("scnGame.Play"));
    }

    [Test]
    public void BootstrapTimeHarmonyPatchSurvivesIntoTheSnapshot()
    {
        // JALib's own bootstrap installs a Harmony prefix on Type.GetConstructor before CompatSetup runs.
        // The registry used to be cleared after bootstrap, which dropped it without a trace - so this is
        // the case that proves both the Harmony registry wiring and the clear ordering.
        var repoRoot = FindRepoRoot();
        var modDir = Path.Combine(repoRoot, "JipperResourcePack_release");
        var shimDir = Path.Combine(repoRoot, "xphorror.PcModCompat", "out", "shims");

        Assume.That(Directory.Exists(modDir), Is.True, $"missing sample mod dir: {modDir}");
        Assume.That(Directory.Exists(shimDir), Is.True, $"missing shim dir: {shimDir}");
        Assert.That(PcModManifestReader.TryRead(modDir, out var manifest, out var error), Is.True, error);

        using var session = PcCompatManagedLoader.Load(manifest, new PcCompatLoadOptions
        {
            ShimFolder = shimDir,
            AllowLegacyStubExecution = true,
            TryBootstrap = true,
            Enable = false
        });

        var harmony = session.RegisteredPatches
            .Where(patch => patch.Source == "shim_harmony_registry")
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(
                harmony.Select(patch => $"{patch.Kind} {patch.TargetType}.{patch.TargetMethod}"),
                Does.Contain("Prefix System.Type.GetConstructor"));
            Assert.That(harmony.All(patch => patch.Status == PcCompatPatchStatus.RegisteredOnly), Is.True);
            // The reason must name the physical-hook owner, since a registered_only descriptor otherwise
            // reads as "the patch is live".
            Assert.That(harmony.Select(patch => patch.Reason), Has.All.Contains("HookBroker"));
            Assert.That(session.HarmonyShimStatus, Does.StartWith("registrations=1 active=1"));
        });
    }

    [Test]
    public void LoadedJipperOriginalSettingsEmitsRootControlAndAllFeatureSections()
    {
        var repoRoot = FindRepoRoot();
        var modDir = Path.Combine(repoRoot, "JipperResourcePack_release");
        var shimDir = Path.Combine(repoRoot, "xphorror.PcModCompat", "out", "shims");
        Assume.That(Directory.Exists(modDir), Is.True, $"missing sample mod dir: {modDir}");
        Assume.That(Directory.Exists(shimDir), Is.True, $"missing shim dir: {shimDir}");
        Assert.That(PcModManifestReader.TryRead(
            modDir,
            out var manifest,
            out var manifestError), Is.True, manifestError);

        using var session = PcCompatManagedLoader.Load(manifest, new PcCompatLoadOptions
        {
            ShimFolder = shimDir,
            AllowLegacyStubExecution = true,
            TryBootstrap = false,
            Enable = false
        });
        var context = AssemblyLoadContext.GetLoadContext(session.Assembly)
            ?? throw new InvalidOperationException("Jipper load context is unavailable");
        var umm = context.Assemblies.Single(assembly =>
            assembly.GetName().Name == "UnityModManager");
        var bridge = umm.GetType(
            "UnityModManagerNet.PcCompatSettingsUiBridge",
            throwOnError: true)!;
        var backend = new JipperSettingsProbe();
        bridge.GetMethod(
            "Register",
            BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [backend]);
        var type = session.Instance.GetType();
        var overlayType = session.Assembly.GetType(
            "JipperResourcePack.OverlayContents.Overlay",
            throwOnError: true)!;
        overlayType.GetField(
            "Instance",
            BindingFlags.Public | BindingFlags.Static)!.SetValue(
            null,
            RuntimeHelpers.GetUninitializedObject(overlayType));

        type.GetMethod("CompatOpenGUI", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(session.Instance, null);
        type.GetMethod("CompatOnGUI", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(session.Instance, null);

        Assert.Multiple(() =>
        {
            Assert.That(backend.NumberLabels, Does.Contain("Size"));
            Assert.That(backend.SectionLabels, Has.Count.EqualTo(8));
            Assert.That(backend.SectionLabels, Does.Contain("Status"));
            Assert.That(backend.SectionLabels, Does.Contain("Key Viewer"));
            Assert.That(backend.SectionLabels, Does.Not.Contain("Feature.Status"));
        });
    }

    [Test]
    public void RegistryCanResolveJipperReversePatchForNativeBridge()
    {
        var repoRoot = FindRepoRoot();
        var modDir = Path.Combine(repoRoot, "JipperResourcePack_release");
        var shimDir = Path.Combine(repoRoot, "xphorror.PcModCompat", "out", "shims");

        Assume.That(Directory.Exists(modDir), Is.True, $"missing sample mod dir: {modDir}");
        Assume.That(Directory.Exists(shimDir), Is.True, $"missing shim dir: {shimDir}");

        var ok = PcModManifestReader.TryRead(modDir, out var manifest, out var error);
        Assert.That(ok, Is.True, error);

        using var session = PcCompatManagedLoader.Load(manifest, new PcCompatLoadOptions
        {
            ShimFolder = shimDir,
            AllowLegacyStubExecution = true,
            TryBootstrap = true,
            Enable = false
        });

        var registry = new PcCompatPatchRegistry();
        foreach (var patch in session.RegisteredPatches)
            registry.Register(patch);

        Assert.That(registry.SnapshotByKind(PcCompatPatchKind.ReversePatch), Has.Count.EqualTo(9));

        var byTarget = registry.FindByTarget("JipperResourcePack.VersionSafe", "GetHitMarginsCount");
        Assert.That(byTarget, Has.Count.EqualTo(1));
        Assert.That(byTarget[0].CallbackType, Is.EqualTo("JipperResourcePack.VersionSafe"));
        Assert.That(byTarget[0].CallbackMethod, Is.EqualTo("GetHitMarginsCountR141"));

        var callback = registry.FindCallback("JipperResourcePack.VersionSafe", "GetHitMarginsCountR141");
        Assert.That(callback, Is.Not.Null);
        Assert.That(callback!.TargetType, Is.EqualTo("JipperResourcePack.VersionSafe"));

        var updated = registry.UpdateStatus(
            manifest.Id,
            "JipperResourcePack.VersionSafe",
            "GetHitMarginsCountR141",
            PcCompatPatchStatus.Supported,
            "mapped by native bridge");

        Assert.That(updated, Is.True);
        Assert.That(registry.FindCallback("JipperResourcePack.VersionSafe", "GetHitMarginsCountR141")!.Status, Is.EqualTo(PcCompatPatchStatus.Supported));
        Assert.That(registry.FindCallback("JipperResourcePack.VersionSafe", "GetHitMarginsCountR141")!.Reason, Is.EqualTo("mapped by native bridge"));
    }

    private sealed class JipperSettingsProbe
    {
        public List<string> NumberLabels { get; } = [];
        public List<string> SectionLabels { get; } = [];

        public void BeginFrame(string title) { }
        public int EndFrame() => 0;
        public void AbortFrame() { }
        public bool CanApplyStructureChanges() => true;

        public void ReleaseInputFocus()
        {
        }
        public bool Toggle(bool value, string label) => value;
        public string Text(string value, string label) => value;
        public string Number(
            string value,
            string label,
            double min,
            double max,
            double step,
            bool integral)
        {
            NumberLabels.Add(label);
            return value;
        }
        public string SliderNumber(
            string value,
            string label,
            double min,
            double max,
            bool integral)
        {
            NumberLabels.Add(label);
            return value;
        }
        public string Enum(string value, string label, string[] values) => value;
        public int Section(
            bool enabled,
            bool expanded,
            bool canEnable,
            bool canExpand,
            string label)
        {
            SectionLabels.Add(label);
            return (enabled ? 1 : 0) | (expanded ? 2 : 0);
        }
        public void BeginSectionBody() { }
        public void EndSectionBody() { }
        public bool Button(string label) => false;
        public void Label(string label) { }
    }

    [Test]
    public void JipperReversePatchSnapshotHasBridgeHandlers()
    {
        var repoRoot = FindRepoRoot();
        var modDir = Path.Combine(repoRoot, "JipperResourcePack_release");
        var shimDir = Path.Combine(repoRoot, "xphorror.PcModCompat", "out", "shims");

        Assume.That(Directory.Exists(modDir), Is.True, $"missing sample mod dir: {modDir}");
        Assume.That(Directory.Exists(shimDir), Is.True, $"missing shim dir: {shimDir}");

        var ok = PcModManifestReader.TryRead(modDir, out var manifest, out var error);
        Assert.That(ok, Is.True, error);

        using var session = PcCompatManagedLoader.Load(manifest, new PcCompatLoadOptions
        {
            ShimFolder = shimDir,
            AllowLegacyStubExecution = true,
            TryBootstrap = true,
            Enable = false
        });

        foreach (var patch in session.RegisteredPatches.Where(patch => patch.Kind == PcCompatPatchKind.ReversePatch))
        {
            Assert.That(
                PcCompatReversePatchBridge.TryFindHandler(patch.TargetType, patch.TargetMethod, out var handler),
                Is.True,
                $"missing bridge handler for {patch.TargetType}.{patch.TargetMethod}");

            Assert.That(handler!.AndroidBridgeMethod, Is.Not.Empty);
        }
    }

    [Test]
    public void ReversePatchHandlersMatchOperationSemanticsInsteadOfDeclaringType()
    {
        Assert.That(
            PcCompatReversePatchBridge.TryFindHandler(
                "AnyPcMod.RuntimeBridge",
                "GetPlayerCount",
                out var handler),
            Is.True);
        Assert.That(handler!.TargetType, Is.EqualTo("*"));
        Assert.That(handler.AndroidBridgeMethod, Is.EqualTo(nameof(PcCompatReversePatchBridge.GetPlayerCount)));

        Assert.That(
            PcCompatReversePatchBridge.TryFindHandler(
                "AnyPcMod.RuntimeBridge",
                "UnknownOperation",
                out _),
            Is.False);
    }

    [Test]
    public void ReversePatchBridgeKeepsOneStableArrayWithoutAliasingPublishedInput()
    {
        PcCompatReversePatchBridge.InitializeHitMarginsCountLayout(12);
        var published = Enumerable.Range(1, 12).ToArray();
        PcCompatReversePatchBridge.PublishSnapshot(new PcCompatGameSnapshot
        {
            HitMarginsCount = published,
            PlanetSpeed = 1.25,
            PercentAcc = 98.5f,
            PercentXAcc = 97.25f,
            PlayerCount = 2,
            SceneName = "scnEditor"
        });

        var first = PcCompatReversePatchBridge.GetHitMarginsCount();
        published[0] = 99;

        Assert.That(PcCompatReversePatchBridge.GetHitMarginsCount(), Is.SameAs(first));
        Assert.That(PcCompatReversePatchBridge.GetHitMarginsCount()[0], Is.EqualTo(1));
        Assert.That(PcCompatReversePatchBridge.GetPlanetSpeed(), Is.EqualTo(1.25));
        Assert.That(PcCompatReversePatchBridge.GetPercentAcc(), Is.EqualTo(98.5f));
        Assert.That(PcCompatReversePatchBridge.GetPercentXAcc(), Is.EqualTo(97.25f));
        Assert.That(PcCompatReversePatchBridge.IsCoopMode(), Is.True);
        Assert.That(PcCompatReversePatchBridge.GetPlayerCount(), Is.EqualTo(2));
    }

    [TestCase(0d)]
    [TestCase(-1d)]
    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    public void ReversePatchBridgeRejectsInvalidPlanetSpeed(double publishedSpeed)
    {
        PcCompatReversePatchBridge.PublishSnapshot(new PcCompatGameSnapshot
        {
            PlanetSpeed = publishedSpeed
        });

        Assert.That(PcCompatReversePatchBridge.GetPlanetSpeed(), Is.EqualTo(1d));
    }

    [Test]
    public void ReversePatchBridgeClearsSessionCountsWithoutChangingArrayIdentity()
    {
        PcCompatReversePatchBridge.InitializeHitMarginsCountLayout(12);
        PcCompatReversePatchBridge.PublishHitMarginsCount(
            Enumerable.Range(10, 12).ToArray());
        var retainedByMod = PcCompatReversePatchBridge.GetHitMarginsCount();

        PcCompatReversePatchBridge.ClearHitMarginsCount();

        Assert.That(PcCompatReversePatchBridge.GetHitMarginsCount(), Is.SameAs(retainedByMod));
        Assert.That(retainedByMod, Is.All.Zero);

        PcCompatReversePatchBridge.PublishHitMarginsCount(
            Enumerable.Range(20, 12).ToArray());
        Assert.That(PcCompatReversePatchBridge.GetHitMarginsCount(), Is.SameAs(retainedByMod));
        Assert.That(retainedByMod[0], Is.EqualTo(20));
    }

    [Test]
    public void ReversePatchBridgeRefreshesAccuracyFromRegisteredNativeProvider()
    {
        var refreshCount = 0;
        PcCompatReversePatchBridge.PublishSnapshot(new PcCompatGameSnapshot
        {
            PlanetSpeed = 1.5,
            PercentAcc = 0.1f,
            PercentXAcc = 0.2f,
            PlayerCount = 2,
            SceneName = "scnGame"
        });
        PcCompatReversePatchBridge.RegisterSnapshotRefresh(() =>
        {
            refreshCount++;
            PcCompatReversePatchBridge.PublishAccuracySnapshot(0.991f, 0.982f);
        });

        try
        {
            var snapshot = PcCompatReversePatchBridge.Snapshot();

            Assert.That(refreshCount, Is.EqualTo(1));
            Assert.That(snapshot.PercentAcc, Is.EqualTo(0.991f));
            Assert.That(snapshot.PercentXAcc, Is.EqualTo(0.982f));
            Assert.That(snapshot.PlanetSpeed, Is.EqualTo(1.5));
            Assert.That(snapshot.PlayerCount, Is.EqualTo(2));
            Assert.That(snapshot.SceneName, Is.EqualTo("scnGame"));
        }
        finally
        {
            PcCompatReversePatchBridge.RegisterSnapshotRefresh(null);
        }
    }

    [Test]
    public void NativeBridgeEntryPointsPublishSnapshotAndPatchStatus()
    {
        PcCompatReversePatchBridge.InitializeHitMarginsCountLayout(12);
        var hitMargins = Enumerable.Range(10, 12).ToArray();
        PcCompatNativeBridge.PublishGameSnapshot(
            hitMargins,
            planetSpeed: 2.5,
            percentAcc: 99.1f,
            percentXAcc: 98.2f,
            playerCount: 0,
            sceneName: "scnGame");
        hitMargins[0] = 99;

        Assert.That(PcCompatReversePatchBridge.GetHitMarginsCount()[0], Is.EqualTo(10));
        Assert.That(PcCompatReversePatchBridge.GetPlanetSpeed(), Is.EqualTo(2.5));
        Assert.That(PcCompatReversePatchBridge.GetPlayerCount(), Is.EqualTo(1));

        PcCompatReversePatchBridge.LoadScene("scnEditor");
        Assert.That(PcCompatNativeBridge.ConsumeRequestedSceneName(), Is.EqualTo("scnEditor"));
        Assert.That(PcCompatNativeBridge.ConsumeRequestedSceneName(), Is.Null);

        var modId = "test-" + Guid.NewGuid().ToString("N");
        PcCompatRuntime.PatchRegistry.Register(new PcCompatPatchDescriptor
        {
            ModId = modId,
            TargetType = "JipperResourcePack.VersionSafe",
            TargetMethod = "GetHitMarginsCount",
            Kind = PcCompatPatchKind.ReversePatch,
            CallbackType = "JipperResourcePack.VersionSafe",
            CallbackMethod = "GetHitMarginsCountR141"
        });

        try
        {
            var updated = PcCompatNativeBridge.UpdatePatchStatus(
                modId,
                "JipperResourcePack.VersionSafe",
                "GetHitMarginsCountR141",
                status: 1,
                reason: "native snapshot bridge ready");

            Assert.That(updated, Is.True);
            var patch = PcCompatRuntime.PatchRegistry.FindCallback("JipperResourcePack.VersionSafe", "GetHitMarginsCountR141");
            Assert.That(patch, Is.Not.Null);
            Assert.That(patch!.Status, Is.EqualTo(PcCompatPatchStatus.Supported));
            Assert.That(patch.Reason, Is.EqualTo("native snapshot bridge ready"));
        }
        finally
        {
            PcCompatRuntime.PatchRegistry.RemoveMod(modId);
        }
    }

    [Test]
    public void OverlayRuntimeProviderPublishesSnapshots()
    {
        try
        {
            PcCompatOverlayRuntime.RegisterProvider(
                () => new PcCompatOverlaySnapshot
                {
                    ProviderAvailable = true,
                    Generation = 12,
                    Visible = true,
                    Practice = true,
                    ShowCount = 2,
                    HideCount = 1,
                    PlayerUpdateCount = 3,
                    StateChangeCount = 4,
                    LastOpCode = (int)PcCompatRuleOp.OverlayShowPractice,
                    LastTargetKind = 1,
                    PlayerCount = 2,
                    LastSeqId = 3,
                    LastIsRestart = true,
                    LastWipeDirection = 1,
                    LastResetToEditor = false,
                    JudgementHitCount = 7,
                    JudgementResetCount = 2,
                    LastHitMargin = 10,
                    AccuracySnapshotCount = 8,
                    PercentAcc = 1.0007f,
                    PercentXAcc = 0.9875f,
                    InstalledSlots = 5,
                    BoundDispatcherSlots = 7,
                    DispatcherReadySlots = 6,
                    DispatcherCapacity = 32
                },
                () => true);

            var snapshot = PcCompatOverlayRuntime.Snapshot();

            Assert.That(snapshot.ProviderAvailable, Is.True);
            Assert.That(snapshot.Generation, Is.EqualTo(12));
            Assert.That(snapshot.Visible, Is.True);
            Assert.That(snapshot.Practice, Is.True);
            Assert.That(snapshot.LastOpName, Is.EqualTo("OverlayShowPractice"));
            Assert.That(snapshot.LastTargetName, Is.EqualTo("scnGame.Play"));
            Assert.That(snapshot.PlayerCount, Is.EqualTo(2));
            Assert.That(snapshot.LastSeqId, Is.EqualTo(3));
            Assert.That(snapshot.LastIsRestart, Is.True);
            Assert.That(snapshot.LastWipeDirection, Is.EqualTo(1));
            Assert.That(snapshot.JudgementHitCount, Is.EqualTo(7));
            Assert.That(snapshot.JudgementResetCount, Is.EqualTo(2));
            Assert.That(snapshot.LastHitMarginName, Is.EqualTo("Auto"));
            Assert.That(snapshot.AccuracyAvailable, Is.True);
            Assert.That(snapshot.PercentAcc, Is.EqualTo(1.0007f));
            Assert.That(snapshot.PercentXAcc, Is.EqualTo(0.9875f));
            Assert.That(snapshot.InstalledSlots, Is.EqualTo(5));
            Assert.That(snapshot.BoundDispatcherSlots, Is.EqualTo(7));
            Assert.That(snapshot.DispatcherCapacity, Is.EqualTo(32));
            Assert.That(PcCompatOverlayRuntime.IsVisible(), Is.True);
        }
        finally
        {
            PcCompatOverlayRuntime.ClearProvider();
        }

        Assert.That(PcCompatOverlayRuntime.Snapshot().ProviderAvailable, Is.False);
        Assert.That(PcCompatOverlayRuntime.IsVisible(), Is.False);
    }

    [Test]
    public void OverlayAccuracyRequiresFinitePublishedSnapshot()
    {
        Assert.That(new PcCompatOverlaySnapshot
        {
            AccuracySnapshotCount = 1,
            PercentAcc = float.NaN,
            PercentXAcc = 1f
        }.AccuracyAvailable, Is.False);
        Assert.That(new PcCompatOverlaySnapshot
        {
            AccuracySnapshotCount = 0,
            PercentAcc = 1f,
            PercentXAcc = 1f
        }.AccuracyAvailable, Is.False);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(TestContext.CurrentContext.WorkDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "xphorror.PcModCompat")) &&
                Directory.Exists(Path.Combine(current.FullName, "StArray.ModManager")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate StArray.ModManager repo root");
    }
}

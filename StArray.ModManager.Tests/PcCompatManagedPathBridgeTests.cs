using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class PcCompatManagedPathBridgeTests
{
    private const string ModId = "pccompat.path.test";
    private const string OtherModId = "pccompat.path.other";
    private const long Generation = 41;

    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        PcCompatManagedPathBridge.ClearAllRootsForTests();
        _root = Path.Combine(Path.GetTempPath(), "pccompat-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        PcCompatManagedPathBridge.ClearAllRootsForTests();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void FilesystemAccessWithoutManagedScopeIsRejected()
    {
        Bind(ModId, Generation);
        Assert.Multiple(() =>
        {
            Assert.That(
                () => PcCompatManagedPathBridge.GetFullPath("settings.json"),
                Throws.InvalidOperationException);
            Assert.That(
                () => PcCompatManagedPathBridge.FileExists("settings.json"),
                Throws.InvalidOperationException);
        });
    }

    [Test]
    public void FilesystemAccessWithoutBoundRootsIsRejected()
    {
        using var scope = EnterEnable(ModId, Generation);
        Assert.That(
            () => PcCompatManagedPathBridge.GetFullPath("settings.json"),
            Throws.InvalidOperationException.With.Message.Contains("not bound"));
    }

    [Test]
    public void RelativePathResolvesUnderConfigRootNotProcessWorkingDirectory()
    {
        var roots = Bind(ModId, Generation);
        using var scope = EnterEnable(ModId, Generation);

        var resolved = PcCompatManagedPathBridge.GetFullPath("KeyCodes.json");

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.EqualTo(Path.Combine(roots.ConfigRoot, "KeyCodes.json")));
            Assert.That(
                resolved,
                Does.Not.StartWith(Path.GetFullPath(Environment.CurrentDirectory)),
                "relative MOD paths must not resolve against the shared working directory");
        });
    }

    [Test]
    public void WritesRoundTripInsideTheConfigRoot()
    {
        Bind(ModId, Generation);
        using var scope = EnterEnable(ModId, Generation);

        PcCompatManagedPathBridge.FileWriteAllText("Plays.dat", "42");

        Assert.Multiple(() =>
        {
            Assert.That(PcCompatManagedPathBridge.FileExists("Plays.dat"), Is.True);
            Assert.That(PcCompatManagedPathBridge.FileReadAllText("Plays.dat"), Is.EqualTo("42"));
        });
    }

    [Test]
    public void InstallRootWritesShadowIntoTheDataOverlayAndLegacyFilesStayReadable()
    {
        var roots = Bind(ModId, Generation);
        // Legacy file shipped with the MOD (or written before isolation): package layer only.
        var legacy = Path.Combine(roots.InstallRoot, "Settings.json");
        File.WriteAllText(legacy, "legacy");
        using var scope = EnterEnable(ModId, Generation);

        Assert.Multiple(() =>
        {
            // Package layer stays readable in place — no migration copy needed.
            Assert.That(PcCompatManagedPathBridge.FileReadAllText(legacy), Is.EqualTo("legacy"));

            // A write aimed at the install root lands in the overlay with the same relative
            // layout and then shadows the legacy file for reads.
            PcCompatManagedPathBridge.FileWriteAllText(legacy, "saved");
            Assert.That(PcCompatManagedPathBridge.FileReadAllText(legacy), Is.EqualTo("saved"));
            Assert.That(File.ReadAllText(Path.Combine(roots.DataOverlayRoot, "Settings.json")), Is.EqualTo("saved"));
            Assert.That(File.ReadAllText(legacy), Is.EqualTo("legacy"), "package layer must stay untouched");

            // Deleting removes the shadow; the immutable package copy resurfaces for reads.
            PcCompatManagedPathBridge.FileDelete(legacy);
            Assert.That(PcCompatManagedPathBridge.FileExists(legacy), Is.True);
            Assert.That(PcCompatManagedPathBridge.FileReadAllText(legacy), Is.EqualTo("legacy"));
        });
    }

    [Test]
    public void KeyViewerStyleCountRotationRoundTripsThroughTheOverlay()
    {
        var roots = Bind(ModId, Generation);
        using var scope = EnterEnable(ModId, Generation);
        var dat = Path.Combine(roots.InstallRoot, "KeyCount.dat");
        var bak = dat + ".bak";

        // Jipper's SaveData loop: rotate dat -> .bak, then recreate dat.
        PcCompatManagedPathBridge.FileWriteAllText(dat, "counts-v1");
        PcCompatManagedPathBridge.FileDelete(bak);
        PcCompatManagedPathBridge.FileMove(dat, bak);
        PcCompatManagedPathBridge.FileWriteAllText(dat, "counts-v2");

        Assert.Multiple(() =>
        {
            Assert.That(PcCompatManagedPathBridge.FileReadAllText(bak), Is.EqualTo("counts-v1"));
            Assert.That(PcCompatManagedPathBridge.FileReadAllText(dat), Is.EqualTo("counts-v2"));
            // Both files live in the overlay; nothing was written into the install tree.
            Assert.That(File.ReadAllText(Path.Combine(roots.DataOverlayRoot, "KeyCount.dat")), Is.EqualTo("counts-v2"));
            Assert.That(File.ReadAllText(Path.Combine(roots.DataOverlayRoot, "KeyCount.dat.bak")), Is.EqualTo("counts-v1"));
            Assert.That(Directory.GetFiles(roots.InstallRoot), Is.Empty, "package layer must stay immutable");
        });
    }

    [Test]
    public void MoveOfPackageOnlySourceEmulatesIntoTheOverlay()
    {
        var roots = Bind(ModId, Generation);
        var dat = Path.Combine(roots.InstallRoot, "KeyCount.dat");
        File.WriteAllText(dat, "pre-isolation");
        using var scope = EnterEnable(ModId, Generation);
        var bak = dat + ".bak";

        PcCompatManagedPathBridge.FileMove(dat, bak);

        Assert.Multiple(() =>
        {
            // The rotation target exists in the overlay...
            Assert.That(PcCompatManagedPathBridge.FileReadAllText(bak), Is.EqualTo("pre-isolation"));
            Assert.That(File.Exists(Path.Combine(roots.DataOverlayRoot, "KeyCount.dat.bak")), Is.True);
            // ...and the immutable package original remains readable (documented shadowing
            // divergence from raw Move: the layer cannot be mutated).
            Assert.That(PcCompatManagedPathBridge.FileExists(dat), Is.True);
            Assert.That(File.ReadAllText(dat), Is.EqualTo("pre-isolation"));
        });
    }

    [Test]
    public void SharedGameResourcesAreReadableButNotWritable()
    {
        var shared = Path.Combine(_root, "game-assets");
        Directory.CreateDirectory(shared);
        var asset = Path.Combine(shared, "official.bundle");
        File.WriteAllText(asset, "x");
        Bind(ModId, Generation, [shared]);
        using var scope = EnterEnable(ModId, Generation);

        Assert.Multiple(() =>
        {
            Assert.That(PcCompatManagedPathBridge.FileExists(asset), Is.True);
            Assert.That(
                () => PcCompatManagedPathBridge.FileWriteAllText(asset, "y"),
                Throws.InvalidOperationException.With.Message.Contains("read-only"));
        });
    }

    [Test]
    public void AnotherModsRootIsRejectedAndNamesTheOwner()
    {
        Bind(ModId, Generation);
        var victimRoots = Bind(OtherModId, Generation);
        var victimFile = Path.Combine(victimRoots.ConfigRoot, "victim.json");
        File.WriteAllText(victimFile, "x");

        using var scope = EnterEnable(ModId, Generation);

        Assert.Multiple(() =>
        {
            Assert.That(
                () => PcCompatManagedPathBridge.FileDelete(victimFile),
                Throws.InvalidOperationException.With.Message.Contains(OtherModId));
            Assert.That(File.Exists(victimFile), Is.True, "denied access must not touch the file");
        });
    }

    [Test]
    public void OlderGenerationOfTheSameModIsAlsoAForeignOwner()
    {
        var oldRoots = Bind(ModId, Generation - 1);
        Bind(ModId, Generation);
        var oldFile = Path.Combine(oldRoots.ConfigRoot, "stale.json");
        File.WriteAllText(oldFile, "x");

        using var scope = EnterEnable(ModId, Generation);

        Assert.That(
            () => PcCompatManagedPathBridge.FileDelete(oldFile),
            Throws.InvalidOperationException,
            "a reloaded MOD must not reach the previous generation's roots");
    }

    [Test]
    public void RelativeTraversalCannotEscapeTheRoots()
    {
        Bind(ModId, Generation);
        using var scope = EnterEnable(ModId, Generation);

        Assert.That(
            () => PcCompatManagedPathBridge.ResolveWritablePath(
                Path.Combine("..", "..", "escaped.txt")),
            Throws.InvalidOperationException);
    }

    [Test]
    public void DirectoryNameOfVirtualRootIsClampedToThatRoot()
    {
        var roots = Bind(ModId, Generation);
        using var scope = EnterEnable(ModId, Generation);

        Assert.Multiple(() =>
        {
            Assert.That(
                PcCompatManagedPathBridge.GetDirectoryName(roots.InstallRoot),
                Is.EqualTo(roots.InstallRoot));
            Assert.That(
                PcCompatManagedPathBridge.GetDirectoryName(
                    roots.InstallRoot + Path.DirectorySeparatorChar),
                Is.EqualTo(roots.InstallRoot));
            Assert.That(
                PcCompatManagedPathBridge.GetDirectoryName(roots.ConfigRoot),
                Is.EqualTo(roots.ConfigRoot));
            Assert.That(
                PcCompatManagedPathBridge.GetDirectoryName(roots.InstallRoot),
                Is.Not.EqualTo(Directory.GetParent(roots.InstallRoot)!.FullName),
                "a virtual MOD root must not expose the shared mods directory as its parent");
        });
    }

    [Test]
    public void DirectoryNameInsideVirtualRootKeepsStandardPathSemantics()
    {
        var roots = Bind(ModId, Generation);
        using var scope = EnterEnable(ModId, Generation);
        var configPath = Path.Combine(roots.InstallRoot, "config", "settings.json");

        Assert.Multiple(() =>
        {
            Assert.That(
                PcCompatManagedPathBridge.GetDirectoryName(configPath),
                Is.EqualTo(Path.Combine(roots.InstallRoot, "config")));
            Assert.That(
                PcCompatManagedPathBridge.GetDirectoryName(Path.Combine("config", "settings.json")),
                Is.EqualTo("config"));
        });
    }

    [Test]
    public void DirectoryNameCannotInspectAnotherModsVirtualRoot()
    {
        Bind(ModId, Generation);
        var victimRoots = Bind(OtherModId, Generation);
        using var scope = EnterEnable(ModId, Generation);

        Assert.That(
            () => PcCompatManagedPathBridge.GetDirectoryName(victimRoots.InstallRoot),
            Throws.InvalidOperationException.With.Message.Contains(OtherModId));
    }

    [Test]
    public void SiblingRootPrefixIsNotTreatedAsContained()
    {
        var roots = Bind(ModId, Generation);
        var sibling = roots.ConfigRoot + "-evil";
        using var scope = EnterEnable(ModId, Generation);

        Assert.That(
            () => PcCompatManagedPathBridge.ResolveWritablePath(Path.Combine(sibling, "x.txt")),
            Throws.InvalidOperationException);
    }

    [Test]
    public void DisablingSessionLosesFilesystemAccess()
    {
        Bind(ModId, Generation);
        using (EnterEnable(ModId, Generation))
            Assert.That(() => PcCompatManagedPathBridge.GetFullPath("a.json"), Throws.Nothing);

        using var disabling = PcCompatManagedExecutionContext.Enter(
            new PcCompatManagedExecutionState(
                ModId,
                Generation,
                PcCompatManagedExecutionPhase.Disable));
        Assert.That(
            () => PcCompatManagedPathBridge.GetFullPath("a.json"),
            Throws.InvalidOperationException.With.Message.Contains("disabling"));
    }

    [Test]
    public void ClearingRootsRevokesAccess()
    {
        Bind(ModId, Generation);
        using (EnterEnable(ModId, Generation))
            Assert.That(() => PcCompatManagedPathBridge.GetFullPath("a.json"), Throws.Nothing);

        PcCompatManagedPathBridge.ClearRoots(ModId, Generation);

        using var scope = EnterEnable(ModId, Generation);
        Assert.That(
            () => PcCompatManagedPathBridge.GetFullPath("a.json"),
            Throws.InvalidOperationException.With.Message.Contains("not bound"));
    }

    [Test]
    public void OverlayShadowPathStaysUsableThroughTheBridgeWithoutNestedMapping()
    {
        var roots = Bind(ModId, Generation);
        var legacy = Path.Combine(roots.InstallRoot, "Settings.json");
        File.WriteAllText(legacy, "legacy");
        using var scope = EnterEnable(ModId, Generation);
        PcCompatManagedPathBridge.FileWriteAllText(legacy, "saved");

        // GetFullPath hands the MOD its shadow path; feeding that path back into the bridge
        // must resolve to the same file, not be mapped into the overlay a second time.
        var shadow = PcCompatManagedPathBridge.GetFullPath(legacy);
        Assert.Multiple(() =>
        {
            Assert.That(shadow, Is.EqualTo(Path.Combine(roots.DataOverlayRoot, "Settings.json")));
            Assert.That(PcCompatManagedPathBridge.GetFullPath(shadow), Is.EqualTo(shadow),
                "resolving an overlay path must be idempotent");
            Assert.That(PcCompatManagedPathBridge.FileReadAllText(shadow), Is.EqualTo("saved"));

            PcCompatManagedPathBridge.FileWriteAllText(shadow, "rewritten");
            Assert.That(PcCompatManagedPathBridge.FileReadAllText(legacy), Is.EqualTo("rewritten"));
            Assert.That(
                Directory.Exists(Path.Combine(roots.DataOverlayRoot, ".pccompat-data")),
                Is.False,
                "a second mapping would have created a nested data root inside the overlay");
        });
    }

    [Test]
    public void InstallDirectoryEnumerationMergesPackageAndOverlayWithOverlayPrecedence()
    {
        var roots = Bind(ModId, Generation);
        var packageDirectory = Path.Combine(roots.InstallRoot, "CustomFont");
        var overlayDirectory = Path.Combine(roots.DataOverlayRoot, "CustomFont");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(overlayDirectory);
        var packageOnly = Path.Combine(packageDirectory, "package.ttf");
        var packageShadowed = Path.Combine(packageDirectory, "shadowed.ttf");
        var overlayOnly = Path.Combine(overlayDirectory, "overlay.ttf");
        var overlayShadow = Path.Combine(overlayDirectory, "shadowed.ttf");
        File.WriteAllText(packageOnly, "package");
        File.WriteAllText(packageShadowed, "old");
        File.WriteAllText(overlayOnly, "overlay");
        File.WriteAllText(overlayShadow, "new");

        using var scope = EnterEnable(ModId, Generation);
        var files = PcCompatManagedPathBridge.DirectoryGetFilesSearch(
            packageDirectory,
            "*.ttf",
            SearchOption.TopDirectoryOnly);

        Assert.Multiple(() =>
        {
            Assert.That(files, Has.Length.EqualTo(3));
            Assert.That(files, Does.Contain(packageOnly));
            Assert.That(files, Does.Contain(overlayOnly));
            Assert.That(files, Does.Contain(overlayShadow));
            Assert.That(files, Does.Not.Contain(packageShadowed));
        });
    }

    [Test]
    public void InstallDirectoryCreatedInOverlayCanBeEnumeratedThroughOriginalPackagePath()
    {
        var roots = Bind(ModId, Generation);
        var packageDirectory = Path.Combine(roots.InstallRoot, "OptionalDirectory");
        using var scope = EnterEnable(ModId, Generation);

        Assert.That(PcCompatManagedPathBridge.DirectoryExists(packageDirectory), Is.False);
        PcCompatManagedPathBridge.DirectoryCreate(packageDirectory);

        var files = PcCompatManagedPathBridge.DirectoryGetFilesPattern(
            packageDirectory,
            "*.json");

        Assert.Multiple(() =>
        {
            Assert.That(files, Is.Empty);
            Assert.That(Directory.Exists(packageDirectory), Is.False,
                "the immutable package layer must not be modified");
            Assert.That(
                Directory.Exists(Path.Combine(roots.DataOverlayRoot, "OptionalDirectory")),
                Is.True);
        });
    }

    [Test]
    public void MissingLogicalDirectoryPreservesDirectoryNotFoundFailure()
    {
        var roots = Bind(ModId, Generation);
        var missing = Path.Combine(roots.InstallRoot, "MissingDirectory");
        using var scope = EnterEnable(ModId, Generation);

        Assert.That(
            () => PcCompatManagedPathBridge.DirectoryGetFiles(missing),
            Throws.TypeOf<DirectoryNotFoundException>());
    }

    [Test]
    public void EnumerateFilesMaterializesTheOwnerScopedSnapshotBeforeReturning()
    {
        var roots = Bind(ModId, Generation);
        var path = Path.Combine(roots.ConfigRoot, "state.json");
        File.WriteAllText(path, "state");

        IEnumerable<string> snapshot;
        using (EnterEnable(ModId, Generation))
            snapshot = PcCompatManagedPathBridge.DirectoryEnumerateFilesPattern(".", "*.json");

        Assert.That(snapshot, Is.EqualTo(new[] { path }));
    }

    [Test]
    public void RelativeMoveSourceIsAnchoredInTheConfigRootNotTheProcessDirectory()
    {
        var roots = Bind(ModId, Generation);
        using var scope = EnterEnable(ModId, Generation);
        PcCompatManagedPathBridge.FileWriteAllText("rotate.dat", "v1");

        // A relative source must resolve against this MOD's config root; resolving it against
        // the shared process CWD is exactly what the isolation contract forbids.
        PcCompatManagedPathBridge.FileMove("rotate.dat", "rotate.dat.bak");

        Assert.Multiple(() =>
        {
            Assert.That(PcCompatManagedPathBridge.FileReadAllText("rotate.dat.bak"), Is.EqualTo("v1"));
            Assert.That(File.Exists(Path.Combine(roots.ConfigRoot, "rotate.dat.bak")), Is.True);
            Assert.That(File.Exists(Path.Combine(Environment.CurrentDirectory, "rotate.dat.bak")),
                Is.False,
                "nothing may be written next to the process working directory");
        });
    }

    [Test]
    public void SymlinkPlantedInsideOwnRootCannotReachAnotherModsFiles()
    {
        var roots = Bind(ModId, Generation);
        var victimRoots = Bind(OtherModId, Generation);
        var victimFile = Path.Combine(victimRoots.ConfigRoot, "victim.json");
        File.WriteAllText(victimFile, "victim-secret");

        // A MOD may create links inside its own writable root with plain BCL calls and no
        // elevation. Lexical containment would then accept escape/victim.json because the
        // prefix is legitimate — §4.10 requires this traversal to fail closed.
        var link = Path.Combine(roots.ConfigRoot, "escape");
        try
        {
            Directory.CreateSymbolicLink(link, victimRoots.ConfigRoot);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Ignore("this host does not permit creating directory links: " + exception.Message);
            return;
        }

        using var scope = EnterEnable(ModId, Generation);
        var escaped = Path.Combine(link, "victim.json");

        Assert.Multiple(() =>
        {
            Assert.That(
                () => PcCompatManagedPathBridge.FileReadAllText(escaped),
                Throws.InvalidOperationException.With.Message.Contains("symlink"),
                "reading through a planted link must fail closed");
            Assert.That(
                () => PcCompatManagedPathBridge.FileWriteAllText(escaped, "tampered"),
                Throws.InvalidOperationException.With.Message.Contains("symlink"),
                "writing through a planted link must fail closed");
            Assert.That(File.ReadAllText(victimFile), Is.EqualTo("victim-secret"),
                "the other MOD's file must be untouched");
            // The link itself is still inside the MOD's own root; ordinary paths beside it
            // keep working, so the check must not over-reject.
            Assert.That(
                () => PcCompatManagedPathBridge.FileWriteAllText("ordinary.json", "ok"),
                Throws.Nothing);
        });
    }

    private PcCompatModPathRoots Bind(
        string modId,
        long generation,
        IReadOnlyList<string>? sharedReadOnlyRoots = null)
    {
        var modRoot = Path.Combine(_root, modId + "-" + generation);
        var roots = new PcCompatModPathRoots
        {
            InstallRoot = Path.Combine(modRoot, "install"),
            ConfigRoot = Path.Combine(modRoot, "config"),
            CacheRoot = Path.Combine(modRoot, "cache"),
            LogRoot = Path.Combine(modRoot, "log"),
            TempRoot = Path.Combine(modRoot, "temp"),
            DataOverlayRoot = Path.Combine(modRoot, "data-overlay"),
            SharedReadOnlyRoots = sharedReadOnlyRoots ?? []
        };
        foreach (var root in new[]
                 {
                     roots.InstallRoot, roots.ConfigRoot, roots.CacheRoot,
                     roots.LogRoot, roots.TempRoot, roots.DataOverlayRoot
                 })
        {
            Directory.CreateDirectory(root);
        }
        PcCompatManagedPathBridge.BindRoots(modId, generation, roots);
        return roots;
    }

    private static IDisposable EnterEnable(string modId, long generation) =>
        PcCompatManagedExecutionContext.Enter(
            new PcCompatManagedExecutionState(
                modId,
                generation,
                PcCompatManagedExecutionPhase.Enable));
}

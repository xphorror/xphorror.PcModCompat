using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;
using StArray.ModManager.Android;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class NativeModPathBridgeTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        ModOwnedResourceRegistry.ClearForTests();
        _root = Path.Combine(Path.GetTempPath(), "starray-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        ModOwnedResourceRegistry.ClearForTests();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void FilesystemAccessWithoutDomainScopeIsRejected()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                () => NativeModPathBridge.GetFullPath("settings.json"),
                Throws.InvalidOperationException);
            Assert.That(
                () => NativeModPathBridge.FileExists("settings.json"),
                Throws.InvalidOperationException);
            Assert.That(
                () => NativeModPathBridge.DirectoryCreate("cache"),
                Throws.InvalidOperationException);
        });
    }

    [Test]
    public void RelativePathResolvesUnderDomainConfigRootNotProcessWorkingDirectory()
    {
        var mod = CreateBoundRuntime("relative-mod");
        using var scope = EnterScope(mod);

        var resolved = NativeModPathBridge.GetFullPath("settings.json");

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.EqualTo(Path.Combine(mod.Roots.ConfigRoot, "settings.json")));
            Assert.That(
                resolved,
                Does.Not.StartWith(Path.GetFullPath(Environment.CurrentDirectory)),
                "relative MOD paths must not resolve against the shared working directory");
        });
    }

    [Test]
    public void WritesAreAllowedInsideEveryWritableRoot()
    {
        var mod = CreateBoundRuntime("writable-mod");
        using var scope = EnterScope(mod);

        Assert.Multiple(() =>
        {
            foreach (var root in new[]
                     {
                         mod.Roots.ConfigRoot,
                         mod.Roots.CacheRoot,
                         mod.Roots.LogRoot,
                         mod.Roots.TempRoot
                     })
            {
                var target = Path.Combine(root, "probe.bin");
                Assert.That(
                    () => NativeModPathBridge.ResolveWritablePath(target),
                    Throws.Nothing,
                    $"root should be writable: {root}");
            }
        });
    }

    [Test]
    public void BindingCreatesDeclaredWritableRootsBeforeTheFirstFileWrite()
    {
        var mod = CreateBoundRuntime(
            "binding-roots-mod",
            initializeWritableRoots: false);

        Assert.That(
            mod.Roots.WritableRoots.All(Directory.Exists),
            Is.True,
            "domain binding must materialize every declared owner writable root");
    }

    [Test]
    public void InstallRootDataWritesShadowIntoOverlayButExecutablesStayRejected()
    {
        var mod = CreateBoundRuntime("install-mod");
        var legacy = Path.Combine(mod.Roots.InstallRoot, "counters.dat");
        File.WriteAllText(legacy, "legacy");
        var ownDll = Path.Combine(mod.Roots.InstallRoot, "Mod.dll");
        File.WriteAllText(ownDll, "original");
        using var scope = EnterScope(mod);

        Assert.Multiple(() =>
        {
            // Package layer stays readable in place.
            Assert.That(NativeModPathBridge.FileExists(legacy), Is.True);

            // Data writes land in the overlay and shadow the legacy file for reads.
            NativeModPathBridge.FileWriteAllTextEncoding(legacy, "saved", System.Text.Encoding.UTF8);
            Assert.That(File.ReadAllText(legacy), Is.EqualTo("legacy"), "package layer untouched");
            Assert.That(
                NativeModPathBridge.FileReadAllBytes(legacy),
                Is.EqualTo(System.Text.Encoding.UTF8.GetPreamble()
                    .Concat(System.Text.Encoding.UTF8.GetBytes("saved")).ToArray()));

            // Deleting removes only the overlay copy; the package original resurfaces.
            NativeModPathBridge.FileDelete(legacy);
            Assert.That(NativeModPathBridge.FileReadAllBytes(legacy),
                Is.EqualTo(System.Text.Encoding.UTF8.GetBytes("legacy")));

            // Executables are Host-owned package contents reached through the capturing file
            // APIs: a write is recorded as a pending self-update instead of touching the
            // package layer (activation happens before the next scan, not now).
            NativeModPathBridge.FileDelete(ownDll);
            Assert.That(File.ReadAllText(ownDll), Is.EqualTo("original"),
                "captured delete must not touch the package layer");
        });
    }

    [Test]
    public void InstallRootRotationRoundTripsThroughTheOverlay()
    {
        var mod = CreateBoundRuntime("rotation-mod");
        using var scope = EnterScope(mod);
        var dat = Path.Combine(mod.Roots.InstallRoot, "state.dat");
        var bak = dat + ".bak";

        NativeModPathBridge.FileWriteAllTextEncoding(dat, "v1", System.Text.Encoding.UTF8);
        NativeModPathBridge.FileDelete(bak);
        NativeModPathBridge.FileMove(dat, bak);
        NativeModPathBridge.FileWriteAllTextEncoding(dat, "v2", System.Text.Encoding.UTF8);

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(Path.Combine(mod.Roots.DataOverlayRoot, "state.dat")), Is.EqualTo("v2"));
            Assert.That(File.ReadAllText(Path.Combine(mod.Roots.DataOverlayRoot, "state.dat.bak")), Is.EqualTo("v1"));
            Assert.That(Directory.GetFiles(mod.Roots.InstallRoot), Is.Empty, "package layer must stay immutable");
        });
    }

    [Test]
    public void MoveOfPackageOnlySourceEmulatesIntoTheOverlay()
    {
        var mod = CreateBoundRuntime("package-move-mod");
        var dat = Path.Combine(mod.Roots.InstallRoot, "state.dat");
        File.WriteAllText(dat, "pre-isolation");
        using var scope = EnterScope(mod);
        var bak = dat + ".bak";

        NativeModPathBridge.FileMove(dat, bak);

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(Path.Combine(mod.Roots.DataOverlayRoot, "state.dat.bak")),
                Is.EqualTo("pre-isolation"));
            // The immutable package original remains readable (documented shadowing
            // divergence from raw Move).
            Assert.That(File.ReadAllText(dat), Is.EqualTo("pre-isolation"));
        });
    }

    [Test]
    public void DirectoryEnumerationMergesPackageAndOverlayLayersWithoutDuplicates()
    {
        var mod = CreateBoundRuntime("enum-mod");
        var install = mod.Roots.InstallRoot;

        // Package layer: a legacy file plus a nested legacy file.
        File.WriteAllText(Path.Combine(install, "pack-only.txt"), "p");
        Directory.CreateDirectory(Path.Combine(install, "nested"));
        File.WriteAllText(Path.Combine(install, "nested", "deep.txt"), "d");
        // Overlay layer: shadows one package name, adds a new name and a new subtree.
        var overlay = mod.Roots.DataOverlayRoot;
        File.WriteAllText(Path.Combine(overlay, "shadowed.txt"), "shadow");

        using var scope = EnterScope(mod);
        NativeModPathBridge.FileWriteAllTextEncoding(
            Path.Combine(install, "shadowed.txt"), "shadow", System.Text.Encoding.UTF8);
        NativeModPathBridge.FileWriteAllTextEncoding(
            Path.Combine(install, "overlay-only.txt"), "o", System.Text.Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(install, "fresh-sub"));
        NativeModPathBridge.FileWriteAllTextEncoding(
            Path.Combine(install, "fresh-sub", "new.bin"), "n", System.Text.Encoding.UTF8);

        var flat = NativeModPathBridge
            .DirectoryEnumerateFilesSearch(install, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.That(flat, Is.EqualTo(new[]
        {
            "overlay-only.txt", "pack-only.txt", "shadowed.txt"
        }));

        var recursiveNames = NativeModPathBridge
            .DirectoryEnumerateFilesSearch(install, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.That(recursiveNames, Is.EqualTo(new[]
        {
            "deep.txt", "new.bin", "overlay-only.txt", "pack-only.txt", "shadowed.txt"
        }));

        // The shadowed entry is reported through its overlay path so reopening observes the
        // shadowed content.
        var reported = NativeModPathBridge
            .DirectoryEnumerateFilesSearch(install, "shadowed.txt", SearchOption.TopDirectoryOnly)
            .Single();
        Assert.That(File.ReadAllText(reported), Is.EqualTo("shadow"));
    }

    [Test]
    public void CommonOnlineModFileAndDirectoryOperationsStayWithinDomain()
    {
        var mod = CreateBoundRuntime("online-mod-files");
        using var scope = EnterScope(mod);

        var tempPath = NativeModPathBridge.GetTempPath();
        Assert.That(tempPath, Is.EqualTo(
            Path.GetFullPath(mod.Roots.TempRoot) + Path.DirectorySeparatorChar));

        var payload = Path.Combine(tempPath, "payload.bin");
        NativeModPathBridge.FileWriteAllBytes(payload, [1, 2, 3]);
        using (NativeModPathBridge.FileCreate(Path.Combine(tempPath, "created.bin")))
        {
        }

        var source = Path.Combine(tempPath, "source");
        var destination = Path.Combine(tempPath, "destination");
        NativeModPathBridge.DirectoryCreate(source);
        NativeModPathBridge.FileWriteAllBytes(Path.Combine(source, "nested.bin"), [4]);
        NativeModPathBridge.DirectoryMove(source, destination);

        var files = NativeModPathBridge.DirectoryGetFilesSearch(
                tempPath,
                "*",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var directories = NativeModPathBridge.DirectoryEnumerateDirectoriesSearch(
                tempPath,
                "*",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .ToArray();
        var entries = NativeModPathBridge.DirectoryEnumerateFileSystemEntries(tempPath)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(NativeModPathBridge.FileReadAllBytes(payload), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(File.Exists(Path.Combine(destination, "nested.bin")), Is.True);
            Assert.That(files, Is.EqualTo(new[] { "created.bin", "payload.bin" }));
            Assert.That(directories, Is.EqualTo(new[] { "destination" }));
            Assert.That(entries, Is.SupersetOf(new[] { "created.bin", "payload.bin", "destination" }));
        });
    }

    [Test]
    public void FileStreamOptionsAreResolvedWithoutDroppingOptions()
    {
        var mod = CreateBoundRuntime("file-stream-options");
        using var scope = EnterScope(mod);

        var installPath = Path.Combine(mod.Roots.InstallRoot, "options.bin");
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous
        };

        using (var stream = NativeModPathBridge.FileOpenOptions(installPath, options))
            stream.WriteByte(0x7a);

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(Path.Combine(
                mod.Roots.DataOverlayRoot,
                "options.bin")), Is.EqualTo(new byte[] { 0x7a }));
            Assert.That(File.Exists(installPath), Is.False,
                "FileStreamOptions writes must use the domain overlay for install paths");
        });
    }

    [Test]
    public void SharedGameResourcesAreReadableButNotWritable()
    {
        var shared = Path.Combine(_root, "game-assets");
        Directory.CreateDirectory(shared);
        var asset = Path.Combine(shared, "official.bundle");
        File.WriteAllText(asset, "x");

        var mod = CreateBoundRuntime("shared-mod", sharedReadOnlyRoots: [shared]);
        using var scope = EnterScope(mod);

        Assert.Multiple(() =>
        {
            Assert.That(NativeModPathBridge.FileExists(asset), Is.True);
            Assert.That(
                () => NativeModPathBridge.FileDelete(asset),
                Throws.InvalidOperationException.With.Message.Contains("read-only"));
        });
    }

    [Test]
    public void HostGrantedSharedRootSupportsFileAndDirectoryMutation()
    {
        var shared = Path.Combine(_root, "shared-storage");
        Directory.CreateDirectory(shared);
        var mod = CreateBoundRuntime("shared-write-mod", sharedWritableRoots: [shared]);
        using var scope = EnterScope(mod);

        var levelDirectory = Path.Combine(shared, "publisher", "levels", "downloaded");
        var levelFile = Path.Combine(levelDirectory, "main.adofai");
        NativeModPathBridge.DirectoryCreate(levelDirectory);
        NativeModPathBridge.FileWriteAllText(levelFile, "level-data");

        Assert.Multiple(() =>
        {
            Assert.That(NativeModPathBridge.FileReadAllText(levelFile), Is.EqualTo("level-data"));
            Assert.That(NativeModPathBridge.DirectoryExists(levelDirectory), Is.True);
        });

        NativeModPathBridge.FileDelete(levelFile);
        NativeModPathBridge.DirectoryDelete(levelDirectory);
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(levelFile), Is.False);
            Assert.That(Directory.Exists(levelDirectory), Is.False);
        });
    }

    [Test]
    public void HostProtectedRootStaysDeniedInsideSharedWritableStorage()
    {
        var shared = Path.Combine(_root, "shared-storage");
        var protectedRoot = Path.Combine(shared, "host-runtime");
        Directory.CreateDirectory(protectedRoot);
        var hostFile = Path.Combine(protectedRoot, "StArray.ModManager.dll");
        File.WriteAllText(hostFile, "host");
        var mod = CreateBoundRuntime(
            "protected-shared-mod",
            sharedWritableRoots: [shared],
            hostProtectedRoots: [protectedRoot]);
        using var scope = EnterScope(mod);

        Assert.Multiple(() =>
        {
            Assert.That(
                () => NativeModPathBridge.FileReadAllText(hostFile),
                Throws.InvalidOperationException.With.Message.Contains("Host-protected"));
            Assert.That(
                () => NativeModPathBridge.FileWriteAllText(hostFile, "changed"),
                Throws.InvalidOperationException.With.Message.Contains("Host-protected"));
            Assert.That(File.ReadAllText(hostFile), Is.EqualTo("host"));
        });
    }

    [Test]
    public void ModLoaderCopiesHostPolicyWithoutInferringBusinessDirectories()
    {
        var managerRoot = Path.Combine(_root, "manager");
        var modsRoot = Path.Combine(managerRoot, "packages");
        var sharedWritable = Path.Combine(_root, "platform-shared");
        var sharedReadOnly = Path.Combine(_root, "platform-system");
        var policy = new ModHostPathPolicy
        {
            SharedWritableRoots = [sharedWritable],
            SharedReadOnlyRoots = [sharedReadOnly],
            HostProtectedRoots = [managerRoot]
        };
        var loader = new ModLoader(modsRoot, policy);

        var roots = loader.BuildDomainPathRoots(
            "arbitrary-mod",
            Path.Combine(modsRoot, "arbitrary-mod"));

        Assert.Multiple(() =>
        {
            Assert.That(roots.SharedWritableRoots, Is.EqualTo(new[] { sharedWritable }));
            Assert.That(roots.SharedReadOnlyRoots, Is.EqualTo(new[] { sharedReadOnly }));
            Assert.That(roots.HostProtectedRoots, Is.EqualTo(new[] { managerRoot }));
        });
    }

    [Test]
    public void AndroidHostPolicyUsesOnlyPlatformAndActualHostPaths()
    {
        var externalStorage = Path.Combine(_root, "external-volume");
        var internalFiles = Path.Combine(_root, "app-files");
        var systemRoot = Path.Combine(_root, "system-root");
        var managerRoot = Path.Combine(externalStorage, "manager-instance");
        var modsRoot = Path.Combine(managerRoot, "packages");
        var configRoot = Path.Combine(internalFiles, "manager-config");
        var runtimeRoot = Path.Combine(internalFiles, "manager-runtime");

        var policy = Managed.CreateNativeModHostPathPolicy(
            externalStorage,
            internalFiles,
            systemRoot,
            modsRoot,
            configRoot,
            runtimeRoot);

        Assert.Multiple(() =>
        {
            Assert.That(policy.SharedWritableRoots,
                Is.EquivalentTo(new[] { externalStorage, internalFiles }));
            Assert.That(policy.SharedReadOnlyRoots, Is.EqualTo(new[] { systemRoot }));
            Assert.That(policy.HostProtectedRoots,
                Is.EquivalentTo(new[] { managerRoot, configRoot, runtimeRoot }));
            Assert.That(
                policy.SharedWritableRoots.Concat(policy.SharedReadOnlyRoots)
                    .Any(path => path.Contains("ADOFAI", StringComparison.OrdinalIgnoreCase)),
                Is.False);
        });
    }

    [Test]
    public void AnotherModsRootIsRejectedAndNamesTheOwner()
    {
        var victim = CreateBoundRuntime("victim-mod");
        var attacker = CreateBoundRuntime("attacker-mod");
        var victimFile = Path.Combine(victim.Roots.ConfigRoot, "victim.json");
        File.WriteAllText(victimFile, "x");

        using var scope = EnterScope(attacker);

        Assert.Multiple(() =>
        {
            Assert.That(
                () => NativeModPathBridge.FileDelete(victimFile),
                Throws.InvalidOperationException.With.Message.Contains(victim.Key.OwnerId));
            Assert.That(
                () => NativeModPathBridge.FileExists(victimFile),
                Throws.InvalidOperationException.With.Message.Contains(victim.Key.OwnerId));
            Assert.That(File.Exists(victimFile), Is.True, "denied access must not touch the file");
        });
    }

    [Test]
    public void PathOutsideEveryRootIsRejected()
    {
        var mod = CreateBoundRuntime("outside-mod");
        var outside = Path.Combine(_root, "unowned", "file.txt");
        using var scope = EnterScope(mod);

        Assert.Multiple(() =>
        {
            Assert.That(
                () => NativeModPathBridge.ResolvePath(outside),
                Throws.InvalidOperationException.With.Message.Contains("outside every root"));
            Assert.That(
                () => NativeModPathBridge.ResolveWritablePath(outside),
                Throws.InvalidOperationException.With.Message.Contains("outside every root"));
        });
    }

    [Test]
    public void RelativeTraversalCannotEscapeTheDomainRoots()
    {
        var mod = CreateBoundRuntime("traversal-mod");
        using var scope = EnterScope(mod);

        Assert.That(
            () => NativeModPathBridge.ResolveWritablePath(
                Path.Combine("..", "..", "escaped.txt")),
            Throws.InvalidOperationException);
    }

    [Test]
    public void SiblingRootPrefixIsNotTreatedAsContained()
    {
        // "…/config-evil" shares a string prefix with "…/config" but is a different directory.
        var mod = CreateBoundRuntime("prefix-mod");
        var sibling = mod.Roots.ConfigRoot + "-evil";
        using var scope = EnterScope(mod);

        Assert.That(
            () => NativeModPathBridge.ResolveWritablePath(Path.Combine(sibling, "x.txt")),
            Throws.InvalidOperationException);
    }

    [Test]
    public void RetiredGenerationLosesFilesystemAccess()
    {
        var mod = CreateBoundRuntime("retired-mod");
        var scope = EnterScope(mod);
        Assert.That(
            () => NativeModPathBridge.GetFullPath("settings.json"),
            Throws.Nothing);
        scope.Dispose();

        Assert.That(mod.Session.TryBeginRetirement(mod.Key), Is.True);
        Assert.That(mod.Session.TryCompleteRetirement(mod.Key), Is.True);

        // Retirement closes the data domain, so a stale generation cannot even re-enter its
        // owner scope; there is no window in which it could reach the filesystem again.
        Assert.That(
            () => HookHelper.EnterOwnerScope(mod.Key.OwnerId, mod.Session, mod.Key),
            Throws.InvalidOperationException);
        Assert.That(
            () => NativeModPathBridge.GetFullPath("settings.json"),
            Throws.InvalidOperationException);
    }

    [Test]
    public void EnumeratedOverlayShadowPathStaysUsableThroughTheBridge()
    {
        var mod = CreateBoundRuntime("enum-usable-mod");
        var install = mod.Roots.InstallRoot;
        File.WriteAllText(Path.Combine(install, "shadowed.txt"), "package");
        using var scope = EnterScope(mod);
        NativeModPathBridge.FileWriteAllTextEncoding(
            Path.Combine(install, "shadowed.txt"), "shadow", System.Text.Encoding.UTF8);

        // Merged enumeration reports a shadowed entry through its overlay path; that path must
        // remain usable through the bridge instead of falling outside every root.
        var reported = NativeModPathBridge
            .DirectoryEnumerateFilesSearch(install, "shadowed.txt", SearchOption.TopDirectoryOnly)
            .Single();

        Assert.Multiple(() =>
        {
            Assert.That(reported, Is.EqualTo(Path.Combine(mod.Roots.DataOverlayRoot, "shadowed.txt")));
            Assert.That(() => NativeModPathBridge.FileExists(reported), Throws.Nothing);
            Assert.That(NativeModPathBridge.FileExists(reported), Is.True);
            Assert.That(NativeModPathBridge.ResolvePath(reported), Is.EqualTo(reported),
                "resolving an overlay path must be idempotent");
        });
    }

    [Test]
    public void SymlinkPlantedInsideOwnRootCannotReachAnotherDomainsFiles()
    {
        var mod = CreateBoundRuntime("link-escape-mod");
        var victim = CreateBoundRuntime("link-victim-mod");
        var victimFile = Path.Combine(victim.Roots.ConfigRoot, "victim.json");
        File.WriteAllText(victimFile, "victim-secret");

        var link = Path.Combine(mod.Roots.ConfigRoot, "escape");
        try
        {
            Directory.CreateSymbolicLink(link, victim.Roots.ConfigRoot);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Ignore("this host does not permit creating directory links: " + exception.Message);
            return;
        }

        using var scope = EnterScope(mod);
        var escaped = Path.Combine(link, "victim.json");

        Assert.Multiple(() =>
        {
            Assert.That(
                () => NativeModPathBridge.FileReadAllBytes(escaped),
                Throws.InvalidOperationException.With.Message.Contains("symlink"));
            Assert.That(
                () => NativeModPathBridge.FileDelete(escaped),
                Throws.InvalidOperationException.With.Message.Contains("symlink"));
            Assert.That(File.ReadAllText(victimFile), Is.EqualTo("victim-secret"));
            // Ordinary paths beside the link keep working: the check must not over-reject.
            Assert.That(
                () => NativeModPathBridge.ResolveWritablePath(
                    Path.Combine(mod.Roots.ConfigRoot, "ordinary.json")),
                Throws.Nothing);
        });
    }

    private BoundRuntime CreateBoundRuntime(
        string id,
        IReadOnlyList<string>? sharedReadOnlyRoots = null,
        IReadOnlyList<string>? sharedWritableRoots = null,
        IReadOnlyList<string>? hostProtectedRoots = null,
        bool initializeWritableRoots = true)
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, id);
        Assert.That(session.TryPublishActive(key), Is.True);
        Assert.That(
            ModDataDomainRegistry.TryResolve(session.DomainToken, out var domain),
            Is.True);

        var modRoot = Path.Combine(_root, id);
        var roots = new ModDataDomainPathRoots
        {
            InstallRoot = Path.Combine(modRoot, "install"),
            ConfigRoot = Path.Combine(modRoot, "config"),
            CacheRoot = Path.Combine(modRoot, "cache"),
            LogRoot = Path.Combine(modRoot, "log"),
            TempRoot = Path.Combine(modRoot, "temp"),
            DataOverlayRoot = Path.Combine(modRoot, "data"),
            SharedReadOnlyRoots = sharedReadOnlyRoots ?? [],
            SharedWritableRoots = sharedWritableRoots ?? [],
            HostProtectedRoots = hostProtectedRoots ?? []
        };
        if (initializeWritableRoots)
        {
            foreach (var root in roots.OwnedRoots.Append(roots.DataOverlayRoot))
                Directory.CreateDirectory(root);
        }
        domain.BindPathRoots(roots);
        Assert.That(domain.TryGetPathRoots(out var bound), Is.True);
        return new BoundRuntime(session, key, bound);
    }

    private static IDisposable EnterScope(BoundRuntime runtime) =>
        HookHelper.EnterOwnerScope(runtime.Key.OwnerId, runtime.Session, runtime.Key);

    private sealed record BoundRuntime(
        ModRuntimeSession Session,
        ModRuntimeKey Key,
        ModDataDomainPathRoots Roots);
}

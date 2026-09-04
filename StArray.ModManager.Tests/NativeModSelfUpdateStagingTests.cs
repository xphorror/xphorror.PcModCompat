using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace StArray.ModManager.Tests;

/// <summary>
/// The audited self-update shape of XPerfect/ShowBPM/Replay: download, stage, then replace
/// their own assemblies with Copy/Move. The overlay is the staging area, so these writes are
/// pending updates that the loader has not adopted yet.
/// </summary>
[NonParallelizable]
public sealed class NativeModSelfUpdateStagingTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        ModOwnedResourceRegistry.ClearForTests();
        _root = Path.Combine(Path.GetTempPath(), "starray-selfupdate-" + Guid.NewGuid().ToString("N"));
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
    public void ReplacingItsOwnAssemblyStagesIntoTheOverlayAndLeavesThePackageRunning()
    {
        var mod = CreateBoundRuntime("selfupdate-copy");
        var download = Path.Combine(mod.Roots.TempRoot, "download");
        Directory.CreateDirectory(download);
        var staged = Path.Combine(download, "XPerfect.dll");
        File.WriteAllText(staged, "new-binary");
        var own = Path.Combine(mod.Roots.InstallRoot, "XPerfect.dll");
        File.WriteAllText(own, "running-binary");
        using var scope = EnterScope(mod);

        NativeModPathBridge.FileCopyOverwrite(staged, own, overwrite: true);

        Assert.Multiple(() =>
        {
            // The loader is still running the package copy; activation is a Host decision.
            Assert.That(File.ReadAllText(own), Is.EqualTo("running-binary"));
            // The MOD's own read sees its new bytes, so updaters that verify their work pass.
            Assert.That(
                File.ReadAllText(NativeModPathBridge.ResolvePath(own)),
                Is.EqualTo("new-binary"));

            var pending = NativeModPathBridge.SnapshotPendingSelfUpdates(mod.Roots);
            Assert.That(pending, Has.Count.EqualTo(1));
            Assert.That(pending[0].RelativePath, Is.EqualTo("XPerfect.dll"));
            Assert.That(pending[0].PackageCopyExists, Is.True);
            Assert.That(File.ReadAllText(pending[0].StagedPath), Is.EqualTo("new-binary"));
        });
    }

    [Test]
    public void StagedUpdateSurvivesTheUpdaterDeletingItsDownloadDirectory()
    {
        var mod = CreateBoundRuntime("selfupdate-cleanup");
        var download = Path.Combine(mod.Roots.TempRoot, "extract");
        Directory.CreateDirectory(download);
        var staged = Path.Combine(download, "Mod.dll");
        File.WriteAllText(staged, "new-binary");
        var own = Path.Combine(mod.Roots.InstallRoot, "Mod.dll");
        File.WriteAllText(own, "running-binary");
        using var scope = EnterScope(mod);

        NativeModPathBridge.FileCopyOverwrite(staged, own, overwrite: true);
        // Real updaters (Jipper's InstallScreen does exactly this) wipe their temp tree once
        // the copy is done. The pending update must not depend on those bytes surviving.
        NativeModPathBridge.DirectoryDeleteRecursive(download, recursive: true);

        var pending = NativeModPathBridge.SnapshotPendingSelfUpdates(mod.Roots);
        Assert.Multiple(() =>
        {
            Assert.That(Directory.Exists(download), Is.False, "the updater's temp tree is gone");
            Assert.That(pending, Has.Count.EqualTo(1));
            Assert.That(
                File.ReadAllText(pending[0].StagedPath),
                Is.EqualTo("new-binary"),
                "the staged bytes live in the Host-owned overlay, not in the MOD's temp tree");
        });
    }

    [Test]
    public void DroppingTheOverlayEntryRollsBackToThePackageCopy()
    {
        var mod = CreateBoundRuntime("selfupdate-rollback");
        var staged = Path.Combine(mod.Roots.TempRoot, "new.dll");
        File.WriteAllText(staged, "new-binary");
        var own = Path.Combine(mod.Roots.InstallRoot, "Mod.dll");
        File.WriteAllText(own, "running-binary");
        using var scope = EnterScope(mod);
        NativeModPathBridge.FileCopyOverwrite(staged, own, overwrite: true);

        // Rollback is free: the package original was never modified.
        NativeModPathBridge.FileDelete(own);

        Assert.Multiple(() =>
        {
            Assert.That(
                File.ReadAllText(NativeModPathBridge.ResolvePath(own)),
                Is.EqualTo("running-binary"));
            Assert.That(
                NativeModPathBridge.SnapshotPendingSelfUpdates(mod.Roots),
                Is.Empty,
                "no pending update remains after rollback");
        });
    }

    [Test]
    public void DataWritesAreNotReportedAsPendingSelfUpdates()
    {
        var mod = CreateBoundRuntime("selfupdate-data");
        var dat = Path.Combine(mod.Roots.InstallRoot, "counters.dat");
        File.WriteAllText(dat, "legacy");
        using var scope = EnterScope(mod);

        NativeModPathBridge.FileWriteAllTextEncoding(dat, "saved", System.Text.Encoding.UTF8);

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(dat), Is.EqualTo("legacy"), "package layer untouched");
            Assert.That(
                File.Exists(Path.Combine(mod.Roots.DataOverlayRoot, "counters.dat")),
                Is.True,
                "data keeps the VFS overlay persistence path");
            Assert.That(
                NativeModPathBridge.SnapshotPendingSelfUpdates(mod.Roots),
                Is.Empty,
                "only assemblies are pending self-updates");
        });
    }

    private BoundRuntime CreateBoundRuntime(string id)
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, id);
        Assert.That(session.TryPublishActive(key), Is.True);
        Assert.That(ModDataDomainRegistry.TryResolve(session.DomainToken, out var domain), Is.True);

        var modRoot = Path.Combine(_root, id);
        var roots = new ModDataDomainPathRoots
        {
            InstallRoot = Path.Combine(modRoot, "install"),
            ConfigRoot = Path.Combine(modRoot, "config"),
            CacheRoot = Path.Combine(modRoot, "cache"),
            LogRoot = Path.Combine(modRoot, "log"),
            TempRoot = Path.Combine(modRoot, "temp"),
            DataOverlayRoot = Path.Combine(modRoot, "data")
        };
        foreach (var root in roots.OwnedRoots.Append(roots.DataOverlayRoot))
            Directory.CreateDirectory(root);
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

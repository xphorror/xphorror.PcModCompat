using StArray.ModManager.Android.PcCompat;
using Xphorror.PcModCompat;
using Xphorror.PcModCompat.Resources;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class PcCompatResourceCompileCacheTests
{
    private string _root = null!;
    private string _modRoot = null!;
    private PcModManifest _manifest = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "pccompat-resource-cache-" + Guid.NewGuid().ToString("N"));
        _modRoot = Path.Combine(_root, "mods", "JipperKeyViewer");
        Directory.CreateDirectory(_modRoot);
        _manifest = new PcModManifest
        {
            Id = "JipperKeyViewer",
            DisplayName = "Jipper Key Viewer",
            FolderPath = _modRoot
        };
        PcCompatAndroidResourceAssemblyCompile.Install();
    }

    [TearDown]
    public void TearDown()
    {
        PcCompatResourceAssemblyCompile.RegisterProvider(null);
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void ReimportRestoresValidatedArtifactsFromStableContentCache()
    {
        File.WriteAllBytes(Path.Combine(_modRoot, "JipperKeyViewer.dll"), [1, 2, 3]);

        var first = PcCompatResourceAssemblyCompile.Prepare(_manifest)!;
        Directory.Delete(Path.Combine(_modRoot, ".pccompat"), recursive: true);
        var second = PcCompatResourceAssemblyCompile.Prepare(_manifest)!;

        Assert.Multiple(() =>
        {
            Assert.That(first.CacheHit, Is.False);
            Assert.That(second.CacheHit, Is.True);
            Assert.That(second.RecipePath, Does.StartWith(Path.Combine(_modRoot, ".pccompat")));
            Assert.That(ResourceRecipeBinary.TryValidate(second.RecipePath, out _), Is.True);
            Assert.That(ResourceIrBinary.TryValidate(second.ResourceIrPath, out _), Is.True);
            Assert.That(
                File.ReadAllText(Path.Combine(
                    Path.GetDirectoryName(second.ResourceIrPath)!,
                    ResourceIrCompiler.CacheMarkerFileName)),
                Is.EqualTo(ResourceCompileInputFingerprint.BuildCompilerMarker(
                    ResourceCompileInputFingerprint.Compute(
                        _manifest.Id,
                        _manifest.FolderPath))));
            Assert.That(
                Directory.GetDirectories(PcCompatResourceCompileCache.GetCacheRoot(_manifest)),
                Has.Length.EqualTo(1));
        });
    }

    [Test]
    public void AssemblyChangeInvalidatesStableResourceCompileEntry()
    {
        var assembly = Path.Combine(_modRoot, "JipperKeyViewer.dll");
        File.WriteAllBytes(assembly, [1, 2, 3]);
        var first = PcCompatResourceAssemblyCompile.Prepare(_manifest)!;

        Directory.Delete(Path.Combine(_modRoot, ".pccompat"), recursive: true);
        File.WriteAllBytes(assembly, [4, 5, 6]);
        var changed = PcCompatResourceAssemblyCompile.Prepare(_manifest)!;

        Assert.Multiple(() =>
        {
            Assert.That(first.CacheHit, Is.False);
            Assert.That(changed.CacheHit, Is.False);
            Assert.That(
                Directory.GetDirectories(PcCompatResourceCompileCache.GetCacheRoot(_manifest)),
                Has.Length.EqualTo(2));
        });
    }

    [Test]
    public void CorruptStableEntryIsRejectedAndRebuilt()
    {
        File.WriteAllBytes(Path.Combine(_modRoot, "JipperKeyViewer.dll"), [1, 2, 3]);
        _ = PcCompatResourceAssemblyCompile.Prepare(_manifest)!;
        var fingerprint = PcCompatResourceCompileCache.ComputeInputFingerprint(
            _manifest,
            CancellationToken.None);
        var stable = PcCompatResourceCompileCache.GetEntry(_manifest, fingerprint);
        File.WriteAllBytes(stable.RecipePath, [0, 1, 2, 3]);
        Directory.Delete(Path.Combine(_modRoot, ".pccompat"), recursive: true);

        var rebuilt = PcCompatResourceAssemblyCompile.Prepare(_manifest)!;

        Assert.Multiple(() =>
        {
            Assert.That(rebuilt.CacheHit, Is.False);
            Assert.That(ResourceRecipeBinary.TryValidate(rebuilt.RecipePath, out _), Is.True);
            Assert.That(ResourceRecipeBinary.TryValidate(stable.RecipePath, out _), Is.True);
        });
    }

    [Test]
    public void CachePruningKeepsOnlyTheThreeNewestContentVersions()
    {
        var assembly = Path.Combine(_modRoot, "JipperKeyViewer.dll");
        for (byte version = 1; version <= 5; version++)
        {
            var local = Path.Combine(_modRoot, ".pccompat");
            if (Directory.Exists(local))
                Directory.Delete(local, recursive: true);
            File.WriteAllBytes(assembly, [version]);
            _ = PcCompatResourceAssemblyCompile.Prepare(_manifest)!;
        }

        Assert.That(
            Directory.GetDirectories(PcCompatResourceCompileCache.GetCacheRoot(_manifest)),
            Has.Length.EqualTo(3));
    }

    [Test]
    [Explicit("Runs the real 23.5 MB JPKV UnityFS fixture to compare cold compile and stable restore.")]
    public void RealJipperKeyViewerBundleIsRestoredWithoutReindexing()
    {
        var source = Path.Combine(FindModManagerRoot(), "JipperKeyViewer-AssetBundle");
        Assume.That(Directory.Exists(source), Is.True, $"missing JPKV fixture: {source}");
        CopyDirectoryContents(source, _modRoot);

        var coldTimer = System.Diagnostics.Stopwatch.StartNew();
        var cold = PcCompatResourceAssemblyCompile.Prepare(_manifest)!;
        coldTimer.Stop();
        Directory.Delete(Path.Combine(_modRoot, ".pccompat"), recursive: true);

        var cachedTimer = System.Diagnostics.Stopwatch.StartNew();
        var cached = PcCompatResourceAssemblyCompile.Prepare(_manifest)!;
        cachedTimer.Stop();

        TestContext.Progress.WriteLine(
            $"JPKV cold={coldTimer.ElapsedMilliseconds}ms cached={cachedTimer.ElapsedMilliseconds}ms");
        Assert.Multiple(() =>
        {
            Assert.That(cold.CacheHit, Is.False);
            Assert.That(cached.CacheHit, Is.True);
            Assert.That(cachedTimer.Elapsed, Is.LessThan(coldTimer.Elapsed));
            Assert.That(ResourceRecipeBinary.TryValidate(cached.RecipePath, out _), Is.True);
            Assert.That(ResourceIrBinary.TryValidate(cached.ResourceIrPath, out _), Is.True);
        });
        Assert.That(ResourceIrBinary.TryRead(cached.ResourceIrPath, out var document, out var error),
            Is.True,
            error);
        var required = document.Assets.Where(asset => asset.RequiredByMod).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(required.Select(asset => asset.Name), Is.EquivalentTo(new[]
            {
                "KeyBackground",
                "KeyOutline",
                "GhostRain",
                "MAPLESTORY_OTF_BOLD",
                "cjkFonts-regular-normalized"
            }));
            Assert.That(required, Has.None.Matches<ResourceIrAsset>(asset =>
                asset.MaterializationKind is ResourceIrMaterializationKind.MetadataOnly or
                    ResourceIrMaterializationKind.Unsupported));
            Assert.That(required.Count(asset =>
                asset.MaterializationKind == ResourceIrMaterializationKind.FontFromFile), Is.EqualTo(2));
        });
    }

    private static void CopyDirectoryContents(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string FindModManagerRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "build_android_single.ps1")) &&
                Directory.Exists(Path.Combine(directory.FullName, "xphorror.PcModCompat")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("StArray.ModManager repository root not found.");
    }
}

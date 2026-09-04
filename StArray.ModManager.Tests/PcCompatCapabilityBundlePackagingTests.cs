using System.Security.Cryptography;
using System.Text.Json;
using Xphorror.PcModCompat.Resources;

namespace StArray.ModManager.Tests;

public sealed class PcCompatCapabilityBundlePackagingTests
{
    private const string BundleName = "pccompat_capabilities_android";

    [Test]
    public void PublishedCapabilityArtifactsAreCompleteAndHashVerified()
    {
        var root = FindModManagerRoot();
        var capabilityRoot = Path.Combine(
            root,
            "xphorror.PcModCompat",
            "assets",
            "pc_compat_capabilities");
        var bundlePath = Path.Combine(capabilityRoot, BundleName);
        var whitelistPath = Path.Combine(capabilityRoot, "pccompat_capability_whitelist.json");
        var manifestPath = Path.Combine(capabilityRoot, BundleName + ".manifest.json");

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(bundlePath), Is.True, bundlePath);
            Assert.That(File.Exists(whitelistPath), Is.True, whitelistPath);
            Assert.That(File.Exists(manifestPath), Is.True, manifestPath);
        });

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        using var whitelist = JsonDocument.Parse(File.ReadAllText(whitelistPath));
        var manifestRoot = manifest.RootElement;
        var whitelistRoot = whitelist.RootElement;
        var graphicsApis = manifestRoot.GetProperty("graphicsApis")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        var assets = whitelistRoot.GetProperty("assets")
            .EnumerateArray()
            .ToArray();
        var assetIds = assets.Select(item => item.GetProperty("id").GetString()).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(manifestRoot.GetProperty("schemaVersion").GetInt32(), Is.EqualTo(1));
            Assert.That(whitelistRoot.GetProperty("schemaVersion").GetInt32(), Is.EqualTo(1));
            Assert.That(manifestRoot.GetProperty("bundleName").GetString(), Is.EqualTo(BundleName));
            Assert.That(whitelistRoot.GetProperty("bundleName").GetString(), Is.EqualTo(BundleName));
            Assert.That(manifestRoot.GetProperty("unityVersion").GetString(), Is.EqualTo("6000.3.10f1"));
            Assert.That(manifestRoot.GetProperty("buildTarget").GetString(), Is.EqualTo("Android"));
            Assert.That(graphicsApis, Does.Contain("Vulkan"));
            Assert.That(graphicsApis, Does.Contain("OpenGLES3"));
            Assert.That(manifestRoot.GetProperty("bundleBytes").GetInt64(),
                Is.EqualTo(new FileInfo(bundlePath).Length));
            Assert.That(manifestRoot.GetProperty("bundleSha256").GetString(),
                Is.EqualTo(ComputeSha256(bundlePath)));
            Assert.That(manifestRoot.GetProperty("whitelistSha256").GetString(),
                Is.EqualTo(ComputeSha256(whitelistPath)));
            Assert.That(manifestRoot.GetProperty("internalManifestSha256").GetString(),
                Does.Match("^[0-9a-f]{64}$"));
            Assert.That(assets, Is.Not.Empty);
            Assert.That(assetIds, Has.All.Not.Null.And.Not.Empty);
            Assert.That(assetIds.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(assetIds.Length));
            Assert.That(assets.Any(item =>
                item.GetProperty("type").GetString() == "UnityEngine.Shader"), Is.True);
            Assert.That(assets.Any(item =>
                item.GetProperty("type").GetString() == "TMPro.TMP_FontAsset"), Is.True);
            Assert.That(assets.Any(item =>
                item.GetProperty("type").GetString() == "UnityEngine.GameObject"), Is.True);
            Assert.That(assetIds, Does.Contain("prefab.compat.progress_bar"));
            var progressBar = assets.Single(item =>
                item.GetProperty("id").GetString() == "prefab.compat.progress_bar");
            Assert.That(progressBar.GetProperty("type").GetString(), Is.EqualTo("UnityEngine.GameObject"));
            Assert.That(progressBar.GetProperty("compatibility").GetString(), Is.EqualTo("compatible"));
        });
    }

    [Test]
    public void BuildChainRequiresCapabilityArtifactsWithoutChangingBuildParameters()
    {
        var root = FindModManagerRoot();
        var androidBuild = File.ReadAllText(Path.Combine(root, "build_android_single.ps1"));
        var installer = File.ReadAllText(Path.Combine(root, "install_android_overlay.ps1"));
        var verifier = File.ReadAllText(Path.Combine(root, "_pccompat_capability_assets.ps1"));
        var topLevelBuild = File.ReadAllText(Path.Combine(root, "build.ps1"));

        Assert.Multiple(() =>
        {
            Assert.That(androidBuild, Does.Contain("_pccompat_capability_assets.ps1"));
            Assert.That(androidBuild, Does.Contain("function Copy-PcCompatCapabilityAssets"));
            Assert.That(androidBuild, Does.Contain("Copy-PcCompatCapabilityAssets $RuntimeOut"));
            Assert.That(verifier, Does.Contain("function Assert-PcCompatCapabilityAssets"));
            Assert.That(verifier, Does.Contain("Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256"));
            Assert.That(installer, Does.Contain("pc_compat_capabilities"));
            Assert.That(installer, Does.Contain("Assert-PcCompatCapabilityAssets $InstalledCapabilityAssets"));
            Assert.That(installer, Does.Contain(BundleName + ".manifest.json"));
            Assert.That(topLevelBuild, Does.Contain("assets\\runtime\\pc_compat_capabilities"));
            Assert.That(topLevelBuild, Does.Contain("Assert-PcCompatCapabilityAssets $capabilityDir"));
            Assert.That(topLevelBuild, Does.Contain(BundleName + ".manifest.json"));
        });
    }

    [Test]
    public void PublishedProgressBarPrefabHasTheRequiredSerializedShape()
    {
        var bundlePath = Path.Combine(
            FindModManagerRoot(),
            "xphorror.PcModCompat",
            "assets",
            "pc_compat_capabilities",
            BundleName);
        var candidate = UnityBundleIndexer.IndexFile(bundlePath);
        Assert.That(candidate.IndexSucceeded, Is.True, string.Join("; ", candidate.Warnings));
        var roots = candidate.Assets.Where(asset =>
            asset.TypeName.Equals("GameObject", StringComparison.OrdinalIgnoreCase) &&
            asset.Name.Equals("pccompat_progress_bar", StringComparison.Ordinal)).ToArray();
        Assert.That(
            roots,
            Has.Length.EqualTo(1),
            "Serialized GameObjects: " + string.Join(", ", candidate.Assets
                .Where(asset => asset.TypeName.Equals("GameObject", StringComparison.OrdinalIgnoreCase))
                .Select(asset => $"'{asset.Name}'#{asset.PathId}")));
        var root = roots[0];
        var shape = ResourceIrUnityExtractor.InspectGameObject(
            bundlePath,
            root.AssetsFileName,
            root.PathId);
        var children = shape.Children.ToDictionary(child => child.Name, StringComparer.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(shape.Name, Is.EqualTo("pccompat_progress_bar"));
            Assert.That(shape.ComponentKinds, Does.Contain("RectTransform"));
            Assert.That(children.Keys, Is.EquivalentTo(new[] { "line", "borderLine", "background" }));
            foreach (var name in new[] { "line", "borderLine", "background" })
            {
                Assert.That(children[name].ComponentKinds, Does.Contain("RectTransform"), name);
                Assert.That(children[name].ComponentKinds, Does.Contain("CanvasRenderer"), name);
                Assert.That(children[name].ComponentKinds, Does.Contain("UnityEngine.UI.Image"), name);
            }
        });
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
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

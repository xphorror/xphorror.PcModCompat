using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatCapabilityPackageTests
{
    [Test]
    public void LoadsPublishedPackageAndIndexesStableIds()
    {
        var package = PcCompatCapabilityPackageLoader.LoadFromDirectory(GetPublishedDirectory());

        Assert.Multiple(() =>
        {
            Assert.That(package.CapabilityVersion, Is.EqualTo("1"));
            Assert.That(package.BundleSha256, Has.Length.EqualTo(64));
            Assert.That(package.Assets, Has.Count.EqualTo(37));
            Assert.That(package.Assets["shader.tmp.mobile.sdf"].ExpectedType,
                Is.EqualTo("UnityEngine.Shader"));
            Assert.That(package.Assets["font.adofai.cjk"].ExpectedType,
                Is.EqualTo("TMPro.TMP_FontAsset"));
            Assert.That(package.Assets["prefab.compat.overlay"].ExpectedType,
                Is.EqualTo("UnityEngine.GameObject"));
            Assert.That(package.Assets["prefab.compat.progress_bar"].ExpectedType,
                Is.EqualTo("UnityEngine.GameObject"));
            Assert.That(package.Assets["pccompat.shader_variants"].ExpectedType,
                Is.EqualTo("UnityEngine.Object"));
            Assert.That(package.Assets.Values, Has.All.Matches<PcCompatCapabilityAssetDescriptor>(
                asset => asset.Required));
        });
    }

    [Test]
    public void RejectsTamperedBundleBeforeUnityLoad()
    {
        using var temporary = CopyPublishedPackage();
        File.AppendAllText(
            Path.Combine(temporary.Path, PcCompatCapabilityPackageLoader.BundleName),
            "tamper",
            Encoding.ASCII);

        Assert.That(
            () => PcCompatCapabilityPackageLoader.LoadFromDirectory(temporary.Path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("length mismatch"));
    }

    [Test]
    public void RejectsPlaceholderPromotedToExactEvenWithUpdatedWhitelistHash()
    {
        using var temporary = CopyPublishedPackage();
        var whitelistPath = Path.Combine(
            temporary.Path,
            PcCompatCapabilityPackageLoader.WhitelistFileName);
        var whitelist = JsonNode.Parse(File.ReadAllText(whitelistPath))!.AsObject();
        var placeholder = whitelist["assets"]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(asset => asset["id"]!.GetValue<string>() == "shader.adofai.overlay");
        placeholder["compatibility"] = "exact";
        WriteJson(whitelistPath, whitelist);

        var externalPath = Path.Combine(
            temporary.Path,
            PcCompatCapabilityPackageLoader.ExternalManifestFileName);
        var external = JsonNode.Parse(File.ReadAllText(externalPath))!.AsObject();
        external["whitelistSha256"] = ComputeSha256(whitelistPath);
        WriteJson(externalPath, external);

        Assert.That(
            () => PcCompatCapabilityPackageLoader.LoadFromDirectory(temporary.Path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("placeholder cannot be exact"));
    }

    [Test]
    public void ValidatesInternalManifestAssetsAndRequiredShaderVariants()
    {
        using var temporary = CopyPublishedPackage();
        var whitelist = JsonNode.Parse(File.ReadAllText(Path.Combine(
            temporary.Path,
            PcCompatCapabilityPackageLoader.WhitelistFileName)))!.AsObject();
        var externalPath = Path.Combine(
            temporary.Path,
            PcCompatCapabilityPackageLoader.ExternalManifestFileName);
        var external = JsonNode.Parse(File.ReadAllText(externalPath))!.AsObject();
        var shaderVariants = new JsonArray();
        foreach (var shader in whitelist["assets"]!.AsArray()
                     .Select(node => node!.AsObject())
                     .Where(asset => asset["type"]!.GetValue<string>() == "UnityEngine.Shader"))
        {
            shaderVariants.Add(new JsonObject
            {
                ["assetId"] = shader["id"]!.GetValue<string>(),
                ["variantCount"] = 1,
            });
        }
        var internalManifest = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["capabilityVersion"] = "1",
            ["bundleName"] = PcCompatCapabilityPackageLoader.BundleName,
            ["unityVersion"] = PcCompatCapabilityPackageLoader.RequiredUnityVersion,
            ["buildTarget"] = "Android",
            ["graphicsApis"] = new JsonArray("Vulkan", "OpenGLES3"),
            ["variantCollectionAddress"] = "pccompat.shader_variants",
            ["assets"] = whitelist["assets"]!.DeepClone(),
            ["shaderVariants"] = shaderVariants,
        };
        var internalJson = SerializeJson(internalManifest);
        external["internalManifestSha256"] = ComputeSha256(Encoding.UTF8.GetBytes(internalJson));
        WriteJson(externalPath, external);

        var package = PcCompatCapabilityPackageLoader.LoadFromDirectory(temporary.Path);
        Assert.That(() => package.ValidateInternalManifest(internalJson), Throws.Nothing);

        shaderVariants.RemoveAt(0);
        var invalidJson = SerializeJson(internalManifest);
        external["internalManifestSha256"] = ComputeSha256(Encoding.UTF8.GetBytes(invalidJson));
        WriteJson(externalPath, external);
        package = PcCompatCapabilityPackageLoader.LoadFromDirectory(temporary.Path);
        Assert.That(
            () => package.ValidateInternalManifest(invalidJson),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("required shader has no retained variant"));
    }

    private static string GetPublishedDirectory()
        => Path.Combine(
            FindModManagerRoot(),
            "xphorror.PcModCompat",
            "assets",
            PcCompatCapabilityPackageLoader.CapabilityDirectoryName);

    private static TemporaryDirectory CopyPublishedPackage()
    {
        var root = FindModManagerRoot();
        var temporary = new TemporaryDirectory(Path.Combine(root, "build"));
        foreach (var source in Directory.EnumerateFiles(GetPublishedDirectory()))
            File.Copy(source, Path.Combine(temporary.Path, Path.GetFileName(source)));
        return temporary;
    }

    private static void WriteJson(string path, JsonNode node)
        => File.WriteAllText(path, SerializeJson(node), new UTF8Encoding(false));

    private static string SerializeJson(JsonNode node)
        => node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string FindModManagerRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "build.ps1")) &&
                Directory.Exists(Path.Combine(directory.FullName, "xphorror.PcModCompat")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("StArray.ModManager repository root not found.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _allowedRoot;

        public TemporaryDirectory(string allowedRoot)
        {
            _allowedRoot = System.IO.Path.GetFullPath(allowedRoot) +
                           System.IO.Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_allowedRoot);
            Path = System.IO.Path.Combine(
                _allowedRoot,
                "capability_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(_allowedRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe test cleanup target: " + resolved);
            if (Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }
}

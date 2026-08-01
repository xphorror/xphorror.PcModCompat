using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xphorror.PcModCompat;

public sealed record PcCompatCapabilityAssetDescriptor(
    string Id,
    string Address,
    string AssetPath,
    string ExpectedType,
    bool Required,
    string Compatibility,
    IReadOnlyList<string> Tags);

public sealed class PcCompatCapabilityPackage
{
    internal PcCompatCapabilityPackage(
        string directoryPath,
        string bundlePath,
        string whitelistPath,
        string externalManifestPath,
        string capabilityVersion,
        string bundleSha256,
        string internalManifestSha256,
        IReadOnlyDictionary<string, PcCompatCapabilityAssetDescriptor> assets)
    {
        DirectoryPath = directoryPath;
        BundlePath = bundlePath;
        WhitelistPath = whitelistPath;
        ExternalManifestPath = externalManifestPath;
        CapabilityVersion = capabilityVersion;
        BundleSha256 = bundleSha256;
        InternalManifestSha256 = internalManifestSha256;
        Assets = assets;
    }

    public string DirectoryPath { get; }
    public string BundlePath { get; }
    public string WhitelistPath { get; }
    public string ExternalManifestPath { get; }
    public string CapabilityVersion { get; }
    public string BundleSha256 { get; }
    public string InternalManifestSha256 { get; }
    public IReadOnlyDictionary<string, PcCompatCapabilityAssetDescriptor> Assets { get; }

    public void ValidateInternalManifest(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("PcCompat capability internal manifest is empty.");
        var actualHash = PcCompatCapabilityPackageLoader.ComputeSha256(Encoding.UTF8.GetBytes(json));
        if (!actualHash.Equals(InternalManifestSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"PcCompat capability internal manifest SHA-256 mismatch: " +
                $"expected={InternalManifestSha256} actual={actualHash}.");
        }

        var manifest = PcCompatCapabilityPackageLoader.Deserialize<InternalManifestDocument>(
            json,
            "internal manifest");
        PcCompatCapabilityPackageLoader.ValidateCommonManifest(
            manifest.SchemaVersion,
            manifest.CapabilityVersion,
            manifest.BundleName,
            manifest.UnityVersion,
            manifest.BuildTarget,
            manifest.GraphicsApis,
            CapabilityVersion);
        if (!string.Equals(
                manifest.VariantCollectionAddress,
                "pccompat.shader_variants",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "PcCompat capability internal manifest has an invalid variant collection address.");
        }

        var internalAssets = PcCompatCapabilityPackageLoader.ValidateAssets(manifest.Assets);
        if (internalAssets.Count != Assets.Count)
        {
            throw new InvalidDataException(
                $"PcCompat capability internal/whitelist asset count differs: " +
                $"internal={internalAssets.Count} whitelist={Assets.Count}.");
        }
        foreach (var pair in Assets)
        {
            if (!internalAssets.TryGetValue(pair.Key, out var internalAsset) ||
                !PcCompatCapabilityPackageLoader.AssetEquals(pair.Value, internalAsset))
            {
                throw new InvalidDataException(
                    "PcCompat capability internal manifest differs for asset " + pair.Key + ".");
            }
        }

        var variants = manifest.ShaderVariants ?? [];
        var variantIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            if (string.IsNullOrWhiteSpace(variant.AssetId) ||
                !variantIds.Add(variant.AssetId) ||
                variant.VariantCount <= 0)
            {
                throw new InvalidDataException(
                    "PcCompat capability internal manifest has an invalid shader variant record.");
            }
            if (!Assets.TryGetValue(variant.AssetId, out var descriptor) ||
                !descriptor.ExpectedType.Equals("UnityEngine.Shader", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "PcCompat capability variant references a non-shader asset: " + variant.AssetId + ".");
            }
        }
        foreach (var shader in Assets.Values.Where(asset =>
                     asset.Required &&
                     asset.ExpectedType.Equals("UnityEngine.Shader", StringComparison.Ordinal)))
        {
            if (!variantIds.Contains(shader.Id))
            {
                throw new InvalidDataException(
                    "PcCompat capability required shader has no retained variant: " + shader.Id + ".");
            }
        }
    }

    private sealed class InternalManifestDocument : CommonManifestDocument
    {
        [JsonPropertyName("variantCollectionAddress")]
        public string? VariantCollectionAddress { get; init; }

        [JsonPropertyName("assets")]
        public List<AssetDocument>? Assets { get; init; }

        [JsonPropertyName("shaderVariants")]
        public List<ShaderVariantDocument>? ShaderVariants { get; init; }
    }

    private sealed class ShaderVariantDocument
    {
        [JsonPropertyName("assetId")]
        public string? AssetId { get; init; }

        [JsonPropertyName("variantCount")]
        public int VariantCount { get; init; }
    }

    internal class CommonManifestDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("capabilityVersion")]
        public string? CapabilityVersion { get; init; }

        [JsonPropertyName("bundleName")]
        public string? BundleName { get; init; }

        [JsonPropertyName("unityVersion")]
        public string? UnityVersion { get; init; }

        [JsonPropertyName("buildTarget")]
        public string? BuildTarget { get; init; }

        [JsonPropertyName("graphicsApis")]
        public List<string>? GraphicsApis { get; init; }
    }

    internal sealed class AssetDocument
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("address")]
        public string? Address { get; init; }

        [JsonPropertyName("path")]
        public string? Path { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("required")]
        public bool Required { get; init; }

        [JsonPropertyName("compatibility")]
        public string? Compatibility { get; init; }

        [JsonPropertyName("tags")]
        public List<string>? Tags { get; init; }
    }
}

public static class PcCompatCapabilityPackageLoader
{
    public const int SchemaVersion = 1;
    public const string BundleName = "pccompat_capabilities_android";
    public const string RequiredUnityVersion = "6000.3.10f1";
    public const string CapabilityDirectoryName = "pc_compat_capabilities";
    public const string WhitelistFileName = "pccompat_capability_whitelist.json";
    public const string ExternalManifestFileName = BundleName + ".manifest.json";
    private const int MaxAssetCount = 512;

    private static readonly HashSet<string> SupportedAssetTypes = new(StringComparer.Ordinal)
    {
        "UnityEngine.Shader",
        "TMPro.TMP_FontAsset",
        "UnityEngine.Material",
        "UnityEngine.Texture2D",
        "UnityEngine.Sprite",
        "UnityEngine.GameObject",
        "UnityEngine.TextAsset",
        "UnityEngine.Object",
    };

    public static PcCompatCapabilityPackage LoadFromRuntimeRoot(string runtimeRoot)
    {
        if (string.IsNullOrWhiteSpace(runtimeRoot))
            throw new ArgumentException("PcCompat runtime root is empty.", nameof(runtimeRoot));
        return LoadFromDirectory(Path.Combine(runtimeRoot, CapabilityDirectoryName));
    }

    public static PcCompatCapabilityPackage LoadFromDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            throw new ArgumentException("PcCompat capability directory is empty.", nameof(directoryPath));
        var directory = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("PcCompat capability directory is missing: " + directory);

        var bundlePath = RequiredFile(directory, BundleName);
        var whitelistPath = RequiredFile(directory, WhitelistFileName);
        var externalManifestPath = RequiredFile(directory, ExternalManifestFileName);
        var manifest = Deserialize<ExternalManifestDocument>(
            File.ReadAllText(externalManifestPath, Encoding.UTF8),
            "external manifest");
        ValidateCommonManifest(
            manifest.SchemaVersion,
            manifest.CapabilityVersion,
            manifest.BundleName,
            manifest.UnityVersion,
            manifest.BuildTarget,
            manifest.GraphicsApis,
            expectedCapabilityVersion: null);

        var bundleHash = RequiredSha(manifest.BundleSha256, "bundleSha256");
        var whitelistHash = RequiredSha(manifest.WhitelistSha256, "whitelistSha256");
        var internalManifestHash = RequiredSha(
            manifest.InternalManifestSha256,
            "internalManifestSha256");
        var bundleLength = new FileInfo(bundlePath).Length;
        if (manifest.BundleBytes <= 0 || bundleLength != manifest.BundleBytes)
        {
            throw new InvalidDataException(
                $"PcCompat capability bundle length mismatch: " +
                $"expected={manifest.BundleBytes} actual={bundleLength}.");
        }
        VerifySha(bundlePath, bundleHash, "bundle");
        VerifySha(whitelistPath, whitelistHash, "whitelist");

        var whitelist = Deserialize<WhitelistDocument>(
            File.ReadAllText(whitelistPath, Encoding.UTF8),
            "whitelist");
        if (whitelist.SchemaVersion != SchemaVersion ||
            !string.Equals(
                whitelist.CapabilityVersion,
                manifest.CapabilityVersion,
                StringComparison.Ordinal) ||
            !string.Equals(whitelist.BundleName, BundleName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "PcCompat capability whitelist identity does not match the external manifest.");
        }
        var assets = ValidateAssets(whitelist.Assets);
        return new PcCompatCapabilityPackage(
            directory,
            bundlePath,
            whitelistPath,
            externalManifestPath,
            manifest.CapabilityVersion!,
            bundleHash,
            internalManifestHash,
            new ReadOnlyDictionary<string, PcCompatCapabilityAssetDescriptor>(assets));
    }

    internal static Dictionary<string, PcCompatCapabilityAssetDescriptor> ValidateAssets(
        IReadOnlyCollection<PcCompatCapabilityPackage.AssetDocument>? documents)
    {
        if (documents == null || documents.Count == 0 || documents.Count > MaxAssetCount)
            throw new InvalidDataException("PcCompat capability asset count is outside the supported range.");
        var result = new Dictionary<string, PcCompatCapabilityAssetDescriptor>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            var id = RequiredIdentity(document.Id, "asset id");
            var address = RequiredIdentity(document.Address, "asset address");
            var assetPath = RequiredIdentity(document.Path, "asset path");
            var expectedType = RequiredIdentity(document.Type, "asset type");
            var compatibility = RequiredIdentity(document.Compatibility, "asset compatibility");
            if (!address.Equals(id, StringComparison.Ordinal))
                throw new InvalidDataException("PcCompat capability address must equal stable id: " + id + ".");
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("PcCompat capability asset path is invalid: " + assetPath + ".");
            }
            if (!SupportedAssetTypes.Contains(expectedType))
                throw new InvalidDataException("PcCompat capability asset type is unsupported: " + expectedType + ".");
            if (compatibility is not ("exact" or "compatible"))
                throw new InvalidDataException("PcCompat capability compatibility is invalid: " + compatibility + ".");

            var tags = (document.Tags ?? [])
                .Select(tag => RequiredIdentity(tag, "asset tag"))
                .ToArray();
            if (tags.Distinct(StringComparer.Ordinal).Count() != tags.Length)
                throw new InvalidDataException("PcCompat capability contains duplicate tags: " + id + ".");
            if (compatibility == "exact" && tags.Contains("asset-ripper-placeholder", StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    "PcCompat capability placeholder cannot be exact: " + id + ".");
            }

            var descriptor = new PcCompatCapabilityAssetDescriptor(
                id,
                address,
                assetPath,
                expectedType,
                document.Required,
                compatibility,
                Array.AsReadOnly(tags));
            if (!result.TryAdd(id, descriptor))
                throw new InvalidDataException("PcCompat capability contains duplicate asset id: " + id + ".");
        }
        return result;
    }

    internal static bool AssetEquals(
        PcCompatCapabilityAssetDescriptor left,
        PcCompatCapabilityAssetDescriptor right)
        => left.Id.Equals(right.Id, StringComparison.Ordinal) &&
           left.Address.Equals(right.Address, StringComparison.Ordinal) &&
           left.AssetPath.Equals(right.AssetPath, StringComparison.Ordinal) &&
           left.ExpectedType.Equals(right.ExpectedType, StringComparison.Ordinal) &&
           left.Required == right.Required &&
           left.Compatibility.Equals(right.Compatibility, StringComparison.Ordinal) &&
           left.Tags.SequenceEqual(right.Tags, StringComparer.Ordinal);

    internal static void ValidateCommonManifest(
        int schemaVersion,
        string? capabilityVersion,
        string? bundleName,
        string? unityVersion,
        string? buildTarget,
        IReadOnlyCollection<string>? graphicsApis,
        string? expectedCapabilityVersion)
    {
        if (schemaVersion != SchemaVersion ||
            string.IsNullOrWhiteSpace(capabilityVersion) ||
            (expectedCapabilityVersion != null &&
             !capabilityVersion.Equals(expectedCapabilityVersion, StringComparison.Ordinal)) ||
            !string.Equals(bundleName, BundleName, StringComparison.Ordinal) ||
            !string.Equals(unityVersion, RequiredUnityVersion, StringComparison.Ordinal) ||
            !string.Equals(buildTarget, "Android", StringComparison.Ordinal))
        {
            throw new InvalidDataException("PcCompat capability manifest identity is invalid.");
        }
        if (graphicsApis == null ||
            !graphicsApis.Contains("Vulkan", StringComparer.Ordinal) ||
            !graphicsApis.Contains("OpenGLES3", StringComparer.Ordinal) ||
            graphicsApis.Distinct(StringComparer.Ordinal).Count() != graphicsApis.Count)
        {
            throw new InvalidDataException(
                "PcCompat capability manifest must contain unique Vulkan and OpenGLES3 entries.");
        }
    }

    internal static T Deserialize<T>(string json, string label)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                   ?? throw new InvalidDataException("PcCompat capability " + label + " is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "PcCompat capability " + label + " JSON is invalid.",
                exception);
        }
    }

    internal static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string RequiredFile(string directory, string fileName)
    {
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path) ||
            new FileInfo(path).Length <= 0)
        {
            throw new FileNotFoundException("PcCompat capability artifact is missing or empty.", path);
        }
        return path;
    }

    private static string RequiredIdentity(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.Equals(value.Trim(), StringComparison.Ordinal) ||
            value.IndexOf('\0') >= 0)
        {
            throw new InvalidDataException("PcCompat capability " + label + " is invalid.");
        }
        return value;
    }

    private static string RequiredSha(string? value, string label)
    {
        if (value == null || value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException("PcCompat capability " + label + " is invalid.");
        }
        return value;
    }

    private static void VerifySha(string path, string expected, string label)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!actual.Equals(expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"PcCompat capability {label} SHA-256 mismatch: expected={expected} actual={actual}.");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private sealed class ExternalManifestDocument : PcCompatCapabilityPackage.CommonManifestDocument
    {
        [JsonPropertyName("bundleBytes")]
        public long BundleBytes { get; init; }

        [JsonPropertyName("bundleSha256")]
        public string? BundleSha256 { get; init; }

        [JsonPropertyName("whitelistSha256")]
        public string? WhitelistSha256 { get; init; }

        [JsonPropertyName("internalManifestSha256")]
        public string? InternalManifestSha256 { get; init; }
    }

    private sealed class WhitelistDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("capabilityVersion")]
        public string? CapabilityVersion { get; init; }

        [JsonPropertyName("bundleName")]
        public string? BundleName { get; init; }

        [JsonPropertyName("assets")]
        public List<PcCompatCapabilityPackage.AssetDocument>? Assets { get; init; }
    }
}

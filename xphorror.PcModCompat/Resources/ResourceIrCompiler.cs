using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Xphorror.PcModCompat.Resources;

public static class ResourceIrCompiler
{
    public const string AliasFileName = "pccompat_resource_aliases.json";
    public const string CacheMarkerFileName = "resource_ir_compiler.txt";
    public const string CompilerRevision = "resource-ir-compiler-v4-alpha8-atlas";
    private static readonly JsonSerializerOptions AliasJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static ResourceIrDocument Build(
        ResourceCompileReport report,
        string modFolder,
        string? payloadOutputDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);

        var aliases = ReadAliases(modFolder);
        var warnings = new List<string>();
        var matchedAliases = new HashSet<ResourceIrAliasEntry>(ReferenceEqualityComparer.Instance);
        var groupCandidates = report.FeatureGroups.ToDictionary(
            group => group.Id,
            group => group.SelectedCandidateSha256Hex,
            StringComparer.OrdinalIgnoreCase);
        var assets = new List<ResourceIrAsset>();
        var bundles = new List<ResourceIrBundle>();
        var selectedCandidateShas = report.FeatureGroups
            .Select(group => group.SelectedCandidateSha256Hex)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in report.Candidates
                     .Where(candidate => candidate.IndexSucceeded)
                     .OrderBy(candidate => candidate.Sha256Hex, StringComparer.Ordinal))
        {
            var bundleId = "vb." + candidate.Sha256Hex[..32].ToLowerInvariant();
            var bundleAssetIds = new List<string>();
            foreach (var source in candidate.Assets
                         .OrderBy(asset => asset.AssetsFileName, StringComparer.Ordinal)
                         .ThenBy(asset => asset.PathId)
                         .ThenBy(asset => asset.Name, StringComparer.Ordinal))
            {
                var relevantBindings = report.Bindings.Where(binding =>
                        binding.AssetName.Equals(source.Name, StringComparison.Ordinal) &&
                        SourceTypeMatchesExpected(source, binding.ExpectedType) &&
                        groupCandidates.TryGetValue(binding.FeatureGroupId, out var selectedSha) &&
                        selectedSha.Equals(candidate.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var expectedTypes = relevantBindings
                    .Select(binding => NormalizeExpectedType(binding.ExpectedType))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (expectedTypes.Length > 1)
                {
                    throw new InvalidDataException(
                        $"Resource IR expected type is ambiguous candidate={candidate.FileName} " +
                        $"asset={source.Name} types={string.Join(',', expectedTypes)}");
                }

                var expectedType = expectedTypes.Length == 1
                    ? expectedTypes[0]
                    : NormalizeExpectedType(source.TypeName);
                var aliasMatches = aliases.Where(alias =>
                        alias.AssetName.Equals(source.Name, StringComparison.Ordinal) &&
                        NormalizeExpectedType(alias.ExpectedType).Equals(expectedType, StringComparison.Ordinal) &&
                        (string.IsNullOrWhiteSpace(alias.CandidateSha256Hex) ||
                         alias.CandidateSha256Hex.Equals(candidate.Sha256Hex, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                if (aliasMatches.Length > 1)
                {
                    throw new InvalidDataException(
                        $"Resource capability alias is ambiguous candidate={candidate.FileName} " +
                        $"asset={source.Name} type={expectedType}");
                }

                var alias = aliasMatches.SingleOrDefault();
                if (alias != null)
                    matchedAliases.Add(alias);
                var compatibility = alias == null
                    ? ResourceIrCompatibility.Unsupported
                    : ParseAliasCompatibility(alias.Compatibility);
                var kind = alias == null
                    ? ResourceIrMaterializationKind.MetadataOnly
                    : ResourceIrMaterializationKind.CapabilityReference;
                var assetId = BuildAssetId(candidate.Sha256Hex, source);
                bundleAssetIds.Add(assetId);
                assets.Add(new ResourceIrAsset
                {
                    Id = assetId,
                    BundleId = bundleId,
                    Name = source.Name,
                    SourceType = source.TypeName,
                    ExpectedType = expectedType,
                    Container = source.Container,
                    AssetsFileName = source.AssetsFileName,
                    PathId = source.PathId,
                    TypeId = source.TypeId,
                    RequiredByMod = relevantBindings.Any(binding =>
                        binding.Confidence == AssetBindConfidence.Proven),
                    MaterializationKind = kind,
                    Compatibility = compatibility,
                    CapabilityStableId = alias?.CapabilityStableId ?? string.Empty,
                    CloneCapabilityAsset = alias?.Clone ?? false,
                    Tags = BuildTags(expectedType, alias)
                });
            }

            bundles.Add(new ResourceIrBundle
            {
                Id = bundleId,
                CandidateSha256Hex = candidate.Sha256Hex.ToLowerInvariant(),
                SourceFileName = candidate.FileName,
                SourceRelativePath = NormalizeRelativePath(modFolder, candidate.SourcePath),
                PlatformHint = candidate.PlatformHint.ToString(),
                UnityVersion = candidate.UnityVersion,
                LoadPolicy = candidate.LoadPolicy.ToString(),
                SelectedForRuntime = selectedCandidateShas.Contains(candidate.Sha256Hex),
                AssetIds = bundleAssetIds
            });
        }

        foreach (var unmatched in aliases.Where(alias => !matchedAliases.Contains(alias)))
        {
            warnings.Add(
                $"Capability alias did not match an indexed asset: name={unmatched.AssetName} " +
                $"type={unmatched.ExpectedType} candidate={unmatched.CandidateSha256Hex}");
        }

        var document = new ResourceIrDocument
        {
            ModId = report.ModId,
            TargetUnityVersion = report.TargetUnityVersion,
            Bundles = bundles,
            Assets = assets,
            Warnings = warnings
        };
        return string.IsNullOrWhiteSpace(payloadOutputDirectory)
            ? document
            : ResourceIrUnityExtractor.Enrich(report, document, payloadOutputDirectory);
    }

    private static IReadOnlyList<ResourceIrAliasEntry> ReadAliases(string modFolder)
    {
        var path = Path.Combine(Path.GetFullPath(modFolder), AliasFileName);
        if (!File.Exists(path))
            return Array.Empty<ResourceIrAliasEntry>();
        var document = JsonSerializer.Deserialize<ResourceIrAliasDocument>(
                           File.ReadAllText(path),
                           AliasJsonOptions)
                       ?? throw new InvalidDataException($"Resource alias document is empty: {path}");
        if (document.SchemaVersion != 1 || document.Assets.Count > 4096)
            throw new InvalidDataException($"Unsupported resource alias schema/count: {path}");

        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in document.Assets)
        {
            if (string.IsNullOrWhiteSpace(alias.AssetName) || alias.AssetName.Length > 4096 ||
                string.IsNullOrWhiteSpace(alias.ExpectedType) || alias.ExpectedType.Length > 1024 ||
                string.IsNullOrWhiteSpace(alias.CapabilityStableId) || alias.CapabilityStableId.Length > 256 ||
                alias.Reason.Length > 4096 ||
                (!string.IsNullOrWhiteSpace(alias.CandidateSha256Hex) && !IsSha256(alias.CandidateSha256Hex)))
            {
                throw new InvalidDataException($"Invalid resource capability alias in {path}");
            }
            _ = ParseAliasCompatibility(alias.Compatibility);
            var identity = alias.CandidateSha256Hex + "\0" + alias.AssetName + "\0" +
                           NormalizeExpectedType(alias.ExpectedType);
            if (!identities.Add(identity))
                throw new InvalidDataException($"Duplicate resource capability alias: {alias.AssetName}");
        }
        return document.Assets;
    }

    private static string BuildAssetId(string candidateSha, ResourceAssetEntry asset)
    {
        var identity = candidateSha.ToLowerInvariant() + "\0" + asset.Container + "\0" +
                       asset.AssetsFileName + "\0" + asset.PathId + "\0" + asset.TypeId + "\0" + asset.Name;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return "res." + Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }

    private static string NormalizeRelativePath(string modFolder, string sourcePath)
    {
        var root = Path.GetFullPath(modFolder);
        var fullPath = Path.GetFullPath(sourcePath);
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative is "." or ".." ||
            relative.StartsWith("../", StringComparison.Ordinal))
            throw new InvalidDataException($"Resource candidate escapes MOD folder: {sourcePath}");
        return relative;
    }

    private static string NormalizeExpectedType(string value)
    {
        var trimmed = value.Trim();
        return trimmed switch
        {
            "Object" => "UnityEngine.Object",
            "GameObject" => "UnityEngine.GameObject",
            "Texture" => "UnityEngine.Texture",
            "Texture2D" => "UnityEngine.Texture2D",
            "Sprite" => "UnityEngine.Sprite",
            "Material" => "UnityEngine.Material",
            "Shader" => "UnityEngine.Shader",
            "Font" => "UnityEngine.Font",
            "TMP_FontAsset" => "TMPro.TMP_FontAsset",
            _ when trimmed.Contains('.') => trimmed,
            _ => "UnityEngine.Object"
        };
    }

    private static bool SourceTypeMatchesExpected(ResourceAssetEntry source, string expectedType)
    {
        var normalized = NormalizeExpectedType(expectedType);
        return normalized switch
        {
            "UnityEngine.GameObject" => source.TypeName.Equals("GameObject", StringComparison.OrdinalIgnoreCase),
            "UnityEngine.Texture2D" => source.TypeName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase),
            "UnityEngine.Sprite" => source.TypeName.Equals("Sprite", StringComparison.OrdinalIgnoreCase),
            "UnityEngine.Material" => source.TypeName.Equals("Material", StringComparison.OrdinalIgnoreCase),
            "UnityEngine.Shader" => source.TypeName.Equals("Shader", StringComparison.OrdinalIgnoreCase),
            "UnityEngine.Font" => source.TypeName.Equals("Font", StringComparison.OrdinalIgnoreCase),
            "TMPro.TMP_FontAsset" =>
                source.TypeName.Equals("TMP_FontAsset", StringComparison.OrdinalIgnoreCase) ||
                source.TypeName.Equals("MonoBehaviour", StringComparison.OrdinalIgnoreCase),
            "UnityEngine.Object" => true,
            _ => source.TypeName.Equals(normalized, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static ResourceIrCompatibility ParseAliasCompatibility(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "exact" => ResourceIrCompatibility.Exact,
            "compatible" => ResourceIrCompatibility.Compatible,
            _ => throw new InvalidDataException($"Unsupported resource alias compatibility: {value}")
        };

    private static IReadOnlyList<string> BuildTags(string expectedType, ResourceIrAliasEntry? alias)
    {
        var tags = new List<string>();
        if (expectedType.Contains("Font", StringComparison.Ordinal))
            tags.Add("font");
        if (expectedType.EndsWith("Sprite", StringComparison.Ordinal))
            tags.Add("sprite");
        if (expectedType.EndsWith("Texture2D", StringComparison.Ordinal))
            tags.Add("texture");
        if (expectedType.EndsWith("Material", StringComparison.Ordinal))
            tags.Add("material");
        if (expectedType.EndsWith("GameObject", StringComparison.Ordinal))
            tags.Add("prefab");
        if (alias != null)
            tags.Add("explicit-capability-alias");
        return tags;
    }

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Xphorror.PcModCompat;

/// <summary>
/// Runtime/import-facing view of resource_recipe.bin. This reader intentionally
/// does not depend on AssetsTools.NET; only the isolated Resources assembly may
/// parse UnityFS containers.
/// </summary>
public static class PcCompatResourceRecipe
{
    public const string FormatVersion = "resource-recipe-v1";
    public const ushort SchemaVersion = 1;
    public const ushort HeaderSize = 64;
    public const int MaxFileSize = 16 * 1024 * 1024;
    public const string IndexedBundleRecipeId = "xphorror.resource.indexed_bundle.v1";
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("XPHRRESC");

    public static bool TryRead(string path, out PcCompatResourceRecipeDocument document, out string? error)
    {
        document = null!;
        error = null;
        try
        {
            var length = new FileInfo(path).Length;
            if (length is < HeaderSize or > MaxFileSize)
            {
                error = "file size is outside limits";
                return false;
            }
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < HeaderSize)
            {
                error = "file shorter than header";
                return false;
            }
            if (!bytes.AsSpan(0, 8).SequenceEqual(Magic))
            {
                error = "bad magic";
                return false;
            }
            var schema = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2));
            if (schema != SchemaVersion)
            {
                error = $"unsupported schema {schema}";
                return false;
            }
            var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10, 2));
            if (headerSize != HeaderSize)
            {
                error = $"unexpected header size {headerSize}";
                return false;
            }
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
            if (flags != 1 || bytes.AsSpan(60, 4).IndexOfAnyExcept((byte)0) >= 0)
            {
                error = "unsupported flags or reserved header data";
                return false;
            }
            var jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4));
            var totalSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(52, 4));
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(56, 4));
            if (totalSize != bytes.Length || HeaderSize + jsonLength != bytes.Length)
            {
                error = "size mismatch";
                return false;
            }
            var json = bytes.AsSpan(HeaderSize);
            var expectedSha = bytes.AsSpan(20, 32);
            var actualSha = SHA256.HashData(json);
            if (!expectedSha.SequenceEqual(actualSha))
            {
                error = "payload sha256 mismatch";
                return false;
            }
            if (crc != Crc32(json))
            {
                error = "payload crc32 mismatch";
                return false;
            }

            using var payload = JsonDocument.Parse(json.ToArray());
            document = ParseDocument(payload.RootElement);
            if (!TryValidateDocument(document, expectedModId: null, out error))
            {
                document = null!;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    public static bool TryValidateDocument(
        PcCompatResourceRecipeDocument document,
        string? expectedModId,
        out string? error)
    {
        error = null;
        if (document == null)
            return FailValidation("document is null", out error);
        if (!ValidText(document.ModId, 256))
            return FailValidation("modId is empty or too long", out error);
        if (!string.IsNullOrWhiteSpace(expectedModId) &&
            !document.ModId.Equals(expectedModId, StringComparison.OrdinalIgnoreCase))
            return FailValidation($"modId mismatch: recipe={document.ModId} manifest={expectedModId}", out error);
        if (!document.RecipeId.Equals(IndexedBundleRecipeId, StringComparison.Ordinal))
            return FailValidation($"unsupported recipeId '{document.RecipeId}'", out error);
        if (!ValidText(document.Compatibility, 64) || !ValidText(document.TargetUnityVersion, 64))
            return FailValidation("compatibility or targetUnityVersion is invalid", out error);
        if (document.Candidates.Count > 256 || document.FeatureGroups.Count > 256 || document.Bindings.Count > 4096)
            return FailValidation("resource recipe collection count exceeds limits", out error);

        var candidates = new Dictionary<string, PcCompatResourceCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in document.Candidates)
        {
            if (!IsSha256(candidate.Sha256Hex) || !candidates.TryAdd(candidate.Sha256Hex, candidate))
                return FailValidation("candidate sha256 is invalid or duplicated", out error);
            if (!IsSafeFileName(candidate.FileName))
                return FailValidation($"candidate fileName is unsafe: '{candidate.FileName}'", out error);
            if (candidate.FileSize < 0 || !ValidText(candidate.PlatformHint, 32) ||
                !ValidText(candidate.UnityVersion, 64) || !ValidText(candidate.VersionGate, 32) ||
                !ValidText(candidate.LoadPolicy, 32) || candidate.SourcePath.Length > 32_768)
                return FailValidation($"candidate metadata is invalid: {candidate.FileName}", out error);
            if (!Allowed(candidate.PlatformHint, "Unknown", "0", "Android", "1", "Linux", "2", "Windows", "3", "Mac", "4") ||
                !Allowed(candidate.VersionGate, "Auto", "0", "Controlled", "1", "ForcedOnly", "2", "Unknown", "3") ||
                !Allowed(candidate.LoadPolicy, "AutoLoad", "0", "ControlledLoad", "1", "ForceRequired", "2", "IndexOnly", "3", "Rejected", "4"))
                return FailValidation($"candidate enum metadata is unsupported: {candidate.FileName}", out error);
            if (!IsCandidatePolicyConsistent(candidate))
                return FailValidation($"candidate policy/version/platform combination is invalid: {candidate.FileName}", out error);
            if (candidate.DirectoryEntries.Count > 16_384 || candidate.Assets.Count > 65_536 || candidate.Warnings.Count > 4096)
                return FailValidation($"candidate nested collection count exceeds limits: {candidate.FileName}", out error);
        }

        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in document.FeatureGroups)
        {
            if (!ValidText(group.Id, 256) || !groups.Add(group.Id) || !ValidText(group.DisplayName, 1024) ||
                group.AssetNames.Count > 16_384 || group.Notes.Count > 4096)
                return FailValidation("feature group identity or collection count is invalid", out error);
            if (!candidates.TryGetValue(group.SelectedCandidateSha256Hex, out var selected))
                return FailValidation($"feature group '{group.Id}' references an unknown candidate", out error);
            if (!EquivalentPolicy(group.LoadPolicy, selected.LoadPolicy) ||
                !EquivalentPlatform(group.SelectedPlatform, selected.PlatformHint))
                return FailValidation($"feature group '{group.Id}' policy/platform disagrees with its candidate", out error);
        }

        var bindingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in document.Bindings)
        {
            if (!groups.Contains(binding.FeatureGroupId) || !ValidText(binding.AssetName, 4096) ||
                !ValidText(binding.ExpectedType, 1024) || !ValidText(binding.Confidence, 32) ||
                binding.SourceFieldIdentity.Length > 2048 || binding.Reason.Length > 16_384)
                return FailValidation("resource binding is incomplete or references an unknown feature group", out error);
            if (binding.SourceFieldIdentity.Length != 0 &&
                string.IsNullOrWhiteSpace(binding.SourceFieldIdentity))
                return FailValidation("resource binding source field identity is invalid", out error);
            if (!Allowed(binding.Confidence, "Unbound", "0", "Proven", "1", "UniqueType", "2", "SemanticMatch", "3", "FuzzyMatch", "4"))
                return FailValidation($"resource binding confidence is unsupported: {binding.Confidence}", out error);
            var group = document.FeatureGroups.First(item =>
                item.Id.Equals(binding.FeatureGroupId, StringComparison.OrdinalIgnoreCase));
            if (!group.AssetNames.Contains(binding.AssetName, StringComparer.Ordinal))
                return FailValidation($"resource binding asset is not part of feature group '{binding.FeatureGroupId}'", out error);
            if (!bindingKeys.Add(binding.FeatureGroupId + "\0" + binding.AssetName + "\0" + binding.ExpectedType))
                return FailValidation("resource binding is duplicated", out error);
        }

        return true;
    }

    public static bool IsSha256(string value)
    {
        if (value.Length != 64)
            return false;
        foreach (var ch in value)
        {
            if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F')))
                return false;
        }
        return true;
    }

    public static bool TryVerifyCandidateFile(
        string path,
        string expectedSha256Hex,
        long expectedFileSize,
        out string? error)
    {
        error = null;
        if (!IsSha256(expectedSha256Hex))
            return FailValidation("expected candidate sha256 is invalid", out error);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return FailValidation("candidate path is missing on disk", out error);
        try
        {
            var info = new FileInfo(path);
            if (expectedFileSize > 0 && info.Length != expectedFileSize)
                return FailValidation($"candidate file size mismatch: expected={expectedFileSize} actual={info.Length}", out error);
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!actual.Equals(expectedSha256Hex, StringComparison.OrdinalIgnoreCase))
                return FailValidation($"candidate sha256 mismatch: expected={expectedSha256Hex} actual={actual}", out error);
            return true;
        }
        catch (Exception ex)
        {
            return FailValidation(ex.GetType().Name + ": " + ex.Message, out error);
        }
    }

    private static bool IsSafeFileName(string value)
        => ValidText(value, 1024) && value is not "." and not ".." &&
           value.IndexOfAny(['/', '\\']) < 0 && Path.GetFileName(value) == value;

    private static bool IsCandidatePolicyConsistent(PcCompatResourceCandidate candidate)
    {
        var policy = NormalizePolicy(candidate.LoadPolicy);
        var gate = NormalizeVersionGate(candidate.VersionGate);
        var platform = NormalizePlatform(candidate.PlatformHint);
        return policy switch
        {
            0 => candidate.IndexSucceeded && gate == 0 && platform == 1 &&
                 candidate.UnityVersion.StartsWith("6000.3.", StringComparison.Ordinal),
            1 => candidate.IndexSucceeded && (gate == 1 || (gate == 0 && platform != 1)),
            2 => candidate.IndexSucceeded && gate == 2,
            3 or 4 => true,
            _ => false
        };
    }

    private static bool EquivalentPolicy(string left, string right)
    {
        return NormalizePolicy(left) >= 0 && NormalizePolicy(left) == NormalizePolicy(right);
    }

    private static bool EquivalentPlatform(string left, string right)
        => NormalizePlatform(left) >= 0 && NormalizePlatform(left) == NormalizePlatform(right);

    private static int NormalizePolicy(string value)
        => value.ToLowerInvariant() switch
        {
            "autoload" or "0" => 0,
            "controlledload" or "1" => 1,
            "forcerequired" or "2" => 2,
            "indexonly" or "3" => 3,
            "rejected" or "4" => 4,
            _ => -1
        };

    private static int NormalizeVersionGate(string value)
        => value.ToLowerInvariant() switch
        {
            "auto" or "0" => 0,
            "controlled" or "1" => 1,
            "forcedonly" or "2" => 2,
            "unknown" or "3" => 3,
            _ => -1
        };

    private static int NormalizePlatform(string value)
        => value.ToLowerInvariant() switch
        {
            "unknown" or "0" => 0,
            "android" or "1" => 1,
            "linux" or "2" => 2,
            "windows" or "3" => 3,
            "mac" or "4" => 4,
            _ => -1
        };

    private static bool Allowed(string value, params string[] values)
        => values.Any(candidate => value.Equals(candidate, StringComparison.OrdinalIgnoreCase));

    private static bool ValidText(string value, int maxLength)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;

    private static bool FailValidation(string message, out string? error)
    {
        error = message;
        return false;
    }

    private static PcCompatResourceRecipeDocument ParseDocument(JsonElement root)
        => new()
        {
            ModId = ReadString(root, "modId"),
            RecipeId = ReadString(root, "recipeId"),
            Compatibility = ReadString(root, "compatibility"),
            TargetUnityVersion = ReadString(root, "targetUnityVersion"),
            Candidates = ReadArray(root, "candidates", ParseCandidate),
            FeatureGroups = ReadArray(root, "featureGroups", ParseFeatureGroup),
            Bindings = ReadArray(root, "bindings", ParseBinding)
        };

    private static PcCompatResourceCandidate ParseCandidate(JsonElement element)
        => new()
        {
            SourcePath = ReadString(element, "sourcePath"),
            FileName = ReadString(element, "fileName"),
            PlatformHint = ReadFlexibleString(element, "platformHint"),
            UnityVersion = ReadString(element, "unityVersion"),
            VersionGate = ReadFlexibleString(element, "versionGate"),
            LoadPolicy = ReadFlexibleString(element, "loadPolicy"),
            FileSize = element.TryGetProperty("fileSize", out var size) && size.TryGetInt64(out var fileSize)
                ? fileSize
                : 0,
            Sha256Hex = ReadString(element, "sha256Hex"),
            HasEmbeddedTypeTree = element.TryGetProperty("hasEmbeddedTypeTree", out var tree) && tree.ValueKind == JsonValueKind.True,
            IndexSucceeded = element.TryGetProperty("indexSucceeded", out var ok) &&
                             ok.ValueKind == JsonValueKind.True,
            IndexError = element.TryGetProperty("indexError", out var indexError) && indexError.ValueKind == JsonValueKind.String
                ? indexError.GetString()
                : null,
            DirectoryEntries = ReadStringArray(element, "directoryEntries"),
            Assets = ReadArray(element, "assets", ParseAsset),
            Warnings = ReadStringArray(element, "warnings")
        };

    private static PcCompatResourceAssetEntry ParseAsset(JsonElement element)
        => new()
        {
            Name = ReadString(element, "name"),
            TypeName = ReadString(element, "typeName"),
            PathId = element.TryGetProperty("pathId", out var pathId) && pathId.TryGetInt64(out var value) ? value : 0,
            TypeId = element.TryGetProperty("typeId", out var typeId) && typeId.TryGetInt32(out var type) ? type : 0,
            Container = ReadString(element, "container"),
            AssetsFileName = ReadString(element, "assetsFileName")
        };

    private static PcCompatResourceFeatureGroup ParseFeatureGroup(JsonElement element)
        => new()
        {
            Id = ReadString(element, "id"),
            DisplayName = ReadString(element, "displayName"),
            SelectedCandidateSha256Hex = ReadString(element, "selectedCandidateSha256Hex"),
            SelectedPlatform = ReadFlexibleString(element, "selectedPlatform"),
            LoadPolicy = ReadFlexibleString(element, "loadPolicy"),
            AssetNames = ReadStringArray(element, "assetNames"),
            Notes = ReadStringArray(element, "notes")
        };

    private static PcCompatResourceBinding ParseBinding(JsonElement element)
        => new()
        {
            FeatureGroupId = ReadString(element, "featureGroupId"),
            AssetName = ReadString(element, "assetName"),
            ExpectedType = ReadString(element, "expectedType"),
            Confidence = ReadFlexibleString(element, "confidence"),
            SourceFieldIdentity = ReadString(element, "sourceFieldIdentity"),
            Reason = ReadString(element, "reason")
        };

    private static string ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string ReadFlexibleString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.ToString()
        };
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.ToString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static IReadOnlyList<T> ReadArray<T>(JsonElement element, string name, Func<JsonElement, T> map)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<T>();
        return value.EnumerateArray().Select(map).ToArray();
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                var mask = 0u - (crc & 1u);
                crc = (crc >> 1) ^ (0xEDB88320u & mask);
            }
        }
        return ~crc;
    }
}

public sealed class PcCompatResourceRecipeDocument
{
    public required string ModId { get; init; }
    public required string RecipeId { get; init; }
    public required string Compatibility { get; init; }
    public required string TargetUnityVersion { get; init; }
    public IReadOnlyList<PcCompatResourceCandidate> Candidates { get; init; } = Array.Empty<PcCompatResourceCandidate>();
    public IReadOnlyList<PcCompatResourceFeatureGroup> FeatureGroups { get; init; } = Array.Empty<PcCompatResourceFeatureGroup>();
    public IReadOnlyList<PcCompatResourceBinding> Bindings { get; init; } = Array.Empty<PcCompatResourceBinding>();
}

public sealed class PcCompatResourceCandidate
{
    public string SourcePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string PlatformHint { get; init; } = string.Empty;
    public string UnityVersion { get; init; } = string.Empty;
    public string VersionGate { get; init; } = string.Empty;
    public string LoadPolicy { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public string Sha256Hex { get; init; } = string.Empty;
    public bool HasEmbeddedTypeTree { get; init; }
    public bool IndexSucceeded { get; init; }
    public string? IndexError { get; init; }
    public IReadOnlyList<string> DirectoryEntries { get; init; } = Array.Empty<string>();
    public IReadOnlyList<PcCompatResourceAssetEntry> Assets { get; init; } = Array.Empty<PcCompatResourceAssetEntry>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class PcCompatResourceAssetEntry
{
    public string Name { get; init; } = string.Empty;
    public string TypeName { get; init; } = string.Empty;
    public long PathId { get; init; }
    public int TypeId { get; init; }
    public string Container { get; init; } = string.Empty;
    public string AssetsFileName { get; init; } = string.Empty;
}

public sealed class PcCompatResourceFeatureGroup
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string SelectedCandidateSha256Hex { get; init; } = string.Empty;
    public string SelectedPlatform { get; init; } = string.Empty;
    public string LoadPolicy { get; init; } = string.Empty;
    public IReadOnlyList<string> AssetNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed class PcCompatResourceBinding
{
    public string FeatureGroupId { get; init; } = string.Empty;
    public string AssetName { get; init; } = string.Empty;
    public string ExpectedType { get; init; } = string.Empty;
    public string Confidence { get; init; } = string.Empty;
    public string SourceFieldIdentity { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

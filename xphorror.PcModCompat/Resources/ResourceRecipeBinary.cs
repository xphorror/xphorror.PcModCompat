using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Xphorror.PcModCompat.Resources;

/// <summary>
/// Compact import-time resource recipe. Native runtime will consume a verified
/// subset later; the first version is intentionally self-describing and auditable.
/// </summary>
public static class ResourceRecipeBinary
{
    public const string FormatVersion = "resource-recipe-v1";
    public const ushort SchemaVersion = 1;
    public const ushort HeaderSize = 64;
    public const string RecipeId = "xphorror.resource.indexed_bundle.v1";
    public const int MaxFileSize = 16 * 1024 * 1024;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("XPHRRESC");
    private static readonly JsonSerializerOptions RecipeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static void Write(string path, ResourceCompileReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(report);

        var document = ResourceCompiler.ToRecipeDocument(report);
        var json = JsonSerializer.SerializeToUtf8Bytes(document, RecipeJsonOptions);

        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], SchemaVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 1); // flags: little-endian json payload
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], checked((uint)json.Length));
        var sha = SHA256.HashData(json);
        sha.CopyTo(header[20..52]);
        // total size and crc filled later
        stream.Write(header);
        stream.Write(json);

        var payload = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(52, 4), checked((uint)payload.Length));
        var crc = Crc32(payload.AsSpan(HeaderSize));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(56, 4), crc);
        File.WriteAllBytes(path, payload);
    }

    public static bool TryValidate(string path, out string? error)
    {
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
            if (totalSize != bytes.Length)
            {
                error = "totalSize mismatch";
                return false;
            }
            if (HeaderSize + jsonLength != bytes.Length)
            {
                error = "json length mismatch";
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
            var document = JsonSerializer.Deserialize<ResourceRecipeDocument>(json, RecipeJsonOptions);
            if (!TryValidateDocument(document, out error))
                return false;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static bool TryValidateDocument(ResourceRecipeDocument? document, out string? error)
    {
        error = null;
        if (document == null || !ValidText(document.ModId, 256))
            return Fail("json missing or invalid modId", out error);
        if (!document.RecipeId.Equals(RecipeId, StringComparison.Ordinal) ||
            !ValidText(document.Compatibility, 64) || !ValidText(document.TargetUnityVersion, 64))
            return Fail("recipe identity or target version is invalid", out error);
        if (document.Candidates == null || document.FeatureGroups == null || document.Bindings == null ||
            document.Candidates.Count > 256 || document.FeatureGroups.Count > 256 || document.Bindings.Count > 4096)
            return Fail("resource recipe collection count is invalid", out error);

        var candidates = new Dictionary<string, ResourceCandidateIndex>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in document.Candidates)
        {
            if (!IsSha256(candidate.Sha256Hex) || !candidates.TryAdd(candidate.Sha256Hex, candidate) ||
                !IsSafeFileName(candidate.FileName) || candidate.FileSize < 0 || candidate.SourcePath.Length > 32_768 ||
                !ValidText(candidate.UnityVersion, 64) || !Enum.IsDefined(candidate.PlatformHint) ||
                !Enum.IsDefined(candidate.VersionGate) || !Enum.IsDefined(candidate.LoadPolicy))
                return Fail("candidate metadata is invalid or duplicated", out error);
            if (!IsCandidatePolicyConsistent(candidate))
                return Fail("candidate policy/version/platform combination is invalid", out error);
            if (candidate.DirectoryEntries.Count > 16_384 || candidate.Assets.Count > 65_536 ||
                candidate.Warnings.Count > 4096)
                return Fail("candidate nested collection count exceeds limits", out error);
        }

        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in document.FeatureGroups)
        {
            if (!ValidText(group.Id, 256) || !groups.Add(group.Id) || !ValidText(group.DisplayName, 1024) ||
                group.AssetNames.Count > 16_384 || group.Notes.Count > 4096 ||
                !candidates.TryGetValue(group.SelectedCandidateSha256Hex, out var selected) ||
                group.LoadPolicy != selected.LoadPolicy || group.SelectedPlatform != selected.PlatformHint)
                return Fail($"feature group '{group.Id}' is invalid or references an unknown candidate", out error);
        }

        var bindings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in document.Bindings)
        {
            if (!groups.Contains(binding.FeatureGroupId) || !ValidText(binding.AssetName, 4096) ||
                !ValidText(binding.ExpectedType, 1024) || !Enum.IsDefined(binding.Confidence) ||
                binding.SourceFieldIdentity.Length > 2048 || binding.Reason.Length > 16_384 ||
                !bindings.Add(binding.FeatureGroupId + "\0" + binding.AssetName + "\0" + binding.ExpectedType))
                return Fail("resource binding is invalid or duplicated", out error);
            if (binding.SourceFieldIdentity.Length != 0 &&
                string.IsNullOrWhiteSpace(binding.SourceFieldIdentity))
                return Fail("resource binding source field identity is invalid", out error);
            var group = document.FeatureGroups.First(item =>
                item.Id.Equals(binding.FeatureGroupId, StringComparison.OrdinalIgnoreCase));
            if (!group.AssetNames.Contains(binding.AssetName, StringComparer.Ordinal))
                return Fail($"resource binding asset is not part of feature group '{binding.FeatureGroupId}'", out error);
        }

        return true;
    }

    private static bool IsSha256(string value)
    {
        if (value.Length != 64)
            return false;
        return value.All(ch =>
            (ch >= '0' && ch <= '9') ||
            (ch >= 'a' && ch <= 'f') ||
            (ch >= 'A' && ch <= 'F'));
    }

    private static bool IsSafeFileName(string value)
        => ValidText(value, 1024) && value is not "." and not ".." &&
           value.IndexOfAny(['/', '\\']) < 0 && Path.GetFileName(value) == value;

    private static bool IsCandidatePolicyConsistent(ResourceCandidateIndex candidate)
        => candidate.LoadPolicy switch
        {
            BundleLoadPolicy.AutoLoad => candidate.IndexSucceeded &&
                candidate.VersionGate == UnityVersionGate.Auto &&
                candidate.PlatformHint == BundlePlatformHint.Android &&
                candidate.UnityVersion.StartsWith("6000.3.", StringComparison.Ordinal),
            BundleLoadPolicy.ControlledLoad => candidate.IndexSucceeded &&
                (candidate.VersionGate == UnityVersionGate.Controlled ||
                 (candidate.VersionGate == UnityVersionGate.Auto &&
                  candidate.PlatformHint != BundlePlatformHint.Android)),
            BundleLoadPolicy.ForceRequired => candidate.IndexSucceeded &&
                candidate.VersionGate == UnityVersionGate.ForcedOnly,
            BundleLoadPolicy.IndexOnly or BundleLoadPolicy.Rejected => true,
            _ => false
        };

    private static bool ValidText(string value, int maxLength)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
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

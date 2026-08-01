using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xphorror.PcModCompat;

public enum PcCompatResourceIrMaterializationKind
{
    MetadataOnly = 0,
    CapabilityReference = 1,
    TextureRgba32 = 2,
    SpriteFromTexture = 3,
    MaterialFromCapabilityShader = 4,
    TmpFontFromAtlas = 5,
    PrefabGraph = 6,
    TextureAlpha8 = 7,
    Unsupported = 255
}

public enum PcCompatResourceIrCompatibility
{
    Exact = 0,
    Compatible = 1,
    Unsupported = 2
}

public sealed class PcCompatResourceIrBundle
{
    public required string Id { get; init; }
    public required string CandidateSha256Hex { get; init; }
    public required string SourceFileName { get; init; }
    public required string SourceRelativePath { get; init; }
    public required string PlatformHint { get; init; }
    public required string UnityVersion { get; init; }
    public required string LoadPolicy { get; init; }
    public bool SelectedForRuntime { get; init; }
    public IReadOnlyList<string> AssetIds { get; init; } = Array.Empty<string>();
}

public sealed class PcCompatResourceIrAsset
{
    public required string Id { get; init; }
    public required string BundleId { get; init; }
    public required string Name { get; init; }
    public required string SourceType { get; init; }
    public required string ExpectedType { get; init; }
    public required string Container { get; init; }
    public required string AssetsFileName { get; init; }
    public long PathId { get; init; }
    public int TypeId { get; init; }
    public bool RequiredByMod { get; init; }
    public required PcCompatResourceIrMaterializationKind MaterializationKind { get; init; }
    public required PcCompatResourceIrCompatibility Compatibility { get; init; }
    public string CapabilityStableId { get; init; } = string.Empty;
    public bool CloneCapabilityAsset { get; init; }
    public string PayloadId { get; init; } = string.Empty;
    public IReadOnlyList<string> DependencyIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public PcCompatResourceIrTextureInfo? Texture { get; init; }
    public PcCompatResourceIrSpriteInfo? Sprite { get; init; }
    public PcCompatResourceIrMaterialInfo? Material { get; init; }
    public PcCompatResourceIrTmpFontInfo? TmpFont { get; init; }
    public PcCompatResourceIrPrefabInfo? Prefab { get; init; }
}

public sealed class PcCompatResourceIrPayload
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string RelativePath { get; init; }
    public required string Sha256Hex { get; init; }
    public long Length { get; init; }
    public string Compression { get; init; } = "none";
}

public sealed class PcCompatResourceIrTextureInfo
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int SourceFormat { get; init; }
    public int MipCount { get; init; }
    public int FilterMode { get; init; }
    public int WrapU { get; init; }
    public int WrapV { get; init; }
    public bool Linear { get; init; }
}

public sealed class PcCompatResourceIrSpriteInfo
{
    public required string TextureAssetId { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
    public float PivotX { get; init; }
    public float PivotY { get; init; }
    public float PixelsPerUnit { get; init; }
    public float BorderLeft { get; init; }
    public float BorderBottom { get; init; }
    public float BorderRight { get; init; }
    public float BorderTop { get; init; }
    public uint Extrude { get; init; }
}

public sealed class PcCompatResourceIrMaterialInfo
{
    public int CustomRenderQueue { get; init; } = -1;
    public int GlobalIlluminationFlags { get; init; }
    public bool EnableInstancing { get; init; }
    public bool DoubleSidedGi { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
    public IReadOnlyList<PcCompatResourceIrMaterialInt> Ints { get; init; } = Array.Empty<PcCompatResourceIrMaterialInt>();
    public IReadOnlyList<PcCompatResourceIrMaterialFloat> Floats { get; init; } = Array.Empty<PcCompatResourceIrMaterialFloat>();
    public IReadOnlyList<PcCompatResourceIrMaterialColor> Colors { get; init; } = Array.Empty<PcCompatResourceIrMaterialColor>();
    public IReadOnlyList<PcCompatResourceIrMaterialTexture> Textures { get; init; } = Array.Empty<PcCompatResourceIrMaterialTexture>();
}

public sealed record PcCompatResourceIrMaterialInt(string PropertyName, int Value);

public sealed record PcCompatResourceIrMaterialFloat(string PropertyName, float Value);

public sealed record PcCompatResourceIrMaterialColor(
    string PropertyName,
    float R,
    float G,
    float B,
    float A);

public sealed record PcCompatResourceIrMaterialTexture(
    string PropertyName,
    string TextureAssetId,
    float OffsetX,
    float OffsetY,
    float ScaleX,
    float ScaleY);

public sealed class PcCompatResourceIrTmpFontInfo
{
    public required PcCompatResourceIrTmpFontFaceInfo Face { get; init; }
    public required string MaterialAssetId { get; init; }
    public IReadOnlyList<string> AtlasTextureAssetIds { get; init; } = Array.Empty<string>();
    public int AtlasTextureIndex { get; init; }
    public int AtlasPopulationMode { get; init; }
    public bool MultiAtlasTexturesEnabled { get; init; }
    public int AtlasWidth { get; init; }
    public int AtlasHeight { get; init; }
    public int AtlasPadding { get; init; }
    public int AtlasRenderMode { get; init; }
    public float NormalStyle { get; init; }
    public float NormalSpacingOffset { get; init; }
    public float BoldStyle { get; init; }
    public float BoldSpacing { get; init; }
    public int ItalicStyle { get; init; }
    public int TabSize { get; init; }
    public int GlyphCount { get; init; }
    public int CharacterCount { get; init; }
}

public sealed class PcCompatResourceIrTmpFontFaceInfo
{
    public int FaceIndex { get; init; }
    public required string FamilyName { get; init; }
    public required string StyleName { get; init; }
    public float PointSize { get; init; }
    public float Scale { get; init; }
    public int UnitsPerEm { get; init; }
    public float LineHeight { get; init; }
    public float AscentLine { get; init; }
    public float CapLine { get; init; }
    public float MeanLine { get; init; }
    public float Baseline { get; init; }
    public float DescentLine { get; init; }
    public float SuperscriptOffset { get; init; }
    public float SuperscriptSize { get; init; }
    public float SubscriptOffset { get; init; }
    public float SubscriptSize { get; init; }
    public float UnderlineOffset { get; init; }
    public float UnderlineThickness { get; init; }
    public float StrikethroughOffset { get; init; }
    public float StrikethroughThickness { get; init; }
    public float TabWidth { get; init; }
}

public sealed class PcCompatResourceIrPrefabInfo
{
    public IReadOnlyList<PcCompatResourceIrPrefabNode> Nodes { get; init; } = Array.Empty<PcCompatResourceIrPrefabNode>();
}

public sealed class PcCompatResourceIrPrefabNode
{
    public required string Name { get; init; }
    public int ParentIndex { get; init; } = -1;
    public int Layer { get; init; }
    public bool Active { get; init; } = true;
    public required PcCompatResourceIrPrefabTransform Transform { get; init; }
    public PcCompatResourceIrPrefabCanvasRenderer? CanvasRenderer { get; init; }
    public PcCompatResourceIrPrefabImage? Image { get; init; }
    public PcCompatResourceIrPrefabRawImage? RawImage { get; init; }
}

public sealed class PcCompatResourceIrPrefabTransform
{
    public bool IsRectTransform { get; init; }
    public float LocalPositionX { get; init; }
    public float LocalPositionY { get; init; }
    public float LocalPositionZ { get; init; }
    public float LocalRotationX { get; init; }
    public float LocalRotationY { get; init; }
    public float LocalRotationZ { get; init; }
    public float LocalRotationW { get; init; } = 1f;
    public float LocalScaleX { get; init; } = 1f;
    public float LocalScaleY { get; init; } = 1f;
    public float LocalScaleZ { get; init; } = 1f;
    public float AnchorMinX { get; init; }
    public float AnchorMinY { get; init; }
    public float AnchorMaxX { get; init; } = 1f;
    public float AnchorMaxY { get; init; } = 1f;
    public float AnchoredPositionX { get; init; }
    public float AnchoredPositionY { get; init; }
    public float SizeDeltaX { get; init; }
    public float SizeDeltaY { get; init; }
    public float PivotX { get; init; } = 0.5f;
    public float PivotY { get; init; } = 0.5f;
}

public sealed class PcCompatResourceIrPrefabCanvasRenderer
{
    public bool CullTransparentMesh { get; init; } = true;
}

public sealed class PcCompatResourceIrPrefabGraphic
{
    public float ColorR { get; init; } = 1f;
    public float ColorG { get; init; } = 1f;
    public float ColorB { get; init; } = 1f;
    public float ColorA { get; init; } = 1f;
    public bool RaycastTarget { get; init; } = true;
    public bool Maskable { get; init; } = true;
    public string MaterialAssetId { get; init; } = string.Empty;
}

public sealed class PcCompatResourceIrPrefabImage
{
    public required PcCompatResourceIrPrefabGraphic Graphic { get; init; }
    public string SpriteAssetId { get; init; } = string.Empty;
    public int Type { get; init; }
    public bool PreserveAspect { get; init; }
    public bool FillCenter { get; init; } = true;
    public int FillMethod { get; init; } = 4;
    public float FillAmount { get; init; } = 1f;
    public bool FillClockwise { get; init; } = true;
    public int FillOrigin { get; init; }
    public bool UseSpriteMesh { get; init; }
    public float PixelsPerUnitMultiplier { get; init; } = 1f;
}

public sealed class PcCompatResourceIrPrefabRawImage
{
    public required PcCompatResourceIrPrefabGraphic Graphic { get; init; }
    public string TextureAssetId { get; init; } = string.Empty;
    public float UvX { get; init; }
    public float UvY { get; init; }
    public float UvWidth { get; init; } = 1f;
    public float UvHeight { get; init; } = 1f;
}

public sealed class PcCompatResourceIrDocument
{
    public string FormatVersion { get; init; } = PcCompatResourceIr.FormatVersion;
    public required string ModId { get; init; }
    public required string TargetUnityVersion { get; init; }
    public IReadOnlyList<PcCompatResourceIrBundle> Bundles { get; init; } = Array.Empty<PcCompatResourceIrBundle>();
    public IReadOnlyList<PcCompatResourceIrAsset> Assets { get; init; } = Array.Empty<PcCompatResourceIrAsset>();
    public IReadOnlyList<PcCompatResourceIrPayload> Payloads { get; init; } = Array.Empty<PcCompatResourceIrPayload>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public static class PcCompatResourceIr
{
    public const string FormatVersion = "resource-ir-v1";
    public const ushort SchemaVersion = 1;
    public const ushort HeaderSize = 64;
    public const int MaxFileSize = 32 * 1024 * 1024;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("XPHRIR01");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static bool TryRead(
        string path,
        string? expectedModId,
        out PcCompatResourceIrDocument document,
        out string? error)
    {
        document = null!;
        error = null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length is < HeaderSize or > MaxFileSize ||
                !bytes.AsSpan(0, 8).SequenceEqual(Magic))
                return Fail("resource IR header or file size is invalid", out error);
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2)) != SchemaVersion ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10, 2)) != HeaderSize ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4)) != 1 ||
                bytes.AsSpan(60, 4).IndexOfAnyExcept((byte)0) >= 0)
                return Fail("resource IR schema, flags or reserved bytes are invalid", out error);
            var jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4));
            var totalSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(52, 4));
            if (totalSize != bytes.Length || HeaderSize + jsonLength != bytes.Length)
                return Fail("resource IR length mismatch", out error);
            var json = bytes.AsSpan(HeaderSize);
            if (!bytes.AsSpan(20, 32).SequenceEqual(SHA256.HashData(json)) ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(56, 4)) != Crc32(json))
                return Fail("resource IR hash or crc mismatch", out error);

            document = JsonSerializer.Deserialize<PcCompatResourceIrDocument>(json, JsonOptions)!;
            if (!TryValidateDocument(document, expectedModId, out error))
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
        PcCompatResourceIrDocument? document,
        string? expectedModId,
        out string? error)
    {
        error = null;
        if (document == null || document.FormatVersion != FormatVersion ||
            !ValidText(document.ModId, 256) || !ValidText(document.TargetUnityVersion, 64))
            return Fail("resource IR identity is invalid", out error);
        if (!string.IsNullOrWhiteSpace(expectedModId) &&
            !document.ModId.Equals(expectedModId, StringComparison.OrdinalIgnoreCase))
            return Fail($"resource IR modId mismatch: ir={document.ModId} manifest={expectedModId}", out error);
        if (document.Bundles.Count > 256 || document.Assets.Count > 65_536 ||
            document.Payloads.Count > 65_536 || document.Warnings.Count > 4096)
            return Fail("resource IR collection count exceeds limits", out error);

        var bundleIds = new HashSet<string>(StringComparer.Ordinal);
        var bundleShas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in document.Bundles)
        {
            if (!ValidId(bundle.Id, 128) || !bundleIds.Add(bundle.Id) ||
                !PcCompatResourceRecipe.IsSha256(bundle.CandidateSha256Hex) ||
                !bundleShas.Add(bundle.CandidateSha256Hex) || !SafeFileName(bundle.SourceFileName) ||
                !SafeRelativePath(bundle.SourceRelativePath) ||
                !ValidText(bundle.PlatformHint, 32) || !ValidText(bundle.UnityVersion, 64) ||
                !ValidText(bundle.LoadPolicy, 32) || bundle.AssetIds.Count > 65_536 ||
                bundle.AssetIds.Distinct(StringComparer.Ordinal).Count() != bundle.AssetIds.Count)
                return Fail("resource IR bundle metadata is invalid", out error);
        }

        var payloadIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var payload in document.Payloads)
        {
            if (!ValidId(payload.Id, 128) || !payloadIds.Add(payload.Id) ||
                !ValidText(payload.Kind, 64) || !SafeRelativePath(payload.RelativePath) ||
                !PcCompatResourceRecipe.IsSha256(payload.Sha256Hex) || payload.Length < 0 ||
                !ValidText(payload.Compression, 32))
                return Fail("resource IR payload metadata is invalid", out error);
        }

        var assetIds = new HashSet<string>(StringComparer.Ordinal);
        var assetsByBundle = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var asset in document.Assets)
        {
            if (!ValidId(asset.Id, 128) || !assetIds.Add(asset.Id) || !bundleIds.Contains(asset.BundleId) ||
                asset.Name.Length > 4096 || !ValidText(asset.SourceType, 1024) ||
                !ValidText(asset.ExpectedType, 1024) || asset.Container.Length > 4096 ||
                asset.AssetsFileName.Length > 4096 || !Enum.IsDefined(asset.MaterializationKind) ||
                !Enum.IsDefined(asset.Compatibility) || asset.DependencyIds.Count > 4096 ||
                asset.Tags.Count > 256 || !ValidateMaterialization(asset, payloadIds, out error))
                return error != null ? false : Fail("resource IR asset metadata is invalid", out error);
            if (!assetsByBundle.TryGetValue(asset.BundleId, out var members))
                assetsByBundle[asset.BundleId] = members = new HashSet<string>(StringComparer.Ordinal);
            members.Add(asset.Id);
        }
        foreach (var asset in document.Assets)
        foreach (var dependency in asset.DependencyIds)
        {
            if (!assetIds.Contains(dependency) || dependency == asset.Id)
                return Fail($"resource IR asset dependency is invalid: {asset.Id}", out error);
        }
        foreach (var bundle in document.Bundles)
        {
            assetsByBundle.TryGetValue(bundle.Id, out var actual);
            actual ??= new HashSet<string>(StringComparer.Ordinal);
            if (!actual.SetEquals(bundle.AssetIds))
                return Fail($"resource IR bundle asset membership mismatch: {bundle.Id}", out error);
        }
        return true;
    }

    public static bool TryVerifyPayloadFiles(
        string resourceIrPath,
        PcCompatResourceIrDocument document,
        out string? error)
    {
        error = null;
        try
        {
            var root = Path.GetDirectoryName(Path.GetFullPath(resourceIrPath))
                       ?? throw new InvalidDataException("Resource IR path has no parent directory.");
            foreach (var payload in document.Payloads)
            {
                var path = Path.GetFullPath(Path.Combine(root, payload.RelativePath));
                var relative = Path.GetRelativePath(root, path);
                if (Path.IsPathRooted(relative) || relative is "." or ".." ||
                    relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                    return Fail($"resource IR payload escapes root: {payload.Id}", out error);
                if (!File.Exists(path) || new FileInfo(path).Length != payload.Length)
                    return Fail($"resource IR payload is missing or has wrong length: {payload.Id}", out error);
                using var stream = File.OpenRead(path);
                var sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!sha.Equals(payload.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                    return Fail($"resource IR payload sha256 mismatch: {payload.Id}", out error);
            }
            return true;
        }
        catch (Exception ex)
        {
            return Fail(ex.GetType().Name + ": " + ex.Message, out error);
        }
    }

    public static bool TryValidateAgainstRecipe(
        PcCompatResourceIrDocument document,
        PcCompatResourceRecipeDocument recipe,
        out string? error)
    {
        error = null;
        if (!document.ModId.Equals(recipe.ModId, StringComparison.OrdinalIgnoreCase) ||
            !document.TargetUnityVersion.Equals(recipe.TargetUnityVersion, StringComparison.Ordinal))
            return Fail("resource IR identity disagrees with resource recipe", out error);

        var recipeCandidates = recipe.Candidates.ToDictionary(
            candidate => candidate.Sha256Hex,
            StringComparer.OrdinalIgnoreCase);
        var bundlesById = document.Bundles.ToDictionary(bundle => bundle.Id, StringComparer.Ordinal);
        foreach (var bundle in document.Bundles)
        {
            if (!recipeCandidates.TryGetValue(bundle.CandidateSha256Hex, out var candidate) ||
                !bundle.SourceFileName.Equals(candidate.FileName, StringComparison.Ordinal) ||
                !EquivalentEnum(bundle.PlatformHint, candidate.PlatformHint) ||
                !EquivalentEnum(bundle.LoadPolicy, candidate.LoadPolicy))
                return Fail($"resource IR bundle disagrees with recipe candidate: {bundle.Id}", out error);
        }
        var selectedCandidates = recipe.FeatureGroups
            .Select(group => group.SelectedCandidateSha256Hex)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in document.Bundles)
        {
            if (bundle.SelectedForRuntime != selectedCandidates.Contains(bundle.CandidateSha256Hex))
                return Fail($"resource IR selected candidate disagrees with recipe: {bundle.Id}", out error);
        }

        var bindingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in recipe.Bindings.Where(binding =>
                     binding.Confidence.Equals("Proven", StringComparison.OrdinalIgnoreCase) ||
                     binding.Confidence == "1"))
        {
            var group = recipe.FeatureGroups.Single(item =>
                item.Id.Equals(binding.FeatureGroupId, StringComparison.OrdinalIgnoreCase));
            bindingKeys.Add(group.SelectedCandidateSha256Hex + "\0" + binding.AssetName + "\0" +
                            NormalizeType(binding.ExpectedType));
        }
        var irRequiredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in document.Assets.Where(asset => asset.RequiredByMod))
        {
            var bundle = bundlesById[asset.BundleId];
            irRequiredKeys.Add(bundle.CandidateSha256Hex + "\0" + asset.Name + "\0" +
                               NormalizeType(asset.ExpectedType));
        }
        if (!bindingKeys.SetEquals(irRequiredKeys))
            return Fail("resource IR required assets disagree with recipe bindings", out error);
        return true;
    }

    private static bool ValidateMaterialization(
        PcCompatResourceIrAsset asset,
        IReadOnlySet<string> payloadIds,
        out string? error)
    {
        switch (asset.MaterializationKind)
        {
            case PcCompatResourceIrMaterializationKind.MetadataOnly:
            case PcCompatResourceIrMaterializationKind.Unsupported:
                if (asset.CapabilityStableId.Length != 0 || asset.PayloadId.Length != 0 ||
                    asset.Texture != null || asset.Sprite != null || asset.Material != null ||
                    asset.TmpFont != null ||
                    asset.Prefab != null ||
                    asset.CloneCapabilityAsset)
                    return Fail($"metadata-only resource has a materialization reference: {asset.Id}", out error);
                break;
            case PcCompatResourceIrMaterializationKind.CapabilityReference:
                if (!ValidId(asset.CapabilityStableId, 256) || asset.PayloadId.Length != 0 ||
                    asset.Compatibility == PcCompatResourceIrCompatibility.Unsupported ||
                    asset.Texture != null || asset.Sprite != null || asset.Material != null ||
                    asset.TmpFont != null ||
                    asset.Prefab != null)
                    return Fail($"capability resource is invalid: {asset.Id}", out error);
                break;
            case PcCompatResourceIrMaterializationKind.TextureRgba32:
                if (!ValidId(asset.PayloadId, 128) || !payloadIds.Contains(asset.PayloadId) ||
                    asset.Compatibility == PcCompatResourceIrCompatibility.Unsupported ||
                    !ValidTexture(asset.Texture) || asset.Sprite != null || asset.Material != null ||
                    asset.TmpFont != null ||
                    asset.Prefab != null ||
                    asset.CloneCapabilityAsset)
                    return Fail($"RGBA32 texture resource is invalid: {asset.Id}", out error);
                break;
            case PcCompatResourceIrMaterializationKind.TextureAlpha8:
                if (!ValidId(asset.PayloadId, 128) || !payloadIds.Contains(asset.PayloadId) ||
                    asset.Compatibility == PcCompatResourceIrCompatibility.Unsupported ||
                    !ValidTexture(asset.Texture) || asset.Texture!.SourceFormat != 1 ||
                    asset.Sprite != null || asset.Material != null || asset.TmpFont != null ||
                    asset.Prefab != null || asset.CloneCapabilityAsset)
                    return Fail($"Alpha8 texture resource is invalid: {asset.Id}", out error);
                break;
            case PcCompatResourceIrMaterializationKind.SpriteFromTexture:
                if (asset.PayloadId.Length != 0 || asset.Texture != null || asset.Material != null ||
                    asset.TmpFont != null ||
                    asset.Prefab != null ||
                    asset.CloneCapabilityAsset ||
                    asset.Compatibility == PcCompatResourceIrCompatibility.Unsupported ||
                    !ValidSprite(asset.Sprite) ||
                    !asset.DependencyIds.Contains(asset.Sprite!.TextureAssetId, StringComparer.Ordinal))
                    return Fail($"sprite resource is invalid: {asset.Id}", out error);
                break;
            case PcCompatResourceIrMaterializationKind.MaterialFromCapabilityShader:
                if (!ValidId(asset.CapabilityStableId, 256) || asset.PayloadId.Length != 0 ||
                    asset.Texture != null || asset.Sprite != null || asset.CloneCapabilityAsset ||
                    asset.TmpFont != null ||
                    asset.Prefab != null ||
                    asset.Compatibility == PcCompatResourceIrCompatibility.Unsupported ||
                    !ValidMaterial(asset.Material, asset.DependencyIds))
                    return Fail($"material resource is invalid: {asset.Id}", out error);
                break;
            case PcCompatResourceIrMaterializationKind.TmpFontFromAtlas:
                if (!ValidId(asset.CapabilityStableId, 256) || !asset.CloneCapabilityAsset ||
                    !ValidId(asset.PayloadId, 128) || !payloadIds.Contains(asset.PayloadId) ||
                    asset.Compatibility == PcCompatResourceIrCompatibility.Unsupported ||
                    asset.Texture != null || asset.Sprite != null || asset.Material != null ||
                    asset.Prefab != null || !ValidTmpFont(asset.TmpFont, asset.DependencyIds))
                    return Fail($"TMP font resource is invalid: {asset.Id}", out error);
                break;
            case PcCompatResourceIrMaterializationKind.PrefabGraph:
                if (asset.CapabilityStableId.Length != 0 || asset.PayloadId.Length != 0 ||
                    asset.Texture != null || asset.Sprite != null || asset.Material != null ||
                    asset.TmpFont != null ||
                    asset.CloneCapabilityAsset ||
                    asset.Compatibility == PcCompatResourceIrCompatibility.Unsupported ||
                    !ValidPrefab(asset.Prefab, asset.DependencyIds))
                    return Fail($"prefab graph resource is invalid: {asset.Id}", out error);
                break;
            default:
                if (!ValidId(asset.PayloadId, 128) || !payloadIds.Contains(asset.PayloadId) ||
                    asset.Compatibility == PcCompatResourceIrCompatibility.Unsupported ||
                    asset.CloneCapabilityAsset || asset.Material != null || asset.TmpFont != null ||
                    asset.Prefab != null)
                    return Fail($"payload-backed resource is invalid: {asset.Id}", out error);
                break;
        }
        error = null;
        return true;
    }

    private static bool ValidTexture(PcCompatResourceIrTextureInfo? texture)
        => texture != null && texture.Width is > 0 and <= 16_384 &&
           texture.Height is > 0 and <= 16_384 && texture.MipCount is > 0 and <= 32;

    private static bool ValidSprite(PcCompatResourceIrSpriteInfo? sprite)
        => sprite != null && ValidId(sprite.TextureAssetId, 128) &&
           float.IsFinite(sprite.X) && float.IsFinite(sprite.Y) &&
           float.IsFinite(sprite.Width) && sprite.Width > 0 &&
           float.IsFinite(sprite.Height) && sprite.Height > 0 &&
           float.IsFinite(sprite.PivotX) && float.IsFinite(sprite.PivotY) &&
           float.IsFinite(sprite.PixelsPerUnit) && sprite.PixelsPerUnit > 0 &&
           float.IsFinite(sprite.BorderLeft) && float.IsFinite(sprite.BorderBottom) &&
           float.IsFinite(sprite.BorderRight) && float.IsFinite(sprite.BorderTop);

    private static bool ValidMaterial(
        PcCompatResourceIrMaterialInfo? material,
        IReadOnlyList<string> dependencyIds)
    {
        if (material == null || material.CustomRenderQueue is < -1 or > 5000 ||
            material.GlobalIlluminationFlags is < 0 or > 15 ||
            material.Keywords.Count > 64 || material.Ints.Count > 128 ||
            material.Floats.Count > 256 || material.Colors.Count > 128 ||
            material.Textures.Count > 64 ||
            material.Keywords.Any(keyword => !ValidText(keyword, 128)) ||
            material.Keywords.Distinct(StringComparer.Ordinal).Count() != material.Keywords.Count)
            return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (!material.Ints.All(value => ValidText(value.PropertyName, 128) && names.Add(value.PropertyName)) ||
            !material.Floats.All(value => ValidText(value.PropertyName, 128) &&
                                          float.IsFinite(value.Value) && names.Add(value.PropertyName)) ||
            !material.Colors.All(value => ValidText(value.PropertyName, 128) &&
                                          float.IsFinite(value.R) && float.IsFinite(value.G) &&
                                          float.IsFinite(value.B) && float.IsFinite(value.A) &&
                                          names.Add(value.PropertyName)) ||
            !material.Textures.All(value => ValidText(value.PropertyName, 128) &&
                                            (value.TextureAssetId.Length == 0 ||
                                             ValidId(value.TextureAssetId, 128)) &&
                                            float.IsFinite(value.OffsetX) && float.IsFinite(value.OffsetY) &&
                                            float.IsFinite(value.ScaleX) && float.IsFinite(value.ScaleY) &&
                                            names.Add(value.PropertyName)))
            return false;
        var textureDependencies = material.Textures
            .Select(value => value.TextureAssetId)
            .Where(value => value.Length != 0)
            .ToHashSet(StringComparer.Ordinal);
        return textureDependencies.SetEquals(dependencyIds);
    }

    private static bool ValidTmpFont(
        PcCompatResourceIrTmpFontInfo? font,
        IReadOnlyList<string> dependencyIds)
    {
        if (font == null || !ValidTmpFontFace(font.Face) ||
            !ValidId(font.MaterialAssetId, 128) ||
            font.AtlasTextureAssetIds.Count is < 1 or > 16 ||
            font.AtlasTextureAssetIds.Any(id => !ValidId(id, 128)) ||
            font.AtlasTextureAssetIds.Distinct(StringComparer.Ordinal).Count() !=
            font.AtlasTextureAssetIds.Count ||
            font.AtlasTextureIndex < 0 || font.AtlasTextureIndex >= font.AtlasTextureAssetIds.Count ||
            font.AtlasPopulationMode is < 0 or > 2 ||
            font.AtlasWidth is <= 0 or > 16_384 || font.AtlasHeight is <= 0 or > 16_384 ||
            font.AtlasPadding is < 0 or > 256 || font.GlyphCount is <= 0 or > 262_144 ||
            font.CharacterCount is <= 0 or > 262_144 ||
            font.ItalicStyle is < 0 or > 255 || font.TabSize is < 0 or > 255 ||
            !Finite(font.NormalStyle, font.NormalSpacingOffset, font.BoldStyle, font.BoldSpacing))
            return false;
        var references = font.AtlasTextureAssetIds
            .Append(font.MaterialAssetId)
            .ToHashSet(StringComparer.Ordinal);
        return references.SetEquals(dependencyIds);
    }

    private static bool ValidTmpFontFace(PcCompatResourceIrTmpFontFaceInfo? face)
        => face != null && face.FaceIndex >= 0 && ValidText(face.FamilyName, 512) &&
           ValidText(face.StyleName, 512) && face.UnitsPerEm > 0 &&
           Finite(
               face.PointSize, face.Scale, face.LineHeight, face.AscentLine, face.CapLine,
               face.MeanLine, face.Baseline, face.DescentLine, face.SuperscriptOffset,
               face.SuperscriptSize, face.SubscriptOffset, face.SubscriptSize,
               face.UnderlineOffset, face.UnderlineThickness, face.StrikethroughOffset,
               face.StrikethroughThickness, face.TabWidth);

    private static bool ValidPrefab(
        PcCompatResourceIrPrefabInfo? prefab,
        IReadOnlyList<string> dependencyIds)
    {
        if (prefab == null || prefab.Nodes.Count is < 1 or > 128 ||
            dependencyIds.Count > 384 ||
            dependencyIds.Distinct(StringComparer.Ordinal).Count() != dependencyIds.Count)
            return false;
        var references = new HashSet<string>(StringComparer.Ordinal);
        var depths = new int[prefab.Nodes.Count];
        for (var index = 0; index < prefab.Nodes.Count; index++)
        {
            var node = prefab.Nodes[index];
            if (!ValidText(node.Name, 256) || node.Layer is < 0 or > 31 ||
                (index == 0 && node.ParentIndex != -1) ||
                (index > 0 && (node.ParentIndex < 0 || node.ParentIndex >= index)) ||
                node.Transform == null || !ValidPrefabTransform(node.Transform) ||
                (node.Image != null && node.RawImage != null) ||
                ((node.Image != null || node.RawImage != null) && node.CanvasRenderer == null))
                return false;
            depths[index] = index == 0 ? 0 : depths[node.ParentIndex] + 1;
            if (depths[index] > 32)
                return false;
            if (node.Image != null && !ValidPrefabImage(node.Image, references))
                return false;
            if (node.RawImage != null && !ValidPrefabRawImage(node.RawImage, references))
                return false;
        }
        return references.SetEquals(dependencyIds);
    }

    private static bool ValidPrefabTransform(PcCompatResourceIrPrefabTransform value)
        => Finite(
            value.LocalPositionX, value.LocalPositionY, value.LocalPositionZ,
            value.LocalRotationX, value.LocalRotationY, value.LocalRotationZ, value.LocalRotationW,
            value.LocalScaleX, value.LocalScaleY, value.LocalScaleZ,
            value.AnchorMinX, value.AnchorMinY, value.AnchorMaxX, value.AnchorMaxY,
            value.AnchoredPositionX, value.AnchoredPositionY,
            value.SizeDeltaX, value.SizeDeltaY, value.PivotX, value.PivotY) &&
           (value.LocalRotationX != 0f || value.LocalRotationY != 0f ||
            value.LocalRotationZ != 0f || value.LocalRotationW != 0f);

    private static bool ValidPrefabImage(
        PcCompatResourceIrPrefabImage value,
        ISet<string> references)
    {
        if (!ValidPrefabGraphic(value.Graphic, references) ||
            value.Type is < 0 or > 3 || value.FillMethod is < 0 or > 4 ||
            value.FillOrigin is < 0 or > 3 || !float.IsFinite(value.FillAmount) ||
            value.FillAmount is < 0f or > 1f ||
            !float.IsFinite(value.PixelsPerUnitMultiplier) || value.PixelsPerUnitMultiplier <= 0f)
            return false;
        return AddOptionalReference(value.SpriteAssetId, references);
    }

    private static bool ValidPrefabRawImage(
        PcCompatResourceIrPrefabRawImage value,
        ISet<string> references)
        => ValidPrefabGraphic(value.Graphic, references) &&
           Finite(value.UvX, value.UvY, value.UvWidth, value.UvHeight) &&
           value.UvWidth >= 0f && value.UvHeight >= 0f &&
           AddOptionalReference(value.TextureAssetId, references);

    private static bool ValidPrefabGraphic(
        PcCompatResourceIrPrefabGraphic? value,
        ISet<string> references)
        => value != null && Finite(value.ColorR, value.ColorG, value.ColorB, value.ColorA) &&
           AddOptionalReference(value.MaterialAssetId, references);

    private static bool AddOptionalReference(string value, ISet<string> references)
    {
        if (value.Length == 0)
            return true;
        if (!ValidId(value, 128))
            return false;
        references.Add(value);
        return true;
    }

    private static bool Finite(params float[] values)
        => values.All(float.IsFinite);

    private static string NormalizeType(string value)
        => value.Trim() switch
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
            var identity => identity
        };

    private static bool EquivalentEnum(string left, string right)
    {
        if (left.Equals(right, StringComparison.OrdinalIgnoreCase))
            return true;
        return int.TryParse(left, out var leftNumber) && int.TryParse(right, out var rightNumber) &&
               leftNumber == rightNumber;
    }

    private static bool SafeRelativePath(string value)
    {
        if (!ValidText(value, 4096) || Path.IsPathRooted(value))
            return false;
        return value.Replace('\\', '/').Split('/').All(segment =>
            segment.Length != 0 && segment is not "." and not "..");
    }

    private static bool SafeFileName(string value)
        => ValidText(value, 1024) && value is not "." and not ".." &&
           value.IndexOfAny(['/', '\\']) < 0 && Path.GetFileName(value) == value;

    private static bool ValidId(string value, int maxLength)
        => ValidText(value, maxLength) && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':' or '/');

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
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                var mask = 0u - (crc & 1u);
                crc = (crc >> 1) ^ (0xEDB88320u & mask);
            }
        }
        return ~crc;
    }
}

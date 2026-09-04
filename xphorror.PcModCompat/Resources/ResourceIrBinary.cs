using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xphorror.PcModCompat.Resources;

public static class ResourceIrBinary
{
    public const string FormatVersion = "resource-ir-v1";
    public const ushort SchemaVersion = 1;
    public const ushort HeaderSize = 64;
    public const int MaxFileSize = 32 * 1024 * 1024;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("XPHRIR01");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Write(string path, ResourceIrDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        if (!TryValidateDocument(document, out var error))
            throw new InvalidDataException("Resource IR validation failed: " + error);

        var json = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (HeaderSize + json.Length > MaxFileSize)
            throw new InvalidDataException("Resource IR exceeds maximum file size.");
        var bytes = new byte[HeaderSize + json.Length];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), SchemaVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10, 2), HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), checked((uint)json.Length));
        SHA256.HashData(json).CopyTo(bytes, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(52, 4), checked((uint)bytes.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56, 4), Crc32(json));
        json.CopyTo(bytes.AsSpan(HeaderSize));
        File.WriteAllBytes(path, bytes);
    }

    public static bool TryRead(string path, out ResourceIrDocument document, out string? error)
    {
        document = null!;
        error = null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (!TryReadPayload(bytes, out var json, out error))
                return false;
            document = JsonSerializer.Deserialize<ResourceIrDocument>(json, JsonOptions)!;
            if (!TryValidateDocument(document, out error))
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

    public static bool TryValidate(string path, out string? error)
        => TryRead(path, out _, out error);

    public static bool TryVerifyPayloadFiles(
        string resourceIrPath,
        ResourceIrDocument document,
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

    public static bool TryValidateDocument(ResourceIrDocument? document, out string? error)
    {
        error = null;
        if (document == null || document.FormatVersion != FormatVersion ||
            !ValidText(document.ModId, 256) || !ValidText(document.TargetUnityVersion, 64))
            return Fail("resource IR identity is invalid", out error);
        if (document.Bundles.Count > 256 || document.Assets.Count > 65_536 ||
            document.Payloads.Count > 65_536 || document.Warnings.Count > 4096)
            return Fail("resource IR collection count exceeds limits", out error);

        var bundleIds = new HashSet<string>(StringComparer.Ordinal);
        var bundleShas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in document.Bundles)
        {
            if (!ValidId(bundle.Id, 128) || !bundleIds.Add(bundle.Id) ||
                !IsSha256(bundle.CandidateSha256Hex) || !bundleShas.Add(bundle.CandidateSha256Hex) ||
                !SafeFileName(bundle.SourceFileName) || !SafeRelativePath(bundle.SourceRelativePath) ||
                !ValidText(bundle.PlatformHint, 32) ||
                !ValidText(bundle.UnityVersion, 64) || !ValidText(bundle.LoadPolicy, 32) ||
                bundle.AssetIds.Count > 65_536 || bundle.AssetIds.Distinct(StringComparer.Ordinal).Count() != bundle.AssetIds.Count)
                return Fail("resource IR bundle metadata is invalid", out error);
        }

        var payloadIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var payload in document.Payloads)
        {
            if (!ValidId(payload.Id, 128) || !payloadIds.Add(payload.Id) ||
                !ValidText(payload.Kind, 64) || !SafeRelativePath(payload.RelativePath) ||
                !IsSha256(payload.Sha256Hex) || payload.Length < 0 ||
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
                asset.Tags.Count > 256)
                return Fail("resource IR asset metadata is invalid", out error);
            if (!ValidateMaterialization(asset, payloadIds, out error))
                return false;
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

    private static bool TryReadPayload(byte[] bytes, out ReadOnlySpan<byte> json, out string? error)
    {
        json = default;
        error = null;
        if (bytes.Length is < HeaderSize or > MaxFileSize || !bytes.AsSpan(0, 8).SequenceEqual(Magic))
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
        json = bytes.AsSpan(HeaderSize);
        if (!bytes.AsSpan(20, 32).SequenceEqual(SHA256.HashData(json)) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(56, 4)) != Crc32(json))
            return Fail("resource IR hash or crc mismatch", out error);
        return true;
    }

    private static bool ValidateMaterialization(
        ResourceIrAsset asset,
        IReadOnlySet<string> payloadIds,
        out string? error)
    {
        switch (asset.MaterializationKind)
        {
            case ResourceIrMaterializationKind.MetadataOnly:
            case ResourceIrMaterializationKind.Unsupported:
                if (asset.CapabilityStableId.Length != 0 || asset.PayloadId.Length != 0 ||
                    asset.Texture != null || asset.Sprite != null || asset.Material != null ||
                    asset.TmpFont != null ||
                    asset.Prefab != null ||
                    asset.CloneCapabilityAsset)
                    return Fail($"metadata-only resource has a materialization reference: {asset.Id}", out error);
                break;
            case ResourceIrMaterializationKind.CapabilityReference:
                if (!ValidId(asset.CapabilityStableId, 256) || asset.PayloadId.Length != 0 ||
                    asset.Compatibility == ResourceIrCompatibility.Unsupported ||
                    asset.Texture != null || asset.Sprite != null || asset.Material != null ||
                    asset.TmpFont != null ||
                    asset.Prefab != null)
                    return Fail($"capability resource is invalid: {asset.Id}", out error);
                break;
            case ResourceIrMaterializationKind.TextureRgba32:
                if (!ValidId(asset.PayloadId, 128) || !payloadIds.Contains(asset.PayloadId) ||
                    asset.Compatibility == ResourceIrCompatibility.Unsupported ||
                    !ValidTexture(asset.Texture) || asset.Sprite != null || asset.Material != null ||
                    asset.TmpFont != null ||
                    asset.Prefab != null ||
                    asset.CloneCapabilityAsset)
                    return Fail($"RGBA32 texture resource is invalid: {asset.Id}", out error);
                break;
            case ResourceIrMaterializationKind.TextureAlpha8:
                if (!ValidId(asset.PayloadId, 128) || !payloadIds.Contains(asset.PayloadId) ||
                    asset.Compatibility == ResourceIrCompatibility.Unsupported ||
                    !ValidTexture(asset.Texture) || asset.Texture!.SourceFormat != 1 ||
                    asset.Sprite != null || asset.Material != null || asset.TmpFont != null ||
                    asset.Prefab != null || asset.CloneCapabilityAsset)
                    return Fail($"Alpha8 texture resource is invalid: {asset.Id}", out error);
                break;
            case ResourceIrMaterializationKind.FontFromFile:
                if (!ValidId(asset.PayloadId, 128) || !payloadIds.Contains(asset.PayloadId) ||
                    asset.Compatibility == ResourceIrCompatibility.Unsupported ||
                    asset.CapabilityStableId.Length != 0 || asset.Texture != null ||
                    asset.Sprite != null || asset.Material != null || asset.TmpFont != null ||
                    asset.Prefab != null || asset.CloneCapabilityAsset)
                    return Fail($"font file resource is invalid: {asset.Id}", out error);
                break;
            case ResourceIrMaterializationKind.SpriteFromTexture:
                if (asset.PayloadId.Length != 0 || asset.Texture != null || asset.Material != null ||
                    asset.TmpFont != null ||
                    asset.Prefab != null ||
                    asset.CloneCapabilityAsset ||
                    asset.Compatibility == ResourceIrCompatibility.Unsupported ||
                    !ValidSprite(asset.Sprite) ||
                    !asset.DependencyIds.Contains(asset.Sprite!.TextureAssetId, StringComparer.Ordinal))
                    return Fail($"sprite resource is invalid: {asset.Id}", out error);
                break;
            case ResourceIrMaterializationKind.MaterialFromCapabilityShader:
                if (!ValidId(asset.CapabilityStableId, 256) || asset.PayloadId.Length != 0 ||
                    asset.Texture != null || asset.Sprite != null || asset.CloneCapabilityAsset ||
                    asset.TmpFont != null ||
                    asset.Prefab != null ||
                    asset.Compatibility == ResourceIrCompatibility.Unsupported ||
                    !ValidMaterial(asset.Material, asset.DependencyIds))
                    return Fail($"material resource is invalid: {asset.Id}", out error);
                break;
            case ResourceIrMaterializationKind.TmpFontFromAtlas:
                if (!ValidId(asset.CapabilityStableId, 256) || !asset.CloneCapabilityAsset ||
                    !ValidId(asset.PayloadId, 128) || !payloadIds.Contains(asset.PayloadId) ||
                    asset.Compatibility == ResourceIrCompatibility.Unsupported ||
                    asset.Texture != null || asset.Sprite != null || asset.Material != null ||
                    asset.Prefab != null || !ValidTmpFont(asset.TmpFont, asset.DependencyIds))
                    return Fail($"TMP font resource is invalid: {asset.Id}", out error);
                break;
            case ResourceIrMaterializationKind.PrefabGraph:
                if (asset.CapabilityStableId.Length != 0 || asset.PayloadId.Length != 0 ||
                    asset.Texture != null || asset.Sprite != null || asset.Material != null ||
                    asset.TmpFont != null ||
                    asset.CloneCapabilityAsset ||
                    asset.Compatibility == ResourceIrCompatibility.Unsupported ||
                    !ValidPrefab(asset.Prefab, asset.DependencyIds))
                    return Fail($"prefab graph resource is invalid: {asset.Id}", out error);
                break;
            default:
                if (!ValidId(asset.PayloadId, 128) || !payloadIds.Contains(asset.PayloadId) ||
                    asset.Compatibility == ResourceIrCompatibility.Unsupported || asset.CloneCapabilityAsset ||
                    asset.Material != null || asset.TmpFont != null || asset.Prefab != null)
                    return Fail($"payload-backed resource is invalid: {asset.Id}", out error);
                break;
        }
        error = null;
        return true;
    }

    private static bool ValidTexture(ResourceIrTextureInfo? texture)
        => texture != null && texture.Width is > 0 and <= 16_384 &&
           texture.Height is > 0 and <= 16_384 && texture.MipCount is > 0 and <= 32;

    private static bool ValidSprite(ResourceIrSpriteInfo? sprite)
        => sprite != null && ValidId(sprite.TextureAssetId, 128) &&
           float.IsFinite(sprite.X) && float.IsFinite(sprite.Y) &&
           float.IsFinite(sprite.Width) && sprite.Width > 0 &&
           float.IsFinite(sprite.Height) && sprite.Height > 0 &&
           float.IsFinite(sprite.PivotX) && float.IsFinite(sprite.PivotY) &&
           float.IsFinite(sprite.PixelsPerUnit) && sprite.PixelsPerUnit > 0 &&
           float.IsFinite(sprite.BorderLeft) && float.IsFinite(sprite.BorderBottom) &&
           float.IsFinite(sprite.BorderRight) && float.IsFinite(sprite.BorderTop);

    private static bool ValidMaterial(
        ResourceIrMaterialInfo? material,
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
        ResourceIrTmpFontInfo? font,
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

    private static bool ValidTmpFontFace(ResourceIrTmpFontFaceInfo? face)
        => face != null && face.FaceIndex >= 0 && ValidText(face.FamilyName, 512) &&
           ValidText(face.StyleName, 512) && face.UnitsPerEm > 0 &&
           Finite(
               face.PointSize, face.Scale, face.LineHeight, face.AscentLine, face.CapLine,
               face.MeanLine, face.Baseline, face.DescentLine, face.SuperscriptOffset,
               face.SuperscriptSize, face.SubscriptOffset, face.SubscriptSize,
               face.UnderlineOffset, face.UnderlineThickness, face.StrikethroughOffset,
               face.StrikethroughThickness, face.TabWidth);

    private static bool ValidPrefab(
        ResourceIrPrefabInfo? prefab,
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

    private static bool ValidPrefabTransform(ResourceIrPrefabTransform value)
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
        ResourceIrPrefabImage value,
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
        ResourceIrPrefabRawImage value,
        ISet<string> references)
        => ValidPrefabGraphic(value.Graphic, references) &&
           Finite(value.UvX, value.UvY, value.UvWidth, value.UvHeight) &&
           value.UvWidth >= 0f && value.UvHeight >= 0f &&
           AddOptionalReference(value.TextureAssetId, references);

    private static bool ValidPrefabGraphic(
        ResourceIrPrefabGraphic? value,
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

    private static bool SafeRelativePath(string value)
    {
        if (!ValidText(value, 4096) || Path.IsPathRooted(value))
            return false;
        var normalized = value.Replace('\\', '/');
        return normalized.Split('/').All(segment => segment.Length != 0 && segment is not "." and not "..");
    }

    private static bool SafeFileName(string value)
        => ValidText(value, 1024) && value is not "." and not ".." &&
           value.IndexOfAny(['/', '\\']) < 0 && Path.GetFileName(value) == value;

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

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

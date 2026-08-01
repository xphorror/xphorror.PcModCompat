namespace Xphorror.PcModCompat.Resources;

public enum ResourceIrMaterializationKind
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

public enum ResourceIrCompatibility
{
    Exact = 0,
    Compatible = 1,
    Unsupported = 2
}

public sealed class ResourceIrBundle
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

public sealed record ResourceIrAsset
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
    public required ResourceIrMaterializationKind MaterializationKind { get; init; }
    public required ResourceIrCompatibility Compatibility { get; init; }
    public string CapabilityStableId { get; init; } = string.Empty;
    public bool CloneCapabilityAsset { get; init; }
    public string PayloadId { get; init; } = string.Empty;
    public IReadOnlyList<string> DependencyIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public ResourceIrTextureInfo? Texture { get; init; }
    public ResourceIrSpriteInfo? Sprite { get; init; }
    public ResourceIrMaterialInfo? Material { get; init; }
    public ResourceIrTmpFontInfo? TmpFont { get; init; }
    public ResourceIrPrefabInfo? Prefab { get; init; }
}

public sealed class ResourceIrPayload
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string RelativePath { get; init; }
    public required string Sha256Hex { get; init; }
    public long Length { get; init; }
    public string Compression { get; init; } = "none";
}

public sealed class ResourceIrTextureInfo
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

public sealed class ResourceIrSpriteInfo
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

public sealed class ResourceIrMaterialInfo
{
    public int CustomRenderQueue { get; init; } = -1;
    public int GlobalIlluminationFlags { get; init; }
    public bool EnableInstancing { get; init; }
    public bool DoubleSidedGi { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ResourceIrMaterialInt> Ints { get; init; } = Array.Empty<ResourceIrMaterialInt>();
    public IReadOnlyList<ResourceIrMaterialFloat> Floats { get; init; } = Array.Empty<ResourceIrMaterialFloat>();
    public IReadOnlyList<ResourceIrMaterialColor> Colors { get; init; } = Array.Empty<ResourceIrMaterialColor>();
    public IReadOnlyList<ResourceIrMaterialTexture> Textures { get; init; } = Array.Empty<ResourceIrMaterialTexture>();
}

public sealed record ResourceIrMaterialInt(string PropertyName, int Value);

public sealed record ResourceIrMaterialFloat(string PropertyName, float Value);

public sealed record ResourceIrMaterialColor(
    string PropertyName,
    float R,
    float G,
    float B,
    float A);

public sealed record ResourceIrMaterialTexture(
    string PropertyName,
    string TextureAssetId,
    float OffsetX,
    float OffsetY,
    float ScaleX,
    float ScaleY);

public sealed class ResourceIrTmpFontInfo
{
    public required ResourceIrTmpFontFaceInfo Face { get; init; }
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

public sealed class ResourceIrTmpFontFaceInfo
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

public sealed class ResourceIrPrefabInfo
{
    public IReadOnlyList<ResourceIrPrefabNode> Nodes { get; init; } = Array.Empty<ResourceIrPrefabNode>();
}

public sealed class ResourceIrPrefabNode
{
    public required string Name { get; init; }
    public int ParentIndex { get; init; } = -1;
    public int Layer { get; init; }
    public bool Active { get; init; } = true;
    public required ResourceIrPrefabTransform Transform { get; init; }
    public ResourceIrPrefabCanvasRenderer? CanvasRenderer { get; init; }
    public ResourceIrPrefabImage? Image { get; init; }
    public ResourceIrPrefabRawImage? RawImage { get; init; }
}

public sealed class ResourceIrPrefabTransform
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

public sealed class ResourceIrPrefabCanvasRenderer
{
    public bool CullTransparentMesh { get; init; } = true;
}

public sealed class ResourceIrPrefabGraphic
{
    public float ColorR { get; init; } = 1f;
    public float ColorG { get; init; } = 1f;
    public float ColorB { get; init; } = 1f;
    public float ColorA { get; init; } = 1f;
    public bool RaycastTarget { get; init; } = true;
    public bool Maskable { get; init; } = true;
    public string MaterialAssetId { get; init; } = string.Empty;
}

public sealed class ResourceIrPrefabImage
{
    public required ResourceIrPrefabGraphic Graphic { get; init; }
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

public sealed class ResourceIrPrefabRawImage
{
    public required ResourceIrPrefabGraphic Graphic { get; init; }
    public string TextureAssetId { get; init; } = string.Empty;
    public float UvX { get; init; }
    public float UvY { get; init; }
    public float UvWidth { get; init; } = 1f;
    public float UvHeight { get; init; } = 1f;
}

public sealed record ResourceIrDocument
{
    public string FormatVersion { get; init; } = ResourceIrBinary.FormatVersion;
    public required string ModId { get; init; }
    public required string TargetUnityVersion { get; init; }
    public IReadOnlyList<ResourceIrBundle> Bundles { get; init; } = Array.Empty<ResourceIrBundle>();
    public IReadOnlyList<ResourceIrAsset> Assets { get; init; } = Array.Empty<ResourceIrAsset>();
    public IReadOnlyList<ResourceIrPayload> Payloads { get; init; } = Array.Empty<ResourceIrPayload>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class ResourceIrAliasDocument
{
    public int SchemaVersion { get; init; }
    public IReadOnlyList<ResourceIrAliasEntry> Assets { get; init; } = Array.Empty<ResourceIrAliasEntry>();
}

public sealed class ResourceIrAliasEntry
{
    public string CandidateSha256Hex { get; init; } = string.Empty;
    public required string AssetName { get; init; }
    public required string ExpectedType { get; init; }
    public required string CapabilityStableId { get; init; }
    public string Compatibility { get; init; } = "compatible";
    public string Reason { get; init; } = string.Empty;
    public bool Clone { get; init; }
}

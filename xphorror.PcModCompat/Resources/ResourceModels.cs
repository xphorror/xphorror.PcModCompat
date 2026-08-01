using System.Text.Json.Serialization;

namespace Xphorror.PcModCompat.Resources;

public enum BundlePlatformHint
{
    Unknown = 0,
    Android = 1,
    Linux = 2,
    Windows = 3,
    Mac = 4,
}

public enum UnityVersionGate
{
    Auto = 0,
    Controlled = 1,
    ForcedOnly = 2,
    Unknown = 3,
}

public enum BundleLoadPolicy
{
    AutoLoad = 0,
    ControlledLoad = 1,
    ForceRequired = 2,
    IndexOnly = 3,
    Rejected = 4,
}

public enum AssetBindConfidence
{
    Unbound = 0,
    Proven = 1,
    UniqueType = 2,
    SemanticMatch = 3,
    FuzzyMatch = 4,
}

public sealed class ResourceCandidateIndex
{
    public required string SourcePath { get; init; }
    public required string FileName { get; init; }
    public required BundlePlatformHint PlatformHint { get; init; }
    public required string UnityVersion { get; init; }
    public required UnityVersionGate VersionGate { get; init; }
    public required BundleLoadPolicy LoadPolicy { get; init; }
    public required long FileSize { get; init; }
    public required string Sha256Hex { get; init; }
    public bool HasEmbeddedTypeTree { get; init; }
    public bool IndexSucceeded { get; init; }
    public string? IndexError { get; init; }
    public IReadOnlyList<string> DirectoryEntries { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ResourceAssetEntry> Assets { get; init; } = Array.Empty<ResourceAssetEntry>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class ResourceAssetEntry
{
    public required string Name { get; init; }
    public required string TypeName { get; init; }
    public long PathId { get; init; }
    public int TypeId { get; init; }
    public string Container { get; init; } = string.Empty;
    public string AssetsFileName { get; init; } = string.Empty;
}

public sealed class ResourceFeatureGroup
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string SelectedCandidateSha256Hex { get; init; }
    public required BundlePlatformHint SelectedPlatform { get; init; }
    public required BundleLoadPolicy LoadPolicy { get; init; }
    public IReadOnlyList<string> AssetNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed class ResourceBinding
{
    public required string FeatureGroupId { get; init; }
    public required string AssetName { get; init; }
    public required string ExpectedType { get; init; }
    public required AssetBindConfidence Confidence { get; init; }
    public string SourceFieldIdentity { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed class ResourceCompileReport
{
    public string FormatVersion { get; init; } = "resource-compile-v1";
    public required string ModId { get; init; }
    public required string Compatibility { get; init; }
    public required string TargetUnityVersion { get; init; }
    public IReadOnlyList<ResourceCandidateIndex> Candidates { get; init; } = Array.Empty<ResourceCandidateIndex>();
    public IReadOnlyList<ResourceFeatureGroup> FeatureGroups { get; init; } = Array.Empty<ResourceFeatureGroup>();
    public IReadOnlyList<ResourceBinding> Bindings { get; init; } = Array.Empty<ResourceBinding>();
    public IReadOnlyList<string> Unsupported { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class ResourceRecipeDocument
{
    public required string ModId { get; init; }
    public required string RecipeId { get; init; }
    public required string Compatibility { get; init; }
    public required string TargetUnityVersion { get; init; }
    public required IReadOnlyList<ResourceCandidateIndex> Candidates { get; init; }
    public required IReadOnlyList<ResourceFeatureGroup> FeatureGroups { get; init; }
    public required IReadOnlyList<ResourceBinding> Bindings { get; init; }
}

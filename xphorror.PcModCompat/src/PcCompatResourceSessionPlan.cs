namespace Xphorror.PcModCompat;

public enum PcCompatResourceCandidateStatus
{
    Pending = 0,
    Missing = 1,
    Rejected = 2,
    Ready = 3,
    LoadFailed = 4,
    Loaded = 5,
    Unloaded = 6,
    Controlled = 7,
    LoadQueued = 8
}

public enum PcCompatResourceLoadAuthorization
{
    None = 0,
    Controlled = 1,
    Forced = 2
}

public sealed class PcCompatResourceSessionCandidate
{
    public required string Sha256Hex { get; init; }
    public required string FileName { get; init; }
    public required string PlatformHint { get; init; }
    public required string LoadPolicy { get; init; }
    public required string ResolvedPath { get; init; }
    public long ExpectedFileSize { get; init; }
    public required bool AutoLoadAllowed { get; init; }
    public required PcCompatResourceCandidateStatus Status { get; init; }
    public string? StatusReason { get; init; }
}

public sealed class PcCompatResourceFeatureGroupPlan
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string SelectedCandidateSha256Hex { get; init; }
    public required IReadOnlyList<string> AssetNames { get; init; }
    public required string LoadPolicy { get; init; }
}

public sealed class PcCompatResourceSessionPlan
{
    public required string ModId { get; init; }
    public required string ModFolder { get; init; }
    public string? CompiledResourcesDirectory { get; init; }
    public required string Compatibility { get; init; }
    public required IReadOnlyList<PcCompatResourceSessionCandidate> Candidates { get; init; }
    public required IReadOnlyList<PcCompatResourceFeatureGroupPlan> FeatureGroups { get; init; }
}

public sealed class PcCompatResourceLoadResult
{
    public required bool Success { get; init; }
    public required string ModId { get; init; }
    public required string CandidateSha256Hex { get; init; }
    public required string Path { get; init; }
    public string? Error { get; init; }
    public bool CacheHit { get; init; }
    public bool Pending { get; init; }
    public bool Retryable { get; init; }
    public long SessionGeneration { get; init; }
}

public sealed class PcCompatResourceLoadRequest
{
    public required string ModId { get; init; }
    public required string CandidateSha256Hex { get; init; }
    public required string Path { get; init; }
    public required long ExpectedFileSize { get; init; }
    public required long SessionGeneration { get; init; }
}

public sealed class PcCompatResourceUnloadRequest
{
    public required string ModId { get; init; }
    public required string CandidateSha256Hex { get; init; }
    public required long SessionGeneration { get; init; }
}

/// <summary>
/// A recipe binding proven to belong to a bundle loaded by the current MOD
/// resource session. This is metadata only; the host still owns Unity object
/// loading and lifetime.
/// </summary>
public sealed class PcCompatResolvedResourceBinding
{
    public required string ModId { get; init; }
    public required string FeatureGroupId { get; init; }
    public required string CandidateSha256Hex { get; init; }
    public required string AssetName { get; init; }
    public required string ExpectedType { get; init; }
    public required string Confidence { get; init; }
    public required long SessionGeneration { get; init; }
}

/// <summary>
/// Read-only readiness view for diagnostics. Building this never loads AssetBundles.
/// </summary>
public sealed class PcCompatResourceReadinessSummary
{
    public required string ModId { get; init; }
    public required bool RecipeLoaded { get; init; }
    public required string Compatibility { get; init; }
    public required bool RuntimeLoadEnabled { get; init; }
    public required bool LoadSinkRegistered { get; init; }
    public required int FeatureGroupCount { get; init; }
    public required int CandidateCount { get; init; }
    public required int ReadyCandidateCount { get; init; }
    public required int ControlledCandidateCount { get; init; }
    public required int QueuedCandidateCount { get; init; }
    public required int LoadedCandidateCount { get; init; }
    public required int RejectedCandidateCount { get; init; }
    public required int MissingCandidateCount { get; init; }
    public required IReadOnlyList<string> FeatureGroupIds { get; init; }
    public string? CompiledResourcesDirectory { get; init; }
}

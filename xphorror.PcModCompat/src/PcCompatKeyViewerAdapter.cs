using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xphorror.PcModCompat;

public enum PcCompatAdapterEvidenceStatus
{
    Proven,
    Probable,
    Ambiguous,
    Unsupported
}

public enum PcCompatKeyViewerInputProfileKind
{
    LegacyUnityPolling,
    Win32Polling,
    InputSystemEvent,
    RewiredPolling,
    HarmonyGameAction,
    ManagedEventSource,
    CustomProvider
}

public enum PcCompatLaneBindingKind
{
    DirectIdentity,
    AliasSet,
    LogicalAction,
    GameAction,
    Chord,
    AnyOf,
    TouchLane,
    Wildcard
}

public enum PcCompatInputIdentityKind
{
    UnityKeyCode,
    AndroidKeyCode,
    WindowsVirtualKey,
    MouseButton,
    ControllerControl,
    TouchLane,
    ActionId,
    GameAction
}

public enum PcCompatKeyViewerCountEdge
{
    Rising,
    Falling,
    Both,
    Callback
}

public enum PcCompatKeyViewerBackend
{
    ManagedSelfRender,
    ProvenRecipe,
    CompatibleFallback,
    Disabled
}

public enum PcCompatKeyViewerIdentityTransformKind
{
    UnityKeyCodeIdentity,
    WindowsVirtualKeyIdentity,
    WindowsVirtualKeyOffset,
    UnityWindowsThresholdSplit
}

public sealed class PcCompatKeyViewerIdentityTransform
{
    public required string CandidateKey { get; init; }
    public PcCompatKeyViewerIdentityTransformKind Kind { get; init; }
    public int Threshold { get; init; }
    public int Offset { get; init; }
    public required PcCompatAdapterEvidence Evidence { get; init; }
}

public sealed class PcCompatAdapterEvidence
{
    public PcCompatAdapterEvidenceStatus Status { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
    public string? FirstBreak { get; init; }
    public string? SelectedCandidate { get; init; }
    public bool UserConfirmed { get; init; }
    public string? OriginalDisablePath { get; init; }
}

public sealed class PcCompatKeyViewerEvidenceMatrix
{
    public required PcCompatAdapterEvidence Input { get; init; }
    public required PcCompatAdapterEvidence Lane { get; init; }
    public required PcCompatAdapterEvidence Transition { get; init; }
    public required PcCompatAdapterEvidence Count { get; init; }
    public required PcCompatAdapterEvidence Kps { get; init; }
    public required PcCompatAdapterEvidence Rain { get; init; }
    public required PcCompatAdapterEvidence Presentation { get; init; }
    public required PcCompatAdapterEvidence Visibility { get; init; }
    public required PcCompatAdapterEvidence InputActivation { get; init; }
    public required PcCompatAdapterEvidence Settings { get; init; }
    public required PcCompatAdapterEvidence Persistence { get; init; }
}

public sealed class PcCompatAdapterAssemblyFingerprint
{
    public required string AssemblyName { get; init; }
    public required string Sha256 { get; init; }
    public required string Mvid { get; init; }
}

public sealed class PcCompatKeyViewerSourceProfile
{
    public required string Id { get; init; }
    public PcCompatKeyViewerInputProfileKind Kind { get; init; }
    public IReadOnlyList<string> EntryPoints { get; init; } = Array.Empty<string>();
    public required PcCompatAdapterEvidence Evidence { get; init; }
}

public sealed class PcCompatInputIdentity
{
    public PcCompatInputIdentityKind Kind { get; init; }
    public required string Value { get; init; }
    public int? DeviceId { get; init; }
    public int? ScanCode { get; init; }
}

public sealed class PcCompatLaneBinding
{
    public PcCompatLaneBindingKind Kind { get; init; }
    public IReadOnlyList<PcCompatInputIdentity> Identities { get; init; } =
        Array.Empty<PcCompatInputIdentity>();
    public int? TouchLane { get; init; }
    public string? SourceProfileId { get; init; }
    public bool CountEligible { get; init; } = true;
    public bool RainEligible { get; init; } = true;
}

public sealed class PcCompatKeyViewerLane
{
    public required string Id { get; init; }
    public required string DisplayLabel { get; init; }
    public required PcCompatLaneBinding Binding { get; init; }
}

public sealed class PcCompatKeyViewerLaneGroup
{
    public required string Id { get; init; }
    public string? DisplayName { get; init; }
    public IReadOnlyList<PcCompatKeyViewerLane> Lanes { get; init; } =
        Array.Empty<PcCompatKeyViewerLane>();
}

public sealed class PcCompatKeyViewerRoleBinding
{
    public required string Role { get; init; }
    public required string AssemblyName { get; init; }
    public required string TypeName { get; init; }
    public string? MemberName { get; init; }
    public string? MemberKind { get; init; }
    /// <summary>
    /// Proven destination lane base at the input-transaction call site. Zero identifies the primary
    /// lane group; a positive value identifies an appended group. Null is deliberately unranked.
    /// </summary>
    public int? ConsumerLaneBase { get; init; }
    public required PcCompatAdapterEvidence Evidence { get; init; }
}

public sealed class PcCompatKeyViewerPredicate
{
    public required string Kind { get; init; }
    public required string Expression { get; init; }
    public required PcCompatAdapterEvidence Evidence { get; init; }
}

public sealed class PcCompatKeyViewerCountSemantics
{
    public PcCompatKeyViewerCountEdge Edge { get; init; } = PcCompatKeyViewerCountEdge.Rising;
    public bool CountRepeats { get; init; }
    public bool GhostAffectsCount { get; init; }
    public int KpsWindowMilliseconds { get; init; } = 1000;
    public required string Clock { get; init; }
    public required string ResetEntryPoint { get; init; }
    public string? PersistencePath { get; init; }
    public string? BackupPersistencePath { get; init; }
}

public sealed class PcCompatKeyViewerFeatureAdapter
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public PcCompatKeyViewerBackend Backend { get; init; } =
        PcCompatKeyViewerBackend.ManagedSelfRender;
    public IReadOnlyList<PcCompatKeyViewerSourceProfile> SourceProfiles { get; init; } =
        Array.Empty<PcCompatKeyViewerSourceProfile>();
    public IReadOnlyList<PcCompatKeyViewerLaneGroup> LaneGroups { get; init; } =
        Array.Empty<PcCompatKeyViewerLaneGroup>();
    public IReadOnlyList<PcCompatKeyViewerRoleBinding> Roles { get; init; } =
        Array.Empty<PcCompatKeyViewerRoleBinding>();
    public IReadOnlyList<PcCompatKeyViewerIdentityTransform> IdentityTransforms { get; init; } =
        Array.Empty<PcCompatKeyViewerIdentityTransform>();
    public required PcCompatKeyViewerPredicate Visibility { get; init; }
    public required PcCompatKeyViewerPredicate InputActivation { get; init; }
    public required PcCompatKeyViewerCountSemantics CountSemantics { get; init; }
    public required PcCompatKeyViewerEvidenceMatrix Capabilities { get; init; }
}

public sealed class PcCompatKeyViewerAdapterDocument
{
    public const string CurrentFormatVersion = "keyviewer-adapter-v2-lane-origin";

    public string FormatVersion { get; init; } = CurrentFormatVersion;
    public required string ModId { get; init; }
    public required string PackageSha256 { get; init; }
    public int TargetGameRevision { get; init; }
    public required string ProxySurfaceHash { get; init; }
    public IReadOnlyList<PcCompatAdapterAssemblyFingerprint> Assemblies { get; init; } =
        Array.Empty<PcCompatAdapterAssemblyFingerprint>();
    public IReadOnlyList<PcCompatKeyViewerFeatureAdapter> Features { get; init; } =
        Array.Empty<PcCompatKeyViewerFeatureAdapter>();

    public string ToJson()
        => JsonSerializer.Serialize(Canonicalize(this), JsonOptions);

    public static PcCompatKeyViewerAdapterDocument? FromJson(string json)
        => JsonSerializer.Deserialize<PcCompatKeyViewerAdapterDocument>(json, JsonOptions);

    private static PcCompatKeyViewerAdapterDocument Canonicalize(
        PcCompatKeyViewerAdapterDocument document)
        => new()
        {
            FormatVersion = document.FormatVersion,
            ModId = document.ModId,
            PackageSha256 = document.PackageSha256,
            TargetGameRevision = document.TargetGameRevision,
            ProxySurfaceHash = document.ProxySurfaceHash,
            Assemblies = document.Assemblies
                .OrderBy(value => value.AssemblyName, StringComparer.Ordinal)
                .ThenBy(value => value.Mvid, StringComparer.Ordinal)
                .ToArray(),
            Features = document.Features
                .OrderBy(value => value.Id, StringComparer.Ordinal)
                .Select(Canonicalize)
                .ToArray()
        };

    private static PcCompatKeyViewerFeatureAdapter Canonicalize(
        PcCompatKeyViewerFeatureAdapter feature)
        => new()
        {
            Id = feature.Id,
            DisplayName = feature.DisplayName,
            Backend = feature.Backend,
            SourceProfiles = feature.SourceProfiles
                .OrderBy(value => value.Id, StringComparer.Ordinal)
                .ToArray(),
            LaneGroups = feature.LaneGroups
                .OrderBy(value => value.Id, StringComparer.Ordinal)
                .Select(group => new PcCompatKeyViewerLaneGroup
                {
                    Id = group.Id,
                    DisplayName = group.DisplayName,
                    Lanes = group.Lanes
                        .OrderBy(value => value.Id, StringComparer.Ordinal)
                        .ToArray()
                })
                .ToArray(),
            Roles = feature.Roles
                .OrderBy(value => value.Role, StringComparer.Ordinal)
                .ThenBy(value => value.TypeName, StringComparer.Ordinal)
                .ThenBy(value => value.MemberName, StringComparer.Ordinal)
                .ToArray(),
            IdentityTransforms = feature.IdentityTransforms
                .OrderBy(value => value.CandidateKey, StringComparer.Ordinal)
                .ToArray(),
            Visibility = feature.Visibility,
            InputActivation = feature.InputActivation,
            CountSemantics = feature.CountSemantics,
            Capabilities = feature.Capabilities
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed class PcCompatKeyViewerAdapterValidationContext
{
    public string? PackageSha256 { get; init; }
    public int? TargetGameRevision { get; init; }
    public string? ProxySurfaceHash { get; init; }
    public IReadOnlyDictionary<string, string> AssemblyMvids { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class PcCompatKeyViewerAdapterValidationResult
{
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public bool IsValid => Errors.Count == 0;
}

public static class PcCompatKeyViewerAdapterValidator
{
    private static readonly PcCompatAdapterEvidenceStatus[] ReadyStatuses =
        [PcCompatAdapterEvidenceStatus.Proven];

    public static PcCompatKeyViewerAdapterValidationResult Validate(
        PcCompatKeyViewerAdapterDocument document,
        PcCompatKeyViewerAdapterValidationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<string>();
        if (!string.Equals(
                document.FormatVersion,
                PcCompatKeyViewerAdapterDocument.CurrentFormatVersion,
                StringComparison.Ordinal)) {
            errors.Add($"formatVersion: unsupported value '{document.FormatVersion}'.");
        }
        RequireId(document.ModId, "modId", errors);
        RequireSha256(document.PackageSha256, "packageSha256", errors);
        RequireSha256(document.ProxySurfaceHash, "proxySurfaceHash", errors);
        if (document.TargetGameRevision <= 0)
            errors.Add("targetGameRevision: must be positive.");
        ValidateFingerprints(document.Assemblies, errors);
        ValidateUnique(document.Features.Select(value => value.Id), "features", errors);
        if (document.Features.Count == 0)
            errors.Add("features: at least one KeyViewer feature is required.");
        foreach (var feature in document.Features)
            ValidateFeature(feature, errors);
        ValidateContext(document, context, errors);
        return new PcCompatKeyViewerAdapterValidationResult { Errors = errors };
    }

    public static bool IsCoreReady(PcCompatKeyViewerFeatureAdapter feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        var matrix = feature.Capabilities;
        return ReadyStatuses.Contains(matrix.Input.Status) &&
               ReadyStatuses.Contains(matrix.Lane.Status) &&
               ReadyStatuses.Contains(matrix.Transition.Status) &&
               ReadyStatuses.Contains(matrix.Count.Status) &&
               ReadyStatuses.Contains(matrix.Presentation.Status) &&
               ReadyStatuses.Contains(matrix.Visibility.Status) &&
               ReadyStatuses.Contains(matrix.InputActivation.Status) &&
               ReadyStatuses.Contains(matrix.Persistence.Status);
    }

    private static void ValidateFingerprints(
        IReadOnlyList<PcCompatAdapterAssemblyFingerprint> assemblies,
        ICollection<string> errors)
    {
        ValidateUnique(assemblies.Select(value => value.AssemblyName), "assemblies", errors);
        foreach (var assembly in assemblies) {
            RequireId(assembly.AssemblyName, "assemblies[].assemblyName", errors);
            RequireSha256(assembly.Sha256, $"assembly '{assembly.AssemblyName}' sha256", errors);
            if (!Guid.TryParse(assembly.Mvid, out _))
                errors.Add($"assembly '{assembly.AssemblyName}' mvid: invalid GUID.");
        }
    }

    private static void ValidateFeature(
        PcCompatKeyViewerFeatureAdapter feature,
        ICollection<string> errors)
    {
        var path = $"feature '{feature.Id}'";
        RequireId(feature.Id, "feature id", errors);
        RequireId(feature.DisplayName, $"{path} displayName", errors);
        ValidateUnique(feature.SourceProfiles.Select(value => value.Id), $"{path} sourceProfiles", errors);
        ValidateUnique(feature.LaneGroups.Select(value => value.Id), $"{path} laneGroups", errors);
        ValidateUnique(
            feature.Roles.Select(value => value.Role + "!" +
                PcCompatKeyViewerOverrideStore.GetCandidateKey(
                    value.AssemblyName,
                    value.TypeName,
                    value.MemberName,
                    value.MemberKind)),
            $"{path} role candidates",
            errors);
        ValidateUnique(
            feature.IdentityTransforms.Select(value => value.CandidateKey),
            $"{path} identityTransforms",
            errors);
        if (feature.SourceProfiles.Count == 0)
            errors.Add($"{path}: at least one source profile is required.");
        if (feature.LaneGroups.Count == 0)
            errors.Add($"{path}: at least one lane group is required.");

        var sourceProfileIds = feature.SourceProfiles
            .Select(value => value.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var profile in feature.SourceProfiles) {
            RequireId(profile.Id, $"{path} source profile id", errors);
            if (profile.EntryPoints.Count == 0)
                errors.Add($"{path} source profile '{profile.Id}': no entry points.");
            ValidateEvidence(profile.Evidence, $"{path} source profile '{profile.Id}'", errors);
        }
        foreach (var transform in feature.IdentityTransforms)
        {
            RequireId(transform.CandidateKey, $"{path} identity transform candidate", errors);
            ValidateEvidence(
                transform.Evidence,
                $"{path} identity transform '{transform.CandidateKey}'",
                errors);
            if (!feature.Roles.Any(role =>
                    role.Role == "IdentityTransform" &&
                    PcCompatKeyViewerOverrideStore.GetCandidateKey(
                        role.AssemblyName,
                        role.TypeName,
                        role.MemberName,
                        role.MemberKind) == transform.CandidateKey))
            {
                errors.Add(
                    $"{path} identity transform '{transform.CandidateKey}': role candidate is missing.");
            }
        }
        foreach (var group in feature.LaneGroups)
            ValidateLaneGroup(group, sourceProfileIds, path, errors);
        foreach (var role in feature.Roles) {
            RequireId(role.Role, $"{path} role", errors);
            RequireId(role.AssemblyName, $"{path} role '{role.Role}' assembly", errors);
            RequireId(role.TypeName, $"{path} role '{role.Role}' type", errors);
            if (role.ConsumerLaneBase < 0)
                errors.Add($"{path} role '{role.Role}' consumerLaneBase: must not be negative.");
            ValidateEvidence(role.Evidence, $"{path} role '{role.Role}'", errors);
        }
        ValidateEvidenceMatrix(feature.Capabilities, path, errors);
        ValidateEvidence(feature.Visibility.Evidence, $"{path} visibility", errors);
        ValidateEvidence(feature.InputActivation.Evidence, $"{path} inputActivation", errors);
        RequireId(feature.Visibility.Kind, $"{path} visibility kind", errors);
        RequireId(feature.Visibility.Expression, $"{path} visibility expression", errors);
        RequireId(feature.InputActivation.Kind, $"{path} inputActivation kind", errors);
        RequireId(feature.InputActivation.Expression, $"{path} inputActivation expression", errors);
        RequireId(feature.CountSemantics.Clock, $"{path} count clock", errors);
        RequireId(feature.CountSemantics.ResetEntryPoint, $"{path} reset entry point", errors);
        if (feature.CountSemantics.KpsWindowMilliseconds <= 0)
            errors.Add($"{path} kpsWindowMilliseconds: must be positive.");
    }

    private static void ValidateLaneGroup(
        PcCompatKeyViewerLaneGroup group,
        IReadOnlySet<string> sourceProfileIds,
        string featurePath,
        ICollection<string> errors)
    {
        var path = $"{featurePath} lane group '{group.Id}'";
        RequireId(group.Id, $"{featurePath} lane group id", errors);
        ValidateUnique(group.Lanes.Select(value => value.Id), path, errors);
        if (group.Lanes.Count == 0)
            errors.Add($"{path}: at least one lane is required.");
        foreach (var lane in group.Lanes) {
            RequireId(lane.Id, $"{path} lane id", errors);
            RequireId(lane.DisplayLabel, $"{path} lane '{lane.Id}' displayLabel", errors);
            ValidateBinding(lane.Binding, sourceProfileIds, $"{path} lane '{lane.Id}'", errors);
        }
    }

    private static void ValidateBinding(
        PcCompatLaneBinding binding,
        IReadOnlySet<string> sourceProfileIds,
        string path,
        ICollection<string> errors)
    {
        if (binding.SourceProfileId is { Length: > 0 } profileId &&
            !sourceProfileIds.Contains(profileId)) {
            errors.Add($"{path}: source profile '{profileId}' does not exist.");
        }
        switch (binding.Kind) {
            case PcCompatLaneBindingKind.DirectIdentity when binding.Identities.Count != 1:
                errors.Add($"{path}: DirectIdentity requires exactly one identity.");
                break;
            case PcCompatLaneBindingKind.AliasSet when binding.Identities.Count < 2:
                errors.Add($"{path}: AliasSet requires at least two identities.");
                break;
            case PcCompatLaneBindingKind.TouchLane:
                if (binding.TouchLane is not (>= 1 and <= 10))
                    errors.Add($"{path}: TouchLane must be in [1, 10].");
                if (binding.Identities.Count != 0)
                    errors.Add($"{path}: TouchLane cannot also declare identities.");
                break;
        }
        foreach (var identity in binding.Identities)
            RequireId(identity.Value, $"{path} identity", errors);
    }

    private static void ValidateEvidenceMatrix(
        PcCompatKeyViewerEvidenceMatrix matrix,
        string path,
        ICollection<string> errors)
    {
        ValidateEvidence(matrix.Input, $"{path} capability input", errors);
        ValidateEvidence(matrix.Lane, $"{path} capability lane", errors);
        ValidateEvidence(matrix.Transition, $"{path} capability transition", errors);
        ValidateEvidence(matrix.Count, $"{path} capability count", errors);
        ValidateEvidence(matrix.Kps, $"{path} capability kps", errors);
        ValidateEvidence(matrix.Rain, $"{path} capability rain", errors);
        ValidateEvidence(matrix.Presentation, $"{path} capability presentation", errors);
        ValidateEvidence(matrix.Visibility, $"{path} capability visibility", errors);
        ValidateEvidence(matrix.InputActivation, $"{path} capability inputActivation", errors);
        ValidateEvidence(matrix.Settings, $"{path} capability settings", errors);
        ValidateEvidence(matrix.Persistence, $"{path} capability persistence", errors);
    }

    private static void ValidateEvidence(
        PcCompatAdapterEvidence evidence,
        string path,
        ICollection<string> errors)
    {
        if (evidence.Status == PcCompatAdapterEvidenceStatus.Proven &&
            evidence.Evidence.Count == 0) {
            errors.Add($"{path}: Proven status requires evidence.");
        }
        if (evidence.Status is PcCompatAdapterEvidenceStatus.Probable or
            PcCompatAdapterEvidenceStatus.Ambiguous &&
            string.IsNullOrWhiteSpace(evidence.FirstBreak)) {
            errors.Add($"{path}: unresolved status requires firstBreak.");
        }
        if (evidence.UserConfirmed && string.IsNullOrWhiteSpace(evidence.SelectedCandidate))
            errors.Add($"{path}: userConfirmed requires selectedCandidate.");
    }

    private static void ValidateContext(
        PcCompatKeyViewerAdapterDocument document,
        PcCompatKeyViewerAdapterValidationContext? context,
        ICollection<string> errors)
    {
        if (context is null)
            return;
        if (context.PackageSha256 is { } packageSha &&
            !string.Equals(packageSha, document.PackageSha256, StringComparison.OrdinalIgnoreCase)) {
            errors.Add("context: package SHA-256 changed; adapter must be revalidated.");
        }
        if (context.TargetGameRevision is { } revision && revision != document.TargetGameRevision)
            errors.Add("context: target game revision changed; adapter must be revalidated.");
        if (context.ProxySurfaceHash is { } proxyHash &&
            !string.Equals(proxyHash, document.ProxySurfaceHash, StringComparison.OrdinalIgnoreCase)) {
            errors.Add("context: proxy surface changed; adapter must be revalidated.");
        }
        foreach (var assembly in document.Assemblies) {
            if (context.AssemblyMvids.TryGetValue(assembly.AssemblyName, out var currentMvid) &&
                !string.Equals(currentMvid, assembly.Mvid, StringComparison.OrdinalIgnoreCase)) {
                errors.Add($"context: assembly '{assembly.AssemblyName}' MVID changed; adapter must be revalidated.");
            }
        }
    }

    private static void ValidateUnique(
        IEnumerable<string> values,
        string path,
        ICollection<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values) {
            if (!seen.Add(value))
                errors.Add($"{path}: duplicate id '{value}'.");
        }
    }

    private static void RequireId(string? value, string path, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"{path}: value is required.");
    }

    private static void RequireSha256(string? value, string path, ICollection<string> errors)
    {
        if (value is null || value.Length != 64 || value.Any(ch => !Uri.IsHexDigit(ch)))
            errors.Add($"{path}: expected 64 hexadecimal characters.");
    }
}

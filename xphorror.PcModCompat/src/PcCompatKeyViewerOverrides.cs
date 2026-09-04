using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xphorror.PcModCompat;

public enum PcCompatKeyViewerInputMode
{
    Auto,
    Touch,
    External,
    Hybrid
}

public enum PcCompatKeyViewerInputOrigin
{
    Unavailable,
    AsyncInput,
    OfficialActivity,
    ReplayVirtual
}

public sealed class PcCompatKeyViewerRoleOverride
{
    public required string Role { get; init; }
    public required string AssemblyName { get; init; }
    public required string TypeName { get; init; }
    public string? MemberName { get; init; }
    public string? MemberKind { get; init; }

    public string CandidateKey
        => PcCompatKeyViewerOverrideStore.GetCandidateKey(
            AssemblyName,
            TypeName,
            MemberName,
            MemberKind);
}

public sealed class PcCompatKeyViewerFeatureOverride
{
    public required string FeatureId { get; init; }
    public bool Enabled { get; set; }
    public PcCompatKeyViewerInputMode InputMode { get; set; } = PcCompatKeyViewerInputMode.Auto;
    public int TouchLaneCount { get; set; } = 10;
    public bool CompatibleFallbackEnabled { get; set; }
    public List<PcCompatKeyViewerRoleOverride> Roles { get; init; } = [];

    public void Normalize()
    {
        TouchLaneCount = TouchLaneCount is 2 or 4 or 6 or 8 or 10
            ? TouchLaneCount
            : 10;
        Roles.RemoveAll(role => string.IsNullOrWhiteSpace(role.Role) ||
                                string.IsNullOrWhiteSpace(role.AssemblyName) ||
                                string.IsNullOrWhiteSpace(role.TypeName));
    }
}

public sealed class PcCompatKeyViewerOverrideDocument
{
    public const string CurrentFormatVersion = "keyviewer-overrides-v1";

    public string FormatVersion { get; init; } = CurrentFormatVersion;
    public required string ModId { get; init; }
    public required string PackageSha256 { get; init; }
    public int TargetGameRevision { get; init; }
    public required string ProxySurfaceHash { get; init; }
    public IReadOnlyList<PcCompatAdapterAssemblyFingerprint> Assemblies { get; init; } =
        Array.Empty<PcCompatAdapterAssemblyFingerprint>();
    public List<PcCompatKeyViewerFeatureOverride> Features { get; init; } = [];

    public string ToJson()
        => JsonSerializer.Serialize(this, JsonOptions);

    public static PcCompatKeyViewerOverrideDocument? FromJson(string json)
        => JsonSerializer.Deserialize<PcCompatKeyViewerOverrideDocument>(json, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed class PcCompatKeyViewerOverrideValidationResult
{
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public bool IsValid => Errors.Count == 0;
}

public sealed class PcCompatKeyViewerInputProjection
{
    public required string FeatureId { get; init; }
    public required PcCompatKeyViewerInputMode Mode { get; init; }
    public required PcCompatKeyViewerInputOrigin Origin { get; init; }
    public required int LaneCount { get; init; }
    public required uint HeldMask { get; init; }
    public required uint LastDownMask { get; init; }
    public required uint LastUpMask { get; init; }
    public required uint SourceGeneration { get; init; }
    public required ulong SourceSequence { get; init; }
    public required bool IsTouchIdentity { get; init; }
}

public static class PcCompatKeyViewerOverrideStore
{
    private const string SettingsDirectoryName = ".pccompat";
    private const string SettingsFileName = "keyviewer_overrides.json";

    public static string GetPath(string modFolderPath)
        => Path.Combine(modFolderPath, SettingsDirectoryName, SettingsFileName);

    public static PcCompatKeyViewerOverrideDocument? Load(
        string modFolderPath,
        out string? error)
    {
        error = null;
        var path = GetPath(modFolderPath);
        if (!File.Exists(path))
            return null;

        try
        {
            var document = PcCompatKeyViewerOverrideDocument.FromJson(
                File.ReadAllText(path));
            if (document == null)
                throw new InvalidDataException("KeyViewer override JSON is empty.");
            return document;
        }
        catch (Exception exception) when (exception is IOException or
                                           InvalidDataException or
                                           JsonException)
        {
            error = $"{exception.GetType().Name}: {exception.Message}";
            return null;
        }
    }

    public static void Save(
        string modFolderPath,
        PcCompatKeyViewerOverrideDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        foreach (var feature in document.Features)
            feature.Normalize();

        var path = GetPath(modFolderPath);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, document.ToJson());
        File.Move(temporary, path, overwrite: true);
    }

    public static PcCompatKeyViewerOverrideDocument CreateFor(
        PcCompatKeyViewerAdapterDocument adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return new PcCompatKeyViewerOverrideDocument
        {
            ModId = adapter.ModId,
            PackageSha256 = adapter.PackageSha256,
            TargetGameRevision = adapter.TargetGameRevision,
            ProxySurfaceHash = adapter.ProxySurfaceHash,
            Assemblies = adapter.Assemblies.ToArray(),
            Features = adapter.Features
                .Select(feature => new PcCompatKeyViewerFeatureOverride
                {
                    FeatureId = feature.Id,
                    Enabled = false,
                    InputMode = PcCompatKeyViewerInputMode.Auto,
                    TouchLaneCount = 10
                })
                .ToList()
        };
    }

    public static PcCompatKeyViewerOverrideDocument CreateRecommendedFor(
        PcCompatKeyViewerAdapterDocument adapter)
    {
        var document = CreateFor(adapter);
        foreach (var featureOverride in document.Features)
        {
            var feature = adapter.Features.Single(candidate =>
                string.Equals(candidate.Id, featureOverride.FeatureId, StringComparison.Ordinal));
            featureOverride.Enabled = SupportsAutomaticInput(feature);
        }
        return document;
    }

    public static bool SupportsAutomaticInput(PcCompatKeyViewerFeatureAdapter feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return feature.Capabilities.Input.Status == PcCompatAdapterEvidenceStatus.Proven &&
               feature.Roles.Any(role => role.Role == "BindingProvider") &&
               feature.IdentityTransforms.Count(transform =>
                   transform.Evidence.Status == PcCompatAdapterEvidenceStatus.Proven &&
                   feature.Roles.Any(role =>
                       role.Role == "IdentityTransform" &&
                       role.Evidence.Status == PcCompatAdapterEvidenceStatus.Proven &&
                       string.Equals(
                           GetCandidateKey(
                               role.AssemblyName,
                               role.TypeName,
                               role.MemberName,
                               role.MemberKind),
                           transform.CandidateKey,
                           StringComparison.Ordinal))) == 1;
    }

    public static PcCompatKeyViewerOverrideValidationResult Validate(
        PcCompatKeyViewerOverrideDocument document,
        PcCompatKeyViewerAdapterDocument adapter)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(adapter);
        var errors = new List<string>();
        if (document.FormatVersion != PcCompatKeyViewerOverrideDocument.CurrentFormatVersion)
            errors.Add($"formatVersion: unsupported value '{document.FormatVersion}'.");
        if (!string.Equals(document.ModId, adapter.ModId, StringComparison.OrdinalIgnoreCase))
            errors.Add("modId: override belongs to another MOD.");
        if (!string.Equals(document.PackageSha256, adapter.PackageSha256, StringComparison.OrdinalIgnoreCase))
            errors.Add("packageSha256: adapter package changed; override is stale.");
        if (document.TargetGameRevision != adapter.TargetGameRevision)
            errors.Add("targetGameRevision: game revision changed; override is stale.");
        if (!string.Equals(document.ProxySurfaceHash, adapter.ProxySurfaceHash, StringComparison.OrdinalIgnoreCase))
            errors.Add("proxySurfaceHash: proxy surface changed; override is stale.");

        var documentAssemblies = document.Assemblies ??
            Array.Empty<PcCompatAdapterAssemblyFingerprint>();
        var adapterAssemblies = adapter.Assemblies.ToDictionary(
            assembly => assembly.AssemblyName,
            StringComparer.OrdinalIgnoreCase);
        if (documentAssemblies.Count != adapterAssemblies.Count)
            errors.Add("assemblies: fingerprint set is incomplete; override is stale.");
        foreach (var assembly in documentAssemblies)
        {
            if (!adapterAssemblies.TryGetValue(assembly.AssemblyName, out var current))
            {
                errors.Add($"assembly '{assembly.AssemblyName}': no longer exists.");
                continue;
            }
            if (!string.Equals(assembly.Sha256, current.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(assembly.Mvid, current.Mvid, StringComparison.OrdinalIgnoreCase))
                errors.Add($"assembly '{assembly.AssemblyName}': fingerprint changed; override is stale.");
        }

        var features = adapter.Features.ToDictionary(
            feature => feature.Id,
            StringComparer.Ordinal);
        var seenFeatureIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var featureOverride in document.Features ?? [])
        {
            if (!seenFeatureIds.Add(featureOverride.FeatureId))
                errors.Add($"feature '{featureOverride.FeatureId}': duplicate override.");
            if (!features.TryGetValue(featureOverride.FeatureId, out var feature))
            {
                errors.Add($"feature '{featureOverride.FeatureId}': no longer exists.");
                continue;
            }
            ValidateFeatureOverride(featureOverride, feature, errors);
        }

        return new PcCompatKeyViewerOverrideValidationResult { Errors = errors };
    }

    public static bool TryRebase(
        PcCompatKeyViewerOverrideDocument stale,
        PcCompatKeyViewerAdapterDocument adapter,
        out PcCompatKeyViewerOverrideDocument? rebased,
        out string summary)
    {
        ArgumentNullException.ThrowIfNull(stale);
        ArgumentNullException.ThrowIfNull(adapter);
        rebased = null;

        if (!string.Equals(
                stale.FormatVersion,
                PcCompatKeyViewerOverrideDocument.CurrentFormatVersion,
                StringComparison.Ordinal))
        {
            summary = $"unsupported override format '{stale.FormatVersion}'";
            return false;
        }
        if (!string.Equals(stale.ModId, adapter.ModId, StringComparison.OrdinalIgnoreCase))
        {
            summary = "override belongs to another MOD";
            return false;
        }
        var adapterValidation = PcCompatKeyViewerAdapterValidator.Validate(adapter);
        if (!adapterValidation.IsValid)
        {
            summary = "current adapter is invalid: " +
                      string.Join("; ", adapterValidation.Errors.Take(3));
            return false;
        }

        var staleFeatures = stale.Features ?? [];
        var duplicateFeature = staleFeatures
            .GroupBy(feature => feature.FeatureId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicateFeature != null)
        {
            summary = $"duplicate feature override '{duplicateFeature.Key}'";
            return false;
        }

        var staleById = staleFeatures.ToDictionary(
            feature => feature.FeatureId,
            StringComparer.Ordinal);
        var current = CreateRecommendedFor(adapter);
        var retainedFeatures = 0;
        var retainedRoles = 0;
        var droppedRoles = staleFeatures
            .Where(feature => !adapter.Features.Any(candidate =>
                string.Equals(candidate.Id, feature.FeatureId, StringComparison.Ordinal)))
            .Sum(feature => feature.Roles?.Count ?? 0);

        foreach (var currentFeature in current.Features)
        {
            if (!staleById.TryGetValue(currentFeature.FeatureId, out var previous))
                continue;
            var adapterFeature = adapter.Features.Single(feature =>
                string.Equals(feature.Id, currentFeature.FeatureId, StringComparison.Ordinal));
            retainedFeatures++;
            currentFeature.Enabled = previous.Enabled;
            currentFeature.InputMode = Enum.IsDefined(previous.InputMode)
                ? previous.InputMode
                : PcCompatKeyViewerInputMode.Auto;
            currentFeature.TouchLaneCount = previous.TouchLaneCount is 2 or 4 or 6 or 8 or 10
                ? previous.TouchLaneCount
                : currentFeature.TouchLaneCount;
            currentFeature.CompatibleFallbackEnabled = previous.CompatibleFallbackEnabled;

            var retainedRoleNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var role in previous.Roles ?? [])
            {
                var candidateStillExists = adapterFeature.Roles.Any(candidate =>
                    string.Equals(candidate.Role, role.Role, StringComparison.Ordinal) &&
                    string.Equals(
                        GetCandidateKey(
                            candidate.AssemblyName,
                            candidate.TypeName,
                            candidate.MemberName,
                            candidate.MemberKind),
                        role.CandidateKey,
                        StringComparison.Ordinal));
                if (!candidateStillExists || !retainedRoleNames.Add(role.Role))
                {
                    droppedRoles++;
                    continue;
                }
                currentFeature.Roles.Add(new PcCompatKeyViewerRoleOverride
                {
                    Role = role.Role,
                    AssemblyName = role.AssemblyName,
                    TypeName = role.TypeName,
                    MemberName = role.MemberName,
                    MemberKind = role.MemberKind
                });
                retainedRoles++;
            }
            currentFeature.Normalize();
        }

        var validation = Validate(current, adapter);
        if (!validation.IsValid)
        {
            summary = "rebased override is invalid: " +
                      string.Join("; ", validation.Errors.Take(3));
            return false;
        }

        rebased = current;
        summary = $"retainedFeatures={retainedFeatures} retainedRoles={retainedRoles} " +
                  $"droppedRoles={droppedRoles} newFeatures={current.Features.Count - retainedFeatures}";
        return true;
    }

    public static string GetCandidateKey(
        string assemblyName,
        string typeName,
        string? memberName,
        string? memberKind)
        => string.Join("!", assemblyName, typeName, memberKind ?? "", memberName ?? "");

    public static bool HasConfirmedRole(
        PcCompatKeyViewerFeatureOverride featureOverride,
        PcCompatKeyViewerRoleBinding role)
        => featureOverride.Roles.Any(candidate =>
            string.Equals(candidate.Role, role.Role, StringComparison.Ordinal) &&
            string.Equals(candidate.CandidateKey, GetCandidateKey(
                role.AssemblyName,
                role.TypeName,
                role.MemberName,
                role.MemberKind), StringComparison.Ordinal));

    public static PcCompatKeyViewerRoleOverride? ResolveSelectedOrUniqueRole(
        PcCompatKeyViewerFeatureAdapter feature,
        PcCompatKeyViewerFeatureOverride featureOverride,
        string roleName)
    {
        ArgumentNullException.ThrowIfNull(feature);
        ArgumentNullException.ThrowIfNull(featureOverride);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleName);

        var selected = featureOverride.Roles.SingleOrDefault(role =>
            string.Equals(role.Role, roleName, StringComparison.Ordinal));
        if (selected != null)
            return selected;
        var candidates = feature.Roles
            .Where(role => string.Equals(role.Role, roleName, StringComparison.Ordinal))
            .DistinctBy(role => GetCandidateKey(
                role.AssemblyName,
                role.TypeName,
                role.MemberName,
                role.MemberKind))
            .ToArray();
        if (candidates.Length != 1)
            return null;
        var candidate = candidates[0];
        return new PcCompatKeyViewerRoleOverride
        {
            Role = candidate.Role,
            AssemblyName = candidate.AssemblyName,
            TypeName = candidate.TypeName,
            MemberName = candidate.MemberName,
            MemberKind = candidate.MemberKind
        };
    }

    private static void ValidateFeatureOverride(
        PcCompatKeyViewerFeatureOverride featureOverride,
        PcCompatKeyViewerFeatureAdapter feature,
        ICollection<string> errors)
    {
        if (featureOverride.TouchLaneCount is not (2 or 4 or 6 or 8 or 10))
            errors.Add($"feature '{feature.Id}': touch lane count must be 2, 4, 6, 8 or 10.");
        var candidates = feature.Roles
            .GroupBy(role => role.Role, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var seenRoles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var roleOverride in featureOverride.Roles)
        {
            if (!seenRoles.Add(roleOverride.Role))
                errors.Add($"feature '{feature.Id}' role '{roleOverride.Role}': duplicate override.");
            if (!candidates.TryGetValue(roleOverride.Role, out var roleCandidates))
            {
                errors.Add($"feature '{feature.Id}' role '{roleOverride.Role}': candidate role does not exist.");
                continue;
            }
            var key = roleOverride.CandidateKey;
            if (!roleCandidates.Any(candidate => string.Equals(
                    GetCandidateKey(candidate.AssemblyName, candidate.TypeName,
                        candidate.MemberName, candidate.MemberKind), key,
                    StringComparison.Ordinal)))
                errors.Add($"feature '{feature.Id}' role '{roleOverride.Role}': selected candidate is not in the scan result.");
        }
    }
}

public static class PcCompatKeyViewerInputProjector
{
    public static PcCompatKeyViewerInputProjection ProjectTouch(
        PcCompatKeyViewerFeatureAdapter feature,
        PcCompatKeyViewerFeatureOverride featureOverride,
        PcCompatInputHudSnapshot snapshot,
        PcCompatKeyViewerInputOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(feature);
        ArgumentNullException.ThrowIfNull(featureOverride);
        ArgumentNullException.ThrowIfNull(snapshot);

        var group = feature.LaneGroups
            .FirstOrDefault(candidate => candidate.Lanes.Any(lane =>
                lane.Binding.Kind == PcCompatLaneBindingKind.TouchLane));
        var laneCount = Math.Clamp(featureOverride.TouchLaneCount, 2, 10);
        var sourceLaneMask = snapshot.TouchLaneHeldMask;
        var sourceDownMask = snapshot.TouchLaneLastDownMask;
        var sourceUpMask = snapshot.TouchLaneLastUpMask;
        uint heldMask = 0;
        uint downMask = 0;
        uint upMask = 0;

        if (group != null)
        {
            foreach (var (lane, index) in group.Lanes.Select((value, index) => (value, index)))
            {
                if (lane.Binding.TouchLane is not { } touchLane ||
                    touchLane < 1 || touchLane > 10 ||
                    index >= 32)
                    continue;
                var sourceBit = 1u << (touchLane - 1);
                var targetBit = 1u << index;
                if ((sourceLaneMask & sourceBit) != 0)
                    heldMask |= targetBit;
                if ((sourceDownMask & sourceBit) != 0)
                    downMask |= targetBit;
                if ((sourceUpMask & sourceBit) != 0)
                    upMask |= targetBit;
            }
            laneCount = Math.Clamp(group.Lanes.Count, 1, 10);
        }

        return new PcCompatKeyViewerInputProjection
        {
            FeatureId = feature.Id,
            Mode = featureOverride.InputMode,
            Origin = origin,
            LaneCount = laneCount,
            HeldMask = heldMask,
            LastDownMask = downMask,
            LastUpMask = upMask,
            SourceGeneration = snapshot.SourceGeneration,
            SourceSequence = snapshot.SourceSequence,
            IsTouchIdentity = true
        };
    }
}

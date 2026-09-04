namespace Xphorror.PcModCompat;

/// <summary>
/// The raw sequence the selected BindingProvider returned while a plan was being lowered, kept so
/// the configuration change watcher can baseline it without calling into MOD code a second time.
/// </summary>
public sealed class PcCompatKeyViewerResolvedProviderSequence
{
    public required string FeatureId { get; init; }
    public required PcCompatKeyViewerRoleOverride Role { get; init; }
    public required int RequiredCount { get; init; }
    public IReadOnlyList<int> Values { get; init; } = Array.Empty<int>();
}

public sealed class PcCompatKeyViewerBindingPlanLoweringResult
{
    public IReadOnlyList<PcCompatKeyViewerLoweredConsumerPlan> Plans { get; init; } =
        Array.Empty<PcCompatKeyViewerLoweredConsumerPlan>();
    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();
    public IReadOnlyList<PcCompatKeyViewerLoweredConsumerPlan> PresentationPlans { get; init; } =
        Array.Empty<PcCompatKeyViewerLoweredConsumerPlan>();
    public IReadOnlyList<string> PresentationIssues { get; init; } = Array.Empty<string>();

    /// <summary>
    /// One entry per feature that produced a plan, covering presentation-only features as well:
    /// their labels are rendered from the same sequence and go stale the same way.
    /// </summary>
    public IReadOnlyList<PcCompatKeyViewerResolvedProviderSequence> ResolvedProviders { get; init; } =
        Array.Empty<PcCompatKeyViewerResolvedProviderSequence>();
}

/// <summary>
/// Converts a verified runtime BindingProvider sequence into the immutable
/// canonical identities consumed by the owner-scoped legacy input bridge.
/// The lowerer is MOD-agnostic: activation requires an import-time proof of the
/// KeyCode/Win32 threshold transform plus the fingerprinted provider override.
/// </summary>
public static class PcCompatKeyViewerBindingPlanLowerer
{
    public const int UnityToWindowsThreshold = 0x1000;
    public const int MaximumWindowsVirtualKey = 0xFF;

    public static PcCompatKeyViewerBindingPlanLoweringResult Lower(
        PcCompatKeyViewerAdapterDocument adapter,
        PcCompatKeyViewerOverrideDocument overrides,
        Func<PcCompatKeyViewerRoleOverride, int, (bool Success, int[] Values, string? Error)>
            resolveProvider)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(resolveProvider);

        var adapterValidation = PcCompatKeyViewerAdapterValidator.Validate(adapter);
        var overrideValidation = PcCompatKeyViewerOverrideStore.Validate(overrides, adapter);
        if (!adapterValidation.IsValid || !overrideValidation.IsValid)
        {
            return new PcCompatKeyViewerBindingPlanLoweringResult
            {
                Issues = ["adapter or override document is invalid"]
            };
        }

        var plans = new List<PcCompatKeyViewerLoweredConsumerPlan>();
        var presentationPlans = new List<PcCompatKeyViewerLoweredConsumerPlan>();
        var resolvedProviders = new List<PcCompatKeyViewerResolvedProviderSequence>();
        var issues = new List<string>();
        var presentationIssues = new List<string>();
        foreach (var featureOverride in overrides.Features.Where(feature => feature.Enabled))
        {
            var consumerRequired = featureOverride.InputMode is
                PcCompatKeyViewerInputMode.Auto or
                PcCompatKeyViewerInputMode.Touch or
                PcCompatKeyViewerInputMode.Hybrid;
            var featureIssues = consumerRequired ? issues : presentationIssues;
            var feature = adapter.Features.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, featureOverride.FeatureId, StringComparison.Ordinal));
            if (feature == null)
                continue;

            var transforms = feature.IdentityTransforms.Where(transform =>
                transform.Evidence.Status == PcCompatAdapterEvidenceStatus.Proven &&
                feature.Roles.Any(role =>
                    role.Role == "IdentityTransform" &&
                    role.Evidence.Status == PcCompatAdapterEvidenceStatus.Proven &&
                    PcCompatKeyViewerOverrideStore.GetCandidateKey(
                        role.AssemblyName,
                        role.TypeName,
                        role.MemberName,
                        role.MemberKind) == transform.CandidateKey)).ToArray();
            if (transforms.Length != 1)
            {
                featureIssues.Add(
                    $"feature '{feature.Id}': expected one proven IdentityTransform, found {transforms.Length}");
                continue;
            }

            var providerCandidates = feature.Roles
                .Where(role => role.Role == "BindingProvider")
                .Select(role => new ProviderCandidate(
                    ToRoleOverride(role),
                    role.ConsumerLaneBase))
                .DistinctBy(candidate => candidate.Role.CandidateKey)
                .ToArray();
            var selectedProvider = featureOverride.Roles.SingleOrDefault(role =>
                role.Role == "BindingProvider");
            PcCompatKeyViewerRoleOverride provider;
            IReadOnlyList<PcCompatKeyViewerLoweredLaneBinding> lanes;
            int[] providerValues;
            if (selectedProvider == null)
            {
                var usableCandidates = providerCandidates
                    .Select(candidate =>
                    {
                        var usable = TryBuildLanes(
                            candidate.Role,
                            transforms[0],
                            featureOverride.TouchLaneCount,
                            resolveProvider,
                            out var candidateLanes,
                            out var candidateValues,
                            out var error);
                        return new UsableProviderCandidate(
                            candidate.Role,
                            candidate.ConsumerLaneBase,
                            usable,
                            candidateLanes,
                            candidateValues,
                            error);
                    })
                    .Where(candidate => candidate.Usable)
                    .ToArray();
                if (!TrySelectProvider(usableCandidates, out var selectedCandidate))
                {
                    featureIssues.Add(
                        $"feature '{feature.Id}': no confirmed BindingProvider; " +
                        $"found {usableCandidates.Length} usable candidates and no unique " +
                        "proven lane-base 0 provider");
                    continue;
                }
                provider = selectedCandidate.Role;
                lanes = selectedCandidate.Lanes;
                providerValues = selectedCandidate.Values;
            }
            else
            {
                provider = selectedProvider;
                if (!TryBuildLanes(
                        provider,
                        transforms[0],
                        featureOverride.TouchLaneCount,
                        resolveProvider,
                        out lanes,
                        out providerValues,
                        out var providerError))
                {
                    var alternatives = providerCandidates
                        .Where(candidate => !string.Equals(
                            candidate.Role.CandidateKey,
                            selectedProvider.CandidateKey,
                            StringComparison.Ordinal))
                        .Select(candidate =>
                        {
                            var usable = TryBuildLanes(
                                candidate.Role,
                                transforms[0],
                                featureOverride.TouchLaneCount,
                                resolveProvider,
                                out var candidateLanes,
                                out var candidateValues,
                                out var error);
                            return new UsableProviderCandidate(
                                candidate.Role,
                                candidate.ConsumerLaneBase,
                                usable,
                                candidateLanes,
                                candidateValues,
                                error);
                        })
                        .Where(candidate => candidate.Usable)
                        .ToArray();
                    if (!TrySelectProvider(alternatives, out var recoveredCandidate))
                    {
                        featureIssues.Add(
                            $"feature '{feature.Id}': selected BindingProvider is unusable " +
                            $"({providerError}); found {alternatives.Length} usable alternatives " +
                            "and no unique proven lane-base 0 provider");
                        continue;
                    }
                    provider = recoveredCandidate.Role;
                    lanes = recoveredCandidate.Lanes;
                    providerValues = recoveredCandidate.Values;
                    featureIssues.Add(
                        $"feature '{feature.Id}': selected BindingProvider is unusable " +
                        $"({providerError}); recovered with {provider.CandidateKey}");
                }
            }

            var plan = new PcCompatKeyViewerLoweredConsumerPlan
            {
                ModId = adapter.ModId,
                PackageSha256 = adapter.PackageSha256,
                ProxySurfaceHash = adapter.ProxySurfaceHash,
                TargetGameRevision = adapter.TargetGameRevision,
                FeatureId = feature.Id,
                BindingProviderCandidateKey = provider.CandidateKey,
                Lanes = lanes
            };
            presentationPlans.Add(plan);
            resolvedProviders.Add(new PcCompatKeyViewerResolvedProviderSequence
            {
                FeatureId = feature.Id,
                Role = provider,
                RequiredCount = featureOverride.TouchLaneCount,
                Values = providerValues
            });
            if (consumerRequired)
                plans.Add(plan);
        }

        return new PcCompatKeyViewerBindingPlanLoweringResult
        {
            Plans = plans,
            Issues = issues,
            PresentationPlans = presentationPlans,
            PresentationIssues = presentationIssues,
            ResolvedProviders = resolvedProviders
        };
    }

    private static bool TryBuildLanes(
        PcCompatKeyViewerRoleOverride provider,
        PcCompatKeyViewerIdentityTransform transform,
        int laneCount,
        Func<PcCompatKeyViewerRoleOverride, int, (bool Success, int[] Values, string? Error)>
            resolveProvider,
        out IReadOnlyList<PcCompatKeyViewerLoweredLaneBinding> lanes,
        out int[] values,
        out string? error)
    {
        var resolved = resolveProvider(provider, laneCount);
        values = resolved.Values ?? Array.Empty<int>();
        if (!resolved.Success)
        {
            lanes = Array.Empty<PcCompatKeyViewerLoweredLaneBinding>();
            error = "provider failed: " + (resolved.Error ?? "unknown error");
            return false;
        }
        if (values.Length < laneCount)
        {
            lanes = Array.Empty<PcCompatKeyViewerLoweredLaneBinding>();
            error = $"provider returned {values.Length} keys for {laneCount} touch lanes";
            return false;
        }

        var lowered = new List<PcCompatKeyViewerLoweredLaneBinding>(laneCount);
        for (var lane = 0; lane < laneCount; ++lane)
        {
            if (!TryLowerIdentity(transform, values[lane], out var identity, out error))
            {
                lanes = Array.Empty<PcCompatKeyViewerLoweredLaneBinding>();
                error = $"lane {lane + 1}: {error}";
                return false;
            }
            if (identity.Value == "0")
            {
                lanes = Array.Empty<PcCompatKeyViewerLoweredLaneBinding>();
                error = $"lane {lane + 1}: identity {identity.Kind}:0 is not a usable key";
                return false;
            }
            lowered.Add(new PcCompatKeyViewerLoweredLaneBinding
            {
                Lane = lane,
                Identities = [identity]
            });
        }

        lanes = lowered;
        error = null;
        return true;
    }

    private static bool TrySelectProvider(
        IReadOnlyList<UsableProviderCandidate> candidates,
        out UsableProviderCandidate selected)
    {
        if (candidates.Count == 1)
        {
            selected = candidates[0];
            return true;
        }

        var primary = candidates
            .Where(candidate => candidate.ConsumerLaneBase == 0)
            .ToArray();
        if (primary.Length == 1)
        {
            selected = primary[0];
            return true;
        }

        selected = null!;
        return false;
    }

    private sealed record ProviderCandidate(
        PcCompatKeyViewerRoleOverride Role,
        int? ConsumerLaneBase);

    private sealed record UsableProviderCandidate(
        PcCompatKeyViewerRoleOverride Role,
        int? ConsumerLaneBase,
        bool Usable,
        IReadOnlyList<PcCompatKeyViewerLoweredLaneBinding> Lanes,
        int[] Values,
        string? Error);

    private static PcCompatKeyViewerRoleOverride ToRoleOverride(
        PcCompatKeyViewerRoleBinding role)
        => new()
        {
            Role = role.Role,
            AssemblyName = role.AssemblyName,
            TypeName = role.TypeName,
            MemberName = role.MemberName,
            MemberKind = role.MemberKind
        };

    public static bool TryLowerIdentity(
        int configuredKeyCode,
        out PcCompatInputIdentity identity,
        out string? error)
        => TryLowerIdentity(
            new PcCompatKeyViewerIdentityTransform
            {
                CandidateKey = "legacy-threshold-split",
                Kind = PcCompatKeyViewerIdentityTransformKind.UnityWindowsThresholdSplit,
                Threshold = UnityToWindowsThreshold,
                Offset = UnityToWindowsThreshold,
                Evidence = new PcCompatAdapterEvidence
                {
                    Status = PcCompatAdapterEvidenceStatus.Proven
                }
            },
            configuredKeyCode,
            out identity,
            out error);

    public static bool TryLowerIdentity(
        PcCompatKeyViewerIdentityTransform transform,
        int configuredKeyCode,
        out PcCompatInputIdentity identity,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (transform.Kind == PcCompatKeyViewerIdentityTransformKind.UnityKeyCodeIdentity)
        {
            return TryCreateIdentity(
                PcCompatInputIdentityKind.UnityKeyCode,
                configuredKeyCode,
                0,
                511,
                "Unity KeyCode",
                out identity,
                out error);
        }
        if (transform.Kind == PcCompatKeyViewerIdentityTransformKind.WindowsVirtualKeyIdentity)
        {
            return TryCreateIdentity(
                PcCompatInputIdentityKind.WindowsVirtualKey,
                configuredKeyCode,
                0,
                MaximumWindowsVirtualKey,
                "Windows virtual key",
                out identity,
                out error);
        }
        if (transform.Kind == PcCompatKeyViewerIdentityTransformKind.WindowsVirtualKeyOffset)
        {
            return TryCreateIdentity(
                PcCompatInputIdentityKind.WindowsVirtualKey,
                configuredKeyCode - transform.Offset,
                0,
                MaximumWindowsVirtualKey,
                "offset Windows virtual key",
                out identity,
                out error);
        }
        if (transform.Kind == PcCompatKeyViewerIdentityTransformKind.UnityWindowsThresholdSplit)
        {
            return configuredKeyCode < transform.Threshold
                ? TryCreateIdentity(
                    PcCompatInputIdentityKind.UnityKeyCode,
                    configuredKeyCode,
                    0,
                    511,
                    "threshold Unity KeyCode",
                    out identity,
                    out error)
                : TryCreateIdentity(
                    PcCompatInputIdentityKind.WindowsVirtualKey,
                    configuredKeyCode - transform.Offset,
                    0,
                    MaximumWindowsVirtualKey,
                    "threshold Windows virtual key",
                    out identity,
                    out error);
        }

        identity = null!;
        error = $"identity transform {transform.Kind} is unsupported";
        return false;
    }

    private static bool TryCreateIdentity(
        PcCompatInputIdentityKind kind,
        int value,
        int minimum,
        int maximum,
        string domain,
        out PcCompatInputIdentity identity,
        out string? error)
    {
        if (value < minimum || value > maximum)
        {
            identity = null!;
            error = $"value {value} is outside the proven {domain} domain [{minimum}, {maximum}]";
            return false;
        }
        identity = new PcCompatInputIdentity
        {
            Kind = kind,
            Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        error = null;
        return true;
    }
}

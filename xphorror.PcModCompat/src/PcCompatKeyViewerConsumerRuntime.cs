using System.Collections.Concurrent;

namespace Xphorror.PcModCompat;

public enum PcCompatKeyViewerConsumerQualification
{
    None,
    ProvenAdapter,
    VerifiedLoweredBinding
}

public readonly record struct PcCompatKeyViewerConsumerKeyState(
    PcCompatKeyViewerInputMode Mode,
    bool Held,
    ulong DownOrdinal,
    ulong UpOrdinal,
    ulong SourceSequence,
    uint SessionGeneration,
    uint ProducerEpoch,
    long RegistrationGeneration);

public sealed class PcCompatKeyViewerConsumerFeatureStatus
{
    public required string FeatureId { get; init; }
    public PcCompatKeyViewerConsumerQualification Qualification { get; init; }
    public bool Active { get; init; }
    public string? Reason { get; init; }
    public int MappedIdentityCount { get; init; }
    public ulong PublishedSequence { get; init; }
}

public sealed class PcCompatKeyViewerConsumerSnapshot
{
    public static PcCompatKeyViewerConsumerSnapshot Unregistered { get; } = new();

    public bool Registered { get; init; }
    public ulong PublishedSequence { get; init; }
    public IReadOnlyList<PcCompatKeyViewerConsumerFeatureStatus> Features { get; init; } =
        Array.Empty<PcCompatKeyViewerConsumerFeatureStatus>();
}

public sealed class PcCompatKeyViewerLoweredLaneBinding
{
    public int Lane { get; init; }
    public IReadOnlyList<PcCompatInputIdentity> Identities { get; init; } =
        Array.Empty<PcCompatInputIdentity>();
}

public sealed class PcCompatKeyViewerLoweredConsumerPlan
{
    public required string ModId { get; init; }
    public required string PackageSha256 { get; init; }
    public required string ProxySurfaceHash { get; init; }
    public int TargetGameRevision { get; init; }
    public required string FeatureId { get; init; }
    public required string BindingProviderCandidateKey { get; init; }
    public IReadOnlyList<PcCompatKeyViewerLoweredLaneBinding> Lanes { get; init; } =
        Array.Empty<PcCompatKeyViewerLoweredLaneBinding>();
}

/// <summary>
/// Session-local output of the verified BindingProvider lowerer. A UI role
/// selection or automatic candidate alone cannot create a plan; the translator
/// must publish exact canonical identities for every lane.
/// </summary>
public static class PcCompatKeyViewerLoweredConsumerPlanRegistry
{
    private static readonly ConcurrentDictionary<string, CompiledPlan> Plans =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool Register(
        PcCompatKeyViewerAdapterDocument adapter,
        PcCompatKeyViewerOverrideDocument overrides,
        PcCompatKeyViewerLoweredConsumerPlan plan,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(plan);
        error = Validate(adapter, overrides, plan, out var compiled);
        if (error != null)
            return false;
        Plans[Key(plan.ModId, plan.FeatureId)] = compiled!;
        PcCompatDeepDebug.Write(
            "consumer-plan",
            $"action=register mod={plan.ModId} feature={plan.FeatureId} " +
            $"provider={PcCompatDeepDebug.Sanitize(plan.BindingProviderCandidateKey)} " +
            $"lanes={compiled!.LaneCount} identities=[{string.Join(',', compiled.Identities.Select(identity =>
                $"{identity.Kind}:{identity.Value}->lane{identity.Lane}"))}]");
        return true;
    }

    public static void Remove(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
            return;
        foreach (var key in Plans.Keys.Where(key => key.StartsWith(
                     modId + "\0",
                     StringComparison.OrdinalIgnoreCase)))
            Plans.TryRemove(key, out _);
        PcCompatDeepDebug.Write("consumer-plan", $"action=remove mod={modId}");
    }

    internal static bool TryGet(
        string modId,
        string featureId,
        PcCompatKeyViewerFeatureOverride featureOverride,
        out PcCompatKeyViewerPublishedIdentity[] identities)
    {
        if (Plans.TryGetValue(Key(modId, featureId), out var plan) &&
            featureOverride.Enabled &&
            featureOverride.TouchLaneCount == plan.LaneCount &&
            featureOverride.InputMode is PcCompatKeyViewerInputMode.Auto or
                PcCompatKeyViewerInputMode.Touch or PcCompatKeyViewerInputMode.Hybrid)
        {
            identities = plan.Identities;
            return true;
        }
        identities = Array.Empty<PcCompatKeyViewerPublishedIdentity>();
        return false;
    }

    private static string? Validate(
        PcCompatKeyViewerAdapterDocument adapter,
        PcCompatKeyViewerOverrideDocument overrides,
        PcCompatKeyViewerLoweredConsumerPlan plan,
        out CompiledPlan? compiled)
    {
        compiled = null;
        var adapterValidation = PcCompatKeyViewerAdapterValidator.Validate(adapter);
        var overrideValidation = PcCompatKeyViewerOverrideStore.Validate(overrides, adapter);
        if (!adapterValidation.IsValid || !overrideValidation.IsValid)
            return "adapter or override document is invalid";
        if (!string.Equals(plan.ModId, adapter.ModId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(plan.ModId, overrides.ModId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(plan.PackageSha256, adapter.PackageSha256, StringComparison.Ordinal) ||
            !string.Equals(plan.ProxySurfaceHash, adapter.ProxySurfaceHash, StringComparison.Ordinal) ||
            plan.TargetGameRevision != adapter.TargetGameRevision)
            return "lowered consumer plan fingerprint does not match the Adapter";

        var feature = adapter.Features.FirstOrDefault(value => value.Id == plan.FeatureId);
        var featureOverride = overrides.Features.FirstOrDefault(value =>
            value.FeatureId == plan.FeatureId && value.Enabled);
        if (feature == null || featureOverride == null)
            return "lowered consumer plan feature is missing or disabled";
        if (featureOverride.InputMode is not (PcCompatKeyViewerInputMode.Auto or
            PcCompatKeyViewerInputMode.Touch or PcCompatKeyViewerInputMode.Hybrid))
            return "lowered consumer plan requires Auto, Touch or Hybrid input mode";
        var planProviderIsCandidate = feature.Roles.Any(value =>
            value.Role == "BindingProvider" &&
            string.Equals(
                PcCompatKeyViewerOverrideStore.GetCandidateKey(
                    value.AssemblyName,
                    value.TypeName,
                    value.MemberName,
                    value.MemberKind),
                plan.BindingProviderCandidateKey,
                StringComparison.Ordinal));
        if (!planProviderIsCandidate)
            return "lowered consumer plan provider is not in the Adapter candidate set";
        if (plan.Lanes.Count != featureOverride.TouchLaneCount)
            return "lowered consumer lane count does not match the Touch override";

        var identities = new List<PcCompatKeyViewerPublishedIdentity>();
        var seenLanes = new HashSet<int>();
        foreach (var lane in plan.Lanes)
        {
            if (lane.Lane < 0 || lane.Lane >= plan.Lanes.Count || !seenLanes.Add(lane.Lane))
                return "lowered consumer lane indices must be unique and contiguous";
            if (lane.Identities.Count == 0)
                return $"lowered consumer lane {lane.Lane} has no identity";
            foreach (var identity in lane.Identities)
            {
                if (identity.Kind is not (PcCompatInputIdentityKind.UnityKeyCode or
                    PcCompatInputIdentityKind.WindowsVirtualKey or
                    PcCompatInputIdentityKind.ActionId) ||
                    !int.TryParse(
                        identity.Value,
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var value))
                    return $"lowered consumer lane {lane.Lane} has an unsupported identity";
                identities.Add(new PcCompatKeyViewerPublishedIdentity(
                    identity.Kind,
                    value,
                    lane.Lane));
            }
        }
        if (seenLanes.Count != plan.Lanes.Count)
            return "lowered consumer lane indices are incomplete";
        compiled = new CompiledPlan(
            plan.BindingProviderCandidateKey,
            plan.Lanes.Count,
            identities.ToArray());
        return null;
    }

    private static string Key(string modId, string featureId)
        => modId + "\0" + featureId;

    private sealed record CompiledPlan(
        string BindingProviderCandidateKey,
        int LaneCount,
        PcCompatKeyViewerPublishedIdentity[] Identities);
}

/// <summary>
/// Immutable per-MOD query surface consumed by rewritten legacy input calls.
/// Actor workers publish state; the original MOD state machine reads it from its
/// verified managed execution context on UnityMain.
/// </summary>
public static class PcCompatKeyViewerConsumerRuntime
{
    private static readonly ConcurrentDictionary<string, PublishedModState> States =
        new(StringComparer.OrdinalIgnoreCase);

    public static PcCompatKeyViewerConsumerSnapshot Snapshot(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId) || !States.TryGetValue(modId, out var state))
            return PcCompatKeyViewerConsumerSnapshot.Unregistered;
        return state.Diagnostic;
    }

    public static string GetQuerySurfaceStatus(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId) || !States.TryGetValue(modId, out var state))
            return "unregistered";
        static string Format(IReadOnlyDictionary<int, PcCompatKeyViewerConsumerKeyState> values)
            => string.Join(',', values
                .OrderBy(pair => pair.Key)
                .Take(32)
                .Select(pair =>
                    $"{pair.Key}:h{(pair.Value.Held ? 1 : 0)}/d{pair.Value.DownOrdinal}/u{pair.Value.UpOrdinal}"));
        return $"unity=[{Format(state.UnityKeys)}]" +
               $" windows=[{Format(state.WindowsKeys)}]" +
               $" actions=[{Format(state.Actions)}]";
    }

    public static bool TryGetUnityKeyState(
        string modId,
        int keyCode,
        out PcCompatKeyViewerConsumerKeyState state)
        => TryGetState(modId, PcCompatInputIdentityKind.UnityKeyCode, keyCode, out state);

    public static bool TryGetWindowsVirtualKeyState(
        string modId,
        int virtualKey,
        out PcCompatKeyViewerConsumerKeyState state)
        => TryGetState(modId, PcCompatInputIdentityKind.WindowsVirtualKey, virtualKey, out state);

    public static bool TryGetActionState(
        string modId,
        int actionId,
        out PcCompatKeyViewerConsumerKeyState state)
        => TryGetState(modId, PcCompatInputIdentityKind.ActionId, actionId, out state);

    public static bool TryGetAnyUnityKeyDownState(
        string modId,
        out PcCompatKeyViewerInputMode mode,
        out ulong downOrdinal,
        out long registrationGeneration)
    {
        mode = PcCompatKeyViewerInputMode.External;
        downOrdinal = 0;
        registrationGeneration = 0;
        if (string.IsNullOrWhiteSpace(modId) || !States.TryGetValue(modId, out var published))
            return false;
        mode = published.AnyUnityMode;
        downOrdinal = published.AnyUnityDownOrdinal;
        registrationGeneration = published.AnyUnityRegistrationGeneration;
        return published.UnityKeys.Count != 0;
    }

    internal static void Publish(
        string modId,
        IReadOnlyList<PcCompatKeyViewerPublishedFeature> features,
        ulong sequence,
        ulong anyUnityDownOrdinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentNullException.ThrowIfNull(features);
        if (!features.Any(feature => feature.Active))
        {
            PcCompatDeepDebug.WriteState(
                "consumer-state",
                modId,
                "inactive:" + (features.Count == 0 ? 0 : features[0].RegistrationGeneration),
                $"action=inactive mod={modId} sequence={sequence} features={features.Count} " +
                $"registrationGeneration={(features.Count == 0 ? 0 : features[0].RegistrationGeneration)}");
            if (features.Count != 0)
                Remove(modId, features[0].RegistrationGeneration);
            return;
        }

        var activeFeatures = features.ToArray();
        var unityKeys = new Dictionary<int, PcCompatKeyViewerConsumerKeyState>();
        var windowsKeys = new Dictionary<int, PcCompatKeyViewerConsumerKeyState>();
        var actions = new Dictionary<int, PcCompatKeyViewerConsumerKeyState>();
        var anyMode = PcCompatKeyViewerInputMode.External;
        long anyGeneration = 0;
        foreach (var feature in activeFeatures.Where(feature => feature.Active))
        {
            var seen = new HashSet<(PcCompatInputIdentityKind Kind, int Value, int Lane)>();
            var unityLanes = new HashSet<int>();
            foreach (var identity in feature.Identities)
            {
                if (!seen.Add((identity.Kind, identity.Value, identity.Lane)))
                    continue;
                var keyState = new PcCompatKeyViewerConsumerKeyState(
                    feature.Mode,
                    (feature.HeldMask & (1u << identity.Lane)) != 0,
                    feature.DownOrdinals[identity.Lane],
                    feature.UpOrdinals[identity.Lane],
                    feature.SourceSequence,
                    feature.SessionGeneration,
                    feature.ProducerEpoch,
                    feature.RegistrationGeneration);
                var target = identity.Kind switch
                {
                    PcCompatInputIdentityKind.UnityKeyCode => unityKeys,
                    PcCompatInputIdentityKind.WindowsVirtualKey => windowsKeys,
                    PcCompatInputIdentityKind.ActionId => actions,
                    _ => null
                };
                if (target == null)
                    continue;
                target[identity.Value] = target.TryGetValue(identity.Value, out var current)
                    ? Merge(current, keyState)
                    : keyState;
                if (identity.Kind == PcCompatInputIdentityKind.UnityKeyCode)
                    unityLanes.Add(identity.Lane);
            }
            if (unityLanes.Count == 0)
                continue;
            anyMode = MergeMode(anyMode, feature.Mode);
            anyGeneration = Math.Max(anyGeneration, feature.RegistrationGeneration);
        }
        States[modId] = new PublishedModState(
            activeFeatures,
            activeFeatures.First(feature => feature.Active).RegistrationGeneration,
            unityKeys,
            windowsKeys,
            actions,
            anyMode,
            anyUnityDownOrdinal,
            anyGeneration,
            new PcCompatKeyViewerConsumerSnapshot
            {
                Registered = true,
                PublishedSequence = sequence,
                Features = features.Select(feature => new PcCompatKeyViewerConsumerFeatureStatus
                {
                    FeatureId = feature.FeatureId,
                    Qualification = feature.Qualification,
                    Active = feature.Active,
                    Reason = feature.Reason,
                    MappedIdentityCount = feature.Identities.Length,
                    PublishedSequence = feature.SourceSequence
                }).ToArray()
            });
        var stateIdentity = string.Join('|', activeFeatures.Select(feature =>
            feature.FeatureId + ":" + feature.Mode + ":" + feature.HeldMask + ":" +
            string.Join(',', feature.DownOrdinals) + ":" + string.Join(',', feature.UpOrdinals) + ":" +
            feature.SourceSequence + ":" + feature.RegistrationGeneration));
        PcCompatDeepDebug.WriteState(
            "consumer-state",
            modId,
            stateIdentity,
            $"action=publish mod={modId} sequence={sequence} anyUnityDown={anyUnityDownOrdinal} " +
            $"features=[{string.Join(" | ", activeFeatures.Select(DescribeFeature))}] " +
            $"unitySurface=[{FormatSurface(unityKeys)}] windowsSurface=[{FormatSurface(windowsKeys)}] " +
            $"actionSurface=[{FormatSurface(actions)}]");
    }

    internal static void Remove(string modId, long registrationGeneration)
    {
        if (string.IsNullOrWhiteSpace(modId) ||
            !States.TryGetValue(modId, out var state) ||
            state.RegistrationGeneration != registrationGeneration)
            return;
        ((ICollection<KeyValuePair<string, PublishedModState>>)States).Remove(
            new KeyValuePair<string, PublishedModState>(modId, state));
        PcCompatDeepDebug.Write(
            "consumer-state",
            $"action=remove mod={modId} registrationGeneration={registrationGeneration} " +
            $"publishedSequence={state.Diagnostic.PublishedSequence}");
    }

    private static string DescribeFeature(PcCompatKeyViewerPublishedFeature feature)
        => $"id={feature.FeatureId} active={feature.Active} qualification={feature.Qualification} " +
           $"mode={feature.Mode} held=0x{feature.HeldMask:X} sourceSequence={feature.SourceSequence} " +
           $"sessionGeneration={feature.SessionGeneration} producerEpoch={feature.ProducerEpoch} " +
           $"registrationGeneration={feature.RegistrationGeneration} " +
           $"down=[{string.Join(',', feature.DownOrdinals)}] up=[{string.Join(',', feature.UpOrdinals)}] " +
           $"identities=[{string.Join(',', feature.Identities.Select(identity =>
               $"{identity.Kind}:{identity.Value}->lane{identity.Lane}"))}]";

    private static string FormatSurface(
        IReadOnlyDictionary<int, PcCompatKeyViewerConsumerKeyState> surface)
        => string.Join(',', surface.OrderBy(pair => pair.Key).Select(pair =>
            $"{pair.Key}:mode={pair.Value.Mode}/held={pair.Value.Held}/" +
            $"down={pair.Value.DownOrdinal}/up={pair.Value.UpOrdinal}/" +
            $"seq={pair.Value.SourceSequence}/session={pair.Value.SessionGeneration}/" +
            $"epoch={pair.Value.ProducerEpoch}/reg={pair.Value.RegistrationGeneration}"));

    private static bool TryGetState(
        string modId,
        PcCompatInputIdentityKind kind,
        int value,
        out PcCompatKeyViewerConsumerKeyState state)
    {
        state = default;
        if (string.IsNullOrWhiteSpace(modId) || !States.TryGetValue(modId, out var published))
            return false;

        var source = kind == PcCompatInputIdentityKind.UnityKeyCode
            ? published.UnityKeys
            : kind == PcCompatInputIdentityKind.WindowsVirtualKey
                ? published.WindowsKeys
                : published.Actions;
        return source.TryGetValue(value, out state);
    }

    private static PcCompatKeyViewerInputMode MergeMode(
        PcCompatKeyViewerInputMode current,
        PcCompatKeyViewerInputMode next)
        => current == PcCompatKeyViewerInputMode.Hybrid ||
           next == PcCompatKeyViewerInputMode.Hybrid
            ? PcCompatKeyViewerInputMode.Hybrid
            : next;

    private static PcCompatKeyViewerConsumerKeyState Merge(
        PcCompatKeyViewerConsumerKeyState current,
        PcCompatKeyViewerConsumerKeyState next)
        => new(
            MergeMode(current.Mode, next.Mode),
            current.Held || next.Held,
            current.DownOrdinal + next.DownOrdinal,
            current.UpOrdinal + next.UpOrdinal,
            Math.Max(current.SourceSequence, next.SourceSequence),
            Math.Max(current.SessionGeneration, next.SessionGeneration),
            Math.Max(current.ProducerEpoch, next.ProducerEpoch),
            Math.Max(current.RegistrationGeneration, next.RegistrationGeneration));

    private sealed record PublishedModState(
        PcCompatKeyViewerPublishedFeature[] Features,
        long RegistrationGeneration,
        IReadOnlyDictionary<int, PcCompatKeyViewerConsumerKeyState> UnityKeys,
        IReadOnlyDictionary<int, PcCompatKeyViewerConsumerKeyState> WindowsKeys,
        IReadOnlyDictionary<int, PcCompatKeyViewerConsumerKeyState> Actions,
        PcCompatKeyViewerInputMode AnyUnityMode,
        ulong AnyUnityDownOrdinal,
        long AnyUnityRegistrationGeneration,
        PcCompatKeyViewerConsumerSnapshot Diagnostic);
}

internal readonly record struct PcCompatKeyViewerPublishedIdentity(
    PcCompatInputIdentityKind Kind,
    int Value,
    int Lane);

internal sealed class PcCompatKeyViewerPublishedFeature
{
    public required string FeatureId { get; init; }
    public PcCompatKeyViewerConsumerQualification Qualification { get; init; }
    public bool Active { get; init; }
    public string? Reason { get; init; }
    public PcCompatKeyViewerInputMode Mode { get; init; }
    public required PcCompatKeyViewerPublishedIdentity[] Identities { get; init; }
    public uint HeldMask { get; init; }
    public required ulong[] DownOrdinals { get; init; }
    public required ulong[] UpOrdinals { get; init; }
    public ulong SourceSequence { get; init; }
    public uint SessionGeneration { get; init; }
    public uint ProducerEpoch { get; init; }
    public long RegistrationGeneration { get; init; }
}

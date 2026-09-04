using System.Text;

namespace StArray.ModManager.Runtime;

internal enum ModOwnedResourceKind
{
    Hook,
    CodePatch,
    Symbol,
    Behaviour,
    NativeLibrary,
    Hud,
    Provider,
    Resource,
    UnityObject,
    AsyncOperation,
    NativeOperation,
    InputSubscription,
}

internal enum ModOwnedResourceRetirementPolicy
{
    MustRetire,
    RetainWhileSuspended,
    ObserveOnly,
}

internal enum ModOwnedResourceAuditIssueKind
{
    ConflictingRuntimeSnapshot,
    ActiveResourceWithoutRuntimeSession,
    ActiveResourceAfterTerminalState,
    MustRetireResourceWhileSuspended,
}

internal readonly record struct ModOwnedResourceSnapshot(
    long Sequence,
    ModRuntimeKey Key,
    ModOwnedResourceKind Kind,
    ModOwnedResourceRetirementPolicy RetirementPolicy,
    string Identity,
    bool Retired);

internal readonly record struct ModOwnedResourceAuditIssue(
    ModOwnedResourceAuditIssueKind Kind,
    ModRuntimeKey Key,
    int ResourceCount,
    IReadOnlyList<long> ResourceSequences,
    string Detail);

internal readonly record struct ModOwnedResourceGenerationAudit(
    ModRuntimeKey Key,
    bool RuntimeSessionKnown,
    ModRuntimeLifecycleState? RuntimeState,
    int ActiveCallbacks,
    int ActiveOperations,
    int ActiveResources,
    int RetiredResources,
    int MustRetireResources,
    int SuspendRetainedResources,
    int ObservedResources,
    IReadOnlyList<ModOwnedResourceSnapshot> Resources);

internal sealed record ModOwnedResourceAuditSnapshot(
    long LastSequence,
    int ActiveResources,
    int RetiredResources,
    IReadOnlyList<ModOwnedResourceGenerationAudit> Generations,
    IReadOnlyList<ModOwnedResourceAuditIssue> Issues)
{
    internal bool HasLeaks => Issues.Count != 0;

    internal void AssertNoLeaks()
    {
        if (!HasLeaks)
            return;
        throw new InvalidOperationException(
            $"MOD owned-resource audit found {Issues.Count} violation(s): " +
            string.Join(", ", Issues.Select(issue =>
                $"{issue.Kind}:{issue.Key.LoaderKind}/{issue.Key.ModId}/g{issue.Key.Generation}")));
    }

    internal string ToDiagnosticText(bool includeResources = true)
    {
        var builder = new StringBuilder();
        builder.Append("owned-resource-audit")
            .Append(" sequence=").Append(LastSequence)
            .Append(" generations=").Append(Generations.Count)
            .Append(" active=").Append(ActiveResources)
            .Append(" retired=").Append(RetiredResources)
            .Append(" issues=").Append(Issues.Count)
            .AppendLine();

        foreach (var generation in Generations)
        {
            builder.Append("generation")
                .Append(" loader=").Append(Sanitize(generation.Key.LoaderKind))
                .Append(" mod=").Append(Sanitize(generation.Key.ModId))
                .Append(" value=").Append(generation.Key.Generation)
                .Append(" runtime=").Append(generation.RuntimeSessionKnown
                    ? generation.RuntimeState?.ToString() ?? "unknown"
                    : "missing")
                .Append(" callbacks=").Append(generation.ActiveCallbacks)
                .Append(" operations=").Append(generation.ActiveOperations)
                .Append(" active=").Append(generation.ActiveResources)
                .Append(" retired=").Append(generation.RetiredResources)
                .Append(" mustRetire=").Append(generation.MustRetireResources)
                .Append(" suspendRetained=").Append(generation.SuspendRetainedResources)
                .Append(" observed=").Append(generation.ObservedResources)
                .AppendLine();

            if (!includeResources)
                continue;
            foreach (var resource in generation.Resources)
            {
                builder.Append("resource")
                    .Append(" sequence=").Append(resource.Sequence)
                    .Append(" kind=").Append(resource.Kind)
                    .Append(" policy=").Append(resource.RetirementPolicy)
                    .Append(" retired=").Append(resource.Retired ? 1 : 0)
                    .Append(" identity=").Append(Sanitize(resource.Identity))
                    .AppendLine();
            }
        }

        foreach (var issue in Issues)
        {
            builder.Append("issue")
                .Append(" kind=").Append(issue.Kind)
                .Append(" loader=").Append(Sanitize(issue.Key.LoaderKind))
                .Append(" mod=").Append(Sanitize(issue.Key.ModId))
                .Append(" generation=").Append(issue.Key.Generation)
                .Append(" resources=").Append(issue.ResourceCount)
                .Append(" sequences=").Append(issue.ResourceSequences.Count == 0
                    ? "none"
                    : string.Join(',', issue.ResourceSequences))
                .Append(" detail=").Append(Sanitize(issue.Detail))
                .AppendLine();
        }
        return builder.ToString();
    }

    private static string Sanitize(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ');
}

/// <summary>
/// Host-side ownership ledger for resources which can outlive a managed callback.
/// Registration and retirement are cold-path operations; frame dispatch keeps using
/// ModRuntimeSession and owner-specific fast paths.
/// </summary>
internal static class ModOwnedResourceRegistry
{
    private sealed class Entry(
        long sequence,
        ModRuntimeKey key,
        ModOwnedResourceKind kind,
        ModOwnedResourceRetirementPolicy retirementPolicy,
        string identity)
    {
        public long Sequence { get; } = sequence;
        public ModRuntimeKey Key { get; } = key;
        public ModOwnedResourceKind Kind { get; } = kind;
        public ModOwnedResourceRetirementPolicy RetirementPolicy { get; } = retirementPolicy;
        public string Identity { get; } = identity;
        public bool Retired { get; set; }
    }

    private readonly record struct RuntimeKeyIdentity(
        string LoaderKind,
        string NormalizedModId,
        long Generation)
    {
        internal static RuntimeKeyIdentity From(ModRuntimeKey key)
            => new(key.LoaderKind, key.ModId.ToUpperInvariant(), key.Generation);
    }

    private static readonly object Sync = new();
    private static readonly List<Entry> Entries = new();
    private static long _nextSequence;

    internal static bool TryRegister(
        ModRuntimeKey key,
        ModOwnedResourceKind kind,
        string identity,
        ModOwnedResourceRetirementPolicy? retirementPolicy = null)
    {
        if (!key.IsValid || string.IsNullOrWhiteSpace(identity))
            return false;
        var policy = retirementPolicy ?? DefaultRetirementPolicy(kind);

        lock (Sync)
        {
            var existing = Entries.FirstOrDefault(entry =>
                !entry.Retired &&
                entry.Key.Matches(key) &&
                entry.Kind == kind &&
                string.Equals(entry.Identity, identity, StringComparison.Ordinal));
            if (existing != null)
                return existing.RetirementPolicy == policy;

            // Per-domain budget: refuse only this owner's new resource, never touch anyone
            // else's. Counted under the same lock so the check cannot race a concurrent
            // registration into exceeding the ceiling.
            var live = 0;
            foreach (var entry in Entries)
            {
                if (!entry.Retired && entry.Key.Matches(key) && entry.Kind == kind)
                    live++;
            }
            if (!ModResourceBudget.TryReserve(key, kind, live, out var refusal))
            {
                Manager.Logger.Error(nameof(ModOwnedResourceRegistry), refusal!);
                return false;
            }

            if (_nextSequence == long.MaxValue)
                return false;
            Entries.Add(new Entry(++_nextSequence, key, kind, policy, identity));
            return true;
        }
    }

    internal static int Retire(ModRuntimeKey key)
    {
        if (!key.IsValid)
            return 0;

        lock (Sync)
        {
            var retired = 0;
            foreach (var entry in Entries)
            {
                if (entry.Retired || !entry.Key.Matches(key))
                    continue;
                entry.Retired = true;
                ++retired;
            }
            return retired;
        }
    }

    internal static int RetireOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return 0;

        lock (Sync)
        {
            var retired = 0;
            foreach (var entry in Entries)
            {
                if (entry.Retired ||
                    !string.Equals(entry.Key.OwnerId, ownerId, StringComparison.Ordinal))
                    continue;
                entry.Retired = true;
                ++retired;
            }
            return retired;
        }
    }

    internal static int RetireMatching(
        ModRuntimeKey key,
        ModOwnedResourceKind kind,
        string identityPrefix)
    {
        if (!key.IsValid || string.IsNullOrWhiteSpace(identityPrefix))
            return 0;

        lock (Sync)
        {
            var retired = 0;
            foreach (var entry in Entries)
            {
                if (entry.Retired ||
                    !entry.Key.Matches(key) ||
                    entry.Kind != kind ||
                    !entry.Identity.StartsWith(identityPrefix, StringComparison.Ordinal))
                    continue;
                entry.Retired = true;
                ++retired;
            }
            return retired;
        }
    }

    internal static int RetireKind(
        ModRuntimeKey key,
        ModOwnedResourceKind kind)
    {
        if (!key.IsValid)
            return 0;

        lock (Sync)
        {
            var retired = 0;
            foreach (var entry in Entries)
            {
                if (entry.Retired || !entry.Key.Matches(key) || entry.Kind != kind)
                    continue;
                entry.Retired = true;
                ++retired;
            }
            return retired;
        }
    }

    internal static bool RetireExact(
        ModRuntimeKey key,
        ModOwnedResourceKind kind,
        string identity)
    {
        if (!key.IsValid || string.IsNullOrWhiteSpace(identity))
            return false;

        lock (Sync)
        {
            var entry = Entries.FirstOrDefault(candidate =>
                !candidate.Retired &&
                candidate.Key.Matches(key) &&
                candidate.Kind == kind &&
                string.Equals(candidate.Identity, identity, StringComparison.Ordinal));
            if (entry == null)
                return false;
            entry.Retired = true;
            return true;
        }
    }

    internal static IReadOnlyList<ModOwnedResourceSnapshot> Snapshot(
        ModRuntimeKey? key = null,
        bool includeRetired = true)
    {
        lock (Sync)
        {
            return Entries
                .Where(entry =>
                    (key is null || entry.Key.Matches(key.Value)) &&
                    (includeRetired || !entry.Retired))
                .Select(ToSnapshot)
                .ToArray();
        }
    }

    internal static ModOwnedResourceAuditSnapshot CreateAuditSnapshot(
        IEnumerable<ModRuntimeSessionSnapshot> runtimeSessions,
        ModRuntimeKey? scope = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeSessions);
        var sessionSnapshots = runtimeSessions
            .Where(session => session.Key.IsValid &&
                              (scope is null || session.Key.Matches(scope.Value)))
            .ToArray();
        ModOwnedResourceSnapshot[] resourceSnapshots;
        long lastSequence;
        lock (Sync)
        {
            resourceSnapshots = Entries
                .Where(entry => scope is null || entry.Key.Matches(scope.Value))
                .Select(ToSnapshot)
                .ToArray();
            lastSequence = _nextSequence;
        }

        var sessionsByKey = sessionSnapshots
            .GroupBy(session => RuntimeKeyIdentity.From(session.Key))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var resourcesByKey = resourceSnapshots
            .GroupBy(resource => RuntimeKeyIdentity.From(resource.Key))
            .ToDictionary(group => group.Key, group => group.OrderBy(resource => resource.Sequence).ToArray());
        var identities = new HashSet<RuntimeKeyIdentity>(sessionsByKey.Keys);
        identities.UnionWith(resourcesByKey.Keys);

        var generations = new List<ModOwnedResourceGenerationAudit>(identities.Count);
        var issues = new List<ModOwnedResourceAuditIssue>();
        foreach (var identity in identities
                     .OrderBy(value => value.LoaderKind, StringComparer.Ordinal)
                     .ThenBy(value => value.NormalizedModId, StringComparer.Ordinal)
                     .ThenBy(value => value.Generation))
        {
            sessionsByKey.TryGetValue(identity, out var sessions);
            resourcesByKey.TryGetValue(identity, out var resources);
            sessions ??= Array.Empty<ModRuntimeSessionSnapshot>();
            resources ??= Array.Empty<ModOwnedResourceSnapshot>();
            var session = sessions.FirstOrDefault();
            var key = resources.FirstOrDefault().Key.IsValid
                ? resources[0].Key
                : session.Key;
            var distinctRuntimeStates = sessions
                .Select(value => (
                    value.State,
                    value.ActiveCallbacks,
                    value.ActiveOperations))
                .Distinct()
                .ToArray();
            if (distinctRuntimeStates.Length > 1)
            {
                issues.Add(new ModOwnedResourceAuditIssue(
                    ModOwnedResourceAuditIssueKind.ConflictingRuntimeSnapshot,
                    key,
                    0,
                    Array.Empty<long>(),
                    $"runtime snapshots disagree count={distinctRuntimeStates.Length}"));
            }

            var active = resources.Where(resource => !resource.Retired).ToArray();
            var retiredCount = resources.Length - active.Length;
            if (sessions.Length == 0 && active.Length != 0)
            {
                AddIssue(
                    issues,
                    ModOwnedResourceAuditIssueKind.ActiveResourceWithoutRuntimeSession,
                    key,
                    active,
                    "active resources have no matching runtime generation");
            }
            else if (sessions.Length != 0 &&
                     session.State is (
                         ModRuntimeLifecycleState.New or
                         ModRuntimeLifecycleState.Retired or
                         ModRuntimeLifecycleState.Faulted) &&
                     active.Length != 0)
            {
                AddIssue(
                    issues,
                    ModOwnedResourceAuditIssueKind.ActiveResourceAfterTerminalState,
                    key,
                    active,
                    $"runtime state={session.State}");
            }
            else if (sessions.Length != 0 && session.State == ModRuntimeLifecycleState.Suspended)
            {
                var mustRetire = active
                    .Where(resource =>
                        resource.RetirementPolicy == ModOwnedResourceRetirementPolicy.MustRetire)
                    .ToArray();
                if (mustRetire.Length != 0)
                {
                    AddIssue(
                        issues,
                        ModOwnedResourceAuditIssueKind.MustRetireResourceWhileSuspended,
                        key,
                        mustRetire,
                        "suspended generation retained resources that require retirement");
                }
            }

            generations.Add(new ModOwnedResourceGenerationAudit(
                key,
                sessions.Length != 0,
                sessions.Length == 0 ? null : session.State,
                sessions.Length == 0 ? 0 : session.ActiveCallbacks,
                sessions.Length == 0 ? 0 : session.ActiveOperations,
                active.Length,
                retiredCount,
                active.Count(resource =>
                    resource.RetirementPolicy == ModOwnedResourceRetirementPolicy.MustRetire),
                active.Count(resource =>
                    resource.RetirementPolicy == ModOwnedResourceRetirementPolicy.RetainWhileSuspended),
                active.Count(resource =>
                    resource.RetirementPolicy == ModOwnedResourceRetirementPolicy.ObserveOnly),
                resources));
        }

        return new ModOwnedResourceAuditSnapshot(
            lastSequence,
            resourceSnapshots.Count(resource => !resource.Retired),
            resourceSnapshots.Count(resource => resource.Retired),
            generations,
            issues);
    }

    private static ModOwnedResourceRetirementPolicy DefaultRetirementPolicy(
        ModOwnedResourceKind kind)
        => kind switch
        {
            ModOwnedResourceKind.Hook or ModOwnedResourceKind.CodePatch or
                ModOwnedResourceKind.Behaviour or ModOwnedResourceKind.InputSubscription =>
                ModOwnedResourceRetirementPolicy.RetainWhileSuspended,
            ModOwnedResourceKind.NativeLibrary =>
                ModOwnedResourceRetirementPolicy.ObserveOnly,
            _ => ModOwnedResourceRetirementPolicy.MustRetire,
        };

    private static ModOwnedResourceSnapshot ToSnapshot(Entry entry)
        => new(
            entry.Sequence,
            entry.Key,
            entry.Kind,
            entry.RetirementPolicy,
            entry.Identity,
            entry.Retired);

    private static void AddIssue(
        ICollection<ModOwnedResourceAuditIssue> issues,
        ModOwnedResourceAuditIssueKind kind,
        ModRuntimeKey key,
        IReadOnlyCollection<ModOwnedResourceSnapshot> resources,
        string detail)
        => issues.Add(new ModOwnedResourceAuditIssue(
            kind,
            key,
            resources.Count,
            resources.Select(resource => resource.Sequence).ToArray(),
            detail));

    internal static void ClearForTests()
    {
        lock (Sync)
        {
            Entries.Clear();
            _nextSequence = 0;
        }
    }
}

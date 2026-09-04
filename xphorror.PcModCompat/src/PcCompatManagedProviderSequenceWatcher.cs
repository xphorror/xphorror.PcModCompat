using System.Globalization;

namespace Xphorror.PcModCompat;

/// <summary>
/// Watches the integer sequences returned by the BindingProviders that back live lowered plans and
/// reports when one of them stops matching the value the plan was built from.
/// </summary>
/// <remarks>
/// <para>
/// A lowered consumer plan freezes a configuration the MOD still owns. Nothing stops the MOD from
/// changing it: the audited JipperKeyViewer release assembly resolves its key array through a static
/// switch over a settings field and re-reads it whenever that field changes, and its own settings
/// menu both changes the style and rebinds individual keys. Once our snapshot and the MOD's live
/// configuration disagree, touch lanes publish identities the MOD no longer queries and the MOD
/// queries identities nobody publishes - so the key viewer stops responding with no error raised
/// anywhere. This watcher is what turns that silent divergence into a republication.
/// </para>
/// <para>
/// It is deliberately MOD-agnostic. It knows about provider roles and integer sequences; it never
/// looks at field names, key counts or styles, and it holds no MOD-specific special cases.
/// </para>
/// </remarks>
public sealed class PcCompatManagedProviderSequenceWatcher
{
    /// <summary>
    /// Matches the interval the plugin already uses for its other bounded polls. A configuration
    /// change is user-initiated, so half a second of latency is imperceptible, and the gate lives
    /// here rather than in the caller so no caller can turn provider reflection - which crosses into
    /// MOD code - into per-frame work.
    /// </summary>
    public const long PollIntervalMilliseconds = 500;

    private readonly object _gate = new();
    private readonly List<WatchedProvider> _watched = [];
    private long _nextPoll;

    public bool IsWatching
    {
        get
        {
            lock (_gate)
                return _watched.Count > 0;
        }
    }

    /// <summary>
    /// Admits at most one observation per <see cref="PollIntervalMilliseconds"/>. The clock is a
    /// parameter so the caller supplies the host's monotonic tick count and the gate stays testable.
    /// </summary>
    public bool ShouldPoll(long nowMilliseconds)
    {
        lock (_gate)
        {
            if (nowMilliseconds < _nextPoll)
                return false;
            _nextPoll = nowMilliseconds + PollIntervalMilliseconds;
            return true;
        }
    }

    /// <summary>
    /// Records the sequences a completed lowering resolved.
    /// </summary>
    /// <remarks>
    /// Entries are merged by candidate key rather than replaced wholesale. A lowering that failed
    /// reports nothing, and one that partly failed reports only the features it got through; in
    /// both cases dropping the providers it did not mention would strand the MOD in the failed
    /// state, because the value change that would restore it is exactly what we would stop
    /// watching. Retained entries keep their last observed fingerprint, so growth is bounded to the
    /// candidates already seen and <see cref="Clear"/> at teardown resets it.
    /// </remarks>
    public void SetBaseline(IEnumerable<PcCompatKeyViewerResolvedProviderSequence> resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        lock (_gate)
        {
            foreach (var entry in resolved)
            {
                if (entry == null)
                    continue;
                var fingerprint = Fingerprint(entry.Values);
                var index = IndexOf(entry.Role.CandidateKey);
                if (index >= 0)
                {
                    _watched[index] = _watched[index] with
                    {
                        RequiredCount = entry.RequiredCount,
                        Fingerprint = fingerprint
                    };
                    continue;
                }
                _watched.Add(new WatchedProvider(
                    entry.Role.CandidateKey,
                    entry.FeatureId,
                    entry.Role,
                    entry.RequiredCount,
                    fingerprint));
            }
        }
    }

    /// <summary>
    /// Resolves every watched provider and reports whether any of them diverged from its baseline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A resolution failure counts as a change. The concrete case is a provider whose sequence grows
    /// past the published lane ABI: resolution starts failing, and reporting a change is what lets
    /// the caller withdraw the plan instead of leaving the consumer publishing identities the MOD no
    /// longer reads.
    /// </para>
    /// <para>
    /// Every provider is polled and every baseline advanced even after the first divergence is
    /// found, so a single configuration change causes a single republication. Short-circuiting would
    /// leave the providers after the changed one stale and report them again on the next poll.
    /// </para>
    /// <para>
    /// The fingerprint covers exactly the provider prefix consumed by the current lane projection.
    /// Layouts may expose many more keys (including full keyboard layouts); values after that prefix
    /// cannot alter this plan and must not turn provider polling into unbounded enumeration.
    /// </para>
    /// </remarks>
    public bool TryDetectChange(
        Func<PcCompatKeyViewerRoleOverride, int, (bool Success, int[] Values, string? Error)>
            resolveProvider,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(resolveProvider);
        reason = null;
        WatchedProvider[] watched;
        lock (_gate)
        {
            if (_watched.Count == 0)
                return false;
            watched = _watched.ToArray();
        }

        List<string>? changes = null;
        foreach (var provider in watched)
        {
            var resolved = resolveProvider(provider.Role, provider.RequiredCount);
            var fingerprint = resolved.Success
                ? Fingerprint(resolved.Values)
                : FailureFingerprint(resolved.Error);
            if (string.Equals(fingerprint, provider.Fingerprint, StringComparison.Ordinal))
                continue;
            lock (_gate)
            {
                var index = IndexOf(provider.CandidateKey);
                if (index >= 0)
                    _watched[index] = _watched[index] with { Fingerprint = fingerprint };
            }
            changes ??= [];
            changes.Add(
                $"feature '{provider.FeatureId}' provider {provider.CandidateKey}: " +
                (resolved.Success
                    ? $"[{provider.Fingerprint}] -> [{fingerprint}]"
                    : $"resolution failed: {resolved.Error ?? "unknown error"}"));
        }

        if (changes == null)
            return false;
        reason = string.Join("; ", changes);
        return true;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _watched.Clear();
            _nextPoll = 0;
        }
    }

    private int IndexOf(string candidateKey)
    {
        for (var index = 0; index < _watched.Count; ++index)
        {
            if (string.Equals(_watched[index].CandidateKey, candidateKey, StringComparison.Ordinal))
                return index;
        }
        return -1;
    }

    private static string Fingerprint(IReadOnlyList<int>? values)
    {
        if (values == null || values.Count == 0)
            return "empty";
        return string.Join(
            ',',
            values.Select(value => value.ToString(CultureInfo.InvariantCulture)));
    }

    // Prefixed so no successful sequence can ever produce the same fingerprint as a failure.
    private static string FailureFingerprint(string? error)
        => "!" + (error ?? "unknown error");

    private sealed record WatchedProvider(
        string CandidateKey,
        string FeatureId,
        PcCompatKeyViewerRoleOverride Role,
        int RequiredCount,
        string Fingerprint);
}

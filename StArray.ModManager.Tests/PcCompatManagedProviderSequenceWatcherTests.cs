using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

/// <summary>
/// A lowered consumer plan is a frozen snapshot of configuration the MOD still owns and still
/// mutates. These tests pin the contract that notices the divergence.
/// </summary>
/// <remarks>
/// <para>
/// The audited JipperKeyViewer 1.7.0 release assembly makes this concrete: <c>GetKeyCode()</c> is a
/// static switch over <c>Settings.Data.KeyViewerStyle</c> that returns a different array field per
/// style, and the MOD re-reads it whenever the style changes. Nothing on our side re-read it, so
/// after the user changed the style inside the MOD's own menu we kept publishing identities the MOD
/// no longer queried, and the MOD queried identities we never published - touch silently stopped
/// reaching the key viewer with no error anywhere.
/// </para>
/// <para>
/// The watcher is deliberately MOD-agnostic: it knows about provider roles and integer sequences,
/// never about styles, field names or key counts.
/// </para>
/// </remarks>
public sealed class PcCompatManagedProviderSequenceWatcherTests
{
    [Test]
    public void ProviderMaterializationReadsOnlyProjectedPrefixFromFullKeyboardLayout()
    {
        var fullKeyboard = Enumerable.Range(1, 108).Cast<object>().ToArray();

        var success = PcCompatManagedModSession.TryMaterializeIntPrefix(
            fullKeyboard,
            requiredCount: 10,
            out var values,
            out var error);

        Assert.Multiple(() =>
        {
            Assert.That(success, Is.True, error);
            Assert.That(values, Is.EqualTo(Enumerable.Range(1, 10)));
        });
    }

    [Test]
    public void StableProviderValuesReportNoChange()
    {
        var watcher = new PcCompatManagedProviderSequenceWatcher();
        watcher.SetBaseline([Resolved("GetKeys", 97, 98)]);

        Assert.Multiple(() =>
        {
            Assert.That(watcher.IsWatching, Is.True);
            Assert.That(
                watcher.TryDetectChange(Resolver(("GetKeys", [97, 98])), out var reason),
                Is.False,
                reason);
        });
    }

    [Test]
    public void ChangedProviderValuesReportChangeExactlyOnce()
    {
        var watcher = new PcCompatManagedProviderSequenceWatcher();
        watcher.SetBaseline([Resolved("GetKeys", 97, 98)]);
        var resolver = Resolver(("GetKeys", [97, 99]));

        Assert.Multiple(() =>
        {
            Assert.That(watcher.TryDetectChange(resolver, out var first), Is.True);
            Assert.That(first, Does.Contain("GetKeys"));
            // The baseline advances as part of detection, so one configuration change causes one
            // republication rather than one per poll for as long as the new value persists.
            Assert.That(watcher.TryDetectChange(resolver, out _), Is.False);
        });
    }

    [Test]
    public void FullKeyboardProviderDoesNotFailOrInvalidateAnUnchangedProjectedPrefix()
    {
        var watcher = new PcCompatManagedProviderSequenceWatcher();
        watcher.SetBaseline([Resolved("GetKeys", 97, 98)]);

        Assert.That(
            watcher.TryDetectChange(
                Resolver(("GetKeys", Enumerable.Range(97, 108).ToArray())),
                out var reason),
            Is.False,
            reason);
    }

    [Test]
    public void ResolutionFailureThatPersistsReportsChangeOnlyOnce()
    {
        var watcher = new PcCompatManagedProviderSequenceWatcher();
        watcher.SetBaseline([Resolved("GetKeys", 97, 98)]);
        Func<PcCompatKeyViewerRoleOverride, int, (bool, int[], string?)> failing =
            (_, _) => (false, Array.Empty<int>(), "provider failed");

        Assert.Multiple(() =>
        {
            Assert.That(watcher.TryDetectChange(failing, out _), Is.True);
            Assert.That(watcher.TryDetectChange(failing, out _), Is.False);
        });
    }

    [Test]
    public void FailedRepublicationKeepsWatchingSoRestoringTheConfigurationRecovers()
    {
        var watcher = new PcCompatManagedProviderSequenceWatcher();
        watcher.SetBaseline([Resolved("GetKeys", 97, 98)]);

        Assert.That(
            watcher.TryDetectChange(
                (_, _) => (false, Array.Empty<int>(), "provider failed"),
                out _),
            Is.True);

        // Re-lowering failed, so it produced no plans and therefore no resolved providers. An empty
        // baseline must not be taken as "nothing to watch" - that would strand the MOD in the
        // failed state until it is reloaded, because the value change that restores it would never
        // be observed.
        watcher.SetBaseline([]);

        Assert.Multiple(() =>
        {
            Assert.That(watcher.IsWatching, Is.True);
            Assert.That(watcher.TryDetectChange(Resolver(("GetKeys", [97, 98])), out var reason),
                Is.True,
                "restoring the original configuration must be observable");
            Assert.That(reason, Does.Contain("GetKeys"));
        });
    }

    [Test]
    public void SequenceSuffixChangeIsIgnoredWhenTheProjectedPrefixMatches()
    {
        var watcher = new PcCompatManagedProviderSequenceWatcher();
        watcher.SetBaseline([Resolved("GetKeys", 97, 98)]);

        Assert.That(
            watcher.TryDetectChange(Resolver(("GetKeys", [97, 98, 99])), out var reason),
            Is.False,
            reason);
    }

    [Test]
    public void EveryWatchedProviderIsPolledAndAnyOneOfThemTriggersRepublication()
    {
        var watcher = new PcCompatManagedProviderSequenceWatcher();
        watcher.SetBaseline([Resolved("GetKeys", 97, 98), Resolved("GetFootKeys", 32, 13)]);
        var polled = new List<string>();

        var changed = watcher.TryDetectChange(
            (role, _) =>
            {
                polled.Add(role.MemberName!);
                return role.MemberName == "GetFootKeys"
                    ? (true, [32, 14], null)
                    : (true, [97, 98], null);
            },
            out var reason);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(polled, Is.EquivalentTo(new[] { "GetKeys", "GetFootKeys" }));
            Assert.That(reason, Does.Contain("GetFootKeys"));
        });
    }

    [Test]
    public void ClearStopsWatchingSoTeardownCannotPollARetiredSession()
    {
        var watcher = new PcCompatManagedProviderSequenceWatcher();
        watcher.SetBaseline([Resolved("GetKeys", 97, 98)]);
        watcher.Clear();

        Assert.Multiple(() =>
        {
            Assert.That(watcher.IsWatching, Is.False);
            Assert.That(
                watcher.TryDetectChange(
                    (_, _) => throw new InvalidOperationException("a cleared watcher must not resolve"),
                    out _),
                Is.False);
        });
    }

    [Test]
    public void PollGateAdmitsOneObservationPerIntervalRegardlessOfFrameRate()
    {
        var watcher = new PcCompatManagedProviderSequenceWatcher();
        var interval = PcCompatManagedProviderSequenceWatcher.PollIntervalMilliseconds;

        Assert.Multiple(() =>
        {
            // The gate lives with the watcher rather than the caller so no caller can turn provider
            // reflection into per-frame work.
            Assert.That(watcher.ShouldPoll(1_000), Is.True, "the first observation is admitted");
            Assert.That(watcher.ShouldPoll(1_000 + interval - 1), Is.False);
            Assert.That(watcher.ShouldPoll(1_000 + interval), Is.True);
        });
    }

    private static PcCompatKeyViewerResolvedProviderSequence Resolved(
        string memberName,
        params int[] values)
        => new()
        {
            FeatureId = "keyviewer",
            Role = Role(memberName),
            RequiredCount = values.Length,
            Values = values
        };

    private static PcCompatKeyViewerRoleOverride Role(string memberName)
        => new()
        {
            Role = "BindingProvider",
            AssemblyName = "TestMod",
            TypeName = "TestMod.Viewer",
            MemberName = memberName,
            MemberKind = "Method"
        };

    private static Func<PcCompatKeyViewerRoleOverride, int, (bool Success, int[] Values, string? Error)>
        Resolver(params (string MemberName, int[] Values)[] results)
        => (role, requiredCount) =>
        {
            foreach (var result in results)
            {
                if (result.MemberName == role.MemberName)
                    return result.Values.Length < requiredCount
                        ? (false, Array.Empty<int>(), $"provider returned {result.Values.Length} keys")
                        : (true, result.Values.Take(requiredCount).ToArray(), null);
            }
            return (false, Array.Empty<int>(), $"unexpected provider {role.MemberName}");
        };
}

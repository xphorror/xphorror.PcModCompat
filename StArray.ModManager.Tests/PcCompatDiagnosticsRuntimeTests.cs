using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public class PcCompatDiagnosticsRuntimeTests
{
    [SetUp]
    public void SetUp()
        => PcCompatDiagnosticsRuntime.ClearProvider();

    [TearDown]
    public void TearDown()
        => PcCompatDiagnosticsRuntime.ClearProvider();

    [Test]
    public void SnapshotCachesProviderReadsUntilForced()
    {
        var reads = 0;
        PcCompatDiagnosticsRuntime.RegisterProvider(
            () => new PcCompatDiagnosticsSnapshot
            {
                ProviderAvailable = true,
                LoadedRules = ++reads
            },
            modId => $"slots:{modId}",
            _ => 0);

        var first = PcCompatDiagnosticsRuntime.Snapshot(forceRefresh: true);
        var cached = PcCompatDiagnosticsRuntime.Snapshot();
        var refreshed = PcCompatDiagnosticsRuntime.Snapshot(forceRefresh: true);

        Assert.That(first.LoadedRules, Is.EqualTo(1));
        Assert.That(cached, Is.SameAs(first));
        Assert.That(refreshed.LoadedRules, Is.EqualTo(2));
        Assert.That(reads, Is.EqualTo(2));
    }

    [Test]
    public void ExecuteReturnsNativeResultAndRefreshesSnapshot()
    {
        var reads = 0;
        PcCompatDiagnosticsCommand? executed = null;
        PcCompatDiagnosticsRuntime.RegisterProvider(
            () => new PcCompatDiagnosticsSnapshot
            {
                ProviderAvailable = true,
                InstalledSlots = ++reads
            },
            modId => $"slots:{modId}",
            command =>
            {
                executed = command;
                return 3;
            });

        _ = PcCompatDiagnosticsRuntime.Snapshot(forceRefresh: true);
        var result = PcCompatDiagnosticsRuntime.Execute(PcCompatDiagnosticsCommand.Install);

        Assert.That(executed, Is.EqualTo(PcCompatDiagnosticsCommand.Install));
        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.NativeResult, Is.EqualTo(3));
        Assert.That(result.Snapshot.InstalledSlots, Is.EqualTo(2));
    }

    [Test]
    public void SlotSummaryUsesPerModProvider()
    {
        PcCompatDiagnosticsRuntime.RegisterProvider(
            () => new PcCompatDiagnosticsSnapshot { ProviderAvailable = true },
            modId => $"filtered:{modId}",
            _ => 0);

        Assert.That(PcCompatDiagnosticsRuntime.GetSlotSummaryForMod("sample.mod"),
            Is.EqualTo("filtered:sample.mod"));
    }

    [Test]
    public void MissingProviderReturnsUnavailableOperation()
    {
        var result = PcCompatDiagnosticsRuntime.Execute(PcCompatDiagnosticsCommand.Resolve);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Snapshot.ProviderAvailable, Is.False);
    }
}

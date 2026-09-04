using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

/// <summary>
/// Cross-backend Unity lease teardown: fixed order, per-backend fault isolation, and the order
/// must stay identical to the production unload path.
/// </summary>
[NonParallelizable]
public sealed class PcCompatUnityLeaseTeardownTests
{
    [Test]
    public void TeardownDrivesBackendsInDependencyOrder()
    {
        var result = PcCompatUnityLeaseTeardown.Run("lease.teardown.order", 3);

        Assert.Multiple(() =>
        {
            Assert.That(
                result.Steps.Select(step => step.Backend),
                Is.EqualTo(new[] { "managedComponents", "virtualBundle", "resourceChanger" }),
                "components own the host objects and must be destroyed before the resources " +
                "they consumed are released; shared-property contributions restore the next " +
                "owner's baseline last");
            Assert.That(result.Steps.All(step => step.Attempted), Is.True);
            Assert.That(result.ModId, Is.EqualTo("lease.teardown.order"));
            Assert.That(result.ResourceSessionGeneration, Is.EqualTo(3));
        });
    }

    [Test]
    public void TeardownOfAnUnknownSessionSucceedsAndLeavesNothingBehind()
    {
        var modId = "lease.teardown.unknown-" + Guid.NewGuid().ToString("N");

        var result = PcCompatUnityLeaseTeardown.Run(modId, 1);
        var audit = PcCompatUnityObjectLeaseAudit.Snapshot(modId, 1);

        Assert.Multiple(() =>
        {
            // Every backend step is idempotent, so recovery passes after a partially failed
            // unload must not throw.
            Assert.That(result.Succeeded, Is.True, result.FirstError);
            Assert.That(result.FirstError, Is.Null);
            // Paired with the audit half this is the "session ended clean" proof.
            Assert.That(audit.IsClear, Is.True);
        });
    }

    [Test]
    public void ProductionUnloadPathKeepsTheSameBackendOrderAsTheProtocol()
    {
        // The protocol is the executable specification of PcCompatRuntime.UnregisterMod's
        // ordering. If someone reorders the production path without updating the protocol the
        // two silently disagree, so the source order is asserted directly.
        var runtime = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "xphorror.PcModCompat",
            "src",
            "PcCompatRuntime.cs"));

        var unregister = runtime.IndexOf(
            "runtime-unregister-enter",
            StringComparison.Ordinal);
        Assert.That(unregister, Is.GreaterThanOrEqualTo(0), "unload path anchor not found");

        var dispose = runtime.IndexOf("session?.Dispose();", unregister, StringComparison.Ordinal);
        var bundle = runtime.IndexOf(
            "PcCompatVirtualBundleRegistry.RemoveMod(manifest.Id);",
            dispose,
            StringComparison.Ordinal);
        var changer = runtime.IndexOf(
            "PcCompatResourceChangerRuntime.Remove(manifest.Id);",
            bundle,
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(dispose, Is.GreaterThan(unregister), "session dispose runs first");
            Assert.That(bundle, Is.GreaterThan(dispose), "VirtualBundle releases after dispose");
            Assert.That(changer, Is.GreaterThan(bundle), "ResourceChanger restores last");
        });
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "xphorror.PcModCompat")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the StArray.ModManager root");
    }
}

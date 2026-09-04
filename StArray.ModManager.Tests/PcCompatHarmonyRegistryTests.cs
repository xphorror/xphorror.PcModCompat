using System.Reflection;
using HarmonyLib;
using JALib.Core.Patch;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

/// <summary>
/// The shim Harmony registry is what the host reads back instead of inspecting physical hooks, and it is
/// reached purely by reflection over duck-typed members. A rename on either side would otherwise surface
/// only as silently missing descriptors, so the shape is asserted here rather than inferred at runtime.
/// </summary>
[NonParallelizable]
public class PcCompatHarmonyRegistryTests
{
    [SetUp]
    public void ResetRegistry()
    {
        HarmonyRegistry.ClearRegisteredPatches();
        HarmonyRegistry.ClearDiagnostics();
    }

    [Test]
    public void ClearingRegistrationsKeepsTheDiagnosticPaperTrail()
    {
        // The host clears the registry before the MOD's bootstrap entry point runs, but a diagnostic can
        // be raised at any later point too. Clearing records must not take the reasons with it: an
        // unavailable-API report is the only record of why a MOD behaved differently than on PC.
        var harmony = new Harmony("test.diagnostics");
        harmony.Patch(
            typeof(DiagnosticTarget).GetMethod(nameof(DiagnosticTarget.Run))!,
            prefix: new HarmonyMethod(typeof(DiagnosticPatch).GetMethod(nameof(DiagnosticPatch.Prefix))!));
        HarmonyRegistry.ReportUnavailable("Harmony.Test", "recorded for the paper trail");

        Assert.That(HarmonyRegistry.RegisteredPatchCount, Is.EqualTo(1));
        Assert.That(HarmonyRegistry.SnapshotDiagnostics(), Has.Length.EqualTo(1));

        HarmonyRegistry.ClearRegisteredPatches();

        Assert.Multiple(() =>
        {
            Assert.That(HarmonyRegistry.RegisteredPatchCount, Is.Zero);
            Assert.That(
                HarmonyRegistry.SnapshotDiagnostics().Select(diagnostic => diagnostic.Api),
                Does.Contain("Harmony.Test"));
        });

        HarmonyRegistry.ClearDiagnostics();
        Assert.That(HarmonyRegistry.SnapshotDiagnostics(), Is.Empty);
    }

    [Test]
    public void BothLogicalRegistriesExposeTheStaticsTheHostReflectsOver()
    {
        foreach (var registry in new[] { typeof(JAPatcher), typeof(HarmonyRegistry) })
        {
            const BindingFlags statics = BindingFlags.Public | BindingFlags.Static;
            Assert.Multiple(() =>
            {
                Assert.That(
                    registry.GetProperty("RegisteredPatchCount", statics)?.PropertyType,
                    Is.EqualTo(typeof(int)),
                    $"{registry.Name}.RegisteredPatchCount");
                Assert.That(
                    registry.GetMethod("ClearRegisteredPatches", statics),
                    Is.Not.Null,
                    $"{registry.Name}.ClearRegisteredPatches");
                Assert.That(
                    registry.GetMethod("SnapshotRegisteredPatches", statics)?.ReturnType.IsArray,
                    Is.True,
                    $"{registry.Name}.SnapshotRegisteredPatches");
            });
        }
    }

    [Test]
    public void BothLogicalRegistriesShareTheRecordPropertyNamesTheHostReads()
    {
        // Kept in sync with PcCompatManagedLoader.SnapshotPatches and
        // PcCompatManagedModSession.SnapshotShimCallbacks, which look these up by name.
        string[] required =
        [
            "TargetType", "TargetMethod", "Kind", "CallbackType", "CallbackMethod",
            "Status", "Reason", "CallbackMethodInfo", "CallbackTarget", "Active",
            "OriginalMethod", "RegistrationIndex", "Priority", "Before", "After"
        ];

        foreach (var registry in new[] { typeof(JAPatcher), typeof(HarmonyRegistry) })
        {
            var record = registry
                .GetMethod("SnapshotRegisteredPatches", BindingFlags.Public | BindingFlags.Static)!
                .ReturnType
                .GetElementType()!;
            var missing = required.Where(name => record.GetProperty(name) == null).ToArray();
            Assert.That(missing, Is.Empty, $"{record.Name} is missing host-read properties");
        }
    }

    [Test]
    public void RuntimeRegistrationRetainsOwnerAndOrderingMetadata()
    {
        var harmony = new Harmony("owner.runtime-order");
        harmony.Patch(
            typeof(DiagnosticTarget).GetMethod(nameof(DiagnosticTarget.Run))!,
            prefix: new HarmonyMethod(
                typeof(DiagnosticPatch).GetMethod(nameof(DiagnosticPatch.Prefix))!,
                priority: Priority.High,
                before: ["owner.after-us"],
                after: ["owner.before-us"]));

        var record = HarmonyRegistry.SnapshotRegisteredPatches().Single();
        Assert.Multiple(() =>
        {
            Assert.That(record.HarmonyId, Is.EqualTo("owner.runtime-order"));
            Assert.That(record.RegistrationIndex, Is.GreaterThan(0));
            Assert.That(record.Priority, Is.EqualTo(Priority.High));
            Assert.That(record.Before, Is.EqualTo(new[] { "owner.after-us" }));
            Assert.That(record.After, Is.EqualTo(new[] { "owner.before-us" }));
        });
    }

    [Test]
    public void RevisionChangesOnPatchUnpatchAndRepatchEvenWhenRecordCountDoesNot()
    {
        var harmony = new Harmony("owner.dynamic-lifecycle");
        var original = typeof(DiagnosticTarget).GetMethod(nameof(DiagnosticTarget.Run))!;
        var callback = typeof(DiagnosticPatch).GetMethod(nameof(DiagnosticPatch.Prefix))!;
        var initialRevision = HarmonyRegistry.Revision;

        harmony.Patch(original, prefix: new HarmonyMethod(callback));
        var patchedRevision = HarmonyRegistry.Revision;
        Assert.That(patchedRevision, Is.GreaterThan(initialRevision));
        Assert.That(HarmonyRegistry.RegisteredPatchCount, Is.EqualTo(1));

        harmony.Unpatch(original, HarmonyPatchType.Prefix, harmony.Id);
        var unpatchedRevision = HarmonyRegistry.Revision;
        Assert.Multiple(() =>
        {
            Assert.That(unpatchedRevision, Is.GreaterThan(patchedRevision));
            Assert.That(HarmonyRegistry.RegisteredPatchCount, Is.EqualTo(1));
            Assert.That(HarmonyRegistry.SnapshotRegisteredPatches().Single().Active, Is.False);
        });

        harmony.Patch(original, prefix: new HarmonyMethod(callback));
        Assert.Multiple(() =>
        {
            Assert.That(HarmonyRegistry.Revision, Is.GreaterThan(unpatchedRevision));
            Assert.That(HarmonyRegistry.RegisteredPatchCount, Is.EqualTo(2));
            Assert.That(HarmonyRegistry.SnapshotRegisteredPatches().Count(record => record.Active), Is.EqualTo(1));
        });
    }

    [Test]
    public void HostRegistryVersionReaderPrefersRevisionAndFallsBackToJalibChangeCounter()
    {
        var harmonyReader = PcCompatShimPatchRegistries.ChangeVersionReader(typeof(HarmonyRegistry));
        var jalibReader = PcCompatShimPatchRegistries.ChangeVersionReader(typeof(JAPatcher));
        Assert.That(harmonyReader, Is.Not.Null);
        Assert.That(jalibReader, Is.Not.Null);

        var harmony = new Harmony("owner.version-reader");
        var original = typeof(DiagnosticTarget).GetMethod(nameof(DiagnosticTarget.Run))!;
        var callback = typeof(DiagnosticPatch).GetMethod(nameof(DiagnosticPatch.Prefix))!;
        var harmonyBefore = harmonyReader!();
        harmony.Patch(original, prefix: new HarmonyMethod(callback));
        var harmonyPatched = harmonyReader();
        harmony.Unpatch(original, HarmonyPatchType.Prefix, harmony.Id);
        var harmonyUnpatched = harmonyReader();

        var jalibBefore = jalibReader!();
        JAPatcher.ClearRegisteredPatches();
        var jalibAfter = jalibReader();

        Assert.Multiple(() =>
        {
            Assert.That(harmonyPatched, Is.GreaterThan(harmonyBefore));
            Assert.That(harmonyUnpatched, Is.GreaterThan(harmonyPatched));
            Assert.That(jalibAfter, Is.GreaterThan(jalibBefore));
        });
    }

    [Test]
    public void ManagedSessionChecksCompiledRegistryVersionReadersWithoutFrameThrottle()
    {
        var session = typeof(PcCompatManagedModSession);
        Assert.Multiple(() =>
        {
            Assert.That(
                session.GetField("_shimRegistryVersionReaders", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType,
                Is.EqualTo(typeof(Func<int>[])));
            Assert.That(
                session.GetField("_shimRecheckCountdown", BindingFlags.Instance | BindingFlags.NonPublic),
                Is.Null);
            Assert.That(
                session.GetMethod("ShimRegistryChanged", BindingFlags.Instance | BindingFlags.NonPublic),
                Is.Not.Null);
        });
    }

    private static class DiagnosticTarget
    {
        public static void Run()
        {
        }
    }

    private static class DiagnosticPatch
    {
        public static void Prefix()
        {
        }
    }
}

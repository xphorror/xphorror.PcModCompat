using System.Reflection;
using HarmonyLib;
using JALib.Core.Patch;

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

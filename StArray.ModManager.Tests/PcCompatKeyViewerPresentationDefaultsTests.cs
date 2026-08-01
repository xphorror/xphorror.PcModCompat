using System.Collections;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatKeyViewerPresentationDefaultsTests
{
    [Test]
    public void FillsOnlyMissingTouchLabelsAndPreservesModCustomization()
    {
        IList labels = new string?[] { null, "Custom", "", " " };

        Assert.That(PcCompatKeyViewerPresentationDefaults.TryFill(
            labels,
            ["T1", "T2", "T3", "T4"],
            out var changed,
            out var error), Is.True, error);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(3));
            Assert.That(labels.Cast<string?>(), Is.EqualTo(
                new string?[] { "T1", "Custom", "T3", "T4" }));
        });
    }

    [Test]
    public void ShortCollectionFailsWithoutPartialMutation()
    {
        IList labels = new string?[] { null };

        Assert.That(PcCompatKeyViewerPresentationDefaults.TryFill(
            labels,
            ["T1", "T2"],
            out var changed,
            out var error), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.Zero);
            Assert.That(error, Does.Contain("1 entries"));
            Assert.That(labels[0], Is.Null);
        });
    }

    [Test]
    public void ProjectionRoundTripsTouchAndExternalLabelsWithoutChangingCustomization()
    {
        IList labels = new string?[] { null, "Custom", "", " " };
        var projection = new PcCompatKeyViewerPresentationProjection();

        Assert.That(projection.TryApply(
            labels,
            ["T1", "T2", "T3", "T4"],
            adoptLegacyTouchLabels: false,
            out var touchChanged,
            out var touchError), Is.True, touchError);
        Assert.That(labels.Cast<string?>(), Is.EqualTo(
            new string?[] { "T1", "Custom", "T3", "T4" }));
        Assert.That(touchChanged, Is.EqualTo(new[] { 0, 2, 3 }));

        Assert.That(projection.TryRestore(
            out var externalChanged,
            out var externalError), Is.True, externalError);
        Assert.That(labels.Cast<string?>(), Is.EqualTo(
            new string?[] { null, "Custom", "", " " }));
        Assert.That(externalChanged, Is.EqualTo(new[] { 0, 2, 3 }));

        Assert.That(projection.TryApply(
            labels,
            ["T1", "T2", "T3", "T4"],
            adoptLegacyTouchLabels: false,
            out var restored,
            out var restoreError), Is.True, restoreError);
        Assert.That(labels.Cast<string?>(), Is.EqualTo(
            new string?[] { "T1", "Custom", "T3", "T4" }));
        Assert.That(restored, Is.EqualTo(new[] { 0, 2, 3 }));
    }

    [Test]
    public void ProjectionAdoptsLabelsLeftByLegacyTouchInjection()
    {
        IList labels = new string?[] { "T1", "Named", "T3" };
        Assert.That(PcCompatKeyViewerPresentationDefaults.TryClearLegacyTouchLabels(
            labels,
            3,
            out var changed,
            out var error), Is.True, error);

        Assert.Multiple(() =>
        {
            Assert.That(labels.Cast<string?>(), Is.EqualTo(
                new string?[] { null, "Named", null }));
            Assert.That(changed, Is.EqualTo(new[] { 0, 2 }));
        });
    }

    [Test]
    public void ProjectionStopsOwningALabelChangedByTheMod()
    {
        IList labels = new string?[] { null };
        var projection = new PcCompatKeyViewerPresentationProjection();
        Assert.That(projection.TryApply(
            labels,
            ["T1"],
            adoptLegacyTouchLabels: false,
            out _,
            out var touchError), Is.True, touchError);

        labels[0] = "MOD";
        Assert.That(projection.TryApply(
            labels,
            ["A"],
            adoptLegacyTouchLabels: true,
            out var changed,
            out var externalError), Is.True, externalError);

        Assert.Multiple(() =>
        {
            Assert.That(labels[0], Is.EqualTo("MOD"));
            Assert.That(changed, Is.Empty);
        });
    }

    [TestCase(PcCompatInputIdentityKind.UnityKeyCode, "97", "A")]
    [TestCase(PcCompatInputIdentityKind.UnityKeyCode, "303", "RShift")]
    [TestCase(PcCompatInputIdentityKind.UnityKeyCode, "323", "M0")]
    [TestCase(PcCompatInputIdentityKind.WindowsVirtualKey, "90", "Z")]
    [TestCase(PcCompatInputIdentityKind.WindowsVirtualKey, "112", "F1")]
    [TestCase(PcCompatInputIdentityKind.WindowsVirtualKey, "162", "LCtrl")]
    public void FormatsConfiguredExternalIdentityAsAsciiKeyName(
        PcCompatInputIdentityKind kind,
        string value,
        string expected)
    {
        Assert.That(PcCompatKeyViewerLabelFormatter.Format(new PcCompatInputIdentity
        {
            Kind = kind,
            Value = value
        }), Is.EqualTo(expected));
    }
}

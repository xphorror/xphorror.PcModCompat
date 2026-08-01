using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatKeyViewerBehaviorScannerTests
{
    [Test]
    public void RealModProducesBoundDiagnosticAdapterWithoutAutoEnable()
    {
        var modFolder = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "..", "JipperResourcePack_release"));
        Assume.That(Directory.Exists(modFolder), Is.True, $"missing sample mod dir: {modFolder}");
        Assert.That(
            PcModManifestReader.TryRead(modFolder, out var manifest, out var manifestError),
            Is.True,
            manifestError);

        var result = PcCompatKeyViewerBehaviorScanner.Scan(
            manifest,
            new string('a', 64));

        Assert.That(result.HasCandidate, Is.True, string.Join(
            Environment.NewLine,
            result.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
        var document = result.Adapter!;
        var feature = document.Features.Single();
        Assert.Multiple(() =>
        {
            Assert.That(
                feature.SourceProfiles.Select(profile => profile.Kind),
                Does.Contain(PcCompatKeyViewerInputProfileKind.LegacyUnityPolling));
            Assert.That(
                feature.SourceProfiles.Select(profile => profile.Kind),
                Does.Contain(PcCompatKeyViewerInputProfileKind.Win32Polling));
            Assert.That(feature.Capabilities.Input.Status,
                Is.EqualTo(PcCompatAdapterEvidenceStatus.Proven));
            Assert.That(feature.Capabilities.Lane.Status,
                Is.EqualTo(PcCompatAdapterEvidenceStatus.Probable));
            Assert.That(feature.Capabilities.Lane.FirstBreak,
                Does.Contain("same-index consumers"));
            Assert.That(feature.Roles.Select(role => role.Role),
                Does.Contain("BindingProvider"));
            Assert.That(feature.Roles
                    .Where(role => role.Role == "BindingProvider")
                    .Select(role => role.MemberName),
                Is.SupersetOf(new[] { "GetKeyCode", "GetGhostKeyCode" }));
            Assert.That(feature.Roles.Select(role => role.Role),
                Does.Contain("IdentityTransform"));
            Assert.That(feature.Roles.Single(role => role.Role == "IdentityTransform")
                    .Evidence.Status,
                Is.EqualTo(PcCompatAdapterEvidenceStatus.Proven));
            Assert.That(feature.Roles.Select(role => role.Role),
                Does.Contain("LabelProvider"));
            Assert.That(feature.Roles.Select(role => role.Role),
                Does.Contain("LaneCollection"));
            Assert.That(feature.Capabilities.Persistence.Status,
                Is.Not.EqualTo(PcCompatAdapterEvidenceStatus.Proven));
            Assert.That(PcCompatKeyViewerAdapterValidator.IsCoreReady(feature), Is.False);
            Assert.That(PcCompatKeyViewerAdapterValidator.Validate(document).IsValid, Is.True);
        });
    }

    [Test]
    public void RealModGhostProviderRecoversToOnlyUsableCountedKeyArray()
    {
        var modFolder = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "..", "JipperResourcePack_release"));
        Assume.That(Directory.Exists(modFolder), Is.True, $"missing sample mod dir: {modFolder}");
        Assert.That(PcModManifestReader.TryRead(modFolder, out var manifest, out var error),
            Is.True, error);
        var adapter = PcCompatKeyViewerBehaviorScanner.Scan(
            manifest,
            new string('c', 64)).Adapter!;
        var feature = adapter.Features.Single();
        var overrides = PcCompatKeyViewerOverrideStore.CreateFor(adapter);
        var featureOverride = overrides.Features.Single();
        featureOverride.Enabled = true;
        featureOverride.InputMode = PcCompatKeyViewerInputMode.Auto;
        featureOverride.TouchLaneCount = 6;
        var ghost = feature.Roles.Single(role =>
            role.Role == "BindingProvider" && role.MemberName == "GetGhostKeyCode");
        featureOverride.Roles.Add(new PcCompatKeyViewerRoleOverride
        {
            Role = ghost.Role,
            AssemblyName = ghost.AssemblyName,
            TypeName = ghost.TypeName,
            MemberName = ghost.MemberName,
            MemberKind = ghost.MemberKind
        });

        var result = PcCompatKeyViewerBindingPlanLowerer.Lower(
            adapter,
            overrides,
            role => role.MemberName switch
            {
                "GetGhostKeyCode" => (true, new int[16], null),
                "GetFootKeyCode" => (true, new[] { 97, 98, 99, 100 }, null),
                "GetKeyCode" => (true, new[] { 97, 98, 99, 100, 101, 102 }, null),
                _ => (false, Array.Empty<int>(), "unexpected provider")
            });

        Assert.That(result.Plans.Single().BindingProviderCandidateKey,
            Does.EndWith("!Method!GetKeyCode"));
        Assert.That(result.Issues.Single(), Does.Contain("recovered"));
    }

    [Test]
    public void RealModScanIsDeterministicAndScannerHasNoModIdentitySeed()
    {
        var modFolder = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "..", "JipperResourcePack_release"));
        Assume.That(Directory.Exists(modFolder), Is.True, $"missing sample mod dir: {modFolder}");
        Assert.That(PcModManifestReader.TryRead(modFolder, out var manifest, out _), Is.True);

        var first = PcCompatKeyViewerBehaviorScanner.Scan(manifest, new string('b', 64));
        var second = PcCompatKeyViewerBehaviorScanner.Scan(manifest, new string('b', 64));

        Assert.That(first.Adapter, Is.Not.Null);
        Assert.That(second.Adapter, Is.Not.Null);
        Assert.That(second.Adapter!.ToJson(), Is.EqualTo(first.Adapter!.ToJson()));

        var scannerPath = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "..",
            "xphorror.PcModCompat", "src", "PcCompatKeyViewerBehaviorScanner.cs"));
        var scannerSource = File.ReadAllText(scannerPath);
        Assert.That(scannerSource, Does.Not.Contain("JipperResourcePack"));
    }
}

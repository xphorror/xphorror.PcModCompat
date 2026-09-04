using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatKeyViewerBehaviorScannerTests
{
    [Test]
    public void RealJipperKeyViewerPublishesAdapterEvidence()
    {
        var modFolder = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "..", "JipperKeyViewer-AssetBundle"));
        Assert.That(
            PcModManifestReader.TryRead(modFolder, out var manifest, out var manifestError),
            Is.True,
            manifestError);

        var proxyDirectory = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "..", "xphorror.PcModCompat", "out", "interop",
            "proxy_assemblies"));
        var proxyAssemblyNames = Directory.EnumerateFiles(proxyDirectory, "*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!);
        var assemblyCatalog = PcCompatManagedAssemblyCatalog.Discover(
            manifest,
            proxyAssemblyNames);
        var result = PcCompatKeyViewerBehaviorScanner.Scan(
            manifest,
            new string('d', 64),
            assemblyCatalog);
        foreach (var diagnosticFeature in result.Adapter?.Features ?? [])
        {
            var matrix = diagnosticFeature.Capabilities;
            TestContext.Progress.WriteLine(
                $"feature={diagnosticFeature.Id} core={PcCompatKeyViewerAdapterValidator.IsCoreReady(diagnosticFeature)} " +
                $"input={matrix.Input.Status} lane={matrix.Lane.Status} " +
                $"transition={matrix.Transition.Status} count={matrix.Count.Status} " +
                $"presentation={matrix.Presentation.Status} visibility={matrix.Visibility.Status} " +
                $"activation={matrix.InputActivation.Status} persistence={matrix.Persistence.Status} " +
                $"groups={diagnosticFeature.LaneGroups.Count} lanes={diagnosticFeature.LaneGroups.Sum(group => group.Lanes.Count)} " +
                $"transforms={diagnosticFeature.IdentityTransforms.Count} roles=" +
                string.Join(',', diagnosticFeature.Roles.Select(role =>
                    $"{role.Role}:{role.MemberName}:{role.Evidence.Status}")));
        }

        Assert.That(result.HasCandidate, Is.True, string.Join(
            Environment.NewLine,
            result.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
        var adapter = PcCompatKeyViewerAdapterDocument.FromJson(result.Adapter!.ToJson());
        Assert.That(adapter, Is.Not.Null, "serialized Adapter must reload before lowering");
        var feature = adapter.Features.Single();
        var providers = feature.Roles
            .Where(role => role.Role == "BindingProvider")
            .ToArray();
        var listener = feature.Roles.Single(role => role.Role == "InputListenerMethod");
        var transform = feature.IdentityTransforms.Single();
        Assert.Multiple(() =>
        {
            Assert.That(feature.Capabilities.Input.Status,
                Is.EqualTo(PcCompatAdapterEvidenceStatus.Proven));
            Assert.That(feature.Capabilities.Lane.Status,
                Is.EqualTo(PcCompatAdapterEvidenceStatus.Proven));
            Assert.That(feature.Capabilities.Transition.Status,
                Is.EqualTo(PcCompatAdapterEvidenceStatus.Proven));
            Assert.That(feature.Capabilities.Count.Status,
                Is.EqualTo(PcCompatAdapterEvidenceStatus.Proven));
            Assert.That(listener.MemberName, Is.EqualTo("ProcessKeyGroup"));
            Assert.That(listener.Evidence.Status,
                Is.EqualTo(PcCompatAdapterEvidenceStatus.Proven));
            Assert.That(feature.Roles
                    .Where(role => role.Role == "HeldState")
                    .Select(role => role.MemberName),
                Is.EqualTo(new[] { "isPressed" }));
            Assert.That(feature.Roles
                    .Where(role => role.Role == "CountState")
                    .Select(role => role.MemberName),
                Is.EqualTo(new[] { "Count" }));
            Assert.That(feature.Roles
                    .Where(role => role.Role == "TotalState")
                    .Select(role => role.MemberName),
                Does.Contain("TotalCount"));
            Assert.That(providers.Select(role => role.MemberName), Does.Contain("GetKeyCode"));
            Assert.That(providers.Select(role => role.MemberName), Does.Contain("GetFootKeyCode"));
            Assert.That(
                providers.Single(role => role.MemberName == "GetKeyCode").ConsumerLaneBase,
                Is.Zero,
                "the primary input group must retain its proven zero lane origin through JSON");
            Assert.That(
                providers.Single(role => role.MemberName == "GetFootKeyCode").ConsumerLaneBase,
                Is.EqualTo(24),
                "the appended input group must not compete with the primary touch projection");
            Assert.That(transform.Kind,
                Is.EqualTo(PcCompatKeyViewerIdentityTransformKind.UnityKeyCodeIdentity));
            Assert.That(PcCompatKeyViewerOverrideStore.SupportsAutomaticInput(feature), Is.True);
        });

        var overrides = PcCompatKeyViewerOverrideStore.CreateRecommendedFor(adapter);
        Assert.That(overrides.Features.Single().Enabled, Is.True);
        var lowering = PcCompatKeyViewerBindingPlanLowerer.Lower(
            adapter,
            overrides,
            (role, requiredCount) => role.MemberName switch
            {
                "GetKeyCode" => (true, Enumerable.Range(1, requiredCount).ToArray(), null),
                "GetFootKeyCode" => (true, Enumerable.Range(201, requiredCount).ToArray(), null),
                _ => (false, Array.Empty<int>(), "unexpected provider")
            });
        Assert.Multiple(() =>
        {
            Assert.That(lowering.Issues, Is.Empty);
            Assert.That(lowering.Plans, Has.Count.EqualTo(1));
            Assert.That(lowering.Plans.Single().BindingProviderCandidateKey,
                Does.EndWith("!Method!GetKeyCode"));
            Assert.That(lowering.Plans.Single().Lanes, Has.Count.EqualTo(10));
            Assert.That(
                lowering.Plans.Single().Lanes
                    .SelectMany(lane => lane.Identities)
                    .Select(identity => identity.Value),
                Is.EqualTo(Enumerable.Range(1, 10).Select(value => value.ToString())));
        });
    }

    [Test]
    public void RealModProducesBoundDiagnosticAdapterWithoutAutoEnable()
    {
        var modFolder = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "..", "JipperResourcePack_release"));
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
            Assert.That(feature.Roles.Select(role => role.Role),
                Does.Contain("HeldState"));
            Assert.That(feature.Roles.Select(role => role.Role),
                Does.Contain("CountState"));
            Assert.That(feature.Roles.Select(role => role.Role),
                Does.Contain("TotalState"));
            Assert.That(feature.Roles.Select(role => role.Role),
                Does.Contain("KpsWindow"));
            Assert.That(feature.Roles.Select(role => role.Role),
                Does.Contain("KpsState"));
            Assert.That(feature.Roles.Select(role => role.Role),
                Does.Contain("PersistenceSink"));
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
            (role, _) => role.MemberName switch
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
        Assert.That(scannerSource, Does.Not.Contain("JipperKeyViewer"));
    }
}

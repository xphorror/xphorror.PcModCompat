using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatKeyViewerOverrideTests
{
    [Test]
    public void OverrideRoundTripsAndOnlyAcceptsScannedRoleCandidate()
    {
        var adapter = CreateAdapter();
        var document = PcCompatKeyViewerOverrideStore.CreateFor(adapter);
        var featureOverride = document.Features.Single();
        featureOverride.Enabled = true;
        featureOverride.InputMode = PcCompatKeyViewerInputMode.Touch;
        featureOverride.TouchLaneCount = 4;
        featureOverride.Roles.Add(ToOverride(adapter.Features[0].Roles[0]));

        var root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "pccompat-keyviewer-overrides-" + Guid.NewGuid().ToString("N"));
        try
        {
            PcCompatKeyViewerOverrideStore.Save(root, document);
            var loaded = PcCompatKeyViewerOverrideStore.Load(root, out var loadError);
            var validation = PcCompatKeyViewerOverrideStore.Validate(loaded!, adapter);

            Assert.Multiple(() =>
            {
                Assert.That(loadError, Is.Null);
                Assert.That(loaded, Is.Not.Null);
                Assert.That(validation.IsValid, Is.True,
                    string.Join(Environment.NewLine, validation.Errors));
                Assert.That(loaded!.Features.Single().InputMode,
                    Is.EqualTo(PcCompatKeyViewerInputMode.Touch));
                Assert.That(loaded.Features.Single().Roles.Single().CandidateKey,
                    Is.EqualTo(featureOverride.Roles.Single().CandidateKey));
            });
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void FabricatedCandidateAndChangedFingerprintInvalidateOverride()
    {
        var adapter = CreateAdapter();
        var document = PcCompatKeyViewerOverrideStore.CreateFor(adapter);
        document.Features.Single().Roles.Add(new PcCompatKeyViewerRoleOverride
        {
            Role = "InputListenerMethod",
            AssemblyName = "Mod",
            TypeName = "Injected.NotScanned",
            MemberName = "Update",
            MemberKind = "Method"
        });

        var fabricated = PcCompatKeyViewerOverrideStore.Validate(document, adapter);
        var changedAdapter = CreateAdapter(packageSha256: new string('b', 64));
        var stale = PcCompatKeyViewerOverrideStore.Validate(document, changedAdapter);

        Assert.Multiple(() =>
        {
            Assert.That(fabricated.IsValid, Is.False);
            Assert.That(fabricated.Errors,
                Has.Some.Contains("selected candidate is not in the scan result"));
            Assert.That(stale.IsValid, Is.False);
            Assert.That(stale.Errors, Has.Some.Contains("adapter package changed"));
        });
    }

    [Test]
    public void StaleFingerprintRebasePreservesSafeSettingsAndDropsUnknownRoles()
    {
        var previousAdapter = CreateAdapter(
            packageSha256: new string('a', 64),
            autoConfigurable: true);
        var stale = PcCompatKeyViewerOverrideStore.CreateFor(previousAdapter);
        var staleFeature = stale.Features.Single();
        staleFeature.Enabled = true;
        staleFeature.InputMode = PcCompatKeyViewerInputMode.Hybrid;
        staleFeature.TouchLaneCount = 4;
        staleFeature.CompatibleFallbackEnabled = true;
        var retainedRole = previousAdapter.Features.Single().Roles.Single(role =>
            role.Role == "BindingProvider");
        staleFeature.Roles.Add(ToOverride(retainedRole));
        staleFeature.Roles.Add(new PcCompatKeyViewerRoleOverride
        {
            Role = "InputListenerMethod",
            AssemblyName = "Mod",
            TypeName = "Removed.Listener",
            MemberName = "Update",
            MemberKind = "Method"
        });

        var currentAdapter = CreateAdapter(
            packageSha256: new string('b', 64),
            autoConfigurable: true);
        var rebased = PcCompatKeyViewerOverrideStore.TryRebase(
            stale,
            currentAdapter,
            out var current,
            out var summary);

        Assert.That(rebased, Is.True, summary);
        Assert.That(current, Is.Not.Null);
        var validation = PcCompatKeyViewerOverrideStore.Validate(current!, currentAdapter);
        var currentFeature = current!.Features.Single();
        Assert.Multiple(() =>
        {
            Assert.That(validation.IsValid, Is.True,
                string.Join(Environment.NewLine, validation.Errors));
            Assert.That(current.PackageSha256, Is.EqualTo(currentAdapter.PackageSha256));
            Assert.That(currentFeature.Enabled, Is.True);
            Assert.That(currentFeature.InputMode, Is.EqualTo(PcCompatKeyViewerInputMode.Hybrid));
            Assert.That(currentFeature.TouchLaneCount, Is.EqualTo(4));
            Assert.That(currentFeature.CompatibleFallbackEnabled, Is.True);
            Assert.That(currentFeature.Roles, Has.Count.EqualTo(1));
            Assert.That(currentFeature.Roles.Single().CandidateKey,
                Is.EqualTo(ToOverride(retainedRole).CandidateKey));
            Assert.That(summary, Does.Contain("retainedRoles=1"));
            Assert.That(summary, Does.Contain("droppedRoles=1"));
        });
    }

    [Test]
    public void TouchProjectionPreservesTouchIdentityAndSourceOrdering()
    {
        var adapter = CreateAdapter();
        var feature = adapter.Features.Single();
        var featureOverride = PcCompatKeyViewerOverrideStore.CreateFor(adapter)
            .Features.Single();
        featureOverride.Enabled = true;
        featureOverride.InputMode = PcCompatKeyViewerInputMode.Hybrid;
        featureOverride.TouchLaneCount = 4;
        var snapshot = new PcCompatInputHudSnapshot
        {
            ProviderAvailable = true,
            SourceGeneration = 17,
            SourceSequence = 91,
            TouchLaneCount = 4,
            TouchLaneHeldMask = 0b1001,
            TouchLaneLastDownMask = 0b1000,
            TouchLaneLastUpMask = 0b0001
        };

        var projection = PcCompatKeyViewerInputProjector.ProjectTouch(
            feature,
            featureOverride,
            snapshot,
            PcCompatKeyViewerInputOrigin.AsyncInput);

        Assert.Multiple(() =>
        {
            Assert.That(projection.IsTouchIdentity, Is.True);
            Assert.That(projection.Origin, Is.EqualTo(PcCompatKeyViewerInputOrigin.AsyncInput));
            Assert.That(projection.Mode, Is.EqualTo(PcCompatKeyViewerInputMode.Hybrid));
            Assert.That(projection.LaneCount, Is.EqualTo(4));
            Assert.That(projection.HeldMask, Is.EqualTo(0b1001));
            Assert.That(projection.LastDownMask, Is.EqualTo(0b1000));
            Assert.That(projection.LastUpMask, Is.EqualTo(0b0001));
            Assert.That(projection.SourceGeneration, Is.EqualTo(17));
            Assert.That(projection.SourceSequence, Is.EqualTo(91));
        });
    }

    [Test]
    public void RecommendedConfigurationEnablesOnlyProvableAutomaticInput()
    {
        var automatic = PcCompatKeyViewerOverrideStore.CreateRecommendedFor(
            CreateAdapter(autoConfigurable: true));
        var diagnosticOnly = PcCompatKeyViewerOverrideStore.CreateRecommendedFor(
            CreateAdapter());

        Assert.Multiple(() =>
        {
            Assert.That(automatic.Features.Single().Enabled, Is.True);
            Assert.That(automatic.Features.Single().InputMode,
                Is.EqualTo(PcCompatKeyViewerInputMode.Auto));
            Assert.That(automatic.Features.Single().TouchLaneCount, Is.EqualTo(10));
            Assert.That(automatic.Features.Single().Roles, Is.Empty);
            Assert.That(diagnosticOnly.Features.Single().Enabled, Is.False);
        });
    }

    [Test]
    public void UniqueOptionalRoleNeedsNoManualConfirmation()
    {
        var adapter = CreateAdapter();
        var feature = adapter.Features.Single();
        var overrideDocument = PcCompatKeyViewerOverrideStore.CreateFor(adapter);
        var resolved = PcCompatKeyViewerOverrideStore.ResolveSelectedOrUniqueRole(
            feature,
            overrideDocument.Features.Single(),
            "InputListenerMethod");

        Assert.That(resolved?.MemberName, Is.EqualTo("Update"));
    }

    private static PcCompatKeyViewerRoleOverride ToOverride(PcCompatKeyViewerRoleBinding role)
        => new()
        {
            Role = role.Role,
            AssemblyName = role.AssemblyName,
            TypeName = role.TypeName,
            MemberName = role.MemberName,
            MemberKind = role.MemberKind
        };

    private static PcCompatKeyViewerAdapterDocument CreateAdapter(
        string? packageSha256 = null,
        bool autoConfigurable = false)
    {
        var proven = new PcCompatAdapterEvidence
        {
            Status = PcCompatAdapterEvidenceStatus.Proven,
            Evidence = ["test graph"]
        };
        var roles = new List<PcCompatKeyViewerRoleBinding>
        {
            new()
            {
                Role = "InputListenerMethod",
                AssemblyName = "Mod",
                TypeName = "Example.Listener",
                MemberName = "Update",
                MemberKind = "Method",
                Evidence = proven
            }
        };
        var identityTransforms = new List<PcCompatKeyViewerIdentityTransform>();
        if (autoConfigurable)
        {
            roles.Add(new PcCompatKeyViewerRoleBinding
            {
                Role = "BindingProvider",
                AssemblyName = "Mod",
                TypeName = "Example.Listener",
                MemberName = "GetKeys",
                MemberKind = "Method",
                Evidence = proven
            });
            roles.Add(new PcCompatKeyViewerRoleBinding
            {
                Role = "IdentityTransform",
                AssemblyName = "Mod",
                TypeName = "Example.Listener",
                MemberName = "CheckKey",
                MemberKind = "Method",
                Evidence = proven
            });
            identityTransforms.Add(new PcCompatKeyViewerIdentityTransform
            {
                CandidateKey = PcCompatKeyViewerOverrideStore.GetCandidateKey(
                    "Mod", "Example.Listener", "CheckKey", "Method"),
                Kind = PcCompatKeyViewerIdentityTransformKind.UnityWindowsThresholdSplit,
                Threshold = 0x1000,
                Offset = 0x1000,
                Evidence = proven
            });
        }
        var feature = new PcCompatKeyViewerFeatureAdapter
        {
            Id = "keyviewer",
            DisplayName = "Key Viewer",
            SourceProfiles =
            [
                new PcCompatKeyViewerSourceProfile
                {
                    Id = "legacy",
                    Kind = PcCompatKeyViewerInputProfileKind.LegacyUnityPolling,
                    EntryPoints = ["Input.GetKeyDown"],
                    Evidence = proven
                }
            ],
            LaneGroups =
            [
                new PcCompatKeyViewerLaneGroup
                {
                    Id = "touch",
                    Lanes = Enumerable.Range(1, 4).Select(index =>
                        new PcCompatKeyViewerLane
                        {
                            Id = $"touch-{index}",
                            DisplayLabel = $"T{index}",
                            Binding = new PcCompatLaneBinding
                            {
                                Kind = PcCompatLaneBindingKind.TouchLane,
                                TouchLane = index,
                                SourceProfileId = "legacy"
                            }
                        }).ToArray()
                }
            ],
            Roles = roles,
            IdentityTransforms = identityTransforms,
            Visibility = new PcCompatKeyViewerPredicate
            {
                Kind = "FeatureEnabled",
                Expression = "Enabled",
                Evidence = proven
            },
            InputActivation = new PcCompatKeyViewerPredicate
            {
                Kind = "FeatureEnabled",
                Expression = "Enabled",
                Evidence = proven
            },
            CountSemantics = new PcCompatKeyViewerCountSemantics
            {
                Clock = "Stopwatch.Monotonic",
                ResetEntryPoint = "Settings.Reset"
            },
            Capabilities = new PcCompatKeyViewerEvidenceMatrix
            {
                Input = proven,
                Lane = proven,
                Transition = proven,
                Count = proven,
                Kps = proven,
                Rain = proven,
                Presentation = proven,
                Visibility = proven,
                InputActivation = proven,
                Settings = proven,
                Persistence = proven
            }
        };
        return new PcCompatKeyViewerAdapterDocument
        {
            ModId = "Example",
            PackageSha256 = packageSha256 ?? new string('a', 64),
            TargetGameRevision = 143,
            ProxySurfaceHash = new string('f', 64),
            Assemblies =
            [
                new PcCompatAdapterAssemblyFingerprint
                {
                    AssemblyName = "Mod",
                    Sha256 = new string('1', 64),
                    Mvid = "1caa829e-37fa-4ad2-918f-09f8dcc61248"
                }
            ],
            Features = [feature]
        };
    }
}

using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatKeyViewerBindingPlanLowererTests
{
    [Test]
    public void ProvenThresholdTransformLowersDynamicUnityAndWindowsIdentities()
    {
        var (adapter, overrides) = CreateConfiguration();

        var result = PcCompatKeyViewerBindingPlanLowerer.Lower(
            adapter,
            overrides,
            (_, _) => (true, [97, 0x1000 + 0x5A], null));

        Assert.That(result.Issues, Is.Empty);
        var plan = result.Plans.Single();
        Assert.Multiple(() =>
        {
            Assert.That(plan.BindingProviderCandidateKey,
                Is.EqualTo(overrides.Features.Single().Roles.Single().CandidateKey));
            Assert.That(plan.Lanes[0].Identities.Single().Kind,
                Is.EqualTo(PcCompatInputIdentityKind.UnityKeyCode));
            Assert.That(plan.Lanes[0].Identities.Single().Value, Is.EqualTo("97"));
            Assert.That(plan.Lanes[1].Identities.Single().Kind,
                Is.EqualTo(PcCompatInputIdentityKind.WindowsVirtualKey));
            Assert.That(plan.Lanes[1].Identities.Single().Value, Is.EqualTo("90"));
        });
    }

    /// <summary>
    /// The lowerer already invokes the provider, so it is the only place that can report the raw
    /// sequence without a second call into MOD code. The change watcher needs that sequence as its
    /// baseline.
    /// </summary>
    [Test]
    public void LoweringReportsOnlyTheSelectedProviderSequenceForChangeWatching()
    {
        var (adapter, overrides) = CreateConfiguration();
        var feature = adapter.Features.Single();
        ((List<PcCompatKeyViewerRoleBinding>)feature.Roles).Add(new PcCompatKeyViewerRoleBinding
        {
            Role = "BindingProvider",
            AssemblyName = "TestMod",
            TypeName = "TestMod.Viewer",
            MemberName = "GetGhostKeys",
            MemberKind = "Method",
            Evidence = new PcCompatAdapterEvidence
            {
                Status = PcCompatAdapterEvidenceStatus.Probable,
                Evidence = ["alternate ghost provider"],
                FirstBreak = "runtime provider"
            }
        });

        var result = PcCompatKeyViewerBindingPlanLowerer.Lower(
            adapter,
            overrides,
            (role, _) => role.MemberName == "GetKeys"
                ? (true, [97, 98], null)
                : (true, [0, 0], null));

        var resolved = result.ResolvedProviders.Single();
        Assert.Multiple(() =>
        {
            // Rejected candidates must not be watched: polling them every interval would run MOD
            // code that backs no live plan.
            Assert.That(resolved.Role.MemberName, Is.EqualTo("GetKeys"));
            Assert.That(resolved.FeatureId, Is.EqualTo(feature.Id));
            Assert.That(resolved.Values, Is.EqualTo(new[] { 97, 98 }));
        });
    }

    [Test]
    public void RecoveredProviderIsTheOneReportedForChangeWatching()
    {
        var (adapter, overrides) = CreateConfiguration();
        var feature = adapter.Features.Single();
        ((List<PcCompatKeyViewerRoleBinding>)feature.Roles).Add(new PcCompatKeyViewerRoleBinding
        {
            Role = "BindingProvider",
            AssemblyName = "TestMod",
            TypeName = "TestMod.Viewer",
            MemberName = "GetMainKeys",
            MemberKind = "Method",
            Evidence = new PcCompatAdapterEvidence
            {
                Status = PcCompatAdapterEvidenceStatus.Probable,
                Evidence = ["alternate counted provider"],
                FirstBreak = "runtime provider"
            }
        });

        var result = PcCompatKeyViewerBindingPlanLowerer.Lower(
            adapter,
            overrides,
            (role, _) => role.MemberName == "GetMainKeys"
                ? (true, [97, 98], null)
                : (true, [0, 0], null));

        var resolved = result.ResolvedProviders.Single();
        Assert.Multiple(() =>
        {
            Assert.That(resolved.Role.MemberName, Is.EqualTo("GetMainKeys"));
            Assert.That(resolved.Values, Is.EqualTo(new[] { 97, 98 }));
        });
    }

    /// <summary>
    /// External mode publishes no consumer plan but still renders labels from the provider, so its
    /// sequence has to be watched too or the labels go stale.
    /// </summary>
    [Test]
    public void PresentationOnlyFeaturesAreStillReportedForChangeWatching()
    {
        var (adapter, overrides) = CreateConfiguration();
        overrides.Features.Single().InputMode = PcCompatKeyViewerInputMode.External;

        var result = PcCompatKeyViewerBindingPlanLowerer.Lower(
            adapter,
            overrides,
            (_, _) => (true, [97, 98], null));

        Assert.That(result.Plans, Is.Empty);
        Assert.That(result.ResolvedProviders.Single().Values, Is.EqualTo(new[] { 97, 98 }));
    }

    [Test]
    public void FailedLoweringReportsNoProviderSequence()
    {
        var (adapter, overrides) = CreateConfiguration();

        var result = PcCompatKeyViewerBindingPlanLowerer.Lower(
            adapter,
            overrides,
            (_, _) => (true, [97], null));

        Assert.That(result.Plans, Is.Empty);
        Assert.That(result.ResolvedProviders, Is.Empty);
    }

    [Test]
    public void ExternalModeBuildsPresentationPlanWithoutRegisteringTouchConsumerPlan()
    {
        var (adapter, overrides) = CreateConfiguration();
        overrides.Features.Single().InputMode = PcCompatKeyViewerInputMode.External;

        var result = PcCompatKeyViewerBindingPlanLowerer.Lower(
            adapter,
            overrides,
            (_, _) => (true, [97, 0x1000 + 0x5A], null));

        Assert.Multiple(() =>
        {
            Assert.That(result.Plans, Is.Empty);
            Assert.That(result.PresentationPlans, Has.Count.EqualTo(1));
            Assert.That(result.PresentationPlans.Single().Lanes
                .Select(lane => PcCompatKeyViewerLabelFormatter.Format(
                    lane.Identities.Single())),
                Is.EqualTo(new[] { "A", "Z" }));
            Assert.That(result.PresentationIssues, Is.Empty);
        });
    }

    [Test]
    public void MissingTransformProofFailsClosed()
    {
        var (adapter, overrides) = CreateConfiguration(includeTransform: false);

        var result = PcCompatKeyViewerBindingPlanLowerer.Lower(
            adapter,
            overrides,
            (_, _) => (true, [97, 98], null));

        Assert.That(result.Plans, Is.Empty);
        Assert.That(result.Issues.Single(), Does.Contain("proven IdentityTransform"));
    }

    [Test]
    public void ProviderMustCoverEveryConfiguredTouchLane()
    {
        var (adapter, overrides) = CreateConfiguration();

        var result = PcCompatKeyViewerBindingPlanLowerer.Lower(
            adapter,
            overrides,
            (_, _) => (true, [97], null));

        Assert.That(result.Plans, Is.Empty);
        Assert.That(result.Issues.Single(), Does.Contain("2 touch lanes"));
    }

    [Test]
    public void MultipleDisplayLanesMayShareOnePhysicalIdentity()
    {
        var (adapter, overrides) = CreateConfiguration();

        var result = PcCompatKeyViewerBindingPlanLowerer.Lower(
            adapter,
            overrides,
            (_, _) => (true, [97, 97], null));

        Assert.That(result.Issues, Is.Empty);
        Assert.That(result.Plans.Single().Lanes
            .SelectMany(lane => lane.Identities)
            .Select(identity => identity.Value), Is.EqualTo(new[] { "97", "97" }));
    }

    [Test]
    public void RegistryAcceptsVerifiedRewiredActionIdentities()
    {
        var (adapter, overrides) = CreateConfiguration();
        var provider = overrides.Features.Single().Roles.Single(role => role.Role == "BindingProvider");
        var plan = new PcCompatKeyViewerLoweredConsumerPlan
        {
            ModId = adapter.ModId,
            PackageSha256 = adapter.PackageSha256,
            ProxySurfaceHash = adapter.ProxySurfaceHash,
            TargetGameRevision = adapter.TargetGameRevision,
            FeatureId = adapter.Features.Single().Id,
            BindingProviderCandidateKey = provider.CandidateKey,
            Lanes =
            [
                new PcCompatKeyViewerLoweredLaneBinding
                {
                    Lane = 0,
                    Identities =
                    [
                        new PcCompatInputIdentity
                        {
                            Kind = PcCompatInputIdentityKind.ActionId,
                            Value = "97"
                        }
                    ]
                },
                new PcCompatKeyViewerLoweredLaneBinding
                {
                    Lane = 1,
                    Identities =
                    [
                        new PcCompatInputIdentity
                        {
                            Kind = PcCompatInputIdentityKind.ActionId,
                            Value = "98"
                        }
                    ]
                }
            ]
        };

        try
        {
            Assert.That(
                PcCompatKeyViewerLoweredConsumerPlanRegistry.Register(
                    adapter,
                    overrides,
                    plan,
                    out var error),
                Is.True,
                error);
        }
        finally
        {
            PcCompatKeyViewerLoweredConsumerPlanRegistry.Remove(adapter.ModId);
        }
    }

    [Test]
    public void InvalidSelectedProviderRecoversOnlyToOneUsableCandidate()
    {
        var (adapter, overrides) = CreateConfiguration();
        var feature = adapter.Features.Single();
        ((List<PcCompatKeyViewerRoleBinding>)feature.Roles).Add(new PcCompatKeyViewerRoleBinding
        {
            Role = "BindingProvider",
            AssemblyName = "TestMod",
            TypeName = "TestMod.Viewer",
            MemberName = "GetMainKeys",
            MemberKind = "Method",
            Evidence = new PcCompatAdapterEvidence
            {
                Status = PcCompatAdapterEvidenceStatus.Probable,
                Evidence = ["alternate counted provider"],
                FirstBreak = "runtime provider"
            }
        });

        var result = PcCompatKeyViewerBindingPlanLowerer.Lower(
            adapter,
            overrides,
            (role, _) => role.MemberName == "GetMainKeys"
                ? (true, [97, 98], null)
                : (true, [0, 0], null));

        var plan = result.Plans.Single();
        Assert.Multiple(() =>
        {
            Assert.That(plan.BindingProviderCandidateKey, Does.EndWith("!Method!GetMainKeys"));
            Assert.That(plan.Lanes.SelectMany(lane => lane.Identities).Select(identity => identity.Value),
                Is.EqualTo(new[] { "97", "98" }));
            Assert.That(result.Issues.Single(), Does.Contain("recovered"));
        });
    }

    [Test]
    public void InvalidSelectedProviderFailsClosedWhenAlternativesAreAmbiguous()
    {
        var (adapter, overrides) = CreateConfiguration();
        var feature = adapter.Features.Single();
        var roles = (List<PcCompatKeyViewerRoleBinding>)feature.Roles;
        foreach (var name in new[] { "GetMainKeysA", "GetMainKeysB" })
        {
            roles.Add(new PcCompatKeyViewerRoleBinding
            {
                Role = "BindingProvider",
                AssemblyName = "TestMod",
                TypeName = "TestMod.Viewer",
                MemberName = name,
                MemberKind = "Method",
                Evidence = new PcCompatAdapterEvidence
                {
                    Status = PcCompatAdapterEvidenceStatus.Probable,
                    Evidence = ["alternate provider"],
                    FirstBreak = "runtime provider"
                }
            });
        }

        var result = PcCompatKeyViewerBindingPlanLowerer.Lower(
            adapter,
            overrides,
            (role, _) => role.MemberName == "GetKeys"
                ? (true, [0, 0], null)
                : (true, role.MemberName == "GetMainKeysA" ? [97, 98] : [99, 100], null));

        Assert.That(result.Plans, Is.Empty);
        Assert.That(result.Issues.Single(), Does.Contain("2 usable alternatives"));
    }

    [Test]
    public void MissingSelectionAutomaticallyUsesOnlyUsableProvider()
    {
        var (adapter, overrides) = CreateConfiguration();
        var feature = adapter.Features.Single();
        ((List<PcCompatKeyViewerRoleBinding>)feature.Roles).Add(new PcCompatKeyViewerRoleBinding
        {
            Role = "BindingProvider",
            AssemblyName = "TestMod",
            TypeName = "TestMod.Viewer",
            MemberName = "GetGhostKeys",
            MemberKind = "Method",
            Evidence = new PcCompatAdapterEvidence
            {
                Status = PcCompatAdapterEvidenceStatus.Probable,
                Evidence = ["alternate ghost provider"],
                FirstBreak = "runtime provider"
            }
        });
        overrides.Features.Single().Roles.Clear();

        var result = PcCompatKeyViewerBindingPlanLowerer.Lower(
            adapter,
            overrides,
            (role, _) => role.MemberName == "GetKeys"
                ? (true, [97, 98], null)
                : (true, [0, 0], null));

        Assert.Multiple(() =>
        {
            Assert.That(result.Issues, Is.Empty);
            Assert.That(result.Plans.Single().BindingProviderCandidateKey,
                Does.EndWith("!Method!GetKeys"));
        });
        try
        {
            Assert.That(PcCompatKeyViewerLoweredConsumerPlanRegistry.Register(
                adapter,
                overrides,
                result.Plans.Single(),
                out var registryError), Is.True, registryError);
        }
        finally
        {
            PcCompatKeyViewerLoweredConsumerPlanRegistry.Remove(adapter.ModId);
        }
    }

    [Test]
    public void MissingSelectionFailsClosedWhenUsableProvidersAreAmbiguous()
    {
        var (adapter, overrides) = CreateConfiguration();
        var feature = adapter.Features.Single();
        ((List<PcCompatKeyViewerRoleBinding>)feature.Roles).Add(new PcCompatKeyViewerRoleBinding
        {
            Role = "BindingProvider",
            AssemblyName = "TestMod",
            TypeName = "TestMod.Viewer",
            MemberName = "GetOtherKeys",
            MemberKind = "Method",
            Evidence = new PcCompatAdapterEvidence
            {
                Status = PcCompatAdapterEvidenceStatus.Probable,
                Evidence = ["alternate provider"],
                FirstBreak = "runtime provider"
            }
        });
        overrides.Features.Single().Roles.Clear();

        var result = PcCompatKeyViewerBindingPlanLowerer.Lower(
            adapter,
            overrides,
            (role, _) => role.MemberName == "GetKeys"
                ? (true, [97, 98], null)
                : (true, [99, 100], null));

        Assert.That(result.Plans, Is.Empty);
        Assert.That(result.Issues.Single(), Does.Contain("found 2 usable candidates"));
    }

    [TestCase(PcCompatKeyViewerIdentityTransformKind.UnityKeyCodeIdentity, 97,
        PcCompatInputIdentityKind.UnityKeyCode, "97")]
    [TestCase(PcCompatKeyViewerIdentityTransformKind.WindowsVirtualKeyIdentity, 90,
        PcCompatInputIdentityKind.WindowsVirtualKey, "90")]
    [TestCase(PcCompatKeyViewerIdentityTransformKind.WindowsVirtualKeyOffset, 0x205A,
        PcCompatInputIdentityKind.WindowsVirtualKey, "90")]
    public void ProvenSingleDomainTransformsLowerWithoutThresholdGuessing(
        PcCompatKeyViewerIdentityTransformKind kind,
        int configured,
        PcCompatInputIdentityKind expectedKind,
        string expectedValue)
    {
        var transform = new PcCompatKeyViewerIdentityTransform
        {
            CandidateKey = "test",
            Kind = kind,
            Offset = kind == PcCompatKeyViewerIdentityTransformKind.WindowsVirtualKeyOffset
                ? 0x2000
                : 0,
            Evidence = new PcCompatAdapterEvidence
            {
                Status = PcCompatAdapterEvidenceStatus.Proven
            }
        };

        Assert.That(PcCompatKeyViewerBindingPlanLowerer.TryLowerIdentity(
            transform, configured, out var identity, out var error), Is.True, error);
        Assert.Multiple(() =>
        {
            Assert.That(identity.Kind, Is.EqualTo(expectedKind));
            Assert.That(identity.Value, Is.EqualTo(expectedValue));
        });
    }

    private static (
        PcCompatKeyViewerAdapterDocument Adapter,
        PcCompatKeyViewerOverrideDocument Overrides) CreateConfiguration(
        bool includeTransform = true)
    {
        var proven = new PcCompatAdapterEvidence
        {
            Status = PcCompatAdapterEvidenceStatus.Proven,
            Evidence = ["test proof"]
        };
        var probable = new PcCompatAdapterEvidence
        {
            Status = PcCompatAdapterEvidenceStatus.Probable,
            Evidence = ["test candidate"],
            FirstBreak = "runtime provider"
        };
        var roles = new List<PcCompatKeyViewerRoleBinding>
        {
            new()
            {
                Role = "BindingProvider",
                AssemblyName = "TestMod",
                TypeName = "TestMod.Viewer",
                MemberName = "GetKeys",
                MemberKind = "Method",
                Evidence = probable
            }
        };
        if (includeTransform)
        {
            roles.Add(new PcCompatKeyViewerRoleBinding
            {
                Role = "IdentityTransform",
                AssemblyName = "TestMod",
                TypeName = "TestMod.Viewer",
                MemberName = "CheckKey",
                MemberKind = "Method",
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
                    EntryPoints = ["UnityEngine.Input.GetKey"],
                    Evidence = proven
                }
            ],
            LaneGroups =
            [
                new PcCompatKeyViewerLaneGroup
                {
                    Id = "dynamic",
                    Lanes =
                    [
                        new PcCompatKeyViewerLane
                        {
                            Id = "runtime",
                            DisplayLabel = "Configured",
                            Binding = new PcCompatLaneBinding
                            {
                                Kind = PcCompatLaneBindingKind.Wildcard,
                                SourceProfileId = "legacy"
                            }
                        }
                    ]
                }
            ],
            Roles = roles,
            IdentityTransforms = includeTransform
                ?
                [
                    new PcCompatKeyViewerIdentityTransform
                    {
                        CandidateKey = PcCompatKeyViewerOverrideStore.GetCandidateKey(
                            "TestMod",
                            "TestMod.Viewer",
                            "CheckKey",
                            "Method"),
                        Kind = PcCompatKeyViewerIdentityTransformKind.UnityWindowsThresholdSplit,
                        Threshold = 0x1000,
                        Offset = 0x1000,
                        Evidence = proven
                    }
                ]
                : Array.Empty<PcCompatKeyViewerIdentityTransform>(),
            Visibility = new PcCompatKeyViewerPredicate
            {
                Kind = "LifecyclePair",
                Expression = "enable/disable",
                Evidence = probable
            },
            InputActivation = new PcCompatKeyViewerPredicate
            {
                Kind = "LoopPredicate",
                Expression = "listener",
                Evidence = probable
            },
            CountSemantics = new PcCompatKeyViewerCountSemantics
            {
                Clock = "Monotonic.Stopwatch",
                ResetEntryPoint = "manual"
            },
            Capabilities = new PcCompatKeyViewerEvidenceMatrix
            {
                Input = proven,
                Lane = probable,
                Transition = probable,
                Count = probable,
                Kps = probable,
                Rain = probable,
                Presentation = probable,
                Visibility = probable,
                InputActivation = probable,
                Settings = probable,
                Persistence = probable
            }
        };
        var adapter = new PcCompatKeyViewerAdapterDocument
        {
            ModId = "TestMod",
            PackageSha256 = new string('a', 64),
            TargetGameRevision = 143,
            ProxySurfaceHash = new string('b', 64),
            Features = [feature]
        };
        var overrides = PcCompatKeyViewerOverrideStore.CreateFor(adapter);
        var featureOverride = overrides.Features.Single();
        featureOverride.Enabled = true;
        featureOverride.InputMode = PcCompatKeyViewerInputMode.Touch;
        featureOverride.TouchLaneCount = 2;
        var provider = roles.Single(role => role.Role == "BindingProvider");
        featureOverride.Roles.Add(new PcCompatKeyViewerRoleOverride
        {
            Role = provider.Role,
            AssemblyName = provider.AssemblyName,
            TypeName = provider.TypeName,
            MemberName = provider.MemberName,
            MemberKind = provider.MemberKind
        });
        return (adapter, overrides);
    }
}

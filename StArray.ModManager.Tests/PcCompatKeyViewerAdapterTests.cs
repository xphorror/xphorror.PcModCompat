using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatKeyViewerAdapterTests
{
    [Test]
    public void ValidAdapterRoundTripsWithDeterministicFeatureAndLaneOrder()
    {
        var document = CreateDocument();

        var validation = PcCompatKeyViewerAdapterValidator.Validate(document);
        var json = document.ToJson();
        var roundTrip = PcCompatKeyViewerAdapterDocument.FromJson(json);

        Assert.Multiple(() =>
        {
            Assert.That(validation.IsValid, Is.True, string.Join(Environment.NewLine, validation.Errors));
            Assert.That(roundTrip, Is.Not.Null);
            Assert.That(roundTrip!.FormatVersion,
                Is.EqualTo(PcCompatKeyViewerAdapterDocument.CurrentFormatVersion));
            Assert.That(PcCompatKeyViewerAdapterValidator.IsCoreReady(roundTrip.Features[0]), Is.True);
            Assert.That(json.IndexOf("\"id\": \"main\"", StringComparison.Ordinal),
                Is.LessThan(json.IndexOf("\"id\": \"touch\"", StringComparison.Ordinal)));
            Assert.That(roundTrip.ToJson(), Is.EqualTo(json));
        });
    }

    [Test]
    public void ManualCandidateDoesNotPromoteProbableEvidenceToCoreReady()
    {
        var document = CreateDocument(inputEvidence: new PcCompatAdapterEvidence
        {
            Status = PcCompatAdapterEvidenceStatus.Probable,
            Evidence = ["single candidate"],
            FirstBreak = "reflection source not closed",
            SelectedCandidate = "InputListener.Update",
            UserConfirmed = true
        });

        var result = PcCompatKeyViewerAdapterValidator.Validate(document);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.True, string.Join(Environment.NewLine, result.Errors));
            Assert.That(PcCompatKeyViewerAdapterValidator.IsCoreReady(document.Features[0]), Is.False);
        });
    }

    [Test]
    public void InvalidBindingAndDuplicateLaneAreRejected()
    {
        var feature = CreateFeature();
        var firstLane = feature.LaneGroups[0].Lanes[0];
        var invalidGroup = new PcCompatKeyViewerLaneGroup
        {
            Id = "main",
            Lanes =
            [
                firstLane,
                new PcCompatKeyViewerLane
                {
                    Id = firstLane.Id,
                    DisplayLabel = "duplicate",
                    Binding = new PcCompatLaneBinding
                    {
                        Kind = PcCompatLaneBindingKind.DirectIdentity,
                        Identities = Array.Empty<PcCompatInputIdentity>(),
                        SourceProfileId = "missing"
                    }
                }
            ]
        };
        var document = CreateDocument(features:
        [
            CloneFeature(feature, laneGroups: [invalidGroup])
        ]);

        var result = PcCompatKeyViewerAdapterValidator.Validate(document);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors, Has.Some.Contains("duplicate id 'touch-1'"));
            Assert.That(result.Errors, Has.Some.Contains("does not exist"));
            Assert.That(result.Errors, Has.Some.Contains("exactly one identity"));
        });
    }

    [Test]
    public void ChangedPackageRevisionProxyOrMvidInvalidatesCachedAdapter()
    {
        var document = CreateDocument();
        var context = new PcCompatKeyViewerAdapterValidationContext
        {
            PackageSha256 = new string('b', 64),
            TargetGameRevision = document.TargetGameRevision + 1,
            ProxySurfaceHash = new string('c', 64),
            AssemblyMvids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["JipperResourcePack"] = Guid.NewGuid().ToString("D")
            }
        };

        var result = PcCompatKeyViewerAdapterValidator.Validate(document, context);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors, Has.Some.Contains("package SHA-256 changed"));
            Assert.That(result.Errors, Has.Some.Contains("target game revision changed"));
            Assert.That(result.Errors, Has.Some.Contains("proxy surface changed"));
            Assert.That(result.Errors, Has.Some.Contains("MVID changed"));
        });
    }

    private static PcCompatKeyViewerAdapterDocument CreateDocument(
        PcCompatAdapterEvidence? inputEvidence = null,
        IReadOnlyList<PcCompatKeyViewerFeatureAdapter>? features = null)
        => new()
        {
            ModId = "JipperResourcePack",
            PackageSha256 = new string('a', 64),
            TargetGameRevision = 143,
            ProxySurfaceHash = new string('f', 64),
            Assemblies =
            [
                new PcCompatAdapterAssemblyFingerprint
                {
                    AssemblyName = "JipperResourcePack",
                    Sha256 = new string('1', 64),
                    Mvid = "1caa829e-37fa-4ad2-918f-09f8dcc61248"
                }
            ],
            Features = features ?? [CreateFeature(inputEvidence)]
        };

    private static PcCompatKeyViewerFeatureAdapter CreateFeature(
        PcCompatAdapterEvidence? inputEvidence = null)
    {
        var proven = Proven("closed test graph");
        return new PcCompatKeyViewerFeatureAdapter
        {
            Id = "keyviewer",
            DisplayName = "Key Viewer",
            SourceProfiles =
            [
                new PcCompatKeyViewerSourceProfile
                {
                    Id = "legacy",
                    Kind = PcCompatKeyViewerInputProfileKind.LegacyUnityPolling,
                    EntryPoints = ["Input.GetKey", "Input.GetKeyDown"],
                    Evidence = proven
                }
            ],
            LaneGroups =
            [
                new PcCompatKeyViewerLaneGroup
                {
                    Id = "touch",
                    Lanes =
                    [
                        new PcCompatKeyViewerLane
                        {
                            Id = "touch-1",
                            DisplayLabel = "T1",
                            Binding = new PcCompatLaneBinding
                            {
                                Kind = PcCompatLaneBindingKind.TouchLane,
                                TouchLane = 1,
                                SourceProfileId = "legacy"
                            }
                        }
                    ]
                },
                new PcCompatKeyViewerLaneGroup
                {
                    Id = "main",
                    Lanes =
                    [
                        new PcCompatKeyViewerLane
                        {
                            Id = "key-z",
                            DisplayLabel = "Z",
                            Binding = new PcCompatLaneBinding
                            {
                                Kind = PcCompatLaneBindingKind.DirectIdentity,
                                Identities =
                                [
                                    new PcCompatInputIdentity
                                    {
                                        Kind = PcCompatInputIdentityKind.UnityKeyCode,
                                        Value = "Z"
                                    }
                                ],
                                SourceProfileId = "legacy"
                            }
                        }
                    ]
                }
            ],
            Roles =
            [
                new PcCompatKeyViewerRoleBinding
                {
                    Role = "ControllerType",
                    AssemblyName = "JipperResourcePack",
                    TypeName = "JipperResourcePack.KeyViewer",
                    Evidence = proven
                }
            ],
            Visibility = new PcCompatKeyViewerPredicate
            {
                Kind = "AlwaysWhileFeatureEnabled",
                Expression = "Enabled",
                Evidence = proven
            },
            InputActivation = new PcCompatKeyViewerPredicate
            {
                Kind = "AlwaysWhileFeatureEnabled",
                Expression = "Enabled",
                Evidence = proven
            },
            CountSemantics = new PcCompatKeyViewerCountSemantics
            {
                Clock = "Stopwatch.Monotonic",
                ResetEntryPoint = "Setting.ResetCountConfirmed",
                PersistencePath = "KeyCount.dat",
                BackupPersistencePath = "KeyCount.dat.bak"
            },
            Capabilities = new PcCompatKeyViewerEvidenceMatrix
            {
                Input = inputEvidence ?? proven,
                Lane = proven,
                Transition = proven,
                Count = proven,
                Kps = proven,
                Rain = new PcCompatAdapterEvidence
                {
                    Status = PcCompatAdapterEvidenceStatus.Unsupported,
                    FirstBreak = "rain mesh factory not translated",
                    OriginalDisablePath = "Settings.useRain=false"
                },
                Presentation = proven,
                Visibility = proven,
                InputActivation = proven,
                Settings = proven,
                Persistence = proven
            }
        };
    }

    private static PcCompatKeyViewerFeatureAdapter CloneFeature(
        PcCompatKeyViewerFeatureAdapter source,
        IReadOnlyList<PcCompatKeyViewerLaneGroup> laneGroups)
        => new()
        {
            Id = source.Id,
            DisplayName = source.DisplayName,
            Backend = source.Backend,
            SourceProfiles = source.SourceProfiles,
            LaneGroups = laneGroups,
            Roles = source.Roles,
            Visibility = source.Visibility,
            InputActivation = source.InputActivation,
            CountSemantics = source.CountSemantics,
            Capabilities = source.Capabilities
        };

    private static PcCompatAdapterEvidence Proven(string evidence)
        => new()
        {
            Status = PcCompatAdapterEvidenceStatus.Proven,
            Evidence = [evidence]
        };
}

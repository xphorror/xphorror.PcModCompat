using System.Text;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace StArray.ModManager.Tests;

public sealed class ModIsolationManifestTests
{
    private const string HashA =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Test]
    public void CanonicalJsonAndHashIgnoreInputCollectionOrder()
    {
        var first = CreateManifest(reverse: false);
        var second = CreateManifest(reverse: true);

        Assert.Multiple(() =>
        {
            Assert.That(second.ToCanonicalJson(), Is.EqualTo(first.ToCanonicalJson()));
            Assert.That(second.ComputeManifestHash(), Is.EqualTo(first.ComputeManifestHash()));
            Assert.That(first.ComputeManifestHash(), Has.Length.EqualTo(64));
        });
    }

    [Test]
    public void AtomicRoundTripUsesUtf8AndPreservesCanonicalIdentity()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "starray-isolation-manifest-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "isolation.json");
        try
        {
            var source = CreateManifest(reverse: true);
            source.WriteAtomic(path);
            var bytes = File.ReadAllBytes(path);
            var loaded = ModIsolationManifest.Read(path);

            Assert.Multiple(() =>
            {
                Assert.That(bytes.Take(3).ToArray(), Is.Not.EqualTo(Encoding.UTF8.GetPreamble()));
                Assert.That(loaded.ComputeManifestHash(), Is.EqualTo(source.ComputeManifestHash()));
                Assert.That(loaded.Features.Select(feature => feature.FeatureId),
                    Is.EqualTo(new[] { "feature-a", "feature-b" }));
                Assert.That(Directory.GetFiles(root), Has.Length.EqualTo(1));
            });
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void DuplicateStaticSlotAndInvalidHashFailClosed()
    {
        var duplicate = CreateManifest(reverse: false) with
        {
            StaticMembers =
            [
                new ModIsolationStaticMemberRecord
                {
                    MemberIdentity = "A::Value",
                    Classification = ModStaticStateClassification.DomainMutable,
                    StaticSlotId = 7
                },
                new ModIsolationStaticMemberRecord
                {
                    MemberIdentity = "B::Value",
                    Classification = ModStaticStateClassification.DomainMutable,
                    StaticSlotId = 7
                }
            ]
        };
        var invalidHash = CreateManifest(reverse: false) with
        {
            OriginalAssembly = CreateAssembly("Assembly-CSharp", "not-a-hash")
        };

        Assert.Multiple(() =>
        {
            Assert.Throws<InvalidDataException>(() => duplicate.ToCanonicalJson());
            Assert.Throws<InvalidDataException>(() => invalidHash.ToCanonicalJson());
        });
    }

    [Test]
    public void BootstrapFactoryReadsManagedAssemblyWithoutExecutingIt()
    {
        var path = typeof(ModIsolationManifestTests).Assembly.Location;
        var manifest = ModIsolationManifestFactory.CreateBootstrap(
            "bootstrap-fixture",
            ModEntry.NativeLoaderKind,
            path);

        Assert.Multiple(() =>
        {
            Assert.That(manifest.OriginalAssembly.Name,
                Is.EqualTo("StArray.ModManager.Tests"));
            Assert.That(manifest.OriginalAssembly.ModuleVersionId,
                Is.Not.EqualTo(Guid.Empty.ToString("D")));
            Assert.That(manifest.OriginalAssembly.Sha256, Has.Length.EqualTo(64));
            Assert.That(manifest.Features.Single().Level,
                Is.EqualTo(ModIsolationCapabilityLevel.Guarded));
        });
    }

    private static ModIsolationManifest CreateManifest(bool reverse)
    {
        var features = new[]
        {
            new ModIsolationFeatureRecord
            {
                FeatureId = "feature-a",
                Level = ModIsolationCapabilityLevel.Proven,
                Evidence = ["z-evidence", "a-evidence"]
            },
            new ModIsolationFeatureRecord
            {
                FeatureId = "feature-b",
                Level = ModIsolationCapabilityLevel.Guarded,
                Dependencies = ["feature-a"]
            }
        };
        var staticMembers = new[]
        {
            new ModIsolationStaticMemberRecord
            {
                MemberIdentity = "A::Value",
                Classification = ModStaticStateClassification.DomainMutable,
                StaticSlotId = 1
            },
            new ModIsolationStaticMemberRecord
            {
                MemberIdentity = "B::Data",
                Classification = ModStaticStateClassification.SharedImmutable,
                StaticSlotId = 2
            }
        };
        if (reverse)
        {
            Array.Reverse(features);
            Array.Reverse(staticMembers);
        }

        return new ModIsolationManifest
        {
            ModId = "fixture",
            LoaderKind = "StArray.Android.Native",
            OriginalAssembly = CreateAssembly("Fixture", HashA),
            SemanticPack = new ModSemanticPackIdentity
            {
                FormatVersion = "starray-cil-semantic-pack-v1",
                GameVersion = "3.1.2",
                PackSha256 = HashA,
                MethodStreamSha256 = HashB,
                SourceTreeSha256 = HashA,
                SourceFileCount = 2,
                Assemblies = [CreateAssembly("Assembly-CSharp", HashB)]
            },
            Features = features,
            StaticMembers = staticMembers,
            DirectLinks =
            [
                new ModIsolationDirectLinkRecord
                {
                    ProviderId = "Provider",
                    ProviderAssemblyIdentity = "Provider, Version=1.0.0.0",
                    ApiClosureHash = HashA,
                    TypeClosureHash = HashB,
                    ReferencedMembers = ["Z::Run", "A::Read"]
                }
            ],
            DataSources =
            [
                new ModIsolationDataSourceRecord
                {
                    FeatureId = "feature-a",
                    SourceKind = "OfficialGameFact",
                    ProviderIdentity = "host:game-facts",
                    SourceGeneration = "3.1.2",
                    SchemaHash = HashA
                }
            ],
            NativeCalls =
            [
                new ModIsolationNativeCallRecord
                {
                    MemberIdentity = "Fixture::ReadClock",
                    Library = "libc",
                    EntryPoint = "clock_gettime",
                    Classification = ModNativeCallClassification.SharedStateless,
                    Level = ModIsolationCapabilityLevel.Proven
                }
            ]
        };
    }

    private static ModAssemblyIdentity CreateAssembly(string name, string hash) => new()
    {
        Name = name,
        Version = "1.0.0.0",
        ModuleVersionId = "11111111-2222-3333-4444-555555555555",
        Sha256 = hash,
        ApiSurfaceHash = HashB,
        FileSize = 1234
    };
}

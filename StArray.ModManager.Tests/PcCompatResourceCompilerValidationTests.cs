using Xphorror.PcModCompat.Resources;

namespace StArray.ModManager.Tests;

public sealed class PcCompatResourceCompilerValidationTests
{
    [TestCase(@"C:\mods\Jipper\Linux\jipperresourcepackbundle", "jipperresourcepackbundle")]
    [TestCase("/mods/Jipper/Linux/jipperresourcepackbundle", "jipperresourcepackbundle")]
    [TestCase("/mods/Jipper/Linux\\jipperresourcepackbundle", "jipperresourcepackbundle")]
    public void PortableBundleFileNameAcceptsWindowsUnixAndMixedSeparators(
        string path,
        string expected)
    {
        Assert.That(UnityBundleIndexer.GetPortableFileName(path), Is.EqualTo(expected));
    }

    [Test]
    public void ProvenBindingCarriesStructuredSourceFieldIdentity()
    {
        const string candidateSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var report = ResourceCompiler.Compile(
            "Sample",
            new[]
            {
                new ResourceCandidateIndex
                {
                    SourcePath = "bundle",
                    FileName = "bundle",
                    PlatformHint = BundlePlatformHint.Android,
                    UnityVersion = UnityBundleIndexer.TargetUnityVersion,
                    VersionGate = UnityVersionGate.Auto,
                    LoadPolicy = BundleLoadPolicy.AutoLoad,
                    FileSize = 8,
                    Sha256Hex = candidateSha,
                    IndexSucceeded = true,
                    Assets = new[]
                    {
                        new ResourceAssetEntry
                        {
                            Name = "KeyBackground",
                            TypeName = "Sprite"
                        }
                    }
                }
            },
            new AssetLoadFlowReport
            {
                ProvenBindings = new[]
                {
                    new AssetLoadFlowBinding
                    {
                        AssetName = "KeyBackground",
                        FieldType = "UnityEngine.Sprite",
                        FieldName = "KeyBackground",
                        DeclaringType = "Sample.BundleLoader",
                        MethodName = "LoadBundle",
                        AssemblyPath = "Sample.dll",
                        ExpectedTypeHint = "Sprite"
                    }
                }
            });

        Assert.That(report.Bindings.Single().SourceFieldIdentity,
            Is.EqualTo("Sample.BundleLoader.KeyBackground"));
    }

    [Test]
    public void OfflineValidatorRejectsFeatureGroupWithUnknownCandidate()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pccompat-resource-validator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "resource_recipe.bin");
        const string candidateSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        try
        {
            var report = new ResourceCompileReport
            {
                ModId = "Sample",
                Compatibility = "partial",
                TargetUnityVersion = UnityBundleIndexer.TargetUnityVersion,
                Candidates =
                [
                    new ResourceCandidateIndex
                    {
                        SourcePath = Path.Combine(root, "bundle"),
                        FileName = "bundle",
                        PlatformHint = BundlePlatformHint.Android,
                        UnityVersion = UnityBundleIndexer.TargetUnityVersion,
                        VersionGate = UnityVersionGate.Auto,
                        LoadPolicy = BundleLoadPolicy.AutoLoad,
                        FileSize = 8,
                        Sha256Hex = candidateSha,
                        IndexSucceeded = true
                    }
                ],
                FeatureGroups =
                [
                    new ResourceFeatureGroup
                    {
                        Id = "bundle.primary",
                        DisplayName = "Primary",
                        SelectedCandidateSha256Hex =
                            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                        SelectedPlatform = BundlePlatformHint.Android,
                        LoadPolicy = BundleLoadPolicy.AutoLoad
                    }
                ]
            };

            ResourceRecipeBinary.Write(path, report);

            Assert.That(ResourceRecipeBinary.TryValidate(path, out var error), Is.False);
            Assert.That(error, Does.Contain("feature group").IgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestCase(BundlePlatformHint.Android, BundleLoadPolicy.AutoLoad)]
    [TestCase(BundlePlatformHint.Linux, BundleLoadPolicy.ControlledLoad)]
    [TestCase(BundlePlatformHint.Windows, BundleLoadPolicy.ControlledLoad)]
    [TestCase(BundlePlatformHint.Mac, BundleLoadPolicy.ControlledLoad)]
    public void Unity6000DesktopBundlesRemainControlled(
        BundlePlatformHint platform,
        BundleLoadPolicy expected)
    {
        Assert.That(
            UnityBundleIndexer.DecideLoadPolicy(
                UnityVersionGate.Auto,
                platform,
                allowForceNonUnity6000: false),
            Is.EqualTo(expected));
    }
}

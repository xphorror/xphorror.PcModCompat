using Xphorror.PcModCompat.Resources;

namespace StArray.ModManager.Tests;

public sealed class PcCompatResourceCompilerValidationTests
{
    [Test]
    public void JipperKeyViewerGenericLoadAssetCallsAreProvenWithoutStaticFieldStores()
    {
        var root = FindModManagerRoot();
        var assembly = Path.Combine(root, "JipperKeyViewer-AssetBundle", "JipperKeyViewer.dll");
        Assume.That(File.Exists(assembly), Is.True, assembly);

        var flow = AssetLoadFlowAnalyzer.AnalyzeAssemblies([assembly]);
        var requests = flow.ProvenRequests
            .Where(request => request.Kind == AssetLoadFlowRequestKind.LoadAssetByName)
            .ToDictionary(request => request.AssetName, request => request.ExpectedTypeHint, StringComparer.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(requests, Has.Count.EqualTo(5), string.Join(Environment.NewLine, flow.Issues));
            Assert.That(requests["KeyBackground"], Is.EqualTo("Sprite"));
            Assert.That(requests["KeyOutline"], Is.EqualTo("Sprite"));
            Assert.That(requests["GhostRain"], Is.EqualTo("Sprite"));
            Assert.That(requests["MAPLESTORY_OTF_BOLD"], Is.EqualTo("Font"));
            Assert.That(requests["cjkFonts-regular-normalized"], Is.EqualTo("Font"));
        });

        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var report = ResourceCompiler.Compile(
            "JipperKeyViewer",
            [
                new ResourceCandidateIndex
                {
                    SourcePath = "keyviewer_resources",
                    FileName = "keyviewer_resources",
                    PlatformHint = BundlePlatformHint.Android,
                    UnityVersion = UnityBundleIndexer.TargetUnityVersion,
                    VersionGate = UnityVersionGate.Auto,
                    LoadPolicy = BundleLoadPolicy.AutoLoad,
                    FileSize = 1,
                    Sha256Hex = sha,
                    IndexSucceeded = true,
                    Assets = requests.Select(request => new ResourceAssetEntry
                    {
                        Name = request.Key,
                        TypeName = request.Value
                    }).ToArray()
                }
            ],
            flow);

        Assert.That(
            report.Bindings.Where(binding => binding.Confidence == AssetBindConfidence.Proven)
                .Select(binding => binding.AssetName),
            Is.EquivalentTo(requests.Keys));
    }

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
    public void CompileCollapsesDuplicateBundleContentAndKeepsUnity6000Candidate()
    {
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        static ResourceCandidateIndex Candidate(string fileName) => new()
        {
            SourcePath = Path.Combine("JipperOverlayer", fileName),
            FileName = fileName,
            PlatformHint = BundlePlatformHint.Windows,
            UnityVersion = "6000.3.16f1",
            VersionGate = UnityVersionGate.Auto,
            LoadPolicy = BundleLoadPolicy.ControlledLoad,
            FileSize = 8,
            Sha256Hex = sha,
            IndexSucceeded = true,
            Assets =
            [
                new ResourceAssetEntry
                {
                    Name = "ProgressBar",
                    TypeName = "GameObject"
                }
            ]
        };

        var report = ResourceCompiler.Compile(
            "JipperOverlayer",
            [
                Candidate("jipperoverlayerbundle2022"),
                Candidate("jipperoverlayerbundle6000")
            ]);

        Assert.Multiple(() =>
        {
            Assert.That(report.Candidates, Has.Count.EqualTo(1));
            Assert.That(report.Candidates.Single().FileName,
                Is.EqualTo("jipperoverlayerbundle6000"));
            Assert.That(report.Warnings,
                Has.Some.Contains("duplicate candidate content collapsed"));
            Assert.That(report.Warnings,
                Has.Some.Contains("jipperoverlayerbundle2022"));
        });

        var root = Path.Combine(
            Path.GetTempPath(),
            "pccompat-resource-dedup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var recipe = Path.Combine(root, "resource_recipe.bin");
            ResourceRecipeBinary.Write(recipe, report);
            Assert.That(ResourceRecipeBinary.TryValidate(recipe, out var error),
                Is.True,
                error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    private static string FindModManagerRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "build_android_single.ps1")) &&
                Directory.Exists(Path.Combine(directory.FullName, "xphorror.PcModCompat")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("StArray.ModManager repository root not found.");
    }
}

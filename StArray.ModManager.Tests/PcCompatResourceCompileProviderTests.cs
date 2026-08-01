using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class PcCompatResourceCompileProviderTests
{
    [TearDown]
    public void TearDown()
        => PcCompatResourceAssemblyCompile.RegisterProvider(null);

    [Test]
    public void RegisteredProviderReceivesManifestAndCancellationToken()
    {
        using var cancellation = new CancellationTokenSource();
        PcModManifest? receivedManifest = null;
        CancellationToken receivedToken = default;
        var manifest = new PcModManifest
        {
            FolderPath = TestContext.CurrentContext.WorkDirectory,
            Id = "ResourceProbe",
            DisplayName = "Resource Probe"
        };
        var expected = new PcCompatResourceCompileInfo
        {
            RecipePath = "resource_recipe.bin",
            ResourceIrPath = "resource_ir.bin",
            ResourceIrPayloadDirectory = "resource_ir_blobs",
            ReportPath = "resource_report.json",
            Compatibility = "partial",
            CacheHit = false,
            CandidateCount = 2,
            FeatureGroupCount = 3,
            BindingCount = 4,
            IrBundleCount = 2,
            IrAssetCount = 8,
            IrRequiredAssetCount = 4
        };
        PcCompatResourceAssemblyCompile.RegisterProvider((value, token) =>
        {
            receivedManifest = value;
            receivedToken = token;
            return expected;
        });

        var actual = PcCompatResourceAssemblyCompile.Prepare(manifest, cancellation.Token);

        Assert.Multiple(() =>
        {
            Assert.That(PcCompatResourceAssemblyCompile.IsProviderRegistered, Is.True);
            Assert.That(actual, Is.SameAs(expected));
            Assert.That(receivedManifest, Is.SameAs(manifest));
            Assert.That(receivedToken, Is.EqualTo(cancellation.Token));
        });
    }

    [Test]
    public void AndroidBuildWiresImportCompilerAndPackagesItsDependencies()
    {
        var root = FindModManagerRoot();
        var managedEntry = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "Managed.cs"));
        var androidProject = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "StArray.ModManager.Android.csproj"));
        var provider = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatAndroidResourceAssemblyCompile.cs"));
        var resourceCompiler = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "Resources",
            "ResourceIrCompiler.cs"));
        var recipeTool = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "tools",
            "ResourceRecipeTool",
            "Program.cs"));
        var recipeScript = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "tools",
            "compile_resource_recipe.ps1"));
        var buildScript = File.ReadAllText(Path.Combine(root, "build.ps1"));

        Assert.Multiple(() =>
        {
            Assert.That(managedEntry, Does.Contain("PcCompatAndroidResourceAssemblyCompile.Install()"));
            Assert.That(androidProject, Does.Contain("xphorror.PcModCompat.Resources.csproj"));
            Assert.That(provider, Does.Contain("CompileGate.Wait(cancellationToken)"));
            Assert.That(provider, Does.Contain("TryVerifyCandidateFile"));
            Assert.That(provider, Does.Contain("ResourceIrCompiler.Build"));
            Assert.That(provider, Does.Contain("PcCompatResourceIr.TryValidateAgainstRecipe"));
            Assert.That(provider, Does.Contain("GroupBy(item => item.Sha256Hex"));
            Assert.That(provider, Does.Contain("HasCurrentCompilerMarker"));
            Assert.That(provider, Does.Contain("ResourceIrCompiler.CompilerRevision"));
            Assert.That(resourceCompiler, Does.Contain(
                "resource-ir-compiler-v4-alpha8-atlas"));
            Assert.That(recipeTool, Does.Contain("ResourceIrCompiler.CacheMarkerFileName"));
            Assert.That(recipeScript, Does.Contain(
                "resource-ir-compiler-v4-alpha8-atlas"));
            Assert.That(buildScript, Does.Contain("xphorror.PcModCompat.Resources.dll"));
            Assert.That(buildScript, Does.Contain("AssetsTools.NET.dll"));
        });
    }

    private static string FindModManagerRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "build.ps1")) &&
                Directory.Exists(Path.Combine(directory.FullName, "xphorror.PcModCompat")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("StArray.ModManager repository root not found.");
    }
}

using System.Runtime.Loader;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public class PcCompatStaticPatchScannerTests
{
    [Test]
    public void ScansJipperDirectAttributesWithoutExecutingModCode()
    {
        var repoRoot = FindRepoRoot();
        var modDir = Path.Combine(repoRoot, "JipperResourcePack_release");
        Assume.That(Directory.Exists(modDir), Is.True, $"missing sample mod dir: {modDir}");

        Assert.That(PcModManifestReader.TryRead(modDir, out var manifest, out var error), Is.True, error);

        var report = PcCompatStaticPatchScanner.Scan(manifest, targetGameRevision: 143);

        Assert.That(report.FormatVersion, Is.EqualTo("static-patch-scan-v2"));
        Assert.That(report.AssembliesScanned, Has.Count.EqualTo(2));
        Assert.That(report.Patches.Count(patch => patch.Source == "static_attribute"), Is.EqualTo(40));
        Assert.That(report.Patches.Count(patch => patch.Source == "dynamic_addpatch"), Is.EqualTo(34));
        Assert.That(report.ActivePatches.Count(patch => patch.Source == "static_attribute"), Is.EqualTo(32));
        Assert.That(report.ActivePatches.Count(patch => patch.Source == "dynamic_addpatch"), Is.EqualTo(17));
        Assert.That(report.Issues, Is.Empty, () => DescribeIssues(report));

        var stateChange = report.Patches.Single(patch =>
            patch.CallbackType == "JipperResourcePack.Main" &&
            patch.CallbackMethod == "OnChangeState");
        Assert.That(stateChange.TargetType, Is.EqualTo("MonsterLove.StateMachine.StateBehaviour"));
        Assert.That(stateChange.TargetMethod, Is.EqualTo("ChangeState"));
        Assert.That(stateChange.Kind, Is.EqualTo(PcCompatPatchKind.Postfix));
        Assert.That(stateChange.NeedInstance, Is.True);
        Assert.That(stateChange.ArgumentTypeNames, Is.EqualTo(new[] { "System.Enum" }));
        Assert.That(stateChange.CallbackParameterTypeNames, Is.EqualTo(new[] { "System.Enum" }));

        var modernAccuracy = report.ActivePatches.Single(patch =>
            patch.CallbackType == "JipperResourcePack.OverlayContents.Status" &&
            patch.CallbackMethod == "OnAccuracyChange");
        Assert.That(modernAccuracy.CallbackParameterTypeNames, Is.EqualTo(new[] { "System.Object" }));

        var modernBpm = report.Patches.Single(patch =>
            patch.CallbackType == "JipperResourcePack.OverlayContents.Bpm" &&
            patch.TargetType == "scrPlayer");
        Assert.That(modernBpm.MinVersion, Is.EqualTo(141));
        Assert.That(modernBpm.MaxVersion, Is.EqualTo(int.MaxValue));
        Assert.That(modernBpm.IsApplicableToRevision(143), Is.True);

        var legacyBpm = report.Patches.Single(patch =>
            patch.CallbackType == "JipperResourcePack.OverlayContents.Bpm" &&
            patch.TargetType == "scrController");
        Assert.That(legacyBpm.MaxVersion, Is.EqualTo(140));
        Assert.That(legacyBpm.IsApplicableToRevision(143), Is.False);

        var modernReversePatch = report.Patches.Single(patch =>
            patch.Source == "dynamic_addpatch" &&
            patch.TargetType == "JipperResourcePack.VersionSafe" &&
            patch.TargetMethod == "GetPercentAcc" &&
            patch.CallbackMethod == "GetPercentAccR141");
        Assert.That(modernReversePatch.Kind, Is.EqualTo(PcCompatPatchKind.ReversePatch));
        Assert.That(modernReversePatch.MinVersion, Is.EqualTo(141));
        Assert.That(modernReversePatch.MaxVersion, Is.EqualTo(int.MaxValue));
        Assert.That(modernReversePatch.NeedInstance, Is.False);

        var legacyReversePatch = report.Patches.Single(patch =>
            patch.Source == "dynamic_addpatch" &&
            patch.TargetType == "JipperResourcePack.VersionSafe" &&
            patch.TargetMethod == "GetPercentAcc" &&
            patch.CallbackMethod == "GetPercentAccR136");
        Assert.That(legacyReversePatch.Kind, Is.EqualTo(PcCompatPatchKind.ReversePatch));
        Assert.That(legacyReversePatch.MinVersion, Is.EqualTo(0));
        Assert.That(legacyReversePatch.MaxVersion, Is.EqualTo(140));

        var json = report.ToJson();
        Assert.That(json, Does.Not.Contain("\"activePatches\""));
        Assert.That(json, Does.Contain("\"source\": \"dynamic_addpatch\""));
    }

    [Test]
    public void SelectsLegacyVersionSafeDynamicPatchesForRevision140()
    {
        var repoRoot = FindRepoRoot();
        var modDir = Path.Combine(repoRoot, "JipperResourcePack_release");
        Assume.That(Directory.Exists(modDir), Is.True, $"missing sample mod dir: {modDir}");

        Assert.That(PcModManifestReader.TryRead(modDir, out var manifest, out var error), Is.True, error);

        var report = PcCompatStaticPatchScanner.Scan(manifest, targetGameRevision: 140);
        var dynamicPatches = report.ActivePatches
            .Where(patch =>
                patch.Source == "dynamic_addpatch" &&
                patch.CallbackType == "JipperResourcePack.VersionSafe")
            .ToArray();

        Assert.That(dynamicPatches, Has.Length.EqualTo(9));
        Assert.That(dynamicPatches, Has.All.Matches<PcCompatPatchDescriptor>(patch =>
            patch.Kind == PcCompatPatchKind.ReversePatch &&
            patch.CallbackMethod.EndsWith("R136", StringComparison.Ordinal)));
    }

    [Test]
    public void InterpretsResourceChangerMethodInfoArraysAndRevisionTarget()
    {
        var repoRoot = FindRepoRoot();
        var modDir = Path.Combine(repoRoot, "JipperResourcePack_release");
        Assume.That(Directory.Exists(modDir), Is.True, $"missing sample mod dir: {modDir}");

        Assert.That(PcModManifestReader.TryRead(modDir, out var manifest, out var error), Is.True, error);

        var modernReport = PcCompatStaticPatchScanner.Scan(manifest, targetGameRevision: 143);
        var modern = modernReport.ActivePatches
            .Where(patch =>
                patch.Source == "dynamic_addpatch" &&
                patch.CallbackType == "JipperResourcePack.ResourceChanger")
            .ToArray();

        Assert.That(modern, Has.Length.EqualTo(8));
        Assert.That(modern, Has.All.Matches<PcCompatPatchDescriptor>(patch =>
            patch.TargetType == "PlanetRenderer" &&
            patch.Kind == PcCompatPatchKind.Prefix &&
            !patch.NeedInstance &&
            !patch.TryingCatch &&
            patch.MinVersion == 130));
        Assert.That(modern.Select(patch => patch.TargetMethod), Is.EquivalentTo(new[]
        {
            "SetRainbow",
            "LoadPlanetColor",
            "SetColor",
            "SetPlanetColor",
            "SetCoreColor",
            "SetTailColor",
            "SetRingColor",
            "SetFaceColor"
        }));

        var legacyReport = PcCompatStaticPatchScanner.Scan(manifest, targetGameRevision: 129);
        var legacy = legacyReport.ActivePatches
            .Where(patch =>
                patch.Source == "dynamic_addpatch" &&
                patch.CallbackType == "JipperResourcePack.ResourceChanger")
            .ToArray();
        Assert.That(legacy, Has.Length.EqualTo(8));
        Assert.That(legacy, Has.All.Matches<PcCompatPatchDescriptor>(patch =>
            patch.TargetType == "scrPlanet" && patch.MaxVersion == 129));
    }

    [Test]
    public void VersionSafeStaticRecoveryMatchesManagedOracleForModernBranch()
    {
        var repoRoot = FindRepoRoot();
        var modDir = Path.Combine(repoRoot, "JipperResourcePack_release");
        var shimDir = Path.Combine(repoRoot, "xphorror.PcModCompat", "out", "shims");
        Assume.That(Directory.Exists(modDir), Is.True, $"missing sample mod dir: {modDir}");
        Assume.That(Directory.Exists(shimDir), Is.True, $"missing shim dir: {shimDir}");

        Assert.That(PcModManifestReader.TryRead(modDir, out var manifest, out var error), Is.True, error);

        var report = PcCompatStaticPatchScanner.Scan(manifest, targetGameRevision: 143);
        using var session = PcCompatManagedLoader.Load(manifest, new PcCompatLoadOptions
        {
            ShimFolder = shimDir,
            AllowLegacyStubExecution = true,
            TryBootstrap = true,
            Enable = false
        });

        static string Key(PcCompatPatchDescriptor patch)
            => $"{patch.Kind}|{patch.TargetType}.{patch.TargetMethod}|{patch.CallbackType}.{patch.CallbackMethod}|{patch.NeedInstance}";

        var recovered = report.ActivePatches
            .Where(patch =>
                patch.Source == "dynamic_addpatch" &&
                patch.CallbackType == "JipperResourcePack.VersionSafe")
            .Select(Key)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var oracle = session.RegisteredPatches
            .Where(patch => patch.Kind == PcCompatPatchKind.ReversePatch)
            .Select(Key)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.That(recovered, Is.EqualTo(oracle));
    }

    [Test]
    public void ResourceChangerStaticRecoveryMatchesManagedOracle()
    {
        var repoRoot = FindRepoRoot();
        var modDir = Path.Combine(repoRoot, "JipperResourcePack_release");
        var shimDir = Path.Combine(repoRoot, "xphorror.PcModCompat", "out", "shims");
        Assume.That(Directory.Exists(modDir), Is.True, $"missing sample mod dir: {modDir}");
        Assume.That(Directory.Exists(shimDir), Is.True, $"missing shim dir: {shimDir}");

        Assert.That(PcModManifestReader.TryRead(modDir, out var manifest, out var error), Is.True, error);

        var report = PcCompatStaticPatchScanner.Scan(manifest, targetGameRevision: 143);
        using var session = PcCompatManagedLoader.Load(manifest, new PcCompatLoadOptions
        {
            ShimFolder = shimDir,
            AllowLegacyStubExecution = true,
            TryBootstrap = true,
            Enable = false
        });

        var resourceType = session.Assembly.GetType("JipperResourcePack.ResourceChanger", throwOnError: true)!;
        var resource = Activator.CreateInstance(resourceType)!;
        var patcher = resourceType.BaseType!.GetProperty("Patcher")!.GetValue(resource)!;
        patcher.GetType().GetMethod("Patch")!.Invoke(patcher, null);

        var loadContext = AssemblyLoadContext.GetLoadContext(session.Assembly)!;
        var jalib = loadContext.Assemblies.Single(assembly => assembly.GetName().Name == "JALib");
        var patcherType = jalib.GetType("JALib.Core.Patch.JAPatcher", throwOnError: true)!;
        var snapshot = (Array)patcherType.GetMethod("SnapshotRegisteredPatches")!.Invoke(null, null)!;

        static string StaticKey(PcCompatPatchDescriptor patch)
            => $"{patch.Kind}|{patch.TargetType}.{patch.TargetMethod}|{patch.CallbackType}.{patch.CallbackMethod}";

        static string OracleKey(object record)
        {
            var type = record.GetType();
            string Get(string name) => type.GetProperty(name)!.GetValue(record)?.ToString() ?? string.Empty;
            return $"{Get("Kind")}|{Get("TargetType")}.{Get("TargetMethod")}|{Get("CallbackType")}.{Get("CallbackMethod")}";
        }

        var recovered = report.ActivePatches
            .Where(patch =>
                patch.CallbackType == "JipperResourcePack.ResourceChanger")
            .Select(StaticKey)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var oracleAll = snapshot.Cast<object>()
            .Where(record => record.GetType().GetProperty("CallbackType")!.GetValue(record)?.ToString() == "JipperResourcePack.ResourceChanger")
            .Select(OracleKey)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var scanned = report.Patches
            .Where(patch => patch.CallbackType == "JipperResourcePack.ResourceChanger")
            .ToArray();
        var scannedKeys = scanned
            .Select(StaticKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var activeKeys = scanned
            .Where(patch => patch.IsApplicableToRevision(143))
            .Select(StaticKey)
            .ToHashSet(StringComparer.Ordinal);
        var oracle = oracleAll
            .Where(activeKeys.Contains)
            .ToArray();

        Assert.That(oracleAll, Is.SubsetOf(scannedKeys));
        Assert.That(recovered, Is.EqualTo(oracle));
    }

    [Test]
    public void RuntimeWritesStaticScanAuditReport()
    {
        const string recipeOnlyVariable = "STARRAY_PCMOD_COMPAT_RECIPE_ONLY";
        var previousRecipeOnly = Environment.GetEnvironmentVariable(recipeOnlyVariable);
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "pccompat-static-report-" + Guid.NewGuid().ToString("N"));
        var modDir = Path.Combine(root, "JipperResourcePack");
        Directory.CreateDirectory(modDir);
        var manifest = new PcModManifest
        {
            FolderPath = modDir,
            Id = "JipperResourcePack",
            DisplayName = "JipperResourcePack",
            Kind = PcModKind.JAMod,
            JAModClassName = "JipperResourcePack.Main"
        };

        try
        {
            Environment.SetEnvironmentVariable(recipeOnlyVariable, "1");
            Assert.Throws<NotSupportedException>(() => PcCompatRuntime.RegisterMod(manifest));

            var report = PcCompatRuntime.GetStaticScanReport(manifest.Id);
            var reportPath = Path.Combine(modDir, ".pccompat", "static_patch_scan.json");
            Assert.That(report, Is.Not.Null);
            Assert.That(report!.Issues.Select(issue => issue.Code), Does.Contain("AssemblyNotFound"));
            Assert.That(File.Exists(reportPath), Is.True);
            Assert.That(File.ReadAllText(reportPath), Does.Contain("\"formatVersion\": \"static-patch-scan-v2\""));
        }
        finally
        {
            Environment.SetEnvironmentVariable(recipeOnlyVariable, previousRecipeOnly);
            PcCompatRuntime.UnregisterMod(manifest);
            Directory.Delete(root, true);
        }
    }

    private static string DescribeIssues(PcCompatStaticPatchScanReport report)
        => string.Join(
            Environment.NewLine,
            report.Issues.Select(issue =>
                $"{issue.Code} @ {issue.CallbackType}.{issue.CallbackMethod}: {issue.Message}"));

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(TestContext.CurrentContext.WorkDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "xphorror.PcModCompat")) &&
                Directory.Exists(Path.Combine(current.FullName, "StArray.ModManager")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate StArray.ModManager repo root");
    }
}

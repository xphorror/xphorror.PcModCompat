using System.Reflection;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests
{
 [NonParallelizable]
 public sealed class PcCompatJipperOverlayerLifecycleContractTests
 {
    [Test]
    public void R143ActivatesOnlyV141BranchAndR140ActivatesOnlyV136Branch()
    {
        var modern = new[]
        {
            ("ScrPlayerHitBpmPatch", "scrPlayer", "Hit"),
            ("ScrMarginAddHitComboPatch", "scrMarginTracker", "AddHit"),
            ("ScrMarginAddHitJudgementPatch", "scrMarginTracker", "AddHit"),
            ("ScrMarginResetPatch", "scrMarginTracker", "Reset"),
            ("ScrMarginCalcAccPatch", "scrMarginTracker", "CalculatePercentAcc"),
            ("ScrMarginAddHitJComboPatch", "scrMarginTracker", "AddHit"),
            ("MistakesManagerSetPlayerCountPatch", "scrMistakesManager", "SetPlayerCount")
        };
        var legacy = new[]
        {
            ("ScrControllerHitBpmPatch", "scrController", "Hit"),
            ("ScrMistakesAddHitComboPatch", "scrMistakesManager", "AddHit"),
            ("ScrMistakesAddHitJudgementPatch", "scrMistakesManager", "AddHit"),
            ("ScrMistakesResetPatch", "scrMistakesManager", "Reset"),
            ("ScrMistakesCalcAccPatch", "scrMistakesManager", "CalculatePercentAcc"),
            ("ScrMistakesAddHitJComboPatch", "scrMistakesManager", "AddHit")
        };
        var patches = modern.Concat(legacy)
            .Select(item => Patch(
                "JipperOverlayer.Overlayer.Features." + item.Item1,
                item.Item2,
                item.Item3))
            .ToArray();

        var issues = new List<PcCompatStaticPatchScanIssue>();
        var normalized = PcCompatKnownModPatchActivationPolicy.Apply("JipperOverlayer", patches, issues);

        Assert.Multiple(() =>
        {
            Assert.That(normalized.Where(patch => patch.IsApplicableToRevision(143))
                    .Select(patch => patch.CallbackType.Split('.').Last()),
                Is.EquivalentTo(modern.Select(item => item.Item1)));
            Assert.That(normalized.Where(patch => patch.IsApplicableToRevision(140))
                    .Select(patch => patch.CallbackType.Split('.').Last()),
                Is.EquivalentTo(legacy.Select(item => item.Item1)));
            Assert.That(issues, Is.Empty);
        });
    }

    [Test]
    public void ActivationPolicyIntersectsAuthorRangeAndRejectsConflicts()
    {
        var patch = Patch(
            "JipperOverlayer.Overlayer.Features.ScrPlayerHitBpmPatch",
            "scrPlayer",
            "Hit",
            minVersion: 0,
            maxVersion: 140);
        var issues = new List<PcCompatStaticPatchScanIssue>();

        var normalized = PcCompatKnownModPatchActivationPolicy.Apply("JipperOverlayer", [patch], issues);

        Assert.Multiple(() =>
        {
            Assert.That(normalized.Single().MinVersion, Is.EqualTo(141));
            Assert.That(normalized.Single().MaxVersion, Is.EqualTo(140));
            Assert.That(normalized.Single().IsApplicableToRevision(140), Is.False);
            Assert.That(issues.Select(issue => issue.Code), Does.Contain("KnownModActivationRangeConflict"));
        });
    }

    [Test]
    public void RdcAutoIsManagedOnlyAndHasAnExactStaticSetterAbi()
    {
        var patch = Patch(
            "JipperOverlayer.Overlayer.Features.RdcSetAutoPatch",
            "RDC",
            "set_auto",
            kind: PcCompatPatchKind.Postfix);

        Assert.That(PcCompatManagedOnlyCallbackCatalog.TryFind(patch, out var entry), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(entry.Supported, Is.True);
            Assert.That(entry.TargetSignature, Is.Not.Null);
            Assert.That(entry.TargetSignature!.IsStatic, Is.True);
            Assert.That(entry.TargetSignature.ReturnType, Is.EqualTo("System.Void"));
            Assert.That(entry.TargetSignature.ParameterTypes, Is.EqualTo(new[] { "System.Boolean" }));
        });
    }

    [Test]
    public void ScrShowIfDebugCallbacksAreSupportedButBetaAwakeRemainsUnsupported()
    {
        var updatePatch = Patch(
            "JipperOverlayer.Overlayer.Features.ScrShowIfDebugUpdatePatch",
            "scrShowIfDebug",
            "Update",
            PcCompatPatchKind.Prefix,
            callbackParameterTypes: ["UnityEngine.UI.Text"]);
        Assert.That(PcCompatManagedOnlyCallbackCatalog.TryFind(updatePatch, out var updateEntry), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(updateEntry.Supported, Is.True);
            Assert.That(updateEntry.TargetSignature, Is.Not.Null);
            Assert.That(updateEntry.TargetSignature!.AssemblyName, Is.EqualTo("Assembly-CSharp"));
            Assert.That(updateEntry.TargetSignature.TypeName, Is.EqualTo("scrShowIfDebug"));
            Assert.That(updateEntry.TargetSignature.MethodName, Is.EqualTo("Update"));
            Assert.That(updateEntry.TargetSignature.IsStatic, Is.False);
            Assert.That(updateEntry.TargetSignature.ReturnType, Is.EqualTo("System.Void"));
            Assert.That(updateEntry.TargetSignature.ParameterTypes, Is.Empty);
        });

        var surfacePath = Path.Combine(
            FindModManagerRoot(),
            "xphorror.PcModCompat",
            "tools",
            "ProxyInputClosure",
            "proxy_surface_members.txt");
        var surface = File.ReadAllText(surfacePath);
        Assert.That(surface, Does.Contain("F|Assembly-CSharp|scrShowIfDebug|txt"));

        var awakePatch = Patch(
            "JipperOverlayer.Overlayer.Features.ScrShowIfDebugAwakePatch",
            "scrShowIfDebug",
            "Awake",
            PcCompatPatchKind.Postfix,
            callbackParameterTypes: ["scrShowIfDebug"]);
        Assert.That(PcCompatManagedOnlyCallbackCatalog.TryFind(awakePatch, out var awakeEntry), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(awakeEntry.Supported, Is.True);
            Assert.That(awakeEntry.TargetSignature, Is.Not.Null);
            Assert.That(awakeEntry.TargetSignature!.AssemblyName, Is.EqualTo("Assembly-CSharp"));
            Assert.That(awakeEntry.TargetSignature.TypeName, Is.EqualTo("scrShowIfDebug"));
            Assert.That(awakeEntry.TargetSignature.MethodName, Is.EqualTo("Awake"));
            Assert.That(awakeEntry.TargetSignature.IsStatic, Is.False);
            Assert.That(awakeEntry.TargetSignature.ReturnType, Is.EqualTo("System.Void"));
            Assert.That(awakeEntry.TargetSignature.ParameterTypes, Is.Empty);
        });

        var betaPatch = Patch(
            "JipperOverlayer.Overlayer.Features.BetaWatermarkCapturePatch",
            "scrEnableIfBeta",
            "Awake",
            PcCompatPatchKind.Postfix,
            callbackParameterTypes: ["scrEnableIfBeta"]);
        Assert.That(PcCompatManagedOnlyCallbackCatalog.TryFind(betaPatch, out var betaEntry), Is.True);
        Assert.That(betaEntry.Supported, Is.False);
        Assert.That(betaEntry.Reason, Is.Not.Empty);
    }

    [Test]
    public void ScrShowIfDebugUpdateCompilesAsSynchronousPrefixWithoutFixedOp()
    {
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var manifest = new PcModManifest
        {
            FolderPath = Path.GetDirectoryName(assemblyPath)!,
            Id = "JipperOverlayer",
            DisplayName = "Jipper Overlayer"
        };
        var descriptor = Patch(
            "JipperOverlayer.Overlayer.Features.ScrShowIfDebugUpdatePatch",
            "scrShowIfDebug",
            "Update",
            PcCompatPatchKind.Prefix,
            callbackParameterTypes: ["UnityEngine.UI.Text"],
            callbackAssemblyPath: assemblyPath);
        var scan = new PcCompatStaticPatchScanReport
        {
            ModId = manifest.Id,
            TargetGameRevision = 143,
            AssembliesScanned = [assemblyPath],
            Patches = [descriptor]
        };

        var translation = PcCompatCallbackTranslator.Translate(manifest, scan);
        Assert.That(translation.Items, Has.Count.EqualTo(1));
        Assert.That(translation.Items.Single().Status, Is.EqualTo(PcCompatCallbackTranslationStatus.Translated));
        Assert.That(translation.Items.Single().ManagedDispatchRequired, Is.True);
        Assert.That(translation.Items.Single().ResolvedTarget, Is.Not.Null);

        Assert.That(PcCompatRecipeCompiler.TryCompile(manifest, translation, out var recipe, out var error), Is.True, error);
        var managed = recipe.Rules.Single(rule => rule.Op == PcCompatRuleOp.ManagedSynchronousPrefix);
        Assert.Multiple(() =>
        {
            Assert.That(managed.TargetAssemblyName, Is.EqualTo("Assembly-CSharp"));
            Assert.That(managed.TargetType, Is.EqualTo("scrShowIfDebug"));
            Assert.That(managed.TargetMethod, Is.EqualTo("Update"));
            Assert.That(managed.TargetIsStatic, Is.False);
            Assert.That(managed.TargetReturnType, Is.EqualTo("System.Void"));
            Assert.That(managed.TargetParameterTypes, Is.Empty);
            Assert.That(managed.Stage, Is.EqualTo(PcCompatRuleStage.BeforeOriginal));
            Assert.That(managed.RequiredCapabilities, Is.EqualTo(PcCompatCapability.SkipOriginal));
            Assert.That(recipe.Rules, Has.None.Matches<PcCompatCompiledRule>(rule =>
                rule.TargetType == "scrShowIfDebug" && rule.TargetMethod == "Update" &&
                rule.Op != PcCompatRuleOp.ManagedSynchronousPrefix));
        });
    }

    [Test]
    public void ScrShowIfDebugAwakeCompilesAsManagedPostfixWithoutFixedOp()
    {
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var manifest = new PcModManifest
        {
            FolderPath = Path.GetDirectoryName(assemblyPath)!,
            Id = "JipperOverlayer",
            DisplayName = "Jipper Overlayer"
        };
        var descriptor = Patch(
            "JipperOverlayer.Overlayer.Features.ScrShowIfDebugAwakePatch",
            "scrShowIfDebug",
            "Awake",
            PcCompatPatchKind.Postfix,
            callbackParameterTypes: ["scrShowIfDebug"],
            callbackAssemblyPath: assemblyPath);
        var scan = new PcCompatStaticPatchScanReport
        {
            ModId = manifest.Id,
            TargetGameRevision = 143,
            AssembliesScanned = [assemblyPath],
            Patches = [descriptor]
        };

        var translation = PcCompatCallbackTranslator.Translate(manifest, scan);
        Assert.That(translation.Items, Has.Count.EqualTo(1));
        Assert.That(translation.Items.Single().Status, Is.EqualTo(PcCompatCallbackTranslationStatus.Translated));
        Assert.That(translation.Items.Single().ManagedDispatchRequired, Is.True);
        Assert.That(translation.Items.Single().ResolvedTarget, Is.Not.Null);

        Assert.That(PcCompatRecipeCompiler.TryCompile(manifest, translation, out var recipe, out var error), Is.True, error);
        var managed = recipe.Rules.Single(rule => rule.Op == PcCompatRuleOp.ManagedEventCallback);
        Assert.Multiple(() =>
        {
            Assert.That(managed.TargetAssemblyName, Is.EqualTo("Assembly-CSharp"));
            Assert.That(managed.TargetType, Is.EqualTo("scrShowIfDebug"));
            Assert.That(managed.TargetMethod, Is.EqualTo("Awake"));
            Assert.That(managed.TargetIsStatic, Is.False);
            Assert.That(managed.TargetReturnType, Is.EqualTo("System.Void"));
            Assert.That(managed.TargetParameterTypes, Is.Empty);
            Assert.That(managed.Stage, Is.EqualTo(PcCompatRuleStage.AfterOriginal));
            Assert.That(managed.RequiredCapabilities, Is.EqualTo(PcCompatCapability.AfterOriginalObserve));
            Assert.That(recipe.Rules, Has.None.Matches<PcCompatCompiledRule>(rule =>
                rule.TargetType == "scrShowIfDebug" && rule.TargetMethod == "Awake" &&
                rule.Op != PcCompatRuleOp.ManagedEventCallback));
        });
    }

    [Test]
    public void ManagedOnlyRdcCallbackProducesOneManagedRuleAndNoFixedOp()
    {
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var manifest = new PcModManifest
        {
            FolderPath = Path.GetDirectoryName(assemblyPath)!,
            Id = "JipperOverlayer",
            DisplayName = "Jipper Overlayer"
        };
        var descriptor = Patch(
            "JipperOverlayer.Overlayer.Features.RdcSetAutoPatch",
            "RDC",
            "set_auto",
            kind: PcCompatPatchKind.Postfix,
            callbackAssemblyPath: assemblyPath);
        var scan = new PcCompatStaticPatchScanReport
        {
            ModId = manifest.Id,
            TargetGameRevision = 143,
            AssembliesScanned = [assemblyPath],
            Patches = [descriptor]
        };

        var translation = PcCompatCallbackTranslator.Translate(manifest, scan);
        Assert.That(translation.Items, Has.Count.EqualTo(1));
        Assert.That(translation.Items.Single().Status, Is.EqualTo(PcCompatCallbackTranslationStatus.Translated));
        Assert.That(translation.Rules, Is.Empty);
        Assert.That(translation.Items.Single().ManagedDispatchRequired, Is.True);

        Assert.That(PcCompatRecipeCompiler.TryCompile(manifest, translation, out var recipe, out var error), Is.True, error);
        var managed = recipe.Rules.Single(rule => rule.Op == PcCompatRuleOp.ManagedEventCallback);
        Assert.Multiple(() =>
        {
            Assert.That(recipe.Rules.Count(rule => rule.TargetType == "RDC" && rule.TargetMethod == "set_auto"), Is.EqualTo(1));
            Assert.That(managed.TargetIsStatic, Is.True);
            Assert.That(managed.TargetParameterTypes, Is.EqualTo(new[] { "System.Boolean" }));
            Assert.That(recipe.Rules.Any(rule => rule.Op == PcCompatRuleOp.OverlayUpdatePlayers), Is.False);
        });
    }

    private static string FindModManagerRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.WorkDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "xphorror.PcModCompat")) &&
                Directory.Exists(Path.Combine(directory.FullName, "StArray.ModManager")))
                return directory.FullName;

            directory = directory.Parent;
        }

        Assert.Fail("Could not locate StArray.ModManager root");
        return string.Empty;
    }

    private static PcCompatPatchDescriptor Patch(
        string callbackType,
        string targetType,
        string targetMethod,
        PcCompatPatchKind kind = PcCompatPatchKind.Postfix,
        IReadOnlyList<string>? callbackParameterTypes = null,
        string? callbackAssemblyPath = null,
        int minVersion = 0,
        int maxVersion = int.MaxValue)
        => new()
        {
            ModId = "JipperOverlayer",
            TargetType = targetType,
            TargetMethod = targetMethod,
            Kind = kind,
            CallbackType = callbackType,
            CallbackMethod = kind == PcCompatPatchKind.Prefix ? "Prefix" : "Postfix",
            CallbackAssemblyPath = callbackAssemblyPath,
            CallbackParameterTypeNames = callbackParameterTypes ?? Array.Empty<string>(),
            MinVersion = minVersion,
            MaxVersion = maxVersion
        };
 }
}

// This is only a metadata fixture for the managed-only catalog test. It is not a MOD source file
// and is never loaded by the Android runtime.
namespace JipperOverlayer.Overlayer.Features
{
    public static class RdcSetAutoPatch
    {
        public static void Postfix()
        {
        }
    }

    public static class ScrShowIfDebugUpdatePatch
    {
        public static bool Prefix(UnityEngine.UI.Text ___txt)
        {
            return true;
        }
    }

    public static class ScrShowIfDebugAwakePatch
    {
        public static void Postfix(global::scrShowIfDebug __instance)
        {
            var transform = __instance.GetComponent<UnityEngine.RectTransform>();
            if (transform)
                transform.anchoredPosition = new UnityEngine.Vector2(300, transform.anchoredPosition.y);
        }
    }
}

public sealed class scrShowIfDebug : UnityEngine.Component
{
}

// Metadata-only fixture for the callback scanner. The production proxy surface supplies the real
// UnityEngine.UI.Text type; the test project deliberately does not need to reference that shim.
namespace UnityEngine.UI
{
    public sealed class Text
    {
    }
}

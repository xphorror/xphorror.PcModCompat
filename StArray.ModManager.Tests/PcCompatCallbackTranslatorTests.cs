using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public class PcCompatCallbackTranslatorTests
{
    [Test]
    public void TranslatesJipperFixedOpCallbacksWithoutExecutingModCode()
    {
        var (manifest, _) = ReadSampleManifest();
        var scan = PcCompatStaticPatchScanner.Scan(manifest, targetGameRevision: 143);

        var translation = PcCompatCallbackTranslator.Translate(manifest, scan);

        Assert.That(
            translation.FormatVersion,
            Is.EqualTo("callback-translation-v9-managed-only-catalog"));
        Assert.That(translation.Items, Has.Count.EqualTo(49));
        Assert.That(translation.Rules, Has.Count.EqualTo(32));
        Assert.That(translation.TranslatedCount, Is.EqualTo(31));
        Assert.That(translation.UnsupportedCount, Is.EqualTo(4));
        Assert.That(translation.Items.Count(item => item.Status == PcCompatCallbackTranslationStatus.Skipped), Is.EqualTo(9));
        Assert.That(translation.Rules, Has.All.Matches<PcCompatCompiledRule>(rule =>
            rule.Source.StartsWith("translator:fixed-op-v2:", StringComparison.Ordinal)));
        Assert.That(translation.Rules.Count(rule => rule.Stage == PcCompatRuleStage.BeforeOriginal), Is.EqualTo(13));
        Assert.That(translation.Rules.Count(rule => rule.Stage == PcCompatRuleStage.AfterOriginal), Is.EqualTo(19));
        Assert.That(translation.Rules.Select(rule => rule.Op), Is.SupersetOf(new[]
        {
            PcCompatRuleOp.OverlayShow,
            PcCompatRuleOp.OverlayShowPractice,
            PcCompatRuleOp.OverlayHandleStateChange,
            PcCompatRuleOp.OverlayHide,
            PcCompatRuleOp.OverlayHide,
            PcCompatRuleOp.OverlayHide,
            PcCompatRuleOp.OverlayUpdatePlayers,
            PcCompatRuleOp.PublishMarginSnapshot,
            PcCompatRuleOp.OverlayRecordPlayerHit,
            PcCompatRuleOp.OverlayRecordHitTiming,
            PcCompatRuleOp.OverlayRecordHit,
            PcCompatRuleOp.OverlayResetJudgement,
            PcCompatRuleOp.OverlayRecordFloorMove,
            PcCompatRuleOp.OverlayRecordDeath,
            PcCompatRuleOp.ResourceApplyEditorRabbit,
            PcCompatRuleOp.ResourceApplyFloorColor,
            PcCompatRuleOp.ResourceApplyPlanetColor,
            PcCompatRuleOp.ResourceSkipPlanetColorOriginal,
            PcCompatRuleOp.ResourceOverridePlanetColorArg,
            PcCompatRuleOp.ResourceSkipTileColorOriginal,
            PcCompatRuleOp.ResourceApplyLogoText
        }));
        Assert.That(translation.Rules.Where(rule => rule.FeatureId == "resource_changer"),
            Has.All.Matches<PcCompatCompiledRule>(rule =>
                rule.RequiredCapabilities.HasFlag(PcCompatCapability.ReadIl2CppField) &&
                rule.RequiredCapabilities.HasFlag(PcCompatCapability.CallIl2CppMutator)));
        Assert.That(
            translation.Rules.Count(rule => rule.FeatureId == "resource_changer"),
            Is.EqualTo(18));
        var ottoBlink = translation.Rules.Single(rule =>
            rule.TargetType == "scnEditor" && rule.TargetMethod == "OttoBlink");
        Assert.Multiple(() =>
        {
            Assert.That(ottoBlink.Stage, Is.EqualTo(PcCompatRuleStage.AfterOriginal));
            Assert.That(ottoBlink.Op, Is.EqualTo(PcCompatRuleOp.ResourceApplyEditorRabbit));
            Assert.That(ottoBlink.TargetParameterTypes, Is.Empty);
            Assert.That(ottoBlink.Source, Does.Contain("companion:otto-blink"));
        });
        Assert.That(
            translation.Items.Where(item =>
                item.Status == PcCompatCallbackTranslationStatus.Translated &&
                item.RuleId != null &&
                translation.Rules.Any(rule =>
                    rule.Id == item.RuleId && rule.FeatureId == "resource_changer")),
            Has.All.Property(nameof(PcCompatCallbackTranslationItem.ManagedDispatchRequired)).False);
        Assert.That(
            translation.Rules
                .Where(rule => rule.TargetType == "scrLogoText")
                .Select(rule => $"{rule.TargetMethod}:{rule.Stage}:{rule.Op}"),
            Is.EquivalentTo(new[]
            {
                "Awake:AfterOriginal:ResourceApplyLogoText",
                "LateUpdate:BeforeOriginal:ResourceSkipPlanetColorOriginal",
                "UpdateColors:BeforeOriginal:ResourceSkipPlanetColorOriginal"
            }));
        Assert.That(translation.Items.Count(item => item.Reason.Contains("projected to player 0", StringComparison.Ordinal)),
            Is.EqualTo(3));
        Assert.That(translation.Items.Where(item => item.Reason.Contains("projected to player 0", StringComparison.Ordinal)),
            Has.All.Matches<PcCompatCallbackTranslationItem>(item =>
                item.Status == PcCompatCallbackTranslationStatus.Translated &&
                item.CallbackParameterTypeNames.SequenceEqual(new[] { "System.Object" }, StringComparer.Ordinal)));
    }

    [Test]
    public void VerifiedTranslationDirectlyBuildsGenericRecipe()
    {
        var (manifest, _) = ReadSampleManifest();
        var scan = PcCompatStaticPatchScanner.Scan(manifest, targetGameRevision: 143);
        var translation = PcCompatCallbackTranslator.Translate(manifest, scan);

        Assert.That(PcCompatRecipeCompiler.TryCompile(manifest, translation, out var recipe, out var recipeError), Is.True, recipeError);

        static string Shape(PcCompatCompiledRule rule)
            => $"{rule.Id}|{rule.TargetType}.{rule.TargetMethod}|{rule.ParamCount}|{rule.Stage}|{rule.Op}|{(ulong)rule.RequiredCapabilities}";

        var translatedRules = translation.Rules
            .Select(Shape)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var compiledTranslatedRules = recipe.Rules
            .Where(rule => rule.Source.StartsWith("translator:fixed-op-v2:", StringComparison.Ordinal))
            .Select(Shape)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.That(compiledTranslatedRules, Is.EqualTo(translatedRules));
        Assert.That(recipe.Rules, Has.Count.EqualTo(54));
        var gameplayAccepted = recipe.Rules.Single(rule =>
            rule.Id == "platform.input.gameplay_accepted");
        Assert.That(gameplayAccepted.Op, Is.EqualTo(PcCompatRuleOp.GameplayAcceptedObserve));
        Assert.That(gameplayAccepted.Source, Is.EqualTo("platform:gameplay-accepted-v1"));
        var lifecycleFallback = recipe.Rules.Single(rule => rule.Id == "platform.overlay.quit_to_main_menu");
        Assert.That(lifecycleFallback.Source, Is.EqualTo("platform:overlay-lifecycle-v1"));
        var editorLifecycle = recipe.Rules.Single(rule => rule.Id == "platform.overlay.editor_switch_to_edit");
        Assert.That(editorLifecycle.TargetParameterTypes, Is.EqualTo(new[] { "System.Boolean" }));
        Assert.That(editorLifecycle.Source, Is.EqualTo("platform:overlay-lifecycle-v2"));
        var telemetry = recipe.Rules.Single(rule => rule.Id == "platform.overlay.player_control_telemetry");
        Assert.That(telemetry.Op, Is.EqualTo(PcCompatRuleOp.OverlayPollTelemetry));
        Assert.That(telemetry.Source, Is.EqualTo("platform:overlay-telemetry-v1"));
    }

    [Test]
    public void RuntimeWritesCallbackAuditAndUsesTranslatedRulesInBundle()
    {
        var (_, sampleDir) = ReadSampleManifest();
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "pccompat-callback-" + Guid.NewGuid().ToString("N"));
        var modDir = Path.Combine(root, "JipperResourcePack");
        Directory.CreateDirectory(modDir);
        foreach (var name in new[] { "Info.json", "JAModInfo.json", "JAMod.Bootstrap.dll", "JipperResourcePack.dll" })
            File.Copy(Path.Combine(sampleDir, name), Path.Combine(modDir, name));

        Assert.That(PcModManifestReader.TryRead(modDir, out var manifest, out var error), Is.True, error);
        var previousOracle = Environment.GetEnvironmentVariable("STARRAY_PCMOD_COMPAT_MANAGED_ORACLE");
        Environment.SetEnvironmentVariable("STARRAY_PCMOD_COMPAT_MANAGED_ORACLE", null);
        try
        {
            PcCompatRuntime.RegisterMod(manifest);

            var translation = PcCompatRuntime.GetCallbackTranslationReport(manifest.Id);
            var recipe = PcCompatRuntime.GetRecipeReport(manifest.Id);
            var bundle = PcCompatRuntime.GetRecipeBundle(manifest.Id);
            var auditPath = Path.Combine(modDir, ".pccompat", "callback_translation.json");

            Assert.That(translation, Is.Not.Null);
            Assert.That(translation!.Rules, Has.Count.EqualTo(32));
            Assert.That(recipe, Is.Not.Null);
            Assert.That(recipe!.Rules.Where(rule =>
                    rule.FeatureId == "overlay" &&
                    !rule.Id.StartsWith("platform.overlay.", StringComparison.Ordinal)),
                Has.All.Matches<PcCompatCompiledRule>(rule =>
                rule.Source.StartsWith("translator:fixed-op-v2:", StringComparison.Ordinal)));
            Assert.That(recipe.Rules.Single(rule => rule.Id == "platform.overlay.quit_to_main_menu").Source,
                Is.EqualTo("platform:overlay-lifecycle-v1"));
            Assert.That(recipe.Rules.Single(rule => rule.Id == "platform.overlay.editor_switch_to_edit").Source,
                Is.EqualTo("platform:overlay-lifecycle-v2"));
            Assert.That(bundle, Is.Not.Null);
            Assert.That(File.Exists(auditPath), Is.True);
            Assert.That(
                File.ReadAllText(auditPath),
                Does.Contain("\"formatVersion\": \"callback-translation-v9-managed-only-catalog\""));
            Assert.That(File.ReadAllText(bundle!.RulesPath), Does.Contain("translator:fixed-op-v2:JipperResourcePack.Main.OnGameStop"));
        }
        finally
        {
            PcCompatRuntime.UnregisterMod(manifest);
            Environment.SetEnvironmentVariable("STARRAY_PCMOD_COMPAT_MANAGED_ORACLE", previousOracle);
            Directory.Delete(root, recursive: true);
        }
    }

    private static (PcModManifest Manifest, string SampleDir) ReadSampleManifest()
    {
        var repoRoot = FindRepoRoot();
        var sampleDir = Path.Combine(repoRoot, "JipperResourcePack_release");
        Assume.That(Directory.Exists(sampleDir), Is.True, $"missing sample mod dir: {sampleDir}");
        Assert.That(PcModManifestReader.TryRead(sampleDir, out var manifest, out var error), Is.True, error);
        return (manifest, sampleDir);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(TestContext.CurrentContext.WorkDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "xphorror.PcModCompat")) &&
                Directory.Exists(Path.Combine(current.FullName, "StArray.ModManager")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate StArray.ModManager repo root");
    }
}

using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public class PcCompatRecipeCompilerTests
{
    [Test]
    public void ReversePatchStateConsumerGetsSharedTelemetryWithoutOwnerOverlay()
    {
        var manifest = new PcModManifest
        {
            Id = "ReversePatchStateConsumer",
            DisplayName = "ReversePatchStateConsumer",
            Version = "1.0.0",
            EntryMethod = "Test.Entry.Load",
            FolderPath = TestContext.CurrentContext.WorkDirectory
        };
        var translation = new PcCompatCallbackTranslationReport
        {
            ModId = manifest.Id,
            TargetGameRevision = 143,
            Rules = new[]
            {
                new PcCompatCompiledRule
                {
                    Id = "state-consumer.anchor",
                    FeatureId = "state_consumer",
                    TargetType = "scrController",
                    TargetMethod = "StartLoadingScene",
                    ParamCount = 0,
                    TargetIsStatic = false,
                    TargetReturnType = "System.Void",
                    TargetParameterTypes = Array.Empty<string>(),
                    Stage = PcCompatRuleStage.AfterOriginal,
                    Op = PcCompatRuleOp.ManagedEventCallback,
                    RequiredCapabilities = PcCompatCapability.AfterOriginalObserve
                }
            },
            Items = new[]
            {
                new PcCompatCallbackTranslationItem
                {
                    TargetType = "scrController",
                    TargetMethod = "GetProgress",
                    CallbackType = "Test.State",
                    CallbackMethod = "GetProgress",
                    PatchKind = PcCompatPatchKind.ReversePatch,
                    Status = PcCompatCallbackTranslationStatus.Skipped,
                    Reason = "ReversePatch is handled by the dedicated state bridge."
                }
            }
        };

        var ok = PcCompatRecipeCompiler.TryCompile(
            manifest,
            translation,
            out var report,
            out var error);

        Assert.That(ok, Is.True, error);
        var telemetry = report.Rules.Single(rule =>
            rule.Op == PcCompatRuleOp.OverlayPollTelemetry &&
            rule.TargetType == "scrController" &&
            rule.TargetMethod == "PlayerControl_Update");
        Assert.Multiple(() =>
        {
            Assert.That(
                telemetry.RequiredCapabilities.HasFlag(PcCompatCapability.UiOverlay),
                Is.False);
            Assert.That(
                telemetry.RequiredCapabilities.HasFlag(PcCompatCapability.ReadIl2CppField),
                Is.True);
            Assert.That(
                telemetry.RequiredCapabilities.HasFlag(PcCompatCapability.CallIl2CppGetter),
                Is.True);
        });
    }

    [Test]
    public void CompilesVerifiedFixedOpRecipeWithoutIdentitySelection()
    {
        var source = ReadSampleManifest();
        var manifest = CloneManifest(source, source.FolderPath, "GenericTelemetryMod");
        var scan = PcCompatStaticPatchScanner.Scan(manifest, targetGameRevision: 143);
        var translation = PcCompatCallbackTranslator.Translate(manifest, scan);

        var ok = PcCompatRecipeCompiler.TryCompile(manifest, translation, out var report, out var error);

        Assert.That(ok, Is.True, error);
        Assert.That(report.ModId, Is.EqualTo("GenericTelemetryMod"));
        Assert.That(report.RecipeId, Is.EqualTo("xphorror.recipe.verified_fixed_op.v1"));
        Assert.That(report.Compatibility, Is.EqualTo("partial"));
        Assert.That(report.Rules, Has.Count.EqualTo(54));
        Assert.That(report.Rules.Select(rule => rule.Id), Does.Contain("platform.input.gameplay_accepted"));
        Assert.That(report.Rules.Select(rule => rule.Id), Does.Contain("domain.status.margin_snapshot"));
        Assert.That(report.Rules.Select(rule => rule.Id), Does.Contain("domain.status.player_hit"));
        Assert.That(report.Rules.Select(rule => rule.Id), Does.Contain("domain.status.hit_timing"));
        Assert.That(report.Rules.Select(rule => rule.Id), Does.Contain("domain.judgement.margin_hit"));
        Assert.That(report.Rules.Select(rule => rule.Id), Does.Contain("domain.judgement.floor_move"));
        Assert.That(report.Rules.Select(rule => rule.Id), Does.Contain("domain.judgement.player_die"));
        Assert.That(report.Rules.Select(rule => rule.TargetType + "." + rule.TargetMethod), Does.Contain("scrMarginTracker.CalculatePercentAcc"));
        Assert.That(report.Rules.Select(rule => rule.TargetType + "." + rule.TargetMethod), Does.Contain("scrPlanet.MoveToNextFloor"));
        Assert.That(report.Rules.Select(rule => rule.TargetType + "." + rule.TargetMethod), Does.Contain("scrPlayer.Hit"));
        Assert.That(report.Rules.Select(rule => rule.TargetType + "." + rule.TargetMethod), Does.Contain("scrPlayer.HitInputEvent"));
        Assert.That(report.Rules.Select(rule => rule.TargetType + "." + rule.TargetMethod), Does.Contain("scrPlayer.Die"));
        Assert.That(report.Rules.Select(rule => rule.TargetType + "." + rule.TargetMethod), Does.Contain("scrMisc.GetHitMargin"));
        Assert.That(report.Rules.Select(rule => rule.TargetType + "." + rule.TargetMethod), Does.Contain("scrController.QuitToMainMenu"));
        Assert.That(report.Rules.Select(rule => rule.TargetType + "." + rule.TargetMethod), Does.Contain("scnEditor.SwitchToEditMode"));
        Assert.That(report.Rules.Select(rule => rule.TargetType + "." + rule.TargetMethod), Does.Contain("scrController.PlayerControl_Update"));
        Assert.That(report.Rules.Select(rule => rule.TargetType + "." + rule.TargetMethod), Does.Contain("scrFloor.SetTileColor"));
        Assert.That(report.Rules.Select(rule => rule.TargetType + "." + rule.TargetMethod), Does.Contain("PlanetRenderer.SetPlanetColor"));
        Assert.That(report.Rules.Select(rule => rule.TargetType + "." + rule.TargetMethod), Does.Contain("PlanetRenderer.SetTailColor"));
        Assert.That(report.RequiredCapabilities.HasFlag(PcCompatCapability.UiOverlay), Is.True);
        Assert.That(report.RequiredCapabilities.HasFlag(PcCompatCapability.ReadIl2CppField), Is.True);
        Assert.That(report.RequiredCapabilities.HasFlag(PcCompatCapability.SkipOriginal), Is.True);
        Assert.That(report.RequiredCapabilities.HasFlag(PcCompatCapability.CallIl2CppMutator), Is.True);

        var overlay = report.Features.Single(feature => feature.Id == "overlay");
        Assert.That(overlay.Status, Is.EqualTo(PcCompatFeatureStatus.Supported));
        Assert.That(overlay.RuleIds, Has.Count.EqualTo(9));
        var resourceChanger = report.Features.Single(feature => feature.Id == "resource_changer");
        Assert.That(resourceChanger.Status, Is.EqualTo(PcCompatFeatureStatus.Supported));
        Assert.That(resourceChanger.RuleIds, Has.Count.EqualTo(18));

        Assert.That(report.Features.Single(feature => feature.Id == "untranslated_callbacks").Status,
            Is.EqualTo(PcCompatFeatureStatus.Partial));
        Assert.That(report.Unsupported, Is.Not.Empty);
        Assert.That(PcCompatRecipeCapabilities.SupportsStandardUnityHud(report), Is.True);
    }

    [Test]
    public void EmitsManagedEventRulesForActivePostfixCallbacks()
    {
        var manifest = ReadSampleManifest();
        var scan = PcCompatStaticPatchScanner.Scan(manifest, targetGameRevision: 143);
        var translation = PcCompatCallbackTranslator.Translate(manifest, scan);

        Assert.That(PcCompatRecipeCompiler.TryCompile(manifest, translation, out var report, out var error), Is.True, error);

        var managedRules = report.Rules
            .Where(rule => rule.Op == PcCompatRuleOp.ManagedEventCallback)
            .ToArray();
        Assert.That(managedRules, Has.Length.EqualTo(18));
        Assert.That(managedRules.All(rule => rule.Stage == PcCompatRuleStage.AfterOriginal), Is.True);
        Assert.That(managedRules.All(rule => rule.Source == "managed_event"), Is.True);
        Assert.That(managedRules.All(rule => rule.FeatureId == "managed_callback"), Is.True);
        Assert.That(
            managedRules.All(rule => rule.RequiredCapabilities == PcCompatCapability.AfterOriginalObserve),
            Is.True);

        // Rule ids are the cross-layer contract: native parses the patch id, the
        // managed dispatcher parses the callback identity.
        var patchIds = new HashSet<uint>();
        foreach (var rule in managedRules)
        {
            var parts = rule.Id.Split(':');
            Assert.That(parts, Has.Length.EqualTo(4));
            Assert.That(parts[0], Is.EqualTo("managed_event"));
            Assert.That(uint.TryParse(parts[1], out var patchId), Is.True);
            Assert.That(patchIds.Add(patchId), Is.True, $"duplicate patch id {patchId}");
            Assert.That(parts[2], Is.Not.Empty);
            Assert.That(parts[3], Is.Not.Empty);
        }

        static string ManagedId(string callbackType, string callbackMethod, string[] ids)
            => ids.Single(id =>
            {
                var parts = id.Split(':');
                return parts.Length == 4 && parts[2] == callbackType && parts[3] == callbackMethod;
            });

        var ruleIds = managedRules.Select(rule => rule.Id).ToArray();
        var gameStart = managedRules.Single(rule =>
            rule.Id == ManagedId("JipperResourcePack.Main", "OnGameStart1", ruleIds));
        Assert.That(gameStart.TargetType, Is.EqualTo("scnGame"));
        Assert.That(gameStart.TargetMethod, Is.EqualTo("Play"));
        Assert.That(gameStart.TargetParameterTypes, Is.EqualTo(new[] { "System.Int32", "System.Boolean" }));

        var changeState = managedRules.Single(rule =>
            rule.Id == ManagedId("JipperResourcePack.Main", "OnChangeState", ruleIds));
        Assert.That(changeState.TargetType, Is.EqualTo("MonsterLove.StateMachine.StateBehaviour"));
        Assert.That(changeState.TargetParameterTypes, Is.EqualTo(new[] { "System.Enum" }));

        var gameStops = managedRules.Where(rule =>
        {
            var parts = rule.Id.Split(':');
            return parts.Length == 4 && parts[2] == "JipperResourcePack.Main" && parts[3] == "OnGameStop";
        }).ToArray();
        Assert.That(gameStops, Has.Length.EqualTo(3));
        Assert.That(
            gameStops.Select(rule => rule.TargetType + "." + rule.TargetMethod),
            Is.EquivalentTo(new[]
            {
                "scrUIController.WipeToBlack",
                "scnEditor.ResetScene",
                "scrController.StartLoadingScene"
            }));

        var hitMarginTiming = managedRules.Single(rule =>
            rule.Id == ManagedId("JipperResourcePack.Jongyeol.JStatus", "OnHitMarginChange", ruleIds));
        Assert.That(hitMarginTiming.TargetType, Is.EqualTo("scrMisc"));
        Assert.That(hitMarginTiming.TargetIsStatic, Is.True);
        Assert.That(hitMarginTiming.TargetParameterTypes, Has.Count.EqualTo(6));

        // Prefix patches never get managed-event rules, and postfix targets outside the
        // verified catalog are audited instead of guessed.
        Assert.That(
            managedRules.Any(rule => rule.Id.Contains("HideDebugText")),
            Is.False);
        Assert.That(
            managedRules.Any(rule =>
                rule.Id.Contains("JipperResourcePack.ResourceChanger", StringComparison.Ordinal)),
            Is.False,
            "Descriptor-only ResourceChanger callbacks are already executed by native fixed ops.");
        Assert.That(
            managedRules.Any(rule =>
                (rule.TargetType == "scrFloor" || rule.TargetType == "scrPlanet" ||
                 rule.TargetType == "scnEditor" || rule.TargetType == "scrLogoText") &&
                rule.Id.Contains("ResourceChanger", StringComparison.Ordinal)),
            Is.False);
        Assert.That(
            report.Unsupported.Any(item =>
                item.Id.StartsWith("managed_event.", StringComparison.Ordinal) &&
                item.Id.Contains("Awake_Rewind", StringComparison.Ordinal)),
            Is.True);

        // Managed-event rules share target records with the fixed-op rules, so the
        // runtime bundle keeps one hook per target.
        var bundle = PcCompatRuntimeRuleBundle.FromReport(report);
        Assert.That(bundle.Targets, Has.Count.EqualTo(36));
        var addHit = bundle.Targets.Single(target =>
            target.TypeName == "scrMarginTracker" && target.MethodName == "AddHit");
        Assert.That(addHit.Rules.Count(rule => rule.OpCode == (int)PcCompatRuleOp.ManagedEventCallback),
            Is.EqualTo(3));
        Assert.That(addHit.Rules.Count(rule => rule.OpCode == (int)PcCompatRuleOp.OverlayRecordHit),
            Is.EqualTo(1));

        // The binary recipe validates and the managed reader recovers the same rules.
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "pccompat-managed-event-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var recipePath = Path.Combine(root, "ui_recipe.bin");
            PcCompatUiRecipeBinary.Write(recipePath, manifest, report, 143);
            Assert.That(
                PcCompatUiRecipeBinary.TryValidate(recipePath, out var validationError),
                Is.True,
                validationError);

            var readBack = PcCompatManagedEventRecipeReader.Read(recipePath);
            Assert.That(readBack, Has.Length.EqualTo(managedRules.Length));
            Assert.That(
                readBack.Select(rule => rule.PatchId).Distinct().Count(),
                Is.EqualTo(managedRules.Length));
            var readGameStart = readBack.Single(rule =>
                rule.CallbackType == "JipperResourcePack.Main" && rule.CallbackMethod == "OnGameStart1");
            Assert.That(readGameStart.TargetType, Is.EqualTo("scnGame"));
            Assert.That(readGameStart.TargetMethod, Is.EqualTo("Play"));
            Assert.That(readGameStart.TargetIsStatic, Is.False);
            Assert.That(readGameStart.ParameterTypes, Is.EqualTo(new[] { "System.Int32", "System.Boolean" }));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public void LowersJipperHudConstructorIntoVerifiedNativeGraph()
    {
        var source = ReadSampleManifest();
        var scan = PcCompatStaticPatchScanner.Scan(source, targetGameRevision: 143);
        var translation = PcCompatCallbackTranslator.Translate(source, scan);

        Assert.That(
            PcCompatRecipeCompiler.TryCompile(source, scan, translation, out var report, out var error),
            Is.True,
            error);
        Assert.That(report.UiObjectGraph, Is.Not.Empty);
        Assert.That(report.UiObjectGraph.Count(node => node.ParentId == 0), Is.EqualTo(1));
        Assert.That(report.UiObjectGraph.Select(node => node.Name), Does.Contain("JipperResourcePack Overlay"));
        Assert.That(report.UiLifecyclePrograms, Has.Count.EqualTo(2));
        Assert.That(report.UiLifecyclePrograms.Select(program => program.Trigger),
            Does.Contain(PcCompatUiLifecycleTrigger.BundleLoad));
        Assert.That(report.UiLifecyclePrograms.Select(program => program.Trigger),
            Does.Contain(PcCompatUiLifecycleTrigger.OverlayStateChanged));
        Assert.That(report.UiLifecyclePrograms.Single(program =>
                program.Trigger == PcCompatUiLifecycleTrigger.OverlayStateChanged)
            .Instructions[0].Opcode,
            Is.EqualTo(PcCompatNativeVmOpcode.LoadOverlayVisible));
        Assert.That(report.UiObjectGraph.SelectMany(node => node.Initialization)
                .Select(operation => operation.OpCode),
            Does.Contain(PcCompatUiComponentOpCode.SetTextLineSpacing));
        Assert.That(report.UiObjectGraph.Select(node => node.Components),
            Does.Contain(PcCompatUiComponentMask.RectTransform |
                         PcCompatUiComponentMask.TextMeshProUGUI |
                         PcCompatUiComponentMask.ContentSizeFitter));
        Assert.That(report.UiObjectGraph.SelectMany(node => node.Initialization)
                .Select(operation => operation.OpCode),
            Does.Contain(PcCompatUiComponentOpCode.SetContentSizeHorizontalFit));
        Assert.That(report.UiObjectGraph.SelectMany(node => node.Initialization)
                .Select(operation => operation.OpCode),
            Does.Contain(PcCompatUiComponentOpCode.SetContentSizeVerticalFit));
        Assert.That(report.UiResourceBindings, Is.Not.Empty);
        Assert.That(report.UiResourceBindings.All(binding =>
            binding.Target == PcCompatUiResourceTarget.TextFont &&
            binding.FeatureGroupId == "overlay.font" &&
            binding.AssetName == "MAPLESTORY_OTF_BOLD SDF"), Is.True);
        Assert.That(report.UiResourceBindings.Select(binding => binding.NodeId).Distinct().Count(),
            Is.EqualTo(report.UiResourceBindings.Count));
        Assert.That(report.Unsupported.Any(item =>
                item.Id == "ui_graph.UnsupportedUiComponent" &&
                item.Reason.Contains("ContentSizeFitter", StringComparison.Ordinal)),
            Is.False);
        Assert.That(report.Features.Single(feature => feature.Id == "ui_graph").Status,
            Is.EqualTo(PcCompatFeatureStatus.Partial));
    }

    [Test]
    public void RejectsModsWithoutFeatureRecipe()
    {
        var manifest = new PcModManifest
        {
            FolderPath = "mods/Unknown",
            Id = "UnknownMod",
            DisplayName = "UnknownMod",
            Kind = PcModKind.UnityModManager
        };

        var ok = PcCompatRecipeCompiler.TryCompile(manifest, out var report, out var error);

        Assert.That(ok, Is.False);
        Assert.That(report, Is.Null);
        Assert.That(error, Does.Contain("No verified callback translation"));
    }

    [Test]
    public void RecipeReportSerializesEnumsAsReadableJson()
    {
        var manifest = ReadSampleManifest();
        var scan = PcCompatStaticPatchScanner.Scan(manifest, targetGameRevision: 143);
        var translation = PcCompatCallbackTranslator.Translate(manifest, scan);

        Assert.That(PcCompatRecipeCompiler.TryCompile(manifest, translation, out var report, out var error), Is.True, error);

        var json = PcCompatRecipeReportJson.Serialize(report);
        var restored = PcCompatRecipeReportJson.Deserialize(json);

        Assert.That(json, Does.Contain("\"op\": \"OverlayShow\""));
        Assert.That(json, Does.Contain("\"status\": \"Supported\""));
        Assert.That(restored, Is.Not.Null);
        Assert.That(restored!.Rules, Has.Count.EqualTo(report.Rules.Count));
        Assert.That(restored.Features.Single(feature => feature.Id == "overlay").Status, Is.EqualTo(PcCompatFeatureStatus.Supported));
    }

    [Test]
    public void UiRecipeBinaryCarriesVerifiedLifecycleBytecode()
    {
        var manifest = ReadSampleManifest();
        var scan = PcCompatStaticPatchScanner.Scan(manifest, targetGameRevision: 143);
        var translation = PcCompatCallbackTranslator.Translate(manifest, scan);
        Assert.That(PcCompatRecipeCompiler.TryCompile(manifest, translation, out var source, out var error), Is.True, error);

        var lifecycle = new PcCompatUiLifecycleProgram
        {
            Id = "test.lifecycle",
            RuntimeRuleId = 501,
            Trigger = PcCompatUiLifecycleTrigger.BundleLoad,
            ClockDomain = PcCompatUiClockDomain.Realtime,
            Flags = PcCompatUiLifecycleFlags.RequireInputSnapshot,
            InstructionBudget = 64,
            CommandType = 7,
            TargetId = 9,
            InitialDelayNs = 1_000_000,
            DeferredRetryDelayNs = 5_000_000,
            Instructions = new[]
            {
                new PcCompatNativeVmInstruction(PcCompatNativeVmOpcode.LoadConstI64, Destination: 0, Payload: 42),
                new PcCompatNativeVmInstruction(PcCompatNativeVmOpcode.Return)
            }
        };
        var objectGraph = new[]
        {
            new PcCompatUiObjectNode
            {
                Id = 9,
                Name = "Test.Canvas",
                Components = PcCompatUiComponentMask.RectTransform |
                             PcCompatUiComponentMask.Canvas,
                Flags = PcCompatUiObjectFlags.ActiveInitially,
                Initialization = new[]
                {
                    new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetCanvasRenderMode,
                        Payload0 = 0
                    }
                }
            },
            new PcCompatUiObjectNode
            {
                Id = 10,
                ParentId = 9,
                Name = "Test.Text",
                Components = PcCompatUiComponentMask.RectTransform |
                             PcCompatUiComponentMask.TextMeshProUGUI,
                Initialization = new[]
                {
                    new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetText,
                        StringValue = "hello"
                    }
                }
            }
        };
        var report = new PcCompatRecipeCompileReport
        {
            ModId = source.ModId,
            RecipeId = source.RecipeId,
            Compatibility = source.Compatibility,
            Features = source.Features,
            Rules = source.Rules,
            Unsupported = source.Unsupported,
            RequiredCapabilities = source.RequiredCapabilities,
            UiObjectGraph = objectGraph,
            UiResourceBindings = new[]
            {
                new PcCompatUiResourceBinding
                {
                    NodeId = 10,
                    Target = PcCompatUiResourceTarget.TextFont,
                    FeatureGroupId = "overlay.font",
                    AssetName = "Test SDF",
                    ExpectedType = "TMP_FontAsset"
                }
            },
            UiLifecyclePrograms = new[] { lifecycle }
        };

        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "pccompat-ui-recipe-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "ui_recipe.bin");
        Directory.CreateDirectory(root);
        try
        {
            PcCompatUiRecipeBinary.Write(path, manifest, report, 143);
            var bytes = File.ReadAllBytes(path);
            Assert.That(PcCompatUiRecipeBinary.TryValidate(bytes, out var validationError), Is.True, validationError);
            var flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
            Assert.That(flags & 4u, Is.EqualTo(4u));
            Assert.That(flags & 8u, Is.EqualTo(8u));
            Assert.That(flags & 16u, Is.EqualTo(16u));

            var resourceEntry = FindUiRecipeSectionEntry(bytes, 9);
            Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(resourceEntry + 12, 4)), Is.EqualTo(1u));
            Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(resourceEntry + 16, 4)), Is.EqualTo(32u));

            var invalidResourceTarget = bytes.ToArray();
            var resourceOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                invalidResourceTarget.AsSpan(resourceEntry + 4, 4));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                invalidResourceTarget.AsSpan(checked((int)resourceOffset + 4), 4),
                99);
            RecomputeUiRecipeCrc(invalidResourceTarget);
            Assert.That(
                PcCompatUiRecipeBinary.TryValidate(invalidResourceTarget, out var resourceError),
                Is.False,
                "managed validation must reject a resource target that native validation rejects");
            Assert.That(resourceError, Does.Contain("resource").IgnoreCase);

            var invalidParameterDescriptor = bytes.ToArray();
            var parameterEntry = FindUiRecipeSectionEntry(invalidParameterDescriptor, 2);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                invalidParameterDescriptor.AsSpan(parameterEntry + 16, 4),
                1);
            RecomputeUiRecipeCrc(invalidParameterDescriptor);
            Assert.That(
                PcCompatUiRecipeBinary.TryValidate(invalidParameterDescriptor, out var parameterError),
                Is.False,
                "managed validation must reject a parameter table that native validation rejects");
            Assert.That(parameterError, Does.Contain("table").IgnoreCase);

            var invalidTargetString = bytes.ToArray();
            var stringEntry = FindUiRecipeSectionEntry(invalidTargetString, 1);
            var targetEntry = FindUiRecipeSectionEntry(invalidTargetString, 3);
            var stringSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                invalidTargetString.AsSpan(stringEntry + 8, 4));
            var targetOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                invalidTargetString.AsSpan(targetEntry + 4, 4));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                invalidTargetString.AsSpan(checked((int)targetOffset + 12), 4),
                stringSize);
            RecomputeUiRecipeCrc(invalidTargetString);
            Assert.That(
                PcCompatUiRecipeBinary.TryValidate(invalidTargetString, out var targetError),
                Is.False,
                "managed validation must reject target string offsets before cache publication");
            Assert.That(targetError, Does.Contain("target").IgnoreCase);

            var excessiveLifecycleBudget = bytes.ToArray();
            var lifecycleEntry = FindUiRecipeSectionEntry(excessiveLifecycleBudget, 7);
            var lifecycleOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                excessiveLifecycleBudget.AsSpan(lifecycleEntry + 4, 4));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                excessiveLifecycleBudget.AsSpan(checked((int)lifecycleOffset + 28), 4),
                1_000_001);
            RecomputeUiRecipeCrc(excessiveLifecycleBudget);
            Assert.That(
                PcCompatUiRecipeBinary.TryValidate(excessiveLifecycleBudget, out var lifecycleError),
                Is.False,
                "managed and native lifecycle instruction-budget limits must match");
            Assert.That(lifecycleError, Does.Contain("budget").IgnoreCase);

            Assert.That(PcCompatRecipeBundleCache.ComputeCacheKey(manifest, report),
                Is.Not.EqualTo(PcCompatRecipeBundleCache.ComputeCacheKey(manifest, source)));

            var invalid = new PcCompatUiLifecycleProgram
            {
                Id = "test.invalid-branch",
                RuntimeRuleId = 502,
                CommandType = 7,
                Instructions = new[]
                {
                    new PcCompatNativeVmInstruction(PcCompatNativeVmOpcode.Branch, Immediate: 10)
                }
            };
            var invalidReport = new PcCompatRecipeCompileReport
            {
                ModId = source.ModId,
                RecipeId = source.RecipeId,
                Compatibility = source.Compatibility,
                Rules = source.Rules,
                RequiredCapabilities = source.RequiredCapabilities,
                UiLifecyclePrograms = new[] { invalid }
            };
            Assert.That(
                () => PcCompatUiRecipeBinary.Write(Path.Combine(root, "invalid.bin"), manifest, invalidReport, 143),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("invalid bytecode"));

            var cyclicGraphReport = new PcCompatRecipeCompileReport
            {
                ModId = source.ModId,
                RecipeId = source.RecipeId,
                Compatibility = source.Compatibility,
                Rules = source.Rules,
                RequiredCapabilities = source.RequiredCapabilities,
                UiObjectGraph = new[]
                {
                    new PcCompatUiObjectNode
                    {
                        Id = 1,
                        ParentId = 2,
                        Name = "Cycle.A"
                    },
                    new PcCompatUiObjectNode
                    {
                        Id = 2,
                        ParentId = 1,
                        Name = "Cycle.B"
                    }
                }
            };
            Assert.That(
                () => PcCompatUiRecipeBinary.Write(Path.Combine(root, "cycle.bin"), manifest, cyclicGraphReport, 143),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("cycle"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public void DefaultRuntimeRegistrationUsesRecipeWithoutExecutingManagedMod()
    {
        var sample = ReadSampleManifest();
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "pccompat-recipe-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "JipperResourcePack");
        CopyDirectory(sample.FolderPath, dir);
        Assert.That(PcModManifestReader.TryRead(dir, out var manifest, out var manifestError), Is.True, manifestError);

        try
        {
            PcCompatRuntime.RegisterMod(manifest);

            var report = PcCompatRuntime.GetRecipeReport("JipperResourcePack");
            var bundle = PcCompatRuntime.GetRecipeBundle("JipperResourcePack");
            var reportPath = Path.Combine(dir, ".pccompat", "recipe_report.json");

            Assert.That(report, Is.Not.Null);
            Assert.That(bundle, Is.Not.Null);
            Assert.That(report!.Rules, Has.Count.EqualTo(54));
            Assert.That(File.Exists(reportPath), Is.True);
            Assert.That(File.Exists(bundle!.ReportPath), Is.True);
            Assert.That(File.Exists(bundle.RulesPath), Is.True);
            Assert.That(File.Exists(bundle.RecipePath), Is.True);
            Assert.That(File.Exists(bundle.CompleteMarkerPath), Is.True);
            Assert.That(bundle.BundleDirectory, Does.StartWith(Path.Combine(root, "compiled", "JipperResourcePack")));
            Assert.That(File.ReadAllText(reportPath), Does.Contain("\"recipeId\": \"xphorror.recipe.verified_fixed_op.v1\""));
            Assert.That(PcCompatRuntime.SnapshotSessions(), Is.Empty);
        }
        finally
        {
            PcCompatRuntime.UnregisterMod(manifest);
            Directory.Delete(root, true);
        }
    }

    [Test]
    public void RecipeOnlyRuntimeRejectsUnknownPcModWithoutRecipe()
    {
        const string recipeOnlyVariable = "STARRAY_PCMOD_COMPAT_RECIPE_ONLY";
        var previousRecipeOnly = Environment.GetEnvironmentVariable(recipeOnlyVariable);
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "pccompat-unknown-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "UnknownMod");
        Directory.CreateDirectory(dir);
        var manifest = new PcModManifest
        {
            FolderPath = dir,
            Id = "UnknownMod",
            DisplayName = "UnknownMod",
            Kind = PcModKind.UnityModManager
        };

        try
        {
            Environment.SetEnvironmentVariable(recipeOnlyVariable, "1");
            var ex = Assert.Throws<NotSupportedException>(() => PcCompatRuntime.RegisterMod(manifest));
            Assert.That(ex!.Message, Does.Contain("No verified fixed-op or managed callback rules"));
            Assert.That(PcCompatRuntime.GetRecipeReport("UnknownMod"), Is.Null);
            Assert.That(PcCompatRuntime.SnapshotSessions(), Is.Empty);
        }
        finally
        {
            Environment.SetEnvironmentVariable(recipeOnlyVariable, previousRecipeOnly);
            PcCompatRuntime.UnregisterMod(manifest);
            Directory.Delete(root, true);
        }
    }

    [Test]
    public void RuntimeRegistrationFailsClosedWhenCompiledRecipeCannotBePublished()
    {
        var source = ReadSampleManifest();
        var prepared = PcCompatRuntime.PrepareMod(source);
        var invalidManifest = CloneManifest(source, "\0invalid-folder", source.Id);
        var invalidPrepared = new PcCompatPreparedMod
        {
            Manifest = invalidManifest,
            StaticScan = prepared.StaticScan,
            CallbackTranslation = prepared.CallbackTranslation,
            HasRecipe = prepared.HasRecipe,
            RecipeReport = prepared.RecipeReport,
            RecipeError = prepared.RecipeError,
            ManagedAssemblyBundle = prepared.ManagedAssemblyBundle
        };

        try
        {
            Assert.That(
                () => PcCompatRuntime.RegisterPreparedMod(invalidPrepared),
                Throws.Exception,
                "a MOD must not report loaded when its native ui_recipe.bin was never published");
            Assert.That(PcCompatRuntime.GetRecipeBundle(source.Id), Is.Null);
        }
        finally
        {
            PcCompatRuntime.UnregisterMod(invalidManifest);
        }
    }

    [Test]
    public void LegacyManagedOracleEnvironmentCannotExecuteUnrewrittenMod()
    {
        var repoRoot = FindRepoRoot();
        var sampleModDir = Path.Combine(repoRoot, "JipperResourcePack_release");

        Assume.That(Directory.Exists(sampleModDir), Is.True, $"missing sample mod dir: {sampleModDir}");

        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "pccompat-oracle-" + Guid.NewGuid().ToString("N"));
        var modDir = Path.Combine(root, "JipperResourcePack");
        CopyDirectory(sampleModDir, modDir);

        var ok = PcModManifestReader.TryRead(modDir, out var manifest, out var error);
        Assert.That(ok, Is.True, error);

        var previousOracle = Environment.GetEnvironmentVariable("STARRAY_PCMOD_COMPAT_MANAGED_ORACLE");
        Environment.SetEnvironmentVariable("STARRAY_PCMOD_COMPAT_MANAGED_ORACLE", "1");
        try
        {
            PcCompatRuntime.RegisterMod(manifest);

            Assert.That(PcCompatRuntime.GetRecipeReport("JipperResourcePack"), Is.Not.Null);
            Assert.That(PcCompatRuntime.GetRecipeBundle("JipperResourcePack"), Is.Not.Null);
            Assert.That(PcCompatRuntime.SnapshotSessions(), Is.Empty);
            Assert.That(PcCompatRuntime.PatchRegistry.Snapshot(), Is.Empty);
        }
        finally
        {
            PcCompatRuntime.UnregisterMod(manifest);
            Environment.SetEnvironmentVariable("STARRAY_PCMOD_COMPAT_MANAGED_ORACLE", previousOracle);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            TryDeleteDirectory(root);
        }
    }

    [Test]
    public void RecipeBundleCacheUsesStableCacheKey()
    {
        var source = ReadSampleManifest();
        var scanManifest = CloneManifest(source, source.FolderPath, "GenericOverlayCache");
        var scan = PcCompatStaticPatchScanner.Scan(scanManifest, targetGameRevision: 143);
        var translation = PcCompatCallbackTranslator.Translate(scanManifest, scan);
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "pccompat-cache-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "GenericOverlayCache");
        Directory.CreateDirectory(dir);
        var manifest = CloneManifest(source, dir, "GenericOverlayCache");
        Assert.That(File.Exists(source.EntryAssemblyPath), Is.True);
        File.Copy(source.EntryAssemblyPath, manifest.EntryAssemblyPath);

        try
        {
            Assert.That(PcCompatRecipeCompiler.TryCompile(manifest, translation, out var report, out var error), Is.True, error);

            var first = PcCompatRecipeBundleCache.Write(manifest, report);
            var second = PcCompatRecipeBundleCache.Write(manifest, report);

            Assert.That(second.CacheKey, Is.EqualTo(first.CacheKey));
            Assert.That(second.BundleDirectory, Is.EqualTo(first.BundleDirectory));
            Assert.That(File.Exists(first.CompleteMarkerPath), Is.True);
            Assert.That(File.Exists(first.RecipePath), Is.True);
            var recipeBytes = File.ReadAllBytes(first.RecipePath);
            Assert.That(PcCompatUiRecipeBinary.HasValidHeader(recipeBytes), Is.True);
            Assert.That(PcCompatUiRecipeBinary.TryValidate(recipeBytes, out var recipeError), Is.True, recipeError);
            Assert.That(recipeBytes.Length, Is.GreaterThan(PcCompatUiRecipeBinary.HeaderSize));
            var corruptedRecipe = recipeBytes.ToArray();
            corruptedRecipe[^1] ^= 0x5A;
            Assert.That(PcCompatUiRecipeBinary.TryValidate(corruptedRecipe, out var corruptedError), Is.False);
            Assert.That(corruptedError, Does.Contain("checksum"));
            var rulesJson = File.ReadAllText(first.RulesPath);
            var runtimeBundle = PcCompatRuntimeRuleBundle.Deserialize(rulesJson);

            Assert.That(rulesJson, Does.Contain("\"formatVersion\": \"mvp-fixed-op-v3\""));
            Assert.That(rulesJson, Does.Contain("\"targets\""));
            Assert.That(rulesJson, Does.Not.Contain("\"features\""));
            Assert.That(runtimeBundle, Is.Not.Null);
            var runtimeRules = runtimeBundle!.Targets.SelectMany(target => target.Rules).ToArray();
            Assert.That(runtimeBundle.Targets, Has.Count.EqualTo(36));
            Assert.That(runtimeRules, Has.Length.EqualTo(54));
            Assert.That(runtimeBundle.Targets.Single(target =>
                    target.TypeName == "scnEditor" && target.MethodName == "OttoBlink")
                .Rules.Single().Op, Is.EqualTo("ResourceApplyEditorRabbit"));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "scrMarginTracker" && target.MethodName == "CalculatePercentAcc").Rules.Select(rule => rule.Op), Does.Contain("PublishMarginSnapshot"));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "scrMarginTracker" && target.MethodName == "CalculatePercentAcc").AbiKind, Is.EqualTo("InstanceVoid0"));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "scrMarginTracker" && target.MethodName == "AddHit").AbiKind, Is.EqualTo("InstanceVoidInt1"));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "scrMarginTracker" && target.MethodName == "Reset").AbiKind, Is.EqualTo("InstanceVoid0"));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "scrPlanet" && target.MethodName == "MoveToNextFloor").ParamCount, Is.EqualTo(3));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "scrPlanet" && target.MethodName == "MoveToNextFloor").AbiKind, Is.EqualTo("InstanceVoidPtrFloatInt"));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "scrPlayer" && target.MethodName == "Hit").ParamCount, Is.EqualTo(1));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "scrPlayer" && target.MethodName == "Hit").AbiKind, Is.EqualTo("InstanceBool1"));
            var hitInputEvent = runtimeBundle.Targets.Single(target =>
                target.TypeName == "scrPlayer" && target.MethodName == "HitInputEvent");
            Assert.That(hitInputEvent.AbiKind, Is.EqualTo("InstanceBoolBoolInt"));
            Assert.That(hitInputEvent.ReturnType, Is.EqualTo("System.Boolean"));
            Assert.That(hitInputEvent.ParameterTypes,
                Is.EqualTo(new[] { "System.Boolean", "InputEventState" }));
            Assert.That(hitInputEvent.Rules.Single().Op, Is.EqualTo("GameplayAcceptedObserve"));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "scrPlayer" && target.MethodName == "Die").ParamCount, Is.EqualTo(4));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "scrPlayer" && target.MethodName == "Die").AbiKind, Is.EqualTo("InstanceVoidBoolBoolPtrBool"));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "scrMisc" && target.MethodName == "GetHitMargin").ParamCount, Is.EqualTo(6));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "scrMisc" && target.MethodName == "GetHitMargin").AbiKind, Is.EqualTo("StaticIntFloatFloatBoolFloatFloatDouble"));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "scnGame").ParamCount, Is.EqualTo(2));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "scnGame").AbiKind, Is.EqualTo("InstanceBool2"));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "scrUIController").ParamCount, Is.EqualTo(3));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "scrUIController").AbiKind, Is.EqualTo("InstanceVoid3"));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "scrMistakesManager").AbiKind, Is.EqualTo("StaticVoid1"));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "scrController" && target.MethodName == "QuitToMainMenu").AbiKind, Is.EqualTo("InstanceVoid0"));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "scnEditor" && target.MethodName == "ResetScene").AbiKind, Is.EqualTo("InstanceVoidInt1"));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "scnEditor" && target.MethodName == "SwitchToEditMode").AbiKind, Is.EqualTo("InstanceVoidInt1"));
            var playerControlTelemetry = runtimeBundle.Targets.Single(target =>
                target.TypeName == "scrController" && target.MethodName == "PlayerControl_Update");
            Assert.That(playerControlTelemetry.AbiKind, Is.EqualTo("InstanceVoid0"));
            Assert.That(playerControlTelemetry.Rules.Single().Op, Is.EqualTo("OverlayPollTelemetry"));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "scrFloor" && target.MethodName == "SetTileColor").AbiKind, Is.EqualTo("InstanceVoidColor1"));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "PlanetRenderer" && target.MethodName == "SetPlanetColor").AbiKind, Is.EqualTo("InstanceVoidColor1"));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "PlanetRenderer" && target.MethodName == "SetTailColor").AbiKind, Is.EqualTo("InstanceVoidColor1"));
            Assert.That(runtimeBundle.Targets.Single(target => target.TypeName == "PlanetRenderer" && target.MethodName == "SetColor").AbiKind, Is.EqualTo("InstanceVoidPtrBool"));

            var changeState = runtimeBundle.Targets.Single(target => target.TypeName == "MonsterLove.StateMachine.StateBehaviour");
            Assert.That(changeState.Namespace, Is.EqualTo("MonsterLove.StateMachine"));
            Assert.That(changeState.IsStatic, Is.False);
            Assert.That(changeState.GenericArity, Is.Zero);
            Assert.That(changeState.ReturnType, Is.EqualTo("System.Void"));
            Assert.That(changeState.ParameterTypes, Is.EqualTo(new[] { "System.Enum" }));

            var getHitMargin = runtimeBundle.Targets.Single(target => target.TypeName == "scrMisc");
            Assert.That(getHitMargin.IsStatic, Is.True);
            Assert.That(getHitMargin.ReturnType, Is.EqualTo("HitMargin"));
            Assert.That(getHitMargin.ParameterTypes, Is.EqualTo(new[]
            {
                "System.Single",
                "System.Single",
                "System.Boolean",
                "System.Single",
                "System.Single",
                "System.Double"
            }));

            File.WriteAllText(first.RulesPath, "{}");
            File.WriteAllText(first.ReportPath, "{}");
            var repaired = PcCompatRecipeBundleCache.Write(manifest, report);
            Assert.That(
                File.ReadAllText(repaired.RulesPath),
                Is.EqualTo(PcCompatRuntimeRuleBundle.Serialize(PcCompatRuntimeRuleBundle.FromReport(report))));
            Assert.That(
                File.ReadAllText(repaired.ReportPath),
                Is.EqualTo(PcCompatRecipeReportJson.Serialize(report)));

            File.AppendAllText(manifest.EntryAssemblyPath, "cache-key-mutation");
            Assert.That(PcCompatRecipeBundleCache.ComputeCacheKey(manifest, report), Is.Not.EqualTo(first.CacheKey));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public void RecipeBundleCacheExercisesPlatformCryptographyRuntime()
    {
        var references = typeof(PcCompatRecipeBundleCache).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        Assert.That(references, Does.Contain("System.Security.Cryptography"));
    }

    [Test]
    public void RuntimeRuleBundleGroupsRulesByIl2CppTarget()
    {
        var report = new PcCompatRecipeCompileReport
        {
            ModId = "GroupedMod",
            RecipeId = "recipe.grouped",
            Compatibility = "supported",
            Rules = new[]
            {
                new PcCompatCompiledRule
                {
                    Id = "second",
                    FeatureId = "overlay",
                    TargetType = "Target",
                    TargetMethod = "Method",
                    ParamCount = 0,
                    TargetIsStatic = false,
                    TargetReturnType = "System.Void",
                    TargetParameterTypes = Array.Empty<string>(),
                    Stage = PcCompatRuleStage.AfterOriginal,
                    Op = PcCompatRuleOp.OverlayHide,
                    RequiredCapabilities = PcCompatCapability.UiOverlay
                },
                new PcCompatCompiledRule
                {
                    Id = "first",
                    FeatureId = "overlay",
                    TargetType = "Target",
                    TargetMethod = "Method",
                    ParamCount = 0,
                    TargetIsStatic = false,
                    TargetReturnType = "System.Void",
                    TargetParameterTypes = Array.Empty<string>(),
                    Stage = PcCompatRuleStage.AfterOriginal,
                    Op = PcCompatRuleOp.OverlayShow,
                    RequiredCapabilities = PcCompatCapability.UiOverlay
                }
            },
            RequiredCapabilities = PcCompatCapability.UiOverlay
        };

        var bundle = PcCompatRuntimeRuleBundle.FromReport(report);

        Assert.That(bundle.FormatVersion, Is.EqualTo("mvp-fixed-op-v3"));
        Assert.That(bundle.RequiredCapabilities, Is.EqualTo((ulong)PcCompatCapability.UiOverlay));
        Assert.That(bundle.Targets, Has.Count.EqualTo(1));
        Assert.That(bundle.Targets[0].AssemblyName, Is.EqualTo("Assembly-CSharp"));
        Assert.That(bundle.Targets[0].TypeName, Is.EqualTo("Target"));
        Assert.That(bundle.Targets[0].MethodName, Is.EqualTo("Method"));
        Assert.That(bundle.Targets[0].ReturnType, Is.EqualTo("System.Void"));
        Assert.That(bundle.Targets[0].ParameterTypes, Is.Empty);
        Assert.That(bundle.Targets[0].AbiKind, Is.EqualTo("InstanceVoid0"));
        Assert.That(bundle.Targets[0].Rules.Select(rule => rule.Id), Is.EqualTo(new[] { "first", "second" }));
        Assert.That(bundle.Targets[0].Rules[0].OpCode, Is.EqualTo((int)PcCompatRuleOp.OverlayShow));
    }

    [TestCase("System.Boolean")]
    [TestCase("System.Int32")]
    [TestCase("HitMargin")]
    public void RuntimeRuleBundleClassifiesStaticGp32SetterAsStaticVoid1(string parameterType)
    {
        var report = new PcCompatRecipeCompileReport
        {
            ModId = "JipperResourcePack",
            RecipeId = "recipe.rdc-auto",
            Compatibility = "supported",
            Rules = new[]
            {
                new PcCompatCompiledRule
                {
                    Id = "managed_event.rdc_auto",
                    FeatureId = "status",
                    TargetType = "RDC",
                    TargetMethod = "set_auto",
                    ParamCount = 1,
                    TargetIsStatic = true,
                    TargetReturnType = "System.Void",
                    TargetParameterTypes = new[] { parameterType },
                    Stage = PcCompatRuleStage.AfterOriginal,
                    Op = PcCompatRuleOp.ManagedEventCallback,
                    RequiredCapabilities = PcCompatCapability.AfterOriginalObserve
                }
            }
        };

        var target = PcCompatRuntimeRuleBundle.FromReport(report).Targets.Single();

        Assert.That(target.AbiKind, Is.EqualTo("StaticVoid1"));
    }

    [Test]
    public void RuntimeRuleBundleDoesNotMergeSameCountDifferentSignatureOverloads()
    {
        static PcCompatCompiledRule Rule(string id, string parameterType) => new()
        {
            Id = id,
            FeatureId = "overlay",
            TargetType = "OverloadedTarget",
            TargetMethod = "Apply",
            ParamCount = 1,
            TargetIsStatic = false,
            TargetReturnType = "System.Void",
            TargetParameterTypes = new[] { parameterType },
            Stage = PcCompatRuleStage.AfterOriginal,
            Op = PcCompatRuleOp.OverlayHide,
            RequiredCapabilities = PcCompatCapability.UiOverlay
        };

        var report = new PcCompatRecipeCompileReport
        {
            ModId = "OverloadMod",
            RecipeId = "recipe.overload",
            Compatibility = "supported",
            Rules = new[] { Rule("int", "System.Int32"), Rule("bool", "System.Boolean") },
            RequiredCapabilities = PcCompatCapability.UiOverlay
        };

        var bundle = PcCompatRuntimeRuleBundle.FromReport(report);

        Assert.That(bundle.Targets, Has.Count.EqualTo(2));
        Assert.That(
            bundle.Targets.Select(target => target.ParameterTypes.Single()),
            Is.EquivalentTo(new[] { "System.Int32", "System.Boolean" }));
    }

    [Test]
    public void RuntimeRuleBundleKeepsSameSignatureTargetsFromDifferentAssembliesSeparate()
    {
        static PcCompatCompiledRule Rule(string id, string assemblyName) => new()
        {
            Id = id,
            FeatureId = "overlay",
            TargetAssemblyName = assemblyName,
            TargetType = "SharedTarget",
            TargetMethod = "Apply",
            ParamCount = 0,
            TargetIsStatic = false,
            TargetReturnType = "System.Void",
            TargetParameterTypes = Array.Empty<string>(),
            Stage = PcCompatRuleStage.AfterOriginal,
            Op = PcCompatRuleOp.OverlayHide,
            RequiredCapabilities = PcCompatCapability.UiOverlay
        };

        var report = new PcCompatRecipeCompileReport
        {
            ModId = "AssemblyIdentityMod",
            RecipeId = "recipe.assembly-identity",
            Compatibility = "supported",
            Rules = new[]
            {
                Rule("game", "Assembly-CSharp"),
                Rule("plugin", "Another.Game.Assembly")
            },
            RequiredCapabilities = PcCompatCapability.UiOverlay
        };

        var bundle = PcCompatRuntimeRuleBundle.FromReport(report);

        Assert.That(bundle.Targets, Has.Count.EqualTo(2));
        Assert.That(
            bundle.Targets.Select(target => target.AssemblyName),
            Is.EquivalentTo(new[] { "Assembly-CSharp", "Another.Game.Assembly" }));
    }

    [Test]
    public void RuntimeRuleBundleNormalizesAssemblySuffixWhitespaceAndCase()
    {
        static PcCompatCompiledRule Rule(string id, string assemblyName) => new()
        {
            Id = id,
            FeatureId = "overlay",
            TargetAssemblyName = assemblyName,
            TargetType = "Target",
            TargetMethod = "Apply",
            ParamCount = 0,
            TargetIsStatic = false,
            TargetReturnType = "System.Void",
            TargetParameterTypes = Array.Empty<string>(),
            Stage = PcCompatRuleStage.AfterOriginal,
            Op = PcCompatRuleOp.OverlayHide,
            RequiredCapabilities = PcCompatCapability.UiOverlay
        };

        var report = new PcCompatRecipeCompileReport
        {
            ModId = "AssemblyNormalizationMod",
            RecipeId = "recipe.assembly-normalization",
            Compatibility = "supported",
            Rules = new[]
            {
                Rule("first", "Assembly-CSharp"),
                Rule("second", " assembly-csharp.DLL ")
            },
            RequiredCapabilities = PcCompatCapability.UiOverlay
        };

        var bundle = PcCompatRuntimeRuleBundle.FromReport(report);

        Assert.That(bundle.Targets, Has.Count.EqualTo(1));
        Assert.That(bundle.Targets[0].AssemblyName, Is.EqualTo("Assembly-CSharp"));
        Assert.That(bundle.Targets[0].Rules.Select(rule => rule.Id), Is.EqualTo(new[] { "first", "second" }));
    }

    [Test]
    public void RuntimeRuleBundleRejectsParamCountThatDisagreesWithSignature()
    {
        var report = new PcCompatRecipeCompileReport
        {
            ModId = "InvalidMod",
            RecipeId = "recipe.invalid",
            Compatibility = "unsupported",
            Rules = new[]
            {
                new PcCompatCompiledRule
                {
                    Id = "invalid",
                    FeatureId = "overlay",
                    TargetType = "Target",
                    TargetMethod = "Method",
                    ParamCount = 0,
                    TargetIsStatic = false,
                    TargetReturnType = "System.Void",
                    TargetParameterTypes = new[] { "System.Int32" },
                    Op = PcCompatRuleOp.OverlayHide,
                    RequiredCapabilities = PcCompatCapability.UiOverlay
                }
            }
        };

        Assert.That(
            () => PcCompatRuntimeRuleBundle.FromReport(report),
            Throws.InvalidOperationException.With.Message.Contains("does not match its complete signature"));
    }

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

    private static PcModManifest ReadSampleManifest()
    {
        var sampleDir = Path.Combine(FindRepoRoot(), "JipperResourcePack_release");
        Assume.That(Directory.Exists(sampleDir), Is.True, $"missing sample mod dir: {sampleDir}");
        Assert.That(PcModManifestReader.TryRead(sampleDir, out var manifest, out var error), Is.True, error);
        return manifest;
    }

    private static PcModManifest CloneManifest(PcModManifest source, string folderPath, string id)
        => new()
        {
            FolderPath = folderPath,
            Id = id,
            DisplayName = id,
            Author = source.Author,
            Version = source.Version,
            AssemblyName = source.AssemblyName,
            EntryMethod = source.EntryMethod,
            Kind = source.Kind,
            JAModAssemblyPath = source.JAModAssemblyPath,
            JAModClassName = source.JAModClassName,
            AssemblyRequireModPath = source.AssemblyRequireModPath,
            Requirements = source.Requirements,
            LoadAfter = source.LoadAfter,
            RawInfoJson = source.RawInfoJson,
            RawJAModInfoJson = source.RawJAModInfoJson
        };

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);

        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
            // Collectible AssemblyLoadContext can release loaded DLL handles after
            // the test assertion has completed; cleanup failure must not mask the
            // oracle-mode behavior being tested.
        }
    }

    private static int FindUiRecipeSectionEntry(byte[] bytes, uint sectionType)
    {
        var tableOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(88, 4));
        var sectionCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4));
        for (var index = 0u; index < sectionCount; ++index)
        {
            var entry = checked((int)(tableOffset + index * PcCompatUiRecipeBinary.SectionEntrySize));
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry, 4)) == sectionType)
                return entry;
        }

        throw new InvalidDataException($"ui recipe section {sectionType} was not found");
    }

    private static void RecomputeUiRecipeCrc(byte[] bytes)
    {
        bytes.AsSpan(84, 4).Clear();
        uint crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; ++bit)
            {
                var mask = 0u - (crc & 1u);
                crc = (crc >> 1) ^ (0xEDB88320u & mask);
            }
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(84, 4), ~crc);
    }
}

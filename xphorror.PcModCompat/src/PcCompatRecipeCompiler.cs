namespace Xphorror.PcModCompat;

public static class PcCompatRecipeCompiler
{
    private const string RecipeId = "xphorror.recipe.verified_fixed_op.v1";

    public static bool TryCompile(PcModManifest manifest, out PcCompatRecipeCompileReport report, out string? error)
    {
        report = null!;
        error = $"No verified callback translation was supplied for mod id={manifest.Id}";
        return false;
    }

    public static bool TryCompile(
        PcModManifest manifest,
        PcCompatCallbackTranslationReport? callbackTranslation,
        out PcCompatRecipeCompileReport report,
        out string? error)
        => TryCompile(manifest, null, callbackTranslation, out report, out error);

    public static bool TryCompile(
        PcModManifest manifest,
        PcCompatStaticPatchScanReport? staticScan,
        PcCompatCallbackTranslationReport? callbackTranslation,
        out PcCompatRecipeCompileReport report,
        out string? error)
    {
        report = null!;
        error = null;

        if (callbackTranslation == null)
        {
            error = $"No verified callback translation was supplied for mod id={manifest.Id}";
            return false;
        }

        if (!callbackTranslation.ModId.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Callback translation belongs to mod id={callbackTranslation.ModId}, expected {manifest.Id}";
            return false;
        }

        var rules = callbackTranslation.Rules
            .Where(rule => rule.DefaultEnabled)
            .GroupBy(RuleShape, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(rule => rule.Id, StringComparer.Ordinal)
            .ToList();

        if (rules.Count == 0)
        {
            error = $"No verified fixed-op callback rules are available for mod id={manifest.Id}";
            return false;
        }

        AddPlatformRules(rules);
        var managedEventMisses = EmitManagedEventRules(rules, callbackTranslation);

        var unsupported = callbackTranslation.Items
            .Where(item => item.Status is PcCompatCallbackTranslationStatus.NotMapped or
                                          PcCompatCallbackTranslationStatus.Unsupported)
            .Select(item => new PcCompatUnsupportedItem
            {
                Id = $"{item.CallbackType}.{item.CallbackMethod}@{item.TargetType}.{item.TargetMethod}",
                Reason = item.Reason,
                Severity = item.Status == PcCompatCallbackTranslationStatus.Unsupported
                    ? "warning"
                    : "info"
            })
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

        if (managedEventMisses.Length != 0)
        {
            unsupported = unsupported
                .Concat(managedEventMisses)
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
        }

        var features = rules
            .GroupBy(rule => rule.FeatureId, StringComparer.Ordinal)
            .Select(group => new PcCompatCompiledFeature
            {
                Id = group.Key,
                DisplayName = FeatureDisplayName(group.Key),
                Status = PcCompatFeatureStatus.Supported,
                RuleIds = group.Select(rule => rule.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                Notes = new[] { FeatureNote(group.Key) }
            })
            .OrderBy(feature => feature.Id, StringComparer.Ordinal)
            .ToList();

        if (unsupported.Length != 0)
        {
            features.Add(new PcCompatCompiledFeature
            {
                Id = "untranslated_callbacks",
                DisplayName = "Untranslated callbacks",
                Status = PcCompatFeatureStatus.Partial,
                Notes = new[] { "Callbacks outside the verified fixed-op catalog remain disabled and are listed in the audit report." }
            });
        }

        PcCompatUiGraphLoweringResult? uiLowering = null;
        if (staticScan != null)
        {
            uiLowering = PcCompatUiGraphLowerer.Lower(manifest, staticScan);
            foreach (var issue in uiLowering.Issues)
            {
                unsupported = unsupported
                    .Append(new PcCompatUnsupportedItem
                    {
                        Id = "ui_graph." + issue.Code,
                        Reason = issue.Message +
                                 (issue.Method == null ? string.Empty : $" method={issue.Method}") +
                                 (issue.IlOffset == null ? string.Empty : $" il=0x{issue.IlOffset.Value:X4}"),
                        Severity = issue.Severity == "info" ? "info" : "warning"
                    })
                    .ToArray();
            }
            unsupported = unsupported
                .GroupBy(item => item.Id + "\0" + item.Reason, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
        }

        if (uiLowering is { HasGraph: true })
        {
            features.Add(new PcCompatCompiledFeature
            {
                Id = "ui_graph",
                DisplayName = "Translated Unity UI graph",
                Status = uiLowering.Issues.Count == 0
                    ? PcCompatFeatureStatus.Supported
                    : PcCompatFeatureStatus.Partial,
                Notes = new[]
                {
                    $"Lowered {uiLowering.ObjectGraph.Count} Unity UI nodes, {uiLowering.ResourceBindings.Count} resource bindings and {uiLowering.LifecyclePrograms.Count} lifecycle programs from verified IL."
                }
            });
        }

        report = new PcCompatRecipeCompileReport
        {
            ModId = manifest.Id,
            RecipeId = RecipeId,
            Compatibility = unsupported.Any(item => item.Severity == "warning")
                ? "partial"
                : "supported",
            Features = features,
            Rules = rules,
            Unsupported = unsupported,
            RequiredCapabilities = rules.Aggregate(
                PcCompatCapability.None,
                (value, rule) => value | rule.RequiredCapabilities),
            UiObjectGraph = uiLowering?.ObjectGraph ?? Array.Empty<PcCompatUiObjectNode>(),
            UiResourceBindings = uiLowering?.ResourceBindings ?? Array.Empty<PcCompatUiResourceBinding>(),
            UiLifecyclePrograms = uiLowering?.LifecyclePrograms ?? Array.Empty<PcCompatUiLifecycleProgram>()
        };
        return true;
    }

    // Every active postfix callback that still owns managed behavior gets a
    // managed-event rule: at hook time the
    // native dispatcher only captures the raw instance/argument slots and enqueues a
    // per-MOD event; the MOD's own rewritten callback is invoked later on UnityMain by
    // the managed callback dispatcher. Signatures come from the same verified domain
    // catalog as the fixed-op rules so both rule kinds share one target record (and
    // therefore one hook). Descriptor-only mappings are already fully consumed by
    // their native fixed op and must not execute the original callback again. Targets
    // outside the catalog are reported, not guessed.
    private static PcCompatUnsupportedItem[] EmitManagedEventRules(
        List<PcCompatCompiledRule> rules,
        PcCompatCallbackTranslationReport callbackTranslation)
    {
        var misses = new List<PcCompatUnsupportedItem>();
        var candidates = callbackTranslation.Items
            .Where(item =>
                item.PatchKind is PcCompatPatchKind.Prefix or PcCompatPatchKind.Postfix &&
                item.ManagedDispatchRequired &&
                item.Status != PcCompatCallbackTranslationStatus.Skipped)
            .GroupBy(
                item => string.Join(
                    "\0",
                    item.TargetType,
                    item.TargetMethod,
                    item.PatchKind,
                    item.CallbackType,
                    item.CallbackMethod),
                StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.CallbackType, StringComparer.Ordinal)
            .ThenBy(item => item.CallbackMethod, StringComparer.Ordinal)
            .ThenBy(item => item.TargetType, StringComparer.Ordinal)
            .ThenBy(item => item.TargetMethod, StringComparer.Ordinal)
            .ToArray();

        var patchId = 0;
        foreach (var item in candidates)
        {
            // Two signature sources, in priority order: the hand-audited catalog, then whatever the
            // translator read back out of runtime IL2CPP metadata for a target the catalog does not
            // cover. Both produce the same strict rule shape - the native resolver cannot tell them
            // apart, and must not be able to.
            string assemblyName;
            string targetNamespace;
            string targetType;
            string targetMethod;
            bool targetIsStatic;
            int genericArity;
            string returnType;
            IReadOnlyList<string> parameterTypes;
            string source;

            if (PcCompatCallbackDomainMappings.TryFindByTarget(
                    item.TargetType,
                    item.TargetMethod,
                    out var mapping))
            {
                assemblyName = mapping.TargetAssemblyName;
                targetNamespace = mapping.TargetNamespace;
                targetType = mapping.TargetType;
                targetMethod = mapping.TargetMethod;
                targetIsStatic = mapping.TargetIsStatic;
                genericArity = mapping.TargetGenericArity;
                returnType = mapping.TargetReturnType;
                parameterTypes = mapping.TargetParameterTypes;
                source = "managed_event";
            }
            else if (item.ResolvedTarget is { } resolved)
            {
                assemblyName = resolved.AssemblyName;
                targetNamespace = resolved.Namespace;
                targetType = resolved.TypeName;
                targetMethod = resolved.MethodName;
                targetIsStatic = resolved.IsStatic;
                // The strict resolver refuses generic targets outright, so a runtime-resolved rule
                // is always arity 0; the translator never produces one otherwise.
                genericArity = 0;
                returnType = resolved.ReturnType;
                parameterTypes = resolved.ParameterTypes;
                source = "managed_event:runtime_resolved";
            }
            else
            {
                misses.Add(new PcCompatUnsupportedItem
                {
                    Id = $"managed_event.{item.CallbackType}.{item.CallbackMethod}@{item.TargetType}.{item.TargetMethod}",
                    Reason = "no verified domain signature for target; managed callback is not dispatched",
                    Severity = "info"
                });
                continue;
            }

            ++patchId;
            var synchronousPrefix = item.PatchKind == PcCompatPatchKind.Prefix;
            rules.Add(new PcCompatCompiledRule
            {
                // The id is parsed by both the native hook runtime (patch id) and the
                // managed dispatcher (callback identity); keep the shape in sync with
                // kManagedEventRuleIdPrefix on both sides.
                Id = $"{(synchronousPrefix ? "managed_prefix" : "managed_event")}:{patchId}:{item.CallbackType}:{item.CallbackMethod}",
                FeatureId = "managed_callback",
                TargetAssemblyName = assemblyName,
                TargetNamespace = targetNamespace,
                TargetType = targetType,
                TargetMethod = targetMethod,
                ParamCount = parameterTypes.Count,
                TargetIsStatic = targetIsStatic,
                TargetGenericArity = genericArity,
                TargetReturnType = returnType,
                TargetParameterTypes = parameterTypes,
                Stage = synchronousPrefix
                    ? PcCompatRuleStage.BeforeOriginal
                    : PcCompatRuleStage.AfterOriginal,
                Op = synchronousPrefix
                    ? PcCompatRuleOp.ManagedSynchronousPrefix
                    : PcCompatRuleOp.ManagedEventCallback,
                RequiredCapabilities = synchronousPrefix
                    ? PcCompatCapability.SkipOriginal
                    : PcCompatCapability.AfterOriginalObserve,
                DefaultEnabled = true,
                Source = synchronousPrefix
                    ? source.Replace("managed_event", "managed_prefix", StringComparison.Ordinal)
                    : source
            });
        }

        if (patchId != 0)
            rules.Sort((left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));

        return misses.ToArray();
    }

    private static void AddPlatformRules(List<PcCompatCompiledRule> rules)
    {
        if (!rules.Any(rule => rule.RequiredCapabilities.HasFlag(PcCompatCapability.UiOverlay)))
            return;

        if (!rules.Any(rule =>
                rule.TargetType == "scrPlayer" &&
                rule.TargetMethod == "HitInputEvent" &&
                rule.Op == PcCompatRuleOp.GameplayAcceptedObserve))
        {
            rules.Add(new PcCompatCompiledRule
            {
                Id = "platform.input.gameplay_accepted",
                FeatureId = "input_adapter",
                TargetType = "scrPlayer",
                TargetMethod = "HitInputEvent",
                ParamCount = 2,
                TargetIsStatic = false,
                TargetReturnType = "System.Boolean",
                TargetParameterTypes = new[] { "System.Boolean", "InputEventState" },
                Stage = PcCompatRuleStage.AfterOriginal,
                Op = PcCompatRuleOp.GameplayAcceptedObserve,
                RequiredCapabilities = PcCompatCapability.AfterOriginalObserve,
                Source = "platform:gameplay-accepted-v1"
            });
        }

        if (!rules.Any(rule =>
                rule.TargetType == "scrController" &&
                rule.TargetMethod == "QuitToMainMenu" &&
                rule.Op == PcCompatRuleOp.OverlayHide))
        {
            rules.Add(new PcCompatCompiledRule
            {
                Id = "platform.overlay.quit_to_main_menu",
                FeatureId = "overlay",
                TargetType = "scrController",
                TargetMethod = "QuitToMainMenu",
                ParamCount = 0,
                TargetIsStatic = false,
                TargetReturnType = "System.Void",
                TargetParameterTypes = Array.Empty<string>(),
                Stage = PcCompatRuleStage.AfterOriginal,
                Op = PcCompatRuleOp.OverlayHide,
                RequiredCapabilities = PcCompatCapability.UiOverlay | PcCompatCapability.AfterOriginalObserve,
                Source = "platform:overlay-lifecycle-v1"
            });
        }

        if (!rules.Any(rule =>
                rule.TargetType == "scnEditor" &&
                rule.TargetMethod == "SwitchToEditMode" &&
                rule.Op == PcCompatRuleOp.OverlayHide))
        {
            rules.Add(new PcCompatCompiledRule
            {
                Id = "platform.overlay.editor_switch_to_edit",
                FeatureId = "overlay",
                TargetType = "scnEditor",
                TargetMethod = "SwitchToEditMode",
                ParamCount = 1,
                TargetIsStatic = false,
                TargetReturnType = "System.Void",
                TargetParameterTypes = new[] { "System.Boolean" },
                Stage = PcCompatRuleStage.AfterOriginal,
                Op = PcCompatRuleOp.OverlayHide,
                RequiredCapabilities = PcCompatCapability.UiOverlay | PcCompatCapability.AfterOriginalObserve,
                Source = "platform:overlay-lifecycle-v2"
            });
        }

        if (!rules.Any(rule =>
                rule.TargetType == "scrController" &&
                rule.TargetMethod == "PlayerControl_Update" &&
                rule.Op == PcCompatRuleOp.OverlayPollTelemetry))
        {
            rules.Add(new PcCompatCompiledRule
            {
                Id = "platform.overlay.player_control_telemetry",
                FeatureId = "status_snapshot",
                TargetType = "scrController",
                TargetMethod = "PlayerControl_Update",
                ParamCount = 0,
                TargetIsStatic = false,
                TargetReturnType = "System.Void",
                TargetParameterTypes = Array.Empty<string>(),
                Stage = PcCompatRuleStage.AfterOriginal,
                Op = PcCompatRuleOp.OverlayPollTelemetry,
                RequiredCapabilities = PcCompatCapability.UiOverlay |
                                       PcCompatCapability.AfterOriginalObserve |
                                       PcCompatCapability.ReadIl2CppField |
                                       PcCompatCapability.CallIl2CppGetter,
                Source = "platform:overlay-telemetry-v1"
            });
        }
        rules.Sort((left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
    }

    private static string RuleShape(PcCompatCompiledRule rule)
        => string.Join(
            "\0",
            NormalizeAssemblyName(rule.TargetAssemblyName),
            rule.TargetNamespace,
            rule.TargetType,
            rule.TargetMethod,
            rule.TargetIsStatic ? "static" : "instance",
            rule.TargetGenericArity.ToString(),
            rule.TargetReturnType,
            string.Join(";", rule.TargetParameterTypes),
            rule.Stage.ToString(),
            rule.Op.ToString(),
            ((ulong)rule.RequiredCapabilities).ToString());

    private static string NormalizeAssemblyName(string value)
    {
        var normalized = value.Trim();
        if (normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];
        return normalized.ToUpperInvariant();
    }

    private static string FeatureDisplayName(string featureId)
        => featureId switch
        {
            "overlay" => "Overlay lifecycle",
            "status_snapshot" => "Accuracy/status snapshot",
            "judgement_snapshot" => "Judgement snapshot",
            "resource_changer" => "Resource changer",
            "managed_callback" => "Managed callback dispatch",
            "input_adapter" => "Gameplay input adapter",
            _ => featureId.Replace('_', ' ')
        };

    private static string FeatureNote(string featureId)
        => featureId switch
        {
            "overlay" => "Verified overlay lifecycle callbacks are executed as native domain rules and rendered through the generic Unity HUD adapter when available.",
            "status_snapshot" => "Reads official Android IL2CPP state only at verified after-original observation points.",
            "judgement_snapshot" => "Records verified official judgement events without running the PC callback body on Android.",
            "resource_changer" => "Uses audited descriptor-only ResourceChanger callbacks and native fixed ops; the PC callback body is not executed on Android.",
            "managed_callback" => "Native hooks capture raw argument slots and the MOD's own postfix callbacks run on UnityMain while managed self-render owns presentation.",
            "input_adapter" => "Publishes successful official gameplay actions without assigning them a physical key or touch lane identity.",
            _ => "Compiled from callback IL that matched the verified fixed-op catalog."
        };
}

public static class PcCompatRecipeCapabilities
{
    public static bool SupportsStandardUnityHud(PcCompatRecipeCompileReport? report)
    {
        if (report == null || !report.RequiredCapabilities.HasFlag(PcCompatCapability.UiOverlay))
            return false;

        var hasShow = report.Rules.Any(rule =>
            rule.Op is PcCompatRuleOp.OverlayShow or PcCompatRuleOp.OverlayShowPractice);
        var hasTelemetry = report.Rules.Any(rule =>
            rule.Op is PcCompatRuleOp.PublishMarginSnapshot or
                       PcCompatRuleOp.OverlayRecordHit or
                       PcCompatRuleOp.OverlayRecordPlayerHit or
                       PcCompatRuleOp.OverlayRecordHitTiming or
                       PcCompatRuleOp.OverlayRecordFloorMove or
                       PcCompatRuleOp.OverlayRecordDeath);
        return hasShow && hasTelemetry;
    }
}

using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Xphorror.PcModCompat;

public static class PcCompatCallbackTranslator
{
    private const int MaxCallbackInstructions = 128;

    public static PcCompatCallbackTranslationReport Translate(
        PcModManifest manifest,
        PcCompatStaticPatchScanReport patchReport)
    {
        var contexts = patchReport.AssembliesScanned
            .Where(File.Exists)
            .Select(path => new AssemblyScanContext(path))
            .ToArray();
        var rules = new List<PcCompatCompiledRule>();
        var items = new List<PcCompatCallbackTranslationItem>();

        try
        {
            foreach (var patch in patchReport.ActivePatches)
            {
                if (patch.Kind == PcCompatPatchKind.ReversePatch)
                {
                    items.Add(Item(
                        patch,
                        PcCompatCallbackTranslationStatus.Skipped,
                        "ReversePatch is handled by the dedicated state bridge."));
                    continue;
                }

                if (!PcCompatCallbackDomainMappings.TryFind(patch, out var mapping))
                {
                    items.Add(TranslateOutsideCatalog(patch, contexts));
                    continue;
                }

                var methods = contexts
                    .SelectMany(context => context.FindMethods(
                        patch.CallbackType,
                        patch.CallbackMethod,
                        patch.CallbackParameterTypeNames))
                    .ToArray();
                if (methods.Length != 1)
                {
                    items.Add(Item(
                        patch,
                        PcCompatCallbackTranslationStatus.Unsupported,
                        methods.Length == 0
                            ? "Callback method body with the scanned parameter signature was not found."
                            : "Callback parameter signature resolved to multiple method bodies."));
                    continue;
                }

                if (!TryTranslateMethod(methods[0], patch, mapping, out var rule, out var reason))
                {
                    items.Add(Item(
                        patch,
                        PcCompatCallbackTranslationStatus.Unsupported,
                        reason));
                    continue;
                }

                rules.Add(rule);
                AddCompanionRules(rule, rules);
                items.Add(Item(
                    patch,
                    PcCompatCallbackTranslationStatus.Translated,
                    mapping.SinglePlayerCoopIndexProjection
                        ? "Callback IL matched a verified fixed-op domain mapping; the recognized coop index loop is projected to player 0."
                        : mapping.DescriptorOnly
                            ? "Callback target matched an audited descriptor-only fixed-op mapping; PC callback IL is not executed on Android."
                            : "Callback IL matched a verified fixed-op domain mapping.",
                    rule.Id,
                    managedDispatchRequired: !mapping.DescriptorOnly));
            }
        }
        finally
        {
            foreach (var context in contexts)
                context.Dispose();
        }

        return new PcCompatCallbackTranslationReport
        {
            ModId = manifest.Id,
            TargetGameRevision = patchReport.TargetGameRevision,
            Rules = rules
                .OrderBy(rule => rule.Id, StringComparer.Ordinal)
                .ToArray(),
            Items = items
                .OrderBy(item => item.CallbackType, StringComparer.Ordinal)
                .ThenBy(item => item.CallbackMethod, StringComparer.Ordinal)
                .ThenBy(item => item.TargetType, StringComparer.Ordinal)
                .ThenBy(item => item.TargetMethod, StringComparer.Ordinal)
                .ToArray()
        };
    }

    // Targets the verified fixed-op catalog does not cover. There is no native domain effect to
    // lower the callback IL into, so the only thing on offer is a managed-event hook: capture the
    // raw slots and run the MOD's own callback on UnityMain. That still needs the target's exact
    // signature, which only the running game can supply - see PcCompatTargetSignatureResolver.
    //
    // Every failure here is an audit entry, never a guess. With no provider registered this method
    // reproduces the pre-existing NotMapped behaviour verbatim.
    private static PcCompatCallbackTranslationItem TranslateOutsideCatalog(
        PcCompatPatchDescriptor patch,
        AssemblyScanContext[] contexts)
    {
        if (patch.Kind is not (PcCompatPatchKind.Prefix or PcCompatPatchKind.Postfix))
        {
            return Item(
                patch,
                PcCompatCallbackTranslationStatus.NotMapped,
                $"No callback domain mapping is available for this target, and {patch.Kind} callbacks " +
                "cannot be served by a managed callback path.");
        }

        if (!PcCompatTargetSignatureResolver.IsProviderRegistered)
        {
            return Item(
                patch,
                PcCompatCallbackTranslationStatus.NotMapped,
                "No callback domain mapping is available for this target.");
        }

        // Same fail-closed callback-body check the catalog path applies: a callback the dispatcher
        // cannot uniquely identify must not get a hook.
        var methods = contexts
            .SelectMany(context => context.FindMethods(
                patch.CallbackType,
                patch.CallbackMethod,
                patch.CallbackParameterTypeNames))
            .ToArray();
        if (methods.Length != 1)
        {
            return Item(
                patch,
                PcCompatCallbackTranslationStatus.Unsupported,
                methods.Length == 0
                    ? "Callback method body with the scanned parameter signature was not found."
                    : "Callback parameter signature resolved to multiple method bodies.");
        }

        var request = new PcCompatTargetSignatureRequest
        {
            TypeName = patch.TargetType,
            MethodName = patch.TargetMethod,
            ArgumentTypeNames = patch.ArgumentTypeNames,
            HasArgumentTypeNames = patch.ArgumentTypeNames.Count != 0
        };

        if (!PcCompatTargetSignatureResolver.TryResolve(request, out var resolved, out var resolveError))
        {
            return Item(
                patch,
                PcCompatCallbackTranslationStatus.NotMapped,
                "No callback domain mapping is available for this target and runtime metadata could " +
                $"not resolve it: {resolveError}");
        }

        var dispatchReason = patch.Kind == PcCompatPatchKind.Prefix
            ? "the MOD's own Prefix runs synchronously on the original hook thread before the original"
            : "the MOD's own Postfix runs on UnityMain through the managed-event path";
        return Item(
            patch,
            PcCompatCallbackTranslationStatus.Translated,
            $"Target resolved from runtime IL2CPP metadata; {dispatchReason}. Signature: {resolved}",
            managedDispatchRequired: true,
            resolvedTarget: resolved);
    }

    private static bool TryTranslateMethod(
        CallbackMethodBody method,
        PcCompatPatchDescriptor patch,
        PcCompatCallbackDomainMapping mapping,
        out PcCompatCompiledRule rule,
        out string reason)
    {
        rule = null!;
        reason = string.Empty;

        if (!mapping.AllowedPatchKinds.Contains(patch.Kind))
        {
            reason = $"The fixed-op translator mapping does not accept {patch.Kind} callbacks for this target.";
            return false;
        }

        if (mapping.DescriptorOnly)
        {
            rule = BuildRule(patch, mapping);
            return true;
        }

        if (method.Body.ExceptionRegions.Length != 0)
        {
            reason = "Callback contains exception regions.";
            return false;
        }

        IReadOnlyList<PcCompatIlInstruction> instructions;
        try
        {
            instructions = PcCompatIlDecoder.Decode(method.Body);
        }
        catch (Exception ex)
        {
            reason = $"IL decode failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }

        if (instructions.Count > MaxCallbackInstructions)
        {
            reason = $"Callback has {instructions.Count} instructions; limit is {MaxCallbackInstructions}.";
            return false;
        }

        var calls = new List<string>();
        var backEdges = new List<PcCompatIlInstruction>();
        foreach (var instruction in instructions)
        {
            if (IsUnsupportedInstruction(instruction.OpCode))
            {
                reason = $"Unsupported instruction {instruction.OpCode.Name} at IL_{instruction.Offset:X4}.";
                return false;
            }

            if (HasBackwardBranch(instruction))
                backEdges.Add(instruction);

            if (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
            {
                var identity = PcCompatMetadataNames.GetMethodIdentity(method.Reader, instruction.MetadataToken);
                if (identity.IsEmpty)
                {
                    reason = $"Method token at IL_{instruction.Offset:X4} could not be resolved.";
                    return false;
                }
                calls.Add(identity.DisplayName);
            }
        }

        if (backEdges.Count != 0 &&
            !TryVerifySinglePlayerCoopIndexProjection(
                method,
                patch,
                mapping,
                instructions,
                calls,
                backEdges,
                out reason))
        {
            return false;
        }

        foreach (var requiredCall in mapping.RequiredCalls)
        {
            if (calls.Count(call => CallMatches(call, requiredCall)) != 1)
            {
                reason = $"Required domain call {requiredCall} was not found exactly once.";
                return false;
            }
        }

        var unknownCalls = calls
            .Where(call => !mapping.RequiredCalls.Any(pattern => CallMatches(call, pattern)) &&
                           !mapping.AllowedSupportCalls.Any(pattern => CallMatches(call, pattern)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (unknownCalls.Length != 0)
        {
            reason = "Unsupported callback calls: " + string.Join(", ", unknownCalls);
            return false;
        }

        rule = BuildRule(patch, mapping);
        return true;
    }

    private static PcCompatCompiledRule BuildRule(
        PcCompatPatchDescriptor patch,
        PcCompatCallbackDomainMapping mapping)
        => new()
        {
            Id = mapping.RuleId,
            FeatureId = mapping.FeatureId,
            TargetAssemblyName = mapping.TargetAssemblyName,
            TargetNamespace = mapping.TargetNamespace,
            TargetType = patch.TargetType,
            TargetMethod = patch.TargetMethod,
            ParamCount = mapping.TargetParameterTypes.Count,
            TargetIsStatic = mapping.TargetIsStatic,
            TargetGenericArity = mapping.TargetGenericArity,
            TargetReturnType = mapping.TargetReturnType,
            TargetParameterTypes = mapping.TargetParameterTypes,
            Stage = mapping.Stage,
            Op = mapping.Op,
            RequiredCapabilities = mapping.Capabilities,
            Source = $"translator:fixed-op-v2:{patch.CallbackType}.{patch.CallbackMethod}"
        };

    private static void AddCompanionRules(
        PcCompatCompiledRule translatedRule,
        ICollection<PcCompatCompiledRule> rules)
    {
        if (translatedRule.Id != "domain.resource.editor_rabbit" ||
            translatedRule.Op != PcCompatRuleOp.ResourceApplyEditorRabbit ||
            translatedRule.TargetType != "scnEditor" ||
            translatedRule.TargetMethod != "OttoUpdate")
        {
            return;
        }

        rules.Add(new PcCompatCompiledRule
        {
            Id = "domain.resource.editor_rabbit_blink",
            FeatureId = translatedRule.FeatureId,
            TargetAssemblyName = translatedRule.TargetAssemblyName,
            TargetNamespace = translatedRule.TargetNamespace,
            TargetType = translatedRule.TargetType,
            TargetMethod = "OttoBlink",
            ParamCount = 0,
            TargetIsStatic = false,
            TargetGenericArity = 0,
            TargetReturnType = "System.Void",
            TargetParameterTypes = Array.Empty<string>(),
            Stage = PcCompatRuleStage.AfterOriginal,
            Op = PcCompatRuleOp.ResourceApplyEditorRabbit,
            RequiredCapabilities = translatedRule.RequiredCapabilities,
            Source = translatedRule.Source + ":companion:otto-blink"
        });
    }

    private static bool TryVerifySinglePlayerCoopIndexProjection(
        CallbackMethodBody method,
        PcCompatPatchDescriptor patch,
        PcCompatCallbackDomainMapping mapping,
        IReadOnlyList<PcCompatIlInstruction> instructions,
        IReadOnlyList<string> calls,
        IReadOnlyList<PcCompatIlInstruction> backEdges,
        out string reason)
    {
        reason = string.Empty;
        if (!mapping.SinglePlayerCoopIndexProjection)
        {
            reason = $"Callback contains a loop/back-edge at IL_{backEdges[0].Offset:X4}.";
            return false;
        }

        if (!patch.CallbackParameterTypeNames.SequenceEqual(new[] { "System.Object" }, StringComparer.Ordinal))
        {
            reason = "Single-player coop projection requires callback signature (System.Object).";
            return false;
        }

        var expectedOpCodes = new[]
        {
            "ldc.i4.0", "stloc.0", "call", "brfalse.s",
            "ldarg.0", "call", "stloc.1", "ldc.i4.0", "stloc.2", "br.s",
            "ldsfld", "ldloc.2", "ldelem.ref", "ldloc.1", "bne.un.s",
            "ldloc.2", "stloc.0", "br.s",
            "ldloc.2", "ldc.i4.1", "add", "stloc.2",
            "ldloc.2", "ldsfld", "blt.s",
            "ldsfld", "ldloc.0", "callvirt", "ret"
        };
        var actualOpCodes = instructions.Select(instruction => instruction.OpCode.Name ?? string.Empty).ToArray();
        if (!actualOpCodes.SequenceEqual(expectedOpCodes, StringComparer.Ordinal))
        {
            reason = "Coop index loop does not match the audited single-player projection opcode shape.";
            return false;
        }

        if (backEdges.Count != 1 ||
            backEdges[0].OpCode != OpCodes.Blt_S ||
            backEdges[0].Operand is not int target ||
            target != instructions[10].Offset)
        {
            reason = "Coop index loop has an unexpected back-edge shape.";
            return false;
        }

        var supportCalls = new[]
        {
            "scrController.get_coopMode",
            "JALib.Tools.Unsafe.AsUnsafe"
        };
        if (calls.Count != mapping.RequiredCalls.Count + supportCalls.Length ||
            mapping.RequiredCalls.Any(pattern => calls.Count(call => CallMatches(call, pattern)) != 1) ||
            supportCalls.Any(expected => calls.Count(call => call == expected) != 1))
        {
            reason = "Coop index loop call set differs from the audited single-player projection.";
            return false;
        }

        var fields = new List<string>();
        foreach (var instruction in instructions.Where(instruction => instruction.OpCode == OpCodes.Ldsfld))
        {
            var field = PcCompatMetadataNames.GetFieldIdentity(method.Reader, instruction.MetadataToken);
            if (field.IsEmpty)
            {
                reason = $"Field token at IL_{instruction.Offset:X4} could not be resolved.";
                return false;
            }
            fields.Add(field.DisplayName);
        }

        if (fields.Count != 3 ||
            fields.Count(field => field.EndsWith(".Instance", StringComparison.Ordinal)) != 1 ||
            !fields.Contains("scrMistakesManager.marginTrackers", StringComparer.Ordinal) ||
            !fields.Contains("scrPlayerManager.playerCount", StringComparer.Ordinal))
        {
            reason = "Coop index loop field set differs from the audited single-player projection.";
            return false;
        }

        return true;
    }

    private static bool IsUnsupportedInstruction(OpCode opCode)
        => opCode == OpCodes.Newobj ||
           opCode == OpCodes.Newarr ||
           opCode == OpCodes.Throw ||
           opCode == OpCodes.Rethrow ||
           opCode == OpCodes.Calli ||
           opCode == OpCodes.Localloc ||
           opCode == OpCodes.Cpblk ||
           opCode == OpCodes.Initblk ||
           opCode == OpCodes.Stfld ||
           opCode == OpCodes.Stsfld;

    private static bool HasBackwardBranch(PcCompatIlInstruction instruction)
    {
        if (instruction.OpCode.FlowControl is not (FlowControl.Branch or FlowControl.Cond_Branch))
            return false;
        if (instruction.Operand is int target)
            return target <= instruction.Offset;
        if (instruction.Operand is int[] targets)
            return targets.Any(target => target <= instruction.Offset);
        return false;
    }

    private static bool CallMatches(string identity, string pattern)
    {
        if (pattern.Contains(".", StringComparison.Ordinal))
            return identity.Equals(pattern, StringComparison.Ordinal);

        var separator = identity.LastIndexOf('.');
        var methodName = separator >= 0 ? identity[(separator + 1)..] : identity;
        return methodName.Equals(pattern, StringComparison.Ordinal);
    }

    private static PcCompatCallbackTranslationItem Item(
        PcCompatPatchDescriptor patch,
        PcCompatCallbackTranslationStatus status,
        string reason,
        string? ruleId = null,
        bool managedDispatchRequired = true,
        PcCompatResolvedTargetSignature? resolvedTarget = null)
        => new()
        {
            TargetType = patch.TargetType,
            TargetMethod = patch.TargetMethod,
            CallbackType = patch.CallbackType,
            CallbackMethod = patch.CallbackMethod,
            CallbackParameterTypeNames = patch.CallbackParameterTypeNames,
            PatchKind = patch.Kind,
            Status = status,
            RuleId = ruleId,
            ManagedDispatchRequired = managedDispatchRequired,
            ResolvedTarget = resolvedTarget,
            Reason = reason
        };

    private sealed class AssemblyScanContext : IDisposable
    {
        private readonly FileStream _stream;
        private readonly PEReader _peReader;
        private readonly Dictionary<string, MethodDefinitionHandle[]> _methods;

        public AssemblyScanContext(string assemblyPath)
        {
            _stream = File.Open(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            _peReader = new PEReader(_stream, PEStreamOptions.LeaveOpen);
            Reader = _peReader.GetMetadataReader();
            _methods = BuildMethodMap(Reader);
        }

        public MetadataReader Reader { get; }

        public IEnumerable<CallbackMethodBody> FindMethods(
            string callbackType,
            string callbackMethod,
            IReadOnlyList<string> callbackParameterTypeNames)
        {
            var key = MakeMethodKey(callbackType, callbackMethod);
            if (!_methods.TryGetValue(key, out var handles))
                yield break;

            foreach (var handle in handles)
            {
                var method = Reader.GetMethodDefinition(handle);
                if (method.RelativeVirtualAddress == 0)
                    continue;
                var parameterTypes = PcCompatMetadataNames.GetMethodParameterTypes(Reader, handle);
                if (!parameterTypes.SequenceEqual(callbackParameterTypeNames, StringComparer.Ordinal))
                    continue;
                yield return new CallbackMethodBody(Reader, _peReader.GetMethodBody(method.RelativeVirtualAddress));
            }
        }

        public void Dispose()
        {
            _peReader.Dispose();
            _stream.Dispose();
        }

        private static Dictionary<string, MethodDefinitionHandle[]> BuildMethodMap(MetadataReader reader)
        {
            var methods = new Dictionary<string, List<MethodDefinitionHandle>>(StringComparer.Ordinal);
            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var typeName = PcCompatMetadataNames.GetTypeFullName(reader, typeHandle);
                var type = reader.GetTypeDefinition(typeHandle);
                foreach (var methodHandle in type.GetMethods())
                {
                    var methodName = reader.GetString(reader.GetMethodDefinition(methodHandle).Name);
                    var key = MakeMethodKey(typeName, methodName);
                    if (!methods.TryGetValue(key, out var list))
                    {
                        list = new List<MethodDefinitionHandle>();
                        methods.Add(key, list);
                    }
                    list.Add(methodHandle);
                }
            }

            return methods.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray(),
                StringComparer.Ordinal);
        }

        private static string MakeMethodKey(string typeName, string methodName)
            => typeName + "\0" + methodName;
    }

    private readonly record struct CallbackMethodBody(MetadataReader Reader, MethodBodyBlock Body);
}

internal sealed class PcCompatCallbackDomainMapping
{
    public required string RuleId { get; init; }
    public string FeatureId { get; init; } = "overlay";
    public string TargetAssemblyName { get; init; } = "Assembly-CSharp";
    public string TargetNamespace { get; init; } = string.Empty;
    public required string TargetType { get; init; }
    public required string TargetMethod { get; init; }
    public required bool TargetIsStatic { get; init; }
    public int TargetGenericArity { get; init; }
    public required string TargetReturnType { get; init; }
    public required IReadOnlyList<string> TargetParameterTypes { get; init; }
    public int ParamCount => TargetParameterTypes.Count;
    public PcCompatRuleOp Op { get; init; }
    public PcCompatCapability Capabilities { get; init; }
    public IReadOnlyList<string> RequiredCalls { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedSupportCalls { get; init; } = Array.Empty<string>();
    public bool SinglePlayerCoopIndexProjection { get; init; }
    public PcCompatRuleStage Stage { get; init; } = PcCompatRuleStage.AfterOriginal;
    public IReadOnlySet<PcCompatPatchKind> AllowedPatchKinds { get; init; } =
        new HashSet<PcCompatPatchKind> { PcCompatPatchKind.Postfix };
    public bool DescriptorOnly { get; init; }
}

internal static class PcCompatCallbackDomainMappings
{
    private const PcCompatCapability OverlayCapabilities =
        PcCompatCapability.UiOverlay | PcCompatCapability.AfterOriginalObserve;
    private const PcCompatCapability ResourceObserveCapabilities =
        PcCompatCapability.ReadState |
        PcCompatCapability.AfterOriginalObserve |
        PcCompatCapability.ReadIl2CppField |
        PcCompatCapability.WriteIl2CppField |
        PcCompatCapability.CallIl2CppGetter |
        PcCompatCapability.CallIl2CppMutator |
        PcCompatCapability.ResourceRedirect;
    private const PcCompatCapability ResourceBeforeCapabilities =
        PcCompatCapability.ReadState |
        PcCompatCapability.ReadIl2CppField |
        PcCompatCapability.WriteIl2CppField |
        PcCompatCapability.CallIl2CppGetter |
        PcCompatCapability.CallIl2CppMutator |
        PcCompatCapability.SkipOriginal;
    private static readonly IReadOnlySet<PcCompatPatchKind> PostfixOnly =
        new HashSet<PcCompatPatchKind> { PcCompatPatchKind.Postfix };
    private static readonly IReadOnlySet<PcCompatPatchKind> PrefixOnly =
        new HashSet<PcCompatPatchKind> { PcCompatPatchKind.Prefix };

    private static readonly PcCompatCallbackDomainMapping[] Mappings =
    {
        Mapping(
            "domain.overlay.game_start",
            "scnGame",
            "Play",
            Signature(false, "System.Boolean", "System.Int32", "System.Boolean"),
            PcCompatRuleOp.OverlayShow,
            new[] { "Show" }),
        Mapping(
            "domain.overlay.practice_start",
            "scrPressToStart",
            "ShowText",
            Signature(false, "System.Void"),
            PcCompatRuleOp.OverlayShowPractice,
            new[] { "Show" },
            new[] { "UnityEngine.Object.op_Implicit", "scrController.get_instance" }),
        Mapping(
            "domain.overlay.state_change",
            "MonsterLove.StateMachine.StateBehaviour",
            "ChangeState",
            Signature(false, "System.Void", "System.Enum"),
            PcCompatRuleOp.OverlayHandleStateChange,
            new[] { "Death", "Clear" }),
        Mapping(
            "domain.overlay.wipe_to_black",
            "scrUIController",
            "WipeToBlack",
            Signature(false, "System.Void", "WipeDirection", "System.Action", "System.Action"),
            PcCompatRuleOp.OverlayHide,
            new[] { "Hide" }),
        Mapping(
            "domain.overlay.editor_reset",
            "scnEditor",
            "ResetScene",
            Signature(false, "System.Void", "System.Boolean"),
            PcCompatRuleOp.OverlayHide,
            new[] { "Hide" }),
        Mapping(
            "domain.overlay.start_loading_scene",
            "scrController",
            "StartLoadingScene",
            Signature(false, "System.Void", "WipeDirection"),
            PcCompatRuleOp.OverlayHide,
            new[] { "Hide" }),
        Mapping(
            "domain.overlay.players",
            "scrMistakesManager",
            "SetPlayerCount",
            Signature(true, "System.Void", "System.Int32"),
            PcCompatRuleOp.OverlayUpdatePlayers,
            new[] { "OnChangePlayers" },
            capabilities: OverlayCapabilities | PcCompatCapability.ReadState),
        Mapping(
            "domain.status.margin_snapshot",
            "scrMarginTracker",
            "CalculatePercentAcc",
            Signature(false, "System.Void"),
            PcCompatRuleOp.PublishMarginSnapshot,
            new[] { "UpdateAccuracy" },
            new[] { "scrController.get_coopMode", "JALib.Tools.Unsafe.AsUnsafe" },
            featureId: "status_snapshot",
            singlePlayerCoopIndexProjection: true,
            capabilities: PcCompatCapability.ReadState |
                          PcCompatCapability.ReadIl2CppField |
                          PcCompatCapability.AfterOriginalObserve),
        Mapping(
            "domain.status.player_hit",
            "scrPlayer",
            "Hit",
            Signature(false, "System.Boolean", "System.Boolean"),
            PcCompatRuleOp.OverlayRecordPlayerHit,
            new[] { "UpdateBpm" },
            featureId: "status_snapshot",
            capabilities: OverlayCapabilities | PcCompatCapability.ReadState),
        Mapping(
            "domain.status.hit_timing",
            "scrMisc",
            "GetHitMargin",
            Signature(
                true,
                "HitMargin",
                "System.Single",
                "System.Single",
                "System.Boolean",
                "System.Single",
                "System.Single",
                "System.Double"),
            PcCompatRuleOp.OverlayRecordHitTiming,
            new[] { "UpdateTiming" },
            featureId: "status_snapshot",
            capabilities: OverlayCapabilities | PcCompatCapability.ReadState),
        Mapping(
            "domain.judgement.margin_hit",
            "scrMarginTracker",
            "AddHit",
            Signature(false, "System.Void", "HitMargin"),
            PcCompatRuleOp.OverlayRecordHit,
            new[] { "UpdateJudgement" },
            new[] { "scrController.get_coopMode", "JALib.Tools.Unsafe.AsUnsafe" },
            featureId: "judgement_snapshot",
            singlePlayerCoopIndexProjection: true,
            capabilities: OverlayCapabilities | PcCompatCapability.ReadState),
        Mapping(
            "domain.judgement.margin_reset",
            "scrMarginTracker",
            "Reset",
            Signature(false, "System.Void"),
            PcCompatRuleOp.OverlayResetJudgement,
            new[] { "UpdateJudgement" },
            new[] { "scrController.get_coopMode", "JALib.Tools.Unsafe.AsUnsafe" },
            featureId: "judgement_snapshot",
            singlePlayerCoopIndexProjection: true,
            capabilities: OverlayCapabilities | PcCompatCapability.ReadState),
        Mapping(
            "domain.judgement.floor_move",
            "scrPlanet",
            "MoveToNextFloor",
            Signature(false, "System.Void", "scrFloor", "System.Single", "HitMargin"),
            PcCompatRuleOp.OverlayRecordFloorMove,
            new[] { "UpdateProgress" },
            featureId: "judgement_snapshot",
            capabilities: OverlayCapabilities | PcCompatCapability.ReadState),
        Mapping(
            "domain.judgement.player_die",
            "scrPlayer",
            "Die",
            Signature(
                false,
                "System.Void",
                "System.Boolean",
                "System.Boolean",
                "System.String",
                "System.Boolean"),
            PcCompatRuleOp.OverlayRecordDeath,
            new[] { "UpdateJudgement" },
            featureId: "judgement_snapshot",
            capabilities: OverlayCapabilities | PcCompatCapability.ReadState),
        Mapping(
            "domain.resource.editor_rabbit",
            "scnEditor",
            "OttoUpdate",
            Signature(false, "System.Void"),
            PcCompatRuleOp.ResourceApplyEditorRabbit,
            Array.Empty<string>(),
            featureId: "resource_changer",
            capabilities: ResourceObserveCapabilities),
        Mapping(
            "domain.resource.floor_start_color",
            "scrFloor",
            "Start",
            Signature(false, "System.Void"),
            PcCompatRuleOp.ResourceApplyFloorColor,
            Array.Empty<string>(),
            featureId: "resource_changer",
            capabilities: ResourceObserveCapabilities),
        Mapping(
            "domain.resource.planet_start_color",
            "scrPlanet",
            "Start",
            Signature(false, "System.Void"),
            PcCompatRuleOp.ResourceApplyPlanetColor,
            Array.Empty<string>(),
            featureId: "resource_changer",
            capabilities: ResourceObserveCapabilities),
        Mapping(
            "domain.resource.planetary_rainbow",
            "PlanetarySystem",
            "RainbowMode",
            Signature(false, "System.Void"),
            PcCompatRuleOp.ResourceSkipPlanetColorOriginal,
            Array.Empty<string>(),
            featureId: "resource_changer",
            stage: PcCompatRuleStage.BeforeOriginal,
            allowedPatchKinds: PrefixOnly,
            capabilities: ResourceBeforeCapabilities),
        Mapping(
            "domain.resource.planetary_enby",
            "PlanetarySystem",
            "EnbyMode",
            Signature(false, "System.Void"),
            PcCompatRuleOp.ResourceSkipPlanetColorOriginal,
            Array.Empty<string>(),
            featureId: "resource_changer",
            stage: PcCompatRuleStage.BeforeOriginal,
            allowedPatchKinds: PrefixOnly,
            capabilities: ResourceBeforeCapabilities),
        Mapping(
            "domain.resource.floor_set_tile_color",
            "scrFloor",
            "SetTileColor",
            Signature(false, "System.Void", "UnityEngine.Color"),
            PcCompatRuleOp.ResourceSkipTileColorOriginal,
            Array.Empty<string>(),
            featureId: "resource_changer",
            stage: PcCompatRuleStage.BeforeOriginal,
            allowedPatchKinds: PrefixOnly,
            capabilities: ResourceBeforeCapabilities),
        Mapping(
            "domain.resource.planet_renderer_load_color",
            "PlanetRenderer",
            "LoadPlanetColor",
            Signature(false, "System.Void", "System.Boolean"),
            PcCompatRuleOp.ResourceSkipPlanetColorOriginal,
            Array.Empty<string>(),
            featureId: "resource_changer",
            stage: PcCompatRuleStage.BeforeOriginal,
            allowedPatchKinds: PrefixOnly,
            capabilities: ResourceBeforeCapabilities),
        Mapping(
            "domain.resource.planet_renderer_set_rainbow",
            "PlanetRenderer",
            "SetRainbow",
            Signature(false, "System.Void", "System.Boolean"),
            PcCompatRuleOp.ResourceSkipPlanetColorOriginal,
            Array.Empty<string>(),
            featureId: "resource_changer",
            stage: PcCompatRuleStage.BeforeOriginal,
            allowedPatchKinds: PrefixOnly,
            capabilities: ResourceBeforeCapabilities),
        Mapping(
            "domain.resource.planet_renderer_set_color",
            "PlanetRenderer",
            "SetColor",
            Signature(false, "System.Void", "PlanetColor", "System.Boolean"),
            PcCompatRuleOp.ResourceSkipPlanetColorOriginal,
            Array.Empty<string>(),
            featureId: "resource_changer",
            stage: PcCompatRuleStage.BeforeOriginal,
            allowedPatchKinds: PrefixOnly,
            capabilities: ResourceBeforeCapabilities),
        Mapping(
            "domain.resource.planet_renderer_set_planet_color",
            "PlanetRenderer",
            "SetPlanetColor",
            Signature(false, "System.Void", "UnityEngine.Color"),
            PcCompatRuleOp.ResourceOverridePlanetColorArg,
            Array.Empty<string>(),
            featureId: "resource_changer",
            stage: PcCompatRuleStage.BeforeOriginal,
            allowedPatchKinds: PrefixOnly,
            capabilities: ResourceBeforeCapabilities),
        Mapping(
            "domain.resource.planet_renderer_set_core_color",
            "PlanetRenderer",
            "SetCoreColor",
            Signature(false, "System.Void", "UnityEngine.Color"),
            PcCompatRuleOp.ResourceOverridePlanetColorArg,
            Array.Empty<string>(),
            featureId: "resource_changer",
            stage: PcCompatRuleStage.BeforeOriginal,
            allowedPatchKinds: PrefixOnly,
            capabilities: ResourceBeforeCapabilities),
        Mapping(
            "domain.resource.planet_renderer_set_tail_color",
            "PlanetRenderer",
            "SetTailColor",
            Signature(false, "System.Void", "UnityEngine.Color"),
            PcCompatRuleOp.ResourceOverridePlanetColorArg,
            Array.Empty<string>(),
            featureId: "resource_changer",
            stage: PcCompatRuleStage.BeforeOriginal,
            allowedPatchKinds: PrefixOnly,
            capabilities: ResourceBeforeCapabilities),
        Mapping(
            "domain.resource.planet_renderer_set_ring_color",
            "PlanetRenderer",
            "SetRingColor",
            Signature(false, "System.Void", "UnityEngine.Color"),
            PcCompatRuleOp.ResourceOverridePlanetColorArg,
            Array.Empty<string>(),
            featureId: "resource_changer",
            stage: PcCompatRuleStage.BeforeOriginal,
            allowedPatchKinds: PrefixOnly,
            capabilities: ResourceBeforeCapabilities),
        Mapping(
            "domain.resource.planet_renderer_set_face_color",
            "PlanetRenderer",
            "SetFaceColor",
            Signature(false, "System.Void", "UnityEngine.Color"),
            PcCompatRuleOp.ResourceOverridePlanetColorArg,
            Array.Empty<string>(),
            featureId: "resource_changer",
            stage: PcCompatRuleStage.BeforeOriginal,
            allowedPatchKinds: PrefixOnly,
            capabilities: ResourceBeforeCapabilities),
        Mapping(
            "domain.resource.logo_awake",
            "scrLogoText",
            "Awake",
            Signature(false, "System.Void"),
            PcCompatRuleOp.ResourceApplyLogoText,
            Array.Empty<string>(),
            featureId: "resource_changer",
            capabilities: ResourceObserveCapabilities),
        Mapping(
            "domain.resource.logo_update_colors",
            "scrLogoText",
            "UpdateColors",
            Signature(false, "System.Void"),
            PcCompatRuleOp.ResourceSkipPlanetColorOriginal,
            Array.Empty<string>(),
            featureId: "resource_changer",
            stage: PcCompatRuleStage.BeforeOriginal,
            allowedPatchKinds: PrefixOnly,
            capabilities: ResourceBeforeCapabilities),
        Mapping(
            "domain.resource.logo_late_update",
            "scrLogoText",
            "LateUpdate",
            Signature(false, "System.Void"),
            PcCompatRuleOp.ResourceSkipPlanetColorOriginal,
            Array.Empty<string>(),
            featureId: "resource_changer",
            stage: PcCompatRuleStage.BeforeOriginal,
            allowedPatchKinds: PrefixOnly,
            capabilities: ResourceBeforeCapabilities)
    };

    public static bool TryFind(PcCompatPatchDescriptor patch, out PcCompatCallbackDomainMapping mapping)
    {
        mapping = Mappings.FirstOrDefault(candidate =>
            candidate.TargetType == patch.TargetType &&
            candidate.TargetMethod == patch.TargetMethod &&
            candidate.AllowedPatchKinds.Contains(patch.Kind) &&
            (patch.ArgumentTypeNames.Count == 0 ||
             patch.ArgumentTypeNames.Count == candidate.ParamCount))!;
        return mapping != null;
    }

    // Signature-only lookup used by the managed-event rule emitter. Unlike TryFind it
    // ignores patch kind/argument hints, but refuses to guess when the same target
    // name maps to multiple distinct signatures (future overload catalog entries).
    public static bool TryFindByTarget(
        string targetType,
        string targetMethod,
        out PcCompatCallbackDomainMapping mapping)
    {
        mapping = null!;
        foreach (var candidate in Mappings)
        {
            if (candidate.TargetType != targetType || candidate.TargetMethod != targetMethod)
                continue;
            if (mapping == null)
            {
                mapping = candidate;
                continue;
            }
            if (candidate.TargetReturnType != mapping.TargetReturnType ||
                candidate.TargetIsStatic != mapping.TargetIsStatic ||
                !candidate.TargetParameterTypes.SequenceEqual(mapping.TargetParameterTypes))
            {
                mapping = null!;
                return false;
            }
        }
        return mapping != null;
    }

    private static PcCompatCallbackDomainMapping Mapping(
        string ruleId,
        string targetType,
        string targetMethod,
        PcCompatTargetSignature signature,
        PcCompatRuleOp op,
        IReadOnlyList<string> requiredCalls,
        IReadOnlyList<string>? allowedCalls = null,
        string featureId = "overlay",
        bool singlePlayerCoopIndexProjection = false,
        PcCompatCapability capabilities = OverlayCapabilities,
        PcCompatRuleStage stage = PcCompatRuleStage.AfterOriginal,
        IReadOnlySet<PcCompatPatchKind>? allowedPatchKinds = null,
        bool descriptorOnly = false)
        => new()
        {
            RuleId = ruleId,
            FeatureId = featureId,
            TargetNamespace = GetTargetNamespace(targetType),
            TargetType = targetType,
            TargetMethod = targetMethod,
            TargetIsStatic = signature.IsStatic,
            TargetReturnType = signature.ReturnType,
            TargetParameterTypes = signature.ParameterTypes,
            Op = op,
            Capabilities = capabilities,
            RequiredCalls = requiredCalls,
            AllowedSupportCalls = allowedCalls ?? Array.Empty<string>(),
            SinglePlayerCoopIndexProjection = singlePlayerCoopIndexProjection,
            Stage = stage,
            AllowedPatchKinds = allowedPatchKinds ?? PostfixOnly,
            DescriptorOnly = descriptorOnly || featureId == "resource_changer"
        };

    private static PcCompatTargetSignature Signature(
        bool isStatic,
        string returnType,
        params string[] parameterTypes)
        => new(isStatic, returnType, parameterTypes);

    private static string GetTargetNamespace(string typeName)
    {
        var separator = typeName.LastIndexOf('.');
        return separator < 0 ? string.Empty : typeName[..separator];
    }

    private sealed record PcCompatTargetSignature(
        bool IsStatic,
        string ReturnType,
        IReadOnlyList<string> ParameterTypes);
}

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;

namespace Xphorror.PcModCompat;

/// <summary>
/// Metadata-only mirror of Harmony's attribute aggregation, so a Harmony patch class produces the
/// same <see cref="PcCompatPatchDescriptor"/> records a JAPatch attribute does and enters the
/// existing descriptor -> callback translation -> native rule path.
/// </summary>
/// <remarks>
/// <para>Three upstream pieces are reproduced, in the order Harmony runs them:</para>
/// <list type="number">
/// <item>the <c>PatchClassProcessor</c> constructor, which merges every class-level Harmony
/// attribute into one container info and then merges that container into each patch method;</item>
/// <item><c>AttributePatch.Create</c>, which decides whether a method is a prefix/postfix/... and
/// which attributes are allowed to contribute to its info;</item>
/// <item><c>PatchTools.GetOriginalMethod</c>, which turns <c>MethodType</c> plus a method name into
/// the actual member name to hook.</item>
/// </list>
/// <para>This type lives in StArray.ModManager, which deliberately does not reference 0Harmony, so
/// every Harmony enum is mirrored as a numeric table here rather than imported. The tables are
/// pinned to explicit numeric values because that is what lands in the attribute blob.</para>
/// <para>Everything whose target cannot be named from metadata alone - bulk patching, a runtime
/// <c>TargetMethod</c>, indexers, enumerator/async state machines - records an issue and produces no
/// descriptor. Getting a wrong target into the native rule set is far worse than reporting a gap.</para>
/// </remarks>
internal static class PcCompatHarmonyAttributeAggregator
{
    public const string HarmonySource = "harmony_attribute";

    private const string HarmonyNamespace = "HarmonyLib.";
    private const string HarmonyAttributePrefix = "HarmonyLib.Harmony";

    // MethodType from Harmony/Public/Attributes.cs. Values are explicit upstream and are what the
    // attribute blob carries, so they are pinned here instead of relying on declaration order.
    private const int MethodTypeNormal = 0;
    private const int MethodTypeGetter = 1;
    private const int MethodTypeSetter = 2;
    private const int MethodTypeConstructor = 3;
    private const int MethodTypeStaticConstructor = 4;
    private const int MethodTypeEnumerator = 5;
    private const int MethodTypeAsync = 6;
    private const int MethodTypeFinalizer = 7;
    private const int MethodTypeEventAdd = 8;
    private const int MethodTypeEventRemove = 9;
    private const int MethodTypeFirstOperator = 10;
    private const int MethodTypeLastOperator = 36;

    // ArgumentType from Harmony/Public/Attributes.cs.
    private const int ArgumentTypeRef = 1;
    private const int ArgumentTypeOut = 2;
    private const int ArgumentTypePointer = 3;

    // MethodDispatchType.Call; VirtualCall is 0.
    private const int MethodDispatchTypeCall = 1;

    /// <summary>
    /// Operator MethodType values map to <c>"op_" + name.Replace("Operator", "")</c> upstream. The
    /// names are spelled out because the enum itself is not referenced here.
    /// </summary>
    private static readonly Dictionary<int, string> OperatorMethodNames = new()
    {
        [10] = "op_Implicit",
        [11] = "op_Explicit",
        [12] = "op_UnaryPlus",
        [13] = "op_UnaryNegation",
        [14] = "op_LogicalNot",
        [15] = "op_OnesComplement",
        [16] = "op_Increment",
        [17] = "op_Decrement",
        [18] = "op_True",
        [19] = "op_False",
        [20] = "op_Addition",
        [21] = "op_Subtraction",
        [22] = "op_Multiply",
        [23] = "op_Division",
        [24] = "op_Modulus",
        [25] = "op_BitwiseAnd",
        [26] = "op_BitwiseOr",
        [27] = "op_ExclusiveOr",
        [28] = "op_LeftShift",
        [29] = "op_RightShift",
        [30] = "op_Equality",
        [31] = "op_Inequality",
        [32] = "op_GreaterThan",
        [33] = "op_LessThan",
        [34] = "op_GreaterThanOrEqual",
        [35] = "op_LessThanOrEqual",
        [36] = "op_Comma"
    };

    /// <summary>
    /// The patch kinds <c>AttributePatch.GetPatchType</c> probes, in its exact order. The first hit
    /// wins, so a method named <c>Prefix</c> carrying <c>[HarmonyPostfix]</c> is a prefix upstream.
    /// </summary>
    private static readonly string[] PatchTypeProbeOrder =
    [
        "Prefix",
        "Postfix",
        "Transpiler",
        "Finalizer",
        "ReversePatch",
        "InnerPrefix",
        "InnerPostfix"
    ];

    /// <summary>
    /// Attributes whose direct base type is <c>HarmonyLib.HarmonyAttribute</c>. Only these may
    /// contribute to a patch method's info, which is why <c>HarmonyDelegate</c> - whose base is
    /// <c>HarmonyPatch</c> - is absent even though it does carry an <c>info</c> field.
    /// </summary>
    private static readonly HashSet<string> DirectHarmonyAttributeSubclasses = new(StringComparer.Ordinal)
    {
        "HarmonyLib.HarmonyPatch",
        "HarmonyLib.HarmonyPatchCategory",
        "HarmonyLib.HarmonyReversePatch",
        "HarmonyLib.HarmonyPatchAll",
        "HarmonyLib.HarmonyPriority",
        "HarmonyLib.HarmonyBefore",
        "HarmonyLib.HarmonyAfter",
        "HarmonyLib.HarmonyDebug"
    };

    public static void Scan(
        MetadataReader reader,
        string assemblyPath,
        string modId,
        List<PcCompatPatchDescriptor> patches,
        List<PcCompatStaticPatchScanIssue> issues)
    {
        foreach (var typeHandle in reader.TypeDefinitions)
            ScanType(reader, typeHandle, assemblyPath, modId, patches, issues);
    }

    private static void ScanType(
        MetadataReader reader,
        TypeDefinitionHandle typeHandle,
        string assemblyPath,
        string modId,
        List<PcCompatPatchDescriptor> patches,
        List<PcCompatStaticPatchScanIssue> issues)
    {
        var type = reader.GetTypeDefinition(typeHandle);

        // GetPatchType() treats a method *named* Prefix/Postfix/... as a patch method even with no
        // attribute at all, but upstream only ever reaches that code for a class HasHarmonyAttribute()
        // already accepted. Without this gate, an ordinary helper named Prefix - which is exactly how
        // JAPatch callbacks are often named - would look like a Harmony patch.
        if (!MentionsHarmony(reader, type))
            return;

        var callbackType = PcCompatMetadataNames.GetTypeFullName(reader, typeHandle);
        var classAttributes = ReadHarmonyAttributes(reader, type.GetCustomAttributes(), assemblyPath, callbackType, null, issues);
        var patchMethods = CollectPatchMethods(reader, type, assemblyPath, callbackType, issues);

        if (patchMethods.Count == 0)
        {
            // A class-level [HarmonyPatch] with no patch methods is legal and produces nothing
            // upstream too, so it is not worth an issue.
            return;
        }

        var annotatedBase = FindHarmonyAnnotatedBase(reader, type);
        if (annotatedBase is not null)
        {
            // GetFromType calls GetCustomAttributes(inherit: true) and every Harmony attribute
            // defaults to Inherited = true, so the base class contributes to the container info too.
            // Whether the base or the derived value wins depends on the order the runtime returns the
            // attributes in, which is not part of the reflection contract - so the target cannot be
            // named with confidence here.
            issues.Add(Issue(
                "HarmonyInheritedClassAttributeUnsupported",
                $"{callbackType} inherits class-level Harmony attributes from {annotatedBase}; " +
                "the merge order between inherited and declared attributes is runtime-defined, " +
                "so no target is derived.",
                assemblyPath,
                callbackType,
                null));
            return;
        }

        if (!ContributesContainerInfo(reader, type.GetCustomAttributes()))
        {
            // HasHarmonyAttribute() only looks at class-level attributes, so PatchAll() walks past a
            // class that annotates only its methods. The patch methods are real but never applied,
            // which is exactly the kind of silent no-op worth surfacing.
            issues.Add(Issue(
                "HarmonyPatchClassNotDiscoverable",
                $"{callbackType} annotates patch methods but carries no class-level Harmony attribute, " +
                "so Harmony.PatchAll skips it; no patch is applied.",
                assemblyPath,
                callbackType,
                null));
            return;
        }

        if (classAttributes.Count == 0)
        {
            // Discoverable, but every contributing class-level attribute was a MOD-defined subclass
            // whose constructor body assigns info in IL. HarmonyDerivedAttributeUnsupported has
            // already been recorded for each of them, so there is nothing to add here.
            return;
        }

        // PatchClassProcessor: merge every class-level attribute, then default methodType to Normal.
        var containerInfo = MergeAll(classAttributes);
        containerInfo.MethodType ??= MethodTypeNormal;

        // The merged info per patch method, computed once. Upstream builds these in the
        // PatchClassProcessor constructor, which is also where a bad patch method aborts the whole
        // class - so validation happens here, before any descriptor is emitted.
        var merged = patchMethods
            .Select(patchMethod => MergeDetail(containerInfo, patchMethod.Info))
            .ToArray();

        for (var i = 0; i < patchMethods.Count; i++)
        {
            var patchMethod = patchMethods[i];
            var isReversePatch = string.Equals(patchMethod.PatchType, "ReversePatch", StringComparison.Ordinal);

            if (!isReversePatch && !patchMethod.IsStatic)
            {
                // Upstream throws ArgumentException from AttributePatch.Create, which happens while
                // constructing the processor and therefore cancels every patch in the class.
                issues.Add(Issue(
                    "HarmonyPatchMethodNotStatic",
                    $"Patch method {callbackType}.{patchMethod.Name} is not static; Harmony rejects the whole patch class.",
                    assemblyPath,
                    callbackType,
                    patchMethod.Name));
                return;
            }

            if (merged[i].VariationMismatch is { } mismatch)
            {
                issues.Add(Issue(
                    "HarmonyArgumentVariationsMismatch",
                    $"{callbackType}.{patchMethod.Name}: {mismatch}",
                    assemblyPath,
                    callbackType,
                    patchMethod.Name));
                return;
            }
        }

        if (HasAttribute(reader, type.GetCustomAttributes(), HarmonyNamespace + "HarmonyPatchAll"))
        {
            // GetBulkMethods() expands to every declared ctor, method and property accessor of the
            // declaring type. Enumerating those needs the target assembly's metadata, which is the
            // game, not the MOD - and the resulting fan-out is far too broad to install blind.
            issues.Add(Issue(
                "HarmonyPatchAllUnsupported",
                $"{callbackType} uses [HarmonyPatchAll]; bulk expansion over " +
                $"{containerInfo.DeclaringTypeName ?? "an unspecified type"} cannot be resolved from MOD metadata.",
                assemblyPath,
                callbackType,
                null));
            return;
        }

        var auxiliary = FindAuxiliaryMethod(reader, type, "HarmonyTargetMethods")
                        ?? FindAuxiliaryMethod(reader, type, "HarmonyTargetMethod");
        if (auxiliary is not null)
        {
            issues.Add(Issue(
                "HarmonyDynamicTargetMethodUnsupported",
                $"{callbackType} resolves its target through {auxiliary} at runtime; " +
                "static aggregation cannot evaluate it.",
                assemblyPath,
                callbackType,
                auxiliary));
            return;
        }

        var prepareMethod = FindAuxiliaryMethod(reader, type, "HarmonyPrepare");
        if (prepareMethod is not null)
        {
            // Prepare is a runtime gate: returning false cancels the whole class (or one target).
            // The descriptors are still emitted because discovery must stay faithful; the gate is
            // recorded so the install decision can honour it later.
            issues.Add(Issue(
                "HarmonyPrepareGateNotEvaluated",
                $"{callbackType} declares {prepareMethod}, which decides at runtime whether the patch " +
                "applies; static aggregation assumes it returns true.",
                assemblyPath,
                callbackType,
                prepareMethod));
        }

        // ReversePatch runs before the normal path upstream and carries the previous resolved target
        // forward when a stand-in has no target of its own.
        string? carriedTargetType = null;
        string? carriedTargetMethod = null;

        for (var i = 0; i < patchMethods.Count; i++)
        {
            var patchMethod = patchMethods[i];
            var info = merged[i];
            var isReversePatch = string.Equals(patchMethod.PatchType, "ReversePatch", StringComparison.Ordinal);

            var kind = MapPatchKind(patchMethod.PatchType);
            if (kind == PcCompatPatchKind.Unknown)
            {
                // Inner prefixes/postfixes patch a call site inside the target body. The annotations
                // are unreleased upstream and there is no fixed-op equivalent, so they are recorded
                // as Unknown, which the translator's AllowedPatchKinds check refuses.
                issues.Add(Issue(
                    "HarmonyInnerPatchUnsupported",
                    $"{callbackType}.{patchMethod.Name} is a {patchMethod.PatchType}; " +
                    "call-site patching has no PcCompat equivalent.",
                    assemblyPath,
                    callbackType,
                    patchMethod.Name));
            }

            if (!TryResolveTarget(info, out var targetType, out var targetMethod, out var failureCode, out var failureDetail))
            {
                if (isReversePatch && carriedTargetMethod is not null)
                {
                    // Mirrors ReversePatch(ref lastOriginal): an unannotated stand-in reuses the
                    // previous stand-in's target instead of failing.
                    targetType = carriedTargetType;
                    targetMethod = carriedTargetMethod;
                }
                else
                {
                    issues.Add(Issue(
                        failureCode,
                        $"{callbackType}.{patchMethod.Name}: {failureDetail}",
                        assemblyPath,
                        callbackType,
                        patchMethod.Name));
                    continue;
                }
            }

            if (isReversePatch)
            {
                carriedTargetType = targetType;
                carriedTargetMethod = targetMethod;
            }

            patches.Add(new PcCompatPatchDescriptor
            {
                ModId = modId,
                TargetType = targetType!,
                TargetMethod = targetMethod!,
                Kind = kind,
                CallbackType = callbackType,
                CallbackMethod = patchMethod.Name,
                CallbackAssemblyPath = assemblyPath,
                CallbackParameterTypeNames = patchMethod.ParameterTypeNames,
                Priority = info.Priority,
                Before = info.Before ?? Array.Empty<string>(),
                After = info.After ?? Array.Empty<string>(),
                NeedInstance = patchMethod.DeclaresInstanceParameter,
                ArgumentTypeNames = info.ArgumentTypes ?? Array.Empty<string>(),
                // Harmony does not wrap patch bodies in a try/catch; an exception from a prefix
                // propagates into the target method.
                TryingCatch = false,
                Source = HarmonySource,
                Status = PcCompatPatchStatus.RegisteredOnly,
                Reason = BuildReason(info, kind, prepareMethod)
            });
        }
    }

    // --- patch method discovery -------------------------------------------------------------

    private sealed class HarmonyPatchMethod
    {
        public required string Name { get; init; }
        public required string PatchType { get; init; }
        public required bool IsStatic { get; init; }
        public required bool DeclaresInstanceParameter { get; init; }
        public required IReadOnlyList<string> ParameterTypeNames { get; init; }
        public required HarmonyMethodInfo Info { get; init; }
    }

    private static List<HarmonyPatchMethod> CollectPatchMethods(
        MetadataReader reader,
        TypeDefinition type,
        string assemblyPath,
        string callbackType,
        List<PcCompatStaticPatchScanIssue> issues)
    {
        var result = new List<HarmonyPatchMethod>();

        foreach (var methodHandle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(methodHandle);
            var methodName = reader.GetString(method.Name);

            var attributeNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attributeHandle in method.GetCustomAttributes())
            {
                var name = GetAttributeTypeFullName(reader, reader.GetCustomAttribute(attributeHandle));
                // GetPatchType filters on FullName.StartsWith("Harmony"), which matches the
                // HarmonyLib namespace prefix as well as any type literally named Harmony*.
                if (name.StartsWith("Harmony", StringComparison.Ordinal))
                    attributeNames.Add(name);
            }

            var patchType = ResolvePatchType(methodName, attributeNames);
            if (patchType is null)
                continue;

            var infos = ReadHarmonyAttributes(
                reader,
                method.GetCustomAttributes(),
                assemblyPath,
                callbackType,
                methodName,
                issues,
                directSubclassesOnly: true);

            result.Add(new HarmonyPatchMethod
            {
                Name = methodName,
                PatchType = patchType,
                IsStatic = (method.Attributes & MethodAttributes.Static) != 0,
                DeclaresInstanceParameter = DeclaresInstanceParameter(reader, method),
                ParameterTypeNames = PcCompatMetadataNames.GetMethodParameterTypes(reader, methodHandle),
                Info = MergeAll(infos)
            });
        }

        return result;
    }

    private static string? ResolvePatchType(string methodName, HashSet<string> harmonyAttributeNames)
    {
        foreach (var candidate in PatchTypeProbeOrder)
        {
            if (string.Equals(candidate, methodName, StringComparison.Ordinal) ||
                harmonyAttributeNames.Contains(HarmonyAttributePrefix + candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static PcCompatPatchKind MapPatchKind(string patchType) => patchType switch
    {
        "Prefix" => PcCompatPatchKind.Prefix,
        "Postfix" => PcCompatPatchKind.Postfix,
        "Transpiler" => PcCompatPatchKind.Transpiler,
        "Finalizer" => PcCompatPatchKind.Finalizer,
        "ReversePatch" => PcCompatPatchKind.ReversePatch,
        _ => PcCompatPatchKind.Unknown
    };

    private static bool DeclaresInstanceParameter(MetadataReader reader, MethodDefinition method)
    {
        foreach (var parameterHandle in method.GetParameters())
        {
            var parameter = reader.GetParameter(parameterHandle);
            if (parameter.SequenceNumber == 0)
                continue;
            if (string.Equals(reader.GetString(parameter.Name), "__instance", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string? FindAuxiliaryMethod(MetadataReader reader, TypeDefinition type, string attributeSimpleName)
    {
        // PatchTools.GetPatchMethod looks for the attribute first and falls back to the bare name,
        // which is the attribute name minus the "HarmonyLib.Harmony" prefix. Neither lookup passes
        // DeclaredOnly, so an auxiliary method inherited from a base class counts - and an attributed
        // method anywhere in the chain outranks a merely well-named one.
        var attributeFullName = HarmonyAttributePrefix + attributeSimpleName["Harmony".Length..];
        var bareName = attributeSimpleName["Harmony".Length..];
        string? byName = null;

        var current = type;
        for (var depth = 0; depth < 8; depth++)
        {
            foreach (var methodHandle in current.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                var name = reader.GetString(method.Name);

                if (HasAttribute(reader, method.GetCustomAttributes(), attributeFullName))
                    return name;

                if (string.Equals(name, bareName, StringComparison.Ordinal))
                    byName ??= name;
            }

            // Anything outside this assembly is a TypeReference, which also ends the walk.
            if (!TryGetBaseTypeDefinition(reader, current, out _, out var baseType))
                break;
            current = baseType;
        }

        return byName;
    }

    /// <summary>
    /// Resolves the base type when it is defined in this same assembly. A missing extends decodes as a
    /// <em>nil TypeDefinitionHandle</em> rather than a nil handle of some other kind, so the kind check
    /// alone lets interfaces and System.Object through and the row lookup then reads out of bounds.
    /// </summary>
    private static bool TryGetBaseTypeDefinition(
        MetadataReader reader,
        TypeDefinition type,
        out TypeDefinitionHandle baseHandle,
        out TypeDefinition baseType)
    {
        var handle = type.BaseType;
        if (handle.IsNil || handle.Kind != HandleKind.TypeDefinition)
        {
            baseHandle = default;
            baseType = default;
            return false;
        }

        baseHandle = (TypeDefinitionHandle)handle;
        baseType = reader.GetTypeDefinition(baseHandle);
        return true;
    }

    private static bool HasAttribute(MetadataReader reader, CustomAttributeHandleCollection attributes, string fullName)
    {
        foreach (var handle in attributes)
        {
            if (string.Equals(GetAttributeTypeFullName(reader, reader.GetCustomAttribute(handle)), fullName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when the type or any of its methods carries an attribute Harmony would recognise: either
    /// one from the HarmonyLib namespace, or a MOD-defined subclass of a Harmony attribute.
    /// </summary>
    private static bool MentionsHarmony(MetadataReader reader, TypeDefinition type)
    {
        if (MentionsHarmony(reader, type.GetCustomAttributes()))
            return true;

        foreach (var methodHandle in type.GetMethods())
        {
            if (MentionsHarmony(reader, reader.GetMethodDefinition(methodHandle).GetCustomAttributes()))
                return true;
        }

        // A subclass of an annotated patch class needs no annotation of its own to be picked up by
        // PatchAll, so it stays relevant even when nothing local mentions Harmony.
        return FindHarmonyAnnotatedBase(reader, type) is not null;
    }

    /// <summary>
    /// Name of the nearest base type in this assembly whose class-level attributes would contribute
    /// to the container info, or null when the base chain carries nothing Harmony cares about.
    /// </summary>
    private static string? FindHarmonyAnnotatedBase(MetadataReader reader, TypeDefinition type)
    {
        var current = type;
        for (var depth = 0; depth < 8; depth++)
        {
            if (!TryGetBaseTypeDefinition(reader, current, out var baseHandle, out var baseType))
                break;

            if (ContributesContainerInfo(reader, baseType.GetCustomAttributes()))
                return PcCompatMetadataNames.GetTypeFullName(reader, baseHandle);

            current = baseType;
        }

        return null;
    }

    /// <summary>
    /// True when at least one of the attributes carries an <c>info</c> field, which is what
    /// <c>GetHarmonyMethodInfo</c> requires before an attribute may feed the container info. The
    /// auxiliary attributes derive straight from <c>Attribute</c> and therefore never do.
    /// </summary>
    private static bool ContributesContainerInfo(MetadataReader reader, CustomAttributeHandleCollection attributes)
    {
        foreach (var handle in attributes)
        {
            var attribute = reader.GetCustomAttribute(handle);
            var name = GetAttributeTypeFullName(reader, attribute);
            if (name.StartsWith(HarmonyNamespace, StringComparison.Ordinal))
            {
                if (!IsAuxiliaryAttribute(name))
                    return true;
                continue;
            }

            if (DerivesFromHarmonyAttribute(reader, attribute))
                return true;
        }

        return false;
    }

    private static bool MentionsHarmony(MetadataReader reader, CustomAttributeHandleCollection attributes)
    {
        foreach (var handle in attributes)
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (GetAttributeTypeFullName(reader, attribute).StartsWith(HarmonyNamespace, StringComparison.Ordinal))
                return true;
            if (DerivesFromHarmonyAttribute(reader, attribute))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Walks the base chain of an attribute defined in the scanned assembly looking for a Harmony
    /// attribute. Attributes defined elsewhere cannot be followed from this assembly's metadata, so
    /// they read as unrelated - the same blind spot the rest of the scanner has.
    /// </summary>
    private static bool DerivesFromHarmonyAttribute(MetadataReader reader, CustomAttribute attribute)
    {
        var handle = GetAttributeTypeDefinition(reader, attribute);
        for (var depth = 0; depth < 8 && !handle.IsNil; depth++)
        {
            var baseType = reader.GetTypeDefinition(handle).BaseType;
            if (baseType.IsNil)
                return false;

            if (PcCompatMetadataNames.GetEntityTypeFullName(reader, baseType)
                .StartsWith(HarmonyAttributePrefix, StringComparison.Ordinal))
            {
                return true;
            }

            handle = baseType.Kind == HandleKind.TypeDefinition ? (TypeDefinitionHandle)baseType : default;
        }

        return false;
    }

    private static TypeDefinitionHandle GetAttributeTypeDefinition(MetadataReader reader, CustomAttribute attribute)
    {
        var constructor = attribute.Constructor;
        if (constructor.Kind == HandleKind.MethodDefinition)
            return reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType();

        if (constructor.Kind == HandleKind.MemberReference)
        {
            var parent = reader.GetMemberReference((MemberReferenceHandle)constructor).Parent;
            if (parent.Kind == HandleKind.TypeDefinition)
                return (TypeDefinitionHandle)parent;
        }

        return default;
    }

    // --- target resolution ------------------------------------------------------------------

    private static bool TryResolveTarget(
        HarmonyMethodInfo info,
        out string? targetType,
        out string? targetMethod,
        out string failureCode,
        out string failureDetail)
    {
        targetType = info.DeclaringTypeName;
        targetMethod = null;
        failureCode = "HarmonyUndefinedTargetMethod";
        failureDetail = string.Empty;

        if (string.IsNullOrEmpty(targetType))
        {
            failureCode = "HarmonyUndefinedTargetType";
            failureDetail = "no declaring type is specified by the class-level or method-level Harmony attributes.";
            return false;
        }

        var methodType = info.MethodType ?? MethodTypeNormal;
        var methodName = info.MethodName;
        var hasName = !string.IsNullOrEmpty(methodName);

        switch (methodType)
        {
            case MethodTypeNormal:
                if (!hasName)
                {
                    failureDetail = "MethodType.Normal requires a method name.";
                    return false;
                }
                targetMethod = methodName;
                return true;

            case MethodTypeGetter:
            case MethodTypeSetter:
                if (!hasName)
                {
                    // Upstream falls back to DeclaredIndexerGetter/Setter, which needs the target
                    // type's property table to find the indexer's real accessor name.
                    failureCode = "HarmonyIndexerTargetUnsupported";
                    failureDetail = $"MethodType.{(methodType == MethodTypeGetter ? "Getter" : "Setter")} without a " +
                                    "property name targets an indexer, which cannot be named from MOD metadata.";
                    return false;
                }
                targetMethod = (methodType == MethodTypeGetter ? "get_" : "set_") + methodName;
                return true;

            case MethodTypeConstructor:
                targetMethod = ".ctor";
                return true;

            case MethodTypeStaticConstructor:
                targetMethod = ".cctor";
                return true;

            case MethodTypeEnumerator:
                failureCode = "HarmonyEnumeratorTargetUnsupported";
                failureDetail = "MethodType.Enumerator retargets the compiler-generated MoveNext of the iterator " +
                                "state machine, which requires reading the target method's IL.";
                return false;

            case MethodTypeAsync:
                failureCode = "HarmonyAsyncTargetUnsupported";
                failureDetail = "MethodType.Async retargets the compiler-generated MoveNext of the async state " +
                                "machine, which requires reading the target method's IL.";
                return false;

            case MethodTypeFinalizer:
                targetMethod = "Finalize";
                return true;

            case MethodTypeEventAdd:
            case MethodTypeEventRemove:
                if (!hasName)
                {
                    failureDetail = $"MethodType.{(methodType == MethodTypeEventAdd ? "EventAdd" : "EventRemove")} requires an event name.";
                    return false;
                }
                targetMethod = (methodType == MethodTypeEventAdd ? "add_" : "remove_") + methodName;
                return true;

            case >= MethodTypeFirstOperator and <= MethodTypeLastOperator:
                targetMethod = OperatorMethodNames[methodType];
                return true;

            default:
                failureCode = "HarmonyUnknownMethodType";
                failureDetail = $"MethodType value {methodType} is not part of the mirrored Harmony enum.";
                return false;
        }
    }

    private static string BuildReason(HarmonyMethodInfo info, PcCompatPatchKind kind, string? prepareMethod)
    {
        var builder = new StringBuilder("Harmony attribute aggregation; callback translation decides applicability.");

        if (kind == PcCompatPatchKind.Unknown)
            builder.Append(" Inner (call-site) patching is unsupported.");

        // PatchAll applies categorised classes too, but PatchAllUncategorized skips them and
        // PatchCategory only takes one name - so which entry point the MOD calls decides whether this
        // descriptor is ever meant to apply.
        if (string.IsNullOrEmpty(info.Category) is false)
            builder.Append(" category=").Append(info.Category).Append('.');

        if (info.Priority != -1)
            builder.Append(" priority=").Append(info.Priority).Append('.');
        if (info.Before is { Count: > 0 })
            builder.Append(" before=").Append(string.Join(",", info.Before)).Append('.');
        if (info.After is { Count: > 0 })
            builder.Append(" after=").Append(string.Join(",", info.After)).Append('.');
        if (info.NonVirtualDelegate)
            builder.Append(" Non-virtual delegate dispatch requested.");
        if (prepareMethod is not null)
            builder.Append(" Gated by ").Append(prepareMethod).Append(" at runtime.");

        return builder.ToString();
    }

    // --- HarmonyMethod mirror ---------------------------------------------------------------

    /// <summary>
    /// Mirror of the <c>HarmonyMethod</c> fields that <c>HarmonyFields()</c> merges, minus
    /// <c>method</c> which is the patch method itself.
    /// </summary>
    private sealed class HarmonyMethodInfo
    {
        public string? Category;
        public string? DeclaringTypeName;
        public string? MethodName;
        public int? MethodType;
        public IReadOnlyList<string>? ArgumentTypes;

        /// <summary>-1 is the upstream "unset" sentinel, not a real priority.</summary>
        public int Priority = -1;

        public IReadOnlyList<string>? Before;
        public IReadOnlyList<string>? After;
        public int? ReversePatchType;
        public bool? Debug;

        /// <summary>Non-nullable upstream, so the last attribute to set it always wins.</summary>
        public bool NonVirtualDelegate;

        /// <summary>Set when ParseSpecialArguments would have thrown instead of decorating types.</summary>
        public string? VariationMismatch;
    }

    /// <summary>Mirror of <c>HarmonyMethod.Merge(List&lt;HarmonyMethod&gt;)</c>.</summary>
    private static HarmonyMethodInfo MergeAll(IReadOnlyList<HarmonyMethodInfo> attributes)
    {
        var result = new HarmonyMethodInfo();

        foreach (var attribute in attributes)
        {
            if (attribute.Category is not null)
                result.Category = attribute.Category;
            if (attribute.DeclaringTypeName is not null)
                result.DeclaringTypeName = attribute.DeclaringTypeName;
            if (attribute.MethodName is not null)
                result.MethodName = attribute.MethodName;
            if (attribute.MethodType is not null)
                result.MethodType = attribute.MethodType;
            if (attribute.ArgumentTypes is not null)
                result.ArgumentTypes = attribute.ArgumentTypes;
            // Skipping the sentinel keeps a [HarmonyPriority] from being wiped by a later attribute
            // that never touched priority.
            if (attribute.Priority != -1)
                result.Priority = attribute.Priority;
            if (attribute.Before is not null)
                result.Before = attribute.Before;
            if (attribute.After is not null)
                result.After = attribute.After;
            if (attribute.ReversePatchType is not null)
                result.ReversePatchType = attribute.ReversePatchType;
            if (attribute.Debug is not null)
                result.Debug = attribute.Debug;
            result.NonVirtualDelegate = attribute.NonVirtualDelegate;
            result.VariationMismatch ??= attribute.VariationMismatch;
        }

        return result;
    }

    /// <summary>Mirror of <c>HarmonyMethod.Merge(master, detail)</c>, where detail wins.</summary>
    private static HarmonyMethodInfo MergeDetail(HarmonyMethodInfo master, HarmonyMethodInfo? detail)
    {
        if (detail is null)
            return master;

        return new HarmonyMethodInfo
        {
            Category = detail.Category ?? master.Category,
            DeclaringTypeName = detail.DeclaringTypeName ?? master.DeclaringTypeName,
            MethodName = detail.MethodName ?? master.MethodName,
            MethodType = detail.MethodType ?? master.MethodType,
            ArgumentTypes = detail.ArgumentTypes ?? master.ArgumentTypes,
            Priority = MergePriority(master.Priority, detail.Priority),
            Before = detail.Before ?? master.Before,
            After = detail.After ?? master.After,
            ReversePatchType = detail.ReversePatchType ?? master.ReversePatchType,
            Debug = detail.Debug ?? master.Debug,
            // Boxed as a non-null bool upstream, so detail always overwrites.
            NonVirtualDelegate = detail.NonVirtualDelegate,
            VariationMismatch = detail.VariationMismatch ?? master.VariationMismatch
        };
    }

    private static int MergePriority(int master, int detail)
    {
        if (master == -1)
            return detail;
        if (detail == -1)
            return master;
        return Math.Max(master, detail);
    }

    // --- attribute decoding -----------------------------------------------------------------

    private static List<HarmonyMethodInfo> ReadHarmonyAttributes(
        MetadataReader reader,
        CustomAttributeHandleCollection attributes,
        string assemblyPath,
        string callbackType,
        string? callbackMethod,
        List<PcCompatStaticPatchScanIssue> issues,
        bool directSubclassesOnly = false)
    {
        var result = new List<HarmonyMethodInfo>();

        foreach (var handle in attributes)
        {
            var attribute = reader.GetCustomAttribute(handle);
            var attributeType = GetAttributeTypeFullName(reader, attribute);
            if (!attributeType.StartsWith(HarmonyNamespace, StringComparison.Ordinal))
            {
                // A MOD-defined subclass still carries an info field, so Harmony merges it, but the
                // values come from the subclass constructor body - which is IL this scanner refuses
                // to interpret.
                if (DerivesFromHarmonyAttribute(reader, attribute))
                {
                    issues.Add(Issue(
                        "HarmonyDerivedAttributeUnsupported",
                        $"{attributeType} derives from a Harmony attribute; the values its constructor " +
                        "assigns to info cannot be evaluated statically.",
                        assemblyPath,
                        callbackType,
                        callbackMethod));
                }

                continue;
            }

            var isDelegate = string.Equals(attributeType, HarmonyNamespace + "HarmonyDelegate", StringComparison.Ordinal);
            var isKnown = isDelegate || DirectHarmonyAttributeSubclasses.Contains(attributeType);
            if (!isKnown)
            {
                // HarmonyPrepare and friends derive from Attribute, carry no info field and are
                // handled by their own lookups; anything else under HarmonyLib means the MOD was
                // built against a Harmony version with attributes this mirror does not know.
                if (IsAuxiliaryAttribute(attributeType))
                    continue;

                issues.Add(Issue(
                    "HarmonyUnknownBuiltinAttribute",
                    $"{attributeType} is not part of the mirrored Harmony attribute set; " +
                    "its contribution to the patch target is ignored.",
                    assemblyPath,
                    callbackType,
                    callbackMethod));
                continue;
            }

            // AttributePatch.Create only merges attributes whose *direct* base is HarmonyAttribute,
            // while HarmonyMethodExtensions.GetFromType accepts anything with an info field. That
            // asymmetry is why HarmonyDelegate contributes at class level but not at method level.
            if (directSubclassesOnly && isDelegate)
                continue;

            ImmutableArray<CustomAttributeTypedArgument<string>> arguments;
            try
            {
                arguments = attribute.DecodeValue(AttributeTypeProvider.Instance).FixedArguments;
            }
            catch (Exception exception)
            {
                issues.Add(Issue(
                    "HarmonyAttributeDecodeFailed",
                    $"{attributeType} could not be decoded ({exception.GetType().Name}: {exception.Message}).",
                    assemblyPath,
                    callbackType,
                    callbackMethod));
                continue;
            }

            var info = DecodeAttribute(attributeType, arguments);
            if (info is not null)
                result.Add(info);
        }

        return result;
    }

    private static bool IsAuxiliaryAttribute(string attributeType) => attributeType switch
    {
        HarmonyNamespace + "HarmonyPrepare" => true,
        HarmonyNamespace + "HarmonyCleanup" => true,
        HarmonyNamespace + "HarmonyTargetMethod" => true,
        HarmonyNamespace + "HarmonyTargetMethods" => true,
        HarmonyNamespace + "HarmonyPrefix" => true,
        HarmonyNamespace + "HarmonyPostfix" => true,
        HarmonyNamespace + "HarmonyTranspiler" => true,
        HarmonyNamespace + "HarmonyFinalizer" => true,
        HarmonyNamespace + "HarmonyArgument" => true,
        _ => false
    };

    private static HarmonyMethodInfo? DecodeAttribute(
        string attributeType,
        ImmutableArray<CustomAttributeTypedArgument<string>> arguments)
    {
        var info = new HarmonyMethodInfo();

        switch (attributeType)
        {
            case HarmonyNamespace + "HarmonyPatch":
            case HarmonyNamespace + "HarmonyDelegate":
                DecodePatchArguments(info, arguments);
                return info;

            case HarmonyNamespace + "HarmonyPatchCategory":
                info.Category = AsString(arguments, 0);
                return info;

            case HarmonyNamespace + "HarmonyReversePatch":
                info.ReversePatchType = arguments.Length > 0 ? AsInt(arguments, 0) : 0;
                return info;

            case HarmonyNamespace + "HarmonyPriority":
                info.Priority = AsInt(arguments, 0) ?? -1;
                return info;

            case HarmonyNamespace + "HarmonyBefore":
                info.Before = AsStringArray(arguments, 0);
                return info;

            case HarmonyNamespace + "HarmonyAfter":
                info.After = AsStringArray(arguments, 0);
                return info;

            case HarmonyNamespace + "HarmonyDebug":
                info.Debug = true;
                return info;

            case HarmonyNamespace + "HarmonyPatchAll":
                // Carries no data; the class-level presence check drives the bulk path.
                return info;

            default:
                return null;
        }
    }

    /// <summary>
    /// Decodes the 20 <c>HarmonyPatch</c> constructors (and the <c>HarmonyDelegate</c> ones that
    /// chain into them) positionally. The declared type of each fixed argument is enough to tell the
    /// overloads apart, so the constructor itself never has to be resolved.
    /// </summary>
    private static void DecodePatchArguments(
        HarmonyMethodInfo info,
        ImmutableArray<CustomAttributeTypedArgument<string>> arguments)
    {
        IReadOnlyList<string>? argumentTypes = null;
        IReadOnlyList<int>? argumentVariations = null;
        var stringCount = arguments.Count(argument => argument.Type == "System.String");
        var seenStrings = 0;
        var dispatchOnly = arguments.Length == 1;

        foreach (var argument in arguments)
        {
            switch (argument.Type)
            {
                case "System.Type":
                    info.DeclaringTypeName ??= argument.Value as string;
                    break;

                case "System.String":
                    // Two strings only occur in (string typeName, string methodName, MethodType):
                    // the first names the declaring type, the second the method.
                    if (stringCount > 1 && seenStrings == 0)
                        info.DeclaringTypeName = NormalizeSerializedTypeName(argument.Value as string);
                    else
                        info.MethodName = argument.Value as string;
                    seenStrings++;
                    break;

                case HarmonyNamespace + "MethodType":
                    info.MethodType = AsInt(argument);
                    break;

                case HarmonyNamespace + "MethodDispatchType":
                    info.NonVirtualDelegate = AsInt(argument) == MethodDispatchTypeCall;
                    // Every HarmonyDelegate overload but the single-argument one chains into a base
                    // constructor that pins MethodType.Normal.
                    if (!dispatchOnly)
                        info.MethodType = MethodTypeNormal;
                    break;

                case "System.Type[]":
                    argumentTypes = AsTypeArray(argument);
                    break;

                case HarmonyNamespace + "ArgumentType[]":
                    argumentVariations = AsIntArray(argument);
                    break;
            }
        }

        ApplyArgumentVariations(info, argumentTypes, argumentVariations);
    }

    /// <summary>Mirror of <c>HarmonyPatch.ParseSpecialArguments</c>.</summary>
    private static void ApplyArgumentVariations(
        HarmonyMethodInfo info,
        IReadOnlyList<string>? argumentTypes,
        IReadOnlyList<int>? argumentVariations)
    {
        if (argumentTypes is null)
            return;

        if (argumentVariations is null || argumentVariations.Count == 0)
        {
            info.ArgumentTypes = argumentTypes;
            return;
        }

        if (argumentTypes.Count != argumentVariations.Count)
        {
            // Upstream throws ArgumentException when there are too many variations and
            // IndexOutOfRangeException when there are too few, so both directions abort the class.
            info.VariationMismatch =
                $"argumentTypes has {argumentTypes.Count} entries but argumentVariations has {argumentVariations.Count}; " +
                "Harmony throws for either direction.";
            info.ArgumentTypes = argumentTypes;
            return;
        }

        var decorated = new string[argumentTypes.Count];
        for (var i = 0; i < argumentTypes.Count; i++)
        {
            decorated[i] = argumentVariations[i] switch
            {
                // Ref and out are both byref in metadata; the signature provider spells that "T&".
                ArgumentTypeRef or ArgumentTypeOut => argumentTypes[i] + "&",
                ArgumentTypePointer => argumentTypes[i] + "*",
                _ => argumentTypes[i]
            };
        }

        info.ArgumentTypes = decorated;
    }

    private static string? AsString(ImmutableArray<CustomAttributeTypedArgument<string>> arguments, int index)
        => index < arguments.Length ? arguments[index].Value as string : null;

    private static int? AsInt(ImmutableArray<CustomAttributeTypedArgument<string>> arguments, int index)
        => index < arguments.Length ? AsInt(arguments[index]) : null;

    private static int? AsInt(CustomAttributeTypedArgument<string> argument)
    {
        try
        {
            return argument.Value is null ? null : Convert.ToInt32(argument.Value);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? AsStringArray(ImmutableArray<CustomAttributeTypedArgument<string>> arguments, int index)
    {
        if (index >= arguments.Length)
            return null;
        if (arguments[index].Value is not ImmutableArray<CustomAttributeTypedArgument<string>> elements)
            return null;
        return elements
            .Select(element => element.Value as string ?? string.Empty)
            .ToArray();
    }

    private static IReadOnlyList<string>? AsTypeArray(CustomAttributeTypedArgument<string> argument)
    {
        if (argument.Value is not ImmutableArray<CustomAttributeTypedArgument<string>> elements)
            return null;
        return elements
            .Select(element => NormalizeSerializedTypeName(element.Value as string) ?? string.Empty)
            .ToArray();
    }

    private static IReadOnlyList<int>? AsIntArray(CustomAttributeTypedArgument<string> argument)
    {
        if (argument.Value is not ImmutableArray<CustomAttributeTypedArgument<string>> elements)
            return null;
        return elements.Select(element => AsInt(element) ?? 0).ToArray();
    }

    // --- name helpers -----------------------------------------------------------------------

    private static string GetAttributeTypeFullName(MetadataReader reader, CustomAttribute attribute)
    {
        var constructor = attribute.Constructor;
        return constructor.Kind switch
        {
            HandleKind.MethodDefinition => PcCompatMetadataNames.GetTypeFullName(
                reader,
                reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType()),
            HandleKind.MemberReference => PcCompatMetadataNames.GetEntityTypeFullName(
                reader,
                reader.GetMemberReference((MemberReferenceHandle)constructor).Parent),
            _ => string.Empty
        };
    }

    /// <summary>
    /// Strips the assembly qualification from an <c>AssemblyQualifiedName</c>-style string while
    /// leaving nested generic arguments intact, so <c>List`1[[Int32, mscorlib]], mscorlib</c> keeps
    /// its inner brackets instead of being cut at the first comma.
    /// </summary>
    private static string? NormalizeSerializedTypeName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        var depth = 0;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '[')
                depth++;
            else if (c == ']')
                depth--;
            else if (c == ',' && depth == 0)
                return name[..i].Trim();
        }

        return name.Trim();
    }

    private static PcCompatStaticPatchScanIssue Issue(
        string code,
        string message,
        string assemblyPath,
        string? callbackType,
        string? callbackMethod)
        => new()
        {
            Code = code,
            Message = message,
            AssemblyPath = assemblyPath,
            CallbackType = callbackType,
            CallbackMethod = callbackMethod
        };

    /// <summary>
    /// Spells primitives as their framework names so decoded argument types match the
    /// <c>PcCompatSignatureTypeProvider</c> spelling used everywhere else in the scanner.
    /// </summary>
    private sealed class AttributeTypeProvider : ICustomAttributeTypeProvider<string>
    {
        public static readonly AttributeTypeProvider Instance = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.Byte => "System.Byte",
            PrimitiveTypeCode.Char => "System.Char",
            PrimitiveTypeCode.Double => "System.Double",
            PrimitiveTypeCode.Int16 => "System.Int16",
            PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.Int64 => "System.Int64",
            PrimitiveTypeCode.IntPtr => "System.IntPtr",
            PrimitiveTypeCode.Object => "System.Object",
            PrimitiveTypeCode.SByte => "System.SByte",
            PrimitiveTypeCode.Single => "System.Single",
            PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.UInt16 => "System.UInt16",
            PrimitiveTypeCode.UInt32 => "System.UInt32",
            PrimitiveTypeCode.UInt64 => "System.UInt64",
            PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
            PrimitiveTypeCode.Void => "System.Void",
            PrimitiveTypeCode.TypedReference => "System.TypedReference",
            _ => typeCode.ToString()
        };

        public string GetSystemType() => "System.Type";

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => PcCompatMetadataNames.GetTypeFullName(reader, handle);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            => PcCompatMetadataNames.GetEntityTypeFullName(reader, handle);

        public string GetTypeFromSerializedName(string name) => NormalizeSerializedTypeName(name) ?? name;

        // Every Harmony enum is Int32-backed, which is what the blob encodes.
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;

        public bool IsSystemType(string type) => type == "System.Type";
    }
}

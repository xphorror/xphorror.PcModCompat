using System.Reflection;

namespace HarmonyLib;

// Mirror of Harmony 2.4 HarmonyLib/Internal/PatchModels.cs (AttributePatch) and the target
// resolution half of Internal/PatchTools.cs (GetOriginalMethod / GetPatchMethod).
//
// The aggregation rules are the contract MODs are written against, so they are reproduced exactly:
// same patch-type precedence, same "method name or attribute" recognition, same two-level merge,
// same static-only requirement. What differs is the endpoint - instead of emitting a detour, the
// result is handed to HarmonyRegistry as a logical registration.
internal sealed class AttributePatch
{
    // Order matters: the first match wins, so a method named "Prefix" carrying [HarmonyPostfix]
    // is a prefix upstream too.
    private static readonly HarmonyPatchType[] AllPatchTypes =
    [
        HarmonyPatchType.Prefix,
        HarmonyPatchType.Postfix,
        HarmonyPatchType.Transpiler,
        HarmonyPatchType.Finalizer,
        HarmonyPatchType.ReversePatch,
        HarmonyPatchType.InnerPrefix,
        HarmonyPatchType.InnerPostfix
    ];

    internal HarmonyMethod info = new();

    internal HarmonyPatchType? type;

    internal static AttributePatch? Create(MethodInfo patch)
    {
        if (patch is null)
            throw new NullReferenceException("Patch method cannot be null");

        var allAttributes = patch.GetCustomAttributes(true);
        var type = GetPatchType(patch.Name, allAttributes);
        if (type is null)
            return null;

        if (type != HarmonyPatchType.ReversePatch && patch.IsStatic is false)
            throw new ArgumentException("Patch method " + patch.FullDescription() + " must be static");

        var list = allAttributes
            .Where(attr => attr.GetType().BaseType?.FullName == typeof(HarmonyAttribute).FullName)
            .Select(attr => AccessTools.Field(attr.GetType(), nameof(HarmonyAttribute.info))?.GetValue(attr))
            .OfType<HarmonyMethod>()
            .Select(method => method.Clone())
            .ToList();
        var info = HarmonyMethod.Merge(list);
        info.method = patch;

        return new AttributePatch { info = info, type = type };
    }

    private static HarmonyPatchType? GetPatchType(string methodName, object[] allAttributes)
    {
        var harmonyAttributes = new HashSet<string>(allAttributes
            .Select(attr => attr.GetType().FullName)
            .Where(name => name is not null && name.StartsWith("Harmony", StringComparison.Ordinal))
            .Select(name => name!));

        foreach (var patchType in AllPatchTypes)
        {
            var name = patchType.ToString();
            // Harmony 2.4 still ships no HarmonyInnerPrefix/HarmonyInnerPostfix attribute, so those
            // two are reachable through the method-name convention only. That falls out of this loop
            // automatically - do not "fix" it by inventing the attributes.
            if (name == methodName || harmonyAttributes.Contains($"HarmonyLib.Harmony{name}"))
                return patchType;
        }

        return null;
    }
}

internal static class HarmonyTargetResolution
{
    internal static MethodInfo? GetPatchMethod(Type patchType, string attributeName)
    {
        var method = patchType.GetMethods(AccessTools.all)
            .FirstOrDefault(m => m.GetCustomAttributes(true).Any(a => a.GetType().FullName == attributeName));
        if (method is null)
        {
            var methodName = attributeName.Replace("HarmonyLib.Harmony", "");
            method = patchType.GetMethod(methodName, AccessTools.all);
        }
        return method;
    }

    internal static List<AttributePatch> GetPatchMethods(Type type)
        => [.. AccessTools.GetDeclaredMethods(type)
            .Select(AttributePatch.Create)
            .Where(attributePatch => attributePatch is not null)
            .Select(attributePatch => attributePatch!)];

    /// <summary>
    /// Mirror of PatchTools.GetOriginalMethod. Returns null when the target cannot be resolved with
    /// reflection alone, which on Android is expected for IL2CPP types with no managed proxy.
    /// </summary>
    internal static MethodBase? GetOriginalMethod(this HarmonyMethod attr)
    {
        try
        {
            switch (attr.methodType)
            {
                case null:
                case MethodType.Normal:
                    if (string.IsNullOrEmpty(attr.methodName))
                        return null;
                    return AccessTools.DeclaredMethod(attr.declaringType, attr.methodName, attr.argumentTypes);

                case MethodType.Getter:
                    if (string.IsNullOrEmpty(attr.methodName))
                        return AccessTools.DeclaredIndexerGetter(attr.declaringType, attr.argumentTypes);
                    return AccessTools.DeclaredPropertyGetter(attr.declaringType, attr.methodName);

                case MethodType.Setter:
                    if (string.IsNullOrEmpty(attr.methodName))
                        return AccessTools.DeclaredIndexerSetter(attr.declaringType, attr.argumentTypes);
                    return AccessTools.DeclaredPropertySetter(attr.declaringType, attr.methodName);

                case MethodType.Constructor:
                    return AccessTools.DeclaredConstructor(attr.declaringType, attr.argumentTypes);

                case MethodType.StaticConstructor:
                    return AccessTools.GetDeclaredConstructors(attr.declaringType).FirstOrDefault(c => c.IsStatic);

                case MethodType.Enumerator:
                case MethodType.Async:
                {
                    // Upstream finds the compiler-generated MoveNext by reading the annotated method's
                    // IL. AccessTools takes it off the state machine attribute instead, which answers
                    // for any managed target; an IL2CPP target has no managed method to carry the
                    // attribute, so that case stays fail-closed with a diagnostic rather than a guess.
                    if (string.IsNullOrEmpty(attr.methodName))
                        return null;

                    var declared = AccessTools.DeclaredMethod(attr.declaringType, attr.methodName, attr.argumentTypes);
                    var moveNext = attr.methodType == MethodType.Enumerator
                        ? AccessTools.EnumeratorMoveNext(declared)
                        : AccessTools.AsyncMoveNext(declared);
                    if (moveNext is not null)
                        return moveNext;

                    HarmonyRegistry.Report(
                        attr.methodType == MethodType.Enumerator
                            ? "HarmonyEnumeratorTargetUnsupported"
                            : "HarmonyAsyncTargetUnsupported",
                        $"MethodType.{attr.methodType}",
                        $"target {TargetTypeName(attr)}.{attr.methodName} needs the state machine MoveNext, and " +
                        (declared is null
                            ? "the method is not visible to the managed runtime, so no state machine attribute can be read."
                            : "it carries no state machine attribute, so only its IL could name one - PcCompat has no IL for IL2CPP methods."));
                    return null;
                }

                case MethodType.Finalizer:
                    return AccessTools.DeclaredFinalizer(attr.declaringType);

                case MethodType.EventAdd:
                    if (string.IsNullOrEmpty(attr.methodName))
                        return null;
                    return AccessTools.DeclaredEventAdder(attr.declaringType, attr.methodName);

                case MethodType.EventRemove:
                    if (string.IsNullOrEmpty(attr.methodName))
                        return null;
                    return AccessTools.DeclaredEventRemover(attr.declaringType, attr.methodName);

                default:
                    return AccessTools.DeclaredMethod(attr.declaringType, OperatorName(attr.methodType.Value), attr.argumentTypes);
            }
        }
        catch (AmbiguousMatchException ex)
        {
            throw new HarmonyException($"Ambiguous match for HarmonyMethod[{attr.Description()}]", ex.InnerException ?? ex);
        }
    }

    /// <summary>
    /// The member name a MethodType maps onto. Used for registration identity when the declaring
    /// type is invisible to CoreCLR and <see cref="GetOriginalMethod"/> can only return null.
    /// </summary>
    internal static string TargetMemberName(HarmonyMethod attr)
    {
        var methodName = attr.methodName;
        return attr.methodType switch
        {
            null or MethodType.Normal or MethodType.Enumerator or MethodType.Async => methodName ?? "<unknown>",
            MethodType.Getter => methodName is null ? "get_Item" : $"get_{methodName}",
            MethodType.Setter => methodName is null ? "set_Item" : $"set_{methodName}",
            MethodType.Constructor => ".ctor",
            MethodType.StaticConstructor => ".cctor",
            MethodType.Finalizer => "Finalize",
            MethodType.EventAdd => $"add_{methodName}",
            MethodType.EventRemove => $"remove_{methodName}",
            _ => OperatorName(attr.methodType.Value)
        };
    }

    internal static string TargetTypeName(HarmonyMethod attr)
        => attr.declaringType?.FullName ?? attr.declaringTypeName ?? "<unknown>";

    private static string OperatorName(MethodType methodType)
        => "op_" + methodType.ToString().Replace("Operator", "");
}

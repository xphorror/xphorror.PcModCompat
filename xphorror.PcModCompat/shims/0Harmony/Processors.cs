using System.Reflection;
using System.Reflection.Emit;

namespace HarmonyLib;

// ABI mirror of Harmony 2.4 HarmonyLib/Public/{PatchProcessor,PatchClassProcessor,ReversePatcher}.cs.
//
// Aggregation is faithful; application is not and cannot be. PcCompat has a single physical hook
// owner (ModManager/HookBroker) and no IL emitter, so Patch() ends at HarmonyRegistry instead of a
// detour, and Unpatch() only clears the logical Active flag. Every deviation leaves a diagnostic.
public class PatchProcessor
{
    internal static readonly object locker = new();

    private readonly Harmony instance;

    private readonly MethodBase original;

    private readonly List<HarmonyMethod> prefixes = [];
    private readonly List<HarmonyMethod> postfixes = [];
    private readonly List<HarmonyMethod> transpilers = [];
    private readonly List<HarmonyMethod> finalizers = [];
    private readonly List<HarmonyMethod> innerprefixes = [];
    private readonly List<HarmonyMethod> innerpostfixes = [];

    public PatchProcessor(Harmony instance, MethodBase original)
    {
        this.instance = instance ?? throw new ArgumentNullException(nameof(instance));
        this.original = original ?? throw new ArgumentNullException(nameof(original));
    }

    public PatchProcessor AddPrefix(HarmonyMethod prefix)
    {
        prefixes.Add(prefix);
        return this;
    }

    public PatchProcessor AddPrefix(MethodInfo fixMethod)
    {
        prefixes.Add(new HarmonyMethod(fixMethod));
        return this;
    }

    public PatchProcessor AddPostfix(HarmonyMethod postfix)
    {
        postfixes.Add(postfix);
        return this;
    }

    public PatchProcessor AddPostfix(MethodInfo fixMethod)
    {
        postfixes.Add(new HarmonyMethod(fixMethod));
        return this;
    }

    public PatchProcessor AddTranspiler(HarmonyMethod transpiler)
    {
        transpilers.Add(transpiler);
        return this;
    }

    public PatchProcessor AddTranspiler(MethodInfo fixMethod)
    {
        transpilers.Add(new HarmonyMethod(fixMethod));
        return this;
    }

    public PatchProcessor AddFinalizer(HarmonyMethod finalizer)
    {
        finalizers.Add(finalizer);
        return this;
    }

    public PatchProcessor AddFinalizer(MethodInfo fixMethod)
    {
        finalizers.Add(new HarmonyMethod(fixMethod));
        return this;
    }

    public PatchProcessor AddInnerPrefix(HarmonyMethod innerPrefix)
    {
        innerprefixes.Add(innerPrefix);
        return this;
    }

    public PatchProcessor AddInnerPrefix(MethodInfo fixMethod)
    {
        innerprefixes.Add(new HarmonyMethod(fixMethod));
        return this;
    }

    public PatchProcessor AddInnerPostfix(HarmonyMethod innerPostfix)
    {
        innerpostfixes.Add(innerPostfix);
        return this;
    }

    public PatchProcessor AddInnerPostfix(MethodInfo fixMethod)
    {
        innerpostfixes.Add(new HarmonyMethod(fixMethod));
        return this;
    }

    /// <summary>
    /// Records the collected patches. Upstream returns the generated replacement method; there is no
    /// replacement here, so the original is returned when it is a MethodInfo (MODs normally ignore
    /// the value and only chain the call).
    /// </summary>
    public MethodInfo? Patch()
    {
        lock (locker)
        {
            Register(prefixes, nameof(HarmonyPatchType.Prefix));
            Register(postfixes, nameof(HarmonyPatchType.Postfix));
            Register(transpilers, nameof(HarmonyPatchType.Transpiler));
            Register(finalizers, nameof(HarmonyPatchType.Finalizer));
            Register(innerprefixes, nameof(HarmonyPatchType.InnerPrefix));
            Register(innerpostfixes, nameof(HarmonyPatchType.InnerPostfix));
        }

        return original as MethodInfo;
    }

    private void Register(List<HarmonyMethod> methods, string kind)
    {
        foreach (var method in methods)
        {
            HarmonyRegistry.Register(
                instance.Id,
                "PatchProcessor.Patch",
                original.DeclaringType?.FullName ?? "<unknown>",
                original.Name,
                kind,
                method.method,
                HarmonyRegistry.StatusRegistered,
                "native hook mapping is owned by ModManager/HookBroker",
                original,
                method.category,
                method.priority,
                method.before,
                method.after,
                method.debug ?? false);
        }
    }

    public PatchProcessor Unpatch(HarmonyPatchType type, string harmonyID)
    {
        var kinds = type == HarmonyPatchType.All
            ? null
            : new[] { type.ToString() };
        _ = HarmonyRegistry.Deactivate(
            record => Equals(record.OriginalMethod, original)
                      && (kinds is null || kinds.Contains(record.Kind))
                      && (harmonyID == "*" || record.HarmonyId == harmonyID),
            "PatchProcessor.Unpatch");
        return this;
    }

    public PatchProcessor Unpatch(MethodInfo patch)
    {
        _ = HarmonyRegistry.Deactivate(
            record => Equals(record.OriginalMethod, original) && Equals(record.CallbackMethodInfo, patch),
            "PatchProcessor.Unpatch");
        return this;
    }

    public static IEnumerable<MethodBase> GetAllPatchedMethods()
        => HarmonyRegistry.SnapshotRegisteredPatches()
            .Where(record => record.Active && record.OriginalMethod is not null)
            .Select(record => record.OriginalMethod!)
            .Distinct()
            .ToArray();

    public static Patches GetPatchInfo(MethodBase method)
    {
        var records = HarmonyRegistry.SnapshotRegisteredPatches()
            .Where(record => record.Active && Equals(record.OriginalMethod, method))
            .OrderBy(record => record.RegistrationIndex)
            .ToArray();

        return new Patches(
            Build(records, nameof(HarmonyPatchType.Prefix)),
            Build(records, nameof(HarmonyPatchType.Postfix)),
            Build(records, nameof(HarmonyPatchType.Transpiler)),
            Build(records, nameof(HarmonyPatchType.Finalizer)),
            Build(records, nameof(HarmonyPatchType.InnerPrefix)),
            Build(records, nameof(HarmonyPatchType.InnerPostfix)));
    }

    private static Patch[] Build(HarmonyRegistrationRecord[] records, string kind)
        => [.. records
            .Where(record => record.Kind == kind && record.CallbackMethodInfo is not null)
            .Select((record, index) => new Patch(
                record.CallbackMethodInfo!,
                index,
                record.HarmonyId,
                record.Priority,
                record.Before,
                record.After,
                record.Debug))];

    /// <summary>
    /// Priority/index ordering only. Upstream additionally resolves the before/after owner
    /// constraints through a topological sort; when any are present a diagnostic is recorded rather
    /// than silently returning a differently ordered list.
    /// </summary>
    public static List<MethodInfo> GetSortedPatchMethods(MethodBase original, Patch[] patches)
    {
        if (patches.Any(patch => patch.before.Length > 0 || patch.after.Length > 0))
        {
            HarmonyRegistry.Report(
                "HarmonyPatchOrderConstraintsIgnored",
                "PatchProcessor.GetSortedPatchMethods",
                $"before/after constraints on {original.DeclaringType?.FullName}.{original.Name} are not resolved; " +
                "ordering falls back to priority then registration index.");
        }

        return [.. patches.OrderBy(patch => patch).Select(patch => patch.PatchMethod)];
    }

    public static Dictionary<string, Version> VersionInfo(out Version currentVersion)
    {
        currentVersion = typeof(Harmony).Assembly.GetName().Version ?? new Version(0, 0);
        var result = new Dictionary<string, Version>();
        foreach (var record in HarmonyRegistry.SnapshotRegisteredPatches())
        {
            var assemblyVersion = record.CallbackMethodInfo?.DeclaringType?.Assembly.GetName().Version;
            if (assemblyVersion is not null)
                result[record.HarmonyId] = assemblyVersion;
        }
        return result;
    }

    public static ILGenerator CreateILGenerator()
        => throw UnsupportedIlApi("PatchProcessor.CreateILGenerator");

    public static ILGenerator CreateILGenerator(MethodBase original)
        => throw UnsupportedIlApi("PatchProcessor.CreateILGenerator");

    public static List<CodeInstruction> GetOriginalInstructions(MethodBase original, ILGenerator? generator = null)
        => throw UnsupportedIlApi("PatchProcessor.GetOriginalInstructions");

    public static List<CodeInstruction> GetOriginalInstructions(MethodBase original, out ILGenerator generator)
        => throw UnsupportedIlApi("PatchProcessor.GetOriginalInstructions");

    public static List<CodeInstruction> GetCurrentInstructions(
        MethodBase original,
        int maxTranspilers = int.MaxValue,
        ILGenerator? generator = null)
        => throw UnsupportedIlApi("PatchProcessor.GetCurrentInstructions");

    public static List<CodeInstruction> GetCurrentInstructions(
        MethodBase original,
        out ILGenerator generator,
        int maxTranspilers = int.MaxValue)
        => throw UnsupportedIlApi("PatchProcessor.GetCurrentInstructions");

    public static IEnumerable<KeyValuePair<OpCode, object>> ReadMethodBody(MethodBase method)
        => throw UnsupportedIlApi("PatchProcessor.ReadMethodBody");

    public static IEnumerable<KeyValuePair<OpCode, object>> ReadMethodBody(MethodBase method, ILGenerator generator)
        => throw UnsupportedIlApi("PatchProcessor.ReadMethodBody");

    private static NotSupportedException UnsupportedIlApi(string api)
    {
        const string detail = "reading or generating method IL is unavailable because PcCompat maps patches to metadata-resolved HookBroker rules.";
        HarmonyRegistry.ReportUnavailable(api, detail);
        return new NotSupportedException(detail);
    }
}

public class PatchClassProcessor
{
    private static readonly List<Type> AuxilaryTypes =
    [
        typeof(HarmonyPrepare),
        typeof(HarmonyCleanup),
        typeof(HarmonyTargetMethod),
        typeof(HarmonyTargetMethods)
    ];

    private readonly Harmony instance;

    private readonly Type containerType;

    private readonly HarmonyMethod containerAttributes;

    private readonly Dictionary<Type, MethodInfo> auxilaryMethods;

    private readonly List<AttributePatch> patchMethods;

    public string? Category { get; set; }

    public PatchClassProcessor(Harmony instance, Type type)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(type);

        this.instance = instance;
        containerType = type;

        containerAttributes = HarmonyMethod.Merge(HarmonyMethodExtensions.GetFromType(type));
        containerAttributes.methodType ??= MethodType.Normal;
        Category = containerAttributes.category;

        auxilaryMethods = [];
        foreach (var auxType in AuxilaryTypes)
        {
            var method = HarmonyTargetResolution.GetPatchMethod(containerType, auxType.FullName!);
            if (method is not null)
                auxilaryMethods[auxType] = method;
        }

        patchMethods = HarmonyTargetResolution.GetPatchMethods(containerType);
        foreach (var patchMethod in patchMethods)
        {
            var method = patchMethod.info.method;
            patchMethod.info = containerAttributes.Merge(patchMethod.info);
            patchMethod.info.method = method;
        }
    }

    public List<MethodInfo> Patch()
    {
        Exception? exception = null;

        var mainPrepareResult = RunMethod<HarmonyPrepare, bool>(true, false);
        if (mainPrepareResult is false)
        {
            RunMethod<HarmonyCleanup>(ref exception);
            ReportException(exception, null);
            return [];
        }

        var replacements = new List<MethodInfo>();
        MethodBase? lastOriginal = null;
        try
        {
            var originals = GetBulkMethods();

            if (originals.Count == 1)
                lastOriginal = originals[0];
            ReversePatch(ref lastOriginal);

            replacements = originals.Count > 0
                ? BulkPatch(originals, ref lastOriginal)
                : PatchWithAttributes();
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        RunMethod<HarmonyCleanup>(ref exception, exception);
        ReportException(exception, lastOriginal);
        return replacements;
    }

    public void Unpatch()
    {
        var kinds = patchMethods
            .Where(patchMethod => patchMethod.type.HasValue)
            .Select(patchMethod => patchMethod.type!.Value.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var callbacks = patchMethods
            .Select(patchMethod => patchMethod.info.method)
            .Where(method => method is not null)
            .ToHashSet();

        _ = HarmonyRegistry.Deactivate(
            record => record.HarmonyId == instance.Id
                      && kinds.Contains(record.Kind)
                      && record.CallbackMethodInfo is not null
                      && callbacks.Contains(record.CallbackMethodInfo),
            "PatchClassProcessor.Unpatch");
    }

    private void ReversePatch(ref MethodBase? lastOriginal)
    {
        foreach (var patchMethod in patchMethods)
        {
            if (patchMethod.type != HarmonyPatchType.ReversePatch)
                continue;

            var annotatedOriginal = patchMethod.info.GetOriginalMethod();
            if (annotatedOriginal is not null)
                lastOriginal = annotatedOriginal;

            // A reverse patch needs a real body copied out of the original, which PcCompat cannot
            // produce. Register it as unsupported so the host reports a concrete cause instead of
            // the MOD silently calling an empty stand-in.
            _ = HarmonyRegistry.Register(
                instance.Id,
                "PatchClassProcessor.ReversePatch",
                lastOriginal?.DeclaringType?.FullName ?? HarmonyTargetResolution.TargetTypeName(patchMethod.info),
                lastOriginal?.Name ?? HarmonyTargetResolution.TargetMemberName(patchMethod.info),
                nameof(HarmonyPatchType.ReversePatch),
                patchMethod.info.method,
                HarmonyRegistry.StatusUnsupported,
                "reverse patching needs an IL copy of the original body; PcCompat emits no IL",
                lastOriginal,
                patchMethod.info.category,
                patchMethod.info.priority,
                patchMethod.info.before,
                patchMethod.info.after,
                patchMethod.info.debug ?? false);
        }
    }

    private List<MethodInfo> BulkPatch(List<MethodBase> originals, ref MethodBase? lastOriginal)
    {
        var replacements = new List<MethodInfo>();
        foreach (var original in originals)
        {
            lastOriginal = original;
            foreach (var patchMethod in patchMethods)
            {
                const string note = "You cannot combine TargetMethod, TargetMethods or [HarmonyPatchAll] with individual annotations";
                var info = patchMethod.info;
                if (info.methodName is not null)
                    throw new ArgumentException($"{note} [{info.methodName}]");
                if (info.methodType.HasValue && info.methodType.Value != MethodType.Normal)
                    throw new ArgumentException($"{note} [{info.methodType}]");
                if (info.argumentTypes is not null)
                    throw new ArgumentException($"{note} [{info.argumentTypes.Description()}]");

                if (patchMethod.type == HarmonyPatchType.ReversePatch)
                    continue;

                RegisterPatch(patchMethod, original, original.DeclaringType?.FullName ?? "<unknown>", original.Name, "PatchClassProcessor.BulkPatch");
            }

            if (original is MethodInfo replacement)
                replacements.Add(replacement);
        }
        return replacements;
    }

    private List<MethodInfo> PatchWithAttributes()
    {
        var replacements = new List<MethodInfo>();
        foreach (var patchMethod in patchMethods)
        {
            if (patchMethod.type == HarmonyPatchType.ReversePatch)
                continue;

            var original = patchMethod.info.GetOriginalMethod();
            var targetType = HarmonyTargetResolution.TargetTypeName(patchMethod.info);
            var targetMethod = HarmonyTargetResolution.TargetMemberName(patchMethod.info);

            if (original is null)
            {
                // Upstream throws here. Under PcCompat the declaring type is usually an IL2CPP type
                // with no managed reflection identity, so a throw would break every MOD that names
                // a game type. The registration keeps the name pair the static aggregator also
                // produced, and the diagnostic records that the binding is name-based.
                if (targetType == "<unknown>" || targetMethod == "<unknown>")
                    throw new ArgumentException($"Undefined target method for patch method {patchMethod.info.method.FullDescription()}");

                HarmonyRegistry.Report(
                    "HarmonyTargetResolvedByNameOnly",
                    "PatchClassProcessor.PatchWithAttributes",
                    $"{targetType}.{targetMethod} has no managed reflection identity; " +
                    "the registration is matched by name against the static patch scan.");
            }
            else
            {
                targetType = original.DeclaringType?.FullName ?? targetType;
                targetMethod = original.Name;
            }

            RegisterPatch(patchMethod, original, targetType, targetMethod, "PatchClassProcessor.PatchWithAttributes");
            if (original is MethodInfo replacement)
                replacements.Add(replacement);
        }
        return replacements;
    }

    private void RegisterPatch(
        AttributePatch patchMethod,
        MethodBase? original,
        string targetType,
        string targetMethod,
        string api)
    {
        var info = patchMethod.info;
        _ = HarmonyRegistry.Register(
            instance.Id,
            api,
            targetType,
            targetMethod,
            patchMethod.type?.ToString() ?? "Unknown",
            info.method,
            HarmonyRegistry.StatusRegistered,
            "native hook mapping is owned by ModManager/HookBroker",
            original,
            info.category,
            info.priority,
            info.before,
            info.after,
            info.debug ?? false);
    }

    private List<MethodBase> GetBulkMethods()
    {
        var isPatchAll = containerType.GetCustomAttributes(true)
            .Any(a => a.GetType().FullName == typeof(HarmonyPatchAll).FullName);
        if (isPatchAll)
        {
            var type = containerAttributes.declaringType;
            if (type is null)
            {
                // Either the class forgot the type annotation (upstream error) or the annotated type
                // is IL2CPP-only. Both are unrecoverable here because the member list is unknown.
                var name = containerAttributes.declaringTypeName;
                HarmonyRegistry.Report(
                    "HarmonyPatchAllUnsupported",
                    "HarmonyPatchAll",
                    name is null
                        ? $"{containerType.FullName} uses [HarmonyPatchAll] without a resolvable Class/Type annotation."
                        : $"{containerType.FullName} uses [HarmonyPatchAll] on '{name}', whose member list is not visible to the managed runtime.");
                throw new ArgumentException(
                    $"Using {typeof(HarmonyPatchAll).FullName} requires an additional attribute for specifying the Class/Type");
            }

            var list = new List<MethodBase>();
            list.AddRange(AccessTools.GetDeclaredConstructors(type).Cast<MethodBase>());
            list.AddRange(AccessTools.GetDeclaredMethods(type).Cast<MethodBase>());
            var props = AccessTools.GetDeclaredProperties(type);
            list.AddRange(props.Select(prop => prop.GetGetMethod(true)).Where(method => method is not null).Cast<MethodBase>());
            list.AddRange(props.Select(prop => prop.GetSetMethod(true)).Where(method => method is not null).Cast<MethodBase>());
            return list;
        }

        var result = new List<MethodBase>();

        var targetMethods = RunMethod<HarmonyTargetMethods, IEnumerable<MethodBase>?>(null, null);
        if (targetMethods is not null)
        {
            result = [.. targetMethods];
            if (result.Any(m => m is null))
            {
                var error = "some element was null";
                throw auxilaryMethods.TryGetValue(typeof(HarmonyTargetMethods), out var method)
                    ? new Exception($"Method {method.FullDescription()} returned an unexpected result: {error}")
                    : new Exception($"Some method returned an unexpected result: {error}");
            }
            return result;
        }

        var targetMethod = RunMethod<HarmonyTargetMethod, MethodBase?>(null, null, method => method is null ? "null" : null);
        if (targetMethod is not null)
            result.Add(targetMethod);

        return result;
    }

    private void ReportException(Exception? exception, MethodBase? original)
    {
        if (exception is null)
            return;

        if ((containerAttributes.debug ?? false) || Harmony.DEBUG)
        {
            FileLog.Log($"### Exception from user \"{instance.Id}\", Harmony shim v{typeof(Harmony).Assembly.GetName().Version}");
            FileLog.Log($"### Original: {original.FullDescription()}");
            FileLog.Log($"### Patch class: {containerType.FullDescription()}");
            FileLog.Log((exception is HarmonyException harmony ? harmony.InnerException ?? harmony : exception).ToString());
        }

        if (exception is HarmonyException)
            throw exception;
        throw new HarmonyException($"Patching exception in method {original.FullDescription()}", exception);
    }

    private void RunMethod<S>(ref Exception? exception, params object?[] parameters)
    {
        if (auxilaryMethods.TryGetValue(typeof(S), out var method) is false)
            return;

        try
        {
            var input = (parameters ?? []).Union([instance]).ToArray();
            _ = method.Invoke(null, AccessTools.ActualParameters(method, input));
        }
        catch (Exception ex)
        {
            exception ??= ex.InnerException ?? ex;
        }
    }

    private T RunMethod<S, T>(T defaultIfNotExisting, T defaultIfFailing, Func<T, string?>? failOnResult = null, params object?[] parameters)
    {
        if (auxilaryMethods.TryGetValue(typeof(S), out var method) is false)
            return defaultIfNotExisting;

        var input = (parameters ?? []).Union([instance]).ToArray();
        var actualParameters = AccessTools.ActualParameters(method, input);

        if (method.ReturnType != typeof(void) && typeof(T).IsAssignableFrom(method.ReturnType) is false)
            throw new Exception($"Method {method.FullDescription()} has wrong return type (should be assignable to {typeof(T).FullName})");

        var result = defaultIfFailing;
        try
        {
            if (method.ReturnType == typeof(void))
            {
                _ = method.Invoke(null, actualParameters);
                result = defaultIfNotExisting;
            }
            else
            {
                result = (T)method.Invoke(null, actualParameters)!;
            }

            if (failOnResult is not null)
            {
                var error = failOnResult(result);
                if (error is not null)
                    throw new Exception($"Method {method.FullDescription()} returned an unexpected result: {error}");
            }
        }
        catch (Exception ex)
        {
            ReportException(ex.InnerException ?? ex, method);
        }
        return result;
    }
}

public class ReversePatcher
{
    private readonly Harmony instance;

    private readonly MethodBase original;

    private readonly HarmonyMethod standin;

    public ReversePatcher(Harmony instance, MethodBase original, HarmonyMethod standin)
    {
        this.instance = instance;
        this.original = original;
        this.standin = standin;
    }

    /// <summary>
    /// Records the reverse patch as unsupported and returns the stand-in unchanged. Upstream copies
    /// the original body into the stand-in, which requires reading and emitting IL.
    /// </summary>
    public MethodInfo? Patch(HarmonyReversePatchType type = HarmonyReversePatchType.Original)
    {
        _ = HarmonyRegistry.Register(
            instance.Id,
            "ReversePatcher.Patch",
            original?.DeclaringType?.FullName ?? HarmonyTargetResolution.TargetTypeName(standin),
            original?.Name ?? HarmonyTargetResolution.TargetMemberName(standin),
            $"{nameof(HarmonyPatchType.ReversePatch)}.{type}",
            standin.method,
            HarmonyRegistry.StatusUnsupported,
            "reverse patching needs an IL copy of the original body; PcCompat emits no IL",
            original,
            standin.category,
            standin.priority,
            standin.before,
            standin.after,
            standin.debug ?? false);
        return standin.method;
    }
}

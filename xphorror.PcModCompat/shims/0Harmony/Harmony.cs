using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace HarmonyLib;

// ABI mirror of Harmony 2.4 HarmonyLib/Public/Harmony.cs.
//
// Discovery (which classes, which patch methods, which targets) is faithful to upstream. Application
// is not: ModManager/HookBroker owns every physical hook for the whole process, so each Harmony call
// ends in the HarmonyRegistry logical registry, and Unpatch* only flips Active. The registry is what
// the host snapshots to build native rules and to bind managed callbacks.
public class Harmony
{
    private static readonly ConditionalWeakTable<Assembly, Dictionary<string, List<Type>>> AssemblyCachedCategories = new();

    private static readonly object SwitchLock = new();

    private static readonly Dictionary<string, object> Switches = new(StringComparer.Ordinal);

    public string Id { get; private set; }

    public static bool DEBUG;

    public Harmony(string id)
    {
        if (string.IsNullOrEmpty(id))
            throw new ArgumentException("id cannot be null or empty");

        try
        {
            var envDebug = Environment.GetEnvironmentVariable("HARMONY_DEBUG");
            if (string.IsNullOrEmpty(envDebug) is false)
            {
                envDebug = envDebug.Trim();
                DEBUG = envDebug == "1" || bool.Parse(envDebug);
            }
        }
        catch (Exception)
        {
            // Upstream ignores a malformed value too.
        }

        Id = id;
    }

    public void PatchAll()
    {
        var method = new StackTrace().GetFrame(1)?.GetMethod();
        var assembly = method?.ReflectedType?.Assembly;
        if (assembly is null)
        {
            HarmonyRegistry.ReportUnavailable(
                "PatchAll()",
                "the calling assembly could not be determined from the stack trace; call PatchAll(Assembly) explicitly.");
            return;
        }
        PatchAll(assembly);
    }

    public void PatchAll(Assembly assembly)
        => AccessTools.GetTypesFromAssembly(assembly)
            .DoIf(type => type.HasHarmonyAttribute(), type => _ = CreateClassProcessor(type).Patch());

    public void PatchAllUncategorized()
    {
        var method = new StackTrace().GetFrame(1)?.GetMethod();
        var assembly = method?.ReflectedType?.Assembly;
        if (assembly is null)
        {
            HarmonyRegistry.ReportUnavailable(
                "PatchAllUncategorized()",
                "the calling assembly could not be determined from the stack trace; call PatchAllUncategorized(Assembly) explicitly.");
            return;
        }
        PatchAllUncategorized(assembly);
    }

    public void PatchAllUncategorized(Assembly assembly)
    {
        var patchClasses = AccessTools.GetTypesFromAssembly(assembly)
            .Where(type => type.HasHarmonyAttribute())
            .Select(CreateClassProcessor)
            .ToArray();
        patchClasses.DoIf(
            patchClass => string.IsNullOrEmpty(patchClass.Category),
            patchClass => _ = patchClass.Patch());
    }

    public void PatchCategory(string category)
    {
        var method = new StackTrace().GetFrame(1)?.GetMethod();
        var assembly = method?.ReflectedType?.Assembly;
        if (assembly is null)
        {
            HarmonyRegistry.ReportUnavailable(
                "PatchCategory(string)",
                "the calling assembly could not be determined from the stack trace; call PatchCategory(Assembly, string) explicitly.");
            return;
        }
        PatchCategory(assembly, category);
    }

    public void PatchCategory(Assembly assembly, string category)
    {
        var categoryCache = AssemblyCachedCategories.GetValue(assembly, BuildCategoryCache);
        if (categoryCache.TryGetValue(category, out var toPatch))
            toPatch.Do(type => _ = CreateClassProcessor(type).Patch());
    }

    public void UnpatchCategory(string category)
    {
        var method = new StackTrace().GetFrame(1)?.GetMethod();
        var assembly = method?.ReflectedType?.Assembly;
        if (assembly is null)
        {
            HarmonyRegistry.ReportUnavailable(
                "UnpatchCategory(string)",
                "the calling assembly could not be determined from the stack trace; call UnpatchCategory(Assembly, string) explicitly.");
            return;
        }
        UnpatchCategory(assembly, category);
    }

    public void UnpatchCategory(Assembly assembly, string category)
    {
        var categoryCache = AssemblyCachedCategories.GetValue(assembly, BuildCategoryCache);
        if (categoryCache.TryGetValue(category, out var toPatch))
            toPatch.Do(type => CreateClassProcessor(type).Unpatch());
    }

    private Dictionary<string, List<Type>> BuildCategoryCache(Assembly assembly)
    {
        var cache = new Dictionary<string, List<Type>>(StringComparer.Ordinal);
        foreach (var type in AccessTools.GetTypesFromAssembly(assembly))
        {
            if (type.HasHarmonyAttribute() is false)
                continue;
            var category = HarmonyMethod.Merge(HarmonyMethodExtensions.GetFromType(type)).category;
            if (string.IsNullOrEmpty(category))
                continue;
            if (cache.TryGetValue(category, out var list) is false)
                cache[category] = list = [];
            list.Add(type);
        }
        return cache;
    }

    public PatchProcessor CreateProcessor(MethodBase original) => new(this, original);

    public PatchClassProcessor CreateClassProcessor(Type type) => new(this, type);

    public ReversePatcher CreateReversePatcher(MethodBase original, HarmonyMethod standin) => new(this, original, standin);

    public MethodInfo? Patch(
        MethodBase original,
        HarmonyMethod? prefix = null,
        HarmonyMethod? postfix = null,
        HarmonyMethod? transpiler = null,
        HarmonyMethod? finalizer = null)
    {
        var processor = CreateProcessor(original);
        if (prefix is not null)
            _ = processor.AddPrefix(prefix);
        if (postfix is not null)
            _ = processor.AddPostfix(postfix);
        if (transpiler is not null)
            _ = processor.AddTranspiler(transpiler);
        if (finalizer is not null)
            _ = processor.AddFinalizer(finalizer);
        return processor.Patch();
    }

    /// <summary>
    /// Registers the reverse patch as unsupported and returns the stand-in unchanged; copying the
    /// original body into the stand-in would require emitting IL.
    /// </summary>
    public static MethodInfo? ReversePatch(MethodBase original, HarmonyMethod standin, MethodInfo? transpiler = null)
    {
        if (transpiler is not null)
        {
            HarmonyRegistry.ReportUnavailable(
                "Harmony.ReversePatch(transpiler)",
                $"the transpiler {transpiler.FullDescription()} cannot run because there is no IL to transform.");
        }

        return new ReversePatcher(new Harmony("<static>"), original, standin).Patch();
    }

    public void UnpatchAll(string? harmonyID = null)
        => HarmonyRegistry.Deactivate(
            record => harmonyID is null || record.HarmonyId == harmonyID,
            "Harmony.UnpatchAll");

    public void Unpatch(MethodBase original, HarmonyPatchType type, string harmonyID = "*")
        => CreateProcessor(original).Unpatch(type, harmonyID);

    public void Unpatch(MethodBase original, MethodInfo patch)
        => CreateProcessor(original).Unpatch(patch);

    public static bool HasAnyPatches(string harmonyID)
        => HarmonyRegistry.SnapshotRegisteredPatches()
            .Any(record => record.Active && record.HarmonyId == harmonyID);

    public static Patches GetPatchInfo(MethodBase method) => PatchProcessor.GetPatchInfo(method);

    public IEnumerable<MethodBase> GetPatchedMethods()
        => HarmonyRegistry.SnapshotRegisteredPatches()
            .Where(record => record.Active && record.HarmonyId == Id && record.OriginalMethod is not null)
            .Select(record => record.OriginalMethod!)
            .Distinct()
            .ToArray();

    public static IEnumerable<MethodBase> GetAllPatchedMethods() => PatchProcessor.GetAllPatchedMethods();

    /// <summary>
    /// Always null: there is no generated replacement method to map back to an original.
    /// </summary>
    public static MethodBase? GetOriginalMethod(MethodInfo replacement)
    {
        HarmonyRegistry.ReportUnavailable(
            "Harmony.GetOriginalMethod",
            $"{replacement.FullDescription()} cannot be mapped back because PcCompat generates no replacement methods.");
        return null;
    }

    public static MethodBase? GetMethodFromStackframe(StackFrame frame) => frame.GetMethod();

    public static MethodBase? GetOriginalMethodFromStackframe(StackFrame frame)
    {
        var method = GetMethodFromStackframe(frame);
        return method is MethodInfo replacement ? GetOriginalMethod(replacement) ?? method : method;
    }

    public static Dictionary<string, Version> VersionInfo(out Version currentVersion) => PatchProcessor.VersionInfo(out currentVersion);

    public static void SetSwitch(string name, object value)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (SwitchLock)
            Switches[name] = value;
        HarmonyRegistry.Report(
            "HarmonySwitchLocalOnly",
            "Harmony.SetSwitch",
            $"switch '{name}' is retained for MOD round-trips but does not configure a MonoMod/DMD backend.");
    }

    public static void ClearSwitch(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (SwitchLock)
            _ = Switches.Remove(name);
    }

    public static bool TryGetSwitch(string name, out object? value)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (SwitchLock)
            return Switches.TryGetValue(name, out value);
    }

    public static bool TryIsSwitchEnabled(string name, out bool isEnabled)
    {
        if (TryGetSwitch(name, out var value) is false)
        {
            isEnabled = false;
            return false;
        }

        isEnabled = value switch
        {
            bool boolean => boolean,
            string text when bool.TryParse(text, out var parsed) => parsed,
            null => false,
            _ => true
        };
        return true;
    }
}

using System.Reflection;
using HarmonyLib;

namespace JALib.Core.Patch;

[Flags]
public enum PatchBinding
{
    None = 0,
    Prefix = 1,
    Postfix = 2,
    Transpiler = 4,
    Finalizer = 8,
    Replace = 16,
    AllNormalPatch = Prefix | Postfix | Transpiler | Finalizer | Replace,
    Reverse = 32,
    Override = 64,
    AllPatch = AllNormalPatch | Reverse | Override
}

public class PatchData
{
    public MethodBase[] Prefixes = [];
    public MethodBase[] Postfixes = [];
    public MethodBase[] TryPrefixes = [];
    public MethodBase[] TryPostfixes = [];
    public MethodBase[] Transpilers = [];
    public MethodBase[] Finalizers = [];
    public MethodBase[] Replaces = [];
    public MethodBase[] Removes = [];
    public MethodBase[] Overrides = [];

    internal PatchData() { }
}

public class JAPatchInfo
{
    public MethodBase Original;
    public HarmonyLib.Patch[] Prefixes = [];
    public HarmonyLib.Patch[] Postfixes = [];
    public HarmonyLib.Patch[] Transpilers = [];
    public HarmonyLib.Patch[] Finalizers = [];
    public TriedPatchData[] TryPrefixes = [];
    public TriedPatchData[] TryPostfixes = [];
    public HarmonyLib.Patch[] Replaces = [];
    public HarmonyLib.Patch[] Removes = [];
    public ReversePatchData[] ReversePatches = [];
    public OverridePatchData[] OverridePatches = [];

    internal JAPatchInfo(MethodBase original)
    {
        Original = original;
    }
}

public class TriedPatchData : HarmonyLib.Patch
{
    public readonly JAMod Mod;

    internal TriedPatchData(
        MethodInfo patch,
        int index,
        string owner,
        int priority,
        string[]? before,
        string[]? after,
        bool debug,
        JAMod mod)
        : base(patch, index, owner, priority, before, after, debug)
    {
        Mod = mod;
    }

    internal TriedPatchData(HarmonyMethod method, int index, string owner, JAMod mod)
        : this(
            method.method ?? throw new ArgumentException("HarmonyMethod has no method.", nameof(method)),
            index,
            owner,
            method.priority,
            method.before,
            method.after,
            method.debug.GetValueOrDefault(),
            mod)
    {
    }
}

public class ReversePatchData
{
    public MethodBase Original = null!;
    public MethodInfo PatchMethod = null!;
    public bool Debug;
    public JAReversePatchAttribute Attribute = null!;
    public JAMod Mod = null!;

    internal ReversePatchData() { }
}

public class OverridePatchData
{
    public readonly Type TargetType;
    public readonly MethodInfo PatchMethod;
    public readonly bool IgnoreBasePatch;
    public readonly bool Debug;
    public readonly bool TryCatch;
    public readonly string ID;
    public readonly JAMod Mod;

    internal OverridePatchData(
        Type targetType,
        MethodInfo patchMethod,
        bool ignoreBasePatch,
        bool debug,
        bool tryCatch,
        string id,
        JAMod mod)
    {
        TargetType = targetType;
        PatchMethod = patchMethod;
        IgnoreBasePatch = ignoreBasePatch;
        Debug = debug;
        TryCatch = tryCatch;
        ID = id;
        Mod = mod;
    }

    internal OverridePatchData(MethodInfo patchMethod, JAOverridePatchAttribute attribute, JAMod mod)
        : this(
            attribute.targetType ?? attribute.ResolvedTargetType ?? patchMethod.DeclaringType!,
            patchMethod,
            attribute.IgnoreBasePatch,
            attribute.Debug,
            attribute.TryingCatch,
            attribute.PatchId,
            mod)
    {
    }
}

public static class JAPatchManager
{
    public static PatchData GetPatchData(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var info = GetPatchInfo(method);
        return new PatchData
        {
            Prefixes = Methods(info.Prefixes),
            Postfixes = Methods(info.Postfixes),
            TryPrefixes = Methods(info.TryPrefixes),
            TryPostfixes = Methods(info.TryPostfixes),
            Transpilers = Methods(info.Transpilers),
            Finalizers = Methods(info.Finalizers),
            Replaces = Methods(info.Replaces),
            Removes = Methods(info.Removes),
            Overrides = info.OverridePatches.Select(patch => (MethodBase)patch.PatchMethod).ToArray()
        };
    }

    public static JAPatchInfo GetPatchInfo(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var records = JAPatcher.SnapshotRegisteredPatches()
            .Where(record => record.Active && Equals(record.OriginalMethod, method))
            .OrderBy(record => record.RegistrationIndex)
            .ToArray();
        var result = new JAPatchInfo(method)
        {
            Prefixes = BuildPatches(records, "Prefix", trying: false),
            Postfixes = BuildPatches(records, "Postfix", trying: false),
            Transpilers = BuildPatches(records, "Transpiler", trying: false),
            Finalizers = BuildPatches(records, "Finalizer", trying: false),
            TryPrefixes = BuildTriedPatches(records, "Prefix"),
            TryPostfixes = BuildTriedPatches(records, "Postfix"),
            Replaces = BuildPatches(records, "Replace", trying: null),
            Removes = BuildPatches(records, "Remove", trying: null),
            ReversePatches = records
                .Where(record => record.Kind == "ReversePatch")
                .Select(record => record.CreateReversePatchData())
                .Where(data => data != null)
                .Cast<ReversePatchData>()
                .ToArray(),
            OverridePatches = records
                .Where(record => record.Kind == "Override")
                .Select(record => record.CreateOverridePatchData())
                .Where(data => data != null)
                .Cast<OverridePatchData>()
                .ToArray()
        };
        return result;
    }

    public static IEnumerable<JAPatchInfo> GetPatchInfos()
        => JAPatcher.SnapshotRegisteredPatches()
            .Where(record => record.Active && record.OriginalMethod != null)
            .Select(record => record.OriginalMethod!)
            .Distinct()
            .Select(GetPatchInfo)
            .ToArray();

    private static HarmonyLib.Patch[] BuildPatches(
        IEnumerable<RegisteredPatchRecord> records,
        string kind,
        bool? trying)
        => records
            .Where(record => record.Kind == kind &&
                             (!trying.HasValue || record.TryingCatch == trying.Value))
            .Select(record => record.CreatePatch())
            .Where(patch => patch != null)
            .Cast<HarmonyLib.Patch>()
            .ToArray();

    private static TriedPatchData[] BuildTriedPatches(
        IEnumerable<RegisteredPatchRecord> records,
        string kind)
        => records
            .Where(record => record.Kind == kind && record.TryingCatch)
            .Select(record => record.CreateTriedPatchData())
            .Where(patch => patch != null)
            .Cast<TriedPatchData>()
            .ToArray();

    private static MethodBase[] Methods(IEnumerable<HarmonyLib.Patch> patches)
        => patches.Select(patch => (MethodBase)patch.PatchMethod).ToArray();
}

using System.Reflection;

namespace HarmonyLib;

// PcCompat has exactly one physical hook owner (ModManager/HookBroker), so the shim can never
// install or remove a detour on its own. Everything a MOD asks Harmony to do is therefore recorded
// in this process-wide logical registry: the ModManager snapshots it to build native rules and to
// bind managed callbacks, and Unpatch* only flips the logical Active flag.
//
// Record property names intentionally match JALib.Core.Patch.RegisteredPatchRecord so the existing
// duck-typed snapshot readers in PcCompatManagedLoader / PcCompatManagedModSession can consume both
// registries through the same code path.
public sealed class HarmonyRegistrationRecord
{
    private int _active = 1;

    internal HarmonyRegistrationRecord(
        long registrationIndex,
        string harmonyId,
        string api,
        string targetType,
        string targetMethod,
        string kind,
        string callbackType,
        string callbackMethod,
        string status,
        string reason,
        MethodBase? originalMethod,
        MethodInfo? callbackMethodInfo,
        string? category,
        int priority,
        string[] before,
        string[] after,
        bool debug)
    {
        RegistrationIndex = registrationIndex;
        HarmonyId = harmonyId;
        Api = api;
        TargetType = targetType;
        TargetMethod = targetMethod;
        Kind = kind;
        CallbackType = callbackType;
        CallbackMethod = callbackMethod;
        Status = status;
        Reason = reason;
        OriginalMethod = originalMethod;
        CallbackMethodInfo = callbackMethodInfo;
        Category = category;
        Priority = priority;
        Before = before;
        After = after;
        Debug = debug;
    }

    public long RegistrationIndex { get; }

    public string HarmonyId { get; }

    /// <summary>Which Harmony entry point produced this registration (PatchAll, Patch, ...).</summary>
    public string Api { get; }

    public string TargetType { get; }

    public string TargetMethod { get; }

    public string Kind { get; }

    public string CallbackType { get; }

    public string CallbackMethod { get; }

    public string Status { get; }

    public string Reason { get; }

    public MethodBase? OriginalMethod { get; }

    public MethodInfo? CallbackMethodInfo { get; }

    /// <summary>Harmony patch callbacks are always static, so there is never an instance target.</summary>
    public object? CallbackTarget => null;

    public string? Category { get; }

    public int Priority { get; }

    public string[] Before { get; }

    public string[] After { get; }

    public bool Debug { get; }

    public bool Active => Volatile.Read(ref _active) != 0;

    internal void Deactivate() => Volatile.Write(ref _active, 0);

    public override string ToString()
        => $"{HarmonyId}: {Kind} {TargetType}.{TargetMethod} -> {CallbackType}.{CallbackMethod} " +
           $"api={Api} priority={Priority} [{Status}] {Reason}";
}

public sealed class HarmonyDiagnostic
{
    internal HarmonyDiagnostic(string code, string api, string detail)
    {
        Code = code;
        Api = api;
        Detail = detail;
    }

    public string Code { get; }

    public string Api { get; }

    public string Detail { get; }

    public override string ToString() => $"{Code} api={Api} {Detail}";
}

public static class HarmonyRegistry
{
    public const string StatusRegistered = "registered_only";
    public const string StatusUnsupported = "unsupported";

    // A MOD in a patch loop must not be able to exhaust memory through the diagnostic sink, and a
    // repeated failure carries no extra information after the first occurrence.
    private const int MaxDiagnostics = 512;

    private static readonly object Lock = new();
    private static readonly List<HarmonyRegistrationRecord> Records = [];
    private static readonly List<HarmonyDiagnostic> Diagnostics = [];
    private static readonly HashSet<string> SeenDiagnostics = new(StringComparer.Ordinal);
    private static long s_registrationIndex;
    private static int s_revision;

    public static int RegisteredPatchCount
    {
        get
        {
            lock (Lock)
                return Records.Count;
        }
    }

    public static int Revision => Volatile.Read(ref s_revision);

    public static HarmonyRegistrationRecord[] SnapshotRegisteredPatches()
    {
        lock (Lock)
            return [.. Records];
    }

    public static HarmonyDiagnostic[] SnapshotDiagnostics()
    {
        lock (Lock)
            return [.. Diagnostics];
    }

    /// <summary>
    /// Drops every registration. Called by the host between MOD reloads; it does not - and must not -
    /// touch any physical hook, which stays owned by the native HookBroker for the whole process.
    ///
    /// Diagnostics deliberately survive. The host clears the registry after the MOD's bootstrap entry
    /// point has already run, so wiping them here would erase exactly the evidence of what a MOD asked
    /// for before setup - the one window nothing else records.
    /// </summary>
    public static void ClearRegisteredPatches()
    {
        lock (Lock)
        {
            Records.Clear();
            ++s_revision;
        }
    }

    /// <summary>
    /// Drops the diagnostic sink. Separate from <see cref="ClearRegisteredPatches"/> so a host that
    /// wants a fresh paper trail per process - rather than per MOD load - can ask for one explicitly.
    /// </summary>
    public static void ClearDiagnostics()
    {
        lock (Lock)
        {
            Diagnostics.Clear();
            SeenDiagnostics.Clear();
        }
    }

    internal static HarmonyRegistrationRecord Register(
        string harmonyId,
        string api,
        string targetType,
        string targetMethod,
        string kind,
        MethodInfo? callback,
        string status,
        string reason,
        MethodBase? originalMethod = null,
        string? category = null,
        int priority = -1,
        string[]? before = null,
        string[]? after = null,
        bool debug = false)
    {
        var record = new HarmonyRegistrationRecord(
            Interlocked.Increment(ref s_registrationIndex),
            harmonyId,
            api,
            targetType,
            targetMethod,
            kind,
            callback?.DeclaringType?.FullName ?? "<unknown>",
            callback?.Name ?? "<unknown>",
            status,
            reason,
            originalMethod,
            callback,
            category,
            priority,
            before ?? [],
            after ?? [],
            debug);

        lock (Lock)
        {
            Records.Add(record);
            ++s_revision;
        }

        Console.WriteLine(
            $"[PcModCompat][Harmony] id={record.HarmonyId} api={record.Api} " +
            $"patch={record.TargetType}.{record.TargetMethod} kind={record.Kind} " +
            $"callback={record.CallbackType}.{record.CallbackMethod} status={record.Status}");
        return record;
    }

    /// <summary>
    /// Flips matching registrations to inactive. Physical hooks are never removed inside a session,
    /// so a later re-patch of the same target just adds a new active registration.
    /// </summary>
    internal static int Deactivate(Func<HarmonyRegistrationRecord, bool> predicate, string api)
    {
        HarmonyRegistrationRecord[] affected;
        lock (Lock)
        {
            affected = [.. Records.Where(record => record.Active && predicate(record))];
            foreach (var record in affected)
                record.Deactivate();
            if (affected.Length > 0)
                ++s_revision;
        }

        foreach (var record in affected)
        {
            Console.WriteLine(
                $"[PcModCompat][Harmony] id={record.HarmonyId} api={api} " +
                $"deactivate={record.TargetType}.{record.TargetMethod} kind={record.Kind} " +
                $"callback={record.CallbackType}.{record.CallbackMethod}");
        }

        return affected.Length;
    }

    /// <summary>
    /// Records an ABI member that exists but cannot behave like upstream here. Never silently
    /// no-ops: the host exports these so an unexplained MOD behaviour always has a paper trail.
    /// </summary>
    public static void ReportUnavailable(string api, string detail) => Report("HarmonyUnavailable", api, detail);

    internal static void ReportUnresolvedTypeName(string typeName, string? methodName)
        => Report(
            "HarmonyUnresolvedDeclaringType",
            "HarmonyPatch(string typeName, ...)",
            $"type '{typeName}'{(string.IsNullOrEmpty(methodName) ? "" : $" method '{methodName}'")} " +
            "is not visible to the managed runtime; the static aggregator resolves it from the attribute blob instead.");

    /// <summary>
    /// Records a <c>"Type:Member"</c> lookup whose type half did not resolve. Upstream would either
    /// throw NullReferenceException from inside AccessTools or return null with no trace; either way
    /// the MOD is left guessing, so the miss is written down here before null is handed back.
    /// </summary>
    internal static void ReportUnresolvedTypeColonName(string api, string typeColonName, string typeName)
        => Report(
            "HarmonyUnresolvedDeclaringType",
            api,
            $"'{typeColonName}' names type '{typeName}', which is not visible to the managed runtime; " +
            "returning null instead of dereferencing it.");

    internal static void Report(string code, string api, string detail)
    {
        var key = $"{code}|{api}|{detail}";
        lock (Lock)
        {
            if (SeenDiagnostics.Add(key) is false)
                return;
            if (Diagnostics.Count >= MaxDiagnostics)
                return;
            Diagnostics.Add(new HarmonyDiagnostic(code, api, detail));
        }

        Console.WriteLine($"[PcModCompat][Harmony] diagnostic={code} api={api} {detail}");
    }
}

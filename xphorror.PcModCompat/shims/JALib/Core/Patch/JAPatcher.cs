using System.Reflection;

namespace JALib.Core.Patch;

public class JAPatcher
{
    private static readonly object RegistryLock = new();
    private static readonly List<RegisteredPatchRecord> RegisteredPatches = new();

    private readonly JAMod _mod;
    private readonly List<(MethodInfo? Method, JAPatchBaseAttribute Attribute, object? Target)> _patches = new();
    private readonly List<RegisteredPatchRecord> _registrations = new();
    private long _generation;
    private bool _disposed;
    private static int s_registryRevision;
    private static int s_registrationIndex;

    public delegate void FailPatch(string patchId, bool disabled);

    public event FailPatch? OnFailPatch;

    public bool usingWaiting = true;

    // Real JALib exposes this as a property (get_patched); MOD IL binds to the getter.
    public bool patched { get; private set; }

    public JAPatcher(JAMod mod)
    {
        _mod = mod;
    }

    public JAPatcher AddPatch(Delegate callback)
    {
        foreach (var attr in callback.Method.GetCustomAttributes<JAPatchBaseAttribute>())
            AddEntry(callback.Method, attr, callback.Target);
        return this;
    }

    public JAPatcher AddPatch(Delegate callback, JAPatchBaseAttribute attribute)
    {
        AddEntry(callback.Method, attribute, callback.Target);
        return this;
    }

    public JAPatcher AddPatch(Delegate callback, JAPatchAttribute attribute)
        => AddPatch(callback, (JAPatchBaseAttribute)attribute);

    public JAPatcher AddPatch(MethodInfo method)
    {
        foreach (var attr in method.GetCustomAttributes<JAPatchBaseAttribute>())
            AddEntry(method, attr, null);
        return this;
    }

    public JAPatcher AddPatch(MethodInfo method, JAPatchBaseAttribute attribute)
    {
        AddEntry(method, attribute, null);
        return this;
    }

    public JAPatcher AddPatch(MethodInfo method, JAPatchAttribute attribute)
        => AddPatch(method, (JAPatchBaseAttribute)attribute);

    public JAPatcher AddPatch(Type type)
    {
        // Real JALib scans every method on the type (static and instance).
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
        {
            foreach (var attr in method.GetCustomAttributes<JAPatchBaseAttribute>())
                AddEntry(method, attr, null);
        }
        return this;
    }

    public JAPatcher AddPatch(Type type, PatchBinding binding)
    {
        ArgumentNullException.ThrowIfNull(type);
        foreach (var method in type.GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic |
                     BindingFlags.Static | BindingFlags.Instance))
        {
            foreach (var attribute in method.GetCustomAttributes<JAPatchBaseAttribute>())
            {
                if (!Matches(binding, attribute))
                    continue;
                attribute.Method = method;
                AddPatch(attribute);
            }
        }
        return this;
    }

    public JAPatcher AddAllPatch(PatchBinding binding)
        => AddAllPatch(_mod.GetType().Assembly, binding);

    public JAPatcher AddAllPatch(Assembly assembly, PatchBinding binding)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var type in GetLoadableTypes(assembly))
            AddPatch(type, binding);
        return this;
    }

    public JAPatcher AddPatch(string nameSpace)
        => AddPatch(_mod.GetType().Assembly, nameSpace);

    public JAPatcher AddPatch(string nameSpace, PatchBinding binding)
        => AddPatch(_mod.GetType().Assembly, nameSpace, binding);

    public JAPatcher AddPatch(Assembly assembly, string nameSpace)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var type in GetLoadableTypes(assembly)
                     .Where(type => string.Equals(type.Namespace, nameSpace, StringComparison.Ordinal)))
        {
            AddPatch(type);
        }
        return this;
    }

    public JAPatcher AddPatch(Assembly assembly, string nameSpace, PatchBinding binding)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var type in GetLoadableTypes(assembly)
                     .Where(type => string.Equals(type.Namespace, nameSpace, StringComparison.Ordinal)))
        {
            AddPatch(type, binding);
        }
        return this;
    }

    public JAPatcher AddPatch(JAPatchBaseAttribute patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        AddEntry(patch.Method, patch, target: null);
        return this;
    }

    private void AddEntry(MethodInfo? method, JAPatchBaseAttribute attribute, object? target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (method != null)
            attribute.Method ??= method;
        _patches.Add((method, attribute, target));
        // Real JALib applies a patch immediately when it is added after Patch() ran
        // (features often register more patches from OnEnable, which runs after Patch()).
        if (patched)
            Register(method, attribute, target);
    }

    public void Patch()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (patched)
            return;
        patched = true;
        var generation = checked(++_generation);
        if (_registrations.Count == 0)
        {
            foreach (var (method, attr, target) in _patches)
                Register(method, attr, target, generation);
        }
        else
        {
            foreach (var registration in _registrations)
                registration.SetActive(generation, active: true);
            for (var index = _registrations.Count; index < _patches.Count; ++index)
            {
                var (method, attr, target) = _patches[index];
                Register(method, attr, target, generation);
            }
            BumpRegistryRevision();
        }
    }

    public void Unpatch()
    {
        if (!patched)
            return;
        patched = false;
        var generation = checked(++_generation);
        foreach (var registration in _registrations)
            registration.SetActive(generation, active: false);
        BumpRegistryRevision();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Unpatch();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void Register(
        MethodInfo? method,
        JAPatchBaseAttribute attr,
        object? target,
        long? generation = null)
    {
        var targetType = attr.ResolvedTargetType?.FullName ??
                         attr.ResolvedTargetTypeName ??
                         "<unknown>";
        var targetMethod = attr.ResolvedTargetMethodName ?? "<unknown>";
        var kind = attr switch
        {
            JAReversePatchAttribute reverse => $"ReversePatch",
            JAOverridePatchAttribute => "Override",
            // Only Replace/Transpiler bodies can be VersionSafe-style stand-in
            // redirections; a Prefix/Postfix whose callback happens to live in the
            // target's own type is still a normal patch and must keep its kind for
            // dispatcher binding.
            JAPatchAttribute patch when method != null &&
                patch.PatchType is PatchType.Replace or PatchType.Transpiler &&
                IsReversePatchStub(method, patch, targetType, targetMethod) => "ReversePatch",
            JAPatchAttribute patch => patch.PatchType.ToString(),
            _ => "Unknown"
        };
        var record = new RegisteredPatchRecord(
            _mod.GetType().FullName ?? _mod.GetType().Name,
            targetType,
            targetMethod,
            kind,
            method?.DeclaringType?.FullName ?? "<unknown>",
            method?.Name ?? "<unknown>",
            "registered_only",
            "native hook mapping is not implemented yet",
            method,
            target,
            generation ?? _generation,
            active: true,
            originalMethod: ResolveOriginalMethod(attr),
            mod: _mod,
            attribute: attr,
            registrationIndex: Interlocked.Increment(ref s_registrationIndex),
            patchId: attr.PatchId,
            priority: attr is JAPatchAttribute patchAttribute ? patchAttribute.Priority : -1,
            before: attr is JAPatchAttribute beforeAttribute ? beforeAttribute.Before : null,
            after: attr is JAPatchAttribute afterAttribute ? afterAttribute.After : null,
            debug: attr.Debug,
            tryingCatch: attr.TryingCatch);

        lock (RegistryLock)
        {
            RegisteredPatches.Add(record);
            _registrations.Add(record);
            ++s_registryRevision;
        }

        Console.WriteLine($"[PcModCompat][JALib] mod={record.ModType} patch={record.TargetType}.{record.TargetMethod} kind={record.Kind} callback={record.CallbackType}.{record.CallbackMethod} status={record.Status}");
    }

    private static bool IsReversePatchStub(MethodInfo method, JAPatchBaseAttribute attr, string targetType, string targetMethod)
    {
        if (method.DeclaringType == null)
            return false;

        if (!string.Equals(method.DeclaringType.FullName, targetType, StringComparison.Ordinal))
            return false;

        if (string.Equals(method.Name, targetMethod, StringComparison.Ordinal))
            return false;

        var target = method.DeclaringType.GetMethod(
            targetMethod,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

        return target != null;
    }

    private static MethodBase? ResolveOriginalMethod(JAPatchBaseAttribute attribute)
    {
        if (attribute.MethodBase != null)
            return attribute.MethodBase;
        var type = attribute.ResolvedTargetType;
        var name = attribute.ResolvedTargetMethodName;
        if (type == null || string.IsNullOrWhiteSpace(name))
            return null;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Static | BindingFlags.Instance;
        if (attribute.ArgumentTypesType is { Length: > 0 } argumentTypes &&
            argumentTypes.All(argument => argument != null))
        {
            return type.GetMethod(name, flags, binder: null, argumentTypes!, modifiers: null);
        }
        var candidates = type.GetMember(name, MemberTypes.Method | MemberTypes.Constructor, flags)
            .OfType<MethodBase>()
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private Type[] GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            _mod.Warning(
                $"Partial patch scan for assembly '{assembly.GetName().Name}': " +
                $"{exception.LoaderExceptions.Length} type load failures.");
            return exception.Types.OfType<Type>().ToArray();
        }
    }

    private static bool Matches(PatchBinding binding, JAPatchBaseAttribute attribute)
        => attribute switch
        {
            JAReversePatchAttribute => (binding & PatchBinding.Reverse) != 0,
            JAOverridePatchAttribute => (binding & PatchBinding.Override) != 0,
            JAPatchAttribute { PatchType: PatchType.Prefix } =>
                (binding & PatchBinding.Prefix) != 0,
            JAPatchAttribute { PatchType: PatchType.Postfix } =>
                (binding & PatchBinding.Postfix) != 0,
            JAPatchAttribute { PatchType: PatchType.Transpiler } =>
                (binding & PatchBinding.Transpiler) != 0,
            JAPatchAttribute { PatchType: PatchType.Finalizer } =>
                (binding & PatchBinding.Finalizer) != 0,
            JAPatchAttribute { PatchType: PatchType.Replace } =>
                (binding & PatchBinding.Replace) != 0,
            _ => false
        };

    internal void NotifyFailPatch(string patchId, bool disabled)
    {
        try
        {
            OnFailPatch?.Invoke(patchId, disabled);
        }
        catch
        {
            // Listener failures must not break registration.
        }
    }

    public static RegisteredPatchRecord[] SnapshotRegisteredPatches()
    {
        lock (RegistryLock)
            return RegisteredPatches.ToArray();
    }

    // Cheap liveness counter for the compat host: when features register patches late
    // (e.g. on level entry), the managed callback dispatcher rebuilds on count change.
    public static int RegisteredPatchCount
    {
        get
        {
            return Volatile.Read(ref s_registryRevision);
        }
    }

    public static void ClearRegisteredPatches()
    {
        lock (RegistryLock)
        {
            RegisteredPatches.Clear();
            ++s_registryRevision;
        }
    }

    private static void BumpRegistryRevision()
        => Interlocked.Increment(ref s_registryRevision);

    [Obsolete("Deprecated. Use JAPatchManager.GetPatchData instead.", true)]
    public static PatchData GetPatchData(MethodBase method)
        => JAPatchManager.GetPatchData(method);

    public static void Unpatch(MethodBase original, MethodInfo patch)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(patch);
        SetMatchingRecordsInactive(record =>
            Equals(record.OriginalMethod, original) &&
            Equals(record.CallbackMethodInfo, patch));
    }

    public static void Unpatch(MethodBase original, AllPatchType type, string id)
    {
        ArgumentNullException.ThrowIfNull(original);
        SetMatchingRecordsInactive(record =>
            Equals(record.OriginalMethod, original) &&
            (string.IsNullOrEmpty(id) || record.PatchId == id || record.ModType == id) &&
            Matches(type, record.Kind));
    }

    // v42 ships the zero-arg form, v44 the bool form; the ABI union needs both exact
    // signatures (a default parameter only emits the bool overload).
    public static void RunWaiterPatchForce()
        => RunWaiterPatchForce(setNull: false);

    public static void RunWaiterPatchForce(bool setNull)
    {
        // Registration is immediate in PcCompat; native HookBroker publication is asynchronous.
    }

    private static void SetMatchingRecordsInactive(Func<RegisteredPatchRecord, bool> predicate)
    {
        lock (RegistryLock)
        {
            var changed = false;
            foreach (var record in RegisteredPatches.Where(predicate))
            {
                record.SetActive(record.Generation + 1, active: false);
                changed = true;
            }
            if (changed)
                ++s_registryRevision;
        }
    }

    private static bool Matches(AllPatchType type, string kind)
        => kind switch
        {
            "Prefix" => (type & AllPatchType.AllPrefix) != 0,
            "Postfix" => (type & AllPatchType.AllPostfix) != 0,
            "Transpiler" => (type & AllPatchType.AllTranspiler) != 0,
            "Finalizer" => (type & AllPatchType.Finalizer) != 0,
            "Replace" => (type & AllPatchType.Replace) != 0,
            "Remove" => (type & AllPatchType.Remove) != 0,
            "ReversePatch" => (type & AllPatchType.Reverse) != 0,
            "Override" => (type & AllPatchType.Override) != 0,
            _ => false
        };
}

// CallbackMethodInfo/CallbackTarget keep the live invocation handles so the managed
// callback dispatcher (game event callbacks drained from native hooks) can invoke the
// MOD's own postfix code. String fields stay for the loader's plain descriptors.
public sealed class RegisteredPatchRecord
{
    private long _generation;
    private int _active;

    public RegisteredPatchRecord(
        string modType,
        string targetType,
        string targetMethod,
        string kind,
        string callbackType,
        string callbackMethod,
        string status,
        string reason,
        MethodInfo? callbackMethodInfo = null,
        object? callbackTarget = null,
        long generation = 0,
        bool active = false,
        MethodBase? originalMethod = null,
        JAMod? mod = null,
        JAPatchBaseAttribute? attribute = null,
        int registrationIndex = 0,
        string? patchId = null,
        int priority = -1,
        string[]? before = null,
        string[]? after = null,
        bool debug = false,
        bool tryingCatch = false)
    {
        ModType = modType;
        TargetType = targetType;
        TargetMethod = targetMethod;
        Kind = kind;
        CallbackType = callbackType;
        CallbackMethod = callbackMethod;
        Status = status;
        Reason = reason;
        CallbackMethodInfo = callbackMethodInfo;
        CallbackTarget = callbackTarget;
        OriginalMethod = originalMethod;
        Mod = mod;
        Attribute = attribute;
        RegistrationIndex = registrationIndex;
        PatchId = patchId ?? string.Empty;
        Priority = priority;
        Before = before ?? [];
        After = after ?? [];
        Debug = debug;
        TryingCatch = tryingCatch;
        _generation = generation;
        _active = active ? 1 : 0;
    }

    public string ModType { get; }
    public string TargetType { get; }
    public string TargetMethod { get; }
    public string Kind { get; }
    public string CallbackType { get; }
    public string CallbackMethod { get; }
    public string Status { get; }
    public string Reason { get; }
    public MethodInfo? CallbackMethodInfo { get; }
    public object? CallbackTarget { get; }
    public MethodBase? OriginalMethod { get; }
    public JAMod? Mod { get; }
    internal JAPatchBaseAttribute? Attribute { get; }
    public int RegistrationIndex { get; }
    public string PatchId { get; }
    public int Priority { get; }
    public string[] Before { get; }
    public string[] After { get; }
    public bool Debug { get; }
    public bool TryingCatch { get; }
    public long Generation => Volatile.Read(ref _generation);
    public bool Active => Volatile.Read(ref _active) != 0;

    internal void SetActive(long generation, bool active)
    {
        Volatile.Write(ref _generation, generation);
        Volatile.Write(ref _active, active ? 1 : 0);
    }

    internal HarmonyLib.Patch? CreatePatch()
        => CallbackMethodInfo == null
            ? null
            : new HarmonyLib.Patch(
                CallbackMethodInfo,
                RegistrationIndex,
                PatchId,
                Priority,
                Before,
                After,
                Debug);

    internal TriedPatchData? CreateTriedPatchData()
        => CallbackMethodInfo == null || Mod == null
            ? null
            : new TriedPatchData(
                CallbackMethodInfo,
                RegistrationIndex,
                PatchId,
                Priority,
                Before,
                After,
                Debug,
                Mod);

    internal ReversePatchData? CreateReversePatchData()
        => CallbackMethodInfo == null || OriginalMethod == null || Mod == null ||
           Attribute is not JAReversePatchAttribute reverse
            ? null
            : new ReversePatchData
            {
                Original = OriginalMethod,
                PatchMethod = CallbackMethodInfo,
                Debug = Debug,
                Attribute = reverse,
                Mod = Mod
            };

    internal OverridePatchData? CreateOverridePatchData()
        => CallbackMethodInfo == null || Mod == null ||
           Attribute is not JAOverridePatchAttribute @override
            ? null
            : new OverridePatchData(CallbackMethodInfo, @override, Mod);
}

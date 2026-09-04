using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StArray.ModManager.Runtime;
using StArray.ModManager.RuntimeAbstractions;

namespace StArray.ModManager.Android.Native;

/// <summary>
/// ModManager native hook P/Invoke 兼容封装。
/// Hook 安装直接进入 <c>core/hook_broker.cpp</c>；其余 Dobby 工具函数由
/// <c>core/dobby_hook.cpp</c> 导出。所有方法通过单一 native SO 调用。
/// </summary>
public static class Dobby
{
    private const string Lib = "starray_modmanager";
    private const uint HookLayerFlagManagedCallbackGate = 1u << 0;
    private static readonly object HookLock = new();
    private static readonly Dictionary<HookKey, HookRecord> InstalledHooks = new();
    private static readonly Dictionary<nint, HookRecord> LatestHooks = new();

    private readonly record struct HookKey(
        nint Target,
        nint Detour,
        string Owner,
        long Generation,
        bool ManagedCallbackGate);
    private sealed record HookRecord(
        nint Target,
        nint Detour,
        nint Origin,
        string Owner,
        long Generation,
        bool ManagedCallbackGate);

    // ========================================================================
    // Native externs
    // ========================================================================

    /// <summary>安装 inline hook。</summary>
    /// <param name="owner">用于诊断的 hook layer 所有者</param>
    /// <param name="address">目标函数地址</param>
    /// <param name="replace">替换函数地址（nint 指向 delegate 或函数指针）</param>
    /// <param name="origin">[out] 原函数指针（用于调用原逻辑）</param>
    /// <returns>0 = 成功，非 0 = 失败</returns>
    [DllImport(Lib, EntryPoint = "modmanager_hook_broker_install", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _Hook(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        nint address,
        nint replace,
        out nint origin);

    [DllImport(Lib, EntryPoint = "modmanager_hook_broker_install_generation", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _HookGeneration(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation,
        nint address,
        nint replace,
        out nint origin);

    [DllImport(Lib, EntryPoint = "modmanager_hook_broker_install_generation_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _HookGenerationV2(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation,
        nint address,
        nint replace,
        uint layerFlags,
        out nint origin);

    [DllImport(Lib, EntryPoint = "modmanager_hook_broker_install_compatible", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _HookCompatible(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        nint address,
        nint replace,
        RuntimeMethodCompatibilityKind compatibilityKind,
        out nint origin);

    [DllImport(Lib, EntryPoint = "modmanager_hook_broker_install_compatible_generation", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _HookCompatibleGeneration(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation,
        nint address,
        nint replace,
        RuntimeMethodCompatibilityKind compatibilityKind,
        out nint origin);

    [DllImport(Lib, EntryPoint = "modmanager_hook_broker_install_compatible_generation_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _HookCompatibleGenerationV2(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation,
        nint address,
        nint replace,
        RuntimeMethodCompatibilityKind compatibilityKind,
        uint layerFlags,
        out nint origin);

    [DllImport(Lib, EntryPoint = "modmanager_hook_broker_supports_owner_control", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _SupportsOwnerControl();

    [DllImport(Lib, EntryPoint = "modmanager_hook_broker_set_owner_enabled", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _SetOwnerEnabled(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        int enabled);

    [DllImport(Lib, EntryPoint = "modmanager_hook_broker_set_owner_generation_enabled", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _SetOwnerGenerationEnabled(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation,
        int enabled);

    [DllImport(Lib, EntryPoint = "modmanager_dobby_set_instrument_generation_enabled", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _SetInstrumentGenerationEnabled(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation,
        int enabled);

    [DllImport(Lib, EntryPoint = "modmanager_dobby_set_code_patch_generation_enabled", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _SetCodePatchGenerationEnabled(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation,
        int enabled);

    [DllImport(Lib, EntryPoint = "modmanager_hook_broker_retire_owner_target", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _RetireOwnerTarget(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        nint target);

    [DllImport(Lib, EntryPoint = "modmanager_hook_broker_retire_owner_generation_target", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _RetireOwnerGenerationTarget(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation,
        nint target);

    [DllImport(Lib, EntryPoint = "modmanager_dobby_retire_instrument_generation_target", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _RetireInstrumentGenerationTarget(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation,
        nint target);

    [DllImport(Lib, EntryPoint = "modmanager_dobby_retire_code_patch_generation_target", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _RetireCodePatchGenerationTarget(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation,
        nint target);

    [DllImport(Lib, EntryPoint = "modmanager_hook_broker_retire_owner", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _RetireOwner(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner);

    [DllImport(Lib, EntryPoint = "modmanager_hook_broker_retire_owner_generation", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _RetireOwnerGeneration(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation);

    [DllImport(Lib, EntryPoint = "modmanager_dobby_retire_instrument_generation", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _RetireInstrumentGeneration(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation);

    [DllImport(Lib, EntryPoint = "modmanager_dobby_retire_code_patch_generation", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _RetireCodePatchGeneration(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation);

    [DllImport(Lib, EntryPoint = "modmanager_hook_broker_get_owner_retained_layer_count", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _GetOwnerRetainedLayerCount(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner);

    [DllImport(Lib, EntryPoint = "modmanager_hook_broker_get_owner_generation_retained_layer_count", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _GetOwnerGenerationRetainedLayerCount(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation);

    [DllImport(Lib, EntryPoint = "modmanager_hook_broker_get_owner_generation_untracked_callback_layer_count", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _GetOwnerGenerationUntrackedCallbackLayerCount(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation);

    [DllImport(Lib, EntryPoint = "modmanager_hook_broker_get_layer_count", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _GetLayerCount(nint target);

    [DllImport(Lib, EntryPoint = "modmanager_dobby_get_instrument_generation_retained_count", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _GetInstrumentGenerationRetainedCount(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation);

    [DllImport(Lib, EntryPoint = "modmanager_dobby_get_code_patch_generation_retained_count", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _GetCodePatchGenerationRetainedCount(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation);

    [DllImport(Lib, EntryPoint = "modmanager_native_operation_begin_v1", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _BeginNativeOperation(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        out ModNativeOperationToken token);

    [DllImport(Lib, EntryPoint = "modmanager_native_operation_is_cancellation_requested_v1", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _GetNativeOperationCancellationState(
        in ModNativeOperationToken token);

    [DllImport(Lib, EntryPoint = "modmanager_native_operation_end_v1", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _EndNativeOperation(
        in ModNativeOperationToken token);

    [DllImport(Lib, EntryPoint = "modmanager_native_operation_host_open_generation", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _OpenNativeOperationGeneration(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation);

    [DllImport(Lib, EntryPoint = "modmanager_native_operation_host_cancel_generation_and_wait", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _CancelNativeOperationGenerationAndWait(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation,
        uint timeoutMilliseconds);

    [DllImport(Lib, EntryPoint = "modmanager_native_operation_host_resume_generation", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _ResumeNativeOperationGeneration(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation);

    [DllImport(Lib, EntryPoint = "modmanager_native_operation_host_retire_generation", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _RetireNativeOperationGeneration(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation);

    [DllImport(Lib, EntryPoint = "modmanager_native_operation_host_get_active_count", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _GetActiveNativeOperationCount(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation);

    /// <summary>安装 inline hook。</summary>
    /// <param name="address">目标函数地址</param>
    /// <param name="replace">替换函数地址（nint 指向 delegate 或函数指针）</param>
    /// <param name="origin">[out] 原函数指针（用于调用原逻辑）</param>
    /// <returns>0 = 成功，非 0 = 失败</returns>
    public static int Hook(nint address, nint replace, out nint origin)
        => Hook(address, replace, out origin,
            HookHelper.CurrentOwnerId ?? "host:Dobby.Hook");

    /// <summary>
    /// 安装 inline hook，并记录安装方。
    /// 同一地址重复安装同一 detour 会返回已保存的 continuation；同一地址的不同 detour 由 native HookBroker 追加为新 layer。
    /// </summary>
    /// <param name="address">目标函数地址</param>
    /// <param name="replace">替换函数地址</param>
    /// <param name="origin">当前 layer 的 continuation</param>
    /// <param name="owner">用于诊断的 hook layer 所有者</param>
    /// <returns>0 = 成功，非 0 = 失败</returns>
    public static int Hook(nint address, nint replace, out nint origin, string? owner)
        => HookCore(
            address,
            replace,
            out origin,
            owner,
            managedCallbackGate: false);

    internal static int HookWithManagedCallbackGate(
        nint address,
        nint replace,
        out nint origin,
        string owner)
        => HookCore(
            address,
            replace,
            out origin,
            owner,
            managedCallbackGate: true);

    private static int HookCore(
        nint address,
        nint replace,
        out nint origin,
        string? owner,
        bool managedCallbackGate)
    {
        origin = nint.Zero;
        if (address == nint.Zero || replace == nint.Zero)
            return -1;

        lock (HookLock)
        {
            // A MOD cannot claim another MOD's or a host component's hook namespace by
            // passing an arbitrary owner string. Host calls outside a MOD scope retain
            // their explicit diagnostic owner.
            var scopedOwner = HookHelper.CurrentOwnerId;
            var normalizedOwner = !string.IsNullOrWhiteSpace(scopedOwner)
                ? scopedOwner
                : string.IsNullOrWhiteSpace(owner) ? "host:unknown" : owner;
            var generation = ResolveCurrentGeneration(normalizedOwner);
            var key = new HookKey(
                address,
                replace,
                normalizedOwner,
                generation,
                managedCallbackGate);
            // Dobby detours cannot be physically removed safely while chained. Reserve
            // the owner before every install/reuse so a concurrent unload cannot reactivate
            // a cached layer or release its ALC.
            var ownerWasRegistered =
                HookHelper.IsProcessLifetimeHookOwnerRegistered(normalizedOwner);
            if (!HookHelper.RegisterProcessLifetimeHookOwner(normalizedOwner))
                return -2;
            // Native HookBroker owns idempotence. Re-entering it is required because a
            // retained layer may currently be disabled and must be explicitly re-enabled.
            try
            {
                var result = generation > 0
                    ? managedCallbackGate
                        ? _HookGenerationV2(
                            normalizedOwner,
                            checked((ulong)generation),
                            address,
                            replace,
                            HookLayerFlagManagedCallbackGate,
                            out origin)
                        : _HookGeneration(
                            normalizedOwner,
                            checked((ulong)generation),
                            address,
                            replace,
                            out origin)
                    : _Hook(normalizedOwner, address, replace, out origin);
                if ((result != 0 || origin == nint.Zero) && !ownerWasRegistered)
                    HookHelper.RollbackProcessLifetimeHookOwner(normalizedOwner, true);
                if (result == 0 && origin != nint.Zero)
                {
                    var record = new HookRecord(
                        address,
                        replace,
                        origin,
                        normalizedOwner,
                        generation,
                        managedCallbackGate);
                    InstalledHooks[key] = record;
                    LatestHooks[address] = record;
                }
                return result;
            }
            catch
            {
                if (!ownerWasRegistered)
                    HookHelper.RollbackProcessLifetimeHookOwner(normalizedOwner, true);
                throw;
            }
        }
    }

    public static bool SupportsCompatibility(RuntimeMethodCompatibilityKind kind)
        => kind == RuntimeMethodCompatibilityKind.CalculateTickColorWithoutHitFloor;

    public static int HookCompatible(
        nint address,
        nint replace,
        out nint origin,
        RuntimeMethodCompatibilityKind compatibilityKind,
        string owner)
        => HookCompatibleCore(
            address,
            replace,
            out origin,
            compatibilityKind,
            owner,
            managedCallbackGate: false);

    internal static int HookCompatibleWithManagedCallbackGate(
        nint address,
        nint replace,
        out nint origin,
        RuntimeMethodCompatibilityKind compatibilityKind,
        string owner)
        => HookCompatibleCore(
            address,
            replace,
            out origin,
            compatibilityKind,
            owner,
            managedCallbackGate: true);

    private static int HookCompatibleCore(
        nint address,
        nint replace,
        out nint origin,
        RuntimeMethodCompatibilityKind compatibilityKind,
        string owner,
        bool managedCallbackGate)
    {
        origin = nint.Zero;
        if (address == nint.Zero || replace == nint.Zero || !SupportsCompatibility(compatibilityKind))
            return -1;

        lock (HookLock)
        {
            var scopedOwner = HookHelper.CurrentOwnerId;
            var normalizedOwner = !string.IsNullOrWhiteSpace(scopedOwner)
                ? scopedOwner
                : string.IsNullOrWhiteSpace(owner) ? "host:unknown" : owner;
            var generation = ResolveCurrentGeneration(normalizedOwner);
            var key = new HookKey(
                address,
                replace,
                normalizedOwner,
                generation,
                managedCallbackGate);
            var ownerWasRegistered =
                HookHelper.IsProcessLifetimeHookOwnerRegistered(normalizedOwner);
            if (!HookHelper.RegisterProcessLifetimeHookOwner(normalizedOwner))
                return -2;
            try
            {
                var result = generation > 0
                    ? managedCallbackGate
                        ? _HookCompatibleGenerationV2(
                            normalizedOwner,
                            checked((ulong)generation),
                            address,
                            replace,
                            compatibilityKind,
                            HookLayerFlagManagedCallbackGate,
                            out origin)
                        : _HookCompatibleGeneration(
                            normalizedOwner,
                            checked((ulong)generation),
                            address,
                            replace,
                            compatibilityKind,
                            out origin)
                    : _HookCompatible(
                        normalizedOwner,
                        address,
                        replace,
                        compatibilityKind,
                        out origin);
                if ((result != 0 || origin == nint.Zero) && !ownerWasRegistered)
                    HookHelper.RollbackProcessLifetimeHookOwner(normalizedOwner, true);
                if (result == 0 && origin != nint.Zero)
                {
                    var record = new HookRecord(
                        address,
                        replace,
                        origin,
                        normalizedOwner,
                        generation,
                        managedCallbackGate);
                    InstalledHooks[key] = record;
                    LatestHooks[address] = record;
                }
                return result;
            }
            catch
            {
                if (!ownerWasRegistered)
                    HookHelper.RollbackProcessLifetimeHookOwner(normalizedOwner, true);
                throw;
            }
        }
    }

    public static bool SupportsOwnerControl
    {
        get
        {
            try
            {
                return _SupportsOwnerControl() != 0;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    public static bool SetOwnerEnabled(string owner, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(owner) || !SupportsOwnerControl)
            return false;
        var runtimeKey = HookHelper.CurrentRuntimeKey;
        if (runtimeKey.IsValid)
            return string.Equals(runtimeKey.OwnerId, owner, StringComparison.Ordinal) &&
                   SetOwnerGenerationEnabled(owner, runtimeKey.Generation, enabled);
        return _SetOwnerEnabled(owner, enabled ? 1 : 0) >= 0;
    }

    internal static bool SetOwnerGenerationEnabled(
        string owner,
        long generation,
        bool enabled)
    {
        if (string.IsNullOrWhiteSpace(owner) || generation <= 0 || !SupportsOwnerControl)
            return false;
        var rawGeneration = checked((ulong)generation);
        var next = enabled ? 1 : 0;
        var previous = enabled ? 0 : 1;
        var hooks = _SetOwnerGenerationEnabled(
            owner, rawGeneration, next);
        if (hooks < 0)
            return false;
        var instruments = _SetInstrumentGenerationEnabled(
            owner, rawGeneration, next);
        if (instruments < 0)
        {
            if (hooks > 0)
                _SetOwnerGenerationEnabled(owner, rawGeneration, previous);
            return false;
        }
        var codePatches = _SetCodePatchGenerationEnabled(
            owner, rawGeneration, next);
        if (codePatches >= 0)
            return true;

        if (instruments > 0)
            _SetInstrumentGenerationEnabled(owner, rawGeneration, previous);
        if (hooks > 0)
            _SetOwnerGenerationEnabled(owner, rawGeneration, previous);
        return false;
    }

    public static bool RetireOwnerTarget(string owner, nint target)
    {
        if (string.IsNullOrWhiteSpace(owner) || target == nint.Zero || !SupportsOwnerControl)
            return false;
        var runtimeKey = HookHelper.CurrentRuntimeKey;
        if (runtimeKey.IsValid)
            return string.Equals(runtimeKey.OwnerId, owner, StringComparison.Ordinal) &&
                   RetireOwnerGenerationTarget(owner, runtimeKey.Generation, target);

        var retired = _RetireOwnerTarget(owner, target);
        if (retired <= 0)
            return false;

        lock (HookLock)
        {
            foreach (var key in InstalledHooks.Keys.Where(key => key.Target == target).ToArray())
            {
                if (InstalledHooks.TryGetValue(key, out var record) &&
                    string.Equals(record.Owner, owner, StringComparison.Ordinal))
                    InstalledHooks.Remove(key);
            }
            if (LatestHooks.TryGetValue(target, out var latest) &&
                string.Equals(latest.Owner, owner, StringComparison.Ordinal))
                LatestHooks.Remove(target);
        }
        return true;
    }

    internal static bool RetireOwnerGenerationTarget(
        string owner,
        long generation,
        nint target)
    {
        if (string.IsNullOrWhiteSpace(owner) || generation <= 0 ||
            target == nint.Zero || !SupportsOwnerControl)
            return false;

        var rawGeneration = checked((ulong)generation);
        var retiredHooks = _RetireOwnerGenerationTarget(
            owner, rawGeneration, target);
        var retiredInstruments = _RetireInstrumentGenerationTarget(
            owner, rawGeneration, target);
        var retiredCodePatches = _RetireCodePatchGenerationTarget(
            owner, rawGeneration, target);
        if (retiredHooks < 0 || retiredInstruments < 0 || retiredCodePatches < 0 ||
            retiredHooks + retiredInstruments + retiredCodePatches <= 0)
            return false;

        lock (HookLock)
        {
            foreach (var key in InstalledHooks.Keys.Where(key =>
                         key.Target == target && key.Generation == generation).ToArray())
            {
                if (InstalledHooks.TryGetValue(key, out var record) &&
                    string.Equals(record.Owner, owner, StringComparison.Ordinal))
                    InstalledHooks.Remove(key);
            }
            if (LatestHooks.TryGetValue(target, out var latest) &&
                latest.Generation == generation &&
                string.Equals(latest.Owner, owner, StringComparison.Ordinal))
                LatestHooks.Remove(target);
        }
        return true;
    }

    public static int RetireOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner) || !SupportsOwnerControl)
            return 0;
        var runtimeKey = HookHelper.CurrentRuntimeKey;
        if (runtimeKey.IsValid)
            return string.Equals(runtimeKey.OwnerId, owner, StringComparison.Ordinal)
                ? RetireOwnerGeneration(owner, runtimeKey.Generation)
                : 0;

        var retired = Math.Max(0, _RetireOwner(owner));
        if (retired == 0)
            return 0;

        lock (HookLock)
        {
            foreach (var pair in InstalledHooks
                         .Where(pair => string.Equals(pair.Value.Owner, owner, StringComparison.Ordinal))
                         .ToArray())
                InstalledHooks.Remove(pair.Key);
            foreach (var pair in LatestHooks
                         .Where(pair => string.Equals(pair.Value.Owner, owner, StringComparison.Ordinal))
                         .ToArray())
                LatestHooks.Remove(pair.Key);
        }
        return retired;
    }

    internal static int RetireOwnerGeneration(string owner, long generation)
    {
        if (string.IsNullOrWhiteSpace(owner) || generation <= 0 || !SupportsOwnerControl)
            return 0;

        var rawGeneration = checked((ulong)generation);
        var retiredHooks = _RetireOwnerGeneration(owner, rawGeneration);
        var retiredInstruments = _RetireInstrumentGeneration(owner, rawGeneration);
        var retiredCodePatches = _RetireCodePatchGeneration(owner, rawGeneration);
        if (retiredHooks < 0 || retiredInstruments < 0 || retiredCodePatches < 0)
            return 0;
        var retired = retiredHooks + retiredInstruments + retiredCodePatches;
        if (retired == 0)
            return 0;

        lock (HookLock)
        {
            foreach (var pair in InstalledHooks.Where(pair =>
                         pair.Value.Generation == generation &&
                         string.Equals(pair.Value.Owner, owner, StringComparison.Ordinal)).ToArray())
                InstalledHooks.Remove(pair.Key);
            foreach (var pair in LatestHooks.Where(pair =>
                         pair.Value.Generation == generation &&
                         string.Equals(pair.Value.Owner, owner, StringComparison.Ordinal)).ToArray())
                LatestHooks.Remove(pair.Key);
        }
        return retired;
    }

    public static int GetOwnerRetainedLayerCount(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner) || !SupportsOwnerControl)
            return 0;
        var runtimeKey = HookHelper.CurrentRuntimeKey;
        if (runtimeKey.IsValid)
            return string.Equals(runtimeKey.OwnerId, owner, StringComparison.Ordinal)
                ? GetOwnerGenerationRetainedLayerCount(owner, runtimeKey.Generation)
                : 0;
        return Math.Max(0, _GetOwnerRetainedLayerCount(owner));
    }

    internal static int GetOwnerGenerationRetainedLayerCount(
        string owner,
        long generation)
    {
        if (string.IsNullOrWhiteSpace(owner) || generation <= 0 || !SupportsOwnerControl)
            return 0;
        var rawGeneration = checked((ulong)generation);
        return Math.Max(0, _GetOwnerGenerationRetainedLayerCount(
                   owner, rawGeneration)) +
               Math.Max(0, _GetInstrumentGenerationRetainedCount(
                   owner, rawGeneration)) +
               Math.Max(0, _GetCodePatchGenerationRetainedCount(
                   owner, rawGeneration));
    }

    internal static int GetUntrackedCallbackLayerCount(
        string owner,
        long generation)
    {
        if (string.IsNullOrWhiteSpace(owner) || generation <= 0 || !SupportsOwnerControl)
            return 0;
        try
        {
            var count = _GetOwnerGenerationUntrackedCallbackLayerCount(
                owner,
                checked((ulong)generation));
            return count >= 0
                ? count
                : GetOwnerGenerationRetainedLayerCount(owner, generation);
        }
        catch (EntryPointNotFoundException)
        {
            // Older native providers cannot prove that a retained layer has a managed gate.
            return GetOwnerGenerationRetainedLayerCount(owner, generation);
        }
    }

    internal static bool OpenNativeOperationGeneration(string owner, long generation)
        => !string.IsNullOrWhiteSpace(owner) && generation > 0 &&
           _OpenNativeOperationGeneration(owner, checked((ulong)generation)) != 0;

    internal static bool TryBeginNativeOperation(
        string owner,
        long generation,
        string name,
        out ModNativeOperationToken token)
    {
        token = default;
        return !string.IsNullOrWhiteSpace(owner) && generation > 0 &&
               !string.IsNullOrWhiteSpace(name) &&
               _BeginNativeOperation(
                   owner,
                   checked((ulong)generation),
                   name,
                   out token) != 0 &&
               token.IsValid;
    }

    internal static int GetNativeOperationCancellationState(
        in ModNativeOperationToken token)
        => token.IsValid ? _GetNativeOperationCancellationState(token) : -1;

    internal static bool EndNativeOperation(in ModNativeOperationToken token)
        => token.IsValid && _EndNativeOperation(token) != 0;

    internal static bool CancelNativeOperationGenerationAndWait(
        string owner,
        long generation,
        uint timeoutMilliseconds)
        => !string.IsNullOrWhiteSpace(owner) && generation > 0 &&
           _CancelNativeOperationGenerationAndWait(
               owner,
               checked((ulong)generation),
               timeoutMilliseconds) != 0;

    internal static bool ResumeNativeOperationGeneration(string owner, long generation)
        => !string.IsNullOrWhiteSpace(owner) && generation > 0 &&
           _ResumeNativeOperationGeneration(owner, checked((ulong)generation)) != 0;

    internal static bool RetireNativeOperationGeneration(string owner, long generation)
        => !string.IsNullOrWhiteSpace(owner) && generation > 0 &&
           _RetireNativeOperationGeneration(owner, checked((ulong)generation)) != 0;

    internal static int GetActiveNativeOperationCount(string owner, long generation)
        => string.IsNullOrWhiteSpace(owner) || generation <= 0
            ? -1
            : _GetActiveNativeOperationCount(owner, checked((ulong)generation));

    private static long ResolveCurrentGeneration(string owner)
    {
        var runtimeKey = HookHelper.CurrentRuntimeKey;
        return runtimeKey.IsValid &&
               string.Equals(runtimeKey.OwnerId, owner, StringComparison.Ordinal)
            ? runtimeKey.Generation
            : 0;
    }

    public static bool TryGetInstalledHook(nint address, out string owner, out nint detour, out nint origin)
    {
        lock (HookLock)
        {
            if (LatestHooks.TryGetValue(address, out var existing))
            {
                owner = existing.Owner;
                detour = existing.Detour;
                origin = existing.Origin;
                return true;
            }
        }

        owner = string.Empty;
        detour = nint.Zero;
        origin = nint.Zero;
        return false;
    }

    /// <summary>获取指定物理目标当前保留的 HookBroker 层数。</summary>
    public static int GetLayerCount(nint address)
        => address == nint.Zero ? 0 : Math.Max(0, _GetLayerCount(address));

    /// <summary>
    /// 安装 inline hook — 传入 C# Reflection MethodInfo。
    /// 方法会被 PrepareMethod 强制 JIT 编译，再取其函数指针作为 replace。
    /// </summary>
    /// <param name="address">目标函数地址</param>
    /// <param name="replaceMethod">C# MethodInfo（需为静态方法）</param>
    /// <param name="origin">[out] 原函数指针</param>
    /// <returns>0 = 成功，非 0 = 失败</returns>
    public static int Hook(nint address, MethodInfo replaceMethod, out nint origin)
    {
        if (replaceMethod == null)
        {
            origin = IntPtr.Zero;
            return -1;
        }
        // 强制 JIT 编译该方法
        System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(replaceMethod.MethodHandle);
        nint replacePtr = replaceMethod.MethodHandle.GetFunctionPointer();
        var owner = replaceMethod.DeclaringType == null
            ? replaceMethod.Name
            : replaceMethod.DeclaringType.FullName + "." + replaceMethod.Name;
        return Hook(address, replacePtr, out origin, owner);
    }

    /// <summary>安装动态指令插桩。</summary>
    /// <param name="address">目标函数地址</param>
    /// <param name="preHandler">前置回调函数指针（dobby_instrument_callback_t 签名）</param>
    /// <returns>0 = 成功</returns>
    [DllImport(Lib, EntryPoint = "modmanager_dobby_instrument")]
    private static extern int _Instrument(nint address, nint preHandler);

    [DllImport(Lib, EntryPoint = "modmanager_dobby_instrument_generation", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _InstrumentGeneration(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation,
        nint address,
        nint preHandler);

    public static int Instrument(nint address, nint preHandler)
    {
        if (address == nint.Zero || preHandler == nint.Zero)
            return -1;
        var owner = HookHelper.CurrentOwnerId ?? "host:Dobby.Instrument";
        var runtimeKey = HookHelper.CurrentRuntimeKey;
        var generation = runtimeKey.IsValid &&
                         string.Equals(runtimeKey.OwnerId, owner, StringComparison.Ordinal)
            ? runtimeKey.Generation
            : 0;
        var ownerWasRegistered =
            HookHelper.IsProcessLifetimeHookOwnerRegistered(owner);
        if (!HookHelper.RegisterProcessLifetimeHookOwner(owner))
            return -2;
        try
        {
            var result = generation > 0
                ? _InstrumentGeneration(
                    owner,
                    checked((ulong)generation),
                    address,
                    preHandler)
                : _Instrument(address, preHandler);
            if (result == 0 && runtimeKey.IsValid &&
                !ModOwnedResourceRegistry.TryRegister(
                    runtimeKey,
                    ModOwnedResourceKind.Hook,
                    $"instrument=0x{address.ToInt64():X};handler=0x{preHandler.ToInt64():X}",
                    ModOwnedResourceRetirementPolicy.RetainWhileSuspended))
            {
                _RetireInstrumentGenerationTarget(
                    owner, checked((ulong)generation), address);
                result = -5;
            }
            if (result != 0 && !ownerWasRegistered)
                HookHelper.RollbackProcessLifetimeHookOwner(owner, true);
            return result;
        }
        catch
        {
            if (!ownerWasRegistered)
                HookHelper.RollbackProcessLifetimeHookOwner(owner, true);
            throw;
        }
    }

    /// <summary>移除 hook 并恢复原函数。</summary>
    /// <param name="address">被 hook 的函数地址</param>
    /// <returns>0 = 成功</returns>
    [DllImport(Lib, EntryPoint = "modmanager_dobby_destroy")]
    private static extern int _Destroy(nint address);

    /// <summary>移除 hook 并恢复原函数。</summary>
    /// <param name="address">被 hook 的函数地址</param>
    /// <returns>0 = 成功</returns>
    public static int Destroy(nint address)
    {
        var owner = HookHelper.CurrentOwnerId;
        if (!string.IsNullOrWhiteSpace(owner) && SupportsOwnerControl)
            return RetireOwnerTarget(owner, address) ? 0 : -1;

        // Physically removing the stable gateway would sever every owner layer.
        if (SupportsOwnerControl)
            return -2;

        var result = _Destroy(address);
        if (result == 0)
        {
            lock (HookLock)
            {
                foreach (var key in InstalledHooks.Keys.Where(key => key.Target == address).ToArray())
                    InstalledHooks.Remove(key);
                LatestHooks.Remove(address);
            }
        }

        return result;
    }

    /// <summary>按动态库名和符号名解析函数地址。</summary>
    /// <param name="imageName">动态库名，如 "libil2cpp.so"</param>
    /// <param name="symbolName">符号名</param>
    /// <returns>符号地址，失败返回 nint.Zero</returns>
    [DllImport(Lib, EntryPoint = "modmanager_dobby_symbol_resolver")]
    private static extern nint _SymbolResolverLegacy(string imageName, string symbolName);

    [DllImport(Lib, EntryPoint = "modmanager_dobby_symbol_resolver_generation", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint _SymbolResolverGeneration(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string imageName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string symbolName);

    public static nint SymbolResolver(string imageName, string symbolName)
    {
        if (string.IsNullOrWhiteSpace(imageName) || string.IsNullOrWhiteSpace(symbolName))
            return nint.Zero;
        var owner = HookHelper.CurrentOwnerId;
        var runtimeKey = HookHelper.CurrentRuntimeKey;
        var generation = runtimeKey.IsValid && !string.IsNullOrWhiteSpace(owner) &&
                         string.Equals(runtimeKey.OwnerId, owner, StringComparison.Ordinal)
            ? runtimeKey.Generation
            : 0;
        var address = generation > 0
            ? _SymbolResolverGeneration(
                owner!,
                checked((ulong)generation),
                imageName,
                symbolName)
            : _SymbolResolverLegacy(imageName, symbolName);
        if (address != nint.Zero && runtimeKey.IsValid &&
            !ModOwnedResourceRegistry.TryRegister(
                runtimeKey,
                ModOwnedResourceKind.Symbol,
                $"symbol={imageName}!{symbolName};address=0x{address.ToInt64():X}",
                ModOwnedResourceRetirementPolicy.ObserveOnly))
            return nint.Zero;
        return address;
    }

    /// <summary>上游公开名称；转发到同一 HookBroker 侧 resolver。</summary>
    public static nint _SymbolResolver(string imageName, string symbolName) =>
        SymbolResolver(imageName, symbolName);

    /// <summary>内存代码补丁。</summary>
    /// <param name="address">目标地址</param>
    /// <param name="buffer">补丁数据</param>
    /// <param name="bufferSize">补丁数据大小</param>
    /// <returns>0 = 成功</returns>
    [DllImport(Lib, EntryPoint = "modmanager_dobby_code_patch")]
    private static extern int _CodePatch(nint address, byte[] buffer, uint bufferSize);

    [DllImport(Lib, EntryPoint = "modmanager_dobby_code_patch_generation", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _CodePatchGeneration(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        ulong generation,
        nint address,
        byte[] buffer,
        uint bufferSize);

    public static int CodePatch(nint address, byte[] buffer, uint bufferSize)
    {
        if (address == nint.Zero || buffer == null || bufferSize == 0 ||
            bufferSize > buffer.Length)
            return -1;

        var owner = HookHelper.CurrentOwnerId ?? "host:Dobby.CodePatch";
        var runtimeKey = HookHelper.CurrentRuntimeKey;
        var generation = runtimeKey.IsValid &&
                         string.Equals(runtimeKey.OwnerId, owner, StringComparison.Ordinal)
            ? runtimeKey.Generation
            : 0;
        var ownerWasRegistered =
            HookHelper.IsProcessLifetimeHookOwnerRegistered(owner);
        if (!HookHelper.RegisterProcessLifetimeHookOwner(owner))
            return -2;

        try
        {
            var result = generation > 0
                ? _CodePatchGeneration(
                    owner,
                    checked((ulong)generation),
                    address,
                    buffer,
                    bufferSize)
                : _CodePatch(address, buffer, bufferSize);
            if (result == 0 && runtimeKey.IsValid &&
                !ModOwnedResourceRegistry.TryRegister(
                    runtimeKey,
                    ModOwnedResourceKind.CodePatch,
                    $"code-patch=0x{address.ToInt64():X};size={bufferSize}",
                    ModOwnedResourceRetirementPolicy.RetainWhileSuspended))
            {
                _RetireCodePatchGenerationTarget(
                    owner, checked((ulong)generation), address);
                result = -5;
            }
            if (result != 0 && !ownerWasRegistered)
                HookHelper.RollbackProcessLifetimeHookOwner(owner, true);
            return result;
        }
        catch
        {
            if (!ownerWasRegistered)
                HookHelper.RollbackProcessLifetimeHookOwner(owner, true);
            throw;
        }
    }

    /// <summary>获取 Dobby 版本字符串。</summary>
    [DllImport(Lib, EntryPoint = "modmanager_dobby_get_version")]
    private static extern nint _GetVersionRaw();

    /// <summary>获取 Dobby 版本字符串。</summary>
    public static string GetVersion()
    {
        var ptr = _GetVersionRaw();
        return Marshal.PtrToStringAnsi(ptr) ?? "unknown";
    }
}

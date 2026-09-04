using System.Runtime.InteropServices;
using StArray.ModManager.RuntimeAbstractions;

namespace StArray.ModManager.Runtime;

/// <summary>
/// 原生方法 Hook 抽象 —— 支持安装/卸载 inline hook 及获取库导出函数
/// </summary>
public interface IHook
{
    /// <summary>Whether installed hooks can be removed safely during the current process.</summary>
    bool SupportsRuntimeUnhook => true;

    /// <summary>安装 hook，将 target 重定向到 detour，返回原始函数指针</summary>
    nint Hook(nint target, nint detour);

    /// <summary>卸载 target 上的 hook</summary>
    bool Unhook(nint target);

    /// <summary>从指定库获取导出函数地址</summary>
    nint GetFunction(string library, string name);

    /// <summary>从指定库计算 RVA（相对虚拟地址）对应的绝对地址</summary>
    nint GetFunctionRVA(string library, long rva)
    {
        return nint.Zero;
    }
}

/// <summary>
/// Optional lifecycle control for platforms whose physical detours remain installed
/// for the process lifetime. Operations apply only to layers owned by one MOD.
/// </summary>
public interface IOwnerScopedHook : IHook
{
    /// <summary>Whether the provider can bypass and retire owner layers independently.</summary>
    bool SupportsOwnerControl { get; }

    /// <summary>Temporarily enables or disables every non-retired layer for an owner.</summary>
    bool SetOwnerEnabled(string owner, bool enabled);

    /// <summary>Permanently retires the owner's layer(s) for one target.</summary>
    bool RetireOwnerTarget(string owner, nint target);

    /// <summary>Permanently retires every layer belonging to an owner.</summary>
    int RetireOwner(string owner);

    /// <summary>Returns the number of non-retired process-lifetime layers.</summary>
    int GetRetainedLayerCount(string owner);
}

/// <summary>
/// Optional lifecycle control that binds permanent native hook layers to one load generation.
/// Owner-only methods remain available for host and legacy callers.
/// </summary>
public interface IGenerationScopedHook : IOwnerScopedHook
{
    bool SetOwnerGenerationEnabled(string owner, long generation, bool enabled);
    bool RetireOwnerGenerationTarget(string owner, long generation, nint target);
    int RetireOwnerGeneration(string owner, long generation);
    int GetRetainedLayerCount(string owner, long generation);
}

/// <summary>Optional provider for explicit legacy-to-current native ABI adapters.</summary>
public interface IRuntimeMethodCompatibilityHook
{
    bool SupportsCompatibility(RuntimeMethodCompatibilityKind kind);

    nint HookCompatible(
        nint target,
        nint detour,
        RuntimeMethodCompatibilityKind kind);
}

/// <summary>
/// Optional provider contract for detours whose managed wrapper already participates in
/// <see cref="ModRuntimeSession"/> callback quiescence. Providers use this marker to avoid
/// treating source-generated hooks as untracked arbitrary-ABI callbacks.
/// </summary>
public interface IManagedCallbackGateAwareHook
{
    nint HookWithManagedCallbackGate(nint target, nint detour);

    nint HookCompatibleWithManagedCallbackGate(
        nint target,
        nint detour,
        RuntimeMethodCompatibilityKind kind);

    int GetUntrackedCallbackLayerCount(string owner, long generation);
}

/// <summary>
/// Opaque token shared with a private native worker. The cookie prevents a stale operation
/// from completing a slot reused by another MOD generation.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ModNativeOperationToken
{
    public uint AbiVersion;
    public uint Slot;
    public ulong OperationId;
    public ulong Cookie;

    public ModNativeOperationToken(
        uint abiVersion,
        uint slot,
        ulong operationId,
        ulong cookie)
    {
        AbiVersion = abiVersion;
        Slot = slot;
        OperationId = operationId;
        Cookie = cookie;
    }

    public readonly bool IsValid => AbiVersion == 1 && OperationId != 0 && Cookie != 0;
}

/// <summary>
/// Optional platform bridge for cooperative native worker retirement. Host lifecycle methods
/// are cold-path operations; cancellation polling is implemented by the native registry.
/// </summary>
public interface INativeModOperationProvider
{
    bool OpenGeneration(string owner, long generation);

    bool TryBeginOperation(
        string owner,
        long generation,
        string name,
        out ModNativeOperationToken token);

    /// <returns>0 while active, 1 when cancelled, and -1 for a stale token.</returns>
    int GetCancellationState(in ModNativeOperationToken token);

    bool EndOperation(in ModNativeOperationToken token);

    bool CancelGenerationAndWait(
        string owner,
        long generation,
        uint timeoutMilliseconds);

    bool ResumeGeneration(string owner, long generation);
    bool RetireGeneration(string owner, long generation);
    int GetActiveOperationCount(string owner, long generation);
}

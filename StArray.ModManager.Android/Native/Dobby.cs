using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace StArray.ModManager.Android.Native;

/// <summary>
/// ModManager native hook P/Invoke 兼容封装。
/// Hook 安装直接进入 <c>core/hook_broker.cpp</c>；其余 Dobby 工具函数由
/// <c>core/dobby_hook.cpp</c> 导出。所有方法通过单一 native SO 调用。
/// </summary>
public static class Dobby
{
    private const string Lib = "starray_modmanager";
    private static readonly object HookLock = new();
    private static readonly Dictionary<HookKey, HookRecord> InstalledHooks = new();
    private static readonly Dictionary<nint, HookRecord> LatestHooks = new();

    private readonly record struct HookKey(nint Target, nint Detour);
    private sealed record HookRecord(nint Target, nint Detour, nint Origin, string Owner);

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

    /// <summary>安装 inline hook。</summary>
    /// <param name="address">目标函数地址</param>
    /// <param name="replace">替换函数地址（nint 指向 delegate 或函数指针）</param>
    /// <param name="origin">[out] 原函数指针（用于调用原逻辑）</param>
    /// <returns>0 = 成功，非 0 = 失败</returns>
    public static int Hook(nint address, nint replace, out nint origin)
        => Hook(address, replace, out origin, "Dobby.Hook");

    /// <summary>
    /// 安装 inline hook，并记录安装方。
    /// 同一地址重复安装同一 detour 会返回已保存的 continuation；同一地址的不同 detour 由 native HookBroker 追加为新 layer。
    /// </summary>
    /// <param name="address">目标函数地址</param>
    /// <param name="replace">替换函数地址</param>
    /// <param name="origin">当前 layer 的 continuation</param>
    /// <param name="owner">用于诊断的 hook layer 所有者</param>
    /// <returns>0 = 成功，非 0 = 失败</returns>
    public static int Hook(nint address, nint replace, out nint origin, string owner)
    {
        origin = nint.Zero;
        if (address == nint.Zero || replace == nint.Zero)
            return -1;

        lock (HookLock)
        {
            var key = new HookKey(address, replace);
            if (InstalledHooks.TryGetValue(key, out var existing))
            {
                origin = existing.Origin;
                return 0;
            }

            var normalizedOwner = string.IsNullOrWhiteSpace(owner) ? "unknown" : owner;
            var result = _Hook(normalizedOwner, address, replace, out origin);
            if (result == 0 && origin != nint.Zero)
            {
                var record = new HookRecord(
                    address,
                    replace,
                    origin,
                    normalizedOwner);
                InstalledHooks[key] = record;
                LatestHooks[address] = record;
            }

            return result;
        }
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
        RuntimeHelpers.PrepareMethod(replaceMethod.MethodHandle);
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
    public static extern int Instrument(nint address, nint preHandler);

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
    public static extern nint SymbolResolver(string imageName, string symbolName);

    /// <summary>上游公开名称；转发到同一 HookBroker 侧 resolver。</summary>
    public static nint _SymbolResolver(string imageName, string symbolName) =>
        SymbolResolver(imageName, symbolName);

    /// <summary>内存代码补丁。</summary>
    /// <param name="address">目标地址</param>
    /// <param name="buffer">补丁数据</param>
    /// <param name="bufferSize">补丁数据大小</param>
    /// <returns>0 = 成功</returns>
    [DllImport(Lib, EntryPoint = "modmanager_dobby_code_patch")]
    public static extern int CodePatch(nint address, byte[] buffer, uint bufferSize);

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

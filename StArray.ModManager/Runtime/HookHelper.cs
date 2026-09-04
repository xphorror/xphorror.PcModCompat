using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using StArray.ModManager.Manager;
using StArray.ModManager.RuntimeAbstractions;

namespace StArray.ModManager.Runtime;

/// <summary>
/// Captured generation gate used by source-generated detours. A rejected entry must call the
/// original trampoline instead of entering MOD code.
/// </summary>
public interface IModRuntimeCallbackGate
{
    bool TryEnter(out IDisposable? lease);

    void ReportFailure(string callbackName, Exception exception);
}

/// <summary>
/// Hook 辅助类 —— 提供与平台无关的静态 Hook 操作入口，
/// 供 <c>HookGenerator</c> 生成的代码调用。
/// 平台初始化时需设置 <see cref="Instance"/>。
/// </summary>
public static class HookHelper
{
    private static readonly AsyncLocal<string?> CurrentOwner = new();
    private static readonly AsyncLocal<RuntimeBinding?> CurrentRuntime = new();
    private static readonly object ProcessLifetimeOwnerLock = new();
    private static readonly HashSet<string> ProcessLifetimeHookOwners = new(StringComparer.Ordinal);
    private static readonly HashSet<string> RetiringHookOwners = new(StringComparer.Ordinal);
    private static IHook? _instance;

    /// <summary>当前平台的 Hook 实现（Windows = MinHook，Android = Dobby）</summary>
    public static IHook? Instance
    {
        get => Volatile.Read(ref _instance);
        set
        {
            var current = Volatile.Read(ref _instance);
            if (CurrentRuntime.Value != null && !ReferenceEquals(current, value))
            {
                throw new InvalidOperationException(
                    "A MOD generation cannot replace the process-wide hook provider.");
            }
            Volatile.Write(ref _instance, value);
        }
    }

    internal static string? CurrentOwnerId => CurrentOwner.Value;
    internal static ModRuntimeSession? CurrentRuntimeSession => CurrentRuntime.Value?.Session;
    internal static ModRuntimeKey CurrentRuntimeKey => CurrentRuntime.Value?.Key ?? default;
    internal static ModDataDomainToken CurrentDomainToken =>
        CurrentRuntime.Value?.DomainToken ?? default;

    internal static bool TryBeginNativeOperation(
        string name,
        out IModNativeOperationLease? lease)
    {
        lease = null;
        var runtime = CurrentRuntime.Value;
        if (runtime == null ||
            Instance is not INativeModOperationProvider provider ||
            !runtime.Session.CanRegisterOwnedResource(runtime.Key) ||
            !provider.TryBeginOperation(
                runtime.Key.OwnerId,
                runtime.Key.Generation,
                name,
                out var token) ||
            !token.IsValid)
        {
            return false;
        }

        var identity = NativeOperationIdentity(token, name);
        if (!ModOwnedResourceRegistry.TryRegister(
                runtime.Key,
                ModOwnedResourceKind.NativeOperation,
                identity))
        {
            provider.EndOperation(token);
            return false;
        }

        lease = new NativeOperationLease(provider, runtime.Key, token, identity);
        return true;
    }

    /// <summary>Captures the current MOD generation for a generated Hook wrapper.</summary>
    public static IModRuntimeCallbackGate? CaptureRuntimeCallbackGate()
    {
        var runtime = CurrentRuntime.Value;
        return runtime == null
            ? null
            : new RuntimeCallbackGate(runtime.Session, runtime.Key);
    }

    internal static bool SupportsOwnerScopedHookLifecycle
        => Instance is IOwnerScopedHook ownerScoped && ownerScoped.SupportsOwnerControl;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern nint GetProcAddress(nint hModule, string lpProcName);

    /// <summary>安装 Hook</summary>
    public static nint Hook(nint target, nint detour)
        => HookCore(target, detour, managedCallbackGate: null);

    /// <summary>
    /// Installs a detour whose wrapper enters the supplied Host runtime gate for every call.
    /// The gate must have been captured from the current MOD generation.
    /// </summary>
    public static nint HookRuntimeGated(
        nint target,
        nint detour,
        IModRuntimeCallbackGate? runtimeGate)
    {
        if (runtimeGate == null)
            return HookCore(target, detour, managedCallbackGate: null);
        var runtime = CurrentRuntime.Value;
        if (runtime == null ||
            runtimeGate is not RuntimeCallbackGate capturedGate ||
            !capturedGate.Matches(runtime))
        {
            return nint.Zero;
        }
        return HookCore(target, detour, capturedGate);
    }

    /// <summary>
    /// Installs a MOD detour only when a generation gate was captured from the current scope.
    /// Host hooks use <see cref="HookRuntimeGated"/> and may legitimately have no MOD scope.
    /// </summary>
    public static nint HookRuntimeGatedRequired(
        nint target,
        nint detour,
        IModRuntimeCallbackGate? runtimeGate) =>
        runtimeGate == null
            ? nint.Zero
            : HookRuntimeGated(target, detour, runtimeGate);

    private static nint HookCore(
        nint target,
        nint detour,
        RuntimeCallbackGate? managedCallbackGate)
    {
        var provider = Instance;
        if (provider == null) return nint.Zero;

        var resolved = RuntimeMethodCompatibility.TryResolveHandle(
            target,
            out var physicalTarget,
            out var compatibilityKind);
        var compatible = resolved &&
                         compatibilityKind != RuntimeMethodCompatibilityKind.None;
        if (compatible &&
            (provider is not IRuntimeMethodCompatibilityHook compatibilityProvider ||
             !compatibilityProvider.SupportsCompatibility(compatibilityKind)))
        {
            return nint.Zero;
        }

        var owner = CurrentOwner.Value;
        var runtime = CurrentRuntime.Value;
        if (runtime != null && !runtime.Session.CanRegisterOwnedResource(runtime.Key))
            return nint.Zero;
        var newlyRegistered = false;
        if (!provider.SupportsRuntimeUnhook &&
            !string.IsNullOrWhiteSpace(owner) &&
            !TryRegisterProcessLifetimeHookOwner(owner, out newlyRegistered))
            return nint.Zero;

        try
        {
            nint continuation;
            if (managedCallbackGate != null &&
                provider is IManagedCallbackGateAwareHook gateAwareProvider)
            {
                continuation = compatible
                    ? gateAwareProvider.HookCompatibleWithManagedCallbackGate(
                        physicalTarget,
                        detour,
                        compatibilityKind)
                    : gateAwareProvider.HookWithManagedCallbackGate(
                        physicalTarget,
                        detour);
            }
            else
            {
                continuation = compatible
                    ? ((IRuntimeMethodCompatibilityHook)provider).HookCompatible(
                        physicalTarget,
                        detour,
                        compatibilityKind)
                    : provider.Hook(physicalTarget, detour);
            }
            if (continuation != nint.Zero && runtime != null &&
                !ModOwnedResourceRegistry.TryRegister(
                    runtime.Key,
                    ModOwnedResourceKind.Hook,
                    $"target=0x{physicalTarget.ToInt64():X};detour=0x{detour.ToInt64():X}",
                    provider.SupportsRuntimeUnhook
                        ? ModOwnedResourceRetirementPolicy.MustRetire
                        : ModOwnedResourceRetirementPolicy.RetainWhileSuspended))
            {
                if (newlyRegistered)
                    RollbackProcessLifetimeHookOwner(owner, newlyRegistered);
                return nint.Zero;
            }
            if (continuation == nint.Zero && newlyRegistered)
                RollbackProcessLifetimeHookOwner(owner, newlyRegistered);
            return continuation;
        }
        catch
        {
            if (newlyRegistered)
                RollbackProcessLifetimeHookOwner(owner, newlyRegistered);
            throw;
        }
    }

    /// <summary>卸载 Hook</summary>
    public static bool Unhook(nint target)
    {
        var provider = Instance;
        if (provider == null) return false;
        var resolved = RuntimeMethodCompatibility.TryResolveHandle(
            target,
            out var physicalTarget,
            out var compatibilityKind);
        var compatible = resolved &&
                         compatibilityKind != RuntimeMethodCompatibilityKind.None;
        bool result;
        if (!provider.SupportsRuntimeUnhook)
        {
            var owner = CurrentOwner.Value;
            if (resolved)
            {
                result = provider is IOwnerScopedHook ownerScoped &&
                         ownerScoped.SupportsOwnerControl &&
                         !string.IsNullOrWhiteSpace(owner) &&
                         ownerScoped.RetireOwnerTarget(owner, physicalTarget);
            }
            else
            {
                result = provider is IOwnerScopedHook ownerScoped &&
                         ownerScoped.SupportsOwnerControl &&
                         !string.IsNullOrWhiteSpace(owner) &&
                         ownerScoped.RetireOwnerTarget(owner, target);
            }
        }
        else
        {
            result = provider.Unhook(physicalTarget);
        }

        if (result && compatible)
            RuntimeMethodCompatibility.ReleaseHandle(target);
        if (result && CurrentRuntime.Value is { } runtime)
        {
            var resolvedTarget = resolved ? physicalTarget : target;
            ModOwnedResourceRegistry.RetireMatching(
                runtime.Key,
                ModOwnedResourceKind.Hook,
                $"target=0x{resolvedTarget.ToInt64():X};");
        }
        return result;
    }

    internal static IDisposable EnterOwnerScope(string owner)
    {
        var previous = CurrentOwner.Value;
        var previousRuntime = CurrentRuntime.Value;
        var domainScope = ModDataDomainRuntime.SuppressScope();
        CurrentOwner.Value = owner;
        CurrentRuntime.Value = null;
        return new OwnerScope(previous, previousRuntime, domainScope);
    }

    internal static IDisposable EnterOwnerScope(
        string owner,
        ModRuntimeSession runtimeSession,
        ModRuntimeKey runtimeKey)
    {
        ArgumentNullException.ThrowIfNull(runtimeSession);
        if (!runtimeKey.IsValid || !string.Equals(owner, runtimeKey.OwnerId, StringComparison.Ordinal))
            throw new ArgumentException("Runtime key does not match the owner scope.", nameof(runtimeKey));

        var previous = CurrentOwner.Value;
        var previousRuntime = CurrentRuntime.Value;
        var domainToken = runtimeSession.DomainToken;
        if (!domainToken.IsValid ||
            !ModDataDomainRegistry.TryGetKey(domainToken, out var domainKey) ||
            !domainKey.Matches(runtimeKey))
        {
            throw new InvalidOperationException(
                $"MOD data domain is unavailable for owner={owner} generation={runtimeKey.Generation}.");
        }
        var domainScope = ModDataDomainRuntime.EnterScope(domainToken);
        CurrentOwner.Value = owner;
        CurrentRuntime.Value = new RuntimeBinding(runtimeSession, runtimeKey, domainToken);
        return new OwnerScope(previous, previousRuntime, domainScope);
    }

    internal static bool HasProcessLifetimeHooks(string owner)
    {
        lock (ProcessLifetimeOwnerLock)
        {
            if (!ProcessLifetimeHookOwners.Contains(owner))
                return false;
        }

        var provider = Instance;
        return provider is not IOwnerScopedHook ownerScoped ||
               !ownerScoped.SupportsOwnerControl ||
               ownerScoped.GetRetainedLayerCount(owner) > 0;
    }

    internal static bool HasProcessLifetimeHooks(ModRuntimeKey key)
    {
        if (!key.IsValid)
            return false;
        var owner = key.OwnerId;
        lock (ProcessLifetimeOwnerLock)
        {
            if (!ProcessLifetimeHookOwners.Contains(owner))
                return false;
        }

        var provider = Instance;
        return provider switch
        {
            IGenerationScopedHook generationScoped
                when generationScoped.SupportsOwnerControl =>
                generationScoped.GetRetainedLayerCount(owner, key.Generation) > 0,
            IOwnerScopedHook ownerScoped when ownerScoped.SupportsOwnerControl =>
                ownerScoped.GetRetainedLayerCount(owner) > 0,
            _ => true
        };
    }

    internal static bool HasUntrackedProcessLifetimeCallbacks(ModRuntimeKey key)
    {
        if (!key.IsValid || Instance is not IGenerationScopedHook generationScoped ||
            !generationScoped.SupportsOwnerControl)
        {
            return false;
        }
        return Instance is IManagedCallbackGateAwareHook gateAware
            ? gateAware.GetUntrackedCallbackLayerCount(key.OwnerId, key.Generation) > 0
            : HasProcessLifetimeHooks(key);
    }

    internal static bool RegisterProcessLifetimeHookOwner(string? owner)
        => TryRegisterProcessLifetimeHookOwner(owner, out _);

    internal static bool TryRegisterProcessLifetimeHookOwner(
        string? owner,
        out bool newlyRegistered)
    {
        newlyRegistered = false;
        if (string.IsNullOrWhiteSpace(owner))
            return true;
        lock (ProcessLifetimeOwnerLock)
        {
            if (RetiringHookOwners.Contains(owner))
                return false;
            newlyRegistered = ProcessLifetimeHookOwners.Add(owner);
            return true;
        }
    }

    internal static bool IsProcessLifetimeHookOwnerRegistered(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return false;
        lock (ProcessLifetimeOwnerLock)
            return ProcessLifetimeHookOwners.Contains(owner);
    }

    internal static void RollbackProcessLifetimeHookOwner(
        string? owner,
        bool newlyRegistered)
    {
        if (!newlyRegistered || string.IsNullOrWhiteSpace(owner))
            return;
        if (Instance is IOwnerScopedHook ownerScoped &&
            ownerScoped.SupportsOwnerControl &&
            ownerScoped.GetRetainedLayerCount(owner) > 0)
        {
            return;
        }
        lock (ProcessLifetimeOwnerLock)
            ProcessLifetimeHookOwners.Remove(owner);
    }

    internal static void BlockProcessLifetimeHookRegistration(string owner)
    {
        lock (ProcessLifetimeOwnerLock)
            RetiringHookOwners.Add(owner);
    }

    internal static void ResumeProcessLifetimeHookRegistration(string owner)
    {
        lock (ProcessLifetimeOwnerLock)
            RetiringHookOwners.Remove(owner);
    }

    internal static bool SuspendProcessLifetimeHooks(string owner)
    {
        var provider = Instance;
        return provider is IOwnerScopedHook ownerScoped &&
               ownerScoped.SupportsOwnerControl &&
               ownerScoped.SetOwnerEnabled(owner, false);
    }

    internal static bool SuspendProcessLifetimeHooks(ModRuntimeKey key)
    {
        if (!key.IsValid)
            return false;
        var provider = Instance;
        return provider switch
        {
            IGenerationScopedHook generationScoped
                when generationScoped.SupportsOwnerControl =>
                generationScoped.SetOwnerGenerationEnabled(
                    key.OwnerId, key.Generation, false),
            IOwnerScopedHook ownerScoped when ownerScoped.SupportsOwnerControl =>
                ownerScoped.SetOwnerEnabled(key.OwnerId, false),
            // Legacy providers have no owner gate to close. Their callers retain the
            // plugin and delegate roots for the process lifetime instead.
            _ => true
        };
    }

    internal static bool ResumeProcessLifetimeHooks(string owner)
    {
        var provider = Instance;
        return provider is IOwnerScopedHook ownerScoped &&
               ownerScoped.SupportsOwnerControl &&
               ownerScoped.SetOwnerEnabled(owner, true);
    }

    internal static bool ResumeProcessLifetimeHooks(ModRuntimeKey key)
    {
        if (!key.IsValid)
            return false;
        var provider = Instance;
        return provider switch
        {
            IGenerationScopedHook generationScoped
                when generationScoped.SupportsOwnerControl =>
                generationScoped.SetOwnerGenerationEnabled(
                    key.OwnerId, key.Generation, true),
            IOwnerScopedHook ownerScoped when ownerScoped.SupportsOwnerControl =>
                ownerScoped.SetOwnerEnabled(key.OwnerId, true),
            _ => true
        };
    }

    internal static bool OpenNativeOperationGeneration(ModRuntimeKey key)
    {
        if (!key.IsValid)
            return false;
        if (Instance is not INativeModOperationProvider provider)
            return true;
        return provider.OpenGeneration(key.OwnerId, key.Generation);
    }

    internal static bool CancelNativeOperationsAndWait(
        ModRuntimeKey key,
        TimeSpan timeout)
    {
        if (!key.IsValid ||
            (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan))
        {
            return false;
        }
        if (Instance is not INativeModOperationProvider provider)
            return true;

        var timeoutMilliseconds = timeout == Timeout.InfiniteTimeSpan
            ? uint.MaxValue
            : (uint)Math.Clamp(
                Math.Ceiling(timeout.TotalMilliseconds),
                0d,
                uint.MaxValue);
        return provider.CancelGenerationAndWait(
            key.OwnerId,
            key.Generation,
            timeoutMilliseconds);
    }

    internal static bool ResumeNativeOperationGeneration(ModRuntimeKey key)
    {
        if (!key.IsValid)
            return false;
        return Instance is not INativeModOperationProvider provider ||
               provider.ResumeGeneration(key.OwnerId, key.Generation);
    }

    internal static bool RetireNativeOperationGeneration(ModRuntimeKey key)
    {
        if (!key.IsValid)
            return false;
        if (Instance is not INativeModOperationProvider provider)
            return true;
        if (!provider.RetireGeneration(key.OwnerId, key.Generation))
            return false;
        ModOwnedResourceRegistry.RetireKind(
            key,
            ModOwnedResourceKind.NativeOperation);
        return true;
    }

    internal static int GetActiveNativeOperationCount(ModRuntimeKey key)
    {
        if (!key.IsValid)
            return -1;
        return Instance is INativeModOperationProvider provider
            ? provider.GetActiveOperationCount(key.OwnerId, key.Generation)
            : 0;
    }

    internal static int RetireProcessLifetimeHooks(string owner)
    {
        var provider = Instance;
        return provider is IOwnerScopedHook ownerScoped && ownerScoped.SupportsOwnerControl
            ? ownerScoped.RetireOwner(owner)
            : 0;
    }

    internal static int RetireProcessLifetimeHooks(ModRuntimeKey key)
    {
        if (!key.IsValid)
            return 0;
        var provider = Instance;
        return provider switch
        {
            IGenerationScopedHook generationScoped
                when generationScoped.SupportsOwnerControl =>
                generationScoped.RetireOwnerGeneration(key.OwnerId, key.Generation),
            IOwnerScopedHook ownerScoped when ownerScoped.SupportsOwnerControl =>
                ownerScoped.RetireOwner(key.OwnerId),
            _ => 0
        };
    }

    /// <summary>获取库导出函数地址</summary>
    public static nint GetFunction(string library, string name)
    {
        if (Instance != null)
            return Instance.GetFunction(library, name);

        // Instance == null 时的降级路径
        return GetFunctionFallback(library, name);
    }

    /// <summary>获取库基址 + RVA 对应的绝对地址</summary>
    public static nint GetFunctionRVA(string library, long rva)
    {
        if (Instance != null)
        {
            var addr = Instance.GetFunctionRVA(library, rva);
            if (addr != nint.Zero) return addr;
        }

        return GetFunctionRVAFallback(library, rva);
    }

    /// <summary>降级路径（无 Instance 时使用）</summary>
    public static nint GetFunctionFallback(string library, string name)
    {
        // 1) GetModuleHandle 获取已加载的模块
        var dllName = library.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? library : library + ".dll";
        var mod = GetModuleHandle(dllName);
        if (mod != nint.Zero)
        {
            var addr = GetProcAddress(mod, name);
            if (addr != nint.Zero) return addr;
        }

        // 2) 从 dlls/ 加载
        var dllsPath = Path.Combine(AppContext.BaseDirectory, "dlls", dllName);
        if (File.Exists(dllsPath) && NativeLibrary.TryLoad(dllsPath, out var lib))
            return NativeLibrary.GetExport(lib, name);

        // 3) 从输出目录根加载
        var rootPath = Path.Combine(AppContext.BaseDirectory, dllName);
        if (File.Exists(rootPath) && NativeLibrary.TryLoad(rootPath, out lib))
            return NativeLibrary.GetExport(lib, name);

        // 4) 回退标准 TryLoad
        if (NativeLibrary.TryLoad(library, out lib))
            return NativeLibrary.GetExport(lib, name);
        return nint.Zero;
    }

    /// <summary>降级 RVA 解析路径</summary>
    public static nint GetFunctionRVAFallback(string library, long rva)
    {
        var dllName = library.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? library : library + ".dll";

        var mod = GetModuleHandle(dllName);
        if (mod != nint.Zero)
            return mod + (nint)rva;

        var localPath = Path.Combine(AppContext.BaseDirectory, dllName);
        if (File.Exists(localPath) && NativeLibrary.TryLoad(localPath, out _))
        {
            mod = GetModuleHandle(dllName);
            if (mod != nint.Zero)
                return mod + (nint)rva;
        }

        var dllsPath = Path.Combine(AppContext.BaseDirectory, "dlls", dllName);
        if (File.Exists(dllsPath) && NativeLibrary.TryLoad(dllsPath, out _))
        {
            mod = GetModuleHandle(dllName);
            if (mod != nint.Zero)
                return mod + (nint)rva;
        }

        if (NativeLibrary.TryLoad(library, out _))
        {
            mod = GetModuleHandle(dllName);
            if (mod != nint.Zero)
                return mod + (nint)rva;
        }

        return nint.Zero;
    }

    private sealed record RuntimeBinding(
        ModRuntimeSession Session,
        ModRuntimeKey Key,
        ModDataDomainToken DomainToken);

    private static string NativeOperationIdentity(
        ModNativeOperationToken token,
        string name)
    {
        var normalized = name.Trim().Replace('\r', ' ').Replace('\n', ' ');
        if (normalized.Length > 160)
            normalized = normalized[..160];
        return $"operation={token.OperationId};slot={token.Slot};name={normalized}";
    }

    private sealed class NativeOperationLease(
        INativeModOperationProvider provider,
        ModRuntimeKey key,
        ModNativeOperationToken token,
        string identity) : IModNativeOperationLease
    {
        private INativeModOperationProvider? _provider = provider;
        private readonly ModRuntimeKey _key = key;
        private readonly string _identity = identity;

        public ModNativeOperationToken Token { get; } = token;

        public bool IsCancellationRequested
        {
            get
            {
                var current = Volatile.Read(ref _provider);
                return current == null || current.GetCancellationState(Token) != 0;
            }
        }

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _provider, null);
            if (current == null)
                return;
            current.EndOperation(Token);
            ModOwnedResourceRegistry.RetireExact(
                _key,
                ModOwnedResourceKind.NativeOperation,
                _identity);
        }
    }

    private sealed class RuntimeCallbackGate(
        ModRuntimeSession session,
        ModRuntimeKey key) : IModRuntimeCallbackGate
    {
        private readonly ModRuntimeSession _session = session;
        private readonly ModRuntimeKey _key = key;
        private ConcurrentDictionary<string, int>? _failureCounts;

        public bool TryEnter(out IDisposable? lease)
        {
            lease = null;
            IDisposable? callbackLease = null;
            IDisposable? domainScope = null;
            var previousOwner = CurrentOwner.Value;
            var previousRuntime = CurrentRuntime.Value;

            // Native detours enter CoreCLR from a Unity thread, so there is no ambient
            // MOD scope to inherit. Resolve the token captured for this generation before
            // entering the callback; using the session's current token without this match
            // check could accidentally admit a stale detour into a reloaded generation.
            try
            {
                var token = _session.DomainToken;
                if (!ModDataDomainRegistry.TryResolve(token, out var domain) ||
                    !domain.Key.Matches(_key))
                {
                    return false;
                }

                if (!_session.TryEnterCallback(_key, out callbackLease) || callbackLease == null)
                    return false;

                // The callback lease prevents this generation from completing retirement while
                // the Host restores its ambient owner/runtime scope. Revalidate the token after
                // acquiring that lease so a stale callback can never inherit a replacement domain.
                if (_session.DomainToken != token ||
                    !ModDataDomainRegistry.TryResolve(token, out domain) ||
                    !domain.Key.Matches(_key))
                {
                    callbackLease.Dispose();
                    callbackLease = null;
                    return false;
                }

                domainScope = ModDataDomainRuntime.EnterScope(token);
                CurrentOwner.Value = _key.OwnerId;
                CurrentRuntime.Value = new RuntimeBinding(_session, _key, token);
                lease = new RuntimeCallbackScope(
                    previousOwner,
                    previousRuntime,
                    domainScope,
                    callbackLease);
                domainScope = null;
                callbackLease = null;
                return true;
            }
            catch
            {
                // This is a native callback boundary. Retirement can close the domain between
                // the token check and scope entry; reject that callback instead of allowing a
                // managed exception to cross the unmanaged thunk and abort CoreCLR.
                CurrentOwner.Value = previousOwner;
                CurrentRuntime.Value = previousRuntime;
                domainScope?.Dispose();
                callbackLease?.Dispose();
                lease = null;
                return false;
            }
        }

        internal bool Matches(RuntimeBinding runtime)
            => ReferenceEquals(_session, runtime.Session) && _key.Matches(runtime.Key);

        public void ReportFailure(string callbackName, Exception exception)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(exception);
                var normalized = NormalizeCallbackName(callbackName);
                var failureCounts = Volatile.Read(ref _failureCounts);
                if (failureCounts == null)
                {
                    var candidate = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
                    failureCounts = Interlocked.CompareExchange(
                        ref _failureCounts,
                        candidate,
                        null) ?? candidate;
                }
                var count = failureCounts.AddOrUpdate(
                    normalized,
                    1,
                    static (_, current) => current == int.MaxValue ? current : current + 1);
                if (count != 1 && (count & (count - 1)) != 0)
                    return;

                Logger.Error(
                    "NativeModCallback",
                    $"owner={_key.OwnerId} generation={_key.Generation} " +
                    $"callback={normalized} repeated={count}: {exception}");
            }
            catch
            {
                // Diagnostics must never turn a contained MOD callback failure into a process abort.
            }
        }

        private static string NormalizeCallbackName(string callbackName)
        {
            var normalized = string.IsNullOrWhiteSpace(callbackName)
                ? "<unknown>"
                : callbackName.Trim().Replace('\r', ' ').Replace('\n', ' ');
            return normalized.Length <= 240 ? normalized : normalized[..240];
        }
    }

    private sealed class RuntimeCallbackScope(
        string? previousOwner,
        RuntimeBinding? previousRuntime,
        IDisposable domainScope,
        IDisposable callbackLease) : IDisposable
    {
        private readonly string? _previousOwner = previousOwner;
        private readonly RuntimeBinding? _previousRuntime = previousRuntime;
        private IDisposable? _domainScope = domainScope;
        private IDisposable? _callbackLease = callbackLease;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            CurrentOwner.Value = _previousOwner;
            CurrentRuntime.Value = _previousRuntime;
            Interlocked.Exchange(ref _domainScope, null)?.Dispose();
            Interlocked.Exchange(ref _callbackLease, null)?.Dispose();
        }
    }

    private sealed class OwnerScope(
        string? previous,
        RuntimeBinding? previousRuntime,
        IDisposable domainScope) : IDisposable
    {
        private readonly string? _previous = previous;
        private readonly RuntimeBinding? _previousRuntime = previousRuntime;
        private IDisposable? _domainScope = domainScope;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                var completedRuntime = CurrentRuntime.Value;
                CurrentOwner.Value = _previous;
                CurrentRuntime.Value = _previousRuntime;
                Interlocked.Exchange(ref _domainScope, null)?.Dispose();
                if (completedRuntime != null)
                    completedRuntime.Session.AuditOwnedResources(completedRuntime.Key);
            }
        }
    }
}

using StArray.ModManager.Resources;
using System.Reflection;
using System.Text;
using System.Threading;
using StArray.ModManager.Manager;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Runtime;

/// <summary>Mod 管理器核心 / Mod loader — scan, load, enable/disable mods</summary>
public class ModLoader
{
    private readonly List<ModEntry> _mods = new();
    private readonly IReadOnlyList<ModEntry> _modsView;
    private readonly List<ModEntry> _pendingLoads = new(4);
    private readonly string _modsDirectory;
    private int _pendingLoadUpdateActive;
    private int _pendingLoadRequestScheduled;
    private int _pendingAsyncLoadCount;
    private Func<Action, bool>? _pendingLoadCompletionScheduler;

    /// <summary>已发现的 Mod 列表（只读）</summary>
    public IReadOnlyList<ModEntry> Mods => _modsView;
    /// <summary>Mods 目录路径</summary>
    public string ModsDirectory
    {
        get => _modsDirectory;
        set => throw new NotSupportedException("ModsDirectory is set via constructor only");
    }

    /// <summary>Mod 状态变更事件</summary>
    public event Action<ModEntry>? OnModStateChanged;

    public int PendingAsyncLoadCount => Math.Max(0, Volatile.Read(ref _pendingAsyncLoadCount));
    public bool HasPendingAsyncLoads => PendingAsyncLoadCount > 0 || _mods.Any(mod =>
        mod.LoadState == ModLoadState.Loading && mod.PluginInstance is IAsyncModPlugin);

    /// <summary>创建 ModLoader 并指定 Mods 目录</summary>
    public ModLoader(string modsDirectory)
    {
        _modsDirectory = modsDirectory;
        _modsView = _mods.AsReadOnly();
    }

    private static Type? ResolvePluginType(Assembly assembly)
    {
        try
        {
            if (assembly.GetCustomAttribute<ModEntryPointAttribute>()?.PluginType is { } declared)
            {
                if (IsPluginType(declared))
                    return declared;

                Logger.Warn(nameof(ModLoader),
                    $"ModEntryPoint ignored assembly={assembly.GetName().Name} " +
                    $"type={declared.FullName}: not a concrete IModPlugin");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(nameof(ModLoader),
                $"ModEntryPoint unavailable assembly={assembly.GetName().Name}: " +
                $"{ex.GetType().Name}: {ToSingleLine(ex.Message)}");
        }

        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
            var first = ex.LoaderExceptions?.FirstOrDefault();
            Logger.Warn(nameof(ModLoader),
                $"partial type scan assembly={assembly.GetName().Name} " +
                $"loaded={types.Count(type => type != null)} failed={types.Count(type => type == null)}" +
                (first == null
                    ? string.Empty
                    : $" first={first.GetType().Name}:{ToSingleLine(first.Message)}"));
        }

        return types.FirstOrDefault(type => type != null && IsPluginType(type));
    }

    private static bool IsPluginType(Type type)
        => typeof(IModPlugin).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract;

    /// <summary>
    /// Routes async MOD finalization to the platform-owned execution thread.
    /// A null scheduler preserves the desktop synchronous behavior.
    /// </summary>
    public void SetPendingLoadCompletionScheduler(Func<Action, bool>? scheduler)
        => Interlocked.Exchange(ref _pendingLoadCompletionScheduler, scheduler);

    /// <summary>Requests one coalesced async-load finalization pass.</summary>
    public bool RequestPendingLoadUpdate()
    {
        if (!HasPendingAsyncLoads)
            return true;

        var scheduler = Volatile.Read(ref _pendingLoadCompletionScheduler);
        if (scheduler == null)
        {
            UpdatePendingLoads();
            return true;
        }

        if (Interlocked.CompareExchange(ref _pendingLoadRequestScheduled, 1, 0) != 0)
            return true;

        try
        {
            var accepted = scheduler(() =>
            {
                try
                {
                    UpdatePendingLoads();
                }
                finally
                {
                    Volatile.Write(ref _pendingLoadRequestScheduled, 0);
                }
            });
            if (!accepted)
                Volatile.Write(ref _pendingLoadRequestScheduled, 0);
            return accepted;
        }
        catch
        {
            Volatile.Write(ref _pendingLoadRequestScheduled, 0);
            throw;
        }
    }

    /// <summary>
    /// 扫描 mods 目录，发现所有 Mod
    /// </summary>
    public void ScanMods()
    {
        // 保存当前已加载 Mod 的状态（扫描后恢复）
        var runtimeStates = _mods
            .Where(m => m.LoadState is ModLoadState.Loading or ModLoadState.Loaded or ModLoadState.Error)
            .ToDictionary(
                m => m.Id,
                m => (m.PluginInstance, m.IsEnabled, m.LoadState, m.LoadError, m.LoadProgress, m.LoadStage));

        _mods.Clear();

        if (!Directory.Exists(_modsDirectory))
        {
            Directory.CreateDirectory(_modsDirectory);
            Logger.Info(nameof(ModLoader), L10n.Get("Log_DirCreated", _modsDirectory));
            return;
        }

        foreach (var dir in Directory.GetDirectories(_modsDirectory))
        {
            var mod = DiscoverMod(dir);
            if (mod != null)
            {
                // 恢复之前已加载的状态
                if (runtimeStates.TryGetValue(mod.Id, out var state))
                {
                    mod.PluginInstance = state.PluginInstance;
                    mod.IsEnabled = state.IsEnabled;
                    mod.LoadState = state.LoadState;
                    mod.LoadError = state.LoadError;
                    mod.LoadProgress = state.LoadProgress;
                    mod.LoadStage = state.LoadStage;
                }
                _mods.Add(mod);
                Logger.Info(nameof(ModLoader), L10n.Get("Log_ModFound", mod.Name, mod.Id));
            }
        }

        Logger.Info(nameof(ModLoader), L10n.Get("Log_ModCount", _mods.Count));
    }

    /// <summary>重新扫描导入目录并定位刚导入的 MOD，不改变其加载状态。</summary>
    public ModEntry? RefreshImportedMod(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return null;

        ScanMods();

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(folderPath);
        }
        catch (Exception ex)
        {
            Logger.Warn(nameof(ModLoader), $"Imported mod path is invalid: {ex.Message}");
            return null;
        }

        var mod = _mods.FirstOrDefault(entry =>
            string.Equals(
                Path.GetFullPath(entry.FolderPath),
                fullPath,
                StringComparison.OrdinalIgnoreCase));
        if (mod == null)
        {
            Logger.Warn(nameof(ModLoader), $"Imported mod was not discovered: {fullPath}");
            return null;
        }

        return mod;
    }

    /// <summary>重新扫描、定位并立即加载指定的导入 MOD。</summary>
    public ModEntry? LoadImportedMod(string? folderPath)
    {
        var mod = RefreshImportedMod(folderPath);
        if (mod != null)
            LoadMod(mod);
        return mod;
    }

    /// <summary>加载配置中标记为启用的 MOD。用于启动后台加载，不依赖 ModManager 面板渲染。</summary>
    public int LoadConfiguredEnabledMods(IReadOnlyDictionary<string, bool> enabledMods)
    {
        if (enabledMods.Count == 0)
            return 0;

        var started = 0;
        foreach (var mod in _mods)
        {
            if (!enabledMods.TryGetValue(mod.Id, out var enabled) || !enabled)
                continue;

            if (mod.LoadState is ModLoadState.Loading or ModLoadState.Loaded)
                continue;

            if (LoadMod(mod))
                started++;
            else if (mod.LoadState == ModLoadState.Loading)
                started++;
        }

        return started;
    }

    /// <summary>
    /// 从文件夹发现 Mod 信息
    /// </summary>
    private ModEntry? DiscoverMod(string folderPath)
    {
        var dirName = Path.GetFileName(folderPath);

        if (PcModManifestReader.TryRead(folderPath, out var pcManifest, out var pcError))
        {
            Logger.Info(nameof(ModLoader), $"PcModCompat manifest found: {pcManifest.Id} ({pcManifest.Kind})");
            return PcCompatModPlugin.CreateEntry(pcManifest);
        }

        if (!string.IsNullOrEmpty(pcError))
            Logger.Warn(nameof(ModLoader), $"PcModCompat manifest ignored for {dirName}: {pcError}");

        var entryDll = Directory.GetFiles(folderPath, "*.dll")
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == dirName)
            ?? Directory.GetFiles(folderPath, "*.dll")
                .FirstOrDefault(f => !Path.GetFileNameWithoutExtension(f).Equals("StArray.ModManager", StringComparison.OrdinalIgnoreCase));

        if (entryDll == null) return null;

        try
        {
            var assembly = Assembly.LoadFrom(entryDll);

            var pluginType = ResolvePluginType(assembly);

            if (pluginType == null) return null;

            // 实例化以读取元数据
            var plugin = (IModPlugin)Activator.CreateInstance(pluginType)!;

            return new ModEntry
            {
                Id = plugin.Id,
                Name = plugin.Name,
                Version = plugin.Version,
                Author = plugin.Author,
                Description = plugin.Description,
                Dependencies = plugin.Dependencies.ToList(),
                FolderPath = folderPath,
                EntryPoint = entryDll,
            };
        }
        catch (Exception ex)
        {
            Logger.Error(nameof(ModLoader), L10n.Get("Log_ModAssemblyError", dirName, ex.Message));
            return null;
        }
    }

    /// <summary>
    /// 加载指定的 Mod
    /// </summary>
    public bool LoadMod(ModEntry mod)
    {
        if (mod.LoadState == ModLoadState.Loaded)
        {
            Logger.Info(nameof(ModLoader), L10n.Get("Log_ModAlreadyLoaded", mod.Name));
            return true;
        }
        if (mod.LoadState == ModLoadState.Loading)
            return true;

        if (mod.LoadState == ModLoadState.NotLoaded &&
            mod.PluginInstance != null &&
            HookHelper.HasProcessLifetimeHooks(mod.Id))
        {
            mod.IsEnabled = true;
            mod.LoadState = ModLoadState.Loaded;
            mod.LoadError = null;
            mod.LoadProgress = 1;
            mod.LoadStage = string.Empty;
            Logger.Info(nameof(ModLoader), $"{mod.Name} resumed with process-lifetime native hooks");
            OnModStateChanged?.Invoke(mod);
            return true;
        }

        mod.LoadState = ModLoadState.Loading;
        mod.LoadError = null;
        OnModStateChanged?.Invoke(mod);

        try
        {
            // 依赖检查
            foreach (var depId in mod.Dependencies)
            {
                var dep = _mods.FirstOrDefault(m => m.Id == depId);
                if (dep == null)
                {
                    throw new Exception(L10n.Get("Log_MissingDep", depId));
                }
                if (dep.LoadState != ModLoadState.Loaded)
                {
                    Logger.Info(nameof(ModLoader), $"  load dep: {dep.Name}");
                    LoadMod(dep);
                }
            }

            if (mod.LoaderData is PcModManifest manifest)
            {
                var plugin = mod.PluginInstance as PcCompatModPlugin ?? new PcCompatModPlugin(manifest);
                mod.PluginInstance = plugin;
                mod.IsEnabled = true;

                if (plugin is IAsyncModPlugin asyncPlugin)
                {
                    using (HookHelper.EnterOwnerScope(mod.Id))
                        asyncPlugin.BeginLoad();
                    Interlocked.Increment(ref _pendingAsyncLoadCount);
                    try
                    {
                        UpdateLoadProgress(mod, asyncPlugin);
                    }
                    catch
                    {
                        asyncPlugin.CancelLoad();
                        CompletePendingAsyncLoad();
                        throw;
                    }
                    Logger.Info(
                        nameof(ModLoader),
                        L10n.Get("Log_PcCompatBackgroundStarted", mod.Name));
                    OnModStateChanged?.Invoke(mod);
                    return true;
                }

                using (HookHelper.EnterOwnerScope(mod.Id))
                    plugin.OnLoad();
                MarkLoaded(mod, L10n.Get("Log_PcCompatLoadSuccess", mod.Name));
                OnModStateChanged?.Invoke(mod);
                return true;
            }

            // 加载入口程序集
            if (!string.IsNullOrEmpty(mod.EntryPoint) && File.Exists(mod.EntryPoint))
            {
                var assembly = Assembly.LoadFrom(mod.EntryPoint);
                Logger.Info(
                    nameof(ModLoader),
                    L10n.Get("Log_ModAssemblyLoaded", mod.Name, assembly.GetName().Name ?? string.Empty));

                var pluginType = ResolvePluginType(assembly);

                if (pluginType != null)
                {
                    var plugin = (IModPlugin)Activator.CreateInstance(pluginType)!;
                    mod.PluginInstance = plugin;
                    using (HookHelper.EnterOwnerScope(mod.Id))
                        plugin.OnLoad();

                    if (plugin is IModSettings s)
                        ModManagerUI.LoadSettings(mod, s);

                    Logger.Info(nameof(ModLoader), L10n.Get("Log_ModEntryExecuted", mod.Name));
                }
            }
            else
            {
                Logger.Info(nameof(ModLoader), L10n.Get("Log_ModNoEntry", mod.Name));
            }

            mod.IsEnabled = true;
            mod.LoadState = ModLoadState.Loaded;
            Logger.Info(nameof(ModLoader), L10n.Get("Log_ModLoadSuccess", mod.Name));
        }
        catch (Exception ex)
        {
            mod.LoadState = ModLoadState.Error;
            mod.LoadError = ex.Message;
            LogLoadFailure(mod, ex);
        }

        OnModStateChanged?.Invoke(mod);
        return mod.LoadState == ModLoadState.Loaded;
    }

    /// <summary>
    /// 卸载指定的 Mod
    /// </summary>
    public void UnloadMod(ModEntry mod)
    {
        if (mod.LoadState is not (ModLoadState.Loaded or ModLoadState.Loading)) return;

        var hasProcessLifetimeHooks = HookHelper.HasProcessLifetimeHooks(mod.Id);
        var supportsLogicalRetirement =
            mod.PluginInstance is ILogicalProcessLifetimeHookRetirement;
        Logger.Info(
            nameof(ModLoader),
            $"[DEBUG-kv-unload-v1] request mod={mod.Id} state={mod.LoadState} " +
            $"enabled={mod.IsEnabled} processHooks={hasProcessLifetimeHooks} " +
            $"logicalRetire={supportsLogicalRetirement} " +
            $"plugin={mod.PluginInstance?.GetType().FullName ?? "<null>"} " +
            $"tid={Environment.CurrentManagedThreadId}");

        if (hasProcessLifetimeHooks && !supportsLogicalRetirement)
        {
            if (mod.LoadState == ModLoadState.Loading)
            {
                Logger.Warn(
                    nameof(ModLoader),
                    $"{mod.Name} cannot be cancelled after installing process-lifetime native hooks");
                return;
            }

            mod.IsEnabled = false;
            mod.LoadState = ModLoadState.NotLoaded;
            mod.LoadProgress = 0;
            mod.LoadStage = "Suspended";
            mod.LoadError = "Native hooks remain active until the app restarts.";
            Logger.Warn(
                nameof(ModLoader),
                $"[DEBUG-kv-unload-v1] route=suspend mod={mod.Id} reason=process-lifetime-hooks " +
                $"tid={Environment.CurrentManagedThreadId}; {mod.Name} suspended without OnUnload; " +
                "native hooks and delegate roots remain until restart");
            OnModStateChanged?.Invoke(mod);
            return;
        }

        var wasLoading = mod.LoadState == ModLoadState.Loading;
        if (mod.PluginInstance is IAsyncModPlugin asyncPlugin && wasLoading)
            asyncPlugin.CancelLoad();
        if (wasLoading)
            CompletePendingAsyncLoad();
        if (mod.PluginInstance != null)
        {
            Logger.Info(
                nameof(ModLoader),
                $"[DEBUG-kv-unload-v1] route=onunload-enter mod={mod.Id} " +
                $"tid={Environment.CurrentManagedThreadId}");
            try
            {
                using (HookHelper.EnterOwnerScope(mod.Id))
                    mod.PluginInstance.OnUnload();
            }
            catch (Exception exception)
            {
                Logger.Error(
                    nameof(ModLoader),
                    $"[DEBUG-kv-unload-v1] route=onunload-failed mod={mod.Id} " +
                    $"tid={Environment.CurrentManagedThreadId} error={exception}");
                throw;
            }
            Logger.Info(
                nameof(ModLoader),
                $"[DEBUG-kv-unload-v1] route=onunload-complete mod={mod.Id} " +
                $"tid={Environment.CurrentManagedThreadId}");
        }
        mod.PluginInstance = mod.LoaderData is PcModManifest manifest
            ? new PcCompatModPlugin(manifest)
            : null;
        mod.IsEnabled = false;
        mod.LoadState = ModLoadState.NotLoaded;
        mod.LoadProgress = 0;
        mod.LoadStage = string.Empty;
        Logger.Info(nameof(ModLoader), L10n.Get("Log_ModUnloaded", mod.Name));
        OnModStateChanged?.Invoke(mod);
    }

    /// <summary>
    /// 切换 Mod 启用状态
    /// </summary>
    public void ToggleMod(ModEntry mod)
    {
        if (mod.LoadState is ModLoadState.Loading or ModLoadState.Loaded)
            UnloadMod(mod);
        else
            LoadMod(mod);
    }

    /// <summary>在 UI/Unity 主线程轮询异步 MOD，并执行需要主线程语义的收尾。</summary>
    public void UpdatePendingLoads()
    {
        if (Interlocked.Exchange(ref _pendingLoadUpdateActive, 1) != 0)
            return;

        try
        {
            _pendingLoads.Clear();
            foreach (var mod in _mods)
            {
                if (mod.LoadState == ModLoadState.Loading)
                    _pendingLoads.Add(mod);
            }

            foreach (var mod in _pendingLoads)
            {
                if (mod.PluginInstance is not IAsyncModPlugin asyncPlugin)
                    continue;

                UpdateLoadProgress(mod, asyncPlugin);
                if (!asyncPlugin.IsLoadReady)
                    continue;

                // UnloadMod runs on the UI thread while this pass runs on the
                // platform completion thread. Re-verify that the entry is still
                // waiting for this exact plugin before installing anything;
                // UnloadMod already accounted the pending count in that case.
                if (mod.LoadState != ModLoadState.Loading ||
                    !ReferenceEquals(mod.PluginInstance, asyncPlugin))
                    continue;

                try
                {
                    using (HookHelper.EnterOwnerScope(mod.Id))
                        asyncPlugin.CompleteLoad();
                    if (mod.LoadState == ModLoadState.Loading &&
                        ReferenceEquals(mod.PluginInstance, asyncPlugin))
                    {
                        MarkLoaded(mod, L10n.Get("Log_PcCompatLoadSuccess", mod.Name));
                    }
                    else
                    {
                        UnwindStaleCompletion(mod);
                    }
                }
                catch (Exception ex)
                {
                    if (mod.LoadState == ModLoadState.Loading &&
                        ReferenceEquals(mod.PluginInstance, asyncPlugin))
                    {
                        mod.LoadState = ModLoadState.Error;
                        mod.IsEnabled = false;
                        mod.LoadProgress = 1;
                        mod.LoadStage = "Failed";
                        mod.LoadError = ex.Message;
                        LogLoadFailure(mod, ex);
                    }
                    else
                    {
                        Logger.Info(
                            nameof(ModLoader),
                            L10n.Get("Log_StaleCompletionIgnored", mod.Name, ex.Message));
                    }
                }
                finally
                {
                    CompletePendingAsyncLoad();
                }

                OnModStateChanged?.Invoke(mod);
            }
        }
        finally
        {
            Volatile.Write(ref _pendingLoadUpdateActive, 0);
        }
    }

    private void CompletePendingAsyncLoad()
    {
        while (true)
        {
            var current = Volatile.Read(ref _pendingAsyncLoadCount);
            if (current <= 0)
                return;
            if (Interlocked.CompareExchange(ref _pendingAsyncLoadCount, current - 1, current) == current)
                return;
        }
    }

    /// <summary>
    /// UnloadMod raced with a background completion: the plugin had already
    /// installed its runtime registration before the entry was torn down.
    /// Remove that stale registration so no orphaned MOD stays active.
    /// </summary>
    private static void UnwindStaleCompletion(ModEntry mod)
    {
        try
        {
            if (mod.LoaderData is PcModManifest manifest)
            {
                Logger.Warn(
                    nameof(ModLoader),
                    L10n.Get("Log_StaleRegistrationRollback", mod.Name));
                Xphorror.PcModCompat.PcCompatRuntime.UnregisterMod(manifest);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(
                nameof(ModLoader),
                L10n.Get("Log_StaleRegistrationRollbackFailed", mod.Name, ex.Message));
        }
    }

    private static void LogLoadFailure(ModEntry mod, Exception exception)
    {
        const int maxLogcatChars = 2800;
        var chain = new List<Exception>(4);
        for (Exception? current = exception; current != null; current = current.InnerException)
            chain.Add(current);

        var root = chain[^1];
        var rootFrame = root.StackTrace?
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(frame => frame.Trim())
            .FirstOrDefault();
        var summary = new StringBuilder(512)
            .Append("load-failure mod=").Append(mod.Id)
            .Append(" root=").Append(root.GetType().FullName)
            .Append(": ").Append(ToSingleLine(root.Message))
            .Append(" chain=");

        for (var i = 0; i < chain.Count; i++)
        {
            if (i > 0)
                summary.Append(" -> ");
            summary.Append(chain[i].GetType().FullName)
                .Append(": ")
                .Append(ToSingleLine(chain[i].Message));
        }

        if (!string.IsNullOrWhiteSpace(rootFrame))
            summary.Append(" rootAt=").Append(ToSingleLine(rootFrame));

        if (summary.Length > maxLogcatChars)
        {
            summary.Length = maxLogcatChars - 3;
            summary.Append("...");
        }

        Logger.Error(nameof(ModLoader), summary.ToString());
    }

    private static string ToSingleLine(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "<empty>"
            : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void UpdateLoadProgress(ModEntry mod, IAsyncModPlugin plugin)
    {
        var progress = plugin.GetLoadProgress();
        mod.LoadProgress = Math.Clamp(progress.Progress, 0f, 1f);
        mod.LoadStage = progress.Stage;
    }

    private static void MarkLoaded(ModEntry mod, string logMessage)
    {
        mod.IsEnabled = true;
        mod.LoadState = ModLoadState.Loaded;
        mod.LoadProgress = 1;
        mod.LoadStage = "Ready";
        Logger.Info(nameof(ModLoader), logMessage);
    }

    /// <summary>
    /// 添加一个新的 Mod 条目（手动创建）
    /// </summary>
    public ModEntry AddMod(ModEntry mod)
    {
        _mods.Add(mod);
        Logger.Info(nameof(ModLoader), L10n.Get("Log_ModAdded", mod.Name));
        return mod;
    }

    /// <summary>
    /// 移除 Mod 条目
    /// </summary>
    public bool RemoveMod(ModEntry mod)
    {
        if (mod.LoadState == ModLoadState.Loaded)
            UnloadMod(mod);

        var removed = _mods.Remove(mod);
        if (removed)
            Logger.Info(nameof(ModLoader), L10n.Get("Log_ModRemoved", mod.Name));
        return removed;
    }

}

using StArray.ModManager.Resources;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using StArray.ModManager.Behaviours;
using StArray.ModManager.Manager;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Runtime;

/// <summary>Mod 管理器核心 / Mod loader — scan, load, enable/disable mods</summary>
public class ModLoader
{
    private const string NativeShadowDirectoryName = ".starray-shadow";
    private const string NativeDataDirectoryName = ".starray-data";
    private static readonly TimeSpan RuntimeCallbackQuiescenceTimeout = TimeSpan.FromSeconds(5);
    private readonly List<ModEntry> _mods = new();
    private readonly IReadOnlyList<ModEntry> _modsView;
    private readonly List<ModEntry> _pendingLoads = new(4);
    private readonly List<NativeModLoadState> _orphanedNativeStates = new();
    private readonly ConditionalWeakTable<ModEntry, object> _transitionLocks = new();
    private readonly string _modsDirectory;
    private readonly string _nativeShadowRoot;
    private readonly string _nativeDataRoot;
    private readonly ModHostPathPolicy _hostPathPolicy;
    // ScanMods rebuilds ModEntry objects. A per-object lock is insufficient because an old
    // UI/config reference can otherwise race a newly discovered entry that shares its
    // RuntimeSession. Serialize lifecycle transitions and scans at the host boundary.
    private readonly object _transitionGate = new();
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

    internal ModOwnedResourceAuditSnapshot SnapshotOwnedResourceAudit()
    {
        var sessions = _mods
            .Select(mod => mod.RuntimeSession.Snapshot())
            .Concat(_orphanedNativeStates.Select(state => state.RuntimeSession.Snapshot()))
            .ToArray();
        return ModOwnedResourceRegistry.CreateAuditSnapshot(sessions);
    }

    internal string GetOwnedResourceDiagnostics(bool includeResources = true)
        => SnapshotOwnedResourceAudit().ToDiagnosticText(includeResources);

    /// <summary>创建 ModLoader 并指定 Mods 目录</summary>
    public ModLoader(string modsDirectory)
        : this(modsDirectory, null)
    {
    }

    public ModLoader(string modsDirectory, ModHostPathPolicy? hostPathPolicy)
    {
        _modsDirectory = Path.GetFullPath(modsDirectory);
        _nativeShadowRoot = Path.Combine(_modsDirectory, NativeShadowDirectoryName);
        _nativeDataRoot = Path.Combine(_modsDirectory, NativeDataDirectoryName);
        _hostPathPolicy = hostPathPolicy ?? new ModHostPathPolicy();
        _modsView = _mods.AsReadOnly();
    }

    /// <summary>
    /// Host-owned subdirectories of the Mods directory. Both hold generated state, never a
    /// MOD, so the scanner must not treat them as discoverable MOD folders.
    /// </summary>
    private static bool IsHostOwnedDirectory(string directory)
    {
        var name = Path.GetFileName(directory);
        return string.Equals(name, NativeShadowDirectoryName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, NativeDataDirectoryName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Per-domain filesystem roots for an Android Managed MOD. The MOD directory stays
    /// read-only (execution runs from the shadow package), and every writable root lives
    /// under a Host-owned per-MOD directory so one MOD cannot reach another's files.
    /// </summary>
    internal ModDataDomainPathRoots BuildDomainPathRoots(string modId, string sourceDirectory)
    {
        var modDataRoot = Path.Combine(_nativeDataRoot, SanitizeModDirectoryName(modId));
        return new ModDataDomainPathRoots
        {
            InstallRoot = sourceDirectory,
            ConfigRoot = Path.Combine(modDataRoot, "config"),
            CacheRoot = Path.Combine(modDataRoot, "cache"),
            LogRoot = Path.Combine(modDataRoot, "log"),
            TempRoot = Path.Combine(modDataRoot, "temp"),
            DataOverlayRoot = Path.Combine(modDataRoot, "data"),
            SharedReadOnlyRoots = _hostPathPolicy.SharedReadOnlyRoots,
            SharedWritableRoots = _hostPathPolicy.SharedWritableRoots,
            HostProtectedRoots = _hostPathPolicy.HostProtectedRoots
        };
    }

    /// <summary>
    /// MOD IDs reach the filesystem here, so anything that could escape the data root or
    /// collide across MODs is replaced rather than trusted.
    /// </summary>
    private static string SanitizeModDirectoryName(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        var invalid = Path.GetInvalidFileNameChars();
        var buffer = new char[modId.Length];
        for (var i = 0; i < modId.Length; i++)
        {
            var c = modId[i];
            buffer[i] = c == '.' || Array.IndexOf(invalid, c) >= 0 ? '_' : c;
        }
        var name = new string(buffer).Trim('_');
        return name.Length == 0 ? "_" : name;
    }

    internal static Type? ResolvePluginType(Assembly assembly)
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
        lock (_transitionGate)
            ScanModsCore();
    }

    private void ScanModsCore()
    {
        // 保存当前运行状态和原生 MOD 的 load context（扫描后恢复）。
        // Native contexts with process-lifetime hooks must also survive a rescan.
        var previousMods = _mods.ToArray();
        var runtimeStates = new Dictionary<string, (IModPlugin? PluginInstance, bool IsEnabled,
            ModLoadState LoadState, string? LoadError, float LoadProgress, string LoadStage,
            object? LoaderData, string LoaderKind, string FolderPath,
            ModRuntimeSession RuntimeSession)>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in previousMods.Where(mod =>
                     mod.LoadState is ModLoadState.Loading or ModLoadState.Loaded or ModLoadState.Error ||
                     mod.LoaderData is NativeModLoadState))
        {
            if (!runtimeStates.TryAdd(
                    existing.Id,
                    (existing.PluginInstance,
                     existing.IsEnabled,
                     existing.LoadState,
                     existing.LoadError,
                     existing.LoadProgress,
                     existing.LoadStage,
                      existing.LoaderData,
                      existing.LoaderKind,
                      existing.FolderPath,
                      existing.RuntimeSession)))
            {
                Logger.Error(nameof(ModLoader),
                    $"duplicate MOD id retained only once during rescan: {existing.Id}");
            }
        }

        _mods.Clear();

        if (!Directory.Exists(_modsDirectory))
        {
            Directory.CreateDirectory(_modsDirectory);
            Logger.Info(nameof(ModLoader), L10n.Get("Log_DirCreated", _modsDirectory));
            ReleaseOrphanedRuntimeStates(previousMods, Array.Empty<ModEntry>());
            return;
        }

        foreach (var dir in Directory.GetDirectories(_modsDirectory).Where(dir =>
                     !IsHostOwnedDirectory(dir)))
        {
            var mod = DiscoverMod(dir);
            if (mod != null)
            {
                if (string.IsNullOrWhiteSpace(mod.Id))
                {
                    Logger.Error(nameof(ModLoader), $"MOD rejected because its ID is empty: {dir}");
                    ReleaseRejectedNativeMod(mod);
                    continue;
                }
                if (_mods.Any(existing =>
                        string.Equals(existing.Id, mod.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    Logger.Error(nameof(ModLoader),
                        $"MOD rejected because ID is already used: {mod.Id} ({dir})");
                    ReleaseRejectedNativeMod(mod);
                    continue;
                }

                // 恢复之前已加载的状态
                if (runtimeStates.TryGetValue(mod.Id, out var state))
                {
                    if (HasSameRuntimeIdentity(mod, state.LoaderKind, state.FolderPath))
                    {
                        if (mod.LoaderData is NativeModLoadState discoveredNative &&
                            !ReferenceEquals(discoveredNative, state.LoaderData))
                            discoveredNative.ReleaseContext();
                        mod.PluginInstance = state.PluginInstance;
                        mod.IsEnabled = state.IsEnabled;
                        mod.LoadState = state.LoadState;
                        mod.LoadError = state.LoadError;
                        mod.LoadProgress = state.LoadProgress;
                        mod.LoadStage = state.LoadStage;
                        mod.LoaderData = state.LoaderData;
                        mod.RuntimeSession = state.RuntimeSession;
                    }
                    else
                    {
                        Logger.Warn(nameof(ModLoader),
                            $"runtime state not restored because MOD identity changed: {mod.Id} " +
                            $"{state.LoaderKind} -> {mod.LoaderKind}");
                    }
                }
                _mods.Add(mod);
                Logger.Info(nameof(ModLoader), L10n.Get("Log_ModFound", mod.Name, mod.Id));
            }
        }

        ReleaseOrphanedRuntimeStates(previousMods, _mods);

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

        var entryDll = Directory.GetFiles(folderPath, "*.dll")
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == dirName)
            ?? Directory.GetFiles(folderPath, "*.dll")
                .FirstOrDefault(f => !Path.GetFileNameWithoutExtension(f).Equals("StArray.ModManager", StringComparison.OrdinalIgnoreCase));

        // Info.json is shared by several Android MOD packages for display/update metadata.
        // Prove the actual plugin shape first so an Android IModPlugin cannot be routed to the
        // PC compatibility loader merely because that file happens to be present.
        string? nativePluginTypeName = null;
        string? nativeProbeReason = null;
        var nativePluginProven = entryDll != null &&
            NativeModMetadataDescriptor.TryReadPluginTypeName(
                entryDll,
                out nativePluginTypeName,
                out nativeProbeReason);

        if (nativePluginProven && nativePluginTypeName != null)
        {
            Logger.Info(
                nameof(ModLoader),
                $"native IModPlugin takes precedence over PcModCompat manifest: " +
                $"mod={dirName} type={nativePluginTypeName}");
        }
        else if (!string.IsNullOrEmpty(nativeProbeReason))
        {
            Logger.Debug(nameof(ModLoader),
                $"native plugin probe unavailable for {dirName}: {nativeProbeReason}");
        }

        PcModManifest? parsedPcManifest;
        string? pcError;
        var hasPcManifest = PcModManifestReader.TryRead(
            folderPath,
            out parsedPcManifest,
            out pcError);
        if (!nativePluginProven && hasPcManifest && parsedPcManifest != null)
        {
            Logger.Info(nameof(ModLoader), $"PcModCompat manifest found: {parsedPcManifest.Id} ({parsedPcManifest.Kind})");
            return PcCompatModPlugin.CreateEntry(parsedPcManifest);
        }

        if (!nativePluginProven && !string.IsNullOrEmpty(pcError))
            Logger.Warn(nameof(ModLoader), $"PcModCompat manifest ignored for {dirName}: {pcError}");

        if (entryDll == null) return null;

        try
        {
            NativeModShadowPackage? shadowPackage = null;
            var metadataAssemblyPath = entryDll;
            if (NativeModShadowRewriteRuntime.IsEnabled)
            {
                shadowPackage = NativeModShadowPackage.Prepare(
                    _nativeShadowRoot,
                    folderPath,
                    entryDll);
                metadataAssemblyPath = shadowPackage.EntryAssemblyPath;
            }
            NativeModMetadataDescriptor? descriptor = null;
            if (NativeModMetadataDescriptor.TryRead(
                    metadataAssemblyPath,
                    out var staticallyProvenDescriptor,
                    out var descriptorReason) &&
                staticallyProvenDescriptor != null)
            {
                descriptor = staticallyProvenDescriptor;
            }
            else if (nativePluginProven && nativePluginTypeName != null)
            {
                // A proven native entry must not fall into LegacyReadOnly merely because an
                // identity getter uses compiler-generated static data or another opaque but
                // domain-safe implementation. Info.json is consumed as display metadata only;
                // with no manifest, the folder name remains the discovery identity. The real
                // plugin getters run only after BeginLoad has bound the isolated domain.
                descriptor = NativeModMetadataDescriptor.CreateDiscoveryFallback(
                    nativePluginTypeName,
                    parsedPcManifest?.Id ?? dirName,
                    parsedPcManifest?.DisplayName ?? dirName,
                    parsedPcManifest?.Version ?? "0.0.0",
                    parsedPcManifest?.Author ?? string.Empty);
                Logger.Warn(
                    nameof(ModLoader),
                    $"native metadata-only identity unavailable mod={dirName}: " +
                    $"{descriptorReason ?? "unknown reason"}; using host metadata fallback " +
                    "and deferring identity getters to isolated load");
            }

            if (descriptor != null)
            {
                var runtimeSession = new ModRuntimeSession();
                var state = new NativeModLoadState(
                    descriptor.Id,
                    descriptor.PluginTypeName,
                    entryDll,
                    runtimeSession,
                    shadowPackage);
                Logger.Info(
                    nameof(ModLoader),
                    $"native metadata-only discovery mod={descriptor.Id} " +
                    $"type={descriptor.PluginTypeName} " +
                    (shadowPackage == null
                        ? "mode=direct "
                        : $"cache={shadowPackage.CacheKey} ") +
                    $"metadata={(string.IsNullOrWhiteSpace(descriptorReason) ? "static" : "host-fallback")}");
                return new ModEntry
                {
                    Id = descriptor.Id,
                    Name = descriptor.Name,
                    Version = descriptor.Version,
                    Author = descriptor.Author,
                    Description = descriptor.Description,
                    Dependencies = descriptor.Dependencies.ToList(),
                    FolderPath = folderPath,
                    EntryPoint = entryDll,
                    PluginInstance = null,
                    LoaderData = state,
                    LoaderKind = ModEntry.NativeLoaderKind,
                    RuntimeSession = runtimeSession
                };
            }

            Logger.Warn(
                nameof(ModLoader),
                $"native metadata-only discovery unavailable mod={dirName}: " +
                $"{descriptorReason ?? "unknown reason"}; native entry was not proven; " +
                "using LegacyReadOnly discovery");
            var context = new NativeModAssemblyLoadContext(
                dirName,
                metadataAssemblyPath);
            try
            {
                var assembly = context.LoadFromAssemblyPath(metadataAssemblyPath);
                var pluginType = ResolvePluginType(assembly);
                if (pluginType == null)
                {
                    context.Unload();
                    return null;
                }

                // Metadata discovery and actual loading share this one instance. This prevents
                // constructors and static registration from running once during scan and again
                // during enable.
                var plugin = (IModPlugin)Activator.CreateInstance(pluginType)!;
                context.ReconcileObservedUnmanagedLibraries();
                var runtimeSession = new ModRuntimeSession();
                var state = new NativeModLoadState(
                    entryDll,
                    context,
                    assembly,
                    plugin,
                    runtimeSession,
                    shadowPackage);
                Logger.Info(
                    nameof(ModLoader),
                    shadowPackage == null
                        ? $"native direct package mod={plugin.Id}"
                        : $"native shadow package mod={plugin.Id} cache={shadowPackage.CacheKey} " +
                          $"hit={(shadowPackage.CacheHit ? 1 : 0)} " +
                          $"assemblies={shadowPackage.Assemblies.Count}");
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
                    PluginInstance = plugin,
                    LoaderData = state,
                    LoaderKind = ModEntry.NativeLoaderKind,
                    RuntimeSession = runtimeSession
                };
            }
            catch
            {
                context.Unload();
                throw;
            }
        }
        catch (Exception ex)
        {
            LogDiscoveryFailure(dirName, ex);
            return null;
        }
    }

    /// <summary>
    /// 加载指定的 Mod
    /// </summary>
    public bool LoadMod(ModEntry mod)
    {
        lock (_transitionGate)
        lock (_transitionLocks.GetValue(mod, static _ => new object()))
            return LoadModCore(mod);
    }

    private bool LoadModCore(ModEntry mod)
    {
        if (mod.LoadState == ModLoadState.Loaded)
        {
            Logger.Info(nameof(ModLoader), L10n.Get("Log_ModAlreadyLoaded", mod.Name));
            return true;
        }
        if (mod.LoadState == ModLoadState.Loading)
            return true;

        var runtimeSnapshot = mod.RuntimeSession.Snapshot();
        if (runtimeSnapshot.State == ModRuntimeLifecycleState.Active)
        {
            // A rescan or a notification failure can leave a fresh ModEntry carrying a
            // generation that was already published. Never call BeginLoad again: that would
            // either duplicate hooks or produce the misleading "state=Active" failure.
            if (mod.PluginInstance == null)
            {
                mod.LoadState = ModLoadState.Error;
                mod.IsEnabled = false;
                mod.LoadStage = "ActiveRuntimeWithoutPlugin";
                mod.LoadError =
                    $"MOD runtime is already active but plugin instance is unavailable " +
                    $"generation={runtimeSnapshot.Key.Generation}; restart the app before retrying.";
                Logger.Error(nameof(ModLoader),
                    $"active runtime has no plugin instance mod={mod.Id} " +
                    $"generation={runtimeSnapshot.Key.Generation}");
                NotifyModStateChanged(mod);
                return false;
            }

            mod.IsEnabled = true;
            mod.LoadState = ModLoadState.Loaded;
            mod.LoadError = null;
            mod.LoadProgress = 1;
            mod.LoadStage = "Ready";
            Logger.Warn(nameof(ModLoader),
                $"reconciled already-active MOD runtime mod={mod.Id} " +
                $"generation={runtimeSnapshot.Key.Generation}; duplicate load ignored");
            NotifyModStateChanged(mod);
            return true;
        }

        if (runtimeSnapshot.State is
                ModRuntimeLifecycleState.Retiring or
                ModRuntimeLifecycleState.Quiescing)
        {
            mod.IsEnabled = false;
            mod.LoadStage = "Transitioning";
            mod.LoadError =
                $"MOD runtime is still transitioning state={runtimeSnapshot.State} " +
                $"generation={runtimeSnapshot.Key.Generation}; retry after it finishes.";
            Logger.Warn(nameof(ModLoader),
                $"load rejected during runtime transition mod={mod.Id} " +
                $"state={runtimeSnapshot.State} generation={runtimeSnapshot.Key.Generation}");
            NotifyModStateChanged(mod);
            return false;
        }

        HookHelper.ResumeProcessLifetimeHookRegistration(mod.RuntimeOwnerId);
        if (mod.LoadState == ModLoadState.NotLoaded &&
            mod.PluginInstance != null &&
            HookHelper.HasProcessLifetimeHooks(mod.RuntimeKey))
        {
            try
            {
                VerifyRetainedIsolationManifest(mod);
            }
            catch (Exception ex)
            {
                mod.LoadState = ModLoadState.NotLoaded;
                mod.IsEnabled = false;
                mod.LoadError = ex.Message;
                mod.LoadStage = "Suspended";
                Logger.Warn(
                    nameof(ModLoader),
                    $"{mod.Name} retained generation cannot resume after isolation identity change: " +
                    ToSingleLine(ex.Message));
                NotifyModStateChanged(mod);
                return false;
            }
            if (!mod.RuntimeSession.TryResume(out var resumedKey))
            {
                var snapshot = mod.RuntimeSession.Snapshot();
                Logger.Warn(
                    nameof(ModLoader),
                    $"{mod.Name} cannot resume runtime state={snapshot.State} " +
                    $"generation={snapshot.Key.Generation} callbacks={snapshot.ActiveCallbacks} " +
                    $"operations={snapshot.ActiveOperations}");
                return false;
            }
            if (!HookHelper.ResumeNativeOperationGeneration(resumedKey))
            {
                if (mod.RuntimeSession.TryBeginRetirement(resumedKey) &&
                    mod.RuntimeSession.WaitForQuiescence(resumedKey, TimeSpan.Zero))
                {
                    mod.RuntimeSession.TryCompleteSuspension(resumedKey);
                }
                Logger.Warn(
                    nameof(ModLoader),
                    $"{mod.Name} cannot resume native operation generation=" +
                    resumedKey.Generation);
                return false;
            }
            HookHelper.ResumeProcessLifetimeHooks(resumedKey);
            BehaviourManager.ResumeOwner(mod.RuntimeOwnerId);
            mod.IsEnabled = true;
            mod.LoadState = ModLoadState.Loaded;
            mod.LoadError = null;
            mod.LoadProgress = 1;
            mod.LoadStage = string.Empty;
            Logger.Info(
                nameof(ModLoader),
                $"{mod.Name} resumed with process-lifetime native hooks " +
                $"generation={resumedKey.Generation}");
            NotifyModStateChanged(mod);
            return true;
        }

        var runtimeKey = default(ModRuntimeKey);
        try
        {
            runtimeKey = mod.RuntimeSession.BeginLoad(mod.LoaderKind, mod.Id);
            if (!mod.RuntimeSession.TrySetTrustedDependencies(runtimeKey, mod.Dependencies))
            {
                mod.RuntimeSession.TryAbortLoad(runtimeKey);
                throw new InvalidOperationException(
                    $"MOD dependency metadata could not be bound to generation={runtimeKey.Generation}.");
            }
            EnsureNativeShadowState(mod);
            BindBootstrapIsolationManifest(mod, runtimeKey);
            if (mod.LoaderData is NativeModLoadState nativeState)
                nativeState.BindRuntimeKey(runtimeKey);
            if (!HookHelper.OpenNativeOperationGeneration(runtimeKey))
            {
                mod.RuntimeSession.TryAbortLoad(runtimeKey);
                throw new InvalidOperationException(
                    $"Native operation generation could not open for {mod.Id} " +
                    $"generation={runtimeKey.Generation}.");
            }
        }
        catch (Exception ex)
        {
            if (runtimeKey.IsValid)
                mod.RuntimeSession.TryAbortLoad(runtimeKey);
            if (mod.LoaderData is NativeModLoadState failedNativeState &&
                (!runtimeKey.IsValid || !HookHelper.HasProcessLifetimeHooks(runtimeKey)))
            {
                failedNativeState.ReleaseContext();
                mod.PluginInstance = null;
            }
            mod.LoadState = ModLoadState.Error;
            mod.LoadError = ex.Message;
            LogLoadFailure(mod, ex);
            NotifyModStateChanged(mod);
            return false;
        }

        mod.LoadState = ModLoadState.Loading;
        mod.LoadError = null;
        NotifyModStateChanged(mod);
        var pluginLifecycleStarted = false;

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
                    pluginLifecycleStarted = true;
                    using (HookHelper.EnterOwnerScope(
                               mod.RuntimeOwnerId,
                               mod.RuntimeSession,
                               runtimeKey))
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
                    NotifyModStateChanged(mod);
                    return true;
                }

                pluginLifecycleStarted = true;
                using (HookHelper.EnterOwnerScope(
                           mod.RuntimeOwnerId,
                           mod.RuntimeSession,
                           runtimeKey))
                    plugin.OnLoad();
                MarkLoaded(mod, L10n.Get("Log_PcCompatLoadSuccess", mod.Name));
                NotifyModStateChanged(mod);
                return true;
            }

            if (mod.LoaderData is NativeModLoadState nativeState)
            {
                // ReleaseContext deliberately keeps the entry descriptor. A later enable must
                // create a fresh collectible ALC instead of falling through to Assembly.LoadFrom.
                using (HookHelper.EnterOwnerScope(
                           mod.RuntimeOwnerId,
                           mod.RuntimeSession,
                           runtimeKey))
                {
                    var plugin = nativeState.EnsureLoaded(
                        mod.RuntimeSession.SnapshotIsolationManifest().Manifest);
                    mod.PluginInstance = plugin;
                    pluginLifecycleStarted = true;
                    plugin.OnLoad();
                }

                if (mod.PluginInstance is IModSettings settings)
                    ModManagerUI.LoadSettings(mod, settings);

                Logger.Info(nameof(ModLoader),
                    L10n.Get("Log_ModEntryExecuted", mod.Name) +
                    $" context={nativeState.Assembly?.GetName().Name}");
                MarkLoaded(mod, L10n.Get("Log_ModLoadSuccess", mod.Name));
                NotifyModStateChanged(mod);
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
                    pluginLifecycleStarted = true;
                    using (HookHelper.EnterOwnerScope(
                               mod.RuntimeOwnerId,
                               mod.RuntimeSession,
                               runtimeKey))
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

            MarkLoaded(mod, L10n.Get("Log_ModLoadSuccess", mod.Name));
        }
        catch (Exception ex)
        {
            HandleLoadFailure(
                mod,
                runtimeKey,
                ex,
                pluginLifecycleStarted);
        }

        NotifyModStateChanged(mod);
        return mod.LoadState == ModLoadState.Loaded;
    }

    /// <summary>
    /// 卸载指定的 Mod
    /// </summary>
    public void UnloadMod(ModEntry mod)
    {
        lock (_transitionGate)
        lock (_transitionLocks.GetValue(mod, static _ => new object()))
            UnloadModCore(mod);
    }

    private void UnloadModCore(ModEntry mod)
    {
        if (mod.LoadState is not (ModLoadState.Loaded or ModLoadState.Loading)) return;

        var runtimeKey = mod.EnsureRuntimeActive();
        var runtimeStateBeforeRetirement = mod.RuntimeSession.Snapshot().State;
        if (!mod.RuntimeSession.TryBeginRetirement(runtimeKey))
        {
            var snapshot = mod.RuntimeSession.Snapshot();
            throw new InvalidOperationException(
                $"MOD runtime retirement rejected state={snapshot.State} " +
                $"generation={snapshot.Key.Generation} callbacks={snapshot.ActiveCallbacks} " +
                $"operations={snapshot.ActiveOperations}.");
        }
        HookHelper.BlockProcessLifetimeHookRegistration(mod.RuntimeOwnerId);
        var hasProcessLifetimeHooks = HookHelper.HasProcessLifetimeHooks(runtimeKey);
        var hasUntrackedProcessLifetimeCallbacks =
            HookHelper.HasUntrackedProcessLifetimeCallbacks(runtimeKey);
        var supportsLogicalRetirement =
            !hasUntrackedProcessLifetimeCallbacks &&
            (mod.PluginInstance is ILogicalProcessLifetimeHookRetirement ||
             HookHelper.SupportsOwnerScopedHookLifecycle);
        Logger.Info(
            nameof(ModLoader),
            $"[DEBUG-kv-unload-v1] request mod={mod.Id} state={mod.LoadState} " +
            $"enabled={mod.IsEnabled} processHooks={hasProcessLifetimeHooks} " +
            $"untrackedCallbacks={hasUntrackedProcessLifetimeCallbacks} " +
            $"logicalRetire={supportsLogicalRetirement} " +
            $"plugin={mod.PluginInstance?.GetType().FullName ?? "<null>"} " +
            $"tid={Environment.CurrentManagedThreadId}");

        try
        {
            EnsureNativeOperationsQuiesced(mod, runtimeKey);
        }
        catch
        {
            mod.RuntimeSession.TryCancelRetirement(
                runtimeKey,
                runtimeStateBeforeRetirement);
            HookHelper.ResumeProcessLifetimeHookRegistration(mod.RuntimeOwnerId);
            HookHelper.ResumeNativeOperationGeneration(runtimeKey);
            throw;
        }

        if (hasProcessLifetimeHooks && !supportsLogicalRetirement)
        {
            if (mod.LoadState == ModLoadState.Loading)
            {
                mod.RuntimeSession.TryCancelRetirement(
                    runtimeKey,
                    runtimeStateBeforeRetirement);
                HookHelper.ResumeProcessLifetimeHookRegistration(mod.RuntimeOwnerId);
                HookHelper.ResumeNativeOperationGeneration(runtimeKey);
                Logger.Warn(
                    nameof(ModLoader),
                    $"{mod.Name} cannot be cancelled after installing process-lifetime native hooks");
                return;
            }

            try
            {
                EnsureProcessLifetimeHooksSuspended(mod, runtimeKey);
                EnsureRuntimeCallbacksQuiesced(mod, runtimeKey);
            }
            catch
            {
                mod.RuntimeSession.TryCancelRetirement(
                    runtimeKey,
                    runtimeStateBeforeRetirement);
                HookHelper.ResumeProcessLifetimeHookRegistration(mod.RuntimeOwnerId);
                HookHelper.ResumeProcessLifetimeHooks(runtimeKey);
                HookHelper.ResumeNativeOperationGeneration(runtimeKey);
                throw;
            }
            BehaviourManager.SuspendOwner(mod.RuntimeOwnerId);
            if (!mod.RuntimeSession.TryCompleteSuspension(runtimeKey))
                throw new InvalidOperationException("MOD runtime suspension could not be committed.");
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
            LogOwnedResourceAudit("suspend");
            NotifyModStateChanged(mod);
            return;
        }

        var wasLoading = mod.LoadState == ModLoadState.Loading;
        if (mod.PluginInstance is IAsyncModPlugin asyncPlugin && wasLoading)
            asyncPlugin.CancelLoad();
        if (wasLoading)
            CompletePendingAsyncLoad();
        try
        {
            if (hasProcessLifetimeHooks)
                EnsureProcessLifetimeHooksSuspended(mod, runtimeKey);
            EnsureRuntimeCallbacksQuiesced(mod, runtimeKey);
        }
        catch
        {
            mod.RuntimeSession.TryCancelRetirement(
                runtimeKey,
                runtimeStateBeforeRetirement);
            HookHelper.ResumeProcessLifetimeHookRegistration(mod.RuntimeOwnerId);
            if (hasProcessLifetimeHooks)
                HookHelper.ResumeProcessLifetimeHooks(runtimeKey);
            HookHelper.ResumeNativeOperationGeneration(runtimeKey);
            throw;
        }
        if (mod.PluginInstance != null)
        {
            Logger.Info(
                nameof(ModLoader),
                $"[DEBUG-kv-unload-v1] route=onunload-enter mod={mod.Id} " +
                $"tid={Environment.CurrentManagedThreadId}");
            try
            {
                using (HookHelper.EnterOwnerScope(
                           mod.RuntimeOwnerId,
                           mod.RuntimeSession,
                           runtimeKey))
                    mod.PluginInstance.OnUnload();
            }
            catch (Exception exception)
            {
                mod.RuntimeSession.TryCancelRetirement(
                    runtimeKey,
                    runtimeStateBeforeRetirement);
                HookHelper.ResumeProcessLifetimeHookRegistration(mod.RuntimeOwnerId);
                if (hasProcessLifetimeHooks)
                    HookHelper.ResumeProcessLifetimeHooks(runtimeKey);
                HookHelper.ResumeNativeOperationGeneration(runtimeKey);
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
        if (hasProcessLifetimeHooks)
            EnsureProcessLifetimeHooksSuspended(mod, runtimeKey);
        hasProcessLifetimeHooks = HookHelper.HasProcessLifetimeHooks(runtimeKey);
        if (hasProcessLifetimeHooks)
            BehaviourManager.SuspendOwner(mod.RuntimeOwnerId);
        else
        {
            EnsureNativeOperationGenerationRetired(mod, runtimeKey);
            BehaviourManager.RetireOwner(mod.RuntimeOwnerId);
            ModOwnedResourceRegistry.Retire(runtimeKey);
            // Drop this generation's UI fault/quarantine bookkeeping so a reload starts clean.
            UiOwnerScope.Release(mod.RuntimeOwnerId, runtimeKey.Generation);
            // Direct Link: drop links on both sides so a retired generation can neither be
            // entered as a Provider nor keep driving one as a Consumer.
            ModDirectLinkGate.ReleaseLinksFor(runtimeKey);
        }
        if (mod.LoaderData is NativeModLoadState nativeState)
        {
            if (hasProcessLifetimeHooks)
            {
                Logger.Warn(nameof(ModLoader),
                    $"native MOD context retained after logical unload: {mod.Id}");
                mod.PluginInstance = nativeState.Plugin;
            }
            else
            {
                if (mod.PluginInstance is IDisposable disposable)
                    disposable.Dispose();
                nativeState.ReleaseContext();
                mod.PluginInstance = null;
            }
        }
        else
        {
            mod.PluginInstance = mod.LoaderData is PcModManifest manifest
                ? new PcCompatModPlugin(manifest)
                : null;
        }
        var runtimeTransitionCommitted = hasProcessLifetimeHooks
            ? mod.RuntimeSession.TryCompleteSuspension(runtimeKey)
            : mod.RuntimeSession.TryCompleteRetirement(runtimeKey);
        if (!runtimeTransitionCommitted)
            throw new InvalidOperationException("MOD runtime retirement could not be committed.");
        mod.IsEnabled = false;
        mod.LoadState = ModLoadState.NotLoaded;
        mod.LoadProgress = 0;
        mod.LoadStage = string.Empty;
        Logger.Info(nameof(ModLoader), L10n.Get("Log_ModUnloaded", mod.Name));
        LogOwnedResourceAudit("unload");
        NotifyModStateChanged(mod);
    }

    private void LogOwnedResourceAudit(string reason)
    {
        var audit = SnapshotOwnedResourceAudit();
        var report = $"reason={reason}{Environment.NewLine}" +
                     audit.ToDiagnosticText(includeResources: false).TrimEnd();
        if (audit.HasLeaks)
            Logger.Error(nameof(ModLoader), report);
        else
            Logger.Info(nameof(ModLoader), report);
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
        lock (_transitionGate)
        {
            UpdatePendingLoadsCore();
            return;
        }
    }

    private void UpdatePendingLoadsCore()
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
                lock (_transitionLocks.GetValue(mod, static _ => new object()))
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
                        var runtimeKey = mod.EnsureRuntimeLoading();
                        using (HookHelper.EnterOwnerScope(
                                   mod.RuntimeOwnerId,
                                   mod.RuntimeSession,
                                   runtimeKey))
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
                            HandleLoadFailure(
                                mod,
                                mod.RuntimeKey,
                                ex,
                                pluginLifecycleStarted: true);
                            mod.LoadProgress = 1;
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

                    NotifyModStateChanged(mod);
                }
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

    private void NotifyModStateChanged(ModEntry mod)
    {
        try
        {
            OnModStateChanged?.Invoke(mod);
        }
        catch (Exception exception)
        {
            // State observers are UI/configuration observers. They must not turn an already
            // published runtime into a failed load or cause a second BeginLoad on retry.
            Logger.Warn(
                nameof(ModLoader),
                $"MOD state observer failed id={mod.Id} state={mod.LoadState}: " +
                $"{exception.GetType().Name}: {ToSingleLine(exception.Message)}");
        }
    }

    private static void LogLoadFailure(ModEntry mod, Exception exception)
    {
        Logger.Error(
            nameof(ModLoader),
            FormatExceptionChain(exception, $"load-failure mod={mod.Id}"));
    }

    private static void LogDiscoveryFailure(string modId, Exception exception)
    {
        var headline = L10n.Get(
            "Log_ModAssemblyError",
            modId,
            ToSingleLine(exception.Message));
        Logger.Error(
            nameof(ModLoader),
            $"{headline} {FormatExceptionChain(exception, $"discovery-failure mod={modId}")}");
    }

    private static string FormatExceptionChain(Exception exception, string prefix)
    {
        const int maxLogcatChars = 2800;
        const int maxExceptionDepth = 32;
        var chain = new List<Exception>(4);
        for (Exception? current = exception;
             current != null && chain.Count < maxExceptionDepth;
             current = current.InnerException)
        {
            chain.Add(current);
        }

        var root = chain[^1];
        var rootFrame = root.StackTrace?
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(frame => frame.Trim())
            .FirstOrDefault();
        var summary = new StringBuilder(512)
            .Append(prefix)
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

        if (chain.Count == maxExceptionDepth && root.InnerException != null)
            summary.Append(" -> <exception-chain-truncated>");
        if (!string.IsNullOrWhiteSpace(rootFrame))
            summary.Append(" rootAt=").Append(ToSingleLine(rootFrame));

        if (summary.Length > maxLogcatChars)
        {
            summary.Length = maxLogcatChars - 3;
            summary.Append("...");
        }

        return summary.ToString();
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

    private void EnsureNativeShadowState(ModEntry mod)
    {
        if (!string.Equals(mod.LoaderKind, ModEntry.NativeLoaderKind, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(mod.EntryPoint) ||
            !File.Exists(mod.EntryPoint) ||
            !NativeModShadowRewriteRuntime.IsEnabled)
        {
            return;
        }

        var entryPath = Path.GetFullPath(mod.EntryPoint);
        var sourceDirectory = Path.GetDirectoryName(entryPath)
                              ?? throw new InvalidDataException(
                                  "Native MOD entry assembly has no parent directory.");
        if (mod.LoaderData is NativeModLoadState { ShadowPackage: { } existingPackage })
        {
            var currentIdentity = ModIsolationManifestFactory.ReadAssemblyIdentity(entryPath);
            if (!ModIsolationManifestFactory.MatchesAssemblyIdentity(
                    existingPackage.OriginalEntryIdentity,
                    currentIdentity))
            {
                throw new InvalidDataException(
                    $"Native MOD source changed after discovery: {mod.Id}; " +
                    "rescan the MOD directory before loading.");
            }
        }
        var shadowPackage = NativeModShadowPackage.Prepare(
            _nativeShadowRoot,
            sourceDirectory,
            entryPath);
        if (!ModDataDomainRegistry.TryResolve(
                mod.RuntimeSession.DomainToken,
                out var domain))
        {
            throw new InvalidOperationException(
                $"Native MOD data domain is unavailable while binding shadow paths: {mod.Id}.");
        }
        domain.BindOriginalAssemblyLocations(shadowPackage.OriginalAssemblyLocations);
        domain.BindPathRoots(BuildDomainPathRoots(mod.Id, sourceDirectory));
        if (mod.LoaderData is NativeModLoadState state)
        {
            state.BindShadowPackage(shadowPackage);
            mod.PluginInstance = state.Plugin;
            return;
        }

        var created = new NativeModLoadState(
            mod.Id,
            null,
            entryPath,
            mod.RuntimeSession,
            shadowPackage);
        mod.LoaderData = created;
        mod.PluginInstance = null;
    }

    private static void BindBootstrapIsolationManifest(
        ModEntry mod,
        ModRuntimeKey runtimeKey)
    {
        if (string.IsNullOrWhiteSpace(mod.EntryPoint) || !File.Exists(mod.EntryPoint))
            return;

        var manifestPath = Path.Combine(mod.FolderPath, "isolation.json");
        var manifest = File.Exists(manifestPath)
            ? ModIsolationManifest.Read(manifestPath)
            : ModIsolationManifestFactory.CreateBootstrap(
                mod.Id,
                mod.LoaderKind,
                mod.EntryPoint);
        var actualIdentity = ModIsolationManifestFactory.ReadAssemblyIdentity(mod.EntryPoint);
        if (!ModIsolationManifestFactory.MatchesAssemblyIdentity(
                manifest.OriginalAssembly,
                actualIdentity))
        {
            throw new InvalidDataException(
                $"MOD isolation manifest does not match entry assembly: mod={mod.Id} " +
                $"manifest={manifest.OriginalAssembly.ModuleVersionId}/" +
                $"{manifest.OriginalAssembly.Sha256} " +
                $"actual={actualIdentity.ModuleVersionId}/{actualIdentity.Sha256}.");
        }
        if (mod.LoaderData is NativeModLoadState { ShadowPackage: { } shadowPackage })
        {
            shadowPackage.Verify();
            if (!ModIsolationManifestFactory.MatchesAssemblyIdentity(
                    shadowPackage.OriginalEntryIdentity,
                    actualIdentity))
            {
                throw new InvalidDataException(
                    $"Native MOD source changed after shadow package preparation: {mod.Id}; " +
                    "rescan the MOD directory before loading.");
            }
            if (manifest.ShadowAssembly != null &&
                !ModIsolationManifestFactory.MatchesAssemblyIdentity(
                    manifest.ShadowAssembly,
                    shadowPackage.EntryIdentity))
            {
                throw new InvalidDataException(
                    $"MOD isolation manifest shadow identity does not match the verified package: " +
                    $"{mod.Id}.");
            }
            manifest = manifest with
            {
                ShadowAssembly = shadowPackage.EntryIdentity,
                StaticMembers = MergeShadowStaticMembers(
                    manifest.StaticMembers,
                    shadowPackage.StaticMembers)
            };
        }
        if (!mod.RuntimeSession.TryBindIsolationManifest(
                runtimeKey,
                manifest,
                out var manifestHash))
        {
            throw new InvalidOperationException(
                $"MOD isolation manifest could not bind for {mod.Id} " +
                $"generation={runtimeKey.Generation}.");
        }
        Logger.Info(
            nameof(ModLoader),
            $"isolation manifest bound mod={mod.Id} generation={runtimeKey.Generation} " +
            $"hash={manifestHash} level={ModIsolationCapabilityLevel.Guarded}");
    }

    private static void VerifyRetainedIsolationManifest(ModEntry mod)
    {
        var snapshot = mod.RuntimeSession.SnapshotIsolationManifest();
        if (snapshot.Manifest is null || string.IsNullOrWhiteSpace(mod.EntryPoint) ||
            !File.Exists(mod.EntryPoint))
        {
            return;
        }

        var manifestPath = Path.Combine(mod.FolderPath, "isolation.json");
        var current = File.Exists(manifestPath)
            ? ModIsolationManifest.Read(manifestPath)
            : ModIsolationManifestFactory.CreateBootstrap(
                mod.Id,
                mod.LoaderKind,
                mod.EntryPoint);
        var actualIdentity = ModIsolationManifestFactory.ReadAssemblyIdentity(mod.EntryPoint);
        if (mod.LoaderData is NativeModLoadState { ShadowPackage: { } shadowPackage })
        {
            shadowPackage.Verify();
            if (!ModIsolationManifestFactory.MatchesAssemblyIdentity(
                    shadowPackage.OriginalEntryIdentity,
                    actualIdentity))
            {
                throw new InvalidDataException(
                    $"Native MOD source no longer matches its retained shadow package: {mod.Id}.");
            }
            current = current with
            {
                ShadowAssembly = shadowPackage.EntryIdentity,
                StaticMembers = MergeShadowStaticMembers(
                    current.StaticMembers,
                    shadowPackage.StaticMembers)
            };
        }
        if (!ModIsolationManifestFactory.MatchesAssemblyIdentity(
                current.OriginalAssembly,
                actualIdentity) ||
            !ModIsolationManifestFactory.MatchesAssemblyIdentity(
                snapshot.Manifest.OriginalAssembly,
                actualIdentity) ||
            !string.Equals(
                current.ComputeManifestHash(),
                snapshot.Hash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"retained MOD isolation manifest changed for {mod.Id}; " +
                "unload and reload the MOD to establish a new generation.");
        }
    }

    private static IReadOnlyList<ModIsolationStaticMemberRecord> MergeShadowStaticMembers(
        IReadOnlyList<ModIsolationStaticMemberRecord> declared,
        IReadOnlyList<ModIsolationStaticMemberRecord> rewritten)
    {
        if (rewritten.Count == 0)
            return declared;

        var merged = declared.ToDictionary(
            member => member.MemberIdentity,
            StringComparer.Ordinal);
        foreach (var member in rewritten)
        {
            if (merged.TryGetValue(member.MemberIdentity, out var existing))
            {
                if (existing.StaticSlotId != member.StaticSlotId ||
                    existing.Classification != ModStaticStateClassification.DomainMutable)
                {
                    throw new InvalidDataException(
                        $"Isolation manifest static classification does not match shadow rewrite: " +
                        $"{member.MemberIdentity}.");
                }
                continue;
            }
            merged.Add(member.MemberIdentity, member);
        }

        var slotCollision = merged.Values
            .GroupBy(member => member.StaticSlotId)
            .FirstOrDefault(group => group.Select(member => member.MemberIdentity)
                .Distinct(StringComparer.Ordinal).Skip(1).Any());
        if (slotCollision != null)
        {
            throw new InvalidDataException(
                $"Isolation manifest static slot collision: {slotCollision.Key}.");
        }
        return merged.Values
            .OrderBy(member => member.MemberIdentity, StringComparer.Ordinal)
            .ToArray();
    }

    private static void MarkLoaded(ModEntry mod, string logMessage)
    {
        var runtimeKey = mod.RuntimeKey;
        if (!mod.RuntimeSession.TryPublishActive(runtimeKey))
        {
            var snapshot = mod.RuntimeSession.Snapshot();
            throw new InvalidOperationException(
                $"MOD runtime active publication rejected state={snapshot.State} " +
                $"generation={snapshot.Key.Generation} callbacks={snapshot.ActiveCallbacks} " +
                $"operations={snapshot.ActiveOperations}.");
        }
        mod.IsEnabled = true;
        mod.LoadState = ModLoadState.Loaded;
        mod.LoadProgress = 1;
        mod.LoadStage = "Ready";
        Logger.Info(nameof(ModLoader), logMessage);
    }

    private static void EnsureRuntimeCallbacksQuiesced(ModEntry mod, ModRuntimeKey runtimeKey)
    {
        if (mod.RuntimeSession.WaitForQuiescence(
                runtimeKey,
                RuntimeCallbackQuiescenceTimeout))
            return;

        var snapshot = mod.RuntimeSession.Snapshot();
        var operations = mod.RuntimeSession.SnapshotOwnedOperations(runtimeKey);
        var operationDetails = operations.Count == 0
            ? "none"
            : string.Join(',', operations.Select(operation =>
                $"{operation.OperationId}:{operation.Name}:cancel=" +
                (operation.CancellationRequested ? "1" : "0")));
        throw new TimeoutException(
            $"MOD runtime callback quiescence timed out mod={mod.Id} " +
            $"generation={runtimeKey.Generation} callbacks={snapshot.ActiveCallbacks} " +
            $"operations={snapshot.ActiveOperations} pending=[{operationDetails}].");
    }

    private static void EnsureNativeOperationsQuiesced(
        ModEntry mod,
        ModRuntimeKey runtimeKey)
    {
        if (HookHelper.CancelNativeOperationsAndWait(
                runtimeKey,
                RuntimeCallbackQuiescenceTimeout))
        {
            return;
        }

        var active = HookHelper.GetActiveNativeOperationCount(runtimeKey);
        throw new TimeoutException(
            $"MOD native operation quiescence timed out mod={mod.Id} " +
            $"generation={runtimeKey.Generation} active={active}.");
    }

    private static void EnsureNativeOperationGenerationRetired(
        ModEntry mod,
        ModRuntimeKey runtimeKey)
    {
        if (HookHelper.RetireNativeOperationGeneration(runtimeKey))
            return;
        var active = HookHelper.GetActiveNativeOperationCount(runtimeKey);
        throw new InvalidOperationException(
            $"MOD native operation generation retirement failed mod={mod.Id} " +
            $"generation={runtimeKey.Generation} active={active}.");
    }

    private static void CloseUnusedNativeOperationGeneration(ModRuntimeKey runtimeKey)
    {
        if (!HookHelper.CancelNativeOperationsAndWait(runtimeKey, TimeSpan.Zero))
            return;
        HookHelper.RetireNativeOperationGeneration(runtimeKey);
    }

    private static void EnsureProcessLifetimeHooksSuspended(
        ModEntry mod,
        ModRuntimeKey runtimeKey)
    {
        if (HookHelper.SuspendProcessLifetimeHooks(runtimeKey))
            return;
        throw new TimeoutException(
            $"MOD native hook quiescence timed out mod={mod.Id} " +
            $"generation={runtimeKey.Generation}.");
    }

    private static void HandleLoadFailure(
        ModEntry mod,
        ModRuntimeKey runtimeKey,
        Exception exception,
        bool pluginLifecycleStarted)
    {
        HookHelper.BlockProcessLifetimeHookRegistration(mod.RuntimeOwnerId);
        var retainedHooks = HookHelper.HasProcessLifetimeHooks(runtimeKey);
        var nativeHooksSuspended = !retainedHooks ||
            HookHelper.SuspendProcessLifetimeHooks(runtimeKey);
        if (!nativeHooksSuspended)
        {
            Logger.Error(
                nameof(ModLoader),
                $"failed load native hook quiescence timed out mod={mod.Id} " +
                $"generation={runtimeKey.Generation}");
        }
        var retirementStarted = mod.RuntimeSession.TryBeginRetirement(runtimeKey);
        var quiesced = false;
        if (retirementStarted)
        {
            var nativeOperationsQuiesced = HookHelper.CancelNativeOperationsAndWait(
                runtimeKey,
                RuntimeCallbackQuiescenceTimeout);
            if (!nativeOperationsQuiesced)
            {
                Logger.Error(
                    nameof(ModLoader),
                    $"failed load native operation quiescence timed out mod={mod.Id} " +
                    $"generation={runtimeKey.Generation} active=" +
                    HookHelper.GetActiveNativeOperationCount(runtimeKey));
            }
            try
            {
                EnsureRuntimeCallbacksQuiesced(mod, runtimeKey);
                quiesced = nativeHooksSuspended && nativeOperationsQuiesced;
            }
            catch (Exception retirementError)
            {
                Logger.Error(
                    nameof(ModLoader),
                    $"failed load could not quiesce mod={mod.Id}: {retirementError.Message}");
            }
        }

        if (retainedHooks)
        {
            if (quiesced)
            {
                BehaviourManager.SuspendOwner(mod.RuntimeOwnerId);
                mod.RuntimeSession.TryCompleteSuspension(runtimeKey);
            }
            mod.LoadStage = "Suspended";
            mod.LoadError = exception.Message +
                            " Native hooks remain mapped; restart the app before retrying.";
        }
        else if (quiesced && HookHelper.RetireNativeOperationGeneration(runtimeKey))
        {
            CleanupFailedPlugin(mod, runtimeKey, pluginLifecycleStarted);
            BehaviourManager.RetireOwner(mod.RuntimeOwnerId);
            ModOwnedResourceRegistry.Retire(runtimeKey);
            UiOwnerScope.Release(mod.RuntimeOwnerId, runtimeKey.Generation);
            ModDirectLinkGate.ReleaseLinksFor(runtimeKey);
            if (!mod.RuntimeSession.TryCompleteRetirement(runtimeKey))
            {
                Logger.Error(
                    nameof(ModLoader),
                    $"failed load retirement commit rejected mod={mod.Id} " +
                    $"generation={runtimeKey.Generation}");
            }
            mod.LoadStage = "Failed";
            mod.LoadError = exception.Message;
        }
        else
        {
            if (!retirementStarted)
                mod.RuntimeSession.TryAbortLoad(runtimeKey);
            mod.LoadStage = "RestartRequired";
            mod.LoadError = exception.Message +
                            " MOD runtime did not retire cleanly; restart the app before retrying.";
            Logger.Error(
                nameof(ModLoader),
                $"failed load retained runtime mod={mod.Id} " +
                $"generation={runtimeKey.Generation} nativeOperations=" +
                HookHelper.GetActiveNativeOperationCount(runtimeKey));
        }

        mod.IsEnabled = false;
        mod.LoadState = ModLoadState.Error;
        LogLoadFailure(mod, exception);
    }

    private static void CleanupFailedPlugin(
        ModEntry mod,
        ModRuntimeKey runtimeKey,
        bool invokeOnUnload)
    {
        var plugin = mod.PluginInstance;
        if (plugin != null && invokeOnUnload)
        {
            try
            {
                using (HookHelper.EnterOwnerScope(
                           mod.RuntimeOwnerId,
                           mod.RuntimeSession,
                           runtimeKey))
                    plugin.OnUnload();
            }
            catch (Exception cleanupError)
            {
                Logger.Warn(
                    nameof(ModLoader),
                    $"failed load cleanup OnUnload threw mod={mod.Id}: {cleanupError.Message}");
            }
        }

        if (plugin is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception cleanupError)
            {
                Logger.Warn(
                    nameof(ModLoader),
                    $"failed load cleanup Dispose threw mod={mod.Id}: {cleanupError.Message}");
            }
        }

        if (mod.LoaderData is NativeModLoadState nativeState)
        {
            nativeState.ReleaseContext();
            mod.PluginInstance = null;
        }
        else if (mod.LoaderData is PcModManifest manifest)
        {
            mod.PluginInstance = new PcCompatModPlugin(manifest);
        }
        else
        {
            mod.PluginInstance = null;
        }
    }

    /// <summary>
    /// 添加一个新的 Mod 条目（手动创建）
    /// </summary>
    public ModEntry AddMod(ModEntry mod)
    {
        if (string.IsNullOrWhiteSpace(mod.Id))
            throw new ArgumentException("MOD ID cannot be empty.", nameof(mod));
        lock (_transitionGate)
        {
            if (_mods.Any(existing =>
                    string.Equals(existing.Id, mod.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A MOD with ID '{mod.Id}' is already registered.");
            _mods.Add(mod);
        }
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

        bool removed;
        lock (_transitionGate)
            removed = _mods.Remove(mod);
        if (removed)
            Logger.Info(nameof(ModLoader), L10n.Get("Log_ModRemoved", mod.Name));
        return removed;
    }

    private void ReleaseRejectedNativeMod(ModEntry mod)
    {
        if (mod.LoaderData is not NativeModLoadState state)
            return;

        // The rejected entry has never been enabled by this loader. Its state is
        // therefore not one of the previously retained process-hook states.
        state.ReleaseContext();
    }

    private void ReleaseOrphanedRuntimeStates(
        IReadOnlyList<ModEntry> previousMods,
        IReadOnlyList<ModEntry> currentMods)
    {
        foreach (var previous in previousMods)
        {
            if (currentMods.Any(current =>
                    string.Equals(current.Id, previous.Id, StringComparison.OrdinalIgnoreCase) &&
                    ReferenceEquals(current.LoaderData, previous.LoaderData)))
                continue;

            if (previous.LoadState is ModLoadState.Loading or ModLoadState.Loaded)
            {
                try
                {
                    UnloadMod(previous);
                }
                catch (Exception ex)
                {
                    Logger.Error(nameof(ModLoader),
                        $"orphaned MOD retirement failed mod={previous.Id}: {ex}");
                }
            }

            if (previous.LoaderData is NativeModLoadState state &&
                HookHelper.HasProcessLifetimeHooks(previous.RuntimeKey))
            {
                if (!_orphanedNativeStates.Contains(state))
                    _orphanedNativeStates.Add(state);
                Logger.Warn(nameof(ModLoader),
                    $"retaining removed native MOD context because process hooks remain: {previous.Id}");
            }
            else
            {
                if (previous.LoaderData is NativeModLoadState nativeState)
                    nativeState.ReleaseContext();
                // An abandoned PCCompat entry must not retain a fresh placeholder plugin
                // after its session has been unregistered by UnloadMod.
                if (previous.LoaderData is PcModManifest)
                    previous.PluginInstance = null;
            }
        }
    }

    private static bool HasSameRuntimeIdentity(
        ModEntry discovered,
        string previousLoaderKind,
        string previousFolderPath)
    {
        if (!string.Equals(
                discovered.LoaderKind,
                previousLoaderKind,
                StringComparison.Ordinal))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(discovered.FolderPath),
                Path.GetFullPath(previousFolderPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

}

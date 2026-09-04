using StArray.ModManager.Resources;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using IconFonts;
using ImGuiNET;
using StArray.ModManager.Behaviours;
using StArray.ModManager.Inspector;
using StArray.ModManager.Runtime;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Manager;

/// <summary>Mod 管理器 UI / Mod manager main UI — all ImGui interface logic</summary>
public partial class ModManagerUI
{
    private static readonly ConditionalWeakTable<Type, LegacyOverlayCapability> LegacyOverlayCapabilities = new();
    private readonly ModLoader _modManager;
    private readonly IModManagerPlatformServices _platform;
    private readonly List<string> _logMessages = new();
    private ModEntry? _selectedMod;
    private ModManagerConfig _config = new();
    private readonly string _configDir;

    private bool _showAddModPopup;
    private string? _expandedModId;
    private string? _externalSettingsModId;
    private bool _externalSettingsOverlayHidden;
    private int _backgroundLayerFailureCount;
    private int _behaviourLayerFailureCount;
    private int _foregroundLayerFailureCount;
    private int _handledImportSerial;
    private ModImportStatus _lastImportStatus;

    // 通知
    private string _toastMessage = string.Empty;
    private float _toastTimer;

    private bool _configApplied;
    private float _lastFrameTime;
    private int _pendingLoadPollActive;
    private string? _lastInputGateSettingsTrace;

    public bool RequiresRenderingWhenHidden
    {
        get
        {
            if (BehaviourManager.RequiresFrame)
                return true;

            if (_externalSettingsModId != null)
                return true;

            var mods = _modManager.Mods;
            for (var index = 0; index < mods.Count; ++index)
            {
                var mod = mods[index];
                if (mod is not { LoadState: ModLoadState.Loaded, PluginInstance: { } plugin })
                    continue;

                if (plugin is IPersistentModOverlay overlay)
                {
                    if (overlay.ShouldRenderWhenManagerHidden)
                        return true;
                    continue;
                }

                if (LegacyOverlayCapabilities.GetValue(
                        plugin.GetType(),
                        static type => new LegacyOverlayCapability(HasLegacyOverlayCallback(type)))
                    .RequiresRenderingWhenHidden)
                    return true;
            }

            return false;
        }
    }

    private static bool HasLegacyOverlayCallback(Type pluginType)
    {
        try
        {
            var map = pluginType.GetInterfaceMap(typeof(IModPlugin));
            for (var index = 0; index < map.InterfaceMethods.Length; index++)
            {
                var name = map.InterfaceMethods[index].Name;
                if (name is not nameof(IModPlugin.OnBackgroundGUI) and
                    not nameof(IModPlugin.OnForegroundGUI))
                    continue;

                if (map.TargetMethods[index].DeclaringType != typeof(IModPlugin))
                    return true;
            }
        }
        catch
        {
            // Invalid plugin metadata must not break the hidden render predicate.
        }

        return false;
    }

    private sealed class LegacyOverlayCapability(bool requiresRenderingWhenHidden)
    {
        public bool RequiresRenderingWhenHidden { get; } = requiresRenderingWhenHidden;
    }

    public bool IsOverlayVisible => _platform.IsOverlayVisible;

    /// <summary>初始化 UI，加载配置并扫描 Mod</summary>
    public ModManagerUI(ModLoader modManager, string configDir)
        : this(modManager, configDir, null)
    {
    }

    /// <summary>初始化 UI，加载配置并扫描 Mod</summary>
    public ModManagerUI(
        ModLoader modManager,
        string configDir,
        IModManagerPlatformServices? platform)
    {
        _modManager = modManager;
        _platform = platform ?? NullModManagerPlatformServices.Instance;
        Logger.OnLog += OnLogMessage;

        _configDir = configDir;
        _config = ModManagerConfig.Load(_configDir);
        L10n.SetLanguage(_config.Language);
        PcCompatTouchLaneMappingRuntime.SetTouchContactReuseDelayMilliseconds(
            _config.TouchKeyViewerContactReuseDelayMilliseconds);
        PcCompatTouchLaneMappingRuntime.SetMode(_config.TouchKeyViewerMappingMode);
        if (string.IsNullOrEmpty(_config.ModsDirectory))
            _config.ModsDirectory = _modManager.ModsDirectory;
        _modManager.ScanMods();
        RegisterModGlyphText();
        AutoEnableMods();
    }

    private void ApplyConfig()
    {
        var io = ImGui.GetIO();
        io.FontGlobalScale = _config.UiScale;
        var style = ImGui.GetStyle();
        float scale = LayoutScale;
        style.GrabMinSize = Math.Max(_config.GrabMinSize, 18f * scale);
        style.ScrollbarSize = Math.Max(_config.ScrollbarSize, 20f * scale);
        PcCompatTouchLaneMappingRuntime.SetTouchContactReuseDelayMilliseconds(
            _config.TouchKeyViewerContactReuseDelayMilliseconds);
        PcCompatTouchLaneMappingRuntime.SetMode(_config.TouchKeyViewerMappingMode);
        ApplyStyleScale();
    }

    private void ApplyStyleScale()
    {
        float scale = LayoutScale;
        var style = ImGui.GetStyle();
        style.FramePadding = new Vector2(8f * scale, 6f * scale);
        style.ItemSpacing = new Vector2(12f * scale, 8f * scale);
        style.ItemInnerSpacing = new Vector2(8f * scale, 6f * scale);
        style.FrameRounding = 4f * scale;
        style.WindowPadding = new Vector2(14f * scale, 12f * scale);
        style.CellPadding = new Vector2(8f * scale, 6f * scale);
        style.IndentSpacing = 18f * scale;
    }

    private static float LayoutScale => Math.Max(1f, ImGui.GetIO().FontGlobalScale / 2f);

    /// <summary>保存管理器全局配置</summary>
    public void SaveConfig()
    {
        _config.ModsDirectory = _modManager.ModsDirectory;
        _config.UiScale = ImGui.GetIO().FontGlobalScale;
        _config.GrabMinSize = ImGui.GetStyle().GrabMinSize;
        _config.ScrollbarSize = ImGui.GetStyle().ScrollbarSize;

        // 收集当前存在的 Mod 启用状态，清理已删除的
        var currentIds = _modManager.Mods.Select(m => m.Id).ToHashSet();
        _config.ModEnabled = _config.ModEnabled
            .Where(kv => currentIds.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var m in _modManager.Mods)
            _config.ModEnabled[m.Id] = m.IsEnabled;

        _config.Save(_configDir);
    }

    /// <summary>根据配置自动启用之前已开启的 Mod</summary>
    private void AutoEnableMods()
    {
        var started = _modManager.LoadConfiguredEnabledMods(_config.ModEnabled);
        if (started > 0)
            Logger.Info(nameof(ModManagerUI), $"auto-enabled configured mods count={started}");
    }

    private void OnLogMessage(Logger.Level level, string tag, string msg)
    {
        L10n.RegisterDynamicGlyphText(tag, msg);
        var prefix = level switch
        {
            Logger.Level.Error => "[ERROR]",
            Logger.Level.Warn  => "[WARN]",
            Logger.Level.Debug => "[DEBUG]",
            _                  => "[INFO]"
        };
        _logMessages.Add($"[{DateTime.Now:HH:mm:ss}] {prefix}[{tag}] {msg}");

        while (_logMessages.Count > 500)
            _logMessages.RemoveAt(0);
    }

    private void RegisterModGlyphText()
    {
        foreach (var mod in _modManager.Mods)
        {
            L10n.RegisterDynamicGlyphText(
                mod.Id,
                mod.Name,
                mod.Author,
                mod.Description,
                mod.Version,
                mod.LoaderKind,
                mod.EntryPoint,
                mod.FolderPath,
                mod.LoadError,
                mod.LoadStage,
                string.Join(' ', mod.Dependencies));
        }
    }

    /// <summary>
    /// 渲染所有 UI（由外部每帧调用）
    /// </summary>
    public void Render()
    {
        UpdatePendingLoadsGuarded();

        if (!_configApplied)
        {
            ApplyConfig();
            _configApplied = true;
        }

        var now = (float)ImGui.GetTime();
        var delta = _lastFrameTime > 0 ? now - _lastFrameTime : 1f / 60f;
        _lastFrameTime = now;
        BehaviourManager.ProcessPending();
        BehaviourManager.Update(delta);

        var managerVisible = _platform.IsOverlayVisible;
        PollOriginalSettingsSurface(managerVisible);
        managerVisible = _platform.IsOverlayVisible;
        var inputSurfaceActive = managerVisible || RequiresRenderingWhenHidden;
        if (inputSurfaceActive)
            _platform.BeginOverlayInputFrame();
        try
        {
            RegisterModGlyphText();
            L10n.RegisterDynamicGlyphText(
                _config.ModsDirectory,
                _toastMessage,
                _lastImportStatus.Message,
                _lastImportStatus.Path);
            RenderBackgroundLayer();

            if (managerVisible)
            {
                RenderMainWindow();
                RenderModSettingsWindow();
                RenderAddModPopup();
                RenderToast();
            }

            RenderBehaviours();
            RenderForegroundLayer();
        }
        finally
        {
            if (inputSurfaceActive)
                _platform.EndOverlayInputFrame();
        }
    }

    public void PollPendingLoadsWhenHidden()
    {
        if (_platform.IsOverlayVisible)
            return;

        UpdatePendingLoadsGuarded();
        PollOriginalSettingsSurface(managerVisible: false);
    }

    private void UpdatePendingLoadsGuarded()
    {
        if (Interlocked.Exchange(ref _pendingLoadPollActive, 1) != 0)
            return;

        try
        {
            _modManager.RequestPendingLoadUpdate();
        }
        finally
        {
            Volatile.Write(ref _pendingLoadPollActive, 0);
        }
    }

    private void PollOriginalSettingsSurface(bool managerVisible)
    {
        var modId = _externalSettingsModId;
        if (modId == null)
        {
            TraceInputGateSettings(managerVisible, null, null, "no-target");
            if (_platform.IsModalInputCaptureActive)
                _platform.SetModalInputCapture(false, blockUnityEventSystem: false);
            return;
        }
        var mod = _modManager.Mods.FirstOrDefault(candidate => candidate.Id == modId);
        if (mod?.PluginInstance is not IModOriginalSettingsSurface original)
        {
            TraceInputGateSettings(managerVisible, modId, null, "surface-missing");
            _platform.SetModalInputCapture(false, blockUnityEventSystem: false);
            _externalSettingsModId = null;
            if (_externalSettingsOverlayHidden)
                _platform.SetOverlayVisible(true);
            _externalSettingsOverlayHidden = false;
            return;
        }

        if (_platform.ConsumeModalCloseRequest())
            original.RequestCloseOriginalSettings();

        var snapshot = original.SnapshotOriginalSettings();
        TraceInputGateSettings(managerVisible, modId, snapshot, null);
        if (snapshot.State is ModOriginalSettingsState.Faulted or
            ModOriginalSettingsState.Unavailable)
        {
            _platform.SetModalInputCapture(false, blockUnityEventSystem: false);
            _externalSettingsModId = null;
            _externalSettingsOverlayHidden = false;
            _expandedModId = modId;
            _platform.SetOverlayVisible(true);
            return;
        }
        if (snapshot.State == ModOriginalSettingsState.Closed)
        {
            _platform.SetModalInputCapture(false, blockUnityEventSystem: false);
            _externalSettingsModId = null;
            _expandedModId = modId;
            if (_externalSettingsOverlayHidden)
                _platform.SetOverlayVisible(true);
            _externalSettingsOverlayHidden = false;
            return;
        }
        if (snapshot.State is ModOriginalSettingsState.Opening or
            ModOriginalSettingsState.Open)
        {
            var blockUnityEventSystem =
                snapshot.State == ModOriginalSettingsState.Opening ||
                snapshot.SurfaceKind != ModOriginalSettingsSurfaceKind.UnityCanvas;
            _platform.SetModalInputCapture(true, blockUnityEventSystem);
            if (!_externalSettingsOverlayHidden)
            {
                _externalSettingsOverlayHidden = true;
                _platform.SetOverlayVisible(false);
                return;
            }
        }
        if (managerVisible && _externalSettingsOverlayHidden)
        {
            original.RequestCloseOriginalSettings();
            _platform.SetModalInputCapture(false, blockUnityEventSystem: false);
            _externalSettingsModId = null;
            _externalSettingsOverlayHidden = false;
        }
    }

    private void TraceInputGateSettings(
        bool managerVisible,
        string? modId,
        ModOriginalSettingsSnapshot? snapshot,
        string? absenceReason)
    {
        var state = snapshot?.State.ToString() ?? absenceReason ?? "unknown";
        var error = snapshot?.Error;
        var errorHead = string.IsNullOrWhiteSpace(error)
            ? "none"
            : error!.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
        var signature =
            $"mod={modId ?? "<none>"} managerVisible={managerVisible} state={state} " +
            $"surface={snapshot?.SurfaceKind.ToString() ?? "none"} " +
            $"error={errorHead} overlayHidden={_externalSettingsOverlayHidden} " +
            $"overlayVisible={_platform.IsOverlayVisible} modalCapture={_platform.IsModalInputCaptureActive}";
        if (string.Equals(_lastInputGateSettingsTrace, signature, StringComparison.Ordinal))
            return;

        _lastInputGateSettingsTrace = signature;
        Logger.Info("PcCompatInputGate", signature);
    }

    private void RenderBackgroundLayer()
    {
        ImDrawListPtr bgDrawList;
        try
        {
            var viewport = ImGui.GetMainViewport();
            bgDrawList = ImGui.GetBackgroundDrawList(viewport);
        }
        catch (Exception ex)
        {
            if (_backgroundLayerFailureCount++ < 3)
                Logger.Warn(nameof(ModManagerUI), $"Background layer skipped: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var mods = _modManager.Mods;
        for (var index = 0; index < mods.Count; ++index)
        {
            var mod = mods[index];
            if (mod is not { LoadState: ModLoadState.Loaded, PluginInstance: not null })
                continue;

            try
            {
                if (!mod.TryEnterRuntimeCallback(out var callbackLease))
                    continue;
                using (callbackLease)
                using (HookHelper.EnterOwnerScope(
                           mod.RuntimeOwnerId,
                           mod.RuntimeSession,
                           mod.RuntimeKey))
                {
                    UiOwnerScope.TryDraw(
                        mod.RuntimeOwnerId,
                        mod.RuntimeKey.Generation,
                        "OnBackgroundGUI",
                        () => mod.PluginInstance!.OnBackgroundGUI(bgDrawList));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(nameof(ModManagerUI), $"OnBackgroundGUI failed for {mod.Name}: {ex}");
            }
        }
    }

    private void RenderForegroundLayer()
    {
        ImDrawListPtr fgDrawList;
        try
        {
            var viewport = ImGui.GetMainViewport();
            fgDrawList = ImGui.GetForegroundDrawList(viewport);
        }
        catch (Exception ex)
        {
            if (_foregroundLayerFailureCount++ < 3)
                Logger.Warn(nameof(ModManagerUI), $"Foreground layer skipped: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var mods = _modManager.Mods;
        for (var index = 0; index < mods.Count; ++index)
        {
            var mod = mods[index];
            if (mod is not { LoadState: ModLoadState.Loaded, PluginInstance: not null })
                continue;

            try
            {
                if (!mod.TryEnterRuntimeCallback(out var callbackLease))
                    continue;
                using (callbackLease)
                using (HookHelper.EnterOwnerScope(
                           mod.RuntimeOwnerId,
                           mod.RuntimeSession,
                           mod.RuntimeKey))
                {
                    UiOwnerScope.TryDraw(
                        mod.RuntimeOwnerId,
                        mod.RuntimeKey.Generation,
                        "OnForegroundGUI",
                        () => mod.PluginInstance!.OnForegroundGUI(fgDrawList));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(nameof(ModManagerUI), $"OnForegroundGUI failed for {mod.Name}: {ex}");
            }
        }
    }

    private void RenderBehaviours()
    {
        try
        {
            var viewport = ImGui.GetMainViewport();
            BehaviourManager.GUI(ImGui.GetBackgroundDrawList(viewport));
        }
        catch (Exception ex)
        {
            if (_behaviourLayerFailureCount++ < 3)
                Logger.Warn(nameof(ModManagerUI),
                    $"Behaviour layer skipped: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void RenderMainWindow()
    {
        float scale = LayoutScale;
        ImGui.SetNextWindowSize(new Vector2(840 * scale, 760 * scale), ImGuiCond.Once);
        ImGui.SetNextWindowSizeConstraints(new Vector2(720 * scale, 560 * scale), Vector2.One * 100000f);
        var open = true;
        ImGui.Begin(L10n.Get("MainWindow_Title"), ref open);
            TrackCurrentWindowInputRect();
            ImGui.PushTextWrapPos();
            if (ImGui.Button(FontAwesome7.Xmark + " " + L10n.Get("Btn_CloseOverlay")))
                open = false;
            ImGui.Separator();
            if (ImGui.BeginTabBar("MainTabs"))
            {

                if (ImGui.BeginTabItem(L10n.Get("Tab_ModList")))
                {

                    if (ImGui.Button(FontAwesome7.MagnifyingGlass + " " + L10n.Get("Btn_ScanMods")))
                    {
                        _modManager.ScanMods();
                        RegisterModGlyphText();
                        AutoEnableMods();
                    }
                    ImGui.SameLine();
                    if (ImGui.Button(FontAwesome7.Plus + " " + L10n.Get("Btn_AddMod")))
                        _showAddModPopup = true;

                    ImGui.Separator();

                    var loaded = _modManager.Mods.Count(m => m.LoadState == ModLoadState.Loaded);
                    ImGui.Text(L10n.Get("Status_ModCount", _modManager.Mods.Count, loaded));

                    if (ImGui.BeginTable("ModTable", 5,
                        ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                        ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
                        new Vector2(0, 300 * scale)))
                    {
                        ImGui.TableSetupColumn(L10n.Get("Col_State"), ImGuiTableColumnFlags.WidthFixed, 150 * scale);
                        ImGui.TableSetupColumn(L10n.Get("Col_Name"), ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn(L10n.Get("Col_Version"), ImGuiTableColumnFlags.WidthFixed, 120 * scale);
                        ImGui.TableSetupColumn(L10n.Get("Col_Action"), ImGuiTableColumnFlags.WidthFixed, 150 * scale);
                        ImGui.TableSetupColumn(L10n.Get("Col_Settings"), ImGuiTableColumnFlags.WidthFixed, 86 * scale);
                        ImGui.TableHeadersRow();

                        foreach (var mod in _modManager.Mods)
                        {
                            ImGui.TableNextRow();
                            ImGui.TableSetColumnIndex(0);
                            ImGui.AlignTextToFramePadding();
                            RenderModState(mod);

                            ImGui.TableSetColumnIndex(1);
                            var isSelected = _selectedMod == mod;
                            ImGui.Selectable($"{mod.Name}##{mod.Id}", isSelected,
                                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap);
                            if (ImGui.IsItemClicked()) _selectedMod = mod;
                            if (mod.LoadState == ModLoadState.Loading)
                            {
                                ImGui.ProgressBar(
                                    mod.LoadProgress,
                                    new Vector2(-1, 3 * scale),
                                    string.Empty);
                                if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(mod.LoadStage))
                                    ImGui.SetTooltip($"{mod.LoadProgress:P0} {mod.LoadStage}");
                            }

                            ImGui.TableSetColumnIndex(2);
                            ImGui.Text(mod.Version);

                            ImGui.TableSetColumnIndex(3);
                            ImGui.AlignTextToFramePadding();
                            RenderModAction(mod);

                            ImGui.TableSetColumnIndex(4);
                            ImGui.AlignTextToFramePadding();
                            if (mod.PluginInstance is IModSettings)
                            {
                                var isExpanded = _expandedModId == mod.Id;
                                var label = isExpanded ? FontAwesome7.ChevronDown : FontAwesome7.Gear;
                                if (ImGui.SmallButton($"{label} {L10n.Get("Col_Settings")}##cfg_{mod.Id}"))
                                    HandleSettingsButtonClick(mod, isExpanded);
                            }
                            else
                                ImGui.TextDisabled("-");
                        }
                        ImGui.EndTable();
                    }
                    RenderSelectedModDetails();
                    ImGui.EndTabItem();
                }


                if (ImGui.BeginTabItem(L10n.Get("Tab_Console")))
                {
                    if (ImGui.BeginTabBar("ConsoleTabs"))
                    {
                        if (ImGui.BeginTabItem(L10n.Get("Tab_Log")))
                        {
                            if (ImGui.Button(FontAwesome7.Trash + " " + L10n.Get("Btn_Clear"))) _logMessages.Clear();
                            ImGui.SameLine();
                            ImGui.Text(L10n.Get("Status_LogCount", _logMessages.Count));
                            ImGui.Separator();

                            if (ImGui.BeginChild("LogScroll", Vector2.Zero, ImGuiChildFlags.None,
                                ImGuiWindowFlags.HorizontalScrollbar))
                            {
                                foreach (var msg in _logMessages.AsEnumerable().Reverse())
                                {
                                    var color = msg.Contains("[ERROR]")
                                        ? new Vector4(1f, 0.3f, 0.3f, 1f)
                                        : new Vector4(0.7f, 0.7f, 0.7f, 1f);
                                    ImGui.TextColored(color, msg);
                                }
                            }
                            ImGui.EndChild();
                            ImGui.EndTabItem();
                        }

                        if (ImGui.BeginTabItem(L10n.Get("Tab_Settings")))
                        {
                            ImGui.Text(L10n.Get("Settings_ModsDir") + " " + _config.ModsDirectory);
                            ImGui.Separator();

                            ImGui.Text(L10n.Get("Settings_UiScale"));
                            float uiscale = ImGui.GetIO().FontGlobalScale;
                            if (ImGui.SliderFloat("##uiscale", ref uiscale, 1f, 5f, "%.1f"))
                            {
                                ImGui.GetIO().FontGlobalScale = uiscale;
                                ApplyStyleScale();
                            }

                            ImGui.Text(L10n.Get("Settings_Language"));
                            var languageIndex = _config.Language == L10n.EnglishLanguage ? 1 : 0;
                            var languageLabels = new[]
                            {
                                L10n.Get("Language_Chinese"),
                                L10n.Get("Language_English")
                            };
                            if (ImGui.Combo(
                                    "##language",
                                    ref languageIndex,
                                    languageLabels,
                                    languageLabels.Length))
                            {
                                _config.Language = languageIndex == 1
                                    ? L10n.EnglishLanguage
                                    : L10n.ChineseLanguage;
                                L10n.SetLanguage(_config.Language);
                            }

                            var style = ImGui.GetStyle();
                            ImGui.Text(L10n.Get("Settings_GrabSize"));
                            float grab = style.GrabMinSize;
                            if (ImGui.SliderFloat("##grab", ref grab, 5f, 60f, "%.0f"))
                                style.GrabMinSize = grab;

                            float scrollW = style.ScrollbarSize;
                            ImGui.Text(L10n.Get("Settings_ScrollSize"));
                            if (ImGui.SliderFloat("##scroll", ref scrollW, 10f, 60f, "%.0f"))
                                style.ScrollbarSize = scrollW;

                            ImGui.Separator();
                            ImGui.Text(L10n.Get("Settings_TouchKeyViewerMapping"));
                            var touchMappingMode = (int)_config.TouchKeyViewerMappingMode;
                            var touchMappingLabels = new[]
                            {
                                L10n.Get("Settings_TouchKeyViewerMappingRegions"),
                                L10n.Get("Settings_TouchKeyViewerMappingContacts")
                            };
                            if (ImGui.Combo(
                                    "##touch-keyviewer-mapping",
                                    ref touchMappingMode,
                                    touchMappingLabels,
                                    touchMappingLabels.Length))
                            {
                                _config.TouchKeyViewerMappingMode =
                                    PcCompatTouchLaneMappingRuntime.Normalize(
                                        (PcCompatTouchLaneMappingMode)touchMappingMode);
                                PcCompatTouchLaneMappingRuntime.SetMode(
                                    _config.TouchKeyViewerMappingMode);
                            }
                            ImGui.TextWrapped(L10n.Get(
                                _config.TouchKeyViewerMappingMode ==
                                PcCompatTouchLaneMappingMode.TouchContacts
                                    ? "Settings_TouchKeyViewerMappingContactsHint"
                                    : "Settings_TouchKeyViewerMappingRegionsHint"));
                            if (_config.TouchKeyViewerMappingMode ==
                                PcCompatTouchLaneMappingMode.TouchContacts)
                            {
                                ImGui.Text(L10n.Get(
                                    "Settings_TouchKeyViewerContactReuseDelay"));
                                var reuseDelay =
                                    _config.TouchKeyViewerContactReuseDelayMilliseconds;
                                if (ImGui.SliderInt(
                                        "##touch-keyviewer-contact-reuse-delay",
                                        ref reuseDelay,
                                        0,
                                        PcCompatTouchLaneMappingRuntime
                                            .MaximumTouchContactReuseDelayMilliseconds,
                                        "%d ms"))
                                {
                                    _config.TouchKeyViewerContactReuseDelayMilliseconds =
                                        PcCompatTouchLaneMappingRuntime
                                            .NormalizeTouchContactReuseDelayMilliseconds(
                                                reuseDelay);
                                    PcCompatTouchLaneMappingRuntime
                                        .SetTouchContactReuseDelayMilliseconds(
                                            _config.TouchKeyViewerContactReuseDelayMilliseconds);
                                }
                                ImGui.TextWrapped(L10n.Get(
                                    "Settings_TouchKeyViewerContactReuseDelayHint"));
                            }

                            ImGui.Separator();
                            if (ImGui.Button($"{FontAwesome7.FloppyDisk} {L10n.Get("Btn_SaveSettings")}"))
                            {
                                SaveConfig();
                                _toastMessage = L10n.Get("Toast_SettingsSaved");
                                _toastTimer = 2f;
                            }
                            ImGui.Separator();
                            ImGui.Text(L10n.Get("Settings_Shortcuts"));
                            ImGui.BulletText(L10n.Get("Settings_Shortcut_Scan"));
                            ImGui.Separator();
                            ImGui.Text(L10n.Get("Settings_ImGuiVersion") + " " + ImGui.GetVersion());
                            ImGui.Text(L10n.Get("Settings_LoadedModCount") + " " + _modManager.Mods.Count(m => m.LoadState == ModLoadState.Loaded));
                            ImGui.EndTabItem();
                        }

                        ImGui.EndTabBar();
                    }
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }
            ImGui.PopTextWrapPos();
        ImGui.End();
        if (!open)
            _platform.SetOverlayVisible(false);
    }

    private void HandleSettingsButtonClick(ModEntry mod, bool isExpanded)
    {
        _expandedModId = isExpanded ? null : mod.Id;
    }

    private bool TryOpenOriginalSettingsSurface(ModEntry mod)
    {
        if (mod.LoadState != ModLoadState.Loaded ||
            mod.PluginInstance is not IModOriginalSettingsSurface original)
            return false;

        if (!original.TryOpenOriginalSettings(out var error))
        {
            _toastMessage = L10n.Get(
                    "Mod.OriginalSettingsOpenFailed",
                    error ?? L10n.Get("Mod.OriginalSettingsUnavailable"));
            _toastTimer = 3f;
            return false;
        }

        // Preserve the host settings page so closing the MOD-owned surface
        // returns to compatibility controls and diagnostics.
        _expandedModId = mod.Id;
        _externalSettingsModId = mod.Id;
        _platform.SetModalInputCapture(true, blockUnityEventSystem: true);
        _externalSettingsOverlayHidden = true;
        _platform.SetOverlayVisible(false);
        return true;
    }

    private void RenderSelectedModDetails()
    {
        var mod = _selectedMod;
        if (mod == null)
            return;

        ImGui.Separator();
        ImGui.Text(L10n.Get("ModDetails_Title"));
        ImGui.BeginChild("SelectedModDetails", new Vector2(0, 240 * LayoutScale), ImGuiChildFlags.None);
        if (ImGui.BeginTable("SelectedModDetailsTable", 2,
            ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("##detail_label", ImGuiTableColumnFlags.WidthFixed, 120 * LayoutScale);
            ImGui.TableSetupColumn("##detail_value", ImGuiTableColumnFlags.WidthStretch);
            RenderDetailLine(L10n.Get("Detail_Id"), mod.Id);
            RenderDetailLine(L10n.Get("Detail_Name"), mod.Name);
            RenderDetailLine(L10n.Get("Detail_Version"), mod.Version);
            RenderDetailLine(L10n.Get("Detail_Author"), mod.Author);
            RenderDetailLine(L10n.Get("Detail_LoaderKind"), mod.LoaderKind);
            RenderDetailLine(L10n.Get("Detail_State"), L10n.Get("State_" + mod.LoadState));
            RenderDetailLine(L10n.Get("Detail_EntryPoint"), mod.EntryPoint ?? string.Empty);
            RenderDetailLine(L10n.Get("Detail_FolderPath"), mod.FolderPath);
            RenderDetailLine(L10n.Get("Detail_Description"), mod.Description);
            RenderDetailLine(L10n.Get("Detail_Dependencies"),
                mod.Dependencies.Count == 0 ? "-" : string.Join(", ", mod.Dependencies));
            if (!string.IsNullOrWhiteSpace(mod.LoadError))
                RenderDetailLine(L10n.Get("Detail_Error"), mod.LoadError);
            if (mod.LoadState == ModLoadState.Loading)
            {
                RenderDetailLine(L10n.Get("Detail_LoadStage"), mod.LoadStage);
                RenderDetailLine(L10n.Get("Detail_LoadProgress"), $"{mod.LoadProgress:P0}");
            }
            ImGui.EndTable();
        }
        ImGui.EndChild();
    }

    private static void RenderDetailLine(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextDisabled(label);
        ImGui.TableSetColumnIndex(1);
        ImGui.PushTextWrapPos();
        ImGui.TextWrapped(string.IsNullOrWhiteSpace(value) ? "-" : value);
        ImGui.PopTextWrapPos();
    }

    private void TrackCurrentWindowInputRect()
    {
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        _platform.AddOverlayInputRect(pos.X, pos.Y, size.X, size.Y);
    }

}

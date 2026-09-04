using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;
using StArray.ModManager.Resources;
using IconFonts;
using ImGuiNET;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Xphorror.PcModCompat;

public sealed class PcCompatModPlugin :
    IModPlugin,
    IAsyncModPlugin,
    IModSettings,
    IModSettingsLayout,
    IModOriginalSettingsSurface,
    IPersistentModOverlay,
    ILogicalProcessLifetimeHookRetirement,
    IPcCompatUnityHudSource
{
    private readonly PcModManifest _manifest;
    private readonly object _loadLock = new();
    private PcCompatDiagnosticsOperationResult? _lastDiagnosticsOperation;
    private CancellationTokenSource? _loadCancellation;
    private Task<PcCompatPreparedMod>? _loadTask;
    private float _loadProgress;
    private string _loadStage = "Idle";
    private int _loadGeneration;
    private bool _showAllSlots;
    private long _nextExportStatusPoll;
    private PcCompatDiagnosticsExportStatus _exportStatus = PcCompatDiagnosticsExportStatus.Unavailable;
    private PcCompatMobileSettings _mobileSettings;
    private bool _mobileSettingsDirty;
    private string? _mobileSettingsStatus;
    private readonly string _practiceHudTitle;
    private readonly HudLine[] _hudLines = new HudLine[20];
    private readonly PcCompatPlayStatsSession _playStats;
    private PcCompatOverlaySnapshot _pendingOverlaySnapshot = PcCompatOverlaySnapshot.Unavailable;
    private PcCompatDiagnosticsSnapshot _hudDiagnostics = PcCompatDiagnosticsSnapshot.Unavailable;
    private string _hudTitle = string.Empty;
    private int _hudLineCount;
    private bool _hudProgressBarVisible;
    private float _hudProgressBarValue;
    private int _hudSettingsGeneration;
    private int _renderedHudSettingsGeneration = -1;
    private int _hudCultureLcid = int.MinValue;
    private uint _hudSnapshotGeneration;
    private uint _hudInputSnapshotGeneration;
    private long _nextHudDiagnosticsRefresh;
    private float _hudMeasuredScale = float.NaN;
    private float _hudMeasuredFontSize = float.NaN;
    private float _hudContentWidth;
    private bool _hasPendingOverlaySnapshot;
    private bool _hudModelValid;
    private bool _hudLayoutDirty = true;
    private int _hudModelRevision;
    private int _unityHudFrameRevision = -1;
    private PcCompatUnityHudFrame? _unityHudFrame;
    private bool _supportsStandardUnityHud;
    private bool _unityHudRegistered;
    private PcCompatPlayStatsSnapshot _playStatsSnapshot;
    private string? _pendingResourceCandidateSha;
    private PcCompatResourceLoadAuthorization _pendingResourceAuthorization;
    private string? _resourceLoadStatus;
    private bool _resourceLoadStatusError;
    private string? _managedSelfRenderRequestError;
    private string? _originalSettingsFallbackReason;
    private readonly Dictionary<string, string> _settingsSchemaValues =
        new(StringComparer.Ordinal);
    private string? _settingsSchemaRevision;
    private string? _settingsSchemaRequestError;
    private string? _loadCompletionError;
    private string? _keyViewerAdapterPath;
    private PcCompatKeyViewerAdapterDocument? _keyViewerAdapter;
    private string? _keyViewerAdapterError;
    private string? _keyViewerOverridesPath;
    private PcCompatKeyViewerOverrideDocument? _keyViewerOverrides;
    private string? _keyViewerOverridesError;
    private string? _keyViewerOverridesStatus;
    private string? _keyViewerPreviewError;
    private string? _keyViewerLoweringStatus;
    private bool _keyViewerOverridesDirty;
    private ModRuntimeSession? _runtimeSession;
    private ModRuntimeKey _runtimeKey;
    private PcCompatManagedModSession? _observedManagedSession;
    private long _adapterActivationGeneration;
    private readonly PcCompatManagedProviderSequenceWatcher _providerSequenceWatcher = new();
    private string? _keyViewerRepublicationStatus;
    private string? _keyViewerAdapterDiagnosticState;

    private const long HudDiagnosticsRefreshMilliseconds = 500;
    private static readonly int[] TouchKeyCountOptions = [2, 4, 6, 8, 10];
    private static readonly string[] TouchKeyCountLabels = ["2", "4", "6", "8", "10"];

    private readonly record struct HudLine(string Text, uint Color);

    public PcCompatModPlugin(PcModManifest manifest)
    {
        _manifest = manifest;
        _mobileSettings = PcCompatMobileSettingsStore.Load(manifest.FolderPath);
        _playStats = new PcCompatPlayStatsSession(manifest.FolderPath);
        _practiceHudTitle = manifest.DisplayName + " Practice";
    }

    public string Id => _manifest.Id;
    public string Name => _manifest.DisplayName;
    public string Version => _manifest.Version;
    public string Author => _manifest.Author;
    public string Description => $"{_manifest.Kind} PC MOD compatibility entry";
    public IReadOnlyList<string> Dependencies => _manifest.Requirements;
    public Vector2 PreferredWindowSize => new(780, 720);
    public bool ShowSaveButton => false;
    // While managed self-render holds presentation ownership every MOD visual
    // must come from the MOD's own rewritten code. The compatibility HUD (and
    // its ImGui fallback) would otherwise double-draw generic text/progress
    // over the MOD canvas and make self-render indistinguishable from compat
    // rendering.
    private bool ManagedSelfRenderBlocksCompatibilityPresentation
    {
        get
        {
            var session = PcCompatRuntime.GetManagedSession(Id);
            return session is { ManagedPresentationClaimed: true } or { ActivationPending: true };
        }
    }

    public bool ShouldRenderWhenManagerHidden
    {
        get
        {
            if (!_supportsStandardUnityHud)
                return false;

            if (PcCompatUnityHudRuntime.RendererAvailableFor(Id))
                return false;

            if (ManagedSelfRenderBlocksCompatibilityPresentation)
            {
                _hasPendingOverlaySnapshot = false;
                return false;
            }

            if (!_mobileSettings.ShowHud)
            {
                _hasPendingOverlaySnapshot = false;
                return false;
            }

            var overlay = PcCompatOverlayRuntime.Snapshot(Id);
            if (!overlay.ProviderAvailable || !overlay.Visible)
            {
                _hasPendingOverlaySnapshot = false;
                return false;
            }

            _pendingOverlaySnapshot = overlay;
            _hasPendingOverlaySnapshot = true;
            return true;
        }
    }

    public void OnLoad()
    {
        CaptureRuntimeIdentity();
        PcCompatRuntime.RegisterMod(_manifest, _runtimeSession, _runtimeKey);
        ApplyResourceChangerSettings();
        ObserveManagedActivation();
        RefreshRuntimeAdapters();
    }

    public void BeginLoad()
    {
        CaptureRuntimeIdentity();
        lock (_loadLock)
        {
            if (_loadTask is { IsCompleted: false })
                return;

            _loadCancellation?.Dispose();
            _loadCancellation = new CancellationTokenSource();
            var generation = ++_loadGeneration;
            _loadProgress = 0.01f;
            _loadStage = "Queued";
            _loadCompletionError = null;
            var token = _loadCancellation.Token;
            _loadTask = StartOwnedPrepareTask(generation, token);
        }
    }

    private Task<PcCompatPreparedMod> StartOwnedPrepareTask(
        int generation,
        CancellationToken cancellationToken)
    {
        var runtimeSession = _runtimeSession;
        var runtimeKey = _runtimeKey;
        var identity = $"prepare-generation={generation};";
        IModRuntimeOperationLease? operationLease = null;
        CancellationTokenSource? linkedCancellation = null;

        if (runtimeSession != null && runtimeKey.IsValid)
        {
            if (!runtimeSession.TryBeginOwnedOperation(
                    runtimeKey,
                    identity,
                    out operationLease) ||
                operationLease == null)
            {
                throw new InvalidOperationException(
                    $"PcCompat background preparation rejected for retired generation " +
                    $"mod={Id} generation={runtimeKey.Generation}.");
            }
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                operationLease.CancellationToken);
        }

        try
        {
            var effectiveCancellation = linkedCancellation?.Token ?? cancellationToken;
            // Do not pass the cancellation token to Task.Run itself. The delegate must
            // always enter its finally block so the runtime operation lease is released.
            return Task.Run(() =>
            {
                try
                {
                    return PcCompatRuntime.PrepareMod(
                        _manifest,
                        (progress, stage) => UpdateLoadProgress(generation, progress, stage),
                        effectiveCancellation);
                }
                finally
                {
                    linkedCancellation?.Dispose();
                    operationLease?.Dispose();
                }
            });
        }
        catch
        {
            linkedCancellation?.Dispose();
            operationLease?.Dispose();
            throw;
        }
    }

    public ModLoadProgress GetLoadProgress()
    {
        lock (_loadLock)
        {
            if (!string.IsNullOrWhiteSpace(_loadCompletionError))
            {
                return new ModLoadProgress(
                    1f,
                    L10n.Get("PcCompat_LoadStage_Failed") + ": " +
                    FirstStatusLine(_loadCompletionError));
            }
            if (_loadTask?.IsFaulted == true)
                return new ModLoadProgress(1f, L10n.Get("PcCompat_LoadStage_Failed"));
            if (_loadTask?.IsCanceled == true)
                return new ModLoadProgress(1f, L10n.Get("PcCompat_LoadStage_Cancelled"));
            return new ModLoadProgress(_loadProgress, LocalizeLoadStage(_loadStage));
        }
    }

    public bool IsLoadReady
    {
        get
        {
            lock (_loadLock)
                return _loadTask?.IsCompleted == true;
        }
    }

    public void CompleteLoad()
    {
        CaptureRuntimeIdentity();
        Task<PcCompatPreparedMod>? task;
        lock (_loadLock)
            task = _loadTask;

        if (task == null)
            throw new InvalidOperationException("PcCompat background load was not started.");

        lock (_loadLock)
        {
            _loadProgress = 0.95f;
            _loadStage = "Installing managed runtime";
        }

        try
        {
            var prepared = task.GetAwaiter().GetResult();
            lock (_loadLock)
            {
                // CancelLoad/UnloadMod on the UI thread may have swapped or cleared
                // the task while we waited for it. Never install a stale result:
                // that would resurrect a MOD the user explicitly unloaded.
                if (!ReferenceEquals(_loadTask, task))
                    throw new OperationCanceledException(
                        "PcCompat load was cancelled or replaced before managed installation.");
                _loadTask = null;
            }

            PcCompatRuntime.RegisterPreparedMod(prepared, _runtimeSession, _runtimeKey);
            ObserveManagedActivation();
            RefreshRuntimeAdapters();
            lock (_loadLock)
            {
                _loadProgress = 1f;
                _loadStage = "Ready";
                _loadCompletionError = null;
            }
        }
        catch (Exception exception)
        {
            lock (_loadLock)
            {
                _loadProgress = 1f;
                _loadStage = "Failed";
                _loadCompletionError = exception.ToString();
            }
            throw;
        }
    }

    public void CancelLoad()
    {
        lock (_loadLock)
        {
            _loadCancellation?.Cancel();
            _loadGeneration++;
            _loadTask = null;
            _loadStage = "Cancelled";
        }
    }

    public void OnUnload()
    {
        Logger.Info(
            nameof(PcCompatModPlugin),
            $"[DEBUG-kv-unload-v1] plugin-unload-enter mod={Id} " +
            $"tid={Environment.CurrentManagedThreadId}");
        StopObservingManagedActivation();
        // Native callback gates must retire before any managed/UI state they can
        // reach is dismantled. A retirement failure leaves this plugin intact.
        PcCompatRuntime.UnregisterMod(_manifest);
        PcCompatOverlayRuntime.RemoveOwner(Id);
        if (_unityHudRegistered)
        {
            PcCompatUnityHudRuntime.UnregisterSource(this);
            _unityHudRegistered = false;
            RetireOwnedHudSource();
        }
        _supportsStandardUnityHud = false;
        _playStats.Dispose();
        SaveMobileSettingsIfDirty();
        SaveKeyViewerOverridesIfDirty();
        PcCompatKeyViewerLabelProjectionRuntime.Unregister(Id);
        PcCompatKeyViewerPreviewRuntime.Unregister(Id);
        PcCompatKeyViewerFallbackRuntime.Unregister(Id);
        CancelLoad();
        Logger.Info(
            nameof(PcCompatModPlugin),
            $"[DEBUG-kv-unload-v1] plugin-unload-complete mod={Id} " +
            $"tid={Environment.CurrentManagedThreadId}");
    }

    private void CaptureRuntimeIdentity()
    {
        var session = HookHelper.CurrentRuntimeSession;
        var key = HookHelper.CurrentRuntimeKey;
        if (session == null || !key.IsValid)
            return;
        _runtimeSession = session;
        _runtimeKey = key;
    }

    private void ObserveManagedActivation()
    {
        var session = PcCompatRuntime.GetManagedSession(Id);
        if (ReferenceEquals(session, _observedManagedSession))
            return;
        StopObservingManagedActivation();
        if (session == null)
            return;
        _observedManagedSession = session;
        session.RegisterActivationCompletedObserver(OnManagedActivationCompleted);
        session.RegisterConfigurationPollObserver(OnManagedConfigurationPoll);
    }

    private void StopObservingManagedActivation()
    {
        var session = _observedManagedSession;
        _observedManagedSession = null;
        _providerSequenceWatcher.Clear();
        _keyViewerRepublicationStatus = null;
        if (session != null)
        {
            session.UnregisterActivationCompletedObserver(OnManagedActivationCompleted);
            session.UnregisterConfigurationPollObserver(OnManagedConfigurationPoll);
        }
    }

    /// <summary>
    /// Re-reads the BindingProviders backing the live lowered plans and republishes when one of them
    /// no longer matches what its plan was built from.
    /// </summary>
    /// <remarks>
    /// A lowered plan is a snapshot of configuration the MOD keeps owning and keeps changing - the
    /// audited key viewer resolves its key array from a settings field and its own menu both switches
    /// the layout and rebinds individual keys. Without this, the snapshot and the MOD diverge on the
    /// first such change: touch lanes publish identities the MOD no longer queries and the MOD queries
    /// identities nobody publishes, so input silently stops arriving and stays that way until the MOD
    /// is reloaded.
    /// </remarks>
    private void OnManagedConfigurationPoll(PcCompatManagedModSession session)
    {
        if (!ReferenceEquals(session, _observedManagedSession) ||
            !_providerSequenceWatcher.IsWatching ||
            !_providerSequenceWatcher.ShouldPoll(Environment.TickCount64))
        {
            return;
        }
        if (!_providerSequenceWatcher.TryDetectChange(
                ResolveManagedProviderSequence,
                out var reason))
        {
            return;
        }

        Logger.Info(
            nameof(PcCompatModPlugin),
            $"keyviewer provider configuration changed mod={Id} " +
            $"resourceGeneration={session.ResourceSessionGeneration}; {reason}");
        _keyViewerRepublicationStatus = reason;
        RefreshKeyViewerPreviewRegistration();
    }

    private (bool Success, int[] Values, string? Error) ResolveManagedProviderSequence(
        PcCompatKeyViewerRoleOverride role,
        int requiredCount)
        => PcCompatRuntime.TryResolveManagedIntSequence(
            Id,
            role,
            requiredCount,
            out var values,
            out var error)
            ? (true, values, null)
            : (false, Array.Empty<int>(), error);

    private void OnManagedActivationCompleted(PcCompatManagedModSession session)
    {
        if (!ReferenceEquals(session, _observedManagedSession) ||
            session.ResourceSessionGeneration <= 0 ||
            _adapterActivationGeneration == session.ResourceSessionGeneration)
        {
            return;
        }
        _adapterActivationGeneration = session.ResourceSessionGeneration;
        // A new generation reloaded the MOD's own configuration, so fingerprints taken against the
        // previous one carry no information about it - and neither does the reason text from it.
        _providerSequenceWatcher.Clear();
        _keyViewerRepublicationStatus = null;
        RefreshRuntimeAdapters();
        Logger.Info(
            nameof(PcCompatModPlugin),
            $"managed activation adapters refreshed mod={Id} " +
            $"resourceGeneration={session.ResourceSessionGeneration}");
    }

    public void OnGui()
    {
        ImGui.Text(
            FontAwesome7.ScrewdriverWrench + " " +
            L10n.Get("PcCompat_SettingsTitle"));
        if (!string.IsNullOrWhiteSpace(_originalSettingsFallbackReason))
        {
            ImGui.Spacing();
            ImGui.TextColored(
                ErrorColor,
                L10n.Get("PcCompat_SettingsFallbackTitle"));
            ImGui.PushTextWrapPos();
            ImGui.TextColored(
                ErrorColor,
                L10n.Get(
                    "PcCompat_SettingsFallbackReason",
                    _originalSettingsFallbackReason));
            ImGui.PopTextWrapPos();
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        RenderLoadProgress();
        if (!ImGui.BeginTabBar($"PcCompatSettingsTabs##{Id}"))
            return;

        if (ImGui.BeginTabItem(L10n.Get("PcCompat_Tab_ModSettings")))
        {
            if (RenderLinkedOriginalSettings(
                    !string.IsNullOrWhiteSpace(_originalSettingsFallbackReason)))
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
            }
            RenderManagedSelfRenderControl();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            if (RenderKeyViewerUserSettings())
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
            }
            RenderMobileModSettings();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(L10n.Get("PcCompat_Tab_Diagnostics")))
        {
            RenderManagedTranslationStatus();
            ImGui.Spacing();
            ImGui.Separator();
            RenderKeyViewerAdapterStatus();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Text(FontAwesome7.ScrewdriverWrench + " " + L10n.Get("PcCompat_NativeGlobal"));

            var snapshot = PcCompatDiagnosticsRuntime.Snapshot();
            RenderNativeStatus(snapshot);
            RenderDiagnosticsActions(ref snapshot);
            RenderDiagnosticsResult(snapshot);
            RenderDiagnosticsExport(snapshot);
            RenderSlotSummary(snapshot);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private bool RenderLinkedOriginalSettings(bool fallback)
    {
        var schema = PcCompatRuntime.SnapshotSettingsSchema(Id);
        if (!schema.Available && !fallback)
            return false;

        ImGui.Text(
            FontAwesome7.Sliders + " " +
            L10n.Get(fallback
                ? "PcCompat_SettingsSchemaTitle"
                : "PcCompat_SettingsLinkedTitle"));
        if (!fallback)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextDisabled(L10n.Get("PcCompat_SettingsLinkedHint"));
            ImGui.PopTextWrapPos();
        }
        if (!schema.Available)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextDisabled(schema.Error ??
                L10n.Get("PcCompat_SettingsSchemaUnavailable"));
            ImGui.PopTextWrapPos();
            return true;
        }

        if (!string.Equals(
                _settingsSchemaRevision,
                schema.Revision,
                StringComparison.Ordinal))
        {
            _settingsSchemaRevision = schema.Revision;
            _settingsSchemaValues.Clear();
            _settingsSchemaRequestError = null;
        }

        string? currentGroup = null;
        foreach (var entry in schema.Entries)
        {
            if (!string.Equals(currentGroup, entry.Group, StringComparison.Ordinal))
            {
                if (currentGroup != null)
                    ImGui.Spacing();
                currentGroup = entry.Group;
                ImGui.TextDisabled(currentGroup);
            }

            if (!schema.HasPendingWrite ||
                !_settingsSchemaValues.TryGetValue(entry.Path, out var value))
            {
                value = entry.Value;
                _settingsSchemaValues[entry.Path] = value;
            }

            var changed = false;
            ImGui.BeginDisabled(!entry.Editable);
            switch (entry.Kind)
            {
                case PcCompatManagedSettingsValueKind.Boolean:
                {
                    var boolean = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                    changed = ImGui.Checkbox(
                        $"{entry.Label}##schema_{entry.Path}",
                        ref boolean);
                    if (changed)
                        value = boolean ? "true" : "false";
                    break;
                }
                case PcCompatManagedSettingsValueKind.Enum:
                {
                    var values = entry.EnumValues.ToArray();
                    var index = Array.IndexOf(values, value);
                    if (index < 0)
                        index = 0;
                    changed = values.Length != 0 && ImGui.Combo(
                        $"{entry.Label}##schema_{entry.Path}",
                        ref index,
                        values,
                        values.Length);
                    if (changed)
                        value = values[index];
                    break;
                }
                case PcCompatManagedSettingsValueKind.Integer
                    when entry.Minimum is double integerMinimum &&
                         entry.Maximum is double integerMaximum &&
                         integerMinimum >= int.MinValue && integerMaximum <= int.MaxValue &&
                         int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer):
                    changed = ImGui.SliderInt(
                        $"{entry.Label}##schema_{entry.Path}",
                        ref integer,
                        (int)integerMinimum,
                        (int)integerMaximum);
                    if (changed)
                        value = integer.ToString(CultureInfo.InvariantCulture);
                    break;
                case PcCompatManagedSettingsValueKind.Number
                    when entry.Minimum is double numberMinimum &&
                         entry.Maximum is double numberMaximum &&
                         numberMinimum >= -float.MaxValue && numberMaximum <= float.MaxValue &&
                         float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number):
                    changed = ImGui.SliderFloat(
                        $"{entry.Label}##schema_{entry.Path}",
                        ref number,
                        (float)numberMinimum,
                        (float)numberMaximum);
                    if (changed)
                        value = number.ToString("R", CultureInfo.InvariantCulture);
                    break;
                default:
                    changed = ImGui.InputText(
                        $"{entry.Label}##schema_{entry.Path}",
                        ref value,
                        entry.Kind == PcCompatManagedSettingsValueKind.String
                            ? 1024u
                            : 96u);
                    break;
            }
            ImGui.EndDisabled();

            if (!entry.Editable && !string.IsNullOrWhiteSpace(entry.Reason) &&
                ImGui.IsItemHovered())
                ImGui.SetTooltip(entry.Reason);
            if (!changed)
                continue;

            _settingsSchemaValues[entry.Path] = value;
            if (!PcCompatRuntime.RequestSettingsSchemaValue(
                    Id,
                    schema.Revision,
                    entry.Path,
                    value,
                    out var requestError))
            {
                _settingsSchemaRequestError = requestError;
            }
            else
            {
                _settingsSchemaRequestError = null;
            }
        }

        if (schema.HasPendingWrite)
            ImGui.TextDisabled(L10n.Get("PcCompat_SettingsSchemaApplying"));
        if (!string.IsNullOrWhiteSpace(schema.ApplyError))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(ErrorColor, schema.ApplyError);
            ImGui.PopTextWrapPos();
        }
        if (schema.HasUnsavedChanges || !string.IsNullOrWhiteSpace(schema.SaveError))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(
                ErrorColor,
                schema.SaveError ?? L10n.Get("PcCompat_SettingsSchemaUnsaved"));
            ImGui.PopTextWrapPos();
            if (ImGui.Button(
                    FontAwesome7.Rotate + " " +
                    L10n.Get("PcCompat_SettingsSchemaRetrySave") +
                    $"##schema_retry_{Id}"))
            {
                PcCompatRuntime.RequestSettingsSchemaSaveRetry(Id);
            }
        }
        if (!string.IsNullOrWhiteSpace(_settingsSchemaRequestError))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(ErrorColor, _settingsSchemaRequestError);
            ImGui.PopTextWrapPos();
        }
        return true;
    }

    public bool TryOpenOriginalSettings(out string? error)
    {
        if (PcCompatRuntime.TryOpenOriginalSettings(Id, out error))
        {
            _originalSettingsFallbackReason = null;
            return true;
        }
        _originalSettingsFallbackReason = error ??
            "original MOD settings surface is unavailable";
        return false;
    }

    public void RequestCloseOriginalSettings()
        => PcCompatRuntime.RequestCloseOriginalSettings(Id);

    public ModOriginalSettingsSnapshot SnapshotOriginalSettings()
    {
        var snapshot = PcCompatRuntime.SnapshotOriginalSettings(Id);
        if (snapshot.State is PcCompatManagedSettingsState.Faulted or
            PcCompatManagedSettingsState.Unavailable)
            _originalSettingsFallbackReason = snapshot.Fault;
        return new ModOriginalSettingsSnapshot(
            snapshot.State switch
            {
                PcCompatManagedSettingsState.Closed => ModOriginalSettingsState.Closed,
                PcCompatManagedSettingsState.Opening => ModOriginalSettingsState.Opening,
                PcCompatManagedSettingsState.Open => ModOriginalSettingsState.Open,
                PcCompatManagedSettingsState.Faulted => ModOriginalSettingsState.Faulted,
                _ => ModOriginalSettingsState.Unavailable
            },
            snapshot.Fault,
            snapshot.SurfaceKind switch
            {
                PcCompatManagedSettingsSurfaceKind.UnityImGui =>
                    ModOriginalSettingsSurfaceKind.UnityImGui,
                PcCompatManagedSettingsSurfaceKind.UnityCanvas =>
                    ModOriginalSettingsSurfaceKind.UnityCanvas,
                _ => ModOriginalSettingsSurfaceKind.None
            });
    }

    private bool RenderKeyViewerUserSettings()
    {
        RefreshKeyViewerAdapter();
        RefreshKeyViewerOverrides();
        if (_keyViewerAdapter == null)
            return false;

        ImGui.Text(FontAwesome7.Keyboard + " " +
                   L10n.Get("PcCompat_KeyViewerSettingsTitle"));
        if (!string.IsNullOrWhiteSpace(_keyViewerAdapterError))
        {
            ImGui.TextColored(ErrorColor, _keyViewerAdapterError);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(_keyViewerOverridesError))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(ErrorColor, _keyViewerOverridesError);
            ImGui.PopTextWrapPos();
            if (ImGui.Button(
                    FontAwesome7.WandMagicSparkles + " " +
                    L10n.Get("PcCompat_KeyViewerRecommendedReset") +
                    $"##keyviewer_repair_{Id}"))
            {
                _keyViewerOverrides = PcCompatKeyViewerOverrideStore.CreateRecommendedFor(
                    _keyViewerAdapter);
                _keyViewerOverridesError = null;
                _keyViewerOverridesDirty = true;
                SaveKeyViewerOverrides();
            }
            return true;
        }

        _keyViewerOverrides ??= PcCompatKeyViewerOverrideStore.CreateRecommendedFor(
            _keyViewerAdapter);
        var preview = PcCompatKeyViewerPreviewRuntime.Snapshot(Id);
        var changed = false;
        foreach (var feature in _keyViewerAdapter.Features)
        {
            var featureOverride = FindOrCreateKeyViewerFeatureOverride(feature, create: true)!;
            if (_keyViewerAdapter.Features.Count > 1)
                ImGui.TextUnformatted(feature.DisplayName);

            var enabled = featureOverride.Enabled;
            if (ImGui.Checkbox(
                    L10n.Get("PcCompat_KeyViewerEnable") +
                    $"##keyviewer_enable_{Id}_{feature.Id}",
                    ref enabled))
            {
                featureOverride.Enabled = enabled;
                changed = true;
            }

            if (featureOverride.Enabled)
            {
                var mode = (int)featureOverride.InputMode;
                var modeLabels = GetKeyViewerInputModeLabels();
                if (ImGui.Combo(
                        L10n.Get("PcCompat_KeyViewerInputMode") +
                        $"##keyviewer_mode_{Id}_{feature.Id}",
                        ref mode,
                        modeLabels,
                        modeLabels.Length))
                {
                    featureOverride.InputMode = Enum.IsDefined(
                        typeof(PcCompatKeyViewerInputMode), mode)
                        ? (PcCompatKeyViewerInputMode)mode
                        : PcCompatKeyViewerInputMode.Auto;
                    changed = true;
                }

                if (featureOverride.InputMode != PcCompatKeyViewerInputMode.External)
                {
                    var laneCountIndex = Array.IndexOf(
                        TouchKeyCountOptions,
                        featureOverride.TouchLaneCount);
                    if (laneCountIndex < 0)
                        laneCountIndex = TouchKeyCountOptions.Length - 1;
                    if (ImGui.Combo(
                            L10n.Get("PcCompat_KeyViewerTouchLaneCount") +
                            $"##keyviewer_lanes_{Id}_{feature.Id}",
                            ref laneCountIndex,
                            TouchKeyCountLabels,
                            TouchKeyCountLabels.Length))
                    {
                        featureOverride.TouchLaneCount = TouchKeyCountOptions[laneCountIndex];
                        changed = true;
                    }
                }

                var fallbackEnabled = featureOverride.CompatibleFallbackEnabled;
                if (ImGui.Checkbox(
                        L10n.Get("PcCompat_KeyViewerCompatibleFallback") +
                        $"##keyviewer_fallback_{Id}_{feature.Id}",
                        ref fallbackEnabled))
                {
                    featureOverride.CompatibleFallbackEnabled = fallbackEnabled;
                    changed = true;
                }
                if (featureOverride.CompatibleFallbackEnabled)
                {
                    ImGui.PushTextWrapPos();
                    ImGui.TextColored(
                        WarningColor,
                        L10n.Get("PcCompat_KeyViewerCompatibleFallbackWarning"));
                    ImGui.PopTextWrapPos();
                }
            }

            RenderKeyViewerUserStatus(featureOverride, preview);

            if (ImGui.Button(
                    FontAwesome7.WandMagicSparkles + " " +
                    L10n.Get("PcCompat_KeyViewerRecommendedReset") +
                    $"##keyviewer_recommended_{Id}_{feature.Id}"))
            {
                featureOverride.Enabled =
                    PcCompatKeyViewerOverrideStore.SupportsAutomaticInput(feature);
                featureOverride.InputMode = PcCompatKeyViewerInputMode.Auto;
                featureOverride.TouchLaneCount = 10;
                featureOverride.CompatibleFallbackEnabled = false;
                featureOverride.Roles.Clear();
                changed = true;
            }
        }

        if (changed)
        {
            _keyViewerOverridesDirty = true;
            _keyViewerOverridesStatus = null;
            SaveKeyViewerOverrides();
        }
        if (!string.IsNullOrWhiteSpace(_keyViewerOverridesStatus))
            ImGui.TextColored(SuccessColor, _keyViewerOverridesStatus);
        return true;
    }

    private void RenderKeyViewerUserStatus(
        PcCompatKeyViewerFeatureOverride featureOverride,
        PcCompatKeyViewerPreviewSnapshot preview)
    {
        if (!featureOverride.Enabled)
        {
            ImGui.TextDisabled(L10n.Get("PcCompat_KeyViewerStatusDisabled"));
            return;
        }
        var fallback = PcCompatKeyViewerFallbackRuntime.Snapshot();
        if (featureOverride.CompatibleFallbackEnabled &&
            !string.IsNullOrWhiteSpace(fallback.RendererError))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(
                ErrorColor,
                L10n.Get(
                    "PcCompat_KeyViewerFallbackFailed",
                    FirstStatusLine(fallback.RendererError)));
            ImGui.PopTextWrapPos();
            return;
        }
        if (!string.IsNullOrWhiteSpace(_keyViewerPreviewError))
        {
            ImGui.TextColored(ErrorColor, _keyViewerPreviewError);
            return;
        }
        if (preview.Faulted)
        {
            ImGui.TextColored(
                ErrorColor,
                L10n.Get("PcCompat_KeyViewerPreviewFaulted", preview.Fault ?? "unknown"));
            return;
        }

        var feature = preview.Features.FirstOrDefault(candidate =>
            string.Equals(candidate.FeatureId, featureOverride.FeatureId,
                StringComparison.Ordinal));
        if (feature?.ConsumerActive != true)
        {
            var reason = _keyViewerLoweringStatus ?? feature?.ConsumerReason ??
                L10n.Get("PcCompat_KeyViewerStatusNotApplied");
            ImGui.PushTextWrapPos();
            ImGui.TextColored(
                WarningColor,
                L10n.Get("PcCompat_KeyViewerStatusNeedsAdvanced", reason));
            ImGui.PopTextWrapPos();
            return;
        }

        var mode = GetKeyViewerInputModeLabel(feature.InputMode);
        if (preview.EventCount == 0)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(
                WarningColor,
                L10n.Get("PcCompat_KeyViewerStatusWaiting", mode));
            ImGui.PopTextWrapPos();
            return;
        }
        ImGui.TextColored(
            SuccessColor,
            L10n.Get(
                "PcCompat_KeyViewerStatusReceiving",
                mode,
                preview.EventCount,
                feature.TransitionCount));
    }

    private static string[] GetKeyViewerInputModeLabels()
        =>
        [
            L10n.Get("PcCompat_KeyViewerInputModeAuto"),
            L10n.Get("PcCompat_KeyViewerInputModeTouch"),
            L10n.Get("PcCompat_KeyViewerInputModeExternal"),
            L10n.Get("PcCompat_KeyViewerInputModeHybrid")
        ];

    private static string GetKeyViewerInputModeLabel(PcCompatKeyViewerInputMode mode)
    {
        var labels = GetKeyViewerInputModeLabels();
        var index = (int)mode;
        return index >= 0 && index < labels.Length ? labels[index] : mode.ToString();
    }

    private void RenderKeyViewerAdapterStatus()
    {
        RefreshKeyViewerAdapter();
        RefreshKeyViewerOverrides();
        ImGui.Text(FontAwesome7.Keyboard + " " + L10n.Get("PcCompat_KeyViewerAdapterTitle"));
        ImGui.PushTextWrapPos();
        ImGui.TextDisabled(L10n.Get("PcCompat_KeyViewerAdapterHint"));
        ImGui.PopTextWrapPos();

        if (!string.IsNullOrWhiteSpace(_keyViewerAdapterError))
        {
            ImGui.TextColored(ErrorColor, _keyViewerAdapterError);
            return;
        }
        if (_keyViewerAdapter == null)
        {
            ImGui.TextDisabled(L10n.Get("PcCompat_KeyViewerAdapterNone"));
            return;
        }

        var inputOrigin = PcCompatInputOriginRuntime.GetCurrent();
        ImGui.TextDisabled(
            L10n.Get("PcCompat_KeyViewerInputOrigin", inputOrigin.ToString()));
        RenderKeyViewerPreviewStatus();

        foreach (var feature in _keyViewerAdapter.Features)
        {
            var coreReady = PcCompatKeyViewerAdapterValidator.IsCoreReady(feature);
            var featureLabel = $"{feature.DisplayName}##adapter_{Id}_{feature.Id}";
            if (!ImGui.TreeNode(featureLabel))
                continue;

            ImGui.TextColored(
                coreReady ? SuccessColor : WarningColor,
                coreReady
                    ? L10n.Get("PcCompat_KeyViewerAdapterCoreReady")
                    : L10n.Get("PcCompat_KeyViewerAdapterDiagnosticOnly"));
            if (ImGui.BeginTable(
                    $"KeyViewerAdapterCapabilities##{Id}_{feature.Id}",
                    3,
                    ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn(L10n.Get("PcCompat_KeyViewerAdapterCapability"),
                    ImGuiTableColumnFlags.WidthFixed,
                    130 * UiScale);
                ImGui.TableSetupColumn(L10n.Get("PcCompat_KeyViewerAdapterStatus"),
                    ImGuiTableColumnFlags.WidthFixed,
                    110 * UiScale);
                ImGui.TableSetupColumn(L10n.Get("PcCompat_KeyViewerAdapterFirstBreak"));
                ImGui.TableHeadersRow();
                foreach (var (name, evidence) in EnumerateKeyViewerEvidence(feature))
                    RenderKeyViewerEvidenceRow(name, evidence);
                ImGui.EndTable();
            }
            RenderKeyViewerManualControls(feature);
            ImGui.TreePop();
        }

        if (_keyViewerOverridesDirty)
        {
            ImGui.TextColored(WarningColor, L10n.Get("PcCompat_KeyViewerOverrideUnsaved"));
        }
        if (!string.IsNullOrWhiteSpace(_keyViewerOverridesError))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(ErrorColor, _keyViewerOverridesError);
            ImGui.PopTextWrapPos();
        }
        else if (!string.IsNullOrWhiteSpace(_keyViewerOverridesStatus))
        {
            ImGui.TextColored(SuccessColor, _keyViewerOverridesStatus);
        }
        if (ImGui.Button(
                FontAwesome7.FloppyDisk + " " +
                L10n.Get("PcCompat_KeyViewerOverrideSave") +
                $"##keyviewer_override_save_{Id}"))
            SaveKeyViewerOverrides();
    }

    private void RefreshKeyViewerAdapter()
    {
        var bundle = PcCompatRuntime.GetManagedAssemblyBundle(Id);
        var path = bundle?.KeyViewerAdapterPath;
        if (string.Equals(path, _keyViewerAdapterPath, StringComparison.Ordinal) &&
            (_keyViewerAdapter != null || bundle == null))
            return;

        _keyViewerAdapterPath = path;
        _keyViewerAdapter = null;
        _keyViewerAdapterError = null;
        _keyViewerOverridesPath = null;
        _keyViewerOverridesStatus = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            var scanIssue = DescribeKeyViewerScanIssues(bundle?.KeyViewerScanIssuesPath);
            _keyViewerAdapterError = scanIssue == "none"
                ? "KeyViewer behavior scan produced no adapter candidate."
                : scanIssue;
            RecordKeyViewerAdapterState(
                $"stage=adapter outcome=missing cache={bundle?.CacheKey ?? "none"} " +
                $"issues={scanIssue}",
                warning: false);
            return;
        }
        try
        {
            var document = PcCompatKeyViewerAdapterDocument.FromJson(File.ReadAllText(path));
            if (document == null)
                throw new InvalidDataException("KeyViewer adapter JSON is empty.");
            var validation = PcCompatKeyViewerAdapterValidator.Validate(document);
            if (!validation.IsValid)
                throw new InvalidDataException(string.Join("; ", validation.Errors));
            _keyViewerAdapter = document;
            RecordKeyViewerAdapterState(
                $"stage=adapter outcome=ready path={path} features={document.Features.Count}",
                warning: false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            _keyViewerAdapterError = $"{exception.GetType().Name}: {exception.Message}";
            RecordKeyViewerAdapterState(
                $"stage=adapter outcome=failed path={path} error={_keyViewerAdapterError}",
                warning: true);
        }
    }

    private void RefreshKeyViewerOverrides()
    {
        var path = PcCompatKeyViewerOverrideStore.GetPath(_manifest.FolderPath);
        if (string.Equals(path, _keyViewerOverridesPath, StringComparison.Ordinal))
            return;

        _keyViewerOverridesPath = path;
        _keyViewerOverrides = null;
        _keyViewerOverridesError = null;
        _keyViewerOverridesStatus = null;
        _keyViewerOverridesDirty = false;
        if (_keyViewerAdapter == null)
            return;

        var document = PcCompatKeyViewerOverrideStore.Load(
            _manifest.FolderPath,
            out var loadError);
        if (!string.IsNullOrWhiteSpace(loadError))
        {
            _keyViewerOverridesError = loadError;
            RecordKeyViewerAdapterState(
                $"stage=override outcome=load-failed path={path} error={loadError}",
                warning: true);
            return;
        }
        if (document == null)
        {
            var recommended = PcCompatKeyViewerOverrideStore.CreateRecommendedFor(
                _keyViewerAdapter);
            _keyViewerOverrides = recommended;
            if (!recommended.Features.Any(feature => feature.Enabled))
                return;
            try
            {
                PcCompatKeyViewerOverrideStore.Save(_manifest.FolderPath, recommended);
                _keyViewerOverridesStatus = L10n.Get(
                    "PcCompat_KeyViewerRecommendedApplied");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _keyViewerOverridesError = L10n.Get(
                    "PcCompat_KeyViewerOverrideSaveFailed",
                    exception.Message);
            }
            RecordKeyViewerAdapterState(
                $"stage=override outcome=recommended path={path} " +
                $"enabled={recommended.Features.Count(feature => feature.Enabled)} " +
                $"saveError={_keyViewerOverridesError ?? "none"}",
                warning: _keyViewerOverridesError != null);
            return;
        }

        var validation = PcCompatKeyViewerOverrideStore.Validate(document, _keyViewerAdapter);
        if (!validation.IsValid)
        {
            if (PcCompatKeyViewerOverrideStore.TryRebase(
                    document,
                    _keyViewerAdapter,
                    out var rebased,
                    out var rebaseSummary))
            {
                _keyViewerOverrides = rebased;
                try
                {
                    PcCompatKeyViewerOverrideStore.Save(_manifest.FolderPath, rebased!);
                    _keyViewerOverridesStatus = L10n.Get(
                        "PcCompat_KeyViewerRecommendedApplied");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _keyViewerOverridesError = L10n.Get(
                        "PcCompat_KeyViewerOverrideSaveFailed",
                        exception.Message);
                }
                RecordKeyViewerAdapterState(
                    $"stage=override outcome=rebased path={path} {rebaseSummary} " +
                    $"saveError={_keyViewerOverridesError ?? "none"}",
                    warning: true);
                return;
            }
            PcCompatKeyViewerLabelProjectionRuntime.Unregister(Id);
            PcCompatKeyViewerPreviewRuntime.Unregister(Id);
            PcCompatKeyViewerFallbackRuntime.Unregister(Id);
            _keyViewerOverridesError = L10n.Get(
                "PcCompat_KeyViewerOverrideStale",
                string.Join("; ", validation.Errors.Take(3)));
            RecordKeyViewerAdapterState(
                $"stage=override outcome=stale-rejected path={path} " +
                $"validation={string.Join("; ", validation.Errors.Take(3))} " +
                $"rebase={rebaseSummary}",
                warning: true);
            return;
        }
        _keyViewerOverrides = document;
        RecordKeyViewerAdapterState(
            $"stage=override outcome=ready path={path} features={document.Features.Count}",
            warning: false);
    }

    private void RecordKeyViewerAdapterState(string state, bool warning)
    {
        if (string.Equals(state, _keyViewerAdapterDiagnosticState, StringComparison.Ordinal))
            return;
        _keyViewerAdapterDiagnosticState = state;
        var message = $"keyviewer adapter state mod={Id} {state}";
        if (warning)
            Logger.Warn(nameof(PcCompatModPlugin), message);
        else
            Logger.Info(nameof(PcCompatModPlugin), message);
    }

    private static string DescribeKeyViewerScanIssues(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return "none";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() == 0)
            {
                return "none";
            }
            var first = document.RootElement[0];
            var code = first.TryGetProperty("code", out var codeElement)
                ? codeElement.GetString() ?? "unknown"
                : "unknown";
            var message = first.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? "unknown"
                : "unknown";
            return $"{code}: {message.Replace('\r', ' ').Replace('\n', ' ')}";
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    private void RenderKeyViewerManualControls(PcCompatKeyViewerFeatureAdapter feature)
    {
        ImGui.Separator();
        if (!ImGui.TreeNode(
                FontAwesome7.UserGear + " " +
                L10n.Get("PcCompat_KeyViewerManualTitle") +
                $"##keyviewer_advanced_{Id}_{feature.Id}"))
            return;
        ImGui.PushTextWrapPos();
        ImGui.TextDisabled(L10n.Get("PcCompat_KeyViewerManualHint"));
        ImGui.PopTextWrapPos();

        var featureOverride = FindOrCreateKeyViewerFeatureOverride(feature, create: false);
        if (featureOverride == null)
        {
            ImGui.TextDisabled(L10n.Get("PcCompat_KeyViewerOverrideNotConfigured"));
            ImGui.TreePop();
            return;
        }

        if (feature.Roles.Count == 0)
        {
            ImGui.TextDisabled(L10n.Get("PcCompat_KeyViewerNoRoleCandidates"));
            ImGui.TreePop();
            return;
        }

        ImGui.TextDisabled(L10n.Get("PcCompat_KeyViewerRoleCandidates"));
        foreach (var roleGroup in feature.Roles.GroupBy(role => role.Role, StringComparer.Ordinal))
        {
            ImGui.TextUnformatted(roleGroup.Key);
            foreach (var role in roleGroup)
            {
                var confirmed = PcCompatKeyViewerOverrideStore.HasConfirmedRole(
                    featureOverride,
                    role);
                var candidateKey = PcCompatKeyViewerOverrideStore.GetCandidateKey(
                    role.AssemblyName,
                    role.TypeName,
                    role.MemberName,
                    role.MemberKind);
                var label = $"{role.TypeName}::{role.MemberName ?? "(type)"}";
                if (!ImGui.Checkbox(
                        $"{label}##keyviewer_role_{Id}_{feature.Id}_{candidateKey}",
                        ref confirmed))
                    continue;

                featureOverride.Roles.RemoveAll(candidate =>
                    string.Equals(candidate.Role, role.Role, StringComparison.Ordinal));
                if (confirmed)
                {
                    featureOverride.Roles.Add(new PcCompatKeyViewerRoleOverride
                    {
                        Role = role.Role,
                        AssemblyName = role.AssemblyName,
                        TypeName = role.TypeName,
                        MemberName = role.MemberName,
                        MemberKind = role.MemberKind
                    });
                }
                _keyViewerOverridesDirty = true;
                _keyViewerOverridesStatus = null;
            }
        }
        ImGui.TreePop();
    }

    private PcCompatKeyViewerFeatureOverride? FindOrCreateKeyViewerFeatureOverride(
        PcCompatKeyViewerFeatureAdapter feature,
        bool create)
    {
        if (_keyViewerAdapter == null)
            return null;
        if (_keyViewerOverrides == null && create)
            _keyViewerOverrides = PcCompatKeyViewerOverrideStore.CreateFor(_keyViewerAdapter);
        if (_keyViewerOverrides == null)
            return null;

        var existing = _keyViewerOverrides.Features.FirstOrDefault(value =>
            string.Equals(value.FeatureId, feature.Id, StringComparison.Ordinal));
        if (existing != null || !create)
            return existing;

        existing = new PcCompatKeyViewerFeatureOverride
        {
            FeatureId = feature.Id
        };
        _keyViewerOverrides.Features.Add(existing);
        return existing;
    }

    private static IEnumerable<(string Name, PcCompatAdapterEvidence Evidence)>
        EnumerateKeyViewerEvidence(PcCompatKeyViewerFeatureAdapter feature)
    {
        yield return ("input", feature.Capabilities.Input);
        yield return ("lane", feature.Capabilities.Lane);
        yield return ("transition", feature.Capabilities.Transition);
        yield return ("count", feature.Capabilities.Count);
        yield return ("KPS", feature.Capabilities.Kps);
        yield return ("rain", feature.Capabilities.Rain);
        yield return ("presentation", feature.Capabilities.Presentation);
        yield return ("visibility", feature.Capabilities.Visibility);
        yield return ("inputActivation", feature.Capabilities.InputActivation);
        yield return ("settings", feature.Capabilities.Settings);
        yield return ("persistence", feature.Capabilities.Persistence);
    }

    private static void RenderKeyViewerEvidenceRow(
        string name,
        PcCompatAdapterEvidence evidence)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(name);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextColored(evidence.Status switch
        {
            PcCompatAdapterEvidenceStatus.Proven => SuccessColor,
            PcCompatAdapterEvidenceStatus.Probable => WarningColor,
            PcCompatAdapterEvidenceStatus.Ambiguous => WarningColor,
            _ => ErrorColor
        }, evidence.Status.ToString());
        ImGui.TableSetColumnIndex(2);
        ImGui.PushTextWrapPos();
        ImGui.TextWrapped(evidence.FirstBreak ?? string.Join("; ", evidence.Evidence.Take(3)));
        ImGui.PopTextWrapPos();
        if (ImGui.IsItemHovered() && evidence.Evidence.Count != 0)
            ImGui.SetTooltip(string.Join("\n", evidence.Evidence.Take(16)));
    }

    private void RenderLoadProgress()
    {
        var progress = GetLoadProgress();
        if (progress.Stage is "Idle" or "Ready")
            return;

        ImGui.Text(FontAwesome7.Spinner + " " + L10n.Get("PcCompat_LoadProgress"));
        ImGui.ProgressBar(progress.Progress, new Vector2(-1, 0), $"{progress.Progress:P0}  {progress.Stage}");
        ImGui.Spacing();
    }

    private void RenderManagedSelfRenderControl()
    {
        var session = PcCompatRuntime.GetManagedSession(Id);
        var lifecycle = session?.Lifecycle;
        var rewriteError = PcCompatRuntime.GetManagedAssemblyError(Id);
        string? completionError;
        lock (_loadLock)
            completionError = _loadCompletionError;
        var canStart = session is
        {
            UsesRewrittenAssembly: true,
            ActivationPending: false,
            ActivationFailed: false
        } && lifecycle?.State == PcCompatManagedLifecycleState.Loaded;

        string status;
        var statusColor = NormalColor;
        if (session == null && !PcCompatManagedAssemblyRewrite.IsProviderRegistered)
        {
            status = L10n.Get("PcCompat_ManagedSelfRenderProviderUnavailable");
            statusColor = ErrorColor;
        }
        else if (session == null && !string.IsNullOrWhiteSpace(rewriteError))
        {
            status = L10n.Get(
                "PcCompat_ManagedSelfRenderRewriteFailed",
                FirstStatusLine(rewriteError));
            statusColor = ErrorColor;
        }
        else if (session == null && !string.IsNullOrWhiteSpace(completionError))
        {
            status = L10n.Get(
                "PcCompat_ManagedSelfRenderInstallFailed",
                FirstStatusLine(completionError));
            statusColor = ErrorColor;
        }
        else if (session == null)
        {
            status = L10n.Get("PcCompat_ManagedSelfRenderSessionUnavailable");
            statusColor = WarningColor;
        }
        else if (!session.UsesRewrittenAssembly)
        {
            status = L10n.Get("PcCompat_ManagedSelfRenderRewriteUnavailable");
            statusColor = WarningColor;
        }
        else if (session.ManagedPresentationClaimed &&
                 lifecycle?.State == PcCompatManagedLifecycleState.Enabled)
        {
            status = L10n.Get("PcCompat_ManagedSelfRenderActive");
            statusColor = SuccessColor;
        }
        else if (session.ActivationPending)
        {
            status = L10n.Get(
                "PcCompat_ManagedSelfRenderPending",
                session.ActivationStatus ?? "-");
            statusColor = WarningColor;
        }
        else if (session.ActivationFailed ||
                 lifecycle?.State == PcCompatManagedLifecycleState.Faulted)
        {
            status = L10n.Get(
                "PcCompat_ManagedSelfRenderFailed",
                session.ActivationStatus ?? lifecycle?.LastError ?? "-");
            statusColor = ErrorColor;
        }
        else if (lifecycle?.State == PcCompatManagedLifecycleState.Loaded)
        {
            status = L10n.Get("PcCompat_ManagedSelfRenderReady");
            statusColor = SuccessColor;
        }
        else
        {
            status = L10n.Get(
                "PcCompat_ManagedSelfRenderLifecycleUnavailable",
                lifecycle?.State.ToString() ?? "-");
            statusColor = WarningColor;
        }

        ImGui.Text(FontAwesome7.Palette + " " + L10n.Get("PcCompat_ManagedSelfRenderTitle"));
        ImGui.PushTextWrapPos();
        ImGui.TextColored(statusColor, status);
        ImGui.PopTextWrapPos();

        if (!canStart)
            ImGui.BeginDisabled();
        if (ImGui.Button(
                L10n.Get("PcCompat_ManagedSelfRenderStartTest") +
                $"##managed_self_render_{Id}"))
        {
            _managedSelfRenderRequestError =
                PcCompatRuntime.TryRequestManagedSelfRender(Id, out var requestError)
                    ? null
                    : requestError;
        }
        if (!canStart)
            ImGui.EndDisabled();

        if (!string.IsNullOrWhiteSpace(_managedSelfRenderRequestError))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(
                ErrorColor,
                L10n.Get(
                    "PcCompat_ManagedSelfRenderRequestFailed",
                    _managedSelfRenderRequestError));
            ImGui.PopTextWrapPos();
        }
    }

    private void RenderMobileModSettings()
    {
        if (!_supportsStandardUnityHud)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextDisabled(L10n.Get("PcCompat_NoMobileSettingsAdapter"));
            ImGui.PopTextWrapPos();
            return;
        }

        ImGui.Text(FontAwesome7.Gear + " " + L10n.Get("PcCompat_TranslatedOverlaySettings"));
        ImGui.PushTextWrapPos();
        ImGui.TextDisabled(L10n.Get("PcCompat_TranslatedOverlaySettingsHint"));
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        var changed = false;
        changed |= ImGui.Checkbox(L10n.Get("PcCompat_Setting_ShowHud"), ref _mobileSettings.ShowHud);
        changed |= ImGui.Checkbox(L10n.Get("PcCompat_Setting_ShowProgress"), ref _mobileSettings.ShowProgress);
        changed |= ImGui.Checkbox(L10n.Get("PcCompat_Setting_ShowProgressBar"), ref _mobileSettings.ShowProgressBar);
        changed |= ImGui.Checkbox(L10n.Get("PcCompat_Setting_ShowBpm"), ref _mobileSettings.ShowBpm);
        changed |= ImGui.Checkbox(L10n.Get("PcCompat_Setting_ShowCombo"), ref _mobileSettings.ShowCombo);
        changed |= ImGui.Checkbox(L10n.Get("PcCompat_Setting_ShowAttempt"), ref _mobileSettings.ShowAttempt);
        changed |= ImGui.Checkbox(L10n.Get("PcCompat_Setting_ShowMusicTime"), ref _mobileSettings.ShowMusicTime);
        changed |= ImGui.Checkbox(L10n.Get("PcCompat_Setting_ShowMapTime"), ref _mobileSettings.ShowMapTime);
        changed |= ImGui.Checkbox(L10n.Get("PcCompat_Setting_ShowCheckpoint"), ref _mobileSettings.ShowCheckpoint);
        changed |= ImGui.Checkbox(L10n.Get("PcCompat_Setting_ShowBest"), ref _mobileSettings.ShowBest);
        changed |= ImGui.Checkbox(L10n.Get("PcCompat_Setting_KeyViewer"), ref _mobileSettings.ShowKeyViewer);
        if (_mobileSettings.ShowKeyViewer)
        {
            var touchKeyCountIndex = Array.IndexOf(TouchKeyCountOptions, _mobileSettings.TouchKeyCount);
            if (touchKeyCountIndex < 0)
                touchKeyCountIndex = TouchKeyCountOptions.Length - 1;
            if (ImGui.Combo(
                    L10n.Get("PcCompat_Setting_TouchKeyCount"),
                    ref touchKeyCountIndex,
                    TouchKeyCountLabels,
                    TouchKeyCountLabels.Length))
            {
                _mobileSettings.TouchKeyCount = TouchKeyCountOptions[touchKeyCountIndex];
                changed = true;
            }
        }
        changed |= ImGui.Checkbox(L10n.Get("PcCompat_Setting_ShowAccuracy"), ref _mobileSettings.ShowAccuracy);
        changed |= ImGui.Checkbox(L10n.Get("PcCompat_Setting_ShowXAccuracy"), ref _mobileSettings.ShowXAccuracy);
        changed |= ImGui.Checkbox(L10n.Get("PcCompat_Setting_ShowLastJudgement"), ref _mobileSettings.ShowLastJudgement);
        changed |= ImGui.Checkbox(L10n.Get("PcCompat_Setting_ShowHitTiming"), ref _mobileSettings.ShowHitTiming);
        changed |= ImGui.Checkbox(L10n.Get("PcCompat_Setting_ShowPlayerCount"), ref _mobileSettings.ShowPlayerCount);
        changed |= ImGui.Checkbox(L10n.Get("PcCompat_Setting_ShowTechnicalDiagnostics"), ref _mobileSettings.ShowTechnicalDiagnostics);

        ImGui.Spacing();
        RenderResourceChangerSettings(ref changed);

        ImGui.Spacing();
        changed |= ImGui.SliderFloat(
            L10n.Get("PcCompat_Setting_HudScale"),
            ref _mobileSettings.HudScale,
            0.5f,
            2.5f,
            "%.2fx");
        changed |= ImGui.DragFloat(
            L10n.Get("PcCompat_Setting_PositionX"),
            ref _mobileSettings.PositionX,
            1f,
            0f,
            4096f,
            "%.0f px");
        changed |= ImGui.DragFloat(
            L10n.Get("PcCompat_Setting_PositionY"),
            ref _mobileSettings.PositionY,
            1f,
            0f,
            4096f,
            "%.0f px");
        changed |= ImGui.SliderFloat(
            L10n.Get("PcCompat_Setting_BackgroundOpacity"),
            ref _mobileSettings.BackgroundOpacity,
            0f,
            1f,
            "%.2f");

        if (changed)
        {
            _mobileSettings.Normalize();
            _mobileSettingsDirty = true;
            _mobileSettingsStatus = null;
            ApplyResourceChangerSettings();
            unchecked
            {
                _hudSettingsGeneration++;
            }
            _unityHudFrameRevision = -1;
        }

        ImGui.Spacing();
        if (ImGui.Button(FontAwesome7.FloppyDisk + " " + L10n.Get("Btn.Save")))
            SaveMobileSettings();
        ImGui.SameLine();
        if (ImGui.Button(FontAwesome7.RotateLeft + " " + L10n.Get("PcCompat_Setting_ResetDefaults")))
        {
            _mobileSettings = new PcCompatMobileSettings();
            _mobileSettingsDirty = true;
            _mobileSettingsStatus = L10n.Get("PcCompat_Setting_DefaultsRestored");
            ApplyResourceChangerSettings();
            unchecked
            {
                _hudSettingsGeneration++;
            }
            _unityHudFrameRevision = -1;
        }

        if (_mobileSettingsDirty)
        {
            ImGui.SameLine();
            ImGui.TextColored(WarningColor, L10n.Get("PcCompat_Setting_Unsaved"));
        }
        if (!string.IsNullOrWhiteSpace(_mobileSettingsStatus))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(_mobileSettingsDirty ? WarningColor : SuccessColor, _mobileSettingsStatus);
            ImGui.PopTextWrapPos();
        }

        RenderMobileDataStatus();
    }

    private void RenderResourceChangerSettings(ref bool changed)
    {
        if (PcCompatResourceChangerRuntime.TryGetState(_manifest.Id, out var resourceState) &&
            resourceState.ManagedSource)
        {
            _mobileSettings.ResourceChangerChangeRabbit = resourceState.ChangeRabbit;
            _mobileSettings.ResourceChangerChangeBallColor = resourceState.ChangeBallColor;
            _mobileSettings.ResourceChangerChangeTileColor = resourceState.ChangeTileColor;
        }
        ImGui.Separator();
        ImGui.Text(FontAwesome7.Palette + " " + L10n.Get("PcCompat_Setting_ResourceChanger"));
        ImGui.PushTextWrapPos();
        ImGui.TextDisabled(L10n.Get("PcCompat_ResourceChangerHint"));
        ImGui.PopTextWrapPos();
        changed |= ImGui.Checkbox(
            L10n.Get("PcCompat_Setting_ResourceChangerRabbit"),
            ref _mobileSettings.ResourceChangerChangeRabbit);
        changed |= ImGui.Checkbox(
            L10n.Get("PcCompat_Setting_ResourceChangerBallColor"),
            ref _mobileSettings.ResourceChangerChangeBallColor);
        changed |= ImGui.Checkbox(
            L10n.Get("PcCompat_Setting_ResourceChangerTileColor"),
            ref _mobileSettings.ResourceChangerChangeTileColor);

        if (!PcCompatResourceChangerRuntime.IsSettingsSinkRegistered)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(WarningColor, L10n.Get("PcCompat_ResourceChangerNativeUnavailable"));
            ImGui.PopTextWrapPos();
        }
    }

    private void RenderMobileDataStatus()
    {
        var game = PcCompatReversePatchBridge.Snapshot();
        var overlay = PcCompatOverlayRuntime.Snapshot(Id);
        var hasAccuracy = TryGetAccuracy(overlay, game, out var percentAcc, out var percentXAcc);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text(FontAwesome7.ChartSimple + " " + L10n.Get("PcCompat_CurrentData"));
        if (!overlay.ProviderAvailable)
        {
            ImGui.TextColored(WarningColor, L10n.Get("PcCompat_CurrentDataUnavailable"));
            return;
        }

        if (ImGui.BeginTable(
                "PcCompatCurrentMobileData",
                2,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            RenderMobileDataRow(L10n.Get("PcCompat_Data_Overlay"), overlay.Visible
                ? L10n.Get("PcCompat_Data_Visible")
                : L10n.Get("PcCompat_Data_Hidden"));
            RenderMobileDataRow(L10n.Get("PcCompat_Data_Progress"), FormatPercent(overlay.Progress, overlay.FloorMoveCount > 0 || overlay.ShowCount > 0));
            RenderMobileDataRow(L10n.Get("PcCompat_Data_Bpm"), FormatBpm(overlay));
            RenderMobileDataRow(L10n.Get("PcCompat_Data_Combo"), overlay.ComboCount.ToString());
            RenderMobileDataRow(L10n.Get("PcCompat_Data_Attempt"), overlay.AttemptCount.ToString());
            RenderMobileDataRow(L10n.Get("PcCompat_Data_MusicTime"), FormatTimeRange(overlay.MusicTime, overlay.MusicTotalTime));
            RenderMobileDataRow(L10n.Get("PcCompat_Data_MapTime"), FormatTimeRange(overlay.MapTime, overlay.MapTotalTime));
            RenderMobileDataRow(L10n.Get("PcCompat_Data_Checkpoint"), FormatCheckpoint(overlay));
            RenderMobileDataRow(L10n.Get("PcCompat_Data_Input"), $"0x{overlay.InputHeldMask:X8} / {overlay.InputTotalCount}");
            RenderMobileDataRow(L10n.Get("PcCompat_Data_Accuracy"), FormatAccuracy(percentAcc, hasAccuracy));
            RenderMobileDataRow(L10n.Get("PcCompat_Data_XAccuracy"), FormatAccuracy(percentXAcc, hasAccuracy));
            RenderMobileDataRow(L10n.Get("PcCompat_Data_LastJudgement"), overlay.LastHitMarginName);
            RenderMobileDataRow(L10n.Get("PcCompat_Data_HitTiming"), $"{overlay.LastHitTimingMs:+0.00;-0.00;0.00} ms");
            RenderMobileDataRow(L10n.Get("PcCompat_Data_PlayerCount"), Math.Max(game.PlayerCount, overlay.PlayerCount).ToString());
            ImGui.EndTable();
        }
    }

    private static void RenderMobileDataRow(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextDisabled(label);
        ImGui.TableSetColumnIndex(1);
        ImGui.Text(value);
    }

    private void SaveMobileSettings()
    {
        try
        {
            PcCompatMobileSettingsStore.Save(_manifest.FolderPath, _mobileSettings);
            ApplyResourceChangerSettings();
            _mobileSettingsDirty = false;
            _mobileSettingsStatus = L10n.Get("PcCompat_Setting_Saved");
        }
        catch (Exception ex)
        {
            _mobileSettingsStatus = L10n.Get("PcCompat_Setting_SaveFailed", ex.Message);
            Logger.Error(nameof(PcCompatModPlugin), $"Mobile settings save failed for {Id}: {ex}");
        }
    }

    private void SaveMobileSettingsIfDirty()
    {
        if (_mobileSettingsDirty)
            SaveMobileSettings();
    }

    private void SaveKeyViewerOverrides()
    {
        if (_keyViewerAdapter == null)
        {
            _keyViewerOverridesError = L10n.Get("PcCompat_KeyViewerAdapterNone");
            return;
        }

        _keyViewerOverrides ??= PcCompatKeyViewerOverrideStore.CreateFor(_keyViewerAdapter);
        var validation = PcCompatKeyViewerOverrideStore.Validate(
            _keyViewerOverrides,
            _keyViewerAdapter);
        if (!validation.IsValid)
        {
            PcCompatKeyViewerLabelProjectionRuntime.Unregister(Id);
            PcCompatKeyViewerPreviewRuntime.Unregister(Id);
            PcCompatKeyViewerFallbackRuntime.Unregister(Id);
            _keyViewerOverridesError = L10n.Get(
                "PcCompat_KeyViewerOverrideStale",
                string.Join("; ", validation.Errors.Take(3)));
            return;
        }

        try
        {
            PcCompatKeyViewerOverrideStore.Save(
                _manifest.FolderPath,
                _keyViewerOverrides);
            _keyViewerOverridesDirty = false;
            _keyViewerOverridesError = null;
            _keyViewerOverridesStatus = L10n.Get("PcCompat_KeyViewerOverrideSaved");
            RefreshKeyViewerPreviewRegistration();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _keyViewerOverridesError = L10n.Get(
                "PcCompat_KeyViewerOverrideSaveFailed",
                exception.Message);
            _keyViewerOverridesStatus = null;
        }
    }

    private void SaveKeyViewerOverridesIfDirty()
    {
        if (_keyViewerOverridesDirty)
            SaveKeyViewerOverrides();
    }

    private void ApplyResourceChangerSettings()
    {
        PcCompatResourceChangerRuntime.TryApply(
            _manifest.Id,
            _mobileSettings);
        PcCompatRuntime.TryApplyManagedResourceChangerSettings(
            _manifest.Id,
            _mobileSettings.ResourceChangerChangeRabbit,
            _mobileSettings.ResourceChangerChangeBallColor,
            _mobileSettings.ResourceChangerChangeTileColor,
            out _);
    }

    private void UpdateLoadProgress(int generation, float progress, string stage)
    {
        lock (_loadLock)
        {
            if (generation != _loadGeneration)
                return;
            _loadProgress = progress;
            _loadStage = stage;
        }
    }

    private void RenderManagedTranslationStatus()
    {
        ImGui.Text(FontAwesome7.ListCheck + " " + L10n.Get("PcCompat_ManagedTranslation"));

        var scan = PcCompatRuntime.GetStaticScanReport(Id);
        var translation = PcCompatRuntime.GetCallbackTranslationReport(Id);
        var recipe = PcCompatRuntime.GetRecipeReport(Id);
        var managedSession = PcCompatRuntime.GetManagedSession(Id);

        if (ImGui.BeginTable("PcCompatManagedStatus", 4,
            ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            RenderMetricRow(
                L10n.Get("PcCompat_StaticPatches"),
                scan == null ? "-" : $"{scan.ActivePatches.Count}/{scan.Patches.Count}",
                L10n.Get("PcCompat_StaticIssues"),
                scan?.Issues.Count.ToString() ?? "-");

            RenderMetricRow(
                L10n.Get("PcCompat_Callbacks"),
                translation == null ? "-" : $"{translation.TranslatedCount}/{translation.Items.Count}",
                L10n.Get("PcCompat_Unsupported"),
                translation?.UnsupportedCount.ToString() ?? "-",
                secondValueColor: translation?.UnsupportedCount > 0 ? WarningColor : null);

            var notMapped = translation?.Items.Count(item => item.Status == PcCompatCallbackTranslationStatus.NotMapped);
            var skipped = translation?.Items.Count(item => item.Status == PcCompatCallbackTranslationStatus.Skipped);
            RenderMetricRow(
                L10n.Get("PcCompat_NotMapped"),
                notMapped?.ToString() ?? "-",
                L10n.Get("PcCompat_Skipped"),
                skipped?.ToString() ?? "-");

            RenderMetricRow(
                L10n.Get("PcCompat_RecipeRules"),
                recipe?.Rules.Count.ToString() ?? "-",
                L10n.Get("PcCompat_RecipeUnsupported"),
                recipe?.Unsupported.Count.ToString() ?? "-",
                secondValueColor: recipe?.Unsupported.Count > 0 ? WarningColor : null);

            var resourcePlan = PcCompatResourceRecipeRuntime.GetPlan(Id);
            var resourceSummary = PcCompatResourceRecipeRuntime.GetReadinessSummary(Id);
            var resourceReady = resourceSummary.ReadyCandidateCount;
            var resourceTotal = resourcePlan?.Candidates.Count ?? 0;
            var virtualGeneration = managedSession?.ResourceSessionGeneration ??
                                    (PcCompatResourceRecipeRuntime.TryGetSessionGeneration(
                                        Id,
                                        out var recipeGeneration)
                                        ? recipeGeneration
                                        : 0);
            var virtualReadiness = virtualGeneration > 0
                ? PcCompatVirtualBundleRegistry.GetSessionReadiness(Id, virtualGeneration)
                : null;
            RenderMetricRow(
                "Resource groups",
                resourcePlan?.FeatureGroups.Count.ToString() ?? "-",
                "Raw bundle candidates",
                resourcePlan == null
                    ? "-"
                    : $"ready={resourceReady} controlled={resourceSummary.ControlledCandidateCount}/{resourceTotal}");

            RenderMetricRow(
                "VirtualBundle",
                virtualReadiness == null || !virtualReadiness.SessionPresent
                    ? "-"
                    : virtualReadiness.IsReady ? "ready" : "pending",
                "Required assets",
                virtualReadiness == null || !virtualReadiness.SessionPresent
                    ? "-"
                    : $"ready={virtualReadiness.RequiredReadyCount}/" +
                      $"{virtualReadiness.RequiredAssetCount} " +
                      $"pending={virtualReadiness.RequiredPendingCount} " +
                      $"unsupported={virtualReadiness.RequiredUnsupportedCount} " +
                      $"failed={virtualReadiness.RequiredFailedCount}",
                secondValueColor: virtualReadiness is { IsReady: false } ? WarningColor : null);

            RenderMetricRow(
                "Managed lifecycle",
                managedSession?.Lifecycle.State.ToString() ?? "-",
                "Presentation owner",
                managedSession == null
                    ? "recipe"
                    : managedSession.ManagedPresentationClaimed
                        ? "managed"
                        : managedSession.ActivationPending
                            ? "pending"
                            : "recipe",
                secondValueColor: managedSession?.ActivationFailed == true ? ErrorColor : null);
            ImGui.EndTable();
        }

        if (managedSession != null &&
            (managedSession.ActivationPending || managedSession.ActivationFailed) &&
            !string.IsNullOrWhiteSpace(managedSession.ActivationStatus))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(
                managedSession.ActivationFailed ? ErrorColor : WarningColor,
                "managed self-render: " + managedSession.ActivationStatus);
            ImGui.PopTextWrapPos();
        }
        var resourceSession = PcCompatResourceRecipeRuntime.GetPlan(Id);
        var resourceReadiness = PcCompatResourceRecipeRuntime.GetReadinessSummary(Id);
        var virtualResourceGeneration = managedSession?.ResourceSessionGeneration ??
                                        (PcCompatResourceRecipeRuntime.TryGetSessionGeneration(
                                            Id,
                                            out var loadedRecipeGeneration)
                                            ? loadedRecipeGeneration
                                            : 0);
        var virtualResourceReadiness = virtualResourceGeneration > 0
            ? PcCompatVirtualBundleRegistry.GetSessionReadiness(Id, virtualResourceGeneration)
            : null;
        var localResourceRecipePath = Path.Combine(_manifest.FolderPath, ".pccompat", "resource_recipe.bin");
        if (resourceSession == null)
        {
            if (ImGui.CollapsingHeader("Resource session plan"))
            {
                ImGui.PushTextWrapPos();
                if (File.Exists(localResourceRecipePath))
                {
                    ImGui.TextColored(
                        WarningColor,
                        "resource_recipe.bin exists but is not loaded into the session plan.");
                }
                else
                {
                    ImGui.TextColored(
                        WarningColor,
                        "resource_recipe.bin missing. Run ResourceRecipeTool compile or PcCompatProbe --recipe-only.");
                }
                ImGui.TextDisabled("path=" + localResourceRecipePath);
                ImGui.TextDisabled(
                    "AssetBundle load remains opt-in (" +
                    PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable +
                    "=1); current HUD visuals are unchanged.");
                ImGui.PopTextWrapPos();
            }
        }
        else if (ImGui.CollapsingHeader("Resource session plan"))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextDisabled(
                $"compatibility={resourceSession.Compatibility} " +
                $"groups={resourceSession.FeatureGroups.Count} " +
                $"candidates={resourceSession.Candidates.Count} " +
                $"rawReady={resourceReadiness.ReadyCandidateCount} " +
                $"rawControlled={resourceReadiness.ControlledCandidateCount} " +
                $"queued={resourceReadiness.QueuedCandidateCount} " +
                $"loaded={resourceReadiness.LoadedCandidateCount} " +
                $"loadEnabled={resourceReadiness.RuntimeLoadEnabled} " +
                $"sink={resourceReadiness.LoadSinkRegistered} " +
                $"compiledResources={(string.IsNullOrWhiteSpace(resourceSession.CompiledResourcesDirectory) ? "none" : "yes")}");
            if (virtualResourceReadiness is { SessionPresent: true })
            {
                ImGui.TextDisabled(
                    $"virtualBundle={(virtualResourceReadiness.IsReady ? "ready" : "pending")} " +
                    $"required={virtualResourceReadiness.RequiredReadyCount}/" +
                    $"{virtualResourceReadiness.RequiredAssetCount} " +
                    $"pending={virtualResourceReadiness.RequiredPendingCount} " +
                    $"unsupported={virtualResourceReadiness.RequiredUnsupportedCount} " +
                    $"failed={virtualResourceReadiness.RequiredFailedCount} " +
                    $"optional={virtualResourceReadiness.OptionalReadyCount}/" +
                    $"{virtualResourceReadiness.OptionalAssetCount}");
            }
            if (!resourceReadiness.RuntimeLoadEnabled)
            {
                ImGui.TextDisabled(
                    "AssetBundle load is opt-in (" +
                    PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable +
                    "=1); current HUD visuals are unchanged.");
                if (ImGui.SmallButton(L10n.Get("PcCompat_ResourceEnableRuntimeLoad") + $"##resource_gate_{Id}"))
                {
                    try
                    {
                        Environment.SetEnvironmentVariable(
                            PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable,
                            "1");
                        _resourceLoadStatusError = !PcCompatResourceRecipeRuntime.IsRuntimeLoadEnabled();
                        _resourceLoadStatus = _resourceLoadStatusError
                            ? L10n.Get("PcCompat_ResourceEnableRuntimeLoadFailed", "gate remained disabled")
                            : L10n.Get("PcCompat_ResourceRuntimeLoadEnabled");
                        if (!_resourceLoadStatusError)
                        {
                            foreach (var group in resourceSession.FeatureGroups)
                                PcCompatResourceRecipeRuntime.TryEnsureFeatureGroupLoaded(Id, group.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        _resourceLoadStatusError = true;
                        _resourceLoadStatus = L10n.Get(
                            "PcCompat_ResourceEnableRuntimeLoadFailed",
                            ex.Message);
                    }
                }
            }
            foreach (var group in resourceSession.FeatureGroups.Take(8))
            {
                ImGui.Text(
                    $"group {group.Id}: assets={group.AssetNames.Count} policy={group.LoadPolicy} sha={TruncateSha(group.SelectedCandidateSha256Hex)}");
            }
            foreach (var candidate in resourceSession.Candidates.Take(8))
            {
                var color = candidate.Status switch
                {
                    PcCompatResourceCandidateStatus.Ready or
                    PcCompatResourceCandidateStatus.Loaded => SuccessColor,
                    PcCompatResourceCandidateStatus.LoadQueued => WarningColor,
                    PcCompatResourceCandidateStatus.Controlled or
                    PcCompatResourceCandidateStatus.Rejected => WarningColor,
                    PcCompatResourceCandidateStatus.Missing or
                    PcCompatResourceCandidateStatus.LoadFailed => ErrorColor,
                    _ => WarningColor
                };
                ImGui.TextColored(
                    color,
                    $"{candidate.Status} auto={candidate.AutoLoadAllowed} platform={candidate.PlatformHint} " +
                    $"policy={candidate.LoadPolicy} file={candidate.FileName}");
                RenderResourceCandidateAction(candidate, resourceReadiness);
            }
            if (!string.IsNullOrWhiteSpace(_resourceLoadStatus))
                ImGui.TextColored(_resourceLoadStatusError ? ErrorColor : SuccessColor, _resourceLoadStatus);
            ImGui.PopTextWrapPos();
        }

        RenderResourceLoadConfirmation();

        if (translation != null && translation.Items.Any(item =>
                item.Status is PcCompatCallbackTranslationStatus.Unsupported or PcCompatCallbackTranslationStatus.NotMapped))
        {
            if (ImGui.CollapsingHeader(L10n.Get("PcCompat_UnsupportedDetails")))
            {
                foreach (var item in translation.Items.Where(item =>
                             item.Status is PcCompatCallbackTranslationStatus.Unsupported or PcCompatCallbackTranslationStatus.NotMapped))
                {
                    ImGui.PushTextWrapPos();
                    ImGui.TextColored(
                        item.Status == PcCompatCallbackTranslationStatus.Unsupported ? ErrorColor : WarningColor,
                        $"{item.Status}: {item.TargetType}.{item.TargetMethod} -> {item.CallbackType}.{item.CallbackMethod}: {item.Reason}");
                    ImGui.PopTextWrapPos();
                }
            }
        }
    }

    private static string TruncateSha(string? sha)
        => string.IsNullOrWhiteSpace(sha)
            ? "-"
            : sha.Length <= 12 ? sha : sha[..12];

    private void RenderResourceCandidateAction(
        PcCompatResourceSessionCandidate candidate,
        PcCompatResourceReadinessSummary readiness)
    {
        string? label = null;
        var authorization = PcCompatResourceLoadAuthorization.None;
        if (candidate.Status == PcCompatResourceCandidateStatus.Ready && candidate.AutoLoadAllowed)
        {
            label = L10n.Get("PcCompat_ResourceLoad");
        }
        else if (candidate.Status == PcCompatResourceCandidateStatus.Controlled)
        {
            var force = candidate.LoadPolicy.Equals("ForceRequired", StringComparison.OrdinalIgnoreCase) ||
                        candidate.LoadPolicy == "2";
            authorization = force
                ? PcCompatResourceLoadAuthorization.Forced
                : PcCompatResourceLoadAuthorization.Controlled;
            label = L10n.Get(force
                ? "PcCompat_ResourceForceTrial"
                : "PcCompat_ResourceControlledTrial");
        }
        if (label == null)
            return;

        ImGui.SameLine();
        var disabled = !readiness.RuntimeLoadEnabled || !readiness.LoadSinkRegistered;
        if (disabled)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton($"{label}##resource_{Id}_{candidate.Sha256Hex}"))
        {
            if (authorization == PcCompatResourceLoadAuthorization.None)
            {
                SetResourceLoadStatus(
                    PcCompatResourceRecipeRuntime.TryEnsureCandidateLoaded(Id, candidate.Sha256Hex));
            }
            else
            {
                _pendingResourceCandidateSha = candidate.Sha256Hex;
                _pendingResourceAuthorization = authorization;
                ImGui.OpenPopup(ResourceLoadPopupId);
            }
        }
        if (disabled)
            ImGui.EndDisabled();
    }

    private void RenderResourceLoadConfirmation()
    {
        var popupOpen = true;
        if (!ImGui.BeginPopupModal(ResourceLoadPopupId, ref popupOpen, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        var force = _pendingResourceAuthorization == PcCompatResourceLoadAuthorization.Forced;
        ImGui.PushTextWrapPos(520f * UiScale);
        ImGui.TextColored(
            WarningColor,
            L10n.Get(force
                ? "PcCompat_ResourceForceWarning"
                : "PcCompat_ResourceControlledWarning"));
        ImGui.TextDisabled("SHA-256: " + TruncateSha(_pendingResourceCandidateSha));
        ImGui.PopTextWrapPos();

        if (ImGui.Button(L10n.Get("PcCompat_ResourceConfirmLoad")))
        {
            if (string.IsNullOrWhiteSpace(_pendingResourceCandidateSha))
            {
                _resourceLoadStatus = L10n.Get("PcCompat_ResourceLoadMissingCandidate");
                _resourceLoadStatusError = true;
            }
            else if (!PcCompatResourceRecipeRuntime.TryAuthorizeCandidateLoad(
                         Id,
                         _pendingResourceCandidateSha,
                         _pendingResourceAuthorization,
                         out var error))
            {
                _resourceLoadStatus = L10n.Get("PcCompat_ResourceLoadFailed", error ?? "unknown");
                _resourceLoadStatusError = true;
            }
            else
            {
                SetResourceLoadStatus(
                    PcCompatResourceRecipeRuntime.TryEnsureCandidateLoaded(Id, _pendingResourceCandidateSha));
            }
            _pendingResourceCandidateSha = null;
            _pendingResourceAuthorization = PcCompatResourceLoadAuthorization.None;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button(L10n.Get("Btn_Cancel")))
        {
            _pendingResourceCandidateSha = null;
            _pendingResourceAuthorization = PcCompatResourceLoadAuthorization.None;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void SetResourceLoadStatus(PcCompatResourceLoadResult result)
    {
        _resourceLoadStatusError = !result.Success && !result.Pending;
        _resourceLoadStatus = result.Pending
            ? L10n.Get("PcCompat_ResourceLoadQueued")
            : result.Success
                ? L10n.Get("PcCompat_ResourceLoadSucceeded")
                : L10n.Get("PcCompat_ResourceLoadFailed", result.Error ?? "unknown");
    }

    private static void RenderNativeStatus(PcCompatDiagnosticsSnapshot snapshot)
    {
        if (!snapshot.ProviderAvailable)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(ErrorColor, L10n.Get("PcCompat_ProviderUnavailableText"));
            ImGui.PopTextWrapPos();
            return;
        }

        if (ImGui.BeginTable("PcCompatNativeStatus", 4,
            ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            RenderMetricRow(L10n.Get("PcCompat_Provider"), L10n.Get("PcCompat_Available"),
                L10n.Get("PcCompat_Capabilities"), $"0x{snapshot.ApprovedCapabilities:X}", SuccessColor);
            RenderMetricRow(L10n.Get("PcCompat_Bundles"), snapshot.LoadedBundles.ToString(),
                L10n.Get("PcCompat_Targets"), snapshot.LoadedTargets.ToString());
            RenderMetricRow(L10n.Get("PcCompat_Rules"), snapshot.LoadedRules.ToString(),
                L10n.Get("PcCompat_Slots"), snapshot.MergedSlots.ToString());
            RenderMetricRow(L10n.Get("PcCompat_LifecyclePrograms"), snapshot.LoadedUiLifecyclePrograms.ToString(),
                L10n.Get("PcCompat_VmInstructions"), snapshot.LoadedUiBytecodeInstructions.ToString());
            RenderMetricRow(L10n.Get("PcCompat_Pending"), snapshot.PendingSlots.ToString(),
                L10n.Get("PcCompat_Resolved"), snapshot.ResolvedSlots.ToString(),
                snapshot.PendingSlots > 0 ? WarningColor : null);
            RenderMetricRow(L10n.Get("PcCompat_Failed"), snapshot.FailedSlots.ToString(),
                L10n.Get("PcCompat_Blocked"), snapshot.InstallBlockedSlots.ToString(),
                snapshot.FailedSlots > 0 ? ErrorColor : SuccessColor,
                snapshot.InstallBlockedSlots > 0 ? WarningColor : null);
            RenderMetricRow(L10n.Get("PcCompat_Installable"), snapshot.InstallableSlots.ToString(),
                L10n.Get("PcCompat_Installed"), snapshot.InstalledSlots.ToString(),
                null, snapshot.InstalledSlots > 0 ? SuccessColor : null);
            RenderMetricRow(L10n.Get("PcCompat_DispatcherReady"), snapshot.DispatcherReadySlots.ToString(),
                L10n.Get("PcCompat_DispatcherBound"), $"{snapshot.BoundDispatcherSlots}/{snapshot.DispatcherCapacity}",
                null, DispatcherColor(snapshot));
            RenderMetricRow(L10n.Get("PcCompat_SlotRules"), snapshot.SlotRules.ToString(),
                L10n.Get("PcCompat_RuleState"), $"{snapshot.EnabledSlotRules}/{snapshot.DisabledSlotRules}");
            ImGui.EndTable();
        }
    }

    private void RenderDiagnosticsActions(ref PcCompatDiagnosticsSnapshot snapshot)
    {
        ImGui.Spacing();
        if (!snapshot.ProviderAvailable)
            ImGui.BeginDisabled();

        if (ImGui.Button(FontAwesome7.Rotate + " " + L10n.Get("PcCompat_Refresh")))
            snapshot = PcCompatDiagnosticsRuntime.Snapshot(forceRefresh: true);
        ImGui.SameLine();
        if (ImGui.Button(FontAwesome7.MagnifyingGlass + " " + L10n.Get("PcCompat_Resolve")))
            ExecuteDiagnostics(PcCompatDiagnosticsCommand.Resolve, ref snapshot);
        ImGui.SameLine();
        if (ImGui.Button(FontAwesome7.ListCheck + " " + L10n.Get("PcCompat_Prepare")))
            ExecuteDiagnostics(PcCompatDiagnosticsCommand.Prepare, ref snapshot);
        ImGui.SameLine();
        if (ImGui.Button(FontAwesome7.Play + " " + L10n.Get("PcCompat_Install")))
            ExecuteDiagnostics(PcCompatDiagnosticsCommand.Install, ref snapshot);

        if (ImGui.Button(FontAwesome7.ArrowsRotate + " " + L10n.Get("PcCompat_Reload")))
            ExecuteDiagnostics(PcCompatDiagnosticsCommand.ReloadRules, ref snapshot);
        ImGui.SameLine();
        if (ImGui.Button(FontAwesome7.Trash + " " + L10n.Get("PcCompat_ClearRules")))
            ImGui.OpenPopup(ClearPopupId);

        if (!snapshot.ProviderAvailable)
            ImGui.EndDisabled();

        var popupOpen = true;
        if (ImGui.BeginPopupModal(ClearPopupId, ref popupOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 520 * UiScale);
            ImGui.TextWrapped(L10n.Get("PcCompat_ClearWarning"));
            ImGui.PopTextWrapPos();
            ImGui.Spacing();

            if (ImGui.Button(FontAwesome7.Trash + " " + L10n.Get("PcCompat_ClearConfirm")))
            {
                ExecuteDiagnostics(PcCompatDiagnosticsCommand.ClearRules, ref snapshot);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button(L10n.Get("Btn_Cancel")))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private void RenderDiagnosticsResult(PcCompatDiagnosticsSnapshot snapshot)
    {
        if (_lastDiagnosticsOperation != null)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(
                _lastDiagnosticsOperation.Succeeded ? SuccessColor : ErrorColor,
                $"{L10n.Get("PcCompat_LastOperation")} {_lastDiagnosticsOperation.Message}");
            ImGui.PopTextWrapPos();
        }

        if (!string.IsNullOrWhiteSpace(snapshot.LastError))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(ErrorColor, $"{L10n.Get("PcCompat_LastError")} {snapshot.LastError}");
            ImGui.PopTextWrapPos();
        }
        if (snapshot.LatestVmFault.ProviderAvailable)
        {
            var fault = snapshot.LatestVmFault;
            ImGui.PushTextWrapPos();
            ImGui.TextColored(
                ErrorColor,
                $"{L10n.Get("PcCompat_VmFault")} rule={fault.RuleId} count={fault.Count} " +
                $"pc={fault.Pc} opcode={fault.Opcode}: {fault.Message}");
            ImGui.PopTextWrapPos();
        }
    }

    private void RenderDiagnosticsExport(PcCompatDiagnosticsSnapshot snapshot)
    {
        var now = Environment.TickCount64;
        if (now >= _nextExportStatusPoll)
        {
            _nextExportStatusPoll = now + 500;
            _exportStatus = PcCompatDiagnosticsExportRuntime.GetStatus();
        }

        ImGui.Spacing();
        var exportBusy = _exportStatus.State is "Selecting" or "Exporting";
        if (!_exportStatus.ProviderAvailable || exportBusy)
            ImGui.BeginDisabled();
        if (ImGui.Button(FontAwesome7.FileExport + " " + L10n.Get("PcCompat_ExportDiagnostics")))
        {
            var report = BuildDiagnosticsReport(snapshot);
            var fileName = $"pccompat_{SanitizeFileName(Id)}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt";
            if (PcCompatDiagnosticsExportRuntime.RequestExport(fileName, report))
            {
                _exportStatus = new PcCompatDiagnosticsExportStatus
                {
                    ProviderAvailable = true,
                    State = "Selecting",
                    Message = L10n.Get("PcCompat_ExportSelecting")
                };
            }
            else
            {
                _exportStatus = new PcCompatDiagnosticsExportStatus
                {
                    ProviderAvailable = false,
                    State = "Error",
                    Message = "Android document export provider is unavailable."
                };
            }
            _nextExportStatusPoll = 0;
        }
        if (!_exportStatus.ProviderAvailable || exportBusy)
            ImGui.EndDisabled();

        if (_exportStatus.State is not ("Idle" or "Unavailable"))
        {
            ImGui.SameLine();
            ImGui.TextColored(GetExportStateColor(_exportStatus.State),
                $"{_exportStatus.State}: {_exportStatus.Message}");
        }
    }

    private void RenderSlotSummary(PcCompatDiagnosticsSnapshot snapshot)
    {
        ImGui.Separator();
        ImGui.Text(FontAwesome7.List + " " + L10n.Get("PcCompat_SlotDetails"));
        ImGui.SameLine();
        ImGui.Checkbox(L10n.Get("PcCompat_ShowAllSlots"), ref _showAllSlots);

        var slotSummary = _showAllSlots
            ? snapshot.SlotSummary
            : PcCompatDiagnosticsRuntime.GetSlotSummaryForMod(Id);
        ImGui.PushTextWrapPos();
        ImGui.TextDisabled(_showAllSlots
            ? L10n.Get("PcCompat_GlobalSlotHint")
            : L10n.Get("PcCompat_ModSlotHint", Id));
        ImGui.PopTextWrapPos();

        if (ImGui.BeginChild("PcCompatSlotSummary", new Vector2(0, 250 * UiScale), ImGuiChildFlags.None))
        {
            if (string.IsNullOrWhiteSpace(slotSummary))
            {
                ImGui.TextDisabled("-");
            }
            else
            {
                foreach (var line in slotSummary.Split('\n'))
                {
                    ImGui.PushTextWrapPos();
                    ImGui.TextColored(GetSlotLineColor(line), line.TrimEnd('\r'));
                    ImGui.PopTextWrapPos();
                }
            }
        }
        ImGui.EndChild();
    }

    private string BuildDiagnosticsReport(PcCompatDiagnosticsSnapshot snapshot)
    {
        var scan = PcCompatRuntime.GetStaticScanReport(Id);
        var translation = PcCompatRuntime.GetCallbackTranslationReport(Id);
        var recipe = PcCompatRuntime.GetRecipeReport(Id);
        var managedBundle = PcCompatRuntime.GetManagedAssemblyBundle(Id);
        var managedSession = PcCompatRuntime.GetManagedSession(Id);
        var managedLifecycle = managedSession?.Lifecycle;
        var managedSettings = managedSession?.Settings;
        var managedSettingsSchema = managedSession?.SettingsSchema;
        var managedComponentLifecycle = managedSession?.ManagedComponentLifecycle;
        var modSlots = PcCompatDiagnosticsRuntime.GetSlotSummaryForMod(Id);
        string? loadCompletionError;
        lock (_loadLock)
            loadCompletionError = _loadCompletionError;
        var builder = new StringBuilder(8192);

        builder.AppendLine("xphorror.PcModCompat diagnostics");
        builder.AppendLine($"generatedUtc={DateTimeOffset.UtcNow:O}");
        builder.AppendLine($"modId={Id}");
        builder.AppendLine($"modName={Name}");
        builder.AppendLine($"modVersion={Version}");
        builder.AppendLine($"modKind={_manifest.Kind}");
        builder.AppendLine($"entry={_manifest.EntryAssemblyPath}");
        builder.AppendLine();

        builder.AppendLine("[ownedResources]");
        if (_runtimeSession != null && _runtimeKey.IsValid)
        {
            builder.AppendLine(ModOwnedResourceRegistry.CreateAuditSnapshot(
                    new[] { _runtimeSession.Snapshot() },
                    _runtimeKey)
                .ToDiagnosticText(includeResources: true));
        }
        else
        {
            builder.AppendLine("unavailable: runtime generation is not bound");
        }
        builder.AppendLine();

        builder.AppendLine("[managed]");
        builder.AppendLine($"staticPatches={scan?.ActivePatches.Count ?? 0}/{scan?.Patches.Count ?? 0}");
        builder.AppendLine($"scanIssues={scan?.Issues.Count ?? 0}");
        builder.AppendLine($"callbacksTranslated={translation?.TranslatedCount ?? 0}/{translation?.Items.Count ?? 0}");
        builder.AppendLine($"callbacksUnsupported={translation?.UnsupportedCount ?? 0}");
        builder.AppendLine($"callbacksNotMapped={translation?.Items.Count(item => item.Status == PcCompatCallbackTranslationStatus.NotMapped) ?? 0}");
        builder.AppendLine($"recipeRules={recipe?.Rules.Count ?? 0}");
        builder.AppendLine($"recipeUnsupported={recipe?.Unsupported.Count ?? 0}");
        builder.AppendLine($"rewriteCacheKey={managedBundle?.CacheKey ?? "none"}");
        builder.AppendLine($"rewriteProviderRegistered={PcCompatManagedAssemblyRewrite.IsProviderRegistered}");
        builder.AppendLine($"rewriteError={FirstStatusLine(PcCompatRuntime.GetManagedAssemblyError(Id))}");
        builder.AppendLine($"rewriteCacheHit={managedBundle?.CacheHit ?? false}");
        builder.AppendLine($"rewriteInstructions={managedBundle?.RewrittenInstructions ?? 0}");
        builder.AppendLine($"rewritePassthrough={managedBundle?.PassthroughInstructions ?? 0}");
        builder.AppendLine($"rewriteAssembly={managedBundle?.RewrittenAssemblyPath ?? "none"}");
        builder.AppendLine($"rewriteAssemblyCount={managedBundle?.RewrittenAssemblyPaths.Count ?? 0}");
        builder.AppendLine($"rewriteBootstrapAssembly={managedBundle?.BootstrapAssemblyName ?? "none"}");
        builder.AppendLine($"rewriteReport={managedBundle?.ReportPath ?? "none"}");
        builder.AppendLine($"keyViewerAdapter={managedBundle?.KeyViewerAdapterPath ?? "none"}");
        builder.AppendLine($"keyViewerAdapterIssues={managedBundle?.KeyViewerScanIssuesPath ?? "none"}");
        var diagnosticResourceGeneration = managedSession?.ResourceSessionGeneration ??
                                           (PcCompatResourceRecipeRuntime.TryGetSessionGeneration(
                                               Id,
                                               out var diagnosticRecipeGeneration)
                                               ? diagnosticRecipeGeneration
                                               : 0);
        if (diagnosticResourceGeneration > 0)
        {
            var virtualReadiness = PcCompatVirtualBundleRegistry.GetSessionReadiness(
                Id,
                diagnosticResourceGeneration);
            builder.AppendLine(
                $"virtualBundleReadiness=present={virtualReadiness.SessionPresent} " +
                $"ready={virtualReadiness.IsReady} " +
                $"required={virtualReadiness.RequiredReadyCount}/" +
                $"{virtualReadiness.RequiredAssetCount} " +
                $"pending={virtualReadiness.RequiredPendingCount} " +
                $"unsupported={virtualReadiness.RequiredUnsupportedCount} " +
                $"failed={virtualReadiness.RequiredFailedCount} " +
                $"optional={virtualReadiness.OptionalReadyCount}/" +
                $"{virtualReadiness.OptionalAssetCount} " +
                $"error={FirstStatusLine(virtualReadiness.LastError)}");
        }
        if (managedSession != null || managedBundle != null)
        {
            // Stage-4 unified lease audit: one line answers "does any backend still hold
            // objects for this session", instead of cross-referencing four export sections.
            var leaseGeneration = managedSession?.ResourceSessionGeneration
                ?? (PcCompatResourceRecipeRuntime.TryGetSessionGeneration(Id, out var recipeGeneration)
                    ? recipeGeneration
                    : 0);
            var leaseAudit = PcCompatUnityObjectLeaseAudit.Snapshot(Id, leaseGeneration);
            builder.AppendLine(
                $"unityLease=hostObjects={leaseAudit.OwnedHostGameObjects} " +
                $"virtualBundle={leaseAudit.VirtualBundleSessionPresent} " +
                $"resourceChanger={leaseAudit.ResourceChangerContributionPresent} " +
                $"hudSurfaces={leaseAudit.HudSurfaces}");
        }
        builder.AppendLine($"managedSessionLoaded={managedSession != null}");
        builder.AppendLine($"managedUsesRewrittenAssembly={managedSession?.UsesRewrittenAssembly ?? false}");
        builder.AppendLine($"managedActivationPending={managedSession?.ActivationPending ?? false}");
        builder.AppendLine($"managedActivationFailed={managedSession?.ActivationFailed ?? false}");
        builder.AppendLine($"managedPresentationClaimed={managedSession?.ManagedPresentationClaimed ?? false}");
        builder.AppendLine($"managedLifecycleState={managedLifecycle?.State.ToString() ?? "none"}");
        builder.AppendLine($"managedLifecycleUpdateCount={managedLifecycle?.UpdateCount ?? 0}");
        builder.AppendLine($"managedLifecycleFaultCount={managedLifecycle?.FaultCount ?? 0}");
        builder.AppendLine($"managedLifecycleTotalUpdateMs={managedLifecycle?.TotalUpdateMilliseconds ?? 0:F3}");
        builder.AppendLine($"managedLifecycleMaxUpdateMs={managedLifecycle?.MaximumUpdateMilliseconds ?? 0:F3}");
        builder.AppendLine($"managedRequiresFrameDispatch={managedSession?.RequiresFrameDispatch ?? false}");
        builder.AppendLine($"managedRequiresContinuousFrameDispatch={managedSession?.RequiresContinuousFrameDispatch ?? false}");
        builder.AppendLine($"managedSettingsState={managedSettings?.State.ToString() ?? "none"}");
        builder.AppendLine($"managedSettingsSupported={managedSettings?.Supported ?? false}");
        builder.AppendLine($"managedSettingsFailureReport={managedSession?.SettingsFailureReportPath ?? "none"}");
        builder.AppendLine($"managedSettingsFailureReportExists={managedSession != null && File.Exists(managedSession.SettingsFailureReportPath)}");
        builder.AppendLine("managedSettingsErrorBegin");
        builder.AppendLine(managedSettings?.Fault ?? "none");
        builder.AppendLine("managedSettingsErrorEnd");
        builder.AppendLine($"managedSettingsSurfaceKind={managedSettings?.SurfaceKind.ToString() ?? "none"}");
        builder.AppendLine($"managedSettingsPresentation={managedSettings?.PresentationDiagnostics ?? "none"}");
        builder.AppendLine($"managedSettingsSchemaAvailable={managedSettingsSchema?.Available ?? false}");
        builder.AppendLine($"managedSettingsSchemaRevision={managedSettingsSchema?.Revision ?? "none"}");
        builder.AppendLine($"managedSettingsSchemaEntries={managedSettingsSchema?.Entries.Count ?? 0}");
        builder.AppendLine($"managedSettingsSchemaPending={managedSettingsSchema?.HasPendingWrite ?? false}");
        builder.AppendLine($"managedSettingsSchemaUnsaved={managedSettingsSchema?.HasUnsavedChanges ?? false}");
        builder.AppendLine($"managedSettingsSchemaError={FirstStatusLine(managedSettingsSchema?.Error)}");
        builder.AppendLine($"managedSettingsSchemaApplyError={FirstStatusLine(managedSettingsSchema?.ApplyError)}");
        builder.AppendLine($"managedSettingsSchemaSaveError={FirstStatusLine(managedSettingsSchema?.SaveError)}");
        if (managedSettingsSchema != null)
        {
            foreach (var entry in managedSettingsSchema.Entries)
            {
                builder.AppendLine(
                    $"managedSettingsSchemaEntry={entry.Path}|group={entry.Group}|" +
                    $"kind={entry.Kind}|editable={entry.Editable}|" +
                    $"callback={entry.CallbackStatus}|reason={FirstStatusLine(entry.Reason)}");
            }
        }
        builder.AppendLine($"jalibMainThread={managedSession?.JALibMainThreadStatus ?? "none"}");
        builder.AppendLine($"legacyInputQueries={PcCompatLegacyInputBridge.GetDiagnosticStatus(Id)}");
        builder.AppendLine($"consumerQuerySurface={PcCompatKeyViewerConsumerRuntime.GetQuerySurfaceStatus(Id)}");
        builder.AppendLine(
            $"managedComponentLifecycle=frame={managedComponentLifecycle?.FrameGeneration ?? 0}" +
            $" components={managedComponentLifecycle?.Components.Count ?? 0}");
        if (managedComponentLifecycle != null)
        {
            foreach (var component in managedComponentLifecycle.Components)
            {
                builder.AppendLine(
                    $"managedComponent type={component.TypeName}" +
                    $" active={component.Active}" +
                    $" started={component.Started}" +
                    $" destroying={component.Destroying}" +
                    $" awake={component.AwakeCount}" +
                    $" enable={component.OnEnableCount}" +
                    $" start={component.StartCount}" +
                    $" update={component.UpdateCount}" +
                    $" lateUpdate={component.LateUpdateCount}" +
                    $" disable={component.OnDisableCount}" +
                    $" destroy={component.OnDestroyCount}" +
                    $" onGui={component.OnGuiCount}");
            }
        }
        builder.AppendLine("jalibLifecycleStatusBegin");
        builder.AppendLine(managedSession?.JALibLifecycleStatus ?? "none");
        builder.AppendLine("jalibLifecycleStatusEnd");
        builder.AppendLine("harmonyShimStatusBegin");
        builder.AppendLine(managedSession?.HarmonyShimStatus ?? "none");
        builder.AppendLine("harmonyShimStatusEnd");
        builder.AppendLine($"platformRuntime={PcCompatDiagnosticsRuntime.GetPlatformRuntimeStats()}");
        builder.AppendLine($"managedFailureReport={managedSession?.ManagedFailureReportPath ?? "none"}");
        builder.AppendLine($"managedFailureReportExists={managedSession != null && File.Exists(managedSession.ManagedFailureReportPath)}");
        builder.AppendLine("managedActivationStatusBegin");
        builder.AppendLine(managedSession?.ActivationStatus ?? "none");
        builder.AppendLine("managedActivationStatusEnd");
        builder.AppendLine("managedLifecycleErrorBegin");
        builder.AppendLine(managedLifecycle?.LastError ?? "none");
        builder.AppendLine("managedLifecycleErrorEnd");
        builder.AppendLine("loadCompletionErrorBegin");
        builder.AppendLine(loadCompletionError ?? "none");
        builder.AppendLine("loadCompletionErrorEnd");
        builder.AppendLine();

        RefreshKeyViewerAdapter();
        RefreshKeyViewerOverrides();
        builder.AppendLine("[keyviewer-adapter]");
        builder.AppendLine($"path={_keyViewerAdapterPath ?? "none"}");
        builder.AppendLine($"error={_keyViewerAdapterError ?? "none"}");
        builder.AppendLine($"inputOrigin={PcCompatInputOriginRuntime.GetCurrent()}");
        builder.AppendLine($"overridePath={_keyViewerOverridesPath ?? "none"}");
        builder.AppendLine($"overrideError={_keyViewerOverridesError ?? "none"}");
        builder.AppendLine($"overrideDirty={_keyViewerOverridesDirty}");
        builder.AppendLine($"loweringStatus={_keyViewerLoweringStatus ?? "none"}");
        builder.AppendLine(
            $"labelProjectionError={PcCompatKeyViewerLabelProjectionRuntime.GetLastError(Id) ?? "none"}");
        builder.AppendLine($"features={_keyViewerAdapter?.Features.Count ?? 0}");
        if (_keyViewerAdapter != null)
        {
            foreach (var feature in _keyViewerAdapter.Features)
            {
                builder.AppendLine($"feature={feature.Id}|backend={feature.Backend}|coreReady={PcCompatKeyViewerAdapterValidator.IsCoreReady(feature)}");
                foreach (var (name, evidence) in EnumerateKeyViewerEvidence(feature))
                    builder.AppendLine($"capability={name}|status={evidence.Status}|firstBreak={FirstStatusLine(evidence.FirstBreak)}");
                var featureOverride = _keyViewerOverrides?.Features.FirstOrDefault(value =>
                    string.Equals(value.FeatureId, feature.Id, StringComparison.Ordinal));
                if (featureOverride != null)
                {
                    builder.AppendLine(
                        $"override={feature.Id}|enabled={featureOverride.Enabled}|mode={featureOverride.InputMode}|" +
                        $"touchLanes={featureOverride.TouchLaneCount}|fallback={featureOverride.CompatibleFallbackEnabled}|" +
                        $"confirmedRoles={featureOverride.Roles.Count}");
                    foreach (var role in featureOverride.Roles)
                        builder.AppendLine($"overrideRole={feature.Id}|role={role.Role}|candidate={role.CandidateKey}");
                }
            }
        }
        var keyViewerPreview = PcCompatKeyViewerPreviewRuntime.Snapshot(Id);
        var keyViewerConsumer = PcCompatKeyViewerConsumerRuntime.Snapshot(Id);
        var keyViewerFallback = PcCompatKeyViewerFallbackRuntime.Snapshot();
        var keyViewerProviderTail = PcCompatKeyViewerEventRuntime.OpenAtTail();
        builder.AppendLine(
            $"providerTailAvailable={keyViewerProviderTail.ProviderAvailable}|" +
            $"cursor={keyViewerProviderTail.Cursor}|" +
            $"dropped={keyViewerProviderTail.DroppedBeforeCursor}|" +
            $"events={keyViewerProviderTail.Events.Count}");
        builder.AppendLine(
            $"fallbackRendererRegistered={keyViewerFallback.RendererRegistered}|" +
            $"registrations={keyViewerFallback.RegistrationCount}|" +
            $"dispatches={keyViewerFallback.DispatchCount}|" +
            $"lastFrames={keyViewerFallback.LastFrameCount}|" +
            $"error={FirstStatusLine(keyViewerFallback.RendererError)}");
        builder.AppendLine("fallbackRendererErrorBegin");
        builder.AppendLine(keyViewerFallback.RendererError ?? "none");
        builder.AppendLine("fallbackRendererErrorEnd");
        builder.AppendLine(
            $"consumerRegistered={keyViewerConsumer.Registered}|" +
            $"publishedSequence={keyViewerConsumer.PublishedSequence}|" +
            $"features={keyViewerConsumer.Features.Count}");
        foreach (var feature in keyViewerConsumer.Features)
        {
            builder.AppendLine(
                $"consumerFeature={feature.FeatureId}|active={feature.Active}|" +
                $"qualification={feature.Qualification}|identities={feature.MappedIdentityCount}|" +
                $"sequence={feature.PublishedSequence}|reason={FirstStatusLine(feature.Reason)}");
        }
        builder.AppendLine(
            $"previewRegistered={keyViewerPreview.Registered}|" +
            $"initialized={keyViewerPreview.CursorInitialized}|" +
            $"faulted={keyViewerPreview.Faulted}|startCursor={keyViewerPreview.StartCursor}|" +
            $"cursor={keyViewerPreview.Cursor}|" +
            $"events={keyViewerPreview.EventCount}|dropped={keyViewerPreview.DroppedEventCount}|" +
            $"session={keyViewerPreview.SessionGeneration}|producerEpoch={keyViewerPreview.ProducerEpoch}|" +
            $"origin={keyViewerPreview.Origin}");
        builder.AppendLine(
            $"previewActorRegistered={keyViewerPreview.Actor.Registered}|" +
            $"faulted={keyViewerPreview.Actor.Faulted}|pending={keyViewerPreview.Actor.PendingWork}|" +
            $"capacity={keyViewerPreview.Actor.MailboxCapacity}|" +
            $"highWatermark={keyViewerPreview.Actor.MailboxHighWatermark}|" +
            $"accepted={keyViewerPreview.Actor.AcceptedWork}|completed={keyViewerPreview.Actor.CompletedWork}|" +
            $"rejected={keyViewerPreview.Actor.RejectedWork}|" +
            $"yieldedTurns={keyViewerPreview.Actor.YieldedTurns}");
        builder.AppendLine($"previewError={keyViewerPreview.Fault ?? _keyViewerPreviewError ?? "none"}");
        builder.AppendLine(
            $"previewRawTouch=down={keyViewerPreview.TouchDownEventCount}|" +
            $"up={keyViewerPreview.TouchUpEventCount}|" +
            $"cancel={keyViewerPreview.TouchCancelEventCount}|" +
            $"tail={keyViewerPreview.RecentTouchEvents.Count}|" +
            $"cancelContext={keyViewerPreview.LastTouchCancelContext.Count}");
        foreach (var inputEvent in keyViewerPreview.RecentTouchEvents)
        {
            builder.AppendLine(
                $"previewRawTouchEvent=sequence={inputEvent.Sequence}|rawNs={inputEvent.RawNs}|" +
                $"origin={inputEvent.Origin}|phase={inputEvent.Phase}|" +
                $"pointerId={inputEvent.Code}|slot={inputEvent.Slot}|" +
                $"pointerCount={inputEvent.PointerCount}|androidFlags=0x{inputEvent.AndroidFlags:X}|" +
                $"edgeMask=0x{inputEvent.Flags:X}|sourceCode=0x{inputEvent.SourceCode:X}|" +
                $"x={inputEvent.X:R}|y={inputEvent.Y:R}|" +
                $"viewport={inputEvent.ViewportWidth}x{inputEvent.ViewportHeight}");
        }
        foreach (var inputEvent in keyViewerPreview.LastTouchCancelContext)
        {
            builder.AppendLine(
                $"previewRawTouchCancelEvent=sequence={inputEvent.Sequence}|rawNs={inputEvent.RawNs}|" +
                $"origin={inputEvent.Origin}|phase={inputEvent.Phase}|" +
                $"pointerId={inputEvent.Code}|slot={inputEvent.Slot}|" +
                $"pointerCount={inputEvent.PointerCount}|androidFlags=0x{inputEvent.AndroidFlags:X}|" +
                $"edgeMask=0x{inputEvent.Flags:X}|sourceCode=0x{inputEvent.SourceCode:X}|" +
                $"x={inputEvent.X:R}|y={inputEvent.Y:R}|" +
                $"viewport={inputEvent.ViewportWidth}x{inputEvent.ViewportHeight}");
        }
        foreach (var feature in keyViewerPreview.Features)
        {
            builder.AppendLine(
                $"previewFeature={feature.FeatureId}|requested={feature.RequestedInputMode}|" +
                $"mode={feature.InputMode}|sessionFrozen={feature.SessionModeFrozen}|" +
                $"frozenSession={feature.FrozenSessionGeneration}|" +
                $"sessionDeviceFlags={feature.SessionDeviceFlags}|" +
                $"sessionModeReason={FirstStatusLine(feature.SessionModeReason)}|" +
                $"lanes={feature.LaneCount}|touchMapping={feature.TouchLaneMappingMode}|" +
                $"touchReuseDelayMs={feature.TouchContactReuseDelayMilliseconds}|" +
                $"held=0x{feature.HeldMask:X}|" +
                $"transitions={feature.TransitionCount}|unmapped={feature.UnmappedEventCount}|" +
                $"consumerActive={feature.ConsumerActive}|" +
                $"consumerQualification={feature.ConsumerQualification}|" +
                $"consumerIdentities={feature.ConsumerMappedIdentityCount}|" +
                $"consumerReason={FirstStatusLine(feature.ConsumerReason)}");
            if (feature.LastTransition is { } transition)
            {
                builder.AppendLine(
                    $"previewTransition={feature.FeatureId}|sequence={transition.Sequence}|" +
                    $"rawNs={transition.RawNs}|source={transition.Source}|phase={transition.Phase}|" +
                    $"sourceCode={transition.SourceCode}|lane={transition.Lane}|" +
                    $"identity={transition.LaneIdentity}");
            }
        }
        builder.AppendLine();

        var resourcePlan = PcCompatResourceRecipeRuntime.GetPlan(Id);
        var resourceDocument = PcCompatResourceRecipeRuntime.Get(Id);
        var resourceReadySummary = PcCompatResourceRecipeRuntime.GetReadinessSummary(Id);
        var recipeBundle = PcCompatRuntime.GetRecipeBundle(Id);
        var resourceCompile = PcCompatRuntime.GetResourceCompileInfo(Id);
        var localResourceRecipe = Path.Combine(_manifest.FolderPath, ".pccompat", "resource_recipe.bin");
        builder.AppendLine("[resources]");
        builder.AppendLine($"resourceCompileProviderRegistered={PcCompatResourceAssemblyCompile.IsProviderRegistered}");
        builder.AppendLine($"resourceCompileCacheHit={resourceCompile?.CacheHit ?? false}");
        builder.AppendLine($"resourceCompileError={FirstStatusLine(PcCompatRuntime.GetResourceCompileError(Id))}");
        builder.AppendLine($"resourceRecipeLoaded={resourceDocument != null}");
        builder.AppendLine($"resourceRecipePath={localResourceRecipe}");
        builder.AppendLine($"resourceRecipeFileExists={File.Exists(localResourceRecipe)}");
        builder.AppendLine($"resourceCompatibility={resourceDocument?.Compatibility ?? "none"}");
        builder.AppendLine($"resourceGroups={resourcePlan?.FeatureGroups.Count ?? 0}");
        builder.AppendLine($"resourceCandidates={resourcePlan?.Candidates.Count ?? 0}");
        builder.AppendLine($"resourceReadyCandidates={resourceReadySummary.ReadyCandidateCount}");
        builder.AppendLine($"resourceControlledCandidates={resourceReadySummary.ControlledCandidateCount}");
        builder.AppendLine($"resourceQueuedCandidates={resourceReadySummary.QueuedCandidateCount}");
        builder.AppendLine($"resourceLoadedCandidates={resourceReadySummary.LoadedCandidateCount}");
        builder.AppendLine($"resourceRejectedCandidates={resourceReadySummary.RejectedCandidateCount}");
        builder.AppendLine($"resourceMissingCandidates={resourceReadySummary.MissingCandidateCount}");
        builder.AppendLine($"resourceCompiledDir={resourcePlan?.CompiledResourcesDirectory ?? "none"}");
        builder.AppendLine($"compiledBundleDir={recipeBundle?.BundleDirectory ?? "none"}");
        builder.AppendLine($"compiledResourceRecipe={recipeBundle?.ResourceRecipePath ?? "none"}");
        builder.AppendLine($"compiledResourcesDir={recipeBundle?.ResourceDirectory ?? "none"}");
        builder.AppendLine($"resourceRuntimeLoadEnabled={resourceReadySummary.RuntimeLoadEnabled}");
        builder.AppendLine($"resourceLoadSinkRegistered={resourceReadySummary.LoadSinkRegistered}");
        builder.AppendLine("resourceAutoLoad=false");
        builder.AppendLine(
            $"resourceFeatureGroups={string.Join(',', resourceReadySummary.FeatureGroupIds)}");
        if (resourcePlan == null && !File.Exists(localResourceRecipe))
        {
            builder.AppendLine(
                "resourceNote=resource_recipe.bin missing; run ResourceRecipeTool compile or PcCompatProbe --recipe-only");
        }
        else if (resourcePlan == null)
        {
            builder.AppendLine(
                "resourceNote=resource_recipe.bin exists but session plan is not loaded");
        }
        if (resourcePlan != null)
        {
            foreach (var group in resourcePlan.FeatureGroups)
            {
                builder.AppendLine(
                    $"resourceGroup id={group.Id} assets={group.AssetNames.Count} policy={group.LoadPolicy} sha={group.SelectedCandidateSha256Hex}");
            }
            foreach (var candidate in resourcePlan.Candidates)
            {
                builder.AppendLine(
                    $"resourceCandidate status={candidate.Status} auto={candidate.AutoLoadAllowed} " +
                    $"platform={candidate.PlatformHint} policy={candidate.LoadPolicy} file={candidate.FileName} " +
                    $"path={candidate.ResolvedPath}");
            }
        }
        builder.AppendLine();

        builder.AppendLine("[native-global]");
        builder.AppendLine($"providerAvailable={snapshot.ProviderAvailable}");
        builder.AppendLine($"bundles={snapshot.LoadedBundles} targets={snapshot.LoadedTargets} rules={snapshot.LoadedRules}");
        builder.AppendLine(
            $"uiObjects={snapshot.LoadedUiObjectNodes} uiComponentOps={snapshot.LoadedUiComponentOps} " +
            $"uiResources={snapshot.LoadedUiResourceBindings} " +
            $"uiLifecycle={snapshot.LoadedUiLifecyclePrograms} vmInstructions={snapshot.LoadedUiBytecodeInstructions}");
        builder.AppendLine(
            $"presentation=available:{snapshot.Presentation.ProviderAvailable} " +
            $"generation:{snapshot.Presentation.PublicationGeneration} " +
            $"commands:{snapshot.Presentation.Commands.Count} " +
            $"stale:{snapshot.Presentation.DroppedStaleTasks} " +
            $"overflow:{snapshot.Presentation.SchedulerOverflowCount}");
        builder.AppendLine(
            $"presentationSink=available:{snapshot.PresentationSink.ProviderAvailable} " +
            $"installed:{snapshot.PresentationSink.Installed} " +
            $"primary:{snapshot.PresentationSink.PrimaryHook} " +
            $"fallback:{snapshot.PresentationSink.FallbackHook} " +
            $"opportunities:{snapshot.PresentationSink.ConsumeOpportunities} " +
            $"updates:{snapshot.PresentationSink.SnapshotUpdates} " +
            $"commands:{snapshot.PresentationSink.CommandCount} " +
            $"unsupported:{snapshot.PresentationSink.UnsupportedCommandCount} " +
            $"graphs:{snapshot.PresentationSink.MaterializedGraphCount}/" +
            $"{snapshot.PresentationSink.RegisteredGraphCount} " +
            $"historyOverflow:{snapshot.PresentationSink.PresentationHistoryOverflowCount} " +
            $"streamGaps:{snapshot.PresentationSink.StreamGapCount} " +
            $"streamFaulted:{snapshot.PresentationSink.StreamFaulted} " +
            $"graphFailures:{snapshot.PresentationSink.GraphMaterializationFailures} " +
            $"invalidTargets:{snapshot.PresentationSink.InvalidTargetCount} " +
            $"onGuiHook:{snapshot.PresentationSink.OnGUIHook} " +
            $"onGuiProcessHook:{snapshot.PresentationSink.OnGUIProcessHook} " +
            $"onGuiBeginHook:{snapshot.PresentationSink.OnGUIBeginHook} " +
            $"onGuiEnabled:{snapshot.PresentationSink.OnGUIEnabled} " +
            $"onGuiProcess:{snapshot.PresentationSink.OnGUIProcessEventCount} " +
            $"onGuiBegin:{snapshot.PresentationSink.OnGUIBeginGUICount} " +
            $"onGuiDispatch:{snapshot.PresentationSink.OnGUIDispatchCount}");
        builder.AppendLine($"slots={snapshot.MergedSlots} pending={snapshot.PendingSlots} resolved={snapshot.ResolvedSlots} failed={snapshot.FailedSlots}");
        builder.AppendLine($"installable={snapshot.InstallableSlots} blocked={snapshot.InstallBlockedSlots} installed={snapshot.InstalledSlots}");
        builder.AppendLine($"dispatcherRequired={snapshot.DispatcherRequiredSlots} capacity={snapshot.DispatcherCapacity} bound={snapshot.BoundDispatcherSlots} ready={snapshot.DispatcherReadySlots} new={snapshot.DispatcherNewSlots} allocated={snapshot.DispatcherAllocatedSlots} remaining={snapshot.DispatcherRemainingSlots} blocked={snapshot.DispatcherBlockedSlots}");
        builder.AppendLine($"slotRules={snapshot.SlotRules} enabled={snapshot.EnabledSlotRules} disabled={snapshot.DisabledSlotRules}");
        builder.AppendLine($"approvedCapabilities=0x{snapshot.ApprovedCapabilities:X}");
        builder.AppendLine($"lastError={snapshot.LastError}");
        if (snapshot.LatestVmFault.ProviderAvailable)
        {
            var fault = snapshot.LatestVmFault;
            builder.AppendLine(
                $"vmFault=seq:{fault.Sequence} rule:{fault.RuleId} code:{fault.Code} " +
                $"count:{fault.Count} pc:{fault.Pc} opcode:{fault.Opcode} " +
                $"dropped:{fault.DroppedBeforeCursor} message:{fault.Message}");
        }
        builder.AppendLine();

        var nativeEventStats = PcCompatDiagnosticsRuntime.GetManagedEventStats(Id);
        var dispatchStats = managedSession?.ManagedEventDispatchStats;
        builder.AppendLine("[managed-events]");
        builder.AppendLine(
            $"native=available:{nativeEventStats.ProviderAvailable} " +
            $"rings:{nativeEventStats.Rings} enabled:{nativeEventStats.EnabledRings} " +
            $"pushed:{nativeEventStats.PushedTotal} queued:{nativeEventStats.QueuedCurrent} " +
            $"dropped:{nativeEventStats.DroppedTotal}");
        builder.AppendLine(
            dispatchStats == null
                ? "dispatch=built:false"
                 : $"dispatch=built:true parsed:{dispatchStats.ParsedRules} " +
                   $"bound:{dispatchStats.BoundCallbacks} fused:{dispatchStats.DisabledCallbacks} " +
                   $"drainCalls:{dispatchStats.DrainCalls} drained:{dispatchStats.DrainedEvents} " +
                   $"nativeDropped:{dispatchStats.NativeDroppedEvents} " +
                   $"budgetExhaustedFrames:{dispatchStats.DrainBudgetExhaustedFrames} " +
                   $"hitSnapshots:{dispatchStats.HitMarginSnapshots} " +
                   $"invalidHitSnapshots:{dispatchStats.InvalidHitMarginSnapshots} " +
                   $"lastHitSnapshotGeneration:{dispatchStats.LastHitMarginSnapshotGeneration} " +
                   $"lastNonZeroHitSnapshotGeneration:{dispatchStats.LastNonZeroHitMarginSnapshotGeneration} " +
                   $"lastNonZeroHitCounts:{dispatchStats.LastNonZeroHitMarginCounts} " +
                   $"dispatched:{dispatchStats.DispatchedCallbacks} failed:{dispatchStats.FailedCallbacks}");
        if (dispatchStats != null)
        {
            if (!string.IsNullOrWhiteSpace(dispatchStats.SkipReasons))
                builder.AppendLine($"skipReasons={dispatchStats.SkipReasons}");
            builder.AppendLine($"dispatchLastError={dispatchStats.LastError}");
            if (!string.IsNullOrWhiteSpace(dispatchStats.CallbackStats))
                builder.AppendLine($"callbackStats={dispatchStats.CallbackStats}");
        }
        builder.AppendLine();

        builder.AppendLine("[mod-slots]");
        builder.AppendLine(modSlots);
        builder.AppendLine();
        builder.AppendLine("[global-slots]");
        builder.AppendLine(snapshot.SlotSummary);

        if (translation != null)
        {
            builder.AppendLine();
            builder.AppendLine("[callback-issues]");
            foreach (var item in translation.Items.Where(item =>
                         item.Status is PcCompatCallbackTranslationStatus.Unsupported or PcCompatCallbackTranslationStatus.NotMapped))
            {
                builder.AppendLine($"{item.Status}|{item.TargetType}.{item.TargetMethod}|{item.CallbackType}.{item.CallbackMethod}|{item.Reason}");
            }
        }

        return builder.ToString();
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static string FirstStatusLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "none";
        var lineBreak = value.IndexOfAny(['\r', '\n']);
        return lineBreak < 0 ? value.Trim() : value[..lineBreak].Trim();
    }

    private static string LocalizeLoadStage(string stage) => stage switch
    {
        "Queued" => L10n.Get("PcCompat_LoadStage_Queued"),
        "Scanning PATCH metadata" => L10n.Get("PcCompat_LoadStage_Scanning"),
        "Translating callbacks" => L10n.Get("PcCompat_LoadStage_Translating"),
        "Rewriting managed assembly" => L10n.Get("PcCompat_LoadStage_Compiling"),
        "Compiling native rules" => L10n.Get("PcCompat_LoadStage_Compiling"),
        "Waiting for main-thread install" => L10n.Get("PcCompat_LoadStage_Finalizing"),
        "Installing managed runtime" => L10n.Get("PcCompat_LoadStage_Installing"),
        "Ready" => L10n.Get("PcCompat_LoadStage_Ready"),
        "Cancelled" => L10n.Get("PcCompat_LoadStage_Cancelled"),
        "Failed" => L10n.Get("PcCompat_LoadStage_Failed"),
        _ => stage
    };

    private static Vector4 GetExportStateColor(string state) => state switch
    {
        "Exported" => SuccessColor,
        "Error" => ErrorColor,
        "Cancelled" => WarningColor,
        _ => NormalColor
    };

    private void ExecuteDiagnostics(
        PcCompatDiagnosticsCommand command,
        ref PcCompatDiagnosticsSnapshot snapshot)
    {
        _lastDiagnosticsOperation = PcCompatDiagnosticsRuntime.Execute(command);
        snapshot = _lastDiagnosticsOperation.Snapshot;
    }

    private static void RenderMetricRow(
        string firstLabel,
        string firstValue,
        string secondLabel,
        string secondValue,
        Vector4? firstValueColor = null,
        Vector4? secondValueColor = null)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextDisabled(firstLabel);
        ImGui.TableSetColumnIndex(1);
        RenderMetricValue(firstValue, firstValueColor);
        ImGui.TableSetColumnIndex(2);
        ImGui.TextDisabled(secondLabel);
        ImGui.TableSetColumnIndex(3);
        RenderMetricValue(secondValue, secondValueColor);
    }

    private static void RenderMetricValue(string value, Vector4? color)
    {
        if (color.HasValue)
            ImGui.TextColored(color.Value, value);
        else
            ImGui.Text(value);
    }

    private static Vector4 DispatcherColor(PcCompatDiagnosticsSnapshot snapshot)
    {
        if (snapshot.DispatcherBlockedSlots > 0 || snapshot.DispatcherCapacity < snapshot.DispatcherRequiredSlots)
            return ErrorColor;
        if (snapshot.DispatcherReadySlots < snapshot.DispatcherRequiredSlots)
            return WarningColor;
        return SuccessColor;
    }

    private static Vector4 GetSlotLineColor(string line)
    {
        if (line.Contains("state=Faulted", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("state=InstallFailed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("ABI mismatch", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("ambiguous", StringComparison.OrdinalIgnoreCase))
            return ErrorColor;
        if (line.Contains("state=SkippedKnownConflict", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("state=PendingResolve", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("blocked=1", StringComparison.OrdinalIgnoreCase))
            return WarningColor;
        if (line.Contains("state=HookInstalled", StringComparison.OrdinalIgnoreCase))
            return SuccessColor;
        return NormalColor;
    }

    private string ClearPopupId => L10n.Get("PcCompat_ClearTitle") + $"##PcCompatClear_{Id}";
    private string ResourceLoadPopupId => L10n.Get("PcCompat_ResourceConfirmTitle") + $"##PcCompatResourceLoad_{Id}";
    private static float UiScale => Math.Max(1f, ImGui.GetIO().FontGlobalScale / 2f);
    private static readonly Vector4 NormalColor = new(0.82f, 0.82f, 0.82f, 1f);
    private static readonly Vector4 SuccessColor = new(0.25f, 0.85f, 0.35f, 1f);
    private static readonly Vector4 WarningColor = new(1f, 0.72f, 0.2f, 1f);
    private static readonly Vector4 ErrorColor = new(1f, 0.3f, 0.3f, 1f);

    public void OnForegroundGUI(ImDrawListPtr drawList)
    {
        if (!_supportsStandardUnityHud)
            return;

        if (PcCompatUnityHudRuntime.RendererAvailableFor(Id))
            return;

        if (!_mobileSettings.ShowHud)
            return;

        PcCompatOverlaySnapshot overlay;
        if (_hasPendingOverlaySnapshot)
        {
            overlay = _pendingOverlaySnapshot;
            _hasPendingOverlaySnapshot = false;
        }
        else
        {
            overlay = PcCompatOverlayRuntime.Snapshot(Id);
        }

        if (!overlay.ProviderAvailable || !overlay.Visible)
            return;

        EnsureHudModel(overlay);

        var viewport = ImGui.GetMainViewport();
        var scale = _mobileSettings.HudScale;
        var pos = viewport.Pos + new Vector2(_mobileSettings.PositionX, _mobileSettings.PositionY);
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize() * scale;
        var lineAdvance = fontSize + 4f * scale;
        EnsureHudLayout(scale, fontSize);
        var width = _hudContentWidth + 28f * scale;
        var height = (_hudLineCount + 1) * lineAdvance + 16f * scale;
        if (_hudProgressBarVisible)
            height += 18f * scale;
        var end = pos + new Vector2(width, height);

        var backgroundAlpha = (uint)Math.Clamp((int)MathF.Round(_mobileSettings.BackgroundOpacity * 255f), 0, 255);
        drawList.AddRectFilled(pos, end, backgroundAlpha << 24 | 0x00202020u, 6f * scale);
        drawList.AddRect(pos, end, 0x66FFFFFF, 6f * scale);

        var textPos = pos + new Vector2(14f * scale, 8f * scale);
        drawList.AddText(font, fontSize, textPos, 0xFFFFFFFF, _hudTitle);
        textPos.Y += lineAdvance;
        for (var index = 0; index < _hudLineCount; ++index)
        {
            var line = _hudLines[index];
            drawList.AddText(font, fontSize, textPos, line.Color, line.Text);
            textPos.Y += lineAdvance;
        }
        if (_hudProgressBarVisible)
        {
            var barMin = new Vector2(pos.X + 14f * scale, end.Y - 12f * scale);
            var barMax = new Vector2(end.X - 14f * scale, end.Y - 6f * scale);
            var fillMax = new Vector2(
                barMin.X + (barMax.X - barMin.X) * Math.Clamp(_hudProgressBarValue, 0f, 1f),
                barMax.Y);
            drawList.AddRectFilled(barMin, barMax, 0x66303030, 3f * scale);
            drawList.AddRectFilled(barMin, fillMax, 0xFFE0C4FF, 3f * scale);
        }
    }

    private void EnsureHudModel(PcCompatOverlaySnapshot overlay)
    {
        var input = _mobileSettings.ShowKeyViewer
            ? PcCompatInputHudRuntime.Snapshot(_mobileSettings.TouchKeyCount)
            : PcCompatInputHudSnapshot.Unavailable;
        var cultureLcid = CultureInfo.CurrentUICulture.LCID;
        var now = _mobileSettings.ShowTechnicalDiagnostics ? Environment.TickCount64 : 0;
        var refreshDiagnostics = _mobileSettings.ShowTechnicalDiagnostics &&
                                 now >= _nextHudDiagnosticsRefresh;
        if (_hudModelValid &&
            _hudSnapshotGeneration == overlay.Generation &&
            _hudInputSnapshotGeneration == input.PublicationGeneration &&
            _renderedHudSettingsGeneration == _hudSettingsGeneration &&
            _hudCultureLcid == cultureLcid &&
            !refreshDiagnostics)
            return;

        var game = _mobileSettings.ShowPlayerCount
            ? PcCompatReversePatchBridge.Snapshot()
            : PcCompatGameSnapshot.Empty;
        var hasAccuracy = TryGetAccuracy(overlay, game, out var percentAcc, out var percentXAcc);
        var progressAvailable = overlay.FloorMoveCount > 0 || overlay.ShowCount > 0;
        _playStatsSnapshot = _playStats.Update(overlay);

        _hudTitle = overlay.Practice ? _practiceHudTitle : Name;
        _hudLineCount = 0;
        _hudProgressBarVisible = _mobileSettings.ShowProgressBar &&
                                 progressAvailable &&
                                 float.IsFinite(overlay.Progress);
        _hudProgressBarValue = _hudProgressBarVisible
            ? Math.Clamp(overlay.Progress, 0f, 1f)
            : 0f;
        if (_mobileSettings.ShowProgress)
        {
            AddHudLine(
                $"{L10n.Get("PcCompat_Data_Progress")}  {FormatPercent(overlay.Progress, progressAvailable)}",
                0xFFE0C4FF);
        }
        if (_mobileSettings.ShowBpm && overlay.BpmSnapshotCount > 0)
        {
            AddHudLine(
                $"{L10n.Get("PcCompat_Data_Bpm")}  {overlay.TileBpm:F2}  KPS {overlay.Kps:F2}",
                0xFFFFD968);
        }
        if (_mobileSettings.ShowCombo)
        {
            AddHudLine(
                $"{L10n.Get("PcCompat_Data_Combo")}  {overlay.ComboCount}",
                overlay.ComboCount > 0 ? 0xFFFFB8FF : 0xFFD8D8D8);
        }
        if (_mobileSettings.ShowAttempt)
        {
            AddHudLine(
                $"{L10n.Get("PcCompat_Data_Attempt")}  {(_playStatsSnapshot.Available ? _playStatsSnapshot.Attempts : overlay.AttemptCount)}",
                0xFFD8D8D8);
        }
        if (_mobileSettings.ShowMusicTime)
        {
            var useMapFallback = overlay.MusicTotalTime <= 0f && _mobileSettings.ShowMapTimeIfMusicUnavailable;
            AddHudLine(
                $"{L10n.Get(useMapFallback ? "PcCompat_Data_MapTime" : "PcCompat_Data_MusicTime")}  " +
                FormatTimeRange(
                    useMapFallback ? overlay.MapTime : overlay.MusicTime,
                    useMapFallback ? overlay.MapTotalTime : overlay.MusicTotalTime),
                0xFFE8E8E8);
        }
        if (_mobileSettings.ShowMapTime)
        {
            AddHudLine(
                $"{L10n.Get("PcCompat_Data_MapTime")}  {FormatTimeRange(overlay.MapTime, overlay.MapTotalTime)}",
                0xFFD8E8FF);
        }
        if (_mobileSettings.ShowCheckpoint && overlay.TotalCheckpoints > 0)
        {
            AddHudLine(
                $"{L10n.Get("PcCompat_Data_Checkpoint")}  {FormatCheckpoint(overlay)}",
                0xFFFFD968);
        }
        if (_mobileSettings.ShowBest && _playStatsSnapshot.Available)
        {
            AddHudLine(
                $"{L10n.Get("PcCompat_Data_Best")}  {_playStatsSnapshot.DisplayBest * 100f:F2}%",
                0xFFE0C4FF);
        }
        if (_mobileSettings.ShowKeyViewer)
        {
            var laneCount = input.ProviderAvailable
                ? input.TouchLaneCount
                : _mobileSettings.TouchKeyCount;
            var heldMask = input.ProviderAvailable
                ? input.TouchLaneHeldMask
                : overlay.InputHeldMask;
            var inputKps = input.ProviderAvailable
                ? input.InputKps
                : overlay.InputKps;
            var inputTotal = input.ProviderAvailable
                ? input.InputTotalCount
                : overlay.InputTotalCount;
            AddHudLine(
                $"{L10n.Get("PcCompat_Data_Input")}  {FormatInputKeys(heldMask, laneCount)}  " +
                $"KPS {inputKps:F0}  Total {inputTotal}",
                heldMask != 0 ? 0xFFFFFFFF : 0xFFA8A8A8);
        }
        if (_mobileSettings.ShowAccuracy)
        {
            AddHudLine(
                $"{L10n.Get("PcCompat_Data_Accuracy")}  {FormatAccuracy(percentAcc, hasAccuracy)}",
                0xFFE8E8E8);
        }
        if (_mobileSettings.ShowXAccuracy)
        {
            AddHudLine(
                $"{L10n.Get("PcCompat_Data_XAccuracy")}  {FormatAccuracy(percentXAcc, hasAccuracy)}",
                0xFF68D9FF);
        }
        if (_mobileSettings.ShowLastJudgement && overlay.JudgementHitCount > 0)
        {
            AddHudLine(
                $"{L10n.Get("PcCompat_Data_LastJudgement")}  {overlay.LastHitMarginName}",
                HitMarginColor(overlay.LastHitMargin));
        }
        if (_mobileSettings.ShowHitTiming && overlay.HitTimingCount > 0)
        {
            AddHudLine(
                $"{L10n.Get("PcCompat_Data_HitTiming")}  {overlay.LastHitTimingMs:+0.00;-0.00;0.00} ms",
                HitMarginColor(overlay.LastHitTimingMargin));
        }
        if (_mobileSettings.ShowPlayerCount)
        {
            AddHudLine(
                $"{L10n.Get("PcCompat_Data_PlayerCount")}  {Math.Max(game.PlayerCount, overlay.PlayerCount)}",
                0xFFD8D8D8);
        }
        if (_mobileSettings.ShowTechnicalDiagnostics)
        {
            _hudDiagnostics = PcCompatDiagnosticsRuntime.Snapshot();
            _nextHudDiagnosticsRefresh = now + HudDiagnosticsRefreshMilliseconds;
            AddHudLine(
                _hudDiagnostics.ProviderAvailable
                    ? $"PcCompat {_hudDiagnostics.InstalledSlots} active, {_hudDiagnostics.BoundDispatcherSlots}/{_hudDiagnostics.DispatcherCapacity} bound"
                    : "PcCompat diagnostics unavailable",
                0xCCB8E8FF);
            AddHudLine(BuildOverlayContext(overlay), 0xAAA8A8A8);
        }
        else
        {
            _hudDiagnostics = PcCompatDiagnosticsSnapshot.Unavailable;
            _nextHudDiagnosticsRefresh = 0;
        }

        _hudSnapshotGeneration = overlay.Generation;
        _hudInputSnapshotGeneration = input.PublicationGeneration;
        _renderedHudSettingsGeneration = _hudSettingsGeneration;
        _hudCultureLcid = cultureLcid;
        _hudModelValid = true;
        _hudLayoutDirty = true;
        unchecked
        {
            _hudModelRevision++;
        }
    }

    public bool TryGetUnityHudFrame(out PcCompatUnityHudFrame frame)
    {
        if (!_supportsStandardUnityHud)
        {
            frame = null!;
            return false;
        }

        var overlay = PcCompatOverlayRuntime.Snapshot(Id);
        _playStatsSnapshot = _playStats.Update(overlay);
        // Managed self-render owns every MOD visual while claimed; report the
        // compatibility HUD as hidden so only the MOD's own objects remain.
        if (ManagedSelfRenderBlocksCompatibilityPresentation ||
            !_mobileSettings.ShowHud || !overlay.ProviderAvailable || !overlay.Visible)
        {
            if (_unityHudFrame is not { Visible: false } ||
                _unityHudFrame.OverlayGeneration != overlay.Generation ||
                _unityHudFrame.StyleGeneration != _hudSettingsGeneration)
            {
                _unityHudFrame = new PcCompatUnityHudFrame
                {
                    ModId = Id,
                    Visible = false,
                    OverlayGeneration = overlay.Generation,
                    StyleGeneration = _hudSettingsGeneration
                };
            }

            frame = _unityHudFrame;
            return true;
        }

        EnsureHudModel(overlay);
        if (_unityHudFrame is { Visible: true } &&
            _unityHudFrameRevision == _hudModelRevision)
        {
            frame = _unityHudFrame;
            return true;
        }

        var richText = BuildUnityHudText(out var plainText, out var measuredWidth);
        var lineCount = _hudLineCount + 1;
        var frameHeight = lineCount * 34f + 16f;
        if (_hudProgressBarVisible)
            frameHeight += 18f;
        _unityHudFrame = new PcCompatUnityHudFrame
        {
            ModId = Id,
            Visible = true,
            OverlayGeneration = overlay.Generation,
            StyleGeneration = HashCode.Combine(_hudSettingsGeneration, _hudCultureLcid),
            RichText = richText,
            PlainText = plainText,
            LineCount = lineCount,
            Width = Math.Clamp(measuredWidth + 28f, 300f, 960f),
            Height = frameHeight,
            Scale = _mobileSettings.HudScale,
            PositionX = _mobileSettings.PositionX,
            PositionY = _mobileSettings.PositionY,
            BackgroundOpacity = _mobileSettings.BackgroundOpacity,
            ProgressBarVisible = _hudProgressBarVisible,
            ProgressBarValue = _hudProgressBarValue
        };
        _unityHudFrameRevision = _hudModelRevision;
        frame = _unityHudFrame;
        return true;
    }

    private string BuildUnityHudText(out string plainText, out float measuredWidth)
    {
        var builder = new StringBuilder(256);
        var plainBuilder = new StringBuilder(192);
        AppendRichTextLine(builder, _hudTitle, 0xFFFFFFFF, bold: true);
        plainBuilder.Append(_hudTitle);
        measuredWidth = MeasureHudTextWidth(_hudTitle);
        for (var index = 0; index < _hudLineCount; ++index)
        {
            var line = _hudLines[index];
            builder.Append('\n');
            plainBuilder.Append('\n');
            AppendRichTextLine(builder, line.Text, line.Color, bold: false);
            plainBuilder.Append(line.Text);
            measuredWidth = MathF.Max(measuredWidth, MeasureHudTextWidth(line.Text));
        }

        plainText = plainBuilder.ToString();
        return builder.ToString();
    }

    private static void AppendRichTextLine(StringBuilder builder, string text, uint color, bool bold)
    {
        if (bold)
            builder.Append("<b>");
        builder.Append("<color=#");
        AppendHexByte(builder, (byte)color);
        AppendHexByte(builder, (byte)(color >> 8));
        AppendHexByte(builder, (byte)(color >> 16));
        AppendHexByte(builder, (byte)(color >> 24));
        builder.Append('>');
        AppendEscapedRichText(builder, text);
        builder.Append("</color>");
        if (bold)
            builder.Append("</b>");
    }

    private static void AppendEscapedRichText(StringBuilder builder, string text)
    {
        foreach (var character in text)
        {
            switch (character)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }
    }

    private static void AppendHexByte(StringBuilder builder, byte value)
    {
        const string hex = "0123456789ABCDEF";
        builder.Append(hex[value >> 4]);
        builder.Append(hex[value & 0x0F]);
    }

    private static float MeasureHudTextWidth(string text)
    {
        var units = 0f;
        foreach (var character in text)
            units += character >= 0x2E80 ? 1f : 0.56f;
        return units * 25f;
    }

    private static string FormatTimeRange(float current, float total)
    {
        if (!float.IsFinite(current) || !float.IsFinite(total) || total <= 0f)
            return "--:--";
        return $"{FormatTime(current)} / {FormatTime(total)}";
    }

    private static string FormatTime(float seconds)
    {
        var value = Math.Max(0, (int)seconds);
        return value >= 3600
            ? $"{value / 3600}:{value % 3600 / 60:00}:{value % 60:00}"
            : $"{value / 60}:{value % 60:00}";
    }

    private static string FormatCheckpoint(PcCompatOverlaySnapshot overlay)
        => $"{overlay.CheckpointsUsed} ({overlay.CurrentCheckpoint}/{overlay.TotalCheckpoints})";

    private static string FormatInputKeys(uint heldMask, int laneCount)
    {
        laneCount = Math.Clamp(laneCount, 1, 10);
        var builder = new StringBuilder(laneCount * 6);
        for (var index = 0; index < laneCount; ++index)
        {
            if (index != 0)
                builder.Append(' ');
            var held = (heldMask & (1u << index)) != 0;
            if (held)
                builder.Append('[');
            builder.Append('T').Append(index + 1);
            builder.Append(held ? ']' : '.');
        }
        return builder.ToString();
    }

    private void RefreshRuntimeAdapters()
    {
        _supportsStandardUnityHud = PcCompatRecipeCapabilities.SupportsStandardUnityHud(
            PcCompatRuntime.GetRecipeReport(Id));

        if (_supportsStandardUnityHud && !_unityHudRegistered)
        {
            var sessionGeneration = PcCompatRuntime.GetManagedSession(Id)?.ResourceSessionGeneration ?? 0;
            if (TryRegisterOwnedHudSource())
            {
                try
                {
                    PcCompatUnityHudRuntime.RegisterSource(Id, sessionGeneration, this);
                    _unityHudRegistered = true;
                }
                catch
                {
                    RetireOwnedHudSource();
                    throw;
                }
            }
            else
            {
                PcCompatUnityHudRuntime.MarkSourceRendererFailed(Id);
                Logger.Warn(
                    nameof(PcCompatModPlugin),
                    $"Unity HUD ownership registration rejected mod={Id} " +
                    $"generation={_runtimeKey.Generation}; ImGui fallback retained");
            }
        }
        else if (!_supportsStandardUnityHud && _unityHudRegistered)
        {
            PcCompatUnityHudRuntime.UnregisterSource(this);
            _unityHudRegistered = false;
            RetireOwnedHudSource();
        }

        RefreshKeyViewerAdapter();
        RefreshKeyViewerOverrides();
        RefreshKeyViewerPreviewRegistration();
    }

    private string OwnedHudIdentity
        => $"source=0x{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this):X};";

    private bool TryRegisterOwnedHudSource()
    {
        if (_runtimeSession == null || !_runtimeKey.IsValid)
            return true;
        return _runtimeSession.CanRegisterOwnedResource(_runtimeKey) &&
               ModOwnedResourceRegistry.TryRegister(
                   _runtimeKey,
                   ModOwnedResourceKind.Hud,
                   OwnedHudIdentity);
    }

    private void RetireOwnedHudSource()
    {
        if (!_runtimeKey.IsValid)
            return;
        ModOwnedResourceRegistry.RetireMatching(
            _runtimeKey,
            ModOwnedResourceKind.Hud,
            OwnedHudIdentity);
    }

    private void RefreshKeyViewerPreviewRegistration()
    {
        _keyViewerPreviewError = null;
        _keyViewerLoweringStatus = null;
        PcCompatKeyViewerLoweredConsumerPlanRegistry.Remove(Id);
        if (_keyViewerAdapter == null || _keyViewerOverrides == null)
        {
            RecordKeyViewerAdapterState(
                $"stage=registration outcome=blocked adapter={(_keyViewerAdapter == null ? "missing" : "ready")} " +
                $"adapterError={_keyViewerAdapterError ?? "none"} " +
                $"override={(_keyViewerOverrides == null ? "missing" : "ready")} " +
                $"overrideError={_keyViewerOverridesError ?? "none"}",
                warning: _keyViewerAdapterError != null || _keyViewerOverridesError != null);
            PcCompatKeyViewerLabelProjectionRuntime.Unregister(Id);
            PcCompatKeyViewerPreviewRuntime.Unregister(Id);
            PcCompatKeyViewerFallbackRuntime.Unregister(Id);
            _providerSequenceWatcher.Clear();
            return;
        }

        var lowering = PcCompatKeyViewerBindingPlanLowerer.Lower(
            _keyViewerAdapter,
            _keyViewerOverrides,
            ResolveManagedProviderSequence);
        // Baseline before registering: the lowerer already called every provider, and this is the
        // value the plans about to be registered were actually built from. A lowering that resolved
        // nothing leaves the previous baseline in place so the change that restores it is still
        // observable.
        _providerSequenceWatcher.SetBaseline(lowering.ResolvedProviders);
        var loweringIssues = new List<string>(lowering.Issues);
        var registeredPlanCount = 0;
        foreach (var plan in lowering.Plans)
        {
            if (!PcCompatKeyViewerLoweredConsumerPlanRegistry.Register(
                    _keyViewerAdapter,
                    _keyViewerOverrides,
                    plan,
                    out var planError))
            {
                loweringIssues.Add($"feature '{plan.FeatureId}': {planError}");
                continue;
            }
            registeredPlanCount++;
        }
        loweringIssues.AddRange(lowering.PresentationIssues);
        _keyViewerLoweringStatus = loweringIssues.Count == 0
            ? lowering.PresentationPlans.Count == 0
                ? null
                : $"lowered consumer plans={lowering.Plans.Count} " +
                  $"presentation plans={lowering.PresentationPlans.Count}"
            : string.Join("; ", loweringIssues.Take(3));

        var previewRegistered = PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
                Id,
                _keyViewerAdapter,
                _keyViewerOverrides,
                out var error);
        if (!previewRegistered)
        {
            _keyViewerPreviewError = error;
            PcCompatKeyViewerLabelProjectionRuntime.Unregister(Id);
        }
        else
            PcCompatKeyViewerLabelProjectionRuntime.RegisterOrUpdate(
                Id,
                _keyViewerAdapter,
                _keyViewerOverrides,
                lowering.PresentationPlans);
        if (ManagedSelfRenderBlocksCompatibilityPresentation)
            PcCompatKeyViewerFallbackRuntime.Unregister(Id);
        else
            PcCompatKeyViewerFallbackRuntime.RegisterOrUpdate(
                Id,
                _keyViewerAdapter,
                _keyViewerOverrides,
                lowering.PresentationPlans,
                PcCompatRuntime.GetManagedSession(Id)?.ResourceSessionGeneration ?? 0);
        var managedSession = PcCompatRuntime.GetManagedSession(Id);
        Logger.Info(
            nameof(PcCompatModPlugin),
            $"keyviewer adapter registration mod={Id} " +
            $"resourceGeneration={managedSession?.ResourceSessionGeneration ?? 0} " +
            $"activationReady={managedSession?.EnableCompleted == true} " +
            $"features={_keyViewerAdapter.Features.Count} loweringPlans={lowering.Plans.Count} " +
            $"registeredPlans={registeredPlanCount} loweringIssues={loweringIssues.Count} " +
            $"previewRegistered={previewRegistered} " +
            $"loweringStatus={FirstStatusLine(_keyViewerLoweringStatus)}");
    }

    private void RenderKeyViewerPreviewStatus()
    {
        var preview = PcCompatKeyViewerPreviewRuntime.Snapshot(Id);
        if (!string.IsNullOrWhiteSpace(_keyViewerPreviewError))
        {
            ImGui.TextColored(ErrorColor, _keyViewerPreviewError);
            return;
        }
        if (!preview.Registered)
        {
            ImGui.TextDisabled(L10n.Get("PcCompat_KeyViewerPreviewInactive"));
            return;
        }
        if (!string.IsNullOrWhiteSpace(_keyViewerLoweringStatus))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextDisabled(_keyViewerLoweringStatus);
            ImGui.PopTextWrapPos();
        }
        // Shown because a republication is otherwise invisible: the plan silently becomes a
        // different one, and this is the only place that says which provider moved and to what.
        if (!string.IsNullOrWhiteSpace(_keyViewerRepublicationStatus))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextDisabled(_keyViewerRepublicationStatus);
            ImGui.PopTextWrapPos();
        }
        var labelProjectionError = PcCompatKeyViewerLabelProjectionRuntime.GetLastError(Id);
        if (!string.IsNullOrWhiteSpace(labelProjectionError))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(ErrorColor, labelProjectionError);
            ImGui.PopTextWrapPos();
        }
        if (preview.Faulted)
        {
            ImGui.TextColored(
                ErrorColor,
                L10n.Get("PcCompat_KeyViewerPreviewFaulted", preview.Fault ?? "unknown"));
            return;
        }

        ImGui.TextDisabled(L10n.Get(
            "PcCompat_KeyViewerPreviewActive",
            preview.Cursor,
            preview.EventCount));
        foreach (var feature in preview.Features)
        {
            ImGui.TextDisabled(
                $"{feature.FeatureId}: " +
                FormatInputKeys(feature.HeldMask, feature.LaneCount) +
                $" transitions={feature.TransitionCount} unmapped={feature.UnmappedEventCount}");
            if (feature.ConsumerActive)
            {
                ImGui.TextColored(
                    SuccessColor,
                    L10n.Get(
                        "PcCompat_KeyViewerConsumerActive",
                        feature.ConsumerQualification,
                        feature.ConsumerMappedIdentityCount));
            }
            else
            {
                ImGui.TextDisabled(L10n.Get(
                    "PcCompat_KeyViewerConsumerInactive",
                    feature.ConsumerReason ?? "unavailable"));
            }
        }
    }

    private void AddHudLine(string text, uint color)
    {
        if (_hudLineCount >= _hudLines.Length)
            return;
        _hudLines[_hudLineCount++] = new HudLine(text, color);
    }

    private void EnsureHudLayout(float scale, float fontSize)
    {
        if (!_hudLayoutDirty &&
            _hudMeasuredScale == scale &&
            _hudMeasuredFontSize == fontSize)
            return;

        var width = ImGui.CalcTextSize(_hudTitle).X * scale;
        for (var index = 0; index < _hudLineCount; ++index)
            width = MathF.Max(width, ImGui.CalcTextSize(_hudLines[index].Text).X * scale);

        _hudContentWidth = width;
        _hudMeasuredScale = scale;
        _hudMeasuredFontSize = fontSize;
        _hudLayoutDirty = false;
    }

    private static uint HitMarginColor(int hitMargin) => hitMargin switch
    {
        3 => 0xFFFFFFFF,
        2 or 4 => 0xFFFFD968,
        1 or 5 => 0xFF60B8FF,
        _ => 0xFF6262FF
    };

    private static bool TryGetAccuracy(
        PcCompatOverlaySnapshot overlay,
        PcCompatGameSnapshot game,
        out float percentAcc,
        out float percentXAcc)
    {
        if (overlay.ProviderAvailable)
        {
            percentAcc = overlay.PercentAcc;
            percentXAcc = overlay.PercentXAcc;
            return overlay.AccuracyAvailable;
        }

        percentAcc = game.PercentAcc;
        percentXAcc = game.PercentXAcc;
        return float.IsFinite(percentAcc) &&
               float.IsFinite(percentXAcc) &&
               (percentAcc != 0f || percentXAcc != 0f);
    }

    private static string FormatAccuracy(float fraction, bool available)
        => available ? $"{fraction * 100f:F3}%" : "--";

    private static string FormatPercent(float fraction, bool available)
        => available && float.IsFinite(fraction) ? $"{Math.Clamp(fraction, 0f, 1f) * 100f:F2}%" : "--";

    private static string FormatBpm(PcCompatOverlaySnapshot overlay)
        => overlay.BpmSnapshotCount > 0 && float.IsFinite(overlay.TileBpm)
            ? $"{overlay.TileBpm:F2} / {overlay.Kps:F2} KPS"
            : "--";

    private static string BuildOverlayContext(PcCompatOverlaySnapshot overlay)
    {
        var parts = new List<string> { overlay.LastTargetName };
        if (overlay.PlayerCount > 0)
            parts.Add($"players={overlay.PlayerCount}");
        if (overlay.LastTargetKind == 1)
            parts.Add($"seq={overlay.LastSeqId} restart={(overlay.LastIsRestart ? 1 : 0)}");
        if (overlay.LastTargetKind is 4 or 6)
            parts.Add($"wipe={overlay.LastWipeDirection}");
        if (overlay.LastTargetKind == 5)
            parts.Add($"editorReset={(overlay.LastResetToEditor ? 1 : 0)}");
        if (overlay.JudgementHitCount > 0 || overlay.JudgementResetCount > 0)
            parts.Add($"hit={overlay.LastHitMarginName} hits={overlay.JudgementHitCount} resets={overlay.JudgementResetCount}");
        if (overlay.FloorMoveCount > 0)
            parts.Add($"floorMoves={overlay.FloorMoveCount} exit={overlay.LastFloorExitAngle:F3} moveHit={overlay.LastFloorMoveHitMarginName}");
        if (overlay.PlayerHitCount > 0)
            parts.Add($"playerHits={overlay.PlayerHitCount} auto={(overlay.LastPlayerHitIsAuto ? 1 : 0)}");
        if (overlay.DeathCount > 0)
            parts.Add($"deaths={overlay.DeathCount} overload={(overlay.LastDeathOverload ? 1 : 0)} multi={(overlay.LastDeathMultipress ? 1 : 0)} hitbox={(overlay.LastDeathHitbox ? 1 : 0)}");
        if (overlay.HitTimingCount > 0)
            parts.Add($"timing={overlay.LastHitTimingMs:F2}ms {overlay.LastHitTimingMarginName} n={overlay.HitTimingCount}");
        if (overlay.AccuracySnapshotCount > 0)
            parts.Add($"acc={overlay.PercentAcc:F6} xacc={overlay.PercentXAcc:F6} n={overlay.AccuracySnapshotCount}");
        if (overlay.FloorMoveCount > 0 || overlay.ShowCount > 0)
            parts.Add($"progress={overlay.Progress:F4}");
        if (overlay.ComboCount > 0)
            parts.Add($"combo={overlay.ComboCount}");
        if (overlay.AttemptCount > 0)
            parts.Add($"attempt={overlay.AttemptCount}");
        if (overlay.BpmSnapshotCount > 0)
            parts.Add($"bpm={overlay.TileBpm:F2} kps={overlay.Kps:F2}");

        return string.Join(" | ", parts);
    }

    public static ModEntry CreateEntry(PcModManifest manifest)
    {
        return new ModEntry
        {
            Id = manifest.Id,
            Name = manifest.DisplayName,
            Version = manifest.Version,
            Author = manifest.Author,
            Description = $"{manifest.Kind} PC MOD compatibility entry",
            Dependencies = manifest.Requirements.ToList(),
            FolderPath = manifest.FolderPath,
            EntryPoint = manifest.EntryAssemblyPath,
            PluginInstance = new PcCompatModPlugin(manifest),
            LoaderKind = PcCompatRuntime.LoaderKind,
            LoaderData = manifest
        };
    }
}

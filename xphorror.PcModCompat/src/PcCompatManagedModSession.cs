using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Collections;
using System.Text;
using StArray.ModManager.Manager;

namespace Xphorror.PcModCompat;

public sealed class PcCompatManagedModSession : IDisposable
{
    private const int ActivationPollMilliseconds = 250;
    private const int ActivationTimeoutMilliseconds = 60_000;
    private const int CallbackBuildRetrySeconds = 5;
    private const string LogTag = "PcModCompat";
    private readonly AssemblyLoadContext _loadContext;
    private readonly PcCompatManagedLifecycleController _lifecycle;
    private readonly PcCompatManagedExecutionState _enableContext;
    private readonly PcCompatManagedExecutionState _updateContext;
    private readonly PcCompatManagedExecutionState _disableContext;
    private readonly PcCompatManagedSettingsController? _settingsController;
    private readonly PcCompatResourceChangerStateAdapter? _resourceChangerStateAdapter;
    private readonly string? _settingsUnavailableReason;
    private readonly string[] _resourceFeatureGroups;
    private int _disposed;
    private int _activationRequested;
    private int _activationFailed;
    private int _managedPresentationClaimed;
    private int _managedFailureReportWritten;
    private long _nextActivationPollTimestamp;
    private long _activationDeadlineTimestamp;
    private string? _activationStatus;
    private string? _pendingContinuationFailure;
    private PcCompatManagedCallbackDispatcher? _callbackDispatcher;
    private byte[]? _managedEventBuffer;
    private long _nextCallbackBuildTimestamp;
    private PropertyInfo[]? _shimRegisteredPatchCountProperties;
    private int _lastShimRegisteredPatchCount = -1;
    private int _shimRecheckCountdown;
    private string? _resourceChangerStateError;
    private readonly Dictionary<string, PcCompatKeyViewerPresentationProjection>
        _keyViewerLabelProjections = new(StringComparer.Ordinal);
    internal PcCompatManagedModSession(
        PcModManifest manifest,
        AssemblyLoadContext loadContext,
        Assembly assembly,
        object instance,
        object unityModEntry,
        IReadOnlyList<PcCompatPatchDescriptor> patches,
        bool bootstrapAttempted,
        bool bootstrapSucceeded,
        bool setupCompleted,
        bool enableCompleted,
        long resourceSessionGeneration,
        bool usesRewrittenAssembly)
    {
        Manifest = manifest;
        _loadContext = loadContext;
        Assembly = assembly;
        Instance = instance;
        _lifecycle = new PcCompatManagedLifecycleController(instance);
        RegisteredPatches = patches;
        BootstrapAttempted = bootstrapAttempted;
        BootstrapSucceeded = bootstrapSucceeded;
        SetupCompleted = setupCompleted;
        EnableCompleted = enableCompleted;
        ResourceSessionGeneration = resourceSessionGeneration;
        UsesRewrittenAssembly = usesRewrittenAssembly;
        _enableContext = new PcCompatManagedExecutionState(
            manifest.Id,
            resourceSessionGeneration,
            PcCompatManagedExecutionPhase.Enable);
        _updateContext = _enableContext with { Phase = PcCompatManagedExecutionPhase.Update };
        _disableContext = _enableContext with { Phase = PcCompatManagedExecutionPhase.Disable };
        _resourceFeatureGroups = PcCompatResourceRecipeRuntime.GetPlan(manifest.Id)?.FeatureGroups
            .Select(group => group.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();
        _settingsController = CreateSettingsController(
            manifest,
            resourceSessionGeneration,
            loadContext,
            instance,
            unityModEntry,
            out _settingsUnavailableReason);
        _resourceChangerStateAdapter = PcCompatResourceChangerStateAdapter.TryCreate(
            assembly,
            manifest.Id,
            resourceSessionGeneration,
            out var resourceChangerStateError);
        if (resourceChangerStateError != null)
        {
            _resourceChangerStateError = resourceChangerStateError;
            Logger.Warn(
                LogTag,
                $"ResourceChanger state adapter unavailable mod={manifest.Id}: {resourceChangerStateError}");
        }
        if (setupCompleted)
            _callbackDispatcher = BuildCallbackDispatcher();
    }

    public PcModManifest Manifest { get; }
    public Assembly Assembly { get; }
    public object Instance { get; }
    public IReadOnlyList<PcCompatPatchDescriptor> RegisteredPatches { get; }
    public bool BootstrapAttempted { get; }
    public bool BootstrapSucceeded { get; }
    public bool SetupCompleted { get; }
    public bool EnableCompleted { get; private set; }
    public long ResourceSessionGeneration { get; }
    public bool UsesRewrittenAssembly { get; }
    public bool ActivationPending =>
        Volatile.Read(ref _activationRequested) != 0 &&
        Volatile.Read(ref _activationFailed) == 0 &&
        _lifecycle.State == PcCompatManagedLifecycleState.Loaded;
    public bool ActivationFailed => Volatile.Read(ref _activationFailed) != 0;
    public bool ManagedPresentationClaimed => Volatile.Read(ref _managedPresentationClaimed) != 0;
    public PcCompatManagedEventDispatchStats? ManagedEventDispatchStats => _callbackDispatcher?.SnapshotStats();
    public string? ActivationStatus => Volatile.Read(ref _activationStatus);
    public string ManagedFailureReportPath => Path.Combine(
        Manifest.FolderPath,
        ".pccompat",
        "last_managed_failure.txt");
    public string SettingsFailureReportPath => Path.Combine(
        Manifest.FolderPath,
        ".pccompat",
        "last_settings_failure.txt");
    public PcCompatManagedSettingsSnapshot Settings => SnapshotSettings();
    public PcCompatManagedSettingsSchemaSnapshot SettingsSchema =>
        _settingsController?.SnapshotSchema() ?? new PcCompatManagedSettingsSchemaSnapshot
        {
            ModId = Manifest.Id,
            Error = _settingsUnavailableReason ?? "verified MOD settings schema is unavailable"
        };
    public bool RequiresFrameDispatch =>
        Volatile.Read(ref _pendingContinuationFailure) != null ||
        ActivationPending ||
        ManagedPresentationClaimed ||
        _lifecycle.RequiresFrameDispatch ||
        SettingsRequiresDispatch ||
        PcCompatManagedComponentBridge.HasComponents(Manifest.Id, ResourceSessionGeneration) ||
        _resourceChangerStateAdapter != null ||
        (_callbackDispatcher?.PrefixRuleCount ?? 0) != 0;
    public bool RequiresContinuousFrameDispatch =>
        Volatile.Read(ref _pendingContinuationFailure) != null ||
        ManagedPresentationClaimed ||
        _lifecycle.RequiresFrameDispatch ||
        SettingsRequiresDispatch ||
        PcCompatManagedComponentBridge.HasComponents(Manifest.Id, ResourceSessionGeneration) ||
        _resourceChangerStateAdapter != null ||
        (_callbackDispatcher?.PrefixRuleCount ?? 0) != 0;
    public bool RequiresManagedFrameDispatch =>
        Volatile.Read(ref _pendingContinuationFailure) != null ||
        ActivationPending ||
        ManagedPresentationClaimed ||
        _lifecycle.RequiresFrameDispatch ||
        PcCompatManagedComponentBridge.HasComponents(Manifest.Id, ResourceSessionGeneration) ||
        _resourceChangerStateAdapter != null ||
        (_callbackDispatcher?.PrefixRuleCount ?? 0) != 0;
    public bool RequiresOnGUIDispatch =>
        SettingsRequiresOnGUIDispatch ||
        (_lifecycle.State == PcCompatManagedLifecycleState.Enabled &&
         !ActivationPending &&
         PcCompatManagedComponentBridge.HasOnGUIComponents(
             Manifest.Id,
             ResourceSessionGeneration));
    internal PcCompatManagedLifecycleState LifecycleState => _lifecycle.State;
    public PcCompatManagedLifecycleSnapshot Lifecycle => _lifecycle.Snapshot();
    public PcCompatManagedComponentLifecycleSnapshot ManagedComponentLifecycle =>
        PcCompatManagedComponentBridge.SnapshotLifecycle(
            Manifest.Id,
            ResourceSessionGeneration);
    public string JALibMainThreadStatus => SnapshotJALibMainThreadStatus();
    public string JALibLifecycleStatus => SnapshotJALibLifecycleStatus();
    public string HarmonyShimStatus => SnapshotHarmonyShimStatus();

    private bool SettingsRequiresDispatch
    {
        get
        {
            return _settingsController?.RequiresDispatch == true;
        }
    }

    private bool SettingsRequiresFrameDispatch =>
        _settingsController?.RequiresFrameDispatch == true;

    private bool SettingsRequiresOnGUIDispatch =>
        _settingsController?.RequiresOnGUIDispatch == true;

    public bool RequestSettingsOpen(out string? error)
    {
        if (_settingsController == null)
        {
            error = _settingsUnavailableReason ?? "original MOD settings surface is unavailable";
            return false;
        }
        if (Volatile.Read(ref _disposed) != 0)
        {
            error = "managed MOD session is disposed";
            return false;
        }
        var requested = _settingsController.RequestOpen(out error);
        if (requested)
            ClearSettingsFailureReport();
        return requested;
    }

    public void RequestSettingsClose()
        => _settingsController?.RequestClose();

    public void RequestSettingsSave()
        => _settingsController?.RequestSave();

    public bool RequestSettingsSchemaValue(
        string revision,
        string path,
        string value,
        out string? error)
    {
        var controller = _settingsController;
        if (controller == null)
        {
            error = _settingsUnavailableReason ?? "verified MOD settings schema is unavailable";
            return false;
        }
        return controller.RequestSchemaValue(revision, path, value, out error);
    }

    public void RequestSettingsSchemaSaveRetry()
        => _settingsController?.RequestSchemaSaveRetry();

    private PcCompatManagedSettingsSnapshot SnapshotSettings()
    {
        var controller = _settingsController;
        if (controller == null)
        {
            return new PcCompatManagedSettingsSnapshot
            {
                State = PcCompatManagedSettingsState.Unavailable,
                Fault = _settingsUnavailableReason,
                Supported = false
            };
        }

        return controller.Snapshot();
    }

    private static PcCompatManagedSettingsController? CreateSettingsController(
        PcModManifest manifest,
        long resourceSessionGeneration,
        AssemblyLoadContext loadContext,
        object instance,
        object unityModEntry,
        out string? error)
    {
        if (!PcCompatManagedSettingsUnityBackend.TryCreate(
                loadContext,
                manifest.Id,
                resourceSessionGeneration,
                out var backend,
                out error))
            return null;
        try
        {
            var umm = loadContext.LoadFromAssemblyName(new AssemblyName("UnityModManager"));
            var bridge = umm.GetType(
                "UnityModManagerNet.PcCompatSettingsUiBridge",
                throwOnError: true)!;
            var register = bridge.GetMethod(
                "Register",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [typeof(object)],
                modifiers: null)
                ?? throw new MissingMethodException(bridge.FullName, "Register");
            register.Invoke(null, [backend]);
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().ToString();
            return null;
        }

        if (!PcCompatManagedSettingsController.TryCreate(
                manifest,
                instance,
                unityModEntry,
                backend!,
                () => PcCompatManagedComponentBridge.SnapshotOwnerGameObjects(
                    manifest.Id,
                    resourceSessionGeneration),
                out var controller,
                out error))
            return null;
        return controller;
    }

    public bool TryResolveManagedIntSequence(
        PcCompatKeyViewerRoleOverride role,
        out int[] values,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(role);
        values = Array.Empty<int>();
        error = null;
        if (!UsesRewrittenAssembly)
        {
            error = "managed session is not using the rewritten assembly";
            return false;
        }
        if (!string.Equals(role.MemberKind, "Method", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(role.MemberName))
        {
            error = "BindingProvider must resolve to a zero-argument method";
            return false;
        }

        try
        {
            var assembly = _loadContext.Assemblies.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.GetName().Name,
                    role.AssemblyName,
                    StringComparison.OrdinalIgnoreCase));
            if (assembly == null)
            {
                error = $"assembly '{role.AssemblyName}' is not loaded in the MOD context";
                return false;
            }
            var type = assembly.GetType(role.TypeName, throwOnError: false, ignoreCase: false);
            if (type == null)
            {
                error = $"type '{role.TypeName}' was not found";
                return false;
            }
            var methods = type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Static | BindingFlags.Instance)
                .Where(method => method.Name == role.MemberName &&
                                 method.GetParameters().Length == 0)
                .ToArray();
            if (methods.Length != 1)
            {
                error = $"method '{role.TypeName}.{role.MemberName}()' resolved {methods.Length} candidates";
                return false;
            }
            var method = methods[0];
            var target = method.IsStatic ? null : ResolveManagedRoleTarget(type);
            if (!method.IsStatic && target == null)
            {
                error = $"instance target for '{role.TypeName}.{role.MemberName}()' is unavailable";
                return false;
            }

            object? result;
            using (PcCompatManagedExecutionContext.Enter(_updateContext))
                result = method.Invoke(target, null);
            if (result is not IEnumerable sequence || result is string)
            {
                error = $"'{role.TypeName}.{role.MemberName}()' did not return an enumerable key sequence";
                return false;
            }
            var resolved = new List<int>(32);
            foreach (var item in sequence)
            {
                if (item == null || resolved.Count >= 32)
                {
                    error = item == null
                        ? "BindingProvider returned a null key"
                        : "BindingProvider returned more than 32 keys";
                    return false;
                }
                resolved.Add(Convert.ToInt32(
                    item,
                    System.Globalization.CultureInfo.InvariantCulture));
            }
            if (resolved.Count == 0)
            {
                error = "BindingProvider returned no keys";
                return false;
            }
            values = resolved.ToArray();
            return true;
        }
        catch (Exception exception)
        {
            var cause = exception is TargetInvocationException { InnerException: not null }
                ? exception.InnerException
                : exception;
            error = $"{cause!.GetType().Name}: {cause.Message}";
            return false;
        }
    }

    private string SnapshotJALibMainThreadStatus()
    {
        try
        {
            var jalib = _loadContext.Assemblies.FirstOrDefault(candidate =>
                            string.Equals(
                                candidate.GetName().Name,
                                "JALib",
                                StringComparison.OrdinalIgnoreCase)) ??
                        _loadContext.LoadFromAssemblyName(new AssemblyName("JALib"));
            var mainThreadType = jalib.GetType(
                "JALib.Tools.MainThread",
                throwOnError: true)!;
            var status = mainThreadType.GetMethod(
                    "GetDiagnosticStatus",
                    BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, null) as string;
            return string.IsNullOrWhiteSpace(status)
                ? "unavailable: GetDiagnosticStatus returned no value"
                : status.Replace('\r', ' ').Replace('\n', ' ');
        }
        catch (Exception exception)
        {
            var root = exception.GetBaseException();
            return $"unavailable: {root.GetType().Name}: {root.Message}"
                .Replace('\r', ' ')
                .Replace('\n', ' ');
        }
    }

    private string SnapshotJALibLifecycleStatus()
    {
        try
        {
            var status = Instance.GetType().GetMethod(
                    "GetCompatDiagnosticStatus",
                    BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(Instance, null) as string;
            return string.IsNullOrWhiteSpace(status)
                ? "unavailable: GetCompatDiagnosticStatus returned no value"
                : status;
        }
        catch (Exception exception)
        {
            var root = exception.GetBaseException();
            return $"unavailable: {root.GetType().Name}: {root.Message}";
        }
    }

    /// <summary>
    /// Exports the shim Harmony registry: how many logical registrations exist, how many are still
    /// active, and every ABI member the MOD reached that cannot behave like upstream here. Without this
    /// an unexplained MOD behaviour has no paper trail, since the shim never throws for an unavailable
    /// member - it records and continues.
    /// </summary>
    private string SnapshotHarmonyShimStatus()
    {
        try
        {
            var registryType = PcCompatShimPatchRegistries.Resolve(
                _loadContext,
                PcCompatShimPatchRegistries.All.Single(registry => registry.AssemblyName == "0Harmony"));

            var records = PcCompatShimPatchRegistries.Snapshot(registryType);
            var active = 0;
            if (records != null)
            {
                foreach (var record in records)
                {
                    if (record.GetType().GetProperty("Active")?.GetValue(record) is true)
                        ++active;
                }
            }

            var builder = new StringBuilder();
            builder.Append("registrations=").Append(records?.Length ?? 0)
                .Append(" active=").Append(active);

            var diagnostics = (Array?)registryType
                .GetMethod("SnapshotDiagnostics", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, null);
            builder.Append(" diagnostics=").Append(diagnostics?.Length ?? 0);

            if (diagnostics != null)
            {
                foreach (var diagnostic in diagnostics)
                {
                    // One line per diagnostic, newlines stripped: the export is line-oriented and a
                    // multi-line detail would be read as separate fields.
                    builder.AppendLine().Append("  ").Append(
                        diagnostic.ToString()?.Replace('\r', ' ').Replace('\n', ' ') ?? "<null>");
                }
            }

            return builder.ToString();
        }
        catch (Exception exception)
        {
            var root = exception.GetBaseException();
            return $"unavailable: {root.GetType().Name}: {root.Message}"
                .Replace('\r', ' ')
                .Replace('\n', ' ');
        }
    }

    public bool TryProjectManagedKeyViewerLabels(
        PcCompatKeyViewerRoleOverride role,
        IReadOnlyList<string> labels,
        bool adoptLegacyTouchLabels,
        out int changed,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(labels);
        changed = 0;
        if (!TryResolveManagedStringCollection(
                role,
                out var type,
                out var values,
                out error))
        {
            return false;
        }

        if (!_keyViewerLabelProjections.TryGetValue(role.CandidateKey, out var projection))
        {
            projection = new PcCompatKeyViewerPresentationProjection();
            _keyViewerLabelProjections.Add(role.CandidateKey, projection);
        }
        if (!projection.TryApply(
                values,
                labels,
                adoptLegacyTouchLabels,
                out var changedIndices,
                out error))
        {
            return false;
        }
        changed = changedIndices.Length;
        return TryRefreshManagedKeyViewerLabels(type, changedIndices, out error);
    }

    public bool TryRestoreManagedKeyViewerLabels(
        PcCompatKeyViewerRoleOverride role,
        int laneCount,
        out int changed,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(role);
        changed = 0;
        error = null;
        Type type;
        int[] changedIndices;
        if (_keyViewerLabelProjections.Remove(role.CandidateKey, out var projection))
        {
            if (!projection.TryRestore(out changedIndices, out error))
                return false;
            if (!TryResolveManagedStringCollection(role, out type, out _, out error))
                return false;
        }
        else
        {
            if (!TryResolveManagedStringCollection(role, out type, out var values, out error))
                return false;
            if (!PcCompatKeyViewerPresentationDefaults.TryClearLegacyTouchLabels(
                    values,
                    laneCount,
                    out changedIndices,
                    out error))
            {
                return false;
            }
        }
        changed = changedIndices.Length;
        if (changedIndices.Length == 0)
            return true;
        return TryRefreshManagedKeyViewerLabels(type, changedIndices, out error);
    }

    private bool TryResolveManagedStringCollection(
        PcCompatKeyViewerRoleOverride role,
        out Type type,
        out IList values,
        out string? error)
    {
        type = null!;
        values = null!;
        error = null;
        if (!UsesRewrittenAssembly)
        {
            error = "managed session is not using the rewritten assembly";
            return false;
        }
        if (!string.Equals(role.MemberKind, "Method", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(role.MemberName))
        {
            error = "LabelProvider must resolve to a zero-argument method";
            return false;
        }

        try
        {
            var assembly = _loadContext.Assemblies.FirstOrDefault(candidate =>
                string.Equals(candidate.GetName().Name, role.AssemblyName,
                    StringComparison.OrdinalIgnoreCase));
            type = assembly?.GetType(role.TypeName, throwOnError: false, ignoreCase: false)!;
            if (type == null)
            {
                error = $"type '{role.TypeName}' was not found";
                return false;
            }
            var methods = type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Static | BindingFlags.Instance)
                .Where(method => method.Name == role.MemberName &&
                                 method.GetParameters().Length == 0)
                .ToArray();
            if (methods.Length != 1)
            {
                error = $"method '{role.TypeName}.{role.MemberName}()' resolved {methods.Length} candidates";
                return false;
            }
            var method = methods[0];
            var target = method.IsStatic ? null : ResolveManagedRoleTarget(type);
            if (!method.IsStatic && target == null)
            {
                error = $"instance target for '{role.TypeName}.{role.MemberName}()' is unavailable";
                return false;
            }
            object? result;
            using (PcCompatManagedExecutionContext.Enter(_updateContext))
                result = method.Invoke(target, null);
            if (result is not IList collection)
            {
                error = $"'{role.TypeName}.{role.MemberName}()' did not return a mutable string collection";
                return false;
            }
            values = collection;
            return true;
        }
        catch (Exception exception)
        {
            var cause = exception is TargetInvocationException { InnerException: not null }
                ? exception.InnerException
                : exception;
            error = $"{cause!.GetType().Name}: {cause.Message}";
            return false;
        }
    }

    private bool TryRefreshManagedKeyViewerLabels(
        Type providerType,
        IReadOnlyList<int> changedIndices,
        out string? error)
    {
        error = null;
        if (changedIndices.Count == 0)
            return true;
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Static | BindingFlags.Instance;
            var owner = ResolveManagedRoleTarget(providerType);
            var keysField = providerType.GetField("Keys", flags);
            if (keysField == null || !keysField.IsStatic && owner == null)
                return true;
            if (keysField.GetValue(keysField.IsStatic ? null : owner) is not IList keys)
                return true;
            var refreshMethods = providerType.GetMethods(flags)
                .Where(method => method.IsStatic &&
                                 method.Name == "UpdateKeyText" &&
                                 method.ReturnType == typeof(void))
                .Where(method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length == 2 && parameters[1].ParameterType == typeof(int);
                })
                .ToArray();
            if (refreshMethods.Length == 0)
                return true;
            if (refreshMethods.Length != 1)
            {
                error = $"'{providerType.FullName}.UpdateKeyText' resolved {refreshMethods.Length} candidates";
                return false;
            }
            var refresh = refreshMethods[0];
            var keyType = refresh.GetParameters()[0].ParameterType;
            using (PcCompatManagedExecutionContext.Enter(_updateContext))
            {
                foreach (var index in changedIndices)
                {
                    if (index < 0 || index >= keys.Count || keys[index] is not { } key)
                        continue;
                    if (!keyType.IsInstanceOfType(key))
                    {
                        error = $"Keys[{index}] is {key.GetType().FullName}, expected {keyType.FullName}";
                        return false;
                    }
                    refresh.Invoke(null, [key, index]);
                }
            }
            return true;
        }
        catch (Exception exception)
        {
            var cause = exception is TargetInvocationException { InnerException: not null }
                ? exception.InnerException
                : exception;
            error = $"{cause!.GetType().Name}: {cause.Message}";
            return false;
        }
    }

    public void RequestActivation()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(PcCompatManagedModSession));
        Volatile.Write(ref _activationStatus, "waiting for UnityMain activation");
        Volatile.Write(
            ref _activationDeadlineTimestamp,
            Stopwatch.GetTimestamp() +
            (long)(Stopwatch.Frequency * (ActivationTimeoutMilliseconds / 1000d)));
        Volatile.Write(ref _activationRequested, 1);
    }

    public bool TryEnable(out string? error)
    {
        ClearManagedFailureReport();
        using var execution = PcCompatManagedExecutionContext.Enter(_enableContext);
        var enabled = _lifecycle.TryEnable(out error);
        EnableCompleted = enabled;
        if (enabled)
        {
            Volatile.Write(ref _activationStatus, "enabled");
            RefreshResourceChangerState();
        }
        else
        {
            if (!TryClearManagedComponents(out var cleanupError) && cleanupError != null)
                error = (error ?? "CompatEnable failed") + Environment.NewLine + cleanupError;
            PersistManagedFailure(error ?? "CompatEnable failed");
        }
        return enabled;
    }

    public bool TryDispatchUpdate(float deltaTime)
    {
        if (!TryApplyPendingContinuationFailure())
            return false;
        if (ActivationPending)
        {
            if (!TryAdvanceActivation())
                return !ActivationFailed;
            // Let the host transfer presentation ownership before the first
            // CompatUpdate so recipe and managed HUDs never update together.
            return true;
        }
        var hasComponents = PcCompatManagedComponentBridge.HasComponents(
            Manifest.Id,
            ResourceSessionGeneration);
        if (_lifecycle.State != PcCompatManagedLifecycleState.Enabled)
        {
            if (hasComponents)
                TryClearManagedComponents(out _);
            return false;
        }
        if (!_lifecycle.RequiresFrameDispatch && !hasComponents &&
            _resourceChangerStateAdapter == null)
            return _lifecycle.State == PcCompatManagedLifecycleState.Enabled;

        using var execution = PcCompatManagedExecutionContext.Enter(_updateContext);
        var updated = !_lifecycle.RequiresFrameDispatch ||
                      _lifecycle.TryDispatchUpdate(deltaTime);
        string? componentError = null;
        if (updated && hasComponents)
        {
            updated = PcCompatManagedComponentBridge.TryDispatchFrame(
                Manifest.Id,
                ResourceSessionGeneration,
                deltaTime,
                out componentError);
            if (!updated)
            {
                _lifecycle.FaultFromChild(
                    componentError ?? "Managed component frame dispatch failed.");
            }
        }
        if (updated)
            RefreshResourceChangerState();
        if (!updated)
        {
            EnableCompleted = false;
            if (!TryClearManagedComponents(out var cleanupError) && cleanupError != null)
            {
                componentError = string.IsNullOrWhiteSpace(componentError)
                    ? cleanupError
                    : componentError + Environment.NewLine + cleanupError;
            }
            PersistManagedFailure(
                componentError ??
                _lifecycle.Snapshot().LastError ??
                "CompatUpdate failed");
        }
        return updated;
    }

    private void RefreshResourceChangerState()
    {
        if (_resourceChangerStateAdapter == null)
            return;
        if (_resourceChangerStateAdapter.Refresh(out var error))
        {
            _resourceChangerStateError = null;
            return;
        }
        if (string.Equals(_resourceChangerStateError, error, StringComparison.Ordinal))
            return;
        _resourceChangerStateError = error;
        Logger.Warn(LogTag, $"ResourceChanger state publish failed mod={Manifest.Id}: {error}");
    }

    internal bool TryApplyResourceChangerSettings(
        bool changeRabbit,
        bool changeBallColor,
        bool changeTileColor,
        out string? error)
    {
        if (_resourceChangerStateAdapter == null)
        {
            error = "ResourceChanger managed state adapter is unavailable.";
            return false;
        }
        return _resourceChangerStateAdapter.ApplySettings(
            changeRabbit,
            changeBallColor,
            changeTileColor,
            out error);
    }

    internal void ReportManagedContinuationFailure(
        PcCompatManagedExecutionState owner,
        string error)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var failure =
            $"Managed UnityMain continuation failed during {owner.Phase}: {error}";
        if (Interlocked.CompareExchange(
                ref _pendingContinuationFailure,
                failure,
                null) != null)
            return;

        Volatile.Write(ref _activationStatus, failure);
        PersistManagedFailure(failure, owner);
    }

    private bool TryApplyPendingContinuationFailure()
    {
        var failure = Interlocked.Exchange(ref _pendingContinuationFailure, null);
        if (failure == null)
            return true;

        var activationWasPending = ActivationPending;
        using var execution = PcCompatManagedExecutionContext.Enter(_updateContext);
        if (_lifecycle.State is not (
                PcCompatManagedLifecycleState.Faulted or
                PcCompatManagedLifecycleState.Disposed))
        {
            _lifecycle.FaultFromChild(failure);
        }
        EnableCompleted = false;
        if (activationWasPending)
        {
            Volatile.Write(ref _activationRequested, 0);
            Volatile.Write(ref _activationFailed, 1);
        }
        Volatile.Write(ref _activationStatus, failure);

        if (!TryClearManagedComponents(out var cleanupError) && cleanupError != null)
            failure += Environment.NewLine + cleanupError;
        PersistManagedFailure(failure, _updateContext);
        return false;
    }

    // OnGUI faults follow the same fail-closed policy as Update faults: the
    // offending component is destroyed by the bridge and the session is
    // marked faulted with a persisted failure report.
    public bool TryDispatchOnGUI()
    {
        if (SettingsRequiresOnGUIDispatch &&
            (ActivationPending || _lifecycle.State != PcCompatManagedLifecycleState.Enabled))
            return TryDispatchSettingsOnGUI();
        if (ActivationPending || _lifecycle.State != PcCompatManagedLifecycleState.Enabled)
            return true;
        if (!PcCompatManagedComponentBridge.HasComponents(
                Manifest.Id,
                ResourceSessionGeneration))
            return TryDispatchSettingsOnGUI();

        using var execution = PcCompatManagedExecutionContext.Enter(_updateContext);
        if (PcCompatManagedComponentBridge.TryDispatchOnGUI(
                Manifest.Id,
                ResourceSessionGeneration,
                out var componentError))
            return TryDispatchSettingsOnGUI();

        _lifecycle.FaultFromChild(componentError ?? "Managed component OnGUI dispatch failed.");
        EnableCompleted = false;
        if (!TryClearManagedComponents(out var cleanupError) && cleanupError != null)
        {
            componentError = string.IsNullOrWhiteSpace(componentError)
                ? cleanupError
                : componentError + Environment.NewLine + cleanupError;
        }
        PersistManagedFailure(componentError ?? "CompatOnGUI failed");
        return false;
    }

    private bool TryDispatchSettingsOnGUI()
    {
        var controller = _settingsController;
        if (controller == null || !SettingsRequiresOnGUIDispatch)
            return true;
        return TryDispatchSettings(controller, onGui: true);
    }

    public bool TryDispatchSettingsFrame()
    {
        var controller = _settingsController;
        if (controller == null || !SettingsRequiresFrameDispatch)
            return true;
        return TryDispatchSettings(controller, onGui: false);
    }

    private bool TryDispatchSettings(
        PcCompatManagedSettingsController controller,
        bool onGui)
    {
        var beforeState = controller.State;
        var beforeSurface = controller.SurfaceKind;
        using var execution = PcCompatManagedExecutionContext.Enter(_updateContext);
        var dispatched = onGui
            ? controller.Dispatch()
            : controller.DispatchFrame();
        var afterState = controller.State;
        var afterSurface = controller.SurfaceKind;
        if (beforeState != afterState || beforeSurface != afterSurface)
        {
            Logger.Info(
                LogTag,
                $"mod={Manifest.Id} settings_surface=transition " +
                $"lane={(onGui ? "ongui" : "frame")} " +
                $"state={beforeState}->{afterState} " +
                $"surface={beforeSurface}->{afterSurface} " +
                $"lifecycle={_lifecycle.State}");
        }
        if (dispatched)
            return true;

        var failure = controller.Snapshot().Fault ?? "original MOD settings callback failed";
        PersistSettingsFailure(failure);
        Logger.Error(
            LogTag,
            $"mod={Manifest.Id} settings_surface=faulted " +
            $"error={SingleLineSummary(failure)} report={SettingsFailureReportPath}");
        return true;
    }

    public void MarkManagedPresentationClaimed()
        => Volatile.Write(ref _managedPresentationClaimed, 1);

    // Game event callbacks (JAPatch postfixes) only flow while managed self-render
    // owns presentation; in compat-render mode the native fixed ops already update
    // the compatibility overlay and the MOD's own uGUI tree stays inactive.
    public bool TryDispatchManagedCallbacks()
        => TryDispatchManagedCallbacks(collector: null);

    internal bool TryCollectManagedCallbacks(PcCompatManagedEventDispatchCollector collector)
        => TryDispatchManagedCallbacks(collector);

    private bool TryDispatchManagedCallbacks(PcCompatManagedEventDispatchCollector? collector)
    {
        if (!EnableCompleted || ActivationPending || Volatile.Read(ref _disposed) != 0)
            return true;

        var dispatcher = _callbackDispatcher;
        if (dispatcher == null)
        {
            var now = Stopwatch.GetTimestamp();
            if (now < _nextCallbackBuildTimestamp)
                return true;
            _nextCallbackBuildTimestamp = now + Stopwatch.Frequency * CallbackBuildRetrySeconds;
            dispatcher = BuildCallbackDispatcher();
            _callbackDispatcher = dispatcher;
            if (dispatcher != null && dispatcher.RuleCount != 0)
                Logger.Info(
                    LogTag,
                    $"mod={Manifest.Id} managed_event_dispatch=ready rules={dispatcher.RuleCount} prefixes={dispatcher.PrefixRuleCount}");
        }
        else if (ShimRegistryChanged())
        {
            // Features may register patches late (e.g. only when a level starts). The shim
            // exposes a cheap registry counter; when it moves, rebuild the binding table so
            // late arrivals get bound instead of staying skipped for the whole session.
            var rebuilt = BuildCallbackDispatcher();
            if (rebuilt != null)
            {
                _callbackDispatcher = rebuilt;
                dispatcher = rebuilt;
                Logger.Info(
                    LogTag,
                    $"mod={Manifest.Id} managed_event_dispatch=rebuilt rules={rebuilt.RuleCount}");
            }
        }

        if (!ManagedPresentationClaimed)
            return true;

        if (dispatcher == null || dispatcher.RuleCount == 0)
            return true;

        using var execution = PcCompatManagedExecutionContext.Enter(_updateContext);
        var drain = PcCompatRuntime.ManagedEventDrain;
        var boxedValueReader = PcCompatRuntime.ManagedBoxedValueReader;
        if (drain == null || boxedValueReader == null)
            return true;

        _managedEventBuffer ??= new byte[PcCompatManagedCallbackDispatcher.BufferSize];
        try
        {
            return dispatcher.DrainAndDispatch(
                drain,
                boxedValueReader,
                _managedEventBuffer,
                PublishHitMarginsEventSnapshot,
                RefreshHitMarginsFallback,
                collector,
                this);
        }
        catch (Exception exception)
        {
            Logger.Error(
                LogTag,
                $"mod={Manifest.Id} managed_event_dispatch=frame_fault error={exception.Message}");
            return false;
        }
    }

    internal bool DispatchCollectedManagedCallback(
        PcCompatManagedCallbackDispatcher dispatcher,
        byte[] buffer,
        int cursor,
        PcCompatManagedBoxedValueHandler boxedValueReader)
    {
        if (!EnableCompleted ||
            ActivationPending ||
            !ManagedPresentationClaimed ||
            Volatile.Read(ref _disposed) != 0)
            return true;

        try
        {
            using var execution = PcCompatManagedExecutionContext.Enter(_updateContext);
            dispatcher.DispatchRecord(
                buffer,
                cursor,
                boxedValueReader,
                PublishHitMarginsEventSnapshot,
                RefreshHitMarginsFallback);
            return true;
        }
        catch (Exception exception)
        {
            Logger.Error(
                LogTag,
                $"mod={Manifest.Id} managed_event_dispatch=record_fault error={exception.Message}");
            return false;
        }
    }

    internal int DispatchManagedPrefix(
        uint patchId,
        ref PcCompatManagedPrefixInvocationV2 invocation)
    {
        if (!EnableCompleted ||
            ActivationPending ||
            _lifecycle.State != PcCompatManagedLifecycleState.Enabled ||
            Volatile.Read(ref _disposed) != 0)
            return -1;

        var dispatcher = _callbackDispatcher;
        var boxedValueReader = PcCompatRuntime.ManagedBoxedValueReader;
        if (dispatcher == null || dispatcher.PrefixRuleCount == 0 || boxedValueReader == null)
            return -2;

        using var execution = PcCompatManagedExecutionContext.Enter(_updateContext);
        return dispatcher.TryDispatchSynchronousPrefix(
            patchId,
            ref invocation,
            boxedValueReader,
            out var runOriginal)
            ? runOriginal ? 1 : 0
            : -3;
    }

    private object? ResolveManagedRoleTarget(Type type)
    {
        if (type.IsInstanceOfType(Instance))
            return Instance;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Static;
        var instanceField = type.GetField("Instance", flags);
        if (instanceField?.GetValue(null) is { } fieldValue && type.IsInstanceOfType(fieldValue))
            return fieldValue;
        var instanceProperty = type.GetProperty("Instance", flags);
        if (instanceProperty?.GetMethod != null &&
            instanceProperty.GetValue(null) is { } propertyValue &&
            type.IsInstanceOfType(propertyValue))
            return propertyValue;
        return null;
    }

    private static void RefreshHitMarginsFallback()
    {
        try
        {
            PcCompatReversePatchBridge.RefreshHitMarginsCount();
        }
        catch
        {
            // Platform refresh failures are fused off on their own side.
        }
    }

    private static void PublishHitMarginsEventSnapshot(bool valid, ReadOnlySpan<int> counts)
    {
        if (valid)
            PcCompatReversePatchBridge.PublishHitMarginsCount(counts);
        else
            PcCompatReversePatchBridge.ClearHitMarginsCount();
    }

    private PcCompatManagedCallbackDispatcher? BuildCallbackDispatcher()
    {
        try
        {
            var recipePath = PcCompatRuntime.GetRecipeBundle(Manifest.Id)?.RecipePath;
            if (string.IsNullOrWhiteSpace(recipePath) || !File.Exists(recipePath))
                return null;

            var registrations = SnapshotShimCallbacks();
            var dispatcher = PcCompatManagedCallbackDispatcher.Build(
                Manifest.Id,
                recipePath,
                registrations,
                RegisteredPatches);
            try
            {
                PcCompatRuntime.PublishManagedPrefixOrderPlan(
                    Manifest.Id,
                    dispatcher.PrefixOrderPlan);
            }
            catch (Exception exception)
            {
                Logger.Warn(
                    LogTag,
                    $"mod={Manifest.Id} managed_prefix_order=publish_failed " +
                    $"error={exception.GetType().Name}: {exception.Message}; using deterministic recipe order");
            }
            try
            {
                PcCompatRuntime.PublishManagedPostfixOrderPlan(
                    Manifest.Id,
                    dispatcher.PostfixOrderPlan);
            }
            catch (Exception exception)
            {
                Logger.Warn(
                    LogTag,
                    $"mod={Manifest.Id} managed_postfix_order=publish_failed " +
                    $"error={exception.GetType().Name}: {exception.Message}; using deterministic recipe order");
            }
            return dispatcher;
        }
        catch (Exception exception)
        {
            Logger.Error(
                LogTag,
                $"mod={Manifest.Id} managed_event_dispatch=build_failed error={exception.Message}");
            return null;
        }
    }

    private bool ShimRegistryChanged()
    {
        if (--_shimRecheckCountdown > 0)
            return false;
        _shimRecheckCountdown = 60;
        var properties = _shimRegisteredPatchCountProperties;
        if (properties == null || properties.Length == 0)
            return false;
        if (!TryReadShimRegistrationCount(properties, out var count))
            return false;
        return count != _lastShimRegisteredPatchCount;
    }

    // Same registries the loader snapshots into plain descriptors, but keeping the live
    // MethodInfo/delegate target so callbacks can actually be invoked. Older shim
    // builds without the CallbackMethodInfo property yield no registrations (the
    // dispatcher then stays empty instead of guessing methods by name).
    private PcCompatShimCallbackRegistration[] SnapshotShimCallbacks()
    {
        var registrations = new List<PcCompatShimCallbackRegistration>();
        var countProperties = new List<PropertyInfo>();
        var snapshotLength = 0;

        foreach (var registry in PcCompatShimPatchRegistries.All)
        {
            if (!PcCompatShimPatchRegistries.TryResolve(_loadContext, registry, out var registryType))
                continue;

            var countProperty = PcCompatShimPatchRegistries.CountProperty(registryType);
            if (countProperty != null)
                countProperties.Add(countProperty);

            var snapshot = PcCompatShimPatchRegistries.Snapshot(registryType);
            if (snapshot == null || snapshot.Length == 0)
                continue;
            snapshotLength += snapshot.Length;

            foreach (var record in snapshot)
            {
                var type = record.GetType();
                string Get(string name) => type.GetProperty(name)?.GetValue(record)?.ToString() ?? string.Empty;
                long GetInt64(string name)
                    => Convert.ToInt64(
                        type.GetProperty(name)?.GetValue(record) ?? 0,
                        System.Globalization.CultureInfo.InvariantCulture);
                int GetInt32(string name, int fallback = 0)
                {
                    var value = type.GetProperty(name)?.GetValue(record);
                    return value == null
                        ? fallback
                        : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
                }
                string[] GetStrings(string name)
                    => type.GetProperty(name)?.GetValue(record) is IEnumerable<string> values
                        ? values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
                        : Array.Empty<string>();
                var method = type.GetProperty("CallbackMethodInfo")?.GetValue(record) as MethodInfo;
                if (method == null)
                    continue;
                var target = type.GetProperty("CallbackTarget")?.GetValue(record);
                var activeGetter = type.GetProperty("Active")?.GetMethod;
                if (activeGetter == null)
                    continue;
                var isActive = activeGetter.CreateDelegate<Func<bool>>(record);

                registrations.Add(new PcCompatShimCallbackRegistration
                {
                    TargetType = Get("TargetType"),
                    TargetMethod = Get("TargetMethod"),
                    Kind = Get("Kind"),
                    CallbackType = Get("CallbackType"),
                    CallbackMethod = Get("CallbackMethod"),
                    Method = method,
                    OriginalMethod = type.GetProperty("OriginalMethod")?.GetValue(record) as MethodBase,
                    IsActive = isActive,
                    Target = target,
                    Owner = Get("HarmonyId") is { Length: > 0 } harmonyId
                        ? harmonyId
                        : Get("PatchId"),
                    RegistrationIndex = GetInt64("RegistrationIndex"),
                    Priority = GetInt32("Priority", -1),
                    Before = GetStrings("Before"),
                    After = GetStrings("After")
                });
            }
        }

        _shimRegisteredPatchCountProperties = countProperties.ToArray();
        UpdateShimRegistrationCount(snapshotLength);
        return registrations.ToArray();
    }

    private void UpdateShimRegistrationCount(int snapshotLength)
    {
        var properties = _shimRegisteredPatchCountProperties;
        _lastShimRegisteredPatchCount = properties != null && TryReadShimRegistrationCount(properties, out var count)
            ? count
            : snapshotLength;
    }

    /// <summary>
    /// Sums the registries' live counts. Either every registry answers or the read is abandoned - a
    /// partial sum would look like a registry change on the next poll and rebuild the dispatcher for
    /// nothing.
    /// </summary>
    private static bool TryReadShimRegistrationCount(PropertyInfo[] properties, out int count)
    {
        count = 0;
        try
        {
            foreach (var property in properties)
            {
                if (property.GetValue(null) is not int value)
                    return false;
                count += value;
            }
        }
        catch
        {
            return false;
        }

        return true;
    }

    public void ClearManagedPresentationClaim()
        => Volatile.Write(ref _managedPresentationClaimed, 0);

    public void Disable()
    {
        using var execution = PcCompatManagedExecutionContext.Enter(_disableContext);
        _lifecycle.Disable();
        if (!PcCompatManagedComponentBridge.TryClearSession(
                Manifest.Id,
                ResourceSessionGeneration,
                out var componentError) &&
            componentError != null)
        {
            _lifecycle.FaultFromChild(componentError);
        }
        EnableCompleted = false;
        Volatile.Write(ref _activationRequested, 0);
        if (_lifecycle.State == PcCompatManagedLifecycleState.Faulted)
        {
            PersistManagedFailure(
                _lifecycle.Snapshot().LastError ?? "CompatDisable failed");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Disable();
        if (Instance is IDisposable disposable)
            disposable.Dispose();

        _lifecycle.MarkDisposed();
        if (_loadContext.IsCollectible)
            _loadContext.Unload();
    }

    private bool TryAdvanceActivation()
    {
        var now = Stopwatch.GetTimestamp();
        var next = Volatile.Read(ref _nextActivationPollTimestamp);
        if (next != 0 && now < next)
            return false;
        Volatile.Write(
            ref _nextActivationPollTimestamp,
            now + (long)(Stopwatch.Frequency * (ActivationPollMilliseconds / 1000d)));

        if (ResourceSessionGeneration != 0 &&
            (!PcCompatResourceRecipeRuntime.TryGetSessionGeneration(
                 Manifest.Id,
                 out var currentGeneration) ||
             currentGeneration != ResourceSessionGeneration))
        {
            FailActivation("resource session generation changed before managed enable");
            return false;
        }

        if (_resourceFeatureGroups.Length != 0)
        {
            var deadline = Volatile.Read(ref _activationDeadlineTimestamp);
            if (deadline != 0 && now >= deadline)
            {
                FailActivation(
                    $"managed activation timed out after " +
                    $"{ActivationTimeoutMilliseconds / 1000}s: " +
                    (Volatile.Read(ref _activationStatus) ?? "resources still pending") +
                    $" (generation={ResourceSessionGeneration})");
                return false;
            }

            if (UsesRewrittenAssembly)
            {
                if (!PcCompatVirtualBundleRegistry.HasSession(
                        Manifest.Id,
                        ResourceSessionGeneration))
                {
                    Volatile.Write(
                        ref _activationStatus,
                        "waiting for VirtualBundle Resource IR session");
                    return false;
                }
                try
                {
                    if (!PcCompatVirtualBundleRegistry.TryPrepareRequiredAssets(
                            Manifest.Id,
                            ResourceSessionGeneration,
                            out var pendingReason))
                    {
                        Volatile.Write(
                            ref _activationStatus,
                            "waiting for VirtualBundle required assets: " + pendingReason);
                        return false;
                    }
                }
                catch (Exception exception)
                {
                    FailActivation(
                        "VirtualBundle required asset preparation failed: " + exception);
                    return false;
                }
            }
            else if (!TryEnsureLegacyResourceGroups())
            {
                return false;
            }
        }

        Volatile.Write(ref _activationStatus, "invoking CompatEnable on UnityMain");
        Logger.Info(
            "PcCompatManagedSession",
            $"managed activation entering CompatEnable mod={Manifest.Id}");
        var enableStarted = Stopwatch.GetTimestamp();
        if (TryEnable(out var error))
        {
            Logger.Info(
                "PcCompatManagedSession",
                $"managed activation completed CompatEnable mod={Manifest.Id} " +
                $"elapsedMs={(Stopwatch.GetTimestamp() - enableStarted) * 1000d / Stopwatch.Frequency:F3}");
            return true;
        }
        Logger.Error(
            "PcCompatManagedSession",
            $"managed activation failed CompatEnable mod={Manifest.Id} " +
            $"elapsedMs={(Stopwatch.GetTimestamp() - enableStarted) * 1000d / Stopwatch.Frequency:F3} " +
            $"error={SingleLineSummary(error)} report={ManagedFailureReportPath}");
        // TryEnable already persisted the failure while the Enable execution
        // context was active. Do not overwrite it with phase=unknown here.
        FailActivation(error ?? "CompatEnable failed", persistFailureReport: false);
        return false;
    }

    private bool TryEnsureLegacyResourceGroups()
    {
        if (!PcCompatResourceRecipeRuntime.IsRuntimeLoadEnabled())
        {
            Volatile.Write(
                ref _activationStatus,
                PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable + " is disabled");
            return false;
        }
        if (!PcCompatResourceRecipeRuntime.IsLoadSinkRegistered())
        {
            Volatile.Write(ref _activationStatus, "UnityMain AssetBundle scheduler is unavailable");
            return false;
        }

        foreach (var groupId in _resourceFeatureGroups)
        {
            var result = PcCompatResourceRecipeRuntime.TryEnsureFeatureGroupLoaded(
                Manifest.Id,
                groupId);
            if (result.Success)
                continue;
            if (result.Pending)
            {
                Volatile.Write(ref _activationStatus, $"waiting for resource group {groupId}");
                return false;
            }

            var status = ResolveCandidateStatus(groupId);
            if (status is PcCompatResourceCandidateStatus.Controlled or
                PcCompatResourceCandidateStatus.Ready or
                PcCompatResourceCandidateStatus.LoadQueued or
                PcCompatResourceCandidateStatus.Pending)
            {
                Volatile.Write(
                    ref _activationStatus,
                    $"resource group {groupId} requires load authorization: {result.Error}");
                return false;
            }

            FailActivation(
                $"resource group {groupId} cannot load ({status}): {result.Error}");
            return false;
        }
        return true;
    }

    private static string SingleLineSummary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "<empty>";
        var lineEnd = value.IndexOfAny(['\r', '\n']);
        var line = lineEnd < 0 ? value : value[..lineEnd];
        return line.Length <= 512 ? line : line[..509] + "...";
    }

    private bool TryClearManagedComponents(out string? error)
    {
        if (PcCompatManagedExecutionContext.Current?.Phase == PcCompatManagedExecutionPhase.Disable)
        {
            return PcCompatManagedComponentBridge.TryClearSession(
                Manifest.Id,
                ResourceSessionGeneration,
                out error);
        }

        using var execution = PcCompatManagedExecutionContext.Enter(_disableContext);
        return PcCompatManagedComponentBridge.TryClearSession(
            Manifest.Id,
            ResourceSessionGeneration,
            out error);
    }

    private PcCompatResourceCandidateStatus ResolveCandidateStatus(string groupId)
    {
        var plan = PcCompatResourceRecipeRuntime.GetPlan(Manifest.Id);
        var group = plan?.FeatureGroups.FirstOrDefault(candidate =>
            candidate.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
        if (group == null)
            return PcCompatResourceCandidateStatus.Missing;
        return plan!.Candidates.FirstOrDefault(candidate =>
                   candidate.Sha256Hex.Equals(
                       group.SelectedCandidateSha256Hex,
                       StringComparison.OrdinalIgnoreCase))?.Status
               ?? PcCompatResourceCandidateStatus.Missing;
    }

    private void FailActivation(string error, bool persistFailureReport = true)
    {
        Volatile.Write(ref _activationStatus, error);
        Volatile.Write(ref _activationFailed, 1);
        if (persistFailureReport)
            PersistManagedFailure(error);
    }

    private void ClearManagedFailureReport()
    {
        Volatile.Write(ref _managedFailureReportWritten, 0);
        try
        {
            if (File.Exists(ManagedFailureReportPath))
                File.Delete(ManagedFailureReportPath);
        }
        catch
        {
            // A stale diagnostic file must not fail a successful activation.
        }
    }

    private void PersistManagedFailure(
        string error,
        PcCompatManagedExecutionState? executionOverride = null)
    {
        if (Interlocked.CompareExchange(ref _managedFailureReportWritten, 1, 0) != 0)
            return;
        try
        {
            var lifecycle = _lifecycle.Snapshot();
            var execution = executionOverride ?? PcCompatManagedExecutionContext.Current;
            var reportPath = ManagedFailureReportPath;
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

            var report = new StringBuilder(4096)
                .AppendLine("xphorror.PcModCompat managed failure")
                .Append("generatedUtc=").AppendLine(DateTimeOffset.UtcNow.ToString("O"))
                .Append("modId=").AppendLine(Manifest.Id)
                .Append("modName=").AppendLine(Manifest.DisplayName)
                .Append("assembly=").AppendLine(Assembly.FullName ?? Assembly.GetName().Name ?? "unknown")
                .Append("phase=").AppendLine(execution?.Phase.ToString() ?? "unknown")
                .Append("resourceSessionGeneration=").AppendLine(ResourceSessionGeneration.ToString())
                .Append("usesRewrittenAssembly=").AppendLine(UsesRewrittenAssembly.ToString())
                .Append("lifecycleState=").AppendLine(lifecycle.State.ToString())
                .Append("lifecycleFaultCount=").AppendLine(lifecycle.FaultCount.ToString())
                .Append("activationStatus=").AppendLine(ActivationStatus ?? "none")
                .AppendLine("errorBegin")
                .AppendLine(error)
                .AppendLine("errorEnd")
                .ToString();

            File.WriteAllText(reportPath, report, new UTF8Encoding(false));
        }
        catch
        {
            Volatile.Write(ref _managedFailureReportWritten, 0);
            // Failure persistence must not replace the original lifecycle error.
        }
    }

    private void PersistSettingsFailure(string error)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFailureReportPath)!);
            var report = new StringBuilder(2048)
                .AppendLine("xphorror.PcModCompat settings surface failure")
                .Append("generatedUtc=").AppendLine(DateTimeOffset.UtcNow.ToString("O"))
                .Append("modId=").AppendLine(Manifest.Id)
                .Append("modName=").AppendLine(Manifest.DisplayName)
                .Append("resourceSessionGeneration=")
                .AppendLine(ResourceSessionGeneration.ToString())
                .Append("lifecycleState=").AppendLine(_lifecycle.State.ToString())
                .AppendLine("errorBegin")
                .AppendLine(error)
                .AppendLine("errorEnd")
                .ToString();
            File.WriteAllText(
                SettingsFailureReportPath,
                report,
                new UTF8Encoding(false));
        }
        catch
        {
            // Settings diagnostics must not replace the original callback failure.
        }
    }

    private void ClearSettingsFailureReport()
    {
        try
        {
            if (File.Exists(SettingsFailureReportPath))
                File.Delete(SettingsFailureReportPath);
        }
        catch
        {
            // A stale diagnostic file must not block an explicit settings retry.
        }
    }
}

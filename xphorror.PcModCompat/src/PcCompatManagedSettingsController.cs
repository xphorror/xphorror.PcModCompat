using System.Reflection;
using System.Diagnostics;

namespace Xphorror.PcModCompat;

public enum PcCompatManagedSettingsState
{
    Unavailable,
    Closed,
    Opening,
    Open,
    Faulted
}

public enum PcCompatManagedSettingsSurfaceKind
{
    None,
    UnityImGui,
    UnityCanvas
}

public sealed class PcCompatManagedSettingsSnapshot
{
    public PcCompatManagedSettingsState State { get; init; }
    public string? Fault { get; init; }
    public bool Supported { get; init; }
    public PcCompatManagedSettingsSurfaceKind SurfaceKind { get; init; }
    public string? PresentationDiagnostics { get; init; }
}

public interface IPcCompatManagedSettingsCanvasProbe
{
    void BeginCanvasProbe(IReadOnlyList<object> ownerGameObjects);
    bool TryClaimCanvasSurface();
    bool IsClaimedCanvasSurfaceVisible();
    void ReleaseCanvasSurface();
}

internal interface IPcCompatManagedSettingsImGuiTransaction
{
    bool ShouldDispatchCurrentEvent();
    bool IsStable { get; }
    void MarkRecoverableLayoutFailure();
}

public sealed class PcCompatManagedSettingsController
{
    private const int RecoverableLayoutFailureLimit = 3;
    private static readonly long ImGuiOpenTimeoutTicks =
        checked(Stopwatch.Frequency * 3L);

    private readonly object _gate = new();
    private readonly Action _open;
    private readonly Action _draw;
    private readonly Action _save;
    private readonly Action _close;
    private readonly Func<bool> _isVisible;
    private readonly IPcCompatManagedSettingsCanvasProbe? _unityBackend;
    private readonly Func<IReadOnlyList<object>>? _ownerGameObjects;
    private readonly PcCompatManagedSettingsSchemaRuntime? _schema;
    private PcCompatManagedSettingsState _state = PcCompatManagedSettingsState.Closed;
    private PcCompatManagedSettingsSurfaceKind _surfaceKind;
    private bool _openRequested;
    private bool _saveRequested;
    private bool _closeRequested;
    private bool _closeCallbackActive;
    private string? _fault;
    private int _recoverableLayoutFailureCount;
    private long _openStartedTimestamp;

    private PcCompatManagedSettingsController(
        SettingsSurface surface,
        IPcCompatManagedSettingsCanvasProbe? unityBackend = null,
        Func<IReadOnlyList<object>>? ownerGameObjects = null,
        PcCompatManagedSettingsSchemaRuntime? schema = null)
    {
        _open = surface.Open;
        _draw = surface.Draw;
        _save = surface.Save;
        _close = surface.Close;
        _isVisible = surface.IsVisible;
        _unityBackend = unityBackend;
        _ownerGameObjects = ownerGameObjects;
        _schema = schema;
    }

    public static bool TryCreate(
        object primaryTarget,
        object? fallbackTarget,
        out PcCompatManagedSettingsController? controller,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(primaryTarget);
        if (TryBind(primaryTarget, out var surface, out var primaryError) ||
            fallbackTarget != null && TryBind(fallbackTarget, out surface, out _))
        {
            controller = new PcCompatManagedSettingsController(surface!);
            error = null;
            return true;
        }

        controller = null;
        error = primaryError;
        return false;
    }

    public static bool TryCreate(
        object primaryTarget,
        object? fallbackTarget,
        IPcCompatManagedSettingsCanvasProbe canvasProbe,
        Func<IReadOnlyList<object>> ownerGameObjects,
        out PcCompatManagedSettingsController? controller,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(primaryTarget);
        ArgumentNullException.ThrowIfNull(canvasProbe);
        ArgumentNullException.ThrowIfNull(ownerGameObjects);
        if (TryBind(primaryTarget, out var surface, out var primaryError) ||
            fallbackTarget != null && TryBind(fallbackTarget, out surface, out _))
        {
            controller = new PcCompatManagedSettingsController(
                surface!,
                canvasProbe,
                ownerGameObjects);
            error = null;
            return true;
        }

        controller = null;
        error = primaryError;
        return false;
    }

    public static bool TryCreate(
        PcModManifest manifest,
        object primaryTarget,
        object? fallbackTarget,
        PcCompatManagedSettingsUnityBackend unityBackend,
        Func<IReadOnlyList<object>> ownerGameObjects,
        out PcCompatManagedSettingsController? controller,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(primaryTarget);
        ArgumentNullException.ThrowIfNull(unityBackend);
        ArgumentNullException.ThrowIfNull(ownerGameObjects);
        if (!TryBind(primaryTarget, out var surface, out var primaryError) &&
            (fallbackTarget == null || !TryBind(fallbackTarget, out surface, out _)))
        {
            controller = null;
            error = primaryError;
            return false;
        }

        var schema = PcCompatManagedSettingsSchemaRuntime.Create(
            manifest,
            primaryTarget,
            fallbackTarget,
            surface!.Save);
        controller = new PcCompatManagedSettingsController(
            surface,
            unityBackend,
            ownerGameObjects,
            schema);
        error = null;
        return true;
    }

    public bool RequiresDispatch
    {
        get
        {
            lock (_gate)
            {
                return _state is PcCompatManagedSettingsState.Opening or
                           PcCompatManagedSettingsState.Open ||
                       _schema?.HasPendingWork == true;
            }
        }
    }

    public bool RequiresFrameDispatch
    {
        get
        {
            lock (_gate)
            {
                return _openRequested ||
                       _saveRequested ||
                       _closeRequested ||
                       _state == PcCompatManagedSettingsState.Opening ||
                       _schema?.HasPendingWork == true ||
                       (_state == PcCompatManagedSettingsState.Open &&
                        _surfaceKind == PcCompatManagedSettingsSurfaceKind.UnityCanvas);
            }
        }
    }

    public bool RequiresOnGUIDispatch
    {
        get
        {
            lock (_gate)
            {
                return _state == PcCompatManagedSettingsState.Opening ||
                       (_state == PcCompatManagedSettingsState.Open &&
                        _surfaceKind == PcCompatManagedSettingsSurfaceKind.UnityImGui);
            }
        }
    }

    public PcCompatManagedSettingsState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public PcCompatManagedSettingsSurfaceKind SurfaceKind
    {
        get
        {
            lock (_gate)
                return _surfaceKind;
        }
    }

    public bool RequestOpen(out string? error)
    {
        lock (_gate)
        {
            if (_state == PcCompatManagedSettingsState.Faulted)
            {
                // A settings callback fault is isolated from the MOD lifecycle.
                // The previous attempt has already run close/release cleanup, so
                // an explicit user click is also the retry boundary.
                _fault = null;
                _state = PcCompatManagedSettingsState.Closed;
            }
            if (_state is PcCompatManagedSettingsState.Opening or
                PcCompatManagedSettingsState.Open)
            {
                error = null;
                return true;
            }

            _openRequested = true;
            _closeRequested = false;
            _recoverableLayoutFailureCount = 0;
            _openStartedTimestamp = Stopwatch.GetTimestamp();
            _state = PcCompatManagedSettingsState.Opening;
            error = null;
            return true;
        }
    }

    public void RequestSave()
    {
        lock (_gate)
        {
            if (_state == PcCompatManagedSettingsState.Open)
                _saveRequested = true;
        }
    }

    public void RequestClose()
    {
        lock (_gate)
        {
            if (_state == PcCompatManagedSettingsState.Opening)
            {
                if (_openRequested)
                {
                    _openRequested = false;
                    _closeRequested = false;
                    _openStartedTimestamp = 0;
                    _state = PcCompatManagedSettingsState.Closed;
                }
                else
                    _closeRequested = true;
            }
            else if (_state == PcCompatManagedSettingsState.Open)
                _closeRequested = true;
        }
    }

    public void ReleaseForSessionTeardown()
    {
        lock (_gate)
        {
            try
            {
                if (_state == PcCompatManagedSettingsState.Open ||
                    _state == PcCompatManagedSettingsState.Opening && !_openRequested)
                {
                    InvokeClose();
                }
            }
            catch
            {
                // Session teardown must still revoke the settings surface and
                // its input transaction. The outer lifecycle records actual
                // MOD shutdown faults through its existing failure path.
            }
            finally
            {
                _openRequested = false;
                _saveRequested = false;
                _closeRequested = false;
                _fault = null;
                _recoverableLayoutFailureCount = 0;
                _unityBackend?.ReleaseCanvasSurface();
                _surfaceKind = PcCompatManagedSettingsSurfaceKind.None;
                _openStartedTimestamp = 0;
                _state = PcCompatManagedSettingsState.Closed;
            }
        }
    }

    public bool DispatchFrame()
        => DispatchCore(allowImGuiDraw: false);

    public bool Dispatch()
        => DispatchCore(allowImGuiDraw: true);

    private bool DispatchCore(bool allowImGuiDraw)
    {
        lock (_gate)
        {
            var drewImGui = false;
            if (_schema?.HasPendingWork == true)
                _schema.Dispatch(out _);
            if (_state is PcCompatManagedSettingsState.Closed or
                PcCompatManagedSettingsState.Unavailable)
                return true;
            if (_state == PcCompatManagedSettingsState.Faulted)
                return true;

            try
            {
                var skippedImGui = false;
                var canDispatchImGui = allowImGuiDraw &&
                                       CanDispatchImGuiCurrentEvent(out skippedImGui);
                if (_openRequested)
                {
                    _openRequested = false;
                    _unityBackend?.BeginCanvasProbe(
                        _ownerGameObjects?.Invoke() ?? Array.Empty<object>());
                    _open();
                    if (WasReleasedDuringCallback())
                        return true;
                    if (_unityBackend?.TryClaimCanvasSurface() == true)
                    {
                        _surfaceKind = PcCompatManagedSettingsSurfaceKind.UnityCanvas;
                        _openStartedTimestamp = 0;
                        _state = PcCompatManagedSettingsState.Open;
                    }
                    else if (_isVisible())
                    {
                        _surfaceKind = PcCompatManagedSettingsSurfaceKind.UnityImGui;
                    }
                    else
                        throw new InvalidOperationException(
                            "original MOD settings did not enter the visible state");
                }

                if (_state == PcCompatManagedSettingsState.Opening &&
                    _surfaceKind == PcCompatManagedSettingsSurfaceKind.UnityImGui)
                {
                    if (canDispatchImGui)
                    {
                        _draw();
                        drewImGui = true;
                        if (WasReleasedDuringCallback())
                            return true;
                        if (!_isVisible())
                        {
                            InvokeClose();
                            if (WasReleasedDuringCallback())
                                return true;
                            _schema?.Refresh();
                            _surfaceKind = PcCompatManagedSettingsSurfaceKind.None;
                            _openStartedTimestamp = 0;
                            _state = PcCompatManagedSettingsState.Closed;
                            return true;
                        }
                        _openStartedTimestamp = 0;
                        _state = PcCompatManagedSettingsState.Open;
                    }
                    else if (!skippedImGui &&
                             _openStartedTimestamp != 0 &&
                             Stopwatch.GetTimestamp() - _openStartedTimestamp >=
                             ImGuiOpenTimeoutTicks)
                    {
                        throw new InvalidOperationException(
                            "original MOD IMGUI settings did not receive a valid OnGUI dispatch");
                    }
                }

                if (_saveRequested)
                {
                    _saveRequested = false;
                    _save();
                    if (WasReleasedDuringCallback())
                        return true;
                    _schema?.Refresh();
                }

                if (_closeRequested)
                {
                    _closeRequested = false;
                    InvokeClose();
                    if (WasReleasedDuringCallback())
                        return true;
                    _schema?.Refresh();
                    _unityBackend?.ReleaseCanvasSurface();
                    _surfaceKind = PcCompatManagedSettingsSurfaceKind.None;
                    _openStartedTimestamp = 0;
                    _state = PcCompatManagedSettingsState.Closed;
                    return true;
                }

                if (_state == PcCompatManagedSettingsState.Open)
                {
                    if (canDispatchImGui &&
                        _surfaceKind == PcCompatManagedSettingsSurfaceKind.UnityImGui &&
                        !drewImGui)
                    {
                        _draw();
                        if (WasReleasedDuringCallback())
                            return true;
                    }
                    var visible = _surfaceKind == PcCompatManagedSettingsSurfaceKind.UnityCanvas
                        ? _unityBackend?.IsClaimedCanvasSurfaceVisible() == true
                        : _isVisible();
                    if (WasReleasedDuringCallback())
                        return true;
                    if (!visible)
                    {
                        InvokeClose();
                        if (WasReleasedDuringCallback())
                            return true;
                        _schema?.Refresh();
                        _unityBackend?.ReleaseCanvasSurface();
                        _surfaceKind = PcCompatManagedSettingsSurfaceKind.None;
                        _openStartedTimestamp = 0;
                        _state = PcCompatManagedSettingsState.Closed;
                    }
                }
                if (_recoverableLayoutFailureCount != 0 &&
                    (_unityBackend is not IPcCompatManagedSettingsImGuiTransaction transaction ||
                     transaction.IsStable))
                {
                    _recoverableLayoutFailureCount = 0;
                }
                return true;
            }
            catch (Exception exception)
            {
                // Explicit session teardown wins over an exception thrown by a
                // callback that was already being unwound. A retired surface
                // must not become Faulted and be mistaken for a live MOD fault.
                if (WasReleasedDuringCallback())
                    return true;
                var failure = exception.GetBaseException().ToString();
                if (IsRecoverableGUILayoutRepaintMismatch(exception))
                {
                    if (_unityBackend is IPcCompatManagedSettingsImGuiTransaction transaction)
                        transaction.MarkRecoverableLayoutFailure();
                    if (++_recoverableLayoutFailureCount < RecoverableLayoutFailureLimit)
                    {
                        _openRequested = false;
                        _saveRequested = false;
                        _closeRequested = false;
                        _fault = null;
                        return true;
                    }
                }
                try
                {
                    InvokeClose();
                    if (WasReleasedDuringCallback())
                        return true;
                    _schema?.Refresh();
                }
                catch (Exception closeException)
                {
                    failure += Environment.NewLine +
                               "Settings close after failure also failed:" +
                               Environment.NewLine + closeException.GetBaseException();
                }

                _openRequested = false;
                _saveRequested = false;
                _closeRequested = false;
                _fault = failure;
                _unityBackend?.ReleaseCanvasSurface();
                _surfaceKind = PcCompatManagedSettingsSurfaceKind.None;
                _openStartedTimestamp = 0;
                _state = PcCompatManagedSettingsState.Faulted;
                return false;
            }
        }
    }

    private bool WasReleasedDuringCallback()
        => _state is PcCompatManagedSettingsState.Closed or
           PcCompatManagedSettingsState.Unavailable;

    private void InvokeClose()
    {
        if (_closeCallbackActive)
            return;

        _closeCallbackActive = true;
        try
        {
            _close();
        }
        finally
        {
            _closeCallbackActive = false;
        }
    }

    private static bool IsRecoverableGUILayoutRepaintMismatch(Exception exception)
    {
        var text = exception.ToString();
        return text.Contains("GUILayout: Mismatched LayoutGroup.Ignore", StringComparison.Ordinal) ||
               text.Contains("Getting control ", StringComparison.Ordinal) &&
               text.Contains("position in a group with only ", StringComparison.Ordinal) &&
               text.Contains("when doing repaint", StringComparison.OrdinalIgnoreCase);
    }

    private bool CanDispatchImGuiCurrentEvent(out bool skipped)
    {
        skipped = false;
        if (_unityBackend is not IPcCompatManagedSettingsImGuiTransaction transaction)
            return true;

        var allowed = transaction.ShouldDispatchCurrentEvent();
        skipped = !allowed;
        return allowed;
    }

    public PcCompatManagedSettingsSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new PcCompatManagedSettingsSnapshot
            {
                State = _state,
                Fault = _fault,
                Supported = true,
                SurfaceKind = _surfaceKind,
                PresentationDiagnostics =
                    (_unityBackend as PcCompatManagedSettingsUnityBackend)?.GetDiagnostics()
            };
        }
    }

    public PcCompatManagedSettingsSchemaSnapshot SnapshotSchema()
        => _schema?.Snapshot() ?? new PcCompatManagedSettingsSchemaSnapshot
        {
            Error = "verified MOD settings schema is unavailable"
        };

    public bool RequestSchemaValue(
        string revision,
        string path,
        string value,
        out string? error)
        => _schema?.RequestValue(revision, path, value, out error) ??
           FailSchemaRequest(out error);

    public void RequestSchemaSaveRetry()
        => _schema?.RequestRetrySave();

    private static bool FailSchemaRequest(out string? error)
    {
        error = "verified MOD settings schema is unavailable";
        return false;
    }

    private static bool TryBind(
        object target,
        out SettingsSurface? surface,
        out string? error)
    {
        var type = target.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        var open = type.GetMethod("CompatOpenGUI", flags, Type.EmptyTypes);
        var draw = type.GetMethod("CompatOnGUI", flags, Type.EmptyTypes);
        var save = type.GetMethod("CompatSaveGUI", flags, Type.EmptyTypes);
        var close = type.GetMethod("CompatCloseGUI", flags, Type.EmptyTypes);
        var visible = type.GetProperty("CompatSettingsVisible", flags)?.GetMethod;
        if (open == null || draw == null || save == null || close == null || visible == null ||
            open.ReturnType != typeof(void) || draw.ReturnType != typeof(void) ||
            save.ReturnType != typeof(void) || close.ReturnType != typeof(void) ||
            visible.ReturnType != typeof(bool))
        {
            surface = null;
            error = $"type '{type.FullName}' does not expose the required " +
                    "CompatOpenGUI/CompatOnGUI/CompatSaveGUI/CompatCloseGUI/" +
                    "CompatSettingsVisible surface";
            return false;
        }

        surface = new SettingsSurface(
            open.CreateDelegate<Action>(target),
            draw.CreateDelegate<Action>(target),
            save.CreateDelegate<Action>(target),
            close.CreateDelegate<Action>(target),
            visible.CreateDelegate<Func<bool>>(target));
        error = null;
        return true;
    }

    private sealed record SettingsSurface(
        Action Open,
        Action Draw,
        Action Save,
        Action Close,
        Func<bool> IsVisible);
}

using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using StArray.ModManager.Manager;

namespace Xphorror.PcModCompat;

public sealed class PcCompatManagedSettingsUnityBackend : IPcCompatManagedSettingsCanvasProbe
{
    private const string DiagnosticTag = "PcCompatSettingsDiag";
    private const string DiagnosticPrefix = "[DEBUG-settings-surface-v1]";
    private const int DiagnosticFrameBudget = 24;
    private const int DiagnosticSampleLimit = 8;
    private readonly Type _guiContentType;
    private readonly ConstructorInfo _guiContentConstructor;
    private readonly Type _rectType;
    private readonly Type _vector2Type;
    private readonly Type _vector3Type;
    private readonly Type _quaternionType;
    private readonly MethodInfo _screenWidth;
    private readonly MethodInfo _screenHeight;
    private readonly MethodInfo _screenDpi;
    private readonly MethodInfo _guiSkin;
    private readonly MethodInfo _skinGetFont;
    private readonly MethodInfo _skinSetFont;
    private readonly MethodInfo _skinLabel;
    private readonly MethodInfo _skinTextField;
    private readonly MethodInfo _skinTextArea;
    private readonly MethodInfo _skinButton;
    private readonly MethodInfo _skinToggle;
    private readonly MethodInfo _skinHorizontalSlider;
    private readonly MethodInfo _skinHorizontalSliderThumb;
    private readonly MethodInfo _styleGetFontSize;
    private readonly MethodInfo _styleSetFontSize;
    private readonly MethodInfo _styleGetFont;
    private readonly MethodInfo _styleSetFont;
    private readonly MethodInfo _styleGetWordWrap;
    private readonly MethodInfo _styleSetWordWrap;
    private readonly MethodInfo _styleGetFixedHeight;
    private readonly MethodInfo _styleGetRichText;
    private readonly MethodInfo _styleGetPadding;
    private readonly MethodInfo _styleGetNormal;
    private readonly MethodInfo _styleStateGetTextColor;
    private readonly MethodInfo[] _paddingGetters;
    private readonly MethodInfo[] _paddingSetters;
    private readonly MethodInfo _guiBox;
    private readonly MethodInfo _guiLabelRect;
    private readonly MethodInfo _guiButtonRect;
    private readonly MethodInfo _guiTextField;
    private readonly MethodInfo _guiSlider;
    private readonly MethodInfo _guiToggleContent;
    private readonly MethodInfo _beginArea;
    private readonly MethodInfo _endArea;
    private readonly MethodInfo _beginVertical;
    private readonly MethodInfo _endVertical;
    private readonly MethodInfo _beginScrollView;
    private readonly MethodInfo _endScrollView;
    private readonly MethodInfo _beginHorizontal;
    private readonly MethodInfo _endHorizontal;
    private readonly MethodInfo _flexibleSpace;
    private readonly MethodInfo _space;
    private readonly MethodInfo _label;
    private readonly MethodInfo _button;
    private readonly MethodInfo _toggle;
    private readonly MethodInfo _toggleStyled;
    private readonly MethodInfo _labelStyled;
    private readonly MethodInfo _getRect;
    private readonly MethodInfo _getLastRect;
    private readonly MethodInfo _getControlId;
    private readonly MethodInfo _rdStringSetup;
    private readonly MethodInfo _rdStringFontData;
    private readonly MethodInfo _rdStringLanguage;
    private readonly MethodInfo _fontDataFont;
    private readonly MethodInfo _fontDataScale;
    private readonly MethodInfo _eventCurrent;
    private readonly MethodInfo _eventType;
    private readonly MethodInfo _eventRawType;
    private readonly MethodInfo _guiHotControl;
    private readonly MethodInfo _guiKeyboardControl;
    private readonly MethodInfo _guiSetHotControl;
    private readonly MethodInfo _guiSetKeyboardControl;
    private readonly MethodInfo _guiGetColor;
    private readonly MethodInfo _guiGetBackgroundColor;
    private readonly MethodInfo _guiGetContentColor;
    private readonly MethodInfo _guiGetEnabled;
    private readonly MethodInfo _guiGetMatrix;
    private readonly MethodInfo _guiSetMatrix;
    private readonly MethodInfo _matrixTrs;
    private readonly MethodInfo _matrixMultiply;
    private readonly object _passiveFocus;
    private readonly string? _modId;
    private readonly long _resourceSessionGeneration;
    private Type? _canvasType;
    private MethodInfo? _findCanvases;
    private object? _findObjectsSortModeNone;
    private MethodInfo? _canvasGetGameObject;
    private MethodInfo? _canvasGetEnabled;
    private MethodInfo? _gameObjectGetActive;
    private MethodInfo? _gameObjectGetTransform;
    private MethodInfo? _transformGetParent;
    private MethodInfo? _objectGetInstanceId;
    private readonly HashSet<int> _canvasBaseline = [];
    private readonly HashSet<int> _canvasOwnerIds = [];
    private readonly HashSet<int> _claimedCanvasIds = [];
    private readonly Dictionary<Type, object> _emptyOptionsByType = new();
    private readonly Dictionary<string, object> _guiContentByText = new(StringComparer.Ordinal);
    private object? _scrollPosition;
    private readonly List<StyleSnapshot> _mobileStyleSnapshots = [];
    private object? _mobileSkin;
    private object? _mobileSkinFont;
    private object? _previousGuiMatrix;
    private (float Dimension, float Font, float TouchHeight) _previousImGuiScale;
    private bool _mobileGuiMatrixActive;
    private bool _mobileImGuiScaleActive;
    private bool _fontResolutionFailureLogged;
    private bool _fontResolutionSuccessLogged;
    private bool _resourceFontResolutionAttempted;
    private object? _resourceFont;
    private string _fontSource = "RDString";
    private string _diagnostics = "frame=not-rendered";
    private string _gameLanguage = "English";
    private float _contentWidth;
    private float _touchHeight;
    private bool _stackControlRows;
    private int _pendingAction;
    private bool _frameOpen;
    private bool _legacyInputFrameOpen;
    private bool _areaOpen;
    private bool _verticalOpen;
    private bool _scrollOpen;
    private bool _sectionBodyHorizontalOpen;
    private bool _sectionBodyVerticalOpen;
    private readonly List<string> _diagnosticSamples = new(DiagnosticSampleLimit);
    private readonly List<string> _diagnosticRects = new(DiagnosticSampleLimit);
    private readonly List<string> _lastDiagnosticRepaintRects = new(DiagnosticSampleLimit);
    private long _diagnosticSession;
    private long _diagnosticFrame;
    private int _diagnosticBudget;
    private bool _captureDiagnosticRects;
    private bool _captureNextDiagnosticRepaint = true;
    private string _diagnosticStructure = string.Empty;
    private string _diagnosticTitle = string.Empty;
    private string _diagnosticEventAtBegin = "event=not-captured";
    private string _diagnosticLastOperation = "none";
    private string _diagnosticMetrics = "metrics=not-captured";
    private string _diagnosticStyles = "styles=not-captured";
    private int _diagnosticLabels;
    private int _diagnosticButtons;
    private int _diagnosticToggles;
    private int _diagnosticTextFields;
    private int _diagnosticNumbers;
    private int _diagnosticEnums;
    private int _diagnosticSections;

    private PcCompatManagedSettingsUnityBackend(
        AssemblyLoadContext loadContext,
        string? modId,
        long resourceSessionGeneration)
    {
        _modId = string.IsNullOrWhiteSpace(modId) ? null : modId;
        _resourceSessionGeneration = resourceSessionGeneration;
        var core = loadContext.LoadFromAssemblyName(new AssemblyName("UnityEngine.CoreModule"));
        var imgui = loadContext.LoadFromAssemblyName(new AssemblyName("UnityEngine.IMGUIModule"));
        var textRendering = loadContext.LoadFromAssemblyName(
            new AssemblyName("UnityEngine.TextRenderingModule"));
        var game = loadContext.LoadFromAssemblyName(new AssemblyName("Assembly-CSharp"));
        _rectType = RequireType(core, "UnityEngine.Rect");
        _vector2Type = RequireType(core, "UnityEngine.Vector2");
        _vector3Type = RequireType(core, "UnityEngine.Vector3");
        _quaternionType = RequireType(core, "UnityEngine.Quaternion");
        var font = RequireType(textRendering, "UnityEngine.Font");
        var screen = RequireType(core, "UnityEngine.Screen");
        var gui = RequireType(imgui, "UnityEngine.GUI");
        _guiContentType = RequireType(imgui, "UnityEngine.GUIContent");
        _guiContentConstructor = _guiContentType.GetConstructor([typeof(string)])
            ?? throw new MissingMethodException(_guiContentType.FullName, ".ctor(String)");
        var guiSkin = RequireType(imgui, "UnityEngine.GUISkin");
        var guiStyle = RequireType(imgui, "UnityEngine.GUIStyle");
        var layout = RequireType(imgui, "UnityEngine.GUILayout");
        var layoutUtility = RequireType(imgui, "UnityEngine.GUILayoutUtility");
        var guiUtility = RequireType(imgui, "UnityEngine.GUIUtility");
        var unityEvent = RequireType(imgui, "UnityEngine.Event");
        var eventType = RequireType(imgui, "UnityEngine.EventType");
        var focusType = RequireType(imgui, "UnityEngine.FocusType");
        var color = RequireType(core, "UnityEngine.Color");
        var matrix = RequireType(core, "UnityEngine.Matrix4x4");

        TryBindCanvasProbe(core);

        _screenWidth = RequireGetter(screen, "width", typeof(int), isStatic: true);
        _screenHeight = RequireGetter(screen, "height", typeof(int), isStatic: true);
        _screenDpi = RequireGetter(screen, "dpi", typeof(float), isStatic: true);
        _guiSkin = RequireGetter(gui, "skin", guiSkin, isStatic: true);
        _skinGetFont = RequireGetter(guiSkin, "font", font, isStatic: false);
        _skinSetFont = RequireSetter(guiSkin, "font", font, isStatic: false);
        _skinLabel = RequireGetter(guiSkin, "label", guiStyle, isStatic: false);
        _skinTextField = RequireGetter(guiSkin, "textField", guiStyle, isStatic: false);
        _skinTextArea = RequireGetter(guiSkin, "textArea", guiStyle, isStatic: false);
        _skinButton = RequireGetter(guiSkin, "button", guiStyle, isStatic: false);
        _skinToggle = RequireGetter(guiSkin, "toggle", guiStyle, isStatic: false);
        _skinHorizontalSlider = RequireGetter(
            guiSkin,
            "horizontalSlider",
            guiStyle,
            isStatic: false);
        _skinHorizontalSliderThumb = RequireGetter(
            guiSkin,
            "horizontalSliderThumb",
            guiStyle,
            isStatic: false);
        _styleGetFontSize = RequireGetter(guiStyle, "fontSize", typeof(int), isStatic: false);
        _styleSetFontSize = RequireSetter(guiStyle, "fontSize", typeof(int), isStatic: false);
        _styleGetFont = RequireGetter(guiStyle, "font", font, isStatic: false);
        _styleSetFont = RequireSetter(guiStyle, "font", font, isStatic: false);
        _styleGetWordWrap = RequireGetter(guiStyle, "wordWrap", typeof(bool), isStatic: false);
        _styleSetWordWrap = RequireSetter(guiStyle, "wordWrap", typeof(bool), isStatic: false);
        _styleGetFixedHeight = RequireGetter(guiStyle, "fixedHeight", typeof(float), isStatic: false);
        _styleGetRichText = RequireGetter(guiStyle, "richText", typeof(bool), isStatic: false);
        var rectOffset = RequireType(core, "UnityEngine.RectOffset");
        _styleGetPadding = RequireGetter(guiStyle, "padding", rectOffset, isStatic: false);
        var guiStyleState = RequireType(imgui, "UnityEngine.GUIStyleState");
        _styleGetNormal = RequireGetter(guiStyle, "normal", guiStyleState, isStatic: false);
        _styleStateGetTextColor = RequireGetter(
            guiStyleState,
            "textColor",
            color,
            isStatic: false);
        var edges = new[] { "left", "right", "top", "bottom" };
        _paddingGetters = edges.Select(edge =>
                RequireGetter(rectOffset, edge, typeof(int), isStatic: false))
            .ToArray();
        _paddingSetters = edges.Select(edge =>
                RequireSetter(rectOffset, edge, typeof(int), isStatic: false))
            .ToArray();
        _guiBox = RequireMethod(gui, "Box", typeof(void), _rectType, typeof(string));
        _guiLabelRect = RequireMethod(gui, "Label", typeof(void), _rectType, typeof(string));
        _guiButtonRect = RequireMethod(gui, "Button", typeof(bool), _rectType, typeof(string));
        _guiTextField = RequireMethod(
            gui,
            "TextField",
            typeof(string),
            _rectType,
            typeof(string),
            typeof(int));
        _guiSlider = RequireMethod(
            gui,
            "Slider",
            typeof(float),
            _rectType,
            typeof(float),
            typeof(float),
            typeof(float),
            typeof(float),
            guiStyle,
            guiStyle,
            typeof(bool),
            typeof(int),
            guiStyle);
        _guiToggleContent = RequireMethod(
            gui,
            "Toggle",
            typeof(bool),
            _rectType,
            typeof(bool),
            _guiContentType,
            guiStyle);
        _beginArea = RequireMethod(layout, "BeginArea", typeof(void), _rectType);
        _endArea = RequireMethod(layout, "EndArea", typeof(void));
        _beginVertical = RequireOptionsMethod(layout, "BeginVertical", typeof(void));
        _endVertical = RequireMethod(layout, "EndVertical", typeof(void));
        _beginScrollView = RequireOptionsMethod(
            layout,
            "BeginScrollView",
            _vector2Type,
            _vector2Type);
        _endScrollView = RequireMethod(layout, "EndScrollView", typeof(void));
        _beginHorizontal = RequireOptionsMethod(layout, "BeginHorizontal", typeof(void));
        _endHorizontal = RequireMethod(layout, "EndHorizontal", typeof(void));
        _flexibleSpace = RequireMethod(layout, "FlexibleSpace", typeof(void));
        _space = RequireMethod(layout, "Space", typeof(void), typeof(float));
        _label = RequireOptionsMethod(layout, "Label", typeof(void), typeof(string));
        _button = RequireOptionsMethod(layout, "Button", typeof(bool), typeof(string));
        _toggle = RequireOptionsMethod(
            layout,
            "Toggle",
            typeof(bool),
            typeof(bool),
            typeof(string));
        _toggleStyled = RequireOptionsMethod(
            layout,
            "Toggle",
            typeof(bool),
            typeof(bool),
            typeof(string),
            guiStyle);
        _labelStyled = RequireOptionsMethod(
            layout,
            "Label",
            typeof(void),
            typeof(string),
            guiStyle);
        _getRect = RequireOptionsMethod(
            layoutUtility,
            "GetRect",
            _rectType,
            typeof(float),
            typeof(float),
            guiStyle);
        _getLastRect = RequireMethod(layoutUtility, "GetLastRect", _rectType);
        _getControlId = RequireMethod(
            guiUtility,
            "GetControlID",
            typeof(int),
            focusType,
            _rectType);
        _passiveFocus = System.Enum.ToObject(focusType, 2);

        var rdString = RequireType(game, "RDString");
        var fontData = RequireType(game, "FontData");
        var systemLanguage = RequireType(core, "UnityEngine.SystemLanguage");
        _rdStringSetup = RequireMethod(rdString, "Setup", typeof(void));
        _rdStringFontData = RequireGetter(rdString, "fontData", fontData, isStatic: true);
        _rdStringLanguage = RequireGetter(
            rdString,
            "language",
            systemLanguage,
            isStatic: true);
        _fontDataFont = RequireGetter(fontData, "font", font, isStatic: false);
        _fontDataScale = RequireGetter(fontData, "fontScale", typeof(float), isStatic: false);
        _eventCurrent = RequireGetter(unityEvent, "current", unityEvent, isStatic: true);
        _eventType = RequireGetter(unityEvent, "type", eventType, isStatic: false);
        _eventRawType = RequireGetter(unityEvent, "rawType", eventType, isStatic: false);
        _guiHotControl = RequireGetter(guiUtility, "hotControl", typeof(int), isStatic: true);
        _guiKeyboardControl = RequireGetter(
            guiUtility,
            "keyboardControl",
            typeof(int),
            isStatic: true);
        _guiSetHotControl = RequireSetter(
            guiUtility,
            "hotControl",
            typeof(int),
            isStatic: true);
        _guiSetKeyboardControl = RequireSetter(
            guiUtility,
            "keyboardControl",
            typeof(int),
            isStatic: true);
        _guiGetColor = RequireGetter(gui, "color", color, isStatic: true);
        _guiGetBackgroundColor = RequireGetter(gui, "backgroundColor", color, isStatic: true);
        _guiGetContentColor = RequireGetter(gui, "contentColor", color, isStatic: true);
        _guiGetEnabled = RequireGetter(gui, "enabled", typeof(bool), isStatic: true);
        _guiGetMatrix = RequireGetter(gui, "matrix", matrix, isStatic: true);
        _guiSetMatrix = RequireSetter(gui, "matrix", matrix, isStatic: true);
        _matrixTrs = RequireMethod(
            matrix,
            "TRS",
            matrix,
            _vector3Type,
            _quaternionType,
            _vector3Type);
        _matrixMultiply = RequireMethod(matrix, "op_Multiply", matrix, matrix, matrix);
    }

    public bool SupportsCanvasProbe => _findCanvases != null;

    public void BeginCanvasProbe(IReadOnlyList<object> ownerGameObjects)
    {
        _diagnosticSession++;
        _diagnosticFrame = 0;
        _diagnosticBudget = DiagnosticFrameBudget;
        _canvasBaseline.Clear();
        _canvasOwnerIds.Clear();
        _claimedCanvasIds.Clear();
        _lastDiagnosticRepaintRects.Clear();
        _captureDiagnosticRects = false;
        _captureNextDiagnosticRepaint = true;
        _diagnosticStructure = string.Empty;
        if (!SupportsCanvasProbe)
            return;

        foreach (var canvas in SnapshotVisibleCanvases())
            _canvasBaseline.Add(GetInstanceId(canvas));
        foreach (var owner in ownerGameObjects)
        {
            try
            {
                _canvasOwnerIds.Add(GetInstanceId(owner));
            }
            catch
            {
                // A destroyed owner is not eligible to claim a settings Canvas.
            }
        }
        TraceDiagnostic(
            $"probe session={_diagnosticSession} baseline={_canvasBaseline.Count} " +
            $"owners={_canvasOwnerIds.Count} canvasProbe={SupportsCanvasProbe}",
            consumeBudget: false);
    }

    public bool TryClaimCanvasSurface()
    {
        if (!SupportsCanvasProbe)
            return false;

        foreach (var canvas in SnapshotVisibleCanvases())
        {
            var canvasId = GetInstanceId(canvas);
            var owner = _canvasGetGameObject!.Invoke(canvas, null);
            if (IsCanvasClaimCandidate(
                    wasVisibleBeforeOpen: _canvasBaseline.Contains(canvasId),
                    ownerSetKnown: _canvasOwnerIds.Count != 0,
                    ownerOrDescendant: owner != null && IsOwnerOrDescendant(owner)))
                _claimedCanvasIds.Add(canvasId);
        }
        return _claimedCanvasIds.Count != 0;
    }

    internal static bool IsCanvasClaimCandidate(
        bool wasVisibleBeforeOpen,
        bool ownerSetKnown,
        bool ownerOrDescendant)
        => !wasVisibleBeforeOpen && (!ownerSetKnown || ownerOrDescendant);

    public bool IsClaimedCanvasSurfaceVisible()
    {
        if (_claimedCanvasIds.Count == 0 || !SupportsCanvasProbe)
            return false;
        foreach (var canvas in SnapshotVisibleCanvases())
        {
            if (_claimedCanvasIds.Contains(GetInstanceId(canvas)))
                return true;
        }
        return false;
    }

    public void ReleaseCanvasSurface()
    {
        _canvasBaseline.Clear();
        _canvasOwnerIds.Clear();
        _claimedCanvasIds.Clear();
    }

    public static bool TryCreate(
        AssemblyLoadContext loadContext,
        out PcCompatManagedSettingsUnityBackend? backend,
        out string? error)
        => TryCreate(loadContext, modId: null, resourceSessionGeneration: 0, out backend, out error);

    public static bool TryCreate(
        AssemblyLoadContext loadContext,
        string? modId,
        long resourceSessionGeneration,
        out PcCompatManagedSettingsUnityBackend? backend,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(loadContext);
        try
        {
            backend = new PcCompatManagedSettingsUnityBackend(
                loadContext,
                modId,
                resourceSessionGeneration);
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            backend = null;
            error = exception.GetBaseException().ToString();
            return false;
        }
    }

    public void BeginFrame(string title)
    {
        if (_frameOpen)
            throw new InvalidOperationException("PcCompat settings frame re-entry was rejected.");
        ResetFrameDiagnostics(title);
        _diagnosticLastOperation = "EnsureRuntimeValues";
        EnsureRuntimeValues();
        _diagnosticLastOperation = "Screen.metrics";
        var width = Convert.ToInt32(_screenWidth.Invoke(null, null));
        var height = Convert.ToInt32(_screenHeight.Invoke(null, null));
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"invalid Unity screen size {width}x{height}");
        var dpi = Convert.ToSingle(_screenDpi.Invoke(null, null));
        var localizedFont = ResolveLocalizedFont();
        _gameLanguage = localizedFont.Language;
        var metrics = ComputeMobileMetrics(width, height, dpi, localizedFont.Scale);
        _diagnosticMetrics =
            $"screen={width}x{height} logical={metrics.LogicalWidth:0.##}x" +
            $"{metrics.LogicalHeight:0.##} dpi={dpi:0.##} scale={metrics.RenderScale:0.###} " +
            $"margin={metrics.Margin:0.##} panel={metrics.PanelWidth:0.##}x" +
            $"{metrics.LogicalHeight - metrics.Margin * 2f:0.##} " +
            $"touch={metrics.TouchHeight:0.##} font={metrics.FontSize}";
        _diagnostics =
            $"frame=rendered width={width} height={height} dpi={dpi:0.##} " +
            $"renderScale={metrics.RenderScale:0.###} " +
            $"language={localizedFont.Language} fontResolved={localizedFont.Font != null} " +
            $"fontSource={localizedFont.Source} " +
            $"fontScale={localizedFont.Scale:0.###} fontSize={metrics.FontSize} " +
            $"touchHeight={metrics.TouchHeight:0.##} panelWidth={metrics.PanelWidth:0.##}";
        if (!_fontResolutionSuccessLogged && localizedFont.Font != null)
        {
            _fontResolutionSuccessLogged = true;
            Console.WriteLine(
                "[PcModCompat][SettingsFont][INFO] " +
                $"language={localizedFont.Language} source={localizedFont.Source} dpi={dpi:0.##} " +
                $"renderScale={metrics.RenderScale:0.###} " +
                $"fontScale={localizedFont.Scale:0.###} fontSize={metrics.FontSize} " +
                $"touchHeight={metrics.TouchHeight:0.##} hasFont=true");
        }
        var margin = metrics.Margin;
        var panelWidth = metrics.PanelWidth;
        var panelHeight = metrics.LogicalHeight - margin * 2f;
        var panelX = (metrics.LogicalWidth - panelWidth) * 0.5f;
        _contentWidth = metrics.ContentWidth;
        _touchHeight = metrics.TouchHeight;
        _stackControlRows = metrics.ContentWidth < 520f;
        _pendingAction = 0;
        var panel = Activator.CreateInstance(
            _rectType,
            panelX,
            margin,
            panelWidth,
            panelHeight)
            ?? throw new InvalidOperationException("could not construct UnityEngine.Rect");

        try
        {
            _diagnosticLastOperation = "ApplyMobileSkin";
            ApplyMobileSkin(metrics, localizedFont.Font);
            _diagnosticLastOperation = "GUI.Box";
            _guiBox.Invoke(null, [panel, string.Empty]);
            _diagnosticLastOperation = "GUILayout.BeginArea";
            _beginArea.Invoke(null, [panel]);
            _areaOpen = true;
            _beginVertical.Invoke(null, [GetEmptyOptions(_beginVertical)]);
            _verticalOpen = true;
            _beginHorizontal.Invoke(null, [GetEmptyOptions(_beginHorizontal)]);
            try
            {
                _diagnosticLastOperation = "GUI.Label(header)";
                _diagnosticLabels++;
                AddDiagnosticSample(title);
                var headerSkin = _guiSkin.Invoke(null, null)
                    ?? throw new InvalidOperationException("Unity GUI.skin is unavailable");
                var labelStyle = _skinLabel.Invoke(headerSkin, null)
                    ?? throw new InvalidOperationException("Unity GUI.skin.label is unavailable");
                var headerTitleWidth = Math.Max(120f, _contentWidth - _touchHeight - 8f);
                var titleRect = _getRect.Invoke(
                    null,
                    [headerTitleWidth, _touchHeight, labelStyle, GetEmptyOptions(_getRect)])
                    ?? throw new InvalidOperationException("GUILayoutUtility.GetRect returned null");
                _guiLabelRect.Invoke(
                    null,
                    [InsetHeaderTitleRect(titleRect, _touchHeight), title]);
                CaptureRect("header-title", title, titleRect);

                _space.Invoke(null, [8f]);
                _diagnosticLastOperation = "GUI.Button(header-close)";
                _diagnosticButtons++;
                AddDiagnosticSample("X");
                var buttonStyle = _skinButton.Invoke(headerSkin, null)
                    ?? throw new InvalidOperationException("Unity GUI.skin.button is unavailable");
                var closeRect = _getRect.Invoke(
                    null,
                    [_touchHeight, _touchHeight, buttonStyle, GetEmptyOptions(_getRect)])
                    ?? throw new InvalidOperationException("GUILayoutUtility.GetRect returned null");
                var close = _guiButtonRect.Invoke(null, [closeRect, "X"]) is true;
                CaptureRect("header-close", "X", closeRect);
                if (close)
                    _pendingAction |= 2;
            }
            finally
            {
                _endHorizontal.Invoke(null, null);
            }
            _space.Invoke(null, [Math.Max(8f, _touchHeight * 0.2f)]);
            _scrollPosition = _beginScrollView.Invoke(
                null,
                [_scrollPosition!, GetEmptyOptions(_beginScrollView)])
                ?? _scrollPosition;
            _scrollOpen = true;
            PcCompatLegacyInputBridge.BeginSettingsGuiFrame();
            _legacyInputFrameOpen = true;
            _frameOpen = true;
            _diagnosticLastOperation = "content";
        }
        catch (Exception exception)
        {
            TraceFrameDiagnostic("begin-fault", 0, exception);
            CloseFrameBestEffort();
            throw;
        }
    }

    public int EndFrame()
    {
        if (!_frameOpen)
            return 0;
        var action = _pendingAction;
        try
        {
            Exception? contentFailure = null;
            CloseContentLayout(ref contentFailure);
            if (contentFailure != null)
                throw contentFailure;
            _space.Invoke(null, [Math.Max(10f, _touchHeight * 0.2f)]);
            _beginHorizontal.Invoke(null, [GetEmptyOptions(_beginHorizontal)]);
            try
            {
                _flexibleSpace.Invoke(null, null);
                if (Button(IsChinese() ? "保存" : "Save"))
                    action |= 1;
                if (Button(IsChinese() ? "关闭" : "Close"))
                    action |= 2;
            }
            finally
            {
                _endHorizontal.Invoke(null, null);
            }
            _diagnosticLastOperation = "EndFrame.complete";
            TraceFrameDiagnostic("complete", action, null);
            return action;
        }
        catch (Exception exception)
        {
            TraceFrameDiagnostic("end-fault", action, exception);
            CloseFrameBestEffort();
            throw;
        }
        finally
        {
            if (_frameOpen)
                CloseFrame();
        }
    }

    public void AbortFrame()
    {
        if (!_frameOpen)
            return;
        _diagnosticLastOperation = "AbortFrame";
        try
        {
            TraceFrameDiagnostic("aborted", 0, null);
        }
        finally
        {
            if (_frameOpen)
                CloseFrame();
        }
    }

    public bool CanApplyStructureChanges()
    {
        try
        {
            var current = _eventCurrent.Invoke(null, null);
            var eventType = current == null ? null : _eventType.Invoke(current, null)?.ToString();
            return string.Equals(eventType, "Layout", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void ReleaseInputFocus()
    {
        _guiSetKeyboardControl.Invoke(null, [0]);
        _guiSetHotControl.Invoke(null, [0]);
    }

    public bool Toggle(bool value, string label)
    {
        _diagnosticLastOperation = "GUILayout.Toggle";
        _diagnosticToggles++;
        AddDiagnosticSample(label);
        var result = _toggle.Invoke(null, [value, label, GetEmptyOptions(_toggle)]) is bool next
            ? next
            : value;
        CaptureLastRect("toggle", label);
        return result;
    }

    public string Text(string value, string label)
    {
        _diagnosticLastOperation = "Text";
        if (_stackControlRows)
        {
            _beginVertical.Invoke(null, [GetEmptyOptions(_beginVertical)]);
            try
            {
                if (!string.IsNullOrWhiteSpace(label))
                    Label(label);
                return DrawInlineTextField(value, Math.Max(180f, _contentWidth - 12f));
            }
            finally
            {
                _endVertical.Invoke(null, null);
            }
        }
        _beginHorizontal.Invoke(null, [GetEmptyOptions(_beginHorizontal)]);
        try
        {
            if (!string.IsNullOrWhiteSpace(label))
            {
                Label(label);
                _space.Invoke(null, [4f]);
            }
            var result = DrawInlineTextField(
                value,
                Math.Clamp(_contentWidth * 0.62f, 180f, 360f));
            _flexibleSpace.Invoke(null, null);
            return result;
        }
        finally
        {
            _endHorizontal.Invoke(null, null);
        }
    }

    public string Number(
        string value,
        string label,
        double min,
        double max,
        double step,
        bool integral)
    {
        _ = step;
        return DrawNumber(value, label, min, max, integral, slider: false);
    }

    public string SliderNumber(
        string value,
        string label,
        double min,
        double max,
        bool integral)
        => DrawNumber(value, label, min, max, integral, slider: true);

    public string Enum(string value, string label, string[] values)
    {
        _diagnosticLastOperation = "Enum";
        _diagnosticEnums++;
        if (values == null || values.Length == 0)
            return value;
        var index = Array.IndexOf(values, value);
        if (index < 0)
            index = 0;
        if (!string.IsNullOrWhiteSpace(label))
            Label(label);
        var skin = _guiSkin.Invoke(null, null)
            ?? throw new InvalidOperationException("Unity GUI.skin is unavailable");
        var buttonStyle = _skinButton.Invoke(skin, null)
            ?? throw new InvalidOperationException("Unity GUI.skin.button is unavailable");
        var supportsRichText = _styleGetRichText.Invoke(buttonStyle, null) is true;
        return DrawEnumRows(values, index, supportsRichText);
    }

    private string DrawEnumRows(string[] values, int selectedIndex, bool supportsRichText)
    {
        var choicesPerRow = _contentWidth >= 650f ? 4 : _contentWidth >= 480f ? 3 : 2;
        for (var rowStart = 0; rowStart < values.Length; rowStart += choicesPerRow)
        {
            _beginHorizontal.Invoke(null, [GetEmptyOptions(_beginHorizontal)]);
            try
            {
                var rowEnd = Math.Min(values.Length, rowStart + choicesPerRow);
                for (var candidateIndex = rowStart; candidateIndex < rowEnd; candidateIndex++)
                {
                    var candidate = values[candidateIndex];
                    var selected = candidateIndex == selectedIndex;
                    var buttonLabel = selected
                        ? supportsRichText ? $"<b>{candidate}</b>" : $"[{candidate}]"
                        : candidate;
                    if (Button(buttonLabel))
                        selectedIndex = candidateIndex;
                }
                _flexibleSpace.Invoke(null, null);
            }
            finally
            {
                _endHorizontal.Invoke(null, null);
            }
        }
        return values[selectedIndex];
    }

    public int Section(
        bool enabled,
        bool expanded,
        bool canEnable,
        bool canExpand,
        string label)
    {
        _diagnosticLastOperation = "Section";
        _diagnosticSections++;
        AddDiagnosticSample(label);
        var skin = _guiSkin.Invoke(null, null)
            ?? throw new InvalidOperationException("Unity GUI.skin is unavailable");
        var labelStyle = _skinLabel.Invoke(skin, null)
            ?? throw new InvalidOperationException("Unity GUI.skin.label is unavailable");
        _beginHorizontal.Invoke(null, [GetEmptyOptions(_beginHorizontal)]);
        try
        {
            _diagnosticToggles++;
            var expandLabel = enabled && canExpand
                ? expanded ? "◢" : "▶"
                : string.Empty;
            var expandRect = _getRect.Invoke(
                null,
                [_touchHeight,
                 _touchHeight,
                 labelStyle,
                 GetEmptyOptions(_getRect)])
                ?? throw new InvalidOperationException("GUILayoutUtility.GetRect returned null");
            expanded = _guiToggleContent.Invoke(
                null,
                [expandRect, expanded, GetGuiContent(expandLabel), labelStyle]) is bool nextExpanded
                ? nextExpanded
                : expanded;
            CaptureRect("section-arrow", expandLabel, expandRect);
            if (canEnable)
                enabled = Toggle(enabled, label);
            else
            {
                _space.Invoke(null, [15f]);
                _diagnosticLabels++;
                _labelStyled.Invoke(
                    null,
                    [label, labelStyle, GetEmptyOptions(_labelStyled)]);
                CaptureLastRect("label", label);
            }
            _flexibleSpace.Invoke(null, null);
        }
        finally
        {
            _endHorizontal.Invoke(null, null);
        }
        return (enabled ? 1 : 0) | (expanded ? 2 : 0);
    }

    public void BeginSectionBody()
    {
        if (_sectionBodyHorizontalOpen || _sectionBodyVerticalOpen)
            throw new InvalidOperationException("PcCompat settings section body re-entry was rejected.");
        _diagnosticLastOperation = "Section.BeginBody";
        _beginHorizontal.Invoke(null, [GetEmptyOptions(_beginHorizontal)]);
        _sectionBodyHorizontalOpen = true;
        _space.Invoke(null, [24f]);
        _beginVertical.Invoke(null, [GetEmptyOptions(_beginVertical)]);
        _sectionBodyVerticalOpen = true;
    }

    public void EndSectionBody()
    {
        _diagnosticLastOperation = "Section.EndBody";
        var hadOpenLayout = _sectionBodyVerticalOpen || _sectionBodyHorizontalOpen;
        Exception? failure = null;
        CloseLayoutLevel(ref _sectionBodyVerticalOpen, _endVertical, ref failure);
        CloseLayoutLevel(ref _sectionBodyHorizontalOpen, _endHorizontal, ref failure);
        try
        {
            if (hadOpenLayout)
                _space.Invoke(null, [12f]);
        }
        catch (Exception exception)
        {
            failure ??= exception.GetBaseException();
        }
        if (failure != null)
            throw new InvalidOperationException("PcCompat settings section cleanup failed.", failure);
    }

    private string DrawNumber(
        string value,
        string label,
        double min,
        double max,
        bool integral,
        bool slider)
    {
        _diagnosticLastOperation = slider ? "SliderNumber" : "Number";
        _diagnosticNumbers++;
        if (slider)
            return DrawSliderNumber(value, label, min, max, integral);
        if (_stackControlRows)
        {
            _beginVertical.Invoke(null, [GetEmptyOptions(_beginVertical)]);
            try
            {
                if (!string.IsNullOrWhiteSpace(label))
                    Label(label);
                return DrawInlineTextField(value, Math.Max(160f, _contentWidth - 12f));
            }
            finally
            {
                _endVertical.Invoke(null, null);
            }
        }
        _beginHorizontal.Invoke(null, [GetEmptyOptions(_beginHorizontal)]);
        try
        {
            if (!string.IsNullOrWhiteSpace(label))
            {
                Label(label);
                _space.Invoke(null, [4f]);
            }
            value = DrawInlineTextField(value, 120f);
            _flexibleSpace.Invoke(null, null);
            return value;
        }
        finally
        {
            _endHorizontal.Invoke(null, null);
        }
    }

    private string DrawSliderNumber(
        string value,
        string label,
        double min,
        double max,
        bool integral)
    {
        _beginVertical.Invoke(null, [GetEmptyOptions(_beginVertical)]);
        try
        {
            if (!string.IsNullOrWhiteSpace(label))
                Label(label);
            _beginHorizontal.Invoke(null, [GetEmptyOptions(_beginHorizontal)]);
            try
            {
                if (double.IsFinite(min) && double.IsFinite(max) && max > min)
                {
                    var sliderValue = ComputeMobileSliderValue(value, min, max);
                    var next = DrawHorizontalSlider(
                        sliderValue,
                        min,
                        max,
                        ComputeMobileSliderWidth(_contentWidth));
                    if (!next.Equals(sliderValue))
                        value = FormatNumber(next, integral);
                    _space.Invoke(null, [4f]);
                }
                return DrawInlineTextField(value, 88f);
            }
            finally
            {
                _endHorizontal.Invoke(null, null);
            }
        }
        finally
        {
            _endVertical.Invoke(null, null);
        }
    }

    private string DrawInlineTextField(string value, float width)
    {
        _diagnosticLastOperation = "GUI.TextField";
        _diagnosticTextFields++;
        var skin = _guiSkin.Invoke(null, null)
            ?? throw new InvalidOperationException("Unity GUI.skin is unavailable");
        var style = _skinTextField.Invoke(skin, null)
            ?? throw new InvalidOperationException("Unity GUI.skin.textField is unavailable");
        var rect = _getRect.Invoke(
            null,
            [width, _touchHeight, style, GetEmptyOptions(_getRect)])
            ?? throw new InvalidOperationException("GUILayoutUtility.GetRect returned null");
        var result = _guiTextField.Invoke(null, [rect, value, 1024]) as string ?? value;
        CaptureRect("text", value, rect);
        return result;
    }

    private double DrawHorizontalSlider(
        double value,
        double min,
        double max,
        float? requestedWidth = null)
    {
        _diagnosticLastOperation = "GUI.Slider";
        var skin = _guiSkin.Invoke(null, null)
            ?? throw new InvalidOperationException("Unity GUI.skin is unavailable");
        var sliderStyle = _skinHorizontalSlider.Invoke(skin, null)
            ?? throw new InvalidOperationException("Unity GUI.skin.horizontalSlider is unavailable");
        var thumbStyle = _skinHorizontalSliderThumb.Invoke(skin, null)
            ?? throw new InvalidOperationException("Unity GUI.skin.horizontalSliderThumb is unavailable");
        var rect = _getRect.Invoke(
            null,
            [requestedWidth ?? ComputeMobileSliderWidth(_contentWidth),
             _touchHeight,
             sliderStyle,
             GetEmptyOptions(_getRect)])
            ?? throw new InvalidOperationException("GUILayoutUtility.GetRect returned null");
        var controlId = Convert.ToInt32(_getControlId.Invoke(null, [_passiveFocus, rect]));
        var result = _guiSlider.Invoke(
            null,
            [rect,
             Convert.ToSingle(value),
             0f,
             Convert.ToSingle(min),
             Convert.ToSingle(max),
             sliderStyle,
             thumbStyle,
             true,
             controlId,
             null]);
        CaptureRect("slider", value.ToString(System.Globalization.CultureInfo.InvariantCulture), rect);
        return result == null ? value : Convert.ToSingle(result);
    }

    public bool Button(string label)
    {
        _diagnosticLastOperation = "GUILayout.Button";
        _diagnosticButtons++;
        AddDiagnosticSample(label);
        var result = _button.Invoke(null, [label, GetEmptyOptions(_button)]) is true;
        CaptureLastRect("button", label);
        return result;
    }

    public void Label(string label)
    {
        _diagnosticLastOperation = "GUILayout.Label";
        _diagnosticLabels++;
        AddDiagnosticSample(label);
        _label.Invoke(null, [label, GetEmptyOptions(_label)]);
        CaptureLastRect("label", label);
    }

    public string GetDiagnostics() => _diagnostics;

    private void ResetFrameDiagnostics(string title)
    {
        _diagnosticFrame++;
        _diagnosticTitle = SanitizeDiagnosticText(title);
        _diagnosticEventAtBegin = CaptureImGuiContext();
        var isRepaint = _diagnosticEventAtBegin.Contains(
            "repaint",
            StringComparison.OrdinalIgnoreCase);
        _captureDiagnosticRects = isRepaint &&
                                  (_diagnosticBudget > 0 || _captureNextDiagnosticRepaint);
        if (_captureDiagnosticRects)
            _captureNextDiagnosticRepaint = false;
        _diagnosticLastOperation = "BeginFrame";
        _diagnosticLabels = 0;
        _diagnosticButtons = 0;
        _diagnosticToggles = 0;
        _diagnosticTextFields = 0;
        _diagnosticNumbers = 0;
        _diagnosticEnums = 0;
        _diagnosticSections = 0;
        _diagnosticSamples.Clear();
        _diagnosticRects.Clear();
    }

    private void TraceFrameDiagnostic(string outcome, int action, Exception? exception)
    {
        var eventAtEnd = CaptureImGuiContext();
        var structure =
            $"{_diagnosticSections}:{_diagnosticLabels}:{_diagnosticButtons}:" +
            $"{_diagnosticToggles}:{_diagnosticTextFields}:{_diagnosticNumbers}:" +
            $"{_diagnosticEnums}";
        if (!string.Equals(structure, _diagnosticStructure, StringComparison.Ordinal))
        {
            _diagnosticStructure = structure;
            if (!_captureDiagnosticRects)
                _captureNextDiagnosticRepaint = true;
        }
        if (_diagnosticRects.Count != 0)
        {
            _lastDiagnosticRepaintRects.Clear();
            _lastDiagnosticRepaintRects.AddRange(_diagnosticRects);
        }
        var samples = _diagnosticSamples.Count == 0
            ? "none"
            : string.Join('|', _diagnosticSamples);
        var rects = _diagnosticRects.Count != 0
            ? string.Join('|', _diagnosticRects)
            : _lastDiagnosticRepaintRects.Count != 0
                ? "lastRepaint:" + string.Join('|', _lastDiagnosticRepaintRects)
                : "none";
        var failure = exception == null
            ? "none"
            : $"{exception.GetBaseException().GetType().Name}:" +
              SanitizeDiagnosticText(exception.GetBaseException().Message);
        _diagnostics +=
            $" eventBegin=[{_diagnosticEventAtBegin}] eventEnd=[{eventAtEnd}] " +
            $"controls=section:{_diagnosticSections},label:{_diagnosticLabels}," +
            $"button:{_diagnosticButtons},toggle:{_diagnosticToggles}," +
            $"text:{_diagnosticTextFields},number:{_diagnosticNumbers},enum:{_diagnosticEnums} " +
            $"action={action} samples={samples} rects={rects} " +
            $"lastOperation={_diagnosticLastOperation} {_diagnosticMetrics} {_diagnosticStyles}";
        TraceDiagnostic(
            $"frame session={_diagnosticSession} sequence={_diagnosticFrame} " +
            $"outcome={outcome} title={_diagnosticTitle} action={action} " +
            $"begin=[{_diagnosticEventAtBegin}] end=[{eventAtEnd}] " +
            $"controls=section:{_diagnosticSections},label:{_diagnosticLabels}," +
            $"button:{_diagnosticButtons},toggle:{_diagnosticToggles}," +
            $"text:{_diagnosticTextFields},number:{_diagnosticNumbers},enum:{_diagnosticEnums} " +
            $"samples={samples} rects={rects} {_diagnosticMetrics} {_diagnosticStyles} " +
            $"last={_diagnosticLastOperation} failure={failure}",
            consumeBudget: true);
    }

    private string CaptureImGuiContext()
    {
        try
        {
            var current = _eventCurrent.Invoke(null, null);
            var hot = Convert.ToInt32(_guiHotControl.Invoke(null, null));
            var keyboard = Convert.ToInt32(_guiKeyboardControl.Invoke(null, null));
            if (current == null)
                return $"event=null hot={hot} keyboard={keyboard} tid={Environment.CurrentManagedThreadId}";
            var type = _eventType.Invoke(current, null)?.ToString() ?? "<null>";
            var rawType = _eventRawType.Invoke(current, null)?.ToString() ?? "<null>";
            var color = FormatColor(_guiGetColor.Invoke(null, null));
            var background = FormatColor(_guiGetBackgroundColor.Invoke(null, null));
            var content = FormatColor(_guiGetContentColor.Invoke(null, null));
            var enabled = _guiGetEnabled.Invoke(null, null) is true;
            var matrix = FormatMatrix(_guiGetMatrix.Invoke(null, null));
            return $"event={type} raw={rawType} hot={hot} keyboard={keyboard} " +
                   $"enabled={enabled} color={color} background={background} " +
                   $"content={content} matrix={matrix} " +
                   $"tid={Environment.CurrentManagedThreadId}";
        }
        catch (Exception exception)
        {
            return "event=capture-failed " + exception.GetBaseException().GetType().Name + ':' +
                   SanitizeDiagnosticText(exception.GetBaseException().Message);
        }
    }

    private void AddDiagnosticSample(string? value)
    {
        if (_diagnosticSamples.Count >= DiagnosticSampleLimit || string.IsNullOrWhiteSpace(value))
            return;
        var sample = SanitizeDiagnosticText(value);
        if (!_diagnosticSamples.Contains(sample, StringComparer.Ordinal))
            _diagnosticSamples.Add(sample);
    }

    private void CaptureLastRect(string kind, string? label)
    {
        if (!_captureDiagnosticRects || _diagnosticRects.Count >= DiagnosticSampleLimit)
            return;
        try
        {
            CaptureRect(kind, label, _getLastRect.Invoke(null, null));
        }
        catch (Exception exception)
        {
            _diagnosticRects.Add(
                $"{kind}=rect-failed:{exception.GetBaseException().GetType().Name}");
        }
    }

    private void CaptureRect(string kind, string? label, object? rect)
    {
        if (!_captureDiagnosticRects || _diagnosticRects.Count >= DiagnosticSampleLimit)
            return;
        _diagnosticRects.Add(
            $"{kind}:{SanitizeDiagnosticText(label)}={FormatRect(rect)}");
    }

    private static string FormatRect(object? value)
        => value == null
            ? "null"
            : $"{ReadNumericField(value, "m_XMin"):0.#}," +
              $"{ReadNumericField(value, "m_YMin"):0.#}," +
              $"{ReadNumericField(value, "m_Width"):0.#}x" +
              $"{ReadNumericField(value, "m_Height"):0.#}";

    private object InsetHeaderTitleRect(object rect, float touchHeight)
    {
        var inset = ComputeHeaderTitleInset(touchHeight);
        var height = Math.Max(1f, ReadNumericField(rect, "m_Height") - inset * 2f);
        return Activator.CreateInstance(
                _rectType,
                ReadNumericField(rect, "m_XMin"),
                ReadNumericField(rect, "m_YMin") + inset,
                ReadNumericField(rect, "m_Width"),
                height)
            ?? throw new InvalidOperationException("could not construct header title Rect");
    }

    private static float ComputeHeaderTitleInset(float touchHeight)
        => Math.Clamp(touchHeight * 0.2f, 4f, 16f);

    private static string FormatColor(object? value)
        => value == null
            ? "null"
            : $"{ReadNumericField(value, "r"):0.##}," +
              $"{ReadNumericField(value, "g"):0.##}," +
              $"{ReadNumericField(value, "b"):0.##}," +
              $"{ReadNumericField(value, "a"):0.##}";

    private static string FormatMatrix(object? value)
        => value == null
            ? "null"
            : $"[{ReadNumericField(value, "m00"):0.##}," +
              $"{ReadNumericField(value, "m11"):0.##}," +
              $"{ReadNumericField(value, "m03"):0.##}," +
              $"{ReadNumericField(value, "m13"):0.##}]";

    private static float ReadNumericField(object value, string name)
    {
        var field = value.GetType().GetField(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return field == null ? float.NaN : Convert.ToSingle(field.GetValue(value));
    }

    private static string SanitizeDiagnosticText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "<empty>";
        var singleLine = string.Join(' ', value.Split(
            ['\r', '\n', '\t'],
            StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= 96 ? singleLine : singleLine[..93] + "...";
    }

    private void TraceDiagnostic(string message, bool consumeBudget)
    {
        if (consumeBudget && _diagnosticBudget <= 0)
            return;
        if (consumeBudget)
            _diagnosticBudget--;
        Logger.Info(DiagnosticTag, $"{DiagnosticPrefix} {message}");
    }

    private void EnsureRuntimeValues()
    {
        _scrollPosition ??= Activator.CreateInstance(_vector2Type, 0f, 0f)
            ?? throw new InvalidOperationException("could not construct UnityEngine.Vector2");
    }

    private object GetEmptyOptions(MethodInfo method)
    {
        var type = method.GetParameters()[^1].ParameterType;
        if (_emptyOptionsByType.TryGetValue(type, out var value))
            return value;
        value = CreateEmptyOptions(type);
        _emptyOptionsByType.Add(type, value);
        return value;
    }

    private object GetGuiContent(string text)
    {
        if (_guiContentByText.TryGetValue(text, out var content))
            return content;
        content = _guiContentConstructor.Invoke([text]);
        _guiContentByText.Add(text, content);
        return content;
    }

    private void CloseFrame()
    {
        Exception? failure = null;
        try
        {
            CloseContentLayout(ref failure);
            CloseLayoutLevel(ref _verticalOpen, _endVertical, ref failure);
            CloseLayoutLevel(ref _areaOpen, _endArea, ref failure);
            RestoreMobileSkin(ref failure);
        }
        finally
        {
            _frameOpen = false;
            if (_legacyInputFrameOpen)
            {
                _legacyInputFrameOpen = false;
                PcCompatLegacyInputBridge.EndSettingsGuiFrame();
            }
        }
        if (failure != null)
        {
            throw new InvalidOperationException(
                "PcCompat settings IMGUI layout cleanup failed.",
                failure);
        }
    }

    private void CloseFrameBestEffort()
    {
        try
        {
            CloseFrame();
        }
        catch
        {
            // Preserve the original settings callback/layout failure.
        }
    }

    private void CloseContentLayout(ref Exception? failure)
    {
        CloseLayoutLevel(ref _sectionBodyVerticalOpen, _endVertical, ref failure);
        CloseLayoutLevel(ref _sectionBodyHorizontalOpen, _endHorizontal, ref failure);
        CloseLayoutLevel(ref _scrollOpen, _endScrollView, ref failure);
    }

    private static void CloseLayoutLevel(
        ref bool isOpen,
        MethodInfo closeMethod,
        ref Exception? failure)
    {
        if (!isOpen)
            return;
        try
        {
            closeMethod.Invoke(null, null);
        }
        catch (Exception exception)
        {
            failure ??= exception.GetBaseException();
        }
        finally
        {
            isOpen = false;
        }
    }

    private static object CreateEmptyOptions(Type type)
    {
        if (type.IsArray)
            return Array.CreateInstance(type.GetElementType()!, 0);
        var constructor = type.GetConstructor([typeof(long)]) ??
                          type.GetConstructor([typeof(int)]);
        if (constructor == null)
            throw new MissingMethodException(type.FullName, ".ctor(length)");
        var argument = constructor.GetParameters()[0].ParameterType == typeof(long)
            ? 0L
            : 0;
        return constructor.Invoke([argument]);
    }

    private void ApplyMobileSkin(MobileMetrics metrics, object? localizedFont)
    {
        ApplyMobileMatrix(metrics.RenderScale);
        try
        {
            ApplyMobileSkinCore(metrics, localizedFont);
        }
        catch
        {
            Exception? ignored = null;
            RestoreMobileSkin(ref ignored);
            throw;
        }
    }

    private void ApplyMobileSkinCore(MobileMetrics metrics, object? localizedFont)
    {
        var skin = _guiSkin.Invoke(null, null)
            ?? throw new InvalidOperationException("Unity GUI.skin is unavailable");
        var skinFont = _skinGetFont.Invoke(skin, null);
        _previousImGuiScale = PcCompatManagedImGuiBridge.EnterMobileSettingsScale(
            metrics.CustomDimensionScale,
            metrics.CustomFontScale,
            metrics.TouchHeight);
        _mobileImGuiScaleActive = true;
        _mobileSkin = skin;
        _mobileSkinFont = skinFont;
        if (localizedFont != null)
            _skinSetFont.Invoke(skin, [localizedFont]);
        var styles = new[]
        {
            new StylePolicy(_skinLabel.Invoke(skin, null), WordWrap: true, FixedHeight: 0f),
            new StylePolicy(_skinTextField.Invoke(skin, null), WordWrap: false, FixedHeight: metrics.TouchHeight),
            new StylePolicy(_skinTextArea.Invoke(skin, null), WordWrap: true, FixedHeight: 0f),
            new StylePolicy(_skinButton.Invoke(skin, null), WordWrap: false, FixedHeight: metrics.TouchHeight),
            new StylePolicy(_skinToggle.Invoke(skin, null), WordWrap: false, FixedHeight: metrics.TouchHeight)
        };
        foreach (var policy in styles)
        {
            var style = policy.Style;
            if (style == null || _mobileStyleSnapshots.Any(item => ReferenceEquals(item.Style, style)))
                continue;
            var padding = _styleGetPadding.Invoke(style, null)
                ?? throw new InvalidOperationException("GUIStyle.padding is unavailable");
            var edges = _paddingGetters
                .Select(getter => Convert.ToInt32(getter.Invoke(padding, null)))
                .ToArray();
            _mobileStyleSnapshots.Add(new StyleSnapshot(
                style,
                Convert.ToInt32(_styleGetFontSize.Invoke(style, null)),
                _styleGetFont.Invoke(style, null),
                _styleGetWordWrap.Invoke(style, null) is true,
                Convert.ToSingle(_styleGetFixedHeight.Invoke(style, null)),
                edges));
            _styleSetFontSize.Invoke(style, [metrics.FontSize]);
            if (localizedFont != null)
                _styleSetFont.Invoke(style, [localizedFont]);
            _styleSetWordWrap.Invoke(style, [policy.WordWrap]);
            PcCompatManagedImGuiBridge.SetFixedHeight(style, policy.FixedHeight);
            _paddingSetters[0].Invoke(padding, [Math.Max(edges[0], metrics.HorizontalPadding)]);
            _paddingSetters[1].Invoke(padding, [Math.Max(edges[1], metrics.HorizontalPadding)]);
            _paddingSetters[2].Invoke(padding, [Math.Max(edges[2], metrics.VerticalPadding)]);
            _paddingSetters[3].Invoke(padding, [Math.Max(edges[3], metrics.VerticalPadding)]);
        }
        if (_diagnosticBudget > 0)
        {
            _diagnosticStyles = string.Join(';', styles
                .Select(static policy => policy.Style)
                .Where(static style => style != null)
                .Take(5)
                .Select(style =>
                {
                    var normal = _styleGetNormal.Invoke(style, null);
                    var textColor = normal == null
                        ? null
                        : _styleStateGetTextColor.Invoke(normal, null);
                    return
                        $"fontSize={Convert.ToInt32(_styleGetFontSize.Invoke(style, null))}," +
                        $"font={_styleGetFont.Invoke(style, null) != null}," +
                        $"wrap={_styleGetWordWrap.Invoke(style, null) is true}," +
                        $"rich={_styleGetRichText.Invoke(style, null) is true}," +
                        $"color={FormatColor(textColor)}";
                }));
        }
    }

    private void RestoreMobileSkin(ref Exception? failure)
    {
        for (var index = _mobileStyleSnapshots.Count - 1; index >= 0; --index)
        {
            var snapshot = _mobileStyleSnapshots[index];
            try
            {
                _styleSetFontSize.Invoke(snapshot.Style, [snapshot.FontSize]);
                _styleSetFont.Invoke(snapshot.Style, [snapshot.Font]);
                _styleSetWordWrap.Invoke(snapshot.Style, [snapshot.WordWrap]);
                PcCompatManagedImGuiBridge.SetFixedHeight(snapshot.Style, snapshot.FixedHeight);
                var padding = _styleGetPadding.Invoke(snapshot.Style, null)
                    ?? throw new InvalidOperationException("GUIStyle.padding is unavailable");
                for (var edge = 0; edge < _paddingSetters.Length; ++edge)
                    _paddingSetters[edge].Invoke(padding, [snapshot.Padding[edge]]);
            }
            catch (Exception exception)
            {
                failure ??= exception.GetBaseException();
            }
        }
        _mobileStyleSnapshots.Clear();
        if (_mobileSkin != null)
        {
            try
            {
                _skinSetFont.Invoke(_mobileSkin, [_mobileSkinFont]);
            }
            catch (Exception exception)
            {
                failure ??= exception.GetBaseException();
            }
            finally
            {
                _mobileSkin = null;
                _mobileSkinFont = null;
            }
        }
        if (_mobileImGuiScaleActive)
        {
            try
            {
                PcCompatManagedImGuiBridge.ExitMobileSettingsScale(_previousImGuiScale);
            }
            catch (Exception exception)
            {
                failure ??= exception.GetBaseException();
            }
            finally
            {
                _mobileImGuiScaleActive = false;
                _previousImGuiScale = default;
            }
        }
        RestoreMobileMatrix(ref failure);
    }

    private void ApplyMobileMatrix(float renderScale)
    {
        if (_mobileGuiMatrixActive)
            throw new InvalidOperationException("PcCompat settings GUI matrix is already active.");
        var previous = _guiGetMatrix.Invoke(null, null)
            ?? throw new InvalidOperationException("Unity GUI.matrix is unavailable");
        var zero = Activator.CreateInstance(_vector3Type, 0f, 0f, 0f)
            ?? throw new InvalidOperationException("could not construct UnityEngine.Vector3 zero");
        var identityRotation = Activator.CreateInstance(_quaternionType, 0f, 0f, 0f, 1f)
            ?? throw new InvalidOperationException("could not construct UnityEngine.Quaternion identity");
        var scale = Activator.CreateInstance(_vector3Type, renderScale, renderScale, 1f)
            ?? throw new InvalidOperationException("could not construct UnityEngine.Vector3 scale");
        var scaleMatrix = _matrixTrs.Invoke(null, [zero, identityRotation, scale])
            ?? throw new InvalidOperationException("Unity Matrix4x4.TRS returned null");
        var composed = _matrixMultiply.Invoke(null, [previous, scaleMatrix])
            ?? throw new InvalidOperationException("Unity Matrix4x4 multiplication returned null");
        _guiSetMatrix.Invoke(null, [composed]);
        _previousGuiMatrix = previous;
        _mobileGuiMatrixActive = true;
    }

    private void RestoreMobileMatrix(ref Exception? failure)
    {
        if (!_mobileGuiMatrixActive)
            return;
        try
        {
            _guiSetMatrix.Invoke(null, [_previousGuiMatrix!]);
        }
        catch (Exception exception)
        {
            failure ??= exception.GetBaseException();
        }
        finally
        {
            _mobileGuiMatrixActive = false;
            _previousGuiMatrix = null;
        }
    }

    private LocalizedFont ResolveLocalizedFont()
    {
        var language = "English";
        object? gameFont = null;
        var scale = 1f;
        try
        {
            _rdStringSetup.Invoke(null, null);
            language = _rdStringLanguage.Invoke(null, null)?.ToString() ?? language;
            var data = _rdStringFontData.Invoke(null, null)
                ?? throw new InvalidOperationException("RDString.fontData is unavailable");
            gameFont = _fontDataFont.Invoke(data, null)
                ?? throw new InvalidOperationException("FontData.font is unavailable");
            scale = Convert.ToSingle(_fontDataScale.Invoke(data, null));
            if (!float.IsFinite(scale) || scale <= 0f)
                scale = 1f;
        }
        catch (Exception exception)
        {
            if (!_fontResolutionFailureLogged)
            {
                _fontResolutionFailureLogged = true;
                Console.WriteLine(
                    "[PcModCompat][SettingsFont][WARN] localized font unavailable; " +
                    exception.GetBaseException().Message);
            }
        }

        var resourceFont = ResolveResourceFont();
        if (resourceFont != null)
        {
            _fontSource = "VirtualBundle";
            return new LocalizedFont(resourceFont, scale, language, _fontSource);
        }
        _fontSource = "RDString";
        return new LocalizedFont(gameFont, scale, language, _fontSource);
    }

    private object? ResolveResourceFont()
    {
        if (_resourceFontResolutionAttempted)
            return _resourceFont;
        _resourceFontResolutionAttempted = true;
        if (_modId == null || _resourceSessionGeneration <= 0)
            return null;

        var result = PcCompatVirtualBundleRegistry.ResolvePreferredAsset(
            _modId,
            _resourceSessionGeneration,
            "UnityEngine.Font",
            "TMPro.TMP_FontAsset");
        if (result.Status == PcCompatVirtualAssetResolveStatus.Ready && result.Asset != null)
        {
            var expectedFontType = _fontDataFont.ReturnType;
            if (expectedFontType.IsInstanceOfType(result.Asset))
            {
                _resourceFont = result.Asset;
                return _resourceFont;
            }
            result = result with
            {
                Status = PcCompatVirtualAssetResolveStatus.Failed,
                Error = $"projected font type mismatch expected={expectedFontType.FullName} " +
                        $"actual={result.Asset.GetType().FullName}"
            };
        }
        if (result.Status == PcCompatVirtualAssetResolveStatus.Failed)
        {
            var error = result.Error ?? "unknown VirtualBundle font failure";
            error = error.Replace('\r', ' ').Replace('\n', ' ');
            if (error.Length > 512)
                error = error[..512] + "...";
            Console.WriteLine(
                "[PcModCompat][SettingsFont][WARN] VirtualBundle font unavailable; " + error);
        }
        return null;
    }

    private static MobileMetrics ComputeMobileMetrics(
        int width,
        int height,
        float dpi,
        float fontScale)
    {
        var shortSide = Math.Min(width, height);
        var renderScale = float.IsFinite(dpi) && dpi is >= 120f and <= 800f
            ? Math.Clamp(dpi / 160f, 1f, 3.5f)
            : Math.Clamp(shortSide / 480f, 1f, 3.5f);
        var normalizedFontScale = float.IsFinite(fontScale) && fontScale > 0f
            ? Math.Clamp(fontScale, 0.8f, 1.35f)
            : 1f;
        var logicalWidth = width / renderScale;
        var logicalHeight = height / renderScale;
        var logicalShortSide = Math.Min(logicalWidth, logicalHeight);
        var margin = Math.Max(8f, Math.Max(10f, logicalShortSide * 0.02f));
        var panelWidth = Math.Min(
            logicalWidth - margin * 2f,
            Math.Max(420f, logicalShortSide * 1.75f));
        return new MobileMetrics(
            LogicalWidth: logicalWidth,
            LogicalHeight: logicalHeight,
            RenderScale: renderScale,
            Margin: margin,
            PanelWidth: panelWidth,
            ContentWidth: panelWidth - 24f,
            TouchHeight: 48f,
            FontSize: (int)Math.Clamp(
                MathF.Round(18f * normalizedFontScale),
                15f,
                24f),
            HorizontalPadding: 6,
            VerticalPadding: 5,
            CustomDimensionScale: 1f,
            CustomFontScale: 1f);
    }

    private static float ComputeMobileSliderWidth(float contentWidth)
        => Math.Clamp(contentWidth * 0.32f, 160f, 220f);

    private static double ComputeMobileSliderValue(string value, double min, double max)
        => double.TryParse(
               value,
               System.Globalization.NumberStyles.Float,
               System.Globalization.CultureInfo.InvariantCulture,
               out var parsed)
            ? ClampNumber(parsed, min, max)
            : min;

    private static double ClampNumber(double value, double min, double max)
    {
        value = Math.Max(value, min);
        return double.IsNaN(max) ? value : Math.Min(value, max);
    }

    private static string FormatNumber(double value, bool integral)
        => integral
            ? Math.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private void TryBindCanvasProbe(Assembly core)
    {
        try
        {
            var objectType = RequireType(core, "UnityEngine.Object");
            var gameObjectType = RequireType(core, "UnityEngine.GameObject");
            var transformType = RequireType(core, "UnityEngine.Transform");
            var behaviourType = RequireType(core, "UnityEngine.Behaviour");
            var componentType = RequireType(core, "UnityEngine.Component");
            Type? canvasType = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (AssemblyLoadContext.GetLoadContext(assembly) !=
                    AssemblyLoadContext.GetLoadContext(core))
                    continue;
                canvasType = assembly.GetType("UnityEngine.Canvas", throwOnError: false);
                if (canvasType != null)
                    break;
            }
            if (canvasType == null)
            {
                var uiModule = AssemblyLoadContext.GetLoadContext(core)!
                    .LoadFromAssemblyName(new AssemblyName("UnityEngine.UIModule"));
                canvasType = RequireType(uiModule, "UnityEngine.Canvas");
            }

            var genericFind = objectType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .SingleOrDefault(method =>
                    method.Name == "FindObjectsByType" &&
                    method.IsGenericMethodDefinition &&
                    method.GetGenericArguments().Length == 1 &&
                    method.GetParameters().Length == 1);
            if (genericFind == null)
                return;
            var sortModeType = genericFind.GetParameters()[0].ParameterType;

            _canvasType = canvasType;
            _findCanvases = genericFind.MakeGenericMethod(canvasType);
            _findObjectsSortModeNone = System.Enum.ToObject(sortModeType, 0);
            _canvasGetGameObject = componentType.GetMethod(
                "get_gameObject",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _canvasGetEnabled = behaviourType.GetMethod(
                "get_enabled",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _gameObjectGetActive = gameObjectType.GetMethod(
                "get_activeInHierarchy",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _gameObjectGetTransform = gameObjectType.GetMethod(
                "get_transform",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _transformGetParent = transformType.GetMethod(
                "get_parent",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _objectGetInstanceId = objectType.GetMethod(
                "GetInstanceID",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                Type.EmptyTypes,
                modifiers: null);
            if (_canvasGetGameObject == null || _canvasGetEnabled == null ||
                _gameObjectGetActive == null || _gameObjectGetTransform == null ||
                _transformGetParent == null || _objectGetInstanceId == null)
            {
                _findCanvases = null;
            }
        }
        catch
        {
            _findCanvases = null;
        }
    }

    private IReadOnlyList<object> SnapshotVisibleCanvases()
    {
        var result = _findCanvases!.Invoke(null, [_findObjectsSortModeNone]);
        if (result is not IEnumerable enumerable)
            return Array.Empty<object>();

        var canvases = new List<object>();
        foreach (var canvas in enumerable)
        {
            if (canvas == null || _canvasType?.IsInstanceOfType(canvas) != true)
                continue;
            if (_canvasGetEnabled!.Invoke(canvas, null) is not true)
                continue;
            var owner = _canvasGetGameObject!.Invoke(canvas, null);
            if (owner == null || _gameObjectGetActive!.Invoke(owner, null) is not true)
                continue;
            canvases.Add(canvas);
        }
        return canvases;
    }

    private int GetInstanceId(object value)
        => Convert.ToInt32(_objectGetInstanceId!.Invoke(value, null));

    private bool IsOwnerOrDescendant(object gameObject)
    {
        try
        {
            var current = gameObject;
            for (var depth = 0; depth < 128; ++depth)
            {
                if (_canvasOwnerIds.Contains(GetInstanceId(current)))
                    return true;
                var transform = _gameObjectGetTransform!.Invoke(current, null);
                if (transform == null)
                    return false;
                var parent = _transformGetParent!.Invoke(transform, null);
                if (parent == null)
                    return false;
                current = _canvasGetGameObject!.Invoke(parent, null)!;
                if (current == null)
                    return false;
            }
        }
        catch
        {
            // Destroyed or malformed hierarchy nodes cannot establish ownership.
        }
        return false;
    }

    private static MethodInfo RequireOptionsMethod(
        Type type,
        string name,
        Type returnType,
        params Type[] prefixTypes)
    {
        var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method =>
            {
                if (method.Name != name || method.ReturnType != returnType)
                    return false;
                var parameters = method.GetParameters();
                if (parameters.Length != prefixTypes.Length + 1 ||
                    !IsOptionsType(parameters[^1].ParameterType))
                    return false;
                for (var index = 0; index < prefixTypes.Length; ++index)
                {
                    if (parameters[index].ParameterType != prefixTypes[index])
                        return false;
                }
                return true;
            })
            .OrderBy(method => OptionsTypeRank(method.GetParameters()[^1].ParameterType))
            .ThenBy(
                method => method.GetParameters()[^1].ParameterType.FullName,
                StringComparer.Ordinal)
            .ThenBy(method => method.ToString(), StringComparer.Ordinal)
            .ToArray();
        return candidates.FirstOrDefault()
            ?? throw new MissingMethodException(type.FullName, name);
    }

    private static int OptionsTypeRank(Type type)
    {
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition().FullName ?? string.Empty;
            if (definition.Contains("Il2CppReferenceArray", StringComparison.Ordinal))
                return 0;
            if (definition.Contains("Il2CppStructArray", StringComparison.Ordinal))
                return 1;
            return 2;
        }
        return type.IsArray ? 3 : 4;
    }

    private static MethodInfo RequireMethod(
        Type type,
        string name,
        Type returnType,
        params Type[] parameterTypes)
        => type.GetMethods(BindingFlags.Public | BindingFlags.Static)
               .SingleOrDefault(method =>
                   method.Name == name &&
                   method.ReturnType == returnType &&
                   method.GetParameters().Select(parameter => parameter.ParameterType)
                       .SequenceEqual(parameterTypes))
           ?? throw new MissingMethodException(type.FullName, name);

    private static bool IsOptionsType(Type type)
        => type.IsArray && type.GetElementType()?.FullName == "UnityEngine.GUILayoutOption" ||
           type.IsGenericType &&
           type.GetGenericArguments().Length == 1 &&
           type.GetGenericArguments()[0].FullName == "UnityEngine.GUILayoutOption";

    private static Type RequireType(Assembly assembly, string name)
        => assembly.GetType(name, throwOnError: true)!;

    private static MethodInfo RequireGetter(
        Type type,
        string name,
        Type returnType,
        bool isStatic)
    {
        var flags = BindingFlags.Public |
                    (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var accessor = type.GetProperty(name, flags)?.GetMethod;
        if (accessor != null && accessor.IsStatic == isStatic &&
            accessor.ReturnType == returnType && accessor.GetParameters().Length == 0)
            return accessor;

        return type.GetMethods(flags)
                   .SingleOrDefault(method =>
                       method.Name == $"get_{name}" &&
                       method.IsStatic == isStatic &&
                       method.ReturnType == returnType &&
                       method.GetParameters().Length == 0)
               ?? throw new MissingMethodException(type.FullName, $"get_{name}");
    }

    private static MethodInfo RequireSetter(
        Type type,
        string name,
        Type valueType,
        bool isStatic)
    {
        var flags = BindingFlags.Public |
                    (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var accessor = type.GetProperty(name, flags)?.SetMethod;
        if (accessor != null && accessor.IsStatic == isStatic &&
            accessor.ReturnType == typeof(void) &&
            accessor.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual([valueType]))
            return accessor;

        return type.GetMethods(flags)
                   .SingleOrDefault(method =>
                       method.Name == $"set_{name}" &&
                       method.IsStatic == isStatic &&
                       method.ReturnType == typeof(void) &&
                       method.GetParameters().Select(parameter => parameter.ParameterType)
                           .SequenceEqual([valueType]))
               ?? throw new MissingMethodException(type.FullName, $"set_{name}");
    }

    private bool IsChinese()
        => _gameLanguage.StartsWith("Chinese", StringComparison.OrdinalIgnoreCase);

    private sealed record StyleSnapshot(
        object Style,
        int FontSize,
        object? Font,
        bool WordWrap,
        float FixedHeight,
        int[] Padding);

    private sealed record StylePolicy(
        object? Style,
        bool WordWrap,
        float FixedHeight);

    private sealed record LocalizedFont(object? Font, float Scale, string Language, string Source);

    private sealed record MobileMetrics(
        float LogicalWidth,
        float LogicalHeight,
        float RenderScale,
        float Margin,
        float PanelWidth,
        float ContentWidth,
        float TouchHeight,
        int FontSize,
        int HorizontalPadding,
        int VerticalPadding,
        float CustomDimensionScale,
        float CustomFontScale);
}

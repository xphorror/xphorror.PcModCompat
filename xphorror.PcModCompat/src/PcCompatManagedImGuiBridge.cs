using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;

namespace Xphorror.PcModCompat;

/// <summary>
/// Rebuilds PC-only GUILayout convenience overloads from the smaller Android
/// IL2CPP IMGUI surface. The cache is assembly-scoped so collectible MOD load
/// contexts are not rooted after unload.
/// </summary>
public static class PcCompatManagedImGuiBridge
{
    private static readonly ConditionalWeakTable<Assembly, Backend> Backends = new();
    private static Action<object, float>? s_fixedWidthSetter;
    private static Action<object, float>? s_fixedHeightSetter;
    private static Action<string?>? s_setNextControlName;
    private static Func<string?>? s_getNameOfFocusedControl;
    private static Action<object>? s_dragWindow;
    private static Func<PcCompatImGuiOptionKind, float, IntPtr>? s_nativeOptionFactory;
    [ThreadStatic] private static float t_mobileDimensionScale;
    [ThreadStatic] private static float t_mobileFontScale;
    [ThreadStatic] private static float t_mobileTouchHeight;
    [ThreadStatic] private static float t_mobileInteractiveVisualHeight;
    [ThreadStatic] private static float t_mobileContentWidth;
    [ThreadStatic] private static int t_mobileMeasurementStyleFingerprint;
    [ThreadStatic] private static PcCompatManagedImGuiInteractionFence? t_interactionFence;
    [ThreadStatic] private static Stack<ResponsiveLayoutScope>? t_responsiveLayouts;
    [ThreadStatic] private static Stack<ResponsiveLayoutScope>? t_recycledResponsiveLayouts;
    // Used only while one settings frame is active. Keeping this cache frame-scoped
    // avoids repeatedly scanning a MOD ALC for GUILayout on every Space/FlexibleSpace
    // call without retaining a collectible ALC after the frame ends.
    [ThreadStatic] private static AssemblyLoadContext? t_hostBackendContext;
    [ThreadStatic] private static Backend? t_hostBackend;

    internal static (float Dimension, float Font, float TouchHeight, float InteractiveVisualHeight, float ContentWidth, int MeasurementStyleFingerprint)
        EnterMobileSettingsScale(
        float dimensionScale,
        float fontScale,
        float touchHeight)
        => EnterMobileSettingsScale(
            dimensionScale,
            fontScale,
            touchHeight,
            interactiveVisualHeight: ComputeInteractiveVisualHeight(touchHeight),
            contentWidth: 0f,
            measurementStyleFingerprint: 0);

    internal static (float Dimension, float Font, float TouchHeight, float InteractiveVisualHeight, float ContentWidth, int MeasurementStyleFingerprint)
        EnterMobileSettingsScale(
        float dimensionScale,
        float fontScale,
        float touchHeight,
        float contentWidth)
        => EnterMobileSettingsScale(
            dimensionScale,
            fontScale,
            touchHeight,
            interactiveVisualHeight: ComputeInteractiveVisualHeight(touchHeight),
            contentWidth,
            measurementStyleFingerprint: 0);

    internal static (float Dimension, float Font, float TouchHeight, float InteractiveVisualHeight, float ContentWidth, int MeasurementStyleFingerprint)
        EnterMobileSettingsScale(
        float dimensionScale,
        float fontScale,
        float touchHeight,
        float contentWidth,
        int measurementStyleFingerprint)
        => EnterMobileSettingsScale(
            dimensionScale,
            fontScale,
            touchHeight,
            interactiveVisualHeight: ComputeInteractiveVisualHeight(touchHeight),
            contentWidth,
            measurementStyleFingerprint);

    internal static (float Dimension, float Font, float TouchHeight, float InteractiveVisualHeight, float ContentWidth, int MeasurementStyleFingerprint)
        EnterMobileSettingsScale(
        float dimensionScale,
        float fontScale,
        float touchHeight,
        float interactiveVisualHeight,
        float contentWidth,
        int measurementStyleFingerprint)
    {
        var previous = (
            t_mobileDimensionScale,
            t_mobileFontScale,
            t_mobileTouchHeight,
            t_mobileInteractiveVisualHeight,
            t_mobileContentWidth,
            t_mobileMeasurementStyleFingerprint);
        t_mobileDimensionScale = NormalizeScale(dimensionScale);
        t_mobileFontScale = NormalizeScale(fontScale);
        t_mobileTouchHeight = NormalizeTouchHeight(touchHeight);
        t_mobileInteractiveVisualHeight = NormalizeInteractiveVisualHeight(interactiveVisualHeight);
        t_mobileContentWidth = NormalizeContentWidth(contentWidth);
        t_mobileMeasurementStyleFingerprint = measurementStyleFingerprint;
        return previous;
    }

    internal static void ExitMobileSettingsScale(
        (float Dimension, float Font, float TouchHeight, float InteractiveVisualHeight, float ContentWidth, int MeasurementStyleFingerprint) previous)
    {
        t_mobileDimensionScale = previous.Dimension;
        t_mobileFontScale = previous.Font;
        t_mobileTouchHeight = previous.TouchHeight;
        t_mobileInteractiveVisualHeight = previous.InteractiveVisualHeight;
        t_mobileContentWidth = previous.ContentWidth;
        t_mobileMeasurementStyleFingerprint = previous.MeasurementStyleFingerprint;
    }

    private static float GetMobileTouchHeight() => t_mobileTouchHeight;

    private static float GetMobileInteractiveVisualHeight()
        => t_mobileInteractiveVisualHeight > 0f
            ? t_mobileInteractiveVisualHeight
            : ComputeInteractiveVisualHeight(t_mobileTouchHeight);

    // IMGUI cannot separate a control's visual rectangle from its hit rectangle. Keep
    // third-party compact controls visually compact, but never permit a text baseline
    // below the height required by the mobile font and skin padding.
    internal static float ComputeInteractiveVisualHeight(float touchHeight)
    {
        if (!float.IsFinite(touchHeight) || touchHeight <= 0f)
            return 0f;
        return Math.Clamp(touchHeight * 0.75f, 28f, 36f);
    }

    private static float GetMobileContentWidth() => t_mobileContentWidth;

    private static int GetMobileMeasurementFingerprint()
        => HashCode.Combine(
            (int)MathF.Round(t_mobileDimensionScale * 100f),
            (int)MathF.Round(t_mobileFontScale * 100f),
            (int)MathF.Round(t_mobileTouchHeight * 10f),
            (int)MathF.Round(t_mobileInteractiveVisualHeight * 10f),
            t_mobileMeasurementStyleFingerprint);

    internal static void BeginSettingsInteractionFrame(
        PcCompatManagedImGuiInteractionFence fence,
        PcCompatManagedImGuiEventKind eventKind)
    {
        ArgumentNullException.ThrowIfNull(fence);
        if (t_interactionFence != null)
            throw new InvalidOperationException("Nested settings interaction frame was rejected.");
        CloseOutstandingResponsiveLayouts();
        ClearFrameHostBackend();
        fence.BeginFrame(eventKind);
        try
        {
            t_interactionFence = fence;
            PcCompatManagedResponsiveImGuiLayout.BeginFrame(
                eventKind == PcCompatManagedImGuiEventKind.Layout,
                eventKind == PcCompatManagedImGuiEventKind.Repaint,
                t_mobileContentWidth,
                t_mobileFontScale,
                t_mobileTouchHeight,
                measurementStyleFingerprint: GetMobileMeasurementFingerprint());
        }
        catch
        {
            CloseOutstandingResponsiveLayouts();
            ClearFrameHostBackend();
            t_interactionFence = null;
            fence.EndFrame(completed: false);
            throw;
        }
    }

    internal static void EndSettingsInteractionFrame(
        PcCompatManagedImGuiInteractionFence fence,
        bool completed)
    {
        ArgumentNullException.ThrowIfNull(fence);
        if (!ReferenceEquals(t_interactionFence, fence))
            return;
        try
        {
            CloseOutstandingResponsiveLayouts();
            PcCompatManagedResponsiveImGuiLayout.EndFrame();
        }
        finally
        {
            try
            {
                // A failed responsive-layout cleanup must not leave the fence
                // open. Otherwise the controller cannot enter Recovering after
                // Unity reports the original GUILayout mismatch.
                fence.EndFrame(completed);
            }
            finally
            {
                ClearFrameHostBackend();
                t_interactionFence = null;
            }
        }
    }

    public static void RegisterFixedWidthSetter(Action<object, float> setter)
    {
        ArgumentNullException.ThrowIfNull(setter);
        Volatile.Write(ref s_fixedWidthSetter, setter);
    }

    public static void RegisterFixedHeightSetter(Action<object, float> setter)
    {
        ArgumentNullException.ThrowIfNull(setter);
        Volatile.Write(ref s_fixedHeightSetter, setter);
    }

    public static void RegisterControlFocusBridge(
        Action<string?> setNextControlName,
        Func<string?> getNameOfFocusedControl)
    {
        ArgumentNullException.ThrowIfNull(setNextControlName);
        ArgumentNullException.ThrowIfNull(getNameOfFocusedControl);
        Volatile.Write(ref s_setNextControlName, setNextControlName);
        Volatile.Write(ref s_getNameOfFocusedControl, getNameOfFocusedControl);
    }

    public static void SetNextControlName(string? name)
    {
        var setter = Volatile.Read(ref s_setNextControlName)
            ?? throw new InvalidOperationException(
                "Android GUI control focus bridge is not registered.");
        setter(name);
    }

    public static string? GetNameOfFocusedControl()
    {
        var getter = Volatile.Read(ref s_getNameOfFocusedControl)
            ?? throw new InvalidOperationException(
                "Android GUI control focus bridge is not registered.");
        return getter();
    }

    public static void RegisterDragWindowBridge(Action<object> dragWindow)
    {
        ArgumentNullException.ThrowIfNull(dragWindow);
        Volatile.Write(ref s_dragWindow, dragWindow);
    }

    /// <summary>
    /// Registers the Android-only fallback for GUILayoutOption kinds whose public
    /// GUILayout factories were stripped from this game's metadata. The callback
    /// returns an IL2CPP object pointer; the active MOD ALC owns the proxy wrapper.
    /// </summary>
    public static void RegisterNativeOptionFactory(
        Func<PcCompatImGuiOptionKind, float, IntPtr> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Volatile.Write(ref s_nativeOptionFactory, factory);
    }

    public static void DragWindow(object position)
    {
        ArgumentNullException.ThrowIfNull(position);
        var dragWindow = Volatile.Read(ref s_dragWindow)
            ?? throw new InvalidOperationException(
                "Android GUI DragWindow bridge is not registered.");
        dragWindow(position);
    }

    public static void SetFixedWidth(object style, float value)
    {
        ArgumentNullException.ThrowIfNull(style);
        var setter = Volatile.Read(ref s_fixedWidthSetter)
            ?? throw new InvalidOperationException(
                "Android GUIStyle fixedWidth bridge is not registered.");
        setter(style, ScaleDimension(value));
    }

    public static void SetFixedHeight(object style, float value)
    {
        ArgumentNullException.ThrowIfNull(style);
        var setter = Volatile.Read(ref s_fixedHeightSetter)
            ?? throw new InvalidOperationException(
                "Android GUIStyle fixedHeight bridge is not registered.");
        setter(style, ScaleDimension(value));
    }

    public static void SetFontSize(object style, int value)
    {
        ArgumentNullException.ThrowIfNull(style);
        Backends.GetValue(style.GetType().Assembly, static assembly => new Backend(assembly))
            .SetFontSize(style, ScaleFont(value));
    }

    public static void SetNormal(object style, object state)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(state);
        Backends.GetValue(style.GetType().Assembly, static assembly => new Backend(assembly))
            .SetNormal(style, state);
    }

    public static void SetMargin(object style, object margin)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(margin);
        Backends.GetValue(style.GetType().Assembly, static assembly => new Backend(assembly))
            .SetMargin(style, margin);
    }

    public static bool ButtonTextWithStyle(string text, object style, object options)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(options);
        var observed = Backends.GetValue(style.GetType().Assembly, static assembly => new Backend(assembly))
            .ButtonTextWithStyle(text ?? string.Empty, style, options, wrapText: null);
        return ResolveButtonResult(observed, nameof(ButtonTextWithStyle), text);
    }

    public static bool ButtonTextWithStyle(string text, object style, object options, int callsiteToken)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(options);
        var backend = Backends.GetValue(style.GetType().Assembly, static assembly => new Backend(assembly));
        var decision = PrepareElement(
            backend,
            callsiteToken,
            PcCompatImGuiElementKind.Button,
            PcCompatManagedResponsiveImGuiLayout.RequiresMeasurement
                ? backend.MeasureText(text ?? string.Empty, style, interactive: true, supportsTextWrapping: true)
                : default,
            options);
        var observed = backend.ButtonTextWithStyle(text ?? string.Empty, style, options, decision.WrapText);
        return ResolveButtonResult(observed, nameof(ButtonTextWithStyle), text, callsiteToken);
    }

    public static bool ButtonText(string text, object options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var observed = GetBackendForOptions(options).ButtonText(text ?? string.Empty, options, wrapText: null);
        return ResolveButtonResult(observed, nameof(ButtonText), text);
    }

    public static bool ButtonText(string text, object options, int callsiteToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var backend = GetBackendForOptions(options);
        var decision = PrepareElement(
            backend,
            callsiteToken,
            PcCompatImGuiElementKind.Button,
            PcCompatManagedResponsiveImGuiLayout.RequiresMeasurement
                ? backend.MeasureButtonText(text ?? string.Empty)
                : default,
            options);
        var observed = backend.ButtonText(text ?? string.Empty, options, decision.WrapText);
        return ResolveButtonResult(observed, nameof(ButtonText), text, callsiteToken);
    }

    public static bool ButtonTextureWithStyle(object? image, object style, object options)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(options);
        var observed = Backends.GetValue(style.GetType().Assembly, static assembly => new Backend(assembly))
            .ButtonTextureWithStyle(image, style, options);
        return ResolveButtonResult(observed, nameof(ButtonTextureWithStyle), image?.GetType().FullName);
    }

    public static bool ButtonTextureWithStyle(object? image, object style, object options, int callsiteToken)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(options);
        var backend = Backends.GetValue(style.GetType().Assembly, static assembly => new Backend(assembly));
        PrepareElement(
            backend,
            callsiteToken,
            PcCompatImGuiElementKind.Icon,
            PcCompatManagedResponsiveImGuiLayout.RequiresMeasurement
                ? backend.MeasureIconButton(style)
                : default,
            options);
        var observed = backend.ButtonTextureWithStyle(image, style, options);
        return ResolveButtonResult(
            observed,
            nameof(ButtonTextureWithStyle),
            image?.GetType().FullName,
            callsiteToken);
    }

    public static bool ToggleTextWithStyle(bool value, string text, object style, object options)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(options);
        var observed = Backends.GetValue(style.GetType().Assembly, static assembly => new Backend(assembly))
            .ToggleTextWithStyle(value, text ?? string.Empty, style, options, wrapText: null);
        return t_interactionFence?.ResolveRawToggle(value, observed) ?? observed;
    }

    public static bool ToggleTextWithStyle(
        bool value,
        string text,
        object style,
        object options,
        int callsiteToken)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(options);
        var backend = Backends.GetValue(style.GetType().Assembly, static assembly => new Backend(assembly));
        var decision = PrepareElement(
            backend,
            callsiteToken,
            PcCompatImGuiElementKind.Toggle,
            PcCompatManagedResponsiveImGuiLayout.RequiresMeasurement
                ? backend.MeasureText(text ?? string.Empty, style, interactive: true, supportsTextWrapping: true)
                : default,
            options);
        var observed = backend.ToggleTextWithStyle(value, text ?? string.Empty, style, options, decision.WrapText);
        return t_interactionFence?.ResolveRawToggle(value, observed, callsiteToken) ?? observed;
    }

    public static bool ToggleText(bool value, string text, object options, int callsiteToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var backend = GetBackendForOptions(options);
        var decision = PrepareElement(
            backend,
            callsiteToken,
            PcCompatImGuiElementKind.Toggle,
            PcCompatManagedResponsiveImGuiLayout.RequiresMeasurement
                ? backend.MeasureToggleText(text ?? string.Empty)
                : default,
            options);
        var observed = backend.ToggleText(value, text ?? string.Empty, options, decision.WrapText);
        return t_interactionFence?.ResolveRawToggle(value, observed, callsiteToken) ?? observed;
    }

    public static bool ToggleContent(bool value, object? content, object options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var observed = GetBackendForOptions(options)
            .ToggleContent(value, content, options);
        return t_interactionFence?.ResolveRawToggle(value, observed) ?? observed;
    }

    public static bool ToggleContent(bool value, object? content, object options, int callsiteToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var backend = GetBackendForOptions(options);
        var decision = PrepareElement(
            backend,
            callsiteToken,
            PcCompatImGuiElementKind.Toggle,
            PcCompatManagedResponsiveImGuiLayout.RequiresMeasurement
                ? backend.MeasureToggleContent(content)
                : default,
            options);
        var observed = backend.ToggleContent(value, content, options, decision.WrapText);
        return t_interactionFence?.ResolveRawToggle(value, observed, callsiteToken) ?? observed;
    }

    public static string TextArea(string text, object options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var current = text ?? string.Empty;
        var observed = GetBackendForOptions(options)
            .TextArea(current, options);
        return t_interactionFence?.ResolveRawText(current, observed) ?? observed;
    }

    public static string TextArea(string text, object options, int callsiteToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var current = text ?? string.Empty;
        var backend = GetBackendForOptions(options);
        PrepareElement(
            backend,
            callsiteToken,
            PcCompatImGuiElementKind.Input,
            PcCompatManagedResponsiveImGuiLayout.RequiresMeasurement
                ? backend.MeasureTextArea(current)
                : default,
            options);
        var observed = backend.TextArea(current, options);
        return t_interactionFence?.ResolveRawText(current, observed, callsiteToken) ?? observed;
    }

    public static void DrawTexture<T>(T position, object? image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var backend = GetBackendForAssembly(typeof(T).Assembly);
        backend.DrawTexture(position!, image);
    }

    public static void LabelContent(object content, object options)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        GetBackendForAssembly(content.GetType().Assembly).LabelContent(content, options);
    }

    public static void LabelContent(object content, object options, int callsiteToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        var backend = GetBackendForAssembly(content.GetType().Assembly);
        var decision = PrepareElement(
            backend,
            callsiteToken,
            PcCompatImGuiElementKind.Label,
            PcCompatManagedResponsiveImGuiLayout.RequiresMeasurement
                ? backend.MeasureLabelContent(content)
                : default,
            options);
        backend.LabelContent(content, options, decision.WrapText);
    }

    public static void LabelText(string text, object options, int callsiteToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var backend = GetBackendForOptions(options);
        var decision = PrepareElement(
            backend,
            callsiteToken,
            PcCompatImGuiElementKind.Label,
            PcCompatManagedResponsiveImGuiLayout.RequiresMeasurement
                ? backend.MeasureLabelText(text ?? string.Empty)
                : default,
            options);
        backend.LabelText(text ?? string.Empty, options, decision.WrapText);
    }

    public static void LabelTextWithStyle(string text, object style, object options, int callsiteToken)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(options);
        var backend = Backends.GetValue(style.GetType().Assembly, static assembly => new Backend(assembly));
        var decision = PrepareElement(
            backend,
            callsiteToken,
            PcCompatImGuiElementKind.Label,
            PcCompatManagedResponsiveImGuiLayout.RequiresMeasurement
                ? backend.MeasureText(text ?? string.Empty, style, interactive: false, supportsTextWrapping: true)
                : default,
            options);
        backend.LabelTextWithStyle(text ?? string.Empty, style, options, decision.WrapText);
    }

    public static int SelectionGrid(int selected, object texts, int xCount, object options)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(options);
        var backend = GetBackendForAssembly(texts.GetType().Assembly);
        var observed = backend.SelectionGrid(selected, texts, xCount, options);
        return t_interactionFence?.ResolveRawValue(selected, observed) ?? observed;
    }

    public static int SelectionGrid(
        int selected,
        object texts,
        int xCount,
        object options,
        int callsiteToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(options);
        var backend = GetBackendForAssembly(texts.GetType().Assembly);
        var measurement = PcCompatManagedResponsiveImGuiLayout.RequiresMeasurement
            ? backend.MeasureSelectionGrid(texts, xCount)
            : new PcCompatSelectionGridMeasurement(
                CellMinimumWidth: 0f,
                CellPreferredWidth: 0f,
                CellPreferredHeight: 0f,
                ItemCount: backend.GetCollectionCount(texts));
        if (measurement.ItemCount <= 0)
        {
            var emptyObserved = backend.SelectionGrid(selected, texts, xCount, options);
            return t_interactionFence?.ResolveRawValue(selected, emptyObserved, callsiteToken) ?? emptyObserved;
        }

        var selection = PcCompatManagedResponsiveImGuiLayout.SelectSelectionGridColumns(
            callsiteToken,
            xCount,
            measurement,
            PcCompatManagedResponsiveImGuiLayout.ReadOptions(options));
        _ = PrepareElement(
            backend,
            callsiteToken,
            PcCompatImGuiElementKind.Selection,
            PcCompatManagedResponsiveImGuiLayout.RequiresMeasurement
                ? measurement.ToOuterMeasurement(xCount)
                : default,
            options);
        var observed = backend.SelectionGrid(
            selected,
            texts,
            selection.Columns,
            options,
            selection.WrapLabels,
            selection.CellWidth,
            selection.CellHeight,
            selection.OverrideHeightConstraints);
        return t_interactionFence?.ResolveRawValue(selected, observed, callsiteToken) ?? observed;
    }

    public static float HorizontalSlider(float value, float leftValue, float rightValue, object options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var backend = GetBackendForAssembly(options.GetType().Assembly);
        var observed = backend.HorizontalSlider(value, leftValue, rightValue, options);
        return t_interactionFence?.ResolveRawValue(value, observed) ?? observed;
    }

    public static float HorizontalSlider(
        float value,
        float leftValue,
        float rightValue,
        object options,
        int callsiteToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var backend = GetBackendForOptions(options);
        PrepareElement(
            backend,
            callsiteToken,
            PcCompatImGuiElementKind.Slider,
            PcCompatManagedResponsiveImGuiLayout.RequiresMeasurement
                ? backend.MeasureHorizontalSlider()
                : default,
            options);
        var observed = backend.HorizontalSlider(value, leftValue, rightValue, options);
        return t_interactionFence?.ResolveRawValue(value, observed, callsiteToken) ?? observed;
    }

    public static string TextField(string text, object options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var current = text ?? string.Empty;
        var backend = GetBackendForAssembly(options.GetType().Assembly);
        var observed = backend.TextField(current, options);
        return t_interactionFence?.ResolveRawValue(current, observed) ?? observed;
    }

    public static string TextField(string text, object options, int callsiteToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var current = text ?? string.Empty;
        var backend = GetBackendForOptions(options);
        PrepareElement(
            backend,
            callsiteToken,
            PcCompatImGuiElementKind.Input,
            PcCompatManagedResponsiveImGuiLayout.RequiresMeasurement
                ? backend.MeasureTextField(current)
                : default,
            options);
        var observed = backend.TextField(current, options);
        return t_interactionFence?.ResolveRawValue(current, observed, callsiteToken) ?? observed;
    }

    public static void BeginVerticalWithStyle(object style, object options)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(options);
        GetBackendForAssembly(style.GetType().Assembly)
            .BeginVerticalWithStyle(style, options);
    }

    public static void BeginHorizontal(object options, int callsiteToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var backend = GetBackendForOptions(options);
        var decision = PcCompatManagedResponsiveImGuiLayout.BeginHorizontal(callsiteToken);
        var scope = AcquireResponsiveLayoutScope();
        try
        {
            scope.BeginHorizontal(backend, options, decision.Mode);
            GetResponsiveLayoutStack().Push(scope);
        }
        catch
        {
            RecycleResponsiveLayoutScope(scope);
            throw;
        }
    }

    public static void EndHorizontal(int callsiteToken)
    {
        var scope = PopResponsiveScope(isHorizontal: true);
        if (scope is null)
        {
            GetHostBackend().EndHorizontal();
            PcCompatManagedResponsiveImGuiLayout.EndHorizontal(callsiteToken, 0f);
            return;
        }

        try
        {
            var measuredWidth = scope.Close();
            PcCompatManagedResponsiveImGuiLayout.EndHorizontal(callsiteToken, measuredWidth);
        }
        finally
        {
            RecycleResponsiveLayoutScope(scope);
        }
    }

    public static void BeginVertical(object options, int callsiteToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var backend = GetBackendForOptions(options);
        _ = PcCompatManagedResponsiveImGuiLayout.BeginVertical(callsiteToken);
        var scope = AcquireResponsiveLayoutScope();
        try
        {
            scope.BeginVertical(backend);
            backend.BeginVertical(options);
            GetResponsiveLayoutStack().Push(scope);
        }
        catch
        {
            RecycleResponsiveLayoutScope(scope);
            throw;
        }
    }

    public static void BeginVerticalWithStyle(object style, object options, int callsiteToken)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(options);
        var backend = Backends.GetValue(style.GetType().Assembly, static assembly => new Backend(assembly));
        _ = PcCompatManagedResponsiveImGuiLayout.BeginVertical(callsiteToken);
        var scope = AcquireResponsiveLayoutScope();
        try
        {
            scope.BeginVertical(backend);
            backend.BeginVerticalWithStyle(style, options);
            GetResponsiveLayoutStack().Push(scope);
        }
        catch
        {
            RecycleResponsiveLayoutScope(scope);
            throw;
        }
    }

    public static void BeginVerticalWithContentStyle(
        object content,
        object style,
        object options,
        int callsiteToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(options);
        var backend = Backends.GetValue(style.GetType().Assembly, static assembly => new Backend(assembly));
        _ = PcCompatManagedResponsiveImGuiLayout.BeginVertical(callsiteToken);
        var scope = AcquireResponsiveLayoutScope();
        try
        {
            scope.BeginVertical(backend);
            backend.BeginVerticalWithContentStyle(content, style, options);
            GetResponsiveLayoutStack().Push(scope);
        }
        catch
        {
            RecycleResponsiveLayoutScope(scope);
            throw;
        }
    }

    public static void BeginVerticalNamed(string styleName, object options, int callsiteToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var backend = GetBackendForOptions(options);
        BeginVerticalWithStyle(backend.ResolveStyle(styleName), options, callsiteToken);
    }

    public static void EndVertical(int callsiteToken)
    {
        var scope = PopResponsiveScope(isHorizontal: false);
        if (scope is null)
        {
            GetHostBackend().EndVertical();
            PcCompatManagedResponsiveImGuiLayout.EndVertical(callsiteToken);
            return;
        }

        try
        {
            scope.Close();
            PcCompatManagedResponsiveImGuiLayout.EndVertical(callsiteToken);
        }
        finally
        {
            RecycleResponsiveLayoutScope(scope);
        }
    }

    public static void Space(float pixels, int callsiteToken)
    {
        var backend = GetHostBackend();
        _ = PrepareElement(
            backend,
            callsiteToken,
            PcCompatImGuiElementKind.Space,
            new PcCompatImGuiMeasurement(Math.Max(0f, pixels), Math.Max(0f, pixels), false),
            default);
        backend.Space(pixels);
    }

    public static void FlexibleSpace(int callsiteToken)
    {
        var backend = GetHostBackend();
        _ = PrepareElement(
            backend,
            callsiteToken,
            PcCompatImGuiElementKind.FlexibleSpace,
            new PcCompatImGuiMeasurement(0f, 0f, false, ExpandWidth: true),
            default);
        backend.FlexibleSpace();
    }

    public static object Width(float value, int callsiteToken)
        => CreateTaggedOption(PcCompatImGuiOptionKind.Width, value, callsiteToken);

    public static object MinWidth(float value, int callsiteToken)
        => CreateTaggedOption(PcCompatImGuiOptionKind.MinWidth, value, callsiteToken);

    public static object MaxWidth(float value, int callsiteToken)
        => CreateTaggedOption(PcCompatImGuiOptionKind.MaxWidth, value, callsiteToken);

    public static object Height(float value, int callsiteToken)
        => CreateTaggedOption(PcCompatImGuiOptionKind.Height, value, callsiteToken);

    public static object MinHeight(float value, int callsiteToken)
        => CreateTaggedOption(PcCompatImGuiOptionKind.MinHeight, value, callsiteToken);

    public static object MaxHeight(float value, int callsiteToken)
        => CreateTaggedOption(PcCompatImGuiOptionKind.MaxHeight, value, callsiteToken);

    public static object ExpandWidth(bool value, int callsiteToken)
        => CreateTaggedOption(PcCompatImGuiOptionKind.ExpandWidth, value ? 1f : 0f, callsiteToken);

    public static object ExpandHeight(bool value, int callsiteToken)
        => CreateTaggedOption(PcCompatImGuiOptionKind.ExpandHeight, value ? 1f : 0f, callsiteToken);

    public static object GetRect(float width, float height)
    {
        var backendAssembly = FindUnityAssembly(typeof(PcCompatManagedImGuiBridge).Assembly);
        return GetBackendForAssembly(backendAssembly).GetRect(width, height);
    }

    private static bool ResolveButtonResult(bool observed, string source, string? label)
    {
        var activated = t_interactionFence?.ResolveRawButton(observed) ?? observed;
        return CompleteButtonResult(observed, activated, source, label);
    }

    private static bool ResolveButtonResult(
        bool observed,
        string source,
        string? label,
        int callsiteToken)
    {
        var activated = t_interactionFence?.ResolveRawButton(observed, callsiteToken) ?? observed;
        return CompleteButtonResult(observed, activated, source, label);
    }

    private static bool CompleteButtonResult(bool observed, bool activated, string source, string? label)
    {
        if (observed)
        {
            PcCompatLegacyInputBridge.NotifySettingsButtonActivated(source, label);
        }
        else if (activated)
        {
            PcCompatLegacyInputBridge.SuppressSettingsInputAfterDeferredButton();
        }
        return activated;
    }

    private static PcCompatImGuiElementDecision PrepareElement(
        Backend backend,
        int callsiteToken,
        PcCompatImGuiElementKind kind,
        PcCompatImGuiMeasurement measurement,
        object? options)
    {
        var decision = PcCompatManagedResponsiveImGuiLayout.BeforeElement(
            callsiteToken,
            kind,
            measurement,
            options is null ? default : PcCompatManagedResponsiveImGuiLayout.ReadOptions(options));
        return decision;
    }

    private static ResponsiveLayoutScope? PopResponsiveScope(bool isHorizontal)
    {
        if (t_responsiveLayouts is not { Count: > 0 } ||
            t_responsiveLayouts.Peek().IsHorizontal != isHorizontal)
        {
            // Do not let an unbalanced third-party Begin/End sequence contaminate a
            // later event. Close only the structures this bridge created; Unity still
            // receives the MOD's original End call below and reports the MOD error.
            CloseOutstandingResponsiveLayouts();
            return null;
        }

        return t_responsiveLayouts.Pop();
    }

    private static void CloseOutstandingResponsiveLayouts()
    {
        var layouts = t_responsiveLayouts;
        if (layouts is not { Count: > 0 })
            return;

        // This path exists solely for a third-party callback that aborted before its
        // matching End call. It must not replace the original exception or attempt to
        // synthesize missing MOD calls across frames.
        while (layouts.Count != 0)
        {
            var scope = layouts.Pop();
            try
            {
                _ = scope.Close();
            }
            catch
            {
                // Continue unwinding inner-to-outer. The settings controller retains
                // the originating MOD failure as the user-visible diagnostic.
            }
            finally
            {
                RecycleResponsiveLayoutScope(scope);
            }
        }
    }

    private static Stack<ResponsiveLayoutScope> GetResponsiveLayoutStack()
        => t_responsiveLayouts ??= new Stack<ResponsiveLayoutScope>();

    private static ResponsiveLayoutScope AcquireResponsiveLayoutScope()
        => t_recycledResponsiveLayouts is { Count: > 0 }
            ? t_recycledResponsiveLayouts.Pop()
            : new ResponsiveLayoutScope();

    private static void RecycleResponsiveLayoutScope(ResponsiveLayoutScope scope)
    {
        scope.Release();
        (t_recycledResponsiveLayouts ??= new Stack<ResponsiveLayoutScope>()).Push(scope);
    }

    private static object CreateTaggedOption(
        PcCompatImGuiOptionKind kind,
        float value,
        int callsiteToken)
    {
        var option = GetHostBackend().CreateOption(
            kind,
            value,
            Volatile.Read(ref s_nativeOptionFactory));
        PcCompatManagedResponsiveImGuiLayout.TagOption(option, kind, value, callsiteToken);
        return option;
    }

    private static Backend GetBackendForAssembly(Assembly anchor)
    {
        var assembly = FindUnityAssembly(anchor);
        return Backends.GetValue(assembly, static value => new Backend(value));
    }

    private static Backend GetBackendForOptions(object options)
    {
        var type = options.GetType();
        if (type.IsArray && type.GetElementType() is { } elementType)
            return GetBackendForAssembly(elementType.Assembly);
        if (type.IsGenericType && type.GetGenericArguments() is [var genericArgument])
            return GetBackendForAssembly(genericArgument.Assembly);
        return GetBackendForAssembly(type.Assembly);
    }

    private static Backend GetHostBackend()
    {
        var context = PcCompatManagedExecutionContext.Current?.ManagedLoadContext;
        if (t_hostBackend is not null && ReferenceEquals(t_hostBackendContext, context))
            return t_hostBackend;

        Backend backend;
        if (context is not null)
        {
            var proxy = context.Assemblies.FirstOrDefault(assembly =>
                assembly.GetType("UnityEngine.GUILayout", throwOnError: false, ignoreCase: false) is not null);
            if (proxy is not null)
            {
                backend = Backends.GetValue(proxy, static value => new Backend(value));
                t_hostBackendContext = context;
                t_hostBackend = backend;
                return backend;
            }
        }
        backend = GetBackendForAssembly(typeof(PcCompatManagedImGuiBridge).Assembly);
        t_hostBackendContext = context;
        t_hostBackend = backend;
        return backend;
    }

    private static void ClearFrameHostBackend()
    {
        t_hostBackend = null;
        t_hostBackendContext = null;
    }

    private static Assembly FindUnityAssembly(Assembly anchor)
    {
        if (anchor.GetType("UnityEngine.GUI", throwOnError: false, ignoreCase: false) is not null)
            return anchor;

        var candidates = new List<Assembly>();
        var context = AssemblyLoadContext.GetLoadContext(anchor);
        if (context is not null)
            candidates.AddRange(context.Assemblies);
        candidates.AddRange(AssemblyLoadContext.Default.Assemblies);
        candidates.AddRange(AppDomain.CurrentDomain.GetAssemblies());
        return candidates
                   .Distinct()
                   .FirstOrDefault(assembly =>
                       assembly.GetType("UnityEngine.GUI", throwOnError: false, ignoreCase: false) is not null)
               ?? throw new MissingMemberException(
                    "UnityEngine.IMGUIModule",
                    "UnityEngine.GUI");
    }

    private sealed class ResponsiveLayoutScope
    {
        private Backend? _backend;
        private object? _options;

        public bool IsHorizontal { get; private set; }

        public void BeginHorizontal(
            Backend backend,
            object options,
            PcCompatImGuiContainerMode _)
        {
            _backend = backend;
            IsHorizontal = true;
            _options = options;
            // The responsive planner may select wrapping or a stacked text policy,
            // but it must never add, remove, or substitute third-party GUILayout
            // groups. Unity requires an identical group stack for Layout, Repaint,
            // and input events; changing this horizontal group into a vertical one
            // caused the real JRP/JPOV LayoutGroup mismatch failures.
            backend.BeginHorizontal(options);
        }

        public void BeginVertical(Backend backend)
        {
            _backend = backend;
            IsHorizontal = false;
            _options = null;
        }

        public float Close()
        {
            var backend = RequireBackend();
            if (!IsHorizontal)
            {
                backend.EndVertical();
                return 0f;
            }

            backend.EndHorizontal();
            return backend.GetLastRectWidth();
        }

        public void Release()
        {
            // The thread-local pool outlives individual collectible MOD ALCs. Do not
            // let a retained backend or option wrapper keep an unloaded MOD rooted.
            _backend = null;
            _options = null;
            IsHorizontal = false;
        }

        private Backend RequireBackend()
            => _backend ?? throw new InvalidOperationException(
                "Responsive IMGUI layout scope is not initialized.");
    }

    private sealed class Backend
    {
        private const int TextMeasurementCacheCapacity = 512;
        private const BindingFlags StaticMethods =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags InstanceMethods =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private readonly ConstructorInfo _contentFromText;
        private readonly ConstructorInfo _contentFromImage;
        private readonly MethodInfo _doButton;
        private readonly MethodInfo _toggle;
        private readonly MethodInfo _labelWithContentStyle;
        private readonly MethodInfo _getSkin;
        private readonly MethodInfo _getButton;
        private readonly MethodInfo _getToggle;
        private readonly MethodInfo _getLabel;
        private readonly MethodInfo _getTextField;
        private readonly MethodInfo _getHorizontalSlider;
        private readonly MethodInfo _getHorizontalSliderThumb;
        private readonly MethodInfo _getNone;
        private readonly MethodInfo _getTextArea;
        private readonly MethodInfo _getRect;
        private readonly MethodInfo _getRectDimensions;
        private readonly MethodInfo _getControlId;
        private readonly MethodInfo _guiToggle;
        private readonly MethodInfo _guiTextField;
        private readonly MethodInfo _guiSlider;
        private readonly MethodInfo _beginVerticalWithContentStyle;
        private readonly MethodInfo _beginVertical;
        private readonly MethodInfo _endVertical;
        private readonly MethodInfo _beginHorizontal;
        private readonly MethodInfo _endHorizontal;
        private readonly MethodInfo _space;
        private readonly MethodInfo _flexibleSpace;
        private readonly MethodInfo _width;
        private readonly MethodInfo _minWidth;
        private readonly MethodInfo _height;
        private readonly MethodInfo _expandWidth;
        private readonly MethodInfo _expandHeight;
        private readonly Func<IntPtr, object> _wrapNativeOption;
        private readonly MethodInfo _doTextField;
        private readonly MethodInfo _getContentText;
        private readonly MethodInfo _getNormal;
        private readonly MethodInfo _getFont;
        private readonly MethodInfo _getFontSize;
        private readonly MethodInfo _getMargin;
        private readonly MethodInfo _getPadding;
        private readonly MethodInfo _getTextColor;
        private readonly MethodInfo _setTextColor;
        private readonly MethodInfo _setFontSize;
        private readonly MethodInfo _getFixedHeight;
        private readonly MethodInfo _getWordWrap;
        private readonly MethodInfo _getRichText;
        private readonly MethodInfo _setWordWrap;
        private readonly MethodInfo _styleFromName;
        private readonly MethodInfo? _calcMinMaxWidth;
        private readonly MethodInfo? _calcHeight;
        private readonly MethodInfo? _getLastRect;
        private readonly MethodInfo? _getRectWidth;
        private readonly MethodInfo _getRectCellWidth;
        private readonly MethodInfo _getRectX;
        private readonly MethodInfo _getRectY;
        private readonly MethodInfo _getRectHeight;
        private readonly MethodInfo _setRectX;
        private readonly MethodInfo _setRectY;
        private readonly MethodInfo _setRectWidth;
        private readonly MethodInfo _setRectHeight;
        private readonly MethodInfo[] _getMarginEdges;
        private readonly MethodInfo[] _setMarginEdges;
        private readonly object _keyboardFocus;
        private readonly object _passiveFocus;
        private readonly Func<object, IntPtr>? _stylePointerGetter;
        private readonly Func<object, IntPtr>? _fontPointerGetter;
        private readonly Dictionary<TextMeasurementKey, PcCompatImGuiMeasurement> _textMeasurements = new();
        private readonly Dictionary<Type, object> _emptyOptionsByType = new();

        public Backend(Assembly assembly)
        {
            var guiContent = RequireType(assembly, "UnityEngine.GUIContent");
            var guiStyle = RequireType(assembly, "UnityEngine.GUIStyle");
            var guiLayout = RequireType(assembly, "UnityEngine.GUILayout");
            var guiLayoutUtility = RequireType(assembly, "UnityEngine.GUILayoutUtility");
            var guiUtility = RequireType(assembly, "UnityEngine.GUIUtility");
            var gui = RequireType(assembly, "UnityEngine.GUI");
            var guiSkin = RequireType(assembly, "UnityEngine.GUISkin");
            var focusType = RequireType(assembly, "UnityEngine.FocusType");
            _stylePointerGetter = CreateNativePointerGetter(guiStyle);

            _contentFromText = RequireConstructor(
                guiContent,
                static parameters =>
                    parameters.Length == 1 && IsType(parameters[0], "System.String"),
                ".ctor(String)");
            _contentFromImage = RequireConstructor(
                guiContent,
                static parameters =>
                    parameters.Length == 3 &&
                    IsType(parameters[0], "System.String") &&
                    IsType(parameters[1], "UnityEngine.Texture") &&
                    IsType(parameters[2], "System.String"),
                ".ctor(String, Texture, String)");
            _doButton = RequireMethod(
                guiLayout,
                "DoButton",
                StaticMethods,
                static parameters =>
                    parameters.Length == 3 &&
                    IsType(parameters[0], "UnityEngine.GUIContent") &&
                    IsType(parameters[1], "UnityEngine.GUIStyle") &&
                    IsOptions(parameters[2]),
                "Boolean DoButton(GUIContent, GUIStyle, GUILayoutOption[])",
                preferClrOptions: true);
            _toggle = RequireMethod(
                guiLayout,
                "Toggle",
                StaticMethods,
                static parameters =>
                    parameters.Length == 4 &&
                    IsType(parameters[0], "System.Boolean") &&
                    IsType(parameters[1], "System.String") &&
                    IsType(parameters[2], "UnityEngine.GUIStyle") &&
                    IsOptions(parameters[3]),
                "Boolean Toggle(Boolean, String, GUIStyle, GUILayoutOption[])",
                preferClrOptions: true);
            _labelWithContentStyle = RequireMethod(
                gui,
                "Label",
                StaticMethods,
                static parameters =>
                    parameters.Length == 3 &&
                    IsType(parameters[0], "UnityEngine.Rect") &&
                    IsType(parameters[1], "UnityEngine.GUIContent") &&
                    IsType(parameters[2], "UnityEngine.GUIStyle"),
                "Void Label(Rect, GUIContent, GUIStyle)");
            _getSkin = RequireMethod(
                gui,
                "get_skin",
                StaticMethods,
                static parameters => parameters.Length == 0,
                "GUISkin get_skin()");
            _getButton = RequireMethod(
                guiSkin,
                "get_button",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "GUIStyle get_button()");
            _getToggle = RequireMethod(
                guiSkin,
                "get_toggle",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "GUIStyle get_toggle()");
            _getLabel = RequireMethod(
                guiSkin,
                "get_label",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "GUIStyle get_label()");
            _getTextField = RequireMethod(
                guiSkin,
                "get_textField",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "GUIStyle get_textField()");
            _getHorizontalSlider = RequireMethod(
                guiSkin,
                "get_horizontalSlider",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "GUIStyle get_horizontalSlider()");
            _getHorizontalSliderThumb = RequireMethod(
                guiSkin,
                "get_horizontalSliderThumb",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "GUIStyle get_horizontalSliderThumb()");
            _getNone = RequireMethod(
                guiStyle,
                "get_none",
                StaticMethods,
                static parameters => parameters.Length == 0,
                "GUIStyle get_none()");
            _getTextArea = RequireMethod(
                guiSkin,
                "get_textArea",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "GUIStyle get_textArea()");
            _getRect = RequireMethod(
                guiLayoutUtility,
                "GetRect",
                StaticMethods,
                static parameters =>
                    parameters.Length == 3 &&
                    IsType(parameters[0], "UnityEngine.GUIContent") &&
                    IsType(parameters[1], "UnityEngine.GUIStyle") &&
                    IsOptions(parameters[2]),
                "Rect GetRect(GUIContent, GUIStyle, GUILayoutOption[])",
                preferClrOptions: true);
            _getRectDimensions = RequireMethod(
                guiLayoutUtility,
                "GetRect",
                StaticMethods,
                static parameters =>
                    parameters.Length == 4 &&
                    IsType(parameters[0], "System.Single") &&
                    IsType(parameters[1], "System.Single") &&
                    IsType(parameters[2], "UnityEngine.GUIStyle") &&
                    IsOptions(parameters[3]),
                "Rect GetRect(Single, Single, GUIStyle, GUILayoutOption[])",
                preferClrOptions: true);
            _getControlId = RequireMethod(
                guiUtility,
                "GetControlID",
                StaticMethods,
                static parameters =>
                    parameters.Length == 2 &&
                    IsType(parameters[0], "UnityEngine.FocusType") &&
                    IsType(parameters[1], "UnityEngine.Rect"),
                "Int32 GetControlID(FocusType, Rect)");
            _guiToggle = RequireMethod(
                gui,
                "Toggle",
                StaticMethods,
                static parameters =>
                    parameters.Length == 4 &&
                    IsType(parameters[0], "UnityEngine.Rect") &&
                    IsType(parameters[1], "System.Boolean") &&
                    IsType(parameters[2], "UnityEngine.GUIContent") &&
                    IsType(parameters[3], "UnityEngine.GUIStyle"),
                "Boolean Toggle(Rect, Boolean, GUIContent, GUIStyle)");
            _guiTextField = RequireMethod(
                gui,
                "TextField",
                StaticMethods,
                static parameters =>
                    parameters.Length == 3 &&
                    IsType(parameters[0], "UnityEngine.Rect") &&
                    IsType(parameters[1], "System.String") &&
                    IsType(parameters[2], "System.Int32"),
                "String TextField(Rect, String, Int32)");
            _guiSlider = RequireMethod(
                gui,
                "Slider",
                StaticMethods,
                static parameters =>
                    parameters.Length == 10 &&
                    IsType(parameters[0], "UnityEngine.Rect") &&
                    IsType(parameters[1], "System.Single") &&
                    IsType(parameters[2], "System.Single") &&
                    IsType(parameters[3], "System.Single") &&
                    IsType(parameters[4], "System.Single") &&
                    IsType(parameters[5], "UnityEngine.GUIStyle") &&
                    IsType(parameters[6], "UnityEngine.GUIStyle") &&
                    IsType(parameters[7], "System.Boolean") &&
                    IsType(parameters[8], "System.Int32") &&
                    IsType(parameters[9], "UnityEngine.GUIStyle"),
                "Single Slider(Rect, Single, Single, Single, Single, GUIStyle, GUIStyle, Boolean, Int32, GUIStyle)");
            _beginVerticalWithContentStyle = RequireMethod(
                guiLayout,
                "BeginVertical",
                StaticMethods,
                static parameters =>
                    parameters.Length == 3 &&
                    IsType(parameters[0], "UnityEngine.GUIContent") &&
                    IsType(parameters[1], "UnityEngine.GUIStyle") &&
                    IsOptions(parameters[2]),
                "Void BeginVertical(GUIContent, GUIStyle, GUILayoutOption[])",
                preferClrOptions: true);
            _beginVertical = RequireMethod(
                guiLayout,
                "BeginVertical",
                StaticMethods,
                static parameters => parameters.Length == 1 && IsOptions(parameters[0]),
                "Void BeginVertical(GUILayoutOption[])",
                preferClrOptions: true);
            _endVertical = RequireMethod(
                guiLayout,
                "EndVertical",
                StaticMethods,
                static parameters => parameters.Length == 0,
                "Void EndVertical()");
            _beginHorizontal = RequireMethod(
                guiLayout,
                "BeginHorizontal",
                StaticMethods,
                static parameters => parameters.Length == 1 && IsOptions(parameters[0]),
                "Void BeginHorizontal(GUILayoutOption[])",
                preferClrOptions: true);
            _endHorizontal = RequireMethod(
                guiLayout,
                "EndHorizontal",
                StaticMethods,
                static parameters => parameters.Length == 0,
                "Void EndHorizontal()");
            _space = RequireMethod(
                guiLayout,
                "Space",
                StaticMethods,
                static parameters => parameters.Length == 1 && IsType(parameters[0], "System.Single"),
                "Void Space(Single)");
            _flexibleSpace = RequireMethod(
                guiLayout,
                "FlexibleSpace",
                StaticMethods,
                static parameters => parameters.Length == 0,
                "Void FlexibleSpace()");
            _width = RequireMethod(
                guiLayout,
                "Width",
                StaticMethods,
                static parameters => parameters.Length == 1 && IsType(parameters[0], "System.Single"),
                "GUILayoutOption Width(Single)");
            _wrapNativeOption = CreateNativePointerWrapper(RequireConstructor(
                _width.ReturnType,
                static parameters =>
                    parameters.Length == 1 && parameters[0].ParameterType == typeof(IntPtr),
                ".ctor(IntPtr)"));
            _minWidth = RequireMethod(
                guiLayout,
                "MinWidth",
                StaticMethods,
                static parameters => parameters.Length == 1 && IsType(parameters[0], "System.Single"),
                "GUILayoutOption MinWidth(Single)");
            _height = RequireMethod(
                guiLayout,
                "Height",
                StaticMethods,
                static parameters => parameters.Length == 1 && IsType(parameters[0], "System.Single"),
                "GUILayoutOption Height(Single)");
            _expandWidth = RequireMethod(
                guiLayout,
                "ExpandWidth",
                StaticMethods,
                static parameters => parameters.Length == 1 && IsType(parameters[0], "System.Boolean"),
                "GUILayoutOption ExpandWidth(Boolean)");
            _expandHeight = RequireMethod(
                guiLayout,
                "ExpandHeight",
                StaticMethods,
                static parameters => parameters.Length == 1 && IsType(parameters[0], "System.Boolean"),
                "GUILayoutOption ExpandHeight(Boolean)");
            _doTextField = RequireMethod(
                gui,
                "DoTextField",
                StaticMethods,
                static parameters =>
                    parameters.Length == 6 &&
                    IsType(parameters[0], "UnityEngine.Rect") &&
                    IsType(parameters[1], "System.Int32") &&
                    IsType(parameters[2], "UnityEngine.GUIContent") &&
                    IsType(parameters[3], "System.Boolean") &&
                    IsType(parameters[4], "System.Int32") &&
                    IsType(parameters[5], "UnityEngine.GUIStyle"),
                "Void DoTextField(Rect, Int32, GUIContent, Boolean, Int32, GUIStyle)");
            _getContentText = RequireMethod(
                guiContent,
                "get_text",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "String get_text()");
            _getNormal = RequireMethod(
                guiStyle,
                "get_normal",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "GUIStyleState get_normal()");
            _getFont = RequireMethod(
                guiStyle,
                "get_font",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "Font get_font()");
            _fontPointerGetter = CreateNativePointerGetter(_getFont.ReturnType);
            _getFontSize = RequireMethod(
                guiStyle,
                "get_fontSize",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "Int32 get_fontSize()");
            _getMargin = RequireMethod(
                guiStyle,
                "get_margin",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "RectOffset get_margin()");
            _getPadding = RequireMethod(
                guiStyle,
                "get_padding",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "RectOffset get_padding()");
            _getTextColor = RequireMethod(
                _getNormal.ReturnType,
                "get_textColor",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "Color get_textColor()");
            _setTextColor = RequireMethod(
                _getNormal.ReturnType,
                "set_textColor",
                InstanceMethods,
                static parameters =>
                    parameters.Length == 1 && IsType(parameters[0], "UnityEngine.Color"),
                "Void set_textColor(Color)");
            _setFontSize = RequireMethod(
                guiStyle,
                "set_fontSize",
                InstanceMethods,
                static parameters =>
                    parameters.Length == 1 && IsType(parameters[0], "System.Int32"),
                "Void set_fontSize(Int32)");
            _getFixedHeight = RequireMethod(
                guiStyle,
                "get_fixedHeight",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "Single get_fixedHeight()");
            _getWordWrap = RequireMethod(
                guiStyle,
                "get_wordWrap",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "Boolean get_wordWrap()");
            _getRichText = RequireMethod(
                guiStyle,
                "get_richText",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "Boolean get_richText()");
            _setWordWrap = RequireMethod(
                guiStyle,
                "set_wordWrap",
                InstanceMethods,
                static parameters =>
                    parameters.Length == 1 && IsType(parameters[0], "System.Boolean"),
                "Void set_wordWrap(Boolean)");
            _styleFromName = RequireMethod(
                guiStyle,
                "op_Implicit",
                StaticMethods,
                static parameters => parameters.Length == 1 && IsType(parameters[0], "System.String"),
                "GUIStyle op_Implicit(String)");
            _calcMinMaxWidth = FindMethod(
                guiStyle,
                "CalcMinMaxWidth",
                InstanceMethods,
                static parameters =>
                    parameters.Length == 3 &&
                    IsType(parameters[0], "UnityEngine.GUIContent") &&
                    IsSingleByRef(parameters[1]) &&
                    IsSingleByRef(parameters[2]),
                preferClrOptions: false);
            _calcHeight = FindMethod(
                guiStyle,
                "CalcHeight",
                InstanceMethods,
                static parameters =>
                    parameters.Length == 2 &&
                    IsType(parameters[0], "UnityEngine.GUIContent") &&
                    IsType(parameters[1], "System.Single"),
                preferClrOptions: false);
            _getLastRect = FindMethod(
                guiLayoutUtility,
                "GetLastRect",
                StaticMethods,
                static parameters => parameters.Length == 0,
                preferClrOptions: false);
            var rectType = _getRect.ReturnType;
            _getRectWidth = FindMethod(
                _getLastRect?.ReturnType ?? rectType,
                "get_width",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                preferClrOptions: false);
            _getRectCellWidth = RequireMethod(
                rectType,
                "get_width",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "Single Rect.get_width()");
            _getRectX = RequireMethod(
                rectType,
                "get_x",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "Single Rect.get_x()");
            _getRectY = RequireMethod(
                rectType,
                "get_y",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "Single Rect.get_y()");
            _getRectHeight = RequireMethod(
                rectType,
                "get_height",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "Single Rect.get_height()");
            _setRectX = RequireMethod(
                rectType,
                "set_x",
                InstanceMethods,
                static parameters => parameters.Length == 1 && IsType(parameters[0], "System.Single"),
                "Void Rect.set_x(Single)");
            _setRectY = RequireMethod(
                rectType,
                "set_y",
                InstanceMethods,
                static parameters => parameters.Length == 1 && IsType(parameters[0], "System.Single"),
                "Void Rect.set_y(Single)");
            _setRectWidth = RequireMethod(
                rectType,
                "set_width",
                InstanceMethods,
                static parameters => parameters.Length == 1 && IsType(parameters[0], "System.Single"),
                "Void Rect.set_width(Single)");
            _setRectHeight = RequireMethod(
                rectType,
                "set_height",
                InstanceMethods,
                static parameters => parameters.Length == 1 && IsType(parameters[0], "System.Single"),
                "Void Rect.set_height(Single)");
            var marginEdges = new[] { "left", "right", "top", "bottom" };
            _getMarginEdges = marginEdges.Select(edge => RequireMethod(
                    _getMargin.ReturnType,
                    "get_" + edge,
                    InstanceMethods,
                    static parameters => parameters.Length == 0,
                    $"Int32 get_{edge}()"))
                .ToArray();
            _setMarginEdges = marginEdges.Select(edge => RequireMethod(
                    _getMargin.ReturnType,
                    "set_" + edge,
                    InstanceMethods,
                    static parameters =>
                        parameters.Length == 1 && IsType(parameters[0], "System.Int32"),
                    $"Void set_{edge}(Int32)"))
                .ToArray();
            _keyboardFocus = Enum.ToObject(focusType, 1);
            _passiveFocus = Enum.ToObject(focusType, 0);
        }

        public bool ButtonTextWithStyle(string text, object style, object options, bool? wrapText = null)
        {
            var content = InvokeConstructor(_contentFromText, text);
            var layout = ApplyResponsiveTextLayout(
                style,
                wrapText,
                interactive: true,
                text: text);
            var effectiveOptions = NormalizeTextOptions(
                options,
                text,
                interactive: true,
                layout.UsesAutomaticHeight);
            try
            {
                return (bool)(Invoke(_doButton, null, content, style, effectiveOptions)
                    ?? throw new InvalidOperationException("GUILayout.DoButton returned null."));
            }
            finally
            {
                RestoreMobileTextLayout(style, layout);
            }
        }

        public bool ButtonText(string text, object options, bool? wrapText = null)
        {
            var skin = Invoke(_getSkin, null)
                ?? throw new InvalidOperationException("GUI.skin returned null.");
            var style = Invoke(_getButton, skin)
                ?? throw new InvalidOperationException("GUISkin.button returned null.");
            return ButtonTextWithStyle(text, style, options, wrapText);
        }

        public bool ButtonTextureWithStyle(object? image, object style, object options)
        {
            var content = InvokeConstructor(_contentFromImage, string.Empty, image, string.Empty);
            var effectiveOptions = NormalizeInteractiveOptions(options);
            return (bool)(Invoke(_doButton, null, content, style, effectiveOptions)
                ?? throw new InvalidOperationException("GUILayout.DoButton returned null."));
        }

        public bool ToggleTextWithStyle(
            bool value,
            string text,
            object style,
            object options,
            bool? wrapText = null)
        {
            var layout = ApplyResponsiveTextLayout(
                style,
                wrapText,
                interactive: true,
                text: text);
            var effectiveOptions = NormalizeTextOptions(
                options,
                text,
                interactive: true,
                layout.UsesAutomaticHeight);
            try
            {
                return (bool)(Invoke(_toggle, null, value, text, style, effectiveOptions)
                    ?? throw new InvalidOperationException("GUILayout.Toggle returned null."));
            }
            finally
            {
                RestoreMobileTextLayout(style, layout);
            }
        }

        public bool ToggleText(bool value, string text, object options, bool? wrapText = null)
        {
            var skin = Invoke(_getSkin, null)
                ?? throw new InvalidOperationException("GUI.skin returned null.");
            var style = Invoke(_getToggle, skin)
                ?? throw new InvalidOperationException("GUISkin.toggle returned null.");
            return ToggleTextWithStyle(value, text, style, options, wrapText);
        }

        public bool ToggleContent(bool value, object? content, object options, bool? wrapText = null)
        {
            var skin = Invoke(_getSkin, null)
                ?? throw new InvalidOperationException("GUI.skin returned null.");
            var style = Invoke(_getToggle, skin)
                ?? throw new InvalidOperationException("GUISkin.toggle returned null.");
            var effectiveContent = content ?? InvokeConstructor(_contentFromText, string.Empty);
            var layout = ApplyResponsiveTextLayout(
                style,
                wrapText,
                interactive: true,
                text: ReadContentText(effectiveContent));
            var effectiveOptions = NormalizeTextOptions(
                options,
                ReadContentText(effectiveContent),
                interactive: true,
                layout.UsesAutomaticHeight);
            try
            {
                var rect = Invoke(_getRect, null, effectiveContent, style, effectiveOptions)
                    ?? throw new InvalidOperationException("GUILayoutUtility.GetRect returned null.");
                return Convert.ToBoolean(Invoke(
                    _guiToggle,
                    null,
                    rect,
                    value,
                    effectiveContent,
                    style));
            }
            finally
            {
                RestoreMobileTextLayout(style, layout);
            }
        }

        public string TextArea(string text, object options)
        {
            var content = InvokeConstructor(_contentFromText, text);
            var skin = Invoke(_getSkin, null)
                ?? throw new InvalidOperationException("GUI.skin returned null.");
            var style = Invoke(_getTextArea, skin)
                ?? throw new InvalidOperationException("GUISkin.textArea returned null.");
            var layout = ApplyResponsiveTextLayout(
                style,
                wrapText: true,
                interactive: true,
                text: text);
            var effectiveOptions = NormalizeTextOptions(
                options,
                text,
                interactive: true,
                layout.UsesAutomaticHeight);
            try
            {
                var rect = Invoke(_getRect, null, content, style, effectiveOptions)
                    ?? throw new InvalidOperationException("GUILayoutUtility.GetRect returned null.");
                var controlId = Invoke(_getControlId, null, _keyboardFocus, rect)
                    ?? throw new InvalidOperationException("GUIUtility.GetControlID returned null.");
                Invoke(_doTextField, null, rect, controlId, content, true, int.MaxValue, style);
                return (string)(Invoke(_getContentText, content) ?? string.Empty);
            }
            finally
            {
                RestoreMobileTextLayout(style, layout);
            }
        }

        public void DrawTexture(object position, object image)
        {
            var content = InvokeConstructor(_contentFromImage, string.Empty, image, string.Empty);
            var style = Invoke(_getNone, null)
                ?? throw new InvalidOperationException("GUIStyle.none returned null.");
            Invoke(_labelWithContentStyle, null, position, content, style);
        }

        public void LabelContent(object content, object options, bool? wrapText = null)
        {
            var skin = Invoke(_getSkin, null)
                ?? throw new InvalidOperationException("GUI.skin returned null.");
            var style = Invoke(_getLabel, skin)
                ?? throw new InvalidOperationException("GUISkin.label returned null.");
            LabelContentWithStyle(content, style, options, wrapText);
        }

        public void LabelText(string text, object options, bool? wrapText = null)
        {
            var skin = Invoke(_getSkin, null)
                ?? throw new InvalidOperationException("GUI.skin returned null.");
            var style = Invoke(_getLabel, skin)
                ?? throw new InvalidOperationException("GUISkin.label returned null.");
            LabelTextWithStyle(text, style, options, wrapText);
        }

        public void LabelTextWithStyle(string text, object style, object options, bool? wrapText = null)
        {
            var content = InvokeConstructor(_contentFromText, text);
            LabelContentWithStyle(content, style, options, wrapText);
        }

        private void LabelContentWithStyle(object content, object style, object options, bool? wrapText)
        {
            var layout = ApplyResponsiveTextLayout(
                style,
                wrapText,
                interactive: false,
                text: ReadContentText(content));
            var effectiveOptions = NormalizeTextOptions(
                options,
                ReadContentText(content),
                interactive: false,
                layout.UsesAutomaticHeight);
            try
            {
                var rect = Invoke(_getRect, null, content, style, effectiveOptions)
                    ?? throw new InvalidOperationException("GUILayoutUtility.GetRect returned null.");
                Invoke(_labelWithContentStyle, null, rect, content, style);
            }
            finally
            {
                RestoreMobileTextLayout(style, layout);
            }
        }

        public int SelectionGrid(
            int selected,
            object texts,
            int xCount,
            object options,
            bool? wrapText = null,
            float cellWidth = 0f,
            float cellHeight = 0f,
            bool overrideHeightConstraints = false)
        {
            var labels = ReadStrings(texts);
            if (labels.Count == 0)
                return selected;

            var skin = Invoke(_getSkin, null)
                ?? throw new InvalidOperationException("GUI.skin returned null.");
            var style = Invoke(_getButton, skin)
                ?? throw new InvalidOperationException("GUISkin.button returned null.");
            var columns = Math.Max(1, xCount);
            var result = selected;
            var groupContent = InvokeConstructor(_contentFromText, string.Empty);
            var layout = ApplyResponsiveTextLayout(style, wrapText, interactive: true);
            var rows = (labels.Count + columns - 1) / columns;
            var gridHeight = ResolveSelectionGridHeight(rows, cellHeight);
            var forceGridHeight = overrideHeightConstraints || layout.UsesAutomaticHeight || rows > 1;
            var outerOptions = forceGridHeight
                ? ReplaceSelectionGridHeightConstraints(options, gridHeight)
                : options;
            try
            {
                // Reserve exactly one GUILayout entry, then draw every cell through
                // GUI.Toggle. Creating a vertical group plus one horizontal group per
                // row changed Unity's group stack under third-party OnGUI methods and
                // caused JPOV/JRP to fail with "Mismatched LayoutGroup.Ignore".
                var outerRect = Invoke(_getRect, null, groupContent, style, outerOptions)
                    ?? throw new InvalidOperationException("GUILayoutUtility.GetRect returned null.");
                var outer = ReadRect(outerRect);
                var fallbackWidth = cellWidth > 0f && float.IsFinite(cellWidth)
                    ? cellWidth * columns + PcCompatManagedResponsiveImGuiLayout.SelectionGridCellGap *
                        Math.Max(0, columns - 1)
                    : Math.Max(
                        PcCompatManagedResponsiveImGuiLayout.MinimumLayoutExtent,
                        GetMobileContentWidth());
                var renderWidth = float.IsFinite(outer.Width) && outer.Width > 0f
                    ? outer.Width
                    : fallbackWidth;
                var renderHeight = float.IsFinite(outer.Height) && outer.Height > 0f
                    ? outer.Height
                    : gridHeight;
                for (var index = 0; index < labels.Count; index++)
                {
                    var cell = PcCompatManagedSelectionGridGeometry.ResolveCell(
                        outer.X,
                        outer.Y,
                        renderWidth,
                        renderHeight,
                        index,
                        labels.Count,
                        columns,
                        PcCompatManagedResponsiveImGuiLayout.SelectionGridCellGap);
                    WriteRect(outerRect, cell);
                    var content = InvokeConstructor(_contentFromText, labels[index]);
                    var wasSelected = index == selected;
                    var activated = Convert.ToBoolean(Invoke(
                        _guiToggle,
                        null,
                        outerRect,
                        wasSelected,
                        content,
                        style));
                    // GUI.Toggle returns true for the old selected cell even when another cell was
                    // clicked. Only a false-to-true transition selects a new grid entry; otherwise a
                    // later old cell overwrites a lower-index click.
                    if (!wasSelected && activated)
                        result = index;
                }
                return result;
            }
            finally
            {
                RestoreMobileTextLayout(style, layout);
            }
        }

        public float HorizontalSlider(float value, float leftValue, float rightValue, object options)
        {
            var skin = Invoke(_getSkin, null)
                ?? throw new InvalidOperationException("GUI.skin returned null.");
            var sliderStyle = Invoke(_getHorizontalSlider, skin)
                ?? throw new InvalidOperationException("GUISkin.horizontalSlider returned null.");
            var thumbStyle = Invoke(_getHorizontalSliderThumb, skin)
                ?? throw new InvalidOperationException("GUISkin.horizontalSliderThumb returned null.");
            var content = InvokeConstructor(_contentFromText, string.Empty);
            var rect = Invoke(_getRect, null, content, sliderStyle, options)
                ?? throw new InvalidOperationException("GUILayoutUtility.GetRect returned null.");
            var controlId = Invoke(_getControlId, null, _passiveFocus, rect)
                ?? throw new InvalidOperationException("GUIUtility.GetControlID returned null.");
            var result = Invoke(
                _guiSlider,
                null,
                rect,
                value,
                0f,
                leftValue,
                rightValue,
                sliderStyle,
                thumbStyle,
                true,
                controlId,
                null);
            return result is null ? value : Convert.ToSingle(result);
        }

        public string TextField(string text, object options)
        {
            var skin = Invoke(_getSkin, null)
                ?? throw new InvalidOperationException("GUI.skin returned null.");
            var style = Invoke(_getTextField, skin)
                ?? throw new InvalidOperationException("GUISkin.textField returned null.");
            var content = InvokeConstructor(_contentFromText, text);
            var layout = ApplyResponsiveTextLayout(
                style,
                wrapText: false,
                interactive: true,
                text: text);
            var effectiveOptions = NormalizeTextOptions(
                options,
                text,
                interactive: true,
                layout.UsesAutomaticHeight);
            try
            {
                var rect = Invoke(_getRect, null, content, style, effectiveOptions)
                    ?? throw new InvalidOperationException("GUILayoutUtility.GetRect returned null.");
                return Invoke(_guiTextField, null, rect, text, int.MaxValue) as string ?? text;
            }
            finally
            {
                RestoreMobileTextLayout(style, layout);
            }
        }

        public void BeginVerticalWithStyle(object style, object options)
        {
            var content = InvokeConstructor(_contentFromText, string.Empty);
            BeginVerticalWithContentStyle(content, style, options);
        }

        public void BeginVerticalWithContentStyle(object content, object style, object options)
            => Invoke(_beginVerticalWithContentStyle, null, content, style, options);

        public void BeginVertical(object options)
            => Invoke(_beginVertical, null, options);

        public void BeginHorizontal(object options)
            => Invoke(_beginHorizontal, null, options);

        public void EndVertical()
            => Invoke(_endVertical, null);

        public void EndHorizontal()
            => Invoke(_endHorizontal, null);

        public void Space(float pixels)
            => Invoke(_space, null, pixels);

        public void FlexibleSpace()
            => Invoke(_flexibleSpace, null);

        public object CreateOption(
            PcCompatImGuiOptionKind kind,
            float value,
            Func<PcCompatImGuiOptionKind, float, IntPtr>? nativeOptionFactory)
            => kind switch
            {
                PcCompatImGuiOptionKind.Width => Invoke(_width, null, value),
                PcCompatImGuiOptionKind.MinWidth => Invoke(_minWidth, null, value),
                PcCompatImGuiOptionKind.Height => Invoke(_height, null, value),
                PcCompatImGuiOptionKind.ExpandWidth => Invoke(_expandWidth, null, value >= 0.5f),
                PcCompatImGuiOptionKind.ExpandHeight => Invoke(_expandHeight, null, value >= 0.5f),
                PcCompatImGuiOptionKind.MaxWidth or
                    PcCompatImGuiOptionKind.MinHeight or
                    PcCompatImGuiOptionKind.MaxHeight => WrapNativeOption(
                        nativeOptionFactory,
                        kind,
                        value),
                _ => throw new NotSupportedException($"Unsupported initial responsive IMGUI option: {kind}.")
            } ?? throw new InvalidOperationException($"GUILayout option factory returned null: {kind}.");

        private object WrapNativeOption(
            Func<PcCompatImGuiOptionKind, float, IntPtr>? nativeOptionFactory,
            PcCompatImGuiOptionKind kind,
            float value)
        {
            if (nativeOptionFactory is null)
            {
                throw new InvalidOperationException(
                    $"Android native GUILayout option factory is not registered for {kind}.");
            }

            var pointer = nativeOptionFactory(kind, value);
            if (pointer == IntPtr.Zero)
                throw new InvalidOperationException($"Native GUILayout option factory returned null for {kind}.");
            return _wrapNativeOption(pointer);
        }

        public object ResolveStyle(string? styleName)
            => Invoke(_styleFromName, null, styleName ?? string.Empty)
               ?? throw new InvalidOperationException("GUIStyle string conversion returned null.");

        public PcCompatImGuiMeasurement MeasureButtonText(string text)
        {
            var skin = Invoke(_getSkin, null)
                ?? throw new InvalidOperationException("GUI.skin returned null.");
            var style = Invoke(_getButton, skin)
                ?? throw new InvalidOperationException("GUISkin.button returned null.");
            return MeasureText(text, style, interactive: true, supportsTextWrapping: true);
        }

        public PcCompatImGuiMeasurement MeasureToggleText(string text)
        {
            var skin = Invoke(_getSkin, null)
                ?? throw new InvalidOperationException("GUI.skin returned null.");
            var style = Invoke(_getToggle, skin)
                ?? throw new InvalidOperationException("GUISkin.toggle returned null.");
            return MeasureText(text, style, interactive: true, supportsTextWrapping: true);
        }

        public PcCompatImGuiMeasurement MeasureToggleContent(object? content)
        {
            var skin = Invoke(_getSkin, null)
                ?? throw new InvalidOperationException("GUI.skin returned null.");
            var style = Invoke(_getToggle, skin)
                ?? throw new InvalidOperationException("GUISkin.toggle returned null.");
            var effectiveContent = content ?? InvokeConstructor(_contentFromText, string.Empty);
            return MeasureContent(
                effectiveContent,
                style,
                interactive: true,
                supportsTextWrapping: true,
                ComputeLayoutFingerprint(
                    ComputeStyleFingerprint(style),
                    ReadContentText(effectiveContent) ?? string.Empty,
                    interactive: true,
                    supportsTextWrapping: true));
        }

        public PcCompatImGuiMeasurement MeasureLabelText(string text)
        {
            var skin = Invoke(_getSkin, null)
                ?? throw new InvalidOperationException("GUI.skin returned null.");
            var style = Invoke(_getLabel, skin)
                ?? throw new InvalidOperationException("GUISkin.label returned null.");
            return MeasureText(text, style, interactive: false, supportsTextWrapping: true);
        }

        public PcCompatImGuiMeasurement MeasureLabelContent(object content)
        {
            var skin = Invoke(_getSkin, null)
                ?? throw new InvalidOperationException("GUI.skin returned null.");
            var style = Invoke(_getLabel, skin)
                ?? throw new InvalidOperationException("GUISkin.label returned null.");
            return MeasureContent(
                content,
                style,
                interactive: false,
                supportsTextWrapping: true,
                ComputeLayoutFingerprint(
                    ComputeStyleFingerprint(style),
                    ReadContentText(content) ?? string.Empty,
                    interactive: false,
                    supportsTextWrapping: true));
        }

        public PcCompatImGuiMeasurement MeasureTextField(string text)
        {
            var skin = Invoke(_getSkin, null)
                ?? throw new InvalidOperationException("GUI.skin returned null.");
            var style = Invoke(_getTextField, skin)
                ?? throw new InvalidOperationException("GUISkin.textField returned null.");
            return MeasureText(text, style, interactive: true, supportsTextWrapping: false);
        }

        public PcCompatImGuiMeasurement MeasureTextArea(string text)
        {
            var skin = Invoke(_getSkin, null)
                ?? throw new InvalidOperationException("GUI.skin returned null.");
            var style = Invoke(_getTextArea, skin)
                ?? throw new InvalidOperationException("GUISkin.textArea returned null.");
            return MeasureText(text, style, interactive: true, supportsTextWrapping: true);
        }

        public PcCompatImGuiMeasurement MeasureHorizontalSlider()
        {
            var skin = Invoke(_getSkin, null)
                ?? throw new InvalidOperationException("GUI.skin returned null.");
            var style = Invoke(_getHorizontalSlider, skin)
                ?? throw new InvalidOperationException("GUISkin.horizontalSlider returned null.");
            return MeasureText(string.Empty, style, interactive: true, supportsTextWrapping: false) with
            {
                MinimumWidth = Math.Max(PcCompatManagedResponsiveImGuiLayout.MinimumLayoutExtent, 96f),
                PreferredWidth = Math.Max(PcCompatManagedResponsiveImGuiLayout.MinimumLayoutExtent, 160f)
            };
        }

        public PcCompatSelectionGridMeasurement MeasureSelectionGrid(object texts, int xCount)
        {
            var labels = ReadStrings(texts);
            if (labels.Count == 0)
                return default;

            var skin = Invoke(_getSkin, null)
                ?? throw new InvalidOperationException("GUI.skin returned null.");
            var style = Invoke(_getButton, skin)
                ?? throw new InvalidOperationException("GUISkin.button returned null.");
            var maximumMinimum = PcCompatManagedResponsiveImGuiLayout.MinimumLayoutExtent;
            var maximumPreferred = maximumMinimum;
            var maximumHeight = PcCompatManagedResponsiveImGuiLayout.MinimumLayoutExtent;
            var fingerprint = new HashCode();
            fingerprint.Add(xCount);
            fingerprint.Add(labels.Count);
            foreach (var label in labels)
            {
                var measurement = MeasureText(
                    label,
                    style,
                    interactive: true,
                    supportsTextWrapping: true);
                maximumMinimum = Math.Max(maximumMinimum, measurement.MinimumWidth);
                maximumPreferred = Math.Max(maximumPreferred, measurement.PreferredWidth);
                maximumHeight = Math.Max(maximumHeight, measurement.PreferredHeight);
                fingerprint.Add(measurement.LayoutFingerprint);
            }

            return new PcCompatSelectionGridMeasurement(
                maximumMinimum,
                maximumPreferred,
                maximumHeight,
                labels.Count,
                fingerprint.ToHashCode());
        }

        public PcCompatImGuiMeasurement MeasureIconButton(object style)
            => MeasureText(string.Empty, style, interactive: true, supportsTextWrapping: false) with
            {
                MinimumWidth = PcCompatManagedResponsiveImGuiLayout.MinimumLayoutExtent,
                PreferredWidth = PcCompatManagedResponsiveImGuiLayout.MinimumLayoutExtent
            };

        public PcCompatImGuiMeasurement MeasureText(
            string text,
            object style,
            bool interactive,
            bool supportsTextWrapping)
        {
            text ??= string.Empty;
            var pointer = _stylePointerGetter?.Invoke(style) ?? IntPtr.Zero;
            var styleFingerprint = ComputeStyleFingerprint(style);
            var layoutFingerprint = ComputeLayoutFingerprint(
                styleFingerprint,
                text,
                interactive,
                supportsTextWrapping);
            var key = new TextMeasurementKey(
                pointer,
                pointer == IntPtr.Zero ? RuntimeHelpers.GetHashCode(style) : 0,
                GetMobileMeasurementFingerprint(),
                styleFingerprint,
                text,
                interactive,
                supportsTextWrapping);
            if (_textMeasurements.TryGetValue(key, out var cached))
                return cached;

            var measured = MeasureContent(
                InvokeConstructor(_contentFromText, text),
                style,
                interactive,
                supportsTextWrapping,
                layoutFingerprint);
            if (_textMeasurements.Count >= TextMeasurementCacheCapacity)
                _textMeasurements.Clear();
            _textMeasurements[key] = measured;
            return measured;
        }

        public float GetLastRectWidth()
        {
            if (_getLastRect is null || _getRectWidth is null)
                return 0f;
            var rect = Invoke(_getLastRect, null);
            if (rect is null)
                return 0f;
            var width = Invoke(_getRectWidth, rect);
            return width is null ? 0f : Convert.ToSingle(width);
        }

        private PcCompatImGuiMeasurement MeasureContent(
            object content,
            object style,
            bool interactive,
            bool supportsTextWrapping,
            int layoutFingerprint)
        {
            var minimum = 0f;
            var preferred = 0f;
            var preferredHeight = 0f;

            // CalcMinMaxWidth must measure the intrinsic, one-line extent. If a
            // third-party style arrives with wordWrap=true, Unity reports the
            // narrowest breakable token as its minimum and the responsive row
            // compiler can incorrectly split a line that already fits.
            var previousWordWrap = Convert.ToBoolean(Invoke(_getWordWrap, style));
            if (previousWordWrap)
                Invoke(_setWordWrap, style, false);
            try
            {
                if (_calcMinMaxWidth is not null)
                {
                    var widths = new object?[] { content, 0f, 0f };
                    InvokeWithMutableArguments(_calcMinMaxWidth, style, widths);
                    var measuredMin = widths[1] is null ? 0f : Convert.ToSingle(widths[1]);
                    var measuredMax = widths[2] is null ? 0f : Convert.ToSingle(widths[2]);
                    if (float.IsFinite(measuredMin))
                        minimum = Math.Max(minimum, Math.Max(0f, measuredMin));
                    if (float.IsFinite(measuredMax))
                        preferred = Math.Max(minimum, Math.Max(0f, measuredMax));
                }

                if (_calcHeight is not null)
                {
                    var width = Math.Max(minimum, preferred);
                    var height = Invoke(_calcHeight, style, content, width);
                    if (height is not null)
                    {
                        var measuredHeight = Convert.ToSingle(height);
                        if (float.IsFinite(measuredHeight))
                            preferredHeight = Math.Max(preferredHeight, Math.Max(0f, measuredHeight));
                    }
                }
            }
            finally
            {
                if (previousWordWrap)
                    Invoke(_setWordWrap, style, true);
            }

            // A missing generated measurement member is deliberately conservative. It
            // cannot cause a clipped first frame: the layout state machine stays stacked.
            if (preferred <= 0f)
            {
                minimum = Math.Max(minimum, PcCompatManagedResponsiveImGuiLayout.MinimumLayoutExtent);
                preferred = Math.Max(minimum, 160f);
            }

            return new PcCompatImGuiMeasurement(
                minimum,
                preferred,
                supportsTextWrapping,
                PreferredHeight: preferredHeight,
                LayoutFingerprint: layoutFingerprint);
        }

        private int ComputeStyleFingerprint(object style)
        {
            var hash = new HashCode();
            var stylePointer = _stylePointerGetter?.Invoke(style) ?? IntPtr.Zero;
            hash.Add(stylePointer);
            if (stylePointer == IntPtr.Zero)
                hash.Add(RuntimeHelpers.GetHashCode(style));
            hash.Add(Convert.ToInt32(Invoke(_getFontSize, style)));
            AddQuantized(ref hash, Convert.ToSingle(Invoke(_getFixedHeight, style)));
            hash.Add(Convert.ToBoolean(Invoke(_getWordWrap, style)));
            hash.Add(Convert.ToBoolean(Invoke(_getRichText, style)));

            var font = Invoke(_getFont, style);
            var fontPointer = font is null
                ? IntPtr.Zero
                : _fontPointerGetter?.Invoke(font) ?? IntPtr.Zero;
            hash.Add(fontPointer);
            if (font is not null && fontPointer == IntPtr.Zero)
                hash.Add(RuntimeHelpers.GetHashCode(font));

            AddRectOffsetFingerprint(ref hash, Invoke(_getMargin, style));
            AddRectOffsetFingerprint(ref hash, Invoke(_getPadding, style));
            return hash.ToHashCode();
        }

        private static int ComputeLayoutFingerprint(
            int styleFingerprint,
            string text,
            bool interactive,
            bool supportsTextWrapping)
        {
            var hash = new HashCode();
            hash.Add(GetMobileMeasurementFingerprint());
            hash.Add(styleFingerprint);
            hash.Add(text, StringComparer.Ordinal);
            hash.Add(interactive);
            hash.Add(supportsTextWrapping);
            return hash.ToHashCode();
        }

        private void AddRectOffsetFingerprint(ref HashCode hash, object? offset)
        {
            if (offset is null)
            {
                hash.Add(0);
                return;
            }

            hash.Add(1);
            foreach (var getter in _getMarginEdges)
                hash.Add(Convert.ToInt32(Invoke(getter, offset)));
        }

        private static void AddQuantized(ref HashCode hash, float value)
        {
            if (!float.IsFinite(value))
            {
                hash.Add(int.MinValue);
                return;
            }

            hash.Add((int)MathF.Round(Math.Clamp(value, -1_000_000f, 1_000_000f) * 10f));
        }

        public object GetRect(float width, float height)
        {
            var style = Invoke(_getNone, null)
                ?? throw new InvalidOperationException("GUIStyle.none returned null.");
            var options = CreateOptions(
                _getRectDimensions.GetParameters()[3].ParameterType,
                Array.Empty<object?>());
            return Invoke(_getRectDimensions, null, width, height, style, options)
                   ?? throw new InvalidOperationException("GUILayoutUtility.GetRect returned null.");
        }

        public int GetCollectionCount(object values)
        {
            if (values is ICollection collection)
                return collection.Count;

            var type = values.GetType();
            var length = type.GetProperty("Length", InstanceMethods)?.GetValue(values)
                         ?? type.GetProperty("Count", InstanceMethods)?.GetValue(values);
            return length is null ? 0 : Math.Max(0, Convert.ToInt32(length));
        }

        private List<string> ReadStrings(object texts)
        {
            if (texts is IEnumerable enumerable)
                return enumerable.Cast<object?>().Select(value => value?.ToString() ?? string.Empty).ToList();

            var type = texts.GetType();
            var length = type.GetProperty("Length", InstanceMethods)?.GetValue(texts)
                         ?? type.GetProperty("Count", InstanceMethods)?.GetValue(texts);
            if (length is null)
                throw new InvalidOperationException($"SelectionGrid texts are not enumerable: {type.FullName}.");
            var itemProperty = type.GetProperty("Item", InstanceMethods);
            var itemMethod = type.GetMethods(InstanceMethods)
                .SingleOrDefault(method =>
                    method.Name == "get_Item" &&
                    method.GetParameters() is [{ ParameterType: var parameterType }] &&
                    parameterType == typeof(int));
            if (itemProperty is null && itemMethod is null)
                throw new MissingMemberException(type.FullName, "Item");
            var count = Convert.ToInt32(length);
            var result = new List<string>(count);
            for (var index = 0; index < count; index++)
            {
                var value = itemProperty is not null
                    ? itemProperty.GetValue(texts, [index])
                    : itemMethod!.Invoke(texts, [index]);
                result.Add(value?.ToString() ?? string.Empty);
            }
            return result;
        }

        internal object CreateEmptyOptions(object options)
        {
            var type = options.GetType();
            if (_emptyOptionsByType.TryGetValue(type, out var cached))
                return cached;
            var empty = CreateOptions(type, Array.Empty<object?>());
            _emptyOptionsByType.Add(type, empty);
            return empty;
        }

        private object RemoveSelectionGridHeightConstraints(object options)
        {
            var original = ReadOptionValues(options);
            var filtered = original
                .Where(option => !PcCompatManagedResponsiveImGuiLayout.IsSelectionGridHeightConstraint(option))
                .ToArray();
            return filtered.Length == original.Count
                ? options
                : CreateOptions(options.GetType(), filtered);
        }

        private object ReplaceSelectionGridHeightConstraints(object options, float height)
        {
            var withoutHeightConstraints = RemoveSelectionGridHeightConstraints(options);
            var values = ReadOptionValues(withoutHeightConstraints).ToList();
            values.Add(Invoke(_height, null, height)
                ?? throw new InvalidOperationException("GUILayout.Height returned null."));
            return CreateOptions(options.GetType(), values);
        }

        private static float ResolveSelectionGridHeight(int rows, float cellHeight)
        {
            var baseline = GetMobileTextBaselineHeight(interactive: true);
            var safeCellHeight = float.IsFinite(cellHeight) && cellHeight > 0f
                ? cellHeight
                : 0f;
            safeCellHeight = Math.Max(
                PcCompatManagedResponsiveImGuiLayout.MinimumLayoutExtent,
                Math.Max(baseline, safeCellHeight));
            var safeRows = Math.Max(1, rows);
            var height = safeCellHeight * safeRows +
                         PcCompatManagedResponsiveImGuiLayout.SelectionGridCellGap *
                         Math.Max(0, safeRows - 1);
            return float.IsFinite(height)
                ? height
                : float.MaxValue;
        }

        private PcCompatImGuiRect ReadRect(object rect)
            => new(
                Convert.ToSingle(Invoke(_getRectX, rect)),
                Convert.ToSingle(Invoke(_getRectY, rect)),
                Convert.ToSingle(Invoke(_getRectCellWidth, rect)),
                Convert.ToSingle(Invoke(_getRectHeight, rect)));

        private void WriteRect(object rect, PcCompatImGuiRect value)
        {
            _ = Invoke(_setRectX, rect, value.X);
            _ = Invoke(_setRectY, rect, value.Y);
            _ = Invoke(_setRectWidth, rect, value.Width);
            _ = Invoke(_setRectHeight, rect, value.Height);
        }

        private object NormalizeInteractiveOptions(object options)
        {
            // Texture and glyph-only controls still use the legacy entry point. Keep
            // its public behavior while routing text-bearing controls through the
            // richer normalizer below.
            return NormalizeTextOptions(
                options,
                text: null,
                interactive: true,
                usesAutomaticHeight: false);
        }

        private object NormalizeTextOptions(
            object options,
            string? text,
            bool interactive,
            bool usesAutomaticHeight)
        {
            var minimumHeight = GetMobileTextBaselineHeight(interactive);
            if (!float.IsFinite(minimumHeight) || minimumHeight <= 0f)
                return options;

            var protectBaseline = ShouldProtectTextBaseline(text);
            if (!protectBaseline && !usesAutomaticHeight)
                return options;

            var snapshot = PcCompatManagedResponsiveImGuiLayout.ReadOptions(options);
            var hasHeightCap = snapshot.Height is not null || snapshot.MaxHeight is not null;
            var hasUnsafeHeight = snapshot.Height is { } height && height + 0.5f < minimumHeight ||
                                  snapshot.MaxHeight is { } maximum && maximum + 0.5f < minimumHeight;
            if (!hasUnsafeHeight && !(usesAutomaticHeight && hasHeightCap))
                return options;

            var original = ReadOptionValues(options);
            var filtered = original
                .Where(option => usesAutomaticHeight
                    ? !PcCompatManagedResponsiveImGuiLayout.IsSelectionGridHeightConstraint(option)
                    : !PcCompatManagedResponsiveImGuiLayout
                        .IsInteractiveHeightConstraintBelow(option, minimumHeight))
                .ToArray();
            return filtered.Length == original.Count
                ? options
                : CreateOptions(options.GetType(), filtered);
        }

        private static object CreateOptions(Type type, IReadOnlyList<object?> values)
        {
            if (type.IsArray)
            {
                var array = Array.CreateInstance(type.GetElementType()!, values.Count);
                for (var index = 0; index < values.Count; ++index)
                    array.SetValue(values[index], index);
                return array;
            }
            if (type.IsGenericType)
            {
                var argument = type.GetGenericArguments().SingleOrDefault();
                if (argument is not null)
                {
                    var array = Array.CreateInstance(argument, values.Count);
                    for (var index = 0; index < values.Count; ++index)
                        array.SetValue(values[index], index);
                    var constructor = type.GetConstructors(InstanceMethods)
                        .SingleOrDefault(candidate =>
                        {
                            var parameters = candidate.GetParameters();
                            return parameters.Length == 1 &&
                                   parameters[0].ParameterType.IsAssignableFrom(array.GetType());
                        });
                    if (constructor is not null)
                        return constructor.Invoke([array]);
                }
            }
            throw new InvalidOperationException(
                $"Cannot materialize GUILayout options for {type.FullName}.");
        }

        private static IReadOnlyList<object?> ReadOptionValues(object options)
        {
            if (options is IEnumerable enumerable)
                return enumerable.Cast<object?>().ToArray();

            var type = options.GetType();
            var length = type.GetProperty("Length", InstanceMethods)?.GetValue(options);
            var indexer = type.GetProperty("Item", InstanceMethods);
            if (length is null || indexer is null)
                throw new InvalidOperationException(
                    $"Cannot enumerate GUILayout options for {type.FullName}.");

            var count = Convert.ToInt32(length);
            var values = new object?[count];
            for (var index = 0; index < count; ++index)
                values[index] = indexer.GetValue(options, [index]);
            return values;
        }

        private (bool HeightChanged, float PreviousHeight, bool WordWrapChanged, bool PreviousWordWrap, bool UsesAutomaticHeight)
            ApplyResponsiveTextLayout(
                object style,
                bool? wrapText,
                bool interactive,
                string? text = null)
        {
            var previousWordWrap = Convert.ToBoolean(Invoke(_getWordWrap, style));
            var hasExplicitLineBreak = ContainsExplicitLineBreak(text);
            var desiredWordWrap = wrapText ?? previousWordWrap;
            var wordWrapChanged = wrapText.HasValue && previousWordWrap != desiredWordWrap;
            if (wordWrapChanged)
                Invoke(_setWordWrap, style, desiredWordWrap);

            var previousHeight = Convert.ToSingle(Invoke(_getFixedHeight, style));
            var setter = Volatile.Read(ref s_fixedHeightSetter);
            var baselineHeight = GetMobileTextBaselineHeight(interactive);
            var usesAutomaticHeight = desiredWordWrap || hasExplicitLineBreak;
            var protectBaseline = !usesAutomaticHeight &&
                                  ShouldProtectTextBaseline(text) &&
                                  float.IsFinite(baselineHeight) &&
                                  baselineHeight > 0f;
            var targetHeight = usesAutomaticHeight
                ? 0f
                : protectBaseline
                    ? Math.Max(previousHeight, baselineHeight)
                    : previousHeight;
            var heightChanged = setter is not null && targetHeight != previousHeight;
            if (heightChanged)
                setter!(style, targetHeight);

            return (heightChanged, previousHeight, wordWrapChanged, previousWordWrap, usesAutomaticHeight);
        }

        private static bool ContainsExplicitLineBreak(string? text)
            => text?.IndexOfAny(['\r', '\n']) >= 0;

        private static bool ShouldProtectTextBaseline(string? text)
            // A one-glyph arrow/icon is intentionally allowed to keep its compact
            // visual height. Null means the caller has a text-bearing style but no
            // individual string (for example SelectionGrid), so protect its baseline.
            => text is null || text.Length > 1;

        private static float GetMobileTextBaselineHeight(bool interactive)
        {
            var interactiveHeight = GetMobileInteractiveVisualHeight();
            if (!float.IsFinite(interactiveHeight) || interactiveHeight <= 0f)
                return 0f;

            // Non-interactive text needs the same CJK baseline protection as a
            // button, but can remain slightly denser when the host gives it a
            // dedicated line. The Android host has no separate font ascent API.
            return interactive
                ? interactiveHeight
                : Math.Clamp(interactiveHeight - 2f, 24f, interactiveHeight);
        }

        private string? ReadContentText(object? content)
            => content is null ? string.Empty : Invoke(_getContentText, content) as string;

        private void RestoreMobileTextLayout(
            object style,
            (bool HeightChanged, float PreviousHeight, bool WordWrapChanged, bool PreviousWordWrap, bool UsesAutomaticHeight) state)
        {
            if (state.HeightChanged)
                Volatile.Read(ref s_fixedHeightSetter)?.Invoke(style, state.PreviousHeight);
            if (state.WordWrapChanged)
                Invoke(_setWordWrap, style, state.PreviousWordWrap);
        }

        public void SetNormal(object style, object state)
        {
            var targetState = Invoke(_getNormal, style)
                ?? throw new InvalidOperationException("GUIStyle.normal returned null.");
            var color = Invoke(_getTextColor, state)
                ?? throw new InvalidOperationException("GUIStyleState.textColor returned null.");
            Invoke(_setTextColor, targetState, color);
        }

        public void SetFontSize(object style, int value)
            => Invoke(_setFontSize, style, value);

        public void SetMargin(object style, object margin)
        {
            var targetMargin = Invoke(_getMargin, style)
                ?? throw new InvalidOperationException("GUIStyle.margin returned null.");
            for (var index = 0; index < _getMarginEdges.Length; index++)
            {
                var value = Invoke(_getMarginEdges[index], margin)
                    ?? throw new InvalidOperationException(
                        $"RectOffset edge {_getMarginEdges[index].Name} returned null.");
                Invoke(
                    _setMarginEdges[index],
                    targetMargin,
                ScaleDimension(Convert.ToInt32(value)));
            }
        }

        private readonly record struct TextMeasurementKey(
            IntPtr NativeStylePointer,
            int ManagedStyleIdentity,
            int MobileStyleFingerprint,
            int StyleFingerprint,
            string Text,
            bool Interactive,
            bool SupportsTextWrapping);

        private static Type RequireType(Assembly assembly, string name)
            => assembly.GetType(name, throwOnError: false, ignoreCase: false)
               ?? throw new MissingMemberException(assembly.FullName, name);

        private static ConstructorInfo RequireConstructor(
            Type type,
            Func<ParameterInfo[], bool> predicate,
            string signature)
            => type.GetConstructors(InstanceMethods).SingleOrDefault(constructor =>
                   predicate(constructor.GetParameters()))
               ?? throw new MissingMethodException(type.FullName, signature);

        private static Func<IntPtr, object> CreateNativePointerWrapper(ConstructorInfo constructor)
        {
            var pointer = Expression.Parameter(typeof(IntPtr), "pointer");
            var construct = Expression.New(constructor, pointer);
            return Expression.Lambda<Func<IntPtr, object>>(
                    Expression.Convert(construct, typeof(object)),
                    pointer)
                .Compile();
        }

        private static Func<object, IntPtr>? CreateNativePointerGetter(Type type)
        {
            var property = type.GetProperty("Pointer", InstanceMethods);
            if (property?.GetMethod is null || property.PropertyType != typeof(IntPtr))
                return null;

            var value = Expression.Parameter(typeof(object), "value");
            var read = Expression.Property(Expression.Convert(value, type), property);
            return Expression.Lambda<Func<object, IntPtr>>(
                    Expression.Convert(read, typeof(IntPtr)),
                    value)
                .Compile();
        }

        private static MethodInfo RequireMethod(
            Type type,
            string name,
            BindingFlags flags,
            Func<ParameterInfo[], bool> predicate,
            string signature,
            bool preferClrOptions = false)
        {
            var candidates = type.GetMethods(flags)
                .Where(method => method.Name == name && predicate(method.GetParameters()))
                .ToArray();
            if (candidates.Length == 1)
                return candidates[0];
            if (preferClrOptions)
            {
                var clrCandidates = candidates.Where(method =>
                        method.GetParameters().Any(parameter =>
                            IsOptions(parameter) && parameter.ParameterType.IsArray))
                    .ToArray();
                if (clrCandidates.Length == 1)
                    return clrCandidates[0];
            }

            throw new MissingMethodException(
                type.FullName,
                $"{signature}; compatible candidates={candidates.Length}");
        }

        private static MethodInfo? FindMethod(
            Type type,
            string name,
            BindingFlags flags,
            Func<ParameterInfo[], bool> predicate,
            bool preferClrOptions)
        {
            var candidates = type.GetMethods(flags)
                .Where(method => method.Name == name && predicate(method.GetParameters()))
                .ToArray();
            if (candidates.Length == 1)
                return candidates[0];
            if (preferClrOptions)
            {
                var clrCandidates = candidates.Where(method =>
                        method.GetParameters().Any(parameter =>
                            IsOptions(parameter) && parameter.ParameterType.IsArray))
                    .ToArray();
                if (clrCandidates.Length == 1)
                    return clrCandidates[0];
            }
            return null;
        }

        private static bool IsType(ParameterInfo parameter, string fullName)
            => parameter.ParameterType.FullName == fullName;

        private static bool IsSingleByRef(ParameterInfo parameter)
            => parameter.ParameterType.IsByRef &&
               parameter.ParameterType.GetElementType()?.FullName == "System.Single";

        private static bool IsOptions(ParameterInfo parameter)
        {
            var type = parameter.ParameterType;
            if (type.IsArray)
                return type.GetElementType()?.FullName == "UnityEngine.GUILayoutOption";
            return type.IsGenericType &&
                   type.GetGenericTypeDefinition().FullName is
                       "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1" &&
                   type.GetGenericArguments()[0].FullName == "UnityEngine.GUILayoutOption";
        }

        private static object InvokeConstructor(ConstructorInfo constructor, params object?[] arguments)
            => InvokeCore(constructor, null, arguments)
               ?? throw new InvalidOperationException(
                   $"Constructor returned null: {constructor.DeclaringType?.FullName}.");

        private static object? Invoke(MethodInfo method, object? instance, params object?[] arguments)
            => InvokeCore(method, instance, arguments);

        private static void InvokeWithMutableArguments(
            MethodInfo method,
            object? instance,
            object?[] arguments)
        {
            try
            {
                method.Invoke(instance, arguments);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        private static object? InvokeCore(
            MethodBase method,
            object? instance,
            object?[] arguments)
        {
            var parameters = method.GetParameters();
            var prepared = new object?[arguments.Length];
            for (var index = 0; index < arguments.Length; index++)
                prepared[index] = CoerceArgument(parameters[index].ParameterType, arguments[index]);

            try
            {
                return method switch
                {
                    ConstructorInfo constructor => constructor.Invoke(prepared),
                    MethodInfo callable => callable.Invoke(instance, prepared),
                    _ => throw new NotSupportedException($"Unsupported reflection member: {method}.")
                };
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        private static object? CoerceArgument(Type expected, object? value)
        {
            if (value is null || expected.IsInstanceOfType(value))
                return value;

            var sourceType = value.GetType();
            var constructor = expected.GetConstructors(InstanceMethods)
                .SingleOrDefault(candidate =>
                {
                    var parameters = candidate.GetParameters();
                    return parameters.Length == 1 &&
                           parameters[0].ParameterType.IsAssignableFrom(sourceType);
                });
            if (constructor is not null)
                return constructor.Invoke([value]);

            throw new InvalidCastException(
                $"Cannot pass {sourceType.FullName} to {expected.FullName}.");
        }
    }

    private static float NormalizeScale(float value)
        => float.IsFinite(value) && value > 0f ? Math.Clamp(value, 1f, 4f) : 1f;

    private static float NormalizeTouchHeight(float value)
        => float.IsFinite(value) && value > 0f ? Math.Clamp(value, 24f, 96f) : 0f;

    private static float NormalizeInteractiveVisualHeight(float value)
        => float.IsFinite(value) && value > 0f ? Math.Clamp(value, 20f, 64f) : 0f;

    private static float NormalizeContentWidth(float value)
        => float.IsFinite(value) && value > 0f ? Math.Clamp(value, 64f, 4096f) : 0f;

    private static float ScaleDimension(float value)
        => value == 0f ? 0f : value * Math.Max(1f, t_mobileDimensionScale);

    private static int ScaleDimension(int value)
        => value == 0
            ? 0
            : (int)Math.Clamp(
                MathF.Round(value * Math.Max(1f, t_mobileDimensionScale)),
                int.MinValue,
                int.MaxValue);

    private static int ScaleFont(int value)
        => value <= 0
            ? value
            : (int)Math.Clamp(
                MathF.Round(value * Math.Max(1f, t_mobileFontScale)),
                1f,
                256f);
}

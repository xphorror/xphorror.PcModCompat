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
    [ThreadStatic] private static float t_mobileDimensionScale;
    [ThreadStatic] private static float t_mobileFontScale;
    [ThreadStatic] private static float t_mobileTouchHeight;

    internal static (float Dimension, float Font, float TouchHeight) EnterMobileSettingsScale(
        float dimensionScale,
        float fontScale,
        float touchHeight)
    {
        var previous = (t_mobileDimensionScale, t_mobileFontScale, t_mobileTouchHeight);
        t_mobileDimensionScale = NormalizeScale(dimensionScale);
        t_mobileFontScale = NormalizeScale(fontScale);
        t_mobileTouchHeight = NormalizeTouchHeight(touchHeight);
        return previous;
    }

    internal static void ExitMobileSettingsScale(
        (float Dimension, float Font, float TouchHeight) previous)
    {
        t_mobileDimensionScale = previous.Dimension;
        t_mobileFontScale = previous.Font;
        t_mobileTouchHeight = previous.TouchHeight;
    }

    private static float GetMobileTouchHeight() => t_mobileTouchHeight;

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
        var activated = Backends.GetValue(style.GetType().Assembly, static assembly => new Backend(assembly))
            .ButtonTextWithStyle(text ?? string.Empty, style, options);
        if (activated)
        {
            PcCompatLegacyInputBridge.NotifySettingsButtonActivated(
                nameof(ButtonTextWithStyle),
                text);
        }
        return activated;
    }

    public static bool ButtonText(string text, object options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var elementType = options.GetType().GetElementType()
            ?? throw new InvalidOperationException(
                $"GUILayout options are not a CLR array: {options.GetType().FullName}.");
        var activated = Backends.GetValue(
                elementType.Assembly,
                static assembly => new Backend(assembly))
            .ButtonText(text ?? string.Empty, options);
        if (activated)
            PcCompatLegacyInputBridge.NotifySettingsButtonActivated(nameof(ButtonText), text);
        return activated;
    }

    public static bool ButtonTextureWithStyle(object? image, object style, object options)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(options);
        var activated = Backends.GetValue(style.GetType().Assembly, static assembly => new Backend(assembly))
            .ButtonTextureWithStyle(image, style, options);
        if (activated)
        {
            PcCompatLegacyInputBridge.NotifySettingsButtonActivated(
                nameof(ButtonTextureWithStyle),
                image?.GetType().FullName);
        }
        return activated;
    }

    public static bool ToggleTextWithStyle(bool value, string text, object style, object options)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(options);
        return Backends.GetValue(style.GetType().Assembly, static assembly => new Backend(assembly))
            .ToggleTextWithStyle(value, text ?? string.Empty, style, options);
    }

    public static string TextArea(string text, object options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var elementType = options.GetType().GetElementType()
            ?? throw new InvalidOperationException(
                $"GUILayout options are not a CLR array: {options.GetType().FullName}.");
        return Backends.GetValue(elementType.Assembly, static assembly => new Backend(assembly))
            .TextArea(text ?? string.Empty, options);
    }

    private sealed class Backend
    {
        private const BindingFlags StaticMethods =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags InstanceMethods =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private readonly ConstructorInfo _contentFromText;
        private readonly ConstructorInfo _contentFromImage;
        private readonly MethodInfo _doButton;
        private readonly MethodInfo _toggle;
        private readonly MethodInfo _getSkin;
        private readonly MethodInfo _getButton;
        private readonly MethodInfo _getTextArea;
        private readonly MethodInfo _getRect;
        private readonly MethodInfo _getControlId;
        private readonly MethodInfo _doTextField;
        private readonly MethodInfo _getContentText;
        private readonly MethodInfo _getNormal;
        private readonly MethodInfo _getMargin;
        private readonly MethodInfo _getTextColor;
        private readonly MethodInfo _setTextColor;
        private readonly MethodInfo _setFontSize;
        private readonly MethodInfo _getFixedHeight;
        private readonly MethodInfo[] _getMarginEdges;
        private readonly MethodInfo[] _setMarginEdges;
        private readonly object _keyboardFocus;

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
            _getControlId = RequireMethod(
                guiUtility,
                "GetControlID",
                StaticMethods,
                static parameters =>
                    parameters.Length == 2 &&
                    IsType(parameters[0], "UnityEngine.FocusType") &&
                    IsType(parameters[1], "UnityEngine.Rect"),
                "Int32 GetControlID(FocusType, Rect)");
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
            _getMargin = RequireMethod(
                guiStyle,
                "get_margin",
                InstanceMethods,
                static parameters => parameters.Length == 0,
                "RectOffset get_margin()");
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
        }

        public bool ButtonTextWithStyle(string text, object style, object options)
        {
            var content = InvokeConstructor(_contentFromText, text);
            var height = ApplyMinimumTouchHeight(style);
            try
            {
                return (bool)(Invoke(_doButton, null, content, style, options)
                    ?? throw new InvalidOperationException("GUILayout.DoButton returned null."));
            }
            finally
            {
                RestoreMinimumTouchHeight(style, height);
            }
        }

        public bool ButtonText(string text, object options)
        {
            var skin = Invoke(_getSkin, null)
                ?? throw new InvalidOperationException("GUI.skin returned null.");
            var style = Invoke(_getButton, skin)
                ?? throw new InvalidOperationException("GUISkin.button returned null.");
            return ButtonTextWithStyle(text, style, options);
        }

        public bool ButtonTextureWithStyle(object? image, object style, object options)
        {
            var content = InvokeConstructor(_contentFromImage, string.Empty, image, string.Empty);
            var height = ApplyMinimumTouchHeight(style);
            try
            {
                return (bool)(Invoke(_doButton, null, content, style, options)
                    ?? throw new InvalidOperationException("GUILayout.DoButton returned null."));
            }
            finally
            {
                RestoreMinimumTouchHeight(style, height);
            }
        }

        public bool ToggleTextWithStyle(bool value, string text, object style, object options)
        {
            var height = ApplyMinimumTouchHeight(style);
            try
            {
                return (bool)(Invoke(_toggle, null, value, text, style, options)
                    ?? throw new InvalidOperationException("GUILayout.Toggle returned null."));
            }
            finally
            {
                RestoreMinimumTouchHeight(style, height);
            }
        }

        public string TextArea(string text, object options)
        {
            var content = InvokeConstructor(_contentFromText, text);
            var skin = Invoke(_getSkin, null)
                ?? throw new InvalidOperationException("GUI.skin returned null.");
            var style = Invoke(_getTextArea, skin)
                ?? throw new InvalidOperationException("GUISkin.textArea returned null.");
            var height = ApplyMinimumTouchHeight(style);
            try
            {
                var rect = Invoke(_getRect, null, content, style, options)
                    ?? throw new InvalidOperationException("GUILayoutUtility.GetRect returned null.");
                var controlId = Invoke(_getControlId, null, _keyboardFocus, rect)
                    ?? throw new InvalidOperationException("GUIUtility.GetControlID returned null.");
                Invoke(_doTextField, null, rect, controlId, content, true, int.MaxValue, style);
                return (string)(Invoke(_getContentText, content) ?? string.Empty);
            }
            finally
            {
                RestoreMinimumTouchHeight(style, height);
            }
        }

        private (bool Changed, float Previous) ApplyMinimumTouchHeight(object style)
        {
            var minimum = GetMobileTouchHeight();
            if (minimum <= 0f)
                return default;
            var previous = Convert.ToSingle(Invoke(_getFixedHeight, style));
            if (previous >= minimum)
                return default;
            var setter = Volatile.Read(ref s_fixedHeightSetter);
            if (setter == null)
                return default;
            setter(style, minimum);
            return (true, previous);
        }

        private static void RestoreMinimumTouchHeight(
            object style,
            (bool Changed, float Previous) state)
        {
            if (!state.Changed)
                return;
            Volatile.Read(ref s_fixedHeightSetter)?.Invoke(style, state.Previous);
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

        private static Type RequireType(Assembly assembly, string name)
        {
            var type = assembly.GetType(name, throwOnError: false, ignoreCase: false);
            if (type is not null)
                return type;

            var loadContext = AssemblyLoadContext.GetLoadContext(assembly);
            if (loadContext is not null)
            {
                foreach (var loaded in loadContext.Assemblies)
                {
                    type = loaded.GetType(name, throwOnError: false, ignoreCase: false);
                    if (type is not null)
                        return type;
                }
            }

            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                try
                {
                    var referenced = loadContext?.LoadFromAssemblyName(reference)
                                     ?? Assembly.Load(reference);
                    type = referenced.GetType(name, throwOnError: false, ignoreCase: false);
                    if (type is not null)
                        return type;
                }
                catch (Exception exception) when (
                    exception is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    // Other references may still own the generated Unity proxy type.
                }
            }

            throw new MissingMemberException(assembly.FullName, name);
        }

        private static ConstructorInfo RequireConstructor(
            Type type,
            Func<ParameterInfo[], bool> predicate,
            string signature)
            => type.GetConstructors(InstanceMethods).SingleOrDefault(constructor =>
                   predicate(constructor.GetParameters()))
               ?? throw new MissingMethodException(type.FullName, signature);

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

        private static bool IsType(ParameterInfo parameter, string fullName)
            => parameter.ParameterType.FullName == fullName;

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

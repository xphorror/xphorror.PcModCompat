using System.Globalization;
using System.Reflection;

namespace UnityModManagerNet;

public static class PcCompatSettingsUiBridge
{
    private static object? s_backend;
    private static MethodInfo? s_beginFrame;
    private static MethodInfo? s_endFrame;
    private static MethodInfo? s_abortFrame;
    private static MethodInfo? s_canApplyStructureChanges;
    private static MethodInfo? s_releaseInputFocus;
    private static MethodInfo? s_toggle;
    private static MethodInfo? s_text;
    private static MethodInfo? s_number;
    private static MethodInfo? s_sliderNumber;
    private static MethodInfo? s_enum;
    private static MethodInfo? s_section;
    private static MethodInfo? s_beginSectionBody;
    private static MethodInfo? s_endSectionBody;
    private static MethodInfo? s_button;
    private static MethodInfo? s_label;

    public static void Register(object backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        var type = backend.GetType();
        s_beginFrame = Require(type, "BeginFrame", typeof(string));
        s_endFrame = Require(type, "EndFrame");
        s_abortFrame = Require(type, "AbortFrame");
        s_canApplyStructureChanges = Require(type, "CanApplyStructureChanges");
        s_releaseInputFocus = Require(type, "ReleaseInputFocus");
        s_toggle = Require(type, "Toggle", typeof(bool), typeof(string));
        s_text = Require(type, "Text", typeof(string), typeof(string));
        s_number = Require(
            type,
            "Number",
            typeof(string),
            typeof(string),
            typeof(double),
            typeof(double),
            typeof(double),
            typeof(bool));
        s_sliderNumber = Require(
            type,
            "SliderNumber",
            typeof(string),
            typeof(string),
            typeof(double),
            typeof(double),
            typeof(bool));
        s_enum = Require(type, "Enum", typeof(string), typeof(string), typeof(string[]));
        s_section = Require(
            type,
            "Section",
            typeof(bool),
            typeof(bool),
            typeof(bool),
            typeof(bool),
            typeof(string));
        s_beginSectionBody = Require(type, "BeginSectionBody");
        s_endSectionBody = Require(type, "EndSectionBody");
        s_button = Require(type, "Button", typeof(string));
        s_label = Require(type, "Label", typeof(string));
        Volatile.Write(ref s_backend, backend);
    }

    public static void BeginFrame(string title)
        => Invoke(s_beginFrame, title);

    public static PcCompatSettingsFrameAction EndFrame()
    {
        var result = Invoke(s_endFrame);
        return result is int value
            ? (PcCompatSettingsFrameAction)value
            : PcCompatSettingsFrameAction.None;
    }

    public static void AbortFrame()
        => Invoke(s_abortFrame);

    public static bool CanApplyStructureChanges()
        => Invoke(s_canApplyStructureChanges) is true;

    public static void ReleaseInputFocus()
        => Invoke(s_releaseInputFocus);

    public static bool Toggle(bool value, string label)
        => Invoke(s_toggle, value, label) is bool next ? next : value;

    public static string Text(string value, string label)
        => Invoke(s_text, value, label) as string ?? value;

    public static string Number(
        string value,
        string label,
        double min,
        double max,
        double step,
        bool integral)
        => Invoke(s_number, value, label, min, max, step, integral) as string ?? value;

    public static string SliderNumber(
        string value,
        string label,
        double min,
        double max,
        bool integral)
        => Invoke(s_sliderNumber, value, label, min, max, integral) as string ?? value;

    public static string Enum(string value, string label, string[] values)
        => Invoke(s_enum, value, label, values) as string ?? value;

    public static int Section(
        bool enabled,
        bool expanded,
        bool canEnable,
        bool canExpand,
        string label)
        => Invoke(s_section, enabled, expanded, canEnable, canExpand, label) is int state
            ? state
             : (enabled ? 1 : 0) | (expanded ? 2 : 0);

    public static void BeginSectionBody()
        => Invoke(s_beginSectionBody);

    public static void EndSectionBody()
        => Invoke(s_endSectionBody);

    public static bool Button(string label)
        => Invoke(s_button, label) is true;

    public static void Label(string label)
        => Invoke(s_label, label);

    public static string SaveLabel =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "保存" : "Save";

    public static string CloseLabel =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "关闭" : "Close";

    private static object? Invoke(MethodInfo? method, params object?[]? arguments)
    {
        var backend = Volatile.Read(ref s_backend)
            ?? throw new InvalidOperationException(
                "PcCompat original settings UI backend is not registered.");
        return method!.Invoke(backend, arguments);
    }

    private static MethodInfo Require(Type type, string name, params Type[] parameters)
        => type.GetMethod(
               name,
               BindingFlags.Public | BindingFlags.Instance,
               binder: null,
               types: parameters,
               modifiers: null)
           ?? throw new MissingMethodException(type.FullName, name);
}

[Flags]
public enum PcCompatSettingsFrameAction
{
    None = 0,
    Save = 1,
    Close = 2
}

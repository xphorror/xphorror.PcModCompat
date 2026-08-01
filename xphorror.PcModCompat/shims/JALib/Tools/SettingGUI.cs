using JALib.Core;
using System.Globalization;
using UnityModManagerNet;

namespace JALib.Tools;

public class SettingGUI
{
    public SettingGUI(JAMod mod)
    {
        Mod = mod;
    }

    public JAMod Mod { get; }

    public void AddSettingToggle(ref bool value, string label, Action? onChange = null)
    {
        var next = PcCompatSettingsUiBridge.Toggle(value, label);
        if (next == value)
            return;
        value = next;
        onChange?.Invoke();
        Mod.SaveSetting();
    }

    public void AddSettingSliderFloat(ref float value, float defaultValue, ref string? text, string label, float min, float max, Action? onChange = null)
    {
        text ??= value.ToString("0.###", CultureInfo.InvariantCulture);
        var nextText = PcCompatSettingsUiBridge.SliderNumber(
            text,
            label,
            min,
            max,
            integral: false);
        text = nextText;
        if (!float.TryParse(nextText, NumberStyles.Float, CultureInfo.InvariantCulture, out var next))
            return;
        next = Math.Clamp(next, min, max);
        if (next.Equals(value))
            return;
        value = next;
        onChange?.Invoke();
        Mod.SaveSetting();
    }

    public void AddSettingFloat(ref float value, float defaultValue, ref string? text, string label, float min, Action? onChange = null)
    {
        ApplyFloat(ref value, ref text, label, min, null, onChange);
    }

    public void AddSettingFloat(ref float value, float defaultValue, ref string? text, string label, float min, float max, Action? onChange = null)
    {
        ApplyFloat(ref value, ref text, label, min, max, onChange);
    }

    public void AddSettingInt(ref int value, int defaultValue, ref string? text, string label, int min, Action? onChange = null)
    {
        ApplyInt(ref value, ref text, label, min, null, onChange);
    }

    public void AddSettingInt(ref int value, int defaultValue, ref string? text, string label, int min, int max, Action? onChange = null)
    {
        ApplyInt(ref value, ref text, label, min, max, onChange);
    }

    public void AddSettingString(ref string value, string defaultValue, string label, Action? onChange = null)
    {
        value ??= defaultValue;
        var next = PcCompatSettingsUiBridge.Text(value, label);
        if (string.Equals(next, value, StringComparison.Ordinal))
            return;
        value = next;
        onChange?.Invoke();
        Mod.SaveSetting();
    }

    public void AddSettingEnum<T>(ref T value, string label, Action? onChange = null)
        where T : Enum
    {
        AddSettingEnum(ref value, label, (T[])Enum.GetValues(typeof(T)), onChange);
    }

    public void AddSettingEnum<T>(ref T value, string label, T[] values, Action? onChange = null)
        where T : Enum
    {
        if (values == null || values.Length == 0)
            return;
        var names = values.Select(item => item.ToString()).ToArray();
        var selected = PcCompatSettingsUiBridge.Enum(value.ToString(), label, names);
        var index = Array.FindIndex(
            values,
            item => string.Equals(item.ToString(), selected, StringComparison.Ordinal));
        if (index < 0 || values[index].Equals(value))
            return;
        value = values[index];
        onChange?.Invoke();
        Mod.SaveSetting();
    }

    public void AddSettingToggleInt(
        ref int value,
        int defaultValue,
        ref bool enabled,
        ref string? text,
        string label,
        int min = int.MinValue,
        int max = int.MaxValue,
        Action? onChange = null)
    {
        var nextEnabled = PcCompatSettingsUiBridge.Toggle(enabled, label);
        if (nextEnabled != enabled)
        {
            enabled = nextEnabled;
            onChange?.Invoke();
            Mod.SaveSetting();
        }
        if (enabled)
            ApplyInt(ref value, ref text, string.Empty, min, max, onChange);
    }

    public void AddSettingSliderInt(
        ref int value,
        int defaultValue,
        ref string? text,
        string label,
        int min,
        int max,
        Action? onChange = null)
    {
        text ??= value.ToString(CultureInfo.InvariantCulture);
        var nextText = PcCompatSettingsUiBridge.SliderNumber(
            text,
            label,
            min,
            max,
            integral: true);
        text = nextText;
        if (!int.TryParse(nextText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var next))
            return;
        next = Math.Clamp(next, min, max);
        if (next == value)
            return;
        value = next;
        onChange?.Invoke();
        Mod.SaveSetting();
    }

    public void AddSettingLong(
        ref long value,
        long defaultValue,
        ref string? text,
        string label,
        long min = long.MinValue,
        long max = long.MaxValue,
        Action? onChange = null)
    {
        text ??= value.ToString(CultureInfo.InvariantCulture);
        var nextText = PcCompatSettingsUiBridge.Number(
            text,
            label,
            min,
            max,
            CalculateStep(min, max, integral: true),
            integral: true);
        text = nextText;
        if (!long.TryParse(nextText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var next))
            return;
        next = Math.Clamp(next, min, max);
        if (next == value)
            return;
        value = next;
        onChange?.Invoke();
        Mod.SaveSetting();
    }

    public void AddSettingDouble(
        ref double value,
        double defaultValue,
        ref string? text,
        string label,
        double min = double.MinValue,
        double max = double.MaxValue,
        Action? onChange = null)
    {
        text ??= value.ToString("0.###", CultureInfo.InvariantCulture);
        var nextText = PcCompatSettingsUiBridge.Number(
            text,
            label,
            min,
            max,
            CalculateStep(min, max, integral: false),
            integral: false);
        text = nextText;
        if (!double.TryParse(nextText, NumberStyles.Float, CultureInfo.InvariantCulture, out var next))
            return;
        next = Math.Clamp(next, min, max);
        if (next.Equals(value))
            return;
        value = next;
        onChange?.Invoke();
        Mod.SaveSetting();
    }

    private void ApplyFloat(
        ref float value,
        ref string? text,
        string label,
        float min,
        float? max,
        Action? onChange)
    {
        text ??= value.ToString("0.###", CultureInfo.InvariantCulture);
        var nextText = PcCompatSettingsUiBridge.Number(
            text,
            label,
            min,
            max ?? double.NaN,
            max.HasValue ? CalculateStep(min, max.Value, integral: false) : 0.1d,
            integral: false);
        text = nextText;
        if (!float.TryParse(nextText, NumberStyles.Float, CultureInfo.InvariantCulture, out var next))
            return;
        next = max.HasValue ? Math.Clamp(next, min, max.Value) : Math.Max(min, next);
        if (next.Equals(value))
            return;
        value = next;
        onChange?.Invoke();
        Mod.SaveSetting();
    }

    private void ApplyInt(
        ref int value,
        ref string? text,
        string label,
        int min,
        int? max,
        Action? onChange)
    {
        text ??= value.ToString(CultureInfo.InvariantCulture);
        var nextText = PcCompatSettingsUiBridge.Number(
            text,
            label,
            min,
            max ?? double.NaN,
            max.HasValue ? CalculateStep(min, max.Value, integral: true) : 1d,
            integral: true);
        text = nextText;
        if (!int.TryParse(nextText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var next))
            return;
        next = max.HasValue ? Math.Clamp(next, min, max.Value) : Math.Max(min, next);
        if (next == value)
            return;
        value = next;
        onChange?.Invoke();
        Mod.SaveSetting();
    }

    private static double CalculateStep(double min, double max, bool integral)
    {
        var range = max - min;
        if (!double.IsFinite(range) || range <= 0d)
            return integral ? 1d : 0.1d;
        var raw = range / 100d;
        if (integral)
            return Math.Max(1d, Math.Round(raw));
        var magnitude = Math.Pow(10d, Math.Floor(Math.Log10(raw)));
        var normalized = raw / magnitude;
        var nice = normalized <= 1d ? 1d : normalized <= 2d ? 2d : normalized <= 5d ? 5d : 10d;
        return nice * magnitude;
    }
}

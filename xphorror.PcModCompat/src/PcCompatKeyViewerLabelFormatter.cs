using System.Globalization;

namespace Xphorror.PcModCompat;

public static class PcCompatKeyViewerLabelFormatter
{
    public static string Format(PcCompatInputIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!int.TryParse(
                identity.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return identity.Value;
        }
        return identity.Kind switch
        {
            PcCompatInputIdentityKind.UnityKeyCode => FormatUnityKeyCode(value),
            PcCompatInputIdentityKind.WindowsVirtualKey => FormatWindowsVirtualKey(value),
            PcCompatInputIdentityKind.MouseButton => $"M{value}",
            PcCompatInputIdentityKind.ControllerControl => $"C{value}",
            PcCompatInputIdentityKind.ActionId => $"A{value}",
            _ => $"K{value}"
        };
    }

    public static string[] CreateTouchLabels(int laneCount)
    {
        var labels = new string[laneCount];
        for (var index = 0; index < labels.Length; ++index)
            labels[index] = $"T{index + 1}";
        return labels;
    }

    public static string[] CreateExternalLabels(
        PcCompatKeyViewerLoweredConsumerPlan plan,
        int laneCount)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var labels = new string[laneCount];
        for (var index = 0; index < labels.Length; ++index)
        {
            var lane = plan.Lanes.FirstOrDefault(candidate => candidate.Lane == index);
            var identity = lane?.Identities.FirstOrDefault();
            labels[index] = identity == null ? $"K{index + 1}" : Format(identity);
        }
        return labels;
    }

    private static string FormatUnityKeyCode(int value)
    {
        if (value is >= 97 and <= 122)
            return ((char)(value - 32)).ToString();
        if (value is >= 48 and <= 57)
            return ((char)value).ToString();
        if (value is >= 256 and <= 265)
            return $"Num{value - 256}";
        if (value is >= 282 and <= 296)
            return $"F{value - 281}";
        if (value is >= 323 and <= 329)
            return $"M{value - 323}";
        return value switch
        {
            0 => "None",
            8 => "Back",
            9 => "Tab",
            13 => "Enter",
            19 => "Pause",
            27 => "Esc",
            32 => "Space",
            39 => "'",
            43 => "+",
            44 => ",",
            45 => "-",
            46 => ".",
            47 => "/",
            59 => ";",
            61 => "=",
            91 => "[",
            92 => "\\",
            93 => "]",
            96 => "`",
            127 => "Del",
            266 => "Num.",
            267 => "Num/",
            268 => "Num*",
            269 => "Num-",
            270 => "Num+",
            271 => "NumEnter",
            272 => "Num=",
            273 => "Up",
            274 => "Down",
            275 => "Right",
            276 => "Left",
            277 => "Ins",
            278 => "Home",
            279 => "End",
            280 => "PgUp",
            281 => "PgDown",
            300 => "NumLock",
            301 => "CapsLock",
            302 => "ScrollLock",
            303 => "RShift",
            304 => "LShift",
            305 => "RCtrl",
            306 => "LCtrl",
            307 => "RAlt",
            308 => "LAlt",
            309 => "RCmd",
            310 => "LCmd",
            311 => "LWin",
            312 => "RWin",
            319 => "Menu",
            _ => $"Key{value}"
        };
    }

    private static string FormatWindowsVirtualKey(int value)
    {
        if (value is >= 0x41 and <= 0x5A || value is >= 0x30 and <= 0x39)
            return ((char)value).ToString();
        if (value is >= 0x60 and <= 0x69)
            return $"Num{value - 0x60}";
        if (value is >= 0x70 and <= 0x87)
            return $"F{value - 0x6F}";
        return value switch
        {
            0x08 => "Back",
            0x09 => "Tab",
            0x0D => "Enter",
            0x10 => "Shift",
            0x11 => "Ctrl",
            0x12 => "Alt",
            0x13 => "Pause",
            0x14 => "CapsLock",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PgUp",
            0x22 => "PgDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Ins",
            0x2E => "Del",
            0x5B => "LWin",
            0x5C => "RWin",
            0x5D => "Menu",
            0x6A => "Num*",
            0x6B => "Num+",
            0x6D => "Num-",
            0x6E => "Num.",
            0x6F => "Num/",
            0x90 => "NumLock",
            0x91 => "ScrollLock",
            0xA0 => "LShift",
            0xA1 => "RShift",
            0xA2 => "LCtrl",
            0xA3 => "RCtrl",
            0xA4 => "LAlt",
            0xA5 => "RAlt",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => $"VK{value:X2}"
        };
    }
}

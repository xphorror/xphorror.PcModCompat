namespace Xphorror.PcModCompat;

public readonly record struct PcCompatCanonicalInputIdentity(
    PcCompatInputIdentityKind Kind,
    int Value);

/// <summary>
/// Maps Android KeyEvent identities to the two polling domains currently
/// supported by the rewritten PC MOD surface. The mapping is layout-stable and
/// does not guess from Linux scan codes when Android did not provide a known key.
/// </summary>
public static class PcCompatExternalInputIdentityMapper
{
    public static IReadOnlyList<PcCompatCanonicalInputIdentity> Map(
        PcCompatKeyViewerRawEvent inputEvent)
    {
        if (inputEvent.Source != PcCompatKeyViewerRawSource.Keyboard)
            return Array.Empty<PcCompatCanonicalInputIdentity>();

        Span<PcCompatCanonicalInputIdentity> values = stackalloc PcCompatCanonicalInputIdentity[2];
        var count = 0;
        if (TryMapAndroidToUnity(inputEvent.Code, out var unity))
            values[count++] = new(PcCompatInputIdentityKind.UnityKeyCode, unity);
        if (TryMapAndroidToWindowsVirtualKey(inputEvent.Code, out var windows))
            values[count++] = new(PcCompatInputIdentityKind.WindowsVirtualKey, windows);
        return count switch
        {
            0 => Array.Empty<PcCompatCanonicalInputIdentity>(),
            1 => [values[0]],
            _ => [values[0], values[1]]
        };
    }

    public static bool TryMapAndroidToUnity(int keyCode, out int unityKeyCode)
    {
        if (keyCode is >= 29 and <= 54)
            unityKeyCode = 97 + keyCode - 29;
        else if (keyCode is >= 7 and <= 16)
            unityKeyCode = 48 + keyCode - 7;
        else if (keyCode is >= 131 and <= 142)
            unityKeyCode = 282 + keyCode - 131;
        else if (keyCode is >= 144 and <= 153)
            unityKeyCode = 256 + keyCode - 144;
        else
        {
            unityKeyCode = keyCode switch
            {
                4 or 111 => 27,
                19 => 273,
                20 => 274,
                21 => 276,
                22 => 275,
                55 => 44,
                56 => 46,
                57 => 308,
                58 => 307,
                59 => 304,
                60 => 303,
                61 => 9,
                62 => 32,
                66 => 13,
                67 => 8,
                68 => 96,
                69 => 45,
                70 => 61,
                71 => 91,
                72 => 93,
                73 => 92,
                74 => 59,
                75 => 39,
                76 => 47,
                81 => 43,
                92 => 280,
                93 => 281,
                112 => 127,
                113 => 306,
                114 => 305,
                115 => 301,
                116 => 302,
                122 => 278,
                123 => 279,
                124 => 277,
                143 => 300,
                154 => 267,
                155 => 268,
                156 => 269,
                157 => 270,
                158 => 266,
                160 => 271,
                161 => 272,
                _ => -1
            };
        }
        return unityKeyCode >= 0;
    }

    public static bool TryMapAndroidToWindowsVirtualKey(int keyCode, out int virtualKey)
    {
        if (keyCode is >= 29 and <= 54)
            virtualKey = 0x41 + keyCode - 29;
        else if (keyCode is >= 7 and <= 16)
            virtualKey = 0x30 + keyCode - 7;
        else if (keyCode is >= 131 and <= 142)
            virtualKey = 0x70 + keyCode - 131;
        else if (keyCode is >= 144 and <= 153)
            virtualKey = 0x60 + keyCode - 144;
        else
        {
            virtualKey = keyCode switch
            {
                4 or 111 => 0x1B,
                19 => 0x26,
                20 => 0x28,
                21 => 0x25,
                22 => 0x27,
                55 => 0xBC,
                56 => 0xBE,
                57 => 0xA4,
                58 => 0xA5,
                59 => 0xA0,
                60 => 0xA1,
                61 => 0x09,
                62 => 0x20,
                66 => 0x0D,
                67 => 0x08,
                68 => 0xC0,
                69 => 0xBD,
                70 or 81 => 0xBB,
                71 => 0xDB,
                72 => 0xDD,
                73 => 0xDC,
                74 => 0xBA,
                75 => 0xDE,
                76 => 0xBF,
                82 => 0x5D,
                92 => 0x21,
                93 => 0x22,
                112 => 0x2E,
                113 => 0xA2,
                114 => 0xA3,
                115 => 0x14,
                116 => 0x91,
                117 => 0x5B,
                118 => 0x5C,
                122 => 0x24,
                123 => 0x23,
                124 => 0x2D,
                143 => 0x90,
                154 => 0x6F,
                155 => 0x6A,
                156 => 0x6D,
                157 => 0x6B,
                158 => 0x6E,
                160 => 0x0D,
                161 => 0xBB,
                _ => -1
            };
        }
        return virtualKey >= 0;
    }
}

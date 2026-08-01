using UnityEngine;

namespace TMPro;

public class TMP_Text : Behaviour
{
    public string text { get; set; } = string.Empty;
    public float fontSize { get; set; }
    public float fontSizeMin { get; set; }
    public float fontSizeMax { get; set; }
    public bool enableAutoSizing { get; set; }
    public float fixedWidth { get; set; }
    public float lineSpacing { get; set; }
    public Color color { get; set; } = Color.white;
    public TextAlignmentOptions alignment { get; set; }
    public RectTransform rectTransform { get; } = new();
    public TMP_FontAsset? font { get; set; }
    public object? fontSharedMaterial { get; set; }
    public object? fontMaterial { get; set; }
}

public class TextMeshProUGUI : TMP_Text;

public class TMP_FontAsset : UnityEngine.Object
{
    public List<TMP_FontAsset> fallbackFontAssetTable { get; } = new();
}

public enum TextAlignmentOptions
{
    TopLeft,
    Top,
    TopRight,
    Left,
    Center,
    Right,
    BottomLeft,
    Bottom,
    BottomRight,
    Midline,
    Baseline
}

public static class ShaderUtilities
{
    public static Shader? ShaderRef_MobileSDF { get; set; } = new();
    public static int ID_UnderlayColor { get; } = 1;
    public static int ID_UnderlayOffsetX { get; } = 2;
    public static int ID_UnderlayOffsetY { get; } = 3;
    public static int ID_UnderlayDilate { get; } = 4;
    public static int ID_UnderlaySoftness { get; } = 5;
    public static int ID_OutlineColor { get; } = 6;
    public static int ID_OutlineWidth { get; } = 7;
}

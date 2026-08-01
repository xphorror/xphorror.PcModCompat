using UnityEngine;

namespace UnityEngine.UI;

public class Graphic : Behaviour
{
    public Color color { get; set; } = Color.white;
}

public class MaskableGraphic : Graphic;

public class Text : MaskableGraphic
{
    public string text { get; set; } = string.Empty;
    public int fontSize { get; set; }
    public bool supportRichText { get; set; } = true;
    public TextAnchor alignment { get; set; }
    public RectTransform rectTransform { get; } = new();
}

public class Image : MaskableGraphic
{
    public Sprite? sprite { get; set; }
}

public class RawImage : MaskableGraphic
{
    public Texture? texture { get; set; }
}

public class CanvasScaler : Behaviour
{
    public Vector2 referenceResolution { get; set; }
    public float matchWidthOrHeight { get; set; }
    public ScaleMode uiScaleMode { get; set; }

    public enum ScaleMode
    {
        ConstantPixelSize,
        ScaleWithScreenSize,
        ConstantPhysicalSize
    }
}

public class ContentSizeFitter : Behaviour
{
    public FitMode horizontalFit { get; set; }
    public FitMode verticalFit { get; set; }

    public enum FitMode
    {
        Unconstrained,
        MinSize,
        PreferredSize
    }
}

public enum TextAnchor
{
    UpperLeft,
    UpperCenter,
    UpperRight,
    MiddleLeft,
    MiddleCenter,
    MiddleRight,
    LowerLeft,
    LowerCenter,
    LowerRight
}

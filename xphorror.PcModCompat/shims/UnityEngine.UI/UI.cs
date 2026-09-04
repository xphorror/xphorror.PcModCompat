using UnityEngine;

namespace UnityEngine.UI;

public class Graphic : Behaviour
{
    public Color color { get; set; } = Color.white;

    /// <summary>
    /// Declared so a MOD's mesh-generating subclass can <c>override</c> it, as the real one does. Kept
    /// <c>protected virtual</c> to match the generated proxy: a subclass override is not publicly
    /// callable, which is precisely the constraint the component bridge has to bind through.
    /// </summary>
    protected virtual void OnPopulateMesh(VertexHelper vh)
    {
    }
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

/// <summary>
/// Compile-time stand-in for the generated <c>VertexHelper</c> proxy, so a MOD's
/// <c>OnPopulateMesh(VertexHelper)</c> override has a type to name in the shim build.
/// </summary>
/// <remarks>
/// Vertex submission is deliberately not modelled. <c>AddVert</c> takes a <c>UIVertex</c>, which the
/// real proxy carries as a value type forwarded straight to IL2CPP; there is no way to observe the
/// result of submitting one outside a device, so a shim overload would only invite tests that assert
/// against the shim's own bookkeeping rather than Unity's.
/// </remarks>
public class VertexHelper
{
    public int currentVertCount => 0;

    public void Clear()
    {
    }

    public void AddTriangle(int index0, int index1, int index2)
    {
    }
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

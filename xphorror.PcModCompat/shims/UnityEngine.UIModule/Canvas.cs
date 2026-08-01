namespace UnityEngine;

public class Canvas : Behaviour
{
    public RenderMode renderMode { get; set; }
    public int sortingOrder { get; set; }
}

public enum RenderMode
{
    ScreenSpaceOverlay,
    ScreenSpaceCamera,
    WorldSpace
}

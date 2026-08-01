namespace UnityEngine;

public static class GUILayout
{
    public static bool Button(string text, params GUILayoutOption[] options) => false;
    public static bool Button(Texture image, GUIStyle style, params GUILayoutOption[] options) => false;
    public static bool Toggle(bool value, string text, GUIStyle style, params GUILayoutOption[] options) => value;
    public static void Label(string text, params GUILayoutOption[] options) { }
    public static void Space(float pixels) { }
    public static void FlexibleSpace() { }
    public static void BeginHorizontal(params GUILayoutOption[] options) { }
    public static void EndHorizontal() { }
    public static void BeginVertical(params GUILayoutOption[] options) { }
    public static void EndVertical() { }
}

public static class GUI
{
    public static GUISkin skin { get; } = new();
}

public class GUISkin
{
    public GUIStyle label { get; set; } = new();
}

public class GUIStyle
{
    public GUIStyleState normal { get; set; } = new();
    public RectOffset margin { get; set; } = new();
    public float fixedWidth { get; set; }
    public int fontSize { get; set; }
    public bool richText { get; set; }
}

public class GUIStyleState
{
    public Color textColor { get; set; } = Color.white;
}

public class GUILayoutOption;

public class RectOffset
{
    public int left;
    public int right;
    public int top;
    public int bottom;

    public RectOffset() { }
    public RectOffset(int left, int right, int top, int bottom)
    {
        this.left = left;
        this.right = right;
        this.top = top;
        this.bottom = bottom;
    }
}

namespace Xphorror.PcModCompat.Tools;

internal static class ManagedBridgeOwnedSurface
{
    internal static readonly IReadOnlySet<string> Entries = new HashSet<string>(StringComparer.Ordinal)
    {
        "M|UnityEngine.AssetBundleModule|UnityEngine.AssetBundle|static|0|UnityEngine.AssetBundle|LoadFromFile|System.String",
        "M|UnityEngine.AssetBundleModule|UnityEngine.AssetBundle|instance|0|UnityEngine.Object|LoadAsset|System.String",
        "M|UnityEngine.AssetBundleModule|UnityEngine.AssetBundle|instance|0|UnityEngine.Object|LoadAsset|System.String;System.Type",
        "M|UnityEngine.AssetBundleModule|UnityEngine.AssetBundle|instance|1|!!0|LoadAsset|System.String",
        "M|UnityEngine.AssetBundleModule|UnityEngine.AssetBundle|instance|0|UnityEngine.Object[]|LoadAllAssets|",
        "M|UnityEngine.AssetBundleModule|UnityEngine.AssetBundle|instance|0|UnityEngine.Object[]|LoadAllAssets|System.Type",
        "M|UnityEngine.AssetBundleModule|UnityEngine.AssetBundle|instance|1|!!0[]|LoadAllAssets|",
        "M|UnityEngine.AssetBundleModule|UnityEngine.AssetBundle|instance|0|System.Void|Unload|System.Boolean",
        "M|UnityEngine.IMGUIModule|UnityEngine.GUILayout|static|0|System.Boolean|Button|System.String;UnityEngine.GUIStyle;UnityEngine.GUILayoutOption[]",
        "M|UnityEngine.IMGUIModule|UnityEngine.GUILayout|static|0|System.Boolean|Button|UnityEngine.Texture;UnityEngine.GUIStyle;UnityEngine.GUILayoutOption[]",
        "M|UnityEngine.IMGUIModule|UnityEngine.GUILayout|static|0|System.Boolean|Toggle|System.Boolean;UnityEngine.GUIContent;UnityEngine.GUILayoutOption[]",
        "M|UnityEngine.IMGUIModule|UnityEngine.GUILayout|static|0|System.String|TextArea|System.String;UnityEngine.GUILayoutOption[]",
        "M|UnityEngine.IMGUIModule|UnityEngine.GUI|static|0|System.Void|SetNextControlName|System.String",
        "M|UnityEngine.IMGUIModule|UnityEngine.GUI|static|0|System.String|GetNameOfFocusedControl|",
        "M|UnityEngine.IMGUIModule|UnityEngine.GUI|static|0|System.Void|DragWindow|UnityEngine.Rect",
        "M|UnityEngine.IMGUIModule|UnityEngine.GUI|static|0|System.Void|DrawTexture|UnityEngine.Rect;UnityEngine.Texture",
        "M|UnityEngine.IMGUIModule|UnityEngine.GUILayout|static|0|System.Void|Label|UnityEngine.GUIContent;UnityEngine.GUILayoutOption[]",
        "M|UnityEngine.IMGUIModule|UnityEngine.GUILayout|static|0|System.Int32|SelectionGrid|System.Int32;System.String[];System.Int32;UnityEngine.GUILayoutOption[]",
        "M|UnityEngine.IMGUIModule|UnityEngine.GUILayout|static|0|System.Single|HorizontalSlider|System.Single;System.Single;System.Single;UnityEngine.GUILayoutOption[]",
        "M|UnityEngine.IMGUIModule|UnityEngine.GUILayout|static|0|System.String|TextField|System.String;UnityEngine.GUILayoutOption[]",
        "M|UnityEngine.IMGUIModule|UnityEngine.GUILayout|static|0|System.Void|BeginVertical|UnityEngine.GUIStyle;UnityEngine.GUILayoutOption[]",
        "M|UnityEngine.IMGUIModule|UnityEngine.GUILayoutUtility|static|0|UnityEngine.Rect|GetRect|System.Single;System.Single",
        "M|UnityEngine.JSONSerializeModule|UnityEngine.JsonUtility|static|0|System.String|ToJson|System.Object;System.Boolean",
        "M|UnityEngine.JSONSerializeModule|UnityEngine.JsonUtility|static|0|System.Void|FromJsonOverwrite|System.String;System.Object",
        "M|UnityEngine.JSONSerializeModule|UnityEngine.JsonUtility|static|1|!!0|FromJson|System.String",
        "M|UnityEngine.IMGUIModule|UnityEngine.GUIStyle|instance|0|System.Void|set_fixedWidth|System.Single",
        "M|UnityEngine.IMGUIModule|UnityEngine.GUIStyle|instance|0|System.Void|set_normal|UnityEngine.GUIStyleState",
        "M|UnityEngine.IMGUIModule|UnityEngine.GUIStyle|instance|0|System.Void|set_margin|UnityEngine.RectOffset"
    };

    internal static bool Contains(string value)
        => Entries.Contains(Normalize(value));

    internal static string Normalize(string value)
        => value.Trim().Replace('+', '/');
}

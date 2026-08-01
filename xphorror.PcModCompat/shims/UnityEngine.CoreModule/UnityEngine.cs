namespace UnityEngine;

public class Object
{
    public string name { get; set; } = string.Empty;

    public static void Destroy(Object? obj) { }
    public static void Destroy(Object? obj, float delay) { }
    public static void DontDestroyOnLoad(Object? target) { }
    public static T? FindObjectOfType<T>() where T : Object => null;
    public static T[] FindObjectsByType<T>(FindObjectsSortMode sortMode) where T : Object => Array.Empty<T>();
    public static T Instantiate<T>(T original, Transform? parent = null) where T : Object => original;

    public static implicit operator bool(Object? obj) => obj != null;
}

public class Component : Object
{
    public GameObject gameObject { get; set; } = null!;
    public Transform transform { get; set; } = null!;

    public T GetComponent<T>() where T : Component, new() => new();
    public Component? GetComponent(Type type) => type.GetConstructor(Type.EmptyTypes) != null ? (Component?)Activator.CreateInstance(type) : null;
    public T[] GetComponents<T>() where T : Component, new() => [GetComponent<T>()];
    public Component[] GetComponents(Type type)
        => GetComponent(type) is { } component ? [component] : [];
    public bool TryGetComponent<T>(out T? component) where T : Component, new()
    {
        component = GetComponent<T>();
        return component != null;
    }
    public bool TryGetComponent(Type type, out Component? component)
    {
        component = GetComponent(type);
        return component != null;
    }
}

public class Behaviour : Component
{
    public bool enabled { get; set; } = true;
}

public class MonoBehaviour : Behaviour
{
    public void Invoke(string methodName) { }
    public void Invoke(string methodName, float time) { }
    public Coroutine StartCoroutine(System.Collections.IEnumerator routine) => new();
    public Coroutine StartCoroutine(string methodName) => new();
    public Coroutine StartCoroutine(string methodName, object? value) => new();
    public void StopCoroutine(System.Collections.IEnumerator routine) { }
    public void StopCoroutine(Coroutine routine) { }
    public void StopCoroutine(string methodName) { }
    public void StopAllCoroutines() { }
}

public sealed class Coroutine : Object;

public class GameObject : Object
{
    private readonly Dictionary<Type, Component> _components = new();

    public GameObject(string name = "")
    {
        this.name = name;
        transform = new Transform { gameObject = this };
    }

    public Transform transform { get; set; }
    public bool activeSelf { get; private set; } = true;
    public string tag { get; set; } = string.Empty;

    public T AddComponent<T>() where T : Component, new()
    {
        var component = new T { gameObject = this };
        _components[typeof(T)] = component;
        return component;
    }

    public Component AddComponent(Type type)
    {
        var component = (Component)Activator.CreateInstance(type)!;
        component.gameObject = this;
        _components[type] = component;
        return component;
    }

    public T GetComponent<T>() where T : Component, new()
    {
        if (_components.TryGetValue(typeof(T), out var component))
            return (T)component;
        return AddComponent<T>();
    }

    public Component? GetComponent(Type type)
        => _components.TryGetValue(type, out var component) ? component : null;

    public T[] GetComponents<T>() where T : Component, new()
        => _components.Values.OfType<T>().ToArray();

    public Component[] GetComponents(Type type)
        => _components.Values.Where(type.IsInstanceOfType).ToArray();

    public bool TryGetComponent<T>(out T? component) where T : Component, new()
    {
        component = _components.Values.OfType<T>().FirstOrDefault();
        return component != null;
    }

    public bool TryGetComponent(Type type, out Component? component)
    {
        component = GetComponent(type);
        return component != null;
    }

    public void SetActive(bool value) => activeSelf = value;
}

public class Transform : Component
{
    private readonly Dictionary<string, Transform> _children = new(StringComparer.Ordinal);

    public Transform? parent { get; set; }
    public int childCount => _children.Count;
    public Vector3 localScale { get; set; } = Vector3.one;
    public Vector3 position { get; set; }

    public Transform Find(string name)
    {
        if (_children.TryGetValue(name, out var child))
            return child;
        child = new Transform { name = name, parent = this };
        child.gameObject = new GameObject(name);
        _children[name] = child;
        return child;
    }

    public void SetParent(Transform? parent) => this.parent = parent;
    public void SetSiblingIndex(int index) { }
}

public class RectTransform : Transform
{
    public Vector2 anchoredPosition { get; set; }
    public Vector2 sizeDelta { get; set; }
    public Vector2 anchorMin { get; set; }
    public Vector2 anchorMax { get; set; }
    public Vector2 pivot { get; set; }
    public Rect rect { get; set; }
}

public readonly struct Vector2
{
    public readonly float x;
    public readonly float y;
    public Vector2(float x, float y) { this.x = x; this.y = y; }
    public static Vector2 zero => new(0, 0);
    public static Vector2 one => new(1, 1);
}

public readonly struct Vector3
{
    public readonly float x;
    public readonly float y;
    public readonly float z;
    public Vector3(float x, float y, float z = 0) { this.x = x; this.y = y; this.z = z; }
    public static Vector3 zero => new(0, 0, 0);
    public static Vector3 one => new(1, 1, 1);
}

public readonly struct Rect
{
    public readonly float x;
    public readonly float y;
    public readonly float width;
    public readonly float height;
    public Rect(float x, float y, float width, float height)
    {
        this.x = x;
        this.y = y;
        this.width = width;
        this.height = height;
    }
}

public struct Color
{
    public float r;
    public float g;
    public float b;
    public float a;

    public Color(float r, float g, float b)
        : this(r, g, b, 1)
    {
    }

    public Color(float r, float g, float b, float a = 1)
    {
        this.r = r;
        this.g = g;
        this.b = b;
        this.a = a;
    }
    public static Color white => new(1, 1, 1, 1);
    public static Color black => new(0, 0, 0, 1);
    public static Color red => new(1, 0, 0, 1);
    public static Color green => new(0, 1, 0, 1);
    public static Color blue => new(0, 0, 1, 1);
    public static Color yellow => new(1, 0.9215686f, 0.01568628f, 1);
    public static Color cyan => new(0, 1, 1, 1);
    public static Color magenta => new(1, 0, 1, 1);
    public static Color gray => new(0.5f, 0.5f, 0.5f, 1);
    public static Color grey => gray;
    public static Color clear => new(0, 0, 0, 0);
}

public static class ColorUtility
{
    public static string ToHtmlStringRGB(Color color) => ToHtmlStringRGBA(color)[..6];
    public static string ToHtmlStringRGBA(Color color)
        => $"{Clamp(color.r):X2}{Clamp(color.g):X2}{Clamp(color.b):X2}{Clamp(color.a):X2}";
    private static int Clamp(float value) => Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}

public static class Mathf
{
    public static int RoundToInt(float value) => (int)MathF.Round(value);
    public static float Abs(float value) => MathF.Abs(value);
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
    public static float Pow(float f, float p) => MathF.Pow(f, p);
}

public static class Application
{
    public static string unityVersion => "6000.3.10f1";
    public static string version => "0.0";
    public static event Action? quitting;
    public static void OpenURL(string url) { }
    public static void Quit() => quitting?.Invoke();
}

public enum SystemLanguage
{
    Afrikaans = 0,
    Arabic = 1,
    Basque = 2,
    Belarusian = 3,
    Bulgarian = 4,
    Catalan = 5,
    Chinese = 6,
    Czech = 7,
    Danish = 8,
    Dutch = 9,
    English = 10,
    Estonian = 11,
    Faroese = 12,
    Finnish = 13,
    French = 14,
    German = 15,
    Greek = 16,
    Hebrew = 17,
    Hungarian = 18,
    Icelandic = 19,
    Indonesian = 20,
    Italian = 21,
    Japanese = 22,
    Korean = 23,
    Latvian = 24,
    Lithuanian = 25,
    Norwegian = 26,
    Polish = 27,
    Portuguese = 28,
    Romanian = 29,
    Russian = 30,
    SerboCroatian = 31,
    Slovak = 32,
    Slovenian = 33,
    Spanish = 34,
    Swedish = 35,
    Thai = 36,
    Turkish = 37,
    Ukrainian = 38,
    Vietnamese = 39,
    ChineseSimplified = 40,
    ChineseTraditional = 41,
    Hindi = 42,
    Unknown = 43
}

public static class Debug
{
    public static void Log(object? message) => Console.WriteLine(message);
    public static void LogWarning(object? message) => Console.WriteLine(message);
    public static void LogError(object? message) => Console.Error.WriteLine(message);
}

public class Texture : Object;
public class Texture2D : Texture;
public class Sprite : Object;
public class Shader : Object;
public class AudioClip : Object
{
    public float length { get; set; }
}

public class AudioSource : Behaviour
{
    public AudioClip? clip { get; set; }
    public float pitch { get; set; } = 1f;
    public float time { get; set; }
    public float volume { get; set; } = 1f;
}

public enum FindObjectsSortMode { None }

public enum KeyCode
{
    None = 0,
    Backspace = 8,
    Tab = 9,
    Return = 13,
    Space = 32,
    Comma = 44,
    Period = 46,
    Alpha0 = 48,
    Alpha1 = 49,
    Alpha2 = 50,
    Alpha3 = 51,
    Alpha4 = 52,
    Alpha5 = 53,
    Alpha6 = 54,
    Alpha7 = 55,
    Alpha8 = 56,
    Alpha9 = 57,
    Semicolon = 59,
    Equals = 61,
    Backslash = 92,
    A = 97,
    C = 99,
    D = 100,
    E = 101,
    H = 104,
    P = 112,
    F1 = 282,
    F2 = 283,
    F3 = 284,
    F4 = 285,
    F5 = 286,
    F6 = 287,
    F7 = 288,
    F8 = 289,
    CapsLock = 301,
    RightShift = 303,
    LeftShift = 304
}

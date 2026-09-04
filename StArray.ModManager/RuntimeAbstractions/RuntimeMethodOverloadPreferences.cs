namespace StArray.ModManager.RuntimeAbstractions;

/// <summary>
/// Stabilizes count-only lookups for APIs where newer Unity versions added an
/// ABI-incompatible overload with the same parameter count.
/// </summary>
internal static class RuntimeMethodOverloadPreferences
{
    private static readonly string[] LegacyTexture2DConstructor =
    [
        "System.Int32",
        "System.Int32",
        "UnityEngine.TextureFormat",
        "System.Boolean"
    ];

    private static readonly string[] PublicSpriteCreate =
    [
        "UnityEngine.Texture2D",
        "UnityEngine.Rect",
        "UnityEngine.Vector2",
        "System.Single"
    ];

    private static readonly string[] CalculateTickColorWithHitFloor =
    [
        "System.Single",
        "System.Single",
        "scrFloor"
    ];

    public static string[]? Resolve(
        string? typeNamespace,
        string? typeName,
        string methodName,
        int parameterCount)
    {
        // Unity 6 places DefaultFormat/GraphicsFormat constructors alongside the
        // legacy TextureFormat overload. mono_class_get_method_from_name only
        // receives the count and can therefore select a different native ABI.
        if (parameterCount == 4 &&
            methodName == ".ctor" &&
            typeNamespace == "UnityEngine" &&
            typeName == "Texture2D")
            return LegacyTexture2DConstructor;

        // Unity 6 also exposes an internal four-argument Sprite.Create overload
        // with Texture2D last. Count-only lookup may select it even though the
        // public API and existing mods pass Texture2D first.
        if (parameterCount == 4 &&
            methodName == "Create" &&
            typeNamespace == "UnityEngine" &&
            typeName == "Sprite")
            return PublicSpriteCreate;

        return null;
    }

    public static RuntimeMethodCompatibilityDescriptor? ResolveCompatibility(
        string? typeNamespace,
        string? typeName,
        string methodName,
        int parameterCount)
    {
        // XPerfect targets the pre-Unity-6 method, which did not expose hitFloor.
        // This is deliberately a full signature whitelist: count-nearest matching
        // would silently bind arbitrary native ABIs.
        if (parameterCount == 2 &&
            methodName == "CalculateTickColor" &&
            string.IsNullOrEmpty(typeNamespace) &&
            typeName == "scrHitErrorMeter")
        {
            return new RuntimeMethodCompatibilityDescriptor(
                CalculateTickColorWithHitFloor,
                RuntimeMethodCompatibilityKind.CalculateTickColorWithoutHitFloor);
        }

        return null;
    }
}

internal readonly record struct RuntimeMethodCompatibilityDescriptor(
    string[] ActualParameterTypes,
    RuntimeMethodCompatibilityKind Kind);

public enum RuntimeMethodCompatibilityKind
{
    None = 0,
    CalculateTickColorWithoutHitFloor = 1
}

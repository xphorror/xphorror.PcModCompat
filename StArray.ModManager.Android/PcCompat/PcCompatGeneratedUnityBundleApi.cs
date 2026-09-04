using System.Linq.Expressions;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace StArray.ModManager.Android.PcCompat;

/// <summary>
/// Low-frequency AssetBundle calls through runtime-metadata-only generated proxies.
/// Instances returned by this facade remain strongly rooted by the caller.
/// </summary>
internal sealed class PcCompatGeneratedUnityBundleApi
{
    private const string AssetBundleAssembly = "UnityEngine.AssetBundleModule";
    private const string CoreAssembly = "UnityEngine.CoreModule";
    private readonly Func<string, object?> _loadFromFileAsync;
    private readonly Func<object, string, object, object?> _loadAssetAsync;
    private readonly Func<object, bool> _isDone;
    private readonly Func<object, object?> _getAssetBundle;
    private readonly Func<object, object?> _getAsset;
    private readonly Func<object, string?> _getTextAssetText;
    private readonly Func<object, bool> _isUnityObjectAlive;
    // Unload is bridge-owned and is intentionally excluded from the generated
    // AssetBundle proxy. Older proxy sets may still expose it, but its absence
    // is a supported Android ABI shape and must not block startup.
    private readonly Action<object, bool>? _unload;
    private readonly ConstructorInfo _assetBundleConstructor;
    private readonly Dictionary<string, object> _assetTypeObjects =
        new(StringComparer.OrdinalIgnoreCase);

    public PcCompatGeneratedUnityBundleApi()
    {
        PcCompatIl2CppInteropBootstrap.RequireReady();

        var assetBundle = RequiredProxyType(AssetBundleAssembly, "UnityEngine.AssetBundle");
        var createRequest = RequiredProxyType(
            AssetBundleAssembly,
            "UnityEngine.AssetBundleCreateRequest");
        var assetRequest = RequiredProxyType(
            AssetBundleAssembly,
            "UnityEngine.AssetBundleRequest");
        var asyncOperation = RequiredProxyType(CoreAssembly, "UnityEngine.AsyncOperation");
        var unityObject = RequiredProxyType(CoreAssembly, "UnityEngine.Object");
        var textAsset = RequiredProxyType(CoreAssembly, "UnityEngine.TextAsset");
        var il2CppType = typeof(Il2CppSystem.Type);

        var loadFromFileAsync = RequiredMethod(
            assetBundle,
            "LoadFromFileAsync",
            isStatic: true,
            typeof(string));
        var loadAssetAsync = RequiredMethod(
            assetBundle,
            "LoadAssetAsync",
            isStatic: false,
            typeof(string),
            il2CppType);
        var isDone = RequiredMethod(asyncOperation, "get_isDone", isStatic: false);
        var getAssetBundle = RequiredMethod(
            createRequest,
            "get_assetBundle",
            isStatic: false);
        var getAsset = RequiredMethod(assetRequest, "get_asset", isStatic: false);
        var getTextAssetText = RequiredMethod(textAsset, "get_text", isStatic: false);
        var isUnityObjectAlive = RequiredMethod(
            unityObject,
            "op_Implicit",
            isStatic: true,
            unityObject);
        var unload = TryGetMethod(
            assetBundle,
            "Unload",
            isStatic: false,
            typeof(bool));

        _loadFromFileAsync = CompileStaticStringCall(loadFromFileAsync);
        _loadAssetAsync = CompileInstanceStringObjectCall(loadAssetAsync);
        _isDone = CompileInstanceResult<bool>(isDone);
        _getAssetBundle = CompileInstanceResult<object?>(getAssetBundle);
        _getAsset = CompileInstanceResult<object?>(getAsset);
        _getTextAssetText = CompileInstanceResult<string?>(getTextAssetText);
        _isUnityObjectAlive = CompileStaticObjectResult<bool>(isUnityObjectAlive);
        _unload = unload is null ? null : CompileInstanceBoolCall(unload);
        _assetBundleConstructor = assetBundle.GetConstructor([typeof(IntPtr)])
            ?? throw new MissingMethodException(assetBundle.FullName, ".ctor(IntPtr)");
    }

    public object? LoadFromFileAsync(string path)
        => _loadFromFileAsync(path);

    public object? LoadAssetAsync(object bundle, string assetName, string expectedType)
        => _loadAssetAsync(bundle, assetName, ResolveAssetTypeObject(expectedType));

    public bool IsDone(object request)
        => _isDone(request);

    public object? GetAssetBundle(object request)
        => _getAssetBundle(request);

    public object? GetAsset(object request)
        => _getAsset(request);

    public object WrapAsset(object proxy, string expectedType)
    {
        var pointer = GetPointer(proxy);
        if (pointer == nint.Zero)
            throw new InvalidOperationException("Cannot wrap a null Unity asset pointer.");
        var proxyType = ResolveAssetProxyType(NormalizeExpectedType(expectedType));
        if (proxyType.IsInstanceOfType(proxy))
            return proxy;
        var constructor = proxyType.GetConstructor([typeof(IntPtr)])
                          ?? throw new MissingMethodException(proxyType.FullName, ".ctor(IntPtr)");
        return constructor.Invoke([pointer]);
    }

    public string? GetTextAssetText(object textAsset)
        => _getTextAssetText(textAsset);

    public bool IsUnityObjectAlive(object proxy)
        => _isUnityObjectAlive(proxy);

    public void Unload(object bundle, bool unloadAllLoadedObjects)
    {
        // VirtualBundle teardown releases owner leases and materialized Unity
        // objects. If the host proxy has no native AssetBundle.Unload member,
        // retaining the process-wide capability bundle is the correct fallback.
        _unload?.Invoke(bundle, unloadAllLoadedObjects);
    }

    public object WrapAssetBundle(nint bundle)
        => _assetBundleConstructor.Invoke([bundle]);

    /// <summary>
    /// Creates an owner-scoped IL2CPP root for a long-lived Unity object. The
    /// AsyncOperation proxy can be released after its result is consumed, while
    /// the returned bundle or asset must remain rooted until its owner unloads it.
    /// </summary>
    public object CreateNativeRoot(nint pointer)
    {
        if (pointer == nint.Zero)
            throw new ArgumentException("Cannot root a null IL2CPP object.", nameof(pointer));
        return new Il2CppSystem.Object(pointer);
    }

    public static nint GetPointer(object? proxy)
        => proxy switch
        {
            null => nint.Zero,
            Il2CppObjectBase il2CppObject => il2CppObject.Pointer,
            _ => throw new InvalidCastException(
                $"Generated proxy does not derive from {nameof(Il2CppObjectBase)}: " +
                proxy.GetType().AssemblyQualifiedName)
        };

    private object ResolveAssetTypeObject(string expectedType)
    {
        var normalized = NormalizeExpectedType(expectedType);
        if (_assetTypeObjects.TryGetValue(normalized, out var cached))
            return cached;

        var proxyType = ResolveAssetProxyType(normalized);
        var typeObject = Il2CppType.From(proxyType);
        _assetTypeObjects.Add(normalized, typeObject);
        return typeObject;
    }

    private static Type ResolveAssetProxyType(string expectedType)
    {
        var simpleName = expectedType[(expectedType.LastIndexOf('.') + 1)..];
        var known = simpleName switch
        {
            "TMP_FontAsset" => ("Unity.TextMeshPro", "TMPro.TMP_FontAsset"),
            "Font" => ("UnityEngine.TextRenderingModule", "UnityEngine.Font"),
            "AudioClip" => ("UnityEngine.AudioModule", "UnityEngine.AudioClip"),
            "Object" or "GameObject" or "Sprite" or "Texture" or "Texture2D" or "Material" or
                "Shader" or "TextAsset" =>
                (CoreAssembly, "UnityEngine." + simpleName),
            _ => default
        };
        if (known.Item1 != null)
            return RequiredProxyType(known.Item1, known.Item2);

        string? uniqueError = null;
        if (expectedType.Contains('.') &&
            PcCompatIl2CppInteropBootstrap.TryGetUniqueProxyType(
                expectedType,
                out var exact,
                out uniqueError))
        {
            return exact;
        }

        throw new TypeLoadException(
            expectedType.Contains('.')
                ? uniqueError
                : $"Unknown simple asset type requires an explicit proxy identity: {expectedType}");
    }

    private static string NormalizeExpectedType(string expectedType)
    {
        if (string.IsNullOrWhiteSpace(expectedType))
            throw new ArgumentException("Expected Unity asset type is empty.", nameof(expectedType));
        return expectedType.Split(',', 2)[0].Trim().Replace('+', '.');
    }

    private static Type RequiredProxyType(string assemblyName, string fullTypeName)
    {
        if (PcCompatIl2CppInteropBootstrap.TryGetProxyType(
                assemblyName,
                fullTypeName,
                out var type))
        {
            return type;
        }

        throw new TypeLoadException(
            $"Generated proxy type is unavailable: {assemblyName}:{fullTypeName}");
    }

    private static MethodInfo RequiredMethod(
        Type type,
        string name,
        bool isStatic,
        params Type[] parameterTypes)
    {
        var flags = BindingFlags.Public |
                    (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        return type.GetMethod(name, flags, binder: null, parameterTypes, modifiers: null)
               ?? throw new MissingMethodException(
                   type.FullName,
                   $"{name}({string.Join(", ", parameterTypes.Select(item => item.FullName))})");
    }

    private static MethodInfo? TryGetMethod(
        Type type,
        string name,
        bool isStatic,
        params Type[] parameterTypes)
    {
        var flags = BindingFlags.Public |
                    (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        return type.GetMethod(name, flags, binder: null, parameterTypes, modifiers: null);
    }

    private static Func<string, object?> CompileStaticStringCall(MethodInfo method)
    {
        var argument = Expression.Parameter(typeof(string), "argument");
        var call = Expression.Call(method, argument);
        return Expression.Lambda<Func<string, object?>>(
            Expression.Convert(call, typeof(object)),
            argument).Compile();
    }

    private static Func<object, TResult> CompileStaticObjectResult<TResult>(MethodInfo method)
    {
        var source = Expression.Parameter(typeof(object), "source");
        var parameterType = method.GetParameters()[0].ParameterType;
        var call = Expression.Call(method, Expression.Convert(source, parameterType));
        return Expression.Lambda<Func<object, TResult>>(
            Expression.Convert(call, typeof(TResult)),
            source).Compile();
    }

    private static Func<object, string, object, object?> CompileInstanceStringObjectCall(
        MethodInfo method)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var text = Expression.Parameter(typeof(string), "text");
        var value = Expression.Parameter(typeof(object), "value");
        var call = Expression.Call(
            Expression.Convert(instance, method.DeclaringType!),
            method,
            text,
            Expression.Convert(value, method.GetParameters()[1].ParameterType));
        return Expression.Lambda<Func<object, string, object, object?>>(
            Expression.Convert(call, typeof(object)),
            instance,
            text,
            value).Compile();
    }

    private static Func<object, TResult> CompileInstanceResult<TResult>(MethodInfo method)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var call = Expression.Call(Expression.Convert(instance, method.DeclaringType!), method);
        return Expression.Lambda<Func<object, TResult>>(
            Expression.Convert(call, typeof(TResult)),
            instance).Compile();
    }

    private static Action<object, bool> CompileInstanceBoolCall(MethodInfo method)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var value = Expression.Parameter(typeof(bool), "value");
        var call = Expression.Call(
            Expression.Convert(instance, method.DeclaringType!),
            method,
            value);
        return Expression.Lambda<Action<object, bool>>(call, instance, value).Compile();
    }
}

using System.Linq.Expressions;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using StArray.ModManager.Manager;

namespace StArray.ModManager.Android.PcCompat;

/// <summary>
/// Generated-proxy implementation of the Unity HUD object surface.
/// All delegates are compiled once; frame updates do not use reflection invoke.
/// </summary>
internal sealed class PcCompatGeneratedUnityHudApi
{
    private const string Core = "UnityEngine.CoreModule";
    private readonly Dictionary<(Type Type, nint Pointer), object> _wrappers = new();
    private readonly Dictionary<Type, ConstructorInfo> _pointerConstructors = new();
    private readonly Dictionary<Type, Il2CppSystem.Type> _typeObjects = new();
    private readonly Func<string, Il2CppSystem.Type[], object> _createGameObject;
    private readonly Func<object, object?> _instantiate;
    private readonly Action<object> _destroy;
    private readonly Func<object, object, object?> _addComponent;
    private readonly Func<object, object, object?> _getComponent;
    private readonly Func<object, object?> _getTransform;
    private readonly Func<object, string, object?> _findTransform;
    private readonly Action<object, bool> _setActive;
    private readonly Action<object> _dontDestroyOnLoad;
    private readonly Action<object, object, bool> _setParent;
    private readonly Action<object, float, float> _setAnchorMin;
    private readonly Action<object, float, float> _setAnchorMax;
    private readonly Action<object, float, float> _setPivot;
    private readonly Action<object, float, float> _setAnchoredPosition;
    private readonly Action<object, float, float> _setSizeDelta;
    private readonly Func<object, float> _getSizeDeltaY;
    private readonly Action<object, int> _setCanvasRenderMode;
    private readonly Action<object, int> _setCanvasSortingOrder;
    private readonly Action<object, int> _setCanvasScaleMode;
    private readonly Action<object, float, float> _setCanvasReferenceResolution;
    private readonly Action<object, float> _setCanvasMatch;
    private readonly Func<object, object?> _getTextRectTransform;
    private readonly Action<object, string> _setText;
    private readonly Action<object, float> _setFontSize;
    private readonly Action<object, object> _setFont;
    private readonly Func<object, object?> _getFont;
    private readonly Action<object, int> _setAlignment;
    private readonly Action<object, bool> _setRichText;
    private readonly Action<object, bool> _setRaycastTarget;
    private readonly Action<object, float, float, float, float> _setGraphicColor;
    private readonly Action<object> _setLocalizedFont;
    private bool _meshApiInitialized;
    private Type? _meshType;
    private Type? _vector3Type;
    private Type? _vector3ArrayType;
    private Type? _canvasRendererType;
    private Func<object>? _createMesh;
    private Action<object, int, float, float, float>? _setVector3ArrayItem;
    private Action<object>? _markMeshDynamic;
    private Action<object>? _recalculateMeshBounds;
    private Action<object, object>? _setMeshVertices;
    private Action<object, object>? _setMeshTriangles;
    private Action<object, object>? _setCanvasRendererMesh;
    private Action<object, object, int>? _setCanvasRendererMaterial;
    private Action<object, float, float, float, float>? _setCanvasRendererColor;
    private Func<object, object?>? _getGraphicMaterial;
    private readonly Dictionary<int, MeshUploadBuffer> _meshUploadBuffers = [];
    private readonly Dictionary<nint, int> _meshTriangleCapacities = [];
    private bool _localizedFontFailed;

    public Type CanvasType { get; }
    public Type CanvasScalerType { get; }
    public Type ImageType { get; }
    public Type TextMeshProType { get; }
    public Type RectTransformType { get; }
    public Type CanvasRendererType
    {
        get
        {
            EnsureMeshApi();
            return _canvasRendererType!;
        }
    }
    private Type ObjectType { get; }
    private Type GameObjectType { get; }
    private Type ComponentType { get; }
    private Type TransformType { get; }
    private Type GraphicType { get; }
    private Type TmpTextType { get; }
    private Type TmpFontType { get; }

    public PcCompatGeneratedUnityHudApi()
    {
        PcCompatIl2CppInteropBootstrap.RequireReady();

        ObjectType = RequiredType(Core, "UnityEngine.Object");
        GameObjectType = RequiredType(Core, "UnityEngine.GameObject");
        ComponentType = RequiredType(Core, "UnityEngine.Component");
        TransformType = RequiredType(Core, "UnityEngine.Transform");
        RectTransformType = RequiredType(Core, "UnityEngine.RectTransform");
        var vector2Type = RequiredType(Core, "UnityEngine.Vector2");
        var colorType = RequiredType(Core, "UnityEngine.Color");
        CanvasType = RequiredType("UnityEngine.UIModule", "UnityEngine.Canvas");
        CanvasScalerType = RequiredType("UnityEngine.UI", "UnityEngine.UI.CanvasScaler");
        ImageType = RequiredType("UnityEngine.UI", "UnityEngine.UI.Image");
        GraphicType = RequiredType("UnityEngine.UI", "UnityEngine.UI.Graphic");
        TextMeshProType = RequiredType("Unity.TextMeshPro", "TMPro.TextMeshProUGUI");
        TmpTextType = RequiredType("Unity.TextMeshPro", "TMPro.TMP_Text");
        TmpFontType = RequiredType("Unity.TextMeshPro", "TMPro.TMP_FontAsset");
        var rdStringType = RequiredType("Assembly-CSharp", "RDString");

        var gameObjectConstructor = GameObjectType.GetConstructor(
            [typeof(string), typeof(Il2CppSystem.Type[])])
            ?? throw new MissingMethodException(
                GameObjectType.FullName,
                ".ctor(String, Il2CppSystem.Type[])");
        _createGameObject = CompileGameObjectConstructor(gameObjectConstructor);
        _instantiate = CompileStaticObjectResult(RequiredMethod(
            ObjectType,
            "Instantiate",
            isStatic: true,
            ObjectType));
        _destroy = CompileStaticObjectCall(RequiredMethod(
            ObjectType,
            "Destroy",
            isStatic: true,
            ObjectType));
        _dontDestroyOnLoad = CompileStaticObjectCall(RequiredMethod(
            ObjectType,
            "DontDestroyOnLoad",
            isStatic: true,
            ObjectType));
        _addComponent = CompileInstanceObjectResult(RequiredMethod(
            GameObjectType,
            "AddComponent",
            isStatic: false,
            typeof(Il2CppSystem.Type)));
        _getComponent = CompileInstanceObjectResult(RequiredMethod(
            ComponentType,
            "GetComponent",
            isStatic: false,
            typeof(Il2CppSystem.Type)));
        _getTransform = CompileInstanceResult(RequiredMethod(
            GameObjectType,
            "get_transform",
            isStatic: false));
        _findTransform = CompileInstanceStringResult(RequiredMethod(
            TransformType,
            "Find",
            isStatic: false,
            typeof(string)));
        _setActive = CompileInstanceBoolCall(RequiredMethod(
            GameObjectType,
            "SetActive",
            isStatic: false,
            typeof(bool)));
        _setParent = CompileInstanceObjectBoolCall(RequiredMethod(
            TransformType,
            "SetParent",
            isStatic: false,
            TransformType,
            typeof(bool)));
        _setAnchorMin = CompileInstanceVector2Call(
            RequiredMethod(RectTransformType, "set_anchorMin", false, vector2Type),
            vector2Type);
        _setAnchorMax = CompileInstanceVector2Call(
            RequiredMethod(RectTransformType, "set_anchorMax", false, vector2Type),
            vector2Type);
        _setPivot = CompileInstanceVector2Call(
            RequiredMethod(RectTransformType, "set_pivot", false, vector2Type),
            vector2Type);
        _setAnchoredPosition = CompileInstanceVector2Call(
            RequiredMethod(RectTransformType, "set_anchoredPosition", false, vector2Type),
            vector2Type);
        _setSizeDelta = CompileInstanceVector2Call(
            RequiredMethod(RectTransformType, "set_sizeDelta", false, vector2Type),
            vector2Type);
        _getSizeDeltaY = CompileVector2YGetter(
            RequiredMethod(RectTransformType, "get_sizeDelta", false),
            vector2Type);
        _setCanvasRenderMode = CompileInstanceEnumCall(RequiredSingleParameterMethod(
            CanvasType,
            "set_renderMode"));
        _setCanvasSortingOrder = CompileInstanceIntCall(RequiredMethod(
            CanvasType,
            "set_sortingOrder",
            false,
            typeof(int)));
        _setCanvasScaleMode = CompileInstanceEnumCall(RequiredSingleParameterMethod(
            CanvasScalerType,
            "set_uiScaleMode"));
        _setCanvasReferenceResolution = CompileInstanceVector2Call(
            RequiredMethod(CanvasScalerType, "set_referenceResolution", false, vector2Type),
            vector2Type);
        _setCanvasMatch = CompileInstanceFloatCall(RequiredMethod(
            CanvasScalerType,
            "set_matchWidthOrHeight",
            false,
            typeof(float)));
        _getTextRectTransform = CompileInstanceResult(RequiredMethod(
            TmpTextType,
            "get_rectTransform",
            false));
        _setText = CompileInstanceStringCall(RequiredMethod(
            TmpTextType,
            "set_text",
            false,
            typeof(string)));
        _setFontSize = CompileInstanceFloatCall(RequiredMethod(
            TmpTextType,
            "set_fontSize",
            false,
            typeof(float)));
        _setFont = CompileInstanceObjectCall(RequiredMethod(
            TmpTextType,
            "set_font",
            false,
            TmpFontType));
        _getFont = CompileInstanceResult(RequiredMethod(TmpTextType, "get_font", false));
        _setAlignment = CompileInstanceEnumCall(RequiredSingleParameterMethod(
            TmpTextType,
            "set_alignment"));
        _setRichText = CompileInstanceBoolCall(RequiredMethod(
            TmpTextType,
            "set_richText",
            false,
            typeof(bool)));
        _setRaycastTarget = CompileInstanceBoolCall(RequiredMethod(
            GraphicType,
            "set_raycastTarget",
            false,
            typeof(bool)));
        _setGraphicColor = CompileInstanceColorCall(
            RequiredMethod(GraphicType, "set_color", false, colorType),
            colorType);
        _setLocalizedFont = CompileStaticObjectCall(RequiredMethod(
            rdStringType,
            "SetLocalizedFont",
            true,
            TmpTextType));
    }

    public nint CreateGameObject(string name)
    {
        var rectTransform = GetTypeObject(RectTransformType);
        return Track(_createGameObject(name, [rectTransform]));
    }

    public nint Instantiate(nint original)
        => Track(_instantiate(Wrap(ObjectType, original)));

    public void Destroy(nint obj)
    {
        _destroy(Wrap(ObjectType, obj));
        Forget(obj);
    }

    public nint AddComponent(nint gameObject, Type componentType)
        => Track(_addComponent(
            Wrap(GameObjectType, gameObject),
            GetTypeObject(componentType)));

    public nint GetComponent(nint component, Type componentType)
        => Track(_getComponent(
            Wrap(ComponentType, component),
            GetTypeObject(componentType)));

    public nint GetTransform(nint gameObject)
        => Track(_getTransform(Wrap(GameObjectType, gameObject)));

    public nint FindChild(nint transform, string name)
        => Track(_findTransform(Wrap(TransformType, transform), name));

    public nint GetRectTransform(nint text, nint fallbackGameObject)
    {
        var rect = Track(_getTextRectTransform(Wrap(TmpTextType, text)));
        return rect != nint.Zero ? rect : GetTransform(fallbackGameObject);
    }

    public void SetActive(nint gameObject, bool active)
        => _setActive(Wrap(GameObjectType, gameObject), active);

    public void DontDestroyOnLoad(nint gameObject)
        => _dontDestroyOnLoad(Wrap(ObjectType, gameObject));

    public void SetParent(nint transform, nint parent)
        => _setParent(
            Wrap(TransformType, transform),
            Wrap(TransformType, parent),
            false);

    public void SetTopLeftRect(nint rect, float x, float y, float width, float height)
    {
        var proxy = Wrap(RectTransformType, rect);
        _setAnchorMin(proxy, 0f, 1f);
        _setAnchorMax(proxy, 0f, 1f);
        _setPivot(proxy, 0f, 1f);
        _setAnchoredPosition(proxy, x, -y);
        _setSizeDelta(proxy, width, height);
    }

    public void SetSizeDeltaX(nint rect, float width)
    {
        var proxy = Wrap(RectTransformType, rect);
        _setSizeDelta(proxy, width, _getSizeDeltaY(proxy));
    }

    public void SetCanvasRenderMode(nint canvas, int mode)
        => _setCanvasRenderMode(Wrap(CanvasType, canvas), mode);

    public void SetCanvasSortingOrder(nint canvas, int order)
        => _setCanvasSortingOrder(Wrap(CanvasType, canvas), order);

    public void SetCanvasScaleMode(nint scaler, int mode)
        => _setCanvasScaleMode(Wrap(CanvasScalerType, scaler), mode);

    public void SetCanvasReferenceResolution(nint scaler, float width, float height)
        => _setCanvasReferenceResolution(Wrap(CanvasScalerType, scaler), width, height);

    public void SetCanvasMatch(nint scaler, float value)
        => _setCanvasMatch(Wrap(CanvasScalerType, scaler), value);

    public void ConfigureText(nint text, bool richText)
    {
        var proxy = Wrap(TmpTextType, text);
        _setRaycastTarget(Wrap(GraphicType, text), false);
        _setAlignment(proxy, 257);
        _setRichText(proxy, richText);
    }

    public void SetRaycastTarget(nint graphic, bool enabled)
        => _setRaycastTarget(Wrap(GraphicType, graphic), enabled);

    public void SetText(nint text, string value)
        => _setText(Wrap(TmpTextType, text), value);

    public void SetFontSize(nint text, float size)
        => _setFontSize(Wrap(TmpTextType, text), size);

    public void SetFont(nint text, nint font)
        => _setFont(Wrap(TmpTextType, text), Wrap(TmpFontType, font));

    public void SetGraphicColor(nint graphic, float r, float g, float b, float a)
        => _setGraphicColor(Wrap(GraphicType, graphic), r, g, b, a);

    public nint ApplyLocalizedFont(nint text)
    {
        var proxy = Wrap(TmpTextType, text);
        if (_localizedFontFailed)
            return nint.Zero;

        try
        {
            _setLocalizedFont(proxy);
            return Track(_getFont(proxy));
        }
        catch (Exception exception)
        {
            _localizedFontFailed = true;
            Logger.Warn(
                "PcCompatGeneratedUnityHudApi",
                "RDString.SetLocalizedFont disabled after failure: " + exception.Message);
            return nint.Zero;
        }
    }

    public nint GetFont(nint text)
        => Track(_getFont(Wrap(TmpTextType, text)));

    public nint CreateBatchMesh()
    {
        EnsureMeshApi();
        var mesh = Track(_createMesh!());
        _markMeshDynamic!(Wrap(_meshType!, mesh));
        return mesh;
    }

    public nint GetGraphicMaterial(nint graphic)
    {
        EnsureMeshApi();
        return Track(_getGraphicMaterial!(Wrap(GraphicType, graphic)));
    }

    public void SetCanvasRendererMaterial(nint renderer, nint material)
    {
        EnsureMeshApi();
        _setCanvasRendererMaterial!(
            Wrap(_canvasRendererType!, renderer),
            Wrap(RequiredType(Core, "UnityEngine.Material"), material),
            0);
    }

    public void SetCanvasRendererColor(
        nint renderer,
        float r,
        float g,
        float b,
        float a)
    {
        EnsureMeshApi();
        _setCanvasRendererColor!(Wrap(_canvasRendererType!, renderer), r, g, b, a);
    }

    public void SetBatchMesh(
        nint renderer,
        nint mesh,
        IReadOnlyList<(float Left, float Top, float Right, float Bottom)> quads)
    {
        EnsureMeshApi();
        var sourceCount = Math.Min(quads.Count, 256);
        var capacity = sourceCount switch
        {
            <= 1 => 1,
            <= 4 => 4,
            <= 16 => 16,
            <= 64 => 64,
            _ => 256
        };
        var buffer = GetMeshUploadBuffer(capacity);
        // Unity/IL2CPP rejects an empty Mesh submission on some Android builds.
        // Keep one zero-area quad and clear only the previously active tail.
        var activeQuads = Math.Max(sourceCount, 1);
        for (var index = 0; index < activeQuads; ++index)
        {
            var quad = index < sourceCount ? quads[index] : default;
            var vertex = index * 4;
            _setVector3ArrayItem!(buffer.Vertices, vertex, quad.Left, quad.Top, 0f);
            _setVector3ArrayItem(buffer.Vertices, vertex + 1, quad.Right, quad.Top, 0f);
            _setVector3ArrayItem(buffer.Vertices, vertex + 2, quad.Right, quad.Bottom, 0f);
            _setVector3ArrayItem(buffer.Vertices, vertex + 3, quad.Left, quad.Bottom, 0f);
        }
        for (var index = activeQuads; index < buffer.ActiveQuads; ++index)
        {
            var vertex = index * 4;
            _setVector3ArrayItem!(buffer.Vertices, vertex, 0f, 0f, 0f);
            _setVector3ArrayItem(buffer.Vertices, vertex + 1, 0f, 0f, 0f);
            _setVector3ArrayItem(buffer.Vertices, vertex + 2, 0f, 0f, 0f);
            _setVector3ArrayItem(buffer.Vertices, vertex + 3, 0f, 0f, 0f);
        }
        buffer.ActiveQuads = activeQuads;
        var meshProxy = Wrap(_meshType!, mesh);
        _setMeshVertices!(meshProxy, buffer.Vertices);
        if (!_meshTriangleCapacities.TryGetValue(mesh, out var currentCapacity) ||
            currentCapacity != capacity)
        {
            _setMeshTriangles!(meshProxy, buffer.Triangles);
            _meshTriangleCapacities[mesh] = capacity;
        }
        _recalculateMeshBounds!(meshProxy);
        _setCanvasRendererMesh!(Wrap(_canvasRendererType!, renderer), meshProxy);
    }

    private MeshUploadBuffer GetMeshUploadBuffer(int capacity)
    {
        if (_meshUploadBuffers.TryGetValue(capacity, out var existing))
            return existing;
        var vertices = Activator.CreateInstance(
            _vector3ArrayType!,
            [checked((long)capacity * 4)])
            ?? throw new InvalidOperationException("Could not allocate reusable IL2CPP Vector3 array.");
        var triangleValues = new int[capacity * 6];
        for (var index = 0; index < capacity; ++index)
        {
            var vertex = index * 4;
            var triangle = index * 6;
            triangleValues[triangle] = vertex;
            triangleValues[triangle + 1] = vertex + 1;
            triangleValues[triangle + 2] = vertex + 2;
            triangleValues[triangle + 3] = vertex + 2;
            triangleValues[triangle + 4] = vertex + 3;
            triangleValues[triangle + 5] = vertex;
        }
        var created = new MeshUploadBuffer(
            vertices,
            new Il2CppStructArray<int>(triangleValues));
        _meshUploadBuffers.Add(capacity, created);
        return created;
    }

    private void EnsureMeshApi()
    {
        if (_meshApiInitialized)
            return;
        var materialType = RequiredType(Core, "UnityEngine.Material");
        _meshType = RequiredType(Core, "UnityEngine.Mesh");
        _vector3Type = RequiredType(Core, "UnityEngine.Vector3");
        _vector3ArrayType = typeof(Il2CppStructArray<>).MakeGenericType(_vector3Type);
        _canvasRendererType = RequiredType("UnityEngine.UIModule", "UnityEngine.CanvasRenderer");
        var colorType = RequiredType(Core, "UnityEngine.Color");
        var meshConstructor = _meshType.GetConstructor(Type.EmptyTypes)
            ?? throw new MissingMethodException(_meshType.FullName, ".ctor()");
        var vectorConstructor = _vector3Type.GetConstructor(
            [typeof(float), typeof(float), typeof(float)])
            ?? throw new MissingMethodException(_vector3Type.FullName, ".ctor(Single, Single, Single)");
        _createMesh = CompileObjectConstructor(meshConstructor);
        _setVector3ArrayItem = CompileVector3ArrayItemSetter(
            _vector3ArrayType,
            vectorConstructor);
        _markMeshDynamic = CompileInstanceNoArgCall(RequiredMethod(
            _meshType, "MarkDynamic", false));
        _recalculateMeshBounds = CompileInstanceNoArgCall(RequiredMethod(
            _meshType, "RecalculateBounds", false));
        _setMeshVertices = CompileInstanceObjectCall(RequiredSingleParameterMethod(
            _meshType, "set_vertices"));
        _setMeshTriangles = CompileInstanceObjectCall(RequiredSingleParameterMethod(
            _meshType, "set_triangles"));
        _setCanvasRendererMesh = CompileInstanceObjectCall(RequiredMethod(
            _canvasRendererType, "SetMesh", false, _meshType));
        _setCanvasRendererMaterial = CompileInstanceObjectIntCall(RequiredMethod(
            _canvasRendererType, "SetMaterial", false, materialType, typeof(int)));
        _setCanvasRendererColor = CompileInstanceColorCall(RequiredMethod(
            _canvasRendererType, "SetColor", false, colorType), colorType);
        _getGraphicMaterial = CompileInstanceResult(RequiredMethod(
            GraphicType, "get_material", false));
        _meshApiInitialized = true;
    }

    public void Forget(nint pointer)
    {
        if (pointer == nint.Zero)
            return;
        foreach (var key in _wrappers.Keys.Where(key => key.Pointer == pointer).ToArray())
            _wrappers.Remove(key);
        _meshTriangleCapacities.Remove(pointer);
    }

    public void Clear()
    {
        _wrappers.Clear();
        _typeObjects.Clear();
        _meshUploadBuffers.Clear();
        _meshTriangleCapacities.Clear();
    }

    private object Wrap(Type type, nint pointer)
    {
        if (pointer == nint.Zero)
            throw new NullReferenceException($"Cannot wrap null IL2CPP object as {type.FullName}.");
        var key = (type, pointer);
        if (_wrappers.TryGetValue(key, out var cached))
            return cached;
        if (!_pointerConstructors.TryGetValue(type, out var constructor))
        {
            constructor = type.GetConstructor([typeof(IntPtr)])
                ?? throw new MissingMethodException(type.FullName, ".ctor(IntPtr)");
            _pointerConstructors.Add(type, constructor);
        }
        var proxy = constructor.Invoke([pointer]);
        _wrappers.Add(key, proxy);
        return proxy;
    }

    private nint Track(object? proxy)
    {
        if (proxy is null)
            return nint.Zero;
        if (proxy is not Il2CppObjectBase il2CppObject)
            throw new InvalidCastException("Generated Unity proxy is not an Il2CppObjectBase.");
        var pointer = il2CppObject.Pointer;
        _wrappers.TryAdd((proxy.GetType(), pointer), proxy);
        return pointer;
    }

    private Il2CppSystem.Type GetTypeObject(Type proxyType)
    {
        if (_typeObjects.TryGetValue(proxyType, out var cached))
            return cached;
        var result = Il2CppType.From(proxyType);
        _typeObjects.Add(proxyType, result);
        return result;
    }

    private static Type RequiredType(string assemblyName, string fullTypeName)
        => PcCompatIl2CppInteropBootstrap.TryGetProxyType(assemblyName, fullTypeName, out var type)
            ? type
            : throw new TypeLoadException(
                $"Generated proxy type is unavailable: {assemblyName}:{fullTypeName}");

    private static MethodInfo RequiredMethod(
        Type type,
        string name,
        bool isStatic,
        params Type[] parameters)
    {
        var flags = BindingFlags.Public |
                    (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        return type.GetMethod(name, flags, binder: null, parameters, modifiers: null)
               ?? throw new MissingMethodException(type.FullName, name);
    }

    private static MethodInfo RequiredSingleParameterMethod(Type type, string name)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == name && method.GetParameters().Length == 1)
            .ToArray();
        return methods.Length == 1
            ? methods[0]
            : throw new MissingMethodException(type.FullName, name + "/1");
    }

    private static Func<string, Il2CppSystem.Type[], object> CompileGameObjectConstructor(
        ConstructorInfo constructor)
    {
        var name = Expression.Parameter(typeof(string), "name");
        var components = Expression.Parameter(typeof(Il2CppSystem.Type[]), "components");
        return Expression.Lambda<Func<string, Il2CppSystem.Type[], object>>(
            Expression.Convert(Expression.New(constructor, name, components), typeof(object)),
            name,
            components).Compile();
    }

    private static Func<object> CompileObjectConstructor(ConstructorInfo constructor)
        => Expression.Lambda<Func<object>>(
            Expression.Convert(Expression.New(constructor), typeof(object))).Compile();

    private static Action<object, int, float, float, float> CompileVector3ArrayItemSetter(
        Type arrayType,
        ConstructorInfo constructor)
    {
        var array = Expression.Parameter(typeof(object), "array");
        var index = Expression.Parameter(typeof(int), "index");
        var x = Expression.Parameter(typeof(float), "x");
        var y = Expression.Parameter(typeof(float), "y");
        var z = Expression.Parameter(typeof(float), "z");
        var item = arrayType.GetProperty("Item")
            ?? throw new MissingMemberException(arrayType.FullName, "Item");
        return Expression.Lambda<Action<object, int, float, float, float>>(
            Expression.Assign(
                Expression.MakeIndex(Expression.Convert(array, arrayType), item, [index]),
                Expression.New(constructor, x, y, z)),
            array,
            index,
            x,
            y,
            z).Compile();
    }

    private sealed class MeshUploadBuffer(object vertices, Il2CppStructArray<int> triangles)
    {
        public object Vertices { get; } = vertices;
        public Il2CppStructArray<int> Triangles { get; } = triangles;
        public int ActiveQuads { get; set; }
    }

    private static Func<object, object?> CompileStaticObjectResult(MethodInfo method)
    {
        var value = Expression.Parameter(typeof(object), "value");
        return Expression.Lambda<Func<object, object?>>(
            Expression.Convert(
                Expression.Call(method, Expression.Convert(value, method.GetParameters()[0].ParameterType)),
                typeof(object)),
            value).Compile();
    }

    private static Action<object> CompileStaticObjectCall(MethodInfo method)
    {
        var value = Expression.Parameter(typeof(object), "value");
        return Expression.Lambda<Action<object>>(
            Expression.Call(method, Expression.Convert(value, method.GetParameters()[0].ParameterType)),
            value).Compile();
    }

    private static Func<object, object, object?> CompileInstanceObjectResult(MethodInfo method)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var value = Expression.Parameter(typeof(object), "value");
        return Expression.Lambda<Func<object, object, object?>>(
            Expression.Convert(Expression.Call(
                Expression.Convert(instance, method.DeclaringType!),
                method,
                Expression.Convert(value, method.GetParameters()[0].ParameterType)), typeof(object)),
            instance,
            value).Compile();
    }

    private static Func<object, object?> CompileInstanceResult(MethodInfo method)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        return Expression.Lambda<Func<object, object?>>(
            Expression.Convert(
                Expression.Call(Expression.Convert(instance, method.DeclaringType!), method),
                typeof(object)),
            instance).Compile();
    }

    private static Func<object, string, object?> CompileInstanceStringResult(MethodInfo method)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var value = Expression.Parameter(typeof(string), "value");
        return Expression.Lambda<Func<object, string, object?>>(
            Expression.Convert(Expression.Call(
                Expression.Convert(instance, method.DeclaringType!), method, value), typeof(object)),
            instance,
            value).Compile();
    }

    private static Action<object, object> CompileInstanceObjectCall(MethodInfo method)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var value = Expression.Parameter(typeof(object), "value");
        return Expression.Lambda<Action<object, object>>(
            Expression.Call(
                Expression.Convert(instance, method.DeclaringType!),
                method,
                Expression.Convert(value, method.GetParameters()[0].ParameterType)),
            instance,
            value).Compile();
    }

    private static Action<object> CompileInstanceNoArgCall(MethodInfo method)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        return Expression.Lambda<Action<object>>(
            Expression.Call(Expression.Convert(instance, method.DeclaringType!), method),
            instance).Compile();
    }

    private static Action<object, object, int> CompileInstanceObjectIntCall(MethodInfo method)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var value = Expression.Parameter(typeof(object), "value");
        var index = Expression.Parameter(typeof(int), "index");
        return Expression.Lambda<Action<object, object, int>>(
            Expression.Call(
                Expression.Convert(instance, method.DeclaringType!),
                method,
                Expression.Convert(value, method.GetParameters()[0].ParameterType),
                index),
            instance,
            value,
            index).Compile();
    }

    private static Action<object, object, bool> CompileInstanceObjectBoolCall(MethodInfo method)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var value = Expression.Parameter(typeof(object), "value");
        var flag = Expression.Parameter(typeof(bool), "flag");
        return Expression.Lambda<Action<object, object, bool>>(
            Expression.Call(
                Expression.Convert(instance, method.DeclaringType!),
                method,
                Expression.Convert(value, method.GetParameters()[0].ParameterType),
                flag),
            instance,
            value,
            flag).Compile();
    }

    private static Action<object, bool> CompileInstanceBoolCall(MethodInfo method)
        => CompileInstanceValueCall<bool>(method);

    private static Action<object, int> CompileInstanceIntCall(MethodInfo method)
        => CompileInstanceValueCall<int>(method);

    private static Action<object, float> CompileInstanceFloatCall(MethodInfo method)
        => CompileInstanceValueCall<float>(method);

    private static Action<object, string> CompileInstanceStringCall(MethodInfo method)
        => CompileInstanceValueCall<string>(method);

    private static Action<object, TValue> CompileInstanceValueCall<TValue>(MethodInfo method)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var value = Expression.Parameter(typeof(TValue), "value");
        return Expression.Lambda<Action<object, TValue>>(
            Expression.Call(Expression.Convert(instance, method.DeclaringType!), method, value),
            instance,
            value).Compile();
    }

    private static Action<object, int> CompileInstanceEnumCall(MethodInfo method)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var value = Expression.Parameter(typeof(int), "value");
        return Expression.Lambda<Action<object, int>>(
            Expression.Call(
                Expression.Convert(instance, method.DeclaringType!),
                method,
                Expression.Convert(value, method.GetParameters()[0].ParameterType)),
            instance,
            value).Compile();
    }

    private static Action<object, float, float> CompileInstanceVector2Call(
        MethodInfo method,
        Type vectorType)
    {
        var constructor = vectorType.GetConstructor([typeof(float), typeof(float)])
            ?? throw new MissingMethodException(vectorType.FullName, ".ctor(Single, Single)");
        var instance = Expression.Parameter(typeof(object), "instance");
        var x = Expression.Parameter(typeof(float), "x");
        var y = Expression.Parameter(typeof(float), "y");
        return Expression.Lambda<Action<object, float, float>>(
            Expression.Call(
                Expression.Convert(instance, method.DeclaringType!),
                method,
                Expression.New(constructor, x, y)),
            instance,
            x,
            y).Compile();
    }

    private static Func<object, float> CompileVector2YGetter(MethodInfo method, Type vectorType)
    {
        var yField = vectorType.GetField("y", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingFieldException(vectorType.FullName, "y");
        var instance = Expression.Parameter(typeof(object), "instance");
        return Expression.Lambda<Func<object, float>>(
            Expression.Field(
                Expression.Call(Expression.Convert(instance, method.DeclaringType!), method),
                yField),
            instance).Compile();
    }

    private static Action<object, float, float, float, float> CompileInstanceColorCall(
        MethodInfo method,
        Type colorType)
    {
        var constructor = colorType.GetConstructor(
            [typeof(float), typeof(float), typeof(float), typeof(float)])
            ?? throw new MissingMethodException(colorType.FullName, ".ctor(Single, Single, Single, Single)");
        var instance = Expression.Parameter(typeof(object), "instance");
        var r = Expression.Parameter(typeof(float), "r");
        var g = Expression.Parameter(typeof(float), "g");
        var b = Expression.Parameter(typeof(float), "b");
        var a = Expression.Parameter(typeof(float), "a");
        return Expression.Lambda<Action<object, float, float, float, float>>(
            Expression.Call(
                Expression.Convert(instance, method.DeclaringType!),
                method,
                Expression.New(constructor, r, g, b, a)),
            instance,
            r,
            g,
            b,
            a).Compile();
    }
}

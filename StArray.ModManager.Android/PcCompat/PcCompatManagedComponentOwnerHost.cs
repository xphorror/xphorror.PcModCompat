using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Android.PcCompat;

internal static class PcCompatManagedComponentOwnerHost
{
    private static readonly ConcurrentDictionary<Type, Func<object, object?>> GameObjectResolvers = new();
    private static readonly ConcurrentDictionary<Type, Func<object, object?>> TransformResolvers = new();
    private static readonly ConcurrentDictionary<Type, Func<object, bool>> AliveResolvers = new();
    private static readonly ConcurrentDictionary<Type, Func<object?, object?, bool>> EqualityResolvers = new();
    private static readonly ConcurrentDictionary<Type, Func<object, bool>> ActiveResolvers = new();
    private static readonly ConcurrentDictionary<Type, Func<object, bool>> BehaviourEnabledReaders = new();
    private static readonly ConcurrentDictionary<Type, Action<object, bool>> BehaviourEnabledWriters = new();
    private static readonly ConcurrentDictionary<Type, Action<object, bool>> GameObjectActiveWriters = new();
    private static readonly ConcurrentDictionary<Type, Func<object, object>> AnchoredPositionReaders = new();
    private static readonly ConcurrentDictionary<Type, Action<object, object>> AnchoredPositionWriters = new();
    private static readonly ConcurrentDictionary<(Type Type, string Method), Func<object, object, object?>>
        ComponentTypeInvokers = new();
    private static readonly object DontDestroyInvokerLock = new();
    private static readonly object DestroyInvokerLock = new();
    private static readonly object ScaledDeltaTimeLock = new();
    private static Action<object>? s_dontDestroyInvoker;
    private static Action<object>? s_destroyInvoker;
    private static readonly object GameObjectFactoryLock = new();
    private static readonly object InstantiateInvokerLock = new();
    private static Func<string, object>? s_gameObjectFactory;
    private static Func<object, object?, object>? s_instantiateInvoker;
    private static readonly ConcurrentDictionary<Type, Func<object, object, object>>
        GenericInstantiateWithParentInvokers = new();
    private static Action<object, float>? s_destroyDelayedInvoker;
    private static Func<float>? s_scaledDeltaTime;
    private static readonly ConcurrentDictionary<Type, Func<object, float>> YieldDelayResolvers = new();
    private static readonly ConcurrentDictionary<Type, Action<Il2CppObjectBase, nint>>
        RenderComponentBinders = new();
    private static readonly ConcurrentDictionary<Type, ConstructorInfo> ProxyPointerConstructors = new();
    private static readonly PcCompatManagedComponentHostOperations HostOperations = new(
        ResolveTransform,
        IsNativeComponentType,
        IsManagedComponentTypeOwnedByMod,
        AddNativeComponent,
        GetNativeComponent,
        GetNativeComponents,
        ReadNativeBehaviourEnabled,
        WriteNativeBehaviourEnabled,
        IsGameObject,
        IsNativeObjectAlive,
        DontDestroyNativeObject,
        DestroyNativeObject,
        DestroyNativeObjectDelayed,
        ResolveYieldDelay,
        ReadScaledDeltaTime,
        CreateNativeGameObject,
        InstantiateNativeObject,
        ReadNativeAnchoredPosition,
        WriteNativeAnchoredPosition,
        BindManagedRenderComponent,
        ReadNativeInstancePointer,
        WrapNativeProxyPointer,
        SetNativeGameObjectActive,
        PcCompatNativeRenderHostRegistry.Register,
        PcCompatNativeRenderHostRegistry.Unregister,
        PcCompatNativeRenderHostRegistry.Clear,
        AreNativeObjectsEqual);

    public static void Install()
    {
        PcCompatManagedDynamicGetterBridge.RegisterGeneratedProxyTypeProbe(
            PcCompatIl2CppInteropBootstrap.IsGeneratedProxyType);
        PcCompatManagedDynamicGetterBridge.RegisterObjectPointerProbe(
            static value => value is Il2CppObjectBase native && !native.WasCollected
                ? native.Pointer
                : nint.Zero);
        PcCompatManagedComponentBridge.RegisterOwnerResolver(Resolve);
        PcCompatManagedComponentBridge.RegisterHostOperations(HostOperations);
        PcCompatManagedComponentBridge.RegisterRenderProxyTypeResolver(
            static (assembly, typeName) =>
                PcCompatIl2CppInteropBootstrap.TryGetProxyType(assembly, typeName, out var type)
                    ? type
                    : null);
    }

    /// <summary>
    /// Binds a MOD component type to an existing host component's IL2CPP instance, without running
    /// any native constructor on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three steps are the ones <c>Il2CppObjectBase.InitializerStore&lt;T&gt;</c> performs for a
    /// type with only a parameterless constructor: allocate an uninitialized managed shell, give it a
    /// GC handle on the existing pointer, and set <c>isWrapped</c> so a later
    /// <c>CreateGCHandle</c> is a no-op. They are reimplemented here rather than reused because
    /// InitializerStore is generic over a statically known T, and this type is only known at runtime.
    /// </para>
    /// <para>
    /// InitializerStore also calls the parameterless constructor afterwards. This does the same, but
    /// it is only safe because the rewriter blanked the MOD constructor's
    /// <c>call MaskableGraphic::.ctor()</c>: the proxy constructor's body ends in an
    /// <c>il2cpp_runtime_invoke</c> of the native base constructor on <c>this</c>, which is the host
    /// component <c>AddComponent</c> already constructed. Running it would re-run native construction
    /// on a live object. What remains of the MOD constructor is its field initialization, which is
    /// exactly what has to run.
    /// </para>
    /// </remarks>
    private static object BindManagedRenderComponent(Type componentType, object host)
    {
        if (host is not Il2CppObjectBase nativeHost)
        {
            throw new InvalidOperationException(
                "Render component host is not an IL2CPP proxy: " +
                host.GetType().AssemblyQualifiedName);
        }
        if (nativeHost.WasCollected)
            throw new InvalidOperationException("Render component host was already collected.");

        var pointer = nativeHost.Pointer;
        if (pointer == nint.Zero)
            throw new InvalidOperationException("Render component host has a null instance pointer.");

        var instance = RuntimeHelpers.GetUninitializedObject(componentType);
        if (instance is not Il2CppObjectBase nativeInstance)
        {
            throw new InvalidOperationException(
                "Render component type does not derive Il2CppObjectBase: " +
                componentType.AssemblyQualifiedName);
        }

        RenderComponentBinders.GetOrAdd(componentType, BuildRenderComponentBinder)(
            nativeInstance,
            pointer);
        return instance;
    }

    /// <summary>
    /// Compiles the bind-and-initialize sequence for one component type.
    /// </summary>
    /// <remarks>
    /// <c>CreateGCHandle</c> and <c>isWrapped</c> are internal to Il2CppInterop.Runtime, so they are
    /// reached by reflection once per type rather than named directly - adding an InternalsVisibleTo
    /// to a vendored dependency for this would be a heavier change than a cached lookup.
    /// </remarks>
    private static Action<Il2CppObjectBase, nint> BuildRenderComponentBinder(Type componentType)
    {
        const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        var createGcHandle = typeof(Il2CppObjectBase).GetMethod("CreateGCHandle", instanceFlags)
                             ?? throw new MissingMethodException(
                                 nameof(Il2CppObjectBase),
                                 "CreateGCHandle");
        var isWrapped = typeof(Il2CppObjectBase).GetField("isWrapped", instanceFlags)
                        ?? throw new MissingFieldException(nameof(Il2CppObjectBase), "isWrapped");
        var constructor = componentType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null);

        return (instance, pointer) =>
        {
            createGcHandle.Invoke(instance, [pointer]);
            isWrapped.SetValue(instance, true);
            // Field initialization only - see the remarks on BindManagedRenderComponent for why the
            // base constructor call inside it has already been blanked by the rewriter. A type with
            // no parameterless constructor is not rejected: it simply has no field initializers to
            // run, and the rewriter's own registration check already proved the type's shape.
            constructor?.Invoke(instance, null);
        };
    }

    private static long ReadNativeInstancePointer(object source)
        => source is Il2CppObjectBase { WasCollected: false } native
            ? native.Pointer.ToInt64()
            : 0L;

    internal static object WrapNativeProxyPointer(Type proxyType, nint pointer)
    {
        var constructor = ProxyPointerConstructors.GetOrAdd(proxyType, static type =>
            type.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [typeof(nint)],
                modifiers: null)
            ?? throw new MissingMethodException(type.FullName, ".ctor(IntPtr)"));
        // A fresh wrapper per call. The pointer is a stack-scoped VertexHelper that Unity owns for
        // the duration of the callback, so caching one by pointer value would risk handing back a
        // wrapper for a different VertexHelper that reused the address.
        return constructor.Invoke([pointer]);
    }

    private static PcCompatManagedComponentOwnerSnapshot Resolve(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var gameObject = source.GetType().FullName == "UnityEngine.GameObject"
            ? source
            : GameObjectResolvers.GetOrAdd(source.GetType(), BuildGameObjectResolver)(source)
              ?? throw new InvalidOperationException(
                  $"Unity component owner has no GameObject: {source.GetType().AssemblyQualifiedName}");
        if (gameObject is not Il2CppObjectBase nativeObject)
        {
            throw new InvalidOperationException(
                $"Managed component owner is not an IL2CPP proxy: {gameObject.GetType().AssemblyQualifiedName}");
        }
        if (nativeObject.WasCollected)
            return new PcCompatManagedComponentOwnerSnapshot(0, gameObject, false, false);

        var pointer = nativeObject.Pointer;
        var alive = AliveResolvers.GetOrAdd(gameObject.GetType(), BuildAliveResolver)(gameObject);
        var active = alive && ActiveResolvers.GetOrAdd(gameObject.GetType(), BuildActiveResolver)(gameObject);
        return new PcCompatManagedComponentOwnerSnapshot(pointer.ToInt64(), gameObject, alive, active);
    }

    private static object ResolveTransform(object source)
        => TransformResolvers.GetOrAdd(source.GetType(), BuildTransformResolver)(source)
           ?? throw new InvalidOperationException(
               $"Unity owner has no Transform: {source.GetType().AssemblyQualifiedName}");

    private static bool IsNativeComponentType(Type type)
    {
        if (!PcCompatIl2CppInteropBootstrap.IsGeneratedProxyType(type))
            return false;
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.FullName == "UnityEngine.Component" &&
                PcCompatIl2CppInteropBootstrap.IsGeneratedProxyType(current))
                return true;
        }
        return false;
    }

    private static bool IsManagedComponentTypeOwnedByMod(Type type, string modId)
    {
        var context = AssemblyLoadContext.GetLoadContext(type.Assembly);
        return context != null && string.Equals(
            context.Name,
            $"PcCompat:{modId}",
            StringComparison.OrdinalIgnoreCase);
    }

    private static object AddNativeComponent(object owner, Type componentType)
        => InvokeNativeComponentTypeMethod(owner, componentType, "AddComponent")
           ?? throw new InvalidOperationException(
               $"Native GameObject.AddComponent returned null for {componentType.AssemblyQualifiedName}.");

    private static object? GetNativeComponent(object owner, Type componentType)
        => InvokeNativeComponentTypeMethod(owner, componentType, "GetComponent");

    private static IReadOnlyList<object> GetNativeComponents(object owner, Type componentType)
    {
        var result = InvokeNativeComponentTypeMethod(owner, componentType, "GetComponents");
        if (result is null)
            return Array.Empty<object>();
        if (result is not IEnumerable enumerable)
        {
            throw new InvalidOperationException(
                $"Native GameObject.GetComponents returned a non-enumerable value: " +
                result.GetType().AssemblyQualifiedName);
        }

        var components = new List<object>();
        foreach (var component in enumerable)
        {
            if (component != null)
                components.Add(component);
        }
        return components;
    }

    private static bool ReadNativeBehaviourEnabled(object source)
        => BehaviourEnabledReaders.GetOrAdd(
            source.GetType(),
            static type => BuildBooleanPropertyReader(type, "get_enabled"))(source);

    private static void WriteNativeBehaviourEnabled(object source, bool enabled)
        => BehaviourEnabledWriters.GetOrAdd(
            source.GetType(),
            static type => BuildBooleanPropertyWriter(type, "set_enabled"))(source, enabled);

    private static void SetNativeGameObjectActive(object source, bool active)
        => GameObjectActiveWriters.GetOrAdd(
            source.GetType(),
            static type => BuildBooleanPropertyWriter(type, "SetActive"))(source, active);

    private static object? InvokeNativeComponentTypeMethod(
        object owner,
        Type componentType,
        string methodName)
    {
        var nativeType = Il2CppType.From(componentType);
        var invoker = ComponentTypeInvokers.GetOrAdd(
            (owner.GetType(), methodName),
            static key => BuildComponentTypeInvoker(key.Type, key.Method));
        return invoker(owner, nativeType);
    }

    private static bool IsGameObject(object source)
        => source.GetType().FullName == "UnityEngine.GameObject" &&
           PcCompatIl2CppInteropBootstrap.IsGeneratedProxyType(source.GetType());

    private static bool IsNativeObjectAlive(object source)
    {
        if (source is not Il2CppObjectBase nativeObject || nativeObject.WasCollected)
            return false;
        return AliveResolvers.GetOrAdd(source.GetType(), BuildAliveResolver)(source);
    }

    private static bool AreNativeObjectsEqual(object? left, object? right)
    {
        var source = left ?? right;
        if (source is null)
            return true;
        var unityObjectType = ResolveUnityObjectProxyType(source.GetType());
        return EqualityResolvers.GetOrAdd(
            unityObjectType,
            BuildEqualityResolver)(left, right);
    }

    private static void DontDestroyNativeObject(object target)
    {
        var invoker = Volatile.Read(ref s_dontDestroyInvoker);
        if (invoker == null)
        {
            lock (DontDestroyInvokerLock)
            {
                invoker = s_dontDestroyInvoker;
                if (invoker == null)
                {
                    invoker = BuildDontDestroyInvoker();
                    Volatile.Write(ref s_dontDestroyInvoker, invoker);
                }
            }
        }
        invoker(target);
    }

    private static void DestroyNativeObject(object target)
    {
        var invoker = Volatile.Read(ref s_destroyInvoker);
        if (invoker == null)
        {
            lock (DestroyInvokerLock)
            {
                invoker = s_destroyInvoker;
                if (invoker == null)
                {
                    invoker = BuildDestroyInvoker();
                    Volatile.Write(ref s_destroyInvoker, invoker);
                }
            }
        }
        invoker(target);
    }

    private static void DestroyNativeObjectDelayed(object target, float delay)
    {
        var invoker = Volatile.Read(ref s_destroyDelayedInvoker);
        if (invoker == null)
        {
            lock (DestroyInvokerLock)
            {
                invoker = s_destroyDelayedInvoker;
                if (invoker == null)
                {
                    invoker = BuildDestroyDelayedInvoker();
                    Volatile.Write(ref s_destroyDelayedInvoker, invoker);
                }
            }
        }
        invoker(target, delay);
    }

    private static PcCompatManagedYieldDelay? ResolveYieldDelay(object yielded)
    {
        var type = yielded.GetType();
        if (!PcCompatIl2CppInteropBootstrap.IsGeneratedProxyType(type))
            return null;
        var kind = type.FullName switch
        {
            "UnityEngine.WaitForSeconds" => PcCompatManagedYieldDelayKind.ScaledSeconds,
            "UnityEngine.WaitForSecondsRealtime" => PcCompatManagedYieldDelayKind.RealtimeSeconds,
            _ => (PcCompatManagedYieldDelayKind?)null
        };
        if (kind == null)
            return null;
        var seconds = YieldDelayResolvers.GetOrAdd(type, BuildYieldDelayResolver)(yielded);
        return new PcCompatManagedYieldDelay(kind.Value, seconds);
    }

    private static float ReadScaledDeltaTime()
    {
        var reader = Volatile.Read(ref s_scaledDeltaTime);
        if (reader == null)
        {
            lock (ScaledDeltaTimeLock)
            {
                reader = s_scaledDeltaTime;
                if (reader == null)
                {
                    reader = BuildScaledDeltaTimeReader();
                    Volatile.Write(ref s_scaledDeltaTime, reader);
                }
            }
        }
        return reader();
    }

    private static Func<object, object?> BuildGameObjectResolver(Type sourceType)
    {
        var method = sourceType.GetMethod(
            "get_gameObject",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null)
            ?? throw new MissingMethodException(sourceType.FullName, "get_gameObject");
        var source = Expression.Parameter(typeof(object), "source");
        return Expression.Lambda<Func<object, object?>>(
            Expression.Convert(
                Expression.Call(Expression.Convert(source, method.DeclaringType!), method),
                typeof(object)),
            source).Compile();
    }

    private static Func<object, object?> BuildTransformResolver(Type sourceType)
    {
        var method = sourceType.GetMethod(
            "get_transform",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null)
            ?? throw new MissingMethodException(sourceType.FullName, "get_transform");
        var source = Expression.Parameter(typeof(object), "source");
        return Expression.Lambda<Func<object, object?>>(
            Expression.Convert(
                Expression.Call(Expression.Convert(source, method.DeclaringType!), method),
                typeof(object)),
            source).Compile();
    }

    private static Func<object, object, object?> BuildComponentTypeInvoker(
        Type sourceType,
        string methodName)
    {
        var method = sourceType.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [typeof(Il2CppSystem.Type)],
            modifiers: null)
            ?? throw new MissingMethodException(
                sourceType.FullName,
                $"{methodName}(Il2CppSystem.Type)");
        var source = Expression.Parameter(typeof(object), "source");
        var componentType = Expression.Parameter(typeof(object), "componentType");
        return Expression.Lambda<Func<object, object, object?>>(
            Expression.Convert(
                Expression.Call(
                    Expression.Convert(source, method.DeclaringType!),
                    method,
                    Expression.Convert(componentType, typeof(Il2CppSystem.Type))),
                typeof(object)),
            source,
            componentType).Compile();
    }

    private static Action<object> BuildDontDestroyInvoker()
    {
        if (!PcCompatIl2CppInteropBootstrap.TryGetProxyType(
                "UnityEngine.CoreModule",
                "UnityEngine.Object",
                out var unityObjectType))
        {
            throw new TypeLoadException("Generated UnityEngine.Object proxy is unavailable.");
        }
        var method = unityObjectType.GetMethod(
            "DontDestroyOnLoad",
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            [unityObjectType],
            modifiers: null)
            ?? throw new MissingMethodException(unityObjectType.FullName, "DontDestroyOnLoad");
        var target = Expression.Parameter(typeof(object), "target");
        return Expression.Lambda<Action<object>>(
            Expression.Call(method, Expression.Convert(target, unityObjectType)),
            target).Compile();
    }

    private static object CreateNativeGameObject(string name)
    {
        var invoker = Volatile.Read(ref s_gameObjectFactory);
        if (invoker == null)
        {
            lock (GameObjectFactoryLock)
            {
                invoker = s_gameObjectFactory;
                if (invoker == null)
                {
                    invoker = BuildGameObjectFactory();
                    Volatile.Write(ref s_gameObjectFactory, invoker);
                }
            }
        }
        return invoker(name);
    }

    private static Func<string, object> BuildGameObjectFactory()
    {
        if (!PcCompatIl2CppInteropBootstrap.TryGetProxyType(
                "UnityEngine.CoreModule",
                "UnityEngine.GameObject",
                out var gameObjectType))
        {
            throw new TypeLoadException("Generated UnityEngine.GameObject proxy is unavailable.");
        }
        var constructor = gameObjectType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            [typeof(string)],
            modifiers: null)
            ?? throw new MissingMethodException(gameObjectType.FullName, ".ctor(String)");
        var name = Expression.Parameter(typeof(string), "name");
        return Expression.Lambda<Func<string, object>>(
            Expression.Convert(Expression.New(constructor, name), typeof(object)),
            name).Compile();
    }

    private static object InstantiateNativeObject(object original, object? parent)
    {
        var invoker = Volatile.Read(ref s_instantiateInvoker);
        if (invoker == null)
        {
            lock (InstantiateInvokerLock)
            {
                invoker = s_instantiateInvoker;
                if (invoker == null)
                {
                    invoker = BuildInstantiateInvoker();
                    Volatile.Write(ref s_instantiateInvoker, invoker);
                }
            }
        }
        return NormalizeInstantiatedObject(original, invoker(original, parent));
    }

    /// <summary>
    /// The non-generic Unity proxy returns an Object wrapper even when the native clone is a
    /// GameObject or Component. Rewrap that pointer with the prototype's concrete proxy type
    /// before ownership resolution; the rewritten caller's cast happens after the bridge returns.
    /// </summary>
    private static object NormalizeInstantiatedObject(object original, object clone)
    {
        var expectedType = original.GetType();
        if (expectedType.IsInstanceOfType(clone) ||
            !PcCompatIl2CppInteropBootstrap.IsGeneratedProxyType(expectedType) ||
            clone is not Il2CppObjectBase nativeClone ||
            nativeClone.WasCollected)
        {
            return clone;
        }

        var pointer = nativeClone.Pointer;
        if (pointer == nint.Zero)
        {
            throw new InvalidOperationException(
                $"Instantiated Unity object has a null native pointer: {expectedType.AssemblyQualifiedName}");
        }

        return WrapNativeProxyPointer(expectedType, pointer);
    }

    /// <summary>
    /// Binds the non-generic Instantiate overloads once. Dependency-closed proxy generation may
    /// expose the parent overload only as Instantiate&lt;T&gt;(T, Transform), so that fallback is
    /// inflated and compiled per concrete proxy type on first use.
    /// </summary>
    private static Func<object, object?, object> BuildInstantiateInvoker()
    {
        if (!PcCompatIl2CppInteropBootstrap.TryGetProxyType(
                "UnityEngine.CoreModule",
                "UnityEngine.Object",
                out var unityObjectType))
        {
            throw new TypeLoadException("Generated UnityEngine.Object proxy is unavailable.");
        }
        var single = unityObjectType.GetMethod(
            "Instantiate",
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            [unityObjectType],
            modifiers: null)
            ?? throw new MissingMethodException(unityObjectType.FullName, "Instantiate(Object)");
        var originalParameter = Expression.Parameter(typeof(object), "original");
        var singleInvoker = Expression.Lambda<Func<object, object>>(
            Expression.Convert(
                Expression.Call(
                    single,
                    Expression.Convert(originalParameter, unityObjectType)),
                typeof(object)),
            originalParameter).Compile();

        if (!PcCompatIl2CppInteropBootstrap.TryGetProxyType(
                "UnityEngine.CoreModule",
                "UnityEngine.Transform",
                out var transformType))
        {
            return (original, parent) =>
            {
                if (parent is null)
                    return singleInvoker(original);
                throw new MissingMethodException(
                    unityObjectType.FullName,
                    "Instantiate(Object,Transform)");
            };
        }

        var withParent = unityObjectType.GetMethod(
            "Instantiate",
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            [unityObjectType, transformType],
            modifiers: null);
        if (withParent is not null)
        {
            var parentInvoker = BuildNonGenericInstantiateWithParentInvoker(
                withParent,
                unityObjectType,
                transformType);
            return (original, parent) => parent is null
                ? singleInvoker(original)
                : parentInvoker(original, parent);
        }

        var genericWithParent = unityObjectType.GetMethods(
                BindingFlags.Static | BindingFlags.Public)
            .Where(method =>
                method.Name == "Instantiate" &&
                method.IsGenericMethodDefinition &&
                method.GetGenericArguments().Length == 1)
            .SingleOrDefault(method =>
            {
                var parameters = method.GetParameters();
                return parameters.Length == 2 &&
                       parameters[1].ParameterType == transformType;
            });
        if (genericWithParent is null)
        {
            return (original, parent) =>
            {
                if (parent is null)
                    return singleInvoker(original);
                throw new MissingMethodException(
                    unityObjectType.FullName,
                    "Instantiate(Object,Transform)");
            };
        }

        return (original, parent) =>
        {
            if (parent is null)
                return singleInvoker(original);
            var invoker = GenericInstantiateWithParentInvokers.GetOrAdd(
                original.GetType(),
                type => BuildGenericInstantiateWithParentInvoker(
                    genericWithParent,
                    type,
                    unityObjectType,
                    transformType));
            return invoker(original, parent);
        };
    }

    private static Func<object, object, object> BuildNonGenericInstantiateWithParentInvoker(
        MethodInfo method,
        Type unityObjectType,
        Type transformType)
    {
        var original = Expression.Parameter(typeof(object), "original");
        var parent = Expression.Parameter(typeof(object), "parent");
        var call = Expression.Call(
            method,
            Expression.Convert(original, unityObjectType),
            Expression.Convert(parent, transformType));
        return Expression.Lambda<Func<object, object, object>>(
            Expression.Convert(call, typeof(object)),
            original,
            parent).Compile();
    }

    private static Func<object, object, object> BuildGenericInstantiateWithParentInvoker(
        MethodInfo genericMethod,
        Type originalType,
        Type unityObjectType,
        Type transformType)
    {
        if (!unityObjectType.IsAssignableFrom(originalType))
        {
            throw new InvalidCastException(
                $"UnityEngine.Object proxy type {originalType.FullName} is not assignable to " +
                $"{unityObjectType.FullName}.");
        }

        var method = genericMethod.MakeGenericMethod(originalType);
        var original = Expression.Parameter(typeof(object), "original");
        var parent = Expression.Parameter(typeof(object), "parent");
        var call = Expression.Call(
            method,
            Expression.Convert(original, originalType),
            Expression.Convert(parent, transformType));
        return Expression.Lambda<Func<object, object, object>>(
            Expression.Convert(call, typeof(object)),
            original,
            parent).Compile();
    }

    private static Action<object> BuildDestroyInvoker()
    {
        if (!PcCompatIl2CppInteropBootstrap.TryGetProxyType(
                "UnityEngine.CoreModule",
                "UnityEngine.Object",
                out var unityObjectType))
        {
            throw new TypeLoadException("Generated UnityEngine.Object proxy is unavailable.");
        }
        var method = unityObjectType.GetMethod(
            "Destroy",
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            [unityObjectType],
            modifiers: null)
            ?? throw new MissingMethodException(unityObjectType.FullName, "Destroy");
        var target = Expression.Parameter(typeof(object), "target");
        return Expression.Lambda<Action<object>>(
            Expression.Call(method, Expression.Convert(target, unityObjectType)),
            target).Compile();
    }

    private static Action<object, float> BuildDestroyDelayedInvoker()
    {
        if (!PcCompatIl2CppInteropBootstrap.TryGetProxyType(
                "UnityEngine.CoreModule",
                "UnityEngine.Object",
                out var unityObjectType))
        {
            throw new TypeLoadException("Generated UnityEngine.Object proxy is unavailable.");
        }
        var method = unityObjectType.GetMethod(
            "Destroy",
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            [unityObjectType, typeof(float)],
            modifiers: null)
            ?? throw new MissingMethodException(unityObjectType.FullName, "Destroy(Object, Single)");
        var target = Expression.Parameter(typeof(object), "target");
        var delay = Expression.Parameter(typeof(float), "delay");
        return Expression.Lambda<Action<object, float>>(
            Expression.Call(
                method,
                Expression.Convert(target, unityObjectType),
                delay),
            target,
            delay).Compile();
    }

    private static Func<object, float> BuildYieldDelayResolver(Type yieldType)
    {
        var source = Expression.Parameter(typeof(object), "yielded");
        Expression value;
        if (yieldType.FullName == "UnityEngine.WaitForSeconds")
        {
            var property = yieldType.GetProperty(
                "m_Seconds",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMemberException(yieldType.FullName, "m_Seconds");
            value = Expression.Property(Expression.Convert(source, yieldType), property);
        }
        else
        {
            var getter = yieldType.GetMethod(
                "get_waitTime",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                Type.EmptyTypes,
                modifiers: null)
                ?? throw new MissingMethodException(yieldType.FullName, "get_waitTime");
            value = Expression.Call(Expression.Convert(source, yieldType), getter);
        }
        return Expression.Lambda<Func<object, float>>(
            Expression.Convert(value, typeof(float)),
            source).Compile();
    }

    private static Func<float> BuildScaledDeltaTimeReader()
    {
        if (!PcCompatIl2CppInteropBootstrap.TryGetProxyType(
                "UnityEngine.CoreModule",
                "UnityEngine.Time",
                out var timeType))
        {
            throw new TypeLoadException("Generated UnityEngine.Time proxy is unavailable.");
        }
        var getter = timeType.GetMethod(
            "get_deltaTime",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null)
            ?? throw new MissingMethodException(timeType.FullName, "get_deltaTime");
        return Expression.Lambda<Func<float>>(
            Expression.Convert(Expression.Call(getter), typeof(float))).Compile();
    }

    private static Func<object, bool> BuildAliveResolver(Type gameObjectType)
    {
        var unityObjectType = ResolveUnityObjectProxyType(gameObjectType);
        var method = unityObjectType.GetMethod(
            "op_Implicit",
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            [unityObjectType],
            modifiers: null)
            ?? throw new MissingMethodException(unityObjectType.FullName, "op_Implicit");
        return BuildBooleanCall(gameObjectType, method);
    }

    private static Func<object?, object?, bool> BuildEqualityResolver(Type unityObjectType)
    {
        var method = unityObjectType.GetMethod(
            "op_Equality",
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            [unityObjectType, unityObjectType],
            modifiers: null)
            ?? throw new MissingMethodException(unityObjectType.FullName, "op_Equality");
        var left = Expression.Parameter(typeof(object), "left");
        var right = Expression.Parameter(typeof(object), "right");
        return Expression.Lambda<Func<object?, object?, bool>>(
            Expression.Call(
                method,
                Expression.Convert(left, unityObjectType),
                Expression.Convert(right, unityObjectType)),
            left,
            right).Compile();
    }

    private static Type ResolveUnityObjectProxyType(Type gameObjectType)
    {
        // A generated Unity type can be declared in Unity.TextMeshPro or UnityEngine.UI while its
        // UnityEngine.Object base lives in UnityEngine.CoreModule. Looking only in the concrete
        // type's assembly therefore manufactures the exact TypeLoadException seen during cleanup.
        // Generated proxy assemblies can carry a base-type reference whose declaring module is
        // trimmed or loaded in a different order. Do not let that metadata probe prevent the
        // authoritative CoreModule lookup below from running.
        for (var current = gameObjectType; current != null;)
        {
            try
            {
                if (string.Equals(current.FullName, "UnityEngine.Object", StringComparison.Ordinal) &&
                    current.GetMethod(
                        "op_Implicit",
                        BindingFlags.Static | BindingFlags.Public,
                        binder: null,
                        [current],
                        modifiers: null) is not null)
                {
                    return current;
                }

                current = current.BaseType;
            }
            catch (TypeLoadException)
            {
                break;
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (FileLoadException)
            {
                break;
            }
        }

        if (PcCompatIl2CppInteropBootstrap.TryGetProxyType(
                "UnityEngine.CoreModule",
                "UnityEngine.Object",
                out var coreModuleObject))
        {
            return coreModuleObject;
        }

        if (PcCompatIl2CppInteropBootstrap.TryGetUniqueProxyType(
                "UnityEngine.Object",
                out var uniqueObject,
                out var error))
        {
            return uniqueObject;
        }

        throw new TypeLoadException(error ??
            $"Generated UnityEngine.Object proxy is unavailable for {gameObjectType.AssemblyQualifiedName}.");
    }

    private static Func<object, bool> BuildActiveResolver(Type gameObjectType)
    {
        var method = gameObjectType.GetMethod(
                         "get_activeInHierarchy",
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                         binder: null,
                         Type.EmptyTypes,
                         modifiers: null)
                     ?? gameObjectType.GetMethod(
                         "get_activeSelf",
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                         binder: null,
                         Type.EmptyTypes,
                         modifiers: null)
                     ?? throw new MissingMethodException(
                         gameObjectType.FullName,
                         "get_activeInHierarchy/get_activeSelf");
        return BuildBooleanCall(gameObjectType, method);
    }

    private static Func<object, bool> BuildBooleanPropertyReader(Type sourceType, string methodName)
    {
        var method = sourceType.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null)
            ?? throw new MissingMethodException(sourceType.FullName, methodName);
        return BuildBooleanCall(sourceType, method);
    }

    private static Action<object, bool> BuildBooleanPropertyWriter(Type sourceType, string methodName)
    {
        var method = sourceType.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [typeof(bool)],
            modifiers: null)
            ?? throw new MissingMethodException(sourceType.FullName, methodName);
        var source = Expression.Parameter(typeof(object), "source");
        var value = Expression.Parameter(typeof(bool), "value");
        return Expression.Lambda<Action<object, bool>>(
            Expression.Call(
                Expression.Convert(source, method.DeclaringType ?? sourceType),
                method,
                value),
            source,
            value).Compile();
    }

    private static Func<object, bool> BuildBooleanCall(Type sourceType, MethodInfo method)
    {
        var source = Expression.Parameter(typeof(object), "source");
        Expression call = method.IsStatic
            ? Expression.Call(method, Expression.Convert(source, method.GetParameters()[0].ParameterType))
            : Expression.Call(Expression.Convert(source, method.DeclaringType ?? sourceType), method);
        return Expression.Lambda<Func<object, bool>>(call, source).Compile();
    }

    /// <summary>
    /// Reads <c>anchoredPosition</c> and hands back the proxy <c>Vector2</c> boxed. The bridge only
    /// stores and replays the value, so it never has to name the generated struct type - which it
    /// could not, since the proxy assemblies are resolved at runtime.
    /// </summary>
    private static object ReadNativeAnchoredPosition(object source)
        => AnchoredPositionReaders.GetOrAdd(
            source.GetType(),
            static type => BuildBoxedPropertyReader(type, "get_anchoredPosition"))(source);

    private static void WriteNativeAnchoredPosition(object source, object value)
        => AnchoredPositionWriters.GetOrAdd(
            source.GetType(),
            static type => BuildBoxedPropertyWriter(type, "set_anchoredPosition"))(source, value);

    private static Func<object, object> BuildBoxedPropertyReader(Type sourceType, string methodName)
    {
        var method = sourceType.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null)
            ?? throw new MissingMethodException(sourceType.FullName, methodName);
        var source = Expression.Parameter(typeof(object), "source");
        return Expression.Lambda<Func<object, object>>(
            Expression.Convert(
                Expression.Call(
                    Expression.Convert(source, method.DeclaringType ?? sourceType),
                    method),
                typeof(object)),
            source).Compile();
    }

    private static Action<object, object> BuildBoxedPropertyWriter(Type sourceType, string methodName)
    {
        // The setter parameter type belongs to the generated proxy assembly and is only known at
        // runtime. Passing null to Type.GetMethod's parameter-type overload is invalid on CoreCLR;
        // enumerate the candidate methods instead, then require one unambiguous instance setter.
        var candidates = sourceType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name == methodName)
            .Where(method => method.ReturnType == typeof(void))
            .Where(method => method.GetParameters().Length == 1)
            .ToArray();
        var method = candidates.Length switch
        {
            0 => throw new MissingMethodException(
                sourceType.FullName,
                $"{methodName} with a single parameter"),
            1 => candidates[0],
            _ => throw new AmbiguousMatchException(
                $"Multiple single-parameter setters named {methodName} were found on " +
                $"{sourceType.AssemblyQualifiedName}.")
        };
        var parameters = method.GetParameters();
        var source = Expression.Parameter(typeof(object), "source");
        var value = Expression.Parameter(typeof(object), "value");
        return Expression.Lambda<Action<object, object>>(
            Expression.Call(
                Expression.Convert(source, method.DeclaringType ?? sourceType),
                method,
                // Unbox back to the exact proxy struct type. A value that came from a different
                // proxy assembly generation throws here rather than corrupting native memory.
                Expression.Convert(value, parameters[0].ParameterType)),
            source,
            value).Compile();
    }
}

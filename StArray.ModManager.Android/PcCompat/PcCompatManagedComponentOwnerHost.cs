using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
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
    private static readonly ConcurrentDictionary<Type, Func<object, bool>> ActiveResolvers = new();
    private static readonly ConcurrentDictionary<Type, Func<object, bool>> BehaviourEnabledReaders = new();
    private static readonly ConcurrentDictionary<Type, Action<object, bool>> BehaviourEnabledWriters = new();
    private static readonly ConcurrentDictionary<(Type Type, string Method), Func<object, object, object?>>
        ComponentTypeInvokers = new();
    private static readonly object DontDestroyInvokerLock = new();
    private static readonly object DestroyInvokerLock = new();
    private static readonly object ScaledDeltaTimeLock = new();
    private static Action<object>? s_dontDestroyInvoker;
    private static Action<object>? s_destroyInvoker;
    private static Action<object, float>? s_destroyDelayedInvoker;
    private static Func<float>? s_scaledDeltaTime;
    private static readonly ConcurrentDictionary<Type, Func<object, float>> YieldDelayResolvers = new();
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
        DontDestroyNativeObject,
        DestroyNativeObject,
        DestroyNativeObjectDelayed,
        ResolveYieldDelay,
        ReadScaledDeltaTime);

    public static void Install()
    {
        PcCompatManagedComponentBridge.RegisterOwnerResolver(Resolve);
        PcCompatManagedComponentBridge.RegisterHostOperations(HostOperations);
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
        var unityObjectType = gameObjectType.Assembly.GetType("UnityEngine.Object", throwOnError: true)!;
        var method = unityObjectType.GetMethod(
            "op_Implicit",
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            [unityObjectType],
            modifiers: null)
            ?? throw new MissingMethodException(unityObjectType.FullName, "op_Implicit");
        return BuildBooleanCall(gameObjectType, method);
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
}

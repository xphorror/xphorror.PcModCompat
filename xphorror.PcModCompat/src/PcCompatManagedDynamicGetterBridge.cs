using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using StArray.ModManager.Manager;

namespace Xphorror.PcModCompat;

/// <summary>
/// Replaces PC reflection getter factories in rewritten MOD assemblies. The factory ABI stays
/// unchanged; only the member binding is moved to the generated IL2CPP proxy surface.
/// </summary>
public static class PcCompatManagedDynamicGetterBridge
{
    private enum GetterKind
    {
        InstanceObject,
        InstanceTyped,
        StaticField,
        StaticProperty,
        StaticMember
    }

    private readonly record struct GetterKey(
        string ModId,
        long Generation,
        Type DeclaringType,
        string MemberName,
        GetterKind Kind,
        Type? ValueType);

    private readonly record struct ObjectKey(
        string ModId,
        long Generation,
        uint SessionEpoch,
        Type Type,
        nint Pointer);

    private static readonly object Gate = new();
    private static readonly Dictionary<GetterKey, Delegate> Getters = new();
    private static readonly Dictionary<ObjectKey, object> Objects = new();
    private static readonly Dictionary<(string ModId, long Generation), uint> SessionEpochs = new();
    private static Func<Type, bool>? s_generatedProxyProbe;
    private static Func<object, nint>? s_pointerProbe;

    public static void RegisterGeneratedProxyTypeProbe(Func<Type, bool>? probe)
        => Volatile.Write(ref s_generatedProxyProbe, probe);

    public static void RegisterObjectPointerProbe(Func<object, nint>? probe)
        => Volatile.Write(ref s_pointerProbe, probe);

    public static Func<TField> CreateStaticFieldGetter<TField>(Type declaringType, string fieldName)
    {
        var owner = RequireFactoryScope("static field getter");
        ArgumentNullException.ThrowIfNull(declaringType);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        var key = new GetterKey(owner.ModId, owner.ResourceSessionGeneration, declaringType,
            fieldName, GetterKind.StaticField, typeof(TField));
        lock (Gate)
        {
            if (Getters.TryGetValue(key, out var cached))
                return (Func<TField>)cached;
            var member = FindReadableMember(declaringType, fieldName, propertyFirst: false)
                         ?? throw new MissingFieldException(declaringType.FullName, fieldName);
            if (!IsStatic(member))
                throw new ArgumentException($"Field is not static: {declaringType.FullName}.{fieldName}");
            LogBinding(owner, declaringType, fieldName, GetterKind.StaticField, member);
            var getter = BuildStaticGetter<TField>(owner, declaringType, fieldName, member);
            Getters.Add(key, getter);
            return getter;
        }
    }

    public static Func<TField> CreateStaticPropertyGetter<TField>(Type declaringType, string propertyName)
    {
        var owner = RequireFactoryScope("static property getter");
        ArgumentNullException.ThrowIfNull(declaringType);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        var key = new GetterKey(owner.ModId, owner.ResourceSessionGeneration, declaringType,
            propertyName, GetterKind.StaticProperty, typeof(TField));
        lock (Gate)
        {
            if (Getters.TryGetValue(key, out var cached))
                return (Func<TField>)cached;
            var member = FindReadableMember(declaringType, propertyName, propertyFirst: true)
                         ?? throw new MissingMemberException(declaringType.FullName, propertyName);
            if (!IsStatic(member))
                throw new ArgumentException($"Property is not static: {declaringType.FullName}.{propertyName}");
            LogBinding(owner, declaringType, propertyName, GetterKind.StaticProperty, member);
            var getter = BuildStaticGetter<TField>(owner, declaringType, propertyName, member);
            Getters.Add(key, getter);
            return getter;
        }
    }

    public static Func<T, object> CreateMemberGetter<T>(string name) where T : class
    {
        var owner = RequireFactoryScope("object member getter");
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var key = new GetterKey(owner.ModId, owner.ResourceSessionGeneration, typeof(T),
            name, GetterKind.InstanceObject, typeof(object));
        lock (Gate)
        {
            if (Getters.TryGetValue(key, out var cached))
                return (Func<T, object>)cached;
            var member = FindReadableMember(typeof(T), name, propertyFirst: false)
                         ?? throw new MissingMemberException(typeof(T).FullName, name);
            if (IsStatic(member))
                throw new ArgumentException($"Member is static: {typeof(T).FullName}.{name}");
            LogBinding(owner, typeof(T), name, GetterKind.InstanceObject, member);
            var getter = BuildObjectInstanceGetter<T>(owner, typeof(T), name, member);
            Getters.Add(key, getter);
            return getter;
        }
    }

    public static Func<T, F> CreateMemberGetter<T, F>(string name) where T : class
    {
        var owner = RequireFactoryScope("typed member getter");
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var key = new GetterKey(owner.ModId, owner.ResourceSessionGeneration, typeof(T),
            name, GetterKind.InstanceTyped, typeof(F));
        lock (Gate)
        {
            if (Getters.TryGetValue(key, out var cached))
                return (Func<T, F>)cached;
            var member = FindReadableMember(typeof(T), name, propertyFirst: true)
                         ?? throw new MissingMemberException(typeof(T).FullName, name);
            if (IsStatic(member))
                throw new ArgumentException($"Member is static: {typeof(T).FullName}.{name}");
            LogBinding(owner, typeof(T), name, GetterKind.InstanceTyped, member);
            var getter = BuildTypedInstanceGetter<T, F>(owner, typeof(T), name, member);
            Getters.Add(key, getter);
            return getter;
        }
    }

    public static Func<object> CreateStaticMemberGetter(Type declaringType, string name)
    {
        var owner = RequireFactoryScope("static member getter");
        ArgumentNullException.ThrowIfNull(declaringType);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var key = new GetterKey(owner.ModId, owner.ResourceSessionGeneration, declaringType,
            name, GetterKind.StaticMember, typeof(object));
        lock (Gate)
        {
            if (Getters.TryGetValue(key, out var cached))
                return (Func<object>)cached;
            var member = FindReadableMember(declaringType, name, propertyFirst: false);
            if (member is null || !IsStatic(member))
                throw new MissingMemberException(declaringType.FullName, name);
            LogBinding(owner, declaringType, name, GetterKind.StaticMember, member);
            var getter = BuildStaticObjectGetter(owner, declaringType, name, member);
            Getters.Add(key, getter);
            return getter;
        }
    }

    /// <summary>Retires all bindings and canonical proxy objects owned by one resource generation.</summary>
    public static void RetireSession(string modId, long generation)
    {
        if (string.IsNullOrWhiteSpace(modId) || generation <= 0)
            return;
        lock (Gate)
        {
            foreach (var key in Getters.Keys.Where(key =>
                         key.Generation == generation &&
                         string.Equals(key.ModId, modId, StringComparison.OrdinalIgnoreCase)).ToArray())
                Getters.Remove(key);
            foreach (var key in Objects.Keys.Where(key =>
                         key.Generation == generation &&
                         string.Equals(key.ModId, modId, StringComparison.OrdinalIgnoreCase)).ToArray())
                Objects.Remove(key);
            SessionEpochs.Remove((modId, generation));
        }
    }

    private static PcCompatManagedExecutionState RequireFactoryScope(string operation)
    {
        var owner = PcCompatManagedExecutionContext.Current
                    ?? throw new InvalidOperationException(
                        $"PcCompat dynamic getter factory requires an active managed scope: {operation}.");
        RequireInvocationScope(owner, operation);
        return owner;
    }

    private static void RequireInvocationScope(
        PcCompatManagedExecutionState owner,
        string operation,
        bool requireUnityMain = true)
    {
        var current = PcCompatManagedExecutionContext.Current;
        if (current is null ||
            !string.Equals(current.ModId, owner.ModId, StringComparison.OrdinalIgnoreCase) ||
            current.ResourceSessionGeneration != owner.ResourceSessionGeneration)
        {
            throw new InvalidOperationException(
                $"PcCompat dynamic getter owner scope mismatch: expected={owner.ModId}/" +
                $"{owner.ResourceSessionGeneration} operation={operation}.");
        }
        var provisionalFactoryScope =
            current.Phase is (PcCompatManagedExecutionPhase.Bootstrap or
                PcCompatManagedExecutionPhase.Setup) &&
            owner.Phase == current.Phase &&
            PcCompatManagedSessionBindings.IsBound(
                owner.ModId,
                owner.ResourceSessionGeneration);
        if (owner.Phase == PcCompatManagedExecutionPhase.Disable ||
            current.Phase == PcCompatManagedExecutionPhase.Disable ||
            ((!PcCompatRuntime.CanDispatchManagedContinuation(owner) ||
              !PcCompatRuntime.CanDispatchManagedContinuation(current)) &&
             !provisionalFactoryScope))
        {
            throw new InvalidOperationException(
                $"PcCompat dynamic getter scope is retired: mod={owner.ModId} " +
                $"generation={owner.ResourceSessionGeneration} operation={operation}.");
        }
        if (requireUnityMain && !PcCompatUnityMainExecutionContext.IsActive)
        {
            throw new InvalidOperationException(
                $"PcCompat dynamic getter requires UnityMain: mod={owner.ModId} " +
                $"generation={owner.ResourceSessionGeneration} operation={operation}.");
        }
    }

    private static object? NormalizeResult(
        PcCompatManagedExecutionState owner,
        object? value,
        string operation)
    {
        if (value is null)
            return null;
        var probe = Volatile.Read(ref s_generatedProxyProbe);
        if (probe is null || !probe(value.GetType()))
            return value;
        RequireInvocationScope(owner, operation);
        var pointerProbe = Volatile.Read(ref s_pointerProbe)
                           ?? throw new InvalidOperationException(
                               "PcCompat generated proxy pointer provider is unavailable.");
        var pointer = pointerProbe(value);
        if (pointer == nint.Zero)
            throw new InvalidOperationException(
                $"PcCompat generated proxy is collected or null: {operation}.");
        var sessionEpoch = PcCompatReversePatchBridge.PublishedSessionEpoch();
        var key = new ObjectKey(owner.ModId, owner.ResourceSessionGeneration, sessionEpoch,
            value.GetType(), pointer);
        lock (Gate)
        {
            var epochKey = (owner.ModId, owner.ResourceSessionGeneration);
            if (SessionEpochs.TryGetValue(epochKey, out var previousEpoch) &&
                previousEpoch != sessionEpoch)
            {
                foreach (var stale in Objects.Keys.Where(item =>
                             item.Generation == owner.ResourceSessionGeneration &&
                             string.Equals(item.ModId, owner.ModId,
                                 StringComparison.OrdinalIgnoreCase)).ToArray())
                    Objects.Remove(stale);
            }
            SessionEpochs[epochKey] = sessionEpoch;
            if (Objects.TryGetValue(key, out var stable))
            {
                if (pointerProbe(stable) == pointer)
                    return stable;
                Objects.Remove(key);
            }
            Objects.Add(key, value);
            return value;
        }
    }

    private static bool TryResolveSnapshot<T>(
        PcCompatManagedExecutionState owner,
        Type declaringType,
        string memberName,
        object? instance,
        string operation,
        out T value)
    {
        RequireInvocationScope(owner, operation, requireUnityMain: false);
        if (!PcCompatRuntime.TryEnterManagedSessionCallback(
                owner, out var lease, out _))
        {
            throw new InvalidOperationException(
                $"PcCompat dynamic getter callback lease is retired: {operation}.");
        }
        using (lease)
        {
            var status = PcCompatManagedSnapshotScalarBridge.TryResolve(
                owner,
                declaringType,
                memberName,
                typeof(T),
                instance,
                out var resolved);
            if (status != PcCompatSnapshotScalarResolution.Resolved)
            {
                value = default!;
                return false;
            }

            resolved = NormalizeResult(owner, resolved, operation);

            if (resolved is null)
            {
                value = default!;
                return true;
            }
            if (resolved is T typed)
            {
                value = typed;
                return true;
            }
            try
            {
                value = (T)Convert.ChangeType(resolved, typeof(T),
                    System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"PcCompat snapshot scalar type mismatch: expected={typeof(T).FullName} " +
                    $"actual={resolved.GetType().FullName} operation={operation}.",
                    exception);
            }
        }
    }

    private static T ReadObjectGraph<T>(
        PcCompatManagedExecutionState owner,
        string operation,
        Func<T> read)
    {
        RequireInvocationScope(owner, operation, requireUnityMain: false);
        if (!PcCompatRuntime.TryEnterManagedSessionCallback(
                owner, out var lease, out var retirementToken))
        {
            throw new InvalidOperationException(
                $"PcCompat dynamic getter callback lease is retired: {operation}.");
        }
        var callbackLease = lease!;
        if (PcCompatUnityMainExecutionContext.IsActive)
        {
            using (callbackLease)
            {
                RequireInvocationScope(owner, operation, requireUnityMain: true);
                return read();
            }
        }
        return PcCompatRuntime.InvokeDynamicGetterOnUnityMain(
            owner,
            operation,
            () =>
            {
                RequireInvocationScope(owner, operation, requireUnityMain: true);
                return read();
            },
            retirementToken,
            callbackLease);
    }

    private static T ReadDefault<T>(
        PcCompatManagedExecutionState owner,
        string operation)
    {
        RequireInvocationScope(owner, operation, requireUnityMain: false);
        if (!PcCompatRuntime.TryEnterManagedSessionCallback(owner, out var lease, out _))
        {
            throw new InvalidOperationException(
                $"PcCompat dynamic getter callback lease is retired: {operation}.");
        }
        using (lease)
            return default!;
    }

    private static Func<TField> BuildStaticGetter<TField>(
        PcCompatManagedExecutionState owner,
        Type declaringType,
        string memberName,
        MemberInfo member)
    {
        try
        {
            var body = Expression.Convert(BuildReadExpression(member, instance: null), typeof(TField));
            var compiled = Expression.Lambda<Func<TField>>(body).Compile();
            return () =>
            {
                var operation = $"{declaringType.FullName}.{memberName}";
                if (TryResolveSnapshot(owner, declaringType, memberName,
                        instance: null, operation, out TField snapshot))
                    return snapshot;
                return ReadObjectGraph(owner, operation,
                    () => (TField)NormalizeResult(owner, compiled(), operation)!);
            };
        }
        catch
        {
            return () =>
            {
                var operation = $"{declaringType.FullName}.{memberName}";
                if (TryResolveSnapshot(owner, declaringType, memberName,
                        instance: null, operation, out TField snapshot))
                    return snapshot;
                return ReadObjectGraph(owner, operation,
                    () => (TField)NormalizeResult(owner, ReadMember(member, null), operation)!);
            };
        }
    }

    private static Func<T, object> BuildObjectInstanceGetter<T>(
        PcCompatManagedExecutionState owner,
        Type declaringType,
        string memberName,
        MemberInfo member) where T : class
    {
        var read = BuildObjectReader<T>(member);
        return instance =>
        {
            var operation = $"{declaringType.FullName}.{memberName}";
            if (TryResolveSnapshot(owner, declaringType, memberName,
                    instance, operation, out object snapshot))
                return snapshot;
            if (instance is null)
                return ReadDefault<object>(owner, operation);
            return ReadObjectGraph(owner, operation,
                () => NormalizeResult(owner, read(instance), operation)!);
        };
    }

    private static Func<T, F> BuildTypedInstanceGetter<T, F>(
        PcCompatManagedExecutionState owner,
        Type declaringType,
        string memberName,
        MemberInfo member) where T : class
    {
        var read = BuildTypedReader<T, F>(member);
        return instance =>
        {
            var operation = $"{declaringType.FullName}.{memberName}";
            if (TryResolveSnapshot(owner, declaringType, memberName,
                    instance, operation, out F snapshot))
                return snapshot;
            if (instance is null)
                return ReadDefault<F>(owner, operation);
            return ReadObjectGraph(owner, operation, () =>
            {
                var value = read(instance);
                return (F)NormalizeResult(owner, value, operation)!;
            });
        };
    }

    private static Func<object> BuildStaticObjectGetter(
        PcCompatManagedExecutionState owner,
        Type declaringType,
        string memberName,
        MemberInfo member)
    {
        var read = BuildStaticObjectReader(member);
        return () =>
        {
            var operation = $"{declaringType.FullName}.{memberName}";
            if (TryResolveSnapshot(owner, declaringType, memberName,
                    instance: null, operation, out object snapshot))
                return snapshot;
            return ReadObjectGraph(owner, operation,
                () => (NormalizeResult(owner, read(), operation) ?? null)!);
        };
    }

    private static Func<T, object?> BuildObjectReader<T>(MemberInfo member) where T : class
    {
        try
        {
            var parameter = Expression.Parameter(typeof(T), "instance");
            var access = BuildReadExpression(member, parameter);
            var body = Expression.Convert(access, typeof(object));
            return Expression.Lambda<Func<T, object?>>(body, parameter).Compile();
        }
        catch
        {
            return instance => InvokeMember(member, instance);
        }
    }

    private static Func<T, F> BuildTypedReader<T, F>(MemberInfo member) where T : class
    {
        try
        {
            var parameter = Expression.Parameter(typeof(T), "instance");
            var access = BuildReadExpression(member, parameter);
            var body = Expression.Convert(access, typeof(F));
            return Expression.Lambda<Func<T, F>>(body, parameter).Compile();
        }
        catch
        {
            return instance => (F)InvokeMember(member, instance)!;
        }
    }

    private static Func<object?> BuildStaticObjectReader(MemberInfo member)
    {
        try
        {
            var access = BuildReadExpression(member, instance: null);
            var body = Expression.Convert(access, typeof(object));
            return Expression.Lambda<Func<object?>>(body).Compile();
        }
        catch
        {
            return () => ReadMember(member, null);
        }
    }

    private static object? InvokeMember(MemberInfo member, object instance)
    {
        try
        {
            return ReadMember(member, instance);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static MemberInfo? FindReadableMember(Type type, string name, bool propertyFirst)
    {
        if (propertyFirst)
            return (MemberInfo?)FindProperty(type, name) ??
                   (MemberInfo?)FindField(type, name) ??
                   FindGetterMethod(type, name);
        return (MemberInfo?)FindField(type, name) ??
               (MemberInfo?)FindProperty(type, name) ??
               FindGetterMethod(type, name);
    }

    private static MethodInfo? FindGetterMethod(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var method = current.GetMethod(
                "get_" + name,
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            if (method is not null && method.ReturnType != typeof(void))
                return method;
        }
        return null;
    }

    private static bool IsStatic(MemberInfo member)
        => member switch
        {
            FieldInfo field => field.IsStatic,
            PropertyInfo property => property.GetGetMethod(true)?.IsStatic == true,
            MethodInfo method => method.IsStatic,
            _ => false
        };

    private static void LogBinding(
        PcCompatManagedExecutionState owner,
        Type declaringType,
        string memberName,
        GetterKind kind,
        MemberInfo member)
    {
        var representation = member switch
        {
            FieldInfo => "field",
            PropertyInfo => "property",
            MethodInfo => "getter-method",
            _ => member.MemberType.ToString()
        };
        Logger.Info(
            "PcCompatDynamicGetter",
            $"bound mod={owner.ModId} generation={owner.ResourceSessionGeneration} " +
            $"logical={declaringType.FullName}.{memberName} kind={kind} " +
            $"representation={representation}");
    }

    private static Expression BuildReadExpression(MemberInfo member, Expression? instance)
        => member switch
        {
            FieldInfo field => Expression.Field(instance, field),
            PropertyInfo property => Expression.Call(instance,
                property.GetGetMethod(true) ?? throw new InvalidOperationException(
                    $"Property has no getter: {property.DeclaringType?.FullName}.{property.Name}")),
            MethodInfo method => Expression.Call(instance, method),
            _ => throw new NotSupportedException($"Unsupported readable member: {member.MemberType}")
        };

    private static object? ReadMember(MemberInfo member, object? instance)
        => member switch
        {
            FieldInfo field => field.GetValue(instance),
            PropertyInfo property => (property.GetGetMethod(true) ??
                                      throw new InvalidOperationException(
                                          $"Property has no getter: {property.DeclaringType?.FullName}.{property.Name}"))
                .Invoke(instance, null),
            MethodInfo method => method.Invoke(instance, null),
            _ => throw new NotSupportedException($"Unsupported readable member: {member.MemberType}")
        };

    private static FieldInfo? FindField(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var field = current.GetField(name,
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field is not null)
                return field;
        }
        return null;
    }

    private static PropertyInfo? FindProperty(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var property = current.GetProperty(name,
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property is not null)
                return property;
        }
        return null;
    }
}

using System.ComponentModel;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace HarmonyLib;

public delegate object? FastInvokeHandler(object? target, params object?[] parameters);

public static class MethodInvoker
{
    public static FastInvokeHandler GetHandler(MethodInfo methodInfo, bool directBoxValueAccess = false)
    {
        ArgumentNullException.ThrowIfNull(methodInfo);
        if (directBoxValueAccess)
            throw Unsupported("directBoxValueAccess requires emitted IL to mutate an existing boxed value in place.");

        return (target, parameters) =>
        {
            try
            {
                return methodInfo.Invoke(target, parameters);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        };
    }

    private static NotSupportedException Unsupported(string detail)
    {
        HarmonyRegistry.ReportUnavailable("MethodInvoker.GetHandler", detail);
        return new NotSupportedException(detail);
    }
}

[Obsolete("Use AccessTools.FieldRefAccess<T, S> for fields and AccessTools.MethodDelegate<Func<T, S>> for property getters")]
[EditorBrowsable(EditorBrowsableState.Never)]
public delegate S GetterHandler<in T, out S>(T source);

[Obsolete("Use AccessTools.FieldRefAccess<T, S> for fields and AccessTools.MethodDelegate<Action<T, S>> for property setters")]
[EditorBrowsable(EditorBrowsableState.Never)]
public delegate void SetterHandler<in T, in S>(T source, S value);

public delegate T InstantiationHandler<out T>();

public delegate ref T RefResult<T>();

public static class FastAccess
{
    public static InstantiationHandler<T> CreateInstantiationHandler<T>()
    {
        var constructor = typeof(T).GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: [],
            modifiers: null)
            ?? throw new ApplicationException(
                $"The type {typeof(T)} must declare an empty constructor (the constructor may be private, internal, protected, protected internal, or public).");
        return () => Invoke<T>(constructor, null, []);
    }

    [Obsolete("Use AccessTools.MethodDelegate<Func<T, S>>(PropertyInfo.GetGetMethod(true))")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static GetterHandler<T, S> CreateGetterHandler<T, S>(PropertyInfo propertyInfo)
        => source => (S)Invoke(propertyInfo.GetGetMethod(true)!, source, [])!;

    [Obsolete("Use AccessTools.FieldRefAccess<T, S>(fieldInfo)")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static GetterHandler<T, S> CreateGetterHandler<T, S>(FieldInfo fieldInfo)
        => source => (S)fieldInfo.GetValue(source)!;

    [Obsolete("Use AccessTools.FieldRefAccess<T, S>(name) for fields and AccessTools.MethodDelegate<Func<T, S>>(AccessTools.PropertyGetter(typeof(T), name)) for properties")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static GetterHandler<T, S>? CreateFieldGetter<T, S>(params string[] names)
    {
        foreach (var name in names)
        {
            if (typeof(T).GetField(name, AccessTools.all) is { } field)
                return CreateGetterHandler<T, S>(field);
            if (typeof(T).GetProperty(name, AccessTools.all) is { } property)
                return CreateGetterHandler<T, S>(property);
        }
        return null;
    }

    [Obsolete("Use AccessTools.MethodDelegate<Action<T, S>>(PropertyInfo.GetSetMethod(true))")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static SetterHandler<T, S> CreateSetterHandler<T, S>(PropertyInfo propertyInfo)
        => (source, value) => _ = Invoke(propertyInfo.GetSetMethod(true)!, source, [value]);

    [Obsolete("Use AccessTools.FieldRefAccess<T, S>(fieldInfo)")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static SetterHandler<T, S> CreateSetterHandler<T, S>(FieldInfo fieldInfo)
        => (source, value) => fieldInfo.SetValue(source, value);

    private static T Invoke<T>(ConstructorInfo constructor, object? target, object?[] parameters)
        => (T)Invoke(constructor, target, parameters)!;

    private static object? Invoke(MethodBase method, object? target, object?[] parameters)
    {
        try
        {
            return method switch
            {
                ConstructorInfo constructor => constructor.Invoke(parameters),
                MethodInfo methodInfo => methodInfo.Invoke(target, parameters),
                _ => throw new ArgumentException("Unsupported method kind", nameof(method))
            };
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}

public class DelegateTypeFactory
{
    public Type CreateDelegateType(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        const string detail = "runtime delegate type creation requires Reflection.Emit, which PcCompat does not use.";
        HarmonyRegistry.ReportUnavailable("DelegateTypeFactory.CreateDelegateType", detail);
        throw new NotSupportedException(detail);
    }
}

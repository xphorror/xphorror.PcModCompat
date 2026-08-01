using System.Reflection;

namespace JALib.Tools;

public static class SimpleUnsafeReflect
{
    public static T GetValueUnsafe<T>(this FieldInfo field, object? target = null)
        where T : class
        => Reinterpret<T>(field.GetValue(target));

    public static T InvokeUnsafe<T>(this MethodInfo methodInfo, object? target = null)
        where T : class
        => Reinterpret<T>(methodInfo.Invoke(target, null));

    public static T InvokeUnsafe<T>(this MethodInfo methodInfo, params object?[] objects)
        where T : class
        => Reinterpret<T>(methodInfo.Invoke(null, objects));

    public static T InvokeUnsafe<T>(
        this MethodInfo methodInfo,
        object? target,
        params object?[] parameters)
        where T : class
        => Reinterpret<T>(methodInfo.Invoke(target, parameters));

    public static T InvokeUnsafe<T>(this Type type, string name)
        where T : class
        => type.Method(name)!.InvokeUnsafe<T>();

    public static T InvokeUnsafe<T>(this Type type, string name, object? target)
        where T : class
        => type.Method(name)!.InvokeUnsafe<T>(target);

    public static T InvokeUnsafe<T>(this Type type, string name, params object?[] objects)
        where T : class
        => type.Method(name)!.InvokeUnsafe<T>(objects);

    public static T InvokeUnsafe<T>(
        this Type type,
        Type[] argumentTypes,
        string name,
        object? target = null)
        where T : class
        => type.Method(name, argumentTypes)!.InvokeUnsafe<T>(target);

    public static T InvokeUnsafe<T>(
        this Type type,
        Type[] argumentTypes,
        string name,
        params object?[] objects)
        where T : class
        => type.Method(name, argumentTypes)!.InvokeUnsafe<T>(objects);

    public static T GetValueUnsafe<T>(
        this Type type,
        string name,
        object? target = null)
        where T : class
        => GetValueUnsafe<T>(SimpleReflect.Field(type, name)!, target);

    public static T NewUnsafe<T>(this Type type)
        where T : class
        => Reinterpret<T>(type.New());

    public static T NewUnsafe<T>(this Type type, params object?[] objects)
        where T : class
        => Reinterpret<T>(type.New(objects));

    public static T GetValueUnsafe<T>(this object obj, string name)
        where T : class
        => Reinterpret<T>(SimpleReflect.GetValue(obj, name));

    public static T InvokeUnsafe<T>(this object obj, string name)
        where T : class
        => Reinterpret<T>(obj.Invoke(name));

    public static T InvokeUnsafe<T>(
        this object obj,
        string name,
        params object?[] objects)
        where T : class
        => Reinterpret<T>(obj.Invoke(name, objects));

    public static T InvokeUnsafe<T>(
        this object obj,
        string name,
        Type[] argumentTypes,
        params object?[] objects)
        where T : class
        => Reinterpret<T>(obj.Invoke(name, argumentTypes, objects));

    public static T GetValueUnsafe<T>(
        this object obj,
        string name,
        params object?[] objects)
        where T : class
        => Reinterpret<T>(obj.GetValue(name, objects));

    public static T GetValueUnsafe<T>(this object obj, string name, object? target)
        where T : class
        => Reinterpret<T>(obj.GetValue(name, target));

    public static T GetValueUnsafe<T>(
        this object obj,
        string name,
        Type[] argumentTypes,
        params object?[] objects)
        where T : class
        => Reinterpret<T>(obj.GetValue(name, argumentTypes, objects));

    public static T GetValueUnsafe<T>(
        this object obj,
        string name,
        Type[] argumentTypes,
        object? target)
        where T : class
        => Reinterpret<T>(obj.GetValue(name, argumentTypes, target));

    public static T GetValueUnsafe<T>(
        this object obj,
        string name,
        Type[] argumentTypes)
        where T : class
        => Reinterpret<T>(obj.GetValue(name, argumentTypes));

    public static T GetValueUnsafe<T>(
        this object obj,
        string name,
        Type[] argumentTypes,
        object? target,
        params object?[] objects)
        where T : class
        => Reinterpret<T>(obj.GetValue(name, argumentTypes, target, objects));

    public static T GetValueUnsafe<T>(this PropertyInfo property, object? target = null)
        where T : class
        => Reinterpret<T>(property.GetValue(target));

    public static T NewUnsafe<T>()
        where T : class
        => typeof(T).NewUnsafe<T>();

    public static T NewUnsafe<T>(params object?[] objects)
        where T : class
        => typeof(T).NewUnsafe<T>(objects);

    public static T NewUnsafeValue<T>()
        where T : struct
        => (T)typeof(T).New();

    public static T NewUnsafeValue<T>(params object?[] objects)
        where T : struct
        => (T)typeof(T).New(objects);

    private static T Reinterpret<T>(object? value)
        where T : class
    {
        if (value == null)
            return null!;
        return value is T compatible ? compatible : Unsafe.AsUnsafe<T>(value);
    }
}

using System.Reflection;

namespace JALib.Tools;

public static class ReflectionExtensions
{
    public static FieldInfo? Field(this Type type, string name)
        => SimpleReflect.Field(type, name);

    public static T? GetValue<T>(this object obj, string name)
        => SimpleReflect.GetValue<T>(obj, name);

    public static object? GetValue(this object obj, string name)
        => SimpleReflect.GetValue(obj, name);

    public static void SetValue(this object obj, string name, object? value)
        => SimpleReflect.SetValue(obj, name, value);

    public static T AsUnsafe<T>(this object obj)
        where T : class
        => SimpleReflect.AsUnsafe<T>(obj);
}

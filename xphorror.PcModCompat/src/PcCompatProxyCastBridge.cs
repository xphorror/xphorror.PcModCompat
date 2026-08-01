using System.Linq.Expressions;
using System.Reflection;

namespace Xphorror.PcModCompat;

/// <summary>
/// Restores Mono-style runtime downcasts for generated IL2CPP proxy wrappers.
/// Generated methods expose their declared wrapper type, while the native
/// object may be a derived Unity type that must be recovered through TryCast.
/// </summary>
public static class PcCompatProxyCastBridge
{
    public static T? IsInstance<T>(object? value) where T : class
    {
        if (value is null)
            return null;
        return value as T ?? CastCache<T>.TryCast(value);
    }

    public static T? Cast<T>(object? value) where T : class
    {
        if (value is null)
            return null;
        return IsInstance<T>(value) ?? throw new InvalidCastException(
            $"Generated IL2CPP proxy {value.GetType().FullName} cannot be cast to {typeof(T).FullName}.");
    }

    private static class CastCache<T> where T : class
    {
        private static readonly Type? ProxyBase = FindIl2CppObjectBase(typeof(T));
        private static readonly Func<object, T?> Converter = Create();

        public static T? TryCast(object value)
            => ProxyBase?.IsInstanceOfType(value) == true
                ? Converter(value)
                : null;

        private static Func<object, T?> Create()
        {
            var proxyBase = ProxyBase;
            if (proxyBase is null)
                return _ => null;

            var tryCast = proxyBase.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .SingleOrDefault(method =>
                    method.Name == "TryCast" &&
                    method.IsGenericMethodDefinition &&
                    method.GetGenericArguments().Length == 1 &&
                    method.GetParameters().Length == 0);
            if (tryCast is null)
                return _ => null;

            MethodInfo closed;
            try
            {
                closed = tryCast.MakeGenericMethod(typeof(T));
            }
            catch (ArgumentException)
            {
                return _ => null;
            }

            var value = Expression.Parameter(typeof(object), "value");
            var call = Expression.Call(Expression.Convert(value, proxyBase), closed);
            return Expression.Lambda<Func<object, T?>>(call, value).Compile();
        }

        private static Type? FindIl2CppObjectBase(Type type)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                if (current.FullName ==
                    "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase")
                    return current;
            }
            return null;
        }
    }
}

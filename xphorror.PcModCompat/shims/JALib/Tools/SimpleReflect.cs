using System.Collections;
using System.Globalization;
using System.Reflection;
using JALib.Core;
using UnityModManagerNet;

namespace JALib.Tools;

public static class SimpleReflect
{
    private const BindingFlags Flags =
        BindingFlags.Public |
        BindingFlags.NonPublic |
        BindingFlags.Static |
        BindingFlags.Instance |
        BindingFlags.DeclaredOnly;

    public static object? GetValue(this FieldInfo field)
        => field.GetValue(null);

    public static T GetValue<T>(this FieldInfo field, object? target = null)
        => (T)field.GetValue(target)!;

    public static void SetValue(this FieldInfo field, object? value)
        => field.SetValue(null, value);

    public static FieldInfo? Field(this Type type, string name)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var field = FindField(type, name);
        if (field is not null)
            return field;

        var property = FindProperty(type, name);
        var getter = FindMethod(type, "get_" + name, 0);
        var setter = FindMethod(type, "set_" + name, 1);
        return property is null && getter is null && setter is null
            ? null
            : new ProxyFieldInfo(type, name, property, getter, setter);
    }

    public static MethodInfo? Method(this Type type, string name)
        => FindMethod(type, name, null);

    public static FieldInfo[] Fields(this Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return EnumerateHierarchy(type)
            .SelectMany(current => current.GetFields(Flags))
            .ToArray();
    }

    public static MethodInfo? Method(this Type type, string name, params Type[] argumentTypes)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrEmpty(name);
        for (var current = type; current is not null; current = current.BaseType)
        {
            var method = current.GetMethod(name, Flags, binder: null, types: argumentTypes, modifiers: null);
            if (method is not null)
                return method;
        }
        return null;
    }

    public static ConstructorInfo? Constructor(this Type type, params Type[] argumentTypes)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.GetConstructor(Flags, binder: null, types: argumentTypes, modifiers: null);
    }

    public static MethodInfo[] Methods(this Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return EnumerateHierarchy(type)
            .SelectMany(current => current.GetMethods(Flags))
            .ToArray();
    }

    public static MethodInfo[] Methods(this Type type, string name)
        => type.Methods()
            .Where(method => string.Equals(method.Name, name, StringComparison.Ordinal))
            .ToArray();

    public static object? Invoke(this MethodInfo methodInfo, object? target = null)
        => methodInfo.Invoke(target, null);

    public static object? Invoke(this MethodInfo methodInfo, object?[] objects)
        => methodInfo.Invoke(null, objects);

    public static object? Invoke(
        this MethodInfo methodInfo,
        object? target,
        object?[] objects)
        => methodInfo.Invoke(target, objects);

    public static T Invoke<T>(this MethodInfo methodInfo, object? target = null)
        => (T)methodInfo.Invoke(target, null)!;

    public static T Invoke<T>(this MethodInfo methodInfo, params object?[] objects)
        => (T)methodInfo.Invoke(null, objects)!;

    public static T Invoke<T>(
        this MethodInfo methodInfo,
        object? target,
        params object?[] parameters)
        => (T)methodInfo.Invoke(target, parameters)!;

    public static MemberInfo[] Member(this Type type, string name)
        => type.Members()
            .Where(member => string.Equals(member.Name, name, StringComparison.Ordinal))
            .ToArray();

    public static MemberInfo[] Members(this Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.GetMembers(Flags);
    }

    public static object? Invoke(this Type type, string name)
        => type.Method(name)!.Invoke(null, null);

    public static object? Invoke(this Type type, string name, object? target)
        => type.Method(name)!.Invoke(target, null);

    public static object? Invoke(this Type type, string name, params object?[] objects)
        => type.Method(name)!.Invoke(null, objects);

    public static object? Invoke(
        this Type type,
        Type[] argumentTypes,
        string name,
        object? target = null)
        => type.Method(name, argumentTypes)!.Invoke(target, null);

    public static object? Invoke(
        this Type type,
        Type[] argumentTypes,
        string name,
        params object?[] objects)
        => type.Method(name, argumentTypes)!.Invoke(null, objects);

    public static T Invoke<T>(this Type type, string name)
        => type.Method(name)!.Invoke<T>();

    public static T Invoke<T>(this Type type, string name, object? target)
        => type.Method(name)!.Invoke<T>(target);

    public static T Invoke<T>(this Type type, string name, params object?[] objects)
        => (T)type.Method(name)!.Invoke(null, objects)!;

    public static T Invoke<T>(
        this Type type,
        Type[] argumentTypes,
        string name,
        object? target = null)
        => type.Method(name, argumentTypes)!.Invoke<T>(target);

    public static T Invoke<T>(
        this Type type,
        Type[] argumentTypes,
        string name,
        params object?[] objects)
        => (T)type.Method(name, argumentTypes)!.Invoke(null, objects)!;

    public static object? GetValue(
        this Type type,
        string name,
        object? target = null)
        => Field(type, name)!.GetValue(target);

    public static T GetValue<T>(
        this Type type,
        string name,
        object? target = null)
        => (T)Field(type, name)!.GetValue(target)!;

    public static void SetValue(
        this Type type,
        string name,
        object? value,
        object? target = null)
        => Field(type, name)!.SetValue(target, value);

    public static ConstructorInfo? Constructor(this Type type)
        => type.Constructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    public static ConstructorInfo? Constructor(this Type type, BindingFlags flags)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.GetConstructors(flags).FirstOrDefault();
    }

    public static ConstructorInfo? Constructor(
        this Type type,
        BindingFlags flags,
        params Type[] argumentTypes)
        => type.GetConstructor(flags, null, argumentTypes, null);

    public static ConstructorInfo? GetConstructor(this Type type)
        => type.Constructor();

    public static ConstructorInfo? GetConstructor(this Type type, BindingFlags flags)
        => type.Constructor(flags);

    public static ConstructorInfo[] Constructors(this Type type)
        => type.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    public static object New(this Type type)
        => Activator.CreateInstance(type, nonPublic: true)!;

    public static object New(this Type type, params object?[] objects)
        => Activator.CreateInstance(type, Flags, binder: null, args: objects, culture: null)!;

    public static T New<T>(this Type type)
        => (T)type.New();

    public static T New<T>(this Type type, params object?[] objects)
        => (T)type.New(objects);

    public static PropertyInfo? Property(this Type type, string name)
        => FindProperty(type, name);

    public static PropertyInfo[] Properties(this Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return EnumerateHierarchy(type)
            .SelectMany(current => current.GetProperties(Flags))
            .ToArray();
    }

    public static object? GetValue(this PropertyInfo property, object? target = null)
        => property.GetValue(target);

    public static T GetValue<T>(this PropertyInfo property, object? target = null)
        => (T)property.GetValue(target)!;

    public static void SetValue(
        this PropertyInfo property,
        object? target,
        object? value)
        => property.SetValue(target, value);

    public static MethodInfo? Getter(this PropertyInfo property)
        => property.GetGetMethod(nonPublic: true);

    public static MethodInfo? Setter(this PropertyInfo property)
        => property.GetSetMethod(nonPublic: true);

    public static MethodInfo[] Accessors(this PropertyInfo property)
        => property.GetAccessors(nonPublic: true);

    public static MethodInfo? Getter(this Type type, string name)
        => type.Property(name)?.Getter();

    public static MethodInfo? Setter(this Type type, string name)
        => type.Property(name)?.Setter();

    public static MethodInfo[] Accessors(this Type type, string name)
        => type.Property(name)?.Accessors() ?? [];

    public static MethodInfo? Getter(this object obj, string name)
        => obj.GetType().Property(name)?.Getter();

    public static MethodInfo? Setter(this object obj, string name)
        => obj.GetType().Property(name)?.Setter();

    public static MethodInfo[] Accessors(this object obj, string name)
        => obj.GetType().Property(name)?.Accessors() ?? [];

    public static T? GetValue<T>(this object obj, string name)
    {
        var value = GetValue(obj, name);
        return TryConvertValue(value, typeof(T), out var converted)
            ? (T?)converted
            : default;
    }

    public static object? GetValue(this object obj, string name)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var type = obj is Type reflectedType ? reflectedType : obj.GetType();
        var target = obj is Type ? null : obj;
        var field = FindField(type, name);
        if (field is not null)
            return field.GetValue(target);

        var property = FindProperty(type, name);
        if (property?.GetMethod is not null)
            return property.GetValue(target);

        return FindMethod(type, "get_" + name, 0)?.Invoke(target, null);
    }

    public static void SetValue(this object obj, string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var type = obj is Type reflectedType ? reflectedType : obj.GetType();
        var target = obj is Type ? null : obj;
        var field = FindField(type, name);
        if (field is not null)
        {
            field.SetValue(target, value);
            return;
        }

        var property = FindProperty(type, name);
        if (property?.SetMethod is not null)
        {
            property.SetValue(target, value);
            return;
        }

        var setter = FindMethod(type, "set_" + name, 1);
        setter?.Invoke(target, [value]);
    }

    public static object? Invoke(this object obj, string name)
        => Invoke(obj, name, Array.Empty<object?>());

    public static object? Invoke(this object obj, string name, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var type = obj is Type reflectedType ? reflectedType : obj.GetType();
        var target = obj is Type ? null : obj;
        var method = FindCompatibleMethod(type, name, args);
        return method?.Invoke(target, args);
    }

    public static FieldInfo? Field(this object obj, string name)
        => Field(obj.GetType(), name);

    public static MethodInfo? Method(this object obj, string name)
        => obj.GetType().Method(name);

    public static MethodInfo? Method(
        this object obj,
        string name,
        params Type[] argumentTypes)
        => obj.GetType().Method(name, argumentTypes);

    public static object? Invoke(
        this object obj,
        string name,
        Type[] argumentTypes,
        params object?[] objects)
        => obj.Method(name, argumentTypes)!.Invoke(obj, objects);

    public static T Invoke<T>(this object obj, string name)
        => obj.Method(name, Type.EmptyTypes)!.Invoke<T>(obj);

    public static T Invoke<T>(this object obj, string name, params object?[] objects)
        => obj.Method(name)!.Invoke<T>(obj, objects);

    public static T Invoke<T>(
        this object obj,
        string name,
        Type[] argumentTypes,
        params object?[] objects)
        => obj.Method(name, argumentTypes)!.Invoke<T>(obj, objects);

    public static object? GetValue(
        this object obj,
        string name,
        params object?[] objects)
        => obj.Method(name)!.Invoke(obj, objects);

    public static object? GetValue(this object obj, string name, object? target)
        => obj.Method(name)!.Invoke(target, null);

    public static object? GetValue(
        this object obj,
        string name,
        Type[] argumentTypes,
        params object?[] objects)
        => obj.Method(name, argumentTypes)!.Invoke(obj, objects);

    public static object? GetValue(
        this object obj,
        string name,
        Type[] argumentTypes,
        object? target)
        => obj.Method(name, argumentTypes)!.Invoke(target, null);

    public static object? GetValue(
        this object obj,
        string name,
        Type[] argumentTypes)
        => obj.Method(name, argumentTypes)!.Invoke(null, null);

    public static object? GetValue(
        this object obj,
        string name,
        Type[] argumentTypes,
        object? target,
        params object?[] objects)
        => obj.Method(name, argumentTypes)!.Invoke(target, objects);

    public static T GetValue<T>(
        this object obj,
        string name,
        params object?[] objects)
        => obj.Method(name)!.Invoke<T>(obj, objects);

    public static T GetValue<T>(this object obj, string name, object? target)
        => obj.Method(name)!.Invoke<T>(target);

    public static T GetValue<T>(
        this object obj,
        string name,
        Type[] argumentTypes,
        params object?[] objects)
        => obj.Method(name, argumentTypes)!.Invoke<T>(obj, objects);

    public static T GetValue<T>(
        this object obj,
        string name,
        Type[] argumentTypes,
        object? target)
        => obj.Method(name, argumentTypes)!.Invoke<T>(target);

    public static T GetValue<T>(
        this object obj,
        string name,
        Type[] argumentTypes)
        => obj.Method(name, argumentTypes)!.Invoke<T>();

    public static T GetValue<T>(
        this object obj,
        string name,
        Type[] argumentTypes,
        object? target,
        params object?[] objects)
        => obj.Method(name, argumentTypes)!.Invoke<T>(target, objects);

    public static T New<T>()
        => typeof(T).New<T>();

    public static T New<T>(params object?[] objects)
        => typeof(T).New<T>(objects);

    public static bool IsNumeric(this Type type)
        => type.IsInteger() || type.IsFloat();

    public static bool IsInteger(this Type type)
        => type == typeof(byte) || type == typeof(sbyte) ||
           type == typeof(short) || type == typeof(ushort) ||
           type == typeof(int) || type == typeof(uint) ||
           type == typeof(long) || type == typeof(ulong);

    public static bool IsFloat(this Type type)
        => type == typeof(float) || type == typeof(double) || type == typeof(decimal);

    public static Type? GetType(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        foreach (var assembly in GetAssemblies())
        {
            var type = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
            if (type != null)
                return type;
        }
        return null;
    }

    public static Assembly[] GetAssemblies()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().ToHashSet();
        foreach (var entry in UnityModManager.modEntries)
        {
            if (entry.Assembly != null)
                assemblies.Add(entry.Assembly);
            AddDelegateAssembly(assemblies, entry.OnToggle);
            AddDelegateAssembly(assemblies, entry.OnGUI);
            AddDelegateAssembly(assemblies, entry.OnUpdate);
            AddDelegateAssembly(assemblies, entry.OnFixedUpdate);
            AddDelegateAssembly(assemblies, entry.OnLateUpdate);
        }
        return assemblies.ToArray();
    }

    public static UnityModManager.ModEntry? GetMod(this Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return UnityModManager.modEntries.FirstOrDefault(entry =>
            entry.Assembly == assembly ||
            entry.OnToggle?.Method.DeclaringType?.Assembly == assembly ||
            entry.OnGUI?.Method.DeclaringType?.Assembly == assembly ||
            entry.OnUpdate?.Method.DeclaringType?.Assembly == assembly ||
            entry.OnFixedUpdate?.Method.DeclaringType?.Assembly == assembly ||
            entry.OnLateUpdate?.Method.DeclaringType?.Assembly == assembly);
    }

    public static UnityModManager.ModEntry? GetMod(this Type type)
        => type.Assembly.GetMod();

    public static JAMod? GetJAMod(this Assembly assembly)
        => JAMod.GetMods().FirstOrDefault(mod => mod.GetType().Assembly == assembly);

    public static JAMod? GetJAMod(this Type type)
        => type.Assembly.GetJAMod();

    public static T AsUnsafe<T>(this object obj)
        where T : class
        => (T)obj;

    private static FieldInfo? FindField(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var field = current.GetField(name, Flags);
            if (field is not null)
                return field;
        }
        return null;
    }

    private static IEnumerable<Type> EnumerateHierarchy(Type type)
    {
        for (var current = type; current != null; current = current.BaseType)
            yield return current;
    }

    private static void AddDelegateAssembly(HashSet<Assembly> assemblies, Delegate? callback)
    {
        var assembly = callback?.Method.DeclaringType?.Assembly;
        if (assembly != null)
            assemblies.Add(assembly);
    }

    private static PropertyInfo? FindProperty(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var property = current.GetProperty(name, Flags);
            if (property is not null)
                return property;
        }
        return null;
    }

    private static MethodInfo? FindMethod(Type type, string name, int? parameterCount)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var methods = current.GetMethods(Flags)
                .Where(method => method.Name == name &&
                                 (!parameterCount.HasValue ||
                                  method.GetParameters().Length == parameterCount.Value))
                .ToArray();
            if (methods.Length != 0)
                return methods[0];
        }
        return null;
    }

    private static MethodInfo? FindCompatibleMethod(Type type, string name, object?[] args)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var method in current.GetMethods(Flags))
            {
                if (method.Name != name)
                    continue;
                var parameters = method.GetParameters();
                if (parameters.Length != args.Length)
                    continue;
                if (parameters.Select(parameter => parameter.ParameterType)
                    .Zip(args, IsCompatibleArgument)
                    .All(compatible => compatible))
                {
                    return method;
                }
            }
        }
        return null;
    }

    private static bool IsCompatibleArgument(Type parameterType, object? value)
        => value is not null
            ? parameterType.IsInstanceOfType(value)
            : !parameterType.IsValueType || Nullable.GetUnderlyingType(parameterType) is not null;

    private static bool TryConvertValue(object? value, Type targetType, out object? converted)
    {
        if (value is null)
        {
            converted = null;
            return !targetType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null;
        }
        if (targetType.IsInstanceOfType(value))
        {
            converted = value;
            return true;
        }

        if (!TryReadSequence(value, out var items))
        {
            converted = null;
            return false;
        }

        if (targetType.IsArray)
        {
            var elementType = targetType.GetElementType()!;
            var array = Array.CreateInstance(elementType, items.Count);
            for (var index = 0; index < items.Count; index++)
            {
                if (!TryConvertElement(items[index], elementType, out var item))
                {
                    converted = null;
                    return false;
                }
                array.SetValue(item, index);
            }
            converted = array;
            return true;
        }

        if (!TryGetManagedListElementType(targetType, out var listElementType))
        {
            converted = null;
            return false;
        }

        var concreteType = targetType.IsInterface || targetType.IsAbstract
            ? typeof(List<>).MakeGenericType(listElementType)
            : targetType;
        if (Activator.CreateInstance(concreteType) is not IList list)
        {
            converted = null;
            return false;
        }
        foreach (var sourceItem in items)
        {
            if (!TryConvertElement(sourceItem, listElementType, out var item))
            {
                converted = null;
                return false;
            }
            list.Add(item);
        }
        converted = list;
        return targetType.IsInstanceOfType(list);
    }

    private static bool TryConvertElement(object? value, Type targetType, out object? converted)
    {
        if (value is null)
        {
            converted = null;
            return !targetType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null;
        }
        if (targetType.IsInstanceOfType(value))
        {
            converted = value;
            return true;
        }
        converted = null;
        return false;
    }

    private static bool TryGetManagedListElementType(Type type, out Type elementType)
    {
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(List<>) ||
                definition == typeof(IList<>) ||
                definition == typeof(ICollection<>) ||
                definition == typeof(IEnumerable<>) ||
                definition == typeof(IReadOnlyList<>) ||
                definition == typeof(IReadOnlyCollection<>))
            {
                elementType = type.GetGenericArguments()[0];
                return true;
            }
        }
        elementType = null!;
        return false;
    }

    private static bool TryReadSequence(object source, out IReadOnlyList<object?> items)
    {
        if (source is IEnumerable enumerable && source is not string)
        {
            items = enumerable.Cast<object?>().ToArray();
            return true;
        }

        var sourceType = source.GetType();
        var countProperty = FindProperty(sourceType, "Count");
        var countValue = countProperty?.GetValue(source) ??
                         FindMethod(sourceType, "get_Count", 0)?.Invoke(source, null);
        if (countValue is null)
        {
            items = Array.Empty<object?>();
            return false;
        }

        var count = Convert.ToInt32(countValue, CultureInfo.InvariantCulture);
        var itemProperty = FindProperty(sourceType, "Item");
        var itemGetter = FindMethod(sourceType, "get_Item", 1);
        if (itemProperty?.GetMethod is null && itemGetter is null)
        {
            items = Array.Empty<object?>();
            return false;
        }

        var result = new object?[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = itemProperty?.GetMethod is not null
                ? itemProperty.GetValue(source, [index])
                : itemGetter!.Invoke(source, [index]);
        }
        items = result;
        return true;
    }

    private sealed class ProxyFieldInfo(
        Type reflectedType,
        string name,
        PropertyInfo? property,
        MethodInfo? getter,
        MethodInfo? setter) : FieldInfo
    {
        private MemberInfo Source => (MemberInfo?)property ?? getter ?? setter!;
        private MethodInfo? Getter => property?.GetMethod ?? getter;
        private MethodInfo? Setter => property?.SetMethod ?? setter;

        public override FieldAttributes Attributes
        {
            get
            {
                var accessor = Getter ?? Setter;
                var attributes = accessor?.IsPublic == true
                    ? FieldAttributes.Public
                    : FieldAttributes.Private;
                if (accessor?.IsStatic == true)
                    attributes |= FieldAttributes.Static;
                if (Setter is null)
                    attributes |= FieldAttributes.InitOnly;
                return attributes;
            }
        }

        public override RuntimeFieldHandle FieldHandle => default;
        public override Type FieldType => property?.PropertyType ??
                                          Getter?.ReturnType ??
                                          Setter!.GetParameters()[0].ParameterType;
        public override Type? DeclaringType => Source.DeclaringType;
        public override string Name => name;
        public override Type? ReflectedType => reflectedType;
        public override Module Module => Source.Module;
        public override int MetadataToken => Source.MetadataToken;

        public override object[] GetCustomAttributes(bool inherit)
            => Source.GetCustomAttributes(inherit).ToArray();

        public override object[] GetCustomAttributes(Type attributeType, bool inherit)
            => Source.GetCustomAttributes(attributeType, inherit).ToArray();

        public override bool IsDefined(Type attributeType, bool inherit)
            => Source.IsDefined(attributeType, inherit);

        public override IList<CustomAttributeData> GetCustomAttributesData()
            => Source.GetCustomAttributesData();

        public override object? GetValue(object? obj)
            => property?.GetMethod is not null
                ? property.GetValue(obj)
                : Getter?.Invoke(obj, null);

        public override void SetValue(
            object? obj,
            object? value,
            BindingFlags invokeAttr,
            Binder? binder,
            CultureInfo? culture)
        {
            if (property?.SetMethod is not null)
            {
                property.SetValue(obj, value, invokeAttr, binder, null, culture);
                return;
            }
            if (Setter is not null)
            {
                Setter.Invoke(obj, invokeAttr, binder, [value], culture);
                return;
            }
            throw new FieldAccessException($"Field '{DeclaringType?.FullName}.{Name}' is read-only.");
        }
    }
}

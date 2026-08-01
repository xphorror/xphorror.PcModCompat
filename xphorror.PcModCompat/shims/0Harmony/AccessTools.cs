using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;

namespace HarmonyLib;

// ABI mirror of the reflection helpers in Harmony 2.4 HarmonyLib/Tools/AccessTools.cs.
//
// The pure-reflection surface is mirrored in full: those members behave identically on CoreCLR, so
// MODs that use AccessTools for lookups keep working unchanged. MethodDelegate/HarmonyDelegate are
// mirrored for the shapes reachable without emitted IL and throw for the two that are not (see
// there). The field-ref family (FieldRefAccess, StaticFieldRefAccess, StructFieldRefAccess) has no
// such form and is absent - it needs a runtime that can emit and JIT new method bodies.
public static class AccessTools
{
    public delegate ref F FieldRef<in T, F>(T instance = default!);

    public delegate ref F StructFieldRef<T, F>(ref T instance) where T : struct;

    public delegate ref F FieldRef<F>();

    public static readonly BindingFlags all = BindingFlags.Public
        | BindingFlags.NonPublic
        | BindingFlags.Instance
        | BindingFlags.Static
        | BindingFlags.GetField
        | BindingFlags.SetField
        | BindingFlags.GetProperty
        | BindingFlags.SetProperty;

    public static readonly BindingFlags allDeclared = all | BindingFlags.DeclaredOnly;

    public static IEnumerable<Assembly> AllAssemblies()
        => AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.FullName?.StartsWith("Microsoft.VisualStudio", StringComparison.Ordinal) is not true);

    public static Type? TypeByName(string name)
    {
        var localType = Type.GetType(name, false);
        if (localType is not null)
            return localType;

        foreach (var assembly in AllAssemblies())
        {
            var specificType = assembly.GetType(name, false);
            if (specificType is not null)
                return specificType;
        }

        var allTypes = AllTypes().ToArray();
        return allTypes.FirstOrDefault(t => t.FullName == name)
               ?? allTypes.FirstOrDefault(t => t.Name == name);
    }

    public static Type[] GetTypesFromAssembly(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return [.. ex.Types.Where(type => type is not null).Select(type => type!)];
        }
    }

    public static IEnumerable<Type> AllTypes() => AllAssemblies().SelectMany(GetTypesFromAssembly);

    private static Type[]? s_allTypesCached;

    public static Type? TypeSearch(Regex search, bool invalidateCache = false)
    {
        if (s_allTypesCached is null || invalidateCache)
            s_allTypesCached = [.. AllTypes()];

        return s_allTypesCached.FirstOrDefault(t => t.FullName is { } full && search.IsMatch(full))
               ?? s_allTypesCached.FirstOrDefault(t => search.IsMatch(t.Name));
    }

    public static void ClearTypeSearchCache() => s_allTypesCached = null;

    public static IEnumerable<Type> InnerTypes(Type type) => type.GetNestedTypes(all);

    public static T? FindIncludingBaseTypes<T>(Type type, Func<Type, T?> func) where T : class
    {
        var current = type;
        while (true)
        {
            var result = func(current);
            if (result is not null)
                return result;
            if (current.BaseType is not { } baseType)
                return null;
            current = baseType;
        }
    }

    public static T? FindIncludingInnerTypes<T>(Type type, Func<Type, T?> func) where T : class
    {
        var result = func(type);
        if (result is not null)
            return result;

        foreach (var subType in type.GetNestedTypes(all))
        {
            result = FindIncludingInnerTypes(subType, func);
            if (result is not null)
                break;
        }

        return result;
    }

    // Mirrors upstream Tools.TypColonName, including its malformed message text, so a MOD that
    // catches ArgumentException around a bad literal sees the same shape.
    //
    // One deliberate deviation: upstream dereferences the resolved type with no null check, so an
    // unresolvable "Type:Member" throws NullReferenceException from inside AccessTools for the
    // field/property/event families while the method family quietly returns null. Under PcCompat a
    // type that resolves on desktop can genuinely be missing (IL2CPP-backed types are not managed
    // assemblies), so every family here behaves alike - record a diagnostic, return null - and the
    // miss lands in the exported Harmony diagnostics instead of an NRE with no MOD frame on it.
    private static (Type? Type, string Name) SplitTypeColonName(string typeColonName, string api)
    {
        ArgumentNullException.ThrowIfNull(typeColonName);

        var parts = typeColonName.Split(':');
        if (parts.Length != 2)
            throw new ArgumentException(" must be specified as 'Namespace.Type1.Type2:MemberName", nameof(typeColonName));

        var type = TypeByName(parts[0]);
        if (type is null)
            HarmonyRegistry.ReportUnresolvedTypeColonName(api, typeColonName, parts[0]);

        return (type, parts[1]);
    }

    public static FieldInfo? DeclaredField(Type? type, string? name)
    {
        if (type is null || string.IsNullOrEmpty(name))
            return null;
        return type.GetField(name, allDeclared);
    }

    public static FieldInfo? DeclaredField(string typeColonName)
    {
        var (type, name) = SplitTypeColonName(typeColonName, "AccessTools.DeclaredField");
        return DeclaredField(type, name);
    }

    public static FieldInfo? Field(Type? type, string? name)
    {
        if (type is null || string.IsNullOrEmpty(name))
            return null;
        return FindIncludingBaseTypes(type, t => t.GetField(name, all));
    }

    public static FieldInfo? Field(string typeColonName)
    {
        var (type, name) = SplitTypeColonName(typeColonName, "AccessTools.Field");
        return Field(type, name);
    }

    public static FieldInfo? DeclaredField(Type? type, int idx)
    {
        var fields = GetDeclaredFields(type);
        return idx >= 0 && idx < fields.Count ? fields[idx] : null;
    }

    public static PropertyInfo? DeclaredProperty(Type? type, string? name)
    {
        if (type is null || string.IsNullOrEmpty(name))
            return null;
        return type.GetProperty(name, allDeclared);
    }

    public static PropertyInfo? DeclaredProperty(string typeColonName)
    {
        var (type, name) = SplitTypeColonName(typeColonName, "AccessTools.DeclaredProperty");
        return DeclaredProperty(type, name);
    }

    public static PropertyInfo? Property(Type? type, string? name)
    {
        if (type is null || string.IsNullOrEmpty(name))
            return null;
        return FindIncludingBaseTypes(type, t => t.GetProperty(name, all));
    }

    public static PropertyInfo? Property(string typeColonName)
    {
        var (type, name) = SplitTypeColonName(typeColonName, "AccessTools.Property");
        return Property(type, name);
    }

    public static PropertyInfo? DeclaredIndexer(Type? type, Type[]? parameters = null)
    {
        if (type is null)
            return null;
        try
        {
            // Upstream: without parameters the indexer must be unambiguous, with parameters the
            // first exact signature match wins.
            return parameters is null
                ? type.GetProperties(allDeclared).SingleOrDefault(property => property.GetIndexParameters().Length > 0)
                : type.GetProperties(allDeclared).FirstOrDefault(property =>
                    property.GetIndexParameters().Select(param => param.ParameterType).SequenceEqual(parameters));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static PropertyInfo? Indexer(Type? type, Type[]? parameters = null)
    {
        if (type is null)
            return null;
        return FindIncludingBaseTypes(type, t => DeclaredIndexer(t, parameters));
    }

    public static MethodInfo? DeclaredPropertyGetter(Type? type, string? name) => DeclaredProperty(type, name)?.GetGetMethod(true);

    public static MethodInfo? DeclaredPropertyGetter(string typeColonName) => DeclaredProperty(typeColonName)?.GetGetMethod(true);

    public static MethodInfo? DeclaredPropertySetter(Type? type, string? name) => DeclaredProperty(type, name)?.GetSetMethod(true);

    public static MethodInfo? DeclaredPropertySetter(string typeColonName) => DeclaredProperty(typeColonName)?.GetSetMethod(true);

    public static MethodInfo? PropertyGetter(Type? type, string? name) => Property(type, name)?.GetGetMethod(true);

    public static MethodInfo? PropertyGetter(string typeColonName) => Property(typeColonName)?.GetGetMethod(true);

    public static MethodInfo? PropertySetter(Type? type, string? name) => Property(type, name)?.GetSetMethod(true);

    public static MethodInfo? PropertySetter(string typeColonName) => Property(typeColonName)?.GetSetMethod(true);

    public static MethodInfo? DeclaredIndexerGetter(Type? type, Type[]? parameters = null) => DeclaredIndexer(type, parameters)?.GetGetMethod(true);

    public static MethodInfo? DeclaredIndexerSetter(Type? type, Type[]? parameters = null) => DeclaredIndexer(type, parameters)?.GetSetMethod(true);

    public static MethodInfo? IndexerGetter(Type? type, Type[]? parameters = null) => Indexer(type, parameters)?.GetGetMethod(true);

    public static MethodInfo? IndexerSetter(Type? type, Type[]? parameters = null) => Indexer(type, parameters)?.GetSetMethod(true);

    public static EventInfo? DeclaredEvent(Type? type, string? name)
    {
        if (type is null || string.IsNullOrEmpty(name))
            return null;
        return type.GetEvent(name, allDeclared);
    }

    public static EventInfo? DeclaredEvent(string typeColonName)
    {
        var (type, name) = SplitTypeColonName(typeColonName, "AccessTools.DeclaredEvent");
        return DeclaredEvent(type, name);
    }

    public static EventInfo? Event(Type? type, string? name)
    {
        if (type is null || string.IsNullOrEmpty(name))
            return null;
        return FindIncludingBaseTypes(type, t => t.GetEvent(name, all));
    }

    public static EventInfo? Event(string typeColonName)
    {
        var (type, name) = SplitTypeColonName(typeColonName, "AccessTools.Event");
        return Event(type, name);
    }

    public static MethodInfo? DeclaredEventAdder(Type? type, string? name) => DeclaredEvent(type, name)?.GetAddMethod(true);

    public static MethodInfo? DeclaredEventAdder(string typeColonName) => DeclaredEvent(typeColonName)?.GetAddMethod(true);

    public static MethodInfo? DeclaredEventRemover(Type? type, string? name) => DeclaredEvent(type, name)?.GetRemoveMethod(true);

    public static MethodInfo? DeclaredEventRemover(string typeColonName) => DeclaredEvent(typeColonName)?.GetRemoveMethod(true);

    public static MethodInfo? EventAdder(Type? type, string? name) => Event(type, name)?.GetAddMethod(true);

    public static MethodInfo? EventAdder(string typeColonName) => Event(typeColonName)?.GetAddMethod(true);

    public static MethodInfo? EventRemover(Type? type, string? name) => Event(type, name)?.GetRemoveMethod(true);

    public static MethodInfo? EventRemover(string typeColonName) => Event(typeColonName)?.GetRemoveMethod(true);

    public static MethodInfo? DeclaredMethod(Type? type, string? name, Type[]? parameters = null, Type[]? generics = null)
    {
        if (type is null || string.IsNullOrEmpty(name))
            return null;

        var result = parameters is null
            ? type.GetMethod(name, allDeclared)
            : type.GetMethod(name, allDeclared, null, parameters, []);

        if (result is null)
            return null;

        return generics is not null ? result.MakeGenericMethod(generics) : result;
    }

    public static MethodInfo? DeclaredMethod(string typeColonName, Type[]? parameters = null, Type[]? generics = null)
    {
        var (type, name) = SplitTypeColonName(typeColonName, "AccessTools.DeclaredMethod");
        return DeclaredMethod(type, name, parameters, generics);
    }

    public static MethodInfo? Method(Type? type, string? name, Type[]? parameters = null, Type[]? generics = null)
    {
        if (type is null || string.IsNullOrEmpty(name))
            return null;

        MethodInfo? result;
        if (parameters is null)
        {
            try
            {
                result = FindIncludingBaseTypes(type, t => t.GetMethod(name, all));
            }
            catch (AmbiguousMatchException)
            {
                // Upstream falls back to the parameterless overload when the name alone is
                // ambiguous, then gives up.
                result = FindIncludingBaseTypes(type, t => t.GetMethod(name, all, null, [], []));
            }
        }
        else
        {
            result = FindIncludingBaseTypes(type, t => t.GetMethod(name, all, null, parameters, []));
        }

        if (result is null)
            return null;

        return generics is not null ? result.MakeGenericMethod(generics) : result;
    }

    public static MethodInfo? Method(string typeColonName, Type[]? parameters = null, Type[]? generics = null)
    {
        var (type, name) = SplitTypeColonName(typeColonName, "AccessTools.Method");
        return Method(type, name, parameters, generics);
    }

    public static MethodInfo? Finalizer(Type? type) => Method(type, "Finalize");

    public static MethodInfo? DeclaredFinalizer(Type? type) => DeclaredMethod(type, "Finalize");

    public static ConstructorInfo? DeclaredConstructor(Type? type, Type[]? parameters = null, bool searchForStatic = false)
    {
        if (type is null)
            return null;
        parameters ??= [];
        var flags = searchForStatic ? allDeclared & ~BindingFlags.Instance : allDeclared & ~BindingFlags.Static;
        return type.GetConstructor(flags, null, parameters, []);
    }

    public static ConstructorInfo? Constructor(Type? type, Type[]? parameters = null, bool searchForStatic = false)
    {
        if (type is null)
            return null;
        parameters ??= [];
        var flags = searchForStatic ? all & ~BindingFlags.Instance : all & ~BindingFlags.Static;
        return FindIncludingBaseTypes(type, t => t.GetConstructor(flags, null, parameters, []));
    }

    public static List<ConstructorInfo> GetDeclaredConstructors(Type? type, bool? searchForStatic = null)
    {
        if (type is null)
            return [];
        var flags = allDeclared;
        if (searchForStatic.HasValue)
            flags = searchForStatic.Value ? flags & ~BindingFlags.Instance : flags & ~BindingFlags.Static;
        return [.. type.GetConstructors(flags).Where(method => method.DeclaringType == type)];
    }

    public static List<MethodInfo> GetDeclaredMethods(Type? type)
        => type is null ? [] : [.. type.GetMethods(allDeclared)];

    public static List<PropertyInfo> GetDeclaredProperties(Type? type)
        => type is null ? [] : [.. type.GetProperties(allDeclared)];

    public static List<FieldInfo> GetDeclaredFields(Type? type)
        => type is null ? [] : [.. type.GetFields(allDeclared)];

    public static List<string> GetMethodNames(Type? type)
        => type is null ? [] : [.. GetDeclaredMethods(type).Select(m => m.Name)];

    public static List<string> GetMethodNames(object? instance)
        => instance is null ? [] : GetMethodNames(instance.GetType());

    public static List<string> GetFieldNames(Type? type)
        => type is null ? [] : [.. GetDeclaredFields(type).Select(f => f.Name)];

    public static List<string> GetFieldNames(object? instance)
        => instance is null ? [] : GetFieldNames(instance.GetType());

    public static List<string> GetPropertyNames(Type? type)
        => type is null ? [] : [.. GetDeclaredProperties(type).Select(p => p.Name)];

    public static List<string> GetPropertyNames(object? instance)
        => instance is null ? [] : GetPropertyNames(instance.GetType());

    public static Type? GetUnderlyingType(this MemberInfo member) => member.MemberType switch
    {
        MemberTypes.Event => ((EventInfo)member).EventHandlerType,
        MemberTypes.Field => ((FieldInfo)member).FieldType,
        MemberTypes.Method => ((MethodInfo)member).ReturnType,
        MemberTypes.Property => ((PropertyInfo)member).PropertyType,
        _ => throw new ArgumentException("Member must be of type EventInfo, FieldInfo, MethodInfo, or PropertyInfo")
    };

    public static Type? GetReturnedType(MethodBase? methodOrConstructor) => methodOrConstructor switch
    {
        null => null,
        ConstructorInfo => typeof(void),
        _ => ((MethodInfo)methodOrConstructor).ReturnType
    };

    public static bool IsStatic(MemberInfo member) => member.MemberType switch
    {
        MemberTypes.Constructor or MemberTypes.Method => ((MethodBase)member).IsStatic,
        MemberTypes.Field => ((FieldInfo)member).IsStatic,
        MemberTypes.Property => ((PropertyInfo)member).GetAccessors(true).Any(accessor => accessor.IsStatic),
        MemberTypes.Event => ((EventInfo)member).GetAddMethod(true)?.IsStatic ?? false,
        MemberTypes.TypeInfo or MemberTypes.NestedType => ((Type)member) is { IsAbstract: true, IsSealed: true },
        _ => throw new NotSupportedException($"Unsupported member type {member.MemberType}")
    };

    public static bool IsStatic(Type type) => type is { IsAbstract: true, IsSealed: true };

    public static bool IsStatic(PropertyInfo propertyInfo) => propertyInfo.GetAccessors(true).Any(accessor => accessor.IsStatic);

    public static bool IsStatic(FieldInfo fieldInfo) => fieldInfo.IsStatic;

    public static bool IsStatic(MethodBase methodBase) => methodBase.IsStatic;

    public static object? CreateInstance(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var ctor = type.GetConstructor(allDeclared & ~BindingFlags.Static, null, [], []);
        return ctor is not null ? Activator.CreateInstance(type) : System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
    }

    public static T CreateInstance<T>() => (T)CreateInstance(typeof(T))!;

    public static object? GetDefaultValue(Type type)
    {
        if (type is null || type == typeof(void))
            return null;
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    /// <summary>
    /// Maps a loose set of candidate arguments onto a method signature by assignability. Used for
    /// HarmonyPrepare/HarmonyCleanup/HarmonyTargetMethod(s), whose parameter lists are optional.
    /// </summary>
    public static object?[] ActualParameters(MethodBase method, object?[] inputs)
    {
        var inputTypes = inputs.Select(obj => obj?.GetType()).ToList();
        return [.. method.GetParameters().Select(p => p.ParameterType).Select(pType =>
        {
            var index = inputTypes.FindIndex(inType => inType is not null && pType.IsAssignableFrom(inType));
            return index >= 0 ? inputs[index] : GetDefaultValue(pType);
        })];
    }

    public static Type? Inner(Type? type, string? name)
    {
        if (type is null || string.IsNullOrEmpty(name))
            return null;
        return FindIncludingBaseTypes(type, t => t.GetNestedType(name, all));
    }

    public static Type? FirstInner(Type? type, Func<Type, bool>? predicate)
    {
        if (type is null || predicate is null)
            return null;
        return type.GetNestedTypes(all).FirstOrDefault(subType => predicate(subType));
    }

    public static MethodInfo? FirstMethod(Type? type, Func<MethodInfo, bool>? predicate)
    {
        if (type is null || predicate is null)
            return null;
        return type.GetMethods(allDeclared).FirstOrDefault(method => predicate(method));
    }

    public static ConstructorInfo? FirstConstructor(Type? type, Func<ConstructorInfo, bool>? predicate)
    {
        if (type is null || predicate is null)
            return null;
        return type.GetConstructors(allDeclared).FirstOrDefault(constructor => predicate(constructor));
    }

    public static PropertyInfo? FirstProperty(Type? type, Func<PropertyInfo, bool>? predicate)
    {
        if (type is null || predicate is null)
            return null;
        return type.GetProperties(allDeclared).FirstOrDefault(property => predicate(property));
    }

    public static Type[] GetTypes(object?[]? parameters)
        => parameters is null ? [] : [.. parameters.Select(p => p?.GetType() ?? typeof(object))];

    public static MethodInfo? GetMethodByModuleAndToken(string moduleGUID, int token)
    {
        var moduleVersionId = new Guid(moduleGUID);
        var module = AllAssemblies()
            .SelectMany(assembly => assembly.GetModules())
            .FirstOrDefault(m => m.ModuleVersionId == moduleVersionId);
        return module?.ResolveMethod(token) as MethodInfo;
    }

    public static bool IsDeclaredMember<T>(this T member) where T : MemberInfo
        => member.DeclaringType == member.ReflectedType;

    public static T GetDeclaredMember<T>(this T member) where T : MemberInfo
    {
        if (member.DeclaringType is null || member.IsDeclaredMember())
            return member;

        var metaToken = member.MetadataToken;
        foreach (var other in member.DeclaringType.GetMembers(all))
            if (other.MetadataToken == metaToken)
                return (T)other;

        return member;
    }

    // Upstream asks MonoMod's PlatformTriple for the runtime's canonical handle for a method, which
    // matters when it is about to detour that handle. Nothing here detours through MonoMod, and
    // upstream itself falls back to the input whenever the platform has no distinct identity for it,
    // so the identity transform is the honest answer rather than a stub that throws.
    public static MethodInfo Identifiable(this MethodInfo method) => method;

    // Upstream reads the target's IL and takes the single Newobj operand to find the compiler's
    // iterator class. That needs an IL reader this host does not have, so the state machine is taken
    // from the attribute the C# compiler emits alongside it. Same answer for compiler-generated
    // iterators; null (never a guess) when the attribute is absent, which is also what upstream
    // returns when it cannot identify the state machine.
    public static MethodInfo? EnumeratorMoveNext(MethodBase? method)
    {
        if (method?.GetCustomAttribute<IteratorStateMachineAttribute>() is not { } iteratorAttribute)
            return null;
        return Method(iteratorAttribute.StateMachineType, nameof(System.Collections.IEnumerator.MoveNext));
    }

    public static MethodInfo? AsyncMoveNext(MethodBase? method)
    {
        if (method?.GetCustomAttribute<AsyncStateMachineAttribute>() is not { } asyncAttribute)
            return null;
        return DeclaredMethod(asyncAttribute.StateMachineType, nameof(IAsyncStateMachine.MoveNext));
    }

    public static bool IsStruct(Type? type) => type is not null && type.IsValueType && IsValue(type) is false && IsVoid(type) is false;

    public static bool IsClass(Type? type) => type is not null && type.IsValueType is false;

    public static bool IsValue(Type? type) => type is not null && (type.IsPrimitive || type.IsEnum);

    public static bool IsInteger(Type? type) => type is not null && Type.GetTypeCode(type) switch
    {
        TypeCode.Byte or TypeCode.SByte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64
            or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 => true,
        _ => false
    };

    public static bool IsFloatingPoint(Type? type) => type is not null && Type.GetTypeCode(type) switch
    {
        TypeCode.Decimal or TypeCode.Double or TypeCode.Single => true,
        _ => false
    };

    public static bool IsNumber(Type? type) => IsInteger(type) || IsFloatingPoint(type);

    public static bool IsVoid(Type? type) => type == typeof(void);

    public static bool IsOfNullableType<T>(T instance) => Nullable.GetUnderlyingType(typeof(T)) is not null;

    public static bool IsMonoRuntime { get; } = Type.GetType("Mono.Runtime") is not null;

    public static void ThrowMissingMemberException(Type type, params string[] names)
    {
        var fields = string.Join(",", GetFieldNames(type));
        var properties = string.Join(",", GetPropertyNames(type));
        throw new MissingMemberException($"{string.Join(",", names)}; available fields: {fields}; available properties: {properties}");
    }

    public static MethodBase GetOutsideCaller()
    {
        var trace = new StackTrace(true);
        foreach (var frame in trace.GetFrames())
        {
            var method = frame.GetMethod();
            if (method is not null && method.DeclaringType?.Namespace != typeof(Harmony).Namespace)
                return method;
        }

        throw new Exception("Unexpected end of stack trace");
    }

    public static void RethrowException(Exception exception)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
        throw exception;
    }

    public static int CombinedHashCode(IEnumerable<object> objects)
    {
        var hash1 = (5381 << 16) + 5381;
        var hash2 = hash1;
        var i = 0;
        foreach (var obj in objects)
        {
            if (i % 2 == 0)
                hash1 = ((hash1 << 5) + hash1 + (hash1 >> 27)) ^ obj.GetHashCode();
            else
                hash2 = ((hash2 << 5) + hash2 + (hash2 >> 27)) ^ obj.GetHashCode();
            ++i;
        }

        return hash1 + (hash2 * 1566083941);
    }

    // Upstream MethodDelegate has six shapes and only two of them actually need emitted IL: an
    // open-instance *non-virtual* call, and any instance method whose receiver is a struct (the
    // receiver is passed by ref there, so no plain delegate signature fits). Everything else is a
    // plain Delegate.CreateDelegate or - for the closed non-virtual case - a delegate constructed
    // over a function pointer, and all of those behave identically on this runtime.
    //
    // So the two IL shapes throw with a diagnostic and the other four work, rather than the whole
    // member being absent: absence is a TypeLoadException that takes the entire MOD assembly down,
    // while a throw is confined to the one call site that asked for the shape this host cannot build.
    // Same rule as CodeInstruction.CallClosure - never hand back something that silently differs.
    public static DelegateType MethodDelegate<DelegateType>(
        MethodInfo method,
        object? instance = null,
        bool virtualCall = true,
        Type[]? delegateArgs = null) where DelegateType : Delegate
    {
        ArgumentNullException.ThrowIfNull(method);
        _ = delegateArgs; // only consulted by the emitted-IL shapes, which are refused below

        var delegateType = typeof(DelegateType);

        if (method.IsStatic)
            return (DelegateType)Delegate.CreateDelegate(delegateType, method);

        var declaringType = method.DeclaringType;
        if (declaringType is { IsInterface: true } && virtualCall is false)
            throw new ArgumentException("Interface methods must be called virtually");

        if (instance is null)
        {
            var delegateParameters = delegateType.GetMethod("Invoke")!.GetParameters();
            if (delegateParameters.Length == 0)
            {
                // Upstream lets CreateDelegate produce the precise message, then guards in case it does not.
                _ = Delegate.CreateDelegate(delegateType, method);
                throw new ArgumentException("Invalid delegate type");
            }

            var delegateInstanceType = delegateParameters[0].ParameterType;

            // Upstream remaps an interface method onto the struct's own implementation here and then
            // always falls through to its DynamicMethodDefinition, so this shape has no non-IL answer.
            if (declaringType is { IsInterface: true } && delegateInstanceType.IsValueType)
                throw UnsupportedMethodDelegate(method, "a struct-typed delegate instance bound to an interface method");

            if (declaringType is not null && virtualCall)
            {
                if (declaringType.IsInterface)
                    return (DelegateType)Delegate.CreateDelegate(delegateType, method);

                if (delegateInstanceType.IsInterface)
                {
                    var interfaceMapping = declaringType.GetInterfaceMap(delegateInstanceType);
                    var interfaceMethod = interfaceMapping.InterfaceMethods[Array.IndexOf(interfaceMapping.TargetMethods, method)];
                    return (DelegateType)Delegate.CreateDelegate(delegateType, interfaceMethod);
                }

                if (declaringType.IsValueType is false)
                    return (DelegateType)Delegate.CreateDelegate(delegateType, method.GetBaseDefinition());
            }

            throw UnsupportedMethodDelegate(
                method,
                declaringType is { IsValueType: true }
                    ? "an instance method on a struct, whose receiver upstream passes by ref through emitted IL"
                    : "an open-instance non-virtual call");
        }

        if (virtualCall)
            return (DelegateType)Delegate.CreateDelegate(delegateType, instance, method.GetBaseDefinition());

        // Binding a derived-class method to a base-class object is undefined behaviour, so upstream
        // refuses it; CreateDelegate words the rejection.
        if (declaringType is not null && declaringType.IsInstanceOfType(instance) is false)
        {
            _ = Delegate.CreateDelegate(delegateType, instance, method);
            throw new ArgumentException("Invalid delegate type");
        }

        // Upstream emits an ldftn thunk at this point purely to dodge a Mono bug (mono/mono#19964)
        // where the delegate constructor behaves like ldvirtftn. IsMonoRuntime is false here, so the
        // direct construction upstream itself uses off Mono is the whole story.
        return (DelegateType)Activator.CreateInstance(delegateType, instance, method.MethodHandle.GetFunctionPointer())!;
    }

    public static DelegateType MethodDelegate<DelegateType>(
        string typeColonName,
        object? instance = null,
        bool virtualCall = true,
        Type[]? delegateArgs = null) where DelegateType : Delegate
    {
        var method = DeclaredMethod(typeColonName)
                     ?? throw new ArgumentNullException(nameof(typeColonName), $"No method found for {typeColonName}");
        return MethodDelegate<DelegateType>(method, instance, virtualCall, delegateArgs);
    }

    // Kept because MODs compiled against Harmony 2.3 reference this exact three-parameter signature;
    // dropping it would be a MissingMethodException at their call site, not a compile error we would see.
#pragma warning disable CS1591
    [Obsolete("This overload only exists for runtime backwards compatibility and will be removed in Harmony 3. Use MethodDelegate(string, object, bool, Type[]) instead")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static DelegateType MethodDelegate<DelegateType>(string typeColonName, object? instance, bool virtualCall) where DelegateType : Delegate
        => MethodDelegate<DelegateType>(typeColonName, instance, virtualCall, null);
#pragma warning restore CS1591

    public static DelegateType HarmonyDelegate<DelegateType>(object? instance = null) where DelegateType : Delegate
    {
        var harmonyMethod = HarmonyMethodExtensions.GetMergedFromType(typeof(DelegateType));
        harmonyMethod.methodType ??= MethodType.Normal;
        if (harmonyMethod.GetOriginalMethod() is not MethodInfo method)
            throw new NullReferenceException($"Delegate {typeof(DelegateType)} has no defined original method");
        return MethodDelegate<DelegateType>(method, instance, harmonyMethod.nonVirtualDelegate is false, null);
    }

    private static NotSupportedException UnsupportedMethodDelegate(MethodInfo method, string shape)
    {
        var detail =
            $"'{method.DeclaringType?.FullName}.{method.Name}' was requested as {shape}, which upstream builds " +
            "with runtime IL emission; this host has none. Use a virtual-call delegate over a class instance, " +
            "or bind the instance up front, or call the method through reflection.";
        HarmonyRegistry.ReportUnavailable("AccessTools.MethodDelegate", detail);
        return new NotSupportedException(detail);
    }

    // The Add-method cache behind MakeDeepCopy's generic-collection branch. Upstream caches a
    // FastInvokeHandler built from a DynamicMethod; this host cannot emit, so the cache holds the
    // MethodInfo itself and the call goes through reflection instead. Negative results are cached
    // exactly as upstream does: a result type with no single-argument Add stores null and is never
    // looked up again.
    static readonly Dictionary<Type, MethodInfo?> addHandlerCache = [];
    static readonly ReaderWriterLockSlim addHandlerCacheLock = new(LockRecursionPolicy.SupportsRecursion);

    // MethodBase.Invoke wraps whatever the callee threw in a TargetInvocationException, while the
    // emitted FastInvokeHandler upstream lets it propagate untouched. Unwrap so a MOD's catch block
    // still sees the exception type it would see on the PC build.
    static object? InvokeAddOperation(MethodInfo addOperation, object? target, object? element)
    {
        try
        {
            return addOperation.Invoke(target, [element]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    public static T? MakeDeepCopy<T>(object? source) where T : class => MakeDeepCopy(source, typeof(T)) as T;

    public static void MakeDeepCopy<T>(object? source, out T result, Func<string, Traverse, Traverse, object>? processor = null, string pathRoot = "")
        => result = (T)MakeDeepCopy(source, typeof(T), processor, pathRoot)!;

    public static object? MakeDeepCopy(object? source, Type? resultType, Func<string, Traverse, Traverse, object>? processor = null, string pathRoot = "")
    {
        if (source is null || resultType is null)
            return null;

        resultType = Nullable.GetUnderlyingType(resultType) ?? resultType;
        var type = source.GetType();

        if (type.IsPrimitive)
            return source;

        if (type.IsEnum)
            return Enum.ToObject(resultType, (int)source);

        if (type.IsGenericType && resultType.IsGenericType)
        {
            addHandlerCacheLock.EnterUpgradeableReadLock();
            try
            {
                if (!addHandlerCache.TryGetValue(resultType, out var addOperation))
                {
                    addOperation = FirstMethod(resultType, m => m.Name == "Add" && m.GetParameters().Length == 1);
                    addHandlerCacheLock.EnterWriteLock();
                    try
                    {
                        addHandlerCache[resultType] = addOperation;
                    }
                    finally
                    {
                        addHandlerCacheLock.ExitWriteLock();
                    }
                }
                if (addOperation is not null)
                {
                    var addableResult = Activator.CreateInstance(resultType);
                    var newElementType = resultType.GetGenericArguments()[0];
                    var i = 0;
                    foreach (var element in (source as IEnumerable)!)
                    {
                        var iStr = (i++).ToString();
                        var path = pathRoot.Length > 0 ? pathRoot + "." + iStr : iStr;
                        var newElement = MakeDeepCopy(element, newElementType, processor, path);
                        _ = InvokeAddOperation(addOperation, addableResult, newElement);
                    }
                    return addableResult;
                }
            }
            finally
            {
                addHandlerCacheLock.ExitUpgradeableReadLock();
            }
        }

        if (type.IsArray && resultType.IsArray)
        {
            var elementType = resultType.GetElementType();
            var length = ((Array)source).Length;
            var arrayResult = Activator.CreateInstance(resultType, [length]) as object[];
            var originalArray = source as object[];
            for (var i = 0; i < length; i++)
            {
                var iStr = i.ToString();
                var path = pathRoot.Length > 0 ? pathRoot + "." + iStr : iStr;
                arrayResult![i] = MakeDeepCopy(originalArray![i], elementType, processor, path)!;
            }
            return arrayResult;
        }

        var ns = type.Namespace;
        if (ns == "System" || (ns?.StartsWith("System.") ?? false))
            return source;

        var result = CreateInstance(resultType == typeof(object) ? type : resultType);
        Traverse.IterateFields(source, result!, (name, src, dst) =>
        {
            var path = pathRoot.Length > 0 ? pathRoot + "." + name : name;
            var value = processor is not null ? processor(path, src, dst) : src.GetValue();
            if (dst.IsWriteable)
                _ = dst.SetValue(MakeDeepCopy(value, dst.GetValueType(), processor, path));
        });
        return result;
    }

    public static FieldRef<T, F> FieldRefAccess<T, F>(string fieldName) => throw UnsupportedFieldRef("FieldRefAccess");

    public static ref F FieldRefAccess<T, F>(T instance, string fieldName) => throw UnsupportedFieldRef("FieldRefAccess");

    public static FieldRef<object, F> FieldRefAccess<F>(Type type, string fieldName) => throw UnsupportedFieldRef("FieldRefAccess");

    public static FieldRef<object, F> FieldRefAccess<F>(string typeColonName) => throw UnsupportedFieldRef("FieldRefAccess");

    public static FieldRef<T, F> FieldRefAccess<T, F>(FieldInfo fieldInfo) => throw UnsupportedFieldRef("FieldRefAccess");

    public static ref F FieldRefAccess<T, F>(T instance, FieldInfo fieldInfo) => throw UnsupportedFieldRef("FieldRefAccess");

    public static StructFieldRef<T, F> StructFieldRefAccess<T, F>(string fieldName) where T : struct
        => throw UnsupportedFieldRef("StructFieldRefAccess");

    public static ref F StructFieldRefAccess<T, F>(ref T instance, string fieldName) where T : struct
        => throw UnsupportedFieldRef("StructFieldRefAccess");

    public static StructFieldRef<T, F> StructFieldRefAccess<T, F>(FieldInfo fieldInfo) where T : struct
        => throw UnsupportedFieldRef("StructFieldRefAccess");

    public static ref F StructFieldRefAccess<T, F>(ref T instance, FieldInfo fieldInfo) where T : struct
        => throw UnsupportedFieldRef("StructFieldRefAccess");

    public static ref F StaticFieldRefAccess<T, F>(string fieldName) => throw UnsupportedFieldRef("StaticFieldRefAccess");

    public static ref F StaticFieldRefAccess<F>(Type type, string fieldName) => throw UnsupportedFieldRef("StaticFieldRefAccess");

    public static ref F StaticFieldRefAccess<F>(string typeColonName) => throw UnsupportedFieldRef("StaticFieldRefAccess");

    public static ref F StaticFieldRefAccess<T, F>(FieldInfo fieldInfo) => throw UnsupportedFieldRef("StaticFieldRefAccess");

    public static FieldRef<F> StaticFieldRefAccess<F>(FieldInfo fieldInfo) => throw UnsupportedFieldRef("StaticFieldRefAccess");

    public static bool IsNetFrameworkRuntime { get; }
        = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.Ordinal);

    public static bool IsNetCoreRuntime { get; }
        = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.StartsWith(".NET Core", StringComparison.Ordinal)
          || System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.StartsWith(".NET ", StringComparison.Ordinal);

    private static NotSupportedException UnsupportedFieldRef(string member)
    {
        var detail = $"{member} requires a generated ref-return delegate and runtime IL emission; reflection cannot preserve ref identity.";
        HarmonyRegistry.ReportUnavailable($"AccessTools.{member}", detail);
        return new NotSupportedException(detail);
    }
}

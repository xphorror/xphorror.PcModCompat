using System.Reflection;

namespace HarmonyLib;

// ABI mirror of Harmony 2.4 HarmonyLib/Tools/AccessToolsExtensions.cs.
//
// Every member is a one-line forward to the AccessTools static of the same name - upstream is the
// same - so this file adds no behaviour, only the fluent `type.Method("Name")` spelling. It exists
// because a missing extension method is a compile/load failure for the whole MOD assembly, not a
// degraded lookup, and this spelling is common in MOD code.
//
// Absent for the same reason as their AccessTools counterparts: FieldRefAccess and
// StaticFieldRefAccess need runtime IL emission, which this host does not provide.
public static class AccessToolsExtensions
{
    public static IEnumerable<Type> InnerTypes(this Type type) => AccessTools.InnerTypes(type);

    public static T? FindIncludingBaseTypes<T>(this Type type, Func<Type, T?> func) where T : class
        => AccessTools.FindIncludingBaseTypes(type, func);

    public static T? FindIncludingInnerTypes<T>(this Type type, Func<Type, T?> func) where T : class
        => AccessTools.FindIncludingInnerTypes(type, func);

    public static FieldInfo? DeclaredField(this Type type, string name) => AccessTools.DeclaredField(type, name);

    public static FieldInfo? Field(this Type type, string name) => AccessTools.Field(type, name);

    public static AccessTools.FieldRef<object, F> FieldRefAccess<F>(this Type type, string fieldName)
        => AccessTools.FieldRefAccess<F>(type, fieldName);

    public static ref F StaticFieldRefAccess<F>(this Type type, string fieldName)
        => ref AccessTools.StaticFieldRefAccess<F>(type, fieldName);

    public static FieldInfo? DeclaredField(this Type type, int idx) => AccessTools.DeclaredField(type, idx);

    public static PropertyInfo? DeclaredProperty(this Type type, string name) => AccessTools.DeclaredProperty(type, name);

    public static PropertyInfo? DeclaredIndexer(this Type type, Type[]? parameters = null) => AccessTools.DeclaredIndexer(type, parameters);

    public static MethodInfo? DeclaredPropertyGetter(this Type type, string name) => AccessTools.DeclaredPropertyGetter(type, name);

    public static MethodInfo? DeclaredIndexerGetter(this Type type, Type[]? parameters = null) => AccessTools.DeclaredIndexerGetter(type, parameters);

    public static MethodInfo? DeclaredPropertySetter(this Type type, string name) => AccessTools.DeclaredPropertySetter(type, name);

    public static MethodInfo? DeclaredIndexerSetter(this Type type, Type[]? parameters) => AccessTools.DeclaredIndexerSetter(type, parameters);

    public static PropertyInfo? Property(this Type type, string name) => AccessTools.Property(type, name);

    public static PropertyInfo? Indexer(this Type type, Type[]? parameters = null) => AccessTools.Indexer(type, parameters);

    public static MethodInfo? PropertyGetter(this Type type, string name) => AccessTools.PropertyGetter(type, name);

    public static MethodInfo? IndexerGetter(this Type type, Type[]? parameters = null) => AccessTools.IndexerGetter(type, parameters);

    public static MethodInfo? PropertySetter(this Type type, string name) => AccessTools.PropertySetter(type, name);

    public static MethodInfo? IndexerSetter(this Type type, Type[]? parameters = null) => AccessTools.IndexerSetter(type, parameters);

    public static EventInfo? DeclaredEvent(this Type type, string name) => AccessTools.DeclaredEvent(type, name);

    public static EventInfo? Event(this Type type, string name) => AccessTools.Event(type, name);

    public static MethodInfo? DeclaredEventAdder(this Type type, string name) => AccessTools.DeclaredEventAdder(type, name);

    public static MethodInfo? EventAdder(this Type type, string name) => AccessTools.EventAdder(type, name);

    public static MethodInfo? DeclaredEventRemover(this Type type, string name) => AccessTools.DeclaredEventRemover(type, name);

    public static MethodInfo? EventRemover(this Type type, string name) => AccessTools.EventRemover(type, name);

    public static MethodInfo? Finalizer(this Type type) => AccessTools.Finalizer(type);

    public static MethodInfo? DeclaredFinalizer(this Type type) => AccessTools.DeclaredFinalizer(type);

    public static MethodInfo? DeclaredMethod(this Type type, string name, Type[]? parameters = null, Type[]? generics = null)
        => AccessTools.DeclaredMethod(type, name, parameters, generics);

    public static MethodInfo? Method(this Type type, string name, Type[]? parameters = null, Type[]? generics = null)
        => AccessTools.Method(type, name, parameters, generics);

    public static List<string> GetMethodNames(this Type type) => AccessTools.GetMethodNames(type);

    public static List<string> GetFieldNames(this Type type) => AccessTools.GetFieldNames(type);

    public static List<string> GetPropertyNames(this Type type) => AccessTools.GetPropertyNames(type);

    public static ConstructorInfo? DeclaredConstructor(this Type type, Type[]? parameters = null, bool searchForStatic = false)
        => AccessTools.DeclaredConstructor(type, parameters, searchForStatic);

    public static ConstructorInfo? Constructor(this Type type, Type[]? parameters = null, bool searchForStatic = false)
        => AccessTools.Constructor(type, parameters, searchForStatic);

    public static List<ConstructorInfo> GetDeclaredConstructors(this Type type, bool? searchForStatic = null)
        => AccessTools.GetDeclaredConstructors(type, searchForStatic);

    public static List<MethodInfo> GetDeclaredMethods(this Type type) => AccessTools.GetDeclaredMethods(type);

    public static List<PropertyInfo> GetDeclaredProperties(this Type type) => AccessTools.GetDeclaredProperties(type);

    public static List<FieldInfo> GetDeclaredFields(this Type type) => AccessTools.GetDeclaredFields(type);

    public static Type? Inner(this Type type, string name) => AccessTools.Inner(type, name);

    public static Type? FirstInner(this Type type, Func<Type, bool> predicate) => AccessTools.FirstInner(type, predicate);

    public static MethodInfo? FirstMethod(this Type type, Func<MethodInfo, bool> predicate) => AccessTools.FirstMethod(type, predicate);

    public static ConstructorInfo? FirstConstructor(this Type type, Func<ConstructorInfo, bool> predicate) => AccessTools.FirstConstructor(type, predicate);

    public static PropertyInfo? FirstProperty(this Type type, Func<PropertyInfo, bool> predicate) => AccessTools.FirstProperty(type, predicate);

    public static void ThrowMissingMemberException(this Type type, params string[] names) => AccessTools.ThrowMissingMemberException(type, names);

    public static object? GetDefaultValue(this Type type) => AccessTools.GetDefaultValue(type);

    public static object? CreateInstance(this Type type) => AccessTools.CreateInstance(type);

    public static bool IsStruct(this Type type) => AccessTools.IsStruct(type);

    public static bool IsClass(this Type type) => AccessTools.IsClass(type);

    public static bool IsValue(this Type type) => AccessTools.IsValue(type);

    public static bool IsInteger(this Type type) => AccessTools.IsInteger(type);

    public static bool IsFloatingPoint(this Type type) => AccessTools.IsFloatingPoint(type);

    public static bool IsNumber(this Type type) => AccessTools.IsNumber(type);

    public static bool IsVoid(this Type type) => AccessTools.IsVoid(type);

    public static bool IsStatic(this Type type) => AccessTools.IsStatic(type);
}

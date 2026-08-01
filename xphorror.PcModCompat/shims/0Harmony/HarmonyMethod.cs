using System.Reflection;

namespace HarmonyLib;

// ABI mirror of Harmony 2.4 HarmonyLib/Public/HarmonyMethod.cs.
//
// Field names, declaration order and default values matter: upstream Merge()/CopyTo() walk the
// public field list through Traverse, and MODs read the fields directly. The shim implements the
// same merge rules with explicit field access instead of reflection (same observable result,
// no Traverse dependency in the hot path).
public class HarmonyMethod
{
    public MethodInfo? method; // need to be called 'method'

    public string? category = null;

    public Type? declaringType;

    public string? methodName;

    public MethodType? methodType;

    public Type[]? argumentTypes;

    public int priority = -1;

    public string[]? before;

    public string[]? after;

    public HarmonyReversePatchType? reversePatchType;

    public bool? debug;

    public bool nonVirtualDelegate;

    // Not part of the upstream ABI (and deliberately excluded from HarmonyFields()).
    // Upstream throws away the raw type name once AccessTools.TypeByName fails, which on Android is
    // the normal case for IL2CPP game types. Keeping the string lets the runtime aggregator still
    // name the target so the registration lines up with what the static aggregator produced.
    internal string? declaringTypeName;

    public HarmonyMethod()
    {
    }

    private void ImportMethod(MethodInfo? theMethod)
    {
        method = theMethod;
        if (method is not null)
        {
            var infos = HarmonyMethodExtensions.GetFromMethod(method);
            if (infos is not null)
                Merge(infos).CopyTo(this);
        }
    }

    public HarmonyMethod(MethodInfo method) => ImportMethod(method);

    public HarmonyMethod(Delegate @delegate) => ImportMethod(@delegate.Method);

    public HarmonyMethod(
        MethodInfo method,
        int priority = -1,
        string[]? before = null,
        string[]? after = null,
        bool? debug = null)
    {
        ImportMethod(method);
        this.priority = priority;
        this.before = before;
        this.after = after;
        this.debug = debug;
    }

    public HarmonyMethod(
        Delegate @delegate,
        int priority = -1,
        string[]? before = null,
        string[]? after = null,
        bool? debug = null)
        : this(@delegate.Method, priority, before, after, debug)
    {
    }

    public HarmonyMethod(Type methodType, string methodName, Type[]? argumentTypes = null)
    {
        var method = AccessTools.DeclaredMethod(methodType, methodName, argumentTypes)
                     ?? throw new ArgumentException($"Cannot not find method for type {methodType} and name {methodName} and parameters {argumentTypes?.Description()}");
        ImportMethod(method);
    }

    // Upstream derives this from AccessTools.GetFieldNames(typeof(HarmonyMethod)) minus "method".
    // The literal list keeps the same names/order without reflecting over the type, which also
    // means adding an internal helper field can never silently change merge behaviour.
    public static List<string> HarmonyFields() =>
    [
        nameof(category),
        nameof(declaringType),
        nameof(methodName),
        nameof(methodType),
        nameof(argumentTypes),
        nameof(priority),
        nameof(before),
        nameof(after),
        nameof(reversePatchType),
        nameof(debug),
        nameof(nonVirtualDelegate)
    ];

    public static HarmonyMethod Merge(List<HarmonyMethod> attributes)
    {
        var result = new HarmonyMethod();
        if (attributes is null || attributes.Count == 0)
            return result;

        foreach (var attribute in attributes)
        {
            if (attribute is null)
                continue;

            if (attribute.category is not null)
                result.category = attribute.category;
            if (attribute.declaringType is not null)
                result.declaringType = attribute.declaringType;
            if (attribute.declaringTypeName is not null)
                result.declaringTypeName = attribute.declaringTypeName;
            if (attribute.methodName is not null)
                result.methodName = attribute.methodName;
            if (attribute.methodType.HasValue)
                result.methodType = attribute.methodType;
            if (attribute.argumentTypes is not null)
                result.argumentTypes = attribute.argumentTypes;
            // priority defaults to -1 instead of null, so an unset value must not overwrite a
            // HarmonyPriority attribute that was merged earlier.
            if (attribute.priority != -1)
                result.priority = attribute.priority;
            if (attribute.before is not null)
                result.before = attribute.before;
            if (attribute.after is not null)
                result.after = attribute.after;
            if (attribute.reversePatchType.HasValue)
                result.reversePatchType = attribute.reversePatchType;
            if (attribute.debug.HasValue)
                result.debug = attribute.debug;
            // A non-nullable bool is never "unset" upstream either: the last attribute wins.
            result.nonVirtualDelegate = attribute.nonVirtualDelegate;
        }

        return result;
    }

    public override string ToString()
    {
        var result = "";
        void Append(string name, object? value)
        {
            if (result.Length > 0)
                result += ", ";
            result += $"{name}={value}";
        }

        Append(nameof(category), category);
        Append(nameof(declaringType), declaringType);
        Append(nameof(methodName), methodName);
        Append(nameof(methodType), methodType);
        Append(nameof(argumentTypes), argumentTypes);
        Append(nameof(priority), priority);
        Append(nameof(before), before);
        Append(nameof(after), after);
        Append(nameof(reversePatchType), reversePatchType);
        Append(nameof(debug), debug);
        Append(nameof(nonVirtualDelegate), nonVirtualDelegate);
        return $"HarmonyMethod[{result}]";
    }

    // used for error reporting
    internal string Description()
    {
        var cName = declaringType is not null ? declaringType.FullName : "undefined";
        var mName = methodName ?? "undefined";
        var tName = methodType.HasValue ? methodType.Value.ToString() : "undefined";
        var aName = argumentTypes is not null ? argumentTypes.Description() : "undefined";
        return $"(class={cName}, methodname={mName}, type={tName}, args={aName})";
    }

    public static implicit operator HarmonyMethod(MethodInfo method) => new(method);

    public static implicit operator HarmonyMethod(Delegate @delegate) => new(@delegate);
}

public static class HarmonyMethodExtensions
{
    public static void CopyTo(this HarmonyMethod from, HarmonyMethod to)
    {
        if (to is null)
            return;

        if (from.category is not null)
            to.category = from.category;
        if (from.declaringType is not null)
            to.declaringType = from.declaringType;
        if (from.declaringTypeName is not null)
            to.declaringTypeName = from.declaringTypeName;
        if (from.methodName is not null)
            to.methodName = from.methodName;
        if (from.methodType.HasValue)
            to.methodType = from.methodType;
        if (from.argumentTypes is not null)
            to.argumentTypes = from.argumentTypes;
        if (from.priority != -1)
            to.priority = from.priority;
        if (from.before is not null)
            to.before = from.before;
        if (from.after is not null)
            to.after = from.after;
        if (from.reversePatchType.HasValue)
            to.reversePatchType = from.reversePatchType;
        if (from.debug.HasValue)
            to.debug = from.debug;
        to.nonVirtualDelegate = from.nonVirtualDelegate;
    }

    public static HarmonyMethod Clone(this HarmonyMethod original)
    {
        var result = new HarmonyMethod();
        original.CopyTo(result);
        result.method = original.method;
        return result;
    }

    public static HarmonyMethod Merge(this HarmonyMethod master, HarmonyMethod? detail)
    {
        if (detail is null)
            return master;

        var result = new HarmonyMethod
        {
            category = detail.category ?? master.category,
            declaringType = detail.declaringType ?? master.declaringType,
            methodName = detail.methodName ?? master.methodName,
            methodType = detail.methodType ?? master.methodType,
            argumentTypes = detail.argumentTypes ?? master.argumentTypes,
            before = detail.before ?? master.before,
            after = detail.after ?? master.after,
            reversePatchType = detail.reversePatchType ?? master.reversePatchType,
            debug = detail.debug ?? master.debug,
            nonVirtualDelegate = detail.nonVirtualDelegate,
            priority = MergePriority(master.priority, detail.priority)
        };
        return result;
    }

    // priority defaults to -1 rather than null upstream, so "unset" has to be filtered out
    // before falling back to Math.Max.
    private static int MergePriority(int master, int detail)
    {
        if (master == -1 && detail != -1)
            return detail;
        if (master != -1 && detail == -1)
            return master;
        return Math.Max(master, detail);
    }

    private static HarmonyMethod? GetHarmonyMethodInfo(object attribute)
    {
        var f_info = attribute.GetType().GetField(nameof(HarmonyAttribute.info), AccessTools.all);
        if (f_info is null)
            return null;
        if (f_info.FieldType.FullName != typeof(HarmonyMethod).FullName)
            return null;
        return (f_info.GetValue(attribute) as HarmonyMethod)?.Clone();
    }

    public static List<HarmonyMethod> GetFromType(Type type)
        => [.. type.GetCustomAttributes(true)
            .Select(GetHarmonyMethodInfo)
            .Where(info => info is not null)
            .Select(info => info!)];

    public static HarmonyMethod GetMergedFromType(Type type) => HarmonyMethod.Merge(GetFromType(type));

    public static List<HarmonyMethod> GetFromMethod(MethodBase method)
        => [.. method.GetCustomAttributes(true)
            .Select(GetHarmonyMethodInfo)
            .Where(info => info is not null)
            .Select(info => info!)];

    public static HarmonyMethod GetMergedFromMethod(MethodBase method) => HarmonyMethod.Merge(GetFromMethod(method));
}

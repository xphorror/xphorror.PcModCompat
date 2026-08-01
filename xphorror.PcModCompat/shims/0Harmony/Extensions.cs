using System.Reflection;
using System.Text;

namespace HarmonyLib;

// ABI mirror of the string/collection helpers in Harmony 2.4 HarmonyLib/Tools/Extensions.cs.
// These are pure helpers, so the shim reproduces upstream output verbatim - error messages coming
// out of the aggregator have to be diffable against a real Harmony run.
public static class GeneralExtensions
{
    public static string Join<T>(this IEnumerable<T>? enumeration, Func<T, string>? converter = null, string delimiter = ", ")
    {
        if (enumeration is null)
            return string.Empty;
        converter ??= item => item?.ToString() ?? string.Empty;
        return string.Join(delimiter, enumeration.Select(converter));
    }

    public static string Description(this Type[]? parameters)
    {
        if (parameters is null)
            return "NULL";
        return $"({parameters.Join(p => p.FullDescription())})";
    }

    public static string FullDescription(this Type? type)
    {
        if (type is null)
            return "null";

        var ns = type.Namespace;
        if (string.IsNullOrEmpty(ns) is false)
            ns += ".";
        var result = ns + type.Name;

        if (type.IsGenericType)
        {
            result += "<";
            var subTypes = type.GetGenericArguments();
            for (var i = 0; i < subTypes.Length; i++)
            {
                if (result.EndsWith('<') is false)
                    result += ", ";
                result += subTypes[i].FullDescription();
            }
            result += ">";
        }
        return result;
    }

    public static string FullDescription(this MethodBase? member)
    {
        if (member is null)
            return "null";
        var returnType = AccessTools.GetReturnedType(member);

        var result = new StringBuilder();
        if (member.IsStatic)
            _ = result.Append("static ");
        if (member.IsAbstract)
            _ = result.Append("abstract ");
        if (member.IsVirtual)
            _ = result.Append("virtual ");
        _ = result.Append($"{returnType.FullDescription()} ");
        if (member.DeclaringType is not null)
            _ = result.Append($"{member.DeclaringType.FullDescription()}::");
        var parameterString = member.GetParameters().Join(p => $"{p.ParameterType.FullDescription()} {p.Name}");
        _ = result.Append($"{member.Name}({parameterString})");
        return result.ToString();
    }

    public static Type[] Types(this ParameterInfo[] pinfo) => [.. pinfo.Select(pi => pi.ParameterType)];

    public static bool HasHarmonyAttribute(this Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return HarmonyMethodExtensions.GetFromType(type).Count > 0;
    }

    public static T? GetValueSafe<S, T>(this Dictionary<S, T> dictionary, S key) where S : notnull
        => dictionary.TryGetValue(key, out var result) ? result : default;

    public static T? GetTypedValue<T>(this Dictionary<string, object> dictionary, string key)
        => dictionary.TryGetValue(key, out var result) && result is T value ? value : default;

    public static string ToLiteral(this string? input, string quoteChar = "\"")
    {
        if (input is null)
            return "null";

        var literal = new StringBuilder(input.Length + 2);
        _ = literal.Append(quoteChar);
        foreach (var c in input)
        {
            switch (c)
            {
                case '\'': _ = literal.Append(@"\'"); break;
                case '\"': _ = literal.Append("\\\""); break;
                case '\\': _ = literal.Append(@"\\"); break;
                case '\0': _ = literal.Append(@"\0"); break;
                case '\a': _ = literal.Append(@"\a"); break;
                case '\b': _ = literal.Append(@"\b"); break;
                case '\f': _ = literal.Append(@"\f"); break;
                case '\n': _ = literal.Append(@"\n"); break;
                case '\r': _ = literal.Append(@"\r"); break;
                case '\t': _ = literal.Append(@"\t"); break;
                case '\v': _ = literal.Append(@"\v"); break;
                default:
                    if (c >= 0x20 && c <= 0x7e)
                        _ = literal.Append(c);
                    else
                        _ = literal.Append(@"\u").Append(((int)c).ToString("x4"));
                    break;
            }
        }
        _ = literal.Append(quoteChar);
        return literal.ToString();
    }
}

public static class CollectionExtensions
{
    public static void Do<T>(this IEnumerable<T>? sequence, Action<T> action)
    {
        if (sequence is null)
            return;
        foreach (var item in sequence.ToList())
            action(item);
    }

    public static void DoIf<T>(this IEnumerable<T>? sequence, Func<T, bool> condition, Action<T> action)
        => (sequence ?? []).Where(condition).Do(action);

    public static IEnumerable<T> AddItem<T>(this IEnumerable<T>? sequence, T item)
        => (sequence ?? []).Concat([item]);

    public static T[] AddRangeToArray<T>(this T[]? sequence, T[]? items)
        => [.. sequence ?? [], .. items ?? []];

    public static T[] AddToArray<T>(this T[]? sequence, T item) => sequence.AddItem(item).ToArray();
}

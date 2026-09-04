using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Xphorror.PcModCompat;

/// <summary>
/// A minimal reimplementation of Unity's <c>JsonUtility</c> serialization rules, for MOD-owned types
/// that the IL2CPP side cannot see.
/// </summary>
/// <remarks>
/// <para>
/// The IL2CPP <c>JsonUtility</c> reflects over the runtime type it is handed. A CoreCLR type such as
/// JipperKeyViewer's <c>ProfileData</c> is not in the IL2CPP class table at all, so forwarding the
/// call produces either a failure or <c>{}</c> - no amount of boxing fixes that. Serializing on the
/// managed side is the only option, which makes this a manual bridge rather than a converter.
/// </para>
/// <para>
/// The goal is byte-compatibility with Unity's format, not with .NET conventions: a profile written on
/// PC has to load here and vice versa. That rules out <c>System.Text.Json</c>, which serializes
/// properties rather than fields, camel-cases names, and writes enums as strings - three defaults that
/// would each have to be undone, after which the remaining behavioural gaps are the hard part.
/// </para>
/// <para>
/// Unity's rules that matter here, and are implemented:
/// public instance fields plus <c>[SerializeField]</c> private ones; properties are never serialized;
/// enums as their underlying integer; arrays and <c>List&lt;T&gt;</c> as JSON arrays; structs recursed
/// into by field (so <c>Color</c> becomes <c>{"r":..,"g":..,"b":..,"a":..}</c>); <c>null</c> strings
/// written as <c>""</c>; <c>null</c> arrays written as <c>[]</c>.
/// </para>
/// <para>
/// Deliberately unsupported, because Unity does not support them either: dictionaries, interfaces and
/// polymorphism. An unsupported shape throws rather than emitting partial JSON, so a silently
/// truncated profile cannot overwrite a good one.
/// </para>
/// </remarks>
public static class PcCompatUnityJson
{
    private const BindingFlags FieldFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Serializes <paramref name="value"/> the way <c>JsonUtility.ToJson</c> would.</summary>
    public static string ToJson(object? value, bool prettyPrint)
    {
        // Unity returns "{}" for null rather than "null"; a caller writing that to a settings file
        // and reading it back gets defaults, which is the intended degradation.
        if (value is null)
            return "{}";

        var builder = new StringBuilder();
        WriteObject(builder, value, prettyPrint, 0);
        return builder.ToString();
    }

    /// <summary>
    /// Overwrites the fields of <paramref name="target"/> that appear in <paramref name="json"/>,
    /// the way <c>JsonUtility.FromJsonOverwrite</c> would.
    /// </summary>
    /// <remarks>
    /// Fields absent from the JSON keep their current value. JipperKeyViewer depends on this exact
    /// behaviour: it replaces the instance with a fresh default before overwriting, specifically so
    /// that absent fields fall back to defaults rather than leaking across profile switches.
    /// </remarks>
    public static void FromJsonOverwrite(string? json, object? target)
    {
        if (target is null || string.IsNullOrWhiteSpace(json))
            return;

        var reader = new JsonReader(json!);
        var parsed = reader.ReadValue();
        if (parsed is Dictionary<string, object?> members)
            PopulateObject(target, members);
    }

    /// <summary>Deserializes a fresh instance, the way <c>JsonUtility.FromJson&lt;T&gt;</c> would.</summary>
    public static T? FromJson<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;

        var instance = Activator.CreateInstance<T>();
        FromJsonOverwrite(json, instance);
        return instance;
    }

    private static void WriteObject(StringBuilder builder, object value, bool pretty, int depth)
    {
        builder.Append('{');
        var first = true;
        foreach (var field in SerializedFields(value.GetType()))
        {
            if (!first)
                builder.Append(',');
            first = false;
            NewLine(builder, pretty, depth + 1);
            WriteString(builder, field.Name);
            builder.Append(':');
            if (pretty)
                builder.Append(' ');
            WriteValue(builder, field.GetValue(value), field.FieldType, pretty, depth + 1);
        }
        if (!first)
            NewLine(builder, pretty, depth);
        builder.Append('}');
    }

    private static void WriteValue(
        StringBuilder builder,
        object? value,
        Type declaredType,
        bool pretty,
        int depth)
    {
        var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;

        if (type == typeof(string))
        {
            // Unity writes a null string as "", not null. JipperKeyViewer relies on this: it detects
            // "user kept the default label" by comparing against the default string, and notes in its
            // own source that null and "" are indistinguishable after a round trip.
            WriteString(builder, value as string ?? string.Empty);
            return;
        }

        if (type.IsEnum)
        {
            builder.Append(Convert.ToInt64(value ?? 0, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (type == typeof(bool))
        {
            builder.Append((value as bool? ?? false) ? "true" : "false");
            return;
        }

        if (IsIntegral(type))
        {
            builder.Append(Convert.ToInt64(value ?? 0, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (type == typeof(float) || type == typeof(double))
        {
            WriteNumber(builder, Convert.ToDouble(value ?? 0d, CultureInfo.InvariantCulture));
            return;
        }

        if (TryGetElementType(type, out var elementType))
        {
            WriteArray(builder, value as IEnumerable, elementType, pretty, depth);
            return;
        }

        if (value is null)
        {
            // Unity emits {} for a null nested object, keeping the field present so a round trip
            // reconstructs a default instance instead of dropping the member.
            builder.Append("{}");
            return;
        }

        // Reject collections that are not arrays or List<T> before the class/struct fallback below.
        // Without this, a Dictionary<K,V> would be walked field-by-field and emit its private buckets
        // and comparer as JSON - output that looks valid, restores nothing, and would be written over
        // a live settings file. Unity does not serialize dictionaries either, so failing loudly here
        // matches it and is the only way the caller can tell.
        if (value is IEnumerable and not string)
        {
            throw new NotSupportedException(
                $"PcCompatUnityJson cannot serialize {declaredType.FullName}; Unity JsonUtility " +
                "serializes only arrays and List<T>, not other collection types.");
        }

        if (type.IsClass || type.IsValueType)
        {
            WriteObject(builder, value, pretty, depth);
            return;
        }

        throw new NotSupportedException(
            $"PcCompatUnityJson cannot serialize {declaredType.FullName}; Unity JsonUtility " +
            "supports only fields of primitive, enum, string, array/List and serializable " +
            "class/struct types.");
    }

    private static void WriteArray(
        StringBuilder builder,
        IEnumerable? items,
        Type elementType,
        bool pretty,
        int depth)
    {
        builder.Append('[');
        if (items is null)
        {
            // A null array becomes [], matching Unity. The MOD's own constructor re-creates arrays
            // that come back empty, so this is the shape it already handles.
            builder.Append(']');
            return;
        }

        var first = true;
        foreach (var item in items)
        {
            if (!first)
                builder.Append(',');
            first = false;
            NewLine(builder, pretty, depth + 1);
            WriteValue(builder, item, elementType, pretty, depth + 1);
        }
        if (!first)
            NewLine(builder, pretty, depth);
        builder.Append(']');
    }

    /// <summary>
    /// Writes a float the way Unity does: round-trippable, invariant, and never in exponential form
    /// with a capital E or a leading "+" that Unity's own reader would reject.
    /// </summary>
    private static void WriteNumber(StringBuilder builder, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            // Unity writes these as 0; emitting NaN would produce JSON its own parser rejects.
            builder.Append('0');
            return;
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        builder.Append(text);
    }

    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (character < ' ')
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(character);
                    break;
            }
        }
        builder.Append('"');
    }

    private static void NewLine(StringBuilder builder, bool pretty, int depth)
    {
        if (!pretty)
            return;
        builder.Append('\n');
        builder.Append(' ', depth * 4);
    }

    private static void PopulateObject(object target, Dictionary<string, object?> members)
    {
        foreach (var field in SerializedFields(target.GetType()))
        {
            if (!members.TryGetValue(field.Name, out var raw))
                continue;
            if (TryConvert(raw, field.FieldType, field.GetValue(target), out var converted))
                field.SetValue(target, converted);
        }
    }

    /// <summary>
    /// Converts a parsed JSON value to the field's declared type, leaving the field untouched when the
    /// JSON shape does not fit.
    /// </summary>
    /// <remarks>
    /// A mismatch is skipped rather than thrown on, because Unity does the same: a hand-edited or
    /// version-skewed settings file must not take the whole load down, it must lose one field. The
    /// serialize direction is strict; the deserialize direction is lenient. That asymmetry is Unity's,
    /// and matching it is the point.
    /// </remarks>
    private static bool TryConvert(object? raw, Type declaredType, object? current, out object? result)
    {
        result = null;
        var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;

        if (type == typeof(string))
        {
            result = raw as string ?? (raw is null ? string.Empty : null);
            return result is not null;
        }

        if (type.IsEnum)
        {
            if (raw is not double number)
                return false;
            result = Enum.ToObject(type, Convert.ToInt64(number, CultureInfo.InvariantCulture));
            return true;
        }

        if (type == typeof(bool))
        {
            if (raw is bool flag)
            {
                result = flag;
                return true;
            }
            // Unity accepts 0/1 here, and older hand-written files use it.
            if (raw is double numeric)
            {
                result = numeric != 0d;
                return true;
            }
            return false;
        }

        if (IsIntegral(type) || type == typeof(float) || type == typeof(double))
        {
            if (raw is not double value)
                return false;
            result = Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
            return true;
        }

        if (TryGetElementType(type, out var elementType))
        {
            if (raw is not List<object?> items)
                return false;
            result = BuildSequence(type, elementType, items);
            return true;
        }

        if (raw is not Dictionary<string, object?> members)
            return false;

        // Reuse the instance already on the field so that, exactly like FromJsonOverwrite, members
        // absent from the JSON keep their current value instead of reverting to type defaults.
        var instance = current ?? CreateInstance(type);
        if (instance is null)
            return false;
        PopulateObject(instance, members);
        result = instance;
        return true;
    }

    private static object BuildSequence(Type declaredType, Type elementType, List<object?> items)
    {
        var values = Array.CreateInstance(elementType, items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            values.SetValue(
                TryConvert(items[index], elementType, null, out var converted)
                    ? converted
                    : CreateInstance(elementType),
                index);
        }

        if (declaredType.IsArray)
            return values;

        var list = (IList)Activator.CreateInstance(declaredType)!;
        foreach (var value in values)
            list.Add(value);
        return list;
    }

    private static object? CreateInstance(Type type)
        => type.IsValueType ? Activator.CreateInstance(type) : TryCreateReference(type);

    private static object? TryCreateReference(Type type)
    {
        try
        {
            return Activator.CreateInstance(type);
        }
        catch (MissingMethodException)
        {
            // No public parameterless constructor. Unity would leave the field alone rather than
            // fail the whole load, so the caller treats this as "shape does not fit".
            return null;
        }
    }

    /// <summary>
    /// Unity's field-selection rule: public instance fields, plus private ones marked
    /// <c>[SerializeField]</c>. Static, const, readonly and <c>[NonSerialized]</c> fields are skipped,
    /// as are compiler-generated backing fields - a property is never serialized, and its backing
    /// field carries a name no Unity-written JSON contains.
    /// </summary>
    private static IEnumerable<FieldInfo> SerializedFields(Type type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Base fields first, matching Unity's declaration order for an inherited serializable class.
        var chain = new List<Type>();
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
            chain.Add(current);
        chain.Reverse();

        foreach (var level in chain)
        foreach (var field in level.GetFields(FieldFlags | BindingFlags.DeclaredOnly))
        {
            if (field.IsStatic || field.IsLiteral || field.IsInitOnly)
                continue;
            if (field.IsDefined(typeof(NonSerializedAttribute), inherit: false))
                continue;
            if (field.Name.StartsWith('<'))
                continue;
            if (!field.IsPublic && !IsSerializeField(field))
                continue;
            if (!seen.Add(field.Name))
                continue;
            yield return field;
        }
    }

    /// <summary>
    /// Detects <c>UnityEngine.SerializeField</c> by name rather than by type.
    /// </summary>
    /// <remarks>
    /// The attribute the MOD applied comes from the generated Unity proxy, which this assembly does
    /// not reference; matching on the full name avoids taking that dependency and keeps working if the
    /// MOD was compiled against a different Unity version.
    /// </remarks>
    private static bool IsSerializeField(FieldInfo field)
        => field.CustomAttributes.Any(attribute =>
            attribute.AttributeType.FullName == "UnityEngine.SerializeField");

    private static bool IsIntegral(Type type)
        => type == typeof(byte) || type == typeof(sbyte) ||
           type == typeof(short) || type == typeof(ushort) ||
           type == typeof(int) || type == typeof(uint) ||
           type == typeof(long) || type == typeof(ulong) ||
           type == typeof(char);

    private static bool TryGetElementType(Type type, out Type elementType)
    {
        if (type.IsArray && type.GetArrayRank() == 1)
        {
            elementType = type.GetElementType()!;
            return true;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            elementType = type.GetGenericArguments()[0];
            return true;
        }
        elementType = typeof(object);
        return false;
    }

    /// <summary>
    /// A JSON reader limited to what Unity emits, plus what a user might hand-edit into a settings
    /// file. Numbers are all read as <see cref="double"/> and narrowed by the field's declared type,
    /// which is how Unity behaves and avoids guessing int-vs-float from the text.
    /// </summary>
    private sealed class JsonReader(string text)
    {
        private int _index;

        public object? ReadValue()
        {
            SkipWhitespace();
            if (_index >= text.Length)
                return null;
            return text[_index] switch
            {
                '{' => ReadObject(),
                '[' => ReadArray(),
                '"' => ReadString(),
                't' or 'f' => ReadBoolean(),
                'n' => ReadNull(),
                _ => ReadNumber()
            };
        }

        private Dictionary<string, object?> ReadObject()
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            Expect('{');
            SkipWhitespace();
            if (Peek() == '}')
            {
                _index++;
                return result;
            }

            while (true)
            {
                SkipWhitespace();
                var name = ReadString();
                SkipWhitespace();
                Expect(':');
                result[name] = ReadValue();
                SkipWhitespace();
                var next = Read();
                if (next == ',')
                    continue;
                if (next == '}')
                    return result;
                throw new FormatException($"Unexpected '{next}' at offset {_index} in JSON object.");
            }
        }

        private List<object?> ReadArray()
        {
            var result = new List<object?>();
            Expect('[');
            SkipWhitespace();
            if (Peek() == ']')
            {
                _index++;
                return result;
            }

            while (true)
            {
                result.Add(ReadValue());
                SkipWhitespace();
                var next = Read();
                if (next == ',')
                    continue;
                if (next == ']')
                    return result;
                throw new FormatException($"Unexpected '{next}' at offset {_index} in JSON array.");
            }
        }

        private string ReadString()
        {
            Expect('"');
            var builder = new StringBuilder();
            while (true)
            {
                var character = Read();
                if (character == '"')
                    return builder.ToString();
                if (character != '\\')
                {
                    builder.Append(character);
                    continue;
                }

                var escape = Read();
                builder.Append(escape switch
                {
                    '"' => '"',
                    '\\' => '\\',
                    '/' => '/',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'b' => '\b',
                    'f' => '\f',
                    'u' => ReadUnicodeEscape(),
                    _ => throw new FormatException(
                        $"Unsupported escape '\\{escape}' at offset {_index} in JSON string.")
                });
            }
        }

        private char ReadUnicodeEscape()
        {
            if (_index + 4 > text.Length)
                throw new FormatException("Truncated \\u escape in JSON string.");
            var code = ushort.Parse(
                text.AsSpan(_index, 4),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture);
            _index += 4;
            return (char)code;
        }

        private bool ReadBoolean()
        {
            if (text.AsSpan(_index).StartsWith("true", StringComparison.Ordinal))
            {
                _index += 4;
                return true;
            }
            if (text.AsSpan(_index).StartsWith("false", StringComparison.Ordinal))
            {
                _index += 5;
                return false;
            }
            throw new FormatException($"Invalid literal at offset {_index} in JSON.");
        }

        private object? ReadNull()
        {
            if (!text.AsSpan(_index).StartsWith("null", StringComparison.Ordinal))
                throw new FormatException($"Invalid literal at offset {_index} in JSON.");
            _index += 4;
            return null;
        }

        private double ReadNumber()
        {
            var start = _index;
            while (_index < text.Length &&
                   (char.IsAsciiDigit(text[_index]) || text[_index] is '-' or '+' or '.' or 'e' or 'E'))
            {
                _index++;
            }
            if (start == _index)
                throw new FormatException($"Expected a number at offset {start} in JSON.");
            return double.Parse(text.AsSpan(start, _index - start), CultureInfo.InvariantCulture);
        }

        private void SkipWhitespace()
        {
            while (_index < text.Length && char.IsWhiteSpace(text[_index]))
                _index++;
        }

        private char Peek() => _index < text.Length ? text[_index] : '\0';

        private char Read()
        {
            if (_index >= text.Length)
                throw new FormatException("Unexpected end of JSON.");
            return text[_index++];
        }

        private void Expect(char expected)
        {
            var actual = Read();
            if (actual != expected)
                throw new FormatException(
                    $"Expected '{expected}' but found '{actual}' at offset {_index - 1} in JSON.");
        }
    }
}

using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using StArray.ModManager.Manager;

namespace Xphorror.PcModCompat;

internal static class PcCompatDeepDebug
{
    internal const string Prefix = "[DEBUG-jpkv-deep-v1]";
    private const string LogTag = "PcCompatDeepDebug";
    private const int MaxFieldCount = 72;
    private const int MaxArrayItems = 16;
    private const int MaxTextLength = 192;
    internal const int MaxFieldSnapshotLength = 8192;
    private const string FieldSnapshotTruncated = "; <snapshot-truncated>";
    private static readonly ConcurrentDictionary<string, long> Counters =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> LastStates =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> LastPeriodicWrites =
        new(StringComparer.Ordinal);

    [Conditional("PCCOMPAT_DEEP_DEBUG")]
    public static void Write(string area, string message)
    {
        try
        {
            Logger.Info(
                LogTag,
                $"{Prefix} area={area} {message} " +
                $"tid={Environment.CurrentManagedThreadId} unityMain={PcCompatUnityMainExecutionContext.IsActive}");
        }
        catch
        {
            // Diagnostics must never alter compatibility behavior.
        }
    }

    [Conditional("PCCOMPAT_DEEP_DEBUG")]
    public static void WriteSampled(
        string area,
        string key,
        Func<long, string> messageFactory,
        int first = 8,
        long periodic = 4096)
    {
        var count = Counters.AddOrUpdate(area + "\0" + key, 1, static (_, value) => value + 1);
        if (count > first && !IsPowerOfTwo(count) && (periodic <= 0 || count % periodic != 0))
            return;
        Write(area, messageFactory(count));
    }

    public static bool ShouldSample(
        string area,
        string key,
        out long count,
        int first = 8,
        long periodic = 4096)
    {
#if PCCOMPAT_DEEP_DEBUG
        try
        {
            count = Counters.AddOrUpdate(
                area + "\0" + key,
                1,
                static (_, value) => value + 1);
            return count <= first || IsPowerOfTwo(count) || periodic > 0 && count % periodic == 0;
        }
        catch
        {
            count = 0;
            return false;
        }
#else
        count = 0;
        return false;
#endif
    }

    [Conditional("PCCOMPAT_DEEP_DEBUG")]
    public static void WritePeriodic(
        string area,
        string key,
        TimeSpan interval,
        Func<string> messageFactory)
    {
        try
        {
            var intervalTicks = Math.Max(
                1L,
                (long)Math.Ceiling(interval.TotalSeconds * System.Diagnostics.Stopwatch.Frequency));
            var stateKey = area + "\0" + key;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            while (true)
            {
                if (LastPeriodicWrites.TryGetValue(stateKey, out var previous))
                {
                    if (now - previous < intervalTicks)
                        return;
                    if (!LastPeriodicWrites.TryUpdate(stateKey, now, previous))
                        continue;
                }
                else if (!LastPeriodicWrites.TryAdd(stateKey, now))
                {
                    continue;
                }
                Write(area, messageFactory());
                return;
            }
        }
        catch
        {
            // Diagnostics must never alter compatibility behavior.
        }
    }

    [Conditional("PCCOMPAT_DEEP_DEBUG")]
    public static void WriteState(string area, string key, string state, string message)
    {
        var stateKey = area + "\0" + key;
        if (LastStates.TryGetValue(stateKey, out var previous) &&
            string.Equals(previous, state, StringComparison.Ordinal))
        {
            return;
        }
        LastStates[stateKey] = state;
        Write(area, message);
    }

    public static string ExecutionIdentity()
    {
        var execution = PcCompatManagedExecutionContext.Current;
        return execution == null
            ? "mod=<none> generation=0 phase=<none>"
            : $"mod={Sanitize(execution.ModId)} generation={execution.ResourceSessionGeneration} " +
              $"phase={execution.Phase}";
    }

    public static string DescribeObject(object? value)
    {
        if (value == null)
            return "null";
        try
        {
            var type = value.GetType();
            var builder = new StringBuilder(128);
            builder.Append(type.FullName ?? type.Name)
                .Append("#0x")
                .Append(RuntimeHelpers.GetHashCode(value).ToString("X"));
            if (TryReadPointer(value, type, out var pointer, out var pointerError))
                builder.Append(" ptr=0x").Append(pointer.ToString("X"));
            else if (pointerError != null)
                builder.Append(" ptrError=").Append(Sanitize(pointerError));
            if (TryReadUnityTruthy(value, type, out var truthy, out var truthyError))
                builder.Append(" unityTruthy=").Append(truthy);
            else if (truthyError != null)
                builder.Append(" unityTruthyError=").Append(Sanitize(truthyError));
            return builder.ToString();
        }
        catch (Exception exception)
        {
            return $"<describe-failed:{exception.GetType().Name}:{Sanitize(exception.Message)}>";
        }
    }

    public static string DescribeFields(object value, bool includeStatic = true)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            var rootType = value.GetType();
            var fields = EnumerateDiagnosticFields(rootType, includeStatic)
                .Take(MaxFieldCount + 1)
                .ToArray();
            var truncated = fields.Length > MaxFieldCount;
            var builder = new StringBuilder(1024);
            foreach (var field in fields.Take(MaxFieldCount))
            {
                if (builder.Length != 0)
                    builder.Append("; ");
                builder.Append(field.IsStatic ? "static " : string.Empty)
                    .Append(field.DeclaringType?.Name)
                    .Append('.')
                    .Append(field.Name)
                    .Append('=');
                try
                {
                    builder.Append(DescribeValue(
                        field.GetValue(field.IsStatic ? null : value),
                        rootType.Assembly,
                        depth: 2));
                }
                catch (Exception exception)
                {
                    builder.Append("<read-failed:")
                        .Append(exception.GetType().Name)
                        .Append(':')
                        .Append(Sanitize(exception.Message))
                        .Append('>');
                }
                if (builder.Length > MaxFieldSnapshotLength)
                    return TruncateFieldSnapshot(builder);
            }
            if (truncated)
                builder.Append("; <fields-truncated>");
            return builder.Length > MaxFieldSnapshotLength
                ? TruncateFieldSnapshot(builder)
                : builder.ToString();
        }
        catch (Exception exception)
        {
            return $"<field-snapshot-failed:{exception.GetType().Name}:{Sanitize(exception.Message)}>";
        }
    }

    private static string TruncateFieldSnapshot(StringBuilder builder)
    {
        builder.Length = Math.Max(0, MaxFieldSnapshotLength - FieldSnapshotTruncated.Length);
        builder.Append(FieldSnapshotTruncated);
        return builder.ToString();
    }

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
        return normalized.Length <= MaxTextLength
            ? normalized
            : normalized[..MaxTextLength] + "...";
    }

    private static IEnumerable<FieldInfo> EnumerateDiagnosticFields(Type type, bool includeStatic)
    {
        for (var current = type;
             current != null && current != typeof(object) && !IsProxyNamespace(current.Namespace);
             current = current.BaseType)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.DeclaredOnly;
            if (includeStatic)
                flags |= BindingFlags.Static;
            foreach (var field in current.GetFields(flags).OrderBy(field => field.MetadataToken))
            {
                if (!includeStatic && field.IsStatic)
                    continue;
                yield return field;
            }
        }
    }

    private static string DescribeValue(object? value, Assembly rootAssembly, int depth)
    {
        if (value == null)
            return "null";
        var type = value.GetType();
        if (value is string text)
            return '"' + Sanitize(text) + '"';
        if (type.IsEnum)
            return $"{value}({Convert.ToInt64(value)})";
        if (value is bool boolean)
            return boolean ? "true" : "false";
        if (value is char character)
            return $"'{character}'({(int)character})";
        if (type.IsPrimitive || value is decimal || value is DateTime || value is TimeSpan)
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        if (value is Array array)
        {
            var items = new List<string>(Math.Min(array.Length, MaxArrayItems));
            for (var index = 0; index < Math.Min(array.Length, MaxArrayItems); ++index)
                items.Add(DescribeValue(array.GetValue(index), rootAssembly, 0));
            return $"{type.GetElementType()?.FullName ?? "?"}[{array.Length}]" +
                   $"{{{string.Join(',', items)}{(array.Length > MaxArrayItems ? ",..." : string.Empty)}}}";
        }
        if (value is ICollection collection)
            return $"{DescribeObject(value)} count={collection.Count}";
        if (depth <= 0 || !ReferenceEquals(type.Assembly, rootAssembly) || IsProxyNamespace(type.Namespace))
            return DescribeObject(value);

        var nested = type.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(field => !field.IsStatic)
            .Take(16)
            .Select(field =>
            {
                try
                {
                    return field.Name + "=" + DescribeValue(field.GetValue(value), rootAssembly, depth - 1);
                }
                catch (Exception exception)
                {
                    return field.Name + "=<" + exception.GetType().Name + ">";
                }
            });
        return $"{DescribeObject(value)}{{{string.Join(',', nested)}}}";
    }

    private static bool TryReadPointer(
        object value,
        Type type,
        out ulong pointer,
        out string? error)
    {
        pointer = 0;
        error = null;
        try
        {
            var property = FindProperty(type, "Pointer");
            var raw = property?.GetValue(value);
            if (raw == null)
                return false;
            pointer = raw switch
            {
                IntPtr native => unchecked((ulong)native.ToInt64()),
                UIntPtr native => native.ToUInt64(),
                long signed => unchecked((ulong)signed),
                ulong unsigned => unsigned,
                _ => Convert.ToUInt64(raw)
            };
            return true;
        }
        catch (Exception exception)
        {
            error = Unwrap(exception).GetType().Name + ":" + Unwrap(exception).Message;
            return false;
        }
    }

    private static bool TryReadUnityTruthy(
        object value,
        Type type,
        out bool truthy,
        out string? error)
    {
        truthy = false;
        error = null;
        var unityObject = FindBaseType(type, "UnityEngine.Object");
        if (unityObject == null)
            return false;
        try
        {
            var implicitOperator = unityObject.GetMethod(
                "op_Implicit",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                [unityObject],
                modifiers: null);
            if (implicitOperator != null)
            {
                truthy = Convert.ToBoolean(implicitOperator.Invoke(null, [value]));
                return true;
            }
            var inequality = unityObject.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .SingleOrDefault(method => method.Name == "op_Inequality" &&
                                           method.GetParameters().Length == 2);
            if (inequality == null)
                return false;
            truthy = Convert.ToBoolean(inequality.Invoke(null, [value, null]));
            return true;
        }
        catch (Exception exception)
        {
            var unwrapped = Unwrap(exception);
            error = unwrapped.GetType().Name + ":" + unwrapped.Message;
            return false;
        }
    }

    private static PropertyInfo? FindProperty(Type type, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var property = current.GetProperty(
                name,
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (property != null)
                return property;
        }
        return null;
    }

    private static Type? FindBaseType(Type type, string fullName)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (string.Equals(current.FullName, fullName, StringComparison.Ordinal))
                return current;
        }
        return null;
    }

    private static bool IsProxyNamespace(string? value)
        => value != null &&
           (value.StartsWith("UnityEngine", StringComparison.Ordinal) ||
            value.StartsWith("TMPro", StringComparison.Ordinal) ||
            value.StartsWith("Il2Cpp", StringComparison.Ordinal));

    private static bool IsPowerOfTwo(long value)
        => value > 0 && (value & (value - 1)) == 0;

    private static Exception Unwrap(Exception exception)
        => exception is TargetInvocationException { InnerException: not null } target
            ? target.InnerException!
            : exception;
}

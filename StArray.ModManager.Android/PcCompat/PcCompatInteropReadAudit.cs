using System.Reflection;
using StArray.ModManager.Manager;

namespace StArray.ModManager.Android.PcCompat;

internal static class PcCompatInteropReadAudit
{
    private const string LogTag = "PcCompatInteropAudit";
    private static readonly bool Enabled = EnvEnabled("STARRAY_PCMOD_INTEROP_AUDIT", false);
    private static int s_state;
    private static int s_sampleCounter;
    private static nint s_trackerPointer;
    private static object? s_trackerProxy;
    private static ConstructorInfo? s_trackerConstructor;
    private static PropertyInfo? s_percentAcc;
    private static PropertyInfo? s_percentXAcc;

    public static void CompareMarginTracker(nint tracker, float nativeAcc, float nativeXAcc)
    {
        if (!Enabled || tracker == nint.Zero || Volatile.Read(ref s_state) == 3)
            return;
        if ((Interlocked.Increment(ref s_sampleCounter) & 127) != 0)
            return;

        try
        {
            EnsureInitialized();
            if (s_trackerProxy is null || s_trackerPointer != tracker)
            {
                s_trackerProxy = s_trackerConstructor!.Invoke([tracker]);
                s_trackerPointer = tracker;
            }

            var proxyAcc = (float)s_percentAcc!.GetValue(s_trackerProxy)!;
            var proxyXAcc = (float)s_percentXAcc!.GetValue(s_trackerProxy)!;
            if (Math.Abs(proxyAcc - nativeAcc) > 0.0001f ||
                Math.Abs(proxyXAcc - nativeXAcc) > 0.0001f)
            {
                Logger.Warn(
                    LogTag,
                    $"read mismatch ptr=0x{tracker:X} " +
                    $"proxyAcc={proxyAcc:R} nativeAcc={nativeAcc:R} " +
                    $"proxyXAcc={proxyXAcc:R} nativeXAcc={nativeXAcc:R}");
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref s_state, 3);
            Logger.Error(LogTag, $"proxy read audit disabled after failure: {exception}");
        }
    }

    private static void EnsureInitialized()
    {
        if (Volatile.Read(ref s_state) == 2)
            return;

        if (!PcCompatIl2CppInteropBootstrap.TryGetProxyType(
                "Assembly-CSharp",
                "scrMarginTracker",
                out var trackerType))
            throw new TypeLoadException("Generated scrMarginTracker proxy is unavailable.");

        s_trackerConstructor = trackerType.GetConstructor([typeof(IntPtr)])
            ?? throw new MissingMethodException(trackerType.FullName, ".ctor(IntPtr)");
        s_percentAcc = RequireReadOnlyProperty(trackerType, "percentAcc");
        s_percentXAcc = RequireReadOnlyProperty(trackerType, "percentXAcc");
        Volatile.Write(ref s_state, 2);
        Logger.Info(LogTag, "generated scrMarginTracker read audit enabled sampleRate=1/128");
    }

    private static PropertyInfo RequireReadOnlyProperty(Type type, string name)
    {
        var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(type.FullName, name);
        if (property.GetMethod is null || property.SetMethod is not null)
            throw new InvalidOperationException($"Generated proxy property is not read-only: {type.FullName}.{name}");
        return property;
    }

    private static bool EnvEnabled(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}

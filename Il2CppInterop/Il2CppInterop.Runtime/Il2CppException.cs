using System;
using System.Reflection;
using System.Text;

namespace Il2CppInterop.Runtime;

public class Il2CppException : Exception
{
    [ThreadStatic] private static byte[] ourMessageBytes;

    public static Func<IntPtr, string> ParseMessageHook;

    public Il2CppException(IntPtr exception) : base(BuildMessage(exception))
    {
    }

    private static unsafe string BuildMessage(IntPtr exception)
    {
        if (ParseMessageHook != null) return ParseMessageHook(exception);
        ourMessageBytes ??= new byte[65536];
        fixed (byte* message = ourMessageBytes)
        {
            IL2CPP.il2cpp_format_exception(exception, message, ourMessageBytes.Length);
        }

        var builtMessage = Encoding.UTF8.GetString(ourMessageBytes, 0, Array.IndexOf(ourMessageBytes, (byte)0));
        return builtMessage + "\n" +
               "--- BEGIN IL2CPP STACK TRACE ---\n" +
               $"{BuildIl2CppStackTrace(exception)}\n" +
               "--- END IL2CPP STACK TRACE ---\n";
    }

    private static string BuildIl2CppStackTrace(IntPtr exception)
    {
        // Trimmed generated proxy closures can omit Il2CppSystem.Exception.ToString(bool, bool).
        // Bind it through reflection so a missing member cannot fail JIT compilation of this
        // error path itself; a static call would throw MissingMethodException at JIT time and
        // mask the native exception we are trying to report.
        try
        {
            var toString = typeof(Il2CppSystem.Exception).GetMethod(
                "ToString",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(bool), typeof(bool) },
                modifiers: null);
            if (toString == null)
            {
                return "(unavailable: Il2CppSystem.Exception.ToString(bool, bool) " +
                       "is not part of the generated proxy surface)";
            }

            var il2cppException = new Il2CppSystem.Exception(exception);
            return toString.Invoke(il2cppException, new object[] { false, true }) as string
                   ?? "(empty)";
        }
        catch (Exception reflectionFailure)
        {
            return "(unavailable: " + reflectionFailure.Message + ")";
        }
    }

    public static void RaiseExceptionIfNecessary(IntPtr returnedException)
    {
        if (returnedException == IntPtr.Zero) return;
        throw new Il2CppException(returnedException);
    }
}

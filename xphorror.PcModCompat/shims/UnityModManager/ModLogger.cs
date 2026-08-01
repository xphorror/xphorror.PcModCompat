namespace UnityModManagerNet;

public partial class UnityModManager
{
    public partial class ModEntry
    {
        public class ModLogger
        {
            private readonly string _modId;

            public ModLogger(string modId)
            {
                _modId = string.IsNullOrWhiteSpace(modId) ? "unknown" : modId;
            }

            public void Log(string message) => Write("INFO", message);
            public void Warning(string message) => Write("WARN", message);
            public void Error(string message) => Write("ERROR", message);
            public void Critical(string message) => Write("CRITICAL", message);
            public void NativeLog(string message) => Write("NATIVE", message);

            public void LogException(string context, Exception exception)
                => Error($"{context}: {exception}");

            public void LogException(Exception exception)
                => Error(exception.ToString());

            private void Write(string level, string message)
                => Console.WriteLine($"[PcModCompat][UMM][{level}][{_modId}] {message}");
        }
    }

    public static class Logger
    {
        public static void Log(string message) => Write("INFO", "Manager", message);
        public static void Log(string message, string prefix) => Write("INFO", prefix, message);
        public static void Warning(string message) => Write("WARN", "Manager", message);
        public static void Warning(string message, string prefix) => Write("WARN", prefix, message);
        public static void Error(string message) => Write("ERROR", "Manager", message);
        public static void Error(string message, string prefix) => Write("ERROR", prefix, message);
        public static void Critical(string message) => Write("CRITICAL", "Manager", message);
        public static void Critical(string message, string prefix) => Write("CRITICAL", prefix, message);
        public static void NativeLog(string message) => Write("NATIVE", "Manager", message);
        public static void NativeLog(string message, string prefix) => Write("NATIVE", prefix, message);
        public static void LogException(Exception exception) => Error(exception.ToString());
        public static void LogException(string? context, Exception exception, string? prefix = null)
            => Write("ERROR", prefix ?? "Manager", $"{context ?? "Exception"}: {exception}");
        public static void Clear() { }
        public static void WriteBuffers() { }

        private static void Write(string level, string prefix, string message)
            => Console.WriteLine($"[PcModCompat][UMM][{level}][{prefix}] {message}");
    }

    public class ModLogger
    {
        private readonly string _modId;

        public ModLogger(string modId)
        {
            _modId = string.IsNullOrWhiteSpace(modId) ? "unknown" : modId;
        }

        public void Log(string message) => Write("INFO", message);
        public void Warning(string message) => Write("WARN", message);
        public void Error(string message) => Write("ERROR", message);
        public void Critical(string message) => Write("CRITICAL", message);
        public void NativeLog(string message) => Write("NATIVE", message);

        public void LogException(string context, Exception exception)
            => Error($"{context}: {exception}");

        public void LogException(Exception exception)
            => Error(exception.ToString());

        private void Write(string level, string message)
            => Console.WriteLine($"[PcModCompat][UMM][{level}][{_modId}] {message}");
    }
}

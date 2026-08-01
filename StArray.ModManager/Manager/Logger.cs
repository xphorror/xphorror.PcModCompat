using System.Runtime.CompilerServices;

namespace StArray.ModManager.Manager;

/// <summary>
/// 统一日志系统 —— 纯事件广播，不依赖任何平台。由 Managed 入口订阅以桥接 Android logcat。
/// </summary>
public static class Logger
{
    /// <summary>日志级别</summary>
    public enum Level { Debug, Info, Warn, Error }

    /// <summary>日志广播事件（Level, Tag, Message）</summary>
    public static event Action<Level, string, string>? OnLog;

    /// <summary>调试日志，msg 为空时输出调用位置</summary>
    public static void Debug(string tag, string? msg = null,
        [CallerFilePath] string? path = null, [CallerLineNumber] int line = 0)
        => Emit(Level.Debug, tag, msg ?? $"{path}:{line}");

    /// <summary>信息日志，msg 为空时输出调用位置</summary>
    public static void Info(string tag, string? msg = null,
        [CallerFilePath] string? path = null, [CallerLineNumber] int line = 0)
        => Emit(Level.Info, tag, msg ?? $"{path}:{line}");

    /// <summary>警告日志，msg 为空时输出调用位置</summary>
    public static void Warn(string tag, string? msg = null,
        [CallerFilePath] string? path = null, [CallerLineNumber] int line = 0)
        => Emit(Level.Warn, tag, msg ?? $"{path}:{line}");

    /// <summary>错误日志，msg 为空时输出调用位置</summary>
    public static void Error(string tag, string? msg = null,
        [CallerFilePath] string? path = null, [CallerLineNumber] int line = 0)
        => Emit(Level.Error, tag, msg ?? $"{path}:{line}");

    private static void Emit(Level level, string tag, string msg)
    {
        var handlers = OnLog;
        if (handlers == null)
            return;

        foreach (Action<Level, string, string> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(level, tag, msg);
            }
            catch
            {
                // Logging subscribers are diagnostic only; never let them break hooks.
            }
        }
    }
}

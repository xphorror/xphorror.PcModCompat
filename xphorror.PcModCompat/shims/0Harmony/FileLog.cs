using System.Text;
using System.Reflection;
using System.Reflection.Emit;

namespace HarmonyLib;

// ABI mirror of Harmony 2.4 HarmonyLib/Tools/FileLog.cs.
//
// Upstream writes next to the desktop by default. On Android that path does not exist, so output
// goes to the same stdout channel the rest of PcCompat logs to, and only follows logPath when the
// MOD sets a writable one explicitly.
public static class FileLog
{
    private static readonly object FileLock = new();

    private static readonly List<string> Buffer = [];

    public static StreamWriter? LogWriter { get; set; }

    public static string? logPath;

    public static char indentChar = ' ';

    public static int indentLevel;

    private static int GetIndentation() => indentLevel;

    private static string CodePos(int offset) => $"IL_{offset:X4}: ";

    public static void ChangeIndent(int delta) => indentLevel = Math.Max(0, indentLevel + delta);

    public static void LogBuffered(string str)
    {
        lock (FileLock)
            Buffer.Add(IndentedLine(str));
    }

    public static void LogBuffered(List<string> strings)
    {
        lock (FileLock)
            Buffer.AddRange(strings);
    }

    public static List<string> GetBuffer(bool clear)
    {
        lock (FileLock)
        {
            var result = new List<string>(Buffer);
            if (clear)
                Buffer.Clear();
            return result;
        }
    }

    public static void SetBuffer(List<string> buffer)
    {
        lock (FileLock)
        {
            Buffer.Clear();
            Buffer.AddRange(buffer);
        }
    }

    public static void FlushBuffer()
    {
        lock (FileLock)
        {
            foreach (var line in Buffer)
                Write(line);
            Buffer.Clear();
        }
    }

    public static void Log(string str)
    {
        lock (FileLock)
            Write(IndentedLine(str));
    }

    public static void LogILComment(int codePos, string comment)
        => LogBuffered($"{CodePos(codePos)}// {comment}");

    public static void LogIL(int codePos, OpCode opcode)
        => LogBuffered($"{CodePos(codePos)}{opcode}");

    public static void LogIL(int codePos, OpCode opcode, object? arg)
    {
        var operand = FormatOperand(arg);
        var opcodeName = opcode.ToString() ?? "";
        if (opcode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch)
            opcodeName += " =>";
        opcodeName = opcodeName.PadRight(10);
        LogBuffered($"{CodePos(codePos)}{opcodeName}{(operand.Length == 0 ? "" : " ")}{operand}");
    }

    public static void LogIL(int codePos, Label label)
        => LogBuffered(CodePos(codePos) + FormatOperand(label));

    public static void LogILBlockBegin(int codePos, ExceptionBlock block)
    {
        switch (block.blockType)
        {
            case ExceptionBlockType.BeginExceptionBlock:
                LogBuffered(".try");
                LogBuffered("{");
                ChangeIndent(1);
                break;
            case ExceptionBlockType.BeginCatchBlock:
                LogLeave(codePos);
                ChangeIndent(-1);
                LogBuffered("} // end try");
                LogBuffered($".catch {block.catchType}");
                LogBuffered("{");
                ChangeIndent(1);
                break;
            case ExceptionBlockType.BeginExceptFilterBlock:
                BeginHandler(codePos, ".filter");
                break;
            case ExceptionBlockType.BeginFaultBlock:
                BeginHandler(codePos, ".fault");
                break;
            case ExceptionBlockType.BeginFinallyBlock:
                BeginHandler(codePos, ".finally");
                break;
        }
    }

    public static void LogILBlockEnd(int codePos, ExceptionBlock block)
    {
        if (block.blockType != ExceptionBlockType.EndExceptionBlock)
            return;

        LogLeave(codePos);
        ChangeIndent(-1);
        LogBuffered("} // end handler");
    }

    public static bool TryLog(string str)
    {
        try
        {
            Log(str);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Debug(string str)
    {
        if (Harmony.DEBUG)
            Log($"DEBUG: {str}");
    }

    public static void Reset()
    {
        lock (FileLock)
        {
            Buffer.Clear();
            if (string.IsNullOrEmpty(logPath) is false && File.Exists(logPath))
                File.Delete(logPath);
        }
    }

    // Upstream walks a byte* here; Marshal.ReadByte gives the same bytes without making the whole
    // shim assembly unsafe.
    public static void LogBytes(long ptr, int len)
    {
        var basePointer = new IntPtr(ptr);
        var s = "";
        for (var i = 1; i <= len; i++)
        {
            if (s.Length == 0)
                s = "#  ";
            s += $"{System.Runtime.InteropServices.Marshal.ReadByte(basePointer, i - 1):X2} ";
            if (i > 1 || len == 1)
            {
                if (i % 8 == 0 || i == len)
                {
                    Log(s);
                    s = "";
                }
                else if (i % 4 == 0)
                {
                    s += " ";
                }
            }
        }
    }

    private static string IndentedLine(string str) => new string(indentChar, GetIndentation()) + str;

    private static void Write(string line)
    {
        if (LogWriter is not null)
        {
            LogWriter.WriteLine(line);
            return;
        }

        if (string.IsNullOrEmpty(logPath))
        {
            Console.WriteLine($"[PcModCompat][Harmony][FileLog] {line}");
            return;
        }

        try
        {
            using var stream = new StreamWriter(logPath, append: true, Encoding.UTF8);
            stream.WriteLine(line);
        }
        catch (Exception exception)
        {
            // A MOD-provided path can easily be unwritable on Android; losing the line silently
            // would hide the very failure the MOD was trying to log.
            HarmonyRegistry.Report(
                "HarmonyFileLogUnwritable",
                "FileLog.logPath",
                $"'{logPath}' is not writable ({exception.GetType().Name}); output goes to stdout instead.");
            Console.WriteLine($"[PcModCompat][Harmony][FileLog] {line}");
        }
    }

    private static void BeginHandler(int codePos, string directive)
    {
        LogLeave(codePos);
        ChangeIndent(-1);
        LogBuffered("} // end try");
        LogBuffered(directive);
        LogBuffered("{");
        ChangeIndent(1);
    }

    private static void LogLeave(int codePos)
        => LogIL(codePos, OpCodes.Leave, "(autogenerated)");

    private static string FormatOperand(object? operand)
        => operand switch
        {
            null => "",
            string value => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
            char value => $"'{value}'",
            Type type => type.FullName ?? type.Name,
            MemberInfo member => member.ToString() ?? member.Name,
            Label label => label.ToString() ?? "",
            Label[] labels => string.Join(", ", labels.Select(label => label.ToString() ?? "")),
            _ => operand.ToString() ?? ""
        };
}

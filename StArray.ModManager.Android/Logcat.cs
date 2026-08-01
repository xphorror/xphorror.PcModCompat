namespace StArray.ModManager.Android;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public class LogcatCapture
{
    private readonly object _sync = new();
    private Process? _process;
    private readonly string _outputFilePath;
    private readonly string _logcatArguments;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 初始化 Logcat 捕获器
    /// </summary>
    /// <param name="outputFilePath">输出文件路径</param>
    /// <param name="logcatArguments">logcat 参数，默认为 "-v time"</param>
    public LogcatCapture(string outputFilePath, string logcatArguments = "-v time")
    {
        _outputFilePath = outputFilePath;
        _logcatArguments = logcatArguments;
    }

    /// <summary>
    /// 启动 logcat 并异步写入文件
    /// </summary>
    public async Task StartAsync()
    {
        Stop();
        var cts = new CancellationTokenSource();
        lock (_sync)
            _cts = cts;

        try
        {
            await RunLogcatAsync(cts.Token);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_cts, cts))
                    _cts = null;
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// 停止 logcat 捕获
    /// </summary>
    public void Stop()
    {
        Process? process;
        CancellationTokenSource? cts;
        lock (_sync)
        {
            process = _process;
            cts = _cts;
            _process = null;
            _cts = null;
        }

        cts?.Cancel();
        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private async Task RunLogcatAsync(CancellationToken token)
    {
        var dir = Path.GetDirectoryName(_outputFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var startInfo = new ProcessStartInfo
        {
            FileName = "logcat",
            Arguments = _logcatArguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };
        lock (_sync)
            _process = process;

        process.Start();
        await using var writer = new StreamWriter(
            _outputFilePath,
            false,
            System.Text.Encoding.UTF8)
        {
            AutoFlush = true,
        };
        using var writerLock = new SemaphoreSlim(1, 1);
        var stdoutTask = ReadStreamAsync(process.StandardOutput, writer, writerLock, token);
        var stderrTask = ReadStreamAsync(process.StandardError, writer, writerLock, token);

        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }

            lock (_sync)
            {
                if (ReferenceEquals(_process, process))
                    _process = null;
            }
        }
    }

    private static async Task ReadStreamAsync(
        StreamReader reader,
        StreamWriter writer,
        SemaphoreSlim writerLock,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(token);
            if (line == null)
                break;

            await writerLock.WaitAsync(token);
            try
            {
                await writer.WriteLineAsync(line);
            }
            finally
            {
                writerLock.Release();
            }
        }
    }
}

using System.Runtime.CompilerServices;
using JALib.Core;

namespace JALib.Tools;

public static class JATask
{
    public static Task Run(JAMod mod, Action action)
        => Task.Run(() => Invoke(mod, action));

    public static Task Run(JAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Task.Run(action.Invoke);
    }

    public static Task Run(JAMod mod, Action action, CancellationToken cancellationToken)
        => Task.Run(() => Invoke(mod, action), cancellationToken);

    public static Task Run(JAction action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Task.Run(action.Invoke, cancellationToken);
    }

    public static Task Run(JAMod mod, Func<Task> action)
        => Task.Run(() => InvokeAsync(mod, action));

    public static Task Run(
        JAMod mod,
        Func<Task> action,
        CancellationToken cancellationToken)
        => Task.Run(() => InvokeAsync(mod, action), cancellationToken);

    public static Task<TResult?> Run<TResult>(JAMod mod, Func<TResult> action)
        => Task.Run(() => Invoke(mod, action));

    public static Task<TResult?> Run<TResult>(
        JAMod mod,
        Func<TResult> action,
        CancellationToken cancellationToken)
        => Task.Run(() => Invoke(mod, action), cancellationToken);

    public static Task<TResult?> Run<TResult>(JAMod mod, Func<Task<TResult>> action)
        => Task.Run(() => InvokeAsync(mod, action));

    public static Task<TResult?> Run<TResult>(
        JAMod mod,
        Func<Task<TResult>> action,
        CancellationToken cancellationToken)
        => Task.Run(() => InvokeAsync(mod, action), cancellationToken);

    public static void CatchException(this Task task, JAMod mod)
        => Observe(task, mod);

    public static void CatchException<TResult>(this Task<TResult> task, JAMod mod)
        => Observe(task, mod);

    public static void CatchExceptionSync(this Task task, JAMod mod)
        => Observe(task, mod);

    public static void CatchExceptionSync<TResult>(this Task<TResult> task, JAMod mod)
        => Observe(task, mod);

    public static void OnCompleted(
        this Task task,
        JAMod mod,
        Action<Task> action,
        CompleteFlag flag = CompleteFlag.All)
        => RegisterCompletion(task, mod, () => Execute(task, mod, action, flag));

    public static void OnCompleted(this Task task, Action<Task> action)
    {
        var owner = MainThread.CurrentOwner;
        RegisterCompletion(task, owner, () => action(task));
    }

    public static void OnCompleted<TResult>(
        this Task<TResult> task,
        JAMod mod,
        Action<Task<TResult>> action,
        CompleteFlag flag = CompleteFlag.All)
        => RegisterCompletion(task, mod, () => Execute(task, mod, action, flag));

    public static void OnCompleted<TResult>(
        this Task<TResult> task,
        Action<Task<TResult>> action)
    {
        var owner = MainThread.CurrentOwner;
        RegisterCompletion(task, owner, () => action(task));
    }

    public static void OnCompletedAsync(
        this Task task,
        JAMod mod,
        Action<Task> action,
        CompleteFlag flag = CompleteFlag.All)
        => RegisterCompletion(task, mod, () => Execute(task, mod, action, flag), forceQueue: true);

    public static void OnCompletedAsync(this Task task, Action<Task> action)
    {
        var owner = MainThread.CurrentOwner;
        RegisterCompletion(task, owner, () => action(task), forceQueue: true);
    }

    public static void OnCompletedAsync<TResult>(
        this Task<TResult> task,
        JAMod mod,
        Action<Task<TResult>> action,
        CompleteFlag flag = CompleteFlag.All)
        => RegisterCompletion(task, mod, () => Execute(task, mod, action, flag), forceQueue: true);

    public static void OnCompletedAsync<TResult>(
        this Task<TResult> task,
        Action<Task<TResult>> action)
    {
        var owner = MainThread.CurrentOwner;
        RegisterCompletion(task, owner, () => action(task), forceQueue: true);
    }

    public static void OnCompleted(this Task task, JAMod mod, Action action)
        => RegisterCompletion(task, mod, () =>
        {
            ReportTaskFailure(task, mod);
            Invoke(mod, action);
        });

    public static void OnCompleted(this Task task, Action action)
    {
        var owner = MainThread.CurrentOwner;
        RegisterCompletion(task, owner, action);
    }

    public static void OnCompletedAsync(this Task task, JAMod mod, Action action)
        => RegisterCompletion(task, mod, () =>
        {
            ReportTaskFailure(task, mod);
            Invoke(mod, action);
        }, forceQueue: true);

    public static void OnCompletedAsync(this Task task, Action action)
    {
        var owner = MainThread.CurrentOwner;
        RegisterCompletion(task, owner, action, forceQueue: true);
    }

    public static void OnCompleted(this YieldAwaitable awaitable, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var owner = MainThread.CurrentOwner;
        awaitable.GetAwaiter().OnCompleted(() => MainThread.RunCaptured(owner, action));
    }

    private static void RegisterCompletion(
        Task task,
        JAMod? owner,
        Action action,
        bool forceQueue = false)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(action);

        void Schedule()
        {
            if (forceQueue)
                MainThread.ForceQueueCaptured(owner, action);
            else
                MainThread.RunCaptured(owner, action);
        }

        if (task.IsCompleted)
        {
            Schedule();
            return;
        }
        task.GetAwaiter().UnsafeOnCompleted(Schedule);
    }

    private static void Execute(
        Task task,
        JAMod mod,
        Action<Task> action,
        CompleteFlag flag)
    {
        var faulted = task.IsFaulted;
        if (faulted && (flag & CompleteFlag.TryCatchTask) != 0)
            ReportTaskFailure(task, mod);
        if (faulted && (flag & CompleteFlag.CompleteOnly) != 0)
            return;
        if ((flag & CompleteFlag.TryCatchAction) != 0)
            Invoke(mod, () => action(task));
        else
            action(task);
    }

    private static void Execute<TResult>(
        Task<TResult> task,
        JAMod mod,
        Action<Task<TResult>> action,
        CompleteFlag flag)
    {
        var faulted = task.IsFaulted;
        if (faulted && (flag & CompleteFlag.TryCatchTask) != 0)
            ReportTaskFailure(task, mod);
        if (faulted && (flag & CompleteFlag.CompleteOnly) != 0)
            return;
        if ((flag & CompleteFlag.TryCatchAction) != 0)
            Invoke(mod, () => action(task));
        else
            action(task);
    }

    private static void Observe(Task task, JAMod mod)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(mod);
        if (task.IsCompleted)
        {
            ReportTaskFailure(task, mod);
            return;
        }
        task.GetAwaiter().UnsafeOnCompleted(() => ReportTaskFailure(task, mod));
    }

    private static void ReportTaskFailure(Task task, JAMod mod)
    {
        if (!task.IsFaulted || task.Exception == null)
            return;
        var exceptions = task.Exception.InnerExceptions;
        SendErrorMessage(mod, exceptions.Count == 1 ? exceptions[0] : task.Exception);
    }

    private static void Invoke(JAMod mod, Action action)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            action();
        }
        catch (Exception exception)
        {
            SendErrorMessage(mod, exception);
        }
    }

    private static TResult? Invoke<TResult>(JAMod mod, Func<TResult> action)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            return action();
        }
        catch (Exception exception)
        {
            SendErrorMessage(mod, exception);
            return default;
        }
    }

    private static async Task InvokeAsync(JAMod mod, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SendErrorMessage(mod, exception);
        }
    }

    private static async Task<TResult?> InvokeAsync<TResult>(
        JAMod mod,
        Func<Task<TResult>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SendErrorMessage(mod, exception);
            return default;
        }
    }

    private static void SendErrorMessage(JAMod mod, Exception exception)
        => mod.LogReportException("An error occurred while running a task", exception);

    [Flags]
    public enum CompleteFlag : byte
    {
        None = 0,
        TryCatchTask = 0x1,
        CompleteOnly = 0x2,
        TryCatchAction = 0x4,
        All = byte.MaxValue
    }
}

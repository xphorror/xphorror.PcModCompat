namespace Xphorror.PcModCompat;

public static class PcCompatManagedThreadBridge
{
    public static Thread Create(ThreadStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        var execution = RequireExecution("thread creation");
        return new Thread(() =>
        {
            using var scope = PcCompatManagedExecutionContext.EnterFlowing(execution);
            start();
        });
    }

    public static Task Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var execution = RequireExecution("background work");

        return Task.Run(() =>
        {
            using var scope = PcCompatManagedExecutionContext.EnterFlowing(execution);
            action();
        });
    }

    private static PcCompatManagedExecutionState RequireExecution(string operation) =>
        PcCompatManagedExecutionContext.Current
        ?? throw new InvalidOperationException(
            $"PcCompat {operation} requires an active managed scope.");

    public static void Abort(object threadObject)
    {
        if (threadObject == null)
            throw new NullReferenceException();
        if (threadObject is not Thread thread)
            throw new InvalidCastException(
                $"Expected {typeof(Thread).FullName}, got {threadObject.GetType().FullName}.");
        if (!thread.IsAlive)
            return;

        try
        {
            thread.Interrupt();
        }
        catch (ThreadStateException)
        {
            // The thread exited between IsAlive and Interrupt.
        }
    }
}

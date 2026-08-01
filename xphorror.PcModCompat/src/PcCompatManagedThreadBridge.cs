namespace Xphorror.PcModCompat;

public static class PcCompatManagedThreadBridge
{
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

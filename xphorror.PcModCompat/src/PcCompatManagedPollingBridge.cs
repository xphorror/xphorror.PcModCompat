namespace Xphorror.PcModCompat;

public static class PcCompatManagedPollingBridge
{
    public static bool WaitForCoarseClockAdvance()
    {
        Thread.Sleep(1);
        return true;
    }
}

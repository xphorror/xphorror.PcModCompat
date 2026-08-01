namespace Xphorror.PcModCompat;

public enum PcCompatKeyViewerRawSource : byte
{
    Touch = 1,
    Keyboard = 2,
    Controller = 3,
    Synthetic = 4,
    Mouse = 5,
    GameAction = 6
}

public enum PcCompatKeyViewerRawPhase : byte
{
    Down = 1,
    Up = 2,
    Cancel = 3,
    Reset = 4,
    ProducerChanged = 5
}

public readonly record struct PcCompatKeyViewerRawEvent(
    ulong Sequence,
    long RawNs,
    uint StateGeneration,
    uint SessionGeneration,
    uint ProducerEpoch,
    PcCompatKeyViewerInputOrigin Origin,
    PcCompatKeyViewerRawSource Source,
    PcCompatKeyViewerRawPhase Phase,
    int Code,
    int Slot,
    int PointerCount,
    int ScanCode,
    int MetaState,
    int DeviceId,
    int RepeatCount,
    int AndroidFlags,
    int SourceCode,
    int ViewportWidth,
    int ViewportHeight,
    float X,
    float Y,
    uint Flags);

public sealed class PcCompatKeyViewerEventBatch
{
    public static PcCompatKeyViewerEventBatch Unavailable { get; } = new();

    public bool ProviderAvailable { get; init; }
    public ulong Cursor { get; init; }
    public ulong DroppedBeforeCursor { get; init; }
    public IReadOnlyList<PcCompatKeyViewerRawEvent> Events { get; init; } =
        Array.Empty<PcCompatKeyViewerRawEvent>();

    public bool IsLossless => ProviderAvailable && DroppedBeforeCursor == 0;
}

public delegate PcCompatKeyViewerEventBatch PcCompatKeyViewerEventReadHandler(
    ulong cursor,
    int capacity);

public delegate bool PcCompatKeyViewerEventWaitHandler(
    ulong cursor,
    int timeoutMilliseconds);

public static class PcCompatKeyViewerEventRuntime
{
    public const ulong OpenAtTailCursor = ulong.MaxValue;

    private static PcCompatKeyViewerEventReadHandler? s_provider;
    private static PcCompatKeyViewerEventWaitHandler? s_waitProvider;
    private static Action? s_interruptWait;

    public static void RegisterProvider(PcCompatKeyViewerEventReadHandler provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Volatile.Write(ref s_provider, provider);
    }

    public static void RegisterWakeProvider(
        PcCompatKeyViewerEventWaitHandler waitProvider,
        Action interruptWait)
    {
        ArgumentNullException.ThrowIfNull(waitProvider);
        ArgumentNullException.ThrowIfNull(interruptWait);
        Volatile.Write(ref s_waitProvider, waitProvider);
        Volatile.Write(ref s_interruptWait, interruptWait);
    }

    public static void ClearProvider()
    {
        Volatile.Write(ref s_provider, null);
        ClearWakeProvider();
    }

    public static void ClearWakeProvider()
    {
        var interrupt = Interlocked.Exchange(ref s_interruptWait, null);
        Volatile.Write(ref s_waitProvider, null);
        try
        {
            interrupt?.Invoke();
        }
        catch
        {
            // Provider teardown is best effort.
        }
    }

    public static bool HasWakeProvider => Volatile.Read(ref s_waitProvider) != null;

    public static PcCompatKeyViewerEventBatch Read(ulong cursor, int capacity = 128)
    {
        var provider = Volatile.Read(ref s_provider);
        if (provider == null)
            return PcCompatKeyViewerEventBatch.Unavailable;
        capacity = Math.Clamp(capacity, 1, 256);

        try
        {
            return provider(cursor, capacity) ?? PcCompatKeyViewerEventBatch.Unavailable;
        }
        catch
        {
            return PcCompatKeyViewerEventBatch.Unavailable;
        }
    }

    public static PcCompatKeyViewerEventBatch OpenAtTail()
        => Read(OpenAtTailCursor, 1);

    public static bool WaitForChange(ulong cursor, int timeoutMilliseconds = 250)
    {
        var provider = Volatile.Read(ref s_waitProvider);
        if (provider == null)
            return false;
        try
        {
            return provider(cursor, Math.Clamp(timeoutMilliseconds, 1, 1000));
        }
        catch
        {
            Interlocked.CompareExchange(ref s_waitProvider, null, provider);
            return false;
        }
    }

    public static void InterruptWait()
    {
        try
        {
            Volatile.Read(ref s_interruptWait)?.Invoke();
        }
        catch
        {
            // Wake remains optional; UnityMain dispatch is the fallback.
        }
    }
}

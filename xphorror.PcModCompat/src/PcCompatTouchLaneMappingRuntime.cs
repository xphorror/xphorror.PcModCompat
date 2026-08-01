namespace Xphorror.PcModCompat;

public enum PcCompatTouchLaneMappingMode
{
    ScreenRegions = 0,
    TouchContacts = 1
}

public static class PcCompatTouchLaneMappingRuntime
{
    public const int DefaultTouchContactReuseDelayMilliseconds = 80;
    public const int MaximumTouchContactReuseDelayMilliseconds = 500;

    private static int s_mode;
    private static int s_touchContactReuseDelayMilliseconds =
        DefaultTouchContactReuseDelayMilliseconds;
    private static Action<PcCompatTouchLaneMappingMode>? s_nativeSink;
    private static Action<int>? s_nativeReuseDelaySink;

    public static PcCompatTouchLaneMappingMode Current
        => Normalize((PcCompatTouchLaneMappingMode)Volatile.Read(ref s_mode));

    public static int TouchContactReuseDelayMilliseconds
        => Volatile.Read(ref s_touchContactReuseDelayMilliseconds);

    public static void RegisterNativeSink(Action<PcCompatTouchLaneMappingMode>? sink)
    {
        Volatile.Write(ref s_nativeSink, sink);
        if (sink != null)
            InvokeNativeSink(sink, Current);
    }

    public static void RegisterNativeReuseDelaySink(Action<int>? sink)
    {
        Volatile.Write(ref s_nativeReuseDelaySink, sink);
        if (sink != null)
        {
            var milliseconds = TouchContactReuseDelayMilliseconds;
            PcCompatKeyViewerPreviewRuntime.ApplyTouchContactReuseDelayTransition(
                milliseconds,
                () => InvokeNativeReuseDelaySink(sink, milliseconds));
        }
    }

    public static void SetMode(PcCompatTouchLaneMappingMode mode)
    {
        mode = Normalize(mode);
        var previous = (PcCompatTouchLaneMappingMode)Interlocked.Exchange(
            ref s_mode,
            (int)mode);
        if (previous == mode)
            return;

        var rawNs = checked(Environment.TickCount64 * 1_000_000L);
        var sink = Volatile.Read(ref s_nativeSink);
        PcCompatKeyViewerPreviewRuntime.ApplyTouchLaneMappingModeTransition(
            mode,
            rawNs,
            sink == null ? null : () => InvokeNativeSink(sink, mode));
    }

    public static void SetTouchContactReuseDelayMilliseconds(int milliseconds)
    {
        milliseconds = NormalizeTouchContactReuseDelayMilliseconds(milliseconds);
        if (Interlocked.Exchange(
                ref s_touchContactReuseDelayMilliseconds,
                milliseconds) == milliseconds)
            return;

        var sink = Volatile.Read(ref s_nativeReuseDelaySink);
        PcCompatKeyViewerPreviewRuntime.ApplyTouchContactReuseDelayTransition(
            milliseconds,
            sink == null
                ? null
                : () => InvokeNativeReuseDelaySink(sink, milliseconds));
    }

    public static PcCompatTouchLaneMappingMode Normalize(PcCompatTouchLaneMappingMode mode)
        => mode is PcCompatTouchLaneMappingMode.ScreenRegions or
            PcCompatTouchLaneMappingMode.TouchContacts
            ? mode
            : PcCompatTouchLaneMappingMode.ScreenRegions;

    public static int NormalizeTouchContactReuseDelayMilliseconds(int milliseconds)
        => Math.Clamp(milliseconds, 0, MaximumTouchContactReuseDelayMilliseconds);

    private static void InvokeNativeSink(
        Action<PcCompatTouchLaneMappingMode> sink,
        PcCompatTouchLaneMappingMode mode)
    {
        try
        {
            sink(mode);
        }
        catch
        {
            // The managed consumer remains usable when an older native host lacks the setter.
        }
    }

    private static void InvokeNativeReuseDelaySink(Action<int> sink, int milliseconds)
    {
        try
        {
            sink(milliseconds);
        }
        catch
        {
            // Older native hosts keep their built-in default.
        }
    }
}

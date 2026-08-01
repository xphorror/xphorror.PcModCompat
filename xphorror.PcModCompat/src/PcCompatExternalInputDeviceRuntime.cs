namespace Xphorror.PcModCompat;

[Flags]
public enum PcCompatExternalInputDeviceFlags : uint
{
    None = 0,
    Keyboard = 1u << 0,
    Controller = 1u << 1,
    Mouse = 1u << 2
}

public readonly record struct PcCompatExternalInputDeviceSnapshot(
    bool Available,
    uint Generation,
    PcCompatExternalInputDeviceFlags Flags)
{
    public static PcCompatExternalInputDeviceSnapshot Unavailable { get; } =
        new(false, 0, PcCompatExternalInputDeviceFlags.None);

    public bool HasExternalInput
        => (Flags & (PcCompatExternalInputDeviceFlags.Keyboard |
                     PcCompatExternalInputDeviceFlags.Controller |
                     PcCompatExternalInputDeviceFlags.Mouse)) != 0;
}

public static class PcCompatExternalInputDeviceRuntime
{
    private static Func<PcCompatExternalInputDeviceSnapshot>? s_provider;

    public static void RegisterProvider(Func<PcCompatExternalInputDeviceSnapshot> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Volatile.Write(ref s_provider, provider);
    }

    public static void ClearProvider()
        => Volatile.Write(ref s_provider, null);

    public static PcCompatExternalInputDeviceSnapshot Snapshot()
    {
        var provider = Volatile.Read(ref s_provider);
        if (provider == null)
            return PcCompatExternalInputDeviceSnapshot.Unavailable;
        try
        {
            return provider();
        }
        catch
        {
            return PcCompatExternalInputDeviceSnapshot.Unavailable;
        }
    }
}

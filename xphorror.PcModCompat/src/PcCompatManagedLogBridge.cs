using StArray.ModManager.Manager;

namespace Xphorror.PcModCompat;

/// <summary>
/// Routes <c>UnityEngine.Debug</c> logging to the host logger instead of the IL2CPP proxy.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LogException"/> exists because the proxy needs an <c>Il2CppSystem.Exception</c>, which
/// a CoreCLR exception is not. The three message overloads are here for a different reason: their
/// parameter is <c>System.Object</c> and the proxy wants <c>Il2CppSystem.Object</c>, so forwarding
/// them would mean boxing an arbitrary CoreCLR object across the IL2CPP boundary and owning its
/// lifetime there - for no benefit, since the only thing the Unity side would do with it is call
/// <c>ToString()</c>.
/// </para>
/// <para>
/// Doing it here also keeps MOD diagnostics in the ModManager log, where they can actually be read
/// on Android, rather than in the Unity player log.
/// </para>
/// <para>
/// The <c>message.ToString()</c> is what Unity itself does, and it runs entirely on the managed
/// side, so a MOD type with a custom <c>ToString()</c> behaves exactly as it does on PC.
/// </para>
/// </remarks>
public static class PcCompatManagedLogBridge
{
    public static void LogException(Exception exception)
        => Logger.Error("UnityEngine.Debug", exception?.ToString() ?? "LogException(null)");

    public static void Log(object? message)
        => Logger.Info("UnityEngine.Debug", Describe(message));

    public static void LogWarning(object? message)
        => Logger.Warn("UnityEngine.Debug", Describe(message));

    public static void LogError(object? message)
        => Logger.Error("UnityEngine.Debug", Describe(message));

    /// <summary>
    /// Mirrors Unity's own null rendering. A MOD <c>ToString()</c> that throws must not take the
    /// caller down: on PC the exception would surface inside Unity's logger, here it would escape
    /// into MOD code that was only trying to log.
    /// </summary>
    private static string Describe(object? message)
    {
        if (message is null)
            return "Null";
        try
        {
            return message.ToString() ?? "Null";
        }
        catch (Exception exception)
        {
            return $"<{message.GetType().FullName}.ToString() threw {exception.GetType().Name}: " +
                   $"{exception.Message}>";
        }
    }
}

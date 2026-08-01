using StArray.ModManager.Manager;

namespace Xphorror.PcModCompat;

/// <summary>
/// Keeps CoreCLR exceptions on the managed side instead of passing them to an
/// IL2CPP Debug.LogException proxy that requires an Il2CppSystem.Exception.
/// </summary>
public static class PcCompatManagedLogBridge
{
    public static void LogException(Exception exception)
        => Logger.Error("UnityEngine.Debug", exception?.ToString() ?? "LogException(null)");
}

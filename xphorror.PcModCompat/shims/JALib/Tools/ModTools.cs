using JALib.Core;
using UnityModManagerNet;

namespace JALib.Tools;

public static class ModTools
{
    private static readonly object EventLock = new();
    private static readonly List<(JAMod Mod, Action<UnityModManager.ModEntry> Action)>
        ActiveEvents = [];

    static ModTools()
    {
        UnityModManager.ModLoadCompleted += DispatchModLoad;
    }

    public static void RegisterModLoadEvent(
        JAMod mod,
        Action<UnityModManager.ModEntry> action)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(action);
        lock (EventLock)
            ActiveEvents.Add((mod, action));
    }

    public static void UnregisterModLoadEvent(
        JAMod mod,
        Action<UnityModManager.ModEntry> action)
    {
        lock (EventLock)
            ActiveEvents.Remove((mod, action));
    }

    public static void ApplyMod(JAMod requestMod, string path)
    {
        ArgumentNullException.ThrowIfNull(requestMod);
        requestMod.LogReportException(
            $"[ApplyMod] Failed to apply mod from path '{path}'",
            new PlatformNotSupportedException(
                "Runtime managed-DLL loading is unavailable on Android IL2CPP; " +
                "import the package through PcModCompat so it can be translated and cached."));
    }

    private static void DispatchModLoad(UnityModManager.ModEntry entry)
    {
        (JAMod Mod, Action<UnityModManager.ModEntry> Action)[] snapshot;
        lock (EventLock)
            snapshot = ActiveEvents.ToArray();
        foreach (var registration in snapshot)
        {
            try
            {
                registration.Action(entry);
            }
            catch (Exception exception)
            {
                registration.Mod.LogReportException(
                    $"Mod Load Event Error for mod '{entry.Info.Id}'",
                    exception);
            }
        }
    }
}

[Obsolete("Deprecated. Use ModTools.ApplyMod instead.", true)]
public static class ForceApplyMod
{
    [Obsolete("Deprecated. Use ModTools.ApplyMod instead.", true)]
    public static void ApplyMod(string path)
    {
        var owner = JAMod.GetMods().FirstOrDefault();
        if (owner != null)
        {
            ModTools.ApplyMod(owner, path);
            return;
        }
        Console.Error.WriteLine(
            $"[PcModCompat][JALib][ERROR][ForceApplyMod] Cannot apply '{path}': " +
            "no JAMod owner is registered and runtime managed-DLL loading is unavailable.");
    }
}

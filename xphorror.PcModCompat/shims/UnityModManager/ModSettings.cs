namespace UnityModManagerNet;

public abstract class UnityModManagerModSettings
{
    public virtual void Save(UnityModManager.ModEntry modEntry) { }
}

public abstract class ModSettings : UnityModManagerModSettings
{
    public static T Load<T>(UnityModManager.ModEntry modEntry) where T : ModSettings, new()
        => new();
}

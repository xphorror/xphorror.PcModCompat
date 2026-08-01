using System.Reflection;

namespace JALib.Core;

public class ModReloadCache
{
    public Dictionary<(Type, int), object> CachedObjects = new();
    public Assembly NewAssembly;
    public Assembly OldAssembly;

    internal ModReloadCache(Assembly oldAssembly, Assembly assembly)
    {
        OldAssembly = oldAssembly;
        NewAssembly = assembly;
    }

    public object? GetCachedObject(object? oldValue)
    {
        if (oldValue == null)
            return null;
        var oldType = oldValue.GetType();
        if (oldType.Assembly != OldAssembly)
            return oldValue;
        var key = (oldType, oldValue.GetHashCode());
        if (CachedObjects.TryGetValue(key, out var cached))
            return cached;
        var newType = NewAssembly.GetType(oldType.FullName ?? string.Empty);
        if (newType == null)
            return null;

        try
        {
            var newValue = Activator.CreateInstance(newType)
                ?? throw new InvalidOperationException(
                    $"Could not construct reloaded type '{newType.FullName}'.");
            CachedObjects[key] = newValue;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance;
            foreach (var oldField in oldType.GetFields(flags))
            {
                try
                {
                    var newField = newType.GetField(oldField.Name, flags);
                    if (newField != null)
                        newField.SetValue(newValue, GetCachedObject(oldField.GetValue(oldValue)));
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        $"[PcModCompat][JALib][ModReloadCache][ERROR] Failed to reload field " +
                        $"'{oldField.Name}' of type '{oldType.FullName}': {exception}");
                }
            }
            return newValue;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[PcModCompat][JALib][ModReloadCache][ERROR] Failed to reload object " +
                $"of type '{oldType.FullName}': {exception}");
            return null;
        }
    }
}

namespace UnityEngine;

public class AssetBundle : Object
{
    public static AssetBundle? LoadFromFile(string path) => new();
    public T? LoadAsset<T>(string name) where T : Object => null;
    public Object? LoadAsset(string name) => null;
    public Object[] LoadAllAssets() => Array.Empty<Object>();
    public T[] LoadAllAssets<T>() where T : Object => Array.Empty<T>();
    public void Unload(bool unloadAllLoadedObjects) { }
}

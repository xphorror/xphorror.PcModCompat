namespace StArray.ModManager.Android.PcCompat;

public static class PcCompatCollectionBridge
{
    public static List<T> CopyList<T>(Il2CppSystem.Collections.Generic.List<T>? source)
    {
        if (source is null)
            return [];

        var result = new List<T>(source.Count);
        for (var index = 0; index < source.Count; index++)
            result.Add(source[index]);
        return result;
    }
}

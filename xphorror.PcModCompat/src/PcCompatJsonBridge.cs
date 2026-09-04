using StArray.ModManager.Manager;

namespace Xphorror.PcModCompat;

/// <summary>
/// Keeps <c>UnityEngine.JsonUtility</c> calls on the managed side, where the MOD's own types exist.
/// </summary>
/// <remarks>
/// <para>
/// The IL2CPP <c>JsonUtility</c> reflects over the object it is given. JipperKeyViewer hands it
/// <c>ProfileData</c>, <c>KeyViewerSettings</c> and <c>SettingsMeta</c> - CoreCLR types with no entry
/// in the IL2CPP class table - so forwarding the call yields a failure or <c>{}</c>. That makes this a
/// manual bridge by the rule in <c>MANAGED_BRIDGE_AND_CONVERTER_DESIGN.md</c> §2.3: the argument is a
/// MOD-owned type the proxy cannot name, so no converter can help.
/// </para>
/// <para>
/// <see cref="FromJson{T}"/> is bridged even though the proxy's generic signature matches exactly and
/// the rewriter reports the callsite as clean. That report is a false negative: a match on the
/// signature says nothing about whether <c>T</c> exists on the IL2CPP side, and here it does not.
/// </para>
/// </remarks>
public static class PcCompatJsonBridge
{
    public static string ToJson(object? value, bool prettyPrint)
    {
        try
        {
            return PcCompatUnityJson.ToJson(value, prettyPrint);
        }
        catch (Exception exception)
        {
            // Returning "{}" would let the caller write a valid-looking but empty settings file over a
            // good one. Rethrowing keeps the MOD's own try/catch in charge of that decision - both
            // JipperKeyViewer save paths already wrap the call and skip the write on failure.
            Logger.Error(
                "UnityEngine.JsonUtility",
                $"ToJson({value?.GetType().FullName ?? "null"}) failed: {exception}");
            throw;
        }
    }

    public static void FromJsonOverwrite(string? json, object? target)
    {
        try
        {
            PcCompatUnityJson.FromJsonOverwrite(json, target);
        }
        catch (Exception exception)
        {
            Logger.Error(
                "UnityEngine.JsonUtility",
                $"FromJsonOverwrite into {target?.GetType().FullName ?? "null"} failed: {exception}");
            throw;
        }
    }

    public static T? FromJson<T>(string? json)
    {
        try
        {
            return PcCompatUnityJson.FromJson<T>(json);
        }
        catch (Exception exception)
        {
            Logger.Error(
                "UnityEngine.JsonUtility",
                $"FromJson<{typeof(T).FullName}> failed: {exception}");
            throw;
        }
    }
}

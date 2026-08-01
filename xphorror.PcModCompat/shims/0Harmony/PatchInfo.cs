using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Serialization;

namespace HarmonyLib;

[Serializable]
public class PatchInfo
{
    [JsonInclude]
    public Patch[] prefixes = [];

    [JsonInclude]
    public Patch[] postfixes = [];

    [JsonInclude]
    public Patch[] transpilers = [];

    [JsonInclude]
    public Patch[] finalizers = [];

    [JsonInclude]
    public Patch[] innerprefixes = [];

    [JsonInclude]
    public Patch[] innerpostfixes = [];

    [JsonIgnore]
    public bool Debugging => prefixes.Any(patch => patch.debug)
        || postfixes.Any(patch => patch.debug)
        || transpilers.Any(patch => patch.debug)
        || finalizers.Any(patch => patch.debug)
        || innerprefixes.Any(patch => patch.debug)
        || innerpostfixes.Any(patch => patch.debug);

    [JsonInclude]
    public int VersionCount;

    internal void AddPrefixes(string owner, params HarmonyMethod[] methods) => prefixes = Add(owner, methods, prefixes);

    [Obsolete("This method only exists for backwards compatibility since the class is public.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void AddPrefix(MethodInfo patch, string owner, int priority, string[] before, string[] after, bool debug)
        => AddPrefixes(owner, new HarmonyMethod(patch, priority, before, after, debug));

    public void RemovePrefix(string owner) => prefixes = Remove(owner, prefixes);

    internal void AddPostfixes(string owner, params HarmonyMethod[] methods) => postfixes = Add(owner, methods, postfixes);

    [Obsolete("This method only exists for backwards compatibility since the class is public.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void AddPostfix(MethodInfo patch, string owner, int priority, string[] before, string[] after, bool debug)
        => AddPostfixes(owner, new HarmonyMethod(patch, priority, before, after, debug));

    public void RemovePostfix(string owner) => postfixes = Remove(owner, postfixes);

    internal void AddTranspilers(string owner, params HarmonyMethod[] methods) => transpilers = Add(owner, methods, transpilers);

    [Obsolete("This method only exists for backwards compatibility since the class is public.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void AddTranspiler(MethodInfo patch, string owner, int priority, string[] before, string[] after, bool debug)
        => AddTranspilers(owner, new HarmonyMethod(patch, priority, before, after, debug));

    public void RemoveTranspiler(string owner) => transpilers = Remove(owner, transpilers);

    internal void AddFinalizers(string owner, params HarmonyMethod[] methods) => finalizers = Add(owner, methods, finalizers);

    [Obsolete("This method only exists for backwards compatibility since the class is public.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void AddFinalizer(MethodInfo patch, string owner, int priority, string[] before, string[] after, bool debug)
        => AddFinalizers(owner, new HarmonyMethod(patch, priority, before, after, debug));

    public void RemoveFinalizer(string owner) => finalizers = Remove(owner, finalizers);

    internal void AddInnerPrefixes(string owner, params HarmonyMethod[] methods) => innerprefixes = Add(owner, methods, innerprefixes);

    public void RemoveInnerPrefix(string owner) => innerprefixes = Remove(owner, innerprefixes);

    internal void AddInnerPostfixes(string owner, params HarmonyMethod[] methods) => innerpostfixes = Add(owner, methods, innerpostfixes);

    public void RemoveInnerPostfix(string owner) => innerpostfixes = Remove(owner, innerpostfixes);

    public void RemovePatch(MethodInfo patch)
    {
        prefixes = [.. prefixes.Where(candidate => candidate.PatchMethod != patch)];
        postfixes = [.. postfixes.Where(candidate => candidate.PatchMethod != patch)];
        transpilers = [.. transpilers.Where(candidate => candidate.PatchMethod != patch)];
        finalizers = [.. finalizers.Where(candidate => candidate.PatchMethod != patch)];
        innerprefixes = [.. innerprefixes.Where(candidate => candidate.PatchMethod != patch)];
        innerpostfixes = [.. innerpostfixes.Where(candidate => candidate.PatchMethod != patch)];
    }

    private static Patch[] Add(string owner, HarmonyMethod[] additions, Patch[] current)
        => additions.Length == 0
            ? current
            : [.. current, .. additions.Where(method => method is not null).Select((method, index) => new Patch(method, current.Length + index, owner))];

    private static Patch[] Remove(string owner, Patch[] current)
        => owner == "*" ? [] : [.. current.Where(patch => patch.owner != owner)];
}

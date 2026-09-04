namespace StArray.ModManager.Android.PcCompat;

using System.Runtime.CompilerServices;
using System.Reflection;
using StArray.ModManager.Manager;

public static class PcCompatCollectionBridge
{
    /// <summary>
    /// Maps a managed copy handed to a MOD back to the Il2Cpp collection it came from, so mutations
    /// on the copy can be replayed onto the real one.
    /// </summary>
    /// <remarks>
    /// Keyed by reference identity on the copy, which the getter bridge creates fresh per call, so
    /// entries cannot collide between MODs or between calls. <see cref="ConditionalWeakTable{TKey,TValue}"/>
    /// rather than a dictionary because the copy's lifetime is the MOD's, not ours - a strong map
    /// would pin every table a MOD ever read.
    /// </remarks>
    private static readonly ConditionalWeakTable<object, object> BoundCollections = new();
    private static readonly ConditionalWeakTable<Type, SetterCache> SetterCaches = new();

    public static List<T> CopyList<T>(Il2CppSystem.Collections.Generic.List<T>? source)
    {
        if (source is null)
            return [];

        var result = new List<T>(source.Count);
        for (var index = 0; index < source.Count; index++)
            result.Add(source[index]);
        return result;
    }

    /// <summary>
    /// The inverse of <see cref="CopyList{T}"/>: materializes a managed list as the Il2Cpp one a
    /// proxy setter requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the setter half of a property whose getter goes through <see cref="CopyList{T}"/>.
    /// Both directions copy, so a get/mutate/set round trip does reach Unity - which is how
    /// <c>fallbackFontAssetTable = new List&lt;TMP_FontAsset&gt;()</c> works. What copying cannot fix
    /// is mutating the value the getter returned <i>without</i> calling the setter; that is what
    /// <see cref="CopyBoundList{T}"/> exists for.
    /// </para>
    /// <para>
    /// A null argument produces a null Il2Cpp reference rather than an empty list, because the two
    /// mean different things to Unity and the caller asked for null.
    /// </para>
    /// </remarks>
    public static Il2CppSystem.Collections.Generic.List<T>? ToIl2CppList<T>(List<T>? source)
    {
        if (source is null)
            return null;

        var result = new Il2CppSystem.Collections.Generic.List<T>(source.Count);
        foreach (var item in source)
            result.Add(item);
        return result;
    }

    /// <summary>
    /// Like <see cref="CopyList{T}"/>, but remembers which Il2Cpp collection the copy came from so
    /// that <see cref="AddToBoundList{T}"/> and its siblings can write through to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Used for properties registered as writable collections. The plain <see cref="CopyList{T}"/>
    /// is a dead end for mutation: a MOD doing <c>font.fallbackFontAssetTable.Add(cjk)</c> appends to
    /// a copy that nothing ever reads, so the CJK fallback silently never applies. Returning a
    /// <c>List&lt;T&gt;</c> subclass would not help - <c>List&lt;T&gt;.Add</c> is not virtual, and the
    /// compiled MOD calls it non-virtually on the concrete type.
    /// </para>
    /// <para>
    /// The copy is still a copy: reads (<c>Count</c>, <c>Contains</c>, indexer, enumeration) go to it
    /// untouched, which is correct because the audited MODs always read immediately after the getter
    /// call that produced it.
    /// </para>
    /// </remarks>
    public static List<T> CopyBoundList<T>(Il2CppSystem.Collections.Generic.List<T>? source)
    {
        if (source is null)
        {
            // No target to write through to. Reported here rather than at mutation time because this
            // is where the cause is known; a caller that only reads is unaffected and should not be
            // warned about, but that cannot be distinguished later.
            Logger.Warn(
                "PcCompatCollectionBridge",
                $"writable collection of {typeof(T).Name} was null on the Unity side; " +
                "mutations through this copy cannot reach Unity");
            return [];
        }

        var result = CopyList(source);
        BoundCollections.AddOrUpdate(result, source);
        return result;
    }

    /// <summary>
    /// Returns a write-through copy and initializes a null Unity-side collection through its real
    /// proxy setter. The rewriter preserves the getter receiver and supplies the property name.
    /// </summary>
    public static List<T> CopyOrCreateBoundList<T>(
        object owner,
        Il2CppSystem.Collections.Generic.List<T>? source,
        string propertyName)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        if (source is null)
        {
            // The dependency-closed Android corlib exposes the capacity constructor, not .ctor().
            source = new Il2CppSystem.Collections.Generic.List<T>(0);
            var setter = SetterCaches.GetValue(owner.GetType(), static _ => new SetterCache())
                .Resolve(owner.GetType(), propertyName, source.GetType());
            setter.Invoke(owner, [source]);
        }

        var result = CopyList(source);
        BoundCollections.AddOrUpdate(result, source);
        return result;
    }

    /// <summary>Writes through to the bound Il2Cpp collection, if there is one.</summary>
    /// <remarks>
    /// An unbound list - one the MOD created itself - is mutated in place and nothing else happens,
    /// so these four methods are faithful stand-ins for the <c>List&lt;T&gt;</c> members they replace.
    /// That is what lets the rewriter retarget every matching callsite without first proving the
    /// receiver came from a registered getter.
    /// </remarks>
    public static void AddToBoundList<T>(List<T> list, T item)
    {
        list.Add(item);
        if (TryGetBound<T>(list, out var bound))
            bound.Add(item);
    }

    public static bool RemoveFromBoundList<T>(List<T> list, T item)
    {
        var removed = list.Remove(item);
        if (removed && TryGetBound<T>(list, out var bound))
            bound.Remove(item);
        return removed;
    }

    public static void ClearBoundList<T>(List<T> list)
    {
        list.Clear();
        if (TryGetBound<T>(list, out var bound))
            bound.Clear();
    }

    public static void InsertIntoBoundList<T>(List<T> list, int index, T item)
    {
        list.Insert(index, item);
        if (TryGetBound<T>(list, out var bound))
            bound.Insert(index, item);
    }

    private static bool TryGetBound<T>(
        List<T> list,
        out Il2CppSystem.Collections.Generic.List<T> bound)
    {
        if (BoundCollections.TryGetValue(list, out var stored) &&
            stored is Il2CppSystem.Collections.Generic.List<T> typed)
        {
            bound = typed;
            return true;
        }

        bound = null!;
        return false;
    }

    private sealed class SetterCache
    {
        private readonly object _lock = new();
        private readonly Dictionary<(string Property, Type ValueType), MethodInfo> _setters = [];

        public MethodInfo Resolve(Type ownerType, string propertyName, Type valueType)
        {
            lock (_lock)
            {
                var key = (propertyName, valueType);
                if (_setters.TryGetValue(key, out var cached))
                    return cached;
                var candidates = ownerType.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(method => method.Name == "set_" + propertyName &&
                                     method.ReturnType == typeof(void) &&
                                     method.GetParameters() is [{ ParameterType: var parameterType }] &&
                                     parameterType.IsAssignableFrom(valueType))
                    .ToArray();
                if (candidates.Length != 1)
                {
                    throw new MissingMethodException(
                        $"Expected one {ownerType.FullName}.set_{propertyName}({valueType.FullName}), " +
                        $"found {candidates.Length}.");
                }
                _setters[key] = candidates[0];
                return candidates[0];
            }
        }
    }
}

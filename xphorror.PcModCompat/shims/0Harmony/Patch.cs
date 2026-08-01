using System.Collections.ObjectModel;
using System.Reflection;

namespace HarmonyLib;

// ABI mirror of Harmony 2.4 HarmonyLib/Public/{Priority,InnerMethod,Patch,Patches}.cs.
// JALib's PatchMetadata projects the PcCompat logical registry into these shapes, so the
// constructor signatures and readonly field names have to stay exactly as upstream.

public static class Priority
{
    public const int Last = 0;
    public const int VeryLow = 100;
    public const int Low = 200;
    public const int LowerThanNormal = 300;
    public const int Normal = 400;
    public const int HigherThanNormal = 500;
    public const int High = 600;
    public const int VeryHigh = 700;
    public const int First = 800;
}

public class InnerMethod
{
    public int[] positions;

    private readonly MethodInfo? method;

    public InnerMethod(MethodInfo method, params int[] positions)
    {
        this.method = method;
        this.positions = positions ?? [];
    }

    public MethodInfo Method
        => method ?? throw new InvalidOperationException("InnerMethod has no resolved method.");
}

public class Patch : IComparable
{
    public readonly int index;

    public readonly string owner;

    public readonly int priority;

    public readonly string[] before;

    public readonly string[] after;

    public readonly bool debug;

    public readonly InnerMethod? innerMethod;

    public MethodInfo PatchMethod { get; set; }

    public Patch(
        MethodInfo patch,
        int index,
        string owner,
        int priority,
        string[]? before,
        string[]? after,
        bool debug)
    {
        if (patch is System.Reflection.Emit.DynamicMethod)
            throw new Exception(
                $"Cannot directly reference dynamic method \"{patch.FullDescription()}\" in Harmony. " +
                "Use a factory method instead that will return the dynamic method.");

        this.index = index;
        this.owner = owner;
        // Upstream normalizes the "unset" sentinel here, not at merge time; JALib's PatchInfo
        // projection reads the normalized value back.
        this.priority = priority == -1 ? Priority.Normal : priority;
        this.before = before ?? [];
        this.after = after ?? [];
        this.debug = debug;
        PatchMethod = patch;
    }

    public Patch(HarmonyMethod method, int index, string owner)
        : this(
            method.method ?? throw new ArgumentException("HarmonyMethod has no method", nameof(method)),
            index,
            owner,
            method.priority,
            method.before,
            method.after,
            method.debug ?? false)
    {
    }

    // A patch method may be a factory returning the real replacement; upstream honours that
    // shape, and MODs occasionally rely on it for version-dependent bodies.
    public MethodInfo GetMethod(MethodBase original)
    {
        var method = PatchMethod;
        if (method.ReturnType != typeof(System.Reflection.Emit.DynamicMethod) && method.ReturnType != typeof(MethodInfo))
            return method;
        if (method.IsStatic is false)
            return method;
        var parameters = method.GetParameters();
        if (parameters.Length != 1)
            return method;
        if (parameters[0].ParameterType != typeof(MethodBase))
            return method;

        return (MethodInfo)method.Invoke(null, [original])!;
    }

    public override bool Equals(object? obj)
        => obj is Patch other && PatchMethod == other.PatchMethod;

    public int CompareTo(object? obj)
    {
        if (obj is not Patch other)
            return 1;

        // Upstream sorts descending by priority and ascending by index for equal priorities.
        var result = other.priority.CompareTo(priority);
        return result != 0 ? result : index.CompareTo(other.index);
    }

    public override int GetHashCode()
        => PatchMethod.GetHashCode();
}

public class Patches
{
    public readonly ReadOnlyCollection<Patch> Prefixes;

    public readonly ReadOnlyCollection<Patch> Postfixes;

    public readonly ReadOnlyCollection<Patch> Transpilers;

    public readonly ReadOnlyCollection<Patch> Finalizers;

    public readonly ReadOnlyCollection<Patch> InnerPrefixes;

    public readonly ReadOnlyCollection<Patch> InnerPostfixes;

    public ReadOnlyCollection<string> Owners
    {
        get
        {
            var result = new HashSet<string>();
            result.UnionWith(Prefixes.Select(p => p.owner));
            result.UnionWith(Postfixes.Select(p => p.owner));
            result.UnionWith(Transpilers.Select(p => p.owner));
            result.UnionWith(Finalizers.Select(p => p.owner));
            result.UnionWith(InnerPrefixes.Select(p => p.owner));
            result.UnionWith(InnerPostfixes.Select(p => p.owner));
            return new ReadOnlyCollection<string>([.. result]);
        }
    }

    public Patches(
        Patch[]? prefixes,
        Patch[]? postfixes,
        Patch[]? transpilers,
        Patch[]? finalizers,
        Patch[]? innerprefixes,
        Patch[]? innerpostfixes)
    {
        Prefixes = new ReadOnlyCollection<Patch>(prefixes ?? []);
        Postfixes = new ReadOnlyCollection<Patch>(postfixes ?? []);
        Transpilers = new ReadOnlyCollection<Patch>(transpilers ?? []);
        Finalizers = new ReadOnlyCollection<Patch>(finalizers ?? []);
        InnerPrefixes = new ReadOnlyCollection<Patch>(innerprefixes ?? []);
        InnerPostfixes = new ReadOnlyCollection<Patch>(innerpostfixes ?? []);
    }
}

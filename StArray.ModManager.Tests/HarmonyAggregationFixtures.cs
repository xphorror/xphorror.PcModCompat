using System.Reflection;
using HarmonyLib;

// Fixtures for PcCompatHarmonyAttributeAggregator. They are annotated with the real shim attributes
// and compiled into the test assembly, so the aggregator reads exactly the metadata a PC MOD built
// against 0Harmony would produce - no hand-written blobs, no mocked reader.
//
// Nothing here is ever executed or reflected over: some fixtures carry annotations whose constructor
// would throw if it ran (see VariationMismatchPatch), which is precisely the case being covered.
//
// Every type lives under StArray.ModManager.Tests.HarmonyFixtures so the tests can isolate their own
// descriptors from the rest of the test assembly by callback type prefix.
namespace StArray.ModManager.Tests.HarmonyFixtures;

/// <summary>Stand-in for a game type. Only its name reaches the aggregator, but the members are real
/// so the fixtures can use <c>nameof</c>.</summary>
public class FixtureTarget
{
    public int Value { get; set; }

    public string this[int index]
    {
        get => string.Empty;
        set { }
    }

    public event Action? Fired;

    static FixtureTarget()
    {
    }

    public void Run() => Fired?.Invoke();

    public void Mix(int a, string b, int c, byte d)
    {
    }

    public IEnumerable<int> Iterate()
    {
        yield return Value;
    }

    public Task Wait() => Task.CompletedTask;

    public static FixtureTarget operator +(FixtureTarget left, FixtureTarget right) => left;
}

// --- the ordinary case: class names the target, methods name their patch kind -------------------

[HarmonyPatch(typeof(FixtureTarget), nameof(FixtureTarget.Run))]
public static class MergedPatch
{
    [HarmonyPrefix]
    public static void Pre()
    {
    }

    [HarmonyPostfix]
    public static void Post(FixtureTarget __instance)
    {
    }

    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Trans(IEnumerable<CodeInstruction> instructions) => instructions;

    [HarmonyFinalizer]
    public static void Fin()
    {
    }
}

/// <summary>No method-level annotations at all: <c>GetPatchType</c> accepts these by name.</summary>
[HarmonyPatch(typeof(FixtureTarget), nameof(FixtureTarget.Run))]
public static class ConventionPatch
{
    public static void Prefix()
    {
    }

    public static void Postfix()
    {
    }
}

/// <summary>Method-level attribute supplies the method name, class level supplies the type.</summary>
[HarmonyPatch(typeof(FixtureTarget))]
public static class SplitTargetPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(FixtureTarget.Run))]
    public static void ByName()
    {
    }
}

// --- MethodType coverage ------------------------------------------------------------------------

[HarmonyPatch(typeof(FixtureTarget))]
public static class MethodTypePatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(FixtureTarget.Value), MethodType.Getter)]
    public static void Getter()
    {
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(FixtureTarget.Value), MethodType.Setter)]
    public static void Setter()
    {
    }

    [HarmonyPrefix]
    [HarmonyPatch(MethodType.Constructor)]
    public static void Ctor()
    {
    }

    [HarmonyPrefix]
    [HarmonyPatch(MethodType.StaticConstructor)]
    public static void Cctor()
    {
    }

    [HarmonyPrefix]
    [HarmonyPatch(MethodType.Finalizer)]
    public static void Destructor()
    {
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(FixtureTarget.Fired), MethodType.EventAdd)]
    public static void EventAdd()
    {
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(FixtureTarget.Fired), MethodType.EventRemove)]
    public static void EventRemove()
    {
    }

    [HarmonyPrefix]
    [HarmonyPatch(MethodType.OperatorAddition)]
    public static void OperatorAddition()
    {
    }

    // Highest operator value in the enum; guards the upper bound of the operator range.
    [HarmonyPrefix]
    [HarmonyPatch(MethodType.OperatorComma)]
    public static void OperatorComma()
    {
    }
}

// --- argument types -----------------------------------------------------------------------------

[HarmonyPatch(
    typeof(FixtureTarget),
    nameof(FixtureTarget.Mix),
    new[] { typeof(int), typeof(string), typeof(int), typeof(byte) },
    new[] { ArgumentType.Normal, ArgumentType.Ref, ArgumentType.Out, ArgumentType.Pointer })]
public static class ArgumentVariationPatch
{
    [HarmonyPrefix]
    public static void Pre()
    {
    }
}

[HarmonyPatch(typeof(FixtureTarget), nameof(FixtureTarget.Mix), typeof(int), typeof(string))]
public static class ArgumentTypesPatch
{
    [HarmonyPrefix]
    public static void Pre()
    {
    }
}

/// <summary>Fewer variations than types; upstream throws <c>IndexOutOfRangeException</c> from the
/// attribute constructor, which cancels the whole class.</summary>
[HarmonyPatch(
    typeof(FixtureTarget),
    nameof(FixtureTarget.Mix),
    new[] { typeof(int), typeof(string), typeof(int) },
    new[] { ArgumentType.Normal, ArgumentType.Ref })]
public static class VariationMismatchPatch
{
    [HarmonyPrefix]
    public static void Pre()
    {
    }
}

// --- rejected shapes ----------------------------------------------------------------------------

/// <summary>A non-static prefix makes <c>AttributePatch.Create</c> throw, which aborts every patch
/// in the class - not just this one.</summary>
[HarmonyPatch(typeof(FixtureTarget), nameof(FixtureTarget.Run))]
public class InstancePatch
{
    [HarmonyPrefix]
    public void Pre()
    {
    }

    [HarmonyPostfix]
    public static void Post()
    {
    }
}

[HarmonyPatch(typeof(FixtureTarget))]
[HarmonyPatchAll]
public static class BulkPatch
{
    [HarmonyPrefix]
    public static void Pre()
    {
    }
}

[HarmonyPatch]
public static class DynamicTargetPatch
{
    [HarmonyTargetMethod]
    public static MethodBase? Resolve() => null;

    [HarmonyPrefix]
    public static void Pre()
    {
    }
}

/// <summary>No attribute on the auxiliary method: the bare-name fallback has to find it.</summary>
[HarmonyPatch]
public static class DynamicTargetsPatch
{
    public static IEnumerable<MethodBase> TargetMethods() => Array.Empty<MethodBase>();

    [HarmonyPrefix]
    public static void Pre()
    {
    }
}

[HarmonyPatch(typeof(FixtureTarget), nameof(FixtureTarget.Iterate), MethodType.Enumerator)]
public static class EnumeratorPatch
{
    [HarmonyPrefix]
    public static void Pre()
    {
    }
}

[HarmonyPatch(typeof(FixtureTarget), nameof(FixtureTarget.Wait), MethodType.Async)]
public static class AsyncPatch
{
    [HarmonyPrefix]
    public static void Pre()
    {
    }
}

/// <summary>Getter with no property name is upstream's spelling for "the indexer".</summary>
[HarmonyPatch(typeof(FixtureTarget), MethodType.Getter)]
public static class IndexerPatch
{
    [HarmonyPrefix]
    public static void Pre()
    {
    }
}

[HarmonyPatch(nameof(FixtureTarget.Run))]
public static class NoDeclaringTypePatch
{
    [HarmonyPrefix]
    public static void Pre()
    {
    }
}

[HarmonyPatch(typeof(FixtureTarget))]
public static class NoMethodNamePatch
{
    [HarmonyPrefix]
    public static void Pre()
    {
    }
}

/// <summary>A MethodType value outside the mirrored enum, as a MOD built against a newer Harmony
/// would produce.</summary>
[HarmonyPatch(typeof(FixtureTarget), (MethodType)99)]
public static class UnknownMethodTypePatch
{
    [HarmonyPrefix]
    public static void Pre()
    {
    }
}

// --- gates and ordering -------------------------------------------------------------------------

[HarmonyPatch(typeof(FixtureTarget), nameof(FixtureTarget.Run))]
public static class PreparedPatch
{
    [HarmonyPrepare]
    public static bool Ready() => true;

    [HarmonyPrefix]
    public static void Pre()
    {
    }
}

[HarmonyPatch(typeof(FixtureTarget), nameof(FixtureTarget.Run))]
[HarmonyPriority(100)]
[HarmonyBefore("fixture.before")]
[HarmonyAfter("fixture.after")]
public static class OrderingPatch
{
    /// <summary>Takes the container priority unchanged.</summary>
    [HarmonyPrefix]
    public static void Inherited()
    {
    }

    /// <summary>Higher than the container's, so Math.Max picks this one.</summary>
    [HarmonyPrefix]
    [HarmonyPriority(300)]
    public static void Higher()
    {
    }

    /// <summary>Lower than the container's; Math.Max keeps the container value even though the
    /// method-level attribute is the "detail" side of the merge.</summary>
    [HarmonyPrefix]
    [HarmonyPriority(50)]
    public static void Lower()
    {
    }
}

[HarmonyPatch(typeof(FixtureTarget), nameof(FixtureTarget.Run))]
[HarmonyPatchCategory("fixture-category")]
[HarmonyDebug]
public static class CategorisedPatch
{
    [HarmonyPrefix]
    public static void Pre()
    {
    }
}

[HarmonyPatch(typeof(FixtureTarget))]
public static class ReversePatchSet
{
    [HarmonyReversePatch]
    [HarmonyPatch(nameof(FixtureTarget.Run))]
    public static void First()
    {
    }

    /// <summary>No target of its own; upstream's <c>lastOriginal</c> carries First's forward.</summary>
    [HarmonyReversePatch]
    public static void Second()
    {
    }
}

[HarmonyPatch(typeof(FixtureTarget), nameof(FixtureTarget.Run))]
public static class InnerPatchSet
{
    public static void InnerPrefix()
    {
    }
}

/// <summary>Type name as a string, which is what survives when the declaring type is an IL2CPP type
/// CoreCLR cannot resolve.</summary>
[HarmonyPatch("Game.Hidden.Type", "DoWork")]
public static class StringTypeNamePatch
{
    [HarmonyPrefix]
    public static void Pre()
    {
    }
}

// --- discoverability ----------------------------------------------------------------------------

/// <summary>Annotates only its methods, so <c>HasHarmonyAttribute</c> is false and PatchAll skips
/// the whole class.</summary>
public static class MethodOnlyPatch
{
    [HarmonyPatch(typeof(FixtureTarget), nameof(FixtureTarget.Run))]
    [HarmonyPrefix]
    public static void Pre()
    {
    }
}

[HarmonyPatch(typeof(FixtureTarget), nameof(FixtureTarget.Run))]
public class InheritedPatchBase
{
    [HarmonyPrefix]
    public static void BasePrefix()
    {
    }
}

/// <summary>Carries no annotation of its own, yet <c>GetFromType</c> passes <c>inherit: true</c> so
/// the base's class-level attributes still reach the container info.</summary>
public class InheritedPatchDerived : InheritedPatchBase
{
    [HarmonyPostfix]
    public static void DerivedPostfix()
    {
    }
}

public sealed class FixtureDerivedPatchAttribute : HarmonyPatch
{
    public FixtureDerivedPatchAttribute()
        : base(typeof(FixtureTarget), nameof(FixtureTarget.Run))
    {
    }
}

/// <summary>The subclass has an <c>info</c> field, so Harmony merges it and the class is
/// discoverable - but the values come from constructor IL.</summary>
[FixtureDerivedPatch]
public static class DerivedAttributePatch
{
    [HarmonyPrefix]
    public static void Pre()
    {
    }
}

[HarmonyPatch(typeof(FixtureTarget))]
public static class DelegateHolder
{
    /// <summary>A HarmonyDelegate target is a delegate type, never a patch method; it must not
    /// produce a descriptor or an issue of its own.</summary>
    [HarmonyDelegate(typeof(FixtureTarget), nameof(FixtureTarget.Run))]
    public delegate void RunDelegate();

    [HarmonyPrefix]
    [HarmonyPatch(nameof(FixtureTarget.Run))]
    public static void Pre()
    {
    }
}

/// <summary>An ordinary helper class with a method named <c>Prefix</c> and nothing Harmony-related.
/// The relevance gate has to leave it alone - this is how JAPatch callbacks are usually named.</summary>
public static class NotAPatchAtAll
{
    public static void Prefix()
    {
    }

    public static void Postfix()
    {
    }
}

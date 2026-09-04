using StArray.ModManager.Android.PcCompat;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace StArray.ModManager.Tests;

/// <summary>
/// Covers the claim that the write-through collection bridges are faithful stand-ins for the
/// <see cref="List{T}"/> members they replace when the list is not bound.
/// </summary>
/// <remarks>
/// <para>
/// This is the load-bearing assumption behind the rewriter matching mutators by element type alone
/// (see <c>CollectWritableCollectionMutations</c>). Every <c>List&lt;TMP_FontAsset&gt;::Add</c> in a
/// MOD is retargeted, including ones on lists the MOD created itself, on the grounds that an unbound
/// list is simply mutated in place. If that were not exactly true, the rewriter would be silently
/// changing the behaviour of unrelated MOD code, and it would need a stack-flow analysis instead.
/// </para>
/// <para>
/// The bound half - the write-through to the Il2Cpp collection - cannot be exercised here: building
/// an <c>Il2CppSystem.Collections.Generic.List&lt;T&gt;</c> needs a live IL2CPP runtime. That path is
/// only observable on a device, and is not claimed to be verified by these tests.
/// </para>
/// </remarks>
public sealed class PcCompatCollectionBridgeTests
{
    [Test]
    public void NullWritableCollectionUsesGeneratedCorlibCapacityConstructor()
    {
        using var module = ModuleDefMD.Load(typeof(PcCompatCollectionBridge).Assembly.Location);
        var bridge = module.Find(typeof(PcCompatCollectionBridge).FullName!, isReflectionName: false);
        var method = bridge!.Methods.Single(candidate =>
            candidate.Name == nameof(PcCompatCollectionBridge.CopyOrCreateBoundList));
        var constructors = method.Body!.Instructions
            .Where(instruction => instruction.OpCode == OpCodes.Newobj)
            .Select(instruction => instruction.Operand as IMethod)
            .Where(candidate => candidate is not null &&
                                candidate.Name == ".ctor" &&
                                candidate.DeclaringType.FullName ==
                                "Il2CppSystem.Collections.Generic.List`1<T>")
            .Cast<IMethod>()
            .ToArray();

        var parameterTypes = constructors.Single().MethodSig!.Params
            .Select(parameter => parameter.FullName)
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(constructors, Has.Length.EqualTo(1));
            Assert.That(parameterTypes, Is.EqualTo(new[] { "System.Int32" }));
        });
    }

    [Test]
    public void AddOnAnUnboundListBehavesLikeListAdd()
    {
        var list = new List<string> { "a" };

        PcCompatCollectionBridge.AddToBoundList(list, "b");

        Assert.That(list, Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void RemoveOnAnUnboundListReportsAndBehavesLikeListRemove()
    {
        var list = new List<string> { "a", "b" };

        Assert.Multiple(() =>
        {
            Assert.That(PcCompatCollectionBridge.RemoveFromBoundList(list, "a"), Is.True);
            Assert.That(PcCompatCollectionBridge.RemoveFromBoundList(list, "missing"), Is.False);
            Assert.That(list, Is.EqualTo(new[] { "b" }));
        });
    }

    [Test]
    public void ClearOnAnUnboundListBehavesLikeListClear()
    {
        var list = new List<string> { "a", "b" };

        PcCompatCollectionBridge.ClearBoundList(list);

        Assert.That(list, Is.Empty);
    }

    [Test]
    public void InsertOnAnUnboundListBehavesLikeListInsert()
    {
        var list = new List<string> { "a", "c" };

        PcCompatCollectionBridge.InsertIntoBoundList(list, 1, "b");

        Assert.That(list, Is.EqualTo(new[] { "a", "b", "c" }));
    }

    /// <summary>
    /// Out-of-range and null arguments must fail the way <see cref="List{T}"/> fails, so a MOD's own
    /// error handling still sees the exception type it was written against.
    /// </summary>
    [Test]
    public void UnboundMutatorsPropagateTheSameExceptionsAsList()
    {
        var list = new List<string>();

        Assert.Multiple(() =>
        {
            Assert.That(
                () => PcCompatCollectionBridge.InsertIntoBoundList(list, 5, "x"),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => PcCompatCollectionBridge.AddToBoundList<string>(null!, "x"),
                Throws.TypeOf<NullReferenceException>());
        });
    }

    /// <summary>
    /// A null Unity-side collection must not become an empty one: the setter's argument is what the
    /// MOD asked for, and Unity distinguishes "no fallback table" from "empty fallback table".
    /// </summary>
    /// <remarks>
    /// Invoked reflectively because the declared return type is
    /// <c>Il2CppSystem.Collections.Generic.List&lt;T&gt;</c>, and referencing <c>Il2Cppmscorlib</c>
    /// from the test project just to name it would pull the whole IL2CPP corlib into every test run.
    /// The null case is reachable without a runtime because the method returns before touching it.
    /// </remarks>
    [Test]
    public void ToIl2CppListPreservesNull()
    {
        var method = typeof(PcCompatCollectionBridge)
            .GetMethod(nameof(PcCompatCollectionBridge.ToIl2CppList))!
            .MakeGenericMethod(typeof(string));

        Assert.That(method.Invoke(null, [null]), Is.Null);
    }
}

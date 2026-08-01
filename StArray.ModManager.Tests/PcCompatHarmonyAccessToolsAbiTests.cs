using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using HarmonyLib;

namespace StArray.ModManager.Tests;

// The fixtures below are deliberately not nested inside the test class: the "Type:Member" overloads
// resolve their type half through AccessTools.TypeByName, and a plain namespace-qualified name is the
// spelling a MOD would actually write.

/// <summary>Nothing here is patched; the types exist only to be looked up by name.</summary>
public interface IAccessToolsSample
{
    string Interfaced(string value);
}

public class AccessToolsSampleBase : IAccessToolsSample
{
    public static string StaticField = "static-field";

    private string instanceField = "instance-field";

    public string Property { get; set; } = "property";

    private string PrivateProperty { get; set; } = "private-property";

    public event EventHandler? Happened;

    public string ReadFields() => instanceField + PrivateProperty;

    public void Raise() => Happened?.Invoke(this, EventArgs.Empty);

    public virtual string Virtual(string value) => "base:" + value;

    public string NonVirtual(string value) => "non-virtual:" + value;

    public static string Static(string value) => "static:" + value;

    public string Interfaced(string value) => "interfaced:" + value;

    public IEnumerable<int> Iterate()
    {
        yield return 1;
    }

    public async Task<int> Await()
    {
        await Task.Yield();
        return 1;
    }

    public Task NotAsync() => Task.CompletedTask;

    public class Nested
    {
        public class Deeper
        {
            public static string Marker = "deeper";
        }
    }
}

public class AccessToolsSampleDerived : AccessToolsSampleBase
{
    public override string Virtual(string value) => "derived:" + value;
}

public struct AccessToolsSampleStruct
{
    public string Read(string value) => "struct:" + value;
}

public delegate string StaticSampleDelegate(string value);

public delegate string OpenSampleDelegate(AccessToolsSampleBase instance, string value);

public delegate string ClosedSampleDelegate(string value);

public delegate string OpenInterfaceDelegate(IAccessToolsSample instance, string value);

public delegate string OpenStructDelegate(AccessToolsSampleStruct instance, string value);

[HarmonyDelegate(typeof(AccessToolsSampleBase), nameof(AccessToolsSampleBase.NonVirtual))]
public delegate string AnnotatedSampleDelegate(AccessToolsSampleBase instance, string value);

public delegate string UnannotatedSampleDelegate(AccessToolsSampleBase instance, string value);

#pragma warning restore CA1050

/// <summary>
/// Pins the AccessTools surface that compiling the upstream HarmonyTests corpus against this shim
/// found missing: the whole <c>"Type:Member"</c> overload family, <see cref="AccessToolsExtensions"/>,
/// the state-machine and reflection-identity helpers, and MethodDelegate/HarmonyDelegate.
///
/// The corpus lives outside the repository and cannot be a permanent test dependency, so the shapes
/// it exercised are re-stated here. Two behaviours are deliberate deviations from upstream and are
/// asserted as such: an unresolvable type half yields null plus a diagnostic instead of upstream's
/// NullReferenceException, and the two MethodDelegate shapes that upstream builds with emitted IL
/// throw rather than returning something subtly different.
/// </summary>
[NonParallelizable]
public class PcCompatHarmonyAccessToolsAbiTests
{
    private const string Sample = "StArray.ModManager.Tests.AccessToolsSampleBase";

    [SetUp]
    public void ResetDiagnostics() => HarmonyRegistry.ClearDiagnostics();

    private static string[] DiagnosticApis()
        => [.. HarmonyRegistry.SnapshotDiagnostics().Select(diagnostic => diagnostic.Api)];

    // ---- "Type:Member" parsing -------------------------------------------------------------

    [Test]
    public void TypeColonNameResolvesEveryMemberFamily()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AccessTools.DeclaredField($"{Sample}:instanceField"), Is.Not.Null);
            Assert.That(AccessTools.Field($"{Sample}:StaticField"), Is.Not.Null);
            Assert.That(AccessTools.DeclaredProperty($"{Sample}:PrivateProperty"), Is.Not.Null);
            Assert.That(AccessTools.Property($"{Sample}:Property"), Is.Not.Null);
            Assert.That(AccessTools.DeclaredEvent($"{Sample}:Happened"), Is.Not.Null);
            Assert.That(AccessTools.Event($"{Sample}:Happened"), Is.Not.Null);
            Assert.That(AccessTools.DeclaredMethod($"{Sample}:NonVirtual"), Is.Not.Null);
            Assert.That(AccessTools.Method($"{Sample}:Static"), Is.Not.Null);
        });
    }

    [Test]
    public void TypeColonNameResolvesAccessors()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AccessTools.DeclaredPropertyGetter($"{Sample}:Property")!.Name, Is.EqualTo("get_Property"));
            Assert.That(AccessTools.DeclaredPropertySetter($"{Sample}:Property")!.Name, Is.EqualTo("set_Property"));
            Assert.That(AccessTools.PropertyGetter($"{Sample}:Property")!.Name, Is.EqualTo("get_Property"));
            Assert.That(AccessTools.PropertySetter($"{Sample}:Property")!.Name, Is.EqualTo("set_Property"));
            Assert.That(AccessTools.DeclaredEventAdder($"{Sample}:Happened")!.Name, Is.EqualTo("add_Happened"));
            Assert.That(AccessTools.DeclaredEventRemover($"{Sample}:Happened")!.Name, Is.EqualTo("remove_Happened"));
            Assert.That(AccessTools.EventAdder($"{Sample}:Happened")!.Name, Is.EqualTo("add_Happened"));
            Assert.That(AccessTools.EventRemover($"{Sample}:Happened")!.Name, Is.EqualTo("remove_Happened"));
        });
    }

    [Test]
    public void TypeColonNameLookupWalksUpTheHierarchyOnlyForTheNonDeclaredSpelling()
    {
        const string derived = "StArray.ModManager.Tests.AccessToolsSampleDerived";
        Assert.Multiple(() =>
        {
            Assert.That(AccessTools.DeclaredMethod($"{derived}:NonVirtual"), Is.Null);
            Assert.That(AccessTools.Method($"{derived}:NonVirtual"), Is.Not.Null);
        });
    }

    [Test]
    public void MalformedTypeColonNameThrowsUpstreamsArgumentException()
    {
        // The message is upstream's verbatim, unbalanced quote and leading space included: a MOD that
        // matches on it must keep matching.
        var exception = Assert.Throws<ArgumentException>(() => AccessTools.Method("NoColonHere"));
        Assert.Multiple(() =>
        {
            Assert.That(exception!.ParamName, Is.EqualTo("typeColonName"));
            Assert.That(exception.Message, Does.Contain(" must be specified as 'Namespace.Type1.Type2:MemberName"));
        });

        Assert.Throws<ArgumentException>(() => AccessTools.Field("Too:Many:Colons"));
        Assert.Throws<ArgumentNullException>(() => AccessTools.Property(null!));
    }

    [Test]
    public void UnresolvableTypeHalfYieldsNullAndADiagnosticForEveryFamily()
    {
        // Upstream returns null from the method family but dereferences the unresolved type in the
        // field/property/event family, so those throw NullReferenceException from inside AccessTools.
        // Here a type that resolves on desktop can genuinely be absent, and an NRE carries no MOD
        // frame, so every family records the miss and hands back null instead.
        Assert.Multiple(() =>
        {
            Assert.That(AccessTools.DeclaredField("No.Such.Type:Member"), Is.Null);
            Assert.That(AccessTools.Field("No.Such.Type:Member"), Is.Null);
            Assert.That(AccessTools.DeclaredProperty("No.Such.Type:Member"), Is.Null);
            Assert.That(AccessTools.Property("No.Such.Type:Member"), Is.Null);
            Assert.That(AccessTools.DeclaredEvent("No.Such.Type:Member"), Is.Null);
            Assert.That(AccessTools.Event("No.Such.Type:Member"), Is.Null);
            Assert.That(AccessTools.DeclaredMethod("No.Such.Type:Member"), Is.Null);
            Assert.That(AccessTools.Method("No.Such.Type:Member"), Is.Null);
            Assert.That(AccessTools.PropertyGetter("No.Such.Type:Member"), Is.Null);
        });

        var diagnostics = HarmonyRegistry.SnapshotDiagnostics();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostics.Select(diagnostic => diagnostic.Code).Distinct(),
                Is.EqualTo(new[] { "HarmonyUnresolvedDeclaringType" }));
            Assert.That(diagnostics.Select(diagnostic => diagnostic.Api), Does.Contain("AccessTools.DeclaredField"));
            Assert.That(diagnostics.Select(diagnostic => diagnostic.Api), Does.Contain("AccessTools.Method"));
            Assert.That(diagnostics[0].Detail, Does.Contain("No.Such.Type"));
        });
    }

    // ---- CodeInstruction.Call(string) ------------------------------------------------------

    [Test]
    public void CodeInstructionCallAcceptsTypeColonName()
    {
        var instruction = CodeInstruction.Call($"{Sample}:Static");
        Assert.Multiple(() =>
        {
            Assert.That(instruction.opcode, Is.EqualTo(OpCodes.Call));
            Assert.That(instruction.operand, Is.EqualTo(AccessTools.Method(typeof(AccessToolsSampleBase), "Static")));
        });

        Assert.Throws<ArgumentException>(() => CodeInstruction.Call($"{Sample}:NoSuchMethod"));
    }

    // ---- AccessToolsExtensions -------------------------------------------------------------

    [Test]
    public void ExtensionSpellingForwardsToTheStatics()
    {
        var type = typeof(AccessToolsSampleBase);
        Assert.Multiple(() =>
        {
            Assert.That(type.Method("NonVirtual"), Is.EqualTo(AccessTools.Method(type, "NonVirtual")));
            Assert.That(type.Field("instanceField"), Is.EqualTo(AccessTools.Field(type, "instanceField")));
            Assert.That(type.PropertyGetter("Property"), Is.EqualTo(AccessTools.PropertyGetter(type, "Property")));
            Assert.That(type.InnerTypes(), Is.EqualTo(AccessTools.InnerTypes(type)));
            Assert.That(type.Inner("Nested"), Is.EqualTo(typeof(AccessToolsSampleBase.Nested)));
            Assert.That(type.IsStruct(), Is.False);
            Assert.That(typeof(AccessToolsSampleStruct).IsStruct(), Is.True);
            Assert.That(type.GetDeclaredMethods(), Is.EqualTo(AccessTools.GetDeclaredMethods(type)));
        });
    }

    // ---- type search / inner types ---------------------------------------------------------

    [Test]
    public void TypeSearchMatchesOnFullNameFirstAndCachesUntilInvalidated()
    {
        var byFullName = AccessTools.TypeSearch(new Regex("^StArray\\.ModManager\\.Tests\\.AccessToolsSampleStruct$"));
        Assert.That(byFullName, Is.EqualTo(typeof(AccessToolsSampleStruct)));

        // The cache is a plain snapshot of the loaded assemblies, so the same query answers the same
        // way; invalidating it must not change the answer either.
        Assert.Multiple(() =>
        {
            Assert.That(AccessTools.TypeSearch(new Regex("^AccessToolsSampleStruct$")), Is.EqualTo(typeof(AccessToolsSampleStruct)));
            Assert.That(AccessTools.TypeSearch(new Regex("^AccessToolsSampleStruct$"), invalidateCache: true), Is.EqualTo(typeof(AccessToolsSampleStruct)));
            Assert.That(AccessTools.TypeSearch(new Regex("^NothingMatchesThisAtAll$")), Is.Null);
        });

        AccessTools.ClearTypeSearchCache();
        Assert.That(AccessTools.TypeSearch(new Regex("^AccessToolsSampleStruct$")), Is.EqualTo(typeof(AccessToolsSampleStruct)));
    }

    [Test]
    public void InnerTypeHelpersDescendAndFilter()
    {
        var type = typeof(AccessToolsSampleBase);
        Assert.Multiple(() =>
        {
            Assert.That(AccessTools.Inner(type, "Nested"), Is.EqualTo(typeof(AccessToolsSampleBase.Nested)));
            Assert.That(AccessTools.Inner(type, "Deeper"), Is.Null, "Inner is one level only");
            Assert.That(AccessTools.FirstInner(type, candidate => candidate.Name == "Nested"),
                Is.EqualTo(typeof(AccessToolsSampleBase.Nested)));

            // FindIncludingInnerTypes recurses, so it reaches the second level Inner cannot.
            Assert.That(
                AccessTools.FindIncludingInnerTypes(type, candidate => candidate.Name == "Deeper" ? candidate : null),
                Is.EqualTo(typeof(AccessToolsSampleBase.Nested.Deeper)));

            Assert.That(AccessTools.FirstMethod(type, method => method.Name == "NonVirtual"), Is.Not.Null);
            Assert.That(AccessTools.FirstProperty(type, property => property.Name == "Property"), Is.Not.Null);
            Assert.That(AccessTools.FirstConstructor(type, constructor => constructor.GetParameters().Length == 0), Is.Not.Null);
        });
    }

    // ---- state machines --------------------------------------------------------------------

    [Test]
    public void StateMachineHelpersReadTheCompilerAttribute()
    {
        var iterator = AccessTools.Method(typeof(AccessToolsSampleBase), nameof(AccessToolsSampleBase.Iterate));
        var asyncMethod = AccessTools.Method(typeof(AccessToolsSampleBase), nameof(AccessToolsSampleBase.Await));
        var plain = AccessTools.Method(typeof(AccessToolsSampleBase), nameof(AccessToolsSampleBase.NonVirtual));
        var fakeAsync = AccessTools.Method(typeof(AccessToolsSampleBase), nameof(AccessToolsSampleBase.NotAsync));

        Assert.Multiple(() =>
        {
            Assert.That(AccessTools.EnumeratorMoveNext(iterator)!.Name, Is.EqualTo("MoveNext"));
            Assert.That(AccessTools.EnumeratorMoveNext(iterator)!.DeclaringType!.Name, Does.StartWith("<Iterate>d__"));
            Assert.That(AccessTools.AsyncMoveNext(asyncMethod)!.Name, Is.EqualTo("MoveNext"));
            Assert.That(AccessTools.AsyncMoveNext(asyncMethod)!.DeclaringType!.Name, Does.StartWith("<Await>d__"));

            // No attribute means no answer - never a guess.
            Assert.That(AccessTools.EnumeratorMoveNext(plain), Is.Null);
            Assert.That(AccessTools.AsyncMoveNext(fakeAsync), Is.Null);
            Assert.That(AccessTools.EnumeratorMoveNext(null), Is.Null);
        });
    }

    // ---- reflection identity ---------------------------------------------------------------

    [Test]
    public void DeclaredMemberNormalisesTheReflectedType()
    {
        var throughDerived = typeof(AccessToolsSampleDerived).GetMethod(nameof(AccessToolsSampleBase.NonVirtual))!;
        var declared = throughDerived.GetDeclaredMember();

        Assert.Multiple(() =>
        {
            Assert.That(throughDerived.IsDeclaredMember(), Is.False);
            Assert.That(declared.IsDeclaredMember(), Is.True);
            Assert.That(declared.DeclaringType, Is.EqualTo(typeof(AccessToolsSampleBase)));
            Assert.That(declared.MetadataToken, Is.EqualTo(throughDerived.MetadataToken));

            // Already-declared members come back untouched.
            var direct = typeof(AccessToolsSampleBase).GetMethod(nameof(AccessToolsSampleBase.NonVirtual))!;
            Assert.That(direct.GetDeclaredMember(), Is.SameAs(direct));

            // Identifiable is upstream's MonoMod handle lookup; nothing here detours through MonoMod.
            Assert.That(direct.Identifiable(), Is.SameAs(direct));
        });
    }

    [Test]
    public void SmallHelpersMatchUpstreamSemantics()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AccessTools.GetTypes(null), Is.Empty);
            Assert.That(AccessTools.GetTypes(["text", null, 1]), Is.EqualTo(new[] { typeof(string), typeof(object), typeof(int) }));

            Assert.That(AccessTools.IsStruct(typeof(AccessToolsSampleStruct)), Is.True);
            Assert.That(AccessTools.IsStruct(typeof(int)), Is.False, "primitives are values, not structs, upstream");
            Assert.That(AccessTools.IsClass(typeof(AccessToolsSampleBase)), Is.True);
            Assert.That(AccessTools.IsValue(typeof(DayOfWeek)), Is.True);
            Assert.That(AccessTools.IsVoid(typeof(void)), Is.True);
            Assert.That(AccessTools.IsNumber(typeof(double)), Is.True);
            Assert.That(AccessTools.IsInteger(typeof(long)), Is.True);
            Assert.That(AccessTools.IsFloatingPoint(typeof(decimal)), Is.True);
            Assert.That(AccessTools.IsOfNullableType<int?>(null), Is.True);
            Assert.That(AccessTools.IsOfNullableType(1), Is.False);

            // Stable and order-sensitive, because upstream uses it as a cache key.
            var forward = AccessTools.CombinedHashCode(["a", "b"]);
            var again = AccessTools.CombinedHashCode([string.Concat("a"), string.Concat("b")]);
            var reversed = AccessTools.CombinedHashCode(["b", "a"]);
            Assert.That(again, Is.EqualTo(forward));
            Assert.That(reversed, Is.Not.EqualTo(forward));
        });

        Assert.Throws<MissingMemberException>(() => AccessTools.ThrowMissingMemberException(typeof(AccessToolsSampleBase), "nope"));

        // The first frame outside HarmonyLib is this test method itself.
        Assert.That(AccessTools.GetOutsideCaller().Name, Is.EqualTo(nameof(SmallHelpersMatchUpstreamSemantics)));
    }

    [Test]
    public void RethrowExceptionPreservesTheOriginalStack()
    {
        Exception captured;
        try
        {
            throw new InvalidOperationException("original");
        }
        catch (Exception exception)
        {
            captured = exception;
        }

        var rethrown = Assert.Throws<InvalidOperationException>(() => AccessTools.RethrowException(captured));
        Assert.Multiple(() =>
        {
            Assert.That(rethrown, Is.SameAs(captured));
            Assert.That(rethrown!.StackTrace, Does.Contain(nameof(RethrowExceptionPreservesTheOriginalStack)));
        });
    }

    // ---- MethodDelegate --------------------------------------------------------------------

    private static MethodInfo MethodOf(string name)
        => AccessTools.Method(typeof(AccessToolsSampleBase), name)!;

    [Test]
    public void MethodDelegateBindsTheShapesThatNeedNoEmittedIl()
    {
        var derived = new AccessToolsSampleDerived();

        Assert.Multiple(() =>
        {
            // static
            Assert.That(AccessTools.MethodDelegate<StaticSampleDelegate>(MethodOf(nameof(AccessToolsSampleBase.Static)))("x"),
                Is.EqualTo("static:x"));

            // open instance, virtual call -> dispatches on the runtime type
            Assert.That(AccessTools.MethodDelegate<OpenSampleDelegate>(MethodOf(nameof(AccessToolsSampleBase.Virtual)))(derived, "x"),
                Is.EqualTo("derived:x"));

            // closed instance, virtual call
            Assert.That(AccessTools.MethodDelegate<ClosedSampleDelegate>(MethodOf(nameof(AccessToolsSampleBase.Virtual)), derived)("x"),
                Is.EqualTo("derived:x"));

            // closed instance, non-virtual call -> the declaring type's own body, built over a
            // function pointer exactly as upstream does off Mono
            Assert.That(AccessTools.MethodDelegate<ClosedSampleDelegate>(MethodOf(nameof(AccessToolsSampleBase.Virtual)), derived, virtualCall: false)("x"),
                Is.EqualTo("base:x"));

            // delegate instance type is an interface -> resolved through the interface map
            Assert.That(AccessTools.MethodDelegate<OpenInterfaceDelegate>(MethodOf(nameof(AccessToolsSampleBase.Interfaced)))(derived, "x"),
                Is.EqualTo("interfaced:x"));

            // the interface method itself
            Assert.That(AccessTools.MethodDelegate<OpenInterfaceDelegate>(
                AccessTools.Method(typeof(IAccessToolsSample), nameof(IAccessToolsSample.Interfaced))!)(derived, "x"),
                Is.EqualTo("interfaced:x"));

            // "Type:Member" spelling
            Assert.That(AccessTools.MethodDelegate<StaticSampleDelegate>($"{Sample}:Static")("x"), Is.EqualTo("static:x"));
        });
    }

    [Test]
    public void MethodDelegateRefusesTheShapesUpstreamBuildsWithEmittedIl()
    {
        // An instance method on a struct takes its receiver by ref, so no plain delegate signature
        // fits and upstream emits a thunk. A struct-typed delegate instance over an interface method
        // is the same story after upstream's remap.
        var structMethod = AccessTools.Method(typeof(AccessToolsSampleStruct), nameof(AccessToolsSampleStruct.Read))!;
        var onStruct = Assert.Throws<NotSupportedException>(
            () => AccessTools.MethodDelegate<OpenStructDelegate>(structMethod));

        // Open instance + non-virtual is the other emitted shape.
        var openNonVirtual = Assert.Throws<NotSupportedException>(
            () => AccessTools.MethodDelegate<OpenSampleDelegate>(MethodOf(nameof(AccessToolsSampleBase.Virtual)), null, virtualCall: false));

        Assert.Multiple(() =>
        {
            Assert.That(onStruct!.Message, Does.Contain("struct"));
            Assert.That(openNonVirtual!.Message, Does.Contain("open-instance non-virtual"));

            // Refusals are never silent - the host exports them with everything else.
            Assert.That(DiagnosticApis(), Has.Some.EqualTo("AccessTools.MethodDelegate"));
        });
    }

    [Test]
    public void MethodDelegateRejectsBadArgumentsLikeUpstream()
    {
        Assert.Throws<ArgumentNullException>(() => AccessTools.MethodDelegate<StaticSampleDelegate>((MethodInfo)null!));

        // Interface methods have no non-virtual entry point to bind.
        Assert.Throws<ArgumentException>(() => AccessTools.MethodDelegate<OpenInterfaceDelegate>(
            AccessTools.Method(typeof(IAccessToolsSample), nameof(IAccessToolsSample.Interfaced))!,
            null,
            virtualCall: false));

        Assert.Throws<ArgumentNullException>(() => AccessTools.MethodDelegate<StaticSampleDelegate>($"{Sample}:NoSuchMethod"));
    }

    [Test]
    public void RuntimePatchingResolvesAManagedIteratorTargetToItsMoveNext()
    {
        // [HarmonyPatch(..., MethodType.Enumerator)] used to be fail-closed everywhere. The runtime
        // path can now answer whenever the target is managed, because the compiler left the state
        // machine attribute on it - the same MoveNext upstream finds by reading the iterator's IL.
        // The static metadata scanner still cannot (see PcCompatHarmonyAttributeAggregatorTests):
        // in production the target is an IL2CPP method with no managed metadata to carry it.
        HarmonyRegistry.ClearRegisteredPatches();
        new Harmony("accesstools-abi-tests")
            .CreateClassProcessor(typeof(HarmonyFixtures.EnumeratorPatch))
            .Patch();

        var record = HarmonyRegistry.SnapshotRegisteredPatches().Single();
        Assert.Multiple(() =>
        {
            Assert.That(record.TargetMethod, Is.EqualTo("MoveNext"));
            Assert.That(record.TargetType, Does.Contain("<Iterate>d__"));
            Assert.That(record.OriginalMethod, Is.Not.Null);
            Assert.That(record.Status, Is.EqualTo(HarmonyRegistry.StatusRegistered));
        });

        HarmonyRegistry.ClearRegisteredPatches();
    }

    [Test]
    public void HarmonyDelegateReadsTheAnnotationOnTheDelegateType()
    {
        var bound = AccessTools.HarmonyDelegate<AnnotatedSampleDelegate>();
        Assert.That(bound(new AccessToolsSampleDerived(), "x"), Is.EqualTo("non-virtual:x"));

        Assert.Throws<NullReferenceException>(() => AccessTools.HarmonyDelegate<UnannotatedSampleDelegate>());
    }
}

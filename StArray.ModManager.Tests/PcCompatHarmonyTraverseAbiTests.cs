using System.Collections;
using HarmonyLib;

namespace StArray.ModManager.Tests;

// Traverse is pure reflection upstream - no IL is read or emitted - so the shim mirrors it verbatim.
// These cases are upstream's HarmonyTests/Traverse suite recast onto self-contained fixtures, plus
// the AccessTools.MakeDeepCopy cases upstream has no tests for. MakeDeepCopy is the one member here
// that could not be copied literally: upstream calls a collection's Add through an emitted
// FastInvokeHandler and this host has to call it through reflection, so the last test pins the only
// observable seam that substitution creates.
public class PcCompatHarmonyTraverseAbiTests
{
#pragma warning disable CS0414
#pragma warning disable IDE0052

    private static readonly string[] TestStrings = ["test01", "test02", "test03", "test04"];
    private static readonly string[] FieldNames = ["publicField", "privateField", "protectedField", "internalField"];
    private static readonly string[] InternalFieldNames = ["_root", "_type", "_info", "_method", "_params"];

    private enum Flavour { Third = 3 }

    private class AccessModifiers(string[] s)
    {
        public string publicField = s[0];
        readonly string privateField = s[1];
        protected string protectedField = s[2];
        internal string internalField = s[3];

        public string? GetTestField(int n) => n switch
        {
            0 => publicField,
            1 => privateField,
            2 => protectedField,
            3 => internalField,
            _ => null
        };

        public override string ToString() => "AccessModifiers";
    }

    private class PropertyAccessModifiers(string[] s)
    {
        private string backingPublic = s[0];
        private string backingPrivate = s[1];

        public string PublicProperty { get => backingPublic; set => backingPublic = value; }
        private string PrivateProperty { get => backingPrivate; set => backingPrivate = value; }
        public string GetOnlyProperty => "get-only";
    }

    private static class StaticProperties
    {
        static string StaticProperty => "static-property";
    }

    private class InstanceMethods
    {
        public bool VoidCalled;

        void Void() => VoidCalled = true;

        string Doubled(string arg) => arg + arg;

        public bool Overloaded(string p1, bool p2 = true) => !p2;

        public int Overloaded(string p1, int p2, bool p3 = true) => 0;
    }

    private static class StaticMethods
    {
        static int Multiply(int a, int b) => a * b;

        static string WithOutParameter(out string value)
        {
            value = "hello";
            return "ok";
        }

        static string WithRefParameter(ref string value)
        {
            value = "world";
            return "ok";
        }
    }

    private class Branch
    {
        static readonly string staticField = "static-1";
        readonly string branchField = "branch-field";
    }

    private class Leaf(string value)
    {
        public readonly string someString = value;
        public readonly Branch branch = new();
    }

    private static class LeafHolder
    {
        public static readonly Leaf leaf = new("leaf-2");
    }

    private class Nest
    {
        internal static class Inner1
        {
            internal static class Inner2
            {
                internal static string field = "nested";
            }
        }
    }

    private class Writeability
    {
        public const string ConstField = "const";
        public static readonly string StaticReadonlyField = "static-readonly";
        public readonly string InstanceReadonlyField = "instance-readonly";
        public string MutableField = "mutable";
    }

    private class Empty
    {
        readonly string field = null!;
    }

    private class Node
    {
        public string Name = "";
        public int Count;
        public Flavour Flavour;
        public Node? Child;
        public List<string> Tags = [];
        public string[] Codes = [];
        public DateTime Stamp;
    }

    // Deep-copying into this type reaches the generic-collection branch and then fails inside Add.
    private class RefusingCollection<T> : IEnumerable<T>
    {
        public void Add(T item) => throw new InvalidOperationException("add refused");

        public IEnumerator<T> GetEnumerator()
        {
            yield break;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

#pragma warning restore IDE0052
#pragma warning restore CS0414

    private static void AssertIsEmpty(Traverse traverse)
    {
        Assert.Multiple(() =>
        {
            foreach (var name in InternalFieldNames)
                Assert.That(AccessTools.DeclaredField(typeof(Traverse), name)!.GetValue(traverse), Is.Null, name);
        });
    }

    [Test]
    public void TraverseKeepsTheInternalFieldNamesUpstreamTestsProbe()
    {
        Assert.Multiple(() =>
        {
            foreach (var name in InternalFieldNames)
                Assert.That(AccessTools.DeclaredField(typeof(Traverse), name), Is.Not.Null, name);
        });
    }

    [Test]
    public void EmptyTraversesAreReturnedInsteadOfNulls()
    {
        AssertIsEmpty(new Traverse((Type)null!));
        AssertIsEmpty(Traverse.Create((Type)null!));

        // An unresolvable inner type, an instance field reached without an instance, and a method
        // looked up on a null value all degrade to the same empty traverse.
        AssertIsEmpty(Traverse.Create((Type)null!).Type("FooBar"));
        AssertIsEmpty(Traverse.Create<Empty>().Field("field"));

        var fieldOfNull = new Traverse(new Empty()).Field("field");
        AssertIsEmpty(fieldOfNull.Method("", []));
        AssertIsEmpty(fieldOfNull.Method("", [], []));
    }

    [Test]
    public void CreateWithNullSurvivesEveryAccessor()
    {
        var trv = Traverse.Create((Type)null!);
        Assert.That(trv.ToString(), Is.Null);

        var field = trv.Field("foo");
        var property = trv.Property("foo");
        var method = trv.Method("zee");

        Assert.Multiple(() =>
        {
            Assert.That(field.GetValue(), Is.Null);
            Assert.That(field.ToString(), Is.Null);
            Assert.That(field.GetValue<int>(), Is.EqualTo(0));
            Assert.That(field.SetValue(123), Is.SameAs(field));

            Assert.That(property.GetValue(), Is.Null);
            Assert.That(property.GetValue<string>(), Is.Null);
            Assert.That(property.SetValue("test"), Is.SameAs(property));

            Assert.That(method.GetValue(), Is.Null);
            Assert.That(method.GetValue<float>(), Is.EqualTo(0f));
            Assert.That(method.SetValue(null), Is.SameAs(method));
        });
    }

    [Test]
    public void ToStringReportsTheInstanceTypeOrTraversedValue()
    {
        var instance = new AccessModifiers(TestStrings);

        Assert.Multiple(() =>
        {
            Assert.That(Traverse.Create(instance).ToString(), Is.EqualTo(instance.ToString()));
            Assert.That(Traverse.Create(typeof(AccessModifiers)).ToString(), Is.EqualTo(typeof(AccessModifiers).ToString()));
            Assert.That(Traverse.Create(instance).Field(FieldNames[0]).ToString(), Is.EqualTo(TestStrings[0]));
        });
    }

    [Test]
    public void FieldsReadAndWriteThroughEveryAccessModifier()
    {
        var instance = new AccessModifiers(TestStrings);
        var trv = Traverse.Create(instance);

        Assert.Multiple(() =>
        {
            for (var i = 0; i < TestStrings.Length; i++)
            {
                var field = trv.Field(FieldNames[i]);
                Assert.That(field.GetValue(), Is.EqualTo(TestStrings[i]), FieldNames[i]);
                Assert.That(field.GetValue<string>(), Is.EqualTo(TestStrings[i]), FieldNames[i]);

                var newValue = "newvalue" + i;
                _ = field.SetValue(newValue);
                Assert.That(instance.GetTestField(i), Is.EqualTo(newValue), FieldNames[i]);
                Assert.That(field.GetValue<string>(), Is.EqualTo(newValue), FieldNames[i]);
            }
        });
    }

    [Test]
    public void StaticFieldsResolveFromInstanceAndFromType()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Traverse.Create(new Branch()).Field("staticField").GetValue(), Is.EqualTo("static-1"));
            Assert.That(Traverse.Create(typeof(LeafHolder)).Field("leaf").GetValue(), Is.SameAs(LeafHolder.leaf));
        });
    }

    [Test]
    public void FieldChainsWalkStaticAndInstanceHops()
    {
        // static field -> instance field
        var fromType = Traverse.Create(typeof(LeafHolder)).Field("leaf");
        // instance field -> instance field -> field on the nested object
        var fromInstance = Traverse.Create(new Leaf("leaf-1")).Field("branch").Field("branchField");

        Assert.Multiple(() =>
        {
            Assert.That(fromType.GetValue()!.GetType(), Is.EqualTo(typeof(Leaf)));
            Assert.That(fromType.Field("someString").GetValue(), Is.EqualTo("leaf-2"));
            Assert.That(fromInstance.GetValue(), Is.EqualTo("branch-field"));
        });
    }

    [Test]
    public void PropertiesReadAndWriteThroughEveryAccessModifier()
    {
        var instance = new PropertyAccessModifiers(TestStrings);
        var trv = Traverse.Create(instance);

        Assert.Multiple(() =>
        {
            Assert.That(trv.Property("PublicProperty").GetValue<string>(), Is.EqualTo(TestStrings[0]));
            Assert.That(trv.Property("PrivateProperty").GetValue<string>(), Is.EqualTo(TestStrings[1]));
            Assert.That(trv.Property("PublicProperty").ToString(), Is.EqualTo(TestStrings[0]));

            _ = trv.Property("PublicProperty").SetValue("changed-public");
            _ = trv.Property("PrivateProperty").SetValue("changed-private");
            Assert.That(instance.PublicProperty, Is.EqualTo("changed-public"));
            Assert.That(trv.Property("PrivateProperty").GetValue<string>(), Is.EqualTo("changed-private"));

            Assert.That(Traverse.Create(typeof(StaticProperties)).Property("StaticProperty").GetValue(), Is.EqualTo("static-property"));
        });
    }

    [Test]
    public void MethodsCoverInstanceStaticOutRefAndOverloads()
    {
        var instance = new InstanceMethods();
        var instanceTrv = Traverse.Create(instance);
        var staticTrv = Traverse.Create(typeof(StaticMethods));

        var voidResult = instanceTrv.Method("Void").GetValue();

        var byRefTypes = new[] { typeof(string).MakeByRefType() };
        var outParams = new object?[] { null };
        var outResult = staticTrv.Method("WithOutParameter", byRefTypes, outParams!).GetValue<string>();
        var refParams = new object?[] { null };
        var refResult = staticTrv.Method("WithRefParameter", byRefTypes, refParams!).GetValue<string>();

        var overload = instanceTrv.Method("Overloaded", [typeof(string), typeof(bool)]);

        Assert.Multiple(() =>
        {
            Assert.That(voidResult, Is.Null);
            Assert.That(instance.VoidCalled, Is.True);
            Assert.That(instanceTrv.Method("Doubled", ["arg"]).GetValue(), Is.EqualTo("argarg"));
            Assert.That(staticTrv.Method("Multiply", 6, 7).GetValue<int>(), Is.EqualTo(42));

            Assert.That(outResult, Is.EqualTo("ok"));
            Assert.That(outParams[0], Is.EqualTo("hello"));
            Assert.That(refResult, Is.EqualTo("ok"));
            Assert.That(refParams[0], Is.EqualTo("world"));

            // The same traverse can be re-invoked with different arguments.
            Assert.That(overload.GetValue<bool>("test", false), Is.True);
            Assert.That(overload.GetValue<bool>("test", true), Is.False);
        });
    }

    [Test]
    public void MemberProbesAndEnumerationsReportTheTraversedShape()
    {
        var instance = new AccessModifiers(TestStrings);
        var field = Traverse.Create(instance).Field("publicField");
        var property = Traverse.Create(new PropertyAccessModifiers(TestStrings)).Property("PublicProperty");
        var method = Traverse.Create(typeof(StaticMethods)).Method("Multiply", 2, 3);

        Assert.Multiple(() =>
        {
            Assert.That(field.IsField, Is.True);
            Assert.That(field.IsProperty, Is.False);
            Assert.That(field.FieldExists(), Is.True);
            Assert.That(field.MethodExists(), Is.False);
            Assert.That(field.TypeExists(), Is.True);
            Assert.That(field.GetValueType(), Is.EqualTo(typeof(string)));

            Assert.That(property.IsProperty, Is.True);
            Assert.That(property.PropertyExists(), Is.True);
            Assert.That(property.GetValueType(), Is.EqualTo(typeof(string)));

            Assert.That(method.MethodExists(), Is.True);
            Assert.That(method.FieldExists(), Is.False);
            // Neither a field nor a property, so there is no member type to report.
            Assert.That(method.GetValueType(), Is.Null);

            Assert.That(Traverse.Create(instance).Fields(), Is.EquivalentTo(FieldNames));
            Assert.That(Traverse.Create(new PropertyAccessModifiers(TestStrings)).Properties(),
                Is.EquivalentTo(new[] { "PublicProperty", "PrivateProperty", "GetOnlyProperty" }));
            Assert.That(Traverse.Create(new InstanceMethods()).Methods(), Does.Contain("Doubled"));
        });
    }

    [Test]
    public void IsWriteableRejectsConstStaticReadonlyAndGetOnly()
    {
        var trv = Traverse.Create(new Writeability());
        var properties = Traverse.Create(new PropertyAccessModifiers(TestStrings));

        Assert.Multiple(() =>
        {
            Assert.That(trv.Field(nameof(Writeability.ConstField)).IsWriteable, Is.False);
            Assert.That(trv.Field(nameof(Writeability.StaticReadonlyField)).IsWriteable, Is.False);
            // Instance readonly fields stay writeable - only const and static readonly are refused.
            Assert.That(trv.Field(nameof(Writeability.InstanceReadonlyField)).IsWriteable, Is.True);
            Assert.That(trv.Field(nameof(Writeability.MutableField)).IsWriteable, Is.True);

            Assert.That(properties.Property("GetOnlyProperty").IsWriteable, Is.False);
            Assert.That(properties.Property("PublicProperty").IsWriteable, Is.True);
        });
    }

    [Test]
    public void TypeNavigatesIntoNestedTypes()
    {
        var field = Traverse.Create(typeof(Nest)).Type("Inner1").Type("Inner2").Field("field");

        Assert.That(field.GetValue<string>(), Is.EqualTo("nested"));

        _ = field.SetValue("nested-2");
        Assert.That(Traverse.Create(typeof(Nest)).Type("Inner1").Type("Inner2").Field("field").GetValue(),
            Is.EqualTo("nested-2"));
    }

    [Test]
    public void IterateFieldsAndCopyFieldsMoveValuesBetweenInstances()
    {
        var source = new AccessModifiers(TestStrings);
        var target = new AccessModifiers(["a", "b", "c", "d"]);
        var visited = new List<string>();

        Traverse.IterateFields(source, target, Traverse.CopyFields);
        Traverse.IterateFields(source, target, (name, _, _) => visited.Add(name));

        Assert.Multiple(() =>
        {
            for (var i = 0; i < TestStrings.Length; i++)
                Assert.That(target.GetTestField(i), Is.EqualTo(TestStrings[i]), FieldNames[i]);
            Assert.That(visited, Is.EquivalentTo(FieldNames));
        });
    }

    [Test]
    public void TraverseOfTExposesTheValueAsTypedProperty()
    {
        var instance = new AccessModifiers(TestStrings);
        var typed = Traverse.Create(instance).Field<string>("publicField");

        Assert.That(typed.Value, Is.EqualTo(TestStrings[0]));

        typed.Value = "typed-write";
        Assert.That(instance.publicField, Is.EqualTo("typed-write"));
    }

    [Test]
    public void GetValueWithArgumentsAndSetValueRejectMissingOrMethodTargets()
    {
        var notAMethod = Traverse.Create(new AccessModifiers(TestStrings));
        var method = Traverse.Create(typeof(StaticMethods)).Method("Multiply", 6, 7);

        Assert.Multiple(() =>
        {
            Assert.That(Assert.Throws<Exception>(() => notAMethod.GetValue(["x"]))!.Message,
                Is.EqualTo("cannot get method value without method"));
            Assert.That(Assert.Throws<Exception>(() => notAMethod.GetValue<string>(["x"]))!.Message,
                Is.EqualTo("cannot get method value without method"));
            Assert.That(Assert.Throws<Exception>(() => method.SetValue(1))!.Message,
                Does.StartWith("cannot set value of method"));
        });
    }

    [Test]
    public void MakeDeepCopyClonesGraphsAndSharesSystemTypes()
    {
        var child = new Node { Name = "child", Count = 2 };
        var source = new Node
        {
            Name = "root",
            Count = 1,
            Flavour = Flavour.Third,
            Child = child,
            Tags = ["a", "b"],
            Codes = ["x", "y"],
            Stamp = new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc)
        };

        var copy = AccessTools.MakeDeepCopy<Node>(source);
        AccessTools.MakeDeepCopy<Node>(source, out var viaOut);
        var uri = new Uri("https://example.invalid/");

        Assert.That(copy, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(copy, Is.Not.SameAs(source));
            Assert.That(copy!.Name, Is.EqualTo("root"));
            Assert.That(copy.Count, Is.EqualTo(1));
            Assert.That(copy.Flavour, Is.EqualTo(Flavour.Third));
            Assert.That(copy.Stamp, Is.EqualTo(source.Stamp));

            Assert.That(copy.Child, Is.Not.SameAs(child));
            Assert.That(copy.Child!.Name, Is.EqualTo("child"));
            Assert.That(copy.Child.Count, Is.EqualTo(2));
            Assert.That(copy.Child.Child, Is.Null);

            // Generic collections go through the Add branch, arrays through the array branch.
            Assert.That(copy.Tags, Is.Not.SameAs(source.Tags));
            Assert.That(copy.Tags, Is.EqualTo(new[] { "a", "b" }));
            Assert.That(copy.Codes, Is.Not.SameAs(source.Codes));
            Assert.That(copy.Codes, Is.EqualTo(new[] { "x", "y" }));

            Assert.That(viaOut, Is.Not.SameAs(source));
            Assert.That(viaOut.Name, Is.EqualTo("root"));

            // Anything under the System namespace is handed back untouched.
            Assert.That(AccessTools.MakeDeepCopy(uri, typeof(Uri)), Is.SameAs(uri));
        });
    }

    [Test]
    public void MakeDeepCopyHandlesNullPrimitivesEnumsAndNullables()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AccessTools.MakeDeepCopy<Node>(null), Is.Null);
            Assert.That(AccessTools.MakeDeepCopy(null, typeof(Node)), Is.Null);
            Assert.That(AccessTools.MakeDeepCopy(new Node(), null), Is.Null);

            Assert.That(AccessTools.MakeDeepCopy(7, typeof(int)), Is.EqualTo(7));
            // The result type is unwrapped before any of the branches run.
            Assert.That(AccessTools.MakeDeepCopy(7, typeof(int?)), Is.EqualTo(7));
            Assert.That(AccessTools.MakeDeepCopy(Flavour.Third, typeof(Flavour)), Is.EqualTo(Flavour.Third));
            Assert.That(AccessTools.MakeDeepCopy(new[] { "x" }, typeof(string[])), Is.EqualTo(new[] { "x" }));
        });
    }

    [Test]
    public void MakeDeepCopyRunsTheProcessorWithDottedPaths()
    {
        var seen = new List<string>();
        var source = new Node { Name = "root", Child = new Node { Name = "child" } };

        var copy = (Node)AccessTools.MakeDeepCopy(source, typeof(Node), (path, src, _) =>
        {
            seen.Add(path);
            return path.EndsWith(".Name") ? "renamed" : src.GetValue()!;
        }, "top")!;

        Assert.Multiple(() =>
        {
            Assert.That(seen, Does.Contain("top.Name"));
            Assert.That(seen, Does.Contain("top.Child"));
            Assert.That(seen, Does.Contain("top.Child.Name"));
            Assert.That(copy.Name, Is.EqualTo("renamed"));
            Assert.That(copy.Child!.Name, Is.EqualTo("renamed"));
        });
    }

    [Test]
    public void MakeDeepCopySurfacesCollectionAddExceptionsUnwrapped()
    {
        // Upstream reaches Add through an emitted FastInvokeHandler; this host reaches it through
        // MethodBase.Invoke, which would otherwise hand back a TargetInvocationException wrapper.
        var refused = Assert.Throws<InvalidOperationException>(
            () => AccessTools.MakeDeepCopy(new List<string> { "a" }, typeof(RefusingCollection<string>)));

        Assert.That(refused!.Message, Is.EqualTo("add refused"));
    }
}

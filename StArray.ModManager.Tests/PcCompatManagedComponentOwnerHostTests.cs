using System.Reflection;
using StArray.ModManager.Android.PcCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatManagedComponentOwnerHostTests
{
    private readonly record struct FakeVector2(float X, float Y);

    private class FakeUnityObject
    {
    }

    private sealed class FakeTransform
    {
    }

    private static class FakeUnityObjectFactory
    {
        public static T Instantiate<T>(T original, FakeTransform parent)
            where T : FakeUnityObject
            => original;
    }

    private sealed class FakeRectTransform
    {
        public FakeVector2 anchoredPosition { get; set; }
    }

    [Test]
    public void AliveResolverDoesNotAssumeUnityObjectLivesInConcreteComponentAssembly()
    {
        var root = FindModManagerRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatManagedComponentOwnerHost.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("ResolveUnityObjectProxyType"));
            Assert.That(source, Does.Contain("TryGetProxyType"));
            Assert.That(source, Does.Contain("current = current.BaseType"));
            Assert.That(source, Does.Contain("TryGetUniqueProxyType"));
            Assert.That(source, Does.Contain("catch (TypeLoadException)"));
            Assert.That(
                source,
                Does.Not.Contain("gameObjectType.Assembly.GetType(\"UnityEngine.Object\""),
                "UnityEngine.Object is declared by CoreModule, not every component assembly");
        });
    }

    [Test]
    public void BoxedPropertyWriterResolvesRuntimeGeneratedSetterWithoutNullParameterTypes()
    {
        var builder = typeof(PcCompatManagedComponentOwnerHost).GetMethod(
            "BuildBoxedPropertyWriter",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(builder, Is.Not.Null);

        var writerObject = builder!.Invoke(
            null,
            new object[] { typeof(FakeRectTransform), "set_anchoredPosition" });
        Assert.That(writerObject, Is.TypeOf<Action<object, object>>());
        var writer = (Action<object, object>)writerObject!;
        var target = new FakeRectTransform();

        writer(target, new FakeVector2(12f, 34f));

        Assert.That(target.anchoredPosition, Is.EqualTo(new FakeVector2(12f, 34f)));
    }

    [Test]
    public void GenericParentInstantiateInvokerSupportsProxyWithoutNonGenericParentOverload()
    {
        var builder = typeof(PcCompatManagedComponentOwnerHost).GetMethod(
            "BuildGenericInstantiateWithParentInvoker",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(builder, Is.Not.Null);

        var genericMethod = typeof(FakeUnityObjectFactory).GetMethod(
            nameof(FakeUnityObjectFactory.Instantiate),
            BindingFlags.Static | BindingFlags.Public);
        Assert.That(genericMethod, Is.Not.Null);

        var invokerObject = builder!.Invoke(
            null,
            new object[]
            {
                genericMethod!,
                typeof(FakeUnityObject),
                typeof(FakeUnityObject),
                typeof(FakeTransform)
            });
        Assert.That(invokerObject, Is.TypeOf<Func<object, object, object>>());

        var original = new FakeUnityObject();
        var result = ((Func<object, object, object>)invokerObject!)(
            original,
            new FakeTransform());

        Assert.That(result, Is.SameAs(original));
    }

    private static string FindModManagerRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "xphorror.PcModCompat")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the StArray.ModManager root");
    }
}

using JALib.Tools;

namespace StArray.ModManager.Tests;

public sealed class PcCompatSimpleReflectTests
{
    [Test]
    public void FieldTreatsGeneratedProxyPropertyAsField()
    {
        var proxy = new GeneratedFieldProxy { planetarySystem = "ready" };

        var field = SimpleReflect.Field(typeof(GeneratedFieldProxy), "planetarySystem");

        Assert.That(field, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(field!.FieldType, Is.EqualTo(typeof(string)));
            Assert.That(field.GetValue(proxy), Is.EqualTo("ready"));
        });

        field.SetValue(proxy, "updated");
        Assert.That(proxy.planetarySystem, Is.EqualTo("updated"));
    }

    [Test]
    public void GetValueUsesGeneratedProxyGetterMethod()
    {
        var expected = new object();
        var proxy = new GeneratedGetterProxy(expected);

        var actual = SimpleReflect.GetValue(proxy, "planetarySystem");

        Assert.That(actual, Is.SameAs(expected));
    }

    [Test]
    public void GetValueCopiesProxyListIntoManagedList()
    {
        var proxy = new GeneratedListOwner
        {
            allPlanets = new GeneratedListProxy<string>(["red", "blue"])
        };

        var actual = SimpleReflect.GetValue<List<string>>(proxy, "allPlanets");

        Assert.That(actual, Is.EqualTo(new[] { "red", "blue" }));
    }

    [Test]
    public void ProductionSurfaceCoversDynamicPlanetResourceGraph()
    {
        var surface = File.ReadAllText(Path.Combine(
            FindModManagerRoot(),
            "xphorror.PcModCompat",
            "tools",
            "ProxyInputClosure",
            "proxy_surface_members.txt"));

        Assert.Multiple(() =>
        {
            Assert.That(surface, Does.Contain("F|Assembly-CSharp|scrPlanet|planetarySystem"));
            Assert.That(surface, Does.Contain("F|Assembly-CSharp|PlanetarySystem|allPlanets"));
            Assert.That(surface, Does.Contain("F|Assembly-CSharp|PlanetRenderer|sprite"));
            Assert.That(surface, Does.Contain("P|Assembly-CSharp|PlanetSprite|sprite"));
            Assert.That(surface, Does.Contain("|PlanetRenderer|instance|0|System.Void|DisableAllSpecialPlanets|"));
            Assert.That(surface, Does.Contain("|PlanetRenderer|instance|0|System.Void|SetPlanetColor|UnityEngine.Color"));
            Assert.That(surface, Does.Contain("|PlanetRenderer|instance|0|System.Void|SetTailColor|UnityEngine.Color"));
        });
    }

    private sealed class GeneratedFieldProxy
    {
        public string planetarySystem { get; set; } = string.Empty;
    }

    private sealed class GeneratedGetterProxy(object value)
    {
        public object get_planetarySystem() => value;
    }

    private sealed class GeneratedListOwner
    {
        public GeneratedListProxy<string>? allPlanets { get; set; }
    }

    private sealed class GeneratedListProxy<T>(IReadOnlyList<T> values)
    {
        public int get_Count() => values.Count;

        public T get_Item(int index) => values[index];
    }

    private static string FindModManagerRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "build.ps1")) &&
                Directory.Exists(Path.Combine(directory.FullName, "xphorror.PcModCompat")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        Assert.Fail("Could not find StArray.ModManager root from test directory");
        return string.Empty;
    }
}

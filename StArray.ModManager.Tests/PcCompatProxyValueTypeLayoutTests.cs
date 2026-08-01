using dnlib.DotNet;

namespace StArray.ModManager.Tests;

public sealed class PcCompatProxyValueTypeLayoutTests
{
    [Test]
    public void GeneratedUnityMathStructsPreserveBlittableFieldLayout()
    {
        var root = FindModManagerRoot();
        var proxyPath = Path.Combine(
            root,
            "xphorror.PcModCompat",
            "out",
            "interop",
            "proxy_assemblies",
            "UnityEngine.CoreModule.dll");
        Assume.That(File.Exists(proxyPath), Is.True, $"missing generated proxy: {proxyPath}");

        using var module = ModuleDefMD.Load(proxyPath);
        AssertLayout(module, "UnityEngine.Vector2", ("x", 0), ("y", 4));
        AssertLayout(module, "UnityEngine.Vector3", ("x", 0), ("y", 4), ("z", 8));
        AssertLayout(
            module,
            "UnityEngine.Color",
            ("r", 0),
            ("g", 4),
            ("b", 8),
            ("a", 12));
    }

    private static void AssertLayout(
        ModuleDefMD module,
        string fullName,
        params (string Name, uint Offset)[] expected)
    {
        var type = module.Find(fullName, isReflectionName: false);
        Assert.That(type, Is.Not.Null, $"missing generated value type {fullName}");

        var fields = type!.Fields.Where(field => !field.IsStatic).ToArray();
        Assert.That(
            fields.Select(field => field.Name.String),
            Is.EqualTo(expected.Select(field => field.Name)),
            $"{fullName} instance field order changed");

        if (type.IsExplicitLayout)
        {
            Assert.That(
                fields.Select(field => field.FieldOffset),
                Is.EqualTo(expected.Select(field => (uint?)field.Offset)),
                $"{fullName} explicit offsets overlap or drift");
            return;
        }

        Assert.That(
            type.IsSequentialLayout,
            Is.True,
            $"{fullName} must use sequential layout when explicit offsets are unavailable");
        Assert.That(
            fields.Select(field => field.FieldSig?.Type.FullName),
            Has.All.EqualTo("System.Single"),
            $"{fullName} sequential layout is only valid for the expected float fields");
    }

    private static string FindModManagerRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
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

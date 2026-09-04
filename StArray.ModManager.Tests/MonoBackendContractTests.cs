using System.Reflection;
using System.Runtime.InteropServices;
using StArray.ModManager.RuntimeAbstractions;

namespace StArray.ModManager.Tests;

public sealed class MonoBackendContractTests
{
    [Test]
    public void CoreAssemblyExposesCompleteUpstreamMonoBackend()
    {
        var assembly = typeof(RuntimeManager).Assembly;
        var functions = RequireType(assembly, "StArray.ModManager.Mono.MonoFunctions");
        var methods = RequireType(assembly, "StArray.ModManager.Mono.Methods");

        Assert.Multiple(() =>
        {
            RequireType(assembly, "StArray.ModManager.Mono.MonoDomain");
            RequireType(assembly, "StArray.ModManager.Mono.MonoAssembly");
            RequireType(assembly, "StArray.ModManager.Mono.MonoImage");
            RequireType(assembly, "StArray.ModManager.Mono.MonoClass");
            RequireType(assembly, "StArray.ModManager.Mono.MonoMethod");
            RequireType(assembly, "StArray.ModManager.Mono.MonoField");
            RequireType(assembly, "StArray.ModManager.Mono._MonoDomain");
            RequireType(assembly, "StArray.ModManager.Mono._MonoType");
            RequireType(assembly, "StArray.ModManager.Mono._MonoReflectionType");

            Assert.That(
                methods.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Count(method => method.GetCustomAttribute<DllImportAttribute>() != null),
                Is.EqualTo(711),
                "generated Mono embedding API is incomplete");
            Assert.That(
                functions.GetMethods(BindingFlags.Public | BindingFlags.Static),
                Has.Length.GreaterThanOrEqualTo(90),
                "high-level Mono wrappers are incomplete");
        });
    }

    [Test]
    public void XPerfectRequiredMonoAbiHasExactSignatures()
    {
        var assembly = typeof(RuntimeManager).Assembly;
        var functions = RequireType(assembly, "StArray.ModManager.Mono.MonoFunctions");
        var methods = RequireType(assembly, "StArray.ModManager.Mono.Methods");
        var domain = RequireType(assembly, "StArray.ModManager.Mono._MonoDomain");
        var monoType = RequireType(assembly, "StArray.ModManager.Mono._MonoType");
        var reflectionType = RequireType(assembly, "StArray.ModManager.Mono._MonoReflectionType");

        Assert.Multiple(() =>
        {
            AssertMethod(functions, "MonoClassGetType", typeof(nint), typeof(nint));
            AssertMethod(
                functions,
                "MonoArrayAddrWithSize",
                typeof(nint),
                typeof(nint),
                typeof(int),
                typeof(nuint));
            AssertMethod(
                functions,
                "MonoGCHandleNew",
                typeof(uint),
                typeof(nint),
                typeof(bool));
            AssertMethod(functions, "MonoGCHandleFree", typeof(void), typeof(uint));

            var getTypeObject = methods.GetMethod(
                "mono_type_get_object",
                BindingFlags.Public | BindingFlags.Static);
            Assert.That(getTypeObject, Is.Not.Null);
            Assert.That(getTypeObject!.ReturnType, Is.EqualTo(reflectionType.MakePointerType()));
            Assert.That(
                getTypeObject.GetParameters().Select(parameter => parameter.ParameterType),
                Is.EqualTo(new[] { domain.MakePointerType(), monoType.MakePointerType() }));
        });
    }

    [Test]
    public void UnifiedRuntimeAndAndroidResolverRetainMonoBranches()
    {
        var root = FindRepoRoot();
        var manager = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "RuntimeAbstractions", "RuntimeManager.cs"));
        var runtimeFiles = new[]
        {
            "RuntimeArray.cs",
            "RuntimeString.cs",
            "RuntimeObject.cs",
            "UnmanagedEnumerable.cs"
        };
        var runtimeSource = string.Join(
            "\n",
            runtimeFiles.Select(file => File.ReadAllText(Path.Combine(
                root, "StArray.ModManager", "RuntimeAbstractions", file))));
        var androidManaged = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager.Android", "Managed.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(manager, Does.Contain("MonoDomain.Current"));
            Assert.That(runtimeSource, Does.Contain("MonoFunctions.MonoArrayLength"));
            Assert.That(runtimeSource, Does.Contain("MonoFunctions.MonoStringLength"));
            Assert.That(runtimeSource, Does.Contain("MonoFunctions.MonoObjectGetClass"));
            Assert.That(runtimeSource, Does.Contain("MonoFunctions.MonoObjectUnbox"));
            Assert.That(androidManaged, Does.Contain("mono-2.0-bdwgc.dll"));
            Assert.That(androidManaged, Does.Contain("libmonobdwgc-2.0.so"));
            Assert.That(androidManaged, Does.Contain("libmono.so"));
        });
    }

    [Test]
    public void ExistingXPerfectHelpersCanBeJittedAfterMonoBackendRestore()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "XPerfect", "XPerfect.dll");
        if (!File.Exists(path))
            Assert.Ignore("local XPerfect binary is not present");

        var assembly = Assembly.LoadFrom(path);
        var gameApi = assembly.GetType("XPerfect.Mobile.GameApi", throwOnError: true)!;
        foreach (var methodName in new[] { "GetRuntimeTypeObject", "LoadMeterSprite", "NewGcHandle" })
        {
            var method = gameApi.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                                            BindingFlags.NonPublic)
                .Single(method => method.Name == methodName);
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle);
        }
    }

    private static Type RequireType(Assembly assembly, string name)
    {
        var type = assembly.GetType(name, throwOnError: false);
        Assert.That(type, Is.Not.Null, $"missing public Mono ABI type {name}");
        return type!;
    }

    private static void AssertMethod(
        Type type,
        string name,
        Type returnType,
        params Type[] parameterTypes)
    {
        var method = type.GetMethod(
            name,
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: parameterTypes,
            modifiers: null);
        Assert.That(method, Is.Not.Null, $"missing {type.FullName}.{name}");
        Assert.That(method!.ReturnType, Is.EqualTo(returnType));
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(TestContext.CurrentContext.WorkDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "StArray.ModManager.Android")) &&
                Directory.Exists(Path.Combine(current.FullName, "StArray.ModManager")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate StArray.ModManager repo root");
    }
}

using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatManagedAssemblyCatalogTests
{
    [Test]
    public void DiscoversJipperPrimaryAndBootstrapAsOneManagedBundle()
    {
        var root = FindRepoRoot();
        var modFolder = Path.Combine(root, "JipperResourcePack_release");
        Assert.That(
            PcModManifestReader.TryRead(modFolder, out var manifest, out var error),
            Is.True,
            error);
        var proxyFolder = Path.Combine(root, "xphorror.PcModCompat", "out", "interop", "proxy_assemblies");
        var proxyNames = Directory.EnumerateFiles(proxyFolder, "*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .ToArray();

        var catalog = PcCompatManagedAssemblyCatalog.Discover(manifest, proxyNames!);

        Assert.Multiple(() =>
        {
            Assert.That(
                catalog.Select(item => item.AssemblyName),
                Is.EquivalentTo(new[] { "JipperResourcePack", "JAMod.Bootstrap" }));
            Assert.That(catalog.Single(item => item.IsPrimary).AssemblyName, Is.EqualTo("JipperResourcePack"));
            Assert.That(catalog.Single(item => item.IsBootstrap).AssemblyName, Is.EqualTo("JAMod.Bootstrap"));
        });
    }

    [Test]
    public void RewriterProvesManagedMonoBehaviourAcrossOwnedAssemblyBoundary()
    {
        var repoRoot = FindRepoRoot();
        var root = Path.Combine(Path.GetTempPath(), "pccompat-owned-assemblies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var companionPath = Path.Combine(root, "Owned.Companion.dll");
        var inputPath = Path.Combine(root, "Owned.Entry.dll");
        var outputPath = Path.Combine(root, "Owned.Entry.rewritten.dll");
        var reportPath = Path.Combine(root, "rewrite.json");
        try
        {
            CreateCompanionAssembly(companionPath);
            CreateEntryAssembly(inputPath);
            var bridgeType = typeof(PcCompatManagedComponentBridge).FullName!;
            var spec = new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.GameObject",
                "AddComponent",
                SourceIsStatic: false,
                SourceGenericArity: 1,
                "!!0",
                [],
                bridgeType,
                nameof(PcCompatManagedComponentBridge.AddComponent),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                GenericArgumentFilter: ManagedCallGenericArgumentFilter.ModOwnedMonoBehaviour);

            var report = ModAssemblyRewriteApi.Rewrite(
                inputPath,
                outputPath,
                Path.Combine(repoRoot, "xphorror.PcModCompat", "out", "interop", "proxy_assemblies"),
                reportPath,
                managedBridgeAssemblyPath: typeof(PcCompatManagedComponentBridge).Assembly.Location,
                managedCallBridgeRewrites: [spec],
                managedOwnedAssemblyPaths: [inputPath, companionPath]);

            Assert.That(report.ManagedBridgeIssues, Is.Empty);
            Assert.That(report.OutputWritten, Is.True);
            using var rewritten = ModuleDefMD.Load(outputPath);
            var call = rewritten.GetTypes()
                .SelectMany(type => type.Methods.Where(method => method.HasBody))
                .SelectMany(method => method.Body.Instructions)
                .Where(instruction => instruction.OpCode.Code == Code.Call)
                .Select(instruction => instruction.Operand)
                .OfType<MethodSpec>()
                .Single(method => method.Name == nameof(PcCompatManagedComponentBridge.AddComponent));
            Assert.That(call.DeclaringType.FullName, Is.EqualTo(bridgeType));
            Assert.That(
                call.GenericInstMethodSig.GenericArguments.Single().FullName,
                Is.EqualTo("Owned.Companion.CustomUpdater"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void CreateCompanionAssembly(string path)
    {
        using var module = new ModuleDefUser("Owned.Companion.dll") { Kind = ModuleKind.Dll };
        new AssemblyDefUser("Owned.Companion", new Version(1, 0, 0, 0)).Modules.Add(module);
        var core = new AssemblyRefUser(new AssemblyNameInfo("UnityEngine.CoreModule"));
        var monoBehaviour = new TypeRefUser(module, "UnityEngine", "MonoBehaviour", core);
        module.Types.Add(new TypeDefUser("Owned.Companion", "CustomUpdater", monoBehaviour)
        {
            Attributes = TypeAttributes.Public | TypeAttributes.Class
        });
        module.Write(path);
    }

    private static void CreateEntryAssembly(string path)
    {
        using var module = new ModuleDefUser("Owned.Entry.dll") { Kind = ModuleKind.Dll };
        new AssemblyDefUser("Owned.Entry", new Version(1, 0, 0, 0)).Modules.Add(module);
        var core = new AssemblyRefUser(new AssemblyNameInfo("UnityEngine.CoreModule"));
        var companion = new AssemblyRefUser(new AssemblyNameInfo("Owned.Companion"));
        var gameObject = new TypeRefUser(module, "UnityEngine", "GameObject", core);
        var updater = new TypeRefUser(module, "Owned.Companion", "CustomUpdater", companion);
        var probe = new TypeDefUser(
            "Owned.Entry",
            "Probe",
            module.CorLibTypes.Object.TypeDefOrRef)
        {
            Attributes = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed
        };
        module.Types.Add(probe);
        var method = new MethodDefUser(
            "Install",
            MethodSig.CreateStatic(module.CorLibTypes.Void, new ClassSig(gameObject)),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static)
        {
            Body = new CilBody()
        };
        var genericSignature = MethodSig.CreateInstance(new GenericMVar(0));
        genericSignature.Generic = true;
        genericSignature.GenParamCount = 1;
        var add = new MethodSpecUser(
            new MemberRefUser(module, "AddComponent", genericSignature, gameObject),
            new GenericInstMethodSig(new ClassSig(updater)));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, add));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        probe.Methods.Add(method);
        module.Write(path);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "JipperResourcePack_release")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("StArray.ModManager repository root not found.");
    }
}

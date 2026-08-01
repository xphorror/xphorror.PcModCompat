using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatManagedBridgeRewriteTests
{
    private string _root = null!;
    private string _inputPath = null!;
    private string _outputPath = null!;
    private string _reportPath = null!;
    private string _proxyDirectory = null!;
    private string _bridgeAssemblyPath = null!;
    private IReadOnlyList<ManagedBridgeRewriteSpec> _specs = null!;
    private IReadOnlyList<ManagedCallBridgeRewriteSpec> _callSpecs = null!;
    private IReadOnlyList<ManagedFieldConstantRewriteSpec> _fieldConstantSpecs = null!;
    private RewriteReport _report = null!;

    [OneTimeSetUp]
    public void RewriteJipperAssembly()
    {
        var repoRoot = FindRepoRoot();
        var modDirectory = Path.Combine(repoRoot, "JipperResourcePack_release");
        _proxyDirectory = Path.Combine(
            repoRoot,
            "xphorror.PcModCompat",
            "out",
            "interop",
            "proxy_assemblies");
        Assume.That(Directory.Exists(modDirectory), Is.True, $"missing sample mod dir: {modDirectory}");
        Assume.That(Directory.Exists(_proxyDirectory), Is.True, $"missing proxy dir: {_proxyDirectory}");
        Assert.That(
            PcModManifestReader.TryRead(modDirectory, out var manifest, out var manifestError),
            Is.True,
            manifestError);

        var scan = PcCompatStaticPatchScanner.Scan(manifest, targetGameRevision: 143);
        _specs = scan.ActivePatches
            .Where(patch => patch.Kind == PcCompatPatchKind.ReversePatch)
            .Select(patch =>
            {
                Assert.That(
                    PcCompatReversePatchBridge.TryFindHandler(
                        patch.TargetType,
                        patch.TargetMethod,
                        out var handler),
                    Is.True);
                return new ManagedBridgeRewriteSpec(
                    patch.TargetType,
                    patch.TargetMethod,
                    patch.ArgumentTypeNames.ToArray(),
                    typeof(PcCompatReversePatchBridge).FullName!,
                    handler!.AndroidBridgeMethod);
            })
            .Append(new ManagedBridgeRewriteSpec(
                "JipperResourcePack.KeyViewerContents.KeyViewer",
                "GetAsyncKeyState",
                ["System.Int32"],
                typeof(PcCompatLegacyInputBridge).FullName!,
                nameof(PcCompatLegacyInputBridge.GetAsyncKeyStateOwned),
                "RewriteTestMod",
                AppendCallsiteToken: true))
            .ToArray();

        _root = Path.Combine(Path.GetTempPath(), "pccompat-bridge-rewrite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _inputPath = Path.Combine(_root, "JipperResourcePack.input.dll");
        _outputPath = Path.Combine(_root, "JipperResourcePack.rewritten.dll");
        _reportPath = Path.Combine(_root, "rewrite-report.json");
        AddUnrelatedSameNamedCall(
            Path.Combine(modDirectory, "JipperResourcePack.dll"),
            _inputPath);

        _bridgeAssemblyPath = typeof(PcCompatReversePatchBridge).Assembly.Location;
        _callSpecs = CreateManagedCallSpecs();
        _fieldConstantSpecs =
        [
            new ManagedFieldConstantRewriteSpec(
                "Assembly-CSharp",
                "ADOBase",
                "platform",
                "Platform",
                3)
        ];
        _report = ModAssemblyRewriteApi.Rewrite(
            _inputPath,
            _outputPath,
            _proxyDirectory,
            _reportPath,
            managedBridgeAssemblyPath: _bridgeAssemblyPath,
            managedBridgeRewrites: _specs,
            managedCallBridgeRewrites: _callSpecs,
            managedFieldConstantRewrites: _fieldConstantSpecs,
            managedProxyCastBridge: new ManagedProxyCastBridgeSpec(
                typeof(PcCompatProxyCastBridge).FullName!,
                nameof(PcCompatProxyCastBridge.IsInstance),
                nameof(PcCompatProxyCastBridge.Cast)),
            managedReadProgressGuard: new ManagedReadProgressGuardSpec(
                typeof(PcCompatManagedIoBridge).FullName!,
                nameof(PcCompatManagedIoBridge.RequireFileReadProgress),
                nameof(PcCompatManagedIoBridge.TryReadFileExactly)),
            managedPollingWaitRewrite: new ManagedPollingWaitRewriteSpec(
                typeof(PcCompatManagedPollingBridge).FullName!,
                nameof(PcCompatManagedPollingBridge.WaitForCoarseClockAdvance)),
            managedOptionalDelegateRewrite: new ManagedOptionalDelegateRewriteSpec(
                "JALib",
                "JALib.Tools.SettingGUI",
                "AddSetting",
                "System.Action",
                typeof(PcCompatManagedSettingsDelegateBridge).FullName!,
                nameof(PcCompatManagedSettingsDelegateBridge.CreateOptionalAction)));

        Assert.That(_report.Issues, Is.Empty);
        Assert.That(_report.MethodIssues, Is.Empty);
        Assert.That(_report.ManagedBridgeIssues, Is.Empty);
        Assert.That(_report.OutputWritten, Is.True);
    }

    [Test]
    public void RewritesPcOnlyPlatformGuardWithoutMutatingGamePlatformState()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var constructor = module.GetTypes()
            .Single(type => type.FullName == "JipperResourcePack.KeyViewerContents.KeyViewer")
            .Methods.Single(method => method.IsConstructor && !method.IsStaticConstructor);

        Assert.Multiple(() =>
        {
            Assert.That(constructor.Body.Instructions.Any(instruction =>
                instruction.OpCode.Code == Code.Ldsfld &&
                instruction.Operand is IField field &&
                field.DeclaringType.FullName == "ADOBase" &&
                field.Name == "platform"), Is.False);
            Assert.That(constructor.Body.Instructions.Any(instruction =>
                instruction.IsLdcI4() && instruction.GetLdcI4Value() == 3), Is.True);
            Assert.That(_report.ManagedBridgeRewrites.Any(item =>
                item.SourceMethod == "Assembly-CSharp!ADOBase::platform" &&
                item.BridgeMethod == "constant:i4:3"), Is.True);
        });
    }

    [Test]
    public void BoxedValueConvertersPreserveGeneratedProxyAssemblyAndTypeKind()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var proxies = Directory.EnumerateFiles(_proxyDirectory, "*.dll")
            .Select(path => ModuleDefMD.Load(path))
            .ToArray();
        try
        {
            var proxyTypes = proxies
                .SelectMany(proxy => proxy.GetTypes()
                    .Where(type => !type.IsGlobalModuleType)
                    .Select(type => new
                    {
                        Assembly = proxy.Assembly.Name.String,
                        Type = type
                    }))
                .GroupBy(item => item.Type.FullName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var converterTypes = module.GetTypes()
                .SelectMany(type => type.Methods.Where(method => method.HasBody))
                .SelectMany(method => method.Body.Instructions)
                .Select(instruction => instruction.Operand)
                .OfType<MethodSpec>()
                .Where(method =>
                    method.Name == "BoxUnboxedValue" &&
                    method.DeclaringType.FullName ==
                        "StArray.ModManager.Android.PcCompat.PcCompatAbiBridge" &&
                    method.GenericInstMethodSig?.GenericArguments.Count == 1)
                .Select(method => method.GenericInstMethodSig!.GenericArguments[0])
                .GroupBy(type =>
                {
                    var typeRef = type.TryGetTypeDefOrRef();
                    return $"{typeRef?.DefinitionAssembly?.Name?.String}|{type.FullName}|{type.IsValueType}";
                }, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();
            var auditedExternalTypes = new List<string>();
            var issues = new List<string>();

            foreach (var converterType in converterTypes)
            {
                var typeRef = converterType.TryGetTypeDefOrRef();
                if (typeRef is null || !proxyTypes.TryGetValue(converterType.FullName, out var matches))
                    continue;
                auditedExternalTypes.Add(converterType.FullName);
                if (matches.Length != 1)
                {
                    issues.Add(
                        $"{converterType.FullName}: generated proxy type is ambiguous across " +
                        string.Join(", ", matches.Select(match => match.Assembly)));
                    continue;
                }

                var expected = matches[0];
                var actualAssembly = typeRef.DefinitionAssembly?.Name?.String;
                if (!string.Equals(actualAssembly, expected.Assembly, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(
                        $"{converterType.FullName}: assembly expected={expected.Assembly} actual={actualAssembly}");
                }
                if (converterType.IsValueType != expected.Type.IsValueType)
                {
                    issues.Add(
                        $"{expected.Assembly}!{converterType.FullName}: IsValueType " +
                        $"expected={expected.Type.IsValueType} actual={converterType.IsValueType}");
                }
            }

            Assert.Multiple(() =>
            {
                Assert.That(converterTypes, Is.Not.Empty, "rewritten sample has no boxed-value converters to audit");
                Assert.That(
                    auditedExternalTypes,
                    Is.Not.Empty,
                    "rewritten sample has no generated-proxy boxed-value converters to audit");
                Assert.That(issues, Is.Empty, string.Join(Environment.NewLine, issues));
            });
        }
        finally
        {
            foreach (var proxy in proxies)
                proxy.Dispose();
        }
    }

    [OneTimeTearDown]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void RewritesJAModBootstrapManagedExceptionLogging()
    {
        var repoRoot = FindRepoRoot();
        var modDirectory = Path.Combine(repoRoot, "JipperResourcePack_release");
        var inputPath = Path.Combine(modDirectory, "JAMod.Bootstrap.dll");
        var outputPath = Path.Combine(_root, "JAMod.Bootstrap.rewritten.dll");
        var reportPath = Path.Combine(_root, "JAMod.Bootstrap.rewrite-report.json");
        var report = ModAssemblyRewriteApi.Rewrite(
            inputPath,
            outputPath,
            _proxyDirectory,
            reportPath,
            managedBridgeAssemblyPath: _bridgeAssemblyPath,
            managedCallBridgeRewrites: _callSpecs,
            managedProxyCastBridge: new ManagedProxyCastBridgeSpec(
                typeof(PcCompatProxyCastBridge).FullName!,
                nameof(PcCompatProxyCastBridge.IsInstance),
                nameof(PcCompatProxyCastBridge.Cast)),
            managedOwnedAssemblyPaths:
            [
                inputPath,
                Path.Combine(modDirectory, "JipperResourcePack.dll")
            ]);

        Assert.Multiple(() =>
        {
            Assert.That(report.Issues, Is.Empty);
            Assert.That(report.MethodIssues, Is.Empty);
            Assert.That(report.ManagedBridgeIssues, Is.Empty);
            Assert.That(report.OutputWritten, Is.True);
        });

        using var module = ModuleDefMD.Load(outputPath);
        var installer = module.GetTypes().Single(type => type.FullName == "JAMod.Bootstrap.Installer");
        var applyMod = installer.Methods.Single(method => method.Name == "ApplyMod");
        Assert.That(
            applyMod.Body.Instructions.Any(instruction =>
                instruction.OpCode.Code == Code.Call &&
                instruction.Operand is IMethod target &&
                target.DeclaringType.FullName == typeof(PcCompatManagedLogBridge).FullName &&
                target.Name == "LogException"),
            Is.True);
    }

    [Test]
    public void RewritesLegacyInputPollingToNativeSnapshotBridge()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var calls = module.GetTypes()
            .SelectMany(type => type.Methods.Where(method => method.HasBody))
            .SelectMany(method => method.Body.Instructions.Select((instruction, index) =>
                (Method: method, Instruction: instruction, Index: index)))
            .Where(item => item.Instruction.OpCode.Code == Code.Call &&
                           item.Instruction.Operand is IMethod target &&
                           target.DeclaringType.FullName == typeof(PcCompatLegacyInputBridge).FullName)
            .ToArray();

        var getKey = calls.Where(item => ((IMethod)item.Instruction.Operand).Name ==
                                         nameof(PcCompatLegacyInputBridge.GetKeyOwned)).ToArray();
        var getKeyDown = calls.Where(item => ((IMethod)item.Instruction.Operand).Name ==
                                             nameof(PcCompatLegacyInputBridge.GetKeyDownOwned)).ToArray();
        var anyKeyDown = calls.Where(item => ((IMethod)item.Instruction.Operand).Name ==
                                             nameof(PcCompatLegacyInputBridge.GetAnyKeyDownOwned)).ToArray();
        var asyncKeyState = calls.Where(item => ((IMethod)item.Instruction.Operand).Name ==
                                                nameof(PcCompatLegacyInputBridge.GetAsyncKeyStateOwned)).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(getKey, Is.Not.Empty);
            Assert.That(getKeyDown, Is.Not.Empty);
            Assert.That(anyKeyDown, Is.Not.Empty);
            Assert.That(asyncKeyState, Is.Not.Empty);
            Assert.That(
                calls.Where(item => ((IMethod)item.Instruction.Operand).Name.String is
                    nameof(PcCompatLegacyInputBridge.GetKeyDownOwned) or
                    nameof(PcCompatLegacyInputBridge.GetKeyUpOwned) or
                    nameof(PcCompatLegacyInputBridge.GetAnyKeyDownOwned)),
                Is.All.Matches<(MethodDef Method, Instruction Instruction, int Index)>(item =>
                    item.Index > 1 &&
                    item.Method.Body.Instructions[item.Index - 2].IsLdcI4() &&
                    item.Method.Body.Instructions[item.Index - 1].OpCode.Code == Code.Ldstr &&
                    (string)item.Method.Body.Instructions[item.Index - 1].Operand == "RewriteTestMod"),
                "edge queries must carry a stable callsite token and embedded owner");
            Assert.That(
                getKey.Concat(asyncKeyState),
                Is.All.Matches<(MethodDef Method, Instruction Instruction, int Index)>(item =>
                    item.Index > 0 &&
                    item.Method.Body.Instructions[item.Index - 1].OpCode.Code == Code.Ldstr &&
                    (string)item.Method.Body.Instructions[item.Index - 1].Operand == "RewriteTestMod"),
                "held-state queries must carry the embedded owner");
            Assert.That(
                module.GetTypes()
                    .SelectMany(type => type.Methods.Where(method => method.HasBody))
                    .SelectMany(method => method.Body.Instructions)
                    .Where(instruction => instruction.Operand is IMethod)
                    .Select(instruction => (IMethod)instruction.Operand)
                    .Where(target => target.DeclaringType.FullName == "UnityEngine.Input")
                    .Where(target => target.Name.String is "GetKey" or "GetKeyDown" or "get_anyKeyDown"),
                Is.Empty);
        });
    }

    [Test]
    public void JalibShimProvidesExactMainThreadAbiReferencedByJipper()
    {
        using var module = ModuleDefMD.Load(_inputPath);
        var referencedSignatures = module.GetTypes()
            .SelectMany(type => type.Methods.Where(method => method.HasBody))
            .SelectMany(method => method.Body.Instructions)
            .Where(instruction => instruction.Operand is IMethod target &&
                                  target.DeclaringType.FullName == "JALib.Tools.MainThread" &&
                                  target.Name.String == "Run")
            .Select(instruction => (IMethod)instruction.Operand)
            .Select(target => target.MethodSig!.Params.Select(parameter => parameter.FullName).ToArray())
            .DistinctBy(parameters => string.Join("|", parameters))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(referencedSignatures, Has.Length.EqualTo(1));
            Assert.That(
                referencedSignatures[0],
                Is.EqualTo(new[] { "JALib.Core.JAMod", "System.Action" }));
            Assert.That(
                typeof(JALib.Tools.MainThread).GetMethod(
                    "Run",
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Static,
                    binder: null,
                    types: [typeof(JALib.Core.JAMod), typeof(Action)],
                    modifiers: null),
                Is.Not.Null,
                "Run(object, Action) is not ABI-compatible with a MOD reference to Run(JAMod, Action)");
        });
    }

    [Test]
    public void AllowsDistinctProxyResolvableAssetBundleModuleTypes()
    {
        // AssetBundleCreateRequest/AssetBundleRequest exist in the generated
        // UnityEngine.AssetBundleModule proxy and must not be mistaken for the
        // erased UnityEngine.AssetBundle handle by prefix matching.
        var input = Path.Combine(_root, "OpaqueDistinct.input.dll");
        var output = Path.Combine(_root, "OpaqueDistinct.rewritten.dll");
        var reportPath = Path.Combine(_root, "OpaqueDistinct.report.json");
        CreateOpaqueFixtureModule(
            input,
            ("UnityEngine.AssetBundleModule", "UnityEngine.AssetBundleCreateRequest", "Plain"),
            ("UnityEngine.AssetBundleModule", "UnityEngine.AssetBundleRequest", "Plain"));

        var report = RewriteFixture(input, output, reportPath);

        Assert.Multiple(() =>
        {
            Assert.That(
                report.ManagedBridgeIssues.Where(issue => issue.Reason.Contains("nested opaque")),
                Is.Empty);
            Assert.That(
                report.MethodIssues,
                Is.Empty,
                "issues: " + string.Join(" | ", report.Issues) +
                " :: methodIssues: " + string.Join(" | ", report.MethodIssues.Select(i => i.Method + ": " + i.Reason + " (" + i.Target + ")")) +
                " :: bridgeIssues: " + string.Join(" | ", report.ManagedBridgeIssues.Select(i => i.SourceType + "." + i.SourceMethod + ": " + i.Reason)));
            Assert.That(report.OutputWritten, Is.True);
        });
    }

    [Test]
    public void RejectsArrayOfErasedOpaqueType()
    {
        var input = Path.Combine(_root, "OpaqueArray.input.dll");
        var output = Path.Combine(_root, "OpaqueArray.rewritten.dll");
        var reportPath = Path.Combine(_root, "OpaqueArray.report.json");
        CreateOpaqueFixtureModule(
            input,
            ("UnityEngine.AssetBundleModule", "UnityEngine.AssetBundle", "Array"));

        var report = RewriteFixture(input, output, reportPath);

        Assert.Multiple(() =>
        {
            Assert.That(
                report.ManagedBridgeIssues.Any(issue =>
                    issue.Reason.Contains("nested opaque field type is unsupported")),
                Is.True);
            Assert.That(report.OutputWritten, Is.False);
        });
    }

    [Test]
    public void RejectsGenericOfErasedOpaqueType()
    {
        var input = Path.Combine(_root, "OpaqueGeneric.input.dll");
        var output = Path.Combine(_root, "OpaqueGeneric.rewritten.dll");
        var reportPath = Path.Combine(_root, "OpaqueGeneric.report.json");
        CreateOpaqueFixtureModule(
            input,
            ("UnityEngine.AssetBundleModule", "UnityEngine.AssetBundle", "List"));

        var report = RewriteFixture(input, output, reportPath);

        Assert.Multiple(() =>
        {
            Assert.That(
                report.ManagedBridgeIssues.Any(issue =>
                    issue.Reason.Contains("nested opaque field type is unsupported")),
                Is.True);
            Assert.That(report.OutputWritten, Is.False);
        });
    }

    [Test]
    public void RejectsUnproxiedAssetBundleModuleTypeWithPreciseReason()
    {
        // AssetBundleManifest is not part of the generated proxy closure. It must
        // fail through the metadata-reference audit, not the opaque-erasure check.
        var input = Path.Combine(_root, "OpaqueUnproxied.input.dll");
        var output = Path.Combine(_root, "OpaqueUnproxied.rewritten.dll");
        var reportPath = Path.Combine(_root, "OpaqueUnproxied.report.json");
        CreateOpaqueFixtureModule(
            input,
            ("UnityEngine.AssetBundleModule", "UnityEngine.AssetBundleManifest", "Plain"));

        var report = RewriteFixture(input, output, reportPath);

        Assert.Multiple(() =>
        {
            Assert.That(
                report.ManagedBridgeIssues.Where(issue => issue.Reason.Contains("nested opaque")),
                Is.Empty);
            Assert.That(
                report.Issues.Any(issue =>
                    issue.Contains("generated proxy type missing") &&
                    issue.Contains("UnityEngine.AssetBundleManifest")),
                Is.True);
            Assert.That(report.OutputWritten, Is.False);
        });
    }

    private RewriteReport RewriteFixture(string input, string output, string reportPath)
        => ModAssemblyRewriteApi.Rewrite(
            input,
            output,
            _proxyDirectory,
            reportPath,
            managedBridgeAssemblyPath: _bridgeAssemblyPath,
            managedCallBridgeRewrites: _callSpecs,
            managedProxyCastBridge: new ManagedProxyCastBridgeSpec(
                typeof(PcCompatProxyCastBridge).FullName!,
                nameof(PcCompatProxyCastBridge.IsInstance),
                nameof(PcCompatProxyCastBridge.Cast)));

    private static void CreateOpaqueFixtureModule(
        string path,
        params (string AssemblyName, string TypeFullName, string Kind)[] fields)
    {
        var module = new ModuleDefUser("OpaqueNestedFixture")
        {
            Kind = ModuleKind.Dll
        };
        var assembly = new AssemblyDefUser("OpaqueNestedFixture", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);

        var holder = new TypeDefUser(
            "OpaqueFixture",
            "Holder",
            module.CorLibTypes.Object.TypeDefOrRef);
        module.Types.Add(holder);

        var assemblyRefs = new Dictionary<string, AssemblyRefUser>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < fields.Length; index++)
        {
            var (assemblyName, typeFullName, kind) = fields[index];
            if (!assemblyRefs.TryGetValue(assemblyName, out var assemblyRef))
            {
                assemblyRef = new AssemblyRefUser(assemblyName, new Version(0, 0, 0, 0));
                assemblyRefs.Add(assemblyName, assemblyRef);
            }

            var nsEnd = typeFullName.LastIndexOf('.');
            var typeRef = new TypeRefUser(
                module,
                nsEnd > 0 ? typeFullName[..nsEnd] : string.Empty,
                nsEnd > 0 ? typeFullName[(nsEnd + 1)..] : typeFullName,
                assemblyRef);
            TypeSig fieldSig = typeRef.ToTypeSig();
            fieldSig = kind switch
            {
                "Array" => new SZArraySig(fieldSig),
                "List" => new GenericInstSig(
                    module.CorLibTypes
                        .GetTypeRef("System.Collections.Generic", "List`1")
                        .ToTypeSig()
                        .ToClassOrValueTypeSig(),
                    fieldSig),
                _ => fieldSig
            };
            holder.Fields.Add(new FieldDefUser($"Field{index}", new FieldSig(fieldSig)));
        }

        module.Write(path);
    }

    [Test]
    public void RewritesOnlyModuleLocalManagedComponents()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var keyViewer = module.GetTypes().Single(type =>
            type.FullName == "JipperResourcePack.KeyViewerContents.KeyViewer");
        var onEnable = keyViewer.Methods.Single(method => method.Name == "OnEnable");
        var addCalls = onEnable.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
            .Select(instruction => instruction.Operand)
            .OfType<MethodSpec>()
            .Where(method => method.Name == "AddComponent")
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(
                addCalls.Where(method =>
                    method.GenericInstMethodSig!.GenericArguments[0].FullName is
                        "JipperResourcePack.KeyViewerContents.KeyViewer/KeyViewerUpdater" or
                        "JipperResourcePack.KeyViewerContents.RainManager"),
                Has.All.Property(nameof(MethodSpec.DeclaringType))
                    .Property(nameof(ITypeDefOrRef.FullName))
                    .EqualTo(typeof(PcCompatManagedComponentBridge).FullName));
            Assert.That(
                addCalls.Single(method =>
                    method.GenericInstMethodSig!.GenericArguments[0].FullName == "UnityEngine.Canvas")
                    .DeclaringType.FullName,
                Is.EqualTo("UnityEngine.GameObject"),
                "Generated IL2CPP proxy component calls must remain on GameObject.AddComponent<T>().");
        });
    }

    [Test]
    public void RewritesManagedBehaviourEnabledAccess()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var keyViewer = module.GetTypes().Single(type =>
            type.FullName == "JipperResourcePack.KeyViewerContents.KeyViewer");
        var onEnable = keyViewer.Methods.Single(method => method.Name == "OnEnable");
        var calls = onEnable.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
            .Select(instruction => instruction.Operand)
            .OfType<IMethod>()
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(
                calls.Any(call =>
                    call.DeclaringType.FullName == typeof(PcCompatManagedComponentBridge).FullName &&
                    call.Name == "SetEnabled"),
                Is.True);
            Assert.That(
                calls.Any(call =>
                    call.DeclaringType.FullName == "UnityEngine.Behaviour" &&
                    call.Name == "set_enabled"),
                Is.False);
        });
    }

    [Test]
    public void GuardsFiniteFileReadLoopsAgainstZeroProgress()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var keyCountData = module.GetTypes().Single(type =>
            type.FullName == "JipperResourcePack.KeyViewerContents.KeyCountData");
        var readExactly = keyCountData.Methods.Single(method => method.Name == "ReadExactly");
        var calls = readExactly.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
            .Select(instruction => (IMethod)instruction.Operand)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(
                calls.Any(call =>
                    call.DeclaringType.FullName == typeof(PcCompatManagedIoBridge).FullName &&
                    call.Name == nameof(PcCompatManagedIoBridge.TryReadFileExactly)),
                Is.True);
            Assert.That(
                calls.Any(call => call.Name == "Read" &&
                                  call.DeclaringType.FullName is
                                      "System.IO.Stream" or "System.IO.FileStream"),
                Is.False);
            Assert.That(
                _report.ManagedBridgeRewrites.Any(item =>
                    item.Method.Contains("KeyCountData::ReadExactly", StringComparison.Ordinal) &&
                    item.SourceMethod.Contains("finite-progress-loop", StringComparison.Ordinal)),
                Is.True);
        });
    }

    [Test]
    public void RewritesOnlyProvenCoarseClockSpinWaits()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var keyViewer = module.GetTypes().Single(type =>
            type.FullName == "JipperResourcePack.KeyViewerContents.KeyViewer");
        var listenKey = keyViewer.Methods.Single(method => method.Name == "ListenKey");
        var listenCalls = listenKey.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
            .Select(instruction => (IMethod)instruction.Operand)
            .ToArray();

        var probe = module.GetTypes().Single(type =>
            type.FullName == "PcCompat.Tests.UnrelatedBridgeProbe");
        var unrelated = probe.Methods.Single(method => method.Name == "CallUnrelatedThreadYield");
        var unrelatedCalls = unrelated.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
            .Select(instruction => (IMethod)instruction.Operand)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(
                listenCalls.Any(call =>
                    call.DeclaringType.FullName == typeof(PcCompatManagedPollingBridge).FullName &&
                    call.Name == nameof(PcCompatManagedPollingBridge.WaitForCoarseClockAdvance)),
                Is.True);
            Assert.That(
                listenCalls.Any(call =>
                    call.DeclaringType.FullName == "System.Threading.Thread" &&
                    call.Name == "Yield"),
                Is.False);
            Assert.That(
                unrelatedCalls.Any(call =>
                    call.DeclaringType.FullName == "System.Threading.Thread" &&
                    call.Name == "Yield"),
                Is.True,
                "Ordinary Thread.Yield calls must not be rewritten without the proven clock loop.");
            Assert.That(
                _report.ManagedBridgeRewrites.Any(item =>
                    item.Method.Contains("KeyViewer::ListenKey", StringComparison.Ordinal) &&
                    item.SourceMethod.Contains("coarse-clock-spin", StringComparison.Ordinal)),
                Is.True);
        });
    }

    [Test]
    public void RewritesUnsupportedThreadAbortToCooperativeBridge()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var keyViewer = module.GetTypes().Single(type =>
            type.FullName == "JipperResourcePack.KeyViewerContents.KeyViewer");
        var methods = keyViewer.Methods
            .Where(method => method.Name.String is "OnDisable" or "ApplicationOnquitting")
            .ToArray();
        var calls = methods
            .SelectMany(method => method.Body.Instructions)
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
            .Select(instruction => (IMethod)instruction.Operand)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(calls.Count(call =>
                call.DeclaringType.FullName == typeof(PcCompatManagedThreadBridge).FullName &&
                call.Name == nameof(PcCompatManagedThreadBridge.Abort)), Is.EqualTo(2));
            Assert.That(calls.Any(call =>
                call.DeclaringType.FullName == "System.Threading.Thread" &&
                call.Name == "Abort"), Is.False);
            Assert.That(_report.ManagedBridgeRewrites.Count(item =>
                item.SourceMethod.Contains("System.Threading.Thread::Abort", StringComparison.Ordinal) &&
                item.BridgeMethod.Contains(nameof(PcCompatManagedThreadBridge), StringComparison.Ordinal)),
                Is.EqualTo(2));
        });
    }

    [Test]
    public void RewritesJipperPersistentKeyViewerRootToOwnedLifecycleBridge()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var keyViewer = module.GetTypes().Single(type =>
            type.FullName == "JipperResourcePack.KeyViewerContents.KeyViewer");
        var onEnable = keyViewer.Methods.Single(method => method.Name == "OnEnable");
        var calls = onEnable.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
            .Select(instruction => (IMethod)instruction.Operand)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(calls.Count(call =>
                call.DeclaringType.FullName == typeof(PcCompatManagedComponentBridge).FullName &&
                call.Name == "DontDestroyOnLoad"), Is.EqualTo(1));
            Assert.That(calls.Any(call =>
                call.DeclaringType.FullName == "UnityEngine.Object" &&
                call.Name == "DontDestroyOnLoad"), Is.False);
            Assert.That(_report.ManagedBridgeRewrites.Count(item =>
                item.Method.Contains("KeyViewer::OnEnable", StringComparison.Ordinal) &&
                item.SourceMethod.Contains("DontDestroyOnLoad", StringComparison.Ordinal) &&
                item.BridgeMethod.Contains(nameof(PcCompatManagedComponentBridge), StringComparison.Ordinal)),
                Is.EqualTo(1));
        });
    }

    [Test]
    public void RewritesGameObjectAndComponentManagedGetComponentCalls()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var probe = module.GetTypes().Single(type => type.FullName == "PcCompat.Tests.UnrelatedBridgeProbe");
        var method = probe.Methods.Single(candidate => candidate.Name == "CallManagedGetComponents");
        var calls = method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Call)
            .Select(instruction => instruction.Operand)
            .OfType<MethodSpec>()
            .Where(call => call.Name == nameof(PcCompatManagedComponentBridge.GetComponent))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(calls, Has.Length.EqualTo(2));
            Assert.That(
                calls.Select(call => call.DeclaringType.FullName),
                Is.All.EqualTo(typeof(PcCompatManagedComponentBridge).FullName));
            Assert.That(
                calls.Select(call => call.GenericInstMethodSig!.GenericArguments[0].FullName),
                Is.EquivalentTo(new[]
                {
                    "JipperResourcePack.KeyViewerContents.KeyViewer/KeyViewerUpdater",
                    "JipperResourcePack.KeyViewerContents.RainManager"
                }));
        });
    }

    [Test]
    public void RewritesManagedBatchAndTryComponentCalls()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var probe = module.GetTypes().Single(type => type.FullName == "PcCompat.Tests.UnrelatedBridgeProbe");
        var method = probe.Methods.Single(candidate => candidate.Name == "CallManagedGetComponents");
        var calls = method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Call)
            .Select(instruction => instruction.Operand)
            .OfType<MethodSpec>()
            .Where(call => call.DeclaringType.FullName == typeof(PcCompatManagedComponentBridge).FullName)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(calls.Count(call => call.Name == "GetComponents"), Is.EqualTo(2));
            Assert.That(calls.Count(call => call.Name == "TryGetComponent"), Is.EqualTo(2));
            Assert.That(
                calls.Where(call => call.Name.String is "GetComponents" or "TryGetComponent")
                    .Select(call => call.GenericInstMethodSig!.GenericArguments[0].FullName),
                Is.EquivalentTo(new[]
                {
                    "JipperResourcePack.KeyViewerContents.KeyViewer/KeyViewerUpdater",
                    "JipperResourcePack.KeyViewerContents.RainManager",
                    "JipperResourcePack.KeyViewerContents.KeyViewer/KeyViewerUpdater",
                    "JipperResourcePack.KeyViewerContents.RainManager"
                }));
        });
    }

    [Test]
    public void RewritesTypeOwnerAndDestroyCallsToManagedComponentBridge()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var probe = module.GetTypes().Single(type => type.FullName == "PcCompat.Tests.UnrelatedBridgeProbe");
        var bridgeType = typeof(PcCompatManagedComponentBridge).FullName;

        var typeRoutes = probe.Methods.Single(method => method.Name == "CallTypeComponentRoutes");
        var typeCalls = typeRoutes.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Call)
            .Select(instruction => instruction.Operand)
            .OfType<IMethod>()
            .Where(method => method.DeclaringType.FullName == bridgeType)
            .ToArray();
        var ownerProperties = probe.Methods.Single(method => method.Name == "CallManagedOwnerProperties");
        var ownerCalls = ownerProperties.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Call)
            .Select(instruction => instruction.Operand)
            .OfType<IMethod>()
            .Where(method => method.DeclaringType.FullName == bridgeType)
            .ToArray();
        var destroy = probe.Methods.Single(method => method.Name == "CallDestroy");
        var destroyCall = destroy.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Call)
            .Select(instruction => instruction.Operand)
            .OfType<IMethod>()
            .Single(method => method.DeclaringType.FullName == bridgeType);
        var destroyDelayed = probe.Methods.Single(method => method.Name == "CallDestroyDelayed");
        var destroyDelayedCall = destroyDelayed.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Call)
            .Select(instruction => instruction.Operand)
            .OfType<IMethod>()
            .Single(method => method.DeclaringType.FullName == bridgeType);

        Assert.Multiple(() =>
        {
            Assert.That(
                typeCalls.Select(method => method.Name.String),
                Is.EqualTo(new[]
                {
                    "AddComponent",
                    "GetComponent",
                    "GetComponent",
                    "GetComponents",
                    "GetComponents",
                    "TryGetComponent",
                    "TryGetComponent"
                }));
            Assert.That(
                typeRoutes.Body.Instructions.Count(instruction => instruction.OpCode.Code == Code.Castclass),
                Is.EqualTo(5));
            Assert.That(
                ownerCalls.Select(method => method.Name.String),
                Is.EqualTo(new[] { "GetGameObject", "GetTransform" }));
            Assert.That(
                ownerProperties.Body.Instructions.Count(instruction => instruction.OpCode.Code == Code.Castclass),
                Is.EqualTo(2));
            Assert.That(destroyCall.Name.String, Is.EqualTo("Destroy"));
            Assert.That(destroyCall.MethodSig!.Params, Has.Count.EqualTo(1));
            Assert.That(destroyDelayedCall.Name.String, Is.EqualTo("Destroy"));
            Assert.That(destroyDelayedCall.MethodSig!.Params, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void RewritesAndroidStrippedGUILayoutConvenienceCallsToManagedBridge()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var bridgeType = typeof(PcCompatManagedImGuiBridge).FullName;
        var calls = module.GetTypes()
            .SelectMany(type => type.Methods.Where(method => method.HasBody))
            .SelectMany(method => method.Body.Instructions)
            .Where(instruction => instruction.OpCode.Code == Code.Call)
            .Select(instruction => instruction.Operand)
            .OfType<IMethod>()
            .Where(method => method.DeclaringType.FullName == bridgeType)
            .Select(method => method.Name.String)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(calls, Does.Contain("ButtonText"));
            Assert.That(calls, Does.Contain(nameof(PcCompatManagedImGuiBridge.ButtonTextWithStyle)));
            Assert.That(calls, Does.Contain(nameof(PcCompatManagedImGuiBridge.ButtonTextureWithStyle)));
            Assert.That(calls, Does.Contain(nameof(PcCompatManagedImGuiBridge.ToggleTextWithStyle)));
            Assert.That(calls, Does.Contain(nameof(PcCompatManagedImGuiBridge.TextArea)));
            Assert.That(
                _report.ManagedBridgeRewrites.Count(item =>
                    item.BridgeMethod.Contains(nameof(PcCompatManagedImGuiBridge), StringComparison.Ordinal)),
                Is.GreaterThanOrEqualTo(3));
        });
    }

    [Test]
    public void RewritesUnity6StrippedGuiStyleSettersToManagedBridge()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var bridgeType = typeof(PcCompatManagedImGuiBridge).FullName;
        var calls = module.GetTypes()
            .SelectMany(type => type.Methods.Where(method => method.HasBody))
            .SelectMany(method => method.Body.Instructions)
            .Where(instruction => instruction.OpCode.Code == Code.Call)
            .Select(instruction => instruction.Operand)
            .OfType<IMethod>()
            .Where(method => method.DeclaringType.FullName == bridgeType)
            .Select(method => method.Name.String)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(calls, Does.Contain(nameof(PcCompatManagedImGuiBridge.SetFontSize)));
            Assert.That(calls, Does.Contain(nameof(PcCompatManagedImGuiBridge.SetFixedWidth)));
            Assert.That(calls, Does.Contain(nameof(PcCompatManagedImGuiBridge.SetNormal)));
            Assert.That(calls, Does.Contain(nameof(PcCompatManagedImGuiBridge.SetMargin)));
        });
    }

    [Test]
    public void RewritesNullableJalibInstanceSettingsCallbackBeforeDelegateConstruction()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var onGui = module.GetTypes()
            .Single(type => type.FullName == "JipperResourcePack.Main")
            .Methods.Single(method => method.Name == "OnGUI");
        var calls = onGui.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Call)
            .Select(instruction => instruction.Operand)
            .OfType<IMethod>()
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(calls.Any(method =>
                method.DeclaringType.FullName ==
                    "Xphorror.PcModCompat.PcCompatManagedSettingsDelegateBridge" &&
                method.Name == "CreateOptionalAction"), Is.True);
            Assert.That(onGui.Body.Instructions.Any(instruction =>
                instruction.OpCode.Code == Code.Newobj &&
                instruction.Operand is IMethod constructor &&
                constructor.DeclaringType.FullName == "System.Action"), Is.False);
        });
    }

    [Test]
    public void RewritesVirtualSettingsCallbackButLeavesNonSettingsDelegateUntouched()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var statusOnGui = module.GetTypes()
            .Single(type => type.FullName == "JipperResourcePack.OverlayContents.Status")
            .Methods.Single(method => method.Name == "OnGUI");
        var overlayUpdate = module.GetTypes()
            .Single(type => type.FullName == "JipperResourcePack.OverlayContents.Overlay")
            .Methods.Single(method => method.Name == "UpdateComboSize");
        var bridgeType = typeof(PcCompatManagedSettingsDelegateBridge).FullName;

        Assert.Multiple(() =>
        {
            Assert.That(statusOnGui.Body.Instructions.Any(instruction =>
                instruction.OpCode.Code == Code.Ldvirtftn), Is.False);
            Assert.That(statusOnGui.Body.Instructions.Count(instruction =>
                instruction.OpCode.Code == Code.Call &&
                instruction.Operand is IMethod method &&
                method.DeclaringType.FullName == bridgeType &&
                method.Name == nameof(PcCompatManagedSettingsDelegateBridge.CreateOptionalAction)),
                Is.GreaterThan(1));
            Assert.That(overlayUpdate.Body.Instructions.Any(instruction =>
                instruction.OpCode.Code == Code.Newobj &&
                instruction.Operand is IMethod constructor &&
                constructor.DeclaringType.FullName == "System.Action"), Is.True);
        });
    }

    [Test]
    public void RewritesExplicitCoroutineApisAndErasesOpaqueHandle()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var probe = module.GetTypes().Single(type => type.FullName == "PcCompat.Tests.UnrelatedBridgeProbe");
        var method = probe.Methods.Single(candidate => candidate.Name == "CallManagedCoroutineApis");
        var calls = method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Call)
            .Select(instruction => instruction.Operand)
            .OfType<IMethod>()
            .Where(call => call.DeclaringType.FullName == typeof(PcCompatManagedComponentBridge).FullName)
            .Select(call => call.Name.String)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(
                calls,
                Is.EqualTo(new[]
                {
                    "StartCoroutine",
                    "StopCoroutine",
                    "StopCoroutine",
                    "StopAllCoroutines",
                    "StartCoroutine",
                    "StartCoroutine",
                    "StopCoroutine"
                }));
            Assert.That(method.Body.Variables, Has.Count.EqualTo(1));
            Assert.That(method.Body.Variables[0].Type.FullName, Is.EqualTo("System.Object"));
            Assert.That(
                method.Body.Instructions.Any(instruction =>
                    instruction.Operand is ITypeDefOrRef type &&
                    type.FullName == "UnityEngine.Coroutine"),
                Is.False);
        });
    }

    [Test]
    public void GeneratedProxyContainsJipperCallbackSignatureTypes()
    {
        using var mod = ModuleDefMD.Load(_outputPath);
        var combo = mod.Find("JipperResourcePack.OverlayContents.Combo", isReflectionName: false);
        Assert.That(combo, Is.Not.Null);
        var onHit = combo!.Methods.Single(method =>
            method.Name == "OnHit" && method.MethodSig?.Params.Count == 1);
        var hitMargin = onHit.MethodSig!.Params[0];

        using var assemblyCSharp = ModuleDefMD.Load(Path.Combine(_proxyDirectory, "Assembly-CSharp.dll"));
        Assert.That(
            assemblyCSharp.Find(hitMargin.FullName, isReflectionName: false),
            Is.Not.Null,
            $"generated Assembly-CSharp proxy is missing callback signature type {hitMargin.FullName}");
    }

    [Test]
    public void GeneratedProxiesCoverAllJipperExternalTypeReferences()
    {
        using var mod = ModuleDefMD.Load(_outputPath);
        var proxies = Directory.EnumerateFiles(_proxyDirectory, "*.dll")
            .Select(path => ModuleDefMD.Load(path))
            .ToDictionary(module => module.Assembly.Name.String, StringComparer.OrdinalIgnoreCase);
        try
        {
            var missing = mod.GetTypeRefs()
                .Select(type => new
                {
                    Assembly = type.DefinitionAssembly?.Name?.String,
                    Type = type.FullName
                })
                .Where(reference => reference.Assembly is not null && proxies.ContainsKey(reference.Assembly))
                .Where(reference => proxies[reference.Assembly!].Find(reference.Type, isReflectionName: false) is null)
                .Select(reference => $"{reference.Assembly}|{reference.Type}")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(reference => reference, StringComparer.Ordinal)
                .ToArray();

            Assert.That(missing, Is.Empty, "generated proxies do not close the Jipper TypeRef surface");
        }
        finally
        {
            foreach (var proxy in proxies.Values)
                proxy.Dispose();
        }
    }

    [Test]
    public void JalibShimCoversAllJipperExternalTypeReferences()
    {
        using var mod = ModuleDefMD.Load(_outputPath);
        using var shim = ModuleDefMD.Load(typeof(JALib.Core.JAMod).Assembly.Location);
        var missing = mod.GetTypeRefs()
            .Where(type => string.Equals(
                type.DefinitionAssembly?.Name?.String,
                "JALib",
                StringComparison.OrdinalIgnoreCase))
            .Where(type => shim.Find(type.FullName, isReflectionName: false) is null)
            .Select(type => type.FullName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToArray();

        Assert.That(missing, Is.Empty, "JALib shim does not close the Jipper TypeRef surface");
    }

    [Test]
    public void RewritesProxyDowncastUsedByProgressBarHierarchyLookup()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var progressBar = module.GetTypes().Single(type =>
            type.FullName == "JipperResourcePack.OverlayContents.ProgressBar");
        var constructor = progressBar.Methods.Single(method => method.IsInstanceConstructor);

        Assert.That(
            constructor.Body.Instructions.Any(instruction =>
                instruction.OpCode.Code == Code.Isinst &&
                instruction.Operand is ITypeDefOrRef target &&
                target.FullName == "UnityEngine.RectTransform"),
            Is.False,
            "Mono-style 'as RectTransform' must use the IL2CPP-aware cast bridge.");
        Assert.That(
            constructor.Body.Instructions.Any(instruction =>
                instruction.OpCode.Code == Code.Call &&
                instruction.Operand is MethodSpec method &&
                method.Name == "IsInstance" &&
                method.GenericInstMethodSig?.GenericArguments.Single().FullName ==
                "UnityEngine.RectTransform"),
            Is.True);
    }

    [Test]
    public void RewriterRejectsUnboundGeneratedProxyTypeReference()
    {
        var inputPath = Path.Combine(_root, "missing-proxy-type.input.dll");
        var outputPath = Path.Combine(_root, "missing-proxy-type.output.dll");
        var reportPath = Path.Combine(_root, "missing-proxy-type.report.json");
        using (var module = ModuleDefMD.Load(_inputPath))
        {
            var assemblyCSharp = module.GetAssemblyRefs().Single(reference =>
                reference.Name == "Assembly-CSharp");
            var missingType = new TypeRefUser(
                module,
                string.Empty,
                "PcCompatMissingProxyProbe",
                assemblyCSharp);
            var probe = new TypeDefUser(
                "PcCompat.Tests",
                "MissingProxyTypeProbe",
                module.CorLibTypes.Object.TypeDefOrRef)
            {
                Attributes = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed
            };
            var method = new MethodDefUser(
                "Consume",
                MethodSig.CreateStatic(module.CorLibTypes.Void, new ClassSig(missingType)),
                MethodImplAttributes.IL | MethodImplAttributes.Managed,
                MethodAttributes.Public | MethodAttributes.Static)
            {
                Body = new CilBody()
            };
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            probe.Methods.Add(method);
            module.Types.Add(probe);
            module.Write(inputPath);
        }

        var report = ModAssemblyRewriteApi.Rewrite(
            inputPath,
            outputPath,
            _proxyDirectory,
            reportPath);

        Assert.Multiple(() =>
        {
            Assert.That(report.OutputWritten, Is.False);
            Assert.That(report.Issues, Does.Contain(
                "generated proxy type missing for metadata reference " +
                "Assembly-CSharp!PcCompatMissingProxyProbe"));
            Assert.That(File.Exists(outputPath), Is.False);
        });
    }

    [Test]
    public void RewritesZeroArgumentReversePatchCallToManagedBridge()
    {
        var records = _report.ManagedBridgeRewrites
            .Where(item => item.SourceMethod.Contains("GetHitMarginsCount", StringComparison.Ordinal))
            .ToArray();

        Assert.That(records, Is.Not.Empty);
        Assert.That(records, Has.All.Property(nameof(ManagedBridgeRewriteRecord.DroppedArguments)).EqualTo(0));
        AssertBridgeCallExists(nameof(PcCompatReversePatchBridge.GetHitMarginsCount));
    }

    [Test]
    public void DropsSourceArgumentBeforeZeroArgumentBridgeCall()
    {
        var records = _report.ManagedBridgeRewrites
            .Where(item => item.SourceMethod.Contains("GetPlanetSpeed", StringComparison.Ordinal))
            .ToArray();
        Assert.That(records, Is.Not.Empty);
        Assert.That(records, Has.All.Property(nameof(ManagedBridgeRewriteRecord.DroppedArguments)).EqualTo(1));

        using var module = ModuleDefMD.Load(_outputPath);
        var calls = FindBridgeCalls(module, nameof(PcCompatReversePatchBridge.GetPlanetSpeed)).ToArray();
        Assert.That(calls, Is.Not.Empty);
        foreach (var (method, instruction) in calls)
        {
            var index = method.Body.Instructions.IndexOf(instruction);
            Assert.That(index, Is.GreaterThan(0));
            Assert.That(method.Body.Instructions[index - 1].OpCode.Code, Is.EqualTo(Code.Pop));
        }
    }

    [Test]
    public void PreservesCompatibleLoadSceneArgument()
    {
        var records = _report.ManagedBridgeRewrites
            .Where(item => item.SourceMethod.Contains("LoadScene", StringComparison.Ordinal))
            .ToArray();
        Assert.That(records, Is.Not.Empty);
        Assert.That(records, Has.All.Property(nameof(ManagedBridgeRewriteRecord.DroppedArguments)).EqualTo(0));

        using var module = ModuleDefMD.Load(_outputPath);
        var calls = FindBridgeCalls(module, nameof(PcCompatReversePatchBridge.LoadScene)).ToArray();
        Assert.That(calls, Is.Not.Empty);
        foreach (var (method, instruction) in calls)
        {
            var index = method.Body.Instructions.IndexOf(instruction);
            Assert.That(index, Is.GreaterThan(0));
            Assert.That(method.Body.Instructions[index - 1].OpCode.Code, Is.Not.EqualTo(Code.Pop));
        }
    }

    [Test]
    public void DoesNotRewriteSameNamedMethodOnAnotherType()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var probe = module.GetTypes().Single(type => type.FullName == "PcCompat.Tests.UnrelatedBridgeProbe");
        var caller = probe.Methods.Single(method => method.Name == "CallGetHitMarginsCount");
        var call = caller.Body.Instructions.Single(instruction => instruction.OpCode.Code == Code.Call);
        var target = (IMethod)call.Operand;

        Assert.That(target.DeclaringType.FullName, Is.EqualTo(probe.FullName));
        Assert.That(target.Name.String, Is.EqualTo("GetHitMarginsCount"));
    }

    [Test]
    public void RewritesAssetBundleLoadFromFileToVirtualHandleAndErasesStorageType()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var calls = FindBridgeCalls(
                module,
                typeof(PcCompatManagedResourceBridge).FullName!,
                nameof(PcCompatManagedResourceBridge.LoadAssetBundleFromFile))
            .ToArray();

        Assert.That(calls, Is.Not.Empty);
        foreach (var (method, instruction) in calls)
        {
            var index = method.Body.Instructions.IndexOf(instruction);
            if (index < method.Body.Instructions.Count - 1)
                Assert.That(method.Body.Instructions[index + 1].OpCode.Code, Is.Not.EqualTo(Code.Castclass));
        }
        var bundleLoader = module.GetTypes().Single(type => type.FullName == "JipperResourcePack.BundleLoader");
        var bundleField = bundleLoader.Fields.Single(field => field.Name == "_bundle");
        Assert.That(bundleField.FieldSig!.Type.FullName, Is.EqualTo("System.Object"));
    }

    [Test]
    public void RewritesAssetBundleLoadAllAssetsToVirtualBridgeAndCastsArray()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var calls = FindBridgeCalls(
                module,
                typeof(PcCompatManagedResourceBridge).FullName!,
                nameof(PcCompatManagedResourceBridge.LoadAllAssetsFromBundle))
            .ToArray();
        Assert.That(calls, Is.Not.Empty);
        foreach (var (method, instruction) in calls)
        {
            var index = method.Body.Instructions.IndexOf(instruction);
            Assert.That(index, Is.LessThan(method.Body.Instructions.Count - 1));
            var cast = method.Body.Instructions[index + 1];
            Assert.That(cast.OpCode.Code, Is.EqualTo(Code.Castclass));
            Assert.That(((ITypeDefOrRef)cast.Operand).FullName, Is.EqualTo("UnityEngine.Object[]"));
        }
    }

    [Test]
    public void RewritesClosedGenericAssetBundleCallsAndPreservesTypeArgument()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var probe = module.GetTypes().Single(type => type.FullName == "PcCompat.Tests.UnrelatedBridgeProbe");
        AssertGenericAssetBridge(
            probe.Methods.Single(method => method.Name == "LoadSpriteGeneric"),
            nameof(PcCompatManagedResourceBridge.LoadAssetFromBundleGeneric),
            "UnityEngine.Sprite");
        AssertGenericAssetBridge(
            probe.Methods.Single(method => method.Name == "LoadAllSpritesGeneric"),
            nameof(PcCompatManagedResourceBridge.LoadAllAssetsFromBundleGeneric),
            "UnityEngine.Sprite[]");
    }

    [Test]
    public void RewritesAssetBundleUnloadToHostOwnedRelease()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var calls = FindBridgeCalls(
                module,
                typeof(PcCompatManagedResourceBridge).FullName!,
                nameof(PcCompatManagedResourceBridge.ReleaseAssetBundle))
            .ToArray();

        Assert.That(calls, Is.Not.Empty);
        Assert.That(calls.Select(item => item.Instruction.OpCode.Code), Is.All.EqualTo(Code.Call));
    }

    [Test]
    public void ArrayConvertersPreserveDeclaringTypeGenericVariableInMemberSignature()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var converters = module.GetMemberRefs()
            .Where(member =>
                member.Name == "op_Implicit" &&
                member.DeclaringType.FullName.StartsWith(
                    "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase`1<",
                    StringComparison.Ordinal))
            .ToArray();

        Assert.That(converters, Is.Not.Empty);
        foreach (var converter in converters)
        {
            Assert.Multiple(() =>
            {
                Assert.That(converter.DeclaringType.ToTypeSig(), Is.TypeOf<GenericInstSig>());
                Assert.That(converter.MethodSig, Is.Not.Null);
                Assert.That(converter.MethodSig!.RetType, Is.TypeOf<SZArraySig>());
                Assert.That(
                    ((SZArraySig)converter.MethodSig.RetType).Next,
                    Is.TypeOf<GenericVar>().And.Property(nameof(GenericVar.Number)).EqualTo(0));
                Assert.That(converter.MethodSig.Params, Has.Count.EqualTo(1));
                Assert.That(converter.MethodSig.Params[0], Is.TypeOf<GenericInstSig>());
            });

            var parameter = (GenericInstSig)converter.MethodSig!.Params[0];
            Assert.Multiple(() =>
            {
                Assert.That(
                    parameter.GenericType.TypeDefOrRef.FullName,
                    Is.EqualTo("Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase`1"));
                Assert.That(parameter.GenericArguments, Has.Count.EqualTo(1));
                Assert.That(
                    parameter.GenericArguments[0],
                    Is.TypeOf<GenericVar>().And.Property(nameof(GenericVar.Number)).EqualTo(0));
            });
        }
    }

    [Test]
    public void RewritesTypeBasedJalibPatchMetadataToStringTargets()
    {
        Assert.That(_report.FormatVersion, Is.EqualTo(ModAssemblyRewriteApi.FormatVersion));
        Assert.That(_report.PatchMetadataRewrites, Is.Not.Empty);
        Assert.That(
            _report.PatchMetadataRewrites.Any(item =>
                item.TargetType == "scrPressToStart"),
            Is.True);

        using var module = ModuleDefMD.Load(_outputPath);
        var main = module.GetTypes().Single(type => type.FullName == "JipperResourcePack.Main");
        var callback = main.Methods.Single(method => method.Name == "OnGameStart2");
        var patch = callback.CustomAttributes.Single(attribute =>
            attribute.AttributeType.FullName == "JALib.Core.Patch.JAPatchAttribute");

        Assert.Multiple(() =>
        {
            Assert.That(patch.Constructor.MethodSig, Is.Not.Null);
            Assert.That(
                patch.Constructor.MethodSig!.Params[0].FullName,
                Is.EqualTo("System.String"));
            Assert.That(
                patch.ConstructorArguments[0].Value?.ToString(),
                Is.EqualTo("scrPressToStart"));
        });
    }

    [Test]
    public void RejectsAbiIncompatibleBridgeWithoutWritingOutput()
    {
        var output = Path.Combine(_root, "invalid.rewritten.dll");
        var report = ModAssemblyRewriteApi.Rewrite(
            _inputPath,
            output,
            _proxyDirectory,
            Path.Combine(_root, "invalid-report.json"),
            managedBridgeAssemblyPath: _bridgeAssemblyPath,
            managedBridgeRewrites:
            [
                new ManagedBridgeRewriteSpec(
                    "JipperResourcePack.VersionSafe",
                    "LoadScene",
                    Array.Empty<string>(),
                    typeof(PcCompatReversePatchBridge).FullName!,
                    nameof(PcCompatReversePatchBridge.GetPlanetSpeed))
            ]);

        Assert.That(report.ManagedBridgeIssues, Is.Not.Empty);
        Assert.That(report.OutputWritten, Is.False);
        Assert.That(File.Exists(output), Is.False);
    }

    [Test]
    public void RejectsMissingUnityProxyAssemblyAndRemovesStaleOutput()
    {
        var incompleteProxyDirectory = Path.Combine(_root, "incomplete-proxies");
        Directory.CreateDirectory(incompleteProxyDirectory);
        foreach (var source in Directory.EnumerateFiles(_proxyDirectory, "*.dll"))
        {
            if (Path.GetFileName(source).Equals(
                    "UnityEngine.AssetBundleModule.dll",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            File.Copy(source, Path.Combine(incompleteProxyDirectory, Path.GetFileName(source)));
        }

        var output = Path.Combine(_root, "missing-proxy.rewritten.dll");
        File.WriteAllText(output, "stale output must not survive");
        var report = ModAssemblyRewriteApi.Rewrite(
            _inputPath,
            output,
            incompleteProxyDirectory,
            Path.Combine(_root, "missing-proxy-report.json"));

        Assert.That(
            report.MethodIssues.Any(issue =>
                issue.Reason.Contains("UnityEngine.AssetBundleModule", StringComparison.Ordinal)),
            Is.True);
        Assert.That(report.OutputWritten, Is.False);
        Assert.That(File.Exists(output), Is.False);
    }

    [Test]
    public void ExternalBridgeRejectsMissingSourceProxyBeforeWritingOutput()
    {
        var incompleteProxyDirectory = Path.Combine(_root, "incomplete-call-proxies");
        Directory.CreateDirectory(incompleteProxyDirectory);
        foreach (var source in Directory.EnumerateFiles(_proxyDirectory, "*.dll"))
        {
            if (!Path.GetFileName(source).Equals(
                    "UnityEngine.AssetBundleModule.dll",
                    StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(source, Path.Combine(incompleteProxyDirectory, Path.GetFileName(source)));
            }
        }

        var output = Path.Combine(_root, "missing-call-proxy.rewritten.dll");
        var report = ModAssemblyRewriteApi.Rewrite(
            _inputPath,
            output,
            incompleteProxyDirectory,
            Path.Combine(_root, "missing-call-proxy-report.json"),
            managedBridgeAssemblyPath: _bridgeAssemblyPath,
            managedCallBridgeRewrites: _callSpecs);

        Assert.That(
            report.ManagedBridgeIssues.Any(issue =>
                issue.Reason.Contains("generated proxy assembly missing", StringComparison.Ordinal)),
            Is.True);
        Assert.That(report.OutputWritten, Is.False);
        Assert.That(File.Exists(output), Is.False);
    }

    private void AssertBridgeCallExists(string methodName)
    {
        using var module = ModuleDefMD.Load(_outputPath);
        Assert.That(FindBridgeCalls(module, methodName), Is.Not.Empty);
    }

    private static IEnumerable<(MethodDef Method, Instruction Instruction)> FindBridgeCalls(
        ModuleDef module,
        string methodName)
        => FindBridgeCalls(module, typeof(PcCompatReversePatchBridge).FullName!, methodName);

    private static IEnumerable<(MethodDef Method, Instruction Instruction)> FindBridgeCalls(
        ModuleDef module,
        string bridgeType,
        string methodName)
        => module.GetTypes()
            .SelectMany(type => type.Methods.Where(method => method.HasBody))
            .SelectMany(method => method.Body.Instructions
                .Where(instruction => instruction.OpCode.Code == Code.Call &&
                                      instruction.Operand is IMethod target &&
                                      target.DeclaringType.FullName == bridgeType &&
                                      target.Name == methodName)
                .Select(instruction => (method, instruction)));

    private static void AssertGenericAssetBridge(
        MethodDef method,
        string expectedBridgeMethod,
        string expectedCastType)
    {
        var call = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code == Code.Call &&
            instruction.Operand is IMethod target &&
            target.DeclaringType.FullName == typeof(PcCompatManagedResourceBridge).FullName &&
            target.Name == expectedBridgeMethod);
        var target = (IMethod)call.Operand;
        Assert.That(target, Is.InstanceOf<MethodSpec>());
        var methodSpec = (MethodSpec)target;
        Assert.That(methodSpec.GenericInstMethodSig.GenericArguments, Has.Count.EqualTo(1));
        Assert.That(
            methodSpec.GenericInstMethodSig.GenericArguments[0].FullName,
            Is.EqualTo("UnityEngine.Sprite"));
        var index = method.Body.Instructions.IndexOf(call);
        Assert.That(index, Is.LessThan(method.Body.Instructions.Count - 1));
        var cast = method.Body.Instructions[index + 1];
        Assert.That(cast.OpCode.Code, Is.EqualTo(Code.Castclass));
        Assert.That(((ITypeDefOrRef)cast.Operand).FullName, Is.EqualTo(expectedCastType));
        Assert.That(method.MethodSig!.Params[0].FullName, Is.EqualTo("System.Object"));
    }

    private static IReadOnlyList<ManagedCallBridgeRewriteSpec> CreateManagedCallSpecs()
    {
        var resourceBridgeType = typeof(PcCompatManagedResourceBridge).FullName!;
        var componentBridgeType = typeof(PcCompatManagedComponentBridge).FullName!;
        var logBridgeType = typeof(PcCompatManagedLogBridge).FullName!;
        var inputBridgeType = typeof(PcCompatLegacyInputBridge).FullName!;
        var imGuiBridgeType = typeof(PcCompatManagedImGuiBridge).FullName!;
        var threadBridgeType = typeof(PcCompatManagedThreadBridge).FullName!;
        return
        [
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.Threading.Thread",
                "Abort",
                false,
                0,
                "System.Void",
                [],
                threadBridgeType,
                nameof(PcCompatManagedThreadBridge.Abort),
                ManagedCallInstanceForwarding.AsObject,
                false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUIStyle",
                "set_fontSize",
                false,
                0,
                "System.Void",
                ["System.Int32"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.SetFontSize),
                ManagedCallInstanceForwarding.AsObject,
                false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUIStyle",
                "set_fixedWidth",
                false,
                0,
                "System.Void",
                ["System.Single"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.SetFixedWidth),
                ManagedCallInstanceForwarding.AsObject,
                false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUIStyle",
                "set_normal",
                false,
                0,
                "System.Void",
                ["UnityEngine.GUIStyleState"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.SetNormal),
                ManagedCallInstanceForwarding.AsObject,
                false,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUIStyle",
                "set_margin",
                false,
                0,
                "System.Void",
                ["UnityEngine.RectOffset"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.SetMargin),
                ManagedCallInstanceForwarding.AsObject,
                false,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "Button",
                true,
                0,
                "System.Boolean",
                ["System.String", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                "ButtonText",
                ManagedCallInstanceForwarding.None,
                false,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "Button",
                true,
                0,
                "System.Boolean",
                ["System.String", "UnityEngine.GUIStyle", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.ButtonTextWithStyle),
                ManagedCallInstanceForwarding.None,
                false,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "Toggle",
                true,
                0,
                "System.Boolean",
                ["System.Boolean", "System.String", "UnityEngine.GUIStyle", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.ToggleTextWithStyle),
                ManagedCallInstanceForwarding.None,
                false,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "Button",
                true,
                0,
                "System.Boolean",
                ["UnityEngine.Texture", "UnityEngine.GUIStyle", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.ButtonTextureWithStyle),
                ManagedCallInstanceForwarding.None,
                false,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "TextArea",
                true,
                0,
                "System.String",
                ["System.String", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.TextArea),
                ManagedCallInstanceForwarding.None,
                false,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.InputLegacyModule",
                "UnityEngine.Input",
                "GetKey",
                true,
                0,
                "System.Boolean",
                ["UnityEngine.KeyCode"],
                inputBridgeType,
                nameof(PcCompatLegacyInputBridge.GetKeyOwned),
                ManagedCallInstanceForwarding.None,
                false,
                BridgeGenericArgumentsFromSourceParameters: [0],
                AppendCallsiteToken: true,
                AppendOwnerId: "RewriteTestMod"),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.InputLegacyModule",
                "UnityEngine.Input",
                "GetKeyDown",
                true,
                0,
                "System.Boolean",
                ["UnityEngine.KeyCode"],
                inputBridgeType,
                nameof(PcCompatLegacyInputBridge.GetKeyDownOwned),
                ManagedCallInstanceForwarding.None,
                false,
                BridgeGenericArgumentsFromSourceParameters: [0],
                AppendCallsiteToken: true,
                AppendOwnerId: "RewriteTestMod"),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.InputLegacyModule",
                "UnityEngine.Input",
                "GetKeyUp",
                true,
                0,
                "System.Boolean",
                ["UnityEngine.KeyCode"],
                inputBridgeType,
                nameof(PcCompatLegacyInputBridge.GetKeyUpOwned),
                ManagedCallInstanceForwarding.None,
                false,
                BridgeGenericArgumentsFromSourceParameters: [0],
                AppendCallsiteToken: true,
                AppendOwnerId: "RewriteTestMod"),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.InputLegacyModule",
                "UnityEngine.Input",
                "get_anyKeyDown",
                true,
                0,
                "System.Boolean",
                [],
                inputBridgeType,
                nameof(PcCompatLegacyInputBridge.GetAnyKeyDownOwned),
                ManagedCallInstanceForwarding.None,
                false,
                AppendCallsiteToken: true,
                AppendOwnerId: "RewriteTestMod"),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Debug",
                "LogException",
                true,
                0,
                "System.Void",
                ["System.Exception"],
                logBridgeType,
                nameof(PcCompatManagedLogBridge.LogException),
                ManagedCallInstanceForwarding.None,
                false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Behaviour",
                "get_enabled",
                false,
                0,
                "System.Boolean",
                [],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetEnabled),
                ManagedCallInstanceForwarding.AsObject,
                false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Behaviour",
                "set_enabled",
                false,
                0,
                "System.Void",
                ["System.Boolean"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.SetEnabled),
                ManagedCallInstanceForwarding.AsObject,
                false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.GameObject",
                "AddComponent",
                false,
                1,
                "!!0",
                [],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.AddComponent),
                ManagedCallInstanceForwarding.AsObject,
                false,
                GenericArgumentFilter: ManagedCallGenericArgumentFilter.ModOwnedMonoBehaviour),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.GameObject",
                "GetComponent",
                false,
                1,
                "!!0",
                [],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetComponent),
                ManagedCallInstanceForwarding.AsObject,
                false,
                GenericArgumentFilter: ManagedCallGenericArgumentFilter.ModOwnedMonoBehaviour),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Component",
                "GetComponent",
                false,
                1,
                "!!0",
                [],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetComponent),
                ManagedCallInstanceForwarding.AsObject,
                false,
                GenericArgumentFilter: ManagedCallGenericArgumentFilter.ModOwnedMonoBehaviour),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.GameObject",
                "GetComponents",
                false,
                1,
                "!!0[]",
                [],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetComponents),
                ManagedCallInstanceForwarding.AsObject,
                false,
                GenericArgumentFilter: ManagedCallGenericArgumentFilter.ModOwnedMonoBehaviour),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Component",
                "GetComponents",
                false,
                1,
                "!!0[]",
                [],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetComponents),
                ManagedCallInstanceForwarding.AsObject,
                false,
                GenericArgumentFilter: ManagedCallGenericArgumentFilter.ModOwnedMonoBehaviour),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.GameObject",
                "TryGetComponent",
                false,
                1,
                "System.Boolean",
                ["!!0&"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.TryGetComponent),
                ManagedCallInstanceForwarding.AsObject,
                false,
                GenericArgumentFilter: ManagedCallGenericArgumentFilter.ModOwnedMonoBehaviour),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Component",
                "TryGetComponent",
                false,
                1,
                "System.Boolean",
                ["!!0&"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.TryGetComponent),
                ManagedCallInstanceForwarding.AsObject,
                false,
                GenericArgumentFilter: ManagedCallGenericArgumentFilter.ModOwnedMonoBehaviour),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.GameObject",
                "AddComponent",
                false,
                0,
                "UnityEngine.Component",
                ["System.Type"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.AddComponent),
                ManagedCallInstanceForwarding.AsObject,
                true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.GameObject",
                "GetComponent",
                false,
                0,
                "UnityEngine.Component",
                ["System.Type"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetComponent),
                ManagedCallInstanceForwarding.AsObject,
                true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Component",
                "GetComponent",
                false,
                0,
                "UnityEngine.Component",
                ["System.Type"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetComponent),
                ManagedCallInstanceForwarding.AsObject,
                true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.GameObject",
                "GetComponents",
                false,
                0,
                "UnityEngine.Component[]",
                ["System.Type"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetComponents),
                ManagedCallInstanceForwarding.AsObject,
                true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.GameObject",
                "TryGetComponent",
                false,
                0,
                "System.Boolean",
                ["System.Type", "UnityEngine.Component&"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.TryGetComponent),
                ManagedCallInstanceForwarding.AsObject,
                false,
                BridgeGenericArgumentsFromSourceParameters: [1]),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Component",
                "TryGetComponent",
                false,
                0,
                "System.Boolean",
                ["System.Type", "UnityEngine.Component&"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.TryGetComponent),
                ManagedCallInstanceForwarding.AsObject,
                false,
                BridgeGenericArgumentsFromSourceParameters: [1]),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Component",
                "GetComponents",
                false,
                0,
                "UnityEngine.Component[]",
                ["System.Type"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetComponents),
                ManagedCallInstanceForwarding.AsObject,
                true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Component",
                "get_gameObject",
                false,
                0,
                "UnityEngine.GameObject",
                [],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetGameObject),
                ManagedCallInstanceForwarding.AsObject,
                true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Component",
                "get_transform",
                false,
                0,
                "UnityEngine.Transform",
                [],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetTransform),
                ManagedCallInstanceForwarding.AsObject,
                true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Object",
                "DontDestroyOnLoad",
                true,
                0,
                "System.Void",
                ["UnityEngine.Object"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.DontDestroyOnLoad),
                ManagedCallInstanceForwarding.None,
                false,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Object",
                "Destroy",
                true,
                0,
                "System.Void",
                ["UnityEngine.Object"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.Destroy),
                ManagedCallInstanceForwarding.None,
                false,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.MonoBehaviour",
                "StartCoroutine",
                false,
                0,
                "UnityEngine.Coroutine",
                ["System.Collections.IEnumerator"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.StartCoroutine),
                ManagedCallInstanceForwarding.AsObject,
                false,
                ErasedTypeAssembly: "UnityEngine.CoreModule",
                ErasedType: "UnityEngine.Coroutine"),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.MonoBehaviour",
                "StartCoroutine",
                false,
                0,
                "UnityEngine.Coroutine",
                ["System.String"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.StartCoroutine),
                ManagedCallInstanceForwarding.AsObject,
                false,
                ErasedTypeAssembly: "UnityEngine.CoreModule",
                ErasedType: "UnityEngine.Coroutine"),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.MonoBehaviour",
                "StartCoroutine",
                false,
                0,
                "UnityEngine.Coroutine",
                ["System.String", "System.Object"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.StartCoroutine),
                ManagedCallInstanceForwarding.AsObject,
                false,
                ErasedTypeAssembly: "UnityEngine.CoreModule",
                ErasedType: "UnityEngine.Coroutine"),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.MonoBehaviour",
                "StopCoroutine",
                false,
                0,
                "System.Void",
                ["System.Collections.IEnumerator"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.StopCoroutine),
                ManagedCallInstanceForwarding.AsObject,
                false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.MonoBehaviour",
                "StopCoroutine",
                false,
                0,
                "System.Void",
                ["UnityEngine.Coroutine"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.StopCoroutine),
                ManagedCallInstanceForwarding.AsObject,
                false,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.MonoBehaviour",
                "StopCoroutine",
                false,
                0,
                "System.Void",
                ["System.String"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.StopCoroutine),
                ManagedCallInstanceForwarding.AsObject,
                false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.MonoBehaviour",
                "StopAllCoroutines",
                false,
                0,
                "System.Void",
                [],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.StopAllCoroutines),
                ManagedCallInstanceForwarding.AsObject,
                false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Object",
                "Destroy",
                true,
                0,
                "System.Void",
                ["UnityEngine.Object", "System.Single"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.Destroy),
                ManagedCallInstanceForwarding.None,
                false,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.AssetBundleModule",
                "UnityEngine.AssetBundle",
                "LoadFromFile",
                true,
                0,
                "UnityEngine.AssetBundle",
                ["System.String"],
                resourceBridgeType,
                nameof(PcCompatManagedResourceBridge.LoadAssetBundleFromFile),
                ManagedCallInstanceForwarding.None,
                false,
                true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.AssetBundleModule",
                "UnityEngine.AssetBundle",
                "LoadAsset",
                false,
                0,
                "UnityEngine.Object",
                ["System.String"],
                resourceBridgeType,
                nameof(PcCompatManagedResourceBridge.LoadAssetFromBundle),
                ManagedCallInstanceForwarding.AsObject,
                true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.AssetBundleModule",
                "UnityEngine.AssetBundle",
                "LoadAsset",
                false,
                0,
                "UnityEngine.Object",
                ["System.String", "System.Type"],
                resourceBridgeType,
                nameof(PcCompatManagedResourceBridge.LoadAssetFromBundleWithType),
                ManagedCallInstanceForwarding.AsObject,
                true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.AssetBundleModule",
                "UnityEngine.AssetBundle",
                "LoadAsset",
                false,
                1,
                "!!0",
                ["System.String"],
                resourceBridgeType,
                nameof(PcCompatManagedResourceBridge.LoadAssetFromBundleGeneric),
                ManagedCallInstanceForwarding.AsObject,
                true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.AssetBundleModule",
                "UnityEngine.AssetBundle",
                "LoadAllAssets",
                false,
                0,
                "UnityEngine.Object[]",
                [],
                resourceBridgeType,
                nameof(PcCompatManagedResourceBridge.LoadAllAssetsFromBundle),
                ManagedCallInstanceForwarding.AsObject,
                true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.AssetBundleModule",
                "UnityEngine.AssetBundle",
                "LoadAllAssets",
                false,
                0,
                "UnityEngine.Object[]",
                ["System.Type"],
                resourceBridgeType,
                nameof(PcCompatManagedResourceBridge.LoadAllAssetsFromBundleWithType),
                ManagedCallInstanceForwarding.AsObject,
                true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.AssetBundleModule",
                "UnityEngine.AssetBundle",
                "LoadAllAssets",
                false,
                1,
                "!!0[]",
                [],
                resourceBridgeType,
                nameof(PcCompatManagedResourceBridge.LoadAllAssetsFromBundleGeneric),
                ManagedCallInstanceForwarding.AsObject,
                true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.AssetBundleModule",
                "UnityEngine.AssetBundle",
                "Unload",
                false,
                0,
                "System.Void",
                ["System.Boolean"],
                resourceBridgeType,
                nameof(PcCompatManagedResourceBridge.ReleaseAssetBundle),
                ManagedCallInstanceForwarding.AsObject,
                false)
        ];
    }

    private static void AddUnrelatedSameNamedCall(string sourcePath, string outputPath)
    {
        using var module = ModuleDefMD.Load(sourcePath);
        var probe = new TypeDefUser(
            "PcCompat.Tests",
            "UnrelatedBridgeProbe",
            module.CorLibTypes.Object.TypeDefOrRef)
        {
            Attributes = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed
        };
        module.Types.Add(probe);

        var arrayType = new SZArraySig(module.CorLibTypes.Int32);
        var standIn = new MethodDefUser(
            "GetHitMarginsCount",
            MethodSig.CreateStatic(arrayType),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static);
        standIn.Body = new CilBody();
        standIn.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        standIn.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        probe.Methods.Add(standIn);

        var caller = new MethodDefUser(
            "CallGetHitMarginsCount",
            MethodSig.CreateStatic(module.CorLibTypes.Void),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static);
        caller.Body = new CilBody();
        caller.Body.Instructions.Add(Instruction.Create(OpCodes.Call, standIn));
        caller.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        caller.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        probe.Methods.Add(caller);

        AddGenericAssetBundleCalls(module, probe);
        AddManagedComponentCalls(module, probe);
        AddUnrelatedThreadYield(module, probe);

        module.Write(outputPath);
    }

    private static void AddUnrelatedThreadYield(ModuleDef module, TypeDef probe)
    {
        var threadType = new TypeRefUser(
            module,
            "System.Threading",
            "Thread",
            module.CorLibTypes.AssemblyRef);
        var yield = new MemberRefUser(
            module,
            "Yield",
            MethodSig.CreateStatic(module.CorLibTypes.Boolean),
            threadType);
        var caller = new MethodDefUser(
            "CallUnrelatedThreadYield",
            MethodSig.CreateStatic(module.CorLibTypes.Void),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static)
        {
            Body = new CilBody()
        };
        caller.Body.Instructions.Add(Instruction.Create(OpCodes.Call, yield));
        caller.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        caller.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        probe.Methods.Add(caller);
    }

    private static void AddGenericAssetBundleCalls(ModuleDef module, TypeDef probe)
    {
        var assetBundleAssembly = module.GetAssemblyRefs().Single(reference =>
            reference.Name == "UnityEngine.AssetBundleModule");
        var coreAssembly = module.GetAssemblyRefs().Single(reference =>
            reference.Name == "UnityEngine.CoreModule");
        var assetBundleType = new TypeRefUser(
            module,
            "UnityEngine",
            "AssetBundle",
            assetBundleAssembly);
        var spriteType = new TypeRefUser(
            module,
            "UnityEngine",
            "Sprite",
            coreAssembly);
        var sprite = new ClassSig(spriteType);
        var bundle = new ClassSig(assetBundleType);
        var methodVariable = new GenericMVar(0);

        var loadAssetSignature = MethodSig.CreateInstance(methodVariable, module.CorLibTypes.String);
        loadAssetSignature.Generic = true;
        loadAssetSignature.GenParamCount = 1;
        var loadAsset = new MethodSpecUser(
            new MemberRefUser(module, "LoadAsset", loadAssetSignature, assetBundleType),
            new GenericInstMethodSig(sprite));
        var loadSprite = new MethodDefUser(
            "LoadSpriteGeneric",
            MethodSig.CreateStatic(sprite, bundle, module.CorLibTypes.String),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static)
        {
            Body = new CilBody()
        };
        loadSprite.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        loadSprite.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        loadSprite.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, loadAsset));
        loadSprite.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        probe.Methods.Add(loadSprite);

        var loadAllSignature = MethodSig.CreateInstance(new SZArraySig(new GenericMVar(0)));
        loadAllSignature.Generic = true;
        loadAllSignature.GenParamCount = 1;
        var loadAllAssets = new MethodSpecUser(
            new MemberRefUser(module, "LoadAllAssets", loadAllSignature, assetBundleType),
            new GenericInstMethodSig(sprite));
        var loadSprites = new MethodDefUser(
            "LoadAllSpritesGeneric",
            MethodSig.CreateStatic(new SZArraySig(sprite), bundle),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static)
        {
            Body = new CilBody()
        };
        loadSprites.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        loadSprites.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, loadAllAssets));
        loadSprites.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        probe.Methods.Add(loadSprites);
    }

    private static void AddManagedComponentCalls(ModuleDef module, TypeDef probe)
    {
        var coreAssembly = module.GetAssemblyRefs().Single(reference =>
            reference.Name == "UnityEngine.CoreModule");
        var gameObjectType = new TypeRefUser(
            module,
            "UnityEngine",
            "GameObject",
            coreAssembly);
        var componentType = new TypeRefUser(
            module,
            "UnityEngine",
            "Component",
            coreAssembly);
        var updaterType = module.GetTypes().Single(type =>
            type.FullName == "JipperResourcePack.KeyViewerContents.KeyViewer/KeyViewerUpdater");
        var rainManagerType = module.GetTypes().Single(type =>
            type.FullName == "JipperResourcePack.KeyViewerContents.RainManager");

        static MethodSpec BuildGetComponent(
            ModuleDef targetModule,
            ITypeDefOrRef ownerType,
            ITypeDefOrRef managedType)
        {
            var variable = new GenericMVar(0);
            var signature = MethodSig.CreateInstance(variable);
            signature.Generic = true;
            signature.GenParamCount = 1;
            return new MethodSpecUser(
                new MemberRefUser(targetModule, "GetComponent", signature, ownerType),
                new GenericInstMethodSig(new ClassSig(managedType)));
        }

        static MethodSpec BuildGetComponents(
            ModuleDef targetModule,
            ITypeDefOrRef ownerType,
            ITypeDefOrRef managedType)
        {
            var variable = new GenericMVar(0);
            var signature = MethodSig.CreateInstance(new SZArraySig(variable));
            signature.Generic = true;
            signature.GenParamCount = 1;
            return new MethodSpecUser(
                new MemberRefUser(targetModule, "GetComponents", signature, ownerType),
                new GenericInstMethodSig(new ClassSig(managedType)));
        }

        static MethodSpec BuildTryGetComponent(
            ModuleDef targetModule,
            ITypeDefOrRef ownerType,
            ITypeDefOrRef managedType)
        {
            var variable = new GenericMVar(0);
            var signature = MethodSig.CreateInstance(
                targetModule.CorLibTypes.Boolean,
                new ByRefSig(variable));
            signature.Generic = true;
            signature.GenParamCount = 1;
            return new MethodSpecUser(
                new MemberRefUser(targetModule, "TryGetComponent", signature, ownerType),
                new GenericInstMethodSig(new ClassSig(managedType)));
        }

        var method = new MethodDefUser(
            "CallManagedGetComponents",
            MethodSig.CreateStatic(
                module.CorLibTypes.Void,
                new ClassSig(gameObjectType),
                new ClassSig(componentType)),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static)
        {
            Body = new CilBody()
        };
        method.Body.Variables.Add(new Local(new ClassSig(updaterType)));
        method.Body.Variables.Add(new Local(new ClassSig(rainManagerType)));
        method.Body.InitLocals = true;
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        method.Body.Instructions.Add(Instruction.Create(
            OpCodes.Callvirt,
            BuildGetComponent(module, gameObjectType, updaterType)));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        method.Body.Instructions.Add(Instruction.Create(
            OpCodes.Callvirt,
            BuildGetComponent(module, componentType, rainManagerType)));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        method.Body.Instructions.Add(Instruction.Create(
            OpCodes.Callvirt,
            BuildGetComponents(module, gameObjectType, updaterType)));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        method.Body.Instructions.Add(Instruction.Create(
            OpCodes.Callvirt,
            BuildGetComponents(module, componentType, rainManagerType)));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloca, method.Body.Variables[0]));
        method.Body.Instructions.Add(Instruction.Create(
            OpCodes.Callvirt,
            BuildTryGetComponent(module, gameObjectType, updaterType)));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloca, method.Body.Variables[1]));
        method.Body.Instructions.Add(Instruction.Create(
            OpCodes.Callvirt,
            BuildTryGetComponent(module, componentType, rainManagerType)));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        probe.Methods.Add(method);

        var unityObjectType = new TypeRefUser(
            module,
            "UnityEngine",
            "Object",
            coreAssembly);
        var transformType = new TypeRefUser(
            module,
            "UnityEngine",
            "Transform",
            coreAssembly);
        var systemType = new TypeRefUser(
            module,
            "System",
            "Type",
            module.CorLibTypes.AssemblyRef);
        var gameObject = new ClassSig(gameObjectType);
        var component = new ClassSig(componentType);
        var reflectionType = new ClassSig(systemType);

        var typeRoutes = new MethodDefUser(
            "CallTypeComponentRoutes",
            MethodSig.CreateStatic(module.CorLibTypes.Void, gameObject, component, reflectionType),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static)
        {
            Body = new CilBody()
        };
        var addByType = new MemberRefUser(
            module,
            "AddComponent",
            MethodSig.CreateInstance(component, reflectionType),
            gameObjectType);
        var gameObjectGetByType = new MemberRefUser(
            module,
            "GetComponent",
            MethodSig.CreateInstance(component, reflectionType),
            gameObjectType);
        var componentGetByType = new MemberRefUser(
            module,
            "GetComponent",
            MethodSig.CreateInstance(component, reflectionType),
            componentType);
        var componentArray = new SZArraySig(component);
        var gameObjectGetManyByType = new MemberRefUser(
            module,
            "GetComponents",
            MethodSig.CreateInstance(componentArray, reflectionType),
            gameObjectType);
        var componentGetManyByType = new MemberRefUser(
            module,
            "GetComponents",
            MethodSig.CreateInstance(componentArray, reflectionType),
            componentType);
        var gameObjectTryGetByType = new MemberRefUser(
            module,
            "TryGetComponent",
            MethodSig.CreateInstance(
                module.CorLibTypes.Boolean,
                reflectionType,
                new ByRefSig(component)),
            gameObjectType);
        var componentTryGetByType = new MemberRefUser(
            module,
            "TryGetComponent",
            MethodSig.CreateInstance(
                module.CorLibTypes.Boolean,
                reflectionType,
                new ByRefSig(component)),
            componentType);
        typeRoutes.Body.Variables.Add(new Local(component));
        typeRoutes.Body.Variables.Add(new Local(component));
        typeRoutes.Body.InitLocals = true;
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_2));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, addByType));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_2));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, gameObjectGetByType));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_2));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, componentGetByType));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_2));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, gameObjectGetManyByType));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_2));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, componentGetManyByType));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_2));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloca, typeRoutes.Body.Variables[0]));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, gameObjectTryGetByType));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_2));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloca, typeRoutes.Body.Variables[1]));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, componentTryGetByType));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        typeRoutes.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        probe.Methods.Add(typeRoutes);

        var monoBehaviourType = new TypeRefUser(
            module,
            "UnityEngine",
            "MonoBehaviour",
            coreAssembly);
        var coroutineType = new TypeRefUser(
            module,
            "UnityEngine",
            "Coroutine",
            coreAssembly);
        var enumeratorType = new TypeRefUser(
            module,
            "System.Collections",
            "IEnumerator",
            module.CorLibTypes.AssemblyRef);
        var monoBehaviour = new ClassSig(monoBehaviourType);
        var coroutine = new ClassSig(coroutineType);
        var enumerator = new ClassSig(enumeratorType);
        var coroutineApis = new MethodDefUser(
            "CallManagedCoroutineApis",
            MethodSig.CreateStatic(module.CorLibTypes.Void, monoBehaviour, enumerator),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static)
        {
            Body = new CilBody { InitLocals = true }
        };
        coroutineApis.Body.Variables.Add(new Local(coroutine));
        var startCoroutine = new MemberRefUser(
            module,
            "StartCoroutine",
            MethodSig.CreateInstance(coroutine, enumerator),
            monoBehaviourType);
        var stopEnumerator = new MemberRefUser(
            module,
            "StopCoroutine",
            MethodSig.CreateInstance(module.CorLibTypes.Void, enumerator),
            monoBehaviourType);
        var stopHandle = new MemberRefUser(
            module,
            "StopCoroutine",
            MethodSig.CreateInstance(module.CorLibTypes.Void, coroutine),
            monoBehaviourType);
        var stopAll = new MemberRefUser(
            module,
            "StopAllCoroutines",
            MethodSig.CreateInstance(module.CorLibTypes.Void),
            monoBehaviourType);
        var startNamed = new MemberRefUser(
            module,
            "StartCoroutine",
            MethodSig.CreateInstance(coroutine, module.CorLibTypes.String),
            monoBehaviourType);
        var startNamedValue = new MemberRefUser(
            module,
            "StartCoroutine",
            MethodSig.CreateInstance(
                coroutine,
                module.CorLibTypes.String,
                module.CorLibTypes.Object),
            monoBehaviourType);
        var stopNamed = new MemberRefUser(
            module,
            "StopCoroutine",
            MethodSig.CreateInstance(module.CorLibTypes.Void, module.CorLibTypes.String),
            monoBehaviourType);
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, startCoroutine));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Stloc_0));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, stopEnumerator));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc_0));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, stopHandle));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, stopAll));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "Named"));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, startNamed));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "NamedValue"));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, startNamedValue));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "Named"));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, stopNamed));
        coroutineApis.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        probe.Methods.Add(coroutineApis);

        var ownerProperties = new MethodDefUser(
            "CallManagedOwnerProperties",
            MethodSig.CreateStatic(module.CorLibTypes.Void, component),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static)
        {
            Body = new CilBody()
        };
        ownerProperties.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        ownerProperties.Body.Instructions.Add(Instruction.Create(
            OpCodes.Callvirt,
            new MemberRefUser(
                module,
                "get_gameObject",
                MethodSig.CreateInstance(gameObject),
                componentType)));
        ownerProperties.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        ownerProperties.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        ownerProperties.Body.Instructions.Add(Instruction.Create(
            OpCodes.Callvirt,
            new MemberRefUser(
                module,
                "get_transform",
                MethodSig.CreateInstance(new ClassSig(transformType)),
                componentType)));
        ownerProperties.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        ownerProperties.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        probe.Methods.Add(ownerProperties);

        var destroy = new MethodDefUser(
            "CallDestroy",
            MethodSig.CreateStatic(module.CorLibTypes.Void, new ClassSig(unityObjectType)),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static)
        {
            Body = new CilBody()
        };
        destroy.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        destroy.Body.Instructions.Add(Instruction.Create(
            OpCodes.Call,
            new MemberRefUser(
                module,
                "Destroy",
                MethodSig.CreateStatic(module.CorLibTypes.Void, new ClassSig(unityObjectType)),
                unityObjectType)));
        destroy.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        probe.Methods.Add(destroy);

        var destroyDelayed = new MethodDefUser(
            "CallDestroyDelayed",
            MethodSig.CreateStatic(
                module.CorLibTypes.Void,
                new ClassSig(unityObjectType),
                module.CorLibTypes.Single),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static)
        {
            Body = new CilBody()
        };
        destroyDelayed.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        destroyDelayed.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        destroyDelayed.Body.Instructions.Add(Instruction.Create(
            OpCodes.Call,
            new MemberRefUser(
                module,
                "Destroy",
                MethodSig.CreateStatic(
                    module.CorLibTypes.Void,
                    new ClassSig(unityObjectType),
                    module.CorLibTypes.Single),
                unityObjectType)));
        destroyDelayed.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        probe.Methods.Add(destroyDelayed);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StArray.ModManager.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("StArray.ModManager repository root not found.");
    }
}

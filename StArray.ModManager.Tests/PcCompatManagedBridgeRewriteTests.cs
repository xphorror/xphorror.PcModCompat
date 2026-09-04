using dnlib.DotNet;
using dnlib.DotNet.Emit;
using StArray.ModManager.Android.PcCompat;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatManagedBridgeRewriteTests
{
    private string _root = null!;
    private string _inputPath = null!;
    private string _outputPath = null!;
    private string _reportPath = null!;
    private string _bootstrapInputPath = null!;
    private string _bootstrapOutputPath = null!;
    private RewriteReport _bootstrapReport = null!;
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
                nameof(PcCompatManagedSettingsDelegateBridge.CreateOptionalAction)),
            managedWritableCollections: CreateWritableCollectionSpecs());

        Assert.That(_report.Issues, Is.Empty);
        Assert.That(_report.MethodIssues, Is.Empty);
        Assert.That(_report.ManagedBridgeIssues, Is.Empty);
        Assert.That(_report.OutputWritten, Is.True);

        // JAMod.Bootstrap.dll is the second rewrite root (manifest EntryAssemblyPath) and the
        // only sample assembly that touches the network (JALib's self-updater), so the network
        // coverage is pinned against it rather than the primary DLL.
        _bootstrapInputPath = Path.Combine(_root, "JAMod.Bootstrap.input.dll");
        _bootstrapOutputPath = Path.Combine(_root, "JAMod.Bootstrap.rewritten.dll");
        File.Copy(
            Path.Combine(modDirectory, "JAMod.Bootstrap.dll"),
            _bootstrapInputPath,
            overwrite: true);
        _bootstrapReport = ModAssemblyRewriteApi.Rewrite(
            _bootstrapInputPath,
            _bootstrapOutputPath,
            _proxyDirectory,
            Path.Combine(_root, "bootstrap-rewrite-report.json"),
            managedBridgeAssemblyPath: _bridgeAssemblyPath,
            // ReversePatch stand-ins live in the primary assembly only; production scopes
            // those specs per assembly, and unlike call-bridge specs their zero-match is
            // recorded as an issue instead of being silently skipped.
            managedBridgeRewrites: [],
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
                nameof(PcCompatManagedSettingsDelegateBridge.CreateOptionalAction)),
            managedWritableCollections: CreateWritableCollectionSpecs());

        Assert.That(_bootstrapReport.Issues, Is.Empty);
        Assert.That(_bootstrapReport.MethodIssues, Is.Empty);
        Assert.That(_bootstrapReport.ManagedBridgeIssues, Is.Empty);
        Assert.That(_bootstrapReport.OutputWritten, Is.True);
    }

    [Test]
    public void FilesystemEntryPointsAreRoutedThroughTheDomainPathBridge()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var called = module.GetTypes()
            .SelectMany(type => type.Methods.Where(method => method.HasBody))
            .SelectMany(method => method.Body.Instructions)
            .Where(instruction =>
                instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
            .Select(instruction => instruction.Operand)
            .OfType<IMethod>()
            .ToArray();

        var bridgeCalls = called
            .Where(method =>
                method.DeclaringType?.FullName == typeof(PcCompatManagedPathBridge).FullName)
            .Select(method => method.Name.String)
            .ToArray();

        Assert.Multiple(() =>
        {
            // Guards against a silently zero-match registration: Jipper really does read and
            // write its own settings/state files, so the bridge must be reached.
            Assert.That(bridgeCalls, Is.Not.Empty, "file rewrite matched nothing");
            // Real Jipper coverage, so a spec that silently stops matching is caught.
            Assert.That(bridgeCalls, Has.Length.EqualTo(14));

            // No ambient-state filesystem entry point may survive.
            Assert.That(
                called.Any(method =>
                    method.DeclaringType?.FullName is "System.IO.File" or "System.IO.Directory"),
                Is.False,
                "raw File/Directory calls survived the rewrite");
            Assert.That(
                called.Any(method =>
                    method.DeclaringType?.FullName == "System.IO.Path" &&
                    method.Name.String is "GetFullPath" or "GetDirectoryName"),
                Is.False,
                "raw owner-sensitive Path helper survived the rewrite");
            Assert.That(
                called.Any(method =>
                    method.DeclaringType?.FullName == "System.IO.FileStream" &&
                    method.Name == ".ctor"),
                Is.False,
                "raw FileStream construction survived the rewrite");

            // Pure helpers without virtual-root parent semantics stay untouched by design.
            Assert.That(
                called.Any(method =>
                    method.DeclaringType?.FullName == "System.IO.Path" &&
                    method.Name == "Combine"),
                Is.True,
                "Path.Combine is a pure helper and must not be rewritten");
        });
    }

    [Test]
    public void BootstrapNetworkConstructionsAreRoutedThroughTheSessionNetworkBridge()
    {
        using var module = ModuleDefMD.Load(_bootstrapOutputPath);
        var called = module.GetTypes()
            .SelectMany(type => type.Methods.Where(method => method.HasBody))
            .SelectMany(method => method.Body.Instructions)
            .Where(instruction =>
                instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
            .Select(instruction => instruction.Operand)
            .OfType<IMethod>()
            .ToArray();

        Assert.Multiple(() =>
        {
            // JALib's self-updater builds its HttpClient here — the only real network
            // construction in either PcCompat sample assembly, so a silent zero-match would
            // leave the self-update download unisolated.
            Assert.That(
                called.Count(method =>
                    method.DeclaringType?.FullName == typeof(PcCompatManagedNetworkBridge).FullName &&
                    method.Name == nameof(PcCompatManagedNetworkBridge.CreateHttpClient)),
                Is.EqualTo(1));

            Assert.That(
                called.Any(method =>
                    method.DeclaringType?.FullName == "System.Net.Http.HttpClient" &&
                    method.Name == ".ctor"),
                Is.False,
                "raw HttpClient construction survived the rewrite");

            // Operations on the bound client inherit its session identity by design and stay
            // untouched — same reasoning that leaves Path.Combine alone.
            Assert.That(
                called.Any(method =>
                    method.DeclaringType?.FullName == "System.Net.Http.HttpClient" &&
                    (method.Name == "GetAsync" || method.Name == "get_DefaultRequestHeaders")),
                Is.True,
                "bound-client operations must not be rewritten");
        });
    }

    [Test]
    public void ExternalStaticEventSubscriptionsAreRoutedThroughTheRegistrationBridge()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var instructions = module.GetTypes()
            .SelectMany(type => type.Methods.Where(method => method.HasBody))
            .SelectMany(method => method.Body.Instructions)
            .ToArray();

        Assert.Multiple(() =>
        {
            // Real Jipper subscribes SceneManager.sceneUnloaded (Main.OnEnable) and
            // Application.quitting (KeyViewer.OnEnable); both must reach the registration
            // bridge with their event identity embedded.
            var subscribeCalls = instructions
                .Where(instruction => instruction.OpCode.Code == Code.Call)
                .Select(instruction => instruction.Operand)
                .OfType<IMethod>()
                .Where(method =>
                    method.DeclaringType?.FullName ==
                    typeof(PcCompatManagedEventSubscriptionBridge).FullName &&
                    method.Name == nameof(PcCompatManagedEventSubscriptionBridge.Subscribe))
                .ToArray();
            Assert.That(subscribeCalls, Has.Length.EqualTo(2));
            var unsubscribeCalls = instructions
                .Where(instruction => instruction.OpCode.Code == Code.Call)
                .Select(instruction => instruction.Operand)
                .OfType<IMethod>()
                .Where(method =>
                    method.DeclaringType?.FullName ==
                    typeof(PcCompatManagedEventSubscriptionBridge).FullName &&
                    method.Name == nameof(PcCompatManagedEventSubscriptionBridge.Unsubscribe))
                .ToArray();
            Assert.That(unsubscribeCalls, Has.Length.EqualTo(2));

            Assert.That(
                instructions
                    .Where(instruction => instruction.OpCode.Code == Code.Ldstr)
                    .Select(instruction => instruction.Operand)
                    .OfType<string>(),
                Does.Contain("UnityEngine.CoreModule!UnityEngine.Application::quitting"));
            Assert.That(
                instructions
                    .Where(instruction => instruction.OpCode.Code == Code.Ldstr)
                    .Select(instruction => instruction.Operand)
                    .OfType<string>(),
                Does.Contain("UnityEngine.CoreModule!UnityEngine.SceneManagement.SceneManager::sceneUnloaded"));

            Assert.That(
                instructions
                    .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
                    .Select(instruction => instruction.Operand)
                    .OfType<IMethod>()
                    .Any(method =>
                        method.DeclaringType?.FullName is
                            "UnityEngine.Application" or
                            "UnityEngine.SceneManagement.SceneManager" &&
                        method.Name!.String.StartsWith("add_", StringComparison.Ordinal)),
                Is.False,
                "raw static-event add_ accessor calls survived the rewrite");

            Assert.That(
                instructions
                    .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
                    .Select(instruction => instruction.Operand)
                    .OfType<IMethod>()
                    .Count(method =>
                        method.DeclaringType?.FullName is
                            "UnityEngine.Application" or
                            "UnityEngine.SceneManagement.SceneManager" &&
                        method.Name!.String.StartsWith("remove_", StringComparison.Ordinal)),
                Is.EqualTo(0),
                "raw static-event remove_ accessor calls survived the rewrite");
        });
    }

    [Test]
    public void ModCreatedUnityObjectsAreRoutedThroughTheOwnerRegisteringBridge()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var called = module.GetTypes()
            .SelectMany(type => type.Methods.Where(method => method.HasBody))
            .SelectMany(method => method.Body.Instructions)
            .Where(instruction =>
                instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
            .Select(instruction => instruction.Operand)
            .OfType<IMethod>()
            .ToArray();
        var instantiateCalls = called
            .Where(method =>
                method.DeclaringType?.FullName == typeof(PcCompatManagedComponentBridge).FullName &&
                method.Name == nameof(PcCompatManagedComponentBridge.Instantiate))
            .ToArray();

        Assert.Multiple(() =>
        {
            // Jipper builds 19 host GameObjects and clones 2 objects; none may reach Unity
            // without first being registered to the owning MOD session.
            Assert.That(
                called.Count(method =>
                    method.DeclaringType?.FullName == typeof(PcCompatManagedComponentBridge).FullName &&
                    method.Name == nameof(PcCompatManagedComponentBridge.CreateGameObject)),
                Is.EqualTo(19));
            Assert.That(
                instantiateCalls,
                Has.Length.EqualTo(2));
            Assert.That(
                instantiateCalls.All(method => method is not MethodSpec),
                Is.True,
                "generic Object.Instantiate<T> must target the non-generic ownership bridge " +
                "without retaining a MethodSpec");
            Assert.That(
                instantiateCalls.All(method => method.MethodSig?.GenParamCount == 0),
                Is.True,
                "the erased ownership bridge must remain non-generic");

            Assert.That(
                called.Any(method =>
                    method.DeclaringType?.FullName == "UnityEngine.GameObject" &&
                    method.Name == ".ctor"),
                Is.False,
                "no raw GameObject construction may survive the rewrite");
            Assert.That(
                called.Any(method =>
                    method.DeclaringType?.FullName == "UnityEngine.Object" &&
                    method.Name == "Instantiate"),
                Is.False,
                "no raw Object.Instantiate may survive the rewrite");

            // The pre-existing component/destroy coverage must stay wired. Exact counts are not
            // asserted here: those specs carry a GenericArgumentFilter, so the number of
            // rewritten sites is a property of the filter rather than of this slice.
            Assert.That(
                called.Any(method =>
                    method.DeclaringType?.FullName == typeof(PcCompatManagedComponentBridge).FullName &&
                    method.Name == nameof(PcCompatManagedComponentBridge.AddComponent)),
                Is.True);
            Assert.That(
                called.Any(method =>
                    method.DeclaringType?.FullName == typeof(PcCompatManagedComponentBridge).FullName &&
                    method.Name == nameof(PcCompatManagedComponentBridge.Destroy)),
                Is.True);
        });
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
    public void RewritesJipperListenerThreadToScopedThreadBridge()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var keyViewer = module.GetTypes().Single(type =>
            type.FullName == "JipperResourcePack.KeyViewerContents.KeyViewer");
        var calls = keyViewer.Methods
            .Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions)
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
            .Select(instruction => (IMethod)instruction.Operand)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(calls.Count(call =>
                call.DeclaringType.FullName == typeof(PcCompatManagedThreadBridge).FullName &&
                call.Name == nameof(PcCompatManagedThreadBridge.Create)), Is.EqualTo(1));
            Assert.That(calls.Any(call =>
                call.DeclaringType.FullName == "System.Threading.Thread" &&
                call.Name == ".ctor" &&
                call.MethodSig?.Params.Count == 1 &&
                call.MethodSig.Params[0].FullName == "System.Threading.ThreadStart"), Is.False);
            Assert.That(_report.ManagedBridgeRewrites.Any(item =>
                item.SourceMethod.Contains("System.Threading.Thread::.ctor", StringComparison.Ordinal) &&
                item.BridgeMethod.Contains(
                    nameof(PcCompatManagedThreadBridge.Create),
                    StringComparison.Ordinal)), Is.True);
        });
    }

    [Test]
    public void RewritesJipperKeyCountBackgroundSaveToScopedTaskBridge()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var keyCountData = module.GetTypes().Single(type =>
            type.FullName == "JipperResourcePack.KeyViewerContents.KeyCountData");
        var save = keyCountData.Methods.Single(method => method.Name == "Save");
        var calls = save.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
            .Select(instruction => (IMethod)instruction.Operand)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(calls.Count(call =>
                call.DeclaringType.FullName == typeof(PcCompatManagedThreadBridge).FullName &&
                call.Name == "Run"), Is.EqualTo(1));
            Assert.That(calls.Any(call =>
                call.DeclaringType.FullName == "System.Threading.Tasks.Task" &&
                call.Name == "Run"), Is.False);
            Assert.That(_report.ManagedBridgeRewrites.Any(item =>
                item.Method.Contains("KeyCountData::Save", StringComparison.Ordinal) &&
                item.SourceMethod.Contains(
                    "System.Threading.Tasks.Task::Run",
                    StringComparison.Ordinal) &&
                item.BridgeMethod.Contains(
                    nameof(PcCompatManagedThreadBridge),
                    StringComparison.Ordinal)), Is.True);
        });
    }

    [Test]
    public void RewritesBootstrapInstallerToScopedTaskBridge()
    {
        using var module = ModuleDefMD.Load(_bootstrapOutputPath);
        var bootModData = module.GetTypes().Single(type =>
            type.FullName == "JAMod.Bootstrap.BootModData");
        var checker = bootModData.Methods.Single(method => method.Name == "Checker");
        var calls = checker.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
            .Select(instruction => (IMethod)instruction.Operand)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(calls.Count(call =>
                call.DeclaringType.FullName == typeof(PcCompatManagedThreadBridge).FullName &&
                call.Name == nameof(PcCompatManagedThreadBridge.Run)), Is.EqualTo(1));
            Assert.That(calls.Any(call =>
                call.DeclaringType.FullName == "System.Threading.Tasks.Task" &&
                call.Name == "Run"), Is.False);
            Assert.That(_bootstrapReport.ManagedBridgeRewrites.Any(item =>
                item.Method.Contains("BootModData::Checker", StringComparison.Ordinal) &&
                item.SourceMethod.Contains(
                    "System.Threading.Tasks.Task::Run",
                    StringComparison.Ordinal) &&
                item.BridgeMethod.Contains(
                    nameof(PcCompatManagedThreadBridge.Run),
                    StringComparison.Ordinal)), Is.True);
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
            Assert.That(calls, Does.Contain(nameof(PcCompatManagedImGuiBridge.ToggleContent)));
            Assert.That(calls, Does.Contain(nameof(PcCompatManagedImGuiBridge.TextArea)));
            Assert.That(calls, Does.Contain(nameof(PcCompatManagedImGuiBridge.SetNextControlName)));
            Assert.That(calls, Does.Contain(nameof(PcCompatManagedImGuiBridge.GetNameOfFocusedControl)));
            Assert.That(
                _report.ManagedBridgeRewrites.Count(item =>
                    item.BridgeMethod.Contains(nameof(PcCompatManagedImGuiBridge), StringComparison.Ordinal)),
                Is.GreaterThanOrEqualTo(3));
        });
    }

    [Test]
    public void RewritesUnity6StrippedGuiDragWindowWithAValueTypeBox()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var bridgeType = typeof(PcCompatManagedImGuiBridge).FullName!;
        var calls = FindBridgeCalls(
                module,
                bridgeType,
                nameof(PcCompatManagedImGuiBridge.DragWindow))
            .ToArray();

        Assert.That(calls, Is.Not.Empty, "no GUI.DragWindow callsite was rewritten");
        foreach (var (method, call) in calls)
        {
            var previous = method.Body.Instructions[method.Body.Instructions.IndexOf(call) - 1];
            Assert.Multiple(() =>
            {
                Assert.That(previous.OpCode.Code, Is.EqualTo(Code.Box));
                Assert.That(
                    (previous.Operand as ITypeDefOrRef)?.FullName,
                    Is.EqualTo("UnityEngine.Rect"));
            });
        }
    }

    [Test]
    public void SharedGuiProxyDoesNotResolveBridgeOwnedFocusWrappers()
    {
        using var module = ModuleDefMD.Load(Path.Combine(
            _proxyDirectory,
            "UnityEngine.IMGUIModule.dll"));
        var gui = module.GetTypes().Single(type => type.FullName == "UnityEngine.GUI");
        var cctor = gui.FindStaticConstructor();
        Assert.That(cctor, Is.Not.Null);
        var resolvedNames = cctor!.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Ldstr)
            .Select(instruction => instruction.Operand)
            .OfType<string>()
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(resolvedNames, Does.Not.Contain("SetNextControlName"));
            Assert.That(resolvedNames, Does.Not.Contain("GetNameOfFocusedControl"));
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

    /// <summary>
    /// The shared-property bridge takes and returns <c>object</c> because the proxy
    /// <c>UnityEngine.Vector2</c> only exists at runtime, so the rewriter has to box on the way in
    /// and unbox on the way out. Getting either instruction wrong produces IL that verifies but
    /// corrupts the stack, which is why the exact shape is asserted rather than just the call.
    /// </summary>
    [Test]
    public void SharedAnchoredPositionRewriteBoxesTheArgumentAndUnboxesTheReturn()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var bridgeType = typeof(PcCompatManagedComponentBridge).FullName!;

        var setters = FindBridgeCalls(
            module,
            bridgeType,
            nameof(PcCompatManagedComponentBridge.SetAnchoredPosition)).ToArray();
        var getters = FindBridgeCalls(
            module,
            bridgeType,
            nameof(PcCompatManagedComponentBridge.GetAnchoredPosition)).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(setters, Is.Not.Empty, "no set_anchoredPosition callsite was rewritten");
            Assert.That(getters, Is.Not.Empty, "no get_anchoredPosition callsite was rewritten");
        });

        foreach (var (method, call) in setters)
        {
            var instructions = method.Body.Instructions;
            var previous = instructions[instructions.IndexOf(call) - 1];
            Assert.Multiple(() =>
            {
                Assert.That(
                    previous.OpCode.Code,
                    Is.EqualTo(Code.Box),
                    $"{method.FullName}: the Vector2 argument must be boxed before the bridge call");
                Assert.That(
                    (previous.Operand as ITypeDefOrRef)?.FullName,
                    Is.EqualTo("UnityEngine.Vector2"));
            });
        }

        foreach (var (method, call) in getters)
        {
            var instructions = method.Body.Instructions;
            var next = instructions[instructions.IndexOf(call) + 1];
            Assert.Multiple(() =>
            {
                Assert.That(
                    next.OpCode.Code,
                    Is.EqualTo(Code.Unbox_Any),
                    $"{method.FullName}: the boxed return must be unboxed after the bridge call");
                Assert.That(
                    (next.Operand as ITypeDefOrRef)?.FullName,
                    Is.EqualTo("UnityEngine.Vector2"));
            });
        }
    }

    /// <summary>
    /// The read-modify-write in ResourceChanger.OnLogoTextAwake -
    /// <c>rectTransform.anchoredPosition = rectTransform.anchoredPosition with { y = 0.75f }</c> -
    /// is the case that forced the getter to be routed too: the x it preserves has to be the game's
    /// own, not whatever another MOD currently projects.
    /// </summary>
    [Test]
    public void LogoTextReadModifyWriteRoutesBothAnchoredPositionAccessors()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var bridgeType = typeof(PcCompatManagedComponentBridge).FullName!;
        var method = module.GetTypes()
            .SelectMany(type => type.Methods)
            .Single(candidate => candidate.Name == "OnLogoTextAwake");

        var calls = method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Call &&
                                  instruction.Operand is IMethod target &&
                                  target.DeclaringType.FullName == bridgeType)
            .Select(instruction => ((IMethod)instruction.Operand).Name.String)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(
                calls,
                Does.Contain(nameof(PcCompatManagedComponentBridge.GetAnchoredPosition)));
            Assert.That(
                calls,
                Does.Contain(nameof(PcCompatManagedComponentBridge.SetAnchoredPosition)));
        });
    }

    /// <summary>
    /// Pins the converter on every <c>List</c>-returning site the sample MOD actually has, and which
    /// of the two it gets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two of these three sites are genuinely read-only and must keep copy semantics:
    /// <c>scrLevelMaker::listFloors</c> (ten field sites, all <c>Count</c>/<c>Last()</c>/LINQ) and
    /// <c>scnGame::get_events</c> (one <c>foreach</c> in <c>PlayCount</c>). The third,
    /// <c>TMP_FontAsset::get_fallbackFontAssetTable</c>, is registered as a writable collection and so
    /// gets the bound copy - <c>BundleLoader</c> calls <c>.Add</c> on it, and with a plain copy that
    /// <c>.Add</c> reached nothing, leaving the CJK fallback font silently unapplied.
    /// </para>
    /// <para>
    /// Asserting the split by name is the point: it is the only thing standing between "one property
    /// was upgraded deliberately" and "every List-returning member silently changed semantics".
    /// </para>
    /// <para>
    /// <c>PlanetarySystem::allPlanets</c> is not listed. The proxy surface has it, but the MOD reads
    /// it via <c>obj.GetValue&lt;List&lt;scrPlanet&gt;&gt;("allPlanets")</c> - reflection, so no
    /// callsite reaches the converter at all.
    /// </para>
    /// </remarks>
    [Test]
    public void ListSitesGetTheCopyConverterMatchingTheirWritability()
    {
        const string converter =
            "System.Collections.Generic.List`1<{1}> " +
            "StArray.ModManager.Android.PcCompat.PcCompatCollectionBridge::{0}<{1}>" +
            "(Il2CppSystem.Collections.Generic.List`1<{1}>)";

        var floors = _report.Rewrites
            .Where(item => item.OriginalField.EndsWith("scrLevelMaker::listFloors", StringComparison.Ordinal))
            .ToArray();
        var events = _report.MethodCalls
            .Where(item => item.Target.StartsWith("Assembly-CSharp!scnGame::get_events(", StringComparison.Ordinal))
            .ToArray();
        var fallback = _report.MethodCalls
            .Where(item => item.Target.Contains(
                "TMPro.TMP_FontAsset::get_fallbackFontAssetTable(",
                StringComparison.Ordinal))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(floors, Has.Length.EqualTo(10), "scrLevelMaker::listFloors site count");
            Assert.That(events, Has.Length.EqualTo(1), "scnGame::get_events site count");
            Assert.That(fallback, Has.Length.EqualTo(1), "get_fallbackFontAssetTable site count");

            Assert.That(
                floors.Select(item => item.ProxyAccessor).Distinct(StringComparer.Ordinal),
                Is.EqualTo(new[]
                {
                    "Il2CppSystem.Collections.Generic.List`1<scrFloor> scrLevelMaker::get_listFloors()" +
                    " -> " + string.Format(converter, "CopyList", "scrFloor")
                }));
            Assert.That(
                events[0].ProxyMethod,
                Is.EqualTo(
                    "Il2CppSystem.Collections.Generic.List`1<ADOFAI.LevelEvent> scnGame::get_events()" +
                    " -> " + string.Format(converter, "CopyList", "ADOFAI.LevelEvent")));
            Assert.That(
                fallback[0].ProxyMethod,
                Is.EqualTo(
                    "Il2CppSystem.Collections.Generic.List`1<TMPro.TMP_FontAsset> " +
                    "TMPro.TMP_FontAsset::get_fallbackFontAssetTable()" +
                    " -> System.Collections.Generic.List`1<TMPro.TMP_FontAsset> " +
                    "StArray.ModManager.Android.PcCompat.PcCompatCollectionBridge::" +
                    "CopyOrCreateBoundList<TMPro.TMP_FontAsset>(" +
                    "System.Object,Il2CppSystem.Collections.Generic.List`1<TMPro.TMP_FontAsset>," +
                    "System.String)"));
        });
    }

    /// <summary>
    /// JipperResourcePack's <c>BundleLoader</c> adds the CJK font to the fallback table. That
    /// <c>List&lt;T&gt;::Add</c> has to become a write-through bridge call, or the font never reaches
    /// Unity - the failure being invisible is what makes it worth pinning.
    /// </summary>
    [Test]
    public void FallbackFontTableMutationWritesThroughToUnity()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var loadBundle = module.GetTypes()
            .Single(type => type.FullName == "JipperResourcePack.BundleLoader")
            .Methods.Single(method => method.Name == "LoadBundle");

        var calls = loadBundle.Body.Instructions
            .Where(instruction => instruction.Operand is IMethod)
            .Select(instruction => new
            {
                instruction.OpCode.Code,
                Target = (IMethod)instruction.Operand
            })
            .ToArray();

        Assert.Multiple(() =>
        {
            var addCall = calls.SingleOrDefault(call =>
                call.Code == Code.Call &&
                call.Target.Name == nameof(PcCompatCollectionBridge.AddToBoundList) &&
                call.Target.DeclaringType.FullName == typeof(PcCompatCollectionBridge).FullName);
            Assert.That(addCall, Is.Not.Null, "the fallback-table Add was not routed to the write-through bridge");
            var addList = addCall?.Target.MethodSig?.Params.FirstOrDefault() as GenericInstSig;
            Assert.That(
                addList?.GenericType.TypeDefOrRef.DefinitionAssembly?.Name?.String,
                Is.EqualTo("System.Collections"),
                "the bridge List<T> must use the host runtime assembly, not the MOD mscorlib facade");
            Assert.That(
                addList?.GenericArguments.Single(),
                Is.TypeOf<GenericMVar>()
                    .And.Property(nameof(GenericMVar.Number)).EqualTo(0u),
                "the bridge receiver must use its own method generic parameter (!!0)");
            Assert.That(
                addCall?.Target.MethodSig?.Params.ElementAtOrDefault(1),
                Is.TypeOf<GenericMVar>()
                    .And.Property(nameof(GenericMVar.Number)).EqualTo(0u),
                "List<T>.Add's type generic parameter (!0) must become the bridge method parameter (!!0)");
            Assert.That(
                addCall?.Target,
                Is.InstanceOf<MethodSpec>());
            Assert.That(
                ((MethodSpec)addCall!.Target).GenericInstMethodSig.GenericArguments.Single().FullName,
                Is.EqualTo("TMPro.TMP_FontAsset"),
                "the bridge MethodSpec must close !!0 with the rewritten collection element type");
            // No List<TMP_FontAsset> mutator may survive; one that did would be a silent no-op.
            Assert.That(
                calls.Any(call =>
                    call.Target.Name.String is "Add" or "Remove" or "Clear" or "Insert" &&
                    call.Target.DeclaringType is TypeSpec { TypeSig: GenericInstSig list } &&
                    list.GenericType.TypeDefOrRef.FullName == "System.Collections.Generic.List`1" &&
                    list.GenericArguments[0].FullName == "TMPro.TMP_FontAsset"),
                Is.False,
                "a raw List<TMP_FontAsset> mutator survived the rewrite");
        });
    }

    /// <summary>
    /// The IL shape the converters depend on. Read-only accessors are immediately followed by
    /// <c>CopyList</c>. A writable accessor preserves its owner with <c>dup</c>, then appends the
    /// trusted property name before the create-or-bind converter.
    /// </summary>
    /// <remarks>
    /// The two rewrite paths reach the same converter with different opcodes, and both are pinned.
    /// The field path (<c>ldfld listFloors</c>) is retargeted to <c>call get_listFloors</c>; the
    /// method path (<c>callvirt get_events</c>) only swaps the operand and stays <c>callvirt</c>.
    /// </remarks>
    [Test]
    public void CopyListConverterIsEmittedDirectlyAfterTheProxyAccessor()
    {
        using var module = ModuleDefMD.Load(_outputPath);
        var expected = new Dictionary<string, (Code Opcode, string Converter)>(StringComparer.Ordinal)
        {
            ["get_listFloors"] = (Code.Call, nameof(PcCompatCollectionBridge.CopyList)),
            ["get_events"] = (Code.Callvirt, nameof(PcCompatCollectionBridge.CopyList)),
            ["get_fallbackFontAssetTable"] =
                (Code.Callvirt, nameof(PcCompatCollectionBridge.CopyOrCreateBoundList))
        };
        var seen = new List<string>();

        foreach (var method in module.GetTypes()
                     .SelectMany(type => type.Methods.Where(candidate => candidate.HasBody)))
        {
            var instructions = method.Body.Instructions;
            for (var index = 0; index < instructions.Count; index++)
            {
                if (instructions[index].OpCode.Code is not (Code.Call or Code.Callvirt) ||
                    instructions[index].Operand is not IMethod accessor ||
                    !expected.TryGetValue(accessor.Name.String, out var expectation))
                {
                    continue;
                }

                seen.Add(accessor.Name.String);
                Assert.That(
                    instructions[index].OpCode.Code,
                    Is.EqualTo(expectation.Opcode),
                    $"{method.FullName}: {accessor.Name} opcode");
                Assert.That(
                    index + 1,
                    Is.LessThan(instructions.Count),
                    $"{method.FullName}: {accessor.Name} is the last instruction");
                var converterIndex = index + 1;
                if (accessor.Name == "get_fallbackFontAssetTable")
                {
                    Assert.That(instructions[index - 1].OpCode.Code, Is.EqualTo(Code.Dup));
                    Assert.That(instructions[converterIndex].OpCode.Code, Is.EqualTo(Code.Ldstr));
                    Assert.That(
                        instructions[converterIndex].Operand,
                        Is.EqualTo("fallbackFontAssetTable"));
                    converterIndex++;
                }
                var next = instructions[converterIndex];
                Assert.Multiple(() =>
                {
                    Assert.That(
                        next.OpCode.Code,
                        Is.EqualTo(Code.Call),
                        $"{method.FullName}: {accessor.Name} is not followed by a call");
                    Assert.That(
                        (next.Operand as IMethod)?.Name.String,
                        Is.EqualTo(expectation.Converter),
                        $"{method.FullName}: {accessor.Name} got the wrong copy converter");
                    Assert.That(
                        (next.Operand as IMethod)?.DeclaringType.FullName,
                        Is.EqualTo(typeof(PcCompatCollectionBridge).FullName));
                });
            }
        }

        Assert.That(
            seen.GroupBy(name => name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            Is.EqualTo(new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["get_listFloors"] = 10,
                ["get_events"] = 1,
                ["get_fallbackFontAssetTable"] = 1
            }));
    }

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

    private static IReadOnlyList<ManagedWritableCollectionSpec> CreateWritableCollectionSpecs()
        =>
        [
            new ManagedWritableCollectionSpec(
                "Unity.TextMeshPro",
                "TMPro.TMP_FontAsset",
                "fallbackFontAssetTable",
                "TMPro.TMP_FontAsset",
                typeof(PcCompatCollectionBridge).FullName!,
                nameof(PcCompatCollectionBridge.CopyOrCreateBoundList),
                nameof(PcCompatCollectionBridge.AddToBoundList),
                nameof(PcCompatCollectionBridge.RemoveFromBoundList),
                nameof(PcCompatCollectionBridge.ClearBoundList),
                nameof(PcCompatCollectionBridge.InsertIntoBoundList))
        ];

    private static IReadOnlyList<ManagedCallBridgeRewriteSpec> CreateManagedCallSpecs()
    {
        var resourceBridgeType = typeof(PcCompatManagedResourceBridge).FullName!;
        var componentBridgeType = typeof(PcCompatManagedComponentBridge).FullName!;
        var pathBridgeType = typeof(PcCompatManagedPathBridge).FullName!;
        var networkBridgeType = typeof(PcCompatManagedNetworkBridge).FullName!;
        var subscriptionBridgeType = typeof(PcCompatManagedEventSubscriptionBridge).FullName!;
        var logBridgeType = typeof(PcCompatManagedLogBridge).FullName!;
        var inputBridgeType = typeof(PcCompatLegacyInputBridge).FullName!;
        var imGuiBridgeType = typeof(PcCompatManagedImGuiBridge).FullName!;
        var threadBridgeType = typeof(PcCompatManagedThreadBridge).FullName!;
        return
        [
            // ModEntry.Path is a virtual package root, so its parent must stay owner-scoped.
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.Path",
                "GetDirectoryName",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.String",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.GetDirectoryName),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.Path",
                "GetFullPath",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.String",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.GetFullPath),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "Exists",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Boolean",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileExists),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "ReadAllText",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.String",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileReadAllText),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "ReadAllBytes",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Byte[]",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileReadAllBytes),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "WriteAllText",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String", "System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileWriteAllText),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "WriteAllBytes",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String", "System.Byte[]"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileWriteAllBytes),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "Delete",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileDelete),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "Copy",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String", "System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileCopy),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "Copy",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String", "System.String", "System.Boolean"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileCopyOverwrite),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "Move",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String", "System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileMove),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "OpenRead",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.IO.FileStream",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileOpenRead),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "OpenWrite",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.IO.FileStream",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileOpenWrite),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.Directory",
                "Exists",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Boolean",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.DirectoryExists),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.Directory",
                "CreateDirectory",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.IO.DirectoryInfo",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.DirectoryCreate),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.Directory",
                "Delete",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.DirectoryDelete),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.Directory",
                "Delete",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String", "System.Boolean"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.DirectoryDeleteRecursive),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.FileStream",
                ".ctor",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.IO.FileStream",
                ["System.String", "System.IO.FileMode"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.OpenFileStream),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true,
                SourceIsConstructor: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.FileStream",
                ".ctor",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.IO.FileStream",
                ["System.String", "System.IO.FileMode", "System.IO.FileAccess"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.OpenFileStreamAccess),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true,
                SourceIsConstructor: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.FileStream",
                ".ctor",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.IO.FileStream",
                ["System.String", "System.IO.FileMode", "System.IO.FileAccess", "System.IO.FileShare"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.OpenFileStreamShare),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true,
                SourceIsConstructor: true),
            // MOD network sessions: only client-producing constructions are rewritten.
            // Operations on a bound client and its response objects inherit this session's
            // identity already, so they stay as-is (same reasoning as Path.Combine above).
            // ServicePointManager, WebRequest/WebClient and raw sockets have no spec and are
            // not bridged; a MOD using them is an isolation downgrade, not a supported path.
            new ManagedCallBridgeRewriteSpec(
                "System.Net.Http",
                "System.Net.Http.HttpClient",
                ".ctor",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Net.Http.HttpClient",
                [],
                networkBridgeType,
                nameof(PcCompatManagedNetworkBridge.CreateHttpClient),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true,
                SourceIsConstructor: true),
            new ManagedCallBridgeRewriteSpec(
                "System.Net.Http",
                "System.Net.Http.HttpClient",
                ".ctor",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Net.Http.HttpClient",
                ["System.Net.Http.HttpMessageHandler"],
                networkBridgeType,
                nameof(PcCompatManagedNetworkBridge.CreateHttpClientWithHandler),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true,
                SourceIsConstructor: true),
            new ManagedCallBridgeRewriteSpec(
                "System.Net.Http",
                "System.Net.Http.HttpClient",
                ".ctor",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Net.Http.HttpClient",
                ["System.Net.Http.HttpMessageHandler", "System.Boolean"],
                networkBridgeType,
                nameof(PcCompatManagedNetworkBridge.CreateHttpClientWithHandlerDisposal),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true,
                SourceIsConstructor: true),
            new ManagedCallBridgeRewriteSpec(
                "System.Net.Http",
                "System.Net.Http.HttpClientHandler",
                ".ctor",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Net.Http.HttpClientHandler",
                [],
                networkBridgeType,
                nameof(PcCompatManagedNetworkBridge.CreateHttpClientHandler),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true,
                SourceIsConstructor: true),
            // CookieContainer's declaring assembly differs across target frameworks
            // (System.Net.Primitives on netstandard, System on desktop); a single spelling
            // would zero-match silently on the other, so both are registered.
            new ManagedCallBridgeRewriteSpec(
                "System.Net.Primitives",
                "System.Net.CookieContainer",
                ".ctor",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Net.CookieContainer",
                [],
                networkBridgeType,
                nameof(PcCompatManagedNetworkBridge.CreateCookieContainer),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true,
                SourceIsConstructor: true),
            new ManagedCallBridgeRewriteSpec(
                "System",
                "System.Net.CookieContainer",
                ".ctor",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Net.CookieContainer",
                [],
                networkBridgeType,
                nameof(PcCompatManagedNetworkBridge.CreateCookieContainer),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true,
                SourceIsConstructor: true),
            // External static-event subscriptions are routed through the registration bridge
            // in both directions. The generated IL2CPP proxy uses Il2CppSystem delegate
            // parameters, so remove_ must use the same conversion path as add_.
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Application",
                "add_quitting",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.Action"],
                subscriptionBridgeType,
                nameof(PcCompatManagedEventSubscriptionBridge.Subscribe),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AllowUnproxiedSource: true,
                AppendOwnerId: "UnityEngine.CoreModule!UnityEngine.Application::quitting"),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Application",
                "remove_quitting",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.Action"],
                subscriptionBridgeType,
                nameof(PcCompatManagedEventSubscriptionBridge.Unsubscribe),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AllowUnproxiedSource: true,
                AppendOwnerId: "UnityEngine.CoreModule!UnityEngine.Application::quitting"),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.SceneManagement.SceneManager",
                "add_sceneUnloaded",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.Events.UnityAction`1<UnityEngine.SceneManagement.Scene>"],
                subscriptionBridgeType,
                nameof(PcCompatManagedEventSubscriptionBridge.Subscribe),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AllowUnproxiedSource: true,
                AppendOwnerId: "UnityEngine.CoreModule!UnityEngine.SceneManagement.SceneManager::sceneUnloaded"),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.SceneManagement.SceneManager",
                "remove_sceneUnloaded",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.Events.UnityAction`1<UnityEngine.SceneManagement.Scene>"],
                subscriptionBridgeType,
                nameof(PcCompatManagedEventSubscriptionBridge.Unsubscribe),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AllowUnproxiedSource: true,
                AppendOwnerId: "UnityEngine.CoreModule!UnityEngine.SceneManagement.SceneManager::sceneUnloaded"),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.GameObject",
                ".ctor",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "UnityEngine.GameObject",
                ["System.String"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.CreateGameObject),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: true,
                SourceIsConstructor: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Object",
                "Instantiate",
                SourceIsStatic: true,
                SourceGenericArity: 1,
                "!!0",
                ["!!0"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.Instantiate),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: true,
                AllowObjectParameterForwarding: true,
                EraseBridgeGenericArity: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Object",
                "Instantiate",
                SourceIsStatic: true,
                SourceGenericArity: 1,
                "!!0",
                ["!!0", "UnityEngine.Transform"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.Instantiate),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: true,
                AllowObjectParameterForwarding: true,
                EraseBridgeGenericArity: true),
            // Shared RectTransform.anchoredPosition arbitration. JipperResourcePack writes it on
            // both kinds of target in one assembly: game-owned rects (scrShowIfDebug's, which
            // JipperOverlayer and CheryTools also reposition; scrLogoText's; ADOBase.controller's
            // txtLevelName) and its own overlay/keyviewer/rain objects. Both accessors are
            // rewritten because ResourceChanger does a read-modify-write - `anchoredPosition with
            // { y = 0.75f }` - so an unrouted getter would bake another MOD's x into the write.
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.RectTransform",
                "get_anchoredPosition",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "UnityEngine.Vector2",
                [],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetAnchoredPosition),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                AllowValueTypeReturnUnbox: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.RectTransform",
                "set_anchoredPosition",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.Vector2"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.SetAnchoredPosition),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                BoxLastValueTypeArgument: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.Threading.Thread",
                ".ctor",
                false,
                0,
                "System.Threading.Thread",
                ["System.Threading.ThreadStart"],
                threadBridgeType,
                nameof(PcCompatManagedThreadBridge.Create),
                ManagedCallInstanceForwarding.None,
                false,
                AllowUnproxiedSource: true,
                SourceIsConstructor: true),
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
                "mscorlib",
                "System.Threading.Tasks.Task",
                "Run",
                true,
                0,
                "System.Threading.Tasks.Task",
                ["System.Action"],
                threadBridgeType,
                nameof(PcCompatManagedThreadBridge.Run),
                ManagedCallInstanceForwarding.None,
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
                "Toggle",
                true,
                0,
                "System.Boolean",
                ["System.Boolean", "UnityEngine.GUIContent", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.ToggleContent),
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
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUI",
                "SetNextControlName",
                true,
                0,
                "System.Void",
                ["System.String"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.SetNextControlName),
                ManagedCallInstanceForwarding.None,
                false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUI",
                "GetNameOfFocusedControl",
                true,
                0,
                "System.String",
                [],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.GetNameOfFocusedControl),
                ManagedCallInstanceForwarding.None,
                false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUI",
                "DragWindow",
                true,
                0,
                "System.Void",
                ["UnityEngine.Rect"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.DragWindow),
                ManagedCallInstanceForwarding.None,
                false,
                BoxLastValueTypeArgument: true),
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
            // Debug.Log/LogWarning/LogError(System.Object) - see the production registration for
            // why these are a manual bridge rather than a proxy forward.
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Debug",
                "Log",
                true,
                0,
                "System.Void",
                ["System.Object"],
                logBridgeType,
                nameof(PcCompatManagedLogBridge.Log),
                ManagedCallInstanceForwarding.None,
                false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Debug",
                "LogWarning",
                true,
                0,
                "System.Void",
                ["System.Object"],
                logBridgeType,
                nameof(PcCompatManagedLogBridge.LogWarning),
                ManagedCallInstanceForwarding.None,
                false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Debug",
                "LogError",
                true,
                0,
                "System.Void",
                ["System.Object"],
                logBridgeType,
                nameof(PcCompatManagedLogBridge.LogError),
                ManagedCallInstanceForwarding.None,
                false),
            // JsonUtility - the MOD's own CoreCLR types are not in the IL2CPP class table, so
            // serialization has to stay managed. See the production registration.
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.JSONSerializeModule",
                "UnityEngine.JsonUtility",
                "ToJson",
                true,
                0,
                "System.String",
                ["System.Object", "System.Boolean"],
                typeof(PcCompatJsonBridge).FullName!,
                nameof(PcCompatJsonBridge.ToJson),
                ManagedCallInstanceForwarding.None,
                false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.JSONSerializeModule",
                "UnityEngine.JsonUtility",
                "FromJsonOverwrite",
                true,
                0,
                "System.Void",
                ["System.String", "System.Object"],
                typeof(PcCompatJsonBridge).FullName!,
                nameof(PcCompatJsonBridge.FromJsonOverwrite),
                ManagedCallInstanceForwarding.None,
                false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.JSONSerializeModule",
                "UnityEngine.JsonUtility",
                "FromJson",
                true,
                1,
                "!!0",
                ["System.String"],
                typeof(PcCompatJsonBridge).FullName!,
                nameof(PcCompatJsonBridge.FromJson),
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
        AddGuiFocusCalls(module, probe);
        AddGuiDragWindowCall(module, probe);
        AddGuiToggleContentCall(module, probe);

        module.Write(outputPath);
    }

    private static void AddGuiFocusCalls(ModuleDef module, TypeDef probe)
    {
        var imguiAssembly = module.GetAssemblyRefs().Single(reference =>
            reference.Name == "UnityEngine.IMGUIModule");
        var guiType = new TypeRefUser(
            module,
            "UnityEngine",
            "GUI",
            imguiAssembly);
        var setNextControlName = new MemberRefUser(
            module,
            "SetNextControlName",
            MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.String),
            guiType);
        var getNameOfFocusedControl = new MemberRefUser(
            module,
            "GetNameOfFocusedControl",
            MethodSig.CreateStatic(module.CorLibTypes.String),
            guiType);
        var caller = new MethodDefUser(
            "RoundTripGuiControlFocus",
            MethodSig.CreateStatic(module.CorLibTypes.String, module.CorLibTypes.String),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static)
        {
            Body = new CilBody()
        };
        caller.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        caller.Body.Instructions.Add(Instruction.Create(OpCodes.Call, setNextControlName));
        caller.Body.Instructions.Add(Instruction.Create(OpCodes.Call, getNameOfFocusedControl));
        caller.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        probe.Methods.Add(caller);
    }

    private static void AddGuiDragWindowCall(ModuleDef module, TypeDef probe)
    {
        var imguiAssembly = module.GetAssemblyRefs().Single(reference =>
            reference.Name == "UnityEngine.IMGUIModule");
        var coreAssembly = module.GetAssemblyRefs().Single(reference =>
            reference.Name == "UnityEngine.CoreModule");
        var guiType = new TypeRefUser(
            module,
            "UnityEngine",
            "GUI",
            imguiAssembly);
        var rectType = new TypeRefUser(
            module,
            "UnityEngine",
            "Rect",
            coreAssembly);
        var rect = new ValueTypeSig(rectType);
        var dragWindow = new MemberRefUser(
            module,
            "DragWindow",
            MethodSig.CreateStatic(module.CorLibTypes.Void, rect),
            guiType);
        var caller = new MethodDefUser(
            "CallGuiDragWindow",
            MethodSig.CreateStatic(module.CorLibTypes.Void, rect),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static)
        {
            Body = new CilBody()
        };
        caller.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        caller.Body.Instructions.Add(Instruction.Create(OpCodes.Call, dragWindow));
        caller.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        probe.Methods.Add(caller);
    }

    private static void AddGuiToggleContentCall(ModuleDef module, TypeDef probe)
    {
        var imguiAssembly = module.GetAssemblyRefs().Single(reference =>
            reference.Name == "UnityEngine.IMGUIModule");
        var guiLayoutType = new TypeRefUser(
            module,
            "UnityEngine",
            "GUILayout",
            imguiAssembly);
        var guiContentType = new TypeRefUser(
            module,
            "UnityEngine",
            "GUIContent",
            imguiAssembly);
        var guiLayoutOptionType = new TypeRefUser(
            module,
            "UnityEngine",
            "GUILayoutOption",
            imguiAssembly);
        var content = new ClassSig(guiContentType);
        var options = new SZArraySig(new ClassSig(guiLayoutOptionType));
        var toggle = new MemberRefUser(
            module,
            "Toggle",
            MethodSig.CreateStatic(module.CorLibTypes.Boolean, module.CorLibTypes.Boolean, content, options),
            guiLayoutType);
        var caller = new MethodDefUser(
            "CallGuiToggleContent",
            MethodSig.CreateStatic(module.CorLibTypes.Boolean, module.CorLibTypes.Boolean, content, options),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static)
        {
            Body = new CilBody()
        };
        caller.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        caller.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        caller.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_2));
        caller.Body.Instructions.Add(Instruction.Create(OpCodes.Call, toggle));
        caller.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        probe.Methods.Add(caller);
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
            if (Directory.Exists(Path.Combine(directory.FullName, "JipperResourcePack_release")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("StArray.ModManager repository root not found.");
    }
}

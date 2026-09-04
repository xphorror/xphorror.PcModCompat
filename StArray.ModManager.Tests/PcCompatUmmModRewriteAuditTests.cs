using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using StArray.ModManager.Android.PcCompat;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

/// <summary>
/// Rewrites the real UMM release assemblies of the three target MODs with the <b>production</b>
/// bridge specs and reports what still fails to close.
/// </summary>
/// <remarks>
/// <para>
/// Running the <c>ModAssemblyRewriter</c> CLI directly is not a valid audit: the CLI passes no
/// bridge specs, so every callsite the managed bridges own - <c>AssetBundle.LoadAllAssets</c>,
/// <c>GUILayout.Button</c>, <c>GUIStyle.set_*</c> and the rest - is reported as an unresolved proxy
/// method. JipperResourcePack, which does load today, fails the CLI audit for exactly that reason.
/// This test therefore drives the same <c>ModAssemblyRewriteApi.Rewrite</c> overload the Android
/// host uses, with the specs built by
/// <see cref="PcCompatAndroidManagedAssemblyRewrite"/> itself, so a clean result here means the
/// same thing it means in production.
/// </para>
/// <para>
/// Only the UMM loader assemblies are in scope. The Melon loader variants that ship alongside
/// JipperKeyViewer are deliberately excluded - MelonLoader is not a supported host.
/// </para>
/// <para>
/// The two MOD directories are release payloads that may be absent from a clean checkout, so each
/// case skips rather than fails when its input is missing. JipperResourcePack is the regression
/// anchor: it is required, because "JPOV/JPKV work" must not be bought by breaking it.
/// </para>
/// </remarks>
[NonParallelizable]
public sealed class PcCompatUmmModRewriteAuditTests
{
    private string _root = null!;
    private string _proxyDirectory = null!;
    private string _bridgeAssemblyPath = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "pccompat-umm-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _proxyDirectory = Path.Combine(
            FindRepoRoot(),
            "xphorror.PcModCompat",
            "out",
            "interop",
            "proxy_assemblies");
        _bridgeAssemblyPath = typeof(PcCompatManagedComponentBridge).Assembly.Location;
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing the run over.
        }
    }

    [Test]
    public void JipperResourcePackStillRewritesCleanWithProductionSpecs()
    {
        var report = RewriteWithProductionSpecs(
            "JipperResourcePack_release",
            "JipperResourcePack.dll",
            required: true);
        AssertClean(report!, "JipperResourcePack.dll");
    }

    [Test]
    public void OpaqueHandleBridgeIsHostedByTheManagedBridgeAssembly()
    {
        var managedBridgeAssembly = typeof(PcCompatReversePatchBridge).Assembly;
        var opaqueBridgeAssembly = typeof(PcCompatOpaqueHandleBridge).Assembly;

        Assert.Multiple(() =>
        {
            Assert.That(
                opaqueBridgeAssembly,
                Is.SameAs(managedBridgeAssembly),
                "The rewriter imports bridge methods from the managed bridge assembly; placing " +
                "opaque-handle semantics in the Android host assembly makes every erased null " +
                "operator unresolved.");
            Assert.That(
                managedBridgeAssembly.GetType(
                    "StArray.ModManager.Android.PcCompat.PcCompatOpaqueHandleBridge",
                    throwOnError: false),
                Is.Not.Null);
        });
    }

    [Test]
    public void JamodBootstrapStillRewritesCleanWithProductionSpecs()
    {
        // Not primary: ReversePatch stand-ins live only in the primary assembly, and production
        // scopes those specs per assembly. Passing them here would demand JipperResourcePack's
        // VersionSafe type inside JAMod.Bootstrap and report nine phantom bridge issues.
        var report = RewriteWithProductionSpecs(
            "JipperResourcePack_release",
            "JAMod.Bootstrap.dll",
            required: true,
            isPrimary: false);
        AssertClean(report!, "JAMod.Bootstrap.dll");
    }

    [Test]
    public void JipperOverlayerUmmLoaderRewritesClean()
    {
        var report = RewriteWithProductionSpecs(
            "JipperOverlayer-UMM",
            "JipperOverlayer.Loader.UMM.dll",
            required: false);
        AssertClean(report!, "JipperOverlayer.Loader.UMM.dll");
    }

    [Test]
    public void JipperKeyViewerUmmLoaderRewritesClean()
    {
        var report = RewriteWithProductionSpecs(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.Loader.UMM.dll",
            required: false);
        AssertClean(report!, "JipperKeyViewer.Loader.UMM.dll");

        var pathRewrites = report!.ManagedBridgeRewrites
            .Where(record => record.SourceMethod.Contains(
                "System.IO.Path::GetDirectoryName",
                StringComparison.Ordinal))
            .Where(record => record.BridgeMethod.Contains(
                $"{typeof(PcCompatManagedPathBridge).FullName}::GetDirectoryName",
                StringComparison.Ordinal))
            .ToArray();
        Assert.That(
            pathRewrites,
            Has.Length.EqualTo(1),
            "JPKV's UMM ModPath adapter must not escape to the shared mods root");
    }

    [Test]
    public void AdofaiOnlineModPrimaryAssemblyUsesClosedProductionRewrite()
    {
        var report = RewriteWithProductionSpecs(
            "ADOFAIOnlineMod",
            "ADOFAIOnlineMod.dll",
            required: false);
        ReportGaps(report!, "ADOFAIOnlineMod.dll");
        AssertClean(report!, "ADOFAIOnlineMod.dll");
    }

    [Test]
    public void AdofaiOnlineModManagedClosureUsesClosedProductionRewrite()
    {
        var modDirectory = Path.Combine(FindRepoRoot(), "ADOFAIOnlineMod");
        var entryPath = Path.Combine(modDirectory, "ADOFAIOnlineMod.dll");
        if (!File.Exists(entryPath))
            Assert.Ignore($"MOD payload is absent: {entryPath}");

        Assert.That(
            PcModManifestReader.TryRead(modDirectory, out var manifest, out var manifestError),
            Is.True,
            manifestError);
        var staticScan = PcCompatStaticPatchScanner.Scan(manifest, targetGameRevision: 143);
        var assemblies = PcCompatManagedAssemblyCatalog.Discover(manifest);

        Assert.That(
            assemblies.Count(item => item.IsPrimary),
            Is.EqualTo(1),
            "the real native MOD must have one primary managed assembly");

        var reports = assemblies
            .OrderBy(item => item.AssemblyName, StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                var report = RewriteAssemblyWithProductionSpecs(
                    item.InputPath,
                    manifest.Id,
                    staticScan,
                    item.IsPrimary,
                    renderComponentOverride: null);
                ReportGaps(report, item.AssemblyName);
                return (item, report);
            })
            .ToArray();

        Assert.Multiple(() =>
        {
            foreach (var (item, report) in reports)
            {
                Assert.That(report.Issues, Is.Empty, $"{item.AssemblyName}: top-level issues");
                Assert.That(report.MethodIssues, Is.Empty, $"{item.AssemblyName}: method issues");
                Assert.That(report.ManagedBridgeIssues, Is.Empty, $"{item.AssemblyName}: bridge issues");
                Assert.That(report.OutputWritten, Is.True, $"{item.AssemblyName}: output not written");
            }
        });
    }

    /// <summary>
    /// Verifies that JipperOverlayer's main assembly closes through the generated proxy surface.
    /// A clean rewrite is the definition of done for the proxy-surface gap; runtime behavior still
    /// requires a live IL2CPP device.
    /// </summary>
    [Test]
    public void JipperOverlayerMainAssemblyRewritesClean()
    {
        var report = RewriteWithProductionSpecs(
            "JipperOverlayer-UMM",
            "JipperOverlayer.dll",
            required: false);
        AssertClean(report!, "JipperOverlayer.dll");
    }

    [Test]
    public void JipperOverlayerDynamicGetterFactoriesAreFullyBridged()
    {
        var report = RewriteWithProductionSpecs(
            "JipperOverlayer-UMM",
            "JipperOverlayer.dll",
            required: false);
        AssertClean(report!, "JipperOverlayer.dll");

        const string sourcePrefix =
            "JipperOverlayer!JipperOverlayer.PatchManager::";
        var factoryNames = report!.ManagedBridgeRewrites
            .Where(record => record.SourceMethod.StartsWith(
                sourcePrefix,
                StringComparison.Ordinal))
            .Select(record => record.SourceMethod[sourcePrefix.Length..].Split('(')[0])
            .ToHashSet(StringComparer.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(factoryNames, Is.SupersetOf(new[]
            {
                "CreateStaticFieldGetter",
                "CreateStaticPropertyGetter",
                "CreateMemberGetter",
                "CreateStaticMemberGetter"
            }));
            Assert.That(
                report.MethodCalls.Any(record =>
                    record.Target.StartsWith(sourcePrefix, StringComparison.Ordinal) &&
                    record.Target.Contains("Getter", StringComparison.Ordinal)),
                Is.False,
                "rewritten JPOV still calls a desktop PatchManager getter factory");
        });
    }

    [Test]
    public void JipperOverlayerAssetBundleHandleIsOpaqueAfterRewrite()
    {
        var report = RewriteWithProductionSpecs(
            "JipperOverlayer-UMM",
            "JipperOverlayer.dll",
            required: false);
        AssertClean(report!, "JipperOverlayer.dll");

        using var module = ModuleDefMD.Load(GetRewrittenPath(
            "JipperOverlayer-UMM",
            "JipperOverlayer.dll"));
        var bundleLoader = module.GetTypes().Single(type =>
            type.FullName == "JipperOverlayer.Overlayer.BundleLoader");
        var bundleField = bundleLoader.Fields.Single(field => field.Name == "Bundle");
        var loadBundle = bundleLoader.Methods.Single(method => method.Name == "LoadBundle");
        var residualBundleNullOperator = loadBundle.Body.Instructions
            .Select((instruction, index) => (instruction, index))
            .Any(item =>
            {
                if (item.instruction.Operand is not IMethod target ||
                    target.DeclaringType.FullName != "UnityEngine.Object" ||
                    target.Name.String is not ("op_Equality" or "op_Inequality") ||
                    item.index < 2)
                    return false;
                var first = loadBundle.Body.Instructions[item.index - 2];
                var second = loadBundle.Body.Instructions[item.index - 1];
                return (first.Operand is IField firstField &&
                        firstField.FullName == bundleField.FullName &&
                        second.OpCode.Code == Code.Ldnull) ||
                       (second.Operand is IField secondField &&
                        secondField.FullName == bundleField.FullName &&
                        first.OpCode.Code == Code.Ldnull);
            });

        Assert.Multiple(() =>
        {
            Assert.That(
                bundleField.FieldSig!.Type.FullName,
                Is.EqualTo("System.Object"),
                "VirtualBundle handles must not remain typed as UnityEngine.AssetBundle.");
            Assert.That(
                report!.ManagedBridgeRewrites.Any(record =>
                    record.Method.Contains("BundleLoader::LoadBundle", StringComparison.Ordinal) &&
                    record.BridgeMethod.Contains("IsOpaqueHandleEqual", StringComparison.Ordinal)),
                Is.True,
                "The erased Bundle null check must use the opaque-handle bridge.");
            Assert.That(
                residualBundleNullOperator,
                Is.False,
                "Opaque AssetBundle fields must not reach UnityEngine.Object null operators.");
        });
    }

    /// <summary>Same inventory for JipperKeyViewer's main assembly.</summary>
    [Test]
    public void JipperKeyViewerMainAssemblyGapInventory()
    {
        var report = RewriteWithProductionSpecs(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll",
            required: false);
        ReportGaps(report!, "JipperKeyViewer.dll");
    }

    /// <summary>
    /// JipperKeyViewer's rewrite is now complete: all three issue categories are empty and the
    /// assembly is written.
    /// </summary>
    /// <remarks>
    /// A clean rewrite is not the same as a working MOD, and for this one the gap is unusually wide.
    /// <c>RainGraphic</c> only renders if the native render hook forwards
    /// <c>RawImage::OnPopulateMesh</c> to the bound managed instance, and none of that is observable
    /// here - it needs a live IL2CPP runtime. What this test does prove is that the registry accepted
    /// the type and the rewriter emitted the constructor change, which is the precondition.
    /// </remarks>
    [Test]
    public void JipperKeyViewerMainAssemblyRewritesClean()
    {
        var report = RewriteWithProductionSpecs(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll",
            required: false);
        AssertClean(report!, "JipperKeyViewer.dll");
    }

    [Test]
    public void JipperKeyViewerRootManagedComponentUsesTheLifecycleBridge()
    {
        var report = RewriteWithProductionSpecs(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll",
            required: false);
        AssertClean(report!, "JipperKeyViewer.dll");

        using var module = ModuleDefMD.Load(GetRewrittenPath(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll"));
        var enable = module.GetTypes()
            .Single(type => type.FullName == "JipperKeyViewer.Main")
            .Methods.Single(method => method.Name == "EnableKeyViewer");
        var calls = enable.Body.Instructions
            .Where(instruction =>
                instruction.OpCode.Code is Code.Call or Code.Callvirt &&
                instruction.Operand is IMethod)
            .Select(instruction => (IMethod)instruction.Operand)
            .ToArray();
        var bridged = calls.Where(method =>
            method.DeclaringType.FullName == typeof(PcCompatManagedComponentBridge).FullName &&
            method.Name == nameof(PcCompatManagedComponentBridge.AddComponent)).ToArray();
        var residual = calls.Where(method =>
            method.DeclaringType.FullName == "UnityEngine.GameObject" &&
            method.Name == "AddComponent").ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(bridged, Has.Length.EqualTo(1),
                "the root managed MonoBehaviour must enter the owner-scoped lifecycle bridge");
            Assert.That(residual, Is.Empty,
                "the root managed MonoBehaviour must not be submitted to the IL2CPP class table");
        });
    }

    [Test]
    public void JipperKeyViewerRainAwakeUsesOwnerAwareComponentBridges()
    {
        var report = RewriteWithProductionSpecs(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll",
            required: false);
        AssertClean(report!, "JipperKeyViewer.dll");

        using var module = ModuleDefMD.Load(GetRewrittenPath(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll"));
        var awake = module.GetTypes()
            .Single(type => type.FullName == "JipperKeyViewer.KeyViewer.Rain")
            .Methods.Single(method => method.Name == "Awake");
        var calls = awake.Body.Instructions
            .Where(instruction =>
                instruction.OpCode.Code is Code.Call or Code.Callvirt &&
                instruction.Operand is IMethod)
            .Select(instruction => (IMethod)instruction.Operand)
            .ToArray();
        var componentBridgeCalls = calls
            .Where(method =>
                method.DeclaringType.FullName == typeof(PcCompatManagedComponentBridge).FullName)
            .ToArray();
        var residualComponentCalls = calls
            .Where(method =>
                (method.DeclaringType.FullName == "UnityEngine.Component" &&
                 method.Name.String is "GetComponent" or "get_gameObject") ||
                (method.DeclaringType.FullName == "UnityEngine.GameObject" &&
                 method.Name.String == "AddComponent" &&
                 method.FullName.Contains(
                     "JipperKeyViewer.KeyViewer.RainGraphic",
                     StringComparison.Ordinal)))
            .Select(method => method.FullName)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(
                componentBridgeCalls.Any(method =>
                    method.Name == nameof(PcCompatManagedComponentBridge.GetComponent) &&
                    method.FullName.Contains("UnityEngine.RectTransform", StringComparison.Ordinal)),
                Is.True,
                "Rain.Awake must resolve RectTransform through its registered managed owner.");
            Assert.That(
                componentBridgeCalls.Any(method =>
                    method.Name == nameof(PcCompatManagedComponentBridge.GetGameObject)),
                Is.True,
                "Rain.Awake must resolve gameObject through its registered managed owner.");
            Assert.That(
                componentBridgeCalls.Any(method =>
                    method.Name == nameof(PcCompatManagedComponentBridge.AddComponent) &&
                    method.FullName.Contains("JipperKeyViewer.KeyViewer.RainGraphic", StringComparison.Ordinal)),
                Is.True,
                "RainGraphic must be registered through the managed render-component bridge.");
            Assert.That(
                residualComponentCalls,
                Is.Empty,
                "Rain.Awake must not invoke Unity component APIs on a CoreCLR-only component shell.");
        });
    }

    [Test]
    public void JipperKeyViewerRainInitUsesOwnerAwareGameObjectActivation()
    {
        var report = RewriteWithProductionSpecs(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll",
            required: false);
        AssertClean(report!, "JipperKeyViewer.dll");

        using var module = ModuleDefMD.Load(GetRewrittenPath(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll"));
        var initMethods = module.GetTypes()
            .Single(type => type.FullName == "JipperKeyViewer.KeyViewer.Rain")
            .Methods
            .Where(method => method.Name == "Init" && method.HasBody)
            .ToArray();
        var calls = initMethods
            .SelectMany(method => method.Body.Instructions)
            .Where(instruction =>
                instruction.OpCode.Code is Code.Call or Code.Callvirt &&
                instruction.Operand is IMethod)
            .Select(instruction => (IMethod)instruction.Operand)
            .ToArray();
        var bridgeCalls = calls
            .Where(method =>
                method.DeclaringType.FullName == typeof(PcCompatManagedComponentBridge).FullName)
            .ToArray();
        var residualSetActiveCalls = calls
            .Where(method =>
                method.DeclaringType.FullName == "UnityEngine.GameObject" &&
                method.Name.String == "SetActive")
            .Select(method => method.FullName)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(initMethods, Has.Length.EqualTo(2));
            Assert.That(
                bridgeCalls.Count(method =>
                    method.Name == nameof(PcCompatManagedComponentBridge.GetGameObject)),
                Is.EqualTo(2),
                "both Rain.Init overloads must resolve gameObject through the owner bridge");
            Assert.That(
                bridgeCalls.Count(method =>
                    method.Name == nameof(PcCompatManagedComponentBridge.SetActive)),
                Is.EqualTo(2),
                "both Rain.Init overloads must activate through the owner bridge");
            Assert.That(
                residualSetActiveCalls,
                Is.Empty,
                "Rain.Init must not call GameObject.SetActive directly on a generated proxy");
        });
    }

    [Test]
    public void JipperKeyViewerManagedKeyNullChecksUseManagedComponentObjectSemantics()
    {
        var report = RewriteWithProductionSpecs(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll",
            required: false);
        AssertClean(report!, "JipperKeyViewer.dll");

        using var module = ModuleDefMD.Load(GetRewrittenPath(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll"));
        var targetNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "ProcessKeyGroup",
            "UpdateKeyText",
            "UpdateAllFonts"
        };
        var targets = module.GetTypes()
            .SelectMany(type => type.Methods)
            .Where(method => method.HasBody && targetNames.Contains(method.Name.String))
            .ToArray();
        var calls = targets
            .SelectMany(method => method.Body.Instructions
                .Where(instruction =>
                    instruction.OpCode.Code is Code.Call or Code.Callvirt &&
                    instruction.Operand is IMethod)
                .Select(instruction => (Method: method, Target: (IMethod)instruction.Operand)))
            .ToArray();
        var residualUnityOperators = calls
            .Where(call =>
                call.Target.DeclaringType.FullName == "UnityEngine.Object" &&
                call.Target.Name.String is "op_Equality" or "op_Inequality" or "op_Implicit")
            .Select(call => $"{call.Method.FullName} -> {call.Target.FullName}")
            .ToArray();
        var bridgeCalls = calls
            .Where(call =>
                call.Target.DeclaringType.FullName == typeof(PcCompatManagedComponentBridge).FullName &&
                call.Target.Name.String is "ObjectEquals" or "ObjectNotEquals" or "ObjectImplicit")
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(
                targets.Select(method => method.Name.String).Distinct(),
                Is.SupersetOf(targetNames),
                "the real JPKV payload no longer contains the audited key paths");
            Assert.That(residualUnityOperators, Is.Empty,
                "managed Key instances must not reach Unity native fake-null operators");
            Assert.That(
                bridgeCalls.Select(call => call.Method.Name.String).Distinct(),
                Is.SupersetOf(targetNames),
                "input, key text and font refresh must all use managed-component object semantics");
        });
    }

    [Test]
    public void JipperKeyViewerRuntimeFontCreationRejectsUnusableFontFacesThroughTheManagedBridge()
    {
        var report = RewriteWithProductionSpecs(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll",
            required: false);
        AssertClean(report!, "JipperKeyViewer.dll");

        var rewrites = report!.ManagedBridgeRewrites
            .Where(record => record.SourceMethod.Contains(
                "TMPro.TMP_FontAsset::CreateFontAsset(UnityEngine.Font)",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(rewrites, Has.Length.EqualTo(4));
            Assert.That(
                rewrites.All(record => record.BridgeMethod.Contains(
                    $"{typeof(PcCompatManagedFontBridge).FullName}::CreateFontAsset",
                    StringComparison.Ordinal)),
                Is.True);
        });
    }

    [Test]
    public void JipperKeyViewerFinalTmpFontAndMaterialBindingsPreserveTheSelectedAtlas()
    {
        var report = RewriteWithProductionSpecs(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll",
            required: false);
        AssertClean(report!, "JipperKeyViewer.dll");

        var materialRewrites = report!.ManagedBridgeRewrites
            .Where(record => record.SourceMethod.Contains(
                "TMPro.TMP_Text::set_fontMaterial(UnityEngine.Material)",
                StringComparison.Ordinal))
            .ToArray();
        var fontRewrites = report.ManagedBridgeRewrites
            .Where(record => record.SourceMethod.Contains(
                "TMPro.TMP_Text::set_font(TMPro.TMP_FontAsset)",
                StringComparison.Ordinal))
            .ToArray();
        using var module = ModuleDefMD.Load(GetRewrittenPath(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll"));
        var residual = module.GetTypes()
            .SelectMany(type => type.Methods.Where(method => method.HasBody))
            .SelectMany(method => method.Body.Instructions)
            .Where(instruction =>
                instruction.OpCode.Code is Code.Call or Code.Callvirt &&
                instruction.Operand is IMethod target &&
                target.DeclaringType.FullName == "TMPro.TMP_Text" &&
                target.Name.String is "set_font" or "set_fontMaterial" or "set_fontSharedMaterial")
            .Select(instruction => instruction.Operand!.ToString())
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(materialRewrites, Has.Length.GreaterThanOrEqualTo(2));
            Assert.That(materialRewrites.All(record => record.BridgeMethod.Contains(
                $"{typeof(PcCompatManagedFontBridge).FullName}::SetFontMaterial",
                StringComparison.Ordinal)), Is.True);
            Assert.That(fontRewrites, Has.Length.GreaterThanOrEqualTo(2));
            Assert.That(fontRewrites.All(record => record.BridgeMethod.Contains(
                $"{typeof(PcCompatManagedFontBridge).FullName}::SetFont",
                StringComparison.Ordinal)), Is.True);
            Assert.That(residual, Is.Empty,
                "all final TMP font/material assignments must preserve the selected font atlas");
        });
    }

    [Test]
    public void JipperKeyViewerLegacyInputPollingUsesTheOwnerScopedConsumerBridge()
    {
        var report = RewriteWithProductionSpecs(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll",
            required: false);
        AssertClean(report!, "JipperKeyViewer.dll");

        using var module = ModuleDefMD.Load(GetRewrittenPath(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll"));
        var calls = module.GetTypes()
            .SelectMany(type => type.Methods.Where(method => method.HasBody))
            .SelectMany(method => method.Body.Instructions.Select((instruction, index) =>
                (Method: method, Instruction: instruction, Index: index)))
            .Where(item =>
                item.Instruction.OpCode.Code is Code.Call or Code.Callvirt &&
                item.Instruction.Operand is IMethod)
            .ToArray();
        var bridged = calls
            .Where(item =>
                ((IMethod)item.Instruction.Operand).DeclaringType.FullName ==
                typeof(PcCompatLegacyInputBridge).FullName)
            .ToArray();
        var residual = calls
            .Where(item =>
            {
                var target = (IMethod)item.Instruction.Operand;
                return target.DeclaringType.FullName == "UnityEngine.Input" &&
                       target.Name.String is "GetKey" or "GetKeyDown" or "GetKeyUp" or
                           "get_anyKeyDown";
            })
            .Select(item => $"{item.Method.FullName} -> {item.Instruction.Operand}")
            .ToArray();

        var heldCalls = bridged.Where(item =>
            ((IMethod)item.Instruction.Operand).Name ==
            nameof(PcCompatLegacyInputBridge.GetKeyOwned)).ToArray();
        var downCalls = bridged.Where(item =>
            ((IMethod)item.Instruction.Operand).Name ==
            nameof(PcCompatLegacyInputBridge.GetKeyDownOwned)).ToArray();
        var anyDownCalls = bridged.Where(item =>
            ((IMethod)item.Instruction.Operand).Name ==
            nameof(PcCompatLegacyInputBridge.GetAnyKeyDownOwned)).ToArray();
        const string applicationBridge =
            "Xphorror.PcModCompat.PcCompatManagedApplicationBridge";
        var focusBridgeCalls = calls.Where(item =>
        {
            var target = (IMethod)item.Instruction.Operand;
            return target.DeclaringType.FullName == applicationBridge &&
                   target.Name.String == "GetIsFocused";
        }).ToArray();
        var residualFocusCalls = calls.Where(item =>
        {
            var target = (IMethod)item.Instruction.Operand;
            return target.DeclaringType.FullName == "UnityEngine.Application" &&
                   target.Name.String == "get_isFocused";
        }).Select(item => $"{item.Method.FullName} -> {item.Instruction.Operand}").ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(residual, Is.Empty, "a JPKV polling call still targets UnityEngine.Input");
            Assert.That(heldCalls, Has.Length.EqualTo(2),
                "ProcessKeyGroup and ProcessGhostKeysInUpdate must both use the consumer bridge");
            Assert.That(downCalls, Has.Length.EqualTo(1),
                "ProcessKeySelection must use the owner-scoped edge bridge");
            Assert.That(anyDownCalls, Has.Length.EqualTo(1),
                "ProcessKeySelection must use the owner-scoped any-key bridge");
            Assert.That(residualFocusCalls, Is.Empty,
                "JPKV must not gate input on the desktop Application.isFocused proxy");
            Assert.That(focusBridgeCalls, Has.Length.EqualTo(2),
                "Update and ProcessKeySelection must both use the Android lifecycle focus bridge");
            Assert.That(
                heldCalls.Select(item => item.Method.Name.String),
                Is.EquivalentTo(new[] { "ProcessKeyGroup", "ProcessGhostKeysInUpdate" }));
            Assert.That(
                bridged,
                Is.All.Matches<(MethodDef Method, Instruction Instruction, int Index)>(item =>
                    item.Index > 1 &&
                    item.Method.Body.Instructions[item.Index - 2].IsLdcI4() &&
                    item.Method.Body.Instructions[item.Index - 1].OpCode.Code == Code.Ldstr &&
                    (string)item.Method.Body.Instructions[item.Index - 1].Operand ==
                    "JipperKeyViewer"),
                "every rewritten JPKV input call must carry a token and the package owner");
        });
    }

    [Test]
    public void RealModsRouteDirectoryFileEnumerationThroughOwnerVfsBridge()
    {
        var cases = new[]
        {
            (Directory: "JipperOverlayer-UMM", Assembly: "JipperOverlayer.dll", Expected: 1),
            (Directory: "JipperKeyViewer-AssetBundle", Assembly: "JipperKeyViewer.dll", Expected: 3)
        };

        foreach (var item in cases)
        {
            var report = RewriteWithProductionSpecs(
                item.Directory,
                item.Assembly,
                required: false);
            AssertClean(report!, item.Assembly);

            var rewrites = report!.ManagedBridgeRewrites
                .Where(record => record.SourceMethod.Contains(
                    "System.IO.Directory::GetFiles",
                    StringComparison.Ordinal))
                .Where(record => record.BridgeMethod.Contains(
                    $"{typeof(PcCompatManagedPathBridge).FullName}::DirectoryGetFiles",
                    StringComparison.Ordinal))
                .ToArray();

            using var module = ModuleDefMD.Load(GetRewrittenPath(item.Directory, item.Assembly));
            var residual = module.GetTypes()
                .SelectMany(type => type.Methods.Where(method => method.HasBody))
                .SelectMany(method => method.Body.Instructions)
                .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
                .Select(instruction => instruction.Operand)
                .OfType<IMethod>()
                .Where(method =>
                    method.DeclaringType.FullName == "System.IO.Directory" &&
                    method.Name.String == "GetFiles")
                .Select(method => method.FullName)
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(rewrites, Has.Length.EqualTo(item.Expected), item.Assembly);
                Assert.That(
                    residual,
                    Is.Empty,
                    $"{item.Assembly} retains raw Directory.GetFiles calls: " +
                    string.Join(Environment.NewLine, residual));
            });
        }
    }

    [Test]
    public void JipperOverlayerLevelNameBaselineUsesGeneratedNullableVector2Property()
    {
        var report = RewriteWithProductionSpecs(
            "JipperOverlayer-UMM",
            "JipperOverlayer.dll",
            required: false);

        var records = report!.Rewrites
            .Where(record => record.OriginalField.Contains(
                "scrController::txtLevelNameOriginalPosition",
                StringComparison.Ordinal))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(records, Has.Length.EqualTo(2));
            Assert.That(
                records.All(record => record.ProxyAccessor.Contains(
                    "set_txtLevelNameOriginalPosition",
                    StringComparison.Ordinal)),
                Is.True,
                "the nullable Vector2 setter must be the generated proxy target");
            Assert.That(
                records.All(record => record.ProxyAccessor.Contains(
                    "ToIl2CppNullable",
                    StringComparison.Ordinal)),
                Is.True,
                "field stores must convert CoreCLR Nullable<Vector2> to the generated IL2CPP wrapper");
        });

        using var module = ModuleDefMD.Load(report!.OutputPath);
        foreach (var methodName in new[] { "ResetLevelName", "ApplyLevelNamePatch" })
        {
            var method = module.GetTypes()
                .SelectMany(type => type.Methods)
                .Single(method => method.FullName.Contains(
                    $"Overlay::{methodName}()",
                    StringComparison.Ordinal));
            var setterIndexes = method.Body!.Instructions
                .Select((instruction, index) => (instruction, index))
                .Where(item => item.instruction.OpCode.Code is Code.Call or Code.Callvirt &&
                               item.instruction.Operand is IMethod target &&
                               target.Name == "set_txtLevelNameOriginalPosition")
                .Select(item => item.index)
                .ToArray();
            Assert.That(setterIndexes, Has.Length.EqualTo(1), methodName);
            var setterIndex = setterIndexes[0];
            Assert.That(
                setterIndex > 0 &&
                method.Body.Instructions[setterIndex - 1].OpCode.Code == Code.Call &&
                method.Body.Instructions[setterIndex - 1].Operand is IMethod converter &&
                converter.Name == "ToIl2CppNullable",
                Is.True,
                $"{methodName} must convert the nullable value immediately before the proxy setter");

            var setter = (IMethod)method.Body.Instructions[setterIndex].Operand!;
            var converterCall = (IMethod)method.Body.Instructions[setterIndex - 1].Operand!;
            Assert.Multiple(() =>
            {
                Assert.That(
                    setter.MethodSig?.Params.Select(parameter => parameter.FullName).ToArray(),
                    Is.EqualTo(["Il2CppSystem.Nullable`1<UnityEngine.Vector2>"]),
                    $"{methodName} setter must consume the generated Nullable proxy object");
                Assert.That(converterCall, Is.InstanceOf<MethodSpec>(),
                    $"{methodName} converter must be a closed generic method");
                Assert.That(
                    ((MethodSpec)converterCall).GenericInstMethodSig.GenericArguments
                        .Single().FullName,
                    Is.EqualTo("UnityEngine.Vector2"),
                    $"{methodName} converter must be closed over Vector2");
            });
        }

        var residualSourceFieldAccesses = module.GetTypes()
            .SelectMany(type => type.Methods)
            .Where(method => method.Name.String is "ResetLevelName" or "ApplyLevelNamePatch")
            .SelectMany(method => method.Body?.Instructions ?? [])
            .Where(instruction => instruction.OpCode.Code is Code.Ldfld or Code.Stfld or Code.Ldsfld or Code.Stsfld)
            .Select(instruction => instruction.Operand)
            .OfType<IField>()
            .Where(field => field.FullName.Contains(
                "scrController::txtLevelNameOriginalPosition",
                StringComparison.Ordinal))
            .ToArray();
        Assert.That(
            residualSourceFieldAccesses,
            Is.Empty,
            "JPOV level-name methods must not retain the original PC field access after facade rewrite");
    }

    /// <summary>
    /// The registered render component's base constructor call is blanked, and nothing else in the
    /// assembly is.
    /// </summary>
    /// <remarks>
    /// This is the load-bearing half of the rewrite. The proxy <c>MaskableGraphic..ctor()</c> ends in
    /// an <c>il2cpp_runtime_invoke</c> of the native base constructor on <c>this</c>, and the bridge
    /// binds <c>this</c> to a host component that <c>AddComponent</c> already constructed - so leaving
    /// the call in place would re-run native construction on a live object. Asserting the count is
    /// exactly one also pins the narrowness: a bug that blanked base constructors generally would
    /// leave every MOD MonoBehaviour half-constructed.
    /// </remarks>
    [Test]
    public void RegisteredRenderComponentBaseConstructorIsBlanked()
    {
        var report = RewriteWithProductionSpecs(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll",
            required: false);

        var blanked = report!.ManagedBridgeRewrites
            .Where(record => record.BridgeMethod.Contains(
                "render-component base constructor blanked",
                StringComparison.Ordinal))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(blanked, Has.Length.EqualTo(1));
            Assert.That(
                blanked[0].Method,
                Does.Contain("JipperKeyViewer.KeyViewer.RainGraphic::.ctor"));
            Assert.That(
                blanked[0].SourceMethod,
                Is.EqualTo("UnityEngine.UI!UnityEngine.UI.MaskableGraphic::.ctor()"));
            Assert.That(blanked[0].Opcode, Is.EqualTo("call"));
        });
    }

    [Test]
    public void DevelopmentKeyViewerRenderLayersUseTheGenericProductionRewritePath()
    {
        var input = Path.Combine(
            FindRepoRoot(),
            "build",
            "jpkv_source_audit",
            "source_v170_dev",
            "JipperKeyViewer",
            "bin",
            "Release",
            "JipperKeyViewer.dll");
        Assume.That(File.Exists(input), Is.True, $"missing source-audit assembly: {input}");
        var staticScan = PcCompatStaticPatchScanner.ScanAssemblies("JipperKeyViewer", [input]);
        var componentTypes = staticScan.ManagedRenderComponents
            .Select(item => item.ComponentType)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(componentTypes, Does.Contain("JipperKeyViewer.KeyViewer.KeyShapeLayer"));
            Assert.That(componentTypes, Does.Contain("JipperKeyViewer.KeyViewer.RainLayer"));
            Assert.That(componentTypes, Does.Contain("JipperKeyViewer.KeyViewer.GhostRainLayer"));
        });

        var report = RewriteAssemblyWithProductionSpecs(
            input,
            "JipperKeyViewer",
            staticScan,
            isPrimary: true,
            renderComponentOverride: null);
        var rewriteFailures = string.Join(
            Environment.NewLine,
            report.Issues
                .Concat(report.MethodIssues.Select(issue => issue.SurfaceEntry ?? issue.Target))
                .Concat(report.ManagedBridgeIssues.Select(issue => issue.Reason))
                .Distinct(StringComparer.Ordinal));
        Assert.Multiple(() =>
        {
            Assert.That(report.Issues, Is.Empty, rewriteFailures);
            Assert.That(report.MethodIssues, Is.Empty, rewriteFailures);
            Assert.That(report.ManagedBridgeIssues, Is.Empty, rewriteFailures);
            Assert.That(report.OutputWritten, Is.True, rewriteFailures);
            Assert.That(
                report.ManagedBridgeRewrites.Count(record =>
                    record.BridgeMethod.Contains(
                        "render-component base constructor blanked",
                        StringComparison.Ordinal)),
                Is.EqualTo(3));
        });
    }

    /// <summary>
    /// An unregistered type deriving a proxy class is still refused, with a message naming the real
    /// reason.
    /// </summary>
    /// <remarks>
    /// This is what makes the relaxation a registry rather than a general loosening. Dropping the
    /// registration requirement would accept any MOD type deriving any proxy component -
    /// <c>Selectable</c>, <c>ScrollRect</c>, <c>LayoutGroup</c> - and each would rewrite clean and
    /// then silently never receive its callbacks, because only <c>OnPopulateMesh</c> is hooked. The
    /// old message claimed the type "does not derive UnityEngine.MonoBehaviour", which was never the
    /// failing condition; it is asserted here so it cannot regress to that.
    /// </remarks>
    [Test]
    public void UnregisteredProxyDerivedComponentStillFailsClosed()
    {
        var report = RewriteWithProductionSpecs(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll",
            required: false,
            renderComponentOverride: []);

        Assert.Multiple(() =>
        {
            Assert.That(report!.OutputWritten, Is.False);
            var issue = report.ManagedBridgeIssues.SingleOrDefault(candidate =>
                candidate.Reason.Contains("RainGraphic", StringComparison.Ordinal));
            Assert.That(issue, Is.Not.Null, "RainGraphic must still be refused when unregistered");
            Assert.That(
                issue!.Reason,
                Does.Contain("leaves the MOD's own modules"),
                "the message must name the real condition, not MonoBehaviour derivation");
            Assert.That(
                issue.Reason,
                Does.Not.Contain("does not derive UnityEngine.MonoBehaviour"));
        });
    }

    /// <summary>
    /// A registration whose declared base type no longer matches fails closed rather than binding the
    /// managed shell to the wrong host component.
    /// </summary>
    /// <remarks>
    /// Not hypothetical: JipperKeyViewer's own development branch already replaced
    /// <c>RainGraphic</c> with three self-drawing layers, so a future release could well reparent or
    /// remove the type. The registration names <c>MaskableGraphic</c> and the rewriter verifies it.
    /// </remarks>
    [Test]
    public void RenderComponentRegistrationVerifiesTheDeclaredBaseType()
    {
        var report = RewriteWithProductionSpecs(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll",
            required: false,
            renderComponentOverride:
            [
                new ManagedRenderComponentSpec(
                    "JipperKeyViewer",
                    "JipperKeyViewer.KeyViewer.RainGraphic",
                    "UnityEngine.UI",
                    "UnityEngine.UI.Image")
            ]);

        Assert.Multiple(() =>
        {
            Assert.That(report!.OutputWritten, Is.False);
            Assert.That(
                report.ManagedBridgeIssues.Any(issue =>
                    issue.Reason.Contains("UnityEngine.UI.MaskableGraphic", StringComparison.Ordinal) &&
                    issue.Reason.Contains("registration declares", StringComparison.Ordinal)),
                Is.True,
                "a mismatched declared base must be reported, not silently accepted");
        });
    }

    /// <summary>
    /// JipperKeyViewer references neither 0Harmony nor Assembly-CSharp - it is pure Unity plus the
    /// UMM entry point. That is a load-bearing fact for this phase: JPKV needs no Harmony work at
    /// all, and its whole compatibility surface is Unity proxies and input. Pinning it here means a
    /// future JPKV version that starts patching the game cannot slip in unnoticed.
    /// </summary>
    [Test]
    public void JipperKeyViewerReferencesNoHarmonyAndNoGameAssembly()
    {
        var input = Path.Combine(
            FindRepoRoot(),
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll");
        if (!File.Exists(input))
            Assert.Ignore($"MOD payload is absent: {input}");

        using var module = dnlib.DotNet.ModuleDefMD.Load(input);
        var references = module.GetAssemblyRefs()
            .Select(reference => reference.Name.String)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(references, Does.Not.Contain("0Harmony"));
            Assert.That(references, Does.Not.Contain("Assembly-CSharp"));
            Assert.That(references, Does.Not.Contain("JALib"));
        });
    }

    /// <summary>
    /// Pins that the audited main assemblies remain fully closed. A regression that reopens a gap
    /// fails here instead of silently shrinking an inventory.
    /// </summary>
    /// <remarks>
    /// The PC field-backed nullable-vector surface is covered by the generated setter assertion
    /// above; both MODs must now produce complete rewritten assemblies.
    /// </remarks>
    [Test]
    public void RemainingGapsAreExactlyTheKnownSet()
    {
        var overlayer = RewriteWithProductionSpecs(
            "JipperOverlayer-UMM",
            "JipperOverlayer.dll",
            required: false);
        var keyViewer = RewriteWithProductionSpecs(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll",
            required: false);

        Assert.Multiple(() =>
        {
            Assert.That(
                CountByTarget(overlayer!),
                Is.Empty,
                "JipperOverlayer has no remaining argument-form mismatches");
            Assert.That(overlayer!.Issues, Is.Empty);
            Assert.That(overlayer.OutputWritten, Is.True);

            Assert.That(
                CountByTarget(keyViewer!),
                Is.Empty,
                "JipperKeyViewer has no remaining argument-form mismatches");
            Assert.That(keyViewer!.Issues, Is.Empty);
            Assert.That(
                keyViewer.ManagedBridgeIssues,
                Is.Empty,
                "RainGraphic is now accepted by the render-component registry");
            Assert.That(keyViewer.OutputWritten, Is.True);
        });
    }

    [Test]
    public void RealModsRouteEveryExternalStaticEventThroughTheOwnerBridge()
    {
        var cases = new[]
        {
            (Directory: "JipperResourcePack_release", Assembly: "JipperResourcePack.dll"),
            (Directory: "JipperOverlayer-UMM", Assembly: "JipperOverlayer.dll"),
            (Directory: "JipperKeyViewer-AssetBundle", Assembly: "JipperKeyViewer.dll")
        };

        foreach (var item in cases)
        {
            var report = RewriteWithProductionSpecs(
                item.Directory,
                item.Assembly,
                required: item.Directory == "JipperResourcePack_release");
            AssertClean(report!, item.Assembly);

            using var module = ModuleDefMD.Load(GetRewrittenPath(item.Directory, item.Assembly));
            var rawAccessors = module.GetTypes()
                .SelectMany(type => type.Methods.Where(method => method.HasBody))
                .SelectMany(method => method.Body.Instructions)
                .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
                .Select(instruction => instruction.Operand)
                .OfType<IMethod>()
                .Where(method =>
                    (method.DeclaringType.FullName is
                        "UnityEngine.Application" or
                        "UnityEngine.SceneManagement.SceneManager") &&
                    (method.Name.String.StartsWith("add_", StringComparison.Ordinal) ||
                     method.Name.String.StartsWith("remove_", StringComparison.Ordinal)))
                .Select(method => method.FullName)
                .ToArray();

            Assert.That(
                rawAccessors,
                Is.Empty,
                $"{item.Assembly} retains external static-event accessors: " +
                string.Join(Environment.NewLine, rawAccessors));
        }
    }

    /// <summary>
    /// Every <c>JsonUtility</c> callsite must be replaced, including <c>FromJson&lt;T&gt;</c> - whose
    /// proxy signature matches exactly, so the rewriter would have reported it clean while it failed
    /// at runtime for want of an IL2CPP class-table entry for <c>T</c>.
    /// </summary>
    [Test]
    public void JsonUtilityIsReplacedByTheManagedSerializer()
    {
        var keyViewer = RewriteWithProductionSpecs(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll",
            required: false);

        var bridged = keyViewer!.ManagedBridgeRewrites
            .Where(record => record.SourceMethod.StartsWith(
                "UnityEngine.JSONSerializeModule!UnityEngine.JsonUtility::",
                StringComparison.Ordinal))
            .GroupBy(
                record => record.SourceMethod.Split("::")[1].Split('(')[0],
                StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(
                bridged,
                Is.EqualTo(new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["ToJson"] = 3,
                    ["FromJsonOverwrite"] = 3,
                    ["FromJson"] = 1
                }));
            Assert.That(
                keyViewer.MethodCalls.Where(record => record.Target.Contains(
                    "UnityEngine.JsonUtility::",
                    StringComparison.Ordinal)),
                Is.Empty,
                "a JsonUtility callsite still resolves to the proxy");
        });
    }

    /// <summary>
    /// Pins the converter chosen for each argument-form mismatch, by identity. The gap-count test
    /// only proves the callsite stopped being an issue; this proves it resolved to the intended
    /// conversion rather than to some other same-name proxy overload.
    /// </summary>
    [Test]
    public void ArgumentFormMismatchesResolveToTheIntendedConverters()
    {
        var overlayer = RewriteWithProductionSpecs(
            "JipperOverlayer-UMM",
            "JipperOverlayer.dll",
            required: false);
        var keyViewer = RewriteWithProductionSpecs(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll",
            required: false);

        Assert.Multiple(() =>
        {
            // System.Text.StringBuilder -> Il2CppSystem.Text.StringBuilder needs a host helper; no
            // op_Implicit exists for this pair.
            AssertConverted(
                overlayer!,
                "TMPro.TMP_Text::SetText",
                10,
                "StArray.ModManager.Android.PcCompat.PcCompatAbiBridge::ToIl2CppStringBuilder");
            // SelectionGrid is now rebuilt by the managed IMGUI bridge. It must not fall back to
            // the old String[] -> Il2CppStringArray converter and native wrapper resolution.
            AssertBridged(
                overlayer!,
                "UnityEngine.GUILayout::SelectionGrid",
                2,
                nameof(PcCompatManagedImGuiBridge.SelectionGrid),
                requiresCallsiteToken: true);
            AssertBridged(
                keyViewer!,
                "UnityEngine.GUILayout::SelectionGrid",
                3,
                nameof(PcCompatManagedImGuiBridge.SelectionGrid),
                requiresCallsiteToken: true);
            AssertBridged(
                keyViewer!,
                "UnityEngine.GUILayout::Label",
                1,
                nameof(PcCompatManagedImGuiBridge.LabelContent));
            // Char[] -> Il2CppStructArray<Char>, mismatch at parameter 0 of 3.
            AssertConverted(
                keyViewer!,
                "TMPro.TMP_Text::SetText",
                6,
                "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray`1<System.Char>::op_Implicit");
            // List<TMP_FontAsset> -> Il2CppSystem...List<TMP_FontAsset>, the setter half of the
            // property whose getter goes through CopyList.
            AssertConverted(
                overlayer!,
                "TMPro.TMP_FontAsset::set_fallbackFontAssetTable",
                2,
                "StArray.ModManager.Android.PcCompat.PcCompatCollectionBridge::ToIl2CppList");
            AssertConverted(
                keyViewer!,
                "TMPro.TMP_FontAsset::set_fallbackFontAssetTable",
                1,
                "StArray.ModManager.Android.PcCompat.PcCompatCollectionBridge::ToIl2CppList");
        });
    }

    [Test]
    public void AndroidStrippedImGuiSurfaceUsesManagedBridgeWithValueTypeSafeIl()
    {
        var overlayer = RewriteWithProductionSpecs(
            "JipperOverlayer-UMM",
            "JipperOverlayer.dll",
            required: false);
        var keyViewer = RewriteWithProductionSpecs(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll",
            required: false);
        AssertClean(overlayer!, "JipperOverlayer.dll");
        AssertClean(keyViewer!, "JipperKeyViewer.dll");

        var reports = new[] { overlayer, keyViewer };
        var expectedBridgeMethods = new[]
        {
            nameof(PcCompatManagedImGuiBridge.BeginHorizontal),
            nameof(PcCompatManagedImGuiBridge.EndHorizontal),
            nameof(PcCompatManagedImGuiBridge.BeginVertical),
            nameof(PcCompatManagedImGuiBridge.EndVertical),
            nameof(PcCompatManagedImGuiBridge.Space),
            nameof(PcCompatManagedImGuiBridge.FlexibleSpace),
            nameof(PcCompatManagedImGuiBridge.Width),
            nameof(PcCompatManagedImGuiBridge.MinWidth),
            nameof(PcCompatManagedImGuiBridge.Height),
            nameof(PcCompatManagedImGuiBridge.ExpandWidth),
            nameof(PcCompatManagedImGuiBridge.ButtonText),
            nameof(PcCompatManagedImGuiBridge.ButtonTextWithStyle),
            nameof(PcCompatManagedImGuiBridge.ToggleText),
            nameof(PcCompatManagedImGuiBridge.ToggleTextWithStyle),
            nameof(PcCompatManagedImGuiBridge.ToggleContent),
            nameof(PcCompatManagedImGuiBridge.LabelText),
            nameof(PcCompatManagedImGuiBridge.DrawTexture),
            nameof(PcCompatManagedImGuiBridge.LabelContent),
            nameof(PcCompatManagedImGuiBridge.SelectionGrid),
            nameof(PcCompatManagedImGuiBridge.HorizontalSlider),
            nameof(PcCompatManagedImGuiBridge.TextField),
            nameof(PcCompatManagedImGuiBridge.BeginVerticalWithStyle),
            nameof(PcCompatManagedImGuiBridge.GetRect)
        };
        foreach (var bridgeMethod in expectedBridgeMethods)
        {
            Assert.That(
                reports.SelectMany(report => report!.ManagedBridgeRewrites).Any(record =>
                    record.BridgeMethod.Contains(
                        $"{typeof(PcCompatManagedImGuiBridge).FullName}::{bridgeMethod}",
                        StringComparison.Ordinal)),
                Is.True,
                $"no real UMM callsite was rewritten to {bridgeMethod}");
        }

        using var overlayerModule = ModuleDefMD.Load(GetRewrittenPath(
            "JipperOverlayer-UMM",
            "JipperOverlayer.dll"));
        using var keyViewerModule = ModuleDefMD.Load(GetRewrittenPath(
            "JipperKeyViewer-AssetBundle",
            "JipperKeyViewer.dll"));
        var callsites = new[] { overlayerModule, keyViewerModule }
            .SelectMany(module => module.GetTypes())
            .SelectMany(type => type.Methods.Where(method => method.HasBody))
            .SelectMany(method => method.Body.Instructions.Select(instruction => (Method: method, Instruction: instruction)))
            .Where(callsite =>
                callsite.Instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj &&
                callsite.Instruction.Operand is IMethod)
            .ToArray();

        var leaked = callsites
            .Select(callsite => BuildSurfaceEntry((IMethod)callsite.Instruction.Operand))
            .Where(entry => entry is not null && ProxySurfaceIdentity.IsManagedBridgeOwnedEntry(entry))
            .ToArray();
        Assert.That(leaked, Is.Empty, "a bridge-owned call still targets the shared proxy");

        var responsiveGUILayoutNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "BeginHorizontal",
            "EndHorizontal",
            "BeginVertical",
            "EndVertical",
            "Space",
            "FlexibleSpace",
            "Width",
            "MinWidth",
            "Height",
            "ExpandWidth",
            "Button",
            "Label",
            "Toggle",
            "TextField",
            "TextArea",
            "HorizontalSlider"
        };
        var responsiveLeaks = callsites
            .Select(callsite => (IMethod)callsite.Instruction.Operand)
            .Where(target =>
                target.DeclaringType.FullName == "UnityEngine.GUILayout" &&
                responsiveGUILayoutNames.Contains(target.Name))
            .Select(target => target.FullName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.That(
            responsiveLeaks,
            Is.Empty,
            "a confirmed responsive GUILayout call bypassed the token bridge");

        var bridgeCallsites = callsites
            .Where(callsite =>
                callsite.Instruction.Operand is IMethod target &&
                target.DeclaringType.FullName == typeof(PcCompatManagedImGuiBridge).FullName)
            .ToArray();
        var drawTextureCalls = bridgeCallsites
            .Where(callsite => ((IMethod)callsite.Instruction.Operand).Name ==
                               nameof(PcCompatManagedImGuiBridge.DrawTexture))
            .ToArray();
        Assert.That(drawTextureCalls, Is.Not.Empty);
        Assert.That(
            drawTextureCalls.Select(callsite => callsite.Instruction.Operand),
            Has.All.InstanceOf<MethodSpec>());
        Assert.That(
            drawTextureCalls
                .Select(callsite => (MethodSpec)callsite.Instruction.Operand)
                .Select(method => method.GenericInstMethodSig.GenericArguments.Single().FullName),
            Is.All.EqualTo("UnityEngine.Rect"),
            "DrawTexture must carry Rect as a generic value-type argument without erasing it to object");

        var getRectCalls = bridgeCallsites
            .Where(callsite => ((IMethod)callsite.Instruction.Operand).Name ==
                               nameof(PcCompatManagedImGuiBridge.GetRect))
            .ToArray();
        Assert.That(getRectCalls, Is.Not.Empty);
        foreach (var callsite in getRectCalls)
        {
            var instructions = callsite.Method.Body.Instructions;
            var index = instructions.IndexOf(callsite.Instruction);
            Assert.That(index + 1, Is.LessThan(instructions.Count));
            Assert.Multiple(() =>
            {
                Assert.That(instructions[index + 1].OpCode.Code, Is.EqualTo(Code.Unbox_Any));
                Assert.That(
                    (instructions[index + 1].Operand as ITypeDefOrRef)?.FullName,
                    Is.EqualTo("UnityEngine.Rect"));
            });
        }
    }

    /// <summary>
    /// <c>Debug.Log</c> and friends must be replaced outright, not converted: nothing may still call
    /// the <c>UnityEngine.Debug</c> proxy afterwards.
    /// </summary>
    [Test]
    public void DebugLoggingIsReplacedByTheHostLogBridge()
    {
        var overlayer = RewriteWithProductionSpecs(
            "JipperOverlayer-UMM",
            "JipperOverlayer.dll",
            required: false);

        var bridged = overlayer!.ManagedBridgeRewrites
            .Where(record => record.SourceMethod.StartsWith(
                "UnityEngine.CoreModule!UnityEngine.Debug::",
                StringComparison.Ordinal))
            .GroupBy(
                record => record.SourceMethod.Split("::")[1].Split('(')[0],
                StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(
                bridged,
                Is.EqualTo(new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["Log"] = 6,
                    ["LogWarning"] = 8,
                    ["LogError"] = 2
                }));
            // Every one must land on the host log bridge, not on some other bridge type.
            Assert.That(
                overlayer.ManagedBridgeRewrites
                    .Where(record => record.SourceMethod.StartsWith(
                        "UnityEngine.CoreModule!UnityEngine.Debug::",
                        StringComparison.Ordinal))
                    .All(record => record.BridgeMethod.Contains(
                        "Xphorror.PcModCompat.PcCompatManagedLogBridge::",
                        StringComparison.Ordinal)),
                Is.True);
            Assert.That(
                overlayer.MethodCalls.Where(record => record.Target.Contains(
                    "UnityEngine.Debug::",
                    StringComparison.Ordinal)),
                Is.Empty,
                "a Debug callsite still resolves to the proxy");
        });
    }

    private static void AssertConverted(
        RewriteReport report,
        string targetMember,
        int expectedCallsites,
        string expectedConverter)
    {
        // The record renders as "<proxy signature> <- <return type> <converter signature>", so the
        // converter is matched by name and parameter list rather than by an exact record string.
        var records = report.MethodCalls
            .Where(record => record.Target.Contains(targetMember, StringComparison.Ordinal))
            .Where(record => record.ProxyMethod.Contains(
                " <- ",
                StringComparison.Ordinal))
            .Where(record => record.ProxyMethod.Contains(
                expectedConverter,
                StringComparison.Ordinal))
            .ToArray();
        Assert.That(
            records,
            Has.Length.EqualTo(expectedCallsites),
            $"{targetMember} -> {expectedConverter}");
        // The converter must be on the argument side. A return-side converter renders as " -> ".
        Assert.That(
            records.All(record =>
                record.ProxyMethod.IndexOf(expectedConverter, StringComparison.Ordinal) >
                record.ProxyMethod.IndexOf(" <- ", StringComparison.Ordinal)),
            Is.True,
            $"{targetMember}: converter is not applied to the argument");
    }

    private static void AssertBridged(
        RewriteReport report,
        string sourceMethod,
        int expectedCallsites,
        string bridgeMethod,
        bool requiresCallsiteToken = false)
    {
        var records = report.ManagedBridgeRewrites
            .Where(record => record.SourceMethod.Contains(sourceMethod, StringComparison.Ordinal))
            .Where(record => record.BridgeMethod.Contains(
                $"{typeof(PcCompatManagedImGuiBridge).FullName}::{bridgeMethod}",
                StringComparison.Ordinal))
            .ToArray();
        Assert.That(records, Has.Length.EqualTo(expectedCallsites), $"{sourceMethod} -> {bridgeMethod}");
        if (requiresCallsiteToken)
        {
            Assert.That(
                records.All(record => record.BridgeMethod.Contains(" token=0x", StringComparison.Ordinal)),
                Is.True,
                $"{sourceMethod} must use the tokenized {bridgeMethod} bridge overload");
        }
    }

    private string GetRewrittenPath(string modDirectoryName, string assemblyFileName)
    {
        var modDirectory = Path.Combine(FindRepoRoot(), modDirectoryName);
        if (!PcModManifestReader.TryRead(modDirectory, out var manifest, out var manifestError))
        {
            throw new InvalidOperationException(
                $"Could not resolve rewritten output identity for {modDirectoryName}: {manifestError}");
        }

        var stem = Path.GetFileNameWithoutExtension(assemblyFileName);
        return Path.Combine(_root, $"{manifest.Id}.{stem}.rewritten.dll");
    }

    private static string? BuildSurfaceEntry(IMethod target)
    {
        var signature = target.MethodSig;
        var assemblyName = target.DeclaringType.DefinitionAssembly?.Name?.String;
        if (signature is null || string.IsNullOrWhiteSpace(assemblyName))
            return null;
        return string.Join(
            '|',
            "M",
            assemblyName,
            ProxySurfaceIdentity.NormalizeTypeName(target.DeclaringType.FullName),
            signature.HasThis ? "instance" : "static",
            signature.GenParamCount,
            ProxySurfaceIdentity.NormalizeTypeName(SurfaceTypeIdentity(signature.RetType)),
            target.Name.String,
            string.Join(
                ';',
                signature.Params.Select(parameter =>
                    ProxySurfaceIdentity.NormalizeTypeName(SurfaceTypeIdentity(parameter)))));
    }

    private static string SurfaceTypeIdentity(TypeSig type)
    {
        type = type.RemovePinnedAndModifiers();
        return type switch
        {
            GenericMVar methodVariable => "!!" + methodVariable.Number,
            GenericVar typeVariable => "!" + typeVariable.Number,
            SZArraySig array => SurfaceTypeIdentity(array.Next) + "[]",
            ByRefSig byRef => SurfaceTypeIdentity(byRef.Next) + "&",
            PtrSig pointer => SurfaceTypeIdentity(pointer.Next) + "*",
            GenericInstSig generic =>
                generic.GenericType.TypeDefOrRef.FullName + "<" +
                string.Join(",", generic.GenericArguments.Select(SurfaceTypeIdentity)) + ">",
            _ => type.FullName
        };
    }

    private static Dictionary<string, int> CountByTarget(RewriteReport report)
        => report.MethodIssues
            .GroupBy(issue => issue.Target, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private RewriteReport? RewriteWithProductionSpecs(
        string modDirectoryName,
        string assemblyFileName,
        bool required,
        bool isPrimary = true,
        IReadOnlyList<ManagedRenderComponentSpec>? renderComponentOverride = null)
    {
        var modDirectory = Path.Combine(FindRepoRoot(), modDirectoryName);
        var input = Path.Combine(modDirectory, assemblyFileName);
        if (!File.Exists(input))
        {
            var message = $"MOD payload is absent: {input}";
            if (required)
                Assert.Fail(message);
            Assert.Ignore(message);
        }
        Assume.That(Directory.Exists(_proxyDirectory), Is.True, $"missing proxies: {_proxyDirectory}");
        Assert.That(
            PcModManifestReader.TryRead(modDirectory, out var manifest, out var manifestError),
            Is.True,
            manifestError);

        var staticScan = PcCompatStaticPatchScanner.Scan(manifest, targetGameRevision: 143);
        return RewriteAssemblyWithProductionSpecs(
            input,
            manifest.Id,
            staticScan,
            isPrimary,
            renderComponentOverride);
    }

    private RewriteReport RewriteAssemblyWithProductionSpecs(
        string input,
        string modId,
        PcCompatStaticPatchScanReport staticScan,
        bool isPrimary,
        IReadOnlyList<ManagedRenderComponentSpec>? renderComponentOverride)
    {
        var stem = Path.GetFileNameWithoutExtension(input);
        var output = Path.Combine(_root, $"{modId}.{stem}.rewritten.dll");
        var reportPath = Path.Combine(_root, $"{modId}.{stem}.report.json");

        // Production spec factories, reached through InternalsVisibleTo. Rebuilding them here would
        // make the audit measure a copy instead of the shipping configuration.
        var bridgeRewrites = InvokeInternal<IReadOnlyList<ManagedBridgeRewriteSpec>>(
            "BuildManagedBridgeRewrites",
            [staticScan, input, isPrimary, modId]);
        var callBridgeRewrites = InvokeInternal<IReadOnlyList<ManagedCallBridgeRewriteSpec>>(
            "BuildManagedCallBridgeRewrites",
            [modId]);
        var fieldConstantRewrites = InvokeInternal<IReadOnlyList<ManagedFieldConstantRewriteSpec>>(
            "BuildManagedFieldConstantRewrites",
            []);
        var writableCollections = InvokeInternal<IReadOnlyList<ManagedWritableCollectionSpec>>(
            "BuildManagedWritableCollections",
            []);
        var renderComponents = InvokeInternal<IReadOnlyList<ManagedRenderComponentSpec>>(
            "BuildManagedRenderComponents",
            [staticScan]);

        return ModAssemblyRewriteApi.Rewrite(
            input,
            output,
            _proxyDirectory,
            reportPath,
            managedBridgeAssemblyPath: _bridgeAssemblyPath,
            managedBridgeRewrites: bridgeRewrites,
            managedCallBridgeRewrites: callBridgeRewrites,
            managedFieldConstantRewrites: fieldConstantRewrites,
            managedProxyCastBridge: new ManagedProxyCastBridgeSpec(
                typeof(PcCompatProxyCastBridge).FullName!,
                nameof(PcCompatProxyCastBridge.IsInstance),
                nameof(PcCompatProxyCastBridge.Cast)),
            managedOwnedAssemblyPaths: [input],
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
            managedWritableCollections: writableCollections,
            managedRenderComponents: renderComponentOverride ?? renderComponents);
    }

    private static T InvokeInternal<T>(string methodName, object?[] arguments)
    {
        var method = typeof(PcCompatAndroidManagedAssemblyRewrite).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                nameof(PcCompatAndroidManagedAssemblyRewrite),
                methodName);
        return (T)method.Invoke(null, arguments)!;
    }

    private static void AssertClean(RewriteReport report, string label)
        => Assert.Multiple(() =>
        {
            Assert.That(report.Issues, Is.Empty, $"{label}: top-level issues");
            Assert.That(report.MethodIssues, Is.Empty, $"{label}: method issues");
            Assert.That(report.ManagedBridgeIssues, Is.Empty, $"{label}: bridge issues");
            Assert.That(report.OutputWritten, Is.True, $"{label}: output not written");
        });

    private static void ReportGaps(RewriteReport report, string label)
    {
        var reasons = report.MethodIssues
            .Select(issue => issue.Reason.Split(';')[0].Trim())
            .Concat(report.ManagedBridgeIssues.Select(issue => issue.Reason.Split(';')[0].Trim()))
            .GroupBy(reason => reason, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .Select(group => $"{group.Count(),5}  {group.Key}");

        TestContext.Out.WriteLine(
            $"{label}: issues={report.Issues.Count} methodIssues={report.MethodIssues.Count} " +
            $"bridgeIssues={report.ManagedBridgeIssues.Count} rewrites={report.Rewrites.Count} " +
            $"outputWritten={report.OutputWritten}");
        foreach (var line in reasons)
            TestContext.Out.WriteLine("  " + line);
        foreach (var issue in report.Issues.Take(40))
            TestContext.Out.WriteLine("  TOP  " + issue);

        // Every unresolved target, deduplicated. The inventory is only actionable if the actual
        // member names are visible, and the per-callsite list is mostly the same handful repeated.
        foreach (var target in report.MethodIssues
                     .Select(issue => issue.Target)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(target => target, StringComparer.Ordinal))
        {
            TestContext.Out.WriteLine("  UNRESOLVED  " + target);
        }
        foreach (var issue in report.ManagedBridgeIssues)
            TestContext.Out.WriteLine("  BRIDGE  " + issue.SourceType + "::" + issue.SourceMethod + " " + issue.Reason);
    }

    private static string FindRepoRoot()
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

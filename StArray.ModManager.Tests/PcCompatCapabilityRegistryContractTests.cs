using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace StArray.ModManager.Tests;

public sealed class PcCompatCapabilityRegistryContractTests
{
    [Test]
    public void AndroidBootstrapExtractsAndPublishesCapabilityRuntimeRoot()
    {
        var root = FindModManagerRoot();
        var bootstrap = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "library",
            "src",
            "main",
            "java",
            "com",
            "fizzd",
            "connectedworlds",
            "editorport",
            "StArrayModManagerBootstrap.java"));

        Assert.Multiple(() =>
        {
            Assert.That(bootstrap, Does.Contain(
                "pc_compat_capabilities/pccompat_capabilities_android"));
            Assert.That(bootstrap, Does.Contain(
                "pc_compat_capabilities/pccompat_capability_whitelist.json"));
            Assert.That(bootstrap, Does.Contain(
                "pc_compat_capabilities/pccompat_capabilities_android.manifest.json"));
            Assert.That(bootstrap, Does.Contain(
                "nativeSetEnv(\"STARRAY_MODMANAGER_RUNTIME_ROOT\", runtime.getAbsolutePath())"));
        });
    }

    [Test]
    public void RegistryInstallsBeforeModScanningAndUsesExistingUnityMainQueue()
    {
        var root = FindModManagerRoot();
        var managed = File.ReadAllText(Path.Combine(root, "StArray.ModManager.Android", "Managed.cs"));
        var registry = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatCapabilityBundleRegistry.cs"));
        var installIndex = managed.IndexOf(
            "PcCompatCapabilityBundleRegistry.Install()",
            StringComparison.Ordinal);
        var modLoaderIndex = managed.IndexOf("new ModLoader(modsPath)", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(installIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(modLoaderIndex, Is.GreaterThan(installIndex));
            Assert.That(registry, Does.Contain(
                "PcCompatResourceBundleLoader.TryScheduleUnityMainWork(RunStepOnUnityMain)"));
            Assert.That(registry, Does.Contain("package.ValidateInternalManifest"));
            Assert.That(registry, Does.Contain("RegisterCapabilityAssetProvider"));
            Assert.That(registry, Does.Contain("Dictionary<string, LoadedCapabilityAsset>"));
            Assert.That(registry, Does.Contain("PcCompatUnityMainExecutionContext.IsActive"));
            Assert.That(registry, Does.Contain("InvalidateLoadedAsset(stableId, loaded)"));
            Assert.That(registry, Does.Not.Contain("Task.Run"));
        });
    }

    [Test]
    public void RegistryRejectsFakeNullCapabilityAssetsBeforeReturningCachedProxy()
    {
        var root = FindModManagerRoot();
        var registry = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatCapabilityBundleRegistry.cs"));
        var bundleApi = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatGeneratedUnityBundleApi.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(bundleApi, Does.Contain("\"op_Implicit\""));
            Assert.That(bundleApi, Does.Contain("public bool IsUnityObjectAlive(object proxy)"));
            Assert.That(registry, Does.Contain("api.IsUnityObjectAlive(loaded.Proxy)"));
            Assert.That(registry, Does.Contain("RemainingAssets.Enqueue(stale.Descriptor)"));
            Assert.That(registry, Does.Contain(
                "s_status = PcCompatCapabilityRegistryStatus.LoadingAssets"));
        });
    }

    [Test]
    public void GeneratedCoreProxyContainsTextAssetTextGetter()
    {
        var root = FindModManagerRoot();
        var assemblyPath = Path.Combine(
            root,
            "xphorror.PcModCompat",
            "out",
            "interop",
            "proxy_assemblies",
            "UnityEngine.CoreModule.dll");
        Assert.That(File.Exists(assemblyPath), Is.True, assemblyPath);

        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var textAsset = metadata.TypeDefinitions
            .Select(handle => (Handle: handle, Definition: metadata.GetTypeDefinition(handle)))
            .Single(item =>
                metadata.GetString(item.Definition.Namespace) == "UnityEngine" &&
                metadata.GetString(item.Definition.Name) == "TextAsset");
        var methods = textAsset.Definition.GetMethods()
            .Select(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name))
            .ToArray();

        Assert.That(methods, Does.Contain("get_text"));
    }

    [Test]
    public void VirtualCapabilityResolverClonesOnlyAssetsMarkedByResourceIr()
    {
        var root = FindModManagerRoot();
        var loader = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatResourceBundleLoader.cs"));
        var resourceApi = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatGeneratedUnityResourceApi.cs"));
        var bundleApi = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatGeneratedUnityBundleApi.cs"));
        var registry = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatCapabilityBundleRegistry.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(loader, Does.Contain("request.Asset.CloneCapabilityAsset"));
            Assert.That(loader, Does.Contain("EnsureResourceApi().Clone(proxy!)"));
            Assert.That(loader, Does.Contain("ReleaseWithSession: true"));
            Assert.That(resourceApi, Does.Contain("RequiredMethod(_objectType, \"Instantiate\", true, _objectType)"));
            Assert.That(resourceApi, Does.Contain("public object Clone(object proxy)"));
            Assert.That(resourceApi, Does.Contain("proxy.GetType().GetConstructor([typeof(IntPtr)])"));
            Assert.That(bundleApi, Does.Contain("public object WrapAsset(object proxy, string expectedType)"));
            Assert.That(registry, Does.Contain("api.WrapAsset(manifestBaseProxy, \"UnityEngine.TextAsset\")"));
            Assert.That(registry, Does.Contain("api.WrapAsset(assetBaseProxy, pending.Descriptor.ExpectedType)"));
        });
    }

    [Test]
    public void AndroidVirtualAssetResolversPreserveRetryablePendingStatus()
    {
        var root = FindModManagerRoot();
        var loader = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatResourceBundleLoader.cs")).ReplaceLineEndings("\n");

        const string pendingCatch = "catch (PcCompatVirtualAssetPendingException exception)";
        const string pendingResult =
            "PcCompatVirtualAssetResolveStatus.Pending,\n                null,\n                exception.Message";
        Assert.Multiple(() =>
        {
            Assert.That(
                CountOccurrences(loader, pendingCatch),
                Is.EqualTo(2),
                "asset materialization and projection must both preserve Pending");
            Assert.That(CountOccurrences(loader, pendingResult), Is.EqualTo(2));
        });
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }
        return count;
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
        throw new DirectoryNotFoundException("StArray.ModManager repository root not found.");
    }
}

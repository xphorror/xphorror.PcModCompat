using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace StArray.ModManager.Tests;

public sealed class NativeModMetadataDescriptorTests
{
    [Test]
    public void AdofaiOnlineModEntryIsProvenWithoutExecutingThePlugin()
    {
        var repositoryRoot = FindRepoRoot();
        var assemblyPath = Path.Combine(
            repositoryRoot,
            "ADOFAIOnlineMod",
            "ADOFAIOnlineMod.dll");
        if (!File.Exists(assemblyPath))
            Assert.Ignore($"MOD payload is absent: {assemblyPath}");

        Assert.That(
            NativeModMetadataDescriptor.TryReadPluginTypeName(
                assemblyPath,
                out var pluginTypeName,
                out var reason),
            Is.True,
            reason);
        Assert.That(
            pluginTypeName,
            Is.EqualTo("ADOFAIOnlineMod.Mobile.OnlinePlugin"));
    }

    [Test]
    public void AdofaiOnlineModOpaqueIdentityUsesNativeDiscoveryFallback()
    {
        var repositoryRoot = FindRepoRoot();
        var sourceDirectory = Path.Combine(repositoryRoot, "ADOFAIOnlineMod");
        var assemblyPath = Path.Combine(
            sourceDirectory,
            "ADOFAIOnlineMod.dll");
        if (!File.Exists(assemblyPath))
            Assert.Ignore($"MOD payload is absent: {assemblyPath}");

        var root = Path.Combine(
            Path.GetTempPath(),
            $"starray-native-metadata-fallback-{Guid.NewGuid():N}");
        var modDirectory = Path.Combine(root, "ADOFAIOnlineMod");
        Directory.CreateDirectory(modDirectory);
        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
            File.Copy(file, Path.Combine(modDirectory, Path.GetFileName(file)));

        var loader = new ModLoader(root);
        try
        {
            loader.ScanMods();
            var withInfo = loader.Mods.Single();
            Assert.Multiple(() =>
            {
                Assert.That(withInfo.LoaderKind, Is.EqualTo(ModEntry.NativeLoaderKind));
                Assert.That(withInfo.Id, Is.EqualTo("ADOFAIOnlineMod"));
                Assert.That(withInfo.Name, Is.EqualTo("ADOFAI Online Mobile"));
                Assert.That(withInfo.PluginInstance, Is.Null,
                    "Opaque identity fallback must not construct the plugin during scan");
                Assert.That(withInfo.LoaderData, Is.TypeOf<NativeModLoadState>());
            });

            File.Delete(Path.Combine(modDirectory, "Info.json"));
            loader.ScanMods();
            var withoutInfo = loader.Mods.Single();
            Assert.Multiple(() =>
            {
                Assert.That(withoutInfo.LoaderKind, Is.EqualTo(ModEntry.NativeLoaderKind));
                Assert.That(withoutInfo.Id, Is.EqualTo("ADOFAIOnlineMod"));
                Assert.That(withoutInfo.PluginInstance, Is.Null,
                    "Native discovery must not depend on Info.json");
            });
        }
        finally
        {
            foreach (var mod in loader.Mods.ToArray())
            {
                if (mod.LoadState is ModLoadState.Loading or ModLoadState.Loaded)
                    loader.UnloadMod(mod);
            }
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void DeclaredNestedEntryIsReadWithoutConstructingPlugin()
    {
        Assert.That(NativeModMetadataDescriptor.TryRead(
            typeof(NativeModIsolationTests).Assembly.Location,
            out var descriptor,
            out var reason), Is.True, reason);

        Assert.Multiple(() =>
        {
            Assert.That(descriptor, Is.Not.Null);
            Assert.That(descriptor!.PluginTypeName,
                Is.EqualTo(typeof(NativeModIsolationTests.NativeStaticProbe).FullName));
            Assert.That(descriptor.Id, Is.EqualTo("native-probe"));
            Assert.That(descriptor.Name, Is.EqualTo("native-probe"));
            Assert.That(descriptor.Author, Is.EqualTo("test"));
            Assert.That(descriptor.Dependencies, Is.Empty);
            Assert.That(descriptor.Version, Is.Not.Empty);
        });
    }

    [Test]
    public void ProvenNativeTypeCanUseHostMetadataFallbackWithoutExecutingIdentityGetters()
    {
        var descriptor = NativeModMetadataDescriptor.CreateDiscoveryFallback(
            "Native.Plugin",
            "native-folder-id",
            "Host display name",
            "1.2.3",
            "Host author");

        Assert.Multiple(() =>
        {
            Assert.That(descriptor.PluginTypeName, Is.EqualTo("Native.Plugin"));
            Assert.That(descriptor.Id, Is.EqualTo("native-folder-id"));
            Assert.That(descriptor.Name, Is.EqualTo("Host display name"));
            Assert.That(descriptor.Version, Is.EqualTo("1.2.3"));
            Assert.That(descriptor.Author, Is.EqualTo("Host author"));
            Assert.That(descriptor.Description, Is.Empty);
            Assert.That(descriptor.Dependencies, Is.Empty);
        });
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

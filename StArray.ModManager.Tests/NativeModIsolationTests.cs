using System.Reflection;
using System.Net.Http;
using System.Runtime.Loader;
using StArray.ModManager.Behaviours;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;
using Xphorror.PcModCompat;

[assembly: ModEntryPoint(typeof(StArray.ModManager.Tests.NativeModIsolationTests.NativeStaticProbe))]

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class NativeModIsolationTests
{
    [Test]
    public void ProcessLifetimeHookControlUsesExactRuntimeGeneration()
    {
        var previous = HookHelper.Instance;
        var provider = new GenerationScopedHookProbe();
        HookHelper.Instance = provider;
        var id = $"generation-hook-{Guid.NewGuid():N}";
        var session = new ModRuntimeSession();
        var first = session.BeginLoad("StArray.Android.Native", id);
        Assert.That(session.TryPublishActive(first), Is.True);

        try
        {
            Assert.That(HookHelper.RegisterProcessLifetimeHookOwner(first.OwnerId), Is.True);
            provider.SetRetained(first.OwnerId, first.Generation, 1);

            Assert.Multiple(() =>
            {
                Assert.That(HookHelper.HasProcessLifetimeHooks(first), Is.True);
                Assert.That(HookHelper.SuspendProcessLifetimeHooks(first), Is.True);
                Assert.That(provider.LastOwner, Is.EqualTo(first.OwnerId));
                Assert.That(provider.LastGeneration, Is.EqualTo(first.Generation));
                Assert.That(provider.LastEnabled, Is.False);
            });

            var secondOwner = first.OwnerId;
            provider.SetRetained(secondOwner, first.Generation + 1, 1);
            Assert.That(HookHelper.ResumeProcessLifetimeHooks(first), Is.True);
            Assert.That(provider.Enabled[(secondOwner, first.Generation + 1)], Is.True,
                "Controlling one generation must not mutate a later generation.");
        }
        finally
        {
            provider.ClearRetained();
            HookHelper.RollbackProcessLifetimeHookOwner(first.OwnerId, true);
            HookHelper.Instance = previous;
        }
    }

    [Test]
    public void NativeModContextsSeparateStaticStateButShareHostContracts()
    {
        var entryPath = typeof(NativeModIsolationTests).Assembly.Location;
        var contextA = new NativeModAssemblyLoadContext("native-a", entryPath);
        var contextB = new NativeModAssemblyLoadContext("native-b", entryPath);
        try
        {
            var assemblyA = contextA.LoadFromAssemblyPath(entryPath);
            var assemblyB = contextB.LoadFromAssemblyPath(entryPath);
            var probeName = typeof(NativeStaticProbe).FullName!;
            var typeA = assemblyA.GetType(probeName, throwOnError: true)!;
            var typeB = assemblyB.GetType(probeName, throwOnError: true)!;

            var valueA = typeA.GetField(nameof(NativeStaticProbe.Value),
                BindingFlags.Public | BindingFlags.Static)!;
            var valueB = typeB.GetField(nameof(NativeStaticProbe.Value),
                BindingFlags.Public | BindingFlags.Static)!;
            valueA.SetValue(null, 17);
            valueB.SetValue(null, 29);

            Assert.Multiple(() =>
            {
                Assert.That(AssemblyLoadContext.GetLoadContext(assemblyA), Is.SameAs(contextA));
                Assert.That(AssemblyLoadContext.GetLoadContext(assemblyB), Is.SameAs(contextB));
                Assert.That(typeA, Is.Not.SameAs(typeB));
                Assert.That(valueA.GetValue(null), Is.EqualTo(17));
                Assert.That(valueB.GetValue(null), Is.EqualTo(29));
                Assert.That(typeof(IModPlugin).IsAssignableFrom(typeA), Is.True);
                Assert.That(typeof(IModPlugin).IsAssignableFrom(typeB), Is.True);
            });
        }
        finally
        {
            contextA.Unload();
            contextB.Unload();
        }
    }

    [Test]
    public void ReleasedNativeStateReloadsIntoAFreshCollectibleContext()
    {
        var entryPath = typeof(NativeModIsolationTests).Assembly.Location;
        var firstContext = new NativeModAssemblyLoadContext("native-reload-a", entryPath);
        var firstAssembly = firstContext.LoadFromAssemblyPath(entryPath);
        var pluginType = firstAssembly.GetType(
            typeof(NativeStaticProbe).FullName!, throwOnError: true)!;
        var firstPlugin = (IModPlugin)Activator.CreateInstance(pluginType)!;
        var state = new NativeModLoadState(
            entryPath,
            firstContext,
            firstAssembly,
            firstPlugin);

        try
        {
            state.ReleaseContext();
            var reloadedPlugin = state.EnsureLoaded();
            var reloadedAssembly = reloadedPlugin.GetType().Assembly;

            Assert.Multiple(() =>
            {
                Assert.That(reloadedPlugin, Is.Not.SameAs(firstPlugin));
                Assert.That(
                    AssemblyLoadContext.GetLoadContext(reloadedAssembly),
                    Is.Not.SameAs(firstContext));
                Assert.That(
                    AssemblyLoadContext.GetLoadContext(reloadedAssembly),
                    Is.TypeOf<NativeModAssemblyLoadContext>());
                Assert.That(state.Plugin, Is.SameAs(reloadedPlugin));
            });
        }
        finally
        {
            state.ReleaseContext();
            firstContext.Unload();
        }
    }

    [Test]
    public void NativeStateRejectsAssemblyChangedAfterBootstrapBinding()
    {
        var entryPath = typeof(NativeModIsolationTests).Assembly.Location;
        var context = new NativeModAssemblyLoadContext("native-bootstrap", entryPath);
        var assembly = context.LoadFromAssemblyPath(entryPath);
        var pluginType = assembly.GetType(
            typeof(NativeStaticProbe).FullName!, throwOnError: true)!;
        var plugin = (IModPlugin)Activator.CreateInstance(pluginType)!;
        var state = new NativeModLoadState(entryPath, context, assembly, plugin);
        var manifest = ModIsolationManifestFactory.CreateBootstrap(
            plugin.Id,
            ModEntry.NativeLoaderKind,
            entryPath);
        var changed = manifest with
        {
            OriginalAssembly = manifest.OriginalAssembly with
            {
                Sha256 = new string('f', 64)
            }
        };

        try
        {
            Assert.Throws<InvalidDataException>(() => state.EnsureLoaded(changed));
        }
        finally
        {
            state.ReleaseContext();
            context.Unload();
        }
    }

    [Test]
    public void ModLoaderReloadsNativeModInFreshIsolatedContext()
    {
        var root = Path.Combine(Path.GetTempPath(), $"starray-native-reload-{Guid.NewGuid():N}");
        var modFolder = Path.Combine(root, "NativeFixture");
        Directory.CreateDirectory(modFolder);
        File.Copy(
            typeof(NativeModIsolationTests).Assembly.Location,
            Path.Combine(modFolder, "NativeFixture.dll"));
        try
        {
            RunNativeReloadScenario(root);
        }
        finally
        {
            CollectUnloadedContexts();
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void SourceChangedAfterScanRejectsLoadAndReleasesShadowContext()
    {
        var root = Path.Combine(Path.GetTempPath(), $"starray-native-race-{Guid.NewGuid():N}");
        var modFolder = Path.Combine(root, "NativeFixture");
        Directory.CreateDirectory(modFolder);
        var entryPath = Path.Combine(modFolder, "NativeFixture.dll");
        File.Copy(typeof(NativeModIsolationTests).Assembly.Location, entryPath);

        try
        {
            RunSourceChangedAfterScanScenario(root, entryPath);
        }
        finally
        {
            CollectUnloadedContexts();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void NativeContextLoadsPrivateDependencyFromModDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"starray-native-dependency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var entryPath = Path.Combine(root, "NativeFixture.dll");
        File.Copy(typeof(NativeModIsolationTests).Assembly.Location, entryPath);
        var dependencySource = typeof(TestAttribute).Assembly.Location;
        var dependencyPath = Path.Combine(root, Path.GetFileName(dependencySource));
        File.Copy(dependencySource, dependencyPath);

        try
        {
            RunPrivateDependencyScenario(entryPath, dependencyPath);
        }
        finally
        {
            CollectUnloadedContexts();
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void NativeContextAlwaysSharesFrameworkNetworkingAssemblies()
    {
        var root = Path.Combine(Path.GetTempPath(), $"starray-native-network-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var entryPath = Path.Combine(root, "NativeFixture.dll");
        File.Copy(typeof(NativeModIsolationTests).Assembly.Location, entryPath);
        File.Copy(
            typeof(HttpClient).Assembly.Location,
            Path.Combine(root, "System.Net.Http.dll"));

        var context = new NativeModAssemblyLoadContext("native-network", entryPath);
        try
        {
            var resolved = context.LoadFromAssemblyName(typeof(HttpClient).Assembly.GetName());

            Assert.Multiple(() =>
            {
                Assert.That(resolved, Is.SameAs(typeof(HttpClient).Assembly));
                Assert.That(
                    AssemblyLoadContext.GetLoadContext(resolved),
                    Is.SameAs(AssemblyLoadContext.Default));
            });
        }
        finally
        {
            context.Unload();
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void RescanKeepsNativeRuntimeStateWhenInfoJsonLooksLikePcCompat()
    {
        var root = Path.Combine(Path.GetTempPath(), $"starray-cross-loader-{Guid.NewGuid():N}");
        var modFolder = Path.Combine(root, "NativeFixture");
        Directory.CreateDirectory(modFolder);
        File.Copy(
            typeof(NativeModIsolationTests).Assembly.Location,
            Path.Combine(modFolder, "NativeFixture.dll"));
        try
        {
            RunCrossLoaderRescanScenario(root, modFolder);
        }
        finally
        {
            CollectUnloadedContexts();
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void NativePluginTakesPrecedenceOverPcCompatInfoJsonOnInitialScan()
    {
        var root = Path.Combine(Path.GetTempPath(), $"starray-native-info-{Guid.NewGuid():N}");
        var modFolder = Path.Combine(root, "NativeFixture");
        Directory.CreateDirectory(modFolder);
        File.Copy(
            typeof(NativeModIsolationTests).Assembly.Location,
            Path.Combine(modFolder, "NativeFixture.dll"));
        File.WriteAllText(
            Path.Combine(modFolder, "Info.json"),
            "{\"Id\":\"native-probe\",\"DisplayName\":\"PC metadata\"," +
            "\"AssemblyName\":\"NativeFixture.dll\",\"EntryMethod\":\"\"}");

        var loader = new ModLoader(root);
        try
        {
            loader.ScanMods();
            var mod = loader.Mods.Single();

            Assert.Multiple(() =>
            {
                Assert.That(mod.LoaderKind, Is.EqualTo(ModEntry.NativeLoaderKind));
                Assert.That(mod.LoaderData, Is.TypeOf<NativeModLoadState>());
                Assert.That(mod.PluginInstance, Is.Null,
                    "metadata-only native discovery must not construct the plugin during scan");
            });

            File.Delete(Path.Combine(modFolder, "Info.json"));
            loader.ScanMods();
            var withoutInfo = loader.Mods.Single();
            Assert.Multiple(() =>
            {
                Assert.That(withoutInfo.LoaderKind, Is.EqualTo(ModEntry.NativeLoaderKind));
                Assert.That(withoutInfo.LoaderData, Is.TypeOf<NativeModLoadState>());
                Assert.That(withoutInfo.PluginInstance, Is.Null,
                    "removing PC metadata must not remove the native MOD entry");
            });
        }
        finally
        {
            foreach (var mod in loader.Mods.ToArray())
            {
                if (mod.LoadState is ModLoadState.Loading or ModLoadState.Loaded)
                    loader.UnloadMod(mod);
            }
            CollectUnloadedContexts();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void NativeAndPcCompatModsUseDifferentHookOwnerNamespaces()
    {
        var id = $"same-id-{Guid.NewGuid():N}";
        var native = new ModEntry
        {
            Id = id,
            LoaderKind = ModEntry.NativeLoaderKind
        };
        var pcCompat = new ModEntry
        {
            Id = id,
            LoaderKind = PcCompatRuntime.LoaderKind
        };

        Assert.Multiple(() =>
        {
            Assert.That(native.RuntimeOwnerId, Is.EqualTo($"native:{id}"));
            Assert.That(pcCompat.RuntimeOwnerId, Is.EqualTo($"pccompat:{id}"));
            Assert.That(native.RuntimeOwnerId, Is.Not.EqualTo(pcCompat.RuntimeOwnerId));
        });
    }

    [Test]
    public void OwnedBehavioursSuspendResumeAndRetireWithoutAffectingOtherMods()
    {
        var ownerA = $"native:behaviour-a-{Guid.NewGuid():N}";
        var ownerB = $"native:behaviour-b-{Guid.NewGuid():N}";
        var behaviourA = new OwnedBehaviour();
        var behaviourB = new OwnedBehaviour();

        try
        {
            using (HookHelper.EnterOwnerScope(ownerA))
                BehaviourManager.Add(behaviourA);
            using (HookHelper.EnterOwnerScope(ownerB))
                BehaviourManager.Add(behaviourB);

            BehaviourManager.ProcessPending();
            BehaviourManager.Update(1f / 60f);
            BehaviourManager.SuspendOwner(ownerA);
            BehaviourManager.Update(1f / 60f);

            Assert.Multiple(() =>
            {
                Assert.That(behaviourA.UpdateCount, Is.EqualTo(1));
                Assert.That(behaviourA.Enabled, Is.False);
                Assert.That(behaviourB.UpdateCount, Is.EqualTo(2));
                Assert.That(behaviourB.Enabled, Is.True);
            });

            BehaviourManager.ResumeOwner(ownerA);
            BehaviourManager.Update(1f / 60f);
            BehaviourManager.RetireOwner(ownerA);
            BehaviourManager.Update(1f / 60f);

            Assert.Multiple(() =>
            {
                Assert.That(behaviourA.UpdateCount, Is.EqualTo(2));
                Assert.That(behaviourA.StopCount, Is.EqualTo(1));
                Assert.That(behaviourA.IsDestroyed, Is.True);
                Assert.That(behaviourB.UpdateCount, Is.EqualTo(4));
                Assert.That(behaviourB.IsDestroyed, Is.False);
            });
        }
        finally
        {
            BehaviourManager.RetireOwner(ownerA);
            BehaviourManager.RetireOwner(ownerB);
        }
    }

    [Test]
    public void ModLoaderRejectsDuplicateIdsAcrossLoaderKinds()
    {
        var root = Path.Combine(Path.GetTempPath(), $"starray-duplicate-id-{Guid.NewGuid():N}");
        var loader = new ModLoader(root);
        try
        {
            loader.AddMod(new ModEntry
            {
                Id = "shared-id",
                Name = "native",
                FolderPath = root
            });

            Assert.That(
                () => loader.AddMod(new ModEntry
                {
                    Id = "SHARED-ID",
                    Name = "pccompat",
                    FolderPath = root,
                    LoaderData = new PcModManifest
                    {
                        Id = "SHARED-ID",
                        DisplayName = "pccompat",
                        FolderPath = root
                    }
                }),
                Throws.TypeOf<InvalidOperationException>());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    public sealed class NativeStaticProbe : IModPlugin
    {
        public static int Value;
        public int LoadCount { get; private set; }
        public int UnloadCount { get; private set; }
        public string Id => GetId();
        public string Name => GetName();
        public string Version => "1.0";
        public string Author => GetAuthor();
        public string Description => GetDescription();
        public IReadOnlyList<string> Dependencies => Array.Empty<string>();
        public void OnLoad() => LoadCount++;
        public void OnUnload() => UnloadCount++;

        private static string GetId() => "native-probe";
        private static string GetName() => "native-probe";
        private static string GetAuthor() => "test";
        private static string GetDescription() => "test";
    }

    private static int ReadCounter(IModPlugin plugin, string propertyName)
        => (int)plugin.GetType().GetProperty(propertyName)!.GetValue(plugin)!;

    private static void RunSourceChangedAfterScanScenario(string root, string entryPath)
    {
        var loader = new ModLoader(root);
        loader.ScanMods();
        var mod = loader.Mods.Single();
        Assert.That(mod.PluginInstance, Is.Null,
            "Source identity must be checked before metadata-only discovery constructs the plugin.");
        var state = (NativeModLoadState)mod.LoaderData!;
        using (var stream = new FileStream(entryPath, FileMode.Append, FileAccess.Write))
            stream.WriteByte(0x7f);

        Assert.That(loader.LoadMod(mod), Is.False);
        var snapshot = mod.RuntimeSession.Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(state.Plugin, Is.Null);
            Assert.That(state.Assembly, Is.Null);
            Assert.That(mod.PluginInstance, Is.Null);
            Assert.That(snapshot.State, Is.EqualTo(ModRuntimeLifecycleState.Faulted));
            Assert.That(mod.RuntimeSession.DomainToken.IsValid, Is.False);
        });
        state.ReleaseContext();
    }

    private static void RunNativeReloadScenario(string root)
    {
        var loader = new ModLoader(root);
        loader.ScanMods();
        var mod = loader.Mods.Single();
        Assert.That(mod.PluginInstance, Is.Null,
            "Metadata-only discovery must not construct the plugin before BeginLoad/domain scope.");

        try
        {
            Assert.That(loader.LoadMod(mod), Is.True);
            var firstPlugin = mod.PluginInstance!;
            var firstContext = AssemblyLoadContext.GetLoadContext(firstPlugin.GetType().Assembly);
            Assert.Multiple(() =>
            {
                Assert.That(mod.PluginInstance, Is.SameAs(firstPlugin));
                Assert.That(firstContext, Is.TypeOf<NativeModAssemblyLoadContext>());
                Assert.That(firstPlugin.GetType().Assembly.Location,
                    Does.Contain(Path.Combine(root, ".starray-shadow")));
                Assert.That(ReadCounter(firstPlugin, nameof(NativeStaticProbe.LoadCount)), Is.EqualTo(1));
                Assert.That(mod.LoadGeneration, Is.EqualTo(1));
                Assert.That(
                    mod.RuntimeSession.SnapshotIsolationManifest().Manifest?.ShadowAssembly,
                    Is.Not.Null);
            });

            loader.UnloadMod(mod);
            Assert.Multiple(() =>
            {
                Assert.That(mod.PluginInstance, Is.Null);
                Assert.That(mod.LoaderData, Is.TypeOf<NativeModLoadState>());
                Assert.That(ReadCounter(firstPlugin, nameof(NativeStaticProbe.UnloadCount)), Is.EqualTo(1));
            });

            Assert.That(loader.LoadMod(mod), Is.True);
            var secondPlugin = mod.PluginInstance!;
            var secondContext = AssemblyLoadContext.GetLoadContext(secondPlugin.GetType().Assembly);
            Assert.Multiple(() =>
            {
                Assert.That(secondPlugin, Is.Not.SameAs(firstPlugin));
                Assert.That(secondContext, Is.TypeOf<NativeModAssemblyLoadContext>());
                Assert.That(secondContext, Is.Not.SameAs(firstContext));
                Assert.That(secondPlugin.GetType().Assembly.Location,
                    Does.Contain(Path.Combine(root, ".starray-shadow")));
                Assert.That(ReadCounter(secondPlugin, nameof(NativeStaticProbe.LoadCount)), Is.EqualTo(1));
                Assert.That(mod.LoadGeneration, Is.EqualTo(2));
            });
        }
        finally
        {
            if (mod.LoadState is ModLoadState.Loading or ModLoadState.Loaded)
                loader.UnloadMod(mod);
        }
    }

    private static void RunCrossLoaderRescanScenario(string root, string modFolder)
    {
        var loader = new ModLoader(root);
        loader.ScanMods();
        var native = loader.Mods.Single();
        Assert.That(loader.LoadMod(native), Is.True);
        var nativePlugin = native.PluginInstance!;
        ModEntry active = native;
        try
        {
            File.WriteAllText(
                Path.Combine(modFolder, "Info.json"),
                "{\"Id\":\"native-probe\",\"DisplayName\":\"PC fixture\"," +
                "\"AssemblyName\":\"Missing.dll\",\"EntryMethod\":\"\"}");
            loader.ScanMods();
            var rediscovered = loader.Mods.Single();
            active = rediscovered;

            Assert.Multiple(() =>
            {
                Assert.That(rediscovered.LoaderKind, Is.EqualTo(ModEntry.NativeLoaderKind));
                Assert.That(rediscovered.LoaderData, Is.TypeOf<NativeModLoadState>());
                Assert.That(rediscovered.PluginInstance, Is.SameAs(nativePlugin));
                Assert.That(rediscovered.LoadState, Is.EqualTo(ModLoadState.Loaded));
                Assert.That(ReadCounter(nativePlugin, nameof(NativeStaticProbe.UnloadCount)), Is.Zero);
            });

            // The new classification deliberately retains the live native context across a
            // rescan. Release it before the test removes the temporary directory on Windows.
        }
        finally
        {
            if (active.LoadState is ModLoadState.Loading or ModLoadState.Loaded)
                loader.UnloadMod(active);
        }
    }

    private static void RunPrivateDependencyScenario(string entryPath, string dependencyPath)
    {
        var context = new NativeModAssemblyLoadContext("native-private-dependency", entryPath);
        try
        {
            var dependency = context.LoadFromAssemblyName(typeof(TestAttribute).Assembly.GetName());
            Assert.Multiple(() =>
            {
                Assert.That(
                    Path.GetFullPath(dependency.Location),
                    Is.EqualTo(Path.GetFullPath(dependencyPath)));
                Assert.That(AssemblyLoadContext.GetLoadContext(dependency), Is.SameAs(context));
                Assert.That(dependency, Is.Not.SameAs(typeof(TestAttribute).Assembly));
            });
        }
        finally
        {
            context.Unload();
        }
    }

    private static void CollectUnloadedContexts()
    {
        for (var attempt = 0; attempt < 3; ++attempt)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private sealed class OwnedBehaviour : GameBehaviour
    {
        public int UpdateCount { get; private set; }
        public int StopCount { get; private set; }

        public override void OnUpdate(float delta) => UpdateCount++;
        public override void OnStop() => StopCount++;
    }

    private sealed class GenerationScopedHookProbe : IGenerationScopedHook
    {
        private readonly Dictionary<(string Owner, long Generation), int> _retained = new();
        public Dictionary<(string Owner, long Generation), bool> Enabled { get; } = new();
        public string? LastOwner { get; private set; }
        public long LastGeneration { get; private set; }
        public bool LastEnabled { get; private set; }
        public bool SupportsOwnerControl => true;
        public nint Hook(nint target, nint detour) => nint.Zero;
        public bool Unhook(nint target) => false;
        public nint GetFunction(string library, string name) => nint.Zero;
        public bool SetOwnerEnabled(string owner, bool enabled) => false;
        public bool RetireOwnerTarget(string owner, nint target) => false;
        public int RetireOwner(string owner) => 0;
        public int GetRetainedLayerCount(string owner) =>
            _retained.Where(pair => pair.Key.Owner == owner).Sum(pair => pair.Value);

        public bool SetOwnerGenerationEnabled(string owner, long generation, bool enabled)
        {
            LastOwner = owner;
            LastGeneration = generation;
            LastEnabled = enabled;
            Enabled[(owner, generation)] = enabled;
            return true;
        }

        public bool RetireOwnerGenerationTarget(string owner, long generation, nint target)
            => false;

        public int RetireOwnerGeneration(string owner, long generation)
            => 0;

        public int GetRetainedLayerCount(string owner, long generation)
            => _retained.GetValueOrDefault((owner, generation));

        public void SetRetained(string owner, long generation, int count)
        {
            _retained[(owner, generation)] = count;
            Enabled[(owner, generation)] = true;
        }

        public void ClearRetained() => _retained.Clear();
    }
}

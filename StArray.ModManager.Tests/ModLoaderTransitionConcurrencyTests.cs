using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace StArray.ModManager.Tests;

public sealed class ModLoaderTransitionConcurrencyTests
{
    [Test]
    [NonParallelizable]
    public void UnloadWaitsForInFlightAsyncFinalization()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "modloader-transition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var finalizationEntered = new ManualResetEventSlim();
        using var releaseFinalization = new ManualResetEventSlim();
        using var unloadStarted = new ManualResetEventSlim();
        var plugin = new BlockingAsyncPlugin(finalizationEntered, releaseFinalization);
        var mod = new ModEntry
        {
            Id = plugin.Id,
            Name = plugin.Name,
            FolderPath = root,
            PluginInstance = plugin,
            IsEnabled = true,
            LoadState = ModLoadState.Loading,
        };
        var loader = new ModLoader(root);
        try
        {
            loader.AddMod(mod);
            var completion = Task.Run(loader.UpdatePendingLoads);
            Assert.That(finalizationEntered.Wait(TimeSpan.FromSeconds(2)), Is.True);

            var unload = Task.Run(() =>
            {
                unloadStarted.Set();
                loader.UnloadMod(mod);
            });
            Assert.That(unloadStarted.Wait(TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(unload.Wait(TimeSpan.FromMilliseconds(100)), Is.False);

            releaseFinalization.Set();
            Assert.That(Task.WaitAll([completion, unload], TimeSpan.FromSeconds(2)), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(plugin.UnloadOverlappedFinalization, Is.False);
                Assert.That(plugin.UnloadCalls, Is.EqualTo(1));
                Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.NotLoaded));
                Assert.That(mod.IsEnabled, Is.False);
            });
        }
        finally
        {
            releaseFinalization.Set();
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [NonParallelizable]
    public void LoadReconcilesAnAlreadyActiveRuntimeInsteadOfStartingAnotherGeneration()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "modloader-active-reconcile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var plugin = new PassivePlugin("active-reconcile");
        var mod = new ModEntry
        {
            Id = plugin.Id,
            Name = plugin.Name,
            FolderPath = root,
            PluginInstance = plugin,
            LoaderKind = "test-loader",
            LoadState = ModLoadState.Error,
            RuntimeSession = new ModRuntimeSession()
        };
        var activeKey = mod.RuntimeSession.EnsureActive(mod.LoaderKind, mod.Id);
        var loader = new ModLoader(root);
        try
        {
            loader.AddMod(mod);

            Assert.That(loader.LoadMod(mod), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.Loaded));
                Assert.That(mod.IsEnabled, Is.True);
                Assert.That(mod.RuntimeKey, Is.EqualTo(activeKey));
                Assert.That(mod.RuntimeSession.Snapshot().State,
                    Is.EqualTo(ModRuntimeLifecycleState.Active));
                Assert.That(plugin.OnLoadCalls, Is.Zero,
                    "reconciliation must not invoke the plugin a second time");
            });

            loader.UnloadMod(mod);
        }
        finally
        {
            if (mod.RuntimeSession.Snapshot().State == ModRuntimeLifecycleState.Active)
            {
                var key = mod.RuntimeSession.CurrentKey;
                mod.RuntimeSession.TryBeginRetirement(key);
                mod.RuntimeSession.WaitForQuiescence(key, TimeSpan.Zero);
                mod.RuntimeSession.TryCompleteRetirement(key);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [NonParallelizable]
    public void StateObserverFailureCannotTurnPublishedLoadIntoSecondLoadAttempt()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "modloader-state-observer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var plugin = new PassivePlugin("state-observer");
        var mod = new ModEntry
        {
            Id = plugin.Id,
            Name = plugin.Name,
            FolderPath = root,
            PluginInstance = plugin,
            LoaderKind = "test-loader",
            LoadState = ModLoadState.NotLoaded,
            RuntimeSession = new ModRuntimeSession()
        };
        var loader = new ModLoader(root);
        loader.OnModStateChanged += changed =>
        {
            if (changed.LoadState == ModLoadState.Loaded)
                throw new InvalidOperationException("observer failure");
        };
        try
        {
            Assert.That(loader.AddMod(mod), Is.SameAs(mod));
            Assert.That(loader.LoadMod(mod), Is.True);
            Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.Loaded));
            Assert.That(mod.RuntimeSession.Snapshot().State,
                Is.EqualTo(ModRuntimeLifecycleState.Active));
            loader.UnloadMod(mod);
        }
        finally
        {
            if (mod.RuntimeSession.Snapshot().State == ModRuntimeLifecycleState.Active)
            {
                var key = mod.RuntimeSession.CurrentKey;
                mod.RuntimeSession.TryBeginRetirement(key);
                mod.RuntimeSession.WaitForQuiescence(key, TimeSpan.Zero);
                mod.RuntimeSession.TryCompleteRetirement(key);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class PassivePlugin(string id) : IModPlugin
    {
        public string Id { get; } = id;
        public string Name => Id;
        public string Version => "1.0.0";
        public string Author => "tests";
        public string Description => string.Empty;
        public IReadOnlyList<string> Dependencies => Array.Empty<string>();
        public int OnLoadCalls { get; private set; }

        public void OnLoad() => OnLoadCalls++;
        public void OnUnload() { }
    }

    private sealed class BlockingAsyncPlugin(
        ManualResetEventSlim finalizationEntered,
        ManualResetEventSlim releaseFinalization) : IModPlugin, IAsyncModPlugin
    {
        private int _finalizing;
        private int _unloadCalls;
        private int _unloadOverlappedFinalization;

        public string Id => "transition-test";
        public string Name => "Transition Test";
        public string Version => "1.0.0";
        public string Author => "tests";
        public string Description => string.Empty;
        public IReadOnlyList<string> Dependencies => Array.Empty<string>();
        public bool IsLoadReady => true;
        public int UnloadCalls => Volatile.Read(ref _unloadCalls);
        public bool UnloadOverlappedFinalization =>
            Volatile.Read(ref _unloadOverlappedFinalization) != 0;

        public void BeginLoad()
        {
        }

        public ModLoadProgress GetLoadProgress() => new(1, "Ready");

        public void CompleteLoad()
        {
            Volatile.Write(ref _finalizing, 1);
            finalizationEntered.Set();
            releaseFinalization.Wait(TimeSpan.FromSeconds(5));
            Volatile.Write(ref _finalizing, 0);
        }

        public void CancelLoad()
        {
        }

        public void OnLoad()
        {
        }

        public void OnUnload()
        {
            if (Volatile.Read(ref _finalizing) != 0)
                Volatile.Write(ref _unloadOverlappedFinalization, 1);
            Interlocked.Increment(ref _unloadCalls);
        }
    }
}

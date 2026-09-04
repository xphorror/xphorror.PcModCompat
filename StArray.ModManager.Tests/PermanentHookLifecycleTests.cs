using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;
using StArray.ModManager.RuntimeAbstractions;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class PermanentHookLifecycleTests
{
    [Test]
    public void PcCompatPluginDeclaresLogicalProcessLifetimeHookRetirement()
    {
        Assert.That(
            typeof(ILogicalProcessLifetimeHookRetirement).IsAssignableFrom(
                typeof(Xphorror.PcModCompat.PcCompatModPlugin)),
            Is.True);
    }

    [Test]
    public void UnloadSuspendsPluginWithProcessLifetimeHooksWithoutDroppingDelegateRoots()
    {
        var previousHook = HookHelper.Instance;
        var modId = $"permanent-hook-{Guid.NewGuid():N}";
        var modsRoot = Path.Combine(Path.GetTempPath(), modId);
        var plugin = new HookedPlugin(modId);
        try
        {
            HookHelper.Instance = new ProcessLifetimeHook();
            using (HookHelper.EnterOwnerScope(modId))
                Assert.That(HookHelper.Hook((nint)0x1000, (nint)0x2000), Is.EqualTo((nint)0x3000));

            var loader = new ModLoader(modsRoot);
            var mod = new ModEntry
            {
                Id = modId,
                Name = "Permanent Hook",
                IsEnabled = true,
                LoadState = ModLoadState.Loaded,
                PluginInstance = plugin
            };
            loader.AddMod(mod);

            loader.UnloadMod(mod);

            Assert.Multiple(() =>
            {
                Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.NotLoaded));
                Assert.That(mod.IsEnabled, Is.False);
                Assert.That(mod.PluginInstance, Is.SameAs(plugin));
                Assert.That(plugin.UnloadCount, Is.Zero);
                Assert.That(HookHelper.HasProcessLifetimeHooks(modId), Is.True);
            });

            Assert.That(loader.LoadMod(mod), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.Loaded));
                Assert.That(mod.IsEnabled, Is.True);
                Assert.That(mod.PluginInstance, Is.SameAs(plugin));
                Assert.That(plugin.LoadCount, Is.Zero);
            });
        }
        finally
        {
            HookHelper.Instance = previousHook;
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void LegacyProcessLifetimeHookProviderReturnsFalseForDirectUnhook()
    {
        var previousHook = HookHelper.Instance;
        try
        {
            HookHelper.Instance = new ProcessLifetimeHook();
            Assert.That(HookHelper.Unhook((nint)0x1000), Is.False);
        }
        finally
        {
            HookHelper.Instance = previousHook;
        }
    }

    [Test]
    public void FailedProcessLifetimeHookInstallRollsBackOwnerReservation()
    {
        var previousHook = HookHelper.Instance;
        var modId = $"failed-hook-{Guid.NewGuid():N}";
        try
        {
            HookHelper.Instance = new FailingProcessLifetimeHook();
            using (HookHelper.EnterOwnerScope(modId))
                Assert.That(
                    HookHelper.Hook((nint)0x1000, (nint)0x2000),
                    Is.EqualTo(nint.Zero));

            Assert.That(HookHelper.HasProcessLifetimeHooks(modId), Is.False);
        }
        finally
        {
            HookHelper.Instance = previousHook;
        }
    }

    [Test]
    public void OwnerScopedUnhookRetiresOnlyTheCurrentOwnerLayer()
    {
        var previousHook = HookHelper.Instance;
        var hook = new OwnerScopedProcessLifetimeHook();
        var ownerA = $"owner-a-{Guid.NewGuid():N}";
        var ownerB = $"owner-b-{Guid.NewGuid():N}";
        var target = (nint)0x1000;
        try
        {
            HookHelper.Instance = hook;
            using (HookHelper.EnterOwnerScope(ownerA))
                Assert.That(HookHelper.Hook(target, (nint)0x2000), Is.EqualTo((nint)0x3000));
            using (HookHelper.EnterOwnerScope(ownerB))
                Assert.That(HookHelper.Hook(target, (nint)0x4000), Is.EqualTo((nint)0x3000));

            using (HookHelper.EnterOwnerScope(ownerA))
                Assert.That(HookHelper.Unhook(target), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(hook.GetRetainedLayerCount(ownerA), Is.Zero);
                Assert.That(hook.GetRetainedLayerCount(ownerB), Is.EqualTo(1));
                Assert.That(HookHelper.HasProcessLifetimeHooks(ownerA), Is.False);
                Assert.That(HookHelper.HasProcessLifetimeHooks(ownerB), Is.True);
            });
        }
        finally
        {
            HookHelper.Instance = previousHook;
        }
    }

    [Test]
    public void OwnerControlledPluginCanRetireLegacyHookDuringUnload()
    {
        var previousHook = HookHelper.Instance;
        var hook = new OwnerScopedProcessLifetimeHook();
        var modId = $"owner-retire-{Guid.NewGuid():N}";
        var modsRoot = Path.Combine(Path.GetTempPath(), modId);
        var target = (nint)0x1000;
        var plugin = new UnhookingPlugin(modId, target);
        try
        {
            HookHelper.Instance = hook;
            using (HookHelper.EnterOwnerScope(modId))
                HookHelper.Hook(target, (nint)0x2000);

            var loader = new ModLoader(modsRoot);
            var mod = new ModEntry
            {
                Id = modId,
                Name = "Owner Retire Hook",
                IsEnabled = true,
                LoadState = ModLoadState.Loaded,
                PluginInstance = plugin
            };
            loader.AddMod(mod);

            loader.UnloadMod(mod);

            Assert.Multiple(() =>
            {
                Assert.That(plugin.UnloadCount, Is.EqualTo(1));
                Assert.That(plugin.UnhookResult, Is.True);
                Assert.That(hook.GetRetainedLayerCount(modId), Is.Zero);
                Assert.That(mod.PluginInstance, Is.Null);
                Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.NotLoaded));
            });
        }
        finally
        {
            HookHelper.Instance = previousHook;
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void SuspendAndResumeAffectOnlyRequestedOwner()
    {
        var previousHook = HookHelper.Instance;
        var hook = new OwnerScopedProcessLifetimeHook();
        var ownerA = $"suspend-a-{Guid.NewGuid():N}";
        var ownerB = $"suspend-b-{Guid.NewGuid():N}";
        try
        {
            HookHelper.Instance = hook;
            using (HookHelper.EnterOwnerScope(ownerA))
                HookHelper.Hook((nint)0x1000, (nint)0x2000);
            using (HookHelper.EnterOwnerScope(ownerB))
                HookHelper.Hook((nint)0x1000, (nint)0x4000);

            Assert.That(HookHelper.SuspendProcessLifetimeHooks(ownerA), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(hook.GetEnabledLayerCount(ownerA), Is.Zero);
                Assert.That(hook.GetEnabledLayerCount(ownerB), Is.EqualTo(1));
            });

            Assert.That(HookHelper.ResumeProcessLifetimeHooks(ownerA), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(hook.GetEnabledLayerCount(ownerA), Is.EqualTo(1));
                Assert.That(hook.GetEnabledLayerCount(ownerB), Is.EqualTo(1));
            });
        }
        finally
        {
            HookHelper.Instance = previousHook;
        }
    }

    [Test]
    public void LogicalRetirementPluginRunsUnloadWhilePhysicalHookRemainsInstalled()
    {
        var previousHook = HookHelper.Instance;
        var modId = $"logical-retire-hook-{Guid.NewGuid():N}";
        var modsRoot = Path.Combine(Path.GetTempPath(), modId);
        var plugin = new LogicallyRetirableHookedPlugin(modId);
        try
        {
            HookHelper.Instance = new ProcessLifetimeHook();
            using (HookHelper.EnterOwnerScope(modId))
                Assert.That(HookHelper.Hook((nint)0x1000, (nint)0x2000), Is.EqualTo((nint)0x3000));

            var loader = new ModLoader(modsRoot);
            var mod = new ModEntry
            {
                Id = modId,
                Name = "Logical Retirement Hook",
                IsEnabled = true,
                LoadState = ModLoadState.Loaded,
                PluginInstance = plugin
            };
            loader.AddMod(mod);

            loader.UnloadMod(mod);

            Assert.Multiple(() =>
            {
                Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.NotLoaded));
                Assert.That(mod.PluginInstance, Is.Null);
                Assert.That(plugin.UnloadCount, Is.EqualTo(1));
                Assert.That(HookHelper.HasProcessLifetimeHooks(modId), Is.True);
            });
        }
        finally
        {
            HookHelper.Instance = previousHook;
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void UntrackedGenerationHookForcesConservativeSuspension()
    {
        var previousHook = HookHelper.Instance;
        var hook = new GenerationCallbackAwareHook();
        var modId = $"untracked-generation-{Guid.NewGuid():N}";
        var modsRoot = Path.Combine(Path.GetTempPath(), modId);
        var plugin = new LogicallyRetirableHookedPlugin(modId);
        var mod = new ModEntry
        {
            Id = modId,
            Name = "Untracked Generation Hook",
            LoaderKind = ModEntry.NativeLoaderKind,
            IsEnabled = true,
            LoadState = ModLoadState.Loaded,
            PluginInstance = plugin
        };
        var key = mod.EnsureRuntimeActive();
        try
        {
            HookHelper.Instance = hook;
            using (HookHelper.EnterOwnerScope(key.OwnerId, mod.RuntimeSession, key))
                Assert.That(HookHelper.Hook((nint)0x8100, (nint)0x8200),
                    Is.EqualTo((nint)0x8300));
            Assert.That(HookHelper.HasUntrackedProcessLifetimeCallbacks(key), Is.True);

            var loader = new ModLoader(modsRoot);
            loader.AddMod(mod);
            loader.UnloadMod(mod);

            Assert.Multiple(() =>
            {
                Assert.That(plugin.UnloadCount, Is.Zero);
                Assert.That(mod.PluginInstance, Is.SameAs(plugin));
                Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.NotLoaded));
                Assert.That(mod.RuntimeSession.Snapshot().State,
                    Is.EqualTo(ModRuntimeLifecycleState.Suspended));
                Assert.That(hook.GetUntrackedCallbackLayerCount(
                    key.OwnerId, key.Generation), Is.EqualTo(1));
            });
        }
        finally
        {
            HookHelper.Instance = previousHook;
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void CapturedRuntimeGateAllowsDeclaredLogicalRetirement()
    {
        var previousHook = HookHelper.Instance;
        var hook = new GenerationCallbackAwareHook();
        var modId = $"gated-generation-{Guid.NewGuid():N}";
        var modsRoot = Path.Combine(Path.GetTempPath(), modId);
        var plugin = new LogicallyRetirableHookedPlugin(modId);
        var mod = new ModEntry
        {
            Id = modId,
            Name = "Gated Generation Hook",
            LoaderKind = ModEntry.NativeLoaderKind,
            IsEnabled = true,
            LoadState = ModLoadState.Loaded,
            PluginInstance = plugin
        };
        var key = mod.EnsureRuntimeActive();
        try
        {
            HookHelper.Instance = hook;
            using (HookHelper.EnterOwnerScope(key.OwnerId, mod.RuntimeSession, key))
            {
                var runtimeGate = HookHelper.CaptureRuntimeCallbackGate();
                Assert.That(HookHelper.HookRuntimeGated(
                    (nint)0x9100,
                    (nint)0x9200,
                    runtimeGate), Is.EqualTo((nint)0x9300));
            }
            Assert.That(HookHelper.HasUntrackedProcessLifetimeCallbacks(key), Is.False);

            var loader = new ModLoader(modsRoot);
            loader.AddMod(mod);
            loader.UnloadMod(mod);

            Assert.Multiple(() =>
            {
                Assert.That(plugin.UnloadCount, Is.EqualTo(1));
                Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.NotLoaded));
                Assert.That(mod.RuntimeSession.Snapshot().State,
                    Is.EqualTo(ModRuntimeLifecycleState.Suspended));
                Assert.That(hook.GetUntrackedCallbackLayerCount(
                    key.OwnerId, key.Generation), Is.Zero);
            });
        }
        finally
        {
            HookHelper.Instance = previousHook;
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void RuntimeUnhookProviderKeepsNormalUnloadLifecycle()
    {
        var previousHook = HookHelper.Instance;
        var modId = $"runtime-unhook-{Guid.NewGuid():N}";
        var modsRoot = Path.Combine(Path.GetTempPath(), modId);
        var plugin = new HookedPlugin(modId);
        try
        {
            HookHelper.Instance = new RuntimeUnhookHook();
            using (HookHelper.EnterOwnerScope(modId))
                Assert.That(HookHelper.Hook((nint)0x1000, (nint)0x2000), Is.EqualTo((nint)0x3000));

            var loader = new ModLoader(modsRoot);
            var mod = new ModEntry
            {
                Id = modId,
                Name = "Runtime Hook",
                IsEnabled = true,
                LoadState = ModLoadState.Loaded,
                PluginInstance = plugin
            };
            loader.AddMod(mod);

            loader.UnloadMod(mod);

            Assert.Multiple(() =>
            {
                Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.NotLoaded));
                Assert.That(mod.PluginInstance, Is.Null);
                Assert.That(plugin.UnloadCount, Is.EqualTo(1));
                Assert.That(HookHelper.HasProcessLifetimeHooks(modId), Is.False);
            });
        }
        finally
        {
            HookHelper.Instance = previousHook;
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public async Task HookOwnerScopeFlowsIntoAsyncLoadWork()
    {
        var previousHook = HookHelper.Instance;
        var modId = $"async-permanent-hook-{Guid.NewGuid():N}";
        try
        {
            HookHelper.Instance = new ProcessLifetimeHook();
            using (HookHelper.EnterOwnerScope(modId))
            {
                await Task.Run(() => HookHelper.Hook((nint)0x1000, (nint)0x2000));
            }

            Assert.That(HookHelper.HasProcessLifetimeHooks(modId), Is.True);
        }
        finally
        {
            HookHelper.Instance = previousHook;
        }
    }

    [Test]
    public void UnloadRejectsPermanentHookInstallationAfterRetirementStarts()
    {
        var previousHook = HookHelper.Instance;
        var modId = $"retiring-hook-{Guid.NewGuid():N}";
        var modsRoot = Path.Combine(Path.GetTempPath(), modId);
        using var unloadEntered = new ManualResetEventSlim();
        using var releaseUnload = new ManualResetEventSlim();
        var plugin = new BlockingUnloadPlugin(modId, unloadEntered, releaseUnload);
        var hook = new CountingProcessLifetimeHook();
        try
        {
            HookHelper.Instance = hook;
            var loader = new ModLoader(modsRoot);
            var mod = new ModEntry
            {
                Id = modId,
                Name = "Retiring Hook",
                IsEnabled = true,
                LoadState = ModLoadState.Loaded,
                PluginInstance = plugin
            };
            loader.AddMod(mod);

            var unload = Task.Run(() => loader.UnloadMod(mod));
            Assert.That(unloadEntered.Wait(TimeSpan.FromSeconds(2)), Is.True);
            using (HookHelper.EnterOwnerScope(modId))
                Assert.That(HookHelper.Hook((nint)0x4000, (nint)0x5000), Is.EqualTo(nint.Zero));
            Assert.That(hook.HookCalls, Is.Zero);

            releaseUnload.Set();
            Assert.That(unload.Wait(TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(loader.LoadMod(mod), Is.True);
            using (HookHelper.EnterOwnerScope(modId))
                Assert.That(HookHelper.Hook((nint)0x4000, (nint)0x5000), Is.EqualTo((nint)0x3000));
            Assert.That(hook.HookCalls, Is.EqualTo(1));
        }
        finally
        {
            releaseUnload.Set();
            HookHelper.Instance = previousHook;
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    private class HookedPlugin(string id) : IModPlugin
    {
        public string Id => id;
        public string Name => "Permanent Hook";
        public string Version => "1.0";
        public string Author => "test";
        public string Description => "test";
        public IReadOnlyList<string> Dependencies => Array.Empty<string>();
        public int LoadCount { get; private set; }
        public int UnloadCount { get; private set; }
        public void OnLoad() => LoadCount++;
        public virtual void OnUnload() => UnloadCount++;
    }

    private sealed class LogicallyRetirableHookedPlugin(string id) :
        HookedPlugin(id),
        ILogicalProcessLifetimeHookRetirement;

    private sealed class ProcessLifetimeHook : IHook
    {
        public bool SupportsRuntimeUnhook => false;
        public nint Hook(nint target, nint detour) => (nint)0x3000;
        public bool Unhook(nint target) => false;
        public nint GetFunction(string library, string name) => nint.Zero;
    }

    private sealed class FailingProcessLifetimeHook : IHook
    {
        public bool SupportsRuntimeUnhook => false;
        public nint Hook(nint target, nint detour) => nint.Zero;
        public bool Unhook(nint target) => false;
        public nint GetFunction(string library, string name) => nint.Zero;
    }

    private sealed class RuntimeUnhookHook : IHook
    {
        public nint Hook(nint target, nint detour) => (nint)0x3000;
        public bool Unhook(nint target) => true;
        public nint GetFunction(string library, string name) => nint.Zero;
    }

    private sealed class CountingProcessLifetimeHook : IHook
    {
        private int _hookCalls;
        public bool SupportsRuntimeUnhook => false;
        public int HookCalls => Volatile.Read(ref _hookCalls);
        public nint Hook(nint target, nint detour)
        {
            Interlocked.Increment(ref _hookCalls);
            return (nint)0x3000;
        }
        public bool Unhook(nint target) => false;
        public nint GetFunction(string library, string name) => nint.Zero;
    }

    private sealed class BlockingUnloadPlugin(
        string id,
        ManualResetEventSlim unloadEntered,
        ManualResetEventSlim releaseUnload) : HookedPlugin(id)
    {
        public override void OnUnload()
        {
            unloadEntered.Set();
            releaseUnload.Wait(TimeSpan.FromSeconds(5));
            base.OnUnload();
        }
    }

    private sealed class UnhookingPlugin(string id, nint target) : HookedPlugin(id)
    {
        public bool UnhookResult { get; private set; }

        public override void OnUnload()
        {
            UnhookResult = HookHelper.Unhook(target);
            base.OnUnload();
        }
    }

    private sealed class OwnerScopedProcessLifetimeHook : IOwnerScopedHook
    {
        private sealed class Layer
        {
            public bool Enabled { get; set; } = true;
        }

        private readonly object _sync = new();
        private readonly Dictionary<string, Dictionary<nint, Layer>> _layers =
            new(StringComparer.Ordinal);

        public bool SupportsRuntimeUnhook => false;
        public bool SupportsOwnerControl => true;

        public nint Hook(nint target, nint detour)
        {
            var owner = HookHelper.CurrentOwnerId
                        ?? throw new InvalidOperationException("owner scope required");
            lock (_sync)
            {
                if (!_layers.TryGetValue(owner, out var targets))
                {
                    targets = new Dictionary<nint, Layer>();
                    _layers.Add(owner, targets);
                }
                targets[target] = new Layer();
            }
            return (nint)0x3000;
        }

        public bool Unhook(nint target)
        {
            var owner = HookHelper.CurrentOwnerId;
            return owner != null && RetireOwnerTarget(owner, target);
        }

        public bool SetOwnerEnabled(string owner, bool enabled)
        {
            lock (_sync)
            {
                if (!_layers.TryGetValue(owner, out var targets))
                    return true;
                foreach (var layer in targets.Values)
                    layer.Enabled = enabled;
                return true;
            }
        }

        public bool RetireOwnerTarget(string owner, nint target)
        {
            lock (_sync)
                return _layers.TryGetValue(owner, out var targets) && targets.Remove(target);
        }

        public int RetireOwner(string owner)
        {
            lock (_sync)
            {
                if (!_layers.Remove(owner, out var targets))
                    return 0;
                return targets.Count;
            }
        }

        public int GetRetainedLayerCount(string owner)
        {
            lock (_sync)
                return _layers.TryGetValue(owner, out var targets) ? targets.Count : 0;
        }

        public int GetEnabledLayerCount(string owner)
        {
            lock (_sync)
            {
                return _layers.TryGetValue(owner, out var targets)
                    ? targets.Values.Count(layer => layer.Enabled)
                    : 0;
            }
        }

        public nint GetFunction(string library, string name) => nint.Zero;
    }

    private sealed class GenerationCallbackAwareHook :
        IGenerationScopedHook,
        IManagedCallbackGateAwareHook
    {
        private sealed class Layer
        {
            public required string Owner { get; init; }
            public required long Generation { get; init; }
            public required nint Target { get; init; }
            public bool ManagedCallbackGate { get; init; }
            public bool Enabled { get; set; } = true;
            public bool Retired { get; set; }
        }

        private readonly object _sync = new();
        private readonly List<Layer> _layers = new();

        public bool SupportsRuntimeUnhook => false;
        public bool SupportsOwnerControl => true;

        public nint Hook(nint target, nint detour)
            => AddLayer(target, managedCallbackGate: false);

        public nint HookWithManagedCallbackGate(nint target, nint detour)
            => AddLayer(target, managedCallbackGate: true);

        public nint HookCompatibleWithManagedCallbackGate(
            nint target,
            nint detour,
            RuntimeMethodCompatibilityKind kind)
            => AddLayer(target, managedCallbackGate: true);

        private nint AddLayer(nint target, bool managedCallbackGate)
        {
            var key = HookHelper.CurrentRuntimeKey;
            var owner = HookHelper.CurrentOwnerId;
            if (!key.IsValid || owner == null || !string.Equals(
                    key.OwnerId, owner, StringComparison.Ordinal))
            {
                return nint.Zero;
            }
            lock (_sync)
            {
                _layers.Add(new Layer
                {
                    Owner = owner,
                    Generation = key.Generation,
                    Target = target,
                    ManagedCallbackGate = managedCallbackGate
                });
            }
            return target + 0x200;
        }

        public bool Unhook(nint target)
        {
            var key = HookHelper.CurrentRuntimeKey;
            return key.IsValid && RetireOwnerGenerationTarget(
                key.OwnerId, key.Generation, target);
        }

        public bool SetOwnerEnabled(string owner, bool enabled)
        {
            lock (_sync)
            {
                foreach (var layer in _layers.Where(layer =>
                             !layer.Retired && layer.Owner == owner))
                    layer.Enabled = enabled;
            }
            return true;
        }

        public bool SetOwnerGenerationEnabled(string owner, long generation, bool enabled)
        {
            lock (_sync)
            {
                foreach (var layer in _layers.Where(layer =>
                             !layer.Retired && layer.Owner == owner &&
                             layer.Generation == generation))
                    layer.Enabled = enabled;
            }
            return true;
        }

        public bool RetireOwnerTarget(string owner, nint target)
        {
            lock (_sync)
            {
                var changed = false;
                foreach (var layer in _layers.Where(layer =>
                             !layer.Retired && layer.Owner == owner &&
                             layer.Target == target))
                {
                    layer.Enabled = false;
                    layer.Retired = true;
                    changed = true;
                }
                return changed;
            }
        }

        public bool RetireOwnerGenerationTarget(
            string owner,
            long generation,
            nint target)
        {
            lock (_sync)
            {
                var changed = false;
                foreach (var layer in _layers.Where(layer =>
                             !layer.Retired && layer.Owner == owner &&
                             layer.Generation == generation &&
                             layer.Target == target))
                {
                    layer.Enabled = false;
                    layer.Retired = true;
                    changed = true;
                }
                return changed;
            }
        }

        public int RetireOwner(string owner)
        {
            lock (_sync)
                return RetireWhere(layer => layer.Owner == owner);
        }

        public int RetireOwnerGeneration(string owner, long generation)
        {
            lock (_sync)
                return RetireWhere(layer =>
                    layer.Owner == owner && layer.Generation == generation);
        }

        private int RetireWhere(Func<Layer, bool> predicate)
        {
            var retired = 0;
            foreach (var layer in _layers.Where(layer =>
                         !layer.Retired && predicate(layer)))
            {
                layer.Enabled = false;
                layer.Retired = true;
                retired++;
            }
            return retired;
        }

        public int GetRetainedLayerCount(string owner)
        {
            lock (_sync)
                return _layers.Count(layer => !layer.Retired && layer.Owner == owner);
        }

        public int GetRetainedLayerCount(string owner, long generation)
        {
            lock (_sync)
                return _layers.Count(layer =>
                    !layer.Retired && layer.Owner == owner &&
                    layer.Generation == generation);
        }

        public int GetUntrackedCallbackLayerCount(string owner, long generation)
        {
            lock (_sync)
                return _layers.Count(layer =>
                    !layer.Retired && layer.Owner == owner &&
                    layer.Generation == generation && !layer.ManagedCallbackGate);
        }

        public nint GetFunction(string library, string name) => nint.Zero;
    }
}

using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

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
    public void ProcessLifetimeHookProviderRejectsDirectUnhook()
    {
        var previousHook = HookHelper.Instance;
        try
        {
            HookHelper.Instance = new ProcessLifetimeHook();
            Assert.Throws<NotSupportedException>(() => HookHelper.Unhook((nint)0x1000));
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
        public void OnUnload() => UnloadCount++;
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

    private sealed class RuntimeUnhookHook : IHook
    {
        public nint Hook(nint target, nint detour) => (nint)0x3000;
        public bool Unhook(nint target) => true;
        public nint GetFunction(string library, string name) => nint.Zero;
    }
}

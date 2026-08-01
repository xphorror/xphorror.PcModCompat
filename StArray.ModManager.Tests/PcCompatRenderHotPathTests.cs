using StArray.ModManager.Runtime;
using StArray.ModManager.Manager;
using System.Reflection;

namespace StArray.ModManager.Tests;

public sealed class PcCompatRenderHotPathTests
{
    [Test]
    public void LoadingModsDoNotForceHiddenImGuiRender()
    {
        var modsRoot = Path.Combine(Path.GetTempPath(), $"starray-hot-path-{Guid.NewGuid():N}");
        try
        {
            var loader = new ModLoader(modsRoot);
            var ui = new ModManagerUI(loader, Path.Combine(modsRoot, "config"));
            loader.AddMod(new ModEntry
            {
                Id = "loading",
                Name = "Loading",
                LoadState = ModLoadState.Loading
            });

            Assert.That(ui.RequiresRenderingWhenHidden, Is.False);
        }
        finally
        {
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void LoadedPersistentOverlayCanStillRequestHiddenImGuiRender()
    {
        var modsRoot = Path.Combine(Path.GetTempPath(), $"starray-hot-path-{Guid.NewGuid():N}");
        try
        {
            var loader = new ModLoader(modsRoot);
            var ui = new ModManagerUI(loader, Path.Combine(modsRoot, "config"));
            loader.AddMod(new ModEntry
            {
                Id = "persistent",
                Name = "Persistent",
                LoadState = ModLoadState.Loaded,
                PluginInstance = new PersistentOverlayPlugin()
            });

            Assert.That(ui.RequiresRenderingWhenHidden, Is.True);
        }
        finally
        {
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void LoadedLegacyForegroundOverlayCanRequestHiddenImGuiRender()
    {
        var modsRoot = Path.Combine(Path.GetTempPath(), $"starray-hot-path-{Guid.NewGuid():N}");
        try
        {
            var loader = new ModLoader(modsRoot);
            var ui = new ModManagerUI(loader, Path.Combine(modsRoot, "config"));
            loader.AddMod(new ModEntry
            {
                Id = "legacy-overlay",
                Name = "Legacy Overlay",
                LoadState = ModLoadState.Loaded,
                PluginInstance = new LegacyForegroundOverlayPlugin()
            });

            Assert.That(ui.RequiresRenderingWhenHidden, Is.True);
        }
        finally
        {
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void ExplicitPersistentOverlayOptOutOverridesLegacyCallbackDetection()
    {
        var modsRoot = Path.Combine(Path.GetTempPath(), $"starray-hot-path-{Guid.NewGuid():N}");
        try
        {
            var loader = new ModLoader(modsRoot);
            var ui = new ModManagerUI(loader, Path.Combine(modsRoot, "config"));
            loader.AddMod(new ModEntry
            {
                Id = "disabled-overlay",
                Name = "Disabled Overlay",
                LoadState = ModLoadState.Loaded,
                PluginInstance = new DisabledPersistentOverlayPlugin()
            });

            Assert.That(ui.RequiresRenderingWhenHidden, Is.False);
        }
        finally
        {
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void OriginalSettingsRouteKeepsHiddenRendererAliveAcrossCloseAndReopen()
    {
        var modsRoot = Path.Combine(Path.GetTempPath(), $"starray-settings-route-{Guid.NewGuid():N}");
        try
        {
            var loader = new ModLoader(modsRoot);
            var platform = new StatefulOverlayPlatformServices();
            var ui = new ModManagerUI(
                loader,
                Path.Combine(modsRoot, "config"),
                platform);
            var plugin = new OriginalSettingsPlugin
            {
                SettingsState = ModOriginalSettingsState.Open
            };
            loader.AddMod(new ModEntry
            {
                Id = plugin.Id,
                Name = plugin.Name,
                LoadState = ModLoadState.Loaded,
                PluginInstance = plugin
            });

            BeginExternalSettingsRoute(ui, plugin.Id);
            Assert.That(ui.RequiresRenderingWhenHidden, Is.True);
            ui.PollPendingLoadsWhenHidden();
            Assert.That(platform.IsModalInputCaptureActive, Is.True);
            Assert.That(platform.BlocksUnityEventSystem, Is.True);

            plugin.SettingsState = ModOriginalSettingsState.Closed;
            ui.PollPendingLoadsWhenHidden();
            Assert.Multiple(() =>
            {
                Assert.That(platform.IsOverlayVisible, Is.True);
                Assert.That(platform.IsModalInputCaptureActive, Is.False);
                Assert.That(ui.RequiresRenderingWhenHidden, Is.False);
            });

            platform.SetOverlayVisible(false);
            plugin.SettingsState = ModOriginalSettingsState.Open;
            BeginExternalSettingsRoute(ui, plugin.Id);
            ui.PollPendingLoadsWhenHidden();
            Assert.Multiple(() =>
            {
                Assert.That(ui.RequiresRenderingWhenHidden, Is.True);
                Assert.That(platform.IsModalInputCaptureActive, Is.True);
            });
        }
        finally
        {
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void SettingsButtonOpensCompatibilityPageWithoutOpeningOriginalSurface()
    {
        var modsRoot = Path.Combine(Path.GetTempPath(), $"starray-settings-load-{Guid.NewGuid():N}");
        try
        {
            var loader = new ModLoader(modsRoot);
            var platform = new StatefulOverlayPlatformServices();
            var ui = new ModManagerUI(loader, Path.Combine(modsRoot, "config"), platform);
            var plugin = new OriginalSettingsPlugin();
            var mod = loader.AddMod(new ModEntry
            {
                Id = plugin.Id,
                Name = plugin.Name,
                LoadState = ModLoadState.Loaded,
                PluginInstance = plugin
            });
            var handler = typeof(ModManagerUI).GetMethod(
                "HandleSettingsButtonClick",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(
                    typeof(ModManagerUI).FullName,
                    "HandleSettingsButtonClick");
            handler.Invoke(ui, [mod, false]);

            Assert.Multiple(() =>
            {
                Assert.That(plugin.OpenRequestCount, Is.Zero);
                Assert.That(plugin.SettingsState, Is.EqualTo(ModOriginalSettingsState.Unavailable));
                Assert.That(GetPrivateField<string>(ui, "_expandedModId"), Is.EqualTo(mod.Id));
                Assert.That(GetPrivateField<string>(ui, "_externalSettingsModId"), Is.Null);
                Assert.That(ui.RequiresRenderingWhenHidden, Is.False);
                Assert.That(platform.IsModalInputCaptureActive, Is.False);
            });
        }
        finally
        {
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void ExplicitOriginalSettingsCommandKeepsCompatibilityPageForReturn()
    {
        var modsRoot = Path.Combine(Path.GetTempPath(), $"starray-settings-open-{Guid.NewGuid():N}");
        try
        {
            var loader = new ModLoader(modsRoot);
            var platform = new StatefulOverlayPlatformServices();
            platform.SetOverlayVisible(true);
            var ui = new ModManagerUI(loader, Path.Combine(modsRoot, "config"), platform);
            var plugin = new OriginalSettingsPlugin();
            var mod = loader.AddMod(new ModEntry
            {
                Id = plugin.Id,
                Name = plugin.Name,
                LoadState = ModLoadState.Loaded,
                PluginInstance = plugin
            });
            SetPrivateField(ui, "_expandedModId", mod.Id);

            var opener = typeof(ModManagerUI).GetMethod(
                "TryOpenOriginalSettingsSurface",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(
                    typeof(ModManagerUI).FullName,
                    "TryOpenOriginalSettingsSurface");
            var opened = (bool)opener.Invoke(ui, [mod])!;

            Assert.Multiple(() =>
            {
                Assert.That(opened, Is.True);
                Assert.That(plugin.OpenRequestCount, Is.EqualTo(1));
                Assert.That(plugin.SettingsState, Is.EqualTo(ModOriginalSettingsState.Opening));
                Assert.That(GetPrivateField<string>(ui, "_expandedModId"), Is.EqualTo(mod.Id));
                Assert.That(GetPrivateField<string>(ui, "_externalSettingsModId"), Is.EqualTo(mod.Id));
                Assert.That(ui.RequiresRenderingWhenHidden, Is.True);
                Assert.That(platform.IsOverlayVisible, Is.False);
                Assert.That(platform.IsModalInputCaptureActive, Is.True);
                Assert.That(platform.BlocksUnityEventSystem, Is.True);
            });

            plugin.SettingsState = ModOriginalSettingsState.Closed;
            ui.PollPendingLoadsWhenHidden();

            Assert.Multiple(() =>
            {
                Assert.That(GetPrivateField<string>(ui, "_expandedModId"), Is.EqualTo(mod.Id));
                Assert.That(GetPrivateField<string>(ui, "_externalSettingsModId"), Is.Null);
                Assert.That(platform.IsOverlayVisible, Is.True);
                Assert.That(platform.IsModalInputCaptureActive, Is.False);
            });
        }
        finally
        {
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void FailedOriginalSettingsCommandLeavesCompatibilityPageVisible()
    {
        var modsRoot = Path.Combine(Path.GetTempPath(), $"starray-settings-failure-{Guid.NewGuid():N}");
        try
        {
            var loader = new ModLoader(modsRoot);
            var platform = new StatefulOverlayPlatformServices();
            platform.SetOverlayVisible(true);
            var ui = new ModManagerUI(loader, Path.Combine(modsRoot, "config"), platform);
            var plugin = new OriginalSettingsPlugin
            {
                OpenSucceeds = false,
                OpenError = "test failure"
            };
            var mod = loader.AddMod(new ModEntry
            {
                Id = plugin.Id,
                Name = plugin.Name,
                LoadState = ModLoadState.Loaded,
                PluginInstance = plugin
            });
            SetPrivateField(ui, "_expandedModId", mod.Id);

            var opened = InvokeOriginalSettingsCommand(ui, mod);

            Assert.Multiple(() =>
            {
                Assert.That(opened, Is.False);
                Assert.That(plugin.OpenRequestCount, Is.EqualTo(1));
                Assert.That(GetPrivateField<string>(ui, "_expandedModId"), Is.EqualTo(mod.Id));
                Assert.That(GetPrivateField<string>(ui, "_externalSettingsModId"), Is.Null);
                Assert.That(platform.IsOverlayVisible, Is.True);
                Assert.That(platform.IsModalInputCaptureActive, Is.False);
            });
        }
        finally
        {
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void FaultedOriginalSettingsSurfaceReturnsToCompatibilityPage()
    {
        var modsRoot = Path.Combine(Path.GetTempPath(), $"starray-settings-fault-{Guid.NewGuid():N}");
        try
        {
            var loader = new ModLoader(modsRoot);
            var platform = new StatefulOverlayPlatformServices();
            var ui = new ModManagerUI(loader, Path.Combine(modsRoot, "config"), platform);
            var plugin = new OriginalSettingsPlugin
            {
                SettingsState = ModOriginalSettingsState.Faulted
            };
            var mod = loader.AddMod(new ModEntry
            {
                Id = plugin.Id,
                Name = plugin.Name,
                LoadState = ModLoadState.Loaded,
                PluginInstance = plugin
            });
            BeginExternalSettingsRoute(ui, mod.Id);
            SetPrivateField(ui, "_externalSettingsOverlayHidden", true);
            platform.SetModalInputCapture(true, blockUnityEventSystem: true);

            ui.PollPendingLoadsWhenHidden();

            Assert.Multiple(() =>
            {
                Assert.That(GetPrivateField<string>(ui, "_expandedModId"), Is.EqualTo(mod.Id));
                Assert.That(GetPrivateField<string>(ui, "_externalSettingsModId"), Is.Null);
                Assert.That(platform.IsOverlayVisible, Is.True);
                Assert.That(platform.IsModalInputCaptureActive, Is.False);
            });
        }
        finally
        {
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void OriginalUnityCanvasSettingsRetainsModalWithoutBlockingEventSystem()
    {
        var modsRoot = Path.Combine(Path.GetTempPath(), $"starray-canvas-settings-{Guid.NewGuid():N}");
        try
        {
            var loader = new ModLoader(modsRoot);
            var platform = new StatefulOverlayPlatformServices();
            var ui = new ModManagerUI(loader, Path.Combine(modsRoot, "config"), platform);
            var plugin = new OriginalSettingsPlugin
            {
                SettingsState = ModOriginalSettingsState.Open,
                SurfaceKind = ModOriginalSettingsSurfaceKind.UnityCanvas
            };
            loader.AddMod(new ModEntry
            {
                Id = plugin.Id,
                Name = plugin.Name,
                LoadState = ModLoadState.Loaded,
                PluginInstance = plugin
            });

            BeginExternalSettingsRoute(ui, plugin.Id);
            ui.PollPendingLoadsWhenHidden();

            Assert.Multiple(() =>
            {
                Assert.That(platform.IsModalInputCaptureActive, Is.True);
                Assert.That(platform.BlocksUnityEventSystem, Is.False);
                Assert.That(platform.IsOverlayVisible, Is.False);
            });
        }
        finally
        {
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void HiddenOverlayStillCompletesPendingAsyncModLoads()
    {
        var modsRoot = Path.Combine(Path.GetTempPath(), $"starray-hot-path-{Guid.NewGuid():N}");
        try
        {
            var loader = new ModLoader(modsRoot);
            var ui = new ModManagerUI(
                loader,
                Path.Combine(modsRoot, "config"),
                new HiddenOverlayPlatformServices());
            var plugin = new ReadyAsyncPlugin();
            var mod = loader.AddMod(new ModEntry
            {
                Id = "async",
                Name = "Async",
                LoadState = ModLoadState.Loading,
                PluginInstance = plugin
            });

            ui.PollPendingLoadsWhenHidden();

            Assert.Multiple(() =>
            {
                Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.Loaded));
                Assert.That(mod.IsEnabled, Is.True);
                Assert.That(plugin.CompleteLoadCount, Is.EqualTo(1));
            });
        }
        finally
        {
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void ConfiguredEnabledModsCanAutoLoadWithoutManagerOverlay()
    {
        var modsRoot = Path.Combine(Path.GetTempPath(), $"starray-hot-path-{Guid.NewGuid():N}");
        try
        {
            var loader = new ModLoader(modsRoot);
            var mod = loader.AddMod(new ModEntry
            {
                Id = "configured",
                Name = "Configured"
            });

            var started = loader.LoadConfiguredEnabledMods(new Dictionary<string, bool>
            {
                ["configured"] = true
            });

            Assert.Multiple(() =>
            {
                Assert.That(started, Is.EqualTo(1));
                Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.Loaded));
                Assert.That(mod.IsEnabled, Is.True);
            });
        }
        finally
        {
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void ConfiguredAutoLoadDoesNotRestartAlreadyLoadingMods()
    {
        var modsRoot = Path.Combine(Path.GetTempPath(), $"starray-hot-path-{Guid.NewGuid():N}");
        try
        {
            var loader = new ModLoader(modsRoot);
            var mod = loader.AddMod(new ModEntry
            {
                Id = "loading",
                Name = "Loading",
                LoadState = ModLoadState.Loading
            });

            var started = loader.LoadConfiguredEnabledMods(new Dictionary<string, bool>
            {
                ["loading"] = true
            });

            Assert.Multiple(() =>
            {
                Assert.That(started, Is.Zero);
                Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.Loading));
            });
        }
        finally
        {
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void ModListReadOnlyViewIsStableAcrossRenderPolls()
    {
        var modsRoot = Path.Combine(Path.GetTempPath(), $"starray-hot-path-{Guid.NewGuid():N}");
        try
        {
            var loader = new ModLoader(modsRoot);
            var first = loader.Mods;
            var second = loader.Mods;

            Assert.That(second, Is.SameAs(first));

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 1024; ++index)
                _ = loader.Mods;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.Zero);
        }
        finally
        {
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void EmptyPendingLoadPollingDoesNotAllocate()
    {
        var modsRoot = Path.Combine(Path.GetTempPath(), $"starray-hot-path-{Guid.NewGuid():N}");
        try
        {
            var loader = new ModLoader(modsRoot);
            loader.UpdatePendingLoads();

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 1024; ++index)
                loader.UpdatePendingLoads();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.Zero);
        }
        finally
        {
            if (Directory.Exists(modsRoot))
                Directory.Delete(modsRoot, recursive: true);
        }
    }

    private sealed class PersistentOverlayPlugin : IModPlugin, IPersistentModOverlay
    {
        public string Id => "persistent";
        public string Name => "Persistent";
        public string Version => "1.0";
        public string Author => "test";
        public string Description => "test";
        public IReadOnlyList<string> Dependencies => Array.Empty<string>();
        public bool ShouldRenderWhenManagerHidden => true;
        public void OnLoad() { }
        public void OnUnload() { }
    }

    private sealed class LegacyForegroundOverlayPlugin : IModPlugin
    {
        public string Id => "legacy-overlay";
        public string Name => "Legacy Overlay";
        public string Version => "1.0";
        public string Author => "test";
        public string Description => "test";
        public IReadOnlyList<string> Dependencies => Array.Empty<string>();
        public void OnLoad() { }
        public void OnUnload() { }
        public void OnForegroundGUI(ImGuiNET.ImDrawListPtr drawList) { }
    }

    private sealed class DisabledPersistentOverlayPlugin : IModPlugin, IPersistentModOverlay
    {
        public string Id => "disabled-overlay";
        public string Name => "Disabled Overlay";
        public string Version => "1.0";
        public string Author => "test";
        public string Description => "test";
        public IReadOnlyList<string> Dependencies => Array.Empty<string>();
        public bool ShouldRenderWhenManagerHidden => false;
        public void OnLoad() { }
        public void OnUnload() { }
        public void OnForegroundGUI(ImGuiNET.ImDrawListPtr drawList) { }
    }

    private sealed class ReadyAsyncPlugin : IModPlugin, IAsyncModPlugin
    {
        public string Id => "async";
        public string Name => "Async";
        public string Version => "1.0";
        public string Author => "test";
        public string Description => "test";
        public IReadOnlyList<string> Dependencies => Array.Empty<string>();
        public int CompleteLoadCount { get; private set; }
        public void OnLoad() { }
        public void OnUnload() { }
        public void BeginLoad() { }
        public ModLoadProgress GetLoadProgress() => new(1f, "Ready");
        public bool IsLoadReady => true;
        public void CompleteLoad() => CompleteLoadCount++;
        public void CancelLoad() { }
    }

    private sealed class HiddenOverlayPlatformServices : IModManagerPlatformServices
    {
        public bool SupportsModZipImport => false;
        public bool IsOverlayVisible => false;
        public bool IsModalInputCaptureActive => false;
        public void RequestModZipImport() { }
        public ModImportStatus GetModZipImportStatus()
            => new(0, ModImportState.Idle, string.Empty, null);
        public void BeginOverlayInputFrame() { }
        public void AddOverlayInputRect(float x, float y, float width, float height) { }
        public void EndOverlayInputFrame() { }
        public void SetOverlayVisible(bool visible) { }
        public void SetModalInputCapture(bool active, bool blockUnityEventSystem) { }
        public bool ConsumeModalCloseRequest() => false;
    }

    private static void BeginExternalSettingsRoute(ModManagerUI ui, string modId)
    {
        var field = typeof(ModManagerUI).GetField(
            "_externalSettingsModId",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(ModManagerUI).FullName, "_externalSettingsModId");
        field.SetValue(ui, modId);
    }

    private static bool InvokeOriginalSettingsCommand(ModManagerUI ui, ModEntry mod)
    {
        var opener = typeof(ModManagerUI).GetMethod(
            "TryOpenOriginalSettingsSurface",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(ModManagerUI).FullName,
                "TryOpenOriginalSettingsSurface");
        return (bool)opener.Invoke(ui, [mod])!;
    }

    private static void SetPrivateField<T>(ModManagerUI ui, string name, T value)
    {
        var field = typeof(ModManagerUI).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(ModManagerUI).FullName, name);
        field.SetValue(ui, value);
    }

    private static T? GetPrivateField<T>(ModManagerUI ui, string name)
    {
        var field = typeof(ModManagerUI).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(ModManagerUI).FullName, name);
        return (T?)field.GetValue(ui);
    }

    private sealed class OriginalSettingsPlugin : IModPlugin, IModSettings, IModOriginalSettingsSurface
    {
        public string Id => "original-settings";
        public string Name => "Original Settings";
        public string Version => "1.0";
        public string Author => "test";
        public string Description => "test";
        public IReadOnlyList<string> Dependencies => Array.Empty<string>();
        public ModOriginalSettingsState SettingsState { get; set; }
        public ModOriginalSettingsSurfaceKind SurfaceKind { get; set; }
        public bool OpenSucceeds { get; set; } = true;
        public string? OpenError { get; set; }
        public int OpenRequestCount { get; private set; }
        public void OnLoad() { }
        public void OnUnload() { }
        public void OnGui() { }
        public bool TryOpenOriginalSettings(out string? error)
        {
            OpenRequestCount++;
            if (!OpenSucceeds)
            {
                error = OpenError;
                return false;
            }
            SettingsState = ModOriginalSettingsState.Opening;
            error = null;
            return true;
        }
        public void RequestCloseOriginalSettings()
            => SettingsState = ModOriginalSettingsState.Closed;
        public ModOriginalSettingsSnapshot SnapshotOriginalSettings()
            => new(SettingsState, null, SurfaceKind);
    }

    private sealed class StatefulOverlayPlatformServices : IModManagerPlatformServices
    {
        public bool SupportsModZipImport => false;
        public bool IsOverlayVisible { get; private set; }
        public bool IsModalInputCaptureActive { get; private set; }
        public void RequestModZipImport() { }
        public ModImportStatus GetModZipImportStatus()
            => new(0, ModImportState.Idle, string.Empty, null);
        public void BeginOverlayInputFrame() { }
        public void AddOverlayInputRect(float x, float y, float width, float height) { }
        public void EndOverlayInputFrame() { }
        public void SetOverlayVisible(bool visible) => IsOverlayVisible = visible;
        public bool BlocksUnityEventSystem { get; private set; }
        public void SetModalInputCapture(bool active, bool blockUnityEventSystem)
        {
            IsModalInputCaptureActive = active;
            BlocksUnityEventSystem = active && blockUnityEventSystem;
        }
        public bool ConsumeModalCloseRequest() => false;
    }
}

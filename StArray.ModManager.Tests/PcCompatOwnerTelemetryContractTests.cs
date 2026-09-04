namespace StArray.ModManager.Tests;

public sealed class PcCompatOwnerTelemetryContractTests
{
    [Test]
    public void NativeDispatcherPublishesImmutableOwnerOverlaySubscribers()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "pccompat_hook_rules.cpp"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("struct OwnerOverlaySession"));
            Assert.That(source, Does.Contain("struct OwnerOverlayDispatchTarget"));
            Assert.That(source, Does.Contain("OwnerOverlayDispatchSnapshot"));
            Assert.That(source, Does.Contain("owner_overlay_after_rules"));
            Assert.That(source, Does.Contain("build_owner_overlay_dispatch_snapshot"));
            Assert.That(source, Does.Contain("std::atomic_load_explicit("));
            Assert.That(source, Does.Contain("session->retired.load(std::memory_order_acquire)"));
            Assert.That(source, Does.Contain("run_shared_after_ops("));
            Assert.That(source, Does.Contain("run_owner_overlay_after_ops("));
            Assert.That(source, Does.Contain("default_overlay_state_for_legacy_api"));
            Assert.That(source, Does.Contain(
                "return default_overlay_state_for_legacy_api().show_count.load"));
            Assert.That(source, Does.Contain(
                "modmanager_pccompat_read_overlay_snapshot_for_mod"));
            Assert.That(source, Does.Contain(
                "modmanager_pccompat_read_shared_game_snapshot"));
            Assert.That(source, Does.Contain("project_shared_game_facts_to_owner"));
            Assert.That(source, Does.Contain("poll_overlay_telemetry(args.instance, false)"));
        });
    }

    [Test]
    public void ManagedOverlayProviderCarriesOwnerIdentityToNative()
    {
        var root = FindRepositoryRoot();
        var native = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatNativeHookRules.cs"));
        var bridge = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatDobbyBridge.cs"));
        var runtime = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "src",
            "PcCompatOverlayRuntime.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(native, Does.Contain("ReadOverlaySnapshotForModNative"));
            Assert.That(native, Does.Contain(
                "public static PcCompatOverlaySnapshot GetOverlaySnapshot(string modId)"));
            Assert.That(native, Does.Contain("CachedOverlaySnapshots"));
            Assert.That(native, Does.Contain("ReadSharedGameSnapshotNative"));
            Assert.That(native, Does.Contain("GetSharedGameSnapshot"));
            Assert.That(bridge, Does.Contain("ownerProvider: PcCompatNativeHookRules.GetOverlaySnapshot"));
            Assert.That(runtime, Does.Contain("Func<string, PcCompatOverlaySnapshot>? OwnerProvider"));
            Assert.That(runtime, Does.Contain("registration.OwnerProvider(ownerId)"));
        });
    }

    [Test]
    public void LifecycleOverlayStateCanBePublishedPerBundle()
    {
        var core = Path.Combine(
            FindRepositoryRoot(),
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core");
        var header = File.ReadAllText(Path.Combine(core, "ui_recipe_lifecycle_runtime.h"));
        var source = File.ReadAllText(Path.Combine(core, "ui_recipe_lifecycle_runtime.cpp"));

        Assert.Multiple(() =>
        {
            Assert.That(header, Does.Contain("publish_bundle_overlay_state("));
            Assert.That(source, Does.Contain("state.overlay_generation"));
            Assert.That(source, Does.Contain("read_overlay_state(state)"));
            Assert.That(source, Does.Contain("state.bundle_id != bundle_id"));
        });
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Android")) &&
                Directory.Exists(Path.Combine(directory.FullName, "xphorror.PcModCompat")))
                return directory.FullName;
            directory = directory.Parent;
        }

        Assert.Fail("Could not find StArray.ModManager repository root from test directory");
        return string.Empty;
    }
}

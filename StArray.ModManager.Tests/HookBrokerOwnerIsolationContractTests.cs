namespace StArray.ModManager.Tests;

public sealed class HookBrokerOwnerIsolationContractTests
{
    [Test]
    public void NativeBrokerUsesOneStableGatewayAndOwnerControlledLayers()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "hook_broker.cpp"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("std::atomic<void *> head{nullptr}"));
            Assert.That(source, Does.Contain("std::atomic<uint32_t> enabled{0}"));
            Assert.That(source, Does.Contain("std::atomic<uint32_t> retired{0}"));
            Assert.That(source, Does.Contain("uint64_t generation = 0"));
            Assert.That(source, Does.Contain("bool managed_callback_gate = false"));
            Assert.That(source, Does.Contain("build_branch_stub("));
            Assert.That(source, Does.Contain("build_layer_stubs("));
            Assert.That(source, Does.Contain("std::atomic<void *>::is_always_lock_free"));
            Assert.That(source, Does.Contain("0xC8DFFE30u"));
            Assert.That(source, Does.Contain("prepared_layer->next.store("));
            Assert.That(source, Does.Contain("chain->head.store(published_layer->entry"));
            Assert.That(source, Does.Contain("const int rc = DobbyHook("));
            Assert.That(
                CountOccurrences(source, "const int rc = DobbyHook("),
                Is.EqualTo(1),
                "Only first installation may physically patch the target.");
            Assert.That(source, Does.Not.Contain(
                "patch_point = chain == nullptr ? target : chain->head"));
            Assert.That(source, Does.Contain("modmanager_hook_broker_set_owner_enabled"));
            Assert.That(source, Does.Contain("modmanager_hook_broker_retire_owner_target"));
            Assert.That(source, Does.Contain("modmanager_hook_broker_retire_owner"));
            Assert.That(source, Does.Contain(
                "modmanager_hook_broker_install_generation"));
            Assert.That(source, Does.Contain(
                "modmanager_hook_broker_install_generation_v2"));
            Assert.That(source, Does.Contain(
                "MODMANAGER_HOOK_LAYER_FLAG_MANAGED_CALLBACK_GATE"));
            Assert.That(source, Does.Contain(
                "callback gate classification changed for live layer"));
            Assert.That(source, Does.Contain(
                "modmanager_hook_broker_get_owner_generation_untracked_callback_layer_count"));
            Assert.That(source, Does.Not.Contain("active_callbacks"),
                "Generic arbitrary-ABI layers must not pay an atomic callback tax in the hot stub.");
            Assert.That(source, Does.Contain(
                "modmanager_hook_broker_set_owner_generation_enabled"));
            Assert.That(source, Does.Contain(
                "modmanager_hook_broker_retire_owner_generation"));
            Assert.That(source, Does.Contain(
                "modmanager_hook_broker_get_owner_generation_retained_layer_count"));
            Assert.That(source, Does.Contain(
                "layer->generation == generation"));
            Assert.That(source, Does.Contain(
                "modmanager_hook_broker_get_owner_retained_layer_count"));
            Assert.That(source, Does.Contain(
                "MODMANAGER_HOOK_ABI_CALCULATE_TICK_COLOR_WITHOUT_HIT_FLOOR"));
            Assert.That(source, Does.Contain(
                "modmanager_hook_broker_install_compatible"));
            Assert.That(source, Does.Contain("0xA9BF7BF3u"),
                "The compatibility entry must preserve x19/lr before calling the legacy detour.");
            Assert.That(source, Does.Contain("0xA8C17BF3u"),
                "The compatibility entry must restore x19/lr before returning.");
            Assert.That(source, Does.Contain("0xAA0203E1u"),
                "Actual methodInfo in x2 must be moved to legacy x1.");
            Assert.That(source, Does.Contain("0xAA0103E2u"),
                "Legacy methodInfo in x1 must be restored to actual x2.");
            Assert.That(source, Does.Contain("0xAA1303E1u"),
                "The original hitFloor must be restored from callee-saved x19.");
            Assert.That(source, Does.Contain(
                "write_u32(code, 24, encode_ldr_literal_x(16, 48))"),
                "The compatibility entry must load replacement, not the enabled flag.");
            Assert.That(source, Does.Contain(
                "write_u32(code, 40, encode_ldr_literal_x(17, 40))"),
                "The compatibility bypass must load the next layer pointer.");
        });
    }

    [Test]
    public void RetriedGenerationTransactionReactivatesItsRetiredLayer()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "hook_broker.cpp"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("find_reactivatable_generation_layer("));
            Assert.That(source, Does.Contain(
                "existing->retired.store(0, std::memory_order_release)"));
            Assert.That(source, Does.Contain(
                "reactivate owner=%s generation=%llu"));
            Assert.That(source.IndexOf(
                    "find_reactivatable_generation_layer(",
                    StringComparison.Ordinal),
                Is.LessThan(source.IndexOf(
                    "auto prepared_layer = std::make_unique<HookLayer>()",
                    StringComparison.Ordinal)),
                "An exact retired layer must be reused before allocating another executable stub.");
        });
    }

    [Test]
    public void ManagedHookIdentityAndLifecycleAreOwnerScoped()
    {
        var root = FindRepositoryRoot();
        var dobby = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "Native",
            "Dobby.cs"));
        var helper = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Runtime",
            "HookHelper.cs"));
        var loader = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Runtime",
            "ModLoader.cs"));
        var generator = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.SourceGenerator",
            "HookGenerator.cs"));
        var reserveOwner = dobby.IndexOf(
            "HookHelper.RegisterProcessLifetimeHookOwner(normalizedOwner)",
            StringComparison.Ordinal);
        var nativeInstall = dobby.IndexOf(
            "var result = generation > 0",
            StringComparison.Ordinal);
        var unloadComplete = loader.IndexOf(
            "route=onunload-complete",
            StringComparison.Ordinal);
        var finalDisable = loader.IndexOf(
            "EnsureProcessLifetimeHooksSuspended(mod, runtimeKey)",
            unloadComplete,
            StringComparison.Ordinal);
        var retainedQuery = loader.IndexOf(
            "hasProcessLifetimeHooks = HookHelper.HasProcessLifetimeHooks(runtimeKey)",
            unloadComplete,
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(dobby, Does.Contain(
                "bool ManagedCallbackGate);"));
            Assert.That(dobby, Does.Contain(
                "HookLayerFlagManagedCallbackGate"));
            Assert.That(reserveOwner, Is.GreaterThanOrEqualTo(0));
            Assert.That(nativeInstall, Is.GreaterThan(reserveOwner),
                "Cached layer reuse must pass the retiring-owner check and native re-enable path.");
            Assert.That(dobby, Does.Contain("HookHelper.CurrentOwnerId"));
            Assert.That(dobby, Does.Contain("HookHelper.CurrentRuntimeKey"));
            Assert.That(dobby, Does.Contain("_HookGeneration("));
            Assert.That(dobby, Does.Contain("_HookGenerationV2("));
            Assert.That(dobby, Does.Contain("_HookCompatibleGeneration("));
            Assert.That(dobby, Does.Contain("_HookCompatibleGenerationV2("));
            Assert.That(dobby, Does.Contain(
                "_GetOwnerGenerationUntrackedCallbackLayerCount("));
            Assert.That(generator, Does.Contain("HookRuntimeGated(t, d,"));
            Assert.That(generator, Does.Contain("runtimeGate?.ReportFailure("));
            Assert.That(helper, Does.Contain(
                "HasUntrackedProcessLifetimeCallbacks("));
            Assert.That(loader, Does.Contain(
                "hasUntrackedProcessLifetimeCallbacks"));
            Assert.That(dobby, Does.Contain(
                "internal static bool SetOwnerGenerationEnabled("));
            Assert.That(dobby, Does.Contain(
                "internal static bool RetireOwnerGenerationTarget("));
            Assert.That(dobby, Does.Contain(
                "string.Equals(runtimeKey.OwnerId, owner, StringComparison.Ordinal)"));
            Assert.That(dobby, Does.Contain("RetireOwnerTarget(owner, address)"));
            Assert.That(helper, Does.Contain(
                "IGenerationScopedHook generationScoped"));
            Assert.That(helper, Does.Contain(
                "generationScoped.SetOwnerGenerationEnabled("));
            Assert.That(loader, Does.Contain(
                "HookHelper.SuspendProcessLifetimeHooks(runtimeKey)"));
            Assert.That(loader, Does.Contain(
                "HookHelper.ResumeProcessLifetimeHooks(resumedKey)"));
            Assert.That(helper, Does.Contain("provider is IOwnerScopedHook ownerScoped"));
            Assert.That(helper, Does.Contain("ownerScoped.RetireOwnerTarget(owner, target)"));
            Assert.That(loader.IndexOf(
                    "EnsureProcessLifetimeHooksSuspended(mod, runtimeKey)",
                    StringComparison.Ordinal),
                Is.LessThan(loader.IndexOf(
                    "mod.PluginInstance.OnUnload()",
                    StringComparison.Ordinal)));
            Assert.That(loader, Does.Contain(
                "hasProcessLifetimeHooks = HookHelper.HasProcessLifetimeHooks(runtimeKey)"));
            Assert.That(finalDisable, Is.GreaterThan(unloadComplete));
            Assert.That(retainedQuery, Is.GreaterThan(finalDisable),
                "Unload must close the owner gate again before deciding whether its ALC can be released.");
        });
    }

    [Test]
    public void Il2CppPageCommitmentsNormalizeOnlyVerifiedHostHooks()
    {
        var root = FindRepositoryRoot();
        var broker = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "hook_broker.cpp"));
        var dobby = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "dobby_hook.cpp"));
        Assert.Multiple(() =>
        {
            Assert.That(broker, Does.Not.Contain("struct HookPageSnapshot"),
                "HookBroker and Dobby must not maintain divergent page snapshots.");
            Assert.That(dobby, Does.Contain("struct CodePatchPageSnapshot"));
            Assert.That(broker, Does.Contain("trusted_host_hook_request("));
            Assert.That(broker, Does.Contain("libAsyncInput.so"));
            Assert.That(broker, Does.Contain("libEditor_Pausemenu.so"));
            Assert.That(broker, Does.Contain("libadofai_extra_menu.so"));
            Assert.That(broker, Does.Contain("ADOFAI.AsyncInput/il2cpp_init"));
            Assert.That(broker, Does.Contain(
                "untrusted non-generation IL2CPP hook rejected"));
            Assert.That(broker, Does.Contain(
                "untrusted non-generation compatible IL2CPP hook rejected"));
            Assert.That(broker, Does.Contain("modmanager_hook_broker_copy_pristine_page"));
            Assert.That(broker, Does.Contain("CopyAuthenticatedPristinePage("));
            Assert.That(dobby, Does.Contain("snapshot.expected_current.data()"));
            Assert.That(dobby, Does.Contain("requested_offset"),
                "A 4 KiB commitment must be readable from a 16 KiB Android system-page snapshot.");
            Assert.That(dobby, Does.Contain(
                "MODMANAGER_PRISTINE_PAGE_CURRENT_MISMATCH"));
            Assert.That(dobby, Does.Not.Contain("[DEBUG-pristine-current-v1]"));
        });
    }

    [Test]
    public void AuthenticatedPristinePageSurvivesAllCoordinatedWriters()
    {
        var root = FindRepositoryRoot();
        var broker = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "hook_broker.cpp"));
        var dobby = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "dobby_hook.cpp"));

        Assert.Multiple(() =>
        {
            Assert.That(dobby, Does.Contain("authenticated_pristine"));
            Assert.That(dobby, Does.Contain(
                "unscoped IL2CPP instrument rejected"));
            Assert.That(dobby, Does.Contain(
                "unscoped IL2CPP code patch rejected"));
            Assert.That(dobby, Does.Contain("PrepareHookBrokerWrite("));
            Assert.That(dobby, Does.Contain("CommitHookBrokerWrite("));
            Assert.That(dobby, Does.Contain("CommitExternalWrite(address, size)"),
                "ThreadGuard and Dobby writes must update the same expected-current snapshot.");
            Assert.That(broker, Does.Not.Contain("trusted_host_only"));
            Assert.That(broker, Does.Not.Contain("mark_hook_page_snapshots_untrusted"));
            Assert.That(broker, Does.Contain(
                "PrepareHookBrokerWrite("));
            Assert.That(broker, Does.Contain("authenticate_target_page"));
            Assert.That(broker, Does.Contain(
                "&continuation,\n        true,\n        &continuation_protection"));
        });
    }

    [Test]
    public void NativeBrokerUsesFourByteNearBranchForRelocatableShortLeafMethods()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "hook_broker.cpp"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain(
                "constexpr size_t kDobbyNearBranchPatchSize = sizeof(uint32_t)"));
            Assert.That(source, Does.Contain(
                "is_relocatable_short_leaf_instruction"));
            Assert.That(source, Does.Contain(
                "patch_plan.use_near_branch = true"));
            Assert.That(source, Does.Contain(
                "dobby_enable_near_branch_trampoline()"));
            Assert.That(source, Does.Contain(
                "dobby_disable_near_branch_trampoline()"));
            Assert.That(source, Does.Contain(
                "patch_plan.reservation_size"));
            Assert.That(
                CountOccurrences(source, "const int rc = DobbyHook("),
                Is.EqualTo(1),
                "Near and absolute entry patches must share one physical Dobby install path.");
        });
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
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

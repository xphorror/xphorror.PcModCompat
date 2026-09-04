namespace StArray.ModManager.Tests;

public sealed class PcCompatNativeHudContractTests
{
    [Test]
    public void ManagedUnityHudKeepsFailuresInsideOneOwnerSurface()
    {
        var bridge = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatUnityHudBridge.cs"));
        var apply = ExtractMethodBlock(bridge, "private static void ApplySourceSnapshot");
        var create = apply.IndexOf(
            "surface = new HudSurface(snapshot.OwnerId, snapshot.SessionGeneration)",
            StringComparison.Ordinal);
        var ownerTry = apply.LastIndexOf("try", create, StringComparison.Ordinal);
        var ownerCatch = apply.IndexOf("catch (Exception ex)", create, StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(bridge, Does.Contain("Dictionary<string, HudSurface> Surfaces"));
            Assert.That(bridge, Does.Contain("PcCompatUnityHudRuntime.SnapshotSources()"));
            Assert.That(bridge, Does.Contain("MarkSourceRendererFailed(ownerId)"));
            Assert.That(bridge, Does.Contain("RendererAvailableFor(snapshot.OwnerId)"));
            Assert.That(bridge, Does.Contain("ReleaseResourcesOnUnityMain("));
            Assert.That(create, Is.GreaterThanOrEqualTo(0));
            Assert.That(ownerTry, Is.GreaterThanOrEqualTo(0));
            Assert.That(ownerTry, Is.LessThan(create),
                "Surface construction must be inside the per-owner failure boundary.");
            Assert.That(ownerCatch, Is.GreaterThan(create));
            Assert.That(apply, Does.Contain("hidden.Fail(ex)"));
            Assert.That(apply, Does.Contain("Surfaces.Remove(owner)"));
        });
    }

    [Test]
    public void NativeRuleVmUsesRegisterFileBudgetsAndBoundedFaultStorage()
    {
        var root = FindHooksRoot();
        var header = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "native_rule_vm.h"));
        var source = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "native_rule_vm.cpp"));
        var cmake = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "CMakeLists.txt"));
        var hookRules = File.ReadAllText(FindNativeHookRulesSource());
        var managed = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatNativeHookRules.cs"));

        Assert.That(header, Does.Contain("kIntegerRegisterCount = 32"));
        Assert.That(header, Does.Contain("kFloatRegisterCount = 16"));
        Assert.That(header, Does.Contain("kPredicateRegisterCount = 16"));
        Assert.That(header, Does.Contain("kDefaultInstructionBudget = 1024"));
        Assert.That(header, Does.Contain("BranchIf"));
        Assert.That(header, Does.Contain("LoadSongPosition"));
        Assert.That(header, Does.Contain("LoadTouchLaneHeldCount"));
        Assert.That(header, Does.Contain("LoadTouchLaneTotalCount"));
        Assert.That(source, Does.Contain("constexpr size_t kFaultCapacity = 64"));
        Assert.That(source, Does.Contain("BudgetExhausted"));
        Assert.That(source, Does.Contain("kDisableAfterFaultCount"));
        Assert.That(source, Does.Contain("ExecutionStatus::Deferred"));
        Assert.That(source, Does.Contain("verified_program.load"));
        Assert.That(source, Does.Contain("verified_instruction_count.load"));
        Assert.That(source, Does.Not.Contain("std::vector"));
        Assert.That(cmake, Does.Contain("core/native_rule_vm.cpp"));
        Assert.That(source, Does.Contain("StArray.RuleVM"));
        Assert.That(hookRules, Does.Contain("PcCompatVmFaultSnapshotV1"));
        Assert.That(hookRules, Does.Contain("static_assert(sizeof(PcCompatVmFaultSnapshotV1) == 220)"));
        Assert.That(hookRules, Does.Contain("modmanager_pccompat_read_vm_fault_snapshot"));
        Assert.That(managed, Does.Contain("GetLatestVmFault()"));
    }

    [Test]
    public void DynamicDispatcherArenaPlansUniqueTargetsBeforePhysicalInstall()
    {
        var source = File.ReadAllText(FindNativeHookRulesSource());
        var allocation = source.IndexOf(
            "allocate_dispatcher_batch_locked(allocation_batch, allocation_error)",
            StringComparison.Ordinal);
        var planned = source.IndexOf(
            "slot->install_planned = true",
            allocation,
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Not.Contain("kMaxDispatcherSlots"));
            Assert.That(source, Does.Not.Contain("DEFINE_PCMOD_DETOURS"));
            Assert.That(source, Does.Contain("struct DispatcherRuntimePage"));
            Assert.That(source, Does.Contain("allocate_dispatcher_batch_locked"));
            Assert.That(source, Does.Contain("required_dispatchers"));
            Assert.That(source, Does.Contain("new_dispatchers"));
            Assert.That(source, Does.Contain("kDispatcherThunkStride = 64"));
            Assert.That(source, Does.Contain("get_dispatcher_abi_spec(abi_kind, abi_spec)"));
            Assert.That(source, Does.Contain("value_class != AbiValueClass::ColorValue"));
            Assert.That(source, Does.Contain("0xd503245fu"), "Every dynamic entry must be a BTI call target.");
            Assert.That(source, Does.Contain("0x72a00000u"), "Indices above 65535 need a high-half MOVK.");
            Assert.That(source, Does.Contain("__builtin___clear_cache"));
            Assert.That(source, Does.Contain("PROT_READ | PROT_WRITE"));
            Assert.That(source, Does.Contain("PROT_READ | PROT_EXEC"));
            Assert.That(source, Does.Not.Contain("PROT_WRITE | PROT_EXEC"));
            Assert.That(allocation, Is.GreaterThanOrEqualTo(0));
            Assert.That(planned, Is.GreaterThan(allocation),
                "The complete thunk batch must be allocated before any slot is marked installable.");
            Assert.That(source, Does.Contain("modmanager_pccompat_get_dispatcher_required_count"));
            Assert.That(source, Does.Contain("modmanager_pccompat_get_dispatcher_blocked_count"));
        });
    }

    [Test]
    public void RuntimeModUnloadRetiresSessionRulesButPreservesProcessLifetimeDispatchers()
    {
        var root = FindHooksRoot();
        var native = File.ReadAllText(FindNativeHookRulesSource());
        var lifecycleHeader = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "core",
            "ui_recipe_lifecycle_runtime.h"));
        var lifecycleSource = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "core",
            "ui_recipe_lifecycle_runtime.cpp"));
        var schedulerHeader = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "core",
            "hud_deadline_scheduler.h"));
        var managedNative = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "StArray.ModManager.Android", "PcCompat",
            "PcCompatNativeHookRules.cs"));
        var bridge = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "StArray.ModManager.Android", "PcCompat",
            "PcCompatDobbyBridge.cs"));
        var runtime = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "xphorror.PcModCompat", "src", "PcCompatRuntime.cs"));
        var plugin = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "xphorror.PcModCompat", "src", "PcCompatModPlugin.cs"));

        var unregister = ExtractMethodBlock(native, "modmanager_pccompat_unload_hook_rules_for_mod");
        var managedUnregister = ExtractMethodBlock(runtime, "private static void UnregisterModCore");
        var managedUnregisterEntry = ExtractMethodBlock(runtime, "internal static void UnregisterMod");
        var pluginUnload = ExtractMethodBlock(plugin, "public void OnUnload");

        Assert.Multiple(() =>
        {
            Assert.That(native, Does.Contain("modmanager_pccompat_unload_hook_rules_for_mod"));
            Assert.That(unregister, Does.Contain("g_lifecycle_operation_lock"));
            Assert.That(unregister, Does.Contain("g_state.bundles.erase"));
            Assert.That(unregister, Does.Contain("rebuild_slots_locked()"));
            Assert.That(unregister, Does.Contain("retire_bundle(bundle_id)"));
            Assert.That(unregister, Does.Contain("discard_bundle_graph(bundle_id)"));
            Assert.That(unregister, Does.Not.Contain("DobbyDestroy"));
            Assert.That(unregister, Does.Not.Contain("munmap"));
            Assert.That(native, Does.Contain("std::shared_ptr<ManagedEventRing>"));
            Assert.That(native, Does.Contain("retired.store(1"));
            Assert.That(native, Does.Contain("in_flight_prefixes"));
            Assert.That(native, Does.Contain("prefix_lifecycle_condition.wait"));
            Assert.That(unregister, Does.Contain("g_managed_prefix_callback_depth != 0"));

            Assert.That(lifecycleHeader, Does.Contain("bool retire_bundle(uint32_t bundle_id)"));
            Assert.That(lifecycleSource, Does.Contain("hud_logic::cancel_presentation_tasks(bundle_id)"));
            Assert.That(lifecycleSource, Does.Contain("state.bundle_id == 0"),
                "Retired lifecycle storage must be reusable across repeated reloads.");
            Assert.That(schedulerHeader, Does.Contain("cancel_presentation_tasks(uint32_t generation)"));

            Assert.That(managedNative, Does.Contain("UnloadHookRulesForModNative"));
            Assert.That(managedNative, Does.Contain("TryUnloadMod"));
            Assert.That(bridge, Does.Contain("LoadedRuntimeRulePathsByMod"));
            Assert.That(bridge, Does.Contain("RegisterNativeRuleBundleRetireSink"));
            Assert.That(runtime, Does.Contain("RegisterNativeRuleBundleRetireSink"));
            Assert.That(managedUnregister, Does.Contain("managed session preserved"));
            Assert.That(managedUnregisterEntry, Does.Contain("s_managedDispatchDepth != 0"));
            Assert.That(managedUnregisterEntry, Does.Contain("lock (ManagedDispatchLifecycleLock)"));
            Assert.That(
                pluginUnload.IndexOf("PcCompatRuntime.UnregisterMod", StringComparison.Ordinal),
                Is.LessThan(pluginUnload.IndexOf("PcCompatUnityHudRuntime.UnregisterSource", StringComparison.Ordinal)),
                "Native callback retirement must precede plugin UI/session teardown.");
            Assert.That(
                managedUnregister.IndexOf("RetireNativeRuleBundle", StringComparison.Ordinal),
                Is.LessThan(managedUnregister.IndexOf("session?.Dispose()", StringComparison.Ordinal)),
                "Native callbacks must be gated before the managed session is disposed.");
        });
    }

    [Test]
    public void UnityHudStablePointMaskCoversAllDisplayedTelemetryOps()
    {
        var source = File.ReadAllText(FindNativeHookRulesSource());
        var mask = ExtractConstantBlock(source, "kUnityHudStablePointMask");

        string[] requiredOps =
        [
            "kRuleOpOverlayShow",
            "kRuleOpOverlayShowPractice",
            "kRuleOpOverlayHide",
            "kRuleOpOverlayUpdatePlayers",
            "kRuleOpPublishMarginSnapshot",
            "kRuleOpOverlayRecordHit",
            "kRuleOpOverlayResetJudgement",
            "kRuleOpOverlayRecordFloorMove",
            "kRuleOpOverlayRecordPlayerHit",
            "kRuleOpOverlayRecordDeath",
            "kRuleOpOverlayRecordHitTiming",
            "kRuleOpOverlayPollTelemetry"
        ];

        foreach (var op in requiredOps)
            Assert.That(mask, Does.Contain(op), $"{op} must notify Unity HUD after updating displayed telemetry");
    }

    [Test]
    public void EditorExitRetiresOverlaySessionThroughGp32LifecycleHooks()
    {
        var source = File.ReadAllText(FindNativeHookRulesSource());
        var pointerDispatcher = ExtractMethodBlock(source, "void dispatcher_instance_void1(");
        var gp32Dispatcher = ExtractMethodBlock(source, "void dispatcher_instance_void_int1(");
        var retireSession = ExtractMethodBlock(source, "bool retire_overlay_session(");
        var retireSharedSession = ExtractMethodBlock(source, "void retire_shared_overlay_session(");

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("kTargetKindEditorSwitchToEditMode"));
            Assert.That(source, Does.Contain(
                "slot.type_name == \"scnEditor\" && slot.method_name == \"SwitchToEditMode\""));
            Assert.That(pointerDispatcher, Does.Not.Contain("kTargetKindEditorResetScene"));
            Assert.That(gp32Dispatcher, Does.Contain("kTargetKindEditorResetScene"));
            Assert.That(gp32Dispatcher, Does.Contain("kTargetKindEditorSwitchToEditMode"));
            Assert.That(gp32Dispatcher, Does.Contain("args.has_reset_to_editor = true"));
            Assert.That(retireSession, Does.Contain("reset_owner_overlay_session_metrics()"));
            Assert.That(retireSharedSession, Does.Contain("starray::realtime::begin_session("),
                "Retiring a gameplay HUD must advance the realtime session generation.");
            Assert.That(retireSharedSession, Does.Contain("reset_shared_overlay_session_facts()"));
            Assert.That(retireSession, Does.Contain("visible.exchange(0"),
                "ResetScene and SwitchToEditMode may nest, so retirement must be idempotent.");
        });
    }

    [Test]
    public void ResourceChangerPlanetBridgeKeepsJipperWhiteTextureAndTailSetter()
    {
        var source = File.ReadAllText(FindNativeHookRulesSource());

        Assert.That(source, Does.Contain("PlanetSprite\", \"set_sprite\", \"System.Void\", {\"UnityEngine.Texture\"}"));
        Assert.That(source, Does.Contain("cache.rd_constants_klass = find_class(cache.assembly_csharp, \"\", \"RDConstants\")"));
        Assert.That(source, Does.Contain("find_field_offset(cache.rd_constants_klass, \"tex_planetWhite\", nullptr)"));
        Assert.That(source, Does.Contain("apply_resource_planet_white_texture(renderer);"));
        Assert.That(source, Does.Contain("PlanetRenderer\", \"SetTailColor\", \"System.Void\", {\"UnityEngine.Color\"}"));
    }

    [Test]
    public void MarginSnapshotHudCallbackIsChangedAndRateGated()
    {
        var source = File.ReadAllText(FindNativeHookRulesSource());

        Assert.That(source, Does.Contain("constexpr int64_t kMarginSnapshotCallbackIntervalMs = 50"));
        Assert.That(source, Does.Contain("bool margin_snapshot_changed = false;"));
        Assert.That(source, Does.Contain("margin_snapshot_changed = publish_margin_snapshot(args.instance);"));
        Assert.That(source, Does.Contain("old_snapshot_count == 0"));
        Assert.That(source, Does.Contain("non_margin_notify_mask == 0"));
    }

    [Test]
    public void HitMarginMirrorPublishesTrackerIndependentlyOfOverlayVisibility()
    {
        var source = File.ReadAllText(FindNativeHookRulesSource());

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("const bool tracker_method ="));
            Assert.That(source, Does.Contain(
                "args.target_kind == kTargetKindMarginTrackerAddHit"));
            Assert.That(source, Does.Contain(
                "publish_hit_margin_snapshot(tracker_method ? args.instance : nullptr)"));
            Assert.That(source, Does.Contain("void *tracker = preferred_tracker;"));
            Assert.That(source, Does.Contain("current_margin_tracker_from_static_array()"));
            Assert.That(source, Does.Not.Contain("PcCompatHitMarginSnapshotV2"));
            Assert.That(source, Does.Not.Contain("modmanager_pccompat_read_hit_margin_snapshot_v2"));
            Assert.That(source, Does.Not.Contain("modmanager_pccompat_get_margin_tracker_index"));
            Assert.That(source, Does.Not.Contain(
                "if (session_active && tracker_method"));
        });
    }

    [Test]
    public void TimelineAndInputSnapshotUseVersionedNativeAbi()
    {
        var source = File.ReadAllText(FindNativeHookRulesSource());
        var managedSnapshotType = typeof(StArray.ModManager.Android.PcCompat.PcCompatNativeHookRules)
            .GetNestedType(
                "OverlaySnapshotNative",
                System.Reflection.BindingFlags.NonPublic);

        Assert.That(managedSnapshotType, Is.Not.Null);
        Assert.That(
            System.Runtime.InteropServices.Marshal.SizeOf(managedSnapshotType!),
            Is.EqualTo(352));
        Assert.That(source, Does.Contain("constexpr uint32_t kOverlaySnapshotAbiVersion = 7"));
        Assert.That(source, Does.Contain("static_assert(sizeof(PcCompatOverlaySnapshotV1) == 352)"));
        Assert.That(source, Does.Contain("constexpr uint32_t kOverlaySnapshotAbiVersionV6 = 6"));
        Assert.That(source, Does.Contain("constexpr uint32_t kOverlaySnapshotV6Size = 288"));
        Assert.That(source, Does.Contain("constexpr uint32_t kOverlaySnapshotAbiVersionV5 = 5"));
        Assert.That(source, Does.Contain("constexpr uint32_t kOverlaySnapshotV5Size = 284"));
        Assert.That(source, Does.Contain("constexpr uint32_t kOverlaySnapshotAbiVersionV4 = 4"));
        Assert.That(source, Does.Contain("constexpr uint32_t kOverlaySnapshotV4Size = 240"));
        Assert.That(source, Does.Contain("constexpr uint32_t kOverlaySnapshotAbiVersionV3 = 3"));
        Assert.That(source, Does.Contain("constexpr uint32_t kOverlaySnapshotV3Size = 236"));
        Assert.That(source, Does.Contain("constexpr uint32_t kOverlaySnapshotAbiVersionV2 = 2"));
        Assert.That(source, Does.Contain("constexpr uint32_t kOverlaySnapshotV2Size = 160"));
        Assert.That(source, Does.Contain("const bool wants_v7"));
        Assert.That(source, Does.Contain("const bool wants_v6"));
        Assert.That(source, Does.Contain("const bool wants_v5"));
        Assert.That(source, Does.Contain("const bool wants_v4"));
        Assert.That(source, Does.Contain("const bool wants_v3"));
        Assert.That(source, Does.Contain("const bool wants_v2"));
        Assert.That(source, Does.Contain("snapshot.timeline_snapshot_count"));
        Assert.That(source, Does.Contain("snapshot.input_held_mask"));
        Assert.That(source, Does.Contain("snapshot.rdc_auto"));
        Assert.That(source, Does.Contain("snapshot.no_fail"));
        Assert.That(source, Does.Contain("snapshot.paused"));
        Assert.That(source, Does.Contain("snapshot.is_game_world"));
        Assert.That(source, Does.Contain("snapshot.song_pitch"));
        Assert.That(source, Does.Contain("snapshot.conductor_add_offset"));
        Assert.That(source, Does.Contain("snapshot.conductor_songposition_minusi"));
        Assert.That(source, Does.Contain("snapshot.is_scn_game"));
        Assert.That(source, Does.Contain("snapshot.game_ready"));
        Assert.That(source, Does.Contain("snapshot.session_epoch"));
        Assert.That(source, Does.Contain("shared.session_epoch.fetch_add"));
        Assert.That(source, Does.Contain(
            "controller_instance != nullptr ? controller_instance : ado_controller"));
        Assert.That(source, Does.Contain(
            "conductor_instance != nullptr ? conductor_instance : ado_conductor"));
        Assert.That(source, Does.Contain(
            "reinterpret_cast<uintptr_t>(controller)"));
        Assert.That(source, Does.Contain("is_game_world != 0"));
        Assert.That(source, Does.Contain("level_maker != nullptr"));
        Assert.That(source, Does.Contain("modmanager_pccompat_get_level_identity"));
        Assert.That(source, Does.Contain("modmanager_pccompat_observe_touch_input"));
        Assert.That(source, Does.Contain("starray::realtime::read_input_snapshot"));
        Assert.That(source, Does.Contain("starray::hud_logic::read_latest_input_snapshot"));
        Assert.That(source, Does.Contain("completed.source_generation >= producer.generation"));
        Assert.That(source, Does.Contain("modmanager_pccompat_read_input_hud_snapshot"));
        Assert.That(source, Does.Contain("kInputHudSnapshotAbiVersion = 1"));
    }

    [Test]
    public void ReversePatchPlanetSpeedUsesDedicatedTelemetryField()
    {
        var root = FindHooksRoot();
        var native = File.ReadAllText(FindNativeHookRulesSource());
        var managedNative = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "StArray.ModManager.Android", "PcCompat",
            "PcCompatNativeHookRules.cs"));
        var bridge = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "StArray.ModManager.Android", "PcCompat",
            "PcCompatDobbyBridge.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(native, Does.Contain("std::atomic<uint32_t> planet_speed_bits"));
            Assert.That(native, Does.Contain("float planet_speed;"));
            Assert.That(native, Does.Contain("snapshot.planet_speed = bits_to_float("));
            Assert.That(managedNative, Does.Contain("public float PlanetSpeed;"));
            Assert.That(managedNative, Does.Contain("PlanetSpeed = native.PlanetSpeed"));
            Assert.That(bridge, Does.Contain("PcCompatGameSnapshot.FromOverlay"));
            Assert.That(bridge, Does.Not.Contain("PlanetSpeed = 0"));
        });

        var refreshStart = native.IndexOf(
            "bool poll_overlay_telemetry(",
            StringComparison.Ordinal);
        var speedInitialization = native.IndexOf(
            "if (initialize_speed)",
            refreshStart,
            StringComparison.Ordinal);
        var speedFieldRead = native.IndexOf(
            "planetary_system_speed_offset",
            refreshStart,
            StringComparison.Ordinal);
        var liveSpeedStore = native.IndexOf(
            "float_to_bits(planet_speed_value)",
            refreshStart,
            StringComparison.Ordinal);
        var startMultiplierStore = native.IndexOf(
            "float_to_bits(multiplier)",
            refreshStart,
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(refreshStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(speedInitialization, Is.GreaterThan(refreshStart));
            Assert.That(speedFieldRead, Is.GreaterThan(refreshStart));
            Assert.That(liveSpeedStore, Is.GreaterThan(refreshStart));
            Assert.That(speedFieldRead, Is.LessThan(speedInitialization),
                "PlanetSpeed must be sampled on every timeline poll.");
            Assert.That(liveSpeedStore, Is.LessThan(speedInitialization),
                "PlanetSpeed publication must not be guarded by one-time session initialization.");
            Assert.That(startMultiplierStore, Is.GreaterThan(speedInitialization),
                "SpeedMultiplier remains the session-start multiplier used by play stats.");
        });
    }

    [Test]
    public void SharedGameplaySnapshotPublishesExplicitValidityAndObjectRoots()
    {
        var root = FindRepositoryRoot();
        var native = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "pccompat_hook_rules.cpp"));
        var managed = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatNativeHookRules.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(native, Does.Contain("constexpr uint32_t kOverlaySnapshotAbiVersion = 7"));
            Assert.That(native, Does.Contain("uint64_t valid_game_snapshot_fields;"));
            Assert.That(native, Does.Contain("uint64_t controller_pointer;"));
            Assert.That(native, Does.Contain("uint64_t conductor_pointer;"));
            Assert.That(native, Does.Contain("uint64_t level_maker_pointer;"));
            Assert.That(native, Does.Contain("uint64_t current_floor_pointer;"));
            Assert.That(native, Does.Contain("uint64_t first_floor_pointer;"));
            Assert.That(native, Does.Contain("uint64_t song_pointer;"));
            Assert.That(native, Does.Contain("uint64_t planetary_system_pointer;"));
            Assert.That(managed, Does.Contain("private const uint OverlaySnapshotAbiVersion = 7"));
            Assert.That(managed, Does.Contain("public ulong ValidGameSnapshotFields;"));
            Assert.That(managed, Does.Contain("public ulong ControllerPointer;"));
            Assert.That(managed, Does.Contain("ValidGameSnapshotFields ="));
            Assert.That(managed, Does.Contain("ControllerPointer = checked((long)native.ControllerPointer)"));
        });
    }

    [Test]
    public void ManagedUnityMainFrameDrivesSharedTelemetryWithoutDependingOnModHookInstallation()
    {
        var native = File.ReadAllText(FindNativeHookRulesSource());
        var root = FindRepositoryRoot();
        var managed = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatManagedSelfRenderBridge.cs"));

        var frameStart = managed.IndexOf("private static void OnManagedFrame()", StringComparison.Ordinal);
        var frameEnd = managed.IndexOf(
            "private static int OnManagedPrefix(",
            frameStart,
            StringComparison.Ordinal);
        Assert.That(frameStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(frameEnd, Is.GreaterThan(frameStart));
        var frame = managed[frameStart..frameEnd];

        Assert.Multiple(() =>
        {
            Assert.That(native, Does.Contain("int modmanager_pccompat_poll_shared_game_snapshot()"));
            Assert.That(native, Does.Contain("OwnerOverlayScope scope(&g_legacy_overlay_session);"));
            Assert.That(native, Does.Contain("poll_overlay_telemetry(nullptr, false)"));
            Assert.That(managed, Does.Contain(
                "EntryPoint = \"modmanager_pccompat_poll_shared_game_snapshot\""));
            Assert.That(frame, Does.Contain("_ = PollSharedGameSnapshotNative();"));
            Assert.That(
                frame.IndexOf("PollSharedGameSnapshotNative", StringComparison.Ordinal),
                Is.LessThan(frame.IndexOf("PcCompatRuntime.DispatchManagedFrame", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void OptionalTelemetryAbiGroupsCannotDisableCoreGameplaySnapshot()
    {
        var source = File.ReadAllText(FindNativeHookRulesSource());
        var cache = ExtractMethodBlock(
            source,
            "bool ensure_telemetry_runtime_cache(std::string &error) {");
        var floorMetadata = ExtractMethodBlock(source, "bool refresh_floor_metadata(");

        Assert.Multiple(() =>
        {
            Assert.That(cache, Does.Contain(
                "core gameplay classes for overlay telemetry are unavailable"));
            Assert.That(cache, Does.Contain(
                "core gameplay fields for overlay telemetry are unavailable"));
            Assert.That(cache, Does.Contain("const auto resolve_optional = []("));
            Assert.That(cache, Does.Not.Contain(
                "cache.ffx_checkpoint_klass == nullptr ||"));
            Assert.That(cache, Does.Not.Contain(
                "cache.audio_source_klass == nullptr ||"));
            Assert.That(cache, Does.Not.Contain(
                "cache.planetary_system_klass == nullptr ||"));
            Assert.That(floorMetadata, Does.Contain(
                "cache.component_get_component != nullptr &&"));
            Assert.That(floorMetadata, Does.Contain(
                "cache.ffx_checkpoint_type_object != nullptr"));
        });
    }

    [Test]
    public void PlayerControlTelemetryUsesMetadataResolvedSharedDispatcher()
    {
        var source = File.ReadAllText(FindNativeHookRulesSource());

        Assert.That(source, Does.Contain("slot.method_name == \"PlayerControl_Update\""));
        Assert.That(source, Does.Contain("kTargetKindControllerPlayerControlUpdate"));
        Assert.That(source, Does.Contain("find_method_by_identity(cache.scr_conductor_klass"));
        Assert.That(source, Does.Contain("poll_overlay_telemetry(args.instance, false)"));
        Assert.That(source, Does.Not.Contain("PlayerControl_Update RVA"));
    }

    [Test]
    public void MetadataResolverUsesTheMappedIl2CppRuntimeAfterInitialization()
    {
        var source = File.ReadAllText(FindNativeHookRulesSource());

        Assert.That(source, Does.Contain("dl_iterate_phdr(resolve_mapped_dynsym_callback"));
        Assert.That(source, Does.Contain("resolve_mapped_il2cpp_symbol(name)"));
        Assert.That(source, Does.Contain("il2cpp_get_corlib"));
        Assert.That(source, Does.Contain("il2cpp_get_corlib returned null"));
        Assert.That(source, Does.Contain("il2cpp_array_object_header_size"));
        Assert.That(source, Does.Contain("il2cpp_array_length"));
        Assert.That(source, Does.Not.Contain("il2cpp_array_addr_with_size"));
        Assert.That(source, Does.Not.Contain("dlopen(\"libil2cpp.so\", RTLD_NOW | RTLD_LOCAL)"));
        Assert.That(source, Does.Contain("probe_error.empty() ? \"<none>\" : probe_error.c_str()"));
    }

    [Test]
    public void RabbitBridgeConsumesOwnerScopedVirtualBundleSpriteWithoutDiskFallback()
    {
        var source = File.ReadAllText(FindNativeHookRulesSource());

        Assert.That(source, Does.Contain("modmanager_pccompat_publish_resource_changer_sprite"));
        Assert.That(source, Does.Contain("session_generation"));
        Assert.That(source, Does.Contain("g_resource_rabbit_sprite_handle"));
        Assert.That(source, Does.Contain("apply_resource_editor_rabbit(args.instance)"));
        Assert.That(source, Does.Contain("if (current != sprite)"));
        Assert.That(source, Does.Contain("if (set_color != nullptr)"));
        Assert.That(source, Does.Not.Contain("TextureManager\",\n                \"LoadNewSprite\""));
        Assert.That(source, Does.Not.Contain("g_resource_rabbit_sprite_path"));
        Assert.That(source, Does.Not.Contain("Auto.png"));
    }

    [Test]
    public void ResourceChangerTracksEachSceneObjectOnceWithoutLinearHandleScan()
    {
        var source = File.ReadAllText(FindNativeHookRulesSource());

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("g_resource_planet_objects"));
            Assert.That(source, Does.Contain("g_resource_floor_objects"));
            Assert.That(source, Does.Contain("objects.insert(object)"));
            Assert.That(source, Does.Not.Contain(
                "if (g_il2cpp_metadata.gchandle_get_target(handle) == object)"));
            Assert.That(source, Does.Contain("g_resource_planet_objects.clear()"));
            Assert.That(source, Does.Contain("g_resource_floor_objects.clear()"));
        });
    }

    [Test]
    public void AndroidRuntimeDoesNotPackageResourceChangerPngFallback()
    {
        var repository = FindRepositoryRoot();
        var sourceFallback = Path.Combine(
            repository,
            "xphorror.PcModCompat",
            "assets",
            "pc_compat_resources",
            "Auto.png");
        var runtimeFallback = Path.Combine(
            repository,
            "Android",
            "library",
            "src",
            "main",
            "assets",
            "runtime",
            "pc_compat_resources",
            "Auto.png");

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(sourceFallback), Is.False, sourceFallback);
            Assert.That(File.Exists(runtimeFallback), Is.False, runtimeFallback);
            Assert.That(
                File.ReadAllText(Path.Combine(repository, "build_android_single.ps1")),
                Does.Not.Contain("Auto.png"));
        });
    }

    [Test]
    public void ResourceBridgeAppliesRabbitStateColorAndLogoColor()
    {
        var source = File.ReadAllText(FindNativeHookRulesSource());

        Assert.That(source, Does.Contain("resource_rabbit_color(resource_auto_enabled())"));
        Assert.That(source, Does.Contain("invoke_logo_color(color_logo, logo_text, color, true)"));
        Assert.That(source, Does.Contain("invoke_logo_color(color_logo, logo_text, color, false)"));
        Assert.That(source, Does.Contain("g_resource_planet_r.load(std::memory_order_acquire)"));
        Assert.That(source, Does.Contain("g_resource_title_r.load(std::memory_order_acquire)"));
        Assert.That(source, Does.Contain("g_resource_tile_r.load(std::memory_order_acquire)"));
        Assert.That(source, Does.Contain("g_resource_pack_name = resource_pack_name"));
        Assert.That(source, Does.Contain("anchored.y = 0.75f"));
        Assert.That(source, Does.Contain("JipperResourcepack Logo"));
        Assert.That(source, Does.Contain("Education Edition"));
        Assert.That(source, Does.Contain("Vector2Value anchored{-50.0f, 330.0f}"));
        Assert.That(source, Does.Contain("int32_t font_size = 100"));
        Assert.That(source, Does.Not.Contain("logo text bridge is pending"));
    }

    [Test]
    public void ResourceChangerDisableSchedulesUnityMainRestorationForChangedObjects()
    {
        var source = File.ReadAllText(FindNativeHookRulesSource());

        Assert.That(source, Does.Contain("g_resource_pending_restore_mask.fetch_or"));
        Assert.That(source, Does.Contain("modmanager_pccompat_apply_pending_resource_changer_state"));
        Assert.That(source, Does.Contain("g_resource_original_rabbit_sprite_handle"));
        Assert.That(source, Does.Contain("planet_renderer_load_planet_color"));
        Assert.That(source, Does.Contain("ColorValue{0.675f, 0.675f, 0.766f, 1.0f}"));
        Assert.That(source, Does.Contain("scr_logo_text_update_colors"));
        Assert.That(source, Does.Contain("release_resource_scene_handles()"));
    }

    [Test]
    public void ResourceChangerDisabledSetColorPassthroughPreservesIndirectPlanetColorPointer()
    {
        var source = File.ReadAllText(FindNativeHookRulesSource());
        const string signature =
            "void dispatcher_instance_void_ptr_bool(int dispatcher_index, void *self, void *arg0, bool arg1, void *method_info)";
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        var end = source.IndexOf("\n}\n", start, StringComparison.Ordinal);

        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        Assert.That(end, Is.GreaterThan(start));
        var body = source[start..end];

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain(
                "using InstanceVoidPtrBoolFn = void (*)(void *, void *, bool, void *);"));
            Assert.That(body, Does.Contain("capture_raw_pointer(args, 0, arg0);"));
            Assert.That(body, Does.Contain(
                "arg0 = reinterpret_cast<void *>(static_cast<uintptr_t>(args.raw_args[0]));"));
            Assert.That(body, Does.Contain("if (!skip)\n        original(self, arg0, arg1, method_info);"));
            Assert.That(source, Does.Contain("args.target_kind == kTargetKindPlanetRendererSetColor"));
            Assert.That(source, Does.Contain("resource_change_ball_color_enabled()"));
        });
    }

    [Test]
    public void ResourceChangerStartupReplayAndShortSetterUseDeterministicPaths()
    {
        var root = FindHooksRoot();
        var runtime = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "xphorror.PcModCompat", "src", "PcCompatRuntime.cs"));
        var loader = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "StArray.ModManager.Android", "PcCompat",
            "PcCompatResourceBundleLoader.cs"));
        var native = File.ReadAllText(FindNativeHookRulesSource());

        var registerIndex = runtime.IndexOf(
            "PcCompatVirtualBundleRegistry.RegisterSession(",
            StringComparison.Ordinal);
        var replayIndex = runtime.IndexOf(
            "PcCompatResourceChangerRuntime.TryRepublish(manifest.Id)",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(registerIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(replayIndex, Is.GreaterThan(registerIndex));
            Assert.That(loader, Does.Contain("PublishedResourceChangerSpriteKeys"));
            Assert.That(loader, Does.Contain("requested={Volatile.Read(ref s_resourceChangerSpriteRequested)}"));
            Assert.That(loader, Does.Contain("resolved={Volatile.Read(ref s_resourceChangerSpriteResolved)}"));
            Assert.That(loader, Does.Contain("published={Volatile.Read(ref s_resourceChangerSpritePublished)}"));
            Assert.That(loader, Does.Contain("retired={Volatile.Read(ref s_resourceChangerSpriteRetired)}"));
            Assert.That(loader, Does.Contain("failure={Volatile.Read(ref s_resourceChangerSpriteFailure)}"));
            Assert.That(loader, Does.Contain("lastError={lastError}"));
            Assert.That(native, Does.Contain("is_resource_set_rainbow_composite_slot"));
            Assert.That(native, Does.Contain("slot.state = SlotSkippedKnownConflict"));
            Assert.That(native, Does.Contain("short setter covered by composite ResourceSkipPlanetColorOriginal targets"));
            Assert.That(native, Does.Contain(
                "slot.state == SlotResolved || slot.state == SlotHookInstalled ||\n               slot.state == SlotSkippedKnownConflict"));
        });
    }

    [Test]
    public void OverlayStartRulesCannotBeSilentlyBlockedByTelemetryGuards()
    {
        var source = File.ReadAllText(FindNativeHookRulesSource());

        Assert.That(source, Does.Contain("void begin_overlay_session(bool practice"));
        Assert.That(source, Does.Contain("active_overlay_state().visible.store(1"));
        Assert.That(source, Does.Contain("begin_shared_overlay_session("));
        Assert.That(source, Does.Contain("args.seq_id,"));
        Assert.That(source, Does.Contain("args.has_play_args,"));
        Assert.That(source, Does.Contain("args.is_restart);"));
        Assert.That(source, Does.Contain("begin_overlay_session(false);"));
        Assert.That(source, Does.Contain("begin_overlay_session(true);"));
        Assert.That(source, Does.Contain("OwnerOverlayDispatchSnapshot"));
        Assert.That(source, Does.Not.Contain("if (!show_text_path && practice_mode)"));
        Assert.That(source, Does.Not.Contain("if (show_text_path && !practice_mode && custom_level_active)"));
    }

    [Test]
    public void HitMarginMirrorUsesNativeBulkSnapshotAndPublishesBeforeManagedEvents()
    {
        var root = FindHooksRoot();
        var native = File.ReadAllText(FindNativeHookRulesSource());
        var nativeBridge = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "StArray.ModManager.Android", "PcCompat",
            "PcCompatNativeHookRules.cs"));
        var dobbyBridge = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "StArray.ModManager.Android", "PcCompat",
            "PcCompatDobbyBridge.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(native, Does.Contain("PcCompatHitMarginSnapshotV1"));
            Assert.That(native, Does.Contain("modmanager_pccompat_read_hit_margin_snapshot"));
            Assert.That(native, Does.Contain("hitMarginsCount"));
            Assert.That(native, Does.Contain("marginTrackers"));
            Assert.That(native, Does.Contain("g_il2cpp_metadata.field_static_get_value"));
            Assert.That(native, Does.Contain("g_il2cpp_metadata.array_object_header_size"));
            Assert.That(native, Does.Not.Contain("0x18"));
            Assert.That(nativeBridge, Does.Contain("TryReadHitMarginSnapshot"));
            Assert.That(dobbyBridge, Does.Not.Contain("s_hitMarginsTrackerConstructor"));
            Assert.That(dobbyBridge, Does.Not.Contain("s_hitMarginsIndexerProperty"));
            Assert.That(dobbyBridge, Does.Not.Contain("GetValue(il2cppArray"));
        });

        var afterOpsStart = native.IndexOf("void run_shared_after_ops", StringComparison.Ordinal);
        var afterOpsEnd = native.IndexOf("void report_missing_original", afterOpsStart, StringComparison.Ordinal);
        var afterOps = native.Substring(afterOpsStart, afterOpsEnd - afterOpsStart);
        Assert.That(
            afterOps.IndexOf("publish_hit_margin_snapshot", StringComparison.Ordinal),
            Is.LessThan(afterOps.IndexOf("enqueue_managed_event_rules", StringComparison.Ordinal)),
            "managed callbacks must only be queued after native state publication");
    }

    [Test]
    public void UiRecipeBinaryIsValidatedBeforeNativeRuntimeLoading()
    {
        var root = FindHooksRoot();
        var native = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "core",
            "pccompat_recipe_binary.cpp"));
        var cmake = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "CMakeLists.txt"));
        var managed = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "xphorror.PcModCompat", "src", "PcCompatUiRecipeBinary.cs"));
        var cache = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "xphorror.PcModCompat", "src", "PcCompatRecipeBundleCache.cs"));
        var bridge = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "StArray.ModManager.Android", "PcCompat", "PcCompatDobbyBridge.cs"));

        Assert.That(managed, Does.Contain("XPHUIRCP"));
        Assert.That(managed, Does.Contain("ComputeCrc32"));
        Assert.That(cache, Does.Contain("TryValidate(recipePathInTemp"));
        Assert.That(cache, Does.Contain("ui_recipe.bin"));
        Assert.That(cache, Does.Contain("PcCompatUiRecipeBinary.TryValidate"));
        Assert.That(native, Does.Contain("kMaxFileSize"));
        Assert.That(native, Does.Contain("ui recipe checksum mismatch"));
        Assert.That(native, Does.Contain("ui recipe sections overlap"));
        Assert.That(native, Does.Contain("target.parameter_types"));
        Assert.That(native, Does.Contain("kLifecycleRecordSize = 56"));
        Assert.That(native, Does.Contain("rule_vm::verify_program"));
        Assert.That(managed, Does.Contain("AppendVmInstruction"));
        Assert.That(managed, Does.Contain("VerifyVmInstruction"));
        Assert.That(cmake, Does.Contain("core/pccompat_recipe_binary.cpp"));
        Assert.That(bridge, Does.Contain("TryLoadBinary(binaryPath)"));
        Assert.That(bridge, Does.Contain("falling back to audit JSON"));
    }

    [Test]
    public void HudDeadlineSchedulerSeparatesExactAndExtrapolatedClockQueues()
    {
        var root = FindHooksRoot();
        var header = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "core",
            "hud_deadline_scheduler.h"));
        var source = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "core",
            "hud_deadline_scheduler.cpp"));
        var realtime = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "core",
            "realtime_event_core.cpp"));

        Assert.That(header, Does.Contain("kSchedulerQueueCount = kClockDomainCount * 2"));
        Assert.That(header, Does.Contain("SchedulerAllowAnchorExtrapolation"));
        Assert.That(header, Does.Contain("kMaxScheduledPresentationTasksPerDomain = 64"));
        Assert.That(header, Does.Contain("kPresentationSnapshotHistoryCapacity = 64"));
        Assert.That(source, Does.Contain("DeadlineScheduler::pop_due"));
        Assert.That(source, Does.Contain("DeadlineScheduler::next_wake_raw_ns"));
        Assert.That(source, Does.Contain("publish_presentation_commands"));
        Assert.That(source, Does.Contain("std::try_to_lock"));
        Assert.That(source, Does.Not.Contain("sleep_for"));
        Assert.That(realtime, Does.Contain("wake_generation"));
        Assert.That(realtime, Does.Contain("void notify_waiters()"));
    }

    [Test]
    public void PresentationCommandsUseBoundedVersionedNativeAbi()
    {
        var root = FindHooksRoot();
        var nativeHeader = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "core",
            "pccompat_presentation_abi.h"));
        var nativeSource = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "core",
            "pccompat_presentation_abi.cpp"));
        var sinkSource = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "core",
            "unity_presentation_sink.cpp"));
        var objectSource = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "core",
            "unity_presentation_objects.cpp"));
        var managed = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "StArray.ModManager.Android", "PcCompat",
            "PcCompatNativeHookRules.cs"));
        var cmake = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp",
            "CMakeLists.txt"));

        Assert.That(nativeHeader, Does.Contain("PC_COMPAT_PRESENTATION_ABI_VERSION = 1u"));
        Assert.That(nativeHeader, Does.Contain("PC_COMPAT_PRESENTATION_MAX_COMMANDS = 64u"));
        Assert.That(nativeHeader, Does.Contain("PC_COMPAT_PRESENTATION_ENSURE_GRAPH = 1u"));
        Assert.That(nativeHeader, Does.Contain("PC_COMPAT_PRESENTATION_INVALIDATE_TARGET = 8u"));
        Assert.That(nativeSource, Does.Contain("static_assert(sizeof(PcCompatPresentationCommandV1) == 56)"));
        Assert.That(nativeSource, Does.Contain("static_assert(sizeof(PcCompatPresentationSnapshotV1) == 3636)"));
        Assert.That(nativeSource, Does.Contain("modmanager_pccompat_read_presentation_snapshot"));
        Assert.That(sinkSource, Does.Contain("modmanager_pccompat_read_presentation_sink_stats"));
        Assert.That(sinkSource, Does.Contain("PcCompatPresentationSinkStatsV1) == 44"));
        Assert.That(sinkSource, Does.Contain("PcCompatPresentationSinkStatsV2) == 64"));
        Assert.That(sinkSource, Does.Contain("PcCompatPresentationSinkStatsV3) == 80"));
        Assert.That(sinkSource, Does.Contain("PcCompatPresentationSinkStatsV4) == 108"));
        Assert.That(objectSource, Does.Contain("pccompat_metadata::allocate_object"));
        Assert.That(objectSource, Does.Contain("pccompat_metadata::allocate_reference_array"));
        Assert.That(objectSource, Does.Contain("PC_COMPAT_PRESENTATION_ENSURE_GRAPH"));
        Assert.That(objectSource, Does.Not.Contain("DobbyHook"));
        Assert.That(objectSource, Does.Not.Contain("RVA"));
        Assert.That(managed, Does.Contain("PresentationCommandNativeSize = 56"));
        Assert.That(managed, Does.Contain("PresentationMaxCommands = 64"));
        Assert.That(managed, Does.Contain("PresentationSinkStatsAbiVersion = 4"));
        Assert.That(managed, Does.Contain("OnGUIProcessEventCount = native.OnGUIProcessEventCount"));
        Assert.That(managed, Does.Contain("OnGUIBeginGUICount = native.OnGUIBeginGUICount"));
        Assert.That(managed, Does.Contain("OnGUIDispatchCount = native.OnGUIDispatchCount"));
        Assert.That(managed, Does.Contain("GetPresentationSnapshot()"));
        Assert.That(managed, Does.Contain("GetPresentationSinkStats()"));
        Assert.That(cmake, Does.Contain("core/pccompat_presentation_abi.cpp"));
        Assert.That(cmake, Does.Contain("core/unity_presentation_objects.cpp"));
    }

    [Test]
    public void PresentationUnityApiRejectsFakeNullTargetsBeforeRuntimeInvoke()
    {
        var root = FindHooksRoot();
        var objectSource = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "core",
            "unity_presentation_objects.cpp"));

        Assert.Multiple(() =>
        {
            Assert.That(objectSource, Does.Contain("bool require_alive(void *object, const char *name"));
            Assert.That(objectSource, Does.Contain("Unity object fake-null check is unavailable"));
            Assert.That(objectSource, Does.Contain("return false;"));
            Assert.That(objectSource, Does.Not.Contain("if (cached_ptr_offset_ < 0)\n            return true;"));
            Assert.That(objectSource, Does.Contain("if (!require_alive(game_object, \"GameObject\", error))"));
            Assert.That(objectSource, Does.Contain("if (!require_alive(rect, \"RectTransform\", error))"));
            Assert.That(objectSource, Does.Contain("if (!require_alive(graphic, \"Graphic\", error))"));
            Assert.That(objectSource, Does.Contain("if (!require_alive(text, \"TextMeshProUGUI\", error))"));
        });
    }

    [Test]
    public void PresentationMaterializationRootsPersistentObjectsBeforeYielding()
    {
        var source = File.ReadAllText(Path.Combine(
            FindHooksRoot(),
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "unity_presentation_objects.cpp"));
        var createStep = source.IndexOf("case NodeBuildStep::CreateObject:", StringComparison.Ordinal);
        var transformStep = source.IndexOf("case NodeBuildStep::GetTransform:", createStep, StringComparison.Ordinal);
        var createBody = source.Substring(createStep, transformStep - createStep);
        var createObject = createBody.IndexOf("create_game_object(", StringComparison.Ordinal);
        var rootObject = createBody.IndexOf(
            "root_graph_object(runtime, node.game_object, error)",
            StringComparison.Ordinal);
        var firstYield = createBody.IndexOf("return true;", createObject, StringComparison.Ordinal);
        var rootHelper = source.IndexOf("bool root_graph_object(", StringComparison.Ordinal);
        var buildEnd = source.IndexOf("bool initialize_node_step(", transformStep, StringComparison.Ordinal);
        var buildBody = source.Substring(createStep, buildEnd - createStep);
        var activateStart = source.IndexOf("bool activate_canvas_node(", buildEnd, StringComparison.Ordinal);
        var activateEnd = source.IndexOf("MaterializationResult materialize_graph_step(", activateStart, StringComparison.Ordinal);
        var activateBody = source.Substring(activateStart, activateEnd - activateStart);
        const string rootRectTransform = "root_graph_object(runtime, node.rect_transform, error)";
        var rootedRectTransformCount =
            buildBody.Split(rootRectTransform, StringSplitOptions.None).Length - 1;

        Assert.Multiple(() =>
        {
            Assert.That(createStep, Is.GreaterThanOrEqualTo(0));
            Assert.That(transformStep, Is.GreaterThan(createStep));
            Assert.That(rootHelper, Is.GreaterThanOrEqualTo(0));
            Assert.That(rootObject, Is.GreaterThan(createObject));
            Assert.That(firstYield, Is.GreaterThan(rootObject),
                "a newly constructed IL2CPP GameObject must be rooted before materialization yields");
            Assert.That(source, Does.Not.Contain("NodeBuildStep::CreateHandle"));
            Assert.That(rootedRectTransformCount, Is.EqualTo(2));
            Assert.That(buildBody, Does.Contain("root_graph_object(runtime, node.content_size_fitter, error)"));
            Assert.That(buildBody, Does.Contain("root_graph_object(runtime, node.image, error)"));
            Assert.That(buildBody, Does.Contain("root_graph_object(runtime, node.raw_image, error)"));
            Assert.That(buildBody, Does.Contain("root_graph_object(runtime, node.text, error)"));
            Assert.That(buildBody, Does.Contain("root_graph_object(runtime, node.canvas_renderer, error)"));
            Assert.That(activateBody, Does.Contain("root_graph_object(runtime, node.canvas, error)"));
            Assert.That(activateBody, Does.Contain("root_graph_object(runtime, node.canvas_scaler, error)"));
        });
    }

    [Test]
    public void NativeRecipePresentationDoesNotCallSetActiveFromCanvasCallbacks()
    {
        var root = FindHooksRoot();
        var objectSource = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "core",
            "unity_presentation_objects.cpp"));
        var sourceWithoutDefinition = objectSource.Replace(
            "bool set_active(void *game_object, bool active, std::string &error)",
            "bool set_active_definition_removed(void *game_object, bool active, std::string &error)");

        Assert.Multiple(() =>
        {
            Assert.That(objectSource, Does.Contain("Do not call SetActive from Canvas callbacks"));
            Assert.That(objectSource, Does.Contain("destroy_graph_objects(*runtime);"));
            Assert.That(sourceWithoutDefinition, Does.Not.Contain("g_unity_api.set_active("));
        });
    }

    [Test]
    public void PresentationSinkUsesMetadataAndPermanentHookBrokerOnly()
    {
        var root = FindHooksRoot();
        var sink = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "core",
            "unity_presentation_sink.cpp"));
        var resolver = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "core",
            "pccompat_metadata_resolver.h"));
        var rules = File.ReadAllText(FindNativeHookRulesSource());

        Assert.That(sink, Does.Contain("Canvas.SendPreWillRenderCanvases"));
        Assert.That(sink, Does.Contain("CanvasUpdateRegistry.PerformUpdate"));
        Assert.That(sink, Does.Contain("pccompat_metadata::resolve_method"));
        Assert.That(sink, Does.Contain("modmanager_hook_broker_install"));
        Assert.That(sink, Does.Contain("read_next_presentation_snapshot"));
        Assert.That(sink, Does.Contain("invalidate_all_runtime_graphs_on_unity_main"));
        Assert.That(sink, Does.Contain("modmanager_pccompat_set_unity_main_work_callback"));
        Assert.That(sink, Does.Contain("modmanager_pccompat_ensure_presentation_sink"));
        Assert.That(sink, Does.Contain("modmanager_pccompat_is_presentation_sink_installed"));
        Assert.That(sink, Does.Not.Contain("modmanager_pccompat_request_presentation_sink_install"));
        Assert.That(sink, Does.Contain("modmanager_pccompat_request_unity_main_work"));
        Assert.That(sink, Does.Contain("modmanager_pccompat_set_ui_resource_resolver"));
        Assert.That(sink, Does.Contain("modmanager_pccompat_refresh_ui_resources"));
        Assert.That(sink, Does.Contain("g_unity_main_work_requested.exchange(0"));
        Assert.That(sink, Does.Contain("using StaticVoid0Fn = void (*)(void *);"));
        Assert.That(sink, Does.Contain("using InstanceVoid0Fn = void (*)(void *, void *);"));
        Assert.That(sink, Does.Contain(
            "using ProcessEventFn = void (*)(int, void *, bool *, void *);"));
        Assert.That(sink, Does.Contain(
            "using BeginGUIFn = void (*)(int, int, int, void *);"));
        Assert.That(sink, Does.Contain("canvas_send_pre_will_render_canvases(void *method_info)"));
        Assert.That(sink, Does.Contain("original(method_info);"));
        Assert.That(sink, Does.Contain("original(instance, method_info);"));
        Assert.That(sink, Does.Contain(
            "original(event_id, native_event, result, method_info);"));
        Assert.That(sink, Does.Contain(
            "original(skin_mode, instance_id, use_guilayout, method_info);"));
        Assert.That(sink, Does.Not.Contain("DobbyHook"));
        Assert.That(sink, Does.Not.Contain("RVA"));
        Assert.That(resolver, Does.Contain("MethodIdentity"));
        Assert.That(resolver, Does.Not.Contain("resolve_method_by_name_count"));
        Assert.That(rules, Does.Not.Contain("method name/count resolve"));
        Assert.That(rules, Does.Contain("ui_recipe_runtime::register_bundle"));
        Assert.That(rules, Does.Contain("unity_presentation_sink::register_bundle_graph"));
        Assert.That(rules, Does.Contain("modmanager_pccompat_get_loaded_ui_object_node_count"));
        Assert.That(rules, Does.Contain("modmanager_pccompat_get_loaded_ui_resource_binding_count"));
        Assert.That(rules, Does.Contain("unity_presentation_sink::ensure_installed"));
    }

    [Test]
    public void ManagedOnGuiFallbackBorrowsOneStableInitializedBeginGuiHost()
    {
        var sink = File.ReadAllText(Path.Combine(
            FindHooksRoot(),
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "unity_presentation_sink.cpp"));

        Assert.Multiple(() =>
        {
            Assert.That(sink, Does.Contain(".method_name = \"ProcessEvent\""));
            Assert.That(sink, Does.Contain(".return_type = \"System.Void\""));
            Assert.That(sink, Does.Contain(
                ".parameter_types = {\"System.Int32\", \"System.IntPtr\", \"System.Boolean&\"}"));
            Assert.That(sink, Does.Contain(".method_name = \"BeginGUI\""));
            Assert.That(sink, Does.Contain(
                ".parameter_types = {\"System.Int32\", \"System.Int32\", \"System.Int32\"}"));
            Assert.That(sink, Does.Not.Contain(
                "int gui_utility_process_event(int event_id"));
        });

        var processStart = sink.IndexOf(
            "void gui_utility_process_event(",
            StringComparison.Ordinal);
        var beginStart = sink.IndexOf(
            "void gui_utility_begin_gui(",
            processStart,
            StringComparison.Ordinal);
        var processBody = sink.Substring(processStart, beginStart - processStart);
        var originalCall = beginStart < 0
            ? -1
            : sink.IndexOf(
                "original(skin_mode, instance_id, use_guilayout, method_info);",
                beginStart,
                StringComparison.Ordinal);
        var dispatchCall = originalCall < 0
            ? -1
            : sink.IndexOf("dispatch_managed_ongui();", originalCall, StringComparison.Ordinal);
        var beginInstall = sink.IndexOf(
            "\"PcCompat:UnityPresentationSink:GUIUtility.BeginGUI\"",
            StringComparison.Ordinal);
        var processResolve = sink.IndexOf(
            ".method_name = \"ProcessEvent\"",
            beginInstall,
            StringComparison.Ordinal);
        var processInstall = sink.IndexOf(
            "\"PcCompat:UnityPresentationSink:GUIUtility.ProcessEvent\"",
            StringComparison.Ordinal);
        var hookPublication = sink.IndexOf(
            "g_ongui_hook.store(1, std::memory_order_release);",
            processInstall,
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(processStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(beginStart, Is.GreaterThan(processStart));
            Assert.That(processBody, Does.Not.Contain("dispatch_managed_ongui();"));
            Assert.That(processBody, Does.Not.Contain("dispatch_managed_ongui();"));
            Assert.That(originalCall, Is.GreaterThan(beginStart));
            Assert.That(dispatchCall, Is.GreaterThan(originalCall));
            Assert.That(sink, Does.Contain("if (use_guilayout == 0)"));
            Assert.That(sink, Does.Contain("g_ongui_borrowed_instance_id"));
            Assert.That(sink, Does.Contain("kOnGUIBorrowedHostReselectNs"));
            Assert.That(sink, Does.Contain("selected_instance != instance_id"));
            Assert.That(sink, Does.Contain("steady_now_ns() - last_dispatch_ns"));
            Assert.That(sink, Does.Not.Contain("t_ongui_dispatched_generation"));
            Assert.That(sink, Does.Contain("g_ongui_enabled"));
            Assert.That(sink, Does.Contain(
                "modmanager_pccompat_set_managed_ongui_enabled"));
            Assert.That(sink, Does.Contain("g_ongui_process_event_count"));
            Assert.That(sink, Does.Contain("g_ongui_begin_gui_count"));
            Assert.That(sink, Does.Contain("g_ongui_dispatch_count"));
            Assert.That(sink, Does.Contain("skinMode=%d useGUILayout=%d"));
            Assert.That(sink, Does.Contain("gapUs=%lld"));
            Assert.That(beginInstall, Is.GreaterThanOrEqualTo(0));
            Assert.That(processResolve, Is.GreaterThan(beginInstall),
                "optional ProcessEvent telemetry must be resolved after the required BeginGUI hook is live");
            Assert.That(sink, Does.Contain("GUIUtility.ProcessEvent telemetry resolve failed:"));
            Assert.That(processInstall, Is.GreaterThan(beginInstall));
            Assert.That(hookPublication, Is.GreaterThan(processInstall));
        });
    }

    [Test]
    public void PresentationSinkDispatchesManagedFramesFromLivePresentationOpportunities()
    {
        var root = FindHooksRoot();
        var sink = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "core",
            "unity_presentation_sink.cpp"));

        Assert.Multiple(() =>
        {
            Assert.That(sink, Does.Contain("void dispatch_managed_frame()"));
            Assert.That(sink, Does.Contain("g_managed_frame_active.exchange"));
            Assert.That(sink, Does.Contain("kManagedPendingIntervalNs"));
            Assert.That(sink, Does.Not.Contain("g_managed_frame_last_frame_count"));
            Assert.That(sink, Does.Not.Contain("read_latest_clock_anchor(anchor)"));
            Assert.That(sink, Does.Not.Contain("ClockAnchorFrameCount"));
            Assert.That(sink, Does.Contain("if (resolve_and_install_primary(primary_error))"));
            Assert.That(sink, Does.Contain("if (resolve_and_install_fallback(fallback_error))"));
        });
    }

    [Test]
    public void PresentationSinkBudgetsRecipeCommandsWithoutAckingPartialSnapshots()
    {
        var root = FindHooksRoot();
        var sink = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "core",
            "unity_presentation_sink.cpp"));

        Assert.Multiple(() =>
        {
            Assert.That(sink, Does.Contain("kMaxPresentationCommandsPerOpportunity = 16"));
            Assert.That(sink, Does.Contain("g_pending_snapshot"));
            Assert.That(sink, Does.Contain("g_pending_command_offset"));
            Assert.That(sink, Does.Contain("consume_snapshot_slice("));
            Assert.That(sink, Does.Contain("consume_snapshot_range("));
            Assert.That(sink, Does.Contain("if (!result.completed)"));
            Assert.That(sink, Does.Contain("acknowledge_presentation_generation("));
            Assert.That(sink, Does.Contain("handle_pending_snapshot_superseded"));
            Assert.That(sink, Does.Contain("pending presentation superseded by history gap"));
            Assert.That(sink, Does.Not.Contain("PresentationSnapshot slice = snapshot"));
            Assert.That(sink, Does.Not.Contain("slice.commands[index]"));
        });
    }

    [Test]
    public void PresentationObjectMaterializationIsIncrementalAndBlocksFollowingCommands()
    {
        var root = FindHooksRoot();
        var objectSource = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "core",
            "unity_presentation_objects.cpp"));
        var objectHeader = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "core",
            "unity_presentation_objects.h"));
        var sink = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "core",
            "unity_presentation_sink.cpp"));

        Assert.Multiple(() =>
        {
            Assert.That(objectSource, Does.Contain("kMaterializationUnityOperationsPerOpportunity = 12"));
            Assert.That(objectSource, Does.Contain("kMaxResourceBindingsPerOpportunity = 4"));
            Assert.That(objectSource, Does.Contain("kMaxRetiredGraphsPerOpportunity = 4"));
            Assert.That(objectSource, Does.Contain("struct FixedBatch"));
            Assert.That(objectSource, Does.Contain(
                "FixedBatch<ResourceResolveRequest, kMaxResourceBindingsPerOpportunity>"));
            Assert.That(objectSource, Does.Contain(
                "FixedBatch<GraphRuntime, kMaxRetiredGraphsPerOpportunity> retired"));
            Assert.That(objectSource, Does.Not.Contain(
                "retired.reserve(kMaxRetiredGraphsPerOpportunity)"));
            Assert.That(objectSource, Does.Not.Contain(
                "requests.reserve(kMaxResourceBindingsPerOpportunity)"));
            Assert.That(objectSource, Does.Contain("enum class MaterializationPhase"));
            Assert.That(objectSource, Does.Contain("MaterializationPhase::CreateNodes"));
            Assert.That(objectSource, Does.Contain("MaterializationPhase::InitializeNodes"));
            Assert.That(objectSource, Does.Contain("MaterializationPhase::ActivateCanvases"));
            Assert.That(objectSource, Does.Contain("materialization_order"));
            Assert.That(objectSource, Does.Contain("materialize_graph_step("));
            Assert.That(objectSource, Does.Contain("bool validate_existing"));
            Assert.That(objectSource, Does.Contain("if (!validate_existing)"));
            Assert.That(objectSource, Does.Contain("command.command_type == PC_COMPAT_PRESENTATION_ENSURE_GRAPH"));
            Assert.That(objectSource, Does.Contain("if (!apply_presentation_command(command, materialization_budget))"));
            Assert.That(objectSource, Does.Contain("result.deferred = true"));
            Assert.That(objectSource, Does.Contain("graph.materialization_phase = MaterializationPhase::Idle"));
            Assert.That(objectSource, Does.Contain("release_graph_handles(graph)"));
            Assert.That(objectSource, Does.Contain("node.parent_id != 0 && node.parented"));
            Assert.That(objectSource, Does.Contain("node.parented = true"));
            Assert.That(objectSource, Does.Contain("g_resource_resolution_pending"));
            Assert.That(objectSource, Does.Contain("drain_pending_resources_on_unity_main"));
            Assert.That(objectHeader, Does.Contain("SnapshotRangeConsumeResult"));
            Assert.That(objectHeader, Does.Contain("uint32_t consumed_commands = 0"));
            Assert.That(objectHeader, Does.Contain("bool deferred = false"));
            Assert.That(sink, Does.Contain("range_result.consumed_commands"));
            Assert.That(sink, Does.Contain("range_result.deferred || next_index < total"));
            Assert.That(sink, Does.Contain("g_pending_command_offset = next_index"));
            Assert.That(sink, Does.Contain("unity_presentation_objects::drain_pending_resources_on_unity_main()"));
        });
    }

    [Test]
    public void AssetBundleSchedulerUsesBoundedOnDemandUnityMainQueue()
    {
        var root = FindHooksRoot();
        var loader = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "StArray.ModManager.Android", "PcCompat",
            "PcCompatResourceBundleLoader.cs"));
        var sink = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp", "core",
            "unity_presentation_sink.cpp"));

        Assert.That(loader, Does.Contain("WorkQueueCapacity = 64"));
        Assert.That(loader, Does.Contain("MaxWorkItemsPerUnityMainPass = 1"));
        Assert.That(loader, Does.Contain("ContinuationQueueCapacity = 2048"));
        Assert.That(loader, Does.Contain("MaxContinuationsPerUnityMainPass = 16"));
        Assert.That(loader, Does.Contain("PcCompatUnityMainWorkQueue"));
        Assert.That(loader, Does.Contain("RegisterContinuationScheduler("));
        Assert.That(loader, Does.Contain("TryScheduleUnityMainContinuation"));
        Assert.That(loader, Does.Contain(
            "ContinuationQueue.Drain(MaxContinuationsPerUnityMainPass)"));
        Assert.That(loader, Does.Contain("modmanager_pccompat_request_unity_main_work"));
        Assert.That(loader, Does.Contain("PcCompatResourceRecipe.TryVerifyCandidateFile"));
        Assert.That(loader, Does.Contain("OnResolveUiResource"));
        Assert.That(loader, Does.Contain("assetName"));
        Assert.That(loader, Does.Contain("RefreshUiResourcesNative"));
        Assert.That(loader, Does.Contain("CompleteBundleLoad"));
        Assert.That(loader, Does.Not.Contain("ManualResetEvent"));
        Assert.That(loader, Does.Not.Contain("Task.Wait"));
        var scheduleStart = loader.IndexOf(
            "public static bool TryScheduleUnityMainWork",
            StringComparison.Ordinal);
        var scheduleEnd = loader.IndexOf(
            "private static object AcquireManagedBundleProxy",
            scheduleStart,
            StringComparison.Ordinal);
        Assert.That(scheduleStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(scheduleEnd, Is.GreaterThan(scheduleStart));
        var scheduleBlock = loader[scheduleStart..scheduleEnd];
        var readinessIndex = scheduleBlock.IndexOf(
            "PresentationSinkReadyOrRequested()",
            StringComparison.Ordinal);
        var enqueueIndex = scheduleBlock.IndexOf(
            "WorkQueue.TryEnqueue(work)",
            StringComparison.Ordinal);
        Assert.That(readinessIndex, Is.GreaterThanOrEqualTo(0),
            "MOD finalization must request the permanent UnityMain sink before its pump");
        Assert.That(enqueueIndex, Is.GreaterThan(readinessIndex));
        Assert.That(loader, Does.Not.Contain("EnsurePresentationSinkNative"));
        Assert.That(loader, Does.Contain("RequestPresentationSinkInstallNative"));
        Assert.That(loader, Does.Contain("IsPresentationSinkInstalledNative"));
        Assert.That(sink, Does.Contain("g_unity_main_work_requested.exchange(0"));
        Assert.That(sink, Does.Contain("consume_requested_unity_main_work();"));
    }

    private static string ExtractConstantBlock(string source, string constantName)
    {
        var start = source.IndexOf(constantName, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"{constantName} not found");
        var end = source.IndexOf(';', start);
        Assert.That(end, Is.GreaterThan(start), $"{constantName} declaration is not terminated");
        return source[start..end];
    }

    private static string ExtractMethodBlock(string source, string methodName)
    {
        var start = source.IndexOf(methodName, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"{methodName} not found");
        var openingBrace = source.IndexOf('{', start);
        Assert.That(openingBrace, Is.GreaterThan(start), $"{methodName} body not found");

        var depth = 0;
        for (var index = openingBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[start..(index + 1)];
        }

        Assert.Fail($"{methodName} body is not terminated");
        return string.Empty;
    }

    private static string FindNativeHookRulesSource()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "Android",
                "library",
                "src",
                "main",
                "cpp",
                "core",
                "pccompat_hook_rules.cpp");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        Assert.Fail("Could not find pccompat_hook_rules.cpp from test directory");
        return string.Empty;
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

    private static string FindHooksRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "StArray.ModManager")) &&
                Directory.Exists(Path.Combine(directory.FullName, "extra_menu_activity")))
                return directory.FullName;
            directory = directory.Parent;
        }

        Assert.Fail("Could not find ADOFAI_312_HOOKS root from test directory");
        return string.Empty;
    }
}

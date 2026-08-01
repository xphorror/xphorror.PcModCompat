using System.Reflection;

namespace StArray.ModManager.Tests;

public sealed class PcCompatManagedEventResilienceContractTests
{
    private static int s_compiledCallbackValue;

    [Test]
    public void CallbackFailureCircuitBreakerRetriesAfterCooldown()
    {
        var source = File.ReadAllText(Path.Combine(
            FindModManagerRoot(),
            "xphorror.PcModCompat",
            "src",
            "PcCompatManagedCallbackDispatch.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("CallbackRetryDelayTicks = Stopwatch.Frequency"));
            Assert.That(source, Does.Contain("Stopwatch.GetTimestamp() < Volatile.Read(ref _retryAfterTimestamp)"));
            Assert.That(source, Does.Contain("Volatile.Write(ref _retryAfterTimestamp, 0)"));
            Assert.That(source, Does.Contain("backoff={(Disabled ? 1 : 0)} retryMs={retryMilliseconds}"));
            Assert.That(source, Does.Not.Contain(
                "public bool Disabled => Volatile.Read(ref _failureCount) >= MaxCallbackFailures;"));
        });
    }

    [Test]
    public void NativeManagedEventRingPreservesLifecycleBoundariesUnderOverflow()
    {
        var source = File.ReadAllText(Path.Combine(
            FindModManagerRoot(),
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "pccompat_hook_rules.cpp"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("struct ManagedQueuedEvent"));
            Assert.That(source, Does.Contain("bool lifecycle_boundary = false;"));
            Assert.That(source, Does.Contain("is_managed_event_lifecycle_boundary"));
            Assert.That(source, Does.Contain("kManagedEventRingCapacity = 2048"));
            Assert.That(source, Does.Contain("kManagedEventLifecycleReserve = 64"));
            Assert.That(source, Does.Contain("ManagedQueuedEvent pending_lifecycle_event"));
            Assert.That(source, Does.Contain("ring->has_pending_lifecycle_event = true"));
            Assert.That(source, Does.Contain("ring->events[tail] = ring->pending_lifecycle_event"));
            Assert.That(source, Does.Contain(
                "ring->count >= queue.size() - kManagedEventLifecycleReserve"));
            Assert.That(source, Does.Contain(
                "if (lifecycle_boundary && ring->count != 0)"));
            Assert.That(source, Does.Contain("ring->dropped += ring->count"));
            Assert.That(source, Does.Contain("ring->head = 0"));
            Assert.That(source, Does.Contain("ring->count = 0"));
            Assert.That(source, Does.Contain("ring->events[ring->head].event"));
            Assert.That(source, Does.Contain(
                ".lifecycle_boundary = is_managed_event_lifecycle_boundary(target_kind)"));
            Assert.That(source, Does.Contain("ManagedEventDispatchSnapshot"));
            Assert.That(source, Does.Contain("std::atomic_load_explicit("));
        });
    }

    [Test]
    public void DiagnosticsExportIncludesManagedFrameAndHitMirrorProgress()
    {
        var root = FindModManagerRoot();
        var bridge = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatDobbyBridge.cs"));
        var plugin = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "src",
            "PcCompatModPlugin.cs"));
        var frameBridge = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatManagedSelfRenderBridge.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(bridge, Does.Contain("hitMirror[attempts="));
            Assert.That(bridge, Does.Contain("lastSuccessAgeMs="));
            Assert.That(bridge, Does.Contain("RefreshHitMarginsCountThrottled();"));
            Assert.That(bridge, Does.Contain("HitMarginsFallbackRefreshIntervalMilliseconds = 100"));
            Assert.That(bridge, Does.Contain("throttled={Interlocked.Read(ref s_hitMarginsRefreshThrottled)}"));
            Assert.That(frameBridge, Does.Contain("avgWorkUs="));
            Assert.That(frameBridge, Does.Contain("over4ms="));
            Assert.That(bridge, Does.Contain("PcCompatManagedSelfRenderBridge.GetDiagnostics()"));
            Assert.That(plugin, Does.Contain("platformRuntime={PcCompatDiagnosticsRuntime.GetPlatformRuntimeStats()}"));
            Assert.That(plugin, Does.Contain("managedRequiresContinuousFrameDispatch="));
        });
    }

    [Test]
    public void ManagedFrameSteadyStateUsesCachedSessionSnapshot()
    {
        var source = File.ReadAllText(Path.Combine(
            FindModManagerRoot(),
            "xphorror.PcModCompat",
            "src",
            "PcCompatRuntime.cs"));
        var dispatchStart = source.IndexOf("public static void DispatchManagedFrame", StringComparison.Ordinal);
        var dispatchEnd = source.IndexOf("public static void DispatchManagedOnGUI", dispatchStart, StringComparison.Ordinal);
        var dispatch = source.Substring(dispatchStart, dispatchEnd - dispatchStart);

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("s_managedFrameSessions"));
            Assert.That(dispatch, Does.Contain("Volatile.Read(ref s_managedFrameSessions)"));
            Assert.That(dispatch, Does.Contain("if (frameGateChanged)"));
            Assert.That(dispatch, Does.Not.Contain("Sessions.Values.Where"));
            Assert.That(dispatch, Does.Not.Contain(
                "Volatile.Write(ref s_managedFrameDispatchActive, 0);\n            UpdateManagedFrameGate();"));
        });
    }

    [Test]
    public void KeyViewerLabelProjectionRunsAfterModePumpAndBeforeFallbackRendering()
    {
        var source = File.ReadAllText(Path.Combine(
            FindModManagerRoot(),
            "xphorror.PcModCompat",
            "src",
            "PcCompatRuntime.cs"));
        var dispatchStart = source.IndexOf(
            "public static void DispatchManagedFrame",
            StringComparison.Ordinal);
        var dispatchEnd = source.IndexOf(
            "public static void DispatchManagedOnGUI",
            dispatchStart,
            StringComparison.Ordinal);
        var dispatch = source.Substring(dispatchStart, dispatchEnd - dispatchStart);
        var labelRuntime = File.ReadAllText(Path.Combine(
            FindModManagerRoot(),
            "xphorror.PcModCompat",
            "src",
            "PcCompatKeyViewerLabelProjectionRuntime.cs"));
        var preview = dispatch.IndexOf(
            "PcCompatKeyViewerPreviewRuntime.DispatchFrame()",
            StringComparison.Ordinal);
        var labels = dispatch.IndexOf(
            "PcCompatKeyViewerLabelProjectionRuntime.DispatchFrame()",
            StringComparison.Ordinal);
        var fallback = dispatch.IndexOf(
            "PcCompatKeyViewerFallbackRuntime.DispatchFrame(deltaTime)",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(preview, Is.GreaterThanOrEqualTo(0));
            Assert.That(labels, Is.GreaterThan(preview));
            Assert.That(fallback, Is.GreaterThan(labels));
            Assert.That(source, Does.Contain(
                "PcCompatKeyViewerLabelProjectionRuntime.HasDemand"));
            Assert.That(labelRuntime, Does.Contain(
                "if (_lastMode == inputMode && now < _nextRefreshTimestamp)"));
            Assert.That(labelRuntime, Does.Not.Contain("_applied &&"));
        });
    }

    [Test]
    public void ManagedOnGuiUsesIndependentSessionSnapshotAndNativeGate()
    {
        var root = FindModManagerRoot();
        var runtime = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "src",
            "PcCompatRuntime.cs"));
        var bridge = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatManagedSelfRenderBridge.cs"));
        var dispatchStart = runtime.IndexOf(
            "public static void DispatchManagedOnGUI",
            StringComparison.Ordinal);
        var dispatchEnd = runtime.IndexOf(
            "private static string ManagedErrorSummary",
            dispatchStart,
            StringComparison.Ordinal);
        var dispatch = runtime.Substring(dispatchStart, dispatchEnd - dispatchStart);
        var gateStart = runtime.IndexOf(
            "private static void UpdateManagedFrameGate",
            StringComparison.Ordinal);
        var gateEnd = runtime.IndexOf(
            "private static bool SetManagedPresentationOwnership",
            gateStart,
            StringComparison.Ordinal);
        var gate = runtime.Substring(gateStart, gateEnd - gateStart);

        Assert.Multiple(() =>
        {
            Assert.That(runtime, Does.Contain("s_managedOnGUISessions"));
            Assert.That(runtime, Does.Contain("s_managedOnGUIGateSink"));
            Assert.That(runtime, Does.Contain("RegisterManagedOnGUIGateSink"));
            Assert.That(dispatch, Does.Contain("Volatile.Read(ref s_managedOnGUISessions)"));
            Assert.That(dispatch, Does.Not.Contain("Volatile.Read(ref s_managedFrameSessions)"));
            Assert.That(dispatch, Does.Contain("requiredOnGUIBefore"));
            Assert.That(dispatch, Does.Contain(
                "requiredOnGUIBefore != session.RequiresOnGUIDispatch"));
            Assert.That(gate, Does.Contain("session.RequiresOnGUIDispatch"));
            Assert.That(gate, Does.Contain("Volatile.Write(ref s_managedOnGUISessions"));
            Assert.That(bridge, Does.Contain(
                "modmanager_pccompat_set_managed_ongui_enabled"));
            Assert.That(bridge, Does.Contain(
                "PcCompatRuntime.RegisterManagedOnGUIGateSink(SetOnGUIEnabled)"));
        });
    }

    [Test]
    public void ManagedCallbacksDrainBeforeCompatUpdateSoJALibMainThreadRunsSameFrame()
    {
        var source = File.ReadAllText(Path.Combine(
            FindModManagerRoot(),
            "xphorror.PcModCompat",
            "src",
            "PcCompatRuntime.cs"));
        var start = source.IndexOf("public static void DispatchManagedFrame", StringComparison.Ordinal);
        var end = source.IndexOf("public static void DispatchManagedOnGUI", start, StringComparison.Ordinal);
        var dispatch = source.Substring(start, end - start);
        var collectBeforeUpdate = dispatch.IndexOf(
            "session.TryCollectManagedCallbacks(ManagedEventCollector);",
            StringComparison.Ordinal);
        var callbackBeforeUpdate = dispatch.IndexOf(
            "ManagedEventCollector.DispatchAll(boxedValueReader);",
            collectBeforeUpdate,
            StringComparison.Ordinal);
        var update = dispatch.IndexOf("session.TryDispatchUpdate(deltaTime)", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(dispatch, Does.Contain("callbacksDispatchedBeforeUpdate"));
            Assert.That(dispatch, Does.Contain("before any MOD Update"));
            Assert.That(collectBeforeUpdate, Is.GreaterThanOrEqualTo(0));
            Assert.That(callbackBeforeUpdate, Is.GreaterThanOrEqualTo(0));
            Assert.That(callbackBeforeUpdate, Is.LessThan(update));
        });
    }

    [Test]
    public void ManagedCallbacksRunInsideTheOwningModExecutionContext()
    {
        var source = File.ReadAllText(Path.Combine(
            FindModManagerRoot(),
            "xphorror.PcModCompat",
            "src",
            "PcCompatManagedModSession.cs"));
        var start = source.IndexOf(
            "public bool TryDispatchManagedCallbacks()",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "private object? ResolveManagedRoleTarget",
            start,
            StringComparison.Ordinal);
        var method = source.Substring(start, end - start);
        var ownerScope = method.IndexOf(
            "using var execution = PcCompatManagedExecutionContext.Enter(_updateContext);",
            StringComparison.Ordinal);
        var dispatch = method.IndexOf("dispatcher.DrainAndDispatch(", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(ownerScope, Is.GreaterThanOrEqualTo(0));
            Assert.That(dispatch, Is.GreaterThan(ownerScope));
        });
    }

    [Test]
    public void ManagedPresentationOwnershipKeepsCallbackPumpAliveWithoutCompatUpdate()
    {
        var source = File.ReadAllText(Path.Combine(
            FindModManagerRoot(),
            "xphorror.PcModCompat",
            "src",
            "PcCompatManagedModSession.cs"));
        var start = source.IndexOf("public bool RequiresFrameDispatch", StringComparison.Ordinal);
        var end = source.IndexOf("internal PcCompatManagedLifecycleState LifecycleState", start, StringComparison.Ordinal);
        var properties = source.Substring(start, end - start);
        var continuousStart = properties.IndexOf(
            "public bool RequiresContinuousFrameDispatch",
            StringComparison.Ordinal);
        var managedStart = properties.IndexOf(
            "public bool RequiresManagedFrameDispatch",
            StringComparison.Ordinal);
        var onGuiStart = properties.IndexOf(
            "public bool RequiresOnGUIDispatch",
            StringComparison.Ordinal);
        var frameGate = properties[..continuousStart];
        var continuousGate = properties[continuousStart..managedStart];
        var managedGate = properties[managedStart..onGuiStart];

        Assert.Multiple(() =>
        {
            Assert.That(frameGate, Does.Contain("ManagedPresentationClaimed ||"));
            Assert.That(continuousGate, Does.Contain("ManagedPresentationClaimed ||"));
            Assert.That(managedGate, Does.Contain("ManagedPresentationClaimed ||"));
        });
    }

    [Test]
    public void ManagedLifecycleUpdateDoesNotAllocateCapturingDelegatePerFrame()
    {
        var source = File.ReadAllText(Path.Combine(
            FindModManagerRoot(),
            "xphorror.PcModCompat",
            "src",
            "PcCompatManagedLifecycle.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("InvokeUpdateExclusive(deltaTime);"));
            Assert.That(source, Does.Contain("private void InvokeUpdateExclusive(float deltaTime)"));
            Assert.That(source, Does.Not.Contain("InvokeExclusive(() => _update(deltaTime))"));
            Assert.That(source, Does.Contain("RuntimeHelpers.PrepareMethod(method.MethodHandle)"));
            Assert.That(source, Does.Contain("RuntimeHelpers.PrepareDelegate(callback)"));
        });
    }

    [Test]
    public void ManagedCallbackBindingsArePreparedBeforeFirstUnityFrame()
    {
        var root = FindModManagerRoot();
        var session = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "src",
            "PcCompatManagedModSession.cs"));
        var dispatcher = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "src",
            "PcCompatManagedCallbackDispatch.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(session, Does.Contain("if (setupCompleted)\n            _callbackDispatcher = BuildCallbackDispatcher();"));
            Assert.That(dispatcher, Does.Contain("TryPrepareMethod(method);"));
            Assert.That(dispatcher, Does.Contain("RuntimeHelpers.PrepareDelegate(_directAction);"));
            Assert.That(dispatcher, Does.Contain("RuntimeHelpers.PrepareDelegate(_compiledInvoker);"));
        });
    }

    [Test]
    public void ManagedEventHotPathReusesReflectionBuffersAndDirectCallsNoArgCallbacks()
    {
        var root = FindModManagerRoot();
        var dispatcher = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "src",
            "PcCompatManagedCallbackDispatch.cs"));
        var platform = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatManagedSelfRenderBridge.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(dispatcher, Does.Contain("private readonly Action? _directAction"));
            Assert.That(dispatcher, Does.Contain("private readonly Action<object?[]>? _compiledInvoker"));
            Assert.That(dispatcher, Does.Contain("private readonly object?[]? _invokeArgs"));
            Assert.That(dispatcher, Does.Not.Contain("args = new object?[_parameters.Length]"));
            Assert.That(dispatcher, Does.Contain("_directAction();"));
            Assert.That(dispatcher, Does.Contain("CompilePointerConstructor"));
            Assert.That(dispatcher, Does.Contain("public const int EventRecordSize = 184"));
            Assert.That(dispatcher, Does.Contain("DispatchSequenceOffset = 144"));
            Assert.That(dispatcher, Does.Contain("PublishEventHitMarginSnapshot("));
            Assert.That(dispatcher, Does.Contain("var counts = MemoryMarshal.Cast<byte, int>(bytes);"));
            Assert.That(dispatcher, Does.Contain("publish(true, counts);"));
            Assert.That(dispatcher, Does.Contain("publish?.Invoke(false, ReadOnlySpan<int>.Empty);"));
            Assert.That(dispatcher, Does.Contain("_lastPublishedHitMarginSnapshotGeneration"));
            Assert.That(dispatcher, Does.Not.Contain("beforeDispatch?.Invoke();"));
            Assert.That(platform, Does.Contain("BoxedValueNameBuffer"));
            Assert.That(platform, Does.Not.Contain("var nameBuffer = new byte[256]"));
            Assert.That(platform, Does.Contain("GetManagedEventRecordSizeNative()"));
            Assert.That(platform, Does.Contain("Managed event ABI mismatch"));
        });
    }

    [Test]
    public void ManagedPostfixEventsRestoreCrossModHookOrderBeforeUpdate()
    {
        var root = FindModManagerRoot();
        var native = File.ReadAllText(Path.Combine(
            root,
            "Android", "library", "src", "main", "cpp", "core", "pccompat_hook_rules.cpp"));
        var dispatcher = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat", "src", "PcCompatManagedCallbackDispatch.cs"));
        var runtime = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat", "src", "PcCompatRuntime.cs"));

        var collect = runtime.IndexOf("session.TryCollectManagedCallbacks", StringComparison.Ordinal);
        var dispatch = runtime.IndexOf("ManagedEventCollector.DispatchAll", collect, StringComparison.Ordinal);
        var update = runtime.IndexOf("session.TryDispatchUpdate(deltaTime)", dispatch, StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(native, Does.Contain("uint64_t dispatch_sequence;"));
            Assert.That(native, Does.Contain("sizeof(PcCompatManagedEventV2) == 184"));
            Assert.That(native, Does.Contain("uint64_t invocation_id;"));
            Assert.That(native, Does.Contain("event.invocation_id = args.invocation_id"));
            Assert.That(native, Does.Contain("g_managed_event_dispatch_sequence.fetch_add"));
            Assert.That(native, Does.Contain("sequence_base + static_cast<uint64_t>(target_index)"));
            Assert.That(native, Does.Contain("event.dispatch_sequence"));
            Assert.That(dispatcher, Does.Contain("PcCompatManagedEventDispatchCollector"));
            Assert.That(dispatcher, Does.Contain("Array.Sort(_entries, 0, _count, Comparer)"));
            Assert.That(collect, Is.GreaterThanOrEqualTo(0));
            Assert.That(dispatch, Is.GreaterThan(collect));
            Assert.That(update, Is.GreaterThan(dispatch));
        });
    }

    [Test]
    public void CompiledCallbackInvokerCanCallPrivateCallbackWithoutReflectionInvoke()
    {
        var bindingType = typeof(Xphorror.PcModCompat.PcCompatManagedCallbackDispatcher)
            .GetNestedType("CallbackBinding", BindingFlags.NonPublic);
        var compiler = bindingType?.GetMethod(
            "CompileCallbackInvoker",
            BindingFlags.NonPublic | BindingFlags.Static);
        var callback = typeof(PcCompatManagedEventResilienceContractTests).GetMethod(
            nameof(PrivateCallback),
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.That(compiler, Is.Not.Null);
        Assert.That(callback, Is.Not.Null);
        var invoke = (Action<object?[]>)compiler!.Invoke(null, [callback, null])!;
        s_compiledCallbackValue = 0;
        invoke([37]);
        Assert.That(s_compiledCallbackValue, Is.EqualTo(37));
    }

    private static void PrivateCallback(int value)
        => s_compiledCallbackValue = value;

    [Test]
    public void ManagedEventDrainSteadyStateAvoidsRuleRegistryLockAndBundleAllocation()
    {
        var source = File.ReadAllText(Path.Combine(
            FindModManagerRoot(),
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "pccompat_hook_rules.cpp"));
        var start = source.IndexOf(
            "int modmanager_pccompat_drain_managed_events",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "void modmanager_pccompat_request_presentation_sink_install()",
            start,
            StringComparison.Ordinal);
        var drain = source.Substring(start, end - start);
        var enqueueStart = source.IndexOf(
            "void enqueue_managed_event_rules(int dispatcher_index, const FixedOpArgs &args) {",
            StringComparison.Ordinal);
        var enqueueEnd = source.IndexOf("FixedOpArgs make_fixed_op_args", enqueueStart, StringComparison.Ordinal);
        var enqueue = source.Substring(enqueueStart, enqueueEnd - enqueueStart);
        var resetStart = source.IndexOf("void reset_managed_event_state_locked()", StringComparison.Ordinal);
        var resetEnd = source.IndexOf("constexpr uint64_t kUnityHudStablePointMask", resetStart, StringComparison.Ordinal);
        var reset = source.Substring(resetStart, resetEnd - resetStart);

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("managed_event_rings_for_mod"));
            Assert.That(source, Does.Contain("g_managed_event_registry_generation"));
            Assert.That(drain, Does.Not.Contain("guard(g_lock)"));
            Assert.That(drain, Does.Not.Contain("std::vector<uint32_t> bundle_ids"));
            Assert.That(drain, Does.Contain(
                "static thread_local std::vector<std::unique_lock<std::mutex>> ring_locks"));
            Assert.That(drain, Does.Contain("ring_locks.clear();"));
            Assert.That(enqueue, Does.Not.Contain("g_managed_event_registry_epoch.fetch_add"));
            Assert.That(reset, Does.Contain("g_managed_event_registry_epoch.fetch_add"));
        });
    }

    [Test]
    public void ManagedEventDrainUsesMultiBatchFrameBudgetForHighRateCallbacks()
    {
        var dispatcher = File.ReadAllText(Path.Combine(
            FindModManagerRoot(),
            "xphorror.PcModCompat",
            "src",
            "PcCompatManagedCallbackDispatch.cs"));
        var plugin = File.ReadAllText(Path.Combine(
            FindModManagerRoot(),
            "xphorror.PcModCompat",
            "src",
            "PcCompatModPlugin.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(dispatcher, Does.Contain("MaxDrainBatchesPerFrame = 8"));
            Assert.That(dispatcher, Does.Contain("for (var batch = 0; batch < MaxDrainBatchesPerFrame; ++batch)"));
            Assert.That(dispatcher, Does.Contain("RecordNativeDropped(nativeDropped);"));
            Assert.That(dispatcher, Does.Contain("_drainBudgetExhaustedFrames"));
            Assert.That(plugin, Does.Contain("nativeDropped:{dispatchStats.NativeDroppedEvents}"));
            Assert.That(plugin, Does.Contain(
                "budgetExhaustedFrames:{dispatchStats.DrainBudgetExhaustedFrames}"));
        });
    }

    [Test]
    public void ManagedSelfRenderActivationPublishesPendingBeforeTearingDownFallback()
    {
        var source = File.ReadAllText(Path.Combine(
            FindModManagerRoot(),
            "xphorror.PcModCompat",
            "src",
            "PcCompatRuntime.cs"));
        var start = source.IndexOf(
            "public static bool TryRequestManagedSelfRender",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "public static void RegisterManagedFrameGateSink",
            start,
            StringComparison.Ordinal);
        var method = source.Substring(start, end - start);

        Assert.Multiple(() =>
        {
            Assert.That(method, Does.Contain("PcCompatKeyViewerFallbackRuntime.Unregister(modId);"));
            Assert.That(
                method.IndexOf("session.RequestActivation();", StringComparison.Ordinal),
                Is.LessThan(method.IndexOf(
                    "PcCompatKeyViewerFallbackRuntime.Unregister(modId);",
                    StringComparison.Ordinal)));
            Assert.That(method, Does.Not.Contain("RegistryChanged?.Invoke();"));
        });
    }

    [Test]
    public void ManagedSelfRenderFrameGateNeverInstallsHooksOnTheUiCallbackThread()
    {
        var root = FindModManagerRoot();
        var bridge = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatManagedSelfRenderBridge.cs"));
        var native = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "pccompat_hook_rules.cpp"));
        var requestStart = native.IndexOf(
            "void modmanager_pccompat_request_presentation_sink_install()",
            StringComparison.Ordinal);
        var requestEnd = native.IndexOf(
            "int modmanager_pccompat_get_managed_event_record_size()",
            requestStart,
            StringComparison.Ordinal);
        var requestMethod = native.Substring(requestStart, requestEnd - requestStart);
        var resourceLoader = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatResourceBundleLoader.cs"));
        var start = bridge.IndexOf("private static void SetFrameEnabled", StringComparison.Ordinal);
        var end = bridge.IndexOf("[UnmanagedCallersOnly", start, StringComparison.Ordinal);
        var setFrameEnabled = bridge.Substring(start, end - start);

        Assert.Multiple(() =>
        {
            Assert.That(bridge, Does.Contain("RequestPresentationSinkInstallNative"));
            Assert.That(bridge, Does.Contain("IsPresentationSinkInstalledNative"));
            Assert.That(setFrameEnabled, Does.Not.Contain("EnsurePresentationSinkNative"));
            Assert.That(native, Does.Contain("g_presentation_install_requested"));
            Assert.That(native, Does.Contain("modmanager_pccompat_request_presentation_sink_install"));
            Assert.That(native, Does.Contain("hook_coordinator_main"));
            Assert.That(requestMethod, Does.Not.Contain("modmanager_pccompat_start_hook_coordinator"));
            Assert.That(requestMethod, Does.Not.Contain("hud_logic::ensure_started"));
            Assert.That(requestMethod, Does.Not.Contain("async_input_bridge::ensure_registered"));
            Assert.That(requestMethod, Does.Contain("start_hook_coordinator_thread_once"));
            Assert.That(requestMethod, Does.Contain("g_presentation_install_requested.exchange"));
            Assert.That(resourceLoader, Does.Contain("PresentationSinkReadyOrRequested"));
            Assert.That(resourceLoader, Does.Not.Contain("EnsurePresentationSinkNative"));
        });
    }

    [Test]
    public void ManagedSelfRenderPendingStatePreventsFallbackFromBeingReRegistered()
    {
        var source = File.ReadAllText(Path.Combine(
            FindModManagerRoot(),
            "xphorror.PcModCompat",
            "src",
            "PcCompatModPlugin.cs"));
        var start = source.IndexOf(
            "private void RefreshKeyViewerPreviewRegistration()",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "private void RenderKeyViewerPreviewStatus()",
            start,
            StringComparison.Ordinal);
        var method = source.Substring(start, end - start);

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("ManagedSelfRenderBlocksCompatibilityPresentation"));
            Assert.That(source, Does.Contain("or { ActivationPending: true }"));
            Assert.That(method, Does.Contain("if (ManagedSelfRenderBlocksCompatibilityPresentation)"));
            Assert.That(method, Does.Contain("PcCompatKeyViewerFallbackRuntime.Unregister(Id);"));
            Assert.That(method, Does.Contain("PcCompatKeyViewerFallbackRuntime.RegisterOrUpdate("));
        });
    }

    private static string FindModManagerRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "xphorror.PcModCompat")) &&
                Directory.Exists(Path.Combine(directory.FullName, "StArray.ModManager.Android")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("StArray.ModManager root not found.");
    }
}

namespace StArray.ModManager.Tests;

public sealed class PcCompatAndroidInputContractTests
{
    [Test]
    public void SettingsImeDiagnosticsIdentifyDearImGuiStateAndAssemblyInstance()
    {
        var root = FindHooksRoot();
        var input = File.ReadAllText(Path.Combine(
            root,
                        "StArray.ModManager.Android",
            "UI",
            "ImGuiInputHandler.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(input, Does.Contain("[DEBUG-settings-surface-v1] ime source=DearImGui"));
            Assert.That(input, Does.Contain("s_pendingWantTextInput"));
            Assert.That(input, Does.Contain("AssemblyLoadContext.GetLoadContext"));
            Assert.That(input, Does.Contain("ManifestModule.ModuleVersionId"));
            Assert.That(input, Does.Contain("lock (ImeGate)"));
            Assert.That(input, Does.Contain("overlayVisible && dearImGuiWant"));
        });
    }

    [Test]
    public void SettingsImeUsesOneOwnerAndClearsPreviousFocusDomain()
    {
        var root = FindHooksRoot();
        var input = File.ReadAllText(Path.Combine(
            root,
                        "StArray.ModManager.Android",
            "UI",
            "ImGuiInputHandler.cs"));
        var platform = File.ReadAllText(Path.Combine(
            root,
                        "StArray.ModManager.Android",
            "UI",
            "AndroidModManagerPlatformServices.cs"));
        var native = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "cimgui_compat.cpp"));
        var bootstrap = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "java",
            "com",
            "fizzd",
            "connectedworlds",
            "editorport",
            "StArrayModManagerBootstrap.java"));
        var settings = File.ReadAllText(Path.Combine(
            root,
                        "xphorror.PcModCompat",
            "src",
            "PcCompatManagedSettingsUnityBackend.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(input, Does.Contain("enum AndroidImeOwner"));
            Assert.That(input, Does.Contain("SetImeOwner(AndroidImeOwner owner)"));
            Assert.That(input, Does.Contain("s_modManagerObservedReleasedFocus"));
            Assert.That(platform, Does.Contain("modmanager_overlay_input_request_focus_release"));
            Assert.That(native, Does.Contain("g_overlay_focus_release_requested"));
            Assert.That(native, Does.Contain("ImGui::ClearActiveID()"));
            Assert.That(bootstrap, Does.Contain("WindowInsets.Type.ime()"));
            Assert.That(bootstrap, Does.Contain("sKeyboardActuallyVisible"));
            Assert.That(settings, Does.Contain("ReleaseInputFocus"));
            Assert.That(settings, Does.Contain("_guiSetKeyboardControl"));
            Assert.That(settings, Does.Contain("_guiSetHotControl"));
        });
    }

    [Test]
    public void OriginalSettingsModalOwnsAndroidTouchKeyboardAndBackBeforeGameplay()
    {
        var root = FindHooksRoot();
        var activity = File.ReadAllText(RequirePrivateActivitySource(root));
        var bootstrap = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "java",
            "com",
            "fizzd",
            "connectedworlds",
            "editorport",
            "StArrayModManagerBootstrap.java"));
        var native = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "cimgui_compat.cpp"));
        var presentationSink = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "unity_presentation_sink.cpp"));
        var managerUi = File.ReadAllText(Path.Combine(
            root,
                        "StArray.ModManager",
            "Manager",
            "ModManagerUI.cs"));
        var asyncInput = File.ReadAllText(Path.Combine(
            root,
            "external",
            "AsyncInput",
            "async_input.c"));

        var touchStart = activity.IndexOf(
            "public boolean dispatchTouchEvent",
            StringComparison.Ordinal);
        var touchEnd = activity.IndexOf(
            "private boolean finishStArrayTouchDispatch",
            touchStart,
            StringComparison.Ordinal);
        Assert.That(touchStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(touchEnd, Is.GreaterThan(touchStart));
        var touchDispatch = activity[touchStart..touchEnd];
        Assert.That(
            touchDispatch.IndexOf("isStArrayModalInputCaptureActive()", StringComparison.Ordinal),
            Is.LessThan(touchDispatch.IndexOf("forwardStArrayMotionEvent(event)", StringComparison.Ordinal)));
        Assert.That(
            touchDispatch.IndexOf("isStArrayModalInputCaptureActive()", StringComparison.Ordinal),
            Is.LessThan(touchDispatch.IndexOf("nativeOnTouchEvent(event", StringComparison.Ordinal)));

        var keyStart = activity.IndexOf("public boolean dispatchKeyEvent", StringComparison.Ordinal);
        var keyEnd = activity.IndexOf("protected void onPause", keyStart, StringComparison.Ordinal);
        var keyDispatch = activity[keyStart..keyEnd];
        var modalKeyStart = keyDispatch.IndexOf(
            "if (isStArrayModalInputCaptureActive())",
            StringComparison.Ordinal);
        const string observedKeyRoute = "return dispatchObservedStArrayKeyEvent(event);";
        var modalKeyEnd = keyDispatch.IndexOf(
            observedKeyRoute,
            modalKeyStart,
            StringComparison.Ordinal) + observedKeyRoute.Length;
        var modalKeyDispatch = keyDispatch[modalKeyStart..modalKeyEnd];
        var observedKeyStart = activity.IndexOf(
            "private boolean dispatchObservedStArrayKeyEvent",
            StringComparison.Ordinal);
        var observedKeyEnd = activity.IndexOf(
            "public boolean dispatchKeyEvent",
            observedKeyStart,
            StringComparison.Ordinal);
        var observedKeyDispatch = activity[observedKeyStart..observedKeyEnd];
        Assert.That(keyDispatch, Does.Contain("KeyEvent.KEYCODE_BACK"));
        Assert.That(keyDispatch, Does.Contain("requestStArrayModalClose()"));
        Assert.That(keyDispatch, Does.Contain(observedKeyRoute));
        Assert.That(observedKeyDispatch, Does.Contain("observeStArrayKeyEvent(event);"));
        Assert.That(observedKeyDispatch, Does.Contain("nativeOnKeyEvent(event)"));
        Assert.That(
            modalKeyDispatch.IndexOf("KeyEvent.KEYCODE_BACK", StringComparison.Ordinal),
            Is.LessThan(modalKeyDispatch.IndexOf(
                "dispatchObservedStArrayKeyEvent(event)",
                StringComparison.Ordinal)));
        var modalQueryStart = activity.IndexOf(
            "private static boolean isStArrayModalInputCaptureActive()",
            StringComparison.Ordinal);
        var modalQueryEnd = activity.IndexOf(
            "private static void requestStArrayModalClose()",
            modalQueryStart,
            StringComparison.Ordinal);
        var modalQuery = activity[modalQueryStart..modalQueryEnd];
        Assert.That(modalQuery, Does.Not.Contain("stArrayModManagerInputForwarding"));

        var keyObserverStart = bootstrap.IndexOf(
            "public static void observeGameplayKeyEvent",
            StringComparison.Ordinal);
        var keyObserverEnd = bootstrap.IndexOf(
            "public static native int nativeSetEnv",
            keyObserverStart,
            StringComparison.Ordinal);
        var keyObserver = bootstrap[keyObserverStart..keyObserverEnd];
        Assert.That(keyObserver, Does.Contain("KeyCharacterMap.VIRTUAL_KEYBOARD"));
        Assert.That(keyObserver, Does.Not.Contain("sKeyboardShown"));

        Assert.That(bootstrap, Does.Contain("setModalInputCapture(boolean active)"));
        Assert.That(bootstrap, Does.Contain("consumeModalCloseRequest()"));
        Assert.That(native, Does.Contain("modmanager_modal_input_set_active"));
        Assert.That(native, Does.Contain("modmanager_modal_input_take_close_request"));
        Assert.That(native, Does.Contain("__atomic_load_n(&g_modal_input_active"));
        Assert.That(native, Does.Contain("modmanager_modal_input_blocks_unity_event_system"));
        Assert.That(presentationSink, Does.Contain("UnityEngine.EventSystems"));
        Assert.That(presentationSink, Does.Contain("EventSystem"));
        Assert.That(presentationSink, Does.Contain("modmanager_modal_input_blocks_unity_event_system()"));
        Assert.That(presentationSink, Does.Contain(
            "PcCompat:UnityPresentationSink:EventSystem.Update"));
        Assert.That(presentationSink, Does.Contain("pccompat_metadata::resolve_method"));
        Assert.That(presentationSink, Does.Contain("modmanager_hook_broker_install"));
        Assert.That(managerUi, Does.Contain("ModOriginalSettingsState.Opening"));
        Assert.That(managerUi, Does.Contain("blockUnityEventSystem"));
        Assert.That(asyncInput, Does.Contain("modmanager_modal_input_active"));
        Assert.That(asyncInput, Does.Contain("modmanager_modal_input_is_active"));
        Assert.That(asyncInput, Does.Contain("RTLD_NOW | RTLD_NOLOAD"));
        Assert.That(asyncInput, Does.Contain("hooked_touch_enabled"));
        Assert.That(asyncInput, Does.Contain("hooked_valid_triggered"));
        Assert.That(asyncInput, Does.Contain("stop_capture_and_clear_queue();"));
    }

    [Test]
    public void AndroidModalCaptureUsesNativeStateAsAuthoritativeInputGate()
    {
        var root = FindHooksRoot();
        var platform = File.ReadAllText(Path.Combine(
            root,
                        "StArray.ModManager.Android",
            "UI",
            "AndroidModManagerPlatformServices.cs"));

        var queryStart = platform.IndexOf(
            "public bool IsModalInputCaptureActive",
            StringComparison.Ordinal);
        var queryEnd = platform.IndexOf(
            "public void RequestModZipImport",
            queryStart,
            StringComparison.Ordinal);
        var query = platform[queryStart..queryEnd];
        var setterStart = platform.IndexOf(
            "public void SetModalInputCapture",
            StringComparison.Ordinal);
        var setterEnd = platform.IndexOf(
            "public bool ConsumeModalCloseRequest",
            setterStart,
            StringComparison.Ordinal);
        var setter = platform[setterStart..setterEnd];

        Assert.Multiple(() =>
        {
            Assert.That(platform, Does.Contain(
                "EntryPoint = \"modmanager_modal_input_is_active\""));
            Assert.That(platform, Does.Contain(
                "EntryPoint = \"modmanager_modal_input_set_active\""));
            Assert.That(query, Does.Contain("modmanager_modal_input_is_active()"));
            Assert.That(setter, Does.Contain(
                "modmanager_modal_input_set_active(active ? 1 : 0);"));
            Assert.That(setter, Does.Contain(
                "_bootstrap.CallStaticVoidMethod1(_setModalInputCapture, active);"));
        });
    }

    [Test]
    public void GameplayObserverRunsOnlyAfterModManagerDeclinesTouch()
    {
        var root = FindHooksRoot();
        var source = File.ReadAllText(RequirePrivateActivitySource(root));
        var dispatchStart = source.IndexOf("public boolean dispatchTouchEvent", StringComparison.Ordinal);
        var dispatchEnd = source.IndexOf(
            "private static void observeStArrayMotionEvent",
            dispatchStart,
            StringComparison.Ordinal);
        Assert.That(dispatchStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(dispatchEnd, Is.GreaterThan(dispatchStart));

        var dispatch = source[dispatchStart..dispatchEnd];
        Assert.That(
            dispatch.IndexOf("forwardStArrayMotionEvent(event)", StringComparison.Ordinal),
            Is.LessThan(dispatch.IndexOf("observeStArrayMotionEvent(event", StringComparison.Ordinal)));
    }

    [Test]
    public void AndroidTouchOwnerIsFrozenFromDownUntilUpOrCancel()
    {
        var root = FindHooksRoot();
        var source = File.ReadAllText(RequirePrivateActivitySource(root));
        var dispatchStart = source.IndexOf(
            "public boolean dispatchTouchEvent",
            StringComparison.Ordinal);
        var dispatchEnd = source.IndexOf(
            "private boolean finishStArrayTouchDispatch",
            dispatchStart,
            StringComparison.Ordinal);
        var finishEnd = source.IndexOf(
            "private static int pointerCount",
            dispatchEnd,
            StringComparison.Ordinal);

        Assert.That(dispatchStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(dispatchEnd, Is.GreaterThan(dispatchStart));
        Assert.That(finishEnd, Is.GreaterThan(dispatchEnd));
        var dispatch = source[dispatchStart..dispatchEnd];
        var finish = source[dispatchEnd..finishEnd];
        var ownerSelectionEnd = dispatch.IndexOf(
            "switch (stArrayTouchOwner)",
            StringComparison.Ordinal);
        Assert.That(ownerSelectionEnd, Is.GreaterThanOrEqualTo(0));
        var ownerSelection = dispatch[..ownerSelectionEnd];
        var ownerDispatch = dispatch[ownerSelectionEnd..];

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain(
                "private int stArrayTouchOwner = STARRAY_TOUCH_OWNER_NONE;"));
            Assert.That(ownerSelection, Does.Contain(
                "action == MotionEvent.ACTION_DOWN ||"));
            Assert.That(ownerSelection, Does.Contain(
                "stArrayTouchOwner == STARRAY_TOUCH_OWNER_NONE"));
            Assert.That(ownerDispatch, Does.Contain(
                "case STARRAY_TOUCH_OWNER_MODMANAGER:"));
            Assert.That(ownerDispatch, Does.Contain(
                "return finishStArrayTouchDispatch(event, true);"));
            Assert.That(finish, Does.Contain("MotionEvent.ACTION_UP"));
            Assert.That(finish, Does.Contain("MotionEvent.ACTION_CANCEL"));
            Assert.That(finish, Does.Contain(
                "resetStArrayTouchOwner();"));
            Assert.That(source, Does.Contain(
                "protected void onPause()"));
            Assert.That(source, Does.Contain(
                "resetStArrayTouchOwner();"));
        });
    }

    [Test]
    public void PersistentImGuiOverlaysPublishWindowScopedTouchRegionsWhileManagerHidden()
    {
        var root = FindHooksRoot();
        var managerUi = File.ReadAllText(Path.Combine(
            root,
                        "StArray.ModManager",
            "Manager",
            "ModManagerUI.cs"));
        var native = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "cimgui_compat.cpp"));
        var renderer = File.ReadAllText(Path.Combine(
            root,
                        "StArray.ModManager.Android",
            "UI",
            "ImGuiEGLRender.cs"));

        var renderStart = managerUi.IndexOf("public void Render()", StringComparison.Ordinal);
        var renderEnd = managerUi.IndexOf(
            "public void PollPendingLoadsWhenHidden()",
            renderStart,
            StringComparison.Ordinal);
        Assert.That(renderStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(renderEnd, Is.GreaterThan(renderStart));
        var render = managerUi[renderStart..renderEnd];

        var forwardStart = native.IndexOf("nativeForwardMotionEvent(", StringComparison.Ordinal);
        var forwardEnd = native.IndexOf(
            "static void add_forwarded_mouse_source",
            forwardStart,
            StringComparison.Ordinal);
        Assert.That(forwardStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(forwardEnd, Is.GreaterThan(forwardStart));
        var forward = native[forwardStart..forwardEnd];

        Assert.Multiple(() =>
        {
            Assert.That(render, Does.Contain(
                "var inputSurfaceActive = managerVisible || RequiresRenderingWhenHidden;"));
            Assert.That(render, Does.Contain("if (inputSurfaceActive)"));
            Assert.That(native, Does.Contain("collect_current_imgui_input_rects_locked();"));
            Assert.That(native, Does.Contain("window->LastFrameActive != GImGui->FrameCount"));
            Assert.That(native, Does.Contain("ImGuiWindowFlags_NoMouseInputs"));
            Assert.That(native, Does.Contain("overlay_touch_has_active_route()"));
            Assert.That(native, Does.Contain("modmanager_overlay_touch_clear"));
            Assert.That(renderer, Does.Contain("_hiddenInputSurfaceActive"));
            Assert.That(renderer, Does.Contain("ClearOverlayTouchState();"));
            Assert.That(forward, Does.Not.Contain(
                "modmanager_overlay_ui_is_visible() == 0"));
            Assert.That(forward, Does.Contain("if (!manager_visible && !consume)"));
        });
    }

    [Test]
    public void TouchObserverPreservesPointerEdgesWithoutConsumingThem()
    {
        var root = FindHooksRoot();
        var bootstrap = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "java",
            "com",
            "fizzd",
            "connectedworlds",
            "editorport",
            "StArrayModManagerBootstrap.java"));
        var native = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "pccompat_hook_rules.cpp"));
        var realtime = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "realtime_event_core.cpp"));

        Assert.That(bootstrap, Does.Contain("event.getActionMasked()"));
        Assert.That(bootstrap, Does.Contain("event.getPointerId(pointerIndex)"));
        Assert.That(bootstrap, Does.Contain("event.getX(pointerIndex)"));
        Assert.That(bootstrap, Does.Contain("event.getY(pointerIndex)"));
        Assert.That(bootstrap, Does.Contain("viewportWidth"));
        Assert.That(bootstrap, Does.Contain("viewportHeight"));
        Assert.That(native, Does.Contain("starray::realtime::observe_touch"));
        Assert.That(realtime, Does.Contain("kActionPointerDown = 5"));
        Assert.That(realtime, Does.Contain("kActionPointerUp = 6"));
        Assert.That(realtime, Does.Contain("g_state.snapshot.held_mask"));
    }

    [Test]
    public void PhysicalKeyboardObserverPreservesGameplayDispatchAndFullMetadata()
    {
        var root = FindHooksRoot();
        var activity = File.ReadAllText(RequirePrivateActivitySource(root));
        var bootstrap = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "java",
            "com",
            "fizzd",
            "connectedworlds",
            "editorport",
            "StArrayModManagerBootstrap.java"));
        var realtimeHeader = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "realtime_event_core.h"));
        var realtimeSource = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "realtime_event_core.cpp"));

        var observedDispatchStart = activity.IndexOf(
            "private boolean dispatchObservedStArrayKeyEvent",
            StringComparison.Ordinal);
        var dispatchStart = activity.IndexOf("public boolean dispatchKeyEvent", StringComparison.Ordinal);
        var dispatchEnd = activity.IndexOf(
            "protected void onPause",
            dispatchStart,
            StringComparison.Ordinal);
        Assert.That(observedDispatchStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(dispatchStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(dispatchEnd, Is.GreaterThan(dispatchStart));
        var observedDispatch = activity[observedDispatchStart..dispatchStart];
        var dispatch = activity[dispatchStart..dispatchEnd];
        Assert.That(
            observedDispatch.IndexOf("observeStArrayKeyEvent(event)", StringComparison.Ordinal),
            Is.LessThan(observedDispatch.IndexOf("nativeOnKeyEvent(event)", StringComparison.Ordinal)));
        Assert.That(dispatch, Does.Contain("return dispatchObservedStArrayKeyEvent(event);"));

        Assert.That(bootstrap, Does.Contain("sKeyboardShown"));
        Assert.That(bootstrap, Does.Contain("KeyCharacterMap.VIRTUAL_KEYBOARD"));
        Assert.That(bootstrap, Does.Contain("event.getScanCode()"));
        Assert.That(bootstrap, Does.Contain("event.getMetaState()"));
        Assert.That(bootstrap, Does.Contain("event.getDeviceId()"));
        Assert.That(bootstrap, Does.Contain("event.getRepeatCount()"));
        Assert.That(realtimeHeader, Does.Contain("bool observe_key"));
        Assert.That(realtimeHeader, Does.Contain("int32_t scan_code"));
        Assert.That(realtimeHeader, Does.Contain("int32_t android_flags"));
        Assert.That(realtimeSource, Does.Contain("constexpr size_t kMaxKeyboardSlots = 64"));
        Assert.That(realtimeSource, Does.Contain("repeat_count > 0"));
    }

    [Test]
    public void AsyncInputAndActivityUseMutuallyExclusiveVersionedProducers()
    {
        var root = FindHooksRoot();
        var asyncSource = File.ReadAllText(Path.Combine(
            root,
            "external",
            "AsyncInput",
            "async_input.c"));
        var observerAbi = File.ReadAllText(Path.Combine(
            root,
            "external",
            "AsyncInput",
            "include",
            "async_input_observer_abi.h"));
        var bridge = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "async_input_observer_bridge.cpp"));
        var realtimeHeader = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "realtime_event_core.h"));
        var realtimeSource = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "realtime_event_core.cpp"));

        Assert.Multiple(() =>
        {
            Assert.That(observerAbi, Does.Contain("ADOFAI_ASYNC_RAW_OBSERVER_ABI_V1"));
            Assert.That(observerAbi, Does.Contain("producer_epoch"));
            Assert.That(asyncSource, Does.Contain("ADOFAIAsyncInput_RegisterRawObserverV1"));
            Assert.That(asyncSource, Does.Contain("publish_raw_touch_observer"));
            Assert.That(asyncSource, Does.Contain("publish_raw_key_observer"));
            Assert.That(bridge, Does.Contain("RTLD_NOLOAD"));
            Assert.That(bridge, Does.Contain("accepts_async_event"));
            Assert.That(realtimeHeader, Does.Contain("InputProducer::OfficialActivity"));
            Assert.That(realtimeHeader, Does.Contain("AsyncInput = 2"));
            Assert.That(realtimeSource, Does.Contain("set_active_producer"));
            Assert.That(realtimeSource, Does.Contain("InputPhase::ProducerChanged"));
            Assert.That(realtimeSource, Does.Contain("g_state.snapshot.active_producer != producer"));
        });
    }

    [Test]
    public void GameplayAcceptedUsesExactAfterResultHookAndIndependentEventStream()
    {
        var root = FindHooksRoot();
        var header = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "realtime_event_core.h"));
        var realtime = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "realtime_event_core.cpp"));
        var hooks = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "pccompat_hook_rules.cpp"));
        var asyncBridge = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "async_input_observer_bridge.cpp"));

        Assert.Multiple(() =>
        {
            Assert.That(header, Does.Contain("struct GameplayAcceptedEvent"));
            Assert.That(header, Does.Contain("InputSource::GameAction"));
            var acceptedStart = header.IndexOf("struct GameplayAcceptedEvent", StringComparison.Ordinal);
            var acceptedEnd = header.IndexOf("struct GameplayAcceptedSnapshot", acceptedStart, StringComparison.Ordinal);
            Assert.That(acceptedStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(acceptedEnd, Is.GreaterThan(acceptedStart));
            Assert.That(header[acceptedStart..acceptedEnd], Does.Contain("InputSource::GameAction"));
            var rawStart = header.IndexOf("struct InputEvent", StringComparison.Ordinal);
            var rawEnd = header.IndexOf("struct InputSnapshot", rawStart, StringComparison.Ordinal);
            Assert.That(rawStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(rawEnd, Is.GreaterThan(rawStart));
            Assert.That(header[rawStart..rawEnd], Does.Contain("InputSource::Touch"));
            Assert.That(header[rawStart..rawEnd], Does.Not.Contain("InputSource::GameAction"));
            Assert.That(realtime, Does.Contain("kGameplayAcceptedEventCapacity = 2048"));
            Assert.That(realtime, Does.Contain("observe_gameplay_accepted"));
            Assert.That(realtime, Does.Contain("event.is_auto || event.is_test_macro"));
            Assert.That(hooks, Does.Contain("InstanceBoolBoolIntFn"));
            Assert.That(hooks, Does.Contain("dispatcher_instance_bool_bool_int"));
            Assert.That(hooks, Does.Contain("original(self, arg0, arg1, method_info)"));
            Assert.That(hooks, Does.Contain("args.has_bool_result = true"));
            Assert.That(hooks, Does.Contain("args.bool_result &&"));
            Assert.That(hooks, Does.Contain("kRuleOpGameplayAcceptedObserve = 22"));
            Assert.That(hooks, Does.Contain("kTargetKindPlayerHitInputEvent"));
            Assert.That(asyncBridge, Does.Contain("ADOFAIAsyncInput_IsTestMacroEnabled"));
            Assert.That(asyncBridge, Does.Contain("g_is_test_macro_enabled"));
        });
    }

    [Test]
    public void TouchLaneWorkerUsesSessionBoundariesAndFixedLaneState()
    {
        var root = FindHooksRoot();
        var realtime = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "realtime_event_core.cpp"));
        var worker = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "hud_logic_worker.cpp"));
        var hooks = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "pccompat_hook_rules.cpp"));

        Assert.That(realtime, Does.Contain("uint32_t begin_session"));
        Assert.That(realtime, Does.Contain("InputPhase::Reset"));
        Assert.That(realtime, Does.Contain("session_anchor_raw_ns"));
        Assert.That(realtime, Does.Contain("kCountJournalTouchLaneCounts{2, 4, 6, 8, 10}"));
        Assert.That(realtime, Does.Contain("map_touch_lane"));
        Assert.That(realtime, Does.Contain("touch_contact_lanes[index].fill(-1)"));
        Assert.That(realtime, Does.Contain("reset_touch_count_journal_session_locked"));
        Assert.That(worker, Does.Contain("read_input_checkpoint"));
        Assert.That(worker, Does.Contain("source_projection.session_down_counts"));
        Assert.That(worker, Does.Contain("touch_lane_held_counts"));
        Assert.That(hooks, Does.Contain("starray::realtime::begin_session"));
    }

    [Test]
    public void TouchLaneWorkerPublishesAllSupportedPerModLayouts()
    {
        var root = FindHooksRoot();
        var worker = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "hud_logic_worker.cpp"));
        var realtime = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "realtime_event_core.cpp"));
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
        var bridge = File.ReadAllText(Path.Combine(
            root,
                        "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatDobbyBridge.cs"));

        Assert.That(realtime, Does.Contain("kCountJournalTouchLaneCounts{2, 4, 6, 8, 10}"));
        Assert.That(realtime, Does.Contain("record_touch_count_journal_event_locked"));
        Assert.That(worker, Does.Contain("journal.touch_projections[index]"));
        Assert.That(worker, Does.Contain("select_touch_lane_projection"));
        Assert.That(native, Does.Contain("PcCompatInputHudSnapshotV1"));
        Assert.That(native, Does.Contain("static_assert(sizeof(PcCompatInputHudSnapshotV1) == 304)"));
        Assert.That(native, Does.Contain("modmanager_pccompat_read_input_hud_snapshot"));
        Assert.That(managed, Does.Contain("InputHudSnapshotAbiVersion = 1"));
        Assert.That(managed, Does.Contain("GetInputHudSnapshot(int touchLaneCount)"));
        Assert.That(managed, Does.Contain("StructLayout(LayoutKind.Sequential, Pack = 4)"));
        Assert.That(managed, Does.Contain("fixed ushort TouchLaneHeldCounts[10]"));
        Assert.That(managed, Does.Contain("Marshal.SizeOf<InputHudSnapshotNative>()"));
    }

    [Test]
    public void MobileTouchKeyCountAcceptsOnlyPublishedProjectionSizes()
    {
        foreach (var value in new[] { 2, 4, 6, 8, 10 })
        {
            var settings = new Xphorror.PcModCompat.PcCompatMobileSettings
            {
                TouchKeyCount = value
            };
            settings.Normalize();
            Assert.That(settings.TouchKeyCount, Is.EqualTo(value));
        }

        var invalid = new Xphorror.PcModCompat.PcCompatMobileSettings
        {
            TouchKeyCount = 7
        };
        invalid.Normalize();
        Assert.That(invalid.TouchKeyCount, Is.EqualTo(10));
    }

    [Test]
    public void UnityMainPublishesVersionedClockAnchorsWithoutWorkerUnityCalls()
    {
        var root = FindHooksRoot();
        var worker = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "hud_logic_worker.cpp"));
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
        var bridge = File.ReadAllText(Path.Combine(
            root,
                        "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatDobbyBridge.cs"));

        Assert.That(worker, Does.Contain("g_clock_anchor_history"));
        Assert.That(worker, Does.Contain("publish_clock_anchor"));
        Assert.That(worker, Does.Contain("read_latest_clock_anchor"));
        Assert.That(worker, Does.Contain("std::try_to_lock"));
        Assert.That(worker, Does.Not.Contain("runtime_invoke"));
        Assert.That(native, Does.Contain("UnityEngine.Time"));
        Assert.That(native, Does.Contain("get_timeScale"));
        Assert.That(native, Does.Contain("get_frameCount"));
        Assert.That(native, Does.Contain("starray::hud_logic::publish_clock_anchor(clock_anchor)"));
        Assert.That(native, Does.Contain("static_assert(sizeof(PcCompatClockAnchorSnapshotV1) == 64)"));
        Assert.That(native, Does.Contain("modmanager_pccompat_read_clock_anchor_snapshot"));
        Assert.That(managed, Does.Contain("ClockAnchorSnapshotAbiVersion = 1"));
        Assert.That(managed, Does.Contain("GetClockAnchorSnapshot()"));
        Assert.That(managed, Does.Contain("GetMonotonicClockSnapshot()"));
        Assert.That(managed, Does.Contain("PcCompatMonotonicClockSnapshot"));
        Assert.That(bridge, Does.Contain("RegisterMonotonicProvider"));
    }

    [Test]
    public void RealtimeEventCoreUsesBoundedFanOutStorage()
    {
        var root = FindHooksRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "realtime_event_core.cpp"));
        var header = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "realtime_event_core.h"));
        var cmake = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "CMakeLists.txt"));

        Assert.That(header, Does.Contain("kRawInputEventJournalCapacity = 8192"));
        Assert.That(source, Does.Contain("constexpr size_t kPressTimeCapacity = 512"));
        Assert.That(source, Does.Contain("EventReadResult read_events"));
        Assert.That(source, Does.Contain("dropped_before_cursor"));
        Assert.That(source, Does.Contain("record_count_journal_event_locked"));
        Assert.That(source, Does.Contain("read_count_journal_snapshot"));
        Assert.That(source, Does.Contain("read_input_checkpoint"));
        Assert.That(source, Does.Contain("lifetime_down_count"));
        Assert.That(source, Does.Contain("session_down_counts"));
        Assert.That(source, Does.Contain("kCountJournalTouchLaneCounts{2, 4, 6, 8, 10}"));
        Assert.That(source, Does.Contain("void wait_for_change"));
        Assert.That(source, Does.Contain("const size_t first_offset"));
        Assert.That(source, Does.Contain("next_kps_expiry_ns"));
        Assert.That(source, Does.Contain("std::condition_variable changed"));
        Assert.That(source, Does.Not.Contain("std::vector"));
        Assert.That(cmake, Does.Contain("core/realtime_event_core.cpp"));
    }

    [Test]
    public void LegacyInputPollingUsesNativeSnapshotAndManagedCallsiteBridge()
    {
        var root = FindHooksRoot();
        var native = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "pccompat_hook_rules.cpp"));
        var bridge = File.ReadAllText(Path.Combine(
            root,
                        "xphorror.PcModCompat",
            "src",
            "PcCompatLegacyInputBridge.cs"));
        var rewriter = File.ReadAllText(Path.Combine(
            root,
                        "xphorror.PcModCompat",
            "tools",
            "ModAssemblyRewriter",
            "Program.cs"));
        var androidCatalog = File.ReadAllText(Path.Combine(
            root,
                        "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatAndroidManagedAssemblyRewrite.cs"));
        var androidBridge = File.ReadAllText(Path.Combine(
            root,
                        "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatNativeHookRules.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(native, Does.Contain("modmanager_pccompat_read_legacy_input_snapshot"));
            Assert.That(native, Does.Contain("sizeof(starray::realtime::LegacyInputSnapshot) == 8288"));
            Assert.That(bridge, Does.Contain("Stopwatch.Frequency / 1000"));
            Assert.That(bridge, Does.Contain("GetKeyDown<TKey>"));
            Assert.That(bridge, Does.Contain("GetAsyncKeyState"));
            Assert.That(rewriter, Does.Contain("AppendCallsiteToken"));
            Assert.That(rewriter, Does.Contain("InsertBeforeWithRetargeting"));
            Assert.That(androidCatalog, Does.Contain(
                "xphorror.pcmod-managed-cache.v30-ddol-owner-teardown"));
            Assert.That(androidCatalog, Does.Contain("PcCompatManagedImGuiBridge.v4"));
            Assert.That(androidCatalog, Does.Contain("PcCompatManagedSettingsDelegateBridge.v1"));
            Assert.That(
                androidCatalog,
                Does.Contain("builder.AppendLine(ModAssemblyRewriteApi.FormatVersion)"),
                "managed rewrite cache key must include the rewriter schema");
            Assert.That(
                ModAssemblyRewriteApi.FormatVersion,
                Is.EqualTo("xphorror.pcmod-proxy-rewrite.v18-external-valuetype-kind"));
            Assert.That(androidCatalog, Does.Contain("PcCompatManagedComponentBridge.v6"));
            Assert.That(androidCatalog, Does.Contain("nameof(PcCompatManagedComponentBridge.SetEnabled)"));
            Assert.That(androidCatalog, Does.Contain("PcCompatManagedIoBridge.v2"));
            Assert.That(androidCatalog, Does.Contain("PcCompatManagedPollingBridge.v1"));
            Assert.That(native, Does.Contain("modmanager_pccompat_read_raw_input_events"));
            Assert.That(native, Does.Contain("sizeof(PcCompatRawInputEventV1) == 88"));
            Assert.That(native, Does.Contain("dropped_before_cursor = read.dropped_before_cursor"));
            Assert.That(androidBridge, Does.Contain("ReadRawInputEventsNative"));
            Assert.That(androidBridge, Does.Contain("DroppedBeforeCursor = header.DroppedBeforeCursor"));
            Assert.That(androidCatalog, Does.Contain("PcCompatLegacyInputBridge.v1"));
            Assert.That(androidCatalog, Does.Contain("PcCompatKeyViewerBehaviorScanner.Scan"));
            Assert.That(androidCatalog, Does.Contain("keyviewer_adapter.json"));
            Assert.That(androidCatalog, Does.Contain("keyviewer_adapter_manifest.txt"));
            Assert.That(
                androidCatalog.IndexOf("if (IsCompleteBundle", StringComparison.Ordinal),
                Is.LessThan(androidCatalog.IndexOf(
                    "PcCompatKeyViewerBehaviorScanner.Scan",
                    StringComparison.Ordinal)),
                "managed cache hits must not rescan the full MOD IL graph");
            Assert.That(androidCatalog, Does.Contain("method.IsPinvokeImpl"));
            Assert.That(androidCatalog, Does.Contain("user32.dll"));
        });
    }

    [Test]
    public void ModalSettingsCaptureQuarantinesTouchAdaptersButKeepsHardwareKeyObservation()
    {
        var root = FindHooksRoot();
        var nativeRoot = Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core");
        var cimgui = File.ReadAllText(Path.Combine(nativeRoot, "cimgui_compat.cpp"));
        var asyncObserver = File.ReadAllText(Path.Combine(
            nativeRoot,
            "async_input_observer_bridge.cpp"));
        var realtime = File.ReadAllText(Path.Combine(nativeRoot, "realtime_event_core.cpp"));
        var realtimeHeader = File.ReadAllText(Path.Combine(nativeRoot, "realtime_event_core.h"));
        var platform = File.ReadAllText(Path.Combine(
            root,
                        "StArray.ModManager.Android",
            "UI",
            "AndroidModManagerPlatformServices.cs"));
        var bridge = File.ReadAllText(Path.Combine(
            root,
                        "xphorror.PcModCompat",
            "src",
            "PcCompatLegacyInputBridge.cs"));

        var officialTouch = Slice(
            cimgui,
            "nativeObserveGameplayMotionEvent(",
            "nativeObserveGameplayKeyEvent(");
        var asyncTouch = Slice(asyncObserver, "void on_touch(", "void on_key(");
        var asyncKey = Slice(asyncObserver, "void on_key(", "}  // namespace");
        var cancelTouch = Slice(
            realtime,
            "void cancel_touch_input(",
            "bool set_touch_lane_mapping_mode(");

        Assert.Multiple(() =>
        {
            Assert.That(platform, Does.Contain(
                "PcCompatLegacyInputBridge.SetModalInputCapture(active)"));
            Assert.That(bridge, Does.Contain("s_modalInputCaptureActive"));
            Assert.That(bridge, Does.Contain("s_nativeObservedModalInputActive"));
            Assert.That(bridge, Does.Contain(
                "EntryPoint = \"modmanager_modal_input_is_active\""));
            Assert.That(bridge, Does.Contain("IsModalInputCaptureActive()"));
            Assert.That(bridge, Does.Contain("modalNative="));
            Assert.That(bridge, Does.Contain("modalLastKey="));
            Assert.That(bridge, Does.Contain("baselineOnly"));
            Assert.That(bridge, Does.Contain("UpdateModalBindingBaseline"));
            Assert.That(bridge, Does.Contain("ReadNativeKeyEdge"));
            Assert.That(officialTouch, Does.Contain(
                "modmanager_modal_input_is_active() != 0"));
            Assert.That(asyncTouch, Does.Contain(
                "modmanager_modal_input_is_active() != 0"));
            Assert.That(asyncKey, Does.Not.Contain(
                "modmanager_modal_input_is_active() != 0"),
                "external keyboard keys must remain available to the MOD binding UI");
            Assert.That(cimgui, Does.Contain("starray::realtime::cancel_touch_input()"));
            Assert.That(realtimeHeader, Does.Contain("void cancel_touch_input(int64_t raw_ns = 0)"));
            Assert.That(cancelTouch, Does.Contain("InputPhase::Cancel"));
            Assert.That(cancelTouch, Does.Not.Contain("total_count"),
                "opening a settings modal may release held touches but must not reset KV counts");
        });

        static string Slice(string source, string startMarker, string endMarker)
        {
            var start = source.IndexOf(startMarker, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), startMarker);
            var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
            Assert.That(end, Is.GreaterThan(start), endMarker);
            return source[start..end];
        }
    }

    [Test]
    public void HiddenOverlayCommitCannotRepublishStaleImGuiTouchRects()
    {
        var root = FindHooksRoot();
        var nativeRoot = Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core");
        var cimgui = File.ReadAllText(Path.Combine(nativeRoot, "cimgui_compat.cpp"));
        var commitStart = cimgui.IndexOf(
            "extern \"C\" void modmanager_overlay_touch_commit_frame(void)",
            StringComparison.Ordinal);
        Assert.That(commitStart, Is.GreaterThanOrEqualTo(0));
        var commitEnd = cimgui.IndexOf(
            "static bool overlay_touch_has_active_route()",
            commitStart,
            StringComparison.Ordinal);

        Assert.That(commitEnd, Is.GreaterThan(commitStart));
        var commit = cimgui[commitStart..commitEnd];
        var hiddenGuard = commit.IndexOf(
            "if (g_overlay_touch_frame_started_visible && !g_overlay_ui_visible)",
            StringComparison.Ordinal);
        var collectRects = commit.IndexOf(
            "collect_current_imgui_input_rects_locked();",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(hiddenGuard, Is.GreaterThanOrEqualTo(0),
                "a frame that closes the overlay must not publish input rects");
            Assert.That(collectRects, Is.GreaterThan(hiddenGuard),
                "the hidden check must run before ImGui rect collection");
            Assert.That(commit, Does.Contain("g_overlay_touch_active_rect_count = 0"));
            Assert.That(commit, Does.Contain("g_overlay_touch_pending_rect_count = 0"));
            Assert.That(commit, Does.Contain("g_overlay_touch_active = false"));
        });

        var beginStart = cimgui.IndexOf(
            "extern \"C\" void modmanager_overlay_touch_begin_frame(void)",
            StringComparison.Ordinal);
        Assert.That(beginStart, Is.GreaterThanOrEqualTo(0));
        var beginEnd = cimgui.IndexOf(
            "extern \"C\" void modmanager_overlay_touch_clear(void)",
            beginStart,
            StringComparison.Ordinal);
        Assert.That(beginEnd, Is.GreaterThan(beginStart));
        var begin = cimgui[beginStart..beginEnd];
        Assert.That(begin, Does.Contain(
            "g_overlay_touch_frame_started_visible = g_overlay_ui_visible"));
    }

    [Test]
    public void VisibleModManagerOwnsTouchOutsideWindowWhileHiddenOverlaysRemainWindowScoped()
    {
        var root = FindHooksRoot();
        var cimgui = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "cimgui_compat.cpp"));
        var forwardStart = cimgui.IndexOf(
            "nativeForwardMotionEvent(",
            StringComparison.Ordinal);
        var forwardEnd = cimgui.IndexOf(
            "static void add_forwarded_mouse_source",
            forwardStart,
            StringComparison.Ordinal);

        Assert.That(forwardStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(forwardEnd, Is.GreaterThan(forwardStart));
        var forward = cimgui[forwardStart..forwardEnd];

        Assert.Multiple(() =>
        {
            Assert.That(forward, Does.Contain(
                "if (!manager_visible && !overlay_touch_has_active_route())"));
            Assert.That(forward, Does.Contain(
                "if (!manager_visible && !consume)"));
            Assert.That(forward, Does.Contain(
                "return manager_visible || consume ? JNI_TRUE : JNI_FALSE;"),
                "a visible manager is modal even outside its current ImGui window rect");
        });
    }

    [Test]
    public void UnityHudSourceRemovalSchedulesAHideAndNoFrameRefreshDeactivatesCanvas()
    {
        var root = FindHooksRoot();
        var bridge = File.ReadAllText(Path.Combine(
            root,
                        "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatUnityHudBridge.cs"));
        var installStart = bridge.IndexOf("public static void Install()", StringComparison.Ordinal);
        var installEnd = bridge.IndexOf(
            "[UnmanagedCallersOnly",
            installStart,
            StringComparison.Ordinal);
        var refreshStart = bridge.IndexOf(
            "internal static void RefreshResourcesOnUnityMain()",
            StringComparison.Ordinal);
        var refreshEnd = bridge.IndexOf(
            "internal static void ReleaseResourcesOnUnityMain",
            refreshStart,
            StringComparison.Ordinal);

        Assert.That(installStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(installEnd, Is.GreaterThan(installStart));
        Assert.That(refreshStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(refreshEnd, Is.GreaterThan(refreshStart));
        var install = bridge[installStart..installEnd];
        var refresh = bridge[refreshStart..refreshEnd];
        var noFrameStart = refresh.IndexOf(
            "if (!PcCompatUnityHudRuntime.TryGetFrame",
            StringComparison.Ordinal);
        var noFrameEnd = refresh.IndexOf(
            "EnsureCreated();",
            noFrameStart,
            StringComparison.Ordinal);
        Assert.That(noFrameStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(noFrameEnd, Is.GreaterThan(noFrameStart));
        var noFrame = refresh[noFrameStart..noFrameEnd];

        Assert.Multiple(() =>
        {
            Assert.That(install, Does.Contain(
                "PcCompatUnityHudRuntime.RegisterSourcesChangedSink"));
            Assert.That(bridge, Does.Contain("OnSourcesChanged"));
            Assert.That(bridge, Does.Contain(
                "PcCompatResourceBundleLoader.TryScheduleUnityMainWork"));
            Assert.That(noFrame, Does.Contain("SetVisible(false)"));
            Assert.That(noFrame.IndexOf("SetVisible(false)", StringComparison.Ordinal),
                Is.LessThan(noFrame.IndexOf("return;", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void ExternalKeyboardDetectionRejectsNonAlphabeticKeySources()
    {
        var root = FindHooksRoot();
        var bootstrap = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "java",
            "com",
            "fizzd",
            "connectedworlds",
            "editorport",
            "StArrayModManagerBootstrap.java"));
        var diagnostics = File.ReadAllText(Path.Combine(
            root,
                        "xphorror.PcModCompat",
            "src",
            "PcCompatModPlugin.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(bootstrap, Does.Contain("device.isVirtual() || !device.isExternal()"));
            Assert.That(bootstrap, Does.Contain(
                "device.getKeyboardType() == InputDevice.KEYBOARD_TYPE_ALPHABETIC"));
            Assert.That(bootstrap, Does.Not.Contain(
                "(sources & InputDevice.SOURCE_KEYBOARD) == InputDevice.SOURCE_KEYBOARD"),
                "media buttons, remotes and vendor uinput devices are not external alphabetic keyboards");
            Assert.That(diagnostics, Does.Contain("requested={feature.RequestedInputMode}"));
            Assert.That(diagnostics, Does.Contain("sessionDeviceFlags={feature.SessionDeviceFlags}"));
            Assert.That(diagnostics, Does.Contain("sessionModeReason={FirstStatusLine(feature.SessionModeReason)}"));
        });
    }

    [Test]
    public void HudLogicWorkerConsumesEventsWithoutPollingOrBlockingUnityMain()
    {
        var root = FindHooksRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "hud_logic_worker.cpp"));

        Assert.That(source, Does.Contain("kCompletedSnapshotHistory = 3"));
        Assert.That(source, Does.Contain("realtime::read_events"));
        Assert.That(source, Does.Contain("realtime::read_input_checkpoint"));
        Assert.That(source, Does.Contain("source_projection.session_down_counts"));
        Assert.That(source, Does.Contain("realtime::wait_for_change"));
        Assert.That(source, Does.Contain("std::try_to_lock"));
        Assert.That(source, Does.Contain("std::thread(worker_main).detach()"));
        Assert.That(source, Does.Not.Contain("sleep_for"));
        Assert.That(source, Does.Not.Contain("sleep_until"));
    }

    [Test]
    public void RewrittenOracleDefaultIsDefinedByAssemblyThatOwnsPcCompatRuntime()
    {
        var root = FindHooksRoot();
        var managedProject = File.ReadAllText(Path.Combine(
            root,
                        "StArray.ModManager",
            "StArray.ModManager.csproj"));
        var androidProject = File.ReadAllText(Path.Combine(
            root,
                        "StArray.ModManager.Android",
            "StArray.ModManager.Android.csproj"));
        var buildScript = File.ReadAllText(Path.Combine(
            root,
                        "build.ps1"));

        Assert.That(
            managedProject,
            Does.Contain("STARRAY_PCMOD_COMPAT_REWRITTEN_ORACLE_DEFAULT"),
            "PcCompatRuntime.cs is compiled by StArray.ModManager.csproj, so its default gate must be defined there.");
        Assert.That(
            androidProject,
            Does.Not.Contain("STARRAY_PCMOD_COMPAT_REWRITTEN_ORACLE_DEFAULT"),
            "Defining the gate only in the Android host assembly does not affect PcCompatRuntime.");
        Assert.That(buildScript, Does.Contain("-p:PcCompatRewrittenOracleDefault=true"));
    }

    [Test]
    public void PcCompatHookBridgeDelegatesResolutionAndSlotOwnershipToNative()
    {
        var root = FindHooksRoot();
        var bridge = File.ReadAllText(Path.Combine(
            root,
                        "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatDobbyBridge.cs"));
        var project = File.ReadAllText(Path.Combine(
            root,
                        "StArray.ModManager.Android",
            "StArray.ModManager.Android.csproj"));
        var cmake = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "CMakeLists.txt"));
        var nativeRules = File.ReadAllText(Path.Combine(
            root,
                        "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "pccompat_hook_rules.cpp"));

        Assert.Multiple(() =>
        {
            Assert.That(bridge, Does.Not.Contain("UnityResolve"));
            Assert.That(bridge, Does.Not.Contain("FunctionPtr"));
            Assert.That(bridge, Does.Contain("GetRuleCountForModTarget"));
            Assert.That(project, Does.Not.Contain("UnityResolve"));
            Assert.That(
                File.Exists(Path.Combine(
                    root,
                                        "StArray.ModManager.Android",
                    "Native",
                    "UnityResolve.cs")),
                Is.False);
            Assert.That(
                File.Exists(Path.Combine(
                    root,
                                        "Android",
                    "library",
                    "src",
                    "main",
                    "cpp",
                    "core",
                    "unity_resolve.cpp")),
                Is.False);
            Assert.That(cmake, Does.Not.Contain("core/unity_resolve.cpp"));
            Assert.That(cmake, Does.Not.Contain("libs/unityresolve"));
            Assert.That(nativeRules, Does.Contain("modmanager_pccompat_get_rule_count_for_mod_target"));
            Assert.That(nativeRules, Does.Contain("ensure_il2cpp_metadata"));
        });
    }

    [Test]
    public void KeyViewerPreviewUsesOneNativeWakeThreadAndSharedActorWorkers()
    {
        var root = FindHooksRoot();
        var runtime = File.ReadAllText(Path.Combine(
            root,
                        "xphorror.PcModCompat",
            "src",
            "PcCompatKeyViewerPreviewRuntime.cs"));
        var pcCompatRuntime = File.ReadAllText(Path.Combine(
            root,
                        "xphorror.PcModCompat",
            "src",
            "PcCompatRuntime.cs"));
        var androidReader = File.ReadAllText(Path.Combine(
            root,
                        "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatNativeHookRules.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(runtime, Does.Contain("OpenAtTail"));
            Assert.That(runtime, Does.Contain("Registrations normally share one cursor"));
            Assert.That(runtime, Does.Not.Contain("Task.Run"));
            Assert.That(runtime.Split("new Thread(").Length - 1, Is.EqualTo(1));
            Assert.That(runtime, Does.Contain("PcCompatKeyViewerEventRuntime.WaitForChange"));
            Assert.That(runtime, Does.Contain("ThreadPriority.BelowNormal"));
            Assert.That(runtime, Does.Not.Contain("OnGui"));
            Assert.That(pcCompatRuntime, Does.Contain("PcCompatKeyViewerPreviewRuntime.DispatchFrame()"));
            Assert.That(pcCompatRuntime, Does.Contain("PcCompatKeyViewerPreviewRuntime.HasPumpDemand"));
            Assert.That(androidReader, Does.Contain("[ThreadStatic]"));
            Assert.That(androidReader, Does.Contain("s_rawInputEventBuffer ??="));
            Assert.That(androidReader, Does.Contain("Array.Empty<PcCompatKeyViewerRawEvent>()"));
            Assert.That(androidReader, Does.Contain("WaitRawInputChangeNative"));
            Assert.That(androidReader, Does.Contain("InterruptRawInputWaitNative"));
        });
    }

    private static string FindHooksRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StArray.ModManager.slnx")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Android")) &&
                Directory.Exists(Path.Combine(directory.FullName, "xphorror.PcModCompat")))
                return directory.FullName;
            directory = directory.Parent;
        }

        Assert.Fail("Could not find the public repository root from test directory");
        return string.Empty;
    }

    private static string RequirePrivateActivitySource(string root)
    {
        var path = Path.Combine(
            root,
            "extra_menu_activity",
            "src",
            "com",
            "fizzd",
            "connectedworlds",
            "editorport",
            "ExtraMenuUnityPlayerActivity.java");
        if (!File.Exists(path))
            Assert.Ignore("Private Android host activity source is not part of this repository.");
        return path;
    }
}

namespace StArray.ModManager.Tests;

public sealed class PcCompatAndroidInputContractTests
{
    [Test]
    public void ApplicationFocusFlowsFromActivityLifecycleToManagedPcModBridge()
    {
        var root = FindHooksRoot();
        var activity = File.ReadAllText(Path.Combine(
            root, "extra_menu_activity", "src", "com", "fizzd", "connectedworlds",
            "editorport", "ExtraMenuUnityPlayerActivity.java"));
        var bootstrap = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "java",
            "com", "fizzd", "connectedworlds", "editorport",
            "StArrayModManagerBootstrap.java"));
        var jni = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp",
            "core", "cimgui_compat.cpp"));
        var native = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "Android", "library", "src", "main", "cpp",
            "core", "pccompat_hook_rules.cpp"));
        var managed = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager", "xphorror.PcModCompat", "src",
            "PcCompatManagedApplicationBridge.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(activity, Does.Contain("publishStArrayApplicationFocusState()"));
            Assert.That(activity, Does.Contain("protected void onResume()"));
            Assert.That(activity, Does.Contain("protected void onPause()"));
            Assert.That(activity, Does.Contain("public void onWindowFocusChanged(boolean hasFocus)"));
            Assert.That(bootstrap, Does.Contain("setApplicationFocusState("));
            Assert.That(bootstrap, Does.Contain("nativeSetApplicationFocusState("));
            Assert.That(jni, Does.Contain("nativeSetApplicationFocusState("));
            Assert.That(native, Does.Contain("modmanager_pccompat_set_application_focus_state"));
            Assert.That(native, Does.Contain("modmanager_pccompat_application_is_focused"));
            Assert.That(managed, Does.Contain(
                "EntryPoint = \"modmanager_pccompat_application_is_focused\""));
        });
    }

    [Test]
    public void InputQueryHotPathDoesNotExposeDiagnosticBudgetOrTraceEntryPoints()
    {
        var bridge = typeof(Xphorror.PcModCompat.PcCompatLegacyInputBridge);
        var reset = bridge.GetMethod(
            "ResetInputDiagnosticAuditsForTests",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        var reserve = bridge.GetMethod(
            "TryReserveInputDiagnosticForTests",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        var trace = bridge.GetNestedType(
                "ThreadState",
                System.Reflection.BindingFlags.NonPublic)?
            .GetMethod(
                "TraceInputQuery",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);

        Assert.Multiple(() =>
        {
            Assert.That(reset, Is.Null);
            Assert.That(reserve, Is.Null);
            Assert.That(trace, Is.Null);
        });
    }

    [Test]
    public void AndroidRuntimeBuildChainRebuildsManagedGraphAndAuditsEveryCopyBoundary()
    {
        var root = FindHooksRoot();
        var modManagerRoot = Path.Combine(root, "StArray.ModManager");
        var singleBuild = File.ReadAllText(
            Path.Combine(modManagerRoot, "build_android_single.ps1"),
            System.Text.Encoding.UTF8);
        var overlayInstall = File.ReadAllText(
            Path.Combine(modManagerRoot, "install_android_overlay.ps1"),
            System.Text.Encoding.UTF8);
        var managedRewrite = File.ReadAllText(Path.Combine(
            modManagerRoot,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatAndroidManagedAssemblyRewrite.cs"),
            System.Text.Encoding.UTF8);
        var dragWindowBridge = File.ReadAllText(Path.Combine(
            modManagerRoot,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatAndroidImGuiDragWindowBridge.cs"),
            System.Text.Encoding.UTF8);
        var assemblyRewriter = File.ReadAllText(Path.Combine(
            modManagerRoot,
            "xphorror.PcModCompat",
            "tools",
            "ModAssemblyRewriter",
            "Program.cs"),
            System.Text.Encoding.UTF8);
        Assert.Multiple(() =>
        {
            Assert.That(singleBuild, Does.Contain("[switch]$IncrementalManagedBuild"));
            Assert.That(singleBuild, Does.Contain("'-t:Rebuild'"));
            Assert.That(singleBuild, Does.Contain("assert_runtime_bundle.ps1"));
            Assert.That(singleBuild, Does.Contain("ModAssemblyRewriter.dll"));
            Assert.That(managedRewrite, Does.Contain("v86-keyviewer-lane-origin-prefix"));
            Assert.That(managedRewrite, Does.Contain("v4-null-source-initialization"));
            Assert.That(dragWindowBridge, Does.Contain("UnityEngine.GUI::DragWindow_Injected"));
            Assert.That(dragWindowBridge, Does.Contain("m_XMin"));
            Assert.That(dragWindowBridge, Does.Contain("m_Height"));
            Assert.That(assemblyRewriter, Does.Contain("v22-proxy-component-query-filter"));
            Assert.That(overlayInstall, Does.Contain("assert_runtime_bundle.ps1"));
            Assert.That(overlayInstall, Does.Contain("-ReferenceRuntimeDir $RuntimeAssets"));
        });
    }

    [Test]
    public void SettingsImeDiagnosticsIdentifyNativeFocusStateAndAssemblyInstance()
    {
        var root = FindHooksRoot();
        var input = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "StArray.ModManager.Android",
            "UI",
            "ImGuiInputHandler.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(input, Does.Contain("[DEBUG-ime-native-v1]"));
            Assert.That(input, Does.Contain("textActive="));
            Assert.That(input, Does.Contain("touchSeq="));
            Assert.That(input, Does.Contain("s_pendingWantTextInput"));
            Assert.That(input, Does.Contain("AssemblyLoadContext.GetLoadContext"));
            Assert.That(input, Does.Contain("ManifestModule.ModuleVersionId"));
            Assert.That(input, Does.Contain("lock (ImeGate)"));
            Assert.That(input, Does.Contain("ImGuiImplAndroid.GetImeState()"));
            Assert.That(input, Does.Contain("overlayVisible || textInputActive"));
            Assert.That(input, Does.Contain("if (!nativeImeRequested)"));
        });
    }

    [Test]
    public void SettingsImeUsesOneOwnerAndClearsPreviousFocusDomain()
    {
        var root = FindHooksRoot();
        var input = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "StArray.ModManager.Android",
            "UI",
            "ImGuiInputHandler.cs"));
        var platform = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "StArray.ModManager.Android",
            "UI",
            "AndroidModManagerPlatformServices.cs"));
        var native = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "cimgui_compat.cpp"));
        var bootstrap = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
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
            "StArray.ModManager",
            "xphorror.PcModCompat",
            "src",
            "PcCompatManagedSettingsUnityBackend.cs"));
        var jni = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "StArray.ModManager.Android",
            "Native",
            "JNI.cs"));
        var jniHelper = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "StArray.ModManager.Android",
            "Native",
            "JniHelperNative.cs"));

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
            Assert.That(bootstrap, Does.Contain("isKeyboardActuallyVisible"));
            Assert.That(input, Does.Contain("TryReadKeyboardActuallyVisibleLocked"));
            Assert.That(input, Does.Contain("ShouldRetryKeyboardShow"));
            Assert.That(input, Does.Contain("CallStaticBooleanMethod0"));
            Assert.That(input, Does.Not.Contain("CallStaticIntMethod0(\n                s_keyboardVisibilityMethod"));
            Assert.That(jni, Does.Contain("CallStaticBooleanMethod0"));
            Assert.That(jniHelper, Does.Contain("jnihelper_call_static_boolean_method_a"));
            Assert.That(settings, Does.Contain("ReleaseInputFocus"));
            Assert.That(settings, Does.Contain("_guiSetKeyboardControl"));
            Assert.That(settings, Does.Contain("_guiSetHotControl"));
        });
    }

    [Test]
    public void InitialVisibleOverlayAdoptsManagerImeOwnerBeforeFirstTextFocus()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                StArray.ModManager.Android.UI.ImGuiInputHandler.ResolveImeOwner(
                    overlayVisible: true,
                    modalInputCapture: false),
                Is.EqualTo(StArray.ModManager.Android.UI.AndroidImeOwner.ModManager));
            Assert.That(
                StArray.ModManager.Android.UI.ImGuiInputHandler.ResolveImeOwner(
                    overlayVisible: false,
                    modalInputCapture: false),
                Is.EqualTo(StArray.ModManager.Android.UI.AndroidImeOwner.None));
            Assert.That(
                StArray.ModManager.Android.UI.ImGuiInputHandler.ResolveImeOwner(
                    overlayVisible: false,
                    modalInputCapture: false,
                    textInputActive: true),
                Is.EqualTo(StArray.ModManager.Android.UI.AndroidImeOwner.ModManager));
            Assert.That(
                StArray.ModManager.Android.UI.ImGuiInputHandler.ResolveImeOwner(
                    overlayVisible: true,
                    modalInputCapture: true),
                Is.EqualTo(StArray.ModManager.Android.UI.AndroidImeOwner.UnitySettings));
            Assert.That(
                StArray.ModManager.Android.UI.ImGuiInputHandler.ShouldShowKeyboard(
                    StArray.ModManager.Android.UI.AndroidImeOwner.ModManager,
                    overlayVisible: true,
                    nativeImeRequested: true,
                    focusBaselineReady: true),
                Is.True);
            Assert.That(
                StArray.ModManager.Android.UI.ImGuiInputHandler.ShouldShowKeyboard(
                    StArray.ModManager.Android.UI.AndroidImeOwner.UnitySettings,
                    overlayVisible: true,
                    nativeImeRequested: true,
                    focusBaselineReady: true),
                Is.False);
            Assert.That(
                StArray.ModManager.Android.UI.ImGuiInputHandler.ShouldShowKeyboard(
                    StArray.ModManager.Android.UI.AndroidImeOwner.None,
                    overlayVisible: false,
                    nativeImeRequested: true,
                    focusBaselineReady: true),
                Is.False);
            Assert.That(
                StArray.ModManager.Android.UI.ImGuiInputHandler.ShouldShowKeyboard(
                    StArray.ModManager.Android.UI.AndroidImeOwner.ModManager,
                    overlayVisible: false,
                    nativeImeRequested: true,
                    focusBaselineReady: true),
                Is.True,
                "an active hidden ImGui text widget still owns the IME");
        });
    }

    [Test]
    public void HiddenTextInputRequiresCurrentTouchEvidenceBeforeOpeningKeyboard()
    {
        var baseline = StArray.ModManager.Android.UI.ImGuiInputHandler
            .ResolveFocusBaseline(
                ownershipFrame: true,
                imeRequested: true,
                textInputActive: true,
                freshTouchActivatedTextInput: false,
                currentBaselineReady: false);

        Assert.That(
            StArray.ModManager.Android.UI.ImGuiInputHandler.ShouldShowKeyboard(
                StArray.ModManager.Android.UI.AndroidImeOwner.ModManager,
                overlayVisible: false,
                nativeImeRequested: true,
                focusBaselineReady: baseline),
            Is.False,
            "retained hidden focus must not open the keyboard by itself");

        baseline = StArray.ModManager.Android.UI.ImGuiInputHandler
            .ResolveFocusBaseline(
                ownershipFrame: true,
                imeRequested: true,
                textInputActive: true,
                freshTouchActivatedTextInput: true,
                currentBaselineReady: false);

        Assert.That(
            StArray.ModManager.Android.UI.ImGuiInputHandler.ShouldShowKeyboard(
                StArray.ModManager.Android.UI.AndroidImeOwner.ModManager,
                overlayVisible: false,
                nativeImeRequested: true,
                focusBaselineReady: baseline),
            Is.True);
    }

    [Test]
    public void ImeFrameBaselineQuarantinesOwnerFrameAndRequiresFreshTextTouch()
    {
        var baseline = StArray.ModManager.Android.UI.ImGuiInputHandler
            .ResolveFocusBaseline(
                ownershipFrame: true,
                imeRequested: false,
                textInputActive: false,
                freshTouchActivatedTextInput: false,
                currentBaselineReady: false);

        Assert.That(baseline, Is.False,
            "the owner transition frame may still contain the touch that opened the overlay");
        Assert.That(
            StArray.ModManager.Android.UI.ImGuiInputHandler.ShouldShowKeyboard(
                StArray.ModManager.Android.UI.AndroidImeOwner.ModManager,
                overlayVisible: true,
                nativeImeRequested: true,
                focusBaselineReady: baseline),
            Is.False);

        baseline = StArray.ModManager.Android.UI.ImGuiInputHandler
            .ResolveFocusBaseline(
                ownershipFrame: false,
                imeRequested: true,
                textInputActive: true,
                freshTouchActivatedTextInput: false,
                currentBaselineReady: baseline);
        Assert.That(baseline, Is.False,
            "retained text focus without a new touch must remain quarantined");

        baseline = StArray.ModManager.Android.UI.ImGuiInputHandler
            .ResolveFocusBaseline(
                ownershipFrame: false,
                imeRequested: true,
                textInputActive: true,
                freshTouchActivatedTextInput: true,
                currentBaselineReady: baseline);
        Assert.That(
            StArray.ModManager.Android.UI.ImGuiInputHandler.ShouldShowKeyboard(
                StArray.ModManager.Android.UI.AndroidImeOwner.ModManager,
                overlayVisible: true,
                nativeImeRequested: true,
                focusBaselineReady: baseline),
            Is.True,
            "a new touch that leaves a text widget active is genuine IME intent");
    }

    [Test]
    public void ImeGateRequiresEveryNormalizedNativeCondition()
    {
        Assert.That(
            StArray.ModManager.Android.UI.ImGuiInputHandler.ShouldShowKeyboard(
                StArray.ModManager.Android.UI.AndroidImeOwner.ModManager,
                overlayVisible: true,
                nativeImeRequested: true,
                focusBaselineReady: true),
            Is.True);
        Assert.That(
            StArray.ModManager.Android.UI.ImGuiInputHandler.ShouldShowKeyboard(
                StArray.ModManager.Android.UI.AndroidImeOwner.ModManager,
                overlayVisible: true,
                nativeImeRequested: false,
                focusBaselineReady: true),
            Is.False);
    }

    [Test]
    public void KeyboardRetryRequiresFreshTouchOnAnActiveTextInput()
    {
        var shouldRetry = StArray.ModManager.Android.UI.ImGuiInputHandler
            .ShouldRetryKeyboardShow(
                want: true,
                keyboardWasRequested: true,
                keyboardActuallyVisible: false,
                freshTouch: true,
                textInputActive: true);
        Assert.That(shouldRetry, Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(
                StArray.ModManager.Android.UI.ImGuiInputHandler.ShouldRetryKeyboardShow(
                    want: true,
                    keyboardWasRequested: true,
                    keyboardActuallyVisible: false,
                    freshTouch: false,
                    textInputActive: true),
                Is.False,
                "a missing keyboard alone must not cause an unsolicited popup");
            Assert.That(
                StArray.ModManager.Android.UI.ImGuiInputHandler.ShouldRetryKeyboardShow(
                    want: true,
                    keyboardWasRequested: true,
                    keyboardActuallyVisible: false,
                    freshTouch: true,
                    textInputActive: false),
                Is.False,
                "a touch on a non-text control must not retry the IME");
            Assert.That(
                StArray.ModManager.Android.UI.ImGuiInputHandler.ShouldRetryKeyboardShow(
                    want: true,
                    keyboardWasRequested: true,
                    keyboardActuallyVisible: true,
                    freshTouch: true,
                    textInputActive: true),
                Is.False);
        });
    }

    [Test]
    public void AndroidImeStateComesFromNativeTextFocusAndFreshTouchEvidence()
    {
        var root = FindHooksRoot();
        var input = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "StArray.ModManager.Android",
            "UI",
            "ImGuiInputHandler.cs"));
        var backends = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "StArray.ModManager.Android",
            "Native",
            "ImGuiBackends.cs"));
        var native = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "cimgui_compat.cpp"));

        Assert.Multiple(() =>
        {
            Assert.That(backends, Does.Contain(
                "EntryPoint = \"modmanager_imgui_get_ime_state\""));
            Assert.That(native, Does.Contain(
                "extern \"C\" int modmanager_imgui_get_ime_state("));
            Assert.That(native, Does.Contain("GImGui->IO.WantTextInput"));
            Assert.That(native, Does.Contain("GImGui->ActiveId"));
            Assert.That(native, Does.Contain("GImGui->InputTextState.ID"));
            Assert.That(native, Does.Contain("g_forwarded_touch_down_sequence"));
            Assert.That(input, Does.Contain("ImGuiImplAndroid.GetImeState()"));
            Assert.That(input, Does.Contain("TextInputActive"));
            Assert.That(input, Does.Contain("TouchDownSequence"));
            Assert.That(input, Does.Not.Contain("ref io.WantTextInput"),
                "IME ownership must not depend on a managed ImGuiIO field offset");
        });
    }

    [Test]
    public void AllImGuiRenderersCaptureImeBaselineBeforeBuildingUi()
    {
        var root = FindHooksRoot();
        var rendererPaths = new[]
        {
            Path.Combine(root, "StArray.ModManager", "StArray.ModManager.Android", "UI", "ImGuiEGLRender.cs"),
            Path.Combine(root, "StArray.ModManager", "StArray.ModManager.Android", "UI", "ImGuiRender.cs"),
            Path.Combine(root, "StArray.ModManager", "StArray.ModManager.Android", "UI", "ImGuiVulkanRenderer.cs")
        };

        foreach (var path in rendererPaths)
        {
            var source = File.ReadAllText(path);
            var newFrame = source.IndexOf("ImGui.NewFrame();", StringComparison.Ordinal);
            var beginIme = source.IndexOf("ImGuiInputHandler.BeginImeFrame();", StringComparison.Ordinal);
            var buildUi = source.IndexOf("BuildUI();", StringComparison.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(newFrame, Is.GreaterThanOrEqualTo(0), path);
                Assert.That(beginIme, Is.GreaterThan(newFrame), path);
                Assert.That(buildUi, Is.GreaterThan(beginIme), path);
            });
        }
    }

    [Test]
    public void OriginalSettingsModalOwnsAndroidTouchKeyboardAndBackBeforeGameplay()
    {
        var root = FindHooksRoot();
        var activity = File.ReadAllText(Path.Combine(
            root,
            "extra_menu_activity",
            "src",
            "com",
            "fizzd",
            "connectedworlds",
            "editorport",
            "ExtraMenuUnityPlayerActivity.java"));
        var bootstrap = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
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
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "cimgui_compat.cpp"));
        var presentationSink = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
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
            "StArray.ModManager",
            "Manager",
            "ModManagerUI.cs"));
        var asyncInput = File.ReadAllText(Path.Combine(
            root,
            "async_input",
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
            "StArray.ModManager",
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
        var source = File.ReadAllText(Path.Combine(
            root,
            "extra_menu_activity",
            "src",
            "com",
            "fizzd",
            "connectedworlds",
            "editorport",
            "ExtraMenuUnityPlayerActivity.java"));
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
        var source = File.ReadAllText(Path.Combine(
            root,
            "extra_menu_activity",
            "src",
            "com",
            "fizzd",
            "connectedworlds",
            "editorport",
            "ExtraMenuUnityPlayerActivity.java"));
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
        var activity = File.ReadAllText(Path.Combine(
            root,
            "extra_menu_activity",
            "src",
            "com",
            "fizzd",
            "connectedworlds",
            "editorport",
            "ExtraMenuUnityPlayerActivity.java"));
        var managerUi = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "StArray.ModManager",
            "Manager",
            "ModManagerUI.cs"));
        var native = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "cimgui_compat.cpp"));
        var renderer = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
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
        var launchStart = activity.IndexOf(
            "private static boolean requestStArrayModManager(",
            StringComparison.Ordinal);
        var launchEnd = activity.Length;
        Assert.That(launchStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(launchEnd, Is.GreaterThan(launchStart));
        var launch = activity[launchStart..launchEnd];

        Assert.Multiple(() =>
        {
            Assert.That(launch, Does.Contain("stArrayModManagerInputForwarding = true;"));
            Assert.That(launch, Does.Not.Contain(
                "stArrayModManagerInputForwarding = showOverlay;"));
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
            Assert.That(renderer, Does.Contain("if (!EglHooks.InstallHooks())"));
            Assert.That(renderer, Does.Contain("EGL hook install returned false"));
            Assert.That(forward, Does.Not.Contain(
                "modmanager_overlay_ui_is_visible() == 0"));
            Assert.That(forward, Does.Contain("if (!manager_visible && !consume)"));
        });
    }

    [Test]
    public void EditorDeathRetryRestoresOfficialInputBeforeFail2()
    {
        var root = FindHooksRoot();
        var asyncInput = File.ReadAllText(Path.Combine(
            root,
            "async_input",
            "async_input.c"));

        var fail2Start = asyncInput.IndexOf(
            "static void hooked_fail2_action",
            StringComparison.Ordinal);
        // Bound the body with the next function definition. This used to anchor on
        // disable_async_for_dlc_if_needed, which was removed with the DLC fuse; the assertion
        // below is about restoring official input before Fail2 and never depended on DLC.
        var fail2End = asyncInput.IndexOf(
            "static void hooked_update_input_internal",
            fail2Start,
            StringComparison.Ordinal);
        Assert.That(fail2Start, Is.GreaterThanOrEqualTo(0));
        Assert.That(fail2End, Is.GreaterThan(fail2Start));
        var fail2 = asyncInput[fail2Start..fail2End];

        var validStart = asyncInput.IndexOf(
            "static int __attribute__((unused)) hooked_valid_triggered",
            StringComparison.Ordinal);
        var validEnd = asyncInput.IndexOf(
            "static int __attribute__((unused)) hooked_valid_released",
            validStart,
            StringComparison.Ordinal);
        Assert.That(validStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(validEnd, Is.GreaterThan(validStart));
        var valid = asyncInput[validStart..validEnd];

        Assert.Multiple(() =>
        {
            Assert.That(asyncInput, Does.Contain(
                "{\"\", \"scrController\", \"Fail2Action\", 0"));
            Assert.That(fail2, Does.Contain("close_async_capture();"));
            Assert.That(fail2, Does.Contain(
                "g_original_fail2_action(self, method);"));
            Assert.That(fail2.IndexOf(
                    "close_async_capture();",
                    StringComparison.Ordinal),
                Is.LessThan(fail2.IndexOf(
                    "g_original_fail2_action(self, method);",
                    StringComparison.Ordinal)));
            Assert.That(valid, Does.Contain(
                "return g_original_valid_triggered(self, method);"));
            Assert.That(asyncInput, Does.Not.Contain(
                "g_editor_retry_input_pending"));
            Assert.That(asyncInput, Does.Not.Contain(
                "consume_editor_retry_input"));
        });
    }

    [Test]
    public void TouchObserverPreservesPointerEdgesWithoutConsumingThem()
    {
        var root = FindHooksRoot();
        var bootstrap = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
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
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "pccompat_hook_rules.cpp"));
        var realtime = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
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
        var activity = File.ReadAllText(Path.Combine(
            root,
            "extra_menu_activity",
            "src",
            "com",
            "fizzd",
            "connectedworlds",
            "editorport",
            "ExtraMenuUnityPlayerActivity.java"));
        var bootstrap = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
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
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "realtime_event_core.h"));
        var realtimeSource = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
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
        var asyncSource = File.ReadAllText(Path.Combine(root, "async_input", "async_input.c"));
        var observerAbi = File.ReadAllText(Path.Combine(
            root,
            "common",
            "async_input_observer_abi.h"));
        var bridge = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "async_input_observer_bridge.cpp"));
        var realtimeHeader = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "realtime_event_core.h"));
        var realtimeSource = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
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
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "realtime_event_core.h"));
        var realtime = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "realtime_event_core.cpp"));
        var hooks = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "pccompat_hook_rules.cpp"));
        var asyncBridge = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
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
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "realtime_event_core.cpp"));
        var worker = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "hud_logic_worker.cpp"));
        var hooks = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
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
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "hud_logic_worker.cpp"));
        var realtime = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "realtime_event_core.cpp"));
        var native = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "pccompat_hook_rules.cpp"));
        var managed = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatNativeHookRules.cs"));
        var bridge = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
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
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "hud_logic_worker.cpp"));
        var native = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "pccompat_hook_rules.cpp"));
        var managed = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatNativeHookRules.cs"));
        var bridge = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
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
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "realtime_event_core.cpp"));
        var header = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "realtime_event_core.h"));
        var cmake = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
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
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "pccompat_hook_rules.cpp"));
        var bridge = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "xphorror.PcModCompat",
            "src",
            "PcCompatLegacyInputBridge.cs"));
        var rewriter = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "xphorror.PcModCompat",
            "tools",
            "ModAssemblyRewriter",
            "Program.cs"));
        var androidCatalog = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatAndroidManagedAssemblyRewrite.cs"));
        var androidBridge = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatNativeHookRules.cs"));
        var abiBridge = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatAbiBridge.cs"));
        var delegateSupport = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Il2CppInterop",
            "Il2CppInterop.Runtime",
            "DelegateSupport.cs"));

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
                "xphorror.pcmod-managed-cache.v86-keyviewer-lane-origin-prefix"));
            Assert.That(androidCatalog, Does.Contain("PcCompatManagedImGuiBridge.v20-selection-grid-transition"));
            Assert.That(androidCatalog, Does.Contain("PcCompatManagedSettingsTransaction.v1"));
            Assert.That(androidCatalog, Does.Contain("PcCompatManagedSettingsDelegateBridge.v1"));
            Assert.That(
                androidCatalog,
                Does.Contain("builder.AppendLine(ModAssemblyRewriteApi.FormatVersion)"),
                "managed rewrite cache key must include the rewriter schema");
            Assert.That(androidCatalog, Does.Contain("spec.BoxLastValueTypeArgument"));
            Assert.That(androidCatalog, Does.Contain("spec.AllowValueTypeReturnUnbox"));
            Assert.That(
                ModAssemblyRewriteApi.FormatVersion,
                Is.EqualTo(ModAssemblyRewriteApi.FormatVersion));
            Assert.That(androidCatalog, Does.Contain("PcCompatManagedComponentBridge.v13-native-component-result-rewrap"));
            // The registry has to be part of the cache key: registering a type lifts the base-chain
            // rule for it and blanks its base constructor call, so a list change without a key change
            // would leave a stale rewritten assembly in place.
            Assert.That(
                androidCatalog,
                Does.Contain("managed-render-component|"),
                "the render component registry must participate in the managed cache key");
            Assert.That(
                androidCatalog,
                Does.Contain("BuildManagedRenderComponents(staticScan)"),
                "the production rewrite call must pass the render component registry");
            Assert.That(
                androidCatalog,
                Does.Contain("nameof(PcCompatManagedComponentBridge.SetAnchoredPosition)"),
                "shared RectTransform.anchoredPosition must stay routed through the arbitration " +
                "registry; an unrouted setter lets one MOD's offset become another's baseline");
            Assert.That(androidCatalog, Does.Contain(
                "PcCompatManagedPathBridge.v5-directory-file-enumeration"));
            Assert.That(androidCatalog, Does.Contain("PcCompatManagedLogBridge.v2-object-messages"));
            Assert.That(androidCatalog, Does.Contain("PcCompatCollectionBridge.v4-null-source-initialization"));
            Assert.That(androidCatalog, Does.Contain("PcCompatJsonBridge.v1"));
            Assert.That(
                androidCatalog,
                Does.Contain("nameof(PcCompatJsonBridge.FromJson)"),
                "JsonUtility.FromJson<T> must stay bridged even though the proxy signature matches; " +
                "the MOD's T has no IL2CPP class-table entry, so forwarding fails at runtime with " +
                "nothing in the audit to show it");
            Assert.That(
                androidCatalog,
                Does.Contain("nameof(PcCompatCollectionBridge.AddToBoundList)"),
                "fallbackFontAssetTable must stay a registered writable collection; on the plain " +
                "copying converter a MOD's .Add reaches nothing and the CJK fallback font is " +
                "silently dropped");
            Assert.That(
                androidCatalog,
                Does.Contain("managed-writable-collection|"),
                "the writable-collection registry must be part of the managed cache key");
            Assert.That(
                androidCatalog,
                Does.Contain("nameof(PcCompatManagedLogBridge.Log)"),
                "Debug.Log(object) must stay routed to the host logger; forwarding it to the proxy " +
                "would hand an arbitrary CoreCLR object to the IL2CPP domain");
            Assert.That(androidCatalog, Does.Contain("PcCompatManagedNetworkBridge.v1"));
            Assert.That(androidCatalog, Does.Contain("PcCompatManagedEventSubscriptionBridge.v4-proxy-source-delegate"));
            Assert.That(
                abiBridge,
                Does.Contain("RegisterSourceDelegateResolver"),
                "generated UnityAction proxies must be resolved back to their rooted CoreCLR delegate");
            Assert.That(abiBridge, Does.Contain("DelegateSupport.TryResolveManagedDelegate"));
            Assert.That(delegateSupport, Does.Contain("AndroidRootedDelegates.TryGetValue"));
            Assert.That(androidCatalog, Does.Contain("nameof(PcCompatManagedComponentBridge.SetEnabled)"));
            Assert.That(androidCatalog, Does.Contain("PcCompatManagedIoBridge.v2"));
            Assert.That(androidCatalog, Does.Contain("PcCompatManagedPollingBridge.v1"));
            Assert.That(native, Does.Contain("modmanager_pccompat_read_raw_input_events"));
            Assert.That(native, Does.Contain("sizeof(PcCompatRawInputEventV1) == 88"));
            Assert.That(native, Does.Contain("dropped_before_cursor = read.dropped_before_cursor"));
            Assert.That(androidBridge, Does.Contain("ReadRawInputEventsNative"));
            Assert.That(androidBridge, Does.Contain("DroppedBeforeCursor = header.DroppedBeforeCursor"));
            Assert.That(androidCatalog, Does.Contain(
                "PcCompatLegacyInputBridge.v3-hotpath-diagnostics-removed"));
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
            "StArray.ModManager",
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
            "StArray.ModManager",
            "StArray.ModManager.Android",
            "UI",
            "AndroidModManagerPlatformServices.cs"));
        var bridge = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
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
            "StArray.ModManager",
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
            "StArray.ModManager",
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
    public void UnityHudSourceRemovalSchedulesOwnerScopedHideAndDestroy()
    {
        var root = FindHooksRoot();
        var bridge = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatUnityHudBridge.cs"));
        var installStart = bridge.IndexOf("public static void Install()", StringComparison.Ordinal);
        var installEnd = bridge.IndexOf(
            "[UnmanagedCallersOnly",
            installStart,
            StringComparison.Ordinal);
        var applyStart = bridge.IndexOf(
            "private static void ApplySourceSnapshot(bool forceResourceRefresh)",
            StringComparison.Ordinal);
        var applyEnd = bridge.IndexOf(
            "private static void FailSource",
            applyStart,
            StringComparison.Ordinal);

        Assert.That(installStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(installEnd, Is.GreaterThan(installStart));
        Assert.That(applyStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(applyEnd, Is.GreaterThan(applyStart));
        var install = bridge[installStart..installEnd];
        var apply = bridge[applyStart..applyEnd];
        var destroy = apply.IndexOf("Surfaces[owner].Destroy()", StringComparison.Ordinal);
        var remove = apply.IndexOf("Surfaces.Remove(owner)", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(install, Does.Contain(
                "PcCompatUnityHudRuntime.RegisterSourcesChangedSink"));
            Assert.That(bridge, Does.Contain("OnSourcesChanged"));
            Assert.That(bridge, Does.Contain(
                "PcCompatResourceBundleLoader.TryScheduleUnityMainWork"));
            Assert.That(apply, Does.Contain("PcCompatUnityHudRuntime.SnapshotSources()"));
            Assert.That(apply, Does.Contain("hidden.SetVisible(false)"));
            Assert.That(apply, Does.Contain("!registeredOwners.Contains(owner)"));
            Assert.That(destroy, Is.GreaterThanOrEqualTo(0));
            Assert.That(remove, Is.GreaterThan(destroy),
                "The owner surface must be destroyed before it leaves the registry.");
        });
    }

    [Test]
    public void ExternalKeyboardDetectionRejectsNonAlphabeticKeySources()
    {
        var root = FindHooksRoot();
        var bootstrap = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
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
            "StArray.ModManager",
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
            "StArray.ModManager",
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
            "StArray.ModManager",
            "StArray.ModManager.csproj"));
        var androidProject = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "StArray.ModManager.Android",
            "StArray.ModManager.Android.csproj"));
        var buildScript = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "build_android_single.ps1"));

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
            "StArray.ModManager",
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatDobbyBridge.cs"));
        var project = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "StArray.ModManager.Android",
            "StArray.ModManager.Android.csproj"));
        var cmake = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "CMakeLists.txt"));
        var nativeRules = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
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
                    "StArray.ModManager",
                    "StArray.ModManager.Android",
                    "Native",
                    "UnityResolve.cs")),
                Is.False);
            Assert.That(
                File.Exists(Path.Combine(
                    root,
                    "StArray.ModManager",
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
            "StArray.ModManager",
            "xphorror.PcModCompat",
            "src",
            "PcCompatKeyViewerPreviewRuntime.cs"));
        var pcCompatRuntime = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "xphorror.PcModCompat",
            "src",
            "PcCompatRuntime.cs"));
        var androidReader = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
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
            if (Directory.Exists(Path.Combine(directory.FullName, "extra_menu_activity")) &&
                Directory.Exists(Path.Combine(directory.FullName, "StArray.ModManager")))
                return directory.FullName;
            directory = directory.Parent;
        }

        Assert.Fail("Could not find ADOFAI_312_HOOKS root from test directory");
        return string.Empty;
    }
}

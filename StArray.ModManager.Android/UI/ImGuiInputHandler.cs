using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using ImGuiNET;
using StArray.ModManager.Android.Native;
using StArray.ModManager.Manager;

namespace StArray.ModManager.Android.UI;

public enum AndroidImeOwner
{
    None,
    ModManager,
    UnitySettings
}

/// <summary>ImGui input handler / 输入处理器 — touch/key hooks + IME control</summary>
public static class ImGuiInputHandler
{
    /// <summary>ImGui 上下文就绪后由渲染器设置</summary>
    public static bool IsInitialized { get; set; }

    private static InitializeMotionEventDelegate? s_initializeMotionEvent;

    private delegate int InitializeMotionEventDelegate(IntPtr self, IntPtr motionEvent, IntPtr message);

    private static bool s_hooksInstalled;
    private static bool s_imeCallbacksRegistered;
    private static readonly object ImeGate = new();
    private static bool s_wantTextInputLast;
    private static bool s_pendingWantTextInput;
    private static AndroidImeOwner s_imeOwner;
    private static bool s_modManagerObservedReleasedFocus;
    private static bool s_imeOwnerNeedsFrameBaseline;
    private static bool s_imeTouchSequenceKnown;
    private static bool s_imeFrameHadFreshTouch;
    private static bool s_nativeImeStateUnavailable;
    private static bool s_hasImeDiagnosticState;
    private static ulong s_lastImeTouchSequence;
    private static int s_lastImeDiagnosticSignature;
    private static long s_pendingWantTextInputSinceMs;
    private static long s_lastImeDispatchMs;
    private static int s_touchLogBudget = 4;
    private static int s_imeDiagnosticBudget = 24;
    private const long ImeStableDelayMs = 80;
    private const long ImeDispatchMinIntervalMs = 200;

    [DllImport("starray_modmanager", EntryPoint = "modmanager_overlay_ui_is_visible")]
    private static extern int OverlayUiIsVisibleNative();

    [DllImport("starray_modmanager", EntryPoint = "modmanager_modal_input_is_active")]
    private static extern int ModalInputCaptureIsActiveNative();

    public static void RegisterImeCallbacks()
    {
        if (s_imeCallbacksRegistered)
            return;
        s_imeCallbacksRegistered = true;

        // IME 字符回调：Java nativeSendChar → C → 此回调 → ImGui
        NativeFunctions.SetOnAcceptCharCallback(codepoint =>
        {
            try
            {
                if (!IsInitialized) return;
                ImGui.GetIO().AddInputCharacter(codepoint);
            }
            catch (Exception ex)
            {
                Logger.Error(nameof(ImGuiInputHandler), $"AcceptChar failed: {ex}");
            }
        });

        // IME 特殊键回调：Java nativeSendKey → C → 此回调 → ImGui
        NativeFunctions.SetOnAcceptKeyCallback(keyCode =>
        {
            try
            {
                if (!IsInitialized) return;
                var io = ImGui.GetIO();
                switch (keyCode)
                {
                    case 67:  io.AddKeyEvent(ImGuiKey.Backspace, true);  io.AddKeyEvent(ImGuiKey.Backspace, false); break;   // KEYCODE_DEL
                    case 112: io.AddKeyEvent(ImGuiKey.Delete, true);     io.AddKeyEvent(ImGuiKey.Delete, false);    break;   // KEYCODE_FORWARD_DEL
                    case 66:  io.AddKeyEvent(ImGuiKey.Enter, true);      io.AddKeyEvent(ImGuiKey.Enter, false);     break;   // KEYCODE_ENTER
                    case 21:  io.AddKeyEvent(ImGuiKey.LeftArrow, true);  io.AddKeyEvent(ImGuiKey.LeftArrow, false); break;   // KEYCODE_DPAD_LEFT
                    case 22:  io.AddKeyEvent(ImGuiKey.RightArrow, true); io.AddKeyEvent(ImGuiKey.RightArrow, false); break;  // KEYCODE_DPAD_RIGHT
                }
            }
            catch (Exception ex)
            {
                Logger.Error(nameof(ImGuiInputHandler), $"AcceptKey failed: {ex}");
            }
        });
    }

    /// <summary>
    /// 安装触摸事件和按键事件 Hook
    /// </summary>
    public static bool InstallHooks()
    {
        if (s_hooksInstalled)
            return true;

        // —— 触摸事件 Hook ——
        string consumerSymbol = "_ZN7android13InputConsumer21initializeMotionEventEPNS_11MotionEventEPKNS_12InputMessageE";
        IntPtr consumerAddr = Dobby.SymbolResolver("libinput.so", consumerSymbol);
        if (consumerAddr == IntPtr.Zero)
        {
            Logger.Warn(nameof(ImGuiInputHandler), "libinput initializeMotionEvent symbol not found");
            return false;
        }

        int hookResult = Dobby.Hook(consumerAddr,
            typeof(ImGuiInputHandler).GetMethod(nameof(OnTouchEvent))!.MethodHandle.GetFunctionPointer(),
            out var origin);
        if (hookResult != 0 || origin == IntPtr.Zero)
        {
            Logger.Warn(nameof(ImGuiInputHandler), $"Input hook failed: {hookResult}");
            return false;
        }
        s_initializeMotionEvent = Marshal.GetDelegateForFunctionPointer<InitializeMotionEventDelegate>(origin);
        s_hooksInstalled = true;

        Logger.Info(nameof(ImGuiInputHandler), "Input hooks installed");
        return true;
    }

    /// <summary>
    /// Upstream compatibility entry point. Android production input ownership
    /// remains in Activity/modal routing, so this does not install a second
    /// Unity MotionEvent hook.
    /// </summary>
    public static void InstallInputHooks() => RegisterImeCallbacks();

    /// <summary>Upstream callback shape retained as a compatibility facade.</summary>
    public static unsafe bool OnInitializeMotionEvent(void* @event, void* message)
    {
        if (@event != null && InputEvents.HasSubscribers)
            InputEvents.RaiseFrom((nint)@event);
        if (IsInitialized && @event != null)
            ImGuiImplAndroid.HandleInputEvent((IntPtr)@event);
        return true;
    }

    /// <summary>
    /// Physical Android hooks are owned by the process-wide HookBroker and are
    /// intentionally not detached during MOD unload.
    /// </summary>
    public static unsafe bool OnInitializeMotionEventOriginal(void* @event, void* message) => true;

    /// <summary>Compatibility no-op; Android hooks are process-lifetime hooks.</summary>
    public static void UninstallHooks() { }

    /// <summary>触摸事件 Hook 回调</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int OnTouchEvent(IntPtr self, IntPtr motionEvent, IntPtr message)
    {
        var original = s_initializeMotionEvent;
        int result = 0;

        try
        {
            if (original != null)
                result = original(self, motionEvent, message);
        }
        catch (Exception ex)
        {
            Logger.Error(nameof(ImGuiInputHandler), $"initializeMotionEvent original failed: {ex}");
        }

        try
        {
            if (motionEvent != IntPtr.Zero && InputEvents.HasSubscribers)
                InputEvents.RaiseFrom(motionEvent);

            if (IsInitialized && motionEvent != IntPtr.Zero)
            {
                int handled = ImGuiImplAndroid.HandleInputEvent(motionEvent);
                if (s_touchLogBudget > 0)
                {
                    s_touchLogBudget--;
                    Logger.Info(nameof(ImGuiInputHandler),
                        $"Touch forwarded handled={handled} motionEvent=0x{motionEvent:X}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(nameof(ImGuiInputHandler), $"HandleInputEvent failed: {ex}");
        }

        return result;
    }

    private static JavaClass? s_utilsClass;
    private static nint s_showKeyboardMethod;
    private static nint s_keyboardVisibilityMethod;
    private static bool s_keyboardVisibilityProbeUnavailable;

    public static void SetImeOwner(AndroidImeOwner owner)
    {
        lock (ImeGate)
        {
            SetImeOwnerLocked(owner);
        }
    }

    private static void SetImeOwnerLocked(AndroidImeOwner owner)
    {
        if (s_imeOwner == owner)
            return;

        s_imeOwner = owner;
        if (owner == AndroidImeOwner.ModManager)
        {
            // Classify WantTextInput at the start of the next frame. A true
            // value there may be a stale ActiveId from the previous lifetime.
            s_modManagerObservedReleasedFocus = false;
            s_imeOwnerNeedsFrameBaseline = true;
        }
        else
        {
            s_modManagerObservedReleasedFocus = false;
            s_imeOwnerNeedsFrameBaseline = false;
        }

        s_pendingWantTextInput = false;
        s_pendingWantTextInputSinceMs = Environment.TickCount64;
        s_imeTouchSequenceKnown = false;
        s_imeFrameHadFreshTouch = false;
        s_hasImeDiagnosticState = false;
        if (owner != AndroidImeOwner.ModManager)
            DispatchKeyboardLocked(false, force: true);
        else
            s_wantTextInputLast = false;
    }

    /// <summary>
    /// Synchronizes the native owner and captures ImGui's text-input state
    /// before BuildUI can change the active text widget.
    /// </summary>
    public static void BeginImeFrame()
    {
        lock (ImeGate)
        {
            try
            {
                if (!IsInitialized)
                    return;

                if (!TryReadNativeImeStateLocked(out var imeState))
                {
                    var fallbackOwner = ResolveNativeImeOwnerLocked(
                        out _,
                        out _,
                        textInputActive: false);
                    if (fallbackOwner != s_imeOwner)
                        SetImeOwnerLocked(fallbackOwner);
                    return;
                }

                s_imeFrameHadFreshTouch = ObserveTouchSequenceLocked(
                    imeState.TouchDownSequence);
                var nativeOwner = ResolveNativeImeOwnerLocked(
                    out _,
                    out _,
                    imeState.TextInputActive);
                if (nativeOwner != s_imeOwner)
                {
                    var freshTouch = s_imeFrameHadFreshTouch;
                    SetImeOwnerLocked(nativeOwner);
                    s_imeFrameHadFreshTouch = freshTouch;
                }

                if (s_imeOwner != AndroidImeOwner.ModManager)
                    return;

                s_modManagerObservedReleasedFocus = ResolveFocusBaseline(
                    s_imeOwnerNeedsFrameBaseline,
                    imeState.ImeRequested,
                    imeState.TextInputActive,
                    freshTouchActivatedTextInput: false,
                    s_modManagerObservedReleasedFocus);
                s_imeOwnerNeedsFrameBaseline = false;
            }
            catch (Exception ex)
            {
                Logger.Error(nameof(ImGuiInputHandler), $"BeginImeFrame failed: {ex}");
            }
        }
    }

    internal static bool ResolveFocusBaseline(
        bool ownershipFrame,
        bool imeRequested,
        bool textInputActive,
        bool freshTouchActivatedTextInput,
        bool currentBaselineReady)
    {
        // A real touch that activated a text widget is stronger evidence than
        // the owner transition marker. The marker only quarantines focus
        // retained from the previous owner lifetime.
        if (textInputActive && freshTouchActivatedTextInput)
            return true;
        if (ownershipFrame)
            return false;
        if (!imeRequested && !textInputActive)
            return true;
        return currentBaselineReady;
    }

    private static bool ObserveTouchSequenceLocked(ulong touchDownSequence)
    {
        if (!s_imeTouchSequenceKnown)
        {
            s_imeTouchSequenceKnown = true;
            s_lastImeTouchSequence = touchDownSequence;
            return false;
        }

        if (s_lastImeTouchSequence == touchDownSequence)
            return false;

        s_lastImeTouchSequence = touchDownSequence;
        return true;
    }

    private static bool TryReadNativeImeStateLocked(out ImGuiImeState state)
    {
        state = default;
        if (s_nativeImeStateUnavailable)
            return false;

        try
        {
            state = ImGuiImplAndroid.GetImeState();
            return state.ContextReady;
        }
        catch (EntryPointNotFoundException ex)
        {
            s_nativeImeStateUnavailable = true;
            Logger.Error(
                nameof(ImGuiInputHandler),
                $"Native ImGui IME state bridge is unavailable: {ex.Message}");
            return false;
        }
    }

    private static AndroidImeOwner ResolveNativeImeOwnerLocked(
        out bool overlayVisible,
        out bool modalInputCapture,
        bool textInputActive)
    {
        try
        {
            overlayVisible = OverlayUiIsVisibleNative() != 0;
        }
        catch
        {
            overlayVisible = true;
        }

        try
        {
            modalInputCapture = ModalInputCaptureIsActiveNative() != 0;
        }
        catch
        {
            // Older host binaries may not expose the modal probe. Keep the
            // explicit owner supplied by the platform service.
            modalInputCapture = s_imeOwner == AndroidImeOwner.UnitySettings;
        }

        return ResolveImeOwner(overlayVisible, modalInputCapture, textInputActive);
    }

    /// <summary>根据 ImGui 文本输入状态切换软键盘</summary>
    public static void UpdateIme()
    {
        lock (ImeGate)
            UpdateImeLocked();
    }

    private static void UpdateImeLocked()
    {
        try
        {
            if (!IsInitialized) return;
            if (!TryReadNativeImeStateLocked(out var imeState))
            {
                var fallbackOwner = ResolveNativeImeOwnerLocked(
                    out _,
                    out _,
                    textInputActive: false);
                if (fallbackOwner != s_imeOwner)
                    SetImeOwnerLocked(fallbackOwner);
                return;
            }

            s_imeFrameHadFreshTouch |= ObserveTouchSequenceLocked(
                imeState.TouchDownSequence);
            bool overlayVisible;
            var nativeOwner = ResolveNativeImeOwnerLocked(
                out overlayVisible,
                out _,
                imeState.TextInputActive);
            if (nativeOwner != s_imeOwner)
            {
                var freshTouchOnOwnerChange = s_imeFrameHadFreshTouch;
                SetImeOwnerLocked(nativeOwner);
                s_imeFrameHadFreshTouch = freshTouchOnOwnerChange;
            }

            if (s_imeOwner == AndroidImeOwner.ModManager)
            {
                s_modManagerObservedReleasedFocus = ResolveFocusBaseline(
                    s_imeOwnerNeedsFrameBaseline,
                    imeState.ImeRequested,
                    imeState.TextInputActive,
                    s_imeFrameHadFreshTouch && imeState.TextInputActive,
                    s_modManagerObservedReleasedFocus);
            }
            else
            {
                s_modManagerObservedReleasedFocus = false;
            }
            s_imeOwnerNeedsFrameBaseline = false;

            bool want = ShouldShowKeyboard(
                s_imeOwner,
                overlayVisible,
                imeState.ImeRequested,
                s_modManagerObservedReleasedFocus);
            bool keyboardActuallyVisibleKnown = false;
            bool keyboardActuallyVisible = false;
            if (want && s_wantTextInputLast)
            {
                keyboardActuallyVisibleKnown = TryReadKeyboardActuallyVisibleLocked(
                    out keyboardActuallyVisible);
            }
            long now = Environment.TickCount64;

            if (want != s_pendingWantTextInput)
            {
                s_pendingWantTextInput = want;
                s_pendingWantTextInputSinceMs = now;
            }

            LogImeStateChangeLocked(imeState, overlayVisible, want);
            bool freshTouch = s_imeFrameHadFreshTouch;
            s_imeFrameHadFreshTouch = false;

            if (keyboardActuallyVisibleKnown &&
                ShouldRetryKeyboardShow(
                    want,
                    s_wantTextInputLast,
                    keyboardActuallyVisible,
                    freshTouch,
                    imeState.TextInputActive) &&
                now - s_lastImeDispatchMs >= ImeDispatchMinIntervalMs)
            {
                DispatchKeyboardLocked(true, force: true);
                return;
            }

            if (s_pendingWantTextInput == s_wantTextInputLast)
                return;
            if (now - s_pendingWantTextInputSinceMs < ImeStableDelayMs)
                return;
            if (now - s_lastImeDispatchMs < ImeDispatchMinIntervalMs)
                return;

            DispatchKeyboardLocked(s_pendingWantTextInput, force: false);
        }
        catch (Exception ex)
        {
            Logger.Error(nameof(ImGuiInputHandler), $"UpdateIme failed: {ex}");
        }
    }

    internal static AndroidImeOwner ResolveImeOwner(
        bool overlayVisible,
        bool modalInputCapture)
        => ResolveImeOwner(overlayVisible, modalInputCapture, textInputActive: false);

    internal static AndroidImeOwner ResolveImeOwner(
        bool overlayVisible,
        bool modalInputCapture,
        bool textInputActive)
        => modalInputCapture
            ? AndroidImeOwner.UnitySettings
            : overlayVisible || textInputActive
                ? AndroidImeOwner.ModManager
                : AndroidImeOwner.None;

    internal static bool ShouldShowKeyboard(
        AndroidImeOwner owner,
        bool overlayVisible,
        bool nativeImeRequested,
        bool focusBaselineReady)
    {
        if (!nativeImeRequested)
            return false;
        if (owner != AndroidImeOwner.ModManager)
            return false;
        return focusBaselineReady;
    }

    internal static bool ShouldRetryKeyboardShow(
        bool want,
        bool keyboardWasRequested,
        bool keyboardActuallyVisible,
        bool freshTouch,
        bool textInputActive)
        => want &&
           keyboardWasRequested &&
           !keyboardActuallyVisible &&
           freshTouch &&
           textInputActive;

    private static bool TryReadKeyboardActuallyVisibleLocked(out bool visible)
    {
        visible = false;
        if (s_keyboardVisibilityProbeUnavailable)
            return false;

        try
        {
            s_utilsClass ??= new JavaClass(
                "com.fizzd.connectedworlds.editorport.StArrayModManagerBootstrap");
            if (s_keyboardVisibilityMethod == 0)
            {
                s_keyboardVisibilityMethod = s_utilsClass.GetStaticMethodID(
                    "isKeyboardActuallyVisible",
                    "()Z");
            }

            if (s_keyboardVisibilityMethod == 0)
            {
                s_keyboardVisibilityProbeUnavailable = true;
                Logger.Warn(
                    nameof(ImGuiInputHandler),
                    "Keyboard visibility probe method is unavailable");
                return false;
            }

            visible = s_utilsClass.CallStaticBooleanMethod0(
                s_keyboardVisibilityMethod);
            return true;
        }
        catch (Exception ex)
        {
            s_keyboardVisibilityProbeUnavailable = true;
            Logger.Warn(
                nameof(ImGuiInputHandler),
                $"Keyboard visibility probe failed: {ex.Message}");
            return false;
        }
    }

    private static void LogImeStateChangeLocked(
        ImGuiImeState state,
        bool overlayVisible,
        bool want)
    {
        if (s_imeDiagnosticBudget <= 0)
            return;

        var hash = new HashCode();
        hash.Add(s_imeOwner);
        hash.Add(state.Flags);
        hash.Add(state.ActiveId);
        hash.Add(state.InputTextId);
        hash.Add(state.TouchDownSequence);
        hash.Add(s_modManagerObservedReleasedFocus);
        hash.Add(s_imeOwnerNeedsFrameBaseline);
        hash.Add(s_imeFrameHadFreshTouch);
        hash.Add(overlayVisible);
        hash.Add(want);
        hash.Add(s_pendingWantTextInput);
        hash.Add(s_wantTextInputLast);
        int signature = hash.ToHashCode();
        if (s_hasImeDiagnosticState && signature == s_lastImeDiagnosticSignature)
            return;

        s_hasImeDiagnosticState = true;
        s_lastImeDiagnosticSignature = signature;
        s_imeDiagnosticBudget--;
        var loadContext = AssemblyLoadContext.GetLoadContext(typeof(ImGuiInputHandler).Assembly);
        Logger.Info(
            "PcCompatSettingsDiag",
            "[DEBUG-ime-native-v1] " +
            $"owner={s_imeOwner} flags=0x{(int)state.Flags:X2} " +
            $"active=0x{state.ActiveId:X8} input=0x{state.InputTextId:X8} " +
            $"textActive={state.TextInputActive} requested={state.ImeRequested} " +
            $"touchSeq={state.TouchDownSequence} freshTouch={s_imeFrameHadFreshTouch} " +
            $"baseline={s_modManagerObservedReleasedFocus} ownerBaseline={s_imeOwnerNeedsFrameBaseline} " +
            $"overlayVisible={overlayVisible} want={want} pending={s_pendingWantTextInput} " +
            $"last={s_wantTextInputLast} tid={Environment.CurrentManagedThreadId} " +
            $"alc={loadContext?.Name ?? "default"} mvid={typeof(ImGuiInputHandler).Assembly.ManifestModule.ModuleVersionId}");
    }

    private static void DispatchKeyboardLocked(bool show, bool force)
    {
        if (!force && show == s_wantTextInputLast)
            return;
        s_utilsClass ??= new JavaClass(
            "com.fizzd.connectedworlds.editorport.StArrayModManagerBootstrap");
        if (s_showKeyboardMethod == 0)
            s_showKeyboardMethod = s_utilsClass.GetStaticMethodID("showKeyboard", "(Z)V");
        if (s_showKeyboardMethod == 0)
        {
            Logger.Warn(nameof(ImGuiInputHandler), "showKeyboard method not found");
            return;
        }
        s_utilsClass.CallStaticVoidMethod1(s_showKeyboardMethod, show);
        s_wantTextInputLast = show;
        s_lastImeDispatchMs = Environment.TickCount64;
        Logger.Info(nameof(ImGuiInputHandler), $"IME owner={s_imeOwner} {(show ? "Show" : "Hide")}");
    }
}

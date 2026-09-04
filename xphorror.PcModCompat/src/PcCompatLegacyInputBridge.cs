using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Xphorror.PcModCompat;

public sealed record PcCompatLegacyInputDiagnosticSnapshot(
    int ThreadCount,
    IReadOnlyList<int> ThreadIds,
    long UnityHeldCount,
    long WindowsHeldCount,
    long UnityEdgeCount,
    long AnyKeyEdgeCount,
    long MatchedCount,
    long TrueCount,
    long LastSourceSequence,
    long LastRegistrationGeneration)
{
    public long QueryCount =>
        UnityHeldCount + WindowsHeldCount + UnityEdgeCount + AnyKeyEdgeCount;
}

public static unsafe class PcCompatLegacyInputBridge
{
    private const string BindingDiagnosticPrefix = "[DEBUG-kv-binding-v2]";
    private const int BindingDiagnosticQueryLimit = 8;
    private const string NativeLibrary = "starray_modmanager";
    private const int KeyCapacity = 512;
    private const int HeldWordCount = KeyCapacity / 64;
    private const int NativeSnapshotSize = 8288;
    private static int s_nativeUnavailable;
    private static int s_modalInputCaptureActive;
    private static int s_nativeObservedModalInputActive;
    private static long s_modalInputEpoch;
    private static long s_modalQueryCount;
    private static long s_modalNativeTrueCount;
    private static int s_lastModalQueryKind;
    private static int s_lastModalQueryKey = -1;
    private static int s_lastModalQueryThread;
    private static long s_settingsButtonActivationCount;
    private static long s_settingsSuppressedQueryCount;
    private static int s_lastSettingsQueryKind;
    private static int s_lastSettingsQueryKey = -1;
    private static int s_lastSettingsQueryThread;
    private static int s_settingsFrameReadyLogged;
    private static int s_outsideSettingsActivationLogs;
    private static long s_settingsFrameSequence;
    private static long s_settingsActivationSequence;
    private static readonly ConcurrentDictionary<string, ConcurrentBag<QueryAudit>> QueryAudits =
        new(StringComparer.OrdinalIgnoreCase);

    [ThreadStatic]
    private static ThreadState? t_state;
    [ThreadStatic]
    private static int t_settingsGuiFrameDepth;
    [ThreadStatic]
    private static bool t_settingsButtonActivated;
    [ThreadStatic]
    private static long t_settingsFrameSequence;
    [ThreadStatic]
    private static long t_settingsActivationSequence;
    [ThreadStatic]
    private static int t_settingsSuppressedThisFrame;
    [ThreadStatic]
    private static int t_settingsLoggedThisFrame;
    [ThreadStatic]
    private static long t_settingsAcceptedTraceDeadline;
    [ThreadStatic]
    private static int t_settingsAcceptedLogsRemaining;

    public static void BeginSettingsGuiFrame()
    {
        if (t_settingsGuiFrameDepth++ == 0)
        {
            t_settingsButtonActivated = false;
            t_settingsFrameSequence = Interlocked.Increment(ref s_settingsFrameSequence);
            t_settingsSuppressedThisFrame = 0;
            t_settingsLoggedThisFrame = 0;
            if (Interlocked.Exchange(ref s_settingsFrameReadyLogged, 1) == 0)
            {
                Console.WriteLine(
                    $"{BindingDiagnosticPrefix} bridge-ready revision=settings-input-transaction-v2 " +
                    $"tid={Environment.CurrentManagedThreadId}");
            }
        }
    }

    public static void NotifySettingsButtonActivated()
        => NotifySettingsButtonActivated("unknown", string.Empty);

    public static void NotifySettingsButtonActivated(string overload, string? label)
    {
        if (t_settingsGuiFrameDepth <= 0)
        {
            var outsideCount = Interlocked.Increment(ref s_outsideSettingsActivationLogs);
            if (outsideCount <= 4)
            {
                Console.WriteLine(
                    $"{BindingDiagnosticPrefix} activation-outside-frame " +
                    $"overload={overload} label={FormatDiagnosticLabel(label)} " +
                    $"tid={Environment.CurrentManagedThreadId}");
            }
            return;
        }

        t_settingsButtonActivated = true;
        t_settingsActivationSequence = Interlocked.Increment(ref s_settingsActivationSequence);
        t_settingsSuppressedThisFrame = 0;
        t_settingsLoggedThisFrame = 0;
        t_settingsAcceptedTraceDeadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        t_settingsAcceptedLogsRemaining = 12;
        Interlocked.Increment(ref s_settingsButtonActivationCount);
        Console.WriteLine(
            $"{BindingDiagnosticPrefix} activation={t_settingsActivationSequence} " +
            $"frame={t_settingsFrameSequence} overload={overload} " +
            $"label={FormatDiagnosticLabel(label)} tid={Environment.CurrentManagedThreadId} " +
            $"modalManaged={Volatile.Read(ref s_modalInputCaptureActive)} " +
            $"modalNative={ReadNativeModalInputActiveForDiagnostics()}");
    }

    internal static void SuppressSettingsInputAfterDeferredButton()
    {
        if (t_settingsGuiFrameDepth > 0)
            t_settingsButtonActivated = true;
    }

    public static void EndSettingsGuiFrame()
    {
        if (t_settingsGuiFrameDepth <= 0)
            return;
        if (--t_settingsGuiFrameDepth == 0)
        {
            if (t_settingsButtonActivated)
            {
                Console.WriteLine(
                    $"{BindingDiagnosticPrefix} activation={t_settingsActivationSequence} " +
                    $"frame={t_settingsFrameSequence} end " +
                    $"suppressed={t_settingsSuppressedThisFrame} " +
                    $"lastKind={(LegacyQueryKind)Volatile.Read(ref s_lastSettingsQueryKind)} " +
                    $"lastKey={Volatile.Read(ref s_lastSettingsQueryKey)} " +
                    $"tid={Environment.CurrentManagedThreadId}");
            }
            t_settingsButtonActivated = false;
        }
    }

    public static void SetModalInputCapture(bool active)
    {
        var next = active ? 1 : 0;
        if (Interlocked.Exchange(ref s_modalInputCaptureActive, next) != next)
            Interlocked.Increment(ref s_modalInputEpoch);
    }

    public static bool GetKey<TKey>(TKey keyCode)
        where TKey : unmanaged, Enum
        => IsUnityKeyHeld(EnumToInt32(keyCode), 0, null);

    public static bool GetKeyOwned<TKey>(TKey keyCode, int callsiteToken, string modId)
        where TKey : unmanaged, Enum
    {
        var key = EnumToInt32(keyCode);
        return TraceOwnedUnityQuery(
            "GetKey",
            key,
            callsiteToken,
            modId,
            IsUnityKeyHeld(key, callsiteToken, modId));
    }

    public static bool GetKeyDown<TKey>(TKey keyCode, int callsiteToken)
        where TKey : unmanaged, Enum
        => ReadEdge(EnumToInt32(keyCode), callsiteToken, down: true, null);

    public static bool GetKeyDownOwned<TKey>(TKey keyCode, int callsiteToken, string modId)
        where TKey : unmanaged, Enum
    {
        var key = EnumToInt32(keyCode);
        return TraceOwnedUnityQuery(
            "GetKeyDown",
            key,
            callsiteToken,
            modId,
            ReadEdge(key, callsiteToken, down: true, modId));
    }

    public static bool GetKeyUp<TKey>(TKey keyCode, int callsiteToken)
        where TKey : unmanaged, Enum
        => ReadEdge(EnumToInt32(keyCode), callsiteToken, down: false, null);

    public static bool GetKeyUpOwned<TKey>(TKey keyCode, int callsiteToken, string modId)
        where TKey : unmanaged, Enum
    {
        var key = EnumToInt32(keyCode);
        return TraceOwnedUnityQuery(
            "GetKeyUp",
            key,
            callsiteToken,
            modId,
            ReadEdge(key, callsiteToken, down: false, modId));
    }

    public static bool GetAnyKeyDown(int callsiteToken)
        => GetAnyKeyDownCore(callsiteToken, null);

    public static bool GetAnyKeyDownOwned(int callsiteToken, string modId)
        => TraceOwnedAnyQuery(
            callsiteToken,
            modId,
            GetAnyKeyDownCore(callsiteToken, modId));

    private static bool GetAnyKeyDownCore(int callsiteToken, string? explicitModId)
    {
        var state = GetThreadState();
        var nativeModalCapture = state.IsModalInputCaptureActive();
        var buttonActivation = IsSettingsButtonActivationSuppressed();
        var quarantine = nativeModalCapture || buttonActivation;
        var inputEpoch = Volatile.Read(ref s_modalInputEpoch);
        if (TryResolveModId(explicitModId, out var modId) &&
            PcCompatKeyViewerConsumerRuntime.TryGetAnyUnityKeyDownState(
                modId,
                out var mode,
                out var consumerOrdinal,
                out var registrationGeneration))
        {
            var consumer = state.ReadConsumerEdge(
                modId,
                registrationGeneration,
                ComposeCursorKey(callsiteToken, KeyCapacity, down: true),
                consumerOrdinal,
                inputEpoch,
                quarantine);
            if (quarantine)
            {
                if (buttonActivation)
                {
                    RecordSettingsSuppressedQuery(
                        LegacyQueryKind.AnyKeyEdge,
                        -1,
                        callsiteToken,
                        explicitModId);
                }
                state.Refresh();
                var observedNative = state.Available && state.ReadEdge(
                    ComposeCursorKey(callsiteToken, KeyCapacity, down: true),
                    state.Snapshot.KeyboardLifetimeDownCount);
                var native = !buttonActivation && observedNative;
                if (!native)
                    state.UpdateModalBindingBaseline(inputEpoch);
                state.RecordQuery(
                    modId,
                    LegacyQueryKind.AnyKeyEdge,
                    matched: true,
                    native,
                    state.Snapshot.KeyboardLifetimeDownCount,
                    registrationGeneration);
                if (nativeModalCapture)
                    RecordModalQuery(LegacyQueryKind.AnyKeyEdge, -1, native);
                if (native)
                {
                    TraceSettingsAcceptedQuery(
                        LegacyQueryKind.AnyKeyEdge,
                        -1,
                        callsiteToken,
                        explicitModId,
                        "native-modal");
                }
                return native;
            }
            state.RecordQuery(
                modId,
                LegacyQueryKind.AnyKeyEdge,
                matched: true,
                consumer,
                consumerOrdinal,
                registrationGeneration);
            if (mode == PcCompatKeyViewerInputMode.Touch)
            {
                if (consumer)
                {
                    TraceSettingsAcceptedQuery(
                        LegacyQueryKind.AnyKeyEdge,
                        -1,
                        callsiteToken,
                        explicitModId,
                        "consumer-touch");
                }
                return consumer;
            }
            state.Refresh();
            var nativeHybrid = state.Available && state.ReadEdge(
                ComposeCursorKey(callsiteToken, KeyCapacity, down: true),
                state.Snapshot.KeyboardLifetimeDownCount);
            var hybrid = nativeHybrid || consumer;
            if (hybrid)
            {
                TraceSettingsAcceptedQuery(
                    LegacyQueryKind.AnyKeyEdge,
                    -1,
                    callsiteToken,
                    explicitModId,
                    nativeHybrid ? "native-hybrid" : "consumer-hybrid");
            }
            return hybrid;
        }
        if (!string.IsNullOrWhiteSpace(explicitModId))
        {
            state.RecordQuery(
                explicitModId,
                LegacyQueryKind.AnyKeyEdge,
                matched: false,
                observedTrue: false,
                sourceSequence: 0,
                registrationGeneration: 0);
        }
        state.Refresh();
        var observedNativeFallback = state.Available && state.ReadEdge(
            ComposeCursorKey(callsiteToken, KeyCapacity, down: true),
            state.Snapshot.KeyboardLifetimeDownCount);
        var nativeFallback = !buttonActivation && observedNativeFallback;
        if (quarantine && !nativeFallback)
            state.UpdateModalBindingBaseline(inputEpoch);
        if (buttonActivation)
        {
            RecordSettingsSuppressedQuery(
                LegacyQueryKind.AnyKeyEdge,
                -1,
                callsiteToken,
                explicitModId);
        }
        if (nativeFallback)
        {
            TraceSettingsAcceptedQuery(
                LegacyQueryKind.AnyKeyEdge,
                -1,
                callsiteToken,
                explicitModId,
                "native-fallback");
        }
        return nativeFallback;
    }

    public static short GetAsyncKeyState(int virtualKey)
        => GetAsyncKeyStateCore(virtualKey, 0, null);

    public static short GetAsyncKeyStateOwned(
        int virtualKey,
        int callsiteToken,
        string modId)
        => TraceOwnedWindowsQuery(
            virtualKey,
            callsiteToken,
            modId,
            GetAsyncKeyStateCore(virtualKey, callsiteToken, modId));

    private static short GetAsyncKeyStateCore(
        int virtualKey,
        int callsiteToken,
        string? explicitModId)
    {
        var threadState = GetThreadState();
        var nativeModalCapture = threadState.IsModalInputCaptureActive();
        var buttonActivation = IsSettingsButtonActivationSuppressed();
        var quarantine = nativeModalCapture || buttonActivation;
        var inputEpoch = Volatile.Read(ref s_modalInputEpoch);
        if (TryResolveModId(explicitModId, out var modId) &&
            PcCompatKeyViewerConsumerRuntime.TryGetWindowsVirtualKeyState(
                modId, virtualKey, out var consumer))
        {
            var consumerHeld = threadState.ReadConsumerHeld(
                modId,
                consumer.RegistrationGeneration,
                PcCompatInputIdentityKind.WindowsVirtualKey,
                virtualKey,
                callsiteToken,
                consumer,
                inputEpoch,
                quarantine);
            if (quarantine)
            {
                if (buttonActivation)
                {
                    RecordSettingsSuppressedQuery(
                        LegacyQueryKind.WindowsHeld,
                        virtualKey,
                        callsiteToken,
                        explicitModId);
                }
                var native = !buttonActivation &&
                             IsNativeUnityKeyHeld(WindowsVirtualKeyToUnityKey(virtualKey));
                threadState.RecordQuery(
                    modId,
                    LegacyQueryKind.WindowsHeld,
                    matched: true,
                    native,
                    consumer.SourceSequence,
                    consumer.RegistrationGeneration);
                if (nativeModalCapture)
                    RecordModalQuery(LegacyQueryKind.WindowsHeld, virtualKey, native);
                if (native && virtualKey == 8)
                {
                    TraceSettingsAcceptedQuery(
                        LegacyQueryKind.WindowsHeld,
                        virtualKey,
                        callsiteToken,
                        explicitModId,
                        "native-modal");
                }
                return native ? unchecked((short)0x8000) : (short)0;
            }
            threadState.RecordQuery(
                modId,
                LegacyQueryKind.WindowsHeld,
                matched: true,
                consumerHeld,
                consumer.SourceSequence,
                consumer.RegistrationGeneration);
            if (consumer.Mode == PcCompatKeyViewerInputMode.Touch)
            {
                if (consumerHeld && virtualKey == 8)
                {
                    TraceSettingsAcceptedQuery(
                        LegacyQueryKind.WindowsHeld,
                        virtualKey,
                        callsiteToken,
                        explicitModId,
                        "consumer-touch");
                }
                return consumerHeld ? unchecked((short)0x8000) : (short)0;
            }
            var hybrid = IsNativeUnityKeyHeld(WindowsVirtualKeyToUnityKey(virtualKey)) ||
                         consumerHeld;
            if (hybrid && virtualKey == 8)
            {
                TraceSettingsAcceptedQuery(
                    LegacyQueryKind.WindowsHeld,
                    virtualKey,
                    callsiteToken,
                    explicitModId,
                    "hybrid");
            }
            return hybrid ? unchecked((short)0x8000) : (short)0;
        }
        if (!string.IsNullOrWhiteSpace(explicitModId))
        {
            threadState.RecordQuery(
                explicitModId,
                LegacyQueryKind.WindowsHeld,
                matched: false,
                observedTrue: false,
                sourceSequence: 0,
                registrationGeneration: 0);
        }
        var held = !buttonActivation &&
                   IsNativeUnityKeyHeld(WindowsVirtualKeyToUnityKey(virtualKey));
        if (buttonActivation)
        {
            RecordSettingsSuppressedQuery(
                LegacyQueryKind.WindowsHeld,
                virtualKey,
                callsiteToken,
                explicitModId);
        }
        if (held && virtualKey == 8)
        {
            TraceSettingsAcceptedQuery(
                LegacyQueryKind.WindowsHeld,
                virtualKey,
                callsiteToken,
                explicitModId,
                "native-fallback");
        }
        return held ? unchecked((short)0x8000) : (short)0;
    }

    private static bool IsUnityKeyHeld(
        int index,
        int callsiteToken,
        string? explicitModId)
    {
        var threadState = GetThreadState();
        var nativeModalCapture = threadState.IsModalInputCaptureActive();
        var buttonActivation = IsSettingsButtonActivationSuppressed();
        var quarantine = nativeModalCapture || buttonActivation;
        var inputEpoch = Volatile.Read(ref s_modalInputEpoch);
        if (TryResolveModId(explicitModId, out var modId) &&
            PcCompatKeyViewerConsumerRuntime.TryGetUnityKeyState(
                modId, index, out var consumer))
        {
            var consumerHeld = threadState.ReadConsumerHeld(
                modId,
                consumer.RegistrationGeneration,
                PcCompatInputIdentityKind.UnityKeyCode,
                index,
                callsiteToken,
                consumer,
                inputEpoch,
                quarantine);
            if (quarantine)
            {
                if (buttonActivation)
                {
                    RecordSettingsSuppressedQuery(
                        LegacyQueryKind.UnityHeld,
                        index,
                        callsiteToken,
                        explicitModId);
                }
                var native = !buttonActivation && IsNativeUnityKeyHeld(index);
                threadState.RecordQuery(
                    modId,
                    LegacyQueryKind.UnityHeld,
                    matched: true,
                    native,
                    consumer.SourceSequence,
                    consumer.RegistrationGeneration);
                if (nativeModalCapture)
                    RecordModalQuery(LegacyQueryKind.UnityHeld, index, native);
                if (native && index == 8)
                {
                    TraceSettingsAcceptedQuery(
                        LegacyQueryKind.UnityHeld,
                        index,
                        callsiteToken,
                        explicitModId,
                        "native-modal");
                }
                return native;
            }
            threadState.RecordQuery(
                modId,
                LegacyQueryKind.UnityHeld,
                matched: true,
                consumerHeld,
                consumer.SourceSequence,
                consumer.RegistrationGeneration);
            var held = consumer.Mode == PcCompatKeyViewerInputMode.Touch
                ? consumerHeld
                : IsNativeUnityKeyHeld(index) || consumerHeld;
            if (held && index == 8)
            {
                TraceSettingsAcceptedQuery(
                    LegacyQueryKind.UnityHeld,
                    index,
                    callsiteToken,
                    explicitModId,
                    consumer.Mode == PcCompatKeyViewerInputMode.Touch
                        ? "consumer-touch"
                        : "hybrid");
            }
            return held;
        }
        if (!string.IsNullOrWhiteSpace(explicitModId))
        {
            threadState.RecordQuery(
                explicitModId,
                LegacyQueryKind.UnityHeld,
                matched: false,
                observedTrue: false,
                sourceSequence: 0,
                registrationGeneration: 0);
        }
        var nativeFallback = !buttonActivation && IsNativeUnityKeyHeld(index);
        if (buttonActivation)
        {
            RecordSettingsSuppressedQuery(
                LegacyQueryKind.UnityHeld,
                index,
                callsiteToken,
                explicitModId);
        }
        if (nativeFallback && index == 8)
        {
            TraceSettingsAcceptedQuery(
                LegacyQueryKind.UnityHeld,
                index,
                callsiteToken,
                explicitModId,
                "native-fallback");
        }
        return nativeFallback;
    }

    private static bool IsNativeUnityKeyHeld(int index)
    {
        var state = GetState();
        if (!state.Available || (uint)index >= KeyCapacity)
            return false;
        fixed (ulong* held = state.Snapshot.HeldWords)
            return (held[index / 64] & (1UL << (index % 64))) != 0;
    }

    private static int WindowsVirtualKeyToUnityKey(int virtualKey)
    {
        if (virtualKey is >= 0x41 and <= 0x5A)
            return 97 + virtualKey - 0x41;
        if (virtualKey is >= 0x30 and <= 0x39)
            return 48 + virtualKey - 0x30;
        if (virtualKey is >= 0x70 and <= 0x7E)
            return 282 + virtualKey - 0x70;
        if (virtualKey is >= 0x60 and <= 0x69)
            return 256 + virtualKey - 0x60;
        return virtualKey switch
        {
            0x08 => 8,
            0x09 => 9,
            0x0D => 13,
            0x13 => 19,
            0x1B => 27,
            0x20 => 32,
            0x21 => 280,
            0x22 => 281,
            0x23 => 279,
            0x24 => 278,
            0x25 => 276,
            0x26 => 273,
            0x27 => 275,
            0x28 => 274,
            0x2D => 277,
            0x2E => 127,
            0x6A => 268,
            0x6B => 270,
            0x6D => 269,
            0x6E => 266,
            0x6F => 267,
            0x90 => 300,
            0x91 => 302,
            0xA0 => 304,
            0xA1 => 303,
            0xA2 => 306,
            0xA3 => 305,
            0xA4 => 308,
            0xA5 => 307,
            0xBA => 59,
            0xBB => 61,
            0xBC => 44,
            0xBD => 45,
            0xBE => 46,
            0xBF => 47,
            0xC0 => 96,
            0xDB => 91,
            0xDC => 92,
            0xDD => 93,
            0xDE => 39,
            _ => -1
        };
    }

    private static bool ReadEdge(
        int index,
        int callsiteToken,
        bool down,
        string? explicitModId)
    {
        var state = GetThreadState();
        var nativeModalCapture = state.IsModalInputCaptureActive();
        var buttonActivation = IsSettingsButtonActivationSuppressed();
        var quarantine = nativeModalCapture || buttonActivation;
        var inputEpoch = Volatile.Read(ref s_modalInputEpoch);
        if (TryResolveModId(explicitModId, out var modId) &&
            PcCompatKeyViewerConsumerRuntime.TryGetUnityKeyState(
                modId, index, out var consumer))
        {
            var currentConsumer = down ? consumer.DownOrdinal : consumer.UpOrdinal;
            var consumerEdge = state.ReadConsumerEdge(
                modId,
                consumer.RegistrationGeneration,
                ComposeCursorKey(callsiteToken, index, down),
                currentConsumer,
                inputEpoch,
                quarantine);
            if (quarantine)
            {
                if (buttonActivation)
                {
                    RecordSettingsSuppressedQuery(
                        LegacyQueryKind.UnityEdge,
                        index,
                        callsiteToken,
                        explicitModId);
                }
                state.Refresh();
                if (buttonActivation)
                    state.UpdateModalBindingBaseline(inputEpoch);
                var observedNative = ReadNativeEdge(
                    state, index, callsiteToken, down, inputEpoch, useBindingBaseline: true);
                var native = !buttonActivation && observedNative;
                state.RecordQuery(
                    modId,
                    LegacyQueryKind.UnityEdge,
                    matched: true,
                    native,
                    consumer.SourceSequence,
                    consumer.RegistrationGeneration);
                if (nativeModalCapture)
                    RecordModalQuery(LegacyQueryKind.UnityEdge, index, native);
                if (native)
                {
                    TraceSettingsAcceptedQuery(
                        LegacyQueryKind.UnityEdge,
                        index,
                        callsiteToken,
                        explicitModId,
                        "native-modal");
                }
                return native;
            }
            state.RecordQuery(
                modId,
                LegacyQueryKind.UnityEdge,
                matched: true,
                consumerEdge,
                consumer.SourceSequence,
                consumer.RegistrationGeneration);
            if (consumer.Mode == PcCompatKeyViewerInputMode.Touch)
            {
                if (consumerEdge)
                {
                    TraceSettingsAcceptedQuery(
                        LegacyQueryKind.UnityEdge,
                        index,
                        callsiteToken,
                        explicitModId,
                        "consumer-touch");
                }
                return consumerEdge;
            }
            state.Refresh();
            var nativeHybrid = ReadNativeEdge(
                state,
                index,
                callsiteToken,
                down,
                inputEpoch,
                useBindingBaseline: t_settingsGuiFrameDepth > 0);
            var hybrid = nativeHybrid || consumerEdge;
            if (hybrid)
            {
                TraceSettingsAcceptedQuery(
                    LegacyQueryKind.UnityEdge,
                    index,
                    callsiteToken,
                    explicitModId,
                    nativeHybrid ? "native-hybrid" : "consumer-hybrid");
            }
            return hybrid;
        }
        if (!string.IsNullOrWhiteSpace(explicitModId))
        {
            state.RecordQuery(
                explicitModId,
                LegacyQueryKind.UnityEdge,
                matched: false,
                observedTrue: false,
                sourceSequence: 0,
                registrationGeneration: 0);
        }
        state.Refresh();
        if (buttonActivation)
            state.UpdateModalBindingBaseline(inputEpoch);
        var observedNativeFallback = ReadNativeEdge(
            state,
            index,
            callsiteToken,
            down,
            inputEpoch,
            useBindingBaseline: quarantine || t_settingsGuiFrameDepth > 0);
        var nativeFallback = !buttonActivation && observedNativeFallback;
        if (buttonActivation)
        {
            RecordSettingsSuppressedQuery(
                LegacyQueryKind.UnityEdge,
                index,
                callsiteToken,
                explicitModId);
        }
        if (nativeModalCapture)
            RecordModalQuery(LegacyQueryKind.UnityEdge, index, nativeFallback);
        if (nativeFallback)
        {
            TraceSettingsAcceptedQuery(
                LegacyQueryKind.UnityEdge,
                index,
                callsiteToken,
                explicitModId,
                "native-fallback");
        }
        return nativeFallback;
    }

    private static bool ReadNativeEdge(
        ThreadState state,
        int index,
        int callsiteToken,
        bool down,
        long inputEpoch,
        bool useBindingBaseline)
    {
        if (!state.Available || (uint)index >= KeyCapacity)
            return false;
        ulong current;
        if (down)
        {
            fixed (ulong* ordinals = state.Snapshot.DownOrdinals)
                current = ordinals[index];
        }
        else
        {
            fixed (ulong* ordinals = state.Snapshot.UpOrdinals)
                current = ordinals[index];
        }
        return state.ReadNativeKeyEdge(
            ComposeCursorKey(callsiteToken, index, down),
            index,
            current,
            inputEpoch,
            useModalBindingBaseline: down && useBindingBaseline);
    }

    private static bool TryGetCurrentModId(out string modId)
    {
        modId = PcCompatManagedExecutionContext.Current?.ModId ?? string.Empty;
        return modId.Length != 0;
    }

    private static bool TryResolveModId(string? explicitModId, out string modId)
    {
        if (!string.IsNullOrWhiteSpace(explicitModId))
        {
            modId = explicitModId;
            return true;
        }
        return TryGetCurrentModId(out modId);
    }

    private static bool TraceOwnedUnityQuery(
        string query,
        int key,
        int callsiteToken,
        string modId,
        bool result)
    {
        var matched = PcCompatKeyViewerConsumerRuntime.TryGetUnityKeyState(
            modId,
            key,
            out var consumer);
        var threadState = GetThreadState();
        var modal = threadState.IsModalInputCaptureActive();
        var settingsSuppressed = IsSettingsButtonActivationSuppressed();
        PcCompatDeepDebug.WriteSampled(
            "input-query",
            modId + "\0" + query + "\0" + callsiteToken,
            count =>
                $"count={count} query={query} owner={modId} callsite=0x{callsiteToken:X8} " +
                $"keyKind=UnityKeyCode key={key} matched={matched} result={result} " +
                $"mode={(matched ? consumer.Mode : PcCompatKeyViewerInputMode.External)} " +
                $"consumerHeld={(matched && consumer.Held)} " +
                $"down={(matched ? consumer.DownOrdinal : 0)} up={(matched ? consumer.UpOrdinal : 0)} " +
                $"sourceSequence={(matched ? consumer.SourceSequence : 0)} " +
                $"sessionGeneration={(matched ? consumer.SessionGeneration : 0)} " +
                $"producerEpoch={(matched ? consumer.ProducerEpoch : 0)} " +
                $"registrationGeneration={(matched ? consumer.RegistrationGeneration : 0)} " +
                $"nativeAvailable={threadState.Available} nativeHeld={IsNativeUnityKeyHeld(key)} " +
                $"modal={modal} settingsSuppressed={settingsSuppressed} " +
                $"surface=[{PcCompatKeyViewerConsumerRuntime.GetQuerySurfaceStatus(modId)}] " +
                PcCompatDeepDebug.ExecutionIdentity(),
            first: 2,
            periodic: 8192);
        return result;
    }

    private static bool TraceOwnedAnyQuery(int callsiteToken, string modId, bool result)
    {
        var matched = PcCompatKeyViewerConsumerRuntime.TryGetAnyUnityKeyDownState(
            modId,
            out var mode,
            out var downOrdinal,
            out var registrationGeneration);
        var threadState = GetThreadState();
        var modal = threadState.IsModalInputCaptureActive();
        var settingsSuppressed = IsSettingsButtonActivationSuppressed();
        PcCompatDeepDebug.WriteSampled(
            "input-query",
            modId + "\0GetAnyKeyDown\0" + callsiteToken,
            count =>
                $"count={count} query=GetAnyKeyDown owner={modId} callsite=0x{callsiteToken:X8} " +
                $"keyKind=AnyUnityKey key=-1 matched={matched} result={result} mode={mode} " +
                $"down={downOrdinal} registrationGeneration={registrationGeneration} " +
                $"nativeAvailable={threadState.Available} modal={modal} " +
                $"settingsSuppressed={settingsSuppressed} " +
                $"surface=[{PcCompatKeyViewerConsumerRuntime.GetQuerySurfaceStatus(modId)}] " +
                PcCompatDeepDebug.ExecutionIdentity(),
            first: 2,
            periodic: 8192);
        return result;
    }

    private static short TraceOwnedWindowsQuery(
        int virtualKey,
        int callsiteToken,
        string modId,
        short result)
    {
        var matched = PcCompatKeyViewerConsumerRuntime.TryGetWindowsVirtualKeyState(
            modId,
            virtualKey,
            out var consumer);
        var threadState = GetThreadState();
        var modal = threadState.IsModalInputCaptureActive();
        var settingsSuppressed = IsSettingsButtonActivationSuppressed();
        var unityKey = WindowsVirtualKeyToUnityKey(virtualKey);
        PcCompatDeepDebug.WriteSampled(
            "input-query",
            modId + "\0GetAsyncKeyState\0" + callsiteToken,
            count =>
                $"count={count} query=GetAsyncKeyState owner={modId} callsite=0x{callsiteToken:X8} " +
                $"keyKind=WindowsVirtualKey key={virtualKey} mappedUnityKey={unityKey} " +
                $"matched={matched} result=0x{unchecked((ushort)result):X4} " +
                $"mode={(matched ? consumer.Mode : PcCompatKeyViewerInputMode.External)} " +
                $"consumerHeld={(matched && consumer.Held)} " +
                $"down={(matched ? consumer.DownOrdinal : 0)} up={(matched ? consumer.UpOrdinal : 0)} " +
                $"sourceSequence={(matched ? consumer.SourceSequence : 0)} " +
                $"sessionGeneration={(matched ? consumer.SessionGeneration : 0)} " +
                $"producerEpoch={(matched ? consumer.ProducerEpoch : 0)} " +
                $"registrationGeneration={(matched ? consumer.RegistrationGeneration : 0)} " +
                $"nativeAvailable={threadState.Available} nativeHeld={IsNativeUnityKeyHeld(unityKey)} " +
                $"modal={modal} settingsSuppressed={settingsSuppressed} " +
                $"surface=[{PcCompatKeyViewerConsumerRuntime.GetQuerySurfaceStatus(modId)}] " +
                PcCompatDeepDebug.ExecutionIdentity(),
            first: 2,
            periodic: 8192);
        return result;
    }

    private static long ComposeCursorKey(int callsiteToken, int keyCode, bool down)
        => ((long)(uint)callsiteToken << 32) |
           ((long)(down ? 1u : 0u) << 31) |
           (uint)keyCode;

    private static int EnumToInt32<TKey>(TKey value)
        where TKey : unmanaged, Enum
    {
        if (Unsafe.SizeOf<TKey>() == sizeof(int))
            return Unsafe.As<TKey, int>(ref value);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static ThreadState GetState()
    {
        var state = GetThreadState();
        state.Refresh();
        return state;
    }

    private static ThreadState GetThreadState()
        => t_state ??= new ThreadState();

    private static bool IsSettingsButtonActivationSuppressed()
        => t_settingsGuiFrameDepth > 0 && t_settingsButtonActivated;

    private static void RecordModalQuery(LegacyQueryKind kind, int key, bool nativeResult)
    {
        Interlocked.Increment(ref s_modalQueryCount);
        if (nativeResult)
            Interlocked.Increment(ref s_modalNativeTrueCount);
        Volatile.Write(ref s_lastModalQueryKind, (int)kind);
        Volatile.Write(ref s_lastModalQueryKey, key);
        Volatile.Write(ref s_lastModalQueryThread, Environment.CurrentManagedThreadId);
    }

    private static void RecordSettingsSuppressedQuery(
        LegacyQueryKind kind,
        int key,
        int callsiteToken,
        string? modId)
    {
        Interlocked.Increment(ref s_settingsSuppressedQueryCount);
        ++t_settingsSuppressedThisFrame;
        Volatile.Write(ref s_lastSettingsQueryKind, (int)kind);
        Volatile.Write(ref s_lastSettingsQueryKey, key);
        Volatile.Write(ref s_lastSettingsQueryThread, Environment.CurrentManagedThreadId);
        if (t_settingsLoggedThisFrame >= BindingDiagnosticQueryLimit && key != 8)
            return;
        ++t_settingsLoggedThisFrame;
        Console.WriteLine(
            $"{BindingDiagnosticPrefix} activation={t_settingsActivationSequence} " +
            $"frame={t_settingsFrameSequence} suppress kind={kind} key={key} " +
            $"callsite={callsiteToken} mod={FormatDiagnosticLabel(modId)} " +
            $"tid={Environment.CurrentManagedThreadId}");
    }

    private static void TraceSettingsAcceptedQuery(
        LegacyQueryKind kind,
        int key,
        int callsiteToken,
        string? modId,
        string source)
    {
        if (t_settingsActivationSequence == 0 ||
            t_settingsAcceptedLogsRemaining <= 0 ||
            Stopwatch.GetTimestamp() > t_settingsAcceptedTraceDeadline)
        {
            return;
        }

        --t_settingsAcceptedLogsRemaining;
        Console.WriteLine(
            $"{BindingDiagnosticPrefix} activation={t_settingsActivationSequence} " +
            $"frame={t_settingsFrameSequence} accepted kind={kind} key={key} " +
            $"callsite={callsiteToken} source={source} mod={FormatDiagnosticLabel(modId)} " +
            $"tid={Environment.CurrentManagedThreadId} " +
            $"modalManaged={Volatile.Read(ref s_modalInputCaptureActive)} " +
            $"modalNative={ReadNativeModalInputActiveForDiagnostics()}");
    }

    private static string FormatDiagnosticLabel(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "<empty>";
        var normalized = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');
        return normalized.Length <= 96
            ? normalized
            : normalized[..96] + "...";
    }

    public static string GetDiagnosticStatus(string modId)
    {
        var modalPrefix =
            $"modalManaged={Volatile.Read(ref s_modalInputCaptureActive)}" +
            $" modalNative={ReadNativeModalInputActiveForDiagnostics()}" +
            $" modalEpoch={Volatile.Read(ref s_modalInputEpoch)}" +
            $" modalQueries={Interlocked.Read(ref s_modalQueryCount)}" +
            $" modalNativeTrue={Interlocked.Read(ref s_modalNativeTrueCount)}" +
            $" modalLastKind={(LegacyQueryKind)Volatile.Read(ref s_lastModalQueryKind)}" +
            $" modalLastKey={Volatile.Read(ref s_lastModalQueryKey)}" +
            $" modalLastThread={Volatile.Read(ref s_lastModalQueryThread)}" +
            $" settingsButtons={Interlocked.Read(ref s_settingsButtonActivationCount)}" +
            $" settingsSuppressed={Interlocked.Read(ref s_settingsSuppressedQueryCount)}" +
            $" settingsLastKind={(LegacyQueryKind)Volatile.Read(ref s_lastSettingsQueryKind)}" +
            $" settingsLastKey={Volatile.Read(ref s_lastSettingsQueryKey)}" +
            $" settingsLastThread={Volatile.Read(ref s_lastSettingsQueryThread)}";
        var diagnostic = GetDiagnosticSnapshot(modId);
        if (diagnostic.ThreadCount == 0)
            return modalPrefix +
                   " threads=0 unityHeld=0 windowsHeld=0 unityEdge=0 anyEdge=0 matched=0 true=0 lastSequence=0 registration=0";
        return modalPrefix +
               $" threads={diagnostic.ThreadCount}" +
               $" threadIds={string.Join(',', diagnostic.ThreadIds)}" +
               $" unityHeld={diagnostic.UnityHeldCount}" +
               $" windowsHeld={diagnostic.WindowsHeldCount}" +
               $" unityEdge={diagnostic.UnityEdgeCount}" +
               $" anyEdge={diagnostic.AnyKeyEdgeCount}" +
               $" matched={diagnostic.MatchedCount}" +
               $" true={diagnostic.TrueCount}" +
               $" lastSequence={diagnostic.LastSourceSequence}" +
               $" registration={diagnostic.LastRegistrationGeneration}";
    }

    public static PcCompatLegacyInputDiagnosticSnapshot GetDiagnosticSnapshot(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId) || !QueryAudits.TryGetValue(modId, out var audits))
            return new PcCompatLegacyInputDiagnosticSnapshot(
                0,
                Array.Empty<int>(),
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0);
        var snapshot = audits.ToArray();
        return new PcCompatLegacyInputDiagnosticSnapshot(
            snapshot.Length,
            snapshot.Select(audit => audit.ThreadId).Distinct().Order().ToArray(),
            snapshot.Sum(audit => Interlocked.Read(ref audit.UnityHeldCount)),
            snapshot.Sum(audit => Interlocked.Read(ref audit.WindowsHeldCount)),
            snapshot.Sum(audit => Interlocked.Read(ref audit.UnityEdgeCount)),
            snapshot.Sum(audit => Interlocked.Read(ref audit.AnyKeyEdgeCount)),
            snapshot.Sum(audit => Interlocked.Read(ref audit.MatchedCount)),
            snapshot.Sum(audit => Interlocked.Read(ref audit.TrueCount)),
            snapshot.Max(audit => Interlocked.Read(ref audit.LastSourceSequence)),
            snapshot.Max(audit => Interlocked.Read(ref audit.LastRegistrationGeneration)));
    }

    public static void ClearDiagnostics(string modId)
    {
        if (!string.IsNullOrWhiteSpace(modId))
            QueryAudits.TryRemove(modId, out _);
    }

    private static int ReadNativeModalInputActiveForDiagnostics()
    {
        try
        {
            return ReadNativeModalInputActive() != 0 ? 1 : 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or
                                           EntryPointNotFoundException or
                                           BadImageFormatException)
        {
            return -1;
        }
    }

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_read_legacy_input_snapshot")]
    private static extern int ReadLegacyInputSnapshot(
        ref NativeLegacyInputSnapshot snapshot,
        uint snapshotSize);

    [DllImport(NativeLibrary, EntryPoint = "modmanager_modal_input_is_active")]
    private static extern int ReadNativeModalInputActive();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeLegacyInputSnapshot
    {
        public uint AbiVersion;
        public uint StructSize;
        public ulong Generation;
        public long LatestRawNs;
        public ulong KeyboardLifetimeDownCount;
        public fixed ulong HeldWords[HeldWordCount];
        public fixed ulong DownOrdinals[KeyCapacity];
        public fixed ulong UpOrdinals[KeyCapacity];
    }

    private sealed class ThreadState
    {
        public NativeLegacyInputSnapshot Snapshot;
        public bool Available;
        private readonly Dictionary<long, ulong> _edgeCursors = new();
        private readonly Dictionary<ConsumerCursorKey, ConsumerEdgeCursor> _consumerEdgeCursors = new();
        private readonly Dictionary<ConsumerHeldCursorKey, ConsumerHeldCursor> _consumerHeldCursors = new();
        private readonly ulong[] _modalBindingDownBaseline = new ulong[KeyCapacity];
        private long _modalBindingBaselineEpoch = -1;
        private ulong _modalBindingBaselineAnyDown;
        private long _nextRefreshTimestamp;
        private long _nextModalRefreshTimestamp;
        private bool _nativeModalAvailable = true;
        private bool _nativeModalActive;
        private string? _lastAuditModId;
        private QueryAudit? _lastAudit;

        public ThreadState()
        {
            Snapshot.AbiVersion = 1;
            Snapshot.StructSize = NativeSnapshotSize;
        }

        public bool ReadEdge(long cursorKey, ulong current)
        {
            if (!_edgeCursors.TryGetValue(cursorKey, out var consumed))
            {
                _edgeCursors[cursorKey] = current;
                return false;
            }
            if (current == consumed)
                return false;
            _edgeCursors[cursorKey] = current;
            return true;
        }

        public bool ReadNativeKeyEdge(
            long cursorKey,
            int keyIndex,
            ulong current,
            long inputEpoch,
            bool useModalBindingBaseline)
        {
            if (_edgeCursors.TryGetValue(cursorKey, out var consumed))
            {
                if (current == consumed)
                    return false;
                _edgeCursors[cursorKey] = current;
                return true;
            }

            _edgeCursors[cursorKey] = current;
            return useModalBindingBaseline &&
                   _modalBindingBaselineEpoch == inputEpoch &&
                   (uint)keyIndex < KeyCapacity &&
                   current > _modalBindingDownBaseline[keyIndex];
        }

        public void UpdateModalBindingBaseline(long inputEpoch)
        {
            if (!Available ||
                (_modalBindingBaselineEpoch == inputEpoch &&
                 _modalBindingBaselineAnyDown == Snapshot.KeyboardLifetimeDownCount))
            {
                return;
            }

            fixed (ulong* source = Snapshot.DownOrdinals)
            {
                for (var index = 0; index < KeyCapacity; ++index)
                    _modalBindingDownBaseline[index] = source[index];
            }
            _modalBindingBaselineEpoch = inputEpoch;
            _modalBindingBaselineAnyDown = Snapshot.KeyboardLifetimeDownCount;
        }

        public bool ReadConsumerEdge(
            string modId,
            long registrationGeneration,
            long cursorKey,
            ulong current,
            long inputEpoch,
            bool baselineOnly)
        {
            var key = new ConsumerCursorKey(modId, registrationGeneration, cursorKey);
            if (!_consumerEdgeCursors.TryGetValue(key, out var cursor))
            {
                cursor = new ConsumerEdgeCursor
                {
                    InputEpoch = inputEpoch,
                    Consumed = baselineOnly ? current : 0
                };
                _consumerEdgeCursors.Add(key, cursor);
                if (baselineOnly)
                    return false;
            }
            else if (cursor.InputEpoch != inputEpoch)
            {
                cursor.InputEpoch = inputEpoch;
                cursor.Consumed = current;
                return false;
            }
            if (baselineOnly)
            {
                cursor.Consumed = current;
                return false;
            }
            if (current <= cursor.Consumed)
                return false;
            ++cursor.Consumed;
            return true;
        }

        public bool ReadConsumerHeld(
            string modId,
            long registrationGeneration,
            PcCompatInputIdentityKind identityKind,
            int identityValue,
            int callsiteToken,
            PcCompatKeyViewerConsumerKeyState current,
            long inputEpoch,
            bool baselineOnly)
        {
            var key = new ConsumerHeldCursorKey(
                modId,
                registrationGeneration,
                identityKind,
                identityValue,
                callsiteToken);
            if (!_consumerHeldCursors.TryGetValue(key, out var cursor))
            {
                cursor = new ConsumerHeldCursor { InputEpoch = inputEpoch };
                _consumerHeldCursors.Add(key, cursor);
            }

            if (baselineOnly || cursor.InputEpoch != inputEpoch)
            {
                cursor.InputEpoch = inputEpoch;
                cursor.ConsumedDown = current.DownOrdinal;
                cursor.ConsumedUp = current.UpOrdinal;
                cursor.Held = false;
                cursor.SuppressUntilReleased = current.Held;
                return false;
            }
            if (cursor.SuppressUntilReleased)
            {
                cursor.ConsumedDown = current.DownOrdinal;
                cursor.ConsumedUp = current.UpOrdinal;
                if (current.Held)
                    return false;
                cursor.SuppressUntilReleased = false;
                return false;
            }

            // Multiple lanes or aliases can publish one aggregate identity. A
            // partial UP must not replay as a release while another source is
            // still holding that identity.
            if (current.Held)
            {
                cursor.ConsumedDown = current.DownOrdinal;
                cursor.ConsumedUp = current.UpOrdinal;
                cursor.Held = true;
                return true;
            }

            if (!cursor.Held && current.DownOrdinal > cursor.ConsumedDown)
            {
                ++cursor.ConsumedDown;
                cursor.Held = true;
            }
            else if (cursor.Held && current.UpOrdinal > cursor.ConsumedUp)
            {
                ++cursor.ConsumedUp;
                cursor.Held = false;
            }
            else if (current.DownOrdinal == cursor.ConsumedDown &&
                     current.UpOrdinal == cursor.ConsumedUp)
            {
                cursor.Held = current.Held;
            }
            return cursor.Held;
        }

        public void Refresh()
        {
            if (Volatile.Read(ref s_nativeUnavailable) != 0)
                return;

            var now = Stopwatch.GetTimestamp();
            if (now < _nextRefreshTimestamp)
                return;
            _nextRefreshTimestamp = now + Math.Max(1, Stopwatch.Frequency / 1000);

            int result;
            try
            {
                result = ReadLegacyInputSnapshot(ref Snapshot, NativeSnapshotSize);
            }
            catch (Exception exception) when (exception is DllNotFoundException or
                                               EntryPointNotFoundException or
                                               BadImageFormatException)
            {
                Interlocked.Exchange(ref s_nativeUnavailable, 1);
                return;
            }
            if (result < 0)
            {
                Interlocked.Exchange(ref s_nativeUnavailable, 1);
                return;
            }
            Available = true;
        }

        public bool IsModalInputCaptureActive()
        {
            var managedActive = Volatile.Read(ref s_modalInputCaptureActive) != 0;
            if (!_nativeModalAvailable)
                return managedActive;

            var now = Stopwatch.GetTimestamp();
            if (now >= _nextModalRefreshTimestamp)
            {
                _nextModalRefreshTimestamp = now + Math.Max(1, Stopwatch.Frequency / 1000);
                try
                {
                    _nativeModalActive = ReadNativeModalInputActive() != 0;
                    var observed = _nativeModalActive ? 1 : 0;
                    if (Interlocked.Exchange(
                            ref s_nativeObservedModalInputActive,
                            observed) != observed)
                    {
                        Interlocked.Increment(ref s_modalInputEpoch);
                    }
                }
                catch (Exception exception) when (exception is DllNotFoundException or
                                                   EntryPointNotFoundException or
                                                   BadImageFormatException)
                {
                    _nativeModalAvailable = false;
                    _nativeModalActive = false;
                }
            }
            return managedActive || _nativeModalActive;
        }

        public void RecordQuery(
            string modId,
            LegacyQueryKind kind,
            bool matched,
            bool observedTrue,
            ulong sourceSequence,
            long registrationGeneration)
        {
            var audit = GetAudit(modId);
            audit.Record(kind, matched, observedTrue, sourceSequence, registrationGeneration);
        }

        private QueryAudit GetAudit(string modId)
        {
            if (_lastAudit != null &&
                string.Equals(_lastAuditModId, modId, StringComparison.OrdinalIgnoreCase))
            {
                return _lastAudit;
            }

            var audit = new QueryAudit(Environment.CurrentManagedThreadId);
            QueryAudits.GetOrAdd(modId, static _ => new ConcurrentBag<QueryAudit>()).Add(audit);
            _lastAuditModId = modId;
            _lastAudit = audit;
            return audit;
        }
    }

    private enum LegacyQueryKind
    {
        UnityHeld,
        WindowsHeld,
        UnityEdge,
        AnyKeyEdge
    }

    private sealed class QueryAudit(int threadId)
    {
        public int ThreadId { get; } = threadId;
        public long UnityHeldCount;
        public long WindowsHeldCount;
        public long UnityEdgeCount;
        public long AnyKeyEdgeCount;
        public long MatchedCount;
        public long TrueCount;
        public long LastSourceSequence;
        public long LastRegistrationGeneration;
        public void Record(
            LegacyQueryKind kind,
            bool matched,
            bool observedTrue,
            ulong sourceSequence,
            long registrationGeneration)
        {
            switch (kind)
            {
                case LegacyQueryKind.UnityHeld: ++UnityHeldCount; break;
                case LegacyQueryKind.WindowsHeld: ++WindowsHeldCount; break;
                case LegacyQueryKind.UnityEdge: ++UnityEdgeCount; break;
                case LegacyQueryKind.AnyKeyEdge: ++AnyKeyEdgeCount; break;
            }
            if (matched)
                ++MatchedCount;
            if (observedTrue)
                ++TrueCount;
            LastSourceSequence = unchecked((long)sourceSequence);
            LastRegistrationGeneration = registrationGeneration;
        }

    }

    private readonly record struct ConsumerCursorKey(
        string ModId,
        long RegistrationGeneration,
        long CursorKey);

    private readonly record struct ConsumerHeldCursorKey(
        string ModId,
        long RegistrationGeneration,
        PcCompatInputIdentityKind IdentityKind,
        int IdentityValue,
        int CallsiteToken);

    private sealed class ConsumerHeldCursor
    {
        public long InputEpoch;
        public ulong ConsumedDown;
        public ulong ConsumedUp;
        public bool Held;
        public bool SuppressUntilReleased;
    }

    private sealed class ConsumerEdgeCursor
    {
        public long InputEpoch;
        public ulong Consumed;
    }
}

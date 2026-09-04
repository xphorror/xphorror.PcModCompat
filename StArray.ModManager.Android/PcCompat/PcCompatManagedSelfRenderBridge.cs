using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StArray.ModManager.Manager;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Android.PcCompat;

internal static unsafe class PcCompatManagedSelfRenderBridge
{
    private const string LogTag = "PcCompatSelfRender";
    private const string NativeLibrary = "libstarray_modmanager";
    private static int s_installed;
    private static int s_callbackActive;
    private static long s_lastFrameTimestamp;
    private static long s_lastTelemetryPollTimestamp;
    private static long s_frameCallbackCount;
    private static long s_frameReentrySkips;
    private static long s_frameFailureCount;
    private static long s_frameDispatchTotalTicks;
    private static long s_frameDispatchLastTicks;
    private static long s_frameDispatchMaxTicks;
    private static long s_frameDispatchOverBudgetCount;
    private static int s_frameMode;
    private static int s_onGUIEnabled;
    private static int s_unityMainManagedThreadId;
    private static readonly ConcurrentDictionary<uint, string> BundleModIds = new();
    private static readonly byte[] BoxedValueNameBuffer = new byte[256];
    private static readonly object PrefixOrderPlanLock = new();

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_set_managed_frame_callback")]
    private static extern void SetManagedFrameCallback(nint callback);

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_set_managed_ongui_callback")]
    private static extern void SetManagedOnGUICallback(nint callback);

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_set_managed_ongui_enabled")]
    private static extern void SetManagedOnGUIEnabledNative(int enabled);

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_set_managed_frame_mode")]
    private static extern void SetManagedFrameModeNative(int mode);

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_poll_shared_game_snapshot")]
    private static extern int PollSharedGameSnapshotNative();

    [DllImport(
        NativeLibrary,
        EntryPoint = "modmanager_pccompat_request_presentation_sink_install")]
    private static extern void RequestPresentationSinkInstallNative();

    [DllImport(
        NativeLibrary,
        EntryPoint = "modmanager_pccompat_is_presentation_sink_installed")]
    private static extern int IsPresentationSinkInstalledNative();

    [DllImport(
        NativeLibrary,
        EntryPoint = "modmanager_pccompat_set_recipe_presentation_enabled")]
    private static extern int SetRecipePresentationEnabledNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modId,
        int enabled);

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_set_managed_events_enabled")]
    private static extern int SetManagedEventsEnabledNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modId,
        int enabled);

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_drain_managed_events")]
    private static extern int DrainManagedEventsNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modId,
        [Out] byte[] output,
        int capacityEvents,
        out ulong droppedOut);

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_get_managed_event_record_size")]
    private static extern int GetManagedEventRecordSizeNative();

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_read_boxed_value_info")]
    private static extern int ReadBoxedValueInfoNative(
        nint boxed,
        [Out] byte[] nameOut,
        int nameCapacity,
        out long valueOut);

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_set_managed_prefix_callback")]
    private static extern void SetManagedPrefixCallback(nint callback);

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_get_managed_prefix_invocation_size")]
    private static extern int GetManagedPrefixInvocationSizeNative();

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_begin_managed_prefix_order_plan")]
    private static extern int BeginManagedPrefixOrderPlanNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modId);

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_add_managed_prefix_order")]
    private static extern int AddManagedPrefixOrderNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modId,
        uint patchId,
        int priority,
        ulong registrationIndex,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string beforeJson,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string afterJson);

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_commit_managed_prefix_order_plan")]
    private static extern int CommitManagedPrefixOrderPlanNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modId);

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_begin_managed_postfix_order_plan")]
    private static extern int BeginManagedPostfixOrderPlanNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modId);

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_add_managed_postfix_order")]
    private static extern int AddManagedPostfixOrderNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modId,
        uint patchId,
        int priority,
        ulong registrationIndex,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string owner,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string beforeJson,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string afterJson);

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_commit_managed_postfix_order_plan")]
    private static extern int CommitManagedPostfixOrderPlanNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modId);

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_read_bundle_mod_id")]
    private static extern int ReadBundleModIdNative(uint bundleId, byte* output, int capacity);

    public static void Install()
    {
        if (Interlocked.Exchange(ref s_installed, 1) != 0)
            return;

        try
        {
            var nativeEventRecordSize = GetManagedEventRecordSizeNative();
            if (nativeEventRecordSize != PcCompatManagedCallbackDispatcher.EventRecordSize)
            {
                throw new InvalidOperationException(
                    $"Managed event ABI mismatch native={nativeEventRecordSize} " +
                    $"managed={PcCompatManagedCallbackDispatcher.EventRecordSize}.");
            }
            var nativePrefixInvocationSize = GetManagedPrefixInvocationSizeNative();
            if (nativePrefixInvocationSize != PcCompatManagedPrefixInvocationV2.ExpectedSize)
            {
                throw new InvalidOperationException(
                    $"Managed Prefix invocation ABI mismatch native={nativePrefixInvocationSize} " +
                    $"managed={PcCompatManagedPrefixInvocationV2.ExpectedSize}.");
            }
            var callback = (nint)(delegate* unmanaged[Cdecl]<void>)&OnManagedFrame;
            SetManagedFrameCallback(callback);
            SetManagedOnGUICallback((nint)(delegate* unmanaged[Cdecl]<void>)&OnManagedOnGUI);
            SetManagedPrefixCallback(
                (nint)(delegate* unmanaged[Cdecl]<uint, uint, PcCompatManagedPrefixInvocationV2*, int>)&OnManagedPrefix);
            RequestPresentationSinkInstallNative();
            PcCompatRuntime.RegisterManagedFrameGateSink(SetFrameEnabled);
            PcCompatRuntime.RegisterManagedOnGUIGateSink(SetOnGUIEnabled);
            PcCompatRuntime.RegisterManagedPresentationOwnershipSink(SetPresentationOwnership);
            PcCompatRuntime.RegisterManagedPrefixOrderPlanSink(SetManagedPrefixOrderPlan);
            PcCompatRuntime.RegisterManagedPostfixOrderPlanSink(SetManagedPostfixOrderPlan);
            PcCompatRuntime.RegisterManagedEventSinks(DrainManagedEvents, TryReadBoxedValue);
            Logger.Info(LogTag, "managed UnityMain frame bridge registered");
        }
        catch (Exception exception)
        {
            try { SetManagedFrameCallback(nint.Zero); } catch { }
            try { SetManagedOnGUICallback(nint.Zero); } catch { }
            try { SetManagedPrefixCallback(nint.Zero); } catch { }
            PcCompatRuntime.RegisterManagedFrameGateSink(null);
            PcCompatRuntime.RegisterManagedOnGUIGateSink(null);
            PcCompatRuntime.RegisterManagedPresentationOwnershipSink(null);
            PcCompatRuntime.RegisterManagedPrefixOrderPlanSink(null);
            PcCompatRuntime.RegisterManagedPostfixOrderPlanSink(null);
            PcCompatRuntime.RegisterManagedEventSinks(null, null);
            Volatile.Write(ref s_installed, 0);
            Logger.Error(LogTag, "managed self-render bridge registration failed: " + exception);
        }
    }

    private static bool SetPresentationOwnership(
        string modId,
        bool managedOwnsPresentation)
    {
        // Game event callbacks only flow while the MOD owns presentation; the two
        // flips are deliberately one operation so compat-render never leaves the
        // managed queue running (and self-render never leaves it stopped).
        var recipeResult = SetRecipePresentationEnabledNative(
            modId,
            managedOwnsPresentation ? 0 : 1);
        var eventRings = SetManagedEventsEnabledNative(
            modId,
            managedOwnsPresentation ? 1 : 0);
        if (managedOwnsPresentation && eventRings <= 0)
            Logger.Warn(
                LogTag,
                $"managed event rings missing mod={modId}; game event callbacks stay idle");
        return recipeResult >= 0;
    }

    private static int DrainManagedEvents(string modId, byte[] output, out ulong dropped)
        => DrainManagedEventsNative(
            modId,
            output,
            output.Length / PcCompatManagedCallbackDispatcher.EventRecordSize,
            out dropped);

    private static bool TryReadBoxedValue(nint boxed, out string? typeName, out long value)
    {
        var nameBuffer = BoxedValueNameBuffer;
        var result = ReadBoxedValueInfoNative(boxed, nameBuffer, nameBuffer.Length, out value);
        if (result != 0)
        {
            typeName = null;
            return false;
        }

        var length = Array.IndexOf(nameBuffer, (byte)0);
        typeName = length > 0 ? System.Text.Encoding.UTF8.GetString(nameBuffer, 0, length) : null;
        return typeName != null;
    }

    private static void SetFrameEnabled(PcCompatManagedFrameDispatchMode mode)
    {
        if (mode != PcCompatManagedFrameDispatchMode.Disabled &&
            IsPresentationSinkInstalledNative() == 0)
        {
            RequestPresentationSinkInstallNative();
        }

        Interlocked.Exchange(ref s_lastFrameTimestamp, 0);
        Interlocked.Exchange(ref s_lastTelemetryPollTimestamp, 0);
        Interlocked.Exchange(ref s_frameCallbackCount, 0);
        Interlocked.Exchange(ref s_frameReentrySkips, 0);
        Interlocked.Exchange(ref s_frameFailureCount, 0);
        Interlocked.Exchange(ref s_frameDispatchTotalTicks, 0);
        Interlocked.Exchange(ref s_frameDispatchLastTicks, 0);
        Interlocked.Exchange(ref s_frameDispatchMaxTicks, 0);
        Interlocked.Exchange(ref s_frameDispatchOverBudgetCount, 0);
        Volatile.Write(ref s_frameMode, (int)mode);
        SetManagedFrameModeNative((int)mode);
        Logger.Info(LogTag, "managed UnityMain frame mode=" + mode);
    }

    private static void SetManagedPrefixOrderPlan(
        string modId,
        IReadOnlyList<PcCompatManagedPrefixOrderEntry> entries)
    {
        lock (PrefixOrderPlanLock)
        {
            if (BeginManagedPrefixOrderPlanNative(modId) != 1)
                throw new InvalidOperationException($"Could not begin managed Prefix order plan for {modId}.");

            try
            {
                foreach (var entry in entries)
                {
                    var result = AddManagedPrefixOrderNative(
                        modId,
                        entry.PatchId,
                        entry.Priority,
                        unchecked((ulong)Math.Max(0, entry.RegistrationIndex)),
                        entry.Owner ?? string.Empty,
                        System.Text.Json.JsonSerializer.Serialize(entry.Before),
                        System.Text.Json.JsonSerializer.Serialize(entry.After));
                    if (result != 1)
                    {
                        throw new InvalidOperationException(
                            $"Could not add managed Prefix order entry mod={modId} patch={entry.PatchId}: {result}.");
                    }
                }

                if (CommitManagedPrefixOrderPlanNative(modId) < 0)
                    throw new InvalidOperationException($"Could not commit managed Prefix order plan for {modId}.");
            }
            catch
            {
                _ = BeginManagedPrefixOrderPlanNative(modId);
                _ = CommitManagedPrefixOrderPlanNative(modId);
                throw;
            }
        }
    }

    private static void SetManagedPostfixOrderPlan(
        string modId,
        IReadOnlyList<PcCompatManagedPostfixOrderEntry> entries)
    {
        lock (PrefixOrderPlanLock)
        {
            if (BeginManagedPostfixOrderPlanNative(modId) != 1)
                throw new InvalidOperationException($"Could not begin managed Postfix order plan for {modId}.");

            try
            {
                foreach (var entry in entries)
                {
                    var result = AddManagedPostfixOrderNative(
                        modId,
                        entry.PatchId,
                        entry.Priority,
                        unchecked((ulong)Math.Max(0, entry.RegistrationIndex)),
                        entry.Owner ?? string.Empty,
                        System.Text.Json.JsonSerializer.Serialize(entry.Before),
                        System.Text.Json.JsonSerializer.Serialize(entry.After));
                    if (result != 1)
                    {
                        throw new InvalidOperationException(
                            $"Could not add managed Postfix order entry mod={modId} patch={entry.PatchId}: {result}.");
                    }
                }

                if (CommitManagedPostfixOrderPlanNative(modId) < 0)
                    throw new InvalidOperationException($"Could not commit managed Postfix order plan for {modId}.");
            }
            catch
            {
                _ = BeginManagedPostfixOrderPlanNative(modId);
                _ = CommitManagedPostfixOrderPlanNative(modId);
                throw;
            }
        }
    }

    private static void SetOnGUIEnabled(bool enabled)
    {
        if (enabled && IsPresentationSinkInstalledNative() == 0)
            RequestPresentationSinkInstallNative();
        Volatile.Write(ref s_onGUIEnabled, enabled ? 1 : 0);
        PcCompatInjectedOnGUIHost.SetDemand(enabled);
        SetManagedOnGUIEnabledNative(
            enabled && !PcCompatInjectedOnGUIHost.IsDispatchReady ? 1 : 0);
        Logger.Info(
            LogTag,
            "managed UnityMain OnGUI enabled=" + enabled +
            " injectedHost=" + PcCompatInjectedOnGUIHost.IsDispatchReady +
            " nativeFallback=" + (enabled && !PcCompatInjectedOnGUIHost.IsDispatchReady));
    }

    internal static void NotifyInjectedOnGUIHostReady()
    {
        SetManagedOnGUIEnabledNative(0);
        Logger.Info(LogTag, "injected OnGUI host owns managed IMGUI dispatch");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnManagedFrame()
    {
        ObserveUnityMainThread();
        if (Interlocked.Exchange(ref s_callbackActive, 1) != 0)
        {
            Interlocked.Increment(ref s_frameReentrySkips);
            return;
        }

        using var unityMainScope = PcCompatUnityMainExecutionContext.Enter();
        long dispatchStarted = 0;
        try
        {
            if (Volatile.Read(ref s_onGUIEnabled) != 0 &&
                !PcCompatInjectedOnGUIHost.IsDispatchReady)
            {
                PcCompatInjectedOnGUIHost.SetDemand(true);
            }
            Interlocked.Increment(ref s_frameCallbackCount);
            var now = Stopwatch.GetTimestamp();
            dispatchStarted = now;
            var previous = Interlocked.Exchange(ref s_lastFrameTimestamp, now);
            var deltaTime = previous == 0
                ? 0f
                : (float)Math.Clamp(
                    (now - previous) / (double)Stopwatch.Frequency,
                    0d,
                    0.25d);
            // Shared gameplay facts are host telemetry. Drive their throttled sampler from the
            // established UnityMain frame instead of making data availability depend on one MOD
            // recipe's PlayerControl_Update hook remaining installable.
            var lastTelemetryPoll = Interlocked.Read(ref s_lastTelemetryPollTimestamp);
            if (lastTelemetryPoll == 0 ||
                now - lastTelemetryPoll >= Stopwatch.Frequency / 10)
            {
                Interlocked.Exchange(ref s_lastTelemetryPollTimestamp, now);
                _ = PollSharedGameSnapshotNative();
            }
            PcCompatDynamicGetterSnapshotHost.RefreshOnUnityMain();
            PcCompatRuntime.DispatchManagedFrame(deltaTime);
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref s_frameFailureCount);
            Logger.Error(LogTag, "managed frame dispatch failed closed: " + exception);
        }
        finally
        {
            if (dispatchStarted != 0)
                RecordDispatchDuration(Stopwatch.GetTimestamp() - dispatchStarted);
            Volatile.Write(ref s_callbackActive, 0);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnManagedPrefix(
        uint bundleId,
        uint patchId,
        PcCompatManagedPrefixInvocationV2* invocation)
    {
        var unityMainThread = Volatile.Read(ref s_unityMainManagedThreadId);
        if (unityMainThread == 0 || Environment.CurrentManagedThreadId != unityMainThread)
            return -10;

        if (!BundleModIds.TryGetValue(bundleId, out var modId))
        {
            Span<byte> name = stackalloc byte[256];
            fixed (byte* output = name)
            {
                var length = ReadBundleModIdNative(bundleId, output, name.Length);
                if (length <= 0 || length >= name.Length)
                    return -11;
                modId = System.Text.Encoding.UTF8.GetString(name[..length]);
            }
            BundleModIds.TryAdd(bundleId, modId);
        }

        if (invocation == null || !invocation->HasValidLayout)
            return -12;
        try
        {
            var backup = *invocation;
            try
            {
                return PcCompatRuntime.DispatchManagedPrefix(
                    modId,
                    patchId,
                    ref *invocation);
            }
            catch
            {
                *invocation = backup;
                return -12;
            }
        }
        catch
        {
            return -12;
        }
    }

    internal static string GetDiagnostics()
    {
        var lastFrame = Interlocked.Read(ref s_lastFrameTimestamp);
        var ageMs = lastFrame == 0
            ? -1
            : Math.Max(
                0,
                (long)((Stopwatch.GetTimestamp() - lastFrame) * 1000d / Stopwatch.Frequency));
        var callbacks = Interlocked.Read(ref s_frameCallbackCount);
        var lastTicks = Interlocked.Read(ref s_frameDispatchLastTicks);
        var totalTicks = Interlocked.Read(ref s_frameDispatchTotalTicks);
        var maxTicks = Interlocked.Read(ref s_frameDispatchMaxTicks);
        return
            $"mode={(PcCompatManagedFrameDispatchMode)Volatile.Read(ref s_frameMode)} " +
            $"onGuiEnabled={Volatile.Read(ref s_onGUIEnabled) != 0} " +
            $"onGuiHost=[{PcCompatInjectedOnGUIHost.GetDiagnostics()}] " +
            $"callbacks={callbacks} " +
            $"reentrySkips={Interlocked.Read(ref s_frameReentrySkips)} " +
            $"failures={Interlocked.Read(ref s_frameFailureCount)} lastFrameAgeMs={ageMs} " +
            $"workUs={TicksToMicroseconds(lastTicks)} " +
            $"avgWorkUs={TicksToMicroseconds(callbacks == 0 ? 0 : totalTicks / callbacks)} " +
            $"maxWorkUs={TicksToMicroseconds(maxTicks)} " +
            $"over4ms={Interlocked.Read(ref s_frameDispatchOverBudgetCount)}";
    }

    internal static bool IsCurrentUnityMainThread()
    {
        var unityMainThread = Volatile.Read(ref s_unityMainManagedThreadId);
        return unityMainThread != 0 &&
               Environment.CurrentManagedThreadId == unityMainThread;
    }

    private static void RecordDispatchDuration(long elapsedTicks)
    {
        Interlocked.Exchange(ref s_frameDispatchLastTicks, elapsedTicks);
        Interlocked.Add(ref s_frameDispatchTotalTicks, elapsedTicks);
        if (elapsedTicks * 1000L >= Stopwatch.Frequency * 4L)
            Interlocked.Increment(ref s_frameDispatchOverBudgetCount);

        var maximum = Interlocked.Read(ref s_frameDispatchMaxTicks);
        while (elapsedTicks > maximum)
        {
            var previous = Interlocked.CompareExchange(
                ref s_frameDispatchMaxTicks,
                elapsedTicks,
                maximum);
            if (previous == maximum)
                break;
            maximum = previous;
        }
    }

    private static long TicksToMicroseconds(long ticks)
        => ticks <= 0 ? 0 : ticks * 1_000_000L / Stopwatch.Frequency;

    // Runs inside Unity's IMGUI event pump (native GUIUtility.ProcessEvent
    // hook), so mod GUI/GUILayout calls see a valid IMGUI context and the real
    // Event.current. Must not touch the frame-dispatch rate limiter: IMGUI
    // events arrive multiple times per frame.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnManagedOnGUI()
    {
        if (PcCompatInjectedOnGUIHost.IsDispatchReady)
            return;
        DispatchOnGUICore();
    }

    internal static void DispatchOnGUIFromInjectedHost()
    {
        if (Volatile.Read(ref s_onGUIEnabled) == 0 ||
            !PcCompatInjectedOnGUIHost.IsDispatchReady)
        {
            return;
        }
        DispatchOnGUICore();
    }

    private static void DispatchOnGUICore()
    {
        ObserveUnityMainThread();
        if (Interlocked.Exchange(ref s_callbackActive, 1) != 0)
            return;

        using var unityMainScope = PcCompatUnityMainExecutionContext.Enter();
        try
        {
            PcCompatRuntime.DispatchManagedOnGUI();
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, "managed OnGUI dispatch failed closed: " + exception);
        }
        finally
        {
            Volatile.Write(ref s_callbackActive, 0);
        }
    }

    private static void ObserveUnityMainThread()
    {
        var threadId = Environment.CurrentManagedThreadId;
        _ = Interlocked.CompareExchange(ref s_unityMainManagedThreadId, threadId, 0);
    }
}

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using StArray.ModManager.Manager;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Android.PcCompat;

public static unsafe class PcCompatNativeHookRules
{
    private const string Lib = "starray_modmanager";
    private const string LogTag = "PcCompatHookRules";
    private const uint OverlaySnapshotAbiVersion = 7;
    private const uint InputHudSnapshotAbiVersion = 1;
    private const uint RawInputReadAbiVersion = 1;
    private const uint ExternalInputDeviceSnapshotAbiVersion = 1;
    private const uint ClockAnchorSnapshotAbiVersion = 1;
    private const uint VmFaultSnapshotAbiVersion = 1;
    private const uint PresentationSnapshotAbiVersion = 1;
    private const uint PresentationSinkStatsAbiVersion = 4;
    private const uint ManagedEventStatsAbiVersion = 1;
    private const uint HitMarginSnapshotAbiVersion = 1;
    internal const int HitMarginSnapshotMaxCounts = 16;
    private const int PresentationMaxCommands = 64;
    private const int PresentationCommandNativeSize = 56;
    private static readonly uint OverlaySnapshotNativeSize =
        checked((uint)Marshal.SizeOf<OverlaySnapshotNative>());
    private static PcCompatOverlaySnapshot s_cachedOverlaySnapshot = PcCompatOverlaySnapshot.Unavailable;
    private static PcCompatOverlaySnapshot s_cachedSharedGameSnapshot = PcCompatOverlaySnapshot.Unavailable;
    private static readonly ConcurrentDictionary<string, PcCompatOverlaySnapshot>
        CachedOverlaySnapshots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object InputHudSnapshotLock = new();
    private static readonly Dictionary<int, PcCompatInputHudSnapshot> CachedInputHudSnapshots = new();
    private static PcCompatClockAnchorSnapshot s_cachedClockAnchor = PcCompatClockAnchorSnapshot.Unavailable;
    private static uint s_cachedMonotonicClockGeneration;
    private static long s_cachedMonotonicRawNs;
    private static int s_cachedMonotonicAvailable;
    private static readonly object PresentationSnapshotLock = new();
    private static PcCompatPresentationSnapshot s_cachedPresentationSnapshot =
        PcCompatPresentationSnapshot.Unavailable;
    private static readonly object VmFaultLock = new();
    private static ulong s_vmFaultCursor;
    private static PcCompatVmFaultSnapshot s_latestVmFault = PcCompatVmFaultSnapshot.Unavailable;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct OverlaySnapshotNative
    {
        public uint StructSize;
        public uint AbiVersion;
        public uint Generation;
        public uint Visible;
        public uint Practice;
        public uint ShowCount;
        public uint HideCount;
        public uint PlayerUpdateCount;
        public uint StateChangeCount;
        public int LastOpCode;
        public int LastTargetKind;
        public int PlayerCount;
        public int LastSeqId;
        public uint LastIsRestart;
        public int LastWipeDirection;
        public uint LastResetToEditor;
        public uint JudgementHitCount;
        public uint JudgementResetCount;
        public int LastHitMargin;
        public uint FloorMoveCount;
        public float LastFloorExitAngle;
        public int LastFloorMoveHitMargin;
        public uint PlayerHitCount;
        public uint LastPlayerHitIsAuto;
        public uint DeathCount;
        public uint LastDeathOverload;
        public uint LastDeathMultipress;
        public uint LastDeathHitbox;
        public uint HitTimingCount;
        public float LastHitTimingMs;
        public int LastHitTimingMargin;
        public uint AccuracySnapshotCount;
        public float PercentAcc;
        public float PercentXAcc;
        public float Progress;
        public int ComboCount;
        public uint AttemptCount;
        public uint BpmSnapshotCount;
        public float TileBpm;
        public float Kps;
        public uint TimelineSnapshotCount;
        public float MusicTime;
        public float MusicTotalTime;
        public float MapTime;
        public float MapTotalTime;
        public int CheckpointsUsed;
        public int CurrentCheckpoint;
        public int TotalCheckpoints;
        public int CurrentSeqId;
        public int FloorCount;
        public float StartProgress;
        public float SpeedMultiplier;
        public uint SessionAuto;
        public uint InputStateGeneration;
        public uint InputHeldMask;
        public uint InputLastDownMask;
        public uint InputLastUpMask;
        public uint InputTotalCount;
        public float InputKps;
        public float PlanetSpeed;
        public uint RdcAuto;
        public uint NoFail;
        public uint Paused;
        public uint IsGameWorld;
        public float SongPitch;
        public double ConductorAddOffset;
        public double ConductorSongPositionMinusi;
        public uint IsScnGame;
        public uint IsGameReady;
        public uint SessionEpoch;
        public ulong ValidGameSnapshotFields;
        public ulong ControllerPointer;
        public ulong ConductorPointer;
        public ulong LevelMakerPointer;
        public ulong CurrentFloorPointer;
        public ulong FirstFloorPointer;
        public ulong SongPointer;
        public ulong PlanetarySystemPointer;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private unsafe struct InputHudSnapshotNative
    {
        public uint StructSize;
        public uint AbiVersion;
        public uint PublicationGeneration;
        public uint SessionGeneration;
        public uint SourceGeneration;
        public uint TouchLaneCount;
        public uint TouchLaneHeldMask;
        public uint TouchLaneLastDownMask;
        public uint TouchLaneLastUpMask;
        public uint InputTotalCount;
        public uint KeyboardHeldCount;
        public float InputKps;
        public ulong SourceSequence;
        public ulong DroppedEventCount;
        public long CompletedRawNs;
        public long SessionAnchorRawNs;
        public fixed ushort TouchLaneHeldCounts[10];
        public fixed ushort Reserved[2];
        public fixed uint TouchLaneTotalCounts[10];
        public fixed long TouchLaneLastDownRawNs[10];
        public fixed long TouchLaneLastUpRawNs[10];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct RawInputReadNative
    {
        public uint StructSize;
        public uint AbiVersion;
        public ulong Cursor;
        public ulong DroppedBeforeCursor;
        public uint Count;
        public uint Capacity;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct RawInputEventNative
    {
        public ulong Sequence;
        public long RawNs;
        public uint StateGeneration;
        public uint SessionGeneration;
        public uint ProducerEpoch;
        public byte Producer;
        public byte Source;
        public byte Phase;
        public byte Reserved0;
        public int Code;
        public int Slot;
        public int PointerCount;
        public int ScanCode;
        public int MetaState;
        public int DeviceId;
        public int RepeatCount;
        public int AndroidFlags;
        public int SourceCode;
        public int ViewportWidth;
        public int ViewportHeight;
        public float X;
        public float Y;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ExternalInputDeviceSnapshotNative
    {
        public uint AbiVersion;
        public uint StructSize;
        public uint Generation;
        public uint Flags;
    }

    private static readonly uint ExternalInputDeviceSnapshotNativeSize =
        checked((uint)Marshal.SizeOf<ExternalInputDeviceSnapshotNative>());

    private static readonly uint RawInputReadNativeSize =
        checked((uint)Marshal.SizeOf<RawInputReadNative>());
    private static readonly uint RawInputEventNativeSize =
        checked((uint)Marshal.SizeOf<RawInputEventNative>());
    [ThreadStatic]
    private static RawInputEventNative[]? s_rawInputEventBuffer;

    private static readonly uint InputHudSnapshotNativeSize =
        checked((uint)Marshal.SizeOf<InputHudSnapshotNative>());

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct HitMarginSnapshotNative
    {
        public uint StructSize;
        public uint AbiVersion;
        public uint Generation;
        public uint Valid;
        public uint Length;
        public uint Checksum;
        public ulong Tracker;
        public fixed int Counts[HitMarginSnapshotMaxCounts];
    }

    private static readonly uint HitMarginSnapshotNativeSize =
        checked((uint)Marshal.SizeOf<HitMarginSnapshotNative>());

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ClockAnchorSnapshotNative
    {
        public uint StructSize;
        public uint AbiVersion;
        public uint PublicationGeneration;
        public uint SessionGeneration;
        public uint ValidMask;
        public int FrameCount;
        public float UnityTimeScale;
        public float AudioPositionSeconds;
        public double UnityScaledSeconds;
        public double SongPositionSeconds;
        public double MapPositionSeconds;
        public long MonotonicRawNs;
    }

    private static readonly uint ClockAnchorSnapshotNativeSize =
        checked((uint)Marshal.SizeOf<ClockAnchorSnapshotNative>());

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PresentationCommandNative
    {
        public ulong Sequence;
        public uint SessionGeneration;
        public uint Generation;
        public uint RuleId;
        public uint CommandType;
        public uint TargetId;
        public uint Reserved;
        public long Payload0;
        public long Payload1;
        public float Value0;
        public float Value1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private unsafe struct PresentationSnapshotNative
    {
        public uint StructSize;
        public uint AbiVersion;
        public uint PublicationGeneration;
        public uint SessionGeneration;
        public uint Available;
        public uint CommandCount;
        public uint Reserved;
        public ulong DroppedStaleTasks;
        public ulong SchedulerOverflowCount;
        public long PublishedRawNs;
        public fixed byte Commands[PresentationCommandNativeSize * PresentationMaxCommands];
    }

    private static readonly uint PresentationSnapshotNativeSize =
        checked((uint)Marshal.SizeOf<PresentationSnapshotNative>());

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PresentationSinkStatsNative
    {
        public uint StructSize;
        public uint AbiVersion;
        public uint Installed;
        public uint PrimaryHook;
        public uint FallbackHook;
        public uint ConsumeOpportunities;
        public uint SnapshotUpdates;
        public uint CommandCount;
        public uint UnsupportedCommandCount;
        public uint LastPublicationGeneration;
        public uint LastSessionGeneration;
        public uint RegisteredGraphCount;
        public uint MaterializedGraphCount;
        public uint GraphMaterializationFailures;
        public uint InvalidTargetCount;
        public uint RetiredGraphCount;
        public ulong PresentationHistoryOverflowCount;
        public uint StreamGapCount;
        public uint StreamFaulted;
        public uint OnGUIHook;
        public uint OnGUIProcessHook;
        public uint OnGUIBeginHook;
        public uint OnGUIEnabled;
        public uint OnGUIProcessEventCount;
        public uint OnGUIBeginGUICount;
        public uint OnGUIDispatchCount;
    }

    private static readonly uint PresentationSinkStatsNativeSize =
        checked((uint)Marshal.SizeOf<PresentationSinkStatsNative>());

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private unsafe struct VmFaultSnapshotNative
    {
        public uint StructSize;
        public uint AbiVersion;
        public ulong Cursor;
        public ulong Sequence;
        public long TimestampNs;
        public uint RuleId;
        public uint Code;
        public uint Pc;
        public uint Opcode;
        public uint Count;
        public ulong DroppedBeforeCursor;
        public fixed byte Message[160];
    }

    private static readonly uint VmFaultSnapshotNativeSize =
        checked((uint)Marshal.SizeOf<VmFaultSnapshotNative>());

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_prime_il2cpp_metadata")]
    private static extern int PrimeIl2CppMetadataNative();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_load_hook_rules_json")]
    private static extern int LoadHookRulesJsonNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    public static bool TryPrimeIl2CppMetadata()
    {
        try
        {
            var result = PrimeIl2CppMetadataNative();
            if (result == 1)
                return true;
            Logger.Error(LogTag, "protected IL2CPP metadata prime failed result=" + result);
            return false;
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, "protected IL2CPP metadata prime failed: " + exception);
            return false;
        }
    }

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_load_hook_rules_bin")]
    private static extern int LoadHookRulesBinaryNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_unload_hook_rules_for_mod")]
    private static extern int UnloadHookRulesForModNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modId);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_loaded_target_count")]
    public static extern int GetLoadedTargetCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_loaded_bundle_count")]
    public static extern int GetLoadedBundleCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_loaded_rule_count")]
    public static extern int GetLoadedRuleCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_loaded_ui_lifecycle_program_count")]
    public static extern int GetLoadedUiLifecycleProgramCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_loaded_ui_object_node_count")]
    public static extern int GetLoadedUiObjectNodeCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_loaded_ui_component_op_count")]
    public static extern int GetLoadedUiComponentOpCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_loaded_ui_resource_binding_count")]
    public static extern int GetLoadedUiResourceBindingCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_loaded_ui_bytecode_instruction_count")]
    public static extern int GetLoadedUiBytecodeInstructionCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_merged_slot_count")]
    public static extern int GetMergedSlotCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_read_external_input_devices")]
    private static extern int ReadExternalInputDevicesNative(
        ref ExternalInputDeviceSnapshotNative output,
        uint outputSize);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_resolve_pending_slots")]
    private static extern int ResolvePendingSlotsNative();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_resolved_slot_count")]
    public static extern int GetResolvedSlotCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_failed_slot_count")]
    public static extern int GetFailedSlotCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_pending_slot_count")]
    public static extern int GetPendingSlotCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_slot_rule_count")]
    public static extern int GetSlotRuleCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_enabled_slot_rule_count")]
    public static extern int GetEnabledSlotRuleCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_disabled_slot_rule_count")]
    public static extern int GetDisabledSlotRuleCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_installable_slot_count")]
    public static extern int GetInstallableSlotCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_install_blocked_slot_count")]
    public static extern int GetInstallBlockedSlotCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_installed_slot_count")]
    public static extern int GetInstalledSlotCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_dispatcher_ready_slot_count")]
    public static extern int GetDispatcherReadySlotCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_dispatcher_capacity")]
    public static extern int GetDispatcherCapacity();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_bound_dispatcher_count")]
    public static extern int GetBoundDispatcherCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_dispatcher_required_count")]
    public static extern int GetDispatcherRequiredCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_dispatcher_new_count")]
    public static extern int GetDispatcherNewCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_dispatcher_allocated_count")]
    public static extern int GetDispatcherAllocatedCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_dispatcher_remaining_count")]
    public static extern int GetDispatcherRemainingCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_dispatcher_blocked_count")]
    public static extern int GetDispatcherBlockedCount();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_prepare_install_plan")]
    private static extern int PrepareInstallPlanNative();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_install_planned_slots")]
    private static extern int InstallPlannedSlotsNative();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_slot_summary")]
    private static extern nint GetSlotSummaryNative();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_slot_summary_for_mod")]
    private static extern nint GetSlotSummaryForModNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modId);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_approved_capabilities")]
    public static extern ulong GetApprovedCapabilities();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_set_approved_capabilities")]
    public static extern void SetApprovedCapabilities(ulong capabilities);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_rule_count_for_target")]
    private static extern int GetRuleCountForTargetNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string typeName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string methodName,
        int paramCount);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_rule_count_for_mod_target")]
    private static extern int GetRuleCountForModTargetNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string typeName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string methodName,
        int paramCount);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_last_error")]
    private static extern nint GetLastErrorNative();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_clear_hook_rules")]
    public static extern void Clear();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_set_overlay_changed_callback")]
    internal static extern void SetOverlayChangedCallback(nint callback);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_set_resource_changer_settings")]
    private static extern void SetResourceChangerSettingsNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modId,
        long sessionGeneration,
        int changeRabbit,
        int changeBallColor,
        int changeTileColor,
        float planetR,
        float planetG,
        float planetB,
        float planetA,
        float titleR,
        float titleG,
        float titleB,
        float titleA,
        float tileR,
        float tileG,
        float tileB,
        float tileA,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string resourcePackName);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_publish_resource_changer_sprite")]
    private static extern int PublishResourceChangerSpriteNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modId,
        long sessionGeneration,
        nint sprite);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_retire_resource_changer_sprite")]
    private static extern int RetireResourceChangerSpriteNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modId,
        long sessionGeneration);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_apply_pending_resource_changer_state")]
    private static extern int ApplyPendingResourceChangerStateNative();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_level_identity")]
    private static extern nint GetLevelIdentityNative();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_active_input_producer")]
    private static extern int GetActiveInputProducerNative();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_set_touch_lane_mapping_mode")]
    private static extern int SetTouchLaneMappingModeNative(int mode);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_set_touch_contact_reuse_delay_ms")]
    private static extern int SetTouchContactReuseDelayMillisecondsNative(int milliseconds);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_read_raw_input_events")]
    private static extern int ReadRawInputEventsNative(
        ref RawInputReadNative header,
        uint headerSize,
        [Out] RawInputEventNative[] events,
        uint eventSize,
        uint eventCapacity);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_wait_raw_input_change")]
    private static extern int WaitRawInputChangeNative(ulong cursor, int timeoutMilliseconds);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_interrupt_raw_input_wait")]
    private static extern void InterruptRawInputWaitNative();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_read_overlay_snapshot")]
    private static extern int ReadOverlaySnapshotNative(
        ref OverlaySnapshotNative snapshot,
        uint snapshotSize);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_read_overlay_snapshot_for_mod")]
    private static extern int ReadOverlaySnapshotForModNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modId,
        ref OverlaySnapshotNative snapshot,
        uint snapshotSize);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_read_shared_game_snapshot")]
    private static extern int ReadSharedGameSnapshotNative(
        ref OverlaySnapshotNative snapshot,
        uint snapshotSize);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_read_input_hud_snapshot")]
    private static extern int ReadInputHudSnapshotNative(
        ref InputHudSnapshotNative snapshot,
        uint snapshotSize);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ManagedEventStatsNative
    {
        public uint StructSize;
        public uint AbiVersion;
        public uint Rings;
        public uint EnabledRings;
        public ulong PushedTotal;
        public ulong QueuedCurrent;
        public ulong DroppedTotal;
    }

    private static readonly uint ManagedEventStatsNativeSize =
        checked((uint)Marshal.SizeOf<ManagedEventStatsNative>());

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_read_managed_event_stats")]
    private static extern int ReadManagedEventStatsNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modId,
        ref ManagedEventStatsNative output,
        uint outputSize);

    public static PcCompatManagedEventNativeStats GetManagedEventStats(string modId)
    {
        var native = new ManagedEventStatsNative();
        var result = ReadManagedEventStatsNative(modId, ref native, ManagedEventStatsNativeSize);
        if (result != 0 ||
            native.StructSize != ManagedEventStatsNativeSize ||
            native.AbiVersion != ManagedEventStatsAbiVersion)
            return PcCompatManagedEventNativeStats.Unavailable;

        return new PcCompatManagedEventNativeStats(
            true,
            (int)native.Rings,
            (int)native.EnabledRings,
            native.PushedTotal,
            native.QueuedCurrent,
            native.DroppedTotal);
    }

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_read_clock_anchor_snapshot")]
    private static extern int ReadClockAnchorSnapshotNative(
        ref ClockAnchorSnapshotNative snapshot,
        uint snapshotSize);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_read_presentation_snapshot")]
    private static extern int ReadPresentationSnapshotNative(
        ref PresentationSnapshotNative snapshot,
        uint snapshotSize);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_read_presentation_sink_stats")]
    private static extern int ReadPresentationSinkStatsNative(
        ref PresentationSinkStatsNative snapshot,
        uint snapshotSize);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_register_imgui_font_mapping")]
    private static extern int RegisterImGuiFontMappingNative(
        nint font,
        nint textCoreFontAsset);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_unregister_imgui_font_mapping")]
    private static extern int UnregisterImGuiFontMappingNative(nint font);

    internal static void RegisterImGuiFontMapping(nint font, nint textCoreFontAsset)
    {
        var result = RegisterImGuiFontMappingNative(font, textCoreFontAsset);
        if (result != 1)
        {
            throw new InvalidOperationException(
                $"Unity 6 IMGUI TextCore font mapping registration failed: {result}");
        }
    }

    internal static void UnregisterImGuiFontMapping(nint font)
    {
        var result = UnregisterImGuiFontMappingNative(font);
        if (result < 0)
        {
            throw new InvalidOperationException(
                $"Unity 6 IMGUI TextCore font mapping removal failed: {result}");
        }
    }

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_read_vm_fault_snapshot")]
    private static extern int ReadVmFaultSnapshotNative(
        ref VmFaultSnapshotNative snapshot,
        uint snapshotSize);

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_overlay_visible")]
    private static extern int GetOverlayVisibleNative();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_get_margin_tracker_instance")]
    internal static extern nint GetMarginTrackerInstance();

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_read_hit_margin_snapshot")]
    private static extern int ReadHitMarginSnapshotNative(
        ref HitMarginSnapshotNative snapshot,
        uint snapshotSize);

    internal static bool TryReadHitMarginSnapshot(
        uint knownGeneration,
        Span<int> destination,
        out uint generation,
        out bool changed,
        out bool valid,
        out int length,
        out int checksum,
        out nint tracker)
    {
        generation = knownGeneration;
        changed = false;
        valid = false;
        length = 0;
        checksum = 0;
        tracker = nint.Zero;
        if (destination.Length < HitMarginSnapshotMaxCounts)
            throw new ArgumentException("Hit-margin snapshot destination is too small.", nameof(destination));

        var native = new HitMarginSnapshotNative();
        var result = ReadHitMarginSnapshotNative(ref native, HitMarginSnapshotNativeSize);
        if (result != 1 ||
            native.StructSize != HitMarginSnapshotNativeSize ||
            native.AbiVersion != HitMarginSnapshotAbiVersion ||
            native.Length > HitMarginSnapshotMaxCounts)
        {
            throw new InvalidDataException(
                $"Invalid native hit-margin snapshot: result={result}, size={native.StructSize}, " +
                $"abi={native.AbiVersion}, length={native.Length}.");
        }

        generation = native.Generation;
        valid = native.Valid != 0;
        length = checked((int)native.Length);
        checksum = unchecked((int)native.Checksum);
        tracker = unchecked((nint)native.Tracker);
        changed = generation != knownGeneration;
        if (!changed)
            return true;

        int* source = native.Counts;
        new ReadOnlySpan<int>(source, length).CopyTo(destination);
        return true;
    }

    public static bool TryLoad(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var result = LoadHookRulesJsonNative(path);
            if (result == 0)
            {
                Logger.Info(LogTag, $"loaded path={path} bundles={GetLoadedBundleCount()} targets={GetLoadedTargetCount()} rules={GetLoadedRuleCount()} slots={GetMergedSlotCount()} slotRules={GetSlotRuleCount()} enabledRules={GetEnabledSlotRuleCount()} disabledRules={GetDisabledSlotRuleCount()} caps=0x{GetApprovedCapabilities():X}");
                return true;
            }

            Logger.Warn(LogTag, $"load failed ret={result} path={path} error={GetLastError()}");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn(LogTag, $"load threw path={path}: {ex.Message}");
            return false;
        }
    }

    public static bool TryLoadBinary(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var result = LoadHookRulesBinaryNative(path);
            if (result == 0)
            {
                Logger.Info(LogTag, $"loaded binary recipe path={path} bundles={GetLoadedBundleCount()} targets={GetLoadedTargetCount()} rules={GetLoadedRuleCount()} uiObjects={GetLoadedUiObjectNodeCount()} uiResources={GetLoadedUiResourceBindingCount()} lifecycle={GetLoadedUiLifecycleProgramCount()} vmInstructions={GetLoadedUiBytecodeInstructionCount()} slots={GetMergedSlotCount()} slotRules={GetSlotRuleCount()} enabledRules={GetEnabledSlotRuleCount()} disabledRules={GetDisabledSlotRuleCount()} caps=0x{GetApprovedCapabilities():X}");
                return true;
            }

            Logger.Warn(LogTag, $"binary recipe load failed ret={result} path={path} error={GetLastError()}");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn(LogTag, $"binary recipe load threw path={path}: {ex.Message}");
            return false;
        }
    }

    public static bool TryUnloadMod(string modId, out int retiredBundles)
    {
        retiredBundles = 0;
        if (string.IsNullOrWhiteSpace(modId))
            return false;

        try
        {
            var result = UnloadHookRulesForModNative(modId);
            if (result < 0)
            {
                Logger.Warn(LogTag, $"unload failed ret={result} mod={modId} error={GetLastError()}");
                return false;
            }

            retiredBundles = result;
            Logger.Info(
                LogTag,
                $"unloaded mod={modId} bundles={result} remainingBundles={GetLoadedBundleCount()} " +
                $"targets={GetLoadedTargetCount()} rules={GetLoadedRuleCount()} slots={GetMergedSlotCount()}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn(LogTag, $"unload threw mod={modId}: {ex.Message}");
            return false;
        }
    }

    public static string GetLastError()
    {
        var ptr = GetLastErrorNative();
        return ptr == nint.Zero ? string.Empty : Marshal.PtrToStringAnsi(ptr) ?? string.Empty;
    }

    public static int GetRuleCountForTarget(string typeName, string methodName, int? paramCount = null)
    {
        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(methodName))
            return 0;

        return GetRuleCountForTargetNative(typeName, methodName, paramCount ?? -1);
    }

    public static int GetRuleCountForModTarget(
        string modId,
        string typeName,
        string methodName,
        int? paramCount = null)
    {
        if (string.IsNullOrWhiteSpace(modId) ||
            string.IsNullOrWhiteSpace(typeName) ||
            string.IsNullOrWhiteSpace(methodName))
            return 0;

        return GetRuleCountForModTargetNative(
            modId,
            typeName,
            methodName,
            paramCount ?? -1);
    }

    public static bool TryResolvePendingSlots()
        => ResolvePendingSlots() >= 0 && GetFailedSlotCount() == 0;

    public static int ResolvePendingSlots()
    {
        try
        {
            var result = ResolvePendingSlotsNative();
            Logger.Info(LogTag, $"resolve slots result={result} merged={GetMergedSlotCount()} pending={GetPendingSlotCount()} resolved={GetResolvedSlotCount()} failed={GetFailedSlotCount()} installable={GetInstallableSlotCount()} slotRules={GetSlotRuleCount()} enabledRules={GetEnabledSlotRuleCount()} disabledRules={GetDisabledSlotRuleCount()}");
            return result;
        }
        catch (Exception ex)
        {
            Logger.Warn(LogTag, $"resolve slots threw: {ex.Message}");
            return -1;
        }
    }

    public static int PrepareInstallPlan()
    {
        try
        {
            var planned = PrepareInstallPlanNative();
            Logger.Info(LogTag, $"install plan planned={planned} installable={GetInstallableSlotCount()} blocked={GetInstallBlockedSlotCount()} pending={GetPendingSlotCount()} resolved={GetResolvedSlotCount()} installed={GetInstalledSlotCount()} required={GetDispatcherRequiredCount()} capacity={GetDispatcherCapacity()} bound={GetBoundDispatcherCount()} new={GetDispatcherNewCount()} allocated={GetDispatcherAllocatedCount()} remaining={GetDispatcherRemainingCount()} dispatcherBlocked={GetDispatcherBlockedCount()} dispatcherReady={GetDispatcherReadySlotCount()}");
            Logger.Info(LogTag, $"slot summary: {GetSlotSummary()}");
            return planned;
        }
        catch (Exception ex)
        {
            Logger.Warn(LogTag, $"prepare install plan threw: {ex.Message}");
            return -1;
        }
    }

    public static int InstallPlannedSlots()
    {
        try
        {
            var installed = InstallPlannedSlotsNative();
            Logger.Info(LogTag, $"install planned slots result={installed} installed={GetInstalledSlotCount()} installable={GetInstallableSlotCount()} blocked={GetInstallBlockedSlotCount()} required={GetDispatcherRequiredCount()} capacity={GetDispatcherCapacity()} bound={GetBoundDispatcherCount()} new={GetDispatcherNewCount()} allocated={GetDispatcherAllocatedCount()} remaining={GetDispatcherRemainingCount()} dispatcherBlocked={GetDispatcherBlockedCount()} dispatcherReady={GetDispatcherReadySlotCount()}");
            Logger.Info(LogTag, $"slot summary: {GetSlotSummary()}");
            return installed;
        }
        catch (Exception ex)
        {
            Logger.Warn(LogTag, $"install planned slots threw: {ex.Message}");
            return -1;
        }
    }

    public static string GetSlotSummary()
    {
        var ptr = GetSlotSummaryNative();
        return ptr == nint.Zero ? string.Empty : Marshal.PtrToStringAnsi(ptr) ?? string.Empty;
    }

    public static string GetSlotSummaryForMod(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
            return GetSlotSummary();
        var ptr = GetSlotSummaryForModNative(modId);
        return ptr == nint.Zero ? string.Empty : Marshal.PtrToStringAnsi(ptr) ?? string.Empty;
    }

    public static PcCompatDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        var presentation = GetPresentationSnapshot();
        var presentationSink = GetPresentationSinkStats();
        return new()
        {
            ProviderAvailable = true,
            LoadedBundles = GetLoadedBundleCount(),
            LoadedTargets = GetLoadedTargetCount(),
            LoadedRules = GetLoadedRuleCount(),
            LoadedUiLifecyclePrograms = GetLoadedUiLifecycleProgramCount(),
            LoadedUiObjectNodes = GetLoadedUiObjectNodeCount(),
            LoadedUiComponentOps = GetLoadedUiComponentOpCount(),
            LoadedUiResourceBindings = GetLoadedUiResourceBindingCount(),
            LoadedUiBytecodeInstructions = GetLoadedUiBytecodeInstructionCount(),
            Presentation = presentation,
            PresentationSink = presentationSink,
            MergedSlots = GetMergedSlotCount(),
            PendingSlots = GetPendingSlotCount(),
            ResolvedSlots = GetResolvedSlotCount(),
            FailedSlots = GetFailedSlotCount(),
            InstallableSlots = GetInstallableSlotCount(),
            InstallBlockedSlots = GetInstallBlockedSlotCount(),
            InstalledSlots = GetInstalledSlotCount(),
            DispatcherReadySlots = GetDispatcherReadySlotCount(),
            BoundDispatcherSlots = GetBoundDispatcherCount(),
            DispatcherCapacity = GetDispatcherCapacity(),
            DispatcherRequiredSlots = GetDispatcherRequiredCount(),
            DispatcherNewSlots = GetDispatcherNewCount(),
            DispatcherAllocatedSlots = GetDispatcherAllocatedCount(),
            DispatcherRemainingSlots = GetDispatcherRemainingCount(),
            DispatcherBlockedSlots = GetDispatcherBlockedCount(),
            SlotRules = GetSlotRuleCount(),
            EnabledSlotRules = GetEnabledSlotRuleCount(),
            DisabledSlotRules = GetDisabledSlotRuleCount(),
            ApprovedCapabilities = GetApprovedCapabilities(),
            SlotSummary = GetSlotSummary(),
            LastError = GetLastError(),
            LatestVmFault = GetLatestVmFault()
        };
    }

    public static PcCompatOverlaySnapshot GetOverlaySnapshot()
        => GetOverlaySnapshotCore(null);

    // Reverse-patch state is official game telemetry, not an owner HUD projection.
    // It remains readable when the consumer has no visible overlay of its own.
    public static PcCompatOverlaySnapshot GetSharedGameSnapshot()
        => GetOverlaySnapshotCore(null, sharedGameSnapshot: true);

    public static PcCompatOverlaySnapshot GetOverlaySnapshot(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        return GetOverlaySnapshotCore(modId);
    }

    private static PcCompatOverlaySnapshot GetOverlaySnapshotCore(
        string? modId,
        bool sharedGameSnapshot = false)
    {
        if (sharedGameSnapshot && modId != null)
            throw new ArgumentException("A shared game snapshot cannot be scoped to a MOD.", nameof(modId));

        var cached = sharedGameSnapshot
            ? Volatile.Read(ref s_cachedSharedGameSnapshot)
            : modId == null
            ? Volatile.Read(ref s_cachedOverlaySnapshot)
            : CachedOverlaySnapshots.GetValueOrDefault(
                modId,
                PcCompatOverlaySnapshot.Unavailable);
        var native = new OverlaySnapshotNative
        {
            StructSize = OverlaySnapshotNativeSize,
            AbiVersion = OverlaySnapshotAbiVersion,
            Generation = cached.ProviderAvailable ? cached.Generation : 0
        };
        var readResult = sharedGameSnapshot
            ? ReadSharedGameSnapshotNative(ref native, OverlaySnapshotNativeSize)
            : modId == null
            ? ReadOverlaySnapshotNative(ref native, OverlaySnapshotNativeSize)
            : ReadOverlaySnapshotForModNative(modId, ref native, OverlaySnapshotNativeSize);
        if (readResult == 0 && cached.ProviderAvailable)
            return cached;
        if (readResult < 0 ||
            native.StructSize != OverlaySnapshotNativeSize ||
            native.AbiVersion != OverlaySnapshotAbiVersion)
        {
            if (modId != null)
                CachedOverlaySnapshots.TryRemove(modId, out _);
            return PcCompatOverlaySnapshot.Unavailable;
        }

        if (cached.ProviderAvailable && cached.Generation == native.Generation)
            return cached;

        var snapshot = new PcCompatOverlaySnapshot
        {
            ProviderAvailable = true,
            Generation = native.Generation,
            HasExplicitGameSnapshotValidity = true,
            ValidGameSnapshotFields =
                (PcCompatGameSnapshotFields)native.ValidGameSnapshotFields,
            ControllerPointer = checked((long)native.ControllerPointer),
            ConductorPointer = checked((long)native.ConductorPointer),
            LevelMakerPointer = checked((long)native.LevelMakerPointer),
            CurrentFloorPointer = checked((long)native.CurrentFloorPointer),
            FirstFloorPointer = checked((long)native.FirstFloorPointer),
            SongPointer = checked((long)native.SongPointer),
            PlanetarySystemPointer = checked((long)native.PlanetarySystemPointer),
            Visible = native.Visible != 0,
            Practice = native.Practice != 0,
            ShowCount = native.ShowCount,
            HideCount = native.HideCount,
            PlayerUpdateCount = native.PlayerUpdateCount,
            StateChangeCount = native.StateChangeCount,
            LastOpCode = native.LastOpCode,
            LastTargetKind = native.LastTargetKind,
            PlayerCount = native.PlayerCount,
            LastSeqId = native.LastSeqId,
            LastIsRestart = native.LastIsRestart != 0,
            LastWipeDirection = native.LastWipeDirection,
            LastResetToEditor = native.LastResetToEditor != 0,
            JudgementHitCount = native.JudgementHitCount,
            JudgementResetCount = native.JudgementResetCount,
            LastHitMargin = native.LastHitMargin,
            FloorMoveCount = native.FloorMoveCount,
            LastFloorExitAngle = native.LastFloorExitAngle,
            LastFloorMoveHitMargin = native.LastFloorMoveHitMargin,
            PlayerHitCount = native.PlayerHitCount,
            LastPlayerHitIsAuto = native.LastPlayerHitIsAuto != 0,
            DeathCount = native.DeathCount,
            LastDeathOverload = native.LastDeathOverload != 0,
            LastDeathMultipress = native.LastDeathMultipress != 0,
            LastDeathHitbox = native.LastDeathHitbox != 0,
            HitTimingCount = native.HitTimingCount,
            LastHitTimingMs = native.LastHitTimingMs,
            LastHitTimingMargin = native.LastHitTimingMargin,
            AccuracySnapshotCount = native.AccuracySnapshotCount,
            PercentAcc = native.PercentAcc,
            PercentXAcc = native.PercentXAcc,
            Progress = native.Progress,
            ComboCount = native.ComboCount,
            AttemptCount = native.AttemptCount,
            BpmSnapshotCount = native.BpmSnapshotCount,
            TileBpm = native.TileBpm,
            Kps = native.Kps,
            TimelineSnapshotCount = native.TimelineSnapshotCount,
            MusicTime = native.MusicTime,
            MusicTotalTime = native.MusicTotalTime,
            MapTime = native.MapTime,
            MapTotalTime = native.MapTotalTime,
            CheckpointsUsed = native.CheckpointsUsed,
            CurrentCheckpoint = native.CurrentCheckpoint,
            TotalCheckpoints = native.TotalCheckpoints,
            CurrentSeqId = native.CurrentSeqId,
            FloorCount = native.FloorCount,
            StartProgress = native.StartProgress,
            SpeedMultiplier = native.SpeedMultiplier,
            SessionAuto = native.SessionAuto != 0,
            IsAuto = native.RdcAuto != 0,
            IsNoFail = native.NoFail != 0,
            IsPaused = native.Paused != 0,
            IsGameWorld = native.IsGameWorld != 0,
            IsScnGame = native.IsScnGame != 0,
            IsGameReady = native.IsGameReady != 0,
            SongPitch = native.SongPitch,
            ConductorAddOffset = native.ConductorAddOffset,
            ConductorSongPositionMinusi = native.ConductorSongPositionMinusi,
            InputStateGeneration = native.InputStateGeneration,
            InputHeldMask = native.InputHeldMask,
            InputLastDownMask = native.InputLastDownMask,
            InputLastUpMask = native.InputLastUpMask,
            InputTotalCount = native.InputTotalCount,
            InputKps = native.InputKps,
            PlanetSpeed = native.PlanetSpeed,
            SessionEpoch = native.SessionEpoch
        };
        if (sharedGameSnapshot)
            Volatile.Write(ref s_cachedSharedGameSnapshot, snapshot);
        else if (modId == null)
            Volatile.Write(ref s_cachedOverlaySnapshot, snapshot);
        else
            CachedOverlaySnapshots[modId] = snapshot;
        return snapshot;
    }

    public static PcCompatInputHudSnapshot GetInputHudSnapshot(int touchLaneCount)
    {
        if (touchLaneCount is not (2 or 4 or 6 or 8 or 10))
            touchLaneCount = 10;

        lock (InputHudSnapshotLock)
        {
            if (!CachedInputHudSnapshots.TryGetValue(touchLaneCount, out var cached))
                cached = PcCompatInputHudSnapshot.Unavailable;

            var native = new InputHudSnapshotNative
            {
                StructSize = InputHudSnapshotNativeSize,
                AbiVersion = InputHudSnapshotAbiVersion,
                PublicationGeneration = cached.ProviderAvailable
                    ? cached.PublicationGeneration
                    : 0,
                TouchLaneCount = checked((uint)touchLaneCount)
            };
            var readResult = ReadInputHudSnapshotNative(ref native, InputHudSnapshotNativeSize);
            if (readResult == 0 && cached.ProviderAvailable)
                return cached;
            if (readResult <= 0 ||
                native.StructSize != InputHudSnapshotNativeSize ||
                native.AbiVersion != InputHudSnapshotAbiVersion ||
                native.TouchLaneCount != touchLaneCount)
                return PcCompatInputHudSnapshot.Unavailable;

            var heldCounts = new ushort[touchLaneCount];
            var totalCounts = new uint[touchLaneCount];
            var lastDownRawNs = new long[touchLaneCount];
            var lastUpRawNs = new long[touchLaneCount];
            for (var index = 0; index < touchLaneCount; ++index)
            {
                heldCounts[index] = native.TouchLaneHeldCounts[index];
                totalCounts[index] = native.TouchLaneTotalCounts[index];
                lastDownRawNs[index] = native.TouchLaneLastDownRawNs[index];
                lastUpRawNs[index] = native.TouchLaneLastUpRawNs[index];
            }

            var snapshot = new PcCompatInputHudSnapshot
            {
                ProviderAvailable = true,
                PublicationGeneration = native.PublicationGeneration,
                SessionGeneration = native.SessionGeneration,
                SourceGeneration = native.SourceGeneration,
                TouchLaneCount = checked((int)native.TouchLaneCount),
                TouchLaneHeldMask = native.TouchLaneHeldMask,
                TouchLaneLastDownMask = native.TouchLaneLastDownMask,
                TouchLaneLastUpMask = native.TouchLaneLastUpMask,
                InputTotalCount = native.InputTotalCount,
                KeyboardHeldCount = native.KeyboardHeldCount,
                InputKps = native.InputKps,
                SourceSequence = native.SourceSequence,
                DroppedEventCount = native.DroppedEventCount,
                CompletedRawNs = native.CompletedRawNs,
                SessionAnchorRawNs = native.SessionAnchorRawNs,
                TouchLaneHeldCounts = heldCounts,
                TouchLaneTotalCounts = totalCounts,
                TouchLaneLastDownRawNs = lastDownRawNs,
                TouchLaneLastUpRawNs = lastUpRawNs
            };
            CachedInputHudSnapshots[touchLaneCount] = snapshot;
            return snapshot;
        }
    }

    public static PcCompatKeyViewerInputOrigin GetInputOrigin()
        => GetActiveInputProducerNative() switch
        {
            2 => PcCompatKeyViewerInputOrigin.AsyncInput,
            1 => PcCompatKeyViewerInputOrigin.OfficialActivity,
            _ => PcCompatKeyViewerInputOrigin.Unavailable
        };

    public static bool TrySetTouchLaneMappingMode(PcCompatTouchLaneMappingMode mode)
    {
        mode = PcCompatTouchLaneMappingRuntime.Normalize(mode);
        try
        {
            return SetTouchLaneMappingModeNative((int)mode) == 1;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    public static bool TrySetTouchContactReuseDelayMilliseconds(int milliseconds)
    {
        milliseconds = PcCompatTouchLaneMappingRuntime
            .NormalizeTouchContactReuseDelayMilliseconds(milliseconds);
        try
        {
            return SetTouchContactReuseDelayMillisecondsNative(milliseconds) == 1;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    public static PcCompatKeyViewerEventBatch ReadRawInputEvents(
        ulong cursor,
        int capacity)
    {
        capacity = Math.Clamp(capacity, 1, 256);
        var nativeEvents = s_rawInputEventBuffer ??= new RawInputEventNative[256];
        var header = new RawInputReadNative
        {
            StructSize = RawInputReadNativeSize,
            AbiVersion = RawInputReadAbiVersion,
            Cursor = cursor,
            Capacity = checked((uint)capacity)
        };
        var result = ReadRawInputEventsNative(
            ref header,
            RawInputReadNativeSize,
            nativeEvents,
            RawInputEventNativeSize,
            checked((uint)capacity));
        if (result <= 0 ||
            header.StructSize != RawInputReadNativeSize ||
            header.AbiVersion != RawInputReadAbiVersion ||
            header.Count > capacity)
            return PcCompatKeyViewerEventBatch.Unavailable;

        var events = header.Count == 0
            ? Array.Empty<PcCompatKeyViewerRawEvent>()
            : new PcCompatKeyViewerRawEvent[header.Count];
        for (var index = 0; index < events.Length; ++index)
        {
            var source = nativeEvents[index];
            events[index] = new PcCompatKeyViewerRawEvent(
                source.Sequence,
                source.RawNs,
                source.StateGeneration,
                source.SessionGeneration,
                source.ProducerEpoch,
                source.Producer switch
                {
                    2 => PcCompatKeyViewerInputOrigin.AsyncInput,
                    1 => PcCompatKeyViewerInputOrigin.OfficialActivity,
                    _ => PcCompatKeyViewerInputOrigin.Unavailable
                },
                (PcCompatKeyViewerRawSource)source.Source,
                (PcCompatKeyViewerRawPhase)source.Phase,
                source.Code,
                source.Slot,
                source.PointerCount,
                source.ScanCode,
                source.MetaState,
                source.DeviceId,
                source.RepeatCount,
                source.AndroidFlags,
                source.SourceCode,
                source.ViewportWidth,
                source.ViewportHeight,
                source.X,
                source.Y,
                source.Flags);
        }
        return new PcCompatKeyViewerEventBatch
        {
            ProviderAvailable = true,
            Cursor = header.Cursor,
            DroppedBeforeCursor = header.DroppedBeforeCursor,
            Events = events
        };
    }

    public static PcCompatExternalInputDeviceSnapshot GetExternalInputDevices()
    {
        var native = new ExternalInputDeviceSnapshotNative
        {
            AbiVersion = ExternalInputDeviceSnapshotAbiVersion,
            StructSize = ExternalInputDeviceSnapshotNativeSize
        };
        if (ReadExternalInputDevicesNative(
                ref native,
                ExternalInputDeviceSnapshotNativeSize) <= 0 ||
            native.AbiVersion != ExternalInputDeviceSnapshotAbiVersion ||
            native.StructSize != ExternalInputDeviceSnapshotNativeSize)
        {
            return PcCompatExternalInputDeviceSnapshot.Unavailable;
        }
        const PcCompatExternalInputDeviceFlags known =
            PcCompatExternalInputDeviceFlags.Keyboard |
            PcCompatExternalInputDeviceFlags.Controller |
            PcCompatExternalInputDeviceFlags.Mouse;
        return new PcCompatExternalInputDeviceSnapshot(
            true,
            native.Generation,
            (PcCompatExternalInputDeviceFlags)native.Flags & known);
    }

    public static bool WaitForRawInputChange(ulong cursor, int timeoutMilliseconds)
        => WaitRawInputChangeNative(cursor, Math.Clamp(timeoutMilliseconds, 1, 1000)) > 0;

    public static void InterruptRawInputWait()
        => InterruptRawInputWaitNative();

    public static PcCompatClockAnchorSnapshot GetClockAnchorSnapshot()
    {
        var cached = Volatile.Read(ref s_cachedClockAnchor);
        var native = new ClockAnchorSnapshotNative
        {
            StructSize = ClockAnchorSnapshotNativeSize,
            AbiVersion = ClockAnchorSnapshotAbiVersion,
            PublicationGeneration = cached.ProviderAvailable
                ? cached.PublicationGeneration
                : 0
        };
        var readResult = ReadClockAnchorSnapshotNative(ref native, ClockAnchorSnapshotNativeSize);
        if (readResult == 0 && cached.ProviderAvailable)
            return cached;
        if (readResult <= 0 ||
            native.StructSize != ClockAnchorSnapshotNativeSize ||
            native.AbiVersion != ClockAnchorSnapshotAbiVersion)
            return PcCompatClockAnchorSnapshot.Unavailable;

        var snapshot = new PcCompatClockAnchorSnapshot
        {
            ProviderAvailable = true,
            PublicationGeneration = native.PublicationGeneration,
            SessionGeneration = native.SessionGeneration,
            ValidMask = native.ValidMask,
            FrameCount = native.FrameCount,
            UnityTimeScale = native.UnityTimeScale,
            AudioPositionSeconds = native.AudioPositionSeconds,
            UnityScaledSeconds = native.UnityScaledSeconds,
            SongPositionSeconds = native.SongPositionSeconds,
            MapPositionSeconds = native.MapPositionSeconds,
            MonotonicRawNs = native.MonotonicRawNs
        };
        Volatile.Write(ref s_cachedClockAnchor, snapshot);
        return snapshot;
    }

    public static PcCompatMonotonicClockSnapshot GetMonotonicClockSnapshot()
    {
        var native = new ClockAnchorSnapshotNative
        {
            StructSize = ClockAnchorSnapshotNativeSize,
            AbiVersion = ClockAnchorSnapshotAbiVersion,
            PublicationGeneration = Volatile.Read(ref s_cachedMonotonicClockGeneration)
        };
        var readResult = ReadClockAnchorSnapshotNative(ref native, ClockAnchorSnapshotNativeSize);
        if (readResult == 0 && Volatile.Read(ref s_cachedMonotonicAvailable) != 0)
        {
            return new PcCompatMonotonicClockSnapshot(
                true,
                Interlocked.Read(ref s_cachedMonotonicRawNs));
        }
        if (readResult <= 0 ||
            native.StructSize != ClockAnchorSnapshotNativeSize ||
            native.AbiVersion != ClockAnchorSnapshotAbiVersion)
        {
            return default;
        }
        Interlocked.Exchange(ref s_cachedMonotonicRawNs, native.MonotonicRawNs);
        Volatile.Write(ref s_cachedMonotonicClockGeneration, native.PublicationGeneration);
        Volatile.Write(ref s_cachedMonotonicAvailable, 1);
        return new PcCompatMonotonicClockSnapshot(true, native.MonotonicRawNs);
    }

    public static PcCompatPresentationSnapshot GetPresentationSnapshot()
    {
        lock (PresentationSnapshotLock)
        {
            var cached = s_cachedPresentationSnapshot;
            var native = new PresentationSnapshotNative
            {
                StructSize = PresentationSnapshotNativeSize,
                AbiVersion = PresentationSnapshotAbiVersion,
                PublicationGeneration = cached.PublicationGeneration
            };
            var readResult = ReadPresentationSnapshotNative(
                ref native,
                PresentationSnapshotNativeSize);
            if (readResult == 0)
                return cached;
            if (readResult < 0 ||
                native.StructSize != PresentationSnapshotNativeSize ||
                native.AbiVersion != PresentationSnapshotAbiVersion)
                return PcCompatPresentationSnapshot.Unavailable;

            if (native.Available == 0)
            {
                var unavailable = new PcCompatPresentationSnapshot
                {
                    ProviderAvailable = false,
                    PublicationGeneration = native.PublicationGeneration
                };
                s_cachedPresentationSnapshot = unavailable;
                return unavailable;
            }

            var count = checked((int)Math.Min(
                native.CommandCount,
                (uint)PresentationMaxCommands));
            var commands = new List<PcCompatPresentationCommand>(count);
            unsafe
            {
                byte* commandBytes = native.Commands;
                for (var index = 0; index < count; ++index)
                {
                    var command = Marshal.PtrToStructure<PresentationCommandNative>(
                        (nint)(commandBytes + index * PresentationCommandNativeSize));
                    commands.Add(new PcCompatPresentationCommand
                    {
                        Sequence = command.Sequence,
                        SessionGeneration = command.SessionGeneration,
                        Generation = command.Generation,
                            RuleId = command.RuleId,
                            CommandType = command.CommandType,
                            TargetId = command.TargetId,
                            Payload0 = command.Payload0,
                        Payload1 = command.Payload1,
                        Value0 = command.Value0,
                        Value1 = command.Value1
                    });
                }
            }

            var snapshot = new PcCompatPresentationSnapshot
            {
                ProviderAvailable = true,
                PublicationGeneration = native.PublicationGeneration,
                SessionGeneration = native.SessionGeneration,
                DroppedStaleTasks = native.DroppedStaleTasks,
                SchedulerOverflowCount = native.SchedulerOverflowCount,
                PublishedRawNs = native.PublishedRawNs,
                Commands = commands
            };
            s_cachedPresentationSnapshot = snapshot;
            return snapshot;
        }
    }

    public static PcCompatPresentationSinkStats GetPresentationSinkStats()
    {
        var native = new PresentationSinkStatsNative
        {
            StructSize = PresentationSinkStatsNativeSize,
            AbiVersion = PresentationSinkStatsAbiVersion
        };
        var readResult = ReadPresentationSinkStatsNative(
            ref native,
            PresentationSinkStatsNativeSize);
        if (readResult <= 0 ||
            native.StructSize != PresentationSinkStatsNativeSize ||
            native.AbiVersion != PresentationSinkStatsAbiVersion)
            return PcCompatPresentationSinkStats.Unavailable;

        return new PcCompatPresentationSinkStats
        {
            ProviderAvailable = true,
            Installed = native.Installed != 0,
            PrimaryHook = native.PrimaryHook != 0,
            FallbackHook = native.FallbackHook != 0,
            ConsumeOpportunities = native.ConsumeOpportunities,
            SnapshotUpdates = native.SnapshotUpdates,
            CommandCount = native.CommandCount,
            UnsupportedCommandCount = native.UnsupportedCommandCount,
            LastPublicationGeneration = native.LastPublicationGeneration,
            LastSessionGeneration = native.LastSessionGeneration,
            RegisteredGraphCount = native.RegisteredGraphCount,
            MaterializedGraphCount = native.MaterializedGraphCount,
            GraphMaterializationFailures = native.GraphMaterializationFailures,
            InvalidTargetCount = native.InvalidTargetCount,
            RetiredGraphCount = native.RetiredGraphCount,
            PresentationHistoryOverflowCount = native.PresentationHistoryOverflowCount,
            StreamGapCount = native.StreamGapCount,
            StreamFaulted = native.StreamFaulted != 0,
            OnGUIHook = native.OnGUIHook != 0,
            OnGUIProcessHook = native.OnGUIProcessHook != 0,
            OnGUIBeginHook = native.OnGUIBeginHook != 0,
            OnGUIEnabled = native.OnGUIEnabled != 0,
            OnGUIProcessEventCount = native.OnGUIProcessEventCount,
            OnGUIBeginGUICount = native.OnGUIBeginGUICount,
            OnGUIDispatchCount = native.OnGUIDispatchCount
        };
    }

    public static PcCompatVmFaultSnapshot GetLatestVmFault()
    {
        lock (VmFaultLock)
        {
            ulong droppedBeforeCursor = 0;
            for (var attempt = 0; attempt < 64; ++attempt)
            {
                var native = new VmFaultSnapshotNative
                {
                    StructSize = VmFaultSnapshotNativeSize,
                    AbiVersion = VmFaultSnapshotAbiVersion,
                    Cursor = s_vmFaultCursor
                };
                var readResult = ReadVmFaultSnapshotNative(ref native, VmFaultSnapshotNativeSize);
                if (readResult <= 0)
                    break;
                if (native.StructSize != VmFaultSnapshotNativeSize ||
                    native.AbiVersion != VmFaultSnapshotAbiVersion)
                    break;

                var messageLength = 0;
                while (messageLength < 160 && native.Message[messageLength] != 0)
                    ++messageLength;
                var messageBytes = new byte[messageLength];
                for (var index = 0; index < messageLength; ++index)
                    messageBytes[index] = native.Message[index];
                var message = Encoding.UTF8.GetString(messageBytes);

                s_vmFaultCursor = native.Cursor;
                droppedBeforeCursor += native.DroppedBeforeCursor;
                s_latestVmFault = new PcCompatVmFaultSnapshot
                {
                    ProviderAvailable = true,
                    Cursor = native.Cursor,
                    Sequence = native.Sequence,
                    TimestampNs = native.TimestampNs,
                    RuleId = native.RuleId,
                    Code = native.Code,
                    Pc = native.Pc,
                    Opcode = native.Opcode,
                    Count = native.Count,
                    DroppedBeforeCursor = droppedBeforeCursor,
                    Message = message
                };
            }
            return s_latestVmFault;
        }
    }

    public static bool GetOverlayVisible()
        => GetOverlayVisibleNative() != 0;

    public static void SetResourceChangerState(PcCompatResourceChangerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        SetResourceChangerSettingsNative(
            state.ModId,
            state.SessionGeneration,
            state.ChangeRabbit ? 1 : 0,
            state.ChangeBallColor ? 1 : 0,
            state.ChangeTileColor ? 1 : 0,
            state.PlanetColor.R,
            state.PlanetColor.G,
            state.PlanetColor.B,
            state.PlanetColor.A,
            state.TitleColor.R,
            state.TitleColor.G,
            state.TitleColor.B,
            state.TitleColor.A,
            state.TileColor.R,
            state.TileColor.G,
            state.TileColor.B,
            state.TileColor.A,
            state.ResourcePackName);
    }

    public static int PublishResourceChangerSprite(
        string modId,
        long sessionGeneration,
        nint sprite)
        => PublishResourceChangerSpriteNative(modId, sessionGeneration, sprite);

    public static int RetireResourceChangerSprite(string modId, long sessionGeneration)
        => RetireResourceChangerSpriteNative(modId, sessionGeneration);

    public static int ApplyPendingResourceChangerState()
        => ApplyPendingResourceChangerStateNative();

    public static string GetLevelIdentity()
    {
        var ptr = GetLevelIdentityNative();
        return ptr == nint.Zero ? string.Empty : Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
    }
}

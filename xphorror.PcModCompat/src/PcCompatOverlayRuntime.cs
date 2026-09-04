using System.Threading;

namespace Xphorror.PcModCompat;

public sealed class PcCompatInputHudSnapshot
{
    public static PcCompatInputHudSnapshot Unavailable { get; } = new();

    public bool ProviderAvailable { get; init; }
    public uint PublicationGeneration { get; init; }
    public uint SessionGeneration { get; init; }
    public uint SourceGeneration { get; init; }
    public int TouchLaneCount { get; init; }
    public uint TouchLaneHeldMask { get; init; }
    public uint TouchLaneLastDownMask { get; init; }
    public uint TouchLaneLastUpMask { get; init; }
    public uint InputTotalCount { get; init; }
    public uint KeyboardHeldCount { get; init; }
    public float InputKps { get; init; }
    public ulong SourceSequence { get; init; }
    public ulong DroppedEventCount { get; init; }
    public long CompletedRawNs { get; init; }
    public long SessionAnchorRawNs { get; init; }
    public IReadOnlyList<ushort> TouchLaneHeldCounts { get; init; } = Array.Empty<ushort>();
    public IReadOnlyList<uint> TouchLaneTotalCounts { get; init; } = Array.Empty<uint>();
    public IReadOnlyList<long> TouchLaneLastDownRawNs { get; init; } = Array.Empty<long>();
    public IReadOnlyList<long> TouchLaneLastUpRawNs { get; init; } = Array.Empty<long>();
}

public static class PcCompatInputHudRuntime
{
    private static Func<int, PcCompatInputHudSnapshot>? s_provider;

    public static void RegisterProvider(Func<int, PcCompatInputHudSnapshot> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Volatile.Write(ref s_provider, provider);
    }

    public static void ClearProvider()
        => Volatile.Write(ref s_provider, null);

    public static PcCompatInputHudSnapshot Snapshot(int touchLaneCount)
    {
        var provider = Volatile.Read(ref s_provider);
        if (provider == null)
            return PcCompatInputHudSnapshot.Unavailable;

        try
        {
            return provider(touchLaneCount) ?? PcCompatInputHudSnapshot.Unavailable;
        }
        catch
        {
            return PcCompatInputHudSnapshot.Unavailable;
        }
    }
}

public static class PcCompatInputOriginRuntime
{
    private static Func<PcCompatKeyViewerInputOrigin>? s_provider;

    public static void RegisterProvider(Func<PcCompatKeyViewerInputOrigin> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Volatile.Write(ref s_provider, provider);
    }

    public static void ClearProvider()
        => Volatile.Write(ref s_provider, null);

    public static PcCompatKeyViewerInputOrigin GetCurrent()
    {
        var provider = Volatile.Read(ref s_provider);
        if (provider == null)
            return PcCompatKeyViewerInputOrigin.Unavailable;

        try
        {
            return provider();
        }
        catch
        {
            return PcCompatKeyViewerInputOrigin.Unavailable;
        }
    }
}

public sealed class PcCompatClockAnchorSnapshot
{
    public const uint UnityScaledValid = 1u << 0;
    public const uint SongPositionValid = 1u << 1;
    public const uint AudioPositionValid = 1u << 2;
    public const uint MapPositionValid = 1u << 3;
    public const uint FrameCountValid = 1u << 4;

    public static PcCompatClockAnchorSnapshot Unavailable { get; } = new();

    public bool ProviderAvailable { get; init; }
    public uint PublicationGeneration { get; init; }
    public uint SessionGeneration { get; init; }
    public uint ValidMask { get; init; }
    public int FrameCount { get; init; }
    public float UnityTimeScale { get; init; } = 1f;
    public float AudioPositionSeconds { get; init; }
    public double UnityScaledSeconds { get; init; }
    public double SongPositionSeconds { get; init; }
    public double MapPositionSeconds { get; init; }
    public long MonotonicRawNs { get; init; }
}

public readonly record struct PcCompatMonotonicClockSnapshot(
    bool ProviderAvailable,
    long MonotonicRawNs);

public static class PcCompatClockAnchorRuntime
{
    private static Func<PcCompatClockAnchorSnapshot>? s_provider;
    private static Func<PcCompatMonotonicClockSnapshot>? s_monotonicProvider;

    public static void RegisterProvider(Func<PcCompatClockAnchorSnapshot> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Volatile.Write(ref s_provider, provider);
    }

    public static void RegisterMonotonicProvider(
        Func<PcCompatMonotonicClockSnapshot> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Volatile.Write(ref s_monotonicProvider, provider);
    }

    public static void ClearProvider()
    {
        Volatile.Write(ref s_provider, null);
        Volatile.Write(ref s_monotonicProvider, null);
    }

    public static PcCompatClockAnchorSnapshot Snapshot()
    {
        var provider = Volatile.Read(ref s_provider);
        if (provider == null)
            return PcCompatClockAnchorSnapshot.Unavailable;

        try
        {
            return provider() ?? PcCompatClockAnchorSnapshot.Unavailable;
        }
        catch
        {
            return PcCompatClockAnchorSnapshot.Unavailable;
        }
    }

    public static PcCompatMonotonicClockSnapshot MonotonicSnapshot()
    {
        var provider = Volatile.Read(ref s_monotonicProvider);
        if (provider != null)
        {
            try
            {
                return provider();
            }
            catch
            {
                return default;
            }
        }
        var snapshot = Snapshot();
        return new PcCompatMonotonicClockSnapshot(
            snapshot.ProviderAvailable,
            snapshot.MonotonicRawNs);
    }
}

public sealed class PcCompatOverlaySnapshot
{
    public static PcCompatOverlaySnapshot Unavailable { get; } = new();

    public bool ProviderAvailable { get; init; }
    public uint Generation { get; init; }
    public uint SessionEpoch { get; init; }
    public bool HasExplicitGameSnapshotValidity { get; init; }
    public PcCompatGameSnapshotFields ValidGameSnapshotFields { get; init; }
    public long ControllerPointer { get; init; }
    public long ConductorPointer { get; init; }
    public long LevelMakerPointer { get; init; }
    public long CurrentFloorPointer { get; init; }
    public long FirstFloorPointer { get; init; }
    public long SongPointer { get; init; }
    public long PlanetarySystemPointer { get; init; }
    public bool Visible { get; init; }
    public bool Practice { get; init; }
    public uint ShowCount { get; init; }
    public uint HideCount { get; init; }
    public uint PlayerUpdateCount { get; init; }
    public uint StateChangeCount { get; init; }
    public int LastOpCode { get; init; } = -1;
    public int LastTargetKind { get; init; }
    public int PlayerCount { get; init; }
    public int LastSeqId { get; init; }
    public bool LastIsRestart { get; init; }
    public int LastWipeDirection { get; init; }
    public bool LastResetToEditor { get; init; }
    public uint JudgementHitCount { get; init; }
    public uint JudgementResetCount { get; init; }
    public int LastHitMargin { get; init; }
    public uint FloorMoveCount { get; init; }
    public float LastFloorExitAngle { get; init; }
    public int LastFloorMoveHitMargin { get; init; }
    public uint PlayerHitCount { get; init; }
    public bool LastPlayerHitIsAuto { get; init; }
    public uint DeathCount { get; init; }
    public bool LastDeathOverload { get; init; }
    public bool LastDeathMultipress { get; init; }
    public bool LastDeathHitbox { get; init; }
    public uint HitTimingCount { get; init; }
    public float LastHitTimingMs { get; init; }
    public int LastHitTimingMargin { get; init; }
    public uint AccuracySnapshotCount { get; init; }
    public float PercentAcc { get; init; }
    public float PercentXAcc { get; init; }
    public float Progress { get; init; }
    public int ComboCount { get; init; }
    public uint AttemptCount { get; init; }
    public uint BpmSnapshotCount { get; init; }
    public float TileBpm { get; init; }
    public float Kps { get; init; }
    public uint TimelineSnapshotCount { get; init; }
    public float MusicTime { get; init; }
    public float MusicTotalTime { get; init; }
    public float MapTime { get; init; }
    public float MapTotalTime { get; init; }
    public int CheckpointsUsed { get; init; }
    public int CurrentCheckpoint { get; init; }
    public int TotalCheckpoints { get; init; }
    public int CurrentSeqId { get; init; }
    public int FloorCount { get; init; }
    public float StartProgress { get; init; }
    public float SpeedMultiplier { get; init; } = 1f;
    public float PlanetSpeed { get; init; } = 1f;
    public bool SessionAuto { get; init; }
    public bool IsAuto { get; init; }
    public bool IsNoFail { get; init; }
    public bool IsPaused { get; init; }
    public bool IsGameWorld { get; init; }
    public bool IsScnGame { get; init; }
    public bool IsGameReady { get; init; }
    public float SongPitch { get; init; } = 1f;
    public double ConductorAddOffset { get; init; }
    public double ConductorSongPositionMinusi { get; init; }
    public uint InputStateGeneration { get; init; }
    public uint InputHeldMask { get; init; }
    public uint InputLastDownMask { get; init; }
    public uint InputLastUpMask { get; init; }
    public uint InputTotalCount { get; init; }
    public float InputKps { get; init; }
    public int InstalledSlots { get; init; }
    public int BoundDispatcherSlots { get; init; }
    public int DispatcherReadySlots { get; init; }
    public int DispatcherCapacity { get; init; }

    public string LastOpName => LastOpCode switch
    {
        0 => "OverlayShow",
        1 => "OverlayShowPractice",
        2 => "OverlayHandleStateChange",
        3 => "OverlayHide",
        4 => "OverlayUpdatePlayers",
        5 => "PublishMarginSnapshot",
        6 => "ResourceRedirect",
        7 => "OverlayRecordHit",
        8 => "OverlayResetJudgement",
        9 => "OverlayRecordFloorMove",
        10 => "OverlayRecordPlayerHit",
        11 => "OverlayRecordDeath",
        12 => "OverlayRecordHitTiming",
        13 => "ResourceApplyEditorRabbit",
        14 => "ResourceApplyFloorColor",
        15 => "ResourceApplyPlanetColor",
        16 => "ResourceSkipPlanetColorOriginal",
        17 => "ResourceOverridePlanetColorArg",
        18 => "ResourceSkipTileColorOriginal",
        19 => "ResourceApplyLogoText",
        20 => "OverlayPollTelemetry",
        _ => "None"
    };

    public string LastTargetName => LastTargetKind switch
    {
        1 => "scnGame.Play",
        2 => "scrPressToStart.ShowText",
        3 => "StateBehaviour.ChangeState",
        4 => "scrUIController.WipeToBlack",
        5 => "scnEditor.ResetScene",
        6 => "scrController.StartLoadingScene",
        7 => "scrMistakesManager.SetPlayerCount",
        8 => "scrMarginTracker.AddHit",
        9 => "scrMarginTracker.Reset",
        10 => "scrPlanet.MoveToNextFloor",
        11 => "scrPlayer.Hit",
        12 => "scrPlayer.Die",
        13 => "scrMisc.GetHitMargin",
        14 => "scrMarginTracker.CalculatePercentAcc",
        15 => "scrController.QuitToMainMenu",
        16 => "scnEditor.OttoUpdate",
        17 => "scrFloor.Start",
        18 => "scrPlanet.Start",
        19 => "scrLogoText.Awake",
        20 => "PlanetarySystem.RainbowMode",
        21 => "PlanetarySystem.EnbyMode",
        22 => "scrLogoText.UpdateColors",
        23 => "scrLogoText.LateUpdate",
        24 => "scrFloor.SetTileColor",
        25 => "PlanetRenderer.LoadPlanetColor",
        26 => "PlanetRenderer.SetRainbow",
        27 => "PlanetRenderer.SetColor",
        28 => "PlanetRenderer.SetColorArg",
        29 => "scrController.PlayerControl_Update",
        _ => "Unknown"
    };

    public bool AccuracyAvailable =>
        AccuracySnapshotCount > 0 &&
        float.IsFinite(PercentAcc) &&
        float.IsFinite(PercentXAcc);

    public string LastHitMarginName => FormatHitMargin(LastHitMargin);

    public string LastFloorMoveHitMarginName => FormatHitMargin(LastFloorMoveHitMargin);

    public string LastHitTimingMarginName => FormatHitMargin(LastHitTimingMargin);

    private static string FormatHitMargin(int hitMargin) => hitMargin switch
    {
        0 => "TooEarly",
        1 => "VeryEarly",
        2 => "EarlyPerfect",
        3 => "Perfect",
        4 => "LatePerfect",
        5 => "VeryLate",
        6 => "TooLate",
        7 => "Multipress",
        8 => "FailMiss",
        9 => "FailOverload",
        10 => "Auto",
        11 => "OverPress",
        _ => hitMargin.ToString()
    };
}

public static class PcCompatLevelIdentityRuntime
{
    private static Func<string>? s_provider;

    public static void RegisterProvider(Func<string> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Volatile.Write(ref s_provider, provider);
    }

    public static void ClearProvider()
        => Volatile.Write(ref s_provider, null);

    public static string GetCurrent()
    {
        var provider = Volatile.Read(ref s_provider);
        if (provider == null)
            return string.Empty;

        try
        {
            return provider() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

public static class PcCompatOverlayRuntime
{
    private sealed class OwnerProjection
    {
        public PcCompatOverlaySnapshot Snapshot { get; set; } = PcCompatOverlaySnapshot.Unavailable;
    }

    private sealed class ProviderRegistration(
        Func<PcCompatOverlaySnapshot> provider,
        Func<bool>? visibilityProvider,
        Func<string, PcCompatOverlaySnapshot>? ownerProvider)
    {
        public Func<PcCompatOverlaySnapshot> Provider { get; } = provider;
        public Func<bool>? VisibilityProvider { get; } = visibilityProvider;
        public Func<string, PcCompatOverlaySnapshot>? OwnerProvider { get; } = ownerProvider;
    }

    private static ProviderRegistration? s_registration;
    private static readonly object OwnerLock = new();
    private static readonly Dictionary<string, OwnerProjection> OwnerProjections =
        new(StringComparer.OrdinalIgnoreCase);

    public static void RegisterProvider(
        Func<PcCompatOverlaySnapshot> provider,
        Func<bool>? visibilityProvider = null,
        Func<string, PcCompatOverlaySnapshot>? ownerProvider = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Volatile.Write(
            ref s_registration,
            new ProviderRegistration(provider, visibilityProvider, ownerProvider));
    }

    public static void ClearProvider()
    {
        Volatile.Write(ref s_registration, null);
        lock (OwnerLock)
            OwnerProjections.Clear();
    }

    public static bool IsVisible()
    {
        var registration = Volatile.Read(ref s_registration);
        if (registration == null)
            return false;

        try
        {
            if (registration.VisibilityProvider != null)
                return registration.VisibilityProvider();
            return registration.Provider().Visible;
        }
        catch
        {
            return false;
        }
    }

    public static PcCompatOverlaySnapshot Snapshot()
    {
        var registration = Volatile.Read(ref s_registration);
        if (registration == null)
            return PcCompatOverlaySnapshot.Unavailable;

        try
        {
            return registration.Provider() ?? PcCompatOverlaySnapshot.Unavailable;
        }
        catch
        {
            return PcCompatOverlaySnapshot.Unavailable;
        }
    }

    public static PcCompatOverlaySnapshot Snapshot(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var registration = Volatile.Read(ref s_registration);
        if (registration == null)
            return PcCompatOverlaySnapshot.Unavailable;
        PcCompatOverlaySnapshot shared;
        try
        {
            shared = registration.OwnerProvider != null
                ? registration.OwnerProvider(ownerId) ?? PcCompatOverlaySnapshot.Unavailable
                : registration.Provider() ?? PcCompatOverlaySnapshot.Unavailable;
        }
        catch
        {
            shared = PcCompatOverlaySnapshot.Unavailable;
        }
        var isolated = shared.ProviderAvailable
            ? CloneSnapshot(shared)
            : PcCompatOverlaySnapshot.Unavailable;
        lock (OwnerLock)
        {
            if (!OwnerProjections.TryGetValue(ownerId, out var projection))
            {
                projection = new OwnerProjection();
                OwnerProjections.Add(ownerId, projection);
            }
            projection.Snapshot = isolated;
            return projection.Snapshot;
        }
    }

    public static void RemoveOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return;
        lock (OwnerLock)
            OwnerProjections.Remove(ownerId);
    }

    private static PcCompatOverlaySnapshot CloneSnapshot(PcCompatOverlaySnapshot value)
        => new()
        {
            ProviderAvailable = value.ProviderAvailable,
            Generation = value.Generation,
            SessionEpoch = value.SessionEpoch,
            HasExplicitGameSnapshotValidity = value.HasExplicitGameSnapshotValidity,
            ValidGameSnapshotFields = value.ValidGameSnapshotFields,
            ControllerPointer = value.ControllerPointer,
            ConductorPointer = value.ConductorPointer,
            LevelMakerPointer = value.LevelMakerPointer,
            CurrentFloorPointer = value.CurrentFloorPointer,
            FirstFloorPointer = value.FirstFloorPointer,
            SongPointer = value.SongPointer,
            PlanetarySystemPointer = value.PlanetarySystemPointer,
            Visible = value.Visible,
            Practice = value.Practice,
            ShowCount = value.ShowCount,
            HideCount = value.HideCount,
            PlayerUpdateCount = value.PlayerUpdateCount,
            StateChangeCount = value.StateChangeCount,
            LastOpCode = value.LastOpCode,
            LastTargetKind = value.LastTargetKind,
            PlayerCount = value.PlayerCount,
            LastSeqId = value.LastSeqId,
            LastIsRestart = value.LastIsRestart,
            LastWipeDirection = value.LastWipeDirection,
            LastResetToEditor = value.LastResetToEditor,
            JudgementHitCount = value.JudgementHitCount,
            JudgementResetCount = value.JudgementResetCount,
            LastHitMargin = value.LastHitMargin,
            FloorMoveCount = value.FloorMoveCount,
            LastFloorExitAngle = value.LastFloorExitAngle,
            LastFloorMoveHitMargin = value.LastFloorMoveHitMargin,
            PlayerHitCount = value.PlayerHitCount,
            LastPlayerHitIsAuto = value.LastPlayerHitIsAuto,
            DeathCount = value.DeathCount,
            LastDeathOverload = value.LastDeathOverload,
            LastDeathMultipress = value.LastDeathMultipress,
            LastDeathHitbox = value.LastDeathHitbox,
            HitTimingCount = value.HitTimingCount,
            LastHitTimingMs = value.LastHitTimingMs,
            LastHitTimingMargin = value.LastHitTimingMargin,
            AccuracySnapshotCount = value.AccuracySnapshotCount,
            PercentAcc = value.PercentAcc,
            PercentXAcc = value.PercentXAcc,
            Progress = value.Progress,
            ComboCount = value.ComboCount,
            AttemptCount = value.AttemptCount,
            BpmSnapshotCount = value.BpmSnapshotCount,
            TileBpm = value.TileBpm,
            Kps = value.Kps,
            TimelineSnapshotCount = value.TimelineSnapshotCount,
            MusicTime = value.MusicTime,
            MusicTotalTime = value.MusicTotalTime,
            MapTime = value.MapTime,
            MapTotalTime = value.MapTotalTime,
            CheckpointsUsed = value.CheckpointsUsed,
            CurrentCheckpoint = value.CurrentCheckpoint,
            TotalCheckpoints = value.TotalCheckpoints,
            CurrentSeqId = value.CurrentSeqId,
            FloorCount = value.FloorCount,
            StartProgress = value.StartProgress,
            SpeedMultiplier = value.SpeedMultiplier,
            PlanetSpeed = value.PlanetSpeed,
            SessionAuto = value.SessionAuto,
            IsAuto = value.IsAuto,
            IsNoFail = value.IsNoFail,
            IsPaused = value.IsPaused,
            IsGameWorld = value.IsGameWorld,
            IsScnGame = value.IsScnGame,
            IsGameReady = value.IsGameReady,
            SongPitch = value.SongPitch,
            ConductorAddOffset = value.ConductorAddOffset,
            ConductorSongPositionMinusi = value.ConductorSongPositionMinusi,
            InputStateGeneration = value.InputStateGeneration,
            InputHeldMask = value.InputHeldMask,
            InputLastDownMask = value.InputLastDownMask,
            InputLastUpMask = value.InputLastUpMask,
            InputTotalCount = value.InputTotalCount,
            InputKps = value.InputKps,
            InstalledSlots = value.InstalledSlots,
            BoundDispatcherSlots = value.BoundDispatcherSlots,
            DispatcherReadySlots = value.DispatcherReadySlots,
            DispatcherCapacity = value.DispatcherCapacity
        };
}

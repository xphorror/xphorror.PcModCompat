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
    private sealed class ProviderRegistration(
        Func<PcCompatOverlaySnapshot> provider,
        Func<bool>? visibilityProvider)
    {
        public Func<PcCompatOverlaySnapshot> Provider { get; } = provider;
        public Func<bool>? VisibilityProvider { get; } = visibilityProvider;
    }

    private static ProviderRegistration? s_registration;

    public static void RegisterProvider(
        Func<PcCompatOverlaySnapshot> provider,
        Func<bool>? visibilityProvider = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Volatile.Write(ref s_registration, new ProviderRegistration(provider, visibilityProvider));
    }

    public static void ClearProvider()
        => Volatile.Write(ref s_registration, null);

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
}

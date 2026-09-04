using System.Collections.Concurrent;

namespace Xphorror.PcModCompat;

public enum PcCompatBridgeOperationKind
{
    ReadState,
    WriteRequest
}

public sealed class PcCompatReversePatchHandler
{
    public required string TargetType { get; init; }
    public required string TargetMethod { get; init; }
    public required string AndroidBridgeMethod { get; init; }
    public required string ReturnType { get; init; }
    public required PcCompatBridgeOperationKind OperationKind { get; init; }

    public string Key => PcCompatReversePatchBridge.MakeKey(TargetMethod);
}

public static class PcCompatReversePatchBridge
{
    private static readonly Dictionary<string, PcCompatReversePatchHandler> Handlers = CreateHandlers();
    private static Action? SnapshotRefresh;
    private static Action? HitMarginsCountRefresh;
    private static readonly object HitMarginsLayoutLock = new();
    private static readonly ConcurrentDictionary<ReversePatchSessionKey, ReversePatchState> States = new();
    private static readonly ReversePatchState UnscopedState = new();
    private static int s_hitMarginsLayout;

    private readonly record struct ReversePatchSessionKey(
        string ModId,
        long ResourceSessionGeneration);

    private sealed class ReversePatchState
    {
        public readonly object Gate = new();
        public PcCompatGameSnapshot Current = new();
        public string? RequestedSceneName;

        // A MOD retains this array reference once (Overlay.Hit), then re-reads it on
        // every judgement update. It must remain stable for this MOD session only.
        public int[] StableHitMarginsCount = Array.Empty<int>();
        public int[] UnsupportedPlayerHitMarginsCount = Array.Empty<int>();
        public bool HitMarginsCountExposed;
        public int HitMarginsLayout;
    }

    private static ReversePatchState GetState()
    {
        var execution = PcCompatManagedExecutionContext.Current;
        if (execution is null || string.IsNullOrWhiteSpace(execution.ModId))
        {
            EnsureHitMarginsLayout(UnscopedState);
            return UnscopedState;
        }

        var state = States.GetOrAdd(
            new ReversePatchSessionKey(
                execution.ModId,
                execution.ResourceSessionGeneration),
            static _ => new ReversePatchState());
        EnsureHitMarginsLayout(state);
        return state;
    }

    private static void EnsureHitMarginsLayout(ReversePatchState state)
    {
        var layout = Volatile.Read(ref s_hitMarginsLayout);
        if (layout == 0 || Volatile.Read(ref state.HitMarginsLayout) == layout)
            return;

        lock (HitMarginsLayoutLock)
        {
            layout = s_hitMarginsLayout;
            if (layout != 0 && state.HitMarginsLayout != layout)
                InitializeHitMarginsLayoutLocked(state, layout);
        }
    }

    private static void InitializeHitMarginsLayoutLocked(ReversePatchState state, int slotCount)
    {
        lock (state.Gate)
        {
            if (state.HitMarginsLayout == slotCount)
                return;
            if (state.HitMarginsLayout != 0 && state.HitMarginsLayout != slotCount)
            {
                throw new InvalidOperationException(
                    $"Hit-margin array layout changed from {state.HitMarginsLayout} to {slotCount}.");
            }

            if (state.StableHitMarginsCount.Length == 0)
            {
                if (state.HitMarginsCountExposed)
                {
                    throw new InvalidOperationException(
                        "Hit-margin array layout was initialized after its empty identity was exposed.");
                }
                state.StableHitMarginsCount = new int[slotCount];
                state.UnsupportedPlayerHitMarginsCount = new int[slotCount];
            }
            else if (state.StableHitMarginsCount.Length != slotCount)
            {
                throw new InvalidOperationException(
                    $"Hit-margin array layout changed from {state.StableHitMarginsCount.Length} to {slotCount}.");
            }

            if (state.UnsupportedPlayerHitMarginsCount.Length == 0)
            {
                state.UnsupportedPlayerHitMarginsCount = new int[slotCount];
            }
            else if (state.UnsupportedPlayerHitMarginsCount.Length != slotCount)
            {
                throw new InvalidOperationException(
                    "Unsupported player hit-margin layout changed from " +
                    $"{state.UnsupportedPlayerHitMarginsCount.Length} to {slotCount}.");
            }

            state.Current.HitMarginsCount = state.StableHitMarginsCount;
            Volatile.Write(ref state.HitMarginsLayout, slotCount);
        }
    }

    public static IReadOnlyList<PcCompatReversePatchHandler> SnapshotHandlers()
        => Handlers.Values.ToArray();

    public static bool TryFindHandler(string targetType, string targetMethod, out PcCompatReversePatchHandler? handler)
    {
        _ = targetType;
        return Handlers.TryGetValue(MakeKey(targetMethod), out handler);
    }

    public static void PublishSnapshot(PcCompatGameSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var state = GetState();
        lock (state.Gate)
        {
            if (snapshot.HitMarginsCount.Length != 0)
                CopyHitMarginsCountLocked(state, snapshot.HitMarginsCount);
            snapshot.HitMarginsCount = state.StableHitMarginsCount;
            state.Current = snapshot;
        }
    }

    public static void RetireSession(string modId, long resourceSessionGeneration)
    {
        if (string.IsNullOrWhiteSpace(modId) || resourceSessionGeneration <= 0)
            return;
        States.TryRemove(
            new ReversePatchSessionKey(modId, resourceSessionGeneration),
            out _);
    }

    public static void PublishAccuracySnapshot(
        float percentAcc,
        float percentXAcc,
        int? playerCount = null)
    {
        var state = GetState();
        lock (state.Gate)
        {
            var current = state.Current;
            state.Current = new PcCompatGameSnapshot
            {
                HitMarginsCount = current.HitMarginsCount,
                Generation = current.Generation,
                SessionEpoch = current.SessionEpoch,
                ResourceSessionGeneration = current.ResourceSessionGeneration,
                ValidFields = current.ValidFields,
                PlanetSpeed = current.PlanetSpeed,
                ControllerPointer = current.ControllerPointer,
                ConductorPointer = current.ConductorPointer,
                LevelMakerPointer = current.LevelMakerPointer,
                CurrentFloorPointer = current.CurrentFloorPointer,
                FirstFloorPointer = current.FirstFloorPointer,
                SongPointer = current.SongPointer,
                PlanetarySystemPointer = current.PlanetarySystemPointer,
                AccuracySnapshotCount = Math.Max(1u, current.AccuracySnapshotCount),
                PercentAcc = percentAcc,
                PercentXAcc = percentXAcc,
                Progress = current.Progress,
                CurrentSeqId = current.CurrentSeqId,
                CheckpointsUsed = current.CheckpointsUsed,
                CurrentCheckpoint = current.CurrentCheckpoint,
                TotalCheckpoints = current.TotalCheckpoints,
                FloorCount = current.FloorCount,
                StartProgress = current.StartProgress,
                SpeedMultiplier = current.SpeedMultiplier,
                IsPaused = current.IsPaused,
                IsNoFail = current.IsNoFail,
                IsGameWorld = current.IsGameWorld,
                IsScnGame = current.IsScnGame,
                IsGameReady = current.IsGameReady,
                IsAuto = current.IsAuto,
                SongPitch = current.SongPitch,
                ConductorAddOffset = current.ConductorAddOffset,
                ConductorSongPositionMinusi = current.ConductorSongPositionMinusi,
                ComboCount = current.ComboCount,
                AttemptCount = current.AttemptCount,
                BpmSnapshotCount = current.BpmSnapshotCount,
                TileBpm = current.TileBpm,
                Kps = current.Kps,
                TimelineSnapshotCount = current.TimelineSnapshotCount,
                MusicTime = current.MusicTime,
                MusicTotalTime = current.MusicTotalTime,
                MapTime = current.MapTime,
                MapTotalTime = current.MapTotalTime,
                PlayerCount = playerCount is > 0 ? playerCount.Value : current.PlayerCount,
                SceneName = current.SceneName
            };
        }
    }

    public static void RegisterSnapshotRefresh(Action? refresh)
        => Volatile.Write(ref SnapshotRefresh, refresh);

    public static void RegisterHitMarginsCountRefresh(Action? refresh)
        => Volatile.Write(ref HitMarginsCountRefresh, refresh);

    public static void InitializeHitMarginsCountLayout(int slotCount)
    {
        if (slotCount <= 0 || slotCount > 64)
            throw new ArgumentOutOfRangeException(nameof(slotCount));

        lock (HitMarginsLayoutLock)
        {
            var currentLayout = s_hitMarginsLayout;
            if (currentLayout != 0 && currentLayout != slotCount)
            {
                throw new InvalidOperationException(
                    $"Hit-margin array layout changed from {currentLayout} to {slotCount}.");
            }

            s_hitMarginsLayout = slotCount;
            InitializeHitMarginsLayoutLocked(UnscopedState, slotCount);
            foreach (var state in States.Values)
                InitializeHitMarginsLayoutLocked(state, slotCount);
        }
    }

    // Copies platform-read values without changing the array identity already retained
    // by the MOD. A runtime layout change is unsafe and therefore fails closed.
    public static void PublishHitMarginsCount(int[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var state = GetState();
        lock (state.Gate)
        {
            CopyHitMarginsCountLocked(state, values);
        }
    }

    public static void PublishHitMarginsCount(ReadOnlySpan<int> values)
    {
        var state = GetState();
        lock (state.Gate)
        {
            CopyHitMarginsCountLocked(state, values);
        }
    }

    public static void ClearHitMarginsCount()
    {
        var state = GetState();
        lock (state.Gate)
        {
            Array.Clear(state.StableHitMarginsCount);
            Array.Clear(state.UnsupportedPlayerHitMarginsCount);
            state.Current.HitMarginsCount = state.StableHitMarginsCount;
        }
    }

    // Per-frame freshness hook invoked before managed game-event dispatch; platform
    // bridges register the actual proxy read. Cheap no-op when nothing is registered.
    public static void RefreshHitMarginsCount()
        => HitMarginsCountRefresh?.Invoke();

    public static PcCompatGameSnapshot Snapshot()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return state.Current;
    }

    internal static uint PublishedSessionEpoch()
    {
        var state = GetState();
        lock (state.Gate)
            return state.Current.SessionEpoch;
    }

    public static bool TryGetSnapshot(
        PcCompatGameSnapshotFields requiredFields,
        out PcCompatGameSnapshot snapshot)
    {
        snapshot = Snapshot();
        var generation = PcCompatManagedExecutionContext.Current?.ResourceSessionGeneration ?? 0;
        return snapshot.Has(requiredFields, generation);
    }

    internal static bool TryGetPublishedSnapshot(
        PcCompatManagedExecutionState owner,
        PcCompatGameSnapshotFields requiredFields,
        out PcCompatGameSnapshot snapshot)
    {
        var state = GetState();
        lock (state.Gate)
        {
            snapshot = state.Current;
            return snapshot.Has(requiredFields, owner.ResourceSessionGeneration);
        }
    }

    public static int[] GetHitMarginsCount()
    {
        RefreshHitMarginsCount();
        var state = GetState();
        lock (state.Gate)
        {
            state.HitMarginsCountExposed = true;
            return state.StableHitMarginsCount;
        }
    }

    public static bool GetHideWithNoAuto(object? instance)
    {
        _ = instance;
        return true;
    }

    public static int GetPlayerIndex(object? tracker)
    {
        _ = tracker;
        // The current Android game has no multiplayer contract. Keep the single-player
        // projection deterministic and never infer a player from an unmanaged object.
        return 0;
    }

    public static int[] GetHitMarginsCountForPlayer(int playerIdx)
    {
        RefreshHitMarginsCount();
        var state = GetState();
        lock (state.Gate)
            return playerIdx == 0
                ? state.StableHitMarginsCount
                : state.UnsupportedPlayerHitMarginsCount;
    }

    public static string GetPlayerColorHex(int playerIdx)
    {
        _ = playerIdx;
        // Player colors are not part of the current native snapshot contract.
        return "FFFFFF";
    }

    public static int[] SnapshotHitMarginsCount()
    {
        var state = GetState();
        lock (state.Gate)
            return (int[])state.StableHitMarginsCount.Clone();
    }

    public static void CalculatePercentAcc()
    {
        // Android bridge publishes percentAcc/percentXAcc through PcCompatGameSnapshot.
    }

    public static double GetPlanetSpeed()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
        {
            var speed = state.Current.PlanetSpeed;
            return double.IsFinite(speed) && speed > 0d ? speed : 1d;
        }
    }

    public static float GetMusicTime()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return NonNegativeFinite(state.Current.MusicTime);
    }

    public static float GetMusicTotalTime()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return NonNegativeFinite(state.Current.MusicTotalTime);
    }

    public static float GetMapTime()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return NonNegativeFinite(state.Current.MapTime);
    }

    public static float GetMapTotalTime()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return NonNegativeFinite(state.Current.MapTotalTime);
    }

    public static int GetCurrentCheckpoint()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return Math.Max(0, state.Current.CurrentCheckpoint);
    }

    public static int GetTotalCheckpoints()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return Math.Max(0, state.Current.TotalCheckpoints);
    }

    public static int GetFloorCount()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return Math.Max(0, state.Current.FloorCount);
    }

    public static float GetStartProgress()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return ClampProgress(state.Current.StartProgress);
    }

    public static float GetSpeedMultiplier()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return PositiveFinite(state.Current.SpeedMultiplier, 1f);
    }

    public static float GetTileBpm()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return PositiveFinite(state.Current.TileBpm, 0f);
    }

    public static float GetKps()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return PositiveFinite(state.Current.Kps, 0f);
    }

    public static void LoadScene(string name)
    {
        var state = GetState();
        lock (state.Gate)
            state.RequestedSceneName = name;
    }

    public static string? ConsumeRequestedSceneName()
    {
        var state = GetState();
        lock (state.Gate)
        {
            var name = state.RequestedSceneName;
            state.RequestedSceneName = null;
            return name;
        }
    }

    public static float GetPercentAcc()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return state.Current.PercentAcc;
    }

    public static float GetPercentXAcc()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return state.Current.PercentXAcc;
    }

    public static int GetCurrentSeqId()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return state.Current.CurrentSeqId;
    }

    public static float GetProgress()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return state.Current.Progress;
    }

    public static int GetCheckpointsUsed()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return state.Current.CheckpointsUsed;
    }

    public static bool GetIsPaused()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return state.Current.IsPaused;
    }

    public static bool GetIsNoFail()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return state.Current.IsNoFail;
    }

    public static bool GetIsGameWorld()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return state.Current.IsGameWorld;
    }

    public static bool GetIsScnGame()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return state.Current.IsScnGame;
    }

    public static bool GetIsGameReady()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return state.Current.IsGameReady;
    }

    public static bool GetIsAuto()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return state.Current.IsAuto;
    }

    public static float GetSongPitch()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return float.IsFinite(state.Current.SongPitch) ? state.Current.SongPitch : 1f;
    }

    public static double GetConductorAddOffset()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return double.IsFinite(state.Current.ConductorAddOffset)
                ? state.Current.ConductorAddOffset
                : 0d;
    }

    public static double GetConductorSongPositionMinusi()
    {
        RefreshSnapshot();
        var state = GetState();
        lock (state.Gate)
            return double.IsFinite(state.Current.ConductorSongPositionMinusi)
                ? state.Current.ConductorSongPositionMinusi
                : 0d;
    }

    public static bool IsCoopMode()
    {
        return false;
    }

    public static int GetPlayerCount()
    {
        return 1;
    }

    public static void ColorLogoSafe()
    {
        // Color replacement is a write-side request and needs a Unity object bridge before it can do real work.
    }

    internal static string MakeKey(string targetMethod)
        => targetMethod;

    private static void RefreshSnapshot()
        => Volatile.Read(ref SnapshotRefresh)?.Invoke();

    private static void CopyHitMarginsCountLocked(ReversePatchState state, int[] values)
        => CopyHitMarginsCountLocked(state, values.AsSpan());

    private static void CopyHitMarginsCountLocked(
        ReversePatchState state,
        ReadOnlySpan<int> values)
    {
        if (values.Length == 0)
            return;
        if (state.StableHitMarginsCount.Length == 0)
        {
            if (state.HitMarginsCountExposed)
            {
                throw new InvalidOperationException(
                    "Hit-margin values arrived after the empty array identity was exposed.");
            }
            state.StableHitMarginsCount = new int[values.Length];
            state.UnsupportedPlayerHitMarginsCount = new int[values.Length];
            Volatile.Write(ref state.HitMarginsLayout, values.Length);
        }
        else if (state.StableHitMarginsCount.Length != values.Length)
        {
            throw new InvalidDataException(
                $"Hit-margin array length mismatch: expected " +
                $"{state.StableHitMarginsCount.Length}, got {values.Length}.");
        }

        values.CopyTo(state.StableHitMarginsCount);
        state.Current.HitMarginsCount = state.StableHitMarginsCount;
    }

    private static Dictionary<string, PcCompatReversePatchHandler> CreateHandlers()
    {
        var handlers = new[]
        {
            Handler("ColorLogoSafe", "void", nameof(ColorLogoSafe), PcCompatBridgeOperationKind.WriteRequest),
            Handler("CalculatePercentAcc", "void", nameof(CalculatePercentAcc), PcCompatBridgeOperationKind.WriteRequest),
            Handler("GetHitMarginsCount", "int[]", nameof(GetHitMarginsCount), PcCompatBridgeOperationKind.ReadState),
            Handler("GetPlanetSpeed", "double", nameof(GetPlanetSpeed), PcCompatBridgeOperationKind.ReadState),
            Handler("GetMusicTime", "float", nameof(GetMusicTime), PcCompatBridgeOperationKind.ReadState),
            Handler("GetMusicTotalTime", "float", nameof(GetMusicTotalTime), PcCompatBridgeOperationKind.ReadState),
            Handler("GetMapTime", "float", nameof(GetMapTime), PcCompatBridgeOperationKind.ReadState),
            Handler("GetMapTotalTime", "float", nameof(GetMapTotalTime), PcCompatBridgeOperationKind.ReadState),
            Handler("GetCurrentCheckpoint", "int", nameof(GetCurrentCheckpoint), PcCompatBridgeOperationKind.ReadState),
            Handler("GetTotalCheckpoints", "int", nameof(GetTotalCheckpoints), PcCompatBridgeOperationKind.ReadState),
            Handler("GetFloorCount", "int", nameof(GetFloorCount), PcCompatBridgeOperationKind.ReadState),
            Handler("GetStartProgress", "float", nameof(GetStartProgress), PcCompatBridgeOperationKind.ReadState),
            Handler("GetSpeedMultiplier", "float", nameof(GetSpeedMultiplier), PcCompatBridgeOperationKind.ReadState),
            Handler("GetTileBpm", "float", nameof(GetTileBpm), PcCompatBridgeOperationKind.ReadState),
            Handler("GetKps", "float", nameof(GetKps), PcCompatBridgeOperationKind.ReadState),
            Handler("LoadScene", "void", nameof(LoadScene), PcCompatBridgeOperationKind.WriteRequest),
            Handler("GetPercentAcc", "float", nameof(GetPercentAcc), PcCompatBridgeOperationKind.ReadState),
            Handler("GetPercentXAcc", "float", nameof(GetPercentXAcc), PcCompatBridgeOperationKind.ReadState),
            Handler("GetCurrentSeqId", "int", nameof(GetCurrentSeqId), PcCompatBridgeOperationKind.ReadState),
            Handler("GetProgress", "float", nameof(GetProgress), PcCompatBridgeOperationKind.ReadState),
            Handler("GetCheckpointsUsed", "int", nameof(GetCheckpointsUsed), PcCompatBridgeOperationKind.ReadState),
            Handler("GetIsPaused", "bool", nameof(GetIsPaused), PcCompatBridgeOperationKind.ReadState),
            Handler("GetIsNoFail", "bool", nameof(GetIsNoFail), PcCompatBridgeOperationKind.ReadState),
            Handler("GetIsGameWorld", "bool", nameof(GetIsGameWorld), PcCompatBridgeOperationKind.ReadState),
            Handler("GetIsScnGame", "bool", nameof(GetIsScnGame), PcCompatBridgeOperationKind.ReadState),
            Handler("GetIsGameReady", "bool", nameof(GetIsGameReady), PcCompatBridgeOperationKind.ReadState),
            Handler("GetIsAuto", "bool", nameof(GetIsAuto), PcCompatBridgeOperationKind.ReadState),
            Handler("GetSongPitch", "float", nameof(GetSongPitch), PcCompatBridgeOperationKind.ReadState),
            Handler("GetConductorAddOffset", "double", nameof(GetConductorAddOffset), PcCompatBridgeOperationKind.ReadState),
            Handler("GetConductorSongPositionMinusi", "double", nameof(GetConductorSongPositionMinusi), PcCompatBridgeOperationKind.ReadState),
            Handler("IsCoopMode", "bool", nameof(IsCoopMode), PcCompatBridgeOperationKind.ReadState),
            Handler("GetPlayerCount", "int", nameof(GetPlayerCount), PcCompatBridgeOperationKind.ReadState),
            Handler("GetHideWithNoAuto", "bool", nameof(GetHideWithNoAuto), PcCompatBridgeOperationKind.ReadState),
            Handler("GetPlayerIndex", "int", nameof(GetPlayerIndex), PcCompatBridgeOperationKind.ReadState),
            Handler("GetHitMarginsCountForPlayer", "int[]", nameof(GetHitMarginsCountForPlayer), PcCompatBridgeOperationKind.ReadState),
            Handler("GetPlayerColorHex", "string", nameof(GetPlayerColorHex), PcCompatBridgeOperationKind.ReadState)
        };

        return handlers.ToDictionary(handler => handler.Key, StringComparer.Ordinal);
    }

    private static float NonNegativeFinite(float value)
        => float.IsFinite(value) && value >= 0f ? value : 0f;

    private static float PositiveFinite(float value, float fallback)
        => float.IsFinite(value) && value > 0f ? value : fallback;

    private static float ClampProgress(float value)
        => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;

    private static PcCompatReversePatchHandler Handler(
        string targetMethod,
        string returnType,
        string bridgeMethod,
        PcCompatBridgeOperationKind kind)
        => new()
        {
            TargetType = "*",
            TargetMethod = targetMethod,
            AndroidBridgeMethod = bridgeMethod,
            ReturnType = returnType,
            OperationKind = kind
        };
}

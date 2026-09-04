namespace Xphorror.PcModCompat;

[Flags]
public enum PcCompatGameSnapshotFields : ulong
{
    None = 0,
    Progress = 1UL << 0,
    CurrentSeqId = 1UL << 1,
    Checkpoints = 1UL << 2,
    Floor = 1UL << 3,
    Accuracy = 1UL << 4,
    Bpm = 1UL << 5,
    Timeline = 1UL << 6,
    PlanetSpeed = 1UL << 7,
    State = 1UL << 8,
    Conductor = 1UL << 9,
    SongPitch = 1UL << 10,
    Player = 1UL << 11,
    All = Progress | CurrentSeqId | Checkpoints | Floor | Accuracy | Bpm |
          Timeline | PlanetSpeed | State | Conductor | SongPitch | Player
}

public sealed class PcCompatGameSnapshot
{
    public static PcCompatGameSnapshot Empty { get; } = new();

    // Mutable so the reverse-patch bridge can keep the live hit-margins array attached
    // when swapping snapshots under its state lock.
    public int[] HitMarginsCount { get; set; } = Array.Empty<int>();
    public uint Generation { get; init; }
    public uint SessionEpoch { get; init; }
    public long ResourceSessionGeneration { get; init; }
    public PcCompatGameSnapshotFields ValidFields { get; init; }

    public bool Has(PcCompatGameSnapshotFields fields, long resourceSessionGeneration)
        => fields != PcCompatGameSnapshotFields.None &&
           (ValidFields & fields) == fields &&
           Generation != 0 &&
           ResourceSessionGeneration == resourceSessionGeneration;

    public static PcCompatGameSnapshot FromOverlay(
        PcCompatOverlaySnapshot overlay,
        long resourceSessionGeneration)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (!overlay.ProviderAvailable || overlay.Generation == 0 || resourceSessionGeneration <= 0)
            return Empty;

        var valid = overlay.ValidGameSnapshotFields;
        if (!overlay.HasExplicitGameSnapshotValidity)
        {
            // Pre-V7 producers exposed counters instead of a field mask. Keep this fallback for
            // persisted diagnostics and unit fixtures; the V7 native producer always publishes
            // explicit validity and can therefore represent partial telemetry capability.
            if (overlay.AccuracySnapshotCount != 0)
                valid |= PcCompatGameSnapshotFields.Accuracy;
            if (overlay.BpmSnapshotCount != 0)
                valid |= PcCompatGameSnapshotFields.Bpm;
            if (overlay.TimelineSnapshotCount != 0)
            {
                valid |= PcCompatGameSnapshotFields.Progress |
                         PcCompatGameSnapshotFields.CurrentSeqId |
                         PcCompatGameSnapshotFields.Checkpoints |
                         PcCompatGameSnapshotFields.Floor |
                         PcCompatGameSnapshotFields.Timeline |
                         PcCompatGameSnapshotFields.PlanetSpeed |
                         PcCompatGameSnapshotFields.State |
                         PcCompatGameSnapshotFields.Conductor |
                         PcCompatGameSnapshotFields.SongPitch |
                         PcCompatGameSnapshotFields.Player;
            }
        }

        return new PcCompatGameSnapshot
        {
            Generation = overlay.Generation,
            SessionEpoch = overlay.SessionEpoch,
            ResourceSessionGeneration = resourceSessionGeneration,
            ValidFields = valid,
            ControllerPointer = overlay.ControllerPointer,
            ConductorPointer = overlay.ConductorPointer,
            LevelMakerPointer = overlay.LevelMakerPointer,
            CurrentFloorPointer = overlay.CurrentFloorPointer,
            FirstFloorPointer = overlay.FirstFloorPointer,
            SongPointer = overlay.SongPointer,
            PlanetarySystemPointer = overlay.PlanetarySystemPointer,
            PlanetSpeed = overlay.PlanetSpeed,
            AccuracySnapshotCount = overlay.AccuracySnapshotCount,
            PercentAcc = overlay.PercentAcc,
            PercentXAcc = overlay.PercentXAcc,
            Progress = overlay.Progress,
            CurrentSeqId = overlay.CurrentSeqId,
            CheckpointsUsed = overlay.CheckpointsUsed,
            CurrentCheckpoint = overlay.CurrentCheckpoint,
            TotalCheckpoints = overlay.TotalCheckpoints,
            FloorCount = overlay.FloorCount,
            StartProgress = overlay.StartProgress,
            SpeedMultiplier = overlay.SpeedMultiplier,
            IsPaused = overlay.IsPaused,
            IsNoFail = overlay.IsNoFail,
            IsGameWorld = overlay.IsGameWorld,
            IsScnGame = overlay.IsScnGame,
            IsGameReady = overlay.IsGameReady,
            IsAuto = overlay.IsAuto,
            SongPitch = overlay.SongPitch,
            ConductorAddOffset = overlay.ConductorAddOffset,
            ConductorSongPositionMinusi = overlay.ConductorSongPositionMinusi,
            ComboCount = overlay.ComboCount,
            AttemptCount = overlay.AttemptCount,
            BpmSnapshotCount = overlay.BpmSnapshotCount,
            TileBpm = overlay.TileBpm,
            Kps = overlay.Kps,
            TimelineSnapshotCount = overlay.TimelineSnapshotCount,
            MusicTime = overlay.MusicTime,
            MusicTotalTime = overlay.MusicTotalTime,
            MapTime = overlay.MapTime,
            MapTotalTime = overlay.MapTotalTime,
            PlayerCount = Math.Max(1, overlay.PlayerCount)
        };
    }

    public double PlanetSpeed { get; init; }
    public long ControllerPointer { get; init; }
    public long ConductorPointer { get; init; }
    public long LevelMakerPointer { get; init; }
    public long CurrentFloorPointer { get; init; }
    public long FirstFloorPointer { get; init; }
    public long SongPointer { get; init; }
    public long PlanetarySystemPointer { get; init; }
    public uint AccuracySnapshotCount { get; init; }
    public float PercentAcc { get; init; }
    public float PercentXAcc { get; init; }
    public float Progress { get; init; }
    public int CurrentSeqId { get; init; }
    public int CheckpointsUsed { get; init; }
    public int CurrentCheckpoint { get; init; }
    public int TotalCheckpoints { get; init; }
    public int FloorCount { get; init; }
    public float StartProgress { get; init; }
    public float SpeedMultiplier { get; init; } = 1f;
    public bool IsPaused { get; init; }
    public bool IsNoFail { get; init; }
    public bool IsGameWorld { get; init; }
    public bool IsScnGame { get; init; }
    public bool IsGameReady { get; init; }
    public bool IsAuto { get; init; }
    public float SongPitch { get; init; } = 1f;
    public double ConductorAddOffset { get; init; }
    public double ConductorSongPositionMinusi { get; init; }
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
    public int PlayerCount { get; init; } = 1;
    public string SceneName { get; init; } = string.Empty;

    public bool IsCoopMode => PlayerCount > 1;
}

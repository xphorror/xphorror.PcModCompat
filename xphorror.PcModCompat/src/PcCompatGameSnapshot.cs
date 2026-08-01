namespace Xphorror.PcModCompat;

public sealed class PcCompatGameSnapshot
{
    public static PcCompatGameSnapshot Empty { get; } = new();

    // Mutable so the reverse-patch bridge can keep the live hit-margins array attached
    // when swapping snapshots under its state lock.
    public int[] HitMarginsCount { get; set; } = Array.Empty<int>();
    public double PlanetSpeed { get; init; }
    public float PercentAcc { get; init; }
    public float PercentXAcc { get; init; }
    public float Progress { get; init; }
    public int ComboCount { get; init; }
    public uint AttemptCount { get; init; }
    public float TileBpm { get; init; }
    public float Kps { get; init; }
    public int PlayerCount { get; init; } = 1;
    public string SceneName { get; init; } = string.Empty;

    public bool IsCoopMode => PlayerCount > 1;
}

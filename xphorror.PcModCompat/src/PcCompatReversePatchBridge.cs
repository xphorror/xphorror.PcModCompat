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
    private static readonly object StateLock = new();
    private static readonly Dictionary<string, PcCompatReversePatchHandler> Handlers = CreateHandlers();

    private static PcCompatGameSnapshot Current = PcCompatGameSnapshot.Empty;
    private static string? RequestedSceneName;
    private static Action? SnapshotRefresh;

    // The MOD stores the returned array reference once (Overlay.Hit) and re-reads it on
    // every judgement update, so the bridge must hand out ONE stable array whose contents
    // the platform side keeps in sync with the live scrMarginTracker.hitMarginsCount.
    private static int[] StableHitMarginsCount = Array.Empty<int>();
    private static bool HitMarginsCountExposed;
    private static Action? HitMarginsCountRefresh;

    public static IReadOnlyList<PcCompatReversePatchHandler> SnapshotHandlers()
        => Handlers.Values.ToArray();

    public static bool TryFindHandler(string targetType, string targetMethod, out PcCompatReversePatchHandler? handler)
    {
        _ = targetType;
        return Handlers.TryGetValue(MakeKey(targetMethod), out handler);
    }

    public static void PublishSnapshot(PcCompatGameSnapshot snapshot)
    {
        lock (StateLock)
        {
            if (snapshot.HitMarginsCount.Length != 0)
                CopyHitMarginsCountLocked(snapshot.HitMarginsCount);
            snapshot.HitMarginsCount = StableHitMarginsCount;
            Current = snapshot;
        }
    }

    public static void PublishAccuracySnapshot(
        float percentAcc,
        float percentXAcc,
        int? playerCount = null)
    {
        lock (StateLock)
        {
            Current = new PcCompatGameSnapshot
            {
                HitMarginsCount = Current.HitMarginsCount,
                PlanetSpeed = Current.PlanetSpeed,
                PercentAcc = percentAcc,
                PercentXAcc = percentXAcc,
                Progress = Current.Progress,
                ComboCount = Current.ComboCount,
                AttemptCount = Current.AttemptCount,
                TileBpm = Current.TileBpm,
                Kps = Current.Kps,
                PlayerCount = playerCount is > 0 ? playerCount.Value : Current.PlayerCount,
                SceneName = Current.SceneName
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

        lock (StateLock)
        {
            if (StableHitMarginsCount.Length == 0)
            {
                if (HitMarginsCountExposed)
                {
                    throw new InvalidOperationException(
                        "Hit-margin array layout was initialized after its empty identity was exposed.");
                }
                StableHitMarginsCount = new int[slotCount];
            }
            else if (StableHitMarginsCount.Length != slotCount)
            {
                throw new InvalidOperationException(
                    $"Hit-margin array layout changed from {StableHitMarginsCount.Length} to {slotCount}.");
            }

            Current.HitMarginsCount = StableHitMarginsCount;
        }
    }

    // Copies platform-read values without changing the array identity already retained
    // by the MOD. A runtime layout change is unsafe and therefore fails closed.
    public static void PublishHitMarginsCount(int[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        lock (StateLock)
            CopyHitMarginsCountLocked(values);
    }

    public static void PublishHitMarginsCount(ReadOnlySpan<int> values)
    {
        lock (StateLock)
            CopyHitMarginsCountLocked(values);
    }

    public static void ClearHitMarginsCount()
    {
        lock (StateLock)
        {
            Array.Clear(StableHitMarginsCount);
            Current.HitMarginsCount = StableHitMarginsCount;
        }
    }

    // Per-frame freshness hook invoked before managed game-event dispatch; platform
    // bridges register the actual proxy read. Cheap no-op when nothing is registered.
    public static void RefreshHitMarginsCount()
        => HitMarginsCountRefresh?.Invoke();

    public static PcCompatGameSnapshot Snapshot()
    {
        RefreshSnapshot();
        lock (StateLock)
            return Current;
    }

    public static int[] GetHitMarginsCount()
    {
        RefreshHitMarginsCount();
        lock (StateLock)
        {
            HitMarginsCountExposed = true;
            return StableHitMarginsCount;
        }
    }

    public static int[] SnapshotHitMarginsCount()
    {
        lock (StateLock)
            return (int[])StableHitMarginsCount.Clone();
    }

    public static void CalculatePercentAcc()
    {
        // Android bridge publishes percentAcc/percentXAcc through PcCompatGameSnapshot.
    }

    public static double GetPlanetSpeed()
    {
        RefreshSnapshot();
        lock (StateLock)
        {
            var speed = Current.PlanetSpeed;
            return double.IsFinite(speed) && speed > 0d ? speed : 1d;
        }
    }

    public static void LoadScene(string name)
    {
        lock (StateLock)
            RequestedSceneName = name;
    }

    public static string? ConsumeRequestedSceneName()
    {
        lock (StateLock)
        {
            var name = RequestedSceneName;
            RequestedSceneName = null;
            return name;
        }
    }

    public static float GetPercentAcc()
    {
        RefreshSnapshot();
        lock (StateLock)
            return Current.PercentAcc;
    }

    public static float GetPercentXAcc()
    {
        RefreshSnapshot();
        lock (StateLock)
            return Current.PercentXAcc;
    }

    public static bool IsCoopMode()
    {
        RefreshSnapshot();
        lock (StateLock)
            return Current.IsCoopMode;
    }

    public static int GetPlayerCount()
    {
        RefreshSnapshot();
        lock (StateLock)
            return Current.PlayerCount;
    }

    public static void ColorLogoSafe()
    {
        // Color replacement is a write-side request and needs a Unity object bridge before it can do real work.
    }

    internal static string MakeKey(string targetMethod)
        => targetMethod;

    private static void RefreshSnapshot()
        => Volatile.Read(ref SnapshotRefresh)?.Invoke();

    private static void CopyHitMarginsCountLocked(int[] values)
        => CopyHitMarginsCountLocked(values.AsSpan());

    private static void CopyHitMarginsCountLocked(ReadOnlySpan<int> values)
    {
        if (values.Length == 0)
            return;
        if (StableHitMarginsCount.Length == 0)
        {
            if (HitMarginsCountExposed)
            {
                throw new InvalidOperationException(
                    "Hit-margin values arrived after the empty array identity was exposed.");
            }
            StableHitMarginsCount = new int[values.Length];
        }
        else if (StableHitMarginsCount.Length != values.Length)
        {
            throw new InvalidDataException(
                $"Hit-margin array length mismatch: expected {StableHitMarginsCount.Length}, got {values.Length}.");
        }

        values.CopyTo(StableHitMarginsCount);
        Current.HitMarginsCount = StableHitMarginsCount;
    }

    private static Dictionary<string, PcCompatReversePatchHandler> CreateHandlers()
    {
        var handlers = new[]
        {
            Handler("ColorLogoSafe", "void", nameof(ColorLogoSafe), PcCompatBridgeOperationKind.WriteRequest),
            Handler("CalculatePercentAcc", "void", nameof(CalculatePercentAcc), PcCompatBridgeOperationKind.WriteRequest),
            Handler("GetHitMarginsCount", "int[]", nameof(GetHitMarginsCount), PcCompatBridgeOperationKind.ReadState),
            Handler("GetPlanetSpeed", "double", nameof(GetPlanetSpeed), PcCompatBridgeOperationKind.ReadState),
            Handler("LoadScene", "void", nameof(LoadScene), PcCompatBridgeOperationKind.WriteRequest),
            Handler("GetPercentAcc", "float", nameof(GetPercentAcc), PcCompatBridgeOperationKind.ReadState),
            Handler("GetPercentXAcc", "float", nameof(GetPercentXAcc), PcCompatBridgeOperationKind.ReadState),
            Handler("IsCoopMode", "bool", nameof(IsCoopMode), PcCompatBridgeOperationKind.ReadState),
            Handler("GetPlayerCount", "int", nameof(GetPlayerCount), PcCompatBridgeOperationKind.ReadState)
        };

        return handlers.ToDictionary(handler => handler.Key, StringComparer.Ordinal);
    }

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

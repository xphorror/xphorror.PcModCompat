namespace Xphorror.PcModCompat;

public readonly record struct PcCompatResourceColor(float R, float G, float B, float A);

public sealed record PcCompatResourceChangerState
{
    public required string ModId { get; init; }
    public long SessionGeneration { get; init; }
    public bool ChangeRabbit { get; init; }
    public bool ChangeBallColor { get; init; }
    public bool ChangeTileColor { get; init; }
    public PcCompatResourceColor PlanetColor { get; init; }
    public PcCompatResourceColor TitleColor { get; init; }
    public PcCompatResourceColor TileColor { get; init; }
    public string ResourcePackName { get; init; } = string.Empty;
    public bool ManagedSource { get; init; }
}

public static class PcCompatResourceChangerRuntime
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, PcCompatResourceChangerState> Latest =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly PcCompatResourceColor DefaultPlanetColor =
        new(0.8125f, 0.70703125f, 0.96875f, 1f);
    private static readonly PcCompatResourceColor DefaultTitleColor =
        new(0.56640625f, 0.46875f, 0.6328125f, 1f);
    private static readonly PcCompatResourceColor DefaultTileColor =
        new(0.94921875f, 0.87109375f, 1f, 1f);
    private static Action<PcCompatResourceChangerState>? s_settingsSink;

    public static bool IsSettingsSinkRegistered
    {
        get
        {
            lock (Gate)
                return s_settingsSink != null;
        }
    }

    public static void RegisterSettingsSink(Action<PcCompatResourceChangerState> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (Gate)
        {
            s_settingsSink = sink;
            try
            {
                foreach (var state in Latest.Values.OrderBy(value => value.ModId, StringComparer.OrdinalIgnoreCase))
                    sink(state);
            }
            catch
            {
                s_settingsSink = null;
                throw;
            }
        }
    }

    public static void ClearSettingsSink()
    {
        lock (Gate)
            s_settingsSink = null;
    }

    public static bool TryApply(string modId, PcCompatMobileSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentNullException.ThrowIfNull(settings);
        return TryPublish(new PcCompatResourceChangerState
        {
            ModId = modId,
            ChangeRabbit = settings.ResourceChangerChangeRabbit,
            ChangeBallColor = settings.ResourceChangerChangeBallColor,
            ChangeTileColor = settings.ResourceChangerChangeTileColor,
            PlanetColor = DefaultPlanetColor,
            TitleColor = DefaultTitleColor,
            TileColor = DefaultTileColor,
            ResourcePackName = "Jipper Resource Pack"
        });
    }

    internal static bool TryPublish(PcCompatResourceChangerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (Gate)
        {
            Latest[state.ModId] = state;
            if (s_settingsSink == null)
                return false;
            s_settingsSink(state);
            return true;
        }
    }

    public static bool TryRepublish(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        lock (Gate)
        {
            if (s_settingsSink == null || !Latest.TryGetValue(modId, out var state))
                return false;
            s_settingsSink(state);
            return true;
        }
    }

    public static bool TryGetState(string modId, out PcCompatResourceChangerState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        lock (Gate)
            return Latest.TryGetValue(modId, out state!);
    }

    public static bool TryDisable(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        lock (Gate)
        {
            if (!Latest.TryGetValue(modId, out var state))
                return false;
            var disabled = state with
            {
                ChangeRabbit = false,
                ChangeBallColor = false,
                ChangeTileColor = false
            };
            Latest[modId] = disabled;
            if (s_settingsSink == null)
                return false;
            s_settingsSink(disabled);
            return true;
        }
    }

    public static void Remove(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        lock (Gate)
            Latest.Remove(modId);
    }
}

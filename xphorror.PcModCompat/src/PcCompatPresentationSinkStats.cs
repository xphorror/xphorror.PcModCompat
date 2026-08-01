namespace Xphorror.PcModCompat;

public sealed class PcCompatPresentationSinkStats
{
    public static PcCompatPresentationSinkStats Unavailable { get; } = new();

    public bool ProviderAvailable { get; init; }
    public bool Installed { get; init; }
    public bool PrimaryHook { get; init; }
    public bool FallbackHook { get; init; }
    public uint ConsumeOpportunities { get; init; }
    public uint SnapshotUpdates { get; init; }
    public uint CommandCount { get; init; }
    public uint UnsupportedCommandCount { get; init; }
    public uint LastPublicationGeneration { get; init; }
    public uint LastSessionGeneration { get; init; }
    public uint RegisteredGraphCount { get; init; }
    public uint MaterializedGraphCount { get; init; }
    public uint GraphMaterializationFailures { get; init; }
    public uint InvalidTargetCount { get; init; }
    public uint RetiredGraphCount { get; init; }
    public ulong PresentationHistoryOverflowCount { get; init; }
    public uint StreamGapCount { get; init; }
    public bool StreamFaulted { get; init; }
    public bool OnGUIHook { get; init; }
    public bool OnGUIProcessHook { get; init; }
    public bool OnGUIBeginHook { get; init; }
    public bool OnGUIEnabled { get; init; }
    public uint OnGUIProcessEventCount { get; init; }
    public uint OnGUIBeginGUICount { get; init; }
    public uint OnGUIDispatchCount { get; init; }
}

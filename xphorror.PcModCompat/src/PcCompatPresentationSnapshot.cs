namespace Xphorror.PcModCompat;

public sealed class PcCompatPresentationCommand
{
    public ulong Sequence { get; init; }
    public uint SessionGeneration { get; init; }
    public uint Generation { get; init; }
    public uint RuleId { get; init; }
    public uint CommandType { get; init; }
    public uint TargetId { get; init; }
    public long Payload0 { get; init; }
    public long Payload1 { get; init; }
    public float Value0 { get; init; }
    public float Value1 { get; init; }
}

public sealed class PcCompatPresentationSnapshot
{
    public static PcCompatPresentationSnapshot Unavailable { get; } = new();

    public bool ProviderAvailable { get; init; }
    public uint PublicationGeneration { get; init; }
    public uint SessionGeneration { get; init; }
    public ulong DroppedStaleTasks { get; init; }
    public ulong SchedulerOverflowCount { get; init; }
    public long PublishedRawNs { get; init; }
    public IReadOnlyList<PcCompatPresentationCommand> Commands { get; init; } =
        Array.Empty<PcCompatPresentationCommand>();
}

namespace Xphorror.PcModCompat;

public enum PcCompatPatchKind
{
    Prefix = 0,
    Postfix = 1,
    Transpiler = 2,
    Finalizer = 3,
    Replace = 4,
    ReversePatch = 100,
    Unknown = 101
}

internal static class PcCompatPatchKinds
{
    public static PcCompatPatchKind FromJALibValue(int value)
        => value switch
        {
            0 => PcCompatPatchKind.Prefix,
            1 => PcCompatPatchKind.Postfix,
            2 => PcCompatPatchKind.Transpiler,
            3 => PcCompatPatchKind.Finalizer,
            4 => PcCompatPatchKind.Replace,
            _ => PcCompatPatchKind.Unknown
        };
}

public enum PcCompatPatchStatus
{
    RegisteredOnly,
    Supported,
    Unsupported
}

public sealed class PcCompatPatchDescriptor
{
    public required string ModId { get; init; }
    public required string TargetType { get; init; }
    public required string TargetMethod { get; init; }
    public required PcCompatPatchKind Kind { get; init; }
    public required string CallbackType { get; init; }
    public required string CallbackMethod { get; init; }
    public string? CallbackAssemblyPath { get; init; }
    public IReadOnlyList<string> CallbackParameterTypeNames { get; init; } = Array.Empty<string>();
    public string PatchOwner { get; init; } = string.Empty;
    public long RegistrationIndex { get; init; }
    public int Priority { get; init; } = -1;
    public IReadOnlyList<string> Before { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> After { get; init; } = Array.Empty<string>();
    public bool NeedInstance { get; init; }
    public int MinVersion { get; init; }
    public int MaxVersion { get; init; } = int.MaxValue;
    public bool TryingCatch { get; init; } = true;
    public IReadOnlyList<string> ArgumentTypeNames { get; init; } = Array.Empty<string>();
    public string Source { get; init; } = "managed_oracle";
    public PcCompatPatchStatus Status { get; set; } = PcCompatPatchStatus.RegisteredOnly;
    public string Reason { get; set; } = "native hook mapping is not implemented yet";

    public bool IsApplicableToRevision(int revision)
        => revision >= MinVersion && revision <= MaxVersion;

    public override string ToString()
        => $"{ModId}: {Kind} {TargetType}.{TargetMethod} -> {CallbackType}.{CallbackMethod} " +
           $"owner={PatchOwner} priority={Priority} " +
           $"version={MinVersion}..{(MaxVersion == int.MaxValue ? "max" : MaxVersion)} source={Source} [{Status}] {Reason}";
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xphorror.PcModCompat;

public enum PcCompatCallbackTranslationStatus
{
    Translated,
    NotMapped,
    Unsupported,
    Skipped
}

public sealed class PcCompatCallbackTranslationItem
{
    public required string TargetType { get; init; }
    public required string TargetMethod { get; init; }
    public required string CallbackType { get; init; }
    public required string CallbackMethod { get; init; }
    public IReadOnlyList<string> CallbackParameterTypeNames { get; init; } = Array.Empty<string>();
    public PcCompatPatchKind PatchKind { get; init; }
    public PcCompatCallbackTranslationStatus Status { get; init; }
    public string? RuleId { get; init; }
    public bool ManagedDispatchRequired { get; init; } = true;

    // Set only for targets outside the verified fixed-op catalog whose exact signature the host
    // read back from runtime IL2CPP metadata. The recipe compiler needs it to emit a managed-event
    // rule the strict native resolver will accept; it stays null on every catalog-backed item.
    public PcCompatResolvedTargetSignature? ResolvedTarget { get; init; }

    public required string Reason { get; init; }
}

public sealed class PcCompatCallbackTranslationReport
{
    public const string CurrentFormatVersion = "callback-translation-v9-managed-only-catalog";

    public string FormatVersion { get; init; } = CurrentFormatVersion;
    public required string ModId { get; init; }
    public int TargetGameRevision { get; init; }
    public IReadOnlyList<PcCompatCompiledRule> Rules { get; init; } = Array.Empty<PcCompatCompiledRule>();
    public IReadOnlyList<PcCompatCallbackTranslationItem> Items { get; init; } = Array.Empty<PcCompatCallbackTranslationItem>();

    [JsonIgnore]
    public int TranslatedCount => Items.Count(item => item.Status == PcCompatCallbackTranslationStatus.Translated);

    [JsonIgnore]
    public int UnsupportedCount => Items.Count(item => item.Status == PcCompatCallbackTranslationStatus.Unsupported);

    public string ToJson()
        => JsonSerializer.Serialize(this, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}

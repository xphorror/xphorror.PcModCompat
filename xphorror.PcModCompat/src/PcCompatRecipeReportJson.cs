using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xphorror.PcModCompat;

public static class PcCompatRecipeReportJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(PcCompatRecipeCompileReport report)
        => JsonSerializer.Serialize(report, Options);

    public static PcCompatRecipeCompileReport? Deserialize(string json)
        => JsonSerializer.Deserialize<PcCompatRecipeCompileReport>(json, Options);
}

using System.Text.Json;

namespace Xphorror.PcModCompat;

public enum PcModKind
{
    UnityModManager,
    JAMod
}

public sealed class PcModManifest
{
    public required string FolderPath { get; init; }
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string Author { get; init; } = string.Empty;
    public string Version { get; init; } = "0.0.0";
    public string AssemblyName { get; init; } = string.Empty;
    public string EntryMethod { get; init; } = string.Empty;
    public PcModKind Kind { get; init; }
    public string? JAModAssemblyPath { get; init; }
    public string? JAModClassName { get; init; }
    public int JAModLocalizationGid { get; init; } = -1;
    public bool AssemblyRequireModPath { get; init; }
    public IReadOnlyList<string> Requirements { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LoadAfter { get; init; } = Array.Empty<string>();
    public string? RawInfoJson { get; init; }
    public string? RawJAModInfoJson { get; init; }

    public string EntryAssemblyPath =>
        string.IsNullOrWhiteSpace(AssemblyName)
            ? string.Empty
            : Path.Combine(FolderPath, AssemblyName);

    public string? JAModAssemblyFullPath =>
        string.IsNullOrWhiteSpace(JAModAssemblyPath)
            ? null
            : Path.Combine(FolderPath, JAModAssemblyPath);
}

internal static class PcModManifestJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

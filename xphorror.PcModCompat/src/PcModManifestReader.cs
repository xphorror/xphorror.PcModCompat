using System.Text.Json;

namespace Xphorror.PcModCompat;

public static class PcModManifestReader
{
    private static readonly HashSet<string> SystemDependencyIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "JALib",
        "UnityModManager",
        "0Harmony",
        "Harmony",
        "HarmonyLib"
    };

    public static bool TryRead(string folderPath, out PcModManifest manifest, out string? error)
    {
        manifest = null!;
        error = null;

        var infoPath = Path.Combine(folderPath, "Info.json");
        if (!File.Exists(infoPath))
            return false;

        try
        {
            var rawInfo = File.ReadAllText(infoPath);
            using var infoDoc = JsonDocument.Parse(rawInfo, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            var root = infoDoc.RootElement;
            var id = ReadString(root, "Id");
            if (string.IsNullOrWhiteSpace(id))
            {
                error = "Info.json does not contain Id";
                return false;
            }

            var assemblyName = ReadString(root, "AssemblyName");
            var entryMethod = ReadString(root, "EntryMethod") ?? string.Empty;
            var jamodPath = Path.Combine(folderPath, "JAModInfo.json");
            JAModInfo? jamodInfo = null;
            string? rawJAModInfo = null;

            if (File.Exists(jamodPath))
            {
                rawJAModInfo = File.ReadAllText(jamodPath);
                jamodInfo = JsonSerializer.Deserialize<JAModInfo>(rawJAModInfo, PcModManifestJson.Options);
            }

            var kind = jamodInfo != null || entryMethod.Contains("JAMod", StringComparison.OrdinalIgnoreCase)
                ? PcModKind.JAMod
                : PcModKind.UnityModManager;

            manifest = new PcModManifest
            {
                FolderPath = folderPath,
                Id = id,
                DisplayName = ReadString(root, "DisplayName") ?? id,
                Author = ReadString(root, "Author") ?? string.Empty,
                Version = ReadString(root, "Version") ?? "0.0.0",
                AssemblyName = assemblyName ?? string.Empty,
                EntryMethod = entryMethod,
                Kind = kind,
                JAModAssemblyPath = jamodInfo?.AssemblyPath,
                JAModClassName = jamodInfo?.ClassName,
                JAModLocalizationGid = jamodInfo?.Gid ?? -1,
                AssemblyRequireModPath = jamodInfo?.AssemblyRequireModPath ?? false,
                Requirements = FilterSystemDependencies(ReadStringArray(root, "Requirements")),
                LoadAfter = ReadStringArray(root, "LoadAfter"),
                RawInfoJson = rawInfo,
                RawJAModInfoJson = rawJAModInfo
            };

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static IReadOnlyList<string> FilterSystemDependencies(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0)
            return ids;

        return ids.Where(id => !SystemDependencyIds.Contains(id)).ToArray();
    }

    private static string? ReadString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                list.Add(item.GetString()!);
        }

        return list;
    }

    private sealed class JAModInfo
    {
        public string? AssemblyPath { get; set; }
        public string? ClassName { get; set; }
        public bool AssemblyRequireModPath { get; set; }
        public int Gid { get; set; } = -1;
    }
}

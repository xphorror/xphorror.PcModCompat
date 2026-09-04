using System.Text.Json.Serialization;

namespace StArray.ModManager.Manager;

/// <summary>System.Text.Json 源生成上下文，为 AOT 提供预编译序列化代码</summary>
[JsonSerializable(typeof(ModManagerConfig))]
[JsonSerializable(typeof(Dictionary<string, System.Text.Json.JsonElement>))]
[JsonSourceGenerationOptions(WriteIndented = true, IncludeFields = true)]
public partial class ModManagerJsonContext : JsonSerializerContext
{ }

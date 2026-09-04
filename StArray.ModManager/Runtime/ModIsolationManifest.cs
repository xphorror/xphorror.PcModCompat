using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StArray.ModManager.Runtime;

public enum ModIsolationCapabilityLevel
{
    Proven,
    Guarded,
    LegacyReadOnly,
    Unsupported
}

public enum ModStaticStateClassification
{
    SharedImmutable,
    DomainMutable,
    HostContribution,
    DirectLinkProviderState
}

public enum ModNativeCallClassification
{
    SharedStateless,
    DomainAwareHost,
    ProcessGlobal,
    UnsafeRaw
}

public sealed record ModAssemblyIdentity
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string ModuleVersionId { get; init; }
    public required string Sha256 { get; init; }
    public required string ApiSurfaceHash { get; init; }
    public string PublicKeyToken { get; init; } = string.Empty;
    public long FileSize { get; init; }
}

public sealed record ModSemanticPackIdentity
{
    public required string FormatVersion { get; init; }
    public required string GameVersion { get; init; }
    public required string PackSha256 { get; init; }
    public required string MethodStreamSha256 { get; init; }
    public string SourceTreeSha256 { get; init; } = string.Empty;
    public int SourceFileCount { get; init; }
    public IReadOnlyList<ModAssemblyIdentity> Assemblies { get; init; } = [];
}

public sealed record ModIsolationFeatureRecord
{
    public required string FeatureId { get; init; }
    public required ModIsolationCapabilityLevel Level { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public IReadOnlyList<string> Dependencies { get; init; } = [];
}

public sealed record ModIsolationStaticMemberRecord
{
    public required string MemberIdentity { get; init; }
    public required ModStaticStateClassification Classification { get; init; }
    public required int StaticSlotId { get; init; }
}

public sealed record ModIsolationDirectLinkRecord
{
    public required string ProviderId { get; init; }
    public required string ProviderAssemblyIdentity { get; init; }
    public required string ApiClosureHash { get; init; }
    public required string TypeClosureHash { get; init; }
    public IReadOnlyList<string> ReferencedMembers { get; init; } = [];
}

public sealed record ModIsolationDataSourceRecord
{
    public required string FeatureId { get; init; }
    public required string SourceKind { get; init; }
    public required string ProviderIdentity { get; init; }
    public required string SourceGeneration { get; init; }
    public required string SchemaHash { get; init; }
    public ModIsolationCapabilityLevel FallbackLevel { get; init; } =
        ModIsolationCapabilityLevel.Unsupported;
}

public sealed record ModIsolationNativeCallRecord
{
    public required string MemberIdentity { get; init; }
    public required string Library { get; init; }
    public required string EntryPoint { get; init; }
    public required ModNativeCallClassification Classification { get; init; }
    public required ModIsolationCapabilityLevel Level { get; init; }
}

public sealed record ModIsolationManifest
{
    public const string CurrentFormatVersion = "starray-mod-isolation-v1";

    public string FormatVersion { get; init; } = CurrentFormatVersion;
    public required string ModId { get; init; }
    public required string LoaderKind { get; init; }
    public required ModAssemblyIdentity OriginalAssembly { get; init; }
    public ModAssemblyIdentity? ShadowAssembly { get; init; }
    public ModSemanticPackIdentity? SemanticPack { get; init; }
    public IReadOnlyList<ModIsolationFeatureRecord> Features { get; init; } = [];
    public IReadOnlyList<ModIsolationStaticMemberRecord> StaticMembers { get; init; } = [];
    public IReadOnlyList<ModIsolationDirectLinkRecord> DirectLinks { get; init; } = [];
    public IReadOnlyList<ModIsolationDataSourceRecord> DataSources { get; init; } = [];
    public IReadOnlyList<ModIsolationNativeCallRecord> NativeCalls { get; init; } = [];

    public ModIsolationManifest NormalizeAndValidate()
    {
        if (!string.Equals(FormatVersion, CurrentFormatVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported isolation manifest format: {FormatVersion}.");
        }
        ValidateIdentity(ModId, nameof(ModId));
        ValidateIdentity(LoaderKind, nameof(LoaderKind));
        ValidateAssembly(OriginalAssembly, nameof(OriginalAssembly));
        if (ShadowAssembly != null)
            ValidateAssembly(ShadowAssembly, nameof(ShadowAssembly));
        if (SemanticPack != null)
            ValidateSemanticPack(SemanticPack);

        var features = Features
            .Select(feature => feature with
            {
                FeatureId = NormalizeIdentity(feature.FeatureId, "feature ID"),
                Evidence = NormalizeStrings(feature.Evidence),
                Dependencies = NormalizeStrings(feature.Dependencies)
            })
            .OrderBy(feature => feature.FeatureId, StringComparer.Ordinal)
            .ToArray();
        EnsureUnique(features.Select(feature => feature.FeatureId), "feature ID");

        var staticMembers = StaticMembers
            .Select(member =>
            {
                if (member.StaticSlotId < 0)
                    throw new InvalidDataException("Static slot IDs cannot be negative.");
                return member with
                {
                    MemberIdentity = NormalizeIdentity(
                        member.MemberIdentity,
                        "static member identity")
                };
            })
            .OrderBy(member => member.MemberIdentity, StringComparer.Ordinal)
            .ToArray();
        EnsureUnique(
            staticMembers.Select(member => member.MemberIdentity),
            "static member identity");
        EnsureUnique(
            staticMembers.Select(member => member.StaticSlotId.ToString()),
            "static slot ID");

        var directLinks = DirectLinks
            .Select(link => link with
            {
                ProviderId = NormalizeIdentity(link.ProviderId, "provider ID"),
                ProviderAssemblyIdentity = NormalizeIdentity(
                    link.ProviderAssemblyIdentity,
                    "provider assembly identity"),
                ApiClosureHash = NormalizeSha256(link.ApiClosureHash, "API closure hash"),
                TypeClosureHash = NormalizeSha256(link.TypeClosureHash, "type closure hash"),
                ReferencedMembers = NormalizeStrings(link.ReferencedMembers)
            })
            .OrderBy(link => link.ProviderId, StringComparer.Ordinal)
            .ThenBy(link => link.ProviderAssemblyIdentity, StringComparer.Ordinal)
            .ToArray();

        var dataSources = DataSources
            .Select(source => source with
            {
                FeatureId = NormalizeIdentity(source.FeatureId, "data source feature ID"),
                SourceKind = NormalizeIdentity(source.SourceKind, "data source kind"),
                ProviderIdentity = NormalizeIdentity(
                    source.ProviderIdentity,
                    "data source provider"),
                SourceGeneration = NormalizeIdentity(
                    source.SourceGeneration,
                    "data source generation"),
                SchemaHash = NormalizeSha256(source.SchemaHash, "data source schema hash")
            })
            .OrderBy(source => source.FeatureId, StringComparer.Ordinal)
            .ThenBy(source => source.SourceKind, StringComparer.Ordinal)
            .ThenBy(source => source.ProviderIdentity, StringComparer.Ordinal)
            .ToArray();

        var nativeCalls = NativeCalls
            .Select(call => call with
            {
                MemberIdentity = NormalizeIdentity(call.MemberIdentity, "native call member"),
                Library = NormalizeIdentity(call.Library, "native library"),
                EntryPoint = NormalizeIdentity(call.EntryPoint, "native entry point")
            })
            .OrderBy(call => call.MemberIdentity, StringComparer.Ordinal)
            .ToArray();
        EnsureUnique(
            nativeCalls.Select(call => call.MemberIdentity),
            "native call member");

        return this with
        {
            ModId = ModId.Trim(),
            LoaderKind = LoaderKind.Trim(),
            OriginalAssembly = NormalizeAssembly(OriginalAssembly),
            ShadowAssembly = ShadowAssembly is null ? null : NormalizeAssembly(ShadowAssembly),
            SemanticPack = SemanticPack is null ? null : NormalizeSemanticPack(SemanticPack),
            Features = features,
            StaticMembers = staticMembers,
            DirectLinks = directLinks,
            DataSources = dataSources,
            NativeCalls = nativeCalls
        };
    }

    public byte[] ToCanonicalJson()
    {
        var normalized = NormalizeAndValidate();
        return JsonSerializer.SerializeToUtf8Bytes(
            normalized,
            ModIsolationManifestJsonContext.Default.ModIsolationManifest);
    }

    public string ComputeManifestHash() =>
        Convert.ToHexString(SHA256.HashData(ToCanonicalJson())).ToLowerInvariant();

    public void WriteAtomic(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidDataException(
                            "Isolation manifest path has no parent directory.");
        Directory.CreateDirectory(directory);
        var bytes = ToCanonicalJson();
        var staging = fullPath + $".staging-{Environment.ProcessId:x}-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllBytes(staging, bytes);
            File.Move(staging, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }
    }

    public static ModIsolationManifest Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = File.ReadAllText(
            Path.GetFullPath(path),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        return (JsonSerializer.Deserialize(
                    json,
                    ModIsolationManifestJsonContext.Default.ModIsolationManifest)
                ?? throw new InvalidDataException("Isolation manifest is empty."))
            .NormalizeAndValidate();
    }

    private static ModAssemblyIdentity NormalizeAssembly(ModAssemblyIdentity assembly) =>
        assembly with
        {
            Name = assembly.Name.Trim(),
            Version = assembly.Version.Trim(),
            ModuleVersionId = Guid.Parse(assembly.ModuleVersionId).ToString("D"),
            Sha256 = NormalizeSha256(assembly.Sha256, "assembly SHA-256"),
            ApiSurfaceHash = NormalizeSha256(
                assembly.ApiSurfaceHash,
                "assembly API surface hash"),
            PublicKeyToken = assembly.PublicKeyToken.Trim().ToLowerInvariant()
        };

    private static ModSemanticPackIdentity NormalizeSemanticPack(
        ModSemanticPackIdentity pack) =>
        pack with
        {
            FormatVersion = pack.FormatVersion.Trim(),
            GameVersion = pack.GameVersion.Trim(),
            PackSha256 = NormalizeSha256(pack.PackSha256, "semantic pack hash"),
            MethodStreamSha256 = NormalizeSha256(
                pack.MethodStreamSha256,
                "semantic method stream hash"),
            SourceTreeSha256 = string.IsNullOrWhiteSpace(pack.SourceTreeSha256)
                ? string.Empty
                : NormalizeSha256(pack.SourceTreeSha256, "semantic source tree hash"),
            Assemblies = pack.Assemblies
                .Select(NormalizeAssembly)
                .OrderBy(assembly => assembly.Name, StringComparer.Ordinal)
                .ToArray()
        };

    private static void ValidateAssembly(ModAssemblyIdentity assembly, string name)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ValidateIdentity(assembly.Name, $"{name}.Name");
        ValidateIdentity(assembly.Version, $"{name}.Version");
        if (!Guid.TryParse(assembly.ModuleVersionId, out var mvid) || mvid == Guid.Empty)
            throw new InvalidDataException($"{name}.ModuleVersionId is invalid.");
        _ = NormalizeSha256(assembly.Sha256, $"{name}.Sha256");
        _ = NormalizeSha256(assembly.ApiSurfaceHash, $"{name}.ApiSurfaceHash");
        if (assembly.FileSize < 0)
            throw new InvalidDataException($"{name}.FileSize cannot be negative.");
    }

    private static void ValidateSemanticPack(ModSemanticPackIdentity pack)
    {
        ValidateIdentity(pack.FormatVersion, "semantic pack format");
        ValidateIdentity(pack.GameVersion, "semantic pack game version");
        _ = NormalizeSha256(pack.PackSha256, "semantic pack hash");
        _ = NormalizeSha256(pack.MethodStreamSha256, "semantic method stream hash");
        if (!string.IsNullOrWhiteSpace(pack.SourceTreeSha256))
            _ = NormalizeSha256(pack.SourceTreeSha256, "semantic source tree hash");
        if (pack.SourceFileCount < 0)
            throw new InvalidDataException("Semantic source file count cannot be negative.");
        foreach (var assembly in pack.Assemblies)
            ValidateAssembly(assembly, "SemanticPack.Assemblies");
        EnsureUnique(pack.Assemblies.Select(assembly => assembly.Name), "semantic assembly name");
    }

    private static IReadOnlyList<string> NormalizeStrings(IEnumerable<string> values) =>
        values
            .Select(value => NormalizeIdentity(value, "manifest list value"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private static string NormalizeIdentity(string value, string name)
    {
        ValidateIdentity(value, name);
        return value.Trim();
    }

    private static void ValidateIdentity(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"{name} cannot be empty.");
    }

    private static string NormalizeSha256(string value, string name)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"{name} is not a SHA-256 value.");
        return normalized;
    }

    private static void EnsureUnique(IEnumerable<string> values, string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value))
                throw new InvalidDataException($"Duplicate {name}: {value}.");
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ModIsolationManifest))]
internal sealed partial class ModIsolationManifestJsonContext : JsonSerializerContext;

internal static class ModIsolationManifestFactory
{
    internal static bool MatchesAssemblyIdentity(
        ModAssemblyIdentity expected,
        ModAssemblyIdentity actual)
        => string.Equals(expected.Name, actual.Name, StringComparison.Ordinal) &&
           string.Equals(expected.Version, actual.Version, StringComparison.Ordinal) &&
           string.Equals(
               expected.ModuleVersionId,
               actual.ModuleVersionId,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(expected.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase) &&
           expected.FileSize == actual.FileSize;

    internal static ModIsolationManifest CreateBootstrap(
        string modId,
        string loaderKind,
        string assemblyPath)
    {
        var identity = ReadAssemblyIdentity(assemblyPath);
        return new ModIsolationManifest
        {
            ModId = modId,
            LoaderKind = loaderKind,
            OriginalAssembly = identity,
            Features =
            [
                new ModIsolationFeatureRecord
                {
                    FeatureId = "bootstrap-managed-entry",
                    Level = ModIsolationCapabilityLevel.Guarded,
                    Evidence =
                    [
                        "entry assembly identity verified",
                        "runtime owner/generation domain guard active",
                        "full shadow rewrite isolation is pending"
                    ]
                }
            ]
        }.NormalizeAndValidate();
    }

    internal static ModAssemblyIdentity ReadAssemblyIdentity(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        var fullPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("MOD assembly was not found.", fullPath);

        string sha256;
        using (var hashStream = File.OpenRead(fullPath))
            sha256 = Convert.ToHexString(SHA256.HashData(hashStream)).ToLowerInvariant();

        var assemblyName = AssemblyName.GetAssemblyName(fullPath);
        Guid mvid;
        using (var stream = File.OpenRead(fullPath))
        using (var peReader = new PEReader(stream))
        {
            if (!peReader.HasMetadata)
                throw new InvalidDataException($"Managed assembly has no metadata: {fullPath}");
            var metadata = peReader.GetMetadataReader();
            var module = metadata.GetModuleDefinition();
            mvid = metadata.GetGuid(module.Mvid);
        }
        if (mvid == Guid.Empty)
            throw new InvalidDataException($"Managed assembly has no MVID: {fullPath}");

        return new ModAssemblyIdentity
        {
            Name = assemblyName.Name
                   ?? throw new InvalidDataException("Managed assembly has no simple name."),
            Version = assemblyName.Version?.ToString() ?? "0.0.0.0",
            ModuleVersionId = mvid.ToString("D"),
            Sha256 = sha256,
            // Bootstrap is conservative: any implementation change invalidates this identity.
            // The shadow rewriter replaces it with a public API surface hash.
            ApiSurfaceHash = sha256,
            PublicKeyToken = assemblyName.GetPublicKeyToken() is { Length: > 0 } token
                ? Convert.ToHexString(token).ToLowerInvariant()
                : string.Empty,
            FileSize = new FileInfo(fullPath).Length
        };
    }
}

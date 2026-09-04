using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StArray.ModManager.Runtime;

internal sealed record NativeModShadowAssemblyRecord
{
    public required string FileName { get; init; }
    public required ModAssemblyIdentity OriginalIdentity { get; init; }
    public required ModAssemblyIdentity ShadowIdentity { get; init; }
    public IReadOnlyList<NativeModShadowStaticSlotRecord> StaticSlots { get; init; } = [];
    public IReadOnlyList<NativeModShadowAsyncRewriteRecord> AsyncRewrites { get; init; } = [];
    public IReadOnlyList<NativeModShadowFileRewriteRecord> FileRewrites { get; init; } = [];
    public IReadOnlyList<NativeModShadowNetworkRewriteRecord> NetworkRewrites { get; init; } = [];
}

internal sealed record NativeModShadowPackageManifest
{
    public const string CurrentFormatVersion = "starray-native-shadow-v3";

    public string FormatVersion { get; init; } = CurrentFormatVersion;
    public required string CacheKey { get; init; }
    public required string RewriteAbi { get; init; }
    public required string EntryFileName { get; init; }
    public IReadOnlyList<NativeModShadowAssemblyRecord> Assemblies { get; init; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(NativeModShadowPackageManifest))]
internal sealed partial class NativeModShadowPackageJsonContext : JsonSerializerContext;

/// <summary>
/// Immutable, content-addressed execution copy of one Android Managed MOD assembly closure.
/// Source files are only read; execution always uses the verified package paths.
/// </summary>
internal sealed class NativeModShadowPackage
{
    private const string ManifestFileName = "shadow-package.json";
    private const string CompleteMarkerFileName = "complete.marker";
    private const int MaximumAssemblies = 256;
    private static readonly object PublishLock = new();
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private NativeModShadowPackage(
        string cacheKey,
        string rewriteAbi,
        string sourceDirectory,
        string packageDirectory,
        string entryAssemblyPath,
        ModAssemblyIdentity originalEntryIdentity,
        ModAssemblyIdentity entryIdentity,
        IReadOnlyList<NativeModShadowAssemblyRecord> assemblies,
        bool cacheHit)
    {
        CacheKey = cacheKey;
        RewriteAbi = rewriteAbi;
        SourceDirectory = sourceDirectory;
        PackageDirectory = packageDirectory;
        EntryAssemblyPath = entryAssemblyPath;
        OriginalEntryIdentity = originalEntryIdentity;
        EntryIdentity = entryIdentity;
        Assemblies = assemblies;
        CacheHit = cacheHit;
    }

    internal string CacheKey { get; }
    internal string RewriteAbi { get; }
    internal string SourceDirectory { get; }
    internal string PackageDirectory { get; }
    internal string EntryAssemblyPath { get; }
    internal ModAssemblyIdentity EntryIdentity { get; }
    internal ModAssemblyIdentity OriginalEntryIdentity { get; }
    internal IReadOnlyList<NativeModShadowAssemblyRecord> Assemblies { get; }
    internal bool CacheHit { get; }

    internal IReadOnlyDictionary<string, string> OriginalAssemblyLocations =>
        Assemblies.ToDictionary(
            record => record.OriginalIdentity.Name,
            record => Path.Combine(SourceDirectory, record.FileName),
            StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<ModIsolationStaticMemberRecord> StaticMembers =>
        Assemblies
            .SelectMany(record => record.StaticSlots)
            .DistinctBy(slot => slot.MemberIdentity, StringComparer.Ordinal)
            .OrderBy(slot => slot.MemberIdentity, StringComparer.Ordinal)
            .Select(slot => new ModIsolationStaticMemberRecord
            {
                MemberIdentity = slot.MemberIdentity,
                Classification = ModStaticStateClassification.DomainMutable,
                StaticSlotId = slot.StaticSlotId
            })
            .ToArray();

    internal IReadOnlyList<NativeModShadowAsyncRewriteRecord> AsyncRewrites =>
        Assemblies
            .SelectMany(record => record.AsyncRewrites)
            .OrderBy(record => record.MemberIdentity, StringComparer.Ordinal)
            .ThenBy(record => record.Kind, StringComparer.Ordinal)
            .ToArray();

    internal IReadOnlyList<NativeModShadowFileRewriteRecord> FileRewrites =>
        Assemblies
            .SelectMany(record => record.FileRewrites)
            .OrderBy(record => record.MemberIdentity, StringComparer.Ordinal)
            .ThenBy(record => record.Kind, StringComparer.Ordinal)
            .ToArray();

    internal IReadOnlyList<NativeModShadowNetworkRewriteRecord> NetworkRewrites =>
        Assemblies
            .SelectMany(record => record.NetworkRewrites)
            .OrderBy(record => record.MemberIdentity, StringComparer.Ordinal)
            .ThenBy(record => record.Kind, StringComparer.Ordinal)
            .ToArray();

    internal static NativeModShadowPackage Prepare(
        string cacheRoot,
        string modDirectory,
        string entryAssemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryAssemblyPath);

        var fullCacheRoot = Path.GetFullPath(cacheRoot);
        var fullModDirectory = Path.GetFullPath(modDirectory);
        var fullEntryPath = Path.GetFullPath(entryAssemblyPath);
        EnsureDirectChild(fullModDirectory, fullEntryPath, "entry assembly");
        if (!File.Exists(fullEntryPath))
            throw new FileNotFoundException("Native MOD entry assembly was not found.", fullEntryPath);

        var closure = BuildAssemblyClosure(fullModDirectory, fullEntryPath);
        var privateAssemblyPaths = closure.ToDictionary(
            candidate => candidate.Identity.Name,
            candidate => candidate.SourcePath,
            StringComparer.OrdinalIgnoreCase);
        var rewrite = NativeModShadowRewriteRuntime.Snapshot();
        var entry = closure.Single(candidate =>
            string.Equals(candidate.SourcePath, fullEntryPath, StringComparison.OrdinalIgnoreCase));
        var cacheKey = ComputeCacheKey(entry.FileName, rewrite.Abi, closure);
        var versionRoot = Path.Combine(
            fullCacheRoot,
            NativeModShadowPackageManifest.CurrentFormatVersion);
        var finalDirectory = Path.Combine(versionRoot, cacheKey);

        lock (PublishLock)
        {
            if (TryOpen(
                    finalDirectory,
                    fullModDirectory,
                    cacheKey,
                    rewrite.Abi,
                    cacheHit: true,
                    out var cached))
                return cached!;

            Directory.CreateDirectory(versionRoot);
            if (Directory.Exists(finalDirectory))
                Directory.Delete(finalDirectory, recursive: true);

            var stagingDirectory = Path.Combine(
                versionRoot,
                $".staging-{Environment.ProcessId:x}-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(stagingDirectory);
                var assemblyRecords = new List<NativeModShadowAssemblyRecord>(closure.Count);
                foreach (var candidate in closure)
                {
                    var destination = Path.Combine(stagingDirectory, candidate.FileName);
                    IReadOnlyList<NativeModShadowStaticSlotRecord> staticSlots = [];
                    IReadOnlyList<NativeModShadowAsyncRewriteRecord> asyncRewrites = [];
                    IReadOnlyList<NativeModShadowFileRewriteRecord> fileRewrites = [];
                    IReadOnlyList<NativeModShadowNetworkRewriteRecord> networkRewrites = [];
                    if (rewrite.Provider == null)
                    {
                        File.Copy(candidate.SourcePath, destination, overwrite: false);
                    }
                    else
                    {
                        var result = rewrite.Provider(new NativeModShadowRewriteRequest(
                            candidate.SourcePath,
                            destination,
                            privateAssemblyPaths));
                        if (result.Issues.Count != 0 || !File.Exists(destination))
                        {
                            throw new InvalidDataException(
                                $"Native MOD shadow rewrite failed for {candidate.SourcePath}: " +
                                string.Join("; ", result.Issues.Take(4)));
                        }
                        staticSlots = NormalizeStaticSlots(result.StaticSlots);
                        asyncRewrites = NormalizeAsyncRewrites(result.AsyncRewrites);
                        fileRewrites = NormalizeFileRewrites(result.FileRewrites);
                        networkRewrites = NormalizeNetworkRewrites(result.NetworkRewrites);
                    }
                    var sourceIdentity = ModIsolationManifestFactory.ReadAssemblyIdentity(
                        candidate.SourcePath);
                    if (!ModIsolationManifestFactory.MatchesAssemblyIdentity(
                            candidate.Identity,
                            sourceIdentity))
                    {
                        throw new InvalidDataException(
                            $"Native MOD assembly changed while publishing shadow package: " +
                            $"{candidate.SourcePath}.");
                    }
                    var shadowIdentity = ModIsolationManifestFactory.ReadAssemblyIdentity(destination);
                    assemblyRecords.Add(new NativeModShadowAssemblyRecord
                    {
                        FileName = candidate.FileName,
                        OriginalIdentity = candidate.Identity,
                        ShadowIdentity = shadowIdentity,
                        StaticSlots = staticSlots,
                        AsyncRewrites = asyncRewrites,
                        FileRewrites = fileRewrites,
                        NetworkRewrites = networkRewrites
                    });
                }

                ValidateStaticSlots(assemblyRecords.SelectMany(record => record.StaticSlots));
                ValidateAsyncRewrites(assemblyRecords.SelectMany(record => record.AsyncRewrites));
                ValidateFileRewrites(assemblyRecords.SelectMany(record => record.FileRewrites));
                ValidateNetworkRewrites(
                    assemblyRecords.SelectMany(record => record.NetworkRewrites));

                var manifest = new NativeModShadowPackageManifest
                {
                    CacheKey = cacheKey,
                    RewriteAbi = rewrite.Abi,
                    EntryFileName = entry.FileName,
                    Assemblies = assemblyRecords
                        .OrderBy(record => record.OriginalIdentity.Name, StringComparer.Ordinal)
                        .ToArray()
                };
                var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
                    manifest,
                    NativeModShadowPackageJsonContext.Default.NativeModShadowPackageManifest);
                File.WriteAllBytes(
                    Path.Combine(stagingDirectory, ManifestFileName),
                    manifestBytes);
                var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes))
                    .ToLowerInvariant();
                File.WriteAllText(
                    Path.Combine(stagingDirectory, CompleteMarkerFileName),
                    cacheKey + "\n" + manifestHash + "\n",
                    StrictUtf8);
                Directory.Move(stagingDirectory, finalDirectory);
            }
            finally
            {
                if (Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory, recursive: true);
            }

            if (!TryOpen(
                    finalDirectory,
                    fullModDirectory,
                    cacheKey,
                    rewrite.Abi,
                    cacheHit: false,
                    out var published))
                throw new InvalidDataException("Published Native MOD shadow package failed verification.");
            return published!;
        }
    }

    internal void Verify()
    {
        if (!TryOpen(
                PackageDirectory,
                SourceDirectory,
                CacheKey,
                RewriteAbi,
                CacheHit,
                out _))
            throw new InvalidDataException(
                $"Native MOD shadow package verification failed: {PackageDirectory}.");
    }

    private static bool TryOpen(
        string packageDirectory,
        string sourceDirectory,
        string expectedCacheKey,
        string expectedRewriteAbi,
        bool cacheHit,
        out NativeModShadowPackage? package)
    {
        package = null;
        try
        {
            var markerPath = Path.Combine(packageDirectory, CompleteMarkerFileName);
            var manifestPath = Path.Combine(packageDirectory, ManifestFileName);
            if (!File.Exists(markerPath) || !File.Exists(manifestPath))
                return false;
            var markerLines = File.ReadAllText(markerPath, StrictUtf8)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (markerLines.Length != 2 ||
                !string.Equals(markerLines[0], expectedCacheKey, StringComparison.Ordinal))
            {
                return false;
            }

            var manifestBytes = File.ReadAllBytes(manifestPath);
            _ = StrictUtf8.GetString(manifestBytes);
            var actualManifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes))
                .ToLowerInvariant();
            if (!string.Equals(markerLines[1], actualManifestHash, StringComparison.Ordinal))
                return false;
            var manifest = JsonSerializer.Deserialize(
                               manifestBytes,
                               NativeModShadowPackageJsonContext.Default
                                   .NativeModShadowPackageManifest)
                           ?? throw new InvalidDataException("Shadow package manifest is empty.");
            if (!string.Equals(
                    manifest.FormatVersion,
                    NativeModShadowPackageManifest.CurrentFormatVersion,
                    StringComparison.Ordinal) ||
                !string.Equals(manifest.CacheKey, expectedCacheKey, StringComparison.Ordinal) ||
                !string.Equals(manifest.RewriteAbi, expectedRewriteAbi, StringComparison.Ordinal) ||
                manifest.Assemblies.Count is <= 0 or > MaximumAssemblies)
            {
                return false;
            }

            EnsureSafeFileName(manifest.EntryFileName, "entry file");
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var records = manifest.Assemblies
                .OrderBy(record => record.OriginalIdentity.Name, StringComparer.Ordinal)
                .ToArray();
            ModAssemblyIdentity? entryIdentity = null;
            ModAssemblyIdentity? originalEntryIdentity = null;
            foreach (var record in records)
            {
                EnsureSafeFileName(record.FileName, "assembly file");
                _ = NormalizeStaticSlots(record.StaticSlots);
                _ = NormalizeAsyncRewrites(record.AsyncRewrites);
                _ = NormalizeFileRewrites(record.FileRewrites);
                _ = NormalizeNetworkRewrites(record.NetworkRewrites);
                if (!fileNames.Add(record.FileName) ||
                    !identities.Add(record.OriginalIdentity.Name) ||
                    !string.Equals(
                        record.OriginalIdentity.Name,
                        record.ShadowIdentity.Name,
                        StringComparison.Ordinal))
                    return false;
                var assemblyPath = Path.Combine(packageDirectory, record.FileName);
                if (!File.Exists(assemblyPath))
                    return false;
                var actual = ModIsolationManifestFactory.ReadAssemblyIdentity(assemblyPath);
                if (!ModIsolationManifestFactory.MatchesAssemblyIdentity(
                        record.ShadowIdentity,
                        actual))
                    return false;
                if (string.Equals(
                        record.FileName,
                        manifest.EntryFileName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    entryIdentity = actual;
                    originalEntryIdentity = record.OriginalIdentity;
                }
            }
            ValidateStaticSlots(records.SelectMany(record => record.StaticSlots));
            ValidateAsyncRewrites(records.SelectMany(record => record.AsyncRewrites));
            ValidateFileRewrites(records.SelectMany(record => record.FileRewrites));
            ValidateNetworkRewrites(records.SelectMany(record => record.NetworkRewrites));
            if (entryIdentity is null || originalEntryIdentity is null)
                return false;

            var entryPath = Path.Combine(packageDirectory, manifest.EntryFileName);
            package = new NativeModShadowPackage(
                expectedCacheKey,
                expectedRewriteAbi,
                sourceDirectory,
                packageDirectory,
                entryPath,
                originalEntryIdentity,
                entryIdentity,
                records,
                cacheHit);
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            BadImageFormatException or
            InvalidDataException or
            JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<AssemblyCandidate> BuildAssemblyClosure(
        string modDirectory,
        string entryAssemblyPath)
    {
        var catalog = new Dictionary<string, AssemblyCandidate>(StringComparer.OrdinalIgnoreCase);
        AssemblyCandidate? entry = null;
        foreach (var path in Directory.EnumerateFiles(modDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            AssemblyCandidate candidate;
            try
            {
                candidate = ReadCandidate(path);
            }
            catch (BadImageFormatException) when (!string.Equals(
                       Path.GetFullPath(path),
                       entryAssemblyPath,
                       StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!catalog.TryAdd(candidate.Identity.Name, candidate))
            {
                throw new InvalidDataException(
                    $"Native MOD directory contains duplicate managed assembly identity: " +
                    $"{candidate.Identity.Name}.");
            }
            if (string.Equals(candidate.SourcePath, entryAssemblyPath, StringComparison.OrdinalIgnoreCase))
                entry = candidate;
        }
        if (entry is null)
            throw new BadImageFormatException("Native MOD entry is not a managed assembly.");

        var closure = new Dictionary<string, AssemblyCandidate>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<AssemblyCandidate>();
        pending.Enqueue(entry);
        while (pending.TryDequeue(out var current))
        {
            if (!closure.TryAdd(current.Identity.Name, current))
                continue;
            if (closure.Count > MaximumAssemblies)
                throw new InvalidDataException("Native MOD managed dependency closure is too large.");

            foreach (var reference in current.References)
            {
                if (NativeModAssemblyLoadContext.IsSharedAssembly(reference) ||
                    IsAvailableFromDefaultContext(reference))
                {
                    continue;
                }
                if (!catalog.TryGetValue(reference, out var dependency))
                {
                    throw new FileNotFoundException(
                        $"Native MOD managed dependency is missing: {reference} " +
                        $"(referenced by {current.Identity.Name}).");
                }
                pending.Enqueue(dependency);
            }
        }

        var duplicateOutput = closure.Values
            .GroupBy(candidate => candidate.FileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateOutput != null)
            throw new InvalidDataException($"Duplicate shadow output file: {duplicateOutput.Key}.");
        return closure.Values
            .OrderBy(candidate => candidate.Identity.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<NativeModShadowStaticSlotRecord> NormalizeStaticSlots(
        IEnumerable<NativeModShadowStaticSlotRecord> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        var normalized = slots
            .Select(slot =>
            {
                if (slot.StaticSlotId < 0)
                    throw new InvalidDataException("Native MOD static slot ID cannot be negative.");
                if (string.IsNullOrWhiteSpace(slot.MemberIdentity))
                    throw new InvalidDataException("Native MOD static slot member identity cannot be empty.");
                return slot with { MemberIdentity = slot.MemberIdentity.Trim() };
            })
            .Distinct()
            .OrderBy(slot => slot.MemberIdentity, StringComparer.Ordinal)
            .ToArray();
        ValidateStaticSlots(normalized);
        return normalized;
    }

    private static void ValidateStaticSlots(
        IEnumerable<NativeModShadowStaticSlotRecord> slots)
    {
        var identities = new Dictionary<string, int>(StringComparer.Ordinal);
        var slotIds = new Dictionary<int, string>();
        foreach (var slot in slots)
        {
            if (slot.StaticSlotId < 0 || string.IsNullOrWhiteSpace(slot.MemberIdentity))
                throw new InvalidDataException("Native MOD static slot descriptor is invalid.");
            if (identities.TryGetValue(slot.MemberIdentity, out var existingId) &&
                existingId != slot.StaticSlotId)
            {
                throw new InvalidDataException(
                    $"Native MOD static member maps to multiple slots: {slot.MemberIdentity}.");
            }
            if (slotIds.TryGetValue(slot.StaticSlotId, out var existingIdentity) &&
                !string.Equals(existingIdentity, slot.MemberIdentity, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Native MOD static slot collision {slot.StaticSlotId}: " +
                    $"{existingIdentity} / {slot.MemberIdentity}.");
            }
            identities[slot.MemberIdentity] = slot.StaticSlotId;
            slotIds[slot.StaticSlotId] = slot.MemberIdentity;
        }
    }

    private static IReadOnlyList<NativeModShadowAsyncRewriteRecord> NormalizeAsyncRewrites(
        IEnumerable<NativeModShadowAsyncRewriteRecord> rewrites)
    {
        ArgumentNullException.ThrowIfNull(rewrites);
        var normalized = rewrites
            .Select(rewrite => rewrite with
            {
                MemberIdentity = rewrite.MemberIdentity?.Trim() ?? string.Empty,
                Kind = rewrite.Kind?.Trim() ?? string.Empty
            })
            .OrderBy(rewrite => rewrite.MemberIdentity, StringComparer.Ordinal)
            .ThenBy(rewrite => rewrite.Kind, StringComparer.Ordinal)
            .ToArray();
        ValidateAsyncRewrites(normalized);
        return normalized;
    }

    private static void ValidateAsyncRewrites(
        IEnumerable<NativeModShadowAsyncRewriteRecord> rewrites)
    {
        var identities = new HashSet<(string MemberIdentity, string Kind)>();
        foreach (var rewrite in rewrites)
        {
            if (string.IsNullOrWhiteSpace(rewrite.MemberIdentity) ||
                string.IsNullOrWhiteSpace(rewrite.Kind) ||
                rewrite.RewriteCount <= 0)
            {
                throw new InvalidDataException("Native MOD async rewrite proof is invalid.");
            }
            if (!identities.Add((rewrite.MemberIdentity, rewrite.Kind)))
            {
                throw new InvalidDataException(
                    $"Duplicate Native MOD async rewrite proof: " +
                    $"{rewrite.MemberIdentity}/{rewrite.Kind}.");
            }
        }
    }

    private static IReadOnlyList<NativeModShadowFileRewriteRecord> NormalizeFileRewrites(
        IEnumerable<NativeModShadowFileRewriteRecord> rewrites)
    {
        ArgumentNullException.ThrowIfNull(rewrites);
        var normalized = rewrites
            .Select(rewrite => rewrite with
            {
                MemberIdentity = rewrite.MemberIdentity?.Trim() ?? string.Empty,
                Kind = rewrite.Kind?.Trim() ?? string.Empty
            })
            .OrderBy(rewrite => rewrite.MemberIdentity, StringComparer.Ordinal)
            .ThenBy(rewrite => rewrite.Kind, StringComparer.Ordinal)
            .ToArray();
        ValidateFileRewrites(normalized);
        return normalized;
    }

    private static void ValidateFileRewrites(
        IEnumerable<NativeModShadowFileRewriteRecord> rewrites)
    {
        var identities = new HashSet<(string MemberIdentity, string Kind)>();
        foreach (var rewrite in rewrites)
        {
            if (string.IsNullOrWhiteSpace(rewrite.MemberIdentity) ||
                string.IsNullOrWhiteSpace(rewrite.Kind) ||
                rewrite.RewriteCount <= 0)
            {
                throw new InvalidDataException(
                    "Native MOD filesystem rewrite proof is invalid.");
            }
            if (!identities.Add((rewrite.MemberIdentity, rewrite.Kind)))
            {
                throw new InvalidDataException(
                    "Duplicate Native MOD filesystem rewrite proof: " +
                    $"{rewrite.MemberIdentity}/{rewrite.Kind}.");
            }
        }
    }

    private static IReadOnlyList<NativeModShadowNetworkRewriteRecord> NormalizeNetworkRewrites(
        IEnumerable<NativeModShadowNetworkRewriteRecord> rewrites)
    {
        ArgumentNullException.ThrowIfNull(rewrites);
        var normalized = rewrites
            .Select(rewrite => rewrite with
            {
                MemberIdentity = rewrite.MemberIdentity?.Trim() ?? string.Empty,
                Kind = rewrite.Kind?.Trim() ?? string.Empty
            })
            .OrderBy(rewrite => rewrite.MemberIdentity, StringComparer.Ordinal)
            .ThenBy(rewrite => rewrite.Kind, StringComparer.Ordinal)
            .ToArray();
        ValidateNetworkRewrites(normalized);
        return normalized;
    }

    private static void ValidateNetworkRewrites(
        IEnumerable<NativeModShadowNetworkRewriteRecord> rewrites)
    {
        var identities = new HashSet<(string MemberIdentity, string Kind)>();
        foreach (var rewrite in rewrites)
        {
            if (string.IsNullOrWhiteSpace(rewrite.MemberIdentity) ||
                string.IsNullOrWhiteSpace(rewrite.Kind) ||
                rewrite.RewriteCount <= 0)
            {
                throw new InvalidDataException(
                    "Native MOD network rewrite proof is invalid.");
            }
            if (!identities.Add((rewrite.MemberIdentity, rewrite.Kind)))
            {
                throw new InvalidDataException(
                    "Duplicate Native MOD network rewrite proof: " +
                    $"{rewrite.MemberIdentity}/{rewrite.Kind}.");
            }
        }
    }

    private static AssemblyCandidate ReadCandidate(string path)
    {
        var fullPath = Path.GetFullPath(path);
        using var stream = File.OpenRead(fullPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
            throw new BadImageFormatException("File has no managed metadata.", fullPath);
        var metadata = peReader.GetMetadataReader();
        if (!metadata.IsAssembly)
            throw new BadImageFormatException("Managed module is not an assembly.", fullPath);
        var references = metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var fileName = Path.GetFileName(fullPath);
        EnsureSafeFileName(fileName, "source assembly");
        return new AssemblyCandidate(
            fullPath,
            fileName,
            ModIsolationManifestFactory.ReadAssemblyIdentity(fullPath),
            references);
    }

    private static string ComputeCacheKey(
        string entryFileName,
        string rewriteAbi,
        IReadOnlyList<AssemblyCandidate> closure)
    {
        var builder = new StringBuilder();
        builder.AppendLine(NativeModShadowPackageManifest.CurrentFormatVersion);
        builder.Append("rewrite|").AppendLine(rewriteAbi);
        builder.Append("entry|").AppendLine(entryFileName);
        foreach (var candidate in closure.OrderBy(
                     candidate => candidate.Identity.Name,
                     StringComparer.Ordinal))
        {
            var identity = candidate.Identity;
            builder.Append("assembly|")
                .Append(candidate.FileName).Append('|')
                .Append(identity.Name).Append('|')
                .Append(identity.Version).Append('|')
                .Append(identity.ModuleVersionId).Append('|')
                .Append(identity.Sha256).Append('|')
                .Append(identity.FileSize).Append('\n');
        }
        return Convert.ToHexString(
                SHA256.HashData(StrictUtf8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static bool IsAvailableFromDefaultContext(string assemblyName)
    {
        if (AssemblyLoadContext.Default.Assemblies.Any(assembly =>
                string.Equals(
                    assembly.GetName().Name,
                    assemblyName,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var candidate = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        if (!File.Exists(candidate))
            return false;
        try
        {
            return string.Equals(
                AssemblyName.GetAssemblyName(candidate).Name,
                assemblyName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static void EnsureDirectChild(string directory, string path, string description)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Native MOD {description} must be a direct child of its directory.");
    }

    private static void EnsureSafeFileName(string fileName, string description)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(Path.GetExtension(fileName), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Invalid Native MOD {description}: {fileName}.");
        }
    }

    private sealed record AssemblyCandidate(
        string SourcePath,
        string FileName,
        ModAssemblyIdentity Identity,
        IReadOnlyList<string> References);
}

using System.Security.Cryptography;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Xphorror.PcModCompat.Resources;

/// <summary>
/// Import-time UnityFS indexer. Never loads MOD assemblies and never creates Unity objects.
/// </summary>
public static class UnityBundleIndexer
{
    public const string TargetUnityVersion = "6000.3.10f1";

    public static ResourceCandidateIndex IndexFile(string path, bool allowForceNonUnity6000 = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Unity bundle not found.", fullPath);

        using var sourceStream = File.OpenRead(fullPath);
        var fileSize = sourceStream.Length;
        var sha = Convert.ToHexString(SHA256.HashData(sourceStream)).ToLowerInvariant();
        sourceStream.Position = 0;
        var headerBytes = new byte[checked((int)Math.Min(fileSize, 4096))];
        sourceStream.ReadExactly(headerBytes);
        var platform = InferPlatformHint(fullPath);
        var headerVersion = TryReadUnityVersionFromHeader(headerBytes) ?? string.Empty;
        var gate = ClassifyUnityVersion(headerVersion);
        var policy = DecideLoadPolicy(gate, platform, allowForceNonUnity6000);
        var warnings = new List<string>();

        if (gate == UnityVersionGate.Controlled)
            warnings.Add($"Unity version {headerVersion} is outside 6000.3.x; controlled load only.");
        if (gate == UnityVersionGate.ForcedOnly)
            warnings.Add($"Unity version {headerVersion} is not Unity 6000; auto-load is forbidden.");
        if (gate == UnityVersionGate.Auto && platform != BundlePlatformHint.Android)
            warnings.Add($"{platform} bundle requires a controlled Android trial load even though its Unity version is compatible.");
        if (string.IsNullOrWhiteSpace(headerVersion))
            warnings.Add("Unity version was not readable from the UnityFS header.");

        AssetsManager? manager = null;
        try
        {
            sourceStream.Position = 0;
            manager = new AssetsManager();
            var bundle = manager.LoadBundleFile(sourceStream, fullPath);
            var directory = bundle.file.BlockAndDirInfo.DirectoryInfos
                .Select(entry => entry.Name ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            var assets = new List<ResourceAssetEntry>();
            var hasTypeTree = false;
            for (var i = 0; i < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
            {
                var dirName = bundle.file.BlockAndDirInfo.DirectoryInfos[i].Name ?? string.Empty;
                if (string.IsNullOrWhiteSpace(dirName))
                    continue;
                // Serialized asset files inside UnityFS usually lack an extension.
                if (dirName.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) ||
                    dirName.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
                    continue;

                AssetsFileInstance? assetsFile = null;
                try
                {
                    assetsFile = manager.LoadAssetsFileFromBundle(bundle, i, false);
                }
                catch
                {
                    continue;
                }

                if (assetsFile?.file == null)
                    continue;

                hasTypeTree |= assetsFile.file.Metadata.TypeTreeEnabled;
                foreach (var info in assetsFile.file.AssetInfos)
                {
                    string typeName = ((AssetClassID)info.TypeId).ToString();
                    string name = string.Empty;
                    try
                    {
                        var baseField = manager.GetBaseField(
                            assetsFile,
                            info,
                            AssetReadFlags.None);
                        if (baseField != null)
                        {
                            if (!string.IsNullOrWhiteSpace(baseField.TypeName))
                                typeName = baseField.TypeName;
                            name = baseField["m_Name"]?.AsString ?? string.Empty;
                        }
                    }
                    catch
                    {
                        // Keep enum/type-id fallback when type tree/class database is unavailable.
                    }

                    assets.Add(new ResourceAssetEntry
                    {
                        Name = name,
                        TypeName = typeName,
                        PathId = info.PathId,
                        TypeId = info.TypeId,
                        Container = dirName,
                        AssetsFileName = assetsFile.name ?? dirName
                    });
                }
            }

            if (assets.Count == 0)
                warnings.Add("No serialized assets were indexed from this UnityFS container.");

            return new ResourceCandidateIndex
            {
                SourcePath = fullPath,
                FileName = GetPortableFileName(fullPath),
                PlatformHint = platform,
                UnityVersion = string.IsNullOrWhiteSpace(headerVersion) ? "unknown" : headerVersion,
                VersionGate = gate,
                LoadPolicy = policy,
                FileSize = fileSize,
                Sha256Hex = sha,
                HasEmbeddedTypeTree = hasTypeTree,
                IndexSucceeded = true,
                DirectoryEntries = directory,
                Assets = assets
                    .OrderBy(asset => asset.Name, StringComparer.Ordinal)
                    .ThenBy(asset => asset.TypeName, StringComparer.Ordinal)
                    .ThenBy(asset => asset.PathId)
                    .ToArray(),
                Warnings = warnings
            };
        }
        catch (Exception ex)
        {
            return new ResourceCandidateIndex
            {
                SourcePath = fullPath,
                FileName = GetPortableFileName(fullPath),
                PlatformHint = platform,
                UnityVersion = string.IsNullOrWhiteSpace(headerVersion) ? "unknown" : headerVersion,
                VersionGate = gate,
                LoadPolicy = BundleLoadPolicy.IndexOnly,
                FileSize = fileSize,
                Sha256Hex = sha,
                HasEmbeddedTypeTree = false,
                IndexSucceeded = false,
                IndexError = ex.GetType().Name + ": " + ex.Message,
                Warnings = warnings.Append("Index failed: " + ex.Message).ToArray()
            };
        }
        finally
        {
            try
            {
                manager?.UnloadAll(true);
            }
            catch
            {
                // Index failure diagnostics are more useful than a secondary cleanup exception.
            }
        }
    }

    public static IReadOnlyList<ResourceCandidateIndex> IndexModFolder(
        string modFolder,
        bool allowForceNonUnity6000 = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);
        var root = Path.GetFullPath(modFolder);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(root);

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(IsLikelyUnityBundlePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return files.Select(path => IndexFile(path, allowForceNonUnity6000)).ToArray();
    }

    public static BundlePlatformHint InferPlatformHint(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.Contains("/Linux/", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/Linux", StringComparison.OrdinalIgnoreCase))
            return BundlePlatformHint.Linux;
        if (normalized.Contains("/Mac/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/OSX/", StringComparison.OrdinalIgnoreCase))
            return BundlePlatformHint.Mac;
        if (normalized.Contains("/Android/", StringComparison.OrdinalIgnoreCase))
            return BundlePlatformHint.Android;
        if (normalized.Contains("/Windows/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/StandaloneWindows", StringComparison.OrdinalIgnoreCase))
            return BundlePlatformHint.Windows;

        // Root-level Jipper candidates without a platform directory are treated as Windows PC source.
        return BundlePlatformHint.Windows;
    }

    public static string GetPortableFileName(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? normalized : normalized[(separator + 1)..];
    }

    public static UnityVersionGate ClassifyUnityVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version == "unknown")
            return UnityVersionGate.Unknown;

        if (version.StartsWith("6000.3.", StringComparison.Ordinal))
            return UnityVersionGate.Auto;
        if (version.StartsWith("6000.", StringComparison.Ordinal))
            return UnityVersionGate.Controlled;
        return UnityVersionGate.ForcedOnly;
    }

    public static BundleLoadPolicy DecideLoadPolicy(
        UnityVersionGate gate,
        BundlePlatformHint platform,
        bool allowForceNonUnity6000)
        => gate switch
        {
            UnityVersionGate.Auto when platform == BundlePlatformHint.Android => BundleLoadPolicy.AutoLoad,
            UnityVersionGate.Auto => BundleLoadPolicy.ControlledLoad,
            UnityVersionGate.Controlled => BundleLoadPolicy.ControlledLoad,
            UnityVersionGate.ForcedOnly => allowForceNonUnity6000
                ? BundleLoadPolicy.ForceRequired
                : BundleLoadPolicy.Rejected,
            _ => BundleLoadPolicy.IndexOnly
        };

    public static string? TryReadUnityVersionFromHeader(ReadOnlySpan<byte> data)
    {
        // UnityFS\0 + generation(4) + playerVersion cstring + engineVersion cstring
        if (data.Length < 16)
            return null;
        if (!(data[0] == (byte)'U' && data[1] == (byte)'n' && data[2] == (byte)'i' &&
              data[3] == (byte)'t' && data[4] == (byte)'y' && data[5] == (byte)'F' &&
              data[6] == (byte)'S' && data[7] == 0))
            return null;

        var offset = 12;
        if (!TryReadCString(data, ref offset, out _))
            return null;
        if (!TryReadCString(data, ref offset, out var engineVersion))
            return null;
        return engineVersion;
    }

    private static bool TryReadCString(ReadOnlySpan<byte> data, ref int offset, out string value)
    {
        value = string.Empty;
        if (offset < 0 || offset >= data.Length)
            return false;
        var start = offset;
        while (offset < data.Length && data[offset] != 0)
            offset++;
        if (offset >= data.Length)
            return false;
        value = Encoding.UTF8.GetString(data.Slice(start, offset - start));
        offset++;
        return true;
    }

    private static bool IsLikelyUnityBundlePath(string path)
    {
        var name = GetPortableFileName(path);
        if (string.IsNullOrWhiteSpace(name))
            return false;
        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.Contains($"{Path.DirectorySeparatorChar}.pccompat{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
            path.Contains($"{Path.AltDirectorySeparatorChar}.pccompat{Path.AltDirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[64];
            var read = stream.Read(header);
            if (read < 16)
                return false;
            return header[0] == (byte)'U' && header[1] == (byte)'n' && header[2] == (byte)'i' &&
                   header[3] == (byte)'t' && header[4] == (byte)'y' && header[5] == (byte)'F' &&
                   header[6] == (byte)'S' && header[7] == 0;
        }
        catch
        {
            return false;
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using AssetsTools.NET.Extra;

namespace Xphorror.PcModCompat.Resources;

/// <summary>
/// Shared input identity for offline tooling and the Android resource compiler cache.
/// </summary>
public static class ResourceCompileInputFingerprint
{
    public const string FormatVersion = "pccompat-resource-compile-cache-v1";

    public static string Compute(
        string modId,
        string modFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);
        var root = Path.GetFullPath(modFolder);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(root);

        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(aggregate, FormatVersion);
        Append(aggregate, ResourceCompiler.CompilerRevision);
        Append(aggregate, ResourceRecipeBinary.FormatVersion);
        Append(aggregate, ResourceIrBinary.FormatVersion);
        Append(aggregate, ResourceIrCompiler.CompilerRevision);
        Append(aggregate, UnityBundleIndexer.TargetUnityVersion);
        Append(aggregate, typeof(AssetsManager).Assembly.GetName().Version?.ToString() ?? "unknown");
        Append(aggregate, modId);

        foreach (var path in EnumerateCompilerInputs(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var info = new FileInfo(path);
            Append(aggregate, relative);
            Append(aggregate, info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            aggregate.AppendData(HashFile(path, cancellationToken));
        }

        return Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
    }

    public static string BuildCompilerMarker(string inputFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFingerprint);
        if (inputFingerprint.Length != 64 ||
            inputFingerprint.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Resource compile input fingerprint must be a SHA-256 hex string.",
                nameof(inputFingerprint));
        }
        return string.Join(
                   '\n',
                   FormatVersion,
                   ResourceIrCompiler.CompilerRevision,
                   inputFingerprint.ToLowerInvariant()) + "\n";
    }

    private static IEnumerable<string> EnumerateCompilerInputs(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(root, path);
            if (IsUnderGeneratedDirectory(relative))
                continue;
            var directory = Path.GetDirectoryName(relative);
            if (string.IsNullOrEmpty(directory) &&
                (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                 Path.GetFileName(path).Equals(
                     ResourceIrCompiler.AliasFileName,
                     StringComparison.OrdinalIgnoreCase)))
            {
                yield return path;
                continue;
            }
            if (HasUnityFsHeader(path))
                yield return path;
        }
    }

    private static bool IsUnderGeneratedDirectory(string relative)
    {
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment =>
            segment.Equals(".pccompat", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasUnityFsHeader(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[8];
            return stream.Read(header) == header.Length &&
                   header.SequenceEqual("UnityFS\0"u8);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] HashFile(string path, CancellationToken cancellationToken)
    {
        using var stream = File.Open(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return hash.GetHashAndReset();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(bytes);
        hash.AppendData([0]);
    }
}

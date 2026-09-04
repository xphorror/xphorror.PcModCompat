using System.Security.Cryptography;
using System.Text;
using Xphorror.PcModCompat;
using Xphorror.PcModCompat.Resources;

namespace StArray.ModManager.Android.PcCompat;

internal sealed record PcCompatResourceArtifactSet
{
    public required string Directory { get; init; }
    public required string RecipePath { get; init; }
    public required string ResourceIrPath { get; init; }
    public required string PayloadDirectory { get; init; }
    public required string CompilerMarkerPath { get; init; }
    public required string ReportPath { get; init; }

    public static PcCompatResourceArtifactSet ForDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        return new PcCompatResourceArtifactSet
        {
            Directory = full,
            RecipePath = Path.Combine(full, "resource_recipe.bin"),
            ResourceIrPath = Path.Combine(full, "resource_ir.bin"),
            PayloadDirectory = Path.Combine(full, "resource_ir_blobs"),
            CompilerMarkerPath = Path.Combine(full, ResourceIrCompiler.CacheMarkerFileName),
            ReportPath = Path.Combine(full, "resource_report.json")
        };
    }
}

/// <summary>
/// Stable content-addressed cache for the expensive UnityFS index and resource IR extraction.
/// It lives beside imported MOD folders so deleting/reimporting a package does not discard work.
/// </summary>
internal static class PcCompatResourceCompileCache
{
    internal const string FormatVersion = ResourceCompileInputFingerprint.FormatVersion;
    internal const string CompleteMarkerFileName = "complete.marker";
    private const int RetainedEntryCount = 3;
    private static readonly object FinalizeGate = new();
    private static long s_tempSequence;

    internal static string ComputeInputFingerprint(
        PcModManifest manifest,
        CancellationToken cancellationToken)
        => ResourceCompileInputFingerprint.Compute(
            manifest.Id,
            manifest.FolderPath,
            cancellationToken);

    internal static string BuildCompilerMarker(string inputFingerprint)
        => ResourceCompileInputFingerprint.BuildCompilerMarker(inputFingerprint);

    internal static PcCompatResourceArtifactSet GetEntry(
        PcModManifest manifest,
        string inputFingerprint)
        => PcCompatResourceArtifactSet.ForDirectory(
            Path.Combine(GetCacheRoot(manifest), inputFingerprint));

    internal static bool IsStructurallyComplete(
        PcCompatResourceArtifactSet entry,
        string inputFingerprint)
    {
        try
        {
            return File.Exists(Path.Combine(entry.Directory, CompleteMarkerFileName)) &&
                   File.Exists(entry.RecipePath) &&
                   File.Exists(entry.ResourceIrPath) &&
                   File.Exists(entry.ReportPath) &&
                   File.Exists(entry.CompilerMarkerPath) &&
                   File.ReadAllText(entry.CompilerMarkerPath).Equals(
                       BuildCompilerMarker(inputFingerprint),
                       StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    internal static void Restore(
        PcCompatResourceArtifactSet source,
        PcCompatResourceArtifactSet destination,
        CancellationToken cancellationToken)
    {
        var stage = PcCompatResourceArtifactSet.ForDirectory(
            destination.Directory + ".resource-restore-" + NextSuffix());
        try
        {
            CopyArtifacts(source, stage, cancellationToken);
            Directory.CreateDirectory(destination.Directory);
            File.Move(stage.ReportPath, destination.ReportPath, overwrite: true);
            File.Move(stage.RecipePath, destination.RecipePath, overwrite: true);
            if (Directory.Exists(destination.PayloadDirectory))
                Directory.Delete(destination.PayloadDirectory, recursive: true);
            Directory.Move(stage.PayloadDirectory, destination.PayloadDirectory);
            File.Move(stage.ResourceIrPath, destination.ResourceIrPath, overwrite: true);
            File.Move(stage.CompilerMarkerPath, destination.CompilerMarkerPath, overwrite: true);
        }
        finally
        {
            if (Directory.Exists(stage.Directory))
                Directory.Delete(stage.Directory, recursive: true);
        }
    }

    internal static void Publish(
        PcModManifest manifest,
        string inputFingerprint,
        PcCompatResourceArtifactSet source,
        CancellationToken cancellationToken)
    {
        var final = GetEntry(manifest, inputFingerprint);
        if (IsStructurallyComplete(final, inputFingerprint))
        {
            Directory.SetLastWriteTimeUtc(final.Directory, DateTime.UtcNow);
            Prune(manifest, final.Directory);
            return;
        }

        var root = GetCacheRoot(manifest);
        Directory.CreateDirectory(root);
        var stage = PcCompatResourceArtifactSet.ForDirectory(
            Path.Combine(root, $".tmp-{inputFingerprint}-{NextSuffix()}"));
        try
        {
            CopyArtifacts(source, stage, cancellationToken);
            File.WriteAllText(
                Path.Combine(stage.Directory, CompleteMarkerFileName),
                inputFingerprint + "\n",
                new UTF8Encoding(false));

            lock (FinalizeGate)
            {
                if (IsStructurallyComplete(final, inputFingerprint))
                    return;
                if (Directory.Exists(final.Directory))
                    Directory.Delete(final.Directory, recursive: true);
                Directory.Move(stage.Directory, final.Directory);
            }
        }
        finally
        {
            if (Directory.Exists(stage.Directory))
                Directory.Delete(stage.Directory, recursive: true);
        }
        Prune(manifest, final.Directory);
    }

    internal static void Invalidate(PcCompatResourceArtifactSet entry)
    {
        try
        {
            if (Directory.Exists(entry.Directory))
                Directory.Delete(entry.Directory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void Prune(PcModManifest manifest, string currentDirectory)
    {
        var root = GetCacheRoot(manifest);
        if (!Directory.Exists(root))
            return;

        var abandonedBefore = DateTime.UtcNow.AddMinutes(-10);
        foreach (var temporary in Directory.EnumerateDirectories(root, ".tmp-*"))
        {
            if (Directory.GetLastWriteTimeUtc(temporary) >= abandonedBefore)
                continue;
            try { Directory.Delete(temporary, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var complete = new List<DirectoryInfo>();
        foreach (var directory in Directory.EnumerateDirectories(root)
                     .Where(path => !Path.GetFileName(path).StartsWith(".tmp-", StringComparison.Ordinal)))
        {
            var marker = Path.Combine(directory, CompleteMarkerFileName);
            if (!File.Exists(marker))
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                continue;
            }
            complete.Add(new DirectoryInfo(directory));
        }

        foreach (var stale in complete
                     .OrderByDescending(item => PathsEqual(item.FullName, currentDirectory))
                     .ThenByDescending(item => item.LastWriteTimeUtc)
                     .Skip(RetainedEntryCount))
        {
            try { stale.Delete(recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static string GetCacheRoot(PcModManifest manifest)
    {
        var modDirectory = Path.GetFullPath(manifest.FolderPath);
        var modsRoot = Directory.GetParent(modDirectory)?.FullName ?? modDirectory;
        return Path.Combine(
            modsRoot,
            "compiled",
            SanitizePathSegment(manifest.Id),
            "resource-compile");
    }

    private static void CopyArtifacts(
        PcCompatResourceArtifactSet source,
        PcCompatResourceArtifactSet destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination.Directory);
        CopyFile(source.ReportPath, destination.ReportPath, cancellationToken);
        CopyFile(source.RecipePath, destination.RecipePath, cancellationToken);
        CopyFile(source.ResourceIrPath, destination.ResourceIrPath, cancellationToken);
        CopyFile(source.CompilerMarkerPath, destination.CompilerMarkerPath, cancellationToken);
        Directory.CreateDirectory(destination.PayloadDirectory);
        if (!Directory.Exists(source.PayloadDirectory))
            return;
        foreach (var path in Directory.EnumerateFiles(
                     source.PayloadDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(
                destination.PayloadDirectory,
                Path.GetRelativePath(source.PayloadDirectory, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CopyFile(path, target, cancellationToken);
        }
    }

    private static void CopyFile(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    private static string NextSuffix()
        => $"{Environment.ProcessId:x}-{Environment.CurrentManagedThreadId:x}-" +
           $"{Environment.TickCount64:x}-{Interlocked.Increment(ref s_tempSequence):x}";

    private static bool PathsEqual(string left, string right)
        => Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(
                Path.GetFullPath(right).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

    private static string SanitizePathSegment(string value)
    {
        var sanitized = new string(value
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_')
            .Take(128)
            .ToArray())
            .Trim(' ', '.');
        var reserved = sanitized.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                       sanitized.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                       sanitized.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                       sanitized.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                       (sanitized.Length == 4 &&
                        (sanitized.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                         sanitized.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                        sanitized[3] is >= '1' and <= '9');
        if (!string.IsNullOrWhiteSpace(sanitized) &&
            sanitized is not "." and not ".." &&
            !reserved)
            return sanitized;
        return "mod_" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..8];
    }
}

using StArray.ModManager.Runtime;

namespace Xphorror.PcModCompat;

/// <summary>
/// Per-MOD filesystem roots for a PcCompat MOD session.
/// </summary>
public sealed record PcCompatModPathRoots
{
    /// <summary>MOD directory. Read-only package layer: rewritten assemblies execute from the managed cache.</summary>
    public required string InstallRoot { get; init; }

    public required string ConfigRoot { get; init; }
    public required string CacheRoot { get; init; }
    public required string LogRoot { get; init; }
    public required string TempRoot { get; init; }

    /// <summary>
    /// Owner-scoped VFS overlay: every write aimed at the install root lands here with the
    /// same relative layout, and reads prefer it over the package layer. Legacy files the MOD
    /// shipped (or wrote before isolation) stay readable in place — no migration copy.
    /// </summary>
    public required string DataOverlayRoot { get; init; }

    /// <summary>Official game resource directories: readable by any MOD, never writable.</summary>
    public IReadOnlyList<string> SharedReadOnlyRoots { get; init; } = [];

    internal IEnumerable<string> WritableRoots
    {
        get
        {
            yield return ConfigRoot;
            yield return CacheRoot;
            yield return LogRoot;
            yield return TempRoot;
            // The overlay is a writable root in its own right. Without this a path that
            // already points into the overlay — anything GetFullPath/enumeration handed back
            // as a shadow — would be re-mapped a second time (the overlay lives under the
            // install root here), producing overlay/<data-root>/... nesting.
            yield return DataOverlayRoot;
        }
    }

    internal IEnumerable<string> OwnedRoots
    {
        get
        {
            yield return InstallRoot;
            foreach (var root in WritableRoots)
                yield return root;
        }
    }
}

/// <summary>
/// Host-owned filesystem boundary for rewritten PcCompat MOD assemblies.
/// </summary>
/// <remarks>
/// <para>
/// This is the PcCompat counterpart of <see cref="NativeModPathBridge"/>. The two cannot share
/// one implementation because ownership is keyed differently: Android Managed MODs resolve a
/// <c>ModDataDomain</c> from a domain token, while PcCompat MODs carry
/// <c>PcCompatManagedExecutionState(ModId, ResourceSessionGeneration, Phase)</c>. The
/// containment rule itself is shared via <see cref="ModDataDomainPaths.IsWithin"/> so the
/// security-sensitive comparison exists once.
/// </para>
/// <para>
/// Cooperative ownership boundary, not a sandbox: a MOD reaching the filesystem through
/// unrewritten reflection or P/Invoke remains a diagnosable isolation downgrade.
/// </para>
/// </remarks>
public static class PcCompatManagedPathBridge
{
    private static readonly object Gate = new();
    private static readonly Dictionary<SessionKey, PcCompatModPathRoots> Roots = new();

    /// <summary>
    /// Binds the roots for one MOD session. Rebound per resource generation so a reloaded MOD
    /// cannot keep writing through roots captured by the previous generation.
    /// </summary>
    public static void BindRoots(
        string modId,
        long resourceSessionGeneration,
        PcCompatModPathRoots roots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentNullException.ThrowIfNull(roots);
        var normalized = new PcCompatModPathRoots
        {
            InstallRoot = Normalize(roots.InstallRoot, nameof(roots.InstallRoot)),
            ConfigRoot = Normalize(roots.ConfigRoot, nameof(roots.ConfigRoot)),
            CacheRoot = Normalize(roots.CacheRoot, nameof(roots.CacheRoot)),
            LogRoot = Normalize(roots.LogRoot, nameof(roots.LogRoot)),
            TempRoot = Normalize(roots.TempRoot, nameof(roots.TempRoot)),
            DataOverlayRoot = Normalize(roots.DataOverlayRoot, nameof(roots.DataOverlayRoot)),
            SharedReadOnlyRoots = roots.SharedReadOnlyRoots
                .Select(root => Normalize(root, nameof(roots.SharedReadOnlyRoots)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
        lock (Gate)
            Roots[new SessionKey(modId, resourceSessionGeneration)] = normalized;
    }

    public static void ClearRoots(string modId, long resourceSessionGeneration)
    {
        lock (Gate)
            Roots.Remove(new SessionKey(modId, resourceSessionGeneration));
    }

    internal static bool IsBound(string modId, long resourceSessionGeneration)
    {
        lock (Gate)
            return Roots.ContainsKey(new SessionKey(modId, resourceSessionGeneration));
    }

    internal static bool TryGetDataOverlayRoot(
        string modId,
        long resourceSessionGeneration,
        out string root)
    {
        lock (Gate)
        {
            if (Roots.TryGetValue(
                    new SessionKey(modId, resourceSessionGeneration),
                    out var roots))
            {
                root = roots.DataOverlayRoot;
                return true;
            }
        }
        root = string.Empty;
        return false;
    }

    internal static void ClearAllRootsForTests()
    {
        lock (Gate)
            Roots.Clear();
    }

    /// <summary>
    /// Replacement for <see cref="Path.GetFullPath(string)"/>: resolves relative paths against
    /// this MOD's config root instead of the shared process working directory.
    /// </summary>
    public static string GetFullPath(string path) => ResolveForRead(path);

    /// <summary>
    /// Preserves normal string semantics inside a MOD's virtual roots, but treats each virtual
    /// root as its own filesystem root. In particular, taking the parent of the package root
    /// cannot reveal the shared <c>mods</c> directory used by the host.
    /// </summary>
    public static string? GetDirectoryName(string? path)
    {
        if (path is null)
            return null;
        if (!Path.IsPathFullyQualified(path))
            return Path.GetDirectoryName(path);

        var (session, roots) = RequireSession();
        var full = Path.GetFullPath(path);
        var containingRoot = roots.OwnedRoots
            .Concat(roots.SharedReadOnlyRoots)
            .FirstOrDefault(root => ModDataDomainPaths.IsWithin(root, full));
        if (containingRoot is null)
            throw Denied(session, full, forWrite: false);

        return PathsEqual(containingRoot, full)
            ? containingRoot
            : Path.GetDirectoryName(full);
    }

    public static string ResolvePath(string path) => ResolveForRead(path);

    public static string ResolveWritablePath(string path) => ResolveForWrite(path);

    public static bool FileExists(string path) => File.Exists(ResolveForRead(path));

    public static string FileReadAllText(string path) => File.ReadAllText(ResolveForRead(path));

    public static byte[] FileReadAllBytes(string path) => File.ReadAllBytes(ResolveForRead(path));

    public static void FileWriteAllText(string path, string? contents) =>
        File.WriteAllText(ResolveForWrite(path), contents);

    public static void FileWriteAllBytes(string path, byte[] bytes) =>
        File.WriteAllBytes(ResolveForWrite(path), bytes);

    public static void FileDelete(string path) => File.Delete(ResolveForWrite(path));

    public static void FileCopy(string sourceFileName, string destFileName) =>
        File.Copy(ResolveForRead(sourceFileName), ResolveForWrite(destFileName));

    public static void FileCopyOverwrite(
        string sourceFileName,
        string destFileName,
        bool overwrite) =>
        File.Copy(ResolveForRead(sourceFileName), ResolveForWrite(destFileName), overwrite);

    public static FileStream FileOpenRead(string path) => File.OpenRead(ResolveForRead(path));

    public static FileStream FileOpenWrite(string path) => File.OpenWrite(ResolveForWrite(path));

    public static bool DirectoryExists(string path) => Directory.Exists(ResolveForRead(path));

    public static string[] DirectoryGetFiles(string path) =>
        EnumerateFilesSnapshot(path, "*", SearchOption.TopDirectoryOnly);

    public static string[] DirectoryGetFilesPattern(string path, string searchPattern) =>
        EnumerateFilesSnapshot(path, searchPattern, SearchOption.TopDirectoryOnly);

    public static string[] DirectoryGetFilesSearch(
        string path,
        string searchPattern,
        SearchOption searchOption) =>
        EnumerateFilesSnapshot(path, searchPattern, searchOption);

    public static IEnumerable<string> DirectoryEnumerateFiles(string path) =>
        EnumerateFilesSnapshot(path, "*", SearchOption.TopDirectoryOnly);

    public static IEnumerable<string> DirectoryEnumerateFilesPattern(
        string path,
        string searchPattern) =>
        EnumerateFilesSnapshot(path, searchPattern, SearchOption.TopDirectoryOnly);

    public static IEnumerable<string> DirectoryEnumerateFilesSearch(
        string path,
        string searchPattern,
        SearchOption searchOption) =>
        EnumerateFilesSnapshot(path, searchPattern, searchOption);

    public static DirectoryInfo DirectoryCreate(string path) =>
        Directory.CreateDirectory(ResolveForWrite(path));

    public static void DirectoryDelete(string path) => Directory.Delete(ResolveForWrite(path));

    public static void DirectoryDeleteRecursive(string path, bool recursive) =>
        Directory.Delete(ResolveForWrite(path), recursive);

    public static FileStream OpenFileStream(string path, FileMode mode) =>
        new(ResolveForMode(path, mode, null), mode);

    public static FileStream OpenFileStreamAccess(string path, FileMode mode, FileAccess access) =>
        new(ResolveForMode(path, mode, access), mode, access);

    public static FileStream OpenFileStreamShare(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share) =>
        new(ResolveForMode(path, mode, access), mode, access, share);

    private static string ResolveForMode(string path, FileMode mode, FileAccess? access)
    {
        var readOnly = mode == FileMode.Open &&
                       (access ?? FileAccess.ReadWrite) == FileAccess.Read;
        return readOnly ? ResolveForRead(path) : ResolveForWrite(path);
    }

    private static string ResolveForRead(string path) => Resolve(path, forWrite: false);

    private static string ResolveForWrite(string path) => Resolve(path, forWrite: true);

    private static string[] EnumerateFilesSnapshot(
        string path,
        string searchPattern,
        SearchOption searchOption)
    {
        if (searchOption is not SearchOption.TopDirectoryOnly and not SearchOption.AllDirectories)
            throw new ArgumentOutOfRangeException(nameof(searchOption));

        var (session, roots) = RequireSession();
        var requested = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(roots.ConfigRoot, path));
        var inInstall = ModDataDomainPaths.IsWithin(roots.InstallRoot, requested) &&
                        !roots.WritableRoots.Any(root =>
                            ModDataDomainPaths.IsWithin(root, requested));

        if (!inInstall)
        {
            var resolved = ResolveForRead(path);
            if (!Directory.Exists(resolved))
                return Directory.GetFiles(resolved, searchPattern, searchOption);
            return EnumerateMergedFiles(
                    session,
                    roots,
                    resolved,
                    shadowDirectory: null,
                    searchPattern,
                    searchOption)
                .ToArray();
        }

        RejectLinkTraversal(session, roots, requested, forWrite: false);
        var shadowDirectory = MapInstallToOverlay(roots, requested);
        RejectLinkTraversal(session, roots, shadowDirectory, forWrite: false);

        var packageExists = Directory.Exists(requested);
        var shadowExists = Directory.Exists(shadowDirectory);
        if (!packageExists && !shadowExists)
        {
            // Preserve the BCL failure for a genuinely absent logical directory. The JPKV
            // create-then-enumerate case reaches this method with shadowExists=true.
            var missing = File.Exists(shadowDirectory) ? shadowDirectory : requested;
            return Directory.GetFiles(missing, searchPattern, searchOption);
        }

        return EnumerateMergedFiles(
                session,
                roots,
                packageExists ? requested : null,
                shadowExists ? shadowDirectory : null,
                searchPattern,
                searchOption)
            .ToArray();
    }

    private static IEnumerable<string> EnumerateMergedFiles(
        SessionKey session,
        PcCompatModPathRoots roots,
        string? packageDirectory,
        string? shadowDirectory,
        string searchPattern,
        SearchOption searchOption)
    {
        if (packageDirectory is not null)
            RejectLinkTraversal(session, roots, packageDirectory, forWrite: false);
        if (shadowDirectory is not null)
            RejectLinkTraversal(session, roots, shadowDirectory, forWrite: false);

        var shadowEntries = shadowDirectory is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : Directory.EnumerateFileSystemEntries(
                    shadowDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .GroupBy(
                    entry => RequireEntryName(Path.GetFileName(entry)),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.OrdinalIgnoreCase);
        var emittedShadowFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (packageDirectory is not null)
        {
            foreach (var packageFile in Directory.EnumerateFiles(
                         packageDirectory,
                         searchPattern,
                         SearchOption.TopDirectoryOnly))
            {
                var name = RequireEntryName(Path.GetFileName(packageFile));
                if (!shadowEntries.TryGetValue(name, out var shadowEntry))
                {
                    yield return packageFile;
                    continue;
                }

                // An overlay entry shadows the package entry regardless of its kind. If the
                // shadow is a directory, the package file disappears from the logical file view.
                if (File.Exists(shadowEntry) && emittedShadowFiles.Add(name))
                    yield return shadowEntry;
            }
        }

        if (shadowDirectory is not null)
        {
            foreach (var shadowFile in Directory.EnumerateFiles(
                         shadowDirectory,
                         searchPattern,
                         SearchOption.TopDirectoryOnly))
            {
                if (emittedShadowFiles.Add(RequireEntryName(Path.GetFileName(shadowFile))))
                    yield return shadowFile;
            }
        }

        if (searchOption != SearchOption.AllDirectories)
            yield break;

        var mergedShadowDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (packageDirectory is not null)
        {
            foreach (var packageSubdirectory in Directory.EnumerateDirectories(
                         packageDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var name = RequireEntryName(Path.GetFileName(packageSubdirectory));
                if (!shadowEntries.TryGetValue(name, out var shadowEntry))
                {
                    foreach (var nested in EnumerateMergedFiles(
                                 session,
                                 roots,
                                 packageSubdirectory,
                                 shadowDirectory: null,
                                 searchPattern,
                                 searchOption))
                    {
                        yield return nested;
                    }
                    continue;
                }

                if (!Directory.Exists(shadowEntry))
                    continue;
                mergedShadowDirectories.Add(name);
                foreach (var nested in EnumerateMergedFiles(
                             session,
                             roots,
                             packageSubdirectory,
                             shadowEntry,
                             searchPattern,
                             searchOption))
                {
                    yield return nested;
                }
            }
        }

        if (shadowDirectory is null)
            yield break;
        foreach (var shadowSubdirectory in Directory.EnumerateDirectories(
                     shadowDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            if (!mergedShadowDirectories.Add(
                    RequireEntryName(Path.GetFileName(shadowSubdirectory))))
                continue;
            foreach (var nested in EnumerateMergedFiles(
                         session,
                         roots,
                         packageDirectory: null,
                         shadowSubdirectory,
                         searchPattern,
                         searchOption))
            {
                yield return nested;
            }
        }
    }

    private static string RequireEntryName(string? name) =>
        !string.IsNullOrEmpty(name)
            ? name
            : throw new InvalidOperationException("Enumerated filesystem entry has no name.");

    private static string Resolve(string path, bool forWrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var (session, roots) = RequireSession();

        var full = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(roots.ConfigRoot, path));

        if (roots.WritableRoots.Any(root => ModDataDomainPaths.IsWithin(root, full)))
        {
            RejectLinkTraversal(session, roots, full, forWrite);
            return full;
        }

        var inInstall = ModDataDomainPaths.IsWithin(roots.InstallRoot, full);
        var inShared = roots.SharedReadOnlyRoots.Any(root =>
            ModDataDomainPaths.IsWithin(root, full));
        if (inInstall || inShared)
            RejectLinkTraversal(session, roots, full, forWrite);

        if (!forWrite)
        {
            // Owner-scoped VFS, package layer: a path inside the install root resolves to its
            // data-overlay shadow when one exists, so files the MOD has saved shadow the
            // immutable originals it shipped. No shadow means the legacy file is still
            // readable in place — old settings survive without a migration copy.
            if (inInstall)
            {
                var shadow = MapInstallToOverlay(roots, full);
                return OverlayEntryExists(shadow) ? shadow : full;
            }
            if (inShared)
                return full;
            throw Denied(session, full, forWrite);
        }
        if (inInstall)
        {
            // Owner-scoped VFS, write layer: writes aimed at the read-only install root land
            // in this MOD's data overlay with the same relative layout. The package layer
            // itself stays untouched.
            return EnsureOverlayParent(MapInstallToOverlay(roots, full));
        }
        if (inShared)
        {
            throw new InvalidOperationException(
                $"Shared game resources are read-only for mod={session.ModId} " +
                $"generation={session.Generation}: {full}");
        }
        throw Denied(session, full, forWrite);
    }

    /// <summary>
    /// Moves within the MOD's own tree, honoring the VFS layers: an overlay-backed source is
    /// moved for real; a package-only source cannot leave the immutable layer, so the move is
    /// emulated by copying into the destination's overlay location while the original stays
    /// readable — the same shadowing semantics as every other operation.
    /// </summary>
    public static void FileMove(string sourceFileName, string destFileName)
    {
        var (_, roots) = RequireSession();
        // Anchor a relative source in the config root, never the shared process CWD — the
        // same rule the read/write resolution follows.
        var sourceFull = Path.IsPathFullyQualified(sourceFileName)
            ? Path.GetFullPath(sourceFileName)
            : Path.GetFullPath(Path.Combine(roots.ConfigRoot, sourceFileName));
        var destResolved = ResolveForWrite(destFileName);

        var sourceInInstall = ModDataDomainPaths.IsWithin(roots.InstallRoot, sourceFull) &&
                              !roots.WritableRoots.Any(root =>
                                  ModDataDomainPaths.IsWithin(root, sourceFull));
        if (!sourceInInstall)
        {
            File.Move(ResolveForRead(sourceFileName), destResolved);
            return;
        }

        var sourceShadow = EnsureOverlayParent(MapInstallToOverlay(roots, sourceFull));
        if (File.Exists(sourceShadow))
        {
            File.Move(sourceShadow, destResolved);
            return;
        }
        File.Copy(ResolveForRead(sourceFileName), destResolved, overwrite: true);
    }

    private static void RejectLinkTraversal(
        SessionKey session,
        PcCompatModPathRoots roots,
        string full,
        bool forWrite)
    {
        // Containment is lexical, so a symlink/junction the MOD planted inside its own roots
        // would carry a legitimate prefix while pointing anywhere. Checked after ownership and
        // before any filesystem side effect (MOD_RUNTIME_ISOLATION §4.10).
        foreach (var root in roots.OwnedRoots.Concat(roots.SharedReadOnlyRoots))
        {
            if (!ModDataDomainPaths.IsWithin(root, full) ||
                !ModDataDomainPaths.TraversesLinkBelow(root, full))
            {
                continue;
            }
            throw new InvalidOperationException(
                "MOD path traverses a symlink or reparse point and may not be " +
                $"{(forWrite ? "written" : "read")} for mod={session.ModId} " +
                $"generation={session.Generation}: {full}");
        }
    }

    private static InvalidOperationException Denied(
        SessionKey session,
        string full,
        bool forWrite)
    {
        var operation = forWrite ? "write" : "read";
        lock (Gate)
        {
            foreach (var pair in Roots)
            {
                if (pair.Key.Equals(session))
                    continue;
                if (!pair.Value.OwnedRoots.Any(root => ModDataDomainPaths.IsWithin(root, full)))
                    continue;
                return new InvalidOperationException(
                    $"MOD {session.ModId} generation={session.Generation} may not {operation} " +
                    $"a path owned by {pair.Key.ModId} generation={pair.Key.Generation}: {full}");
            }
        }
        return new InvalidOperationException(
            $"Path is outside every root of mod={session.ModId} " +
            $"generation={session.Generation} and may not be {operation}: {full}");
    }

    private static string MapInstallToOverlay(PcCompatModPathRoots roots, string installPath)
    {
        var relative = Path.GetRelativePath(roots.InstallRoot, installPath);
        return Path.GetFullPath(Path.Combine(roots.DataOverlayRoot, relative));
    }

    private static bool OverlayEntryExists(string shadow) =>
        File.Exists(shadow) || Directory.Exists(shadow);

    private static string EnsureOverlayParent(string shadow)
    {
        var parent = Path.GetDirectoryName(shadow);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        return shadow;
    }

    private static (SessionKey Session, PcCompatModPathRoots Roots) RequireSession()
    {
        var execution = PcCompatManagedExecutionContext.Current
                        ?? throw new InvalidOperationException(
                            "PcCompat MOD filesystem access requires an active managed scope.");
        if (execution.Phase == PcCompatManagedExecutionPhase.Disable)
        {
            throw new InvalidOperationException(
                $"PcCompat MOD filesystem access is rejected while mod={execution.ModId} " +
                "is disabling.");
        }
        var session = new SessionKey(execution.ModId, execution.ResourceSessionGeneration);
        lock (Gate)
        {
            if (Roots.TryGetValue(session, out var roots))
                return (session, roots);
        }
        throw new InvalidOperationException(
            $"PcCompat MOD filesystem roots are not bound for mod={session.ModId} " +
            $"generation={session.Generation}.");
    }

    private static string Normalize(string root, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root, parameterName);
        var full = Path.GetFullPath(root);
        return full.Length > 1 &&
               (full.EndsWith(Path.DirectorySeparatorChar) ||
                full.EndsWith(Path.AltDirectorySeparatorChar))
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }

    private static bool PathsEqual(string left, string right)
        => left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(
                right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

    private readonly record struct SessionKey(string ModId, long Generation);
}

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace StArray.ModManager.Runtime;

/// <summary>
/// Host-owned filesystem boundary used by rewritten Android Managed MOD assemblies.
/// Every path a MOD passes to a rewritten call site is resolved against the current
/// <see cref="ModDataDomain"/> roots and checked for ownership before it reaches the real
/// filesystem API.
/// </summary>
/// <remarks>
/// This is a cooperative ownership/fault boundary for normal MODs, not a sandbox against
/// managed code that deliberately bypasses the rewrite (raw P/Invoke, unrewritten
/// reflection). Those stay diagnosable isolation downgrades, per MOD_RUNTIME_ISOLATION.md.
/// </remarks>
public static class NativeModPathBridge
{
    private static readonly ConditionalWeakTable<FileSystemInfo, ModRuntimeCapturedScope>
        OwnedFileInfos = new();

    public static string GetAssemblyLocation(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var token = HookHelper.CurrentDomainToken;
        if (token.IsValid &&
            ModDataDomainRegistry.TryResolve(token, out var domain) &&
            domain.TryGetOriginalAssemblyLocation(
                assembly.GetName().Name ?? string.Empty,
                out var originalPath))
        {
            return originalPath;
        }

        if (AssemblyLoadContext.GetLoadContext(assembly) is NativeModAssemblyLoadContext)
        {
            throw new InvalidOperationException(
                "Native MOD Assembly.Location requires an active data domain.");
        }
        return assembly.Location;
    }

    /// <summary>
    /// Replacement for <see cref="Path.GetFullPath(string)"/>. The framework version
    /// resolves relative paths against the process working directory, which is shared by
    /// every MOD and by the game; this resolves against the domain config root instead.
    /// </summary>
    public static string GetFullPath(string path) => ResolveForRead(path);

    /// <summary>Returns the current MOD's private temporary directory.</summary>
    public static string GetTempPath()
    {
        var (_, roots) = RequireDomain();
        var full = Path.GetFullPath(roots.TempRoot);
        return Path.EndsInDirectorySeparator(full)
            ? full
            : full + Path.DirectorySeparatorChar;
    }

    /// <summary>Resolves a path for reading without touching the filesystem.</summary>
    public static string ResolvePath(string path) => ResolveForRead(path);

    /// <summary>Resolves a path for writing without touching the filesystem.</summary>
    public static string ResolveWritablePath(string path) => ResolveForWrite(path);

    public static bool FileExists(string path) => File.Exists(ResolveForRead(path));

    public static byte[] FileReadAllBytes(string path) => File.ReadAllBytes(ResolveForRead(path));

    public static string FileReadAllText(string path) => File.ReadAllText(ResolveForRead(path));

    public static void FileWriteAllBytes(string path, byte[] bytes) =>
        File.WriteAllBytes(ResolveForWrite(path), bytes);

    public static FileStream FileCreate(string path) =>
        File.Create(ResolveForWrite(path));

    public static FileStream FileOpenRead(string path) => File.OpenRead(ResolveForRead(path));

    public static DateTime FileGetLastWriteTimeUtc(string path) =>
        File.GetLastWriteTimeUtc(ResolveForRead(path));

    public static void FileWriteAllTextEncoding(
        string path,
        string? contents,
        System.Text.Encoding encoding) =>
        File.WriteAllText(ResolveForWrite(path), contents, encoding);

    public static void FileWriteAllText(string path, string? contents) =>
        File.WriteAllText(ResolveForWrite(path), contents);

    /// <summary>
    /// Creates a FileInfo whose path has already been resolved through the current MOD VFS.
    /// FileInfo is registered so rewritten property reads cannot be used with an arbitrary
    /// unowned instance to escape the domain boundary.
    /// </summary>
    public static FileInfo CreateFileInfo(string path)
    {
        var scope = ModRuntimeCapturedScope.Capture("FileInfo creation");
        var info = new FileInfo(ResolveForRead(path));
        OwnedFileInfos.Add(info, scope);
        return info;
    }

    public static bool FileSystemInfoGetExists(FileSystemInfo info) =>
        RequireOwnedFileInfo(info).Exists;

    public static DateTime FileSystemInfoGetLastWriteTimeUtc(FileSystemInfo info) =>
        RequireOwnedFileInfo(info).LastWriteTimeUtc;

    public static long FileInfoGetLength(FileInfo info) =>
        ((FileInfo)RequireOwnedFileInfo(info)).Length;

    public static void FileDelete(string path) => File.Delete(ResolveForWrite(path));

    public static void FileCopy(string sourceFileName, string destFileName) =>
        File.Copy(ResolveForRead(sourceFileName), ResolveForWrite(destFileName));

    public static void FileCopyOverwrite(
        string sourceFileName,
        string destFileName,
        bool overwrite) =>
        File.Copy(ResolveForRead(sourceFileName), ResolveForWrite(destFileName), overwrite);

    public static void FileMove(string sourceFileName, string destFileName)
    {
        var destination = ResolveForWrite(destFileName);
        MoveInstallAware(sourceFileName, destination, overwrite: false);
    }

    public static void FileMoveOverwrite(
        string sourceFileName,
        string destFileName,
        bool overwrite)
    {
        var destination = ResolveForWrite(destFileName);
        if (File.Exists(destination))
            File.Delete(destination);
        MoveInstallAware(sourceFileName, destination, overwrite: true);
    }

    public static bool DirectoryExists(string path) => Directory.Exists(ResolveForRead(path));

    public static void DirectoryMove(string sourceDirName, string destDirName)
    {
        var (domain, roots) = RequireDomain();
        var sourceFull = ResolveLogicalPath(sourceDirName, roots);
        var destination = ResolveForWrite(destDirName);
        var sourceInInstall = ModDataDomainPaths.IsWithin(roots.InstallRoot, sourceFull) &&
                              !roots.WritableRoots.Any(root =>
                                  ModDataDomainPaths.IsWithin(root, sourceFull));
        if (sourceInInstall)
            RejectLinkTraversal(domain, roots, sourceFull, forWrite: false);

        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException($"Cannot create '{destination}' because it already exists.");
        if (sourceInInstall && ModDataDomainPaths.IsWithin(sourceFull, destination))
        {
            throw new IOException(
                $"Cannot move '{sourceFull}' into one of its own descendants.");
        }

        var sourceShadow = sourceInInstall
            ? MapInstallToOverlay(roots, sourceFull)
            : string.Empty;
        var effectiveSource = sourceInInstall && Directory.Exists(sourceShadow)
            ? sourceShadow
            : sourceInInstall
                ? sourceFull
                : ResolveForRead(sourceDirName);
        if (sourceInInstall &&
            string.Equals(effectiveSource, sourceFull, StringComparison.OrdinalIgnoreCase))
        {
            // The package layer is immutable. Match File.Move's install-root behavior by
            // materializing the moved tree in the owner's overlay while leaving the package
            // copy available as the fallback layer.
            CopyDirectory(effectiveSource, destination);
            return;
        }

        EnsureDirectoryParent(destination);
        Directory.Move(effectiveSource, destination);
    }

    /// <summary>
    /// Enumerates inside the resolved directory as one merged VFS view: package entries and
    /// overlay shadows are unioned per relative name, and a shadowed name is reported through
    /// its overlay path so reopening it observes the same content this enumeration saw. A
    /// name present in both layers is listed once — the layering must not duplicate entries.
    /// </summary>
    public static IEnumerable<string> DirectoryEnumerateFilesSearch(
        string path,
        string searchPattern,
        SearchOption searchOption)
    {
        var (domain, roots) = RequireDomain();
        var requested = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(roots.ConfigRoot, path));

        var inInstall = ModDataDomainPaths.IsWithin(roots.InstallRoot, requested) &&
                        !roots.WritableRoots.Any(root =>
                            ModDataDomainPaths.IsWithin(root, requested));
        if (!inInstall)
            return Directory.EnumerateFiles(ResolveForRead(path), searchPattern, searchOption);

        var shadowDirectory = MapInstallToOverlay(roots, requested);
        var packageExists = Directory.Exists(requested);
        var shadowExists = Directory.Exists(shadowDirectory);
        if (!packageExists && !shadowExists)
            return [];
        if (!shadowExists)
            return Directory.EnumerateFiles(requested, searchPattern, searchOption);
        if (!packageExists)
            return Directory.EnumerateFiles(shadowDirectory, searchPattern, searchOption);

        return EnumerateMerged(requested, shadowDirectory, searchPattern, searchOption);
    }

    public static string[] DirectoryGetFilesSearch(
        string path,
        string searchPattern,
        SearchOption searchOption) =>
        DirectoryEnumerateFilesSearch(path, searchPattern, searchOption).ToArray();

    public static IEnumerable<string> DirectoryEnumerateDirectoriesSearch(
        string path,
        string searchPattern,
        SearchOption searchOption)
    {
        var (domain, roots) = RequireDomain();
        var requested = ResolveLogicalPath(path, roots);
        var inInstall = ModDataDomainPaths.IsWithin(roots.InstallRoot, requested) &&
                        !roots.WritableRoots.Any(root =>
                            ModDataDomainPaths.IsWithin(root, requested));
        if (!inInstall)
        {
            return Directory.EnumerateDirectories(
                    ResolveForRead(path),
                    searchPattern,
                    searchOption)
                .ToArray();
        }

        RejectLinkTraversal(domain, roots, requested, forWrite: false);
        var shadowDirectory = MapInstallToOverlay(roots, requested);
        var packageExists = Directory.Exists(requested);
        var shadowExists = Directory.Exists(shadowDirectory);
        if (!packageExists && !shadowExists)
            return [];
        if (!shadowExists)
        {
            return Directory.EnumerateDirectories(
                    requested,
                    searchPattern,
                    searchOption)
                .ToArray();
        }
        if (!packageExists)
        {
            return Directory.EnumerateDirectories(
                    shadowDirectory,
                    searchPattern,
                    searchOption)
                .ToArray();
        }

        return EnumerateMergedDirectories(
                requested,
                shadowDirectory,
                searchPattern,
                searchOption)
            .ToArray();
    }

    public static IEnumerable<string> DirectoryEnumerateFileSystemEntries(string path) =>
        DirectoryEnumerateFileSystemEntriesSearch(
            path,
            "*",
            SearchOption.TopDirectoryOnly);

    private static IEnumerable<string> DirectoryEnumerateFileSystemEntriesSearch(
        string path,
        string searchPattern,
        SearchOption searchOption) =>
        DirectoryEnumerateFilesSearch(path, searchPattern, searchOption)
            .Concat(DirectoryEnumerateDirectoriesSearch(path, searchPattern, searchOption))
            .ToArray();

    private static IEnumerable<string> EnumerateMergedDirectories(
        string packageDirectory,
        string shadowDirectory,
        string searchPattern,
        SearchOption searchOption)
    {
        if (searchOption == SearchOption.TopDirectoryOnly)
            return EnumerateMergedDirectoryLevel(packageDirectory, shadowDirectory, searchPattern);

        var output = new List<string>();
        WalkMergedDirectories(packageDirectory, shadowDirectory, searchPattern, output);
        return output;
    }

    private static void WalkMergedDirectories(
        string packageDirectory,
        string shadowDirectory,
        string searchPattern,
        List<string> output)
    {
        var packageAll = EnumerateDirectoriesOrEmpty(packageDirectory, "*")
            .ToDictionary(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        var shadowAll = EnumerateDirectoriesOrEmpty(shadowDirectory, "*")
            .ToDictionary(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        var packageMatches = EnumerateDirectoriesOrEmpty(packageDirectory, searchPattern)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shadowMatches = EnumerateDirectoriesOrEmpty(shadowDirectory, searchPattern)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in packageAll.Keys.Concat(shadowAll.Keys)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            packageAll.TryGetValue(name, out var packagePath);
            shadowAll.TryGetValue(name, out var shadowPath);
            if (shadowPath != null && File.Exists(shadowPath))
                continue;

            var selected = shadowPath ?? packagePath;
            if (selected == null)
                continue;
            if (packageMatches.Contains(name) || shadowMatches.Contains(name))
                output.Add(selected);

            var nextPackage = packagePath ?? string.Empty;
            var nextShadow = shadowPath ?? string.Empty;
            if (Directory.Exists(nextPackage) || Directory.Exists(nextShadow))
                WalkMergedDirectories(nextPackage, nextShadow, searchPattern, output);
        }
    }

    private static IEnumerable<string> EnumerateMergedDirectoryLevel(
        string packageDirectory,
        string shadowDirectory,
        string searchPattern)
    {
        var package = EnumerateDirectoriesOrEmpty(packageDirectory, searchPattern)
            .ToDictionary(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        var shadows = EnumerateDirectoriesOrEmpty(shadowDirectory, searchPattern)
            .ToDictionary(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        foreach (var name in package.Keys.Concat(shadows.Keys)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            package.TryGetValue(name, out var packagePath);
            shadows.TryGetValue(name, out var shadowPath);
            if (shadowPath != null && File.Exists(shadowPath))
                continue;
            if (shadowPath != null)
                yield return shadowPath;
            else if (packagePath != null)
                yield return packagePath;
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesOrEmpty(
        string path,
        string searchPattern) =>
        Directory.Exists(path)
            ? Directory.EnumerateDirectories(path, searchPattern, SearchOption.TopDirectoryOnly)
            : [];

    private static IEnumerable<string> EnumerateMerged(
        string packageDirectory,
        string shadowDirectory,
        string searchPattern,
        SearchOption searchOption)
    {
        // Relative-name keyed merge: the overlay wins for names it carries, everything else
        // comes from the immutable package layer. A shadowed name is reported through its
        // overlay path so reopening it observes the shadowed content.
        var recursive = searchOption == SearchOption.AllDirectories;
        var shadows = Directory
            .EnumerateFiles(shadowDirectory, searchPattern, SearchOption.TopDirectoryOnly)
            .GroupBy(entry => Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var entry in Directory.EnumerateFiles(
                     packageDirectory, searchPattern, SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(entry);
            if (shadows.TryGetValue(fileName, out var shadowPath))
            {
                shadows.Remove(fileName);
                yield return shadowPath;
            }
            else
            {
                yield return entry;
            }
        }

        foreach (var shadowPath in shadows.Values)
            yield return shadowPath;

        if (!recursive)
            yield break;

        foreach (var packageSub in Directory.EnumerateDirectories(
                     packageDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var subName = Path.GetFileName(packageSub);
            var shadowSub = Path.Combine(shadowDirectory, subName);
            // A package subdirectory replaced by an overlay FILE cannot be descended into;
            // the file already replaced it in the logical view.
            if (File.Exists(shadowSub))
                continue;
            if (Directory.Exists(shadowSub))
            {
                foreach (var nested in EnumerateMerged(
                             packageSub, shadowSub, searchPattern, searchOption))
                    yield return nested;
            }
            else
            {
                foreach (var nested in Directory.EnumerateFiles(
                             packageSub, searchPattern, SearchOption.AllDirectories))
                    yield return nested;
            }
        }

        foreach (var shadowSub in Directory.EnumerateDirectories(
                     shadowDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (Directory.Exists(Path.Combine(packageDirectory, Path.GetFileName(shadowSub))))
                continue; // already merged above
            foreach (var nested in Directory.EnumerateFiles(
                         shadowSub, searchPattern, SearchOption.AllDirectories))
                yield return nested;
        }
    }

    public static DirectoryInfo DirectoryCreate(string path) =>
        Directory.CreateDirectory(ResolveForWrite(path));

    public static void DirectoryDelete(string path) => Directory.Delete(ResolveForWrite(path));

    public static void DirectoryDeleteRecursive(string path, bool recursive) =>
        Directory.Delete(ResolveForWrite(path), recursive);

    public static FileStream OpenFileStream(string path, FileMode mode) =>
        new(ResolveForMode(path, mode, null), mode);

    public static FileStream OpenFileStreamAccess(
        string path,
        FileMode mode,
        FileAccess access) =>
        new(ResolveForMode(path, mode, access), mode, access);

    public static FileStream OpenFileStreamShare(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share) =>
        new(ResolveForMode(path, mode, access), mode, access, share);

    public static FileStream OpenFileStreamOptions(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        int bufferSize,
        FileOptions options) =>
        new(ResolveForMode(path, mode, access), mode, access, share, bufferSize, options);

    /// <summary>
    /// Replacement for <see cref="File.Open(string, FileStreamOptions)"/>. The complete
    /// options object is passed through unchanged after resolving the path, so buffering,
    /// preallocation, sharing and asynchronous I/O semantics are preserved.
    /// </summary>
    public static FileStream FileOpenOptions(string path, FileStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return File.Open(ResolveForMode(path, options.Mode, options.Access), options);
    }

    public static StreamWriter OpenStreamWriterEncoding(
        string path,
        bool append,
        System.Text.Encoding encoding) =>
        new(ResolveForWrite(path), append, encoding);

    private static string ResolveForMode(string path, FileMode mode, FileAccess? access)
    {
        var readOnly = mode == FileMode.Open &&
                       (access ?? FileAccess.ReadWrite) == FileAccess.Read;
        return readOnly ? ResolveForRead(path) : ResolveForWrite(path);
    }

    /// <summary>
    /// Moves within the caller's own tree, honoring the VFS layers: an overlay-backed source
    /// is moved for real; a package-only source cannot leave the immutable layer, so the move
    /// is emulated by copying into the destination's overlay location while the original
    /// stays readable — the same shadowing semantics as every other operation.
    /// </summary>
    private static void MoveInstallAware(
        string sourceFileName,
        string resolvedDestination,
        bool overwrite)
    {
        var (domain, roots) = RequireDomain();
        var sourceFull = Path.IsPathFullyQualified(sourceFileName)
            ? Path.GetFullPath(sourceFileName)
            : Path.GetFullPath(Path.Combine(roots.ConfigRoot, sourceFileName));

        var sourceInInstall = ModDataDomainPaths.IsWithin(roots.InstallRoot, sourceFull) &&
                              !roots.WritableRoots.Any(root =>
                                  ModDataDomainPaths.IsWithin(root, sourceFull));
        if (!sourceInInstall)
        {
            File.Move(ResolveForRead(sourceFileName), resolvedDestination, overwrite);
            return;
        }

        var sourceShadow = EnsureOverlayParent(MapInstallToOverlay(roots, sourceFull));
        if (File.Exists(sourceShadow))
        {
            File.Move(sourceShadow, resolvedDestination, overwrite);
            return;
        }
        if (!overwrite && File.Exists(resolvedDestination))
        {
            throw new IOException(
                $"Cannot create '{resolvedDestination}' because it already exists.");
        }
        File.Copy(ResolveForRead(sourceFileName), resolvedDestination, overwrite: true);
    }

    private static string ResolveForRead(string path) => Resolve(path, forWrite: false);

    private static string ResolveLogicalPath(
        string path,
        ModDataDomainPathRoots roots) =>
        Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(roots.ConfigRoot, path));

    private static FileSystemInfo RequireOwnedFileInfo(FileSystemInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!OwnedFileInfos.TryGetValue(info, out var scope))
        {
            throw new InvalidOperationException(
                "FileInfo was not created by the current Native MOD path bridge.");
        }
        scope.ValidateCurrentCaller("FileInfo");
        return info;
    }

    private static string ResolveForWrite(string path) => Resolve(path, forWrite: true);

    private static string Resolve(string path, bool forWrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var (domain, roots) = RequireDomain();

        // A relative path would otherwise resolve against the shared process working
        // directory, so anchor it in this domain's config root.
        var full = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(roots.ConfigRoot, path));

        var inWritable = roots.WritableRoots.Any(root =>
            ModDataDomainPaths.IsWithin(root, full));
        if (inWritable)
        {
            RejectLinkTraversal(domain, roots, full, forWrite);
            return full;
        }

        var inInstall = ModDataDomainPaths.IsWithin(roots.InstallRoot, full);
        var inHostProtected = roots.HostProtectedRoots.Any(root =>
            ModDataDomainPaths.IsWithin(root, full));
        var inSharedReadOnly = roots.SharedReadOnlyRoots.Any(root =>
            ModDataDomainPaths.IsWithin(root, full));
        var inSharedWritable = roots.SharedWritableRoots.Any(root =>
            ModDataDomainPaths.IsWithin(root, full));
        if (inInstall || inSharedReadOnly || inSharedWritable)
            RejectLinkTraversal(domain, roots, full, forWrite);

        // Explicit owner roots above take precedence so a shared platform root cannot
        // disable the install overlay or the current generation's private data roots.
        // Everything else below a Host-owned manager tree stays unavailable.
        if (!inInstall && inHostProtected)
        {
            if (ModDataDomainRegistry.TryFindForeignPathOwner(full, domain.Token, out _))
                throw Denied(domain, full, forWrite);
            throw new InvalidOperationException(
                $"Host-protected path may not be {(forWrite ? "written" : "read")} " +
                $"by owner={domain.Key.OwnerId} generation={domain.Key.Generation}: {full}");
        }

        if (!forWrite)
        {
            // Owner-scoped VFS, package layer: an install-root path resolves to its data-
            // overlay shadow when one exists, so files the MOD saved shadow the immutable
            // originals. No shadow means the legacy file is still readable in place.
            if (inInstall)
            {
                var shadow = MapInstallToOverlay(roots, full);
                return OverlayEntryExists(shadow) ? shadow : full;
            }
            if (inSharedReadOnly || inSharedWritable)
                return full;
            throw Denied(domain, full, forWrite);
        }

        if (inInstall)
        {
            // One rule for the whole install root, executables included: the write lands in
            // this owner's overlay. For data files that is the persistence path; for a MOD's
            // own assemblies it is a *pending* self-update — the loader still reads the
            // package layer, so the new binary is inert until the Host activates it, and
            // rollback is just dropping the overlay entry because the package original was
            // never touched. Nothing is ever silently lost: pending updates are enumerable
            // through SnapshotPendingSelfUpdates.
            return EnsureOverlayParent(MapInstallToOverlay(roots, full));
        }
        if (inSharedReadOnly)
        {
            throw new InvalidOperationException(
                $"Shared game resources are read-only for owner={domain.Key.OwnerId} " +
                $"generation={domain.Key.Generation}: {full}");
        }
        if (inSharedWritable)
            return full;
        throw Denied(domain, full, forWrite);
    }

    private static string MapInstallToOverlay(ModDataDomainPathRoots roots, string installPath)
    {
        var relative = Path.GetRelativePath(roots.InstallRoot, installPath);
        return Path.GetFullPath(Path.Combine(roots.DataOverlayRoot, relative));
    }

    private static bool IsExecutableFile(string fullPath)
    {
        var extension = Path.GetExtension(fullPath);
        return string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Assemblies a MOD has written over its own package copies: the overlay holds new bytes
    /// while the package layer still holds what the loader is running. Derived from the
    /// filesystem instead of a side ledger, so it survives process restarts and cannot drift
    /// from the bytes it describes; the staged content is the MOD's own write, so nothing has
    /// to be snapshotted and a MOD deleting its download directory cannot invalidate it.
    /// </summary>
    internal static IReadOnlyList<PendingSelfUpdate> SnapshotPendingSelfUpdates(
        ModDataDomainPathRoots roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (!Directory.Exists(roots.DataOverlayRoot))
            return [];

        var pending = new List<PendingSelfUpdate>();
        foreach (var shadow in Directory.EnumerateFiles(
                     roots.DataOverlayRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            if (!IsExecutableFile(shadow))
                continue;
            var relative = Path.GetRelativePath(roots.DataOverlayRoot, shadow);
            var packagePath = Path.GetFullPath(Path.Combine(roots.InstallRoot, relative));
            pending.Add(new PendingSelfUpdate(
                relative,
                shadow,
                packagePath,
                PackageCopyExists: File.Exists(packagePath)));
        }
        return pending;
    }

    /// <summary>
    /// One assembly a MOD replaced in its own install tree, still awaiting Host activation.
    /// </summary>
    internal sealed record PendingSelfUpdate(
        string RelativePath,
        string StagedPath,
        string PackagePath,
        bool PackageCopyExists);

    private static bool OverlayEntryExists(string shadow) =>
        File.Exists(shadow) || Directory.Exists(shadow);

    private static string EnsureOverlayParent(string shadow)
    {
        var parent = Path.GetDirectoryName(shadow);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        return shadow;
    }

    private static void EnsureDirectoryParent(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException(source);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        foreach (var directory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            CopyDirectory(
                directory,
                Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void RejectLinkTraversal(
        ModDataDomain domain,
        ModDataDomainPathRoots roots,
        string full,
        bool forWrite)
    {
        // Containment is lexical, so a symlink/junction the MOD planted inside its own roots
        // would carry a legitimate prefix while pointing anywhere. Checked after ownership
        // and before any filesystem side effect.
        foreach (var root in roots.OwnedRoots
                     .Concat(roots.SharedReadOnlyRoots)
                     .Concat(roots.SharedWritableRoots))
        {
            if (!ModDataDomainPaths.IsWithin(root, full) ||
                !ModDataDomainPaths.TraversesLinkBelow(root, full))
            {
                continue;
            }
            throw new InvalidOperationException(
                $"MOD path traverses a symlink or reparse point and may not be " +
                $"{(forWrite ? "written" : "read")} for owner={domain.Key.OwnerId} " +
                $"generation={domain.Key.Generation}: {full}");
        }
    }

    private static InvalidOperationException Denied(
        ModDataDomain domain,
        string full,
        bool forWrite)
    {
        var operation = forWrite ? "write" : "read";
        if (ModDataDomainRegistry.TryFindForeignPathOwner(full, domain.Token, out var other))
        {
            return new InvalidOperationException(
                $"MOD {domain.Key.OwnerId} generation={domain.Key.Generation} may not " +
                $"{operation} a path owned by {other.OwnerId} " +
                $"generation={other.Generation}: {full}");
        }
        return new InvalidOperationException(
            $"Path is outside every root of owner={domain.Key.OwnerId} " +
            $"generation={domain.Key.Generation} and may not be {operation}: {full}");
    }

    private static (ModDataDomain Domain, ModDataDomainPathRoots Roots) RequireDomain()
    {
        var token = HookHelper.CurrentDomainToken;
        if (!token.IsValid || !ModDataDomainRegistry.TryResolve(token, out var domain))
        {
            throw new InvalidOperationException(
                "Native MOD filesystem access requires an active data domain.");
        }
        if (!domain.TryGetPathRoots(out var roots))
        {
            throw new InvalidOperationException(
                "Native MOD filesystem roots are not bound for " +
                $"owner={domain.Key.OwnerId} generation={domain.Key.Generation}.");
        }
        return (domain, roots);
    }
}

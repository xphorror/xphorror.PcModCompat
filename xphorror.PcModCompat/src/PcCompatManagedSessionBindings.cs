namespace Xphorror.PcModCompat;

/// <summary>
/// Installs the per-generation PcCompat filesystem and network identities before any MOD
/// bootstrap code is allowed to execute. The managed session constructor calls the same helper,
/// so bootstrap and normal lifecycle use one binding contract.
/// </summary>
internal static class PcCompatManagedSessionBindings
{
    internal static bool IsBound(string modId, long resourceSessionGeneration)
        => resourceSessionGeneration > 0 &&
           PcCompatManagedPathBridge.IsBound(modId, resourceSessionGeneration) &&
           PcCompatManagedNetworkBridge.IsBound(modId, resourceSessionGeneration);

    internal static void Bind(PcModManifest manifest, long resourceSessionGeneration)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (resourceSessionGeneration <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(resourceSessionGeneration),
                resourceSessionGeneration,
                "PcCompat managed session generation must be positive.");

        var pathAlreadyBound = PcCompatManagedPathBridge.IsBound(
            manifest.Id,
            resourceSessionGeneration);
        var networkAlreadyBound = PcCompatManagedNetworkBridge.IsBound(
            manifest.Id,
            resourceSessionGeneration);
        var dataRoot = Path.Combine(manifest.FolderPath, ".pccompat-data");
        if (pathAlreadyBound && networkAlreadyBound)
        {
            EnsureWritableDirectories(dataRoot);
            return;
        }

        if (!pathAlreadyBound)
        {
            PcCompatManagedPathBridge.BindRoots(
                manifest.Id,
                resourceSessionGeneration,
                new PcCompatModPathRoots
                {
                    InstallRoot = manifest.FolderPath,
                    ConfigRoot = Path.Combine(dataRoot, "config"),
                    CacheRoot = Path.Combine(dataRoot, "cache"),
                    LogRoot = Path.Combine(dataRoot, "log"),
                    TempRoot = Path.Combine(dataRoot, "temp"),
                    DataOverlayRoot = Path.Combine(dataRoot, "data")
                });
        }

        try
        {
            // Bootstrap may save settings before the normal managed session object exists. Make
            // every owner-controlled writable root usable at that point, while the package layer
            // itself remains untouched and read-only. This also repairs a partially initialized
            // binding left by an older runtime in the same process.
            EnsureWritableDirectories(dataRoot);
            if (!networkAlreadyBound)
            {
                PcCompatManagedNetworkBridge.BindNetworkState(
                    manifest.Id,
                    resourceSessionGeneration);
            }
        }
        catch
        {
            if (!pathAlreadyBound)
            {
                PcCompatManagedPathBridge.ClearRoots(
                    manifest.Id,
                    resourceSessionGeneration);
            }
            throw;
        }
    }

    private static void EnsureWritableDirectories(string dataRoot)
    {
        Directory.CreateDirectory(Path.Combine(dataRoot, "config"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "cache"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "log"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "temp"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "data"));
    }

    internal static void Clear(string modId, long resourceSessionGeneration)
    {
        PcCompatManagedNetworkBridge.ClearNetworkState(modId, resourceSessionGeneration);
        PcCompatManagedPathBridge.ClearRoots(modId, resourceSessionGeneration);
    }
}

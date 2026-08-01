using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Xphorror.PcModCompat;

public sealed record PcCompatManagedAssemblyDescriptor(
    string AssemblyName,
    string InputPath,
    bool IsPrimary,
    bool IsBootstrap);

public static class PcCompatManagedAssemblyCatalog
{
    private static readonly HashSet<string> BuiltInExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mscorlib",
        "netstandard",
        "System",
        "UnityModManager",
        "JALib",
        "HarmonyLib",
        "Il2Cppmscorlib",
        "Il2CppInterop.Common",
        "Il2CppInterop.Runtime",
        "StArray.ModManager",
        "StArray.ModManager.Android",
        "xphorror.PcModCompat"
    };

    public static IReadOnlyList<PcCompatManagedAssemblyDescriptor> Discover(
        PcModManifest manifest,
        IEnumerable<string>? additionalExclusions = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var modFolder = Path.GetFullPath(manifest.FolderPath);
        if (!Directory.Exists(modFolder))
            throw new DirectoryNotFoundException($"PC MOD folder does not exist: {modFolder}");

        var primaryPath = ResolvePrimaryPath(manifest);
        var bootstrapPath = string.IsNullOrWhiteSpace(manifest.EntryAssemblyPath)
            ? null
            : Path.GetFullPath(manifest.EntryAssemblyPath);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primaryPath };
        if (bootstrapPath != null && File.Exists(bootstrapPath))
            roots.Add(bootstrapPath);

        var exclusions = new HashSet<string>(BuiltInExclusions, StringComparer.OrdinalIgnoreCase);
        if (additionalExclusions != null)
            exclusions.UnionWith(additionalExclusions.Where(name => !string.IsNullOrWhiteSpace(name)));

        var candidates = new Dictionary<string, AssemblyMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(modFolder, "*.dll", SearchOption.AllDirectories)
                     .Select(Path.GetFullPath)
                     .Where(path => !IsGeneratedDirectory(modFolder, path))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var metadata = TryRead(path);
            if (metadata == null)
                continue;
            if (!candidates.TryAdd(metadata.AssemblyName, metadata))
            {
                throw new InvalidDataException(
                    $"PC MOD contains duplicate managed assembly name '{metadata.AssemblyName}': " +
                    $"{candidates[metadata.AssemblyName].Path} and {metadata.Path}");
            }
        }

        foreach (var root in roots)
        {
            if (!File.Exists(root))
                throw new FileNotFoundException("PC MOD managed root assembly is missing.", root);
            var rootMetadata = TryRead(root)
                               ?? throw new BadImageFormatException(
                                   $"PC MOD managed root is not a metadata assembly: {root}");
            if (candidates.TryGetValue(rootMetadata.AssemblyName, out var existing) &&
                !PathEquals(existing.Path, root))
            {
                throw new InvalidDataException(
                    $"PC MOD root assembly name '{rootMetadata.AssemblyName}' is ambiguous: " +
                    $"{existing.Path} and {root}");
            }
            candidates[rootMetadata.AssemblyName] = rootMetadata;
        }

        var selected = new Dictionary<string, AssemblyMetadata>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<AssemblyMetadata>();
        foreach (var root in roots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var metadata = candidates.Values.Single(item => PathEquals(item.Path, root));
            if (selected.TryAdd(metadata.AssemblyName, metadata))
                pending.Enqueue(metadata);
        }

        while (pending.Count != 0)
        {
            var current = pending.Dequeue();
            foreach (var reference in current.References.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                if (selected.ContainsKey(reference) || exclusions.Contains(reference) ||
                    IsFrameworkAssembly(reference) || !candidates.TryGetValue(reference, out var dependency))
                {
                    continue;
                }
                selected.Add(reference, dependency);
                pending.Enqueue(dependency);
            }
        }

        return selected.Values
            .Select(item => new PcCompatManagedAssemblyDescriptor(
                item.AssemblyName,
                item.Path,
                PathEquals(item.Path, primaryPath),
                bootstrapPath != null && PathEquals(item.Path, bootstrapPath)))
            .OrderByDescending(item => item.IsPrimary)
            .ThenByDescending(item => item.IsBootstrap)
            .ThenBy(item => item.AssemblyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static AssemblyMetadata? TryRead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata)
                return null;
            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly)
                return null;
            var definition = reader.GetAssemblyDefinition();
            var name = reader.GetString(definition.Name);
            if (string.IsNullOrWhiteSpace(name))
                return null;
            var references = reader.AssemblyReferences
                .Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))
                .Where(reference => !string.IsNullOrWhiteSpace(reference))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new AssemblyMetadata(name, Path.GetFullPath(path), references);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private static bool IsGeneratedDirectory(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals(".pccompat", StringComparison.OrdinalIgnoreCase) ||
                            segment.Equals("compiled", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsFrameworkAssembly(string name)
        => name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
           name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
           name.StartsWith("UnityEngine.", StringComparison.OrdinalIgnoreCase) ||
           name.StartsWith("Il2CppInterop.", StringComparison.OrdinalIgnoreCase);

    private static string ResolvePrimaryPath(PcModManifest manifest)
        => manifest.Kind == PcModKind.JAMod && !string.IsNullOrWhiteSpace(manifest.JAModAssemblyFullPath)
            ? Path.GetFullPath(manifest.JAModAssemblyFullPath)
            : Path.GetFullPath(manifest.EntryAssemblyPath);

    private static bool PathEquals(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private sealed record AssemblyMetadata(
        string AssemblyName,
        string Path,
        IReadOnlyList<string> References);
}

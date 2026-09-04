using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using StArray.ModManager.Manager;
using StArray.ModManager.Native;

namespace StArray.ModManager.Runtime;

/// <summary>
/// Loads one Android native MOD into its own managed load context.
/// Host contract assemblies remain shared so interface and ABI types keep one identity;
/// MOD-local dependencies are resolved from the MOD directory.
/// </summary>
internal sealed class NativeModAssemblyLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> SharedAssemblyNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "StArray.ModManager",
        "StArray.ModManager.Android",
        "ImGui.NET"
    };

    private string _modId;
    private readonly string _modDirectory;
    private readonly IReadOnlyDictionary<string, string> _managedAssemblyPaths;
    private readonly object _unmanagedSync = new();
    private readonly Dictionary<string, NativeModUnmanagedLibraryIdentity>
        _unmanagedLibraries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ownedUnmanagedLibraryPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private long _runtimeGeneration;
    private bool _retiring;

    public NativeModAssemblyLoadContext(string modId, string entryAssemblyPath)
        : base($"StArray.Native:{modId}", isCollectible: true)
    {
        var fullEntryPath = Path.GetFullPath(entryAssemblyPath);
        _modId = modId;
        _modDirectory = Path.GetDirectoryName(fullEntryPath)
                        ?? throw new ArgumentException(
                            "Native MOD entry assembly has no parent directory.",
                            nameof(entryAssemblyPath));
        _managedAssemblyPaths = IndexManagedAssemblies(_modDirectory);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (IsSharedAssembly(name))
        {
            var shared = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
            if (shared != null)
                return shared;

            try
            {
                return AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);
            }
            catch
            {
                return null;
            }
        }

        return _managedAssemblyPaths.TryGetValue(name, out var path) && File.Exists(path)
            ? LoadFromAssemblyPath(path)
            : null;
    }

    internal static bool IsSharedAssembly(string name)
        => SharedAssemblyNames.Contains(name) ||
           name.Equals("System", StringComparison.OrdinalIgnoreCase) ||
           name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("netstandard", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase) ||
           name.StartsWith("Microsoft.Win32.", StringComparison.OrdinalIgnoreCase);

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        lock (_unmanagedSync)
        {
            if (_retiring)
                return 0;
        }

        if (string.Equals(unmanagedDllName, "starray_modmanager", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(unmanagedDllName, "libstarray_modmanager.so", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return NativeLibrary.Load("starray_modmanager");
            }
            catch
            {
                return 0;
            }
        }

        foreach (var candidate in EnumerateUnmanagedCandidates(unmanagedDllName))
        {
            if (!File.Exists(candidate))
                continue;

            try
            {
                lock (_unmanagedSync)
                {
                    if (_retiring)
                        return 0;
                    var canonicalPath = Path.GetFullPath(candidate);
                    var handle = LoadUnmanagedDllFromPath(canonicalPath);
                    var baseAddress = DL.GetBaseAddress(canonicalPath);
                    if (baseAddress == 0)
                        baseAddress = DL.IteratePhdr(canonicalPath);
                    RegisterUnmanagedLibrary(
                        unmanagedDllName,
                        canonicalPath,
                        handle,
                        baseAddress);
                    return handle;
                }
            }
            catch
            {
                // Try the next platform naming convention before falling back.
            }
        }

        return 0;
    }

    internal void BindRuntimeGeneration(long generation)
    {
        if (generation <= 0)
            throw new ArgumentOutOfRangeException(nameof(generation));
        lock (_unmanagedSync)
        {
            _runtimeGeneration = generation;
            foreach (var path in _unmanagedLibraries.Keys.ToArray())
            {
                var current = _unmanagedLibraries[path];
                if (current.LoadGeneration == 0)
                    _unmanagedLibraries[path] = current with { LoadGeneration = generation };
                RegisterOwnedUnmanagedLibrary(path);
            }
        }
    }

    internal void BindModId(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
            throw new ArgumentException("Native MOD ID cannot be empty.", nameof(modId));
        lock (_unmanagedSync)
        {
            _modId = modId;
            foreach (var path in _unmanagedLibraries.Keys.ToArray())
            {
                var current = _unmanagedLibraries[path];
                if (!string.Equals(current.ModId, modId, StringComparison.Ordinal))
                    _unmanagedLibraries[path] = current with { ModId = modId };
            }
        }
    }

    internal IReadOnlyList<NativeModUnmanagedLibraryIdentity> SnapshotUnmanagedLibraries()
    {
        lock (_unmanagedSync)
            return _unmanagedLibraries.Values.OrderBy(item => item.CanonicalPath).ToArray();
    }

    internal void BeginRetirement()
    {
        lock (_unmanagedSync)
            _retiring = true;
    }

    internal void RegisterUnmanagedLibrary(
        string requestedName,
        string path,
        nint handle,
        nint baseAddress)
    {
        if (handle == 0)
            throw new ArgumentException("Native library handle cannot be zero.", nameof(handle));
        var canonicalPath = Path.GetFullPath(path);
        _ = NativeModElfIdentityReader.TryRead(canonicalPath, out var elf);
        lock (_unmanagedSync)
        {
            if (_retiring)
                throw new InvalidOperationException(
                    $"Native MOD context '{Name}' is retiring.");
            _unmanagedLibraries[canonicalPath] = new NativeModUnmanagedLibraryIdentity(
                _modId,
                _runtimeGeneration,
                requestedName,
                canonicalPath,
                handle,
                baseAddress,
                elf,
                ObservedOutOfBand: false,
                ContextRetired: false);
            RegisterOwnedUnmanagedLibrary(canonicalPath);
        }
    }

    internal int ReconcileObservedUnmanagedLibraries()
    {
        var observed = NativeModProcessMapReader.ReadUnder(_modDirectory);
        if (observed.Count == 0)
            return 0;

        var registered = 0;
        lock (_unmanagedSync)
        {
            if (_retiring)
                return 0;
            foreach (var mapping in observed)
            {
                if (_unmanagedLibraries.ContainsKey(mapping.CanonicalPath))
                    continue;
                _ = NativeModElfIdentityReader.TryRead(mapping.CanonicalPath, out var elf);
                _unmanagedLibraries[mapping.CanonicalPath] =
                    new NativeModUnmanagedLibraryIdentity(
                        _modId,
                        _runtimeGeneration,
                        Path.GetFileName(mapping.CanonicalPath),
                        mapping.CanonicalPath,
                        DlopenHandle: 0,
                        mapping.BaseAddress,
                        elf,
                        ObservedOutOfBand: true,
                        ContextRetired: false);
                RegisterOwnedUnmanagedLibrary(mapping.CanonicalPath);
                registered++;
            }
        }
        return registered;
    }

    private void RegisterOwnedUnmanagedLibrary(string canonicalPath)
    {
        if (_runtimeGeneration <= 0 || _ownedUnmanagedLibraryPaths.Contains(canonicalPath))
            return;

        var key = new ModRuntimeKey(
            ModEntry.NativeLoaderKind,
            _modId,
            _runtimeGeneration);
        if (!ModOwnedResourceRegistry.TryRegister(
                key,
                ModOwnedResourceKind.NativeLibrary,
                canonicalPath))
        {
            throw new InvalidOperationException(
                $"Native MOD owned-library registration failed: {canonicalPath}");
        }
        _ownedUnmanagedLibraryPaths.Add(canonicalPath);
    }

    private static IReadOnlyDictionary<string, string> IndexManagedAssemblies(string directory)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if (!string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var name = AssemblyName.GetAssemblyName(path).Name;
                if (!string.IsNullOrWhiteSpace(name) && !paths.TryAdd(name, Path.GetFullPath(path)))
                {
                    throw new InvalidDataException(
                        $"Native MOD directory contains duplicate assembly identity: {name}.");
                }
            }
            catch (BadImageFormatException)
            {
                // Native libraries and non-managed DLL resources are not dependency candidates.
            }
        }
        return paths;
    }

    private IEnumerable<string> EnumerateUnmanagedCandidates(string unmanagedDllName)
    {
        if (string.IsNullOrWhiteSpace(unmanagedDllName) ||
            Path.GetFileName(unmanagedDllName) != unmanagedDllName)
            yield break;

        yield return Path.Combine(_modDirectory, unmanagedDllName);

        if (!unmanagedDllName.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
            yield return Path.Combine(_modDirectory, unmanagedDllName + ".so");

        if (!unmanagedDllName.StartsWith("lib", StringComparison.OrdinalIgnoreCase))
        {
            var fileName = unmanagedDllName.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
                ? "lib" + unmanagedDllName
                : "lib" + unmanagedDllName + ".so";
            yield return Path.Combine(_modDirectory, fileName);
        }
    }
}

/// <summary>Owns one native MOD instance and its load context.</summary>
internal sealed class NativeModLoadState
{
    private static readonly long UnmanagedAuditIntervalTicks =
        checked(Stopwatch.Frequency * 5L);
    private readonly string _modId;
    private readonly string _entryAssemblyPath;
    private readonly string? _pluginTypeName;
    private NativeModShadowPackage? _shadowPackage;
    private readonly object _sync = new();
    private NativeModAssemblyLoadContext? _loadContext;
    private Assembly? _assembly;
    private IModPlugin? _plugin;
    private ModRuntimeKey _boundRuntimeKey;
    private IReadOnlyList<NativeModUnmanagedLibraryIdentity> _retiredUnmanagedLibraries =
        Array.Empty<NativeModUnmanagedLibraryIdentity>();
    private long _nextUnmanagedAuditTimestamp;

    public NativeModLoadState(
        string entryAssemblyPath,
        NativeModAssemblyLoadContext loadContext,
        Assembly assembly,
        IModPlugin plugin,
        ModRuntimeSession? runtimeSession = null,
        NativeModShadowPackage? shadowPackage = null)
    {
        _modId = plugin.Id;
        _entryAssemblyPath = Path.GetFullPath(entryAssemblyPath);
        _pluginTypeName = plugin.GetType().FullName;
        _shadowPackage = shadowPackage;
        _loadContext = loadContext;
        _loadContext.BindModId(_modId);
        _assembly = assembly;
        _plugin = plugin;
        RuntimeSession = runtimeSession ?? new ModRuntimeSession();
        RuntimeSession.RegisterOwnedResourceAuditor(AuditOwnedResources);
    }

    internal NativeModLoadState(
        string modId,
        string? pluginTypeName,
        string entryAssemblyPath,
        ModRuntimeSession runtimeSession,
        NativeModShadowPackage? shadowPackage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        _modId = modId;
        _pluginTypeName = string.IsNullOrWhiteSpace(pluginTypeName)
            ? null
            : pluginTypeName;
        _entryAssemblyPath = Path.GetFullPath(entryAssemblyPath);
        _shadowPackage = shadowPackage;
        RuntimeSession = runtimeSession ?? throw new ArgumentNullException(nameof(runtimeSession));
        RuntimeSession.RegisterOwnedResourceAuditor(AuditOwnedResources);
    }

    public string EntryAssemblyPath => _entryAssemblyPath;
    internal NativeModShadowPackage? ShadowPackage
    {
        get
        {
            lock (_sync)
                return _shadowPackage;
        }
    }
    public ModRuntimeSession RuntimeSession { get; }
    public long LoadGeneration => RuntimeSession.Generation;
    public IReadOnlyList<NativeModUnmanagedLibraryIdentity> CurrentUnmanagedLibraries
    {
        get
        {
            lock (_sync)
                return _loadContext?.SnapshotUnmanagedLibraries() ??
                       Array.Empty<NativeModUnmanagedLibraryIdentity>();
        }
    }

    public IReadOnlyList<NativeModUnmanagedLibraryIdentity> RetiredUnmanagedLibraries
    {
        get
        {
            lock (_sync)
                return _retiredUnmanagedLibraries;
        }
    }

    internal IReadOnlyList<ModOwnedResourceSnapshot> CurrentOwnedResources
        => ModOwnedResourceRegistry.Snapshot(_boundRuntimeKey, includeRetired: false);

    internal IReadOnlyList<ModOwnedResourceSnapshot> RetiredOwnedResources
        => ModOwnedResourceRegistry.Snapshot(_boundRuntimeKey, includeRetired: true)
            .Where(resource => resource.Retired)
            .ToArray();

    public Assembly? Assembly
    {
        get
        {
            lock (_sync)
                return _assembly;
        }
    }

    public IModPlugin? Plugin
    {
        get
        {
            lock (_sync)
                return _plugin;
        }
    }

    public IModPlugin EnsureLoaded(ModIsolationManifest? expectedManifest = null)
    {
        lock (_sync)
        {
            if (_plugin != null && _assembly != null && _loadContext != null)
            {
                VerifyBootstrapIdentity(expectedManifest);
                return _plugin;
            }

            if (!File.Exists(_entryAssemblyPath))
                throw new FileNotFoundException(
                    $"Native MOD entry assembly was not found: {_entryAssemblyPath}",
                    _entryAssemblyPath);

            _shadowPackage?.Verify();
            var executionPath = _shadowPackage?.EntryAssemblyPath ?? _entryAssemblyPath;
            var context = new NativeModAssemblyLoadContext(_modId, executionPath);
            try
            {
                if (_boundRuntimeKey.Generation > 0)
                    context.BindRuntimeGeneration(_boundRuntimeKey.Generation);
                var assembly = context.LoadFromAssemblyPath(executionPath);
                VerifyBootstrapIdentity(expectedManifest, _entryAssemblyPath);
                var pluginType = _pluginTypeName == null
                    ? ModLoader.ResolvePluginType(assembly)
                    : assembly.GetType(_pluginTypeName, throwOnError: false, ignoreCase: false);
                if (pluginType == null)
                    throw new InvalidOperationException(
                        $"No concrete IModPlugin was found in {_entryAssemblyPath}.");
                if (!typeof(IModPlugin).IsAssignableFrom(pluginType) ||
                    pluginType.IsInterface ||
                    pluginType.IsAbstract)
                {
                    throw new InvalidOperationException(
                        $"Native MOD plugin type is no longer compatible: {_pluginTypeName}.");
                }

                var plugin = (IModPlugin)Activator.CreateInstance(pluginType)!;
                context.ReconcileObservedUnmanagedLibraries();
                _loadContext = context;
                _assembly = assembly;
                _plugin = plugin;
                return plugin;
            }
            catch
            {
                context.Unload();
                throw;
            }
        }
    }

    internal void BindShadowPackage(NativeModShadowPackage shadowPackage)
    {
        ArgumentNullException.ThrowIfNull(shadowPackage);
        NativeModAssemblyLoadContext? context = null;
        IModPlugin? retiredPlugin = null;
        lock (_sync)
        {
            if (_shadowPackage != null &&
                string.Equals(
                    _shadowPackage.CacheKey,
                    shadowPackage.CacheKey,
                    StringComparison.Ordinal))
            {
                return;
            }

            _shadowPackage = shadowPackage;
            if (_loadContext == null)
                return;
            retiredPlugin = _plugin;
            _plugin = null;
            _assembly = null;
            context = _loadContext;
            context.BeginRetirement();
            _retiredUnmanagedLibraries = context.SnapshotUnmanagedLibraries()
                .Select(identity => identity with { ContextRetired = true })
                .ToArray();
            _loadContext = null;
        }
        ModOwnedResourceRegistry.Retire(_boundRuntimeKey);
        try
        {
            if (retiredPlugin is IDisposable disposable)
                disposable.Dispose();
        }
        finally
        {
            context?.Unload();
        }
    }

    private void VerifyBootstrapIdentity(ModIsolationManifest? expectedManifest)
        => VerifyBootstrapIdentity(expectedManifest, _entryAssemblyPath);

    private static void VerifyBootstrapIdentity(
        ModIsolationManifest? expectedManifest,
        string assemblyPath)
    {
        if (expectedManifest is null)
            return;

        var expected = expectedManifest.OriginalAssembly;
        var actual = ModIsolationManifestFactory.ReadAssemblyIdentity(assemblyPath);
        if (!ModIsolationManifestFactory.MatchesAssemblyIdentity(expected, actual))
        {
            throw new InvalidDataException(
                $"MOD bootstrap assembly changed after isolation manifest binding: " +
                $"expected={expected.Name}/{expected.ModuleVersionId}/{expected.Sha256}, " +
                $"actual={actual.Name}/{actual.ModuleVersionId}/{actual.Sha256}.");
        }
    }

    public void BindRuntimeKey(ModRuntimeKey key)
    {
        if (!string.Equals(key.LoaderKind, ModEntry.NativeLoaderKind, StringComparison.Ordinal) ||
            !string.Equals(key.ModId, _modId, StringComparison.OrdinalIgnoreCase) ||
            key.Generation <= 0)
        {
            throw new InvalidOperationException(
                $"Native MOD runtime key mismatch mod={_modId} key={key}.");
        }
        lock (_sync)
        {
            _boundRuntimeKey = key;
            _retiredUnmanagedLibraries = Array.Empty<NativeModUnmanagedLibraryIdentity>();
            Volatile.Write(ref _nextUnmanagedAuditTimestamp, 0);
            _loadContext?.BindRuntimeGeneration(key.Generation);
        }
    }

    private void AuditOwnedResources(ModRuntimeKey key)
    {
        if (!_boundRuntimeKey.Matches(key))
            return;
        var now = Stopwatch.GetTimestamp();
        while (true)
        {
            var next = Volatile.Read(ref _nextUnmanagedAuditTimestamp);
            if (now < next)
                return;
            if (Interlocked.CompareExchange(
                    ref _nextUnmanagedAuditTimestamp,
                    now + UnmanagedAuditIntervalTicks,
                    next) == next)
            {
                break;
            }
        }

        NativeModAssemblyLoadContext? context;
        lock (_sync)
            context = _loadContext;
        context?.ReconcileObservedUnmanagedLibraries();
    }

    public void ReleaseContext()
    {
        NativeModAssemblyLoadContext? context;
        lock (_sync)
        {
            _plugin = null;
            _assembly = null;
            context = _loadContext;
            if (context != null)
            {
                context.BeginRetirement();
                _retiredUnmanagedLibraries = context.SnapshotUnmanagedLibraries()
                    .Select(identity => identity with { ContextRetired = true })
                    .ToArray();
            }
            _loadContext = null;
        }
        ModOwnedResourceRegistry.Retire(_boundRuntimeKey);
        context?.Unload();
    }
}

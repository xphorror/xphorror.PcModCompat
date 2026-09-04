using System.Reflection;
using System.Runtime.Loader;
using StArray.ModManager.Runtime;

namespace Xphorror.PcModCompat;

public static class PcCompatManagedLoader
{
    public static PcCompatManagedModSession Load(PcModManifest manifest, PcCompatLoadOptions? options = null)
    {
        options ??= new PcCompatLoadOptions();
        var pcModSession = options.PcModSession;
        var ownsPcModSession = false;
        if (pcModSession == null)
        {
            var request = PcCompatModSessionIdentity.Create(manifest, 1, 1);
            pcModSession = PcCompatModSessionLease.CreateLocal(request);
            ownsPcModSession = true;
        }
        if (pcModSession?.IsActive != true)
            throw new InvalidOperationException(
                $"Native PC MOD session is unavailable before managed load for mod={manifest.Id}.");

        var hasRewrittenAssembly = !string.IsNullOrWhiteSpace(options.TargetAssemblyPath);
        var hasProxyFolder = !string.IsNullOrWhiteSpace(options.ProxyFolder);
        if (hasRewrittenAssembly != hasProxyFolder)
        {
            throw new InvalidOperationException(
                "Rewritten managed execution requires both TargetAssemblyPath and ProxyFolder.");
        }
        if (!hasRewrittenAssembly && !options.AllowLegacyStubExecution)
        {
            throw new NotSupportedException(
                "Direct PC MOD execution against legacy Unity stubs is disabled. " +
                "Rewrite the assembly and bind it to generated Il2CppInterop proxies.");
        }

        var targetAssemblyPath = hasRewrittenAssembly
            ? Path.GetFullPath(options.TargetAssemblyPath)
            : ResolveTargetAssembly(manifest)
            ?? throw new FileNotFoundException("PC MOD target assembly was not found", manifest.FolderPath);
        if (!File.Exists(targetAssemblyPath))
            throw new FileNotFoundException("PC MOD target assembly was not found", targetAssemblyPath);

        var shimFolder = ResolveShimFolder(manifest, options);
        if (shimFolder == null)
            throw new DirectoryNotFoundException("PcModCompat shim folder was not found");

        var context = new PcCompatAssemblyLoadContext(
            manifest.Id,
            manifest.FolderPath,
            shimFolder,
            options.ProxyFolder,
            options.RewrittenAssemblyPaths,
            pcModSession);
        object? instance = null;
        PcCompatManagedModSession? session = null;
        var managedBindingsBound = false;
        try
        {
            var resourceSession = RunStage(
                "resolve managed session generation",
                () => ResolveResourceSessionIdentity(manifest, pcModSession));
            var resourceSessionGeneration = resourceSession.Generation;
            RunStage(
                "bind managed session roots",
                () => PcCompatManagedSessionBindings.Bind(manifest, resourceSessionGeneration));
            managedBindingsBound = true;

            var modEntry = RunStage(
                "create UnityModManager entry",
                () => CreateUnityModEntry(context, manifest));

            // Clear before any MOD code runs, not after bootstrap: the shim registries are statics that
            // outlive a single load when the shim assemblies resolve outside the collectible context, and
            // a MOD is free to do all of its patching from the bootstrap entry point. Clearing later would
            // discard exactly those registrations.
            RunStage("clear shim patch registry", () => ClearPatchRegistry(context));

            var bootstrapAttempted = false;
            var bootstrapSucceeded = false;
            var bootstrapAssemblyPath = !string.IsNullOrWhiteSpace(options.BootstrapAssemblyPath)
                ? Path.GetFullPath(options.BootstrapAssemblyPath)
                : manifest.EntryAssemblyPath;
            if (options.TryBootstrap && File.Exists(bootstrapAssemblyPath) && !string.IsNullOrWhiteSpace(manifest.EntryMethod))
            {
                if (!pcModSession.IsActive)
                    throw new InvalidOperationException(
                        $"Native PC MOD session expired before bootstrap for mod={manifest.Id}.");
                bootstrapAttempted = true;
                using var execution = PcCompatManagedExecutionContext.Enter(
                    new PcCompatManagedExecutionState(
                        manifest.Id,
                        resourceSessionGeneration,
                        PcCompatManagedExecutionPhase.Bootstrap));
                bootstrapSucceeded = TryInvokeBootstrap(
                    context,
                    manifest,
                    modEntry,
                    bootstrapAssemblyPath);
            }

            if (!pcModSession.IsActive)
                throw new InvalidOperationException(
                    $"Native PC MOD session expired before assembly load for mod={manifest.Id}.");
            var assembly = RunStage(
                "load rewritten assembly",
                () => context.LoadFromAssemblyPath(targetAssemblyPath));
            var setupCompleted = false;
            if (manifest.Kind == PcModKind.UnityModManager)
            {
                // UMM's EntryMethod is a static loader callback. It creates the MOD's event
                // adapter during bootstrap; treating its declaring type as a normal main MOD
                // instance makes reflection try to construct an abstract static class.
                if (!bootstrapAttempted || !bootstrapSucceeded)
                {
                    throw new InvalidOperationException(
                        $"UMM MOD bootstrap did not complete for mod={manifest.Id} " +
                        $"entry={manifest.EntryMethod}.");
                }

                instance = new PcCompatUmmLifecycleAdapter(modEntry);
                setupCompleted = true;
            }
            else
            {
                var mainType = RunStage(
                    "resolve main type",
                    () => assembly.GetType(ResolveMainTypeName(manifest), throwOnError: true)!);
                instance = RunStage(
                    "construct main instance",
                    () => Activator.CreateInstance(mainType)
                          ?? throw new InvalidOperationException(
                              $"Could not create PC MOD main instance: {mainType.FullName}"));

                using (PcCompatManagedExecutionContext.Enter(
                           new PcCompatManagedExecutionState(
                               manifest.Id,
                               resourceSessionGeneration,
                               PcCompatManagedExecutionPhase.Setup)))
                {
                    if (!pcModSession.IsActive)
                        throw new InvalidOperationException(
                            $"Native PC MOD session expired before CompatSetup for mod={manifest.Id}.");
                    RunStage(
                        "invoke CompatSetup",
                        () => InvokeLifecycle(
                            instance,
                            "CompatSetup",
                            new object?[] { manifest.FolderPath },
                            required: true));
                }
                setupCompleted = true;
            }

            var patches = RunStage(
                "snapshot registered patches",
                () => SnapshotPatches(context, manifest.Id));

            var enableCompleted = false;
            session = new PcCompatManagedModSession(
                manifest,
                context,
                assembly,
                instance,
                modEntry,
                patches,
                bootstrapAttempted,
                bootstrapSucceeded,
                setupCompleted,
                enableCompleted,
                resourceSessionGeneration,
                usesRewrittenAssembly: hasRewrittenAssembly,
                hasResourceRecipeSession: resourceSession.HasRecipeSession,
                options.RuntimeSession,
                 options.RuntimeKey,
                 pcModSession,
                 ownsPcModSession);
            // The managed session now owns the generation bindings and revokes them from Disable.
            managedBindingsBound = false;
            if (options.Enable)
            {
                if (!session.TryEnable(out var enableError))
                {
                    session.Dispose();
                    throw new InvalidOperationException(
                        $"Managed MOD enable failed for {manifest.Id}: {enableError}");
                }
            }

            return session;
        }
        catch
        {
            // A collectible ALC that never reached a session must not linger: any
            // Default-ALC static registrations made by bootstrap/setup would anchor
            // it forever and double-register on the next load attempt.
            if (session != null)
            {
                try
                {
                    session.Dispose();
                }
                catch
                {
                    // Best-effort cleanup on the failure path.
                }
            }
            else if (instance is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // Best-effort cleanup on the failure path.
                }
            }
            if (context.IsCollectible)
                context.Unload();
            if (managedBindingsBound)
            {
                var generation = ResolveBestEffortGeneration(manifest, pcModSession);
                PcCompatManagedSessionBindings.Clear(manifest.Id, generation);
            }
            if (ownsPcModSession)
                pcModSession.Dispose();
            throw;
        }
    }

    internal static long ResolveResourceSessionGeneration(
        PcModManifest manifest,
        PcCompatModSessionLease pcModSession)
        => ResolveResourceSessionIdentity(manifest, pcModSession).Generation;

    internal static PcCompatManagedResourceSessionIdentity ResolveResourceSessionIdentity(
        PcModManifest manifest,
        PcCompatModSessionLease pcModSession)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(pcModSession);

        var nativeGeneration = pcModSession.Request.ResourceGeneration;
        if (nativeGeneration <= 0)
            throw new InvalidOperationException(
                $"Native PC MOD session returned an invalid resource generation for mod={manifest.Id}.");

        if (PcCompatResourceRecipeRuntime.TryGetSessionGeneration(
                manifest.Id,
                out var recipeGeneration) &&
            recipeGeneration > 0)
        {
            if (recipeGeneration != nativeGeneration)
            {
                throw new InvalidOperationException(
                    $"PC MOD resource generation mismatch for mod={manifest.Id}: " +
                    $"native={nativeGeneration} recipe={recipeGeneration}.");
            }
            return new PcCompatManagedResourceSessionIdentity(
                recipeGeneration,
                HasRecipeSession: true);
        }

        // JPOV has no resource recipe. The native lease is still authoritative; zero would
        // leave bootstrap filesystem and network calls outside the MOD's generation scope.
        return new PcCompatManagedResourceSessionIdentity(
            nativeGeneration,
            HasRecipeSession: false);
    }

    private static long ResolveBestEffortGeneration(
        PcModManifest manifest,
        PcCompatModSessionLease pcModSession)
    {
        try
        {
            return ResolveResourceSessionGeneration(manifest, pcModSession);
        }
        catch
        {
            return pcModSession.Request.ResourceGeneration;
        }
    }

    public static string? ResolveShimFolder(PcModManifest manifest, PcCompatLoadOptions? options = null)
    {
        options ??= new PcCompatLoadOptions();
        if (!string.IsNullOrWhiteSpace(options.ShimFolder) && Directory.Exists(options.ShimFolder))
            return Path.GetFullPath(options.ShimFolder);

        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "pc_compat_shims"),
            Path.Combine(baseDir, "xphorror.PcModCompat", "out", "shims"),
            Path.Combine(baseDir, "out", "shims"),
            Path.Combine(manifest.FolderPath, "shims"),
            Path.Combine(Directory.GetParent(manifest.FolderPath)?.FullName ?? manifest.FolderPath, "pc_compat_shims"),
            Path.Combine(Directory.GetParent(manifest.FolderPath)?.FullName ?? manifest.FolderPath, "xphorror.PcModCompat", "out", "shims")
        };

        if (!OperatingSystem.IsAndroid())
        {
            foreach (var root in EnumerateAncestorRoots(baseDir)
                         .Concat(EnumerateAncestorRoots(Environment.CurrentDirectory)))
            {
                candidates.Add(Path.Combine(root, "xphorror.PcModCompat", "out", "shims"));
            }
        }

        return candidates
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(Directory.Exists);
    }

    private static IEnumerable<string> EnumerateAncestorRoots(string path)
    {
        DirectoryInfo? current;
        try
        {
            current = new DirectoryInfo(Path.GetFullPath(path));
        }
        catch
        {
            yield break;
        }

        for (var depth = 0; current != null && depth < 10; depth++, current = current.Parent)
            yield return current.FullName;
    }

    private static string? ResolveTargetAssembly(PcModManifest manifest)
    {
        if (manifest.Kind == PcModKind.JAMod && !string.IsNullOrWhiteSpace(manifest.JAModAssemblyFullPath))
            return manifest.JAModAssemblyFullPath;

        if (!string.IsNullOrWhiteSpace(manifest.EntryAssemblyPath))
            return manifest.EntryAssemblyPath;

        return Directory.GetFiles(manifest.FolderPath, "*.dll").FirstOrDefault();
    }

    private static string ResolveMainTypeName(PcModManifest manifest)
    {
        if (!string.IsNullOrWhiteSpace(manifest.JAModClassName))
            return manifest.JAModClassName!;

        if (string.IsNullOrWhiteSpace(manifest.EntryMethod))
            throw new InvalidOperationException("manifest has no JAMod class and no EntryMethod");

        var split = manifest.EntryMethod.LastIndexOf('.');
        if (split <= 0)
            throw new InvalidOperationException($"unsupported EntryMethod format: {manifest.EntryMethod}");

        return manifest.EntryMethod[..split];
    }

    private static object CreateUnityModEntry(PcCompatAssemblyLoadContext context, PcModManifest manifest)
    {
        var umm = context.LoadFromAssemblyName(new AssemblyName("UnityModManager"));
        var managerType = umm.GetType("UnityModManagerNet.UnityModManager", throwOnError: true)!;
        var infoType = umm.GetType("UnityModManagerNet.UnityModManager+ModInfo", throwOnError: true)!;
        var entryType = umm.GetType("UnityModManagerNet.UnityModManager+ModEntry", throwOnError: true)!;

        var info = Activator.CreateInstance(infoType)!;
        SetField(info, "Id", manifest.Id);
        SetField(info, "DisplayName", manifest.DisplayName);
        SetField(info, "Author", manifest.Author);
        SetField(info, "Version", manifest.Version);
        SetField(info, "AssemblyName", manifest.AssemblyName);
        SetField(info, "EntryMethod", manifest.EntryMethod);
        SetField(info, "Requirements", manifest.Requirements.ToArray());
        SetField(info, "LoadAfter", manifest.LoadAfter.ToArray());

        var entry = Activator.CreateInstance(entryType, info, manifest.FolderPath)!;
        var entries = managerType.GetField("modEntries", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        entries.GetType().GetMethod("Add")!.Invoke(entries, new[] { entry });
        return entry;
    }

    private static void SetField(object target, string name, object? value)
        => target.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance)!.SetValue(target, value);

    private static bool TryInvokeBootstrap(
        PcCompatAssemblyLoadContext context,
        PcModManifest manifest,
        object modEntry,
        string bootstrapAssemblyPath)
    {
        try
        {
            var entryAssembly = context.LoadFromAssemblyPath(bootstrapAssemblyPath);
            var (typeName, methodName) = SplitEntryMethod(manifest.EntryMethod);
            var type = entryAssembly.GetType(typeName, throwOnError: true)!;
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException(type.FullName, methodName);
            var parameters = BuildArguments(method, modEntry, manifest.FolderPath);
            var result = method.Invoke(null, parameters);
            return result is not bool success || success;
        }
        catch (Exception exception)
        {
            // Bootstrap failure is non-fatal for direct JAMod setup, but the reason
            // must stay visible: a partially executed bootstrap otherwise leaves an
            // unexplained half-initialized MOD in the diagnostics.
            StArray.ModManager.Manager.Logger.Warn(
                nameof(PcCompatManagedLoader),
                $"PC MOD bootstrap failed mod={manifest.Id} entry={manifest.EntryMethod}: {exception}");
            return false;
        }
    }

    private static (string TypeName, string MethodName) SplitEntryMethod(string entryMethod)
    {
        var split = entryMethod.LastIndexOf('.');
        if (split <= 0 || split == entryMethod.Length - 1)
            throw new InvalidOperationException($"unsupported EntryMethod format: {entryMethod}");
        return (entryMethod[..split], entryMethod[(split + 1)..]);
    }

    private static object?[] BuildArguments(MethodInfo method, object modEntry, string modFolder)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == 0)
            return Array.Empty<object?>();

        if (parameters.Length == 1)
        {
            if (parameters[0].ParameterType.FullName == "UnityModManagerNet.UnityModManager+ModEntry")
                return new[] { modEntry };
            if (parameters[0].ParameterType == typeof(string))
                return new object?[] { modFolder };
        }

        throw new NotSupportedException($"unsupported bootstrap signature: {method}");
    }

    private static void InvokeLifecycle(object instance, string methodName, object?[] args, bool required)
    {
        // Search the whole inheritance chain from the concrete MOD type, not only
        // its direct base: lifecycle hooks may be declared on the MOD class itself
        // or on a shim base further up.
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        if (method == null)
        {
            if (required)
                throw new MissingMethodException(instance.GetType().FullName, methodName);
            return;
        }

        method.Invoke(instance, args);
    }

    private sealed class PcCompatUmmLifecycleAdapter : IDisposable
    {
        private readonly object _entry;
        private readonly PropertyInfo _activeProperty;
        private readonly FieldInfo _updateField;
        private int _disposed;

        public PcCompatUmmLifecycleAdapter(object entry)
        {
            _entry = entry ?? throw new ArgumentNullException(nameof(entry));
            var entryType = entry.GetType();
            _activeProperty = entryType.GetProperty(
                                  "Active",
                                  BindingFlags.Public | BindingFlags.Instance)
                              ?? throw new MissingMemberException(entryType.FullName, "Active");
            _updateField = entryType.GetField(
                               "OnUpdate",
                               BindingFlags.Public | BindingFlags.Instance)
                           ?? throw new MissingFieldException(entryType.FullName, "OnUpdate");
        }

        public void CompatEnable()
        {
            ThrowIfDisposed();
            _activeProperty.SetValue(_entry, true);
            if (_activeProperty.GetValue(_entry) is not true)
                throw new InvalidOperationException("UMM MOD rejected activation.");
        }

        public void CompatUpdate(float deltaTime)
        {
            ThrowIfDisposed();
            var callback = _updateField.GetValue(_entry) as Delegate;
            callback?.DynamicInvoke(_entry, deltaTime);
        }

        public void CompatDisable()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            _activeProperty.SetValue(_entry, false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _activeProperty.SetValue(_entry, false);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(PcCompatUmmLifecycleAdapter));
        }
    }

    private static T RunStage<T>(string stage, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception)
        {
            throw BuildStageException(stage, exception);
        }
    }

    private static void RunStage(string stage, Action action)
        => RunStage(stage, () =>
        {
            action();
            return true;
        });

    private static InvalidOperationException BuildStageException(
        string stage,
        Exception exception)
    {
        var cause = exception;
        while (cause is TargetInvocationException { InnerException: not null } invocation)
            cause = invocation.InnerException!;

        return new InvalidOperationException(
            $"Managed MOD stage '{stage}' failed: {cause.GetType().Name}: {cause.Message}",
            cause);
    }

    private static void ClearPatchRegistry(PcCompatAssemblyLoadContext context)
    {
        foreach (var registry in PcCompatShimPatchRegistries.All)
        {
            if (!PcCompatShimPatchRegistries.TryResolve(context, registry, out var registryType))
                continue;

            registryType
                .GetMethod("ClearRegisteredPatches", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, null);
        }
    }

    private static IReadOnlyList<PcCompatPatchDescriptor> SnapshotPatches(PcCompatAssemblyLoadContext context, string modId)
    {
        var descriptors = new List<PcCompatPatchDescriptor>();
        foreach (var registry in PcCompatShimPatchRegistries.All)
        {
            if (!PcCompatShimPatchRegistries.TryResolve(context, registry, out var registryType))
                continue;

            var snapshot = PcCompatShimPatchRegistries.Snapshot(registryType);
            if (snapshot == null || snapshot.Length == 0)
                continue;

            foreach (var record in snapshot)
            {
                var type = record.GetType();
                // Read through nullable lookups: the two registries share the core property names but
                // not every optional one, and a missing property must not take down the load stage.
                string Get(string name) => type.GetProperty(name)?.GetValue(record)?.ToString() ?? string.Empty;
                long GetInt64(string name)
                    => Convert.ToInt64(
                        type.GetProperty(name)?.GetValue(record) ?? 0,
                        System.Globalization.CultureInfo.InvariantCulture);
                int GetInt32(string name, int fallback = 0)
                {
                    var value = type.GetProperty(name)?.GetValue(record);
                    return value == null
                        ? fallback
                        : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
                }
                string[] GetStrings(string name)
                    => type.GetProperty(name)?.GetValue(record) is IEnumerable<string> values
                        ? values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
                        : Array.Empty<string>();

                descriptors.Add(new PcCompatPatchDescriptor
                {
                    ModId = modId,
                    TargetType = Get("TargetType"),
                    TargetMethod = Get("TargetMethod"),
                    Kind = ParseKind(Get("Kind")),
                    CallbackType = Get("CallbackType"),
                    CallbackMethod = Get("CallbackMethod"),
                    PatchOwner = Get("HarmonyId") is { Length: > 0 } harmonyId
                        ? harmonyId
                        : Get("PatchId"),
                    RegistrationIndex = GetInt64("RegistrationIndex"),
                    Priority = GetInt32("Priority", -1),
                    Before = GetStrings("Before"),
                    After = GetStrings("After"),
                    Source = registry.DescriptorSource,
                    Status = ParseStatus(Get("Status")),
                    Reason = Get("Reason")
                });
            }
        }

        return descriptors;
    }

    private static PcCompatPatchKind ParseKind(string value)
        => Enum.TryParse<PcCompatPatchKind>(value, ignoreCase: true, out var kind)
            ? kind
            : PcCompatPatchKind.Unknown;

    /// <summary>
    /// Maps a registry's status string onto the host enum. An absent or unrecognised value stays
    /// <c>RegisteredOnly</c> - claiming Supported on a guess is exactly the kind of overstatement the
    /// fail-closed rule exists to prevent.
    /// </summary>
    private static PcCompatPatchStatus ParseStatus(string value)
        => value switch
        {
            "unsupported" => PcCompatPatchStatus.Unsupported,
            _ => PcCompatPatchStatus.RegisteredOnly
        };
}

internal readonly record struct PcCompatManagedResourceSessionIdentity(
    long Generation,
    bool HasRecipeSession);

internal sealed class PcCompatAssemblyLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> SharedRuntimeAssemblyNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "Il2CppInterop.Common",
        "Il2CppInterop.Runtime",
        "Il2CppInterop.HarmonySupport",
        "Il2Cppmscorlib",
        "StArray.ModManager",
        "StArray.ModManager.Android",
        "xphorror.PcModCompat"
    };

    private readonly Dictionary<string, string> _assemblyPaths;
    private readonly HashSet<string> _proxyAssemblyNames;
    private readonly PcCompatModSessionLease? _pcModSession;

    public PcCompatAssemblyLoadContext(
        string modId,
        string modFolder,
        string shimFolder,
        string? proxyFolder = null,
        IReadOnlyDictionary<string, string>? rewrittenAssemblyPaths = null,
        PcCompatModSessionLease? pcModSession = null)
        : base($"PcCompat:{modId}", isCollectible: true)
    {
        _pcModSession = pcModSession;
        _proxyAssemblyNames = !string.IsNullOrWhiteSpace(proxyFolder) && Directory.Exists(proxyFolder)
            ? Directory.GetFiles(proxyFolder, "*.dll")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _assemblyPaths = Directory.GetFiles(shimFolder, "*.dll")
            .Concat(Directory.GetFiles(modFolder, "*.dll"))
            .Where(path => !_proxyAssemblyNames.Contains(Path.GetFileNameWithoutExtension(path)))
            .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (rewrittenAssemblyPaths != null)
        {
            foreach (var (assemblyName, path) in rewrittenAssemblyPaths)
            {
                if (string.IsNullOrWhiteSpace(assemblyName) || string.IsNullOrWhiteSpace(path))
                    throw new InvalidDataException("Rewritten managed assembly mapping contains an empty entry.");
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath))
                    throw new FileNotFoundException(
                        $"Rewritten managed assembly is missing: {assemblyName}",
                        fullPath);
                _assemblyPaths[assemblyName] = fullPath;
            }
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (_pcModSession != null && !_pcModSession.IsActive)
            throw new InvalidOperationException(
                $"PC MOD session expired while resolving assembly '{assemblyName.Name}'.");
        var simpleName = assemblyName.Name ?? string.Empty;
        if (_proxyAssemblyNames.Contains(simpleName) ||
            SharedRuntimeAssemblyNames.Contains(simpleName))
        {
            return AssemblyLoadContext.Default.Assemblies.FirstOrDefault(
                       assembly => string.Equals(
                           assembly.GetName().Name,
                           simpleName,
                           StringComparison.OrdinalIgnoreCase)) ??
                   throw new FileNotFoundException(
                       $"Shared runtime assembly is not loaded in the default ALC: {simpleName}");
        }

        if (_assemblyPaths.TryGetValue(simpleName, out var path))
            return LoadFromAssemblyPath(path);

        return null;
    }
}

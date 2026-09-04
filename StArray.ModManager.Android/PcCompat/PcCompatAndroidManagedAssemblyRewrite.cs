using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Android.PcCompat;

internal static class PcCompatAndroidManagedAssemblyRewrite
{
    private const string CacheFormatVersion =
        "xphorror.pcmod-managed-cache.v86-keyviewer-lane-origin-prefix";
    private const string CollectionBridgeAbi =
        "PcCompatCollectionBridge.v4-null-source-initialization+PcCompatAbiBridge.v4-stringbuilder-native-materialization+" +
        "PcCompatJsonBridge.v1+PcCompatReversePatchBridge.v5-jpov-game-state+" +
        "PcCompatManagedResourceBridge.v3-virtual-bundle-unload+PcCompatManagedComponentBridge.v13-native-component-result-rewrap+" +
        "PcCompatManagedLogBridge.v2-object-messages+PcCompatProxyCastBridge.v1+PcCompatManagedIoBridge.v2+" +
        "PcCompatManagedPollingBridge.v1+PcCompatManagedImGuiBridge.v20-selection-grid-transition+" +
        "PcCompatManagedSettingsTransaction.v1+" +
        "PcCompatManagedSettingsDelegateBridge.v1+PcCompatManagedThreadBridge.v2-background-scope+" +
        "PcCompatLegacyInputBridge.v3-hotpath-diagnostics-removed+PcCompatManagedApplicationBridge.v1+" +
        "PcCompatManagedPathBridge.v5-directory-file-enumeration+" +
        "PcCompatManagedNetworkBridge.v1+PcCompatManagedEventSubscriptionBridge.v4-proxy-source-delegate+" +
        "PcCompatProxySurface.v2-nullable-vector2+VirtualBundle.v2-unload-semantics+" +
         "PcCompatOpaqueHandleBridge.v1+PcCompatManagedDynamicGetterBridge.v4-proxy-logical-members+" +
         "PcCompatManagedSnapshotScalarBridge.v1+PcCompatManagedCallbackLeaseGate.v2+" +
         "PcCompatManagedCallbackDispatch.v2-shared-generic-filter+" +
         "PcCompatManagedFontBridge.v5-font-final-binding";
    private const string CompleteMarker = "complete.marker";
    private static long s_tempSequence;
    private static readonly object FinalizeLock = new();
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void Install()
        => PcCompatManagedAssemblyRewrite.RegisterProvider(Prepare);

    private static PcCompatManagedAssemblyBundleInfo Prepare(
        PcModManifest manifest,
        PcCompatStaticPatchScanReport staticScan,
        CancellationToken cancellationToken)
    {
        var proxyDirectory = Path.Combine(AppContext.BaseDirectory, "pc_compat_proxies");
        if (!Directory.Exists(proxyDirectory))
            throw new DirectoryNotFoundException($"PcCompat proxy directory is missing: {proxyDirectory}");

        var proxyFiles = Directory.EnumerateFiles(proxyDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (proxyFiles.Length == 0)
            throw new InvalidOperationException("PcCompat proxy directory contains no assemblies.");
        var proxyAssemblyNames = proxyFiles
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assemblyCatalog = PcCompatManagedAssemblyCatalog.Discover(manifest, proxyAssemblyNames);
        if (assemblyCatalog.Count == 0 || assemblyCatalog.Count(item => item.IsPrimary) != 1)
            throw new InvalidDataException("PC MOD managed assembly catalog has no unique primary assembly.");
        var ownedAssemblyPaths = assemblyCatalog.Select(item => item.InputPath).ToArray();
        var proxySurfaceHash = PcCompatKeyViewerBehaviorScanner.ComputeProxySurfaceHash(
            proxyFiles,
            cancellationToken);

        var bridgeRewrites = assemblyCatalog.ToDictionary(
            item => item.AssemblyName,
            item => BuildManagedBridgeRewrites(
                staticScan,
                item.InputPath,
                item.IsPrimary,
                manifest.Id),
            StringComparer.OrdinalIgnoreCase);
        var callBridgeRewrites = BuildManagedCallBridgeRewrites(manifest.Id);
        var fieldConstantRewrites = BuildManagedFieldConstantRewrites();
        var managedRenderComponents = BuildManagedRenderComponents(staticScan);
        var proxyCastBridge = new ManagedProxyCastBridgeSpec(
            typeof(PcCompatProxyCastBridge).FullName
            ?? throw new InvalidOperationException("PcCompat proxy cast bridge type has no full name."),
            nameof(PcCompatProxyCastBridge.IsInstance),
            nameof(PcCompatProxyCastBridge.Cast));
        var bridgeAssembly = typeof(PcCompatReversePatchBridge).Assembly;
        if (!ReferenceEquals(typeof(PcCompatOpaqueHandleBridge).Assembly, bridgeAssembly))
        {
            throw new InvalidOperationException(
                "PcCompat opaque-handle bridge must be hosted by the managed bridge assembly.");
        }
        var bridgeAssemblyPath = PcCompatManagedAssemblyRewrite.ResolveRuntimeAssemblyPath(
            bridgeAssembly.Location,
            bridgeAssembly.GetName().Name
            ?? throw new InvalidOperationException("PcCompat managed bridge assembly has no name."),
            AppContext.BaseDirectory);

        var cacheKey = ComputeCacheKey(
            assemblyCatalog,
            proxySurfaceHash,
            bridgeAssemblyPath,
            bridgeRewrites,
            callBridgeRewrites,
            fieldConstantRewrites,
            proxyCastBridge,
            managedRenderComponents,
            cancellationToken);
        var modsRoot = Directory.GetParent(Path.GetFullPath(manifest.FolderPath))?.FullName ?? manifest.FolderPath;
        var bundleRoot = Path.Combine(modsRoot, "compiled", SanitizePathSegment(manifest.Id), "managed");
        var finalDirectory = Path.Combine(bundleRoot, cacheKey);
        var outputPaths = assemblyCatalog.ToDictionary(
            item => item.AssemblyName,
            item => Path.Combine(finalDirectory, SanitizePathSegment(item.AssemblyName) + ".dll"),
            StringComparer.OrdinalIgnoreCase);
        var reportPaths = assemblyCatalog.ToDictionary(
            item => item.AssemblyName,
            item => Path.Combine(
                finalDirectory,
                SanitizePathSegment(item.AssemblyName) + ".managed_rewrite_report.json"),
            StringComparer.OrdinalIgnoreCase);
        if (outputPaths.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputPaths.Count ||
            reportPaths.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != reportPaths.Count)
        {
            throw new InvalidDataException(
                "PC MOD managed assembly names collide after cache path sanitization.");
        }
        var completePath = Path.Combine(finalDirectory, CompleteMarker);
        var keyViewerAdapterPath = Path.Combine(finalDirectory, "keyviewer_adapter.json");
        var keyViewerIssuesPath = Path.Combine(finalDirectory, "keyviewer_adapter_issues.json");
        var keyViewerManifestPath = Path.Combine(finalDirectory, "keyviewer_adapter_manifest.txt");

        if (IsCompleteBundle(
                completePath,
                outputPaths.Values,
                reportPaths.Values,
                keyViewerAdapterPath,
                keyViewerIssuesPath,
                keyViewerManifestPath))
        {
            return BuildInfo(
                cacheKey,
                finalDirectory,
                assemblyCatalog,
                outputPaths,
                reportPaths,
                completePath,
                keyViewerAdapterPath,
                keyViewerIssuesPath,
                cacheHit: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var keyViewerScan = PcCompatKeyViewerBehaviorScanner.Scan(
            manifest,
            proxySurfaceHash,
            assemblyCatalog,
            cancellationToken: cancellationToken);
        var keyViewerAdapterJson = keyViewerScan.Adapter?.ToJson();
        var keyViewerIssuesJson = JsonSerializer.Serialize(
            keyViewerScan.Issues,
            ReportJsonOptions);
        var keyViewerManifest = BuildKeyViewerCacheManifest(
            keyViewerAdapterJson,
            keyViewerIssuesJson);

        Directory.CreateDirectory(bundleRoot);
        var tempSuffix =
            $"{Environment.ProcessId:x}-{Environment.CurrentManagedThreadId:x}-" +
            $"{Environment.TickCount64:x}-{Interlocked.Increment(ref s_tempSequence):x}";
        var tempDirectory = Path.Combine(bundleRoot, $".tmp-{cacheKey}-{tempSuffix}");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(tempDirectory);
            foreach (var assembly in assemblyCatalog)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var temporaryOutput = Path.Combine(
                    tempDirectory,
                    Path.GetFileName(outputPaths[assembly.AssemblyName]));
                var temporaryReport = Path.Combine(
                    tempDirectory,
                    Path.GetFileName(reportPaths[assembly.AssemblyName]));
                var report = ModAssemblyRewriteApi.Rewrite(
                    assembly.InputPath,
                    temporaryOutput,
                    proxyDirectory,
                    temporaryReport,
                    managedBridgeAssemblyPath: bridgeAssemblyPath,
                    managedBridgeRewrites: bridgeRewrites[assembly.AssemblyName],
                    managedCallBridgeRewrites: callBridgeRewrites,
                    managedFieldConstantRewrites: fieldConstantRewrites,
                    managedProxyCastBridge: proxyCastBridge,
                    managedOwnedAssemblyPaths: ownedAssemblyPaths,
                    managedReadProgressGuard: new ManagedReadProgressGuardSpec(
                        typeof(PcCompatManagedIoBridge).FullName
                        ?? throw new InvalidOperationException(
                            "PcCompat managed IO bridge type has no full name."),
                        nameof(PcCompatManagedIoBridge.RequireFileReadProgress),
                        nameof(PcCompatManagedIoBridge.TryReadFileExactly)),
                    managedPollingWaitRewrite: new ManagedPollingWaitRewriteSpec(
                        typeof(PcCompatManagedPollingBridge).FullName
                        ?? throw new InvalidOperationException(
                            "PcCompat managed polling bridge type has no full name."),
                        nameof(PcCompatManagedPollingBridge.WaitForCoarseClockAdvance)),
                    managedOptionalDelegateRewrite: new ManagedOptionalDelegateRewriteSpec(
                        "JALib",
                        "JALib.Tools.SettingGUI",
                        "AddSetting",
                        "System.Action",
                        typeof(PcCompatManagedSettingsDelegateBridge).FullName
                        ?? throw new InvalidOperationException(
                            "PcCompat managed settings delegate bridge type has no full name."),
                        nameof(PcCompatManagedSettingsDelegateBridge.CreateOptionalAction)),
                    managedWritableCollections: BuildManagedWritableCollections(),
                    managedRenderComponents: managedRenderComponents);
                cancellationToken.ThrowIfCancellationRequested();

                if (report.Issues.Count != 0 || report.MethodIssues.Count != 0 ||
                    report.ManagedBridgeIssues.Count != 0 ||
                    !report.OutputWritten || !File.Exists(temporaryOutput))
                {
                    throw new InvalidOperationException(
                        $"Managed rewrite did not produce a complete assembly for " +
                        $"{assembly.AssemblyName}: issues={report.Issues.Count} " +
                        $"methodIssues={report.MethodIssues.Count} " +
                        $"bridgeIssues={report.ManagedBridgeIssues.Count} " +
                        $"output={report.OutputWritten}" +
                        DescribeFirstRewriteIssue(report));
                }

                File.WriteAllText(
                    temporaryReport,
                    JsonSerializer.Serialize(
                        report with { OutputPath = outputPaths[assembly.AssemblyName] },
                        ReportJsonOptions),
                    new UTF8Encoding(false));
            }

            if (keyViewerAdapterJson != null)
            {
                File.WriteAllText(
                    Path.Combine(tempDirectory, "keyviewer_adapter.json"),
                    keyViewerAdapterJson,
                    new UTF8Encoding(false));
            }
            File.WriteAllText(
                Path.Combine(tempDirectory, "keyviewer_adapter_issues.json"),
                keyViewerIssuesJson,
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(tempDirectory, "keyviewer_adapter_manifest.txt"),
                keyViewerManifest,
                new UTF8Encoding(false));

            File.WriteAllText(Path.Combine(tempDirectory, "cache_key.txt"), cacheKey, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(tempDirectory, "format_version.txt"), CacheFormatVersion, new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(tempDirectory, CompleteMarker),
                DateTimeOffset.UtcNow.ToString("O"),
                new UTF8Encoding(false));

            lock (FinalizeLock)
            {
                if (IsCompleteBundle(
                        completePath,
                        outputPaths.Values,
                        reportPaths.Values,
                        keyViewerAdapterPath,
                        keyViewerIssuesPath,
                        keyViewerManifestPath))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                    return BuildInfo(
                        cacheKey,
                        finalDirectory,
                        assemblyCatalog,
                        outputPaths,
                        reportPaths,
                        completePath,
                        keyViewerAdapterPath,
                        keyViewerIssuesPath,
                        cacheHit: true);
                }

                if (Directory.Exists(finalDirectory))
                    Directory.Delete(finalDirectory, recursive: true);
                Directory.Move(tempDirectory, finalDirectory);
            }

            return BuildInfo(
                cacheKey,
                finalDirectory,
                assemblyCatalog,
                outputPaths,
                reportPaths,
                completePath,
                keyViewerAdapterPath,
                keyViewerIssuesPath,
                cacheHit: false);
        }
        catch
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
            throw;
        }
    }

    private static PcCompatManagedAssemblyBundleInfo BuildInfo(
        string cacheKey,
        string bundleDirectory,
        IReadOnlyList<PcCompatManagedAssemblyDescriptor> assemblyCatalog,
        IReadOnlyDictionary<string, string> outputPaths,
        IReadOnlyDictionary<string, string> reportPaths,
        string completePath,
        string keyViewerAdapterPath,
        string keyViewerIssuesPath,
        bool cacheHit)
    {
        var rewrites = 0;
        var passthroughs = 0;
        var managedBridgeRewrites = 0;
        foreach (var assembly in assemblyCatalog)
        {
            var reportPath = reportPaths[assembly.AssemblyName];
            using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
            var root = document.RootElement;
            rewrites += root.TryGetProperty("rewrites", out var rewriteItems)
                ? rewriteItems.GetArrayLength()
                : 0;
            passthroughs += root.TryGetProperty("passthroughs", out var passthroughItems)
                ? passthroughItems.GetArrayLength()
                : 0;
            var issues = root.TryGetProperty("issues", out var issueItems)
                ? issueItems.GetArrayLength()
                : -1;
            var methodIssues = root.TryGetProperty("methodIssues", out var methodIssueItems)
                ? methodIssueItems.GetArrayLength()
                : -1;
            var bridgeRewrites = root.TryGetProperty(
                "managedBridgeRewrites",
                out var managedBridgeRewriteItems)
                ? managedBridgeRewriteItems.GetArrayLength()
                : -1;
            var managedBridgeIssues = root.TryGetProperty(
                "managedBridgeIssues",
                out var managedBridgeIssueItems)
                ? managedBridgeIssueItems.GetArrayLength()
                : -1;
            var outputWritten = root.TryGetProperty("outputWritten", out var outputWrittenValue) &&
                                outputWrittenValue.GetBoolean();
            if (issues != 0 || methodIssues != 0 || managedBridgeIssues != 0 ||
                bridgeRewrites < 0 || !outputWritten)
            {
                throw new InvalidDataException(
                    $"Cached managed rewrite report is incomplete: {reportPath}");
            }
            managedBridgeRewrites += bridgeRewrites;
        }

        var primary = assemblyCatalog.Single(item => item.IsPrimary);
        var bootstrap = assemblyCatalog.SingleOrDefault(item => item.IsBootstrap);
        var inputs = assemblyCatalog.ToDictionary(
            item => item.AssemblyName,
            item => item.InputPath,
            StringComparer.OrdinalIgnoreCase);
        var outputs = outputPaths.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.OrdinalIgnoreCase);

        return new PcCompatManagedAssemblyBundleInfo
        {
            CacheKey = cacheKey,
            BundleDirectory = bundleDirectory,
            InputAssemblyPath = primary.InputPath,
            RewrittenAssemblyPath = outputPaths[primary.AssemblyName],
            ReportPath = reportPaths[primary.AssemblyName],
            CompleteMarkerPath = completePath,
            CacheHit = cacheHit,
            RewrittenInstructions = rewrites + managedBridgeRewrites,
            PassthroughInstructions = passthroughs,
            ManagedBridgeRewrites = managedBridgeRewrites,
            InputAssemblyPaths = inputs,
            RewrittenAssemblyPaths = outputs,
            BootstrapAssemblyName = bootstrap?.AssemblyName,
            KeyViewerAdapterPath = File.Exists(keyViewerAdapterPath)
                ? keyViewerAdapterPath
                : null,
            KeyViewerScanIssuesPath = File.Exists(keyViewerIssuesPath)
                ? keyViewerIssuesPath
                : null
        };
    }

    private static string ComputeCacheKey(
        IReadOnlyList<PcCompatManagedAssemblyDescriptor> assemblyCatalog,
        string proxySurfaceHash,
        string bridgeAssemblyPath,
        IReadOnlyDictionary<string, IReadOnlyList<ManagedBridgeRewriteSpec>> bridgeRewrites,
        IReadOnlyList<ManagedCallBridgeRewriteSpec> callBridgeRewrites,
        IReadOnlyList<ManagedFieldConstantRewriteSpec> fieldConstantRewrites,
        ManagedProxyCastBridgeSpec proxyCastBridge,
        IReadOnlyList<ManagedRenderComponentSpec> managedRenderComponents,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CacheFormatVersion);
        builder.AppendLine(ModAssemblyRewriteApi.FormatVersion);
        builder.AppendLine(CollectionBridgeAbi);
        builder.AppendLine(PcCompatKeyViewerBehaviorScanner.CurrentAnalyzerVersion);
        foreach (var assembly in assemblyCatalog.OrderBy(
                     item => item.AssemblyName,
                     StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append("mod-assembly|");
            builder.Append(assembly.AssemblyName);
            builder.Append('|');
            builder.Append(Path.GetFileName(assembly.InputPath));
            builder.Append('|');
            builder.Append(assembly.IsPrimary ? "primary" : "companion");
            builder.Append('|');
            builder.Append(assembly.IsBootstrap ? "bootstrap" : "runtime");
            builder.Append('|');
            builder.AppendLine(HashFile(assembly.InputPath, cancellationToken));
        }
        builder.Append("proxy-surface|");
        builder.AppendLine(proxySurfaceHash);
        builder.Append("managed-bridge|");
        builder.Append(Path.GetFileName(bridgeAssemblyPath));
        builder.Append('|');
        builder.AppendLine(HashFile(bridgeAssemblyPath, cancellationToken));
        builder.Append("proxy-cast|");
        builder.Append(proxyCastBridge.BridgeType);
        builder.Append('|');
        builder.Append(proxyCastBridge.IsInstanceMethod);
        builder.Append('|');
        builder.AppendLine(proxyCastBridge.CastMethod);
        foreach (var (assemblyName, specs) in bridgeRewrites.OrderBy(
                     item => item.Key,
                     StringComparer.OrdinalIgnoreCase))
        {
            foreach (var spec in specs
                         .OrderBy(item => item.SourceType, StringComparer.Ordinal)
                         .ThenBy(item => item.SourceMethod, StringComparer.Ordinal))
            {
                builder.Append("reverse-bridge|");
                builder.Append(assemblyName);
                builder.Append('|');
                builder.Append(spec.SourceType);
                builder.Append('|');
                builder.Append(spec.SourceMethod);
                builder.Append('|');
                builder.Append(string.Join(";", spec.SourceParameterTypes));
                builder.Append('|');
                builder.Append(spec.BridgeType);
                builder.Append('|');
                builder.Append(spec.AppendCallsiteToken ? "callsite-token" : "no-token");
                builder.Append('|');
                builder.Append(spec.AppendOwnerId ?? "-");
                builder.Append('|');
                builder.AppendLine(spec.BridgeMethod);
            }
        }
        foreach (var spec in callBridgeRewrites
                         .OrderBy(item => item.SourceAssembly, StringComparer.Ordinal)
                         .ThenBy(item => item.SourceType, StringComparer.Ordinal)
                         .ThenBy(item => item.SourceMethod, StringComparer.Ordinal))
        {
            builder.Append("managed-call-bridge|");
            builder.Append(spec.SourceAssembly);
            builder.Append('|');
            builder.Append(spec.SourceType);
            builder.Append('|');
            builder.Append(spec.SourceMethod);
            builder.Append('|');
            builder.Append(spec.SourceIsStatic ? "static" : "instance");
            builder.Append('|');
            builder.Append(spec.SourceGenericArity);
            builder.Append('|');
            builder.Append(spec.SourceReturnType);
            builder.Append('|');
            builder.Append(string.Join(";", spec.SourceParameterTypes));
            builder.Append('|');
            builder.Append(spec.BridgeType);
            builder.Append('|');
            builder.Append(spec.BridgeMethod);
            builder.Append('|');
            builder.Append(spec.InstanceForwarding);
            builder.Append('|');
            builder.Append(spec.AllowObjectReturnCast ? "cast" : "exact");
            builder.Append('|');
            builder.Append(spec.EraseSourceTypeToObject ? "erase-object" : "preserve-type");
            builder.Append('|');
            builder.Append(spec.GenericArgumentFilter);
            builder.Append('|');
            builder.Append(spec.AllowObjectParameterForwarding ? "object-params" : "exact-params");
            builder.Append('|');
            builder.Append(spec.AllowUnproxiedSource ? "unproxied-source" : "proxy-source");
            builder.Append('|');
            builder.Append(spec.BoxLastValueTypeArgument ? "box-last" : "no-box-last");
            builder.Append('|');
            builder.Append(spec.AllowValueTypeReturnUnbox ? "unbox-return" : "exact-return");
            builder.Append('|');
            builder.Append(string.Join(",", spec.BridgeGenericArgumentsFromSourceParameters ?? []));
            builder.Append('|');
            builder.Append(spec.ErasedTypeAssembly ?? "-");
            builder.Append('|');
            builder.Append(spec.ErasedType ?? "-");
            builder.Append('|');
            builder.Append(spec.AppendCallsiteToken ? "callsite-token" : "no-token");
            builder.Append('|');
            builder.Append(spec.AppendOwnerId ?? "-");
            builder.Append('|');
            // Behavior-affecting flags must join the cache key: flipping one without a
            // version bump would otherwise let an old rewritten assembly stay cached.
            builder.Append(spec.SourceIsConstructor ? "newobj" : "call");
            builder.Append('|');
            builder.AppendLine($"erase-arity={spec.EraseBridgeGenericArity}");
        }
        foreach (var spec in fieldConstantRewrites
                     .OrderBy(item => item.SourceAssembly, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.SourceType, StringComparer.Ordinal)
                     .ThenBy(item => item.SourceField, StringComparer.Ordinal))
        {
            builder.Append("managed-field-constant|");
            builder.Append(spec.SourceAssembly);
            builder.Append('|');
            builder.Append(spec.SourceType);
            builder.Append('|');
            builder.Append(spec.SourceField);
            builder.Append('|');
            builder.Append(spec.SourceFieldType);
            builder.Append('|');
            builder.AppendLine(spec.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        // Registering or unregistering a writable collection changes which bridge the getter resolves
        // to, so the list has to be part of the key or an old rewritten assembly stays cached.
        foreach (var spec in BuildManagedWritableCollections()
                     .OrderBy(item => item.SourceAssembly, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.SourceType, StringComparer.Ordinal)
                     .ThenBy(item => item.SourceProperty, StringComparer.Ordinal))
        {
            builder.Append("managed-writable-collection|");
            builder.Append(spec.SourceAssembly);
            builder.Append('|');
            builder.Append(spec.SourceType);
            builder.Append('|');
            builder.Append(spec.SourceProperty);
            builder.Append('|');
            builder.Append(spec.ElementType);
            builder.Append('|');
            builder.Append(spec.BridgeType);
            builder.Append('|');
            builder.AppendLine(string.Join(
                ";",
                spec.BoundCopyMethod,
                spec.AddMethod,
                spec.RemoveMethod,
                spec.ClearMethod,
                spec.InsertMethod));
        }
        // Registering a render component lifts the "base chain must stay in MOD-owned modules" rule
        // for that type and blanks its base constructor call, so an unversioned change to the list
        // would otherwise leave a stale rewritten assembly cached.
        foreach (var spec in managedRenderComponents
                     .OrderBy(item => item.ComponentAssembly, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.ComponentType, StringComparer.Ordinal))
        {
            builder.Append("managed-render-component|");
            builder.Append(spec.ComponentAssembly);
            builder.Append('|');
            builder.Append(spec.ComponentType);
            builder.Append('|');
            builder.Append(spec.BaseAssembly);
            builder.Append('|');
            builder.Append(spec.BaseType);
            builder.Append('|');
            builder.AppendLine(spec.ConstructorNoOpBaseCall ? "ctor-pop" : "ctor-intact");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }

    private static bool IsCompleteBundle(
        string completePath,
        IEnumerable<string> outputPaths,
        IEnumerable<string> reportPaths,
        string keyViewerAdapterPath,
        string keyViewerIssuesPath,
        string keyViewerManifestPath)
    {
        if (!File.Exists(completePath) ||
            !outputPaths.All(File.Exists) ||
            !reportPaths.All(File.Exists) ||
            !File.Exists(keyViewerIssuesPath) ||
            !File.Exists(keyViewerManifestPath))
        {
            return false;
        }
        try
        {
            var manifest = File.ReadAllLines(keyViewerManifestPath)
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
            if (!manifest.TryGetValue("adapter", out var expectedAdapterHash) ||
                !manifest.TryGetValue("issues", out var expectedIssuesHash) ||
                !HashText(File.ReadAllText(keyViewerIssuesPath)).Equals(
                    expectedIssuesHash,
                    StringComparison.Ordinal))
                return false;

            using var issuesDocument = JsonDocument.Parse(File.ReadAllText(keyViewerIssuesPath));
            if (issuesDocument.RootElement.ValueKind != JsonValueKind.Array)
                return false;
            if (expectedAdapterHash == "none")
                return !File.Exists(keyViewerAdapterPath);
            if (!File.Exists(keyViewerAdapterPath))
                return false;
            var adapterJson = File.ReadAllText(keyViewerAdapterPath);
            if (!HashText(adapterJson).Equals(expectedAdapterHash, StringComparison.Ordinal))
                return false;
            var adapter = PcCompatKeyViewerAdapterDocument.FromJson(adapterJson);
            return adapter != null && PcCompatKeyViewerAdapterValidator.Validate(adapter).IsValid;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static string BuildKeyViewerCacheManifest(
        string? adapterJson,
        string issuesJson)
        => $"adapter={(adapterJson == null ? "none" : HashText(adapterJson))}\n" +
           $"issues={HashText(issuesJson)}\n";

    private static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static IReadOnlyList<ManagedBridgeRewriteSpec> BuildManagedBridgeRewrites(
        PcCompatStaticPatchScanReport staticScan,
        string assemblyPath,
        bool includeUnscoped,
        string modId)
    {
        var bridgeType = typeof(PcCompatReversePatchBridge).FullName
            ?? throw new InvalidOperationException("PcCompat reverse-patch bridge type has no full name.");
        var rewrites = new Dictionary<string, ManagedBridgeRewriteSpec>(StringComparer.Ordinal);
        foreach (var patch in staticScan.ActivePatches
                     .Where(item => item.Kind == PcCompatPatchKind.ReversePatch)
                     .Where(item => !string.IsNullOrWhiteSpace(item.CallbackAssemblyPath)
                         ? string.Equals(
                             Path.GetFullPath(item.CallbackAssemblyPath),
                             Path.GetFullPath(assemblyPath),
                             StringComparison.OrdinalIgnoreCase)
                         : includeUnscoped))
        {
            if (!PcCompatReversePatchBridge.TryFindHandler(
                    patch.TargetType,
                    patch.TargetMethod,
                    out var handler) || handler is null)
            {
                continue;
            }

            var spec = new ManagedBridgeRewriteSpec(
                patch.TargetType,
                patch.TargetMethod,
                patch.ArgumentTypeNames.ToArray(),
                bridgeType,
                handler.AndroidBridgeMethod);
            var key = spec.SourceType + "::" + spec.SourceMethod + "(" +
                      string.Join(",", spec.SourceParameterTypes) + ")";
            if (!rewrites.TryAdd(key, spec))
            {
                var existing = rewrites[key];
                if (!string.Equals(existing.BridgeType, spec.BridgeType, StringComparison.Ordinal) ||
                    !string.Equals(existing.BridgeMethod, spec.BridgeMethod, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Conflicting managed reverse-patch mapping: {key}");
                }
            }
        }

        using (var module = ModuleDefMD.Load(assemblyPath))
        {
            var inputBridgeType = typeof(PcCompatLegacyInputBridge).FullName
                ?? throw new InvalidOperationException("PcCompat legacy input bridge type has no full name.");
            foreach (var type in module.GetTypes())
            foreach (var method in type.Methods)
            {
                var import = method.ImplMap;
                var importModule = import?.Module?.Name?.String;
                var isUser32 =
                    string.Equals(importModule, "user32.dll", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(importModule, "user32", StringComparison.OrdinalIgnoreCase);
                if (!method.IsPinvokeImpl || import is null || !isUser32 ||
                    !string.Equals(import.Name?.String, "GetAsyncKeyState", StringComparison.Ordinal) ||
                    method.MethodSig is not { } signature ||
                    signature.RetType.FullName != "System.Int16" ||
                    signature.Params.Count != 1 ||
                    signature.Params[0].FullName != "System.Int32")
                {
                    continue;
                }

                var spec = new ManagedBridgeRewriteSpec(
                    type.FullName,
                    method.Name.String,
                    ["System.Int32"],
                    inputBridgeType,
                    nameof(PcCompatLegacyInputBridge.GetAsyncKeyStateOwned),
                    modId,
                    AppendCallsiteToken: true);
                var key = spec.SourceType + "::" + spec.SourceMethod + "(" +
                          string.Join(",", spec.SourceParameterTypes) + ")";
                rewrites[key] = spec;
            }
        }

        return rewrites.Values
            .OrderBy(item => item.SourceType, StringComparer.Ordinal)
            .ThenBy(item => item.SourceMethod, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ManagedCallBridgeRewriteSpec> BuildManagedCallBridgeRewrites(
        string modId)
    {
        var resourceBridgeType = typeof(PcCompatManagedResourceBridge).FullName
            ?? throw new InvalidOperationException("PcCompat resource bridge type has no full name.");
        var componentBridgeType = typeof(PcCompatManagedComponentBridge).FullName
            ?? throw new InvalidOperationException("PcCompat managed component bridge type has no full name.");
        var pathBridgeType = typeof(PcCompatManagedPathBridge).FullName
            ?? throw new InvalidOperationException("PcCompat managed path bridge type has no full name.");
        var networkBridgeType = typeof(PcCompatManagedNetworkBridge).FullName
            ?? throw new InvalidOperationException("PcCompat managed network bridge type has no full name.");
        var subscriptionBridgeType = typeof(PcCompatManagedEventSubscriptionBridge).FullName
            ?? throw new InvalidOperationException("PcCompat managed event subscription bridge type has no full name.");
        var logBridgeType = typeof(PcCompatManagedLogBridge).FullName
            ?? throw new InvalidOperationException("PcCompat managed log bridge type has no full name.");
        var inputBridgeType = typeof(PcCompatLegacyInputBridge).FullName
            ?? throw new InvalidOperationException("PcCompat legacy input bridge type has no full name.");
        var applicationBridgeType = typeof(PcCompatManagedApplicationBridge).FullName
            ?? throw new InvalidOperationException("PcCompat managed application bridge type has no full name.");
        var rewiredBridgeType = typeof(PcCompatRewiredInputBridge).FullName
            ?? throw new InvalidOperationException("PcCompat Rewired input bridge type has no full name.");
        var imGuiBridgeType = typeof(PcCompatManagedImGuiBridge).FullName
            ?? throw new InvalidOperationException("PcCompat managed IMGUI bridge type has no full name.");
        var threadBridgeType = typeof(PcCompatManagedThreadBridge).FullName
            ?? throw new InvalidOperationException("PcCompat managed thread bridge type has no full name.");
        var reversePatchBridgeType = typeof(PcCompatReversePatchBridge).FullName
            ?? throw new InvalidOperationException("PcCompat reverse-patch bridge type has no full name.");
        var jsonBridgeType = typeof(PcCompatJsonBridge).FullName
            ?? throw new InvalidOperationException("PcCompat JSON bridge type has no full name.");
        var dynamicGetterBridgeType = typeof(PcCompatManagedDynamicGetterBridge).FullName
            ?? throw new InvalidOperationException("PcCompat dynamic getter bridge type has no full name.");
        var fontBridgeType = typeof(PcCompatManagedFontBridge).FullName
            ?? throw new InvalidOperationException("PcCompat managed font bridge type has no full name.");
        return
        [
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Object",
                "op_Equality",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Boolean",
                ["UnityEngine.Object", "UnityEngine.Object"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.ObjectEquals),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Object",
                "op_Inequality",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Boolean",
                ["UnityEngine.Object", "UnityEngine.Object"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.ObjectNotEquals),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Object",
                "op_Implicit",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Boolean",
                ["UnityEngine.Object"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.ObjectImplicit),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "JipperOverlayer",
                "JipperOverlayer.PatchManager",
                "CreateStaticFieldGetter",
                SourceIsStatic: true,
                SourceGenericArity: 1,
                "System.Func`1<!!0>",
                ["System.Type", "System.String"],
                dynamicGetterBridgeType,
                nameof(PcCompatManagedDynamicGetterBridge.CreateStaticFieldGetter),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "JipperOverlayer",
                "JipperOverlayer.PatchManager",
                "CreateStaticPropertyGetter",
                SourceIsStatic: true,
                SourceGenericArity: 1,
                "System.Func`1<!!0>",
                ["System.Type", "System.String"],
                dynamicGetterBridgeType,
                nameof(PcCompatManagedDynamicGetterBridge.CreateStaticPropertyGetter),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "JipperOverlayer",
                "JipperOverlayer.PatchManager",
                "CreateMemberGetter",
                SourceIsStatic: true,
                SourceGenericArity: 1,
                "System.Func`2<!!0,System.Object>",
                ["System.String"],
                dynamicGetterBridgeType,
                nameof(PcCompatManagedDynamicGetterBridge.CreateMemberGetter),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "JipperOverlayer",
                "JipperOverlayer.PatchManager",
                "CreateMemberGetter",
                SourceIsStatic: true,
                SourceGenericArity: 2,
                "System.Func`2<!!0,!!1>",
                ["System.String"],
                dynamicGetterBridgeType,
                nameof(PcCompatManagedDynamicGetterBridge.CreateMemberGetter),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "JipperOverlayer",
                "JipperOverlayer.PatchManager",
                "CreateStaticMemberGetter",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Func`1<System.Object>",
                ["System.Type", "System.String"],
                dynamicGetterBridgeType,
                nameof(PcCompatManagedDynamicGetterBridge.CreateStaticMemberGetter),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "Unity.TextMeshPro",
                "TMPro.TMP_FontAsset",
                "CreateFontAsset",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "TMPro.TMP_FontAsset",
                ["UnityEngine.Font"],
                fontBridgeType,
                nameof(PcCompatManagedFontBridge.CreateFontAsset),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: true,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "Unity.TextMeshPro",
                "TMPro.TMP_Text",
                "set_font",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Void",
                ["TMPro.TMP_FontAsset"],
                fontBridgeType,
                nameof(PcCompatManagedFontBridge.SetFont),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "Unity.TextMeshPro",
                "TMPro.TMP_Text",
                "set_fontMaterial",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.Material"],
                fontBridgeType,
                nameof(PcCompatManagedFontBridge.SetFontMaterial),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "Unity.TextMeshPro",
                "TMPro.TMP_Text",
                "set_fontSharedMaterial",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.Material"],
                fontBridgeType,
                nameof(PcCompatManagedFontBridge.SetFontSharedMaterial),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.Threading.Thread",
                ".ctor",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Threading.Thread",
                ["System.Threading.ThreadStart"],
                threadBridgeType,
                nameof(PcCompatManagedThreadBridge.Create),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true,
                SourceIsConstructor: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.Threading.Thread",
                "Abort",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Void",
                [],
                threadBridgeType,
                nameof(PcCompatManagedThreadBridge.Abort),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.Threading.Tasks.Task",
                "Run",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Threading.Tasks.Task",
                ["System.Action"],
                threadBridgeType,
                nameof(PcCompatManagedThreadBridge.Run),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUIStyle",
                "set_fontSize",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Void",
                ["System.Int32"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.SetFontSize),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUIStyle",
                "set_fixedWidth",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Void",
                ["System.Single"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.SetFixedWidth),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUIStyle",
                "set_normal",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.GUIStyleState"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.SetNormal),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUIStyle",
                "set_margin",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.RectOffset"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.SetMargin),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true),
            // Responsive IMGUI captures the complete confirmed GUILayout row surface.
            // Every intercepted call receives a stable IL call-site token so Layout,
            // input and Repaint can share one frozen plan.
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "BeginHorizontal",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.BeginHorizontal),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "EndHorizontal",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                [],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.EndHorizontal),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "BeginVertical",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.BeginVertical),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "BeginVertical",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.BeginVerticalNamed),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "BeginVertical",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.GUIContent", "UnityEngine.GUIStyle", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.BeginVerticalWithContentStyle),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "EndVertical",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                [],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.EndVertical),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "Space",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.Single"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.Space),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "FlexibleSpace",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                [],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.FlexibleSpace),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "Width",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "UnityEngine.GUILayoutOption",
                ["System.Single"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.Width),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "MinWidth",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "UnityEngine.GUILayoutOption",
                ["System.Single"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.MinWidth),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "MaxWidth",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "UnityEngine.GUILayoutOption",
                ["System.Single"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.MaxWidth),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "Height",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "UnityEngine.GUILayoutOption",
                ["System.Single"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.Height),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "MinHeight",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "UnityEngine.GUILayoutOption",
                ["System.Single"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.MinHeight),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "MaxHeight",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "UnityEngine.GUILayoutOption",
                ["System.Single"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.MaxHeight),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "ExpandWidth",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "UnityEngine.GUILayoutOption",
                ["System.Boolean"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.ExpandWidth),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "ExpandHeight",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "UnityEngine.GUILayoutOption",
                ["System.Boolean"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.ExpandHeight),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "Button",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Boolean",
                ["System.String", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.ButtonText),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "Button",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Boolean",
                ["System.String", "UnityEngine.GUIStyle", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.ButtonTextWithStyle),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "Toggle",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Boolean",
                ["System.Boolean", "System.String", "UnityEngine.GUIStyle", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.ToggleTextWithStyle),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "Toggle",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Boolean",
                ["System.Boolean", "System.String", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.ToggleText),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "Toggle",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Boolean",
                ["System.Boolean", "UnityEngine.GUIContent", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.ToggleContent),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "Button",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Boolean",
                ["UnityEngine.Texture", "UnityEngine.GUIStyle", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.ButtonTextureWithStyle),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "TextArea",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.String",
                ["System.String", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.TextArea),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUI",
                "SetNextControlName",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.SetNextControlName),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUI",
                "GetNameOfFocusedControl",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.String",
                [],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.GetNameOfFocusedControl),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUI",
                "DragWindow",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.Rect"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.DragWindow),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                BoxLastValueTypeArgument: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUI",
                "DrawTexture",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.Rect", "UnityEngine.Texture"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.DrawTexture),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                BridgeGenericArgumentsFromSourceParameters: [0]),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "Label",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.LabelText),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "Label",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String", "UnityEngine.GUIStyle", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.LabelTextWithStyle),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "Label",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.GUIContent", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.LabelContent),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "SelectionGrid",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Int32",
                ["System.Int32", "System.String[]", "System.Int32", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.SelectionGrid),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "HorizontalSlider",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Single",
                ["System.Single", "System.Single", "System.Single", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.HorizontalSlider),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "TextField",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.String",
                ["System.String", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.TextField),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayout",
                "BeginVertical",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.GUIStyle", "UnityEngine.GUILayoutOption[]"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.BeginVerticalWithStyle),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AppendCallsiteToken: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.IMGUIModule",
                "UnityEngine.GUILayoutUtility",
                "GetRect",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "UnityEngine.Rect",
                ["System.Single", "System.Single"],
                imGuiBridgeType,
                nameof(PcCompatManagedImGuiBridge.GetRect),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowValueTypeReturnUnbox: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.InputLegacyModule",
                "UnityEngine.Input",
                "GetKey",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Boolean",
                ["UnityEngine.KeyCode"],
                inputBridgeType,
                nameof(PcCompatLegacyInputBridge.GetKeyOwned),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                BridgeGenericArgumentsFromSourceParameters: [0],
                AppendCallsiteToken: true,
                AppendOwnerId: modId),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Application",
                "get_isFocused",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Boolean",
                [],
                applicationBridgeType,
                nameof(PcCompatManagedApplicationBridge.GetIsFocused),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false),
            new ManagedCallBridgeRewriteSpec(
                "Rewired_Core",
                "Rewired.Player",
                "GetButton",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Boolean",
                ["System.Int32"],
                rewiredBridgeType,
                nameof(PcCompatRewiredInputBridge.GetButtonOwned),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                AppendCallsiteToken: true,
                AppendOwnerId: modId,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "Rewired_Core",
                "Rewired.Player",
                "GetButtonDown",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Boolean",
                ["System.Int32"],
                rewiredBridgeType,
                nameof(PcCompatRewiredInputBridge.GetButtonDownOwned),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                AppendCallsiteToken: true,
                AppendOwnerId: modId,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "Rewired_Core",
                "Rewired.Player",
                "GetButtonUp",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Boolean",
                ["System.Int32"],
                rewiredBridgeType,
                nameof(PcCompatRewiredInputBridge.GetButtonUpOwned),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                AppendCallsiteToken: true,
                AppendOwnerId: modId,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.InputLegacyModule",
                "UnityEngine.Input",
                "GetKeyDown",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Boolean",
                ["UnityEngine.KeyCode"],
                inputBridgeType,
                nameof(PcCompatLegacyInputBridge.GetKeyDownOwned),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                BridgeGenericArgumentsFromSourceParameters: [0],
                AppendCallsiteToken: true,
                AppendOwnerId: modId),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.InputLegacyModule",
                "UnityEngine.Input",
                "GetKeyUp",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Boolean",
                ["UnityEngine.KeyCode"],
                inputBridgeType,
                nameof(PcCompatLegacyInputBridge.GetKeyUpOwned),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                BridgeGenericArgumentsFromSourceParameters: [0],
                AppendCallsiteToken: true,
                AppendOwnerId: modId),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.InputLegacyModule",
                "UnityEngine.Input",
                "get_anyKeyDown",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Boolean",
                [],
                inputBridgeType,
                nameof(PcCompatLegacyInputBridge.GetAnyKeyDownOwned),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AppendCallsiteToken: true,
                AppendOwnerId: modId),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Debug",
                "LogException",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.Exception"],
                logBridgeType,
                nameof(PcCompatManagedLogBridge.LogException),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false),
            // Debug.Log/LogWarning/LogError(System.Object). The proxy wants an Il2CppSystem.Object,
            // and producing one would mean handing an arbitrary CoreCLR object to the IL2CPP domain
            // and owning its lifetime there just so Unity can call ToString() on it. The bridge does
            // the ToString() on the managed side and writes to the host log, which is also the only
            // log a user can read on Android. JipperOverlayer has 16 of these callsites.
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Debug",
                "Log",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.Object"],
                logBridgeType,
                nameof(PcCompatManagedLogBridge.Log),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Debug",
                "LogWarning",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.Object"],
                logBridgeType,
                nameof(PcCompatManagedLogBridge.LogWarning),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Debug",
                "LogError",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.Object"],
                logBridgeType,
                nameof(PcCompatManagedLogBridge.LogError),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false),
            // JsonUtility. The MOD serializes its own CoreCLR types (ProfileData, KeyViewerSettings,
            // SettingsMeta), which have no entry in the IL2CPP class table, so the IL2CPP
            // JsonUtility - which reflects over what it is handed - can only fail or return {}.
            // Serializing on the managed side is the only option; see PcCompatUnityJson for the
            // format rules being matched.
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.JSONSerializeModule",
                "UnityEngine.JsonUtility",
                "ToJson",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.String",
                ["System.Object", "System.Boolean"],
                jsonBridgeType,
                nameof(PcCompatJsonBridge.ToJson),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.JSONSerializeModule",
                "UnityEngine.JsonUtility",
                "FromJsonOverwrite",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String", "System.Object"],
                jsonBridgeType,
                nameof(PcCompatJsonBridge.FromJsonOverwrite),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false),
            // Registered even though the rewriter reports this callsite as clean: the proxy's
            // generic signature does match, but a signature match says nothing about whether T
            // exists on the IL2CPP side, and KeyViewerSettings does not. Leaving it forwarded would
            // be a silent runtime failure that no audit number shows.
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.JSONSerializeModule",
                "UnityEngine.JsonUtility",
                "FromJson",
                SourceIsStatic: true,
                SourceGenericArity: 1,
                "!!0",
                ["System.String"],
                jsonBridgeType,
                nameof(PcCompatJsonBridge.FromJson),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Behaviour",
                "get_enabled",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Boolean",
                [],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetEnabled),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Behaviour",
                "set_enabled",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Void",
                ["System.Boolean"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.SetEnabled),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false),
            // anchoredPosition on a game-owned rect is shared state with a save/restore protocol:
            // JipperOverlayer samples the beta watermark's position into BetaWatermarkOriginalPos
            // and CheryTools samples the same rect into ElementState.AnchoredPosition. Routing both
            // accessors through the contribution registry keeps each MOD's "original" anchored to
            // the game's own value instead of the other MOD's offset. The Vector2 is boxed across
            // the bridge because its proxy struct type only exists at runtime.
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.RectTransform",
                "get_anchoredPosition",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "UnityEngine.Vector2",
                [],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetAnchoredPosition),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                AllowValueTypeReturnUnbox: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.RectTransform",
                "set_anchoredPosition",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.Vector2"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.SetAnchoredPosition),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                BoxLastValueTypeArgument: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.GameObject",
                "AddComponent",
                SourceIsStatic: false,
                SourceGenericArity: 1,
                "!!0",
                [],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.AddComponent),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                GenericArgumentFilter: ManagedCallGenericArgumentFilter.ModOwnedMonoBehaviour),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.GameObject",
                "GetComponent",
                SourceIsStatic: false,
                SourceGenericArity: 1,
                "!!0",
                [],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetComponent),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                GenericArgumentFilter: ManagedCallGenericArgumentFilter.ModOwnedOrProxyComponent),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Component",
                "GetComponent",
                SourceIsStatic: false,
                SourceGenericArity: 1,
                "!!0",
                [],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetComponent),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                GenericArgumentFilter: ManagedCallGenericArgumentFilter.ModOwnedOrProxyComponent),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.GameObject",
                "GetComponents",
                SourceIsStatic: false,
                SourceGenericArity: 1,
                "!!0[]",
                [],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetComponents),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                GenericArgumentFilter: ManagedCallGenericArgumentFilter.ModOwnedOrProxyComponent),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Component",
                "GetComponents",
                SourceIsStatic: false,
                SourceGenericArity: 1,
                "!!0[]",
                [],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetComponents),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                GenericArgumentFilter: ManagedCallGenericArgumentFilter.ModOwnedOrProxyComponent),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.GameObject",
                "TryGetComponent",
                SourceIsStatic: false,
                SourceGenericArity: 1,
                "System.Boolean",
                ["!!0&"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.TryGetComponent),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                GenericArgumentFilter: ManagedCallGenericArgumentFilter.ModOwnedOrProxyComponent),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Component",
                "TryGetComponent",
                SourceIsStatic: false,
                SourceGenericArity: 1,
                "System.Boolean",
                ["!!0&"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.TryGetComponent),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                GenericArgumentFilter: ManagedCallGenericArgumentFilter.ModOwnedOrProxyComponent),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.GameObject",
                "AddComponent",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "UnityEngine.Component",
                ["System.Type"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.AddComponent),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.GameObject",
                "GetComponent",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "UnityEngine.Component",
                ["System.Type"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetComponent),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Component",
                "GetComponent",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "UnityEngine.Component",
                ["System.Type"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetComponent),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.GameObject",
                "GetComponents",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "UnityEngine.Component[]",
                ["System.Type"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetComponents),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.GameObject",
                "TryGetComponent",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Boolean",
                ["System.Type", "UnityEngine.Component&"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.TryGetComponent),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                BridgeGenericArgumentsFromSourceParameters: [1]),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Component",
                "TryGetComponent",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Boolean",
                ["System.Type", "UnityEngine.Component&"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.TryGetComponent),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                BridgeGenericArgumentsFromSourceParameters: [1]),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Component",
                "GetComponents",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "UnityEngine.Component[]",
                ["System.Type"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetComponents),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Component",
                "get_gameObject",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "UnityEngine.GameObject",
                [],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetGameObject),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.GameObject",
                "SetActive",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Void",
                ["System.Boolean"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.SetActive),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Component",
                "get_transform",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "UnityEngine.Transform",
                [],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.GetTransform),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Object",
                "DontDestroyOnLoad",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.Object"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.DontDestroyOnLoad),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Object",
                "Destroy",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.Object"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.Destroy),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true),
            // PcCompat filesystem isolation. GetDirectoryName is owner-sensitive here because
            // ModEntry.Path is a virtual package root, not a physical assembly file path.
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.Path",
                "GetDirectoryName",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.String",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.GetDirectoryName),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.Path",
                "GetFullPath",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.String",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.GetFullPath),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "Exists",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Boolean",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileExists),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "ReadAllText",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.String",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileReadAllText),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "ReadAllBytes",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Byte[]",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileReadAllBytes),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "WriteAllText",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String", "System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileWriteAllText),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "WriteAllBytes",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String", "System.Byte[]"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileWriteAllBytes),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "Delete",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileDelete),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "Copy",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String", "System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileCopy),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "Copy",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String", "System.String", "System.Boolean"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileCopyOverwrite),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "Move",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String", "System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileMove),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "OpenRead",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.IO.FileStream",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileOpenRead),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.File",
                "OpenWrite",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.IO.FileStream",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.FileOpenWrite),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.Directory",
                "Exists",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Boolean",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.DirectoryExists),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.Directory",
                "GetFiles",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.String[]",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.DirectoryGetFiles),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.Directory",
                "GetFiles",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.String[]",
                ["System.String", "System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.DirectoryGetFilesPattern),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.Directory",
                "GetFiles",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.String[]",
                ["System.String", "System.String", "System.IO.SearchOption"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.DirectoryGetFilesSearch),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.Directory",
                "EnumerateFiles",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Collections.Generic.IEnumerable`1<System.String>",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.DirectoryEnumerateFiles),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.Directory",
                "EnumerateFiles",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Collections.Generic.IEnumerable`1<System.String>",
                ["System.String", "System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.DirectoryEnumerateFilesPattern),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.Directory",
                "EnumerateFiles",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Collections.Generic.IEnumerable`1<System.String>",
                ["System.String", "System.String", "System.IO.SearchOption"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.DirectoryEnumerateFilesSearch),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.Directory",
                "CreateDirectory",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.IO.DirectoryInfo",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.DirectoryCreate),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.Directory",
                "Delete",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.DirectoryDelete),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.Directory",
                "Delete",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String", "System.Boolean"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.DirectoryDeleteRecursive),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.FileStream",
                ".ctor",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.IO.FileStream",
                ["System.String", "System.IO.FileMode"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.OpenFileStream),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true,
                SourceIsConstructor: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.FileStream",
                ".ctor",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.IO.FileStream",
                ["System.String", "System.IO.FileMode", "System.IO.FileAccess"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.OpenFileStreamAccess),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true,
                SourceIsConstructor: true),
            new ManagedCallBridgeRewriteSpec(
                "mscorlib",
                "System.IO.FileStream",
                ".ctor",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.IO.FileStream",
                ["System.String", "System.IO.FileMode", "System.IO.FileAccess", "System.IO.FileShare"],
                pathBridgeType,
                nameof(PcCompatManagedPathBridge.OpenFileStreamShare),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true,
                SourceIsConstructor: true),
            // MOD network sessions: only client-producing constructions are rewritten.
            // Operations on a bound client and its response objects inherit this session's
            // identity already, so they stay as-is (same reasoning as Path.Combine above).
            // ServicePointManager, WebRequest/WebClient and raw sockets have no spec and are
            // not bridged; a MOD using them is an isolation downgrade, not a supported path.
            new ManagedCallBridgeRewriteSpec(
                "System.Net.Http",
                "System.Net.Http.HttpClient",
                ".ctor",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Net.Http.HttpClient",
                [],
                networkBridgeType,
                nameof(PcCompatManagedNetworkBridge.CreateHttpClient),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true,
                SourceIsConstructor: true),
            new ManagedCallBridgeRewriteSpec(
                "System.Net.Http",
                "System.Net.Http.HttpClient",
                ".ctor",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Net.Http.HttpClient",
                ["System.Net.Http.HttpMessageHandler"],
                networkBridgeType,
                nameof(PcCompatManagedNetworkBridge.CreateHttpClientWithHandler),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true,
                SourceIsConstructor: true),
            new ManagedCallBridgeRewriteSpec(
                "System.Net.Http",
                "System.Net.Http.HttpClient",
                ".ctor",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Net.Http.HttpClient",
                ["System.Net.Http.HttpMessageHandler", "System.Boolean"],
                networkBridgeType,
                nameof(PcCompatManagedNetworkBridge.CreateHttpClientWithHandlerDisposal),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true,
                SourceIsConstructor: true),
            new ManagedCallBridgeRewriteSpec(
                "System.Net.Http",
                "System.Net.Http.HttpClientHandler",
                ".ctor",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Net.Http.HttpClientHandler",
                [],
                networkBridgeType,
                nameof(PcCompatManagedNetworkBridge.CreateHttpClientHandler),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true,
                SourceIsConstructor: true),
            // CookieContainer's declaring assembly differs across target frameworks
            // (System.Net.Primitives on netstandard, System on desktop); a single spelling
            // would zero-match silently on the other, so both are registered.
            new ManagedCallBridgeRewriteSpec(
                "System.Net.Primitives",
                "System.Net.CookieContainer",
                ".ctor",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Net.CookieContainer",
                [],
                networkBridgeType,
                nameof(PcCompatManagedNetworkBridge.CreateCookieContainer),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true,
                SourceIsConstructor: true),
            new ManagedCallBridgeRewriteSpec(
                "System",
                "System.Net.CookieContainer",
                ".ctor",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Net.CookieContainer",
                [],
                networkBridgeType,
                nameof(PcCompatManagedNetworkBridge.CreateCookieContainer),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowUnproxiedSource: true,
                SourceIsConstructor: true),
            // External static-event subscriptions are routed through the registration bridge
            // in both directions. This is required because generated IL2CPP proxies use
            // Il2CppSystem delegate parameters while the rewritten MOD keeps System delegates;
            // leaving remove_ raw would fail during OnDisable and would also leave stale event
            // registrations behind. AppendOwnerId carries the event identity consumed by the
            // bridge.
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Application",
                "add_quitting",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.Action"],
                subscriptionBridgeType,
                nameof(PcCompatManagedEventSubscriptionBridge.Subscribe),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AllowUnproxiedSource: true,
                AppendOwnerId: "UnityEngine.CoreModule!UnityEngine.Application::quitting"),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Application",
                "remove_quitting",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["System.Action"],
                subscriptionBridgeType,
                nameof(PcCompatManagedEventSubscriptionBridge.Unsubscribe),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AllowUnproxiedSource: true,
                AppendOwnerId: "UnityEngine.CoreModule!UnityEngine.Application::quitting"),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.SceneManagement.SceneManager",
                "add_sceneUnloaded",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.Events.UnityAction`1<UnityEngine.SceneManagement.Scene>"],
                subscriptionBridgeType,
                nameof(PcCompatManagedEventSubscriptionBridge.Subscribe),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AllowUnproxiedSource: true,
                AppendOwnerId: "UnityEngine.CoreModule!UnityEngine.SceneManagement.SceneManager::sceneUnloaded"),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.SceneManagement.SceneManager",
                "remove_sceneUnloaded",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.Events.UnityAction`1<UnityEngine.SceneManagement.Scene>"],
                subscriptionBridgeType,
                nameof(PcCompatManagedEventSubscriptionBridge.Unsubscribe),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AllowUnproxiedSource: true,
                AppendOwnerId: "UnityEngine.CoreModule!UnityEngine.SceneManagement.SceneManager::sceneUnloaded"),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.SceneManagement.SceneManager",
                "add_sceneLoaded",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.Events.UnityAction`2<UnityEngine.SceneManagement.Scene,UnityEngine.SceneManagement.LoadSceneMode>"],
                subscriptionBridgeType,
                nameof(PcCompatManagedEventSubscriptionBridge.Subscribe),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AllowUnproxiedSource: true,
                AppendOwnerId: "UnityEngine.CoreModule!UnityEngine.SceneManagement.SceneManager::sceneLoaded"),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.SceneManagement.SceneManager",
                "remove_sceneLoaded",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.Events.UnityAction`2<UnityEngine.SceneManagement.Scene,UnityEngine.SceneManagement.LoadSceneMode>"],
                subscriptionBridgeType,
                nameof(PcCompatManagedEventSubscriptionBridge.Unsubscribe),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true,
                AllowUnproxiedSource: true,
                AppendOwnerId: "UnityEngine.CoreModule!UnityEngine.SceneManagement.SceneManager::sceneLoaded"),
            // MOD-created Unity objects: register at creation so the create/destroy loop is
            // closed. Destroy already validates owner leases, but before this the objects a MOD
            // built with `new GameObject(...)` / Instantiate had no lease to validate against.
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.GameObject",
                ".ctor",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "UnityEngine.GameObject",
                ["System.String"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.CreateGameObject),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: true,
                SourceIsConstructor: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Object",
                "Instantiate",
                SourceIsStatic: true,
                SourceGenericArity: 1,
                "!!0",
                ["!!0"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.Instantiate),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: true,
                AllowObjectParameterForwarding: true,
                EraseBridgeGenericArity: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Object",
                "Instantiate",
                SourceIsStatic: true,
                SourceGenericArity: 1,
                "!!0",
                ["!!0", "UnityEngine.Transform"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.Instantiate),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: true,
                AllowObjectParameterForwarding: true,
                EraseBridgeGenericArity: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.MonoBehaviour",
                "StartCoroutine",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "UnityEngine.Coroutine",
                ["System.Collections.IEnumerator"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.StartCoroutine),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                ErasedTypeAssembly: "UnityEngine.CoreModule",
                ErasedType: "UnityEngine.Coroutine"),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.MonoBehaviour",
                "StartCoroutine",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "UnityEngine.Coroutine",
                ["System.String"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.StartCoroutine),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                ErasedTypeAssembly: "UnityEngine.CoreModule",
                ErasedType: "UnityEngine.Coroutine"),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.MonoBehaviour",
                "StartCoroutine",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "UnityEngine.Coroutine",
                ["System.String", "System.Object"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.StartCoroutine),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                ErasedTypeAssembly: "UnityEngine.CoreModule",
                ErasedType: "UnityEngine.Coroutine"),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.MonoBehaviour",
                "StopCoroutine",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Void",
                ["System.Collections.IEnumerator"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.StopCoroutine),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.MonoBehaviour",
                "StopCoroutine",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.Coroutine"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.StopCoroutine),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.MonoBehaviour",
                "StopCoroutine",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Void",
                ["System.String"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.StopCoroutine),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.MonoBehaviour",
                "StopAllCoroutines",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Void",
                [],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.StopAllCoroutines),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.CoreModule",
                "UnityEngine.Object",
                "Destroy",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "System.Void",
                ["UnityEngine.Object", "System.Single"],
                componentBridgeType,
                nameof(PcCompatManagedComponentBridge.Destroy),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                AllowObjectParameterForwarding: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.AssetBundleModule",
                "UnityEngine.AssetBundle",
                "LoadFromFile",
                SourceIsStatic: true,
                SourceGenericArity: 0,
                "UnityEngine.AssetBundle",
                ["System.String"],
                resourceBridgeType,
                nameof(PcCompatManagedResourceBridge.LoadAssetBundleFromFile),
                ManagedCallInstanceForwarding.None,
                AllowObjectReturnCast: false,
                EraseSourceTypeToObject: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.AssetBundleModule",
                "UnityEngine.AssetBundle",
                "LoadAsset",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "UnityEngine.Object",
                ["System.String"],
                resourceBridgeType,
                nameof(PcCompatManagedResourceBridge.LoadAssetFromBundle),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.AssetBundleModule",
                "UnityEngine.AssetBundle",
                "LoadAsset",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "UnityEngine.Object",
                ["System.String", "System.Type"],
                resourceBridgeType,
                nameof(PcCompatManagedResourceBridge.LoadAssetFromBundleWithType),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.AssetBundleModule",
                "UnityEngine.AssetBundle",
                "LoadAsset",
                SourceIsStatic: false,
                SourceGenericArity: 1,
                "!!0",
                ["System.String"],
                resourceBridgeType,
                nameof(PcCompatManagedResourceBridge.LoadAssetFromBundleGeneric),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.AssetBundleModule",
                "UnityEngine.AssetBundle",
                "LoadAllAssets",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "UnityEngine.Object[]",
                [],
                resourceBridgeType,
                nameof(PcCompatManagedResourceBridge.LoadAllAssetsFromBundle),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.AssetBundleModule",
                "UnityEngine.AssetBundle",
                "LoadAllAssets",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "UnityEngine.Object[]",
                ["System.Type"],
                resourceBridgeType,
                nameof(PcCompatManagedResourceBridge.LoadAllAssetsFromBundleWithType),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.AssetBundleModule",
                "UnityEngine.AssetBundle",
                "LoadAllAssets",
                SourceIsStatic: false,
                SourceGenericArity: 1,
                "!!0[]",
                [],
                resourceBridgeType,
                nameof(PcCompatManagedResourceBridge.LoadAllAssetsFromBundleGeneric),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: true),
            new ManagedCallBridgeRewriteSpec(
                "UnityEngine.AssetBundleModule",
                "UnityEngine.AssetBundle",
                "Unload",
                SourceIsStatic: false,
                SourceGenericArity: 0,
                "System.Void",
                ["System.Boolean"],
                resourceBridgeType,
                nameof(PcCompatManagedResourceBridge.ReleaseAssetBundle),
                ManagedCallInstanceForwarding.AsObject,
                AllowObjectReturnCast: false)
        ];
    }

    private static IReadOnlyList<ManagedFieldConstantRewriteSpec>
        BuildManagedFieldConstantRewrites()
        =>
        [
            // PC MOD code commonly gates desktop-only initialization on this field.
            // Rewrite only the MOD's read site; never mutate the game's global field.
            new ManagedFieldConstantRewriteSpec(
                "Assembly-CSharp",
                "ADOBase",
                "platform",
                "Platform",
                3)
        ];

    /// <summary>
    /// Proxy collection properties the MOD mutates in place, so the getter must return a copy bound
    /// to the Il2Cpp original rather than a detached one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only <c>TMP_FontAsset.fallbackFontAssetTable</c> is registered, and only because all three
    /// audited MODs write to it: JipperResourcePack (<c>BundleLoader.cs:42</c>), JipperOverlayer
    /// (<c>BundleLoader.cs:70</c>, <c>FontManager.cs:79</c>) and JipperKeyViewer
    /// (<c>KeyViewerResources.cs:278</c>) all call <c>.Add</c> on the value the getter returned. With
    /// the plain copying converter that <c>.Add</c> reaches nothing, so the CJK fallback font silently
    /// never applies - not an error, just missing glyphs.
    /// </para>
    /// <para>
    /// The other three <c>List</c>-returning proxy members stay on the copying converter:
    /// <c>scrLevelMaker::listFloors</c> and <c>scnGame::get_events</c> are read-only at every audited
    /// callsite, and <c>PlanetarySystem::allPlanets</c> is reached by reflection so it has no callsite
    /// at all. Registering a property is therefore an explicit statement that MODs mutate it, not a
    /// blanket upgrade.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<ManagedWritableCollectionSpec>
        BuildManagedWritableCollections()
        =>
        [
            new ManagedWritableCollectionSpec(
                "Unity.TextMeshPro",
                "TMPro.TMP_FontAsset",
                "fallbackFontAssetTable",
                "TMPro.TMP_FontAsset",
                typeof(PcCompatCollectionBridge).FullName
                ?? throw new InvalidOperationException(
                    "PcCompat collection bridge type has no full name."),
                nameof(PcCompatCollectionBridge.CopyOrCreateBoundList),
                nameof(PcCompatCollectionBridge.AddToBoundList),
                nameof(PcCompatCollectionBridge.RemoveFromBoundList),
                nameof(PcCompatCollectionBridge.ClearBoundList),
                nameof(PcCompatCollectionBridge.InsertIntoBoundList))
        ];

    /// <summary>
    /// Projects the shared render-component catalog into rewriter specs.
    /// </summary>
    /// <remarks>
    /// The list is not restated here: <see cref="PcCompatManagedRenderComponentCatalog"/> is the one
    /// source the rewriter, the recipe compiler and the component bridge all read, so they cannot
    /// disagree about which types are permitted to derive a proxy class. The component assembly is
    /// the MOD id because both target MODs ship their primary assembly under that name; a MOD whose
    /// assembly name diverges from its id would need the catalog to carry the assembly separately.
    /// </remarks>
    private static IReadOnlyList<ManagedRenderComponentSpec> BuildManagedRenderComponents(
        PcCompatStaticPatchScanReport staticScan)
        => staticScan.ManagedRenderComponents
            .Select(descriptor => new ManagedRenderComponentSpec(
                descriptor.ComponentAssembly,
                descriptor.ComponentType,
                descriptor.BaseAssembly,
                descriptor.BaseType))
            .ToArray();

    private static string HashFile(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string DescribeFirstRewriteIssue(RewriteReport report)
    {
        if (report.MethodIssues.FirstOrDefault() is { } methodIssue)
        {
            return $" firstMethodIssue={methodIssue.Method}@IL_{methodIssue.IlOffset:X4}: " +
                   $"{methodIssue.Reason} ({methodIssue.Target})";
        }

        if (report.ManagedBridgeIssues.FirstOrDefault() is { } bridgeIssue)
        {
            return $" firstBridgeIssue={bridgeIssue.SourceType}.{bridgeIssue.SourceMethod}: " +
                   bridgeIssue.Reason;
        }

        return report.Issues.FirstOrDefault() is { } issue
            ? " firstIssue=" + issue.Replace('\r', ' ').Replace('\n', ' ')
            : string.Empty;
    }

    private static string ResolveInputAssembly(PcModManifest manifest)
        => manifest.Kind == PcModKind.JAMod && !string.IsNullOrWhiteSpace(manifest.JAModAssemblyFullPath)
            ? Path.GetFullPath(manifest.JAModAssemblyFullPath)
            : Path.GetFullPath(manifest.EntryAssemblyPath);

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Android.PcCompat;

internal static class PcCompatAndroidManagedAssemblyRewrite
{
    private const string CacheFormatVersion = "xphorror.pcmod-managed-cache.v30-ddol-owner-teardown";
    private const string CollectionBridgeAbi =
        "PcCompatCollectionBridge.v1+PcCompatAbiBridge.v1+PcCompatReversePatchBridge.v1+" +
        "PcCompatManagedResourceBridge.v2+PcCompatManagedComponentBridge.v6+" +
        "PcCompatManagedLogBridge.v1+PcCompatProxyCastBridge.v1+PcCompatManagedIoBridge.v2+" +
        "PcCompatManagedPollingBridge.v1+PcCompatManagedImGuiBridge.v4+" +
        "PcCompatManagedSettingsDelegateBridge.v1+PcCompatManagedThreadBridge.v1+" +
        "PcCompatLegacyInputBridge.v1+VirtualBundle.v1";
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
        var proxyCastBridge = new ManagedProxyCastBridgeSpec(
            typeof(PcCompatProxyCastBridge).FullName
            ?? throw new InvalidOperationException("PcCompat proxy cast bridge type has no full name."),
            nameof(PcCompatProxyCastBridge.IsInstance),
            nameof(PcCompatProxyCastBridge.Cast));
        var bridgeAssembly = typeof(PcCompatReversePatchBridge).Assembly;
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
                        nameof(PcCompatManagedSettingsDelegateBridge.CreateOptionalAction)));
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
            builder.Append(string.Join(",", spec.BridgeGenericArgumentsFromSourceParameters ?? []));
            builder.Append('|');
            builder.Append(spec.ErasedTypeAssembly ?? "-");
            builder.Append('|');
            builder.Append(spec.ErasedType ?? "-");
            builder.Append('|');
            builder.Append(spec.AppendCallsiteToken ? "callsite-token" : "no-token");
            builder.Append('|');
            builder.AppendLine(spec.AppendOwnerId ?? "-");
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
        var logBridgeType = typeof(PcCompatManagedLogBridge).FullName
            ?? throw new InvalidOperationException("PcCompat managed log bridge type has no full name.");
        var inputBridgeType = typeof(PcCompatLegacyInputBridge).FullName
            ?? throw new InvalidOperationException("PcCompat legacy input bridge type has no full name.");
        var rewiredBridgeType = typeof(PcCompatRewiredInputBridge).FullName
            ?? throw new InvalidOperationException("PcCompat Rewired input bridge type has no full name.");
        var imGuiBridgeType = typeof(PcCompatManagedImGuiBridge).FullName
            ?? throw new InvalidOperationException("PcCompat managed IMGUI bridge type has no full name.");
        var threadBridgeType = typeof(PcCompatManagedThreadBridge).FullName
            ?? throw new InvalidOperationException("PcCompat managed thread bridge type has no full name.");
        return
        [
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
                AllowObjectParameterForwarding: true),
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
                AllowObjectParameterForwarding: true),
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
                AllowObjectParameterForwarding: true),
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
                AllowObjectParameterForwarding: true),
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
                AllowObjectParameterForwarding: true),
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
                GenericArgumentFilter: ManagedCallGenericArgumentFilter.ModOwnedMonoBehaviour),
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
                GenericArgumentFilter: ManagedCallGenericArgumentFilter.ModOwnedMonoBehaviour),
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
                GenericArgumentFilter: ManagedCallGenericArgumentFilter.ModOwnedMonoBehaviour),
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
                GenericArgumentFilter: ManagedCallGenericArgumentFilter.ModOwnedMonoBehaviour),
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
                GenericArgumentFilter: ManagedCallGenericArgumentFilter.ModOwnedMonoBehaviour),
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
                GenericArgumentFilter: ManagedCallGenericArgumentFilter.ModOwnedMonoBehaviour),
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

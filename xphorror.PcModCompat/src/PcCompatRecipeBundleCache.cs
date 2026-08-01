using System.Security.Cryptography;
using System.Text;

namespace Xphorror.PcModCompat;

public sealed class PcCompatRecipeBundleInfo
{
    public required string ModId { get; init; }
    public required string CacheKey { get; init; }
    public required string BundleDirectory { get; init; }
    public required string ReportPath { get; init; }
    public required string RulesPath { get; init; }
    public required string RecipePath { get; init; }
    public string? ResourceRecipePath { get; init; }
    public string? ResourceIrPath { get; init; }
    public string? ResourceIrPayloadDirectory { get; init; }
    public string? ResourceReportPath { get; init; }
    public string? ResourceDirectory { get; init; }
    public required string CompleteMarkerPath { get; init; }
}

public static class PcCompatRecipeBundleCache
{
    private const string CompleteMarker = "complete.marker";
    private const string FormatVersion = "mvp-recipe-cache-v11-indirect-struct-abi";
    private static readonly object WriteGate = new();
    private static long s_tempSequence;

    public static PcCompatRecipeBundleInfo Write(PcModManifest manifest, PcCompatRecipeCompileReport report)
    {
        lock (WriteGate)
            return WriteCore(manifest, report);
    }

    private static PcCompatRecipeBundleInfo WriteCore(PcModManifest manifest, PcCompatRecipeCompileReport report)
    {
        var sourceResourceRecipe = Path.Combine(manifest.FolderPath, ".pccompat", "resource_recipe.bin");
        var sourceResourceIr = Path.Combine(manifest.FolderPath, ".pccompat", "resource_ir.bin");
        var sourceResourceIrPayloadDirectory = Path.Combine(
            manifest.FolderPath,
            ".pccompat",
            "resource_ir_blobs");
        var sourceResourceReport = Path.Combine(manifest.FolderPath, ".pccompat", "resource_report.json");
        var resourceRecipeFileExists = File.Exists(sourceResourceRecipe);
        var hasResourceReport = File.Exists(sourceResourceReport);
        var resourceRecipeBytes = resourceRecipeFileExists &&
                                  new FileInfo(sourceResourceRecipe).Length is >= PcCompatResourceRecipe.HeaderSize and
                                      <= PcCompatResourceRecipe.MaxFileSize
            ? File.ReadAllBytes(sourceResourceRecipe)
            : null;
        var hasResourceRecipe = resourceRecipeBytes != null;
        var resourceRecipeSha = resourceRecipeBytes != null
            ? Convert.ToHexString(SHA256.HashData(resourceRecipeBytes)).ToLowerInvariant()
            : string.Empty;
        var resourceIrBytes = File.Exists(sourceResourceIr) &&
                              new FileInfo(sourceResourceIr).Length is >= PcCompatResourceIr.HeaderSize and
                                  <= PcCompatResourceIr.MaxFileSize
            ? File.ReadAllBytes(sourceResourceIr)
            : null;
        var hasResourceIr = resourceIrBytes != null;
        var resourceIrSha = resourceIrBytes != null
            ? Convert.ToHexString(SHA256.HashData(resourceIrBytes)).ToLowerInvariant()
            : string.Empty;

        var cacheKey = ComputeCacheKey(manifest, report, resourceRecipeSha, resourceIrSha);
        var bundleRoot = GetBundleRoot(manifest, report.ModId);
        var finalDir = Path.Combine(bundleRoot, cacheKey);
        var reportPath = Path.Combine(finalDir, "recipe_report.json");
        var rulesPath = Path.Combine(finalDir, "hook_rules.json");
        var recipePath = Path.Combine(finalDir, "ui_recipe.bin");
        var resourceRecipePath = Path.Combine(finalDir, "resource_recipe.bin");
        var resourceIrPath = Path.Combine(finalDir, "resource_ir.bin");
        var resourceIrPayloadDirectory = Path.Combine(finalDir, "resource_ir_blobs");
        var resourceReportPath = Path.Combine(finalDir, "resource_report.json");
        var resourceDirectory = Path.Combine(finalDir, "resources");
        var completePath = Path.Combine(finalDir, CompleteMarker);

        if (File.Exists(completePath) &&
            File.Exists(reportPath) &&
            File.Exists(recipePath) &&
            File.Exists(rulesPath) &&
            (!hasResourceRecipe || File.Exists(resourceRecipePath)) &&
            (!hasResourceIr || File.Exists(resourceIrPath)) &&
            PcCompatUiRecipeBinary.TryValidate(recipePath, out _) &&
            IsRecipeReportValid(reportPath, report) &&
            IsRuntimeRuleBundleValid(rulesPath, report) &&
            IsResourceCacheValid(
                manifest,
                hasResourceRecipe,
                resourceRecipeSha,
                resourceRecipePath,
                hasResourceIr,
                resourceIrSha,
                resourceIrPath,
                resourceDirectory))
        {
            return new PcCompatRecipeBundleInfo
            {
                ModId = report.ModId,
                CacheKey = cacheKey,
                BundleDirectory = finalDir,
                ReportPath = reportPath,
                RulesPath = rulesPath,
                RecipePath = recipePath,
                ResourceRecipePath = File.Exists(resourceRecipePath) ? resourceRecipePath : null,
                ResourceIrPath = File.Exists(resourceIrPath) ? resourceIrPath : null,
                ResourceIrPayloadDirectory = Directory.Exists(resourceIrPayloadDirectory)
                    ? resourceIrPayloadDirectory
                    : null,
                ResourceReportPath = File.Exists(resourceReportPath) ? resourceReportPath : null,
                ResourceDirectory = Directory.Exists(resourceDirectory) ? resourceDirectory : null,
                CompleteMarkerPath = completePath
            };
        }

        Directory.CreateDirectory(bundleRoot);
        var tempSuffix = $"{Environment.ProcessId:x}-{Environment.CurrentManagedThreadId:x}-{Environment.TickCount64:x}-{Interlocked.Increment(ref s_tempSequence):x}";
        var tempDir = Path.Combine(bundleRoot, $".tmp-{cacheKey}-{tempSuffix}");

        try
        {
            Directory.CreateDirectory(tempDir);

            var reportJson = PcCompatRecipeReportJson.Serialize(report);
            var runtimeRulesJson = PcCompatRuntimeRuleBundle.Serialize(PcCompatRuntimeRuleBundle.FromReport(report));
            File.WriteAllText(Path.Combine(tempDir, "recipe_report.json"), reportJson);
            File.WriteAllText(Path.Combine(tempDir, "hook_rules.json"), runtimeRulesJson);
            var recipePathInTemp = Path.Combine(tempDir, "ui_recipe.bin");
            PcCompatUiRecipeBinary.Write(
                recipePathInTemp,
                manifest,
                report,
                GetTargetGameRevision());
            if (!PcCompatUiRecipeBinary.TryValidate(recipePathInTemp, out var recipeError))
                throw new InvalidDataException($"Generated ui_recipe.bin failed validation: {recipeError}");

            // Resource recipes are produced by the isolated Resources assembly/tooling and
            // only copied here. The Android runtime must not link AssetsTools.NET.
            var tempResourceRecipe = Path.Combine(tempDir, "resource_recipe.bin");
            if (resourceRecipeBytes != null)
                File.WriteAllBytes(tempResourceRecipe, resourceRecipeBytes);
            if (resourceIrBytes != null)
            {
                File.WriteAllBytes(Path.Combine(tempDir, "resource_ir.bin"), resourceIrBytes);
                CopyDirectoryContents(
                    sourceResourceIrPayloadDirectory,
                    Path.Combine(tempDir, "resource_ir_blobs"));
            }
            if (hasResourceReport)
                File.Copy(sourceResourceReport, Path.Combine(tempDir, "resource_report.json"), overwrite: true);
            if (resourceRecipeBytes != null)
                CopyLoadableResourceCandidates(manifest, tempResourceRecipe, Path.Combine(tempDir, "resources"));

            File.WriteAllText(Path.Combine(tempDir, "cache_key.txt"), cacheKey);
            File.WriteAllText(Path.Combine(tempDir, "format_version.txt"), FormatVersion);
            File.WriteAllText(Path.Combine(tempDir, CompleteMarker), DateTimeOffset.UtcNow.ToString("O"));

            if (Directory.Exists(finalDir))
                Directory.Delete(finalDir, recursive: true);

            Directory.Move(tempDir, finalDir);
        }
        catch
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
            throw;
        }

        return new PcCompatRecipeBundleInfo
        {
            ModId = report.ModId,
            CacheKey = cacheKey,
            BundleDirectory = finalDir,
            ReportPath = reportPath,
            RulesPath = rulesPath,
            RecipePath = recipePath,
            ResourceRecipePath = File.Exists(resourceRecipePath) ? resourceRecipePath : null,
            ResourceIrPath = File.Exists(resourceIrPath) ? resourceIrPath : null,
            ResourceIrPayloadDirectory = Directory.Exists(resourceIrPayloadDirectory)
                ? resourceIrPayloadDirectory
                : null,
            ResourceReportPath = File.Exists(resourceReportPath) ? resourceReportPath : null,
            ResourceDirectory = Directory.Exists(resourceDirectory) ? resourceDirectory : null,
            CompleteMarkerPath = completePath
        };
    }

    private static void CopyLoadableResourceCandidates(
        PcModManifest manifest,
        string resourceRecipePath,
        string resourcesDir)
    {
        if (!PcCompatResourceRecipe.TryRead(resourceRecipePath, out var document, out var recipeError) ||
            !PcCompatResourceRecipe.TryValidateDocument(document, manifest.Id, out recipeError))
        {
            Directory.CreateDirectory(resourcesDir);
            File.WriteAllText(
                Path.Combine(resourcesDir, "manifest.json"),
                System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        formatVersion = "resource-cache-manifest-v1",
                        modId = manifest.Id,
                        copiedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                        error = recipeError ?? "resource recipe validation failed",
                        candidates = Array.Empty<object>()
                    },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Directory.CreateDirectory(resourcesDir);
        var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<object>();
        foreach (var candidate in document.Candidates)
        {
            var policy = candidate.LoadPolicy ?? string.Empty;
            var cacheable = IsCacheableResourcePolicy(policy);
            if (!cacheable || string.IsNullOrWhiteSpace(candidate.Sha256Hex))
                continue;

            var source = PcCompatResourceRecipeRuntime.ResolveCandidatePathOnDisk(manifest.FolderPath, candidate);
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                entries.Add(new
                {
                    sha256Hex = candidate.Sha256Hex,
                    sourceFileName = candidate.FileName,
                    status = "missing",
                    error = "candidate source was not found under the MOD folder"
                });
                continue;
            }
            if (!copied.Add(candidate.Sha256Hex))
                continue;

            if (!PcCompatResourceRecipe.TryVerifyCandidateFile(
                    source,
                    candidate.Sha256Hex,
                    candidate.FileSize,
                    out var verifyError))
            {
                entries.Add(new
                {
                    sha256Hex = candidate.Sha256Hex,
                    sourceFileName = candidate.FileName,
                    status = "rejected",
                    error = verifyError ?? "candidate verification failed"
                });
                continue;
            }

            var safeName = string.IsNullOrWhiteSpace(candidate.FileName)
                ? Path.GetFileName(source)
                : Path.GetFileName(candidate.FileName);
            foreach (var ch in Path.GetInvalidFileNameChars())
                safeName = safeName.Replace(ch, '_');
            if (string.IsNullOrWhiteSpace(safeName))
                safeName = "candidate.bundle";

            var fileName = candidate.Sha256Hex.ToLowerInvariant() + "_" + safeName;
            var destination = Path.Combine(resourcesDir, fileName);
            File.Copy(source, destination, overwrite: true);
            if (!PcCompatResourceRecipe.TryVerifyCandidateFile(
                    destination,
                    candidate.Sha256Hex,
                    candidate.FileSize,
                    out var copiedError))
            {
                File.Delete(destination);
                entries.Add(new
                {
                    sha256Hex = candidate.Sha256Hex,
                    sourceFileName = candidate.FileName,
                    status = "rejected",
                    error = copiedError ?? "copied candidate verification failed"
                });
                continue;
            }
            entries.Add(new
            {
                sha256Hex = candidate.Sha256Hex,
                fileName,
                platformHint = candidate.PlatformHint,
                unityVersion = candidate.UnityVersion,
                loadPolicy = candidate.LoadPolicy,
                sourceFileName = candidate.FileName,
                bytes = new FileInfo(destination).Length,
                status = "copied",
                error = (string?)null
            });
        }

        // Audit-only index. Runtime path resolution prefers the full SHA-256 name
        // and revalidates the complete hash immediately before LoadFromFile.
        File.WriteAllText(
            Path.Combine(resourcesDir, "manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    formatVersion = "resource-cache-manifest-v1",
                    modId = document.ModId,
                    copiedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                    candidates = entries
                },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    public static string ComputeCacheKey(PcModManifest manifest, PcCompatRecipeCompileReport report)
        => ComputeCacheKey(
            manifest,
            report,
            resourceRecipeSha256: string.Empty,
            resourceIrSha256: string.Empty);

    public static string ComputeCacheKey(
        PcModManifest manifest,
        PcCompatRecipeCompileReport report,
        string resourceRecipeSha256)
        => ComputeCacheKey(manifest, report, resourceRecipeSha256, resourceIrSha256: string.Empty);

    public static string ComputeCacheKey(
        PcModManifest manifest,
        PcCompatRecipeCompileReport report,
        string resourceRecipeSha256,
        string resourceIrSha256)
    {
        var builder = new StringBuilder();
        builder.AppendLine(FormatVersion);
        builder.AppendLine(manifest.Id);
        builder.AppendLine(manifest.Version);
        builder.AppendLine(manifest.Kind.ToString());
        builder.AppendLine(manifest.AssemblyName);
        builder.AppendLine(manifest.EntryMethod);
        builder.AppendLine(manifest.JAModAssemblyPath ?? string.Empty);
        builder.AppendLine(manifest.JAModClassName ?? string.Empty);
        builder.AppendLine(report.RecipeId);
        builder.AppendLine(report.Compatibility);
        builder.AppendLine(PcCompatRuntimeRuleBundle.CurrentFormatVersion);
        builder.AppendLine(PcCompatUiRecipeBinary.FormatVersion);
        builder.AppendLine(resourceRecipeSha256);
        builder.AppendLine(resourceIrSha256);
        builder.AppendLine(GetTargetGameRevision().ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.AppendLine(Convert.ToHexString(
            PcCompatUiRecipeBinary.ComputeSourceAssemblySha256(manifest)));
        foreach (var rule in report.Rules.OrderBy(rule => rule.Id, StringComparer.Ordinal))
        {
            builder.AppendLine(FormattableString.Invariant(
                $"{rule.Id}|{rule.TargetAssemblyName}|{rule.TargetNamespace}|{rule.TargetType}|{rule.TargetMethod}|{(rule.TargetIsStatic ? "static" : "instance")}|{rule.TargetGenericArity}|{rule.TargetReturnType}|{string.Join(';', rule.TargetParameterTypes)}|{rule.Stage}|{rule.Op}|{(ulong)rule.RequiredCapabilities}"));
        }
        foreach (var node in report.UiObjectGraph.OrderBy(node => node.Id))
        {
            builder.AppendLine(FormattableString.Invariant(
                $"ui-node|{node.Id}|{node.ParentId}|{node.Name}|{(uint)node.Components}|{(uint)node.Flags}"));
            foreach (var operation in node.Initialization)
            {
                builder.AppendLine(FormattableString.Invariant(
                    $"ui-op|{node.Id}|{(uint)operation.OpCode}|{operation.StringValue}|{operation.Payload0}|{operation.Payload1}|{operation.Payload2}|{operation.Payload3}"));
            }
        }
        foreach (var resource in report.UiResourceBindings
                     .OrderBy(binding => binding.NodeId)
                     .ThenBy(binding => binding.Target))
        {
            builder.AppendLine(FormattableString.Invariant(
                $"ui-resource|{resource.NodeId}|{(uint)resource.Target}|{resource.FeatureGroupId}|{resource.AssetName}|{resource.ExpectedType}"));
        }
        foreach (var lifecycle in report.UiLifecyclePrograms.OrderBy(program => program.Id, StringComparer.Ordinal))
        {
            builder.AppendLine(FormattableString.Invariant(
                $"lifecycle|{lifecycle.Id}|{lifecycle.RuntimeRuleId}|{(uint)lifecycle.Trigger}|{(uint)lifecycle.ClockDomain}|{(uint)lifecycle.Flags}|{lifecycle.InstructionBudget}|{lifecycle.CommandType}|{lifecycle.TargetId}|{lifecycle.InitialDelayNs}|{lifecycle.DeferredRetryDelayNs}"));
            foreach (var instruction in lifecycle.Instructions)
            {
                builder.AppendLine(FormattableString.Invariant(
                    $"vm|{(byte)instruction.Opcode}|{instruction.Destination}|{instruction.Source0}|{instruction.Source1}|{instruction.Immediate}|{instruction.Payload}"));
            }
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..32];
    }

    private static string GetBundleRoot(PcModManifest manifest, string modId)
    {
        var modDir = Path.GetFullPath(manifest.FolderPath);
        var modsRoot = Directory.GetParent(modDir)?.FullName ?? modDir;
        return Path.Combine(modsRoot, "compiled", SanitizePathSegment(modId));
    }

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
        if (string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or ".." || reserved)
        {
            var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
                .ToLowerInvariant()[..8];
            sanitized = "mod_" + suffix;
        }
        return sanitized;
    }

    private static bool IsRuntimeRuleBundleValid(string path, PcCompatRecipeCompileReport expected)
    {
        try
        {
            var expectedJson = PcCompatRuntimeRuleBundle.Serialize(PcCompatRuntimeRuleBundle.FromReport(expected));
            return File.ReadAllText(path).Equals(expectedJson, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRecipeReportValid(string path, PcCompatRecipeCompileReport expected)
    {
        try
        {
            return File.ReadAllText(path).Equals(PcCompatRecipeReportJson.Serialize(expected), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsResourceCacheValid(
        PcModManifest manifest,
        bool hasResourceRecipe,
        string expectedRecipeSha,
        string cachedRecipePath,
        bool hasResourceIr,
        string expectedIrSha,
        string cachedIrPath,
        string resourcesDirectory)
    {
        if (!hasResourceRecipe)
            return !hasResourceIr;
        try
        {
            if (!File.Exists(cachedRecipePath))
                return false;
            var actualRecipeSha = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(cachedRecipePath))).ToLowerInvariant();
            if (!actualRecipeSha.Equals(expectedRecipeSha, StringComparison.Ordinal))
                return false;
            if (!PcCompatResourceRecipe.TryRead(cachedRecipePath, out var document, out _) ||
                !PcCompatResourceRecipe.TryValidateDocument(document, manifest.Id, out _))
                return false;
            if (hasResourceIr)
            {
                if (!File.Exists(cachedIrPath))
                    return false;
                var actualIrSha = Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(cachedIrPath))).ToLowerInvariant();
                if (!actualIrSha.Equals(expectedIrSha, StringComparison.Ordinal) ||
                    !PcCompatResourceIr.TryRead(cachedIrPath, manifest.Id, out var resourceIr, out _) ||
                    !PcCompatResourceIr.TryValidateAgainstRecipe(resourceIr, document, out _) ||
                    !PcCompatResourceIr.TryVerifyPayloadFiles(cachedIrPath, resourceIr, out _))
                    return false;
            }

            foreach (var candidate in document.Candidates.Where(candidate =>
                         IsCacheableResourcePolicy(candidate.LoadPolicy)))
            {
                var path = ResolveCompiledCandidate(resourcesDirectory, candidate.Sha256Hex);
                if (path == null || !PcCompatResourceRecipe.TryVerifyCandidateFile(
                        path,
                        candidate.Sha256Hex,
                        candidate.FileSize,
                        out _))
                    return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyDirectoryContents(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        if (!Directory.Exists(source))
            return;
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string? ResolveCompiledCandidate(string resourcesDirectory, string sha256Hex)
    {
        if (!Directory.Exists(resourcesDirectory) || !PcCompatResourceRecipe.IsSha256(sha256Hex))
            return null;
        var root = Path.GetFullPath(resourcesDirectory);
        var normalizedSha = sha256Hex.ToLowerInvariant();
        var paths = Directory.EnumerateFiles(root, normalizedSha + "_*")
            .Concat(Directory.EnumerateFiles(root, normalizedSha[..16] + "_*"))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var fullPath = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(root, fullPath);
            if (!Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                return fullPath;
        }
        return null;
    }

    private static bool IsCacheableResourcePolicy(string policy)
        => policy.Equals("AutoLoad", StringComparison.OrdinalIgnoreCase) || policy == "0" ||
           policy.Equals("ControlledLoad", StringComparison.OrdinalIgnoreCase) || policy == "1" ||
           policy.Equals("ForceRequired", StringComparison.OrdinalIgnoreCase) || policy == "2";

    private static int GetTargetGameRevision()
    {
        var value = Environment.GetEnvironmentVariable("STARRAY_PCMOD_COMPAT_GAME_REVISION");
        return int.TryParse(value, out var revision) && revision > 0
            ? revision
            : PcCompatStaticPatchScanner.DefaultTargetGameRevision;
    }
}

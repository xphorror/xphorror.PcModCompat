using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Xphorror.PcModCompat.Resources;

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

try
{
    var command = args[0];
    if (command.Equals("index", StringComparison.OrdinalIgnoreCase))
        return RunIndex(args);
    if (command.Equals("compile", StringComparison.OrdinalIgnoreCase))
        return RunCompile(args);
    if (command.Equals("validate", StringComparison.OrdinalIgnoreCase))
        return RunValidate(args);
    if (command.Equals("validate-ir", StringComparison.OrdinalIgnoreCase))
        return RunValidateIr(args);
    if (command.Equals("summary", StringComparison.OrdinalIgnoreCase))
        return RunSummary(args);
    if (command.Equals("inspect-asset", StringComparison.OrdinalIgnoreCase))
        return RunInspectAsset(args);
    if (command.Equals("inspect-path", StringComparison.OrdinalIgnoreCase))
        return RunInspectPath(args);

    PrintUsage();
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
    return 1;
}

static int RunIndex(string[] args)
{
    if (args.Length < 2)
    {
        PrintUsage();
        return 2;
    }

    var path = Path.GetFullPath(args[1]);
    if (Directory.Exists(path))
    {
        var candidates = UnityBundleIndexer.IndexModFolder(path);
        Console.WriteLine($"candidates={candidates.Count}");
        foreach (var candidate in candidates)
            PrintCandidate(candidate);
        return candidates.Any(c => c.IndexSucceeded) ? 0 : 1;
    }

    var single = UnityBundleIndexer.IndexFile(path);
    PrintCandidate(single);
    return single.IndexSucceeded ? 0 : 1;
}

static int RunCompile(string[] args)
{
    if (args.Length < 3)
    {
        PrintUsage();
        return 2;
    }

    var modFolder = Path.GetFullPath(args[1]);
    var outputDir = Path.GetFullPath(args[2]);
    var modId = args.Length >= 4 && !string.IsNullOrWhiteSpace(args[3])
        ? args[3]
        : new DirectoryInfo(modFolder).Name;
    var allowForce = args.Any(arg => arg.Equals("--force-non-6000", StringComparison.OrdinalIgnoreCase));

    Directory.CreateDirectory(outputDir);
    var report = ResourceCompiler.CompileModFolder(modId, modFolder, allowForce);
    var reportPath = Path.Combine(outputDir, "resource_report.json");
    var recipePath = Path.Combine(outputDir, "resource_recipe.bin");
    var resourceIrPath = Path.Combine(outputDir, "resource_ir.bin");
    var resourceIrPayloadDirectory = Path.Combine(outputDir, "resource_ir_blobs");
    var compilerMarkerPath = Path.Combine(outputDir, ResourceIrCompiler.CacheMarkerFileName);
    File.WriteAllText(reportPath, ResourceCompiler.ToJson(report));
    ResourceRecipeBinary.Write(recipePath, report);
    if (!ResourceRecipeBinary.TryValidate(recipePath, out var error))
        throw new InvalidDataException("resource_recipe.bin failed validation: " + error);
    var resourceIr = ResourceIrCompiler.Build(report, modFolder, resourceIrPayloadDirectory);
    ResourceIrBinary.Write(resourceIrPath, resourceIr);
    if (!ResourceIrBinary.TryValidate(resourceIrPath, out var irError))
        throw new InvalidDataException("resource_ir.bin failed validation: " + irError);
    if (!ResourceIrBinary.TryVerifyPayloadFiles(resourceIrPath, resourceIr, out var payloadError))
        throw new InvalidDataException("resource_ir.bin payload validation failed: " + payloadError);
    var inputFingerprint = ResourceCompileInputFingerprint.Compute(modId, modFolder);
    File.WriteAllText(
        compilerMarkerPath,
        ResourceCompileInputFingerprint.BuildCompilerMarker(inputFingerprint),
        new System.Text.UTF8Encoding(false));

    // Also publish beside the MOD so PcCompatRecipeBundleCache can atomically
    // include the resource recipe without linking AssetsTools into the runtime.
    var modCompatDir = Path.Combine(modFolder, ".pccompat");
    Directory.CreateDirectory(modCompatDir);
    var modReportPath = Path.Combine(modCompatDir, "resource_report.json");
    var modRecipePath = Path.Combine(modCompatDir, "resource_recipe.bin");
    var modResourceIrPath = Path.Combine(modCompatDir, "resource_ir.bin");
    var modResourceIrPayloadDirectory = Path.Combine(modCompatDir, "resource_ir_blobs");
    var modCompilerMarkerPath = Path.Combine(modCompatDir, ResourceIrCompiler.CacheMarkerFileName);
    File.Copy(reportPath, modReportPath, overwrite: true);
    File.Copy(recipePath, modRecipePath, overwrite: true);
    File.Copy(resourceIrPath, modResourceIrPath, overwrite: true);
    File.Copy(compilerMarkerPath, modCompilerMarkerPath, overwrite: true);
    CopyDirectory(resourceIrPayloadDirectory, modResourceIrPayloadDirectory);

    Console.WriteLine($"mod={report.ModId}");
    Console.WriteLine($"compatibility={report.Compatibility}");
    Console.WriteLine($"candidates={report.Candidates.Count}");
    Console.WriteLine($"groups={report.FeatureGroups.Count}");
    Console.WriteLine($"bindings={report.Bindings.Count}");
    Console.WriteLine($"unsupported={report.Unsupported.Count}");
    Console.WriteLine($"report={reportPath}");
    Console.WriteLine($"recipe={recipePath}");
    Console.WriteLine($"modCompatRecipe={modRecipePath}");
    Console.WriteLine($"resourceIr={resourceIrPath}");
    Console.WriteLine($"modCompatResourceIr={modResourceIrPath}");
    Console.WriteLine($"resourceIrCompiler={ResourceIrCompiler.CompilerRevision}");
    Console.WriteLine($"resourceInputFingerprint={inputFingerprint}");
    Console.WriteLine($"irBundles={resourceIr.Bundles.Count}");
    Console.WriteLine($"irAssets={resourceIr.Assets.Count}");
    Console.WriteLine($"irRequired={resourceIr.Assets.Count(asset => asset.RequiredByMod)}");
    Console.WriteLine($"irCapabilityAliases={resourceIr.Assets.Count(asset => asset.MaterializationKind == ResourceIrMaterializationKind.CapabilityReference)}");
    Console.WriteLine($"irRgbaTextures={resourceIr.Assets.Count(asset => asset.MaterializationKind == ResourceIrMaterializationKind.TextureRgba32)}");
    Console.WriteLine($"irSprites={resourceIr.Assets.Count(asset => asset.MaterializationKind == ResourceIrMaterializationKind.SpriteFromTexture)}");
    Console.WriteLine($"irMaterials={resourceIr.Assets.Count(asset => asset.MaterializationKind == ResourceIrMaterializationKind.MaterialFromCapabilityShader)}");
    Console.WriteLine($"irPrefabGraphs={resourceIr.Assets.Count(asset => asset.MaterializationKind == ResourceIrMaterializationKind.PrefabGraph)}");
    return report.Compatibility == "unsupported" ? 1 : 0;
}

static int RunValidate(string[] args)
{
    if (args.Length < 2)
    {
        PrintUsage();
        return 2;
    }

    var path = Path.GetFullPath(args[1]);
    if (!ResourceRecipeBinary.TryValidate(path, out var error))
    {
        Console.Error.WriteLine($"invalid path={path} error={error}");
        return 1;
    }

    Console.WriteLine($"valid path={path} size={new FileInfo(path).Length} format={ResourceRecipeBinary.FormatVersion}");
    return 0;
}

static int RunValidateIr(string[] args)
{
    if (args.Length < 2)
    {
        PrintUsage();
        return 2;
    }
    var path = Path.GetFullPath(args[1]);
    if (!ResourceIrBinary.TryRead(path, out var document, out var error))
    {
        Console.Error.WriteLine($"invalid-ir path={path} error={error}");
        return 1;
    }
    Console.WriteLine(
        $"valid-ir path={path} size={new FileInfo(path).Length} format={document.FormatVersion} " +
        $"bundles={document.Bundles.Count} assets={document.Assets.Count} " +
        $"required={document.Assets.Count(asset => asset.RequiredByMod)}");
    return 0;
}

static int RunSummary(string[] args)
{
    if (args.Length < 2)
    {
        PrintUsage();
        return 2;
    }

    var path = Path.GetFullPath(args[1]);
    if (!File.Exists(path))
    {
        Console.Error.WriteLine("missing file: " + path);
        return 1;
    }

    // Offline audit-only view of the import recipe. Does not load Unity or AssetBundles.
    string reportJson;
    if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
    {
        reportJson = File.ReadAllText(path);
    }
    else
    {
        if (!ResourceRecipeBinary.TryValidate(path, out var error))
        {
            Console.Error.WriteLine($"invalid path={path} error={error}");
            return 1;
        }

        // resource_recipe.bin payload starts after the fixed header.
        var bytes = File.ReadAllBytes(path);
        reportJson = System.Text.Encoding.UTF8.GetString(bytes, 64, bytes.Length - 64);
    }

    using var document = System.Text.Json.JsonDocument.Parse(reportJson);
    var root = document.RootElement;
    Console.WriteLine("mod=" + ReadString(root, "modId"));
    Console.WriteLine("compatibility=" + ReadString(root, "compatibility"));
    Console.WriteLine("recipeId=" + ReadString(root, "recipeId"));
    Console.WriteLine("runtimeLoadDefault=disabled");
    Console.WriteLine("runtimeLoadGate=STARRAY_PCMOD_RESOURCE_LOAD");

    var autoLoadCandidates = 0;
    var controlledLoadCandidates = 0;
    var rejectedCandidates = 0;
    var provenBindings = 0;
    var uniqueBindings = 0;
    if (root.TryGetProperty("candidates", out var candidateStats) &&
        candidateStats.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        foreach (var candidate in candidateStats.EnumerateArray())
        {
            var policy = ReadFlexible(candidate, "loadPolicy");
            if (policy.Equals("AutoLoad", StringComparison.OrdinalIgnoreCase) || policy == "0")
                autoLoadCandidates++;
            if (policy.Equals("ControlledLoad", StringComparison.OrdinalIgnoreCase) || policy == "1")
                controlledLoadCandidates++;
            else if (policy.Equals("Rejected", StringComparison.OrdinalIgnoreCase) ||
                     policy.Equals("ForceRequired", StringComparison.OrdinalIgnoreCase) ||
                     policy is "2" or "4")
                rejectedCandidates++;
        }
    }
    if (root.TryGetProperty("bindings", out var bindingStats) &&
        bindingStats.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        foreach (var binding in bindingStats.EnumerateArray())
        {
            var confidence = ReadFlexible(binding, "confidence");
            if (confidence.Equals("Proven", StringComparison.OrdinalIgnoreCase) || confidence == "1")
                provenBindings++;
            else if (confidence.Equals("UniqueType", StringComparison.OrdinalIgnoreCase) || confidence == "2")
                uniqueBindings++;
        }
    }
    Console.WriteLine($"autoLoadCandidates={autoLoadCandidates}");
    Console.WriteLine($"controlledLoadCandidates={controlledLoadCandidates}");
    Console.WriteLine($"rejectedOrForcedCandidates={rejectedCandidates}");
    Console.WriteLine($"provenBindings={provenBindings}");
    Console.WriteLine($"uniqueTypeBindings={uniqueBindings}");

    if (root.TryGetProperty("featureGroups", out var groups) &&
        groups.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        Console.WriteLine("groups=" + groups.GetArrayLength());
        foreach (var group in groups.EnumerateArray())
        {
            var assetCount = group.TryGetProperty("assetNames", out var assets) &&
                             assets.ValueKind == System.Text.Json.JsonValueKind.Array
                ? assets.GetArrayLength()
                : 0;
            Console.WriteLine(
                $"  group id={ReadString(group, "id")} assets={assetCount} " +
                $"policy={ReadFlexible(group, "loadPolicy")} platform={ReadFlexible(group, "selectedPlatform")}");
        }
    }

    if (root.TryGetProperty("bindings", out var bindings) &&
        bindings.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        Console.WriteLine("bindings=" + bindings.GetArrayLength());
        foreach (var binding in bindings.EnumerateArray().Take(12))
        {
            Console.WriteLine(
                $"  binding confidence={ReadFlexible(binding, "confidence")} " +
                $"type={ReadString(binding, "expectedType")} name={ReadString(binding, "assetName")} " +
                $"group={ReadString(binding, "featureGroupId")}");
        }
    }

    if (root.TryGetProperty("candidates", out var candidates) &&
        candidates.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        Console.WriteLine("candidates=" + candidates.GetArrayLength());
        foreach (var candidate in candidates.EnumerateArray())
        {
            Console.WriteLine(
                $"  candidate file={ReadString(candidate, "fileName")} " +
                $"unity={ReadString(candidate, "unityVersion")} " +
                $"policy={ReadFlexible(candidate, "loadPolicy")} " +
                $"platform={ReadFlexible(candidate, "platformHint")}");
        }
    }

    return 0;
}

static int RunInspectAsset(string[] args)
{
    if (args.Length < 3)
    {
        PrintUsage();
        return 2;
    }
    var path = Path.GetFullPath(args[1]);
    var requestedName = args[2];
    using var stream = File.OpenRead(path);
    var manager = new AssetsManager();
    try
    {
        var bundle = manager.LoadBundleFile(stream, path);
        var found = 0;
        for (var index = 0; index < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; index++)
        {
            var directoryName = bundle.file.BlockAndDirInfo.DirectoryInfos[index].Name ?? string.Empty;
            if (directoryName.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) ||
                directoryName.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
                continue;
            AssetsFileInstance assetsFile;
            try { assetsFile = manager.LoadAssetsFileFromBundle(bundle, index, false); }
            catch { continue; }
            foreach (var info in assetsFile.file.AssetInfos)
            {
                AssetTypeValueField baseField;
                try { baseField = manager.GetBaseField(assetsFile, info, AssetReadFlags.None); }
                catch { continue; }
                if (baseField["m_Name"].IsDummy ||
                    !baseField["m_Name"].AsString.Equals(requestedName, StringComparison.Ordinal))
                    continue;
                found++;
                Console.WriteLine(
                    $"asset name={requestedName} typeId={info.TypeId} pathId={info.PathId} " +
                    $"type={baseField.TypeName} container={directoryName}");
                PrintField(baseField, 0, 5);
            }
        }
        return found == 0 ? 1 : 0;
    }
    finally
    {
        manager.UnloadAll(true);
    }
}

static int RunInspectPath(string[] args)
{
    if (args.Length < 3 || !long.TryParse(args[2], out var requestedPathId))
    {
        PrintUsage();
        return 2;
    }
    var path = Path.GetFullPath(args[1]);
    using var stream = File.OpenRead(path);
    var manager = new AssetsManager();
    try
    {
        var bundle = manager.LoadBundleFile(stream, path);
        for (var index = 0; index < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; index++)
        {
            var directoryName = bundle.file.BlockAndDirInfo.DirectoryInfos[index].Name ?? string.Empty;
            if (directoryName.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) ||
                directoryName.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
                continue;
            AssetsFileInstance assetsFile;
            try { assetsFile = manager.LoadAssetsFileFromBundle(bundle, index, false); }
            catch { continue; }
            var info = assetsFile.file.GetAssetInfo(requestedPathId);
            if (info == null)
                continue;
            var baseField = manager.GetBaseField(assetsFile, info, AssetReadFlags.None);
            Console.WriteLine(
                $"asset pathId={requestedPathId} typeId={info.TypeId} type={baseField.TypeName} " +
                $"container={directoryName}");
            PrintField(baseField, 0, 5);
            return 0;
        }
        Console.Error.WriteLine($"asset pathId was not found: {requestedPathId}");
        return 1;
    }
    finally
    {
        manager.UnloadAll(true);
    }
}

static void PrintField(AssetTypeValueField field, int depth, int maxDepth)
{
    var indent = new string(' ', depth * 2);
    var value = field.Value == null
        ? string.Empty
        : field.Value.ValueType == AssetValueType.ByteArray
            ? $" bytes={field.AsByteArray?.Length ?? 0}"
            : field.Value.ValueType == AssetValueType.String
                ? $" value='{field.AsString}'"
                : field.Value.ValueType == AssetValueType.None
                    ? string.Empty
                    : $" value={field.Value}";
    Console.WriteLine($"{indent}{field.FieldName}:{field.TypeName} kind={field.Value?.ValueType}{value}");
    if (depth >= maxDepth)
        return;
    foreach (var child in field.Children)
        PrintField(child, depth + 1, maxDepth);
}

static string ReadString(System.Text.Json.JsonElement element, string name)
    => element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
        ? value.GetString() ?? string.Empty
        : string.Empty;

static string ReadFlexible(System.Text.Json.JsonElement element, string name)
{
    if (!element.TryGetProperty(name, out var value))
        return string.Empty;
    return value.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => value.GetString() ?? string.Empty,
        System.Text.Json.JsonValueKind.Number => value.GetRawText(),
        System.Text.Json.JsonValueKind.True => "true",
        System.Text.Json.JsonValueKind.False => "false",
        _ => value.ToString()
    };
}

static void CopyDirectory(string source, string destination)
{
    if (Directory.Exists(destination))
        Directory.Delete(destination, recursive: true);
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

static void PrintCandidate(ResourceCandidateIndex candidate)
{
    Console.WriteLine(
        $"[{(candidate.IndexSucceeded ? "ok" : "fail")}] platform={candidate.PlatformHint} " +
        $"unity={candidate.UnityVersion} gate={candidate.VersionGate} policy={candidate.LoadPolicy} " +
        $"assets={candidate.Assets.Count} typeTree={candidate.HasEmbeddedTypeTree} file={candidate.FileName}");
    if (!string.IsNullOrWhiteSpace(candidate.IndexError))
        Console.WriteLine("  error=" + candidate.IndexError);
    foreach (var warning in candidate.Warnings.Take(5))
        Console.WriteLine("  warn=" + warning);
    foreach (var asset in candidate.Assets.Take(12))
        Console.WriteLine($"  asset type={asset.TypeName} name={asset.Name} pathId={asset.PathId}");
    if (candidate.Assets.Count > 12)
        Console.WriteLine($"  ... {candidate.Assets.Count - 12} more assets");
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  ResourceRecipeTool index <bundle-or-mod-folder>");
    Console.Error.WriteLine("  ResourceRecipeTool compile <mod-folder> <output-dir> [mod-id] [--force-non-6000]");
    Console.Error.WriteLine("  ResourceRecipeTool validate <resource_recipe.bin>");
    Console.Error.WriteLine("  ResourceRecipeTool validate-ir <resource_ir.bin>");
    Console.Error.WriteLine("  ResourceRecipeTool summary <resource_recipe.bin|resource_report.json>");
    Console.Error.WriteLine("  ResourceRecipeTool inspect-asset <bundle> <asset-name>");
    Console.Error.WriteLine("  ResourceRecipeTool inspect-path <bundle> <path-id>");
}

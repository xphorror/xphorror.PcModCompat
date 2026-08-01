using System.Reflection;
using System.Runtime.Loader;
using Xphorror.PcModCompat;
using Xphorror.PcModCompat.Resources;

var options = ProbeOptions.Parse(args);
if (options == null)
    return 2;

if (!PcModManifestReader.TryRead(options.ModFolder, out var manifest, out var manifestError))
{
    Console.Error.WriteLine($"[manifest] failed path={options.ModFolder} error={manifestError ?? "Info.json not found"}");
    return 2;
}

Console.WriteLine($"[manifest] id={manifest.Id} name={manifest.DisplayName} version={manifest.Version} kind={manifest.Kind}");
Console.WriteLine($"[manifest] entryAssembly={manifest.AssemblyName} entryMethod={manifest.EntryMethod}");
if (manifest.Kind == PcModKind.JAMod)
    Console.WriteLine($"[manifest] jamodAssembly={manifest.JAModAssemblyPath ?? "<none>"} jamodClass={manifest.JAModClassName ?? "<none>"} requireModPath={manifest.AssemblyRequireModPath}");

var staticScan = PcCompatStaticPatchScanner.Scan(manifest);
PrintStaticPatchScan(staticScan);
var callbackTranslation = PcCompatCallbackTranslator.Translate(manifest, staticScan);
PrintCallbackTranslation(callbackTranslation);
if (options.StaticScanOnly)
{
    Console.WriteLine(staticScan.ToJson());
    return staticScan.Issues.Any(issue => issue.Code is "MetadataReadFailed" or "BadManagedImage") ? 1 : 0;
}

var hasRecipe = PcCompatRecipeCompiler.TryCompile(manifest, staticScan, callbackTranslation, out var recipeReport, out var recipeError);
if (hasRecipe)
{
    PrintRecipeReport(recipeReport);
}
else
{
    Console.WriteLine($"[recipe] unavailable reason={recipeError}");
}

// Offline import path only: publish resource_recipe next to the MOD for cache/runtime plan.
// Does not LoadFromFile and does not change Android HUD behavior.
var resourceReport = ResourceCompiler.CompileModFolder(manifest.Id, options.ModFolder);
PrintResourceReport(resourceReport);
var resourceOutDir = Path.Combine(options.ModFolder, ".pccompat");
Directory.CreateDirectory(resourceOutDir);
var resourceReportPath = Path.Combine(resourceOutDir, "resource_report.json");
var resourceRecipePath = Path.Combine(resourceOutDir, "resource_recipe.bin");
File.WriteAllText(resourceReportPath, ResourceCompiler.ToJson(resourceReport));
ResourceRecipeBinary.Write(resourceRecipePath, resourceReport);
if (!ResourceRecipeBinary.TryValidate(resourceRecipePath, out var resourceRecipeError))
{
    Console.Error.WriteLine($"[resource] recipe validation failed: {resourceRecipeError}");
    if (options.RecipeOnly)
        return 1;
}
else
{
    Console.WriteLine($"[resource] recipe={resourceRecipePath}");
    Console.WriteLine($"[resource] report={resourceReportPath}");
}

if (options.RecipeOnly)
{
    if (hasRecipe)
        Console.WriteLine(PcCompatRecipeReportJson.Serialize(recipeReport));
    return hasRecipe ? 0 : 1;
}

var targetAssemblyPath = ResolveTargetAssembly(manifest);
if (targetAssemblyPath == null || !File.Exists(targetAssemblyPath))
{
    Console.Error.WriteLine($"[target] missing path={targetAssemblyPath ?? "<none>"}");
    return 2;
}

if (!Directory.Exists(options.ShimFolder))
{
    Console.Error.WriteLine($"[shim] missing path={options.ShimFolder}");
    return 2;
}

var context = new ProbeLoadContext(options.ModFolder, options.ShimFolder);

try
{
    var modEntry = CreateUnityModEntry(context, manifest);
    Console.WriteLine($"[umm] ModEntry created id={manifest.Id}");

    if (options.TryBootstrap && !string.IsNullOrWhiteSpace(manifest.EntryAssemblyPath) && File.Exists(manifest.EntryAssemblyPath))
        TryInvokeBootstrap(context, manifest, modEntry);

    ClearPatchRegistry(context);

    var asm = context.LoadFromAssemblyPath(targetAssemblyPath);
    Console.WriteLine($"[load] {Path.GetFileName(targetAssemblyPath)} -> {asm.FullName}");

    foreach (var reference in asm.GetReferencedAssemblies().OrderBy(a => a.Name))
        Console.WriteLine($"[ref] {reference.FullName}");

    var types = asm.GetTypes();
    Console.WriteLine($"[types] count={types.Length}");

    var mainTypeName = ResolveMainTypeName(manifest);
    var mainType = asm.GetType(mainTypeName, throwOnError: true)!;
    Console.WriteLine($"[main] {mainType.FullName}");

    var instance = Activator.CreateInstance(mainType);
    Console.WriteLine($"[new] {instance?.GetType().FullName ?? "<null>"}");

    InvokeLifecycle(instance, "CompatSetup", new object?[] { options.ModFolder }, required: true);
    Console.WriteLine("[lifecycle] setup=ok");

    PrintPatchRegistry(context);

    if (options.Enable)
    {
        InvokeLifecycle(instance, "CompatEnable", Array.Empty<object?>(), required: true);
        Console.WriteLine("[lifecycle] enable=ok");
    }
    else
    {
        Console.WriteLine("[lifecycle] enable=skipped reason=native bridge/reverse-patch runtime not enabled in probe");
    }

    return 0;
}
catch (ReflectionTypeLoadException ex)
{
    Console.Error.WriteLine("[ReflectionTypeLoadException]");
    foreach (var loaderException in ex.LoaderExceptions)
        Console.Error.WriteLine(loaderException);
    return 1;
}
catch (TargetInvocationException ex)
{
    Console.Error.WriteLine(ex.InnerException ?? ex);
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

static string? ResolveTargetAssembly(PcModManifest manifest)
{
    if (manifest.Kind == PcModKind.JAMod && !string.IsNullOrWhiteSpace(manifest.JAModAssemblyFullPath))
        return manifest.JAModAssemblyFullPath;

    if (!string.IsNullOrWhiteSpace(manifest.EntryAssemblyPath))
        return manifest.EntryAssemblyPath;

    return Directory.GetFiles(manifest.FolderPath, "*.dll").FirstOrDefault();
}

static string ResolveMainTypeName(PcModManifest manifest)
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

static object CreateUnityModEntry(ProbeLoadContext context, PcModManifest manifest)
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

static void SetField(object target, string name, object? value)
    => target.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance)!.SetValue(target, value);

static void TryInvokeBootstrap(ProbeLoadContext context, PcModManifest manifest, object modEntry)
{
    try
    {
        var entryAssembly = context.LoadFromAssemblyPath(manifest.EntryAssemblyPath);
        var (typeName, methodName) = SplitEntryMethod(manifest.EntryMethod);
        var type = entryAssembly.GetType(typeName, throwOnError: true)!;
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(type.FullName, methodName);
        var parameters = BuildArguments(method, modEntry, manifest.FolderPath);
        var result = method.Invoke(null, parameters);
        Console.WriteLine($"[bootstrap] invoked {manifest.EntryMethod} result={result ?? "<void>"}");
    }
    catch (Exception ex)
    {
        var inner = ex is TargetInvocationException tie ? tie.InnerException ?? tie : ex;
        Console.WriteLine($"[bootstrap] skipped_or_failed method={manifest.EntryMethod} error={inner.GetType().Name}: {inner.Message}");
    }
}

static (string TypeName, string MethodName) SplitEntryMethod(string entryMethod)
{
    var split = entryMethod.LastIndexOf('.');
    if (split <= 0 || split == entryMethod.Length - 1)
        throw new InvalidOperationException($"unsupported EntryMethod format: {entryMethod}");
    return (entryMethod[..split], entryMethod[(split + 1)..]);
}

static object?[] BuildArguments(MethodInfo method, object modEntry, string modFolder)
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

static void InvokeLifecycle(object? instance, string methodName, object?[] args, bool required)
{
    if (instance == null)
        throw new InvalidOperationException("mod instance is null");

    var method = instance.GetType().BaseType?.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
    if (method == null)
    {
        if (required)
            throw new MissingMethodException(instance.GetType().BaseType?.FullName, methodName);
        return;
    }

    method.Invoke(instance, args);
}

static void ClearPatchRegistry(ProbeLoadContext context)
{
    var jalib = context.LoadFromAssemblyName(new AssemblyName("JALib"));
    var patcherType = jalib.GetType("JALib.Core.Patch.JAPatcher", throwOnError: true)!;
    patcherType.GetMethod("ClearRegisteredPatches", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
}

static void PrintPatchRegistry(ProbeLoadContext context)
{
    var jalib = context.LoadFromAssemblyName(new AssemblyName("JALib"));
    var patcherType = jalib.GetType("JALib.Core.Patch.JAPatcher", throwOnError: true)!;
    var snapshot = (Array?)patcherType.GetMethod("SnapshotRegisteredPatches", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
    if (snapshot == null)
    {
        Console.WriteLine("[patches] unavailable");
        return;
    }

    Console.WriteLine($"[patches] count={snapshot.Length}");
    foreach (var record in snapshot)
    {
        var type = record.GetType();
        string Get(string name) => type.GetProperty(name)!.GetValue(record)?.ToString() ?? string.Empty;
        Console.WriteLine($"[patch] mod={Get("ModType")} target={Get("TargetType")}.{Get("TargetMethod")} kind={Get("Kind")} callback={Get("CallbackType")}.{Get("CallbackMethod")} status={Get("Status")} reason={Get("Reason")}");
    }
}

static void PrintRecipeReport(PcCompatRecipeCompileReport report)
{
    Console.WriteLine($"[recipe] id={report.RecipeId} compatibility={report.Compatibility} features={report.Features.Count} rules={report.Rules.Count} unsupported={report.Unsupported.Count} capabilities=0x{((ulong)report.RequiredCapabilities):X}");
    Console.WriteLine($"[recipe-ui] nodes={report.UiObjectGraph.Count} roots={report.UiObjectGraph.Count(node => node.ParentId == 0)} lifecycle={report.UiLifecyclePrograms.Count} operations={report.UiObjectGraph.Sum(node => node.Initialization.Count)}");
    foreach (var feature in report.Features)
        Console.WriteLine($"[recipe-feature] id={feature.Id} status={feature.Status} rules={feature.RuleIds.Count} name={feature.DisplayName}");
    foreach (var rule in report.Rules)
        Console.WriteLine($"[recipe-rule] id={rule.Id} target={rule.TargetType}.{rule.TargetMethod} stage={rule.Stage} op={rule.Op} caps=0x{((ulong)rule.RequiredCapabilities):X}");
    foreach (var item in report.Unsupported)
        Console.WriteLine($"[recipe-unsupported] id={item.Id} severity={item.Severity} reason={item.Reason}");
}

static void PrintResourceReport(ResourceCompileReport report)
{
    var autoLoad = report.Candidates.Count(candidate => candidate.LoadPolicy == BundleLoadPolicy.AutoLoad);
    var controlledLoad = report.Candidates.Count(candidate => candidate.LoadPolicy == BundleLoadPolicy.ControlledLoad);
    var rejected = report.Candidates.Count(candidate =>
        candidate.LoadPolicy is BundleLoadPolicy.Rejected or BundleLoadPolicy.ForceRequired);
    var proven = report.Bindings.Count(binding => binding.Confidence == AssetBindConfidence.Proven);
    Console.WriteLine(
        $"[resource] compatibility={report.Compatibility} candidates={report.Candidates.Count} " +
        $"autoLoad={autoLoad} controlledLoad={controlledLoad} rejectedOrForced={rejected} groups={report.FeatureGroups.Count} " +
        $"bindings={report.Bindings.Count} proven={proven} unsupported={report.Unsupported.Count}");
    foreach (var group in report.FeatureGroups)
        Console.WriteLine(
            $"[resource-group] id={group.Id} assets={group.AssetNames.Count} " +
            $"policy={group.LoadPolicy} platform={group.SelectedPlatform}");
    foreach (var binding in report.Bindings.Take(12))
        Console.WriteLine(
            $"[resource-binding] confidence={binding.Confidence} type={binding.ExpectedType} " +
            $"name={binding.AssetName} group={binding.FeatureGroupId}");
    foreach (var item in report.Unsupported.Take(8))
        Console.WriteLine($"[resource-unsupported] {item}");
}

static void PrintStaticPatchScan(PcCompatStaticPatchScanReport report)
{
    var directCount = report.Patches.Count(patch => patch.Source == "static_attribute");
    var dynamicCount = report.Patches.Count(patch => patch.Source == "dynamic_addpatch");
    Console.WriteLine($"[static-scan] format={report.FormatVersion} assemblies={report.AssembliesScanned.Count} patches={report.Patches.Count} direct={directCount} dynamic={dynamicCount} active-r{report.TargetGameRevision}={report.ActivePatches.Count} issues={report.Issues.Count}");
    foreach (var patch in report.Patches)
    {
        var active = patch.IsApplicableToRevision(report.TargetGameRevision) ? 1 : 0;
        Console.WriteLine($"[static-patch] source={patch.Source} target={patch.TargetType}.{patch.TargetMethod} kind={patch.Kind} callback={patch.CallbackType}.{patch.CallbackMethod} version={patch.MinVersion}..{(patch.MaxVersion == int.MaxValue ? "max" : patch.MaxVersion)} active={active} needInstance={(patch.NeedInstance ? 1 : 0)} tryingCatch={(patch.TryingCatch ? 1 : 0)} args=[{string.Join(",", patch.ArgumentTypeNames)}]");
    }
    foreach (var issue in report.Issues)
        Console.WriteLine($"[static-issue] code={issue.Code} callback={issue.CallbackType ?? "<none>"}.{issue.CallbackMethod ?? "<none>"} message={issue.Message}");
}

static void PrintCallbackTranslation(PcCompatCallbackTranslationReport report)
{
    Console.WriteLine($"[callback-translation] format={report.FormatVersion} rules={report.Rules.Count} translated={report.TranslatedCount} unsupported={report.UnsupportedCount} items={report.Items.Count}");
    foreach (var rule in report.Rules)
        Console.WriteLine($"[callback-rule] id={rule.Id} target={rule.TargetType}.{rule.TargetMethod} op={rule.Op} source={rule.Source}");
    foreach (var item in report.Items.Where(item => item.Status == PcCompatCallbackTranslationStatus.Unsupported))
        Console.WriteLine($"[callback-unsupported] callback={item.CallbackType}.{item.CallbackMethod} target={item.TargetType}.{item.TargetMethod} reason={item.Reason}");
}

internal sealed class ProbeOptions
{
    private ProbeOptions(string modFolder, string shimFolder, bool enable, bool tryBootstrap, bool recipeOnly, bool staticScanOnly)
    {
        ModFolder = modFolder;
        ShimFolder = shimFolder;
        Enable = enable;
        TryBootstrap = tryBootstrap;
        RecipeOnly = recipeOnly;
        StaticScanOnly = staticScanOnly;
    }

    public string ModFolder { get; }
    public string ShimFolder { get; }
    public bool Enable { get; }
    public bool TryBootstrap { get; }
    public bool RecipeOnly { get; }
    public bool StaticScanOnly { get; }

    public static ProbeOptions? Parse(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: PcCompatProbe <mod-folder> <shim-folder> [--bootstrap] [--enable] [--recipe-only] [--static-scan-only]");
            return null;
        }

        var flags = args.Skip(2).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ProbeOptions(
            Path.GetFullPath(args[0]),
            Path.GetFullPath(args[1]),
            flags.Contains("--enable"),
            flags.Contains("--bootstrap"),
            flags.Contains("--recipe-only"),
            flags.Contains("--static-scan-only"));
    }
}

internal sealed class ProbeLoadContext : AssemblyLoadContext
{
    private readonly Dictionary<string, string> _assemblyPaths;

    public ProbeLoadContext(string modFolder, string shimFolder)
        : base("PcCompatProbe", isCollectible: true)
    {
        _assemblyPaths = Directory.GetFiles(shimFolder, "*.dll")
            .Concat(Directory.GetFiles(modFolder, "*.dll"))
            .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (_assemblyPaths.TryGetValue(assemblyName.Name ?? string.Empty, out var path))
        {
            Console.WriteLine($"[resolve] {assemblyName.Name} -> {path}");
            return LoadFromAssemblyPath(path);
        }

        Console.WriteLine($"[missing] {assemblyName.FullName}");
        return null;
    }
}

using System.Text;
using Xphorror.PcModCompat;
using Xphorror.PcModCompat.Resources;

namespace StArray.ModManager.Android.PcCompat;

internal static class PcCompatAndroidResourceAssemblyCompile
{
    private static readonly SemaphoreSlim CompileGate = new(1, 1);
    private static long s_tempSequence;

    public static void Install()
        => PcCompatResourceAssemblyCompile.RegisterProvider(Prepare);

    private static PcCompatResourceCompileInfo Prepare(
        PcModManifest manifest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var outputDirectory = Path.Combine(manifest.FolderPath, ".pccompat");
        var recipePath = Path.Combine(outputDirectory, "resource_recipe.bin");
        var resourceIrPath = Path.Combine(outputDirectory, "resource_ir.bin");
        var resourceIrPayloadDirectory = Path.Combine(outputDirectory, "resource_ir_blobs");
        var compilerMarkerPath = Path.Combine(outputDirectory, ResourceIrCompiler.CacheMarkerFileName);
        var reportPath = Path.Combine(outputDirectory, "resource_report.json");

        if (TryReadExisting(
                manifest,
                recipePath,
                resourceIrPath,
                compilerMarkerPath,
                reportPath,
                out var existing))
            return existing;

        CompileGate.Wait(cancellationToken);
        try
        {
            if (TryReadExisting(
                    manifest,
                    recipePath,
                    resourceIrPath,
                    compilerMarkerPath,
                    reportPath,
                    out existing))
                return existing;

            Directory.CreateDirectory(outputDirectory);
            var suffix = $"{Environment.ProcessId:x}-{Environment.CurrentManagedThreadId:x}-" +
                         $"{Environment.TickCount64:x}-{Interlocked.Increment(ref s_tempSequence):x}";
            var temporaryRecipe = Path.Combine(outputDirectory, $".resource_recipe.{suffix}.tmp");
            var temporaryIrStage = Path.Combine(outputDirectory, $".resource_ir_stage.{suffix}.tmp");
            var temporaryResourceIr = Path.Combine(temporaryIrStage, "resource_ir.bin");
            var temporaryPayloadDirectory = Path.Combine(temporaryIrStage, "resource_ir_blobs");
            var temporaryCompilerMarker = Path.Combine(
                outputDirectory,
                $".resource_ir_compiler.{suffix}.tmp");
            var temporaryReport = Path.Combine(outputDirectory, $".resource_report.{suffix}.tmp");
            try
            {
                Directory.CreateDirectory(temporaryIrStage);
                var report = ResourceCompiler.CompileModFolder(manifest.Id, manifest.FolderPath);
                cancellationToken.ThrowIfCancellationRequested();

                File.WriteAllText(
                    temporaryReport,
                    ResourceCompiler.ToJson(report),
                    new UTF8Encoding(false));
                ResourceRecipeBinary.Write(temporaryRecipe, report);
                var resourceIr = ResourceIrCompiler.Build(
                    report,
                    manifest.FolderPath,
                    temporaryPayloadDirectory);
                ResourceIrBinary.Write(temporaryResourceIr, resourceIr);
                if (!ResourceRecipeBinary.TryValidate(temporaryRecipe, out var binaryError))
                    throw new InvalidDataException(
                        "Generated resource recipe failed binary validation: " +
                        (binaryError ?? "unknown"));
                if (!PcCompatResourceRecipe.TryRead(temporaryRecipe, out var document, out var readError))
                    throw new InvalidDataException(
                        "Generated resource recipe failed runtime parsing: " +
                        (readError ?? "unknown"));
                if (!PcCompatResourceRecipe.TryValidateDocument(document, manifest.Id, out var identityError))
                    throw new InvalidDataException(
                        "Generated resource recipe failed identity validation: " +
                        (identityError ?? "unknown"));
                if (!ResourceIrBinary.TryValidate(temporaryResourceIr, out var irBinaryError))
                    throw new InvalidDataException(
                        "Generated resource IR failed binary validation: " +
                        (irBinaryError ?? "unknown"));
                if (!ResourceIrBinary.TryVerifyPayloadFiles(
                        temporaryResourceIr,
                        resourceIr,
                        out var irPayloadError))
                    throw new InvalidDataException(
                        "Generated resource IR payload validation failed: " +
                        (irPayloadError ?? "unknown"));
                string? irReadError;
                string? irRecipeError = null;
                if (!PcCompatResourceIr.TryRead(
                        temporaryResourceIr,
                        manifest.Id,
                        out var runtimeIr,
                        out irReadError) ||
                    !PcCompatResourceIr.TryValidateAgainstRecipe(runtimeIr, document, out irRecipeError))
                {
                    throw new InvalidDataException(
                        "Generated resource IR failed runtime validation: " +
                        (irReadError ?? irRecipeError ?? "unknown"));
                }
                if (!PcCompatResourceIr.TryVerifyPayloadFiles(
                        temporaryResourceIr,
                        runtimeIr,
                        out var runtimePayloadError))
                    throw new InvalidDataException(
                        "Generated resource IR runtime payload validation failed: " +
                        (runtimePayloadError ?? "unknown"));

                cancellationToken.ThrowIfCancellationRequested();
                File.WriteAllText(
                    temporaryCompilerMarker,
                    ResourceIrCompiler.CompilerRevision + "\n",
                    new UTF8Encoding(false));
                File.Move(temporaryReport, reportPath, overwrite: true);
                File.Move(temporaryRecipe, recipePath, overwrite: true);
                if (Directory.Exists(resourceIrPayloadDirectory))
                    Directory.Delete(resourceIrPayloadDirectory, recursive: true);
                Directory.Move(temporaryPayloadDirectory, resourceIrPayloadDirectory);
                File.Move(temporaryResourceIr, resourceIrPath, overwrite: true);
                File.Move(temporaryCompilerMarker, compilerMarkerPath, overwrite: true);
                return BuildInfo(recipePath, resourceIrPath, reportPath, document, runtimeIr, cacheHit: false);
            }
            finally
            {
                if (File.Exists(temporaryRecipe))
                    File.Delete(temporaryRecipe);
                if (Directory.Exists(temporaryIrStage))
                    Directory.Delete(temporaryIrStage, recursive: true);
                if (File.Exists(temporaryReport))
                    File.Delete(temporaryReport);
                if (File.Exists(temporaryCompilerMarker))
                    File.Delete(temporaryCompilerMarker);
            }
        }
        finally
        {
            CompileGate.Release();
        }
    }

    private static bool TryReadExisting(
        PcModManifest manifest,
        string recipePath,
        string resourceIrPath,
        string compilerMarkerPath,
        string reportPath,
        out PcCompatResourceCompileInfo info)
    {
        info = null!;
        if (!HasCurrentCompilerMarker(compilerMarkerPath) ||
            !File.Exists(recipePath) || !File.Exists(resourceIrPath) ||
            !ResourceRecipeBinary.TryValidate(recipePath, out _) ||
            !PcCompatResourceRecipe.TryRead(recipePath, out var document, out _) ||
            !PcCompatResourceRecipe.TryValidateDocument(document, manifest.Id, out _) ||
            !ResourceIrBinary.TryValidate(resourceIrPath, out _) ||
            !PcCompatResourceIr.TryRead(resourceIrPath, manifest.Id, out var resourceIr, out _) ||
            !PcCompatResourceIr.TryValidateAgainstRecipe(resourceIr, document, out _) ||
            !ResourceIrBinary.TryRead(resourceIrPath, out var importIr, out _) ||
            !ResourceIrBinary.TryVerifyPayloadFiles(resourceIrPath, importIr, out _) ||
            !PcCompatResourceIr.TryVerifyPayloadFiles(resourceIrPath, resourceIr, out _))
        {
            return false;
        }

        foreach (var candidate in document.Candidates
                     .GroupBy(item => item.Sha256Hex, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            var candidatePath = PcCompatResourceRecipeRuntime.ResolveCandidatePathOnDisk(
                manifest.FolderPath,
                candidate);
            if (string.IsNullOrWhiteSpace(candidatePath) ||
                !PcCompatResourceRecipe.TryVerifyCandidateFile(
                    candidatePath,
                    candidate.Sha256Hex,
                    candidate.FileSize,
                    out _))
            {
                return false;
            }
        }

        info = BuildInfo(recipePath, resourceIrPath, reportPath, document, resourceIr, cacheHit: true);
        return true;
    }

    private static bool HasCurrentCompilerMarker(string path)
    {
        try
        {
            return File.Exists(path) &&
                   File.ReadAllText(path).Trim().Equals(
                       ResourceIrCompiler.CompilerRevision,
                       StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static PcCompatResourceCompileInfo BuildInfo(
        string recipePath,
        string resourceIrPath,
        string reportPath,
        PcCompatResourceRecipeDocument document,
        PcCompatResourceIrDocument resourceIr,
        bool cacheHit)
        => new()
        {
            RecipePath = recipePath,
            ResourceIrPath = resourceIrPath,
            ResourceIrPayloadDirectory = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(resourceIrPath))!,
                "resource_ir_blobs"),
            ReportPath = reportPath,
            Compatibility = document.Compatibility,
            CacheHit = cacheHit,
            CandidateCount = document.Candidates.Count,
            FeatureGroupCount = document.FeatureGroups.Count,
            BindingCount = document.Bindings.Count,
            IrBundleCount = resourceIr.Bundles.Count,
            IrAssetCount = resourceIr.Assets.Count,
            IrRequiredAssetCount = resourceIr.Assets.Count(asset => asset.RequiredByMod)
        };
}

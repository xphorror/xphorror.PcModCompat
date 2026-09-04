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
        var output = PcCompatResourceArtifactSet.ForDirectory(
            Path.Combine(manifest.FolderPath, ".pccompat"));
        var inputFingerprint = PcCompatResourceCompileCache.ComputeInputFingerprint(
            manifest,
            cancellationToken);

        if (TryReadExisting(manifest, output, inputFingerprint, out var existing))
        {
            TryPublishStableCache(manifest, inputFingerprint, output, cancellationToken);
            return existing;
        }

        CompileGate.Wait(cancellationToken);
        try
        {
            inputFingerprint = PcCompatResourceCompileCache.ComputeInputFingerprint(
                manifest,
                cancellationToken);
            if (TryReadExisting(manifest, output, inputFingerprint, out existing))
            {
                TryPublishStableCache(manifest, inputFingerprint, output, cancellationToken);
                return existing;
            }

            var cached = PcCompatResourceCompileCache.GetEntry(manifest, inputFingerprint);
            if (PcCompatResourceCompileCache.IsStructurallyComplete(cached, inputFingerprint))
            {
                var stableCacheValid = TryReadExisting(
                    manifest,
                    cached,
                    inputFingerprint,
                    out _);
                if (stableCacheValid)
                {
                    try
                    {
                        PcCompatResourceCompileCache.Restore(cached, output, cancellationToken);
                        if (TryReadExisting(manifest, output, inputFingerprint, out existing))
                        {
                            TryTouchAndPruneStableCache(manifest, cached);
                            return existing;
                        }
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                        _ = exception;
                    }
                }
                else
                {
                    PcCompatResourceCompileCache.Invalidate(cached);
                }
            }

            Directory.CreateDirectory(output.Directory);
            var suffix = $"{Environment.ProcessId:x}-{Environment.CurrentManagedThreadId:x}-" +
                         $"{Environment.TickCount64:x}-{Interlocked.Increment(ref s_tempSequence):x}";
            var temporaryRecipe = Path.Combine(output.Directory, $".resource_recipe.{suffix}.tmp");
            var temporaryIrStage = Path.Combine(output.Directory, $".resource_ir_stage.{suffix}.tmp");
            var temporaryResourceIr = Path.Combine(temporaryIrStage, "resource_ir.bin");
            var temporaryPayloadDirectory = Path.Combine(temporaryIrStage, "resource_ir_blobs");
            var temporaryCompilerMarker = Path.Combine(
                output.Directory,
                $".resource_ir_compiler.{suffix}.tmp");
            var temporaryReport = Path.Combine(output.Directory, $".resource_report.{suffix}.tmp");
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
                var completedFingerprint = PcCompatResourceCompileCache.ComputeInputFingerprint(
                    manifest,
                    cancellationToken);
                if (!completedFingerprint.Equals(inputFingerprint, StringComparison.Ordinal))
                    throw new IOException("PC MOD resource inputs changed while they were being compiled.");
                File.WriteAllText(
                    temporaryCompilerMarker,
                    PcCompatResourceCompileCache.BuildCompilerMarker(inputFingerprint),
                    new UTF8Encoding(false));
                File.Move(temporaryReport, output.ReportPath, overwrite: true);
                File.Move(temporaryRecipe, output.RecipePath, overwrite: true);
                if (Directory.Exists(output.PayloadDirectory))
                    Directory.Delete(output.PayloadDirectory, recursive: true);
                Directory.Move(temporaryPayloadDirectory, output.PayloadDirectory);
                File.Move(temporaryResourceIr, output.ResourceIrPath, overwrite: true);
                File.Move(temporaryCompilerMarker, output.CompilerMarkerPath, overwrite: true);

                if (!TryReadExisting(manifest, output, inputFingerprint, out _))
                    throw new InvalidDataException("Published resource compile output failed validation.");
                TryPublishStableCache(manifest, inputFingerprint, output, cancellationToken);
                return BuildInfo(
                    output.RecipePath,
                    output.ResourceIrPath,
                    output.ReportPath,
                    document,
                    runtimeIr,
                    cacheHit: false);
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
        PcCompatResourceArtifactSet artifacts,
        string inputFingerprint,
        out PcCompatResourceCompileInfo info)
    {
        info = null!;
        if (!HasCurrentCompilerMarker(artifacts.CompilerMarkerPath, inputFingerprint) ||
            !File.Exists(artifacts.ReportPath) ||
            !File.Exists(artifacts.RecipePath) || !File.Exists(artifacts.ResourceIrPath) ||
            !ResourceRecipeBinary.TryValidate(artifacts.RecipePath, out _) ||
            !PcCompatResourceRecipe.TryRead(artifacts.RecipePath, out var document, out _) ||
            !PcCompatResourceRecipe.TryValidateDocument(document, manifest.Id, out _) ||
            !ResourceIrBinary.TryValidate(artifacts.ResourceIrPath, out _) ||
            !PcCompatResourceIr.TryRead(artifacts.ResourceIrPath, manifest.Id, out var resourceIr, out _) ||
            !PcCompatResourceIr.TryValidateAgainstRecipe(resourceIr, document, out _) ||
            !ResourceIrBinary.TryRead(artifacts.ResourceIrPath, out var importIr, out _) ||
            !ResourceIrBinary.TryVerifyPayloadFiles(artifacts.ResourceIrPath, importIr, out _) ||
            !PcCompatResourceIr.TryVerifyPayloadFiles(artifacts.ResourceIrPath, resourceIr, out _))
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

        info = BuildInfo(
            artifacts.RecipePath,
            artifacts.ResourceIrPath,
            artifacts.ReportPath,
            document,
            resourceIr,
            cacheHit: true);
        return true;
    }

    private static bool HasCurrentCompilerMarker(string path, string inputFingerprint)
    {
        try
        {
            return File.Exists(path) &&
                   File.ReadAllText(path).Equals(
                       PcCompatResourceCompileCache.BuildCompilerMarker(inputFingerprint),
                       StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void TryPublishStableCache(
        PcModManifest manifest,
        string inputFingerprint,
        PcCompatResourceArtifactSet source,
        CancellationToken cancellationToken)
    {
        try
        {
            PcCompatResourceCompileCache.Publish(
                manifest,
                inputFingerprint,
                source,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // The stable cache is an optimization. The validated per-import output remains usable.
            _ = exception;
        }
    }

    private static void TryTouchAndPruneStableCache(
        PcModManifest manifest,
        PcCompatResourceArtifactSet cached)
    {
        try
        {
            Directory.SetLastWriteTimeUtc(cached.Directory, DateTime.UtcNow);
            PcCompatResourceCompileCache.Prune(manifest, cached.Directory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            _ = exception;
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

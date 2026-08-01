using StArray.ModManager.Manager;

namespace Xphorror.PcModCompat;

/// <summary>
/// Session facade for verified resource recipes. Builds a portable load plan and
/// optionally delegates UnityMain AssetBundle loading to a registered host sink.
/// AssetsTools.NET is never linked into this assembly.
/// </summary>
public static class PcCompatResourceRecipeRuntime
{
    private const string LogTag = "PcCompatResources";
    private static readonly object Gate = new();
    private static readonly Dictionary<string, PcCompatResourceRecipeDocument> Recipes =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PcCompatResourceSessionPlan> Plans =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Dictionary<string, PcCompatResourceLoadResult>> LoadResults =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Dictionary<string, PcCompatResourceLoadAuthorization>> LoadAuthorizations =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> SessionGenerations = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LoadsInProgress = new(StringComparer.OrdinalIgnoreCase);

    // Host-provided scheduler. It may complete immediately on UnityMain or return
    // Pending after queueing work for the proven UnityMain presentation hook.
    private sealed record BundleSinkRegistration(
        Func<PcCompatResourceLoadRequest, PcCompatResourceLoadResult> Load,
        Action<PcCompatResourceUnloadRequest> Unload);

    private static BundleSinkRegistration? s_bundleSink;
    private static Action? s_resourceConsumerRefreshSink;

    /// <summary>
    /// Explicit production gate for AssetBundle loads. Default off so RegisterMod,
    /// diagnostics, and accidental callers cannot change HUD/resource visuals.
    /// Set STARRAY_PCMOD_RESOURCE_LOAD=1 only when a consumer intentionally needs LoadFromFile.
    /// </summary>
    public const string RuntimeLoadEnvironmentVariable = "STARRAY_PCMOD_RESOURCE_LOAD";

    public static void RegisterBundleLoadSink(
        Func<string, string, string, PcCompatResourceLoadResult> loadSink,
        Action<string, string> unloadSink)
    {
        ArgumentNullException.ThrowIfNull(loadSink);
        ArgumentNullException.ThrowIfNull(unloadSink);
        RegisterBundleLoadSink(
            request => loadSink(request.ModId, request.CandidateSha256Hex, request.Path),
            request => unloadSink(request.ModId, request.CandidateSha256Hex));
    }

    public static void RegisterBundleLoadSink(
        Func<PcCompatResourceLoadRequest, PcCompatResourceLoadResult> loadSink,
        Action<PcCompatResourceUnloadRequest> unloadSink)
    {
        ArgumentNullException.ThrowIfNull(loadSink);
        ArgumentNullException.ThrowIfNull(unloadSink);
        Volatile.Write(ref s_bundleSink, new BundleSinkRegistration(loadSink, unloadSink));
        Logger.Info(
            LogTag,
            "UnityMain AssetBundle scheduler registered; runtime load enabled=" +
            (IsRuntimeLoadEnabled() ? "1" : "0"));
    }

    public static void ClearBundleLoadSink()
    {
        Volatile.Write(ref s_bundleSink, null);
    }

    public static void RegisterResourceConsumerRefreshSink(Action refreshSink)
    {
        ArgumentNullException.ThrowIfNull(refreshSink);
        Volatile.Write(ref s_resourceConsumerRefreshSink, refreshSink);
    }

    public static void ClearResourceConsumerRefreshSink()
    {
        Volatile.Write(ref s_resourceConsumerRefreshSink, null);
    }

    public static bool IsRuntimeLoadEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RuntimeLoadEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLoadSinkRegistered()
        => Volatile.Read(ref s_bundleSink) != null;

    public static bool TryLoadForMod(PcModManifest manifest, string? recipePath = null, string? compiledResourcesDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var path = recipePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(manifest.FolderPath, ".pccompat", "resource_recipe.bin");
        }
        if (!File.Exists(path))
            return false;

        if (!PcCompatResourceRecipe.TryRead(path, out var document, out var error))
        {
            Logger.Warn(LogTag, $"resource recipe rejected mod={manifest.Id} path={path} error={error}");
            return false;
        }

        if (!PcCompatResourceRecipe.TryValidateDocument(document, manifest.Id, out error))
        {
            Logger.Warn(LogTag, $"resource recipe identity/shape rejected mod={manifest.Id} path={path} error={error}");
            return false;
        }

        var plan = BuildSessionPlan(manifest, document, compiledResourcesDirectory);
        PcCompatResourceUnloadRequest[] previouslyLoaded;
        lock (Gate)
        {
            var previousGeneration = SessionGenerations.GetValueOrDefault(manifest.Id);
            previouslyLoaded = LoadResults.TryGetValue(manifest.Id, out var existingResults)
                ? existingResults.Values.Where(result => result.Success)
                    .GroupBy(result => result.CandidateSha256Hex, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new PcCompatResourceUnloadRequest
                    {
                        ModId = manifest.Id,
                        CandidateSha256Hex = group.Key,
                        SessionGeneration = ResolveResultGeneration(group, previousGeneration)
                    })
                    .ToArray()
                : Array.Empty<PcCompatResourceUnloadRequest>();
            Recipes[manifest.Id] = document;
            Plans[manifest.Id] = plan;
            LoadResults[manifest.Id] = new Dictionary<string, PcCompatResourceLoadResult>(StringComparer.OrdinalIgnoreCase);
            LoadAuthorizations[manifest.Id] = new Dictionary<string, PcCompatResourceLoadAuthorization>(StringComparer.OrdinalIgnoreCase);
            SessionGenerations[manifest.Id] = previousGeneration + 1;
            RemoveInProgressForModLocked(manifest.Id);
        }

        UnloadCandidates(previouslyLoaded);
        NotifyResourceConsumers();

        Logger.Info(
            LogTag,
            $"resource recipe loaded mod={manifest.Id} compatibility={document.Compatibility} " +
            $"groups={document.FeatureGroups.Count} bindings={document.Bindings.Count} " +
            $"candidates={document.Candidates.Count} autoLoadReady={plan.Candidates.Count(c => c.AutoLoadAllowed && c.Status == PcCompatResourceCandidateStatus.Ready)}");
        return true;
    }

    public static void Unload(string modId)
    {
        PcCompatResourceUnloadRequest[] loadedCandidates;
        lock (Gate)
        {
            var currentGeneration = SessionGenerations.GetValueOrDefault(modId);
            Recipes.Remove(modId);
            Plans.Remove(modId);
            loadedCandidates = LoadResults.TryGetValue(modId, out var results)
                ? results.Values.Where(result => result.Success)
                    .GroupBy(result => result.CandidateSha256Hex, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new PcCompatResourceUnloadRequest
                    {
                        ModId = modId,
                        CandidateSha256Hex = group.Key,
                        SessionGeneration = ResolveResultGeneration(group, currentGeneration)
                    })
                    .ToArray()
                : Array.Empty<PcCompatResourceUnloadRequest>();
            LoadResults.Remove(modId);
            LoadAuthorizations.Remove(modId);
            SessionGenerations[modId] = currentGeneration + 1;
            RemoveInProgressForModLocked(modId);
        }

        UnloadCandidates(loadedCandidates);
    }

    public static PcCompatResourceRecipeDocument? Get(string modId)
    {
        lock (Gate)
            return Recipes.TryGetValue(modId, out var document) ? document : null;
    }

    public static PcCompatResourceSessionPlan? GetPlan(string modId)
    {
        lock (Gate)
            return Plans.TryGetValue(modId, out var plan) ? plan : null;
    }

    public static bool TryGetSessionGeneration(string modId, out long generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        lock (Gate)
            return SessionGenerations.TryGetValue(modId, out generation) &&
                   Plans.ContainsKey(modId);
    }

    public static IReadOnlyList<PcCompatResourceRecipeDocument> Snapshot()
    {
        lock (Gate)
            return Recipes.Values.ToArray();
    }

    public static IReadOnlyList<PcCompatResourceSessionPlan> SnapshotPlans()
    {
        lock (Gate)
            return Plans.Values.ToArray();
    }

    public static PcCompatResourceReadinessSummary GetReadinessSummary(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        lock (Gate)
        {
            Recipes.TryGetValue(modId, out var document);
            Plans.TryGetValue(modId, out var plan);
            return new PcCompatResourceReadinessSummary
            {
                ModId = modId,
                RecipeLoaded = document != null,
                Compatibility = document?.Compatibility ?? plan?.Compatibility ?? "none",
                RuntimeLoadEnabled = IsRuntimeLoadEnabled(),
                LoadSinkRegistered = IsLoadSinkRegistered(),
                FeatureGroupCount = plan?.FeatureGroups.Count ?? 0,
                CandidateCount = plan?.Candidates.Count ?? 0,
                ReadyCandidateCount = plan?.Candidates.Count(candidate =>
                    candidate.AutoLoadAllowed &&
                    candidate.Status == PcCompatResourceCandidateStatus.Ready) ?? 0,
                ControlledCandidateCount = plan?.Candidates.Count(candidate =>
                    candidate.Status == PcCompatResourceCandidateStatus.Controlled) ?? 0,
                QueuedCandidateCount = plan?.Candidates.Count(candidate =>
                    candidate.Status == PcCompatResourceCandidateStatus.LoadQueued) ?? 0,
                LoadedCandidateCount = plan?.Candidates.Count(candidate =>
                    candidate.Status == PcCompatResourceCandidateStatus.Loaded) ?? 0,
                RejectedCandidateCount = plan?.Candidates.Count(candidate =>
                    candidate.Status == PcCompatResourceCandidateStatus.Rejected) ?? 0,
                MissingCandidateCount = plan?.Candidates.Count(candidate =>
                    candidate.Status == PcCompatResourceCandidateStatus.Missing) ?? 0,
                FeatureGroupIds = plan?.FeatureGroups.Select(group => group.Id).ToArray()
                    ?? Array.Empty<string>(),
                CompiledResourcesDirectory = plan?.CompiledResourcesDirectory
            };
        }
    }

    public static string? ResolveSelectedCandidatePath(PcCompatResourceRecipeDocument document, string featureGroupId)
    {
        ArgumentNullException.ThrowIfNull(document);
        var group = document.FeatureGroups.FirstOrDefault(item =>
            item.Id.Equals(featureGroupId, StringComparison.OrdinalIgnoreCase));
        if (group == null)
            return null;

        lock (Gate)
        {
            if (Plans.TryGetValue(document.ModId, out var plan))
            {
                var planned = plan.Candidates.FirstOrDefault(item =>
                    item.Sha256Hex.Equals(group.SelectedCandidateSha256Hex, StringComparison.OrdinalIgnoreCase));
                if (planned != null && !string.IsNullOrWhiteSpace(planned.ResolvedPath))
                    return planned.ResolvedPath;
            }
        }

        var candidate = document.Candidates.FirstOrDefault(item =>
            item.Sha256Hex.Equals(group.SelectedCandidateSha256Hex, StringComparison.OrdinalIgnoreCase));
        return candidate?.SourcePath;
    }

    /// <summary>
    /// Resolves one unambiguous, high-confidence binding whose selected bundle
    /// has completed loading in the current resource session. Semantic/fuzzy
    /// matches are deliberately excluded from automatic runtime consumption.
    /// </summary>
    public static bool TryResolveLoadedBinding(
        string modId,
        string featureGroupId,
        string? expectedType,
        out PcCompatResolvedResourceBinding binding)
        => TryResolveLoadedBinding(
            modId,
            featureGroupId,
            assetName: null,
            expectedType,
            out binding);

    public static bool TryResolveLoadedBinding(
        string modId,
        string featureGroupId,
        string? assetName,
        string? expectedType,
        out PcCompatResolvedResourceBinding binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentException.ThrowIfNullOrWhiteSpace(featureGroupId);

        lock (Gate)
        {
            binding = null!;
            if (!Recipes.TryGetValue(modId, out var document) ||
                !Plans.TryGetValue(modId, out var plan) ||
                !SessionGenerations.TryGetValue(modId, out var generation) ||
                !LoadResults.TryGetValue(modId, out var results))
                return false;

            var group = plan.FeatureGroups.FirstOrDefault(item =>
                item.Id.Equals(featureGroupId, StringComparison.OrdinalIgnoreCase));
            if (group == null ||
                !results.TryGetValue(group.SelectedCandidateSha256Hex, out var loadResult) ||
                !loadResult.Success ||
                loadResult.SessionGeneration != generation)
                return false;

            var matches = document.Bindings.Where(candidate =>
                    candidate.FeatureGroupId.Equals(group.Id, StringComparison.OrdinalIgnoreCase) &&
                    IsRuntimeConsumableConfidence(candidate.Confidence) &&
                    (string.IsNullOrWhiteSpace(assetName) ||
                     candidate.AssetName.Equals(assetName, StringComparison.Ordinal)) &&
                    (string.IsNullOrWhiteSpace(expectedType) ||
                     ResourceTypeMatches(candidate.ExpectedType, expectedType)))
                .Take(2)
                .ToArray();
            if (matches.Length != 1)
                return false;

            var match = matches[0];
            binding = new PcCompatResolvedResourceBinding
            {
                ModId = modId,
                FeatureGroupId = group.Id,
                CandidateSha256Hex = group.SelectedCandidateSha256Hex,
                AssetName = match.AssetName,
                ExpectedType = match.ExpectedType,
                Confidence = match.Confidence,
                SessionGeneration = generation
            };
            return true;
        }
    }

    public static bool TryAuthorizeCandidateLoad(
        string modId,
        string candidateSha256Hex,
        PcCompatResourceLoadAuthorization authorization,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateSha256Hex);
        lock (Gate)
        {
            if (!Plans.TryGetValue(modId, out var plan))
            {
                error = "resource session plan is not loaded";
                return false;
            }

            var candidate = plan.Candidates.FirstOrDefault(item =>
                item.Sha256Hex.Equals(candidateSha256Hex, StringComparison.OrdinalIgnoreCase));
            if (candidate == null)
            {
                error = "candidate is not part of the session plan";
                return false;
            }

            var required = RequiredAuthorization(candidate);
            if (required == PcCompatResourceLoadAuthorization.None)
            {
                if (!candidate.AutoLoadAllowed)
                {
                    error = candidate.StatusReason ?? "candidate policy rejects runtime loading";
                    return false;
                }
                error = null;
                return true;
            }
            if (authorization < required)
            {
                error = required == PcCompatResourceLoadAuthorization.Forced
                    ? "candidate requires explicit forced-load confirmation"
                    : "candidate requires explicit controlled-load confirmation";
                return false;
            }

            if (!LoadAuthorizations.TryGetValue(modId, out var authorizations))
            {
                authorizations = new Dictionary<string, PcCompatResourceLoadAuthorization>(StringComparer.OrdinalIgnoreCase);
                LoadAuthorizations[modId] = authorizations;
            }
            authorizations[candidateSha256Hex] = authorization;
            error = null;
            return true;
        }
    }

    /// <summary>
    /// On-demand load of one candidate. Never called automatically by RegisterMod.
    /// Controlled and forced candidates require an authorization bound to the current session.
    /// The host may return Pending after queueing the actual Unity call for UnityMain.
    /// </summary>
    public static PcCompatResourceLoadResult TryEnsureCandidateLoaded(string modId, string candidateSha256Hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateSha256Hex);

        if (!IsRuntimeLoadEnabled())
        {
            return Fail(
                modId,
                candidateSha256Hex,
                string.Empty,
                RuntimeLoadEnvironmentVariable + " is not enabled; AssetBundle load remains opt-in");
        }

        PcCompatResourceSessionCandidate? candidate;
        PcCompatResourceLoadResult? cachedResult = null;
        long generation;
        string inProgressKey;
        lock (Gate)
        {
            if (!Plans.TryGetValue(modId, out var plan))
                return Fail(modId, candidateSha256Hex, string.Empty, "resource session plan is not loaded");

            generation = SessionGenerations.GetValueOrDefault(modId);
            inProgressKey = MakeInProgressKey(modId, candidateSha256Hex, generation);
            candidate = plan.Candidates.FirstOrDefault(item =>
                item.Sha256Hex.Equals(candidateSha256Hex, StringComparison.OrdinalIgnoreCase));
            if (LoadResults.TryGetValue(modId, out var existingResults) &&
                existingResults.TryGetValue(candidateSha256Hex, out var cached))
            {
                cachedResult = AsCacheHit(cached);
            }
            else if (LoadsInProgress.Contains(inProgressKey))
            {
                return Pending(
                    modId,
                    candidateSha256Hex,
                    candidate?.ResolvedPath ?? string.Empty,
                    generation,
                    cacheHit: true);
            }
            else
            {
                LoadsInProgress.Add(inProgressKey);
            }
        }

        if (cachedResult != null)
        {
            if (cachedResult.Success)
                NotifyResourceConsumers();
            return cachedResult;
        }

        PcCompatResourceLoadResult result;
        var submittedToSink = false;
        var request = new PcCompatResourceLoadRequest
        {
            ModId = modId,
            CandidateSha256Hex = candidateSha256Hex,
            Path = candidate?.ResolvedPath ?? string.Empty,
            ExpectedFileSize = candidate?.ExpectedFileSize ?? 0,
            SessionGeneration = generation
        };
        try
        {
            if (candidate == null)
                result = Fail(modId, candidateSha256Hex, string.Empty, "candidate is not part of the session plan");
            else if (!IsCandidateAuthorized(modId, candidate, out var authorizationError))
                result = Fail(modId, candidateSha256Hex, candidate.ResolvedPath, authorizationError);
            else if (candidate.Status is PcCompatResourceCandidateStatus.Missing or PcCompatResourceCandidateStatus.Rejected)
                result = Fail(modId, candidateSha256Hex, candidate.ResolvedPath, candidate.StatusReason ?? candidate.Status.ToString());
            else if (!PcCompatResourceRecipe.TryVerifyCandidateFile(
                         candidate.ResolvedPath,
                         candidate.Sha256Hex,
                         candidate.ExpectedFileSize,
                         out var verifyError))
                result = Fail(modId, candidateSha256Hex, candidate.ResolvedPath, verifyError ?? "candidate verification failed");
            else
            {
                var sink = Volatile.Read(ref s_bundleSink);
                if (sink == null)
                {
                    result = Fail(modId, candidateSha256Hex, candidate.ResolvedPath, "UnityMain AssetBundle load scheduler is not registered");
                }
                else
                {
                    submittedToSink = true;
                    result = NormalizeLoadResult(sink.Load(request), request);
                }
            }
        }
        catch (Exception ex)
        {
            result = Fail(
                modId,
                candidateSha256Hex,
                candidate?.ResolvedPath ?? string.Empty,
                ex.GetType().Name + ": " + ex.Message);
        }

        if (!submittedToSink || result.Retryable)
        {
            lock (Gate)
                LoadsInProgress.Remove(inProgressKey);
            return result;
        }

        if (result.Pending)
        {
            lock (Gate)
            {
                if (SessionGenerations.GetValueOrDefault(modId) != generation || !Plans.ContainsKey(modId))
                    return Fail(modId, candidateSha256Hex, result.Path, "resource session changed while load was queued");
                UpdateCandidateStatusLocked(
                    modId,
                    candidateSha256Hex,
                    PcCompatResourceCandidateStatus.LoadQueued,
                    "waiting for UnityMain AssetBundle load");
            }
            return result;
        }

        if (!CompleteBundleLoad(request, result))
            return Fail(modId, candidateSha256Hex, result.Path, "resource session changed while candidate was loading");
        return result;
    }

    public static bool CompleteBundleLoad(
        PcCompatResourceLoadRequest request,
        PcCompatResourceLoadResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        var normalized = NormalizeLoadResult(result, request);
        if (normalized.Pending)
            throw new ArgumentException("a completion result cannot still be pending", nameof(result));

        var inProgressKey = MakeInProgressKey(
            request.ModId,
            request.CandidateSha256Hex,
            request.SessionGeneration);
        var staleSession = false;
        lock (Gate)
        {
            LoadsInProgress.Remove(inProgressKey);
            staleSession = !SessionGenerations.TryGetValue(request.ModId, out var currentGeneration) ||
                           currentGeneration != request.SessionGeneration ||
                           !Plans.ContainsKey(request.ModId);
            if (!staleSession)
            {
                if (!LoadResults.TryGetValue(request.ModId, out var results))
                {
                    results = new Dictionary<string, PcCompatResourceLoadResult>(StringComparer.OrdinalIgnoreCase);
                    LoadResults[request.ModId] = results;
                }
                results[request.CandidateSha256Hex] = normalized;
                UpdateCandidateStatusLocked(
                    request.ModId,
                    request.CandidateSha256Hex,
                    normalized.Success
                        ? PcCompatResourceCandidateStatus.Loaded
                        : PcCompatResourceCandidateStatus.LoadFailed,
                    normalized.Success ? null : normalized.Error);
            }
        }

        if (staleSession && normalized.Success)
        {
            UnloadCandidates(
            [
                new PcCompatResourceUnloadRequest
                {
                    ModId = request.ModId,
                    CandidateSha256Hex = request.CandidateSha256Hex,
                    SessionGeneration = request.SessionGeneration
                }
            ]);
        }
        else if (!staleSession && normalized.Success)
        {
            NotifyResourceConsumers();
        }
        return !staleSession;
    }

    public static PcCompatResourceLoadResult TryEnsureFeatureGroupLoaded(string modId, string featureGroupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentException.ThrowIfNullOrWhiteSpace(featureGroupId);
        string candidateSha;
        lock (Gate)
        {
            if (!Plans.TryGetValue(modId, out var plan))
                return Fail(modId, string.Empty, string.Empty, "resource session plan is not loaded");
            var group = plan.FeatureGroups.FirstOrDefault(item =>
                item.Id.Equals(featureGroupId, StringComparison.OrdinalIgnoreCase));
            if (group == null)
                return Fail(modId, string.Empty, string.Empty, "feature group is not present in the session plan");
            candidateSha = group.SelectedCandidateSha256Hex;
        }
        return TryEnsureCandidateLoaded(modId, candidateSha);
    }

    public static PcCompatResourceSessionPlan BuildSessionPlan(
        PcModManifest manifest,
        PcCompatResourceRecipeDocument document,
        string? compiledResourcesDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(document);
        if (!PcCompatResourceRecipe.TryValidateDocument(document, manifest.Id, out var validationError))
            throw new InvalidDataException("Invalid resource recipe document: " + validationError);
        var modFolder = Path.GetFullPath(manifest.FolderPath);
        var candidates = document.Candidates
            .Select(candidate => ToSessionCandidate(modFolder, candidate, compiledResourcesDirectory))
            .OrderBy(candidate => candidate.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var groups = document.FeatureGroups
            .Select(group => new PcCompatResourceFeatureGroupPlan
            {
                Id = group.Id,
                DisplayName = group.DisplayName,
                SelectedCandidateSha256Hex = group.SelectedCandidateSha256Hex,
                AssetNames = group.AssetNames,
                LoadPolicy = group.LoadPolicy
            })
            .OrderBy(group => group.Id, StringComparer.Ordinal)
            .ToArray();
        return new PcCompatResourceSessionPlan
        {
            ModId = document.ModId,
            ModFolder = modFolder,
            CompiledResourcesDirectory = string.IsNullOrWhiteSpace(compiledResourcesDirectory)
                ? null
                : Path.GetFullPath(compiledResourcesDirectory),
            Compatibility = document.Compatibility,
            Candidates = candidates,
            FeatureGroups = groups
        };
    }

    public static string? ResolveCandidatePathOnDisk(
        string modFolder,
        PcCompatResourceCandidate candidate,
        string? compiledResourcesDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);
        ArgumentNullException.ThrowIfNull(candidate);

        if (!PcCompatResourceRecipe.IsSha256(candidate.Sha256Hex))
            return null;
        var modRoot = Path.GetFullPath(modFolder);

        // Prefer the atomically published compiled resources/ copy when available.
        if (!string.IsNullOrWhiteSpace(compiledResourcesDirectory) &&
            Directory.Exists(compiledResourcesDirectory) &&
            !string.IsNullOrWhiteSpace(candidate.Sha256Hex))
        {
            var compiledRoot = Path.GetFullPath(compiledResourcesDirectory);
            var normalizedSha = candidate.Sha256Hex.ToLowerInvariant();
            try
            {
                var match = Directory.EnumerateFiles(compiledRoot, normalizedSha + "_*")
                    .Concat(Directory.EnumerateFiles(compiledRoot, normalizedSha[..16] + "_*"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(Path.GetFullPath)
                    .FirstOrDefault(path => IsPathWithinRoot(path, compiledRoot) && File.Exists(path));
                if (!string.IsNullOrWhiteSpace(match))
                    return match;
            }
            catch
            {
            }
        }

        if (!string.IsNullOrWhiteSpace(candidate.SourcePath))
        {
            try
            {
                var source = Path.GetFullPath(candidate.SourcePath);
                if (IsPathWithinRoot(source, modRoot) && File.Exists(source))
                    return source;
            }
            catch
            {
            }
        }

        var fileName = string.IsNullOrWhiteSpace(candidate.FileName)
            ? Path.GetFileName(candidate.SourcePath)
            : candidate.FileName;
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        foreach (var path in EnumerateCandidatePaths(modRoot, fileName, candidate.PlatformHint))
        {
            var fullPath = Path.GetFullPath(path);
            if (IsPathWithinRoot(fullPath, modRoot) && File.Exists(fullPath))
                return fullPath;
        }

        return null;
    }

    private static PcCompatResourceSessionCandidate ToSessionCandidate(
        string modFolder,
        PcCompatResourceCandidate candidate,
        string? compiledResourcesDirectory)
    {
        var policy = candidate.LoadPolicy ?? string.Empty;
        var autoLoad = policy.Equals("AutoLoad", StringComparison.OrdinalIgnoreCase) ||
                       policy == "0"; // legacy numeric enum
        var resolved = ResolveCandidatePathOnDisk(modFolder, candidate, compiledResourcesDirectory);
        if (!autoLoad)
        {
            var controlled = policy.Equals("ControlledLoad", StringComparison.OrdinalIgnoreCase) ||
                             policy == "1" ||
                             policy.Equals("ForceRequired", StringComparison.OrdinalIgnoreCase) ||
                             policy == "2";
            return new PcCompatResourceSessionCandidate
            {
                Sha256Hex = candidate.Sha256Hex,
                FileName = candidate.FileName,
                PlatformHint = candidate.PlatformHint,
                LoadPolicy = policy,
                ResolvedPath = resolved ?? string.Empty,
                ExpectedFileSize = candidate.FileSize,
                AutoLoadAllowed = false,
                Status = controlled
                    ? PcCompatResourceCandidateStatus.Controlled
                    : PcCompatResourceCandidateStatus.Rejected,
                StatusReason = controlled
                    ? "candidate requires an explicit controlled/forced trial load"
                    : "load policy rejects runtime loading"
            };
        }

        if (string.IsNullOrWhiteSpace(resolved))
        {
            return new PcCompatResourceSessionCandidate
            {
                Sha256Hex = candidate.Sha256Hex,
                FileName = candidate.FileName,
                PlatformHint = candidate.PlatformHint,
                LoadPolicy = policy,
                ResolvedPath = string.Empty,
                ExpectedFileSize = candidate.FileSize,
                AutoLoadAllowed = true,
                Status = PcCompatResourceCandidateStatus.Missing,
                StatusReason = "candidate file was not found under the MOD folder or compiled resources cache"
            };
        }

        return new PcCompatResourceSessionCandidate
        {
            Sha256Hex = candidate.Sha256Hex,
            FileName = candidate.FileName,
            PlatformHint = candidate.PlatformHint,
            LoadPolicy = policy,
            ResolvedPath = resolved,
            ExpectedFileSize = candidate.FileSize,
            AutoLoadAllowed = true,
            Status = PcCompatResourceCandidateStatus.Ready,
            StatusReason = null
        };
    }

    private static IEnumerable<string> EnumerateCandidatePaths(
        string modFolder,
        string fileName,
        string platformHint)
    {
        var preferred = platformHint switch
        {
            "Android" or "1" => new[] { "Android", "", "Linux", "Windows", "Mac" },
            "Linux" or "2" => new[] { "Linux", "", "Android", "Windows", "Mac" },
            "Windows" or "3" => new[] { "", "Windows", "Linux", "Android", "Mac" },
            "Mac" or "4" => new[] { "Mac", "", "Linux", "Windows", "Android" },
            _ => new[] { "", "Linux", "Android", "Windows", "Mac" }
        };

        foreach (var platform in preferred)
        {
            yield return string.IsNullOrEmpty(platform)
                ? Path.Combine(modFolder, fileName)
                : Path.Combine(modFolder, platform, fileName);
        }
    }

    private static PcCompatResourceLoadResult Fail(
        string modId,
        string sha,
        string path,
        string error)
        => new()
        {
            Success = false,
            ModId = modId,
            CandidateSha256Hex = sha,
            Path = path ?? string.Empty,
            Error = error,
            CacheHit = false,
            Pending = false,
            Retryable = false,
            SessionGeneration = 0
        };

    private static PcCompatResourceLoadResult Pending(
        string modId,
        string sha,
        string path,
        long generation,
        bool cacheHit)
        => new()
        {
            Success = false,
            Pending = true,
            ModId = modId,
            CandidateSha256Hex = sha,
            Path = path ?? string.Empty,
            Error = null,
            CacheHit = cacheHit,
            Retryable = false,
            SessionGeneration = generation
        };

    private static PcCompatResourceLoadResult AsCacheHit(PcCompatResourceLoadResult result)
        => new()
        {
            Success = result.Success,
            ModId = result.ModId,
            CandidateSha256Hex = result.CandidateSha256Hex,
            Path = result.Path,
            Error = result.Error,
            CacheHit = true,
            Pending = result.Pending,
            Retryable = result.Retryable,
            SessionGeneration = result.SessionGeneration
        };

    private static PcCompatResourceLoadResult NormalizeLoadResult(
        PcCompatResourceLoadResult? result,
        PcCompatResourceLoadRequest request)
        => result == null
            ? Fail(request.ModId, request.CandidateSha256Hex, request.Path, "UnityMain AssetBundle load scheduler returned null")
            : result.Pending && result.Success
                ? Fail(request.ModId, request.CandidateSha256Hex, request.Path, "UnityMain AssetBundle load scheduler returned an invalid success+pending result")
            : new PcCompatResourceLoadResult
            {
                Success = result.Success,
                Pending = result.Pending,
                ModId = request.ModId,
                CandidateSha256Hex = request.CandidateSha256Hex,
                Path = request.Path,
                Error = result.Success || result.Pending
                    ? null
                    : result.Error ?? "UnityMain AssetBundle load scheduler failed",
                CacheHit = result.CacheHit,
                Retryable = result.Retryable,
                SessionGeneration = request.SessionGeneration
            };

    private static PcCompatResourceLoadAuthorization RequiredAuthorization(
        PcCompatResourceSessionCandidate candidate)
    {
        if (candidate.AutoLoadAllowed)
            return PcCompatResourceLoadAuthorization.None;
        if (candidate.LoadPolicy.Equals("ControlledLoad", StringComparison.OrdinalIgnoreCase) ||
            candidate.LoadPolicy == "1")
            return PcCompatResourceLoadAuthorization.Controlled;
        if (candidate.LoadPolicy.Equals("ForceRequired", StringComparison.OrdinalIgnoreCase) ||
            candidate.LoadPolicy == "2")
            return PcCompatResourceLoadAuthorization.Forced;
        return PcCompatResourceLoadAuthorization.None;
    }

    private static bool IsCandidateAuthorized(
        string modId,
        PcCompatResourceSessionCandidate candidate,
        out string error)
    {
        var required = RequiredAuthorization(candidate);
        if (required == PcCompatResourceLoadAuthorization.None)
        {
            if (candidate.AutoLoadAllowed)
            {
                error = string.Empty;
                return true;
            }
            error = candidate.StatusReason ?? "candidate policy rejects runtime loading";
            return false;
        }

        lock (Gate)
        {
            if (LoadAuthorizations.TryGetValue(modId, out var authorizations) &&
                authorizations.TryGetValue(candidate.Sha256Hex, out var authorization) &&
                authorization >= required)
            {
                error = string.Empty;
                return true;
            }
        }
        error = required == PcCompatResourceLoadAuthorization.Forced
            ? "candidate requires explicit forced-load confirmation"
            : "candidate requires explicit controlled-load confirmation";
        return false;
    }

    private static string MakeInProgressKey(string modId, string sha, long generation)
        => modId + "\0" + sha + "\0" + generation.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void RemoveInProgressForModLocked(string modId)
    {
        var prefix = modId + "\0";
        LoadsInProgress.RemoveWhere(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static void NotifyResourceConsumers()
    {
        var sink = Volatile.Read(ref s_resourceConsumerRefreshSink);
        if (sink == null)
            return;
        try
        {
            sink();
        }
        catch (Exception ex)
        {
            Logger.Warn(LogTag, "resource consumer refresh request failed: " + ex.Message);
        }
    }

    private static long ResolveResultGeneration(
        IEnumerable<PcCompatResourceLoadResult> results,
        long fallback)
    {
        var generation = results.Max(result => result.SessionGeneration);
        return generation > 0 ? generation : fallback;
    }

    private static bool IsRuntimeConsumableConfidence(string confidence)
        => confidence.Equals("Proven", StringComparison.OrdinalIgnoreCase) ||
           confidence.Equals("UniqueType", StringComparison.OrdinalIgnoreCase) ||
           confidence == "1" ||
           confidence == "2";

    private static bool ResourceTypeMatches(string recipeType, string requestedType)
    {
        static string Normalize(string value)
            => value.Split(',', 2)[0].Trim();
        static string SimpleName(string value)
        {
            var normalized = Normalize(value);
            return normalized[(normalized.LastIndexOf('.') + 1)..];
        }

        var recipe = Normalize(recipeType);
        var requested = Normalize(requestedType);
        return recipe.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
               SimpleName(recipe).Equals(SimpleName(requested), StringComparison.OrdinalIgnoreCase);
    }

    private static void UpdateCandidateStatusLocked(
        string modId,
        string sha,
        PcCompatResourceCandidateStatus status,
        string? reason)
    {
        if (!Plans.TryGetValue(modId, out var plan))
            return;
        Plans[modId] = new PcCompatResourceSessionPlan
        {
            ModId = plan.ModId,
            ModFolder = plan.ModFolder,
            CompiledResourcesDirectory = plan.CompiledResourcesDirectory,
            Compatibility = plan.Compatibility,
            FeatureGroups = plan.FeatureGroups,
            Candidates = plan.Candidates.Select(candidate =>
                candidate.Sha256Hex.Equals(sha, StringComparison.OrdinalIgnoreCase)
                    ? new PcCompatResourceSessionCandidate
                    {
                        Sha256Hex = candidate.Sha256Hex,
                        FileName = candidate.FileName,
                        PlatformHint = candidate.PlatformHint,
                        LoadPolicy = candidate.LoadPolicy,
                        ResolvedPath = candidate.ResolvedPath,
                        ExpectedFileSize = candidate.ExpectedFileSize,
                        AutoLoadAllowed = candidate.AutoLoadAllowed,
                        Status = status,
                        StatusReason = reason
                    }
                    : candidate).ToArray()
        };
    }

    private static void UnloadCandidates(IReadOnlyList<PcCompatResourceUnloadRequest> requests)
    {
        var sink = Volatile.Read(ref s_bundleSink);
        if (sink == null)
            return;
        foreach (var request in requests)
        {
            try
            {
                sink.Unload(request);
            }
            catch (Exception ex)
            {
                Logger.Warn(
                    LogTag,
                    $"bundle unload scheduler failed mod={request.ModId} sha={request.CandidateSha256Hex} " +
                    $"generation={request.SessionGeneration}: {ex.Message}");
            }
        }
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xphorror.PcModCompat.Resources;

public static class ResourceCompiler
{
    public const string CompilerRevision = "resource-compiler-v3-proven-load-requests";

    public static ResourceCompileReport CompileModFolder(
        string modId,
        string modFolder,
        bool allowForceNonUnity6000 = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        var candidates = UnityBundleIndexer.IndexModFolder(modFolder, allowForceNonUnity6000);
        var flow = AssetLoadFlowAnalyzer.AnalyzeModFolder(modFolder);
        return Compile(modId, candidates, flow);
    }

    public static ResourceCompileReport Compile(
        string modId,
        IReadOnlyList<ResourceCandidateIndex> candidates,
        AssetLoadFlowReport? flow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentNullException.ThrowIfNull(candidates);
        flow ??= new AssetLoadFlowReport();

        var indexedOrder = candidates
            .OrderBy(candidate => PlatformRank(candidate.PlatformHint))
            .ThenBy(candidate => candidate.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var warnings = indexedOrder
            .SelectMany(candidate => candidate.Warnings)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var ordered = indexedOrder
            .GroupBy(candidate => candidate.Sha256Hex, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var canonical = group
                    .OrderByDescending(ScoreCandidate)
                    .ThenBy(candidate => candidate.FileName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(candidate => candidate.SourcePath, StringComparer.OrdinalIgnoreCase)
                    .First();
                if (group.Skip(1).Any())
                {
                    var aliases = string.Join(",", group
                        .Select(candidate => candidate.FileName)
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
                    warnings.Add(
                        $"duplicate candidate content collapsed sha={group.Key} " +
                        $"canonical={canonical.FileName} aliases={aliases}");
                }
                return canonical;
            })
            .OrderBy(candidate => PlatformRank(candidate.PlatformHint))
            .ThenBy(candidate => candidate.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        warnings.AddRange(flow.Issues);
        var unsupported = new List<string>();
        var groups = new List<ResourceFeatureGroup>();
        var bindings = new List<ResourceBinding>();

        var selected = SelectPrimaryCandidate(ordered);
        if (selected == null)
        {
            unsupported.Add("No indexable UnityFS candidate was found.");
            return new ResourceCompileReport
            {
                ModId = modId,
                Compatibility = "unsupported",
                TargetUnityVersion = UnityBundleIndexer.TargetUnityVersion,
                Candidates = ordered,
                Unsupported = unsupported,
                Warnings = warnings.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            };
        }

        if (!selected.IndexSucceeded)
            unsupported.Add($"Primary candidate index failed: {selected.IndexError}");
        if (selected.LoadPolicy is BundleLoadPolicy.Rejected or BundleLoadPolicy.ForceRequired)
            unsupported.Add($"Primary candidate requires explicit force policy: version={selected.UnityVersion}");

        // Proven bindings from IL first.
        foreach (var proven in flow.ProvenBindings)
        {
            if (!TryFindAsset(selected, proven.AssetName, proven.ExpectedTypeHint, out var asset))
            {
                warnings.Add($"Proven IL asset '{proven.AssetName}' was not found in selected candidate {selected.FileName}.");
                continue;
            }

            bindings.Add(new ResourceBinding
            {
                FeatureGroupId = FeatureGroupIdFor(proven.ExpectedTypeHint, proven.AssetName),
                AssetName = asset.Name,
                ExpectedType = proven.ExpectedTypeHint,
                Confidence = AssetBindConfidence.Proven,
                SourceFieldIdentity = proven.DeclaringType + "." + proven.FieldName,
                Reason = $"IL {proven.DeclaringType}.{proven.MethodName} asset '{proven.AssetName}' -> field {proven.FieldName} (name/type proven) @ 0x{proven.IlOffset:X4}"
            });
        }

        // A direct AssetBundle request is proof of runtime demand even when its result is stored in
        // an instance field, a local, or consumed immediately. Do not make materialization depend on
        // the older static-field recovery heuristic.
        foreach (var request in flow.ProvenRequests)
        {
            IReadOnlyList<ResourceAssetEntry> requestedAssets;
            if (request.Kind == AssetLoadFlowRequestKind.LoadAssetByName)
            {
                if (!TryFindAsset(selected, request.AssetName, request.ExpectedTypeHint, out var asset))
                {
                    warnings.Add(
                        $"Proven IL LoadAsset request '{request.AssetName}' ({request.ExpectedTypeHint}) " +
                        $"was not found in selected candidate {selected.FileName}.");
                    continue;
                }
                requestedAssets = [asset];
            }
            else
            {
                requestedAssets = selected.Assets
                    .Where(asset => !string.IsNullOrWhiteSpace(asset.Name))
                    .Where(asset => AssetTypeMatches(asset, request.ExpectedTypeHint))
                    .GroupBy(asset => asset.Name + "\0" + asset.TypeName, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .OrderBy(asset => asset.Name, StringComparer.Ordinal)
                    .ToArray();
                if (requestedAssets.Count == 0)
                {
                    warnings.Add(
                        $"Proven IL LoadAllAssets<{request.ExpectedTypeHint}> request matched no assets " +
                        $"in selected candidate {selected.FileName}.");
                    continue;
                }
            }

            foreach (var asset in requestedAssets)
            {
                var expectedType = request.ExpectedTypeHint.Equals("Object", StringComparison.OrdinalIgnoreCase)
                    ? asset.TypeName
                    : request.ExpectedTypeHint;
                if (bindings.Any(binding =>
                        binding.AssetName.Equals(asset.Name, StringComparison.Ordinal) &&
                        binding.ExpectedType.Equals(expectedType, StringComparison.OrdinalIgnoreCase) &&
                        binding.Confidence == AssetBindConfidence.Proven))
                    continue;
                bindings.Add(new ResourceBinding
                {
                    FeatureGroupId = FeatureGroupIdFor(expectedType, asset.Name),
                    AssetName = asset.Name,
                    ExpectedType = expectedType,
                    Confidence = AssetBindConfidence.Proven,
                    Reason = request.Kind == AssetLoadFlowRequestKind.LoadAssetByName
                        ? $"IL {request.DeclaringType}.{request.MethodName} directly requests " +
                          $"LoadAsset<{expectedType}>('{asset.Name}') @ 0x{request.IlOffset:X4}"
                        : $"IL {request.DeclaringType}.{request.MethodName} directly requests " +
                          $"LoadAllAssets<{expectedType}>() @ 0x{request.IlOffset:X4}"
                });
            }
        }

        // UniqueType fallback only for types not already proven.
        foreach (var typeHint in new[] { "TMP_FontAsset", "GameObject", "Sprite", "Texture2D", "Material", "Font" })
        {
            if (bindings.Any(binding => binding.ExpectedType.Equals(typeHint, StringComparison.OrdinalIgnoreCase)))
                continue;

            var matches = selected.Assets
                .Where(asset => AssetTypeMatches(asset, typeHint))
                .Where(asset => !string.IsNullOrWhiteSpace(asset.Name))
                .GroupBy(asset => asset.Name, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(asset => asset.Name, StringComparer.Ordinal)
                .ToArray();
            if (matches.Length != 1)
            {
                if (matches.Length > 1)
                    warnings.Add($"{typeHint}: {matches.Length} named assets require Proven/Semantic binding; no automatic multi-bind.");
                continue;
            }

            bindings.Add(new ResourceBinding
            {
                FeatureGroupId = FeatureGroupIdFor(typeHint, matches[0].Name),
                AssetName = matches[0].Name,
                ExpectedType = typeHint,
                Confidence = AssetBindConfidence.UniqueType,
                Reason = $"Exactly one {typeHint}-like asset was indexed in the selected candidate."
            });
        }

        // Feature groups: primary candidate + specialized groups from bindings.
        var allNamed = selected.Assets
            .Select(asset => asset.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        groups.Add(new ResourceFeatureGroup
        {
            Id = "bundle.primary",
            DisplayName = "Primary UnityFS candidate",
            SelectedCandidateSha256Hex = selected.Sha256Hex,
            SelectedPlatform = selected.PlatformHint,
            LoadPolicy = selected.LoadPolicy,
            AssetNames = allNamed,
            Notes =
            [
                $"source={selected.FileName}",
                $"unity={selected.UnityVersion}",
                $"assets={selected.Assets.Count}",
                selected.HasEmbeddedTypeTree ? "typeTree=embedded" : "typeTree=missing",
                $"provenBindings={flow.ProvenBindings.Count}",
                $"provenRequests={flow.ProvenRequests.Count}"
            ]
        });

        AddGroupIfBindings(groups, bindings, selected, "overlay.progress_bar", "ProgressBar prefab",
            binding => binding.AssetName.Equals("ProgressBar", StringComparison.OrdinalIgnoreCase) ||
                       (binding.ExpectedType.Equals("GameObject", StringComparison.OrdinalIgnoreCase) &&
                        binding.AssetName.Contains("Progress", StringComparison.OrdinalIgnoreCase)));

        AddGroupIfBindings(groups, bindings, selected, "overlay.font", "Overlay TMP font",
            binding => binding.ExpectedType.Equals("TMP_FontAsset", StringComparison.OrdinalIgnoreCase) ||
                       binding.ExpectedType.Equals("Font", StringComparison.OrdinalIgnoreCase) ||
                       binding.AssetName.Contains("SDF", StringComparison.OrdinalIgnoreCase));

        AddGroupIfBindings(groups, bindings, selected, "keyviewer.sprites", "KeyViewer sprites",
            binding => binding.ExpectedType.Equals("Sprite", StringComparison.OrdinalIgnoreCase) ||
                       binding.AssetName is "Auto" or "KeyBackground" or "KeyOutline" or "GhostRain");

        AddGroupIfBindings(groups, bindings, selected, "overlay.side_image", "Overlay side image",
            binding => binding.AssetName.Equals("SideImage", StringComparison.OrdinalIgnoreCase));

        if (!groups.Any(group => group.Id == "overlay.progress_bar"))
            unsupported.Add("ProgressBar prefab was not proven from IL/index binding.");
        if (!groups.Any(group => group.Id == "overlay.font"))
            unsupported.Add("No Font/TMP_FontAsset binding was recovered.");

        if (flow.ProvenBindings.Count == 0 && flow.ProvenRequests.Count == 0)
            warnings.Add("No proven LoadAsset/LoadAllAssets requests were recovered from MOD IL.");

        var compatibility = !selected.IndexSucceeded || selected.LoadPolicy == BundleLoadPolicy.Rejected
            ? "unsupported"
            : "partial";

        return new ResourceCompileReport
        {
            ModId = modId,
            Compatibility = compatibility,
            TargetUnityVersion = UnityBundleIndexer.TargetUnityVersion,
            Candidates = ordered,
            FeatureGroups = groups,
            Bindings = bindings
                .OrderBy(binding => binding.FeatureGroupId, StringComparer.Ordinal)
                .ThenBy(binding => binding.AssetName, StringComparer.Ordinal)
                .ToArray(),
            Unsupported = unsupported.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            Warnings = warnings.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray()
        };
    }

    public static string ToJson(ResourceCompileReport report)
        => JsonSerializer.Serialize(report, JsonOptions);

    public static ResourceRecipeDocument ToRecipeDocument(ResourceCompileReport report)
        => new()
        {
            ModId = report.ModId,
            RecipeId = "xphorror.resource.indexed_bundle.v1",
            Compatibility = report.Compatibility,
            TargetUnityVersion = report.TargetUnityVersion,
            Candidates = report.Candidates,
            FeatureGroups = report.FeatureGroups,
            Bindings = report.Bindings
        };

    private static void AddGroupIfBindings(
        List<ResourceFeatureGroup> groups,
        IReadOnlyList<ResourceBinding> bindings,
        ResourceCandidateIndex selected,
        string id,
        string displayName,
        Func<ResourceBinding, bool> predicate)
    {
        var matched = bindings.Where(predicate).Select(binding => binding.AssetName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (matched.Length == 0)
            return;

        groups.Add(new ResourceFeatureGroup
        {
            Id = id,
            DisplayName = displayName,
            SelectedCandidateSha256Hex = selected.Sha256Hex,
            SelectedPlatform = selected.PlatformHint,
            LoadPolicy = selected.LoadPolicy,
            AssetNames = matched,
            Notes = new[]
            {
                $"bindings={matched.Length}",
                "atomic feature group; do not mix candidates"
            }
        });
    }

    private static string FeatureGroupIdFor(string expectedType, string assetName)
    {
        if (assetName.Equals("ProgressBar", StringComparison.OrdinalIgnoreCase) ||
            (expectedType.Equals("GameObject", StringComparison.OrdinalIgnoreCase) &&
             assetName.Contains("Progress", StringComparison.OrdinalIgnoreCase)))
            return "overlay.progress_bar";
        if (expectedType.Equals("TMP_FontAsset", StringComparison.OrdinalIgnoreCase) ||
            expectedType.Equals("Font", StringComparison.OrdinalIgnoreCase) ||
            assetName.Contains("SDF", StringComparison.OrdinalIgnoreCase))
            return "overlay.font";
        if (expectedType.Equals("Sprite", StringComparison.OrdinalIgnoreCase) ||
            assetName is "Auto" or "KeyBackground" or "KeyOutline" or "GhostRain")
            return "keyviewer.sprites";
        if (assetName.Equals("SideImage", StringComparison.OrdinalIgnoreCase))
            return "overlay.side_image";
        return "bundle.primary";
    }

    private static bool TryFindAsset(
        ResourceCandidateIndex selected,
        string assetName,
        string expectedTypeHint,
        out ResourceAssetEntry asset)
    {
        // Prefer exact name + type match, then exact name, then type-only unique fallback for TMP fonts
        // that are stored as MonoBehaviour in the index.
        var exact = selected.Assets
            .Where(entry => entry.Name.Equals(assetName, StringComparison.Ordinal))
            .ToArray();
        if (exact.Length == 1)
        {
            asset = exact[0];
            return true;
        }

        if (exact.Length > 1)
        {
            var typed = exact.FirstOrDefault(entry => AssetTypeMatches(entry, expectedTypeHint));
            if (typed != null)
            {
                asset = typed;
                return true;
            }
            asset = exact[0];
            return true;
        }

        if (expectedTypeHint.Equals("TMP_FontAsset", StringComparison.OrdinalIgnoreCase))
        {
            var fontish = selected.Assets
                .Where(entry => entry.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase) ||
                                entry.Name.Contains(assetName, StringComparison.OrdinalIgnoreCase))
                .Where(entry => entry.TypeName.Contains("MonoBehaviour", StringComparison.OrdinalIgnoreCase) ||
                                entry.TypeName.Contains("Font", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (fontish.Length == 1)
            {
                asset = fontish[0];
                return true;
            }
        }

        asset = null!;
        return false;
    }

    private static bool AssetTypeMatches(ResourceAssetEntry asset, string typeHint)
    {
        if (typeHint.Equals("TMP_FontAsset", StringComparison.OrdinalIgnoreCase))
        {
            return asset.TypeName.Contains("TMP_FontAsset", StringComparison.OrdinalIgnoreCase) ||
                   asset.TypeName.Contains("Font", StringComparison.OrdinalIgnoreCase) ||
                   (asset.TypeName.Contains("MonoBehaviour", StringComparison.OrdinalIgnoreCase) &&
                    asset.Name.Contains("SDF", StringComparison.OrdinalIgnoreCase));
        }

        return asset.TypeName.Equals(typeHint, StringComparison.OrdinalIgnoreCase) ||
               asset.TypeName.EndsWith("." + typeHint, StringComparison.OrdinalIgnoreCase) ||
               asset.TypeName.Contains(typeHint, StringComparison.OrdinalIgnoreCase);
    }

    private static ResourceCandidateIndex? SelectPrimaryCandidate(IReadOnlyList<ResourceCandidateIndex> candidates)
    {
        return candidates
            .OrderByDescending(ScoreCandidate)
            .ThenBy(candidate => candidate.FileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static int ScoreCandidate(ResourceCandidateIndex candidate)
    {
        var score = 0;
        score += candidate.IndexSucceeded ? 1000 : 0;
        score += candidate.LoadPolicy switch
        {
            BundleLoadPolicy.AutoLoad => 400,
            BundleLoadPolicy.ControlledLoad => 300,
            BundleLoadPolicy.ForceRequired => 100,
            BundleLoadPolicy.IndexOnly => 50,
            _ => 0
        };
        score += candidate.PlatformHint switch
        {
            BundlePlatformHint.Android => 40,
            BundlePlatformHint.Linux => 30,
            BundlePlatformHint.Windows => 20,
            BundlePlatformHint.Mac => 10,
            _ => 0
        };
        if (candidate.UnityVersion.StartsWith("6000.3.", StringComparison.Ordinal))
            score += 50;
        else if (candidate.UnityVersion.StartsWith("6000.", StringComparison.Ordinal))
            score += 20;
        if (candidate.FileName.Contains("2022", StringComparison.OrdinalIgnoreCase))
            score -= 15;
        score += Math.Min(candidate.Assets.Count, 200);
        return score;
    }

    private static int PlatformRank(BundlePlatformHint hint)
        => hint switch
        {
            BundlePlatformHint.Android => 0,
            BundlePlatformHint.Linux => 1,
            BundlePlatformHint.Windows => 2,
            BundlePlatformHint.Mac => 3,
            _ => 4
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}

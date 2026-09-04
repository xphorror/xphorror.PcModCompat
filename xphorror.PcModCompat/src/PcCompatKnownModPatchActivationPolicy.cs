namespace Xphorror.PcModCompat;

/// <summary>
/// Applies version gates that a known MOD expresses in ordinary managed control flow rather than
/// in patch attributes. The metadata scanner cannot execute that control flow, so without this
/// policy both mutually-exclusive branches would be installed.
/// </summary>
internal static class PcCompatKnownModPatchActivationPolicy
{
    private const string JipperOverlayerModId = "JipperOverlayer";
    private const int JipperOverlayerV141Revision = 141;

    private sealed record BranchMember(string Role, int MinRevision, int MaxRevision);

    private static readonly IReadOnlyDictionary<string, BranchMember> JipperOverlayerBranches =
        new Dictionary<string, BranchMember>(StringComparer.Ordinal)
        {
            ["JipperOverlayer.Overlayer.Features.ScrPlayerHitBpmPatch"] =
                new("bpm", JipperOverlayerV141Revision, int.MaxValue),
            ["JipperOverlayer.Overlayer.Features.ScrMarginAddHitComboPatch"] =
                new("combo", JipperOverlayerV141Revision, int.MaxValue),
            ["JipperOverlayer.Overlayer.Features.ScrMarginAddHitJudgementPatch"] =
                new("judgement", JipperOverlayerV141Revision, int.MaxValue),
            ["JipperOverlayer.Overlayer.Features.ScrMarginResetPatch"] =
                new("reset", JipperOverlayerV141Revision, int.MaxValue),
            ["JipperOverlayer.Overlayer.Features.ScrMarginCalcAccPatch"] =
                new("accuracy", JipperOverlayerV141Revision, int.MaxValue),
            ["JipperOverlayer.Overlayer.Features.ScrMarginAddHitJComboPatch"] =
                new("jongyeol_combo", JipperOverlayerV141Revision, int.MaxValue),
            ["JipperOverlayer.Overlayer.Features.MistakesManagerSetPlayerCountPatch"] =
                new("player_count", JipperOverlayerV141Revision, int.MaxValue),

            ["JipperOverlayer.Overlayer.Features.ScrControllerHitBpmPatch"] =
                new("bpm", 0, JipperOverlayerV141Revision - 1),
            ["JipperOverlayer.Overlayer.Features.ScrMistakesAddHitComboPatch"] =
                new("combo", 0, JipperOverlayerV141Revision - 1),
            ["JipperOverlayer.Overlayer.Features.ScrMistakesAddHitJudgementPatch"] =
                new("judgement", 0, JipperOverlayerV141Revision - 1),
            ["JipperOverlayer.Overlayer.Features.ScrMistakesResetPatch"] =
                new("reset", 0, JipperOverlayerV141Revision - 1),
            ["JipperOverlayer.Overlayer.Features.ScrMistakesCalcAccPatch"] =
                new("accuracy", 0, JipperOverlayerV141Revision - 1),
            ["JipperOverlayer.Overlayer.Features.ScrMistakesAddHitJComboPatch"] =
                new("jongyeol_combo", 0, JipperOverlayerV141Revision - 1)
        };

    public static IReadOnlyList<PcCompatPatchDescriptor> Apply(
        string modId,
        IReadOnlyList<PcCompatPatchDescriptor> patches,
        ICollection<PcCompatStaticPatchScanIssue> issues)
    {
        if (!string.Equals(modId, JipperOverlayerModId, StringComparison.OrdinalIgnoreCase))
            return patches;

        var normalized = new List<PcCompatPatchDescriptor>(patches.Count);
        foreach (var patch in patches)
        {
            if (!JipperOverlayerBranches.TryGetValue(patch.CallbackType, out var branch))
            {
                normalized.Add(patch);
                continue;
            }

            var minRevision = Math.Max(patch.MinVersion, branch.MinRevision);
            var maxRevision = Math.Min(patch.MaxVersion, branch.MaxRevision);
            if (minRevision > maxRevision)
            {
                issues.Add(new PcCompatStaticPatchScanIssue
                {
                    Code = "KnownModActivationRangeConflict",
                    Message = $"{patch.CallbackType}.{patch.CallbackMethod} declares revision range " +
                              $"{patch.MinVersion}..{FormatMax(patch.MaxVersion)}, but JPOV's " +
                              $"{branch.Role} branch requires {branch.MinRevision}..{FormatMax(branch.MaxRevision)}; " +
                              "the callback is disabled.",
                    AssemblyPath = patch.CallbackAssemblyPath,
                    CallbackType = patch.CallbackType,
                    CallbackMethod = patch.CallbackMethod
                });
            }

            normalized.Add(CloneWithVersionRange(patch, minRevision, maxRevision));
        }

        var overlapping = FindOverlappingJipperOverlayerRoles(normalized, issues);
        return overlapping.Count == 0
            ? normalized
            : normalized
                .Select(patch => overlapping.Contains(patch)
                    ? CloneWithVersionRange(patch, 1, 0)
                    : patch)
                .ToArray();
    }

    private static HashSet<PcCompatPatchDescriptor> FindOverlappingJipperOverlayerRoles(
        IReadOnlyList<PcCompatPatchDescriptor> patches,
        ICollection<PcCompatStaticPatchScanIssue> issues)
    {
        var rejected = new HashSet<PcCompatPatchDescriptor>();
        var known = patches
            .Where(patch => JipperOverlayerBranches.ContainsKey(patch.CallbackType))
            .GroupBy(patch => JipperOverlayerBranches[patch.CallbackType].Role, StringComparer.Ordinal);

        foreach (var role in known)
        {
            var callbacks = role.ToArray();
            for (var leftIndex = 0; leftIndex < callbacks.Length; leftIndex++)
            {
                for (var rightIndex = leftIndex + 1; rightIndex < callbacks.Length; rightIndex++)
                {
                    var left = callbacks[leftIndex];
                    var right = callbacks[rightIndex];
                    if (left.CallbackType == right.CallbackType ||
                        left.MaxVersion < right.MinVersion ||
                        right.MaxVersion < left.MinVersion)
                    {
                        continue;
                    }

                    issues.Add(new PcCompatStaticPatchScanIssue
                    {
                        Code = "KnownModActivationBranchesOverlap",
                        Message = $"JPOV role {role.Key} has overlapping callbacks " +
                                  $"{left.CallbackType} ({left.MinVersion}..{FormatMax(left.MaxVersion)}) and " +
                                  $"{right.CallbackType} ({right.MinVersion}..{FormatMax(right.MaxVersion)}); " +
                                  "both callbacks are rejected for the overlapping revisions.",
                        AssemblyPath = left.CallbackAssemblyPath,
                        CallbackType = left.CallbackType,
                        CallbackMethod = left.CallbackMethod
                    });
                    rejected.Add(left);
                    rejected.Add(right);
                }
            }
        }

        return rejected;
    }

    private static PcCompatPatchDescriptor CloneWithVersionRange(
        PcCompatPatchDescriptor source,
        int minVersion,
        int maxVersion)
        => new()
        {
            ModId = source.ModId,
            TargetType = source.TargetType,
            TargetMethod = source.TargetMethod,
            Kind = source.Kind,
            CallbackType = source.CallbackType,
            CallbackMethod = source.CallbackMethod,
            CallbackAssemblyPath = source.CallbackAssemblyPath,
            CallbackParameterTypeNames = source.CallbackParameterTypeNames,
            PatchOwner = source.PatchOwner,
            RegistrationIndex = source.RegistrationIndex,
            Priority = source.Priority,
            Before = source.Before,
            After = source.After,
            NeedInstance = source.NeedInstance,
            MinVersion = minVersion,
            MaxVersion = maxVersion,
            TryingCatch = source.TryingCatch,
            ArgumentTypeNames = source.ArgumentTypeNames,
            Source = source.Source,
            Status = source.Status,
            Reason = source.Reason
        };

    private static string FormatMax(int value)
        => value == int.MaxValue ? "max" : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

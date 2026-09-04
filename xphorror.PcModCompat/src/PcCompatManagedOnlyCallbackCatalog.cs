namespace Xphorror.PcModCompat;

/// <summary>
/// Exact signatures for audited managed callbacks that deliberately have no native fixed-op.
/// Entries may also explicitly keep a known callback unsupported until its proxy/object graph is
/// closed; the runtime metadata fallback must not silently promote those callbacks.
/// </summary>
internal static class PcCompatManagedOnlyCallbackCatalog
{
    internal sealed class Entry
    {
        public required string ModId { get; init; }
        public required string CallbackType { get; init; }
        public required string CallbackMethod { get; init; }
        public required IReadOnlyList<string> CallbackParameterTypes { get; init; }
        public required PcCompatPatchKind PatchKind { get; init; }
        public required string TargetType { get; init; }
        public required string TargetMethod { get; init; }
        public bool Supported { get; init; }
        public PcCompatResolvedTargetSignature? TargetSignature { get; init; }
        public required string Reason { get; init; }
    }

    private const string JipperOverlayer = "JipperOverlayer";
    private const string JipperFeatureNamespace = "JipperOverlayer.Overlayer.Features.";

    private static readonly Entry[] Entries =
    [
        new()
        {
            ModId = JipperOverlayer,
            CallbackType = JipperFeatureNamespace + "RdcSetAutoPatch",
            CallbackMethod = "Postfix",
            CallbackParameterTypes = Array.Empty<string>(),
            PatchKind = PcCompatPatchKind.Postfix,
            TargetType = "RDC",
            TargetMethod = "set_auto",
            Supported = true,
            TargetSignature = Signature("RDC", "set_auto", isStatic: true, "System.Void", "System.Boolean"),
            Reason = "audited JPOV managed-only callback; no native fixed-op owns Jongyeol layout state"
        },
        new()
        {
            ModId = JipperOverlayer,
            CallbackType = JipperFeatureNamespace + "ScrShowIfDebugUpdatePatch",
            CallbackMethod = "Prefix",
            CallbackParameterTypes = ["UnityEngine.UI.Text"],
            PatchKind = PcCompatPatchKind.Prefix,
            TargetType = "scrShowIfDebug",
            TargetMethod = "Update",
            Supported = true,
            TargetSignature = Signature("scrShowIfDebug", "Update", isStatic: false, "System.Void"),
            Reason = "audited JPOV Prefix; generated proxy exposes scrShowIfDebug.txt as UnityEngine.UI.Text and Behaviour.enabled writeback"
        },
        new()
        {
            ModId = JipperOverlayer,
            CallbackType = JipperFeatureNamespace + "ScrShowIfDebugAwakePatch",
            CallbackMethod = "Postfix",
            CallbackParameterTypes = ["scrShowIfDebug"],
            PatchKind = PcCompatPatchKind.Postfix,
            TargetType = "scrShowIfDebug",
            TargetMethod = "Awake",
            Supported = true,
            TargetSignature = Signature("scrShowIfDebug", "Awake", isStatic: false, "System.Void"),
            Reason = "audited JPOV Postfix; generated proxies close Component.GetComponent<RectTransform>, Unity object truthiness, Vector2 and anchored-position writeback"
        },
        new()
        {
            ModId = JipperOverlayer,
            CallbackType = JipperFeatureNamespace + "BetaWatermarkCapturePatch",
            CallbackMethod = "Postfix",
            CallbackParameterTypes = ["scrEnableIfBeta"],
            PatchKind = PcCompatPatchKind.Postfix,
            TargetType = "scrEnableIfBeta",
            TargetMethod = "Awake",
            Reason = "scrEnableIfBeta.setBuildText and retained RectTransform lifecycle are not closed"
        }
    ];

    public static bool TryFind(PcCompatPatchDescriptor patch, out Entry entry)
    {
        entry = Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.ModId, patch.ModId, StringComparison.OrdinalIgnoreCase) &&
            candidate.CallbackType == patch.CallbackType &&
            candidate.CallbackMethod == patch.CallbackMethod &&
            candidate.CallbackParameterTypes.SequenceEqual(patch.CallbackParameterTypeNames, StringComparer.Ordinal) &&
            candidate.PatchKind == patch.Kind &&
            candidate.TargetType == patch.TargetType &&
            candidate.TargetMethod == patch.TargetMethod)!;
        return entry != null;
    }

    private static PcCompatResolvedTargetSignature Signature(
        string typeName,
        string methodName,
        bool isStatic,
        string returnType,
        params string[] parameterTypes)
        => new()
        {
            AssemblyName = "Assembly-CSharp",
            TypeName = typeName,
            MethodName = methodName,
            IsStatic = isStatic,
            ReturnType = returnType,
            ParameterTypes = parameterTypes
        };
}

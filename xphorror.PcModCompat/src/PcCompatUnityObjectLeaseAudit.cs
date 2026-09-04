namespace Xphorror.PcModCompat;

/// <summary>
/// Cross-backend inventory of the Unity objects one MOD session currently holds through the
/// Host's lease registries: component-bridge owner GameObjects (managed components plus
/// native leases and registered creations), the VirtualBundle resource session, the
/// ResourceChanger contribution and the HUD presentation surfaces.
/// </summary>
/// <remarks>
/// This is the read-only half of the stage-4 <c>UnityObjectLease</c> unification: ownership,
/// teardown and recovery semantics stay inside each backend registry; this snapshot only makes
/// "unload released exactly this owner's objects" checkable from one place instead of four.
/// </remarks>
public sealed record PcCompatUnityObjectLeaseAuditSnapshot(
    string ModId,
    long ResourceSessionGeneration,
    int OwnedHostGameObjects,
    bool VirtualBundleSessionPresent,
    bool ResourceChangerContributionPresent,
    int HudSurfaces)
{
    /// <summary>True when no backend registry holds anything for this session.</summary>
    public bool IsClear =>
        OwnedHostGameObjects == 0 &&
        !VirtualBundleSessionPresent &&
        !ResourceChangerContributionPresent &&
        HudSurfaces == 0;
}

public static class PcCompatUnityObjectLeaseAudit
{
    public static PcCompatUnityObjectLeaseAuditSnapshot Snapshot(
        string modId,
        long resourceSessionGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        var gameObjects = PcCompatManagedComponentBridge.SnapshotOwnerGameObjects(
            modId,
            resourceSessionGeneration);
        return new PcCompatUnityObjectLeaseAuditSnapshot(
            modId,
            resourceSessionGeneration,
            OwnedHostGameObjects: gameObjects.Count,
            VirtualBundleSessionPresent: PcCompatVirtualBundleRegistry.HasSession(
                modId,
                resourceSessionGeneration),
            ResourceChangerContributionPresent:
                PcCompatResourceChangerRuntime.TryGetState(modId, out _),
            HudSurfaces: PcCompatUnityHudRuntime.SnapshotSources().Count(source =>
                string.Equals(source.OwnerId, modId, StringComparison.Ordinal) &&
                source.SessionGeneration == resourceSessionGeneration));
    }
}

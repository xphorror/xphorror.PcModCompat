namespace Xphorror.PcModCompat;

/// <summary>Outcome of tearing down one backend's leases for a session.</summary>
public sealed record PcCompatUnityLeaseTeardownStep(
    string Backend,
    bool Attempted,
    bool Succeeded,
    string? Error);

/// <summary>Aggregate result of the cross-backend teardown protocol.</summary>
public sealed record PcCompatUnityLeaseTeardownResult(
    string ModId,
    long ResourceSessionGeneration,
    IReadOnlyList<PcCompatUnityLeaseTeardownStep> Steps)
{
    /// <summary>True when no step reported a failure.</summary>
    public bool Succeeded => Steps.All(step => step.Error == null);

    public string? FirstError => Steps.FirstOrDefault(step => step.Error != null)?.Error;
}

/// <summary>
/// Cross-backend destroy/recover protocol for a MOD session's Unity leases: the second half of
/// the stage-4 unified <c>UnityObjectLease</c> work (the read half is
/// <see cref="PcCompatUnityObjectLeaseAudit"/>).
/// </summary>
/// <remarks>
/// <para>
/// Each backend keeps its own registry and its own ownership rules; this driver does not
/// reimplement or bypass them. It only guarantees the two properties that were missing while
/// callers tore backends down ad hoc: a <b>fixed order</b>, and <b>per-backend fault
/// isolation</b> — one backend failing must not strand the remaining ones, and every outcome is
/// reported instead of the first exception hiding the rest.
/// </para>
/// <para>
/// Order is deliberate and follows dependency direction: managed components (which own the host
/// GameObjects and drive Unity <c>Destroy</c>) first, then the resource sessions those objects
/// consumed (VirtualBundle), then shared-property contributions (ResourceChanger) so the next
/// owner's baseline is restored last. HUD surfaces are intentionally not driven here: they are
/// unregistered by the source that owns them (<c>UnregisterSource(source)</c> takes the instance,
/// not an owner id), and reaching around that would bypass the source owner's lifecycle.
/// </para>
/// <para>
/// <b>Relationship to the production unload path.</b> <c>PcCompatRuntime.UnregisterMod</c>
/// already performs these steps in exactly this order (session dispose → VirtualBundle →
/// ResourceChanger). This type is therefore the executable specification of that order plus the
/// missing fault aggregation, not a second cleanup pass: calling it on a live session would
/// double-tear-down. Use it for teardown verification, for recovery after a partially failed
/// unload (every step is idempotent in its backend), and for diagnostics that must report which
/// backend refused to release. Pair it with <see cref="PcCompatUnityObjectLeaseAudit"/> to prove
/// a session ended clean.
/// </para>
/// </remarks>
public static class PcCompatUnityLeaseTeardown
{
    public static PcCompatUnityLeaseTeardownResult Run(
        string modId,
        long resourceSessionGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        var steps = new List<PcCompatUnityLeaseTeardownStep>(3);

        // 1. Managed components / native component leases / registered creations. This is the
        //    backend that actually calls Unity Destroy, so it runs before the resources those
        //    objects referenced are released.
        steps.Add(RunStep(
            "managedComponents",
            () =>
            {
                if (!PcCompatManagedComponentBridge.TryClearSession(
                        modId,
                        resourceSessionGeneration,
                        out var error) &&
                    error != null)
                {
                    throw new InvalidOperationException(error);
                }
            }));

        // 2. VirtualBundle resource session: safe to release once the objects consuming those
        //    assets are gone.
        steps.Add(RunStep(
            "virtualBundle",
            () => PcCompatVirtualBundleRegistry.RemoveMod(modId)));

        // 3. Shared-property contributions last, so removing this owner restores the next
        //    owner's value or the official baseline as its final act.
        steps.Add(RunStep(
            "resourceChanger",
            () => PcCompatResourceChangerRuntime.Remove(modId)));

        return new PcCompatUnityLeaseTeardownResult(
            modId,
            resourceSessionGeneration,
            steps);
    }

    private static PcCompatUnityLeaseTeardownStep RunStep(string backend, Action action)
    {
        try
        {
            action();
            return new PcCompatUnityLeaseTeardownStep(backend, true, true, null);
        }
        catch (Exception exception)
        {
            // Per-backend isolation: record and continue so a failing backend cannot strand the
            // ones after it. The caller decides whether to retry on the next teardown pass.
            return new PcCompatUnityLeaseTeardownStep(
                backend,
                true,
                false,
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }
}

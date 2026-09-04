namespace StArray.ModManager.Runtime;

/// <summary>
/// Soft/hard limits for one resource kind.
/// </summary>
/// <param name="Soft">Warn once past this count; MOD semantics are unchanged.</param>
/// <param name="Hard">Refuse new registrations of this kind past this count.</param>
internal readonly record struct ModResourceBudgetLimits(int Soft, int Hard)
{
    internal bool IsUnbounded => Hard <= 0;
}

internal sealed record ModResourceBudgetUsage(
    ModOwnedResourceKind Kind,
    int Live,
    int Soft,
    int Hard,
    bool SoftExceeded,
    bool HardExceeded);

/// <summary>
/// Per-domain resource budgets: one MOD must not be able to exhaust a shared Host resource and
/// starve the others, and hitting a limit must degrade only the offending MOD.
/// </summary>
/// <remarks>
/// <para>
/// Enforced inside <see cref="ModOwnedResourceRegistry.TryRegister"/>, which every owner-scoped
/// resource already passes through, so all registered kinds are covered by one gate rather than
/// a check duplicated per call site.
/// </para>
/// <para>
/// Contract per MOD_RUNTIME_ISOLATION: a soft limit only warns and diagnoses; a hard limit
/// refuses <em>that</em> MOD's new registration and never reclaims another MOD's resources,
/// never revokes already-installed Harmony patches, and never force-releases something holding
/// a lease or Unity ownership. Retirement is unaffected: freeing is always allowed, so a MOD
/// that hit its ceiling can still unload cleanly.
/// </para>
/// <para>
/// Host-owned work does not consume MOD budgets: the registry is only used for MOD-owned
/// resources, so ModManager, the game and authorization keep an implicitly reserved allowance.
/// The ceilings are conservative starting points to be re-scaled from device stress data; a MOD
/// cannot raise them because they are Host-side and not exposed to MOD code.
/// </para>
/// </remarks>
internal static class ModResourceBudget
{
    /// <summary>
    /// Kinds without a numeric ceiling: hooks and code patches are process-lifetime by design
    /// (retiring one only flips a logical gate), so refusing a registration would desynchronize
    /// the registry from the physical chain rather than protect anything. They stay audited.
    /// </summary>
    private static readonly ModResourceBudgetLimits Unbounded = new(0, 0);

    private static ModResourceBudgetLimits LimitsFor(ModOwnedResourceKind kind) => kind switch
    {
        // Physical installation is permanent and owner-gated; see remarks.
        ModOwnedResourceKind.Hook => Unbounded,
        ModOwnedResourceKind.CodePatch => Unbounded,
        ModOwnedResourceKind.Symbol => Unbounded,

        // Concurrency-shaped resources: a runaway loop here is what starves other MODs.
        ModOwnedResourceKind.AsyncOperation => new(64, 256),
        ModOwnedResourceKind.NativeOperation => new(16, 64),

        // Unity/HUD objects and provider registrations grow with real feature use but a leak
        // must not be unbounded.
        ModOwnedResourceKind.UnityObject => new(512, 4096),
        ModOwnedResourceKind.Hud => new(16, 64),
        ModOwnedResourceKind.Behaviour => new(64, 512),
        ModOwnedResourceKind.Provider => new(32, 128),
        ModOwnedResourceKind.Resource => new(512, 4096),
        ModOwnedResourceKind.InputSubscription => new(32, 128),

        // A MOD may only carry managed DLLs; native libraries are a diagnosable downgrade, so
        // the ceiling is deliberately tight.
        ModOwnedResourceKind.NativeLibrary => new(1, 4),

        _ => Unbounded,
    };

    /// <summary>
    /// Whether one more resource of this kind may be registered for this owner.
    /// <paramref name="liveCount"/> is the owner's current live count for the kind.
    /// </summary>
    internal static bool TryReserve(
        ModRuntimeKey key,
        ModOwnedResourceKind kind,
        int liveCount,
        out string? refusal)
    {
        refusal = null;
        var limits = LimitsFor(kind);
        if (limits.IsUnbounded)
            return true;

        if (liveCount >= limits.Hard)
        {
            refusal =
                $"MOD resource budget exhausted owner={key.OwnerId} generation={key.Generation} " +
                $"kind={kind} live={liveCount} hard={limits.Hard}; the request is refused for " +
                "this MOD only.";
            return false;
        }
        if (limits.Soft > 0 && liveCount + 1 == limits.Soft)
        {
            // Warn exactly once per kind at the crossing point: a soft limit must not change
            // behavior, and repeating it per registration would flood the log.
            Manager.Logger.Warn(
                nameof(ModResourceBudget),
                $"MOD resource soft budget reached owner={key.OwnerId} " +
                $"generation={key.Generation} kind={kind} live={liveCount + 1} " +
                $"soft={limits.Soft} hard={limits.Hard}");
        }
        return true;
    }

    internal static ModResourceBudgetUsage Describe(ModOwnedResourceKind kind, int liveCount)
    {
        var limits = LimitsFor(kind);
        return new ModResourceBudgetUsage(
            kind,
            liveCount,
            limits.Soft,
            limits.Hard,
            SoftExceeded: limits.Soft > 0 && liveCount >= limits.Soft,
            HardExceeded: !limits.IsUnbounded && liveCount >= limits.Hard);
    }
}

namespace StArray.ModManager.Runtime;

/// <summary>
/// A generation-bound lease for MOD-owned background work. Dispose the lease only after the
/// operation has stopped accessing MOD code, native callbacks, or Unity resources.
/// </summary>
public interface IModRuntimeOperationLease : IDisposable
{
    /// <summary>Requested when the owning MOD generation starts retiring.</summary>
    CancellationToken CancellationToken { get; }

    /// <summary>Reports whether Host retirement requested this operation to stop.</summary>
    bool IsCancellationRequested { get; }
}

/// <summary>
/// Registers background work against the currently executing MOD generation so unload waits
/// for it and reload cannot let an old generation release a new generation's operation.
/// </summary>
public static class ModRuntimeOperations
{
    /// <summary>
    /// Begins a background operation owned by the current MOD callback or load scope.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The caller is outside a MOD scope, or the current generation is retiring.
    /// </exception>
    public static IModRuntimeOperationLease Begin(string name)
    {
        if (TryBegin(name, out var lease))
            return lease!;
        throw new InvalidOperationException(
            "A MOD runtime operation requires an active MOD scope and generation.");
    }

    /// <summary>
    /// Attempts to begin a background operation owned by the current MOD generation.
    /// </summary>
    public static bool TryBegin(
        string name,
        out IModRuntimeOperationLease? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var session = HookHelper.CurrentRuntimeSession;
        var key = HookHelper.CurrentRuntimeKey;
        if (session == null || !key.IsValid ||
            !session.TryBeginOwnedOperation(
                key,
                name,
                out IModRuntimeOperationLease? ownedLease))
        {
            lease = null;
            return false;
        }

        lease = ownedLease;
        return true;
    }
}

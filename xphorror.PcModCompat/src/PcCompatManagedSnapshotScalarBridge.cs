namespace Xphorror.PcModCompat;

public enum PcCompatSnapshotScalarResolution
{
    Unhandled,
    Resolved,
    Unavailable
}

public delegate PcCompatSnapshotScalarResolution PcCompatSnapshotScalarResolver(
    PcCompatManagedExecutionState owner,
    Type declaringType,
    string memberName,
    Type requestedType,
    object? instance,
    out object? value);

/// <summary>
/// Host-owned scalar snapshot route used by dynamic getter bindings. The route is deliberately
/// signature-based: MOD identity and business code never participate in deciding whether a member
/// is a snapshot candidate.
/// </summary>
public static class PcCompatManagedSnapshotScalarBridge
{
    private static PcCompatSnapshotScalarResolver? s_resolver;

    public static void RegisterResolver(PcCompatSnapshotScalarResolver? resolver)
        => Volatile.Write(ref s_resolver, resolver);

    public static PcCompatSnapshotScalarResolution TryResolve(
        PcCompatManagedExecutionState owner,
        Type declaringType,
        string memberName,
        Type requestedType,
        object? instance,
        out object? value)
    {
        var resolver = Volatile.Read(ref s_resolver);
        if (resolver is null)
        {
            value = null;
            return PcCompatSnapshotScalarResolution.Unhandled;
        }

        try
        {
            return resolver(owner, declaringType, memberName, requestedType, instance, out value);
        }
        catch (Exception exception)
        {
            value = null;
            throw new InvalidOperationException(
                $"PcCompat snapshot scalar resolver failed for " +
                $"{declaringType.FullName}.{memberName}: {exception.Message}",
                exception);
        }
    }
}

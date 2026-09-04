namespace StArray.ModManager.Runtime;

/// <summary>
/// Thrown when a Consumer reaches a Provider whose link is not usable: not registered, not
/// ready yet, or superseded by a Provider reload. Never substituted by a copy of the state or a
/// null placeholder — the contract requires the caller to see an explicit failure.
/// </summary>
public sealed class ModDependencyNotReadyException(string message) : InvalidOperationException(message);

/// <summary>
/// One registered Direct Link: a Consumer generation bound to a Provider generation.
/// </summary>
internal sealed record ModDirectLink(
    ModRuntimeKey Consumer,
    ModRuntimeKey Provider,
    bool Inferred);

/// <summary>
/// Call gate for <c>Direct Mod API Link</c>: lets an existing MOD keep calling another MOD's
/// API directly (static members, singletons, instance methods) while the Host still knows which
/// domain the work belongs to.
/// </summary>
/// <remarks>
/// <para>
/// Skeleton scope, deliberately narrow. It implements the invariants that are testable without
/// real cross-MOD samples: link registration keyed by both generations, an explicit
/// <see cref="ModDependencyNotReadyException"/> when the link is missing or stale, a call gate
/// that takes <b>both</b> sides' callback leases and enters the Provider's owner scope, LIFO
/// restore of the Consumer scope on return <b>and</b> on exception, reentrancy for
/// <c>A -&gt; B -&gt; A</c>, and exception fidelity (the Provider's exception propagates with its
/// own type and stack rather than being wrapped).
/// </para>
/// <para>
/// Deliberately <b>not</b> implemented here, pending real MOD evidence (today's audit of six real
/// MOD assemblies found zero cross-MOD discovery call sites): API Surface Hash identity and
/// automatic candidate binding, <c>AssemblyRef/MemberRef</c> closure scanning, the reverse gate
/// for Consumer delegates called back from the Provider, <c>CrossDomainObjectLease</c> for
/// Provider-owned objects, SCC staging for dependency cycles, and Provider hot-reload rebinding.
/// Those need the shape of a real Provider API to design against; guessing them would bake in
/// assumptions that real data would then have to unwind.
/// </para>
/// <para>
/// No lock is held while Provider code runs: the link is resolved into an immutable snapshot at
/// entry and the registry lock is released before the callee is invoked, per the contract's
/// "生命周期图、链接表和缓存锁不得在进入 MOD 代码时持有".
/// </para>
/// </remarks>
public static class ModDirectLinkGate
{
    private static readonly object Sync = new();
    private static readonly List<ModDirectLink> Links = [];

    /// <summary>
    /// Registers a link from Consumer to Provider. <paramref name="inferred"/> records that the
    /// dependency was not declared in the manifest but resolved as a unique candidate.
    /// </summary>
    internal static bool TryRegisterLink(
        ModRuntimeKey consumer,
        ModRuntimeKey provider,
        bool inferred)
    {
        if (!consumer.IsValid || !provider.IsValid)
            return false;
        if (consumer.Matches(provider))
            return false;

        lock (Sync)
        {
            foreach (var link in Links)
            {
                if (link.Consumer.Matches(consumer) && link.Provider.Matches(provider))
                    return true;
            }
            Links.Add(new ModDirectLink(consumer, provider, inferred));
            return true;
        }
    }

    /// <summary>
    /// Drops every link where this key is the Consumer or the Provider. A Provider reload lands
    /// on a new generation, so its old links stop resolving and further calls fail closed.
    /// </summary>
    internal static int ReleaseLinksFor(ModRuntimeKey key)
    {
        if (!key.IsValid)
            return 0;
        lock (Sync)
            return Links.RemoveAll(link =>
                link.Consumer.Matches(key) || link.Provider.Matches(key));
    }

    internal static IReadOnlyList<ModDirectLink> SnapshotLinks()
    {
        lock (Sync)
            return Links.ToArray();
    }

    /// <summary>
    /// Invokes <paramref name="call"/> as the Provider on the calling thread: both sides' leases
    /// are held for the duration, the Provider's owner scope is active so resources it creates
    /// are attributed to it, and the Consumer's scope is restored afterwards.
    /// </summary>
    /// <exception cref="ModDependencyNotReadyException">
    /// The link is not registered, or either side's generation has retired.
    /// </exception>
    internal static T Invoke<T>(
        ModRuntimeKey consumer,
        ModRuntimeSession consumerSession,
        ModRuntimeKey provider,
        ModRuntimeSession providerSession,
        Func<T> call)
    {
        ArgumentNullException.ThrowIfNull(consumerSession);
        ArgumentNullException.ThrowIfNull(providerSession);
        ArgumentNullException.ThrowIfNull(call);

        var linked = false;
        lock (Sync)
        {
            foreach (var link in Links)
            {
                if (link.Consumer.Matches(consumer) && link.Provider.Matches(provider))
                {
                    linked = true;
                    break;
                }
            }
        }
        // The registry lock is released before any MOD code runs.
        if (!linked)
        {
            throw new ModDependencyNotReadyException(
                $"Direct Link is not established consumer={consumer.OwnerId}" +
                $" generation={consumer.Generation} provider={provider.OwnerId}" +
                $" generation={provider.Generation}.");
        }

        // Both generations must still be live: a retired Provider must not be entered, and a
        // retired Consumer must not be able to keep driving one.
        if (!consumerSession.TryEnterCallback(consumer, out var consumerLease) ||
            consumerLease == null)
        {
            throw new ModDependencyNotReadyException(
                $"Direct Link consumer generation is not callable consumer={consumer.OwnerId}" +
                $" generation={consumer.Generation}.");
        }
        try
        {
            if (!providerSession.TryEnterCallback(provider, out var providerLease) ||
                providerLease == null)
            {
                throw new ModDependencyNotReadyException(
                    $"Direct Link provider generation is not callable provider={provider.OwnerId}" +
                    $" generation={provider.Generation}.");
            }
            using (providerLease)
            using (HookHelper.EnterOwnerScope(provider.OwnerId, providerSession, provider))
            {
                // Provider code runs on the caller's thread with no Host lock held. Its
                // exceptions propagate unwrapped: the contract requires the concrete type,
                // object identity and inner exception to survive the boundary. The scope is
                // restored by the using blocks on both the normal and the exception path.
                return call();
            }
        }
        finally
        {
            consumerLease.Dispose();
        }
    }

    /// <summary>Void-returning overload of <see cref="Invoke{T}"/>.</summary>
    internal static void Invoke(
        ModRuntimeKey consumer,
        ModRuntimeSession consumerSession,
        ModRuntimeKey provider,
        ModRuntimeSession providerSession,
        Action call)
    {
        ArgumentNullException.ThrowIfNull(call);
        Invoke<bool>(consumer, consumerSession, provider, providerSession, () =>
        {
            call();
            return true;
        });
    }

    internal static void ClearForTests()
    {
        lock (Sync)
            Links.Clear();
    }
}

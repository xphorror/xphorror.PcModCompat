using System.Net;
using System.Net.Http;

namespace Xphorror.PcModCompat;

/// <summary>
/// Host-owned network boundary for rewritten PcCompat MOD assemblies.
/// A MOD never constructs its own <see cref="HttpClient"/>; the Host hands it one whose
/// handler, cookie container, credentials and connection pool belong to this MOD session
/// alone.
/// </summary>
/// <remarks>
/// <para>
/// This is the PcCompat counterpart of
/// <c>StArray.ModManager.Runtime.ModRuntimeNetworkBridge</c>. As with the filesystem bridges,
/// the two cannot share one implementation because ownership is keyed differently: Android
/// Managed MODs resolve a data domain from a domain token, while PcCompat MODs carry
/// <see cref="PcCompatManagedExecutionState"/>. Only the client-producing entry points need
/// rewriting: once a client is session-bound, operations on it (<c>GetAsync</c>,
/// <c>DefaultRequestHeaders</c>, <c>Timeout</c>) and on the objects it returns
/// (<c>HttpResponseMessage</c>, <c>HttpContent</c>) are already isolated, so routing them
/// through this bridge would add cost without changing ownership.
/// </para>
/// <para>
/// Cooperative ownership boundary, not a network sandbox: a MOD reaching the network through
/// unrewritten reflection or raw sockets remains a diagnosable isolation downgrade.
/// </para>
/// </remarks>
public static class PcCompatManagedNetworkBridge
{
    private static readonly object Gate = new();
    private static readonly Dictionary<SessionKey, PcCompatNetworkState> States = new();

    /// <summary>
    /// Binds one network identity per MOD session. Rebound per resource generation so a
    /// reloaded MOD cannot keep sending through clients captured by the previous generation.
    /// </summary>
    public static void BindNetworkState(string modId, long resourceSessionGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        lock (Gate)
            States[new SessionKey(modId, resourceSessionGeneration)] =
                new PcCompatNetworkState(modId, resourceSessionGeneration);
    }

    /// <summary>
    /// Cancels this generation's in-flight requests, disposes its clients and rejects all
    /// further use. Called when the session disables so a retired generation never keeps
    /// network traffic alive past unload.
    /// </summary>
    public static void ClearNetworkState(string modId, long resourceSessionGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        PcCompatNetworkState? state;
        lock (Gate)
        {
            if (!States.Remove(new SessionKey(modId, resourceSessionGeneration), out state))
                return;
        }
        state.Retire();
    }

    internal static bool IsBound(string modId, long resourceSessionGeneration)
    {
        lock (Gate)
            return States.ContainsKey(new SessionKey(modId, resourceSessionGeneration));
    }

    internal static void ClearAllNetworkStatesForTests()
    {
        PcCompatNetworkState[] states;
        lock (Gate)
        {
            states = States.Values.ToArray();
            States.Clear();
        }
        foreach (var state in states)
            state.Retire();
    }

    /// <summary>Replacement for <c>new HttpClient()</c>.</summary>
    public static HttpClient CreateHttpClient() => CreateClient(null, disposeInnerHandler: true);

    /// <summary>Replacement for <c>new HttpClient(HttpMessageHandler)</c>.</summary>
    public static HttpClient CreateHttpClientWithHandler(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return CreateClient(handler, disposeInnerHandler: true);
    }

    /// <summary>
    /// Replacement for <c>new HttpClient(HttpMessageHandler, bool)</c>. The Host owns the
    /// outermost handler lifetime, so <paramref name="disposeHandler"/> only decides whether
    /// the MOD's own inner handler is disposed along with the client.
    /// </summary>
    public static HttpClient CreateHttpClientWithHandlerDisposal(
        HttpMessageHandler handler,
        bool disposeHandler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return CreateClient(handler, disposeInnerHandler: disposeHandler);
    }

    /// <summary>
    /// Replacement for <c>new HttpClientHandler()</c>. Pre-bound to this session's cookie
    /// container so two MODs never share session cookies or credentials.
    /// </summary>
    public static HttpClientHandler CreateHttpClientHandler()
    {
        var state = RequireState("MOD network access");
        return new HttpClientHandler
        {
            CookieContainer = state.Cookies,
            UseCookies = true
        };
    }

    /// <summary>
    /// Replacement for <c>new CookieContainer()</c>: returns this session's container instead
    /// of an untracked one. Registered twice (System.Net.Primitives and System) because the
    /// declaring assembly of <c>CookieContainer</c> differs across target frameworks and a
    /// zero-match spec would silently skip rewriting.
    /// </summary>
    public static CookieContainer CreateCookieContainer() =>
        RequireState("MOD network access").Cookies;

    private static HttpClient CreateClient(
        HttpMessageHandler? inner,
        bool disposeInnerHandler)
    {
        var state = RequireState("MOD network access");
        var handler = new SessionHttpHandler(
            state,
            inner ?? new HttpClientHandler
            {
                CookieContainer = state.Cookies,
                UseCookies = true
            },
            disposeInnerHandler);
        var client = new HttpClient(handler, disposeHandler: true);
        state.Track(client);
        return client;
    }

    private static PcCompatNetworkState RequireState(string operationDescription)
    {
        var execution = PcCompatManagedExecutionContext.Current
                        ?? throw new InvalidOperationException(
                            $"{operationDescription} requires an active managed scope.");
        if (execution.Phase == PcCompatManagedExecutionPhase.Disable)
        {
            throw new InvalidOperationException(
                $"{operationDescription} is rejected while mod={execution.ModId} is disabling.");
        }
        var session = new SessionKey(execution.ModId, execution.ResourceSessionGeneration);
        lock (Gate)
        {
            if (States.TryGetValue(session, out var state))
                return state;
        }
        throw new InvalidOperationException(
            $"{operationDescription} state is not bound for mod={session.ModId} " +
            $"generation={session.Generation}.");
    }

    /// <summary>
    /// Per-session network identity: the cookie jar plus every client handed to this
    /// generation. Retirement cancels in-flight requests and disposes the clients, so MOD
    /// unload stops its traffic instead of letting a reply land in a dead generation.
    /// </summary>
    public sealed class PcCompatNetworkState
    {
        private readonly object _sync = new();
        private readonly List<HttpClient> _clients = [];
        private readonly CancellationTokenSource _cancellation = new();
        private bool _retired;

        internal PcCompatNetworkState(string ownerModId, long ownerGeneration)
        {
            OwnerModId = ownerModId;
            OwnerGeneration = ownerGeneration;
        }

        internal string OwnerModId { get; }

        internal long OwnerGeneration { get; }

        internal CookieContainer Cookies { get; } = new();

        internal CancellationToken Cancellation => _cancellation.Token;

        internal void Track(HttpClient client)
        {
            lock (_sync)
            {
                if (_retired)
                {
                    client.Dispose();
                    throw new InvalidOperationException(
                        "PcCompat MOD network state is retired; no new HTTP client may be created.");
                }
                _clients.Add(client);
            }
        }

        internal void Retire()
        {
            HttpClient[] clients;
            lock (_sync)
            {
                if (_retired)
                    return;
                _retired = true;
                clients = _clients.ToArray();
                _clients.Clear();
            }
            // Cancels this session's in-flight requests before the clients are disposed.
            try { _cancellation.Cancel(); } catch { /* already cancelled */ }
            foreach (var client in clients)
            {
                try { client.CancelPendingRequests(); } catch { /* already disposed */ }
                try { client.Dispose(); } catch { /* already disposed */ }
            }
            // The source is deliberately NOT disposed: a request racing retirement reads
            // `Cancellation` to build its linked source, and reading .Token on a disposed
            // source throws ObjectDisposedException instead of surfacing cancellation. One
            // undisposed source per retired generation is bounded and cheap; the wrong
            // exception on a teardown race is not.
        }
    }

    /// <summary>
    /// Validates that every request belongs to the session that received the client, and links
    /// the session cancellation into the request so retirement stops in-flight traffic.
    /// </summary>
    private sealed class SessionHttpHandler(
        PcCompatNetworkState state,
        HttpMessageHandler inner,
        bool disposeInner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // Ownership is bound when the client is constructed: possessing the client is the
            // capability. The ambient managed scope is [ThreadStatic] and is legitimately
            // absent on async continuations — a download's `await GetAsync(...)` resumes on a
            // pool thread — so an absent scope must not be treated as a foreign caller.
            // Only a scope that belongs to a *different* session is rejected, which is the
            // case that actually matters (MOD A reaching for MOD B's client). This mirrors
            // ModRuntimeCapturedScope.ValidateCurrentCaller on the Android Managed side.
            var execution = PcCompatManagedExecutionContext.Current;
            if (execution != null &&
                (!string.Equals(execution.ModId, state.OwnerModId, StringComparison.Ordinal) ||
                 execution.ResourceSessionGeneration != state.OwnerGeneration))
            {
                throw new InvalidOperationException(
                    "MOD HTTP client may only be used by its owning session" +
                    $" owner={state.OwnerModId} generation={state.OwnerGeneration}" +
                    $" caller={execution.ModId} generation={execution.ResourceSessionGeneration}.");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                state.Cancellation);
            return await base.SendAsync(request, linked.Token).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposeInner)
            {
                // Detach the MOD-owned inner handler before the base disposes it.
                InnerHandler = null;
            }
            base.Dispose(disposing);
        }
    }

    private readonly record struct SessionKey(string ModId, long Generation);
}

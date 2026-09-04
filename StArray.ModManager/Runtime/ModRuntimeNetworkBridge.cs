using System.Net;
using System.Net.Http;

namespace StArray.ModManager.Runtime;

/// <summary>
/// Host-owned network boundary used by rewritten Android Managed MOD assemblies.
/// A MOD never constructs its own <see cref="HttpClient"/>; the Host hands it one whose
/// handler, cookie container, credentials and connection pool belong to the calling
/// <see cref="ModDataDomain"/> alone.
/// </summary>
/// <remarks>
/// <para>
/// Only the client-producing entry points need rewriting. Once a client is domain-bound,
/// operations on it (<c>GetAsync</c>, <c>DefaultRequestHeaders</c>, <c>Timeout</c>) and on the
/// objects it returns (<c>HttpResponseMessage</c>, <c>HttpContent</c>) are already isolated,
/// so routing them through the bridge would add cost without changing ownership — the same
/// reasoning that leaves <see cref="Path.Combine(string, string)"/> alone in
/// <see cref="NativeModPathBridge"/>.
/// </para>
/// <para>
/// Requests carry a generation-bound operation lease, so MOD unload cancels in-flight requests
/// and waits for quiescence instead of letting a reply land in a retired generation. This is a
/// cooperative ownership boundary, not a network sandbox: a MOD bypassing the rewrite with raw
/// sockets remains a diagnosable isolation downgrade.
/// </para>
/// </remarks>
public static class ModRuntimeNetworkBridge
{
    /// <summary>Replacement for <c>new HttpClient()</c>.</summary>
    public static HttpClient CreateHttpClient() => CreateClient(null);

    /// <summary>Replacement for <c>new HttpClient(HttpMessageHandler)</c>.</summary>
    public static HttpClient CreateHttpClientWithHandler(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return CreateClient(handler);
    }

    /// <summary>
    /// Replacement for <c>new HttpClient(HttpMessageHandler, bool)</c>. The Host always owns
    /// the outermost handler lifetime, so <paramref name="disposeHandler"/> only decides whether
    /// the MOD's own inner handler is disposed with the client.
    /// </summary>
    public static HttpClient CreateHttpClientWithHandlerDisposal(
        HttpMessageHandler handler,
        bool disposeHandler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return CreateClient(handler, disposeHandler);
    }

    /// <summary>
    /// Replacement for <c>new HttpClientHandler()</c>. Pre-bound to this domain's cookie
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

    /// <summary>Replacement for <c>new CookieContainer()</c>: returns this domain's container.</summary>
    public static CookieContainer CreateCookieContainer() =>
        RequireState("MOD network access").Cookies;

    private static HttpClient CreateClient(
        HttpMessageHandler? inner,
        bool disposeInnerHandler = true)
    {
        var scope = ModRuntimeCapturedScope.Capture("MOD network access");
        var state = RequireState("MOD network access", scope);
        var handler = new DomainHttpHandler(
            scope,
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

    private static DomainNetworkState RequireState(
        string operationDescription,
        ModRuntimeCapturedScope? captured = null)
    {
        var scope = captured ?? ModRuntimeCapturedScope.Capture(operationDescription);
        var token = HookHelper.CurrentDomainToken;
        if (!ModDataDomainRegistry.TryResolve(token, out var domain))
        {
            throw new InvalidOperationException(
                $"{operationDescription} requires an active data domain.");
        }
        return domain.GetOrCreateNetworkState(() => new DomainNetworkState(scope));
    }

    /// <summary>
    /// Per-domain network identity: cookie jar plus the set of clients handed to this
    /// generation, disposed when the generation retires.
    /// </summary>
    internal sealed class DomainNetworkState
    {
        private readonly object _sync = new();
        private readonly List<HttpClient> _clients = [];
        private readonly IModRuntimeTerminalCleanupRegistration? _registration;
        private bool _retired;

        internal DomainNetworkState(ModRuntimeCapturedScope scope)
        {
            Cookies = new CookieContainer();
            // Registration failure means the generation is already retiring; leave the state
            // usable but unregistered so the caller still fails closed on the next request.
            _ = scope.TryRegisterCleanup(DisposeAll, out _registration);
        }

        internal CookieContainer Cookies { get; }

        internal void Track(HttpClient client)
        {
            lock (_sync)
            {
                if (_retired)
                {
                    client.Dispose();
                    throw new InvalidOperationException(
                        "MOD network state is retiring; no new HTTP client may be created.");
                }
                _clients.Add(client);
            }
        }

        private void DisposeAll()
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
            foreach (var client in clients)
            {
                // Cancels this domain's in-flight requests; one failure must not strand the rest.
                try { client.CancelPendingRequests(); } catch { /* already disposed */ }
                try { client.Dispose(); } catch { /* already disposed */ }
            }
            _registration?.Dispose();
        }
    }

    /// <summary>
    /// Wraps every request in a generation-bound operation lease and links the lease's
    /// cancellation to the request, so retirement stops in-flight traffic.
    /// </summary>
    private sealed class DomainHttpHandler(
        ModRuntimeCapturedScope scope,
        HttpMessageHandler inner,
        bool disposeInner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            scope.ValidateCurrentCaller("MOD HTTP client");
            if (!scope.TryBegin("http-request", out var operation) || operation == null)
            {
                throw new InvalidOperationException(
                    $"MOD network request was rejected for retired owner={scope.Key.OwnerId} " +
                    $"generation={scope.Key.Generation}.");
            }

            using (operation)
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                       cancellationToken,
                       operation.CancellationToken))
            {
                using (operation.EnterScope())
                    return await base.SendAsync(request, linked.Token).ConfigureAwait(false);
            }
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
}

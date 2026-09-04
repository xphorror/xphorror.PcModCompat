using System.Net;
using System.Net.Http;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class ModRuntimeNetworkBridgeTests
{
    [SetUp]
    public void SetUp() => ModOwnedResourceRegistry.ClearForTests();

    [TearDown]
    public void TearDown() => ModOwnedResourceRegistry.ClearForTests();

    [Test]
    public void NetworkAccessWithoutDomainScopeIsRejected()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                () => ModRuntimeNetworkBridge.CreateHttpClient(),
                Throws.InvalidOperationException);
            Assert.That(
                () => ModRuntimeNetworkBridge.CreateHttpClientHandler(),
                Throws.InvalidOperationException);
            Assert.That(
                () => ModRuntimeNetworkBridge.CreateCookieContainer(),
                Throws.InvalidOperationException);
        });
    }

    [Test]
    public void EachDomainGetsItsOwnCookieContainer()
    {
        var first = CreateActiveRuntime("cookie-a");
        var second = CreateActiveRuntime("cookie-b");

        CookieContainer a;
        CookieContainer b;
        using (EnterScope(first))
            a = ModRuntimeNetworkBridge.CreateCookieContainer();
        using (EnterScope(second))
            b = ModRuntimeNetworkBridge.CreateCookieContainer();

        a.Add(new Cookie("session", "a", "/", "example.com"));

        Assert.Multiple(() =>
        {
            Assert.That(a, Is.Not.SameAs(b), "two MODs must not share a cookie jar");
            Assert.That(b.Count, Is.Zero, "one MOD's cookie must not appear in another's jar");
        });
    }

    [Test]
    public void SameDomainReusesItsCookieContainerAcrossClients()
    {
        var mod = CreateActiveRuntime("cookie-stable");
        using var scope = EnterScope(mod);

        var first = ModRuntimeNetworkBridge.CreateCookieContainer();
        var second = ModRuntimeNetworkBridge.CreateCookieContainer();

        Assert.That(first, Is.SameAs(second), "network identity is per domain, not per call");
    }

    [Test]
    public void HandlerIsPreBoundToTheDomainCookieContainer()
    {
        var mod = CreateActiveRuntime("handler-bound");
        using var scope = EnterScope(mod);

        var jar = ModRuntimeNetworkBridge.CreateCookieContainer();
        using var handler = ModRuntimeNetworkBridge.CreateHttpClientHandler();

        Assert.Multiple(() =>
        {
            Assert.That(handler.CookieContainer, Is.SameAs(jar));
            Assert.That(handler.UseCookies, Is.True);
        });
    }

    [Test]
    public async Task RequestTakesAnOperationLeaseForItsDuration()
    {
        var mod = CreateActiveRuntime("lease-request");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        HttpClient client;
        using (EnterScope(mod))
        {
            client = ModRuntimeNetworkBridge.CreateHttpClientWithHandler(
                new GatedHandler(entered, gate.Task));
        }

        var send = client.GetAsync("https://example.invalid/probe");
        await entered.Task;

        Assert.That(
            mod.Session.Snapshot().ActiveOperations,
            Is.EqualTo(1),
            "an in-flight request must hold a generation-bound operation");

        gate.SetResult();
        using var response = await send;

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(mod.Session.Snapshot().ActiveOperations, Is.Zero);
        });
        client.Dispose();
    }

    [Test]
    public void RetirementCancelsInFlightRequestsAndBlocksNewOnes()
    {
        var mod = CreateActiveRuntime("retire-request");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource();

        HttpClient client;
        using (EnterScope(mod))
            client = ModRuntimeNetworkBridge.CreateHttpClientWithHandler(
                new GatedHandler(entered, never.Task));

        var send = client.GetAsync("https://example.invalid/probe");
        Assert.That(entered.Task.Wait(TimeSpan.FromSeconds(5)), Is.True);

        Assert.That(mod.Session.TryBeginRetirement(mod.Key), Is.True);

        Assert.Multiple(() =>
        {
            // the in-flight request is cancelled rather than landing in a retired generation
            Assert.That(
                () => send.GetAwaiter().GetResult(),
                Throws.InstanceOf<OperationCanceledException>()
                    .Or.InstanceOf<TaskCanceledException>());

            // and the retiring generation refuses to start another one
            using var stale = HookHelper.EnterOwnerScope(mod.Key.OwnerId, mod.Session, mod.Key);
            Assert.That(
                () => client.GetAsync("https://example.invalid/again").GetAwaiter().GetResult(),
                Throws.Exception);
        });
        client.Dispose();
    }

    [Test]
    public void ClientBelongingToAnotherModIsRejected()
    {
        var owner = CreateActiveRuntime("net-owner");
        var other = CreateActiveRuntime("net-other");

        HttpClient client;
        using (EnterScope(owner))
            client = ModRuntimeNetworkBridge.CreateHttpClientWithHandler(
                new GatedHandler(null, Task.CompletedTask));

        using (EnterScope(other))
        {
            Assert.That(
                () => client.GetAsync("https://example.invalid/probe").GetAwaiter().GetResult(),
                Throws.Exception.With.Message.Contains(owner.Key.OwnerId));
        }
        client.Dispose();
    }

    private static (ModRuntimeSession Session, ModRuntimeKey Key) CreateActiveRuntime(string id)
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, id);
        Assert.That(session.TryPublishActive(key), Is.True);
        return (session, key);
    }

    private static IDisposable EnterScope((ModRuntimeSession Session, ModRuntimeKey Key) runtime) =>
        HookHelper.EnterOwnerScope(runtime.Key.OwnerId, runtime.Session, runtime.Key);

    /// <summary>Never touches the network: signals when entered, then waits on a gate.</summary>
    private sealed class GatedHandler(
        TaskCompletionSource? entered,
        Task gate) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            entered?.TrySetResult();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

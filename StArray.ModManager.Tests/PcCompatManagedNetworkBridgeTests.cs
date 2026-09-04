using System.Net;
using System.Net.Http;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class PcCompatManagedNetworkBridgeTests
{
    private const string ModId = "pccompat.net.test";
    private const string OtherModId = "pccompat.net.other";
    private const long Generation = 73;

    [SetUp]
    public void SetUp()
    {
        PcCompatManagedNetworkBridge.ClearAllNetworkStatesForTests();
    }

    [TearDown]
    public void TearDown()
    {
        PcCompatManagedNetworkBridge.ClearAllNetworkStatesForTests();
    }

    [Test]
    public void NetworkAccessWithoutManagedScopeIsRejected()
    {
        PcCompatManagedNetworkBridge.BindNetworkState(ModId, Generation);

        Assert.Multiple(() =>
        {
            Assert.That(
                () => PcCompatManagedNetworkBridge.CreateHttpClient(),
                Throws.InvalidOperationException);
            Assert.That(
                () => PcCompatManagedNetworkBridge.CreateHttpClientHandler(),
                Throws.InvalidOperationException);
            Assert.That(
                () => PcCompatManagedNetworkBridge.CreateCookieContainer(),
                Throws.InvalidOperationException);
        });
    }

    [Test]
    public void NetworkAccessWithoutBoundStateIsRejected()
    {
        using var scope = EnterEnable(ModId, Generation);
        Assert.That(
            () => PcCompatManagedNetworkBridge.CreateHttpClient(),
            Throws.InvalidOperationException.With.Message.Contains("not bound"));
    }

    [Test]
    public void DisablingSessionLosesNetworkAccess()
    {
        PcCompatManagedNetworkBridge.BindNetworkState(ModId, Generation);
        using (EnterEnable(ModId, Generation))
            Assert.That(() => PcCompatManagedNetworkBridge.CreateHttpClient(), Throws.Nothing);

        using var disabling = PcCompatManagedExecutionContext.Enter(
            new PcCompatManagedExecutionState(
                ModId,
                Generation,
                PcCompatManagedExecutionPhase.Disable));
        Assert.That(
            () => PcCompatManagedNetworkBridge.CreateHttpClient(),
            Throws.InvalidOperationException.With.Message.Contains("disabling"));
    }

    [Test]
    public void SessionsDoNotShareCookieIdentities()
    {
        PcCompatManagedNetworkBridge.BindNetworkState(ModId, Generation);
        PcCompatManagedNetworkBridge.BindNetworkState(OtherModId, Generation);

        CookieContainer owned;
        CookieContainer other;
        using (EnterEnable(ModId, Generation))
        {
            owned = PcCompatManagedNetworkBridge.CreateCookieContainer();
            Assert.That(
                PcCompatManagedNetworkBridge.CreateCookieContainer(),
                Is.SameAs(owned),
                "one MOD session must reuse a single network identity");
            var handler = PcCompatManagedNetworkBridge.CreateHttpClientHandler();
            Assert.That(handler.CookieContainer, Is.SameAs(owned));
        }
        using (EnterEnable(OtherModId, Generation))
            other = PcCompatManagedNetworkBridge.CreateCookieContainer();

        // Session cookies set through one identity must never be visible to another MOD.
        owned.Add(new Uri("http://pccompat-net-test.invalid/"), new Cookie("sid", "a"));
        Assert.Multiple(() =>
        {
            Assert.That(owned.GetCookies(new Uri("http://pccompat-net-test.invalid/")), Is.Not.Empty);
            Assert.That(
                other.GetCookies(new Uri("http://pccompat-net-test.invalid/")),
                Is.Empty,
                "another MOD session must not see this session's cookies");
        });
    }

    [Test]
    public void CrossOwnerSendIsRejectedBeforeAnyRequestIsIssued()
    {
        PcCompatManagedNetworkBridge.BindNetworkState(ModId, Generation);
        PcCompatManagedNetworkBridge.BindNetworkState(OtherModId, Generation);
        HttpClient foreign;
        using (EnterEnable(ModId, Generation))
            foreign = PcCompatManagedNetworkBridge.CreateHttpClient();

        // Another MOD picking up the client is rejected before the request leaves the host;
        // the unreachable host name proves the rejection happens at the ownership gate.
        using var otherScope = EnterEnable(OtherModId, Generation);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await foreign.GetAsync("http://pccompat-net-cross.invalid/"));
    }

    [Test]
    public void AsyncContinuationWithoutAmbientScopeIsNotTreatedAsAForeignCaller()
    {
        PcCompatManagedNetworkBridge.BindNetworkState(ModId, Generation);
        var stub = new StubHandler();
        HttpClient client;
        using (EnterEnable(ModId, Generation))
            client = PcCompatManagedNetworkBridge.CreateHttpClientWithHandler(stub);

        // The managed scope is [ThreadStatic]: a real downloader's `await GetAsync(...)`
        // resumes without it. Ownership was bound when the client was created, so the request
        // must reach the pipeline instead of being rejected as a foreign caller — this is
        // exactly the JALib self-updater shape the slice exists to isolate.
        Assert.That(PcCompatManagedExecutionContext.Current, Is.Null);
        var response = client.GetAsync("http://pccompat-net-stub.invalid/").GetAwaiter().GetResult();

        Assert.Multiple(() =>
        {
            Assert.That(stub.Calls, Is.EqualTo(1), "the request must reach the handler pipeline");
            Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));
        });
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        internal int Calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    [Test]
    public void ClearedNetworkStateDisposesTrackedClients()
    {
        PcCompatManagedNetworkBridge.BindNetworkState(ModId, Generation);
        HttpClient client;
        using (EnterEnable(ModId, Generation))
            client = PcCompatManagedNetworkBridge.CreateHttpClient();

        PcCompatManagedNetworkBridge.ClearNetworkState(ModId, Generation);

        // Retirement cancels and disposes the generation's clients; a late reply cannot land
        // in a dead generation because nothing can be sent anymore.
        Assert.That(async () => await client.GetAsync("http://pccompat-net-retired.invalid/"),
            Throws.TypeOf<ObjectDisposedException>());

        // And the retired generation cannot mint new clients either.
        using var scope = EnterEnable(ModId, Generation);
        Assert.That(
            () => PcCompatManagedNetworkBridge.CreateHttpClient(),
            Throws.InvalidOperationException.With.Message.Contains("not bound"));
    }

    private static IDisposable EnterEnable(string modId, long generation) =>
        PcCompatManagedExecutionContext.Enter(
            new PcCompatManagedExecutionState(
                modId,
                generation,
                PcCompatManagedExecutionPhase.Enable));
}

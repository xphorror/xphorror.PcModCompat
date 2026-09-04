using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class PcCompatManagedEventSubscriptionBridgeTests
{
    private const string ModId = "pccompat.events.test";
    private const long Generation = 97;
    private const string EventKey =
        "StArray.ModManager.Tests!StArray.ModManager.Tests." +
        nameof(PcCompatManagedEventSubscriptionBridgeTests) + "+" +
        nameof(TestEventHost) + "::" + nameof(TestEventHost.Ping);
    private const string ConvertedEventKey =
        "StArray.ModManager.Tests!StArray.ModManager.Tests." +
        nameof(PcCompatManagedEventSubscriptionBridgeTests) + "+" +
        nameof(TestEventHost) + "::" + nameof(TestEventHost.ConvertedPing);
    private const string ProxyEventKey =
        "StArray.ModManager.Tests!StArray.ModManager.Tests." +
        nameof(PcCompatManagedEventSubscriptionBridgeTests) + "+" +
        nameof(TestEventHost) + "::ProxyPing";
    private bool _callbacksEnabled;

    [SetUp]
    public void SetUp()
    {
        PcCompatManagedEventSubscriptionBridge.ClearAllForTests();
        TestEventHost.Reset();
        _callbacksEnabled = true;
        PcCompatManagedEventSubscriptionBridge.RegisterCallbackScopeProvider(owner =>
            _callbacksEnabled
                ? PcCompatManagedExecutionContext.Enter(owner)
                : null);
    }

    [TearDown]
    public void TearDown()
    {
        PcCompatManagedEventSubscriptionBridge.ClearAllForTests();
        TestEventHost.Reset();
    }

    [Test]
    public void SubscribeWithoutManagedScopeIsRejected()
    {
        Assert.That(
            () => PcCompatManagedEventSubscriptionBridge.Subscribe(
                (Action)(() => { }), EventKey),
            Throws.InvalidOperationException);
    }

    [Test]
    public void SubscribeDuringDisableIsRejected()
    {
        using var disabling = PcCompatManagedExecutionContext.Enter(
            new PcCompatManagedExecutionState(
                ModId,
                Generation,
                PcCompatManagedExecutionPhase.Disable));
        Assert.That(
            () => PcCompatManagedEventSubscriptionBridge.Subscribe(
                (Action)(() => { }), EventKey),
            Throws.InvalidOperationException.With.Message.Contains("disabling"));
        Assert.That(TestEventHost.SubscriberCount, Is.EqualTo(0));
    }

    [Test]
    public void SubscribeForwardsAndRecordsTheHandler()
    {
        var calls = 0;
        // Explicitly typed: an untyped lambda would take its natural Func<int> type when the
        // bridge parameter is object.
        Action handler = () => calls++;
        using var scope = EnterEnable(ModId, Generation);
        PcCompatManagedEventSubscriptionBridge.Subscribe(handler, EventKey);

        Assert.Multiple(() =>
        {
            Assert.That(TestEventHost.SubscriberCount, Is.EqualTo(1));
            TestEventHost.Raise();
            Assert.That(calls, Is.EqualTo(1));
        });
    }

    [Test]
    public void IncompatibleAccessorDelegateUsesRegisteredConverter()
    {
        var calls = 0;
        Action handler = () => calls++;
        var conversions = 0;
        PcCompatManagedEventSubscriptionBridge.RegisterDelegateConverter((source, target) =>
        {
            conversions++;
            Assert.That(source, Is.Not.SameAs(handler));
            Assert.That(source.GetType(), Is.EqualTo(handler.GetType()));
            Assert.That(target, Is.EqualTo(typeof(ForeignAction)));
            return new ForeignAction(((Action)source).Invoke);
        });

        using (EnterEnable(ModId, Generation))
            PcCompatManagedEventSubscriptionBridge.Subscribe(handler, ConvertedEventKey);

        Assert.Multiple(() =>
        {
            Assert.That(conversions, Is.EqualTo(1));
            Assert.That(TestEventHost.ConvertedSubscriberCount, Is.EqualTo(1));
            TestEventHost.RaiseConverted();
            Assert.That(calls, Is.EqualTo(1));
        });
    }

    [Test]
    public void Il2CppProxyDelegateObjectIsScopedConvertedAndUnsubscribed()
    {
        var calls = 0;
        PcCompatManagedExecutionState? observed = null;
        var handler = new ProxyAction(() =>
        {
            calls++;
            observed = PcCompatManagedExecutionContext.Current;
        });
        var conversions = 0;
        PcCompatManagedEventSubscriptionBridge.RegisterSourceDelegateResolver(source =>
            source is ProxyAction proxy ? proxy.Callback : null);
        PcCompatManagedEventSubscriptionBridge.RegisterDelegateConverter((source, target) =>
        {
            conversions++;
            Assert.That(source, Is.TypeOf<Action>());
            Assert.That(target, Is.EqualTo(typeof(ProxyAction)));
            return new ProxyAction(((Action)source).Invoke);
        });

        using (EnterEnable(ModId, Generation))
            PcCompatManagedEventSubscriptionBridge.Subscribe(handler, ProxyEventKey);

        Assert.That(PcCompatManagedExecutionContext.Current, Is.Null);
        TestEventHost.RaiseProxy();
        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(conversions, Is.EqualTo(1));
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed!.ModId, Is.EqualTo(ModId));
            Assert.That(observed.ResourceSessionGeneration, Is.EqualTo(Generation));
            Assert.That(observed.Phase, Is.EqualTo(PcCompatManagedExecutionPhase.Update));
            Assert.That(PcCompatManagedExecutionContext.Current, Is.Null);
            Assert.That(TestEventHost.ProxySubscriberCount, Is.EqualTo(1));
        });

        using (EnterEnable(ModId, Generation))
            PcCompatManagedEventSubscriptionBridge.Unsubscribe(handler, ProxyEventKey);

        Assert.Multiple(() =>
        {
            Assert.That(conversions, Is.EqualTo(1), "-= must reuse the wrapper recorded by +=");
            Assert.That(TestEventHost.ProxySubscriberCount, Is.Zero);
            TestEventHost.RaiseProxy();
            Assert.That(calls, Is.EqualTo(1));
        });

        using (EnterEnable(ModId, Generation))
            PcCompatManagedEventSubscriptionBridge.Subscribe(handler, ProxyEventKey);
        PcCompatManagedEventSubscriptionBridge.RetireOwner(ModId, Generation);

        Assert.Multiple(() =>
        {
            Assert.That(conversions, Is.EqualTo(1), "retirement must reuse the cached wrapper");
            Assert.That(TestEventHost.ProxySubscriberCount, Is.Zero);
            TestEventHost.RaiseProxy();
            Assert.That(calls, Is.EqualTo(1));
        });
    }

    [Test]
    public void ConvertedDelegateIsReusedForDuplicateSubscriptionsAndRetirement()
    {
        var calls = 0;
        Action handler = () => calls++;
        var conversions = 0;
        ForeignAction? converted = null;
        PcCompatManagedEventSubscriptionBridge.RegisterDelegateConverter((source, target) =>
        {
            conversions++;
            converted = new ForeignAction(((Action)source).Invoke);
            return converted;
        });

        using (EnterEnable(ModId, Generation))
        {
            PcCompatManagedEventSubscriptionBridge.Subscribe(handler, ConvertedEventKey);
            PcCompatManagedEventSubscriptionBridge.Subscribe(handler, ConvertedEventKey);
        }

        Assert.That(conversions, Is.EqualTo(1));
        Assert.That(TestEventHost.ConvertedSubscriberCount, Is.EqualTo(2));
        TestEventHost.RaiseConverted();
        Assert.That(calls, Is.EqualTo(2));

        PcCompatManagedEventSubscriptionBridge.RetireOwner(ModId, Generation);
        Assert.Multiple(() =>
        {
            Assert.That(TestEventHost.ConvertedSubscriberCount, Is.EqualTo(0));
            TestEventHost.RaiseConverted();
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(converted, Is.Not.Null);
        });
    }

    [Test]
    public void UnsubscribeUsesTheConvertedWrapperAndUpdatesRegistry()
    {
        var calls = 0;
        Action handler = () => calls++;
        var conversions = 0;
        PcCompatManagedEventSubscriptionBridge.RegisterDelegateConverter((source, target) =>
        {
            conversions++;
            return new ForeignAction(((Action)source).Invoke);
        });

        using (EnterEnable(ModId, Generation))
        {
            PcCompatManagedEventSubscriptionBridge.Subscribe(handler, ConvertedEventKey);
            PcCompatManagedEventSubscriptionBridge.Unsubscribe(handler, ConvertedEventKey);
        }

        Assert.Multiple(() =>
        {
            Assert.That(conversions, Is.EqualTo(1));
            Assert.That(TestEventHost.ConvertedSubscriberCount, Is.EqualTo(0));
            TestEventHost.RaiseConverted();
            Assert.That(calls, Is.Zero);
            Assert.DoesNotThrow(() =>
                PcCompatManagedEventSubscriptionBridge.RetireOwner(ModId, Generation));
        });
    }

    [Test]
    public void IncompatibleAccessorDelegateFailsClosedWithoutConverter()
    {
        using var scope = EnterEnable(ModId, Generation);
        Assert.That(
            () => PcCompatManagedEventSubscriptionBridge.Subscribe(
                (Action)(() => { }), ConvertedEventKey),
            Throws.InvalidOperationException.With.Message.Contains("converter"));
        Assert.That(TestEventHost.ConvertedSubscriberCount, Is.EqualTo(0));
    }

    [Test]
    public void RetireOwnerRemovesEveryRecordedSubscription()
    {
        // Duplicate subscription of one handler instance stays two live invocations until
        // retirement; the registry must track both so nothing dangles afterwards.
        var calls = 0;
        Action handler = () => calls++;
        using (EnterEnable(ModId, Generation))
        {
            PcCompatManagedEventSubscriptionBridge.Subscribe(handler, EventKey);
            PcCompatManagedEventSubscriptionBridge.Subscribe(handler, EventKey);
        }

        Assert.That(TestEventHost.SubscriberCount, Is.EqualTo(2));
        PcCompatManagedEventSubscriptionBridge.RetireOwner(ModId, Generation);

        Assert.Multiple(() =>
        {
            Assert.That(TestEventHost.SubscriberCount, Is.EqualTo(0));
            TestEventHost.Raise();
            Assert.That(calls, Is.EqualTo(0), "retired handlers must not fire");
            // Retiring twice is a no-op.
            Assert.DoesNotThrow(() =>
                PcCompatManagedEventSubscriptionBridge.RetireOwner(ModId, Generation));
        });
    }

    [Test]
    public void RetirementIsScopedToTheOwnerGeneration()
    {
        var survivorCalls = 0;
        Action survivorHandler = () => survivorCalls++;
        using (EnterEnable(ModId, Generation - 1))
            PcCompatManagedEventSubscriptionBridge.Subscribe(() => { }, EventKey);
        using (EnterEnable(ModId, Generation))
            PcCompatManagedEventSubscriptionBridge.Subscribe(survivorHandler, EventKey);

        PcCompatManagedEventSubscriptionBridge.RetireOwner(ModId, Generation - 1);

        Assert.Multiple(() =>
        {
            Assert.That(TestEventHost.SubscriberCount, Is.EqualTo(1));
            TestEventHost.Raise();
            Assert.That(survivorCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public void ExternalEventCallbackRestoresCapturedOwnerInUpdatePhase()
    {
        PcCompatManagedExecutionState? observed = null;
        Action handler = () => observed = PcCompatManagedExecutionContext.Current;
        using (EnterEnable(ModId, Generation))
        {
            PcCompatManagedEventSubscriptionBridge.Subscribe(
                handler,
                EventKey);
        }

        Assert.That(PcCompatManagedExecutionContext.Current, Is.Null);
        TestEventHost.Raise();

        Assert.Multiple(() =>
        {
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed!.ModId, Is.EqualTo(ModId));
            Assert.That(observed.ResourceSessionGeneration, Is.EqualTo(Generation));
            Assert.That(observed.Phase, Is.EqualTo(PcCompatManagedExecutionPhase.Update));
            Assert.That(PcCompatManagedExecutionContext.Current, Is.Null);
        });
    }

    [Test]
    public void ExternalEventCallbackIsDroppedWhenSessionScopeCannotBeEntered()
    {
        var calls = 0;
        Action handler = () => calls++;
        using (EnterEnable(ModId, Generation))
            PcCompatManagedEventSubscriptionBridge.Subscribe(handler, EventKey);

        _callbacksEnabled = false;
        TestEventHost.Raise();

        Assert.That(calls, Is.Zero);
    }

    [Test]
    public void SubscribeFailsClosedOnUnknownEventIdentity()
    {
        using var scope = EnterEnable(ModId, Generation);
        Assert.Multiple(() =>
        {
            Assert.That(
                () => PcCompatManagedEventSubscriptionBridge.Subscribe(
                    (Action)(() => { }), "no-separator"),
                Throws.InvalidOperationException);
            Assert.That(
                () => PcCompatManagedEventSubscriptionBridge.Subscribe(
                    (Action)(() => { }),
                    "StArray.ModManager.Tests!StArray.ModManager.Tests.NoSuchType::Ping"),
                Throws.InvalidOperationException);
            Assert.That(TestEventHost.SubscriberCount, Is.EqualTo(0));
        });
    }

    private static IDisposable EnterEnable(string modId, long generation) =>
        PcCompatManagedExecutionContext.Enter(
            new PcCompatManagedExecutionState(
                modId,
                generation,
                PcCompatManagedExecutionPhase.Enable));

    internal static class TestEventHost
    {
        // Public so the bridge's public-static accessor lookup can resolve add_/remove_.
        public static event Action? Ping;
        public static event ForeignAction? ConvertedPing;
        private static readonly List<ProxyAction> ProxyHandlers = [];

        internal static int SubscriberCount => Ping?.GetInvocationList().Length ?? 0;
        internal static int ConvertedSubscriberCount => ConvertedPing?.GetInvocationList().Length ?? 0;
        internal static int ProxySubscriberCount => ProxyHandlers.Count;

        public static void add_ProxyPing(ProxyAction handler) => ProxyHandlers.Add(handler);
        public static void remove_ProxyPing(ProxyAction handler) => ProxyHandlers.Remove(handler);

        internal static void Raise() => Ping?.Invoke();
        internal static void RaiseConverted() => ConvertedPing?.Invoke();
        internal static void RaiseProxy()
        {
            foreach (var handler in ProxyHandlers.ToArray())
                handler.Invoke();
        }

        internal static void Reset()
        {
            foreach (var subscriber in Ping?.GetInvocationList() ?? [])
                Ping -= (Action)subscriber;
            foreach (var subscriber in ConvertedPing?.GetInvocationList() ?? [])
                ConvertedPing -= (ForeignAction)subscriber;
            ProxyHandlers.Clear();
        }
    }

    public delegate void ForeignAction();

    public sealed class ProxyAction(Action callback)
    {
        public Action Callback { get; } = callback;

        public void Invoke() => Callback();
    }
}

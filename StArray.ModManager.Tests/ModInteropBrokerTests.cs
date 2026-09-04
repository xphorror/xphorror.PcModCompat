using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using StArray.ModManager.Interop;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class ModInteropBrokerTests
{
    [SetUp]
    public void SetUp()
    {
        ModOwnedResourceRegistry.ClearForTests();
        PcCompatRuntime.RegisterUnityMainThreadProbe(null);
        PcCompatRuntime.RegisterUnityMainWorkScheduler(null);
    }

    [TearDown]
    public void TearDown()
    {
        PcCompatRuntime.RegisterUnityMainThreadProbe(null);
        PcCompatRuntime.RegisterUnityMainWorkScheduler(null);
        ModOwnedResourceRegistry.ClearForTests();
    }

    [Test]
    public void SubscriberMayLoadBeforePublisherAndReceivesAsynchronously()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var publisherId = "publisher-" + suffix;
        var contractId = $"mod/{publisherId}/events";
        var subscriberRuntime = CreateRuntime("subscriber-" + suffix);
        var publisherRuntime = CreateRuntime(publisherId);
        using var delivered = new ManualResetEventSlim();
        InteropMessage? received = null;

        ModInteropSubscription? subscription;
        using (Enter(subscriberRuntime))
        {
            Assert.That(ModInterop.TrySubscribe(
                new InteropSubscriptionRequest(contractId),
                message =>
                {
                    received = message;
                    delivered.Set();
                },
                out subscription,
                out var error), Is.True, error.ToString());
        }

        ModInteropPublisher? publisher;
        using (Enter(publisherRuntime))
        {
            Assert.That(ModInterop.TryOpenPublisher(
                new InteropContractDeclaration("events"),
                out publisher,
                out var error), Is.True, error.ToString());
            Assert.That(publisher!.TryPublish([1, 2, 3], out error), Is.True, error.ToString());
        }

        Assert.That(delivered.Wait(TimeSpan.FromSeconds(2)), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(received, Is.Not.Null);
            Assert.That(received!.Payload.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(received.PublisherId, Is.EqualTo(publisherId));
            Assert.That(received.Contract.ContractId, Is.EqualTo(contractId));
        });

        subscription!.Dispose();
        publisher!.Dispose();
        Retire(subscriberRuntime);
        Retire(publisherRuntime);
    }

    [Test]
    public void VirtualInputPreservesRecordedTimeAndAssignsSequence()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var consumerRuntime = CreateRuntime("consumer-" + suffix);
        var publisherRuntime = CreateRuntime("replay-" + suffix);
        var batches = new ConcurrentQueue<VirtualInputBatch>();
        using var ended = new ManualResetEventSlim();

        ModInteropSubscription? subscription;
        using (Enter(consumerRuntime))
        {
            Assert.That(ModInterop.TrySubscribe(
                new InteropSubscriptionRequest(ModInteropConstants.VirtualInputPlaybackV2, 2)
                {
                    QueueCapacity = ModInteropConstants.VirtualInputQueueCapacity
                },
                message =>
                {
                    if (message.VirtualInput is { } batch)
                    {
                        batches.Enqueue(batch);
                        if (batch.Kind == VirtualInputBatchKind.Ended)
                            ended.Set();
                    }
                },
                out subscription,
                out var error), Is.True, error.ToString());
        }

        VirtualInputPlaybackPublisher? publisher;
        VirtualInputPlaybackSession? playback;
        using (Enter(publisherRuntime))
        {
            Assert.That(ModInterop.TryOpenVirtualInputPlayback(out publisher, out var error),
                Is.True, error.ToString());
            Assert.That(publisher!.TryStart(out playback, out error), Is.True, error.ToString());
            Assert.That(playback!.TryPublish(new VirtualInputEvent(
                999,
                123_456,
                VirtualInputDevice.Keyboard,
                VirtualInputPhase.Down,
                "Space",
                -1,
                0,
                0,
                0,
                0,
                0), out error), Is.True, error.ToString());
            playback.Complete();
        }

        Assert.That(ended.Wait(TimeSpan.FromSeconds(2)), Is.True);
        var input = batches.SelectMany(batch => batch.Events)
            .Single(candidate => candidate.Phase == VirtualInputPhase.Down);
        Assert.Multiple(() =>
        {
            Assert.That(input.OffsetMicroseconds, Is.EqualTo(123_456));
            Assert.That(input.Sequence, Is.Not.Zero);
            Assert.That(input.Sequence, Is.Not.EqualTo(999));
            Assert.That(batches.Select(batch => batch.Kind), Does.Contain(VirtualInputBatchKind.Started));
            Assert.That(batches.Select(batch => batch.Kind), Does.Contain(VirtualInputBatchKind.Ended));
        });

        subscription!.Dispose();
        publisher!.Dispose();
        Retire(consumerRuntime);
        Retire(publisherRuntime);
    }

    [Test]
    public void LateVirtualInputSubscriberReceivesHeldSnapshotWithoutHistoryReplay()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var publisherRuntime = CreateRuntime("replay-late-" + suffix);
        var consumerRuntime = CreateRuntime("consumer-late-" + suffix);
        VirtualInputPlaybackPublisher? publisher;
        VirtualInputPlaybackSession? playback;

        using (Enter(publisherRuntime))
        {
            Assert.That(ModInterop.TryOpenVirtualInputPlayback(out publisher, out var error),
                Is.True, error.ToString());
            Assert.That(publisher!.TryStart(out playback, out error), Is.True, error.ToString());
            Assert.That(playback!.TryPublish(new VirtualInputEvent(
                0, 42_000, VirtualInputDevice.Keyboard, VirtualInputPhase.Down,
                "KeyA", -1, 0, 0, 0, 0, 0), out error), Is.True, error.ToString());
        }

        var batches = new ConcurrentQueue<VirtualInputBatch>();
        using var snapshotReady = new ManualResetEventSlim();
        ModInteropSubscription? subscription;
        using (Enter(consumerRuntime))
        {
            Assert.That(ModInterop.TrySubscribe(
                new InteropSubscriptionRequest(ModInteropConstants.VirtualInputPlaybackV2, 2)
                {
                    QueueCapacity = ModInteropConstants.VirtualInputQueueCapacity
                },
                message =>
                {
                    if (message.VirtualInput is not { } batch)
                        return;
                    batches.Enqueue(batch);
                    if (batch.Kind == VirtualInputBatchKind.Snapshot)
                        snapshotReady.Set();
                },
                out subscription,
                out var error), Is.True, error.ToString());
        }

        Assert.That(snapshotReady.Wait(TimeSpan.FromSeconds(2)), Is.True);
        var snapshot = batches.Single(batch => batch.Kind == VirtualInputBatchKind.Snapshot);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Events, Has.Count.EqualTo(1));
            Assert.That(snapshot.Events[0].CanonicalKey, Is.EqualTo("KeyA"));
            Assert.That(snapshot.Events[0].Phase, Is.EqualTo(VirtualInputPhase.Down));
            Assert.That(batches.Any(batch => batch.Kind == VirtualInputBatchKind.Events), Is.False);
        });

        playback!.Cancel();
        subscription!.Dispose();
        publisher!.Dispose();
        Retire(consumerRuntime);
        Retire(publisherRuntime);
    }

    [Test]
    public void QueueOverflowBreaksOnlySlowSubscriber()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var publisherId = "overflow-publisher-" + suffix;
        var publisherRuntime = CreateRuntime(publisherId);
        var slowRuntime = CreateRuntime("slow-" + suffix);
        var healthyRuntime = CreateRuntime("healthy-" + suffix);
        using var slowEntered = new ManualResetEventSlim();
        using var releaseSlow = new ManualResetEventSlim();
        using var healthyDelivered = new CountdownEvent(3);
        using var cancellationDelivered = new ManualResetEventSlim();
        var cancellationCount = 0;

        ModInteropSubscription? slow;
        using (Enter(slowRuntime))
        {
            Assert.That(ModInterop.TrySubscribe(
                new InteropSubscriptionRequest($"mod/{publisherId}/events") { QueueCapacity = 1 },
                message =>
                {
                    if (message.IsCancellation)
                    {
                        Interlocked.Increment(ref cancellationCount);
                        cancellationDelivered.Set();
                        return;
                    }
                    slowEntered.Set();
                    releaseSlow.Wait(TimeSpan.FromSeconds(2));
                },
                out slow,
                out var error), Is.True, error.ToString());
        }
        ModInteropSubscription? healthy;
        using (Enter(healthyRuntime))
        {
            Assert.That(ModInterop.TrySubscribe(
                new InteropSubscriptionRequest($"mod/{publisherId}/events") { QueueCapacity = 8 },
                _ => healthyDelivered.Signal(),
                out healthy,
                out var error), Is.True, error.ToString());
        }

        ModInteropPublisher? publisher;
        using (Enter(publisherRuntime))
        {
            Assert.That(ModInterop.TryOpenPublisher(
                new InteropContractDeclaration("events"),
                out publisher,
                out var error), Is.True, error.ToString());
            Assert.That(publisher!.TryPublish([1], out error), Is.True, error.ToString());
        }
        Assert.That(slowEntered.Wait(TimeSpan.FromSeconds(2)), Is.True);
        using (Enter(publisherRuntime))
        {
            Assert.That(publisher!.TryPublish([2], out var error), Is.True, error.ToString());
            Assert.That(publisher.TryPublish([3], out error), Is.True, error.ToString());
        }

        Assert.That(SpinWait.SpinUntil(() => slow!.IsCircuitBroken, TimeSpan.FromSeconds(2)), Is.True);
        releaseSlow.Set();
        Assert.That(healthyDelivered.Wait(TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(healthy!.IsCircuitBroken, Is.False);
        Assert.That(cancellationDelivered.Wait(TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(cancellationCount, Is.EqualTo(1));

        slow.Dispose();
        healthy.Dispose();
        publisher!.Dispose();
        Retire(slowRuntime);
        Retire(healthyRuntime);
        Retire(publisherRuntime);
    }

    [Test]
    public async Task RequestResponseUsesProviderGenerationAndNeverBlocksCallerThread()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var providerId = "rpc-provider-" + suffix;
        var providerRuntime = CreateRuntime(providerId);
        var callerRuntime = CreateRuntime("rpc-caller-" + suffix);
        ModInteropPublisher? publisher;
        using (Enter(providerRuntime))
        {
            Assert.That(ModInterop.TryOpenPublisher(
                new InteropContractDeclaration("echo"),
                out publisher,
                out var error), Is.True, error.ToString());
            Assert.That(publisher!.TrySetRequestHandler(
                request => ValueTask.FromResult(InteropResponse.Success(request.Payload.Span)),
                out error), Is.True, error.ToString());
        }

        Task<InteropResponse> pending;
        using (Enter(callerRuntime))
        {
            pending = ModInterop.RequestAsync(new InteropRequest(
                $"mod/{providerId}/echo",
                1,
                [7, 8, 9],
                TimeSpan.FromSeconds(2)));
        }
        var response = await pending;
        Assert.Multiple(() =>
        {
            Assert.That(response.Succeeded, Is.True);
            Assert.That(response.Payload.ToArray(), Is.EqualTo(new byte[] { 7, 8, 9 }));
        });

        publisher!.Dispose();
        Retire(callerRuntime);
        Retire(providerRuntime);
    }

    [Test]
    public void PublisherRetirementCancelsActiveVirtualInputAndEndsSession()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var consumerRuntime = CreateRuntime("retire-consumer-" + suffix);
        var publisherRuntime = CreateRuntime("retire-replay-" + suffix);
        var batches = new ConcurrentQueue<VirtualInputBatch>();
        using var ended = new ManualResetEventSlim();

        ModInteropSubscription? subscription;
        using (Enter(consumerRuntime))
        {
            Assert.That(ModInterop.TrySubscribe(
                new InteropSubscriptionRequest(ModInteropConstants.VirtualInputPlaybackV2, 2),
                message =>
                {
                    if (message.VirtualInput is not { } batch)
                        return;
                    batches.Enqueue(batch);
                    if (batch.Kind == VirtualInputBatchKind.Ended)
                        ended.Set();
                },
                out subscription,
                out var error), Is.True, error.ToString());
        }

        VirtualInputPlaybackPublisher? publisher;
        VirtualInputPlaybackSession? playback;
        using (Enter(publisherRuntime))
        {
            Assert.That(ModInterop.TryOpenVirtualInputPlayback(out publisher, out var error),
                Is.True, error.ToString());
            Assert.That(publisher!.TryStart(out playback, out error), Is.True, error.ToString());
            Assert.That(playback!.TryPublish(new VirtualInputEvent(
                0, 10_000, VirtualInputDevice.Keyboard, VirtualInputPhase.Down,
                "Space", -1, 0, 0, 0, 0, 0), out error), Is.True, error.ToString());
            publisher.Dispose();
        }

        Assert.That(ended.Wait(TimeSpan.FromSeconds(2)), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(playback!.IsActive, Is.False);
            Assert.That(batches.Count(batch => batch.Kind == VirtualInputBatchKind.Cancelled),
                Is.EqualTo(1));
            Assert.That(batches.Count(batch => batch.Kind == VirtualInputBatchKind.Ended),
                Is.EqualTo(1));
            Assert.That(batches.Single(batch => batch.Kind == VirtualInputBatchKind.Cancelled)
                .Events.Single().Phase, Is.EqualTo(VirtualInputPhase.Cancel));
        });

        playback!.Dispose();
        subscription!.Dispose();
        Retire(consumerRuntime);
        Retire(publisherRuntime);
    }

    [Test]
    public void DependenciesOnlyUsesTrustedModEntryDependencies()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var providerId = "dependency-provider-" + suffix;
        var providerRuntime = CreateRuntime(providerId);
        var dependentRuntime = CreateRuntime("dependent-" + suffix, providerId);
        var outsiderRuntime = CreateRuntime("outsider-" + suffix);
        using var dependentDelivered = new ManualResetEventSlim();
        using var outsiderDelivered = new ManualResetEventSlim();

        ModInteropPublisher? publisher;
        using (Enter(providerRuntime))
        {
            Assert.That(ModInterop.TryOpenPublisher(
                new InteropContractDeclaration("restricted")
                {
                    Visibility = InteropVisibility.DependenciesOnly
                },
                out publisher,
                out var error), Is.True, error.ToString());
        }

        ModInteropSubscription? dependent;
        using (Enter(dependentRuntime))
        {
            Assert.That(ModInterop.TrySubscribe(
                new InteropSubscriptionRequest($"mod/{providerId}/restricted"),
                _ => dependentDelivered.Set(),
                out dependent,
                out var error), Is.True, error.ToString());
        }
        ModInteropSubscription? outsider;
        using (Enter(outsiderRuntime))
        {
            Assert.That(ModInterop.TrySubscribe(
                new InteropSubscriptionRequest($"mod/{providerId}/restricted"),
                _ => outsiderDelivered.Set(),
                out outsider,
                out var error), Is.True, error.ToString());
        }

        using (Enter(providerRuntime))
            Assert.That(publisher!.TryPublish([1], out var error), Is.True, error.ToString());

        Assert.Multiple(() =>
        {
            Assert.That(dependentDelivered.Wait(TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(outsiderDelivered.Wait(TimeSpan.FromMilliseconds(100)), Is.False);
        });

        dependent!.Dispose();
        outsider!.Dispose();
        publisher!.Dispose();
        Retire(dependentRuntime);
        Retire(outsiderRuntime);
        Retire(providerRuntime);
    }

    [Test]
    public void PublisherHotUpdateDropsQueuedOldGenerationAndReattachesWaitingSubscription()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var publisherId = "hot-provider-" + suffix;
        var subscriberRuntime = CreateRuntime("hot-subscriber-" + suffix);
        var providerSession = new ModRuntimeSession();
        var firstKey = Activate(providerSession, publisherId);
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var delivered = new CountdownEvent(2);
        var values = new ConcurrentQueue<byte>();

        ModInteropSubscription? subscription;
        using (Enter(subscriberRuntime))
        {
            Assert.That(ModInterop.TrySubscribe(
                new InteropSubscriptionRequest($"mod/{publisherId}/events")
                {
                    QueueCapacity = 8
                },
                message =>
                {
                    var value = message.Payload.Span[0];
                    values.Enqueue(value);
                    if (value == 1)
                    {
                        firstEntered.Set();
                        releaseFirst.Wait(TimeSpan.FromSeconds(2));
                    }
                    delivered.Signal();
                },
                out subscription,
                out var error), Is.True, error.ToString());
        }

        ModInteropPublisher? firstPublisher;
        using (HookHelper.EnterOwnerScope(firstKey.OwnerId, providerSession, firstKey))
        {
            Assert.That(ModInterop.TryOpenPublisher(
                new InteropContractDeclaration("events"),
                out firstPublisher,
                out var error), Is.True, error.ToString());
            Assert.That(firstPublisher!.TryPublish([1], out error), Is.True, error.ToString());
        }
        Assert.That(firstEntered.Wait(TimeSpan.FromSeconds(2)), Is.True);
        using (HookHelper.EnterOwnerScope(firstKey.OwnerId, providerSession, firstKey))
            Assert.That(firstPublisher!.TryPublish([2], out var error), Is.True, error.ToString());
        firstPublisher!.Dispose();
        Retire((providerSession, firstKey));

        var secondKey = Activate(providerSession, publisherId);
        ModInteropPublisher? secondPublisher;
        using (HookHelper.EnterOwnerScope(secondKey.OwnerId, providerSession, secondKey))
        {
            Assert.That(ModInterop.TryOpenPublisher(
                new InteropContractDeclaration("events"),
                out secondPublisher,
                out var error), Is.True, error.ToString());
            Assert.That(secondPublisher!.TryPublish([3], out error), Is.True, error.ToString());
        }

        releaseFirst.Set();
        Assert.That(delivered.Wait(TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(values.ToArray(), Is.EqualTo(new byte[] { 1, 3 }));

        subscription!.Dispose();
        secondPublisher!.Dispose();
        Retire(subscriberRuntime);
        Retire((providerSession, secondKey));
    }

    [Test]
    public void UnityMainBatchedDispatchRunsInSchedulerAndPreservesOrder()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var publisherId = "unity-provider-" + suffix;
        var publisherRuntime = CreateRuntime(publisherId);
        var subscriberRuntime = CreateRuntime("unity-subscriber-" + suffix);
        var scheduled = new ConcurrentQueue<Action>();
        using var scheduledSignal = new AutoResetEvent(false);
        var received = new ConcurrentQueue<byte>();
        var callbackThreads = new ConcurrentQueue<int>();
        var schedulerThread = Environment.CurrentManagedThreadId;
        PcCompatRuntime.RegisterUnityMainWorkScheduler(work =>
        {
            scheduled.Enqueue(work);
            scheduledSignal.Set();
            return true;
        });

        ModInteropSubscription? subscription;
        using (Enter(subscriberRuntime))
        {
            Assert.That(ModInterop.TrySubscribe(
                new InteropSubscriptionRequest($"mod/{publisherId}/events")
                {
                    DispatchContext = InteropDispatchContext.UnityMainBatched
                },
                message =>
                {
                    received.Enqueue(message.Payload.Span[0]);
                    callbackThreads.Enqueue(Environment.CurrentManagedThreadId);
                },
                out subscription,
                out var error), Is.True, error.ToString());
        }
        ModInteropPublisher? publisher;
        using (Enter(publisherRuntime))
        {
            Assert.That(ModInterop.TryOpenPublisher(
                new InteropContractDeclaration("events"),
                out publisher,
                out var error), Is.True, error.ToString());
            Assert.That(publisher!.TryPublish([1], out error), Is.True, error.ToString());
            Assert.That(publisher.TryPublish([2], out error), Is.True, error.ToString());
            Assert.That(publisher.TryPublish([3], out error), Is.True, error.ToString());
        }

        Assert.That(scheduledSignal.WaitOne(TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(received, Is.Empty);
        Assert.That(SpinWait.SpinUntil(() =>
        {
            while (scheduled.TryDequeue(out var work))
                work();
            return received.Count == 3 || scheduledSignal.WaitOne(TimeSpan.FromMilliseconds(10));
        }, TimeSpan.FromSeconds(2)), Is.True);
        while (scheduled.TryDequeue(out var work))
            work();

        Assert.Multiple(() =>
        {
            Assert.That(received.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(callbackThreads, Is.All.EqualTo(schedulerThread));
        });

        subscription!.Dispose();
        publisher!.Dispose();
        Retire(subscriberRuntime);
        Retire(publisherRuntime);
    }

    [Test]
    public async Task RpcSupportsFanOutAndTargetedProviderGeneration()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var firstRuntime = CreateRuntime("rpc-a-" + suffix);
        var secondRuntime = CreateRuntime("rpc-b-" + suffix);
        var callerRuntime = CreateRuntime("rpc-fanout-caller-" + suffix);
        var declaration = new InteropContractDeclaration(ModInteropConstants.RequestResponseV1);

        ModInteropPublisher? first;
        using (Enter(firstRuntime))
        {
            Assert.That(ModInterop.TryOpenPublisher(declaration, out first, out var error),
                Is.True, error.ToString());
            Assert.That(first!.TrySetRequestHandler(
                _ => ValueTask.FromResult(InteropResponse.Success([1])), out error),
                Is.True, error.ToString());
        }
        ModInteropPublisher? second;
        using (Enter(secondRuntime))
        {
            Assert.That(ModInterop.TryOpenPublisher(declaration, out second, out var error),
                Is.True, error.ToString());
            Assert.That(second!.TrySetRequestHandler(
                _ => ValueTask.FromResult(InteropResponse.Success([2])), out error),
                Is.True, error.ToString());
        }

        InteropFanOutResponse fanOut;
        InteropResponse targeted;
        using (Enter(callerRuntime))
        {
            fanOut = await ModInterop.RequestFanOutAsync(new InteropRequest(
                ModInteropConstants.RequestResponseV1,
                1,
                [9],
                TimeSpan.FromSeconds(2),
                InteropProviderSelection.FanOut));
            targeted = await ModInterop.RequestAsync(new InteropRequest(
                ModInteropConstants.RequestResponseV1,
                1,
                [9],
                TimeSpan.FromSeconds(2),
                InteropProviderSelection.Targeted)
            {
                TargetPublisherId = secondRuntime.Key.ModId,
                TargetPublisherGeneration = secondRuntime.Key.Generation
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(fanOut.Responses.Select(response => response.Payload.Span[0]),
                Is.EquivalentTo(new byte[] { 1, 2 }));
            Assert.That(targeted.Succeeded, Is.True);
            Assert.That(targeted.Payload.ToArray(), Is.EqualTo(new byte[] { 2 }));
        });

        first!.Dispose();
        second!.Dispose();
        Retire(callerRuntime);
        Retire(firstRuntime);
        Retire(secondRuntime);
    }

    [Test]
    public async Task RpcEnforcesPerCallerQuotaAndCancelsWhenProviderRetires()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var providerRuntime = CreateRuntime("rpc-quota-provider-" + suffix);
        var callerRuntime = CreateRuntime("rpc-quota-caller-" + suffix);
        var release = new TaskCompletionSource<InteropResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var providerEntered = new CountdownEvent(16);

        ModInteropPublisher? publisher;
        using (Enter(providerRuntime))
        {
            Assert.That(ModInterop.TryOpenPublisher(
                new InteropContractDeclaration("slow"),
                out publisher,
                out var error), Is.True, error.ToString());
            Assert.That(publisher!.TrySetRequestHandler(_ =>
            {
                providerEntered.Signal();
                return new ValueTask<InteropResponse>(release.Task);
            }, out error), Is.True, error.ToString());
        }

        Task<InteropResponse>[] requests;
        using (Enter(callerRuntime))
        {
            requests = Enumerable.Range(0, 17)
                .Select(_ => ModInterop.RequestAsync(new InteropRequest(
                    $"mod/{providerRuntime.Key.ModId}/slow",
                    1,
                    [1],
                    TimeSpan.FromSeconds(5))))
                .ToArray();
        }
        Assert.That(providerEntered.Wait(TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(requests.Count(task => task.IsCompleted), Is.EqualTo(1));
        release.SetResult(InteropResponse.Success([7]));
        var responses = await Task.WhenAll(requests);
        Assert.Multiple(() =>
        {
            Assert.That(responses.Count(response => response.Code ==
                InteropErrorCode.RequestLimitExceeded), Is.EqualTo(1));
            Assert.That(responses.Count(response => response.Succeeded), Is.EqualTo(16));
        });

        publisher!.Dispose();
        Retire(callerRuntime);
        Retire(providerRuntime);

        var retiringProvider = CreateRuntime("rpc-retiring-provider-" + suffix);
        var retiringCaller = CreateRuntime("rpc-retiring-caller-" + suffix);
        using var requestEntered = new ManualResetEventSlim();
        ModInteropPublisher? retiringPublisher;
        using (Enter(retiringProvider))
        {
            Assert.That(ModInterop.TryOpenPublisher(
                new InteropContractDeclaration("wait"),
                out retiringPublisher,
                out var error), Is.True, error.ToString());
            Assert.That(retiringPublisher!.TrySetRequestHandler(async request =>
            {
                requestEntered.Set();
                await Task.Delay(Timeout.InfiniteTimeSpan, request.CancellationToken);
                return InteropResponse.Success();
            }, out error), Is.True, error.ToString());
        }
        Task<InteropResponse> pending;
        using (Enter(retiringCaller))
        {
            pending = ModInterop.RequestAsync(new InteropRequest(
                $"mod/{retiringProvider.Key.ModId}/wait",
                1,
                [1],
                TimeSpan.FromSeconds(5)));
        }
        Assert.That(requestEntered.Wait(TimeSpan.FromSeconds(2)), Is.True);
        retiringPublisher!.Dispose();
        var retiredResponse = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(retiredResponse.Code, Is.EqualTo(InteropErrorCode.ProviderRetired));

        Retire(retiringCaller);
        Retire(retiringProvider);
    }

    [Test]
    public void DisposedSubscriptionReleasesCapturedCallbackTarget()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var runtime = CreateRuntime("weak-subscriber-" + suffix);
        var (subscription, target) = CreateCapturingSubscription(
            runtime,
            $"mod/missing-{suffix}/events");

        subscription.Dispose();
        ForceCollection();

        Assert.That(target.IsAlive, Is.False,
            "Broker retained the subscriber callback after its lease was disposed.");
        Retire(runtime);
    }

    [Test]
    public void ReservedVirtualInputContractRejectsGenericPublisher()
    {
        var runtime = CreateRuntime("reserved-contract-" + Guid.NewGuid().ToString("N"));
        using (Enter(runtime))
        {
            Assert.That(ModInterop.TryOpenPublisher(
                new InteropContractDeclaration(
                    ModInteropConstants.VirtualInputPlaybackV2,
                    majorVersion: 2),
                out var publisher,
                out var error), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(publisher, Is.Null);
                Assert.That(error.Code, Is.EqualTo(InteropErrorCode.VisibilityDenied));
            });
        }
        Retire(runtime);
    }

    [Test]
    public void InvalidVirtualBatchIsRejectedWithoutPartiallyMutatingHeldState()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var publisherRuntime = CreateRuntime("atomic-replay-" + suffix);
        var consumerRuntime = CreateRuntime("atomic-consumer-" + suffix);
        VirtualInputPlaybackPublisher? publisher;
        VirtualInputPlaybackSession? playback;
        using (Enter(publisherRuntime))
        {
            Assert.That(ModInterop.TryOpenVirtualInputPlayback(out publisher, out var error),
                Is.True, error.ToString());
            Assert.That(publisher!.TryStart(out playback, out error), Is.True, error.ToString());
            Assert.That(playback!.TryPublish(new VirtualInputEvent(
                0, 100, VirtualInputDevice.Keyboard, VirtualInputPhase.Down,
                "A", -1, 0, 0, 0, 0, 0), out error), Is.True, error.ToString());
            Assert.That(playback.TryPublish(
                [
                    new VirtualInputEvent(
                        0, 200, VirtualInputDevice.Keyboard, VirtualInputPhase.Up,
                        "A", -1, 0, 0, 0, 0, 0),
                    new VirtualInputEvent(
                        0, 150, VirtualInputDevice.Keyboard, VirtualInputPhase.Down,
                        "B", -1, 0, 0, 0, 0, 0)
                ],
                out error), Is.False);
            Assert.That(error.Code, Is.EqualTo(InteropErrorCode.InvalidArgument));
        }

        VirtualInputBatch? snapshot = null;
        using var snapshotReady = new ManualResetEventSlim();
        ModInteropSubscription? subscription;
        using (Enter(consumerRuntime))
        {
            Assert.That(ModInterop.TrySubscribe(
                new InteropSubscriptionRequest(ModInteropConstants.VirtualInputPlaybackV2, 2),
                message =>
                {
                    if (message.VirtualInput?.Kind != VirtualInputBatchKind.Snapshot)
                        return;
                    snapshot = message.VirtualInput;
                    snapshotReady.Set();
                },
                out subscription,
                out var error), Is.True, error.ToString());
        }
        Assert.That(snapshotReady.Wait(TimeSpan.FromSeconds(2)), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot, Is.Not.Null);
            Assert.That(snapshot!.Events.Select(input => input.CanonicalKey),
                Is.EqualTo(new[] { "A" }));
        });

        playback!.Cancel();
        subscription!.Dispose();
        publisher!.Dispose();
        Retire(consumerRuntime);
        Retire(publisherRuntime);
    }

    [Test]
    public void VirtualInputQueueCapacityCountsEventsInsteadOfBatches()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var publisherRuntime = CreateRuntime("weighted-replay-" + suffix);
        var consumerRuntime = CreateRuntime("weighted-consumer-" + suffix);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancelled = new ManualResetEventSlim();

        ModInteropSubscription? subscription;
        using (Enter(consumerRuntime))
        {
            Assert.That(ModInterop.TrySubscribe(
                new InteropSubscriptionRequest(ModInteropConstants.VirtualInputPlaybackV2, 2)
                {
                    QueueCapacity = 4
                },
                message =>
                {
                    if (message.IsCancellation)
                    {
                        cancelled.Set();
                        return;
                    }
                    if (message.VirtualInput?.Kind == VirtualInputBatchKind.Started)
                    {
                        started.Set();
                        release.Wait(TimeSpan.FromSeconds(2));
                    }
                },
                out subscription,
                out var error), Is.True, error.ToString());
        }

        VirtualInputPlaybackPublisher? publisher;
        VirtualInputPlaybackSession? playback;
        using (Enter(publisherRuntime))
        {
            Assert.That(ModInterop.TryOpenVirtualInputPlayback(out publisher, out var error),
                Is.True, error.ToString());
            Assert.That(publisher!.TryStart(out playback, out error), Is.True, error.ToString());
        }
        Assert.That(started.Wait(TimeSpan.FromSeconds(2)), Is.True);

        using (Enter(publisherRuntime))
        {
            var first = Enumerable.Range(0, 4)
                .Select(index => new VirtualInputEvent(
                    0,
                    100 + index,
                    VirtualInputDevice.Keyboard,
                    VirtualInputPhase.Down,
                    "Key" + index,
                    -1,
                    0,
                    0,
                    0,
                    0,
                    0))
                .ToArray();
            Assert.That(playback!.TryPublish(first, out var error), Is.True, error.ToString());
            Assert.That(playback.TryPublish(new VirtualInputEvent(
                0, 200, VirtualInputDevice.Keyboard, VirtualInputPhase.Down,
                "Overflow", -1, 0, 0, 0, 0, 0), out error), Is.True, error.ToString());
        }
        Assert.That(SpinWait.SpinUntil(
            () => subscription!.IsCircuitBroken,
            TimeSpan.FromSeconds(2)), Is.True);
        release.Set();
        Assert.That(cancelled.Wait(TimeSpan.FromSeconds(2)), Is.True);

        playback!.Cancel();
        subscription!.Dispose();
        publisher!.Dispose();
        Retire(consumerRuntime);
        Retire(publisherRuntime);
    }

    private static (ModRuntimeSession Session, ModRuntimeKey Key) CreateRuntime(
        string modId,
        params string[] dependencies)
    {
        var session = new ModRuntimeSession();
        var key = Activate(session, modId, dependencies);
        return (session, key);
    }

    private static ModRuntimeKey Activate(
        ModRuntimeSession session,
        string modId,
        params string[] dependencies)
    {
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, modId);
        Assert.That(session.TrySetTrustedDependencies(key, dependencies), Is.True);
        Assert.That(session.TryPublishActive(key), Is.True);
        return key;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ModInteropSubscription Subscription, WeakReference Target)
        CreateCapturingSubscription(
            (ModRuntimeSession Session, ModRuntimeKey Key) runtime,
            string contractId)
    {
        var target = new object();
        ModInteropSubscription? subscription;
        using (Enter(runtime))
        {
            Assert.That(ModInterop.TrySubscribe(
                new InteropSubscriptionRequest(contractId),
                _ => GC.KeepAlive(target),
                out subscription,
                out var error), Is.True, error.ToString());
        }
        return (subscription!, new WeakReference(target));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection()
    {
        for (var attempt = 0; attempt < 3; ++attempt)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static IDisposable Enter((ModRuntimeSession Session, ModRuntimeKey Key) runtime)
        => HookHelper.EnterOwnerScope(runtime.Key.OwnerId, runtime.Session, runtime.Key);

    private static void Retire((ModRuntimeSession Session, ModRuntimeKey Key) runtime)
    {
        var snapshot = runtime.Session.Snapshot();
        if (snapshot.State == ModRuntimeLifecycleState.Active)
            Assert.That(runtime.Session.TryBeginRetirement(runtime.Key), Is.True);
        if (runtime.Session.Snapshot().State is
            ModRuntimeLifecycleState.Retiring or ModRuntimeLifecycleState.Quiescing)
        {
            Assert.That(runtime.Session.WaitForQuiescence(runtime.Key, TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(runtime.Session.TryCompleteRetirement(runtime.Key), Is.True);
        }
    }
}

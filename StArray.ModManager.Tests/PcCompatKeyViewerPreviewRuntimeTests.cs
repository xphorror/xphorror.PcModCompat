using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatKeyViewerPreviewRuntimeTests
{
    private const string ModId = "PreviewRuntimeTest";
    private const string SecondModId = "PreviewRuntimeTest2";

    [TearDown]
    public void TearDown()
    {
        PcCompatKeyViewerPreviewRuntime.Unregister(ModId);
        PcCompatKeyViewerPreviewRuntime.Unregister(SecondModId);
        PcCompatKeyViewerPreviewRuntime.RegisterDemandChangedSink(null);
        PcCompatKeyViewerEventRuntime.ClearProvider();
        PcCompatExternalInputDeviceRuntime.ClearProvider();
        PcCompatKeyViewerLoweredConsumerPlanRegistry.Remove(ModId);
        PcCompatKeyViewerLoweredConsumerPlanRegistry.Remove(SecondModId);
        PcCompatKeyViewerFallbackRuntime.Unregister(ModId);
        PcCompatKeyViewerFallbackRuntime.Unregister(SecondModId);
        PcCompatKeyViewerFallbackRuntime.RegisterRenderer(null);
        PcCompatClockAnchorRuntime.ClearProvider();
        PcCompatTouchLaneMappingRuntime.RegisterNativeSink(null);
        PcCompatTouchLaneMappingRuntime.RegisterNativeReuseDelaySink(null);
        PcCompatTouchLaneMappingRuntime.SetMode(PcCompatTouchLaneMappingMode.ScreenRegions);
        PcCompatTouchLaneMappingRuntime.SetTouchContactReuseDelayMilliseconds(
            PcCompatTouchLaneMappingRuntime.DefaultTouchContactReuseDelayMilliseconds);
        PcCompatLegacyInputBridge.SetModalInputCapture(false);
    }

    [Test]
    public void OpensAtTailThenProjectsOrderedTouchEdgesWithoutWritingModState()
    {
        var reads = 0;
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            ++reads;
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 10
                };
            }
            if (cursor == 12)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 12
                };
            }
            return new PcCompatKeyViewerEventBatch
            {
                ProviderAvailable = true,
                Cursor = 12,
                Events =
                [
                    Event(11, PcCompatKeyViewerRawPhase.Down, slot: 2, x: 760),
                    Event(12, PcCompatKeyViewerRawPhase.Up, slot: 2, x: 760)
                ]
            };
        });
        var (adapter, overrides) = CreateConfiguration(PcCompatKeyViewerInputMode.Touch, 4);
        var demandChanges = 0;
        PcCompatKeyViewerPreviewRuntime.RegisterDemandChangedSink(() => demandChanges++);

        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);
        Pump();
        var snapshot = PcCompatKeyViewerPreviewRuntime.Snapshot(ModId);
        var feature = snapshot.Features.Single();

        Assert.Multiple(() =>
        {
            Assert.That(reads, Is.EqualTo(2));
            Assert.That(demandChanges, Is.EqualTo(1));
            Assert.That(snapshot.Registered, Is.True);
            Assert.That(snapshot.CursorInitialized, Is.True);
            Assert.That(snapshot.Faulted, Is.False, snapshot.Fault);
            Assert.That(snapshot.StartCursor, Is.EqualTo(10));
            Assert.That(snapshot.Cursor, Is.EqualTo(12));
            Assert.That(snapshot.EventCount, Is.EqualTo(2));
            Assert.That(feature.LaneCount, Is.EqualTo(4));
            Assert.That(feature.HeldMask, Is.Zero);
            Assert.That(feature.TransitionCount, Is.EqualTo(2));
            Assert.That(feature.LastTransition?.LaneIdentity, Is.EqualTo("TouchLane:T4"));
            Assert.That(feature.LastTransition?.Phase,
                Is.EqualTo(PcCompatKeyViewerRawPhase.Up));
        });
    }

    [Test]
    public void TouchContactModeSeparatesContactsAtTheSameScreenPosition()
    {
        PcCompatTouchLaneMappingRuntime.SetMode(
            PcCompatTouchLaneMappingMode.TouchContacts);
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 20
                };
            }
            if (cursor == 24)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 24
                };
            }
            return new PcCompatKeyViewerEventBatch
            {
                ProviderAvailable = true,
                Cursor = 24,
                Events =
                [
                    Event(21, PcCompatKeyViewerRawPhase.Down, slot: 0, x: 100),
                    Event(22, PcCompatKeyViewerRawPhase.Down, slot: 1, x: 100),
                    Event(23, PcCompatKeyViewerRawPhase.Up, slot: 1, x: 100),
                    Event(24, PcCompatKeyViewerRawPhase.Up, slot: 0, x: 100)
                ]
            };
        });
        var (adapter, overrides) = CreateConfiguration(PcCompatKeyViewerInputMode.Touch, 4);

        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);
        Pump();
        var feature = PcCompatKeyViewerPreviewRuntime.Snapshot(ModId).Features.Single();

        Assert.Multiple(() =>
        {
            Assert.That(feature.TouchLaneMappingMode,
                Is.EqualTo(PcCompatTouchLaneMappingMode.TouchContacts));
            Assert.That(feature.HeldMask, Is.Zero);
            Assert.That(feature.DownOrdinals[0], Is.EqualTo(1));
            Assert.That(feature.DownOrdinals[1], Is.EqualTo(1));
            Assert.That(feature.DownOrdinals[2], Is.Zero);
            Assert.That(feature.DownOrdinals[3], Is.Zero);
            Assert.That(feature.LastTransition?.LaneIdentity, Is.EqualTo("TouchLane:T1"));
        });
    }

    [Test]
    public void NearbySimultaneousTouchContactsRemainHeldUntilTheirOwnUpEdges()
    {
        PcCompatTouchLaneMappingRuntime.SetMode(
            PcCompatTouchLaneMappingMode.TouchContacts);
        var stage = 0;
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
                return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = 40 };
            if (cursor == 40)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 42,
                    Events =
                    [
                        Event(41, PcCompatKeyViewerRawPhase.Down, slot: 0, x: 100,
                            pointerCount: 1),
                        Event(42, PcCompatKeyViewerRawPhase.Down, slot: 1, x: 101,
                            pointerCount: 2)
                    ]
                };
            }
            if (cursor == 42 && stage >= 1)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 43,
                    Events =
                    [
                        Event(43, PcCompatKeyViewerRawPhase.Up, slot: 1, x: 101,
                            pointerCount: 2, androidFlags: 0x20)
                    ]
                };
            }
            if (cursor == 43 && stage >= 2)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 44,
                    Events =
                    [
                        Event(44, PcCompatKeyViewerRawPhase.Up, slot: 0, x: 100,
                            pointerCount: 1)
                    ]
                };
            }
            return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = cursor };
        });
        var (adapter, overrides) = CreateConfiguration(
            PcCompatKeyViewerInputMode.Touch,
            2,
            staticUnityKeys: true);

        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);
        Pump();
        Assert.Multiple(() =>
        {
            Assert.That(PcCompatKeyViewerPreviewRuntime.Snapshot(ModId)
                .Features.Single().HeldMask, Is.EqualTo(0b11u));
            Assert.That(PcCompatLegacyInputBridge.GetKeyOwned(TestKey.A, 4101, ModId), Is.True);
            Assert.That(PcCompatLegacyInputBridge.GetKeyOwned(TestKey.B, 4102, ModId), Is.True);
            Assert.That(PcCompatLegacyInputBridge.GetKeyOwned(TestKey.A, 4101, ModId), Is.True);
            Assert.That(PcCompatLegacyInputBridge.GetKeyOwned(TestKey.B, 4102, ModId), Is.True);
        });

        stage = 1;
        Pump();
        Assert.Multiple(() =>
        {
            Assert.That(PcCompatKeyViewerPreviewRuntime.Snapshot(ModId)
                .Features.Single().HeldMask, Is.EqualTo(0b01u));
            Assert.That(PcCompatLegacyInputBridge.GetKeyOwned(TestKey.A, 4101, ModId), Is.True);
            Assert.That(PcCompatLegacyInputBridge.GetKeyOwned(TestKey.B, 4102, ModId), Is.False);
        });

        stage = 2;
        Pump();
        var completed = PcCompatKeyViewerPreviewRuntime.Snapshot(ModId);
        Assert.Multiple(() =>
        {
            Assert.That(completed.Features.Single().HeldMask, Is.Zero);
            Assert.That(completed.TouchDownEventCount, Is.EqualTo(2));
            Assert.That(completed.TouchUpEventCount, Is.EqualTo(2));
            Assert.That(completed.TouchCancelEventCount, Is.Zero);
            Assert.That(
                completed.RecentTouchEvents.Select(inputEvent => inputEvent.Sequence),
                Is.EqualTo(new ulong[] { 41, 42, 43, 44 }));
            Assert.That(
                completed.RecentTouchEvents.Select(inputEvent => inputEvent.PointerCount),
                Is.EqualTo(new[] { 1, 2, 2, 1 }));
            Assert.That(completed.RecentTouchEvents[2].AndroidFlags, Is.EqualTo(0x20));
        });
    }

    [Test]
    public void LastCancelContextSurvivesRecentTouchTailOverwrite()
    {
        PcCompatTouchLaneMappingRuntime.SetMode(
            PcCompatTouchLaneMappingMode.TouchContacts);
        var events = new List<PcCompatKeyViewerRawEvent>();
        ulong sequence = 101;
        for (var index = 0; index < 5; ++index)
        {
            events.Add(Event(sequence, PcCompatKeyViewerRawPhase.Down, 0, 100));
            ++sequence;
            events.Add(Event(sequence, PcCompatKeyViewerRawPhase.Up, 0, 100));
            ++sequence;
        }
        events.Add(Event(sequence, PcCompatKeyViewerRawPhase.Down, 0, 100));
        ++sequence;
        events.Add(Event(sequence, PcCompatKeyViewerRawPhase.Down, 1, 101,
            pointerCount: 2));
        ++sequence;
        events.Add(Event(sequence, PcCompatKeyViewerRawPhase.Cancel, -1, 0,
            code: -1,
            flags: 0b11u,
            pointerCount: 3,
            androidFlags: 0x80800));
        ++sequence;
        for (var index = 0; index < 10; ++index)
        {
            events.Add(Event(sequence, PcCompatKeyViewerRawPhase.Down, 0, 100));
            ++sequence;
            events.Add(Event(sequence, PcCompatKeyViewerRawPhase.Up, 0, 100));
            ++sequence;
        }
        var lastSequence = events[^1].Sequence;
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
                return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = 100 };
            if (cursor == 100)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = lastSequence,
                    Events = events
                };
            }
            return new PcCompatKeyViewerEventBatch
            {
                ProviderAvailable = true,
                Cursor = cursor
            };
        });
        var (adapter, overrides) = CreateConfiguration(
            PcCompatKeyViewerInputMode.Touch,
            4);

        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);
        Pump();
        var snapshot = PcCompatKeyViewerPreviewRuntime.Snapshot(ModId);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.TouchDownEventCount, Is.EqualTo(17));
            Assert.That(snapshot.TouchUpEventCount, Is.EqualTo(15));
            Assert.That(snapshot.TouchCancelEventCount, Is.EqualTo(1));
            Assert.That(snapshot.RecentTouchEvents.Select(value => value.Sequence),
                Is.EqualTo(Enumerable.Range(118, 16).Select(value => (ulong)value)));
            Assert.That(snapshot.RecentTouchEvents.Any(
                value => value.Phase == PcCompatKeyViewerRawPhase.Cancel), Is.False);
            Assert.That(snapshot.LastTouchCancelContext.Select(value => value.Sequence),
                Is.EqualTo(Enumerable.Range(105, 17).Select(value => (ulong)value)));
            Assert.That(snapshot.LastTouchCancelContext[8].Phase,
                Is.EqualTo(PcCompatKeyViewerRawPhase.Cancel));
            Assert.That(snapshot.LastTouchCancelContext[8].PointerCount, Is.EqualTo(3));
            Assert.That(snapshot.LastTouchCancelContext[8].AndroidFlags, Is.EqualTo(0x80800));
            Assert.That(snapshot.LastTouchCancelContext[8].Flags, Is.EqualTo(0b11u));
        });
    }

    [Test]
    public void MergedIdentityRemainsHeldWhileAnyMappedLaneIsHeld()
    {
        PcCompatTouchLaneMappingRuntime.SetMode(
            PcCompatTouchLaneMappingMode.TouchContacts);
        var stage = 0;
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
                return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = 60 };
            if (cursor == 60)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 62,
                    Events =
                    [
                        Event(61, PcCompatKeyViewerRawPhase.Down, slot: 0, x: 100),
                        Event(62, PcCompatKeyViewerRawPhase.Down, slot: 1, x: 101,
                            pointerCount: 2)
                    ]
                };
            }
            if (cursor == 62 && stage >= 1)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 63,
                    Events =
                    [
                        Event(63, PcCompatKeyViewerRawPhase.Up, slot: 1, x: 101,
                            pointerCount: 2)
                    ]
                };
            }
            return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = cursor };
        });
        var (adapter, overrides) = CreateConfiguration(
            PcCompatKeyViewerInputMode.Touch,
            2,
            staticUnityKeys: true,
            duplicateStaticIdentity: true);
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);

        Pump();
        Assert.That(
            PcCompatLegacyInputBridge.GetKeyOwned(TestKey.A, 4201, ModId),
            Is.True);
        stage = 1;
        Pump();
        Assert.That(
            PcCompatLegacyInputBridge.GetKeyOwned(TestKey.A, 4201, ModId),
            Is.True,
            "a partial release cannot clear an identity still held by another lane");
    }

    [Test]
    public void TouchContactModeAvoidsRecentlyReleasedLaneForRapidSequentialTaps()
    {
        PcCompatTouchLaneMappingRuntime.SetMode(
            PcCompatTouchLaneMappingMode.TouchContacts);
        PcCompatTouchLaneMappingRuntime.SetTouchContactReuseDelayMilliseconds(80);
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 39
                };
            }
            if (cursor == 45)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 45
                };
            }
            return new PcCompatKeyViewerEventBatch
            {
                ProviderAvailable = true,
                Cursor = 45,
                Events =
                [
                    Event(40, PcCompatKeyViewerRawPhase.Down, slot: 0, x: 100),
                    Event(41, PcCompatKeyViewerRawPhase.Up, slot: 0, x: 100),
                    Event(42, PcCompatKeyViewerRawPhase.Down, slot: 0, x: 100)
                        with { RawNs = 60_000_000 },
                    Event(43, PcCompatKeyViewerRawPhase.Up, slot: 0, x: 100)
                        with { RawNs = 61_000_000 },
                    Event(44, PcCompatKeyViewerRawPhase.Down, slot: 0, x: 100)
                        with { RawNs = 200_000_000 },
                    Event(45, PcCompatKeyViewerRawPhase.Up, slot: 0, x: 100)
                        with { RawNs = 201_000_000 }
                ]
            };
        });
        var (adapter, overrides) = CreateConfiguration(PcCompatKeyViewerInputMode.Touch, 4);

        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);
        Pump();
        var feature = PcCompatKeyViewerPreviewRuntime.Snapshot(ModId).Features.Single();

        Assert.Multiple(() =>
        {
            Assert.That(feature.DownOrdinals[0], Is.EqualTo(2));
            Assert.That(feature.DownOrdinals[1], Is.EqualTo(1));
            Assert.That(feature.DownOrdinals[2], Is.Zero);
            Assert.That(feature.LastTransition?.LaneIdentity, Is.EqualTo("TouchLane:T1"));
        });
    }

    [Test]
    public void TouchContactReuseDelayHotUpdateReachesFeatureAndNativeSink()
    {
        var nativeDelay = -1;
        PcCompatTouchLaneMappingRuntime.RegisterNativeReuseDelaySink(
            value => nativeDelay = value);
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
            new PcCompatKeyViewerEventBatch
            {
                ProviderAvailable = true,
                Cursor = cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor
                    ? 300UL
                    : cursor
            });
        var (adapter, overrides) = CreateConfiguration(
            PcCompatKeyViewerInputMode.Touch,
            4);
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);
        Pump();

        PcCompatTouchLaneMappingRuntime.SetTouchContactReuseDelayMilliseconds(125);
        Assert.That(PcCompatKeyViewerPreviewRuntime.WaitForIdle(TimeSpan.FromSeconds(2)),
            Is.True);
        var feature = PcCompatKeyViewerPreviewRuntime.Snapshot(ModId).Features.Single();

        Assert.Multiple(() =>
        {
            Assert.That(nativeDelay, Is.EqualTo(125));
            Assert.That(feature.TouchContactReuseDelayMilliseconds, Is.EqualTo(125));
        });
    }

    [Test]
    public void SwitchingTouchMappingModeReleasesHeldRegionState()
    {
        var emitOldContactUp = 0;
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 30
                };
            }
            if (cursor == 31)
            {
                if (Volatile.Read(ref emitOldContactUp) != 0)
                {
                    return new PcCompatKeyViewerEventBatch
                    {
                        ProviderAvailable = true,
                        Cursor = 32,
                        Events = [Event(32, PcCompatKeyViewerRawPhase.Up, slot: 0, x: 760)]
                    };
                }
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 31
                };
            }
            if (cursor == 32)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 32
                };
            }
            return new PcCompatKeyViewerEventBatch
            {
                ProviderAvailable = true,
                Cursor = 31,
                Events = [Event(31, PcCompatKeyViewerRawPhase.Down, slot: 0, x: 760)]
            };
        });
        var (adapter, overrides) = CreateConfiguration(PcCompatKeyViewerInputMode.Touch, 4);
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);
        Pump();
        Assert.That(
            PcCompatKeyViewerPreviewRuntime.Snapshot(ModId).Features.Single().HeldMask,
            Is.EqualTo(1u << 3));

        PcCompatTouchLaneMappingRuntime.SetMode(
            PcCompatTouchLaneMappingMode.TouchContacts);
        Assert.That(PcCompatKeyViewerPreviewRuntime.WaitForIdle(TimeSpan.FromSeconds(2)),
            Is.True);
        Volatile.Write(ref emitOldContactUp, 1);
        Pump();
        var feature = PcCompatKeyViewerPreviewRuntime.Snapshot(ModId).Features.Single();

        Assert.Multiple(() =>
        {
            Assert.That(feature.TouchLaneMappingMode,
                Is.EqualTo(PcCompatTouchLaneMappingMode.TouchContacts));
            Assert.That(feature.HeldMask, Is.Zero);
            Assert.That(feature.UpOrdinals[3], Is.EqualTo(1));
            Assert.That(feature.UnmappedEventCount, Is.Zero);
        });
    }

    [Test]
    public void RuntimeRingLossFaultsTheRegistrationAndStopsFurtherConsumption()
    {
        var liveReads = 0;
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 30
                };
            }
            ++liveReads;
            return new PcCompatKeyViewerEventBatch
            {
                ProviderAvailable = true,
                Cursor = 40,
                DroppedBeforeCursor = 9,
                Events = [Event(40, PcCompatKeyViewerRawPhase.Down, slot: 0, x: 10)]
            };
        });
        var (adapter, overrides) = CreateConfiguration(PcCompatKeyViewerInputMode.Touch, 2);
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out _), Is.True);

        Pump();
        Pump();
        Pump();
        var snapshot = PcCompatKeyViewerPreviewRuntime.Snapshot(ModId);

        Assert.Multiple(() =>
        {
            Assert.That(liveReads, Is.EqualTo(1));
            Assert.That(snapshot.Faulted, Is.True);
            Assert.That(snapshot.DroppedEventCount, Is.EqualTo(9));
            Assert.That(snapshot.EventCount, Is.Zero);
            Assert.That(snapshot.Fault, Does.Contain("ring overflow"));
        });
    }

    [Test]
    public void ProducerEpochChangeWithoutMarkerFailsClosed()
    {
        var liveRead = false;
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 50
                };
            }
            if (liveRead)
                return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = 52 };
            liveRead = true;
            return new PcCompatKeyViewerEventBatch
            {
                ProviderAvailable = true,
                Cursor = 52,
                Events =
                [
                    Event(51, PcCompatKeyViewerRawPhase.Down, slot: 0, x: 10,
                        producerEpoch: 3),
                    Event(52, PcCompatKeyViewerRawPhase.Up, slot: 0, x: 10,
                        producerEpoch: 4)
                ]
            };
        });
        var (adapter, overrides) = CreateConfiguration(PcCompatKeyViewerInputMode.Touch, 2);
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out _), Is.True);

        Pump();
        Pump();
        var snapshot = PcCompatKeyViewerPreviewRuntime.Snapshot(ModId);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Faulted, Is.True);
            Assert.That(snapshot.EventCount, Is.EqualTo(1));
            Assert.That(snapshot.Fault, Does.Contain("invalid producer epoch transition"));
            Assert.That(snapshot.Features.Single().HeldMask, Is.Zero);
        });
    }

    [Test]
    public void RegistrationsSharingACursorUseOneNativeReadPerFrame()
    {
        var reads = 0;
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            ++reads;
            return new PcCompatKeyViewerEventBatch
            {
                ProviderAvailable = true,
                Cursor = cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor ? 70u : cursor
            };
        });
        var first = CreateConfiguration(PcCompatKeyViewerInputMode.Touch, 2, ModId);
        var second = CreateConfiguration(PcCompatKeyViewerInputMode.Touch, 4, SecondModId);
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, first.Adapter, first.Overrides, out _), Is.True);
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            SecondModId, second.Adapter, second.Overrides, out _), Is.True);

        Pump();

        Assert.Multiple(() =>
        {
            Assert.That(reads, Is.EqualTo(2), "one tail-open plus one shared live read");
            Assert.That(PcCompatKeyViewerPreviewRuntime.Snapshot(ModId).Cursor,
                Is.EqualTo(70));
            Assert.That(PcCompatKeyViewerPreviewRuntime.Snapshot(SecondModId).Cursor,
                Is.EqualTo(70));
        });
    }

    [Test]
    public void NativeWakePumpsInputWithoutAUnityFrameDispatch()
    {
        var wake = new AutoResetEvent(false);
        var emitted = 0;
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
                return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = 75 };
            if (cursor == 75 && Volatile.Read(ref emitted) != 0)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 76,
                    Events = [Event(76, PcCompatKeyViewerRawPhase.Down, 0, 10)]
                };
            }
            return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = cursor };
        });
        PcCompatKeyViewerEventRuntime.RegisterWakeProvider(
            (_, timeout) => wake.WaitOne(timeout),
            () => wake.Set());
        PcCompatKeyViewerPreviewRuntime.RefreshWakeProvider();
        var (adapter, overrides) = CreateConfiguration(
            PcCompatKeyViewerInputMode.Touch,
            2,
            staticUnityKeys: true);

        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);
        Assert.That(SpinWait.SpinUntil(
            () => PcCompatKeyViewerPreviewRuntime.Snapshot(ModId).CursorInitialized,
            TimeSpan.FromSeconds(2)), Is.True, "native wake pump did not open its cursor");

        Volatile.Write(ref emitted, 1);
        wake.Set();

        Assert.That(SpinWait.SpinUntil(
            () => PcCompatKeyViewerPreviewRuntime.Snapshot(ModId).EventCount == 1,
            TimeSpan.FromSeconds(2)), Is.True, "native wake pump did not consume the event");
        Assert.That(
            Task.Run(() => PcCompatLegacyInputBridge.GetKeyOwned(TestKey.A, 7601, ModId))
                .GetAwaiter()
                .GetResult(),
            Is.True,
            "a MOD-owned worker must consume the published query surface without UnityMain context");
        Assert.That(
            PcCompatLegacyInputBridge.GetDiagnosticStatus(ModId),
            Does.Match(@"unityHeld=[1-9][0-9]*"));
    }

    [Test]
    public void TouchMappingTransitionCannotOvertakeAnInFlightRawBatch()
    {
        using var readEntered = new ManualResetEventSlim(false);
        using var releaseRead = new ManualResetEventSlim(false);
        var blockLiveRead = 0;
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
                return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = 90 };
            if (Volatile.Read(ref blockLiveRead) != 0)
            {
                readEntered.Set();
                releaseRead.Wait(TimeSpan.FromSeconds(2));
            }
            return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = cursor };
        });
        var (adapter, overrides) = CreateConfiguration(PcCompatKeyViewerInputMode.Touch, 2);
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);
        Pump();

        var nativeModes = new List<PcCompatTouchLaneMappingMode>();
        PcCompatTouchLaneMappingRuntime.RegisterNativeSink(mode => nativeModes.Add(mode));
        Volatile.Write(ref blockLiveRead, 1);
        PcCompatKeyViewerPreviewRuntime.DispatchFrame();
        Assert.That(readEntered.Wait(TimeSpan.FromSeconds(2)), Is.True);

        Task? transition = null;
        try
        {
            transition = Task.Run(() => PcCompatTouchLaneMappingRuntime.SetMode(
                PcCompatTouchLaneMappingMode.TouchContacts));
            Assert.That(transition.Wait(TimeSpan.FromMilliseconds(50)), Is.False,
                "mapping transition overtook the in-flight raw provider read");
        }
        finally
        {
            releaseRead.Set();
            transition?.Wait(TimeSpan.FromSeconds(2));
        }

        Assert.That(PcCompatKeyViewerPreviewRuntime.WaitForIdle(TimeSpan.FromSeconds(2)), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(nativeModes.Last(), Is.EqualTo(PcCompatTouchLaneMappingMode.TouchContacts));
            Assert.That(PcCompatKeyViewerPreviewRuntime.Snapshot(ModId)
                .Features.Single().TouchLaneMappingMode,
                Is.EqualTo(PcCompatTouchLaneMappingMode.TouchContacts));
        });
    }

    [Test]
    public void ProducerMarkerCannotSkipAnEpoch()
    {
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 80
                };
            }
            return new PcCompatKeyViewerEventBatch
            {
                ProviderAvailable = true,
                Cursor = 82,
                Events =
                [
                    Event(81, PcCompatKeyViewerRawPhase.Down, slot: 0, x: 10,
                        producerEpoch: 3),
                    Event(82, PcCompatKeyViewerRawPhase.ProducerChanged, slot: -1, x: 0,
                        producerEpoch: 5)
                ]
            };
        });
        var (adapter, overrides) = CreateConfiguration(PcCompatKeyViewerInputMode.Touch, 2);
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out _), Is.True);

        Pump();
        var snapshot = PcCompatKeyViewerPreviewRuntime.Snapshot(ModId);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Faulted, Is.True);
            Assert.That(snapshot.Fault, Does.Contain("invalid producer epoch transition"));
            Assert.That(snapshot.EventCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void ProvenStaticLaneConsumerFeedsTheOriginalModPollingStateMachine()
    {
        var release = false;
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
                return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = 100 };
            if (cursor == 100)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 101,
                    Events = [Event(101, PcCompatKeyViewerRawPhase.Down, slot: 0, x: 10)]
                };
            }
            if (cursor == 101 && release)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 102,
                    Events = [Event(102, PcCompatKeyViewerRawPhase.Up, slot: 0, x: 10)]
                };
            }
            return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = cursor };
        });
        var (adapter, overrides) = CreateConfiguration(
            PcCompatKeyViewerInputMode.Touch,
            2,
            staticUnityKeys: true);
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);

        Pump();
        var consumer = PcCompatKeyViewerConsumerRuntime.Snapshot(ModId);
        Assert.Multiple(() =>
        {
            Assert.That(PcCompatLegacyInputBridge.GetKeyOwned(TestKey.A, 769, ModId), Is.True);
            Assert.That(PcCompatLegacyInputBridge.GetKeyDownOwned(
                TestKey.A, 770, ModId), Is.True);
        });
        using (PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
                   ModId, 1, PcCompatManagedExecutionPhase.Update)))
        {
            Assert.Multiple(() =>
            {
                Assert.That(consumer.Registered, Is.True);
                Assert.That(consumer.Features.Single().Qualification,
                    Is.EqualTo(PcCompatKeyViewerConsumerQualification.ProvenAdapter));
                Assert.That(PcCompatLegacyInputBridge.GetKey(TestKey.A), Is.True);
                Assert.That(PcCompatLegacyInputBridge.GetKeyDown(TestKey.A, 771), Is.True);
                Assert.That(PcCompatLegacyInputBridge.GetKeyDown(TestKey.A, 771), Is.False);
                Assert.That(PcCompatLegacyInputBridge.GetKey(TestKey.B), Is.False);
            });
        }

        release = true;
        Pump();
        Assert.Multiple(() =>
        {
            Assert.That(
                PcCompatLegacyInputBridge.GetKeyOwned(TestKey.A, 773, ModId),
                Is.True,
                "a late held poller must replay the pending DOWN edge");
            Assert.That(
                PcCompatLegacyInputBridge.GetKeyOwned(TestKey.A, 773, ModId),
                Is.False,
                "the following held poll must replay the pending UP edge");
        });
        using (PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
                   ModId, 1, PcCompatManagedExecutionPhase.Update)))
        {
            Assert.Multiple(() =>
            {
                Assert.That(PcCompatLegacyInputBridge.GetKey(TestKey.A), Is.False);
                Assert.That(PcCompatLegacyInputBridge.GetKeyUp(TestKey.A, 772), Is.True);
                Assert.That(PcCompatLegacyInputBridge.GetKeyUp(TestKey.A, 772), Is.False);
            });
        }
    }

    [Test]
    public void AnyKeyDownCountsOneRawEdgeOnceAcrossMultipleFeatures()
    {
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
                return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = 150 };
            if (cursor == 150)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 151,
                    Events = [Event(151, PcCompatKeyViewerRawPhase.Down, slot: 0, x: 10)]
                };
            }
            return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = cursor };
        });
        var (firstAdapter, _) = CreateConfiguration(
            PcCompatKeyViewerInputMode.Touch,
            2,
            staticUnityKeys: true,
            featureId: "keyviewer-a");
        var (secondAdapter, _) = CreateConfiguration(
            PcCompatKeyViewerInputMode.Touch,
            2,
            staticUnityKeys: true,
            featureId: "keyviewer-b");
        var adapter = new PcCompatKeyViewerAdapterDocument
        {
            ModId = ModId,
            PackageSha256 = firstAdapter.PackageSha256,
            TargetGameRevision = firstAdapter.TargetGameRevision,
            ProxySurfaceHash = firstAdapter.ProxySurfaceHash,
            Assemblies = firstAdapter.Assemblies,
            Features = [firstAdapter.Features.Single(), secondAdapter.Features.Single()]
        };
        var overrides = PcCompatKeyViewerOverrideStore.CreateFor(adapter);
        foreach (var feature in overrides.Features)
        {
            feature.Enabled = true;
            feature.InputMode = PcCompatKeyViewerInputMode.Touch;
            feature.TouchLaneCount = 2;
        }
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);

        Pump();
        Assert.Multiple(() =>
        {
            Assert.That(PcCompatLegacyInputBridge.GetAnyKeyDownOwned(8801, ModId), Is.True);
            Assert.That(PcCompatLegacyInputBridge.GetAnyKeyDownOwned(8801, ModId), Is.False);
        });
    }

    [Test]
    public void ModalSettingsCaptureQuarantinesTouchBindingEdgesWithoutDisablingFutureInput()
    {
        var stage = 0;
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
                return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = 900 };
            if (cursor == 900 && stage == 0)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 901,
                    Events = [Event(901, PcCompatKeyViewerRawPhase.Down, 0, 10)]
                };
            }
            if (cursor == 901 && stage == 1)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 902,
                    Events = [Event(902, PcCompatKeyViewerRawPhase.Up, 0, 10)]
                };
            }
            if (cursor == 902 && stage == 2)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 903,
                    Events = [Event(903, PcCompatKeyViewerRawPhase.Down, 0, 10)]
                };
            }
            return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = cursor };
        });
        var (adapter, overrides) = CreateConfiguration(
            PcCompatKeyViewerInputMode.Touch,
            2,
            staticUnityKeys: true);
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);

        PcCompatLegacyInputBridge.SetModalInputCapture(true);
        Pump();
        Assert.Multiple(() =>
        {
            Assert.That(PcCompatLegacyInputBridge.GetAnyKeyDownOwned(8811, ModId), Is.False);
            Assert.That(PcCompatLegacyInputBridge.GetKeyDownOwned(
                TestKey.A, 8812, ModId), Is.False);
            Assert.That(PcCompatLegacyInputBridge.GetKeyOwned(
                TestKey.A, 8813, ModId), Is.False);
        });

        stage = 1;
        Pump();
        PcCompatLegacyInputBridge.SetModalInputCapture(false);
        Assert.Multiple(() =>
        {
            Assert.That(PcCompatLegacyInputBridge.GetAnyKeyDownOwned(8811, ModId), Is.False,
                "the menu touch must not replay after modal capture closes");
            Assert.That(PcCompatLegacyInputBridge.GetKeyDownOwned(
                TestKey.A, 8812, ModId), Is.False);
            Assert.That(PcCompatLegacyInputBridge.GetKeyOwned(
                TestKey.A, 8813, ModId), Is.False);
        });

        stage = 2;
        Pump();
        Assert.Multiple(() =>
        {
            Assert.That(PcCompatLegacyInputBridge.GetAnyKeyDownOwned(8811, ModId), Is.True);
            Assert.That(PcCompatLegacyInputBridge.GetKeyDownOwned(
                TestKey.A, 8812, ModId), Is.True);
            Assert.That(PcCompatLegacyInputBridge.GetKeyOwned(
                TestKey.A, 8813, ModId), Is.True);
        });
    }

    [Test]
    public void SettingsButtonActivationCannotBecomeAKeyBindingInTheSameGuiFrame()
    {
        var beginFrame = typeof(PcCompatLegacyInputBridge).GetMethod(
            "BeginSettingsGuiFrame",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        var notifyButton = typeof(PcCompatLegacyInputBridge).GetMethod(
            "NotifySettingsButtonActivated",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        var endFrame = typeof(PcCompatLegacyInputBridge).GetMethod(
            "EndSettingsGuiFrame",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.Multiple(() =>
        {
            Assert.That(beginFrame, Is.Not.Null);
            Assert.That(notifyButton, Is.Not.Null);
            Assert.That(endFrame, Is.Not.Null);
        });

        var stage = 0;
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
                return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = 910 };
            if (cursor == 910 && stage == 0)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 911,
                    Events = [Event(911, PcCompatKeyViewerRawPhase.Down, 0, 10)]
                };
            }
            if (cursor == 911 && stage == 1)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 912,
                    Events = [Event(912, PcCompatKeyViewerRawPhase.Up, 0, 10)]
                };
            }
            if (cursor == 912 && stage == 2)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 913,
                    Events = [Event(913, PcCompatKeyViewerRawPhase.Down, 0, 10)]
                };
            }
            return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = cursor };
        });
        var (adapter, overrides) = CreateConfiguration(
            PcCompatKeyViewerInputMode.Touch,
            2,
            staticUnityKeys: true);
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);

        Pump();
        beginFrame!.Invoke(null, null);
        try
        {
            notifyButton!.Invoke(null, null);
            Assert.Multiple(() =>
            {
                Assert.That(PcCompatLegacyInputBridge.GetAnyKeyDownOwned(8821, ModId), Is.False);
                Assert.That(PcCompatLegacyInputBridge.GetKeyDownOwned(
                    TestKey.A, 8822, ModId), Is.False);
                Assert.That(PcCompatLegacyInputBridge.GetKeyOwned(
                    TestKey.A, 8823, ModId), Is.False);
                Assert.That(PcCompatLegacyInputBridge.GetAsyncKeyStateOwned(
                    0x41, 8824, ModId), Is.Zero);
                Assert.That(
                    PcCompatLegacyInputBridge.GetDiagnosticStatus(ModId),
                    Does.Contain("settingsButtons=").And.Contain("settingsSuppressed="));
            });
        }
        finally
        {
            endFrame!.Invoke(null, null);
        }

        stage = 1;
        Pump();
        Assert.Multiple(() =>
        {
            Assert.That(PcCompatLegacyInputBridge.GetAnyKeyDownOwned(8821, ModId), Is.False);
            Assert.That(PcCompatLegacyInputBridge.GetKeyDownOwned(
                TestKey.A, 8822, ModId), Is.False);
            Assert.That(PcCompatLegacyInputBridge.GetKeyOwned(
                TestKey.A, 8823, ModId), Is.False);
        });
        stage = 2;
        Pump();
        Assert.Multiple(() =>
        {
            Assert.That(PcCompatLegacyInputBridge.GetAnyKeyDownOwned(8821, ModId), Is.True);
            Assert.That(PcCompatLegacyInputBridge.GetKeyDownOwned(
                TestKey.A, 8822, ModId), Is.True);
            Assert.That(PcCompatLegacyInputBridge.GetKeyOwned(
                TestKey.A, 8823, ModId), Is.True);
        });
    }

    [Test]
    public void ConsumerRegistrationGenerationPreventsReloadFromReusingEdgeCursors()
    {
        var tail = 200UL;
        var emitted = false;
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
                return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = tail };
            if (cursor == tail && !emitted)
            {
                emitted = true;
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = tail + 1,
                    Events = [Event(tail + 1, PcCompatKeyViewerRawPhase.Down, slot: 0, x: 10)]
                };
            }
            return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = cursor };
        });
        var (adapter, overrides) = CreateConfiguration(
            PcCompatKeyViewerInputMode.Touch,
            2,
            staticUnityKeys: true);

        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out _), Is.True);
        Pump();
        using (PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
                   ModId, 1, PcCompatManagedExecutionPhase.Update)))
        {
            Assert.That(PcCompatLegacyInputBridge.GetKeyDown(TestKey.A, 991), Is.True);
            Assert.That(PcCompatLegacyInputBridge.GetKeyDown(TestKey.A, 991), Is.False);
        }

        tail = 300;
        emitted = false;
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out _), Is.True);
        Pump();
        using (PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
                   ModId, 2, PcCompatManagedExecutionPhase.Update)))
        {
            Assert.That(
                PcCompatLegacyInputBridge.GetKeyDown(TestKey.A, 991),
                Is.True,
                "a replacement registration must start a new per-MOD edge cursor domain");
        }
    }

    [Test]
    public void HybridConsumerMapsAndroidKeyboardToCanonicalUnityIdentity()
    {
        var release = false;
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
                return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = 350 };
            if (cursor == 350)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 351,
                    Events =
                    [
                        Event(351, PcCompatKeyViewerRawPhase.Down, 0, 0,
                            source: PcCompatKeyViewerRawSource.Keyboard,
                            code: 29,
                            scanCode: 30,
                            deviceId: 4)
                    ]
                };
            }
            if (cursor == 351 && release)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 352,
                    Events =
                    [
                        Event(352, PcCompatKeyViewerRawPhase.Up, 0, 0,
                            source: PcCompatKeyViewerRawSource.Keyboard,
                            code: 29,
                            scanCode: 30,
                            deviceId: 4)
                    ]
                };
            }
            return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = cursor };
        });
        var (adapter, overrides) = CreateConfiguration(
            PcCompatKeyViewerInputMode.Hybrid,
            2,
            staticUnityKeys: true);
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);

        Pump();
        Assert.Multiple(() =>
        {
            Assert.That(PcCompatLegacyInputBridge.GetKeyOwned(TestKey.A, 3301, ModId), Is.True);
            Assert.That(PcCompatLegacyInputBridge.GetKeyDownOwned(TestKey.A, 3302, ModId), Is.True);
            Assert.That(PcCompatKeyViewerPreviewRuntime.Snapshot(ModId)
                    .Features.Single().UnmappedEventCount,
                Is.Zero);
        });

        release = true;
        Pump();
        Assert.That(PcCompatLegacyInputBridge.GetKeyUpOwned(TestKey.A, 3303, ModId), Is.True);
    }

    [Test]
    public void TouchContactLaneSelectionIgnoresExternalHeldState()
    {
        PcCompatTouchLaneMappingRuntime.SetMode(
            PcCompatTouchLaneMappingMode.TouchContacts);
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
                return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = 500 };
            if (cursor == 503)
                return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = 503 };
            return new PcCompatKeyViewerEventBatch
            {
                ProviderAvailable = true,
                Cursor = 503,
                Events =
                [
                    Event(501, PcCompatKeyViewerRawPhase.Down, 0, 0,
                        source: PcCompatKeyViewerRawSource.Keyboard,
                        code: 29,
                        scanCode: 30,
                        deviceId: 4),
                    Event(502, PcCompatKeyViewerRawPhase.Down, 0, 100),
                    Event(503, PcCompatKeyViewerRawPhase.Up, 0, 100)
                ]
            };
        });
        var (adapter, overrides) = CreateConfiguration(
            PcCompatKeyViewerInputMode.Hybrid,
            2,
            staticUnityKeys: true);
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);

        Pump();
        var feature = PcCompatKeyViewerPreviewRuntime.Snapshot(ModId).Features.Single();

        Assert.Multiple(() =>
        {
            Assert.That(feature.LastTransition?.LaneIdentity, Is.EqualTo("TouchLane:T1"));
            Assert.That(feature.DownOrdinals[0], Is.EqualTo(1));
            Assert.That(feature.DownOrdinals[1], Is.Zero);
            Assert.That(feature.HeldMask, Is.EqualTo(1u));
        });
    }

    [Test]
    public void VerifiedLoweredManualPlanCanActivateAProbableDynamicBinding()
    {
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
                return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = 400 };
            if (cursor == 400)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 401,
                    Events = [Event(401, PcCompatKeyViewerRawPhase.Down, slot: 0, x: 10)]
                };
            }
            return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = cursor };
        });
        var (adapter, overrides) = CreateConfiguration(
            PcCompatKeyViewerInputMode.Touch,
            2,
            coreReady: false,
            withBindingProvider: true);
        var feature = adapter.Features.Single();
        var provider = overrides.Features.Single().Roles.Single(role =>
            role.Role == "BindingProvider");
        var plan = new PcCompatKeyViewerLoweredConsumerPlan
        {
            ModId = ModId,
            PackageSha256 = adapter.PackageSha256,
            ProxySurfaceHash = adapter.ProxySurfaceHash,
            TargetGameRevision = adapter.TargetGameRevision,
            FeatureId = feature.Id,
            BindingProviderCandidateKey = provider.CandidateKey,
            Lanes =
            [
                new PcCompatKeyViewerLoweredLaneBinding
                {
                    Lane = 0,
                    Identities =
                    [
                        new PcCompatInputIdentity
                        {
                            Kind = PcCompatInputIdentityKind.UnityKeyCode,
                            Value = "97"
                        }
                    ]
                },
                new PcCompatKeyViewerLoweredLaneBinding
                {
                    Lane = 1,
                    Identities =
                    [
                        new PcCompatInputIdentity
                        {
                            Kind = PcCompatInputIdentityKind.WindowsVirtualKey,
                            Value = "90"
                        }
                    ]
                }
            ]
        };
        overrides.Features.Single().Roles.Clear();
        Assert.That(PcCompatKeyViewerLoweredConsumerPlanRegistry.Register(
            adapter, overrides, plan, out var planError), Is.True, planError);
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);

        Pump();
        var featureSnapshot = PcCompatKeyViewerPreviewRuntime.Snapshot(ModId).Features.Single();
        using (PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
                   ModId, 1, PcCompatManagedExecutionPhase.Update)))
        {
            Assert.Multiple(() =>
            {
                Assert.That(featureSnapshot.ConsumerQualification,
                    Is.EqualTo(PcCompatKeyViewerConsumerQualification.VerifiedLoweredBinding));
                Assert.That(PcCompatLegacyInputBridge.GetKey(TestKey.A), Is.True);
                Assert.That(PcCompatLegacyInputBridge.GetKeyDown(TestKey.A, 1991), Is.True);
            });
        }
    }

    [Test]
    public void AutoModeFreezesDeviceChoiceUntilTheNextSessionReset()
    {
        var stage = 0;
        PcCompatExternalInputDeviceRuntime.RegisterProvider(() =>
            new PcCompatExternalInputDeviceSnapshot(
                true,
                (uint)stage + 1,
                stage == 0
                    ? PcCompatExternalInputDeviceFlags.Keyboard
                    : PcCompatExternalInputDeviceFlags.None));
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
                return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = 500 };
            if (cursor == 500 && stage == 0)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 503,
                    Events =
                    [
                        Event(501, PcCompatKeyViewerRawPhase.Reset, -1, 0,
                            flags: (uint)PcCompatExternalInputDeviceFlags.Keyboard),
                        Event(502, PcCompatKeyViewerRawPhase.Down, 0, 10),
                        Event(503, PcCompatKeyViewerRawPhase.Down, 0, 0,
                            source: PcCompatKeyViewerRawSource.Keyboard,
                            code: 29,
                            scanCode: 30,
                            deviceId: 4)
                    ]
                };
            }
            if (cursor == 503 && stage == 1)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 504,
                    Events = [Event(504, PcCompatKeyViewerRawPhase.Down, 1, 800)]
                };
            }
            if (cursor == 504 && stage == 2)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 506,
                    Events =
                    [
                        Event(505, PcCompatKeyViewerRawPhase.Reset, -1, 0,
                            sessionGeneration: 8,
                            flags: 0),
                        Event(506, PcCompatKeyViewerRawPhase.Down, 0, 10,
                            sessionGeneration: 8)
                    ]
                };
            }
            return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = cursor };
        });
        var (adapter, overrides) = CreateConfiguration(
            PcCompatKeyViewerInputMode.Auto,
            2,
            staticUnityKeys: true);
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);

        Pump();
        var first = PcCompatKeyViewerPreviewRuntime.Snapshot(ModId).Features.Single();
        Assert.Multiple(() =>
        {
            Assert.That(first.RequestedInputMode, Is.EqualTo(PcCompatKeyViewerInputMode.Auto));
            Assert.That(first.InputMode, Is.EqualTo(PcCompatKeyViewerInputMode.External));
            Assert.That(first.SessionModeFrozen, Is.True);
            Assert.That(first.HeldMask, Is.EqualTo(1));
        });

        stage = 1;
        Pump();
        var hotPlug = PcCompatKeyViewerPreviewRuntime.Snapshot(ModId).Features.Single();
        Assert.Multiple(() =>
        {
            Assert.That(hotPlug.InputMode, Is.EqualTo(PcCompatKeyViewerInputMode.External));
            Assert.That(hotPlug.HeldMask, Is.EqualTo(1),
                "device changes and touch events cannot remap the current session");
        });

        stage = 2;
        Pump();
        var nextSession = PcCompatKeyViewerPreviewRuntime.Snapshot(ModId).Features.Single();
        Assert.Multiple(() =>
        {
            Assert.That(nextSession.InputMode, Is.EqualTo(PcCompatKeyViewerInputMode.Touch));
            Assert.That(nextSession.FrozenSessionGeneration, Is.EqualTo(8));
            Assert.That(nextSession.SessionDeviceFlags,
                Is.EqualTo(PcCompatExternalInputDeviceFlags.None));
            Assert.That(nextSession.HeldMask, Is.EqualTo(1));
        });
    }

    [Test]
    public void RewiredIntegerActionBridgeFeedsOriginalModPollingWithoutCallingDesktopPlayer()
    {
        var release = false;
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
                return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = 600 };
            if (cursor == 600)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 601,
                    Events = [Event(601, PcCompatKeyViewerRawPhase.Down, 0, 10)]
                };
            }
            if (cursor == 601 && release)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 602,
                    Events = [Event(602, PcCompatKeyViewerRawPhase.Up, 0, 10)]
                };
            }
            return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = cursor };
        });
        var (adapter, overrides) = CreateConfiguration(
            PcCompatKeyViewerInputMode.Touch,
            2,
            staticUnityKeys: true,
            staticIdentityKind: PcCompatInputIdentityKind.ActionId,
            sourceKind: PcCompatKeyViewerInputProfileKind.RewiredPolling);
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);
        var player = new TestRewiredPlayer();

        Pump();
        Assert.Multiple(() =>
        {
            Assert.That(PcCompatRewiredInputBridge.GetButtonOwned(
                player, 97, 6101, ModId), Is.True);
            Assert.That(PcCompatRewiredInputBridge.GetButtonDownOwned(
                player, 97, 6102, ModId), Is.True);
            Assert.That(PcCompatRewiredInputBridge.GetButtonDownOwned(
                player, 97, 6102, ModId), Is.False);
            Assert.That(player.QueryCount, Is.Zero,
                "Touch mode must not enter the desktop Rewired player object");
        });

        release = true;
        Pump();
        Assert.Multiple(() =>
        {
            Assert.That(PcCompatRewiredInputBridge.GetButtonOwned(
                player, 97, 6101, ModId), Is.False);
            Assert.That(PcCompatRewiredInputBridge.GetButtonUpOwned(
                player, 97, 6103, ModId), Is.True);
            Assert.That(player.QueryCount, Is.Zero);
        });
    }

    [Test]
    public void ManualCompatibleFallbackPublishesGenericSlotsCountsAndRainOnlyWhenEnabled()
    {
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
                return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = 700 };
            if (cursor == 700)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 701,
                    Events = [Event(701, PcCompatKeyViewerRawPhase.Down, 0, 10)]
                };
            }
            return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = cursor };
        });
        PcCompatClockAnchorRuntime.RegisterProvider(() => new PcCompatClockAnchorSnapshot
        {
            ProviderAvailable = true,
            MonotonicRawNs = 701_500_000
        });
        var (adapter, overrides) = CreateConfiguration(
            PcCompatKeyViewerInputMode.Touch,
            2,
            staticUnityKeys: true);
        overrides.Features.Single().CompatibleFallbackEnabled = true;
        IReadOnlyList<PcCompatKeyViewerFallbackFrame>? rendered = null;
        PcCompatKeyViewerFallbackRuntime.RegisterRenderer(frames => rendered = frames.ToArray());
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);
        PcCompatKeyViewerFallbackRuntime.RegisterOrUpdate(ModId, adapter, overrides);

        Pump();
        PcCompatKeyViewerFallbackRuntime.DispatchFrame(1f / 60f);
        var frame = rendered!.Single();
        Assert.Multiple(() =>
        {
            Assert.That(frame.ModId, Is.EqualTo(ModId));
            Assert.That(frame.Labels, Is.EqualTo(new[] { "T1", "T2" }));
            Assert.That(frame.HeldMask, Is.EqualTo(1));
            Assert.That(frame.Counts, Is.EqualTo(new ulong[] { 1, 0 }));
            Assert.That(frame.RainPulses.Single().Lane, Is.Zero);
        });
        var firstCounts = frame.Counts;
        var firstRainPulses = frame.RainPulses;
        PcCompatKeyViewerFallbackRuntime.DispatchFrame(1f / 60f);
        var secondFrame = rendered!.Single();
        Assert.Multiple(() =>
        {
            Assert.That(secondFrame, Is.SameAs(frame));
            Assert.That(secondFrame.Labels, Is.SameAs(frame.Labels));
            Assert.That(secondFrame.Counts, Is.SameAs(firstCounts));
            Assert.That(secondFrame.RainPulses, Is.SameAs(firstRainPulses));
        });

        overrides.Features.Single().CompatibleFallbackEnabled = false;
        PcCompatKeyViewerFallbackRuntime.RegisterOrUpdate(ModId, adapter, overrides);
        PcCompatKeyViewerFallbackRuntime.DispatchFrame(1f / 60f);
        Assert.That(rendered, Is.Empty);
    }

    [Test]
    public void CompatibleFallbackTracksFrozenInputModeWithoutReplacingLabelBuffer()
    {
        var stage = 0;
        PcCompatExternalInputDeviceRuntime.RegisterProvider(() =>
            new PcCompatExternalInputDeviceSnapshot(
                true,
                1,
                stage == 0
                    ? PcCompatExternalInputDeviceFlags.Keyboard
                    : PcCompatExternalInputDeviceFlags.None));
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            if (cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor)
                return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = 900 };
            if (cursor == 900 && stage == 0)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 902,
                    Events =
                    [
                        Event(901, PcCompatKeyViewerRawPhase.Reset, -1, 0,
                            flags: (uint)PcCompatExternalInputDeviceFlags.Keyboard),
                        Event(902, PcCompatKeyViewerRawPhase.Down, 0, 0,
                            source: PcCompatKeyViewerRawSource.Keyboard,
                            code: 29,
                            scanCode: 30,
                            deviceId: 4)
                    ]
                };
            }
            if (cursor == 902 && stage == 1)
            {
                return new PcCompatKeyViewerEventBatch
                {
                    ProviderAvailable = true,
                    Cursor = 904,
                    Events =
                    [
                        Event(903, PcCompatKeyViewerRawPhase.Reset, -1, 0,
                            sessionGeneration: 8,
                            flags: 0),
                        Event(904, PcCompatKeyViewerRawPhase.Down, 0, 10,
                            sessionGeneration: 8)
                    ]
                };
            }
            return new PcCompatKeyViewerEventBatch { ProviderAvailable = true, Cursor = cursor };
        });
        var (adapter, overrides) = CreateConfiguration(
            PcCompatKeyViewerInputMode.Auto,
            2,
            staticUnityKeys: true);
        overrides.Features.Single().CompatibleFallbackEnabled = true;
        var plan = new PcCompatKeyViewerLoweredConsumerPlan
        {
            ModId = ModId,
            PackageSha256 = adapter.PackageSha256,
            ProxySurfaceHash = adapter.ProxySurfaceHash,
            TargetGameRevision = adapter.TargetGameRevision,
            FeatureId = adapter.Features.Single().Id,
            BindingProviderCandidateKey = "test",
            Lanes =
            [
                new PcCompatKeyViewerLoweredLaneBinding
                {
                    Lane = 0,
                    Identities = [new PcCompatInputIdentity
                    {
                        Kind = PcCompatInputIdentityKind.UnityKeyCode,
                        Value = "97"
                    }]
                },
                new PcCompatKeyViewerLoweredLaneBinding
                {
                    Lane = 1,
                    Identities = [new PcCompatInputIdentity
                    {
                        Kind = PcCompatInputIdentityKind.UnityKeyCode,
                        Value = "115"
                    }]
                }
            ]
        };
        IReadOnlyList<PcCompatKeyViewerFallbackFrame>? rendered = null;
        PcCompatKeyViewerFallbackRuntime.RegisterRenderer(frames => rendered = frames.ToArray());
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);
        PcCompatKeyViewerFallbackRuntime.RegisterOrUpdate(
            ModId,
            adapter,
            overrides,
            [plan]);

        Pump();
        PcCompatKeyViewerFallbackRuntime.DispatchFrame(1f / 60f);
        var labels = rendered!.Single().Labels;
        Assert.That(labels, Is.EqualTo(new[] { "A", "S" }));

        stage = 1;
        Pump();
        PcCompatKeyViewerFallbackRuntime.DispatchFrame(1f / 60f);
        Assert.Multiple(() =>
        {
            Assert.That(rendered!.Single().Labels, Is.SameAs(labels));
            Assert.That(labels, Is.EqualTo(new[] { "T1", "T2" }));
        });
    }

    [Test]
    public void CompatibleFallbackDoesNotRenderWithoutAnActiveConsumer()
    {
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
            new PcCompatKeyViewerEventBatch
            {
                ProviderAvailable = true,
                Cursor = cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor ? 800UL : cursor
            });
        var (adapter, overrides) = CreateConfiguration(
            PcCompatKeyViewerInputMode.Touch,
            2,
            coreReady: false,
            withBindingProvider: false);
        overrides.Features.Single().CompatibleFallbackEnabled = true;
        IReadOnlyList<PcCompatKeyViewerFallbackFrame>? rendered = null;
        PcCompatKeyViewerFallbackRuntime.RegisterRenderer(frames => rendered = frames.ToArray());
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);
        PcCompatKeyViewerFallbackRuntime.RegisterOrUpdate(ModId, adapter, overrides);

        Pump();
        PcCompatKeyViewerFallbackRuntime.DispatchFrame(1f / 60f);

        Assert.That(rendered, Is.Empty);
    }

    [Test]
    public void CompatibleFallbackSteadyStateDoesNotAllocateOnUnityMain()
    {
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
            new PcCompatKeyViewerEventBatch
            {
                ProviderAvailable = true,
                Cursor = cursor == PcCompatKeyViewerEventRuntime.OpenAtTailCursor ? 850UL : cursor
            });
        var clock = new PcCompatClockAnchorSnapshot
        {
            ProviderAvailable = true,
            MonotonicRawNs = 850_000_000
        };
        PcCompatClockAnchorRuntime.RegisterProvider(() => clock);
        var (adapter, overrides) = CreateConfiguration(
            PcCompatKeyViewerInputMode.Touch,
            2,
            staticUnityKeys: true);
        overrides.Features.Single().CompatibleFallbackEnabled = true;
        PcCompatKeyViewerFallbackRuntime.RegisterRenderer(static _ => { });
        Assert.That(PcCompatKeyViewerPreviewRuntime.RegisterOrUpdate(
            ModId, adapter, overrides, out var error), Is.True, error);
        PcCompatKeyViewerFallbackRuntime.RegisterOrUpdate(ModId, adapter, overrides);
        Pump();
        for (var index = 0; index < 8; ++index)
            PcCompatKeyViewerFallbackRuntime.DispatchFrame(1f / 60f);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 128; ++index)
            PcCompatKeyViewerFallbackRuntime.DispatchFrame(1f / 60f);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.That(allocated, Is.Zero);
    }

    [Test]
    public void CompatibleFallbackExportsAndClearsRendererFailure()
    {
        PcCompatKeyViewerFallbackRuntime.RegisterRenderer(_ => { });
        PcCompatKeyViewerFallbackRuntime.ReportRendererFailure("mesh failure");
        Assert.That(PcCompatKeyViewerFallbackRuntime.Snapshot().RendererError,
            Is.EqualTo("mesh failure"));

        PcCompatKeyViewerFallbackRuntime.RegisterRenderer(_ => { });
        Assert.That(PcCompatKeyViewerFallbackRuntime.Snapshot().RendererError, Is.Null);
    }

    private static PcCompatKeyViewerRawEvent Event(
        ulong sequence,
        PcCompatKeyViewerRawPhase phase,
        int slot,
        float x,
        uint producerEpoch = 3,
        PcCompatKeyViewerRawSource source = PcCompatKeyViewerRawSource.Touch,
        int? code = null,
        int scanCode = 0,
        int deviceId = 1,
        uint sessionGeneration = 7,
        uint? flags = null,
        int pointerCount = 1,
        int androidFlags = 0)
        => new(
            sequence,
            (long)sequence * 1_000_000,
            (uint)sequence,
            sessionGeneration,
            producerEpoch,
            PcCompatKeyViewerInputOrigin.AsyncInput,
            source,
            phase,
            code ?? slot + 100,
            slot,
            pointerCount,
            scanCode,
            0,
            deviceId,
            0,
            androidFlags,
            0x1002,
            1000,
            500,
            x,
            100,
            flags ?? (slot >= 0 ? 1u << slot : 0));

    private static void Pump()
    {
        PcCompatKeyViewerPreviewRuntime.DispatchFrame();
        Assert.That(
            PcCompatKeyViewerPreviewRuntime.WaitForIdle(TimeSpan.FromSeconds(2)),
            Is.True,
            "KeyViewer actor pump did not become idle");
    }

    private static (PcCompatKeyViewerAdapterDocument Adapter,
        PcCompatKeyViewerOverrideDocument Overrides) CreateConfiguration(
        PcCompatKeyViewerInputMode mode,
        int laneCount,
        string modId = ModId,
        bool staticUnityKeys = false,
        bool coreReady = true,
        bool withBindingProvider = false,
        string featureId = "keyviewer",
        PcCompatInputIdentityKind staticIdentityKind =
            PcCompatInputIdentityKind.UnityKeyCode,
        PcCompatKeyViewerInputProfileKind sourceKind =
            PcCompatKeyViewerInputProfileKind.LegacyUnityPolling,
        bool duplicateStaticIdentity = false)
    {
        var proven = new PcCompatAdapterEvidence
        {
            Status = PcCompatAdapterEvidenceStatus.Proven,
            Evidence = ["test"]
        };
        var probable = new PcCompatAdapterEvidence
        {
            Status = PcCompatAdapterEvidenceStatus.Probable,
            Evidence = ["test candidate"],
            FirstBreak = "test requires lowered plan"
        };
        var capabilityEvidence = coreReady ? proven : probable;
        var feature = new PcCompatKeyViewerFeatureAdapter
        {
            Id = featureId,
            DisplayName = "Key Viewer",
            SourceProfiles =
            [
                new PcCompatKeyViewerSourceProfile
                {
                    Id = "legacy",
                    Kind = sourceKind,
                    EntryPoints = [sourceKind == PcCompatKeyViewerInputProfileKind.RewiredPolling
                        ? "Rewired.Player.GetButtonDown"
                        : "UnityEngine.Input.GetKeyDown"],
                    Evidence = proven
                }
            ],
            LaneGroups =
            [
                new PcCompatKeyViewerLaneGroup
                {
                    Id = "touch",
                    Lanes = Enumerable.Range(1, laneCount).Select(index =>
                        new PcCompatKeyViewerLane
                        {
                            Id = $"touch-{index}",
                            DisplayLabel = $"T{index}",
                            Binding = staticUnityKeys
                                ? new PcCompatLaneBinding
                                {
                                    Kind = PcCompatLaneBindingKind.DirectIdentity,
                                    Identities =
                                    [
                                        new PcCompatInputIdentity
                                        {
                                            Kind = staticIdentityKind,
                                            Value = (duplicateStaticIdentity ? 97 : 96 + index).ToString(
                                                System.Globalization.CultureInfo.InvariantCulture)
                                        }
                                    ],
                                    SourceProfileId = "legacy"
                                }
                                : new PcCompatLaneBinding
                                {
                                    Kind = PcCompatLaneBindingKind.TouchLane,
                                    TouchLane = index,
                                    SourceProfileId = "legacy"
                                }
                        }).ToArray()
                }
            ],
            Roles = withBindingProvider
                ?
                [
                    new PcCompatKeyViewerRoleBinding
                    {
                        Role = "BindingProvider",
                        AssemblyName = "TestMod",
                        TypeName = "TestMod.KeyViewer",
                        MemberName = "GetKeys",
                        MemberKind = "Method",
                        Evidence = probable
                    }
                ]
                : Array.Empty<PcCompatKeyViewerRoleBinding>(),
            Visibility = new PcCompatKeyViewerPredicate
            {
                Kind = "Always",
                Expression = "true",
                Evidence = proven
            },
            InputActivation = new PcCompatKeyViewerPredicate
            {
                Kind = "Always",
                Expression = "true",
                Evidence = proven
            },
            CountSemantics = new PcCompatKeyViewerCountSemantics
            {
                Clock = "MonotonicRawNs",
                ResetEntryPoint = "none"
            },
            Capabilities = new PcCompatKeyViewerEvidenceMatrix
            {
                Input = proven,
                Lane = capabilityEvidence,
                Transition = capabilityEvidence,
                Count = capabilityEvidence,
                Kps = capabilityEvidence,
                Rain = capabilityEvidence,
                Presentation = capabilityEvidence,
                Visibility = capabilityEvidence,
                InputActivation = capabilityEvidence,
                Settings = capabilityEvidence,
                Persistence = capabilityEvidence
            }
        };
        var adapter = new PcCompatKeyViewerAdapterDocument
        {
            ModId = modId,
            PackageSha256 = new string('a', 64),
            TargetGameRevision = 143,
            ProxySurfaceHash = new string('b', 64),
            Assemblies = Array.Empty<PcCompatAdapterAssemblyFingerprint>(),
            Features = [feature]
        };
        var overrides = PcCompatKeyViewerOverrideStore.CreateFor(adapter);
        overrides.Features.Single().Enabled = true;
        overrides.Features.Single().InputMode = mode;
        overrides.Features.Single().TouchLaneCount = laneCount;
        if (withBindingProvider)
        {
            var role = feature.Roles.Single(value => value.Role == "BindingProvider");
            overrides.Features.Single().Roles.Add(new PcCompatKeyViewerRoleOverride
            {
                Role = role.Role,
                AssemblyName = role.AssemblyName,
                TypeName = role.TypeName,
                MemberName = role.MemberName,
                MemberKind = role.MemberKind
            });
        }
        return (adapter, overrides);
    }

    private enum TestKey
    {
        A = 97,
        B = 98
    }

    private sealed class TestRewiredPlayer
    {
        public int QueryCount { get; private set; }

        public bool GetButton(int actionId)
        {
            ++QueryCount;
            return false;
        }

        public bool GetButtonDown(int actionId)
        {
            ++QueryCount;
            return false;
        }

        public bool GetButtonUp(int actionId)
        {
            ++QueryCount;
            return false;
        }
    }
}

using StArray.ModManager.Android.Native;
using StArray.ModManager.Runtime;
using StArray.ModManager.RuntimeAbstractions;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class InputEventsIsolationTests
{
    [SetUp]
    public void SetUp()
    {
        InputEvents.ResetForTests();
        ModOwnedResourceRegistry.ClearForTests();
    }

    [TearDown]
    public void TearDown()
    {
        InputEvents.ResetForTests();
        ModOwnedResourceRegistry.ClearForTests();
    }

    [Test]
    public void PublicAndroidInputContractMatchesNdkValues()
    {
        Assert.Multiple(() =>
        {
            Assert.That((int)AndroidInput.MotionAction.HoverMove, Is.EqualTo(7));
            Assert.That((int)AndroidInput.MotionAction.Scroll, Is.EqualTo(8));
            Assert.That((int)AndroidInput.MotionAction.HoverEnter, Is.EqualTo(9));
            Assert.That((int)AndroidInput.MotionAction.HoverExit, Is.EqualTo(10));
            Assert.That((int)AndroidInput.MotionAction.ButtonPress, Is.EqualTo(11));
            Assert.That((int)AndroidInput.MotionAction.ButtonRelease, Is.EqualTo(12));
            Assert.That(AndroidInput.ActionMask, Is.EqualTo(0xff));
            Assert.That(AndroidInput.ActionPointerIndexMask, Is.EqualTo(0xff00));
            Assert.That(AndroidInput.ActionPointerIndexShift, Is.EqualTo(8));
            Assert.That(
                AndroidInput.GetMainAction((3 << 8) | (int)AndroidInput.MotionAction.PointerDown),
                Is.EqualTo(AndroidInput.MotionAction.PointerDown));
            Assert.That(
                AndroidInput.GetPointerIndex((3 << 8) | (int)AndroidInput.MotionAction.PointerDown),
                Is.EqualTo(3));
            Assert.That(typeof(AndroidInput).GetMethod(nameof(AndroidInput.AMotionEvent_getEventTime)),
                Is.Not.Null);
            Assert.That(typeof(AndroidInput).GetMethod(nameof(AndroidInput.AMotionEvent_getDownTime)),
                Is.Not.Null);
            Assert.That(typeof(Dobby).GetMethod(nameof(Dobby.GetLayerCount), [typeof(nint)]),
                Is.Not.Null);
            Assert.That(Dobby.GetLayerCount(nint.Zero), Is.Zero);
            Assert.That(RuntimeManager.GetObjectClass(nint.Zero), Is.Null);
        });
    }

    [Test]
    public void DuplicateSubscriptionsFollowNormalEventRemovalSemantics()
    {
        var calls = 0;
        Action<TouchEventInfo> handler = _ => ++calls;

        InputEvents.OnTouch += handler;
        InputEvents.OnTouch += handler;
        InputEvents.OnTouch -= handler;
        InputEvents.RaiseForTests(CreateTouch());

        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(InputEvents.HasSubscribers, Is.True);
        });

        InputEvents.OnTouch -= handler;
        Assert.That(InputEvents.HasSubscribers, Is.False);
    }

    [Test]
    public void SubscriberFailureDoesNotBlockOtherSubscribers()
    {
        var successfulCalls = 0;
        InputEvents.OnTouch += _ => throw new InvalidOperationException("fixture failure");
        InputEvents.OnTouch += _ => ++successfulCalls;

        Assert.DoesNotThrow(() => InputEvents.RaiseForTests(CreateTouch()));
        Assert.That(successfulCalls, Is.EqualTo(1));
    }

    [Test]
    public void TimestampChannelSkipsMoveAndReceivesPressReleaseActions()
    {
        var actions = new List<AndroidInput.MotionAction>();
        InputEvents.OnTouchTimestamp += info => actions.Add(info.Action);

        InputEvents.RaiseForTests(CreateTouch(AndroidInput.MotionAction.Move));
        InputEvents.RaiseForTests(CreateTouch(AndroidInput.MotionAction.Down, 11));
        InputEvents.RaiseForTests(CreateTouch(AndroidInput.MotionAction.Up, 12));

        Assert.That(actions, Is.EqualTo(new[]
        {
            AndroidInput.MotionAction.Down,
            AndroidInput.MotionAction.Up
        }));
    }

    [Test]
    public void DuplicateFingerprintIsFilteredWithinTheSharedWindow()
    {
        const int rawAction = (2 << 8) | (int)AndroidInput.MotionAction.PointerDown;

        Assert.Multiple(() =>
        {
            Assert.That(InputEvents.IsDuplicateForTests(rawAction, 2, 3, 7, 123456), Is.False);
            Assert.That(InputEvents.IsDuplicateForTests(rawAction, 2, 3, 7, 123456), Is.True);
            Assert.That(InputEvents.IsDuplicateForTests(rawAction, 2, 3, 7, 123457), Is.False);
        });
    }

    [Test]
    public void SuspendedGenerationStopsCallbacksAndResumesWithoutResubscribing()
    {
        var session = CreateActiveSession("StArray.Android.Native", "input-suspend");
        var key = session.CurrentKey;
        var calls = 0;
        using (HookHelper.EnterOwnerScope(key.OwnerId, session, key))
            InputEvents.OnTouch += _ => ++calls;

        InputEvents.RaiseForTests(CreateTouch());
        Assert.That(session.TryBeginRetirement(key), Is.True);
        Assert.That(session.WaitForQuiescence(key, TimeSpan.Zero), Is.True);
        Assert.That(session.TryCompleteSuspension(key), Is.True);
        InputEvents.RaiseForTests(CreateTouch(eventTime: 2));

        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(InputEvents.HasSubscribers, Is.True);
            Assert.That(
                ModOwnedResourceRegistry.Snapshot(key, includeRetired: false),
                Has.Count.EqualTo(1));
        });

        Assert.That(session.TryResume(out var resumedKey), Is.True);
        Assert.That(resumedKey.Matches(key), Is.True);
        InputEvents.RaiseForTests(CreateTouch(eventTime: 3));
        Assert.That(calls, Is.EqualTo(2));
    }

    [Test]
    public void TerminalRetirementRemovesDelegateAndResourceEntry()
    {
        var session = CreateActiveSession("StArray.Android.Native", "input-retire");
        var key = session.CurrentKey;
        var calls = 0;
        using (HookHelper.EnterOwnerScope(key.OwnerId, session, key))
            InputEvents.OnTouch += _ => ++calls;

        Assert.That(session.TryBeginRetirement(key), Is.True);
        Assert.That(session.WaitForQuiescence(key, TimeSpan.Zero), Is.True);
        Assert.That(session.TryCompleteRetirement(key), Is.True);

        InputEvents.RaiseForTests(CreateTouch());
        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.Zero);
            Assert.That(InputEvents.HasSubscribers, Is.False);
            Assert.That(
                ModOwnedResourceRegistry.Snapshot(key, includeRetired: false),
                Is.Empty);
        });
    }

    [Test]
    public void FailedLoadRemovesSubscriptionsBeforeAssemblyContextRelease()
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad("StArray.Android.Native", "input-failed-load");
        using (HookHelper.EnterOwnerScope(key.OwnerId, session, key))
            InputEvents.OnTouch += _ => { };

        Assert.That(InputEvents.HasSubscribers, Is.True);
        Assert.That(session.TryAbortLoad(key), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(InputEvents.HasSubscribers, Is.False);
            Assert.That(
                ModOwnedResourceRegistry.Snapshot(key, includeRetired: false),
                Is.Empty);
        });
    }

    [Test]
    public void DifferentModGenerationsAreDispatchedAndRetiredIndependently()
    {
        var first = CreateActiveSession("StArray.Android.Native", "input-first");
        var second = CreateActiveSession("StArray.Android.Native", "input-second");
        var firstCalls = 0;
        var secondCalls = 0;
        using (HookHelper.EnterOwnerScope(first.CurrentKey.OwnerId, first, first.CurrentKey))
            InputEvents.OnTouch += _ => ++firstCalls;
        using (HookHelper.EnterOwnerScope(second.CurrentKey.OwnerId, second, second.CurrentKey))
            InputEvents.OnTouch += _ => ++secondCalls;

        Assert.That(first.TryBeginRetirement(first.CurrentKey), Is.True);
        Assert.That(first.WaitForQuiescence(first.CurrentKey, TimeSpan.Zero), Is.True);
        Assert.That(first.TryCompleteRetirement(first.CurrentKey), Is.True);
        InputEvents.RaiseForTests(CreateTouch());

        Assert.Multiple(() =>
        {
            Assert.That(firstCalls, Is.Zero);
            Assert.That(secondCalls, Is.EqualTo(1));
            Assert.That(InputEvents.HasSubscribers, Is.True);
        });
    }

    private static ModRuntimeSession CreateActiveSession(string loaderKind, string modId)
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(loaderKind, modId);
        Assert.That(session.TryPublishActive(key), Is.True);
        return session;
    }

    private static TouchEventInfo CreateTouch(
        AndroidInput.MotionAction action = AndroidInput.MotionAction.Down,
        long eventTime = 1)
        => new(action, 0, 0, eventTime, 100f, 200f);
}

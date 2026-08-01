using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatKeyViewerEventRuntimeTests
{
    [TearDown]
    public void TearDown()
        => PcCompatKeyViewerEventRuntime.ClearProvider();

    [Test]
    public void ProviderPreservesOrderedEdgesAndExplicitLoss()
    {
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, capacity) =>
            new PcCompatKeyViewerEventBatch
            {
                ProviderAvailable = true,
                Cursor = cursor + 2,
                DroppedBeforeCursor = 3,
                Events =
                [
                    CreateEvent(cursor + 1, PcCompatKeyViewerRawPhase.Down),
                    CreateEvent(cursor + 2, PcCompatKeyViewerRawPhase.Up)
                ]
            });

        var batch = PcCompatKeyViewerEventRuntime.Read(40, 2);

        Assert.Multiple(() =>
        {
            Assert.That(batch.ProviderAvailable, Is.True);
            Assert.That(batch.Cursor, Is.EqualTo(42));
            Assert.That(batch.DroppedBeforeCursor, Is.EqualTo(3));
            Assert.That(batch.IsLossless, Is.False);
            Assert.That(batch.Events.Select(value => value.Sequence), Is.EqualTo(new ulong[] { 41, 42 }));
            Assert.That(batch.Events.Select(value => value.Phase), Is.EqualTo(new[]
            {
                PcCompatKeyViewerRawPhase.Down,
                PcCompatKeyViewerRawPhase.Up
            }));
        });
    }

    [Test]
    public void OpenAtTailUsesTheReservedCursorWithoutReplayingHistory()
    {
        ulong observedCursor = 0;
        PcCompatKeyViewerEventRuntime.RegisterProvider((cursor, _) =>
        {
            observedCursor = cursor;
            return new PcCompatKeyViewerEventBatch
            {
                ProviderAvailable = true,
                Cursor = 92,
                Events = Array.Empty<PcCompatKeyViewerRawEvent>()
            };
        });

        var batch = PcCompatKeyViewerEventRuntime.OpenAtTail();

        Assert.Multiple(() =>
        {
            Assert.That(observedCursor, Is.EqualTo(ulong.MaxValue));
            Assert.That(batch.ProviderAvailable, Is.True);
            Assert.That(batch.Cursor, Is.EqualTo(92));
            Assert.That(batch.Events, Is.Empty);
            Assert.That(batch.IsLossless, Is.True);
        });
    }

    [Test]
    public void MissingOrThrowingProviderFailsClosed()
    {
        Assert.That(PcCompatKeyViewerEventRuntime.Read(0).ProviderAvailable, Is.False);

        PcCompatKeyViewerEventRuntime.RegisterProvider((_, _) =>
            throw new InvalidOperationException("test"));

        Assert.That(PcCompatKeyViewerEventRuntime.Read(0).ProviderAvailable, Is.False);
    }

    [Test]
    public void ThrowingWakeProviderDisablesItselfInsteadOfBusyRetrying()
    {
        PcCompatKeyViewerEventRuntime.RegisterWakeProvider(
            (_, _) => throw new EntryPointNotFoundException("test"),
            () => { });

        Assert.Multiple(() =>
        {
            Assert.That(PcCompatKeyViewerEventRuntime.WaitForChange(0), Is.False);
            Assert.That(PcCompatKeyViewerEventRuntime.HasWakeProvider, Is.False);
        });
    }

    private static PcCompatKeyViewerRawEvent CreateEvent(
        ulong sequence,
        PcCompatKeyViewerRawPhase phase)
        => new(
            sequence,
            1_000_000 + (long)sequence,
            7,
            3,
            2,
            PcCompatKeyViewerInputOrigin.AsyncInput,
            PcCompatKeyViewerRawSource.Touch,
            phase,
            9,
            1,
            2,
            0,
            0,
            4,
            0,
            0,
            0x1002,
            2400,
            1080,
            1200f,
            540f,
            2);
}

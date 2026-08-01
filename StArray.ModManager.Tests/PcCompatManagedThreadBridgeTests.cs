using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatManagedThreadBridgeTests
{
    [Test]
    public void AbortInterruptsBlockingThreadWithoutThrowing()
    {
        using var ready = new ManualResetEventSlim();
        using var interrupted = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            ready.Set();
            try
            {
                Thread.Sleep(Timeout.Infinite);
            }
            catch (ThreadInterruptedException)
            {
                interrupted.Set();
            }
        });
        thread.Start();
        Assert.That(ready.Wait(TimeSpan.FromSeconds(2)), Is.True);

        Assert.DoesNotThrow(() => PcCompatManagedThreadBridge.Abort(thread));

        Assert.Multiple(() =>
        {
            Assert.That(interrupted.Wait(TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(thread.Join(TimeSpan.FromSeconds(2)), Is.True);
        });
    }

    [Test]
    public void AbortPreservesNullInstanceFailureAndIgnoresCompletedThread()
    {
        Assert.Throws<NullReferenceException>(() =>
            PcCompatManagedThreadBridge.Abort(null!));
        var completed = new Thread(static () => { });
        completed.Start();
        Assert.That(completed.Join(TimeSpan.FromSeconds(2)), Is.True);
        Assert.DoesNotThrow(() => PcCompatManagedThreadBridge.Abort(completed));
    }
}

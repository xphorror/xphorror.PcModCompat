using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class PcCompatManagedThreadBridgeTests
{
    private const string ModId = "pccompat.thread.test";
    private const long Generation = 83;

    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        PcCompatManagedPathBridge.ClearAllRootsForTests();
        _root = Path.Combine(Path.GetTempPath(), "pccompat-thread-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        PcCompatManagedPathBridge.ClearAllRootsForTests();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void RunWithoutManagedScopeIsRejected()
    {
        Assert.That(
            () => PcCompatManagedThreadBridge.Run(() => { }),
            Throws.InvalidOperationException.With.Message.Contains("active managed scope"));
    }

    [Test]
    public void CreateWithoutManagedScopeIsRejected()
    {
        Assert.That(
            () => PcCompatManagedThreadBridge.Create(() => { }),
            Throws.InvalidOperationException.With.Message.Contains("active managed scope"));
    }

    [Test]
    public async Task RunFlowsOwnerAcrossAsyncVoidContinuationWithoutCallerLeak()
    {
        var roots = BindRoots();
        var state = new PcCompatManagedExecutionState(
            ModId,
            Generation,
            PcCompatManagedExecutionPhase.Update);
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        PcCompatManagedExecutionState? beforeAwait = null;
        PcCompatManagedExecutionState? afterAwait = null;
        Exception? failure = null;
        Action saveData = async () =>
        {
            try
            {
                beforeAwait = PcCompatManagedExecutionContext.Current;
                PcCompatManagedPathBridge.FileWriteAllText("before.dat", "before");
                await Task.Delay(10).ConfigureAwait(false);
                afterAwait = PcCompatManagedExecutionContext.Current;
                PcCompatManagedPathBridge.FileWriteAllText("after.dat", "after");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                completion.TrySetResult();
            }
        };

        Task dispatch;
        using (PcCompatManagedExecutionContext.Enter(state))
            dispatch = PcCompatManagedThreadBridge.Run(saveData);

        Assert.That(PcCompatManagedExecutionContext.Current, Is.Null);
        await dispatch.WaitAsync(TimeSpan.FromSeconds(2));
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var unrelated = await Task.Run(() => PcCompatManagedExecutionContext.Current);

        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.Null);
            Assert.That(beforeAwait, Is.SameAs(state));
            Assert.That(afterAwait, Is.SameAs(state));
            Assert.That(
                File.ReadAllText(Path.Combine(roots.ConfigRoot, "before.dat"), System.Text.Encoding.UTF8),
                Is.EqualTo("before"));
            Assert.That(
                File.ReadAllText(Path.Combine(roots.ConfigRoot, "after.dat"), System.Text.Encoding.UTF8),
                Is.EqualTo("after"));
            Assert.That(unrelated, Is.Null, "flowing scope must not leak into unrelated tasks");
            Assert.That(PcCompatManagedExecutionContext.Current, Is.Null);
        });
    }

    [Test]
    public async Task CreatedThreadFlowsOwnerIntoAsyncSaveWithoutCallerLeak()
    {
        var roots = BindRoots();
        var state = new PcCompatManagedExecutionState(
            ModId,
            Generation,
            PcCompatManagedExecutionPhase.Enable);
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        PcCompatManagedExecutionState? listenerState = null;
        PcCompatManagedExecutionState? saveState = null;
        Exception? failure = null;

        Thread listener;
        using (PcCompatManagedExecutionContext.Enter(state))
        {
            listener = PcCompatManagedThreadBridge.Create(() =>
            {
                try
                {
                    listenerState = PcCompatManagedExecutionContext.Current;
                    PcCompatManagedThreadBridge.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(10).ConfigureAwait(false);
                            saveState = PcCompatManagedExecutionContext.Current;
                            PcCompatManagedPathBridge.FileWriteAllText("listener-save.dat", "saved");
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                        }
                        finally
                        {
                            completion.TrySetResult();
                        }
                    });
                }
                catch (Exception ex)
                {
                    failure = ex;
                    completion.TrySetResult();
                }
            });
        }

        listener.Start();
        Assert.That(listener.Join(TimeSpan.FromSeconds(2)), Is.True);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.Null);
            Assert.That(listenerState, Is.SameAs(state));
            Assert.That(saveState, Is.SameAs(state));
            Assert.That(
                File.ReadAllText(
                    Path.Combine(roots.ConfigRoot, "listener-save.dat"),
                    System.Text.Encoding.UTF8),
                Is.EqualTo("saved"));
            Assert.That(PcCompatManagedExecutionContext.Current, Is.Null);
        });
    }

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

    private PcCompatModPathRoots BindRoots()
    {
        var roots = new PcCompatModPathRoots
        {
            InstallRoot = Path.Combine(_root, "install"),
            ConfigRoot = Path.Combine(_root, "config"),
            CacheRoot = Path.Combine(_root, "cache"),
            LogRoot = Path.Combine(_root, "log"),
            TempRoot = Path.Combine(_root, "temp"),
            DataOverlayRoot = Path.Combine(_root, "data-overlay")
        };
        foreach (var root in new[]
                 {
                     roots.InstallRoot, roots.ConfigRoot, roots.CacheRoot,
                     roots.LogRoot, roots.TempRoot, roots.DataOverlayRoot
                 })
        {
            Directory.CreateDirectory(root);
        }
        PcCompatManagedPathBridge.BindRoots(ModId, Generation, roots);
        return roots;
    }
}

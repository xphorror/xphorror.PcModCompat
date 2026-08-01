using JALib.Core;
using JALib.Tools;
using System.Collections;
using UnityEngine;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class PcCompatJALibMainThreadTests
{
    [Test]
    public void WorkerActionWaitsForCompatUpdateAndRunsBeforeModUpdate()
    {
        var order = new List<string>();
        var mod = new TestMod(order);
        mod.CompatSetup();
        mod.CompatEnable();
        var unityMainThread = Environment.CurrentManagedThreadId;
        var actionThread = 0;

        Task.Run(() => MainThread.Run(mod, () =>
        {
            actionThread = Environment.CurrentManagedThreadId;
            order.Add("action");
        })).GetAwaiter().GetResult();

        Assert.That(order, Is.Empty, "worker action executed before UnityMain update");
        mod.CompatUpdate(1f / 60f);

        Assert.Multiple(() =>
        {
            Assert.That(actionThread, Is.EqualTo(unityMainThread));
            Assert.That(order, Is.EqualTo(new[] { "action", "update" }));
        });
        mod.CompatDisable();
    }

    [Test]
    public void QueuedActionFromDisabledGenerationDoesNotRunAfterReenable()
    {
        var order = new List<string>();
        var mod = new TestMod(order);
        mod.CompatSetup();
        mod.CompatEnable();

        Task.Run(() => MainThread.Run(mod, () => order.Add("stale")))
            .GetAwaiter()
            .GetResult();
        mod.CompatDisable();
        mod.CompatEnable();
        mod.CompatUpdate(1f / 60f);

        Assert.That(order, Is.EqualTo(new[] { "update" }));
        Assert.That(MainThread.GetDiagnosticStatus(), Does.Contain("inactiveDropped="));
        mod.CompatDisable();
    }

    [Test]
    public void ForceQueueFromUnityMainWaitsUntilNextCompatUpdate()
    {
        var order = new List<string>();
        var mod = new TestMod(order);
        mod.CompatSetup();
        mod.CompatEnable();

        MainThread.ForceQueue(mod, () => order.Add("forced"));

        Assert.That(order, Is.Empty, "ForceQueue executed inline on UnityMain");
        mod.CompatUpdate(1f / 60f);
        Assert.That(order, Is.EqualTo(new[] { "forced", "update" }));
        mod.CompatDisable();
    }

    [Test]
    public void WaitForMainThreadAndTaskContinuationResumeOnCompatUpdate()
    {
        var order = new List<string>();
        var mod = new TestMod(order);
        mod.CompatSetup();
        mod.CompatEnable();
        var unityMainThread = Environment.CurrentManagedThreadId;
        var continuationThread = 0;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var wait = MainThread.WaitForMainThread();
        completion.Task.OnCompleted(mod, _ =>
        {
            continuationThread = Environment.CurrentManagedThreadId;
            order.Add("continuation");
        });
        completion.SetResult(true);
        SpinWait.SpinUntil(() => completion.Task.IsCompleted, 1000);

        Assert.Multiple(() =>
        {
            Assert.That(wait.IsCompleted, Is.False);
            Assert.That(order, Is.Empty);
        });

        mod.CompatUpdate(1f / 60f);

        Assert.Multiple(() =>
        {
            Assert.That(wait.IsCompletedSuccessfully, Is.True);
            Assert.That(continuationThread, Is.EqualTo(unityMainThread));
            Assert.That(order, Is.EqualTo(new[] { "continuation", "update" }));
        });
        mod.CompatDisable();
    }

    [Test]
    public void CoroutineAdvancesAcrossFramesAndCanBeStoppedByHandle()
    {
        var order = new List<string>();
        var mod = new CoroutineMod(order);
        mod.CompatSetup();
        mod.CompatEnable();

        Assert.That(order, Is.EqualTo(new[] { "start" }));
        mod.CompatUpdate(1f / 60f);
        Assert.That(order, Is.EqualTo(new[] { "start", "after-frame", "update" }));

        MainThread.StopCoroutine(mod.Handle!);
        mod.CompatUpdate(1f / 60f);
        Assert.That(
            order,
            Is.EqualTo(new[] { "start", "after-frame", "update", "update" }));
        mod.CompatDisable();
    }

    private sealed class TestMod(List<string> order) : JAMod
    {
        protected override void OnUpdate(float deltaTime)
            => order.Add("update");
    }

    private sealed class CoroutineMod(List<string> order) : JAMod
    {
        public Coroutine? Handle { get; private set; }

        protected override void OnEnable()
            => Handle = MainThread.StartCoroutine(Run());

        protected override void OnUpdate(float deltaTime)
            => order.Add("update");

        private IEnumerator Run()
        {
            order.Add("start");
            yield return null;
            order.Add("after-frame");
            yield return null;
            order.Add("should-not-run");
        }
    }
}

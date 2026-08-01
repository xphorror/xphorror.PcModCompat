using JALib.Core;
using JALib.Core.Patch;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class PcCompatJALibLifecycleTests
{
    [SetUp]
    public void SetUp()
        => JAPatcher.ClearRegisteredPatches();

    [Test]
    public void ModLifecycleIncludesAsyncFixedLateAndUnloadWithoutThreadPoolUnityCallbacks()
    {
        var events = new List<string>();
        var mod = new LifecycleMod(events);
        mod.CompatSetup();
        mod.CompatEnable();

        SpinWait.SpinUntil(
            () => events.Contains("enable-async"),
            TimeSpan.FromSeconds(2));
        mod.CompatUpdate(0.02f);
        mod.CompatDisable();
        SpinWait.SpinUntil(
            () => events.Contains("disable-async"),
            TimeSpan.FromSeconds(2));
        mod.CompatUnload();

        Assert.Multiple(() =>
        {
            Assert.That(events, Does.Contain("setup"));
            Assert.That(events, Does.Contain("enable"));
            Assert.That(events, Does.Contain("enable-async"));
            Assert.That(events, Does.Contain("fixed"));
            Assert.That(events, Does.Contain("update"));
            Assert.That(events, Does.Contain("late"));
            Assert.That(events, Does.Contain("disable"));
            Assert.That(events, Does.Contain("disable-async"));
            Assert.That(events, Does.Contain("unload"));
            Assert.That(mod.UnityCallbackThreads, Has.All.EqualTo(Environment.CurrentManagedThreadId));
        });
    }

    [Test]
    public void PatcherLogicalUnpatchGatesExistingRecordAndReenableDoesNotDuplicateIt()
    {
        var mod = new EmptyMod();
        mod.CompatSetup();
        var patcher = new JAPatcher(mod);
        var callback = GetType().GetMethod(
            nameof(NoOpCallback),
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!;
        patcher.AddPatch(
            callback,
            new JAPatchAttribute(
                typeof(PcCompatJALibLifecycleTests),
                nameof(NoOpTarget),
                PatchType.Postfix,
                needInstance: false));

        patcher.Patch();
        var first = JAPatcher.SnapshotRegisteredPatches().Single();
        var firstGeneration = first.Generation;
        Assert.That(first.Active, Is.True);

        patcher.Unpatch();
        Assert.That(first.Active, Is.False);

        patcher.Patch();
        var second = JAPatcher.SnapshotRegisteredPatches().Single();
        Assert.Multiple(() =>
        {
            Assert.That(second, Is.SameAs(first));
            Assert.That(second.Active, Is.True);
            Assert.That(second.Generation, Is.GreaterThan(firstGeneration));
        });
        mod.CompatUnload();
    }

    [Test]
    public void PatchManagerProjectsCurrentLogicalRegistryAndStaticUnpatchOnlyChangesGate()
    {
        var mod = new EmptyMod();
        var patcher = new JAPatcher(mod);
        var callback = GetType().GetMethod(
            nameof(NoOpCallback),
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!;
        var original = GetType().GetMethod(
            nameof(NoOpTarget),
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!;
        patcher.AddPatch(
            callback,
            new JAPatchAttribute(original, PatchType.Postfix, needInstance: false)
            {
                Priority = 123,
                Before = ["before.owner"],
                After = ["after.owner"]
            });
        patcher.Patch();

        var info = JAPatchManager.GetPatchInfo(original);
        var data = JAPatchManager.GetPatchData(original);
        Assert.Multiple(() =>
        {
            Assert.That(info.Postfixes, Is.Empty);
            Assert.That(info.TryPostfixes, Has.Length.EqualTo(1));
            Assert.That(info.TryPostfixes[0].PatchMethod, Is.EqualTo(callback));
            Assert.That(info.TryPostfixes[0].priority, Is.EqualTo(123));
            Assert.That(info.TryPostfixes[0].before, Is.EqualTo(new[] { "before.owner" }));
            Assert.That(data.TryPostfixes, Is.EqualTo(new[] { callback }));
            Assert.That(JAPatchManager.GetPatchInfos().Single().Original, Is.EqualTo(original));
        });

        JAPatcher.Unpatch(original, callback);
        Assert.That(JAPatcher.SnapshotRegisteredPatches().Single().Active, Is.False);
        Assert.That(JAPatchManager.GetPatchInfo(original).TryPostfixes, Is.Empty);
    }

    private static void NoOpTarget() { }
    private static void NoOpCallback() { }

    private sealed class EmptyMod : JAMod;

    private sealed class LifecycleMod(List<string> events) : JAMod
    {
        public List<int> UnityCallbackThreads { get; } = [];

        protected override void OnSetup() => Record("setup");
        protected override void OnEnable() => Record("enable");

        protected override Task OnEnableAsync()
        {
            Record("enable-async");
            return Task.CompletedTask;
        }

        protected override void OnFixedUpdate(float deltaTime) => Record("fixed");
        protected override void OnUpdate(float deltaTime) => Record("update");
        protected override void OnLateUpdate(float deltaTime) => Record("late");
        protected override void OnDisable() => Record("disable");

        protected override Task OnDisableAsync()
        {
            Record("disable-async");
            return Task.CompletedTask;
        }

        protected override void OnUnload() => Record("unload");

        private void Record(string value)
        {
            events.Add(value);
            UnityCallbackThreads.Add(Environment.CurrentManagedThreadId);
        }
    }
}

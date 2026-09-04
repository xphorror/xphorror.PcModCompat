using StArray.ModManager.Runtime;
using StArray.ModManager.RuntimeAbstractions;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class RuntimeMethodCompatibilityHookTests
{
    [Test]
    public void CompatibilityHandleRoutesOnlyItsHookThroughAbiAwareProvider()
    {
        var previous = HookHelper.Instance;
        var provider = new CompatibilityHookProbe();
        HookHelper.Instance = provider;
        try
        {
            var handle = RuntimeMethodCompatibility.CreateHandle(
                (nint)0x123400,
                RuntimeMethodCompatibilityKind.CalculateTickColorWithoutHitFloor);

            var continuation = HookHelper.Hook(handle, (nint)0x567800);
            var ordinaryContinuation = HookHelper.Hook((nint)0x123400, (nint)0x9ABC00);

            Assert.Multiple(() =>
            {
                Assert.That(continuation, Is.EqualTo((nint)0xCAFEB0));
                Assert.That(provider.CompatibleHookCount, Is.EqualTo(1));
                Assert.That(provider.LastTarget, Is.EqualTo((nint)0x123400));
                Assert.That(
                    provider.LastKind,
                    Is.EqualTo(RuntimeMethodCompatibilityKind.CalculateTickColorWithoutHitFloor));
                Assert.That(ordinaryContinuation, Is.EqualTo((nint)0xBEEF00));
                Assert.That(provider.OrdinaryHookCount, Is.EqualTo(1));
            });
        }
        finally
        {
            HookHelper.Instance = previous;
        }
    }

    [Test]
    public void CompatibilityHandleTranslatesBackToActualTargetForUnhook()
    {
        var previous = HookHelper.Instance;
        var provider = new CompatibilityHookProbe();
        HookHelper.Instance = provider;
        try
        {
            var handle = RuntimeMethodCompatibility.CreateHandle(
                (nint)0x135700,
                RuntimeMethodCompatibilityKind.CalculateTickColorWithoutHitFloor);

            Assert.That(HookHelper.Unhook(handle), Is.True);
            Assert.That(provider.LastUnhookTarget, Is.EqualTo((nint)0x135700));
        }
        finally
        {
            HookHelper.Instance = previous;
        }
    }

    [Test]
    public void PassThroughHandleUsesOrdinaryHookAgainstPhysicalTarget()
    {
        var previous = HookHelper.Instance;
        var provider = new CompatibilityHookProbe();
        HookHelper.Instance = provider;
        try
        {
            Assert.That(
                RuntimeMethodCompatibility.RegisterPassThroughHandle(
                    (nint)0x246800,
                    (nint)0x135700),
                Is.True);

            var continuation = HookHelper.Hook((nint)0x246800, (nint)0xABC000);

            Assert.Multiple(() =>
            {
                Assert.That(continuation, Is.EqualTo((nint)0xBEEF00));
                Assert.That(provider.OrdinaryHookCount, Is.EqualTo(1));
                Assert.That(provider.CompatibleHookCount, Is.Zero);
                Assert.That(provider.LastTarget, Is.EqualTo((nint)0x135700));
            });
        }
        finally
        {
            HookHelper.Instance = previous;
        }
    }

    private sealed class CompatibilityHookProbe : IHook, IRuntimeMethodCompatibilityHook
    {
        public int OrdinaryHookCount { get; private set; }
        public int CompatibleHookCount { get; private set; }
        public nint LastTarget { get; private set; }
        public nint LastUnhookTarget { get; private set; }
        public RuntimeMethodCompatibilityKind LastKind { get; private set; }

        public bool SupportsCompatibility(RuntimeMethodCompatibilityKind kind) => true;

        public nint Hook(nint target, nint detour)
        {
            OrdinaryHookCount++;
            LastTarget = target;
            return (nint)0xBEEF00;
        }

        public nint HookCompatible(
            nint target,
            nint detour,
            RuntimeMethodCompatibilityKind kind)
        {
            CompatibleHookCount++;
            LastTarget = target;
            LastKind = kind;
            return (nint)0xCAFEB0;
        }

        public bool Unhook(nint target)
        {
            LastUnhookTarget = target;
            return true;
        }

        public nint GetFunction(string library, string name) => nint.Zero;
    }
}

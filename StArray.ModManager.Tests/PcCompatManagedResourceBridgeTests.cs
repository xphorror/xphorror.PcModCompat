using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class PcCompatManagedResourceBridgeTests
{
    [TearDown]
    public void Cleanup()
    {
        PcCompatManagedResourceBridge.ClearAssetBundleProvider();
        PcCompatManagedResourceBridge.ClearCapabilityAssetProvider();
    }

    [Test]
    public void RejectsAssetBundleAccessOutsideOwnerScope()
    {
        PcCompatManagedResourceBridge.RegisterAssetBundleProvider(_ => new object(), _ => { });

        Assert.That(
            () => PcCompatManagedResourceBridge.LoadAssetBundleFromFile("bundle"),
            Throws.InvalidOperationException.With.Message.Contains("outside an owner-scoped"));
    }

    [Test]
    public void ForwardsOwnerGenerationPathAndReleaseInsideLifecycleScope()
    {
        var proxy = new object();
        PcCompatManagedAssetBundleRequest? acquired = null;
        PcCompatManagedAssetBundleRelease? released = null;
        PcCompatManagedResourceBridge.RegisterAssetBundleProvider(
            request =>
            {
                acquired = request;
                return proxy;
            },
            request => released = request);

        var state = new PcCompatManagedExecutionState(
            "test.mod",
            17,
            PcCompatManagedExecutionPhase.Enable);
        using (PcCompatManagedExecutionContext.Enter(state))
        {
            Assert.That(
                PcCompatManagedResourceBridge.LoadAssetBundleFromFile("requested.bundle"),
                Is.SameAs(proxy));
            PcCompatManagedResourceBridge.ReleaseAssetBundle(proxy, true);
        }

        Assert.That(acquired, Is.Not.Null);
        Assert.That(acquired!.ModId, Is.EqualTo("test.mod"));
        Assert.That(acquired.SessionGeneration, Is.EqualTo(17));
        Assert.That(acquired.RequestedPath, Is.EqualTo("requested.bundle"));
        Assert.That(released, Is.Not.Null);
        Assert.That(released!.Bundle, Is.SameAs(proxy));
        Assert.That(released.UnloadAllLoadedObjects, Is.True);
        Assert.That(PcCompatManagedExecutionContext.Current, Is.Null);
    }

    [Test]
    public void RestoresNestedExecutionScope()
    {
        var outer = new PcCompatManagedExecutionState(
            "outer",
            1,
            PcCompatManagedExecutionPhase.Setup);
        var inner = new PcCompatManagedExecutionState(
            "inner",
            2,
            PcCompatManagedExecutionPhase.Update);

        using (PcCompatManagedExecutionContext.Enter(outer))
        {
            using (PcCompatManagedExecutionContext.Enter(inner))
                Assert.That(PcCompatManagedExecutionContext.Current, Is.SameAs(inner));
            Assert.That(PcCompatManagedExecutionContext.Current, Is.SameAs(outer));
        }
        Assert.That(PcCompatManagedExecutionContext.Current, Is.Null);
    }

    [Test]
    public void ForwardsOwnerScopedCapabilityStableIdAndType()
    {
        var proxy = new object();
        PcCompatManagedCapabilityAssetRequest? received = null;
        PcCompatManagedResourceBridge.RegisterCapabilityAssetProvider(request =>
        {
            received = request;
            return proxy;
        });

        using (PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
                   "capability.mod",
                   23,
                   PcCompatManagedExecutionPhase.Setup)))
        {
            Assert.That(
                PcCompatManagedResourceBridge.LoadCapabilityAsset(
                    "shader.tmp.mobile.sdf",
                    "UnityEngine.Shader"),
                Is.SameAs(proxy));
        }

        Assert.That(received, Is.EqualTo(new PcCompatManagedCapabilityAssetRequest(
            "capability.mod",
            23,
            "shader.tmp.mobile.sdf",
            "UnityEngine.Shader")));
    }

    [Test]
    public void RejectsCapabilityAccessDuringDisable()
    {
        PcCompatManagedResourceBridge.RegisterCapabilityAssetProvider(_ => new object());
        using (PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
                   "capability.mod",
                   24,
                   PcCompatManagedExecutionPhase.Disable)))
        {
            Assert.That(
                () => PcCompatManagedResourceBridge.LoadCapabilityAsset(
                    "shader.tmp.mobile.sdf",
                    "UnityEngine.Shader"),
                Throws.InvalidOperationException.With.Message.Contains("during Disable"));
        }
    }
}

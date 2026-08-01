using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatProxyCastBridgeTests
{
    [Test]
    public void PreservesClrIsInstanceAndCastSemantics()
    {
        object value = "proxy";

        Assert.That(PcCompatProxyCastBridge.IsInstance<string>(value), Is.SameAs(value));
        Assert.That(PcCompatProxyCastBridge.IsInstance<string>(new object()), Is.Null);
        Assert.That(PcCompatProxyCastBridge.Cast<string>(value), Is.SameAs(value));
        Assert.That(PcCompatProxyCastBridge.Cast<string>(null), Is.Null);
        Assert.Throws<InvalidCastException>(() =>
            PcCompatProxyCastBridge.Cast<string>(new object()));
    }
}

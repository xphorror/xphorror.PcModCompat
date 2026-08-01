using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatManagedIoBridgeTests
{
    [Test]
    public void FiniteFileReadRequiresForwardProgress()
    {
        Assert.That(PcCompatManagedIoBridge.RequireFileReadProgress(7), Is.EqualTo(7));
        Assert.That(
            () => PcCompatManagedIoBridge.RequireFileReadProgress(0),
            Throws.TypeOf<EndOfStreamException>());
        Assert.That(
            () => PcCompatManagedIoBridge.RequireFileReadProgress(-1),
            Throws.TypeOf<IOException>());
    }

    [Test]
    public void ExactFileReadReturnsFalseForTruncatedInput()
    {
        var empty = new byte[8];
        Assert.That(
            PcCompatManagedIoBridge.TryReadFileExactly(new MemoryStream(), empty),
            Is.False);

        var partial = new byte[8];
        Assert.That(
            PcCompatManagedIoBridge.TryReadFileExactly(
                new MemoryStream([1, 2, 3]),
                partial),
            Is.False);
        Assert.That(partial[..3], Is.EqualTo(new byte[] { 1, 2, 3 }));

        var complete = new byte[3];
        Assert.That(
            PcCompatManagedIoBridge.TryReadFileExactly(
                new MemoryStream([4, 5, 6]),
                complete),
            Is.True);
        Assert.That(complete, Is.EqualTo(new byte[] { 4, 5, 6 }));
    }
}

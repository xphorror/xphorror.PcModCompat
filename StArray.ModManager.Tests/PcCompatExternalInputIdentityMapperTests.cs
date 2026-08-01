using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatExternalInputIdentityMapperTests
{
    [TestCase(29, 97, 0x41)]
    [TestCase(54, 122, 0x5A)]
    [TestCase(7, 48, 0x30)]
    [TestCase(62, 32, 0x20)]
    [TestCase(131, 282, 0x70)]
    [TestCase(21, 276, 0x25)]
    public void MapsKnownAndroidKeysToBothPollingDomains(
        int android,
        int unity,
        int windows)
    {
        Assert.Multiple(() =>
        {
            Assert.That(PcCompatExternalInputIdentityMapper.TryMapAndroidToUnity(
                android, out var actualUnity), Is.True);
            Assert.That(actualUnity, Is.EqualTo(unity));
            Assert.That(PcCompatExternalInputIdentityMapper.TryMapAndroidToWindowsVirtualKey(
                android, out var actualWindows), Is.True);
            Assert.That(actualWindows, Is.EqualTo(windows));
        });
    }

    [Test]
    public void UnknownKeyDoesNotGuessFromScanCode()
    {
        var inputEvent = new PcCompatKeyViewerRawEvent(
            1, 1, 1, 1, 1,
            PcCompatKeyViewerInputOrigin.OfficialActivity,
            PcCompatKeyViewerRawSource.Keyboard,
            PcCompatKeyViewerRawPhase.Down,
            999, 0, 0, 30, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0);

        Assert.That(PcCompatExternalInputIdentityMapper.Map(inputEvent), Is.Empty);
    }
}

using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatExternalInputIdentityMapperTests
{
    [TestCase("A", 97, 0x41)]
    [TestCase("_0", 48, 0x30)]
    [TestCase("F12", 293, 0x7B)]
    [TestCase("F13", 294, 0x7C)]
    [TestCase("F16", 670, 0x7F)]
    [TestCase("F24", 678, 0x87)]
    [TestCase("PrintScreen", 316, 0x2C)]
    [TestCase("Pause", 19, 0x13)]
    [TestCase("Menu", 319, 0x5D)]
    [TestCase("LeftSuper", 310, 0x5B)]
    [TestCase("RightSuper", 309, 0x5C)]
    [TestCase("KeypadEnter", 271, 0x0D)]
    [TestCase("KeyA", 97, 0x41)]
    [TestCase("Digit0", 48, 0x30)]
    [TestCase("ArrowUp", 273, 0x26)]
    [TestCase("ControlLeft", 306, 0xA2)]
    [TestCase("ShiftRight", 303, 0xA1)]
    [TestCase("MetaLeft", 310, 0x5B)]
    [TestCase("BracketRight", 93, 0xDD)]
    public void MapsReplayCanonicalKeysToBothConsumerDomains(
        string canonicalKey,
        int unityKeyCode,
        int windowsVirtualKey)
    {
        var identities = PcCompatVirtualInputIdentityMapper.Map(canonicalKey);

        Assert.Multiple(() =>
        {
            Assert.That(identities, Does.Contain(new PcCompatCanonicalInputIdentity(
                PcCompatInputIdentityKind.UnityKeyCode,
                unityKeyCode)));
            Assert.That(identities, Does.Contain(new PcCompatCanonicalInputIdentity(
                PcCompatInputIdentityKind.WindowsVirtualKey,
                windowsVirtualKey)));
        });
    }

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

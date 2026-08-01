namespace Xphorror.PcModCompat;

public static class PcCompatNativeBridge
{
    public static void PublishGameSnapshot(
        int[]? hitMarginsCount,
        double planetSpeed,
        float percentAcc,
        float percentXAcc,
        int playerCount,
        string? sceneName)
    {
        PcCompatReversePatchBridge.PublishSnapshot(new PcCompatGameSnapshot
        {
            HitMarginsCount = hitMarginsCount == null ? Array.Empty<int>() : (int[])hitMarginsCount.Clone(),
            PlanetSpeed = planetSpeed,
            PercentAcc = percentAcc,
            PercentXAcc = percentXAcc,
            PlayerCount = Math.Max(1, playerCount),
            SceneName = sceneName ?? string.Empty
        });
    }

    public static bool UpdatePatchStatus(
        string modId,
        string callbackType,
        string callbackMethod,
        int status,
        string? reason)
    {
        var parsedStatus = status switch
        {
            1 => PcCompatPatchStatus.Supported,
            2 => PcCompatPatchStatus.Unsupported,
            _ => PcCompatPatchStatus.RegisteredOnly
        };

        return PcCompatRuntime.PatchRegistry.UpdateStatus(
            modId,
            callbackType,
            callbackMethod,
            parsedStatus,
            reason ?? string.Empty);
    }

    public static string? ConsumeRequestedSceneName()
        => PcCompatReversePatchBridge.ConsumeRequestedSceneName();
}

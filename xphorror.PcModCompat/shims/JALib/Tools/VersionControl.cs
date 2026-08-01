using UnityEngine;

namespace JALib.Tools;

public static class VersionControl
{
    public static int releaseNumber = JALib.VersionControl.releaseNumber;
    public static Version version;

    static VersionControl()
    {
        version = Version.TryParse(Application.version, out var parsed)
            ? parsed
            : new Version(0, 0);
    }
}

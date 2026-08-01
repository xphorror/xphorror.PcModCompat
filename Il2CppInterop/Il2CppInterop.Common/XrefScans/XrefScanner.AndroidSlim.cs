#if IL2CPPINTEROP_ANDROID_SLIM
#nullable enable
using System.Reflection;

namespace Il2CppInterop.Common.XrefScans;

public static class XrefScanner
{
    public static IEnumerable<XrefInstance> XrefScan(MethodBase methodBase)
        => throw Disabled(methodBase?.Name);

    public static IEnumerable<XrefInstance> UsedBy(MethodBase methodBase)
        => throw Disabled(methodBase?.Name);

    private static NotSupportedException Disabled(string? member)
        => new($"Xref scanner is unavailable in the Android slim runtime (member={member ?? "unknown"}).");
}

public static class XrefScannerLowLevel
{
    public static IEnumerable<IntPtr> JumpTargets(IntPtr codeStart, bool ignoreRetn = false)
    {
        _ = codeStart;
        _ = ignoreRetn;
        throw Disabled();
    }

    public static IEnumerable<IntPtr> CallAndIndirectTargets(IntPtr pointer)
    {
        _ = pointer;
        throw Disabled();
    }

    private static NotSupportedException Disabled()
        => new("Low-level xref scanning is unavailable in the Android slim runtime.");
}

internal static class XrefScanUtilFinder
{
    public static IntPtr FindLastRcxReadAddressBeforeCallTo(IntPtr codeStart, IntPtr callTarget)
    {
        _ = codeStart;
        _ = callTarget;
        throw Disabled();
    }

    public static IntPtr FindByteWriteTargetRightAfterCallTo(IntPtr codeStart, IntPtr callTarget)
    {
        _ = codeStart;
        _ = callTarget;
        throw Disabled();
    }

    private static NotSupportedException Disabled()
        => new("Xref metadata probing is unavailable in the Android slim runtime.");
}
#endif

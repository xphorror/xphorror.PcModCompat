using System.Runtime.InteropServices;

namespace Xphorror.PcModCompat;

/// <summary>
/// Android lifecycle-backed replacement for desktop <c>Application.isFocused</c> polling.
/// Before the activity publishes an authoritative state, compatibility defaults to focused so
/// startup races cannot permanently disable a desktop MOD update loop.
/// </summary>
public static class PcCompatManagedApplicationBridge
{
    private const string NativeLibrary = "starray_modmanager";

    public static bool GetIsFocused()
    {
        if (!OperatingSystem.IsAndroid())
            return true;
        try
        {
            return ReadApplicationFocusState() != 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or
                                           EntryPointNotFoundException or
                                           BadImageFormatException)
        {
            return true;
        }
    }

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_application_is_focused")]
    private static extern int ReadApplicationFocusState();
}

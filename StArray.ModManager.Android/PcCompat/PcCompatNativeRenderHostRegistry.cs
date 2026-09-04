using System.Runtime.InteropServices;
using StArray.ModManager.Manager;

namespace StArray.ModManager.Android.PcCompat;

/// <summary>
/// Publishes the set of MOD-owned host component instance pointers that the native render-callback
/// hook dispatches for.
/// </summary>
/// <remarks>
/// <para>
/// The filter lives in native rather than managed on purpose. The hook target
/// (<c>UnityEngine.UI.RawImage::OnPopulateMesh</c>) is also used by the game itself - nine
/// <c>RawImage</c> fields in the r143 metadata, among them the pause blur screenshot and the waveform,
/// which rebuild their mesh continuously. Deciding ownership on the managed side would be simpler, but
/// it would put a native-to-managed transition and an invocation-struct allocation on the game's own
/// render path. Deciding it in native costs one lookup in a sorted vector.
/// </para>
/// <para>
/// Registration failures are logged and swallowed rather than thrown. The caller is the component
/// bridge's registration path, which has already committed the managed tables; a failure here means
/// the callback does not fire, which the MOD sees as rain that does not render - not a crash, and not
/// a state the bridge could roll back to anything better.
/// </para>
/// </remarks>
internal static class PcCompatNativeRenderHostRegistry
{
    private const string NativeLibrary = "libstarray_modmanager";
    private const string LogTag = "PcCompatRenderHost";

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_register_managed_render_host")]
    private static extern int RegisterNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modId,
        ulong instancePointer);

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_unregister_managed_render_host")]
    private static extern int UnregisterNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modId,
        ulong instancePointer);

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_clear_managed_render_hosts")]
    private static extern int ClearNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modId);

    [DllImport(NativeLibrary, EntryPoint = "modmanager_pccompat_managed_render_host_count")]
    private static extern int CountNative();

    public static void Register(string modId, long instancePointer)
    {
        var result = RegisterNative(modId, unchecked((ulong)instancePointer));
        if (result < 0)
        {
            Logger.Error(
                LogTag,
                $"register rejected mod={modId} instance=0x{instancePointer:X} result={result}");
        }
    }

    public static void Unregister(string modId, long instancePointer)
    {
        var result = UnregisterNative(modId, unchecked((ulong)instancePointer));
        if (result < 0)
        {
            Logger.Error(
                LogTag,
                $"unregister rejected mod={modId} instance=0x{instancePointer:X} result={result}");
        }
    }

    public static void Clear(string modId)
    {
        var result = ClearNative(modId);
        if (result < 0)
            Logger.Error(LogTag, $"clear rejected mod={modId} result={result}");
    }

    internal static int Count() => CountNative();
}

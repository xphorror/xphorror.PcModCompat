using System.Runtime.InteropServices;

namespace StArray.ModManager.Android.Native;

internal static class Il2CppNativeBridge
{
    [DllImport("starray_modmanager", EntryPoint = "modmanager_libil2cpp_handle")]
    private static extern nint GetHandleNative();

    internal static nint GetHandle()
    {
        try
        {
            return GetHandleNative();
        }
        catch
        {
            return nint.Zero;
        }
    }
}

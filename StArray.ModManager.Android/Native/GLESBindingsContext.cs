using OpenTK;

using System.Runtime.InteropServices;

namespace StArray.ModManager.Android.Native;

public class GLESBindingsContext : IBindingsContext
{
    [DllImport("starray_modmanager", EntryPoint = "modmanager_gl_get_proc_address",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ResolveGlProcAddress(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string procName);

    public IntPtr GetProcAddress(string procName)
    {
        return ResolveGlProcAddress(procName);
    }
}

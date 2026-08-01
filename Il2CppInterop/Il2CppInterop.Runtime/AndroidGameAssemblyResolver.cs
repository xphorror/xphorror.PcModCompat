using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Il2CppInterop.Runtime;

public static class AndroidGameAssemblyResolver
{
    private static readonly object InstallLock = new();
    private static bool s_installed;
    private static Exception? s_failure;
    private static nint s_handle;

    [DllImport("starray_modmanager", EntryPoint = "modmanager_libil2cpp_handle")]
    private static extern nint GetIl2CppHandleNative();

    [ModuleInitializer]
    internal static void InitializeModule()
    {
        if (!OperatingSystem.IsAndroid())
            return;
        TryInstall();
    }

    public static void EnsureInstalled()
    {
        if (!OperatingSystem.IsAndroid())
            return;
        if (!TryInstall())
        {
            throw new InvalidOperationException(
                "Il2CppInterop Android native resolver registration failed.",
                s_failure);
        }
    }

    public static bool WaitForHandle(TimeSpan timeout)
    {
        if (!OperatingSystem.IsAndroid())
            return true;
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var timeoutMilliseconds = (long)Math.Min(timeout.TotalMilliseconds, int.MaxValue);
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (true)
        {
            if (ResolveHandle() != IntPtr.Zero)
                return true;
            if (Environment.TickCount64 >= deadline)
                return false;
            Thread.Sleep(10);
        }
    }

    private static bool TryInstall()
    {
        lock (InstallLock)
        {
            if (s_installed)
                return true;
            if (s_failure != null)
                return false;
            try
            {
                NativeLibrary.SetDllImportResolver(
                    typeof(AndroidGameAssemblyResolver).Assembly,
                    ResolveNativeLibrary);
                s_installed = true;
                return true;
            }
            catch (Exception exception)
            {
                s_failure = exception;
                return false;
            }
        }
    }

    private static nint ResolveNativeLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        _ = assembly;
        _ = searchPath;
        if (!libraryName.Equals("GameAssembly", StringComparison.OrdinalIgnoreCase) &&
            !libraryName.Equals("libil2cpp.so", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        return ResolveHandle();
    }

    private static nint ResolveHandle()
    {
        var cached = Volatile.Read(ref s_handle);
        if (cached != IntPtr.Zero)
            return cached;

        nint handle;
        try
        {
            handle = GetIl2CppHandleNative();
        }
        catch
        {
            return IntPtr.Zero;
        }
        if (handle == IntPtr.Zero)
            return IntPtr.Zero;

        Interlocked.CompareExchange(ref s_handle, handle, IntPtr.Zero);
        return Volatile.Read(ref s_handle);
    }
}

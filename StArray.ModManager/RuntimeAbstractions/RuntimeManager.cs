using System.Runtime.InteropServices;
using StArray.ModManager.Il2Cpp;

namespace StArray.ModManager.RuntimeAbstractions;

/// <summary>选择并公开当前 Unity native managed runtime。</summary>
public static class RuntimeManager
{
    public static RuntimeBackend Backend { get; private set; } = RuntimeBackend.None;
    public static bool IsAvailable => Backend != RuntimeBackend.None;
    public static bool IsMono => Backend == RuntimeBackend.Mono;
    public static bool IsIl2Cpp => Backend == RuntimeBackend.Il2Cpp;

    public static RuntimeBackend Detect()
    {
        Backend = RuntimeBackend.None;
        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "..", "..", "GameAssembly.dll")))
                Backend = RuntimeBackend.Il2Cpp;
            else if (File.Exists(Path.Combine(
                         AppContext.BaseDirectory,
                         "..",
                         "..",
                         "MonoBleedingEdge",
                         "EmbedRuntime",
                         "mono-2.0-bdwgc.dll")))
                Backend = RuntimeBackend.Mono;
        }
        else if (OperatingSystem.IsAndroid())
        {
            if (IsUnixLibraryLoaded("libil2cpp.so"))
                Backend = RuntimeBackend.Il2Cpp;
            else if (IsUnixLibraryLoaded("libmono.so") ||
                     IsUnixLibraryLoaded("libmonobdwgc-2.0.so"))
                Backend = RuntimeBackend.Mono;
        }
        else if (OperatingSystem.IsLinux())
        {
            if (IsUnixLibraryLoaded("libil2cpp.so"))
                Backend = RuntimeBackend.Il2Cpp;
            else if (IsUnixLibraryLoaded("libmono-2.0.so.1") ||
                     IsUnixLibraryLoaded("libmono.so"))
                Backend = RuntimeBackend.Mono;
        }

        return Backend;
    }

    public static void SetBackend(RuntimeBackend backend) => Backend = backend;

    public static IAppDomain? GetDomain()
    {
        if (Backend == RuntimeBackend.None)
            Detect();
        return IsIl2Cpp ? Il2CppDomain.Current : null;
    }

    [DllImport("libdl", EntryPoint = "dlopen")]
    private static extern nint Dlopen(string filename, int flags);

    [DllImport("libdl", EntryPoint = "dlclose")]
    private static extern int Dlclose(nint handle);

    internal const int RtldNow = 0x0002;
    internal const int RtldNoLoad = 0x0004;

    private static bool IsUnixLibraryLoaded(string filename)
        => ProbeUnixLibrary(filename, Dlopen, handle => _ = Dlclose(handle));

    internal static bool ProbeUnixLibrary(
        string filename,
        Func<string, int, nint> open,
        Action<nint> close)
    {
        var handle = open(filename, RtldNow | RtldNoLoad);
        if (handle == 0)
            return false;

        try
        {
            return true;
        }
        finally
        {
            close(handle);
        }
    }
}

public enum RuntimeBackend
{
    None,
    Il2Cpp,
    Mono,
}

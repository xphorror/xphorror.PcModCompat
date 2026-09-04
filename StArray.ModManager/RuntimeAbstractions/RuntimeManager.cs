using System.Runtime.InteropServices;
using StArray.ModManager.Il2Cpp;
using StArray.ModManager.Mono;
using StArray.ModManager.Runtime;

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
            if (File.Exists(Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "MonoBleedingEdge",
                    "EmbedRuntime",
                    "mono-2.0-bdwgc.dll")))
                Backend = RuntimeBackend.Mono;
            else if (File.Exists(Path.Combine(AppContext.BaseDirectory, "..", "..", "GameAssembly.dll")))
                Backend = RuntimeBackend.Il2Cpp;
        }
        else if (OperatingSystem.IsAndroid())
        {
            if (IsUnixLibraryLoaded("libmono.so") ||
                IsUnixLibraryLoaded("libmonobdwgc-2.0.so"))
                Backend = RuntimeBackend.Mono;
            else if (IsUnixLibraryLoaded("libil2cpp.so"))
                Backend = RuntimeBackend.Il2Cpp;
        }
        else if (OperatingSystem.IsLinux())
        {
            if (IsUnixLibraryLoaded("libmono-2.0.so.1") ||
                IsUnixLibraryLoaded("libmono.so"))
                Backend = RuntimeBackend.Mono;
            else if (IsUnixLibraryLoaded("libil2cpp.so"))
                Backend = RuntimeBackend.Il2Cpp;
        }

        return Backend;
    }

    /// <summary>显式选择运行时后端，供平台启动器和兼容调用方使用。</summary>
    public static void SetBackend(RuntimeBackend backend)
    {
        var runtimeKey = HookHelper.CurrentRuntimeKey;
        if (runtimeKey.IsValid && Backend != backend)
        {
            throw new InvalidOperationException(
                "A MOD generation cannot replace the process-wide runtime backend.");
        }
        Backend = backend;
    }

    public static IAppDomain? GetDomain()
    {
        if (Backend == RuntimeBackend.None)
            Detect();
        return Backend switch
        {
            RuntimeBackend.Il2Cpp => Il2CppDomain.Current,
            RuntimeBackend.Mono => MonoDomain.Current,
            _ => null
        };
    }

    /// <summary>从运行时对象取得其实际类型。</summary>
    /// <remarks>
    /// 泛型容器通常需要先取得对象的具体方法表。调用方可缓存返回类型上的方法，
    /// 避免在输入等高频路径中反复按名称解析。
    /// </remarks>
    public static IRuntimeClass? GetObjectClass(nint objectPtr)
    {
        if (objectPtr == 0)
            return null;

        if (IsIl2Cpp)
        {
            var klass = Il2CppFunctions.il2cpp_object_get_class(objectPtr);
            return klass == 0 ? null : new Il2CppClass(klass);
        }

        if (IsMono)
        {
            var klass = MonoFunctions.MonoObjectGetClass(objectPtr);
            return klass == 0 ? null : new MonoClass(klass);
        }

        return null;
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

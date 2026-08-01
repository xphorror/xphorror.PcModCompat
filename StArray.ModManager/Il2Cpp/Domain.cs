using System.Runtime.InteropServices;
using StArray.ModManager.RuntimeAbstractions;

namespace StArray.ModManager.Il2Cpp;

public unsafe class Il2CppDomain : IAppDomain
{
    private static readonly object CurrentLock = new();
    private static Il2CppDomain? _current;

    [ThreadStatic] private static nint _attachedThread;
    [ThreadStatic] private static int _attachmentDepth;
    [ThreadStatic] private static bool _ownsAttachment;
    [ThreadStatic] private static IIl2CppRuntimeApi? _attachmentApi;

    public nint Ptr { get; }
    public bool IsValid => Ptr != 0;

    public Il2CppDomain(nint ptr) => Ptr = ptr;

    public static event Action<string, nint>? AssemblyLoad;

    public static Il2CppDomain? Current
    {
        get
        {
            var current = Volatile.Read(ref _current);
            if (current != null) return current;

            lock (CurrentLock)
            {
                if (_current != null) return _current;
                var ptr = Il2CppRuntimeApi.Current.DomainGet();
                if (ptr == 0) return null;
                _current = new Il2CppDomain(ptr);
                return _current;
            }
        }
    }

    internal static void ResetCachedState()
    {
        lock (CurrentLock)
            _current = null;
        _attachedThread = 0;
        _attachmentDepth = 0;
        _ownsAttachment = false;
        _attachmentApi = null;
    }

    public IReadOnlyList<IRuntimeAssembly> GetAssemblies()
    {
        var list = new List<IRuntimeAssembly>();
        uint size = 0;
        var ptr = Il2CppFunctions.il2cpp_domain_get_assemblies(Ptr, ref size);
        if (ptr == null) return list;
        for (uint i = 0; i < size; i++)
            list.Add(new Il2CppAssembly(ptr[i]));
        return list;
    }

    public IReadOnlyList<Il2CppAssembly> GetIl2CppAssemblies()
    {
        var list = new List<Il2CppAssembly>();
        uint size = 0;
        var ptr = Il2CppFunctions.il2cpp_domain_get_assemblies(Ptr, ref size);
        if (ptr == null) return list;
        for (uint i = 0; i < size; i++)
            list.Add(new Il2CppAssembly(ptr[i]));
        return list;
    }

    public IRuntimeAssembly? OpenAssembly(string name)
    {
        var asm = Il2CppRuntimeApi.Current.DomainAssemblyOpen(Ptr, name);
        if (asm == 0)
            return null;
        AssemblyLoad?.Invoke(name, asm);
        return new Il2CppAssembly(asm);
    }

    public Il2CppAssembly? OpenIl2CppAssembly(string name) => OpenAssembly(name) as Il2CppAssembly;

    public IRuntimeAssembly? WaitForAssembly(string name, int timeoutMs = 5000)
    {
        var assembly = OpenAssembly(name);
        if (assembly != null)
            return assembly;

        const int pollMilliseconds = 100;
        var attempts = Math.Max(0, timeoutMs) / pollMilliseconds;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            Thread.Sleep(pollMilliseconds);
            assembly = OpenAssembly(name);
            if (assembly != null)
                return assembly;
        }
        return null;
    }

    public nint NewString(string str) => Il2CppFunctions.il2cpp_string_new(str);

    public nint NewArray(nint elementClass, int length)
        => length >= 0 ? Il2CppFunctions.il2cpp_array_new(elementClass, (ulong)length) : 0;

    public void ThreadAttach()
    {
        if (_attachmentDepth > 0)
        {
            _attachmentDepth++;
            return;
        }

        var api = Il2CppRuntimeApi.Current;
        var currentThread = api.ThreadCurrent();
        if (currentThread != 0)
        {
            _attachedThread = currentThread;
            _attachmentDepth = 1;
            _ownsAttachment = false;
            _attachmentApi = api;
            return;
        }

        var attachedThread = api.ThreadAttach(Ptr);
        if (attachedThread == 0)
            throw new InvalidOperationException("IL2CPP rejected thread attachment.");

        _attachedThread = attachedThread;
        _attachmentDepth = 1;
        _ownsAttachment = true;
        _attachmentApi = api;
    }

    public void ThreadDetach()
    {
        if (_attachmentDepth == 0) return;
        if (--_attachmentDepth > 0) return;

        var thread = _attachedThread;
        var ownsAttachment = _ownsAttachment;
        var api = _attachmentApi;
        _attachedThread = 0;
        _ownsAttachment = false;
        _attachmentApi = null;

        if (ownsAttachment && thread != 0 && api!.CanDetachOwnedThread)
            api.ThreadDetach(thread);
    }
}

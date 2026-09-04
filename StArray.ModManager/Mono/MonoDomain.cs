using StArray.ModManager.Manager;
using StArray.ModManager.RuntimeAbstractions;

namespace StArray.ModManager.Mono;

public unsafe class MonoDomain : IAppDomain
{
    private static readonly object CurrentLock = new();
    private static MonoDomain? _current;

    [ThreadStatic] private static _MonoThread* _attachedThread;
    [ThreadStatic] private static int _attachmentDepth;
    [ThreadStatic] private static bool _ownsAttachment;

    public nint Ptr { get; }
    public bool IsValid => Ptr != 0;

    public MonoDomain(nint ptr) => Ptr = ptr;

    /// <summary>Assembly loaded callback. Args: assembly name (e.g. "Assembly-CSharp.dll"), assembly ptr.</summary>
    public static event Action<string, nint>? AssemblyLoad;

    internal static void OnAssemblyLoad(string name, nint asm) => AssemblyLoad?.Invoke(name, asm);

    static MonoDomain()
    {
        MonoFunctions.InstallAssemblyLoadHook();
        AssemblyLoad += (name, asm) =>
        {
            Logger.Debug("Mono", $"Assembly loaded: {name}");
        };
    }

    public static MonoDomain? Current
    {
        get
        {
            var current = Volatile.Read(ref _current);
            if (current != null) return current;

            var ptr = MonoFunctions.MonoGetRootDomain();
            if (ptr == 0) return null;

            lock (CurrentLock)
            {
                return _current ??= new MonoDomain(ptr);
            }
        }
    }

    public IRuntimeAssembly? OpenAssembly(string name)
    {
        // 1. 查找已加载的程序集
        var searchName = Path.GetFileNameWithoutExtension(name);
        var existing = GetAssemblies().FirstOrDefault(a =>
            string.Equals(Path.GetFileNameWithoutExtension(a.Name), searchName, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        // 2. 从本地文件直接加载
        var status = MonoImageOpenStatus.MONO_IMAGE_OK;
        var asm = MonoFunctions.MonoAssemblyOpen(name, out status);
        if (asm != 0 && status == MonoImageOpenStatus.MONO_IMAGE_OK)
        {
            OnAssemblyLoad(name, asm);
            return new MonoAssembly(asm);
        }

        // 3. 尝试按名称加载（不带 .dll）
        var loaded = MonoFunctions.MonoAssemblyLoadWithPartialName(searchName, out status);
        if (loaded != 0 && status == MonoImageOpenStatus.MONO_IMAGE_OK)
        {
            OnAssemblyLoad(name, loaded);
            return new MonoAssembly(loaded);
        }

        return null;
    }

    public IReadOnlyList<IRuntimeAssembly> GetAssemblies()
    {
        var list = new List<IRuntimeAssembly>();
        MonoFunctions.MonoAssemblyForeach(asm =>
        {
            list.Add(new MonoAssembly(asm));
        });
        return list;
    }

    public nint NewString(string str) => MonoFunctions.MonoStringNew(Ptr, str);

    public nint NewArray(nint elementClass, int length) => MonoFunctions.MonoArrayNew(Ptr, elementClass, (nuint)length);

    public void ThreadAttach()
    {
        if (_attachmentDepth > 0)
        {
            _attachmentDepth++;
            return;
        }

        var current = Methods.mono_thread_current();
        if (current != null)
        {
            _attachedThread = current;
            _attachmentDepth = 1;
            _ownsAttachment = false;
            return;
        }

        var attached = Methods.mono_thread_attach((_MonoDomain*)Ptr);
        if (attached == null)
            throw new InvalidOperationException("Mono rejected thread attachment.");

        _attachedThread = attached;
        _attachmentDepth = 1;
        _ownsAttachment = true;
    }

    public void ThreadDetach()
    {
        if (_attachmentDepth == 0) return;
        if (--_attachmentDepth > 0) return;

        var thread = _attachedThread;
        var ownsAttachment = _ownsAttachment;
        _attachedThread = null;
        _ownsAttachment = false;
        if (ownsAttachment && thread != null)
            Methods.mono_thread_detach(thread);
    }
}

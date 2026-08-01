using StArray.ModManager.Il2Cpp;

namespace StArray.ModManager.RuntimeAbstractions;

/// <summary>统一 IL2CPP managed object-reference array。</summary>
public readonly unsafe struct RuntimeArray
{
    public nint Ptr { get; }
    public bool IsValid => Ptr != 0;

    public RuntimeArray(nint ptr) => Ptr = ptr;
    public RuntimeArray(RuntimeObject obj) => Ptr = obj.Ptr;

    public int Length => RuntimeManager.IsIl2Cpp && Ptr != 0
        ? (int)Il2CppFunctions.il2cpp_array_length(Ptr)
        : 0;

    public nint DataPtr => RuntimeManager.IsIl2Cpp
        ? Il2CppRuntimeApi.GetArrayDataPointer(Ptr)
        : 0;

    internal static nint GetIl2CppDataPtr(nint array)
        => Il2CppRuntimeApi.GetArrayDataPointer(array);

    public nint this[int index]
    {
        get
        {
            var data = DataPtr;
            return index >= 0 && index < Length && data != 0
                ? *(nint*)(data + index * nint.Size)
                : 0;
        }
    }

    public RuntimeObject? GetObject(int index)
    {
        var ptr = this[index];
        return ptr != 0 ? new RuntimeObject(ptr) : null;
    }

    public RuntimeObject?[] ToObjectArray()
    {
        var result = new RuntimeObject?[Length];
        for (var index = 0; index < result.Length; index++)
            result[index] = GetObject(index);
        return result;
    }

    public static RuntimeArray New(nint elementClass, int length)
    {
        var domain = RuntimeManager.GetDomain();
        return domain != null ? New(domain, elementClass, length) : default;
    }

    public static RuntimeArray New(IAppDomain domain, nint elementClass, int length)
    {
        var ptr = domain.NewArray(elementClass, length);
        return ptr != 0 ? new RuntimeArray(ptr) : default;
    }

    public static implicit operator RuntimeArray(RuntimeObject obj) => new(obj.Ptr);
    public static implicit operator RuntimeObject(RuntimeArray array) => new(array.Ptr);
}

/// <summary>统一 IL2CPP managed value array。</summary>
public readonly unsafe struct RuntimeArray<T> where T : unmanaged
{
    public nint Ptr { get; }
    public bool IsValid => Ptr != 0;

    public RuntimeArray(nint ptr) => Ptr = ptr;
    public RuntimeArray(RuntimeObject obj) => Ptr = obj.Ptr;
    public RuntimeArray(RuntimeArray array) => Ptr = array.Ptr;

    public int Length => RuntimeManager.IsIl2Cpp && Ptr != 0
        ? (int)Il2CppFunctions.il2cpp_array_length(Ptr)
        : 0;

    private nint DataPtr => RuntimeManager.IsIl2Cpp
        ? RuntimeArray.GetIl2CppDataPtr(Ptr)
        : 0;

    public T this[int index]
    {
        get
        {
            var data = DataPtr;
            return index >= 0 && index < Length && data != 0
                ? *(T*)(data + index * sizeof(T))
                : default;
        }
    }

    public T[] ToArray()
    {
        var result = new T[Length];
        for (var index = 0; index < result.Length; index++)
            result[index] = this[index];
        return result;
    }

    public List<T> ToList() => [.. ToArray()];

    public static implicit operator RuntimeArray<T>(RuntimeObject obj) => new(obj.Ptr);
    public static implicit operator RuntimeObject(RuntimeArray<T> array) => new(array.Ptr);
}

using StArray.ModManager.Il2Cpp;
using StArray.ModManager.Mono;

namespace StArray.ModManager.RuntimeAbstractions;

/// <summary>统一 managed object-reference array。</summary>
public readonly unsafe struct RuntimeArray
{
    public nint Ptr { get; }
    public bool IsValid => Ptr != 0;

    public RuntimeArray(nint ptr) => Ptr = ptr;
    public RuntimeArray(RuntimeObject obj) => Ptr = obj.Ptr;

    public int Length
    {
        get
        {
            if (Ptr == 0) return 0;
            if (RuntimeManager.IsIl2Cpp)
            {
                if (OperatingSystem.IsAndroid())
                    return Il2CppArrayReader.TryReadLength(Ptr, out var length) ? length : 0;
                return (int)Il2CppFunctions.il2cpp_array_length(Ptr);
            }
            if (RuntimeManager.IsMono)
                return (int)MonoFunctions.MonoArrayLength(Ptr);
            return 0;
        }
    }

    public nint DataPtr
    {
        get
        {
            if (Ptr == 0) return 0;
            if (RuntimeManager.IsIl2Cpp)
            {
                if (OperatingSystem.IsAndroid())
                    return Il2CppArrayReader.TryGetDataAddress(Ptr, out var data) ? data : 0;
                return Il2CppRuntimeApi.GetArrayDataPointer(Ptr);
            }
            if (RuntimeManager.IsMono && Length > 0)
                return MonoFunctions.MonoArrayAddrWithSize(Ptr, nint.Size, 0);
            return 0;
        }
    }

    internal static nint GetIl2CppDataPtr(nint array)
        => Il2CppRuntimeApi.GetArrayDataPointer(array);

    public nint this[int index]
    {
        get
        {
            if (RuntimeManager.IsIl2Cpp && OperatingSystem.IsAndroid())
                return Il2CppArrayReader.TryReadPointerElement(Ptr, index, out var value)
                    ? value
                    : 0;
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

/// <summary>统一 managed value array。</summary>
public readonly unsafe struct RuntimeArray<T> where T : unmanaged
{
    public nint Ptr { get; }
    public bool IsValid => Ptr != 0;

    public RuntimeArray(nint ptr) => Ptr = ptr;
    public RuntimeArray(RuntimeObject obj) => Ptr = obj.Ptr;
    public RuntimeArray(RuntimeArray array) => Ptr = array.Ptr;

    public int Length
    {
        get
        {
            if (Ptr == 0) return 0;
            if (RuntimeManager.IsIl2Cpp)
            {
                if (OperatingSystem.IsAndroid())
                    return Il2CppArrayReader.TryReadLength(Ptr, out var length) ? length : 0;
                return (int)Il2CppFunctions.il2cpp_array_length(Ptr);
            }
            if (RuntimeManager.IsMono)
                return (int)MonoFunctions.MonoArrayLength(Ptr);
            return 0;
        }
    }

    private nint DataPtr => RuntimeManager.IsIl2Cpp
        ? RuntimeArray.GetIl2CppDataPtr(Ptr)
        : 0;

    public T this[int index]
    {
        get
        {
            if (RuntimeManager.IsMono)
            {
                return index >= 0 && index < Length
                    ? *(T*)MonoFunctions.MonoArrayAddrWithSize(Ptr, sizeof(T), (nuint)index)
                    : default;
            }
            if (RuntimeManager.IsIl2Cpp && OperatingSystem.IsAndroid())
                return Il2CppArrayReader.TryReadValueElement<T>(Ptr, index, out var value)
                    ? value
                    : default;
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

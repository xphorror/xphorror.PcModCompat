using System.Runtime.InteropServices;

namespace StArray.ModManager.Il2Cpp;

/// <summary>System.Object — 托管对象基类</summary>
public unsafe class Il2CppObject
{
    /// <summary>il2cpp 对象指针</summary>
    public nint Ptr { get; }

    public Il2CppObject(nint ptr) => Ptr = ptr;
    public bool IsValid => Ptr != IntPtr.Zero;

    public nint Unbox() => Il2CppFunctions.il2cpp_object_unbox(Ptr);
    public static nint Box(nint klass, nint valuePtr) => Il2CppFunctions.il2cpp_value_box(klass, valuePtr);

    public Il2CppString? ToIl2CppString()
    {
        var method = Il2CppFunctions.il2cpp_object_get_virtual_method(Ptr,
            GetObjectVirtual("ToString"));
        return method != 0 ? GetInvoked<Il2CppString>(method) : null;
    }

    public int GetHashCodeIl()
    {
        var method = Il2CppFunctions.il2cpp_object_get_virtual_method(Ptr,
            GetObjectVirtual("GetHashCode"));
        if (method == 0) return 0;
        var boxed = Il2CppRuntimeApi.Invoke(method, Ptr, null, "IL2CPP Object.GetHashCode invocation failed");
        if (boxed == 0) return 0;
        var value = Il2CppRuntimeApi.Current.ObjectUnbox(boxed);
        return value == 0 ? 0 : *(int*)value;
    }

    private static nint GetObjectVirtual(string name)
    {
        var k = Il2CppFunctions.il2cpp_class_from_name(
            Il2CppFunctions.il2cpp_get_corlib(), "System", "Object");
        return Il2CppFunctions.il2cpp_class_get_method_from_name(k, name, 0);
    }

    protected T? GetInvoked<T>(nint method) where T : Il2CppObject
    {
        var r = Il2CppRuntimeApi.Invoke(
            method,
            Ptr,
            null,
            $"IL2CPP method 0x{method:X} invocation failed");
        return r != 0 ? (T)Activator.CreateInstance(typeof(T), r)! : null;
    }
}

/// <summary>System.String — 托管字符串</summary>
public unsafe class Il2CppString : Il2CppObject
{
    public Il2CppString(nint ptr) : base(ptr) { }

    public int Length => OperatingSystem.IsAndroid()
        ? Il2CppStringReader.TryReadLength(Ptr, out var length) ? length : 0
        : Il2CppFunctions.il2cpp_string_length(Ptr);
    public char* Chars => OperatingSystem.IsAndroid()
        ? Il2CppStringReader.TryGetCharsAddress(Ptr, out var chars) ? (char*)chars : null
        : Il2CppFunctions.il2cpp_string_chars(Ptr);

    public override string ToString()
    {
        if (OperatingSystem.IsAndroid())
            return Il2CppStringReader.TryRead(Ptr, out var value) ? value : string.Empty;
        return Marshal.PtrToStringUni((nint)Chars, Length) ?? "";
    }

    public static Il2CppString New(string str) =>
        new(Il2CppFunctions.il2cpp_string_new(str));

    public static implicit operator string(Il2CppString s) => s.ToString();
}

/// <summary>System.Array — 托管数组</summary>
public unsafe class Il2CppArray<T> : Il2CppObject where T : Il2CppObject
{
    public Il2CppArray(nint ptr) : base(ptr) { }

    public uint Length => OperatingSystem.IsAndroid()
        ? Il2CppArrayReader.TryReadLength(Ptr, out var length) ? (uint)length : 0
        : Il2CppFunctions.il2cpp_array_length(Ptr);

    public T? this[int index]
    {
        get
        {
            var length = Length;
            if (index < 0 || index >= length) return null;
            if (OperatingSystem.IsAndroid())
            {
                return Il2CppArrayReader.TryReadPointerElement(Ptr, index, out var value) &&
                       value != nint.Zero
                    ? (T)Activator.CreateInstance(typeof(T), value)!
                    : null;
            }
            var data = Il2CppRuntimeApi.GetArrayDataPointer(Ptr);
            if (data == 0) return null;
            var elemPtr = data + index * nint.Size;
            var objPtr = *(nint*)elemPtr;
            return objPtr != 0 ? (T)Activator.CreateInstance(typeof(T), objPtr)! : null;
        }
    }

    public T[] ToArray()
    {
        var length = Length;
        var arr = new T[length];
        for (var i = 0; i < length; i++) arr[i] = this[i]!;
        return arr;
    }

    public List<T> ToList()
    {
        var length = Length;
        var list = new List<T>((int)length);
        for (var i = 0; i < length; i++) list.Add(this[i]!);
        return list;
    }
}

/// <summary>System.Collections.Generic.List`1</summary>
public unsafe class Il2CppList<T> : Il2CppObject where T : Il2CppObject
{
    public Il2CppList(nint ptr) : base(ptr) { }
}

/// <summary>System.Collections.Generic.Dictionary`2</summary>
public unsafe class Il2CppDictionary<TKey, TValue> : Il2CppObject
    where TKey : Il2CppObject where TValue : Il2CppObject
{
    public Il2CppDictionary(nint ptr) : base(ptr) { }
}

// ===== Enums =====

public enum BindingFlags : uint
{
    Default = 0, IgnoreCase = 1, DeclaredOnly = 2,
    Instance = 4, Static = 8, Public = 16, NonPublic = 32,
    FlattenHierarchy = 64, InvokeMethod = 256, CreateInstance = 512,
    GetField = 1024, SetField = 2048, GetProperty = 4096, SetProperty = 8192,
    ExactBinding = 65536, SuppressChangeType = 131072,
    OptionalParamBinding = 262144, IgnoreReturn = 16777216,
}

public enum FieldAttributes : uint
{
    PrivateScope = 0, Private = 1, FamANDAssem = 2, Assembly = 3, Family = 4,
    FamORAssem = 5, Public = 6, Static = 16, InitOnly = 32, Literal = 64,
    NotSerialized = 128, HasFieldRVA = 256, SpecialName = 512, RTSpecialName = 1024,
    HasDefault = 32768,
}

public enum MemberTypes : uint
{
    Constructor = 1, Event = 2, Field = 4, Method = 8, Property = 16,
    TypeInfo = 32, Custom = 64, NestedType = 128, All = 191
}

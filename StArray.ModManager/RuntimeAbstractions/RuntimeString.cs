using System.Runtime.InteropServices;
using StArray.ModManager.Il2Cpp;

namespace StArray.ModManager.RuntimeAbstractions;

public readonly unsafe struct RuntimeString
{
    public nint Ptr { get; }
    public bool IsValid => Ptr != 0;

    public RuntimeString(nint ptr) => Ptr = ptr;
    public RuntimeString(RuntimeObject obj) => Ptr = obj.Ptr;

    public int Length => RuntimeManager.IsIl2Cpp && Ptr != 0
        ? Il2CppFunctions.il2cpp_string_length(Ptr)
        : 0;

    public char* Chars => RuntimeManager.IsIl2Cpp && Ptr != 0
        ? Il2CppFunctions.il2cpp_string_chars(Ptr)
        : null;

    public override string ToString()
    {
        var length = Length;
        return length > 0 ? Marshal.PtrToStringUni((nint)Chars, length) ?? string.Empty : string.Empty;
    }

    public static RuntimeString New(string str)
    {
        var domain = RuntimeManager.GetDomain();
        return domain != null ? New(domain, str) : default;
    }

    public static RuntimeString New(IAppDomain domain, string str)
    {
        var ptr = domain.NewString(str);
        return ptr != 0 ? new RuntimeString(ptr) : default;
    }

    public static implicit operator string(RuntimeString value) => value.ToString();
    public static implicit operator RuntimeString(RuntimeObject obj) => new(obj.Ptr);
    public static implicit operator RuntimeObject(RuntimeString value) => new(value.Ptr);
}

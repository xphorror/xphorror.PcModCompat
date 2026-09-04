using System.Runtime.InteropServices;
using StArray.ModManager.Il2Cpp;
using StArray.ModManager.Mono;

namespace StArray.ModManager.RuntimeAbstractions;

public readonly unsafe struct RuntimeString
{
    public nint Ptr { get; }
    public bool IsValid => Ptr != 0;

    public RuntimeString(nint ptr) => Ptr = ptr;
    public RuntimeString(RuntimeObject obj) => Ptr = obj.Ptr;

    public int Length
    {
        get
        {
            if (Ptr == 0) return 0;
            if (RuntimeManager.IsIl2Cpp)
            {
                if (OperatingSystem.IsAndroid())
                    return Il2CppStringReader.TryReadLength(Ptr, out var length) ? length : 0;
                return Il2CppFunctions.il2cpp_string_length(Ptr);
            }
            if (RuntimeManager.IsMono)
                return MonoFunctions.MonoStringLength(Ptr);
            return 0;
        }
    }

    public char* Chars
    {
        get
        {
            if (Ptr == 0) return null;
            if (RuntimeManager.IsIl2Cpp)
            {
                if (OperatingSystem.IsAndroid())
                    return Il2CppStringReader.TryGetCharsAddress(Ptr, out var chars)
                        ? (char*)chars
                        : null;
                return Il2CppFunctions.il2cpp_string_chars(Ptr);
            }
            if (RuntimeManager.IsMono)
                return MonoFunctions.MonoStringChars(Ptr);
            return null;
        }
    }

    public override string ToString()
    {
        if (Ptr == nint.Zero)
            return string.Empty;
        if (RuntimeManager.IsIl2Cpp && OperatingSystem.IsAndroid())
            return Il2CppStringReader.TryRead(Ptr, out var value) ? value : string.Empty;
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

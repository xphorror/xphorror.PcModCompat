using System.Runtime.InteropServices;

namespace StArray.ModManager.Hooks
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class NativeHookAttribute : Attribute
    {
        public string Library { get; } = string.Empty;
        public string Symbol { get; } = string.Empty;
        public long RVA { get; }
        public string ResolverMethod { get; } = string.Empty;
        public ulong Address { get; }
        public CallingConvention Convention { get; set; }
        public bool Enabled { get; set; } = true;
        public NativeHookAttribute(string library, string symbol)
        { Library = library; Symbol = symbol; Convention = CallingConvention.StdCall; }
        public NativeHookAttribute(string library, long rva)
        { Library = library; RVA = rva; Convention = CallingConvention.StdCall; }
        public NativeHookAttribute(string symbolResolver)
        { ResolverMethod = symbolResolver; Convention = CallingConvention.StdCall; }
        public NativeHookAttribute(ulong address)
        { Address = address; Convention = CallingConvention.StdCall; }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class UnmanagedHookAttribute : Attribute
    {
        public string AssemblyName { get; }
        public string? Namespace { get; set; }
        public string ClassName { get; }
        public string MethodName { get; }
        public int ParameterCount { get; set; } = -1;
        public string[]? ParameterTypeNames { get; set; }
        public CallingConvention Convention { get; set; } = CallingConvention.Cdecl;

        public UnmanagedHookAttribute(string assembly, string className, string methodName)
        { AssemblyName = assembly; ClassName = className; MethodName = methodName; }

        public UnmanagedHookAttribute(string assembly, string ns, string className, string methodName)
        { AssemblyName = assembly; Namespace = ns; ClassName = className; MethodName = methodName; }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class Il2CppHookAttribute : Attribute
    {
        public string AssemblyName { get; }
        public string ClassName { get; }
        public string MethodName { get; }
        public int ParameterCount { get; set; } = -1;
        public string[] ParameterTypeNames { get; set; }
        public Il2CppHookAttribute(string asm, string cls, string method)
        { AssemblyName = asm; ClassName = cls; MethodName = method; }
    }
}

using System.Runtime.InteropServices;

namespace StArray.ModManager;

/// <summary>描述由 SourceGenerator 生成的 native import。</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class NativeImportAttribute : Attribute
{
    public string? Library { get; }
    public string? EntryPoint { get; init; }
    public CallingConvention Convention { get; init; } = CallingConvention.Cdecl;
    public CharSet CharSet { get; init; } = CharSet.Ansi;

    public NativeImportAttribute() { }
    public NativeImportAttribute(string library) => Library = library;
}

namespace StArray.ModManager.RuntimeAbstractions;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Enum | AttributeTargets.Interface)]
public sealed class UnmanagedTypeAttribute : Attribute
{
    public string Assembly { get; }
    public string Namespace { get; }
    public string ClassName { get; }

    public UnmanagedTypeAttribute(string assembly, string ns, string className)
    {
        Assembly = assembly;
        Namespace = ns;
        ClassName = className;
    }
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = false)]
public sealed class UnmanagedMemberAttribute : Attribute
{
    public string? Name { get; set; }
    public int ParamCount { get; set; } = -1;
}

/// <summary>记录退化为 pointer 的成员在 Unity runtime 中的真实类型名。</summary>
[AttributeUsage(
    AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Parameter,
    AllowMultiple = false)]
public sealed class UnmanagedTypeNameAttribute(string typeName) : Attribute
{
    public string TypeName { get; } = typeName;
}

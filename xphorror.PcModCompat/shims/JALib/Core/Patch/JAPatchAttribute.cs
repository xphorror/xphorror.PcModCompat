using System.Reflection;

namespace JALib.Core.Patch;

public abstract class JAPatchBaseAttribute : Attribute
{
    internal string PatchId
        => $"JAPatch: {Method?.DeclaringType?.FullName ?? "<unknown>"}.{Method?.Name ?? "<unknown>"}({GetPatchTypeString()})";
    internal string? Class;
    internal Type? ClassType;
    internal string? MethodName;
    internal MethodBase? MethodBase;
    internal MethodInfo? Method;
    public Type? TargetType;
    public string? TargetTypeName;
    public string? TargetMethodName;
    public bool NeedInstance;
    public int MinVersion;
    public int MaxVersion = int.MaxValue;
    public string[]? ArgumentTypes;
    public bool TryingCatch = true;
    public Type[]? ArgumentTypesType;
    public string[]? GenericName;
    public Type[]? GenericType;
    public bool Debug;

    internal Type? ResolvedTargetType => TargetType ?? ClassType ?? MethodBase?.DeclaringType;
    internal string? ResolvedTargetTypeName => TargetTypeName ?? Class;
    internal string? ResolvedTargetMethodName => TargetMethodName ?? MethodName ?? MethodBase?.Name;

    protected abstract string GetPatchTypeString();
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class JAPatchAttribute : JAPatchBaseAttribute
{
    public PatchType PatchType;
    internal bool Disable;
    public int Priority = -1;
    public string[]? Before;
    public string[]? After;

    public JAPatchAttribute(Type targetType, string targetMethodName, PatchType patchType, bool needInstance)
    {
        TargetType = targetType;
        ClassType = targetType;
        TargetMethodName = targetMethodName;
        MethodName = targetMethodName;
        PatchType = patchType;
        NeedInstance = needInstance;
        Disable = needInstance;
    }

    public JAPatchAttribute(string targetTypeName, string targetMethodName, PatchType patchType, bool needInstance)
    {
        TargetTypeName = targetTypeName;
        Class = targetTypeName;
        TargetMethodName = targetMethodName;
        MethodName = targetMethodName;
        PatchType = patchType;
        NeedInstance = needInstance;
        Disable = needInstance;
    }

    public JAPatchAttribute(System.Reflection.MethodBase method, PatchType patchType, bool needInstance)
    {
        TargetType = method.DeclaringType;
        ClassType = method.DeclaringType;
        TargetMethodName = method.Name;
        MethodName = method.Name;
        MethodBase = method;
        PatchType = patchType;
        NeedInstance = needInstance;
        Disable = needInstance;
    }

    public JAPatchAttribute(Delegate callback, PatchType patchType, bool needInstance)
    {
        TargetType = callback.Method.DeclaringType;
        ClassType = callback.Method.DeclaringType;
        TargetMethodName = callback.Method.Name;
        MethodName = callback.Method.Name;
        MethodBase = callback.Method;
        PatchType = patchType;
        NeedInstance = needInstance;
        Disable = needInstance;
    }

    protected override string GetPatchTypeString() => PatchType.ToString();
}

[AttributeUsage(AttributeTargets.Method)]
public class JAReversePatchAttribute : JAPatchBaseAttribute
{
    public readonly ReversePatchType PatchType;
    public bool TryCatchChildren = true;

    public JAReversePatchAttribute(
        string targetTypeName,
        string methodName,
        ReversePatchType patchType)
    {
        Class = TargetTypeName = targetTypeName;
        MethodName = TargetMethodName = methodName;
        PatchType = patchType;
    }

    public JAReversePatchAttribute(
        Type targetType,
        string methodName,
        ReversePatchType patchType)
    {
        ClassType = TargetType = targetType;
        MethodName = TargetMethodName = methodName;
        PatchType = patchType;
    }

    public JAReversePatchAttribute(MethodBase method, ReversePatchType patchType)
    {
        MethodBase = method;
        TargetType = method.DeclaringType;
        TargetMethodName = method.Name;
        PatchType = patchType;
    }

    public JAReversePatchAttribute(Delegate callback, ReversePatchType patchType)
        : this(callback.Method, patchType)
    {
    }

    protected override string GetPatchTypeString() => $"ReversePatch ({PatchType})";
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class JAOverridePatchAttribute : JAPatchBaseAttribute
{
    public bool IgnoreBasePatch = true;
    public Type? targetType;
    public string? targetTypeName;
    public bool checkType = true;

    public JAOverridePatchAttribute(string targetTypeName, string methodName)
    {
        Class = TargetTypeName = targetTypeName;
        MethodName = TargetMethodName = methodName;
    }

    public JAOverridePatchAttribute(Type targetType, string methodName)
    {
        ClassType = TargetType = targetType;
        MethodName = TargetMethodName = methodName;
    }

    public JAOverridePatchAttribute(string targetTypeName)
        => Class = TargetTypeName = targetTypeName;

    public JAOverridePatchAttribute(Type targetType)
        => ClassType = TargetType = targetType;

    public JAOverridePatchAttribute()
    {
    }

    protected override string GetPatchTypeString() => "Override";
}

public enum PatchType
{
    Prefix = 0,
    Postfix = 1,
    Transpiler = 2,
    Finalizer = 3,
    Replace = 4
}

[Flags]
public enum ReversePatchType
{
    Original = 0,
    PrefixCombine = 1,
    PostfixCombine = 2,
    TranspilerCombine = 4,
    FinalizerCombine = 8,
    ReplaceCombine = 16,
    OverrideCombine = 32,
    ReplaceTranspilerCombine = 64,
    // v42 and v44 conflict on this member: v42 has AllCombine=127 (no ILManipulate),
    // v44 has ILManipulateCombine=128 / AllInsidePatchCombine=212 / AllCombine=255.
    // One enum field cannot satisfy both literals, so the shim follows the newer v44
    // layout and the v42 "AllCombine|literal=127" manifest line stays permanently
    // unsatisfied in the coverage report.
    ILManipulateCombine = 128,
    AllInsidePatchCombine = TranspilerCombine | ReplaceCombine |
                            ReplaceTranspilerCombine | ILManipulateCombine,
    AllCombine = PrefixCombine | PostfixCombine | FinalizerCombine |
                 OverrideCombine | AllInsidePatchCombine,
    DontUpdate = 0x40000000
}

[Flags]
public enum AllPatchType
{
    Prefix = 1,
    Postfix = 2,
    Transpiler = 4,
    Finalizer = 8,
    TryPrefix = 16,
    TryPostfix = 32,
    Remove = 64,
    Replace = 128,
    Reverse = 256,
    Override = 512,
    ReplaceTranspiler = 1024,
    AllPrefix = Prefix | TryPrefix | Remove | Override,
    AllPostfix = Postfix | TryPostfix,
    AllTranspiler = Transpiler | Replace | ReplaceTranspiler,
    All = AllPrefix | AllPostfix | AllTranspiler | Finalizer | Reverse
}

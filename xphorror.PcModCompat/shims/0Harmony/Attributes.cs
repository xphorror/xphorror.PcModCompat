namespace HarmonyLib;

// ABI mirror of Harmony 2.4 HarmonyLib/Public/Attributes.cs. Member names, values and
// constructor signatures must match the upstream assembly exactly: PC MODs are compiled
// against the real 0Harmony, so a missing annotation type or constructor overload becomes
// a TypeLoadException / MissingMethodException at rewrite or load time on Android.
//
// Behaviour that needs a live IL pipeline is NOT emulated here. Target aggregation is done
// statically by PcCompatHarmonyAttributeAggregator from assembly metadata, which reads the
// very same attribute blobs.

public enum MethodType
{
    Normal = 0,
    Getter = 1,
    Setter = 2,
    Constructor = 3,
    StaticConstructor = 4,
    Enumerator = 5,
    Async = 6,
    Finalizer = 7,
    EventAdd = 8,
    EventRemove = 9,
    OperatorImplicit = 10,
    OperatorExplicit = 11,
    OperatorUnaryPlus = 12,
    OperatorUnaryNegation = 13,
    OperatorLogicalNot = 14,
    OperatorOnesComplement = 15,
    OperatorIncrement = 16,
    OperatorDecrement = 17,
    OperatorTrue = 18,
    OperatorFalse = 19,
    OperatorAddition = 20,
    OperatorSubtraction = 21,
    OperatorMultiply = 22,
    OperatorDivision = 23,
    OperatorModulus = 24,
    OperatorBitwiseAnd = 25,
    OperatorBitwiseOr = 26,
    OperatorExclusiveOr = 27,
    OperatorLeftShift = 28,
    OperatorRightShift = 29,
    OperatorEquality = 30,
    OperatorInequality = 31,
    OperatorGreaterThan = 32,
    OperatorLessThan = 33,
    OperatorGreaterThanOrEqual = 34,
    OperatorLessThanOrEqual = 35,
    OperatorComma = 36
}

public enum ArgumentType
{
    Normal,
    Ref,
    Out,
    Pointer
}

public enum HarmonyPatchType
{
    All,
    Prefix,
    Postfix,
    Transpiler,
    Finalizer,
    ReversePatch,
    InnerPrefix,
    InnerPostfix
}

public enum HarmonyReversePatchType
{
    Original,
    Snapshot
}

public enum MethodDispatchType
{
    VirtualCall,
    Call
}

public class HarmonyAttribute : Attribute
{
    public HarmonyMethod info = new();
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public class HarmonyPatchCategory : HarmonyAttribute
{
    public HarmonyPatchCategory(string category) => info.category = category;
}

[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Delegate | AttributeTargets.Method,
    AllowMultiple = true)]
public class HarmonyPatch : HarmonyAttribute
{
    public HarmonyPatch()
    {
    }

    public HarmonyPatch(Type declaringType) => info.declaringType = declaringType;

    public HarmonyPatch(Type declaringType, Type[] argumentTypes)
    {
        info.declaringType = declaringType;
        info.argumentTypes = argumentTypes;
    }

    public HarmonyPatch(Type declaringType, string methodName)
    {
        info.declaringType = declaringType;
        info.methodName = methodName;
    }

    public HarmonyPatch(Type declaringType, string methodName, params Type[] argumentTypes)
    {
        info.declaringType = declaringType;
        info.methodName = methodName;
        info.argumentTypes = argumentTypes;
    }

    public HarmonyPatch(Type declaringType, string methodName, Type[] argumentTypes, ArgumentType[] argumentVariations)
    {
        info.declaringType = declaringType;
        info.methodName = methodName;
        ParseSpecialArguments(argumentTypes, argumentVariations);
    }

    public HarmonyPatch(Type declaringType, MethodType methodType)
    {
        info.declaringType = declaringType;
        info.methodType = methodType;
    }

    public HarmonyPatch(Type declaringType, MethodType methodType, params Type[] argumentTypes)
    {
        info.declaringType = declaringType;
        info.methodType = methodType;
        info.argumentTypes = argumentTypes;
    }

    public HarmonyPatch(Type declaringType, MethodType methodType, Type[] argumentTypes, ArgumentType[] argumentVariations)
    {
        info.declaringType = declaringType;
        info.methodType = methodType;
        ParseSpecialArguments(argumentTypes, argumentVariations);
    }

    public HarmonyPatch(Type declaringType, string methodName, MethodType methodType)
    {
        info.declaringType = declaringType;
        info.methodName = methodName;
        info.methodType = methodType;
    }

    public HarmonyPatch(string methodName) => info.methodName = methodName;

    public HarmonyPatch(string methodName, params Type[] argumentTypes)
    {
        info.methodName = methodName;
        info.argumentTypes = argumentTypes;
    }

    public HarmonyPatch(string methodName, Type[] argumentTypes, ArgumentType[] argumentVariations)
    {
        info.methodName = methodName;
        ParseSpecialArguments(argumentTypes, argumentVariations);
    }

    public HarmonyPatch(string methodName, MethodType methodType)
    {
        info.methodName = methodName;
        info.methodType = methodType;
    }

    public HarmonyPatch(MethodType methodType) => info.methodType = methodType;

    public HarmonyPatch(MethodType methodType, params Type[] argumentTypes)
    {
        info.methodType = methodType;
        info.argumentTypes = argumentTypes;
    }

    public HarmonyPatch(MethodType methodType, Type[] argumentTypes, ArgumentType[] argumentVariations)
    {
        info.methodType = methodType;
        ParseSpecialArguments(argumentTypes, argumentVariations);
    }

    public HarmonyPatch(Type[] argumentTypes) => info.argumentTypes = argumentTypes;

    public HarmonyPatch(Type[] argumentTypes, ArgumentType[] argumentVariations)
        => ParseSpecialArguments(argumentTypes, argumentVariations);

    public HarmonyPatch(string typeName, string methodName, MethodType methodType = MethodType.Normal)
    {
        // Upstream resolves the type eagerly. On Android the declaring type is usually an
        // IL2CPP type that CoreCLR cannot see, so the resolution fails and only the raw name
        // survives in the attribute blob. The static aggregator reads that blob, so record a
        // diagnostic instead of pretending the annotation was fully understood at runtime.
        info.declaringType = AccessTools.TypeByName(typeName);
        info.declaringTypeName = typeName;
        info.methodName = methodName;
        info.methodType = methodType;
        if (info.declaringType == null)
            HarmonyRegistry.ReportUnresolvedTypeName(typeName, methodName);
    }

    private void ParseSpecialArguments(Type[] argumentTypes, ArgumentType[]? argumentVariations)
    {
        if (argumentVariations is null || argumentVariations.Length == 0)
        {
            info.argumentTypes = argumentTypes;
            return;
        }

        if (argumentTypes.Length < argumentVariations.Length)
            throw new ArgumentException(
                "argumentVariations contains more elements than argumentTypes",
                nameof(argumentVariations));

        var types = new List<Type>();
        for (var i = 0; i < argumentTypes.Length; i++)
        {
            var type = argumentTypes[i];
            switch (argumentVariations[i])
            {
                case ArgumentType.Normal:
                    break;
                case ArgumentType.Ref:
                case ArgumentType.Out:
                    type = type.MakeByRefType();
                    break;
                case ArgumentType.Pointer:
                    type = type.MakePointerType();
                    break;
            }
            types.Add(type);
        }
        info.argumentTypes = [.. types];
    }
}

[AttributeUsage(AttributeTargets.Delegate, AllowMultiple = true)]
public class HarmonyDelegate : HarmonyPatch
{
    public HarmonyDelegate(Type declaringType)
        : base(declaringType) { }

    public HarmonyDelegate(Type declaringType, Type[] argumentTypes)
        : base(declaringType, argumentTypes) { }

    public HarmonyDelegate(Type declaringType, string methodName)
        : base(declaringType, methodName) { }

    public HarmonyDelegate(Type declaringType, string methodName, params Type[] argumentTypes)
        : base(declaringType, methodName, argumentTypes) { }

    public HarmonyDelegate(Type declaringType, string methodName, Type[] argumentTypes, ArgumentType[] argumentVariations)
        : base(declaringType, methodName, argumentTypes, argumentVariations) { }

    public HarmonyDelegate(Type declaringType, MethodDispatchType methodDispatchType)
        : base(declaringType, MethodType.Normal)
        => info.nonVirtualDelegate = methodDispatchType == MethodDispatchType.Call;

    public HarmonyDelegate(Type declaringType, MethodDispatchType methodDispatchType, params Type[] argumentTypes)
        : base(declaringType, MethodType.Normal, argumentTypes)
        => info.nonVirtualDelegate = methodDispatchType == MethodDispatchType.Call;

    public HarmonyDelegate(
        Type declaringType,
        MethodDispatchType methodDispatchType,
        Type[] argumentTypes,
        ArgumentType[] argumentVariations)
        : base(declaringType, MethodType.Normal, argumentTypes, argumentVariations)
        => info.nonVirtualDelegate = methodDispatchType == MethodDispatchType.Call;

    public HarmonyDelegate(Type declaringType, string methodName, MethodDispatchType methodDispatchType)
        : base(declaringType, methodName, MethodType.Normal)
        => info.nonVirtualDelegate = methodDispatchType == MethodDispatchType.Call;

    public HarmonyDelegate(string methodName)
        : base(methodName) { }

    public HarmonyDelegate(string methodName, params Type[] argumentTypes)
        : base(methodName, argumentTypes) { }

    public HarmonyDelegate(string methodName, Type[] argumentTypes, ArgumentType[] argumentVariations)
        : base(methodName, argumentTypes, argumentVariations) { }

    public HarmonyDelegate(string methodName, MethodDispatchType methodDispatchType)
        : base(methodName, MethodType.Normal)
        => info.nonVirtualDelegate = methodDispatchType == MethodDispatchType.Call;

    public HarmonyDelegate(MethodDispatchType methodDispatchType)
        => info.nonVirtualDelegate = methodDispatchType == MethodDispatchType.Call;

    public HarmonyDelegate(MethodDispatchType methodDispatchType, params Type[] argumentTypes)
        : base(MethodType.Normal, argumentTypes)
        => info.nonVirtualDelegate = methodDispatchType == MethodDispatchType.Call;

    public HarmonyDelegate(MethodDispatchType methodDispatchType, Type[] argumentTypes, ArgumentType[] argumentVariations)
        : base(MethodType.Normal, argumentTypes, argumentVariations)
        => info.nonVirtualDelegate = methodDispatchType == MethodDispatchType.Call;

    public HarmonyDelegate(Type[] argumentTypes)
        : base(argumentTypes) { }

    public HarmonyDelegate(Type[] argumentTypes, ArgumentType[] argumentVariations)
        : base(argumentTypes, argumentVariations) { }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method, AllowMultiple = true)]
public class HarmonyReversePatch : HarmonyAttribute
{
    public HarmonyReversePatch(HarmonyReversePatchType type = HarmonyReversePatchType.Original)
        => info.reversePatchType = type;
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public class HarmonyPatchAll : HarmonyAttribute
{
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method)]
public class HarmonyPriority : HarmonyAttribute
{
    public HarmonyPriority(int priority) => info.priority = priority;
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method)]
public class HarmonyBefore : HarmonyAttribute
{
    public HarmonyBefore(params string[] before) => info.before = before;
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method)]
public class HarmonyAfter : HarmonyAttribute
{
    public HarmonyAfter(params string[] after) => info.after = after;
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method)]
public class HarmonyDebug : HarmonyAttribute
{
    public HarmonyDebug() => info.debug = true;
}

[AttributeUsage(AttributeTargets.Method)]
public class HarmonyPrepare : Attribute
{
}

[AttributeUsage(AttributeTargets.Method)]
public class HarmonyCleanup : Attribute
{
}

[AttributeUsage(AttributeTargets.Method)]
public class HarmonyTargetMethod : Attribute
{
}

[AttributeUsage(AttributeTargets.Method)]
public class HarmonyTargetMethods : Attribute
{
}

[AttributeUsage(AttributeTargets.Method)]
public class HarmonyPrefix : Attribute
{
}

[AttributeUsage(AttributeTargets.Method)]
public class HarmonyPostfix : Attribute
{
}

[AttributeUsage(AttributeTargets.Method)]
public class HarmonyTranspiler : Attribute
{
}

[AttributeUsage(AttributeTargets.Method)]
public class HarmonyFinalizer : Attribute
{
}

// Harmony 2.4 recognizes HarmonyPatchType.InnerPrefix / InnerPostfix through the method-name
// convention only; the matching annotation types are still unreleased upstream (see
// Harmony/docs/infix). The shim deliberately does not invent them, so the ABI stays a subset
// of the real assembly. PcCompatHarmonyAttributeAggregator honours the name convention.

[AttributeUsage(
    AttributeTargets.Parameter | AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Struct,
    AllowMultiple = true)]
public class HarmonyArgument : Attribute
{
    public string? OriginalName { get; private set; }

    public int Index { get; private set; }

    public string? NewName { get; private set; }

    public HarmonyArgument(string originalName) : this(originalName, null)
    {
    }

    public HarmonyArgument(int index) : this(index, null)
    {
    }

    public HarmonyArgument(string originalName, string? newName)
    {
        OriginalName = originalName;
        Index = -1;
        NewName = newName;
    }

    public HarmonyArgument(int index, string? name)
    {
        OriginalName = null;
        Index = index;
        NewName = name;
    }
}

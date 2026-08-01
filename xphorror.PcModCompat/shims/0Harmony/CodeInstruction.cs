using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;

namespace HarmonyLib;

// ABI mirror of Harmony 2.4 HarmonyLib/Public/CodeInstruction.cs.
//
// Transpiler bodies are compiled against this type even when they never run: JALib's VersionSafe
// pattern uses transpiler stubs purely as redirection markers, and loading the MOD assembly fails
// with a TypeLoadException if any referenced member is missing. Instances are therefore fully
// constructible and inspectable; nothing here emits IL.
public class CodeInstruction
{
    public OpCode opcode;

    public object? operand;

    public List<Label> labels = [];

    public List<ExceptionBlock> blocks = [];

    internal CodeInstruction()
    {
    }

    public CodeInstruction(OpCode opcode, object? operand = null)
    {
        this.opcode = opcode;
        this.operand = operand;
    }

    public CodeInstruction(CodeInstruction instruction)
    {
        opcode = instruction.opcode;
        operand = instruction.operand;
        labels = [.. instruction.labels];
        blocks = [.. instruction.blocks];
    }

    public CodeInstruction Clone() => new(this) { labels = [], blocks = [] };

    public CodeInstruction Clone(OpCode opcode)
    {
        var instruction = Clone();
        instruction.opcode = opcode;
        return instruction;
    }

    public CodeInstruction Clone(object operand)
    {
        var instruction = Clone();
        instruction.operand = operand;
        return instruction;
    }

    public static CodeInstruction Call(Type type, string name, Type[]? parameters = null, Type[]? generics = null)
    {
        var method = AccessTools.Method(type, name, parameters, generics)
                     ?? throw new ArgumentException($"No method found for type={type}, name={name}, parameters={parameters.Description()}");
        return new CodeInstruction(OpCodes.Call, method);
    }

    public static CodeInstruction Call(string typeColonMethodname, Type[]? parameters = null, Type[]? generics = null)
    {
        var method = AccessTools.Method(typeColonMethodname, parameters, generics)
                     ?? throw new ArgumentException(
                         $"No method found for {typeColonMethodname}, parameters={parameters.Description()}, generics={generics.Description()}");
        return new CodeInstruction(OpCodes.Call, method);
    }

    public static CodeInstruction Call(Expression<Action> expression) => new(OpCodes.Call, SymbolExtensions.GetMethodInfo(expression));

    public static CodeInstruction Call<T>(Expression<Action<T>> expression) => new(OpCodes.Call, SymbolExtensions.GetMethodInfo(expression));

    public static CodeInstruction Call<T, TResult>(Expression<Func<T, TResult>> expression) => new(OpCodes.Call, SymbolExtensions.GetMethodInfo(expression));

    public static CodeInstruction Call(LambdaExpression expression) => new(OpCodes.Call, SymbolExtensions.GetMethodInfo(expression));

    // Upstream builds a DynamicMethodDefinition for a capturing closure so the emitted call can carry
    // the captured state. That path needs runtime IL emission, which is exactly what this host does not
    // have - so the static case (the only one that is a plain method reference) is honoured, and the
    // capturing case fails loudly instead of returning an instruction that would silently lose state.
    public static CodeInstruction CallClosure<T>(T closure) where T : Delegate
    {
        if (closure is null)
            throw new ArgumentNullException(nameof(closure));

        if (closure.Method.IsStatic && closure.Target is null)
            return new CodeInstruction(OpCodes.Call, closure.Method);

        throw new NotSupportedException(
            $"CodeInstruction.CallClosure requires runtime IL emission for the capturing closure " +
            $"{closure.Method.DeclaringType?.FullName}.{closure.Method.Name}, which this host does not provide. " +
            "Use a static method reference instead.");
    }

    public static CodeInstruction LoadField(Type type, string name, bool useAddress = false)
    {
        var field = AccessTools.Field(type, name)
                    ?? throw new ArgumentException($"No field found for type={type}, name={name}");
        return new CodeInstruction(
            useAddress
                ? field.IsStatic ? OpCodes.Ldsflda : OpCodes.Ldflda
                : field.IsStatic ? OpCodes.Ldsfld : OpCodes.Ldfld,
            field);
    }

    public static CodeInstruction StoreField(Type type, string name)
    {
        var field = AccessTools.Field(type, name)
                    ?? throw new ArgumentException($"No field found for type={type}, name={name}");
        return new CodeInstruction(field.IsStatic ? OpCodes.Stsfld : OpCodes.Stfld, field);
    }

    public static CodeInstruction LoadLocal(int index, bool useAddress = false)
    {
        if (useAddress)
        {
            return index < 256
                ? new CodeInstruction(OpCodes.Ldloca_S, Convert.ToByte(index))
                : new CodeInstruction(OpCodes.Ldloca, Convert.ToInt16(index));
        }

        return index switch
        {
            0 => new CodeInstruction(OpCodes.Ldloc_0),
            1 => new CodeInstruction(OpCodes.Ldloc_1),
            2 => new CodeInstruction(OpCodes.Ldloc_2),
            3 => new CodeInstruction(OpCodes.Ldloc_3),
            < 256 => new CodeInstruction(OpCodes.Ldloc_S, Convert.ToByte(index)),
            _ => new CodeInstruction(OpCodes.Ldloc, Convert.ToInt16(index))
        };
    }

    public static CodeInstruction StoreLocal(int index) => index switch
    {
        0 => new CodeInstruction(OpCodes.Stloc_0),
        1 => new CodeInstruction(OpCodes.Stloc_1),
        2 => new CodeInstruction(OpCodes.Stloc_2),
        3 => new CodeInstruction(OpCodes.Stloc_3),
        < 256 => new CodeInstruction(OpCodes.Stloc_S, Convert.ToByte(index)),
        _ => new CodeInstruction(OpCodes.Stloc, Convert.ToInt16(index))
    };

    public static CodeInstruction LoadArgument(int index, bool useAddress = false)
    {
        if (useAddress)
        {
            return index < 256
                ? new CodeInstruction(OpCodes.Ldarga_S, Convert.ToByte(index))
                : new CodeInstruction(OpCodes.Ldarga, Convert.ToInt16(index));
        }

        return index switch
        {
            0 => new CodeInstruction(OpCodes.Ldarg_0),
            1 => new CodeInstruction(OpCodes.Ldarg_1),
            2 => new CodeInstruction(OpCodes.Ldarg_2),
            3 => new CodeInstruction(OpCodes.Ldarg_3),
            < 256 => new CodeInstruction(OpCodes.Ldarg_S, Convert.ToByte(index)),
            _ => new CodeInstruction(OpCodes.Ldarg, Convert.ToInt16(index))
        };
    }

    public static CodeInstruction StoreArgument(int index) => index < 256
        ? new CodeInstruction(OpCodes.Starg_S, Convert.ToByte(index))
        : new CodeInstruction(OpCodes.Starg, Convert.ToInt16(index));

    public bool HasBlock(ExceptionBlockType type) => blocks.Any(block => block.blockType == type);

    public override string ToString()
    {
        var list = new List<string>();
        foreach (var label in labels)
            list.Add($"Label{label.GetHashCode()}");
        foreach (var block in blocks)
            list.Add($"EX_{block.blockType.ToString().Replace("Block", "")}");

        var extras = list.Count > 0 ? $" [{list.Join()}]" : "";
        var operandStr = operand is null ? "" : " " + FormatArgument(operand);
        return opcode + operandStr + extras;
    }

    private static string FormatArgument(object argument) => argument switch
    {
        MethodBase method => method.FullDescription(),
        FieldInfo field => $"{field.FieldType.FullDescription()} {field.DeclaringType.FullDescription()}::{field.Name}",
        Label label => $"Label{label.GetHashCode()}",
        string text => text.ToLiteral(),
        _ => argument.ToString() ?? string.Empty
    };
}

public enum ExceptionBlockType
{
    BeginExceptionBlock,
    BeginCatchBlock,
    BeginExceptFilterBlock,
    BeginFaultBlock,
    BeginFinallyBlock,
    EndExceptionBlock
}

public class ExceptionBlock(ExceptionBlockType blockType, Type? catchType = null)
{
    public ExceptionBlockType blockType = blockType;

    public Type? catchType = catchType ?? typeof(object);
}

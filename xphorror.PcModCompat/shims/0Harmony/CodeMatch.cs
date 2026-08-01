using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;

namespace HarmonyLib;

// ABI mirror of Harmony 2.4 HarmonyLib/Tools/CodeMatch.cs.
//
// A CodeMatch is a pattern over CodeInstruction objects - opcode sets, operands, labels, exception
// blocks, jump relations, or a free predicate. Nothing here reads or writes IL, so the whole class
// reproduces upstream verbatim. Its internal Set overloads exist for HarmonyLib.Code.
public class CodeMatch : CodeInstruction
{
    /// <summary>The name of the match</summary>
    public string? name;

    /// <summary>The matched opcodes</summary>
    public HashSet<OpCode> opcodeSet = [];

    // for backwards compatibility we keep
    /// <summary>The matched opcodes</summary>
    [Obsolete("Use opcodeSet instead")]
    [EditorBrowsable(EditorBrowsableState.Never)]
#pragma warning disable IDE1006
    public List<OpCode> opcodes
    {
        get => [.. opcodeSet];
        set => opcodeSet = [.. value];
    }
#pragma warning restore IDE1006

    /// <summary>The matched operands</summary>
    public List<object> operands = [];

    /// <summary>The jumps from the match</summary>
    public List<int> jumpsFrom = [];

    /// <summary>The jumps to the match</summary>
    public List<int> jumpsTo = [];

    /// <summary>The match predicate</summary>
    public Func<CodeInstruction, bool>? predicate;

    // used by HarmonyLib.Code
    internal CodeMatch Set(object? operand, string? name)
    {
        this.operand ??= operand;
        if (operand != null)
            operands.Add(operand);
        this.name ??= name;
        return this;
    }

    internal CodeMatch Set(OpCode opcode, object? operand, string? name)
    {
        this.opcode = opcode;
        _ = opcodeSet.Add(opcode);
        this.operand ??= operand;
        if (operand != null)
            operands.Add(operand);
        this.name ??= name;
        return this;
    }

    /// <summary>Creates a code match</summary>
    public CodeMatch(OpCode? opcode = null, object? operand = null, string? name = null)
    {
        if (opcode is OpCode opcodeValue)
        {
            this.opcode = opcodeValue;
            _ = opcodeSet.Add(opcodeValue);
        }
        if (operand != null)
            operands.Add(operand);
        this.operand = operand;
        this.name = name;
    }

    /// <summary>Creates a code match</summary>
    public static CodeMatch WithOpcodes(HashSet<OpCode> opcodes, object? operand = null, string? name = null)
        => new(null, operand, name) { opcodeSet = opcodes };

    /// <summary>Creates a code match that calls a method</summary>
    public CodeMatch(Expression<Action> expression, string? name = null)
    {
        opcodeSet.UnionWith(CodeInstructionExtensions.opcodesCalling);
        operand = SymbolExtensions.GetMethodInfo(expression);
        if (operand != null)
            operands.Add(operand);
        this.name = name;
    }

    /// <summary>Creates a code match that calls a method</summary>
    public CodeMatch(LambdaExpression expression, string? name = null)
    {
        opcodeSet.UnionWith(CodeInstructionExtensions.opcodesCalling);
        operand = SymbolExtensions.GetMethodInfo(expression);
        if (operand != null)
            operands.Add(operand);
        this.name = name;
    }

    /// <summary>Creates a code match</summary>
    public CodeMatch(CodeInstruction instruction, string? name = null) : this(instruction.opcode, instruction.operand, name)
    {
    }

    /// <summary>Creates a code match</summary>
    public CodeMatch(Func<CodeInstruction, bool> predicate, string? name = null)
    {
        this.predicate = predicate;
        this.name = name;
    }

    internal bool Matches(List<CodeInstruction> codes, CodeInstruction instruction)
    {
        if (predicate != null)
            return predicate(instruction);

        if (opcodeSet.Count > 0 && opcodeSet.Contains(instruction.opcode) == false)
            return false;
        if (operands.Count > 0 && operands.Contains(instruction.operand!) == false)
            return false;
        if (labels.Count > 0 && labels.Intersect(instruction.labels).Any() == false)
            return false;
        if (blocks.Count > 0 && blocks.Intersect(instruction.blocks).Any() == false)
            return false;

        if (jumpsFrom.Count > 0 && jumpsFrom.Select(index => codes[index].operand).OfType<Label>()
                                            .Intersect(instruction.labels).Any() == false)
            return false;

        if (jumpsTo.Count > 0)
        {
            var operand = instruction.operand;
            if (operand == null || operand.GetType() != typeof(Label))
                return false;
            var label = (Label)operand;
            var indices = Enumerable.Range(0, codes.Count).Where(idx => codes[idx].labels.Contains(label));
            if (jumpsTo.Intersect(indices).Any() == false)
                return false;
        }

        return true;
    }

    /// <summary>Tests for any form of Ldarg*</summary>
    public static CodeMatch IsLdarg(int? n = null) => new(instruction => instruction.IsLdarg(n));

    /// <summary>Tests for Ldarga/Ldarga_S</summary>
    public static CodeMatch IsLdarga(int? n = null) => new(instruction => instruction.IsLdarga(n));

    /// <summary>Tests for Starg/Starg_S</summary>
    public static CodeMatch IsStarg(int? n = null) => new(instruction => instruction.IsStarg(n));

    /// <summary>Tests for any form of Ldloc*</summary>
    public static CodeMatch IsLdloc(LocalBuilder? variable = null) => new(instruction => instruction.IsLdloc(variable));

    /// <summary>Tests for any form of Stloc*</summary>
    public static CodeMatch IsStloc(LocalBuilder? variable = null) => new(instruction => instruction.IsStloc(variable));

    /// <summary>Tests if the code instruction calls the method</summary>
    public static CodeMatch Calls(MethodInfo method) => WithOpcodes(CodeInstructionExtensions.opcodesCalling, method);

    /// <summary>Tests if the code instruction calls the constructor</summary>
    public static CodeMatch Calls(ConstructorInfo? constructor) => new(instruction =>
        (instruction.opcode == OpCodes.Newobj || instruction.opcode == OpCodes.Call)
        && instruction.operand is ConstructorInfo ctor
        && (constructor is null || Equals(ctor, constructor)));

    /// <summary>Tests if the code instruction loads a constant</summary>
    public static CodeMatch LoadsConstant() => new(instruction => instruction.LoadsConstant());

    /// <summary>Tests if the code instruction loads an integer constant</summary>
    public static CodeMatch LoadsConstant(long number) => new(instruction => instruction.LoadsConstant(number));

    /// <summary>Tests if the code instruction loads a floating point constant</summary>
    public static CodeMatch LoadsConstant(double number) => new(instruction => instruction.LoadsConstant(number));

    /// <summary>Tests if the code instruction loads an enum constant</summary>
    public static CodeMatch LoadsConstant(Enum e) => new(instruction => instruction.LoadsConstant(e));

    /// <summary>Tests if the code instruction loads a string constant</summary>
    public static CodeMatch LoadsConstant(string str) => new(instruction => instruction.LoadsConstant(str));

    /// <summary>Tests if the code instruction loads a field</summary>
    public static CodeMatch LoadsField(FieldInfo field, bool byAddress = false) => new(instruction => instruction.LoadsField(field, byAddress));

    /// <summary>Tests if the code instruction loads a field</summary>
    public static CodeMatch LoadsField<T>(Expression<Func<T>> expression, bool byAddress = false)
        => LoadsField(SymbolExtensions.GetFieldInfo(expression), byAddress);

    /// <summary>Tests if the code instruction stores a field</summary>
    public static CodeMatch StoresField(FieldInfo field) => new(instruction => instruction.StoresField(field));

    /// <summary>Tests if the code instruction stores a field</summary>
    public static CodeMatch StoresField<T>(Expression<Func<T>> expression) => StoresField(SymbolExtensions.GetFieldInfo(expression));

    /// <summary>Creates a code match that calls a method</summary>
    public static CodeMatch Calls(Expression<Action> expression) => new(expression);

    /// <summary>Creates a code match that calls a method</summary>
    public static CodeMatch Calls(LambdaExpression expression) => new(expression);

    /// <summary>Creates a code match for local loads</summary>
    public static CodeMatch LoadsLocal(bool useAddress = false, string? name = null)
        => WithOpcodes(useAddress ? CodeInstructionExtensions.opcodesLoadingLocalByAddress : CodeInstructionExtensions.opcodesLoadingLocalNormal, null, name);

    /// <summary>Creates a code match for local stores</summary>
    public static CodeMatch StoresLocal(string? name = null) => WithOpcodes(CodeInstructionExtensions.opcodesStoringLocal, null, name);

    /// <summary>Creates a code match for argument loads</summary>
    public static CodeMatch LoadsArgument(bool useAddress = false, string? name = null)
        => WithOpcodes(useAddress ? CodeInstructionExtensions.opcodesLoadingArgumentByAddress : CodeInstructionExtensions.opcodesLoadingArgumentNormal, null, name);

    /// <summary>Creates a code match for argument stores</summary>
    public static CodeMatch StoresArgument(string? name = null) => WithOpcodes(CodeInstructionExtensions.opcodesStoringArgument, null, name);

    /// <summary>Creates a code match for branching</summary>
    public static CodeMatch Branches(string? name = null) => WithOpcodes(CodeInstructionExtensions.opcodesBranching, null, name);

    /// <summary>Returns a string that represents the match</summary>
    public override string ToString()
    {
        var result = "[";
        if (name != null)
            result += $"{name}: ";
        if (opcodeSet.Count > 0)
            result += $"opcodes={opcodeSet.Join()} ";
        if (operands.Count > 0)
            result += $"operands={operands.Join()} ";
        if (labels.Count > 0)
            result += $"labels={labels.Join()} ";
        if (blocks.Count > 0)
            result += $"blocks={blocks.Join()} ";
        if (jumpsFrom.Count > 0)
            result += $"jumpsFrom={jumpsFrom.Join()} ";
        if (jumpsTo.Count > 0)
            result += $"jumpsTo={jumpsTo.Join()} ";
        if (predicate != null)
            result += "predicate=yes ";
        return $"{result.TrimEnd()}]";
    }
}

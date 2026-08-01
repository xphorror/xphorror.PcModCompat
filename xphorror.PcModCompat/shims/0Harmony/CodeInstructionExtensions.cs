using System.ComponentModel;
using System.Reflection;
using System.Reflection.Emit;

namespace HarmonyLib;

// ABI mirror of Harmony 2.4 HarmonyLib/Tools/Extensions.cs (CodeInstructionExtensions and
// MethodBaseExtensions halves; the CodeInstructionsExtensions half lives in CodeMatcher.cs because it
// needs CodeMatcher).
//
// Same reasoning as Transpilers: none of this rewrites IL. Every member here reads or mutates the
// CodeInstruction objects a MOD already holds - opcode comparisons, label lists, exception block
// lists - so it can be reproduced verbatim on any runtime. What a missing member costs is not a
// degraded transpiler but a TypeLoadException for the entire MOD assembly, taking its prefixes and
// postfixes down with it.
//
// Upstream's internal opcode sets are kept internal here too: CodeMatch and CodeMatcher consume them.
public static class CodeInstructionExtensions
{
    internal static readonly HashSet<OpCode> opcodesCalling =
    [
        OpCodes.Call,
        OpCodes.Callvirt
    ];

    internal static readonly HashSet<OpCode> opcodesLoadingLocalByAddress =
    [
        OpCodes.Ldloca_S,
        OpCodes.Ldloca
    ];

    internal static readonly HashSet<OpCode> opcodesLoadingLocalNormal =
    [
        OpCodes.Ldloc_0,
        OpCodes.Ldloc_1,
        OpCodes.Ldloc_2,
        OpCodes.Ldloc_3,
        OpCodes.Ldloc_S,
        OpCodes.Ldloc
    ];

    internal static readonly HashSet<OpCode> opcodesStoringLocal =
    [
        OpCodes.Stloc_0,
        OpCodes.Stloc_1,
        OpCodes.Stloc_2,
        OpCodes.Stloc_3,
        OpCodes.Stloc_S,
        OpCodes.Stloc
    ];

    internal static readonly HashSet<OpCode> opcodesLoadingArgumentByAddress =
    [
        OpCodes.Ldarga_S,
        OpCodes.Ldarga
    ];

    internal static readonly HashSet<OpCode> opcodesLoadingArgumentNormal =
    [
        OpCodes.Ldarg_0,
        OpCodes.Ldarg_1,
        OpCodes.Ldarg_2,
        OpCodes.Ldarg_3,
        OpCodes.Ldarg_S,
        OpCodes.Ldarg
    ];

    internal static readonly HashSet<OpCode> opcodesStoringArgument =
    [
        OpCodes.Starg_S,
        OpCodes.Starg
    ];

    internal static readonly HashSet<OpCode> opcodesBranching =
    [
        OpCodes.Br_S,
        OpCodes.Brfalse_S,
        OpCodes.Brtrue_S,
        OpCodes.Beq_S,
        OpCodes.Bge_S,
        OpCodes.Bgt_S,
        OpCodes.Ble_S,
        OpCodes.Blt_S,
        OpCodes.Bne_Un_S,
        OpCodes.Bge_Un_S,
        OpCodes.Bgt_Un_S,
        OpCodes.Ble_Un_S,
        OpCodes.Blt_Un_S,
        OpCodes.Br,
        OpCodes.Brfalse,
        OpCodes.Brtrue,
        OpCodes.Beq,
        OpCodes.Bge,
        OpCodes.Bgt,
        OpCodes.Ble,
        OpCodes.Blt,
        OpCodes.Bne_Un,
        OpCodes.Bge_Un,
        OpCodes.Bgt_Un,
        OpCodes.Ble_Un,
        OpCodes.Blt_Un
    ];

    private static readonly HashSet<OpCode> constantLoadingCodes =
    [
        OpCodes.Ldc_I4_M1,
        OpCodes.Ldc_I4_0,
        OpCodes.Ldc_I4_1,
        OpCodes.Ldc_I4_2,
        OpCodes.Ldc_I4_3,
        OpCodes.Ldc_I4_4,
        OpCodes.Ldc_I4_5,
        OpCodes.Ldc_I4_6,
        OpCodes.Ldc_I4_7,
        OpCodes.Ldc_I4_8,
        OpCodes.Ldc_I4,
        OpCodes.Ldc_I4_S,
        OpCodes.Ldc_I8,
        OpCodes.Ldc_R4,
        OpCodes.Ldc_R8,
        OpCodes.Ldstr
    ];

    internal static int GetSize(this CodeInstruction instruction)
    {
        var size = instruction.opcode.Size;
        switch (instruction.opcode.OperandType)
        {
            case OperandType.InlineSwitch:
                size += (1 + ((Array)instruction.operand!).Length) * 4;
                break;

            case OperandType.InlineI8:
            case OperandType.InlineR:
                size += 8;
                break;

            case OperandType.InlineBrTarget:
            case OperandType.InlineField:
            case OperandType.InlineI:
            case OperandType.InlineMethod:
            case OperandType.InlineSig:
            case OperandType.InlineString:
            case OperandType.InlineTok:
            case OperandType.InlineType:
            case OperandType.ShortInlineR:
                size += 4;
                break;

            case OperandType.InlineVar:
                size += 2;
                break;

            case OperandType.ShortInlineBrTarget:
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar:
                size += 1;
                break;
        }

        return size;
    }

    /// <summary>Returns if an <see cref="OpCode"/> is initialized and valid</summary>
    public static bool IsValid(this OpCode code) => code.Size > 0;

    /// <summary>Shortcut for testing whether the operand is equal to a non-null value</summary>
    public static bool OperandIs(this CodeInstruction code, object value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));
        if (code.operand is null)
            return false;
        var type = value.GetType();
        var operandType = code.operand.GetType();
        if (AccessTools.IsInteger(type) && AccessTools.IsNumber(operandType))
            return Convert.ToInt64(code.operand) == Convert.ToInt64(value);
        if (AccessTools.IsFloatingPoint(type) && AccessTools.IsNumber(operandType))
            return Convert.ToDouble(code.operand) == Convert.ToDouble(value);
        return Equals(code.operand, value);
    }

    /// <summary>Shortcut for testing whether the operand is equal to a non-null <see cref="MemberInfo"/></summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool OperandIs(this CodeInstruction code, MemberInfo value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));
        return Equals(code.operand, value);
    }

    /// <summary>Shortcut for <c>code.opcode == opcode &amp;&amp; code.OperandIs(operand)</c></summary>
    public static bool Is(this CodeInstruction code, OpCode opcode, object operand) => code.opcode == opcode && code.OperandIs(operand);

    /// <summary>Shortcut for <c>code.opcode == opcode &amp;&amp; code.OperandIs(operand)</c></summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool Is(this CodeInstruction code, OpCode opcode, MemberInfo operand) => code.opcode == opcode && code.OperandIs(operand);

    /// <summary>Tests for any form of Ldarg*</summary>
    public static bool IsLdarg(this CodeInstruction code, int? n = null)
    {
        if ((n.HasValue is false || n.Value == 0) && code.opcode == OpCodes.Ldarg_0)
            return true;
        if ((n.HasValue is false || n.Value == 1) && code.opcode == OpCodes.Ldarg_1)
            return true;
        if ((n.HasValue is false || n.Value == 2) && code.opcode == OpCodes.Ldarg_2)
            return true;
        if ((n.HasValue is false || n.Value == 3) && code.opcode == OpCodes.Ldarg_3)
            return true;
        if (code.opcode == OpCodes.Ldarg && (n.HasValue is false || n.Value == Convert.ToInt32(code.operand)))
            return true;
        if (code.opcode == OpCodes.Ldarg_S && (n.HasValue is false || n.Value == Convert.ToInt32(code.operand)))
            return true;
        return false;
    }

    /// <summary>Tests for Ldarga/Ldarga_S</summary>
    public static bool IsLdarga(this CodeInstruction code, int? n = null)
    {
        if (code.opcode != OpCodes.Ldarga && code.opcode != OpCodes.Ldarga_S)
            return false;
        return n.HasValue is false || n.Value == Convert.ToInt32(code.operand);
    }

    /// <summary>Tests for Starg/Starg_S</summary>
    public static bool IsStarg(this CodeInstruction code, int? n = null)
    {
        if (code.opcode != OpCodes.Starg && code.opcode != OpCodes.Starg_S)
            return false;
        return n.HasValue is false || n.Value == Convert.ToInt32(code.operand);
    }

    /// <summary>Tests for any form of Ldloc*</summary>
    public static bool IsLdloc(this CodeInstruction code, LocalBuilder? variable = null)
    {
        if (opcodesLoadingLocalNormal.Contains(code.opcode) is false)
            if (opcodesLoadingLocalByAddress.Contains(code.opcode) is false)
                return false;
        return variable is null || Equals(variable, code.operand);
    }

    /// <summary>Tests for any form of Stloc*</summary>
    public static bool IsStloc(this CodeInstruction code, LocalBuilder? variable = null)
    {
        if (opcodesStoringLocal.Contains(code.opcode) is false)
            return false;
        return variable is null || Equals(variable, code.operand);
    }

    /// <summary>Tests if the code instruction branches</summary>
    public static bool Branches(this CodeInstruction code, out Label? label)
    {
        if (opcodesBranching.Contains(code.opcode))
        {
            label = (Label)code.operand!;
            return true;
        }

        label = null;
        return false;
    }

    /// <summary>Tests if the code instruction calls the method</summary>
    public static bool Calls(this CodeInstruction code, MethodInfo method)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        if (code.opcode != OpCodes.Call && code.opcode != OpCodes.Callvirt)
            return false;
        return Equals(code.operand, method);
    }

    /// <summary>Tests if the code instruction loads a constant</summary>
    public static bool LoadsConstant(this CodeInstruction code) => constantLoadingCodes.Contains(code.opcode);

    /// <summary>Tests if the code instruction loads an integer constant</summary>
    public static bool LoadsConstant(this CodeInstruction code, long number)
    {
        var op = code.opcode;
        if (number == -1 && op == OpCodes.Ldc_I4_M1)
            return true;
        if (number == 0 && op == OpCodes.Ldc_I4_0)
            return true;
        if (number == 1 && op == OpCodes.Ldc_I4_1)
            return true;
        if (number == 2 && op == OpCodes.Ldc_I4_2)
            return true;
        if (number == 3 && op == OpCodes.Ldc_I4_3)
            return true;
        if (number == 4 && op == OpCodes.Ldc_I4_4)
            return true;
        if (number == 5 && op == OpCodes.Ldc_I4_5)
            return true;
        if (number == 6 && op == OpCodes.Ldc_I4_6)
            return true;
        if (number == 7 && op == OpCodes.Ldc_I4_7)
            return true;
        if (number == 8 && op == OpCodes.Ldc_I4_8)
            return true;
        if (op != OpCodes.Ldc_I4 && op != OpCodes.Ldc_I4_S && op != OpCodes.Ldc_I8)
            return false;
        return Convert.ToInt64(code.operand) == number;
    }

    /// <summary>Tests if the code instruction loads a floating point constant</summary>
    public static bool LoadsConstant(this CodeInstruction code, double number)
    {
        if (code.opcode != OpCodes.Ldc_R4 && code.opcode != OpCodes.Ldc_R8)
            return false;
        var val = Convert.ToDouble(code.operand);
        return val == number;
    }

    /// <summary>Tests if the code instruction loads an enum constant</summary>
    public static bool LoadsConstant(this CodeInstruction code, Enum e) => code.LoadsConstant(Convert.ToInt64(e));

    /// <summary>Tests if the code instruction loads a string constant</summary>
    public static bool LoadsConstant(this CodeInstruction code, string str)
    {
        if (code.opcode != OpCodes.Ldstr)
            return false;
        var val = Convert.ToString(code.operand);
        return val == str;
    }

    /// <summary>Tests if the code instruction loads a field</summary>
    public static bool LoadsField(this CodeInstruction code, FieldInfo field, bool byAddress = false)
    {
        if (field is null)
            throw new ArgumentNullException(nameof(field));
        var ldfldCode = field.IsStatic ? OpCodes.Ldsfld : OpCodes.Ldfld;
        if (byAddress is false && code.opcode == ldfldCode && Equals(code.operand, field))
            return true;
        var ldfldaCode = field.IsStatic ? OpCodes.Ldsflda : OpCodes.Ldflda;
        if (byAddress is true && code.opcode == ldfldaCode && Equals(code.operand, field))
            return true;
        return false;
    }

    /// <summary>Tests if the code instruction stores a field</summary>
    public static bool StoresField(this CodeInstruction code, FieldInfo field)
    {
        if (field is null)
            throw new ArgumentNullException(nameof(field));
        var stfldCode = field.IsStatic ? OpCodes.Stsfld : OpCodes.Stfld;
        return code.opcode == stfldCode && Equals(code.operand, field);
    }

    /// <summary>Returns the index targeted by this <c>ldloc</c>, <c>ldloca</c>, or <c>stloc</c></summary>
    public static int LocalIndex(this CodeInstruction code)
    {
        if (code.opcode == OpCodes.Ldloc_0 || code.opcode == OpCodes.Stloc_0)
            return 0;
        else if (code.opcode == OpCodes.Ldloc_1 || code.opcode == OpCodes.Stloc_1)
            return 1;
        else if (code.opcode == OpCodes.Ldloc_2 || code.opcode == OpCodes.Stloc_2)
            return 2;
        else if (code.opcode == OpCodes.Ldloc_3 || code.opcode == OpCodes.Stloc_3)
            return 3;
        else if (code.opcode == OpCodes.Ldloc_S || code.opcode == OpCodes.Ldloc)
        {
            if (code.operand is LocalBuilder localBuilder)
                return localBuilder.LocalIndex;
            return Convert.ToInt32(code.operand);
        }
        else if (code.opcode == OpCodes.Stloc_S || code.opcode == OpCodes.Stloc)
        {
            if (code.operand is LocalBuilder localBuilder)
                return localBuilder.LocalIndex;
            return Convert.ToInt32(code.operand);
        }
        else if (code.opcode == OpCodes.Ldloca_S || code.opcode == OpCodes.Ldloca)
        {
            if (code.operand is LocalBuilder localBuilder)
                return localBuilder.LocalIndex;
            return Convert.ToInt32(code.operand);
        }
        else
            throw new ArgumentException("Instruction is not a load or store", nameof(code));
    }

    /// <summary>Returns the index targeted by this <c>ldarg</c>, <c>ldarga</c>, or <c>starg</c></summary>
    public static int ArgumentIndex(this CodeInstruction code)
    {
        if (code.opcode == OpCodes.Ldarg_0)
            return 0;
        else if (code.opcode == OpCodes.Ldarg_1)
            return 1;
        else if (code.opcode == OpCodes.Ldarg_2)
            return 2;
        else if (code.opcode == OpCodes.Ldarg_3)
            return 3;
        else if (code.opcode == OpCodes.Ldarg_S || code.opcode == OpCodes.Ldarg)
            return Convert.ToInt32(code.operand);
        else if (code.opcode == OpCodes.Starg_S || code.opcode == OpCodes.Starg)
            return Convert.ToInt32(code.operand);
        else if (code.opcode == OpCodes.Ldarga_S || code.opcode == OpCodes.Ldarga)
            return Convert.ToInt32(code.operand);
        else
            throw new ArgumentException("Instruction is not a load or store", nameof(code));
    }

    /// <summary>Adds labels to the code instruction and return it</summary>
    public static CodeInstruction WithLabels(this CodeInstruction code, params Label[] labels)
    {
        code.labels.AddRange(labels);
        return code;
    }

    /// <summary>Adds labels to the code instruction and return it</summary>
    public static CodeInstruction WithLabels(this CodeInstruction code, IEnumerable<Label> labels)
    {
        code.labels.AddRange(labels);
        return code;
    }

    /// <summary>Extracts all labels from the code instruction and returns them</summary>
    public static List<Label> ExtractLabels(this CodeInstruction code)
    {
        var labels = new List<Label>(code.labels);
        code.labels.Clear();
        return labels;
    }

    /// <summary>Moves all labels from the code instruction to another one</summary>
    public static CodeInstruction MoveLabelsTo(this CodeInstruction code, CodeInstruction other)
    {
        _ = other.WithLabels(code.ExtractLabels());
        return code;
    }

    /// <summary>Moves all labels from another code instruction to the current one</summary>
    public static CodeInstruction MoveLabelsFrom(this CodeInstruction code, CodeInstruction other) => code.WithLabels(other.ExtractLabels());

    /// <summary>Adds ExceptionBlocks to the code instruction and return it</summary>
    public static CodeInstruction WithBlocks(this CodeInstruction code, params ExceptionBlock[] blocks)
    {
        code.blocks.AddRange(blocks);
        return code;
    }

    /// <summary>Adds ExceptionBlocks to the code instruction and return it</summary>
    public static CodeInstruction WithBlocks(this CodeInstruction code, IEnumerable<ExceptionBlock> blocks)
    {
        code.blocks.AddRange(blocks);
        return code;
    }

    /// <summary>Extracts all ExceptionBlocks from the code instruction and returns them</summary>
    public static List<ExceptionBlock> ExtractBlocks(this CodeInstruction code)
    {
        var blocks = new List<ExceptionBlock>(code.blocks);
        code.blocks.Clear();
        return blocks;
    }

    /// <summary>Moves all ExceptionBlocks from the code instruction to another one</summary>
    public static CodeInstruction MoveBlocksTo(this CodeInstruction code, CodeInstruction other)
    {
        _ = other.WithBlocks(code.ExtractBlocks());
        return code;
    }

    /// <summary>Moves all ExceptionBlocks from another code instruction to the current one</summary>
    public static CodeInstruction MoveBlocksFrom(this CodeInstruction code, CodeInstruction other) => code.WithBlocks(other.ExtractBlocks());
}

/// <summary>General extensions for <see cref="MethodBase"/></summary>
public static class MethodBaseExtensions
{
    /// <summary>Tests a class member if it has an IL method body (external methods for example don't have a body)</summary>
    public static bool HasMethodBody(this MethodBase member) => (member.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0) > 0;
}

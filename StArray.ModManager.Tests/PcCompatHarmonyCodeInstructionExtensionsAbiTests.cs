using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace StArray.ModManager.Tests;

/// <summary>
/// Covers <c>CodeInstructionExtensions</c> and <c>MethodBaseExtensions</c> of the 0Harmony shim ABI.
///
/// These 32 extension methods are the vocabulary a transpiler is written in, and a MOD references them
/// from its own code long before any transpiler would run. They are pure predicates over a
/// CodeInstruction plus label/exception-block bookkeeping - no IL is read or emitted - so the shim
/// mirrors upstream exactly, including which shapes throw and which numeric widenings compare equal.
/// </summary>
public class PcCompatHarmonyCodeInstructionExtensionsAbiTests
{
    private enum Flavour
    {
        Third = 3
    }

    private static readonly MethodInfo mBar =
        SymbolExtensions.GetMethodInfo(() => Bar(""));

    private static readonly FieldInfo fStatic = typeof(Holder).GetField(nameof(Holder.StaticField))!;
    private static readonly FieldInfo fInstance = typeof(Holder).GetField(nameof(Holder.InstanceField))!;

    private class Holder
    {
        public static int StaticField = 1;
        public int InstanceField = 2;
    }

    private static void Bar(string value) => _ = value;

    [Test]
    public void IsValidSeparatesInitializedOpcodesFromDefaults()
    {
        Assert.Multiple(() =>
        {
            Assert.That(OpCodes.Nop.IsValid(), Is.True);
            Assert.That(OpCodes.Call.IsValid(), Is.True);
            Assert.That(default(OpCode).IsValid(), Is.False);
        });
    }

    [Test]
    public void OperandIsWidensNumbersBeforeComparing()
    {
        var five = new CodeInstruction(OpCodes.Ldc_I4, 5);

        Assert.Multiple(() =>
        {
            Assert.That(five.OperandIs(5L), Is.True);
            Assert.That(five.OperandIs((short)5), Is.True);
            Assert.That(five.OperandIs(5.0), Is.True);
            Assert.That(five.OperandIs(6L), Is.False);
            Assert.That(new CodeInstruction(OpCodes.Ldstr, "x").OperandIs("x"), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Nop).OperandIs("x"), Is.False);
            Assert.That(new CodeInstruction(OpCodes.Call, mBar).OperandIs(mBar), Is.True);
            Assert.Throws<ArgumentNullException>(() => five.OperandIs((object)null!));
            Assert.Throws<ArgumentNullException>(() => five.OperandIs((MemberInfo)null!));
        });
    }

    [Test]
    public void IsCombinesOpcodeAndOperand()
    {
        var call = new CodeInstruction(OpCodes.Call, mBar);

        Assert.Multiple(() =>
        {
            Assert.That(call.Is(OpCodes.Call, mBar), Is.True);
            Assert.That(call.Is(OpCodes.Callvirt, mBar), Is.False);
            Assert.That(new CodeInstruction(OpCodes.Ldc_I4, 5).Is(OpCodes.Ldc_I4, (object)5L), Is.True);
        });
    }

    [Test]
    public void ArgumentPredicatesCoverShortAndLongForms()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new CodeInstruction(OpCodes.Ldarg_0).IsLdarg(), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Ldarg_0).IsLdarg(0), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Ldarg_0).IsLdarg(1), Is.False);
            Assert.That(new CodeInstruction(OpCodes.Ldarg_3).IsLdarg(3), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Ldarg_S, (byte)7).IsLdarg(7), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Ldarg, (short)9).IsLdarg(9), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Ldloc_0).IsLdarg(), Is.False);

            Assert.That(new CodeInstruction(OpCodes.Ldarga_S, (byte)2).IsLdarga(2), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Ldarga, (short)2).IsLdarga(), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Ldarg_0).IsLdarga(), Is.False);

            Assert.That(new CodeInstruction(OpCodes.Starg_S, (byte)4).IsStarg(4), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Starg, (short)4).IsStarg(5), Is.False);
            Assert.That(new CodeInstruction(OpCodes.Ldarg_0).IsStarg(), Is.False);
        });
    }

    [Test]
    public void LocalPredicatesCoverValueAndAddressForms()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new CodeInstruction(OpCodes.Ldloc_2).IsLdloc(), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Ldloc_S, (byte)5).IsLdloc(), Is.True);
            // Address loads count as loads for IsLdloc, but not as stores.
            Assert.That(new CodeInstruction(OpCodes.Ldloca_S, (byte)5).IsLdloc(), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Stloc_1).IsLdloc(), Is.False);

            Assert.That(new CodeInstruction(OpCodes.Stloc_1).IsStloc(), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Stloc, (short)6).IsStloc(), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Ldloc_1).IsStloc(), Is.False);
        });
    }

    [Test]
    public void BranchesReportsTheTargetLabel()
    {
        var target = new Label();

        Assert.Multiple(() =>
        {
            Assert.That(new CodeInstruction(OpCodes.Br, target).Branches(out var branched), Is.True);
            Assert.That(branched, Is.EqualTo(target));
            Assert.That(new CodeInstruction(OpCodes.Brtrue_S, target).Branches(out _), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Nop).Branches(out var none), Is.False);
            Assert.That(none, Is.Null);
        });
    }

    [Test]
    public void CallsMatchesCallAndCallvirtOnly()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new CodeInstruction(OpCodes.Call, mBar).Calls(mBar), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Callvirt, mBar).Calls(mBar), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Newobj, mBar).Calls(mBar), Is.False);
            Assert.Throws<ArgumentNullException>(() => new CodeInstruction(OpCodes.Call, mBar).Calls(null!));
        });
    }

    [Test]
    public void LoadsConstantCoversEveryConstantForm()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new CodeInstruction(OpCodes.Ldc_I4_3).LoadsConstant(), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Ldstr, "s").LoadsConstant(), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Nop).LoadsConstant(), Is.False);

            Assert.That(new CodeInstruction(OpCodes.Ldc_I4_M1).LoadsConstant(-1L), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Ldc_I4_8).LoadsConstant(8L), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Ldc_I4_8).LoadsConstant(7L), Is.False);
            Assert.That(new CodeInstruction(OpCodes.Ldc_I4, 42).LoadsConstant(42L), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Ldc_I8, 42L).LoadsConstant(42L), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Ldstr, "42").LoadsConstant(42L), Is.False);

            Assert.That(new CodeInstruction(OpCodes.Ldc_R4, 1.5f).LoadsConstant(1.5), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Ldc_R8, 1.5).LoadsConstant(1.5), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Ldc_I4, 1).LoadsConstant(1.0), Is.False);

            Assert.That(new CodeInstruction(OpCodes.Ldc_I4_3).LoadsConstant(Flavour.Third), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Ldc_I4_2).LoadsConstant(Flavour.Third), Is.False);

            Assert.That(new CodeInstruction(OpCodes.Ldstr, "abc").LoadsConstant("abc"), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Ldstr, "abc").LoadsConstant("xyz"), Is.False);
        });
    }

    [Test]
    public void FieldPredicatesPickTheRightOpcodeForStaticAndInstanceFields()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new CodeInstruction(OpCodes.Ldsfld, fStatic).LoadsField(fStatic), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Ldsflda, fStatic).LoadsField(fStatic, byAddress: true), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Ldsflda, fStatic).LoadsField(fStatic), Is.False);
            Assert.That(new CodeInstruction(OpCodes.Ldfld, fInstance).LoadsField(fInstance), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Ldflda, fInstance).LoadsField(fInstance, byAddress: true), Is.True);

            Assert.That(new CodeInstruction(OpCodes.Stsfld, fStatic).StoresField(fStatic), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Stfld, fInstance).StoresField(fInstance), Is.True);
            Assert.That(new CodeInstruction(OpCodes.Stsfld, fStatic).StoresField(fInstance), Is.False);

            Assert.Throws<ArgumentNullException>(() => new CodeInstruction(OpCodes.Ldsfld, fStatic).LoadsField(null!));
            Assert.Throws<ArgumentNullException>(() => new CodeInstruction(OpCodes.Stsfld, fStatic).StoresField(null!));
        });
    }

    [Test]
    public void LocalIndexAndArgumentIndexDecodeEveryForm()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new CodeInstruction(OpCodes.Ldloc_0).LocalIndex(), Is.EqualTo(0));
            Assert.That(new CodeInstruction(OpCodes.Stloc_3).LocalIndex(), Is.EqualTo(3));
            Assert.That(new CodeInstruction(OpCodes.Ldloc_S, (byte)11).LocalIndex(), Is.EqualTo(11));
            Assert.That(new CodeInstruction(OpCodes.Stloc, (short)12).LocalIndex(), Is.EqualTo(12));
            Assert.That(new CodeInstruction(OpCodes.Ldloca_S, (byte)13).LocalIndex(), Is.EqualTo(13));
            Assert.Throws<ArgumentException>(() => new CodeInstruction(OpCodes.Nop).LocalIndex());

            Assert.That(new CodeInstruction(OpCodes.Ldarg_2).ArgumentIndex(), Is.EqualTo(2));
            Assert.That(new CodeInstruction(OpCodes.Ldarg_S, (byte)21).ArgumentIndex(), Is.EqualTo(21));
            Assert.That(new CodeInstruction(OpCodes.Starg, (short)22).ArgumentIndex(), Is.EqualTo(22));
            Assert.That(new CodeInstruction(OpCodes.Ldarga_S, (byte)23).ArgumentIndex(), Is.EqualTo(23));
            Assert.Throws<ArgumentException>(() => new CodeInstruction(OpCodes.Nop).ArgumentIndex());
        });
    }

    [Test]
    public void LabelHelpersMoveOwnershipInTheDocumentedDirection()
    {
        var source = new CodeInstruction(OpCodes.Nop).WithLabels(new Label(), new Label());
        var target = new CodeInstruction(OpCodes.Ret);

        var returnedFromMoveTo = source.MoveLabelsTo(target);

        Assert.Multiple(() =>
        {
            // MoveLabelsTo returns the source it emptied; MoveLabelsFrom returns the receiver it filled.
            Assert.That(returnedFromMoveTo, Is.SameAs(source));
            Assert.That(source.labels, Is.Empty);
            Assert.That(target.labels, Has.Count.EqualTo(2));

            var back = source.MoveLabelsFrom(target);
            Assert.That(back, Is.SameAs(source));
            Assert.That(source.labels, Has.Count.EqualTo(2));
            Assert.That(target.labels, Is.Empty);

            var extracted = source.ExtractLabels();
            Assert.That(extracted, Has.Count.EqualTo(2));
            Assert.That(source.labels, Is.Empty);

            Assert.That(source.WithLabels(extracted.AsEnumerable()).labels, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void BlockHelpersMirrorTheLabelHelpers()
    {
        var source = new CodeInstruction(OpCodes.Nop)
            .WithBlocks(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
        var target = new CodeInstruction(OpCodes.Ret);

        var returnedFromMoveTo = source.MoveBlocksTo(target);

        Assert.Multiple(() =>
        {
            Assert.That(returnedFromMoveTo, Is.SameAs(source));
            Assert.That(source.blocks, Is.Empty);
            Assert.That(target.blocks, Has.Count.EqualTo(1));
            Assert.That(target.HasBlock(ExceptionBlockType.BeginExceptionBlock), Is.True);

            var back = source.MoveBlocksFrom(target);
            Assert.That(back, Is.SameAs(source));
            Assert.That(source.blocks, Has.Count.EqualTo(1));

            var extracted = source.ExtractBlocks();
            Assert.That(extracted, Has.Count.EqualTo(1));
            Assert.That(source.blocks, Is.Empty);
            Assert.That(source.WithBlocks(extracted.AsEnumerable()).blocks, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void HasMethodBodySeparatesIlMethodsFromAbstractOnes()
    {
        var concrete = typeof(PcCompatHarmonyCodeInstructionExtensionsAbiTests)
            .GetMethod(nameof(Bar), BindingFlags.NonPublic | BindingFlags.Static)!;
        var abstractMethod = typeof(Stream).GetMethod(
            nameof(Stream.Read), [typeof(byte[]), typeof(int), typeof(int)])!;

        Assert.Multiple(() =>
        {
            Assert.That(concrete.HasMethodBody(), Is.True);
            Assert.That(abstractMethod.HasMethodBody(), Is.False);
        });
    }
}

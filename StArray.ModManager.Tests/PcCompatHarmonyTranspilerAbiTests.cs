using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace StArray.ModManager.Tests;

/// <summary>
/// Covers the transpiler-adjacent slice of the 0Harmony shim ABI.
///
/// Every member here was found missing by compiling the upstream HarmonyTests patch assets against
/// the shim: <c>Transpilers</c> and the expression-based <c>CodeInstruction.Call</c> family were
/// simply absent, so a MOD assembly using them failed to load. The upstream corpus lives outside the
/// repository and cannot be a permanent test dependency, so the shapes it exercised are pinned here.
///
/// None of this rewrites IL - the host cannot patch IL2CPP native code. These helpers only transform
/// the CodeInstruction list a MOD hands in, which MODs do call directly from their own code.
/// </summary>
public class PcCompatHarmonyTranspilerAbiTests
{
    private static string Original(string value) => value;

    private static string Replacement(string value) => value;

    private static void Marker() { }

    // Public so the compiler does not flag it as never assigned: it exists only to be found by name.
    public static int field;

    private static readonly MethodInfo OriginalMethod =
        typeof(PcCompatHarmonyTranspilerAbiTests).GetMethod(nameof(Original), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo ReplacementMethod =
        typeof(PcCompatHarmonyTranspilerAbiTests).GetMethod(nameof(Replacement), BindingFlags.NonPublic | BindingFlags.Static)!;

    [Test]
    public void MethodReplacerSwapsOnlyTheMatchingOperand()
    {
        var untouched = new CodeInstruction(OpCodes.Call, ReplacementMethod);
        var input = new[]
        {
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call, OriginalMethod),
            untouched
        };

        var result = input.MethodReplacer(OriginalMethod, ReplacementMethod).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(result[0].opcode, Is.EqualTo(OpCodes.Ldarg_0));
            Assert.That(result[1].operand, Is.SameAs(ReplacementMethod));
            Assert.That(result[1].opcode, Is.EqualTo(OpCodes.Call));
            // A constructor target switches the opcode to newobj; a plain method must not.
            Assert.That(result[2], Is.SameAs(untouched));
        });
    }

    [Test]
    public void MethodReplacerEmitsNewobjForAConstructorTarget()
    {
        var constructor = typeof(object).GetConstructor(Type.EmptyTypes)!;
        var input = new[] { new CodeInstruction(OpCodes.Call, OriginalMethod) };

        var result = input.MethodReplacer(OriginalMethod, constructor).ToArray();

        Assert.That(result[0].opcode, Is.EqualTo(OpCodes.Newobj));
    }

    [Test]
    public void ManipulatorAppliesTheActionOnlyToMatchingInstructions()
    {
        var input = new[]
        {
            new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Ldarg_2)
        };

        var result = input
            .Manipulator(item => item.opcode == OpCodes.Ldarg_1, item => item.opcode = OpCodes.Ldarg_0)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(result[0].opcode, Is.EqualTo(OpCodes.Ldarg_0));
            Assert.That(result[1].opcode, Is.EqualTo(OpCodes.Ldarg_2));
        });
    }

    [Test]
    public void DebugLoggerPrependsTheLogCallAndKeepsTheBody()
    {
        var body = new[] { new CodeInstruction(OpCodes.Ret) };

        var result = body.DebugLogger("hello").ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(result, Has.Length.EqualTo(3));
            Assert.That(result[0].opcode, Is.EqualTo(OpCodes.Ldstr));
            Assert.That(result[0].operand, Is.EqualTo("hello"));
            Assert.That(result[1].operand, Is.EqualTo(AccessTools.Method(typeof(FileLog), nameof(FileLog.Debug))));
            Assert.That(result[2].opcode, Is.EqualTo(OpCodes.Ret));
        });
    }

    [Test]
    public void CallFromAnExpressionResolvesTheMethodWithoutAStringLiteral()
    {
        var fromAction = CodeInstruction.Call(() => Marker());
        var fromInstance = CodeInstruction.Call<string>(text => text.Trim());
        var fromFunc = CodeInstruction.Call<string, int>(text => text.IndexOf('a'));
        var fromLambda = CodeInstruction.Call((System.Linq.Expressions.LambdaExpression)(() => Marker()));

        Assert.Multiple(() =>
        {
            Assert.That(fromAction.opcode, Is.EqualTo(OpCodes.Call));
            Assert.That(fromAction.operand, Is.EqualTo(
                typeof(PcCompatHarmonyTranspilerAbiTests).GetMethod(nameof(Marker), BindingFlags.NonPublic | BindingFlags.Static)));
            Assert.That(fromInstance.operand, Is.EqualTo(typeof(string).GetMethod(nameof(string.Trim), Type.EmptyTypes)));
            Assert.That(fromFunc.operand, Is.EqualTo(typeof(string).GetMethod(nameof(string.IndexOf), [typeof(char)])));
            // A bare property read is a MemberExpression, not a call, and upstream rejects it too.
            Assert.Throws<ArgumentException>(() => CodeInstruction.Call<string, int>(text => text.Length));
            Assert.That(fromLambda.operand, Is.EqualTo(fromAction.operand));
        });
    }

    [Test]
    public void SymbolExtensionsRejectsShapesThatAreNotASingleMemberAccess()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SymbolExtensions.GetFieldInfo(() => field)!.Name, Is.EqualTo(nameof(field)));
            Assert.Throws<ArgumentException>(() => SymbolExtensions.GetMethodInfo(() => new object()));
            Assert.Throws<ArgumentException>(() => SymbolExtensions.GetFieldInfo(() => Original("x")));
        });
    }

    [Test]
    public void CallClosureTakesAStaticMethodAndRefusesToSilentlyDropCapturedState()
    {
        var instruction = CodeInstruction.CallClosure((Action)Marker);
        Assert.That(instruction.opcode, Is.EqualTo(OpCodes.Call));
        Assert.That(instruction.operand, Is.EqualTo(
            typeof(PcCompatHarmonyTranspilerAbiTests).GetMethod(nameof(Marker), BindingFlags.NonPublic | BindingFlags.Static)));

        // Upstream builds a DynamicMethodDefinition for this; without runtime IL emission the only
        // honest answer is to fail, because an instruction that drops the capture would be wrong.
        var captured = 1;
        var exception = Assert.Throws<NotSupportedException>(
            () => CodeInstruction.CallClosure((Action)(() => Assert.That(captured, Is.EqualTo(1)))));
        Assert.That(exception!.Message, Does.Contain("runtime IL emission"));
    }
}

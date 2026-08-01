using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using static HarmonyLib.Code;

namespace StArray.ModManager.Tests;

/// <summary>
/// Covers the CodeMatcher/CodeMatch/Code slice of the 0Harmony shim ABI.
///
/// The whole family was absent until now, which is not a degraded transpiler but a hard load failure:
/// a MOD assembly that references CodeMatcher anywhere - even in a transpiler that never runs on this
/// host - throws TypeLoadException and takes its prefixes and postfixes down with it.
///
/// The instruction fixture below reproduces the one upstream's Test_CodeMatcher builds by reading the
/// IL of a sample method. Reading IL is exactly what this host cannot do, so the same 21 instructions
/// are constructed by hand and the upstream position/length expectations are asserted verbatim - that
/// makes the shim's cursor arithmetic diffable against a real Harmony run.
/// </summary>
public class PcCompatHarmonyCodeMatcherAbiTests
{
    private class Sample
    {
        public static int StaticField = 1;
        public static int OtherStaticField = 2;
        public int InstanceField = 3;

        public static void Foo() { }

        public static void Bar(string value) => _ = value;
    }

    private static readonly MethodInfo mFoo = SymbolExtensions.GetMethodInfo(() => Sample.Foo());
    private static readonly MethodInfo mBar = SymbolExtensions.GetMethodInfo(() => Sample.Bar(""));
    private static readonly ConstructorInfo cObject = typeof(object).GetConstructor(Type.EmptyTypes)!;
    private static readonly ConstructorInfo cSample = typeof(Sample).GetConstructor(Type.EmptyTypes)!;
    private static readonly FieldInfo fStatic = typeof(Sample).GetField(nameof(Sample.StaticField))!;
    private static readonly FieldInfo fOtherStatic = typeof(Sample).GetField(nameof(Sample.OtherStaticField))!;
    private static readonly FieldInfo fInstance = typeof(Sample).GetField(nameof(Sample.InstanceField))!;

    // 00: CALL  Foo()          10: CALL  Foo()
    // 01: LDSTR "A"            11: LDSTR "E"
    // 02: CALL  Bar(String)    12: CALL  Bar(String)
    // 03: LDSTR "B"            13: LDSTR "F"
    // 04: CALL  Bar(String)    14: CALL  Bar(String)
    // 05: LDSTR "C"            15: LDSTR "G"
    // 06: CALL  Bar(String)    16: CALL  Bar(String)
    // 07: CALL  Foo()          17: LDSTR "H"
    // 08: LDSTR "D"            18: CALL  Bar(String)
    // 09: CALL  Bar(String)    19: CALL  Foo()
    //                          20: RET
    private const int InstructionCount = 21;

    private static List<CodeInstruction> Instructions =>
    [
        new(OpCodes.Call, mFoo),
        new(OpCodes.Ldstr, "A"),
        new(OpCodes.Call, mBar),
        new(OpCodes.Ldstr, "B"),
        new(OpCodes.Call, mBar),
        new(OpCodes.Ldstr, "C"),
        new(OpCodes.Call, mBar),
        new(OpCodes.Call, mFoo),
        new(OpCodes.Ldstr, "D"),
        new(OpCodes.Call, mBar),
        new(OpCodes.Call, mFoo),
        new(OpCodes.Ldstr, "E"),
        new(OpCodes.Call, mBar),
        new(OpCodes.Ldstr, "F"),
        new(OpCodes.Call, mBar),
        new(OpCodes.Ldstr, "G"),
        new(OpCodes.Call, mBar),
        new(OpCodes.Ldstr, "H"),
        new(OpCodes.Call, mBar),
        new(OpCodes.Call, mFoo),
        new(OpCodes.Ret)
    ];

    private static bool Matches(CodeInstruction instruction, CodeMatch match)
        => new CodeMatcher([instruction]).MatchStartForward(match).IsValid;

    [Test]
    public void CodeMatchCarriesOpcodeOperandAndName()
    {
        var match = new CodeMatch(OpCodes.Call, mBar, "something");

        Assert.Multiple(() =>
        {
            Assert.That(match.opcode, Is.EqualTo(OpCodes.Call));
            Assert.That(match.opcodeSet, Is.EqualTo(new HashSet<OpCode> { OpCodes.Call }));
            Assert.That(match.operand, Is.EqualTo(mBar));
            Assert.That(match.operands, Is.EqualTo(new[] { mBar }));
            Assert.That(match.name, Is.EqualTo("something"));
            Assert.That(match.ToString(), Does.Contain("something").And.Contain("opcodes="));
        });
    }

    [Test]
    public void CallsWithANullMethodStillMatchesCallAndCallvirt()
    {
        var match = CodeMatch.Calls((MethodInfo)null!);

        Assert.Multiple(() =>
        {
            Assert.That(Matches(new CodeInstruction(OpCodes.Call, mFoo), match), Is.True);
            Assert.That(Matches(new CodeInstruction(OpCodes.Callvirt, mFoo), match), Is.True);
            Assert.That(Matches(new CodeInstruction(OpCodes.Call, cObject), match), Is.True);
            Assert.That(Matches(new CodeInstruction(OpCodes.Newobj, cObject), match), Is.False);
            // The instruction-level overload keeps rejecting null - only the matcher tolerates it.
            Assert.Throws<ArgumentNullException>(() => new CodeInstruction(OpCodes.Call, mFoo).Calls(null!));
        });
    }

    [Test]
    public void CallsBindsToTheConstructorOverloadAndAcceptsNewobj()
    {
        var match = CodeMatch.Calls(cObject);
        var anyConstructor = CodeMatch.Calls((ConstructorInfo?)null);

        Assert.Multiple(() =>
        {
            Assert.That(Matches(new CodeInstruction(OpCodes.Newobj, cObject), match), Is.True);
            Assert.That(Matches(new CodeInstruction(OpCodes.Call, cObject), match), Is.True);
            Assert.That(Matches(new CodeInstruction(OpCodes.Callvirt, cObject), match), Is.False);
            Assert.That(Matches(new CodeInstruction(OpCodes.Newobj, cSample), match), Is.False);
            Assert.That(Matches(new CodeInstruction(OpCodes.Call, mFoo), match), Is.False);
            Assert.That(Matches(new CodeInstruction(OpCodes.Newobj), match), Is.False);

            Assert.That(Matches(new CodeInstruction(OpCodes.Newobj, cSample), anyConstructor), Is.True);
            Assert.That(Matches(new CodeInstruction(OpCodes.Newobj, mFoo), anyConstructor), Is.False);
            Assert.That(Matches(new CodeInstruction(OpCodes.Newobj), anyConstructor), Is.False);
        });
    }

    [Test]
    public void FieldMatchesDistinguishValueAndAddressLoads()
    {
        var instance = new Sample();
        var staticMatch = CodeMatch.LoadsField(() => Sample.StaticField);
        var instanceMatch = CodeMatch.LoadsField(() => instance.InstanceField);
        var staticAddressMatch = CodeMatch.LoadsField(() => Sample.StaticField, byAddress: true);
        var instanceAddressMatch = CodeMatch.LoadsField(() => default(Sample)!.InstanceField, byAddress: true);
        var storesStatic = CodeMatch.StoresField(() => Sample.StaticField);
        var storesInstance = CodeMatch.StoresField(() => instance.InstanceField);

        Assert.Multiple(() =>
        {
            Assert.That(Matches(new CodeInstruction(OpCodes.Ldsfld, fStatic), staticMatch), Is.True);
            Assert.That(Matches(new CodeInstruction(OpCodes.Ldsflda, fStatic), staticMatch), Is.False);
            Assert.That(Matches(new CodeInstruction(OpCodes.Ldsfld, fOtherStatic), staticMatch), Is.False);
            Assert.That(Matches(new CodeInstruction(OpCodes.Stsfld, fStatic), staticMatch), Is.False);

            Assert.That(Matches(new CodeInstruction(OpCodes.Ldfld, fInstance), instanceMatch), Is.True);
            Assert.That(Matches(new CodeInstruction(OpCodes.Ldflda, fInstance), instanceMatch), Is.False);

            Assert.That(Matches(new CodeInstruction(OpCodes.Ldsflda, fStatic), staticAddressMatch), Is.True);
            Assert.That(Matches(new CodeInstruction(OpCodes.Ldsfld, fStatic), staticAddressMatch), Is.False);
            Assert.That(Matches(new CodeInstruction(OpCodes.Ldflda, fInstance), instanceAddressMatch), Is.True);
            Assert.That(Matches(new CodeInstruction(OpCodes.Ldfld, fInstance), instanceAddressMatch), Is.False);

            Assert.That(Matches(new CodeInstruction(OpCodes.Stsfld, fStatic), storesStatic), Is.True);
            Assert.That(Matches(new CodeInstruction(OpCodes.Stsfld, fOtherStatic), storesStatic), Is.False);
            Assert.That(Matches(new CodeInstruction(OpCodes.Stfld, fInstance), storesInstance), Is.True);
            Assert.That(Matches(new CodeInstruction(OpCodes.Ldfld, fInstance), storesInstance), Is.False);

            // A null literal must still bind to the FieldInfo overload. Construction is lazy - the
            // predicate only runs once something is matched against it, and that is where it throws.
            var nullLoad = CodeMatch.LoadsField(null!);
            var nullStore = CodeMatch.StoresField(null!);
            Assert.Throws<ArgumentNullException>(
                () => Matches(new CodeInstruction(OpCodes.Ldsfld, fStatic), nullLoad));
            Assert.Throws<ArgumentNullException>(
                () => Matches(new CodeInstruction(OpCodes.Stsfld, fStatic), nullStore));
        });
    }

    [Test]
    public void TheCodeShorthandBuildsMatchesWithAndWithoutAnOperand()
    {
        var bare = Ldc_I4_0;
        var withOperand = Call[mBar];
        var named = Ldstr["D", "the-d"];

        Assert.Multiple(() =>
        {
            Assert.That(bare.opcode, Is.EqualTo(OpCodes.Ldc_I4_0));
            Assert.That(bare.opcodeSet, Is.EqualTo(new HashSet<OpCode> { OpCodes.Ldc_I4_0 }));
            Assert.That(bare.operand, Is.Null);
            Assert.That(bare.operands, Is.Empty);

            Assert.That(withOperand.opcode, Is.EqualTo(OpCodes.Call));
            Assert.That(withOperand.opcodeSet, Is.EqualTo(new HashSet<OpCode> { OpCodes.Call }));
            Assert.That(withOperand.operand, Is.EqualTo(mBar));
            Assert.That(withOperand.operands, Is.EqualTo(new[] { mBar }));

            Assert.That(named.name, Is.EqualTo("the-d"));
            Assert.That(Operand["D"].operand, Is.EqualTo("D"));
        });
    }

    [Test]
    public void AdvanceStartAndEndTrackBounds()
    {
        var matcher = new CodeMatcher(Instructions)
            .Start()
            .Do(m => Assert.That(m.Pos, Is.EqualTo(0)))
            .Advance(2)
            .Do(m => Assert.That(m.Pos, Is.EqualTo(2)))
            .End()
            .Do(m => Assert.That(m.Pos, Is.EqualTo(20)))
            .Advance(1);

        Assert.Multiple(() =>
        {
            Assert.That(matcher.IsInvalid, Is.True);
            Assert.That(matcher.Length, Is.EqualTo(InstructionCount));
            Assert.That(matcher.Remaining, Is.EqualTo(0));
        });
    }

    [Test]
    public void MatchStartForwardStopsAtTheStartOfTheSequence()
    {
        var instructions = Instructions;
        _ = new CodeMatcher(instructions)
            .MatchStartForward(Call[mBar], Call[mFoo])
            .ThrowIfNotMatch("not found")
            .Do(m => Assert.Multiple(() =>
            {
                Assert.That(m.Instruction.Is(OpCodes.Call, mBar), Is.True);
                Assert.That(m.Pos, Is.EqualTo(6));
            }));
    }

    [Test]
    public void MatchEndForwardStopsAtTheEndOfTheSequence()
    {
        _ = new CodeMatcher(Instructions)
            .MatchEndForward(Call[mBar], Call[mFoo])
            .ThrowIfNotMatch("not found")
            .Do(m => Assert.Multiple(() =>
            {
                Assert.That(m.Instruction.Is(OpCodes.Call, mFoo), Is.True);
                Assert.That(m.Pos, Is.EqualTo(7));
            }));
    }

    [Test]
    public void RepeatWalksEveryOccurrence()
    {
        var expectedPositions = new[] { 6, 9, 18 };
        var count = 0;
        var notFound = 0;

        _ = new CodeMatcher(Instructions)
            .Start()
            .MatchStartForward(Call[mBar], Call[mFoo])
            .Repeat(
                m =>
                {
                    Assert.That(m.Pos, Is.EqualTo(expectedPositions[count++]));
                    Assert.That(m.Instruction.Is(OpCodes.Call, mBar), Is.True);
                    _ = m.Advance(2);
                },
                _ => notFound++);

        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(3));
            Assert.That(notFound, Is.EqualTo(0));
        });
    }

    [Test]
    public void PrepareMatchDefersTheSearchUntilRepeatRuns()
    {
        var expectedPositions = new[] { 0, 6, 9, 18 };
        var count = 0;
        var notFound = 0;

        _ = new CodeMatcher(Instructions)
            .Start()
            .PrepareMatchStartForward(Call[mBar], Call[mFoo])
            .Repeat(
                m =>
                {
                    Assert.That(m.Pos, Is.EqualTo(expectedPositions[count++]));
                    _ = m.Advance(2);
                },
                _ => notFound++);

        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(4));
            Assert.That(notFound, Is.EqualTo(0));
        });
    }

    [Test]
    public void RepeatWithoutAPreviousMatchThrows()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new CodeMatcher(Instructions).Repeat(_ => { }));
        Assert.That(exception!.Message, Is.EqualTo("No previous Match operation - cannot repeat"));
    }

    [Test]
    public void SearchMovesForwardAndBackwards()
    {
        _ = new CodeMatcher(Instructions)
            .SearchForward(ci => ci.OperandIs("D"))
            .Do(m => Assert.That(m.Pos, Is.EqualTo(8)))
            .SearchBackwards(ci => ci.opcode == OpCodes.Ldstr)
            .Do(m => Assert.That(m.Pos, Is.EqualTo(8)))
            .SearchBackwards(ci => ci.OperandIs("C"))
            .Do(m => Assert.That(m.Pos, Is.EqualTo(5)));
    }

    [Test]
    public void RemoveSearchForwardStopsBeforeTheMatch()
    {
        _ = new CodeMatcher(Instructions)
            .SearchForward(ci => ci.OperandIs("D"))
            .RemoveSearchForward(ci => ci.OperandIs("F"))
            .Do(m => Assert.Multiple(() =>
            {
                Assert.That(m.Pos, Is.EqualTo(8));
                Assert.That(m.Length, Is.EqualTo(InstructionCount - 5));
                Assert.That(m.Operand, Is.EqualTo("F"));
            }));
    }

    [Test]
    public void RemoveSearchBackwardStopsAfterTheMatch()
    {
        _ = new CodeMatcher(Instructions)
            .SearchForward(ci => ci.OperandIs("F"))
            .RemoveSearchBackward(ci => ci.OperandIs("D"))
            .Do(m => Assert.Multiple(() =>
            {
                Assert.That(m.Pos, Is.EqualTo(8));
                Assert.That(m.Length, Is.EqualTo(InstructionCount - 5));
                Assert.That(m.Operand, Is.EqualTo("D"));
            }));
    }

    [Test]
    public void RemoveSearchGoesOutOfBoundsWhenNothingMatches()
    {
        _ = new CodeMatcher(Instructions)
            .Do(m => Assert.That(m.IsInvalid, Is.True))
            .SearchForward(ci => ci.OperandIs("D"))
            .Do(m => Assert.That(m.IsValid, Is.True))
            .RemoveSearchForward(ci => ci.OperandIs("X"))
            .Do(m => Assert.That(m.IsInvalid, Is.True));

        _ = new CodeMatcher(Instructions)
            .SearchForward(ci => ci.OperandIs("F"))
            .Do(m => Assert.That(m.IsValid, Is.True))
            .RemoveSearchBackward(ci => ci.OperandIs("X"))
            .Do(m => Assert.That(m.IsInvalid, Is.True));
    }

    [Test]
    public void RemoveUntilForwardKeepsTheMatch()
    {
        _ = new CodeMatcher(Instructions)
            .Start()
            .RemoveUntilForward(Call[mBar], Call[mFoo])
            .Do(m => Assert.Multiple(() =>
            {
                Assert.That(m.Pos, Is.EqualTo(0));
                Assert.That(m.Length, Is.EqualTo(InstructionCount - 6));
                Assert.That(m.Operand, Is.EqualTo(mBar));
            }));
    }

    [Test]
    public void RemoveUntilBackwardKeepsTheMatch()
    {
        _ = new CodeMatcher(Instructions)
            .End()
            .RemoveUntilBackward(Call[mBar], Ldstr["H"])
            .Do(m => Assert.Multiple(() =>
            {
                Assert.That(m.Pos, Is.EqualTo(17));
                Assert.That(m.Length, Is.EqualTo(InstructionCount - 3));
                Assert.That(m.Operand, Is.EqualTo("H"));
            }));
    }

    [Test]
    public void RemoveUntilGoesOutOfBoundsWhenNothingMatches()
    {
        _ = new CodeMatcher(Instructions)
            .SearchForward(ci => ci.OperandIs("F"))
            .RemoveUntilForward(Ldstr["X"], Ldstr["Y"])
            .Do(m => Assert.That(m.IsInvalid, Is.True));

        _ = new CodeMatcher(Instructions)
            .SearchForward(ci => ci.OperandIs("F"))
            .RemoveUntilBackward(Ldstr["X"], Ldstr["Y"])
            .Do(m => Assert.That(m.IsInvalid, Is.True));
    }

    [Test]
    public void EditingHelpersInsertSetAndRemoveAroundTheCursor()
    {
        var matcher = new CodeMatcher(Instructions)
            .Start()
            .InsertAndAdvance(new CodeInstruction(OpCodes.Nop))
            .Do(m => Assert.Multiple(() =>
            {
                Assert.That(m.Pos, Is.EqualTo(1));
                Assert.That(m.Length, Is.EqualTo(InstructionCount + 1));
            }))
            .SetInstructionAndAdvance(new CodeInstruction(OpCodes.Ldstr, "Z"))
            // InsertAfter puts the instruction at Pos + 1 and leaves the cursor where it was.
            .InsertAfter(new CodeInstruction(OpCodes.Pop))
            .Do(m => Assert.That(m.InstructionAt(1).opcode, Is.EqualTo(OpCodes.Pop)))
            // RemoveInstruction deletes at the cursor, so step onto the Pop to take it back out.
            .Advance(1)
            .RemoveInstruction()
            .Advance(-2)
            .Do(m => Assert.That(m.Operand, Is.EqualTo("Z")))
            .SetOpcodeAndAdvance(OpCodes.Ldnull);

        Assert.Multiple(() =>
        {
            Assert.That(matcher.Instructions()[0].opcode, Is.EqualTo(OpCodes.Nop));
            Assert.That(matcher.Instructions()[1].opcode, Is.EqualTo(OpCodes.Ldnull));
            // Net effect: one leading Nop added, instruction 0 overwritten, the Pop added and removed.
            Assert.That(matcher.Length, Is.EqualTo(InstructionCount + 1));
            Assert.That(matcher.InstructionEnumeration().Count(), Is.EqualTo(InstructionCount + 1));
        });
    }

    [Test]
    public void InstructionAccessorsCopyAndRangeCheck()
    {
        var matcher = new CodeMatcher(Instructions).Start();
        var three = matcher.Instructions(3);
        var range = matcher.InstructionsInRange(2, 4);
        var offsets = matcher.Advance(4).InstructionsWithOffsets(-2, 0);

        Assert.Multiple(() =>
        {
            Assert.That(three, Has.Count.EqualTo(3));
            // The returned instructions are copies: mutating them must not touch the matcher.
            Assert.That(three[0], Is.Not.SameAs(matcher.Instructions()[0]));
            Assert.That(range.Select(c => c.opcode), Is.EqualTo(new[] { OpCodes.Call, OpCodes.Ldstr, OpCodes.Call }));
            Assert.That(offsets, Has.Count.EqualTo(3));
            Assert.Throws<InvalidOperationException>(() => matcher.InstructionsInRange(-1, 3));
            Assert.Throws<InvalidOperationException>(() => matcher.Instructions(InstructionCount));
        });
    }

    [Test]
    public void NamedMatchesAreRecoverableAfterAMatch()
    {
        var matcher = new CodeMatcher(Instructions).MatchStartForward(Ldstr["D", "the-d"], Call[mBar, "the-bar"]);

        Assert.Multiple(() =>
        {
            Assert.That(matcher.Pos, Is.EqualTo(8));
            Assert.That(matcher.NamedMatch("the-d").operand, Is.EqualTo("D"));
            Assert.That(matcher.NamedMatch("the-bar").operand, Is.EqualTo(mBar));
        });
    }

    [Test]
    public void CloneIsIndependentOfTheOriginal()
    {
        var original = new CodeMatcher(Instructions).Start();
        var clone = original.Clone().Advance(5).RemoveInstruction();

        Assert.Multiple(() =>
        {
            Assert.That(original.Pos, Is.EqualTo(0));
            Assert.That(original.Length, Is.EqualTo(InstructionCount));
            Assert.That(clone.Pos, Is.EqualTo(5));
            Assert.That(clone.Length, Is.EqualTo(InstructionCount - 1));
        });
    }

    [Test]
    public void LabelHelpersReadAndWriteThroughTheCursor()
    {
        var label = new Label();
        var instructions = Instructions;
        instructions[3].labels.Add(label);

        var matcher = new CodeMatcher(instructions).Start().AddLabelsAt(5, [label]).Advance(3);

        Assert.Multiple(() =>
        {
            Assert.That(matcher.Labels, Has.Count.EqualTo(1));
            Assert.That(matcher.DistinctLabels(matcher.Instructions()), Has.Count.EqualTo(1));
            Assert.That(matcher.Blocks, Is.Empty);
            Assert.Throws<InvalidOperationException>(() => matcher.AddLabelsAt(InstructionCount, [label]));
        });
    }

    [Test]
    public void GeneratorlessLabelAndLocalHelpersFailTheWayUpstreamDoes()
    {
        var matcher = new CodeMatcher(Instructions).Start();

        Assert.Multiple(() =>
        {
            Assert.That(Assert.Throws<InvalidOperationException>(() => matcher.DefineLabel(out _))!.Message,
                Is.EqualTo("Generator must be provided to use this method"));
            Assert.Throws<InvalidOperationException>(() => matcher.CreateLabel(out _));
            Assert.Throws<InvalidOperationException>(() => matcher.CreateLabelAt(0, out _));
            Assert.Throws<InvalidOperationException>(() => matcher.CreateLabelWithOffsets(1, out _));
            Assert.Throws<InvalidOperationException>(() => matcher.DeclareLocal(typeof(int), out _));
            Assert.Throws<InvalidOperationException>(() => matcher.InsertBranch(OpCodes.Br, 3));
        });
    }

    [Test]
    public void OnErrorSuppressesTheThrowAndKeepsTheChainAlive()
    {
        var errors = new List<string>();
        var matcher = new CodeMatcher(Instructions)
            .Start()
            .OnError((_, error) =>
            {
                errors.Add(error);
                return true;
            })
            .CreateLabel(out _)
            .Advance(1);

        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.EqualTo(new[] { "Generator must be provided to use this method" }));
            Assert.That(matcher.Pos, Is.EqualTo(1));
            // Handing back false restores the throwing behaviour.
            Assert.Throws<InvalidOperationException>(() => matcher.OnError((_, _) => false).CreateLabel(out _));
        });
    }

    [Test]
    public void ThrowIfHelpersReportWhereTheMatchFailed()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                Assert.Throws<InvalidOperationException>(
                    () => new CodeMatcher(Instructions).Start().ThrowIfNotMatch("looking for X", Ldstr["X"]))!.Message,
                Is.EqualTo("looking for X - Match failed"));

            Assert.That(
                Assert.Throws<InvalidOperationException>(
                    () => new CodeMatcher(Instructions).ThrowIfInvalid("no cursor yet"))!.Message,
                Is.EqualTo("no cursor yet - Current state is invalid"));

            Assert.That(
                Assert.Throws<InvalidOperationException>(
                    () => new CodeMatcher(Instructions).Start().ThrowIfFalse("never true", _ => false))!.Message,
                Is.EqualTo("never true - Check function returned false"));

            Assert.Throws<InvalidOperationException>(
                () => new CodeMatcher(Instructions).Start().ThrowIfNotMatchForward("forward", Ldstr["X"]));
            Assert.Throws<InvalidOperationException>(
                () => new CodeMatcher(Instructions).End().ThrowIfNotMatchBack("back", Ldstr["X"]));

            // A forward check that does match leaves the position where it was.
            Assert.That(new CodeMatcher(Instructions).Start().ThrowIfNotMatchForward("forward", Ldstr["D"]).Pos, Is.EqualTo(0));
        });
    }

    [Test]
    public void ReportFailureLogsTheLastError()
    {
        string? logged = null;
        var matcher = new CodeMatcher(Instructions).SearchForward(ci => ci.OperandIs("Z"));

        Assert.Multiple(() =>
        {
            Assert.That(matcher.ReportFailure(mFoo, message => logged = message), Is.True);
            Assert.That(logged, Does.StartWith("Cannot find"));
            Assert.That(new CodeMatcher(Instructions).Start().ReportFailure(mFoo, _ => { }), Is.False);
        });
    }

    [Test]
    public void MatchesExtensionAnswersWithoutBuildingAMatcherByHand()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Instructions.Matches([Call[mBar], Call[mFoo]]), Is.True);
            Assert.That(Instructions.Matches([Ldstr["X"]]), Is.False);
        });
    }
}

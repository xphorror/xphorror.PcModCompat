using System.Reflection;
using System.Reflection.Emit;

namespace HarmonyLib;

// ABI mirror of Harmony 2.4 HarmonyLib/Public/Transpilers.cs.
//
// Transpiler bodies never run here - the host cannot rewrite IL2CPP native code - but they are still
// compiled and loaded, and a missing member makes the whole MOD assembly fail to load rather than
// fail to transpile. These three helpers operate purely on the CodeInstruction list a MOD hands in,
// so reproducing them verbatim costs nothing and keeps a transpiler body that gets called from a
// MOD's own code (JALib's VersionSafe stubs do exactly that) behaving as upstream does.
public static class Transpilers
{
    public static IEnumerable<CodeInstruction> MethodReplacer(this IEnumerable<CodeInstruction> instructions, MethodBase from, MethodBase to)
    {
        if (from is null)
            throw new ArgumentException("Unexpected null argument", nameof(from));
        if (to is null)
            throw new ArgumentException("Unexpected null argument", nameof(to));

        foreach (var instruction in instructions)
        {
            var method = instruction.operand as MethodBase;
            if (method == from)
            {
                instruction.opcode = to.IsConstructor ? OpCodes.Newobj : OpCodes.Call;
                instruction.operand = to;
            }

            yield return instruction;
        }
    }

    public static IEnumerable<CodeInstruction> Manipulator(this IEnumerable<CodeInstruction> instructions, Func<CodeInstruction, bool> predicate, Action<CodeInstruction> action)
    {
        if (predicate is null)
            throw new ArgumentNullException(nameof(predicate));
        if (action is null)
            throw new ArgumentNullException(nameof(action));

        return instructions.Select(instruction =>
        {
            if (predicate(instruction))
                action(instruction);
            return instruction;
        }).AsEnumerable();
    }

    public static IEnumerable<CodeInstruction> DebugLogger(this IEnumerable<CodeInstruction> instructions, string text)
    {
        yield return new CodeInstruction(OpCodes.Ldstr, text);
        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FileLog), nameof(FileLog.Debug)));
        foreach (var instruction in instructions)
            yield return instruction;
    }
}

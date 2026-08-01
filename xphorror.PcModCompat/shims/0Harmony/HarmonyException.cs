namespace HarmonyLib;

// ABI mirror of Harmony 2.4 HarmonyLib/Public/HarmonyException.cs.
// The IL-offset half of the type stays present but always empty: PcCompat never compiles IL, so a
// "Invalid IL code" failure cannot originate here.
public class HarmonyException : Exception
{
    private Dictionary<int, CodeInstruction> instructions = [];

    private int errorOffset;

    internal HarmonyException()
    {
    }

    internal HarmonyException(string message)
        : base(message)
    {
    }

    internal HarmonyException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    internal HarmonyException(Exception innerException, Dictionary<int, CodeInstruction> instructions, int errorOffset)
        : base("IL Compile Error", innerException)
    {
        this.instructions = instructions;
        this.errorOffset = errorOffset;
    }

    public List<KeyValuePair<int, CodeInstruction>> GetInstructionsWithOffsets() => [.. instructions.OrderBy(ins => ins.Key)];

    public List<CodeInstruction> GetInstructions() => [.. instructions.OrderBy(ins => ins.Key).Select(ins => ins.Value)];

    public int GetErrorOffset() => errorOffset;

    public int GetErrorIndex()
    {
        if (instructions.TryGetValue(errorOffset, out var instruction))
            return GetInstructions().IndexOf(instruction);
        return -1;
    }
}

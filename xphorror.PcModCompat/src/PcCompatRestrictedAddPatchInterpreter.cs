using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Text.RegularExpressions;

namespace Xphorror.PcModCompat;

internal readonly record struct PcCompatInterpretedAddPatch(
    PcCompatMethodIdentity CallbackMethod,
    string TargetType,
    string TargetMethod,
    int RawPatchKind,
    bool NeedInstance,
    bool TryingCatch,
    int MinVersion,
    int MaxVersion,
    string AnalysisNote);

internal static partial class PcCompatRestrictedAddPatchInterpreter
{
    private const int MaxFiniteArrayElements = 256;

    public static bool TryInterpret(
        MetadataReader reader,
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int addPatchCallIndex,
        out IReadOnlyList<PcCompatInterpretedAddPatch> registrations,
        out string error)
    {
        registrations = Array.Empty<PcCompatInterpretedAddPatch>();
        error = string.Empty;

        if (!TryFindTypeStringAttributeConstructor(
                reader,
                instructions,
                addPatchCallIndex,
                out var attributeIndex,
                out var targetTypeLocal,
                out var targetMethodLocal,
                out var callbackMethodLocal,
                out var rawPatchKind,
                out var needInstance))
        {
            error = "MethodInfo AddPatch arguments are not a supported Type/string/local pattern.";
            return false;
        }

        if (!TryResolveCallbackMethod(
                reader,
                instructions,
                callbackMethodLocal,
                attributeIndex,
                out var callbackMethod))
        {
            error = $"callback MethodInfo local {callbackMethodLocal} could not be traced to a delegate method.";
            return false;
        }

        if (!TryResolveLoopMethodNames(
                reader,
                instructions,
                targetMethodLocal,
                attributeIndex,
                addPatchCallIndex,
                out var targetMethods))
        {
            error = $"target method local {targetMethodLocal} could not be traced to a finite string array.";
            return false;
        }

        if (!TryResolveVersionedTargetTypes(
                reader,
                instructions,
                targetTypeLocal,
                attributeIndex,
                out var targetTypes,
                out var targetError))
        {
            error = targetError;
            return false;
        }

        var tryingCatch = ReadTryingCatchOverride(reader, instructions, attributeIndex, addPatchCallIndex);
        registrations = targetTypes
            .SelectMany(targetType => targetMethods.Select(targetMethod => new PcCompatInterpretedAddPatch(
                callbackMethod,
                targetType.TypeName,
                targetMethod,
                rawPatchKind,
                needInstance,
                tryingCatch,
                targetType.MinVersion,
                targetType.MaxVersion,
                targetType.AnalysisNote)))
            .ToArray();
        return registrations.Count > 0;
    }

    private static bool TryFindTypeStringAttributeConstructor(
        MetadataReader reader,
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int callIndex,
        out int attributeIndex,
        out int targetTypeLocal,
        out int targetMethodLocal,
        out int callbackMethodLocal,
        out int rawPatchKind,
        out bool needInstance)
    {
        attributeIndex = -1;
        targetTypeLocal = -1;
        targetMethodLocal = -1;
        callbackMethodLocal = -1;
        rawPatchKind = 0;
        needInstance = false;

        for (var index = callIndex - 1; index >= 0; index--)
        {
            var instruction = instructions[index];
            if (instruction.OpCode != OpCodes.Newobj)
                continue;

            var identity = PcCompatMetadataNames.GetMethodIdentity(reader, instruction.MetadataToken);
            var parameters = PcCompatMetadataNames.GetMethodParameterTypes(reader, instruction.MetadataToken);
            if (!identity.DeclaringType.EndsWith("JAPatchAttribute", StringComparison.Ordinal) ||
                identity.Name != ".ctor" ||
                parameters.Count != 4 ||
                parameters[0] != "System.Type" ||
                parameters[1] != "System.String")
            {
                continue;
            }

            var needInstanceIndex = PreviousMeaningful(instructions, index - 1);
            var patchKindIndex = PreviousMeaningful(instructions, needInstanceIndex - 1);
            var targetMethodIndex = PreviousMeaningful(instructions, patchKindIndex - 1);
            var targetTypeIndex = PreviousMeaningful(instructions, targetMethodIndex - 1);
            var callbackIndex = PreviousMeaningful(instructions, targetTypeIndex - 1);

            if (needInstanceIndex < 0 || patchKindIndex < 0 || targetMethodIndex < 0 ||
                targetTypeIndex < 0 || callbackIndex < 0 ||
                !TryReadInt32(instructions[needInstanceIndex], out var needInstanceValue) ||
                !TryReadInt32(instructions[patchKindIndex], out rawPatchKind) ||
                !TryGetLoadLocalIndex(instructions[targetMethodIndex], out targetMethodLocal) ||
                !TryGetLoadLocalIndex(instructions[targetTypeIndex], out targetTypeLocal) ||
                !TryGetLoadLocalIndex(instructions[callbackIndex], out callbackMethodLocal))
            {
                return false;
            }

            attributeIndex = index;
            needInstance = needInstanceValue != 0;
            return true;
        }

        return false;
    }

    private static bool TryResolveCallbackMethod(
        MetadataReader reader,
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int methodInfoLocal,
        int beforeIndex,
        out PcCompatMethodIdentity callbackMethod)
    {
        callbackMethod = default;
        var assignmentIndex = FindPreviousStoreLocal(instructions, methodInfoLocal, beforeIndex - 1);
        if (assignmentIndex < 0)
            return false;

        var previousAssignment = FindPreviousStoreLocal(instructions, methodInfoLocal, assignmentIndex - 1);
        for (var index = assignmentIndex - 1; index > previousAssignment; index--)
        {
            var instruction = instructions[index];
            if (instruction.OpCode != OpCodes.Ldftn && instruction.OpCode != OpCodes.Ldvirtftn)
                continue;

            callbackMethod = PcCompatMetadataNames.GetMethodIdentity(reader, instruction.MetadataToken);
            return !callbackMethod.IsEmpty;
        }

        return false;
    }

    private static bool TryResolveLoopMethodNames(
        MetadataReader reader,
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int methodNameLocal,
        int beforeIndex,
        int addPatchCallIndex,
        out IReadOnlyList<string> methodNames)
    {
        methodNames = Array.Empty<string>();
        var methodNameAssignment = FindPreviousStoreLocal(instructions, methodNameLocal, beforeIndex - 1);
        if (methodNameAssignment < 0)
            return false;

        var elementLoadIndex = PreviousMeaningful(instructions, methodNameAssignment - 1);
        if (elementLoadIndex < 0 || instructions[elementLoadIndex].OpCode != OpCodes.Ldelem_Ref)
            return false;

        var indexLoad = PreviousMeaningful(instructions, elementLoadIndex - 1);
        var arrayLoad = PreviousMeaningful(instructions, indexLoad - 1);
        if (arrayLoad < 0 ||
            !TryGetLoadLocalIndex(instructions[arrayLoad], out var arrayLocal) ||
            !TryGetLoadLocalIndex(instructions[indexLoad], out var indexLocal))
            return false;

        var arrayAssignment = FindPreviousStoreLocal(instructions, arrayLocal, methodNameAssignment - 1);
        if (arrayAssignment < 0)
            return false;

        var newArrayIndex = -1;
        for (var index = arrayAssignment - 1; index >= 0; index--)
        {
            if (instructions[index].OpCode == OpCodes.Newarr)
            {
                var elementType = PcCompatMetadataNames.GetTypeFullName(reader, instructions[index].MetadataToken);
                if (elementType == "System.String")
                {
                    newArrayIndex = index;
                    break;
                }
            }

            if (TryGetStoreLocalIndex(instructions[index], out _))
                break;
        }

        if (newArrayIndex < 0)
            return false;

        var lengthIndex = PreviousMeaningful(instructions, newArrayIndex - 1);
        if (lengthIndex < 0 ||
            !TryReadInt32(instructions[lengthIndex], out var declaredLength) ||
            declaredLength <= 0 ||
            declaredLength > MaxFiniteArrayElements)
        {
            return false;
        }

        var values = new SortedDictionary<int, string>();
        for (var index = newArrayIndex + 1; index < arrayAssignment; index++)
        {
            if (instructions[index].OpCode != OpCodes.Stelem_Ref)
                continue;

            var stringIndex = PreviousMeaningful(instructions, index - 1);
            var elementIndex = PreviousMeaningful(instructions, stringIndex - 1);
            if (stringIndex < 0 || elementIndex < 0 || instructions[stringIndex].OpCode != OpCodes.Ldstr ||
                !TryReadInt32(instructions[elementIndex], out var targetIndex))
            {
                return false;
            }

            var value = PcCompatMetadataNames.GetUserString(reader, instructions[stringIndex].MetadataToken);
            if (string.IsNullOrWhiteSpace(value) || !values.TryAdd(targetIndex, value))
                return false;
        }

        if (values.Count != declaredLength ||
            !Enumerable.Range(0, declaredLength).SequenceEqual(values.Keys) ||
            !HasFiniteArrayLoop(
                instructions,
                arrayLocal,
                indexLocal,
                arrayLoad,
                addPatchCallIndex))
        {
            return false;
        }

        methodNames = values.Values.ToArray();
        return true;
    }

    private static bool HasFiniteArrayLoop(
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int arrayLocal,
        int indexLocal,
        int loopStartIndex,
        int addPatchCallIndex)
    {
        var indexInitialization = FindPreviousStoreLocal(instructions, indexLocal, loopStartIndex - 1);
        var initialValueIndex = PreviousMeaningful(instructions, indexInitialization - 1);
        if (indexInitialization < 0 || initialValueIndex < 0 ||
            !TryReadInt32(instructions[initialValueIndex], out var initialValue) || initialValue != 0)
        {
            return false;
        }

        for (var branchIndex = addPatchCallIndex + 1; branchIndex < instructions.Count; branchIndex++)
        {
            var branch = instructions[branchIndex];
            if (branch.OpCode != OpCodes.Blt && branch.OpCode != OpCodes.Blt_S)
                continue;
            if (branch.Operand is not int target || target != instructions[loopStartIndex].Offset)
                continue;

            var convIndex = PreviousMeaningful(instructions, branchIndex - 1);
            var lengthIndex = PreviousMeaningful(instructions, convIndex - 1);
            var arrayIndex = PreviousMeaningful(instructions, lengthIndex - 1);
            var counterIndex = PreviousMeaningful(instructions, arrayIndex - 1);
            if (convIndex < 0 || instructions[convIndex].OpCode != OpCodes.Conv_I4 ||
                lengthIndex < 0 || instructions[lengthIndex].OpCode != OpCodes.Ldlen ||
                arrayIndex < 0 || !TryGetLoadLocalIndex(instructions[arrayIndex], out var branchArrayLocal) ||
                branchArrayLocal != arrayLocal ||
                counterIndex < 0 || !TryGetLoadLocalIndex(instructions[counterIndex], out var branchIndexLocal) ||
                branchIndexLocal != indexLocal)
            {
                continue;
            }

            var incrementStore = FindPreviousStoreLocal(instructions, indexLocal, branchIndex - 1);
            var addIndex = PreviousMeaningful(instructions, incrementStore - 1);
            var oneIndex = PreviousMeaningful(instructions, addIndex - 1);
            var loadIndex = PreviousMeaningful(instructions, oneIndex - 1);
            if (incrementStore > addPatchCallIndex &&
                addIndex >= 0 && instructions[addIndex].OpCode == OpCodes.Add &&
                oneIndex >= 0 && TryReadInt32(instructions[oneIndex], out var increment) && increment == 1 &&
                loadIndex >= 0 && TryGetLoadLocalIndex(instructions[loadIndex], out var incrementLocal) &&
                incrementLocal == indexLocal)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveVersionedTargetTypes(
        MetadataReader reader,
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int targetTypeLocal,
        int beforeIndex,
        out IReadOnlyList<VersionedTargetType> targetTypes,
        out string error)
    {
        targetTypes = Array.Empty<VersionedTargetType>();
        error = string.Empty;
        var assignments = new List<(int Index, string TypeName)>();

        for (var index = 0; index < beforeIndex; index++)
        {
            if (!TryGetStoreLocalIndex(instructions[index], out var local) || local != targetTypeLocal)
                continue;

            if (!TryResolveTypeAssignment(reader, instructions, index, out var typeName))
            {
                error = $"target Type local {targetTypeLocal} has an unsupported assignment at IL_{instructions[index].Offset:X4}.";
                return false;
            }

            assignments.Add((index, typeName));
        }

        if (assignments.Count == 0)
        {
            error = $"target Type local {targetTypeLocal} has no resolvable assignment.";
            return false;
        }

        if (assignments.Count == 1)
        {
            targetTypes = new[]
            {
                new VersionedTargetType(assignments[0].TypeName, 0, int.MaxValue, "constant target Type")
            };
            return true;
        }

        if (assignments.Count != 2 ||
            !TryResolveAfterRevisionGuard(
                reader,
                instructions,
                assignments[1].Index,
                out var revision,
                out var assignmentWhenAfterRevision))
        {
            error = "target Type depends on a runtime condition that cannot be proven from a finite revision guard.";
            return false;
        }

        var baseTarget = assignments[0].TypeName;
        var conditionalTarget = assignments[1].TypeName;
        targetTypes = assignmentWhenAfterRevision
            ? new[]
            {
                new VersionedTargetType(baseTarget, 0, revision, $"fallback target before r{revision + 1}"),
                new VersionedTargetType(conditionalTarget, revision + 1, int.MaxValue, $"target selected by after-r{revision} guard")
            }
            : new[]
            {
                new VersionedTargetType(conditionalTarget, 0, revision, $"target selected by before-or-at-r{revision} guard"),
                new VersionedTargetType(baseTarget, revision + 1, int.MaxValue, $"fallback target after r{revision}")
            };
        return true;
    }

    private static bool TryResolveTypeAssignment(
        MetadataReader reader,
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int storeIndex,
        out string typeName)
    {
        typeName = string.Empty;
        var minimum = Math.Max(0, storeIndex - 8);
        for (var index = storeIndex - 1; index >= minimum; index--)
        {
            var instruction = instructions[index];
            if (instruction.OpCode == OpCodes.Ldtoken)
            {
                typeName = PcCompatMetadataNames.GetTypeFullName(reader, instruction.MetadataToken);
                return !string.IsNullOrWhiteSpace(typeName);
            }

            if (instruction.OpCode == OpCodes.Ldstr)
            {
                var candidate = PcCompatMetadataNames.GetUserString(reader, instruction.MetadataToken);
                var next = NextMeaningful(instructions, index + 1, storeIndex);
                if (!string.IsNullOrWhiteSpace(candidate) && next >= 0 &&
                    (instructions[next].OpCode == OpCodes.Call || instructions[next].OpCode == OpCodes.Callvirt))
                {
                    var call = PcCompatMetadataNames.GetMethodIdentity(reader, instructions[next].MetadataToken);
                    if (call.DeclaringType == "System.Reflection.Assembly" && call.Name == "GetType")
                    {
                        typeName = candidate;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool TryResolveAfterRevisionGuard(
        MetadataReader reader,
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int conditionalAssignmentIndex,
        out int revision,
        out bool assignmentWhenAfterRevision)
    {
        revision = 0;
        assignmentWhenAfterRevision = false;
        var assignment = instructions[conditionalAssignmentIndex];
        for (var index = conditionalAssignmentIndex - 1; index >= 0; index--)
        {
            var branch = instructions[index];
            if (branch.OpCode.FlowControl != FlowControl.Cond_Branch || branch.Operand is not int target ||
                target != assignment.NextOffset)
            {
                continue;
            }

            var fieldIndex = PreviousMeaningful(instructions, index - 1);
            if (fieldIndex < 0 || instructions[fieldIndex].OpCode != OpCodes.Ldsfld)
                return false;

            var field = PcCompatMetadataNames.GetFieldIdentity(reader, instructions[fieldIndex].MetadataToken);
            var match = AfterRevisionFieldPattern().Match(field.Name);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out revision))
                return false;

            assignmentWhenAfterRevision = branch.OpCode == OpCodes.Brfalse || branch.OpCode == OpCodes.Brfalse_S;
            if (!assignmentWhenAfterRevision && branch.OpCode != OpCodes.Brtrue && branch.OpCode != OpCodes.Brtrue_S)
                return false;
            return true;
        }

        return false;
    }

    private static bool ReadTryingCatchOverride(
        MetadataReader reader,
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int attributeIndex,
        int callIndex)
    {
        var tryingCatch = true;
        for (var index = attributeIndex + 1; index < callIndex; index++)
        {
            if (instructions[index].OpCode != OpCodes.Stfld)
                continue;

            var field = PcCompatMetadataNames.GetFieldIdentity(reader, instructions[index].MetadataToken);
            if (field.Name != "TryingCatch")
                continue;

            var valueIndex = PreviousMeaningful(instructions, index - 1);
            if (valueIndex >= 0 && TryReadInt32(instructions[valueIndex], out var value))
                tryingCatch = value != 0;
        }

        return tryingCatch;
    }

    private static int FindPreviousStoreLocal(
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int localIndex,
        int startIndex)
    {
        for (var index = startIndex; index >= 0; index--)
        {
            if (TryGetStoreLocalIndex(instructions[index], out var candidate) && candidate == localIndex)
                return index;
        }

        return -1;
    }

    private static bool TryGetLoadLocalIndex(PcCompatIlInstruction instruction, out int localIndex)
    {
        if (instruction.OpCode == OpCodes.Ldloc_0) localIndex = 0;
        else if (instruction.OpCode == OpCodes.Ldloc_1) localIndex = 1;
        else if (instruction.OpCode == OpCodes.Ldloc_2) localIndex = 2;
        else if (instruction.OpCode == OpCodes.Ldloc_3) localIndex = 3;
        else if (instruction.OpCode == OpCodes.Ldloc || instruction.OpCode == OpCodes.Ldloc_S)
            localIndex = instruction.Operand is int value ? value : -1;
        else
        {
            localIndex = -1;
            return false;
        }

        return localIndex >= 0;
    }

    private static bool TryGetStoreLocalIndex(PcCompatIlInstruction instruction, out int localIndex)
    {
        if (instruction.OpCode == OpCodes.Stloc_0) localIndex = 0;
        else if (instruction.OpCode == OpCodes.Stloc_1) localIndex = 1;
        else if (instruction.OpCode == OpCodes.Stloc_2) localIndex = 2;
        else if (instruction.OpCode == OpCodes.Stloc_3) localIndex = 3;
        else if (instruction.OpCode == OpCodes.Stloc || instruction.OpCode == OpCodes.Stloc_S)
            localIndex = instruction.Operand is int value ? value : -1;
        else
        {
            localIndex = -1;
            return false;
        }

        return localIndex >= 0;
    }

    private static bool TryReadInt32(PcCompatIlInstruction instruction, out int value)
    {
        if (instruction.OpCode == OpCodes.Ldc_I4_M1) value = -1;
        else if (instruction.OpCode == OpCodes.Ldc_I4_0) value = 0;
        else if (instruction.OpCode == OpCodes.Ldc_I4_1) value = 1;
        else if (instruction.OpCode == OpCodes.Ldc_I4_2) value = 2;
        else if (instruction.OpCode == OpCodes.Ldc_I4_3) value = 3;
        else if (instruction.OpCode == OpCodes.Ldc_I4_4) value = 4;
        else if (instruction.OpCode == OpCodes.Ldc_I4_5) value = 5;
        else if (instruction.OpCode == OpCodes.Ldc_I4_6) value = 6;
        else if (instruction.OpCode == OpCodes.Ldc_I4_7) value = 7;
        else if (instruction.OpCode == OpCodes.Ldc_I4_8) value = 8;
        else if (instruction.OpCode == OpCodes.Ldc_I4 || instruction.OpCode == OpCodes.Ldc_I4_S)
            value = instruction.Operand is int number ? number : 0;
        else
        {
            value = 0;
            return false;
        }

        return true;
    }

    private static int PreviousMeaningful(IReadOnlyList<PcCompatIlInstruction> instructions, int index)
    {
        while (index >= 0 && instructions[index].OpCode == OpCodes.Nop)
            index--;
        return index;
    }

    private static int NextMeaningful(
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int index,
        int maximumExclusive)
    {
        while (index < maximumExclusive && instructions[index].OpCode == OpCodes.Nop)
            index++;
        return index < maximumExclusive ? index : -1;
    }

    [GeneratedRegex("(?:^|_)isAfterR(\\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AfterRevisionFieldPattern();

    private readonly record struct VersionedTargetType(
        string TypeName,
        int MinVersion,
        int MaxVersion,
        string AnalysisNote);
}

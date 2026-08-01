using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Xphorror.PcModCompat;

internal static class PcCompatDynamicAddPatchScanner
{
    private const string DynamicSource = "dynamic_addpatch";

    public static void Scan(
        MetadataReader reader,
        PEReader peReader,
        string assemblyPath,
        string modId,
        List<PcCompatPatchDescriptor> patches,
        List<PcCompatStaticPatchScanIssue> issues)
    {
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var containingType = PcCompatMetadataNames.GetTypeFullName(reader, typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                    continue;

                IReadOnlyList<PcCompatIlInstruction> instructions;
                try
                {
                    instructions = PcCompatIlDecoder.Decode(peReader.GetMethodBody(method.RelativeVirtualAddress));
                }
                catch (Exception ex)
                {
                    issues.Add(Issue(
                        "MethodBodyDecodeFailed",
                        $"{ex.GetType().Name}: {ex.Message}",
                        assemblyPath,
                        containingType,
                        reader.GetString(method.Name)));
                    continue;
                }

                ScanMethod(
                    reader,
                    assemblyPath,
                    modId,
                    containingType,
                    reader.GetString(method.Name),
                    instructions,
                    patches,
                    issues);
            }
        }
    }

    private static void ScanMethod(
        MetadataReader reader,
        string assemblyPath,
        string modId,
        string containingType,
        string containingMethod,
        IReadOnlyList<PcCompatIlInstruction> instructions,
        List<PcCompatPatchDescriptor> patches,
        List<PcCompatStaticPatchScanIssue> issues)
    {
        var addPatchCalls = instructions
            .Select((instruction, index) => (instruction, index))
            .Where(item => IsAddPatchCall(reader, item.instruction, out _))
            .ToArray();
        if (addPatchCalls.Length == 0)
            return;

        var versionAnalysis = new RevisionReachability(reader, instructions);
        var previousAddPatchIndex = -1;
        var unsupportedOverloads = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (call, callIndex) in addPatchCalls)
        {
            var callIdentity = PcCompatMetadataNames.GetMethodIdentity(reader, call.MetadataToken);
            var parameterTypes = PcCompatMetadataNames.GetMethodParameterTypes(reader, call.MetadataToken);
            if (IsMethodInfoAttributeAddPatch(parameterTypes))
            {
                if (!PcCompatRestrictedAddPatchInterpreter.TryInterpret(
                        reader,
                        instructions,
                        callIndex,
                        out var registrations,
                        out var interpretationError))
                {
                    issues.Add(Issue(
                        "UnsupportedDynamicAddPatchPattern",
                        $"IL_{call.Offset:X4}: {interpretationError}",
                        assemblyPath,
                        containingType,
                        containingMethod,
                        call.Offset));
                    previousAddPatchIndex = callIndex;
                    continue;
                }

                if (!versionAnalysis.TryGetContiguousGate(call.Offset, out var methodMinVersion, out var methodMaxVersion))
                {
                    issues.Add(Issue(
                        "UnsupportedDynamicVersionGate",
                        $"IL_{call.Offset:X4}: registration is guarded by a non-contiguous or unsupported revision condition.",
                        assemblyPath,
                        containingType,
                        containingMethod,
                        call.Offset));
                    previousAddPatchIndex = callIndex;
                    continue;
                }

                foreach (var registration in registrations)
                {
                    var interpretedMinVersion = Math.Max(methodMinVersion, registration.MinVersion);
                    var interpretedMaxVersion = Math.Min(methodMaxVersion, registration.MaxVersion);
                    if (interpretedMinVersion > interpretedMaxVersion)
                        continue;

                    var interpretedKind = PcCompatPatchKinds.FromJALibValue(registration.RawPatchKind);
                    patches.Add(new PcCompatPatchDescriptor
                    {
                        ModId = modId,
                        TargetType = registration.TargetType,
                        TargetMethod = registration.TargetMethod,
                        Kind = interpretedKind,
                        CallbackType = registration.CallbackMethod.DeclaringType,
                        CallbackMethod = registration.CallbackMethod.Name,
                        CallbackParameterTypeNames = PcCompatMetadataNames.GetMethodParameterTypes(
                            reader,
                            registration.CallbackMethod.MetadataToken),
                        NeedInstance = registration.NeedInstance,
                        MinVersion = interpretedMinVersion,
                        MaxVersion = interpretedMaxVersion,
                        TryingCatch = registration.TryingCatch,
                        Source = DynamicSource,
                        Status = PcCompatPatchStatus.RegisteredOnly,
                        Reason = $"statically interpreted from MethodInfo AddPatch ({registration.AnalysisNote}); callback translation is pending"
                    });
                }

                previousAddPatchIndex = callIndex;
                continue;
            }

            if (!IsDelegateAttributeAddPatch(parameterTypes))
            {
                if (parameterTypes.Count == 2 &&
                    parameterTypes[1].EndsWith("JAPatchAttribute", StringComparison.Ordinal))
                {
                    unsupportedOverloads.Add($"{callIdentity.DisplayName}({string.Join(", ", parameterTypes)})");
                }

                previousAddPatchIndex = callIndex;
                continue;
            }

            if (!TryMatchRegistration(
                    reader,
                    instructions,
                    previousAddPatchIndex + 1,
                    callIndex,
                    out var match,
                    out var error))
            {
                issues.Add(Issue(
                    "UnsupportedDynamicAddPatchPattern",
                    $"IL_{call.Offset:X4}: {error}",
                    assemblyPath,
                    containingType,
                    containingMethod,
                    call.Offset));
                previousAddPatchIndex = callIndex;
                continue;
            }

            if (!versionAnalysis.TryGetContiguousGate(call.Offset, out var minVersion, out var maxVersion))
            {
                issues.Add(Issue(
                    "UnsupportedDynamicVersionGate",
                    $"IL_{call.Offset:X4}: registration is guarded by a non-contiguous or unsupported revision condition.",
                    assemblyPath,
                    containingType,
                    containingMethod,
                    call.Offset));
                previousAddPatchIndex = callIndex;
                continue;
            }

            var declaredKind = PcCompatPatchKinds.FromJALibValue(match.RawPatchKind);
            var kind = IsReversePatch(reader, match.CallbackMethod, match.TargetMethod)
                ? PcCompatPatchKind.ReversePatch
                : declaredKind;

            patches.Add(new PcCompatPatchDescriptor
            {
                ModId = modId,
                TargetType = match.TargetMethod.DeclaringType,
                TargetMethod = match.TargetMethod.Name,
                Kind = kind,
                CallbackType = match.CallbackMethod.DeclaringType,
                CallbackMethod = match.CallbackMethod.Name,
                CallbackParameterTypeNames = PcCompatMetadataNames.GetMethodParameterTypes(
                    reader,
                    match.CallbackMethod.MetadataToken),
                NeedInstance = match.NeedInstance,
                MinVersion = minVersion,
                MaxVersion = maxVersion,
                TryingCatch = true,
                Source = DynamicSource,
                Status = PcCompatPatchStatus.RegisteredOnly,
                Reason = kind == PcCompatPatchKind.ReversePatch
                    ? $"statically discovered from AddPatch; declared JALib kind={declaredKind}; callback translation is pending"
                    : "statically discovered from AddPatch; callback translation is pending"
            });

            previousAddPatchIndex = callIndex;
        }

        foreach (var overload in unsupportedOverloads)
        {
            issues.Add(Issue(
                "UnsupportedDynamicAddPatchOverload",
                $"Static AddPatch analysis does not yet support {overload}.",
                assemblyPath,
                containingType,
                containingMethod));
        }
    }

    private static bool TryMatchRegistration(
        MetadataReader reader,
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int startIndex,
        int callIndex,
        out DynamicAddPatchMatch match,
        out string error)
    {
        match = default;
        error = string.Empty;

        var attributeIndex = -1;
        for (var index = callIndex - 1; index >= startIndex; index--)
        {
            var instruction = instructions[index];
            if (instruction.OpCode != OpCodes.Newobj)
                continue;

            var identity = PcCompatMetadataNames.GetMethodIdentity(reader, instruction.MetadataToken);
            var parameters = PcCompatMetadataNames.GetMethodParameterTypes(reader, instruction.MetadataToken);
            if (identity.DeclaringType.EndsWith("JAPatchAttribute", StringComparison.Ordinal) &&
                identity.Name == ".ctor" &&
                parameters.Count == 3 &&
                parameters[0] == "System.Delegate")
            {
                attributeIndex = index;
                break;
            }
        }

        if (attributeIndex < 0)
        {
            error = "new JAPatchAttribute(Delegate, PatchType, bool) was not found before AddPatch.";
            return false;
        }

        var needInstanceIndex = PreviousMeaningfulInstruction(instructions, attributeIndex - 1, startIndex);
        var patchKindIndex = PreviousMeaningfulInstruction(instructions, needInstanceIndex - 1, startIndex);
        if (needInstanceIndex < 0 || patchKindIndex < 0 ||
            !TryReadInt32(instructions[needInstanceIndex], out var needInstanceValue) ||
            !TryReadInt32(instructions[patchKindIndex], out var patchKindValue))
        {
            error = "PatchType/NeedInstance constants could not be decoded.";
            return false;
        }

        var methodPointers = new List<PcCompatMethodIdentity>();
        for (var index = startIndex; index < patchKindIndex; index++)
        {
            var instruction = instructions[index];
            if (instruction.OpCode != OpCodes.Ldftn && instruction.OpCode != OpCodes.Ldvirtftn)
                continue;

            var identity = PcCompatMetadataNames.GetMethodIdentity(reader, instruction.MetadataToken);
            if (!identity.IsEmpty)
                methodPointers.Add(identity);
        }

        if (methodPointers.Count < 2)
        {
            error = $"expected callback and target delegate method pointers, found {methodPointers.Count}.";
            return false;
        }

        match = new DynamicAddPatchMatch(
            methodPointers[^2],
            methodPointers[^1],
            patchKindValue,
            needInstanceValue != 0);
        return true;
    }

    private static bool IsReversePatch(
        MetadataReader reader,
        PcCompatMethodIdentity callback,
        PcCompatMethodIdentity target)
    {
        if (callback.IsEmpty || target.IsEmpty ||
            !string.Equals(callback.DeclaringType, target.DeclaringType, StringComparison.Ordinal) ||
            string.Equals(callback.Name, target.Name, StringComparison.Ordinal))
        {
            return false;
        }

        return PcCompatMetadataNames.TypeDefinesMethod(reader, target.DeclaringType, target.Name);
    }

    private static bool IsAddPatchCall(
        MetadataReader reader,
        PcCompatIlInstruction instruction,
        out PcCompatMethodIdentity identity)
    {
        identity = default;
        if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
            return false;

        identity = PcCompatMetadataNames.GetMethodIdentity(reader, instruction.MetadataToken);
        return identity.Name == "AddPatch" &&
               identity.DeclaringType.EndsWith(".JAPatcher", StringComparison.Ordinal);
    }

    private static bool IsDelegateAttributeAddPatch(IReadOnlyList<string> parameterTypes)
        => parameterTypes.Count == 2 &&
           parameterTypes[0] == "System.Delegate" &&
           parameterTypes[1].EndsWith("JAPatchAttribute", StringComparison.Ordinal);

    private static bool IsMethodInfoAttributeAddPatch(IReadOnlyList<string> parameterTypes)
        => parameterTypes.Count == 2 &&
           parameterTypes[0] == "System.Reflection.MethodInfo" &&
           parameterTypes[1].EndsWith("JAPatchAttribute", StringComparison.Ordinal);

    private static int PreviousMeaningfulInstruction(
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int index,
        int minimumIndex)
    {
        while (index >= minimumIndex && instructions[index].OpCode == OpCodes.Nop)
            index--;
        return index >= minimumIndex ? index : -1;
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

    private static PcCompatStaticPatchScanIssue Issue(
        string code,
        string message,
        string assemblyPath,
        string callbackType,
        string callbackMethod,
        int? ilOffset = null)
        => new()
        {
            Code = code,
            Message = message,
            AssemblyPath = assemblyPath,
            CallbackType = callbackType,
            CallbackMethod = callbackMethod,
            IlOffset = ilOffset
        };

    private readonly record struct DynamicAddPatchMatch(
        PcCompatMethodIdentity CallbackMethod,
        PcCompatMethodIdentity TargetMethod,
        int RawPatchKind,
        bool NeedInstance);

    private sealed class RevisionReachability
    {
        private readonly MetadataReader _reader;
        private readonly IReadOnlyList<PcCompatIlInstruction> _instructions;
        private readonly Dictionary<int, int> _indicesByOffset;
        private readonly int[] _samples;
        private readonly Dictionary<int, HashSet<int>> _reachableByRevision = new();

        public RevisionReachability(MetadataReader reader, IReadOnlyList<PcCompatIlInstruction> instructions)
        {
            _reader = reader;
            _instructions = instructions;
            _indicesByOffset = instructions
                .Select((instruction, index) => (instruction.Offset, index))
                .ToDictionary(item => item.Offset, item => item.index);
            _samples = BuildRevisionSamples();
        }

        public bool TryGetContiguousGate(int instructionOffset, out int minVersion, out int maxVersion)
        {
            minVersion = 0;
            maxVersion = int.MaxValue;
            var states = _samples
                .Select(revision => (Revision: revision, Reachable: GetReachableOffsets(revision).Contains(instructionOffset)))
                .ToArray();

            var reachable = states.Where(state => state.Reachable).Select(state => state.Revision).ToArray();
            if (reachable.Length == 0)
                return false;

            var entered = false;
            var exited = false;
            foreach (var state in states)
            {
                if (state.Reachable)
                {
                    if (exited)
                        return false;
                    entered = true;
                }
                else if (entered)
                {
                    exited = true;
                }
            }

            minVersion = reachable[0];
            maxVersion = reachable[^1] == int.MaxValue ? int.MaxValue : reachable[^1];
            return true;
        }

        private int[] BuildRevisionSamples()
        {
            var samples = new SortedSet<int> { 0, int.MaxValue };
            for (var index = 0; index < _instructions.Count; index++)
            {
                if (!TryReadVersionComparison(index, 0, out _, out var threshold))
                    continue;

                if (threshold >= 0)
                    samples.Add(threshold);
                if (threshold > 0)
                    samples.Add(threshold - 1);
                if (threshold >= 0 && threshold < int.MaxValue)
                    samples.Add(threshold + 1);
            }

            return samples.ToArray();
        }

        private HashSet<int> GetReachableOffsets(int revision)
        {
            if (_reachableByRevision.TryGetValue(revision, out var cached))
                return cached;

            var reachable = new HashSet<int>();
            if (_instructions.Count == 0)
                return reachable;

            var pending = new Stack<int>();
            pending.Push(_instructions[0].Offset);
            while (pending.Count > 0)
            {
                var offset = pending.Pop();
                if (!reachable.Add(offset) || !_indicesByOffset.TryGetValue(offset, out var index))
                    continue;

                var instruction = _instructions[index];
                var nextOffset = index + 1 < _instructions.Count ? _instructions[index + 1].Offset : -1;

                if (instruction.OpCode == OpCodes.Switch && instruction.Operand is int[] targets)
                {
                    foreach (var target in targets)
                        pending.Push(target);
                    if (nextOffset >= 0)
                        pending.Push(nextOffset);
                    continue;
                }

                if (instruction.OpCode.FlowControl == FlowControl.Branch)
                {
                    if (instruction.Operand is int target)
                        pending.Push(target);
                    continue;
                }

                if (instruction.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    if (instruction.Operand is int target)
                    {
                        if (TryReadVersionComparison(index, revision, out var takeBranch, out _))
                        {
                            if (takeBranch)
                                pending.Push(target);
                            else if (nextOffset >= 0)
                                pending.Push(nextOffset);
                        }
                        else
                        {
                            pending.Push(target);
                            if (nextOffset >= 0)
                                pending.Push(nextOffset);
                        }
                    }
                    continue;
                }

                if (instruction.OpCode.FlowControl is FlowControl.Return or FlowControl.Throw)
                    continue;

                if (nextOffset >= 0)
                    pending.Push(nextOffset);
            }

            _reachableByRevision[revision] = reachable;
            return reachable;
        }

        private bool TryReadVersionComparison(
            int branchIndex,
            int revision,
            out bool takeBranch,
            out int threshold)
        {
            takeBranch = false;
            threshold = 0;
            var branch = _instructions[branchIndex];
            if (branch.OpCode.FlowControl != FlowControl.Cond_Branch || branch.OpCode == OpCodes.Switch)
                return false;

            var rightIndex = PreviousMeaningfulInstruction(_instructions, branchIndex - 1, 0);
            var leftIndex = PreviousMeaningfulInstruction(_instructions, rightIndex - 1, 0);
            if (rightIndex < 0 || leftIndex < 0)
                return false;

            var leftIsRevision = IsReleaseNumberLoad(_instructions[leftIndex]);
            var rightIsRevision = IsReleaseNumberLoad(_instructions[rightIndex]);
            int left;
            int right;
            if (leftIsRevision && TryReadInt32(_instructions[rightIndex], out var rightConstant))
            {
                left = revision;
                right = rightConstant;
                threshold = rightConstant;
            }
            else if (rightIsRevision && TryReadInt32(_instructions[leftIndex], out var leftConstant))
            {
                left = leftConstant;
                right = revision;
                threshold = leftConstant;
            }
            else
            {
                return false;
            }

            var name = branch.OpCode.Name?.Replace(".s", string.Empty, StringComparison.Ordinal) ?? string.Empty;
            takeBranch = name switch
            {
                "beq" => left == right,
                "bne.un" => left != right,
                "bge" or "bge.un" => left >= right,
                "bgt" or "bgt.un" => left > right,
                "ble" or "ble.un" => left <= right,
                "blt" or "blt.un" => left < right,
                _ => false
            };
            return name is "beq" or "bne.un" or "bge" or "bge.un" or
                "bgt" or "bgt.un" or "ble" or "ble.un" or "blt" or "blt.un";
        }

        private bool IsReleaseNumberLoad(PcCompatIlInstruction instruction)
        {
            if (instruction.OpCode != OpCodes.Ldsfld)
                return false;

            var field = PcCompatMetadataNames.GetFieldIdentity(_reader, instruction.MetadataToken);
            return field.Name == "releaseNumber" &&
                   field.DeclaringType.EndsWith(".VersionControl", StringComparison.Ordinal);
        }
    }
}

internal readonly record struct PcCompatMethodIdentity(string DeclaringType, string Name, int MetadataToken)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(DeclaringType) || string.IsNullOrWhiteSpace(Name);
    public string DisplayName => IsEmpty ? "<unknown>" : DeclaringType + "." + Name;
}

internal readonly record struct PcCompatFieldIdentity(string DeclaringType, string Name)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(DeclaringType) || string.IsNullOrWhiteSpace(Name);
    public string DisplayName => IsEmpty ? "<unknown>" : DeclaringType + "." + Name;
}

internal readonly record struct PcCompatIlInstruction(
    int Offset,
    OpCode OpCode,
    object? Operand,
    int NextOffset)
{
    public int MetadataToken => Operand is int token ? token : 0;
}

internal static class PcCompatIlDecoder
{
    private static readonly OpCode[] OneByteOpCodes = new OpCode[0x100];
    private static readonly OpCode[] TwoByteOpCodes = new OpCode[0x100];

    static PcCompatIlDecoder()
    {
        foreach (var field in typeof(OpCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opCode)
                continue;

            var value = unchecked((ushort)opCode.Value);
            if (value < 0x100)
                OneByteOpCodes[value] = opCode;
            else if ((value & 0xFF00) == 0xFE00)
                TwoByteOpCodes[value & 0xFF] = opCode;
        }
    }

    public static IReadOnlyList<PcCompatIlInstruction> Decode(MethodBodyBlock body)
    {
        var il = body.GetILBytes() ?? Array.Empty<byte>();
        var instructions = new List<PcCompatIlInstruction>();
        var offset = 0;
        while (offset < il.Length)
        {
            var instructionOffset = offset;
            var first = il[offset++];
            var opCode = first == 0xFE
                ? TwoByteOpCodes[il[offset++]]
                : OneByteOpCodes[first];
            if (opCode.Size == 0)
                throw new BadImageFormatException($"Unknown IL opcode at IL_{instructionOffset:X4}.");

            object? operand = null;
            switch (opCode.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineI:
                    operand = (int)(sbyte)il[offset++];
                    break;
                case OperandType.InlineI:
                    operand = ReadInt32(il, ref offset);
                    break;
                case OperandType.InlineI8:
                    operand = ReadInt64(il, ref offset);
                    break;
                case OperandType.ShortInlineR:
                    operand = BitConverter.Int32BitsToSingle(ReadInt32(il, ref offset));
                    break;
                case OperandType.InlineR:
                    operand = BitConverter.Int64BitsToDouble(ReadInt64(il, ref offset));
                    break;
                case OperandType.ShortInlineBrTarget:
                {
                    var delta = (sbyte)il[offset++];
                    operand = offset + delta;
                    break;
                }
                case OperandType.InlineBrTarget:
                {
                    var delta = ReadInt32(il, ref offset);
                    operand = offset + delta;
                    break;
                }
                case OperandType.InlineSwitch:
                {
                    var count = ReadInt32(il, ref offset);
                    if (count < 0 || count > (il.Length - offset) / sizeof(int))
                        throw new BadImageFormatException($"Invalid switch table at IL_{instructionOffset:X4}.");
                    var tableOffset = offset;
                    var baseOffset = tableOffset + count * sizeof(int);
                    var targets = new int[count];
                    for (var index = 0; index < count; index++)
                    {
                        var delta = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(tableOffset + index * sizeof(int), sizeof(int)));
                        targets[index] = baseOffset + delta;
                    }
                    offset = baseOffset;
                    operand = targets;
                    break;
                }
                case OperandType.ShortInlineVar:
                    operand = (int)il[offset++];
                    break;
                case OperandType.InlineVar:
                    operand = (int)ReadUInt16(il, ref offset);
                    break;
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                    operand = ReadInt32(il, ref offset);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported IL operand type {opCode.OperandType} at IL_{instructionOffset:X4}.");
            }

            instructions.Add(new PcCompatIlInstruction(instructionOffset, opCode, operand, offset));
        }

        return instructions;
    }

    private static int ReadInt32(byte[] bytes, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);
        return value;
    }

    private static long ReadInt64(byte[] bytes, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset, sizeof(long)));
        offset += sizeof(long);
        return value;
    }

    private static ushort ReadUInt16(byte[] bytes, ref int offset)
    {
        var value = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, sizeof(ushort)));
        offset += sizeof(ushort);
        return value;
    }
}

internal static class PcCompatMetadataNames
{
    private static readonly PcCompatSignatureTypeProvider SignatureProvider = new();

    public static PcCompatMethodIdentity GetMethodIdentity(MetadataReader reader, int metadataToken)
    {
        if (!TryGetEntityHandle(metadataToken, out var handle))
            return default;

        return GetMethodIdentity(reader, handle);
    }

    public static PcCompatFieldIdentity GetFieldIdentity(MetadataReader reader, int metadataToken)
    {
        if (!TryGetEntityHandle(metadataToken, out var handle))
            return default;

        return handle.Kind switch
        {
            HandleKind.FieldDefinition => GetFieldDefinitionIdentity(reader, (FieldDefinitionHandle)handle),
            HandleKind.MemberReference => GetMemberReferenceFieldIdentity(reader, (MemberReferenceHandle)handle),
            _ => default
        };
    }

    public static IReadOnlyList<string> GetMethodParameterTypes(MetadataReader reader, int metadataToken)
    {
        if (!TryGetEntityHandle(metadataToken, out var handle))
            return Array.Empty<string>();

        try
        {
            return handle.Kind switch
            {
                HandleKind.MethodDefinition => GetMethodParameterTypes(reader, (MethodDefinitionHandle)handle),
                HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)handle)
                    .DecodeMethodSignature(SignatureProvider, genericContext: null).ParameterTypes.ToArray(),
                HandleKind.MethodSpecification => GetMethodParameterTypes(
                    reader,
                    MetadataTokens.GetToken(reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method)),
                _ => Array.Empty<string>()
            };
        }
        catch (BadImageFormatException)
        {
            return Array.Empty<string>();
        }
    }

    public static IReadOnlyList<string> GetMethodParameterTypes(
        MetadataReader reader,
        MethodDefinitionHandle handle)
    {
        try
        {
            return reader.GetMethodDefinition(handle)
                .DecodeSignature(SignatureProvider, genericContext: null)
                .ParameterTypes
                .ToArray();
        }
        catch (BadImageFormatException)
        {
            return Array.Empty<string>();
        }
    }

    public static bool? GetMethodIsInstance(MetadataReader reader, int metadataToken)
    {
        if (!TryGetEntityHandle(metadataToken, out var handle))
            return null;

        try
        {
            return handle.Kind switch
            {
                HandleKind.MethodDefinition =>
                    (reader.GetMethodDefinition((MethodDefinitionHandle)handle).Attributes &
                     System.Reflection.MethodAttributes.Static) == 0,
                HandleKind.MemberReference =>
                    reader.GetMemberReference((MemberReferenceHandle)handle)
                        .DecodeMethodSignature(SignatureProvider, genericContext: null)
                        .Header.IsInstance,
                HandleKind.MethodSpecification => GetMethodIsInstance(
                    reader,
                    MetadataTokens.GetToken(reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method)),
                _ => null
            };
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    public static IReadOnlyList<string> GetMethodGenericArguments(MetadataReader reader, int metadataToken)
    {
        if (!TryGetEntityHandle(metadataToken, out var handle) ||
            handle.Kind != HandleKind.MethodSpecification)
            return Array.Empty<string>();

        try
        {
            return reader.GetMethodSpecification((MethodSpecificationHandle)handle)
                .DecodeSignature(SignatureProvider, genericContext: null)
                .ToArray();
        }
        catch (BadImageFormatException)
        {
            return Array.Empty<string>();
        }
    }

    public static string GetMethodReturnType(MetadataReader reader, int metadataToken)
    {
        if (!TryGetEntityHandle(metadataToken, out var handle))
            return string.Empty;

        try
        {
            return handle.Kind switch
            {
                HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)handle)
                    .DecodeSignature(SignatureProvider, genericContext: null)
                    .ReturnType,
                HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)handle)
                    .DecodeMethodSignature(SignatureProvider, genericContext: null)
                    .ReturnType,
                HandleKind.MethodSpecification => GetMethodReturnType(
                    reader,
                    MetadataTokens.GetToken(reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method)),
                _ => string.Empty
            };
        }
        catch (BadImageFormatException)
        {
            return string.Empty;
        }
    }

    public static string GetFieldType(MetadataReader reader, int metadataToken)
    {
        if (!TryGetEntityHandle(metadataToken, out var handle))
            return string.Empty;

        try
        {
            return handle.Kind switch
            {
                HandleKind.FieldDefinition => reader.GetFieldDefinition((FieldDefinitionHandle)handle)
                    .DecodeSignature(SignatureProvider, genericContext: null),
                HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)handle)
                    .DecodeFieldSignature(SignatureProvider, genericContext: null),
                _ => string.Empty
            };
        }
        catch (BadImageFormatException)
        {
            return string.Empty;
        }
    }

    public static string GetTypeFullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var declaringType = type.GetDeclaringType();
        if (!declaringType.IsNil)
            return GetTypeFullName(reader, declaringType) + "+" + name;

        var ns = reader.GetString(type.Namespace);
        return string.IsNullOrWhiteSpace(ns) ? name : ns + "." + name;
    }

    public static string GetTypeFullName(MetadataReader reader, int metadataToken)
    {
        if (!TryGetEntityHandle(metadataToken, out var handle))
            return string.Empty;
        return GetTypeFullName(reader, handle);
    }

    public static string GetUserString(MetadataReader reader, int metadataToken)
    {
        if ((metadataToken & unchecked((int)0xFF000000)) != 0x70000000)
            return string.Empty;

        try
        {
            return reader.GetUserString(MetadataTokens.UserStringHandle(metadataToken & 0x00FFFFFF));
        }
        catch (BadImageFormatException)
        {
            return string.Empty;
        }
    }

    public static bool TypeDefinesMethod(MetadataReader reader, string typeName, string methodName)
    {
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            if (!string.Equals(GetTypeFullName(reader, typeHandle), typeName, StringComparison.Ordinal))
                continue;

            var type = reader.GetTypeDefinition(typeHandle);
            return type.GetMethods().Any(handle =>
                string.Equals(reader.GetString(reader.GetMethodDefinition(handle).Name), methodName, StringComparison.Ordinal));
        }

        return false;
    }

    private static PcCompatMethodIdentity GetMethodIdentity(MetadataReader reader, EntityHandle handle)
    {
        return handle.Kind switch
        {
            HandleKind.MethodDefinition => GetMethodDefinitionIdentity(reader, (MethodDefinitionHandle)handle),
            HandleKind.MemberReference => GetMemberReferenceMethodIdentity(reader, (MemberReferenceHandle)handle),
            HandleKind.MethodSpecification => GetMethodIdentity(
                reader,
                reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method),
            _ => default
        };
    }

    private static PcCompatMethodIdentity GetMethodDefinitionIdentity(MetadataReader reader, MethodDefinitionHandle handle)
    {
        var method = reader.GetMethodDefinition(handle);
        return new PcCompatMethodIdentity(
            GetTypeFullName(reader, method.GetDeclaringType()),
            reader.GetString(method.Name),
            MetadataTokens.GetToken(handle));
    }

    private static PcCompatMethodIdentity GetMemberReferenceMethodIdentity(MetadataReader reader, MemberReferenceHandle handle)
    {
        var member = reader.GetMemberReference(handle);
        return new PcCompatMethodIdentity(
            GetTypeFullName(reader, member.Parent),
            reader.GetString(member.Name),
            MetadataTokens.GetToken(handle));
    }

    private static PcCompatFieldIdentity GetFieldDefinitionIdentity(MetadataReader reader, FieldDefinitionHandle handle)
    {
        var field = reader.GetFieldDefinition(handle);
        return new PcCompatFieldIdentity(
            GetTypeFullName(reader, field.GetDeclaringType()),
            reader.GetString(field.Name));
    }

    private static PcCompatFieldIdentity GetMemberReferenceFieldIdentity(MetadataReader reader, MemberReferenceHandle handle)
    {
        var member = reader.GetMemberReference(handle);
        return new PcCompatFieldIdentity(
            GetTypeFullName(reader, member.Parent),
            reader.GetString(member.Name));
    }

    /// <summary>
    /// Resolves a type definition, type reference or type specification handle to the same name
    /// spelling the rest of the scanner uses. Exposed for the Harmony attribute aggregator, which
    /// reads declaring types straight out of custom attribute blobs.
    /// </summary>
    public static string GetEntityTypeFullName(MetadataReader reader, EntityHandle handle)
        => GetTypeFullName(reader, handle);

    private static string GetTypeFullName(MetadataReader reader, EntityHandle handle)
    {
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => GetTypeFullName(reader, (TypeDefinitionHandle)handle),
            HandleKind.TypeReference => GetTypeReferenceFullName(reader, (TypeReferenceHandle)handle),
            HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle)
                .DecodeSignature(SignatureProvider, genericContext: null),
            _ => string.Empty
        };
    }

    private static string GetTypeReferenceFullName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            return GetTypeReferenceFullName(reader, (TypeReferenceHandle)type.ResolutionScope) + "+" + name;

        var ns = reader.GetString(type.Namespace);
        return string.IsNullOrWhiteSpace(ns) ? name : ns + "." + name;
    }

    private static bool TryGetEntityHandle(int token, out EntityHandle handle)
    {
        try
        {
            handle = MetadataTokens.EntityHandle(token);
            return !handle.IsNil;
        }
        catch (ArgumentException)
        {
            handle = default;
            return false;
        }
    }

    private sealed class PcCompatSignatureTypeProvider : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string elementType, ArrayShape shape)
            => elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";

        public string GetByReferenceType(string elementType)
            => elementType + "&";

        public string GetFunctionPointerType(MethodSignature<string> signature)
            => "methodptr";

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
            => genericType + "<" + string.Join(",", typeArguments) + ">";

        public string GetGenericMethodParameter(object? genericContext, int index)
            => "!!" + index;

        public string GetGenericTypeParameter(object? genericContext, int index)
            => "!" + index;

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
            => unmodifiedType;

        public string GetPinnedType(string elementType)
            => elementType;

        public string GetPointerType(string elementType)
            => elementType + "*";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode)
            => typeCode switch
            {
                PrimitiveTypeCode.Boolean => "System.Boolean",
                PrimitiveTypeCode.Byte => "System.Byte",
                PrimitiveTypeCode.Char => "System.Char",
                PrimitiveTypeCode.Double => "System.Double",
                PrimitiveTypeCode.Int16 => "System.Int16",
                PrimitiveTypeCode.Int32 => "System.Int32",
                PrimitiveTypeCode.Int64 => "System.Int64",
                PrimitiveTypeCode.IntPtr => "System.IntPtr",
                PrimitiveTypeCode.Object => "System.Object",
                PrimitiveTypeCode.SByte => "System.SByte",
                PrimitiveTypeCode.Single => "System.Single",
                PrimitiveTypeCode.String => "System.String",
                PrimitiveTypeCode.TypedReference => "System.TypedReference",
                PrimitiveTypeCode.UInt16 => "System.UInt16",
                PrimitiveTypeCode.UInt32 => "System.UInt32",
                PrimitiveTypeCode.UInt64 => "System.UInt64",
                PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
                PrimitiveTypeCode.Void => "System.Void",
                _ => typeCode.ToString()
            };

        public string GetSZArrayType(string elementType)
            => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => GetTypeFullName(reader, handle);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            => GetTypeReferenceFullName(reader, handle);

        public string GetTypeFromSpecification(
            MetadataReader reader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}

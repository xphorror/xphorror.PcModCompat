using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Xphorror.PcModCompat;

/// <summary>
/// Deterministic, little-endian container shared by the managed importer and
/// the native runtime. Version 1 carries fixed-op target/rule tables, a bounded
/// UI object graph, and optional lifecycle bytecode.
/// </summary>
public static class PcCompatUiRecipeBinary
{
    public const string FormatVersion = "ui-recipe-v1";
    public const ushort SchemaVersion = 1;
    public const ushort HeaderSize = 96;
    public const uint SectionEntrySize = 24;
    public const uint SectionCount = 10;
    public const string CompilerVersion = "pccompat-ui-recipe-v1";
    private const int MaxFileSize = 16 * 1024 * 1024;
    private const int MaxStringLength = 1 << 20;
    private const uint MaxInstructionBudget = 1_000_000;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("XPHUIRCP");

    private enum SectionType : uint
    {
        StringTable = 1,
        ParameterRefs = 2,
        Targets = 3,
        Rules = 4,
        ObjectGraph = 5,
        ComponentOps = 6,
        Lifecycle = 7,
        Bytecode = 8,
        Resources = 9,
        Diagnostics = 10
    }

    public static void Write(
        string path,
        PcModManifest manifest,
        PcCompatRecipeCompileReport report,
        int targetGameRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(report);

        var runtimeBundle = PcCompatRuntimeRuleBundle.FromReport(report);
        if (runtimeBundle.Targets.Count == 0)
            throw new InvalidDataException("Cannot emit an empty UI recipe.");

        var strings = new StringTableBuilder();
        var parameterRefs = new List<uint>();
        var targetRecords = new List<byte>();
        var ruleRecords = new List<byte>();
        var objectRecords = new List<byte>();
        var componentOpRecords = new List<byte>();
        var lifecycleRecords = new List<byte>();
        var bytecodeRecords = new List<byte>();
        var resourceRecords = new List<byte>();

        foreach (var target in runtimeBundle.Targets)
        {
            var parameterStart = checked((uint)(parameterRefs.Count));
            foreach (var parameterType in target.ParameterTypes)
                parameterRefs.Add(strings.Add(parameterType));

            var ruleStart = checked((uint)(ruleRecords.Count / RuleRecordSize));
            foreach (var rule in target.Rules)
            {
                AppendRuleRecord(
                    ruleRecords,
                    strings.Add(rule.Id),
                    strings.Add(rule.FeatureId),
                    strings.Add(rule.Source),
                    checked((uint)rule.StageCode),
                    checked((uint)rule.OpCode),
                    rule.RequiredCapabilities,
                    rule.DefaultEnabled);
            }

            AppendTargetRecord(
                targetRecords,
                checked((uint)target.Id),
                strings.Add(target.AssemblyName),
                strings.Add(target.Namespace),
                strings.Add(target.TypeName),
                strings.Add(target.MethodName),
                strings.Add(target.ReturnType),
                strings.Add(target.AbiKind),
                parameterStart,
                checked((ushort)target.ParameterTypes.Count),
                target.IsStatic,
                checked((uint)target.GenericArity),
                ruleStart,
                checked((uint)target.Rules.Count));
        }

        ValidateObjectGraph(report.UiObjectGraph);
        foreach (var node in report.UiObjectGraph.OrderBy(node => node.Id))
        {
            var operationStart = checked((uint)(componentOpRecords.Count / ComponentOpRecordSize));
            foreach (var operation in node.Initialization)
            {
                AppendComponentOpRecord(
                    componentOpRecords,
                    node.Id,
                    checked((uint)operation.OpCode),
                    strings.Add(operation.StringValue),
                    operation.Payload0,
                    operation.Payload1,
                    operation.Payload2,
                    operation.Payload3);
            }
            AppendObjectRecord(
                objectRecords,
                node.Id,
                node.ParentId,
                strings.Add(node.Name),
                checked((uint)node.Components),
                checked((uint)node.Flags),
                operationStart,
                checked((uint)node.Initialization.Count));
        }

        ValidateResourceBindings(report.UiObjectGraph, report.UiResourceBindings);
        foreach (var resource in report.UiResourceBindings
                     .OrderBy(binding => binding.NodeId)
                     .ThenBy(binding => binding.Target))
        {
            AppendResourceRecord(
                resourceRecords,
                resource.NodeId,
                checked((uint)resource.Target),
                strings.Add(resource.FeatureGroupId),
                strings.Add(resource.AssetName),
                strings.Add(resource.ExpectedType));
        }

        var lifecycleIds = new HashSet<string>(StringComparer.Ordinal);
        var lifecycleRuntimeIds = new HashSet<uint>();
        foreach (var lifecycle in report.UiLifecyclePrograms)
        {
            ValidateLifecycle(lifecycle);
            if (!lifecycleIds.Add(lifecycle.Id) || !lifecycleRuntimeIds.Add(lifecycle.RuntimeRuleId))
                throw new InvalidDataException($"Duplicate UI lifecycle identity: {lifecycle.Id}/{lifecycle.RuntimeRuleId}.");
            var programStart = checked((uint)(bytecodeRecords.Count / VmInstructionSize));
            foreach (var instruction in lifecycle.Instructions)
                AppendVmInstruction(bytecodeRecords, instruction);

            AppendLifecycleRecord(
                lifecycleRecords,
                strings.Add(lifecycle.Id),
                lifecycle.RuntimeRuleId,
                checked((uint)lifecycle.Trigger),
                checked((uint)lifecycle.ClockDomain),
                checked((uint)lifecycle.Flags),
                programStart,
                checked((uint)lifecycle.Instructions.Count),
                lifecycle.InstructionBudget,
                lifecycle.CommandType,
                lifecycle.TargetId,
                lifecycle.InitialDelayNs,
                lifecycle.DeferredRetryDelayNs);
        }

        var sections = new Dictionary<SectionType, byte[]>
        {
            [SectionType.StringTable] = strings.ToArray(),
            [SectionType.ParameterRefs] = EncodeUInt32Array(parameterRefs),
            [SectionType.Targets] = targetRecords.ToArray(),
            [SectionType.Rules] = ruleRecords.ToArray(),
            [SectionType.ObjectGraph] = objectRecords.ToArray(),
            [SectionType.ComponentOps] = componentOpRecords.ToArray(),
            [SectionType.Lifecycle] = lifecycleRecords.ToArray(),
            [SectionType.Bytecode] = bytecodeRecords.ToArray(),
            [SectionType.Resources] = resourceRecords.ToArray(),
            [SectionType.Diagnostics] = Array.Empty<byte>()
        };

        var sourceHash = ComputeSourceAssemblySha256(manifest);
        var modIdOffset = strings.Add(report.ModId);
        var recipeIdOffset = strings.Add(report.RecipeId);
        var compatibilityOffset = strings.Add(report.Compatibility);
        var compilerVersionOffset = strings.Add(CompilerVersion);

        // Adding the header strings above can extend the table. Rebuild the
        // section after all string identities are known.
        sections[SectionType.StringTable] = strings.ToArray();

        var sectionTableOffset = HeaderSize;
        var dataOffset = Align4(sectionTableOffset + checked((int)(SectionCount * SectionEntrySize)));
        var sectionEntries = new List<SectionEntry>((int)SectionCount);
        var totalSize = dataOffset;

        for (uint type = 1; type <= SectionCount; ++type)
        {
            var sectionType = (SectionType)type;
            var data = sections[sectionType];
            if (data.Length == 0)
            {
                sectionEntries.Add(new SectionEntry(type, 0, 0, 0, 0));
                continue;
            }

            dataOffset = Align4(dataOffset);
            var count = SectionCountFor(sectionType, data.Length);
            var elementSize = SectionElementSize(sectionType);
            sectionEntries.Add(new SectionEntry(
                type,
                checked((uint)dataOffset),
                checked((uint)data.Length),
                count,
                elementSize));
            dataOffset = checked(dataOffset + data.Length);
            totalSize = dataOffset;
        }

        totalSize = Align4(totalSize);
        var output = new byte[totalSize];
        Magic.CopyTo(output, 0);
        WriteUInt16(output, 8, SchemaVersion);
        WriteUInt16(output, 10, HeaderSize);
        var headerFlags = 1u | 2u; // little-endian + verified fixed-op tables
        if (lifecycleRecords.Count != 0)
            headerFlags |= 4u;
        if (objectRecords.Count != 0)
            headerFlags |= 8u;
        if (resourceRecords.Count != 0)
            headerFlags |= 16u;
        WriteUInt32(output, 12, headerFlags);
        WriteUInt32(output, 16, SectionCount);
        WriteUInt32(output, 20, checked((uint)Math.Max(0, targetGameRevision)));
        WriteUInt64(output, 24, (ulong)report.RequiredCapabilities);
        WriteUInt32(output, 32, modIdOffset);
        WriteUInt32(output, 36, recipeIdOffset);
        WriteUInt32(output, 40, compatibilityOffset);
        WriteUInt32(output, 44, compilerVersionOffset);
        sourceHash.CopyTo(output, 48);
        WriteUInt32(output, 80, checked((uint)output.Length));
        WriteUInt32(output, 84, 0); // CRC field is zero while calculating.
        WriteUInt32(output, 88, sectionTableOffset);
        WriteUInt32(output, 92, 0);

        var tableCursor = sectionTableOffset;
        foreach (var entry in sectionEntries)
        {
            WriteUInt32(output, tableCursor + 0, entry.Type);
            WriteUInt32(output, tableCursor + 4, entry.Offset);
            WriteUInt32(output, tableCursor + 8, entry.Size);
            WriteUInt32(output, tableCursor + 12, entry.Count);
            WriteUInt32(output, tableCursor + 16, entry.ElementSize);
            WriteUInt32(output, tableCursor + 20, 0);
            tableCursor += checked((int)SectionEntrySize);
        }

        foreach (var entry in sectionEntries)
        {
            if (entry.Size == 0)
                continue;

            var data = sections[(SectionType)entry.Type];
            data.CopyTo(output, checked((int)entry.Offset));
        }

        WriteUInt32(output, 84, ComputeCrc32(output));
        File.WriteAllBytes(path, output);
    }

    public static bool HasValidHeader(ReadOnlySpan<byte> data)
        => data.Length >= HeaderSize && data[..Magic.Length].SequenceEqual(Magic);

    public static bool TryValidate(string path, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "recipe path is empty";
            return false;
        }

        try
        {
            return TryValidate(File.ReadAllBytes(path), out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryValidate(ReadOnlySpan<byte> data, out string error)
    {
        error = string.Empty;
        if (data.Length > MaxFileSize)
            return Fail("recipe file exceeds the size limit", out error);
        if (!HasValidHeader(data))
            return Fail("invalid recipe magic or truncated header", out error);
        if (BinaryPrimitives.ReadUInt16LittleEndian(data[8..]) != SchemaVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(data[10..]) != HeaderSize)
            return Fail("unsupported recipe header version or size", out error);

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        if ((flags & 3u) != 3u || (flags & ~31u) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[92..]) != 0)
            return Fail("unsupported recipe flags or reserved header data", out error);

        var sectionCount = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        var totalSize = BinaryPrimitives.ReadUInt32LittleEndian(data[80..]);
        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(data[84..]);
        var sectionTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[88..]);
        if (sectionCount != SectionCount || totalSize != data.Length)
            return Fail("recipe section count or total size is invalid", out error);
        if (sectionTableOffset > data.Length ||
            SectionCount * SectionEntrySize > data.Length - sectionTableOffset)
            return Fail("recipe section table is outside the file", out error);
        if (expectedCrc != ComputeCrc32(data))
            return Fail("recipe checksum mismatch", out error);

        var tableEnd = checked((int)(sectionTableOffset + SectionCount * SectionEntrySize));
        var seen = new HashSet<uint>();
        var sections = new Dictionary<uint, (uint Offset, uint Size, uint Count, uint ElementSize)>();
        for (var index = 0u; index < SectionCount; ++index)
        {
            var cursor = checked((int)(sectionTableOffset + index * SectionEntrySize));
            var type = BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 4)..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 8)..]);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 12)..]);
            var elementSize = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 16)..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 20)..]) != 0)
                return Fail("recipe section has non-zero reserved data", out error);
            if (type == 0 || type > SectionCount || !seen.Add(type))
                return Fail("recipe section type is unknown or duplicated", out error);
            if (size == 0)
            {
                if (offset != 0 || count != 0 || elementSize != 0)
                    return Fail("empty recipe section has a descriptor", out error);
            }
            else
            {
                if (offset < tableEnd || offset > data.Length || size > data.Length - offset)
                    return Fail("recipe section range is invalid", out error);
                if (elementSize != 0 && count > size / elementSize)
                    return Fail("recipe section count exceeds its size", out error);
            }
            sections[type] = (offset, size, count, elementSize);
        }

        var nonEmptySections = sections.Values.Where(section => section.Size != 0).ToArray();
        for (var left = 0; left < nonEmptySections.Length; ++left)
        {
            var leftEnd = (ulong)nonEmptySections[left].Offset + nonEmptySections[left].Size;
            for (var right = left + 1; right < nonEmptySections.Length; ++right)
            {
                var rightEnd = (ulong)nonEmptySections[right].Offset + nonEmptySections[right].Size;
                if (nonEmptySections[left].Offset < rightEnd && nonEmptySections[right].Offset < leftEnd)
                    return Fail("recipe sections overlap", out error);
            }
        }

        if (!sections.TryGetValue(1, out var strings) || strings.Size == 0 || strings.ElementSize != 1 || strings.Count != 1 ||
            !sections.TryGetValue(2, out var parameters) ||
            !sections.TryGetValue(3, out var targets) || targets.Size == 0 || targets.ElementSize != TargetRecordSize ||
            !sections.TryGetValue(4, out var rules) || rules.Size == 0 || rules.ElementSize != RuleRecordSize)
            return Fail("recipe required section is missing or malformed", out error);
        if (targets.Size != targets.Count * TargetRecordSize ||
            rules.Size != rules.Count * RuleRecordSize ||
            (parameters.Size != 0 &&
             (parameters.ElementSize != sizeof(uint) ||
              parameters.Size != parameters.Count * sizeof(uint))))
            return Fail("recipe fixed table size is invalid", out error);
        if (targets.Count is 0 or > 4096 || rules.Count is 0 or > 16384 || parameters.Count > 16384)
            return Fail("recipe fixed table count exceeds limits", out error);
        if (!ValidateEncodedTargetsAndRules(data, strings, parameters, targets, rules, out error))
            return false;

        var objectGraph = sections[5];
        var componentOps = sections[6];
        var headerHasObjectGraph = (flags & 8u) != 0;
        if (headerHasObjectGraph != (objectGraph.Size != 0) ||
            (objectGraph.Size == 0 && componentOps.Size != 0) ||
            (objectGraph.Size != 0 &&
             (objectGraph.ElementSize != ObjectRecordSize ||
              objectGraph.Size != objectGraph.Count * ObjectRecordSize)) ||
            (componentOps.Size != 0 &&
             (componentOps.ElementSize != ComponentOpRecordSize ||
              componentOps.Size != componentOps.Count * ComponentOpRecordSize)))
            return Fail("recipe object graph sections are malformed", out error);
        if (objectGraph.Count > 1024 || componentOps.Count > 8192)
            return Fail("recipe object graph count exceeds limits", out error);
        if (objectGraph.Size != 0 &&
            !ValidateEncodedObjectGraph(data, strings, objectGraph, componentOps, out error))
            return false;

        var resources = sections[9];
        var headerHasResources = (flags & 16u) != 0;
        if (headerHasResources != (resources.Size != 0) ||
            (resources.Size != 0 &&
             (objectGraph.Size == 0 ||
              resources.ElementSize != ResourceRecordSize ||
              resources.Size != resources.Count * ResourceRecordSize)) ||
            resources.Count > 4096)
            return Fail("recipe resource binding section is malformed", out error);
        if (resources.Size != 0 &&
            !ValidateEncodedResources(data, strings, objectGraph, resources, out error))
            return false;

        var lifecycle = sections[7];
        var bytecode = sections[8];
        var headerHasLifecycle = (flags & 4u) != 0;
        if (headerHasLifecycle != (lifecycle.Size != 0) ||
            (lifecycle.Size == 0) != (bytecode.Size == 0) ||
            (lifecycle.Size != 0 &&
             (lifecycle.ElementSize != LifecycleRecordSize ||
              lifecycle.Size != lifecycle.Count * LifecycleRecordSize ||
              bytecode.ElementSize != VmInstructionSize ||
              bytecode.Size != bytecode.Count * VmInstructionSize)))
            return Fail("recipe lifecycle/bytecode sections are malformed", out error);
        if (lifecycle.Count > 4096 || bytecode.Count > 65536)
            return Fail("recipe lifecycle/bytecode count exceeds limits", out error);

        if (lifecycle.Size != 0)
        {
            var instructions = new PcCompatNativeVmInstruction[bytecode.Count];
            for (var index = 0; index < instructions.Length; ++index)
            {
                var cursor = checked((int)(bytecode.Offset + (uint)index * VmInstructionSize));
                instructions[index] = new PcCompatNativeVmInstruction(
                    (PcCompatNativeVmOpcode)data[cursor],
                    data[cursor + 1],
                    data[cursor + 2],
                    data[cursor + 3],
                    BinaryPrimitives.ReadInt32LittleEndian(data[(cursor + 4)..]),
                    BinaryPrimitives.ReadInt64LittleEndian(data[(cursor + 8)..]));
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var runtimeIds = new HashSet<uint>();
            for (var index = 0u; index < lifecycle.Count; ++index)
            {
                var cursor = checked((int)(lifecycle.Offset + index * LifecycleRecordSize));
                var idOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]);
                if (!TryReadString(data, strings.Offset, strings.Size, idOffset, out var id))
                    return Fail("recipe lifecycle id is invalid", out error);
                var runtimeId = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 4)..]);
                var programStart = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 20)..]);
                var programCount = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 24)..]);
                if (programStart > bytecode.Count || programCount > bytecode.Count - programStart)
                    return Fail("recipe lifecycle program range is invalid", out error);

                var program = instructions
                    .AsSpan(checked((int)programStart), checked((int)programCount))
                    .ToArray();
                var decoded = new PcCompatUiLifecycleProgram
                {
                    Id = id,
                    RuntimeRuleId = runtimeId,
                    Trigger = (PcCompatUiLifecycleTrigger)BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 8)..]),
                    ClockDomain = (PcCompatUiClockDomain)BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 12)..]),
                    Flags = (PcCompatUiLifecycleFlags)BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 16)..]),
                    InstructionBudget = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 28)..]),
                    CommandType = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 32)..]),
                    TargetId = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 36)..]),
                    InitialDelayNs = BinaryPrimitives.ReadInt64LittleEndian(data[(cursor + 40)..]),
                    DeferredRetryDelayNs = BinaryPrimitives.ReadInt64LittleEndian(data[(cursor + 48)..]),
                    Instructions = program
                };
                try
                {
                    ValidateLifecycle(decoded);
                }
                catch (InvalidDataException ex)
                {
                    return Fail(ex.Message, out error);
                }
                if (!ids.Add(id) || !runtimeIds.Add(runtimeId))
                    return Fail("recipe lifecycle identity is duplicated", out error);
            }
        }

        for (var offset = 32; offset <= 44; offset += 4)
        {
            var stringOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            if (!TryReadString(data, strings.Offset, strings.Size, stringOffset, out _))
                return Fail("recipe header string is invalid", out error);
        }

        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static bool TryReadString(
        ReadOnlySpan<byte> data,
        uint tableOffset,
        uint tableSize,
        uint stringOffset,
        out string value)
    {
        value = string.Empty;
        if (stringOffset >= tableSize)
            return false;
        var start = checked((int)(tableOffset + stringOffset));
        var end = checked((int)(tableOffset + tableSize));
        var length = data[start..end].IndexOf((byte)0);
        if (length < 0 || length > MaxStringLength)
            return false;
        value = Encoding.UTF8.GetString(data.Slice(start, length));
        return true;
    }

    private static bool ValidateEncodedTargetsAndRules(
        ReadOnlySpan<byte> data,
        (uint Offset, uint Size, uint Count, uint ElementSize) strings,
        (uint Offset, uint Size, uint Count, uint ElementSize) parameters,
        (uint Offset, uint Size, uint Count, uint ElementSize) targets,
        (uint Offset, uint Size, uint Count, uint ElementSize) rules,
        out string error)
    {
        for (var index = 0u; index < rules.Count; ++index)
        {
            var cursor = checked((int)(rules.Offset + index * RuleRecordSize));
            if (!TryReadString(data, strings.Offset, strings.Size,
                    BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]), out _) ||
                !TryReadString(data, strings.Offset, strings.Size,
                    BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 4)..]), out _) ||
                !TryReadString(data, strings.Offset, strings.Size,
                    BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 8)..]), out _))
                return Fail("recipe rule string reference is invalid", out error);

            var stage = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 12)..]);
            var op = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 16)..]);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 28)..]);
            var reserved = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 32)..]);
            if (stage > int.MaxValue || op > int.MaxValue || (flags & ~1u) != 0 || reserved != 0)
                return Fail("recipe rule code, flags, or reserved data is invalid", out error);
        }

        for (var index = 0u; index < parameters.Count; ++index)
        {
            var cursor = checked((int)(parameters.Offset + index * sizeof(uint)));
            var stringOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]);
            if (!TryReadString(data, strings.Offset, strings.Size, stringOffset, out _))
                return Fail("recipe parameter string reference is invalid", out error);
        }

        var targetIds = new HashSet<uint>();
        for (var index = 0u; index < targets.Count; ++index)
        {
            var cursor = checked((int)(targets.Offset + index * TargetRecordSize));
            var id = BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]);
            var parameterStart = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 28)..]);
            var parameterCount = BinaryPrimitives.ReadUInt16LittleEndian(data[(cursor + 32)..]);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(data[(cursor + 34)..]);
            var genericArity = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 36)..]);
            var ruleStart = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 40)..]);
            var ruleCount = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 44)..]);
            if (id == 0 || !targetIds.Add(id) || (flags & ~1u) != 0 || genericArity > int.MaxValue ||
                parameterStart > parameters.Count || parameterCount > parameters.Count - parameterStart ||
                ruleStart > rules.Count || ruleCount == 0 || ruleCount > rules.Count - ruleStart)
                return Fail("recipe target range, identity, or flags are invalid", out error);

            string typeName = string.Empty;
            string methodName = string.Empty;
            string returnType = string.Empty;
            for (var field = 0; field < 6; ++field)
            {
                var stringOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 4 + field * 4)..]);
                if (!TryReadString(data, strings.Offset, strings.Size, stringOffset, out var value))
                    return Fail("recipe target string reference is invalid", out error);
                if (field == 2)
                    typeName = value;
                else if (field == 3)
                    methodName = value;
                else if (field == 4)
                    returnType = value;
            }

            if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(methodName) || string.IsNullOrEmpty(returnType))
                return Fail("recipe target is incomplete", out error);
        }

        error = string.Empty;
        return true;
    }

    private static bool ValidateEncodedObjectGraph(
        ReadOnlySpan<byte> data,
        (uint Offset, uint Size, uint Count, uint ElementSize) strings,
        (uint Offset, uint Size, uint Count, uint ElementSize) objectGraph,
        (uint Offset, uint Size, uint Count, uint ElementSize) componentOps,
        out string error)
    {
        var decodedOps = new PcCompatUiComponentOperation[checked((int)componentOps.Count)];
        var operationNodeIds = new uint[decodedOps.Length];
        for (var index = 0; index < decodedOps.Length; ++index)
        {
            var cursor = checked((int)(componentOps.Offset + (uint)index * ComponentOpRecordSize));
            if (BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 12)..]) != 0)
                return Fail("recipe component op has non-zero reserved data", out error);
            var stringOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 8)..]);
            if (!TryReadString(data, strings.Offset, strings.Size, stringOffset, out var stringValue))
                return Fail("recipe component op string is invalid", out error);
            operationNodeIds[index] = BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]);
            decodedOps[index] = new PcCompatUiComponentOperation
            {
                OpCode = (PcCompatUiComponentOpCode)BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 4)..]),
                StringValue = stringValue,
                Payload0 = BinaryPrimitives.ReadInt64LittleEndian(data[(cursor + 16)..]),
                Payload1 = BinaryPrimitives.ReadInt64LittleEndian(data[(cursor + 24)..]),
                Payload2 = BinaryPrimitives.ReadInt64LittleEndian(data[(cursor + 32)..]),
                Payload3 = BinaryPrimitives.ReadInt64LittleEndian(data[(cursor + 40)..])
            };
        }

        var nodes = new List<PcCompatUiObjectNode>(checked((int)objectGraph.Count));
        var coveredOps = new bool[decodedOps.Length];
        for (var index = 0u; index < objectGraph.Count; ++index)
        {
            var cursor = checked((int)(objectGraph.Offset + index * ObjectRecordSize));
            if (BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 28)..]) != 0)
                return Fail("recipe object node has non-zero reserved data", out error);
            var id = BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]);
            var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 8)..]);
            if (!TryReadString(data, strings.Offset, strings.Size, nameOffset, out var name))
                return Fail("recipe object node name is invalid", out error);
            var operationStart = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 20)..]);
            var operationCount = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 24)..]);
            if (operationStart > componentOps.Count ||
                operationCount > componentOps.Count - operationStart)
                return Fail("recipe object node operation range is invalid", out error);

            var initialization = new PcCompatUiComponentOperation[checked((int)operationCount)];
            for (var opIndex = 0u; opIndex < operationCount; ++opIndex)
            {
                var flatIndex = checked((int)(operationStart + opIndex));
                if (coveredOps[flatIndex] || operationNodeIds[flatIndex] != id)
                    return Fail("recipe component op ownership is invalid", out error);
                coveredOps[flatIndex] = true;
                initialization[checked((int)opIndex)] = decodedOps[flatIndex];
            }
            nodes.Add(new PcCompatUiObjectNode
            {
                Id = id,
                ParentId = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 4)..]),
                Name = name,
                Components = (PcCompatUiComponentMask)BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 12)..]),
                Flags = (PcCompatUiObjectFlags)BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 16)..]),
                Initialization = initialization
            });
        }
        if (coveredOps.Any(covered => !covered))
            return Fail("recipe component op is not owned by an object node", out error);

        try
        {
            ValidateObjectGraph(nodes);
        }
        catch (InvalidDataException ex)
        {
            return Fail(ex.Message, out error);
        }
        error = string.Empty;
        return true;
    }

    private static bool ValidateEncodedResources(
        ReadOnlySpan<byte> data,
        (uint Offset, uint Size, uint Count, uint ElementSize) strings,
        (uint Offset, uint Size, uint Count, uint ElementSize) objectGraph,
        (uint Offset, uint Size, uint Count, uint ElementSize) resources,
        out string error)
    {
        var nodes = new Dictionary<uint, PcCompatUiComponentMask>();
        for (var index = 0u; index < objectGraph.Count; ++index)
        {
            var cursor = checked((int)(objectGraph.Offset + index * ObjectRecordSize));
            nodes[BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..])] =
                (PcCompatUiComponentMask)BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 12)..]);
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0u; index < resources.Count; ++index)
        {
            var cursor = checked((int)(resources.Offset + index * ResourceRecordSize));
            var nodeId = BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]);
            var target = (PcCompatUiResourceTarget)BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 4)..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 20)..]) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 24)..]) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 28)..]) != 0 ||
                !nodes.TryGetValue(nodeId, out var components) ||
                !Enum.IsDefined(target) ||
                !identities.Add(nodeId + "\0" + (uint)target))
                return Fail("recipe resource binding identity is invalid", out error);

            if (!TryReadString(data, strings.Offset, strings.Size,
                    BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 8)..]), out var group) ||
                !TryReadString(data, strings.Offset, strings.Size,
                    BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 12)..]), out var asset) ||
                !TryReadString(data, strings.Offset, strings.Size,
                    BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 16)..]), out var expectedType) ||
                string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(asset) ||
                string.IsNullOrWhiteSpace(expectedType))
                return Fail("recipe resource binding string is invalid", out error);

            try
            {
                ValidateResourceTarget(nodeId, components, target, expectedType);
            }
            catch (InvalidDataException ex)
            {
                return Fail(ex.Message, out error);
            }
        }
        error = string.Empty;
        return true;
    }

    private const int TargetRecordSize = 48;
    private const int RuleRecordSize = 36;
    private const int ObjectRecordSize = 32;
    private const int ComponentOpRecordSize = 48;
    private const int ResourceRecordSize = 32;
    private const int LifecycleRecordSize = 56;
    private const int VmInstructionSize = 16;

    private static void AppendTargetRecord(
        List<byte> output,
        uint id,
        uint assemblyOffset,
        uint namespaceOffset,
        uint typeOffset,
        uint methodOffset,
        uint returnOffset,
        uint abiOffset,
        uint parameterStart,
        ushort parameterCount,
        bool isStatic,
        uint genericArity,
        uint ruleStart,
        uint ruleCount)
    {
        AppendUInt32(output, id);
        AppendUInt32(output, assemblyOffset);
        AppendUInt32(output, namespaceOffset);
        AppendUInt32(output, typeOffset);
        AppendUInt32(output, methodOffset);
        AppendUInt32(output, returnOffset);
        AppendUInt32(output, abiOffset);
        AppendUInt32(output, parameterStart);
        AppendUInt16(output, parameterCount);
        AppendUInt16(output, (ushort)(isStatic ? 1 : 0));
        AppendUInt32(output, genericArity);
        AppendUInt32(output, ruleStart);
        AppendUInt32(output, ruleCount);
    }

    private static void AppendRuleRecord(
        List<byte> output,
        uint idOffset,
        uint featureOffset,
        uint sourceOffset,
        uint stageCode,
        uint opCode,
        ulong requiredCapabilities,
        bool defaultEnabled)
    {
        AppendUInt32(output, idOffset);
        AppendUInt32(output, featureOffset);
        AppendUInt32(output, sourceOffset);
        AppendUInt32(output, stageCode);
        AppendUInt32(output, opCode);
        AppendUInt64(output, requiredCapabilities);
        AppendUInt32(output, defaultEnabled ? 1u : 0u);
        AppendUInt32(output, 0);
    }

    private static void AppendObjectRecord(
        List<byte> output,
        uint id,
        uint parentId,
        uint nameOffset,
        uint components,
        uint flags,
        uint operationStart,
        uint operationCount)
    {
        AppendUInt32(output, id);
        AppendUInt32(output, parentId);
        AppendUInt32(output, nameOffset);
        AppendUInt32(output, components);
        AppendUInt32(output, flags);
        AppendUInt32(output, operationStart);
        AppendUInt32(output, operationCount);
        AppendUInt32(output, 0);
    }

    private static void AppendComponentOpRecord(
        List<byte> output,
        uint nodeId,
        uint opCode,
        uint stringOffset,
        long payload0,
        long payload1,
        long payload2,
        long payload3)
    {
        AppendUInt32(output, nodeId);
        AppendUInt32(output, opCode);
        AppendUInt32(output, stringOffset);
        AppendUInt32(output, 0);
        AppendUInt64(output, unchecked((ulong)payload0));
        AppendUInt64(output, unchecked((ulong)payload1));
        AppendUInt64(output, unchecked((ulong)payload2));
        AppendUInt64(output, unchecked((ulong)payload3));
    }

    private static void AppendResourceRecord(
        List<byte> output,
        uint nodeId,
        uint target,
        uint featureGroupOffset,
        uint assetNameOffset,
        uint expectedTypeOffset)
    {
        AppendUInt32(output, nodeId);
        AppendUInt32(output, target);
        AppendUInt32(output, featureGroupOffset);
        AppendUInt32(output, assetNameOffset);
        AppendUInt32(output, expectedTypeOffset);
        AppendUInt32(output, 0);
        AppendUInt32(output, 0);
        AppendUInt32(output, 0);
    }

    private static void ValidateObjectGraph(IReadOnlyList<PcCompatUiObjectNode> nodes)
    {
        if (nodes.Count > 1024)
            throw new InvalidDataException("UI object graph exceeds 1024 nodes.");
        var byId = new Dictionary<uint, PcCompatUiObjectNode>();
        var operationCount = 0;
        foreach (var node in nodes)
        {
            if (node.Id == 0 || !byId.TryAdd(node.Id, node))
                throw new InvalidDataException($"UI object graph contains invalid/duplicate node id {node.Id}.");
            if (string.IsNullOrWhiteSpace(node.Name))
                throw new InvalidDataException($"UI object node {node.Id} has an empty name.");
            const PcCompatUiComponentMask knownComponents =
                PcCompatUiComponentMask.RectTransform |
                PcCompatUiComponentMask.Canvas |
                PcCompatUiComponentMask.CanvasScaler |
                PcCompatUiComponentMask.Image |
                PcCompatUiComponentMask.TextMeshProUGUI |
                PcCompatUiComponentMask.CanvasRenderer |
                PcCompatUiComponentMask.ContentSizeFitter |
                PcCompatUiComponentMask.RawImage;
            if ((node.Components & PcCompatUiComponentMask.RectTransform) == 0 ||
                (node.Components & ~knownComponents) != 0)
                throw new InvalidDataException($"UI object node {node.Id} has unsupported components.");
            const PcCompatUiObjectFlags knownFlags =
                PcCompatUiObjectFlags.ActiveInitially |
                PcCompatUiObjectFlags.DontDestroyOnLoad;
            if ((node.Flags & ~knownFlags) != 0)
                throw new InvalidDataException($"UI object node {node.Id} has unsupported flags.");
            operationCount = checked(operationCount + node.Initialization.Count);
            foreach (var operation in node.Initialization)
                ValidateComponentOperation(node, operation);
        }
        if (operationCount > 8192)
            throw new InvalidDataException("UI object graph exceeds 8192 component operations.");

        foreach (var node in nodes)
        {
            if (node.ParentId != 0 && !byId.ContainsKey(node.ParentId))
                throw new InvalidDataException($"UI object node {node.Id} references missing parent {node.ParentId}.");
            var cursor = node;
            for (var depth = 0; cursor.ParentId != 0; ++depth)
            {
                if (depth >= nodes.Count || cursor.ParentId == node.Id)
                    throw new InvalidDataException($"UI object graph contains a cycle at node {node.Id}.");
                cursor = byId[cursor.ParentId];
            }
        }
    }

    private static void ValidateComponentOperation(
        PcCompatUiObjectNode node,
        PcCompatUiComponentOperation operation)
    {
        if (!Enum.IsDefined(operation.OpCode))
            throw new InvalidDataException($"UI object node {node.Id} has unsupported component op {operation.OpCode}.");
        var required = operation.OpCode switch
        {
            PcCompatUiComponentOpCode.SetRect or
            PcCompatUiComponentOpCode.SetAnchors or
            PcCompatUiComponentOpCode.SetPivot or
            PcCompatUiComponentOpCode.SetLocalScale => PcCompatUiComponentMask.RectTransform,
            PcCompatUiComponentOpCode.SetCanvasRenderMode or
            PcCompatUiComponentOpCode.SetCanvasSortingOrder => PcCompatUiComponentMask.Canvas,
            PcCompatUiComponentOpCode.SetCanvasScaleMode or
            PcCompatUiComponentOpCode.SetCanvasReferenceResolution or
            PcCompatUiComponentOpCode.SetCanvasMatch => PcCompatUiComponentMask.CanvasScaler,
            PcCompatUiComponentOpCode.SetGraphicColor or
            PcCompatUiComponentOpCode.SetGraphicRaycastTarget =>
                PcCompatUiComponentMask.Image |
                PcCompatUiComponentMask.RawImage |
                PcCompatUiComponentMask.TextMeshProUGUI,
            PcCompatUiComponentOpCode.SetText or
            PcCompatUiComponentOpCode.SetTextFontSize or
            PcCompatUiComponentOpCode.SetTextAlignment or
            PcCompatUiComponentOpCode.SetTextRichText or
            PcCompatUiComponentOpCode.SetTextLineSpacing => PcCompatUiComponentMask.TextMeshProUGUI,
            PcCompatUiComponentOpCode.SetContentSizeHorizontalFit or
            PcCompatUiComponentOpCode.SetContentSizeVerticalFit => PcCompatUiComponentMask.ContentSizeFitter,
            _ => PcCompatUiComponentMask.None
        };
        if (required != PcCompatUiComponentMask.None &&
            (node.Components & required) == 0)
            throw new InvalidDataException(
                $"UI object node {node.Id} op {operation.OpCode} has no compatible component.");
    }

    private static void ValidateResourceBindings(
        IReadOnlyList<PcCompatUiObjectNode> nodes,
        IReadOnlyList<PcCompatUiResourceBinding> resources)
    {
        if (resources.Count > 4096)
            throw new InvalidDataException("UI resource bindings exceed 4096 records.");
        var byId = nodes.ToDictionary(node => node.Id);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            if (!byId.TryGetValue(resource.NodeId, out var node) ||
                !Enum.IsDefined(resource.Target) ||
                string.IsNullOrWhiteSpace(resource.FeatureGroupId) ||
                string.IsNullOrWhiteSpace(resource.AssetName) ||
                string.IsNullOrWhiteSpace(resource.ExpectedType) ||
                resource.FeatureGroupId.Length > 256 ||
                resource.AssetName.Length > 4096 ||
                resource.ExpectedType.Length > 1024 ||
                !identities.Add(resource.NodeId + "\0" + (uint)resource.Target))
                throw new InvalidDataException("UI resource binding is incomplete, duplicated or references a missing node.");
            ValidateResourceTarget(resource.NodeId, node.Components, resource.Target, resource.ExpectedType);
        }
    }

    private static void ValidateResourceTarget(
        uint nodeId,
        PcCompatUiComponentMask components,
        PcCompatUiResourceTarget target,
        string expectedType)
    {
        var required = target switch
        {
            PcCompatUiResourceTarget.ImageSprite => PcCompatUiComponentMask.Image,
            PcCompatUiResourceTarget.RawImageTexture => PcCompatUiComponentMask.RawImage,
            PcCompatUiResourceTarget.GraphicMaterial =>
                PcCompatUiComponentMask.Image |
                PcCompatUiComponentMask.RawImage |
                PcCompatUiComponentMask.TextMeshProUGUI,
            PcCompatUiResourceTarget.TextFont or
            PcCompatUiResourceTarget.TextFontSharedMaterial or
            PcCompatUiResourceTarget.TextFontMaterial => PcCompatUiComponentMask.TextMeshProUGUI,
            _ => PcCompatUiComponentMask.None
        };
        var expectedSimple = expectedType.Split(',', 2)[0].Trim();
        expectedSimple = expectedSimple[(expectedSimple.LastIndexOf('.') + 1)..];
        var typeValid = target switch
        {
            PcCompatUiResourceTarget.ImageSprite => expectedSimple.Equals("Sprite", StringComparison.OrdinalIgnoreCase),
            PcCompatUiResourceTarget.RawImageTexture =>
                expectedSimple.Equals("Texture", StringComparison.OrdinalIgnoreCase) ||
                expectedSimple.Equals("Texture2D", StringComparison.OrdinalIgnoreCase),
            PcCompatUiResourceTarget.GraphicMaterial or
            PcCompatUiResourceTarget.TextFontSharedMaterial or
            PcCompatUiResourceTarget.TextFontMaterial =>
                expectedSimple.Equals("Material", StringComparison.OrdinalIgnoreCase),
            PcCompatUiResourceTarget.TextFont =>
                expectedSimple.Equals("TMP_FontAsset", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        if (required == PcCompatUiComponentMask.None ||
            (components & required) == 0 || !typeValid)
            throw new InvalidDataException(
                $"UI resource target {target} is incompatible with node {nodeId} or type {expectedType}.");
    }

    private static void AppendLifecycleRecord(
        List<byte> output,
        uint idOffset,
        uint runtimeRuleId,
        uint trigger,
        uint clockDomain,
        uint flags,
        uint programStart,
        uint programCount,
        uint instructionBudget,
        uint commandType,
        uint targetId,
        long initialDelayNs,
        long deferredRetryDelayNs)
    {
        AppendUInt32(output, idOffset);
        AppendUInt32(output, runtimeRuleId);
        AppendUInt32(output, trigger);
        AppendUInt32(output, clockDomain);
        AppendUInt32(output, flags);
        AppendUInt32(output, programStart);
        AppendUInt32(output, programCount);
        AppendUInt32(output, instructionBudget);
        AppendUInt32(output, commandType);
        AppendUInt32(output, targetId);
        AppendUInt64(output, unchecked((ulong)initialDelayNs));
        AppendUInt64(output, unchecked((ulong)deferredRetryDelayNs));
    }

    private static void AppendVmInstruction(
        List<byte> output,
        PcCompatNativeVmInstruction instruction)
    {
        output.Add((byte)instruction.Opcode);
        output.Add(instruction.Destination);
        output.Add(instruction.Source0);
        output.Add(instruction.Source1);
        AppendUInt32(output, unchecked((uint)instruction.Immediate));
        AppendUInt64(output, unchecked((ulong)instruction.Payload));
    }

    private static void ValidateLifecycle(PcCompatUiLifecycleProgram lifecycle)
    {
        if (string.IsNullOrWhiteSpace(lifecycle.Id))
            throw new InvalidDataException("UI lifecycle id is empty.");
        if (lifecycle.RuntimeRuleId == 0 || lifecycle.CommandType == 0)
            throw new InvalidDataException($"UI lifecycle {lifecycle.Id} has an invalid runtime rule/command id.");
        if (!Enum.IsDefined(lifecycle.Trigger) || !Enum.IsDefined(lifecycle.ClockDomain))
            throw new InvalidDataException($"UI lifecycle {lifecycle.Id} has an unsupported trigger or clock domain.");
        const PcCompatUiLifecycleFlags knownFlags =
            PcCompatUiLifecycleFlags.AllowAnchorExtrapolation |
            PcCompatUiLifecycleFlags.RequireInputSnapshot |
            PcCompatUiLifecycleFlags.RequireClockAnchor;
        if ((lifecycle.Flags & ~knownFlags) != 0)
            throw new InvalidDataException($"UI lifecycle {lifecycle.Id} has unsupported flags.");
        if (lifecycle.InstructionBudget == 0 || lifecycle.InstructionBudget > MaxInstructionBudget ||
            lifecycle.Instructions.Count == 0 ||
            lifecycle.Instructions.Count > 4096)
            throw new InvalidDataException($"UI lifecycle {lifecycle.Id} has an invalid instruction budget/program size.");
        if (lifecycle.InitialDelayNs < 0 || lifecycle.DeferredRetryDelayNs <= 0)
            throw new InvalidDataException($"UI lifecycle {lifecycle.Id} has an invalid delay.");

        for (var pc = 0; pc < lifecycle.Instructions.Count; ++pc)
        {
            if (!VerifyVmInstruction(lifecycle.Instructions, pc))
                throw new InvalidDataException($"UI lifecycle {lifecycle.Id} has invalid bytecode at pc={pc}.");
        }
    }

    private static bool VerifyVmInstruction(
        IReadOnlyList<PcCompatNativeVmInstruction> program,
        int pc)
    {
        var instruction = program[pc];
        static bool Integer(byte register) => register < 32;
        static bool Float(byte register) => register < 16;
        static bool Predicate(byte register) => register < 16;
        bool Branch()
        {
            var target = (long)pc + instruction.Immediate;
            return target >= 0 && target < program.Count;
        }

        return instruction.Opcode switch
        {
            PcCompatNativeVmOpcode.Nop or
            PcCompatNativeVmOpcode.Return => true,

            PcCompatNativeVmOpcode.LoadConstI64 or
            PcCompatNativeVmOpcode.LoadRealtimeNs or
            PcCompatNativeVmOpcode.LoadInputTotal or
            PcCompatNativeVmOpcode.LoadInputHeldMask or
            PcCompatNativeVmOpcode.LoadTouchLaneHeldMask or
            PcCompatNativeVmOpcode.LoadUnityFrameCount or
            PcCompatNativeVmOpcode.LoadOverlayVisible => Integer(instruction.Destination),

            PcCompatNativeVmOpcode.LoadTouchLaneHeldCount or
            PcCompatNativeVmOpcode.LoadTouchLaneTotalCount =>
                Integer(instruction.Destination) && Integer(instruction.Source0),

            PcCompatNativeVmOpcode.LoadConstF64 or
            PcCompatNativeVmOpcode.LoadInputKps or
            PcCompatNativeVmOpcode.LoadUnityScaledTime or
            PcCompatNativeVmOpcode.LoadUnityTimeScale or
            PcCompatNativeVmOpcode.LoadSongPosition or
            PcCompatNativeVmOpcode.LoadAudioPosition or
            PcCompatNativeVmOpcode.LoadMapPosition => Float(instruction.Destination),

            PcCompatNativeVmOpcode.MoveI64 =>
                Integer(instruction.Destination) && Integer(instruction.Source0),
            PcCompatNativeVmOpcode.MoveF64 =>
                Float(instruction.Destination) && Float(instruction.Source0),

            PcCompatNativeVmOpcode.AddI64 or
            PcCompatNativeVmOpcode.SubI64 or
            PcCompatNativeVmOpcode.MulI64 or
            PcCompatNativeVmOpcode.DivI64 =>
                Integer(instruction.Destination) && Integer(instruction.Source0) && Integer(instruction.Source1),

            PcCompatNativeVmOpcode.AddF64 or
            PcCompatNativeVmOpcode.SubF64 or
            PcCompatNativeVmOpcode.MulF64 or
            PcCompatNativeVmOpcode.DivF64 =>
                Float(instruction.Destination) && Float(instruction.Source0) && Float(instruction.Source1),

            PcCompatNativeVmOpcode.CompareEqualI64 or
            PcCompatNativeVmOpcode.CompareLessI64 =>
                Predicate(instruction.Destination) && Integer(instruction.Source0) && Integer(instruction.Source1),

            PcCompatNativeVmOpcode.CompareEqualF64 or
            PcCompatNativeVmOpcode.CompareLessF64 =>
                Predicate(instruction.Destination) && Float(instruction.Source0) && Float(instruction.Source1),

            PcCompatNativeVmOpcode.NotPredicate =>
                Predicate(instruction.Destination) && Predicate(instruction.Source0),
            PcCompatNativeVmOpcode.AndPredicate or
            PcCompatNativeVmOpcode.OrPredicate =>
                Predicate(instruction.Destination) && Predicate(instruction.Source0) && Predicate(instruction.Source1),

            PcCompatNativeVmOpcode.Branch => Branch(),
            PcCompatNativeVmOpcode.BranchIf => Predicate(instruction.Source0) && Branch(),
            _ => false
        };
    }

    private static byte[] EncodeUInt32Array(IReadOnlyList<uint> values)
    {
        var bytes = new byte[checked(values.Count * sizeof(uint))];
        for (var index = 0; index < values.Count; ++index)
            WriteUInt32(bytes, index * sizeof(uint), values[index]);
        return bytes;
    }

    private static uint SectionCountFor(SectionType type, int size)
        => type switch
        {
            SectionType.StringTable => 1,
            SectionType.ParameterRefs => checked((uint)(size / sizeof(uint))),
            SectionType.Targets => checked((uint)(size / TargetRecordSize)),
            SectionType.Rules => checked((uint)(size / RuleRecordSize)),
            SectionType.ObjectGraph => checked((uint)(size / ObjectRecordSize)),
            SectionType.ComponentOps => checked((uint)(size / ComponentOpRecordSize)),
            SectionType.Lifecycle => checked((uint)(size / LifecycleRecordSize)),
            SectionType.Bytecode => checked((uint)(size / VmInstructionSize)),
            SectionType.Resources => checked((uint)(size / ResourceRecordSize)),
            _ => 0
        };

    private static uint SectionElementSize(SectionType type)
        => type switch
        {
            SectionType.StringTable => 1,
            SectionType.ParameterRefs => sizeof(uint),
            SectionType.Targets => TargetRecordSize,
            SectionType.Rules => RuleRecordSize,
            SectionType.ObjectGraph => ObjectRecordSize,
            SectionType.ComponentOps => ComponentOpRecordSize,
            SectionType.Lifecycle => LifecycleRecordSize,
            SectionType.Bytecode => VmInstructionSize,
            SectionType.Resources => ResourceRecordSize,
            _ => 0
        };

    public static byte[] ComputeSourceAssemblySha256(PcModManifest manifest)
    {
        var sourcePath = manifest.JAModAssemblyFullPath;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            sourcePath = manifest.EntryAssemblyPath;

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return new byte[32];

        using var stream = File.OpenRead(sourcePath);
        return SHA256.HashData(stream);
    }

    private static int Align4(int value) => (value + 3) & ~3;

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        const uint polynomial = 0xEDB88320u;
        var crc = 0xFFFFFFFFu;
        for (var index = 0; index < data.Length; ++index)
        {
            var value = index is >= 84 and < 88 ? (byte)0 : data[index];
            crc ^= value;
            for (var bit = 0; bit < 8; ++bit)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ polynomial : crc >> 1;
        }

        return ~crc;
    }

    private static void AppendUInt16(List<byte> output, ushort value)
    {
        output.Add((byte)value);
        output.Add((byte)(value >> 8));
    }

    private static void AppendUInt32(List<byte> output, uint value)
    {
        output.Add((byte)value);
        output.Add((byte)(value >> 8));
        output.Add((byte)(value >> 16));
        output.Add((byte)(value >> 24));
    }

    private static void AppendUInt64(List<byte> output, ulong value)
    {
        AppendUInt32(output, (uint)value);
        AppendUInt32(output, (uint)(value >> 32));
    }

    private static void WriteUInt16(byte[] output, int offset, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(offset, sizeof(ushort)), value);

    private static void WriteUInt32(byte[] output, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset, sizeof(uint)), value);

    private static void WriteUInt64(byte[] output, int offset, ulong value)
        => BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset, sizeof(ulong)), value);

    private readonly record struct SectionEntry(
        uint Type,
        uint Offset,
        uint Size,
        uint Count,
        uint ElementSize);

    private sealed class StringTableBuilder
    {
        private readonly List<byte> _bytes = new() { 0 };
        private readonly Dictionary<string, uint> _offsets = new(StringComparer.Ordinal);

        public uint Add(string? value)
        {
            value ??= string.Empty;
            if (value.IndexOf('\0') >= 0)
                throw new InvalidDataException("Recipe strings may not contain NUL characters.");
            if (_offsets.TryGetValue(value, out var existing))
                return existing;

            var offset = checked((uint)_bytes.Count);
            _bytes.AddRange(Encoding.UTF8.GetBytes(value));
            _bytes.Add(0);
            _offsets.Add(value, offset);
            return offset;
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}

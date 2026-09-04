using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace Xphorror.PcModCompat;

public sealed class PcCompatKeyViewerBehaviorScanIssue
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? AssemblyPath { get; init; }
    public string? Method { get; init; }
}

public sealed class PcCompatKeyViewerBehaviorScanResult
{
    public PcCompatKeyViewerAdapterDocument? Adapter { get; init; }
    public IReadOnlyList<PcCompatKeyViewerBehaviorScanIssue> Issues { get; init; } =
        Array.Empty<PcCompatKeyViewerBehaviorScanIssue>();

    public bool HasCandidate => Adapter is { Features.Count: > 0 };
}

/// <summary>
/// Recovers KeyViewer candidates from observable IL behavior. Type names are retained
/// as evidence and role identities, but never used as the feature seed.
/// </summary>
public static class PcCompatKeyViewerBehaviorScanner
{
    public const string CurrentAnalyzerVersion = "keyviewer-behavior-scan-v7-parameter-provider-transaction";
    private const string PackageFingerprintVersion = "keyviewer-package-fingerprint-v1";
    private const string ProxySurfaceFingerprintVersion = "keyviewer-proxy-surface-fingerprint-v1";

    public static PcCompatKeyViewerBehaviorScanResult Scan(
        PcModManifest manifest,
        string proxySurfaceHash,
        IReadOnlyList<PcCompatManagedAssemblyDescriptor>? assemblies = null,
        int targetGameRevision = PcCompatStaticPatchScanner.DefaultTargetGameRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        RequireSha256(proxySurfaceHash, nameof(proxySurfaceHash));
        if (targetGameRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetGameRevision));

        assemblies ??= PcCompatManagedAssemblyCatalog.Discover(manifest);
        var issues = new List<PcCompatKeyViewerBehaviorScanIssue>();
        var images = new List<AssemblyImage>();
        foreach (var assembly in assemblies.OrderBy(value => value.AssemblyName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                images.Add(ReadAssembly(assembly, cancellationToken));
            }
            catch (Exception exception) when (exception is IOException or BadImageFormatException)
            {
                issues.Add(new PcCompatKeyViewerBehaviorScanIssue
                {
                    Code = "AssemblyScanFailed",
                    Message = $"{exception.GetType().Name}: {exception.Message}",
                    AssemblyPath = assembly.InputPath
                });
            }
        }

        var methods = images.SelectMany(image => image.Methods).ToArray();
        if (methods.Length == 0)
            return new PcCompatKeyViewerBehaviorScanResult { Issues = issues };

        var graph = BuildBehaviorGraph(methods);
        var seeds = FindInputSeeds(methods);
        if (seeds.Count == 0)
            return new PcCompatKeyViewerBehaviorScanResult { Issues = issues };

        var features = BuildFeatures(manifest, methods, graph, seeds, issues);
        if (features.Count == 0)
            return new PcCompatKeyViewerBehaviorScanResult { Issues = issues };

        var fingerprints = images
            .Select(image => new PcCompatAdapterAssemblyFingerprint
            {
                AssemblyName = image.AssemblyName,
                Sha256 = image.Sha256,
                Mvid = image.Mvid
            })
            .OrderBy(value => value.AssemblyName, StringComparer.Ordinal)
            .ToArray();
        var document = new PcCompatKeyViewerAdapterDocument
        {
            ModId = manifest.Id,
            PackageSha256 = ComputeBehaviorPackageHash(manifest, fingerprints),
            TargetGameRevision = targetGameRevision,
            ProxySurfaceHash = proxySurfaceHash.ToLowerInvariant(),
            Assemblies = fingerprints,
            Features = features
        };
        var validation = PcCompatKeyViewerAdapterValidator.Validate(document);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                issues.Add(new PcCompatKeyViewerBehaviorScanIssue
                {
                    Code = "GeneratedAdapterInvalid",
                    Message = error
                });
            }
            return new PcCompatKeyViewerBehaviorScanResult { Issues = issues };
        }

        return new PcCompatKeyViewerBehaviorScanResult
        {
            Adapter = document,
            Issues = issues
        };
    }

    public static string ComputeProxySurfaceHash(
        IEnumerable<string> proxyAssemblyPaths,
        CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder(ProxySurfaceFingerprintVersion).Append('\n');
        foreach (var path in proxyAssemblyPaths
                     .Select(Path.GetFullPath)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append(Path.GetFileName(path).ToLowerInvariant()).Append('|');
            builder.Append(HashFile(path)).Append('\n');
        }
        return HashUtf8(builder.ToString());
    }

    private static AssemblyImage ReadAssembly(
        PcCompatManagedAssemblyDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        using var stream = File.Open(
            descriptor.InputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        stream.Position = 0;
        using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!peReader.HasMetadata)
            throw new BadImageFormatException("Assembly has no managed metadata.", descriptor.InputPath);

        var reader = peReader.GetMetadataReader();
        var module = reader.GetModuleDefinition();
        var mvid = reader.GetGuid(module.Mvid).ToString("D");
        var methods = new List<MethodNode>();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = reader.GetTypeDefinition(typeHandle);
            var typeName = PcCompatMetadataNames.GetTypeFullName(reader, typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                var token = MetadataTokens.GetToken(methodHandle);
                var methodName = reader.GetString(method.Name);
                var parameterTypes = PcCompatMetadataNames.GetMethodParameterTypes(reader, methodHandle);
                var node = new MethodNode(
                    descriptor.AssemblyName,
                    descriptor.InputPath,
                    typeName,
                    methodName,
                    token,
                    (method.Attributes & MethodAttributes.Static) != 0,
                    parameterTypes,
                    PcCompatMetadataNames.GetMethodReturnType(reader, token));

                if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                {
                    var import = method.GetImport();
                    node.ImportModule = import.Module.IsNil
                        ? string.Empty
                        : reader.GetString(reader.GetModuleReference(import.Module).Name);
                    node.ImportName = import.Name.IsNil ? methodName : reader.GetString(import.Name);
                }

                if (method.RelativeVirtualAddress != 0)
                {
                    var instructions = PcCompatIlDecoder.Decode(
                        peReader.GetMethodBody(method.RelativeVirtualAddress));
                    node.Instructions = instructions;
                    AnalyzeInstructions(reader, node, instructions);
                    AnalyzeIndexedArrayLoops(reader, node, instructions);
                    AnalyzeIdentityTransform(reader, node, instructions);
                    AnalyzeArrayParameterInputTransactions(node, instructions);
                    AnalyzeArrayFieldProviderAssignments(node, instructions);
                }
                methods.Add(node);
            }
        }

        return new AssemblyImage(descriptor.AssemblyName, sha256, mvid, methods);
    }

    private static void AnalyzeInstructions(
        MetadataReader reader,
        MethodNode node,
        IReadOnlyList<PcCompatIlInstruction> instructions)
    {
        for (var index = 0; index < instructions.Count; index++)
        {
            var instruction = instructions[index];
            var opCode = instruction.OpCode;
            if (opCode.FlowControl == FlowControl.Cond_Branch)
                node.HasConditionalBranch = true;
            if (TryGetBranchTargets(instruction, out var targets) &&
                targets.Any(target => target <= instruction.Offset))
            {
                node.HasBackEdge = true;
            }
            if (opCode == OpCodes.Add || opCode == OpCodes.Add_Ovf || opCode == OpCodes.Add_Ovf_Un)
                node.HasAdd = true;
            if (opCode == OpCodes.Stelem_I1)
                node.HasBooleanArrayWrite = true;
            if (opCode.Name?.StartsWith("stelem", StringComparison.Ordinal) == true)
                node.HasArrayWrite = true;
            if (TryGetI4Constant(instruction, out var constant))
            {
                node.HasZeroConstant |= constant == 0;
                node.LastI4Constant = constant;
            }

            if (opCode == OpCodes.Call || opCode == OpCodes.Callvirt || opCode == OpCodes.Newobj)
            {
                var identity = PcCompatMetadataNames.GetMethodIdentity(reader, instruction.MetadataToken);
                if (!identity.IsEmpty)
                {
                    node.Calls.Add(new CallSite(
                        identity.DeclaringType,
                        identity.Name,
                        PcCompatMetadataNames.GetMethodParameterTypes(reader, instruction.MetadataToken),
                        PcCompatMetadataNames.GetMethodReturnType(reader, instruction.MetadataToken),
                        instruction.Offset,
                        index > 0 && TryGetI4Constant(instructions[index - 1], out var argument)
                            ? argument
                            : null));
                }
            }
            else if (opCode == OpCodes.Ldfld || opCode == OpCodes.Ldsfld ||
                     opCode == OpCodes.Ldflda || opCode == OpCodes.Ldsflda ||
                     opCode == OpCodes.Stfld || opCode == OpCodes.Stsfld)
            {
                var identity = PcCompatMetadataNames.GetFieldIdentity(reader, instruction.MetadataToken);
                if (!identity.IsEmpty)
                {
                    var field = new FieldAccess(
                        identity.DeclaringType,
                        identity.Name,
                        PcCompatMetadataNames.GetFieldType(reader, instruction.MetadataToken));
                    if (opCode == OpCodes.Stfld || opCode == OpCodes.Stsfld)
                    {
                        node.FieldWrites.Add(field);
                        node.FieldWriteSites.Add(new FieldAccessSite(field, instruction.Offset));
                    }
                    else
                    {
                        node.FieldReads.Add(field);
                        node.FieldReadSites.Add(new FieldAccessSite(field, instruction.Offset));
                    }
                }
            }
            else if (opCode == OpCodes.Ldstr)
            {
                var value = PcCompatMetadataNames.GetUserString(reader, instruction.MetadataToken);
                if (!string.IsNullOrEmpty(value))
                    node.Strings.Add(value);
            }
        }
    }

    private static void AnalyzeArrayParameterInputTransactions(
        MethodNode node,
        IReadOnlyList<PcCompatIlInstruction> instructions)
    {
        if (instructions.Count == 0 || !node.HasBackEdge)
            return;

        var instructionIndexByOffset = instructions
            .Select((instruction, index) => (instruction.Offset, index))
            .ToDictionary(value => value.Offset, value => value.index);
        foreach (var inputCall in node.Calls.Where(call =>
                     call.DeclaringType == "UnityEngine.Input" &&
                     call.Name == "GetKey" &&
                     call.ParameterTypes.SequenceEqual(["UnityEngine.KeyCode"]) &&
                     call.ReturnType == "System.Boolean"))
        {
            if (!instructionIndexByOffset.TryGetValue(inputCall.Offset, out var callIndex))
                continue;
            var elementIndex = PreviousValueInstruction(instructions, callIndex - 1);
            var laneIndex = PreviousValueInstruction(instructions, elementIndex - 1);
            var arrayIndex = PreviousValueInstruction(instructions, laneIndex - 1);
            if (elementIndex < 0 || laneIndex < 0 || arrayIndex < 0 ||
                !IsArrayElementAccess(instructions[elementIndex].OpCode) ||
                !TryGetLoadLocalIndex(instructions[laneIndex], out var indexLocal) ||
                !TryGetLoadArgumentIndex(instructions[arrayIndex], out var argumentIndex))
            {
                continue;
            }

            var parameterIndex = argumentIndex - (node.IsStatic ? 0 : 1);
            if (parameterIndex < 0 || parameterIndex >= node.ParameterTypes.Count ||
                node.ParameterTypes[parameterIndex] != "UnityEngine.KeyCode[]" ||
                !TryFindEnclosingLoop(
                    instructions,
                    inputCall.Offset,
                    out var loopStart,
                    out var loopEnd))
            {
                continue;
            }

            var resultStoreIndex = NextMeaningful(instructions, callIndex + 1);
            if (resultStoreIndex < 0 ||
                !TryGetStoreLocalIndex(instructions[resultStoreIndex], out var resultLocal))
            {
                continue;
            }

            var stateWrites = node.FieldWriteSites.Where(site =>
                    site.Offset >= loopStart && site.Offset <= loopEnd &&
                    site.Field.Type == "System.Boolean" &&
                    ValueBeforeFieldStoreIsLocal(
                        instructions,
                        instructionIndexByOffset,
                        site.Offset,
                        resultLocal))
                .ToArray();
            var stateFields = stateWrites.Select(site => site.Field).Distinct().ToArray();
            var stateReadBeforeWrite = stateFields.Any(field =>
                node.FieldReadSites.Any(site =>
                    site.Offset >= loopStart &&
                    site.Offset < stateWrites
                        .Where(write => write.Field == field)
                        .Min(write => write.Offset) &&
                    site.Field == field));
            var transitionProven = stateWrites.Length != 0 && stateReadBeforeWrite &&
                                   instructions.Any(instruction =>
                                       instruction.Offset >= inputCall.Offset &&
                                       instruction.Offset <= loopEnd &&
                                       instruction.OpCode.FlowControl == FlowControl.Cond_Branch);

            var countWrites = node.FieldWriteSites.Where(site =>
                    site.Offset >= loopStart && site.Offset <= loopEnd &&
                    (IsIntegral(site.Field.Type) || IsIntegralArray(site.Field.Type)))
                .Select(site => site.Field)
                .Concat(node.FieldReadSites.Where(site =>
                        site.Offset >= loopStart && site.Offset <= loopEnd &&
                        IsIntegralArray(site.Field.Type))
                    .Select(site => site.Field))
                .Concat(FindArrayFieldsCachedForLoop(
                    node,
                    instructions,
                    instructionIndexByOffset,
                    loopStart,
                    loopEnd,
                    IsIntegralArray))
                .Distinct()
                .ToArray();
            var countProven = instructions.Any(instruction =>
                                  instruction.Offset >= loopStart &&
                                  instruction.Offset <= loopEnd &&
                                  instruction.OpCode is var op &&
                                  (op == OpCodes.Add || op == OpCodes.Add_Ovf ||
                                   op == OpCodes.Add_Ovf_Un)) &&
                              (countWrites.Length != 0 || instructions.Any(instruction =>
                                  instruction.Offset >= loopStart &&
                                  instruction.Offset <= loopEnd &&
                                  instruction.OpCode.Name?.StartsWith(
                                      "stelem",
                                      StringComparison.Ordinal) == true));
            if (!transitionProven || !countProven)
                continue;

            node.ArrayParameterInputTransactions.Add(new ArrayParameterInputTransaction(
                parameterIndex,
                argumentIndex,
                indexLocal,
                resultLocal,
                loopStart,
                loopEnd,
                inputCall,
                stateFields,
                countWrites));
            node.IdentityTransformKind = PcCompatKeyViewerIdentityTransformKind.UnityKeyCodeIdentity;
        }
    }

    private static void AnalyzeArrayFieldProviderAssignments(
        MethodNode node,
        IReadOnlyList<PcCompatIlInstruction> instructions)
    {
        if (node.FieldWriteSites.Count == 0 || node.Calls.Count == 0)
            return;
        var instructionIndexByOffset = instructions
            .Select((instruction, index) => (instruction.Offset, index))
            .ToDictionary(value => value.Offset, value => value.index);
        var callsByOffset = node.Calls.ToDictionary(call => call.Offset);
        foreach (var write in node.FieldWriteSites.Where(site =>
                     site.Field.Type.EndsWith("[]", StringComparison.Ordinal)))
        {
            if (!instructionIndexByOffset.TryGetValue(write.Offset, out var storeIndex))
                continue;
            var valueIndex = PreviousValueInstruction(instructions, storeIndex - 1);
            if (valueIndex < 0 ||
                !callsByOffset.TryGetValue(instructions[valueIndex].Offset, out var provider) ||
                provider.ParameterTypes.Count != 0 ||
                provider.ReturnType != write.Field.Type)
            {
                continue;
            }
            node.ArrayFieldProviderAssignments.Add(new ArrayFieldProviderAssignment(
                write.Field,
                provider,
                write.Offset));
        }
    }

    private static bool ValueBeforeFieldStoreIsLocal(
        IReadOnlyList<PcCompatIlInstruction> instructions,
        IReadOnlyDictionary<int, int> instructionIndexByOffset,
        int storeOffset,
        int localIndex)
    {
        if (!instructionIndexByOffset.TryGetValue(storeOffset, out var storeIndex))
            return false;
        var valueIndex = PreviousValueInstruction(instructions, storeIndex - 1);
        return valueIndex >= 0 &&
               TryGetLoadLocalIndex(instructions[valueIndex], out var loaded) &&
               loaded == localIndex;
    }

    private static bool TryFindEnclosingLoop(
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int containedOffset,
        out int loopStart,
        out int loopEnd)
    {
        var candidates = instructions
            .SelectMany(instruction => TryGetBranchTargets(instruction, out var targets)
                ? targets.Where(target =>
                        target <= containedOffset && instruction.Offset >= containedOffset)
                    .Select(target => (Start: target, End: instruction.Offset))
                : Array.Empty<(int Start, int End)>())
            .OrderBy(value => value.End - value.Start)
            .ThenBy(value => value.Start)
            .ToArray();
        if (candidates.Length == 0)
        {
            loopStart = 0;
            loopEnd = 0;
            return false;
        }
        loopStart = candidates[0].Start;
        loopEnd = candidates[0].End;
        return true;
    }

    private static IEnumerable<FieldAccess> FindArrayFieldsCachedForLoop(
        MethodNode node,
        IReadOnlyList<PcCompatIlInstruction> instructions,
        IReadOnlyDictionary<int, int> instructionIndexByOffset,
        int loopStart,
        int loopEnd,
        Func<string, bool> fieldTypePredicate)
    {
        foreach (var site in node.FieldReadSites.Where(site =>
                     site.Offset < loopEnd && fieldTypePredicate(site.Field.Type)))
        {
            if (!instructionIndexByOffset.TryGetValue(site.Offset, out var readIndex))
                continue;
            var storeIndex = NextMeaningful(instructions, readIndex + 1);
            if (storeIndex < 0 ||
                !TryGetStoreLocalIndex(instructions[storeIndex], out var localIndex))
            {
                continue;
            }
            if (instructions.Any(instruction =>
                    instruction.Offset >= loopStart && instruction.Offset <= loopEnd &&
                    TryGetLoadLocalIndex(instruction, out var loaded) && loaded == localIndex))
            {
                yield return site.Field;
            }
        }
    }

    private static void AnalyzeIndexedArrayLoops(
        MetadataReader reader,
        MethodNode node,
        IReadOnlyList<PcCompatIlInstruction> instructions)
    {
        if (instructions.Count == 0)
            return;

        var callsByOffset = node.Calls.ToDictionary(call => call.Offset);
        var localProviders = new Dictionary<int, List<(int StoreOffset, CallSite Provider)>>();
        for (var index = 0; index < instructions.Count; ++index)
        {
            if (!callsByOffset.TryGetValue(instructions[index].Offset, out var call) ||
                !call.ReturnType.EndsWith("[]", StringComparison.Ordinal))
                continue;
            var storeIndex = NextMeaningful(instructions, index + 1);
            if (storeIndex >= 0 &&
                TryGetStoreLocalIndex(instructions[storeIndex], out var localIndex))
            {
                if (!localProviders.TryGetValue(localIndex, out var providers))
                {
                    providers = [];
                    localProviders.Add(localIndex, providers);
                }
                providers.Add((instructions[storeIndex].Offset, call));
            }
        }

        if (localProviders.Count == 0)
            return;

        for (var branchIndex = 0; branchIndex < instructions.Count; ++branchIndex)
        {
            var branch = instructions[branchIndex];
            if (!TryGetBranchTargets(branch, out var targets))
                continue;
            foreach (var target in targets.Where(target => target <= branch.Offset))
            {
                var bodyStart = FindInstructionAtOrAfter(instructions, target);
                if (bodyStart < 0 || bodyStart >= branchIndex)
                    continue;

                var conditionLocals = new HashSet<int>();
                for (var index = Math.Max(bodyStart, branchIndex - 8);
                     index < branchIndex;
                     ++index)
                {
                    if (TryGetLoadLocalIndex(instructions[index], out var localIndex))
                        conditionLocals.Add(localIndex);
                }

                foreach (var (providerLocal, providerAssignments) in localProviders)
                {
                    var providerCall = providerAssignments
                        .Where(value => value.StoreOffset < target)
                        .OrderByDescending(value => value.StoreOffset)
                        .Select(value => value.Provider)
                        .FirstOrDefault();
                    if (providerCall == null)
                        continue;
                    if (!conditionLocals.Contains(providerLocal))
                        continue;
                    foreach (var indexLocal in conditionLocals.Where(value => value != providerLocal))
                    {
                        var indexedFields = new HashSet<FieldAccess>();
                        var providerReadOffsets = new List<int>();
                        var providerFeedsBooleanQuery = false;
                        for (var index = bodyStart; index < branchIndex; ++index)
                        {
                            if (!TryGetLoadLocalIndex(instructions[index], out var loadedLocal) ||
                                loadedLocal != indexLocal)
                                continue;
                            var previous = PreviousMeaningful(instructions, index - 1);
                            var elementRead = NextMeaningful(instructions, index + 1);
                            if (previous < 0 || elementRead < 0 ||
                                !IsArrayElementAccess(instructions[elementRead].OpCode))
                                continue;

                            if (TryGetLoadLocalIndex(instructions[previous], out var arrayLocal) &&
                                arrayLocal == providerLocal)
                            {
                                providerReadOffsets.Add(instructions[elementRead].Offset);
                                var consumerIndex = NextMeaningful(instructions, elementRead + 1);
                                if (consumerIndex >= 0 &&
                                    callsByOffset.TryGetValue(
                                        instructions[consumerIndex].Offset,
                                        out var consumer) &&
                                    consumer.ReturnType == "System.Boolean")
                                    providerFeedsBooleanQuery = true;
                                continue;
                            }

                            if (instructions[previous].OpCode is var fieldOp &&
                                (fieldOp == OpCodes.Ldfld || fieldOp == OpCodes.Ldsfld ||
                                 fieldOp == OpCodes.Ldflda || fieldOp == OpCodes.Ldsflda))
                            {
                                var identity = PcCompatMetadataNames.GetFieldIdentity(
                                    reader,
                                    instructions[previous].MetadataToken);
                                if (!identity.IsEmpty)
                                {
                                    var field = new FieldAccess(
                                        identity.DeclaringType,
                                        identity.Name,
                                        PcCompatMetadataNames.GetFieldType(
                                            reader,
                                            instructions[previous].MetadataToken));
                                    if (field.Type.EndsWith("[]", StringComparison.Ordinal))
                                        indexedFields.Add(field);
                                }
                            }
                        }

                        if (providerReadOffsets.Count == 0 || indexedFields.Count == 0)
                            continue;
                        node.IndexedArrayLoops.Add(new IndexedArrayLoop(
                            providerCall,
                            providerLocal,
                            indexLocal,
                            target,
                            branch.Offset,
                            providerFeedsBooleanQuery,
                            indexedFields.OrderBy(
                                field => field.DeclaringType + "\0" + field.Name,
                                StringComparer.Ordinal).ToArray(),
                            providerReadOffsets.Order().ToArray()));
                    }
                }
            }
        }
    }

    private static void AnalyzeIdentityTransform(
        MetadataReader reader,
        MethodNode node,
        IReadOnlyList<PcCompatIlInstruction> instructions)
    {
        if (node.ParameterTypes.Count != 1 ||
            node.ParameterTypes[0] is not ("UnityEngine.KeyCode" or "System.Int32") ||
            node.ReturnType != "System.Boolean" ||
            instructions.Count == 0)
            return;

        var parameterArgument = node.IsStatic ? 0 : 1;
        var callsByOffset = node.Calls.ToDictionary(call => call.Offset);
        var directUnityQueryCount = 0;
        var directBridgeCalls = new List<CallSite>();
        var offsetBridgeCalls = new List<CallSite>();
        int? bridgeOffset = null;
        for (var index = 0; index < instructions.Count; ++index)
        {
            if (!callsByOffset.TryGetValue(instructions[index].Offset, out var call))
                continue;
            if (call.DeclaringType == "UnityEngine.Input" &&
                call.Name == "GetKey" &&
                call.ParameterTypes.SequenceEqual(["UnityEngine.KeyCode"]) &&
                call.ReturnType == "System.Boolean" &&
                MatchesDirectArgument(instructions, index, parameterArgument))
            {
                ++directUnityQueryCount;
                continue;
            }
            if (call.ParameterTypes.SequenceEqual(["System.Int32"]) &&
                call.ReturnType == "System.Int16" &&
                MatchesDirectArgument(instructions, index, parameterArgument))
            {
                directBridgeCalls.Add(call);
                continue;
            }
            if (call.ParameterTypes.SequenceEqual(["System.Int32"]) &&
                call.ReturnType == "System.Int16" &&
                TryMatchArgumentMinusConstant(
                    instructions,
                    index,
                    parameterArgument,
                    out var offset))
            {
                offsetBridgeCalls.Add(call);
                bridgeOffset = bridgeOffset is null || bridgeOffset == offset
                    ? offset
                    : int.MinValue;
            }
        }

        var conditionalBranchCount = instructions.Count(instruction =>
            instruction.OpCode.FlowControl == FlowControl.Cond_Branch &&
            instruction.OpCode != OpCodes.Switch);
        if (node.FieldReads.Count != 0 || node.FieldWrites.Count != 0 || node.HasBackEdge)
            return;

        if (directUnityQueryCount == 1 && offsetBridgeCalls.Count == 1 &&
            directBridgeCalls.Count == 0 && node.Calls.Count == 2 &&
            conditionalBranchCount == 1 && bridgeOffset == 0x1000 &&
            HasArgumentThresholdBranch(instructions, parameterArgument, 0x1000))
        {
            node.IdentityTransformKind =
                PcCompatKeyViewerIdentityTransformKind.UnityWindowsThresholdSplit;
            node.IdentityTransformThreshold = 0x1000;
            node.IdentityTransformOffset = 0x1000;
            node.IdentityTransformBridgeCalls.AddRange(offsetBridgeCalls
                .DistinctBy(MethodShape));
            return;
        }
        if (directUnityQueryCount == 1 && directBridgeCalls.Count == 0 &&
            offsetBridgeCalls.Count == 0 && node.Calls.Count == 1 &&
            conditionalBranchCount == 0)
        {
            node.IdentityTransformKind =
                PcCompatKeyViewerIdentityTransformKind.UnityKeyCodeIdentity;
            return;
        }
        if (directUnityQueryCount == 0 && directBridgeCalls.Count == 1 &&
            offsetBridgeCalls.Count == 0 && node.Calls.Count == 1 &&
            conditionalBranchCount == 0)
        {
            node.IdentityTransformKind =
                PcCompatKeyViewerIdentityTransformKind.WindowsVirtualKeyIdentity;
            node.IdentityTransformBridgeCalls.AddRange(directBridgeCalls
                .DistinctBy(MethodShape));
            return;
        }
        if (directUnityQueryCount == 0 && directBridgeCalls.Count == 0 &&
            offsetBridgeCalls.Count == 1 && node.Calls.Count == 1 &&
            conditionalBranchCount == 0 && bridgeOffset is >= 0)
        {
            node.IdentityTransformKind =
                PcCompatKeyViewerIdentityTransformKind.WindowsVirtualKeyOffset;
            node.IdentityTransformOffset = bridgeOffset.Value;
            node.IdentityTransformBridgeCalls.AddRange(offsetBridgeCalls
                .DistinctBy(MethodShape));
        }
    }

    private static BehaviorGraph BuildBehaviorGraph(IReadOnlyList<MethodNode> methods)
    {
        var graph = new BehaviorGraph(methods.Count);
        var byMethod = methods
            .Select((method, index) => (method, index))
            .GroupBy(value => MethodShape(value.method), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(value => value.index).ToArray(),
                StringComparer.Ordinal);

        for (var index = 0; index < methods.Count; index++)
        {
            foreach (var call in methods[index].Calls)
            {
                if (!byMethod.TryGetValue(MethodShape(call), out var targets))
                    continue;
                foreach (var target in targets)
                    graph.Connect(index, target);
            }
        }

        var readers = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var writers = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < methods.Count; index++)
        {
            AddFieldUsers(readers, methods[index].FieldReads, index);
            AddFieldUsers(writers, methods[index].FieldWrites, index);
        }
        foreach (var (field, fieldWriters) in writers)
        {
            if (!readers.TryGetValue(field, out var fieldReaders) ||
                (long)fieldWriters.Count * fieldReaders.Count > 4096)
            {
                continue;
            }
            foreach (var writer in fieldWriters)
            foreach (var reader in fieldReaders)
                graph.Connect(writer, reader);
        }
        return graph;
    }

    private static IReadOnlyList<InputSeed> FindInputSeeds(IReadOnlyList<MethodNode> methods)
    {
        var imported = methods
            .Select((method, index) => (method, index))
            .Where(value => TryClassifyPInvoke(value.method, out _))
            .GroupBy(value => MethodShape(value.method), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(value => value.method).ToArray(),
                StringComparer.Ordinal);
        var seeds = new List<InputSeed>();
        for (var index = 0; index < methods.Count; index++)
        {
            foreach (var call in methods[index].Calls)
            {
                if (TryClassifyInputCall(call, out var kind))
                {
                    seeds.Add(new InputSeed(index, kind, call));
                    continue;
                }
                if (!imported.TryGetValue(MethodShape(call), out var imports))
                    continue;
                foreach (var import in imports)
                {
                    if (TryClassifyPInvoke(import, out kind))
                        seeds.Add(new InputSeed(index, kind, call));
                }
            }
        }
        return seeds
            .GroupBy(seed => $"{seed.MethodIndex}|{seed.Kind}|{seed.Call.Offset}", StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private static IReadOnlyList<CrossMethodArrayProvider> FindCrossMethodArrayProviders(
        IReadOnlyList<MethodNode> methods)
    {
        var byShape = methods
            .GroupBy(MethodShape, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var assignments = methods
            .SelectMany(method => method.ArrayFieldProviderAssignments.Select(assignment =>
                (Method: method, Assignment: assignment)))
            .ToArray();
        var providers = new List<CrossMethodArrayProvider>();
        foreach (var transactionMethod in methods.Where(method =>
                     method.ArrayParameterInputTransactions.Count != 0))
        {
            foreach (var transaction in transactionMethod.ArrayParameterInputTransactions)
            {
                foreach (var caller in methods)
                {
                    foreach (var call in caller.Calls.Where(call =>
                                 MethodShape(call) == MethodShape(transactionMethod)))
                    {
                        if (!TryGetCallArgumentOrigins(
                                caller,
                                call,
                                transactionMethod,
                                byShape,
                                out var origins) ||
                            transaction.ParameterIndex >= origins.Count)
                        {
                            continue;
                        }

                        var origin = origins[transaction.ParameterIndex];
                        var laneBaseParameter = FindLaneBaseParameterIndex(
                            transactionMethod,
                            transaction);
                        var consumerLaneBase = laneBaseParameter.HasValue
                            ? origins[laneBaseParameter.Value].Int32Constant
                            : null;
                        if (origin.Call != null)
                        {
                            AddCrossMethodProvider(
                                providers,
                                byShape,
                                transactionMethod,
                                transaction,
                                caller,
                                origin.Call,
                                null,
                                call.Offset,
                                consumerLaneBase);
                            continue;
                        }
                        if (origin.Field == null)
                            continue;
                        foreach (var assignment in assignments.Where(value =>
                                     value.Assignment.Field == origin.Field))
                        {
                            AddCrossMethodProvider(
                                providers,
                                byShape,
                                transactionMethod,
                                transaction,
                                caller,
                                assignment.Assignment.Provider,
                                origin.Field,
                                call.Offset,
                                consumerLaneBase);
                        }
                    }
                }
            }
        }
        return providers
            .DistinctBy(value =>
                value.TransactionMethod.Id + "\0" + value.Provider.Id + "\0" +
                (value.CacheField?.DeclaringType ?? string.Empty) + "\0" +
                (value.CacheField?.Name ?? string.Empty) + "\0" +
                (value.ConsumerLaneBase?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) ?? "?"))
            .OrderBy(value => value.TransactionMethod.Id, StringComparer.Ordinal)
            .ThenBy(value => value.Provider.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddCrossMethodProvider(
        ICollection<CrossMethodArrayProvider> destination,
        IReadOnlyDictionary<string, MethodNode[]> byShape,
        MethodNode transactionMethod,
        ArrayParameterInputTransaction transaction,
        MethodNode caller,
        CallSite providerCall,
        FieldAccess? cacheField,
        int consumerCallOffset,
        int? consumerLaneBase)
    {
        if (!byShape.TryGetValue(MethodShape(providerCall), out var candidates))
            return;
        foreach (var provider in candidates.Where(candidate =>
                     candidate.ParameterTypes.Count == 0 &&
                     candidate.ReturnType == transactionMethod.ParameterTypes[transaction.ParameterIndex]))
        {
            destination.Add(new CrossMethodArrayProvider(
                transactionMethod,
                transaction,
                caller,
                provider,
                cacheField,
                consumerCallOffset,
                consumerLaneBase));
        }
    }

    private static int? FindLaneBaseParameterIndex(
        MethodNode method,
        ArrayParameterInputTransaction transaction)
    {
        var candidates = new HashSet<int>();
        var instructions = method.Instructions;
        for (var index = 0; index < instructions.Count; ++index)
        {
            var instruction = instructions[index];
            if (instruction.Offset < transaction.LoopStartOffset ||
                instruction.Offset > transaction.LoopEndOffset ||
                instruction.OpCode is var op &&
                op != OpCodes.Add && op != OpCodes.Add_Ovf && op != OpCodes.Add_Ovf_Un)
            {
                continue;
            }

            var right = PreviousValueInstruction(instructions, index - 1);
            var left = PreviousValueInstruction(instructions, right - 1);
            if (left < 0 || right < 0)
                continue;
            if (TryGetLoadLocalIndex(instructions[left], out var leftLocal) &&
                leftLocal == transaction.IndexLocal &&
                TryGetLoadArgumentIndex(instructions[right], out var rightArgument))
            {
                AddLaneBaseParameter(method, transaction, rightArgument, candidates);
            }
            else if (TryGetLoadArgumentIndex(instructions[left], out var leftArgument) &&
                     TryGetLoadLocalIndex(instructions[right], out var rightLocal) &&
                     rightLocal == transaction.IndexLocal)
            {
                AddLaneBaseParameter(method, transaction, leftArgument, candidates);
            }
        }
        return candidates.Count == 1 ? candidates.Single() : null;
    }

    private static void AddLaneBaseParameter(
        MethodNode method,
        ArrayParameterInputTransaction transaction,
        int argumentIndex,
        ISet<int> candidates)
    {
        var parameterIndex = argumentIndex - (method.IsStatic ? 0 : 1);
        if (parameterIndex < 0 || parameterIndex >= method.ParameterTypes.Count ||
            parameterIndex == transaction.ParameterIndex ||
            method.ParameterTypes[parameterIndex] != "System.Int32")
        {
            return;
        }
        candidates.Add(parameterIndex);
    }

    private static bool TryGetCallArgumentOrigins(
        MethodNode caller,
        CallSite call,
        MethodNode target,
        IReadOnlyDictionary<string, MethodNode[]> byShape,
        out IReadOnlyList<ValueOrigin> origins)
    {
        origins = Array.Empty<ValueOrigin>();
        if (caller.Instructions.Count == 0)
            return false;
        var callIndex = FindInstructionAtOrAfter(caller.Instructions, call.Offset);
        if (callIndex < 0 || caller.Instructions[callIndex].Offset != call.Offset)
            return false;
        var cursor = PreviousValueInstruction(caller.Instructions, callIndex - 1);
        var values = new ValueOrigin[target.ParameterTypes.Count];
        for (var parameter = target.ParameterTypes.Count - 1; parameter >= 0; --parameter)
        {
            if (!TryReadValueOrigin(caller, byShape, ref cursor, 0, out values[parameter]))
                return false;
        }
        if (!target.IsStatic &&
            !TryReadValueOrigin(caller, byShape, ref cursor, 0, out _))
        {
            return false;
        }
        origins = values;
        return true;
    }

    private static bool TryReadValueOrigin(
        MethodNode method,
        IReadOnlyDictionary<string, MethodNode[]> byShape,
        ref int cursor,
        int depth,
        out ValueOrigin origin)
    {
        origin = default;
        if (depth > 24)
            return false;
        cursor = PreviousValueInstruction(method.Instructions, cursor);
        if (cursor < 0)
            return false;
        var instruction = method.Instructions[cursor--];
        var opCode = instruction.OpCode;
        if (opCode == OpCodes.Ldfld || opCode == OpCodes.Ldflda)
        {
            var site = method.FieldReadSites.FirstOrDefault(value => value.Offset == instruction.Offset);
            if (site == null ||
                !TryReadValueOrigin(method, byShape, ref cursor, depth + 1, out _))
            {
                return false;
            }
            origin = new ValueOrigin(site.Field, null, null);
            return true;
        }
        if (opCode == OpCodes.Ldsfld || opCode == OpCodes.Ldsflda)
        {
            var site = method.FieldReadSites.FirstOrDefault(value => value.Offset == instruction.Offset);
            if (site == null)
                return false;
            origin = new ValueOrigin(site.Field, null, null);
            return true;
        }
        if (opCode == OpCodes.Call || opCode == OpCodes.Callvirt)
        {
            var call = method.Calls.FirstOrDefault(value => value.Offset == instruction.Offset);
            if (call == null)
                return false;
            var target = byShape.TryGetValue(MethodShape(call), out var candidates) &&
                         candidates.Length == 1
                ? candidates[0]
                : null;
            var parameterCount = target?.ParameterTypes.Count ?? call.ParameterTypes.Count;
            for (var parameter = parameterCount - 1; parameter >= 0; --parameter)
            {
                if (!TryReadValueOrigin(method, byShape, ref cursor, depth + 1, out _))
                    return false;
            }
            var isStatic = target?.IsStatic ?? opCode == OpCodes.Call;
            if (!isStatic &&
                !TryReadValueOrigin(method, byShape, ref cursor, depth + 1, out _))
            {
                return false;
            }
            origin = new ValueOrigin(
                null,
                call,
                target != null && TryReadPureInt32Constant(target, out var getterConstant)
                    ? getterConstant
                    : null);
            return true;
        }
        if (IsArrayElementAccess(opCode))
        {
            if (!TryReadValueOrigin(method, byShape, ref cursor, depth + 1, out _) ||
                !TryReadValueOrigin(method, byShape, ref cursor, depth + 1, out origin))
            {
                return false;
            }
            return true;
        }
        if (opCode == OpCodes.Add || opCode == OpCodes.Add_Ovf ||
            opCode == OpCodes.Add_Ovf_Un || opCode == OpCodes.Sub ||
            opCode == OpCodes.Mul || opCode == OpCodes.Div || opCode == OpCodes.Div_Un)
        {
            return TryReadValueOrigin(method, byShape, ref cursor, depth + 1, out _) &&
                   TryReadValueOrigin(method, byShape, ref cursor, depth + 1, out origin);
        }
        if (TryGetI4Constant(instruction, out var constant))
        {
            origin = new ValueOrigin(null, null, constant);
            return true;
        }
        if (TryGetLoadArgumentIndex(instruction, out _) ||
            TryGetLoadLocalIndex(instruction, out _) ||
            opCode == OpCodes.Ldnull || opCode == OpCodes.Ldstr ||
            opCode == OpCodes.Ldc_I8 || opCode == OpCodes.Ldc_R4 || opCode == OpCodes.Ldc_R8)
        {
            origin = default;
            return true;
        }
        return false;
    }

    private static bool TryReadPureInt32Constant(MethodNode method, out int value)
    {
        value = 0;
        if (!method.IsStatic || method.ParameterTypes.Count != 0 ||
            method.ReturnType != "System.Int32" || method.Calls.Count != 0 ||
            method.FieldReads.Count != 0 || method.FieldWrites.Count != 0 ||
            method.HasConditionalBranch || method.HasBackEdge)
        {
            return false;
        }

        var meaningful = method.Instructions
            .Where(instruction => instruction.OpCode != OpCodes.Nop)
            .ToArray();
        return meaningful.Length == 2 &&
               TryGetI4Constant(meaningful[0], out value) &&
               meaningful[1].OpCode == OpCodes.Ret;
    }

    private static IReadOnlyList<PcCompatKeyViewerFeatureAdapter> BuildFeatures(
        PcModManifest manifest,
        IReadOnlyList<MethodNode> methods,
        BehaviorGraph graph,
        IReadOnlyList<InputSeed> seeds,
        ICollection<PcCompatKeyViewerBehaviorScanIssue> issues)
    {
        var groups = seeds
            .GroupBy(seed => graph.Find(seed.MethodIndex))
            .OrderBy(group => methods[group.Min(seed => seed.MethodIndex)].Id, StringComparer.Ordinal)
            .ToArray();
        var features = new List<PcCompatKeyViewerFeatureAdapter>();
        foreach (var group in groups)
        {
            var component = Enumerable.Range(0, methods.Count)
                .Where(index => graph.Find(index) == group.Key)
                .Select(index => methods[index])
                .ToArray();
            if (component.Length > 2048)
            {
                issues.Add(new PcCompatKeyViewerBehaviorScanIssue
                {
                    Code = "CandidateGraphTooLarge",
                    Message = $"Input candidate graph contains {component.Length} methods and was rejected.",
                    Method = methods[group.First().MethodIndex].Id
                });
                continue;
            }
            var hasPresentation = component.Any(UsesPresentationSink);
            var hasStatefulInput = component.Any(HasTransitionWrite) ||
                                   component.Any(HasCountWrite) ||
                                   component.Any(HasRainEvidence);
            if (!hasPresentation || !hasStatefulInput)
            {
                issues.Add(new PcCompatKeyViewerBehaviorScanIssue
                {
                    Code = "CandidateClosureIncomplete",
                    Message = "Input use was found without a connected state transition/count/rain and presentation sink; no KeyViewer feature was emitted.",
                    Method = methods[group.First().MethodIndex].Id
                });
                continue;
            }
            features.Add(BuildFeature(manifest, component, group.ToArray(), features.Count + 1));
        }
        return features;
    }

    private static PcCompatKeyViewerFeatureAdapter BuildFeature(
        PcModManifest manifest,
        IReadOnlyList<MethodNode> component,
        IReadOnlyList<InputSeed> seeds,
        int ordinal)
    {
        var sourceProfiles = seeds
            .GroupBy(seed => seed.Kind)
            .OrderBy(group => group.Key)
            .Select((group, index) => new PcCompatKeyViewerSourceProfile
            {
                Id = $"source-{index + 1}-{group.Key.ToString().ToLowerInvariant()}",
                Kind = group.Key,
                EntryPoints = group
                    .Select(seed => component.FirstOrDefault(method =>
                        method.Calls.Contains(seed.Call))?.Id ?? seed.Call.DisplayName)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
                Evidence = BuildSourceEvidence(group.Key, group
                    .Select(seed => $"{seed.Call.DisplayName}@IL_{seed.Call.Offset:X4}"))
            })
            .ToArray();
        var defaultSource = sourceProfiles[0].Id;
        var constants = seeds
            .Where(seed => seed.Call.ConstantArgument.HasValue)
            .Select(seed => (seed.Kind, Value: seed.Call.ConstantArgument!.Value))
            .Distinct()
            .OrderBy(value => value.Kind)
            .ThenBy(value => value.Value)
            .ToArray();
        var indexedLoops = component
            .SelectMany(method => method.IndexedArrayLoops.Select(loop => (Method: method, Loop: loop)))
            .GroupBy(value => $"{value.Method.Id}|{value.Loop.Provider.DisplayName}|{value.Loop.LoopStartOffset:X4}|{value.Loop.IndexLocal}", StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var crossMethodProviders = FindCrossMethodArrayProviders(component);
        var transactionMethods = crossMethodProviders
            .Select(value => value.TransactionMethod)
            .Distinct()
            .OrderBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        var laneEvidence = crossMethodProviders.Count != 0
            ? Proven(crossMethodProviders.Select(value =>
                $"{value.Provider.Id} -> " +
                (value.CacheField == null
                    ? "direct argument"
                    : value.CacheField.DeclaringType + "." + value.CacheField.Name) +
                $" -> {value.TransactionMethod.Id} parameter {value.Transaction.ParameterIndex}; " +
                $"same-index Unity KeyCode query IL_{value.Transaction.InputCall.Offset:X4}"))
            : constants.Length != 0
            ? Proven(constants.Select(value => $"constant input identity {value.Kind}:{value.Value}"))
            : indexedLoops.Length != 0
                ? Probable(
                    "dynamic array provider and same-index consumers are proven locally; provider selection and cross-thread publication remain unresolved",
                    indexedLoops.Select(value =>
                        $"{value.Method.Id}: {value.Loop.Provider.DisplayName} local {value.Loop.ProviderLocal} indexed by local {value.Loop.IndexLocal} " +
                        $"in IL_{value.Loop.LoopStartOffset:X4}..IL_{value.Loop.LoopEndOffset:X4}; fields=" +
                        string.Join(',', value.Loop.IndexedFields.Select(field => field.DeclaringType + "." + field.Name))))
            : Unresolved(
                PcCompatAdapterEvidenceStatus.Ambiguous,
                "input identity is selected from runtime state; binding provider role is unresolved",
                seeds.Select(seed => seed.Call.DisplayName));
        var lanes = constants.Length != 0
            ? constants.Select((value, index) => new PcCompatKeyViewerLane
            {
                Id = $"lane-{index + 1}",
                DisplayLabel = value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Binding = new PcCompatLaneBinding
                {
                    Kind = PcCompatLaneBindingKind.DirectIdentity,
                    Identities =
                    [
                        new PcCompatInputIdentity
                        {
                            Kind = value.Kind switch
                            {
                                PcCompatKeyViewerInputProfileKind.Win32Polling =>
                                    PcCompatInputIdentityKind.WindowsVirtualKey,
                                PcCompatKeyViewerInputProfileKind.RewiredPolling =>
                                    PcCompatInputIdentityKind.ActionId,
                                _ => PcCompatInputIdentityKind.UnityKeyCode
                            },
                            Value = value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        }
                    ],
                    SourceProfileId = sourceProfiles.First(profile => profile.Kind == value.Kind).Id
                }
            }).ToArray()
            :
            [
                new PcCompatKeyViewerLane
                {
                    Id = "runtime-configured",
                    DisplayLabel = "Configured",
                    Binding = new PcCompatLaneBinding
                    {
                        Kind = PcCompatLaneBindingKind.Wildcard,
                        SourceProfileId = defaultSource
                    }
                }
            ];

        var stateWriters = transactionMethods.Length != 0
            ? transactionMethods
            : component.Where(HasTransitionWrite).ToArray();
        var countWriters = transactionMethods.Length != 0
            ? transactionMethods
            : component.Where(HasCountWrite).ToArray();
        var monotonic = component.Where(UsesMonotonicClock).ToArray();
        var queueMethods = component.Where(UsesQueue).ToArray();
        var presentation = component.Where(UsesPresentationSink).ToArray();
        var persistence = component.Where(UsesPersistence).ToArray();
        var settings = component.Where(UsesSettingsUi).ToArray();
        var rain = component.Where(HasRainEvidence).ToArray();
        var enable = component.Where(method => method.Name is "OnEnable" or "CompatEnable").ToArray();
        var disable = component.Where(method => method.Name is "OnDisable" or "CompatDisable").ToArray();
        var listener = transactionMethods.Length != 0
            ? transactionMethods
            : component.Where(method => method.HasBackEdge &&
                                        (method.Calls.Any(call =>
                                             seeds.Any(seed => MethodShape(seed.Call) == MethodShape(call))) ||
                                         method.FieldReads.Any())).ToArray();
        var importedWin32Shapes = component
            .Where(method => TryClassifyPInvoke(method, out var kind) &&
                             kind == PcCompatKeyViewerInputProfileKind.Win32Polling)
            .Select(MethodShape)
            .ToHashSet(StringComparer.Ordinal);
        var identityTransforms = transactionMethods.Length != 0
            ? transactionMethods.Where(method => method.IdentityTransformKind ==
                PcCompatKeyViewerIdentityTransformKind.UnityKeyCodeIdentity).ToArray()
            : component.Where(method =>
                method.IdentityTransformKind ==
                    PcCompatKeyViewerIdentityTransformKind.UnityKeyCodeIdentity ||
                method.IdentityTransformKind != null &&
                method.IdentityTransformBridgeCalls.Any(call =>
                    importedWin32Shapes.Contains(MethodShape(call)))).ToArray();
        var labelProviders = component.Where(method =>
            method.ReturnType == "System.String[]" &&
            method.ParameterTypes.Count == 0 &&
            method.FieldReads.Any(field => field.Type == "System.String[]")).ToArray();
        var resetCandidates = component.Where(method => method.HasZeroConstant &&
                                                        (method.HasArrayWrite || method.FieldWrites.Count != 0) &&
                                                        UsesSettingsUi(method)).ToArray();
        var persistencePaths = component.SelectMany(method => method.Strings)
            .Where(value => value.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ||
                            value.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        var transitionEvidence = transactionMethods.Length != 0
            ? Proven(transactionMethods.SelectMany(method =>
                method.ArrayParameterInputTransactions.Select(transaction =>
                    $"{method.Id}: Input.GetKey result local {transaction.ResultLocal} is compared with and written to held state inside IL_{transaction.LoopStartOffset:X4}..IL_{transaction.LoopEndOffset:X4}")))
            : stateWriters.Length != 0 &&
                                 component.Any(method => method.HasConditionalBranch)
            ? Probable(
                "conditional transition and held-state write are connected, but CFG control dependence is not lowered",
                stateWriters.Select(method => method.Id))
            : Unsupported("no connected conditional held-state write was found");
        var countEvidence = transactionMethods.Length != 0
            ? Proven(transactionMethods.SelectMany(method =>
                method.ArrayParameterInputTransactions.Select(transaction =>
                    $"{method.Id}: rising-edge transaction increments integral state inside IL_{transaction.LoopStartOffset:X4}..IL_{transaction.LoopEndOffset:X4}")))
            : countWriters.Length != 0
            ? Probable(
                "increment/write behavior is connected to the input component, but lane/count dominance is not lowered",
                countWriters.Select(method => method.Id))
            : Unsupported("no connected incrementing count state was found");
        var kpsEvidence = monotonic.Length != 0 && queueMethods.Length != 0
            ? Probable(
                "monotonic clock and queue window are connected, but expiry-loop semantics are not lowered",
                monotonic.Concat(queueMethods).Select(method => method.Id))
            : Unsupported("no monotonic queue/window behavior was found");
        var presentationEvidence = presentation.Length != 0
            ? Probable(
                "Unity/TMP presentation sink is connected, but object ownership and lifecycle are not closed",
                presentation.Select(method => method.Id))
            : Unsupported("no connected Unity/TMP presentation sink was found");
        var visibilityEvidence = enable.Length != 0 && disable.Length != 0
            ? Probable(
                "enable/disable lifecycle pair found; root object visibility side effects require lowering",
                enable.Concat(disable).Select(method => method.Id))
            : Unsupported("no complete enable/disable visibility lifecycle was found");
        var activationEvidence = transactionMethods.Length != 0
            ? Proven(transactionMethods.Select(method =>
                $"{method.Id}: frame-polled input transaction is bounded by a verified array loop"))
            : listener.Length != 0
            ? Probable(
                "long-running input loop found; loop predicate field and cancellation semantics require lowering",
                listener.Select(method => method.Id))
            : Unsupported("no input loop or event activation predicate was found");
        var persistenceEvidence = persistence.Length != 0 && persistencePaths.Length != 0
            ? Probable(
                "read/write path found; atomicity, backup and save scheduling require lowering",
                persistence.Select(method => method.Id).Concat(persistencePaths))
            : Unsupported("no connected persistence path and read/write behavior was found");
        var settingsEvidence = settings.Length != 0
            ? Probable(
                "settings UI sink found; individual setting roles require lowering",
                settings.Select(method => method.Id))
            : Unsupported("no connected settings UI was found");
        var rainEvidence = rain.Length != 0
            ? Probable(
                "rain-like queued presentation behavior found; producer/consumer/pool roles require lowering",
                rain.Select(method => method.Id))
            : Unsupported("no rain producer/consumer behavior was proven");

        var roles = new List<PcCompatKeyViewerRoleBinding>();
        AddRole(roles, "InputListenerMethod", listener.FirstOrDefault(), activationEvidence);
        foreach (var transform in identityTransforms)
        {
            var transformEvidence = Proven([DescribeIdentityTransform(transform)]);
            AddRole(
                roles,
                "IdentityTransform",
                transform,
                transformEvidence);
        }
        foreach (var labelProvider in labelProviders)
        {
            AddRole(
                roles,
                "LabelProvider",
                labelProvider,
                Probable(
                    "string lane collection is connected to the feature, but exact lane-to-presentation dominance requires confirmation",
                    [labelProvider.Id]));
        }
        AddRole(roles, "FrameUpdater", presentation.FirstOrDefault(), presentationEvidence);
        AddRole(roles, "RainProducer", rain.FirstOrDefault(), rainEvidence);
        AddRole(roles, "EnableMethod", enable.FirstOrDefault(), visibilityEvidence);
        AddRole(roles, "DisableMethod", disable.FirstOrDefault(), visibilityEvidence);
        foreach (var indexedLoop in indexedLoops)
        {
            var provider = component.FirstOrDefault(method =>
                MethodShape(method) == MethodShape(indexedLoop.Loop.Provider));
            AddRole(roles, "BindingProvider", provider, laneEvidence);
            if (provider == null)
                continue;
            foreach (var field in provider.FieldReads
                         .Where(field => field.Type.EndsWith("[]", StringComparison.Ordinal))
                         .Distinct())
                AddFieldRole(roles, "LaneCollection", provider.AssemblyName, field, laneEvidence);
            foreach (var field in indexedLoop.Loop.IndexedFields)
            {
                if (field.Type == "System.Boolean[]")
                    AddFieldRole(roles, "HeldState", indexedLoop.Method.AssemblyName, field, transitionEvidence);
                else if (IsIntegralArray(field.Type))
                    AddFieldRole(roles, "CountState", indexedLoop.Method.AssemblyName, field, countEvidence);
            }
        }
        foreach (var providerGroup in crossMethodProviders.GroupBy(
                     provider => provider.Provider.Id,
                     StringComparer.Ordinal))
        {
            var providers = providerGroup.ToArray();
            var observedBases = providers
                .Select(provider => provider.ConsumerLaneBase)
                .Distinct()
                .ToArray();
            var consumerLaneBase = observedBases.Length == 1 ? observedBases[0] : null;
            AddRole(
                roles,
                "BindingProvider",
                providers[0].Provider,
                laneEvidence,
                consumerLaneBase);
            foreach (var provider in providers.Where(provider => provider.CacheField != null))
            {
                AddFieldRole(
                    roles,
                    "LaneCollection",
                    provider.Caller.AssemblyName,
                    provider.CacheField!,
                    laneEvidence);
            }
        }
        foreach (var method in stateWriters)
        {
            var fields = transactionMethods.Length != 0
                ? method.ArrayParameterInputTransactions
                    .SelectMany(transaction => transaction.StateFields)
                    .Distinct()
                : method.FieldReads.Concat(method.FieldWrites).Distinct();
            foreach (var field in fields)
            {
                if (field.Type is "System.Boolean" or "System.Boolean[]")
                    AddFieldRole(roles, "HeldState", method.AssemblyName, field, transitionEvidence);
            }
        }
        foreach (var method in countWriters)
        {
            var fields = transactionMethods.Length != 0
                ? method.ArrayParameterInputTransactions
                    .SelectMany(transaction => transaction.CountFields)
                    .Distinct()
                : method.FieldReads.Concat(method.FieldWrites).Distinct();
            foreach (var field in fields)
            {
                if (IsIntegralArray(field.Type))
                    AddFieldRole(roles, "CountState", method.AssemblyName, field, countEvidence);
                else if (IsIntegral(field.Type))
                    AddFieldRole(roles, "TotalState", method.AssemblyName, field, countEvidence);
                else if (transactionMethods.Length == 0 && field.Type == "System.Boolean")
                    AddFieldRole(
                        roles,
                        "PersistencePendingState",
                        method.AssemblyName,
                        field,
                        persistenceEvidence);
            }
        }
        foreach (var method in queueMethods)
        {
            foreach (var field in method.FieldReads.Concat(method.FieldWrites).Distinct())
            {
                if (field.Type.StartsWith("System.Collections.Generic.Queue", StringComparison.Ordinal))
                    AddFieldRole(roles, "KpsWindow", method.AssemblyName, field, kpsEvidence);
                else if (IsIntegral(field.Type))
                    AddFieldRole(roles, "KpsState", method.AssemblyName, field, kpsEvidence);
            }
        }
        foreach (var method in persistence)
        {
            foreach (var field in method.FieldReads.Concat(method.FieldWrites).Distinct())
            {
                if (IsIntegral(field.Type))
                {
                    AddFieldRole(
                        roles,
                        "PersistenceDirtyState",
                        method.AssemblyName,
                        field,
                        persistenceEvidence);
                }
            }
        }
        var countStateTypes = roles
            .Where(role => role.Role == "CountState" && role.MemberKind == "Field")
            .Select(role => role.TypeName)
            .ToHashSet(StringComparer.Ordinal);
        var persistenceSink = component.FirstOrDefault(method =>
            method.Name == "Save" && method.ParameterTypes.Count == 0 &&
            method.ReturnType == "System.Void" && countStateTypes.Contains(method.TypeName));
        AddRole(roles, "PersistenceSink", persistenceSink, persistenceEvidence);

        var featureHash = HashUtf8(string.Join("\n", seeds
            .Select(seed => seed.Call.DisplayName)
            .OrderBy(value => value, StringComparer.Ordinal)))[..12];
        return new PcCompatKeyViewerFeatureAdapter
        {
            Id = "keyviewer-" + featureHash,
            DisplayName = $"Key Viewer candidate {ordinal}",
            Backend = PcCompatKeyViewerBackend.ManagedSelfRender,
            SourceProfiles = sourceProfiles,
            LaneGroups =
            [
                new PcCompatKeyViewerLaneGroup
                {
                    Id = "source-lanes",
                    DisplayName = "Detected source lanes",
                    Lanes = lanes
                }
            ],
            Roles = roles,
            IdentityTransforms = identityTransforms.Select(transform =>
                new PcCompatKeyViewerIdentityTransform
                {
                    CandidateKey = PcCompatKeyViewerOverrideStore.GetCandidateKey(
                        transform.AssemblyName,
                        transform.TypeName,
                        transform.Name,
                        "Method"),
                    Kind = transform.IdentityTransformKind!.Value,
                    Threshold = transform.IdentityTransformThreshold ?? 0,
                    Offset = transform.IdentityTransformOffset ?? 0,
                    Evidence = Proven([DescribeIdentityTransform(transform)])
                }).ToArray(),
            Visibility = new PcCompatKeyViewerPredicate
            {
                Kind = enable.Length != 0 && disable.Length != 0
                    ? "LifecyclePair"
                    : "Unresolved",
                Expression = enable.Length != 0 && disable.Length != 0
                    ? $"{enable[0].Id} / {disable[0].Id}"
                    : "manual role binding required",
                Evidence = visibilityEvidence
            },
            InputActivation = new PcCompatKeyViewerPredicate
            {
                Kind = listener.Length != 0 ? "LoopPredicate" : "Unresolved",
                Expression = listener.Length != 0
                    ? listener[0].Id
                    : "manual role binding required",
                Evidence = activationEvidence
            },
            CountSemantics = new PcCompatKeyViewerCountSemantics
            {
                Edge = PcCompatKeyViewerCountEdge.Rising,
                CountRepeats = false,
                GhostAffectsCount = false,
                KpsWindowMilliseconds = 1000,
                Clock = monotonic.Length != 0 ? "Monotonic.Stopwatch" : "Unresolved",
                ResetEntryPoint = resetCandidates.Length == 1
                    ? resetCandidates[0].Id
                    : "manual role binding required",
                PersistencePath = persistencePaths.FirstOrDefault(value =>
                    !value.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)),
                BackupPersistencePath = persistencePaths.FirstOrDefault(value =>
                    value.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            },
            Capabilities = new PcCompatKeyViewerEvidenceMatrix
            {
                Input = MergeSourceEvidence(sourceProfiles),
                Lane = laneEvidence,
                Transition = transitionEvidence,
                Count = countEvidence,
                Kps = kpsEvidence,
                Rain = rainEvidence,
                Presentation = presentationEvidence,
                Visibility = visibilityEvidence,
                InputActivation = activationEvidence,
                Settings = settingsEvidence,
                Persistence = persistenceEvidence
            }
        };
    }

    private static void AddRole(
        ICollection<PcCompatKeyViewerRoleBinding> roles,
        string role,
        MethodNode? method,
        PcCompatAdapterEvidence evidence,
        int? consumerLaneBase = null)
    {
        if (method == null)
            return;
        var candidate = new PcCompatKeyViewerRoleBinding
        {
            Role = role,
            AssemblyName = method.AssemblyName,
            TypeName = method.TypeName,
            MemberName = method.Name,
            MemberKind = "Method",
            ConsumerLaneBase = consumerLaneBase,
            Evidence = evidence
        };
        var existing = roles.FirstOrDefault(value =>
            value.Role == candidate.Role &&
            value.AssemblyName == candidate.AssemblyName &&
            value.TypeName == candidate.TypeName &&
            value.MemberName == candidate.MemberName &&
            value.MemberKind == candidate.MemberKind);
        if (existing == null)
        {
            roles.Add(candidate);
            return;
        }
        if (role != "BindingProvider" || !consumerLaneBase.HasValue ||
            existing.ConsumerLaneBase.HasValue)
        {
            return;
        }

        // Local-loop discovery can add an unranked candidate before cross-method call-site analysis.
        // Replace only that duplicate with the stronger lane-origin proof.
        roles.Remove(existing);
        roles.Add(candidate);
    }

    private static void AddFieldRole(
        ICollection<PcCompatKeyViewerRoleBinding> roles,
        string role,
        string assemblyName,
        FieldAccess field,
        PcCompatAdapterEvidence evidence)
    {
        var candidate = new PcCompatKeyViewerRoleBinding
        {
            Role = role,
            AssemblyName = assemblyName,
            TypeName = field.DeclaringType,
            MemberName = field.Name,
            MemberKind = "Field",
            Evidence = evidence
        };
        if (!roles.Any(existing => existing.Role == candidate.Role &&
                                  existing.AssemblyName == candidate.AssemblyName &&
                                  existing.TypeName == candidate.TypeName &&
                                  existing.MemberName == candidate.MemberName &&
                                  existing.MemberKind == candidate.MemberKind))
            roles.Add(candidate);
    }

    private static bool TryClassifyInputCall(
        CallSite call,
        out PcCompatKeyViewerInputProfileKind kind)
    {
        if (call.DeclaringType == "UnityEngine.Input" &&
            ((call.Name is "GetKey" or "GetKeyDown" or "GetKeyUp" &&
              call.ParameterTypes.SequenceEqual(["UnityEngine.KeyCode"]) &&
              call.ReturnType == "System.Boolean") ||
             (call.Name == "get_anyKeyDown" && call.ParameterTypes.Count == 0 &&
              call.ReturnType == "System.Boolean")))
        {
            kind = PcCompatKeyViewerInputProfileKind.LegacyUnityPolling;
            return true;
        }
        if (IsSupportedInputSystemCall(call))
        {
            kind = PcCompatKeyViewerInputProfileKind.InputSystemEvent;
            return true;
        }
        if (call.DeclaringType == "Rewired.Player" &&
            call.Name is "GetButton" or "GetButtonDown" or "GetButtonUp" &&
            call.ParameterTypes.SequenceEqual(["System.Int32"]) &&
            call.ReturnType == "System.Boolean")
        {
            kind = PcCompatKeyViewerInputProfileKind.RewiredPolling;
            return true;
        }
        kind = default;
        return false;
    }

    private static bool IsSupportedInputSystemCall(CallSite call)
    {
        if (!call.DeclaringType.StartsWith("UnityEngine.InputSystem.", StringComparison.Ordinal))
            return false;

        // Only exact keyboard/button query shapes enter the candidate graph.
        // Generic device/event APIs require control-path lowering and remain unsupported.
        return (call.DeclaringType is
                    "UnityEngine.InputSystem.Controls.ButtonControl" or
                    "UnityEngine.InputSystem.Controls.KeyControl") &&
               call.Name is "get_isPressed" or "wasPressedThisFrame" or
                   "wasReleasedThisFrame" &&
               call.ParameterTypes.Count == 0 &&
               call.ReturnType == "System.Boolean";
    }

    private static bool TryClassifyPInvoke(
        MethodNode method,
        out PcCompatKeyViewerInputProfileKind kind)
    {
        var module = method.ImportModule ?? string.Empty;
        var import = method.ImportName ?? string.Empty;
        var valid = (module.Equals("user32", StringComparison.OrdinalIgnoreCase) ||
                     module.Equals("user32.dll", StringComparison.OrdinalIgnoreCase)) &&
                    import.Equals("GetAsyncKeyState", StringComparison.Ordinal) &&
                    method.ReturnType == "System.Int16" &&
                    method.ParameterTypes.SequenceEqual(["System.Int32"]);
        kind = PcCompatKeyViewerInputProfileKind.Win32Polling;
        return valid;
    }

    private static bool HasTransitionWrite(MethodNode method)
        => method.HasBooleanArrayWrite || method.FieldWrites.Any(field =>
            field.Type == "System.Boolean" || field.Type.EndsWith("[]", StringComparison.Ordinal));

    private static bool HasCountWrite(MethodNode method)
        => method.HasAdd && (method.HasArrayWrite || method.FieldWrites.Any(field =>
            field.Type is "System.Int16" or "System.Int32" or "System.Int64" or
                          "System.UInt16" or "System.UInt32" or "System.UInt64" ||
            field.Type.EndsWith("[]", StringComparison.Ordinal)));

    private static bool IsIntegral(string type)
        => type is "System.Byte" or "System.SByte" or
                   "System.Int16" or "System.UInt16" or
                   "System.Int32" or "System.UInt32" or
                   "System.Int64" or "System.UInt64";

    private static bool IsIntegralArray(string type)
        => type.EndsWith("[]", StringComparison.Ordinal) &&
           IsIntegral(type[..^2]);

    private static bool UsesMonotonicClock(MethodNode method)
        => method.Calls.Any(call =>
            call.DeclaringType == "System.Diagnostics.Stopwatch" &&
            call.Name is "get_ElapsedMilliseconds" or "GetTimestamp" or "StartNew");

    private static bool UsesQueue(MethodNode method)
        => method.Calls.Any(call =>
            call.DeclaringType.StartsWith("System.Collections.Generic.Queue", StringComparison.Ordinal) &&
            call.Name is "Enqueue" or "Dequeue" or "TryDequeue" or "TryPeek" or "get_Count");

    private static bool UsesPresentationSink(MethodNode method)
        => method.Calls.Any(call =>
            call.DeclaringType.StartsWith("TMPro.", StringComparison.Ordinal) ||
            call.DeclaringType.StartsWith("UnityEngine.UI.", StringComparison.Ordinal) ||
            call.DeclaringType is "UnityEngine.GameObject" or "UnityEngine.Canvas" or
                                  "UnityEngine.RectTransform" &&
            (call.Name.StartsWith("set_", StringComparison.Ordinal) ||
             call.Name is "SetText" or "SetActive" or "AddComponent"));

    private static bool UsesPersistence(MethodNode method)
        => method.Calls.Any(call =>
            call.DeclaringType.StartsWith("System.IO.", StringComparison.Ordinal) &&
            call.Name is "Read" or "Write" or "ReadAllBytes" or "WriteAllBytes" or
                         "Open" or "Move" or "Delete" or "Exists" or ".ctor");

    private static bool UsesSettingsUi(MethodNode method)
        => method.Calls.Any(call =>
            call.DeclaringType is "UnityEngine.GUILayout" or "UnityEngine.GUI" ||
            call.DeclaringType.Contains("UnityModManager", StringComparison.Ordinal));

    private static bool HasRainEvidence(MethodNode method)
        => (method.TypeName.Contains("Rain", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Rain", StringComparison.OrdinalIgnoreCase) ||
            method.FieldReads.Concat(method.FieldWrites).Any(field =>
                field.DeclaringType.Contains("Rain", StringComparison.OrdinalIgnoreCase) ||
                field.Name.Contains("Rain", StringComparison.OrdinalIgnoreCase))) &&
           (UsesQueue(method) || UsesPresentationSink(method) || method.HasArrayWrite);

    private static PcCompatAdapterEvidence Proven(IEnumerable<string> evidence)
        => new()
        {
            Status = PcCompatAdapterEvidenceStatus.Proven,
            Evidence = evidence.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray()
        };

    private static PcCompatAdapterEvidence BuildSourceEvidence(
        PcCompatKeyViewerInputProfileKind kind,
        IEnumerable<string> evidence)
        => kind is PcCompatKeyViewerInputProfileKind.LegacyUnityPolling or
                   PcCompatKeyViewerInputProfileKind.Win32Polling or
                   PcCompatKeyViewerInputProfileKind.RewiredPolling
            ? Proven(evidence)
            : Probable(
                $"{kind} was detected, but its Android event/query bridge is not implemented",
                evidence);

    private static string DescribeIdentityTransform(MethodNode method)
        => method.IdentityTransformKind switch
        {
            PcCompatKeyViewerIdentityTransformKind.UnityKeyCodeIdentity =>
                $"{method.Id}: configured value is passed directly to UnityEngine.Input.GetKey",
            PcCompatKeyViewerIdentityTransformKind.WindowsVirtualKeyIdentity =>
                $"{method.Id}: configured value is passed directly to verified GetAsyncKeyState",
            PcCompatKeyViewerIdentityTransformKind.WindowsVirtualKeyOffset =>
                $"{method.Id}: configured value subtracts 0x{method.IdentityTransformOffset:X} before verified GetAsyncKeyState",
            PcCompatKeyViewerIdentityTransformKind.UnityWindowsThresholdSplit =>
                $"{method.Id}: values below 0x{method.IdentityTransformThreshold:X} use Unity Input; upper values subtract 0x{method.IdentityTransformOffset:X} before verified GetAsyncKeyState",
            _ => $"{method.Id}: unsupported identity transform"
        };

    private static PcCompatAdapterEvidence MergeSourceEvidence(
        IReadOnlyList<PcCompatKeyViewerSourceProfile> profiles)
    {
        var evidence = profiles.SelectMany(profile => profile.Evidence.Evidence);
        return profiles.All(profile => profile.Evidence.Status == PcCompatAdapterEvidenceStatus.Proven)
            ? Proven(evidence)
            : Probable(
                "one or more detected input profiles do not have a verified Android bridge",
                evidence);
    }

    private static PcCompatAdapterEvidence Probable(string firstBreak, IEnumerable<string> evidence)
        => new()
        {
            Status = PcCompatAdapterEvidenceStatus.Probable,
            Evidence = evidence.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            FirstBreak = firstBreak
        };

    private static PcCompatAdapterEvidence Unsupported(string firstBreak)
        => new()
        {
            Status = PcCompatAdapterEvidenceStatus.Unsupported,
            FirstBreak = firstBreak
        };

    private static PcCompatAdapterEvidence Unresolved(
        PcCompatAdapterEvidenceStatus status,
        string firstBreak,
        IEnumerable<string> evidence)
        => new()
        {
            Status = status,
            Evidence = evidence.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            FirstBreak = firstBreak
        };

    private static bool TryGetBranchTargets(
        PcCompatIlInstruction instruction,
        out IReadOnlyList<int> targets)
    {
        if (instruction.Operand is int target &&
            instruction.OpCode.OperandType is OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget)
        {
            targets = [target];
            return true;
        }
        if (instruction.Operand is int[] table)
        {
            targets = table;
            return true;
        }
        targets = Array.Empty<int>();
        return false;
    }

    private static int FindInstructionAtOrAfter(
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int offset)
    {
        for (var index = 0; index < instructions.Count; ++index)
        {
            if (instructions[index].Offset >= offset)
                return index;
        }
        return -1;
    }

    private static int PreviousMeaningful(
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int index)
    {
        while (index >= 0 && instructions[index].OpCode == OpCodes.Nop)
            --index;
        return index;
    }

    private static bool MatchesDirectArgument(
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int callIndex,
        int argumentIndex)
    {
        var index = PreviousValueInstruction(instructions, callIndex - 1);
        return index >= 0 &&
               TryGetLoadArgumentIndex(instructions[index], out var loaded) &&
               loaded == argumentIndex;
    }

    private static bool MatchesArgumentMinusConstant(
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int callIndex,
        int argumentIndex,
        int constant)
        => TryMatchArgumentMinusConstant(
            instructions,
            callIndex,
            argumentIndex,
            out var actual) && actual == constant;

    private static bool TryMatchArgumentMinusConstant(
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int callIndex,
        int argumentIndex,
        out int constant)
    {
        constant = 0;
        var subtractIndex = PreviousValueInstruction(instructions, callIndex - 1);
        if (subtractIndex < 0 || instructions[subtractIndex].OpCode != OpCodes.Sub)
            return false;
        var constantIndex = PreviousValueInstruction(instructions, subtractIndex - 1);
        var argumentLoadIndex = PreviousValueInstruction(instructions, constantIndex - 1);
        return constantIndex >= 0 && argumentLoadIndex >= 0 &&
               TryGetI4Constant(instructions[constantIndex], out constant) &&
               TryGetLoadArgumentIndex(instructions[argumentLoadIndex], out var loaded) &&
               loaded == argumentIndex;
    }

    private static bool HasArgumentThresholdBranch(
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int argumentIndex,
        int threshold)
    {
        for (var index = 0; index < instructions.Count; ++index)
        {
            var branch = instructions[index].OpCode;
            if (branch.FlowControl != FlowControl.Cond_Branch || branch == OpCodes.Switch)
                continue;
            var constantIndex = PreviousValueInstruction(instructions, index - 1);
            var argumentLoadIndex = PreviousValueInstruction(instructions, constantIndex - 1);
            if (constantIndex >= 0 && argumentLoadIndex >= 0 &&
                TryGetI4Constant(instructions[constantIndex], out var actual) &&
                actual == threshold &&
                TryGetLoadArgumentIndex(instructions[argumentLoadIndex], out var loaded) &&
                loaded == argumentIndex)
                return true;
        }
        return false;
    }

    private static int PreviousValueInstruction(
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int index)
    {
        while (index >= 0)
        {
            var opCode = instructions[index].OpCode;
            if (opCode == OpCodes.Nop || IsIntegerConversion(opCode))
            {
                --index;
                continue;
            }
            return index;
        }
        return -1;
    }

    private static bool IsIntegerConversion(OpCode opCode)
        => opCode == OpCodes.Conv_I || opCode == OpCodes.Conv_I4 ||
           opCode == OpCodes.Conv_U || opCode == OpCodes.Conv_U4;

    private static bool TryGetLoadArgumentIndex(
        PcCompatIlInstruction instruction,
        out int argumentIndex)
    {
        if (instruction.OpCode == OpCodes.Ldarg_0) argumentIndex = 0;
        else if (instruction.OpCode == OpCodes.Ldarg_1) argumentIndex = 1;
        else if (instruction.OpCode == OpCodes.Ldarg_2) argumentIndex = 2;
        else if (instruction.OpCode == OpCodes.Ldarg_3) argumentIndex = 3;
        else if (instruction.OpCode == OpCodes.Ldarg || instruction.OpCode == OpCodes.Ldarg_S)
            argumentIndex = instruction.Operand is int value ? value : -1;
        else
        {
            argumentIndex = -1;
            return false;
        }
        return argumentIndex >= 0;
    }

    private static int NextMeaningful(
        IReadOnlyList<PcCompatIlInstruction> instructions,
        int index)
    {
        while (index < instructions.Count && instructions[index].OpCode == OpCodes.Nop)
            ++index;
        return index < instructions.Count ? index : -1;
    }

    private static bool TryGetLoadLocalIndex(
        PcCompatIlInstruction instruction,
        out int localIndex)
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

    private static bool TryGetStoreLocalIndex(
        PcCompatIlInstruction instruction,
        out int localIndex)
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

    private static bool IsArrayElementAccess(OpCode opCode)
        => opCode == OpCodes.Ldelema ||
           opCode.Name?.StartsWith("ldelem", StringComparison.Ordinal) == true;

    private static bool TryGetI4Constant(PcCompatIlInstruction instruction, out int value)
    {
        if (instruction.OpCode == OpCodes.Ldc_I4 && instruction.Operand is int full)
        {
            value = full;
            return true;
        }
        if (instruction.OpCode == OpCodes.Ldc_I4_S && instruction.Operand is int shortValue)
        {
            value = shortValue;
            return true;
        }
        value = instruction.OpCode.Value switch
        {
            var op when op == OpCodes.Ldc_I4_M1.Value => -1,
            var op when op == OpCodes.Ldc_I4_0.Value => 0,
            var op when op == OpCodes.Ldc_I4_1.Value => 1,
            var op when op == OpCodes.Ldc_I4_2.Value => 2,
            var op when op == OpCodes.Ldc_I4_3.Value => 3,
            var op when op == OpCodes.Ldc_I4_4.Value => 4,
            var op when op == OpCodes.Ldc_I4_5.Value => 5,
            var op when op == OpCodes.Ldc_I4_6.Value => 6,
            var op when op == OpCodes.Ldc_I4_7.Value => 7,
            var op when op == OpCodes.Ldc_I4_8.Value => 8,
            _ => int.MinValue
        };
        return value != int.MinValue;
    }

    private static void AddFieldUsers(
        IDictionary<string, List<int>> destination,
        IEnumerable<FieldAccess> fields,
        int methodIndex)
    {
        foreach (var field in fields.Distinct())
        {
            var key = field.DeclaringType + "\0" + field.Name;
            if (!destination.TryGetValue(key, out var users))
                destination[key] = users = new List<int>();
            users.Add(methodIndex);
        }
    }

    private static string MethodShape(MethodNode method)
        => MethodShape(method.TypeName, method.Name, method.ParameterTypes);

    private static string MethodShape(CallSite call)
        => MethodShape(call.DeclaringType, call.Name, call.ParameterTypes);

    private static string MethodShape(string type, string name, IReadOnlyList<string> parameters)
        => type + "\0" + name + "\0" + string.Join(";", parameters);

    private static string ComputeBehaviorPackageHash(
        PcModManifest manifest,
        IReadOnlyList<PcCompatAdapterAssemblyFingerprint> fingerprints)
    {
        var builder = new StringBuilder(PackageFingerprintVersion).Append('\n');
        builder.Append(manifest.RawInfoJson ?? string.Empty).Append('\n');
        builder.Append(manifest.RawJAModInfoJson ?? string.Empty).Append('\n');
        foreach (var assembly in fingerprints)
        {
            builder.Append(assembly.AssemblyName).Append('|')
                .Append(assembly.Sha256).Append('|')
                .Append(assembly.Mvid).Append('\n');
        }
        return HashUtf8(builder.ToString());
    }

    private static string HashFile(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string HashUtf8(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void RequireSha256(string value, string parameterName)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Expected 64 hexadecimal characters.", parameterName);
    }

    private sealed record AssemblyImage(
        string AssemblyName,
        string Sha256,
        string Mvid,
        IReadOnlyList<MethodNode> Methods);

    private sealed class MethodNode
    {
        public MethodNode(
            string assemblyName,
            string assemblyPath,
            string typeName,
            string name,
            int metadataToken,
            bool isStatic,
            IReadOnlyList<string> parameterTypes,
            string returnType)
        {
            AssemblyName = assemblyName;
            AssemblyPath = assemblyPath;
            TypeName = typeName;
            Name = name;
            MetadataToken = metadataToken;
            IsStatic = isStatic;
            ParameterTypes = parameterTypes;
            ReturnType = returnType;
        }

        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string TypeName { get; }
        public string Name { get; }
        public int MetadataToken { get; }
        public bool IsStatic { get; }
        public IReadOnlyList<string> ParameterTypes { get; }
        public string ReturnType { get; }
        public string Id => $"{AssemblyName}:{TypeName}.{Name}({string.Join(',', ParameterTypes)})";
        public string? ImportModule { get; set; }
        public string? ImportName { get; set; }
        public List<CallSite> Calls { get; } = [];
        public List<FieldAccess> FieldReads { get; } = [];
        public List<FieldAccess> FieldWrites { get; } = [];
        public List<FieldAccessSite> FieldReadSites { get; } = [];
        public List<FieldAccessSite> FieldWriteSites { get; } = [];
        public List<string> Strings { get; } = [];
        public List<IndexedArrayLoop> IndexedArrayLoops { get; } = [];
        public List<ArrayParameterInputTransaction> ArrayParameterInputTransactions { get; } = [];
        public List<ArrayFieldProviderAssignment> ArrayFieldProviderAssignments { get; } = [];
        public List<CallSite> IdentityTransformBridgeCalls { get; } = [];
        public IReadOnlyList<PcCompatIlInstruction> Instructions { get; set; } =
            Array.Empty<PcCompatIlInstruction>();
        public PcCompatKeyViewerIdentityTransformKind? IdentityTransformKind { get; set; }
        public int? IdentityTransformThreshold { get; set; }
        public int? IdentityTransformOffset { get; set; }
        public bool HasConditionalBranch { get; set; }
        public bool HasBackEdge { get; set; }
        public bool HasAdd { get; set; }
        public bool HasArrayWrite { get; set; }
        public bool HasBooleanArrayWrite { get; set; }
        public bool HasZeroConstant { get; set; }
        public int? LastI4Constant { get; set; }
    }

    private sealed record CallSite(
        string DeclaringType,
        string Name,
        IReadOnlyList<string> ParameterTypes,
        string ReturnType,
        int Offset,
        int? ConstantArgument)
    {
        public string DisplayName => DeclaringType + "." + Name;
    }

    private sealed record FieldAccess(string DeclaringType, string Name, string Type);
    private sealed record FieldAccessSite(FieldAccess Field, int Offset);
    private sealed record InputSeed(
        int MethodIndex,
        PcCompatKeyViewerInputProfileKind Kind,
        CallSite Call);

    private sealed record IndexedArrayLoop(
        CallSite Provider,
        int ProviderLocal,
        int IndexLocal,
        int LoopStartOffset,
        int LoopEndOffset,
        bool ProviderFeedsBooleanQuery,
        IReadOnlyList<FieldAccess> IndexedFields,
        IReadOnlyList<int> ProviderReadOffsets);

    private sealed record ArrayParameterInputTransaction(
        int ParameterIndex,
        int ArgumentIndex,
        int IndexLocal,
        int ResultLocal,
        int LoopStartOffset,
        int LoopEndOffset,
        CallSite InputCall,
        IReadOnlyList<FieldAccess> StateFields,
        IReadOnlyList<FieldAccess> CountFields);

    private sealed record ArrayFieldProviderAssignment(
        FieldAccess Field,
        CallSite Provider,
        int StoreOffset);

    private sealed record CrossMethodArrayProvider(
        MethodNode TransactionMethod,
        ArrayParameterInputTransaction Transaction,
        MethodNode Caller,
        MethodNode Provider,
        FieldAccess? CacheField,
        int ConsumerCallOffset,
        int? ConsumerLaneBase);

    private readonly record struct ValueOrigin(
        FieldAccess? Field,
        CallSite? Call,
        int? Int32Constant);

    private sealed class BehaviorGraph
    {
        private readonly int[] _parent;
        private readonly byte[] _rank;

        public BehaviorGraph(int count)
        {
            _parent = Enumerable.Range(0, count).ToArray();
            _rank = new byte[count];
        }

        public int Find(int value)
        {
            while (_parent[value] != value)
            {
                _parent[value] = _parent[_parent[value]];
                value = _parent[value];
            }
            return value;
        }

        public void Connect(int left, int right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot == rightRoot)
                return;
            if (_rank[leftRoot] < _rank[rightRoot])
                (leftRoot, rightRoot) = (rightRoot, leftRoot);
            _parent[rightRoot] = leftRoot;
            if (_rank[leftRoot] == _rank[rightRoot])
                _rank[leftRoot]++;
        }
    }
}

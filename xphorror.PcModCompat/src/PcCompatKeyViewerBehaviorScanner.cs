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
    public const string CurrentAnalyzerVersion = "keyviewer-behavior-scan-v5-local-provider-dominance";
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
                    AnalyzeInstructions(reader, node, instructions);
                    AnalyzeIndexedArrayLoops(reader, node, instructions);
                    AnalyzeIdentityTransform(reader, node, instructions);
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
                        node.FieldWrites.Add(field);
                    else
                        node.FieldReads.Add(field);
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
        var laneEvidence = constants.Length != 0
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

        var stateWriters = component.Where(HasTransitionWrite).ToArray();
        var countWriters = component.Where(HasCountWrite).ToArray();
        var monotonic = component.Where(UsesMonotonicClock).ToArray();
        var queueMethods = component.Where(UsesQueue).ToArray();
        var presentation = component.Where(UsesPresentationSink).ToArray();
        var persistence = component.Where(UsesPersistence).ToArray();
        var settings = component.Where(UsesSettingsUi).ToArray();
        var rain = component.Where(HasRainEvidence).ToArray();
        var enable = component.Where(method => method.Name is "OnEnable" or "CompatEnable").ToArray();
        var disable = component.Where(method => method.Name is "OnDisable" or "CompatDisable").ToArray();
        var listener = component.Where(method => method.HasBackEdge &&
                                                 (method.Calls.Any(call =>
                                                      seeds.Any(seed => MethodShape(seed.Call) == MethodShape(call))) ||
                                                  method.FieldReads.Any())).ToArray();
        var importedWin32Shapes = component
            .Where(method => TryClassifyPInvoke(method, out var kind) &&
                             kind == PcCompatKeyViewerInputProfileKind.Win32Polling)
            .Select(MethodShape)
            .ToHashSet(StringComparer.Ordinal);
        var identityTransforms = component.Where(method =>
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

        var transitionEvidence = stateWriters.Length != 0 &&
                                 component.Any(method => method.HasConditionalBranch)
            ? Probable(
                "conditional transition and held-state write are connected, but CFG control dependence is not lowered",
                stateWriters.Select(method => method.Id))
            : Unsupported("no connected conditional held-state write was found");
        var countEvidence = countWriters.Length != 0
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
        var activationEvidence = listener.Length != 0
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
        AddRole(roles, "HeldState", stateWriters.FirstOrDefault(), transitionEvidence);
        AddRole(roles, "CountState", countWriters.FirstOrDefault(), countEvidence);
        AddRole(roles, "FrameUpdater", presentation.FirstOrDefault(), presentationEvidence);
        AddRole(roles, "KpsWindow", monotonic.FirstOrDefault(), kpsEvidence);
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
                else if (field.Type.EndsWith("[]", StringComparison.Ordinal) &&
                         field.Type is "System.Int16[]" or "System.Int32[]" or "System.Int64[]" or
                                       "System.UInt16[]" or "System.UInt32[]" or "System.UInt64[]")
                    AddFieldRole(roles, "CountState", indexedLoop.Method.AssemblyName, field, countEvidence);
            }
        }

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
        PcCompatAdapterEvidence evidence)
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
            Evidence = evidence
        };
        if (!roles.Any(existing => existing.Role == candidate.Role &&
                                  existing.AssemblyName == candidate.AssemblyName &&
                                  existing.TypeName == candidate.TypeName &&
                                  existing.MemberName == candidate.MemberName &&
                                  existing.MemberKind == candidate.MemberKind))
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
        public List<string> Strings { get; } = [];
        public List<IndexedArrayLoop> IndexedArrayLoops { get; } = [];
        public List<CallSite> IdentityTransformBridgeCalls { get; } = [];
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

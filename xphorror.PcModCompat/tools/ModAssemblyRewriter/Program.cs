using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

var options = Options.Parse(args);
if (options is null)
    return 2;

try
{
    var report = ProxyFieldRewriter.Rewrite(options);
    Directory.CreateDirectory(Path.GetDirectoryName(options.ReportPath)!);
    File.WriteAllText(
        options.ReportPath,
        JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }),
        new UTF8Encoding(false));

    Console.WriteLine(
        $"Rewriter scanned={report.ScannedFieldInstructions} rewritten={report.Rewrites.Count} " +
        $"issues={report.Issues.Count} methods={report.MethodCalls.Count}/{report.MethodCalls.Count + report.MethodIssues.Count} " +
        $"bridges={report.ManagedBridgeRewrites.Count}/{report.ManagedBridgeRewrites.Count + report.ManagedBridgeIssues.Count} " +
        $"metadata={report.PatchMetadataRewrites.Count} " +
        $"output={report.OutputWritten}");
    foreach (var issue in report.Issues)
        Console.Error.WriteLine(issue);
    foreach (var issue in report.MethodIssues)
        Console.Error.WriteLine($"{issue.Method}@IL_{issue.IlOffset:X4}: {issue.Reason} ({issue.Target})");
    foreach (var issue in report.ManagedBridgeIssues)
        Console.Error.WriteLine($"{issue.SourceType}.{issue.SourceMethod}: {issue.Reason}");
    return report.Issues.Count == 0 &&
           report.MethodIssues.Count == 0 &&
           report.ManagedBridgeIssues.Count == 0
        ? 0
        : 4;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

internal static class ProxyFieldRewriter
{
    private static readonly HashSet<Code> FieldCodes =
    [
        Code.Ldfld,
        Code.Stfld,
        Code.Ldsfld,
        Code.Stsfld
    ];
    private static readonly HashSet<Code> MethodCodes = [Code.Call, Code.Callvirt, Code.Newobj];

    public static RewriteReport Rewrite(Options options)
    {
        IReadOnlyDictionary<string, ModuleDefMD> proxyModules =
            new Dictionary<string, ModuleDefMD>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, ModuleDefMD> managedOwnedModules =
            new Dictionary<string, ModuleDefMD>(StringComparer.OrdinalIgnoreCase);
        ModuleDefMD? managedBridgeModule = null;
        try
        {
            proxyModules = LoadProxyModules(options.ProxyDirectory);
            managedOwnedModules = LoadManagedOwnedModules(
                options.ManagedOwnedAssemblyPaths,
                options.InputPath);
            if (options.ManagedBridgeRewrites.Count != 0 ||
                options.ManagedCallBridgeRewrites.Count != 0 ||
                options.ManagedProxyCastBridge is not null ||
                options.ManagedReadProgressGuard is not null ||
                options.ManagedPollingWaitRewrite is not null ||
                options.ManagedOptionalDelegateRewrite is not null)
                managedBridgeModule = ModuleDefMD.Load(options.ManagedBridgeAssemblyPath!);

            var proxyAssemblies = proxyModules.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            using var module = ModuleDefMD.Load(options.InputPath);
            var importer = new Importer(module, ImporterOptions.TryToUseTypeDefs);
            var issues = new List<string>();
            var pending = new List<RewritePlan>();
            var fieldConstantPlans = new List<FieldConstantRewritePlan>();
            var passthroughs = new List<PassthroughRecord>();
            var methodCalls = new List<MethodCallRecord>();
            var methodIssues = new List<MethodIssueRecord>();
            var methodRewrites = new List<MethodRewritePlan>();
            var managedBridgeRewrites = new List<ManagedBridgeRewriteRecord>();
            var managedBridgeIssues = new List<ManagedBridgeIssueRecord>();
            AuditProxyTypeReferences(module, proxyModules, issues);
            var patchMetadataRewrites = RewriteJalibPatchMetadata(module, issues);
            var managedBridgePlans = PlanManagedBridgeRewrites(
                module,
                importer,
                managedBridgeModule,
                options.ManagedBridgeRewrites,
                managedBridgeRewrites,
                managedBridgeIssues);
            var managedCallBridgePlans = PlanManagedCallBridgeRewrites(
                module,
                importer,
                managedBridgeModule,
                proxyModules,
                managedOwnedModules,
                options.ManagedCallBridgeRewrites,
                managedBridgeRewrites,
                managedBridgeIssues);
            var managedReadProgressGuardPlans = PlanManagedReadProgressGuards(
                module,
                importer,
                managedBridgeModule,
                options.ManagedReadProgressGuard,
                managedBridgeRewrites,
                managedBridgeIssues);
            var managedPollingWaitPlans = PlanManagedPollingWaitRewrites(
                module,
                importer,
                managedBridgeModule,
                options.ManagedPollingWaitRewrite,
                managedBridgeRewrites,
                managedBridgeIssues);
            var managedOptionalDelegatePlans = PlanManagedOptionalDelegateRewrites(
                module,
                importer,
                managedBridgeModule,
                options.ManagedOptionalDelegateRewrite,
                managedBridgeRewrites,
                managedBridgeIssues);
            var managedCallBridgeInstructions = managedCallBridgePlans
                .Select(plan => plan.Instruction)
                .ToHashSet();
            var opaqueTypeErasurePlans = PlanOpaqueTypeErasures(
                module,
                options.ManagedCallBridgeRewrites,
                managedCallBridgeInstructions,
                managedBridgeRewrites,
                managedBridgeIssues);
            var opaqueTypeInstructions = opaqueTypeErasurePlans
                .SelectMany(plan => plan.TypeInstructions)
                .Select(item => item.Instruction)
                .ToHashSet();
            var proxyCastPlans = PlanProxyCastRewrites(
                module,
                importer,
                managedBridgeModule,
                proxyModules,
                opaqueTypeInstructions,
                options.ManagedProxyCastBridge,
                managedBridgeRewrites,
                managedBridgeIssues);
            var scanned = 0;

            foreach (var type in module.GetTypes())
            foreach (var method in type.Methods.Where(method => method.HasBody))
            foreach (var instruction in method.Body.Instructions)
            {
                if (!FieldCodes.Contains(instruction.OpCode.Code) || instruction.Operand is not IField field)
                    continue;

                var assemblyName = field.DeclaringType.DefinitionAssembly?.Name?.String;
                if (string.IsNullOrWhiteSpace(assemblyName))
                    continue;

                if (instruction.OpCode.Code == Code.Ldsfld)
                {
                    var constantSpecs = options.ManagedFieldConstantRewrites.Where(spec =>
                        string.Equals(spec.SourceAssembly, assemblyName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(spec.SourceType, field.DeclaringType.FullName, StringComparison.Ordinal) &&
                        string.Equals(spec.SourceField, field.Name, StringComparison.Ordinal) &&
                        field.FieldSig is not null &&
                        TypeIdentity(field.FieldSig.Type) == NormalizeExternalTypeIdentity(spec.SourceFieldType))
                        .ToArray();
                    if (constantSpecs.Length > 1)
                    {
                        issues.Add(
                            $"{method.FullName}@IL_{instruction.Offset:X4}: ambiguous managed field constant " +
                            $"rewrite for {assemblyName}!{field.DeclaringType.FullName}::{field.Name}");
                        continue;
                    }
                    if (constantSpecs.Length == 1)
                    {
                        scanned++;
                        var spec = constantSpecs[0];
                        fieldConstantPlans.Add(new FieldConstantRewritePlan(instruction, spec.Value));
                        managedBridgeRewrites.Add(new ManagedBridgeRewriteRecord(
                            method.FullName,
                            instruction.Offset,
                            instruction.OpCode.Name,
                            $"{assemblyName}!{field.DeclaringType.FullName}::{field.Name}",
                            $"constant:i4:{spec.Value}",
                            0));
                        continue;
                    }
                }
                if (!proxyAssemblies.Contains(assemblyName))
                {
                    if (RequiresGeneratedProxy(assemblyName))
                    {
                        issues.Add(
                            $"{method.FullName}@IL_{instruction.Offset:X4}: generated proxy assembly missing " +
                            $"for field target {assemblyName}!{field.DeclaringType.FullName}::{field.Name}");
                    }
                    continue;
                }

                scanned++;
                var location = $"{method.FullName}@IL_{instruction.Offset:X4}";
                var isStatic = instruction.OpCode.Code is Code.Ldsfld or Code.Stsfld;
                var isWrite = instruction.OpCode.Code is Code.Stfld or Code.Stsfld;
                var accessorName = (isWrite ? "set_" : "get_") + field.Name;

                if (!proxyModules.TryGetValue(assemblyName, out var proxyModule))
                {
                    issues.Add($"{location}: proxy assembly not loaded: {assemblyName}");
                    continue;
                }

                var proxyType = proxyModule.Find(field.DeclaringType.FullName, isReflectionName: false);
                if (proxyType is null)
                {
                    issues.Add(
                        $"{location}: proxy type missing: {assemblyName}!{field.DeclaringType.FullName}::{field.Name} " +
                        $"[{field.FieldSig?.Type.FullName ?? "unknown"}]");
                    continue;
                }

                var directFields = proxyType.Fields
                    .Where(candidate => candidate.Name == field.Name && candidate.IsStatic == isStatic)
                    .Where(candidate => candidate.FieldSig is not null && field.FieldSig is not null &&
                                        TypeIdentity(candidate.FieldSig.Type) == TypeIdentity(field.FieldSig.Type))
                    .ToArray();
                if (directFields.Length == 1)
                {
                    passthroughs.Add(new PassthroughRecord(
                        method.FullName,
                        instruction.Offset,
                        instruction.OpCode.Name,
                        field.FullName,
                        "compatible proxy field"));
                    continue;
                }
                if (directFields.Length > 1)
                {
                    issues.Add($"{location}: proxy field is ambiguous: {field.FullName}");
                    continue;
                }

                var candidates = proxyType.Methods
                    .Where(candidate => candidate.Name == accessorName && candidate.IsStatic == isStatic)
                    .Where(candidate => AccessorMatchesField(candidate, field, isWrite) ||
                                        ArrayGetterMatchesField(candidate, field, isWrite) ||
                                        ListGetterMatchesField(candidate, field, isWrite))
                    .ToArray();
                if (candidates.Length != 1)
                {
                    issues.Add(
                        $"{location}: expected one compatible {accessorName}, found {candidates.Length} " +
                        $"for {assemblyName}!{field.DeclaringType.FullName}::{field.Name}");
                    continue;
                }

                var imported = importer.Import(candidates[0]);
                var arrayConverter = ArrayGetterMatchesField(candidates[0], field, isWrite)
                    ? CreateArrayToManagedConverter(module, importer, field)
                    : ListGetterMatchesField(candidates[0], field, isWrite)
                        ? CreateListToManagedConverter(module, importer, field)
                        : null;
                var record = new RewriteRecord(
                    method.FullName,
                    instruction.Offset,
                    instruction.OpCode.Name,
                    field.FullName,
                    arrayConverter is null
                        ? imported.FullName
                        : imported.FullName + " -> " + arrayConverter.FullName);
                pending.Add(new RewritePlan(method, instruction, imported, arrayConverter, record));
            }


            foreach (var type in module.GetTypes())
            foreach (var method in type.Methods.Where(method => method.HasBody))
            foreach (var instruction in method.Body.Instructions)
            {
                if (!MethodCodes.Contains(instruction.OpCode.Code) || instruction.Operand is not IMethod target)
                    continue;
                if (managedCallBridgeInstructions.Contains(instruction))
                    continue;

                var declaringType = target.DeclaringType;
                var assemblyName = declaringType.DefinitionAssembly?.Name?.String;
                if (string.IsNullOrWhiteSpace(assemblyName))
                    continue;

                var identity = MethodIdentity(target);
                var surfaceEntry = MethodSurfaceEntry(assemblyName, declaringType, target, target.MethodSig);
                if (!proxyAssemblies.Contains(assemblyName))
                {
                    if (RequiresGeneratedProxy(assemblyName))
                    {
                        methodIssues.Add(new MethodIssueRecord(
                            method.FullName,
                            instruction.Offset,
                            instruction.OpCode.Name,
                            identity,
                            surfaceEntry,
                            $"generated proxy assembly missing: {assemblyName}"));
                    }
                    continue;
                }
                if (!proxyModules.TryGetValue(assemblyName, out var proxyModule))
                {
                    methodIssues.Add(new MethodIssueRecord(
                        method.FullName, instruction.Offset, instruction.OpCode.Name, identity,
                        surfaceEntry, "proxy assembly not loaded"));
                    continue;
                }

                var proxyType = proxyModule.Find(SurfaceDeclaringTypeName(declaringType), isReflectionName: false);
                if (proxyType is null)
                {
                    methodIssues.Add(new MethodIssueRecord(
                        method.FullName, instruction.Offset, instruction.OpCode.Name, identity,
                        surfaceEntry, "proxy type missing"));
                    continue;
                }

                var targetSig = target.MethodSig;
                if (targetSig is null)
                {
                    methodIssues.Add(new MethodIssueRecord(
                        method.FullName, instruction.Offset, instruction.OpCode.Name, identity,
                        surfaceEntry, "target method has no signature"));
                    continue;
                }
                var targetGenericArity = target is MethodSpec methodSpec
                    ? (uint)(methodSpec.GenericInstMethodSig?.GenericArguments.Count ?? 0)
                    : targetSig.GenParamCount;
                var namedCandidates = proxyType.Methods
                    .Where(candidate => candidate.Name == target.Name)
                    .ToArray();
                var candidates = namedCandidates
                    .Where(candidate => candidate.IsStatic == !targetSig.HasThis)
                    .Where(candidate => (uint)candidate.GenericParameters.Count == targetGenericArity)
                    .Where(candidate => MethodSignatureMatches(candidate.MethodSig, targetSig))
                    .ToArray();
                IMethod? returnConverter = null;
                IMethod? argumentConverter = null;
                var argumentIndex = -1;
                if (candidates.Length == 0)
                {
                    var bridgedCandidates = namedCandidates
                        .Where(candidate => candidate.IsStatic == !targetSig.HasThis)
                        .Where(candidate => (uint)candidate.GenericParameters.Count == targetGenericArity)
                        .Where(candidate => MethodParametersMatch(candidate.MethodSig, targetSig))
                        .Select(candidate => new
                        {
                            Method = candidate,
                            Converter = CreateMethodReturnConverter(
                                module,
                                importer,
                                candidate.MethodSig is null
                                    ? null
                                    : ResolveMethodReturnType(candidate.MethodSig.RetType, target as MethodSpec),
                                ResolveMethodReturnType(targetSig.RetType, target as MethodSpec),
                                GetFollowingUnboxType(method, instruction, proxyModules))
                        })
                        .Where(candidate => candidate.Converter is not null)
                        .ToArray();
                    if (bridgedCandidates.Length == 1)
                    {
                        candidates = [bridgedCandidates[0].Method];
                        returnConverter = bridgedCandidates[0].Converter;
                    }
                }
                if (candidates.Length == 0)
                {
                    var argumentBridges = namedCandidates
                        .Where(candidate => candidate.IsStatic == !targetSig.HasThis)
                        .Where(candidate => (uint)candidate.GenericParameters.Count == targetGenericArity)
                        .Select(candidate =>
                        {
                            var bridge = CreateMethodArgumentConverter(module, importer, candidate.MethodSig, targetSig);
                            return new { Method = candidate, Bridge = bridge };
                        })
                        .Where(candidate => candidate.Bridge is not null)
                        .ToArray();
                    if (argumentBridges.Length == 1 && argumentBridges[0].Bridge is { } bridge)
                    {
                        candidates = [argumentBridges[0].Method];
                        argumentConverter = bridge.Converter;
                        argumentIndex = bridge.ParameterIndex;
                    }
                }
                if (candidates.Length == 0 &&
                    CreateUnityActionConstructorRewrite(module, importer, target, targetSig) is { } delegateConstructor)
                {
                    methodRewrites.Add(new MethodRewritePlan(
                        method,
                        instruction,
                        delegateConstructor.ManagedConstructor,
                        -1,
                        null,
                        delegateConstructor.ToIl2CppConverter));
                    methodCalls.Add(new MethodCallRecord(
                        method.FullName,
                        instruction.Offset,
                        instruction.OpCode.Name,
                        identity,
                        surfaceEntry,
                        delegateConstructor.ManagedConstructor.FullName + " -> " +
                        delegateConstructor.ToIl2CppConverter.FullName));
                    continue;
                }
                if (candidates.Length != 1)
                {
                    methodIssues.Add(new MethodIssueRecord(
                        method.FullName, instruction.Offset, instruction.OpCode.Name, identity,
                        surfaceEntry,
                        $"expected one exact proxy method, found {candidates.Length}; " +
                        $"same-name candidates: {DescribeMethodCandidates(namedCandidates)}; " +
                        $"resolved target return: {TypeIdentity(ResolveMethodReturnType(targetSig.RetType, target as MethodSpec))}; " +
                        $"resolved candidate returns: {string.Join(",", namedCandidates.Where(candidate => candidate.MethodSig is not null).Select(candidate => TypeIdentity(ResolveMethodReturnType(candidate.MethodSig!.RetType, target as MethodSpec))))}"));
                    continue;
                }

                if (returnConverter is not null || argumentConverter is not null)
                {
                    var proxyMethod = ImportProxyMethod(importer, candidates[0], target as MethodSpec);
                    methodRewrites.Add(new MethodRewritePlan(
                        method,
                        instruction,
                        proxyMethod,
                        argumentIndex,
                        argumentConverter,
                        returnConverter));
                }

                methodCalls.Add(new MethodCallRecord(
                    method.FullName,
                    instruction.Offset,
                    instruction.OpCode.Name,
                    identity,
                    surfaceEntry,
                    returnConverter is null && argumentConverter is null
                        ? candidates[0].FullName
                        : candidates[0].FullName +
                          (argumentConverter is null ? string.Empty : " <- " + argumentConverter.FullName) +
                          (returnConverter is null ? string.Empty : " -> " + returnConverter.FullName)));
            }

            var outputWritten = false;
            if (issues.Count == 0 && methodIssues.Count == 0 && managedBridgeIssues.Count == 0)
            {
                foreach (var item in pending)
                {
                    item.Instruction.OpCode = OpCodes.Call;
                    item.Instruction.Operand = item.Accessor;
                    if (item.ArrayConverter is not null)
                    {
                        var instructionIndex = item.Method.Body.Instructions.IndexOf(item.Instruction);
                        item.Method.Body.Instructions.Insert(
                            instructionIndex + 1,
                            Instruction.Create(OpCodes.Call, item.ArrayConverter));
                    }
                }

                foreach (var item in fieldConstantPlans)
                {
                    item.Instruction.OpCode = OpCodes.Ldc_I4;
                    item.Instruction.Operand = item.Value;
                }

                foreach (var item in methodRewrites)
                {
                    if (item.ArgumentConverter is not null)
                        InsertArgumentConverter(importer, item);
                    item.Instruction.Operand = item.ProxyMethod;
                    if (item.ReturnConverter is not null)
                    {
                        var instructionIndex = item.Method.Body.Instructions.IndexOf(item.Instruction);
                        item.Method.Body.Instructions.Insert(
                            instructionIndex + 1,
                            Instruction.Create(OpCodes.Call, item.ReturnConverter));
                    }
                }

                foreach (var item in managedBridgePlans)
                    ApplyManagedBridgeRewrite(item);
                foreach (var item in managedCallBridgePlans)
                    ApplyManagedBridgeRewrite(item);
                foreach (var item in managedReadProgressGuardPlans)
                    ApplyManagedReadProgressGuard(item);
                foreach (var item in managedPollingWaitPlans)
                {
                    item.YieldInstruction.OpCode = OpCodes.Call;
                    item.YieldInstruction.Operand = item.BridgeMethod;
                }
                foreach (var item in managedOptionalDelegatePlans)
                    ApplyManagedOptionalDelegateRewrite(item);
                foreach (var item in proxyCastPlans)
                {
                    item.Instruction.OpCode = OpCodes.Call;
                    item.Instruction.Operand = item.BridgeMethod;
                }
                foreach (var item in opaqueTypeErasurePlans)
                    ApplyOpaqueTypeErasure(module, item);
                NormalizeBranchEncodings(module);

                if (!options.AuditOnly)
                {
                    var outputDirectory = Path.GetDirectoryName(options.OutputPath)!;
                    Directory.CreateDirectory(outputDirectory);
                    var temporaryPath = options.OutputPath + ".tmp";
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                    try
                    {
                        module.Write(temporaryPath);
                        File.Move(temporaryPath, options.OutputPath, overwrite: true);
                        outputWritten = true;
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath))
                            File.Delete(temporaryPath);
                    }
                }
            }
            else if (!options.AuditOnly && File.Exists(options.OutputPath))
            {
                File.Delete(options.OutputPath);
            }

            return new RewriteReport(
                ModAssemblyRewriteApi.FormatVersion,
                Path.GetFullPath(options.InputPath),
                Path.GetFullPath(options.OutputPath),
                Path.GetFullPath(options.ProxyDirectory),
                options.AuditOnly,
                scanned,
                outputWritten,
                pending.Select(item => item.Record).ToArray(),
                passthroughs,
                methodCalls,
                methodIssues,
                managedBridgeRewrites,
                managedBridgeIssues,
                patchMetadataRewrites,
                issues);
        }
        finally
        {
            managedBridgeModule?.Dispose();
            foreach (var module in proxyModules.Values)
                module.Dispose();
            foreach (var module in managedOwnedModules.Values)
                module.Dispose();
        }
    }

    private static IReadOnlyList<PatchMetadataRewriteRecord> RewriteJalibPatchMetadata(
        ModuleDef module,
        List<string> issues)
    {
        const string attributeTypeName = "JALib.Core.Patch.JAPatchAttribute";
        var rewrites = new List<PatchMetadataRewriteRecord>();

        foreach (var type in module.GetTypes())
        foreach (var method in type.Methods)
        foreach (var attribute in method.CustomAttributes.Where(candidate =>
                     candidate.AttributeType.FullName == attributeTypeName))
        {
            var signature = attribute.Constructor.MethodSig;
            if (signature == null || signature.Params.Count == 0)
                continue;
            if (TypeIdentity(signature.Params[0]) != "System.Type")
                continue;

            var location = method.FullName + "@" + attributeTypeName;
            if (signature.Params.Count != 4 ||
                TypeIdentity(signature.Params[1]) != "System.String" ||
                TypeIdentity(signature.Params[3]) != "System.Boolean" ||
                attribute.ConstructorArguments.Count != 4)
            {
                issues.Add($"{location}: unsupported type-based JAPatch constructor shape");
                continue;
            }

            var targetTypeName = attribute.ConstructorArguments[0].Value switch
            {
                TypeSig targetType => targetType.FullName,
                ITypeDefOrRef targetType => targetType.FullName,
                _ => null
            };
            if (string.IsNullOrWhiteSpace(targetTypeName))
            {
                issues.Add($"{location}: JAPatch target type metadata could not be decoded");
                continue;
            }

            var stringConstructor = new MemberRefUser(
                module,
                ".ctor",
                MethodSig.CreateInstance(
                    module.CorLibTypes.Void,
                    module.CorLibTypes.String,
                    signature.Params[1],
                    signature.Params[2],
                    signature.Params[3]),
                attribute.Constructor.DeclaringType);
            attribute.Constructor = stringConstructor;
            attribute.ConstructorArguments[0] = new CAArgument(
                module.CorLibTypes.String,
                new UTF8String(targetTypeName));
            rewrites.Add(new PatchMetadataRewriteRecord(
                method.FullName,
                attributeTypeName,
                targetTypeName,
                "JAPatchAttribute(System.String,System.String,PatchType,System.Boolean)"));
        }

        return rewrites;
    }

    private static IReadOnlyList<ManagedReadProgressGuardPlan> PlanManagedReadProgressGuards(
        ModuleDef module,
        Importer importer,
        ModuleDef? bridgeModule,
        ManagedReadProgressGuardSpec? spec,
        List<ManagedBridgeRewriteRecord> rewrites,
        List<ManagedBridgeIssueRecord> issues)
    {
        if (spec is null)
            return Array.Empty<ManagedReadProgressGuardPlan>();
        if (bridgeModule is null)
        {
            issues.Add(new ManagedBridgeIssueRecord(
                spec.BridgeType,
                spec.RequireProgressMethod + "/" + spec.TryReadExactlyMethod,
                "managed bridge assembly is unavailable"));
            return Array.Empty<ManagedReadProgressGuardPlan>();
        }

        var bridgeType = bridgeModule.Find(spec.BridgeType, isReflectionName: false);
        var progressBridgeCandidates = bridgeType?.Methods
            .Where(method => method.Name == spec.RequireProgressMethod && method.IsStatic)
            .Where(method => method.MethodSig is
            {
                Params.Count: 1,
                RetType.ElementType: ElementType.I4
            } && method.MethodSig.Params[0].ElementType == ElementType.I4)
            .ToArray() ?? [];
        var tryReadBridgeCandidates = bridgeType?.Methods
            .Where(method => method.Name == spec.TryReadExactlyMethod && method.IsStatic)
            .Where(method => method.MethodSig is
            {
                Params.Count: 2,
                RetType.ElementType: ElementType.Boolean
            } &&
            method.MethodSig.Params[0].FullName == "System.IO.Stream" &&
            TypeIdentity(method.MethodSig.Params[1]) == "System.Byte[]")
            .ToArray() ?? [];
        if (progressBridgeCandidates.Length != 1 || tryReadBridgeCandidates.Length != 1)
        {
            issues.Add(new ManagedBridgeIssueRecord(
                spec.BridgeType,
                spec.RequireProgressMethod + "/" + spec.TryReadExactlyMethod,
                "expected one static int(int) progress bridge and one static " +
                $"bool(Stream,byte[]) exact-read bridge; found " +
                $"{progressBridgeCandidates.Length}/{tryReadBridgeCandidates.Length}"));
            return Array.Empty<ManagedReadProgressGuardPlan>();
        }

        var progressBridgeMethod = importer.Import(progressBridgeCandidates[0]);
        var tryReadBridgeMethod = importer.Import(tryReadBridgeCandidates[0]);
        var plans = new List<ManagedReadProgressGuardPlan>();
        foreach (var type in module.GetTypes())
        foreach (var method in type.Methods.Where(method => method.HasBody))
        {
            var instructions = method.Body.Instructions;
            for (var readIndex = 0; readIndex < instructions.Count; readIndex++)
            {
                var read = instructions[readIndex];
                if (read.OpCode.Code is not (Code.Call or Code.Callvirt) ||
                    read.Operand is not IMethod target ||
                    !IsFiniteByteBufferRead(target) ||
                    !TryMatchFiniteReadProgressLoop(method.Body, readIndex))
                {
                    continue;
                }

                var replaceMethod = CanReplaceWithTryReadExactly(method, read);
                var bridgeMethod = replaceMethod ? tryReadBridgeMethod : progressBridgeMethod;
                plans.Add(new ManagedReadProgressGuardPlan(
                    method,
                    read,
                    bridgeMethod,
                    replaceMethod));
                rewrites.Add(new ManagedBridgeRewriteRecord(
                    method.FullName,
                    read.Offset,
                    read.OpCode.Name,
                    target.FullName + " [finite-progress-loop]",
                    bridgeMethod.FullName,
                    0));
            }
        }
        return plans;
    }

    private static bool CanReplaceWithTryReadExactly(MethodDef method, Instruction read)
    {
        if (!method.IsStatic ||
            method.MethodSig is not { Params.Count: 2, RetType.ElementType: ElementType.Boolean } signature ||
            signature.Params[0].FullName is not ("System.IO.Stream" or "System.IO.FileStream") ||
            TypeIdentity(signature.Params[1]) != "System.Byte[]" ||
            method.Body.ExceptionHandlers.Count != 0)
        {
            return false;
        }

        return method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
            .All(instruction => ReferenceEquals(instruction, read));
    }

    private static IReadOnlyList<ManagedPollingWaitRewritePlan> PlanManagedPollingWaitRewrites(
        ModuleDef module,
        Importer importer,
        ModuleDef? bridgeModule,
        ManagedPollingWaitRewriteSpec? spec,
        List<ManagedBridgeRewriteRecord> rewrites,
        List<ManagedBridgeIssueRecord> issues)
    {
        if (spec is null)
            return Array.Empty<ManagedPollingWaitRewritePlan>();
        if (bridgeModule is null)
        {
            issues.Add(new ManagedBridgeIssueRecord(
                spec.BridgeType,
                spec.BridgeMethod,
                "managed bridge assembly is unavailable"));
            return Array.Empty<ManagedPollingWaitRewritePlan>();
        }

        var bridgeType = bridgeModule.Find(spec.BridgeType, isReflectionName: false);
        var bridgeCandidates = bridgeType?.Methods
            .Where(method => method.Name == spec.BridgeMethod && method.IsStatic)
            .Where(method => method.MethodSig is
            {
                Params.Count: 0,
                RetType.ElementType: ElementType.Boolean
            })
            .ToArray() ?? [];
        if (bridgeCandidates.Length != 1)
        {
            issues.Add(new ManagedBridgeIssueRecord(
                spec.BridgeType,
                spec.BridgeMethod,
                $"expected one static bool() polling bridge; found {bridgeCandidates.Length}"));
            return Array.Empty<ManagedPollingWaitRewritePlan>();
        }

        var bridgeMethod = importer.Import(bridgeCandidates[0]);
        var plans = new List<ManagedPollingWaitRewritePlan>();
        foreach (var type in module.GetTypes())
        foreach (var method in type.Methods.Where(method => method.HasBody))
        {
            var instructions = method.Body.Instructions;
            for (var index = 0; index < instructions.Count; index++)
            {
                var instruction = instructions[index];
                if (!IsThreadYield(instruction) ||
                    !TryMatchCoarseClockSpinLoop(method.Body, index, out var clockMethod))
                {
                    continue;
                }

                plans.Add(new ManagedPollingWaitRewritePlan(method, instruction, bridgeMethod));
                rewrites.Add(new ManagedBridgeRewriteRecord(
                    method.FullName,
                    instruction.Offset,
                    instruction.OpCode.Name,
                    instruction.Operand + $" [coarse-clock-spin:{clockMethod}]",
                    bridgeMethod.FullName,
                    0));
            }
        }
        return plans;
    }

    private static bool IsThreadYield(Instruction instruction)
        => instruction.OpCode.Code == Code.Call &&
           instruction.Operand is IMethod method &&
           method.Name == "Yield" &&
           method.DeclaringType.FullName == "System.Threading.Thread" &&
           method.MethodSig is
           {
               HasThis: false,
               Params.Count: 0,
               RetType.ElementType: ElementType.Boolean
           };

    private static bool TryMatchCoarseClockSpinLoop(
        CilBody body,
        int yieldIndex,
        out string clockMethod)
    {
        clockMethod = string.Empty;
        var instructions = body.Instructions;
        var popIndex = NextNonNop(instructions, yieldIndex + 1);
        if (popIndex < 0 || instructions[popIndex].OpCode.Code != Code.Pop)
            return false;

        var clockReadIndex = -1;
        for (var index = popIndex + 1;
             index < Math.Min(instructions.Count, popIndex + 8);
             index++)
        {
            if (IsCoarseClockRead(instructions[index], out _))
            {
                clockReadIndex = index;
                break;
            }
        }
        if (clockReadIndex < 0 ||
            instructions[clockReadIndex].Operand is not IMethod clockRead)
        {
            return false;
        }

        var storeIndex = NextNonNop(instructions, clockReadIndex + 1);
        if (storeIndex < 0 ||
            !TryGetStoredLocal(body, instructions[storeIndex], out var currentClock))
        {
            return false;
        }

        var branchIndex = -1;
        for (var index = storeIndex + 1;
             index < Math.Min(instructions.Count, storeIndex + 12);
             index++)
        {
            if (instructions[index].OpCode.Code is not (Code.Beq or Code.Beq_S) ||
                instructions[index].Operand is not Instruction branchTarget)
            {
                continue;
            }
            var targetIndex = instructions.IndexOf(branchTarget);
            if (targetIndex < 0)
                continue;
            targetIndex = NextNonNop(instructions, targetIndex);
            if (targetIndex != yieldIndex)
                continue;
            branchIndex = index;
            break;
        }
        if (branchIndex < 0 ||
            !Enumerable.Range(storeIndex + 1, branchIndex - storeIndex - 1)
                .Any(index => LoadsLocal(body, instructions[index], currentClock)))
        {
            return false;
        }

        for (var index = yieldIndex; index < branchIndex; index++)
        {
            var instruction = instructions[index];
            if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj))
                continue;
            if (ReferenceEquals(instruction, instructions[yieldIndex]))
                continue;
            if (index == clockReadIndex)
                continue;
            return false;
        }

        var foundInitialRead = false;
        for (var index = Math.Max(0, yieldIndex - 16); index < yieldIndex; index++)
        {
            if (!IsSameCoarseClockRead(instructions[index], clockRead))
                continue;
            var initialStoreIndex = NextNonNop(instructions, index + 1);
            if (initialStoreIndex >= 0 && initialStoreIndex < yieldIndex &&
                TryGetStoredLocal(body, instructions[initialStoreIndex], out var initialClock) &&
                ReferenceEquals(initialClock, currentClock))
            {
                foundInitialRead = true;
                break;
            }
        }
        if (!foundInitialRead)
            return false;

        clockMethod = clockRead.FullName;
        return true;
    }

    private static bool IsCoarseClockRead(Instruction instruction, out IMethod? method)
    {
        method = instruction.Operand as IMethod;
        return instruction.OpCode.Code is Code.Call or Code.Callvirt &&
               method?.Name == "get_ElapsedMilliseconds" &&
               method.DeclaringType.FullName == "System.Diagnostics.Stopwatch" &&
               method.MethodSig is
               {
                   HasThis: true,
                   Params.Count: 0,
                   RetType.ElementType: ElementType.I8
               };
    }

    private static bool IsSameCoarseClockRead(Instruction instruction, IMethod expected)
        => IsCoarseClockRead(instruction, out var method) &&
           method is not null &&
           string.Equals(method.FullName, expected.FullName, StringComparison.Ordinal);

    private static bool IsFiniteByteBufferRead(IMethod target)
    {
        if (target.Name != "Read" ||
            target.DeclaringType.FullName is not ("System.IO.Stream" or "System.IO.FileStream") ||
            target.MethodSig is not { HasThis: true, Params.Count: 3 } signature ||
            signature.RetType.ElementType != ElementType.I4)
        {
            return false;
        }

        return TypeIdentity(signature.Params[0]) == "System.Byte[]" &&
               signature.Params[1].ElementType == ElementType.I4 &&
               signature.Params[2].ElementType == ElementType.I4;
    }

    private static bool TryMatchFiniteReadProgressLoop(CilBody body, int readIndex)
    {
        var instructions = body.Instructions;
        var addIndex = NextNonNop(instructions, readIndex + 1);
        if (addIndex < 0 || instructions[addIndex].OpCode.Code != Code.Add)
            return false;
        var storeIndex = NextNonNop(instructions, addIndex + 1);
        if (storeIndex < 0 || !TryGetStoredLocal(body, instructions[storeIndex], out var progress))
            return false;

        var progressLoadIndex = -1;
        for (var index = readIndex - 1; index >= Math.Max(0, readIndex - 24); index--)
        {
            if (LoadsLocal(body, instructions[index], progress))
            {
                progressLoadIndex = index;
                break;
            }
        }
        if (progressLoadIndex < 0)
            return false;

        for (var branchIndex = storeIndex + 1;
             branchIndex < Math.Min(instructions.Count, storeIndex + 32);
             branchIndex++)
        {
            var branch = instructions[branchIndex];
            if (branch.OpCode.Code is not (Code.Blt or Code.Blt_S or Code.Blt_Un or Code.Blt_Un_S) ||
                branch.Operand is not Instruction target)
            {
                continue;
            }
            var targetIndex = instructions.IndexOf(target);
            if (targetIndex < 0 || targetIndex > progressLoadIndex)
                continue;

            var loadsProgress = false;
            var loadsFiniteLength = false;
            for (var index = storeIndex + 1; index < branchIndex; index++)
            {
                loadsProgress |= LoadsLocal(body, instructions[index], progress);
                loadsFiniteLength |= instructions[index].OpCode.Code == Code.Ldlen;
            }
            if (loadsProgress && loadsFiniteLength)
                return true;
        }
        return false;
    }

    private static int NextNonNop(IList<Instruction> instructions, int start)
    {
        for (var index = start; index < instructions.Count; index++)
        {
            if (instructions[index].OpCode.Code != Code.Nop)
                return index;
        }
        return -1;
    }

    private static bool TryGetStoredLocal(CilBody body, Instruction instruction, out Local local)
    {
        local = null!;
        var index = instruction.OpCode.Code switch
        {
            Code.Stloc_0 => 0,
            Code.Stloc_1 => 1,
            Code.Stloc_2 => 2,
            Code.Stloc_3 => 3,
            _ => -1
        };
        if (index >= 0)
        {
            if (index >= body.Variables.Count)
                return false;
            local = body.Variables[index];
            return true;
        }
        if (instruction.OpCode.Code is not (Code.Stloc or Code.Stloc_S) ||
            instruction.Operand is not Local operand)
        {
            return false;
        }
        local = operand;
        return true;
    }

    private static bool LoadsLocal(CilBody body, Instruction instruction, Local expected)
    {
        var index = instruction.OpCode.Code switch
        {
            Code.Ldloc_0 => 0,
            Code.Ldloc_1 => 1,
            Code.Ldloc_2 => 2,
            Code.Ldloc_3 => 3,
            _ => -1
        };
        if (index >= 0)
            return index < body.Variables.Count && ReferenceEquals(body.Variables[index], expected);
        return instruction.OpCode.Code is Code.Ldloc or Code.Ldloc_S &&
               ReferenceEquals(instruction.Operand, expected);
    }

    private static void ApplyManagedReadProgressGuard(ManagedReadProgressGuardPlan plan)
    {
        if (plan.ReplaceMethod)
        {
            var body = plan.Method.Body;
            body.Instructions.Clear();
            body.ExceptionHandlers.Clear();
            body.Variables.Clear();
            body.InitLocals = false;
            body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
            body.Instructions.Add(Instruction.Create(OpCodes.Call, plan.BridgeMethod));
            body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            return;
        }

        var instructions = plan.Method.Body.Instructions;
        var index = instructions.IndexOf(plan.ReadInstruction);
        if (index < 0)
            throw new InvalidOperationException(
                $"Managed read progress guard lost its source instruction: {plan.Method.FullName}");
        instructions.Insert(index + 1, Instruction.Create(OpCodes.Call, plan.BridgeMethod));
    }

    private static IReadOnlyList<ManagedBridgeRewritePlan> PlanManagedBridgeRewrites(
        ModuleDef module,
        Importer importer,
        ModuleDef? bridgeModule,
        IReadOnlyList<ManagedBridgeRewriteSpec> specs,
        List<ManagedBridgeRewriteRecord> rewrites,
        List<ManagedBridgeIssueRecord> issues)
    {
        if (specs.Count == 0)
            return Array.Empty<ManagedBridgeRewritePlan>();
        if (bridgeModule is null)
            throw new InvalidOperationException("Managed bridge rewrite specs require a bridge assembly.");

        var plans = new List<ManagedBridgeRewritePlan>();
        var claimedCallsiteTokens = new Dictionary<int, string>();
        var seenSources = new Dictionary<string, ManagedBridgeRewriteSpec>(StringComparer.Ordinal);
        foreach (var spec in specs
                     .OrderBy(item => item.SourceType, StringComparer.Ordinal)
                     .ThenBy(item => item.SourceMethod, StringComparer.Ordinal))
        {
            var sourceKey = spec.SourceType + "::" + spec.SourceMethod + "(" +
                            string.Join(",", spec.SourceParameterTypes) + ")";
            if (seenSources.TryGetValue(sourceKey, out var previous))
            {
                if (!string.Equals(previous.BridgeType, spec.BridgeType, StringComparison.Ordinal) ||
                    !string.Equals(previous.BridgeMethod, spec.BridgeMethod, StringComparison.Ordinal) ||
                    !string.Equals(previous.AppendOwnerId, spec.AppendOwnerId, StringComparison.Ordinal) ||
                    previous.AppendCallsiteToken != spec.AppendCallsiteToken)
                {
                    issues.Add(new ManagedBridgeIssueRecord(
                        spec.SourceType,
                        spec.SourceMethod,
                        $"conflicting bridge mappings: {previous.BridgeType}.{previous.BridgeMethod} and " +
                        $"{spec.BridgeType}.{spec.BridgeMethod}"));
                }
                continue;
            }
            seenSources.Add(sourceKey, spec);

            var sourceTypes = module.GetTypes()
                .Where(type => string.Equals(type.FullName, spec.SourceType, StringComparison.Ordinal))
                .ToArray();
            if (sourceTypes.Length != 1)
            {
                issues.Add(new ManagedBridgeIssueRecord(
                    spec.SourceType,
                    spec.SourceMethod,
                    $"expected one source type, found {sourceTypes.Length}"));
                continue;
            }

            var sourceCandidates = sourceTypes[0].Methods
                .Where(method => method.Name == spec.SourceMethod)
                .Where(method => SourceParametersMatch(method.MethodSig, spec.SourceParameterTypes))
                .ToArray();
            if (sourceCandidates.Length != 1)
            {
                issues.Add(new ManagedBridgeIssueRecord(
                    spec.SourceType,
                    spec.SourceMethod,
                    $"expected one source overload, found {sourceCandidates.Length}: " +
                    DescribeMethodCandidates(sourceCandidates)));
                continue;
            }

            var source = sourceCandidates[0];
            if (!source.IsStatic || source.HasGenericParameters ||
                source.MethodSig is not { } sourceSignature)
            {
                issues.Add(new ManagedBridgeIssueRecord(
                    spec.SourceType,
                    spec.SourceMethod,
                    "source stand-in must be a non-generic static method"));
                continue;
            }

            var bridgeTypes = bridgeModule.GetTypes()
                .Where(type => string.Equals(type.FullName, spec.BridgeType, StringComparison.Ordinal))
                .ToArray();
            if (bridgeTypes.Length != 1)
            {
                issues.Add(new ManagedBridgeIssueRecord(
                    spec.SourceType,
                    spec.SourceMethod,
                    $"expected one bridge type {spec.BridgeType}, found {bridgeTypes.Length}"));
                continue;
            }

            var bridgeCandidates = bridgeTypes[0].Methods
                .Where(method => method.Name == spec.BridgeMethod)
                .Where(method => method.IsStatic && !method.HasGenericParameters)
                .Where(method => ManagedBridgeSignatureMatches(
                    sourceSignature,
                    method.MethodSig,
                    spec.AppendCallsiteToken,
                    spec.AppendOwnerId != null))
                .ToArray();
            if (bridgeCandidates.Length != 1)
            {
                issues.Add(new ManagedBridgeIssueRecord(
                    spec.SourceType,
                    spec.SourceMethod,
                    $"expected one ABI-compatible bridge method, found {bridgeCandidates.Length}: " +
                    DescribeMethodCandidates(bridgeTypes[0].Methods.Where(method => method.Name == spec.BridgeMethod))));
                continue;
            }

            var bridge = bridgeCandidates[0];
            var droppedArguments = bridge.MethodSig!.Params.Count == 0 &&
                                   spec.AppendOwnerId == null
                ? sourceSignature.Params.Count
                : 0;
            var callsites = module.GetTypes()
                .SelectMany(type => type.Methods.Where(method => method.HasBody))
                .SelectMany(method => method.Body.Instructions
                    .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
                    .Where(instruction => instruction.Operand is IMethod target &&
                                          MethodReferenceMatches(source, target))
                    .Select(instruction => (Method: method, Instruction: instruction)))
                .ToArray();
            if (callsites.Length == 0)
                continue;

            var importedBridge = importer.Import(bridge);
            foreach (var callsite in callsites)
            {
                if (callsite.Instruction.OpCode.Code != Code.Call)
                {
                    issues.Add(new ManagedBridgeIssueRecord(
                        spec.SourceType,
                        spec.SourceMethod,
                        $"unsupported call opcode {callsite.Instruction.OpCode.Name} at " +
                        $"{callsite.Method.FullName}@IL_{callsite.Instruction.Offset:X4}"));
                    continue;
                }

                var instructionIndex = callsite.Method.Body.Instructions.IndexOf(callsite.Instruction);
                if (instructionIndex > 0 &&
                    callsite.Method.Body.Instructions[instructionIndex - 1].OpCode.Code == Code.Tailcall)
                {
                    issues.Add(new ManagedBridgeIssueRecord(
                        spec.SourceType,
                        spec.SourceMethod,
                        $"tail-prefixed callsite is unsupported at {callsite.Method.FullName}@IL_{callsite.Instruction.Offset:X4}"));
                    continue;
                }

                int? callsiteToken = null;
                if (spec.AppendCallsiteToken)
                {
                    callsiteToken = ComputeManagedCallsiteToken(
                        module,
                        callsite.Method,
                        callsite.Instruction,
                        source);
                    var identity =
                        $"{callsite.Method.FullName}@IL_{callsite.Instruction.Offset:X4}:" +
                        source.FullName;
                    if (claimedCallsiteTokens.TryGetValue(callsiteToken.Value, out var existing) &&
                        !string.Equals(existing, identity, StringComparison.Ordinal))
                    {
                        issues.Add(new ManagedBridgeIssueRecord(
                            spec.SourceType,
                            spec.SourceMethod,
                            $"stable callsite token collision 0x{callsiteToken.Value:X8}: " +
                            $"{existing} vs {identity}"));
                        continue;
                    }
                    claimedCallsiteTokens[callsiteToken.Value] = identity;
                }

                plans.Add(new ManagedBridgeRewritePlan(
                    callsite.Method,
                    callsite.Instruction,
                    importedBridge,
                    droppedArguments,
                    null,
                    callsiteToken,
                    AppendedString: spec.AppendOwnerId));
                rewrites.Add(new ManagedBridgeRewriteRecord(
                    callsite.Method.FullName,
                    callsite.Instruction.Offset,
                    callsite.Instruction.OpCode.Name,
                    source.FullName,
                    importedBridge.FullName +
                    (callsiteToken is null ? string.Empty : $" token=0x{callsiteToken.Value:X8}") +
                    (spec.AppendOwnerId == null ? string.Empty : " owner=embedded"),
                    droppedArguments));
            }
        }

        return plans;
    }

    private static IReadOnlyList<ProxyCastRewritePlan> PlanProxyCastRewrites(
        ModuleDef module,
        Importer importer,
        ModuleDef? bridgeModule,
        IReadOnlyDictionary<string, ModuleDefMD> proxyModules,
        IReadOnlySet<Instruction> excludedInstructions,
        ManagedProxyCastBridgeSpec? spec,
        List<ManagedBridgeRewriteRecord> rewrites,
        List<ManagedBridgeIssueRecord> issues)
    {
        if (spec is null)
            return Array.Empty<ProxyCastRewritePlan>();
        if (bridgeModule is null)
            throw new InvalidOperationException("Managed proxy casts require a bridge assembly.");

        var bridgeType = bridgeModule.GetTypes().SingleOrDefault(type =>
            type.FullName == spec.BridgeType);
        if (bridgeType is null)
        {
            issues.Add(new ManagedBridgeIssueRecord(
                "<proxy-cast>",
                "<type>",
                $"expected one proxy cast bridge type {spec.BridgeType}"));
            return Array.Empty<ProxyCastRewritePlan>();
        }

        var isInstance = FindProxyCastBridgeMethod(
            bridgeType,
            spec.IsInstanceMethod,
            issues);
        var cast = FindProxyCastBridgeMethod(
            bridgeType,
            spec.CastMethod,
            issues);
        if (isInstance is null || cast is null)
            return Array.Empty<ProxyCastRewritePlan>();

        var importedIsInstance = (IMethodDefOrRef)importer.Import(isInstance);
        var importedCast = (IMethodDefOrRef)importer.Import(cast);
        var plans = new List<ProxyCastRewritePlan>();
        foreach (var type in module.GetTypes())
        foreach (var method in type.Methods.Where(method => method.HasBody))
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.OpCode.Code is not (Code.Isinst or Code.Castclass) ||
                instruction.Operand is not ITypeDefOrRef target ||
                excludedInstructions.Contains(instruction))
                continue;

            var assemblyName = target.DefinitionAssembly?.Name?.String;
            if (string.IsNullOrWhiteSpace(assemblyName) ||
                !proxyModules.TryGetValue(assemblyName, out var proxyModule))
                continue;
            var proxyType = proxyModule.Find(target.FullName, isReflectionName: false);
            if (proxyType is null || proxyType.IsInterface || proxyType.IsValueType)
                continue;

            var genericArgument = importer.Import(target.ToTypeSig());
            var bridgeDefinition = instruction.OpCode.Code == Code.Isinst
                ? importedIsInstance
                : importedCast;
            var bridge = new MethodSpecUser(
                bridgeDefinition,
                new GenericInstMethodSig(genericArgument));
            plans.Add(new ProxyCastRewritePlan(method, instruction, bridge));
            rewrites.Add(new ManagedBridgeRewriteRecord(
                method.FullName,
                instruction.Offset,
                instruction.OpCode.Name,
                instruction.OpCode.Name + " " + target.FullName,
                bridge.FullName,
                0));
        }
        return plans;
    }

    private static MethodDef? FindProxyCastBridgeMethod(
        TypeDef bridgeType,
        string methodName,
        List<ManagedBridgeIssueRecord> issues)
    {
        var candidates = bridgeType.Methods
            .Where(method => method.Name == methodName)
            .Where(method => method.IsStatic && method.GenericParameters.Count == 1)
            .Where(method => method.MethodSig is { } signature &&
                             signature.Params.Count == 1 &&
                             TypeIdentity(signature.Params[0]) == "System.Object" &&
                             TypeIdentity(signature.RetType) == "!!0")
            .ToArray();
        if (candidates.Length == 1)
            return candidates[0];

        issues.Add(new ManagedBridgeIssueRecord(
            "<proxy-cast>",
            methodName,
            $"expected one generic proxy cast bridge method, found {candidates.Length}: " +
            DescribeMethodCandidates(bridgeType.Methods.Where(method => method.Name == methodName))));
        return null;
    }

    private static IReadOnlyList<ManagedOptionalDelegateRewritePlan> PlanManagedOptionalDelegateRewrites(
        ModuleDef module,
        Importer importer,
        ModuleDef? bridgeModule,
        ManagedOptionalDelegateRewriteSpec? spec,
        List<ManagedBridgeRewriteRecord> rewrites,
        List<ManagedBridgeIssueRecord> issues)
    {
        if (spec is null)
            return Array.Empty<ManagedOptionalDelegateRewritePlan>();
        if (bridgeModule is null)
            throw new InvalidOperationException("Managed optional delegate rewrite requires a bridge assembly.");

        var bridgeTypes = bridgeModule.GetTypes()
            .Where(type => string.Equals(type.FullName, spec.BridgeType, StringComparison.Ordinal))
            .ToArray();
        if (bridgeTypes.Length != 1)
        {
            issues.Add(new ManagedBridgeIssueRecord(
                spec.SourceType,
                spec.SourceMethodPrefix,
                $"expected one optional delegate bridge type {spec.BridgeType}, found {bridgeTypes.Length}"));
            return Array.Empty<ManagedOptionalDelegateRewritePlan>();
        }

        var bridgeCandidates = bridgeTypes[0].Methods
            .Where(method => method.Name == spec.BridgeMethod && method.IsStatic)
            .Where(method => method.MethodSig is { Params.Count: 3 } signature &&
                             TypeIdentity(signature.RetType) == spec.DelegateType &&
                             TypeIdentity(signature.Params[0]) == "System.Object" &&
                             TypeIdentity(signature.Params[1]) == "System.RuntimeMethodHandle" &&
                             TypeIdentity(signature.Params[2]) == "System.RuntimeTypeHandle")
            .ToArray();
        if (bridgeCandidates.Length != 1)
        {
            issues.Add(new ManagedBridgeIssueRecord(
                spec.SourceType,
                spec.SourceMethodPrefix,
                $"expected one compatible optional delegate bridge method, found {bridgeCandidates.Length}: " +
                DescribeMethodCandidates(bridgeTypes[0].Methods.Where(method => method.Name == spec.BridgeMethod))));
            return Array.Empty<ManagedOptionalDelegateRewritePlan>();
        }

        var bridgeMethod = importer.Import(bridgeCandidates[0]);
        var plans = new List<ManagedOptionalDelegateRewritePlan>();
        foreach (var type in module.GetTypes())
        foreach (var method in type.Methods.Where(candidate => candidate.HasBody))
        {
            var instructions = method.Body.Instructions;
            for (var callIndex = 0; callIndex < instructions.Count; callIndex++)
            {
                var call = instructions[callIndex];
                if (call.OpCode.Code is not (Code.Call or Code.Callvirt) ||
                    call.Operand is not IMethod sink ||
                    !OptionalDelegateSinkMatches(spec, sink))
                    continue;

                var constructorIndex = PreviousNonNopInstruction(instructions, callIndex - 1);
                if (constructorIndex < 0 ||
                    instructions[constructorIndex].OpCode.Code != Code.Newobj ||
                    instructions[constructorIndex].Operand is not IMethod constructor ||
                    !IsDelegateConstructor(constructor, spec.DelegateType))
                    continue;

                var functionIndex = PreviousNonNopInstruction(instructions, constructorIndex - 1);
                if (functionIndex < 0 ||
                    instructions[functionIndex].OpCode.Code is not (Code.Ldftn or Code.Ldvirtftn) ||
                    instructions[functionIndex].Operand is not IMethod callback ||
                    callback.MethodSig?.HasThis != true)
                    continue;

                Instruction? virtualReceiverDup = null;
                if (instructions[functionIndex].OpCode.Code == Code.Ldvirtftn)
                {
                    var dupIndex = PreviousNonNopInstruction(instructions, functionIndex - 1);
                    if (dupIndex < 0 || instructions[dupIndex].OpCode.Code != Code.Dup)
                    {
                        issues.Add(new ManagedBridgeIssueRecord(
                            method.FullName,
                            sink.Name,
                            $"optional virtual delegate has no direct receiver dup at IL_{call.Offset:X4}"));
                        continue;
                    }
                    virtualReceiverDup = instructions[dupIndex];
                }

                plans.Add(new ManagedOptionalDelegateRewritePlan(
                    method,
                    instructions[functionIndex],
                    instructions[constructorIndex],
                    virtualReceiverDup,
                    callback.DeclaringType,
                    bridgeMethod));
                rewrites.Add(new ManagedBridgeRewriteRecord(
                    method.FullName,
                    instructions[constructorIndex].Offset,
                    instructions[constructorIndex].OpCode.Name,
                    $"{sink.DeclaringType.FullName}::{sink.Name}:{spec.DelegateType}",
                    $"{spec.BridgeType}::{spec.BridgeMethod}",
                    0));
            }
        }
        return plans;
    }

    private static bool OptionalDelegateSinkMatches(
        ManagedOptionalDelegateRewriteSpec spec,
        IMethod target)
    {
        var signature = target.MethodSig;
        return string.Equals(
                   target.DeclaringType.DefinitionAssembly?.Name?.String,
                   spec.SourceAssembly,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(target.DeclaringType.FullName, spec.SourceType, StringComparison.Ordinal) &&
               target.Name.String.StartsWith(spec.SourceMethodPrefix, StringComparison.Ordinal) &&
               signature is { Params.Count: > 0 } &&
               TypeIdentity(signature.Params[^1]) == spec.DelegateType;
    }

    private static bool IsDelegateConstructor(IMethod target, string delegateType)
    {
        var signature = target.MethodSig;
        return target.Name == ".ctor" &&
               target.DeclaringType.FullName == delegateType &&
               signature is { Params.Count: 2 } &&
               TypeIdentity(signature.Params[0]) == "System.Object" &&
               TypeIdentity(signature.Params[1]) == "System.IntPtr";
    }

    private static int PreviousNonNopInstruction(IList<Instruction> instructions, int index)
    {
        while (index >= 0 && instructions[index].OpCode.Code == Code.Nop)
            --index;
        return index;
    }

    private static void ApplyManagedOptionalDelegateRewrite(ManagedOptionalDelegateRewritePlan plan)
    {
        if (plan.VirtualReceiverDup is not null)
        {
            plan.VirtualReceiverDup.OpCode = OpCodes.Nop;
            plan.VirtualReceiverDup.Operand = null;
        }

        plan.FunctionInstruction.OpCode = OpCodes.Ldtoken;
        var instructions = plan.Method.Body.Instructions;
        var constructorIndex = instructions.IndexOf(plan.ConstructorInstruction);
        if (constructorIndex < 0)
            throw new InvalidOperationException("Optional delegate rewrite lost its constructor instruction.");
        instructions.Insert(
            constructorIndex,
            Instruction.Create(OpCodes.Ldtoken, plan.CallbackDeclaringType));
        plan.ConstructorInstruction.OpCode = OpCodes.Call;
        plan.ConstructorInstruction.Operand = plan.BridgeMethod;
    }

    private static IReadOnlyList<ManagedBridgeRewritePlan> PlanManagedCallBridgeRewrites(
        ModuleDef module,
        Importer importer,
        ModuleDef? bridgeModule,
        IReadOnlyDictionary<string, ModuleDefMD> proxyModules,
        IReadOnlyDictionary<string, ModuleDefMD> managedOwnedModules,
        IReadOnlyList<ManagedCallBridgeRewriteSpec> specs,
        List<ManagedBridgeRewriteRecord> rewrites,
        List<ManagedBridgeIssueRecord> issues)
    {
        if (specs.Count == 0)
            return Array.Empty<ManagedBridgeRewritePlan>();
        if (bridgeModule is null)
            throw new InvalidOperationException("Managed call bridge specs require a bridge assembly.");

        var plans = new List<ManagedBridgeRewritePlan>();
        var claimedCallsites = new HashSet<Instruction>();
        var claimedCallsiteTokens = new Dictionary<int, string>();
        var seenSpecs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in specs
                     .OrderBy(item => item.SourceAssembly, StringComparer.Ordinal)
                     .ThenBy(item => item.SourceType, StringComparer.Ordinal)
                     .ThenBy(item => item.SourceMethod, StringComparer.Ordinal))
        {
            var sourceKey = DescribeManagedCallSource(spec);
            if (!seenSpecs.Add(sourceKey))
            {
                issues.Add(new ManagedBridgeIssueRecord(
                    spec.SourceType,
                    spec.SourceMethod,
                    "duplicate external managed call bridge identity"));
                continue;
            }

            var hasErasedAssembly = !string.IsNullOrWhiteSpace(spec.ErasedTypeAssembly);
            var hasErasedType = !string.IsNullOrWhiteSpace(spec.ErasedType);
            if (hasErasedAssembly != hasErasedType)
            {
                issues.Add(new ManagedBridgeIssueRecord(
                    spec.SourceType,
                    spec.SourceMethod,
                    "erased type requires both assembly and full type name"));
                continue;
            }
            if (spec.BridgeGenericArgumentsFromSourceParameters is { } bridgeGenericSources &&
                bridgeGenericSources.Any(index =>
                    index < 0 || index >= spec.SourceParameterTypes.Count))
            {
                issues.Add(new ManagedBridgeIssueRecord(
                    spec.SourceType,
                    spec.SourceMethod,
                    "bridge generic argument references an invalid source parameter"));
                continue;
            }

            proxyModules.TryGetValue(spec.SourceAssembly, out var sourceProxyModule);
            if (!spec.AllowUnproxiedSource && sourceProxyModule is null)
            {
                issues.Add(new ManagedBridgeIssueRecord(
                    spec.SourceType,
                    spec.SourceMethod,
                    $"generated proxy assembly missing for external bridge: {spec.SourceAssembly}"));
                continue;
            }
            if (hasErasedType &&
                (!proxyModules.TryGetValue(spec.ErasedTypeAssembly!, out var erasedProxyModule) ||
                 erasedProxyModule.Find(spec.ErasedType!, isReflectionName: false) is null))
            {
                issues.Add(new ManagedBridgeIssueRecord(
                    spec.SourceType,
                    spec.SourceMethod,
                    $"generated proxy type missing for erased handle: " +
                    $"{spec.ErasedTypeAssembly}!{spec.ErasedType}"));
                continue;
            }
            if (!spec.AllowUnproxiedSource &&
                sourceProxyModule!.Find(spec.SourceType, isReflectionName: false) is null)
            {
                issues.Add(new ManagedBridgeIssueRecord(
                    spec.SourceType,
                    spec.SourceMethod,
                    $"generated proxy type missing for external bridge: " +
                    $"{spec.SourceAssembly}!{spec.SourceType}"));
                continue;
            }

            var bridgeTypes = bridgeModule.GetTypes()
                .Where(type => string.Equals(type.FullName, spec.BridgeType, StringComparison.Ordinal))
                .ToArray();
            if (bridgeTypes.Length != 1)
            {
                issues.Add(new ManagedBridgeIssueRecord(
                    spec.SourceType,
                    spec.SourceMethod,
                    $"expected one bridge type {spec.BridgeType}, found {bridgeTypes.Length}"));
                continue;
            }

            var bridgeGenericArity = spec.BridgeGenericArgumentsFromSourceParameters?.Count ??
                                     checked((int)spec.SourceGenericArity);
            var bridgeCandidates = bridgeTypes[0].Methods
                .Where(method => method.Name == spec.BridgeMethod)
                .Where(method => method.IsStatic &&
                                 method.GenericParameters.Count == bridgeGenericArity)
                .Where(method => ManagedCallBridgeSignatureMatches(spec, method.MethodSig))
                .ToArray();
            if (bridgeCandidates.Length != 1)
            {
                issues.Add(new ManagedBridgeIssueRecord(
                    spec.SourceType,
                    spec.SourceMethod,
                    $"expected one compatible external bridge method, found {bridgeCandidates.Length}: " +
                    DescribeMethodCandidates(bridgeTypes[0].Methods.Where(method => method.Name == spec.BridgeMethod))));
                continue;
            }

            var callsites = module.GetTypes()
                .SelectMany(type => type.Methods.Where(method => method.HasBody))
                .SelectMany(method => method.Body.Instructions
                    .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
                    .Where(instruction => instruction.Operand is IMethod target &&
                                          ManagedCallSourceMatches(spec, target))
                    .Select(instruction => (Method: method, Instruction: instruction, Target: (IMethod)instruction.Operand)))
                .ToArray();
            foreach (var callsite in callsites)
            {
                if (spec.SourceIsStatic && callsite.Instruction.OpCode.Code != Code.Call)
                {
                    issues.Add(new ManagedBridgeIssueRecord(
                        spec.SourceType,
                        spec.SourceMethod,
                        $"static source uses unsupported opcode {callsite.Instruction.OpCode.Name} at " +
                        $"{callsite.Method.FullName}@IL_{callsite.Instruction.Offset:X4}"));
                    continue;
                }
                var sourceSignature = callsite.Target.MethodSig!;
                var sourceMethodSpec = callsite.Target as MethodSpec;
                if (spec.SourceGenericArity > 0 &&
                    (sourceMethodSpec?.GenericInstMethodSig is not { } genericArguments ||
                     genericArguments.GenericArguments.Count != spec.SourceGenericArity))
                {
                    issues.Add(new ManagedBridgeIssueRecord(
                        spec.SourceType,
                        spec.SourceMethod,
                        $"generic source callsite is not a closed MethodSpec at " +
                        $"{callsite.Method.FullName}@IL_{callsite.Instruction.Offset:X4}"));
                    continue;
                }
                var filterResult = EvaluateManagedCallGenericArgumentFilter(
                    module,
                    proxyModules,
                    managedOwnedModules,
                    spec,
                    sourceMethodSpec,
                    out var filterReason);
                if (filterResult == ManagedCallFilterResult.Skip)
                    continue;
                if (filterResult == ManagedCallFilterResult.Reject)
                {
                    issues.Add(new ManagedBridgeIssueRecord(
                        spec.SourceType,
                        spec.SourceMethod,
                        $"{filterReason} at " +
                        $"{callsite.Method.FullName}@IL_{callsite.Instruction.Offset:X4}"));
                    continue;
                }
                if (!claimedCallsites.Add(callsite.Instruction))
                {
                    issues.Add(new ManagedBridgeIssueRecord(
                        spec.SourceType,
                        spec.SourceMethod,
                        $"callsite claimed by multiple bridge specs at " +
                        $"{callsite.Method.FullName}@IL_{callsite.Instruction.Offset:X4}"));
                    continue;
                }
                var importedBridge = ImportManagedCallBridge(
                    importer,
                    bridgeCandidates[0],
                    spec,
                    sourceSignature,
                    sourceMethodSpec);
                if (sourceSignature.HasThis &&
                    spec.InstanceForwarding != ManagedCallInstanceForwarding.AsObject)
                {
                    issues.Add(new ManagedBridgeIssueRecord(
                        spec.SourceType,
                        spec.SourceMethod,
                        "instance source requires explicit AsObject forwarding"));
                    continue;
                }
                if (!sourceSignature.HasThis &&
                    spec.InstanceForwarding != ManagedCallInstanceForwarding.None)
                {
                    issues.Add(new ManagedBridgeIssueRecord(
                        spec.SourceType,
                        spec.SourceMethod,
                        "static source cannot request instance forwarding"));
                    continue;
                }

                var instructionIndex = callsite.Method.Body.Instructions.IndexOf(callsite.Instruction);
                if (instructionIndex > 0 &&
                    callsite.Method.Body.Instructions[instructionIndex - 1].OpCode.Code == Code.Tailcall)
                {
                    issues.Add(new ManagedBridgeIssueRecord(
                        spec.SourceType,
                        spec.SourceMethod,
                        $"tail-prefixed callsite is unsupported at " +
                        $"{callsite.Method.FullName}@IL_{callsite.Instruction.Offset:X4}"));
                    continue;
                }

                ITypeDefOrRef? returnCast = null;
                var bridgeReturn = ResolveMethodReturnType(
                    bridgeCandidates[0].MethodSig!.RetType,
                    sourceMethodSpec);
                var resolvedSourceReturn = ResolveMethodReturnType(
                    sourceSignature.RetType,
                    sourceMethodSpec);
                if (TypeIdentity(bridgeReturn) != TypeIdentity(resolvedSourceReturn))
                {
                    var sourceReturn = resolvedSourceReturn.ToTypeDefOrRef();
                    if (ManagedCallErasesReturnType(spec, TypeIdentity(resolvedSourceReturn)) &&
                        TypeIdentity(bridgeReturn) == "System.Object" &&
                        TypeIdentity(resolvedSourceReturn) != "System.Void")
                    {
                        returnCast = null;
                    }
                    else if (!spec.AllowObjectReturnCast ||
                        TypeIdentity(bridgeReturn) != "System.Object" ||
                        sourceReturn is null)
                    {
                        issues.Add(new ManagedBridgeIssueRecord(
                            spec.SourceType,
                            spec.SourceMethod,
                            "bridge return requires an unsupported conversion"));
                        continue;
                    }
                    else
                    {
                        returnCast = importer.Import(sourceReturn);
                    }
                }

                int? callsiteToken = null;
                if (spec.AppendCallsiteToken)
                {
                    callsiteToken = ComputeManagedCallsiteToken(
                        module,
                        callsite.Method,
                        callsite.Instruction,
                        callsite.Target);
                    var identity =
                        $"{callsite.Method.FullName}@IL_{callsite.Instruction.Offset:X4}:" +
                        MethodIdentity(callsite.Target);
                    if (claimedCallsiteTokens.TryGetValue(callsiteToken.Value, out var existing) &&
                        !string.Equals(existing, identity, StringComparison.Ordinal))
                    {
                        issues.Add(new ManagedBridgeIssueRecord(
                            spec.SourceType,
                            spec.SourceMethod,
                            $"stable callsite token collision 0x{callsiteToken.Value:X8}: " +
                            $"{existing} vs {identity}"));
                        continue;
                    }
                    claimedCallsiteTokens[callsiteToken.Value] = identity;
                }

                plans.Add(new ManagedBridgeRewritePlan(
                    callsite.Method,
                    callsite.Instruction,
                    importedBridge,
                    0,
                    returnCast,
                    callsiteToken,
                    spec.AppendOwnerId));
                rewrites.Add(new ManagedBridgeRewriteRecord(
                    callsite.Method.FullName,
                    callsite.Instruction.Offset,
                    callsite.Instruction.OpCode.Name,
                    MethodIdentity(callsite.Target),
                    importedBridge.FullName +
                    (callsiteToken is null ? string.Empty : $" token=0x{callsiteToken.Value:X8}") +
                    (spec.AppendOwnerId == null ? string.Empty : " owner=embedded") +
                    (returnCast is null ? string.Empty : " -> cast " + returnCast.FullName),
                    0));
            }
        }

        return plans;
    }

    private static int ComputeManagedCallsiteToken(
        ModuleDef module,
        MethodDef method,
        Instruction instruction,
        IMethod target)
    {
        var identity =
            $"{module.Mvid:D}|{method.FullName}|{instruction.Offset:X8}|{MethodIdentity(target)}";
        uint hash = 2166136261u;
        foreach (var ch in identity)
        {
            hash = (hash ^ (byte)ch) * 16777619u;
            hash = (hash ^ (byte)(ch >> 8)) * 16777619u;
        }
        var token = unchecked((int)(hash & 0x7fffffffu));
        return token == 0 ? 1 : token;
    }

    private static ManagedCallFilterResult EvaluateManagedCallGenericArgumentFilter(
        ModuleDef module,
        IReadOnlyDictionary<string, ModuleDefMD> proxyModules,
        IReadOnlyDictionary<string, ModuleDefMD> managedOwnedModules,
        ManagedCallBridgeRewriteSpec spec,
        MethodSpec? sourceMethodSpec,
        out string? reason)
    {
        reason = null;
        if (spec.GenericArgumentFilter == ManagedCallGenericArgumentFilter.Any)
            return ManagedCallFilterResult.Rewrite;
        if (spec.GenericArgumentFilter is not (
                ManagedCallGenericArgumentFilter.ModuleLocalMonoBehaviour or
                ManagedCallGenericArgumentFilter.ModOwnedMonoBehaviour))
        {
            reason = $"unsupported generic argument filter {spec.GenericArgumentFilter}";
            return ManagedCallFilterResult.Reject;
        }
        if (sourceMethodSpec?.GenericInstMethodSig?.GenericArguments.Count != 1)
        {
            reason = "managed component bridge requires exactly one closed generic argument";
            return ManagedCallFilterResult.Reject;
        }

        var argument = sourceMethodSpec.GenericInstMethodSig.GenericArguments[0];
        var typeReference = argument.ToTypeDefOrRef();
        if (typeReference == null)
        {
            reason = $"managed component generic argument is not a concrete class: {argument.FullName}";
            return ManagedCallFilterResult.Reject;
        }

        var assemblyName = typeReference.DefinitionAssembly?.Name?.String;
        TypeDef? typeDefinition = typeReference as TypeDef;
        if (typeDefinition == null &&
            string.Equals(
                assemblyName,
                module.Assembly?.Name?.String,
                StringComparison.OrdinalIgnoreCase))
        {
            typeDefinition = module.Find(typeReference.FullName, isReflectionName: false);
        }
        if (typeDefinition == null &&
            !string.IsNullOrWhiteSpace(assemblyName) &&
            managedOwnedModules.TryGetValue(assemblyName, out var ownedModule))
        {
            typeDefinition = ownedModule.Find(typeReference.FullName, isReflectionName: false);
        }
        if (typeDefinition == null)
        {
            try
            {
                typeDefinition = typeReference.ResolveTypeDef();
            }
            catch
            {
                // Import must fail closed below when this is not a generated proxy type.
            }
        }

        if (typeDefinition != null &&
            (ReferenceEquals(typeDefinition.Module, module) ||
             managedOwnedModules.Values.Any(owned => ReferenceEquals(typeDefinition.Module, owned))))
        {
            if (spec.GenericArgumentFilter == ManagedCallGenericArgumentFilter.ModuleLocalMonoBehaviour &&
                !ReferenceEquals(typeDefinition.Module, module))
            {
                reason = $"managed component type is not module-local: {typeDefinition.FullName}";
                return ManagedCallFilterResult.Reject;
            }
            if (IsManagedOwnedMonoBehaviour(typeDefinition, module, managedOwnedModules))
                return ManagedCallFilterResult.Rewrite;

            reason =
                $"MOD-owned component type does not derive UnityEngine.MonoBehaviour: {typeDefinition.FullName}";
            return ManagedCallFilterResult.Reject;
        }

        if (!string.IsNullOrWhiteSpace(assemblyName) && proxyModules.ContainsKey(assemblyName))
            return ManagedCallFilterResult.Skip;

        reason =
            $"cannot prove generic component type is a generated proxy or MOD-owned MonoBehaviour: " +
            $"{assemblyName ?? "<unknown>"}!{typeReference.FullName}";
        return ManagedCallFilterResult.Reject;
    }

    private static bool IsManagedOwnedMonoBehaviour(
        TypeDef type,
        ModuleDef module,
        IReadOnlyDictionary<string, ModuleDefMD> managedOwnedModules)
    {
        var visited = new HashSet<TypeDef>();
        for (var current = type; visited.Add(current);)
        {
            var baseType = current.BaseType;
            if (baseType == null)
                return false;
            if (string.Equals(baseType.FullName, "UnityEngine.MonoBehaviour", StringComparison.Ordinal) &&
                string.Equals(
                    baseType.DefinitionAssembly?.Name?.String,
                    "UnityEngine.CoreModule",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            TypeDef? baseDefinition = baseType as TypeDef;
            var baseAssembly = baseType.DefinitionAssembly?.Name?.String;
            if (baseDefinition == null &&
                string.Equals(baseAssembly, module.Assembly?.Name?.String, StringComparison.OrdinalIgnoreCase))
            {
                baseDefinition = module.Find(baseType.FullName, isReflectionName: false);
            }
            if (baseDefinition == null &&
                !string.IsNullOrWhiteSpace(baseAssembly) &&
                managedOwnedModules.TryGetValue(baseAssembly, out var ownedModule))
            {
                baseDefinition = ownedModule.Find(baseType.FullName, isReflectionName: false);
            }
            if (baseDefinition == null)
            {
                try
                {
                    baseDefinition = baseType.ResolveTypeDef();
                }
                catch
                {
                    return false;
                }
            }
            if (baseDefinition == null ||
                (!ReferenceEquals(baseDefinition.Module, module) &&
                 !managedOwnedModules.Values.Any(owned => ReferenceEquals(baseDefinition.Module, owned))))
                return false;
            current = baseDefinition;
        }
        return false;
    }

    private static bool ManagedCallSourceMatches(
        ManagedCallBridgeRewriteSpec spec,
        IMethod target)
    {
        var signature = target.MethodSig;
        var assembly = target.DeclaringType.DefinitionAssembly?.Name?.String;
        if (signature is null ||
            !string.Equals(assembly, spec.SourceAssembly, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(target.DeclaringType.FullName, spec.SourceType, StringComparison.Ordinal) ||
            target.Name != spec.SourceMethod ||
            signature.HasThis == spec.SourceIsStatic ||
            signature.GenParamCount != spec.SourceGenericArity ||
            TypeIdentity(signature.RetType) != NormalizeExternalTypeIdentity(spec.SourceReturnType) ||
            signature.Params.Count != spec.SourceParameterTypes.Count)
        {
            return false;
        }

        for (var index = 0; index < signature.Params.Count; index++)
        {
            if (TypeIdentity(signature.Params[index]) !=
                NormalizeExternalTypeIdentity(spec.SourceParameterTypes[index]))
                return false;
        }
        return true;
    }

    private static bool ManagedCallBridgeSignatureMatches(
        ManagedCallBridgeRewriteSpec spec,
        MethodSig? bridge)
    {
        var bridgeGenericArity = spec.BridgeGenericArgumentsFromSourceParameters?.Count ??
                                 checked((int)spec.SourceGenericArity);
        if (bridge is null || bridge.HasThis || bridge.GenParamCount != bridgeGenericArity)
            return false;

        var expectedCount = spec.SourceParameterTypes.Count +
                            (spec.InstanceForwarding == ManagedCallInstanceForwarding.AsObject ? 1 : 0) +
                            (spec.AppendCallsiteToken ? 1 : 0) +
                            (spec.AppendOwnerId != null ? 1 : 0);
        if (bridge.Params.Count != expectedCount)
            return false;

        var bridgeIndex = 0;
        if (spec.InstanceForwarding == ManagedCallInstanceForwarding.AsObject)
        {
            if (TypeIdentity(bridge.Params[0]) != "System.Object")
                return false;
            bridgeIndex = 1;
        }
        for (var index = 0; index < spec.SourceParameterTypes.Count; index++)
        {
            var bridgeParameter = ManagedCallBridgeTypeIdentity(
                bridge.Params[bridgeIndex + index],
                spec);
            var sourceParameter = NormalizeExternalTypeIdentity(spec.SourceParameterTypes[index]);
            if (bridgeParameter != sourceParameter &&
                !(spec.AllowObjectParameterForwarding && bridgeParameter == "System.Object"))
                return false;
        }
        if (spec.AppendCallsiteToken &&
            TypeIdentity(bridge.Params[bridgeIndex + spec.SourceParameterTypes.Count]) !=
            "System.Int32")
            return false;
        if (spec.AppendOwnerId != null && TypeIdentity(bridge.Params[^1]) != "System.String")
            return false;

        var sourceReturn = NormalizeExternalTypeIdentity(spec.SourceReturnType);
        var bridgeReturn = ManagedCallBridgeTypeIdentity(bridge.RetType, spec);
        return bridgeReturn == sourceReturn ||
               ((spec.AllowObjectReturnCast ||
                 ManagedCallErasesReturnType(spec, sourceReturn)) &&
                sourceReturn != "System.Void" &&
                bridgeReturn == "System.Object");
    }

    private static string DescribeManagedCallSource(ManagedCallBridgeRewriteSpec spec)
        => $"{spec.SourceAssembly}!{spec.SourceType}::{spec.SourceMethod}" +
           $"({string.Join(',', spec.SourceParameterTypes)}):{spec.SourceReturnType}:" +
           $"{(spec.SourceIsStatic ? "static" : "instance")}:ga={spec.SourceGenericArity}:" +
           $"erase={(spec.EraseSourceTypeToObject ? 1 : 0)}:" +
           $"erase-type={spec.ErasedTypeAssembly ?? "-"}!{spec.ErasedType ?? "-"}:" +
           $"object-params={(spec.AllowObjectParameterForwarding ? 1 : 0)}:" +
           $"unproxied={(spec.AllowUnproxiedSource ? 1 : 0)}:" +
            $"callsite-token={(spec.AppendCallsiteToken ? 1 : 0)}:" +
           $"owner={(spec.AppendOwnerId == null ? 0 : 1)}:" +
           $"filter={spec.GenericArgumentFilter}:" +
           $"bridge-ga={string.Join(',', spec.BridgeGenericArgumentsFromSourceParameters ?? [])}";

    private static string ManagedCallBridgeTypeIdentity(
        TypeSig type,
        ManagedCallBridgeRewriteSpec spec)
    {
        var sources = spec.BridgeGenericArgumentsFromSourceParameters;
        if (sources is null)
            return TypeIdentity(type);

        type = type.RemovePinnedAndModifiers();
        return type switch
        {
            GenericMVar methodVariable when methodVariable.Number < sources.Count =>
                ManagedCallBridgeGenericArgumentIdentity(spec, sources[(int)methodVariable.Number]),
            GenericMVar methodVariable => "!!" + methodVariable.Number,
            GenericVar typeVariable => "!" + typeVariable.Number,
            SZArraySig array => ManagedCallBridgeTypeIdentity(array.Next, spec) + "[]",
            ByRefSig byRef => ManagedCallBridgeTypeIdentity(byRef.Next, spec) + "&",
            PtrSig pointer => ManagedCallBridgeTypeIdentity(pointer.Next, spec) + "*",
            GenericInstSig generic =>
                generic.GenericType.TypeDefOrRef.FullName + "<" +
                string.Join(",", generic.GenericArguments.Select(argument =>
                    ManagedCallBridgeTypeIdentity(argument, spec))) + ">",
            _ => type.FullName
        };
    }

    private static string ManagedCallBridgeGenericArgumentIdentity(
        ManagedCallBridgeRewriteSpec spec,
        int sourceParameterIndex)
    {
        if ((uint)sourceParameterIndex >= (uint)spec.SourceParameterTypes.Count)
            return "<invalid-bridge-generic-source>";
        var source = NormalizeExternalTypeIdentity(spec.SourceParameterTypes[sourceParameterIndex]);
        return source.EndsWith('&') ? source[..^1] : source;
    }

    private static IReadOnlyList<OpaqueTypeErasurePlan> PlanOpaqueTypeErasures(
        ModuleDef module,
        IReadOnlyList<ManagedCallBridgeRewriteSpec> specs,
        IReadOnlySet<Instruction> claimedCallsites,
        List<ManagedBridgeRewriteRecord> rewrites,
        List<ManagedBridgeIssueRecord> issues)
    {
        var plans = new List<OpaqueTypeErasurePlan>();
        foreach (var source in specs
                     .SelectMany(GetManagedCallErasedTypes)
                     .Distinct())
        {
            var fields = new List<FieldDef>();
            var returns = new List<MethodDef>();
            var parameters = new List<(MethodDef Method, int Index)>();
            var locals = new List<(MethodDef Method, Local Local)>();
            var typeInstructions = new List<(MethodDef Method, Instruction Instruction)>();
            var invalid = false;

            foreach (var type in module.GetTypes())
            {
                if (type.BaseType != null && IsSourceType(type.BaseType.ToTypeSig(), source.SourceAssembly, source.SourceType))
                {
                    issues.Add(new ManagedBridgeIssueRecord(
                        source.SourceType,
                        "<type-erasure>",
                        $"opaque source type is used as a base type: {type.FullName}"));
                    invalid = true;
                }
                foreach (var field in type.Fields.Where(field => field.FieldSig != null))
                {
                    if (IsSourceType(field.FieldSig!.Type, source.SourceAssembly, source.SourceType))
                        fields.Add(field);
                    else if (ContainsSourceType(field.FieldSig.Type, source.SourceType))
                    {
                        issues.Add(new ManagedBridgeIssueRecord(
                            source.SourceType,
                            "<type-erasure>",
                            $"nested opaque field type is unsupported: {field.FullName}"));
                        invalid = true;
                    }
                }

                foreach (var method in type.Methods)
                {
                    if (method.MethodSig != null)
                    {
                        if (IsSourceType(method.MethodSig.RetType, source.SourceAssembly, source.SourceType))
                            returns.Add(method);
                        else if (ContainsSourceType(method.MethodSig.RetType, source.SourceType))
                        {
                            issues.Add(new ManagedBridgeIssueRecord(
                                source.SourceType,
                                "<type-erasure>",
                                $"nested opaque return type is unsupported: {method.FullName}"));
                            invalid = true;
                        }
                        for (var index = 0; index < method.MethodSig.Params.Count; index++)
                        {
                            var parameter = method.MethodSig.Params[index];
                            if (IsSourceType(parameter, source.SourceAssembly, source.SourceType))
                                parameters.Add((method, index));
                            else if (ContainsSourceType(parameter, source.SourceType))
                            {
                                issues.Add(new ManagedBridgeIssueRecord(
                                    source.SourceType,
                                    "<type-erasure>",
                                    $"nested opaque parameter type is unsupported: {method.FullName} parameter={index}"));
                                invalid = true;
                            }
                        }
                    }
                    if (!method.HasBody)
                        continue;
                    foreach (var local in method.Body.Variables)
                    {
                        if (IsSourceType(local.Type, source.SourceAssembly, source.SourceType))
                            locals.Add((method, local));
                        else if (ContainsSourceType(local.Type, source.SourceType))
                        {
                            issues.Add(new ManagedBridgeIssueRecord(
                                source.SourceType,
                                "<type-erasure>",
                                $"nested opaque local type is unsupported: {method.FullName}"));
                            invalid = true;
                        }
                    }
                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.Operand is IMethod target &&
                            IsSourceType(target.DeclaringType.ToTypeSig(), source.SourceAssembly, source.SourceType) &&
                            !claimedCallsites.Contains(instruction))
                        {
                            issues.Add(new ManagedBridgeIssueRecord(
                                source.SourceType,
                                target.Name,
                                $"opaque source method is not bridged at {method.FullName}@IL_{instruction.Offset:X4}"));
                            invalid = true;
                        }
                        else if (instruction.Operand is IMethod signatureTarget &&
                                 !claimedCallsites.Contains(instruction) &&
                                 signatureTarget.MethodSig is { } targetSignature &&
                                 (ContainsSourceType(targetSignature.RetType, source.SourceType) ||
                                  targetSignature.Params.Any(parameter =>
                                      ContainsSourceType(parameter, source.SourceType))))
                        {
                            issues.Add(new ManagedBridgeIssueRecord(
                                source.SourceType,
                                signatureTarget.Name,
                                $"opaque type crosses an unbridged method signature at " +
                                $"{method.FullName}@IL_{instruction.Offset:X4}"));
                            invalid = true;
                        }
                        if (instruction.Operand is not ITypeDefOrRef operandType ||
                            !IsSourceType(operandType.ToTypeSig(), source.SourceAssembly, source.SourceType))
                            continue;
                        if (instruction.OpCode.Code is Code.Castclass or Code.Isinst)
                            typeInstructions.Add((method, instruction));
                        else
                        {
                            issues.Add(new ManagedBridgeIssueRecord(
                                source.SourceType,
                                "<type-erasure>",
                                $"opaque source type opcode is unsupported: {method.FullName}@" +
                                $"IL_{instruction.Offset:X4} {instruction.OpCode.Name}"));
                            invalid = true;
                        }
                    }
                }
            }

            if (invalid)
                continue;
            plans.Add(new OpaqueTypeErasurePlan(
                source.SourceAssembly,
                source.SourceType,
                fields,
                returns,
                parameters,
                locals,
                typeInstructions));
            foreach (var field in fields)
            {
                rewrites.Add(new ManagedBridgeRewriteRecord(
                    field.DeclaringType.FullName,
                    0,
                    "type-erase-field",
                    field.FullName,
                    "System.Object",
                    0));
            }
            foreach (var local in locals)
            {
                rewrites.Add(new ManagedBridgeRewriteRecord(
                    local.Method.FullName,
                    0,
                    "type-erase-local",
                    source.SourceType,
                    "System.Object",
                    0));
            }
        }
        return plans;
    }

    private static IEnumerable<(string SourceAssembly, string SourceType)>
        GetManagedCallErasedTypes(ManagedCallBridgeRewriteSpec spec)
    {
        if (spec.EraseSourceTypeToObject)
            yield return (spec.SourceAssembly, spec.SourceType);
        if (!string.IsNullOrWhiteSpace(spec.ErasedTypeAssembly) &&
            !string.IsNullOrWhiteSpace(spec.ErasedType))
        {
            yield return (spec.ErasedTypeAssembly!, spec.ErasedType!);
        }
    }

    private static bool ManagedCallErasesReturnType(
        ManagedCallBridgeRewriteSpec spec,
        string returnType)
        => spec.EraseSourceTypeToObject && returnType == spec.SourceType ||
           !string.IsNullOrWhiteSpace(spec.ErasedType) && returnType == spec.ErasedType;

    private static void ApplyOpaqueTypeErasure(ModuleDef module, OpaqueTypeErasurePlan plan)
    {
        var objectType = module.CorLibTypes.Object;
        foreach (var field in plan.Fields)
            field.FieldSig!.Type = objectType;
        foreach (var method in plan.Returns)
            method.MethodSig!.RetType = objectType;
        foreach (var parameter in plan.Parameters)
            parameter.Method.MethodSig!.Params[parameter.Index] = objectType;
        foreach (var local in plan.Locals)
            local.Local.Type = objectType;
        foreach (var instruction in plan.TypeInstructions)
            instruction.Instruction.Operand = objectType.TypeDefOrRef;
    }

    private static bool IsSourceType(TypeSig? type, string sourceAssembly, string sourceType)
    {
        if (type == null || TypeIdentity(type) != sourceType)
            return false;
        var reference = type.ToTypeDefOrRef();
        return reference != null && string.Equals(
            reference.DefinitionAssembly?.Name?.String,
            sourceAssembly,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsSourceType(TypeSig type, string sourceType)
    {
        // Structural containment check: only composite types (array/byref/pointer/
        // generic instantiation) whose element or argument is exactly the erased
        // source type count as "nested opaque". A plain substring match on the type
        // identity would falsely reject distinct, proxy-resolvable types that merely
        // share the name prefix (e.g. AssetBundleCreateRequest/AssetBundleRequest
        // inside the generated UnityEngine.AssetBundleModule proxy).
        type = type.RemovePinnedAndModifiers();
        return type switch
        {
            SZArraySig array => ContainsSourceType(array.Next, sourceType),
            ByRefSig byRef => ContainsSourceType(byRef.Next, sourceType),
            PtrSig pointer => ContainsSourceType(pointer.Next, sourceType),
            GenericInstSig generic => generic.GenericArguments.Any(
                argument => ContainsSourceType(argument, sourceType)),
            _ => TypeIdentity(type) == sourceType
        };
    }

    private static bool SourceParametersMatch(
        MethodSig? signature,
        IReadOnlyList<string> expectedParameterTypes)
    {
        if (signature is null)
            return false;
        if (expectedParameterTypes.Count == 0)
            return true;
        if (signature.Params.Count != expectedParameterTypes.Count)
            return false;
        for (var index = 0; index < signature.Params.Count; index++)
        {
            if (!string.Equals(
                    TypeIdentity(signature.Params[index]),
                    NormalizeExternalTypeIdentity(expectedParameterTypes[index]),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ManagedBridgeSignatureMatches(
        MethodSig source,
        MethodSig? bridge,
        bool appendCallsiteToken,
        bool appendOwner)
    {
        if (bridge is null || bridge.HasThis ||
            TypeIdentity(source.RetType) != TypeIdentity(bridge.RetType))
        {
            return false;
        }
        if (!appendOwner && !appendCallsiteToken && bridge.Params.Count == 0)
            return true;
        if (!appendOwner && !appendCallsiteToken)
            return MethodParametersMatch(bridge, source);
        var expected = source.Params.Count + (appendCallsiteToken ? 1 : 0) +
                       (appendOwner ? 1 : 0);
        if (bridge.Params.Count != expected)
            return false;
        for (var index = 0; index < source.Params.Count; ++index)
        {
            if (TypeIdentity(bridge.Params[index]) != TypeIdentity(source.Params[index]))
                return false;
        }
        var appendedIndex = source.Params.Count;
        if (appendCallsiteToken &&
            TypeIdentity(bridge.Params[appendedIndex++]) != "System.Int32")
            return false;
        if (appendOwner && TypeIdentity(bridge.Params[appendedIndex]) != "System.String")
            return false;
        return true;
    }

    private static bool MethodReferenceMatches(MethodDef source, IMethod target)
        => target.Name == source.Name &&
           string.Equals(target.DeclaringType.FullName, source.DeclaringType.FullName, StringComparison.Ordinal) &&
           MethodSignatureMatches(target.MethodSig, source.MethodSig);

    private static string NormalizeExternalTypeIdentity(string value)
        => value.Trim() switch
        {
            "void" => "System.Void",
            "bool" => "System.Boolean",
            "byte" => "System.Byte",
            "sbyte" => "System.SByte",
            "short" => "System.Int16",
            "ushort" => "System.UInt16",
            "int" => "System.Int32",
            "uint" => "System.UInt32",
            "long" => "System.Int64",
            "ulong" => "System.UInt64",
            "float" => "System.Single",
            "double" => "System.Double",
            "char" => "System.Char",
            "string" => "System.String",
            "object" => "System.Object",
            var identity => identity.Replace('+', '/')
        };

    private static void ApplyManagedBridgeRewrite(ManagedBridgeRewritePlan plan)
    {
        if (plan.DroppedArguments == 0)
        {
            if (plan.AppendedInt32 is { } appended)
            {
                InsertBeforeWithRetargeting(
                    plan.Method.Body,
                    plan.Instruction,
                    Instruction.CreateLdcI4(appended));
            }
            if (plan.AppendedString is { } appendedString)
            {
                InsertBeforeWithRetargeting(
                    plan.Method.Body,
                    plan.Instruction,
                    Instruction.Create(OpCodes.Ldstr, appendedString));
            }
            plan.Instruction.OpCode = OpCodes.Call;
            plan.Instruction.Operand = plan.BridgeMethod;
            if (plan.ReturnCastType is not null)
            {
                var callIndex = plan.Method.Body.Instructions.IndexOf(plan.Instruction);
                plan.Method.Body.Instructions.Insert(
                    callIndex + 1,
                    Instruction.Create(OpCodes.Castclass, plan.ReturnCastType));
            }
            return;
        }

        var instructions = plan.Method.Body.Instructions;
        var instructionIndex = instructions.IndexOf(plan.Instruction);
        plan.Instruction.OpCode = OpCodes.Pop;
        plan.Instruction.Operand = null;
        for (var index = 1; index < plan.DroppedArguments; index++)
            instructions.Insert(instructionIndex + index, Instruction.Create(OpCodes.Pop));
        instructions.Insert(
            instructionIndex + plan.DroppedArguments,
            Instruction.Create(OpCodes.Call, plan.BridgeMethod));
    }

    private static void NormalizeBranchEncodings(ModuleDef module)
    {
        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue;
                method.Body.SimplifyBranches();
                method.Body.OptimizeBranches();
            }
        }
    }

    private static void InsertBeforeWithRetargeting(
        CilBody body,
        Instruction target,
        Instruction inserted)
    {
        foreach (var instruction in body.Instructions)
        {
            if (ReferenceEquals(instruction.Operand, target))
            {
                instruction.Operand = inserted;
                continue;
            }
            if (instruction.Operand is IList<Instruction> targets)
            {
                for (var index = 0; index < targets.Count; ++index)
                {
                    if (ReferenceEquals(targets[index], target))
                        targets[index] = inserted;
                }
            }
        }
        foreach (var handler in body.ExceptionHandlers)
        {
            if (ReferenceEquals(handler.TryStart, target))
                handler.TryStart = inserted;
            if (ReferenceEquals(handler.TryEnd, target))
                handler.TryEnd = inserted;
            if (ReferenceEquals(handler.HandlerStart, target))
                handler.HandlerStart = inserted;
            if (ReferenceEquals(handler.HandlerEnd, target))
                handler.HandlerEnd = inserted;
            if (ReferenceEquals(handler.FilterStart, target))
                handler.FilterStart = inserted;
        }
        var targetIndex = body.Instructions.IndexOf(target);
        if (targetIndex < 0)
            throw new InvalidOperationException("Managed bridge callsite is not in its declaring method body.");
        body.Instructions.Insert(targetIndex, inserted);
    }

    private static Dictionary<string, ModuleDefMD> LoadProxyModules(string proxyDirectory)
    {
        var modules = new Dictionary<string, ModuleDefMD>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in Directory.EnumerateFiles(proxyDirectory, "*.dll", SearchOption.TopDirectoryOnly))
            {
                var module = ModuleDefMD.Load(path);
                var assemblyName = module.Assembly?.Name?.String;
                if (string.IsNullOrWhiteSpace(assemblyName) || !modules.TryAdd(assemblyName, module))
                {
                    module.Dispose();
                    throw new InvalidOperationException($"Duplicate or invalid proxy assembly: {path}");
                }
            }

            if (modules.Count == 0)
                throw new InvalidOperationException("Proxy directory contains no managed assemblies.");
            return modules;
        }
        catch
        {
            foreach (var module in modules.Values)
                module.Dispose();
            throw;
        }
    }

    private static Dictionary<string, ModuleDefMD> LoadManagedOwnedModules(
        IReadOnlyList<string> assemblyPaths,
        string inputPath)
    {
        var modules = new Dictionary<string, ModuleDefMD>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in assemblyPaths
                         .Select(Path.GetFullPath)
                         .Where(path => !string.Equals(path, inputPath, StringComparison.OrdinalIgnoreCase))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(path))
                    throw new FileNotFoundException("MOD-owned managed assembly is missing.", path);
                var module = ModuleDefMD.Load(path);
                var assemblyName = module.Assembly?.Name?.String;
                if (string.IsNullOrWhiteSpace(assemblyName) || !modules.TryAdd(assemblyName, module))
                {
                    module.Dispose();
                    throw new InvalidDataException(
                        $"Duplicate or invalid MOD-owned managed assembly: {path}");
                }
            }
            return modules;
        }
        catch
        {
            foreach (var module in modules.Values)
                module.Dispose();
            throw;
        }
    }

    private static void AuditProxyTypeReferences(
        ModuleDef module,
        IReadOnlyDictionary<string, ModuleDefMD> proxyModules,
        List<string> issues)
    {
        foreach (var reference in module.GetTypeRefs()
                     .Select(type => new
                     {
                         Assembly = type.DefinitionAssembly?.Name?.String,
                         Type = type.FullName
                     })
                     .Where(reference => !string.IsNullOrWhiteSpace(reference.Assembly) &&
                                         RequiresGeneratedProxy(reference.Assembly!))
                     .DistinctBy(reference => reference.Assembly + "|" + reference.Type))
        {
            if (!proxyModules.TryGetValue(reference.Assembly!, out var proxyModule))
            {
                issues.Add(
                    $"generated proxy assembly missing for type reference " +
                    $"{reference.Assembly}!{reference.Type}");
                continue;
            }

            if (proxyModule.Find(reference.Type, isReflectionName: false) is null)
            {
                issues.Add(
                    $"generated proxy type missing for metadata reference " +
                    $"{reference.Assembly}!{reference.Type}");
            }
        }
    }

    private static bool RequiresGeneratedProxy(string assemblyName)
        => assemblyName.Equals("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) ||
           assemblyName.Equals("RDTools", StringComparison.OrdinalIgnoreCase) ||
           assemblyName.Equals("Unity.TextMeshPro", StringComparison.OrdinalIgnoreCase) ||
           assemblyName.StartsWith("UnityEngine.", StringComparison.OrdinalIgnoreCase);

    private static bool AccessorMatchesField(MethodDef accessor, IField field, bool isWrite)
    {
        var fieldType = field.FieldSig?.Type;
        if (fieldType is null)
            return false;

        if (isWrite)
        {
            return accessor.MethodSig is { Params.Count: 1 } signature &&
                   signature.RetType.ElementType == ElementType.Void &&
                   TypeIdentity(signature.Params[0]) == TypeIdentity(fieldType);
        }

        return accessor.MethodSig is { Params.Count: 0 } getterSignature &&
               TypeIdentity(getterSignature.RetType) == TypeIdentity(fieldType);
    }

    private static bool ArrayGetterMatchesField(MethodDef accessor, IField field, bool isWrite)
    {
        if (isWrite || field.FieldSig?.Type is not SZArraySig expectedArray ||
            accessor.MethodSig is not { Params.Count: 0 } signature ||
            signature.RetType is not GenericInstSig proxyArray || proxyArray.GenericArguments.Count != 1)
        {
            return false;
        }

        var proxyArrayName = proxyArray.GenericType.TypeDefOrRef.FullName;
        if (proxyArrayName is not (
            "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray`1" or
            "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1"))
        {
            return false;
        }

        return TypeIdentity(proxyArray.GenericArguments[0]) == TypeIdentity(expectedArray.Next);
    }

    private static IMethod CreateArrayToManagedConverter(ModuleDef module, Importer importer, IField field)
    {
        var arrayType = (SZArraySig)field.FieldSig!.Type;
        return CreateArrayToManagedConverter(module, importer, arrayType.Next);
    }

    private static IMethod CreateArrayToManagedConverter(ModuleDef module, Importer importer, TypeSig expectedElement)
    {
        var elementType = importer.Import(expectedElement);
        var typeVariable = new GenericVar(0);
        var runtimeAssembly = module.GetAssemblyRefs().FirstOrDefault(
            assembly => assembly.Name == "Il2CppInterop.Runtime") ??
            new AssemblyRefUser(new AssemblyNameInfo("Il2CppInterop.Runtime"));
        var baseType = new TypeRefUser(
            module,
            "Il2CppInterop.Runtime.InteropTypes.Arrays",
            "Il2CppArrayBase`1",
            runtimeAssembly);
        var closedBase = new GenericInstSig(new ClassSig(baseType), elementType);
        var declaringType = new TypeSpecUser(closedBase);
        var signatureBase = new GenericInstSig(new ClassSig(baseType), typeVariable);
        var signature = MethodSig.CreateStatic(new SZArraySig(typeVariable), signatureBase);
        return new MemberRefUser(module, "op_Implicit", signature, declaringType);
    }

    private static bool ListGetterMatchesField(MethodDef accessor, IField field, bool isWrite)
    {
        if (isWrite || field.FieldSig?.Type is not GenericInstSig expectedList ||
            accessor.MethodSig is not { Params.Count: 0 } signature ||
            signature.RetType is not GenericInstSig proxyList ||
            expectedList.GenericArguments.Count != 1 || proxyList.GenericArguments.Count != 1)
        {
            return false;
        }

        return expectedList.GenericType.TypeDefOrRef.FullName == "System.Collections.Generic.List`1" &&
               proxyList.GenericType.TypeDefOrRef.FullName == "Il2CppSystem.Collections.Generic.List`1" &&
               TypeIdentity(expectedList.GenericArguments[0]) == TypeIdentity(proxyList.GenericArguments[0]);
    }

    private static IMethod CreateListToManagedConverter(ModuleDef module, Importer importer, IField field)
    {
        var expectedList = (GenericInstSig)field.FieldSig!.Type;
        return CreateListToManagedConverter(module, importer, expectedList.GenericArguments[0]);
    }

    private static IMethod CreateListToManagedConverter(ModuleDef module, Importer importer, TypeSig expectedElement)
    {
        var elementType = importer.Import(expectedElement);
        var methodVariable = new GenericMVar(0);

        var corlib = module.CorLibTypes.AssemblyRef;
        var managedListType = new TypeRefUser(module, "System.Collections.Generic", "List`1", corlib);
        var managedList = new GenericInstSig(new ClassSig(managedListType), methodVariable);

        var il2CppMscorlib = module.GetAssemblyRefs().FirstOrDefault(
            assembly => assembly.Name == "Il2Cppmscorlib") ??
            new AssemblyRefUser(new AssemblyNameInfo("Il2Cppmscorlib"));
        var il2CppListType = new TypeRefUser(
            module,
            "Il2CppSystem.Collections.Generic",
            "List`1",
            il2CppMscorlib);
        var il2CppList = new GenericInstSig(new ClassSig(il2CppListType), methodVariable);

        var androidAssembly = module.GetAssemblyRefs().FirstOrDefault(
            assembly => assembly.Name == "StArray.ModManager.Android") ??
            new AssemblyRefUser(new AssemblyNameInfo("StArray.ModManager.Android"));
        var bridgeType = new TypeRefUser(
            module,
            "StArray.ModManager.Android.PcCompat",
            "PcCompatCollectionBridge",
            androidAssembly);
        var signature = MethodSig.CreateStatic(managedList, il2CppList);
        signature.Generic = true;
        signature.GenParamCount = 1;
        var genericMethod = new MemberRefUser(module, "CopyList", signature, bridgeType);
        return new MethodSpecUser(genericMethod, new GenericInstMethodSig(elementType));
    }

    private static string TypeIdentity(TypeSig type)
    {
        type = type.RemovePinnedAndModifiers();
        return type switch
        {
            GenericMVar methodVariable => "!!" + methodVariable.Number,
            GenericVar typeVariable => "!" + typeVariable.Number,
            SZArraySig array => TypeIdentity(array.Next) + "[]",
            ByRefSig byRef => TypeIdentity(byRef.Next) + "&",
            PtrSig pointer => TypeIdentity(pointer.Next) + "*",
            GenericInstSig generic =>
                generic.GenericType.TypeDefOrRef.FullName + "<" +
                string.Join(",", generic.GenericArguments.Select(TypeIdentity)) + ">",
            _ => type.FullName
        };
    }

    private static bool MethodSignatureMatches(MethodSig? candidate, MethodSig? target)
    {
        if (candidate is null || target is null || candidate.Params.Count != target.Params.Count)
            return false;
        if (TypeIdentity(candidate.RetType) != TypeIdentity(target.RetType))
            return false;
        for (var index = 0; index < candidate.Params.Count; index++)
        {
            if (TypeIdentity(candidate.Params[index]) != TypeIdentity(target.Params[index]))
                return false;
        }
        return true;
    }

    private static bool MethodParametersMatch(MethodSig? candidate, MethodSig? target)
    {
        if (candidate is null || target is null || candidate.Params.Count != target.Params.Count)
            return false;
        for (var index = 0; index < candidate.Params.Count; index++)
        {
            if (TypeIdentity(candidate.Params[index]) != TypeIdentity(target.Params[index]))
                return false;
        }
        return true;
    }

    private static ArgumentBridge? CreateMethodArgumentConverter(
        ModuleDef module,
        Importer importer,
        MethodSig? proxySignature,
        MethodSig targetSignature)
    {
        if (proxySignature is null ||
            proxySignature.Params.Count != targetSignature.Params.Count ||
            TypeIdentity(proxySignature.RetType) != TypeIdentity(targetSignature.RetType))
        {
            return null;
        }

        var mismatchIndex = -1;
        IMethod? converter = null;
        for (var index = 0; index < proxySignature.Params.Count; index++)
        {
            if (TypeIdentity(proxySignature.Params[index]) == TypeIdentity(targetSignature.Params[index]))
                continue;
            if (mismatchIndex >= 0)
                return null;
            converter = CreateNullableArgumentConverter(
                module,
                importer,
                proxySignature.Params[index],
                targetSignature.Params[index]) ??
                CreateDelegateArgumentConverter(
                    module,
                    importer,
                    proxySignature.Params[index],
                    targetSignature.Params[index]);
            if (converter is null)
                return null;
            mismatchIndex = index;
        }

        return mismatchIndex < 0 || converter is null
            ? null
            : new ArgumentBridge(mismatchIndex, converter);
    }

    private static IMethod? CreateNullableArgumentConverter(
        ModuleDef module,
        Importer importer,
        TypeSig proxyType,
        TypeSig targetType)
    {
        if (proxyType is not GenericInstSig proxyNullable ||
            targetType is not GenericInstSig targetNullable ||
            proxyNullable.GenericArguments.Count != 1 || targetNullable.GenericArguments.Count != 1 ||
            proxyNullable.GenericType.TypeDefOrRef.FullName != "Il2CppSystem.Nullable`1" ||
            targetNullable.GenericType.TypeDefOrRef.FullName != "System.Nullable`1" ||
            TypeIdentity(proxyNullable.GenericArguments[0]) != TypeIdentity(targetNullable.GenericArguments[0]))
        {
            return null;
        }

        var methodVariable = new GenericMVar(0);
        var corlib = module.CorLibTypes.AssemblyRef;
        var managedNullableType = new TypeRefUser(module, "System", "Nullable`1", corlib);
        var managedNullable = new GenericInstSig(new ValueTypeSig(managedNullableType), methodVariable);
        var il2CppMscorlib = module.GetAssemblyRefs().FirstOrDefault(
            assembly => assembly.Name == "Il2Cppmscorlib") ??
            new AssemblyRefUser(new AssemblyNameInfo("Il2Cppmscorlib"));
        var il2CppNullableType = new TypeRefUser(module, "Il2CppSystem", "Nullable`1", il2CppMscorlib);
        var il2CppNullable = new GenericInstSig(new ClassSig(il2CppNullableType), methodVariable);
        var androidAssembly = module.GetAssemblyRefs().FirstOrDefault(
            assembly => assembly.Name == "StArray.ModManager.Android") ??
            new AssemblyRefUser(new AssemblyNameInfo("StArray.ModManager.Android"));
        var bridgeType = new TypeRefUser(
            module,
            "StArray.ModManager.Android.PcCompat",
            "PcCompatAbiBridge",
            androidAssembly);
        var signature = MethodSig.CreateStatic(il2CppNullable, managedNullable);
        signature.Generic = true;
        signature.GenParamCount = 1;
        var genericMethod = new MemberRefUser(module, "ToIl2CppNullable", signature, bridgeType);
        return new MethodSpecUser(
            genericMethod,
            new GenericInstMethodSig(importer.Import(targetNullable.GenericArguments[0])));
    }

    private static IMethod? CreateDelegateArgumentConverter(
        ModuleDef module,
        Importer importer,
        TypeSig proxyType,
        TypeSig targetType)
    {
        var proxyIdentity = TypeIdentity(proxyType);
        var targetIdentity = TypeIdentity(targetType);
        if (!proxyIdentity.StartsWith("Il2CppSystem.", StringComparison.Ordinal) ||
            proxyIdentity[6..] != targetIdentity ||
            !(targetIdentity.StartsWith("System.Action", StringComparison.Ordinal) ||
              targetIdentity.StartsWith("System.Func", StringComparison.Ordinal)))
        {
            return null;
        }

        return CreateToIl2CppDelegateConverter(module, importer, proxyType);
    }

    private static IMethod CreateToIl2CppDelegateConverter(
        ModuleDef module,
        Importer importer,
        TypeSig proxyType)
    {
        var methodVariable = new GenericMVar(0);
        var androidAssembly = module.GetAssemblyRefs().FirstOrDefault(
            assembly => assembly.Name == "StArray.ModManager.Android") ??
            new AssemblyRefUser(new AssemblyNameInfo("StArray.ModManager.Android"));
        var bridgeType = new TypeRefUser(
            module,
            "StArray.ModManager.Android.PcCompat",
            "PcCompatAbiBridge",
            androidAssembly);
        var delegateType = new ClassSig(new TypeRefUser(
            module,
            "System",
            "Delegate",
            module.CorLibTypes.AssemblyRef));
        var signature = MethodSig.CreateStatic(methodVariable, delegateType);
        signature.Generic = true;
        signature.GenParamCount = 1;
        var genericMethod = new MemberRefUser(module, "ToIl2CppDelegate", signature, bridgeType);
        return new MethodSpecUser(
            genericMethod,
            new GenericInstMethodSig(importer.Import(proxyType)));
    }

    private static DelegateConstructorRewrite? CreateUnityActionConstructorRewrite(
        ModuleDef module,
        Importer importer,
        IMethod target,
        MethodSig signature)
    {
        if (target.Name != ".ctor" ||
            target.DeclaringType is not TypeSpec { TypeSig: GenericInstSig unityAction } ||
            !unityAction.GenericType.TypeDefOrRef.FullName.StartsWith(
                "UnityEngine.Events.UnityAction`",
                StringComparison.Ordinal) ||
            signature.Params.Count != 2 ||
            TypeIdentity(signature.Params[0]) != "System.Object" ||
            TypeIdentity(signature.Params[1]) != "System.IntPtr" ||
            unityAction.GenericArguments.Count is < 1 or > 8)
        {
            return null;
        }

        var actionType = new TypeRefUser(
            module,
            "System",
            "Action`" + unityAction.GenericArguments.Count,
            module.CorLibTypes.AssemblyRef);
        var managedAction = new GenericInstSig(
            new ClassSig(actionType),
            unityAction.GenericArguments.Select(importer.Import).ToArray());
        var managedConstructor = new MemberRefUser(
            module,
            ".ctor",
            importer.Import(signature),
            new TypeSpecUser(managedAction));
        var converter = CreateToIl2CppDelegateConverter(module, importer, unityAction);
        return new DelegateConstructorRewrite(managedConstructor, converter);
    }

    private static void InsertArgumentConverter(Importer importer, MethodRewritePlan plan)
    {
        if (plan.Instruction.Operand is not IMethod original || original.MethodSig is not { } signature)
            throw new InvalidOperationException("Method rewrite lost its original signature.");

        var body = plan.Method.Body;
        body.InitLocals = true;
        var trailingLocals = new List<(int Index, Local Local)>();
        for (var index = signature.Params.Count - 1; index > plan.ArgumentIndex; index--)
        {
            var local = new Local(importer.Import(signature.Params[index]));
            body.Variables.Add(local);
            trailingLocals.Add((index, local));
        }

        var inserted = new List<Instruction>();
        foreach (var entry in trailingLocals)
            inserted.Add(Instruction.Create(OpCodes.Stloc, entry.Local));
        inserted.Add(Instruction.Create(OpCodes.Call, plan.ArgumentConverter!));
        foreach (var entry in trailingLocals.OrderBy(entry => entry.Index))
            inserted.Add(Instruction.Create(OpCodes.Ldloc, entry.Local));

        var insertionIndex = body.Instructions.IndexOf(plan.Instruction);
        foreach (var instruction in inserted)
            body.Instructions.Insert(insertionIndex++, instruction);
    }

    private static IMethod? CreateMethodReturnConverter(
        ModuleDef module,
        Importer importer,
        TypeSig? proxyReturn,
        TypeSig expectedReturn,
        TypeSig? followingUnboxType)
    {
        if (proxyReturn is GenericInstSig proxyArray && expectedReturn is SZArraySig expectedArray &&
            proxyArray.GenericArguments.Count == 1 &&
            proxyArray.GenericType.TypeDefOrRef.FullName is (
                "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase`1" or
                "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray`1" or
                "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1") &&
            TypeIdentity(proxyArray.GenericArguments[0]) == TypeIdentity(expectedArray.Next))
        {
            return CreateArrayToManagedConverter(module, importer, expectedArray.Next);
        }

        if (proxyReturn is GenericInstSig proxyList && expectedReturn is GenericInstSig expectedList &&
            proxyList.GenericArguments.Count == 1 && expectedList.GenericArguments.Count == 1 &&
            proxyList.GenericType.TypeDefOrRef.FullName == "Il2CppSystem.Collections.Generic.List`1" &&
            expectedList.GenericType.TypeDefOrRef.FullName == "System.Collections.Generic.List`1" &&
            TypeIdentity(proxyList.GenericArguments[0]) == TypeIdentity(expectedList.GenericArguments[0]))
        {
            return CreateListToManagedConverter(module, importer, expectedList.GenericArguments[0]);
        }

        if (proxyReturn?.FullName == "Il2CppSystem.Object" &&
            expectedReturn.FullName == "System.Object" &&
            followingUnboxType is not null)
        {
            return CreateBoxUnboxedValueConverter(module, followingUnboxType);
        }

        return null;
    }

    private static TypeSig? GetFollowingUnboxType(
        MethodDef method,
        Instruction instruction,
        IReadOnlyDictionary<string, ModuleDefMD> proxyModules)
    {
        var instructions = method.Body.Instructions;
        for (var index = instructions.IndexOf(instruction) + 1; index < instructions.Count; index++)
        {
            var next = instructions[index];
            if (next.OpCode.Code == Code.Nop)
                continue;
            if (next.OpCode.Code != Code.Unbox_Any ||
                next.Operand is not ITypeDefOrRef type)
                return null;

            var typeSig = type.ToTypeSig();
            var assemblyName = type.DefinitionAssembly?.Name?.String;
            if (string.IsNullOrWhiteSpace(assemblyName) ||
                !proxyModules.TryGetValue(assemblyName, out var proxyModule))
                return typeSig;
            var proxyType = proxyModule.Find(type.FullName, isReflectionName: false);
            return proxyType?.IsValueType == true
                ? new ValueTypeSig(type)
                : typeSig;
        }
        return null;
    }

    private static IMethod CreateBoxUnboxedValueConverter(ModuleDef module, TypeSig valueType)
    {
        var methodVariable = new GenericMVar(0);
        var il2CppMscorlib = module.GetAssemblyRefs().FirstOrDefault(
            assembly => assembly.Name == "Il2Cppmscorlib") ??
            new AssemblyRefUser(new AssemblyNameInfo("Il2Cppmscorlib"));
        var il2CppObject = new ClassSig(new TypeRefUser(module, "Il2CppSystem", "Object", il2CppMscorlib));
        var androidAssembly = module.GetAssemblyRefs().FirstOrDefault(
            assembly => assembly.Name == "StArray.ModManager.Android") ??
            new AssemblyRefUser(new AssemblyNameInfo("StArray.ModManager.Android"));
        var bridgeType = new TypeRefUser(
            module,
            "StArray.ModManager.Android.PcCompat",
            "PcCompatAbiBridge",
            androidAssembly);
        var signature = MethodSig.CreateStatic(module.CorLibTypes.Object, il2CppObject);
        signature.Generic = true;
        signature.GenParamCount = 1;
        var genericMethod = new MemberRefUser(module, "BoxUnboxedValue", signature, bridgeType);
        return new MethodSpecUser(
            genericMethod,
            new GenericInstMethodSig(valueType));
    }

    private static TypeSig ResolveMethodReturnType(TypeSig type, MethodSpec? methodSpec)
    {
        type = type.RemovePinnedAndModifiers();
        if (type is GenericMVar methodVariable && methodSpec?.GenericInstMethodSig is { } generic &&
            methodVariable.Number < generic.GenericArguments.Count)
        {
            return generic.GenericArguments[(int)methodVariable.Number];
        }
        if (type is SZArraySig array)
            return new SZArraySig(ResolveMethodReturnType(array.Next, methodSpec));
        if (type is ByRefSig byRef)
            return new ByRefSig(ResolveMethodReturnType(byRef.Next, methodSpec));
        if (type is PtrSig pointer)
            return new PtrSig(ResolveMethodReturnType(pointer.Next, methodSpec));
        if (type is GenericInstSig genericInstance)
            return new GenericInstSig(
                genericInstance.GenericType,
                genericInstance.GenericArguments
                    .Select(argument => ResolveMethodReturnType(argument, methodSpec))
                    .ToArray());
        return type;
    }

    private static IMethod ImportManagedCallBridge(
        Importer importer,
        MethodDef candidate,
        ManagedCallBridgeRewriteSpec spec,
        MethodSig sourceSignature,
        MethodSpec? sourceMethodSpec)
    {
        var sources = spec.BridgeGenericArgumentsFromSourceParameters;
        if (sources is null)
            return ImportProxyMethod(importer, candidate, sourceMethodSpec);

        var arguments = new TypeSig[sources.Count];
        for (var index = 0; index < sources.Count; index++)
        {
            var sourceParameterIndex = sources[index];
            if ((uint)sourceParameterIndex >= (uint)sourceSignature.Params.Count)
            {
                throw new InvalidOperationException(
                    $"Managed bridge generic argument {index} references missing source parameter " +
                    $"{sourceParameterIndex}.");
            }
            var sourceType = ResolveMethodReturnType(
                sourceSignature.Params[sourceParameterIndex],
                sourceMethodSpec);
            if (sourceType is ByRefSig byRef)
                sourceType = byRef.Next;
            arguments[index] = importer.Import(sourceType);
        }

        var imported = importer.Import(candidate);
        return new MethodSpecUser(
            (IMethodDefOrRef)imported,
            new GenericInstMethodSig(arguments));
    }

    private static IMethod ImportProxyMethod(Importer importer, MethodDef candidate, MethodSpec? originalSpec)
    {
        var imported = importer.Import(candidate);
        if (originalSpec?.GenericInstMethodSig is not { } generic)
            return imported;
        return new MethodSpecUser(
            (IMethodDefOrRef)imported,
            new GenericInstMethodSig(generic.GenericArguments.Select(importer.Import).ToArray()));
    }

    private static string DescribeMethodCandidates(IEnumerable<MethodDef> candidates)
    {
        var descriptions = candidates.Select(candidate =>
        {
            var signature = candidate.MethodSig;
            return signature is null
                ? $"{candidate.Name}[no-signature]"
                : $"{candidate.Name}[{(candidate.IsStatic ? "static" : "instance")}," +
                  $"ga={candidate.GenericParameters.Count},ret={TypeIdentity(signature.RetType)}," +
                  $"params={string.Join(";", signature.Params.Select(TypeIdentity))}]";
        }).ToArray();
        return descriptions.Length == 0 ? "<none>" : string.Join(", ", descriptions);
    }

    private static string MethodIdentity(IMethod method)
    {
        var signature = method.MethodSig;
        if (signature is null)
            return method.FullName;
        return $"{method.DeclaringType.DefinitionAssembly?.Name?.String}!{method.DeclaringType.FullName}::" +
               $"{method.Name}({string.Join(",", signature.Params.Select(TypeIdentity))}):{TypeIdentity(signature.RetType)}";
    }

    private static string MethodSurfaceEntry(
        string assemblyName,
        ITypeDefOrRef declaringType,
        IMethod method,
        MethodSig? signature)
    {
        if (signature is null)
            return string.Empty;
        var genericArity = method is MethodSpec methodSpec
            ? methodSpec.GenericInstMethodSig?.GenericArguments.Count ?? 0
            : (int)signature.GenParamCount;
        return string.Join(
            "|",
            "M",
            assemblyName,
            SurfaceDeclaringTypeName(declaringType),
            signature.HasThis ? "instance" : "static",
            genericArity,
            TypeIdentity(signature.RetType),
            method.Name,
            string.Join(";", signature.Params.Select(TypeIdentity)));
    }

    private static string SurfaceDeclaringTypeName(ITypeDefOrRef declaringType)
        => declaringType is TypeSpec { TypeSig: GenericInstSig genericInstance }
            ? genericInstance.GenericType.TypeDefOrRef.FullName
            : declaringType.FullName;
}

public sealed record RewriteReport(
    string FormatVersion,
    string InputPath,
    string OutputPath,
    string ProxyDirectory,
    bool AuditOnly,
    int ScannedFieldInstructions,
    bool OutputWritten,
    IReadOnlyList<RewriteRecord> Rewrites,
    IReadOnlyList<PassthroughRecord> Passthroughs,
    IReadOnlyList<MethodCallRecord> MethodCalls,
    IReadOnlyList<MethodIssueRecord> MethodIssues,
    IReadOnlyList<ManagedBridgeRewriteRecord> ManagedBridgeRewrites,
    IReadOnlyList<ManagedBridgeIssueRecord> ManagedBridgeIssues,
    IReadOnlyList<PatchMetadataRewriteRecord> PatchMetadataRewrites,
    IReadOnlyList<string> Issues);

public sealed record RewriteRecord(
    string Method,
    uint IlOffset,
    string OriginalOpcode,
    string OriginalField,
    string ProxyAccessor);

public sealed record PassthroughRecord(
    string Method,
    uint IlOffset,
    string Opcode,
    string Field,
    string Reason);

public sealed record MethodCallRecord(
    string Method,
    uint IlOffset,
    string Opcode,
    string Target,
    string SurfaceEntry,
    string ProxyMethod);

public sealed record MethodIssueRecord(
    string Method,
    uint IlOffset,
    string Opcode,
    string Target,
    string SurfaceEntry,
    string Reason);

public sealed record ManagedBridgeRewriteSpec(
    string SourceType,
    string SourceMethod,
    IReadOnlyList<string> SourceParameterTypes,
    string BridgeType,
    string BridgeMethod,
    string? AppendOwnerId = null,
    bool AppendCallsiteToken = false);

public enum ManagedCallInstanceForwarding
{
    None,
    AsObject
}

public enum ManagedCallGenericArgumentFilter
{
    Any,
    ModuleLocalMonoBehaviour,
    ModOwnedMonoBehaviour
}

public sealed record ManagedCallBridgeRewriteSpec(
    string SourceAssembly,
    string SourceType,
    string SourceMethod,
    bool SourceIsStatic,
    uint SourceGenericArity,
    string SourceReturnType,
    IReadOnlyList<string> SourceParameterTypes,
    string BridgeType,
    string BridgeMethod,
    ManagedCallInstanceForwarding InstanceForwarding,
    bool AllowObjectReturnCast,
    bool EraseSourceTypeToObject = false,
    ManagedCallGenericArgumentFilter GenericArgumentFilter = ManagedCallGenericArgumentFilter.Any,
    bool AllowObjectParameterForwarding = false,
    IReadOnlyList<int>? BridgeGenericArgumentsFromSourceParameters = null,
    string? ErasedTypeAssembly = null,
    string? ErasedType = null,
    bool AppendCallsiteToken = false,
    string? AppendOwnerId = null,
    bool AllowUnproxiedSource = false);

public sealed record ManagedFieldConstantRewriteSpec(
    string SourceAssembly,
    string SourceType,
    string SourceField,
    string SourceFieldType,
    int Value);

public sealed record ManagedProxyCastBridgeSpec(
    string BridgeType,
    string IsInstanceMethod,
    string CastMethod);

public sealed record ManagedReadProgressGuardSpec(
    string BridgeType,
    string RequireProgressMethod,
    string TryReadExactlyMethod);

public sealed record ManagedPollingWaitRewriteSpec(
    string BridgeType,
    string BridgeMethod);

public sealed record ManagedOptionalDelegateRewriteSpec(
    string SourceAssembly,
    string SourceType,
    string SourceMethodPrefix,
    string DelegateType,
    string BridgeType,
    string BridgeMethod);

public sealed record ManagedBridgeRewriteRecord(
    string Method,
    uint IlOffset,
    string Opcode,
    string SourceMethod,
    string BridgeMethod,
    int DroppedArguments);

public sealed record ManagedBridgeIssueRecord(
    string SourceType,
    string SourceMethod,
    string Reason);

public sealed record PatchMetadataRewriteRecord(
    string Method,
    string AttributeType,
    string TargetType,
    string ReplacementConstructor);

internal sealed record RewritePlan(
    MethodDef Method,
    Instruction Instruction,
    IMethod Accessor,
    IMethod? ArrayConverter,
    RewriteRecord Record);

internal sealed record FieldConstantRewritePlan(
    Instruction Instruction,
    int Value);

internal sealed record MethodRewritePlan(
    MethodDef Method,
    Instruction Instruction,
    IMethod ProxyMethod,
    int ArgumentIndex,
    IMethod? ArgumentConverter,
    IMethod? ReturnConverter);

internal sealed record ManagedBridgeRewritePlan(
    MethodDef Method,
    Instruction Instruction,
    IMethod BridgeMethod,
    int DroppedArguments,
    ITypeDefOrRef? ReturnCastType,
    int? AppendedInt32 = null,
    string? AppendedString = null);

internal sealed record ProxyCastRewritePlan(
    MethodDef Method,
    Instruction Instruction,
    IMethod BridgeMethod);

internal sealed record ManagedReadProgressGuardPlan(
    MethodDef Method,
    Instruction ReadInstruction,
    IMethod BridgeMethod,
    bool ReplaceMethod);

internal sealed record ManagedPollingWaitRewritePlan(
    MethodDef Method,
    Instruction YieldInstruction,
    IMethod BridgeMethod);

internal sealed record ManagedOptionalDelegateRewritePlan(
    MethodDef Method,
    Instruction FunctionInstruction,
    Instruction ConstructorInstruction,
    Instruction? VirtualReceiverDup,
    ITypeDefOrRef CallbackDeclaringType,
    IMethod BridgeMethod);

internal sealed record OpaqueTypeErasurePlan(
    string SourceAssembly,
    string SourceType,
    IReadOnlyList<FieldDef> Fields,
    IReadOnlyList<MethodDef> Returns,
    IReadOnlyList<(MethodDef Method, int Index)> Parameters,
    IReadOnlyList<(MethodDef Method, Local Local)> Locals,
    IReadOnlyList<(MethodDef Method, Instruction Instruction)> TypeInstructions);

internal readonly record struct ArgumentBridge(int ParameterIndex, IMethod Converter);

internal readonly record struct DelegateConstructorRewrite(
    IMethod ManagedConstructor,
    IMethod ToIl2CppConverter);

internal enum ManagedCallFilterResult
{
    Rewrite,
    Skip,
    Reject
}

public static class ModAssemblyRewriteApi
{
    public const string FormatVersion = "xphorror.pcmod-proxy-rewrite.v18-external-valuetype-kind";

    public static RewriteReport Rewrite(
        string inputPath,
        string outputPath,
        string proxyDirectory,
        string reportPath,
        bool auditOnly = false,
        string? managedBridgeAssemblyPath = null,
        IReadOnlyList<ManagedBridgeRewriteSpec>? managedBridgeRewrites = null,
        IReadOnlyList<ManagedCallBridgeRewriteSpec>? managedCallBridgeRewrites = null,
        ManagedProxyCastBridgeSpec? managedProxyCastBridge = null,
        IReadOnlyList<string>? managedOwnedAssemblyPaths = null,
        IReadOnlyList<ManagedFieldConstantRewriteSpec>? managedFieldConstantRewrites = null,
        ManagedReadProgressGuardSpec? managedReadProgressGuard = null,
        ManagedPollingWaitRewriteSpec? managedPollingWaitRewrite = null,
        ManagedOptionalDelegateRewriteSpec? managedOptionalDelegateRewrite = null)
    {
        var options = Options.Create(
            inputPath,
            outputPath,
            proxyDirectory,
            reportPath,
            auditOnly,
            managedBridgeAssemblyPath,
            managedBridgeRewrites,
            managedCallBridgeRewrites,
            managedProxyCastBridge,
            managedOwnedAssemblyPaths,
            managedFieldConstantRewrites,
            managedReadProgressGuard,
            managedPollingWaitRewrite,
            managedOptionalDelegateRewrite);
        var report = ProxyFieldRewriter.Rewrite(options);
        Directory.CreateDirectory(Path.GetDirectoryName(options.ReportPath)!);
        File.WriteAllText(
            options.ReportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }),
            new UTF8Encoding(false));
        return report;
    }
}

internal sealed class Options
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public required string ProxyDirectory { get; init; }
    public required string ReportPath { get; init; }
    public string? ManagedBridgeAssemblyPath { get; init; }
    public IReadOnlyList<ManagedBridgeRewriteSpec> ManagedBridgeRewrites { get; init; } =
        Array.Empty<ManagedBridgeRewriteSpec>();
    public IReadOnlyList<ManagedCallBridgeRewriteSpec> ManagedCallBridgeRewrites { get; init; } =
        Array.Empty<ManagedCallBridgeRewriteSpec>();
    public IReadOnlyList<ManagedFieldConstantRewriteSpec> ManagedFieldConstantRewrites { get; init; } =
        Array.Empty<ManagedFieldConstantRewriteSpec>();
    public ManagedProxyCastBridgeSpec? ManagedProxyCastBridge { get; init; }
    public ManagedReadProgressGuardSpec? ManagedReadProgressGuard { get; init; }
    public ManagedPollingWaitRewriteSpec? ManagedPollingWaitRewrite { get; init; }
    public ManagedOptionalDelegateRewriteSpec? ManagedOptionalDelegateRewrite { get; init; }
    public IReadOnlyList<string> ManagedOwnedAssemblyPaths { get; init; } = Array.Empty<string>();
    public bool AuditOnly { get; init; }

    public static Options? Parse(string[] args)
    {
        string? input = null;
        string? output = null;
        string? proxies = null;
        string? report = null;
        var auditOnly = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--input" when index + 1 < args.Length: input = args[++index]; break;
                case "--output" when index + 1 < args.Length: output = args[++index]; break;
                case "--proxies" when index + 1 < args.Length: proxies = args[++index]; break;
                case "--report" when index + 1 < args.Length: report = args[++index]; break;
                case "--audit-only": auditOnly = true; break;
                case "--help" or "-h": PrintUsage(); return null;
                default: Console.Error.WriteLine($"Unknown or incomplete argument: {args[index]}"); return null;
            }
        }

        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output) ||
            string.IsNullOrWhiteSpace(proxies) || string.IsNullOrWhiteSpace(report))
        {
            PrintUsage();
            return null;
        }

        return Create(input, output, proxies, report, auditOnly);
    }

    public static Options Create(
        string input,
        string output,
        string proxies,
        string report,
        bool auditOnly,
        string? managedBridgeAssemblyPath = null,
        IReadOnlyList<ManagedBridgeRewriteSpec>? managedBridgeRewrites = null,
        IReadOnlyList<ManagedCallBridgeRewriteSpec>? managedCallBridgeRewrites = null,
        ManagedProxyCastBridgeSpec? managedProxyCastBridge = null,
        IReadOnlyList<string>? managedOwnedAssemblyPaths = null,
        IReadOnlyList<ManagedFieldConstantRewriteSpec>? managedFieldConstantRewrites = null,
        ManagedReadProgressGuardSpec? managedReadProgressGuard = null,
        ManagedPollingWaitRewriteSpec? managedPollingWaitRewrite = null,
        ManagedOptionalDelegateRewriteSpec? managedOptionalDelegateRewrite = null)
    {
        input = Path.GetFullPath(input);
        proxies = Path.GetFullPath(proxies);
        output = Path.GetFullPath(output);
        report = Path.GetFullPath(report);
        if (!File.Exists(input))
            throw new FileNotFoundException("Input MOD assembly does not exist.", input);
        if (!Directory.Exists(proxies))
            throw new DirectoryNotFoundException($"Proxy directory does not exist: {proxies}");
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Input MOD assembly and rewritten output must use different paths.");

        var bridgeRewrites = managedBridgeRewrites?.ToArray() ??
                             Array.Empty<ManagedBridgeRewriteSpec>();
        var callBridgeRewrites = managedCallBridgeRewrites?.ToArray() ??
                                 Array.Empty<ManagedCallBridgeRewriteSpec>();
        var fieldConstantRewrites = managedFieldConstantRewrites?.ToArray() ??
                                    Array.Empty<ManagedFieldConstantRewriteSpec>();
        if (bridgeRewrites.Length != 0 || callBridgeRewrites.Length != 0 ||
            managedProxyCastBridge is not null || managedReadProgressGuard is not null ||
            managedPollingWaitRewrite is not null || managedOptionalDelegateRewrite is not null)
        {
            if (string.IsNullOrWhiteSpace(managedBridgeAssemblyPath))
                throw new InvalidOperationException("Managed bridge rewrites require a bridge assembly path.");
            managedBridgeAssemblyPath = Path.GetFullPath(managedBridgeAssemblyPath);
            if (!File.Exists(managedBridgeAssemblyPath))
                throw new FileNotFoundException(
                    "Managed bridge assembly does not exist.",
                    managedBridgeAssemblyPath);
        }

        return new Options
        {
            InputPath = input,
            OutputPath = output,
            ProxyDirectory = proxies,
            ReportPath = report,
            ManagedBridgeAssemblyPath = managedBridgeAssemblyPath,
            ManagedBridgeRewrites = bridgeRewrites,
            ManagedCallBridgeRewrites = callBridgeRewrites,
            ManagedFieldConstantRewrites = fieldConstantRewrites,
            ManagedProxyCastBridge = managedProxyCastBridge,
            ManagedReadProgressGuard = managedReadProgressGuard,
            ManagedPollingWaitRewrite = managedPollingWaitRewrite,
            ManagedOptionalDelegateRewrite = managedOptionalDelegateRewrite,
            ManagedOwnedAssemblyPaths = managedOwnedAssemblyPaths?.ToArray() ?? Array.Empty<string>(),
            AuditOnly = auditOnly
        };
    }

    private static void PrintUsage()
        => Console.WriteLine(
            "ModAssemblyRewriter --input <mod.dll> --output <rewritten.dll> " +
            "--proxies <proxy-dir> --report <report.json> [--audit-only]");
}

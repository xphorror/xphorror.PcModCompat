using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.MD;
using dnlib.DotNet.Writer;

public enum NativeModStrongNameRewritePolicy
{
    RejectModifiedAssembly,
    PreserveIdentityWithoutResigning
}

public enum NativeModIsolationRewriteMode
{
    Full,
    CallbackOnly
}

public sealed record NativeModIsolationRewriteOptions
{
    /// <summary>
    /// Full mode isolates every supported process-global surface. CallbackOnly mode changes
    /// only legacy HookHelper.Hook registrations and their managed detours, preserving the
    /// MOD's original static, IO, async, and resource behavior.
    /// </summary>
    public NativeModIsolationRewriteMode Mode { get; init; } =
        NativeModIsolationRewriteMode.Full;

    /// <summary>
    /// Strong-name rewriting is rejected by default because the original private key is not
    /// available to the runtime. Android CoreCLR may explicitly opt into preserving the
    /// public-key identity while clearing the signature flag; it must not claim the output is
    /// cryptographically re-signed.
    /// </summary>
    public NativeModStrongNameRewritePolicy StrongNamePolicy { get; init; } =
        NativeModStrongNameRewritePolicy.RejectModifiedAssembly;
}

public sealed record NativeModStaticSlotRewriteRecord(
    int StaticSlotId,
    string MemberIdentity);

public sealed record NativeModAsyncRewriteRecord(
    string MemberIdentity,
    string Kind,
    int RewriteCount);

public sealed record NativeModFileRewriteRecord(
    string MemberIdentity,
    string Kind,
    int RewriteCount);

public sealed record NativeModNetworkRewriteRecord(
    string MemberIdentity,
    string Kind,
    int RewriteCount);

public sealed record NativeModIsolationRewriteReport(
    string FormatVersion,
    int RewrittenAssemblyLocationCalls,
    int RewrittenStaticFieldInstructions,
    int RewrittenAsyncCalls,
    int TrackedTaskReturnMethods,
    int RewrittenFileCalls,
    int RewrittenNetworkCalls,
    IReadOnlyList<NativeModStaticSlotRewriteRecord> StaticSlots,
    IReadOnlyList<NativeModAsyncRewriteRecord> AsyncRewrites,
    IReadOnlyList<NativeModFileRewriteRecord> FileRewrites,
    IReadOnlyList<NativeModNetworkRewriteRecord> NetworkRewrites,
    IReadOnlyList<string> Issues);

public static class NativeModIsolationRewriteApi
{
    public const string FormatVersion =
        "starray-native-isolation-rewrite-v16-generic-static-initializer-owner";

    public static NativeModIsolationRewriteReport Rewrite(
        string inputPath,
        string outputPath,
        string bridgeAssemblyPath,
        IReadOnlyDictionary<string, string>? privateAssemblyPaths = null,
        NativeModIsolationRewriteOptions? options = null)
    {
        options ??= new NativeModIsolationRewriteOptions();
        inputPath = Path.GetFullPath(inputPath);
        outputPath = Path.GetFullPath(outputPath);
        bridgeAssemblyPath = Path.GetFullPath(bridgeAssemblyPath);
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("Native MOD input assembly was not found.", inputPath);
        if (!File.Exists(bridgeAssemblyPath))
            throw new FileNotFoundException("Native MOD bridge assembly was not found.", bridgeAssemblyPath);
        if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Native MOD rewrite cannot replace the source assembly.");

        using var module = ModuleDefMD.Load(inputPath);
        using var bridge = ModuleDefMD.Load(bridgeAssemblyPath);
        var issues = new List<string>();
        var isStrongNamed = module.IsStrongNameSigned || module.Assembly?.HasPublicKey == true;
        var bridgeMembers = ResolveBridgeMembers(module, bridge, issues);
        if (bridgeMembers is null)
            return EmptyReport(issues);

        var callbackOnly = options.Mode == NativeModIsolationRewriteMode.CallbackOnly;
        var asyncPlan = callbackOnly
            ? EmptyAsyncIsolationPlan()
            : PlanAsyncIsolation(module, bridgeMembers, issues);
        var filePlan = callbackOnly
            ? EmptyFileIsolationPlan()
            : PlanFileIsolation(module, bridgeMembers, issues);
        var networkPlan = callbackOnly
            ? EmptyNetworkIsolationPlan()
            : PlanNetworkIsolation(module, bridgeMembers, issues);

        if (isStrongNamed &&
            options.StrongNamePolicy == NativeModStrongNameRewritePolicy.RejectModifiedAssembly)
        {
            if (callbackOnly)
            {
                issues.Add(
                    "strong-name signed Native MOD assemblies require an explicit re-signing " +
                    "policy when callback gate rewriting is needed");
                return EmptyReport(issues);
            }
            var locationCalls = module.GetTypes()
                .SelectMany(type => type.Methods)
                .Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions)
                .Count(IsAssemblyLocationGetter);
            if (locationCalls != 0 && !callbackOnly)
            {
                issues.Add(
                    "strong-name signed Native MOD assemblies require an explicit re-signing " +
                    "policy when Assembly.Location rewriting is needed");
                return EmptyReport(issues);
            }
            if (!callbackOnly &&
                (asyncPlan.TaskReturnMethods.Count != 0 || asyncPlan.CallRewrites.Count != 0))
            {
                issues.Add(
                    "strong-name signed Native MOD assemblies require an explicit re-signing " +
                    "policy when managed async lifecycle rewriting is needed");
                return EmptyReport(issues);
            }
            if (!callbackOnly && filePlan.CallRewrites.Count != 0)
            {
                issues.Add(
                    "strong-name signed Native MOD assemblies require an explicit re-signing " +
                    "policy when filesystem domain rewriting is needed");
                return EmptyReport(issues);
            }
            if (!callbackOnly && networkPlan.CallRewrites.Count != 0)
            {
                issues.Add(
                    "strong-name signed Native MOD assemblies require an explicit re-signing " +
                    "policy when network domain rewriting is needed");
                return EmptyReport(issues);
            }
            if (issues.Count != 0)
                return EmptyReport(issues);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.Copy(inputPath, outputPath, overwrite: false);
            return EmptyReport(Array.Empty<string>());
        }

        var privateAssemblies = privateAssemblyPaths == null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : privateAssemblyPaths.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var accessPlans = callbackOnly
            ? Array.Empty<StaticFieldAccessPlan>()
            : PlanStaticFieldAccesses(module, privateAssemblies, issues);
        var slotRecords = accessPlans
            .Select(plan => plan.FieldPlan.Slot)
            .DistinctBy(record => record.MemberIdentity, StringComparer.Ordinal)
            .OrderBy(record => record.MemberIdentity, StringComparer.Ordinal)
            .ToArray();
        var slotCollision = slotRecords
            .GroupBy(record => record.StaticSlotId)
            .FirstOrDefault(group => group.Select(record => record.MemberIdentity)
                .Distinct(StringComparer.Ordinal).Skip(1).Any());
        if (slotCollision != null)
        {
            issues.Add(
                $"static slot ID collision {slotCollision.Key}: " +
                string.Join(", ", slotCollision.Select(record => record.MemberIdentity).Take(4)));
        }
        if (!callbackOnly)
            ValidateStaticFieldHandleUses(module, issues);
        if (issues.Count != 0)
            return new NativeModIsolationRewriteReport(
                FormatVersion,
                0,
                0,
                0,
                0,
                0,
                0,
                slotRecords,
                asyncPlan.Proofs,
                filePlan.Proofs,
                networkPlan.Proofs,
                issues);

        var rewrittenStatic = 0;
        var rewrittenLocation = 0;
        if (!callbackOnly)
        {
            var initializerPlans = MoveStaticInitializers(
                module,
                accessPlans,
                bridgeMembers.RequireCurrentDomain);
            InjectExplicitTypeInitialization(
                initializerPlans,
                bridgeMembers.EnsureStaticTypeInitialized);
            rewrittenStatic = RewriteStaticFieldAccesses(
                module,
                accessPlans,
                initializerPlans,
                bridgeMembers);
        }
        // Shadow execution changes the physical assembly path. Native MODs commonly use
        // Assembly.Location to locate non-managed assets, so callback-only mode must restore
        // that logical location even though it leaves all other surfaces untouched.
        rewrittenLocation = RewriteAssemblyLocations(module, bridgeMembers.AssemblyLocation);
        var rewrittenAsyncCalls = RewriteCallSites(asyncPlan.CallRewrites);
        var rewrittenFileCalls = RewriteCallSites(filePlan.CallRewrites);
        var rewrittenNetworkCalls = RewriteCallSites(networkPlan.CallRewrites);
        var trackedTaskMethods = RewriteTaskReturnMethods(
            module,
            asyncPlan.TaskReturnMethods,
            bridgeMembers);
        RewriteNativeCallbackGates(module, bridgeMembers, issues);

        if (!callbackOnly)
            ValidateResiduals(module, accessPlans, asyncPlan, issues);
        if (issues.Count != 0)
            return new NativeModIsolationRewriteReport(
                FormatVersion,
                rewrittenLocation,
                rewrittenStatic,
                rewrittenAsyncCalls,
                trackedTaskMethods,
                rewrittenFileCalls,
                rewrittenNetworkCalls,
                slotRecords,
                asyncPlan.Proofs,
                filePlan.Proofs,
                networkPlan.Proofs,
                issues);

        foreach (var method in module.GetTypes()
                     .SelectMany(type => type.Methods)
                     .Where(method => method.HasBody))
        {
            method.Body.SimplifyBranches();
            method.Body.OptimizeBranches();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        WriteRewrittenModule(module, outputPath, isStrongNamed, options);
        return new NativeModIsolationRewriteReport(
            FormatVersion,
            rewrittenLocation,
            rewrittenStatic,
            rewrittenAsyncCalls,
            trackedTaskMethods,
            rewrittenFileCalls,
            rewrittenNetworkCalls,
            slotRecords,
            asyncPlan.Proofs,
            filePlan.Proofs,
            networkPlan.Proofs,
            issues);
    }

    private static void WriteRewrittenModule(
        ModuleDef module,
        string outputPath,
        bool isStrongNamed,
        NativeModIsolationRewriteOptions options)
    {
        if (!isStrongNamed ||
            options.StrongNamePolicy !=
            NativeModStrongNameRewritePolicy.PreserveIdentityWithoutResigning)
        {
            module.Write(outputPath);
            return;
        }

        // No private key is available in the Android runtime. Preserve the assembly public
        // key so AssemblyRef identity remains resolvable, but clear the PE signed flag rather
        // than emitting a stale/invalid signature over the rewritten image.
        var writerOptions = new ModuleWriterOptions(module);
        writerOptions.Cor20HeaderOptions.Flags =
            (writerOptions.Cor20HeaderOptions.Flags ?? module.Cor20HeaderFlags) &
            ~ComImageFlags.StrongNameSigned;
        module.Write(outputPath, writerOptions);
    }

    private static NativeModIsolationRewriteReport EmptyReport(IReadOnlyList<string> issues) =>
        new(
            FormatVersion,
            0,
            0,
            0,
            0,
            0,
            0,
            Array.Empty<NativeModStaticSlotRewriteRecord>(),
            Array.Empty<NativeModAsyncRewriteRecord>(),
            Array.Empty<NativeModFileRewriteRecord>(),
            Array.Empty<NativeModNetworkRewriteRecord>(),
            issues);

    private static BridgeMembers? ResolveBridgeMembers(
        ModuleDef module,
        ModuleDef bridge,
        List<string> issues)
    {
        var pathType = bridge.GetTypes().SingleOrDefault(type =>
            type.FullName == "StArray.ModManager.Runtime.NativeModPathBridge");
        var pathMethod = pathType?.Methods.SingleOrDefault(method =>
            method.Name == "GetAssemblyLocation" &&
            method.IsStatic &&
            method.MethodSig is { Params.Count: 1 } signature &&
            signature.RetType.FullName == "System.String" &&
            signature.Params[0].FullName == "System.Reflection.Assembly");

        var domainType = bridge.GetTypes().SingleOrDefault(type =>
            type.FullName == "StArray.ModManager.Runtime.ModDataDomainRuntime");
        var getMethod = FindGenericDomainMethod(domainType, "GetStaticSlot", 1, false);
        var setMethod = FindGenericDomainMethod(domainType, "SetStaticSlot", 2, false);
        var getReferenceMethod = FindGenericDomainMethod(
            domainType,
            "GetStaticSlotReference",
            1,
            true);
        var getOwnerMethod = FindGenericDomainMethod(
            domainType,
            "GetStaticSlotForOwner",
            1,
            false,
            genericParameterCount: 2);
        var setOwnerMethod = FindGenericDomainMethod(
            domainType,
            "SetStaticSlotForOwner",
            2,
            false,
            genericParameterCount: 2);
        var getOwnerReferenceMethod = FindGenericDomainMethod(
            domainType,
            "GetStaticSlotReferenceForOwner",
            1,
            true,
            genericParameterCount: 2);
        var ensureMethod = domainType?.Methods.SingleOrDefault(method =>
            method.Name == "EnsureStaticTypeInitialized" &&
            method.IsStatic &&
            method.GenericParameters.Count == 0 &&
            method.MethodSig is { Params.Count: 3 } signature &&
            signature.RetType.ElementType == ElementType.Void &&
            signature.Params[0].ElementType == ElementType.I4 &&
            signature.Params[1].FullName == "System.RuntimeMethodHandle" &&
            signature.Params[2].FullName == "System.RuntimeTypeHandle");
        var requireMethod = domainType?.Methods.SingleOrDefault(method =>
            method.Name == "RequireCurrentDomain" &&
            method.IsStatic &&
            method.GenericParameters.Count == 0 &&
            method.MethodSig is { Params.Count: 0 } signature &&
            signature.RetType.ElementType == ElementType.Void);

        var asyncType = bridge.GetTypes().SingleOrDefault(type =>
            type.FullName == "StArray.ModManager.Runtime.ModRuntimeAsyncBridge");
        var requiredAsyncMethods = new[]
        {
            "TrackTask",
            "TrackTaskOfT",
            "RequireCurrentScope",
            "RunAction",
            "RunActionCancelable",
            "RunTask",
            "RunTaskCancelable",
            "RunResult",
            "RunResultCancelable",
            "RunTaskResult",
            "RunTaskResultCancelable",
            "CreateThread",
            "CreateThreadWithStack",
            "CreateParameterizedThread",
            "CreateParameterizedThreadWithStack",
            "StartThread",
            "StartParameterizedThread",
            "QueueWaitCallback",
            "QueueWaitCallbackState",
            "QueueAction",
            "ParallelFor",
            "ParallelForWithOptions",
            "ParallelForEach",
            "ParallelForEachWithOptions",
            "CreateTimer",
            "CreateTimerInt32",
            "CreateTimerUInt32",
            "CreateTimerTimeSpan",
            "DisposeTimer",
            "DisposeTimerWaitHandle",
            "DisposeTimerAsync",
            "CreatePeriodicTimer",
            "WaitForNextTickAsync",
            "WaitForNextTickAsyncCancelable",
            "DisposePeriodicTimer"
        };
        var asyncMethods = new Dictionary<string, MethodDef>(StringComparer.Ordinal);
        if (asyncType != null)
        {
            foreach (var methodName in requiredAsyncMethods)
            {
                var matches = asyncType.Methods
                    .Where(method => method.Name == methodName && method.IsStatic)
                    .ToArray();
                if (matches.Length == 1)
                    asyncMethods.Add(methodName, matches[0]);
                else
                    issues.Add($"ModRuntimeAsyncBridge.{methodName} was not found uniquely");
            }
        }
        else
        {
            issues.Add("ModRuntimeAsyncBridge was not found");
        }
        if (asyncMethods.Count != requiredAsyncMethods.Length)
            return null;

        var requiredFileMethods = new[]
        {
            "GetFullPath",
            "GetTempPath",
            "FileExists",
            "FileReadAllBytes",
            "FileReadAllText",
            "FileWriteAllBytes",
            "FileCreate",
            "FileOpenRead",
            "FileGetLastWriteTimeUtc",
            "FileWriteAllTextEncoding",
            "FileWriteAllText",
            "CreateFileInfo",
            "FileSystemInfoGetExists",
            "FileSystemInfoGetLastWriteTimeUtc",
            "FileInfoGetLength",
            "FileDelete",
            "FileCopy",
            "FileCopyOverwrite",
            "FileMove",
            "FileMoveOverwrite",
            "DirectoryExists",
            "DirectoryMove",
            "DirectoryCreate",
            "DirectoryDelete",
            "DirectoryDeleteRecursive",
            "DirectoryEnumerateFilesSearch",
            "DirectoryGetFilesSearch",
            "DirectoryEnumerateDirectoriesSearch",
            "DirectoryEnumerateFileSystemEntries",
            "OpenFileStream",
            "OpenFileStreamAccess",
            "OpenFileStreamShare",
            "OpenFileStreamOptions",
            "FileOpenOptions",
            "OpenStreamWriterEncoding"
        };
        var fileMethods = new Dictionary<string, MethodDef>(StringComparer.Ordinal);
        foreach (var methodName in requiredFileMethods)
        {
            var matches = pathType?.Methods
                .Where(method => method.Name == methodName && method.IsStatic)
                .ToArray() ?? [];
            if (matches.Length == 1)
                fileMethods.Add(methodName, matches[0]);
            else
                issues.Add($"NativeModPathBridge.{methodName} was not found uniquely");
        }
        if (fileMethods.Count != requiredFileMethods.Length)
            return null;

        var networkType = bridge.GetTypes().SingleOrDefault(type =>
            type.FullName == "StArray.ModManager.Runtime.ModRuntimeNetworkBridge");
        var requiredNetworkMethods = new[]
        {
            "CreateHttpClient",
            "CreateHttpClientWithHandler",
            "CreateHttpClientWithHandlerDisposal",
            "CreateHttpClientHandler",
            "CreateCookieContainer"
        };
        var networkMethods = new Dictionary<string, MethodDef>(StringComparer.Ordinal);
        foreach (var methodName in requiredNetworkMethods)
        {
            var matches = networkType?.Methods
                .Where(method => method.Name == methodName && method.IsStatic)
                .ToArray() ?? [];
            if (matches.Length == 1)
                networkMethods.Add(methodName, matches[0]);
            else
                issues.Add($"ModRuntimeNetworkBridge.{methodName} was not found uniquely");
        }
        if (networkMethods.Count != requiredNetworkMethods.Length)
            return null;

        var hookHelperType = bridge.GetTypes().SingleOrDefault(type =>
            type.FullName == "StArray.ModManager.Runtime.HookHelper");
        var callbackGateType = bridge.GetTypes().SingleOrDefault(type =>
            type.FullName == "StArray.ModManager.Runtime.IModRuntimeCallbackGate");
        var captureRuntimeCallbackGate = hookHelperType?.Methods.SingleOrDefault(method =>
            method.Name == "CaptureRuntimeCallbackGate" &&
            method.IsStatic &&
            method.MethodSig is { Params.Count: 0 } signature &&
            signature.RetType.FullName == callbackGateType?.FullName);
        var hookRuntimeGatedRequired = hookHelperType?.Methods.SingleOrDefault(method =>
            method.Name == "HookRuntimeGatedRequired" &&
            method.IsStatic &&
            method.MethodSig is { Params.Count: 3 } signature &&
            signature.RetType.FullName == "System.IntPtr" &&
            signature.Params[0].FullName == "System.IntPtr" &&
            signature.Params[1].FullName == "System.IntPtr" &&
            signature.Params[2].FullName == callbackGateType?.FullName);
        var callbackGateTryEnter = callbackGateType?.Methods.SingleOrDefault(method =>
            method.Name == "TryEnter" &&
            !method.IsStatic &&
            method.MethodSig is { Params.Count: 1 } signature &&
            signature.RetType.ElementType == ElementType.Boolean);
        var callbackGateReportFailure = callbackGateType?.Methods.SingleOrDefault(method =>
            method.Name == "ReportFailure" &&
            !method.IsStatic &&
            method.MethodSig is { Params.Count: 2 } signature &&
            signature.RetType.ElementType == ElementType.Void &&
            signature.Params[0].FullName == "System.String" &&
            signature.Params[1].FullName == "System.Exception");

        if (pathMethod is null)
            issues.Add("NativeModPathBridge.GetAssemblyLocation(Assembly) was not found");
        if (getMethod is null)
            issues.Add("ModDataDomainRuntime.GetStaticSlot<T>(Int32) was not found");
        if (setMethod is null)
            issues.Add("ModDataDomainRuntime.SetStaticSlot<T>(Int32,T) was not found");
        if (getReferenceMethod is null)
            issues.Add("ModDataDomainRuntime.GetStaticSlotReference<T>(Int32) was not found");
        if (getOwnerMethod is null)
            issues.Add(
                "ModDataDomainRuntime.GetStaticSlotForOwner<T,TOwner>(Int32) was not found");
        if (setOwnerMethod is null)
            issues.Add(
                "ModDataDomainRuntime.SetStaticSlotForOwner<T,TOwner>(Int32,T) was not found");
        if (getOwnerReferenceMethod is null)
            issues.Add(
                "ModDataDomainRuntime.GetStaticSlotReferenceForOwner<T,TOwner>(Int32) " +
                "was not found");
        if (ensureMethod is null)
            issues.Add(
                "ModDataDomainRuntime.EnsureStaticTypeInitialized(" +
                "Int32,RuntimeMethodHandle,RuntimeTypeHandle) was not found");
        if (requireMethod is null)
            issues.Add("ModDataDomainRuntime.RequireCurrentDomain() was not found");
        if (callbackGateType is null)
            issues.Add("IModRuntimeCallbackGate was not found");
        if (captureRuntimeCallbackGate is null)
            issues.Add("HookHelper.CaptureRuntimeCallbackGate() was not found");
        if (hookRuntimeGatedRequired is null)
            issues.Add("HookHelper.HookRuntimeGatedRequired(IntPtr,IntPtr,IModRuntimeCallbackGate) was not found");
        if (callbackGateTryEnter is null)
            issues.Add("IModRuntimeCallbackGate.TryEnter(out IDisposable) was not found");
        if (callbackGateReportFailure is null)
            issues.Add("IModRuntimeCallbackGate.ReportFailure(String,Exception) was not found");
        if (issues.Count != 0)
            return null;

        var importer = new Importer(module, ImporterOptions.TryToUseTypeDefs);
        return new BridgeMembers(
            importer.Import(pathMethod!),
            importer.Import(getMethod!),
            importer.Import(setMethod!),
            importer.Import(getReferenceMethod!),
            importer.Import(getOwnerMethod!),
            importer.Import(setOwnerMethod!),
            importer.Import(getOwnerReferenceMethod!),
            importer.Import(ensureMethod!),
            importer.Import(requireMethod!),
            importer.Import(captureRuntimeCallbackGate!),
            importer.Import(hookRuntimeGatedRequired!),
            importer.Import(callbackGateTryEnter!),
            importer.Import(callbackGateReportFailure!),
            asyncMethods.ToDictionary(
                pair => pair.Key,
                pair => (IMethod)importer.Import(pair.Value),
                StringComparer.Ordinal),
            fileMethods.ToDictionary(
                pair => pair.Key,
                pair => (IMethod)importer.Import(pair.Value),
                StringComparer.Ordinal),
            networkMethods.ToDictionary(
                pair => pair.Key,
                pair => (IMethod)importer.Import(pair.Value),
                StringComparer.Ordinal));
    }

    private static MethodDef? FindGenericDomainMethod(
        TypeDef? type,
        string name,
        int parameterCount,
        bool returnsByReference,
        int genericParameterCount = 1) =>
        type?.Methods.SingleOrDefault(method =>
            method.Name == name &&
            method.IsStatic &&
            method.GenericParameters.Count == genericParameterCount &&
            method.MethodSig is { Params.Count: var count } signature &&
            count == parameterCount &&
            signature.Params[0].ElementType == ElementType.I4 &&
            (returnsByReference
                ? signature.RetType is ByRefSig
                : name.StartsWith("SetStaticSlot", StringComparison.Ordinal)
                    ? signature.RetType.ElementType == ElementType.Void
                    : signature.RetType is GenericMVar));

    private static AsyncIsolationPlan PlanAsyncIsolation(
        ModuleDef module,
        BridgeMembers bridgeMembers,
        List<string> issues)
    {
        var taskReturnMethods = new List<TaskReturnRewritePlan>();
        var callRewrites = new List<CallRewritePlan>();
        foreach (var type in module.GetTypes())
        foreach (var method in type.Methods.Where(candidate => candidate.HasBody))
        {
            var returnType = method.MethodSig?.RetType.RemovePinnedAndModifiers();
            if (IsAsyncVoidMethod(method))
            {
                issues.Add($"async void method cannot be generation-tracked: {method.FullName}");
            }
            if (IsValueTaskType(returnType))
            {
                issues.Add($"ValueTask return requires an async lifecycle bridge: {method.FullName}");
            }
            if (TryGetTaskResultType(returnType, out var taskResultType))
            {
                if (method.Body.Instructions.Any(instruction =>
                        instruction.OpCode.Code == Code.Tailcall))
                {
                    issues.Add($"tail-called Task return cannot be lifecycle-rewritten: {method.FullName}");
                }
                else
                {
                    taskReturnMethods.Add(new TaskReturnRewritePlan(method, taskResultType));
                }
            }

            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj) ||
                    instruction.Operand is not IMethod called)
                {
                    continue;
                }

                if (TryPlanSupportedAsyncCall(
                        method,
                        instruction,
                        called,
                        bridgeMembers,
                        out var rewrite))
                {
                    callRewrites.Add(rewrite!);
                    continue;
                }
                var unsupported = DescribeUnsupportedAsyncCall(instruction, called);
                if (unsupported != null)
                {
                    issues.Add(
                        $"{unsupported}: {method.FullName}@IL_{instruction.Offset:X4} -> " +
                        called.FullName);
                }
            }
        }

        var proofs = taskReturnMethods
            .Select(plan => new NativeModAsyncRewriteRecord(
                BuildMethodIdentity(module, plan.Method),
                "task-return",
                plan.Method.Body.Instructions.Count(instruction =>
                    instruction.OpCode.Code == Code.Ret)))
            .Concat(callRewrites
                .GroupBy(
                    plan => (Identity: BuildMethodIdentity(module, plan.Method), plan.Kind),
                    plan => plan,
                    EqualityComparer<(string Identity, string Kind)>.Default)
                .Select(group => new NativeModAsyncRewriteRecord(
                    group.Key.Identity,
                    group.Key.Kind,
                    group.Count())))
            .OrderBy(proof => proof.MemberIdentity, StringComparer.Ordinal)
            .ThenBy(proof => proof.Kind, StringComparer.Ordinal)
            .ToArray();
        return new AsyncIsolationPlan(taskReturnMethods, callRewrites, proofs);
    }

    private static AsyncIsolationPlan EmptyAsyncIsolationPlan() =>
        new(
            Array.Empty<TaskReturnRewritePlan>(),
            Array.Empty<CallRewritePlan>(),
            Array.Empty<NativeModAsyncRewriteRecord>());

    private static FileIsolationPlan EmptyFileIsolationPlan() =>
        new(
            Array.Empty<CallRewritePlan>(),
            Array.Empty<NativeModFileRewriteRecord>());

    private static NetworkIsolationPlan EmptyNetworkIsolationPlan() =>
        new(
            Array.Empty<CallRewritePlan>(),
            Array.Empty<NativeModNetworkRewriteRecord>());

    private static bool TryPlanSupportedAsyncCall(
        MethodDef owner,
        Instruction instruction,
        IMethod called,
        BridgeMembers bridgeMembers,
        out CallRewritePlan? rewrite)
    {
        rewrite = null;
        var declaringType = called.DeclaringType.FullName;
        var signature = called is MethodSpec specification
            ? specification.Method.MethodSig
            : called.MethodSig;
        if (signature == null)
            return false;
        var genericArguments = called is MethodSpec methodSpec
            ? methodSpec.GenericInstMethodSig?.GenericArguments
            : null;
        string? bridgeName = null;
        string? kind = null;

        if (declaringType == "System.Threading.Tasks.Task" &&
            called.Name == "Run" &&
            instruction.OpCode.Code is Code.Call or Code.Callvirt)
        {
            var cancelable = signature.Params.Count == 2 &&
                             IsType(signature.Params[1], "System.Threading.CancellationToken");
            if (signature.Params.Count is 1 or 2 &&
                (signature.Params.Count == 1 || cancelable))
            {
                var callback = signature.Params[0].RemovePinnedAndModifiers();
                if (signature.GenParamCount == 0 && IsType(callback, "System.Action"))
                    bridgeName = cancelable ? "RunActionCancelable" : "RunAction";
                else if (signature.GenParamCount == 0 && IsFuncOfTask(callback))
                    bridgeName = cancelable ? "RunTaskCancelable" : "RunTask";
                else if (signature.GenParamCount == 1 && IsFuncOfMethodResult(callback))
                    bridgeName = cancelable ? "RunResultCancelable" : "RunResult";
                else if (signature.GenParamCount == 1 && IsFuncOfTaskMethodResult(callback))
                    bridgeName = cancelable
                        ? "RunTaskResultCancelable"
                        : "RunTaskResult";
            }
            kind = "task-run";
        }
        else if (declaringType == "System.Threading.Thread" &&
                 called.Name == ".ctor" &&
                 instruction.OpCode.Code == Code.Newobj)
        {
            if (MatchesParameters(signature, "System.Threading.ThreadStart"))
                bridgeName = "CreateThread";
            else if (MatchesParameters(
                         signature,
                         "System.Threading.ThreadStart",
                         "System.Int32"))
                bridgeName = "CreateThreadWithStack";
            else if (MatchesParameters(signature, "System.Threading.ParameterizedThreadStart"))
                bridgeName = "CreateParameterizedThread";
            else if (MatchesParameters(
                         signature,
                         "System.Threading.ParameterizedThreadStart",
                         "System.Int32"))
                bridgeName = "CreateParameterizedThreadWithStack";
            kind = "thread-create";
        }
        else if (declaringType == "System.Threading.Thread" &&
                 called.Name == "Start" &&
                 instruction.OpCode.Code is Code.Call or Code.Callvirt)
        {
            if (signature.Params.Count == 0)
                bridgeName = "StartThread";
            else if (MatchesParameters(signature, "System.Object"))
                bridgeName = "StartParameterizedThread";
            kind = "thread-start";
        }
        else if (declaringType == "System.Threading.ThreadPool" &&
                 called.Name == "QueueUserWorkItem" &&
                 instruction.OpCode.Code is Code.Call or Code.Callvirt)
        {
            if (signature.GenParamCount == 0 &&
                MatchesParameters(signature, "System.Threading.WaitCallback"))
                bridgeName = "QueueWaitCallback";
            else if (signature.GenParamCount == 0 &&
                     MatchesParameters(
                         signature,
                         "System.Threading.WaitCallback",
                         "System.Object"))
                bridgeName = "QueueWaitCallbackState";
            else if (signature.GenParamCount == 1 &&
                     signature.Params.Count == 3 &&
                     IsActionOfMethodResult(signature.Params[0]) &&
                     signature.Params[1].RemovePinnedAndModifiers().ElementType == ElementType.MVar &&
                     IsType(signature.Params[2], "System.Boolean"))
                 bridgeName = "QueueAction";
            kind = "thread-pool";
        }
        else if (declaringType == "System.Threading.Tasks.Parallel" &&
                 called.Name == "For" &&
                 instruction.OpCode.Code is Code.Call or Code.Callvirt)
        {
            if (signature.GenParamCount == 0 &&
                signature.Params.Count == 3 &&
                IsType(signature.Params[0], "System.Int32") &&
                IsType(signature.Params[1], "System.Int32") &&
                IsActionOfType(signature.Params[2], "System.Int32"))
            {
                bridgeName = "ParallelFor";
            }
            else if (signature.GenParamCount == 0 &&
                     signature.Params.Count == 4 &&
                     IsType(signature.Params[0], "System.Int32") &&
                     IsType(signature.Params[1], "System.Int32") &&
                     IsType(signature.Params[2], "System.Threading.Tasks.ParallelOptions") &&
                     IsActionOfType(signature.Params[3], "System.Int32"))
            {
                bridgeName = "ParallelForWithOptions";
            }
            kind = "parallel-for";
        }
        else if (declaringType == "System.Threading.Tasks.Parallel" &&
                 called.Name == "ForEach" &&
                 instruction.OpCode.Code is Code.Call or Code.Callvirt)
        {
            if (signature.GenParamCount == 1 &&
                signature.Params.Count == 2 &&
                IsEnumerableOfMethodResult(signature.Params[0]) &&
                IsActionOfMethodResult(signature.Params[1]))
            {
                bridgeName = "ParallelForEach";
            }
            else if (signature.GenParamCount == 1 &&
                     signature.Params.Count == 3 &&
                     IsEnumerableOfMethodResult(signature.Params[0]) &&
                     IsType(signature.Params[1], "System.Threading.Tasks.ParallelOptions") &&
                     IsActionOfMethodResult(signature.Params[2]))
            {
                bridgeName = "ParallelForEachWithOptions";
            }
            kind = "parallel-foreach";
        }
        else if (declaringType == "System.Threading.Timer" &&
                 called.Name == ".ctor" &&
                 instruction.OpCode.Code == Code.Newobj)
        {
            if (MatchesParameters(signature, "System.Threading.TimerCallback"))
                bridgeName = "CreateTimer";
            else if (MatchesParameters(
                         signature,
                         "System.Threading.TimerCallback",
                         "System.Object",
                         "System.Int32",
                         "System.Int32"))
                bridgeName = "CreateTimerInt32";
            else if (MatchesParameters(
                         signature,
                         "System.Threading.TimerCallback",
                         "System.Object",
                         "System.UInt32",
                         "System.UInt32"))
                bridgeName = "CreateTimerUInt32";
            else if (MatchesParameters(
                         signature,
                         "System.Threading.TimerCallback",
                         "System.Object",
                         "System.TimeSpan",
                         "System.TimeSpan"))
                bridgeName = "CreateTimerTimeSpan";
            kind = "timer-create";
        }
        else if (declaringType == "System.Threading.Timer" &&
                 called.Name == "Dispose" &&
                 instruction.OpCode.Code is Code.Call or Code.Callvirt)
        {
            if (signature.Params.Count == 0)
                bridgeName = "DisposeTimer";
            else if (MatchesParameters(signature, "System.Threading.WaitHandle"))
                bridgeName = "DisposeTimerWaitHandle";
            kind = "timer-dispose";
        }
        else if (declaringType == "System.Threading.Timer" &&
                 called.Name == "DisposeAsync" &&
                 signature.Params.Count == 0 &&
                 instruction.OpCode.Code is Code.Call or Code.Callvirt)
        {
            bridgeName = "DisposeTimerAsync";
            kind = "timer-dispose";
        }
        else if (declaringType == "System.Threading.PeriodicTimer")
        {
            if (called.Name == ".ctor" &&
                instruction.OpCode.Code == Code.Newobj &&
                MatchesParameters(signature, "System.TimeSpan"))
            {
                bridgeName = "CreatePeriodicTimer";
                kind = "periodic-timer-create";
            }
            else if (called.Name == "WaitForNextTickAsync" &&
                     instruction.OpCode.Code is Code.Call or Code.Callvirt)
            {
                if (signature.Params.Count == 0)
                    bridgeName = "WaitForNextTickAsync";
                else if (MatchesParameters(signature, "System.Threading.CancellationToken"))
                    bridgeName = "WaitForNextTickAsyncCancelable";
                kind = "periodic-timer-wait";
            }
            else if (called.Name == "Dispose" &&
                     instruction.OpCode.Code is (Code.Call or Code.Callvirt) &&
                     signature.Params.Count == 0)
            {
                bridgeName = "DisposePeriodicTimer";
                kind = "periodic-timer-dispose";
            }
        }

        if (bridgeName == null || kind == null)
            return false;

        var replacement = bridgeMembers.AsyncMethods[bridgeName];
        if (replacement.MethodSig?.GenParamCount > 0)
        {
            if (genericArguments is not { Count: > 0 })
                return false;
            replacement = CloseGenericMethod(replacement, genericArguments.ToArray());
        }
        rewrite = new CallRewritePlan(owner, instruction, replacement, kind);
        return true;
    }

    /// <summary>
    /// Maps the filesystem entry points observed in real Android Managed MOD assemblies onto
    /// <c>NativeModPathBridge</c>. Anything not matched here and recognised by
    /// <see cref="DescribeUnsupportedFileCall"/> fails the rewrite closed.
    /// </summary>
    /// <remarks>
    /// <see cref="Path.Combine(string, string)"/>, <c>GetDirectoryName</c> and
    /// <c>GetFileName</c> are deliberately NOT rewritten: they are pure string functions with
    /// no ambient state, so routing them through the bridge would add cost without changing
    /// isolation. <see cref="Path.GetFullPath(string)"/> IS rewritten because it resolves
    /// relative paths against the shared process working directory.
    /// </remarks>
    private static bool TryPlanSupportedFileCall(
        MethodDef owner,
        Instruction instruction,
        IMethod called,
        BridgeMembers bridgeMembers,
        out CallRewritePlan? rewrite)
    {
        rewrite = null;
        var declaringType = called.DeclaringType?.FullName;
        var signature = called is MethodSpec specification
            ? specification.Method.MethodSig
            : called.MethodSig;
        if (signature == null || declaringType == null)
            return false;
        if (signature.GenParamCount != 0)
            return false;

        string? bridgeName = null;
        string? kind = null;

        switch (declaringType)
        {
            case "System.IO.Path" when called.Name == "GetFullPath" &&
                                       MatchesParameters(signature, "System.String"):
                bridgeName = "GetFullPath";
                kind = "path-full";
                break;

            case "System.IO.Path" when called.Name == "GetTempPath" &&
                                       MatchesParameters(signature):
                bridgeName = "GetTempPath";
                kind = "path-temp";
                break;

            case "System.IO.File":
                kind = "file";
                bridgeName = called.Name.String switch
                {
                    "Exists" when MatchesParameters(signature, "System.String") => "FileExists",
                    "Delete" when MatchesParameters(signature, "System.String") => "FileDelete",
                    "ReadAllBytes" when MatchesParameters(signature, "System.String") =>
                        "FileReadAllBytes",
                    "ReadAllText" when MatchesParameters(signature, "System.String") =>
                        "FileReadAllText",
                    "WriteAllBytes" when MatchesParameters(
                        signature, "System.String", "System.Byte[]") =>
                        "FileWriteAllBytes",
                    "Create" when instruction.OpCode.Code is Code.Call or Code.Callvirt &&
                                  MatchesParameters(signature, "System.String") =>
                        "FileCreate",
                    "OpenRead" when MatchesParameters(signature, "System.String") =>
                        "FileOpenRead",
                    "GetLastWriteTimeUtc" when MatchesParameters(signature, "System.String") =>
                        "FileGetLastWriteTimeUtc",
                    "Open" when instruction.OpCode.Code is Code.Call or Code.Callvirt &&
                               MatchesParameters(signature, "System.String", "System.IO.FileStreamOptions") =>
                        "FileOpenOptions",
                    "WriteAllText" when MatchesParameters(
                        signature, "System.String", "System.String", "System.Text.Encoding") =>
                        "FileWriteAllTextEncoding",
                    "WriteAllText" when MatchesParameters(
                        signature, "System.String", "System.String") =>
                        "FileWriteAllText",
                    "Copy" when MatchesParameters(signature, "System.String", "System.String") =>
                        "FileCopy",
                    "Copy" when MatchesParameters(
                        signature, "System.String", "System.String", "System.Boolean") =>
                        "FileCopyOverwrite",
                    "Move" when MatchesParameters(signature, "System.String", "System.String") =>
                        "FileMove",
                    "Move" when MatchesParameters(
                        signature, "System.String", "System.String", "System.Boolean") =>
                        "FileMoveOverwrite",
                    _ => null
                };
                break;

            case "System.IO.Directory":
                kind = "directory";
                bridgeName = called.Name.String switch
                {
                    "Exists" when MatchesParameters(signature, "System.String") =>
                        "DirectoryExists",
                    "CreateDirectory" when MatchesParameters(signature, "System.String") =>
                        "DirectoryCreate",
                    "Delete" when MatchesParameters(signature, "System.String") =>
                        "DirectoryDelete",
                    "Delete" when MatchesParameters(
                        signature, "System.String", "System.Boolean") =>
                        "DirectoryDeleteRecursive",
                    "Move" when MatchesParameters(
                        signature, "System.String", "System.String") =>
                        "DirectoryMove",
                    "EnumerateFiles" when MatchesParameters(
                        signature,
                        "System.String",
                        "System.String",
                        "System.IO.SearchOption") =>
                        "DirectoryEnumerateFilesSearch",
                    "GetFiles" when MatchesParameters(
                        signature,
                        "System.String",
                        "System.String",
                        "System.IO.SearchOption") =>
                        "DirectoryGetFilesSearch",
                    "EnumerateDirectories" when MatchesParameters(
                        signature,
                        "System.String",
                        "System.String",
                        "System.IO.SearchOption") =>
                        "DirectoryEnumerateDirectoriesSearch",
                    "EnumerateFileSystemEntries" when MatchesParameters(
                        signature,
                        "System.String") =>
                        "DirectoryEnumerateFileSystemEntries",
                    _ => null
                };
                break;

            case "System.IO.FileStream" when called.Name == ".ctor" &&
                                              instruction.OpCode.Code == Code.Newobj:
                kind = "file-stream";
                bridgeName =
                    MatchesParameters(signature, "System.String", "System.IO.FileMode")
                        ? "OpenFileStream"
                        : MatchesParameters(
                            signature,
                            "System.String",
                            "System.IO.FileMode",
                            "System.IO.FileAccess")
                            ? "OpenFileStreamAccess"
                            : MatchesParameters(
                                signature,
                                "System.String",
                                "System.IO.FileMode",
                                "System.IO.FileAccess",
                                "System.IO.FileShare")
                                ? "OpenFileStreamShare"
                                : MatchesParameters(
                                    signature,
                                    "System.String",
                                    "System.IO.FileMode",
                                    "System.IO.FileAccess",
                                    "System.IO.FileShare",
                                    "System.Int32",
                                    "System.IO.FileOptions")
                                    ? "OpenFileStreamOptions"
                                    : null;
                break;

            case "System.IO.StreamWriter" when called.Name == ".ctor" &&
                                                instruction.OpCode.Code == Code.Newobj:
                kind = "stream-writer";
                bridgeName = MatchesParameters(
                    signature,
                    "System.String",
                    "System.Boolean",
                    "System.Text.Encoding")
                    ? "OpenStreamWriterEncoding"
                    : null;
                break;

            case "System.IO.FileInfo" when called.Name == ".ctor" &&
                                             instruction.OpCode.Code == Code.Newobj &&
                                             MatchesParameters(signature, "System.String"):
                kind = "file-info-create";
                bridgeName = "CreateFileInfo";
                break;

            case "System.IO.FileSystemInfo" when instruction.OpCode.Code is Code.Call or Code.Callvirt:
                kind = "file-info-property";
                bridgeName = called.Name.String switch
                {
                    "get_Exists" when signature.Params.Count == 0 =>
                        "FileSystemInfoGetExists",
                    "get_LastWriteTimeUtc" when signature.Params.Count == 0 =>
                        "FileSystemInfoGetLastWriteTimeUtc",
                    _ => null
                };
                break;

            case "System.IO.FileInfo" when instruction.OpCode.Code is Code.Call or Code.Callvirt:
                kind = "file-info-property";
                bridgeName = called.Name.String switch
                {
                    "get_Exists" when signature.Params.Count == 0 =>
                        "FileSystemInfoGetExists",
                    "get_LastWriteTimeUtc" when signature.Params.Count == 0 =>
                        "FileSystemInfoGetLastWriteTimeUtc",
                    "get_Length" when signature.Params.Count == 0 => "FileInfoGetLength",
                    _ => null
                };
                break;
        }

        if (bridgeName == null || kind == null)
            return false;
        if (!bridgeMembers.FileMethods.TryGetValue(bridgeName, out var replacement))
            return false;

        rewrite = new CallRewritePlan(owner, instruction, replacement, kind);
        return true;
    }

    /// <summary>
    /// Filesystem entry points that would escape domain ownership. Recognised but not
    /// rewritten, so the shadow package fails closed rather than silently letting a MOD reach
    /// the shared filesystem.
    /// </summary>
    /// <summary>
    /// Maps the network client-producing entry points onto <c>ModRuntimeNetworkBridge</c>.
    /// </summary>
    /// <remarks>
    /// Only construction is rewritten. Operations on an already domain-bound client
    /// (<c>GetAsync</c>, <c>DefaultRequestHeaders</c>, <c>Timeout</c>) and on the objects it
    /// returns (<c>HttpResponseMessage</c>, <c>HttpContent</c>) inherit that client's domain, so
    /// rewriting them would add cost without changing ownership — the same reasoning that
    /// leaves <c>Path.Combine</c> alone.
    /// </remarks>
    private static bool TryPlanSupportedNetworkCall(
        MethodDef owner,
        Instruction instruction,
        IMethod called,
        BridgeMembers bridgeMembers,
        out CallRewritePlan? rewrite)
    {
        rewrite = null;
        if (instruction.OpCode.Code != Code.Newobj)
            return false;
        var declaringType = called.DeclaringType?.FullName;
        var signature = called is MethodSpec specification
            ? specification.Method.MethodSig
            : called.MethodSig;
        if (signature == null || declaringType == null || called.Name != ".ctor")
            return false;

        string? bridgeName = null;
        string? kind = null;
        switch (declaringType)
        {
            case "System.Net.Http.HttpClient":
                kind = "http-client";
                bridgeName = signature.Params.Count switch
                {
                    0 => "CreateHttpClient",
                    1 when IsType(signature.Params[0], "System.Net.Http.HttpMessageHandler") =>
                        "CreateHttpClientWithHandler",
                    2 when IsType(signature.Params[0], "System.Net.Http.HttpMessageHandler") &&
                           IsType(signature.Params[1], "System.Boolean") =>
                        "CreateHttpClientWithHandlerDisposal",
                    _ => null
                };
                break;
            case "System.Net.Http.HttpClientHandler" when signature.Params.Count == 0:
                bridgeName = "CreateHttpClientHandler";
                kind = "http-handler";
                break;
            case "System.Net.CookieContainer" when signature.Params.Count == 0:
                bridgeName = "CreateCookieContainer";
                kind = "cookie-container";
                break;
        }

        if (bridgeName == null || kind == null)
            return false;
        if (!bridgeMembers.NetworkMethods.TryGetValue(bridgeName, out var replacement))
            return false;
        rewrite = new CallRewritePlan(owner, instruction, replacement, kind);
        return true;
    }

    /// <summary>
    /// Network entry points that would escape domain ownership: process-global policy, socket
    /// level access and client factories the bridge does not model.
    /// </summary>
    private static string? DescribeUnsupportedNetworkCall(
        Instruction instruction,
        IMethod called)
    {
        var declaringType = called.DeclaringType?.FullName ?? string.Empty;
        var name = called.Name.String;

        if (declaringType == "System.Net.ServicePointManager")
            return "ServicePointManager is process-global network policy";
        if (declaringType is "System.Net.WebRequest" or "System.Net.HttpWebRequest" &&
            name is "Create" or "CreateHttp" or "CreateDefault")
            return "WebRequest factory is not domain scoped";
        if (declaringType is "System.Net.WebClient" && instruction.OpCode.Code == Code.Newobj)
            return "WebClient is not domain scoped";
        if (declaringType is "System.Net.Http.HttpClient" or "System.Net.Http.HttpClientHandler"
                or "System.Net.CookieContainer" &&
            instruction.OpCode.Code == Code.Newobj)
            return $"unsupported {declaringType} constructor overload";
        if (declaringType == "System.Net.Http.SocketsHttpHandler" &&
            instruction.OpCode.Code == Code.Newobj)
            return "SocketsHttpHandler bypasses the domain network bridge";
        if (declaringType.StartsWith("System.Net.Sockets.", StringComparison.Ordinal))
            return "raw socket access is not domain scoped";
        if (declaringType == "System.Net.Http.HttpClient" && name == "set_DefaultProxy")
            return "process-global default proxy may not be changed by a MOD";
        return null;
    }

    private static NetworkIsolationPlan PlanNetworkIsolation(
        ModuleDef module,
        BridgeMembers bridgeMembers,
        List<string> issues)
    {
        var callRewrites = new List<CallRewritePlan>();
        foreach (var method in module.GetTypes()
                     .SelectMany(type => type.Methods)
                     .Where(method => method.HasBody))
        {
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj) ||
                    instruction.Operand is not IMethod called)
                {
                    continue;
                }
                if (TryPlanSupportedNetworkCall(
                        method,
                        instruction,
                        called,
                        bridgeMembers,
                        out var rewrite))
                {
                    callRewrites.Add(rewrite!);
                    continue;
                }
                var unsupported = DescribeUnsupportedNetworkCall(instruction, called);
                if (unsupported != null)
                {
                    issues.Add(
                        $"{unsupported}: {method.FullName}@IL_{instruction.Offset:X4} -> " +
                        called.FullName);
                }
            }
        }

        var proofs = callRewrites
            .GroupBy(plan => (Identity: BuildMethodIdentity(module, plan.Method), plan.Kind))
            .Select(group => new NativeModNetworkRewriteRecord(
                group.Key.Identity,
                group.Key.Kind,
                group.Count()))
            .OrderBy(proof => proof.MemberIdentity, StringComparer.Ordinal)
            .ThenBy(proof => proof.Kind, StringComparer.Ordinal)
            .ToArray();
        return new NetworkIsolationPlan(callRewrites, proofs);
    }

    private static string? DescribeUnsupportedFileCall(
        Instruction instruction,
        IMethod called)
    {
        var declaringType = called.DeclaringType?.FullName ?? string.Empty;
        var name = called.Name.String;

        if (declaringType is "System.IO.File" or "System.IO.Directory")
            return $"unsupported {declaringType} entry point";
        if (declaringType is "System.IO.FileStream" && name == ".ctor")
            return "unsupported FileStream constructor overload";
        if (declaringType is "System.IO.FileInfo" or "System.IO.DirectoryInfo" &&
            instruction.OpCode.Code == Code.Newobj)
            return $"{declaringType} bypasses the domain path bridge";
        if (declaringType is "System.IO.StreamReader" or "System.IO.StreamWriter" &&
            instruction.OpCode.Code == Code.Newobj &&
            called.MethodSig is { Params.Count: > 0 } signature &&
            IsType(signature.Params[0], "System.String"))
            return $"{declaringType} path constructor bypasses the domain path bridge";
        if (declaringType == "System.IO.Path" &&
            name is "GetTempPath" or "GetTempFileName" or "GetRandomFileName")
            return "process-global temp path is not domain scoped";
        if (declaringType == "System.Environment" &&
            name is "GetFolderPath" or "get_CurrentDirectory" or "set_CurrentDirectory")
            return "process-global directory state is not domain scoped";
        if (declaringType == "System.IO.Directory" && name == "SetCurrentDirectory")
            return "process-global working directory may not be changed by a MOD";
        return null;
    }

    private static FileIsolationPlan PlanFileIsolation(
        ModuleDef module,
        BridgeMembers bridgeMembers,
        List<string> issues)
    {
        var callRewrites = new List<CallRewritePlan>();
        foreach (var method in module.GetTypes()
                     .SelectMany(type => type.Methods)
                     .Where(method => method.HasBody))
        {
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj) ||
                    instruction.Operand is not IMethod called)
                {
                    continue;
                }

                if (TryPlanSupportedFileCall(
                        method,
                        instruction,
                        called,
                        bridgeMembers,
                        out var rewrite))
                {
                    callRewrites.Add(rewrite!);
                    continue;
                }
                var unsupported = DescribeUnsupportedFileCall(instruction, called);
                if (unsupported != null)
                {
                    issues.Add(
                        $"{unsupported}: {method.FullName}@IL_{instruction.Offset:X4} -> " +
                        called.FullName);
                }
            }
        }

        var proofs = callRewrites
            .GroupBy(plan => (Identity: BuildMethodIdentity(module, plan.Method), plan.Kind))
            .Select(group => new NativeModFileRewriteRecord(
                group.Key.Identity,
                group.Key.Kind,
                group.Count()))
            .OrderBy(proof => proof.MemberIdentity, StringComparer.Ordinal)
            .ThenBy(proof => proof.Kind, StringComparer.Ordinal)
            .ToArray();
        return new FileIsolationPlan(callRewrites, proofs);
    }

    private static string? DescribeUnsupportedAsyncCall(
        Instruction instruction,
        IMethod called)
    {
        var declaringType = called.DeclaringType.FullName;
        var name = called.Name.String;
        if (declaringType.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal) &&
            (name is "Run" or "ContinueWith" or "Start" ||
             instruction.OpCode.Code == Code.Newobj))
            return "unsupported Task scheduling entry";
        if (declaringType.StartsWith("System.Threading.Tasks.TaskFactory", StringComparison.Ordinal) &&
            name == "StartNew")
            return "TaskFactory.StartNew is not generation-bound";
        if (declaringType == "System.Threading.Thread" &&
            (name is ".ctor" or "Start"))
            return "unsupported Thread lifecycle overload";
        if (declaringType == "System.Threading.ThreadPool" &&
            (name.Contains("Queue", StringComparison.Ordinal) ||
             name.Contains("RegisterWait", StringComparison.Ordinal)))
            return "unsupported ThreadPool scheduling entry";
        if (declaringType == "System.Threading.Timer" &&
            (name == ".ctor" || name.StartsWith("Dispose", StringComparison.Ordinal)))
            return "unsupported Timer lifecycle overload";
        if (declaringType == "System.Threading.PeriodicTimer" &&
            (name is ".ctor" or "WaitForNextTickAsync" or "Dispose"))
            return "unsupported PeriodicTimer lifecycle overload";
        if (declaringType == "System.Timers.Timer" && name == ".ctor")
            return "unsupported timer scheduler";
        if (declaringType == "System.Threading.ExecutionContext" && name == "SuppressFlow")
            return "ExecutionContext flow suppression breaks MOD ownership";
        if (declaringType == "System.Threading.SynchronizationContext" &&
            (name is "Post" or "Send"))
            return "SynchronizationContext callback is not generation-bound";
        if (declaringType == "System.Threading.CancellationToken" && name == "Register")
            return "CancellationToken callback registration is not generation-bound";
        if (declaringType == "System.Threading.Tasks.Parallel")
            return "Parallel callback execution is not generation-bound";
        return null;
    }

    private static bool IsAsyncVoidMethod(MethodDef method) =>
        method.MethodSig?.RetType.ElementType == ElementType.Void &&
        method.CustomAttributes.Any(attribute =>
            attribute.AttributeType.FullName ==
            "System.Runtime.CompilerServices.AsyncStateMachineAttribute");

    private static bool IsValueTaskType(TypeSig? type)
    {
        type = type?.RemovePinnedAndModifiers();
        return IsType(type, "System.Threading.Tasks.ValueTask") ||
               type is GenericInstSig generic &&
               generic.GenericType.TypeDefOrRef.FullName == "System.Threading.Tasks.ValueTask`1";
    }

    private static bool TryGetTaskResultType(TypeSig? type, out TypeSig? resultType)
    {
        type = type?.RemovePinnedAndModifiers();
        if (IsType(type, "System.Threading.Tasks.Task"))
        {
            resultType = null;
            return true;
        }
        if (type is GenericInstSig generic &&
            generic.GenericType.TypeDefOrRef.FullName == "System.Threading.Tasks.Task`1" &&
            generic.GenericArguments.Count == 1)
        {
            resultType = generic.GenericArguments[0];
            return true;
        }
        resultType = null;
        return false;
    }

    private static bool MatchesParameters(MethodSig signature, params string[] types) =>
        signature.Params.Count == types.Length &&
        signature.Params.Select((type, index) => IsType(type, types[index])).All(value => value);

    private static bool IsType(TypeSig? type, string fullName) =>
        type?.RemovePinnedAndModifiers().FullName == fullName;

    private static bool IsFuncOfTask(TypeSig type) =>
        TryGetSingleGenericArgument(type, "System.Func`1", out var argument) &&
        IsType(argument, "System.Threading.Tasks.Task");

    private static bool IsFuncOfMethodResult(TypeSig type) =>
        TryGetSingleGenericArgument(type, "System.Func`1", out var argument) &&
        argument.RemovePinnedAndModifiers().ElementType == ElementType.MVar;

    private static bool IsFuncOfTaskMethodResult(TypeSig type) =>
        TryGetSingleGenericArgument(type, "System.Func`1", out var argument) &&
        argument.RemovePinnedAndModifiers() is GenericInstSig task &&
        task.GenericType.TypeDefOrRef.FullName == "System.Threading.Tasks.Task`1" &&
        task.GenericArguments.Count == 1 &&
        task.GenericArguments[0].RemovePinnedAndModifiers().ElementType == ElementType.MVar;

    private static bool IsActionOfMethodResult(TypeSig type) =>
        TryGetSingleGenericArgument(type, "System.Action`1", out var argument) &&
        argument.RemovePinnedAndModifiers().ElementType == ElementType.MVar;

    private static bool IsActionOfType(TypeSig type, string argumentFullName) =>
        TryGetSingleGenericArgument(type, "System.Action`1", out var argument) &&
        IsType(argument, argumentFullName);

    private static bool IsEnumerableOfMethodResult(TypeSig type) =>
        TryGetSingleGenericArgument(
            type,
            "System.Collections.Generic.IEnumerable`1",
            out var argument) &&
        argument.RemovePinnedAndModifiers().ElementType == ElementType.MVar;

    private static bool TryGetSingleGenericArgument(
        TypeSig type,
        string genericType,
        out TypeSig argument)
    {
        if (type.RemovePinnedAndModifiers() is GenericInstSig instance &&
            instance.GenericType.TypeDefOrRef.FullName == genericType &&
            instance.GenericArguments.Count == 1)
        {
            argument = instance.GenericArguments[0];
            return true;
        }
        argument = null!;
        return false;
    }

    private static string BuildMethodIdentity(ModuleDef module, MethodDef method) =>
        $"{module.Assembly?.Name}|{module.Mvid:D}|{method.FullName}";

    private static IReadOnlyList<StaticFieldAccessPlan> PlanStaticFieldAccesses(
        ModuleDef module,
        IReadOnlySet<string> privateAssemblies,
        List<string> issues)
    {
        var fieldPlans = new Dictionary<string, StaticFieldPlan>(StringComparer.Ordinal);
        var accessPlans = new List<StaticFieldAccessPlan>();
        foreach (var type in module.GetTypes())
        foreach (var method in type.Methods.Where(candidate => candidate.HasBody))
        {
            var instructions = method.Body.Instructions;
            for (var index = 0; index < instructions.Count; index++)
            {
                var instruction = instructions[index];
                if (instruction.OpCode.Code is not (Code.Ldsfld or Code.Stsfld or Code.Ldsflda) ||
                    instruction.Operand is not IField operand)
                {
                    continue;
                }

                if (!TryResolveLocalFieldAccess(module, operand, method, out var access))
                {
                    var assemblyName = operand.DeclaringType.DefinitionAssembly?.Name?.String;
                    if (!string.IsNullOrWhiteSpace(assemblyName) &&
                        privateAssemblies.Contains(assemblyName))
                    {
                        issues.Add(
                            $"cross-assembly private static field access is not yet supported: " +
                            $"{method.FullName}@IL_{instruction.Offset:X4} -> {operand.FullName}");
                    }
                    continue;
                }
                var field = access.Field;
                if (!field.IsStatic)
                {
                    issues.Add(
                        $"static field opcode targets a non-static field: " +
                        $"{method.FullName}@IL_{instruction.Offset:X4} -> {field.FullName}");
                    continue;
                }
                if (field.IsLiteral)
                    continue;

                if (index > 0 && IsUnsupportedFieldPrefix(instructions[index - 1]))
                {
                    issues.Add(
                        $"static field access prefix is not supported: " +
                        $"{method.FullName}@IL_{instruction.Offset:X4} -> {field.FullName}");
                    continue;
                }
                if (field.HasFieldRVA)
                {
                    if (IsCompilerGeneratedImmutableRva(module, field))
                    {
                        continue;
                    }
                    issues.Add($"RVA-backed mutable static field is not supported: {field.FullName}");
                    continue;
                }
                if (field.CustomAttributes.Any(attribute =>
                        attribute.AttributeType.FullName == "System.ThreadStaticAttribute"))
                {
                    issues.Add($"ThreadStatic field requires a domain-thread slot: {field.FullName}");
                    continue;
                }
                var fieldType = access.FieldType;
                if (fieldType is null || IsUnsupportedStaticSlotType(fieldType))
                {
                    issues.Add($"static field type cannot be represented by a domain slot: {field.FullName}");
                    continue;
                }

                // The slot ID belongs to the field definition. The closed owner is carried by
                // each access plan and is used as the runtime isolation key, so a generic
                // initializer and its closed consumers must share one field slot ID.
                var identity = BuildFieldIdentity(module, field);
                if (!fieldPlans.TryGetValue(identity, out var fieldPlan))
                {
                    fieldPlan = new StaticFieldPlan(
                        field,
                        new NativeModStaticSlotRewriteRecord(
                            ComputeStableId("field|" + identity),
                            identity));
                    fieldPlans.Add(identity, fieldPlan);
                }
                accessPlans.Add(new StaticFieldAccessPlan(
                    method,
                    instruction,
                    fieldPlan,
                    fieldType,
                    access.OwnerType,
                    RequiresOwnerAwareStaticSlot(access.OwnerType)));
            }
        }
        return accessPlans;
    }

    private static FieldDef? ResolveLocalField(ModuleDef module, IField operand)
    {
        if (operand is FieldDef definition)
            return ReferenceEquals(definition.Module, module) ? definition : null;
        if (!string.Equals(
                operand.DeclaringType.DefinitionAssembly?.Name?.String,
                module.Assembly?.Name?.String,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        try
        {
            var owner = ResolveLocalTypeDefinition(operand.DeclaringType);
            if (owner == null || !ReferenceEquals(owner.Module, module))
                return null;

            // dnlib's resolver may ignore MemberRef.Class when it is a TypeSpec. The
            // metadata table itself still identifies the field unambiguously by name once
            // the TypeSpec's generic owner is resolved to its TypeDef.
            return owner.Fields.FirstOrDefault(field =>
                string.Equals(field.Name.String, operand.Name.String, StringComparison.Ordinal));
        }
        catch
        {
            return null;
        }
    }

    private static bool TryResolveLocalFieldAccess(
        ModuleDef module,
        IField operand,
        MethodDef method,
        out ResolvedStaticFieldAccess access)
    {
        access = null!;
        var field = ResolveLocalField(module, operand);
        if (field is null)
            return false;

        var ownerType = ResolveStaticFieldOwnerType(operand, field, method);
        var fieldType = operand.FieldSig?.Type.RemovePinnedAndModifiers();
        if (ownerType is null || fieldType is null)
            return false;

        access = new ResolvedStaticFieldAccess(field, ownerType, fieldType);
        return true;
    }

    private static TypeDef? ResolveLocalTypeDefinition(ITypeDefOrRef type)
    {
        var signature = type.ToTypeSig()?.RemovePinnedAndModifiers();
        if (signature is GenericInstSig generic)
            return generic.GenericType.TypeDefOrRef.ResolveTypeDef();
        return type.ResolveTypeDef();
    }

    private static TypeSig? ResolveStaticFieldOwnerType(
        IField operand,
        FieldDef field,
        MethodDef method)
    {
        if (operand.DeclaringType is TypeSpec typeSpec)
            return typeSpec.TypeSig.RemovePinnedAndModifiers();

        if (operand.DeclaringType is TypeDef typeDefinition &&
            typeDefinition.HasGenericParameters)
        {
            var ownerSig = typeDefinition.IsValueType
                ? (ClassOrValueTypeSig)new ValueTypeSig(typeDefinition)
                : new ClassSig(typeDefinition);
            var closedOwner = new GenericInstSig(
                ownerSig,
                (uint)typeDefinition.GenericParameters.Count);
            for (var index = 0; index < typeDefinition.GenericParameters.Count; index++)
                closedOwner.GenericArguments.Add(new GenericVar(index, typeDefinition));
            return closedOwner;
        }

        return operand.DeclaringType.ToTypeSig()?.RemovePinnedAndModifiers() ??
               field.DeclaringType.ToTypeSig()?.RemovePinnedAndModifiers() ??
               method.DeclaringType.ToTypeSig()?.RemovePinnedAndModifiers();
    }

    private static bool RequiresOwnerAwareStaticSlot(TypeSig ownerType) =>
        ownerType.RemovePinnedAndModifiers() is GenericInstSig;

    private static bool IsUnsupportedFieldPrefix(Instruction instruction) =>
        instruction.OpCode.Code is Code.Volatile or Code.Unaligned or Code.Readonly;

    private static bool DeclaringTypeContainsGenericParameters(TypeDef? type)
    {
        for (var current = type; current != null; current = current.DeclaringType)
        {
            if (current.HasGenericParameters)
                return true;
        }
        return false;
    }

    private static bool IsUnsupportedStaticSlotType(TypeSig type) =>
        type.ElementType is
            ElementType.Void or
            ElementType.ByRef or
            ElementType.Ptr or
            ElementType.FnPtr or
            ElementType.TypedByRef or
            ElementType.End or
            ElementType.Internal or
            ElementType.Sentinel;

    private static void ValidateStaticFieldHandleUses(
        ModuleDef module,
        List<string> issues)
    {
        foreach (var type in module.GetTypes())
        foreach (var method in type.Methods.Where(candidate => candidate.HasBody))
        for (var index = 0; index < method.Body.Instructions.Count; index++)
        {
            var instruction = method.Body.Instructions[index];
            if (instruction.OpCode.Code == Code.Ldtoken &&
                instruction.Operand is IField operand &&
                ResolveLocalField(module, operand) is { } field &&
                field.IsStatic &&
                !field.IsLiteral)
            {
                if (IsCompilerGeneratedImmutableRva(module, field) &&
                    IsInitializeArrayDataHandleUse(method.Body.Instructions, index))
                {
                    continue;
                }

                issues.Add(
                    $"mutable static field handle escapes domain rewriting: " +
                    $"{method.FullName}@IL_{instruction.Offset:X4} -> {field.FullName}");
            }
        }
    }

    private static bool IsCompilerGeneratedImmutableRva(
        ModuleDef module,
        FieldDef field)
    {
        if (!field.HasFieldRVA)
            return false;
        var valueTypeDefinition = (field.FieldSig?.Type.RemovePinnedAndModifiers() as ValueTypeSig)
            ?.TypeDefOrRef.ResolveTypeDef();
        if (!HasPrivateImplementationDetailsOwner(field.DeclaringType) &&
            !HasPrivateImplementationDetailsOwner(valueTypeDefinition))
        {
            return false;
        }

        if (!TryGetFixedSizeRvaDataSize(field, out var dataSize))
            return false;

        var hasSafeUse = false;
        foreach (var type in module.GetTypes())
        foreach (var method in type.Methods.Where(candidate => candidate.HasBody))
        for (var index = 0; index < method.Body.Instructions.Count; index++)
        {
            var instruction = method.Body.Instructions[index];
            if (instruction.Operand is not IField operand ||
                ResolveLocalField(module, operand) is not { } resolved ||
                !IsSameLocalField(resolved, field))
            {
                continue;
            }

            if (instruction.OpCode.Code == Code.Ldsfld && field.IsInitOnly)
            {
                // Compiler-generated RVA values may be read directly instead of being
                // materialized through RuntimeHelpers.InitializeArray. InitOnly plus the
                // private implementation-details owner proves that this is immutable data.
                hasSafeUse = true;
                continue;
            }

            if (instruction.OpCode.Code == Code.Ldsflda &&
                field.IsInitOnly &&
                IsReadOnlySpanAddressUse(method.Body.Instructions, index, dataSize))
            {
                // Roslyn uses this form for ReadOnlySpan constants, including primitive
                // fields and __StaticArrayInitTypeSize=N fields. The address is consumed
                // directly by the readonly span constructor, so it is not domain state.
                hasSafeUse = true;
                continue;
            }

            if (instruction.OpCode.Code != Code.Ldtoken ||
                !IsInitializeArrayDataHandleUse(method.Body.Instructions, index))
            {
                return false;
            }

            // Some compiler/runtime combinations omit initonly on the backing RVA field.
            // The exact InitializeArray handle-use proof is sufficient for that form: the
            // field is never loaded, addressed, or written as a static value by the module.
            hasSafeUse = true;
        }

        return hasSafeUse;
    }

    private static bool IsSameLocalField(FieldDef left, FieldDef right) =>
        ReferenceEquals(left, right) ||
        left.MDToken == right.MDToken ||
        string.Equals(left.FullName, right.FullName, StringComparison.Ordinal);

    private static bool HasPrivateImplementationDetailsOwner(TypeDef? type)
    {
        for (var current = type; current != null; current = current.DeclaringType)
        {
            if (IsPrivateImplementationDetailsName(current.Name.String) ||
                IsPrivateImplementationDetailsName(current.Namespace.String))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPrivateImplementationDetailsName(string? value) =>
        value == "<PrivateImplementationDetails>" ||
        value?.StartsWith("<PrivateImplementationDetails>{", StringComparison.Ordinal) == true;

    private static bool TryGetFixedSizeRvaDataSize(
        FieldDef field,
        out int size)
    {
        var valueType = field.FieldSig?.Type.RemovePinnedAndModifiers() as ValueTypeSig;
        var valueTypeDefinition = valueType?.TypeDefOrRef.ResolveTypeDef();
        if (valueTypeDefinition != null &&
            HasPrivateImplementationDetailsOwner(valueTypeDefinition) &&
            TryGetCompilerGeneratedArraySize(valueTypeDefinition.Name.String, out var arraySize) &&
            arraySize > 0)
        {
            size = arraySize;
            return true;
        }

        if (valueTypeDefinition?.IsExplicitLayout == true &&
            valueTypeDefinition.ClassSize > 0 &&
            valueTypeDefinition.ClassSize <= int.MaxValue)
        {
            size = (int)valueTypeDefinition.ClassSize;
            return true;
        }

        size = field.FieldSig?.Type.RemovePinnedAndModifiers().ElementType switch
        {
            ElementType.Boolean or ElementType.I1 or ElementType.U1 => 1,
            ElementType.Char or ElementType.I2 or ElementType.U2 => 2,
            ElementType.I4 or ElementType.U4 or ElementType.R4 => 4,
            ElementType.I8 or ElementType.U8 or ElementType.R8 => 8,
            ElementType.I or ElementType.U => IntPtr.Size,
            _ => 0
        };
        return size > 0;
    }

    private static bool TryGetCompilerGeneratedArraySize(
        string? typeName,
        out int size)
    {
        const string prefix = "__StaticArrayInitTypeSize=";
        if (typeName?.StartsWith(prefix, StringComparison.Ordinal) == true &&
            int.TryParse(
                typeName[prefix.Length..],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out size))
        {
            return true;
        }

        size = 0;
        return false;
    }

    private static bool IsInitializeArrayDataHandleUse(
        IList<Instruction> instructions,
        int ldtokenIndex)
    {
        var nextIndex = ldtokenIndex + 1;
        while (nextIndex < instructions.Count &&
               instructions[nextIndex].OpCode.Code == Code.Nop)
        {
            nextIndex++;
        }

        if (nextIndex >= instructions.Count ||
            instructions[nextIndex].OpCode.Code != Code.Call ||
            instructions[nextIndex].Operand is not IMethod method ||
            method.Name != "InitializeArray" ||
            method.DeclaringType.FullName != "System.Runtime.CompilerServices.RuntimeHelpers")
        {
            return false;
        }

        var signature = method.MethodSig;
        return signature is
        {
            HasThis: false,
            Params.Count: 2,
            RetType.ElementType: ElementType.Void
        } &&
        signature.Params[0].RemovePinnedAndModifiers().FullName == "System.Array" &&
        signature.Params[1].RemovePinnedAndModifiers().FullName == "System.RuntimeFieldHandle";
    }

    private static bool IsReadOnlySpanAddressUse(
        IList<Instruction> instructions,
        int ldsfldaIndex,
        int dataSize)
    {
        var nextIndex = ldsfldaIndex + 1;
        while (nextIndex < instructions.Count &&
               instructions[nextIndex].OpCode.Code == Code.Nop)
        {
            nextIndex++;
        }

        if (nextIndex >= instructions.Count || !instructions[nextIndex].IsLdcI4())
            return false;

        var length = instructions[nextIndex].GetLdcI4Value();
        if (length < 0 || length > dataSize)
            return false;

        nextIndex++;
        while (nextIndex < instructions.Count &&
               instructions[nextIndex].OpCode.Code == Code.Nop)
        {
            nextIndex++;
        }

        if (nextIndex >= instructions.Count ||
            instructions[nextIndex].OpCode.Code != Code.Newobj ||
            instructions[nextIndex].Operand is not IMethod constructor ||
            constructor.Name != ".ctor" ||
            !constructor.DeclaringType.FullName.StartsWith(
                "System.ReadOnlySpan`1",
                StringComparison.Ordinal))
        {
            return false;
        }

        var signature = constructor.MethodSig;
        if (signature is not
            {
                HasThis: true,
                Params.Count: 2,
                RetType.ElementType: ElementType.Void
            })
        {
            return false;
        }

        var pointer = signature.Params[0].RemovePinnedAndModifiers() as PtrSig;
        return pointer?.Next.RemovePinnedAndModifiers().ElementType == ElementType.Void &&
               signature.Params[1].RemovePinnedAndModifiers().ElementType == ElementType.I4;
    }

    private static void RewriteNativeCallbackGates(
        ModuleDef module,
        BridgeMembers bridgeMembers,
        List<string> issues)
    {
        var hookSites = module.GetTypes()
            .SelectMany(type => type.Methods)
            .Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions
                .Where(IsLegacyHookHelperCall)
                .Select(instruction => (Method: method, Instruction: instruction)))
            .ToArray();
        if (hookSites.Length == 0)
            return;

        var dispatches = new HashSet<MethodDef>();
        foreach (var group in hookSites.GroupBy(site => site.Method))
        {
            var candidates = group.Key.Body!.Instructions
                .Where(instruction => instruction.OpCode.Code == Code.Ldftn)
                .Select(instruction => instruction.Operand)
                .OfType<IMethod>()
                .Select(operand => ResolveLocalMethod(module, operand))
                .Where(dispatch => dispatch is
                {
                    IsStatic: true,
                    HasBody: true,
                    MethodSig.RetType: not ByRefSig
                })
                .Cast<MethodDef>()
                .Distinct()
                .ToArray();
            var hookCount = group.Count();
            if (candidates.Length != hookCount)
            {
                issues.Add(
                    $"Native MOD callback mapping is ambiguous in {group.Key.FullName}: " +
                    $"HookHelper.Hook calls={hookCount} static ldftn callbacks={candidates.Length}");
                continue;
            }
            dispatches.UnionWith(candidates);
        }
        if (issues.Count != 0)
            return;

        var importer = new Importer(module, ImporterOptions.TryToUseTypeDefs);
        var stateType = GetOrCreateCallbackStateType(
            module,
            bridgeMembers.CaptureRuntimeCallbackGate.MethodSig.RetType);
        var gateField = stateType.Fields.Single(field =>
            field.Name == "Gate" && field.IsStatic);
        var importedGateField = importer.Import(gateField);

        foreach (var (method, hookCall) in hookSites)
        {
            var index = method.Body.Instructions.IndexOf(hookCall);
            var captureGate = Instruction.Create(
                OpCodes.Call,
                bridgeMembers.CaptureRuntimeCallbackGate);
            InsertBeforeWithRetargeting(method.Body, hookCall, captureGate);
            method.Body.Instructions.Insert(
                index + 1,
                Instruction.Create(OpCodes.Stsfld, importedGateField));
            method.Body.Instructions.Insert(
                index + 2,
                Instruction.Create(OpCodes.Ldsfld, importedGateField));
            hookCall.OpCode = OpCodes.Call;
            hookCall.Operand = bridgeMembers.HookRuntimeGatedRequired;
        }

        foreach (var dispatch in dispatches)
            WrapNativeCallback(module, dispatch, importedGateField, bridgeMembers, importer);
    }

    private static TypeDef GetOrCreateCallbackStateType(
        ModuleDef module,
        TypeSig callbackGateType)
    {
        const string typeName = "__StArrayNativeCallbackState_v1";
        var existing = module.Types.FirstOrDefault(type =>
            type.Namespace.String.Length == 0 && type.Name == typeName);
        if (existing != null)
            return existing;

        var type = new TypeDefUser(
            string.Empty,
            typeName,
            module.CorLibTypes.Object.TypeDefOrRef)
        {
            Attributes = TypeAttributes.NotPublic |
                         TypeAttributes.Abstract |
                         TypeAttributes.Sealed |
                         TypeAttributes.BeforeFieldInit
        };
        type.Fields.Add(new FieldDefUser(
            "Gate",
            new FieldSig(callbackGateType),
            FieldAttributes.Assembly | FieldAttributes.Static));
        module.Types.Add(type);
        return type;
    }

    private static bool IsLegacyHookHelperCall(Instruction instruction)
    {
        if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt) ||
            instruction.Operand is not IMethod method)
        {
            return false;
        }
        return method.DeclaringType.FullName == "StArray.ModManager.Runtime.HookHelper" &&
               method.Name == "Hook" &&
               method.MethodSig is { HasThis: false, Params.Count: 2 };
    }

    private static void WrapNativeCallback(
        ModuleDef module,
        MethodDef callback,
        IField gateField,
        BridgeMembers bridgeMembers,
        Importer importer)
    {
        var declaringType = callback.DeclaringType!;
        var originalName = callback.Name;
        var bodyName = "__starray_callback_body_" + originalName;
        for (var suffix = 1; declaringType.Methods.Any(method => method.Name == bodyName); suffix++)
            bodyName = "__starray_callback_body_" + originalName + "_" + suffix;

        callback.Name = bodyName;
        var wrapper = new MethodDefUser(
            originalName,
            callback.MethodSig,
            callback.ImplAttributes,
            callback.Attributes)
        {
            Body = new CilBody
            {
                InitLocals = true
            }
        };
        foreach (var attribute in callback.CustomAttributes.ToArray())
            wrapper.CustomAttributes.Add(attribute);
        callback.CustomAttributes.Clear();
        declaringType.Methods.Add(wrapper);

        foreach (var method in module.GetTypes().SelectMany(type => type.Methods))
        foreach (var instruction in method.HasBody
                     ? method.Body.Instructions
                     : Array.Empty<Instruction>())
        {
            if (instruction.Operand is IMethod operand &&
                ResolveLocalMethod(module, operand) is { } resolved &&
                ReferenceEquals(resolved, callback))
            {
                instruction.Operand = wrapper;
            }
        }

        var body = wrapper.Body!;
        var returnType = callback.MethodSig.RetType.RemovePinnedAndModifiers();
        var isVoid = returnType.ElementType == ElementType.Void;
        Local? result = null;
        if (!isVoid)
        {
            result = new Local(importer.Import(returnType));
            body.Variables.Add(result);
        }
        var disposableType = importer.Import(typeof(IDisposable)).ToTypeSig();
        var lease = new Local(disposableType);
        body.Variables.Add(lease);
        var exception = new Local(importer.Import(typeof(Exception)).ToTypeSig());
        body.Variables.Add(exception);

        var fallback = Instruction.Create(OpCodes.Nop);
        var tryStart = Instruction.Create(OpCodes.Nop);
        var callBody = Instruction.Create(
            OpCodes.Call,
            callback);
        var catchStart = Instruction.Create(OpCodes.Stloc, exception);
        var disposeStart = Instruction.Create(OpCodes.Ldloc, lease);
        var reportDone = Instruction.Create(OpCodes.Leave, disposeStart);
        var returnResult = Instruction.Create(OpCodes.Nop);

        body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, gateField));
        body.Instructions.Add(Instruction.Create(OpCodes.Brfalse, fallback));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, gateField));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldloca, lease));
        body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, bridgeMembers.CallbackGateTryEnter));
        body.Instructions.Add(Instruction.Create(OpCodes.Brfalse, fallback));
        body.Instructions.Add(tryStart);
        foreach (var parameter in wrapper.Parameters.Where(parameter =>
                     parameter.IsNormalMethodParameter))
        {
            body.Instructions.Add(LoadArgument(parameter));
        }
        body.Instructions.Add(callBody);
        if (result != null)
            body.Instructions.Add(Instruction.Create(OpCodes.Stloc, result));
        body.Instructions.Add(Instruction.Create(OpCodes.Leave, disposeStart));
        body.Instructions.Add(catchStart);
        body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, gateField));
        body.Instructions.Add(Instruction.Create(OpCodes.Brfalse, reportDone));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, gateField));
        body.Instructions.Add(Instruction.Create(
            OpCodes.Ldstr,
            $"{declaringType.FullName}.{originalName}"));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, exception));
        body.Instructions.Add(Instruction.Create(
            OpCodes.Callvirt,
            bridgeMembers.CallbackGateReportFailure));
        body.Instructions.Add(reportDone);
        body.Instructions.Add(disposeStart);
        body.Instructions.Add(Instruction.Create(OpCodes.Brfalse, returnResult));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lease));
        body.Instructions.Add(Instruction.Create(
            OpCodes.Callvirt,
            importer.Import(typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose))!)));
        body.Instructions.Add(returnResult);
        if (result != null)
            body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, result));
        body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        body.Instructions.Add(fallback);
        if (result != null)
            body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, result));
        body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        var exceptionType = importer.Import(typeof(Exception));
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = exceptionType,
            TryStart = tryStart,
            TryEnd = catchStart,
            HandlerStart = catchStart,
            HandlerEnd = disposeStart
        });
    }

    private static MethodDef? ResolveLocalMethod(ModuleDef module, IMethod operand)
    {
        if (operand is MethodDef definition && ReferenceEquals(definition.Module, module))
            return definition;
        return null;
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
                for (var index = 0; index < targets.Count; index++)
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
            throw new InvalidOperationException("Native Hook callsite is not in its method body.");
        body.Instructions.Insert(targetIndex, inserted);
    }

    private static Instruction LoadArgument(Parameter parameter) => parameter.Index switch
    {
        0 => Instruction.Create(OpCodes.Ldarg_0),
        1 => Instruction.Create(OpCodes.Ldarg_1),
        2 => Instruction.Create(OpCodes.Ldarg_2),
        3 => Instruction.Create(OpCodes.Ldarg_3),
        <= byte.MaxValue => Instruction.Create(OpCodes.Ldarg_S, parameter),
        _ => Instruction.Create(OpCodes.Ldarg, parameter)
    };

    private static IReadOnlyDictionary<TypeDef, StaticInitializerPlan> MoveStaticInitializers(
        ModuleDef module,
        IReadOnlyList<StaticFieldAccessPlan> accessPlans,
        IMethod requireCurrentDomain)
    {
        var result = new Dictionary<TypeDef, StaticInitializerPlan>();
        foreach (var type in accessPlans
                     .Select(plan => plan.FieldPlan.Field.DeclaringType)
                     .Where(type => type != null)
                     .Distinct()!)
        {
            var constructor = type.FindStaticConstructor();
            if (constructor is not { HasBody: true })
                continue;

            var typeId = ComputeStableId(
                $"type|{module.Mvid:D}|{module.Assembly?.Name}|{type.FullName}");
            var initializerName = $"__starray_domain_cctor_{typeId:X8}";
            for (var suffix = 1; type.Methods.Any(method => method.Name == initializerName); suffix++)
                initializerName = $"__starray_domain_cctor_{typeId:X8}_{suffix}";

            var initializer = new MethodDefUser(
                initializerName,
                MethodSig.CreateStatic(module.CorLibTypes.Void),
                constructor.ImplAttributes,
                dnlib.DotNet.MethodAttributes.Private |
                dnlib.DotNet.MethodAttributes.Static |
                dnlib.DotNet.MethodAttributes.HideBySig)
            {
                Body = constructor.Body
            };
            constructor.Body = new CilBody();
            constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(initializer);
            initializer.Body.Instructions.Insert(
                0,
                Instruction.Create(OpCodes.Call, requireCurrentDomain));
            result.Add(type, new StaticInitializerPlan(
                type,
                typeId,
                constructor,
                initializer,
                !type.IsBeforeFieldInit));
        }
        return result;
    }

    private static void InjectExplicitTypeInitialization(
        IReadOnlyDictionary<TypeDef, StaticInitializerPlan> initializerPlans,
        IMethod ensureMethod)
    {
        foreach (var plan in initializerPlans.Values.Where(plan => plan.RequireMethodEntryGuard))
        foreach (var method in plan.Type.Methods.Where(method =>
                     method.HasBody &&
                     !ReferenceEquals(method, plan.ClrConstructor) &&
                     !ReferenceEquals(method, plan.Initializer)))
        {
            var sequence = CreateEnsureSequence(
                plan,
                plan.Type.ToTypeSig(),
                ensureMethod);
            for (var index = sequence.Count - 1; index >= 0; index--)
                method.Body.Instructions.Insert(0, sequence[index]);
        }
    }

    private static int RewriteStaticFieldAccesses(
        ModuleDef module,
        IReadOnlyList<StaticFieldAccessPlan> accessPlans,
        IReadOnlyDictionary<TypeDef, StaticInitializerPlan> initializerPlans,
        BridgeMembers bridgeMembers)
    {
        var importer = new Importer(module, ImporterOptions.TryToUseTypeDefs);
        var rewritten = 0;
        foreach (var plan in accessPlans)
        {
            var replacement = new List<Instruction>();
            var fieldType = importer.Import(plan.FieldType);
            var ownerType = importer.Import(plan.OwnerType);
            var initializer = initializerPlans.GetValueOrDefault(plan.FieldPlan.Field.DeclaringType!);
            var method = initializerPlans.TryGetValue(
                             plan.Method.DeclaringType,
                             out var ownerInitializer) &&
                         ReferenceEquals(plan.Method, ownerInitializer.ClrConstructor)
                ? ownerInitializer.Initializer
                : plan.Method;
            var needsEnsure = initializer != null &&
                              !ReferenceEquals(method, initializer.Initializer);

            switch (plan.Instruction.OpCode.Code)
            {
                case Code.Ldsfld:
                    if (needsEnsure)
                        replacement.AddRange(CreateEnsureSequence(
                            initializer!,
                            ownerType,
                            bridgeMembers.EnsureStaticTypeInitialized));
                    replacement.Add(LoadInt32(plan.FieldPlan.Slot.StaticSlotId));
                    replacement.Add(Instruction.Create(
                        OpCodes.Call,
                        CloseGenericMethod(
                            plan.UseOwnerAwareSlot
                                ? bridgeMembers.GetStaticSlotForOwner
                                : bridgeMembers.GetStaticSlot,
                            plan.UseOwnerAwareSlot
                                ? new[] { fieldType, importer.Import(plan.OwnerType) }
                                : new[] { fieldType })));
                    break;

                case Code.Ldsflda:
                    if (needsEnsure)
                        replacement.AddRange(CreateEnsureSequence(
                            initializer!,
                            ownerType,
                            bridgeMembers.EnsureStaticTypeInitialized));
                    replacement.Add(LoadInt32(plan.FieldPlan.Slot.StaticSlotId));
                    replacement.Add(Instruction.Create(
                        OpCodes.Call,
                        CloseGenericMethod(
                            plan.UseOwnerAwareSlot
                                ? bridgeMembers.GetStaticSlotReferenceForOwner
                                : bridgeMembers.GetStaticSlotReference,
                            plan.UseOwnerAwareSlot
                                ? new[] { fieldType, importer.Import(plan.OwnerType) }
                                : new[] { fieldType })));
                    break;

                case Code.Stsfld:
                    var temporary = new Local(fieldType);
                    method.Body.Variables.Add(temporary);
                    method.Body.InitLocals = true;
                    replacement.Add(Instruction.Create(OpCodes.Stloc, temporary));
                    if (needsEnsure)
                        replacement.AddRange(CreateEnsureSequence(
                            initializer!,
                            ownerType,
                            bridgeMembers.EnsureStaticTypeInitialized));
                    replacement.Add(LoadInt32(plan.FieldPlan.Slot.StaticSlotId));
                    replacement.Add(Instruction.Create(OpCodes.Ldloc, temporary));
                    replacement.Add(Instruction.Create(
                        OpCodes.Call,
                        CloseGenericMethod(
                            plan.UseOwnerAwareSlot
                                ? bridgeMembers.SetStaticSlotForOwner
                                : bridgeMembers.SetStaticSlot,
                            plan.UseOwnerAwareSlot
                                ? new[] { fieldType, importer.Import(plan.OwnerType) }
                                : new[] { fieldType })));
                    break;
            }

            ReplaceInstruction(method.Body.Instructions, plan.Instruction, replacement);
            rewritten++;
        }
        return rewritten;
    }

    private static IReadOnlyList<Instruction> CreateEnsureSequence(
        StaticInitializerPlan plan,
        TypeSig ownerType,
        IMethod ensureMethod) =>
    [
        LoadInt32(plan.TypeId),
        Instruction.Create(OpCodes.Ldtoken, plan.Initializer),
        Instruction.Create(OpCodes.Ldtoken, ownerType.ToTypeDefOrRef()),
        Instruction.Create(OpCodes.Call, ensureMethod)
    ];

    private static IMethod CloseGenericMethod(IMethod method, TypeSig type) =>
        CloseGenericMethod(method, new[] { type });

    private static IMethod CloseGenericMethod(
        IMethod method,
        IReadOnlyList<TypeSig> types) =>
        new MethodSpecUser(
            method as IMethodDefOrRef
            ?? throw new InvalidOperationException(
                $"Domain bridge method is not a method definition/reference: {method.FullName}."),
            new GenericInstMethodSig(types.ToArray()));

    private static Instruction LoadInt32(int value) => value switch
    {
        -1 => Instruction.Create(OpCodes.Ldc_I4_M1),
        0 => Instruction.Create(OpCodes.Ldc_I4_0),
        1 => Instruction.Create(OpCodes.Ldc_I4_1),
        2 => Instruction.Create(OpCodes.Ldc_I4_2),
        3 => Instruction.Create(OpCodes.Ldc_I4_3),
        4 => Instruction.Create(OpCodes.Ldc_I4_4),
        5 => Instruction.Create(OpCodes.Ldc_I4_5),
        6 => Instruction.Create(OpCodes.Ldc_I4_6),
        7 => Instruction.Create(OpCodes.Ldc_I4_7),
        8 => Instruction.Create(OpCodes.Ldc_I4_8),
        >= sbyte.MinValue and <= sbyte.MaxValue =>
            Instruction.Create(OpCodes.Ldc_I4_S, (sbyte)value),
        _ => Instruction.Create(OpCodes.Ldc_I4, value)
    };

    private static void ReplaceInstruction(
        IList<Instruction> instructions,
        Instruction original,
        IReadOnlyList<Instruction> replacement)
    {
        if (replacement.Count == 0)
            throw new InvalidOperationException("Static field replacement cannot be empty.");
        var index = instructions.IndexOf(original);
        if (index < 0)
            throw new InvalidOperationException("Static field instruction disappeared during rewriting.");
        original.OpCode = replacement[0].OpCode;
        original.Operand = replacement[0].Operand;
        for (var offset = 1; offset < replacement.Count; offset++)
            instructions.Insert(index + offset, replacement[offset]);
    }

    private static int RewriteAssemblyLocations(ModuleDef module, IMethod importedBridge)
    {
        var rewritten = 0;
        foreach (var type in module.GetTypes())
        foreach (var method in type.Methods.Where(candidate => candidate.HasBody))
        foreach (var instruction in method.Body.Instructions)
        {
            if (!IsAssemblyLocationGetter(instruction))
                continue;
            instruction.OpCode = OpCodes.Call;
            instruction.Operand = importedBridge;
            rewritten++;
        }
        return rewritten;
    }

    private static int RewriteCallSites(
        IReadOnlyList<CallRewritePlan> rewrites)
    {
        foreach (var rewrite in rewrites)
        {
            rewrite.Instruction.OpCode = OpCodes.Call;
            rewrite.Instruction.Operand = rewrite.Replacement;
        }
        return rewrites.Count;
    }

    private static int RewriteTaskReturnMethods(
        ModuleDef module,
        IReadOnlyList<TaskReturnRewritePlan> plans,
        BridgeMembers bridgeMembers)
    {
        var importer = new Importer(module, ImporterOptions.TryToUseTypeDefs);
        foreach (var plan in plans)
        {
            plan.Method.Body.Instructions.Insert(
                0,
                Instruction.Create(
                    OpCodes.Call,
                    bridgeMembers.AsyncMethods["RequireCurrentScope"]));
            var trackingMethod = plan.ResultType == null
                ? bridgeMembers.AsyncMethods["TrackTask"]
                : CloseGenericMethod(
                    bridgeMembers.AsyncMethods["TrackTaskOfT"],
                    importer.Import(plan.ResultType));
            var operationName = "task-return:" + plan.Method.FullName;
            foreach (var instruction in plan.Method.Body.Instructions
                         .Where(candidate => candidate.OpCode.Code == Code.Ret)
                         .ToArray())
            {
                var index = plan.Method.Body.Instructions.IndexOf(instruction);
                instruction.OpCode = OpCodes.Ldstr;
                instruction.Operand = operationName;
                plan.Method.Body.Instructions.Insert(
                    index + 1,
                    Instruction.Create(OpCodes.Call, trackingMethod));
                plan.Method.Body.Instructions.Insert(
                    index + 2,
                    Instruction.Create(OpCodes.Ret));
            }
        }
        return plans.Count;
    }

    private static void ValidateResiduals(
        ModuleDef module,
        IReadOnlyList<StaticFieldAccessPlan> accessPlans,
        AsyncIsolationPlan asyncPlan,
        List<string> issues)
    {
        var fields = accessPlans.Select(plan => plan.FieldPlan.Field).ToHashSet();
        foreach (var type in module.GetTypes())
        foreach (var method in type.Methods.Where(candidate => candidate.HasBody))
        foreach (var instruction in method.Body.Instructions)
        {
            if (IsAssemblyLocationGetter(instruction))
            {
                issues.Add(
                    $"Assembly.Location rewrite left a residual call at " +
                    $"{method.FullName}@IL_{instruction.Offset:X4}");
            }
            if (instruction.OpCode.Code is (Code.Ldsfld or Code.Stsfld or Code.Ldsflda) &&
                instruction.Operand is IField operand &&
                ResolveLocalField(module, operand) is { } field &&
                fields.Contains(field))
            {
                issues.Add(
                    $"static field rewrite left a residual access at " +
                    $"{method.FullName}@IL_{instruction.Offset:X4} -> {field.FullName}");
            }
        }

        foreach (var rewrite in asyncPlan.CallRewrites)
        {
            if (rewrite.Instruction.OpCode.Code != Code.Call ||
                !ReferenceEquals(rewrite.Instruction.Operand, rewrite.Replacement))
            {
                issues.Add(
                    $"async lifecycle rewrite left a residual call at " +
                    $"{rewrite.Method.FullName}@IL_{rewrite.Instruction.Offset:X4}");
            }
        }
        foreach (var plan in asyncPlan.TaskReturnMethods)
        {
            if (plan.Method.Body.Instructions.Any(instruction =>
                    instruction.OpCode.Code == Code.Ret &&
                    (plan.Method.Body.Instructions.IndexOf(instruction) == 0 ||
                     plan.Method.Body.Instructions[
                         plan.Method.Body.Instructions.IndexOf(instruction) - 1].Operand is not IMethod called ||
                     called.DeclaringType.FullName !=
                     "StArray.ModManager.Runtime.ModRuntimeAsyncBridge")))
            {
                issues.Add($"Task return tracking is incomplete for {plan.Method.FullName}");
            }
        }
    }

    private static string BuildFieldIdentity(ModuleDef module, FieldDef field) =>
        $"{module.Assembly?.Name}|{module.Mvid:D}|{field.DeclaringType.FullName}::" +
        $"{field.Name}:{field.FieldSig?.Type.RemovePinnedAndModifiers().FullName}";

    private static int ComputeStableId(string identity)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return BinaryPrimitives.ReadInt32LittleEndian(hash) & int.MaxValue;
    }

    private static bool IsAssemblyLocationGetter(Instruction instruction)
    {
        if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt) ||
            instruction.Operand is not IMethod method ||
            method.Name != "get_Location" ||
            method.DeclaringType.FullName != "System.Reflection.Assembly")
        {
            return false;
        }
        return method.MethodSig is { HasThis: true, Params.Count: 0 } signature &&
               signature.RetType.FullName == "System.String";
    }

    private sealed record BridgeMembers(
        IMethod AssemblyLocation,
        IMethod GetStaticSlot,
        IMethod SetStaticSlot,
        IMethod GetStaticSlotReference,
        IMethod GetStaticSlotForOwner,
        IMethod SetStaticSlotForOwner,
        IMethod GetStaticSlotReferenceForOwner,
        IMethod EnsureStaticTypeInitialized,
        IMethod RequireCurrentDomain,
        IMethod CaptureRuntimeCallbackGate,
        IMethod HookRuntimeGatedRequired,
        IMethod CallbackGateTryEnter,
        IMethod CallbackGateReportFailure,
        IReadOnlyDictionary<string, IMethod> AsyncMethods,
        IReadOnlyDictionary<string, IMethod> FileMethods,
        IReadOnlyDictionary<string, IMethod> NetworkMethods);

    private sealed record FileIsolationPlan(
        IReadOnlyList<CallRewritePlan> CallRewrites,
        IReadOnlyList<NativeModFileRewriteRecord> Proofs);

    private sealed record NetworkIsolationPlan(
        IReadOnlyList<CallRewritePlan> CallRewrites,
        IReadOnlyList<NativeModNetworkRewriteRecord> Proofs);

    private sealed record TaskReturnRewritePlan(
        MethodDef Method,
        TypeSig? ResultType);

    private sealed record CallRewritePlan(
        MethodDef Method,
        Instruction Instruction,
        IMethod Replacement,
        string Kind);

    private sealed record AsyncIsolationPlan(
        IReadOnlyList<TaskReturnRewritePlan> TaskReturnMethods,
        IReadOnlyList<CallRewritePlan> CallRewrites,
        IReadOnlyList<NativeModAsyncRewriteRecord> Proofs);

    private sealed record StaticFieldPlan(
        FieldDef Field,
        NativeModStaticSlotRewriteRecord Slot);

    private sealed record ResolvedStaticFieldAccess(
        FieldDef Field,
        TypeSig OwnerType,
        TypeSig FieldType);

    private sealed record StaticFieldAccessPlan(
        MethodDef Method,
        Instruction Instruction,
        StaticFieldPlan FieldPlan,
        TypeSig FieldType,
        TypeSig OwnerType,
        bool UseOwnerAwareSlot);

    private sealed record StaticInitializerPlan(
        TypeDef Type,
        int TypeId,
        MethodDef ClrConstructor,
        MethodDef Initializer,
        bool RequireMethodEntryGuard);
}

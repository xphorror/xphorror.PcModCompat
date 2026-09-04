using System.Reflection;
using System.Runtime.Loader;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using StArray.ModManager.Hooks;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class NativeModShadowRewriteTests
{
    [Test]
    public void AssemblyLocationCallsAreRewrittenToDomainBridge()
    {
        var root = NewRoot();
        try
        {
            var input = Path.Combine(root, "LocationFixture.dll");
            var output = Path.Combine(root, "LocationFixture.shadow.dll");
            CreateLocationFixture(input);

            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            using var rewritten = ModuleDefMD.Load(output);
            var calls = rewritten.GetTypes()
                .SelectMany(type => type.Methods)
                .Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions)
                .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
                .Select(instruction => (IMethod)instruction.Operand)
                .ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(report.Issues, Is.Empty);
                Assert.That(report.RewrittenAssemblyLocationCalls, Is.EqualTo(1));
                Assert.That(calls.Any(method =>
                    method.DeclaringType.FullName == typeof(NativeModPathBridge).FullName &&
                    method.Name == nameof(NativeModPathBridge.GetAssemblyLocation)), Is.True);
                Assert.That(calls.Any(method =>
                    method.DeclaringType.FullName == "System.Reflection.Assembly" &&
                    method.Name == "get_Location"), Is.False);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void StaticFieldsAreRewrittenAndInitializedOncePerDomain()
    {
        var root = NewRoot();
        try
        {
            var input = Path.Combine(root, "StaticFixture.dll");
            var output = Path.Combine(root, "shadow", "StaticFixture.dll");
            CreateStaticFixture(input);

            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            using (var rewritten = ModuleDefMD.Load(output))
            {
                var instructions = rewritten.GetTypes()
                    .SelectMany(type => type.Methods)
                    .Where(method => method.HasBody)
                    .SelectMany(method => method.Body.Instructions)
                    .ToArray();
                var calls = instructions
                    .Where(instruction => instruction.OpCode.Code == Code.Call)
                    .Select(instruction => instruction.Operand)
                    .OfType<IMethod>()
                    .ToArray();
                Assert.Multiple(() =>
                {
                    Assert.That(report.Issues, Is.Empty);
                    Assert.That(report.RewrittenStaticFieldInstructions, Is.EqualTo(4));
                    Assert.That(report.StaticSlots, Has.Count.EqualTo(1));
                    Assert.That(instructions.Any(instruction =>
                        instruction.OpCode.Code is Code.Ldsfld or Code.Stsfld or Code.Ldsflda),
                        Is.False);
                    Assert.That(calls.Any(method =>
                        method.Name == nameof(ModDataDomainRuntime.GetStaticSlot)), Is.True);
                    Assert.That(calls.Any(method =>
                        method.Name == nameof(ModDataDomainRuntime.SetStaticSlot)), Is.True);
                    Assert.That(calls.Any(method =>
                        method.Name == nameof(ModDataDomainRuntime.GetStaticSlotReference)), Is.True);
                    Assert.That(calls.Any(method =>
                        method.Name == nameof(ModDataDomainRuntime.EnsureStaticTypeInitialized)), Is.True);
                });
            }

            RunStaticDomainScenario(output);
        }
        finally
        {
            CollectContexts();
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ClosedGenericStaticFieldMemberRefsUseOwnerAwareBridgeSlots()
    {
        var root = NewRoot();
        try
        {
            var input = Path.Combine(root, "GenericStaticFixture.dll");
            var output = Path.Combine(root, "shadow", "GenericStaticFixture.dll");
            CreateClosedGenericStaticFixture(input);

            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            using (var rewritten = ModuleDefMD.Load(output))
            {
                var calls = rewritten.GetTypes()
                    .SelectMany(type => type.Methods)
                    .Where(method => method.HasBody)
                    .SelectMany(method => method.Body.Instructions)
                    .Where(instruction => instruction.OpCode.Code == Code.Call)
                    .Select(instruction => instruction.Operand)
                    .OfType<IMethod>()
                    .Where(method => method.DeclaringType.FullName ==
                        typeof(ModDataDomainRuntime).FullName)
                    .ToArray();
                var ownerCalls = calls
                    .Where(method => method.Name.String is
                        nameof(ModDataDomainRuntime.GetStaticSlotForOwner) or
                        nameof(ModDataDomainRuntime.SetStaticSlotForOwner) or
                        nameof(ModDataDomainRuntime.GetStaticSlotReferenceForOwner))
                    .ToArray();
                var ensureCalls = calls
                    .Where(method => method.Name == nameof(ModDataDomainRuntime.EnsureStaticTypeInitialized))
                    .ToArray();

                Assert.Multiple(() =>
                {
                    Assert.That(report.Issues, Is.Empty, string.Join(" | ", report.Issues));
                    Assert.That(report.RewrittenStaticFieldInstructions, Is.EqualTo(6));
                    Assert.That(report.StaticSlots, Has.Count.EqualTo(1));
                    Assert.That(ownerCalls, Has.Length.EqualTo(6));
                    Assert.That(
                        ownerCalls.Count(method =>
                            method.Name == nameof(ModDataDomainRuntime.GetStaticSlotForOwner)),
                        Is.EqualTo(2));
                    Assert.That(
                        ownerCalls.Count(method =>
                            method.Name == nameof(ModDataDomainRuntime.SetStaticSlotForOwner)),
                        Is.EqualTo(3));
                    Assert.That(
                        ownerCalls.Count(method =>
                            method.Name == nameof(ModDataDomainRuntime.GetStaticSlotReferenceForOwner)),
                        Is.EqualTo(1));
                    Assert.That(
                        ownerCalls.All(method =>
                            method is MethodSpec spec &&
                            spec.GenericInstMethodSig.GenericArguments.Count == 2),
                        Is.True);
                    Assert.That(ensureCalls, Is.Not.Empty);
                    Assert.That(
                        ensureCalls.All(method =>
                            method.MethodSig?.Params.Count == 3 &&
                            method.MethodSig.Params[2].FullName == "System.RuntimeTypeHandle"),
                        Is.True);
                    Assert.That(
                        rewritten.GetTypes()
                            .SelectMany(type => type.Methods)
                            .Where(method => method.HasBody)
                            .SelectMany(method => method.Body.Instructions)
                            .Any(instruction => instruction.OpCode.Code is
                                Code.Ldsfld or Code.Stsfld or Code.Ldsflda),
                        Is.False);
                });
            }

            RunClosedGenericStaticDomainScenario(output);
        }
        finally
        {
            CollectContexts();
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CompilerGeneratedRvaDataHandlesAreNotTreatedAsDomainState()
    {
        var root = NewRoot();
        try
        {
            var input = Path.Combine(root, "CompilerGeneratedRvaFixture.dll");
            var output = Path.Combine(root, "shadow", "CompilerGeneratedRvaFixture.dll");
            CreateCompilerGeneratedRvaFixture(input);

            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            Assert.Multiple(() =>
            {
                Assert.That(report.Issues, Is.Empty, string.Join(" | ", report.Issues));
                Assert.That(File.Exists(output), Is.True);
                Assert.That(report.RewrittenStaticFieldInstructions, Is.GreaterThan(0));
                Assert.That(report.StaticSlots, Has.Count.EqualTo(1));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CompilerGeneratedRvaInitializeArrayMayOmitInitOnlyFlag()
    {
        var root = NewRoot();
        try
        {
            var input = Path.Combine(root, "CompilerGeneratedRvaWithoutInitOnlyFixture.dll");
            var output = Path.Combine(root, "shadow", "CompilerGeneratedRvaWithoutInitOnlyFixture.dll");
            CreateCompilerGeneratedRvaFixture(input, dataFieldInitOnly: false);

            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            Assert.Multiple(() =>
            {
                Assert.That(report.Issues, Is.Empty, string.Join(" | ", report.Issues));
                Assert.That(File.Exists(output), Is.True);
                Assert.That(report.StaticSlots, Has.Count.EqualTo(1));
                Assert.That(
                    report.StaticSlots.Any(slot =>
                        slot.MemberIdentity.Contains("::3:", StringComparison.Ordinal)),
                    Is.False,
                    "The RVA data field must not be converted into a mutable domain slot.");
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CompilerGeneratedRvaHandlesStillRejectNonInitializeArrayUse()
    {
        var root = NewRoot();
        try
        {
            var input = Path.Combine(root, "UnsafeCompilerGeneratedRvaFixture.dll");
            var output = Path.Combine(root, "shadow", "UnsafeCompilerGeneratedRvaFixture.dll");
            CreateCompilerGeneratedRvaFixture(input, includeUnsafeHandle: true);

            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            Assert.Multiple(() =>
            {
                Assert.That(report.Issues, Has.Some.Contains(
                    "mutable static field handle escapes domain rewriting"));
                Assert.That(File.Exists(output), Is.False);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CompilerGeneratedRvaReadOnlySpansAreNotTreatedAsDomainState()
    {
        var root = NewRoot();
        try
        {
            var input = Path.Combine(root, "CompilerGeneratedRvaReadOnlySpanFixture.dll");
            var output = Path.Combine(root, "shadow", "CompilerGeneratedRvaReadOnlySpanFixture.dll");
            CreateCompilerGeneratedRvaFixture(input, includeReadOnlySpan: true);

            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            Assert.Multiple(() =>
            {
                Assert.That(report.Issues, Is.Empty, string.Join(" | ", report.Issues));
                Assert.That(File.Exists(output), Is.True);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void MutableCompilerGeneratedRvaReadOnlySpansAreRejected()
    {
        var root = NewRoot();
        try
        {
            var input = Path.Combine(root, "MutableCompilerGeneratedRvaReadOnlySpanFixture.dll");
            var output = Path.Combine(root, "shadow", "MutableCompilerGeneratedRvaReadOnlySpanFixture.dll");
            CreateCompilerGeneratedRvaFixture(
                input,
                includeReadOnlySpan: true,
                dataFieldInitOnly: false);

            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            Assert.Multiple(() =>
            {
                Assert.That(report.Issues, Has.Some.Contains(
                    "RVA-backed mutable static field is not supported"));
                Assert.That(File.Exists(output), Is.False);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void TaskRunAndTaskReturnsAreGenerationBound()
    {
        var root = NewRoot();
        var input = Path.Combine(root, "AsyncFixture.dll");
        var output = Path.Combine(root, "shadow", "AsyncFixture.dll");
        CreateAsyncFixture(input);
        try
        {
            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);
            Assert.Multiple(() =>
            {
                Assert.That(report.Issues, Is.Empty);
                Assert.That(report.RewrittenAsyncCalls, Is.EqualTo(1));
                Assert.That(report.TrackedTaskReturnMethods, Is.EqualTo(2));
                Assert.That(report.AsyncRewrites.Select(proof => proof.Kind),
                    Is.EquivalentTo(new[] { "task-return", "task-return", "task-run" }));
            });

            using var rewritten = ModuleDefMD.Load(output);
            var bridgeCalls = rewritten.GetTypes()
                .SelectMany(type => type.Methods)
                .Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions)
                .Where(instruction => instruction.OpCode.Code == Code.Call)
                .Select(instruction => instruction.Operand)
                .OfType<IMethod>()
                .Where(method => method.DeclaringType.FullName ==
                    typeof(ModRuntimeAsyncBridge).FullName)
                .Select(method => method.Name.String)
                .ToArray();
            Assert.That(bridgeCalls, Does.Contain(nameof(ModRuntimeAsyncBridge.RequireCurrentScope)));
            Assert.That(bridgeCalls, Does.Contain(nameof(ModRuntimeAsyncBridge.RunAction)));
            Assert.That(bridgeCalls, Does.Contain(nameof(ModRuntimeAsyncBridge.TrackTask)));
            Assert.That(bridgeCalls, Does.Contain(nameof(ModRuntimeAsyncBridge.TrackTaskOfT)));

            RunAsyncDomainScenario(output);
        }
        finally
        {
            CollectContexts();
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ParallelForCallsAreGenerationBound()
    {
        var root = NewRoot();
        try
        {
            var input = Path.Combine(root, "ParallelFixture.dll");
            var output = Path.Combine(root, "ParallelFixture.shadow.dll");
            CreateFileFixture(input, EmitParallelCalls);

            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            using var rewritten = ModuleDefMD.Load(output);
            var called = rewritten.GetTypes()
                .SelectMany(type => type.Methods)
                .Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions)
                .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
                .Select(instruction => instruction.Operand)
                .OfType<IMethod>()
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(report.Issues, Is.Empty, string.Join(" | ", report.Issues));
                Assert.That(report.RewrittenAsyncCalls, Is.EqualTo(4));
                Assert.That(report.AsyncRewrites, Has.Count.EqualTo(2));
                Assert.That(report.AsyncRewrites.Single(proof => proof.Kind == "parallel-for")
                    .RewriteCount, Is.EqualTo(2));
                Assert.That(report.AsyncRewrites.Single(proof => proof.Kind == "parallel-foreach")
                    .RewriteCount, Is.EqualTo(2));
                Assert.That(
                    called.Count(method =>
                        method.DeclaringType.FullName == typeof(ModRuntimeAsyncBridge).FullName),
                    Is.EqualTo(4));
                Assert.That(
                    called.Select(method => method.Name.String),
                    Does.Contain(nameof(ModRuntimeAsyncBridge.ParallelFor)));
                Assert.That(
                    called.Select(method => method.Name.String),
                    Does.Contain(nameof(ModRuntimeAsyncBridge.ParallelForWithOptions)));
                Assert.That(
                    called.Select(method => method.Name.String),
                    Does.Contain(nameof(ModRuntimeAsyncBridge.ParallelForEach)));
                Assert.That(
                    called.Select(method => method.Name.String),
                    Does.Contain(nameof(ModRuntimeAsyncBridge.ParallelForEachWithOptions)));
                Assert.That(
                    called.Any(method =>
                        method.DeclaringType.FullName == "System.Threading.Tasks.Parallel" &&
                        method.Name.String is "For" or "ForEach"),
                    Is.False);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void RealImageSharpParallelCallsRewriteClean()
    {
        var input = Path.Combine(
            FindRepoRoot(),
            "ADOFAIOnlineMod",
            "SixLabors.ImageSharp.dll");
        if (!File.Exists(input))
            Assert.Ignore($"ImageSharp payload is absent: {input}");

        var root = NewRoot();
        try
        {
            var rejectedOutput = Path.Combine(root, "SixLabors.ImageSharp.rejected.dll");
            var rejected = NativeModIsolationRewriteApi.Rewrite(
                input,
                rejectedOutput,
                typeof(NativeModPathBridge).Assembly.Location);
            Assert.Multiple(() =>
            {
                Assert.That(rejected.Issues, Has.Some.Contains(
                    "strong-name signed Native MOD assemblies require an explicit re-signing policy"));
                Assert.That(File.Exists(rejectedOutput), Is.False);
            });

            var output = Path.Combine(root, "SixLabors.ImageSharp.shadow.dll");
            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location,
                privateAssemblyPaths: LoadAssemblyPathMap(
                    Path.GetDirectoryName(input)!),
                options: new NativeModIsolationRewriteOptions
                {
                    StrongNamePolicy =
                        NativeModStrongNameRewritePolicy.PreserveIdentityWithoutResigning
                });

            Assert.Multiple(() =>
            {
                Assert.That(report.Issues, Is.Empty, string.Join(" | ", report.Issues));
                Assert.That(File.Exists(output), Is.True);
                var sourceIdentity = AssemblyName.GetAssemblyName(input);
                var outputIdentity = AssemblyName.GetAssemblyName(output);
                Assert.That(outputIdentity.Name, Is.EqualTo(sourceIdentity.Name));
                Assert.That(outputIdentity.Version, Is.EqualTo(sourceIdentity.Version));
                Assert.That(outputIdentity.GetPublicKeyToken(), Is.EqualTo(sourceIdentity.GetPublicKeyToken()));
                using var outputModule = ModuleDefMD.Load(output);
                Assert.That(outputModule.IsStrongNameSigned, Is.False);
                Assert.That(report.RewrittenAsyncCalls, Is.GreaterThanOrEqualTo(5));
                Assert.That(
                    report.AsyncRewrites.Any(rewrite =>
                        rewrite.Kind == "parallel-for" && rewrite.RewriteCount >= 4),
                    Is.True,
                    "ImageSharp's normalization processor must use the generation-bound Parallel bridge.");
                Assert.That(
                    report.AsyncRewrites.Any(rewrite =>
                        rewrite.Kind == "parallel-foreach" && rewrite.RewriteCount >= 1),
                    Is.True,
                    "ImageSharp's processor must use the generation-bound Parallel.ForEach bridge.");
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void RealAdofaiOnlineModRvaInitializeArrayHandlesRewriteClean()
    {
        var input = Path.Combine(
            FindRepoRoot(),
            "ADOFAIOnlineMod",
            "ADOFAIOnlineMod.dll");
        if (!File.Exists(input))
            Assert.Ignore($"ADOFAIOnlineMod payload is absent: {input}");

        var root = NewRoot();
        try
        {
            var output = Path.Combine(root, "ADOFAIOnlineMod.shadow.dll");
            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location,
                privateAssemblyPaths: LoadAssemblyPathMap(
                    Path.GetDirectoryName(input)!),
                options: new NativeModIsolationRewriteOptions
                {
                    StrongNamePolicy =
                        NativeModStrongNameRewritePolicy.PreserveIdentityWithoutResigning
                });

            Assert.Multiple(() =>
            {
                Assert.That(report.Issues, Is.Empty, string.Join(" | ", report.Issues));
                Assert.That(File.Exists(output), Is.True);
                Assert.That(report.RewrittenStaticFieldInstructions, Is.GreaterThan(0));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestCase("ADOFAIOnlineMod/ADOFAIOnlineMod.dll")]
    [TestCase("ADOFAIOnlineMod-1.0.0-mobile.100-unobfuscated/ADOFAIOnlineMod/ADOFAIOnlineMod.dll")]
    public void RealAdofaiOnlineModCallbackOnlyRewritePreservesNonCallbackSurfaces(
        string relativeInput)
    {
        var input = Path.Combine(FindRepoRoot(), relativeInput);
        if (!File.Exists(input))
            Assert.Ignore($"ADOFAIOnlineMod payload is absent: {relativeInput}");

        var root = NewRoot();
        try
        {
            var output = Path.Combine(
                root,
                $"{Path.GetFileNameWithoutExtension(input)}.callback-only.dll");
            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location,
                privateAssemblyPaths: LoadAssemblyPathMap(Path.GetDirectoryName(input)!),
                options: new NativeModIsolationRewriteOptions
                {
                    Mode = NativeModIsolationRewriteMode.CallbackOnly,
                    StrongNamePolicy =
                        NativeModStrongNameRewritePolicy.PreserveIdentityWithoutResigning
                });

            using var rewritten = ModuleDefMD.Load(output);
            var instructions = rewritten.GetTypes()
                .SelectMany(type => type.Methods)
                .Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions)
                .ToArray();
            var gatedCalls = instructions.Count(instruction =>
                instruction.OpCode.Code == Code.Call &&
                instruction.Operand is IMethod called &&
                called.DeclaringType.FullName == typeof(HookHelper).FullName &&
                called.Name == nameof(HookHelper.HookRuntimeGatedRequired));
            var callbackBodies = rewritten.GetTypes()
                .SelectMany(type => type.Methods)
                .Count(method => method.Name.StartsWith(
                    "__starray_callback_body_",
                    StringComparison.Ordinal));

            Assert.Multiple(() =>
            {
                Assert.That(report.Issues, Is.Empty, string.Join(" | ", report.Issues));
                Assert.That(File.Exists(output), Is.True);
                Assert.That(report.RewrittenAssemblyLocationCalls, Is.GreaterThan(0));
                Assert.That(report.RewrittenStaticFieldInstructions, Is.Zero);
                Assert.That(report.RewrittenAsyncCalls, Is.Zero);
                Assert.That(report.TrackedTaskReturnMethods, Is.Zero);
                Assert.That(report.RewrittenFileCalls, Is.Zero);
                Assert.That(report.RewrittenNetworkCalls, Is.Zero);
                Assert.That(gatedCalls, Is.GreaterThan(0));
                Assert.That(callbackBodies, Is.EqualTo(gatedCalls));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void RealUnobfuscatedAdofaiOnlineModFilesystemSurfaceRewritesClean()
    {
        var input = Path.Combine(
            FindRepoRoot(),
            "ADOFAIOnlineMod-1.0.0-mobile.100-unobfuscated",
            "ADOFAIOnlineMod",
            "ADOFAIOnlineMod.dll");
        if (!File.Exists(input))
            Assert.Ignore($"unobfuscated ADOFAIOnlineMod payload is absent: {input}");

        var root = NewRoot();
        try
        {
            var output = Path.Combine(root, "ADOFAIOnlineMod.unobfuscated.shadow.dll");
            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location,
                privateAssemblyPaths: LoadAssemblyPathMap(Path.GetDirectoryName(input)!),
                options: new NativeModIsolationRewriteOptions
                {
                    StrongNamePolicy =
                        NativeModStrongNameRewritePolicy.PreserveIdentityWithoutResigning
                });

            using var rewritten = ModuleDefMD.Load(output);
            var directFileCalls = rewritten.GetTypes()
                .SelectMany(type => type.Methods)
                .Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions)
                .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
                .Select(instruction => instruction.Operand)
                .OfType<IMethod>()
                .Where(method => method.DeclaringType.FullName is
                    "System.IO.File" or "System.IO.Directory" or "System.IO.FileInfo")
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(report.Issues, Is.Empty, string.Join(" | ", report.Issues));
                Assert.That(report.RewrittenFileCalls, Is.GreaterThan(0));
                Assert.That(directFileCalls, Is.Empty,
                    "all supported filesystem entry points must pass through the domain bridge");
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void UnsupportedValueTaskReturnFailsBeforePublishingOutput()
    {
        var root = NewRoot();
        try
        {
            var input = Path.Combine(root, "ValueTaskFixture.dll");
            var output = Path.Combine(root, "ValueTaskFixture.shadow.dll");
            CreateValueTaskFixture(input);

            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            Assert.Multiple(() =>
            {
                Assert.That(report.Issues, Has.Some.Contains("ValueTask return"));
                Assert.That(File.Exists(output), Is.False);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void RewrittenShadowUsesOriginalLocationOnlyInsideItsDomain()
    {
        var root = NewRoot();
        NativeModShadowRewriteRuntime.RegisterProvider(
            "test-location-v1",
            RewriteWithLocationBridge);
        try
        {
            RunPathBridgeScenario(root);
        }
        finally
        {
            NativeModShadowRewriteRuntime.RegisterProvider(null, null);
            CollectContexts();
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void RewriteAbiParticipatesInContentAddress()
    {
        var root = NewRoot();
        var modDirectory = Path.Combine(root, "Fixture");
        var cacheRoot = Path.Combine(root, "cache");
        Directory.CreateDirectory(modDirectory);
        var entry = Path.Combine(modDirectory, "Fixture.dll");
        CreateLocationFixture(entry);
        try
        {
            NativeModShadowRewriteRuntime.RegisterProvider("rewrite-a", CopyRewrite);
            var first = NativeModShadowPackage.Prepare(cacheRoot, modDirectory, entry);
            NativeModShadowRewriteRuntime.RegisterProvider("rewrite-b", CopyRewrite);
            var second = NativeModShadowPackage.Prepare(cacheRoot, modDirectory, entry);

            Assert.Multiple(() =>
            {
                Assert.That(second.CacheKey, Is.Not.EqualTo(first.CacheKey));
                Assert.That(second.PackageDirectory, Is.Not.EqualTo(first.PackageDirectory));
                Assert.That(first.RewriteAbi, Is.EqualTo("rewrite-a"));
                Assert.That(second.RewriteAbi, Is.EqualTo("rewrite-b"));
            });
        }
        finally
        {
            NativeModShadowRewriteRuntime.RegisterProvider(null, null);
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void StaticSlotProofPersistsAcrossShadowCacheHit()
    {
        var root = NewRoot();
        var modDirectory = Path.Combine(root, "Fixture");
        var cacheRoot = Path.Combine(root, "cache");
        Directory.CreateDirectory(modDirectory);
        var entry = Path.Combine(modDirectory, "StaticFixture.dll");
        CreateStaticFixture(entry);
        NativeModShadowRewriteRuntime.RegisterProvider(
            "test-domain-static-v1",
            RewriteWithLocationBridge);
        try
        {
            var published = NativeModShadowPackage.Prepare(cacheRoot, modDirectory, entry);
            var cached = NativeModShadowPackage.Prepare(cacheRoot, modDirectory, entry);

            Assert.Multiple(() =>
            {
                Assert.That(published.CacheHit, Is.False);
                Assert.That(cached.CacheHit, Is.True);
                Assert.That(published.StaticMembers, Has.Count.EqualTo(1));
                Assert.That(cached.StaticMembers, Is.EqualTo(published.StaticMembers));
                Assert.That(
                    cached.StaticMembers[0].Classification,
                    Is.EqualTo(ModStaticStateClassification.DomainMutable));
                Assert.That(cached.PackageDirectory, Is.EqualTo(published.PackageDirectory));
            });
        }
        finally
        {
            NativeModShadowRewriteRuntime.RegisterProvider(null, null);
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void AsyncRewriteProofPersistsAcrossShadowCacheHit()
    {
        var root = NewRoot();
        var modDirectory = Path.Combine(root, "Fixture");
        var cacheRoot = Path.Combine(root, "cache");
        Directory.CreateDirectory(modDirectory);
        var entry = Path.Combine(modDirectory, "AsyncFixture.dll");
        CreateAsyncFixture(entry);
        NativeModShadowRewriteRuntime.RegisterProvider(
            "test-async-domain-v1",
            RewriteWithLocationBridge);
        try
        {
            var published = NativeModShadowPackage.Prepare(cacheRoot, modDirectory, entry);
            var cached = NativeModShadowPackage.Prepare(cacheRoot, modDirectory, entry);

            Assert.Multiple(() =>
            {
                Assert.That(published.AsyncRewrites, Has.Count.EqualTo(3));
                Assert.That(cached.AsyncRewrites, Is.EqualTo(published.AsyncRewrites));
                Assert.That(cached.CacheHit, Is.True);
            });
        }
        finally
        {
            NativeModShadowRewriteRuntime.RegisterProvider(null, null);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void RunPathBridgeScenario(string root)
    {
        var modDirectory = Path.Combine(root, "Fixture");
        var cacheRoot = Path.Combine(root, "cache");
        Directory.CreateDirectory(modDirectory);
        var entry = Path.Combine(modDirectory, "Fixture.dll");
        CreateLocationFixture(entry);
        var package = NativeModShadowPackage.Prepare(cacheRoot, modDirectory, entry);
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, "path-fixture");
        Assert.That(ModDataDomainRegistry.TryResolve(session.DomainToken, out var domain), Is.True);
        domain.BindOriginalAssemblyLocations(package.OriginalAssemblyLocations);
        var context = new NativeModAssemblyLoadContext(key.ModId, package.EntryAssemblyPath);
        try
        {
            var assembly = context.LoadFromAssemblyPath(package.EntryAssemblyPath);
            using (HookHelper.EnterOwnerScope(key.OwnerId, session, key))
            {
                Assert.That(
                    NativeModPathBridge.GetAssemblyLocation(assembly),
                    Is.EqualTo(entry));
            }
            Assert.Throws<InvalidOperationException>(() =>
                NativeModPathBridge.GetAssemblyLocation(assembly));
        }
        finally
        {
            Assert.That(session.TryAbortLoad(key), Is.True);
            context.Unload();
        }
    }

    [Test]
    public void FileCallsAreRewrittenToDomainPathBridgeWithProof()
    {
        var root = NewRoot();
        try
        {
            var input = Path.Combine(root, "FileFixture.dll");
            var output = Path.Combine(root, "FileFixture.shadow.dll");
            CreateFileFixture(input, EmitSupportedFileCalls);

            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            using var rewritten = ModuleDefMD.Load(output);
            var called = rewritten.GetTypes()
                .SelectMany(type => type.Methods)
                .Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions)
                .Where(instruction =>
                    instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
                .Select(instruction => (IMethod)instruction.Operand)
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(report.Issues, Is.Empty);
                Assert.That(report.RewrittenFileCalls, Is.EqualTo(6));

                // every rewritten site now targets the bridge
                Assert.That(called.Count(method =>
                        method.DeclaringType.FullName == typeof(NativeModPathBridge).FullName),
                    Is.EqualTo(6));

                // and none of the raw filesystem entry points survive
                Assert.That(called.Any(method =>
                    method.DeclaringType.FullName is "System.IO.File" or "System.IO.Directory"),
                    Is.False);
                Assert.That(called.Any(method =>
                    method.DeclaringType.FullName == "System.IO.Path" &&
                    method.Name == "GetFullPath"), Is.False);

                // proof is recorded per member and kind
                Assert.That(
                    report.FileRewrites.Select(rewrite => rewrite.Kind).Distinct(),
                    Is.EquivalentTo(new[]
                    {
                        "path-full",
                        "file",
                        "directory",
                        "stream-writer"
                    }));
                Assert.That(
                    report.FileRewrites.Sum(rewrite => rewrite.RewriteCount),
                    Is.EqualTo(6));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void PurePathHelpersAreLeftAlone()
    {
        var root = NewRoot();
        try
        {
            var input = Path.Combine(root, "PurePathFixture.dll");
            var output = Path.Combine(root, "PurePathFixture.shadow.dll");
            CreateFileFixture(input, (module, body, importer) =>
            {
                // Path.Combine has no ambient state, so routing it through the bridge would
                // add cost without changing isolation.
                body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "a"));
                body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "b"));
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Call,
                    importer.Import(typeof(Path).GetMethod(
                        nameof(Path.Combine),
                        [typeof(string), typeof(string)])!)));
                body.Instructions.Add(Instruction.Create(OpCodes.Pop));
                body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            });

            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            Assert.Multiple(() =>
            {
                Assert.That(report.Issues, Is.Empty);
                Assert.That(report.RewrittenFileCalls, Is.Zero);
                Assert.That(report.FileRewrites, Is.Empty);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestCase("AppendAllText")]
    public void UnsupportedFileEntryPointsFailClosed(string methodName)
    {
        var root = NewRoot();
        try
        {
            var input = Path.Combine(root, "UnsupportedFileFixture.dll");
            var output = Path.Combine(root, "UnsupportedFileFixture.shadow.dll");
            CreateFileFixture(input, (module, body, importer) =>
            {
                var readAll = methodName == "ReadAllText";
                var target = readAll
                    ? typeof(File).GetMethod(nameof(File.ReadAllText), [typeof(string)])!
                    : typeof(File).GetMethod(
                        nameof(File.AppendAllText),
                        [typeof(string), typeof(string)])!;
                body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "probe.txt"));
                if (!readAll)
                    body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "payload"));
                body.Instructions.Add(Instruction.Create(OpCodes.Call, importer.Import(target)));
                if (readAll)
                    body.Instructions.Add(Instruction.Create(OpCodes.Pop));
                body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            });

            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            Assert.Multiple(() =>
            {
                Assert.That(
                    report.Issues.Any(issue => issue.Contains(
                        "unsupported System.IO.File entry point",
                        StringComparison.Ordinal)),
                    Is.True,
                    $"expected fail-closed issue for File.{methodName}; got: " +
                    string.Join(" | ", report.Issues));
                Assert.That(File.Exists(output), Is.False, "failed rewrite must not publish");
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void TextAndFileInfoCallsAreRewrittenToTheDomainBridges()
    {
        var root = NewRoot();
        try
        {
            var input = Path.Combine(root, "FileMetadataFixture.dll");
            var output = Path.Combine(root, "FileMetadataFixture.shadow.dll");
            CreateFileFixture(input, (module, body, importer) =>
            {
                var fileInfoConstructor = typeof(FileInfo).GetConstructor([typeof(string)])
                    ?? throw new InvalidOperationException("FileInfo(string) reflection lookup failed.");
                var fileInfoLength = typeof(FileInfo).GetProperty(nameof(FileInfo.Length))?.GetMethod
                    ?? throw new InvalidOperationException("FileInfo.Length reflection lookup failed.");
                var fileInfoExists = typeof(FileInfo).GetProperty(nameof(FileInfo.Exists))?.GetMethod
                    ?? throw new InvalidOperationException("FileInfo.Exists reflection lookup failed.");
                var readAllText = typeof(File).GetMethod(
                    nameof(File.ReadAllText), [typeof(string)])
                    ?? throw new InvalidOperationException("File.ReadAllText(string) reflection lookup failed.");
                var writeAllText = typeof(File).GetMethod(
                    nameof(File.WriteAllText), [typeof(string), typeof(string)])
                    ?? throw new InvalidOperationException("File.WriteAllText(string,string) reflection lookup failed.");
                var timeSpanConstructor = typeof(TimeSpan).GetConstructor([typeof(long)])
                    ?? throw new InvalidOperationException("TimeSpan(long) reflection lookup failed.");
                var periodicConstructor = typeof(PeriodicTimer).GetConstructor([typeof(TimeSpan)])
                    ?? throw new InvalidOperationException("PeriodicTimer(TimeSpan) reflection lookup failed.");
                var periodicWait = typeof(PeriodicTimer).GetMethod(
                    nameof(PeriodicTimer.WaitForNextTickAsync), [typeof(CancellationToken)])
                    ?? throw new InvalidOperationException("PeriodicTimer wait reflection lookup failed.");
                var periodicDispose = typeof(PeriodicTimer).GetMethod(
                    nameof(PeriodicTimer.Dispose), Type.EmptyTypes)
                    ?? throw new InvalidOperationException("PeriodicTimer.Dispose reflection lookup failed.");
                var fileInfo = importer.Import(typeof(FileInfo)).ToTypeSig();
                var periodicTimer = importer.Import(typeof(PeriodicTimer)).ToTypeSig();
                var fileInfoLocal = new Local(fileInfo);
                var periodicTimerLocal = new Local(periodicTimer);
                var cancellationTokenLocal = new Local(
                    importer.Import(typeof(CancellationToken)).ToTypeSig());
                body.Variables.Add(fileInfoLocal);
                body.Variables.Add(periodicTimerLocal);
                body.Variables.Add(cancellationTokenLocal);
                body.InitLocals = true;

                body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "probe.txt"));
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Newobj,
                    importer.Import(fileInfoConstructor)));
                body.Instructions.Add(Instruction.Create(OpCodes.Stloc, fileInfoLocal));
                body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, fileInfoLocal));
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Callvirt,
                    importer.Import(fileInfoLength)));
                body.Instructions.Add(Instruction.Create(OpCodes.Pop));
                body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, fileInfoLocal));
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Callvirt,
                    importer.Import(fileInfoExists)));
                body.Instructions.Add(Instruction.Create(OpCodes.Pop));

                body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "probe.txt"));
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Call,
                    importer.Import(readAllText)));
                body.Instructions.Add(Instruction.Create(OpCodes.Pop));
                body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "probe.txt"));
                body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "payload"));
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Call,
                    importer.Import(writeAllText)));

                body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I8, TimeSpan.TicksPerSecond));
                body.Instructions.Add(Instruction.Create(OpCodes.Newobj, importer.Import(
                    timeSpanConstructor)));
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Newobj,
                    importer.Import(periodicConstructor)));
                body.Instructions.Add(Instruction.Create(OpCodes.Stloc, periodicTimerLocal));
                body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, periodicTimerLocal));
                body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, cancellationTokenLocal));
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Callvirt,
                    importer.Import(periodicWait)));
                body.Instructions.Add(Instruction.Create(OpCodes.Pop));
                body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, periodicTimerLocal));
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Callvirt,
                    importer.Import(periodicDispose)));
                body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            });

            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            using var rewritten = ModuleDefMD.Load(output);
            var called = rewritten.GetTypes()
                .SelectMany(type => type.Methods)
                .Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions)
                .Where(instruction =>
                    instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
                .Select(instruction => (IMethod)instruction.Operand)
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(report.Issues, Is.Empty, string.Join(" | ", report.Issues));
                Assert.That(report.RewrittenFileCalls, Is.EqualTo(5));
                Assert.That(report.RewrittenAsyncCalls, Is.EqualTo(3));
                Assert.That(called.Count(method =>
                        method.DeclaringType.FullName == typeof(NativeModPathBridge).FullName),
                    Is.EqualTo(5));
                Assert.That(called.Count(method =>
                        method.DeclaringType.FullName == typeof(ModRuntimeAsyncBridge).FullName),
                    Is.EqualTo(3));
                Assert.That(called.Any(method =>
                    method.DeclaringType.FullName is "System.IO.File" or "System.IO.FileInfo" or
                    "System.IO.FileSystemInfo" or "System.Threading.PeriodicTimer"), Is.False);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void NativeOnlineModFilesystemSurfaceIsRewrittenToDomainBridges()
    {
        var root = NewRoot();
        try
        {
            var input = Path.Combine(root, "OnlineModFilesystemFixture.dll");
            var output = Path.Combine(root, "OnlineModFilesystemFixture.shadow.dll");
            CreateFileFixture(input, (module, body, importer) =>
            {
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Call,
                    importer.Import(typeof(Path).GetMethod(
                        nameof(Path.GetTempPath), Type.EmptyTypes)!)));
                body.Instructions.Add(Instruction.Create(OpCodes.Pop));

                body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "payload.bin"));
                body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
                body.Instructions.Add(Instruction.Create(OpCodes.Newarr, module.CorLibTypes.Byte));
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Call,
                    importer.Import(typeof(File).GetMethod(
                        nameof(File.WriteAllBytes), [typeof(string), typeof(byte[])])!)));

                body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "created.bin"));
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Call,
                    importer.Import(typeof(File).GetMethod(
                        nameof(File.Create), [typeof(string)])!)));
                body.Instructions.Add(Instruction.Create(OpCodes.Pop));

                body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "from"));
                body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "to"));
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Call,
                    importer.Import(typeof(Directory).GetMethod(
                        nameof(Directory.Move), [typeof(string), typeof(string)])!)));

                body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "entries"));
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Call,
                    importer.Import(typeof(Directory).GetMethod(
                        nameof(Directory.EnumerateFileSystemEntries), [typeof(string)])!)));
                body.Instructions.Add(Instruction.Create(OpCodes.Pop));

                body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "files"));
                body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "*.dat"));
                body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Call,
                    importer.Import(typeof(Directory).GetMethod(
                        nameof(Directory.GetFiles),
                        [typeof(string), typeof(string), typeof(SearchOption)])!)));
                body.Instructions.Add(Instruction.Create(OpCodes.Pop));

                body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "directories"));
                body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "*"));
                body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Call,
                    importer.Import(typeof(Directory).GetMethod(
                        nameof(Directory.EnumerateDirectories),
                        [typeof(string), typeof(string), typeof(SearchOption)])!)));
                body.Instructions.Add(Instruction.Create(OpCodes.Pop));
                body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            });

            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            using var rewritten = ModuleDefMD.Load(output);
            var called = rewritten.GetTypes()
                .SelectMany(type => type.Methods)
                .Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions)
                .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
                .Select(instruction => instruction.Operand)
                .OfType<IMethod>()
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(report.Issues, Is.Empty, string.Join(" | ", report.Issues));
                Assert.That(report.RewrittenFileCalls, Is.EqualTo(7));
                Assert.That(called.Count(method =>
                        method.DeclaringType.FullName == typeof(NativeModPathBridge).FullName),
                    Is.EqualTo(7));
                Assert.That(called.Any(method =>
                    method.DeclaringType.FullName is "System.IO.File" or "System.IO.Directory" or
                    "System.IO.Path"), Is.False);
                Assert.That(report.FileRewrites.Select(rewrite => rewrite.Kind).Distinct(),
                    Is.EquivalentTo(new[]
                    {
                        "path-temp",
                        "file",
                        "directory"
                    }));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void PeriodicTimerCancellationOverloadIsRewritten()
    {
        var root = NewRoot();
        try
        {
            var input = Path.Combine(root, "PeriodicTimerCancellationFixture.dll");
            var output = Path.Combine(root, "PeriodicTimerCancellationFixture.shadow.dll");
            CreateFileFixture(input, (module, body, importer) =>
            {
                var cancellationToken = new Local(
                    importer.Import(typeof(CancellationToken)).ToTypeSig());
                body.Variables.Add(cancellationToken);
                body.InitLocals = true;
                body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I8, TimeSpan.TicksPerSecond));
                body.Instructions.Add(Instruction.Create(OpCodes.Newobj, importer.Import(
                    typeof(TimeSpan).GetConstructor([typeof(long)])!)));
                body.Instructions.Add(Instruction.Create(OpCodes.Newobj, importer.Import(
                    typeof(PeriodicTimer).GetConstructor([typeof(TimeSpan)])!)));
                body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, cancellationToken));
                body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, importer.Import(
                    typeof(PeriodicTimer).GetMethod(
                        nameof(PeriodicTimer.WaitForNextTickAsync),
                        [typeof(CancellationToken)])!)));
                body.Instructions.Add(Instruction.Create(OpCodes.Pop));
                body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            });

            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            Assert.Multiple(() =>
            {
                Assert.That(report.Issues, Is.Empty, string.Join(" | ", report.Issues));
                Assert.That(report.RewrittenAsyncCalls, Is.EqualTo(2));
                Assert.That(report.AsyncRewrites.Select(proof => proof.Kind),
                    Does.Contain("periodic-timer-wait"));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ProcessGlobalDirectoryStateFailsClosed()
    {
        var root = NewRoot();
        try
        {
            var input = Path.Combine(root, "CwdFixture.dll");
            var output = Path.Combine(root, "CwdFixture.shadow.dll");
            CreateFileFixture(input, (module, body, importer) =>
            {
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Call,
                    importer.Import(typeof(Environment)
                        .GetProperty(nameof(Environment.CurrentDirectory))!.GetMethod!)));
                body.Instructions.Add(Instruction.Create(OpCodes.Pop));
                body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            });

            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            Assert.That(
                report.Issues.Any(issue =>
                    issue.Contains("process-global directory state", StringComparison.Ordinal)),
                Is.True,
                string.Join(" | ", report.Issues));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void EmitSupportedFileCalls(
        ModuleDefUser module,
        CilBody body,
        Importer importer)
    {
        body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "probe.txt"));
        body.Instructions.Add(Instruction.Create(
            OpCodes.Call,
            importer.Import(typeof(Path).GetMethod(
                nameof(Path.GetFullPath),
                [typeof(string)])!)));
        body.Instructions.Add(Instruction.Create(OpCodes.Pop));

        body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "probe.txt"));
        body.Instructions.Add(Instruction.Create(
            OpCodes.Call,
            importer.Import(typeof(File).GetMethod(
                nameof(File.Exists),
                [typeof(string)])!)));
        body.Instructions.Add(Instruction.Create(OpCodes.Pop));

        body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "probe.txt"));
        body.Instructions.Add(Instruction.Create(
            OpCodes.Call,
            importer.Import(typeof(File).GetMethod(
                nameof(File.Delete),
                [typeof(string)])!)));

        var fileOpenOptions = typeof(File).GetMethod(
            nameof(File.Open),
            [typeof(string), typeof(FileStreamOptions)])
            ?? throw new InvalidOperationException(
                "File.Open(string, FileStreamOptions) reflection lookup failed.");
        var fileStreamOptionsConstructor = typeof(FileStreamOptions).GetConstructor(Type.EmptyTypes)
            ?? throw new InvalidOperationException("FileStreamOptions constructor lookup failed.");
        body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "options.bin"));
        body.Instructions.Add(Instruction.Create(
            OpCodes.Newobj,
            importer.Import(fileStreamOptionsConstructor)));
        body.Instructions.Add(Instruction.Create(
            OpCodes.Call,
            importer.Import(fileOpenOptions)));
        body.Instructions.Add(Instruction.Create(OpCodes.Pop));

        body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "cache"));
        body.Instructions.Add(Instruction.Create(
            OpCodes.Call,
            importer.Import(typeof(Directory).GetMethod(
                nameof(Directory.CreateDirectory),
                [typeof(string)])!)));
        body.Instructions.Add(Instruction.Create(OpCodes.Pop));

        body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "dump.txt"));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        body.Instructions.Add(Instruction.Create(
            OpCodes.Call,
            importer.Import(typeof(System.Text.Encoding)
                .GetProperty(nameof(System.Text.Encoding.UTF8))!.GetMethod!)));
        body.Instructions.Add(Instruction.Create(
            OpCodes.Newobj,
            importer.Import(typeof(StreamWriter).GetConstructor(
                [typeof(string), typeof(bool), typeof(System.Text.Encoding)])!)));
        body.Instructions.Add(Instruction.Create(OpCodes.Pop));

        body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }

    private static void EmitParallelCalls(
        ModuleDefUser module,
        CilBody body,
        Importer importer)
    {
        var callbackType = typeof(Action<int>);
        var callback = typeof(Parallel).GetMethod(
            nameof(Parallel.For),
            BindingFlags.Public | BindingFlags.Static,
            null,
            [typeof(int), typeof(int), callbackType],
            null) ?? throw new InvalidOperationException(
            "Parallel.For(int,int,Action<int>) reflection lookup failed.");
        var optionsCallback = typeof(Parallel).GetMethod(
            nameof(Parallel.For),
            BindingFlags.Public | BindingFlags.Static,
            null,
            [typeof(int), typeof(int), typeof(ParallelOptions), callbackType],
            null) ?? throw new InvalidOperationException(
            "Parallel.For(int,int,ParallelOptions,Action<int>) reflection lookup failed.");
        var optionsConstructor = typeof(ParallelOptions).GetConstructor(Type.EmptyTypes)
            ?? throw new InvalidOperationException("ParallelOptions constructor lookup failed.");

        body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 8));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        body.Instructions.Add(Instruction.Create(OpCodes.Call, importer.Import(callback)));
        body.Instructions.Add(Instruction.Create(OpCodes.Pop));

        body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 8));
        body.Instructions.Add(Instruction.Create(OpCodes.Newobj, importer.Import(optionsConstructor)));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        body.Instructions.Add(Instruction.Create(OpCodes.Call, importer.Import(optionsCallback)));
        body.Instructions.Add(Instruction.Create(OpCodes.Pop));

        var foreachCallback = typeof(Parallel).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(Parallel.ForEach) &&
                              method.IsGenericMethodDefinition &&
                              method.GetGenericArguments().Length == 1 &&
                              method.GetParameters() is var parameters &&
                              parameters.Length == 2 &&
                              parameters[0].ParameterType.IsGenericType &&
                              parameters[0].ParameterType.GetGenericTypeDefinition() ==
                              typeof(IEnumerable<>) &&
                              parameters[1].ParameterType.IsGenericType &&
                              parameters[1].ParameterType.GetGenericTypeDefinition() ==
                              typeof(Action<>));
        var foreachOptionsCallback = typeof(Parallel).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(Parallel.ForEach) &&
                              method.IsGenericMethodDefinition &&
                              method.GetGenericArguments().Length == 1 &&
                              method.GetParameters() is var parameters &&
                              parameters.Length == 3 &&
                              parameters[0].ParameterType.IsGenericType &&
                              parameters[0].ParameterType.GetGenericTypeDefinition() ==
                              typeof(IEnumerable<>) &&
                              parameters[1].ParameterType == typeof(ParallelOptions) &&
                              parameters[2].ParameterType.IsGenericType &&
                              parameters[2].ParameterType.GetGenericTypeDefinition() ==
                              typeof(Action<>));
        var intType = module.CorLibTypes.Int32;
        var closedForEach = new MethodSpecUser(
            (IMethodDefOrRef)importer.Import(foreachCallback),
            new GenericInstMethodSig(intType));
        var closedForEachWithOptions = new MethodSpecUser(
            (IMethodDefOrRef)importer.Import(foreachOptionsCallback),
            new GenericInstMethodSig(intType));

        body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        body.Instructions.Add(Instruction.Create(OpCodes.Call, closedForEach));
        body.Instructions.Add(Instruction.Create(OpCodes.Pop));

        body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        body.Instructions.Add(Instruction.Create(OpCodes.Newobj, importer.Import(optionsConstructor)));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        body.Instructions.Add(Instruction.Create(OpCodes.Call, closedForEachWithOptions));
        body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }

    private static void CreateFileFixture(
        string path,
        Action<ModuleDefUser, CilBody, Importer> emit)
    {
        var fileName = Path.GetFileName(path);
        using var module = new ModuleDefUser(
            fileName,
            Guid.NewGuid(),
            new AssemblyRefUser(new AssemblyNameInfo(typeof(object).Assembly.GetName().FullName)))
        {
            Kind = ModuleKind.Dll
        };
        new AssemblyDefUser(
            Path.GetFileNameWithoutExtension(fileName),
            new Version(1, 0, 0, 0)).Modules.Add(module);
        var type = new TypeDefUser(
            "Fixture",
            "FileProbe",
            module.CorLibTypes.Object.TypeDefOrRef)
        {
            Attributes = dnlib.DotNet.TypeAttributes.Public |
                         dnlib.DotNet.TypeAttributes.Abstract |
                         dnlib.DotNet.TypeAttributes.Sealed
        };
        module.Types.Add(type);
        var method = new MethodDefUser(
            "Run",
            MethodSig.CreateStatic(module.CorLibTypes.Void),
            dnlib.DotNet.MethodImplAttributes.IL | dnlib.DotNet.MethodImplAttributes.Managed,
            dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static)
        {
            Body = new CilBody()
        };
        var importer = new Importer(module);
        emit(module, method.Body, importer);
        var invalidOperand = method.Body.Instructions.FirstOrDefault(instruction =>
            instruction.OpCode.OperandType != OperandType.InlineNone &&
            instruction.Operand == null);
        if (invalidOperand != null)
        {
            var offset = method.Body.Instructions.IndexOf(invalidOperand);
            throw new InvalidOperationException(
                $"Fixture emitted null operand at index={offset} opcode={invalidOperand.OpCode.Code}.");
        }
        type.Methods.Add(method);
        module.Write(path);
    }

    [Test]
    public void HttpClientConstructionIsRewrittenToDomainNetworkBridge()
    {
        var root = NewRoot();
        try
        {
            var input = Path.Combine(root, "NetFixture.dll");
            var output = Path.Combine(root, "NetFixture.shadow.dll");
            CreateFileFixture(input, (module, body, importer) =>
            {
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Newobj,
                    importer.Import(typeof(System.Net.Http.HttpClient)
                        .GetConstructor(Type.EmptyTypes)!)));
                body.Instructions.Add(Instruction.Create(OpCodes.Pop));
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Newobj,
                    importer.Import(typeof(System.Net.CookieContainer)
                        .GetConstructor(Type.EmptyTypes)!)));
                body.Instructions.Add(Instruction.Create(OpCodes.Pop));
                body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            });

            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            using var rewritten = ModuleDefMD.Load(output);
            var called = rewritten.GetTypes()
                .SelectMany(type => type.Methods)
                .Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions)
                .Where(instruction =>
                    instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
                .Select(instruction => (IMethod)instruction.Operand)
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(report.Issues, Is.Empty);
                Assert.That(report.RewrittenNetworkCalls, Is.EqualTo(2));
                Assert.That(
                    report.FormatVersion,
                    Is.EqualTo(NativeModIsolationRewriteApi.FormatVersion));
                Assert.That(called.Count(method =>
                        method.DeclaringType.FullName ==
                        typeof(ModRuntimeNetworkBridge).FullName),
                    Is.EqualTo(2));
                Assert.That(called.Any(method =>
                    method.DeclaringType.FullName is "System.Net.Http.HttpClient"
                        or "System.Net.CookieContainer"), Is.False);
                Assert.That(
                    report.NetworkRewrites.Select(rewrite => rewrite.Kind).Distinct(),
                    Is.EquivalentTo(new[] { "http-client", "cookie-container" }));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void NativeCallbacksAreGenerationGatedAndRetiredCallbacksDoNotThrow()
    {
        var root = NewRoot();
        var installRoot = Path.Combine(root, "install");
        Directory.CreateDirectory(installRoot);
        var input = Path.Combine(installRoot, "NativeCallbackFixture.dll");
        var output = Path.Combine(root, "shadow", "NativeCallbackFixture.dll");
        CreateNativeCallbackFixture(input);
        var previousHook = HookHelper.Instance;
        var hook = new CountingHookProvider();
        HookHelper.Instance = hook;

        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, "native-callback-fixture");
        Assert.That(session.TryPublishActive(key), Is.True);
        Assert.That(
            ModDataDomainRegistry.TryResolve(session.DomainToken, out var domain),
            Is.True);
        var roots = new ModDataDomainPathRoots
        {
            InstallRoot = installRoot,
            ConfigRoot = Path.Combine(root, "config"),
            CacheRoot = Path.Combine(root, "cache"),
            LogRoot = Path.Combine(root, "log"),
            TempRoot = Path.Combine(root, "temp"),
            DataOverlayRoot = Path.Combine(root, "data"),
            SharedReadOnlyRoots = []
        };
        foreach (var ownedRoot in roots.OwnedRoots.Append(roots.DataOverlayRoot))
            Directory.CreateDirectory(ownedRoot);
        domain.BindPathRoots(roots);
        try
        {
            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            using var rewritten = ModuleDefMD.Load(output);
            var callbackBody = rewritten.GetTypes()
                .SelectMany(type => type.Methods)
                .Single(method => method.Name.StartsWith(
                    "__starray_callback_body_Dispatch",
                    StringComparison.Ordinal));
            var callbackWrapper = rewritten.GetTypes()
                .SelectMany(type => type.Methods)
                .Single(method => method.Name == "Dispatch");
            var businessCallback = rewritten.GetTypes()
                .SelectMany(type => type.Methods)
                .Single(method => method.Name == "Callback");
            var install = rewritten.GetTypes()
                .SelectMany(type => type.Methods)
                .Single(method => method.Name == "Install");
            var calls = install.Body!.Instructions
                .Where(instruction => instruction.OpCode.Code == Code.Call)
                .Select(instruction => instruction.Operand)
                .OfType<IMethod>()
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(report.Issues, Is.Empty);
                Assert.That(report.RewrittenFileCalls, Is.GreaterThanOrEqualTo(1));
                Assert.That(calls.Any(method =>
                    method.DeclaringType.FullName == typeof(HookHelper).FullName &&
                    method.Name == nameof(HookHelper.CaptureRuntimeCallbackGate)), Is.True);
                Assert.That(calls.Any(method =>
                    method.DeclaringType.FullName == typeof(HookHelper).FullName &&
                    method.Name == nameof(HookHelper.HookRuntimeGatedRequired)), Is.True);
                Assert.That(businessCallback.CustomAttributes.Any(attribute =>
                    attribute.AttributeType.FullName == typeof(UnmanagedHookAttribute).FullName), Is.True);
                Assert.That(callbackWrapper.CustomAttributes, Is.Empty);
                Assert.That(callbackWrapper.Body!.ExceptionHandlers, Has.Count.EqualTo(1));
                Assert.That(callbackWrapper.Body.Instructions.Any(instruction =>
                    instruction.Operand is IMethod method &&
                    method.Name == nameof(IModRuntimeCallbackGate.TryEnter)), Is.True);
                Assert.That(callbackWrapper.Body.Instructions.Any(instruction =>
                    instruction.Operand is IMethod method &&
                    method.Name == nameof(IModRuntimeCallbackGate.ReportFailure)), Is.True);
                Assert.That(businessCallback.Body!.Instructions.Any(instruction =>
                    instruction.Operand is IMethod method &&
                    method.Name == nameof(ModDataDomainRuntime.SetStaticSlot)), Is.True);
                Assert.That(install.Body.Instructions.Any(instruction =>
                    instruction.OpCode.Code == Code.Ldftn &&
                    instruction.Operand is IMethod method &&
                    method.Name == "Dispatch"), Is.True);
            });

            RunNativeCallbackScenario(output, session, key, hook);
        }
        finally
        {
            if (session.Snapshot().State == ModRuntimeLifecycleState.Active)
            {
                Assert.That(session.TryBeginRetirement(key), Is.True);
                Assert.That(session.WaitForQuiescence(key, TimeSpan.FromSeconds(2)), Is.True);
                Assert.That(session.TryCompleteRetirement(key), Is.True);
            }
            ModOwnedResourceRegistry.ClearForTests();
            HookHelper.Instance = previousHook;
            CollectContexts();
            GC.WaitForPendingFinalizers();
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void AmbiguousNativeCallbackMappingFailsClosed()
    {
        var root = NewRoot();
        try
        {
            var input = Path.Combine(root, "AmbiguousNativeCallbackFixture.dll");
            var output = Path.Combine(root, "shadow", "AmbiguousNativeCallbackFixture.dll");
            CreateNativeCallbackFixture(input, includeDynamicHook: true);

            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            Assert.Multiple(() =>
            {
                Assert.That(report.Issues.Any(issue => issue.Contains(
                    "callback mapping is ambiguous",
                    StringComparison.Ordinal)), Is.True, string.Join(" | ", report.Issues));
                Assert.That(File.Exists(output), Is.False);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void AuditNativeCallbackGatesInRealAndroidModsWhenRequested()
    {
        var configuredPaths = Environment.GetEnvironmentVariable(
            "STARRAY_NATIVE_MOD_AUDIT_PATHS");
        if (string.IsNullOrWhiteSpace(configuredPaths))
            Assert.Ignore("STARRAY_NATIVE_MOD_AUDIT_PATHS is not configured.");

        var inputPaths = configuredPaths!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries |
                                            StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .ToArray();
        Assert.That(inputPaths, Is.Not.Empty);

        var root = NewRoot();
        try
        {
            foreach (var inputPath in inputPaths)
            {
                Assert.That(File.Exists(inputPath), Is.True, inputPath);
                int originalHookCalls;
                int originalCallbacks;
                using (var original = ModuleDefMD.Load(inputPath))
                {
                    var hookInstallMethods = original.GetTypes()
                        .SelectMany(type => type.Methods)
                        .Where(method => method.HasBody && method.Body.Instructions.Any(
                            instruction =>
                                instruction.OpCode.Code is Code.Call or Code.Callvirt &&
                                instruction.Operand is IMethod called &&
                                called.DeclaringType.FullName == typeof(HookHelper).FullName &&
                                called.Name == nameof(HookHelper.Hook) &&
                                called.MethodSig is { Params.Count: 2 }))
                        .ToArray();
                    originalHookCalls = hookInstallMethods.Sum(method =>
                        method.Body.Instructions.Count(instruction =>
                            instruction.OpCode.Code is Code.Call or Code.Callvirt &&
                            instruction.Operand is IMethod called &&
                            called.DeclaringType.FullName == typeof(HookHelper).FullName &&
                            called.Name == nameof(HookHelper.Hook) &&
                            called.MethodSig is { Params.Count: 2 }));
                    originalCallbacks = hookInstallMethods
                        .SelectMany(method => method.Body.Instructions)
                        .Where(instruction => instruction.OpCode.Code == Code.Ldftn)
                        .Select(instruction => instruction.Operand)
                        .OfType<MethodDef>()
                        .Where(method => method.IsStatic && method.HasBody)
                        .Distinct()
                        .Count();
                }

                Assert.That(originalHookCalls, Is.GreaterThan(0), inputPath);
                Assert.That(originalCallbacks, Is.GreaterThan(0), inputPath);

                var output = Path.Combine(root, Path.GetFileName(inputPath));
                var report = NativeModIsolationRewriteApi.Rewrite(
                    inputPath,
                    output,
                    typeof(NativeModPathBridge).Assembly.Location);
                Assert.That(report.Issues, Is.Empty, inputPath);

                using var rewritten = ModuleDefMD.Load(output);
                var methods = rewritten.GetTypes().SelectMany(type => type.Methods).ToArray();
                var instructions = methods
                    .Where(method => method.HasBody)
                    .SelectMany(method => method.Body.Instructions)
                    .ToArray();
                var callbackBodies = methods.Where(method =>
                    method.Name.StartsWith("__starray_callback_body_", StringComparison.Ordinal))
                    .ToArray();
                var oldHookCalls = instructions.Count(instruction =>
                    instruction.OpCode.Code is Code.Call or Code.Callvirt &&
                    instruction.Operand is IMethod called &&
                    called.DeclaringType.FullName == typeof(HookHelper).FullName &&
                    called.Name == nameof(HookHelper.Hook) &&
                    called.MethodSig is { Params.Count: 2 });
                var gatedHookCalls = instructions.Count(instruction =>
                    instruction.OpCode.Code == Code.Call &&
                    instruction.Operand is IMethod called &&
                    called.DeclaringType.FullName == typeof(HookHelper).FullName &&
                    called.Name == nameof(HookHelper.HookRuntimeGatedRequired));

                Assert.Multiple(() =>
                {
                    Assert.That(oldHookCalls, Is.Zero, inputPath);
                    Assert.That(gatedHookCalls, Is.EqualTo(originalHookCalls), inputPath);
                    Assert.That(callbackBodies, Has.Length.EqualTo(originalCallbacks), inputPath);
                    Assert.That(callbackBodies.All(body =>
                        instructions.All(instruction =>
                            instruction.OpCode.Code != Code.Ldftn ||
                            !ReferenceEquals(instruction.Operand, body))), Is.True, inputPath);
                    Assert.That(callbackBodies.All(body =>
                        methods.Any(wrapper =>
                            wrapper.HasBody &&
                            wrapper.Name == body.Name.String["__starray_callback_body_".Length..] &&
                            wrapper.Body.ExceptionHandlers.Count == 1 &&
                            wrapper.Body.Instructions.Any(instruction =>
                                instruction.OpCode.Code == Code.Call &&
                                ReferenceEquals(instruction.Operand, body)))), Is.True, inputPath);
                });
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ProcessGlobalNetworkPolicyFailsClosed()
    {
        var root = NewRoot();
        try
        {
            var input = Path.Combine(root, "ServicePointFixture.dll");
            var output = Path.Combine(root, "ServicePointFixture.shadow.dll");
            CreateFileFixture(input, (module, body, importer) =>
            {
                body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 32));
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Call,
                    importer.Import(typeof(System.Net.ServicePointManager)
                        .GetProperty(nameof(System.Net.ServicePointManager
                            .DefaultConnectionLimit))!.SetMethod!)));
                body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            });

            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            Assert.Multiple(() =>
            {
                Assert.That(
                    report.Issues.Any(issue => issue.Contains(
                        "ServicePointManager is process-global",
                        StringComparison.Ordinal)),
                    Is.True,
                    string.Join(" | ", report.Issues));
                Assert.That(File.Exists(output), Is.False, "failed rewrite must not publish");
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void OperationsOnBoundClientsAreLeftAlone()
    {
        var root = NewRoot();
        try
        {
            var input = Path.Combine(root, "BoundClientFixture.dll");
            var output = Path.Combine(root, "BoundClientFixture.shadow.dll");
            CreateFileFixture(input, (module, body, importer) =>
            {
                // A client the MOD received from the bridge: reading its headers inherits the
                // client's domain, so rewriting the getter would add cost for no isolation.
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Newobj,
                    importer.Import(typeof(System.Net.Http.HttpClient)
                        .GetConstructor(Type.EmptyTypes)!)));
                body.Instructions.Add(Instruction.Create(
                    OpCodes.Callvirt,
                    importer.Import(typeof(System.Net.Http.HttpClient)
                        .GetProperty(nameof(System.Net.Http.HttpClient
                            .DefaultRequestHeaders))!.GetMethod!)));
                body.Instructions.Add(Instruction.Create(OpCodes.Pop));
                body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            });

            var report = NativeModIsolationRewriteApi.Rewrite(
                input,
                output,
                typeof(NativeModPathBridge).Assembly.Location);

            Assert.Multiple(() =>
            {
                Assert.That(report.Issues, Is.Empty);
                // only the constructor was rewritten
                Assert.That(report.RewrittenNetworkCalls, Is.EqualTo(1));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static NativeModShadowRewriteResult RewriteWithLocationBridge(
        NativeModShadowRewriteRequest request)
    {
        var report = NativeModIsolationRewriteApi.Rewrite(
            request.InputAssemblyPath,
            request.OutputAssemblyPath,
            typeof(NativeModPathBridge).Assembly.Location);
        return new NativeModShadowRewriteResult(
            report.RewrittenAssemblyLocationCalls +
            report.RewrittenStaticFieldInstructions +
            report.RewrittenAsyncCalls +
            report.TrackedTaskReturnMethods +
            report.RewrittenFileCalls +
            report.RewrittenNetworkCalls,
            report.Issues)
        {
            StaticSlots = report.StaticSlots
                .Select(slot => new NativeModShadowStaticSlotRecord(
                    slot.StaticSlotId,
                    slot.MemberIdentity))
                .ToArray(),
            AsyncRewrites = report.AsyncRewrites
                .Select(rewrite => new NativeModShadowAsyncRewriteRecord(
                    rewrite.MemberIdentity,
                    rewrite.Kind,
                    rewrite.RewriteCount))
                .ToArray(),
            FileRewrites = report.FileRewrites
                .Select(rewrite => new NativeModShadowFileRewriteRecord(
                    rewrite.MemberIdentity,
                    rewrite.Kind,
                    rewrite.RewriteCount))
                .ToArray(),
            NetworkRewrites = report.NetworkRewrites
                .Select(rewrite => new NativeModShadowNetworkRewriteRecord(
                    rewrite.MemberIdentity,
                    rewrite.Kind,
                    rewrite.RewriteCount))
                .ToArray()
        };
    }

    private static NativeModShadowRewriteResult CopyRewrite(
        NativeModShadowRewriteRequest request)
    {
        File.Copy(request.InputAssemblyPath, request.OutputAssemblyPath, overwrite: false);
        return new NativeModShadowRewriteResult(0, Array.Empty<string>());
    }

    private static void CreateLocationFixture(string path)
    {
        using var module = new ModuleDefUser(
            "LocationFixture.dll",
            Guid.NewGuid(),
            new AssemblyRefUser(new AssemblyNameInfo(typeof(object).Assembly.GetName().FullName)))
        {
            Kind = ModuleKind.Dll
        };
        new AssemblyDefUser("LocationFixture", new Version(1, 0, 0, 0)).Modules.Add(module);
        var type = new TypeDefUser(
            "Fixture",
            "LocationProbe",
            module.CorLibTypes.Object.TypeDefOrRef)
        {
            Attributes = dnlib.DotNet.TypeAttributes.Public |
                         dnlib.DotNet.TypeAttributes.Abstract |
                         dnlib.DotNet.TypeAttributes.Sealed
        };
        module.Types.Add(type);
        var method = new MethodDefUser(
            "Read",
            MethodSig.CreateStatic(module.CorLibTypes.String),
            dnlib.DotNet.MethodImplAttributes.IL | dnlib.DotNet.MethodImplAttributes.Managed,
            dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static)
        {
            Body = new CilBody()
        };
        var importer = new Importer(module);
        method.Body.Instructions.Add(Instruction.Create(
            OpCodes.Call,
            importer.Import(typeof(Assembly).GetMethod(
                nameof(Assembly.GetExecutingAssembly),
                BindingFlags.Public | BindingFlags.Static)!)));
        method.Body.Instructions.Add(Instruction.Create(
            OpCodes.Callvirt,
            importer.Import(typeof(Assembly).GetProperty(nameof(Assembly.Location))!.GetMethod!)));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        module.Write(path);
    }

    private static void CreateStaticFixture(string path)
    {
        using var module = new ModuleDefUser(
            "StaticFixture.dll",
            Guid.NewGuid(),
            new AssemblyRefUser(new AssemblyNameInfo(typeof(object).Assembly.GetName().FullName)))
        {
            Kind = ModuleKind.Dll
        };
        new AssemblyDefUser("StaticFixture", new Version(1, 0, 0, 0)).Modules.Add(module);
        var type = new TypeDefUser(
            "Fixture",
            "StaticProbe",
            module.CorLibTypes.Object.TypeDefOrRef)
        {
            Attributes = dnlib.DotNet.TypeAttributes.Public |
                         dnlib.DotNet.TypeAttributes.Abstract |
                         dnlib.DotNet.TypeAttributes.Sealed
        };
        module.Types.Add(type);
        var field = new FieldDefUser(
            "Value",
            new FieldSig(module.CorLibTypes.Int32),
            dnlib.DotNet.FieldAttributes.Public | dnlib.DotNet.FieldAttributes.Static);
        type.Fields.Add(field);

        var constructor = new MethodDefUser(
            ".cctor",
            MethodSig.CreateStatic(module.CorLibTypes.Void),
            dnlib.DotNet.MethodImplAttributes.IL | dnlib.DotNet.MethodImplAttributes.Managed,
            dnlib.DotNet.MethodAttributes.Private |
            dnlib.DotNet.MethodAttributes.Static |
            dnlib.DotNet.MethodAttributes.SpecialName |
            dnlib.DotNet.MethodAttributes.RTSpecialName)
        {
            Body = new CilBody()
        };
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_7));
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, field));
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(constructor);

        var read = NewStaticMethod(type, "Read", MethodSig.CreateStatic(module.CorLibTypes.Int32));
        read.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, field));
        read.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        var write = NewStaticMethod(
            type,
            "Write",
            MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.Int32));
        write.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        write.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, field));
        write.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        var writeByReference = NewStaticMethod(
            type,
            "WriteByReference",
            MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.Int32));
        writeByReference.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsflda, field));
        writeByReference.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        writeByReference.Body.Instructions.Add(Instruction.Create(OpCodes.Stind_I4));
        writeByReference.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        module.Write(path);
    }

    private static void CreateClosedGenericStaticFixture(string path)
    {
        using var module = NewFixtureModule("GenericStaticFixture");
        var owner = new TypeDefUser(
            "Fixture",
            "GenericStaticProbe",
            module.CorLibTypes.Object.TypeDefOrRef)
        {
            Attributes = dnlib.DotNet.TypeAttributes.Public |
                         dnlib.DotNet.TypeAttributes.BeforeFieldInit
        };
        owner.GenericParameters.Add(new GenericParamUser(0, 0, "T"));
        module.Types.Add(owner);

        var field = new FieldDefUser(
            "Value",
            new FieldSig(module.CorLibTypes.Int32),
            dnlib.DotNet.FieldAttributes.Private | dnlib.DotNet.FieldAttributes.Static);
        owner.Fields.Add(field);

        var constructor = NewStaticMethod(
            owner,
            ".cctor",
            MethodSig.CreateStatic(module.CorLibTypes.Void));
        constructor.Attributes |= dnlib.DotNet.MethodAttributes.Private |
                                  dnlib.DotNet.MethodAttributes.SpecialName |
                                  dnlib.DotNet.MethodAttributes.RTSpecialName;
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_7));
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, field));
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        var probe = AddStaticFixtureType(module, "GenericStaticCallProbe");
        var intOwner = new TypeSpecUser(new GenericInstSig(
            new ClassSig(owner),
            module.CorLibTypes.Int32));
        var stringOwner = new TypeSpecUser(new GenericInstSig(
            new ClassSig(owner),
            module.CorLibTypes.String));
        var intField = new MemberRefUser(
            module,
            "Value",
            new FieldSig(module.CorLibTypes.Int32),
            intOwner);
        var stringField = new MemberRefUser(
            module,
            "Value",
            new FieldSig(module.CorLibTypes.Int32),
            stringOwner);

        var readInt = NewStaticMethod(
            probe,
            "ReadInt",
            MethodSig.CreateStatic(module.CorLibTypes.Int32));
        readInt.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, intField));
        readInt.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        var writeInt = NewStaticMethod(
            probe,
            "WriteInt",
            MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.Int32));
        writeInt.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        writeInt.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, intField));
        writeInt.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        var writeIntByReference = NewStaticMethod(
            probe,
            "WriteIntByReference",
            MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.Int32));
        writeIntByReference.Body.Instructions.Add(
            Instruction.Create(OpCodes.Ldsflda, intField));
        writeIntByReference.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        writeIntByReference.Body.Instructions.Add(Instruction.Create(OpCodes.Stind_I4));
        writeIntByReference.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        var readString = NewStaticMethod(
            probe,
            "ReadString",
            MethodSig.CreateStatic(module.CorLibTypes.Int32));
        readString.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, stringField));
        readString.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        var writeString = NewStaticMethod(
            probe,
            "WriteString",
            MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.Int32));
        writeString.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        writeString.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, stringField));
        writeString.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        module.Write(path);
    }

    private static void CreateCompilerGeneratedRvaFixture(
        string path,
        bool includeUnsafeHandle = false,
        bool includeReadOnlySpan = false,
        bool dataFieldInitOnly = true)
    {
        using var module = NewFixtureModule("CompilerGeneratedRvaFixture");
        var implementationDetails = new TypeDefUser(
            "<PrivateImplementationDetails>{D3BB0858-5252-43D2-AD24-18FBA18F4CEB}",
            "D98C531D-1FFD-4C81-A03D-A69F47A14807",
            module.CorLibTypes.Object.TypeDefOrRef)
        {
            Attributes = dnlib.DotNet.TypeAttributes.NotPublic |
                         dnlib.DotNet.TypeAttributes.BeforeFieldInit
        };
        module.Types.Add(implementationDetails);

        var blobType = new TypeDefUser(
            "",
            "2",
            module.CorLibTypes.GetTypeRef("System", "ValueType"))
        {
            Attributes = dnlib.DotNet.TypeAttributes.NestedPrivate |
                         dnlib.DotNet.TypeAttributes.ExplicitLayout |
                         dnlib.DotNet.TypeAttributes.Sealed,
            PackingSize = 1,
            ClassSize = 8
        };
        implementationDetails.NestedTypes.Add(blobType);

        var dataField = new FieldDefUser(
            "3",
            new FieldSig(new ValueTypeSig(blobType)),
            dnlib.DotNet.FieldAttributes.Assembly |
            dnlib.DotNet.FieldAttributes.Static |
            (dataFieldInitOnly
                ? dnlib.DotNet.FieldAttributes.InitOnly
                : (dnlib.DotNet.FieldAttributes)0) |
            dnlib.DotNet.FieldAttributes.HasFieldRVA)
        {
            InitialValue = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]
        };
        implementationDetails.Fields.Add(dataField);

        var scalarDataField = new FieldDefUser(
            "scalar",
            new FieldSig(module.CorLibTypes.Int64),
            dnlib.DotNet.FieldAttributes.Assembly |
            dnlib.DotNet.FieldAttributes.Static |
            dnlib.DotNet.FieldAttributes.InitOnly |
            dnlib.DotNet.FieldAttributes.HasFieldRVA)
        {
            InitialValue = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]
        };
        implementationDetails.Fields.Add(scalarDataField);

        var bytesField = new FieldDefUser(
            "4",
            new FieldSig(new SZArraySig(module.CorLibTypes.Byte)),
            dnlib.DotNet.FieldAttributes.Assembly | dnlib.DotNet.FieldAttributes.Static);
        implementationDetails.Fields.Add(bytesField);

        var constructor = NewStaticMethod(
            implementationDetails,
            ".cctor",
            MethodSig.CreateStatic(module.CorLibTypes.Void));
        constructor.Attributes |= dnlib.DotNet.MethodAttributes.SpecialName |
                                  dnlib.DotNet.MethodAttributes.RTSpecialName;
        var importer = new Importer(module, ImporterOptions.TryToUseTypeDefs);
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_8));
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Newarr, module.CorLibTypes.Byte));
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Dup));
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldtoken, dataField));
        constructor.Body.Instructions.Add(Instruction.Create(
            OpCodes.Call,
            importer.Import(typeof(System.Runtime.CompilerServices.RuntimeHelpers).GetMethod(
                nameof(System.Runtime.CompilerServices.RuntimeHelpers.InitializeArray),
                [typeof(Array), typeof(RuntimeFieldHandle)])!)));
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, bytesField));
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_8));
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Newarr, module.CorLibTypes.Byte));
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Dup));
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldtoken, scalarDataField));
        constructor.Body.Instructions.Add(Instruction.Create(
            OpCodes.Call,
            importer.Import(typeof(System.Runtime.CompilerServices.RuntimeHelpers).GetMethod(
                nameof(System.Runtime.CompilerServices.RuntimeHelpers.InitializeArray),
                [typeof(Array), typeof(RuntimeFieldHandle)])!)));
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        var read = NewStaticMethod(
            implementationDetails,
            "Read",
            MethodSig.CreateStatic(new SZArraySig(module.CorLibTypes.Byte)));
        read.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, bytesField));
        read.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        if (includeUnsafeHandle)
        {
            var runtimeFieldHandle = new Importer(module, ImporterOptions.TryToUseTypeDefs)
                .Import(typeof(RuntimeFieldHandle))
                .ToTypeSig();
            var getHandle = NewStaticMethod(
                implementationDetails,
                "GetHandle",
                MethodSig.CreateStatic(runtimeFieldHandle));
            getHandle.Body.Instructions.Add(Instruction.Create(OpCodes.Ldtoken, dataField));
            getHandle.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        }

        if (includeReadOnlySpan)
        {
            var readOnlySpanType = new Importer(module, ImporterOptions.TryToUseTypeDefs)
                .Import(typeof(ReadOnlySpan<byte>))
                .ToTypeSig();
            var readOnlySpanConstructor = typeof(ReadOnlySpan<byte>)
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(candidate =>
                {
                    var parameters = candidate.GetParameters();
                    return parameters.Length == 2 &&
                           parameters[0].ParameterType.IsPointer &&
                           parameters[1].ParameterType == typeof(int);
                });
            var readOnly = NewStaticMethod(
                implementationDetails,
                "ReadOnlySpan",
                MethodSig.CreateStatic(readOnlySpanType));
            readOnly.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsflda, dataField));
            readOnly.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_8));
            readOnly.Body.Instructions.Add(Instruction.Create(
                OpCodes.Newobj,
                new Importer(module, ImporterOptions.TryToUseTypeDefs)
                    .Import(readOnlySpanConstructor)));
            readOnly.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        }
        module.Write(path);
    }

    private static void CreateNativeCallbackFixture(
        string path,
        bool includeDynamicHook = false)
    {
        var assetPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            "XStraightMeter.png");
        File.WriteAllBytes(assetPath, [0x89, 0x50, 0x4E, 0x47]);
        using var module = NewFixtureModule("NativeCallbackFixture");
        var type = AddStaticFixtureType(module, "NativeCallbackProbe");
        var importer = new Importer(module, ImporterOptions.TryToUseTypeDefs);
        var value = new FieldDefUser(
            "Value",
            new FieldSig(module.CorLibTypes.Int32),
            dnlib.DotNet.FieldAttributes.Private | dnlib.DotNet.FieldAttributes.Static);
        type.Fields.Add(value);

        var callback = NewStaticMethod(
            type,
            "Callback",
            MethodSig.CreateStatic(module.CorLibTypes.Int32, module.CorLibTypes.Int32));
        var callbackBody = callback.Body!;
        var normalCallback = Instruction.Create(OpCodes.Ldarg_0);
        var assetReady = Instruction.Create(OpCodes.Ldarg_0);
        callbackBody.Instructions.Add(Instruction.Create(OpCodes.Ldstr, assetPath));
        callbackBody.Instructions.Add(Instruction.Create(
            OpCodes.Call,
            importer.Import(typeof(File).GetMethod(nameof(File.Exists), [typeof(string)])!)));
        callbackBody.Instructions.Add(Instruction.Create(OpCodes.Brtrue_S, assetReady));
        callbackBody.Instructions.Add(Instruction.Create(
            OpCodes.Ldstr,
            "custom error-meter sprite is unavailable"));
        callbackBody.Instructions.Add(Instruction.Create(
            OpCodes.Newobj,
            importer.Import(typeof(FileNotFoundException).GetConstructor([typeof(string)])!)));
        callbackBody.Instructions.Add(Instruction.Create(OpCodes.Throw));
        callbackBody.Instructions.Add(assetReady);
        callbackBody.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        callbackBody.Instructions.Add(Instruction.Create(OpCodes.Bge_S, normalCallback));
        callbackBody.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "native callback failure"));
        callbackBody.Instructions.Add(Instruction.Create(
            OpCodes.Newobj,
            importer.Import(typeof(InvalidOperationException).GetConstructor([typeof(string)])!)));
        callbackBody.Instructions.Add(Instruction.Create(OpCodes.Throw));
        callbackBody.Instructions.Add(normalCallback);
        callbackBody.Instructions.Add(Instruction.Create(OpCodes.Stsfld, value));
        callbackBody.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, value));
        callbackBody.Instructions.Add(Instruction.Create(OpCodes.Ret));
        var hookConstructor = typeof(UnmanagedHookAttribute).GetConstructor(
            [typeof(string), typeof(string), typeof(string)])!;
        callback.CustomAttributes.Add(new CustomAttribute(
            (ICustomAttributeType)importer.Import(hookConstructor),
            [
                new CAArgument(module.CorLibTypes.String, "Assembly-CSharp.dll"),
                new CAArgument(module.CorLibTypes.String, "FixtureTarget"),
                new CAArgument(module.CorLibTypes.String, "Callback")
            ]));

        var dispatch = NewStaticMethod(
            type,
            "Dispatch",
            MethodSig.CreateStatic(module.CorLibTypes.Int32, module.CorLibTypes.Int32));
        dispatch.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        dispatch.Body.Instructions.Add(Instruction.Create(OpCodes.Call, callback));
        dispatch.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        var hook = typeof(HookHelper).GetMethod(
            nameof(HookHelper.Hook),
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(nint), typeof(nint)],
            modifiers: null)!;

        var install = NewStaticMethod(
            type,
            "Install",
            MethodSig.CreateStatic(module.CorLibTypes.Void));
        install.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 0x1000));
        install.Body.Instructions.Add(Instruction.Create(OpCodes.Conv_I));
        install.Body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, dispatch));
        install.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
        var hookCall = Instruction.Create(OpCodes.Call, importer.Import(hook));
        install.Body.Instructions.Add(Instruction.Create(OpCodes.Brtrue_S, hookCall));
        install.Body.Instructions.Add(Instruction.Create(OpCodes.Nop));
        install.Body.Instructions.Add(hookCall);
        install.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        install.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        if (includeDynamicHook)
        {
            var dynamicInstall = NewStaticMethod(
                type,
                "InstallDynamic",
                MethodSig.CreateStatic(module.CorLibTypes.Void));
            dynamicInstall.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 0x2000));
            dynamicInstall.Body.Instructions.Add(Instruction.Create(OpCodes.Conv_I));
            dynamicInstall.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 0x3000));
            dynamicInstall.Body.Instructions.Add(Instruction.Create(OpCodes.Conv_I));
            dynamicInstall.Body.Instructions.Add(Instruction.Create(
                OpCodes.Call,
                importer.Import(hook)));
            dynamicInstall.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
            dynamicInstall.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        }
        module.Write(path);
    }

    private static void RunNativeCallbackScenario(
        string assemblyPath,
        ModRuntimeSession session,
        ModRuntimeKey key,
        CountingHookProvider hook)
    {
        var context = new NativeModAssemblyLoadContext(
            "native-callback-fixture",
            assemblyPath);
        try
        {
            using var image = new MemoryStream(File.ReadAllBytes(assemblyPath));
            var assembly = context.LoadFromStream(image);
            var type = assembly.GetType("Fixture.NativeCallbackProbe", true)!;
            var installMethod = type.GetMethod(
                "Install",
                BindingFlags.Public | BindingFlags.Static)!;
            var callbackMethod = type.GetMethod(
                "Dispatch",
                BindingFlags.Public | BindingFlags.Static)!;

            using (HookHelper.EnterOwnerScope(key.OwnerId, session, key))
                installMethod.Invoke(null, null);

            Assert.That(hook.HookCalls, Is.EqualTo(1));
            Assert.That(callbackMethod.Invoke(null, [41]), Is.EqualTo(41));
            var callbackFailures = new List<string>();
            void CaptureFailure(Logger.Level level, string tag, string message)
            {
                if (level == Logger.Level.Error && tag == "NativeModCallback")
                    callbackFailures.Add(message);
            }

            Logger.OnLog += CaptureFailure;
            try
            {
                Assert.That(callbackMethod.Invoke(null, [-1]), Is.EqualTo(0));
                Assert.That(callbackMethod.Invoke(null, [-1]), Is.EqualTo(0));
                Assert.That(callbackMethod.Invoke(null, [-1]), Is.EqualTo(0));
            }
            finally
            {
                Logger.OnLog -= CaptureFailure;
            }
            Assert.Multiple(() =>
            {
                Assert.That(callbackFailures, Has.Count.EqualTo(2));
                Assert.That(callbackFailures[0], Does.Contain($"owner={key.OwnerId}"));
                Assert.That(callbackFailures[0], Does.Contain("NativeCallbackProbe.Dispatch"));
                Assert.That(callbackFailures[0], Does.Contain("repeated=1"));
                Assert.That(callbackFailures[1], Does.Contain("repeated=2"));
            });

            using (HookHelper.EnterOwnerScope(key.OwnerId, session, key))
                Assert.That(HookHelper.Unhook((nint)0x1000), Is.True);
            Assert.That(session.TryBeginRetirement(key), Is.True);
            Assert.That(callbackMethod.Invoke(null, [41]), Is.EqualTo(0));
            Assert.That(session.WaitForQuiescence(key, TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(session.TryCompleteRetirement(key), Is.True);
        }
        finally
        {
            context.Unload();
        }
    }

    private static void CreateAsyncFixture(string path)
    {
        using var module = NewFixtureModule("AsyncFixture");
        var type = AddStaticFixtureType(module, "AsyncProbe");
        var importer = new Importer(module);
        var taskType = importer.Import(typeof(Task)).ToTypeSig();
        var actionType = importer.Import(typeof(Action)).ToTypeSig();
        var method = NewStaticMethod(
            type,
            "Start",
            MethodSig.CreateStatic(taskType, actionType));
        var run = typeof(Task).GetMethod(
            nameof(Task.Run),
            BindingFlags.Public | BindingFlags.Static,
            null,
            [typeof(Action)],
            null)!;
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, importer.Import(run)));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        var taskOfIntType = importer.Import(typeof(Task<int>)).ToTypeSig();
        var echo = NewStaticMethod(
            type,
            "Echo",
            MethodSig.CreateStatic(taskOfIntType, taskOfIntType));
        echo.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        echo.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        module.Write(path);
    }

    private static void CreateValueTaskFixture(string path)
    {
        using var module = NewFixtureModule("ValueTaskFixture");
        var type = AddStaticFixtureType(module, "ValueTaskProbe");
        var importer = new Importer(module);
        var valueTaskType = importer.Import(typeof(ValueTask)).ToTypeSig();
        var method = NewStaticMethod(
            type,
            "Start",
            MethodSig.CreateStatic(valueTaskType));
        var local = new Local(valueTaskType);
        method.Body.Variables.Add(local);
        method.Body.InitLocals = true;
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, local));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        module.Write(path);
    }

    private static ModuleDefUser NewFixtureModule(string assemblyName)
    {
        var module = new ModuleDefUser(
            assemblyName + ".dll",
            Guid.NewGuid(),
            new AssemblyRefUser(new AssemblyNameInfo(typeof(object).Assembly.GetName().FullName)))
        {
            Kind = ModuleKind.Dll
        };
        new AssemblyDefUser(assemblyName, new Version(1, 0, 0, 0)).Modules.Add(module);
        return module;
    }

    private static TypeDef AddStaticFixtureType(ModuleDef module, string name)
    {
        var type = new TypeDefUser(
            "Fixture",
            name,
            module.CorLibTypes.Object.TypeDefOrRef)
        {
            Attributes = dnlib.DotNet.TypeAttributes.Public |
                         dnlib.DotNet.TypeAttributes.Abstract |
                         dnlib.DotNet.TypeAttributes.Sealed
        };
        module.Types.Add(type);
        return type;
    }

    private static void RunAsyncDomainScenario(string assemblyPath)
    {
        var context = new NativeModAssemblyLoadContext("async-domain-fixture", assemblyPath);
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, "async-domain-fixture");
        Assert.That(session.TryPublishActive(key), Is.True);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var method = assembly.GetType("Fixture.AsyncProbe", throwOnError: true)!
                .GetMethod("Start", BindingFlags.Public | BindingFlags.Static)!;
            var echo = assembly.GetType("Fixture.AsyncProbe", throwOnError: true)!
                .GetMethod("Echo", BindingFlags.Public | BindingFlags.Static)!;
            Task task;
            using (HookHelper.EnterOwnerScope(key.OwnerId, session, key))
            {
                var completed = Task.FromResult(7);
                var echoed = (Task<int>)echo.Invoke(null, new object[] { completed })!;
                Assert.That(echoed, Is.SameAs(completed));
                Assert.That(echoed.GetAwaiter().GetResult(), Is.EqualTo(7));
                task = (Task)method.Invoke(
                    null,
                    new object[]
                    {
                        (Action)(() =>
                        {
                            Assert.That(HookHelper.CurrentRuntimeKey.Matches(key), Is.True);
                            entered.Set();
                            release.Wait(TimeSpan.FromSeconds(5));
                        })
                    })!;
            }

            Assert.That(entered.Wait(TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(session.Snapshot().ActiveOperations, Is.EqualTo(2));
            Assert.That(session.TryBeginRetirement(key), Is.True);
            Assert.That(session.WaitForQuiescence(key, TimeSpan.Zero), Is.False);
            release.Set();
            Assert.DoesNotThrow(() => task.GetAwaiter().GetResult());
            Assert.That(session.WaitForQuiescence(key, TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(session.TryCompleteRetirement(key), Is.True);
        }
        finally
        {
            release.Set();
            if (session.Snapshot().State == ModRuntimeLifecycleState.Active)
            {
                Assert.That(session.TryBeginRetirement(key), Is.True);
                Assert.That(session.WaitForQuiescence(key, TimeSpan.FromSeconds(2)), Is.True);
                Assert.That(session.TryCompleteRetirement(key), Is.True);
            }
            context.Unload();
        }
    }

    private static MethodDef NewStaticMethod(TypeDef type, string name, MethodSig signature)
    {
        var method = new MethodDefUser(
            name,
            signature,
            dnlib.DotNet.MethodImplAttributes.IL | dnlib.DotNet.MethodImplAttributes.Managed,
            dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static)
        {
            Body = new CilBody()
        };
        type.Methods.Add(method);
        return method;
    }

    private static void RunStaticDomainScenario(string assemblyPath)
    {
        var context = new NativeModAssemblyLoadContext("static-domain-fixture", assemblyPath);
        var sessionA = new ModRuntimeSession();
        var keyA = sessionA.BeginLoad(ModEntry.NativeLoaderKind, "static-domain-a");
        var sessionB = new ModRuntimeSession();
        var keyB = sessionB.BeginLoad(ModEntry.NativeLoaderKind, "static-domain-b");
        try
        {
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var type = assembly.GetType("Fixture.StaticProbe", throwOnError: true)!;
            var read = type.GetMethod("Read", BindingFlags.Public | BindingFlags.Static)!;
            var write = type.GetMethod("Write", BindingFlags.Public | BindingFlags.Static)!;
            var writeByReference = type.GetMethod(
                "WriteByReference",
                BindingFlags.Public | BindingFlags.Static)!;

            using (HookHelper.EnterOwnerScope(keyA.OwnerId, sessionA, keyA))
            {
                Assert.That(read.Invoke(null, null), Is.EqualTo(7));
                write.Invoke(null, [11]);
                writeByReference.Invoke(null, [19]);
                Assert.That(read.Invoke(null, null), Is.EqualTo(19));
            }
            using (HookHelper.EnterOwnerScope(keyB.OwnerId, sessionB, keyB))
            {
                Assert.That(read.Invoke(null, null), Is.EqualTo(7));
                write.Invoke(null, [13]);
                Assert.That(read.Invoke(null, null), Is.EqualTo(13));
            }
            using (HookHelper.EnterOwnerScope(keyA.OwnerId, sessionA, keyA))
                Assert.That(read.Invoke(null, null), Is.EqualTo(19));

            var exception = Assert.Throws<TargetInvocationException>(() => read.Invoke(null, null));
            Assert.That(exception!.InnerException, Is.TypeOf<InvalidOperationException>());
        }
        finally
        {
            Assert.That(sessionB.TryAbortLoad(keyB), Is.True);
            Assert.That(sessionA.TryAbortLoad(keyA), Is.True);
            context.Unload();
        }
    }

    private static void RunClosedGenericStaticDomainScenario(string assemblyPath)
    {
        var context = new NativeModAssemblyLoadContext("generic-static-domain-fixture", assemblyPath);
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, "generic-static-domain");
        try
        {
            // Loading from a stream keeps the temporary fixture file deletable on Windows;
            // the assembly and reflection objects remain confined to the helper frame below.
            var image = File.ReadAllBytes(assemblyPath);
            InvokeClosedGenericStaticDomainScenario(context, session, key, image);
        }
        finally
        {
            Assert.That(session.TryAbortLoad(key), Is.True);
            context.Unload();
        }
        CollectContexts();
    }

    private static void InvokeClosedGenericStaticDomainScenario(
        NativeModAssemblyLoadContext context,
        ModRuntimeSession session,
        ModRuntimeKey key,
        byte[] image)
    {
        using var stream = new MemoryStream(image, writable: false);
        var assembly = context.LoadFromStream(stream);
        var type = assembly.GetType("Fixture.GenericStaticCallProbe", throwOnError: true)!;
        var readInt = type.GetMethod("ReadInt", BindingFlags.Public | BindingFlags.Static)!;
        var writeInt = type.GetMethod("WriteInt", BindingFlags.Public | BindingFlags.Static)!;
        var writeIntByReference = type.GetMethod(
            "WriteIntByReference",
            BindingFlags.Public | BindingFlags.Static)!;
        var readString = type.GetMethod(
            "ReadString",
            BindingFlags.Public | BindingFlags.Static)!;
        var writeString = type.GetMethod(
            "WriteString",
            BindingFlags.Public | BindingFlags.Static)!;

        using (HookHelper.EnterOwnerScope(key.OwnerId, session, key))
        {
            Assert.Multiple(() =>
            {
                Assert.That(readInt.Invoke(null, null), Is.EqualTo(7));
                Assert.That(readString.Invoke(null, null), Is.EqualTo(7));
            });
            writeInt.Invoke(null, [11]);
            writeIntByReference.Invoke(null, [19]);
            writeString.Invoke(null, [23]);
            Assert.Multiple(() =>
            {
                Assert.That(readInt.Invoke(null, null), Is.EqualTo(19));
                Assert.That(readString.Invoke(null, null), Is.EqualTo(23));
            });
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "starray-native-rewrite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StArray.ModManager.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException(
            "StArray.ModManager repository root could not be located from the test base directory.");
    }

    private static IReadOnlyDictionary<string, string> LoadAssemblyPathMap(string directory)
    {
        return Directory.EnumerateFiles(directory, "*.dll")
            .ToDictionary(
                path => AssemblyName.GetAssemblyName(path).Name!,
                path => path,
                StringComparer.OrdinalIgnoreCase);
    }

    private static void CollectContexts()
    {
        for (var attempt = 0; attempt < 3; ++attempt)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private sealed class CountingHookProvider : IHook
    {
        public int HookCalls { get; private set; }

        public nint Hook(nint target, nint detour)
        {
            HookCalls++;
            return target + 0x100;
        }

        public bool Unhook(nint target) => true;

        public nint GetFunction(string library, string name) => nint.Zero;
    }
}

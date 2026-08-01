using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

// The verified fixed-op catalog carries a hand-audited signature for every target it covers, which
// is why anything outside it was previously unmappable: the strict native resolver rejects a target
// unless the declared return type and every parameter type match runtime metadata exactly, and the
// importer reads only the MOD assembly.
//
// PcCompatTargetSignatureResolver closes that gap by asking the running game. These tests pin both
// halves of the contract: with no provider the importer behaves exactly as it did before, and with
// one the resolved signature has to survive a consistency check before any rule is emitted.
[NonParallelizable]
public class PcCompatRuntimeResolvedTargetTests
{
    [TearDown]
    public void ClearProvider() => PcCompatTargetSignatureResolver.RegisterProvider(null);

    [Test]
    public void NoRegisteredProviderLeavesTheImportUnchanged()
    {
        Assert.That(PcCompatTargetSignatureResolver.IsProviderRegistered, Is.False);

        var (manifest, _) = ReadSampleManifest();
        var scan = PcCompatStaticPatchScanner.Scan(manifest, targetGameRevision: 143);
        var translation = PcCompatCallbackTranslator.Translate(manifest, scan);

        Assert.Multiple(() =>
        {
            Assert.That(translation.Items, Has.Count.EqualTo(49));
            Assert.That(translation.Rules, Has.Count.EqualTo(32));
            Assert.That(translation.TranslatedCount, Is.EqualTo(31));
            Assert.That(translation.UnsupportedCount, Is.EqualTo(4));
            Assert.That(translation.Items, Has.All.Property(
                nameof(PcCompatCallbackTranslationItem.ResolvedTarget)).Null);
        });

        // Out-of-catalog postfix targets keep the original wording verbatim; there is no provider
        // to blame and no extra detail to report.
        var postfixMisses = OutOfCatalogPostfixMisses(translation);
        Assert.That(postfixMisses, Is.Not.Empty);
        Assert.That(postfixMisses, Has.All.Property(nameof(PcCompatCallbackTranslationItem.Reason))
            .EqualTo("No callback domain mapping is available for this target."));
    }

    [Test]
    public void RegisteredProviderPromotesOutOfCatalogPostfixTargets()
    {
        var (manifest, _) = ReadSampleManifest();
        var scan = PcCompatStaticPatchScanner.Scan(manifest, targetGameRevision: 143);
        var baseline = PcCompatCallbackTranslator.Translate(manifest, scan);
        var expected = OutOfCatalogPostfixMisses(baseline)
            .Select(item => item.TargetType + "." + item.TargetMethod)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Assume.That(expected, Is.Not.Empty);

        var requests = new List<PcCompatTargetSignatureRequest>();
        RegisterResolvingProvider(requests);

        var translation = PcCompatCallbackTranslator.Translate(manifest, scan);
        var promoted = translation.Items
            .Where(item => item.ResolvedTarget != null && item.PatchKind == PcCompatPatchKind.Postfix)
            .ToArray();
        var allPromoted = translation.Items
            .Where(item => item.ResolvedTarget != null)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(
                promoted.Select(item => item.TargetType + "." + item.TargetMethod)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal),
                Is.EqualTo(expected));
            Assert.That(promoted, Has.All.Property(
                nameof(PcCompatCallbackTranslationItem.Status))
                .EqualTo(PcCompatCallbackTranslationStatus.Translated));
            Assert.That(promoted, Has.All.Property(
                nameof(PcCompatCallbackTranslationItem.ManagedDispatchRequired)).True);

            // A managed-event promotion is not a fixed-op translation: no rule is emitted here, the
            // recipe compiler builds it later from ResolvedTarget.
            Assert.That(promoted, Has.All.Property(nameof(PcCompatCallbackTranslationItem.RuleId)).Null);
            Assert.That(translation.Rules, Has.Count.EqualTo(baseline.Rules.Count));

            // The importer asks only about targets it actually needs, and never about a target the
            // catalog already covers.
            Assert.That(
                requests.Select(request => request.TypeName + "." + request.MethodName)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal),
                Is.EqualTo(allPromoted
                    .Select(item => item.TargetType + "." + item.TargetMethod)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)));
        });
    }

    [Test]
    public void RecipeCompilerEmitsRuntimeResolvedManagedEventRules()
    {
        var (manifest, _) = ReadSampleManifest();
        var scan = PcCompatStaticPatchScanner.Scan(manifest, targetGameRevision: 143);
        RegisterResolvingProvider();

        var translation = PcCompatCallbackTranslator.Translate(manifest, scan);
        Assert.That(
            PcCompatRecipeCompiler.TryCompile(manifest, translation, out var recipe, out var recipeError),
            Is.True,
            recipeError);

        var runtimeResolved = recipe.Rules
            .Where(rule => rule.Source == "managed_event:runtime_resolved")
            .ToArray();
        var promoted = translation.Items
            .Where(item => item.ResolvedTarget != null && item.PatchKind == PcCompatPatchKind.Postfix)
            .ToArray();
        var synchronousPrefixes = recipe.Rules
            .Where(rule => rule.Source == "managed_prefix:runtime_resolved")
            .ToArray();
        var promotedPrefixes = translation.Items
            .Where(item => item.ResolvedTarget != null && item.PatchKind == PcCompatPatchKind.Prefix)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(runtimeResolved, Is.Not.Empty);
            Assert.That(runtimeResolved, Has.All.Property(nameof(PcCompatCompiledRule.Op))
                .EqualTo(PcCompatRuleOp.ManagedEventCallback));
            Assert.That(runtimeResolved, Has.All.Property(nameof(PcCompatCompiledRule.Stage))
                .EqualTo(PcCompatRuleStage.AfterOriginal));
            Assert.That(runtimeResolved, Has.All.Matches<PcCompatCompiledRule>(rule =>
                rule.Id.StartsWith("managed_event:", StringComparison.Ordinal)));
            Assert.That(synchronousPrefixes, Has.Length.EqualTo(promotedPrefixes.Length));
            Assert.That(synchronousPrefixes, Has.All.Property(nameof(PcCompatCompiledRule.Op))
                .EqualTo(PcCompatRuleOp.ManagedSynchronousPrefix));
            Assert.That(synchronousPrefixes, Has.All.Property(nameof(PcCompatCompiledRule.Stage))
                .EqualTo(PcCompatRuleStage.BeforeOriginal));
            Assert.That(synchronousPrefixes, Has.All.Matches<PcCompatCompiledRule>(rule =>
                rule.Id.StartsWith("managed_prefix:", StringComparison.Ordinal)));

            // Rules that came from the catalog keep the original source string, so the two
            // signature provenances stay distinguishable in the audit report.
            Assert.That(
                recipe.Rules.Count(rule => rule.Source == "managed_event"),
                Is.EqualTo(18));

            // The whole point: the emitted rule carries the resolved signature verbatim, because
            // that is what the strict native resolver will be matched against.
            foreach (var item in promoted)
            {
                var resolved = item.ResolvedTarget!;
                var rule = runtimeResolved.Single(candidate =>
                    candidate.Id == $"managed_event:{RuleIndexOf(runtimeResolved, candidate)}:" +
                                    $"{item.CallbackType}:{item.CallbackMethod}");
                Assert.That(rule.TargetAssemblyName, Is.EqualTo(resolved.AssemblyName));
                Assert.That(rule.TargetNamespace, Is.EqualTo(resolved.Namespace));
                Assert.That(rule.TargetType, Is.EqualTo(resolved.TypeName));
                Assert.That(rule.TargetMethod, Is.EqualTo(resolved.MethodName));
                Assert.That(rule.TargetIsStatic, Is.EqualTo(resolved.IsStatic));
                Assert.That(rule.TargetReturnType, Is.EqualTo(resolved.ReturnType));
                Assert.That(rule.TargetParameterTypes, Is.EqualTo(resolved.ParameterTypes));
                Assert.That(rule.ParamCount, Is.EqualTo(resolved.ParameterTypes.Count));
                Assert.That(rule.TargetGenericArity, Is.Zero);
            }
        });
    }

    [Test]
    public void PrefixTargetsOutsideTheCatalogResolveForSynchronousDispatch()
    {
        var requests = new List<PcCompatTargetSignatureRequest>();
        RegisterResolvingProvider(requests);

        var translation = TranslateAgainstRealCallback(
            "PcCompatUnitTestAbsentType",
            "Awake",
            PcCompatPatchKind.Prefix);

        var item = translation.Items.Single();
        Assert.Multiple(() =>
        {
            Assert.That(item.Status, Is.EqualTo(PcCompatCallbackTranslationStatus.Translated));
            Assert.That(item.ResolvedTarget, Is.Not.Null);
            Assert.That(item.Reason, Does.Contain("runs synchronously on the original hook thread"));
            Assert.That(requests, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void ProviderFailureAndProviderThrowBothBecomeAudits()
    {
        PcCompatTargetSignatureResolver.RegisterProvider((PcCompatTargetSignatureRequest _,
            out PcCompatResolvedTargetSignature? signature, out string error) =>
        {
            signature = null;
            error = "class not found: RDC";
            return false;
        });
        var refused = TranslateAgainstRealCallback("RDC", "set_auto", PcCompatPatchKind.Postfix)
            .Items.Single();

        PcCompatTargetSignatureResolver.RegisterProvider((PcCompatTargetSignatureRequest _,
            out PcCompatResolvedTargetSignature? signature, out string error)
            => throw new InvalidOperationException("metadata cache is cold"));
        var thrown = TranslateAgainstRealCallback("RDC", "set_auto", PcCompatPatchKind.Postfix)
            .Items.Single();

        Assert.Multiple(() =>
        {
            Assert.That(refused.Status, Is.EqualTo(PcCompatCallbackTranslationStatus.NotMapped));
            Assert.That(refused.Reason, Does.Contain("class not found: RDC"));

            // A host defect must degrade one target, not abort the import of every other patch.
            Assert.That(thrown.Status, Is.EqualTo(PcCompatCallbackTranslationStatus.NotMapped));
            Assert.That(thrown.Reason, Does.Contain("provider threw InvalidOperationException"));
            Assert.That(thrown.Reason, Does.Contain("metadata cache is cold"));
        });
    }

    [Test]
    public void ResolverRejectsASignatureThatAnswersADifferentQuestion()
    {
        var request = new PcCompatTargetSignatureRequest
        {
            TypeName = "scrController",
            MethodName = "Update"
        };

        Assert.Multiple(() =>
        {
            AssertRejected(request, Signature(typeName: "scrPlayer"), "does not match requested scrController");
            AssertRejected(request, Signature(methodName: "LateUpdate"), "does not match requested Update");
            AssertRejected(request, Signature(assemblyName: "   "), "has no assembly name");
            AssertRejected(request, Signature(returnType: ""), "has no return type");
            AssertRejected(
                request,
                Signature(parameterTypes: new[] { "System.Int32", " " }),
                "has an empty parameter type");
        });
    }

    [Test]
    public void ResolverRejectsArityThatDisagreesWithTheAttributeArgumentList()
    {
        var request = new PcCompatTargetSignatureRequest
        {
            TypeName = "scrController",
            MethodName = "Update",
            ArgumentTypeNames = new[] { "System.Int32", "System.Boolean" },
            HasArgumentTypeNames = true
        };

        AssertRejected(
            request,
            Signature(parameterTypes: new[] { "System.Int32" }),
            "has 1 parameters, attribute declared 2");

        // The same arity passes, which is what makes the check a disambiguation guard rather than a
        // blanket refusal of attributes that name their argument types.
        PcCompatTargetSignatureResolver.RegisterProvider((PcCompatTargetSignatureRequest _,
            out PcCompatResolvedTargetSignature? signature, out string error) =>
        {
            signature = Signature(parameterTypes: new[] { "System.Int32", "System.Boolean" });
            error = string.Empty;
            return true;
        });
        Assert.That(
            PcCompatTargetSignatureResolver.TryResolve(request, out var accepted, out var acceptError),
            Is.True,
            acceptError);
        Assert.That(accepted!.ParameterTypes, Has.Count.EqualTo(2));
    }

    [Test]
    public void ResolverReportsSuccessWithoutASignatureAsAFailure()
    {
        PcCompatTargetSignatureResolver.RegisterProvider((PcCompatTargetSignatureRequest _,
            out PcCompatResolvedTargetSignature? signature, out string error) =>
        {
            signature = null;
            error = string.Empty;
            return true;
        });

        Assert.That(
            PcCompatTargetSignatureResolver.TryResolve(
                new PcCompatTargetSignatureRequest { TypeName = "scrController", MethodName = "Update" },
                out var resolved,
                out var error),
            Is.False);
        Assert.That(resolved, Is.Null);
        Assert.That(error, Does.Contain("reported success without a signature"));
    }

    [Test]
    public void RequestCarriesTheAttributeArgumentListWhenTheAuthorWroteOne()
    {
        var requests = new List<PcCompatTargetSignatureRequest>();
        RegisterResolvingProvider(requests);

        TranslateAgainstRealCallback(
            "PcCompatUnitTestAbsentType",
            "Update",
            PcCompatPatchKind.Postfix,
            new[] { "System.Int32" });
        TranslateAgainstRealCallback(
            "PcCompatUnitTestAbsentType",
            "LateUpdate",
            PcCompatPatchKind.Postfix);

        Assert.Multiple(() =>
        {
            Assert.That(requests, Has.Count.EqualTo(2));
            Assert.That(requests[0].HasArgumentTypeNames, Is.True);
            Assert.That(requests[0].ArgumentTypeNames, Is.EqualTo(new[] { "System.Int32" }));

            // An attribute that omitted the argument list must not be reported as declaring an
            // empty one: that would force the host to resolve a zero-parameter overload.
            Assert.That(requests[1].HasArgumentTypeNames, Is.False);
            Assert.That(requests[1].ArgumentTypeNames, Is.Empty);
        });
    }

    // The record the native export writes and the record the Android provider parses are one ABI
    // with no struct to pin it, and neither side can be executed from a Windows test run. Pinning
    // the source contract is the only check available; without it a field added on one side would
    // surface first as a wrong hook target on a real device.
    [Test]
    public void NativeExportAndAndroidProviderAgreeOnTheRecordLayout()
    {
        // FindRepoRoot lands on the StArray.ModManager solution folder, not the outer repo.
        var solutionRoot = FindRepoRoot();
        var native = File.ReadAllText(Path.Combine(
            solutionRoot,
            "Android", "library", "src", "main", "cpp", "core", "pccompat_hook_rules.cpp"));
        var provider = File.ReadAllText(Path.Combine(
            solutionRoot,
            "StArray.ModManager.Android", "PcCompat", "PcCompatAndroidTargetSignature.cs"));
        var host = File.ReadAllText(Path.Combine(
            solutionRoot, "StArray.ModManager.Android", "Managed.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(native, Does.Contain("int modmanager_pccompat_resolve_target_signature("));
            Assert.That(provider, Does.Contain("EntryPoint = \"modmanager_pccompat_resolve_target_signature\""));

            // Six mandatory fields, newline separated, parameters appended after the return type.
            Assert.That(
                native,
                Does.Contain("assembly \\n namespace \\n type \\n method \\n \"static\"|\"instance\" \\n returnType \\n param..."));
            Assert.That(
                provider,
                Does.Contain("// assembly \\n namespace \\n type \\n method \\n static|instance \\n returnType \\n param..."));
            Assert.That(provider, Does.Contain("fields.Length < 6"));
            Assert.That(provider, Does.Contain("fields.Length > 6 ? fields[6..]"));

            // -1 means "the attribute declared no argument list"; anything else narrows the
            // overload set. Both sides have to spell the sentinel the same way.
            Assert.That(native, Does.Contain("declared_param_count >= 0"));
            Assert.That(provider, Does.Contain("request.HasArgumentTypeNames ? request.ArgumentTypeNames.Count : -1"));

            // A provider that is never installed is the same as no provider at all.
            Assert.That(host, Does.Contain("PcCompatAndroidTargetSignature.Install();"));
        });
    }

    private static void AssertRejected(
        PcCompatTargetSignatureRequest request,
        PcCompatResolvedTargetSignature answer,
        string expectedFragment)
    {
        PcCompatTargetSignatureResolver.RegisterProvider((PcCompatTargetSignatureRequest _,
            out PcCompatResolvedTargetSignature? signature, out string error) =>
        {
            signature = answer;
            error = string.Empty;
            return true;
        });

        Assert.That(PcCompatTargetSignatureResolver.TryResolve(request, out var resolved, out var failure), Is.False);
        Assert.That(resolved, Is.Null);
        Assert.That(failure, Does.Contain(expectedFragment));
    }

    private static PcCompatResolvedTargetSignature Signature(
        string assemblyName = "Assembly-CSharp",
        string typeName = "scrController",
        string methodName = "Update",
        string returnType = "System.Void",
        IReadOnlyList<string>? parameterTypes = null)
        => new()
        {
            AssemblyName = assemblyName,
            TypeName = typeName,
            MethodName = methodName,
            IsStatic = false,
            ReturnType = returnType,
            ParameterTypes = parameterTypes ?? Array.Empty<string>()
        };

    // Stands in for the Android host reading live IL2CPP metadata: it answers every request with a
    // deterministic, internally consistent signature so the tests exercise the plumbing rather than
    // a particular game build.
    private static void RegisterResolvingProvider(List<PcCompatTargetSignatureRequest>? requests = null)
        => PcCompatTargetSignatureResolver.RegisterProvider((PcCompatTargetSignatureRequest request,
            out PcCompatResolvedTargetSignature? signature, out string error) =>
        {
            requests?.Add(request);
            signature = new PcCompatResolvedTargetSignature
            {
                AssemblyName = "Assembly-CSharp",
                TypeName = request.TypeName,
                MethodName = request.MethodName,
                IsStatic = false,
                ReturnType = "System.Void",
                ParameterTypes = request.HasArgumentTypeNames
                    ? request.ArgumentTypeNames
                    : Array.Empty<string>()
            };
            error = string.Empty;
            return true;
        });

    private static IReadOnlyList<PcCompatCallbackTranslationItem> OutOfCatalogPostfixMisses(
        PcCompatCallbackTranslationReport translation)
        => translation.Items
            .Where(item =>
                item.Status == PcCompatCallbackTranslationStatus.NotMapped &&
                item.PatchKind == PcCompatPatchKind.Postfix)
            .ToArray();

    private static int RuleIndexOf(IReadOnlyList<PcCompatCompiledRule> rules, PcCompatCompiledRule rule)
        => int.Parse(rule.Id.Split(':')[1]);

    // Runs the translator over a single descriptor whose target/kind/argument list the caller
    // chooses, but whose callback identity is borrowed from a real scanned Jipper patch. The
    // borrowed identity matters: the translator resolves the callback body before it asks about the
    // target, so a made-up callback would short-circuit as Unsupported and never reach the resolver.
    private static PcCompatCallbackTranslationReport TranslateAgainstRealCallback(
        string targetType,
        string targetMethod,
        PcCompatPatchKind kind,
        IReadOnlyList<string>? argumentTypeNames = null)
    {
        var (manifest, _) = ReadSampleManifest();
        var scan = PcCompatStaticPatchScanner.Scan(manifest, targetGameRevision: 143);
        var donor = scan.ActivePatches.First(patch =>
            patch.Kind == PcCompatPatchKind.Postfix &&
            patch.CallbackParameterTypeNames.Count == 0);

        var descriptor = new PcCompatPatchDescriptor
        {
            ModId = manifest.Id,
            TargetType = targetType,
            TargetMethod = targetMethod,
            Kind = kind,
            CallbackType = donor.CallbackType,
            CallbackMethod = donor.CallbackMethod,
            CallbackParameterTypeNames = donor.CallbackParameterTypeNames,
            CallbackAssemblyPath = donor.CallbackAssemblyPath,
            ArgumentTypeNames = argumentTypeNames ?? Array.Empty<string>()
        };

        var singleton = new PcCompatStaticPatchScanReport
        {
            ModId = scan.ModId,
            TargetGameRevision = scan.TargetGameRevision,
            AssembliesScanned = scan.AssembliesScanned,
            Patches = new[] { descriptor }
        };
        return PcCompatCallbackTranslator.Translate(manifest, singleton);
    }

    private static (PcModManifest Manifest, string SampleDir) ReadSampleManifest()
    {
        var repoRoot = FindRepoRoot();
        var sampleDir = Path.Combine(repoRoot, "JipperResourcePack_release");
        Assume.That(Directory.Exists(sampleDir), Is.True, $"missing sample mod dir: {sampleDir}");
        Assert.That(PcModManifestReader.TryRead(sampleDir, out var manifest, out var error), Is.True, error);
        return (manifest, sampleDir);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(TestContext.CurrentContext.WorkDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "xphorror.PcModCompat")) &&
                Directory.Exists(Path.Combine(current.FullName, "StArray.ModManager")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate StArray.ModManager repo root");
    }
}

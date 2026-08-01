using System.Reflection;
using StArray.ModManager.Tests.HarmonyFixtures;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

/// <summary>
/// Drives <c>PcCompatHarmonyAttributeAggregator</c> through the public scanner entry point by
/// scanning the test assembly itself, so the input is real compiler-emitted attribute metadata from
/// <see cref="HarmonyAggregationFixtures"/> rather than a hand-built blob.
/// </summary>
public class PcCompatHarmonyAttributeAggregatorTests
{
    private const string FixtureNamespace = "StArray.ModManager.Tests.HarmonyFixtures.";
    private const string TargetType = FixtureNamespace + "FixtureTarget";

    private static PcCompatStaticPatchScanReport? cachedReport;

    private static PcCompatStaticPatchScanReport Report
    {
        get
        {
            if (cachedReport is not null)
                return cachedReport;

            var assemblyPath = typeof(FixtureTarget).Assembly.Location;
            Assume.That(File.Exists(assemblyPath), Is.True, $"missing test assembly: {assemblyPath}");
            cachedReport = PcCompatStaticPatchScanner.ScanAssemblies("harmony-fixture", [assemblyPath]);
            return cachedReport;
        }
    }

    /// <summary>Descriptors the aggregator produced for one fixture patch class.</summary>
    private static PcCompatPatchDescriptor[] PatchesOf(Type patchClass)
        => Report.Patches
            .Where(patch => patch.Source == "harmony_attribute" && patch.CallbackType == FullName(patchClass))
            .ToArray();

    private static PcCompatPatchDescriptor PatchOf(Type patchClass, string callbackMethod)
        => Report.Patches.Single(patch =>
            patch.Source == "harmony_attribute" &&
            patch.CallbackType == FullName(patchClass) &&
            patch.CallbackMethod == callbackMethod);

    private static string[] IssuesOf(Type patchClass)
        => Report.Issues
            .Where(issue => issue.CallbackType == FullName(patchClass))
            .Select(issue => issue.Code)
            .ToArray();

    /// <summary>Type.FullName already spells nested types with '+', which is the spelling the
    /// scanner builds from metadata.</summary>
    private static string FullName(Type type) => type.FullName!;

    [Test]
    public void ScansTheFixtureAssemblyWithoutTouchingUnrelatedTypes()
    {
        // Every Harmony descriptor and every Harmony issue has to come from the fixture namespace;
        // anything else means the relevance gate leaked into ordinary test code.
        var strayPatches = Report.Patches
            .Where(patch => patch.Source == "harmony_attribute")
            .Select(patch => patch.CallbackType)
            .Where(type => !type.StartsWith(FixtureNamespace, StringComparison.Ordinal))
            .Distinct()
            .ToArray();
        Assert.That(strayPatches, Is.Empty);

        var strayIssues = Report.Issues
            .Where(issue => issue.Code.StartsWith("Harmony", StringComparison.Ordinal))
            .Select(issue => issue.CallbackType ?? "<none>")
            .Where(type => !type.StartsWith(FixtureNamespace, StringComparison.Ordinal))
            .Distinct()
            .ToArray();
        Assert.That(strayIssues, Is.Empty);

        Assert.That(Report.ModId, Is.EqualTo("harmony-fixture"));
        Assert.That(Report.AssembliesScanned, Has.Count.EqualTo(1));
    }

    [Test]
    public void ScanRunsToCompletionInsteadOfEndingOnASwallowedException()
    {
        // ScanAssembly wraps the whole per-assembly pass in a catch, so one escaped exception silently
        // costs every Harmony descriptor while leaving an issue that carries no callback type - which no
        // "is this class handled correctly" assertion in this file would ever look at.
        var aborts = Report.Issues
            .Where(issue => issue.Code is "BadManagedImage" or "MetadataReadFailed" or "AssemblyHasNoMetadata" or "AssemblyNotFound")
            .Select(issue => $"{issue.Code}: {issue.Message}")
            .ToArray();
        Assert.That(aborts, Is.Empty);

        Assert.That(
            Report.Patches.Where(patch => patch.Source == "harmony_attribute"),
            Is.Not.Empty,
            "the fixtures declare patch classes, so a total absence of descriptors is a scanner failure");
    }

    [Test]
    public void EveryAnnotatedFixtureClassIsEitherPatchedOrExplainedByAnIssue()
    {
        // A fixture that produces neither a descriptor nor an issue was dropped without a word. Driving
        // this off reflection rather than a hand-kept list means a newly added fixture cannot go unnoticed.
        var silent = typeof(FixtureTarget).Assembly
            .GetTypes()
            .Where(type => type.Namespace == FixtureNamespace.TrimEnd('.'))
            // Harmony reads [HarmonyDelegate] off a delegate type to bind a detour helper, not to patch
            // the type itself, so a delegate producing nothing is the documented behaviour.
            .Where(type => !typeof(Delegate).IsAssignableFrom(type))
            .Where(IsAnnotatedForHarmony)
            .Select(FullName)
            .Where(name => !Report.Patches.Any(patch => patch.Source == "harmony_attribute" && patch.CallbackType == name)
                        && !Report.Issues.Any(issue => issue.CallbackType == name))
            .ToArray();

        Assert.That(silent, Is.Empty);
    }

    /// <summary>
    /// Mirrors the aggregator's relevance gate: an attribute from HarmonyLib on the type itself or on any
    /// of its methods. Reads attribute data rather than instantiating - VariationMismatchPatch carries an
    /// argument list Harmony's own constructor throws on, which is exactly what it is there to prove.
    /// </summary>
    private static bool IsAnnotatedForHarmony(Type type)
    {
        const string harmonyNamespace = "HarmonyLib";
        static bool IsHarmony(IEnumerable<CustomAttributeData> attributes)
            => attributes.Any(attribute => attribute.AttributeType.Namespace == harmonyNamespace);

        if (IsHarmony(type.GetCustomAttributesData()))
            return true;

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return type.GetMethods(all).Any(method => IsHarmony(method.GetCustomAttributesData()));
    }

    [Test]
    public void HelperNamedPrefixWithoutHarmonyAnnotationsIsNotAPatch()
    {
        Assert.That(PatchesOf(typeof(NotAPatchAtAll)), Is.Empty);
        Assert.That(IssuesOf(typeof(NotAPatchAtAll)), Is.Empty);
    }

    [Test]
    public void MergesClassLevelTargetIntoEveryPatchKind()
    {
        var patches = PatchesOf(typeof(MergedPatch));
        Assert.That(patches, Has.Length.EqualTo(4));
        Assert.That(patches, Has.All.Matches<PcCompatPatchDescriptor>(patch =>
            patch.TargetType == TargetType &&
            patch.TargetMethod == nameof(FixtureTarget.Run) &&
            patch.ModId == "harmony-fixture" &&
            patch.Status == PcCompatPatchStatus.RegisteredOnly &&
            patch.TryingCatch == false));

        Assert.That(
            patches.Select(patch => (patch.CallbackMethod, patch.Kind)),
            Is.EquivalentTo(new[]
            {
                ("Pre", PcCompatPatchKind.Prefix),
                ("Post", PcCompatPatchKind.Postfix),
                ("Trans", PcCompatPatchKind.Transpiler),
                ("Fin", PcCompatPatchKind.Finalizer)
            }));

        // __instance is how a Harmony patch asks for the receiver, same as JAPatch.
        Assert.That(PatchOf(typeof(MergedPatch), "Post").NeedInstance, Is.True);
        Assert.That(PatchOf(typeof(MergedPatch), "Pre").NeedInstance, Is.False);
        Assert.That(IssuesOf(typeof(MergedPatch)), Is.Empty);
    }

    [Test]
    public void DiscoversPatchMethodsByNameConvention()
    {
        var patches = PatchesOf(typeof(ConventionPatch));
        Assert.That(
            patches.Select(patch => (patch.CallbackMethod, patch.Kind)),
            Is.EquivalentTo(new[]
            {
                ("Prefix", PcCompatPatchKind.Prefix),
                ("Postfix", PcCompatPatchKind.Postfix)
            }));
        Assert.That(patches, Has.All.Matches<PcCompatPatchDescriptor>(patch =>
            patch.TargetMethod == nameof(FixtureTarget.Run)));
    }

    [Test]
    public void CombinesClassLevelTypeWithMethodLevelName()
    {
        var patch = PatchOf(typeof(SplitTargetPatch), "ByName");
        Assert.That(patch.TargetType, Is.EqualTo(TargetType));
        Assert.That(patch.TargetMethod, Is.EqualTo(nameof(FixtureTarget.Run)));
        Assert.That(patch.Kind, Is.EqualTo(PcCompatPatchKind.Prefix));
    }

    [Test]
    public void ResolvesEveryNameableMethodType()
    {
        var patches = PatchesOf(typeof(MethodTypePatch));
        Assert.That(IssuesOf(typeof(MethodTypePatch)), Is.Empty);
        Assert.That(patches, Has.All.Matches<PcCompatPatchDescriptor>(patch =>
            patch.TargetType == TargetType && patch.Kind == PcCompatPatchKind.Prefix));

        Assert.That(
            patches.Select(patch => (patch.CallbackMethod, patch.TargetMethod)),
            Is.EquivalentTo(new[]
            {
                ("Getter", "get_Value"),
                ("Setter", "set_Value"),
                ("Ctor", ".ctor"),
                ("Cctor", ".cctor"),
                ("Destructor", "Finalize"),
                ("EventAdd", "add_Fired"),
                ("EventRemove", "remove_Fired"),
                ("OperatorAddition", "op_Addition"),
                ("OperatorComma", "op_Comma")
            }));
    }

    [Test]
    public void DecoratesArgumentTypesWithTheirVariations()
    {
        var patch = PatchOf(typeof(ArgumentVariationPatch), "Pre");
        Assert.That(patch.TargetMethod, Is.EqualTo(nameof(FixtureTarget.Mix)));
        // Ref and out are both byref in metadata, so both spell "T&".
        Assert.That(patch.ArgumentTypeNames, Is.EqualTo(new[]
        {
            "System.Int32",
            "System.String&",
            "System.Int32&",
            "System.Byte*"
        }));
        Assert.That(IssuesOf(typeof(ArgumentVariationPatch)), Is.Empty);
    }

    [Test]
    public void KeepsArgumentTypesWhenNoVariationsAreGiven()
    {
        var patch = PatchOf(typeof(ArgumentTypesPatch), "Pre");
        Assert.That(patch.ArgumentTypeNames, Is.EqualTo(new[] { "System.Int32", "System.String" }));
    }

    [Test]
    public void RejectsWholeClassWhenVariationCountDisagrees()
    {
        Assert.That(IssuesOf(typeof(VariationMismatchPatch)), Is.EqualTo(new[] { "HarmonyArgumentVariationsMismatch" }));
        Assert.That(PatchesOf(typeof(VariationMismatchPatch)), Is.Empty);
    }

    [Test]
    public void RejectsWholeClassWhenAPatchMethodIsNotStatic()
    {
        Assert.That(IssuesOf(typeof(InstancePatch)), Is.EqualTo(new[] { "HarmonyPatchMethodNotStatic" }));
        // The static sibling is collateral damage upstream too: the constructor throws before any
        // patch in the class is applied.
        Assert.That(PatchesOf(typeof(InstancePatch)), Is.Empty);
    }

    [Test]
    public void ReportsBulkPatchingInsteadOfGuessingTargets()
    {
        Assert.That(IssuesOf(typeof(BulkPatch)), Is.EqualTo(new[] { "HarmonyPatchAllUnsupported" }));
        Assert.That(PatchesOf(typeof(BulkPatch)), Is.Empty);
    }

    [Test]
    public void ReportsRuntimeTargetResolution()
    {
        Assert.That(IssuesOf(typeof(DynamicTargetPatch)), Is.EqualTo(new[] { "HarmonyDynamicTargetMethodUnsupported" }));
        Assert.That(PatchesOf(typeof(DynamicTargetPatch)), Is.Empty);

        // Found through the bare-name fallback rather than the attribute.
        var byName = Report.Issues.Single(issue => issue.CallbackType == FullName(typeof(DynamicTargetsPatch)));
        Assert.That(byName.Code, Is.EqualTo("HarmonyDynamicTargetMethodUnsupported"));
        Assert.That(byName.CallbackMethod, Is.EqualTo("TargetMethods"));
        Assert.That(PatchesOf(typeof(DynamicTargetsPatch)), Is.Empty);
    }

    [Test]
    public void ReportsStateMachineAndIndexerTargets()
    {
        // This scanner reads metadata, not a loaded type graph: the state machine attribute lives on
        // the *target* method, which in production belongs to an IL2CPP type with no managed metadata
        // at all. So the static side stays fail-closed here even for a managed iterator; the runtime
        // reflection path does resolve it (see PcCompatHarmonyAccessToolsAbiTests).
        Assert.That(IssuesOf(typeof(EnumeratorPatch)), Is.EqualTo(new[] { "HarmonyEnumeratorTargetUnsupported" }));
        Assert.That(IssuesOf(typeof(AsyncPatch)), Is.EqualTo(new[] { "HarmonyAsyncTargetUnsupported" }));
        Assert.That(IssuesOf(typeof(IndexerPatch)), Is.EqualTo(new[] { "HarmonyIndexerTargetUnsupported" }));
        Assert.That(PatchesOf(typeof(EnumeratorPatch)), Is.Empty);
        Assert.That(PatchesOf(typeof(AsyncPatch)), Is.Empty);
        Assert.That(PatchesOf(typeof(IndexerPatch)), Is.Empty);
    }

    [Test]
    public void ReportsIncompleteAndUnknownTargets()
    {
        Assert.That(IssuesOf(typeof(NoDeclaringTypePatch)), Is.EqualTo(new[] { "HarmonyUndefinedTargetType" }));
        Assert.That(IssuesOf(typeof(NoMethodNamePatch)), Is.EqualTo(new[] { "HarmonyUndefinedTargetMethod" }));
        Assert.That(IssuesOf(typeof(UnknownMethodTypePatch)), Is.EqualTo(new[] { "HarmonyUnknownMethodType" }));
        Assert.That(PatchesOf(typeof(NoDeclaringTypePatch)), Is.Empty);
        Assert.That(PatchesOf(typeof(NoMethodNamePatch)), Is.Empty);
        Assert.That(PatchesOf(typeof(UnknownMethodTypePatch)), Is.Empty);
    }

    [Test]
    public void StillEmitsDescriptorsBehindARuntimePrepareGate()
    {
        var issue = Report.Issues.Single(candidate => candidate.CallbackType == FullName(typeof(PreparedPatch)));
        Assert.That(issue.Code, Is.EqualTo("HarmonyPrepareGateNotEvaluated"));
        Assert.That(issue.CallbackMethod, Is.EqualTo("Ready"));

        // Discovery has to stay faithful: the descriptor exists and records that it is gated.
        var patch = PatchOf(typeof(PreparedPatch), "Pre");
        Assert.That(patch.TargetMethod, Is.EqualTo(nameof(FixtureTarget.Run)));
        Assert.That(patch.Reason, Does.Contain("Gated by Ready at runtime."));
    }

    [Test]
    public void MergesPriorityWithMaxAndCarriesOrderingHints()
    {
        Assert.That(IssuesOf(typeof(OrderingPatch)), Is.Empty);

        var inherited = PatchOf(typeof(OrderingPatch), "Inherited");
        var higher = PatchOf(typeof(OrderingPatch), "Higher");
        var lower = PatchOf(typeof(OrderingPatch), "Lower");

        Assert.That(inherited.Reason, Does.Contain("priority=100."));
        Assert.That(higher.Reason, Does.Contain("priority=300."));
        // Math.Max, not detail-wins: the container's 100 survives a method-level 50.
        Assert.That(lower.Reason, Does.Contain("priority=100."));

        Assert.That(inherited.Reason, Does.Contain("before=fixture.before."));
        Assert.That(inherited.Reason, Does.Contain("after=fixture.after."));
        Assert.Multiple(() =>
        {
            Assert.That(inherited.Priority, Is.EqualTo(100));
            Assert.That(higher.Priority, Is.EqualTo(300));
            Assert.That(lower.Priority, Is.EqualTo(100));
            Assert.That(inherited.Before, Is.EqualTo(new[] { "fixture.before" }));
            Assert.That(inherited.After, Is.EqualTo(new[] { "fixture.after" }));
        });
    }

    [Test]
    public void RecordsThePatchCategory()
    {
        var patch = PatchOf(typeof(CategorisedPatch), "Pre");
        Assert.That(patch.Reason, Does.Contain("category=fixture-category."));
        Assert.That(IssuesOf(typeof(CategorisedPatch)), Is.Empty);
    }

    [Test]
    public void CarriesTheLastReversePatchTargetForward()
    {
        var patches = PatchesOf(typeof(ReversePatchSet));
        Assert.That(patches, Has.Length.EqualTo(2));
        Assert.That(patches, Has.All.Matches<PcCompatPatchDescriptor>(patch =>
            patch.Kind == PcCompatPatchKind.ReversePatch &&
            patch.TargetType == TargetType &&
            patch.TargetMethod == nameof(FixtureTarget.Run)));
        Assert.That(patches.Select(patch => patch.CallbackMethod), Is.EquivalentTo(new[] { "First", "Second" }));
        Assert.That(IssuesOf(typeof(ReversePatchSet)), Is.Empty);
    }

    [Test]
    public void MarksInnerPatchesUnknownSoTheTranslatorRefusesThem()
    {
        Assert.That(IssuesOf(typeof(InnerPatchSet)), Is.EqualTo(new[] { "HarmonyInnerPatchUnsupported" }));

        var patch = PatchOf(typeof(InnerPatchSet), "InnerPrefix");
        // Unknown fails the translator's AllowedPatchKinds check, so the descriptor is recorded
        // without ever becoming a native rule.
        Assert.That(patch.Kind, Is.EqualTo(PcCompatPatchKind.Unknown));
        Assert.That(patch.Reason, Does.Contain("Inner (call-site) patching is unsupported."));
    }

    [Test]
    public void KeepsAnUnresolvableTypeNameAsWritten()
    {
        var patch = PatchOf(typeof(StringTypeNamePatch), "Pre");
        Assert.That(patch.TargetType, Is.EqualTo("Game.Hidden.Type"));
        Assert.That(patch.TargetMethod, Is.EqualTo("DoWork"));
        Assert.That(patch.Kind, Is.EqualTo(PcCompatPatchKind.Prefix));
    }

    [Test]
    public void ReportsPatchClassesPatchAllWouldNeverSee()
    {
        Assert.That(IssuesOf(typeof(MethodOnlyPatch)), Is.EqualTo(new[] { "HarmonyPatchClassNotDiscoverable" }));
        Assert.That(PatchesOf(typeof(MethodOnlyPatch)), Is.Empty);
    }

    [Test]
    public void RefusesToGuessTheMergeOrderOfInheritedClassAttributes()
    {
        // The base class is unambiguous and patches normally.
        var basePatch = PatchOf(typeof(InheritedPatchBase), "BasePrefix");
        Assert.That(basePatch.TargetMethod, Is.EqualTo(nameof(FixtureTarget.Run)));
        Assert.That(IssuesOf(typeof(InheritedPatchBase)), Is.Empty);

        // The subclass is discoverable through inherit: true, but which side of the merge wins is
        // runtime-defined, so nothing is emitted.
        Assert.That(
            IssuesOf(typeof(InheritedPatchDerived)),
            Is.EqualTo(new[] { "HarmonyInheritedClassAttributeUnsupported" }));
        Assert.That(PatchesOf(typeof(InheritedPatchDerived)), Is.Empty);
    }

    [Test]
    public void ReportsModDefinedAttributeSubclassesWithoutCallingThemUndiscoverable()
    {
        // A subclass of HarmonyPatch does carry an info field, so HasHarmonyAttribute accepts the
        // class - the values just come from constructor IL this scanner will not interpret.
        Assert.That(
            IssuesOf(typeof(DerivedAttributePatch)),
            Is.EqualTo(new[] { "HarmonyDerivedAttributeUnsupported" }));
        Assert.That(PatchesOf(typeof(DerivedAttributePatch)), Is.Empty);
    }

    [Test]
    public void IgnoresHarmonyDelegateTypesWhileStillPatchingTheirHolder()
    {
        var patch = PatchOf(typeof(DelegateHolder), "Pre");
        Assert.That(patch.TargetMethod, Is.EqualTo(nameof(FixtureTarget.Run)));

        var delegateType = FullName(typeof(DelegateHolder.RunDelegate));
        Assert.That(Report.Patches.Where(candidate => candidate.CallbackType == delegateType), Is.Empty);
        Assert.That(Report.Issues.Where(issue => issue.CallbackType == delegateType), Is.Empty);
    }

    [Test]
    public void HarmonyDescriptorsSurviveJsonRoundTripUnderTheV2Schema()
    {
        var json = Report.ToJson();
        Assert.That(Report.FormatVersion, Is.EqualTo("static-patch-scan-v2"));
        Assert.That(json, Does.Contain("\"source\": \"harmony_attribute\""));
    }
}

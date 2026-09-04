using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

/// <summary>
/// Covers the render-component path from the shared catalog through recipe emission to the recipe
/// binary the native hook installer and the managed dispatcher both read.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is a native-and-rewrite contract, not a rendering test. Whether a bound
/// <c>RainGraphic</c> actually receives Unity's <c>OnPopulateMesh</c> and submits correct vertices
/// needs a live IL2CPP runtime and a device; see the design document's non-claims.
/// </para>
/// <para>
/// The reason these are worth pinning: the rule is the only thing that installs the hook, and it does
/// not come from a Harmony patch. JipperKeyViewer references no 0Harmony at all, so there is no patch
/// descriptor, no shim registration and no callback-translation item behind it - if emission silently
/// stopped, the MOD would still load and rewrite clean, and the rain would simply never draw.
/// </para>
/// </remarks>
public sealed class PcCompatManagedRenderComponentTests
{
    [Test]
    public void StaticScannerDiscoversUpdatedRenderLayersByShapeWithoutModNameRules()
    {
        var report = PcCompatStaticPatchScanner.ScanAssemblies(
            "RenamedKeyViewer",
            [typeof(JipperKeyViewer.KeyViewer.RainGraphic).Assembly.Location]);
        var discovered = report.ManagedRenderComponents
            .Where(item => item.ComponentType.StartsWith(
                "JipperKeyViewer.KeyViewer.",
                StringComparison.Ordinal))
            .Select(item => item.ComponentType)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(discovered, Does.Contain("JipperKeyViewer.KeyViewer.RainGraphic"));
            Assert.That(discovered, Does.Contain("JipperKeyViewer.KeyViewer.KeyShapeLayer"));
            Assert.That(discovered, Does.Contain("JipperKeyViewer.KeyViewer.RainLayer"));
            Assert.That(discovered, Does.Contain("JipperKeyViewer.KeyViewer.GhostRainLayer"));
            Assert.That(
                report.ManagedRenderComponents.Select(item => item.ModId).Distinct(),
                Is.EqualTo(new[] { "RenamedKeyViewer" }));
        });
    }

    [Test]
    public void CatalogMatchesAnyOwnedMaskableGraphicOverrideAgainstRawImage()
    {
        Assert.That(
            PcCompatManagedRenderComponentCatalog.TryMatchRuntimeType(
                "ArbitraryKeyViewer",
                typeof(JipperKeyViewer.KeyViewer.RainGraphic),
                out var entry),
            Is.True);

        Assert.Multiple(() =>
        {
            // MaskableGraphic is what the MOD derives, and it is abstract with no OnPopulateMesh
            // override of its own - so it can be neither added as a component nor hooked. RawImage is
            // the concrete subclass that supplies both.
            Assert.That(entry.BaseType, Is.EqualTo("UnityEngine.UI.MaskableGraphic"));
            Assert.That(entry.HostType, Is.EqualTo("UnityEngine.UI.RawImage"));
            Assert.That(entry.HostAssembly, Is.EqualTo("UnityEngine.UI"));
            Assert.That(entry.RenderMethod, Is.EqualTo("OnPopulateMesh"));
            Assert.That(entry.RenderParameterType, Is.EqualTo("UnityEngine.UI.VertexHelper"));
        });
    }

    /// <summary>
    /// Two component types sharing one host callback must produce one rule, because they share one
    /// physical hook - a second rule on the same target would dispatch the callback twice.
    /// </summary>
    [Test]
    public void DistinctHostTargetsCollapseByHostAndMethod()
    {
        var targets = PcCompatManagedRenderComponentCatalog.DistinctHostTargets(
            [RenderDescriptor("First"), RenderDescriptor("Second")]);
        Assert.That(
            targets.Select(entry => entry.HostType + "::" + entry.RenderMethod).Distinct(),
            Has.Exactly(targets.Count).Items);
    }

    [Test]
    public void ScanWithoutDiscoveredComponentsGetsNoRenderTargets()
        => Assert.That(
            PcCompatManagedRenderComponentCatalog.DistinctHostTargets([]),
            Is.Empty);

    /// <summary>
    /// The emitted rule's shape, field by field, because every field is read by a different consumer:
    /// native resolves the target, the ordering plan reads the op, and the dispatcher parses the id.
    /// </summary>
    [Test]
    public void EmittedRuleTargetsTheHostRenderCallback()
    {
        var rule = CompileRenderRules("JipperKeyViewer").Single();

        Assert.Multiple(() =>
        {
            Assert.That(rule.TargetAssemblyName, Is.EqualTo("UnityEngine.UI"));
            Assert.That(rule.TargetNamespace, Is.EqualTo("UnityEngine.UI"));
            Assert.That(rule.TargetType, Is.EqualTo("RawImage"));
            Assert.That(rule.TargetMethod, Is.EqualTo("OnPopulateMesh"));
            Assert.That(rule.TargetIsStatic, Is.False);
            Assert.That(rule.TargetReturnType, Is.EqualTo("System.Void"));
            Assert.That(rule.TargetParameterTypes, Is.EqualTo(new[] { "UnityEngine.UI.VertexHelper" }));
            Assert.That(rule.Stage, Is.EqualTo(PcCompatRuleStage.BeforeOriginal));
            Assert.That(rule.Op, Is.EqualTo(PcCompatRuleOp.ManagedRenderCallback));
            // Complete replacement: the managed override opens with vh.Clear(), so anything the host
            // built first is discarded. That makes SkipOriginal a requirement, not an optimisation.
            Assert.That(rule.RequiredCapabilities, Is.EqualTo(PcCompatCapability.SkipOriginal));
        });
    }

    /// <summary>
    /// The rule id must use its own prefix. A <c>managed_prefix:</c> id would parse successfully as an
    /// ordinary synchronous prefix on the native side and lose the owner prefilter with it - turning
    /// every one of the game's own <c>RawImage</c> mesh rebuilds into a native-to-managed transition.
    /// </summary>
    [Test]
    public void RuleIdUsesTheRenderPrefixSoNativeKeepsTheOwnerFilter()
    {
        var rule = CompileRenderRules("JipperKeyViewer").Single();

        Assert.Multiple(() =>
        {
            Assert.That(rule.Id, Does.StartWith("managed_render:"));
            Assert.That(rule.Id, Does.Not.StartWith("managed_prefix:"));
            // managed_render:<patchId>:<componentType>:<method> - four colon-separated parts, which is
            // what the dispatcher's own parser requires.
            Assert.That(rule.Id.Split(':'), Has.Length.EqualTo(4));
            Assert.That(rule.Id.Split(':')[2], Is.EqualTo("JipperKeyViewer.KeyViewer.RainGraphic"));
            Assert.That(rule.Id.Split(':')[3], Is.EqualTo("OnPopulateMesh"));
        });
    }

    /// <summary>
    /// The rule survives the recipe binary round-trip with its op code intact, and the reader hands it
    /// back flagged as a render callback.
    /// </summary>
    /// <remarks>
    /// This is the seam where a mistake would be silent. The reader skips any rule whose op code it
    /// does not recognise, so an unregistered op would drop the rule with no error - the hook would
    /// install and then dispatch to a patch id the dispatcher has never heard of.
    /// </remarks>
    [Test]
    public void RuleSurvivesTheRecipeBinaryAndIsReadBackAsRenderCallback()
    {
        var manifest = RenderManifest();
        Assert.That(
            PcCompatRecipeCompiler.TryCompile(
                manifest,
                RenderScan(manifest.Id),
                Translation(manifest),
                out var report,
                out var error),
            Is.True,
            error);

        var path = Path.Combine(
            Path.GetTempPath(),
            "pccompat-render-recipe-" + Guid.NewGuid().ToString("N"),
            "recipe.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            PcCompatUiRecipeBinary.Write(path, manifest, report, targetGameRevision: 143);
            Assert.That(
                PcCompatUiRecipeBinary.TryValidate(path, out var validationError),
                Is.True,
                validationError);

            var rules = PcCompatManagedEventRecipeReader.Read(path);
            var render = rules.Where(rule => rule.IsRenderCallback).ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(render, Has.Length.EqualTo(1));
                Assert.That(render[0].TargetType, Is.EqualTo("RawImage"));
                Assert.That(render[0].TargetMethod, Is.EqualTo("OnPopulateMesh"));
                Assert.That(render[0].TargetIsStatic, Is.False);
                Assert.That(render[0].CallbackType, Is.EqualTo("JipperKeyViewer.KeyViewer.RainGraphic"));
                Assert.That(render[0].CallbackMethod, Is.EqualTo("OnPopulateMesh"));
                // Prefix, so it lands in the prefix dispatch path and can suppress the original.
                Assert.That(render[0].PatchKind, Is.EqualTo(PcCompatPatchKind.Prefix));
                Assert.That(render[0].PatchId, Is.Not.Zero);
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    /// <summary>
    /// A MOD with no registered render component gets no render rule, so the hook is never installed
    /// for it.
    /// </summary>
    [Test]
    public void ScanWithoutMatchingShapeEmitsNoRenderRule()
        => Assert.That(CompileRenderRules("SomeOtherMod", includeDescriptor: false), Is.Empty);

    /// <summary>
    /// Native must parse the render prefix and must not accept it as an ordinary prefix id.
    /// </summary>
    /// <remarks>
    /// Asserted against the C++ source because the parser is the enforcement point for the owner
    /// prefilter, and there is no way to exercise it from managed tests. Also pins the flat pointer
    /// set's presence: without <c>is_managed_render_host</c> in the dispatch path the filter is gone
    /// and every game-side call crosses the boundary.
    /// </remarks>
    [Test]
    public void NativeParsesTheRenderRuleIdAndFiltersByOwner()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "Android", "library", "src", "main", "cpp", "core", "pccompat_hook_rules.cpp"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("kRuleOpManagedRenderCallback = 24"));
            Assert.That(source, Does.Contain("kManagedRenderRuleIdPrefix = \"managed_render:\""));
            Assert.That(source, Does.Contain("parse_managed_render_rule_id"));
            Assert.That(source, Does.Contain("is_managed_render_host"));
            Assert.That(
                source,
                Does.Contain("modmanager_pccompat_register_managed_render_host"),
                "the managed bridge needs an export to publish owned host pointers");
            Assert.That(
                source,
                Does.Contain("modmanager_pccompat_clear_managed_render_hosts"),
                "session teardown needs to drop one MOD's pointers without touching another's");

            // The prefilter has to run before the invocation struct is populated, or the saving is
            // only the managed call and not the allocation and copy that precede it.
            var dispatch = source.IndexOf("bool run_managed_prefix_rules", StringComparison.Ordinal);
            Assert.That(dispatch, Is.GreaterThanOrEqualTo(0));
            var body = source[dispatch..source.IndexOf("\n}\n", dispatch, StringComparison.Ordinal)];
            var filter = body.IndexOf("any_dispatchable", StringComparison.Ordinal);
            var structInit = body.IndexOf("invocation.struct_size", StringComparison.Ordinal);
            Assert.That(filter, Is.GreaterThanOrEqualTo(0));
            Assert.That(structInit, Is.GreaterThan(filter),
                "owner prefilter must precede invocation construction");
        });
    }

    /// <summary>
    /// A render rule must force managed dispatch. Without this the rule would make
    /// <c>hasRecipe</c> true for a MOD that has no other rules, and the runtime would report it
    /// "loaded from verified rule recipe" and skip its managed setup entirely - so the hook would fire
    /// into a session that never ran.
    /// </summary>
    [Test]
    public void RenderRuleForcesManagedDispatchInsteadOfTheRecipeOnlyPath()
    {
        var runtime = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "xphorror.PcModCompat", "src", "PcCompatRuntime.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(runtime, Does.Contain("requiresManagedRenderDispatch"));
            Assert.That(
                runtime,
                Does.Contain("PcCompatRuleOp.ManagedRenderCallback"),
                "the runtime must recognise a render rule as requiring managed dispatch");
        });
    }

    private static IReadOnlyList<PcCompatCompiledRule> CompileRenderRules(
        string modId,
        bool includeDescriptor = true)
    {
        var manifest = RenderManifest(modId);
        var scan = includeDescriptor
            ? RenderScan(modId)
            : new PcCompatStaticPatchScanReport { ModId = modId };
        Assert.That(
            PcCompatRecipeCompiler.TryCompile(
                manifest,
                scan,
                Translation(manifest),
                out var report,
                out var error),
            Is.True,
            error);
        return report.Rules
            .Where(rule => rule.Op == PcCompatRuleOp.ManagedRenderCallback)
            .ToArray();
    }

    private static PcCompatCallbackTranslationReport Translation(PcModManifest manifest)
        => PcCompatCallbackTranslator.Translate(
            manifest,
            PcCompatStaticPatchScanner.Scan(manifest, targetGameRevision: 143));

    private static PcCompatStaticPatchScanReport RenderScan(string modId)
        => new()
        {
            ModId = modId,
            ManagedRenderComponents = [RenderDescriptor("JipperKeyViewer.KeyViewer.RainGraphic", modId)]
        };

    private static PcCompatManagedRenderComponentDescriptor RenderDescriptor(
        string componentType,
        string modId = "ArbitraryKeyViewer")
        => new()
        {
            ModId = modId,
            ComponentAssembly = "JipperKeyViewer",
            ComponentType = componentType,
            BaseAssembly = "UnityEngine.UI",
            BaseType = "UnityEngine.UI.MaskableGraphic",
            HostAssembly = "UnityEngine.UI",
            HostType = "UnityEngine.UI.RawImage",
            RenderMethod = "OnPopulateMesh",
            RenderParameterType = "UnityEngine.UI.VertexHelper",
            Reason = "test shape"
        };

    /// <summary>
    /// Borrows JipperResourcePack's payload under a different id. The render rule comes from the
    /// catalog keyed on the MOD id alone, so the assembly contents are irrelevant to it - and using a
    /// real manifest keeps the rest of the recipe pipeline exercised rather than stubbed.
    /// </summary>
    private static PcModManifest RenderManifest(string modId = "JipperKeyViewer")
    {
        var sampleDir = Path.Combine(FindRepoRoot(), "JipperResourcePack_release");
        Assume.That(Directory.Exists(sampleDir), Is.True, $"missing sample mod dir: {sampleDir}");
        Assert.That(PcModManifestReader.TryRead(sampleDir, out var source, out var error), Is.True, error);
        return new PcModManifest
        {
            FolderPath = source.FolderPath,
            Id = modId,
            DisplayName = modId,
            Author = source.Author,
            Version = source.Version,
            AssemblyName = source.AssemblyName,
            EntryMethod = source.EntryMethod,
            Kind = source.Kind,
            JAModAssemblyPath = source.JAModAssemblyPath,
            JAModClassName = source.JAModClassName,
            AssemblyRequireModPath = source.AssemblyRequireModPath,
            Requirements = source.Requirements,
            LoadAfter = source.LoadAfter,
            RawInfoJson = source.RawInfoJson,
            RawJAModInfoJson = source.RawJAModInfoJson
        };
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "xphorror.PcModCompat")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the StArray.ModManager root");
    }
}

using System.Reflection;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class PcCompatVirtualBundleRegistryTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "pccompat-virtual-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "resource_ir.bin"), "test");
        PcCompatVirtualBundleRegistry.RegisterAssetReleaseSink(null);
        PcCompatVirtualBundleRegistry.RemoveMod("virtual.test");
        PcCompatVirtualBundleRegistry.RegisterAssetResolver(null);
        PcCompatVirtualBundleRegistry.RegisterAssetProjectionResolver(null);
        PcCompatVirtualBundleRegistry.RegisterAssetLivenessProbe(null);
        PcCompatVirtualBundleRegistry.RegisterArrayFactory(null);
    }

    [TearDown]
    public void TearDown()
    {
        PcCompatVirtualBundleRegistry.RemoveMod("virtual.test");
        PcCompatVirtualBundleRegistry.RegisterAssetResolver(null);
        PcCompatVirtualBundleRegistry.RegisterAssetProjectionResolver(null);
        PcCompatVirtualBundleRegistry.RegisterAssetLivenessProbe(null);
        PcCompatVirtualBundleRegistry.RegisterArrayFactory(null);
        PcCompatVirtualBundleRegistry.RegisterAssetReleaseSink(null);
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void ResolvesOwnerScopedCapabilityAssetsAndInvalidatesReleasedHandle()
    {
        var proxy = new object();
        var document = CreateDocument(requiredKind: PcCompatResourceIrMaterializationKind.CapabilityReference);
        PcCompatVirtualBundleRegistry.RegisterAssetResolver(request =>
            request.Asset.CapabilityStableId == "font.test"
                ? new PcCompatVirtualAssetResolveResult(PcCompatVirtualAssetResolveStatus.Ready, proxy)
                : new PcCompatVirtualAssetResolveResult(PcCompatVirtualAssetResolveStatus.Unsupported, null));
        PcCompatVirtualBundleRegistry.RegisterArrayFactory((_, values) => values.ToArray());
        PcCompatVirtualBundleRegistry.RegisterSession(
            "virtual.test",
            7,
            _root,
            Path.Combine(_root, "resource_ir.bin"),
            document);

        var handle = PcCompatVirtualBundleRegistry.Acquire(
            "virtual.test",
            7,
            Path.Combine(_root, "Linux", "bundle"));
        Assert.That(
            PcCompatVirtualBundleRegistry.LoadAsset(handle, "Required", "TMPro.TMP_FontAsset"),
            Is.SameAs(proxy));
        var all = (object[])PcCompatVirtualBundleRegistry.LoadAllAssets(handle);
        Assert.That(all, Is.EqualTo(new[] { proxy }));

        PcCompatVirtualBundleRegistry.Release(handle);
        Assert.That(
            () => PcCompatVirtualBundleRegistry.LoadAllAssets(handle),
            Throws.InvalidOperationException.With.Message.Contains("stale"));
    }

    [Test]
    public void RejectsDestroyedRequiredAssetAtVirtualBundleBoundary()
    {
        var proxy = new object();
        PcCompatVirtualBundleRegistry.RegisterAssetResolver(_ =>
            new PcCompatVirtualAssetResolveResult(PcCompatVirtualAssetResolveStatus.Ready, proxy));
        PcCompatVirtualBundleRegistry.RegisterAssetLivenessProbe(_ => false);
        PcCompatVirtualBundleRegistry.RegisterArrayFactory((_, values) => values.ToArray());
        PcCompatVirtualBundleRegistry.RegisterSession(
            "virtual.test",
            13,
            _root,
            Path.Combine(_root, "resource_ir.bin"),
            CreateDocument(PcCompatResourceIrMaterializationKind.CapabilityReference));
        var handle = PcCompatVirtualBundleRegistry.Acquire("virtual.test", 13, "Linux/bundle");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PcCompatVirtualBundleRegistry.LoadAllAssets(handle));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("destroyed Unity object"));
            Assert.That(exception.Message, Does.Contain("name=Required"));
            Assert.That(exception.Message, Does.Contain("type=TMPro.TMP_FontAsset"));
            Assert.That(exception.Message, Does.Contain("res.0123456789abcdef0123456789abcdef"));
            Assert.That(exception.Message, Does.Contain("kind=CapabilityReference"));
            Assert.That(exception.Message, Does.Contain("bundle=vb.0123456789abcdef0123456789abcdef"));
            Assert.That(exception.Message, Does.Contain("source=Linux/bundle"));
            Assert.That(exception.Message, Does.Contain("selected=True"));
        });
    }

    [Test]
    public void RejectsDestroyedRequiredAssetDuringPreparation()
    {
        PcCompatVirtualBundleRegistry.RegisterAssetResolver(_ =>
            new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Ready,
                new object()));
        PcCompatVirtualBundleRegistry.RegisterAssetLivenessProbe(_ => false);
        PcCompatVirtualBundleRegistry.RegisterSession(
            "virtual.test",
            16,
            _root,
            Path.Combine(_root, "resource_ir.bin"),
            CreateDocument(PcCompatResourceIrMaterializationKind.CapabilityReference));

        Assert.That(
            () => PcCompatVirtualBundleRegistry.TryPrepareRequiredAssets(
                "virtual.test",
                16,
                out _),
            Throws.InvalidOperationException.With.Message.Contains("destroyed Unity object"));
    }

    [Test]
    public void OmitsDestroyedOptionalAssetFromLoadAllAssets()
    {
        var required = new object();
        var optional = new object();
        PcCompatVirtualBundleRegistry.RegisterAssetResolver(request =>
            new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Ready,
                request.Asset.Name == "Required" ? required : optional));
        PcCompatVirtualBundleRegistry.RegisterAssetLivenessProbe(asset =>
            !ReferenceEquals(asset, optional));
        PcCompatVirtualBundleRegistry.RegisterArrayFactory((_, values) => values.ToArray());
        PcCompatVirtualBundleRegistry.RegisterSession(
            "virtual.test",
            14,
            _root,
            Path.Combine(_root, "resource_ir.bin"),
            CreateDocument(
                PcCompatResourceIrMaterializationKind.CapabilityReference,
                PcCompatResourceIrMaterializationKind.CapabilityReference));
        var handle = PcCompatVirtualBundleRegistry.Acquire("virtual.test", 14, "Linux/bundle");

        var values = (object[])PcCompatVirtualBundleRegistry.LoadAllAssets(handle);

        Assert.That(values, Is.EqualTo(new[] { required }));
    }

    [Test]
    public void LivenessProbeFailurePreservesSessionReleaseOwnership()
    {
        var proxy = new object();
        IReadOnlyList<object>? released = null;
        PcCompatVirtualBundleRegistry.RegisterAssetResolver(_ =>
            new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Ready,
                proxy,
                ReleaseWithSession: true));
        PcCompatVirtualBundleRegistry.RegisterAssetLivenessProbe(_ =>
            throw new InvalidOperationException("probe unavailable"));
        PcCompatVirtualBundleRegistry.RegisterArrayFactory((_, values) => values.ToArray());
        PcCompatVirtualBundleRegistry.RegisterAssetReleaseSink(values => released = values);
        PcCompatVirtualBundleRegistry.RegisterSession(
            "virtual.test",
            15,
            _root,
            Path.Combine(_root, "resource_ir.bin"),
            CreateDocument(PcCompatResourceIrMaterializationKind.CapabilityReference));
        var handle = PcCompatVirtualBundleRegistry.Acquire("virtual.test", 15, "Linux/bundle");

        Assert.That(
            () => PcCompatVirtualBundleRegistry.LoadAllAssets(handle),
            Throws.InvalidOperationException.With.Message.Contains("liveness probe failed: probe unavailable"));

        PcCompatVirtualBundleRegistry.RemoveMod("virtual.test");
        Assert.That(released, Is.EqualTo(new[] { proxy }));
    }

    [Test]
    public void RequiredAssetPreparationRetriesNestedPendingWithoutPoisoningAsset()
    {
        var proxy = new object();
        var resolverCalls = 0;
        PcCompatVirtualBundleRegistry.RegisterAssetResolver(_ =>
        {
            resolverCalls++;
            if (resolverCalls == 1)
            {
                throw new PcCompatVirtualAssetPendingException(
                    "capability asset is pending id=material.compat.tmp_mobile");
            }
            return new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Ready,
                proxy);
        });
        PcCompatVirtualBundleRegistry.RegisterArrayFactory((_, values) => values.ToArray());
        PcCompatVirtualBundleRegistry.RegisterSession(
            "virtual.test",
            8,
            _root,
            Path.Combine(_root, "resource_ir.bin"),
            CreateDocument(requiredKind: PcCompatResourceIrMaterializationKind.CapabilityReference));

        Assert.That(
            PcCompatVirtualBundleRegistry.TryPrepareRequiredAssets(
                "virtual.test",
                8,
                out var pendingReason),
            Is.False);
        Assert.That(pendingReason, Does.Contain("material.compat.tmp_mobile"));
        Assert.That(
            PcCompatVirtualBundleRegistry.TryPrepareRequiredAssets(
                "virtual.test",
                8,
                out pendingReason),
            Is.True);
        Assert.That(pendingReason, Is.Null);
        Assert.That(resolverCalls, Is.EqualTo(2));

        var handle = PcCompatVirtualBundleRegistry.Acquire("virtual.test", 8, "Linux/bundle");
        Assert.That(
            PcCompatVirtualBundleRegistry.LoadAsset(handle, "Required", "TMPro.TMP_FontAsset"),
            Is.SameAs(proxy));
    }

    [Test]
    public void FailsClosedWhenRequiredAssetHasNoMaterializer()
    {
        PcCompatVirtualBundleRegistry.RegisterAssetResolver(_ =>
            new PcCompatVirtualAssetResolveResult(PcCompatVirtualAssetResolveStatus.Unsupported, null));
        PcCompatVirtualBundleRegistry.RegisterArrayFactory((_, values) => values.ToArray());
        PcCompatVirtualBundleRegistry.RegisterSession(
            "virtual.test",
            9,
            _root,
            Path.Combine(_root, "resource_ir.bin"),
            CreateDocument(requiredKind: PcCompatResourceIrMaterializationKind.MetadataOnly));
        var handle = PcCompatVirtualBundleRegistry.Acquire("virtual.test", 9, "Linux/bundle");

        Assert.That(
            () => PcCompatVirtualBundleRegistry.LoadAllAssets(handle),
            Throws.InvalidOperationException.With.Message.Contains("no materializer"));
    }

    [Test]
    public void PreservesNestedResolverFailureAndAssetContext()
    {
        var document = CreateDocument(
            requiredKind: PcCompatResourceIrMaterializationKind.CapabilityReference);
        PcCompatVirtualBundleRegistry.RegisterAssetResolver(_ =>
            throw new TargetInvocationException(
                "generated proxy invocation failed",
                new InvalidOperationException("Texture2D constructor rejected the call")));
        PcCompatVirtualBundleRegistry.RegisterArrayFactory((_, values) => values.ToArray());
        PcCompatVirtualBundleRegistry.RegisterSession(
            "virtual.test",
            10,
            _root,
            Path.Combine(_root, "resource_ir.bin"),
            document);
        var handle = PcCompatVirtualBundleRegistry.Acquire("virtual.test", 10, "Linux/bundle");

        var exception = Assert.Throws<InvalidOperationException>(
            () => PcCompatVirtualBundleRegistry.LoadAllAssets(handle));

        Assert.That(exception!.Message, Does.Contain("modId=virtual.test"));
        Assert.That(exception.Message, Does.Contain("generation=10"));
        Assert.That(exception.Message, Does.Contain("name=Required"));
        Assert.That(exception.Message, Does.Contain("materializationKind=CapabilityReference"));
        Assert.That(exception.Message, Does.Contain("System.Reflection.TargetInvocationException"));
        Assert.That(exception.Message, Does.Contain("System.InvalidOperationException"));
        Assert.That(exception.Message, Does.Contain("Texture2D constructor rejected the call"));
        Assert.That(exception.Message, Does.Contain("rootCauseBegin"));
    }

    [Test]
    public void ReleasesPrefabThenSpriteAndClonesBeforeTextureDependencies()
    {
        var texture = new object();
        var fontClone = new object();
        var sprite = new object();
        var prefab = new object();
        IReadOnlyList<object>? released = null;
        PcCompatVirtualBundleRegistry.RegisterAssetResolver(request =>
        {
            var proxy = request.Asset.ExpectedType switch
            {
                "UnityEngine.Texture2D" => texture,
                "TMPro.TMP_FontAsset" => fontClone,
                "UnityEngine.Sprite" => sprite,
                "UnityEngine.GameObject" => prefab,
                _ => throw new AssertionException("Unexpected release test type")
            };
            return new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Ready,
                proxy,
                ReleaseWithSession: true);
        });
        PcCompatVirtualBundleRegistry.RegisterArrayFactory((_, values) => values.ToArray());
        PcCompatVirtualBundleRegistry.RegisterAssetReleaseSink(values => released = values);
        PcCompatVirtualBundleRegistry.RegisterSession(
            "virtual.test",
            11,
            _root,
            Path.Combine(_root, "resource_ir.bin"),
            CreateReleaseDocument());
        var handle = PcCompatVirtualBundleRegistry.Acquire("virtual.test", 11, "Linux/bundle");
        PcCompatVirtualBundleRegistry.LoadAllAssets(handle);

        PcCompatVirtualBundleRegistry.RemoveMod("virtual.test");

        Assert.That(released, Is.EqualTo(new[] { prefab, sprite, fontClone, texture }));
    }

    [Test]
    public void ResolvesCachesAndReleasesUniqueOwnerFontProjectionBeforeSource()
    {
        var source = new object();
        var projection = new object();
        var projectionCalls = 0;
        IReadOnlyList<object>? released = null;
        PcCompatVirtualBundleRegistry.RegisterAssetResolver(_ =>
            new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Ready,
                source,
                ReleaseWithSession: true));
        PcCompatVirtualBundleRegistry.RegisterAssetProjectionResolver(request =>
        {
            ++projectionCalls;
            Assert.Multiple(() =>
            {
                Assert.That(request.ModId, Is.EqualTo("virtual.test"));
                Assert.That(request.SessionGeneration, Is.EqualTo(12));
                Assert.That(request.SourceAsset.ExpectedType, Is.EqualTo("TMPro.TMP_FontAsset"));
                Assert.That(request.ExpectedType, Is.EqualTo("UnityEngine.Font"));
            });
            return new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Ready,
                projection,
                ReleaseWithSession: true);
        });
        PcCompatVirtualBundleRegistry.RegisterAssetReleaseSink(values => released = values);
        PcCompatVirtualBundleRegistry.RegisterSession(
            "virtual.test",
            12,
            _root,
            Path.Combine(_root, "resource_ir.bin"),
            CreateDocument(PcCompatResourceIrMaterializationKind.CapabilityReference));
        var handle = PcCompatVirtualBundleRegistry.Acquire("virtual.test", 12, "Linux/bundle");
        Assert.That(
            PcCompatVirtualBundleRegistry.LoadAsset(handle, "Required", "TMPro.TMP_FontAsset"),
            Is.SameAs(source));

        var first = PcCompatVirtualBundleRegistry.ResolvePreferredAsset(
            "virtual.test",
            12,
            "UnityEngine.Font",
            "TMPro.TMP_FontAsset");
        var second = PcCompatVirtualBundleRegistry.ResolvePreferredAsset(
            "virtual.test",
            12,
            "UnityEngine.Font",
            "TMPro.TMP_FontAsset");

        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.EqualTo(PcCompatVirtualAssetResolveStatus.Ready));
            Assert.That(first.Asset, Is.SameAs(projection));
            Assert.That(second.Asset, Is.SameAs(projection));
            Assert.That(projectionCalls, Is.EqualTo(1));
        });
        PcCompatVirtualBundleRegistry.RemoveMod("virtual.test");
        Assert.That(released, Is.EqualTo(new[] { projection, source }));
    }

    [Test]
    public void ResolvesNamedSpriteWithoutFallingBackToAnotherBundleAsset()
    {
        var expected = new object();
        PcCompatVirtualBundleRegistry.RegisterAssetResolver(request =>
            new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Ready,
                request.Asset.Name == "Sprite" ? expected : new object()));
        PcCompatVirtualBundleRegistry.RegisterSession(
            "virtual.test",
            17,
            _root,
            Path.Combine(_root, "resource_ir.bin"),
            CreateReleaseDocument());

        var resolved = PcCompatVirtualBundleRegistry.ResolveNamedAsset(
            "virtual.test",
            17,
            "Sprite",
            "UnityEngine.Sprite");
        var missing = PcCompatVirtualBundleRegistry.ResolveNamedAsset(
            "virtual.test",
            17,
            "Auto",
            "UnityEngine.Sprite");

        Assert.Multiple(() =>
        {
            Assert.That(resolved.Status, Is.EqualTo(PcCompatVirtualAssetResolveStatus.Ready));
            Assert.That(resolved.Asset, Is.SameAs(expected));
            Assert.That(missing.Status, Is.EqualTo(PcCompatVirtualAssetResolveStatus.Unsupported));
            Assert.That(missing.Error, Does.Contain("name=Auto"));
        });
    }

    private static PcCompatResourceIrDocument CreateDocument(
        PcCompatResourceIrMaterializationKind requiredKind,
        PcCompatResourceIrMaterializationKind optionalKind =
            PcCompatResourceIrMaterializationKind.MetadataOnly)
    {
        const string bundleId = "vb.0123456789abcdef0123456789abcdef";
        const string requiredId = "res.0123456789abcdef0123456789abcdef";
        const string optionalId = "res.fedcba9876543210fedcba9876543210";
        return new PcCompatResourceIrDocument
        {
            ModId = "virtual.test",
            TargetUnityVersion = "6000.3.10f1",
            Bundles =
            [
                new PcCompatResourceIrBundle
                {
                    Id = bundleId,
                    CandidateSha256Hex = new string('a', 64),
                    SourceFileName = "bundle",
                    SourceRelativePath = "Linux/bundle",
                    PlatformHint = "Linux",
                    UnityVersion = "6000.3.10f1",
                    LoadPolicy = "ControlledLoad",
                    SelectedForRuntime = true,
                    AssetIds = [requiredId, optionalId]
                }
            ],
            Assets =
            [
                new PcCompatResourceIrAsset
                {
                    Id = requiredId,
                    BundleId = bundleId,
                    Name = "Required",
                    SourceType = "MonoBehaviour",
                    ExpectedType = "TMPro.TMP_FontAsset",
                    Container = "CAB-test",
                    AssetsFileName = "bundle.assets",
                    PathId = 1,
                    TypeId = 114,
                    RequiredByMod = true,
                    MaterializationKind = requiredKind,
                    Compatibility = requiredKind == PcCompatResourceIrMaterializationKind.CapabilityReference
                        ? PcCompatResourceIrCompatibility.Compatible
                        : PcCompatResourceIrCompatibility.Unsupported,
                    CapabilityStableId = requiredKind == PcCompatResourceIrMaterializationKind.CapabilityReference
                        ? "font.test"
                        : string.Empty
                },
                new PcCompatResourceIrAsset
                {
                    Id = optionalId,
                    BundleId = bundleId,
                    Name = "Unused",
                    SourceType = "Texture2D",
                    ExpectedType = "UnityEngine.Texture2D",
                    Container = "CAB-test",
                    AssetsFileName = "bundle.assets",
                    PathId = 2,
                    TypeId = 28,
                    RequiredByMod = false,
                    MaterializationKind = optionalKind,
                    Compatibility = optionalKind == PcCompatResourceIrMaterializationKind.CapabilityReference
                        ? PcCompatResourceIrCompatibility.Compatible
                        : PcCompatResourceIrCompatibility.Unsupported,
                    CapabilityStableId = optionalKind == PcCompatResourceIrMaterializationKind.CapabilityReference
                        ? "texture.test"
                        : string.Empty
                }
            ]
        };
    }

    private static PcCompatResourceIrDocument CreateReleaseDocument()
    {
        const string bundleId = "vb.1123456789abcdef0123456789abcdef";
        const string textureId = "res.1123456789abcdef0123456789abcdef";
        const string fontId = "res.2123456789abcdef0123456789abcdef";
        const string spriteId = "res.3123456789abcdef0123456789abcdef";
        const string prefabId = "res.4123456789abcdef0123456789abcdef";
        var assets = new PcCompatResourceIrAsset[]
        {
            CapabilityAsset(textureId, "Texture", "UnityEngine.Texture2D", "texture.test", clone: false),
            CapabilityAsset(fontId, "Font", "TMPro.TMP_FontAsset", "font.test", clone: true),
            CapabilityAsset(
                spriteId,
                "Sprite",
                "UnityEngine.Sprite",
                "sprite.test",
                clone: false,
                dependencies: [textureId]),
            new()
            {
                Id = prefabId,
                BundleId = bundleId,
                Name = "Prefab",
                SourceType = "GameObject",
                ExpectedType = "UnityEngine.GameObject",
                Container = "CAB-release",
                AssetsFileName = "bundle.assets",
                PathId = 4,
                TypeId = 1,
                RequiredByMod = true,
                MaterializationKind = PcCompatResourceIrMaterializationKind.PrefabGraph,
                Compatibility = PcCompatResourceIrCompatibility.Compatible,
                DependencyIds = [spriteId],
                Prefab = new PcCompatResourceIrPrefabInfo
                {
                    Nodes =
                    [
                        new PcCompatResourceIrPrefabNode
                        {
                            Name = "Prefab",
                            ParentIndex = -1,
                            Transform = new PcCompatResourceIrPrefabTransform(),
                            CanvasRenderer = new PcCompatResourceIrPrefabCanvasRenderer(),
                            Image = new PcCompatResourceIrPrefabImage
                            {
                                Graphic = new PcCompatResourceIrPrefabGraphic(),
                                SpriteAssetId = spriteId
                            }
                        }
                    ]
                }
            }
        };
        return new PcCompatResourceIrDocument
        {
            ModId = "virtual.test",
            TargetUnityVersion = "6000.3.10f1",
            Bundles =
            [
                new PcCompatResourceIrBundle
                {
                    Id = bundleId,
                    CandidateSha256Hex = new string('b', 64),
                    SourceFileName = "bundle",
                    SourceRelativePath = "Linux/bundle",
                    PlatformHint = "Linux",
                    UnityVersion = "6000.3.10f1",
                    LoadPolicy = "ControlledLoad",
                    SelectedForRuntime = true,
                    AssetIds = assets.Select(asset => asset.Id).ToArray()
                }
            ],
            Assets = assets
        };

        PcCompatResourceIrAsset CapabilityAsset(
            string id,
            string name,
            string type,
            string capability,
            bool clone,
            IReadOnlyList<string>? dependencies = null)
            => new()
            {
                Id = id,
                BundleId = bundleId,
                Name = name,
                SourceType = type,
                ExpectedType = type,
                Container = "CAB-release",
                AssetsFileName = "bundle.assets",
                PathId = int.Parse(id.AsSpan(4, 1)),
                TypeId = 1,
                RequiredByMod = true,
                MaterializationKind = PcCompatResourceIrMaterializationKind.CapabilityReference,
                Compatibility = PcCompatResourceIrCompatibility.Compatible,
                CapabilityStableId = capability,
                CloneCapabilityAsset = clone,
                DependencyIds = dependencies ?? Array.Empty<string>()
            };
    }
}

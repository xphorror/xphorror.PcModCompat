using System.Security.Cryptography;
using Xphorror.PcModCompat;
using Xphorror.PcModCompat.Resources;

namespace StArray.ModManager.Tests;

public sealed class PcCompatResourceIrTests
{
    [Test]
    public void DecodesAlpha8IntoWhiteRgbaWithSourceAlpha()
    {
        var rgba = TextureRgbaDecoder.Decode(1, 2, 1, [0, 127]);

        Assert.That(rgba, Is.EqualTo(new byte[]
        {
            255, 255, 255, 0,
            255, 255, 255, 127
        }));
    }

    [Test]
    public void RejectsInvalidTmpFontRecordEnumsBeforePublishingPayload()
    {
        var glyph = new ResourceIrTmpFontGlyph(
            1, 8f, 12f, 0f, 10f, 8f, 0, 0, 8, 12, 1f, 0, 0);
        var character = new ResourceIrTmpFontCharacter(65, 1, 1f, 1);

        Assert.Multiple(() =>
        {
            Assert.Throws<InvalidDataException>(() =>
                ResourceIrTmpFontPayloadBinary.Write(
                    [glyph with { ClassDefinitionType = 5 }],
                    [character]));
            Assert.Throws<InvalidDataException>(() =>
                ResourceIrTmpFontPayloadBinary.Write(
                    [glyph],
                    [character with { ElementType = 2 }]));
        });
    }

    [Test]
    public void DecodesDxt1SolidRedBlock()
    {
        byte[] block = [0x00, 0xF8, 0xE0, 0x07, 0, 0, 0, 0];
        var rgba = TextureRgbaDecoder.Decode(10, 4, 4, block);

        Assert.That(rgba, Has.Length.EqualTo(64));
        for (var offset = 0; offset < rgba.Length; offset += 4)
            Assert.That(rgba.AsSpan(offset, 4).ToArray(), Is.EqualTo(new byte[] { 255, 0, 0, 255 }));
    }

    [Test]
    public void DecodesDxt5SolidOpaqueRedBlock()
    {
        byte[] block =
        [
            255, 0, 0, 0, 0, 0, 0, 0,
            0x00, 0xF8, 0xE0, 0x07, 0, 0, 0, 0
        ];
        var rgba = TextureRgbaDecoder.Decode(12, 4, 4, block);

        for (var offset = 0; offset < rgba.Length; offset += 4)
            Assert.That(rgba.AsSpan(offset, 4).ToArray(), Is.EqualTo(new byte[] { 255, 0, 0, 255 }));
    }

    [Test]
    public void ImportWriterAndRuntimeReaderAgreeOnRgbaTexturePayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "pccompat-ir-schema-" + Guid.NewGuid().ToString("N"));
        try
        {
            var blobs = Path.Combine(root, "resource_ir_blobs");
            Directory.CreateDirectory(blobs);
            var data = new byte[] { 1, 2, 3, 4 };
            var blobPath = Path.Combine(blobs, "texture.rgba32");
            File.WriteAllBytes(blobPath, data);
            var payloadSha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            const string bundleId = "vb.0123456789abcdef0123456789abcdef";
            const string assetId = "res.0123456789abcdef0123456789abcdef";
            var importDocument = new ResourceIrDocument
            {
                ModId = "schema.test",
                TargetUnityVersion = "6000.3.10f1",
                Bundles =
                [
                    new ResourceIrBundle
                    {
                        Id = bundleId,
                        CandidateSha256Hex = new string('a', 64),
                        SourceFileName = "bundle",
                        SourceRelativePath = "Linux/bundle",
                        PlatformHint = "Linux",
                        UnityVersion = "6000.3.10f1",
                        LoadPolicy = "ControlledLoad",
                        SelectedForRuntime = true,
                        AssetIds = [assetId]
                    }
                ],
                Assets =
                [
                    new ResourceIrAsset
                    {
                        Id = assetId,
                        BundleId = bundleId,
                        Name = "Texture",
                        SourceType = "Texture2D",
                        ExpectedType = "UnityEngine.Texture2D",
                        Container = "CAB-test",
                        AssetsFileName = "CAB-test",
                        PathId = 1,
                        TypeId = 28,
                        RequiredByMod = true,
                        MaterializationKind = ResourceIrMaterializationKind.TextureRgba32,
                        Compatibility = ResourceIrCompatibility.Exact,
                        PayloadId = "payload.texture",
                        Texture = new ResourceIrTextureInfo
                        {
                            Width = 1,
                            Height = 1,
                            SourceFormat = 4,
                            MipCount = 1
                        }
                    }
                ],
                Payloads =
                [
                    new ResourceIrPayload
                    {
                        Id = "payload.texture",
                        Kind = "rgba32",
                        RelativePath = "resource_ir_blobs/texture.rgba32",
                        Sha256Hex = payloadSha,
                        Length = data.Length
                    }
                ]
            };
            var irPath = Path.Combine(root, "resource_ir.bin");
            ResourceIrBinary.Write(irPath, importDocument);

            Assert.That(ResourceIrBinary.TryVerifyPayloadFiles(irPath, importDocument, out var importError),
                Is.True, importError);
            Assert.That(PcCompatResourceIr.TryRead(irPath, "schema.test", out var runtimeDocument, out var readError),
                Is.True, readError);
            Assert.That(PcCompatResourceIr.TryVerifyPayloadFiles(irPath, runtimeDocument, out var runtimeError),
                Is.True, runtimeError);
            Assert.That(runtimeDocument.Assets.Single().Texture!.Width, Is.EqualTo(1));

            File.WriteAllBytes(blobPath, [4, 3, 2, 1]);
            Assert.That(PcCompatResourceIr.TryVerifyPayloadFiles(irPath, runtimeDocument, out runtimeError), Is.False);
            Assert.That(runtimeError, Does.Contain("sha256"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ImportAndRuntimeValidatorsRejectGraphicWithoutCanvasRenderer()
    {
        const string bundleId = "vb.abcdefabcdefabcdefabcdefabcdefab";
        const string assetId = "res.abcdefabcdefabcdefabcdefabcdefab";
        var import = new ResourceIrDocument
        {
            ModId = "prefab.invalid",
            TargetUnityVersion = "6000.3.10f1",
            Bundles =
            [
                new ResourceIrBundle
                {
                    Id = bundleId,
                    CandidateSha256Hex = new string('a', 64),
                    SourceFileName = "bundle",
                    SourceRelativePath = "Linux/bundle",
                    PlatformHint = "Linux",
                    UnityVersion = "6000.3.10f1",
                    LoadPolicy = "ControlledLoad",
                    SelectedForRuntime = true,
                    AssetIds = [assetId]
                }
            ],
            Assets =
            [
                new ResourceIrAsset
                {
                    Id = assetId,
                    BundleId = bundleId,
                    Name = "InvalidPrefab",
                    SourceType = "GameObject",
                    ExpectedType = "UnityEngine.GameObject",
                    Container = "CAB-test",
                    AssetsFileName = "bundle.assets",
                    PathId = 1,
                    TypeId = 1,
                    RequiredByMod = true,
                    MaterializationKind = ResourceIrMaterializationKind.PrefabGraph,
                    Compatibility = ResourceIrCompatibility.Compatible,
                    Prefab = new ResourceIrPrefabInfo
                    {
                        Nodes =
                        [
                            new ResourceIrPrefabNode
                            {
                                Name = "InvalidPrefab",
                                Transform = new ResourceIrPrefabTransform(),
                                Image = new ResourceIrPrefabImage
                                {
                                    Graphic = new ResourceIrPrefabGraphic()
                                }
                            }
                        ]
                    }
                }
            ]
        };
        var runtime = new PcCompatResourceIrDocument
        {
            ModId = "prefab.invalid",
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
                    AssetIds = [assetId]
                }
            ],
            Assets =
            [
                new PcCompatResourceIrAsset
                {
                    Id = assetId,
                    BundleId = bundleId,
                    Name = "InvalidPrefab",
                    SourceType = "GameObject",
                    ExpectedType = "UnityEngine.GameObject",
                    Container = "CAB-test",
                    AssetsFileName = "bundle.assets",
                    PathId = 1,
                    TypeId = 1,
                    RequiredByMod = true,
                    MaterializationKind = PcCompatResourceIrMaterializationKind.PrefabGraph,
                    Compatibility = PcCompatResourceIrCompatibility.Compatible,
                    Prefab = new PcCompatResourceIrPrefabInfo
                    {
                        Nodes =
                        [
                            new PcCompatResourceIrPrefabNode
                            {
                                Name = "InvalidPrefab",
                                Transform = new PcCompatResourceIrPrefabTransform(),
                                Image = new PcCompatResourceIrPrefabImage
                                {
                                    Graphic = new PcCompatResourceIrPrefabGraphic()
                                }
                            }
                        ]
                    }
                }
            ]
        };

        Assert.Multiple(() =>
        {
            Assert.That(ResourceIrBinary.TryValidateDocument(import, out var importError), Is.False);
            Assert.That(importError, Does.Contain("prefab graph"));
            Assert.That(PcCompatResourceIr.TryValidateDocument(
                    runtime,
                    "prefab.invalid",
                    out var runtimeError),
                Is.False);
            Assert.That(runtimeError, Does.Contain("prefab graph"));
        });
    }

    [Test]
    public void ExtractsVerifiedTmpMaterialIntoCapabilityBackedIr()
    {
        var repo = FindRepoRoot();
        var modFolder = Path.Combine(repo, "JipperResourcePack_release");
        var bundlePath = Path.Combine(modFolder, "Linux", "jipperresourcepackbundle");
        Assume.That(File.Exists(bundlePath), Is.True, bundlePath);
        var root = Path.Combine(Path.GetTempPath(), "pccompat-material-ir-" + Guid.NewGuid().ToString("N"));
        try
        {
            var candidate = UnityBundleIndexer.IndexFile(bundlePath);
            Assert.That(candidate.IndexSucceeded, Is.True, string.Join("; ", candidate.Warnings));
            const string materialName = "MAPLESTORY_OTF_BOLD SDF Material";
            var report = new ResourceCompileReport
            {
                ModId = "material.test",
                Compatibility = "partial",
                TargetUnityVersion = "6000.3.10f1",
                Candidates = [candidate],
                FeatureGroups =
                [
                    new ResourceFeatureGroup
                    {
                        Id = "material.test",
                        DisplayName = "Material test",
                        SelectedCandidateSha256Hex = candidate.Sha256Hex,
                        SelectedPlatform = candidate.PlatformHint,
                        LoadPolicy = candidate.LoadPolicy,
                        AssetNames = [materialName]
                    }
                ],
                Bindings =
                [
                    new ResourceBinding
                    {
                        FeatureGroupId = "material.test",
                        AssetName = materialName,
                        ExpectedType = "Material",
                        Confidence = AssetBindConfidence.Proven,
                        SourceFieldIdentity = "fixture.material"
                    }
                ]
            };
            var document = ResourceIrCompiler.Build(report, modFolder, Path.Combine(root, "resource_ir_blobs"));
            var material = document.Assets.Single(asset => asset.Name == materialName);

            Assert.Multiple(() =>
            {
                Assert.That(material.MaterializationKind,
                    Is.EqualTo(ResourceIrMaterializationKind.MaterialFromCapabilityShader),
                    string.Join(Environment.NewLine, document.Warnings));
                Assert.That(material.CapabilityStableId, Is.EqualTo("material.compat.tmp_mobile"));
                Assert.That(material.Compatibility, Is.EqualTo(ResourceIrCompatibility.Compatible));
                Assert.That(material.Material, Is.Not.Null);
                Assert.That(material.Material!.Keywords, Does.Contain("UNDERLAY_ON"));
                Assert.That(material.Material.Floats.Any(value =>
                    value.PropertyName == "_UnderlayOffsetX" && value.Value == 1f), Is.True);
                Assert.That(material.Material.Floats.Any(value =>
                    value.PropertyName == "_Bevel"), Is.False);
                Assert.That(material.Material.Colors.Any(value =>
                    value.PropertyName == "_UnderlayColor"), Is.True);
                Assert.That(material.Material.Textures.Any(value =>
                    value.PropertyName == "_MainTex" && value.TextureAssetId.Length != 0), Is.True);
                Assert.That(material.DependencyIds, Is.Not.Empty);
                Assert.That(material.Tags, Does.Contain("dropped-property:_Bevel"));
            });
            foreach (var dependency in material.DependencyIds)
            {
                Assert.That(document.Assets.Single(asset => asset.Id == dependency).MaterializationKind,
                    Is.EqualTo(ResourceIrMaterializationKind.TextureAlpha8));
            }

            var irPath = Path.Combine(root, "resource_ir.bin");
            ResourceIrBinary.Write(irPath, document);
            Assert.That(PcCompatResourceIr.TryRead(
                    irPath,
                    "material.test",
                    out var runtimeDocument,
                    out var error),
                Is.True,
                error);
            Assert.That(runtimeDocument.Assets.Single(asset => asset.Name == materialName).Material,
                Is.Not.Null);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ExtractsJipperStaticTmpFontFromSourceAtlas()
    {
        var repo = FindRepoRoot();
        var modFolder = Path.Combine(repo, "JipperResourcePack_release");
        var bundlePath = Path.Combine(modFolder, "Linux", "jipperresourcepackbundle");
        Assume.That(File.Exists(bundlePath), Is.True, bundlePath);
        var root = Path.Combine(Path.GetTempPath(), "pccompat-tmp-font-ir-" + Guid.NewGuid().ToString("N"));
        try
        {
            var candidate = UnityBundleIndexer.IndexFile(bundlePath);
            Assert.That(candidate.IndexSucceeded, Is.True, string.Join("; ", candidate.Warnings));
            const string fontName = "MAPLESTORY_OTF_BOLD SDF";
            var report = new ResourceCompileReport
            {
                ModId = "tmp-font.test",
                Compatibility = "partial",
                TargetUnityVersion = "6000.3.10f1",
                Candidates = [candidate],
                FeatureGroups =
                [
                    new ResourceFeatureGroup
                    {
                        Id = "font.test",
                        DisplayName = "TMP font test",
                        SelectedCandidateSha256Hex = candidate.Sha256Hex,
                        SelectedPlatform = candidate.PlatformHint,
                        LoadPolicy = candidate.LoadPolicy,
                        AssetNames = [fontName]
                    }
                ],
                Bindings =
                [
                    new ResourceBinding
                    {
                        FeatureGroupId = "font.test",
                        AssetName = fontName,
                        ExpectedType = "TMP_FontAsset",
                        Confidence = AssetBindConfidence.Proven,
                        SourceFieldIdentity = "fixture.font"
                    }
                ]
            };

            var document = ResourceIrCompiler.Build(report, modFolder, Path.Combine(root, "resource_ir_blobs"));
            var font = document.Assets.Single(asset => asset.Name == fontName && asset.RequiredByMod);
            Assert.Multiple(() =>
            {
                Assert.That(font.MaterializationKind,
                    Is.EqualTo(ResourceIrMaterializationKind.TmpFontFromAtlas),
                    string.Join(Environment.NewLine, document.Warnings));
                Assert.That(font.Compatibility, Is.EqualTo(ResourceIrCompatibility.Compatible));
                Assert.That(font.CapabilityStableId, Is.EqualTo("font.adofai.korean"));
                Assert.That(font.CloneCapabilityAsset, Is.True);
                Assert.That(font.TmpFont, Is.Not.Null);
                Assert.That(font.TmpFont!.Face.FamilyName, Is.EqualTo("Maplestory OTF"));
                Assert.That(font.TmpFont.Face.StyleName, Is.EqualTo("Bold"));
                Assert.That(font.TmpFont.AtlasWidth, Is.EqualTo(4096));
                Assert.That(font.TmpFont.AtlasHeight, Is.EqualTo(4096));
                Assert.That(font.TmpFont.AtlasTextureAssetIds, Has.Count.EqualTo(1));
                Assert.That(font.TmpFont.AtlasTextureIndex, Is.Zero);
                Assert.That(font.TmpFont.GlyphCount, Is.GreaterThan(11_000));
                Assert.That(font.TmpFont.CharacterCount, Is.GreaterThan(11_000));
                Assert.That(font.DependencyIds, Does.Contain(font.TmpFont.MaterialAssetId));
                Assert.That(font.DependencyIds, Does.Contain(font.TmpFont.AtlasTextureAssetIds[0]));
            });
            var payload = document.Payloads.Single(value => value.Id == font.PayloadId);
            Assert.That(payload.Kind, Is.EqualTo(ResourceIrTmpFontPayloadBinary.PayloadKind));
            var atlas = document.Assets.Single(value =>
                value.Id == font.TmpFont!.AtlasTextureAssetIds[0]);
            var atlasPayload = document.Payloads.Single(value => value.Id == atlas.PayloadId);
            Assert.Multiple(() =>
            {
                Assert.That(atlas.MaterializationKind,
                    Is.EqualTo(ResourceIrMaterializationKind.TextureAlpha8));
                Assert.That(atlas.Texture!.SourceFormat, Is.EqualTo(1));
                Assert.That(atlasPayload.Kind, Is.EqualTo("alpha8"));
                Assert.That(atlasPayload.Length, Is.EqualTo(4096L * 4096L));
                Assert.That(atlasPayload.RelativePath, Does.EndWith(".alpha8"));
            });
            var payloadPath = Path.Combine(root, payload.RelativePath);
            var decoded = ResourceIrTmpFontPayloadBinary.Read(File.ReadAllBytes(payloadPath));
            Assert.Multiple(() =>
            {
                Assert.That(decoded.Glyphs, Has.Count.EqualTo(font.TmpFont!.GlyphCount));
                Assert.That(decoded.Characters, Has.Count.EqualTo(font.TmpFont.CharacterCount));
                Assert.That(decoded.Glyphs[0].Index, Is.EqualTo(1));
                Assert.That(decoded.Characters[0].Unicode, Is.EqualTo(32));
                Assert.That(decoded.Characters[0].GlyphIndex, Is.EqualTo(1));
                Assert.That(decoded.Characters[0].Scale, Is.GreaterThan(0f));
                Assert.That(decoded.Characters[0].ElementType, Is.EqualTo(1));
            });
            var irPath = Path.Combine(root, "resource_ir.bin");
            ResourceIrBinary.Write(irPath, document);
            Assert.That(PcCompatResourceIr.TryRead(
                    irPath,
                    "tmp-font.test",
                    out var runtimeDocument,
                    out var runtimeError),
                Is.True,
                runtimeError);
            var runtimeFont = runtimeDocument.Assets.Single(value => value.Name == fontName);
            var runtimeAtlas = runtimeDocument.Assets.Single(value => value.Id == atlas.Id);
            Assert.Multiple(() =>
            {
                Assert.That(runtimeFont.MaterializationKind,
                    Is.EqualTo(PcCompatResourceIrMaterializationKind.TmpFontFromAtlas));
                Assert.That(runtimeFont.TmpFont, Is.Not.Null);
                Assert.That(runtimeFont.TmpFont!.Face.FamilyName, Is.EqualTo("Maplestory OTF"));
                Assert.That(runtimeFont.TmpFont.AtlasTextureIndex, Is.Zero);
                Assert.That(runtimeFont.TmpFont.GlyphCount, Is.EqualTo(font.TmpFont!.GlyphCount));
                Assert.That(runtimeFont.TmpFont.CharacterCount, Is.EqualTo(font.TmpFont.CharacterCount));
                Assert.That(runtimeAtlas.MaterializationKind,
                    Is.EqualTo(PcCompatResourceIrMaterializationKind.TextureAlpha8));
            });
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ExtractsProgressBarAsGenericPrefabGraph()
    {
        var repo = FindRepoRoot();
        var modFolder = Path.Combine(repo, "JipperResourcePack_release");
        var bundlePath = Path.Combine(modFolder, "Linux", "jipperresourcepackbundle");
        Assume.That(File.Exists(bundlePath), Is.True, bundlePath);
        var root = Path.Combine(Path.GetTempPath(), "pccompat-prefab-ir-" + Guid.NewGuid().ToString("N"));
        try
        {
            var candidate = UnityBundleIndexer.IndexFile(bundlePath);
            Assert.That(candidate.IndexSucceeded, Is.True, string.Join("; ", candidate.Warnings));
            const string prefabName = "ProgressBar";
            var report = new ResourceCompileReport
            {
                ModId = "prefab.test",
                Compatibility = "partial",
                TargetUnityVersion = "6000.3.10f1",
                Candidates = [candidate],
                FeatureGroups =
                [
                    new ResourceFeatureGroup
                    {
                        Id = "prefab.test",
                        DisplayName = "Prefab test",
                        SelectedCandidateSha256Hex = candidate.Sha256Hex,
                        SelectedPlatform = candidate.PlatformHint,
                        LoadPolicy = candidate.LoadPolicy,
                        AssetNames = [prefabName]
                    }
                ],
                Bindings =
                [
                    new ResourceBinding
                    {
                        FeatureGroupId = "prefab.test",
                        AssetName = prefabName,
                        ExpectedType = "GameObject",
                        Confidence = AssetBindConfidence.Proven,
                        SourceFieldIdentity = "fixture.prefab"
                    }
                ]
            };

            var document = ResourceIrCompiler.Build(report, modFolder, Path.Combine(root, "resource_ir_blobs"));
            var prefab = document.Assets.Single(asset =>
                asset.Name == prefabName && asset.RequiredByMod);
            Assert.Multiple(() =>
            {
                Assert.That(prefab.MaterializationKind, Is.EqualTo(ResourceIrMaterializationKind.PrefabGraph),
                    string.Join(Environment.NewLine, document.Warnings));
                Assert.That(prefab.CapabilityStableId, Is.Empty);
                Assert.That(prefab.Prefab, Is.Not.Null);
                Assert.That(prefab.Prefab!.Nodes, Has.Count.EqualTo(4));
                Assert.That(prefab.Prefab.Nodes[0].Name, Is.EqualTo("ProgressBar"));
                Assert.That(prefab.Prefab.Nodes.Skip(1).Select(node => node.Name),
                    Is.EqualTo(new[] { "borderLine", "background", "line" }));
                Assert.That(prefab.Prefab.Nodes.Skip(1).All(node =>
                    node.Transform.IsRectTransform && node.CanvasRenderer != null && node.Image != null), Is.True);
                Assert.That(prefab.DependencyIds, Has.Count.EqualTo(1));
                Assert.That(prefab.Tags, Does.Contain("prefab-graph-v1"));
            });
            var sprite = document.Assets.Single(asset => asset.Id == prefab.DependencyIds.Single());
            Assert.Multiple(() =>
            {
                Assert.That(sprite.Name, Is.EqualTo("Background"));
                Assert.That(sprite.MaterializationKind, Is.EqualTo(ResourceIrMaterializationKind.SpriteFromTexture));
                Assert.That(sprite.DependencyIds, Has.Count.EqualTo(1));
                Assert.That(document.Assets.Single(asset => asset.Id == sprite.DependencyIds.Single())
                    .MaterializationKind, Is.EqualTo(ResourceIrMaterializationKind.TextureRgba32));
            });

            var irPath = Path.Combine(root, "resource_ir.bin");
            ResourceIrBinary.Write(irPath, document);
            Assert.That(PcCompatResourceIr.TryRead(
                    irPath,
                    "prefab.test",
                    out var runtimeDocument,
                    out var error),
                Is.True,
                error);
            var runtimePrefab = runtimeDocument.Assets.Single(asset =>
                asset.MaterializationKind == PcCompatResourceIrMaterializationKind.PrefabGraph);
            Assert.That(runtimePrefab.Prefab!.Nodes, Has.Count.EqualTo(4));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StArray.ModManager.slnx")))
            {
                var sample = Path.Combine(directory.FullName, "JipperResourcePack_release");
                Assume.That(Directory.Exists(sample), Is.True, $"missing sample mod dir: {sample}");
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("StArray.ModManager repository root not found.");
    }
}

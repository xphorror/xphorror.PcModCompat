using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatManagedFontBridgeTests
{
    [Test]
    public void FontAssetIsAcceptedWhenItsNativeFaceMetricsAreValid()
    {
        var font = new FontAssetProbe(
            new FaceInfoProbe(90f, 1000, 110f),
            missingCharacters: string.Empty);

        Assert.That(PcCompatManagedFontBridge.HasUsableFontFace(font), Is.True);
        Assert.That(font.WarmupCalls, Is.EqualTo(1));
        Assert.That(font.Material.MainTexture, Is.SameAs(font.atlasTexture));
    }

    [Test]
    public void NonNullFontWrapperWithoutAnyGlyphFaceIsRejected()
    {
        var font = new FontAssetProbe(
            new FaceInfoProbe(0f, 0, 0f),
            missingCharacters: string.Empty);

        Assert.That(PcCompatManagedFontBridge.HasUsableFontFace(font), Is.False);
        Assert.That(font.WarmupCalls, Is.Zero);
    }

    [Test]
    public void MetricsOnlyFontWhoseDynamicAtlasCannotMaterializeAnyGlyphIsRejected()
    {
        const string warmup =
            "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        var font = new FontAssetProbe(
            new FaceInfoProbe(90f, 1000, 110f),
            missingCharacters: warmup);

        Assert.That(PcCompatManagedFontBridge.HasUsableFontFace(font), Is.False);
        Assert.That(font.WarmupCalls, Is.EqualTo(1));
    }

    [Test]
    public void PartialDynamicGlyphMaterializationKeepsAUsableFont()
    {
        var font = new FontAssetProbe(
            new FaceInfoProbe(90f, 1000, 110f),
            missingCharacters: "XYZ");

        Assert.That(PcCompatManagedFontBridge.HasUsableFontFace(font), Is.True);
        Assert.That(font.WarmupCalls, Is.EqualTo(1));
    }

    [Test]
    public void WarmupSuccessWithoutACharacterTableIsRejected()
    {
        var font = new FontAssetProbe(
            new FaceInfoProbe(90f, 1000, 110f),
            missingCharacters: string.Empty,
            characterCount: 0);

        Assert.That(PcCompatManagedFontBridge.HasUsableFontFace(font), Is.False);
        Assert.That(font.WarmupCalls, Is.EqualTo(1));
    }

    [TestCase(0, 256, true)]
    [TestCase(256, 0, true)]
    [TestCase(256, 256, false)]
    public void MissingAtlasOrMaterialIsRejected(int atlasWidth, int atlasHeight, bool hasMaterial)
    {
        var font = new FontAssetProbe(
            new FaceInfoProbe(90f, 1000, 110f),
            missingCharacters: string.Empty,
            atlasWidth: atlasWidth,
            atlasHeight: atlasHeight,
            hasMaterial: hasMaterial);

        Assert.That(PcCompatManagedFontBridge.HasUsableFontFace(font), Is.False);
    }

    [Test]
    public void FinalInstanceMaterialBindingRefreshesTheSelectedFontsAtlas()
    {
        var font = new FontAssetProbe(
            new FaceInfoProbe(90f, 1000, 110f),
            missingCharacters: string.Empty);
        var text = new TextProbe(font);
        var material = new MaterialProbe();

        PcCompatManagedFontBridge.SetFontMaterial(text, material);

        Assert.Multiple(() =>
        {
            Assert.That(material.MainTexture, Is.SameAs(font.atlasTexture));
            Assert.That(text.InstanceMaterial, Is.SameAs(material));
            Assert.That(text.SharedMaterial, Is.Null);
        });
    }

    [Test]
    public void FinalFontBindingSynchronizesAtlasMaterialAndMarksTextDirty()
    {
        var previous = new FontAssetProbe(
            new FaceInfoProbe(90f, 1000, 110f),
            missingCharacters: string.Empty);
        var selected = new FontAssetProbe(
            new FaceInfoProbe(90f, 1000, 110f),
            missingCharacters: string.Empty);
        var text = new TextProbe(previous);

        PcCompatManagedFontBridge.SetFont(text, selected);

        Assert.Multiple(() =>
        {
            Assert.That(text.get_font(), Is.SameAs(selected));
            Assert.That(selected.Material.MainTexture, Is.SameAs(selected.atlasTexture));
            Assert.That(text.InstanceMaterial, Is.SameAs(selected.Material));
            Assert.That(text.InstanceSetterCalls, Is.EqualTo(1));
            Assert.That(text.DirtyCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public void MissingOptionalInstanceMaterialDoesNotRejectAnAppliedFont()
    {
        var previous = new FontAssetProbe(
            new FaceInfoProbe(90f, 1000, 110f),
            missingCharacters: string.Empty);
        var selected = new FontAssetProbe(
            new FaceInfoProbe(90f, 1000, 110f),
            missingCharacters: string.Empty);
        var text = new TextProbe(previous, throwOnMaterialGet: true);

        Assert.That(
            () => PcCompatManagedFontBridge.SetFont(text, selected),
            Throws.Nothing);

        Assert.Multiple(() =>
        {
            Assert.That(text.get_font(), Is.SameAs(selected));
            Assert.That(text.FontSetterCalls, Is.EqualTo(1));
            Assert.That(selected.Material.MainTexture, Is.SameAs(selected.atlasTexture));
            Assert.That(text.InstanceSetterCalls, Is.Zero);
            Assert.That(text.DirtyCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public void NullFontPreservesOriginalSetterSemantics()
    {
        var font = new FontAssetProbe(
            new FaceInfoProbe(90f, 1000, 110f),
            missingCharacters: string.Empty);
        var text = new TextProbe(font);

        PcCompatManagedFontBridge.SetFont(text, null);

        Assert.Multiple(() =>
        {
            Assert.That(text.get_font(), Is.Null);
            Assert.That(text.FontSetterCalls, Is.EqualTo(1));
            Assert.That(text.DirtyCalls, Is.Zero);
        });
    }

    [Test]
    public void FinalSharedMaterialBindingRefreshesTheSelectedFontsAtlas()
    {
        var font = new FontAssetProbe(
            new FaceInfoProbe(90f, 1000, 110f),
            missingCharacters: string.Empty);
        var text = new TextProbe(font);
        var material = new MaterialProbe();

        PcCompatManagedFontBridge.SetFontSharedMaterial(text, material);

        Assert.Multiple(() =>
        {
            Assert.That(material.MainTexture, Is.SameAs(font.atlasTexture));
            Assert.That(text.SharedMaterial, Is.SameAs(material));
            Assert.That(text.InstanceMaterial, Is.Null);
        });
    }

    [Test]
    public void NullMaterialPreservesTheOriginalSetterSemantics()
    {
        var font = new FontAssetProbe(
            new FaceInfoProbe(90f, 1000, 110f),
            missingCharacters: string.Empty);
        var text = new TextProbe(font);

        PcCompatManagedFontBridge.SetFontMaterial(text, null);
        PcCompatManagedFontBridge.SetFontSharedMaterial(text, null);

        Assert.Multiple(() =>
        {
            Assert.That(text.InstanceSetterCalls, Is.EqualTo(1));
            Assert.That(text.SharedSetterCalls, Is.EqualTo(1));
            Assert.That(text.InstanceMaterial, Is.Null);
            Assert.That(text.SharedMaterial, Is.Null);
        });
    }

    private sealed class FontAssetProbe(
        FaceInfoProbe face,
        string missingCharacters,
        int characterCount = 1,
        int atlasWidth = 256,
        int atlasHeight = 256,
        bool hasMaterial = true)
    {
        public int WarmupCalls { get; private set; }

        public IReadOnlyList<int> characterTable { get; } =
            Enumerable.Range(0, characterCount).ToArray();

        public TextureProbe atlasTexture { get; } = new(atlasWidth, atlasHeight);

        public MaterialProbe Material { get; } = new();

        public object? material => hasMaterial ? Material : null;

        public FaceInfoProbe get_faceInfo() => face;

        public bool TryAddCharacters(
            string characters,
            out string missing,
            bool includeFontFeatures)
        {
            ++WarmupCalls;
            missing = missingCharacters;
            return missing.Length == 0;
        }
    }

    private sealed class MaterialProbe
    {
        public object? MainTexture { get; private set; }

        public void SetTexture(string propertyName, object texture)
        {
            if (propertyName == "_MainTex")
                MainTexture = texture;
        }
    }

    private sealed class TextProbe(
        FontAssetProbe font,
        bool throwOnMaterialGet = false)
    {
        private FontAssetProbe? _font = font;

        public object? InstanceMaterial { get; private set; }
        public object? SharedMaterial { get; private set; }
        public int FontSetterCalls { get; private set; }
        public int InstanceSetterCalls { get; private set; }
        public int SharedSetterCalls { get; private set; }
        public int DirtyCalls { get; private set; }

        public FontAssetProbe? get_font() => _font;

        public object? get_fontMaterial()
        {
            if (throwOnMaterialGet)
                throw new InvalidOperationException("Optional instance material is not initialized.");
            return InstanceMaterial;
        }

        public void set_font(FontAssetProbe? value)
        {
            ++FontSetterCalls;
            _font = value;
            InstanceMaterial = value?.Material;
        }

        public void set_fontMaterial(object? material)
        {
            ++InstanceSetterCalls;
            InstanceMaterial = material;
        }

        public void set_fontSharedMaterial(object? material)
        {
            ++SharedSetterCalls;
            SharedMaterial = material;
        }

        public void SetAllDirty() => ++DirtyCalls;
    }

    private sealed record TextureProbe(int width, int height);

    private sealed record FaceInfoProbe(float m_PointSize, int m_UnitsPerEM, float m_LineHeight);
}

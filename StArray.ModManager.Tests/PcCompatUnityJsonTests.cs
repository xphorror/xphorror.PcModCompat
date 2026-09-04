using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

/// <summary>
/// Covers the Unity <c>JsonUtility</c> format rules that <see cref="PcCompatUnityJson"/> has to match.
/// </summary>
/// <remarks>
/// The reason these are worth pinning individually: a profile written on PC by the real
/// <c>JsonUtility</c> has to load here, and a profile written here has to load back on PC. Every rule
/// below differs from what a general-purpose JSON serializer would do, so getting one wrong loses user
/// settings silently rather than throwing.
/// </remarks>
public sealed class PcCompatUnityJsonTests
{
    private enum Key
    {
        None = 0,
        Tab = 9,
        Space = 32
    }

    private struct Rgba
    {
        public float r;
        public float g;
        public float b;
        public float a;
    }

    private sealed class Nested
    {
        public int Version = 5;
        public string Name = "Default";
    }

    private sealed class Sample
    {
        public int Count = 3;
        public float Size = 1.5f;
        public bool Enabled = true;
        public string Label = "KPS";
        public Key Binding = Key.Tab;
        public Key[] Keys = [Key.Tab, Key.Space];
        public string[] Text = ["a", "b"];
        public Rgba Color = new() { r = 0.25f, g = 0.5f, b = 0.75f, a = 1f };
        public Rgba[] Palette = [new() { r = 1f, g = 0f, b = 0f, a = 1f }];
        public List<int> Numbers = [1, 2, 3];
        public Nested Data = new();
    }

    private sealed class WithNonPublic
    {
        public int Visible = 1;
        private int _hidden = 2;
        [NonSerialized] public int Skipped = 3;
        public int Property { get; set; } = 4;

        public int ReadHidden() => _hidden;
    }

    private sealed class WithNulls
    {
        public string Label = null!;
        public string[] Items = null!;
        public Nested Data = null!;
    }

    [Test]
    public void EnumsAreWrittenAsIntegersNotNames()
    {
        var json = PcCompatUnityJson.ToJson(new Sample(), prettyPrint: false);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"Binding\":9"));
            Assert.That(json, Does.Contain("\"Keys\":[9,32]"));
            Assert.That(json, Does.Not.Contain("\"Tab\""));
        });
    }

    /// <summary>
    /// Unity serializes fields, never properties, and names them verbatim - no camel-casing. Both are
    /// defaults a general-purpose serializer would get wrong, and either would make a PC-written
    /// profile unreadable.
    /// </summary>
    [Test]
    public void FieldsAreSerializedVerbatimAndPropertiesAreNot()
    {
        var json = PcCompatUnityJson.ToJson(new WithNonPublic(), prettyPrint: false);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"Visible\":1"));
            Assert.That(json, Does.Not.Contain("count"), "names must not be camel-cased");
            Assert.That(json, Does.Not.Contain("\"Property\""), "properties are not serialized");
            Assert.That(json, Does.Not.Contain("_hidden"), "private fields need [SerializeField]");
            Assert.That(json, Does.Not.Contain("\"Skipped\""), "[NonSerialized] is honoured");
            Assert.That(json, Does.Not.Contain("k__BackingField"), "backing fields must not leak");
        });
    }

    /// <summary>
    /// Unity writes a null string as <c>""</c> and a null array as <c>[]</c>. JipperKeyViewer's own
    /// source documents relying on the first: it detects "user kept the default label" by string
    /// comparison, noting that null and "" are indistinguishable after a round trip.
    /// </summary>
    [Test]
    public void NullsAreWrittenAsUnityWritesThem()
    {
        var json = PcCompatUnityJson.ToJson(new WithNulls(), prettyPrint: false);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"Label\":\"\""));
            Assert.That(json, Does.Contain("\"Items\":[]"));
            Assert.That(json, Does.Contain("\"Data\":{}"));
            Assert.That(json, Does.Not.Contain("null"));
        });
    }

    [Test]
    public void StructsAreRecursedIntoByField()
    {
        var json = PcCompatUnityJson.ToJson(new Sample(), prettyPrint: false);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"Color\":{\"r\":0.25,\"g\":0.5,\"b\":0.75,\"a\":1}"));
            Assert.That(json, Does.Contain("\"Palette\":[{\"r\":1,\"g\":0,\"b\":0,\"a\":1}]"));
        });
    }

    [Test]
    public void RoundTripPreservesEveryField()
    {
        var original = new Sample
        {
            Count = 42,
            Size = 0.125f,
            Enabled = false,
            Label = "Total",
            Binding = Key.Space,
            Keys = [Key.Space, Key.None, Key.Tab],
            Text = ["x", "", "z"],
            Color = new Rgba { r = 0.1f, g = 0.2f, b = 0.3f, a = 0.4f },
            Palette = [new Rgba { r = 1f, g = 1f, b = 0f, a = 0.5f }],
            Numbers = [9, 8],
            Data = new Nested { Version = 7, Name = "Custom" }
        };

        var restored = PcCompatUnityJson.FromJson<Sample>(
            PcCompatUnityJson.ToJson(original, prettyPrint: true));

        Assert.Multiple(() =>
        {
            Assert.That(restored!.Count, Is.EqualTo(42));
            Assert.That(restored.Size, Is.EqualTo(0.125f));
            Assert.That(restored.Enabled, Is.False);
            Assert.That(restored.Label, Is.EqualTo("Total"));
            Assert.That(restored.Binding, Is.EqualTo(Key.Space));
            Assert.That(restored.Keys, Is.EqualTo(new[] { Key.Space, Key.None, Key.Tab }));
            Assert.That(restored.Text, Is.EqualTo(new[] { "x", "", "z" }));
            Assert.That(restored.Color.b, Is.EqualTo(0.3f));
            Assert.That(restored.Palette[0].a, Is.EqualTo(0.5f));
            Assert.That(restored.Numbers, Is.EqualTo(new[] { 9, 8 }));
            Assert.That(restored.Data.Version, Is.EqualTo(7));
            Assert.That(restored.Data.Name, Is.EqualTo("Custom"));
        });
    }

    /// <summary>
    /// The behaviour JipperKeyViewer's profile switching is built on: fields absent from the JSON keep
    /// the target's current value. Its <c>LoadProfile</c> replaces the instance with a fresh default
    /// first, specifically so absent fields fall back to defaults rather than leaking the previous
    /// profile's values - which only works if overwrite really is partial.
    /// </summary>
    [Test]
    public void FromJsonOverwriteLeavesAbsentFieldsAlone()
    {
        var target = new Sample { Count = 99, Label = "Kept" };

        PcCompatUnityJson.FromJsonOverwrite("{\"Count\":1}", target);

        Assert.Multiple(() =>
        {
            Assert.That(target.Count, Is.EqualTo(1));
            Assert.That(target.Label, Is.EqualTo("Kept"));
        });
    }

    /// <summary>
    /// A shorter array in the JSON must produce a shorter array, not a padded one. JipperKeyViewer's
    /// v4-to-v5 migration depends on it: it checks <c>pd.Count.Length != MaxKeySlots</c> to detect an
    /// old profile, and its own comment records that the copy would run past the end otherwise.
    /// </summary>
    [Test]
    public void ShorterArraysDeserializeToTheirJsonLength()
    {
        var target = new Sample();

        PcCompatUnityJson.FromJsonOverwrite("{\"Keys\":[9]}", target);

        Assert.That(target.Keys, Has.Length.EqualTo(1));
    }

    /// <summary>
    /// A field whose JSON shape does not fit is skipped, not thrown on - matching Unity, so a
    /// hand-edited or version-skewed settings file loses one field instead of failing the whole load.
    /// </summary>
    [Test]
    public void MismatchedFieldShapesAreSkippedNotFatal()
    {
        var target = new Sample { Count = 7 };

        PcCompatUnityJson.FromJsonOverwrite(
            "{\"Count\":\"not-a-number\",\"Label\":\"Applied\"}",
            target);

        Assert.Multiple(() =>
        {
            Assert.That(target.Count, Is.EqualTo(7), "the bad field keeps its value");
            Assert.That(target.Label, Is.EqualTo("Applied"), "later good fields still apply");
        });
    }

    /// <summary>
    /// The serialize direction is strict where the deserialize direction is lenient: an unsupported
    /// shape throws rather than emitting partial JSON, because the callers write the result over a
    /// live settings file and a valid-looking truncation would destroy it.
    /// </summary>
    [Test]
    public void UnsupportedShapesThrowRatherThanEmitPartialJson()
        => Assert.That(
            () => PcCompatUnityJson.ToJson(new WithDictionary(), prettyPrint: false),
            Throws.TypeOf<NotSupportedException>());

    private sealed class WithDictionary
    {
        public Dictionary<string, int> Map = new() { ["a"] = 1 };
    }

    [Test]
    public void NullValueSerializesAsEmptyObjectLikeUnity()
        => Assert.That(PcCompatUnityJson.ToJson(null, prettyPrint: false), Is.EqualTo("{}"));

    [Test]
    public void StringsWithControlCharactersRoundTrip()
    {
        var original = new Nested { Name = "a\"b\\c\nd\te" };

        var restored = PcCompatUnityJson.FromJson<Nested>(
            PcCompatUnityJson.ToJson(original, prettyPrint: false));

        Assert.That(restored!.Name, Is.EqualTo("a\"b\\c\nd\te"));
    }

    /// <summary>
    /// Unity's reader rejects <c>NaN</c>/<c>Infinity</c>, so writing them would produce a file the
    /// real JsonUtility cannot read back. Unity writes 0 instead.
    /// </summary>
    [Test]
    public void NonFiniteFloatsAreWrittenAsZero()
    {
        var json = PcCompatUnityJson.ToJson(
            new Sample { Size = float.NaN },
            prettyPrint: false);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"Size\":0"));
            Assert.That(json, Does.Not.Contain("NaN"));
        });
    }

    /// <summary>
    /// Pretty-printed output must be readable by the same parser, since JipperKeyViewer writes with
    /// <c>prettyPrint: true</c> and reads back with the same pair of methods.
    /// </summary>
    [Test]
    public void PrettyPrintedOutputParsesBack()
    {
        var json = PcCompatUnityJson.ToJson(new Sample(), prettyPrint: true);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\n"));
            Assert.That(PcCompatUnityJson.FromJson<Sample>(json)!.Label, Is.EqualTo("KPS"));
        });
    }

    /// <summary>
    /// Floats must not be written with the invariant culture's alternatives; a comma decimal
    /// separator would make the file unreadable on a device with a European locale.
    /// </summary>
    [Test]
    public void FloatsUseInvariantFormatting()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");
            var json = PcCompatUnityJson.ToJson(new Sample { Size = 1.5f }, prettyPrint: false);

            Assert.Multiple(() =>
            {
                Assert.That(json, Does.Contain("\"Size\":1.5"));
                Assert.That(json, Does.Not.Contain("1,5"));
            });
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}

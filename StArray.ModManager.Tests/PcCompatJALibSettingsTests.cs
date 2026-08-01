using JALib.Core;
using JALib.Core.Setting;
using JALib.Tools;
using Newtonsoft.Json.Linq;
using System.Reflection;
using UnityModManagerNet;
using UnityEngine;

namespace StArray.ModManager.Tests;

public sealed class PcCompatJALibSettingsTests
{
    [Test]
    public void JamodDrawsItsOwnSettingsAndRunsHideLifecycleOnHostClose()
    {
        var order = new List<string>();
        var backend = new SettingsBackend(order)
        {
            EndAction = (int)PcCompatSettingsFrameAction.Close,
            ForceSectionExpanded = true
        };
        PcCompatSettingsUiBridge.Register(backend);
        var mod = new SettingsMod(order);
        mod.AddFeature(new SettingsFeature(order));
        mod.CompatEnable();

        mod.CompatOpenGUI();
        mod.CompatOnGUI();

        Assert.Multiple(() =>
        {
            Assert.That(mod.CompatSettingsVisible, Is.False);
            Assert.That(order, Is.EqualTo(new[]
            {
                "enable", "begin", "mod-gui", "feature-body-begin", "feature-gui",
                "feature-body-end", "behind", "end",
                "mod-hide", "feature-hide"
            }));
        });
    }

    [Test]
    public void ReopeningSettingsRestoresShowLifecycleForExpandedFeatures()
    {
        var order = new List<string>();
        var backend = new SettingsBackend(order)
        {
            ForceSectionExpanded = true
        };
        PcCompatSettingsUiBridge.Register(backend);
        var mod = new ReopenLifecycleMod(order);
        mod.AddFeature(new ReopenLifecycleFeature(order));
        mod.CompatEnable();

        mod.CompatOpenGUI();
        mod.CompatOnGUI();
        mod.CompatCloseGUI();

        Assert.That(order.TakeLast(2), Is.EqualTo(new[] { "mod-hide", "feature-hide" }));

        order.Clear();
        mod.CompatOpenGUI();

        Assert.That(order, Is.EqualTo(new[] { "mod-show", "feature-show" }));
    }

    [Test]
    public void MissingFeatureLocalizationFallsBackToFeatureNameInsteadOfRawKey()
    {
        var backend = new SettingsBackend([]);
        PcCompatSettingsUiBridge.Register(backend);
        var mod = new SettingsMod([]);
        mod.AddFeature(new NamedSettingsFeature("BPM"));
        mod.CompatEnable();
        mod.CompatOpenGUI();

        mod.CompatOnGUI();

        Assert.That(backend.SectionLabels, Does.Contain("BPM"));
        Assert.That(backend.SectionLabels, Does.Not.Contain("Feature.BPM"));
    }

    [Test]
    public void SettingGuiWritesOnlyChangedValuesAndInvokesOriginalCallbacks()
    {
        var backend = new SettingsBackend([])
        {
            ToggleValue = true,
            NumberValue = "2.5"
        };
        PcCompatSettingsUiBridge.Register(backend);
        var mod = new SettingsMod([]);
        var gui = new SettingGUI(mod);
        var toggle = false;
        var number = 1f;
        string? text = null;
        var changes = 0;

        gui.AddSettingToggle(ref toggle, "toggle", () => ++changes);
        gui.AddSettingSliderFloat(
            ref number,
            1f,
            ref text,
            "number",
            0f,
            3f,
            () => ++changes);

        Assert.Multiple(() =>
        {
            Assert.That(toggle, Is.True);
            Assert.That(number, Is.EqualTo(2.5f));
            Assert.That(changes, Is.EqualTo(2));
            Assert.That(backend.NumberCalls, Is.Zero);
            Assert.That(backend.SliderNumberCalls, Is.EqualTo(1));
            Assert.That(backend.TextCalls, Is.Zero);
        });
    }

    [Test]
    public void SettingGuiUsesDedicatedMobileEnumControl()
    {
        var backend = new SettingsBackend([])
        {
            EnumValue = nameof(SettingsMode.Third)
        };
        PcCompatSettingsUiBridge.Register(backend);
        var mod = new SettingsMod([]);
        var gui = new SettingGUI(mod);
        var value = SettingsMode.First;

        gui.AddSettingEnum(ref value, "mode");

        Assert.Multiple(() =>
        {
            Assert.That(value, Is.EqualTo(SettingsMode.Third));
            Assert.That(backend.EnumCalls, Is.EqualTo(1));
            Assert.That(backend.ButtonCalls, Is.Zero);
        });
    }

    [Test]
    public void JalibShimExportsRandomSurfaceUsedByJipperColorSettings()
    {
        var type = typeof(JAMod).Assembly.GetType(
            "JALib.Tools.JARandom",
            throwOnError: false,
            ignoreCase: false);

        Assert.Multiple(() =>
        {
            Assert.That(type, Is.Not.Null);
            Assert.That(type?.GetField(
                "Instance",
                BindingFlags.Public | BindingFlags.Static), Is.Not.Null);
            Assert.That(type?.GetMethod("NextFloat", Type.EmptyTypes), Is.Not.Null);
            Assert.That(type?.GetMethod("NextLong", Type.EmptyTypes), Is.Not.Null);
            Assert.That(type?.GetMethod("NextBytes", [typeof(int)]), Is.Not.Null);
        });
    }

    [Test]
    public void RootSettingsFailureAbortsFrameWithoutDrawingFooter()
    {
        var backend = new SettingsBackend([]);
        PcCompatSettingsUiBridge.Register(backend);
        var mod = new ThrowingSettingsMod();
        mod.CompatOpenGUI();

        var exception = Assert.Throws<InvalidOperationException>(() => mod.CompatOnGUI());

        Assert.Multiple(() =>
        {
            Assert.That(exception?.Message, Is.EqualTo("root settings failed"));
            Assert.That(backend.AbortCalls, Is.EqualTo(1));
            Assert.That(backend.EndCalls, Is.Zero);
        });
    }

    [Test]
    public void FeatureFailureCollapseWaitsForNextLayoutBoundary()
    {
        var backend = new SettingsBackend([])
        {
            ExpandFirstSectionOnly = true,
            CanApplyStructureChangesValue = false
        };
        PcCompatSettingsUiBridge.Register(backend);
        var mod = new SettingsMod([]);
        var feature = new ThrowingSettingsFeature();
        mod.AddFeature(feature);
        mod.CompatOpenGUI();

        for (var index = 0; index < 4; ++index)
            mod.CompatOnGUI();

        Assert.Multiple(() =>
        {
            Assert.That(feature.DrawCalls, Is.EqualTo(4));
            Assert.That(feature.HideCalls, Is.Zero);
            Assert.That(backend.SectionExpanded[^1], Is.True);
        });

        backend.CanApplyStructureChangesValue = true;
        mod.CompatOnGUI();

        Assert.Multiple(() =>
        {
            Assert.That(feature.DrawCalls, Is.EqualTo(4));
            Assert.That(feature.HideCalls, Is.EqualTo(1));
            Assert.That(backend.SectionExpanded[^1], Is.False);
        });
    }

    [Test]
    public void JalibLocalizationLoadsPackageFileAndFallsBackToEnglish()
    {
        var folder = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "pccompat-jalib-localization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "localization"));
        try
        {
            File.WriteAllText(
                Path.Combine(folder, "localization", "English.json"),
                """
                {
                  "credit.button": "Credits",
                  "Feature.Status": "Status"
                }
                """);
            var mod = new SettingsMod([]);

            mod.CompatSetup(folder);

            Assert.Multiple(() =>
            {
                Assert.That(mod.Localization["credit.button"], Is.EqualTo("Credits"));
                Assert.That(mod.Localization["Feature.Status"], Is.EqualTo("Status"));
                Assert.That(mod.Localization["missing.key"], Is.EqualTo("missing.key"));
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void JalibReadsLocalizationGidFromPackageMetadata()
    {
        var folder = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "pccompat-jalib-gid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(
                Path.Combine(folder, "JAModInfo.json"),
                """
                {
                  "Gid": 1313107549
                }
                """);
            var mod = new SettingsMod([]);

            mod.CompatSetup(folder);

            var gid = typeof(JAMod).GetProperty(
                "Gid",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(mod);
            Assert.That(gid, Is.EqualTo(1313107549));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void LoadedOnlyJalibMenuStartsCollapsedAndExposesEveryFeatureSection()
    {
        var backend = new SettingsBackend([]);
        PcCompatSettingsUiBridge.Register(backend);
        var mod = new SettingsMod([]);
        for (var index = 0; index < 8; ++index)
            mod.AddFeature(new NamedSettingsFeature("Feature" + index));

        mod.CompatOpenGUI();
        mod.CompatOnGUI();

        Assert.Multiple(() =>
        {
            Assert.That(backend.SectionLabels, Has.Count.EqualTo(8));
            Assert.That(backend.SectionExpanded, Is.All.False);
        });
    }

    [Test]
    public void JalibSettingsRoundTripUsesOriginalSettingsJsonShape()
    {
        var folder = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "pccompat-jalib-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var first = new PersistentMod();
            first.CompatSetup(folder);
            ((PersistentRootSetting)first.Setting).Scale = 1.75f;
            ((PersistentFeatureSetting)first.Feature.Setting).Label = "touch";
            first.Feature.Enabled = false;
            first.SaveSetting();

            var path = Path.Combine(folder, "Settings.json");
            var json = JObject.Parse(File.ReadAllText(path));
            Assert.Multiple(() =>
            {
                Assert.That(json["Setting"]?["Scale"]?.Value<float>(), Is.EqualTo(1.75f));
                Assert.That(json["Feature"]?["FeatureA"]?["Enabled"]?.Value<bool>(), Is.False);
                Assert.That(json["Feature"]?["FeatureA"]?["Setting"]?["Label"]?.Value<string>(),
                    Is.EqualTo("touch"));
            });

            var second = new PersistentMod();
            second.CompatSetup(folder);
            Assert.Multiple(() =>
            {
                Assert.That(((PersistentRootSetting)second.Setting).Scale,
                    Is.EqualTo(1.75f));
                Assert.That(second.Feature.Enabled, Is.False);
                Assert.That(((PersistentFeatureSetting)second.Feature.Setting).Label,
                    Is.EqualTo("touch"));
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void JalibSettingsPreserveUnknownFieldsAndBackUpPreviousFile()
    {
        var folder = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "pccompat-jalib-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "Settings.json");
            File.WriteAllText(path, """
                {
                  "FutureRoot": 17,
                  "Setting": { "Scale": 2.0, "FutureSetting": "keep" },
                  "Feature": {
                    "FeatureA": {
                      "Enabled": true,
                      "Setting": { "Label": "old", "FutureFeature": 23 }
                    }
                  }
                }
                """);

            var mod = new PersistentMod();
            mod.CompatSetup(folder);
            ((PersistentRootSetting)mod.Setting).Scale = 2.5f;
            mod.SaveSetting();

            var saved = JObject.Parse(File.ReadAllText(path));
            var backup = JObject.Parse(File.ReadAllText(path + ".bak"));
            Assert.Multiple(() =>
            {
                Assert.That(saved["FutureRoot"]?.Value<int>(), Is.EqualTo(17));
                Assert.That(saved["Setting"]?["FutureSetting"]?.Value<string>(), Is.EqualTo("keep"));
                Assert.That(saved["Feature"]?["FeatureA"]?["Setting"]?["FutureFeature"]?.Value<int>(),
                    Is.EqualTo(23));
                Assert.That(saved["Setting"]?["Scale"]?.Value<float>(), Is.EqualTo(2.5f));
                Assert.That(backup["Setting"]?["Scale"]?.Value<float>(), Is.EqualTo(2.0f));
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void JalibColorSettingUsesCanonicalRgbaShapeAndReadsLegacyLowercaseShape()
    {
        var folder = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "pccompat-jalib-color-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "Settings.json");
            File.WriteAllText(path, """
                {
                  "Setting": {
                    "Tint": { "r": 0.1, "g": 0.2, "b": 0.3, "a": 0.4 }
                  }
                }
                """);
            var mod = new ColorSettingMod();
            mod.CompatSetup(folder);
            var setting = (ColorRootSetting)mod.Setting;
            Assert.That(setting.Tint.r, Is.EqualTo(0.1f));
            Assert.That(setting.Tint.a, Is.EqualTo(0.4f));

            setting.Tint = new Color(0.5f, 0.6f, 0.7f, 0.8f);
            mod.SaveSetting();
            var saved = JObject.Parse(File.ReadAllText(path));

            Assert.Multiple(() =>
            {
                Assert.That(saved["Setting"]?["Tint"]?["R"]?.Value<float>(), Is.EqualTo(0.5f));
                Assert.That(saved["Setting"]?["Tint"]?["G"]?.Value<float>(), Is.EqualTo(0.6f));
                Assert.That(saved["Setting"]?["Tint"]?["B"]?.Value<float>(), Is.EqualTo(0.7f));
                Assert.That(saved["Setting"]?["Tint"]?["A"]?.Value<float>(), Is.EqualTo(0.8f));
                Assert.That(saved["Setting"]?["Tint"]?["r"], Is.Null);
            });
            mod.CompatUnload();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class SettingsMod(List<string> order) : JAMod
    {
        protected override void OnEnable()
            => order.Add("enable");

        protected override void OnGUI()
            => order.Add("mod-gui");

        protected override void OnGUIBehind()
            => order.Add("behind");

        protected override void OnHideGUI()
            => order.Add("mod-hide");
    }

    private sealed class SettingsFeature(List<string> order) : Feature
    {
        protected override void OnGUI()
            => order.Add("feature-gui");

        protected override void OnHideGUI()
            => order.Add("feature-hide");
    }

    private sealed class ReopenLifecycleMod(List<string> order) : JAMod
    {
        protected override void OnShowGUI() => order.Add("mod-show");
        protected override void OnHideGUI() => order.Add("mod-hide");
    }

    private sealed class ReopenLifecycleFeature(List<string> order) : Feature
    {
        protected override void OnShowGUI() => order.Add("feature-show");
        protected override void OnHideGUI() => order.Add("feature-hide");
    }

    private sealed class NamedSettingsFeature(string name) : Feature(null, name);

    private sealed class ThrowingSettingsMod : JAMod
    {
        protected override void OnGUI()
            => throw new InvalidOperationException("root settings failed");
    }

    private sealed class ThrowingSettingsFeature : Feature
    {
        public int DrawCalls { get; private set; }
        public int HideCalls { get; private set; }

        protected override void OnGUI()
        {
            DrawCalls++;
            throw new InvalidOperationException("feature settings failed");
        }

        protected override void OnHideGUI() => HideCalls++;
    }

    private enum SettingsMode
    {
        First,
        Second,
        Third
    }

    private sealed class PersistentMod : JAMod
    {
        public PersistentMod()
            : base(typeof(PersistentRootSetting))
        {
        }

        public PersistentFeature Feature { get; private set; } = null!;

        protected override void OnSetup()
        {
            Feature = new PersistentFeature(this);
            AddFeature(Feature);
        }
    }

    private sealed class ColorSettingMod : JAMod
    {
        public ColorSettingMod()
            : base(typeof(ColorRootSetting))
        {
        }
    }

    private sealed class ColorRootSetting(JAMod mod, JObject? json = null)
        : JASetting(mod, json)
    {
        public Color Tint = Color.white;
    }

    private sealed class PersistentRootSetting(JAMod mod, JObject? json = null)
        : JASetting(mod, json)
    {
        public float Scale = 1f;
    }

    private sealed class PersistentFeatureSetting(JAMod mod, JObject? json = null)
        : JASetting(mod, json)
    {
        public string Label = "default";
    }

    private sealed class PersistentFeature(PersistentMod mod)
        : Feature(mod, "FeatureA", true, null, typeof(PersistentFeatureSetting));

    private sealed class SettingsBackend(List<string> order)
    {
        public int EndAction { get; init; }
        public bool? ToggleValue { get; init; }
        public string? TextValue { get; init; }
        public string? NumberValue { get; init; }
        public string? EnumValue { get; init; }
        public bool ForceSectionExpanded { get; init; }
        public bool ExpandFirstSectionOnly { get; init; }
        public bool CanApplyStructureChangesValue { get; set; }
        public int AbortCalls { get; private set; }
        public int EndCalls { get; private set; }
        public int TextCalls { get; private set; }
        public int NumberCalls { get; private set; }
        public int SliderNumberCalls { get; private set; }
        public int EnumCalls { get; private set; }
        public int ButtonCalls { get; private set; }
        public List<string> SectionLabels { get; } = [];
        public List<bool> SectionExpanded { get; } = [];

        public void BeginFrame(string title)
            => order.Add("begin");

        public int EndFrame()
        {
            EndCalls++;
            order.Add("end");
            return EndAction;
        }

        public void AbortFrame() => AbortCalls++;

        public bool CanApplyStructureChanges() => CanApplyStructureChangesValue;

        public void ReleaseInputFocus()
        {
        }

        public bool Toggle(bool value, string label)
            => ToggleValue ?? value;

        public string Text(string value, string label)
        {
            ++TextCalls;
            return TextValue ?? value;
        }

        public string Number(
            string value,
            string label,
            double min,
            double max,
            double step,
            bool integral)
        {
            ++NumberCalls;
            return NumberValue ?? value;
        }

        public string SliderNumber(
            string value,
            string label,
            double min,
            double max,
            bool integral)
        {
            ++SliderNumberCalls;
            return NumberValue ?? value;
        }

        public string Enum(string value, string label, string[] values)
        {
            ++EnumCalls;
            return EnumValue ?? value;
        }

        public int Section(
            bool enabled,
            bool expanded,
            bool canEnable,
            bool canExpand,
            string label)
        {
            SectionLabels.Add(label);
            SectionExpanded.Add(expanded);
            if (ForceSectionExpanded && canExpand)
                expanded = true;
            if (ExpandFirstSectionOnly && SectionLabels.Count == 1 && canExpand)
                expanded = true;
            SectionExpanded[^1] = expanded;
            return (enabled ? 1 : 0) | (expanded ? 2 : 0);
        }

        public void BeginSectionBody()
            => order.Add("feature-body-begin");

        public void EndSectionBody()
            => order.Add("feature-body-end");

        public bool Button(string label)
        {
            ++ButtonCalls;
            return false;
        }

        public void Label(string label)
        {
        }
    }
}

using JALib.Core;
using JALib.Core.Setting;
using Newtonsoft.Json.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatManagedSettingsSchemaTests
{
    [Test]
    public void BuildsJalibSchemaAndAppliesVerifiedBindingsThroughOriginalSave()
    {
        var folder = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "pccompat-settings-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var mod = new SchemaMod();
            mod.CompatSetup(folder);
            mod.CompatEnable();
            var manifest = new PcModManifest
            {
                FolderPath = folder,
                Id = "schema.test",
                DisplayName = "Schema Test",
                Kind = PcModKind.JAMod
            };
            var runtime = PcCompatManagedSettingsSchemaRuntime.Create(
                manifest,
                mod,
                null,
                mod.SaveSetting);
            var schema = runtime.Snapshot();

            Assert.Multiple(() =>
            {
                Assert.That(schema.Available, Is.True, schema.Error);
                Assert.That(schema.Entries.Select(entry => entry.Path), Does.Contain("Setting/Scale"));
                Assert.That(schema.Entries.Select(entry => entry.Path), Does.Contain("Setting/Mode"));
                Assert.That(schema.Entries.Select(entry => entry.Path), Does.Contain("Feature/FeatureA/Enabled"));
                Assert.That(schema.Entries.Select(entry => entry.Path), Does.Contain("Feature/FeatureA/Setting/Label"));
            });

            Assert.That(runtime.RequestValue(
                schema.Revision,
                "Setting/Scale",
                "2.5",
                out var error), Is.True, error);
            Assert.That(runtime.RequestValue(
                schema.Revision,
                "Setting/Mode",
                nameof(SchemaMode.Fast),
                out error), Is.True, error);
            Assert.That(runtime.RequestValue(
                schema.Revision,
                "Feature/FeatureA/Enabled",
                "false",
                out error), Is.True, error);
            Assert.That(runtime.Dispatch(out error), Is.True, error);

            var settings = (SchemaRootSetting)mod.Setting;
            Assert.Multiple(() =>
            {
                Assert.That(settings.Scale, Is.EqualTo(2.5f));
                Assert.That(settings.Mode, Is.EqualTo(SchemaMode.Fast));
                Assert.That(mod.Feature.Enabled, Is.False);
                Assert.That(runtime.Snapshot().HasUnsavedChanges, Is.False);
            });

            var schemaPath = Path.Combine(folder, ".pccompat", "mod_settings.schema");
            Assert.That(File.Exists(schemaPath), Is.True);
            using var document = JsonDocument.Parse(File.ReadAllText(schemaPath));
            Assert.Multiple(() =>
            {
                Assert.That(document.RootElement.GetProperty("SchemaVersion").GetInt32(), Is.EqualTo(1));
                Assert.That(document.RootElement.GetProperty("ModId").GetString(), Is.EqualTo("schema.test"));
                Assert.That(document.RootElement.GetProperty("Entries").GetArrayLength(), Is.GreaterThanOrEqualTo(4));
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void RefreshPublishesOriginalMenuChangesWithoutRepeatingOriginalSave()
    {
        var folder = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "pccompat-settings-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var mod = new SchemaMod();
            mod.CompatSetup(folder);
            mod.CompatEnable();
            var saveAttempts = 0;
            var runtime = PcCompatManagedSettingsSchemaRuntime.Create(
                CreateManifest(folder, "schema.original-refresh"),
                mod,
                null,
                () => saveAttempts++);

            mod.Feature.Enabled = false;
            ((SchemaRootSetting)mod.Setting).Scale = 2.75f;
            runtime.Refresh();

            var refreshed = runtime.Snapshot();
            Assert.Multiple(() =>
            {
                Assert.That(
                    refreshed.Entries.Single(entry =>
                        entry.Path == "Feature/FeatureA/Enabled").Value,
                    Is.EqualTo("false"));
                Assert.That(
                    refreshed.Entries.Single(entry =>
                        entry.Path == "Setting/Scale").Value,
                    Is.EqualTo("2.75"));
                Assert.That(refreshed.HasPendingWrite, Is.False);
                Assert.That(saveAttempts, Is.Zero,
                    "refresh must observe the original menu; it must not save a second time");
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void RejectsStaleRevisionAndInvalidEnumWithoutWriting()
    {
        var folder = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "pccompat-settings-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var mod = new SchemaMod();
            mod.CompatSetup(folder);
            var runtime = PcCompatManagedSettingsSchemaRuntime.Create(
                new PcModManifest
                {
                    FolderPath = folder,
                    Id = "schema.reject",
                    DisplayName = "Schema Reject",
                    Kind = PcModKind.JAMod
                },
                mod,
                null,
                mod.SaveSetting);
            var schema = runtime.Snapshot();

            Assert.That(runtime.RequestValue(
                "stale",
                "Setting/Scale",
                "3",
                out var staleError), Is.False);
            Assert.That(staleError, Does.Contain("revision"));
            Assert.That(runtime.RequestValue(
                schema.Revision,
                "Setting/Mode",
                "Unknown",
                out var enumError), Is.False);
            Assert.That(enumError, Does.Contain("invalid value"));
            Assert.That(((SchemaRootSetting)mod.Setting).Scale, Is.EqualTo(1f));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void ReusesLabelGroupAndNumericRangeFromMatchingPersistedSchema()
    {
        var folder = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "pccompat-settings-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var manifest = CreateManifest(folder, "schema.template");
            var firstMod = new SchemaMod();
            firstMod.CompatSetup(folder);
            _ = PcCompatManagedSettingsSchemaRuntime.Create(
                manifest,
                firstMod,
                null,
                firstMod.SaveSetting);

            var schemaPath = Path.Combine(folder, ".pccompat", "mod_settings.schema");
            var document = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
            var scale = document["Entries"]!.AsArray()
                .Select(node => node!.AsObject())
                .Single(entry => entry["Path"]!.GetValue<string>() == "Setting/Scale");
            scale["Label"] = "HUD scale";
            scale["Group"] = "Display";
            scale["Minimum"] = 0.5;
            scale["Maximum"] = 3.0;
            File.WriteAllText(schemaPath, document.ToJsonString());

            var secondMod = new SchemaMod();
            secondMod.CompatSetup(folder);
            var runtime = PcCompatManagedSettingsSchemaRuntime.Create(
                manifest,
                secondMod,
                null,
                secondMod.SaveSetting);
            var schema = runtime.Snapshot();
            var inherited = schema.Entries.Single(entry => entry.Path == "Setting/Scale");

            Assert.Multiple(() =>
            {
                Assert.That(inherited.Label, Is.EqualTo("HUD scale"));
                Assert.That(inherited.Group, Is.EqualTo("Display"));
                Assert.That(inherited.Minimum, Is.EqualTo(0.5));
                Assert.That(inherited.Maximum, Is.EqualTo(3.0));
            });
            Assert.That(runtime.RequestValue(
                schema.Revision,
                inherited.Path,
                "4",
                out var rangeError), Is.False);
            Assert.That(rangeError, Does.Contain("verified range"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void SaveFailureKeepsMemoryValueAndCanRetryOriginalSave()
    {
        var folder = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "pccompat-settings-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var mod = new SchemaMod();
            mod.CompatSetup(folder);
            var saveAttempts = 0;
            var runtime = PcCompatManagedSettingsSchemaRuntime.Create(
                CreateManifest(folder, "schema.save-retry"),
                mod,
                null,
                () =>
                {
                    if (++saveAttempts == 1)
                        throw new IOException("save unavailable");
                });
            var schema = runtime.Snapshot();

            Assert.That(runtime.RequestValue(
                schema.Revision,
                "Setting/Scale",
                "2",
                out var error), Is.True, error);
            Assert.That(runtime.Dispatch(out error), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(((SchemaRootSetting)mod.Setting).Scale, Is.EqualTo(2f));
                Assert.That(runtime.Snapshot().HasUnsavedChanges, Is.True);
                Assert.That(runtime.Snapshot().SaveError, Does.Contain("save unavailable"));
            });

            runtime.RequestRetrySave();
            Assert.That(runtime.Dispatch(out error), Is.True, error);
            Assert.Multiple(() =>
            {
                Assert.That(saveAttempts, Is.EqualTo(2));
                Assert.That(runtime.Snapshot().HasUnsavedChanges, Is.False);
                Assert.That(runtime.Snapshot().SaveError, Is.Null);
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void PartialWriteFailureIsPublishedWithoutSavingSuccessfulFields()
    {
        var folder = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "pccompat-settings-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var mod = new SchemaMod();
            mod.CompatSetup(folder);
            mod.CompatEnable();
            mod.Feature.ThrowOnDisable = true;
            var saveAttempts = 0;
            var runtime = PcCompatManagedSettingsSchemaRuntime.Create(
                CreateManifest(folder, "schema.partial-write"),
                mod,
                null,
                () => saveAttempts++);
            var schema = runtime.Snapshot();

            Assert.That(runtime.RequestValue(
                schema.Revision,
                "Setting/Scale",
                "2.25",
                out var error), Is.True, error);
            Assert.That(runtime.RequestValue(
                schema.Revision,
                "Feature/FeatureA/Enabled",
                "false",
                out error), Is.True, error);
            Assert.That(runtime.Dispatch(out error), Is.False);
            var failed = runtime.Snapshot();

            Assert.Multiple(() =>
            {
                Assert.That(((SchemaRootSetting)mod.Setting).Scale, Is.EqualTo(2.25f));
                Assert.That(mod.Feature.Enabled, Is.True);
                Assert.That(saveAttempts, Is.Zero);
                Assert.That(failed.HasUnsavedChanges, Is.True);
                Assert.That(failed.ApplyError, Does.Contain("feature disable failed"));
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static PcModManifest CreateManifest(string folder, string id)
        => new()
        {
            FolderPath = folder,
            Id = id,
            DisplayName = id,
            Kind = PcModKind.JAMod
        };

    private enum SchemaMode
    {
        Normal,
        Fast
    }

    private sealed class SchemaMod : JAMod
    {
        public SchemaMod()
            : base(typeof(SchemaRootSetting))
        {
        }

        public SchemaFeature Feature { get; private set; } = null!;

        protected override void OnSetup()
        {
            Feature = new SchemaFeature(this);
            AddFeature(Feature);
        }
    }

    private sealed class SchemaRootSetting(JAMod mod, JObject? json = null)
        : JASetting(mod, json)
    {
        public float Scale = 1f;
        public SchemaMode Mode = SchemaMode.Normal;
    }

    private sealed class SchemaFeatureSetting(JAMod mod, JObject? json = null)
        : JASetting(mod, json)
    {
        public string Label = "default";
    }

    private sealed class SchemaFeature(SchemaMod mod)
        : Feature(mod, "FeatureA", true, null, typeof(SchemaFeatureSetting))
    {
        public bool ThrowOnDisable { get; set; }

        protected override void OnDisable()
        {
            if (ThrowOnDisable)
                throw new InvalidOperationException("feature disable failed");
        }
    }
}

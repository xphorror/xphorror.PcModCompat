using System.Reflection;
using JALib.Core;
using JALib.Core.Patch;
using JALib.Core.Setting;
using JALib.JAException;
using JALib.Tools;
using Newtonsoft.Json.Linq;
using StArray.ModManager.Tests.JalibPatchFixtures;
using UnityEngine;
using UnityModManagerNet;

namespace StArray.ModManager.Tests;

public sealed class PcCompatJALibAbiTests
{
    [Test]
    public void PatchExceptionsPreserveOfficialConstructionSemantics()
    {
        var inner = new InvalidOperationException("inner");

        Assert.Multiple(() =>
        {
            Assert.That(new AlreadyWorkedException("worked").Message, Is.EqualTo("worked"));
            Assert.That(new PatchParameterException("parameter").Message, Is.EqualTo("parameter"));
            Assert.That(new PacketRunningException("packet", inner).InnerException, Is.SameAs(inner));
            Assert.That(
                new PatchReturnException(typeof(string), typeof(int)).Message,
                Is.EqualTo("Patch return type mismatch: System.String -> System.Int32"));
        });
    }

    [Test]
    public void LocalizationFileCompletionPublishesValuesAndNotifiesOwner()
    {
        var mod = new LocalizationMod();

        mod.Localization.LoadOnFile(Task.FromResult("""
            {
              "Feature.Status": "Status",
              "Nested": { "ignored": true }
            }
            """));

        Assert.Multiple(() =>
        {
            Assert.That(mod.Localization["Feature.Status"], Is.EqualTo("Status"));
            Assert.That(mod.Localization["Nested"], Is.EqualTo("Nested"));
            Assert.That(mod.LocalizationUpdates, Is.EqualTo(1));
        });
    }

    [Test]
    public void SettingDisposeUsesOfficialOverridableDispose0Member()
    {
        var setting = new TrackingSetting(new EmptyMod());

        setting.Dispose();
        setting.Dispose();

        Assert.That(setting.DisposeCalls, Is.EqualTo(1));
        var method = typeof(JASetting).GetMethod(
            "Dispose0",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Multiple(() =>
        {
            Assert.That(method, Is.Not.Null);
            Assert.That(method!.IsFamily, Is.True);
            Assert.That(method.IsVirtual, Is.True);
        });
    }

    [Test]
    public void SettingEnumGenericConstraintMatchesOfficialEnumOnlyAbi()
    {
        var methods = typeof(SettingGUI).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == "AddSettingEnum")
            .ToArray();

        Assert.That(methods, Has.Length.EqualTo(2));
        foreach (var method in methods)
        {
            var parameter = method.GetGenericArguments().Single();
            Assert.Multiple(() =>
            {
                Assert.That(parameter.GetGenericParameterConstraints(), Is.EqualTo(new[] { typeof(Enum) }));
                Assert.That(
                    parameter.GenericParameterAttributes & GenericParameterAttributes.NotNullableValueTypeConstraint,
                    Is.EqualTo((GenericParameterAttributes)0));
            });
        }
    }

    [Test]
    public void VersionControlPublishesNonNullVersionField()
    {
        var field = typeof(VersionControl).GetField(
            "version",
            BindingFlags.Public | BindingFlags.Static);

        Assert.Multiple(() =>
        {
            Assert.That(field, Is.Not.Null);
            Assert.That(field!.FieldType, Is.EqualTo(typeof(Version)));
            Assert.That(VersionControl.version, Is.Not.Null);
        });
    }

    [Test]
    public void PatcherBindingAndNamespaceScansUseOfficialSelectionRules()
    {
        JAPatcher.ClearRegisteredPatches();
        var patcher = new JAPatcher(new EmptyMod());

        patcher.AddPatch(typeof(PatchSet), PatchBinding.Postfix);
        patcher.AddPatch(
            typeof(PatchSet).Assembly,
            typeof(PatchSet).Namespace!,
            PatchBinding.Prefix);
        patcher.Patch();

        var records = JAPatcher.SnapshotRegisteredPatches();
        Assert.Multiple(() =>
        {
            Assert.That(records.Count(record => record.Kind == "Prefix"), Is.EqualTo(1));
            Assert.That(records.Count(record => record.Kind == "Postfix"), Is.EqualTo(1));
            Assert.That(
                records.Any(record => record.CallbackType.Contains("ChildPatchSet", StringComparison.Ordinal)),
                Is.False);
        });
    }

    [Test]
    public void PatcherTerminalAttributeOverloadDefersMissingCallbackWithoutThrowing()
    {
        JAPatcher.ClearRegisteredPatches();
        var patcher = new JAPatcher(new EmptyMod());
        var attribute = new JAPatchAttribute(
            typeof(PatchTarget),
            nameof(PatchTarget.Run),
            PatchType.Postfix,
            needInstance: false);

        Assert.DoesNotThrow(() => patcher.AddPatch((JAPatchBaseAttribute)attribute));
        Assert.DoesNotThrow(patcher.Patch);

        var record = JAPatcher.SnapshotRegisteredPatches().Single();
        Assert.Multiple(() =>
        {
            Assert.That(record.CallbackMethodInfo, Is.Null);
            Assert.That(record.OriginalMethod?.Name, Is.EqualTo(nameof(PatchTarget.Run)));
            Assert.That(record.Status, Is.EqualTo("registered_only"));
        });
    }

    [Test]
    public void PatcherUsingWaitingRemainsPublicInstanceField()
    {
        var field = typeof(JAPatcher).GetField(
            "usingWaiting",
            BindingFlags.Public | BindingFlags.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(field, Is.Not.Null);
            Assert.That(field!.FieldType, Is.EqualTo(typeof(bool)));
            Assert.That(field.GetValue(new JAPatcher(new EmptyMod())), Is.EqualTo(true));
        });
    }

    [Test]
    public void JamodPublishesDistinctOfficialConstructorSignatures()
    {
        var constructors = typeof(JAMod).GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic);
        var parameterless = constructors.Single(ctor => ctor.GetParameters().Length == 0);
        var setting = constructors.Single(ctor =>
            ctor.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual(new[] { typeof(Type) }));
        var legacy = constructors.Single(ctor => ctor.GetParameters().Length == 6);
        var obsolete = legacy.GetCustomAttribute<ObsoleteAttribute>();

        Assert.Multiple(() =>
        {
            Assert.That(parameterless.IsFamily, Is.True);
            Assert.That(setting.IsFamily, Is.True);
            Assert.That(obsolete, Is.Not.Null);
            Assert.That(obsolete!.IsError, Is.True);
            Assert.That(obsolete.Message, Is.EqualTo("Deprecated. Use other constructor instead."));
        });
    }

    [Test]
    public void JamodSynthesizesAndRegistersUmmEntryForCompatLifetime()
    {
        var folder = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "pccompat-jalib-entry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var mod = new AbiMod();
        UnityModManager.modEntries.RemoveAll(entry => entry.Info.Id == mod.Name);
        try
        {
            mod.CompatSetup(folder);

            Assert.Multiple(() =>
            {
                Assert.That(mod.ModEntry, Is.Not.Null);
                Assert.That(mod.ModEntry.Info.Id, Is.EqualTo(mod.Name));
                Assert.That(mod.ModEntry.Path, Is.EqualTo(folder));
                Assert.That(mod.Path, Is.EqualTo(folder));
                Assert.That(mod.Logger, Is.SameAs(mod.ModEntry.Logger));
                Assert.That(mod.Version, Is.EqualTo(mod.ModEntry.Version));
                Assert.That(mod.IsLatest, Is.True);
                Assert.That(UnityModManager.modEntries, Does.Contain(mod.ModEntry));
            });

            mod.CompatUnload();
            Assert.That(UnityModManager.modEntries, Does.Not.Contain(mod.ModEntry));
        }
        finally
        {
            UnityModManager.modEntries.RemoveAll(entry => entry.Info.Id == mod.Name);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void JamodLogReportInvokesVirtualReportHookAndPersistsFailure()
    {
        var mod = new AbiMod();
        var exception = new InvalidOperationException("report-me");

        mod.LogReportException("report-key", exception, [mod], stackTraceSkip: 3);

        Assert.Multiple(() =>
        {
            Assert.That(mod.ReportCalls, Is.EqualTo(1));
            Assert.That(mod.ReportKey, Is.EqualTo("report-key"));
            Assert.That(mod.ReportExceptionValue, Is.SameAs(exception));
            Assert.That(mod.ReportMods, Is.EqualTo(new[] { mod }));
            Assert.That(mod.GetCompatDiagnosticStatus(), Does.Contain("report-me"));
        });
    }

    [Test]
    public void JamodUnavailableDownloadCompletionDoesNotThrowAndPersistsDiagnostic()
    {
        var mod = new AbiMod();

        Assert.DoesNotThrow(mod.DownloadComplete);
        Assert.That(
            mod.GetCompatDiagnosticStatus(),
            Does.Contain("JAMod.DownloadComplete is unavailable on Android PcModCompat"));
    }

    [Test]
    public void JamodCustomLanguageLoadsPersistedPackageLanguageAndCanReload()
    {
        var folder = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "pccompat-jalib-language-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "localization"));
        try
        {
            File.WriteAllText(
                Path.Combine(folder, "Settings.json"),
                """
                {
                  "CustomLanguage": 40,
                  "AvailableLanguages": [10, 40]
                }
                """);
            File.WriteAllText(
                Path.Combine(folder, "localization", "ChineseSimplified.json"),
                """{ "title": "zh" }""");
            File.WriteAllText(
                Path.Combine(folder, "localization", "English.json"),
                """{ "title": "en" }""");
            var mod = new AbiMod();

            mod.CompatSetup(folder);

            Assert.Multiple(() =>
            {
                Assert.That(mod.ReadCustomLanguage(), Is.EqualTo(SystemLanguage.ChineseSimplified));
                Assert.That(mod.ReadAvailableLanguages(), Is.EqualTo(new[]
                {
                    SystemLanguage.English,
                    SystemLanguage.ChineseSimplified
                }));
                Assert.That(mod.Localization["title"], Is.EqualTo("zh"));
            });

            mod.SetCustomLanguage(SystemLanguage.English);
            Assert.That(mod.Localization["title"], Is.EqualTo("en"));
            mod.CompatUnload();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void SystemLanguageValuesMatchUnity6000Metadata()
    {
        Assert.Multiple(() =>
        {
            Assert.That((int)SystemLanguage.English, Is.EqualTo(10));
            Assert.That((int)SystemLanguage.Hungarian, Is.EqualTo(18));
            Assert.That((int)SystemLanguage.ChineseSimplified, Is.EqualTo(40));
            Assert.That((int)SystemLanguage.ChineseTraditional, Is.EqualTo(41));
            Assert.That((int)SystemLanguage.Hindi, Is.EqualTo(42));
            Assert.That((int)SystemLanguage.Unknown, Is.EqualTo(43));
        });
    }

    [Test]
    public void ModToolsLoadEventsFireAfterUmmLoadAndIsolateCallbackFailures()
    {
        var owner = new AbiMod();
        var entry = new UnityModManager.ModEntry(
            new UnityModManager.ModInfo { Id = "event-target", Version = "1.0" },
            "event-path");
        var calls = new List<string>();
        Action<UnityModManager.ModEntry> failing = _ => throw new InvalidOperationException("event failure");
        Action<UnityModManager.ModEntry> succeeding = loaded => calls.Add(loaded.Info.Id);
        ModTools.RegisterModLoadEvent(owner, failing);
        ModTools.RegisterModLoadEvent(owner, succeeding);
        try
        {
            Assert.That(entry.Load(), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(calls, Is.EqualTo(new[] { "event-target" }));
                Assert.That(owner.GetCompatDiagnosticStatus(), Does.Contain("event failure"));
            });
        }
        finally
        {
            ModTools.UnregisterModLoadEvent(owner, failing);
            ModTools.UnregisterModLoadEvent(owner, succeeding);
        }
    }

    [Test]
    public void ModToolsApplyModReportsUnavailableWithoutThrowing()
    {
        var owner = new AbiMod();

        Assert.DoesNotThrow(() => ModTools.ApplyMod(owner, "pc-mod"));
        Assert.That(
            owner.GetCompatDiagnosticStatus(),
            Does.Contain("Runtime managed-DLL loading is unavailable on Android IL2CPP"));
    }

    [Test]
    public void ModReloadCachePassesThroughObjectsOutsideOldAssembly()
    {
        var constructor = typeof(ModReloadCache).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(Assembly), typeof(Assembly)],
            modifiers: null);
        var cache = (ModReloadCache)constructor!.Invoke([
            typeof(ModReloadCache).Assembly,
            typeof(ModReloadCache).Assembly
        ]);
        var value = new object();

        Assert.Multiple(() =>
        {
            Assert.That(cache.GetCachedObject(null), Is.Null);
            Assert.That(cache.GetCachedObject(value), Is.SameAs(value));
            Assert.That(cache.OldAssembly, Is.EqualTo(typeof(ModReloadCache).Assembly));
            Assert.That(cache.NewAssembly, Is.EqualTo(typeof(ModReloadCache).Assembly));
        });
    }

    [Test]
    public void ForceApplyModCarriesOfficialErrorObsoleteAttributes()
    {
        var type = typeof(ModTools).Assembly.GetType(
            "JALib.Tools.ForceApplyMod",
            throwOnError: true)!;
        var method = type.GetMethod("ApplyMod", BindingFlags.Public | BindingFlags.Static)!;
        var typeAttribute = type.GetCustomAttribute<ObsoleteAttribute>();
        var methodAttribute = method.GetCustomAttribute<ObsoleteAttribute>();

        Assert.Multiple(() =>
        {
            Assert.That(typeAttribute?.Message, Is.EqualTo("Deprecated. Use ModTools.ApplyMod instead."));
            Assert.That(typeAttribute?.IsError, Is.True);
            Assert.That(methodAttribute?.Message, Is.EqualTo("Deprecated. Use ModTools.ApplyMod instead."));
            Assert.That(methodAttribute?.IsError, Is.True);
        });
    }

    private sealed class EmptyMod : JAMod;

    private sealed class AbiMod : JAMod
    {
        public int ReportCalls { get; private set; }
        public string? ReportKey { get; private set; }
        public Exception? ReportExceptionValue { get; private set; }
        public JAMod[]? ReportMods { get; private set; }

        public SystemLanguage? ReadCustomLanguage() => CustomLanguage;
        public SystemLanguage[] ReadAvailableLanguages() => AvailableLanguages;
        public void SetCustomLanguage(SystemLanguage? language) => CustomLanguage = language;

        protected override void OnReportException(
            string? key,
            Exception exception,
            JAMod[] mods)
        {
            ReportCalls++;
            ReportKey = key;
            ReportExceptionValue = exception;
            ReportMods = mods;
        }
    }

    private sealed class LocalizationMod : JAMod
    {
        public int LocalizationUpdates { get; private set; }

        protected override void OnLocalizationUpdate()
            => LocalizationUpdates++;
    }

    private sealed class TrackingSetting(JAMod mod) : JASetting(mod, new JObject())
    {
        public int DisposeCalls { get; private set; }

        protected override void Dispose0()
        {
            DisposeCalls++;
            base.Dispose0();
        }
    }
}

using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using StArray.ModManager.Behaviours;
using StArray.ModManager.Inspector;
using StArray.ModManager.Manager;
using StArray.ModManager.Resources;
using StArray.ModManager.Runtime;
using StArray.ModManager.UI;

namespace StArray.ModManager.Tests;

public sealed class ModManagerAndroidUpstreamSyncTests
{
    private static readonly MethodInfo ResolvePluginType = typeof(ModLoader).GetMethod(
        "ResolvePluginType",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ModLoader.ResolvePluginType was not found");

    [Test]
    public void RuntimeAbstractionsRequiredByAndroidModsArePresent()
    {
        var assembly = typeof(ModLoader).Assembly;
        var requiredTypes = new[]
        {
            "StArray.ModManager.RuntimeAbstractions.IAppDomain",
            "StArray.ModManager.RuntimeAbstractions.IRuntimeAssembly",
            "StArray.ModManager.RuntimeAbstractions.IRuntimeClass",
            "StArray.ModManager.RuntimeAbstractions.IRuntimeField",
            "StArray.ModManager.RuntimeAbstractions.IRuntimeMethod",
            "StArray.ModManager.RuntimeAbstractions.RuntimeManager",
            "StArray.ModManager.RuntimeAbstractions.RuntimeBackend",
            "StArray.ModManager.RuntimeAbstractions.UnmanagedObject",
            "StArray.ModManager.RuntimeAbstractions.RuntimeObject",
            "StArray.ModManager.RuntimeAbstractions.RuntimeObject`1",
            "StArray.ModManager.RuntimeAbstractions.RuntimeArray",
            "StArray.ModManager.RuntimeAbstractions.RuntimeArray`1",
            "StArray.ModManager.RuntimeAbstractions.RuntimeString",
            "StArray.ModManager.RuntimeAbstractions.RuntimeHelpers",
            "StArray.ModManager.RuntimeAbstractions.UnmanagedEnumerable",
            "StArray.ModManager.RuntimeAbstractions.UnmanagedEnumerable`1",
            "StArray.ModManager.RuntimeAbstractions.UnmanagedTypeAttribute",
            "StArray.ModManager.RuntimeAbstractions.UnmanagedMemberAttribute",
            "StArray.ModManager.RuntimeAbstractions.UnmanagedTypeNameAttribute",
            "StArray.ModManager.RuntimeAbstractions.GraphicsDevice",
            "StArray.ModManager.RuntimeAbstractions.UnityScreen",
        };

        foreach (var typeName in requiredTypes)
            Assert.That(assembly.GetType(typeName, throwOnError: false), Is.Not.Null, typeName);

        var appDomain = assembly.GetType(requiredTypes[0], throwOnError: true)!;
        Assert.Multiple(() =>
        {
            Assert.That(appDomain.GetProperty("Ptr")?.PropertyType, Is.EqualTo(typeof(nint)));
            Assert.That(appDomain.GetProperty("IsValid")?.PropertyType, Is.EqualTo(typeof(bool)));
            Assert.That(appDomain.GetMethod("OpenAssembly", [typeof(string)]), Is.Not.Null);
            Assert.That(appDomain.GetMethod("GetAssemblies", Type.EmptyTypes), Is.Not.Null);
            Assert.That(appDomain.GetMethod("NewString", [typeof(string)])?.ReturnType, Is.EqualTo(typeof(nint)));
            Assert.That(appDomain.GetMethod("NewArray", [typeof(nint), typeof(int)])?.ReturnType,
                Is.EqualTo(typeof(nint)));
        });
    }

    [Test]
    public void AndroidInstallsCoreIl2CppResolverBeforeRuntimeDetection()
    {
        var root = FindRepoRoot();
        var managed = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager.Android", "Managed.cs"));

        var installCall = managed.IndexOf(
            "InstallNativeLibraryResolvers();",
            StringComparison.Ordinal);
        var runtimeDetection = managed.IndexOf(
            "RuntimeManager.Detect();",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(installCall, Is.GreaterThanOrEqualTo(0));
            Assert.That(runtimeDetection, Is.GreaterThan(installCall));
            Assert.That(managed, Does.Contain(
                "TrySetResolver(typeof(Il2CppFunctions).Assembly);"));
            Assert.That(managed, Does.Contain(
                "libraryName.Equals(\"IL2CPP_LIBRARY_NAME\", StringComparison.OrdinalIgnoreCase)"));
            Assert.That(managed, Does.Contain(
                "s_il2CppHandle = Il2CppNativeBridge.GetHandle();"));
            Assert.That(managed, Does.Contain(
                "if (OperatingSystem.IsAndroid() && Il2CppNativeBridge.GetHandle() != IntPtr.Zero)"));
            Assert.That(managed, Does.Contain(
                "RuntimeManager.SetBackend(RuntimeBackend.Il2Cpp);"));
            Assert.That(managed, Does.Contain(
                "if (!PcCompatIl2CppInteropBootstrap.TryStart())"));
            var resolver = File.ReadAllText(Path.Combine(
                root,
                "Il2CppInterop",
                "Il2CppInterop.Runtime",
                "AndroidGameAssemblyResolver.cs"));
            Assert.That(resolver, Does.Contain("[ModuleInitializer]"));
            Assert.That(resolver, Does.Contain(
                "typeof(AndroidGameAssemblyResolver).Assembly"));
            Assert.That(resolver, Does.Contain("modmanager_libil2cpp_handle"));
            Assert.That(resolver, Does.Contain("public static bool WaitForHandle(TimeSpan timeout)"));
            Assert.That(resolver, Does.Contain("Thread.Sleep(10)"));

            var bootstrap = File.ReadAllText(Path.Combine(
                root,
                "StArray.ModManager.Android",
                "PcCompat",
                "PcCompatIl2CppInteropBootstrap.cs"));
            var waitForHandle = bootstrap.IndexOf(
                "AndroidGameAssemblyResolver.WaitForHandle",
                StringComparison.Ordinal);
            var domainGet = bootstrap.IndexOf(
                "IL2CPP.il2cpp_domain_get()",
                StringComparison.Ordinal);
            Assert.That(waitForHandle, Is.GreaterThanOrEqualTo(0));
            Assert.That(domainGet, Is.GreaterThan(waitForHandle));
        });
    }

    [Test]
    public void AndroidIl2CppHandleBridgeWaitsForValidatedAsyncInputProvider()
    {
        var stArrayRoot = FindRepoRoot();
        var asyncInput = File.ReadAllText(Path.Combine(
            stArrayRoot, "external", "AsyncInput", "async_input.c"));
        var runtimeBridge = File.ReadAllText(Path.Combine(
            stArrayRoot,
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "runtime_il2cpp_bridge.c"));

        Assert.Multiple(() =>
        {
            Assert.That(asyncInput, Does.Contain(
                "ADOFAIAsyncInputGetIl2CppHandleV1"));
            Assert.That(asyncInput, Does.Contain("handle_domain_get != mapped_domain_get"));
            Assert.That(runtimeBridge, Does.Contain(
                "adofai_async_il2cpp_handle_provider.ptr"));
            Assert.That(runtimeBridge, Does.Contain(
                "ADOASYNCIL2CPPHANDLE1"));
            Assert.That(runtimeBridge, Does.Contain(
                "ADOFAIAsyncInputGetIl2CppHandleV1"));
            Assert.That(asyncInput, Does.Contain(
                "libstarray_modmanager.so"));
        });
    }

    [Test]
    public void EditorAsyncDriverOwnsOriginalUpdateWithoutDoubleProcessingOfficialInput()
    {
        var stArrayRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(
            stArrayRoot,
            "external",
            "AsyncInput",
            "async_input.c"));
        var editorHook = SliceSource(
            source,
            "static void hooked_editor_update(void *self, void *method)",
            "static void hooked_playercontrol_update(void *self, void *method)");

        var originalUpdate = editorHook.IndexOf(
            "g_original_editor_update(self, method);",
            StringComparison.Ordinal);
        var replayAfterOriginal = editorHook.LastIndexOf(
            "drive_editor_async_update(\"scnEditor.Update.after\");",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(originalUpdate, Is.GreaterThanOrEqualTo(0));
            Assert.That(replayAfterOriginal, Is.GreaterThan(originalUpdate));
            Assert.That(
                editorHook.Split("g_original_editor_update(self, method);").Length - 1,
                Is.EqualTo(2),
                "disabled and enabled paths must each call the original exactly once");
            Assert.That(
                editorHook.Split("drive_editor_async_update(\"scnEditor.Update.after\");").Length - 1,
                Is.EqualTo(1));
            Assert.That(editorHook, Does.Not.Contain("hooked_update_input(controller, NULL);"));
        });
    }

    [Test]
    public void AndroidDobbyHookPreservesUpstreamRvaResolutionContract()
    {
        var root = FindRepoRoot();
        var dobbyHook = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "Native",
            "DobbyHook.cs"));
        var hookContract = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Runtime",
            "IHook.cs"));
        var hookHelper = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager",
            "Runtime",
            "HookHelper.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(hookContract, Does.Contain(
                "nint GetFunctionRVA(string library, long rva)"));
            Assert.That(hookHelper, Does.Contain(
                "public static nint GetFunctionRVA(string library, long rva)"));
            Assert.That(hookHelper, Does.Contain(
                "Instance.GetFunctionRVA(library, rva)"));
            Assert.That(hookHelper, Does.Contain(
                "public static nint GetFunctionRVAFallback(string library, long rva)"));
            Assert.That(dobbyHook, Does.Contain("public nint GetFunctionRVA(string library, long rva)"));
            Assert.That(dobbyHook, Does.Contain("/proc/self/maps"));
            Assert.That(dobbyHook, Does.Contain("NumberStyles.HexNumber"));
            Assert.That(dobbyHook, Does.Contain("return (nint)baseAddress + (nint)rva"));
        });
    }

    [Test]
    public void AndroidHookAttributesPreserveUpstreamPrecompiledModAbi()
    {
        var assembly = typeof(HookHelper).Assembly;
        var nativeHook = assembly.GetType(
            "StArray.ModManager.Hooks.NativeHookAttribute",
            throwOnError: true)!;
        var unmanagedHook = assembly.GetType(
            "StArray.ModManager.Hooks.UnmanagedHookAttribute",
            throwOnError: true)!;

        Assert.Multiple(() =>
        {
            Assert.That(nativeHook.GetProperty("RVA")?.PropertyType, Is.EqualTo(typeof(long)));
            Assert.That(nativeHook.GetProperty("ResolverMethod")?.PropertyType, Is.EqualTo(typeof(string)));
            Assert.That(nativeHook.GetConstructor([typeof(string), typeof(long)]), Is.Not.Null);
            Assert.That(nativeHook.GetConstructor([typeof(string)]), Is.Not.Null);
            Assert.That(unmanagedHook.GetProperty("Namespace")?.PropertyType, Is.EqualTo(typeof(string)));
            Assert.That(unmanagedHook.GetProperty("ParameterTypeNames")?.PropertyType,
                Is.EqualTo(typeof(string[])));
            Assert.That(unmanagedHook.GetConstructor(
                [typeof(string), typeof(string), typeof(string)]), Is.Not.Null);
            Assert.That(unmanagedHook.GetConstructor(
                [typeof(string), typeof(string), typeof(string), typeof(string)]), Is.Not.Null);
        });
    }

    private static string SliceSource(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), startMarker);
        Assert.That(end, Is.GreaterThan(start), endMarker);
        return source[start..end];
    }

    [Test]
    public void EmbeddedFontsMatchApprovedAndroidAssets()
    {
        var assembly = typeof(IImGuiRenderer).Assembly;
        AssertEmbeddedFont(
            assembly,
            "StArray.ModManager.Resources.NotoSansCJK-Regular.otf",
            16_558_780,
            "2B1304A1A2D6B811A38C2C90A2BE503CBAC0BFBF4D8B0E6A6A598146564A61AD");
        AssertEmbeddedFont(
            assembly,
            "StArray.ModManager.Resources.fa-solid-900.ttf",
            322_024,
            "4BA69DAE6214FD61D71F44CD6F2BD802A955FFBD3317A71946EC133F41E8B0F0");
    }

    [TestCase("")]
    [TestCase("en")]
    public void FixedLocalizationTextAvoidsUnsupportedDisplaySymbols(string cultureName)
    {
        var assembly = typeof(IImGuiRenderer).Assembly;
        var manager = new ResourceManager("StArray.ModManager.Resources.Localization", assembly);
        var culture = cultureName.Length == 0
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo(cultureName);
        var resources = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.That(resources, Is.Not.Null, $"resource set missing for '{cultureName}'");

        foreach (DictionaryEntry entry in resources!)
        {
            if (entry.Value is not string value)
                continue;

            foreach (var rune in value.EnumerateRunes())
            {
                Assert.That(
                    IsUnsupportedFixedTextRune(rune),
                    Is.False,
                    $"resource {entry.Key} contains unsupported U+{rune.Value:X} in '{value}'");
            }
        }
    }

    [Test]
    public void AndroidFontAtlasIncludesLocalizedAndDynamicChineseGlyphs()
    {
        const string dynamicText = "罕见汉字测试";
        L10n.RegisterDynamicGlyphText(dynamicText);
        var glyphs = L10n.GetRequiredFontGlyphCodepoints();
        var loader = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "StArray.ModManager.Android",
            "UI",
            "AndroidImGuiFontLoader.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(glyphs, Does.Contain((int)'编'));
            Assert.That(glyphs, Does.Contain((int)'辑'));
            Assert.That(glyphs, Does.Contain((int)'器'));
            Assert.That(glyphs, Does.Contain((int)'需'));
            Assert.That(glyphs, Does.Contain((int)'要'));
            Assert.That(glyphs, Does.Contain((int)'罕'));
            Assert.That(loader, Does.Contain("L10n.GetRequiredFontGlyphCodepoints"));
            Assert.That(loader, Does.Contain("GetGlyphRangesChineseSimplifiedCommon"));
        });
    }

    [Test]
    public void ChineseAndEnglishLocalizationHaveMatchingNonEmptyKeys()
    {
        var assembly = typeof(IImGuiRenderer).Assembly;
        var manager = new ResourceManager("StArray.ModManager.Resources.Localization", assembly);
        var chinese = ReadResourceStrings(manager, CultureInfo.InvariantCulture);
        var english = ReadResourceStrings(manager, CultureInfo.GetCultureInfo("en"));

        Assert.Multiple(() =>
        {
            Assert.That(english.Keys, Is.EquivalentTo(chinese.Keys));
            Assert.That(chinese.Values, Has.None.Empty);
            Assert.That(english.Values, Has.None.Empty);
            Assert.That(english["Settings_Language"], Is.EqualTo("Language:"));
            Assert.That(chinese["Settings_Language"], Is.EqualTo("界面语言:"));
        });
    }

    [Test]
    public void DeclaredModEntryPointWinsWithoutTypeScan()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"DeclaredPlugin_{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);
        var constructor = typeof(ModEntryPointAttribute).GetConstructor([typeof(Type)])!;
        assembly.SetCustomAttribute(new CustomAttributeBuilder(
            constructor,
            [typeof(DeclaredPlugin)]));

        Assert.That(InvokeResolvePluginType(assembly), Is.EqualTo(typeof(DeclaredPlugin)));
    }

    [Test]
    public void InvalidDeclaredEntryFallsBackToConcretePluginScan()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"FallbackPlugin_{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("main");
        var builder = module.DefineType(
            "FallbackPlugin",
            TypeAttributes.Public | TypeAttributes.Class,
            typeof(PluginBase));
        var fallbackType = builder.CreateType()!;

        var constructor = typeof(ModEntryPointAttribute).GetConstructor([typeof(Type)])!;
        assembly.SetCustomAttribute(new CustomAttributeBuilder(constructor, [typeof(string)]));

        Assert.That(InvokeResolvePluginType(assembly), Is.EqualTo(fallbackType));
    }

    [Test]
    public void InvalidSettingMemberDoesNotBlockLaterValidMember()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"starray-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var warnings = new List<string>();
        void OnLog(Logger.Level level, string _, string message)
        {
            if (level == Logger.Level.Warn)
                warnings.Add(message);
        }

        Logger.OnLog += OnLog;
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "settings.json"),
                """
                {
                  "Broken": "not-an-integer",
                  "Valid": 29
                }
                """);

            var entry = new ModEntry { Id = "settings-test", FolderPath = directory };
            var settings = new SettingsFixture();
            ModManagerUI.LoadSettings(entry, settings);

            Assert.Multiple(() =>
            {
                Assert.That(settings.Broken, Is.EqualTo(7));
                Assert.That(settings.Valid, Is.EqualTo(29));
                Assert.That(warnings, Has.Some.Contains("member=Broken"));
            });
        }
        finally
        {
            Logger.OnLog -= OnLog;
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void InspectorKeepsPublicDefaultsAndExplicitPrivateMembers()
    {
        InspectorSettingsFixture.PublicStatic = 2;
        var fixture = new InspectorSettingsFixture();
        var members = ModInspector.GetSettingMembers(typeof(InspectorSettingsFixture));
        var names = members.Select(member => member.Name).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(names, Does.Contain(nameof(InspectorSettingsFixture.PublicField)));
            Assert.That(names, Does.Contain(nameof(InspectorSettingsFixture.PublicProperty)));
            Assert.That(names, Does.Contain(nameof(InspectorSettingsFixture.PublicStatic)));
            Assert.That(names, Does.Contain("PrivateValue"));
            Assert.That(names, Does.Not.Contain("HiddenPrivate"));
            Assert.That(names, Does.Not.Contain(nameof(InspectorSettingsFixture.Transient)));
            Assert.That(names, Does.Not.Contain(nameof(InspectorSettingsFixture.RuntimeValue)));
            Assert.That(names, Does.Not.Contain(nameof(InspectorSettingsFixture.Ignored)));
            Assert.That(names[0], Is.EqualTo(nameof(InspectorSettingsFixture.First)));
            Assert.That(names[^1], Is.EqualTo(nameof(InspectorSettingsFixture.Last)));
        });

        var privateMember = members.Single(member => member.Name == "PrivateValue");
        privateMember.Set(fixture, 41);
        Assert.That(privateMember.Get(fixture), Is.EqualTo(41));
        Assert.That(fixture.ReadPrivateValue(), Is.EqualTo(41));
        Assert.That(fixture.ReadHiddenPrivate(), Is.EqualTo(5));
    }

    [Test]
    public void LoadSettingsUsesInspectorPersistenceMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"starray-inspector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        InspectorSettingsFixture.PublicStatic = 2;

        try
        {
            File.WriteAllText(
                Path.Combine(directory, "settings.json"),
                """
                {
                  "PublicField": 11,
                  "PublicProperty": 12,
                  "PublicStatic": 13,
                  "PrivateValue": 14,
                  "Transient": 99,
                  "RuntimeValue": 98
                }
                """);

            var entry = new ModEntry { Id = "inspector-test", FolderPath = directory };
            var settings = new InspectorSettingsFixture();
            ModManagerUI.LoadSettings(entry, settings);

            Assert.Multiple(() =>
            {
                Assert.That(settings.PublicField, Is.EqualTo(11));
                Assert.That(settings.PublicProperty, Is.EqualTo(12));
                Assert.That(InspectorSettingsFixture.PublicStatic, Is.EqualTo(13));
                Assert.That(settings.ReadPrivateValue(), Is.EqualTo(14));
                Assert.That(settings.Transient, Is.EqualTo(6));
                Assert.That(settings.RuntimeValue, Is.EqualTo(7));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void LegacyInspectorFieldApiKeepsItsPublicInstanceContract()
    {
        var method = typeof(ModInspector).GetMethod(
            nameof(ModInspector.GetInspectorFields),
            BindingFlags.Public | BindingFlags.Static,
            [typeof(Type)]);
        var names = ModInspector.GetInspectorFields(typeof(InspectorSettingsFixture))
            .Select(field => field.Name)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(method, Is.Not.Null);
            Assert.That(names, Does.Contain(nameof(InspectorSettingsFixture.PublicField)));
            Assert.That(names, Does.Contain(nameof(InspectorSettingsFixture.Transient)));
            Assert.That(names, Does.Contain(nameof(InspectorSettingsFixture.RuntimeValue)));
            Assert.That(names, Does.Not.Contain(nameof(InspectorSettingsFixture.PublicProperty)));
            Assert.That(names, Does.Not.Contain(nameof(InspectorSettingsFixture.PublicStatic)));
            Assert.That(names, Does.Not.Contain(nameof(InspectorSettingsFixture.Ignored)));
            Assert.That(names, Does.Not.Contain("PrivateValue"));
        });
    }

    [Test]
    public void AndroidJniWrappersUseAEntryPointsAndOneByteJBoolean()
    {
        var root = FindRepoRoot();
        var jni = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager.Android", "Native", "JNI.cs"));
        var bindings = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager.Android", "Native", "JniHelperNative.cs"));
        var jvalue = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager.Android", "Native", "JValue.cs"));
        var native = File.ReadAllText(Path.Combine(
            root, "Android", "library", "src", "main", "cpp", "core", "jni_helper.c"));
        var header = File.ReadAllText(Path.Combine(
            root, "Android", "library", "src", "main", "cpp", "core", "jni_helper.h"));

        Assert.Multiple(() =>
        {
            Assert.That(jni, Does.Not.Contain("VTable<"));
            Assert.That(jni, Does.Not.Contain("GetDelegateForFunctionPointer"));
            Assert.That(jni, Does.Contain("stackalloc JValue"));
            Assert.That(jni, Does.Contain("args[0].Z = a1 ? (byte)1 : (byte)0"));
            Assert.That(jvalue, Does.Contain("StructLayout(LayoutKind.Explicit, Size = 8)"));
            Assert.That(jvalue, Does.Contain("[FieldOffset(0)] public byte Z"));
            Assert.That(bindings, Does.Contain(
                "EntryPoint = \"jnihelper_call_boolean_method_a\""));
            Assert.That(bindings, Does.Contain(
                "[return: MarshalAs(UnmanagedType.I1)]"));
            Assert.That(native, Does.Contain("(*env)->CallObjectMethodA"));
            Assert.That(native, Does.Contain("(*env)->CallBooleanMethodA"));
            Assert.That(native, Does.Contain("(*env)->CallVoidMethodA"));
            Assert.That(native, Does.Contain("(*env)->CallStaticObjectMethodA"));
            Assert.That(native, Does.Contain("(*env)->CallStaticVoidMethodA"));
            Assert.That(native, Does.Contain("(*env)->CallStaticIntMethodA"));
            Assert.That(native, Does.Contain("JNI exception in CallBooleanMethodA"));
            Assert.That(header, Does.Contain("const jvalue *args"));
        });
    }

    [Test]
    public void AndroidUpstreamNativeFacadesPreserveLocalOwnership()
    {
        var root = FindRepoRoot();
        var jniFacade = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager.Android", "Native", "JniNative.cs"));
        var androidInput = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager.Android", "Native", "AndroidInput.cs"));
        var inputHandler = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager.Android", "UI", "ImGuiInputHandler.cs"));
        var dobby = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager.Android", "Native", "Dobby.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(jniFacade, Does.Contain("public static unsafe class JniNative"));
            Assert.That(jniFacade, Does.Contain("JniHelperNative.CallObjectMethodA"));
            Assert.That(jniFacade, Does.Not.Contain("[DllImport"));
            Assert.That(androidInput, Does.Contain("public static class AndroidInput"));
            Assert.That(androidInput, Does.Contain("AMotionEvent_getPointerCount"));
            Assert.That(inputHandler, Does.Contain("public static void InstallInputHooks() => RegisterImeCallbacks()"));
            Assert.That(inputHandler, Does.Contain("public static void UninstallHooks() { }"));
            Assert.That(dobby, Does.Contain("public static nint _SymbolResolver"));
            Assert.That(dobby, Does.Contain("SymbolResolver(imageName, symbolName)"));
        });
    }

    [Test]
    public void UpstreamConcreteIl2CppSignaturesRemainBinaryCompatible()
    {
        var domain = typeof(StArray.ModManager.Il2Cpp.Il2CppDomain);
        var runtimeAssembly = typeof(StArray.ModManager.RuntimeAbstractions.IRuntimeAssembly);
        var getAssemblies = domain.GetMethod("GetAssemblies", Type.EmptyTypes)!;
        var openAssembly = domain.GetMethod("OpenAssembly", [typeof(string)])!;
        var detach = domain.GetMethod("ThreadDetach", Type.EmptyTypes)!;
        var newObject = typeof(StArray.ModManager.Il2Cpp.Il2CppClass)
            .GetMethod("New", Type.EmptyTypes)!;
        var domainOpen = typeof(StArray.ModManager.Il2Cpp.Il2CppFunctions)
            .GetMethod("il2cpp_domain_assembly_open", [typeof(nint), typeof(string)]);

        Assert.Multiple(() =>
        {
            Assert.That(getAssemblies.ReturnType.IsGenericType, Is.True);
            Assert.That(getAssemblies.ReturnType.GetGenericArguments(), Is.EqualTo([runtimeAssembly]));
            Assert.That(openAssembly.ReturnType, Is.EqualTo(runtimeAssembly));
            Assert.That(detach.IsStatic, Is.False);
            Assert.That(newObject.ReturnType, Is.EqualTo(typeof(nint)));
            Assert.That(domain.GetMethod("GetIl2CppAssemblies", Type.EmptyTypes), Is.Not.Null);
            Assert.That(domain.GetMethod("OpenIl2CppAssembly", [typeof(string)]), Is.Not.Null);
            Assert.That(domainOpen, Is.Not.Null);
        });
    }

    [Test]
    public void NativeImportAndUnmanagedHookGenerationUseLocalResolvers()
    {
        var root = FindRepoRoot();
        var hookGenerator = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager.SourceGenerator", "HookGenerator.cs"));
        var importGenerator = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager.SourceGenerator", "NativeImportGenerator.cs"));
        var coreAssembly = typeof(ModLoader).Assembly;

        Assert.Multiple(() =>
        {
            Assert.That(coreAssembly.GetType("StArray.ModManager.NativeImportAttribute"), Is.Not.Null);
            Assert.That(coreAssembly.GetType("StArray.ModManager.Native.DL"), Is.Not.Null);
            Assert.That(coreAssembly.GetType("StArray.ModManager.Runtime.NativeFuncResolver"), Is.Not.Null);
            Assert.That(coreAssembly.GetType("StArray.ModManager.Runtime.MatchValidator"), Is.Not.Null);
            Assert.That(hookGenerator, Does.Contain("UnmanagedHookAttrName"));
            Assert.That(hookGenerator, Does.Contain("RuntimeAbstractions.RuntimeManager.GetDomain()"));
            Assert.That(hookGenerator, Does.Contain("HookHelper.GetFunctionRVA"));
            Assert.That(hookGenerator, Does.Contain("public static bool InstallHooks()"));
            Assert.That(importGenerator, Does.Contain("HookHelper.GetFunction"));
            Assert.That(importGenerator, Does.Contain("MissingMethodException"));
        });
    }

    [Test]
    public void UpstreamAndroidNonMonoPublicApiClosureIsPresent()
    {
        var root = FindRepoRoot();
        var core = typeof(ModLoader).Assembly;
        var assemblyEmitter = core.GetType("StArray.ModManager.AssemblyEmitter");
        var stubGenerator = core.GetType("StArray.ModManager.StubAssemblyGenerator");
        var behaviourManager = core.GetType("StArray.ModManager.Behaviours.BehaviourManager");
        var gameBehaviour = core.GetType("StArray.ModManager.Behaviours.GameBehaviour");
        var transform = core.GetType("StArray.ModManager.TestStubs.Transform");
        var dl = core.GetType("StArray.ModManager.Native.DL");
        var resolver = core.GetType("StArray.ModManager.Runtime.NativeFuncResolver");
        var renderer = core.GetType("StArray.ModManager.UI.IImGuiRenderer");
        var managerUi = core.GetType("StArray.ModManager.Manager.ModManagerUI");
        var modLoader = core.GetType("StArray.ModManager.Runtime.ModLoader");

        var hotUpdater = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager.Android", "HotUpdater.cs"));
        var logcat = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager.Android", "Logcat.cs"));
        var stubSourceGenerator = File.ReadAllText(Path.Combine(
            root, "StArray.ModManager.SourceGenerator", "UnmanagedStubGenerator.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(assemblyEmitter, Is.Not.Null);
            Assert.That(assemblyEmitter!.GetField("OutputName"), Is.Not.Null);
            Assert.That(assemblyEmitter.GetMethod("GenerateToMods", [typeof(string)]), Is.Not.Null);
            Assert.That(assemblyEmitter.GetMethod("GenerateToDir", [typeof(string), typeof(bool)]), Is.Not.Null);
            Assert.That(stubGenerator?.GetMethod("GenerateToDir", [typeof(string)]), Is.Not.Null);
            Assert.That(behaviourManager, Is.Not.Null);
            Assert.That(gameBehaviour, Is.Not.Null);
            Assert.That(transform?.GetMethod("Find", [typeof(string)]), Is.Not.Null);
            Assert.That(dl?.GetMethod("Addr"), Is.Not.Null);
            Assert.That(resolver, Is.Not.Null);
            Assert.That(resolver!.IsSealed, Is.False);
            Assert.That(renderer?.GetMethod("InitImGui", Type.EmptyTypes), Is.Not.Null);
            Assert.That(managerUi?.GetConstructor([modLoader!, typeof(string)]), Is.Not.Null);
            Assert.That(hotUpdater, Does.Contain("public class HotUpdater"));
            Assert.That(hotUpdater, Does.Contain("public class VersionInfo"));
            Assert.That(hotUpdater, Does.Contain("stackalloc JValue[1]"));
            Assert.That(hotUpdater, Does.Not.Contain("PackArgs("));
            Assert.That(logcat, Does.Contain("public class LogcatCapture"));
            Assert.That(stubSourceGenerator, Does.Contain("public class UnmanagedStubGenerator"));
        });
    }

    [Test]
    [NonParallelizable]
    public void BehaviourManagerDispatchesUpstreamLifecycleOrder()
    {
        BehaviourManager.RemoveAll();
        BehaviourManager.ProcessPending();
        var behaviour = new RecordingBehaviour();

        try
        {
            BehaviourManager.Add(behaviour);
            Assert.That(BehaviourManager.Count, Is.Zero);
            Assert.That(BehaviourManager.RequiresFrame, Is.True);

            BehaviourManager.ProcessPending();
            BehaviourManager.Update(0.25f);
            BehaviourManager.GUI(default);
            behaviour.Enabled = false;
            behaviour.Enabled = true;
            BehaviourManager.Remove(behaviour);
            BehaviourManager.ProcessPending();

            Assert.Multiple(() =>
            {
                Assert.That(behaviour.Events, Is.EqualTo(new[]
                {
                    "Awake", "Enable", "Start", "Update:0.25", "LateUpdate:0.25", "GUI",
                    "Disable", "Enable", "Disable", "Stop",
                }));
                Assert.That(behaviour.IsDestroyed, Is.True);
                Assert.That(BehaviourManager.Count, Is.Zero);
                Assert.That(BehaviourManager.RequiresFrame, Is.False);
            });
        }
        finally
        {
            BehaviourManager.RemoveAll();
            BehaviourManager.ProcessPending();
        }
    }

    private static Type? InvokeResolvePluginType(Assembly assembly)
        => (Type?)ResolvePluginType.Invoke(null, [assembly]);

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(TestContext.CurrentContext.WorkDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "StArray.ModManager.Android")) &&
                Directory.Exists(Path.Combine(current.FullName, "Android")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate StArray.ModManager repo root");
    }

    private static void AssertEmbeddedFont(
        Assembly assembly,
        string resourceName,
        long expectedLength,
        string expectedSha256)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.That(stream, Is.Not.Null, $"missing embedded resource {resourceName}");
        Assert.That(stream!.Length, Is.EqualTo(expectedLength));
        Assert.That(Convert.ToHexString(SHA256.HashData(stream)), Is.EqualTo(expectedSha256));
    }

    private static Dictionary<string, string> ReadResourceStrings(
        ResourceManager manager,
        CultureInfo culture)
    {
        var resourceSet = manager.GetResourceSet(
            culture,
            createIfNotExists: true,
            tryParents: false);
        Assert.That(resourceSet, Is.Not.Null, $"resource set missing for '{culture.Name}'");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in resourceSet!)
        {
            if (entry.Key is string key && entry.Value is string value)
                values.Add(key, value);
        }
        return values;
    }

    private static bool IsUnsupportedFixedTextRune(Rune rune)
    {
        if (rune.IsAscii)
            return false;

        var value = rune.Value;
        if (value is >= 0x2010 and <= 0x2015 or >= 0x2018 and <= 0x201f)
            return true;
        if (value is >= 0x2190 and <= 0x21ff or >= 0x2600 and <= 0x27bf)
            return true;
        if (value is >= 0x1f000 and <= 0x1faff)
            return true;

        return Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.MathSymbol or
            UnicodeCategory.OtherSymbol or
            UnicodeCategory.PrivateUse;
    }

    private sealed class SettingsFixture : IModSettings
    {
        public int Broken = 7;
        public int Valid = 3;

        public void OnGui()
        {
        }
    }

    private sealed class RecordingBehaviour : GameBehaviour
    {
        public List<string> Events { get; } = new();

        public override void OnAwake() => Events.Add("Awake");
        public override void OnEnable() => Events.Add("Enable");
        public override void OnStart() => Events.Add("Start");
        public override void OnUpdate(float delta) => Events.Add($"Update:{delta:0.00}");
        public override void OnLateUpdate(float delta) => Events.Add($"LateUpdate:{delta:0.00}");
        public override void OnGUI(ImGuiNET.ImDrawListPtr drawList) => Events.Add("GUI");
        public override void OnDisable() => Events.Add("Disable");
        public override void OnStop() => Events.Add("Stop");
    }

    private sealed class InspectorSettingsFixture : IModSettings
    {
        [ModSettingOrder(-100)]
        public int First = 8;
        public int PublicField = 1;
        public static int PublicStatic = 2;
        [ModSetting]
        private int PrivateValue = 4;
        private int HiddenPrivate = 5;
        [ModSettingNoSave]
        public int Transient = 6;
        [ModSettingReadOnly]
        public int RuntimeValue = 7;
        [ModSettingIgnore]
        public int Ignored = 10;
        [ModSettingOrder(100)]
        public int Last = 9;

        public int PublicProperty { get; set; } = 3;

        public int ReadPrivateValue() => PrivateValue;
        public int ReadHiddenPrivate() => HiddenPrivate;

        public void OnGui()
        {
        }
    }

    public class PluginBase : IModPlugin
    {
        public string Id => "base";
        public string Name => "base";
        public string Version => "1";
        public string Author => "test";
        public string Description => string.Empty;
        public IReadOnlyList<string> Dependencies => Array.Empty<string>();

        public void OnLoad()
        {
        }

        public void OnUnload()
        {
        }
    }

    private sealed class DeclaredPlugin : PluginBase
    {
    }
}

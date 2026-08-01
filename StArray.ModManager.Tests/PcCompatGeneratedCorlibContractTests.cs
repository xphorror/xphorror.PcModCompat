using dnlib.DotNet;
using System.Reflection;
using System.Runtime.Loader;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatGeneratedCorlibContractTests
{
    [Test]
    public void VirtualBundleLivenessProbeUsesGeneratedUnityObjectTruthiness()
    {
        var root = FindModManagerRoot();
        var proxyPath = Path.Combine(
            root,
            "xphorror.PcModCompat",
            "out",
            "interop",
            "proxy_assemblies",
            "UnityEngine.CoreModule.dll");
        using var proxy = ModuleDefMD.Load(proxyPath);
        var unityObject = proxy.Find("UnityEngine.Object", isReflectionName: false);
        var hideFlags = proxy.Find("UnityEngine.HideFlags", isReflectionName: false);
        var api = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatGeneratedUnityResourceApi.cs"));
        var loader = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatResourceBundleLoader.cs"));
        var ownerHost = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatManagedComponentOwnerHost.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(unityObject, Is.Not.Null);
            Assert.That(hideFlags, Is.Not.Null);
            Assert.That(hideFlags!.Fields.Any(field =>
                field.Name == "DontUnloadUnusedAsset" &&
                field.Constant?.Value is int value && value == 32), Is.True);
            Assert.That(unityObject!.Methods.Any(method =>
                method.Name == "op_Implicit" &&
                method.IsStatic &&
                method.MethodSig?.RetType.FullName == "System.Boolean" &&
                method.MethodSig.Params.Select(parameter => parameter.FullName)
                    .SequenceEqual(["UnityEngine.Object"]) == true), Is.True);
            Assert.That(unityObject.Methods.Any(method =>
                method.Name == "DontDestroyOnLoad" &&
                method.IsStatic &&
                method.MethodSig?.RetType.FullName == "System.Void" &&
                method.MethodSig.Params.Select(parameter => parameter.FullName)
                    .SequenceEqual(["UnityEngine.Object"]) == true), Is.True);
            Assert.That(unityObject.Methods.Any(method =>
                method.Name == "get_hideFlags" &&
                method.MethodSig?.RetType.FullName == "UnityEngine.HideFlags"), Is.True);
            Assert.That(unityObject.Methods.Any(method =>
                method.Name == "set_hideFlags" &&
                method.MethodSig?.Params.Select(parameter => parameter.FullName)
                    .SequenceEqual(["UnityEngine.HideFlags"]) == true), Is.True);
            Assert.That(api, Does.Contain(
                "_objectImplicit = RequiredMethod(_objectType, \"op_Implicit\", true, _objectType)"));
            Assert.That(api, Does.Contain("public bool IsAlive(object proxy)"));
            Assert.That(api, Does.Contain("_applyTexture.Invoke(proxy, [false, false])"));
            Assert.That(api, Does.Contain("private object ProtectFromUnload(object proxy)"));
            Assert.That(api, Does.Contain("flags | DontUnloadUnusedAsset"));
            Assert.That(api.Split("ProtectFromUnload(proxy)").Length - 1, Is.GreaterThanOrEqualTo(3));
            Assert.That(api, Does.Contain("return ProtectFromUnload(shellFontProxy)"));
            Assert.That(api, Does.Contain("ProtectFromUnload(textCoreFont)"));
            Assert.That(api, Does.Contain("ProtectFromUnload(font)"));
            Assert.That(loader, Does.Contain(
                "RegisterAssetLivenessProbe(IsVirtualAssetAlive)"));
            Assert.That(ownerHost, Does.Contain("BuildDontDestroyInvoker"));
            Assert.That(ownerHost, Does.Contain(
                "new MissingMethodException(unityObjectType.FullName, \"DontDestroyOnLoad\")"));
        });
    }

    [Test]
    public void GeneratedUnity6ImGuiFontProjectionUsesTextCoreSurface()
    {
        var root = FindModManagerRoot();
        var proxyDirectory = Path.Combine(
            root,
            "xphorror.PcModCompat",
            "out",
            "interop",
            "proxy_assemblies");
        using var text = ModuleDefMD.Load(Path.Combine(
            proxyDirectory,
            "UnityEngine.TextRenderingModule.dll"));
        using var textCore = ModuleDefMD.Load(Path.Combine(
            proxyDirectory,
            "UnityEngine.TextCoreTextEngineModule.dll"));
        var legacyCharacter = text.Find("UnityEngine.CharacterInfo", isReflectionName: false);
        var font = text.Find("UnityEngine.Font", isReflectionName: false);
        var textAsset = textCore.Find("UnityEngine.TextCore.Text.TextAsset", isReflectionName: false);
        var fontAsset = textCore.Find("UnityEngine.TextCore.Text.FontAsset", isReflectionName: false);
        var character = textCore.Find("UnityEngine.TextCore.Text.Character", isReflectionName: false);

        Assert.Multiple(() =>
        {
            Assert.That(legacyCharacter, Is.Null);
            Assert.That(font, Is.Not.Null);
            Assert.That(font!.Methods.Any(method => method.IsInstanceConstructor &&
                                                     method.MethodSig?.Params.Count == 0), Is.True);
            Assert.That(font.Methods.Any(method => method.Name == "set_material"), Is.True);
            Assert.That(font.Methods.Any(method => method.Name == "set_characterInfo"), Is.False);
            Assert.That(textAsset, Is.Not.Null);
            Assert.That(textAsset!.Methods.Any(method => method.Name == "set_material"), Is.True);
            Assert.That(fontAsset, Is.Not.Null);
            Assert.That(fontAsset!.Methods.Any(method => method.IsInstanceConstructor &&
                                                         method.MethodSig?.Params.Count == 0), Is.True);
            Assert.That(fontAsset.Methods.Any(method => method.Name == "set_faceInfo"), Is.True);
            Assert.That(fontAsset.Methods.Any(method => method.Name == "set_glyphTable"), Is.True);
            Assert.That(fontAsset.Methods.Any(method => method.Name == "set_characterTable"), Is.True);
            foreach (var setter in new[]
                     {
                         "set_regularStyleWeight",
                         "set_regularStyleSpacing",
                         "set_boldStyleWeight",
                         "set_boldStyleSpacing",
                         "set_italicStyleSlant",
                         "set_tabMultiple"
                     })
            {
                Assert.That(fontAsset.Methods.Any(method => method.Name == setter), Is.True);
                Assert.That(
                    fontAsset.Properties.Any(property =>
                        property.Name == setter[4..]),
                    Is.False,
                    $"setter-only proxy member unexpectedly has CLR property metadata: {setter}");
            }
            Assert.That(fontAsset.Methods.Any(method => method.Name == "ReadFontAssetDefinition"), Is.True);
            Assert.That(character, Is.Not.Null);
            Assert.That(character!.Methods.Any(method =>
                method.IsInstanceConstructor &&
                method.MethodSig?.Params.Select(parameter => parameter.FullName).SequenceEqual(
                [
                    "System.UInt32",
                    "UnityEngine.TextCore.Text.FontAsset",
                    "UnityEngine.TextCore.Glyph"
                ]) == true), Is.True);
        });
    }

    [Test]
    public void GeneratedImGuiProxyUsesAndroidAvailablePrimitivesForConvenienceBridge()
    {
        var root = FindModManagerRoot();
        var proxy = Path.Combine(
            root,
            "xphorror.PcModCompat",
            "out",
            "interop",
            "proxy_assemblies",
            "UnityEngine.IMGUIModule.dll");
        var coreProxy = Path.Combine(
            root,
            "xphorror.PcModCompat",
            "out",
            "interop",
            "proxy_assemblies",
            "UnityEngine.CoreModule.dll");
        Assert.That(File.Exists(proxy), Is.True, $"missing generated IMGUI proxy: {proxy}");
        Assert.That(File.Exists(coreProxy), Is.True, $"missing generated core proxy: {coreProxy}");

        using var module = ModuleDefMD.Load(proxy);
        using var coreModule = ModuleDefMD.Load(coreProxy);
        var layout = module.Find("UnityEngine.GUILayout", isReflectionName: false);
        var gui = module.Find("UnityEngine.GUI", isReflectionName: false);
        var utility = module.Find("UnityEngine.GUILayoutUtility", isReflectionName: false);
        var content = module.Find("UnityEngine.GUIContent", isReflectionName: false);
        var style = module.Find("UnityEngine.GUIStyle", isReflectionName: false);
        var styleState = module.Find("UnityEngine.GUIStyleState", isReflectionName: false);
        var rectOffset = coreModule.Find("UnityEngine.RectOffset", isReflectionName: false);

        Assert.Multiple(() =>
        {
            Assert.That(layout, Is.Not.Null);
            Assert.That(gui, Is.Not.Null);
            Assert.That(utility, Is.Not.Null);
            Assert.That(content, Is.Not.Null);
            Assert.That(style, Is.Not.Null);
            Assert.That(styleState, Is.Not.Null);
            Assert.That(rectOffset, Is.Not.Null);
            Assert.That(layout!.Methods.Any(method => method.Name == "DoButton"), Is.True);
            Assert.That(layout.Methods.Any(method => method.Name == "TextArea"), Is.False);
            Assert.That(layout.Methods.Any(method =>
                method.Name == "Button" &&
                method.MethodSig?.Params.Count == 3 &&
                method.MethodSig.Params[0].FullName is
                    "System.String" or "UnityEngine.Texture"), Is.False);
            Assert.That(gui!.Methods.Any(method =>
                method.Name == "DoTextField" && method.MethodSig?.Params.Count == 6), Is.True);
            Assert.That(utility!.Methods.Any(method =>
                method.Name == "GetRect" && method.MethodSig?.Params.Count == 3), Is.True);
            Assert.That(content!.Methods.Count(method => method.IsInstanceConstructor),
                Is.GreaterThanOrEqualTo(2));
            Assert.That(style!.Methods.Any(method =>
                method.Name.String is "set_fixedWidth" or "set_normal" or "set_margin"), Is.False);
            Assert.That(style.Methods.Any(method => method.Name == "set_fontSize"), Is.True);
            Assert.That(style.Methods.Any(method => method.Name == "get_normal"), Is.True);
            Assert.That(style.Methods.Any(method => method.Name == "get_margin"), Is.True);
            Assert.That(styleState!.Methods.Any(method => method.Name == "get_textColor"), Is.True);
            Assert.That(styleState.Methods.Any(method => method.Name == "set_textColor"), Is.True);
            foreach (var edge in new[] { "left", "right", "top", "bottom" })
            {
                Assert.That(rectOffset!.Methods.Any(method => method.Name == "get_" + edge), Is.True);
                Assert.That(rectOffset.Methods.Any(method => method.Name == "set_" + edge), Is.True);
            }
        });
    }

    [Test]
    public void ManagedImGuiBridgeBindsGeneratedProxyWithoutLoadingNativeIl2Cpp()
    {
        var root = FindModManagerRoot();
        var proxyDirectory = Path.Combine(root, "xphorror.PcModCompat", "out", "interop", "proxy_assemblies");
        var runtimeDirectory = Path.Combine(
            root,
            "Il2CppInterop",
            "bin",
            "Il2CppInterop.Runtime",
            "net6.0");
        var proxy = Path.Combine(proxyDirectory, "UnityEngine.IMGUIModule.dll");
        Assert.That(File.Exists(proxy), Is.True, $"missing generated IMGUI proxy: {proxy}");

        var context = new ProxyMetadataLoadContext(proxyDirectory, runtimeDirectory);
        try
        {
            foreach (var dependency in Directory.EnumerateFiles(
                         proxyDirectory,
                         "UnityEngine*.dll",
                         SearchOption.TopDirectoryOnly))
            {
                if (!string.Equals(dependency, proxy, StringComparison.OrdinalIgnoreCase))
                    context.LoadFromAssemblyPath(dependency);
            }
            var assembly = context.LoadFromAssemblyPath(proxy);
            Assert.That(
                assembly.DefinedTypes.Select(type => type.FullName),
                Does.Contain("UnityEngine.GUIContent"),
                "generated IMGUI proxy must define GUIContent before backend binding");
            var backend = typeof(PcCompatManagedImGuiBridge).GetNestedType(
                "Backend",
                BindingFlags.NonPublic);
            var constructor = backend?.GetConstructors(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SingleOrDefault(candidate =>
                    candidate.GetParameters() is [{ ParameterType: var type }] &&
                    type == typeof(Assembly));

            Assert.That(backend, Is.Not.Null);
            Assert.That(constructor, Is.Not.Null);
            Assert.That(() => constructor!.Invoke([assembly]), Throws.Nothing);
        }
        finally
        {
            context.Unload();
        }
    }

    [Test]
    public void AndroidGuiStyleBridgeResolvesMetadataAndIcallWithoutHardcodedAddress()
    {
        var root = FindModManagerRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatAndroidImGuiStyleBridge.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("[ModuleInitializer]"));
            Assert.That(source, Does.Contain("RegisterFixedWidthSetter"));
            Assert.That(source, Does.Contain("UnityEngine.GUIStyle::set_fixedWidth_Injected"));
            Assert.That(source, Does.Contain("IL2CPP.GetIl2CppField(styleClass, \"m_Ptr\")"));
            Assert.That(source, Does.Contain("IL2CPP.il2cpp_field_get_value"));
            Assert.That(source, Does.Contain("IL2CPP.il2cpp_resolve_icall"));
            Assert.That(source, Does.Not.Contain("Dobby"));
            Assert.That(source, Does.Not.Contain("RVA"));
            Assert.That(source, Does.Not.Contain("+ 0x10"));
        });
    }

    [Test]
    public void RuntimePackagesGeneratedCorlibInsteadOfReferenceStub()
    {
        var root = FindModManagerRoot();
        var migration = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "build_interop_migration.ps1"));
        var androidBuild = File.ReadAllText(Path.Combine(root, "build.ps1"));
        var bootstrap = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatIl2CppInteropBootstrap.cs"));

        Assert.That(migration, Does.Not.Contain("--blacklist-assembly Il2Cppmscorlib"));
        Assert.That(migration, Does.Contain("generatedCorlibPackaged = $true"));
        Assert.That(androidBuild, Does.Contain("Runtime Il2Cppmscorlib.dll was not replaced"));
        Assert.That(androidBuild, Does.Contain("Join-Path $proxyStage \"Il2Cppmscorlib.dll\""));
        Assert.That(bootstrap, Does.Contain("ValidateGenericProxyInitializer(listType)"));
        Assert.That(bootstrap, Does.Contain("ValidateAllGenericProxyInitializers()"));
        Assert.That(bootstrap, Does.Contain("ValidateAllNativePointerProducerGuards()"));
        Assert.That(bootstrap, Does.Contain("ValidateNativePointerProducerGuards"));
        Assert.That(bootstrap, Does.Contain("RequireIl2CppPointer"));
        Assert.That(bootstrap, Does.Contain("GetNativeClassPointerForGenericArgument"));
        Assert.That(bootstrap, Does.Contain("Generated generic proxy calls raw il2cpp_class_get_type"));
    }

    [Test]
    public void GeneratedCorlibSurfaceAndAuditCoverHostBridgeMembers()
    {
        var root = FindModManagerRoot();
        var surface = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "tools",
            "ProxyInputClosure",
            "proxy_surface_members.txt"));
        var audit = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "tools",
            "ProxyAssemblyAudit",
            "Program.cs"));
        var closure = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "tools",
            "ProxyInputClosure",
            "Program.cs"));

        Assert.That(surface, Does.Contain("M|mscorlib|System.Nullable`1|instance|0|System.Void|.ctor|!0"));
        Assert.That(surface, Does.Contain("M|mscorlib|System.Action|instance|0|System.Void|.ctor|System.Object;System.IntPtr"));
        Assert.That(closure, Does.Contain("AddDelegateRuntimeMembers"));
        Assert.That(closure, Does.Contain("System.MulticastDelegate"));
        Assert.That(surface, Does.Contain("G|mscorlib|System.Collections.Generic.List`1|Count"));
        Assert.That(surface, Does.Contain("G|mscorlib|System.Collections.Generic.List`1|Item"));
        Assert.That(surface, Does.Contain("F|mscorlib|System.Delegate|method_ptr"));
        Assert.That(surface, Does.Contain("F|mscorlib|System.Delegate|m_target"));
        Assert.That(surface, Does.Contain("F|mscorlib|System.Delegate|method_info"));
        Assert.That(surface, Does.Contain("F|mscorlib|System.Type|_impl"));
        Assert.That(surface, Does.Contain("F|mscorlib|System.RuntimeTypeHandle|value"));
        Assert.That(surface, Does.Contain("|System.Type|internal_from_handle|System.IntPtr"));
        Assert.That(surface, Does.Contain("|System.Reflection.MethodBase|instance|0|System.Reflection.ParameterInfo[]|GetParameters|"));
        Assert.That(surface, Does.Contain("|System.Reflection.MethodInfo|instance|0|System.Reflection.MethodInfo|MakeGenericMethod|System.Type[]"));
        Assert.That(surface, Does.Contain("G|mscorlib|System.Reflection.ParameterInfo|ParameterType"));
        Assert.That(audit, Does.Contain("Object(IntPtr) is still a throw-null reference stub"));
        Assert.That(audit, Does.Contain("Action(Object, IntPtr) delegate constructor is missing"));
        Assert.That(audit, Does.Contain("Nullable<T>(T) is missing"));
        Assert.That(audit, Does.Contain("List<T>.Count getter is missing"));
        Assert.That(audit, Does.Contain("List<T>.Item getter is missing"));
        Assert.That(audit, Does.Contain("Delegate.{field} accessors are missing or ambiguous"));
        Assert.That(audit, Does.Contain("RuntimeTypeHandle.value is missing"));
        Assert.That(audit, Does.Contain("Il2CppReferenceArray`1<Il2CppSystem.Type>"));
        Assert.That(audit, Does.Contain("ParameterType"));
        Assert.That(audit, Does.Contain("calls raw il2cpp_class_get_type"));
        Assert.That(audit, Does.Contain("GetNativeClassPointerForGenericArgument"));
        Assert.That(audit, Does.Contain("AuditAllGenericProxyStaticConstructors"));
        Assert.That(audit, Does.Contain("RequireIl2CppObject"));
        Assert.That(audit, Does.Contain("RequireIl2CppMethod"));
    }

    [Test]
    public void GeneratedGenericProxyValidatesClassPointersBeforeTypeInflation()
    {
        var root = FindModManagerRoot();
        var runtimeStore = File.ReadAllText(Path.Combine(
            root,
            "Il2CppInterop",
            "Il2CppInterop.Runtime",
            "Il2CppClassPointerStore.cs"));
        var generator = File.ReadAllText(Path.Combine(
            root,
            "Il2CppInterop",
            "Il2CppInterop.Generator",
            "Passes",
            "Pass20GenerateStaticConstructors.cs"));
        var genericMethodGenerator = File.ReadAllText(Path.Combine(
            root,
            "Il2CppInterop",
            "Il2CppInterop.Generator",
            "Passes",
            "Pass30GenerateGenericMethodStoreConstructors.cs"));
        var generatorReferences = File.ReadAllText(Path.Combine(
            root,
            "Il2CppInterop",
            "Il2CppInterop.Generator",
            "Utils",
            "RuntimeAssemblyReferences.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(runtimeStore, Does.Contain("GetNativeClassPointerForGenericArgument"));
            Assert.That(runtimeStore, Does.Contain("IL2CPP class pointer is unavailable for generic argument"));
            Assert.That(generator, Does.Contain("IL2CPP_RequireIl2CppClass"));
            Assert.That(generator, Does.Contain("IL2CPP_GetIl2CppTypeForClass"));
            Assert.That(generator, Does.Contain("GetNativeClassPointerForGenericArgument"));
            Assert.That(generator, Does.Contain("typeIdentity + \" inflated class\""));
            Assert.That(genericMethodGenerator, Does.Contain("GetNativeClassPointerForGenericArgument"));
            Assert.That(genericMethodGenerator, Does.Contain("IL2CPP_RequireIl2CppObject"));
            Assert.That(genericMethodGenerator, Does.Contain("IL2CPP_RequireIl2CppMethod"));
            Assert.That(genericMethodGenerator, Does.Not.Contain("IL2CPP_il2cpp_class_get_type"));
            Assert.That(generatorReferences, Does.Not.Contain("IL2CPP_il2cpp_class_get_type"));
        });
    }

    [Test]
    public void GeneratedProxyGuardsNativePointerProducerResults()
    {
        var root = FindModManagerRoot();
        var runtime = File.ReadAllText(Path.Combine(
            root,
            "Il2CppInterop",
            "Il2CppInterop.Runtime",
            "IL2CPP.cs"));
        var staticConstructors = File.ReadAllText(Path.Combine(
            root,
            "Il2CppInterop",
            "Il2CppInterop.Generator",
            "Passes",
            "Pass20GenerateStaticConstructors.cs"));
        var valueTypeConstructors = File.ReadAllText(Path.Combine(
            root,
            "Il2CppInterop",
            "Il2CppInterop.Generator",
            "Passes",
            "Pass25GenerateNonBlittableValueTypeDefaultCtors.cs"));
        var methods = File.ReadAllText(Path.Combine(
            root,
            "Il2CppInterop",
            "Il2CppInterop.Generator",
            "Passes",
            "Pass50GenerateMethods.cs"));
        var ilGenerator = File.ReadAllText(Path.Combine(
            root,
            "Il2CppInterop",
            "Il2CppInterop.Generator",
            "Extensions",
            "ILGeneratorEx.cs"));
        var fieldAccessors = File.ReadAllText(Path.Combine(
            root,
            "Il2CppInterop",
            "Il2CppInterop.Generator",
            "Utils",
            "FieldAccessorGenerator.cs"));
        var generatorReferences = File.ReadAllText(Path.Combine(
            root,
            "Il2CppInterop",
            "Il2CppInterop.Generator",
            "Utils",
            "RuntimeAssemblyReferences.cs"));
        var audit = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "tools",
            "ProxyAssemblyAudit",
            "Program.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(runtime, Does.Contain("throw new MissingFieldException"));
            Assert.That(runtime, Does.Not.Contain("if (clazz == IntPtr.Zero) return IntPtr.Zero;"));
            Assert.That(runtime, Does.Contain("RequireIl2CppPointer"));
            Assert.That(runtime, Does.Contain("return RequireIl2CppPointer(obj.Pointer"));
            Assert.That(runtime, Does.Contain("generic conversion class"));
            Assert.That(runtime, Does.Not.Contain("return obj?.Pointer ?? throw new NullReferenceException();"));
            Assert.That(staticConstructors, Does.Contain("IL2CPP_RequireIl2CppPointer"));
            Assert.That(valueTypeConstructors, Does.Contain("IL2CPP_RequireIl2CppObject"));
            Assert.That(methods, Does.Contain("IL2CPP_RequireIl2CppObject"));
            Assert.That(methods, Does.Contain("IL2CPP_RequireIl2CppMethod"));
            Assert.That(ilGenerator, Does.Contain("EmitGuardedObjectUnbox"));
            Assert.That(ilGenerator, Does.Contain("EmitGuardedObjectClass"));
            Assert.That(ilGenerator, Does.Contain("IL2CPP_RequireIl2CppClass"));
            Assert.That(ilGenerator, Does.Contain("IL2CPP_RequireIl2CppObject"));
            Assert.That(ilGenerator, Does.Contain("IL2CPP_RequireIl2CppPointer"));
            Assert.That(fieldAccessors, Does.Contain("IL2CPP_RequireIl2CppClass"));
            Assert.That(
                fieldAccessors.IndexOf("IL2CPP_RequireIl2CppClass", StringComparison.Ordinal),
                Is.LessThan(fieldAccessors.IndexOf("IL2CPP_il2cpp_class_value_size", StringComparison.Ordinal)));
            Assert.That(generatorReferences, Does.Contain("IL2CPP_RequireIl2CppPointer"));
            Assert.That(audit, Does.Contain("AuditNativePointerProducerGuards"));
            Assert.That(audit, Does.Contain("il2cpp_object_new"));
            Assert.That(audit, Does.Contain("il2cpp_object_get_virtual_method"));
            Assert.That(audit, Does.Contain("il2cpp_object_unbox"));
            Assert.That(audit, Does.Contain("il2cpp_object_get_class"));
            Assert.That(audit, Does.Contain("il2cpp_value_box"));
            Assert.That(audit, Does.Contain("il2cpp_class_is_valuetype"));
        });
    }

    [Test]
    public void AndroidProxyArrayBridgeFailsClosedOnMissingNativePointers()
    {
        var root = FindModManagerRoot();
        var arrayRoot = Path.Combine(
            root,
            "Il2CppInterop",
            "Il2CppInterop.Runtime",
            "InteropTypes",
            "Arrays");
        var arrayBase = File.ReadAllText(Path.Combine(arrayRoot, "Il2CppArrayBase.cs"));
        var referenceArray = File.ReadAllText(Path.Combine(arrayRoot, "Il2CppReferenceArray.cs"));
        var stringArray = File.ReadAllText(Path.Combine(arrayRoot, "Il2CppStringArray.cs"));
        var structArray = File.ReadAllText(Path.Combine(arrayRoot, "Il2CppStructArray.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(arrayBase, Does.Contain("RequireIl2CppClass"));
            Assert.That(arrayBase, Does.Not.Contain("if (nativeClassPtr == IntPtr.Zero)"));
            Assert.That(referenceArray, Does.Contain("RequireIl2CppObject"));
            Assert.That(referenceArray, Does.Contain("RequireIl2CppPointer"));
            Assert.That(stringArray, Does.Contain("RequireIl2CppObject"));
            Assert.That(structArray, Does.Contain("RequireIl2CppObject"));
        });
    }

    [Test]
    public void AndroidProxyObjectBridgeFailsClosedOnMissingNativePointers()
    {
        var root = FindModManagerRoot();
        var objectBase = File.ReadAllText(Path.Combine(
            root,
            "Il2CppInterop",
            "Il2CppInterop.Runtime",
            "InteropTypes",
            "Il2CppObjectBase.cs"));
        var objectPool = File.ReadAllText(Path.Combine(
            root,
            "Il2CppInterop",
            "Il2CppInterop.Runtime",
            "Runtime",
            "Il2CppObjectPool.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(objectBase, Does.Contain("RequireIl2CppObject(pointer"));
            Assert.That(objectBase, Does.Contain("RequireIl2CppClass"));
            Assert.That(objectBase, Does.Contain("RequireIl2CppPointer"));
            Assert.That(objectBase, Does.Contain("if (handle != IntPtr.Zero)"));
            Assert.That(objectPool, Does.Contain("RequireIl2CppClass"));
        });
    }

    [Test]
    public void AndroidDelegateBridgeGuardsNativePointerLookupsAndAllocations()
    {
        var root = FindModManagerRoot();
        var delegateSupport = File.ReadAllText(Path.Combine(
            root,
            "Il2CppInterop",
            "Il2CppInterop.Runtime",
            "DelegateSupport.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(delegateSupport, Does.Contain("GetIl2CppTypeForClass"));
            Assert.That(delegateSupport, Does.Contain("RequireIl2CppPointer"));
            Assert.That(delegateSupport, Does.Contain("nativeType._impl.value"));
            Assert.That(delegateSupport, Does.Contain("RequireIl2CppClass"));
            Assert.That(delegateSupport, Does.Contain("RequireIl2CppObject"));
            Assert.That(delegateSupport, Does.Not.Contain("IL2CPP.il2cpp_class_get_type(classTypePtr)"));
        });
    }

    [Test]
    public void GeneratedProxySurfaceOwnsAsyncAssetBundleConsumption()
    {
        var root = FindModManagerRoot();
        var surface = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "tools",
            "ProxyInputClosure",
            "proxy_surface_members.txt"));
        var loader = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatResourceBundleLoader.cs"));
        var proxyApi = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatGeneratedUnityBundleApi.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(surface, Does.Contain("|UnityEngine.AsyncOperation|instance|0|System.Boolean|get_isDone|"));
            Assert.That(surface, Does.Contain("|UnityEngine.AssetBundleCreateRequest|LoadFromFileAsync|System.String"));
            Assert.That(surface, Does.Contain("|UnityEngine.AssetBundleRequest|LoadAssetAsync|System.String;System.Type"));
            Assert.That(surface, Does.Contain("|UnityEngine.AssetBundle|get_assetBundle|"));
            Assert.That(surface, Does.Contain("|UnityEngine.Object|get_asset|"));
            Assert.That(
                surface,
                Does.Not.Contain(
                    "|UnityEngine.AssetBundle|static|0|UnityEngine.AssetBundle|LoadFromFile|System.String"));
            Assert.That(
                surface,
                Does.Not.Contain(
                    "|UnityEngine.AssetBundle|instance|0|UnityEngine.Object[]|LoadAllAssets|"));
            Assert.That(
                surface,
                Does.Not.Contain("|UnityEngine.AssetBundle|instance|0|UnityEngine.Object|LoadAsset|"));
            Assert.That(loader, Does.Contain("PcCompatGeneratedUnityBundleApi"));
            Assert.That(loader, Does.Not.Contain("UnityResolve"));
            Assert.That(proxyApi, Does.Contain("Il2CppType.From"));
            Assert.That(proxyApi, Does.Not.Contain("IL2CPP.GetIl2CppClass"));
            Assert.That(proxyApi, Does.Contain("Il2CppObjectBase"));
        });
    }

    [Test]
    public void GeneratedProxySurfaceOwnsUnityHudCreationAndLocalizedFont()
    {
        var root = FindModManagerRoot();
        var surface = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "tools",
            "ProxyInputClosure",
            "proxy_surface_members.txt"));
        var bridge = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatUnityHudBridge.cs"));
        var proxyApi = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatGeneratedUnityHudApi.cs"));
        var fallbackBridge = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatKeyViewerFallbackBridge.cs"));
        var fallbackRuntime = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "src",
            "PcCompatKeyViewerFallbackRuntime.cs"));
        var previewRuntime = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "src",
            "PcCompatKeyViewerPreviewRuntime.cs"));
        var audit = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "tools",
            "ProxyAssemblyAudit",
            "Program.cs"));
        var setBatchMesh = SourceBlock(
            proxyApi,
            "    public void SetBatchMesh(",
            "    private MeshUploadBuffer GetMeshUploadBuffer(");

        Assert.Multiple(() =>
        {
            Assert.That(surface, Does.Contain("|UnityEngine.GameObject|instance|0|System.Void|.ctor|System.String;System.Type[]"));
            Assert.That(surface, Does.Contain("|UnityEngine.GameObject|instance|0|UnityEngine.Component|AddComponent|System.Type"));
            Assert.That(surface, Does.Contain("|UnityEngine.GameObject|instance|0|UnityEngine.Component|GetComponent|System.Type"));
            Assert.That(surface, Does.Contain("|UnityEngine.Component|instance|0|UnityEngine.Component|GetComponent|System.Type"));
            Assert.That(surface, Does.Contain("|UnityEngine.Transform|instance|0|System.Void|SetParent|UnityEngine.Transform;System.Boolean"));
            Assert.That(surface, Does.Contain("|TMPro.TMP_Text|instance|0|System.Void|set_font|TMPro.TMP_FontAsset"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.UI.CanvasScaler|instance|0|System.Void|set_uiScaleMode|UnityEngine.UI.CanvasScaler/ScaleMode"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.UI.ContentSizeFitter|instance|0|System.Void|set_horizontalFit|UnityEngine.UI.ContentSizeFitter/FitMode"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.UI.ContentSizeFitter|instance|0|System.Void|set_verticalFit|UnityEngine.UI.ContentSizeFitter/FitMode"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.UI.Image|instance|0|System.Void|set_type|UnityEngine.UI.Image/Type"));
            Assert.That(surface, Does.Contain("G|Unity.TextMeshPro|TMPro.ShaderUtilities|ShaderRef_MobileSDF"));
            Assert.That(surface, Does.Contain("|Assembly-CSharp|RDString|static|0|System.Void|SetLocalizedFont|TMPro.TMP_Text"));
            Assert.That(bridge, Does.Contain("PcCompatGeneratedUnityHudApi"));
            Assert.That(bridge, Does.Not.Contain("UnityResolve"));
            Assert.That(proxyApi, Does.Contain("ApplyLocalizedFont"));
            Assert.That(proxyApi, Does.Contain("_setLocalizedFont"));
            Assert.That(proxyApi, Does.Contain("Il2CppType.From"));
            Assert.That(proxyApi, Does.Not.Contain("IL2CPP.GetIl2CppClass"));
            Assert.That(proxyApi, Does.Contain("_meshUploadBuffers"));
            Assert.That(proxyApi, Does.Contain("CompileVector3ArrayItemSetter"));
            Assert.That(proxyApi, Does.Contain("private sealed class MeshUploadBuffer"));
            Assert.That(proxyApi, Does.Contain("<= 1 => 1"));
            Assert.That(proxyApi, Does.Contain("<= 4 => 4"));
            Assert.That(proxyApi, Does.Contain("<= 16 => 16"));
            Assert.That(proxyApi, Does.Contain("<= 64 => 64"));
            Assert.That(setBatchMesh, Does.Not.Contain("Array.CreateInstance"));
            Assert.That(setBatchMesh, Does.Not.Contain("new Il2CppStructArray<int>"));
            Assert.That(fallbackBridge, Does.Contain("StaleVisualKeys"));
            Assert.That(fallbackBridge, Does.Contain("_rainQuads"));
            Assert.That(fallbackBridge, Does.Contain("_visualIndex"));
            Assert.That(fallbackBridge, Does.Not.Contain("new HashSet"));
            Assert.That(fallbackBridge, Does.Not.Contain("TakeLast"));
            Assert.That(fallbackRuntime, Does.Contain("DispatchFrames.Clear()"));
            Assert.That(fallbackRuntime, Does.Contain("CopyFallbackFeatures"));
            Assert.That(fallbackRuntime, Does.Contain("MonotonicSnapshot"));
            Assert.That(fallbackRuntime, Does.Contain("_featureBuffers"));
            Assert.That(fallbackRuntime, Does.Not.Contain("Snapshot(ModId)"));
            Assert.That(previewRuntime, Does.Contain("CopyFallbackState"));
            Assert.That(previewRuntime, Does.Contain("CopyFallbackFeatures"));
            Assert.That(audit, Does.Contain("AuditHudCoreSurface"));
            Assert.That(audit, Does.Contain("AuditFloatValueTypeLayout"));
            Assert.That(audit, Does.Contain("AuditHudTextSurface"));
            Assert.That(audit, Does.Contain("set_horizontalFit"));
            Assert.That(audit, Does.Contain("set_verticalFit"));
            Assert.That(audit, Does.Contain("set_type"));
            Assert.That(audit, Does.Contain("ShaderRef_MobileSDF getter is missing or ambiguous"));
            Assert.That(audit, Does.Contain("SetLocalizedFont"));
            Assert.That(audit, Does.Contain("UnityEngine.UIModule"));
            Assert.That(audit, Does.Contain("UnityEngine.AudioModule"));
            Assert.That(audit, Does.Contain("UnityEngine.IMGUIModule"));
            Assert.That(audit, Does.Contain("UnityEngine.InputLegacyModule"));
            Assert.That(proxyApi, Does.Contain("CompileInstanceVector2Call"));
        });
    }

    [Test]
    public void GeneratedProxySurfaceOwnsOriginalModSettingsImguiHost()
    {
        var root = FindModManagerRoot();
        var surface = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "tools",
            "ProxyInputClosure",
            "proxy_surface_members.txt"));
        var backend = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "src",
            "PcCompatManagedSettingsUnityBackend.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(surface, Does.Contain(
                "|UnityEngine.Screen|static|0|System.Int32|get_width|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.Screen|static|0|System.Int32|get_height|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.Screen|static|0|System.Single|get_dpi|"));
            Assert.That(surface, Does.Contain(
                "M|Assembly-CSharp|RDString|static|0|System.Void|Setup|"));
            Assert.That(surface, Does.Contain(
                "M|Assembly-CSharp|RDString|static|0|FontData|get_fontData|"));
            Assert.That(surface, Does.Contain(
                "F|Assembly-CSharp|FontData|font"));
            Assert.That(surface, Does.Contain(
                "F|Assembly-CSharp|FontData|fontScale"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.GUI|static|0|UnityEngine.GUISkin|get_skin|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.GUISkin|instance|0|UnityEngine.GUIStyle|get_textField|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.GUISkin|instance|0|UnityEngine.GUIStyle|get_button|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.GUISkin|instance|0|UnityEngine.GUIStyle|get_toggle|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.GUISkin|instance|0|UnityEngine.Font|get_font|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.GUISkin|instance|0|System.Void|set_font|UnityEngine.Font"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.GUIStyle|instance|0|System.Int32|get_fontSize|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.GUIStyle|instance|0|System.Boolean|get_richText|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.GUIStyle|instance|0|System.Void|set_wordWrap|System.Boolean"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.GUIStyle|instance|0|UnityEngine.RectOffset|get_padding|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.GUIStyle|instance|0|UnityEngine.Font|get_font|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.GUIStyle|instance|0|System.Void|set_font|UnityEngine.Font"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.GUILayout|static|0|System.Void|BeginArea|UnityEngine.Rect"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.GUILayout|static|0|UnityEngine.Vector2|BeginScrollView|UnityEngine.Vector2;UnityEngine.GUILayoutOption[]"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.GUI|static|0|System.String|TextField|UnityEngine.Rect;System.String;System.Int32"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.GUILayoutUtility|static|0|UnityEngine.Rect|GetRect|System.Single;System.Single;UnityEngine.GUIStyle;UnityEngine.GUILayoutOption[]"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.GUILayoutUtility|static|0|UnityEngine.Rect|GetLastRect|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.Event|static|0|UnityEngine.Event|get_current|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.Event|instance|0|UnityEngine.EventType|get_type|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.Event|instance|0|UnityEngine.EventType|get_rawType|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.GUIUtility|static|0|System.Int32|get_hotControl|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.GUIUtility|static|0|System.Int32|get_keyboardControl|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.GUI|static|0|System.Boolean|Toggle|UnityEngine.Rect;System.Boolean;UnityEngine.GUIContent;UnityEngine.GUIStyle"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.Object|instance|0|System.Int32|GetInstanceID|"));
            Assert.That(backend, Does.Contain("CloseFrameBestEffort"));
            Assert.That(backend, Does.Contain("CloseContentLayout(ref contentFailure)"));
            Assert.That(backend, Does.Contain("CloseLayoutLevel(ref _scrollOpen"));
            Assert.That(backend, Does.Contain("CloseLayoutLevel(ref _verticalOpen"));
            Assert.That(backend, Does.Contain("CloseLayoutLevel(ref _areaOpen"));
            Assert.That(backend, Does.Contain("TryClaimCanvasSurface"));
            Assert.That(backend, Does.Contain("ApplyMobileSkin"));
            Assert.That(backend, Does.Contain("RestoreMobileSkin"));
            Assert.That(backend, Does.Contain("ResolveLocalizedFont"));
            Assert.That(backend, Does.Contain("ResolveResourceFont"));
            Assert.That(backend, Does.Contain("PcCompatVirtualBundleRegistry.ResolvePreferredAsset"));
            Assert.That(backend, Does.Contain("\"TMPro.TMP_FontAsset\""));
            Assert.That(backend, Does.Contain("fontSource={localizedFont.Source}"));
            Assert.That(backend, Does.Contain("ComputeMobileMetrics"));
            Assert.That(backend, Does.Contain("[DEBUG-settings-surface-v1]"));
            Assert.That(backend, Does.Contain("CaptureImGuiContext"));
            Assert.That(backend, Does.Contain("controls=section:"));
            Assert.That(backend, Does.Contain("action={action} samples={samples} rects={rects}"));
            Assert.That(backend, Does.Contain("FormatRect"));
            Assert.That(backend, Does.Contain("FormatColor"));
            Assert.That(backend, Does.Contain("[PcModCompat][SettingsFont][INFO]"));
            Assert.That(backend, Does.Contain("hasFont=true"));
            Assert.That(backend, Does.Contain("public string Number("));
            Assert.That(backend, Does.Contain("public string Enum("));
            Assert.That(backend, Does.Contain("public int Section("));
            Assert.That(backend, Does.Contain("IsClaimedCanvasSurfaceVisible"));
            Assert.That(backend, Does.Contain("RequireGetter"));
            Assert.That(backend, Does.Not.Contain("RequireProperty(screen"));
        });
    }

    [Test]
    public void GeneratedProxySurfaceOwnsCapabilityBackedMaterialReconstruction()
    {
        var root = FindModManagerRoot();
        var surface = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "tools",
            "ProxyInputClosure",
            "proxy_surface_members.txt"));
        var api = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatGeneratedUnityResourceApi.cs"));
        var loader = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatResourceBundleLoader.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(surface, Does.Contain("|UnityEngine.Material|instance|0|System.Boolean|HasProperty|System.String"));
            Assert.That(surface, Does.Contain("|UnityEngine.Material|instance|0|System.Void|SetTexture|System.String;UnityEngine.Texture"));
            Assert.That(surface, Does.Contain("|UnityEngine.Material|instance|0|System.Void|set_globalIlluminationFlags|UnityEngine.MaterialGlobalIlluminationFlags"));
            Assert.That(api, Does.Contain("public object CreateMaterial("));
            Assert.That(api, Does.Contain("Material capability lacks required property"));
            Assert.That(api, Does.Contain("Destroy(proxy);"));
            Assert.That(loader, Does.Contain("PcCompatResourceIrMaterializationKind.MaterialFromCapabilityShader"));
            Assert.That(loader, Does.Contain("EnsureResourceApi().CreateMaterial("));
        });
    }

    [Test]
    public void GeneratedProxySurfaceOwnsPrefabGraphReconstruction()
    {
        var root = FindModManagerRoot();
        var surface = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "tools",
            "ProxyInputClosure",
            "proxy_surface_members.txt"));
        var api = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatGeneratedUnityResourceApi.cs"));
        var loader = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatResourceBundleLoader.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(surface, Does.Contain(
                "|UnityEngine.Transform|instance|0|System.Void|set_localPosition|UnityEngine.Vector3"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.Transform|instance|0|System.Void|set_localRotation|UnityEngine.Quaternion"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.UI.Image|instance|0|System.Void|set_fillAmount|System.Single"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.UI.RawImage|instance|0|System.Void|set_texture|UnityEngine.Texture"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.CanvasRenderer|instance|0|System.Void|set_cullTransparentMesh|System.Boolean"));
            Assert.That(api, Does.Contain("public object CreatePrefab("));
            Assert.That(api, Does.Contain("__PcCompatPrefabHolder:"));
            Assert.That(api, Does.Contain("_dontDestroyOnLoad.Invoke(null, [holder])"));
            Assert.That(api, Does.Contain("_prefabHolders.Add(root, holder)"));
            Assert.That(loader, Does.Contain("PcCompatResourceIrMaterializationKind.PrefabGraph"));
            Assert.That(loader, Does.Contain("EnsureResourceApi().CreatePrefab(request.Asset, dependencies)"));
        });
    }

    [Test]
    public void GeneratedProxySurfaceOwnsStaticTmpFontReconstruction()
    {
        var root = FindModManagerRoot();
        var surface = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "tools",
            "ProxyInputClosure",
            "proxy_surface_members.txt"));
        var api = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatGeneratedUnityResourceApi.cs"));
        var loader = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatResourceBundleLoader.cs"));
        var bootstrap = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatIl2CppInteropBootstrap.cs"));
        var androidBuild = File.ReadAllText(Path.Combine(root, "build.ps1"));

        Assert.Multiple(() =>
        {
            Assert.That(surface, Does.Contain("|TMPro.TMP_FontAsset|instance|0|System.Void|set_glyphTable|"));
            Assert.That(surface, Does.Contain("|TMPro.TMP_FontAsset|instance|0|System.Void|set_characterTable|"));
            Assert.That(surface, Does.Contain("|TMPro.TMP_FontAsset|instance|0|System.Void|ReadFontAssetDefinition|"));
            Assert.That(surface, Does.Contain("|TMPro.TMP_FontAsset|m_AtlasTexture"));
            Assert.That(surface, Does.Contain("|TMPro.TMP_TextElement|m_ElementType"));
            Assert.That(surface, Does.Contain("|TMPro.TMP_TextElement|m_Unicode"));
            Assert.That(surface, Does.Contain("|TMPro.TMP_TextElement|m_TextAsset"));
            Assert.That(surface, Does.Contain("|TMPro.TMP_TextElement|m_GlyphIndex"));
            Assert.That(surface, Does.Contain("|TMPro.TMP_TextElement|m_Scale"));
            Assert.That(surface, Does.Contain("|UnityEngine.TextCore.Glyph|m_Metrics"));
            Assert.That(surface, Does.Contain("|UnityEngine.TextCore.Glyph|m_AtlasIndex"));
            Assert.That(surface, Does.Not.Contain(
                "|TMPro.TMP_Character|instance|0|System.Void|.ctor|"));
            Assert.That(surface, Does.Not.Contain(
                "|TMPro.TMP_FontFeatureTable|instance|0|System.Void|.ctor|"));
            Assert.That(surface, Does.Not.Contain(
                "|UnityEngine.TextCore.Glyph|instance|0|System.Void|.ctor|"));
            Assert.That(surface, Does.Not.Contain(
                "|UnityEngine.TextCore.FaceInfo|instance|0|System.Void|.ctor|"));
            Assert.That(surface, Does.Not.Contain(
                "|UnityEngine.TextCore.GlyphMetrics|instance|0|System.Void|.ctor|"));
            Assert.That(surface, Does.Not.Contain(
                "|UnityEngine.TextCore.GlyphRect|instance|0|System.Void|.ctor|"));
            Assert.That(surface, Does.Contain("|System.Collections.Generic.List`1|instance|0|System.Void|Add|!0"));
            Assert.That(surface, Does.Not.Contain("|UnityEngine.CharacterInfo|"));
            Assert.That(surface, Does.Not.Contain(
                "|UnityEngine.Font|instance|0|System.Void|set_characterInfo|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.Shader|static|0|UnityEngine.Shader|Find|System.String"));
            Assert.That(api, Does.Contain("public object CreateTmpFont("));
            Assert.That(api, Does.Contain("public object CreateImGuiFontFromTmpAtlas("));
            Assert.That(api, Does.Not.Contain("CreateIl2CppValueArray("));
            Assert.That(api, Does.Contain("_imguiTextCoreFonts.Add(font, textCoreFont)"));
            Assert.That(api, Does.Contain("_tmpFontAtlasTexture.SetValue"));
            Assert.That(api, Does.Contain("_tmpTextElementSetScale(proxy, character.Scale)"));
            Assert.That(api, Does.Contain("CompileDefaultValue(_glyphMetricsType)"));
            Assert.That(api, Does.Contain("CompileIl2CppObjectAllocator(_glyphType)"));
            Assert.That(api, Does.Contain("IL2CPP.il2cpp_object_new"));
            Assert.That(api, Does.Contain("CompileFieldSetter<float>"));
            Assert.That(api, Does.Not.Contain("_tmpFontSetFeatureTable"));
            Assert.That(api, Does.Not.Contain("_tmpFontFeatureTableConstructor"));
            Assert.That(api, Does.Contain("alpha8 ? 1 : 4"));
            Assert.That(api, Does.Contain("_tmpFontReadDefinition.Invoke(shellFontProxy, null)"));
            Assert.That(loader, Does.Contain("PcCompatResourceIrMaterializationKind.TmpFontFromAtlas"));
            Assert.That(loader, Does.Contain("PcCompatResourceIrMaterializationKind.TextureAlpha8"));
            Assert.That(loader, Does.Contain("ResourceIrTmpFontPayloadBinary.Read"));
            Assert.That(loader, Does.Contain(
                "RegisterAssetProjectionResolver(ResolveVirtualAssetProjection)"));
            Assert.That(
                loader.IndexOf("var material = PcCompatVirtualBundleRegistry.ResolveAssetById", StringComparison.Ordinal),
                Is.LessThan(loader.IndexOf("var shell = ResolveCapabilityAsset(request, clone: true)", StringComparison.Ordinal)));
            Assert.That(bootstrap, Does.Contain("UnityEngine.TextCoreFontEngineModule.dll"));
            Assert.That(bootstrap, Does.Contain("UnityEngine.TextCoreTextEngineModule.dll"));
            Assert.That(api, Does.Contain("PcCompatIl2CppInteropBootstrap.RequireReady()"));
            Assert.That(bootstrap, Does.Not.Contain(
                "ValidateType(\"Unity.TextMeshPro\", \"TMPro.TMP_FontFeatureTable\")"));
            Assert.That(androidBuild, Does.Contain("UnityEngine.TextCoreFontEngineModule.dll"));
            Assert.That(androidBuild, Does.Contain("UnityEngine.TextCoreTextEngineModule.dll"));
        });
    }

    [Test]
    public void Unity6ImGuiFontProjectionUsesTextCoreCacheBridge()
    {
        var root = FindModManagerRoot();
        var surface = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "tools",
            "ProxyInputClosure",
            "proxy_surface_members.txt"));
        var api = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatGeneratedUnityResourceApi.cs"));
        var loader = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatResourceBundleLoader.cs"));
        var sink = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "unity_presentation_sink.cpp"));
        var bootstrap = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatIl2CppInteropBootstrap.cs"));
        var androidBuild = File.ReadAllText(Path.Combine(root, "build.ps1"));

        Assert.Multiple(() =>
        {
            Assert.That(surface, Does.Contain(
                "|UnityEngine.TextCore.Text.FontAsset|instance|0|System.Void|.ctor|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.TextCore.Text.FontAsset|instance|0|System.Void|set_glyphTable|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.TextCore.Text.FontAsset|instance|0|System.Void|set_characterTable|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.TextCore.Text.FontAsset|instance|0|System.Void|ReadFontAssetDefinition|"));
            Assert.That(surface, Does.Contain(
                "|UnityEngine.TextCore.Text.Character|instance|0|System.Void|.ctor|System.UInt32;UnityEngine.TextCore.Text.FontAsset;UnityEngine.TextCore.Glyph"));
            Assert.That(api, Does.Contain("CreateImGuiFontFromTmpAtlas("));
            Assert.That(api, Does.Contain("_textCoreFontReadDefinition.Invoke"));
            Assert.That(api, Does.Contain(
                "_textCoreFontRegularStyleWeight = RequiredSingleParameterMethod("));
            Assert.That(api, Does.Contain(
                "InvokeNumericSetter(_textCoreFontRegularStyleWeight"));
            Assert.That(api, Does.Not.Contain(
                "RequiredWritableProperty(_textCoreFontAssetType, \"regularStyleWeight\")"));
            Assert.That(api, Does.Contain("RegisterImGuiFontMapping("));
            Assert.That(api, Does.Contain("UnregisterImGuiFontMapping("));
            Assert.That(api, Does.Not.Contain("CreateLegacyFontFromTmpAtlas("));
            Assert.That(loader, Does.Contain("fontInfo.MaterialAssetId"));
            Assert.That(loader, Does.Contain("fontInfo.AtlasTextureAssetIds.Select"));
            Assert.That(loader, Does.Contain("CreateImGuiFontFromTmpAtlas("));
            Assert.That(sink, Does.Contain("UnityEngine.TextCoreTextEngineModule"));
            Assert.That(sink, Does.Contain(".method_name = \"GetCachedFontAsset\""));
            Assert.That(sink, Does.Contain("using GetCachedFontAssetFn = void *(*)(void *, void *, void *);"));
            Assert.That(sink, Does.Contain("original(text_settings, font, method_info)"));
            Assert.That(sink, Does.Contain("modmanager_pccompat_register_imgui_font_mapping"));
            Assert.That(sink, Does.Contain("modmanager_pccompat_unregister_imgui_font_mapping"));
            Assert.That(sink, Does.Contain(
                "g_imgui_font_mapping_count.load(std::memory_order_acquire)"));
            Assert.That(sink, Does.Not.Contain("for (auto &slot : g_imgui_font_mappings)"));
            Assert.That(bootstrap, Does.Contain("UnityEngine.TextCoreTextEngineModule.dll"));
            Assert.That(androidBuild, Does.Contain("UnityEngine.TextCoreTextEngineModule.dll"));
        });
    }

    [Test]
    public void AndroidRuntimeSeparatesApiShimsFromGeneratedProxyAssemblies()
    {
        var root = FindModManagerRoot();
        var shimBuild = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "build_shims.ps1"));
        var androidBuild = File.ReadAllText(Path.Combine(root, "build.ps1"));

        Assert.Multiple(() =>
        {
            Assert.That(shimBuild, Does.Contain("out\\shims"));
            Assert.That(shimBuild, Does.Contain("$outputs"));
            Assert.That(shimBuild, Does.Contain("Newtonsoft.Json.dll"));
            Assert.That(androidBuild, Does.Contain("pc_compat_shims"));
            Assert.That(androidBuild, Does.Contain("pc_compat_proxies"));
            Assert.That(androidBuild, Does.Contain("$requiredProxies"));
            Assert.That(androidBuild, Does.Contain("Runtime shim dependency missing: Newtonsoft.Json.dll"));
        });
    }

    [Test]
    public void ManagedRuntimeHasNoLegacyOracleEnvironmentBranch()
    {
        var root = FindModManagerRoot();
        var runtime = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "src",
            "PcCompatRuntime.cs"));
        var loader = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "src",
            "PcCompatManagedLoader.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(runtime, Does.Not.Contain("STARRAY_PCMOD_COMPAT_MANAGED_ORACLE"));
            Assert.That(loader, Does.Contain("AllowLegacyStubExecution"));
            Assert.That(loader, Does.Contain("Direct PC MOD execution against legacy Unity stubs is disabled"));
        });
    }

    [Test]
    public void StrictInteropRuntimeResolvesGeneratedGenericMethodsByRuntimeArity()
    {
        var root = FindModManagerRoot();
        var runtime = File.ReadAllText(Path.Combine(
            root,
            "Il2CppInterop",
            "Il2CppInterop.Runtime",
            "IL2CPP.cs"));
        var audit = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "tools",
            "ProxyAssemblyAudit",
            "Program.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(runtime, Does.Contain("TryGetGenericMethodIdentity"));
            Assert.That(runtime, Does.Contain("il2cpp_method_get_object"));
            Assert.That(runtime, Does.Contain("GetGenericArguments"));
            Assert.That(runtime, Does.Contain("il2cpp_array_object_header_size"));
            Assert.That(runtime, Does.Not.Contain("Exact generic IL2CPP method lookup is unavailable"));
            Assert.That(audit, Does.Contain("FindObjectsByType"));
            Assert.That(audit, Does.Contain("genericParameterCount: 1"));
        });
    }

    [Test]
    public void AndroidInteropHostUsesRootedDelegatesAndHookBrokerClassInjection()
    {
        var root = FindModManagerRoot();
        var bootstrap = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatIl2CppInteropBootstrap.cs"));
        var injector = File.ReadAllText(Path.Combine(
            root,
            "Il2CppInterop",
            "Il2CppInterop.Runtime",
            "Injection",
            "ClassInjector.cs"));
        var delegateSupport = File.ReadAllText(Path.Combine(
            root,
            "Il2CppInterop",
            "Il2CppInterop.Runtime",
            "DelegateSupport.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(bootstrap, Does.Contain("EnableClassInjection = true"));
            Assert.That(bootstrap, Does.Contain("HookBrokerDetourProvider"));
            Assert.That(bootstrap, Does.Contain("Dobby.Hook("));
            Assert.That(bootstrap, Does.Contain("public void Dispose()"));
            Assert.That(bootstrap, Does.Not.Contain("Dobby.Destroy("));
            Assert.That(injector, Does.Contain("EnsureClassInjectionEnabled"));
            Assert.That(injector, Does.Contain("Il2Cpp class injection is disabled by the runtime host"));
            Assert.That(delegateSupport, Does.Contain("#if IL2CPPINTEROP_ANDROID_SLIM"));
            Assert.That(delegateSupport, Does.Contain("AndroidRootedDelegateReference"));
            Assert.That(delegateSupport, Does.Contain("ResolveAndroidRootedDelegate"));
            Assert.That(delegateSupport, Does.Contain("RuntimeHelpers.IsReferenceOrContainsReferences<T>()"));
        });
    }

    private static string FindModManagerRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "build.ps1")) &&
                Directory.Exists(Path.Combine(directory.FullName, "xphorror.PcModCompat")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        Assert.Fail("Could not find StArray.ModManager root from test directory");
        return string.Empty;
    }

    private static string SourceBlock(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"Missing source marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.That(end, Is.GreaterThan(start), $"Missing source marker: {endMarker}");
        return source[start..end];
    }

    private sealed class ProxyMetadataLoadContext(params string[] searchDirectories)
        : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            foreach (var directory in searchDirectories)
            {
                var candidate = Path.Combine(directory, assemblyName.Name + ".dll");
                if (File.Exists(candidate))
                    return LoadFromAssemblyPath(candidate);
            }
            return null;
        }
    }
}

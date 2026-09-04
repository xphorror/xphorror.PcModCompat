using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace StArray.ModManager.Tests;

public sealed class PcCompatProxySurfaceScannerTests
{
    [Test]
    public void ClassifiesRewrittenAssetBundleCallsAsBridgeOwned()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                ProxySurfaceIdentity.IsManagedBridgeOwnedEntry(
                    "M|UnityEngine.AssetBundleModule|UnityEngine.AssetBundle|static|0|UnityEngine.AssetBundle|LoadFromFile|System.String"),
                Is.True);
            Assert.That(
                ProxySurfaceIdentity.IsManagedBridgeOwnedEntry(
                    "M|UnityEngine.AssetBundleModule|UnityEngine.AssetBundle|instance|0|UnityEngine.Object[]|LoadAllAssets|"),
                Is.True);
            Assert.That(
                ProxySurfaceIdentity.IsManagedBridgeOwnedEntry(
                    "M|UnityEngine.AssetBundleModule|UnityEngine.AssetBundle|static|0|UnityEngine.AssetBundleCreateRequest|LoadFromFileAsync|System.String"),
                Is.False);
        });
    }

    [Test]
    public void ClassifiesAndroidStrippedGUILayoutConvenienceCallsAsBridgeOwned()
    {
        string[] bridgeOwned =
        [
            "M|UnityEngine.IMGUIModule|UnityEngine.GUILayout|static|0|System.Boolean|Button|System.String;UnityEngine.GUIStyle;UnityEngine.GUILayoutOption[]",
            "M|UnityEngine.IMGUIModule|UnityEngine.GUILayout|static|0|System.Boolean|Button|UnityEngine.Texture;UnityEngine.GUIStyle;UnityEngine.GUILayoutOption[]",
            "M|UnityEngine.IMGUIModule|UnityEngine.GUILayout|static|0|System.String|TextArea|System.String;UnityEngine.GUILayoutOption[]"
        ];

        Assert.That(
            bridgeOwned.All(ProxySurfaceIdentity.IsManagedBridgeOwnedEntry),
            Is.True,
            "Android-stripped convenience overloads must not enter generated proxy static constructors");
    }

    [Test]
    public void ClassifiesUnity6StrippedGuiStyleSettersAsBridgeOwned()
    {
        string[] bridgeOwned =
        [
            "M|UnityEngine.IMGUIModule|UnityEngine.GUIStyle|instance|0|System.Void|set_fixedWidth|System.Single",
            "M|UnityEngine.IMGUIModule|UnityEngine.GUIStyle|instance|0|System.Void|set_normal|UnityEngine.GUIStyleState",
            "M|UnityEngine.IMGUIModule|UnityEngine.GUIStyle|instance|0|System.Void|set_margin|UnityEngine.RectOffset"
        ];

        Assert.That(
            bridgeOwned.All(ProxySurfaceIdentity.IsManagedBridgeOwnedEntry),
            Is.True,
            "Unity 6 stripped GUIStyle setters must not enter generated proxy static constructors");
    }

    [Test]
    public void ClassifiesUnity6StrippedGuiFocusCallsAsBridgeOwned()
    {
        string[] bridgeOwned =
        [
            "M|UnityEngine.IMGUIModule|UnityEngine.GUI|static|0|System.Void|SetNextControlName|System.String",
            "M|UnityEngine.IMGUIModule|UnityEngine.GUI|static|0|System.String|GetNameOfFocusedControl|",
            "M|UnityEngine.IMGUIModule|UnityEngine.GUI|static|0|System.Void|DragWindow|UnityEngine.Rect"
        ];

        Assert.That(
            bridgeOwned.All(ProxySurfaceIdentity.IsManagedBridgeOwnedEntry),
            Is.True,
            "Unity 6 stripped GUI focus wrappers must not enter the shared GUI proxy static constructor");
    }

    [Test]
    public void ClassifiesAndroidStrippedNativeGuiSurfaceCallsAsBridgeOwned()
    {
        string[] bridgeOwned =
        [
            "M|UnityEngine.IMGUIModule|UnityEngine.GUI|static|0|System.Void|DrawTexture|UnityEngine.Rect;UnityEngine.Texture",
            "M|UnityEngine.IMGUIModule|UnityEngine.GUILayout|static|0|System.Boolean|Toggle|System.Boolean;UnityEngine.GUIContent;UnityEngine.GUILayoutOption[]",
            "M|UnityEngine.IMGUIModule|UnityEngine.GUILayout|static|0|System.Void|Label|UnityEngine.GUIContent;UnityEngine.GUILayoutOption[]",
            "M|UnityEngine.IMGUIModule|UnityEngine.GUILayout|static|0|System.Int32|SelectionGrid|System.Int32;System.String[];System.Int32;UnityEngine.GUILayoutOption[]",
            "M|UnityEngine.IMGUIModule|UnityEngine.GUILayout|static|0|System.Single|HorizontalSlider|System.Single;System.Single;System.Single;UnityEngine.GUILayoutOption[]",
            "M|UnityEngine.IMGUIModule|UnityEngine.GUILayout|static|0|System.String|TextField|System.String;UnityEngine.GUILayoutOption[]",
            "M|UnityEngine.IMGUIModule|UnityEngine.GUILayout|static|0|System.Void|BeginVertical|UnityEngine.GUIStyle;UnityEngine.GUILayoutOption[]",
            "M|UnityEngine.IMGUIModule|UnityEngine.GUILayoutUtility|static|0|UnityEngine.Rect|GetRect|System.Single;System.Single"
        ];

        Assert.That(
            bridgeOwned.All(ProxySurfaceIdentity.IsManagedBridgeOwnedEntry),
            Is.True,
            "Android-stripped native GUI wrappers must be implemented by the managed bridge");
    }

    [Test]
    public void ClassifiesManagedJsonCallsAsBridgeOwned()
    {
        string[] bridgeOwned =
        [
            "M|UnityEngine.JSONSerializeModule|UnityEngine.JsonUtility|static|0|System.String|ToJson|System.Object;System.Boolean",
            "M|UnityEngine.JSONSerializeModule|UnityEngine.JsonUtility|static|0|System.Void|FromJsonOverwrite|System.String;System.Object",
            "M|UnityEngine.JSONSerializeModule|UnityEngine.JsonUtility|static|1|!!0|FromJson|System.String"
        ];

        Assert.That(
            bridgeOwned.All(ProxySurfaceIdentity.IsManagedBridgeOwnedEntry),
            Is.True,
            "managed JSON calls must not initialize the shared native proxy surface");
    }

    [Test]
    public void ManualSurfaceManifestDoesNotFeedBridgeOwnedCallsIntoProxyClosure()
    {
        var current = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        string? root = null;
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "StArray.ModManager.slnx")))
            {
                root = current.FullName;
                break;
            }
            current = current.Parent;
        }

        Assume.That(root, Is.Not.Null);
        var surfacePath = Path.Combine(
            root!,
            "xphorror.PcModCompat",
            "tools",
            "ProxyInputClosure",
            "proxy_surface_members.txt");
        Assume.That(File.Exists(surfacePath), Is.True, surfacePath);

        var bridgeOwned = File.ReadLines(surfacePath)
            .Select(line => line.Trim())
            .Where(line => line.Length != 0 && !line.StartsWith("#", StringComparison.Ordinal))
            .Where(ProxySurfaceIdentity.IsManagedBridgeOwnedEntry)
            .ToArray();

        Assert.That(
            bridgeOwned,
            Is.Empty,
            "bridge-owned methods must be rewritten to host bridges and excluded from shared proxies");
    }

    [Test]
    public void GeneratedProxySurfaceContainsNoManagedBridgeOwnedMethodSignatures()
    {
        var root = FindRepoRoot();
        var proxyDirectory = Path.Combine(
            root,
            "xphorror.PcModCompat",
            "out",
            "interop",
            "proxy_assemblies");
        Assume.That(Directory.Exists(proxyDirectory), Is.True, proxyDirectory);

        var leaked = new List<string>();
        foreach (var path in Directory.EnumerateFiles(proxyDirectory, "*.dll"))
        {
            using var module = ModuleDefMD.Load(path);
            var assemblyName = module.Assembly.Name.String;
            foreach (var type in module.GetTypes().Where(candidate => !candidate.IsGlobalModuleType))
            {
                foreach (var method in type.Methods)
                {
                    var signature = method.MethodSig;
                    if (signature is null)
                        continue;
                    var entry = string.Join(
                        '|',
                        "M",
                        assemblyName,
                        ProxySurfaceIdentity.NormalizeTypeName(type.FullName),
                        method.IsStatic ? "static" : "instance",
                        method.GenericParameters.Count,
                        ProxySurfaceIdentity.NormalizeTypeName(SurfaceTypeIdentity(signature.RetType)),
                        method.Name.String,
                        string.Join(
                            ';',
                            signature.Params.Select(parameter =>
                                ProxySurfaceIdentity.NormalizeTypeName(SurfaceTypeIdentity(parameter)))));
                    if (ProxySurfaceIdentity.IsManagedBridgeOwnedEntry(entry))
                        leaked.Add(entry);
                }
            }
        }

        Assert.That(
            leaked,
            Is.Empty,
            "bridge-owned methods leaked into generated shared proxies");
    }

    [Test]
    public void CanonicalizesNestedTypesToSurfaceManifestSeparator()
    {
        const string scannerEntry =
            "M|UnityEngine.UI|UnityEngine.UI.CanvasScaler|instance|0|System.Void|set_uiScaleMode|" +
            "UnityEngine.UI.CanvasScaler+ScaleMode";
        const string manifestEntry =
            "M|UnityEngine.UI|UnityEngine.UI.CanvasScaler|instance|0|System.Void|set_uiScaleMode|" +
            "UnityEngine.UI.CanvasScaler/ScaleMode";

        Assert.Multiple(() =>
        {
            Assert.That(
                ProxySurfaceIdentity.NormalizeTypeName("UnityEngine.UI.Image+Type"),
                Is.EqualTo("UnityEngine.UI.Image/Type"));
            Assert.That(
                ProxySurfaceIdentity.NormalizeEntry(scannerEntry),
                Is.EqualTo(manifestEntry));
            Assert.That(
                ProxySurfaceIdentity.NormalizeEntry(manifestEntry),
                Is.EqualTo(manifestEntry));
        });
    }

    [Test]
    public void FindsConstantPropertyNameThroughReflectionHelper()
    {
        var fixture = CreateFixture();
        var propertyInfo = fixture.Module.CorLibTypes.GetTypeRef("System.Reflection", "PropertyInfo");
        var helperType = new TypeRefUser(
            fixture.Module,
            "JALib.Tools",
            "SimpleReflect",
            new AssemblyRefUser(new AssemblyNameInfo("JALib")));
        var propertyLookup = new MemberRefUser(
            fixture.Module,
            "Property",
            MethodSig.CreateStatic(
                new ClassSig(propertyInfo),
                new ClassSig(fixture.SystemType),
                fixture.Module.CorLibTypes.String),
            helperType);

        fixture.Method.Body.Instructions.Add(OpCodes.Ldtoken.ToInstruction(fixture.TargetType));
        fixture.Method.Body.Instructions.Add(OpCodes.Call.ToInstruction(fixture.GetTypeFromHandle));
        fixture.Method.Body.Instructions.Add(OpCodes.Ldstr.ToInstruction("ShaderRef_MobileSDF"));
        fixture.Method.Body.Instructions.Add(OpCodes.Call.ToInstruction(propertyLookup));
        fixture.Method.Body.Instructions.Add(OpCodes.Pop.ToInstruction());
        fixture.Method.Body.Instructions.Add(OpCodes.Ret.ToInstruction());

        var result = ReflectionSurfaceFlowScanner.Scan(fixture.Method);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(result[0].Kind, Is.EqualTo(ReflectedMemberKind.Property));
            Assert.That(result[0].MemberName, Is.EqualTo("ShaderRef_MobileSDF"));
            Assert.That(result[0].DeclaringType.FullName, Is.EqualTo("TMPro.ShaderUtilities"));
            Assert.That(result[0].DeclaringType.DefinitionAssembly?.Name?.String,
                Is.EqualTo("Unity.TextMeshPro"));
        });
    }

    [Test]
    public void FindsSystemTypeFieldLookupAfterLocalRoundTrip()
    {
        var fixture = CreateFixture();
        var fieldInfo = fixture.Module.CorLibTypes.GetTypeRef("System.Reflection", "FieldInfo");
        var fieldLookup = new MemberRefUser(
            fixture.Module,
            "GetField",
            MethodSig.CreateInstance(
                new ClassSig(fieldInfo),
                fixture.Module.CorLibTypes.String),
            fixture.SystemType);
        fixture.Method.Body.Variables.Add(new Local(new ClassSig(fixture.SystemType)));

        fixture.Method.Body.Instructions.Add(OpCodes.Ldtoken.ToInstruction(fixture.TargetType));
        fixture.Method.Body.Instructions.Add(OpCodes.Call.ToInstruction(fixture.GetTypeFromHandle));
        fixture.Method.Body.Instructions.Add(OpCodes.Stloc_0.ToInstruction());
        fixture.Method.Body.Instructions.Add(OpCodes.Ldloc_0.ToInstruction());
        fixture.Method.Body.Instructions.Add(OpCodes.Ldstr.ToInstruction("ID_OutlineColor"));
        fixture.Method.Body.Instructions.Add(OpCodes.Callvirt.ToInstruction(fieldLookup));
        fixture.Method.Body.Instructions.Add(OpCodes.Pop.ToInstruction());
        fixture.Method.Body.Instructions.Add(OpCodes.Ret.ToInstruction());

        var result = ReflectionSurfaceFlowScanner.Scan(fixture.Method);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(result[0].Kind, Is.EqualTo(ReflectedMemberKind.Field));
            Assert.That(result[0].MemberName, Is.EqualTo("ID_OutlineColor"));
            Assert.That(result[0].DeclaringType.FullName, Is.EqualTo("TMPro.ShaderUtilities"));
        });
    }

    [Test]
    public void FindsPropertyLookupThroughGetTypeOnTypedInstance()
    {
        var fixture = CreateFixture();
        var propertyInfo = fixture.Module.CorLibTypes.GetTypeRef("System.Reflection", "PropertyInfo");
        var objectType = fixture.Module.CorLibTypes.GetTypeRef("System", "Object");
        var getType = new MemberRefUser(
            fixture.Module,
            "GetType",
            MethodSig.CreateInstance(new ClassSig(fixture.SystemType)),
            objectType);
        var propertyLookup = new MemberRefUser(
            fixture.Module,
            "GetProperty",
            MethodSig.CreateInstance(
                new ClassSig(propertyInfo),
                fixture.Module.CorLibTypes.String),
            fixture.SystemType);
        fixture.Method.MethodSig = MethodSig.CreateStatic(
            fixture.Module.CorLibTypes.Void,
            new ClassSig(fixture.TargetType));

        fixture.Method.Body.Instructions.Add(OpCodes.Ldarg_0.ToInstruction());
        fixture.Method.Body.Instructions.Add(OpCodes.Callvirt.ToInstruction(getType));
        fixture.Method.Body.Instructions.Add(OpCodes.Ldstr.ToInstruction("material"));
        fixture.Method.Body.Instructions.Add(OpCodes.Callvirt.ToInstruction(propertyLookup));
        fixture.Method.Body.Instructions.Add(OpCodes.Pop.ToInstruction());
        fixture.Method.Body.Instructions.Add(OpCodes.Ret.ToInstruction());

        var result = ReflectionSurfaceFlowScanner.Scan(fixture.Method);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(result[0].Kind, Is.EqualTo(ReflectedMemberKind.Property));
            Assert.That(result[0].MemberName, Is.EqualTo("material"));
            Assert.That(result[0].DeclaringType.FullName, Is.EqualTo("TMPro.ShaderUtilities"));
            Assert.That(result[0].DeclaringType.DefinitionAssembly?.Name?.String,
                Is.EqualTo("Unity.TextMeshPro"));
        });
    }

    private static ReflectionFixture CreateFixture()
    {
        var module = new ModuleDefUser(
            "reflection-surface-fixture",
            Guid.NewGuid(),
            new AssemblyRefUser(new AssemblyNameInfo(typeof(object).Assembly.GetName().FullName)));
        var systemType = module.CorLibTypes.GetTypeRef("System", "Type");
        var runtimeTypeHandle = module.CorLibTypes.GetTypeRef("System", "RuntimeTypeHandle");
        var getTypeFromHandle = new MemberRefUser(
            module,
            "GetTypeFromHandle",
            MethodSig.CreateStatic(
                new ClassSig(systemType),
                new ValueTypeSig(runtimeTypeHandle)),
            systemType);
        var targetType = new TypeRefUser(
            module,
            "TMPro",
            "ShaderUtilities",
            new AssemblyRefUser(new AssemblyNameInfo("Unity.TextMeshPro")));
        var method = new MethodDefUser(
            "ScanTarget",
            MethodSig.CreateStatic(module.CorLibTypes.Void))
        {
            Body = new CilBody()
        };
        return new ReflectionFixture(module, method, systemType, getTypeFromHandle, targetType);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "StArray.ModManager.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the StArray.ModManager root");
    }

    private static string SurfaceTypeIdentity(TypeSig type)
    {
        type = type.RemovePinnedAndModifiers();
        return type switch
        {
            GenericMVar methodVariable => "!!" + methodVariable.Number,
            GenericVar typeVariable => "!" + typeVariable.Number,
            SZArraySig array => SurfaceTypeIdentity(array.Next) + "[]",
            ByRefSig byRef => SurfaceTypeIdentity(byRef.Next) + "&",
            PtrSig pointer => SurfaceTypeIdentity(pointer.Next) + "*",
            GenericInstSig generic =>
                generic.GenericType.TypeDefOrRef.FullName + "<" +
                string.Join(",", generic.GenericArguments.Select(SurfaceTypeIdentity)) + ">",
            _ => type.FullName
        };
    }

    private sealed record ReflectionFixture(
        ModuleDefUser Module,
        MethodDefUser Method,
        TypeRef SystemType,
        MemberRefUser GetTypeFromHandle,
        TypeRefUser TargetType);
}

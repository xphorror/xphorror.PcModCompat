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

    private sealed record ReflectionFixture(
        ModuleDefUser Module,
        MethodDefUser Method,
        TypeRef SystemType,
        MemberRefUser GetTypeFromHandle,
        TypeRefUser TargetType);
}

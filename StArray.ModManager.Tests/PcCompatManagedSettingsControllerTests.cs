using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatManagedSettingsControllerTests
{
    [Test]
    public void OptionalSettingsDelegateBridgePreservesNullAndCachesLiveReceiver()
    {
        var method = typeof(OptionalCallbackTarget).GetMethod(
            "Invoke",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var target = new OptionalCallbackTarget();

        var missing = PcCompatManagedSettingsDelegateBridge.CreateOptionalAction(
            null,
            method.MethodHandle,
            method.DeclaringType!.TypeHandle);
        var first = PcCompatManagedSettingsDelegateBridge.CreateOptionalAction(
            target,
            method.MethodHandle,
            method.DeclaringType!.TypeHandle);
        var second = PcCompatManagedSettingsDelegateBridge.CreateOptionalAction(
            target,
            method.MethodHandle,
            method.DeclaringType!.TypeHandle);
        first!();

        Assert.Multiple(() =>
        {
            Assert.That(missing, Is.Null);
            Assert.That(second, Is.SameAs(first));
            Assert.That(target.Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void OpenDrawSaveAndCloseAreDispatchedInOrder()
    {
        var target = new SettingsTarget();

        Assert.That(PcCompatManagedSettingsController.TryCreate(
            target,
            null,
            out var controller,
            out var error), Is.True, error);
        Assert.That(controller!.RequestOpen(out error), Is.True, error);
        Assert.That(controller.Snapshot().State,
            Is.EqualTo(PcCompatManagedSettingsState.Opening));

        Assert.That(controller.Dispatch(), Is.True);
        Assert.That(controller.Snapshot().State,
            Is.EqualTo(PcCompatManagedSettingsState.Open));
        controller.RequestSave();
        Assert.That(controller.Dispatch(), Is.True);
        controller.RequestClose();
        Assert.That(controller.Dispatch(), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot().State,
                Is.EqualTo(PcCompatManagedSettingsState.Closed));
            Assert.That(target.Calls,
                Is.EqualTo(new[] { "open", "draw", "save", "draw", "close" }));
        });
    }

    [Test]
    public void ClosedSettingsSurfaceCanBeOpenedAgainInTheSameSession()
    {
        var target = new SettingsTarget();
        Assert.That(PcCompatManagedSettingsController.TryCreate(
            target,
            null,
            out var controller,
            out var error), Is.True, error);

        Assert.That(controller!.RequestOpen(out error), Is.True, error);
        Assert.That(controller.Dispatch(), Is.True);
        controller.RequestClose();
        Assert.That(controller.Dispatch(), Is.True);
        Assert.That(controller.RequestOpen(out error), Is.True, error);
        Assert.That(controller.Dispatch(), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot().State,
                Is.EqualTo(PcCompatManagedSettingsState.Open));
            Assert.That(target.Calls,
                Is.EqualTo(new[] { "open", "draw", "close", "open", "draw" }));
        });
    }

    [Test]
    public void UnityMainFrameDoesNotPublishOpenBeforeFirstSuccessfulImGuiDraw()
    {
        var target = new SettingsTarget();
        Assert.That(PcCompatManagedSettingsController.TryCreate(
            target,
            null,
            out var controller,
            out var error), Is.True, error);

        Assert.That(controller!.RequestOpen(out error), Is.True, error);
        Assert.That(controller.RequiresFrameDispatch, Is.True);
        Assert.That(controller.DispatchFrame(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot().State,
                Is.EqualTo(PcCompatManagedSettingsState.Opening));
            Assert.That(controller.RequiresOnGUIDispatch, Is.True);
            Assert.That(target.Calls, Is.EqualTo(new[] { "open" }));
        });

        Assert.That(controller.Dispatch(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot().State,
                Is.EqualTo(PcCompatManagedSettingsState.Open));
            Assert.That(target.Calls, Is.EqualTo(new[] { "open", "draw" }));
        });
        controller.RequestClose();
        Assert.That(controller.RequiresFrameDispatch, Is.True);
        Assert.That(controller.DispatchFrame(), Is.True);
        Assert.That(controller.Snapshot().State,
            Is.EqualTo(PcCompatManagedSettingsState.Closed));

        Assert.That(controller.RequestOpen(out error), Is.True, error);
        Assert.That(controller.DispatchFrame(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot().State,
                Is.EqualTo(PcCompatManagedSettingsState.Opening));
            Assert.That(target.Calls,
                Is.EqualTo(new[] { "open", "draw", "close", "open" }));
        });
    }

    [Test]
    public void DrawFailureFaultsOnlyTheSettingsSurfaceAndClosesBestEffort()
    {
        var target = new SettingsTarget { ThrowOnDraw = true };
        Assert.That(PcCompatManagedSettingsController.TryCreate(
            target,
            null,
            out var controller,
            out var error), Is.True, error);
        Assert.That(controller!.RequestOpen(out error), Is.True, error);

        Assert.That(controller.Dispatch(), Is.False);
        var snapshot = controller.Snapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.State, Is.EqualTo(PcCompatManagedSettingsState.Faulted));
            Assert.That(snapshot.Fault, Does.Contain("settings draw failed"));
            Assert.That(target.Calls, Is.EqualTo(new[] { "open", "draw", "close" }));
        });
    }

    [Test]
    public void ExplicitOpenRetriesASettingsSurfaceAfterAnIsolatedFault()
    {
        var target = new SettingsTarget { ThrowOnDraw = true };
        Assert.That(PcCompatManagedSettingsController.TryCreate(
            target,
            null,
            out var controller,
            out var error), Is.True, error);
        Assert.That(controller!.RequestOpen(out error), Is.True, error);
        Assert.That(controller.Dispatch(), Is.False);
        Assert.That(controller.Snapshot().State,
            Is.EqualTo(PcCompatManagedSettingsState.Faulted));

        target.ThrowOnDraw = false;
        Assert.That(controller.RequestOpen(out error), Is.True, error);
        Assert.That(controller.Dispatch(), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot().State,
                Is.EqualTo(PcCompatManagedSettingsState.Open));
            Assert.That(controller.Snapshot().Fault, Is.Null);
            Assert.That(target.Calls,
                Is.EqualTo(new[] { "open", "draw", "close", "open", "draw" }));
        });
    }

    [Test]
    public void FallsBackToUmmEntrySurfaceWhenMainHasNoCompatSettingsMethods()
    {
        var target = new SettingsTarget();

        Assert.That(PcCompatManagedSettingsController.TryCreate(
            new object(),
            target,
            out var controller,
            out var error), Is.True, error);
        Assert.That(controller!.RequestOpen(out error), Is.True, error);
        Assert.That(controller.Dispatch(), Is.True);

        Assert.That(target.Calls, Is.EqualTo(new[] { "open", "draw" }));
    }

    [Test]
    public void RejectsTargetsWithoutAnOriginalSettingsSurface()
    {
        Assert.That(PcCompatManagedSettingsController.TryCreate(
            new object(),
            new object(),
            out var controller,
            out var error), Is.False);

        Assert.Multiple(() =>
        {
            Assert.That(controller, Is.Null);
            Assert.That(error, Does.Contain("CompatOpenGUI"));
        });
    }

    [Test]
    public void ClaimedCanvasOwnsVisibilityAndSkipsUnityImGuiDraw()
    {
        var target = new SettingsTarget();
        var canvas = new CanvasProbe { Visible = true };
        Assert.That(PcCompatManagedSettingsController.TryCreate(
            target,
            null,
            canvas,
            () => new object[] { target },
            out var controller,
            out var error), Is.True, error);

        Assert.That(controller!.RequestOpen(out error), Is.True, error);
        Assert.That(controller.Dispatch(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot().State,
                Is.EqualTo(PcCompatManagedSettingsState.Open));
            Assert.That(controller.Snapshot().SurfaceKind,
                Is.EqualTo(PcCompatManagedSettingsSurfaceKind.UnityCanvas));
            Assert.That(target.Calls, Is.EqualTo(new[] { "open" }));
            Assert.That(canvas.OwnerCount, Is.EqualTo(1));
        });

        canvas.Visible = false;
        Assert.That(controller.Dispatch(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot().State,
                Is.EqualTo(PcCompatManagedSettingsState.Closed));
            Assert.That(target.Calls, Is.EqualTo(new[] { "open", "close" }));
            Assert.That(canvas.ReleaseCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void ExistingModHudCanvasIsNotClaimedAsANewSettingsSurface()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                InvokeCanvasClaimCandidate(
                    wasVisibleBeforeOpen: true,
                    ownerSetKnown: true,
                    ownerOrDescendant: true),
                Is.False,
                "an existing owned HUD canvas is not a settings surface");
            Assert.That(
                InvokeCanvasClaimCandidate(
                    wasVisibleBeforeOpen: false,
                    ownerSetKnown: true,
                    ownerOrDescendant: true),
                Is.True,
                "a newly visible owned canvas may be the MOD settings surface");
            Assert.That(
                InvokeCanvasClaimCandidate(
                    wasVisibleBeforeOpen: false,
                    ownerSetKnown: true,
                    ownerOrDescendant: false),
                Is.False,
                "an unrelated canvas cannot steal the settings route");
        });
    }

    private static bool InvokeCanvasClaimCandidate(
        bool wasVisibleBeforeOpen,
        bool ownerSetKnown,
        bool ownerOrDescendant)
    {
        var method = typeof(PcCompatManagedSettingsUnityBackend).GetMethod(
            "IsCanvasClaimCandidate",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("IsCanvasClaimCandidate");
        return (bool)method.Invoke(
            null,
            [wasVisibleBeforeOpen, ownerSetKnown, ownerOrDescendant])!;
    }

    [Test]
    public void AccessorBindingSupportsMethodOnlyGeneratedProxyShape()
    {
        var requireGetter = typeof(PcCompatManagedSettingsUnityBackend).GetMethod(
            "RequireGetter",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("RequireGetter");
        var requireSetter = typeof(PcCompatManagedSettingsUnityBackend).GetMethod(
            "RequireSetter",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("RequireSetter");
        var width = (MethodInfo)requireGetter.Invoke(
            null,
            [typeof(MethodOnlyProxy), "width", typeof(int), true])!;
        var label = (MethodInfo)requireGetter.Invoke(
            null,
            [typeof(MethodOnlyProxy), "label", typeof(string), false])!;
        var matrix = (MethodInfo)requireSetter.Invoke(
            null,
            [typeof(MethodOnlyProxy), "matrix", typeof(object), true])!;
        var instance = new MethodOnlyProxy();
        var marker = new object();
        matrix.Invoke(null, [marker]);

        Assert.Multiple(() =>
        {
            Assert.That(width.Name, Is.EqualTo("get_width"));
            Assert.That(width.Invoke(null, null), Is.EqualTo(2400));
            Assert.That(label.Name, Is.EqualTo("get_label"));
            Assert.That(label.Invoke(instance, null), Is.EqualTo("proxy"));
            Assert.That(matrix.Name, Is.EqualTo("set_matrix"));
            Assert.That(MethodOnlyProxy.Matrix, Is.SameAs(marker));
        });
    }

    [Test]
    public void MobileSettingsMetricsUseLogicalCoordinatesAndOnePhysicalRenderScale()
    {
        var compute = typeof(PcCompatManagedSettingsUnityBackend).GetMethod(
            "ComputeMobileMetrics",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("ComputeMobileMetrics");

        var metrics = compute.Invoke(null, [1080, 2400, 395f, 1f])
            ?? throw new InvalidOperationException("mobile metrics returned null");
        var type = metrics.GetType();
        var renderScale = Convert.ToSingle(type.GetProperty("RenderScale")!.GetValue(metrics));
        var fontSize = Convert.ToInt32(type.GetProperty("FontSize")!.GetValue(metrics));
        var touchHeight = Convert.ToSingle(type.GetProperty("TouchHeight")!.GetValue(metrics));
        var customDimensionScale = Convert.ToSingle(
            type.GetProperty("CustomDimensionScale")!.GetValue(metrics));
        var customFontScale = Convert.ToSingle(
            type.GetProperty("CustomFontScale")!.GetValue(metrics));

        Assert.Multiple(() =>
        {
            Assert.That(renderScale, Is.EqualTo(395f / 160f).Within(0.001f));
            Assert.That(fontSize, Is.EqualTo(18));
            Assert.That(touchHeight, Is.EqualTo(48f).Within(0.001f));
            Assert.That(customDimensionScale, Is.EqualTo(1f));
            Assert.That(customFontScale, Is.EqualTo(1f));
        });
    }

    [Test]
    public void MobileSettingsSliderLeavesRoomForJalibOuterRowActions()
    {
        var compute = typeof(PcCompatManagedSettingsUnityBackend).GetMethod(
            "ComputeMobileSliderWidth",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("ComputeMobileSliderWidth");

        var width = Convert.ToSingle(compute.Invoke(null, [663f]));

        Assert.That(width, Is.InRange(160f, 220f));
    }

    [Test]
    public void MobileSettingsSliderKeepsStableGeometryForInvalidText()
    {
        var compute = typeof(PcCompatManagedSettingsUnityBackend).GetMethod(
            "ComputeMobileSliderValue",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("ComputeMobileSliderValue");

        Assert.Multiple(() =>
        {
            Assert.That(Convert.ToDouble(compute.Invoke(null, ["", 1d, 800d])), Is.EqualTo(1d));
            Assert.That(Convert.ToDouble(compute.Invoke(null, ["900", 1d, 800d])), Is.EqualTo(800d));
        });
    }

    [Test]
    public void MobileSettingsHeaderUsesCenteredTitleAndSquareCloseButton()
    {
        var type = typeof(PcCompatManagedSettingsUnityBackend);
        var computeInset = type.GetMethod(
            "ComputeHeaderTitleInset",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("ComputeHeaderTitleInset");
        var source = File.ReadAllText(Path.Combine(
            FindModManagerRoot(),
            "xphorror.PcModCompat",
            "src",
            "PcCompatManagedSettingsUnityBackend.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(
                Convert.ToSingle(computeInset.Invoke(null, [48f])),
                Is.EqualTo(9.6f).Within(0.001f));
            Assert.That(source, Does.Contain(
                "[_touchHeight, _touchHeight, buttonStyle, GetEmptyOptions(_getRect)]"));
            Assert.That(source, Does.Contain("InsetHeaderTitleRect(titleRect, _touchHeight)"));
            Assert.That(source, Does.Not.Contain("if (Button(\"X\"))"));
        });
    }

    [Test]
    public void MobileSettingsLayoutAppliesRealTouchHeightAndPerStyleWrapping()
    {
        var root = FindModManagerRoot();
        var backend = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "src",
            "PcCompatManagedSettingsUnityBackend.cs"));
        var bridge = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "src",
            "PcCompatManagedImGuiBridge.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(backend, Does.Contain("SetFixedHeight"));
            Assert.That(backend, Does.Contain("WordWrap: true"));
            Assert.That(backend, Does.Contain("WordWrap: false"));
            Assert.That(backend, Does.Contain("_stackControlRows"));
            Assert.That(backend, Does.Contain("DrawEnumRows"));
            Assert.That(bridge, Does.Contain("RegisterFixedHeightSetter"));
        });
    }

    [Test]
    public void MobileSettingsScaleAppliesOnlyInsideOriginalModSettingsFrame()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var bridge = typeof(PcCompatManagedImGuiBridge);
        var enter = bridge.GetMethod(
            "EnterMobileSettingsScale",
            flags,
            binder: null,
            types: [typeof(float), typeof(float), typeof(float)],
            modifiers: null)!;
        var exit = bridge.GetMethod("ExitMobileSettingsScale", flags)!;
        var getTouchHeight = bridge.GetMethod("GetMobileTouchHeight", flags)!;
        var scaleDimension = bridge.GetMethod(
            "ScaleDimension",
            flags,
            binder: null,
            types: [typeof(float)],
            modifiers: null)!;
        var scaleFont = bridge.GetMethod(
            "ScaleFont",
            flags,
            binder: null,
            types: [typeof(int)],
            modifiers: null)!;
        var previous = enter.Invoke(null, [2.46875f, 2.46875f, 48f]);

        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    Convert.ToSingle(scaleDimension.Invoke(null, [10f])),
                    Is.EqualTo(24.6875f).Within(0.001f));
                Assert.That(Convert.ToInt32(scaleFont.Invoke(null, [15])), Is.EqualTo(37));
                Assert.That(Convert.ToSingle(getTouchHeight.Invoke(null, null)), Is.EqualTo(48f));
            });
        }
        finally
        {
            exit.Invoke(null, [previous]);
        }

        Assert.Multiple(() =>
        {
            Assert.That(Convert.ToSingle(scaleDimension.Invoke(null, [10f])), Is.EqualTo(10f));
            Assert.That(Convert.ToInt32(scaleFont.Invoke(null, [15])), Is.EqualTo(15));
            Assert.That(Convert.ToSingle(getTouchHeight.Invoke(null, null)), Is.EqualTo(0f));
        });
    }

    [Test]
    public void MobileSettingsMatrixRestoreClearsStateOnSuccessAndFailure()
    {
        var type = typeof(PcCompatManagedSettingsUnityBackend);
        var restore = type.GetMethod(
            "RestoreMobileMatrix",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("RestoreMobileMatrix");
        var setMatrix = typeof(MatrixSetterProbe).GetMethod(
            nameof(MatrixSetterProbe.Set),
            BindingFlags.Public | BindingFlags.Static)!;
        var active = type.GetField(
            "_mobileGuiMatrixActive",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previous = type.GetField(
            "_previousGuiMatrix",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var setter = type.GetField(
            "_guiSetMatrix",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var backend = (PcCompatManagedSettingsUnityBackend)
            RuntimeHelpers.GetUninitializedObject(type);
        setter.SetValue(backend, setMatrix);
        active.SetValue(backend, true);
        previous.SetValue(backend, "first");
        MatrixSetterProbe.Reset(throwOnSet: false);
        object?[] successArguments = [null];

        restore.Invoke(backend, successArguments);

        Assert.Multiple(() =>
        {
            Assert.That(successArguments[0], Is.Null);
            Assert.That(MatrixSetterProbe.Value, Is.EqualTo("first"));
            Assert.That(active.GetValue(backend), Is.False);
            Assert.That(previous.GetValue(backend), Is.Null);
        });

        active.SetValue(backend, true);
        previous.SetValue(backend, "second");
        MatrixSetterProbe.Reset(throwOnSet: true);
        object?[] failureArguments = [null];

        restore.Invoke(backend, failureArguments);

        Assert.Multiple(() =>
        {
            Assert.That(failureArguments[0], Is.TypeOf<InvalidOperationException>());
            Assert.That(active.GetValue(backend), Is.False);
            Assert.That(previous.GetValue(backend), Is.Null);
        });
    }

    [Test]
    public void GeneratedProxySurfaceContainsMobileSettingsMatrixOperations()
    {
        var path = Path.Combine(
            FindModManagerRoot(),
            "xphorror.PcModCompat",
            "tools",
            "ProxyInputClosure",
            "proxy_surface_members.txt");
        var surface = File.ReadAllText(path);

        Assert.Multiple(() =>
        {
            Assert.That(surface, Does.Contain(
                "M|UnityEngine.IMGUIModule|UnityEngine.GUI|static|0|System.Void|set_matrix|UnityEngine.Matrix4x4"));
            Assert.That(surface, Does.Contain(
                "M|UnityEngine.CoreModule|UnityEngine.Matrix4x4|static|0|UnityEngine.Matrix4x4|TRS|UnityEngine.Vector3;UnityEngine.Quaternion;UnityEngine.Vector3"));
            Assert.That(surface, Does.Contain(
                "M|UnityEngine.CoreModule|UnityEngine.Matrix4x4|static|0|UnityEngine.Matrix4x4|op_Multiply|UnityEngine.Matrix4x4;UnityEngine.Matrix4x4"));
            Assert.That(surface, Does.Contain(
                "M|UnityEngine.IMGUIModule|UnityEngine.GUI|static|0|System.Single|Slider|UnityEngine.Rect;System.Single;System.Single;System.Single;System.Single;UnityEngine.GUIStyle;UnityEngine.GUIStyle;System.Boolean;System.Int32;UnityEngine.GUIStyle"));
            Assert.That(surface, Does.Contain(
                "M|UnityEngine.IMGUIModule|UnityEngine.GUISkin|instance|0|UnityEngine.GUIStyle|get_horizontalSlider|"));
            Assert.That(surface, Does.Contain(
                "M|UnityEngine.IMGUIModule|UnityEngine.GUISkin|instance|0|UnityEngine.GUIStyle|get_horizontalSliderThumb|"));
            Assert.That(surface, Does.Contain(
                "M|UnityEngine.IMGUIModule|UnityEngine.GUI|static|0|System.Boolean|Toggle|UnityEngine.Rect;System.Boolean;UnityEngine.GUIContent;UnityEngine.GUIStyle"));
            Assert.That(surface, Does.Contain(
                "M|UnityEngine.IMGUIModule|UnityEngine.GUI|static|0|System.Void|Label|UnityEngine.Rect;System.String"));
            Assert.That(surface, Does.Contain(
                "M|UnityEngine.IMGUIModule|UnityEngine.GUI|static|0|System.Boolean|Button|UnityEngine.Rect;System.String"));
            Assert.That(surface, Does.Contain(
                "M|UnityEngine.IMGUIModule|UnityEngine.GUIStyle|instance|0|System.Boolean|get_richText|"));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ContentLayoutCleanupIsLifoAndContinuesAfterFailure(bool throwOnVertical)
    {
        var type = typeof(PcCompatManagedSettingsUnityBackend);
        var backend = (PcCompatManagedSettingsUnityBackend)
            RuntimeHelpers.GetUninitializedObject(type);
        type.GetField("_endVertical", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(backend, typeof(LayoutCloseProbe).GetMethod(nameof(LayoutCloseProbe.EndVertical))!);
        type.GetField("_endHorizontal", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(backend, typeof(LayoutCloseProbe).GetMethod(nameof(LayoutCloseProbe.EndHorizontal))!);
        type.GetField("_endScrollView", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(backend, typeof(LayoutCloseProbe).GetMethod(nameof(LayoutCloseProbe.EndScrollView))!);
        foreach (var fieldName in new[]
                 {
                     "_sectionBodyVerticalOpen",
                     "_sectionBodyHorizontalOpen",
                     "_scrollOpen"
                 })
            type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(backend, true);
        LayoutCloseProbe.Reset(throwOnVertical);
        var close = type.GetMethod(
            "CloseContentLayout",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments = [null];

        close.Invoke(backend, arguments);

        Assert.Multiple(() =>
        {
            Assert.That(LayoutCloseProbe.Calls, Is.EqualTo(new[]
            {
                "section-vertical", "section-horizontal", "scroll"
            }));
            Assert.That(arguments[0] is Exception, Is.EqualTo(throwOnVertical));
            Assert.That(type.GetField(
                "_sectionBodyVerticalOpen",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(backend), Is.False);
            Assert.That(type.GetField(
                "_sectionBodyHorizontalOpen",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(backend), Is.False);
            Assert.That(type.GetField(
                "_scrollOpen",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(backend), Is.False);
        });
    }

    [Test]
    public void OptionsBindingPrefersReusableIl2CppArrayOverConvenienceOverload()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("PcCompatOptionsProxy_" + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Proxy");
        var optionType = module.DefineType(
                "UnityEngine.GUILayoutOption",
                TypeAttributes.Public | TypeAttributes.Class)
            .CreateType()!;
        var wrapperBuilder = module.DefineType(
            "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1",
            TypeAttributes.Public | TypeAttributes.Class);
        _ = wrapperBuilder.DefineGenericParameters("T");
        var constructor = wrapperBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [typeof(long)]);
        var constructorIl = constructor.GetILGenerator();
        constructorIl.Emit(OpCodes.Ldarg_0);
        constructorIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        constructorIl.Emit(OpCodes.Ret);
        var wrapperType = wrapperBuilder.CreateType()!.MakeGenericType(optionType);

        var layoutBuilder = module.DefineType(
            "UnityEngine.GUILayout",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        DefineEmptyStaticMethod(layoutBuilder, "BeginVertical", optionType.MakeArrayType());
        DefineEmptyStaticMethod(layoutBuilder, "BeginVertical", wrapperType);
        var layoutType = layoutBuilder.CreateType()!;
        var requireOptions = typeof(PcCompatManagedSettingsUnityBackend).GetMethod(
            "RequireOptionsMethod",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("RequireOptionsMethod");
        var selected = (MethodInfo)requireOptions.Invoke(
            null,
            [layoutType, "BeginVertical", typeof(void), Array.Empty<Type>()])!;
        var optionsType = selected.GetParameters()[0].ParameterType;
        var createEmpty = typeof(PcCompatManagedSettingsUnityBackend).GetMethod(
            "CreateEmptyOptions",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("CreateEmptyOptions");
        var empty = createEmpty.Invoke(null, [optionsType]);

        Assert.Multiple(() =>
        {
            Assert.That(optionsType.IsGenericType, Is.True);
            Assert.That(
                optionsType.GetGenericTypeDefinition().FullName,
                Does.Contain("Il2CppReferenceArray"));
            Assert.That(empty, Is.TypeOf(optionsType));
        });
    }

    [Test]
    public void GeneratedAndroidProxySurfaceBindsCompleteSettingsBackend()
    {
        var root = FindModManagerRoot();
        var proxyDirectory = Path.Combine(
            root,
            "xphorror.PcModCompat",
            "out",
            "interop",
            "proxy_assemblies");
        var runtimeDirectory = Path.Combine(
            root,
            "out",
            "android_single",
            "assets",
            "runtime");
        Assume.That(
            File.Exists(Path.Combine(proxyDirectory, "UnityEngine.IMGUIModule.dll")),
            Is.True,
            "generated Android proxy output is unavailable");
        Assume.That(
            File.Exists(Path.Combine(runtimeDirectory, "Il2CppInterop.Runtime.dll")),
            Is.True,
            "Android managed runtime output is unavailable");

        var loadContext = new ProxySettingsLoadContext(
            proxyDirectory,
            runtimeDirectory);
        try
        {
            Assert.That(
                PcCompatManagedSettingsUnityBackend.TryCreate(
                    loadContext,
                    out var backend,
                    out var error),
                Is.True,
                error);
            Assert.That(backend, Is.Not.Null);
        }
        finally
        {
            loadContext.Unload();
        }
    }

    [Test]
    public void LoadedManagedSessionDispatchesOriginalSettingsWithoutSelfRenderActivation()
    {
        var root = FindModManagerRoot();
        var proxyDirectory = Path.Combine(
            root,
            "xphorror.PcModCompat",
            "out",
            "interop",
            "proxy_assemblies");
        var runtimeDirectory = Path.Combine(
            root,
            "out",
            "android_single",
            "assets",
            "runtime");
        Assume.That(
            File.Exists(Path.Combine(proxyDirectory, "UnityEngine.IMGUIModule.dll")),
            Is.True,
            "generated Android proxy output is unavailable");
        Assume.That(
            File.Exists(Path.Combine(runtimeDirectory, "Il2CppInterop.Runtime.dll")),
            Is.True,
            "Android managed runtime output is unavailable");

        var folder = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "pccompat-settings-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var loadContext = new ProxySettingsLoadContext(proxyDirectory, runtimeDirectory);
        PcCompatManagedModSession? session = null;
        try
        {
            var target = new SettingsTarget();
            var manifest = new PcModManifest
            {
                FolderPath = folder,
                Id = "settings-session",
                DisplayName = "Settings Session"
            };
            var constructor = typeof(PcCompatManagedModSession).GetConstructors(
                    BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(candidate => candidate.GetParameters().Length == 12);
            session = (PcCompatManagedModSession)constructor.Invoke(
            [
                manifest,
                loadContext,
                target.GetType().Assembly,
                target,
                target,
                Array.Empty<PcCompatPatchDescriptor>(),
                false,
                false,
                false,
                false,
                0L,
                false
            ]);
            Assert.That(PcCompatManagedSettingsController.TryCreate(
                target,
                null,
                out var settingsController,
                out var controllerError), Is.True, controllerError);
            var settingsField = typeof(PcCompatManagedModSession).GetField(
                "_settingsController",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(
                    typeof(PcCompatManagedModSession).FullName,
                    "_settingsController");
            settingsField.SetValue(session, settingsController);

            Assert.That(session.Lifecycle.State,
                Is.EqualTo(PcCompatManagedLifecycleState.Loaded));
            File.WriteAllText(session.SettingsFailureReportPath, "stale settings failure");
            Assert.That(session.RequestSettingsOpen(out var error), Is.True, error);
            Assert.That(File.Exists(session.SettingsFailureReportPath), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(session.RequiresOnGUIDispatch, Is.True);
                Assert.That(session.RequiresManagedFrameDispatch, Is.False);
                Assert.That(session.ActivationPending, Is.False);
                Assert.That(session.ManagedPresentationClaimed, Is.False);
            });
            Assert.That(session.TryDispatchSettingsFrame(), Is.True);
            Assert.That(target.Calls, Is.EqualTo(new[] { "open" }));
            Assert.That(session.TryDispatchOnGUI(), Is.True);
            var firstOpen = session.Settings;
            Assert.That(
                firstOpen.State,
                Is.EqualTo(PcCompatManagedSettingsState.Open),
                firstOpen.Fault);

            session.RequestSettingsClose();
            Assert.That(session.TryDispatchSettingsFrame(), Is.True);
            Assert.That(session.Settings.State, Is.EqualTo(PcCompatManagedSettingsState.Closed));
            Assert.That(session.RequestSettingsOpen(out error), Is.True, error);
            Assert.That(session.TryDispatchSettingsFrame(), Is.True);
            Assert.That(session.TryDispatchOnGUI(), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(session.Settings.State, Is.EqualTo(PcCompatManagedSettingsState.Open));
                Assert.That(session.Lifecycle.State,
                    Is.EqualTo(PcCompatManagedLifecycleState.Loaded));
                Assert.That(session.ActivationPending, Is.False);
                Assert.That(target.Calls,
                    Is.EqualTo(new[] { "open", "draw", "close", "open", "draw" }));
            });
        }
        finally
        {
            session?.Dispose();
            if (session == null)
                loadContext.Unload();
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class SettingsTarget
    {
        public List<string> Calls { get; } = new();
        public bool ThrowOnDraw { get; set; }
        public bool CompatSettingsVisible { get; private set; }

        public void CompatOpenGUI()
        {
            Calls.Add("open");
            CompatSettingsVisible = true;
        }

        public void CompatOnGUI()
        {
            Calls.Add("draw");
            if (ThrowOnDraw)
                throw new InvalidOperationException("settings draw failed");
        }

        public void CompatSaveGUI()
            => Calls.Add("save");

        public void CompatCloseGUI()
        {
            Calls.Add("close");
            CompatSettingsVisible = false;
        }
    }

    private sealed class CanvasProbe : IPcCompatManagedSettingsCanvasProbe
    {
        public bool Visible { get; set; }
        public int OwnerCount { get; private set; }
        public int ReleaseCount { get; private set; }

        public void BeginCanvasProbe(IReadOnlyList<object> ownerGameObjects)
            => OwnerCount = ownerGameObjects.Count;

        public bool TryClaimCanvasSurface() => true;

        public bool IsClaimedCanvasSurfaceVisible() => Visible;

        public void ReleaseCanvasSurface() => ReleaseCount++;
    }

    private sealed class MethodOnlyProxy
    {
        public static object? Matrix { get; private set; }
        public static int get_width() => 2400;
        public string get_label() => "proxy";
        public static void set_matrix(object value) => Matrix = value;
    }

    private static class MatrixSetterProbe
    {
        public static object? Value { get; private set; }
        private static bool ThrowOnSet { get; set; }

        public static void Reset(bool throwOnSet)
        {
            Value = null;
            ThrowOnSet = throwOnSet;
        }

        public static void Set(object value)
        {
            if (ThrowOnSet)
                throw new InvalidOperationException("matrix setter failed");
            Value = value;
        }
    }

    private static class LayoutCloseProbe
    {
        public static List<string> Calls { get; } = [];
        private static bool ThrowOnVertical { get; set; }

        public static void Reset(bool throwOnVertical)
        {
            Calls.Clear();
            ThrowOnVertical = throwOnVertical;
        }

        public static void EndVertical()
        {
            Calls.Add("section-vertical");
            if (ThrowOnVertical)
                throw new InvalidOperationException("section vertical close failed");
        }

        public static void EndHorizontal() => Calls.Add("section-horizontal");

        public static void EndScrollView() => Calls.Add("scroll");
    }

    private static void DefineEmptyStaticMethod(
        TypeBuilder type,
        string name,
        Type optionsType)
    {
        var method = type.DefineMethod(
            name,
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            [optionsType]);
        method.GetILGenerator().Emit(OpCodes.Ret);
    }

    private static string FindModManagerRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "build.ps1")) &&
                Directory.Exists(Path.Combine(directory.FullName, "xphorror.PcModCompat")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("StArray.ModManager root was not found");
    }

    private sealed class ProxySettingsLoadContext(
        string proxyDirectory,
        string runtimeDirectory)
        : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var fileName = assemblyName.Name + ".dll";
            foreach (var directory in new[] { proxyDirectory, runtimeDirectory })
            {
                var path = Path.Combine(directory, fileName);
                if (File.Exists(path))
                    return LoadFromAssemblyPath(path);
            }
            return null;
        }
    }

    private sealed class OptionalCallbackTarget
    {
        public int Count { get; private set; }

        private void Invoke() => Count++;
    }
}

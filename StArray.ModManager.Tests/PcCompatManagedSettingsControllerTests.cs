using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatManagedSettingsControllerTests
{
    [TestCase(667f, 48f, 84f, 128f)]
    [TestCase(320f, 48f, 84f, 84f)]
    [TestCase(1600f, 64f, 112f, 128f)]
    public void FooterButtonsReserveAUsableExplicitWidth(
        float contentWidth,
        float touchHeight,
        float minimumWidth,
        float maximumWidth)
    {
        var width = PcCompatManagedSettingsUnityBackend.ComputeFooterButtonWidth(
            contentWidth,
            touchHeight);

        Assert.That(width, Is.InRange(minimumWidth, maximumWidth));
        Assert.That(width, Is.GreaterThanOrEqualTo(touchHeight * 1.75f));
    }

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
    public void RepaintLayoutMismatchRetriesWithoutClosingOriginalSettingsSurface()
    {
        var target = new SettingsTarget { LayoutMismatchFailuresRemaining = 1 };
        Assert.That(PcCompatManagedSettingsController.TryCreate(
            target,
            null,
            out var controller,
            out var error), Is.True, error);
        Assert.That(controller!.RequestOpen(out error), Is.True, error);

        Assert.That(controller.Dispatch(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot().State,
                Is.EqualTo(PcCompatManagedSettingsState.Opening));
            Assert.That(controller.Snapshot().Fault, Is.Null);
            Assert.That(target.CompatSettingsVisible, Is.True);
            Assert.That(target.Calls, Is.EqualTo(new[] { "open", "draw" }));
        });

        Assert.That(controller.Dispatch(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot().State,
                Is.EqualTo(PcCompatManagedSettingsState.Open));
            Assert.That(controller.Snapshot().Fault, Is.Null);
            Assert.That(target.Calls, Is.EqualTo(new[] { "open", "draw", "draw" }));
        });
    }

    [Test]
    public void RepeatedRepaintLayoutMismatchFaultsAfterBoundedRetries()
    {
        var target = new SettingsTarget { LayoutMismatchFailuresRemaining = 3 };
        Assert.That(PcCompatManagedSettingsController.TryCreate(
            target,
            null,
            out var controller,
            out var error), Is.True, error);
        Assert.That(controller!.RequestOpen(out error), Is.True, error);

        Assert.That(controller.Dispatch(), Is.True);
        Assert.That(controller.Dispatch(), Is.True);
        Assert.That(controller.Dispatch(), Is.False);

        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot().State,
                Is.EqualTo(PcCompatManagedSettingsState.Faulted));
            Assert.That(controller.Snapshot().Fault, Does.Contain("Getting control 2's position"));
            Assert.That(target.CompatSettingsVisible, Is.False);
            Assert.That(target.Calls,
                Is.EqualTo(new[] { "open", "draw", "draw", "draw", "close" }));
        });
    }

    [Test]
    public void LayoutGroupIgnoreMismatchRetriesWithoutClosingOriginalSettingsSurface()
    {
        var target = new SettingsTarget { LayoutGroupMismatchFailuresRemaining = 1 };
        Assert.That(PcCompatManagedSettingsController.TryCreate(
            target,
            null,
            out var controller,
            out var error), Is.True, error);
        Assert.That(controller!.RequestOpen(out error), Is.True, error);

        Assert.That(controller.Dispatch(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot().State,
                Is.EqualTo(PcCompatManagedSettingsState.Opening));
            Assert.That(controller.Snapshot().Fault, Is.Null);
            Assert.That(target.CompatSettingsVisible, Is.True);
        });

        Assert.That(controller.Dispatch(), Is.True);
        Assert.That(controller.Snapshot().State,
            Is.EqualTo(PcCompatManagedSettingsState.Open));
    }

    [Test]
    public void SettingsControllerSkipsOriginalImGuiUntilTransactionAllowsRebuild()
    {
        var target = new SettingsTarget();
        var transaction = new ImGuiTransactionProbe { AllowDispatch = false };
        Assert.That(PcCompatManagedSettingsController.TryCreate(
            target,
            null,
            transaction,
            () => Array.Empty<object>(),
            out var controller,
            out var error), Is.True, error);
        Assert.That(controller!.RequestOpen(out error), Is.True, error);

        Assert.That(controller.Dispatch(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot().State,
                Is.EqualTo(PcCompatManagedSettingsState.Opening));
            Assert.That(target.Calls, Is.EqualTo(new[] { "open" }));
        });

        transaction.AllowDispatch = true;
        Assert.That(controller.Dispatch(), Is.True);
        Assert.That(controller.Snapshot().State,
            Is.EqualTo(PcCompatManagedSettingsState.Open));

        transaction.AllowDispatch = false;
        Assert.That(controller.Dispatch(), Is.True);
        Assert.That(target.Calls, Is.EqualTo(new[] { "open", "draw" }));

        transaction.AllowDispatch = true;
        Assert.That(controller.Dispatch(), Is.True);
        Assert.That(target.Calls, Is.EqualTo(new[] { "open", "draw", "draw" }));
    }

    [Test]
    public void SettingsControllerTeardownClosesTheSurfaceAndReleasesItsTransaction()
    {
        var target = new SettingsTarget();
        var transaction = new ImGuiTransactionProbe { AllowDispatch = true };
        Assert.That(PcCompatManagedSettingsController.TryCreate(
            target,
            null,
            transaction,
            () => Array.Empty<object>(),
            out var controller,
            out var error), Is.True, error);
        Assert.That(controller!.RequestOpen(out error), Is.True, error);
        Assert.That(controller.Dispatch(), Is.True);

        controller.ReleaseForSessionTeardown();

        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot().State,
                Is.EqualTo(PcCompatManagedSettingsState.Closed));
            Assert.That(target.Calls, Is.EqualTo(new[] { "open", "draw", "close" }));
            Assert.That(transaction.ReleaseCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void SettingsControllerStopsTheActiveDispatchAfterReentrantSessionTeardown()
    {
        var target = new SettingsTarget();
        var transaction = new ImGuiTransactionProbe { AllowDispatch = true };
        PcCompatManagedSettingsController? controller = null;
        target.OnDraw = () => controller!.ReleaseForSessionTeardown();
        Assert.That(PcCompatManagedSettingsController.TryCreate(
            target,
            null,
            transaction,
            () => Array.Empty<object>(),
            out controller,
            out var error), Is.True, error);
        Assert.That(controller!.RequestOpen(out error), Is.True, error);

        Assert.That(controller.Dispatch(), Is.True);
        Assert.That(controller.Dispatch(), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot().State,
                Is.EqualTo(PcCompatManagedSettingsState.Closed));
            Assert.That(target.Calls, Is.EqualTo(new[] { "open", "draw", "close" }));
            Assert.That(transaction.ReleaseCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void SettingsControllerTeardownWinsOverAnOnGuiExceptionFromTheRetiredSurface()
    {
        var target = new SettingsTarget { ThrowOnDraw = true };
        var transaction = new ImGuiTransactionProbe { AllowDispatch = true };
        PcCompatManagedSettingsController? controller = null;
        target.OnDraw = () => controller!.ReleaseForSessionTeardown();
        Assert.That(PcCompatManagedSettingsController.TryCreate(
            target,
            null,
            transaction,
            () => Array.Empty<object>(),
            out controller,
            out var error), Is.True, error);
        Assert.That(controller!.RequestOpen(out error), Is.True, error);

        Assert.That(controller.Dispatch(), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot().State,
                Is.EqualTo(PcCompatManagedSettingsState.Closed));
            Assert.That(controller.Snapshot().Fault, Is.Null);
            Assert.That(target.Calls, Is.EqualTo(new[] { "open", "draw", "close" }));
            Assert.That(transaction.ReleaseCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void SettingsControllerTeardownDoesNotReenterAnActiveCloseCallback()
    {
        var target = new SettingsTarget();
        var transaction = new ImGuiTransactionProbe { AllowDispatch = true };
        PcCompatManagedSettingsController? controller = null;
        target.OnClose = () => controller!.ReleaseForSessionTeardown();
        Assert.That(PcCompatManagedSettingsController.TryCreate(
            target,
            null,
            transaction,
            () => Array.Empty<object>(),
            out controller,
            out var error), Is.True, error);
        Assert.That(controller!.RequestOpen(out error), Is.True, error);
        Assert.That(controller.Dispatch(), Is.True);

        controller.RequestClose();
        Assert.That(controller.Dispatch(), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot().State,
                Is.EqualTo(PcCompatManagedSettingsState.Closed));
            Assert.That(target.Calls, Is.EqualTo(new[] { "open", "draw", "close" }));
            Assert.That(transaction.ReleaseCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void SettingsControllerTeardownClearsAnAlreadyFaultedSurfaceWithoutClosingTwice()
    {
        var target = new SettingsTarget { ThrowOnDraw = true };
        var transaction = new ImGuiTransactionProbe { AllowDispatch = true };
        Assert.That(PcCompatManagedSettingsController.TryCreate(
            target,
            null,
            transaction,
            () => Array.Empty<object>(),
            out var controller,
            out var error), Is.True, error);
        Assert.That(controller!.RequestOpen(out error), Is.True, error);
        Assert.That(controller.Dispatch(), Is.False);
        Assert.That(controller.Snapshot().State,
            Is.EqualTo(PcCompatManagedSettingsState.Faulted));

        controller.ReleaseForSessionTeardown();

        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot().State,
                Is.EqualTo(PcCompatManagedSettingsState.Closed));
            Assert.That(target.Calls, Is.EqualTo(new[] { "open", "draw", "close" }));
            Assert.That(transaction.ReleaseCount, Is.EqualTo(2),
                "fault cleanup releases the surface once, and session teardown releases the terminal transaction once");
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
    public void ImGuiInteractionFenceCommitsAtLayoutThenWaitsForRebuildBeforeNonLayout()
    {
        var fence = new PcCompatManagedImGuiInteractionFence();
        const int buttonToken = 101;
        const int toggleToken = 102;
        const int textToken = 103;

        fence.BeginFrame(PcCompatManagedImGuiEventKind.Input);
        Assert.Multiple(() =>
        {
            Assert.That(fence.ResolveRawToggle(false, true, toggleToken), Is.False);
            Assert.That(fence.ResolveRawButton(true, buttonToken), Is.False);
            Assert.That(fence.ResolveRawText("old", "new", textToken), Is.EqualTo("old"));
        });
        fence.EndFrame();
        Assert.Multiple(() =>
        {
            Assert.That(fence.State, Is.EqualTo(PcCompatManagedImGuiTransactionState.InputPending));
            Assert.That(fence.ShouldDispatch(PcCompatManagedImGuiEventKind.Repaint), Is.True);
        });

        fence.BeginFrame(PcCompatManagedImGuiEventKind.Layout);
        Assert.Multiple(() =>
        {
            Assert.That(fence.ResolveRawToggle(false, false, toggleToken), Is.True);
            Assert.That(fence.ResolveRawButton(false, buttonToken), Is.True);
            Assert.That(fence.ResolveRawText("old", "old", textToken), Is.EqualTo("new"));
        });
        fence.EndFrame();
        Assert.Multiple(() =>
        {
            Assert.That(fence.State,
                Is.EqualTo(PcCompatManagedImGuiTransactionState.AwaitingRebuildLayout));
            Assert.That(fence.ShouldDispatch(PcCompatManagedImGuiEventKind.Input), Is.False);
            Assert.That(fence.ShouldDispatch(PcCompatManagedImGuiEventKind.Repaint), Is.False);
            Assert.That(fence.ShouldDispatch(PcCompatManagedImGuiEventKind.Layout), Is.True);
        });

        fence.BeginFrame(PcCompatManagedImGuiEventKind.Layout);
        Assert.That(fence.ResolveRawButton(false, buttonToken), Is.False);
        fence.EndFrame();
        Assert.That(fence.State,
            Is.EqualTo(PcCompatManagedImGuiTransactionState.StableVerification));

        fence.BeginFrame(PcCompatManagedImGuiEventKind.Repaint);
        Assert.That(fence.ResolveRawButton(false, buttonToken), Is.False);
        fence.EndFrame();
        Assert.That(fence.State, Is.EqualTo(PcCompatManagedImGuiTransactionState.Stable));
    }

    [Test]
    public void ImGuiInteractionFenceKeepsJipperCreditsTopologyStableAcrossCommitAndRebuild()
    {
        var fence = new PcCompatManagedImGuiInteractionFence();
        var fixture = new JipperCreditsFixture();

        RunFrame(fence, PcCompatManagedImGuiEventKind.Layout, () => fixture.Draw(fence, false));
        RunFrame(fence, PcCompatManagedImGuiEventKind.Input, () => fixture.Draw(fence, true));
        RunFrame(fence, PcCompatManagedImGuiEventKind.Repaint, () => fixture.Draw(fence, false));

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Trees, Is.EqualTo(new[] { "button", "button", "button" }));
            Assert.That(fixture.CreditsShown, Is.False);
        });

        RunFrame(fence, PcCompatManagedImGuiEventKind.Layout, () => fixture.Draw(fence, false));
        Assert.Multiple(() =>
        {
            Assert.That(fixture.Trees[^1], Is.EqualTo("button"));
            Assert.That(fixture.CreditsShown, Is.True);
            Assert.That(fence.ShouldDispatch(PcCompatManagedImGuiEventKind.Repaint), Is.False);
        });

        Assert.That(RunFrameIfAllowed(
            fence,
            PcCompatManagedImGuiEventKind.Repaint,
            () => fixture.Draw(fence, false)), Is.False);

        RunFrame(fence, PcCompatManagedImGuiEventKind.Layout, () => fixture.Draw(fence, false));
        RunFrame(fence, PcCompatManagedImGuiEventKind.Repaint, () => fixture.Draw(fence, false));

        Assert.That(fixture.Trees[^2..], Is.EqualTo(new[] { "credits/horizontal", "credits/horizontal" }));
    }

    [Test]
    public void ImGuiInteractionFenceKeepsJipperOverlayerExpansionTopologyStableAcrossCommitAndRebuild()
    {
        var fence = new PcCompatManagedImGuiInteractionFence();
        var fixture = new JipperOverlayerAlignmentFixture();

        RunFrame(fence, PcCompatManagedImGuiEventKind.Layout, () => fixture.Draw(fence, false));
        RunFrame(fence, PcCompatManagedImGuiEventKind.Input, () => fixture.Draw(fence, true));
        RunFrame(fence, PcCompatManagedImGuiEventKind.Repaint, () => fixture.Draw(fence, false));
        RunFrame(fence, PcCompatManagedImGuiEventKind.Layout, () => fixture.Draw(fence, false));

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Trees, Is.EqualTo(new[]
            {
                "alignment/horizontal",
                "alignment/horizontal",
                "alignment/horizontal",
                "alignment/horizontal"
            }));
            Assert.That(fixture.Expanded, Is.True);
            Assert.That(fence.ShouldDispatch(PcCompatManagedImGuiEventKind.Repaint), Is.False);
        });

        RunFrame(fence, PcCompatManagedImGuiEventKind.Layout, () => fixture.Draw(fence, false));
        RunFrame(fence, PcCompatManagedImGuiEventKind.Repaint, () => fixture.Draw(fence, false));

        Assert.That(fixture.Trees[^2..], Is.EqualTo(new[]
        {
            "alignment/horizontal/selection-horizontal",
            "alignment/horizontal/selection-horizontal"
        }));
    }

    [Test]
    public void ImGuiInteractionFenceRoutesReorderedControlsByCallsiteInsteadOfCursor()
    {
        var fence = new PcCompatManagedImGuiInteractionFence();
        const int firstToken = 201;
        const int secondToken = 202;

        fence.BeginFrame(PcCompatManagedImGuiEventKind.Input);
        Assert.That(fence.ResolveRawText("first", "first-next", firstToken), Is.EqualTo("first"));
        Assert.That(fence.ResolveRawText("second", "second-next", secondToken), Is.EqualTo("second"));
        fence.EndFrame();

        fence.BeginFrame(PcCompatManagedImGuiEventKind.Layout);
        Assert.That(fence.ResolveRawText("second", "second", secondToken), Is.EqualTo("second-next"));
        Assert.That(fence.ResolveRawText("first", "first", firstToken), Is.EqualTo("first-next"));
        fence.EndFrame();
    }

    [Test]
    public void ImGuiInteractionFenceKeepsTheLatestContinuousValueBeforeCommit()
    {
        var fence = new PcCompatManagedImGuiInteractionFence();
        const int textToken = 205;
        const int sliderToken = 206;

        fence.BeginFrame(PcCompatManagedImGuiEventKind.Input);
        Assert.That(fence.ResolveRawText("old", "middle", textToken), Is.EqualTo("old"));
        Assert.That(fence.ResolveRawValue(1f, 2f, sliderToken), Is.EqualTo(1f));
        fence.EndFrame();

        fence.BeginFrame(PcCompatManagedImGuiEventKind.Input);
        Assert.That(fence.ResolveRawText("old", "latest", textToken), Is.EqualTo("old"));
        Assert.That(fence.ResolveRawValue(1f, 3.5f, sliderToken), Is.EqualTo(1f));
        fence.EndFrame();

        fence.BeginFrame(PcCompatManagedImGuiEventKind.Layout);
        Assert.Multiple(() =>
        {
            Assert.That(fence.ResolveRawText("old", "old", textToken), Is.EqualTo("latest"));
            Assert.That(fence.ResolveRawValue(1f, 1f, sliderToken), Is.EqualTo(3.5f));
        });
        fence.EndFrame();
        Assert.That(fence.State,
            Is.EqualTo(PcCompatManagedImGuiTransactionState.AwaitingRebuildLayout));
    }

    [Test]
    public void ImGuiInteractionFenceDropsPendingControlsThatDisappearAfterACommit()
    {
        var fence = new PcCompatManagedImGuiInteractionFence();
        const int collapseToken = 207;
        const int hiddenTextToken = 208;

        fence.BeginFrame(PcCompatManagedImGuiEventKind.Input);
        Assert.That(fence.ResolveRawButton(true, collapseToken), Is.False);
        Assert.That(fence.ResolveRawText("old", "stale", hiddenTextToken), Is.EqualTo("old"));
        fence.EndFrame();
        Assert.That(fence.PendingCount, Is.EqualTo(2));

        fence.BeginFrame(PcCompatManagedImGuiEventKind.Layout);
        Assert.That(fence.ResolveRawButton(false, collapseToken), Is.True);
        // The conditional text field has disappeared from the new branch and is
        // intentionally not resolved by this Layout.
        fence.EndFrame();

        Assert.Multiple(() =>
        {
            Assert.That(fence.PendingCount, Is.Zero);
            Assert.That(fence.State,
                Is.EqualTo(PcCompatManagedImGuiTransactionState.AwaitingRebuildLayout));
        });

        fence.BeginFrame(PcCompatManagedImGuiEventKind.Layout);
        Assert.That(fence.ResolveRawText("replacement", "replacement", hiddenTextToken),
            Is.EqualTo("replacement"));
        fence.EndFrame();
    }

    [Test]
    public void ImGuiInteractionFenceSeparatesRepeatedCallsiteOccurrences()
    {
        var fence = new PcCompatManagedImGuiInteractionFence();
        const int loopToken = 209;

        fence.BeginFrame(PcCompatManagedImGuiEventKind.Input);
        Assert.That(fence.ResolveRawText("first", "first-next", loopToken), Is.EqualTo("first"));
        Assert.That(fence.ResolveRawText("second", "second-next", loopToken), Is.EqualTo("second"));
        fence.EndFrame();

        fence.BeginFrame(PcCompatManagedImGuiEventKind.Layout);
        Assert.Multiple(() =>
        {
            Assert.That(fence.ResolveRawText("first", "first", loopToken), Is.EqualTo("first-next"));
            Assert.That(fence.ResolveRawText("second", "second", loopToken), Is.EqualTo("second-next"));
        });
        fence.EndFrame();
    }

    [Test]
    public void ImGuiInteractionFenceRecoversKnownLayoutMismatchOnlyThroughLayout()
    {
        var fence = new PcCompatManagedImGuiInteractionFence();

        fence.MarkRecoverableLayoutFailure();

        Assert.Multiple(() =>
        {
            Assert.That(fence.State, Is.EqualTo(PcCompatManagedImGuiTransactionState.Recovering));
            Assert.That(fence.ShouldDispatch(PcCompatManagedImGuiEventKind.Input), Is.False);
            Assert.That(fence.ShouldDispatch(PcCompatManagedImGuiEventKind.Repaint), Is.False);
            Assert.That(fence.ShouldDispatch(PcCompatManagedImGuiEventKind.Layout), Is.True);
        });

        fence.BeginFrame(PcCompatManagedImGuiEventKind.Layout);
        fence.EndFrame();
        Assert.That(fence.State,
            Is.EqualTo(PcCompatManagedImGuiTransactionState.StableVerification));
    }

    [Test]
    public void ImGuiInteractionFenceBoundsPendingControlsAndDropsTheOldestValue()
    {
        var fence = new PcCompatManagedImGuiInteractionFence();

        fence.BeginFrame(PcCompatManagedImGuiEventKind.Input);
        for (var index = 0; index <= 256; index++)
        {
            Assert.That(
                fence.ResolveRawText($"old-{index}", $"new-{index}", 4000 + index),
                Is.EqualTo($"old-{index}"));
        }
        fence.EndFrame();

        Assert.That(fence.PendingCount, Is.EqualTo(256));

        fence.BeginFrame(PcCompatManagedImGuiEventKind.Layout);
        Assert.Multiple(() =>
        {
            Assert.That(fence.ResolveRawText("old-0", "old-0", 4000), Is.EqualTo("old-0"));
            Assert.That(fence.ResolveRawText("old-1", "old-1", 4001), Is.EqualTo("new-1"));
        });
        fence.EndFrame();
    }

    [Test]
    public void ImGuiInteractionFenceResetDoesNotLeakPendingInputAcrossGenerations()
    {
        var fence = new PcCompatManagedImGuiInteractionFence();

        fence.BeginFrame(PcCompatManagedImGuiEventKind.Input);
        Assert.That(fence.ResolveRawText("old", "new", 4101), Is.EqualTo("old"));
        fence.EndFrame();
        fence.Reset();

        Assert.Multiple(() =>
        {
            Assert.That(fence.PendingCount, Is.Zero);
            Assert.That(fence.State, Is.EqualTo(PcCompatManagedImGuiTransactionState.Stable));
        });

        fence.BeginFrame(PcCompatManagedImGuiEventKind.Layout);
        Assert.That(fence.ResolveRawText("replacement", "replacement", 4101), Is.EqualTo("replacement"));
        fence.EndFrame();
    }

    [Test]
    public void ImGuiInteractionFenceResetClearsEveryInactiveTeardownState()
    {
        var inputPending = new PcCompatManagedImGuiInteractionFence();
        inputPending.BeginFrame(PcCompatManagedImGuiEventKind.Input);
        _ = inputPending.ResolveRawText("old", "new", 4301);
        inputPending.EndFrame();

        var awaitingRebuild = new PcCompatManagedImGuiInteractionFence();
        awaitingRebuild.BeginFrame(PcCompatManagedImGuiEventKind.Input);
        _ = awaitingRebuild.ResolveRawButton(true, 4302);
        awaitingRebuild.EndFrame();
        awaitingRebuild.BeginFrame(PcCompatManagedImGuiEventKind.Layout);
        _ = awaitingRebuild.ResolveRawButton(false, 4302);
        awaitingRebuild.EndFrame();

        var recovering = new PcCompatManagedImGuiInteractionFence();
        recovering.MarkRecoverableLayoutFailure();

        Assert.Multiple(() =>
        {
            Assert.That(inputPending.State,
                Is.EqualTo(PcCompatManagedImGuiTransactionState.InputPending));
            Assert.That(awaitingRebuild.State,
                Is.EqualTo(PcCompatManagedImGuiTransactionState.AwaitingRebuildLayout));
            Assert.That(recovering.State,
                Is.EqualTo(PcCompatManagedImGuiTransactionState.Recovering));
        });

        inputPending.Reset();
        awaitingRebuild.Reset();
        recovering.Reset();

        Assert.Multiple(() =>
        {
            Assert.That(inputPending.PendingCount, Is.Zero);
            Assert.That(inputPending.LayoutEpoch, Is.Zero);
            Assert.That(inputPending.State, Is.EqualTo(PcCompatManagedImGuiTransactionState.Stable));
            Assert.That(awaitingRebuild.PendingCount, Is.Zero);
            Assert.That(awaitingRebuild.LayoutEpoch, Is.Zero);
            Assert.That(awaitingRebuild.State, Is.EqualTo(PcCompatManagedImGuiTransactionState.Stable));
            Assert.That(recovering.PendingCount, Is.Zero);
            Assert.That(recovering.LayoutEpoch, Is.Zero);
            Assert.That(recovering.State, Is.EqualTo(PcCompatManagedImGuiTransactionState.Stable));
        });
    }

    [Test]
    public void SettingsBackendDefersInteractionFenceResetUntilAnActiveFrameCloses()
    {
        var type = typeof(PcCompatManagedSettingsUnityBackend);
        var backend = (PcCompatManagedSettingsUnityBackend)
            RuntimeHelpers.GetUninitializedObject(type);
        var fence = new PcCompatManagedImGuiInteractionFence();
        fence.BeginFrame(PcCompatManagedImGuiEventKind.Input);
        _ = fence.ResolveRawText("old", "new", 4303);
        fence.EndFrame();
        PcCompatManagedImGuiBridge.BeginSettingsInteractionFrame(
            fence,
            PcCompatManagedImGuiEventKind.Layout);

        try
        {
            SetPrivateField(backend, "_canvasBaseline", new HashSet<int>());
            SetPrivateField(backend, "_canvasOwnerIds", new HashSet<int>());
            SetPrivateField(backend, "_claimedCanvasIds", new HashSet<int>());
            SetPrivateField(backend, "_interactionFence", fence);
            var snapshotsField = type.GetField(
                "_mobileStyleSnapshots",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(type.FullName, "_mobileStyleSnapshots");
            SetPrivateField(backend, "_mobileStyleSnapshots",
                Activator.CreateInstance(snapshotsField.FieldType)!);
            SetPrivateField(backend, "_frameOpen", true);
            SetPrivateField(backend, "_interactionFenceFrameOpen", true);

            backend.ReleaseCanvasSurface();

            Assert.Multiple(() =>
            {
                Assert.That(fence.State,
                    Is.EqualTo(PcCompatManagedImGuiTransactionState.CommitLayout));
                Assert.That(GetPrivateField<bool>(backend, "_resetInteractionFenceAfterFrame"), Is.True);
            });

            var closeFrame = type.GetMethod(
                "CloseFrame",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("CloseFrame");
            closeFrame.Invoke(backend, [false]);
        }
        finally
        {
            // The production CloseFrame path normally clears this thread-static
            // scope. Keep the test isolated even when an assertion fails first.
            PcCompatManagedImGuiBridge.EndSettingsInteractionFrame(fence, completed: false);
        }

        Assert.Multiple(() =>
        {
            Assert.That(fence.PendingCount, Is.Zero);
            Assert.That(fence.LayoutEpoch, Is.Zero);
            Assert.That(fence.State, Is.EqualTo(PcCompatManagedImGuiTransactionState.Stable));
            Assert.That(GetPrivateField<bool>(backend, "_resetInteractionFenceAfterFrame"), Is.False);
        });
    }

    [Test]
    public void ImGuiInteractionFenceHasNoSteadyStateAllocations()
    {
        var fence = new PcCompatManagedImGuiInteractionFence();
        const int token = 4201;

        fence.BeginFrame(PcCompatManagedImGuiEventKind.Layout);
        _ = fence.ResolveRawToggle(false, false, token);
        fence.EndFrame();
        fence.BeginFrame(PcCompatManagedImGuiEventKind.Repaint);
        _ = fence.ResolveRawToggle(false, false, token);
        fence.EndFrame();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 128; index++)
        {
            fence.BeginFrame(PcCompatManagedImGuiEventKind.Layout);
            _ = fence.ResolveRawToggle(false, false, token);
            fence.EndFrame();
            fence.BeginFrame(PcCompatManagedImGuiEventKind.Repaint);
            _ = fence.ResolveRawToggle(false, false, token);
            fence.EndFrame();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.Zero);
    }

    [Test]
    public void UnclassifiedExitGuiFailureStillFaultsTheSettingsSurface()
    {
        var target = new SettingsTarget { ThrowExitGuiOnDraw = true };
        Assert.That(PcCompatManagedSettingsController.TryCreate(
            target,
            null,
            out var controller,
            out var error), Is.True, error);
        Assert.That(controller!.RequestOpen(out error), Is.True, error);

        Assert.That(controller.Dispatch(), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot().State,
                Is.EqualTo(PcCompatManagedSettingsState.Faulted));
            Assert.That(controller.Snapshot().Fault, Does.Contain("ordinary GUI exit"));
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
            "PcCompatManagedSettingsUnityBackend.cs"), Encoding.UTF8);

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
    public void MobileSettingsLayoutKeepsThirdPartyControlsSingleLineUntilTheResponsiveBridgeProvesWrapping()
    {
        var root = FindModManagerRoot();
        var backend = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "src",
            "PcCompatManagedSettingsUnityBackend.cs"), Encoding.UTF8);
        var bridge = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "src",
            "PcCompatManagedImGuiBridge.cs"), Encoding.UTF8);

        Assert.Multiple(() =>
        {
            Assert.That(backend, Does.Contain("SetFixedHeight"));
            Assert.That(backend, Does.Contain("WordWrap: true"));
            Assert.That(backend, Does.Contain("WordWrap: false"));
            Assert.That(
                backend,
                Does.Contain("new StylePolicy(_skinButton.Invoke(skin, null), WordWrap: false, FixedHeight: interactiveVisualHeight)"));
            Assert.That(
                backend,
                Does.Contain("new StylePolicy(_skinToggle.Invoke(skin, null), WordWrap: false, FixedHeight: interactiveVisualHeight)"));
            Assert.That(
                backend,
                Does.Contain("new StylePolicy(_skinLabel.Invoke(skin, null), WordWrap: false, FixedHeight: 0f)"));
            Assert.That(backend, Does.Contain("_stackControlRows"));
            Assert.That(backend, Does.Contain("FooterButton"));
            Assert.That(backend, Does.Contain("ComputeFooterButtonWidth"));
            Assert.That(backend, Does.Contain("DrawEnumRows"));
            Assert.That(bridge, Does.Contain("RegisterFixedHeightSetter"));
            Assert.That(bridge, Does.Contain("t_mobileContentWidth"));
            Assert.That(bridge, Does.Contain("PcCompatManagedResponsiveImGuiLayout.BeginFrame"));
            Assert.That(bridge, Does.Contain("ApplyResponsiveTextLayout"));
            Assert.That(bridge, Does.Contain("bool? wrapText"));
            Assert.That(bridge, Does.Contain("wrapText ?? previousWordWrap"));
            Assert.That(bridge, Does.Contain("NormalizeTextOptions"));
            Assert.That(bridge, Does.Contain("Math.Max(previousHeight, baselineHeight)"));
            Assert.That(bridge, Does.Contain("if (previousWordWrap)"));
            Assert.That(backend, Does.Contain("_toggleStyled.Invoke"));
            Assert.That(backend, Does.Contain("_buttonContentStyled.Invoke"));
            Assert.That(backend, Does.Contain("_guiLabelRectContentStyled.Invoke"));
            Assert.That(backend, Does.Contain("v19-style-fingerprint"));
            Assert.That(bridge, Does.Contain("TextMeasurementCacheCapacity"));
            Assert.That(bridge, Does.Contain("RequiresMeasurement"));
            Assert.That(bridge, Does.Contain("measurementStyleFingerprint: GetMobileMeasurementFingerprint()"));
            Assert.That(bridge, Does.Not.Contain("ApplyMinimumTouchHeight"));
            Assert.That(bridge, Does.Not.Contain("EstimateMobileTextWidth"));
        });
    }

    [Test]
    public void MobileSettingsScaleRetainsCurrentLogicalContentWidthForResponsiveLayout()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var bridge = typeof(PcCompatManagedImGuiBridge);
        var enter = bridge.GetMethod(
            "EnterMobileSettingsScale",
            flags,
            binder: null,
            types: [typeof(float), typeof(float), typeof(float), typeof(float)],
            modifiers: null)!;
        var exit = bridge.GetMethod("ExitMobileSettingsScale", flags)!;
        var getContentWidth = bridge.GetMethod("GetMobileContentWidth", flags)!;
        var previous = enter.Invoke(null, [1f, 1f, 48f, 352f]);

        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(Convert.ToSingle(getContentWidth.Invoke(null, null)), Is.EqualTo(352f));
            });
        }
        finally
        {
            exit.Invoke(null, [previous]);
        }

        Assert.That(Convert.ToSingle(getContentWidth.Invoke(null, null)), Is.Zero);
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
        var surface = File.ReadAllText(path, Encoding.UTF8);

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
                .Single(candidate => candidate.GetParameters().Length == 13);
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
                1L,
                false,
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

    private static void RunFrame(
        PcCompatManagedImGuiInteractionFence fence,
        PcCompatManagedImGuiEventKind eventKind,
        Action draw)
    {
        Assert.That(fence.ShouldDispatch(eventKind), Is.True);
        fence.BeginFrame(eventKind);
        try
        {
            draw();
        }
        finally
        {
            fence.EndFrame();
        }
    }

    private static bool RunFrameIfAllowed(
        PcCompatManagedImGuiInteractionFence fence,
        PcCompatManagedImGuiEventKind eventKind,
        Action draw)
    {
        if (!fence.ShouldDispatch(eventKind))
            return false;
        RunFrame(fence, eventKind, draw);
        return true;
    }

    private sealed class JipperCreditsFixture
    {
        private const int CreditsButtonToken = 3101;

        public List<string> Trees { get; } = [];
        public bool CreditsShown { get; private set; }

        public void Draw(PcCompatManagedImGuiInteractionFence fence, bool observedButton)
        {
            // Mirrors JRP: the branch is evaluated before the button and the
            // old branch always returns after the click changes state.
            if (!CreditsShown)
            {
                Trees.Add("button");
                if (fence.ResolveRawButton(observedButton, CreditsButtonToken))
                    CreditsShown = true;
                return;
            }

            Trees.Add("credits/horizontal");
        }
    }

    private sealed class JipperOverlayerAlignmentFixture
    {
        private const int ExpandButtonToken = 3201;
        private const int GridToken = 3202;

        public List<string> Trees { get; } = [];
        public bool Expanded { get; private set; }

        public void Draw(PcCompatManagedImGuiInteractionFence fence, bool observedButton)
        {
            // Mirrors JPOV: expanded is captured before the button and the
            // conditional selection group uses that old local value.
            var expanded = Expanded;
            var tree = "alignment/horizontal";
            if (fence.ResolveRawButton(observedButton, ExpandButtonToken))
                Expanded = !Expanded;
            if (expanded)
            {
                tree += "/selection-horizontal";
                _ = fence.ResolveRawValue(0, 0, GridToken);
            }
            Trees.Add(tree);
        }
    }

    private sealed class SettingsTarget
    {
        public List<string> Calls { get; } = new();
        public bool ThrowOnDraw { get; set; }
        public bool ThrowExitGuiOnDraw { get; set; }
        public int LayoutMismatchFailuresRemaining { get; set; }
        public int LayoutGroupMismatchFailuresRemaining { get; set; }
        public Action? OnDraw { get; set; }
        public Action? OnClose { get; set; }
        public bool CompatSettingsVisible { get; private set; }

        public void CompatOpenGUI()
        {
            Calls.Add("open");
            CompatSettingsVisible = true;
        }

        public void CompatOnGUI()
        {
            Calls.Add("draw");
            OnDraw?.Invoke();
            if (LayoutMismatchFailuresRemaining > 0)
            {
                LayoutMismatchFailuresRemaining--;
                throw new ArgumentException(
                    "Getting control 2's position in a group with only 2 controls " +
                    "when doing repaint\nAborting");
            }
            if (LayoutGroupMismatchFailuresRemaining > 0)
            {
                LayoutGroupMismatchFailuresRemaining--;
                throw new InvalidOperationException(
                    "UnityEngine.ExitGUIException: GUILayout: Mismatched LayoutGroup.Ignore");
            }
            if (ThrowExitGuiOnDraw)
                throw new InvalidOperationException("UnityEngine.ExitGUIException: ordinary GUI exit");
            if (ThrowOnDraw)
                throw new InvalidOperationException("settings draw failed");
        }

        public void CompatSaveGUI()
            => Calls.Add("save");

        public void CompatCloseGUI()
        {
            Calls.Add("close");
            OnClose?.Invoke();
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

    private sealed class ImGuiTransactionProbe :
        IPcCompatManagedSettingsCanvasProbe,
        IPcCompatManagedSettingsImGuiTransaction
    {
        public bool AllowDispatch { get; set; }
        public bool IsStable => AllowDispatch;
        public int RecoverableFailureCount { get; private set; }
        public int ReleaseCount { get; private set; }

        public void BeginCanvasProbe(IReadOnlyList<object> ownerGameObjects)
        {
        }

        public bool TryClaimCanvasSurface() => false;

        public bool IsClaimedCanvasSurfaceVisible() => false;

        public void ReleaseCanvasSurface()
            => ReleaseCount++;

        public bool ShouldDispatchCurrentEvent() => AllowDispatch;

        public void MarkRecoverableLayoutFailure() => RecoverableFailureCount++;
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
            if (File.Exists(Path.Combine(directory.FullName, "build_android_single.ps1")) &&
                Directory.Exists(Path.Combine(directory.FullName, "xphorror.PcModCompat")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("StArray.ModManager root was not found");
    }

    private static void SetPrivateField(object target, string name, object value)
    {
        var field = target.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, name);
        field.SetValue(target, value);
    }

    private static T GetPrivateField<T>(object target, string name)
    {
        var field = target.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, name);
        return (T)field.GetValue(target)!;
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

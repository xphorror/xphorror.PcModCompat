using System.Diagnostics;
using System.Reflection;
using StArray.ModManager.Android.PcCompat;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public sealed class PcCompatResponsiveImGuiLayoutTests
{
    private const int DynamicRowBeginToken = 9301;
    private const int DynamicRowEndToken = 9302;
    private const int DynamicRowFirstLabelToken = 9311;
    private const int DynamicRowFirstInputToken = 9312;
    private const int DynamicRowSecondLabelToken = 9321;
    private const int DynamicRowSecondInputToken = 9322;

    [Test]
    public void FirstTransactionPreservesNativeTopologyAndTheNextLayoutPromotesItsSegmentPlan()
    {
        const string modId = "ResponsiveLayoutContract";
        const long generation = 71;
        using var execution = PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
            modId,
            generation,
            PcCompatManagedExecutionPhase.Update));

        try
        {
            PcCompatManagedResponsiveImGuiLayout.BeginFrame(
                layoutEvent: true,
                contentWidth: 180f,
                fontScale: 1f,
                touchHeight: 48f);
            try
            {
                Assert.That(
                    PcCompatManagedResponsiveImGuiLayout.BeginHorizontal(1001).Mode,
                    Is.EqualTo(PcCompatImGuiContainerMode.PassThrough));
                CaptureTwoLabelInputGroups();
                PcCompatManagedResponsiveImGuiLayout.EndHorizontal(2001, measuredWidth: 180f);
            }
            finally
            {
                PcCompatManagedResponsiveImGuiLayout.EndFrame();
            }

            // The first Layout and its following Repaint must retain identical
            // structure. The candidate plan is only promoted by the next Layout.
            PcCompatManagedResponsiveImGuiLayout.BeginFrame(
                layoutEvent: false,
                contentWidth: 180f,
                fontScale: 1f,
                touchHeight: 48f);
            try
            {
                Assert.That(
                    PcCompatManagedResponsiveImGuiLayout.BeginHorizontal(1001).Mode,
                    Is.EqualTo(PcCompatImGuiContainerMode.PassThrough));
                PcCompatManagedResponsiveImGuiLayout.EndHorizontal(2001, measuredWidth: 180f);
            }
            finally
            {
                PcCompatManagedResponsiveImGuiLayout.EndFrame();
            }

            PcCompatManagedResponsiveImGuiLayout.BeginFrame(
                layoutEvent: true,
                contentWidth: 180f,
                fontScale: 1f,
                touchHeight: 48f);
            try
            {
                Assert.That(
                    PcCompatManagedResponsiveImGuiLayout.BeginHorizontal(1001).Mode,
                    Is.EqualTo(PcCompatImGuiContainerMode.Rows));
                Assert.That(
                    PcCompatManagedResponsiveImGuiLayout.BeforeElement(
                        1101,
                        PcCompatImGuiElementKind.Label,
                        Label(),
                        default).StartNewRow,
                    Is.False);
                Assert.That(
                    PcCompatManagedResponsiveImGuiLayout.BeforeElement(
                        1102,
                        PcCompatImGuiElementKind.Input,
                        Input(),
                        default).StartNewRow,
                    Is.False);
                Assert.That(
                    PcCompatManagedResponsiveImGuiLayout.BeforeElement(
                        1201,
                        PcCompatImGuiElementKind.Label,
                        Label(),
                        default).StartNewRow,
                    Is.True);
                PcCompatManagedResponsiveImGuiLayout.BeforeElement(
                    1202,
                    PcCompatImGuiElementKind.Input,
                    Input(),
                    default);
                PcCompatManagedResponsiveImGuiLayout.EndHorizontal(2001, measuredWidth: 180f);
            }
            finally
            {
                PcCompatManagedResponsiveImGuiLayout.EndFrame();
            }

            // The input/repaint phase uses the Layout plan rather than recompiling it.
            PcCompatManagedResponsiveImGuiLayout.BeginFrame(
                layoutEvent: false,
                contentWidth: 185f,
                fontScale: 1f,
                touchHeight: 48f);
            try
            {
                Assert.That(
                    PcCompatManagedResponsiveImGuiLayout.BeginHorizontal(1001).Mode,
                    Is.EqualTo(PcCompatImGuiContainerMode.Rows));
                PcCompatManagedResponsiveImGuiLayout.EndHorizontal(2001, measuredWidth: 185f);
            }
            finally
            {
                PcCompatManagedResponsiveImGuiLayout.EndFrame();
            }
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.Retire(modId, generation);
        }
    }

    [Test]
    public void OptionMetadataPreservesTheConfirmedGUILayoutDimensions()
    {
        var width = new object();
        var minWidth = new object();
        var maxWidth = new object();
        var height = new object();
        var minHeight = new object();
        var maxHeight = new object();
        var expandWidth = new object();
        var expandHeight = new object();

        PcCompatManagedResponsiveImGuiLayout.TagOption(width, PcCompatImGuiOptionKind.Width, 24f, 1);
        PcCompatManagedResponsiveImGuiLayout.TagOption(minWidth, PcCompatImGuiOptionKind.MinWidth, 42f, 2);
        PcCompatManagedResponsiveImGuiLayout.TagOption(maxWidth, PcCompatImGuiOptionKind.MaxWidth, 96f, 3);
        PcCompatManagedResponsiveImGuiLayout.TagOption(height, PcCompatImGuiOptionKind.Height, 20f, 4);
        PcCompatManagedResponsiveImGuiLayout.TagOption(minHeight, PcCompatImGuiOptionKind.MinHeight, 28f, 5);
        PcCompatManagedResponsiveImGuiLayout.TagOption(maxHeight, PcCompatImGuiOptionKind.MaxHeight, 80f, 6);
        PcCompatManagedResponsiveImGuiLayout.TagOption(expandWidth, PcCompatImGuiOptionKind.ExpandWidth, 1f, 7);
        PcCompatManagedResponsiveImGuiLayout.TagOption(expandHeight, PcCompatImGuiOptionKind.ExpandHeight, 1f, 8);

        var options = PcCompatManagedResponsiveImGuiLayout.ReadOptions(
            new object[]
            {
                width, minWidth, maxWidth, height, minHeight, maxHeight, expandWidth, expandHeight
            });

        Assert.Multiple(() =>
        {
            Assert.That(options.Width, Is.EqualTo(24f));
            Assert.That(options.MinWidth, Is.EqualTo(42f));
            Assert.That(options.MaxWidth, Is.EqualTo(96f));
            Assert.That(options.Height, Is.EqualTo(20f));
            Assert.That(options.MinHeight, Is.EqualTo(28f));
            Assert.That(options.MaxHeight, Is.EqualTo(80f));
            Assert.That(options.ExpandWidth, Is.True);
            Assert.That(options.ExpandHeight, Is.True);
            Assert.That(PcCompatManagedResponsiveImGuiLayout.IsSelectionGridHeightConstraint(height), Is.True);
            Assert.That(PcCompatManagedResponsiveImGuiLayout.IsSelectionGridHeightConstraint(maxHeight), Is.True);
            Assert.That(PcCompatManagedResponsiveImGuiLayout.IsSelectionGridHeightConstraint(width), Is.False);
        });
    }

    [Test]
    public void StrippedOptionMaterializerUsesTheVerifiedUnityOptionEnumValues()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                PcCompatAndroidImGuiOptionBridge.ToNativeOptionType(PcCompatImGuiOptionKind.MaxWidth),
                Is.EqualTo(3));
            Assert.That(
                PcCompatAndroidImGuiOptionBridge.ToNativeOptionType(PcCompatImGuiOptionKind.MinHeight),
                Is.EqualTo(4));
            Assert.That(
                PcCompatAndroidImGuiOptionBridge.ToNativeOptionType(PcCompatImGuiOptionKind.MaxHeight),
                Is.EqualTo(5));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PcCompatAndroidImGuiOptionBridge.ToNativeOptionType(PcCompatImGuiOptionKind.Width));
        });
    }

    [Test]
    public void ResponsiveSelectionGridDropsOnlyTaggedFixedHeightConstraints()
    {
        var width = new object();
        var height = new object();
        var maxHeight = new object();
        var minHeight = new object();
        var unknown = new object();
        PcCompatManagedResponsiveImGuiLayout.TagOption(width, PcCompatImGuiOptionKind.Width, 96f, 1);
        PcCompatManagedResponsiveImGuiLayout.TagOption(height, PcCompatImGuiOptionKind.Height, 20f, 2);
        PcCompatManagedResponsiveImGuiLayout.TagOption(maxHeight, PcCompatImGuiOptionKind.MaxHeight, 40f, 3);
        PcCompatManagedResponsiveImGuiLayout.TagOption(minHeight, PcCompatImGuiOptionKind.MinHeight, 16f, 4);

        var backendType = typeof(PcCompatManagedImGuiBridge).GetNestedType(
            "Backend",
            BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(PcCompatManagedImGuiBridge), "Backend");
        var backend = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(backendType);
        var remove = backendType.GetMethod(
            "RemoveSelectionGridHeightConstraints",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(backendType.FullName, "RemoveSelectionGridHeightConstraints");
        var filtered = (object[])remove.Invoke(
            backend,
            [new object[] { width, height, unknown, maxHeight, minHeight }])!;

        Assert.That(filtered, Is.EqualTo(new object[] { width, unknown, minHeight }));
    }

    [Test]
    public void InteractiveControlsDropOnlyTaggedHeightCapsBelowTheirVisualTextBaseline()
    {
        var width = new object();
        var compactHeight = new object();
        var compactMaximum = new object();
        var minimum = new object();
        var safeHeight = new object();
        var unknown = new object();
        PcCompatManagedResponsiveImGuiLayout.TagOption(width, PcCompatImGuiOptionKind.Width, 24f, 1);
        PcCompatManagedResponsiveImGuiLayout.TagOption(compactHeight, PcCompatImGuiOptionKind.Height, 18f, 2);
        PcCompatManagedResponsiveImGuiLayout.TagOption(compactMaximum, PcCompatImGuiOptionKind.MaxHeight, 20f, 3);
        PcCompatManagedResponsiveImGuiLayout.TagOption(minimum, PcCompatImGuiOptionKind.MinHeight, 16f, 4);
        PcCompatManagedResponsiveImGuiLayout.TagOption(safeHeight, PcCompatImGuiOptionKind.Height, 64f, 5);

        var backendType = typeof(PcCompatManagedImGuiBridge).GetNestedType(
            "Backend",
            BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(PcCompatManagedImGuiBridge), "Backend");
        var backend = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(backendType);
        var normalize = backendType.GetMethod(
            "NormalizeInteractiveOptions",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(backendType.FullName, "NormalizeInteractiveOptions");

        var previous = PcCompatManagedImGuiBridge.EnterMobileSettingsScale(
            1f,
            1f,
            touchHeight: 48f,
            contentWidth: 600f,
            measurementStyleFingerprint: 0);
        try
        {
            var filtered = (object[])normalize.Invoke(
                backend,
                [new object[] { width, compactHeight, compactMaximum, minimum, safeHeight, unknown }])!;

            Assert.Multiple(() =>
            {
                Assert.That(filtered, Is.EqualTo(new object[] { width, minimum, safeHeight, unknown }));
                Assert.That(PcCompatManagedResponsiveImGuiLayout.IsInteractiveHeightConstraintBelow(compactHeight, 36f), Is.True);
                Assert.That(PcCompatManagedResponsiveImGuiLayout.IsInteractiveHeightConstraintBelow(compactMaximum, 36f), Is.True);
                Assert.That(PcCompatManagedResponsiveImGuiLayout.IsInteractiveHeightConstraintBelow(minimum, 36f), Is.False);
                Assert.That(PcCompatManagedResponsiveImGuiLayout.IsInteractiveHeightConstraintBelow(safeHeight, 36f), Is.False);
            });
        }
        finally
        {
            PcCompatManagedImGuiBridge.ExitMobileSettingsScale(previous);
        }
    }

    [Test]
    public void RecycledResponsiveScopeDoesNotRetainBackendOrModOwnedOptions()
    {
        var bridgeType = typeof(PcCompatManagedImGuiBridge);
        var backendType = bridgeType.GetNestedType("Backend", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(PcCompatManagedImGuiBridge), "Backend");
        var scopeType = bridgeType.GetNestedType("ResponsiveLayoutScope", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(PcCompatManagedImGuiBridge), "ResponsiveLayoutScope");
        var backend = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(backendType);
        var scope = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(scopeType);
        var beginVertical = scopeType.GetMethod(
            "BeginVertical",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(scopeType.FullName, "BeginVertical");
        var release = scopeType.GetMethod(
            "Release",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(scopeType.FullName, "Release");

        beginVertical.Invoke(scope, [backend]);
        release.Invoke(scope, null);

        Assert.Multiple(() =>
        {
            Assert.That(
                scopeType.GetField("_backend", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope),
                Is.Null);
            Assert.That(
                scopeType.GetField("_options", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope),
                Is.Null);
            Assert.That(
                Convert.ToBoolean(scopeType.GetProperty("IsHorizontal")!.GetValue(scope)),
                Is.False);
        });
    }

    [TestCase((int)PcCompatImGuiContainerMode.Rows)]
    [TestCase((int)PcCompatImGuiContainerMode.Stacked)]
    public void ResponsiveHorizontalScopeNeverChangesTheModDeclaredLayoutGroupTopology(
        int plannedModeValue)
    {
        var plannedMode = (PcCompatImGuiContainerMode)plannedModeValue;
        var bridgeType = typeof(PcCompatManagedImGuiBridge);
        var backendType = bridgeType.GetNestedType("Backend", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(PcCompatManagedImGuiBridge), "Backend");
        var scopeType = bridgeType.GetNestedType("ResponsiveLayoutScope", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(PcCompatManagedImGuiBridge), "ResponsiveLayoutScope");
        var backend = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(backendType);
        var scope = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(scopeType);

        BindLayoutProbe(backendType, backend, "_beginHorizontal", nameof(ImGuiTopologyProbe.BeginHorizontal));
        BindLayoutProbe(backendType, backend, "_endHorizontal", nameof(ImGuiTopologyProbe.EndHorizontal));
        BindLayoutProbe(backendType, backend, "_beginVertical", nameof(ImGuiTopologyProbe.BeginVertical));
        BindLayoutProbe(backendType, backend, "_endVertical", nameof(ImGuiTopologyProbe.EndVertical));

        var begin = scopeType.GetMethod("BeginHorizontal", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(scopeType.FullName, "BeginHorizontal");
        var close = scopeType.GetMethod("Close", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(scopeType.FullName, "Close");

        ImGuiTopologyProbe.Reset();
        begin.Invoke(scope, [backend, new object(), plannedMode]);
        _ = close.Invoke(scope, null);

        Assert.That(
            ImGuiTopologyProbe.Calls,
            Is.EqualTo(new[] { "begin-horizontal", "end-horizontal" }),
            "A responsive plan may change text policy, but must not replace or add GUILayout groups.");
    }

    [Test]
    public void SelectionGridCellsPartitionOneStableOuterLayoutRectWithoutNestedGroups()
    {
        var first = PcCompatManagedSelectionGridGeometry.ResolveCell(
            outerX: 10f,
            outerY: 20f,
            outerWidth: 308f,
            outerHeight: 104f,
            itemIndex: 0,
            itemCount: 4,
            columns: 2,
            gap: 8f);
        var last = PcCompatManagedSelectionGridGeometry.ResolveCell(
            outerX: 10f,
            outerY: 20f,
            outerWidth: 308f,
            outerHeight: 104f,
            itemIndex: 3,
            itemCount: 4,
            columns: 2,
            gap: 8f);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(new PcCompatImGuiRect(10f, 20f, 150f, 48f)));
            Assert.That(last, Is.EqualTo(new PcCompatImGuiRect(168f, 76f, 150f, 48f)));
        });
    }

    [Test]
    public void SelectionGridUsesOneLayoutEntryAndNoNestedGUILayoutGroups()
    {
        var bridgeType = typeof(PcCompatManagedImGuiBridge);
        var backendType = bridgeType.GetNestedType("Backend", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(PcCompatManagedImGuiBridge), "Backend");
        var backend = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(backendType);
        BindBackendMethod(backendType, backend, "_getSkin", typeof(ImGuiGridProbe), nameof(ImGuiGridProbe.GetSkin));
        BindBackendMethod(backendType, backend, "_getButton", typeof(ImGuiGridProbeStyle), nameof(ImGuiGridProbeStyle.GetButton));
        BindBackendMethod(backendType, backend, "_getWordWrap", typeof(ImGuiGridProbeStyle), nameof(ImGuiGridProbeStyle.GetWordWrap));
        BindBackendMethod(backendType, backend, "_getFixedHeight", typeof(ImGuiGridProbeStyle), nameof(ImGuiGridProbeStyle.GetFixedHeight));
        BindBackendMethod(backendType, backend, "_getRect", typeof(ImGuiGridProbe), nameof(ImGuiGridProbe.GetRect));
        BindBackendMethod(backendType, backend, "_getRectX", typeof(ImGuiGridProbeRect), nameof(ImGuiGridProbeRect.get_x));
        BindBackendMethod(backendType, backend, "_getRectY", typeof(ImGuiGridProbeRect), nameof(ImGuiGridProbeRect.get_y));
        BindBackendMethod(backendType, backend, "_getRectCellWidth", typeof(ImGuiGridProbeRect), nameof(ImGuiGridProbeRect.get_width));
        BindBackendMethod(backendType, backend, "_getRectHeight", typeof(ImGuiGridProbeRect), nameof(ImGuiGridProbeRect.get_height));
        BindBackendMethod(backendType, backend, "_setRectX", typeof(ImGuiGridProbeRect), nameof(ImGuiGridProbeRect.set_x));
        BindBackendMethod(backendType, backend, "_setRectY", typeof(ImGuiGridProbeRect), nameof(ImGuiGridProbeRect.set_y));
        BindBackendMethod(backendType, backend, "_setRectWidth", typeof(ImGuiGridProbeRect), nameof(ImGuiGridProbeRect.set_width));
        BindBackendMethod(backendType, backend, "_setRectHeight", typeof(ImGuiGridProbeRect), nameof(ImGuiGridProbeRect.set_height));
        BindBackendMethod(backendType, backend, "_guiToggle", typeof(ImGuiGridProbe), nameof(ImGuiGridProbe.Toggle));
        backendType.GetField("_contentFromText", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(backend, typeof(ImGuiGridProbeContent).GetConstructor([typeof(string)])!);

        var selectionGrid = backendType.GetMethod("SelectionGrid", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(backendType.FullName, "SelectionGrid");
        ImGuiGridProbe.Reset();
        var result = selectionGrid.Invoke(
            backend,
            [0, new[] { "left", "right" }, 2, Array.Empty<object>(), null, 0f, 0f, false]);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(0));
            Assert.That(ImGuiGridProbe.GetRectCalls, Is.EqualTo(1));
            Assert.That(ImGuiGridProbe.ToggleRects, Has.Count.EqualTo(2));
            Assert.That(ImGuiGridProbe.ToggleRects[0], Is.EqualTo(new PcCompatImGuiRect(10f, 20f, 150f, 48f)));
            Assert.That(ImGuiGridProbe.ToggleRects[1], Is.EqualTo(new PcCompatImGuiRect(168f, 20f, 150f, 48f)));
        });
    }

    [Test]
    public void SelectionGridCanMoveFromAHigherIndexToALowerIndex()
    {
        var bridgeType = typeof(PcCompatManagedImGuiBridge);
        var backendType = bridgeType.GetNestedType("Backend", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(PcCompatManagedImGuiBridge), "Backend");
        var backend = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(backendType);
        BindBackendMethod(backendType, backend, "_getSkin", typeof(ImGuiGridProbe), nameof(ImGuiGridProbe.GetSkin));
        BindBackendMethod(backendType, backend, "_getButton", typeof(ImGuiGridProbeStyle), nameof(ImGuiGridProbeStyle.GetButton));
        BindBackendMethod(backendType, backend, "_getWordWrap", typeof(ImGuiGridProbeStyle), nameof(ImGuiGridProbeStyle.GetWordWrap));
        BindBackendMethod(backendType, backend, "_getFixedHeight", typeof(ImGuiGridProbeStyle), nameof(ImGuiGridProbeStyle.GetFixedHeight));
        BindBackendMethod(backendType, backend, "_getRect", typeof(ImGuiGridProbe), nameof(ImGuiGridProbe.GetRect));
        BindBackendMethod(backendType, backend, "_getRectX", typeof(ImGuiGridProbeRect), nameof(ImGuiGridProbeRect.get_x));
        BindBackendMethod(backendType, backend, "_getRectY", typeof(ImGuiGridProbeRect), nameof(ImGuiGridProbeRect.get_y));
        BindBackendMethod(backendType, backend, "_getRectCellWidth", typeof(ImGuiGridProbeRect), nameof(ImGuiGridProbeRect.get_width));
        BindBackendMethod(backendType, backend, "_getRectHeight", typeof(ImGuiGridProbeRect), nameof(ImGuiGridProbeRect.get_height));
        BindBackendMethod(backendType, backend, "_setRectX", typeof(ImGuiGridProbeRect), nameof(ImGuiGridProbeRect.set_x));
        BindBackendMethod(backendType, backend, "_setRectY", typeof(ImGuiGridProbeRect), nameof(ImGuiGridProbeRect.set_y));
        BindBackendMethod(backendType, backend, "_setRectWidth", typeof(ImGuiGridProbeRect), nameof(ImGuiGridProbeRect.set_width));
        BindBackendMethod(backendType, backend, "_setRectHeight", typeof(ImGuiGridProbeRect), nameof(ImGuiGridProbeRect.set_height));
        BindBackendMethod(backendType, backend, "_guiToggle", typeof(ImGuiGridProbe), nameof(ImGuiGridProbe.Toggle));
        backendType.GetField("_contentFromText", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(backend, typeof(ImGuiGridProbeContent).GetConstructor([typeof(string)])!);

        var selectionGrid = backendType.GetMethod("SelectionGrid", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(backendType.FullName, "SelectionGrid");
        ImGuiGridProbe.Reset(clickedLabel: "left");
        var result = selectionGrid.Invoke(
            backend,
            [1, new[] { "left", "right" }, 2, Array.Empty<object>(), null, 0f, 0f, false]);

        Assert.That(
            result,
            Is.EqualTo(0),
            "The previously selected cell remains true; it must not overwrite a new lower-index click.");
    }

    [Test]
    public void SelectionGridColumnsAreFrozenOutsideLayoutAndSafelyReplannedWhenMeasurementsChange()
    {
        const string modId = "ResponsiveSelectionGrid";
        const long generation = 76;
        using var execution = PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
            modId,
            generation,
            PcCompatManagedExecutionPhase.Update));
        var measurement = new PcCompatSelectionGridMeasurement(
            CellMinimumWidth: 48f,
            CellPreferredWidth: 70f,
            CellPreferredHeight: 48f,
            ItemCount: 5);
        var outerMeasurement = measurement.ToOuterMeasurement(requestedColumns: 3);

        Assert.Multiple(() =>
        {
            Assert.That(outerMeasurement.MinimumWidth, Is.EqualTo(160f));
            Assert.That(outerMeasurement.PreferredWidth, Is.EqualTo(226f));
        });

        try
        {
            // The first Layout keeps the MOD's requested grid topology. The compact
            // plan may only become visible during the following Layout.
            var safe = ResolveSelectionGrid(layoutEvent: true, width: 180f, measurement);
            Assert.Multiple(() =>
            {
                Assert.That(safe.Columns, Is.EqualTo(5));
                Assert.That(safe.CellWidth, Is.Zero);
                Assert.That(safe.OverrideHeightConstraints, Is.False);
            });
            Assert.That(
                ResolveSelectionGrid(layoutEvent: false, width: 320f, measurement).Columns,
                Is.EqualTo(5));

            var compact = ResolveSelectionGrid(layoutEvent: true, width: 180f, measurement);
            Assert.Multiple(() =>
            {
                Assert.That(compact.Columns, Is.EqualTo(3));
                Assert.That(compact.WrapLabels, Is.True);
                Assert.That(compact.CellWidth, Is.EqualTo(164f / 3f).Within(0.001f));
                Assert.That(compact.OverrideHeightConstraints, Is.True);
            });

            // Input/Repaint must retain the same structural column count even if a
            // size change becomes observable before the next Layout transaction.
            Assert.That(
                ResolveSelectionGrid(layoutEvent: false, width: 72f, measurement).Columns,
                Is.EqualTo(3));
            Assert.That(
                ResolveSelectionGrid(layoutEvent: true, width: 72f, measurement).Columns,
                Is.EqualTo(5));

            // A changed label/font measurement cannot reuse the prior compact plan.
            var changedMeasurement = measurement with { CellPreferredWidth = 120f };
            Assert.That(
                ResolveSelectionGrid(layoutEvent: true, width: 180f, changedMeasurement).Columns,
                Is.EqualTo(5));
            Assert.That(
                ResolveSelectionGrid(layoutEvent: true, width: 180f, changedMeasurement).Columns,
                Is.EqualTo(3));
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.Retire(modId, generation);
        }
    }

    [Test]
    public void ResponsiveLayoutRepaintSteadyStateIsAllocationFree()
    {
        const string modId = "ResponsiveAllocationContract";
        const long generation = 77;
        using var execution = PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
            modId,
            generation,
            PcCompatManagedExecutionPhase.Update));

        try
        {
            for (var index = 0; index < 8; ++index)
                RunResponsiveRepaintFrame();

            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 128; ++index)
                RunResponsiveRepaintFrame();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            Assert.That(allocated, Is.Zero);
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.Retire(modId, generation);
        }
    }

    [Test]
    public void ResponsiveLayoutDenseRepaintHostMicrobenchmarkHasNoAllocationOrPathologicalP95()
    {
        const string modId = "ResponsiveDenseHostBenchmark";
        const long generation = 94;
        const int frames = 256;
        using var execution = PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
            modId,
            generation,
            PcCompatManagedExecutionPhase.Update));

        try
        {
            // First Layout discovers two 64-control rows; the second commits their
            // segmented plans. Later Repaints exercise 128 actual bridge elements.
            RunDenseResponsiveFrame(layoutEvent: true, PcCompatImGuiContainerMode.PassThrough);
            RunDenseResponsiveFrame(layoutEvent: true, PcCompatImGuiContainerMode.Rows);
            for (var index = 0; index < 32; ++index)
                RunDenseResponsiveFrame(layoutEvent: false, PcCompatImGuiContainerMode.Rows);

            var samples = new long[frames];
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < samples.Length; ++index)
            {
                var start = Stopwatch.GetTimestamp();
                RunDenseResponsiveFrame(layoutEvent: false, PcCompatImGuiContainerMode.Rows);
                samples[index] = Stopwatch.GetTimestamp() - start;
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Array.Sort(samples);
            var p95Ticks = samples[(samples.Length * 95 + 99) / 100 - 1];
            var p95Microseconds = p95Ticks * 1_000_000d / Stopwatch.Frequency;

            Assert.Multiple(() =>
            {
                Assert.That(allocated, Is.Zero);
                Assert.That(p95Microseconds, Is.LessThan(5_000d),
                    "host sanity limit only; device-side IL2CPP timing remains a manual acceptance item");
            });
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.Retire(modId, generation);
        }
    }

    [Test]
    public void UnrecognizedJrpButtonAndFlexibleSpaceRowKeepsNativeHorizontalTopology()
    {
        const string modId = "ResponsiveJrpTopology";
        const long generation = 78;
        using var execution = PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
            modId,
            generation,
            PcCompatManagedExecutionPhase.Update));

        try
        {
            // JRP repeatedly uses a semantic button followed by FlexibleSpace. It is
            // not a label/control pair, so the responsive layer has no proof that
            // changing BeginHorizontal into BeginVertical preserves its semantics.
            RunUnrecognizedJrpRow(layoutEvent: true, PcCompatImGuiContainerMode.PassThrough);
            RunUnrecognizedJrpRow(layoutEvent: false, PcCompatImGuiContainerMode.PassThrough);
            RunUnrecognizedJrpRow(layoutEvent: true, PcCompatImGuiContainerMode.PassThrough);
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.Retire(modId, generation);
        }
    }

    [Test]
    public void RepaintAndInputFramesNeverRequestNewMeasurements()
    {
        const string modId = "ResponsiveMeasurementGate";
        const long generation = 79;
        using var execution = PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
            modId,
            generation,
            PcCompatManagedExecutionPhase.Update));

        try
        {
            PcCompatManagedResponsiveImGuiLayout.BeginFrame(true, 240f, 1f, 48f);
            try
            {
                Assert.That(PcCompatManagedResponsiveImGuiLayout.RequiresMeasurement, Is.False);
                _ = PcCompatManagedResponsiveImGuiLayout.BeginHorizontal(7901);
                Assert.That(PcCompatManagedResponsiveImGuiLayout.RequiresMeasurement, Is.True);
                _ = PcCompatManagedResponsiveImGuiLayout.EndHorizontal(7902, 240f);
                Assert.That(PcCompatManagedResponsiveImGuiLayout.RequiresMeasurement, Is.False);
            }
            finally
            {
                PcCompatManagedResponsiveImGuiLayout.EndFrame();
            }

            PcCompatManagedResponsiveImGuiLayout.BeginFrame(false, 240f, 1f, 48f);
            try
            {
                Assert.That(PcCompatManagedResponsiveImGuiLayout.RequiresMeasurement, Is.False);
            }
            finally
            {
                PcCompatManagedResponsiveImGuiLayout.EndFrame();
            }
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.Retire(modId, generation);
        }
    }

    [Test]
    public void FixedHeightControlsCanWrapInsideResponsiveRowsAfterTheirUnsafeCapIsNormalized()
    {
        const string modId = "ResponsiveFixedHeight";
        const long generation = 80;
        using var execution = PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
            modId,
            generation,
            PcCompatManagedExecutionPhase.Update));

        try
        {
            PcCompatManagedResponsiveImGuiLayout.BeginFrame(true, 180f, 1f, 48f);
            try
            {
                _ = PcCompatManagedResponsiveImGuiLayout.BeginHorizontal(8201);
                CaptureWideLabelInputGroups();
                _ = PcCompatManagedResponsiveImGuiLayout.EndHorizontal(8202, 180f);
            }
            finally
            {
                PcCompatManagedResponsiveImGuiLayout.EndFrame();
            }

            PcCompatManagedResponsiveImGuiLayout.BeginFrame(true, 180f, 1f, 48f);
            try
            {
                Assert.That(
                    PcCompatManagedResponsiveImGuiLayout.BeginHorizontal(8201).Mode,
                    Is.EqualTo(PcCompatImGuiContainerMode.Rows));
                var decision = PcCompatManagedResponsiveImGuiLayout.BeforeElement(
                    8211,
                    PcCompatImGuiElementKind.Label,
                    WideLabel(),
                    new PcCompatImGuiOptionSnapshot(Width: 80f, Height: 20f));
                Assert.That(decision.WrapText, Is.True);
                _ = PcCompatManagedResponsiveImGuiLayout.EndHorizontal(8202, 180f);
            }
            finally
            {
                PcCompatManagedResponsiveImGuiLayout.EndFrame();
            }
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.Retire(modId, generation);
        }
    }

    [Test]
    public void NativePassThroughKeepsTheModDeclaredWordWrapContract()
    {
        const string modId = "ResponsivePreserveWrap";
        const long generation = 81;
        using var execution = PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
            modId,
            generation,
            PcCompatManagedExecutionPhase.Update));

        try
        {
            PcCompatManagedResponsiveImGuiLayout.BeginFrame(true, 320f, 1f, 48f);
            try
            {
                Assert.That(
                    PcCompatManagedResponsiveImGuiLayout.BeginHorizontal(8301).Mode,
                    Is.EqualTo(PcCompatImGuiContainerMode.PassThrough));
                var decision = PcCompatManagedResponsiveImGuiLayout.BeforeElement(
                    8302,
                    PcCompatImGuiElementKind.Label,
                    WideLabel(),
                    default);
                Assert.That(decision.WrapText, Is.Null);
                _ = PcCompatManagedResponsiveImGuiLayout.EndHorizontal(8303, 320f);
            }
            finally
            {
                PcCompatManagedResponsiveImGuiLayout.EndFrame();
            }
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.Retire(modId, generation);
        }
    }

    [Test]
    public void ACommittedPlanStaysFrozenUntilTheNextLayoutWhenTheContentWidthShrinks()
    {
        const string modId = "ResponsiveWidthContract";
        const long generation = 72;
        using var execution = PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
            modId,
            generation,
            PcCompatManagedExecutionPhase.Update));

        try
        {
            RunLayout(width: 300f, CaptureTwoLabelInputGroups, PcCompatImGuiContainerMode.PassThrough);
            RunRepaint(width: 300f, PcCompatImGuiContainerMode.PassThrough);
            RunLayout(width: 300f, CaptureTwoLabelInputGroups, PcCompatImGuiContainerMode.Horizontal);

            // A width change observed by Repaint/input cannot change the structure
            // selected by the preceding Layout. The next Layout preserves native topology
            // while staging the narrower plan; only the following Layout applies it.
            RunRepaint(width: 250f, PcCompatImGuiContainerMode.Horizontal);
            RunLayout(width: 250f, CaptureTwoLabelInputGroups, PcCompatImGuiContainerMode.PassThrough);
            RunLayout(width: 250f, CaptureTwoLabelInputGroups, PcCompatImGuiContainerMode.Rows);
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.Retire(modId, generation);
        }
    }

    [Test]
    public void MeasurementStyleFingerprintInvalidatesACommittedPlanOnlyAtTheNextLayoutBoundary()
    {
        const string modId = "ResponsiveStyleEnvironment";
        const long generation = 92;
        using var execution = PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
            modId,
            generation,
            PcCompatManagedExecutionPhase.Update));

        try
        {
            RunLayout(
                width: 300f,
                CaptureTwoLabelInputGroups,
                PcCompatImGuiContainerMode.PassThrough,
                measurementStyleFingerprint: 101);
            RunLayout(
                width: 300f,
                CaptureTwoLabelInputGroups,
                PcCompatImGuiContainerMode.Horizontal,
                measurementStyleFingerprint: 101);

            // A font, padding or host-skin change observed between Layout events must
            // not modify the Repaint structure that belongs to the previous Layout.
            RunRepaint(
                width: 300f,
                PcCompatImGuiContainerMode.Horizontal,
                measurementStyleFingerprint: 202);

            // The next Layout safely falls back to the native declaration while it
            // measures and stages a new plan for the changed environment.
            RunLayout(
                width: 300f,
                CaptureTwoLabelInputGroups,
                PcCompatImGuiContainerMode.PassThrough,
                measurementStyleFingerprint: 202);
            RunRepaint(
                width: 300f,
                PcCompatImGuiContainerMode.PassThrough,
                measurementStyleFingerprint: 202);
            RunLayout(
                width: 300f,
                CaptureTwoLabelInputGroups,
                PcCompatImGuiContainerMode.Horizontal,
                measurementStyleFingerprint: 202);
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.Retire(modId, generation);
        }
    }

    [Test]
    public void DynamicTextAndSelectionGridFingerprintsParticipateInLayoutPlanIdentity()
    {
        var firstText = new PcCompatImGuiMeasurement(
            70f,
            70f,
            SupportsTextWrapping: true,
            LayoutFingerprint: 401);
        var changedText = firstText with { LayoutFingerprint = 402 };
        Assert.That(changedText, Is.Not.EqualTo(firstText));

        var selectionGridPlan = typeof(PcCompatManagedResponsiveImGuiLayout).GetNestedType(
            "SelectionGridPlan",
            BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(PcCompatManagedResponsiveImGuiLayout), "SelectionGridPlan");
        var computeFingerprint = selectionGridPlan.GetMethod(
            "ComputeFingerprint",
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(selectionGridPlan.FullName, "ComputeFingerprint");
        var firstGrid = new PcCompatSelectionGridMeasurement(
            CellMinimumWidth: 48f,
            CellPreferredWidth: 96f,
            CellPreferredHeight: 32f,
            ItemCount: 3,
            LayoutFingerprint: 501);
        var changedGrid = firstGrid with { LayoutFingerprint = 502 };
        var firstFingerprint = (int)computeFingerprint.Invoke(
            null,
            [17, 3, firstGrid, default(PcCompatImGuiOptionSnapshot)])!;
        var changedFingerprint = (int)computeFingerprint.Invoke(
            null,
            [17, 3, changedGrid, default(PcCompatImGuiOptionSnapshot)])!;

        Assert.That(changedFingerprint, Is.Not.EqualTo(firstFingerprint));
    }

    [Test]
    public void DynamicMeasurementChangeStagesAReplacementWithoutChangingTheCurrentLayout()
    {
        const string modId = "ResponsiveDynamicMeasurement";
        const long generation = 93;
        using var execution = PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
            modId,
            generation,
            PcCompatManagedExecutionPhase.Update));

        try
        {
            RunFingerprintLayout(
                width: 300f,
                labelWidth: 70f,
                labelFingerprint: 601,
                expected: PcCompatImGuiContainerMode.PassThrough);
            RunFingerprintLayout(
                width: 300f,
                labelWidth: 70f,
                labelFingerprint: 601,
                expected: PcCompatImGuiContainerMode.Horizontal);
            RunFingerprintRepaint(300f, PcCompatImGuiContainerMode.Horizontal);

            // The changed text is observed during Layout, but the old horizontal
            // transaction remains the active structure for this pass. The replacement
            // plan is staged for the following Layout only.
            PcCompatManagedResponsiveImGuiLayout.BeginFrame(true, 300f, 1f, 48f);
            try
            {
                Assert.That(
                    PcCompatManagedResponsiveImGuiLayout.BeginHorizontal(DynamicRowBeginToken).Mode,
                    Is.EqualTo(PcCompatImGuiContainerMode.Horizontal));
                var decision = PcCompatManagedResponsiveImGuiLayout.BeforeElement(
                    DynamicRowFirstLabelToken,
                    PcCompatImGuiElementKind.Label,
                    new PcCompatImGuiMeasurement(
                        180f,
                        180f,
                        SupportsTextWrapping: true,
                        LayoutFingerprint: 602),
                    default);
                Assert.That(decision.WrapText, Is.Null);
                PcCompatManagedResponsiveImGuiLayout.BeforeElement(
                    DynamicRowFirstInputToken,
                    PcCompatImGuiElementKind.Input,
                    new PcCompatImGuiMeasurement(60f, 60f, SupportsTextWrapping: false),
                    default);
                PcCompatManagedResponsiveImGuiLayout.BeforeElement(
                    DynamicRowSecondLabelToken,
                    PcCompatImGuiElementKind.Label,
                    new PcCompatImGuiMeasurement(
                        180f,
                        180f,
                        SupportsTextWrapping: true,
                        LayoutFingerprint: 602),
                    default);
                PcCompatManagedResponsiveImGuiLayout.BeforeElement(
                    DynamicRowSecondInputToken,
                    PcCompatImGuiElementKind.Input,
                    new PcCompatImGuiMeasurement(60f, 60f, SupportsTextWrapping: false),
                    default);
                PcCompatManagedResponsiveImGuiLayout.EndHorizontal(DynamicRowEndToken, 300f);
            }
            finally
            {
                PcCompatManagedResponsiveImGuiLayout.EndFrame();
            }

            RunFingerprintLayout(
                width: 300f,
                labelWidth: 180f,
                labelFingerprint: 602,
                expected: PcCompatImGuiContainerMode.Rows);
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.Retire(modId, generation);
        }
    }

    [Test]
    public void RepeatedContainerCallsitesKeepIndependentPlansForDynamicRows()
    {
        const string modId = "ResponsiveRepeatedRows";
        const long generation = 73;
        using var execution = PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
            modId,
            generation,
            PcCompatManagedExecutionPhase.Update));

        try
        {
            // The same BeginHorizontal IL site can execute repeatedly inside a MOD loop.
            // The first row needs segmentation while the second fits directly.
            CaptureRepeatedRows(layoutEvent: true, PcCompatImGuiContainerMode.PassThrough, PcCompatImGuiContainerMode.PassThrough);
            CaptureRepeatedRows(layoutEvent: false, PcCompatImGuiContainerMode.PassThrough, PcCompatImGuiContainerMode.PassThrough);
            CaptureRepeatedRows(layoutEvent: true, PcCompatImGuiContainerMode.Rows, PcCompatImGuiContainerMode.Horizontal);
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.Retire(modId, generation);
        }
    }

    [Test]
    public void SegmentedRowsUseEightPixelHysteresisBeforeReturningToHorizontal()
    {
        const string modId = "ResponsiveHysteresis";
        const long generation = 74;
        using var execution = PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
            modId,
            generation,
            PcCompatManagedExecutionPhase.Update));

        try
        {
            RunLayout(width: 180f, CaptureTwoLabelInputGroups, PcCompatImGuiContainerMode.PassThrough);
            RunRepaint(width: 180f, PcCompatImGuiContainerMode.PassThrough);
            RunLayout(width: 180f, CaptureTwoLabelInputGroups, PcCompatImGuiContainerMode.Rows);

            // Required width is 260. At 262 px rows remain selected and the next candidate
            // remains rows; the 8 px recovery margin blocks layout oscillation.
            RunLayout(width: 262f, CaptureTwoLabelInputGroups, PcCompatImGuiContainerMode.Rows);
            RunLayout(width: 262f, CaptureTwoLabelInputGroups, PcCompatImGuiContainerMode.Rows);

            // Once the full recovery margin is available, the current Layout remains rows
            // and only the following Layout promotes the direct horizontal structure.
            RunLayout(width: 268f, CaptureTwoLabelInputGroups, PcCompatImGuiContainerMode.Rows);
            RunLayout(width: 268f, CaptureTwoLabelInputGroups, PcCompatImGuiContainerMode.Horizontal);
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.Retire(modId, generation);
        }
    }

    [Test]
    public void OnlyRepaintGeometryCanUpdateANestedContainerWidthCache()
    {
        const string modId = "ResponsiveGeometryObservation";
        const long generation = 75;
        using var execution = PcCompatManagedExecutionContext.Enter(new PcCompatManagedExecutionState(
            modId,
            generation,
            PcCompatManagedExecutionPhase.Update));

        try
        {
            RunNestedFrame(
                layoutEvent: true,
                geometryEvent: false,
                measuredInnerWidth: 300f,
                expectedInner: PcCompatImGuiContainerMode.PassThrough);
            RunNestedFrame(
                layoutEvent: true,
                geometryEvent: false,
                measuredInnerWidth: 300f,
                expectedInner: PcCompatImGuiContainerMode.Horizontal);

            // A mouse/key/Used path can legally return a non-final or dummy last rect.
            // Its narrow value must not survive into the next Layout transaction.
            RunNestedFrame(
                layoutEvent: false,
                geometryEvent: false,
                measuredInnerWidth: 100f,
                expectedInner: PcCompatImGuiContainerMode.Horizontal);
            RunNestedFrame(
                layoutEvent: true,
                geometryEvent: false,
                measuredInnerWidth: 300f,
                expectedInner: PcCompatImGuiContainerMode.Horizontal);
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.Retire(modId, generation);
        }
    }

    [Test]
    public void ResponsiveOptionSnapshotHotPathIsAllocationFreeForManagedOptionArrays()
    {
        var width = new object();
        var height = new object();
        PcCompatManagedResponsiveImGuiLayout.TagOption(width, PcCompatImGuiOptionKind.Width, 96f, 1);
        PcCompatManagedResponsiveImGuiLayout.TagOption(height, PcCompatImGuiOptionKind.Height, 48f, 2);
        object[] options = [width, height];

        for (var index = 0; index < 8; ++index)
            _ = PcCompatManagedResponsiveImGuiLayout.ReadOptions(options);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 128; ++index)
            _ = PcCompatManagedResponsiveImGuiLayout.ReadOptions(options);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.That(allocated, Is.Zero);
    }

    [Test]
    public void ConfirmedGUILayoutSurfaceIsAlwaysTokenizedByTheProductionRewriter()
    {
        var factory = typeof(PcCompatAndroidManagedAssemblyRewrite).GetMethod(
            "BuildManagedCallBridgeRewrites",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(PcCompatAndroidManagedAssemblyRewrite));
        var specs = (IReadOnlyList<ManagedCallBridgeRewriteSpec>)factory.Invoke(null, ["ResponsiveTestMod"])!;

        var expected = new[]
        {
            ("BeginHorizontal", new[] { "UnityEngine.GUILayoutOption[]" }),
            ("EndHorizontal", Array.Empty<string>()),
            ("BeginVertical", new[] { "UnityEngine.GUILayoutOption[]" }),
            ("EndVertical", Array.Empty<string>()),
            ("Space", new[] { "System.Single" }),
            ("FlexibleSpace", Array.Empty<string>()),
            ("Width", new[] { "System.Single" }),
            ("MinWidth", new[] { "System.Single" }),
            ("MaxWidth", new[] { "System.Single" }),
            ("Height", new[] { "System.Single" }),
            ("MinHeight", new[] { "System.Single" }),
            ("MaxHeight", new[] { "System.Single" }),
            ("ExpandWidth", new[] { "System.Boolean" }),
            ("ExpandHeight", new[] { "System.Boolean" }),
            ("Button", new[] { "System.String", "UnityEngine.GUILayoutOption[]" }),
            ("Button", new[] { "System.String", "UnityEngine.GUIStyle", "UnityEngine.GUILayoutOption[]" }),
            ("Label", new[] { "System.String", "UnityEngine.GUILayoutOption[]" }),
            ("Label", new[] { "System.String", "UnityEngine.GUIStyle", "UnityEngine.GUILayoutOption[]" }),
            ("Toggle", new[] { "System.Boolean", "System.String", "UnityEngine.GUILayoutOption[]" }),
            ("Toggle", new[] { "System.Boolean", "System.String", "UnityEngine.GUIStyle", "UnityEngine.GUILayoutOption[]" }),
            ("Toggle", new[] { "System.Boolean", "UnityEngine.GUIContent", "UnityEngine.GUILayoutOption[]" }),
            ("TextField", new[] { "System.String", "UnityEngine.GUILayoutOption[]" }),
            ("TextArea", new[] { "System.String", "UnityEngine.GUILayoutOption[]" }),
            ("HorizontalSlider", new[] { "System.Single", "System.Single", "System.Single", "UnityEngine.GUILayoutOption[]" }),
            ("SelectionGrid", new[] { "System.Int32", "System.String[]", "System.Int32", "UnityEngine.GUILayoutOption[]" })
        };

        foreach (var (method, parameters) in expected)
        {
            var spec = specs.SingleOrDefault(candidate =>
                candidate.SourceAssembly == "UnityEngine.IMGUIModule" &&
                candidate.SourceType == "UnityEngine.GUILayout" &&
                candidate.SourceMethod == method &&
                candidate.SourceParameterTypes.SequenceEqual(parameters));
            Assert.That(spec, Is.Not.Null, $"missing responsive bridge spec: {method}");
            Assert.That(spec!.AppendCallsiteToken, Is.True, $"token missing: {method}");
        }
    }

    private static void CaptureTwoLabelInputGroups()
    {
        PcCompatManagedResponsiveImGuiLayout.BeforeElement(
            1101,
            PcCompatImGuiElementKind.Label,
            Label(),
            default);
        PcCompatManagedResponsiveImGuiLayout.BeforeElement(
            1102,
            PcCompatImGuiElementKind.Input,
            Input(),
            default);
        PcCompatManagedResponsiveImGuiLayout.BeforeElement(
            1201,
            PcCompatImGuiElementKind.Label,
            Label(),
            default);
        PcCompatManagedResponsiveImGuiLayout.BeforeElement(
            1202,
            PcCompatImGuiElementKind.Input,
            Input(),
            default);
    }

    private static void CaptureWideLabelInputGroups()
    {
        PcCompatManagedResponsiveImGuiLayout.BeforeElement(
            8211,
            PcCompatImGuiElementKind.Label,
            WideLabel(),
            default);
        PcCompatManagedResponsiveImGuiLayout.BeforeElement(
            8212,
            PcCompatImGuiElementKind.Input,
            new PcCompatImGuiMeasurement(40f, 40f, SupportsTextWrapping: false),
            default);
        PcCompatManagedResponsiveImGuiLayout.BeforeElement(
            8221,
            PcCompatImGuiElementKind.Label,
            WideLabel(),
            default);
        PcCompatManagedResponsiveImGuiLayout.BeforeElement(
            8222,
            PcCompatImGuiElementKind.Input,
            new PcCompatImGuiMeasurement(40f, 40f, SupportsTextWrapping: false),
            default);
    }

    private static PcCompatSelectionGridDecision ResolveSelectionGrid(
        bool layoutEvent,
        float width,
        PcCompatSelectionGridMeasurement measurement)
    {
        PcCompatManagedResponsiveImGuiLayout.BeginFrame(layoutEvent, width, 1f, 48f);
        try
        {
            return PcCompatManagedResponsiveImGuiLayout.SelectSelectionGridColumns(
                6101,
                requestedColumns: 5,
                measurement,
                default);
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.EndFrame();
        }
    }

    private static void RunResponsiveRepaintFrame()
    {
        PcCompatManagedResponsiveImGuiLayout.BeginFrame(
            layoutEvent: false,
            contentWidth: 320f,
            fontScale: 1f,
            touchHeight: 48f);
        try
        {
            _ = PcCompatManagedResponsiveImGuiLayout.BeginHorizontal(7101);
            _ = PcCompatManagedResponsiveImGuiLayout.BeforeElement(
                7102,
                PcCompatImGuiElementKind.Label,
                Label(),
                default);
            _ = PcCompatManagedResponsiveImGuiLayout.SelectSelectionGridColumns(
                7104,
                requestedColumns: 3,
                new PcCompatSelectionGridMeasurement(
                    CellMinimumWidth: 48f,
                    CellPreferredWidth: 64f,
                    CellPreferredHeight: 48f,
                    ItemCount: 3),
                default);
            _ = PcCompatManagedResponsiveImGuiLayout.EndHorizontal(7103, measuredWidth: 320f);
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.EndFrame();
        }
    }

    private static void CaptureRepeatedRows(
        bool layoutEvent,
        PcCompatImGuiContainerMode firstExpected,
        PcCompatImGuiContainerMode secondExpected)
    {
        PcCompatManagedResponsiveImGuiLayout.BeginFrame(layoutEvent, 180f, 1f, 48f);
        try
        {
            Assert.That(
                PcCompatManagedResponsiveImGuiLayout.BeginHorizontal(4101).Mode,
                Is.EqualTo(firstExpected));
            CaptureTwoLabelInputGroups();
            PcCompatManagedResponsiveImGuiLayout.EndHorizontal(4102, 180f);

            Assert.That(
                PcCompatManagedResponsiveImGuiLayout.BeginHorizontal(4101).Mode,
                Is.EqualTo(secondExpected));
            PcCompatManagedResponsiveImGuiLayout.BeforeElement(
                4201,
                PcCompatImGuiElementKind.Label,
                Label(),
                default);
            PcCompatManagedResponsiveImGuiLayout.BeforeElement(
                4202,
                PcCompatImGuiElementKind.Input,
                Input(),
                default);
            PcCompatManagedResponsiveImGuiLayout.EndHorizontal(4102, 180f);
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.EndFrame();
        }
    }

    private static void RunUnrecognizedJrpRow(
        bool layoutEvent,
        PcCompatImGuiContainerMode expected)
    {
        PcCompatManagedResponsiveImGuiLayout.BeginFrame(layoutEvent, 120f, 1f, 48f);
        try
        {
            Assert.That(
                PcCompatManagedResponsiveImGuiLayout.BeginHorizontal(8101).Mode,
                Is.EqualTo(expected));
            _ = PcCompatManagedResponsiveImGuiLayout.BeforeElement(
                8102,
                PcCompatImGuiElementKind.Button,
                new PcCompatImGuiMeasurement(64f, 64f, SupportsTextWrapping: true),
                default);
            _ = PcCompatManagedResponsiveImGuiLayout.BeforeElement(
                8103,
                PcCompatImGuiElementKind.FlexibleSpace,
                new PcCompatImGuiMeasurement(0f, 0f, SupportsTextWrapping: false, ExpandWidth: true),
                default);
            _ = PcCompatManagedResponsiveImGuiLayout.EndHorizontal(8104, 120f);
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.EndFrame();
        }
    }

    private static void RunNestedFrame(
        bool layoutEvent,
        bool geometryEvent,
        float measuredInnerWidth,
        PcCompatImGuiContainerMode expectedInner)
    {
        PcCompatManagedResponsiveImGuiLayout.BeginFrame(
            layoutEvent,
            geometryEvent,
            contentWidth: 300f,
            fontScale: 1f,
            touchHeight: 48f);
        try
        {
            _ = PcCompatManagedResponsiveImGuiLayout.BeginHorizontal(5101);
            Assert.That(
                PcCompatManagedResponsiveImGuiLayout.BeginHorizontal(5102).Mode,
                Is.EqualTo(expectedInner));
            CaptureTwoLabelInputGroups();
            PcCompatManagedResponsiveImGuiLayout.EndHorizontal(5103, measuredInnerWidth);
            PcCompatManagedResponsiveImGuiLayout.EndHorizontal(5104, 300f);
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.EndFrame();
        }
    }

    private static void RunLayout(
        float width,
        Action capture,
        PcCompatImGuiContainerMode expected = PcCompatImGuiContainerMode.Stacked,
        int measurementStyleFingerprint = 0)
    {
        PcCompatManagedResponsiveImGuiLayout.BeginFrame(
            layoutEvent: true,
            geometryEvent: false,
            contentWidth: width,
            fontScale: 1f,
            touchHeight: 48f,
            measurementStyleFingerprint);
        try
        {
            Assert.That(PcCompatManagedResponsiveImGuiLayout.BeginHorizontal(3001).Mode, Is.EqualTo(expected));
            capture();
            PcCompatManagedResponsiveImGuiLayout.EndHorizontal(3002, width);
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.EndFrame();
        }
    }

    private static void RunRepaint(
        float width,
        PcCompatImGuiContainerMode expected,
        int measurementStyleFingerprint = 0)
    {
        PcCompatManagedResponsiveImGuiLayout.BeginFrame(
            layoutEvent: false,
            geometryEvent: true,
            contentWidth: width,
            fontScale: 1f,
            touchHeight: 48f,
            measurementStyleFingerprint);
        try
        {
            Assert.That(PcCompatManagedResponsiveImGuiLayout.BeginHorizontal(3001).Mode, Is.EqualTo(expected));
            PcCompatManagedResponsiveImGuiLayout.EndHorizontal(3002, width);
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.EndFrame();
        }
    }

    private static void RunFingerprintLayout(
        float width,
        float labelWidth,
        int labelFingerprint,
        PcCompatImGuiContainerMode expected)
    {
        PcCompatManagedResponsiveImGuiLayout.BeginFrame(true, width, 1f, 48f);
        try
        {
            Assert.That(
                PcCompatManagedResponsiveImGuiLayout.BeginHorizontal(DynamicRowBeginToken).Mode,
                Is.EqualTo(expected));
            CaptureFingerprintControls(labelWidth, labelFingerprint);
            PcCompatManagedResponsiveImGuiLayout.EndHorizontal(DynamicRowEndToken, width);
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.EndFrame();
        }
    }

    private static void RunDenseResponsiveFrame(
        bool layoutEvent,
        PcCompatImGuiContainerMode expected)
    {
        PcCompatManagedResponsiveImGuiLayout.BeginFrame(
            layoutEvent,
            geometryEvent: !layoutEvent,
            contentWidth: 720f,
            fontScale: 1f,
            touchHeight: 48f);
        try
        {
            RunDenseResponsiveRow(
                beginToken: 9401,
                endToken: 9402,
                firstControlToken: 9410,
                layoutEvent,
                expected);
            RunDenseResponsiveRow(
                beginToken: 9501,
                endToken: 9502,
                firstControlToken: 9510,
                layoutEvent,
                expected);
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.EndFrame();
        }
    }

    private static void RunDenseResponsiveRow(
        int beginToken,
        int endToken,
        int firstControlToken,
        bool layoutEvent,
        PcCompatImGuiContainerMode expected)
    {
        var actualMode = PcCompatManagedResponsiveImGuiLayout.BeginHorizontal(beginToken).Mode;
        if (actualMode != expected)
            throw new InvalidOperationException(
                $"dense responsive row mode mismatch: expected={expected}; actual={actualMode}");
        for (var group = 0; group < 32; ++group)
        {
            var labelToken = firstControlToken + group * 2;
            var inputToken = labelToken + 1;
            var label = layoutEvent
                ? new PcCompatImGuiMeasurement(70f, 70f, SupportsTextWrapping: true)
                : default;
            var input = layoutEvent
                ? new PcCompatImGuiMeasurement(60f, 60f, SupportsTextWrapping: false)
                : default;
            PcCompatManagedResponsiveImGuiLayout.BeforeElement(
                labelToken,
                PcCompatImGuiElementKind.Label,
                label,
                default);
            PcCompatManagedResponsiveImGuiLayout.BeforeElement(
                inputToken,
                PcCompatImGuiElementKind.Input,
                input,
                default);
        }
        PcCompatManagedResponsiveImGuiLayout.EndHorizontal(endToken, 720f);
    }

    private static void RunFingerprintRepaint(
        float width,
        PcCompatImGuiContainerMode expected)
    {
        PcCompatManagedResponsiveImGuiLayout.BeginFrame(false, width, 1f, 48f);
        try
        {
            Assert.That(
                PcCompatManagedResponsiveImGuiLayout.BeginHorizontal(DynamicRowBeginToken).Mode,
                Is.EqualTo(expected));
            PcCompatManagedResponsiveImGuiLayout.BeforeElement(
                DynamicRowFirstLabelToken,
                PcCompatImGuiElementKind.Label,
                default,
                default);
            PcCompatManagedResponsiveImGuiLayout.BeforeElement(
                DynamicRowFirstInputToken,
                PcCompatImGuiElementKind.Input,
                default,
                default);
            PcCompatManagedResponsiveImGuiLayout.BeforeElement(
                DynamicRowSecondLabelToken,
                PcCompatImGuiElementKind.Label,
                default,
                default);
            PcCompatManagedResponsiveImGuiLayout.BeforeElement(
                DynamicRowSecondInputToken,
                PcCompatImGuiElementKind.Input,
                default,
                default);
            PcCompatManagedResponsiveImGuiLayout.EndHorizontal(DynamicRowEndToken, width);
        }
        finally
        {
            PcCompatManagedResponsiveImGuiLayout.EndFrame();
        }
    }

    private static void CaptureFingerprintControls(float labelWidth, int labelFingerprint)
    {
        PcCompatManagedResponsiveImGuiLayout.BeforeElement(
            DynamicRowFirstLabelToken,
            PcCompatImGuiElementKind.Label,
            new PcCompatImGuiMeasurement(
                labelWidth,
                labelWidth,
                SupportsTextWrapping: true,
                LayoutFingerprint: labelFingerprint),
            default);
        PcCompatManagedResponsiveImGuiLayout.BeforeElement(
            DynamicRowFirstInputToken,
            PcCompatImGuiElementKind.Input,
            new PcCompatImGuiMeasurement(60f, 60f, SupportsTextWrapping: false),
            default);
        PcCompatManagedResponsiveImGuiLayout.BeforeElement(
            DynamicRowSecondLabelToken,
            PcCompatImGuiElementKind.Label,
            new PcCompatImGuiMeasurement(
                labelWidth,
                labelWidth,
                SupportsTextWrapping: true,
                LayoutFingerprint: labelFingerprint),
            default);
        PcCompatManagedResponsiveImGuiLayout.BeforeElement(
            DynamicRowSecondInputToken,
            PcCompatImGuiElementKind.Input,
            new PcCompatImGuiMeasurement(60f, 60f, SupportsTextWrapping: false),
            default);
    }

    private static PcCompatImGuiMeasurement Label()
        => new(70f, 70f, SupportsTextWrapping: true);

    private static PcCompatImGuiMeasurement WideLabel()
        => new(120f, 120f, SupportsTextWrapping: true);

    private static PcCompatImGuiMeasurement Input()
        => new(60f, 60f, SupportsTextWrapping: false);

    private static void BindLayoutProbe(
        Type backendType,
        object backend,
        string fieldName,
        string methodName)
    {
        var field = backendType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(backendType.FullName, fieldName);
        var method = typeof(ImGuiTopologyProbe).GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(ImGuiTopologyProbe), methodName);
        field.SetValue(backend, method);
    }

    private static void BindBackendMethod(
        Type backendType,
        object backend,
        string fieldName,
        Type owner,
        string methodName)
    {
        var field = backendType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(backendType.FullName, fieldName);
        var method = owner.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            ?? throw new MissingMethodException(owner.FullName, methodName);
        field.SetValue(backend, method);
    }

    private static class ImGuiTopologyProbe
    {
        public static List<string> Calls { get; } = [];

        public static void Reset() => Calls.Clear();

        public static void BeginHorizontal(object options) => Calls.Add("begin-horizontal");

        public static void EndHorizontal() => Calls.Add("end-horizontal");

        public static void BeginVertical(object options) => Calls.Add("begin-vertical");

        public static void EndVertical() => Calls.Add("end-vertical");
    }

    private sealed class ImGuiGridProbeContent(string text)
    {
        public string Text { get; } = text;
    }

    private sealed class ImGuiGridProbeRect(float x, float y, float width, float height)
    {
        public float X { get; private set; } = x;
        public float Y { get; private set; } = y;
        public float Width { get; private set; } = width;
        public float Height { get; private set; } = height;

        public float get_x() => X;
        public float get_y() => Y;
        public float get_width() => Width;
        public float get_height() => Height;
        public void set_x(float value) => X = value;
        public void set_y(float value) => Y = value;
        public void set_width(float value) => Width = value;
        public void set_height(float value) => Height = value;
    }

    private sealed class ImGuiGridProbeStyle
    {
        public ImGuiGridProbeStyle GetButton() => this;

        public bool GetWordWrap() => false;

        public float GetFixedHeight() => 0f;
    }

    private static class ImGuiGridProbe
    {
        public static int GetRectCalls { get; private set; }
        public static List<PcCompatImGuiRect> ToggleRects { get; } = [];
        private static string? ClickedLabel { get; set; }

        public static void Reset(string? clickedLabel = null)
        {
            GetRectCalls = 0;
            ToggleRects.Clear();
            ClickedLabel = clickedLabel;
        }

        public static ImGuiGridProbeStyle GetSkin() => new();

        public static ImGuiGridProbeRect GetRect(
            ImGuiGridProbeContent content,
            ImGuiGridProbeStyle style,
            object options)
        {
            GetRectCalls++;
            return new ImGuiGridProbeRect(10f, 20f, 308f, 48f);
        }

        public static bool Toggle(
            ImGuiGridProbeRect rect,
            bool value,
            ImGuiGridProbeContent content,
            ImGuiGridProbeStyle style)
        {
            ToggleRects.Add(new PcCompatImGuiRect(rect.X, rect.Y, rect.Width, rect.Height));
            return string.Equals(content.Text, ClickedLabel, StringComparison.Ordinal) || value;
        }
    }
}

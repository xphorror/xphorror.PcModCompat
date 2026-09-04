using System.Collections;
using System.Runtime.CompilerServices;

namespace Xphorror.PcModCompat;

/// <summary>
/// Keeps IMGUI structural changes deterministic across Layout, input and Repaint.
/// The bridge deliberately records only immutable layout intent here; Unity calls
/// still happen in <see cref="PcCompatManagedImGuiBridge"/>.
/// </summary>
internal static class PcCompatManagedResponsiveImGuiLayout
{
    internal const float MinimumTouchExtent = 48f;
    internal const float MinimumLayoutExtent = 1f;
    internal const float SelectionGridCellGap = 8f;
    private const float WidthHysteresis = 8f;
    private const int MaxRowsPerGeneration = 512;
    private const int MaxNodesPerRow = 64;
    private const int MaxDepth = 16;

    private static readonly object Sync = new();
    private static readonly Dictionary<SessionKey, SessionState> Sessions = new(SessionKeyComparer.Instance);
    private static readonly ConditionalWeakTable<object, OptionTag> OptionTags = new();

    [ThreadStatic]
    private static FrameState? t_frame;

    [ThreadStatic]
    private static FrameState? t_reusableFrame;

    internal static bool IsFrameActive => t_frame is not null;

    // Layout is the only event that may produce a new plan. Repaint and input must
    // replay the already-frozen structure without probing GUIStyle through IL2CPP.
    internal static bool RequiresMeasurement => t_frame?.RequiresMeasurement == true;

    internal static void BeginFrame(
        bool layoutEvent,
        float contentWidth,
        float fontScale,
        float touchHeight)
        => BeginFrame(
            layoutEvent,
            geometryEvent: !layoutEvent,
            contentWidth,
            fontScale,
            touchHeight);

    internal static void BeginFrame(
        bool layoutEvent,
        bool geometryEvent,
        float contentWidth,
        float fontScale,
        float touchHeight)
        => BeginFrame(
            layoutEvent,
            geometryEvent,
            contentWidth,
            fontScale,
            touchHeight,
            measurementStyleFingerprint: 0);

    internal static void BeginFrame(
        bool layoutEvent,
        bool geometryEvent,
        float contentWidth,
        float fontScale,
        float touchHeight,
        int measurementStyleFingerprint)
    {
        if (t_frame is not null)
            throw new InvalidOperationException("Nested responsive IMGUI layout frame was rejected.");

        if (!float.IsFinite(contentWidth) || contentWidth <= 0f)
            return;

        var execution = PcCompatManagedExecutionContext.Current;
        var modId = execution?.ModId;
        if (string.IsNullOrWhiteSpace(modId))
            return;

        var key = new SessionKey(modId, execution!.ResourceSessionGeneration);
        SessionState session;
        lock (Sync)
        {
            if (!Sessions.TryGetValue(key, out session!))
            {
                session = new SessionState();
                Sessions.Add(key, session);
            }
        }

        var normalizedWidth = Math.Max(MinimumLayoutExtent, contentWidth);
        var styleFingerprintBuilder = new HashCode();
        styleFingerprintBuilder.Add((int)MathF.Round(Math.Max(0.1f, fontScale) * 100f));
        styleFingerprintBuilder.Add((int)MathF.Round(Math.Max(MinimumTouchExtent, touchHeight) * 10f));
        styleFingerprintBuilder.Add(measurementStyleFingerprint);
        var styleFingerprint = styleFingerprintBuilder.ToHashCode();
        var frame = t_reusableFrame ??= new FrameState();
        frame.Reset(
            session,
            layoutEvent,
            geometryEvent,
            normalizedWidth,
            styleFingerprint);
        t_frame = frame;
    }

    internal static void EndFrame()
    {
        var frame = t_frame;
        t_frame = null;
        frame?.Complete();
    }

    internal static void Retire(string modId, long resourceSessionGeneration)
    {
        if (string.IsNullOrWhiteSpace(modId))
            return;

        var key = new SessionKey(modId, resourceSessionGeneration);
        lock (Sync)
            Sessions.Remove(key);
    }

    internal static void TagOption(object option, PcCompatImGuiOptionKind kind, float value, int callsiteToken)
    {
        ArgumentNullException.ThrowIfNull(option);
        OptionTags.Remove(option);
        OptionTags.Add(option, new OptionTag(kind, value, callsiteToken));
    }

    internal static PcCompatImGuiOptionSnapshot ReadOptions(object options)
    {
        var snapshot = new PcCompatImGuiOptionSnapshot();
        if (options is Array { Rank: 1 } array)
        {
            for (var index = 0; index < array.Length; ++index)
                ApplyOption(ref snapshot, array.GetValue(index));
            return snapshot;
        }

        if (options is IList list)
        {
            for (var index = 0; index < list.Count; ++index)
                ApplyOption(ref snapshot, list[index]);
            return snapshot;
        }

        // Il2CppReferenceArray<T> does not promise the CLR IEnumerable contract on
        // every generated proxy revision. Its Length/indexer shape is stable instead.
        var type = options.GetType();
        var length = type.GetProperty("Length")?.GetValue(options);
        var indexer = type.GetProperty("Item");
        if (length is null || indexer is null)
            return snapshot;
        var count = Convert.ToInt32(length);
        for (var index = 0; index < count; ++index)
            ApplyOption(ref snapshot, indexer.GetValue(options, [index]));
        return snapshot;
    }

    private static void ApplyOption(ref PcCompatImGuiOptionSnapshot snapshot, object? option)
    {
        if (option is null || !OptionTags.TryGetValue(option, out var tag))
            return;

        switch (tag.Kind)
        {
            case PcCompatImGuiOptionKind.Width:
                snapshot = snapshot with { Width = tag.Value };
                break;
            case PcCompatImGuiOptionKind.MinWidth:
                snapshot = snapshot with { MinWidth = tag.Value };
                break;
            case PcCompatImGuiOptionKind.MaxWidth:
                snapshot = snapshot with { MaxWidth = tag.Value };
                break;
            case PcCompatImGuiOptionKind.Height:
                snapshot = snapshot with { Height = tag.Value };
                break;
            case PcCompatImGuiOptionKind.MinHeight:
                snapshot = snapshot with { MinHeight = tag.Value };
                break;
            case PcCompatImGuiOptionKind.MaxHeight:
                snapshot = snapshot with { MaxHeight = tag.Value };
                break;
            case PcCompatImGuiOptionKind.ExpandWidth:
                snapshot = snapshot with { ExpandWidth = tag.Value >= 0.5f };
                break;
            case PcCompatImGuiOptionKind.ExpandHeight:
                snapshot = snapshot with { ExpandHeight = tag.Value >= 0.5f };
                break;
        }
    }

    internal static PcCompatImGuiContainerDecision BeginHorizontal(int callsiteToken)
        => t_frame?.BeginHorizontal(callsiteToken) ?? PcCompatImGuiContainerDecision.PassThrough;

    internal static PcCompatImGuiContainerDecision BeginVertical(int callsiteToken)
        => t_frame?.BeginVertical(callsiteToken) ?? PcCompatImGuiContainerDecision.PassThrough;

    internal static PcCompatImGuiContainerDecision EndHorizontal(
        int callsiteToken,
        float measuredWidth)
        => t_frame?.EndHorizontal(callsiteToken, measuredWidth) ??
           PcCompatImGuiContainerDecision.PassThrough;

    internal static PcCompatImGuiContainerDecision EndVertical(int callsiteToken)
        => t_frame?.EndVertical(callsiteToken) ?? PcCompatImGuiContainerDecision.PassThrough;

    internal static PcCompatImGuiElementDecision BeforeElement(
        int callsiteToken,
        PcCompatImGuiElementKind kind,
        PcCompatImGuiMeasurement measurement,
        PcCompatImGuiOptionSnapshot options)
        => t_frame?.BeforeElement(callsiteToken, kind, measurement, options) ??
           PcCompatImGuiElementDecision.PassThrough;

    internal static PcCompatSelectionGridDecision SelectSelectionGridColumns(
        int callsiteToken,
        int requestedColumns,
        PcCompatSelectionGridMeasurement measurement,
        PcCompatImGuiOptionSnapshot options)
        => t_frame?.SelectSelectionGridColumns(
            callsiteToken,
            requestedColumns,
            measurement,
            options) ?? PcCompatSelectionGridDecision.PassThrough(
                requestedColumns,
                measurement.ItemCount,
                measurement.CellPreferredHeight);

    internal static bool IsSelectionGridHeightConstraint(object? option)
    {
        return option is not null &&
               OptionTags.TryGetValue(option, out var tag) &&
               tag.Kind is PcCompatImGuiOptionKind.Height or PcCompatImGuiOptionKind.MaxHeight;
    }

    internal static bool IsInteractiveHeightConstraintBelow(object? option, float minimumHeight)
    {
        if (!float.IsFinite(minimumHeight) || minimumHeight <= 0f ||
            option is null || !OptionTags.TryGetValue(option, out var tag))
        {
            return false;
        }

        return (tag.Kind is PcCompatImGuiOptionKind.Height or PcCompatImGuiOptionKind.MaxHeight) &&
               float.IsFinite(tag.Value) &&
               tag.Value + 0.5f < minimumHeight;
    }

    private readonly record struct SessionKey(string ModId, long Generation);

    private sealed class SessionKeyComparer : IEqualityComparer<SessionKey>
    {
        public static SessionKeyComparer Instance { get; } = new();

        public bool Equals(SessionKey x, SessionKey y)
            => x.Generation == y.Generation &&
               StringComparer.OrdinalIgnoreCase.Equals(x.ModId, y.ModId);

        public int GetHashCode(SessionKey value)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.ModId),
                value.Generation);
    }

    private readonly record struct LayoutNodeKey(int CallsiteToken, int Occurrence);

    private sealed class SessionState
    {
        private readonly Dictionary<LayoutNodeKey, HorizontalPlan> _plans = new();
        private readonly Dictionary<LayoutNodeKey, HorizontalPlan> _pendingPlans = new();
        private readonly Dictionary<LayoutNodeKey, FrozenPlan> _frozenPlans = new();
        private readonly Dictionary<LayoutNodeKey, LinkedListNode<LayoutNodeKey>> _rowNodes = new();
        private readonly LinkedList<LayoutNodeKey> _rowOrder = new();
        private readonly Dictionary<LayoutNodeKey, float> _observedWidths = new();
        private readonly Dictionary<LayoutNodeKey, SelectionGridPlan> _selectionGridPlans = new();
        private readonly Dictionary<LayoutNodeKey, SelectionGridPlan> _pendingSelectionGridPlans = new();
        private readonly Dictionary<LayoutNodeKey, FrozenSelectionGridPlan> _frozenSelectionGridPlans = new();

        public PlanSelection SelectPlan(
            LayoutNodeKey nodeKey,
            float availableWidth,
            int styleFingerprint,
            bool layoutEvent)
        {
            availableWidth = NormalizeAvailableWidth(availableWidth);
            if (!layoutEvent)
            {
                // Unity assigns control IDs from the Layout structure. A Repaint, mouse or
                // keyboard event must never select a newly inferred shape, even when a screen
                // resize becomes visible between events. The next Layout owns that transition.
                return _frozenPlans.TryGetValue(nodeKey, out var frozen)
                    ? frozen.Selection
                    : PlanSelection.PreserveNative(availableWidth);
            }

            TouchRow(nodeKey);
            if (_pendingPlans.Remove(nodeKey, out var pending) &&
                pending.StyleFingerprint == styleFingerprint)
            {
                StoreCommitted(pending);
            }

            if (!_plans.TryGetValue(nodeKey, out var plan) ||
                plan.StyleFingerprint != styleFingerprint)
            {
                // An unknown third-party row must keep its original topology while
                // this Layout captures enough information for the next transaction.
                // The previous "safe stacked" default changed every arbitrary JRP
                // horizontal group into a vertical group and visibly broke its UI.
                var native = PlanSelection.PreserveNative(availableWidth);
                _frozenPlans[nodeKey] = new FrozenPlan(native);
                return native;
            }

            // A previously committed plan may be used only if it remains safe at this Layout
            // boundary. A new compact plan is still staged from this Layout and becomes visible
            // at the next one, so a transaction never changes shape halfway through.
            var selected = plan.Mode switch
            {
                PcCompatImGuiContainerMode.Horizontal when
                    availableWidth + 0.5f >= plan.RequiredWidth =>
                    new PlanSelection(PcCompatImGuiContainerMode.Horizontal, plan, availableWidth),
                PcCompatImGuiContainerMode.Rows when
                    availableWidth + 0.5f >= plan.MaximumRowWidth =>
                    new PlanSelection(
                        PcCompatImGuiContainerMode.Rows,
                        plan,
                        availableWidth,
                        PreserveRowsUntilHysteresis: true),
                _ => PlanSelection.PreserveNative(availableWidth)
            };
            _frozenPlans[nodeKey] = new FrozenPlan(selected);
            return selected;
        }

        public float ResolveAvailableWidth(
            LayoutNodeKey nodeKey,
            float parentAvailableWidth,
            bool constrainToObservedWidth)
        {
            var fallback = NormalizeAvailableWidth(parentAvailableWidth);
            if (!constrainToObservedWidth)
                return fallback;
            var observed = _observedWidths.GetValueOrDefault(nodeKey);
            return float.IsFinite(observed) && observed > 0f
                ? Math.Min(observed, fallback)
                : fallback;
        }

        public float ObserveWidth(
            LayoutNodeKey nodeKey,
            float measuredWidth,
            float fallbackWidth,
            bool acceptObservation,
            bool constrainToObservedWidth)
        {
            var fallback = NormalizeAvailableWidth(fallbackWidth);
            var resolved = ResolveAvailableWidth(
                nodeKey,
                fallback,
                constrainToObservedWidth);
            // GUILayout only assigns final group rects after the Layout pass. Reading the
            // rect while processing Layout can yield its dummy/pre-layout value and poison
            // the following plan, so only Repaint/input observations are authoritative.
            if (acceptObservation && float.IsFinite(measuredWidth) && measuredWidth > 0f)
            {
                resolved = Math.Min(measuredWidth, fallback);
                _observedWidths[nodeKey] = resolved;
            }
            return resolved;
        }

        public void Stage(
            LayoutNodeKey nodeKey,
            float availableWidth,
            int styleFingerprint,
            IReadOnlyList<CapturedElement> captured,
            bool preserveRowsUntilHysteresis)
        {
            TouchRow(nodeKey);
            if (_pendingPlans.TryGetValue(nodeKey, out var pending) &&
                pending.HasSameCapture(
                    availableWidth,
                    styleFingerprint,
                    captured,
                    preserveRowsUntilHysteresis))
            {
                return;
            }

            if (_plans.TryGetValue(nodeKey, out var committed) &&
                committed.HasSameCapture(
                    availableWidth,
                    styleFingerprint,
                    captured,
                    preserveRowsUntilHysteresis))
            {
                return;
            }

            _pendingPlans[nodeKey] = HorizontalPlan.Create(
                nodeKey,
                availableWidth,
                styleFingerprint,
                captured,
                preserveRowsUntilHysteresis);
        }

        public PcCompatSelectionGridDecision SelectSelectionGridColumns(
            LayoutNodeKey nodeKey,
            float availableWidth,
            int styleFingerprint,
            int requestedColumns,
            PcCompatSelectionGridMeasurement measurement,
            PcCompatImGuiOptionSnapshot options,
            bool layoutEvent)
        {
            if (measurement.ItemCount <= 0)
                return PcCompatSelectionGridDecision.PassThrough(
                    requestedColumns,
                    measurement.ItemCount,
                    measurement.CellPreferredHeight);

            availableWidth = NormalizeAvailableWidth(availableWidth);
            var gridAvailableWidth = SelectionGridPlan.ResolveAvailableWidth(availableWidth, options);
            var desiredColumns = Math.Clamp(requestedColumns, 1, measurement.ItemCount);
            if (!layoutEvent)
            {
                return _frozenSelectionGridPlans.TryGetValue(nodeKey, out var frozen)
                    ? frozen.Decision
                    : PcCompatSelectionGridDecision.PassThrough(
                        desiredColumns,
                        measurement.ItemCount,
                        measurement.CellPreferredHeight);
            }

            var fingerprint = SelectionGridPlan.ComputeFingerprint(
                styleFingerprint,
                requestedColumns,
                measurement,
                options);
            TouchRow(nodeKey);
            if (_pendingSelectionGridPlans.Remove(nodeKey, out var pending) &&
                pending.Fingerprint == fingerprint)
            {
                _selectionGridPlans[nodeKey] = pending;
            }

            var decision = _selectionGridPlans.TryGetValue(nodeKey, out var plan) &&
                           plan.CanReuse(fingerprint, gridAvailableWidth)
                ? plan.Decision
                : PcCompatSelectionGridDecision.PassThrough(
                    desiredColumns,
                    measurement.ItemCount,
                    measurement.CellPreferredHeight);
            _frozenSelectionGridPlans[nodeKey] = new FrozenSelectionGridPlan(decision);
            _pendingSelectionGridPlans[nodeKey] = SelectionGridPlan.Create(
                fingerprint,
                gridAvailableWidth,
                requestedColumns,
                measurement);
            return decision;
        }

        private void StoreCommitted(HorizontalPlan plan)
        {
            TouchRow(plan.NodeKey);
            _plans[plan.NodeKey] = plan;
        }

        private void TouchRow(LayoutNodeKey nodeKey)
        {
            if (_rowNodes.TryGetValue(nodeKey, out var node))
            {
                _rowOrder.Remove(node);
                _rowOrder.AddLast(node);
                return;
            }

            node = _rowOrder.AddLast(nodeKey);
            _rowNodes.Add(nodeKey, node);
            while (_rowOrder.Count > MaxRowsPerGeneration)
            {
                var retired = _rowOrder.First!;
                _rowOrder.RemoveFirst();
                _rowNodes.Remove(retired.Value);
                _plans.Remove(retired.Value);
                _pendingPlans.Remove(retired.Value);
                _frozenPlans.Remove(retired.Value);
                _observedWidths.Remove(retired.Value);
                _selectionGridPlans.Remove(retired.Value);
                _pendingSelectionGridPlans.Remove(retired.Value);
                _frozenSelectionGridPlans.Remove(retired.Value);
            }
        }

        private static float NormalizeAvailableWidth(float width)
            => float.IsFinite(width) && width > 0f ? width : MinimumLayoutExtent;
    }

    private readonly record struct FrozenPlan(PlanSelection Selection);

    private readonly record struct FrozenSelectionGridPlan(PcCompatSelectionGridDecision Decision);

    private readonly record struct PlanSelection(
        PcCompatImGuiContainerMode Mode,
        HorizontalPlan? Plan,
        float AvailableWidth,
        bool PreserveRowsUntilHysteresis = false)
    {
        public static PlanSelection PreserveNative(float availableWidth)
            => new(
                PcCompatImGuiContainerMode.PassThrough,
                null,
                availableWidth);
    }

    private sealed class FrameState
    {
        private SessionState? _session;
        private bool _layoutEvent;
        private bool _geometryEvent;
        private float _contentWidth;
        private int _styleFingerprint;
        private readonly Stack<ContainerState> _containers = new();
        private readonly Stack<ContainerState> _recycledContainers = new();
        private readonly Dictionary<int, int> _callsiteOccurrences = new();

        public void Reset(
            SessionState session,
            bool layoutEvent,
            bool geometryEvent,
            float contentWidth,
            int styleFingerprint)
        {
            while (_containers.TryPop(out var orphaned))
                Recycle(orphaned);
            _callsiteOccurrences.Clear();
            _session = session;
            _layoutEvent = layoutEvent;
            _geometryEvent = geometryEvent;
            _contentWidth = contentWidth;
            _styleFingerprint = styleFingerprint;
        }

        public PcCompatImGuiContainerDecision BeginHorizontal(int callsiteToken)
        {
            var nodeKey = NextNodeKey(callsiteToken);
            var nested = _containers.Count != 0;
            var parentAvailableWidth = GetParentAvailableWidth();
            var availableWidth = RequireSession().ResolveAvailableWidth(
                nodeKey,
                parentAvailableWidth,
                constrainToObservedWidth: nested);
            var placement = PlaceInParent(
                callsiteToken,
                PcCompatImGuiElementKind.HorizontalContainer,
                PcCompatImGuiMeasurement.Container(availableWidth),
                default);
            if (_containers.Count >= MaxDepth)
            {
                var overflow = AcquireContainer();
                overflow.ConfigurePassive(
                    callsiteToken,
                    nodeKey,
                    isHorizontal: true,
                    availableWidth,
                    nested);
                _containers.Push(overflow);
                return new PcCompatImGuiContainerDecision(
                    PcCompatImGuiContainerMode.Stacked,
                    placement.StartNewRow);
            }

            var selection = RequireSession().SelectPlan(
                nodeKey,
                availableWidth,
                _styleFingerprint,
                _layoutEvent);
            var container = AcquireContainer();
            container.ConfigureResponsive(
                callsiteToken,
                nodeKey,
                selection,
                _layoutEvent,
                _geometryEvent,
                nested,
                _styleFingerprint);
            _containers.Push(container);
            return new PcCompatImGuiContainerDecision(selection.Mode, placement.StartNewRow);
        }

        public PcCompatImGuiContainerDecision BeginVertical(int callsiteToken)
        {
            var nodeKey = NextNodeKey(callsiteToken);
            var nested = _containers.Count != 0;
            var availableWidth = GetParentAvailableWidth();
            var placement = PlaceInParent(
                callsiteToken,
                PcCompatImGuiElementKind.VerticalContainer,
                PcCompatImGuiMeasurement.Container(availableWidth),
                default);
            var container = AcquireContainer();
            container.ConfigurePassive(
                callsiteToken,
                nodeKey,
                isHorizontal: false,
                availableWidth,
                nested);
            _containers.Push(container);
            return new PcCompatImGuiContainerDecision(
                PcCompatImGuiContainerMode.PassThrough,
                placement.StartNewRow);
        }

        public PcCompatImGuiContainerDecision EndHorizontal(int callsiteToken, float measuredWidth)
        {
            if (_containers.Count == 0)
                return PcCompatImGuiContainerDecision.PassThrough;

            var container = _containers.Pop();
            try
            {
                if (!container.IsHorizontal)
                    return PcCompatImGuiContainerDecision.PassThrough;

                // Begin/End are distinct IL call sites, so their stable tokens are intentionally
                // different. The begin token owns the cached row; the end token only closes it.
                var availableWidth = RequireSession().ObserveWidth(
                    container.NodeKey,
                    measuredWidth,
                    container.AvailableWidth,
                    acceptObservation: container.GeometryEvent,
                    constrainToObservedWidth: container.IsNested);
                if (container.Responsive && container.LayoutEvent)
                {
                    RequireSession().Stage(
                        container.NodeKey,
                        availableWidth,
                        container.StyleFingerprint,
                        container.Captured,
                        container.PreserveRowsUntilHysteresis);
                }
                return new PcCompatImGuiContainerDecision(container.Mode, false);
            }
            finally
            {
                Recycle(container);
            }
        }

        public PcCompatImGuiContainerDecision EndVertical(int callsiteToken)
        {
            if (_containers.Count == 0)
                return PcCompatImGuiContainerDecision.PassThrough;
            var container = _containers.Pop();
            try
            {
                return !container.IsHorizontal
                    ? PcCompatImGuiContainerDecision.PassThrough
                    : new PcCompatImGuiContainerDecision(PcCompatImGuiContainerMode.Stacked, false);
            }
            finally
            {
                Recycle(container);
            }
        }

        public PcCompatImGuiElementDecision BeforeElement(
            int callsiteToken,
            PcCompatImGuiElementKind kind,
            PcCompatImGuiMeasurement measurement,
            PcCompatImGuiOptionSnapshot options)
            => PlaceInParent(callsiteToken, kind, measurement, options);

        public PcCompatSelectionGridDecision SelectSelectionGridColumns(
            int callsiteToken,
            int requestedColumns,
            PcCompatSelectionGridMeasurement measurement,
            PcCompatImGuiOptionSnapshot options)
        {
            var nodeKey = NextNodeKey(callsiteToken);
            return RequireSession().SelectSelectionGridColumns(
                nodeKey,
                GetParentAvailableWidth(),
                _styleFingerprint,
                requestedColumns,
                measurement,
                options,
                _layoutEvent);
        }

        public void Complete()
        {
            // A malformed third-party OnGUI body should not retain a partial plan or
            // leak a structure stack into the next event.
            while (_containers.TryPop(out var container))
                Recycle(container);
            _callsiteOccurrences.Clear();
            _session = null;
        }

        public bool LayoutEvent => _layoutEvent;

        public bool RequiresMeasurement
            => _layoutEvent &&
               _containers.TryPeek(out var current) &&
               current.Responsive;

        private PcCompatImGuiElementDecision PlaceInParent(
            int callsiteToken,
            PcCompatImGuiElementKind kind,
            PcCompatImGuiMeasurement measurement,
            PcCompatImGuiOptionSnapshot options)
        {
            if (_containers.Count == 0)
                return PcCompatImGuiElementDecision.PassThrough;

            var parent = _containers.Peek();
            if (!parent.Responsive)
                return PcCompatImGuiElementDecision.PassThrough;

            return parent.Place(callsiteToken, kind, measurement, options);
        }

        private float GetParentAvailableWidth()
            => _containers.Count == 0
                ? _contentWidth
                : _containers.Peek().AvailableWidth;

        private LayoutNodeKey NextNodeKey(int callsiteToken)
        {
            var occurrence = _callsiteOccurrences.GetValueOrDefault(callsiteToken);
            _callsiteOccurrences[callsiteToken] = checked(occurrence + 1);
            return new LayoutNodeKey(callsiteToken, occurrence);
        }

        private SessionState RequireSession()
            => _session ?? throw new InvalidOperationException(
                "Responsive IMGUI layout frame has no active session.");

        private ContainerState AcquireContainer()
            => _recycledContainers.TryPop(out var container)
                ? container
                : new ContainerState();

        private void Recycle(ContainerState container)
        {
            container.Release();
            _recycledContainers.Push(container);
        }
    }

    private sealed class ContainerState
    {
        private HorizontalPlan? _plan;
        private int _ordinal;
        private List<CapturedElement>? _captured;

        public int CallsiteToken { get; private set; }
        public LayoutNodeKey NodeKey { get; private set; }
        public bool IsHorizontal { get; private set; }
        public bool Responsive { get; private set; }
        public PcCompatImGuiContainerMode Mode { get; private set; }
        public bool LayoutEvent { get; private set; }
        public bool GeometryEvent { get; private set; }
        public float AvailableWidth { get; private set; }
        public bool IsNested { get; private set; }
        public bool PreserveRowsUntilHysteresis { get; private set; }
        public int StyleFingerprint { get; private set; }
        public bool PlanInvalidated { get; private set; }
        public IReadOnlyList<CapturedElement> Captured
            => _captured is { } captured ? captured : Array.Empty<CapturedElement>();

        public void ConfigurePassive(
            int callsiteToken,
            LayoutNodeKey nodeKey,
            bool isHorizontal,
            float availableWidth,
            bool isNested)
            => Configure(
                callsiteToken,
                nodeKey,
                isHorizontal,
                responsive: false,
                PcCompatImGuiContainerMode.PassThrough,
                null,
                layoutEvent: false,
                geometryEvent: false,
                availableWidth,
                isNested,
                preserveRowsUntilHysteresis: false,
                styleFingerprint: 0);

        public void ConfigureResponsive(
            int callsiteToken,
            LayoutNodeKey nodeKey,
            PlanSelection selection,
            bool layoutEvent,
            bool geometryEvent,
            bool isNested,
            int styleFingerprint)
            => Configure(
                callsiteToken,
                nodeKey,
                isHorizontal: true,
                responsive: true,
                selection.Mode,
                selection.Plan,
                layoutEvent,
                geometryEvent,
                selection.AvailableWidth,
                isNested,
                selection.PreserveRowsUntilHysteresis,
                styleFingerprint);

        public void Release()
        {
            _plan = null;
            _ordinal = 0;
            _captured?.Clear();
            CallsiteToken = 0;
            NodeKey = default;
            IsHorizontal = false;
            Responsive = false;
            Mode = PcCompatImGuiContainerMode.PassThrough;
            LayoutEvent = false;
            GeometryEvent = false;
            AvailableWidth = 0f;
            IsNested = false;
            PreserveRowsUntilHysteresis = false;
            StyleFingerprint = 0;
            PlanInvalidated = false;
        }

        public PcCompatImGuiElementDecision Place(
            int callsiteToken,
            PcCompatImGuiElementKind kind,
            PcCompatImGuiMeasurement measurement,
            PcCompatImGuiOptionSnapshot options)
        {
            var ordinal = _ordinal++;
            var effectiveMeasurement = measurement.With(options);
            if (LayoutEvent)
            {
                var captured = _captured ??= new List<CapturedElement>();
                // Keep one sentinel beyond the documented bound so the planner can
                // distinguish an exactly-full, valid row from a truncated row.
                if (captured.Count <= MaxNodesPerRow)
                    captured.Add(new CapturedElement(callsiteToken, kind, effectiveMeasurement));
            }

            // A row that has not been proven safe to reshape must retain the MOD's
            // own word-wrap setting. Treating "no responsive decision" as
            // "force single line" was the source of clipped labels in nested,
            // otherwise native layouts.
            if (_plan is null || Mode == PcCompatImGuiContainerMode.PassThrough)
                return PcCompatImGuiElementDecision.PassThrough;

            if (!_plan.Matches(ordinal, callsiteToken, kind))
                return PcCompatImGuiElementDecision.PassThrough;

            var plannedElement = _plan.Elements[ordinal];
            // Dynamic text and mutable GUIStyle instances can change after a plan has
            // been committed. The current Layout retains the MOD's original text
            // policy while it stages a replacement; Repaint/input stay frozen.
            if (LayoutEvent &&
                (PlanInvalidated ||
                 plannedElement.Measurement.LayoutFingerprint != effectiveMeasurement.LayoutFingerprint))
            {
                PlanInvalidated = true;
                return PcCompatImGuiElementDecision.PassThrough;
            }

            var wrapText = ResolveWrap(plannedElement.Measurement, options);
            return new PcCompatImGuiElementDecision(
                StartNewRow: Mode == PcCompatImGuiContainerMode.Rows &&
                             _plan.BreakBefore[ordinal],
                // A compact horizontal plan is an explicit proof that each
                // element can remain on one line. Segmented and stacked plans
                // may instead allow a text element constrained by its own
                // width option to grow vertically.
                WrapText: Mode is PcCompatImGuiContainerMode.Rows or
                    PcCompatImGuiContainerMode.Stacked
                    ? wrapText
                    : false);
        }

        private bool ResolveWrap(
            PcCompatImGuiMeasurement measurement,
            PcCompatImGuiOptionSnapshot options)
        {
            if (!measurement.SupportsTextWrapping)
                return false;
            var available = options.Width ?? options.MaxWidth ?? AvailableWidth;
            if (!float.IsFinite(available) || available <= 0f)
                return true;
            return measurement.PreferredWidth > available + 0.5f;
        }

        private void Configure(
            int callsiteToken,
            LayoutNodeKey nodeKey,
            bool isHorizontal,
            bool responsive,
            PcCompatImGuiContainerMode mode,
            HorizontalPlan? plan,
            bool layoutEvent,
            bool geometryEvent,
            float availableWidth,
            bool isNested,
            bool preserveRowsUntilHysteresis,
            int styleFingerprint)
        {
            CallsiteToken = callsiteToken;
            NodeKey = nodeKey;
            IsHorizontal = isHorizontal;
            Responsive = responsive;
            Mode = mode;
            _plan = plan;
            _ordinal = 0;
            _captured?.Clear();
            LayoutEvent = layoutEvent;
            GeometryEvent = geometryEvent;
            AvailableWidth = availableWidth;
            IsNested = isNested;
            PreserveRowsUntilHysteresis = preserveRowsUntilHysteresis;
            StyleFingerprint = styleFingerprint;
            PlanInvalidated = false;
        }
    }

    private sealed class HorizontalPlan
    {
        private HorizontalPlan(
            LayoutNodeKey nodeKey,
            float availableWidth,
            float requiredWidth,
            float maximumRowWidth,
            int styleFingerprint,
            PcCompatImGuiContainerMode mode,
            IReadOnlyList<CapturedElement> elements,
            bool[] breakBefore,
            bool preserveRowsUntilHysteresis)
        {
            NodeKey = nodeKey;
            AvailableWidth = availableWidth;
            RequiredWidth = requiredWidth;
            MaximumRowWidth = maximumRowWidth;
            StyleFingerprint = styleFingerprint;
            Mode = mode;
            Elements = elements;
            BreakBefore = breakBefore;
            _preserveRowsUntilHysteresis = preserveRowsUntilHysteresis;
        }

        public LayoutNodeKey NodeKey { get; }
        public float AvailableWidth { get; }
        public float RequiredWidth { get; }
        public float MaximumRowWidth { get; }
        public int StyleFingerprint { get; }
        public PcCompatImGuiContainerMode Mode { get; }
        public IReadOnlyList<CapturedElement> Elements { get; }
        public bool[] BreakBefore { get; }
        private readonly bool _preserveRowsUntilHysteresis;

        public bool Matches(int ordinal, int callsiteToken, PcCompatImGuiElementKind kind)
            => ordinal >= 0 && ordinal < Elements.Count &&
               Elements[ordinal].CallsiteToken == callsiteToken &&
               Elements[ordinal].Kind == kind;

        public bool HasSameCapture(
            float availableWidth,
            int styleFingerprint,
            IReadOnlyList<CapturedElement> captured,
            bool preserveRowsUntilHysteresis)
        {
            if (StyleFingerprint != styleFingerprint ||
                MathF.Abs(AvailableWidth - availableWidth) > 0.5f ||
                _preserveRowsUntilHysteresis != preserveRowsUntilHysteresis ||
                Elements.Count != captured.Count)
            {
                return false;
            }

            for (var index = 0; index < Elements.Count; ++index)
            {
                if (Elements[index] != captured[index])
                    return false;
            }
            return true;
        }

        public static HorizontalPlan Create(
            LayoutNodeKey nodeKey,
            float availableWidth,
            int styleFingerprint,
            IReadOnlyList<CapturedElement> captured,
            bool preserveRowsUntilHysteresis)
        {
            var truncated = captured.Count > MaxNodesPerRow;
            var elements = captured.Take(MaxNodesPerRow).ToArray();
            var breaks = new bool[elements.Length];
            if (elements.Length == 0 || truncated ||
                !float.IsFinite(availableWidth) || availableWidth < MinimumLayoutExtent)
            {
                return PreserveNative(
                    nodeKey,
                    availableWidth,
                    styleFingerprint,
                    elements,
                    preserveRowsUntilHysteresis,
                    breaks);
            }

            var groups = BuildSemanticGroups(elements);
            if (!groups.Recognized)
            {
                return PreserveNative(
                    nodeKey,
                    availableWidth,
                    styleFingerprint,
                    elements,
                    preserveRowsUntilHysteresis,
                    breaks);
            }

            var requiredWidth = groups.Groups.Sum(group => group.PreferredWidth);
            var used = 0f;
            var maximumRowWidth = 0f;
            var needsBreak = false;
            foreach (var group in groups.Groups)
            {
                if (group.PreferredWidth > availableWidth + 0.5f)
                {
                    return new HorizontalPlan(
                        nodeKey,
                        availableWidth,
                        requiredWidth,
                        float.PositiveInfinity,
                        styleFingerprint,
                        PcCompatImGuiContainerMode.Stacked,
                        elements,
                        breaks,
                        preserveRowsUntilHysteresis);
                }

                if (used > 0f && used + group.PreferredWidth > availableWidth + 0.5f)
                {
                    breaks[group.Start] = true;
                    maximumRowWidth = Math.Max(maximumRowWidth, used);
                    used = 0f;
                    needsBreak = true;
                }
                used += group.PreferredWidth;
            }
            maximumRowWidth = Math.Max(maximumRowWidth, used);

            // Once a row was segmented, restoring its direct horizontal structure needs
            // an additional margin. This keeps tiny changes in DPI/font metrics from
            // oscillating Layout plans on consecutive frames.
            if (!needsBreak && preserveRowsUntilHysteresis &&
                availableWidth < requiredWidth + WidthHysteresis)
            {
                needsBreak = true;
            }

            return new HorizontalPlan(
                nodeKey,
                availableWidth,
                requiredWidth,
                maximumRowWidth,
                styleFingerprint,
                needsBreak
                    ? PcCompatImGuiContainerMode.Rows
                    : PcCompatImGuiContainerMode.Horizontal,
                elements,
                breaks,
                preserveRowsUntilHysteresis);
        }

        private static HorizontalPlan PreserveNative(
            LayoutNodeKey nodeKey,
            float availableWidth,
            int styleFingerprint,
            IReadOnlyList<CapturedElement> elements,
            bool preserveRowsUntilHysteresis,
            bool[] breaks)
            => new(
                nodeKey,
                availableWidth,
                float.PositiveInfinity,
                float.PositiveInfinity,
                styleFingerprint,
                PcCompatImGuiContainerMode.PassThrough,
                elements,
                breaks,
                preserveRowsUntilHysteresis);

        private static (IReadOnlyList<SemanticGroup> Groups, bool Recognized) BuildSemanticGroups(
            IReadOnlyList<CapturedElement> elements)
        {
            var groups = new List<SemanticGroup>();
            var recognized = false;
            var index = 0;
            while (index < elements.Count)
            {
                var start = index;
                while (index < elements.Count && elements[index].Kind is PcCompatImGuiElementKind.Space or PcCompatImGuiElementKind.FlexibleSpace)
                    ++index;
                if (index >= elements.Count)
                {
                    groups.Add(Group(elements, start, elements.Count));
                    break;
                }

                var head = elements[index];
                if (head.Kind is PcCompatImGuiElementKind.HorizontalContainer or
                    PcCompatImGuiElementKind.VerticalContainer)
                {
                    // Do not flatten a nested group into its parent's individual controls.
                    // Its initial width is intentionally the full parent budget until a
                    // final rect is observed, so the outer row cannot safely infer whether
                    // it may change topology. The nested group receives its own plan.
                    groups.Add(Group(elements, start, index + 1));
                    ++index;
                    continue;
                }

                if (head.Kind == PcCompatImGuiElementKind.Label)
                {
                    var end = index + 1;
                    var foundConsumer = false;
                    while (end < elements.Count &&
                           elements[end].Kind is PcCompatImGuiElementKind.Slider or
                               PcCompatImGuiElementKind.Input or
                               PcCompatImGuiElementKind.Toggle or
                               PcCompatImGuiElementKind.Selection)
                    {
                        foundConsumer = true;
                        ++end;
                    }
                    if (foundConsumer)
                    {
                        groups.Add(Group(elements, start, end));
                        recognized = true;
                        index = end;
                        continue;
                    }
                }

                if (IsCompact(elements[index]))
                {
                    var end = index + 1;
                    while (end < elements.Count && IsCompact(elements[end]))
                        ++end;
                    if (end - index >= 2)
                    {
                        groups.Add(Group(elements, start, end));
                        recognized = true;
                        index = end;
                        continue;
                    }
                }

                groups.Add(Group(elements, start, index + 1));
                index++;
            }
            return (groups, recognized);
        }

        private static bool IsCompact(CapturedElement element)
            => (element.Kind is PcCompatImGuiElementKind.Icon or PcCompatImGuiElementKind.Button) &&
               element.Measurement.PreferredWidth <= MinimumTouchExtent + 0.5f;

        private static SemanticGroup Group(
            IReadOnlyList<CapturedElement> elements,
            int start,
            int end)
        {
            var width = 0f;
            for (var index = start; index < end; ++index)
                width += Math.Max(0f, elements[index].Measurement.PreferredWidth);
            return new SemanticGroup(start, width);
        }
    }

    private sealed class SelectionGridPlan
    {
        private SelectionGridPlan(
            int fingerprint,
            float requiredWidth,
            PcCompatSelectionGridDecision decision)
        {
            Fingerprint = fingerprint;
            RequiredWidth = requiredWidth;
            Decision = decision;
        }

        public int Fingerprint { get; }
        public float RequiredWidth { get; }
        public PcCompatSelectionGridDecision Decision { get; }

        public bool CanReuse(int fingerprint, float availableWidth)
            => Fingerprint == fingerprint && RequiredWidth <= availableWidth + 0.5f;

        public static SelectionGridPlan Create(
            int fingerprint,
            float availableWidth,
            int requestedColumns,
            PcCompatSelectionGridMeasurement measurement)
        {
            var desiredColumns = Math.Clamp(requestedColumns, 1, measurement.ItemCount);
            var decision = CreateDecision(
                availableWidth,
                desiredColumns,
                measurement);
            return new SelectionGridPlan(
                fingerprint,
                RequiredWidthFor(decision.Columns, measurement.CellMinimumWidth),
                decision);
        }

        public static float ResolveAvailableWidth(
            float availableWidth,
            PcCompatImGuiOptionSnapshot options)
        {
            var resolved = availableWidth;
            if (options.Width is { } width && float.IsFinite(width) && width > 0f)
                resolved = Math.Min(resolved, width);
            if (options.MaxWidth is { } maximum && float.IsFinite(maximum) && maximum > 0f)
                resolved = Math.Min(resolved, maximum);
            return Math.Max(MinimumLayoutExtent, resolved);
        }

        public static int ComputeFingerprint(
            int styleFingerprint,
            int requestedColumns,
            PcCompatSelectionGridMeasurement measurement,
            PcCompatImGuiOptionSnapshot options)
        {
            var hash = new HashCode();
            hash.Add(styleFingerprint);
            hash.Add(requestedColumns);
            hash.Add(measurement.ItemCount);
            hash.Add(measurement.LayoutFingerprint);
            AddQuantized(ref hash, measurement.CellMinimumWidth);
            AddQuantized(ref hash, measurement.CellPreferredWidth);
            AddQuantized(ref hash, measurement.CellPreferredHeight);
            AddQuantized(ref hash, options.Width);
            AddQuantized(ref hash, options.MinWidth);
            AddQuantized(ref hash, options.MaxWidth);
            AddQuantized(ref hash, options.Height);
            AddQuantized(ref hash, options.MinHeight);
            AddQuantized(ref hash, options.MaxHeight);
            hash.Add(options.ExpandWidth);
            hash.Add(options.ExpandHeight);
            return hash.ToHashCode();
        }

        private static void AddQuantized(ref HashCode hash, float? value)
        {
            hash.Add(value.HasValue);
            if (value is { } finite)
                AddQuantized(ref hash, finite);
        }

        private static void AddQuantized(ref HashCode hash, float value)
        {
            if (!float.IsFinite(value))
            {
                hash.Add(int.MinValue);
                return;
            }

            var bounded = Math.Clamp(value, -1_000_000f, 1_000_000f);
            hash.Add((int)MathF.Round(bounded * 2f));
        }

        private static PcCompatSelectionGridDecision CreateDecision(
            float availableWidth,
            int desiredColumns,
            PcCompatSelectionGridMeasurement measurement)
        {
            var cellMinimumWidth = Math.Max(
                MinimumLayoutExtent,
                measurement.CellMinimumWidth);
            var fittingColumns = Math.Max(
                1,
                (int)MathF.Floor((availableWidth + SelectionGridCellGap) /
                                 (cellMinimumWidth + SelectionGridCellGap)));
            var columns = Math.Min(desiredColumns, fittingColumns);
            var reservedGaps = SelectionGridCellGap * Math.Max(0, columns - 1);
            var cellWidth = Math.Max(
                MinimumLayoutExtent,
                (availableWidth - reservedGaps) / columns);
            var wrapLabels = measurement.CellPreferredWidth > cellWidth + 0.5f;
            var estimatedLines = wrapLabels
                ? Math.Clamp(
                    (int)MathF.Ceiling(measurement.CellPreferredWidth / cellWidth),
                    1,
                    4)
                : 1;
            var cellHeight = Math.Max(
                MinimumLayoutExtent,
                measurement.CellPreferredHeight) * estimatedLines;
            return new PcCompatSelectionGridDecision(
                columns,
                wrapLabels,
                cellWidth,
                cellHeight,
                OverrideHeightConstraints: columns < desiredColumns || wrapLabels);
        }

        private static float RequiredWidthFor(int columns, float cellMinimumWidth)
        {
            var width = Math.Max(MinimumLayoutExtent, cellMinimumWidth) * columns +
                        SelectionGridCellGap * Math.Max(0, columns - 1);
            return float.IsFinite(width) ? width : float.MaxValue;
        }
    }

    private sealed record OptionTag(
        PcCompatImGuiOptionKind Kind,
        float Value,
        int CallsiteToken);

    private readonly record struct CapturedElement(
        int CallsiteToken,
        PcCompatImGuiElementKind Kind,
        PcCompatImGuiMeasurement Measurement);

    private readonly record struct SemanticGroup(int Start, float PreferredWidth);
}

internal enum PcCompatImGuiContainerMode
{
    PassThrough,
    Horizontal,
    Rows,
    Stacked
}

internal enum PcCompatImGuiElementKind
{
    Label,
    Button,
    Toggle,
    Input,
    Slider,
    Icon,
    Space,
    FlexibleSpace,
    HorizontalContainer,
    VerticalContainer,
    Selection
}

public enum PcCompatImGuiOptionKind
{
    Width,
    MinWidth,
    MaxWidth,
    Height,
    MinHeight,
    MaxHeight,
    ExpandWidth,
    ExpandHeight
}

internal readonly record struct PcCompatImGuiContainerDecision(
    PcCompatImGuiContainerMode Mode,
    bool StartNewRow)
{
    public static PcCompatImGuiContainerDecision PassThrough { get; } =
        new(PcCompatImGuiContainerMode.PassThrough, false);
}

internal readonly record struct PcCompatImGuiElementDecision(
    bool StartNewRow,
    bool? WrapText)
{
    public static PcCompatImGuiElementDecision PassThrough { get; } = new(false, null);
}

internal readonly record struct PcCompatSelectionGridDecision(
    int Columns,
    bool WrapLabels,
    float CellWidth,
    float CellHeight,
    bool OverrideHeightConstraints)
{
    public static PcCompatSelectionGridDecision PassThrough(
        int requestedColumns,
        int itemCount,
        float cellHeight = 0f)
    {
        var maximum = Math.Max(1, itemCount);
        return new(
            Math.Clamp(requestedColumns, 1, maximum),
            false,
            0f,
            Math.Max(0f, cellHeight),
            false);
    }
}

internal readonly record struct PcCompatImGuiRect(float X, float Y, float Width, float Height);

internal static class PcCompatManagedSelectionGridGeometry
{
    internal static PcCompatImGuiRect ResolveCell(
        float outerX,
        float outerY,
        float outerWidth,
        float outerHeight,
        int itemIndex,
        int itemCount,
        int columns,
        float gap)
    {
        var safeItemCount = Math.Max(1, itemCount);
        var safeIndex = Math.Clamp(itemIndex, 0, safeItemCount - 1);
        var safeColumns = Math.Clamp(columns, 1, safeItemCount);
        var rows = (int)(((long)safeItemCount + safeColumns - 1) / safeColumns);
        var safeGap = float.IsFinite(gap) ? Math.Max(0f, gap) : 0f;
        var safeOuterX = float.IsFinite(outerX) ? outerX : 0f;
        var safeOuterY = float.IsFinite(outerY) ? outerY : 0f;
        var safeOuterWidth = float.IsFinite(outerWidth) ? Math.Max(0f, outerWidth) : 0f;
        var safeOuterHeight = float.IsFinite(outerHeight) ? Math.Max(0f, outerHeight) : 0f;
        var horizontalGap = safeGap * Math.Max(0, safeColumns - 1);
        var verticalGap = safeGap * Math.Max(0, rows - 1);
        var availableWidth = float.IsFinite(horizontalGap)
            ? Math.Max(1f, safeOuterWidth - horizontalGap)
            : 1f;
        var availableHeight = float.IsFinite(verticalGap)
            ? Math.Max(1f, safeOuterHeight - verticalGap)
            : 1f;
        var width = availableWidth / safeColumns;
        var height = availableHeight / rows;
        var column = safeIndex % safeColumns;
        var row = safeIndex / safeColumns;
        return new PcCompatImGuiRect(
            safeOuterX + column * (width + safeGap),
            safeOuterY + row * (height + safeGap),
            width,
            height);
    }
}

internal readonly record struct PcCompatImGuiOptionSnapshot(
    float? Width = null,
    float? MinWidth = null,
    float? MaxWidth = null,
    float? Height = null,
    float? MinHeight = null,
    float? MaxHeight = null,
    bool? ExpandWidth = null,
    bool? ExpandHeight = null);

internal readonly record struct PcCompatImGuiMeasurement(
    float MinimumWidth,
    float PreferredWidth,
    bool SupportsTextWrapping,
    bool ExpandWidth = false,
    float PreferredHeight = 0f,
    int LayoutFingerprint = 0)
{
    public static PcCompatImGuiMeasurement Container(float availableWidth)
        => new(
            PcCompatManagedResponsiveImGuiLayout.MinimumLayoutExtent,
            // A nested GUILayout group may consume all remaining parent width. Until its
            // own final rect has been observed we reserve the full parent budget rather
            // than guessing a narrow fixed width and allowing siblings to overlap it.
            Math.Max(PcCompatManagedResponsiveImGuiLayout.MinimumLayoutExtent, availableWidth),
            false);

    public PcCompatImGuiMeasurement With(PcCompatImGuiOptionSnapshot options)
    {
        var minimum = Math.Max(0f, options.MinWidth ?? MinimumWidth);
        var preferred = Math.Max(minimum, options.Width ?? PreferredWidth);
        if (options.MaxWidth is { } maximum && maximum >= minimum)
            preferred = Math.Min(preferred, maximum);
        return this with
        {
            MinimumWidth = minimum,
            PreferredWidth = preferred,
            ExpandWidth = options.ExpandWidth ?? ExpandWidth,
            PreferredHeight = Math.Max(
                PreferredHeight,
                options.Height ?? options.MinHeight ?? PreferredHeight)
        };
    }
}

internal readonly record struct PcCompatSelectionGridMeasurement(
    float CellMinimumWidth,
    float CellPreferredWidth,
    float CellPreferredHeight,
    int ItemCount,
    int LayoutFingerprint = 0)
{
    public PcCompatImGuiMeasurement ToOuterMeasurement(int requestedColumns)
    {
        if (ItemCount <= 0)
            return new PcCompatImGuiMeasurement(0f, 0f, SupportsTextWrapping: false);

        var columns = Math.Clamp(requestedColumns, 1, ItemCount);
        var gaps = PcCompatManagedResponsiveImGuiLayout.SelectionGridCellGap *
                   Math.Max(0, columns - 1);
        var minimum = Multiply(Math.Max(
            PcCompatManagedResponsiveImGuiLayout.MinimumLayoutExtent,
            CellMinimumWidth), columns) + gaps;
        var preferred = Multiply(Math.Max(
            (minimum - gaps) / columns,
            CellPreferredWidth), columns) + gaps;
        var rows = (ItemCount + columns - 1) / columns;
        return new PcCompatImGuiMeasurement(
            minimum,
            Math.Max(minimum, preferred),
            SupportsTextWrapping: false,
            PreferredHeight: Multiply(Math.Max(
                PcCompatManagedResponsiveImGuiLayout.MinimumLayoutExtent,
                CellPreferredHeight), rows),
            LayoutFingerprint: LayoutFingerprint);
    }

    private static float Multiply(float value, int count)
    {
        var result = value * count;
        return float.IsFinite(result) ? result : float.MaxValue;
    }
}

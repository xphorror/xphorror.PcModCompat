using StArray.ModManager.Manager;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Android.PcCompat;

internal static class PcCompatKeyViewerFallbackBridge
{
    private const string LogTag = "PcCompatKeyViewerFallback";
    private const float ReferenceWidth = 1920f;
    private const float ReferenceHeight = 1080f;
    private const float SlotWidth = 82f;
    private const float SlotHeight = 74f;
    private const float SlotGap = 8f;
    private const float SlotTop = 930f;
    private static readonly Dictionary<string, Visual> Visuals =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> StaleVisualKeys = [];
    private static PcCompatGeneratedUnityHudApi? s_api;
    private static int s_failed;
    private static uint s_renderGeneration;

    public static void Install()
        => PcCompatKeyViewerFallbackRuntime.RegisterRenderer(RenderOnUnityMain);

    private static void RenderOnUnityMain(
        IReadOnlyList<PcCompatKeyViewerFallbackFrame> frames)
    {
        if (Volatile.Read(ref s_failed) != 0)
            return;
        try
        {
            s_api ??= new PcCompatGeneratedUnityHudApi();
            var generation = unchecked(++s_renderGeneration);
            if (generation == 0)
            {
                generation = ++s_renderGeneration;
                foreach (var visual in Visuals.Values)
                    visual.SeenGeneration = 0;
            }
            for (var index = 0; index < frames.Count; ++index)
            {
                var frame = frames[index];
                var key = Key(frame.ModId, frame.FeatureId);
                if (!Visuals.TryGetValue(key, out var visual) ||
                    visual.LaneCount != frame.LaneCount ||
                    visual.SessionGeneration != frame.SessionGeneration)
                {
                    if (visual != null)
                        visual.Destroy(s_api);
                    visual = CreateVisual(s_api, frame, index);
                    Visuals[key] = visual;
                }
                visual.SeenGeneration = generation;
                visual.Apply(s_api, frame, index);
            }

            StaleVisualKeys.Clear();
            foreach (var pair in Visuals)
            {
                if (pair.Value.SeenGeneration != generation)
                    StaleVisualKeys.Add(pair.Key);
            }
            foreach (var stale in StaleVisualKeys)
            {
                Visuals[stale].Destroy(s_api);
                Visuals.Remove(stale);
            }
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref s_failed, 1);
            PcCompatKeyViewerFallbackRuntime.ReportRendererFailure(exception.ToString());
            Logger.Error(LogTag, "fallback renderer failed closed: " + exception);
            foreach (var visual in Visuals.Values)
            {
                try
                {
                    if (s_api != null)
                        visual.Destroy(s_api);
                }
                catch
                {
                }
            }
            Visuals.Clear();
            PcCompatKeyViewerFallbackRuntime.RegisterRenderer(null);
        }
    }

    private static Visual CreateVisual(
        PcCompatGeneratedUnityHudApi api,
        PcCompatKeyViewerFallbackFrame frame,
        int visualIndex)
    {
        var root = api.CreateGameObject($"PcCompat KeyViewer Fallback {frame.ModId}:{frame.FeatureId}");
        api.SetActive(root, false);
        var canvas = Require(api.AddComponent(root, api.CanvasType), "Canvas");
        var scaler = Require(api.AddComponent(root, api.CanvasScalerType), "CanvasScaler");
        api.SetCanvasRenderMode(canvas, 0);
        api.SetCanvasSortingOrder(canvas, 220 + visualIndex);
        api.SetCanvasScaleMode(scaler, 1);
        api.SetCanvasReferenceResolution(scaler, ReferenceWidth, ReferenceHeight);
        api.SetCanvasMatch(scaler, 0.5f);
        var rootTransform = Require(api.GetTransform(root), "root RectTransform");

        var titleObject = api.CreateGameObject("Fallback.Title");
        var title = Require(api.AddComponent(titleObject, api.TextMeshProType), "title TMP");
        var titleRect = Require(api.GetRectTransform(title, titleObject), "title RectTransform");
        api.SetParent(titleRect, rootTransform);
        api.ConfigureText(title, richText: false);
        api.ApplyLocalizedFont(title);
        api.SetText(title, "Fallback");
        api.SetFontSize(title, 25f);
        api.SetGraphicColor(title, 1f, 0.18f, 0.18f, 1f);

        var rainObject = api.CreateGameObject("Fallback.RainBatch");
        var rainRenderer = Require(
            api.AddComponent(rainObject, api.CanvasRendererType),
            "rain CanvasRenderer");
        var rainRect = Require(api.GetTransform(rainObject), "rain RectTransform");
        api.SetParent(rainRect, rootTransform);
        api.SetTopLeftRect(rainRect, 0f, 0f, ReferenceWidth, ReferenceHeight);
        var rainMesh = api.CreateBatchMesh();

        var lanes = new LaneVisual[frame.LaneCount];
        nint material = nint.Zero;
        for (var lane = 0; lane < lanes.Length; ++lane)
        {
            var panelObject = api.CreateGameObject($"Lane.{lane + 1}.Panel");
            var panel = Require(api.AddComponent(panelObject, api.ImageType), "lane Image");
            var panelRect = Require(api.GetTransform(panelObject), "lane RectTransform");
            api.SetParent(panelRect, rootTransform);
            api.SetRaycastTarget(panel, false);
            material = material == nint.Zero ? api.GetGraphicMaterial(panel) : material;

            var labelObject = api.CreateGameObject($"Lane.{lane + 1}.Label");
            var label = Require(api.AddComponent(labelObject, api.TextMeshProType), "lane label TMP");
            var labelRect = Require(api.GetRectTransform(label, labelObject), "lane label RectTransform");
            api.SetParent(labelRect, rootTransform);
            api.ConfigureText(label, richText: false);
            api.ApplyLocalizedFont(label);
            api.SetFontSize(label, 25f);
            api.SetGraphicColor(label, 1f, 1f, 1f, 1f);

            var countObject = api.CreateGameObject($"Lane.{lane + 1}.Count");
            var count = Require(api.AddComponent(countObject, api.TextMeshProType), "lane count TMP");
            var countRect = Require(api.GetRectTransform(count, countObject), "lane count RectTransform");
            api.SetParent(countRect, rootTransform);
            api.ConfigureText(count, richText: false);
            api.ApplyLocalizedFont(count);
            api.SetFontSize(count, 18f);
            api.SetGraphicColor(count, 0.78f, 0.82f, 0.9f, 1f);
            lanes[lane] = new LaneVisual(
                panel,
                panelRect,
                label,
                labelRect,
                count,
                countRect);
        }
        if (material == nint.Zero)
            throw new InvalidOperationException("Fallback rain material is unavailable.");
        api.SetCanvasRendererMaterial(rainRenderer, material);
        api.SetCanvasRendererColor(rainRenderer, 0.32f, 0.82f, 1f, 0.72f);
        api.DontDestroyOnLoad(root);
        return new Visual(
            root,
            titleRect,
            rainRenderer,
            rainMesh,
            lanes,
            frame.SessionGeneration);
    }

    private static string Key(string modId, string featureId)
        => modId + "\0" + featureId;

    private static nint Require(nint value, string name)
        => value != nint.Zero
            ? value
            : throw new InvalidOperationException($"Could not create fallback {name}.");

    private sealed class Visual
    {
        private readonly nint _root;
        private readonly nint _titleRect;
        private readonly nint _rainRenderer;
        private readonly nint _rainMesh;
        private readonly LaneVisual[] _lanes;
        private readonly long _sessionGeneration;
        private uint _heldMask = uint.MaxValue;
        private readonly ulong[] _counts;
        private readonly string?[] _labels;
        private readonly List<(float Left, float Top, float Right, float Bottom)> _rainQuads =
            new(256);
        private bool _visible;
        private bool _hasRainGeometry;
        private int _visualIndex = int.MinValue;

        public Visual(
            nint root,
            nint titleRect,
            nint rainRenderer,
            nint rainMesh,
            LaneVisual[] lanes,
            long sessionGeneration)
        {
            _root = root;
            _titleRect = titleRect;
            _rainRenderer = rainRenderer;
            _rainMesh = rainMesh;
            _lanes = lanes;
            _sessionGeneration = sessionGeneration;
            _counts = Enumerable.Repeat(ulong.MaxValue, lanes.Length).ToArray();
            _labels = new string?[lanes.Length];
        }

        public int LaneCount => _lanes.Length;
        public long SessionGeneration => _sessionGeneration;
        public uint SeenGeneration { get; set; }

        public void Apply(
            PcCompatGeneratedUnityHudApi api,
            PcCompatKeyViewerFallbackFrame frame,
            int visualIndex)
        {
            var totalWidth = LaneCount * SlotWidth + Math.Max(0, LaneCount - 1) * SlotGap;
            var startX = (ReferenceWidth - totalWidth) * 0.5f;
            var offsetY = visualIndex * 122f;
            var layoutChanged = _visualIndex != visualIndex;
            if (layoutChanged)
                api.SetTopLeftRect(_titleRect, startX, SlotTop - 38f - offsetY, totalWidth, 32f);
            for (var lane = 0; lane < LaneCount; ++lane)
            {
                var x = startX + lane * (SlotWidth + SlotGap);
                var y = SlotTop - offsetY;
                var item = _lanes[lane];
                if (layoutChanged)
                {
                    api.SetTopLeftRect(item.PanelRect, x, y, SlotWidth, SlotHeight);
                    api.SetTopLeftRect(item.LabelRect, x, y + 7f, SlotWidth, 34f);
                    api.SetTopLeftRect(item.CountRect, x, y + 43f, SlotWidth, 24f);
                }
                var held = (frame.HeldMask & (1u << lane)) != 0;
                if (_heldMask == uint.MaxValue ||
                    ((_heldMask & (1u << lane)) != 0) != held)
                {
                    api.SetGraphicColor(
                        item.Panel,
                        held ? 0.16f : 0.08f,
                        held ? 0.58f : 0.11f,
                        held ? 0.82f : 0.15f,
                        0.9f);
                }
                var label = lane < frame.Labels.Length ? frame.Labels[lane] : $"T{lane + 1}";
                if (!string.Equals(_labels[lane], label, StringComparison.Ordinal))
                {
                    api.SetText(item.Label, label);
                    _labels[lane] = label;
                }
                var count = lane < frame.Counts.Length ? frame.Counts[lane] : 0;
                if (_counts[lane] != count)
                {
                    api.SetText(item.Count, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    _counts[lane] = count;
                }
            }
            _heldMask = frame.HeldMask;
            _visualIndex = visualIndex;

            BuildRainQuads(frame, startX, offsetY, _rainQuads);
            if (_rainQuads.Count != 0 || _hasRainGeometry)
            {
                api.SetBatchMesh(_rainRenderer, _rainMesh, _rainQuads);
                _hasRainGeometry = _rainQuads.Count != 0;
            }
            if (_visible != frame.Visible)
            {
                api.SetActive(_root, frame.Visible);
                _visible = frame.Visible;
            }
        }

        public void Destroy(PcCompatGeneratedUnityHudApi api)
        {
            api.Destroy(_rainMesh);
            api.Destroy(_root);
        }

        private static void BuildRainQuads(
            PcCompatKeyViewerFallbackFrame frame,
            float startX,
            float offsetY,
            List<(float Left, float Top, float Right, float Bottom)> result)
        {
            result.Clear();
            var start = Math.Max(0, frame.RainPulses.Count - 256);
            for (var index = start; index < frame.RainPulses.Count; ++index)
            {
                var pulse = frame.RainPulses[index];
                var upRawNs = pulse.UpRawNs == 0
                    ? frame.NowRawNs
                    : Math.Min(frame.NowRawNs, pulse.UpRawNs);
                var heldSeconds = Math.Clamp(
                    (upRawNs - pulse.DownRawNs) / 1_000_000_000f,
                    0f,
                    3f);
                var releasedSeconds = pulse.UpRawNs == 0
                    ? 0f
                    : Math.Max(0f, (frame.NowRawNs - pulse.UpRawNs) / 1_000_000_000f);
                if (releasedSeconds > 1.5f)
                    continue;
                var height = 10f + heldSeconds * 230f;
                var shift = releasedSeconds * 280f;
                var left = startX + pulse.Lane * (SlotWidth + SlotGap) + 8f;
                var right = left + SlotWidth - 16f;
                var bottom = -(SlotTop - offsetY) + shift;
                var top = bottom + height;
                result.Add((left, top, right, bottom));
            }
        }
    }

    private sealed record LaneVisual(
        nint Panel,
        nint PanelRect,
        nint Label,
        nint LabelRect,
        nint Count,
        nint CountRect);
}

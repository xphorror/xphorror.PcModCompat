using System.Collections.Concurrent;
using System.Diagnostics;

namespace Xphorror.PcModCompat;

public sealed class PcCompatKeyViewerFallbackFrame
{
    public required string ModId { get; init; }
    public required string FeatureId { get; init; }
    public bool Visible { get; internal set; }
    public int LaneCount { get; internal set; }
    public uint HeldMask { get; internal set; }
    public PcCompatKeyViewerInputMode InputMode { get; internal set; }
    public long NowRawNs { get; internal set; }
    public required string[] Labels { get; init; }
    public required ulong[] Counts { get; init; }
    public required IReadOnlyList<PcCompatKeyViewerRainPulse> RainPulses { get; init; }
}

public sealed class PcCompatKeyViewerFallbackSnapshot
{
    public bool RendererRegistered { get; init; }
    public int RegistrationCount { get; init; }
    public long DispatchCount { get; init; }
    public int LastFrameCount { get; init; }
    public string? RendererError { get; init; }
}

/// <summary>
/// Explicitly enabled compatibility presentation. It never becomes the default
/// backend and never mutates the MOD-owned state machine. Unity object work is
/// delegated to the registered UnityMain renderer.
/// </summary>
public static class PcCompatKeyViewerFallbackRuntime
{
    private static readonly ConcurrentDictionary<string, Registration> Registrations =
        new(StringComparer.OrdinalIgnoreCase);
    private static Registration[] s_dispatchRegistrations = Array.Empty<Registration>();
    private static readonly List<PcCompatKeyViewerFallbackFrame> DispatchFrames = [];
    private static Action<IReadOnlyList<PcCompatKeyViewerFallbackFrame>>? s_renderer;
    private static Action? s_demandChanged;
    private static int s_pendingClear;
    private static long s_dispatchCount;
    private static int s_lastFrameCount;
    private static int s_dispatchActive;
    private static string? s_rendererError;

    public static bool HasDemand
        => Volatile.Read(ref s_dispatchRegistrations).Length != 0 ||
           Volatile.Read(ref s_pendingClear) != 0;

    public static void RegisterRenderer(
        Action<IReadOnlyList<PcCompatKeyViewerFallbackFrame>>? renderer)
    {
        // Frames and their backing buffers are reused after the synchronous callback returns.
        Volatile.Write(ref s_renderer, renderer);
        if (renderer != null)
            Volatile.Write(ref s_rendererError, null);
    }

    public static void ReportRendererFailure(string error)
        => Volatile.Write(
            ref s_rendererError,
            string.IsNullOrWhiteSpace(error) ? "unknown fallback renderer failure" : error);

    public static PcCompatKeyViewerFallbackSnapshot Snapshot()
        => new()
        {
            RendererRegistered = Volatile.Read(ref s_renderer) != null,
            RegistrationCount = Registrations.Count,
            DispatchCount = Interlocked.Read(ref s_dispatchCount),
            LastFrameCount = Volatile.Read(ref s_lastFrameCount),
            RendererError = Volatile.Read(ref s_rendererError)
        };

    public static void RegisterDemandChangedSink(Action? sink)
        => Volatile.Write(ref s_demandChanged, sink);

    public static void RegisterOrUpdate(
        string modId,
        PcCompatKeyViewerAdapterDocument adapter,
        PcCompatKeyViewerOverrideDocument overrides,
        IReadOnlyList<PcCompatKeyViewerLoweredConsumerPlan>? presentationPlans = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(overrides);
        var features = new List<FeatureConfig>();
        foreach (var featureOverride in overrides.Features)
        {
            if (!featureOverride.Enabled || !featureOverride.CompatibleFallbackEnabled)
                continue;
            var exists = false;
            foreach (var feature in adapter.Features)
            {
                if (feature.Id != featureOverride.FeatureId)
                    continue;
                exists = true;
                break;
            }
            if (!exists)
                continue;
            var laneCount = Math.Clamp(featureOverride.TouchLaneCount, 2, 10);
            var plan = presentationPlans?.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.FeatureId,
                    featureOverride.FeatureId,
                    StringComparison.Ordinal));
            features.Add(new FeatureConfig(
                featureOverride.FeatureId,
                laneCount,
                PcCompatKeyViewerLabelFormatter.CreateTouchLabels(laneCount),
                plan == null
                    ? PcCompatKeyViewerLabelFormatter.CreateTouchLabels(laneCount)
                    : PcCompatKeyViewerLabelFormatter.CreateExternalLabels(plan, laneCount)));
        }
        if (features.Count == 0)
        {
            Unregister(modId);
            return;
        }
        var featureArray = features.ToArray();
        var added = Registrations.TryAdd(modId, new Registration(modId, featureArray));
        if (!added)
            Registrations[modId] = new Registration(modId, featureArray);
        PublishRegistrations();
        NotifyDemandChanged();
    }

    public static void Unregister(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId) || !Registrations.TryRemove(modId, out _))
            return;
        PublishRegistrations();
        Interlocked.Exchange(ref s_pendingClear, 1);
        NotifyDemandChanged();
    }

    public static void DispatchFrame(float deltaTime)
    {
        var renderer = Volatile.Read(ref s_renderer);
        if (renderer == null || Interlocked.CompareExchange(ref s_dispatchActive, 1, 0) != 0)
            return;
        try
        {
            var clock = PcCompatClockAnchorRuntime.MonotonicSnapshot();
            var registrations = Volatile.Read(ref s_dispatchRegistrations);
            DispatchFrames.Clear();
            foreach (var registration in registrations)
                registration.AppendFrames(DispatchFrames, clock, deltaTime);
            Interlocked.Increment(ref s_dispatchCount);
            Volatile.Write(ref s_lastFrameCount, DispatchFrames.Count);
            renderer(DispatchFrames);
            if (Interlocked.Exchange(ref s_pendingClear, 0) != 0)
                NotifyDemandChanged();
        }
        finally
        {
            Volatile.Write(ref s_dispatchActive, 0);
        }
    }

    private static void PublishRegistrations()
        => Volatile.Write(
            ref s_dispatchRegistrations,
            Registrations.Values
                .OrderBy(value => value.ModId, StringComparer.OrdinalIgnoreCase)
                .ToArray());

    private static void NotifyDemandChanged()
    {
        try
        {
            Volatile.Read(ref s_demandChanged)?.Invoke();
        }
        catch
        {
            // Host frame-gate diagnostics own their failure reporting.
        }
    }

    private sealed class Registration
    {
        private long _fallbackRawNs;
        private long _lastTimestamp;
        private readonly FeatureRuntime[] _features;
        private readonly PcCompatKeyViewerFallbackFeatureBuffer[] _featureBuffers;

        public Registration(string modId, FeatureConfig[] features)
        {
            ModId = modId;
            _features = features.Select(feature => new FeatureRuntime(modId, feature)).ToArray();
            _featureBuffers = _features.Select(feature => feature.Buffer).ToArray();
        }

        public string ModId { get; }

        public void AppendFrames(
            List<PcCompatKeyViewerFallbackFrame> output,
            PcCompatMonotonicClockSnapshot clock,
            float deltaTime)
        {
            PcCompatKeyViewerPreviewRuntime.CopyFallbackFeatures(ModId, _featureBuffers);
            var latestEventRawNs = 0L;
            foreach (var feature in _features)
            {
                latestEventRawNs = Math.Max(latestEventRawNs, feature.LatestEventRawNs);
            }
            var nowRawNs = ResolveNowRawNs(clock, latestEventRawNs, deltaTime);
            foreach (var feature in _features)
            {
                if (feature.PrepareFrame(nowRawNs))
                    output.Add(feature.Frame);
            }
        }

        private long ResolveNowRawNs(
            PcCompatMonotonicClockSnapshot clock,
            long latestEventRawNs,
            float deltaTime)
        {
            if (clock.ProviderAvailable && clock.MonotonicRawNs > 0)
            {
                _fallbackRawNs = clock.MonotonicRawNs;
                return _fallbackRawNs;
            }
            var timestamp = Stopwatch.GetTimestamp();
            if (_lastTimestamp != 0)
            {
                var elapsed = Math.Clamp(
                    (timestamp - _lastTimestamp) / (double)Stopwatch.Frequency,
                    0d,
                    Math.Max(0.25d, deltaTime));
                _fallbackRawNs += (long)(elapsed * 1_000_000_000d);
            }
            _lastTimestamp = timestamp;
            _fallbackRawNs = Math.Max(_fallbackRawNs, latestEventRawNs);
            return _fallbackRawNs;
        }
    }

    private sealed class FeatureRuntime
    {
        private readonly int _configuredLaneCount;
        private readonly string[] _touchLabels;
        private readonly string[] _externalLabels;
        private PcCompatKeyViewerInputMode _inputMode = PcCompatKeyViewerInputMode.Auto;

        public FeatureRuntime(string modId, FeatureConfig config)
        {
            _configuredLaneCount = config.LaneCount;
            _touchLabels = config.TouchLabels;
            _externalLabels = config.ExternalLabels;
            Buffer = new PcCompatKeyViewerFallbackFeatureBuffer(
                config.FeatureId,
                config.LaneCount);
            Frame = new PcCompatKeyViewerFallbackFrame
            {
                ModId = modId,
                FeatureId = config.FeatureId,
                Visible = true,
                LaneCount = config.LaneCount,
                Labels = new string[config.LaneCount],
                Counts = Buffer.DownOrdinals,
                RainPulses = Buffer.RainPulses
            };
        }

        public PcCompatKeyViewerFallbackFrame Frame { get; }
        public PcCompatKeyViewerFallbackFeatureBuffer Buffer { get; }
        public long LatestEventRawNs => Buffer.Captured ? Buffer.State.LatestEventRawNs : 0;

        public bool PrepareFrame(long nowRawNs)
        {
            var state = Buffer.State;
            if (!Buffer.Captured || state.Faulted || !state.ConsumerActive)
                return false;
            var rainPulses = Buffer.RainPulses;
            var laneCount = Math.Min(_configuredLaneCount, state.LaneCount);
            if (_inputMode != state.InputMode)
            {
                _inputMode = state.InputMode;
                var source = state.InputMode == PcCompatKeyViewerInputMode.Touch
                    ? _touchLabels
                    : _externalLabels;
                Array.Copy(source, Frame.Labels, Frame.Labels.Length);
            }
            var write = 0;
            for (var read = 0; read < rainPulses.Count; ++read)
            {
                var pulse = rainPulses[read];
                if (pulse.Lane < 0 || pulse.Lane >= laneCount ||
                    (pulse.UpRawNs != 0 && nowRawNs - pulse.UpRawNs > 1_500_000_000L))
                {
                    continue;
                }
                if (write != read)
                    rainPulses[write] = pulse;
                ++write;
            }
            if (write < rainPulses.Count)
                rainPulses.RemoveRange(write, rainPulses.Count - write);
            Frame.Visible = true;
            Frame.InputMode = state.InputMode;
            Frame.LaneCount = laneCount;
            Frame.HeldMask = state.HeldMask & ((1u << laneCount) - 1u);
            Frame.NowRawNs = nowRawNs;
            return true;
        }
    }

    private sealed record FeatureConfig(
        string FeatureId,
        int LaneCount,
        string[] TouchLabels,
        string[] ExternalLabels);
}

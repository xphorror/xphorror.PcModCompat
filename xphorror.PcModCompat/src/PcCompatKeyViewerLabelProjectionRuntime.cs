using System.Diagnostics;

namespace Xphorror.PcModCompat;

/// <summary>
/// Projects mode-specific labels into a confirmed MOD LabelProvider on UnityMain.
/// Mode checks are cheap per-frame; managed reflection is limited to transitions
/// and a low-frequency target refresh for MODs that swap their active style array.
/// </summary>
public static class PcCompatKeyViewerLabelProjectionRuntime
{
    private static readonly object RegistrationLock = new();
    private static readonly Dictionary<string, Registration> Registrations =
        new(StringComparer.OrdinalIgnoreCase);
    private static Registration[] s_dispatchRegistrations = Array.Empty<Registration>();
    private static Action? s_demandChanged;

    public static bool HasDemand => Volatile.Read(ref s_dispatchRegistrations).Length != 0;

    public static void RegisterDemandChangedSink(Action? sink)
        => Volatile.Write(ref s_demandChanged, sink);

    public static void RegisterOrUpdate(
        string modId,
        PcCompatKeyViewerAdapterDocument adapter,
        PcCompatKeyViewerOverrideDocument overrides,
        IReadOnlyList<PcCompatKeyViewerLoweredConsumerPlan> presentationPlans)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(presentationPlans);

        var features = new List<Feature>();
        foreach (var featureOverride in overrides.Features.Where(feature => feature.Enabled))
        {
            var feature = adapter.Features.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, featureOverride.FeatureId, StringComparison.Ordinal));
            var plan = presentationPlans.FirstOrDefault(candidate =>
                string.Equals(candidate.FeatureId, featureOverride.FeatureId, StringComparison.Ordinal));
            if (feature == null || plan == null)
                continue;
            var labelProvider = PcCompatKeyViewerOverrideStore.ResolveSelectedOrUniqueRole(
                feature,
                featureOverride,
                "LabelProvider");
            if (labelProvider == null)
                continue;
            var laneCount = Math.Clamp(featureOverride.TouchLaneCount, 2, 10);
            features.Add(new Feature(
                modId,
                featureOverride.FeatureId,
                labelProvider,
                PcCompatKeyViewerLabelFormatter.CreateTouchLabels(laneCount)));
        }

        Registration? previous;
        lock (RegistrationLock)
        {
            Registrations.Remove(modId, out previous);
            if (features.Count != 0)
                Registrations[modId] = new Registration(modId, features.ToArray());
            PublishRegistrationsLocked();
        }
        previous?.Restore();
        NotifyDemandChanged();
    }

    public static void Unregister(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
            return;
        Registration? removed;
        lock (RegistrationLock)
        {
            if (!Registrations.Remove(modId, out removed))
                return;
            PublishRegistrationsLocked();
        }
        removed.Restore();
        NotifyDemandChanged();
    }

    public static void DispatchFrame()
    {
        var now = Stopwatch.GetTimestamp();
        foreach (var registration in Volatile.Read(ref s_dispatchRegistrations))
            registration.Dispatch(now);
    }

    public static string? GetLastError(string modId)
    {
        lock (RegistrationLock)
            return Registrations.TryGetValue(modId, out var registration)
                ? registration.LastError
                : null;
    }

    private static void PublishRegistrationsLocked()
        => Volatile.Write(
            ref s_dispatchRegistrations,
            Registrations.Values
                .OrderBy(registration => registration.ModId, StringComparer.OrdinalIgnoreCase)
                .ToArray());

    private static void NotifyDemandChanged()
    {
        try
        {
            Volatile.Read(ref s_demandChanged)?.Invoke();
        }
        catch
        {
            // The frame-gate owner reports callback failures.
        }
    }

    private sealed class Registration(string modId, Feature[] features)
    {
        public string ModId { get; } = modId;
        public string? LastError
            => features.Select(feature => feature.LastError)
                .FirstOrDefault(error => !string.IsNullOrWhiteSpace(error));

        public void Dispatch(long now)
        {
            foreach (var feature in features)
                feature.Dispatch(now);
        }

        public void Restore()
        {
            foreach (var feature in features)
                feature.Restore();
        }
    }

    private sealed class Feature(
        string modId,
        string featureId,
        PcCompatKeyViewerRoleOverride labelProvider,
        string[] touchLabels)
    {
        private static readonly long RefreshInterval = Stopwatch.Frequency;
        private PcCompatKeyViewerInputMode _lastMode = PcCompatKeyViewerInputMode.Auto;
        private long _nextRefreshTimestamp;
        private bool _ownsProjection;

        public string? LastError { get; private set; }

        public void Dispatch(long now)
        {
            if (!PcCompatKeyViewerPreviewRuntime.TryGetFeatureInputMode(
                    modId,
                    featureId,
                    out var inputMode))
            {
                return;
            }
            if (_lastMode == inputMode && now < _nextRefreshTimestamp)
                return;

            var useTouchLabels = inputMode == PcCompatKeyViewerInputMode.Touch;
            bool applied;
            string? error;
            if (useTouchLabels)
            {
                applied = PcCompatRuntime.TryProjectManagedKeyViewerLabels(
                    modId,
                    labelProvider,
                    touchLabels,
                    adoptLegacyTouchLabels: false,
                    out _,
                    out error);
                _ownsProjection = true;
            }
            else
            {
                applied = PcCompatRuntime.TryRestoreManagedKeyViewerLabels(
                    modId,
                    labelProvider,
                    touchLabels.Length,
                    out _,
                    out error);
                _ownsProjection = false;
            }
            _lastMode = inputMode;
            if (!applied)
            {
                LastError = error;
                _nextRefreshTimestamp = now + RefreshInterval;
                return;
            }
            LastError = null;
            _nextRefreshTimestamp = now + RefreshInterval;
        }

        public void Restore()
        {
            if (!_ownsProjection)
                return;
            if (!PcCompatRuntime.TryRestoreManagedKeyViewerLabels(
                    modId,
                    labelProvider,
                    touchLabels.Length,
                    out _,
                    out var error))
            {
                LastError = error;
                return;
            }
            _ownsProjection = false;
            LastError = null;
        }
    }
}

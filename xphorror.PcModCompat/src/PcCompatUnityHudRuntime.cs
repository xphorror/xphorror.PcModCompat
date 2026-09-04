using System.Runtime.CompilerServices;
using System.Threading;

namespace Xphorror.PcModCompat;

public sealed class PcCompatUnityHudFrame
{
    public string ModId { get; init; } = string.Empty;
    public bool Visible { get; init; }
    public uint OverlayGeneration { get; init; }
    public int StyleGeneration { get; init; }
    public string RichText { get; init; } = string.Empty;
    public string PlainText { get; init; } = string.Empty;
    public int LineCount { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
    public float Scale { get; init; } = 1f;
    public float PositionX { get; init; }
    public float PositionY { get; init; }
    public float BackgroundOpacity { get; init; }
    public bool ProgressBarVisible { get; init; }
    public float ProgressBarValue { get; init; }
}

public interface IPcCompatUnityHudSource
{
    bool TryGetUnityHudFrame(out PcCompatUnityHudFrame frame);
}

public sealed class PcCompatUnityHudSourceSnapshot
{
    public required string OwnerId { get; init; }
    public long SessionGeneration { get; init; }
    public PcCompatUnityHudFrame? Frame { get; init; }
    public Exception? Error { get; init; }
}

public static class PcCompatUnityHudRuntime
{
    private sealed record SourceRegistration(
        string OwnerId,
        long SessionGeneration,
        IPcCompatUnityHudSource Source);

    private static readonly object SourceLock = new();
    private static SourceRegistration[] s_sources = Array.Empty<SourceRegistration>();
    private static readonly HashSet<string> FailedRenderers = new(StringComparer.OrdinalIgnoreCase);
    private static int s_rendererAvailable;
    private static Action? s_sourcesChangedSink;

    public static bool RendererAvailable
        => Volatile.Read(ref s_rendererAvailable) != 0;

    public static bool RendererAvailableFor(string modId)
    {
        if (!RendererAvailable)
            return false;
        if (string.IsNullOrWhiteSpace(modId))
            return true;
        lock (SourceLock)
            return !FailedRenderers.Contains(modId);
    }

    public static void RegisterRenderer()
        => Volatile.Write(ref s_rendererAvailable, 1);

    public static void MarkRendererFailed()
        => Volatile.Write(ref s_rendererAvailable, 0);

    public static void MarkSourceRendererFailed(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
            return;
        lock (SourceLock)
            FailedRenderers.Add(modId);
    }

    public static void ClearSourceRendererFailure(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
            return;
        lock (SourceLock)
            FailedRenderers.Remove(modId);
    }

    public static void RegisterSourcesChangedSink(Action? sink)
        => Volatile.Write(ref s_sourcesChangedSink, sink);

    public static void RegisterSource(string ownerId, IPcCompatUnityHudSource source)
        => RegisterSource(ownerId, 0, source);

    public static void RegisterSource(
        string ownerId,
        long sessionGeneration,
        IPcCompatUnityHudSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (sessionGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(sessionGeneration));
        ArgumentNullException.ThrowIfNull(source);
        lock (SourceLock)
        {
            if (s_sources.Any(candidate => ReferenceEquals(candidate.Source, source)))
                return;
            if (s_sources.Any(candidate => candidate.OwnerId.Equals(ownerId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"A Unity HUD source is already registered for owner '{ownerId}'.");
            }

            var next = new SourceRegistration[s_sources.Length + 1];
            Array.Copy(s_sources, next, s_sources.Length);
            next[^1] = new SourceRegistration(ownerId, sessionGeneration, source);
            FailedRenderers.Remove(ownerId);
            Volatile.Write(ref s_sources, next);
        }
        NotifySourcesChanged();
    }

    public static void RegisterSource(IPcCompatUnityHudSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RegisterSource(
            $"legacy:{source.GetType().FullName}:{RuntimeHelpers.GetHashCode(source):X8}",
            source);
    }

    public static void UnregisterSource(IPcCompatUnityHudSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (SourceLock)
        {
            var index = Array.FindIndex(
                s_sources,
                candidate => ReferenceEquals(candidate.Source, source));
            if (index < 0)
                return;

            var ownerId = s_sources[index].OwnerId;
            var next = new SourceRegistration[s_sources.Length - 1];
            if (index > 0)
                Array.Copy(s_sources, 0, next, 0, index);
            if (index < s_sources.Length - 1)
                Array.Copy(s_sources, index + 1, next, index, s_sources.Length - index - 1);
            FailedRenderers.Remove(ownerId);
            Volatile.Write(ref s_sources, next);
        }
        NotifySourcesChanged();
    }

    public static IReadOnlyList<PcCompatUnityHudSourceSnapshot> SnapshotSources()
    {
        var sources = Volatile.Read(ref s_sources);
        if (sources.Length == 0)
            return Array.Empty<PcCompatUnityHudSourceSnapshot>();

        var snapshots = new PcCompatUnityHudSourceSnapshot[sources.Length];
        for (var index = 0; index < sources.Length; ++index)
        {
            var registration = sources[index];
            try
            {
            snapshots[index] = registration.Source.TryGetUnityHudFrame(out var frame)
                    ? new PcCompatUnityHudSourceSnapshot
                    {
                        OwnerId = registration.OwnerId,
                        SessionGeneration = registration.SessionGeneration,
                        Frame = frame
                    }
                    : new PcCompatUnityHudSourceSnapshot
                    {
                        OwnerId = registration.OwnerId,
                        SessionGeneration = registration.SessionGeneration
                    };
            }
            catch (Exception exception)
            {
                snapshots[index] = new PcCompatUnityHudSourceSnapshot
                {
                    OwnerId = registration.OwnerId,
                    SessionGeneration = registration.SessionGeneration,
                    Error = exception
                };
            }
        }
        return snapshots;
    }

    public static bool TryGetFrame(out PcCompatUnityHudFrame frame)
    {
        var snapshots = SnapshotSources();
        for (var index = snapshots.Count - 1; index >= 0; --index)
        {
            var snapshot = snapshots[index];
            if (snapshot.Error == null && snapshot.Frame is { Visible: true } visible)
            {
                frame = visible;
                return true;
            }
        }

        for (var index = snapshots.Count - 1; index >= 0; --index)
        {
            var snapshot = snapshots[index];
            if (snapshot.Error == null && snapshot.Frame != null)
            {
                frame = snapshot.Frame;
                return true;
            }
        }

        frame = null!;
        return false;
    }

    private static void NotifySourcesChanged()
    {
        try
        {
            Volatile.Read(ref s_sourcesChangedSink)?.Invoke();
        }
        catch
        {
            // Renderer notification failure must not corrupt source registration.
        }
    }
}

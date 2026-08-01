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

public static class PcCompatUnityHudRuntime
{
    private static readonly object SourceLock = new();
    private static IPcCompatUnityHudSource[] s_sources = Array.Empty<IPcCompatUnityHudSource>();
    private static int s_rendererAvailable;
    private static Action? s_sourcesChangedSink;

    public static bool RendererAvailable
        => Volatile.Read(ref s_rendererAvailable) != 0;

    public static void RegisterRenderer()
        => Volatile.Write(ref s_rendererAvailable, 1);

    public static void MarkRendererFailed()
        => Volatile.Write(ref s_rendererAvailable, 0);

    public static void RegisterSourcesChangedSink(Action? sink)
        => Volatile.Write(ref s_sourcesChangedSink, sink);

    public static void RegisterSource(IPcCompatUnityHudSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (SourceLock)
        {
            if (s_sources.Any(candidate => ReferenceEquals(candidate, source)))
                return;

            var next = new IPcCompatUnityHudSource[s_sources.Length + 1];
            Array.Copy(s_sources, next, s_sources.Length);
            next[^1] = source;
            Volatile.Write(ref s_sources, next);
        }
        NotifySourcesChanged();
    }

    public static void UnregisterSource(IPcCompatUnityHudSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (SourceLock)
        {
            var index = Array.FindIndex(s_sources, candidate => ReferenceEquals(candidate, source));
            if (index < 0)
                return;

            var next = new IPcCompatUnityHudSource[s_sources.Length - 1];
            if (index > 0)
                Array.Copy(s_sources, 0, next, 0, index);
            if (index < s_sources.Length - 1)
                Array.Copy(s_sources, index + 1, next, index, s_sources.Length - index - 1);
            Volatile.Write(ref s_sources, next);
        }
        NotifySourcesChanged();
    }

    public static bool TryGetFrame(out PcCompatUnityHudFrame frame)
    {
        var sources = Volatile.Read(ref s_sources);
        PcCompatUnityHudFrame? hiddenFrame = null;
        for (var index = sources.Length - 1; index >= 0; --index)
        {
            if (!sources[index].TryGetUnityHudFrame(out var candidate))
                continue;
            if (candidate.Visible)
            {
                frame = candidate;
                return true;
            }
            hiddenFrame ??= candidate;
        }

        if (hiddenFrame != null)
        {
            frame = hiddenFrame;
            return true;
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
            // A renderer notification must not corrupt source registration.
        }
    }
}

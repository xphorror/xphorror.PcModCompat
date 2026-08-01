namespace Xphorror.PcModCompat;

public sealed class PcCompatDiagnosticsExportStatus
{
    public static PcCompatDiagnosticsExportStatus Unavailable { get; } = new();

    public bool ProviderAvailable { get; init; }
    public int Serial { get; init; }
    public string State { get; init; } = "Unavailable";
    public string Message { get; init; } = string.Empty;
}

public static class PcCompatDiagnosticsExportRuntime
{
    private static readonly object SyncLock = new();
    private static Func<string, string, bool>? s_exporter;
    private static Func<PcCompatDiagnosticsExportStatus>? s_statusProvider;

    public static void RegisterProvider(
        Func<string, string, bool> exporter,
        Func<PcCompatDiagnosticsExportStatus> statusProvider)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(statusProvider);
        lock (SyncLock)
        {
            s_exporter = exporter;
            s_statusProvider = statusProvider;
        }
    }

    public static bool RequestExport(string suggestedName, string content)
    {
        Func<string, string, bool>? exporter;
        lock (SyncLock)
            exporter = s_exporter;
        if (exporter == null)
            return false;

        try
        {
            return exporter(suggestedName, content);
        }
        catch
        {
            return false;
        }
    }

    public static PcCompatDiagnosticsExportStatus GetStatus()
    {
        Func<PcCompatDiagnosticsExportStatus>? provider;
        lock (SyncLock)
            provider = s_statusProvider;
        if (provider == null)
            return PcCompatDiagnosticsExportStatus.Unavailable;

        try
        {
            return provider() ?? PcCompatDiagnosticsExportStatus.Unavailable;
        }
        catch
        {
            return PcCompatDiagnosticsExportStatus.Unavailable;
        }
    }
}

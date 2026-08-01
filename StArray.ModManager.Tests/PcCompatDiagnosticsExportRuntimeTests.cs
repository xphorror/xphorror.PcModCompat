using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public class PcCompatDiagnosticsExportRuntimeTests
{
    [Test]
    public void RegisteredExporterReceivesNameAndUtf8Content()
    {
        string? actualName = null;
        string? actualContent = null;
        PcCompatDiagnosticsExportRuntime.RegisterProvider(
            (name, content) =>
            {
                actualName = name;
                actualContent = content;
                return true;
            },
            () => new PcCompatDiagnosticsExportStatus
            {
                ProviderAvailable = true,
                State = "Exported",
                Message = "saved"
            });

        var requested = PcCompatDiagnosticsExportRuntime.RequestExport("report.txt", "中文 diagnostics");
        var status = PcCompatDiagnosticsExportRuntime.GetStatus();

        Assert.That(requested, Is.True);
        Assert.That(actualName, Is.EqualTo("report.txt"));
        Assert.That(actualContent, Is.EqualTo("中文 diagnostics"));
        Assert.That(status.State, Is.EqualTo("Exported"));
    }
}

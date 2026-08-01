namespace StArray.ModManager.Manager;

public enum ModImportState
{
    Idle,
    Selecting,
    Importing,
    Imported,
    Cancelled,
    Error
}

public readonly record struct ModImportStatus(
    int Serial,
    ModImportState State,
    string Message,
    string? Path);

public interface IModManagerPlatformServices
{
    bool SupportsModZipImport { get; }

    bool IsOverlayVisible { get; }

    bool IsModalInputCaptureActive { get; }

    void RequestModZipImport();

    ModImportStatus GetModZipImportStatus();

    void BeginOverlayInputFrame();

    void AddOverlayInputRect(float x, float y, float width, float height);

    void EndOverlayInputFrame();

    void SetOverlayVisible(bool visible);

    void SetModalInputCapture(bool active, bool blockUnityEventSystem);

    bool ConsumeModalCloseRequest();
}

public sealed class NullModManagerPlatformServices : IModManagerPlatformServices
{
    public static readonly NullModManagerPlatformServices Instance = new();

    private NullModManagerPlatformServices()
    {
    }

    public bool SupportsModZipImport => false;

    public bool IsOverlayVisible => true;

    public bool IsModalInputCaptureActive => false;

    public void RequestModZipImport()
    {
    }

    public ModImportStatus GetModZipImportStatus()
        => new(0, ModImportState.Idle, string.Empty, null);

    public void BeginOverlayInputFrame()
    {
    }

    public void AddOverlayInputRect(float x, float y, float width, float height)
    {
    }

    public void EndOverlayInputFrame()
    {
    }

    public void SetOverlayVisible(bool visible)
    {
    }

    public void SetModalInputCapture(bool active, bool blockUnityEventSystem)
    {
    }

    public bool ConsumeModalCloseRequest() => false;
}

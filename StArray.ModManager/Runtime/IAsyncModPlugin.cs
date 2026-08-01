namespace StArray.ModManager.Runtime;

public readonly record struct ModLoadProgress(float Progress, string Stage);

/// <summary>
/// Optional two-phase loader. Background work runs without Unity/native calls;
/// CompleteLoad is invoked by the UI thread after the background phase finishes.
/// </summary>
public interface IAsyncModPlugin
{
    void BeginLoad();
    ModLoadProgress GetLoadProgress();
    bool IsLoadReady { get; }
    void CompleteLoad();
    void CancelLoad();
}

using System.Numerics;

namespace StArray.ModManager.Runtime;

/// <summary>
/// 可选接口 —— Mod 实现此接口可提供自定义 ImGui 设置面板
/// </summary>
public interface IModSettings
{
    /// <summary>绘制 Mod 专属设置面板</summary>
    void OnGui();
}

/// <summary>可选的设置窗口布局参数。</summary>
public interface IModSettingsLayout
{
    Vector2 PreferredWindowSize { get; }
    bool ShowSaveButton { get; }
}

public enum ModOriginalSettingsState
{
    Unavailable,
    Closed,
    Opening,
    Open,
    Faulted
}

public enum ModOriginalSettingsSurfaceKind
{
    None,
    UnityImGui,
    UnityCanvas
}

public readonly record struct ModOriginalSettingsSnapshot(
    ModOriginalSettingsState State,
    string? Error,
    ModOriginalSettingsSurfaceKind SurfaceKind = ModOriginalSettingsSurfaceKind.None);

/// <summary>
/// A settings surface rendered by the MOD inside Unity. ModManager only owns
/// opening, closing and fallback routing; it never calls the MOD GUI from the
/// ImGui render thread.
/// </summary>
public interface IModOriginalSettingsSurface
{
    bool TryOpenOriginalSettings(out string? error);
    void RequestCloseOriginalSettings();
    ModOriginalSettingsSnapshot SnapshotOriginalSettings();
}

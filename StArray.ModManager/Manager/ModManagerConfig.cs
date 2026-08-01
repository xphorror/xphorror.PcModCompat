using System.Text.Json;
using StArray.ModManager.Resources;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Manager;

/// <summary>
/// Mod 管理器全局配置 —— UI 设置 + 各 Mod 启用状态
/// </summary>
public class ModManagerConfig
{
    /// <summary>ModManager 界面语言。</summary>
    public string Language { get; set; } = L10n.ChineseLanguage;

    /// <summary>Mods 目录路径</summary>
    public string ModsDirectory { get; set; } = string.Empty;

    /// <summary>界面缩放 (FontGlobalScale)</summary>
    public float UiScale { get; set; } = 2f;

    /// <summary>滑动条抓取宽度</summary>
    public float GrabMinSize { get; set; } = 10f;

    /// <summary>滚动条宽度</summary>
    public float ScrollbarSize { get; set; } = 16f;

    /// <summary>触摸 KeyViewer 的 T1...TN 分配规则</summary>
    public PcCompatTouchLaneMappingMode TouchKeyViewerMappingMode { get; set; } =
        PcCompatTouchLaneMappingMode.ScreenRegions;

    /// <summary>触摸点模式下，抬起后暂不复用同一 T 槽的时间</summary>
    public int TouchKeyViewerContactReuseDelayMilliseconds { get; set; } =
        PcCompatTouchLaneMappingRuntime.DefaultTouchContactReuseDelayMilliseconds;

    /// <summary>Mod ID → 是否启用</summary>
    public Dictionary<string, bool> ModEnabled { get; set; } = new();

    private const string FileName = "modmanager_config.json";

    /// <summary>保存到指定目录（源生成器）</summary>
    public void Save(string directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory))
                return;

            Normalize();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var json = JsonSerializer.Serialize(this, ModManagerJsonContext.Default.ModManagerConfig);
            File.WriteAllText(path, json);
        }
        catch
        {
            // 配置保存失败不能影响 ModManager 主流程。
        }
    }

    /// <summary>从指定目录加载（源生成器），失败返回默认</summary>
    public static ModManagerConfig Load(string directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory))
                return new ModManagerConfig();

            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize(
                                 json,
                                 ModManagerJsonContext.Default.ModManagerConfig)
                             ?? new ModManagerConfig();
                config.Normalize();
                return config;
            }
        }
        catch
        {
            // ignore corrupt or inaccessible config
        }
        return new ModManagerConfig();
    }

    public void Normalize()
    {
        Language = L10n.NormalizeLanguage(Language);
        TouchKeyViewerMappingMode = PcCompatTouchLaneMappingRuntime.Normalize(
            TouchKeyViewerMappingMode);
        TouchKeyViewerContactReuseDelayMilliseconds =
            PcCompatTouchLaneMappingRuntime.NormalizeTouchContactReuseDelayMilliseconds(
                TouchKeyViewerContactReuseDelayMilliseconds);
    }
}

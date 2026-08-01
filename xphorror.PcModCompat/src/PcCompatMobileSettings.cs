using System.Text.Json;
using System.Text.Json.Serialization;
using StArray.ModManager.Manager;

namespace Xphorror.PcModCompat;

public sealed class PcCompatMobileSettings
{
    public bool ShowHud = true;
    public bool ShowAccuracy = true;
    public bool ShowXAccuracy = true;
    public bool ShowProgress = true;
    public bool ShowProgressBar = true;
    public bool ShowBpm = true;
    public bool ShowCombo = true;
    public bool ShowAttempt = true;
    public bool ShowMusicTime = true;
    public bool ShowMapTime;
    public bool ShowMapTimeIfMusicUnavailable = true;
    public bool ShowCheckpoint;
    public bool ShowBest;
    public bool ShowKeyViewer;
    public int TouchKeyCount = 10;
    public bool ShowLastJudgement = true;
    public bool ShowHitTiming = true;
    public bool ShowPlayerCount;
    public float HudScale = 1f;
    public float PositionX = 24f;
    public float PositionY = 72f;
    public float BackgroundOpacity = 0.8f;
    public bool ShowTechnicalDiagnostics;
    public bool ResourceChangerChangeRabbit = true;
    public bool ResourceChangerChangeBallColor = true;
    public bool ResourceChangerChangeTileColor = true;

    public void Normalize()
    {
        HudScale = Math.Clamp(HudScale, 0.5f, 2.5f);
        PositionX = Math.Clamp(PositionX, 0f, 4096f);
        PositionY = Math.Clamp(PositionY, 0f, 4096f);
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0f, 1f);
        TouchKeyCount = TouchKeyCount is 2 or 4 or 6 or 8 or 10
            ? TouchKeyCount
            : 10;
    }
}

public static class PcCompatMobileSettingsStore
{
    private const string SettingsDirectoryName = ".pccompat";
    private const string SettingsFileName = "mobile_settings.json";

    public static PcCompatMobileSettings Load(string modFolderPath)
    {
        var path = GetSettingsPath(modFolderPath);
        if (!File.Exists(path))
            return new PcCompatMobileSettings();

        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize(
                               json,
                               PcCompatMobileSettingsJsonContext.Default.PcCompatMobileSettings)
                           ?? new PcCompatMobileSettings();
            settings.Normalize();
            return settings;
        }
        catch (Exception ex)
        {
            Logger.Warn(nameof(PcCompatMobileSettingsStore), $"Mobile settings load failed: {ex.Message}");
            return new PcCompatMobileSettings();
        }
    }

    public static void Save(string modFolderPath, PcCompatMobileSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();

        var path = GetSettingsPath(modFolderPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(
            settings,
            PcCompatMobileSettingsJsonContext.Default.PcCompatMobileSettings);
        File.WriteAllText(path, json);
    }

    public static string GetSettingsPath(string modFolderPath)
        => Path.Combine(modFolderPath, SettingsDirectoryName, SettingsFileName);
}

[JsonSerializable(typeof(PcCompatMobileSettings))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    IncludeFields = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class PcCompatMobileSettingsJsonContext : JsonSerializerContext
{
}

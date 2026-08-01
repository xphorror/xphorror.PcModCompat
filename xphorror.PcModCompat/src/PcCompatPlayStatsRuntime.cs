using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using StArray.ModManager.Manager;

namespace Xphorror.PcModCompat;

public readonly record struct PcCompatPlayStatsSnapshot(
    bool Available,
    uint Attempts,
    float PreviousBest,
    float DisplayBest);

public sealed class PcCompatPlayStatsSession : IDisposable
{
    private readonly string _storePath;
    private readonly Dictionary<string, PcCompatPlayStatsEntry> _entries;
    private string _sessionKey = string.Empty;
    private uint _lastShowCount;
    private float _startProgress;
    private float _sessionBest;
    private float _previousBest;
    private bool _sessionActive;
    private bool _sessionAuto;
    private bool _dirty;

    public PcCompatPlayStatsSession(string modFolderPath)
    {
        _storePath = Path.Combine(modFolderPath, ".pccompat", "mobile_play_stats.json");
        _entries = LoadEntries(_storePath);
    }

    public PcCompatPlayStatsSnapshot Update(PcCompatOverlaySnapshot overlay)
    {
        if (!overlay.ProviderAvailable)
            return default;

        if (overlay.Visible && overlay.ShowCount != _lastShowCount)
            BeginSession(overlay);

        if (!_sessionActive)
            return default;

        var becameAuto = !_sessionAuto && (overlay.SessionAuto || overlay.LastPlayerHitIsAuto);
        if (becameAuto && _entries.TryGetValue(_sessionKey, out var activeEntry) && activeEntry.Attempts > 0)
        {
            activeEntry.Attempts--;
            _dirty = true;
            SaveIfDirty();
        }
        _sessionAuto |= overlay.SessionAuto || overlay.LastPlayerHitIsAuto;
        if (!_sessionAuto && float.IsFinite(overlay.Progress))
            _sessionBest = Math.Max(_sessionBest, Math.Clamp(overlay.Progress, 0f, 1f));

        if (!overlay.Visible || overlay.DeathCount > 0 || overlay.Progress >= 0.999999f)
            EndSession(removeEmptyAttempt: !overlay.Visible);

        var attempts = _entries.TryGetValue(_sessionKey, out var entry) ? entry.Attempts : 0u;
        var displayBest = _sessionAuto ? _previousBest : Math.Max(_previousBest, _sessionBest);
        return new PcCompatPlayStatsSnapshot(true, attempts, _previousBest, displayBest);
    }

    public void Dispose()
    {
        if (_sessionActive)
            EndSession(removeEmptyAttempt: false);
        SaveIfDirty();
    }

    private bool BeginSession(PcCompatOverlaySnapshot overlay)
    {
        var rawIdentity = PcCompatLevelIdentityRuntime.GetCurrent();
        if (string.IsNullOrWhiteSpace(rawIdentity))
            return false;

        if (_sessionActive)
            EndSession(removeEmptyAttempt: false);

        var identity = NormalizeIdentity(rawIdentity);
        _startProgress = Math.Clamp(overlay.StartProgress, 0f, 1f);
        var multiplier = float.IsFinite(overlay.SpeedMultiplier) && overlay.SpeedMultiplier > 0f
            ? overlay.SpeedMultiplier
            : 1f;
        _sessionKey = $"{identity}|start={_startProgress:F6}|speed={multiplier:F6}";
        ref var entry = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(
            _entries,
            _sessionKey,
            out var exists);
        if (!exists || entry == null)
            entry = new PcCompatPlayStatsEntry();

        _sessionAuto = overlay.SessionAuto;
        if (!_sessionAuto)
        {
            entry.Attempts++;
            _dirty = true;
            SaveIfDirty();
        }

        _previousBest = entry.Best;
        _sessionBest = _startProgress;
        _lastShowCount = overlay.ShowCount;
        _sessionActive = true;
        return true;
    }

    private void EndSession(bool removeEmptyAttempt)
    {
        if (!_sessionActive || !_entries.TryGetValue(_sessionKey, out var entry))
            return;

        if (!_sessionAuto)
        {
            if (removeEmptyAttempt && _sessionBest <= _startProgress + 0.000001f && entry.Attempts > 0)
                entry.Attempts--;
            if (_sessionBest > entry.Best)
                entry.Best = _sessionBest;
            _dirty = true;
        }

        _previousBest = entry.Best;
        _sessionActive = false;
        SaveIfDirty();
    }

    private void SaveIfDirty()
    {
        if (!_dirty)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            var json = JsonSerializer.Serialize(
                new PcCompatPlayStatsStore { Entries = _entries },
                PcCompatPlayStatsJsonContext.Default.PcCompatPlayStatsStore);
            File.WriteAllText(_storePath, json);
            _dirty = false;
        }
        catch (Exception ex)
        {
            Logger.Warn(nameof(PcCompatPlayStatsSession), $"Play stats save failed: {ex.Message}");
        }
    }

    private static Dictionary<string, PcCompatPlayStatsEntry> LoadEntries(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<string, PcCompatPlayStatsEntry>(StringComparer.Ordinal);

        try
        {
            var store = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                PcCompatPlayStatsJsonContext.Default.PcCompatPlayStatsStore);
            return store?.Entries != null
                ? new Dictionary<string, PcCompatPlayStatsEntry>(store.Entries, StringComparer.Ordinal)
                : new Dictionary<string, PcCompatPlayStatsEntry>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Logger.Warn(nameof(PcCompatPlayStatsSession), $"Play stats load failed: {ex.Message}");
            return new Dictionary<string, PcCompatPlayStatsEntry>(StringComparer.Ordinal);
        }
    }

    private static string NormalizeIdentity(string identity)
    {
        if (identity.StartsWith("path:", StringComparison.Ordinal))
        {
            var path = identity[5..];
            try
            {
                if (File.Exists(path))
                {
                    using var stream = File.OpenRead(path);
                    return "sha256:" + Convert.ToHexString(SHA256.HashData(stream));
                }
            }
            catch
            {
            }
        }

        return string.IsNullOrWhiteSpace(identity) ? "unknown-level" : identity;
    }
}

public sealed class PcCompatPlayStatsStore
{
    public Dictionary<string, PcCompatPlayStatsEntry> Entries { get; init; } = new(StringComparer.Ordinal);
}

public sealed class PcCompatPlayStatsEntry
{
    public uint Attempts { get; set; }
    public float Best { get; set; }
}

[JsonSerializable(typeof(PcCompatPlayStatsStore))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class PcCompatPlayStatsJsonContext : JsonSerializerContext
{
}

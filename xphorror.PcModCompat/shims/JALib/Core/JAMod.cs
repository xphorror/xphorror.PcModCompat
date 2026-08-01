using JALib.Core.Patch;
using JALib.Core.Setting;
using JALib.Tools;
using Newtonsoft.Json.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityModManagerNet;

namespace JALib.Core;

public abstract class JAMod
{
    private const float CompatFixedDeltaTime = 0.02f;
    private const int MaximumFixedUpdatesPerFrame = 8;
    private static readonly Dictionary<string, JAMod> Mods = new(StringComparer.Ordinal);
    private readonly Type? _settingType;
    private JAModSetting? _modSetting;
    private bool _compatEnabled;
    private bool _compatUnloaded;
    private bool _compatSettingsVisible;
    private float _compatFixedAccumulator;
    private long _compatLoggedExceptionCount;
    private string? _compatLastException;
    private bool _modEntryRegisteredByCompat;
    private SystemLanguage? _customLanguage;

    protected string Discord = "https://discord.jongyeol.kr/";

    protected JAMod()
        : this(null)
    {
    }

    protected JAMod(Type? settingType)
    {
        _settingType = settingType;
        BindStaticInstance();
        Patcher = new JAPatcher(this);
        Setting = new JASetting(this);
        Localization = new JALocalization(this);
        ModEntry = CreateModEntry(AppContext.BaseDirectory);
        lock (Mods)
            Mods[Name] = this;
    }

    [Obsolete("Deprecated. Use other constructor instead.", true)]
    protected JAMod(
        UnityModManager.ModEntry modEntry,
        bool localization,
        Type? settingType = null,
        string? settingPath = null,
        string? discord = null,
        int gid = -1)
        : this(settingType)
    {
        ArgumentNullException.ThrowIfNull(modEntry);
        var previousName = Name;
        ModEntry = modEntry;
        Path = modEntry.Path;
        Discord = discord ?? Discord;
        Gid = gid;
        _modSetting = new JAModSetting(
            this,
            settingPath ?? System.IO.Path.Combine(Path, "Settings.json"),
            settingType);
        _customLanguage = _modSetting.CustomLanguage;
        Setting = _modSetting.Setting;
        lock (Mods)
        {
            if (ReferenceEquals(Mods.GetValueOrDefault(previousName), this))
                Mods.Remove(previousName);
            Mods[Name] = this;
        }
    }

    public string Name => string.IsNullOrWhiteSpace(ModEntry?.Info.Id)
        ? GetType().Name
        : ModEntry.Info.Id;
    public string Path { get; private set; } = AppContext.BaseDirectory;
    public bool Enabled => _compatEnabled;
    public bool Active => _compatEnabled && !_compatUnloaded;
    public JAPatcher Patcher { get; }
    public JASetting Setting { get; private set; }
    public JASetting ModSetting => _modSetting ?? Setting;
    public List<Feature> Features { get; } = new();
    internal Dictionary<Type, MultiFeature> MultiFeatures { get; } = new();
    public JALocalization Localization { get; }
    internal int Gid { get; private set; } = -1;
    public UnityModManager.ModEntry ModEntry { get; private set; }
    public UnityModManager.ModEntry.ModLogger Logger => ModEntry.Logger;
    public Version Version => ModEntry.Version;
    protected Version? LatestVersion => null;
    public bool IsLatest => LatestVersion == null || LatestVersion <= Version;
    protected SystemLanguage[] AvailableLanguages
        => _modSetting?.AvailableLanguages ?? [];
    protected internal SystemLanguage? CustomLanguage
    {
        get => _modSetting?.CustomLanguage ?? _customLanguage;
        set
        {
            _customLanguage = value;
            if (_modSetting != null)
                _modSetting.CustomLanguage = value;
            Localization.Load(Path);
            CompatLocalizationUpdated();
        }
    }

    public static JAMod? GetMods(string name)
    {
        lock (Mods)
            return Mods.GetValueOrDefault(name);
    }

    public static ICollection<JAMod> GetMods()
    {
        lock (Mods)
            return Mods.Values.ToArray();
    }

    public void CompatSetup(string? modPath = null)
    {
        ObjectDisposedException.ThrowIf(_compatUnloaded, this);
        if (!string.IsNullOrWhiteSpace(modPath))
            Path = modPath;
        if (!string.Equals(ModEntry.Path, Path, StringComparison.Ordinal))
            ModEntry = CreateModEntry(Path, ModEntry.Info);
        RegisterModEntry();

        JALib.Tools.MainThread.Register(this);
        using var ownerScope = JALib.Tools.MainThread.EnterOwner(this);
        Gid = ReadLocalizationGid(Path);
        _modSetting = new JAModSetting(
            this,
            System.IO.Path.Combine(Path, "Settings.json"),
            _settingType);
        if (_customLanguage.HasValue)
            _modSetting.CustomLanguage = _customLanguage;
        else
            _customLanguage = _modSetting.CustomLanguage;
        Setting = _modSetting.Setting;
        Localization.Load(Path);
        OnSetup();
        CompatLocalizationUpdated();
        Patcher.Patch();
        SaveSetting();
    }

    public void CompatEnable()
    {
        ObjectDisposedException.ThrowIf(_compatUnloaded, this);
        if (_compatEnabled)
            return;
        _compatEnabled = true;
        JALib.Tools.MainThread.Activate(this);
        using var ownerScope = JALib.Tools.MainThread.EnterOwner(this);
        OnEnable();
        ObserveLifecycleTask(OnEnableAsync(), "Enable");
        Patcher.Patch();
        foreach (var feature in Features)
            feature.CompatEnable();
    }

    public void CompatUpdate(float deltaTime)
    {
        if (!_compatEnabled)
            return;
        JALib.Tools.MainThread.Drain();
        using var ownerScope = JALib.Tools.MainThread.EnterOwner(this);
        _compatFixedAccumulator = Math.Min(
            _compatFixedAccumulator + deltaTime,
            CompatFixedDeltaTime * MaximumFixedUpdatesPerFrame);
        var fixedUpdates = 0;
        while (_compatFixedAccumulator + float.Epsilon >= CompatFixedDeltaTime &&
               fixedUpdates++ < MaximumFixedUpdatesPerFrame)
        {
            OnFixedUpdate(CompatFixedDeltaTime);
            _compatFixedAccumulator -= CompatFixedDeltaTime;
        }
        OnUpdate(deltaTime);
        foreach (var feature in Features)
            feature.CompatUpdate(deltaTime);
        OnLateUpdate(deltaTime);
    }

    public void CompatDisable()
    {
        if (!_compatEnabled)
            return;
        try
        {
            using var ownerScope = JALib.Tools.MainThread.EnterOwner(this);
            foreach (var feature in Features)
                feature.CompatDisable();
            Patcher.Unpatch();
            OnDisable();
            ObserveLifecycleTask(OnDisableAsync(), "Disable");
        }
        finally
        {
            _compatEnabled = false;
            JALib.Tools.MainThread.Deactivate(this);
        }
    }

    public void CompatUnload()
    {
        if (_compatUnloaded)
            return;
        if (_compatSettingsVisible)
            CompatCloseGUI();

        JALib.Tools.MainThread.Activate(this);
        try
        {
            using var ownerScope = JALib.Tools.MainThread.EnterOwner(this);
            if (_compatEnabled)
            {
                foreach (var feature in Features)
                    feature.CompatDisable();
                Patcher.Unpatch();
                OnDisable();
                ObserveLifecycleTask(OnDisableAsync(), "Disable");
                _compatEnabled = false;
            }
            foreach (var feature in Features.ToArray())
                feature.CompatUnload();
            Patcher.Dispose();
            _modSetting?.Dispose();
            OnUnload();
            lock (Mods)
            {
                if (ReferenceEquals(Mods.GetValueOrDefault(Name), this))
                    Mods.Remove(Name);
            }
            if (_modEntryRegisteredByCompat)
            {
                UnityModManager.modEntries.Remove(ModEntry);
                _modEntryRegisteredByCompat = false;
            }
        }
        finally
        {
            _compatUnloaded = true;
            JALib.Tools.MainThread.Deactivate(this);
        }
    }

    public void Enable() => CompatEnable();
    public void Disable() => CompatDisable();
    public void Inactive() => CompatDisable();

    public bool CompatSettingsVisible => _compatSettingsVisible;

    public void CompatOpenGUI()
    {
        if (_compatSettingsVisible)
            return;
        using var ownerScope = JALib.Tools.MainThread.EnterOwner(this);
        UnityModManagerNet.PcCompatSettingsUiBridge.ReleaseInputFocus();
        _compatSettingsVisible = true;
        try
        {
            OnShowGUI();
            foreach (var feature in Features)
                feature.CompatOnShowGUI();
        }
        catch (Exception exception)
        {
            LogReportException("Failed to Show GUI", exception);
        }
    }

    public void CompatOnGUI()
    {
        if (!_compatSettingsVisible)
            return;
        using var ownerScope = JALib.Tools.MainThread.EnterOwner(this);
        UnityModManagerNet.PcCompatSettingsUiBridge.BeginFrame(Name);
        try
        {
            OnGUI();
            foreach (var feature in Features)
                feature.CompatOnGUI();
            OnGUIBehind();
        }
        catch
        {
            try
            {
                UnityModManagerNet.PcCompatSettingsUiBridge.AbortFrame();
            }
            catch (Exception cleanupException)
            {
                Console.Error.WriteLine(
                    "[PcModCompat][JALib][WARN] settings frame abort cleanup failed: " +
                    cleanupException);
            }
            throw;
        }
        var action = UnityModManagerNet.PcCompatSettingsUiBridge.EndFrame();
        if ((action & UnityModManagerNet.PcCompatSettingsFrameAction.Save) != 0)
            CompatSaveGUI();
        if ((action & UnityModManagerNet.PcCompatSettingsFrameAction.Close) != 0)
            CompatCloseGUI();
    }

    public void CompatSaveGUI()
        => SaveSetting();

    public void CompatCloseGUI()
    {
        if (!_compatSettingsVisible)
            return;
        using var ownerScope = JALib.Tools.MainThread.EnterOwner(this);
        try
        {
            try
            {
                OnHideGUI();
                foreach (var feature in Features)
                    feature.CompatOnHideGUI();
            }
            catch (Exception exception)
            {
                LogReportException("Failed to Hide GUI", exception);
            }
            SaveSetting();
        }
        finally
        {
            UnityModManagerNet.PcCompatSettingsUiBridge.ReleaseInputFocus();
            _compatSettingsVisible = false;
        }
    }

    protected virtual void OnSetup() { }
    protected virtual void OnEnable() { }
    protected virtual Task OnEnableAsync() => Task.CompletedTask;
    protected virtual void OnDisable() { }
    protected virtual Task OnDisableAsync() => Task.CompletedTask;
    protected virtual void OnUnload() { }
    protected virtual void OnUpdate(float deltaTime) { }
    protected virtual void OnFixedUpdate(float deltaTime) { }
    protected virtual void OnLateUpdate(float deltaTime) { }
    protected virtual void OnGUI() { }
    protected virtual void OnShowGUI() { }
    protected virtual void OnHideGUI() { }
    protected virtual void OnGUIBehind() { }

    public void AddFeature(params Feature[] features)
    {
        foreach (var feature in features)
        {
            feature.Mod = this;
            Features.Add(feature);
            if (_compatEnabled)
                feature.CompatEnable();
        }
    }

    public void SaveSetting()
        => _modSetting?.Save();

    private void ObserveLifecycleTask(Task? task, string phase)
    {
        if (task == null)
        {
            LogReportException(
                $"Failed to Async {phase} JAMod {Name}",
                new InvalidOperationException($"On{phase}Async returned null."));
            return;
        }
        task.OnCompleted(
            this,
            completed =>
            {
                if (!completed.IsFaulted || completed.Exception == null)
                    return;
                var exception = completed.Exception.InnerExceptions.Count == 1
                    ? completed.Exception.InnerExceptions[0]
                    : completed.Exception;
                LogReportException($"Failed to Async {phase} JAMod {Name}", exception);
            },
            JALib.Tools.JATask.CompleteFlag.None);
    }

    internal JObject? GetFeatureSettingObject(string name)
        => _modSetting?.GetFeatureObject(name);

    public void Log(string message) => WriteLog("INFO", message);
    public void Log(object? message) => Log(message?.ToString() ?? string.Empty);
    public void Log(object? message, int stackTraceSkip) => Log(message);
    public void Warning(string message) => WriteLog("WARN", message);
    public void Warning(object? message) => Warning(message?.ToString() ?? string.Empty);
    public void Warning(object? message, int stackTraceSkip) => Warning(message);
    public void Error(string message) => WriteLog("ERROR", message);
    public void Error(object? message) => Error(message?.ToString() ?? string.Empty);
    public void Error(object? message, int stackTraceSkip) => Error(message);
    public void Critical(object? message) => WriteLog("CRITICAL", message?.ToString() ?? string.Empty);
    public void Critical(object? message, int stackTraceSkip) => Critical(message);
    public void NativeLog(object? message) => WriteLog("NATIVE", message?.ToString() ?? string.Empty);
    public void NativeLog(object? message, int stackTraceSkip) => NativeLog(message);
    public void LogException(Exception exception) => RecordException(null, exception);
    public void LogException(Exception exception, int stackTraceSkip) => LogException(exception);
    public void LogException(string message, Exception exception) => LogException((object?)message, exception);
    public void LogException(string message, Exception exception, int stackTraceSkip)
        => LogException(message, exception);
    public void LogException(object? message, Exception exception) => RecordException(message, exception);

    public void ReportException(Exception exception)
        => ReportException(exception, [this]);

    public void ReportException(string key, Exception exception)
        => ReportException(key, exception, [this]);

    public void ReportException(Exception exception, JAMod[] mods)
        => ReportException(null, exception, mods);

    public void ReportException(string? key, Exception exception, JAMod[] mods)
    {
        try
        {
            OnReportException(key, exception, mods);
        }
        catch (Exception reportException)
        {
            LogException("Failed to Report Exception Event", reportException);
        }
    }

    protected virtual void OnReportException(string? key, Exception exception, JAMod[] mods) { }

    public void LogReportException(Exception exception)
    {
        LogException(exception);
        ReportException(exception);
    }

    public void LogReportException(Exception exception, int stackTraceSkip)
    {
        LogException(exception, stackTraceSkip + 1);
        ReportException(exception);
    }

    public void LogReportException(string message, Exception exception)
    {
        LogException(message, exception);
        ReportException(message, exception);
    }

    public void LogReportException(string message, Exception exception, int stackTraceSkip)
    {
        LogException(message, exception, stackTraceSkip + 1);
        ReportException(message, exception);
    }

    public void LogReportException(Exception exception, JAMod[] mods)
    {
        LogException(exception);
        ReportException(exception, mods);
    }

    public void LogReportException(Exception exception, JAMod[] mods, int stackTraceSkip)
    {
        LogException(exception, stackTraceSkip + 1);
        ReportException(exception, mods);
    }

    public void LogReportException(string message, Exception exception, JAMod[] mods)
    {
        LogException(message, exception);
        ReportException(message, exception, mods);
    }

    public void LogReportException(
        string message,
        Exception exception,
        JAMod[] mods,
        int stackTraceSkip)
    {
        LogException(message, exception, stackTraceSkip + 1);
        ReportException(message, exception, mods);
    }

    public void LogReportException(object? message, Exception exception)
    {
        LogException(message, exception);
        ReportException(message?.ToString() ?? string.Empty, exception);
    }

    public void DownloadComplete()
        => RecordException(
            "JAMod.DownloadComplete is unavailable on Android PcModCompat",
            new NotSupportedException("JALib desktop self-update and reload are unavailable."));

    internal void CompatLocalizationUpdated()
        => OnLocalizationUpdate();

    public string GetCompatDiagnosticStatus()
    {
        var builder = new StringBuilder(512);
        builder.AppendLine(
            $"compatEnabled={_compatEnabled} features={Features.Count}" +
            $" loggedExceptions={Interlocked.Read(ref _compatLoggedExceptionCount)}" +
            $" localizationGid={Gid}" +
            $" localization={Localization.LoadedPath ?? "none"}" +
            $" language={Localization.SelectedLanguage ?? "none"}");
        foreach (var feature in Features)
        {
            builder.Append(
                $"feature type={feature.GetType().FullName}" +
                $" name={feature.Name}" +
                $" enabled={feature.Enabled}" +
                $" hostActive={feature.CompatHostActive}");
            try
            {
                var threadFields = feature.GetType()
                    .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(field => typeof(Thread).IsAssignableFrom(field.FieldType))
                    .ToArray();
                foreach (var field in threadFields)
                {
                    var thread = field.GetValue(feature) as Thread;
                    builder.Append(
                        thread == null
                            ? $" thread[{field.Name}]=null"
                            : $" thread[{field.Name}]=id:{thread.ManagedThreadId},alive:{thread.IsAlive},state:{thread.ThreadState}");
                }
            }
            catch (Exception exception)
            {
                builder.Append($" threadAuditError={exception.GetBaseException().Message}");
            }
            builder.AppendLine();
        }
        builder.AppendLine("lastLoggedExceptionBegin");
        builder.AppendLine(Volatile.Read(ref _compatLastException) ?? "none");
        builder.Append("lastLoggedExceptionEnd");
        return builder.ToString();
    }

    private void RecordException(object? message, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var text = message == null
            ? exception.ToString()
            : $"{message}: {exception}";
        Volatile.Write(ref _compatLastException, text);
        var count = Interlocked.Increment(ref _compatLoggedExceptionCount);
        // A failing MOD worker can retry every millisecond. Preserve the full
        // latest exception for diagnostics while keeping Logcat bounded.
        if (count == 1 || (count & (count - 1)) == 0)
        {
            Console.WriteLine(
                $"[PcModCompat][JALib][ERROR][{GetType().FullName}]" +
                $" repeated={count} {text}");
        }
    }

    private void WriteLog(string level, string message)
        => Console.WriteLine(
            $"[PcModCompat][JALib][{level}][{GetType().FullName}] {message}");

    private UnityModManager.ModEntry CreateModEntry(
        string path,
        UnityModManager.ModInfo? source = null)
    {
        var assemblyVersion = GetType().Assembly.GetName().Version ?? new Version(0, 0);
        var info = new UnityModManager.ModInfo
        {
            Id = string.IsNullOrWhiteSpace(source?.Id) ? GetType().Name : source.Id,
            DisplayName = string.IsNullOrWhiteSpace(source?.DisplayName)
                ? GetType().Name
                : source.DisplayName,
            Author = source?.Author ?? string.Empty,
            Version = string.IsNullOrWhiteSpace(source?.Version)
                ? assemblyVersion.ToString()
                : source.Version,
            ManagerVersion = source?.ManagerVersion ?? string.Empty,
            GameVersion = source?.GameVersion ?? string.Empty,
            Requirements = source?.Requirements,
            LoadAfter = source?.LoadAfter,
            AssemblyName = source?.AssemblyName ?? GetType().Assembly.GetName().Name ?? string.Empty,
            EntryMethod = source?.EntryMethod ?? string.Empty,
            HomePage = source?.HomePage ?? Discord,
            Repository = source?.Repository ?? string.Empty,
            ContentType = source?.ContentType ?? string.Empty,
            IsCheat = source?.IsCheat ?? false
        };
        return new UnityModManager.ModEntry(info, path);
    }

    private void RegisterModEntry()
    {
        if (UnityModManager.modEntries.Contains(ModEntry))
            return;
        var existing = UnityModManager.FindMod(ModEntry.Info.Id);
        if (existing != null)
        {
            ModEntry = existing;
            return;
        }
        UnityModManager.modEntries.Add(ModEntry);
        _modEntryRegisteredByCompat = true;
    }

    private void BindStaticInstance()
    {
        var field = GetType().GetField("Instance",
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static);
        if (field != null && field.FieldType.IsAssignableFrom(GetType()))
            field.SetValue(null, this);
    }

    private static int ReadLocalizationGid(string modPath)
    {
        var path = System.IO.Path.Combine(modPath, "JAModInfo.json");
        if (!File.Exists(path))
            return -1;
        try
        {
            var json = JObject.Parse(File.ReadAllText(path));
            return json[nameof(Gid)]?.Value<int>() ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    protected virtual void OnLocalizationUpdate() { }

}

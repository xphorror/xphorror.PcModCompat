using System.Reflection;

namespace UnityModManagerNet;

public partial class UnityModManager
{
    public static readonly List<ModEntry> modEntries = new();
    public static string modsPath = string.Empty;
    public static bool IsSupportOnSessionStart { get; set; }
    public static bool IsSupportOnSessionStop { get; set; }
    public static event Action<ModEntry>? ModLoadCompleted;

    public static ModEntry? FindMod(string id)
        => modEntries.FirstOrDefault(mod => string.Equals(mod.Info.Id, id, StringComparison.OrdinalIgnoreCase));

    public class ModInfo : IEquatable<ModInfo>
    {
        public string Id = string.Empty;
        public string DisplayName = string.Empty;
        public string Author = string.Empty;
        public string Version = string.Empty;
        public string ManagerVersion = string.Empty;
        public string GameVersion = string.Empty;
        public string[]? Requirements;
        public string[]? LoadAfter;
        public string AssemblyName = string.Empty;
        public string EntryMethod = string.Empty;
        public string HomePage = string.Empty;
        public string Repository = string.Empty;
        public string ContentType = string.Empty;
        public bool IsCheat = true;

        public bool Equals(ModInfo? other)
            => other != null && string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object? obj)
            => obj is ModInfo other && Equals(other);

        public override int GetHashCode()
            => StringComparer.OrdinalIgnoreCase.GetHashCode(Id);

        public static implicit operator bool(ModInfo? exists)
            => exists != null;
    }

    public partial class ModEntry
    {
        public readonly ModInfo Info;
        public readonly string Path;
        public readonly ModLogger Logger;
        public readonly Version Version;
        public Version? NewestVersion;
        public readonly Dictionary<string, Version> Requirements = new();
        public readonly List<string> LoadAfter = new();
        public string CustomRequirements = string.Empty;
        public KeyBinding Hotkey = new();
        public bool HasUpdate;
        public bool CanReload { get; set; }
        public bool Enabled = true;
        public Assembly? Assembly { get; internal set; }
        public bool Loaded => Assembly != null || !HasAssembly;
        public bool Started { get; private set; }
        public bool ErrorOnLoading { get; private set; }
        public bool HasAssembly => !string.IsNullOrEmpty(Info.AssemblyName) || !string.IsNullOrEmpty(Info.EntryMethod);
        public bool Toggleable => OnToggle != null || !HasAssembly;

        public Func<ModEntry, bool>? OnUnload;
        public Func<ModEntry, bool, bool>? OnToggle;
        public Action<ModEntry>? OnGUI;
        public Action<ModEntry>? OnFixedGUI;
        public Action<ModEntry>? OnShowGUI;
        public Action<ModEntry>? OnHideGUI;
        public Action<ModEntry>? OnSaveGUI;
        public Action<ModEntry, float>? OnUpdate;
        public Action<ModEntry, float>? OnLateUpdate;
        public Action<ModEntry, float>? OnFixedUpdate;
        public Action<ModEntry>? OnSessionStart;
        public Action<ModEntry>? OnSessionStop;

        private bool _active;
        private bool _compatSettingsVisible;

        public ModEntry(ModInfo info, string path)
        {
            Info = info;
            Path = path;
            Logger = new ModLogger(info.Id);
            Version = Version.TryParse(info.Version, out var version) ? version : new Version(0, 0);
            if (info.Requirements != null)
            {
                foreach (var requirement in info.Requirements)
                    Requirements[requirement] = new Version(0, 0);
            }
            if (info.LoadAfter != null)
                LoadAfter.AddRange(info.LoadAfter);
        }

        public bool Active
        {
            get => _active;
            set
            {
                if (_active == value)
                    return;

                if (OnToggle != null && !OnToggle(this, value))
                    return;

                _active = value;
            }
        }

        public bool CompatSettingsVisible => _compatSettingsVisible;

        public void CompatOpenGUI()
        {
            if (_compatSettingsVisible)
                return;
            PcCompatSettingsUiBridge.ReleaseInputFocus();
            _compatSettingsVisible = true;
            OnShowGUI?.Invoke(this);
        }

        public void CompatOnGUI()
        {
            if (!_compatSettingsVisible)
                return;
            PcCompatSettingsUiBridge.BeginFrame(Info.DisplayName);
            try
            {
                OnGUI?.Invoke(this);
                OnFixedGUI?.Invoke(this);
            }
            catch
            {
                PcCompatSettingsUiBridge.AbortFrame();
                throw;
            }
            var action = PcCompatSettingsUiBridge.EndFrame();
            if ((action & PcCompatSettingsFrameAction.Save) != 0)
                CompatSaveGUI();
            if ((action & PcCompatSettingsFrameAction.Close) != 0)
                CompatCloseGUI();
        }

        public void CompatSaveGUI()
            => OnSaveGUI?.Invoke(this);

        public void CompatCloseGUI()
        {
            if (!_compatSettingsVisible)
                return;
            try
            {
                OnHideGUI?.Invoke(this);
            }
            finally
            {
                PcCompatSettingsUiBridge.ReleaseInputFocus();
                _compatSettingsVisible = false;
            }
        }

        public bool Load()
        {
            Started = true;
            Logger.Log("Load requested through xphorror PcModCompat UnityModManager shim.");
            var handlers = ModLoadCompleted;
            if (handlers != null)
            {
                foreach (Action<ModEntry> handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(this);
                    }
                    catch (Exception exception)
                    {
                        Logger.LogException("Mod load completion callback failed", exception);
                    }
                }
            }
            return true;
        }
    }
}

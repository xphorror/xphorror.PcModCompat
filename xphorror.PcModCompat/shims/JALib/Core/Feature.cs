using JALib.Core.Setting;
using JALib.Core.Patch;
using Newtonsoft.Json.Linq;

namespace JALib.Core;

public abstract class Feature
{
    private bool _compatHostActive;
    private bool _enabled;
    private bool _compatSettingsExpanded;
    private readonly bool _compatCanExpand;
    private readonly JObject? _compatSettingObject;
    private byte _compatGuiFailureCount;
    private bool _compatCollapsePending;
    protected readonly List<MultiFeature> MultiFeatures = [];

    // Real JALib exposes a public setter that routes into Enable()/Disable().
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            if (value)
                Enable();
            else
                Disable();
            Mod?.SaveSetting();
        }
    }

    public Feature()
        : this(null, null, true, null, null)
    {
    }

    public Feature(Type? settingType)
        : this(null, null, true, null, settingType)
    {
    }

    public Feature(JAMod? mod, string? name, Type? settingType = null)
        : this(mod, name, true, null, settingType)
    {
    }

    public Feature(JAMod? mod, string? name, bool canEnable, Type? patchType = null, Type? settingType = null)
    {
        Mod = mod;
        Name = name ?? GetType().Name;
        CanEnable = canEnable;
        PatchType = patchType;
        SettingType = settingType;
        var owner = mod ?? NullMod.Instance;
        _compatSettingObject = owner.GetFeatureSettingObject(Name);
        _enabled = ReadEnabled(_compatSettingObject, true);
        Patcher = new JAPatcher(owner);
        // Real JALib registers the patch class on the feature's own patcher in the ctor.
        if (patchType != null)
            Patcher.AddPatch(patchType);
        Setting = CreateSetting(owner, settingType, _compatSettingObject);
        _compatCanExpand = IsOverridden(nameof(OnGUI)) ||
                           IsOverridden(nameof(OnShowGUI)) ||
                           IsOverridden(nameof(OnHideGUI));
    }

    public JAMod? Mod { get; internal set; }
    // Official v42/v44 emit Name/Setting/Patcher with no visible setter (private set);
    // a protected setter changes the manifest accessor shape to set=True.
    public string Name { get; private set; }
    public Type? PatchType { get; protected set; }
    public Type? SettingType { get; protected set; }
    public JASetting Setting { get; private set; }
    public JAPatcher Patcher { get; private set; }
    public bool CompatHostActive => _compatHostActive;
    public bool Active => _compatHostActive && _enabled;
    public bool CanEnable { get; protected set; }

    public void Enable()
    {
        if (_enabled)
            return;
        _enabled = true;
        if (_compatHostActive)
        {
            Patcher.Patch();
            OnEnable();
            foreach (var multiFeature in MultiFeatures)
                multiFeature.ActiveFeature(this);
        }
    }

    public void Disable()
    {
        if (!_enabled)
            return;
        if (_compatHostActive)
        {
            Patcher.Unpatch();
            OnDisable();
            foreach (var multiFeature in MultiFeatures)
                multiFeature.InactiveFeature(this);
        }
        _enabled = false;
    }

    public void CompatEnable()
    {
        if (_compatHostActive)
            return;
        _compatHostActive = true;
        if (_enabled)
        {
            // Real JALib Feature.Enable() patches first, then runs OnEnable().
            Patcher.Patch();
            OnEnable();
            foreach (var multiFeature in MultiFeatures)
                multiFeature.ActiveFeature(this);
        }
    }

    public void CompatUpdate(float deltaTime)
    {
        if (_compatHostActive && _enabled)
            OnUpdate(deltaTime);
    }

    public void CompatDisable()
    {
        if (!_compatHostActive)
            return;
        if (_enabled)
        {
            Patcher.Unpatch();
            OnDisable();
            foreach (var multiFeature in MultiFeatures)
                multiFeature.InactiveFeature(this);
        }
        _compatHostActive = false;
    }

    internal void CompatUnload()
    {
        CompatDisable();
        Patcher.Dispose();
        Setting.Dispose();
        OnUnload();
        Mod = null;
    }

    public void CompatOnGUI()
    {
        if (_compatCollapsePending &&
            UnityModManagerNet.PcCompatSettingsUiBridge.CanApplyStructureChanges())
        {
            _compatCollapsePending = false;
            _compatGuiFailureCount = 0;
            if (_compatSettingsExpanded)
            {
                _compatSettingsExpanded = false;
                OnHideGUI();
            }
        }
        var localizationKey = $"Feature.{Name}";
        var label = Mod?.Localization.TryGet(localizationKey, out var localizedName) == true
            ? localizedName
            : Name;
        var section = UnityModManagerNet.PcCompatSettingsUiBridge.Section(
            _enabled,
            _compatSettingsExpanded,
            CanEnable,
            _compatCanExpand,
            label);
        var enabled = (section & 1) != 0;
        var expanded = (section & 2) != 0;
        if (enabled != _enabled)
        {
            Enabled = enabled;
            if (enabled && _compatCanExpand)
                expanded = true;
        }
        if (!_enabled)
            return;

        if (expanded != _compatSettingsExpanded)
        {
            _compatSettingsExpanded = expanded;
            if (_compatSettingsExpanded)
                CompatOnShowGUI();
            else
            {
                _compatCollapsePending = false;
                _compatGuiFailureCount = 0;
                try
                {
                    OnHideGUI();
                }
                catch (Exception exception)
                {
                    Mod?.LogReportException("Error OnHideGUI", exception);
                }
            }
        }
        if (_compatSettingsExpanded)
        {
            UnityModManagerNet.PcCompatSettingsUiBridge.BeginSectionBody();
            try
            {
                OnGUI();
                _compatGuiFailureCount = 0;
            }
            catch (Exception exception)
            {
                Mod?.LogReportException($"Error OnGUI in {Name}", exception);
                if (++_compatGuiFailureCount > 3)
                    _compatCollapsePending = true;
            }
            finally
            {
                UnityModManagerNet.PcCompatSettingsUiBridge.EndSectionBody();
            }
        }
    }

    internal void CompatOnShowGUI()
    {
        if (!_enabled || !_compatSettingsExpanded)
            return;
        try
        {
            OnShowGUI();
        }
        catch (Exception exception)
        {
            Mod?.LogReportException("Error OnShowGUI", exception);
            _compatSettingsExpanded = false;
        }
    }

    public void CompatOnHideGUI()
    {
        try
        {
            if (_enabled && _compatSettingsExpanded)
                OnHideGUI();
        }
        catch (Exception exception)
        {
            Mod?.LogReportException("Error OnHideGUI", exception);
        }
        finally
        {
            _compatCollapsePending = false;
            _compatGuiFailureCount = 0;
        }
    }

    internal void CompatPutSettingData()
    {
        if (_compatSettingObject == null)
            return;
        _compatSettingObject[nameof(Enabled)] = _enabled;
        if (SettingType == null)
            return;
        Setting.PutFieldData();
        _compatSettingObject[nameof(Setting)] = Setting.CompatJsonObject;
    }

    internal void CompatRemoveSettingData()
    {
        if (_compatSettingObject == null)
            return;
        _compatSettingObject.Remove(nameof(Enabled));
        if (SettingType != null)
            Setting.RemoveFieldData();
    }

    public void Invoke(string methodName)
    {
        var method = GetType().GetMethod(methodName,
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);
        method?.Invoke(this, null);
    }

    protected virtual void OnEnable() { }
    protected virtual void OnDisable() { }
    protected virtual void OnUnload() { }
    protected virtual void OnUpdate(float deltaTime) { }
    protected virtual void OnGUI() { }
    protected virtual void OnShowGUI() { }
    protected virtual void OnHideGUI() { }

    protected void Inactive() => Disable();

    protected void AddMultiFeatures(params Type[] multiFeatureTypes)
    {
        ArgumentNullException.ThrowIfNull(multiFeatureTypes);
        foreach (var type in multiFeatureTypes)
            AddMultiFeatures(type);
    }

    protected void AddMultiFeatures(Type multiFeatureType)
    {
        ArgumentNullException.ThrowIfNull(multiFeatureType);
        var mod = Mod ?? throw new InvalidOperationException(
            $"Feature '{Name}' has no owning JAMod.");
        MultiFeatures.Add(MultiFeature.GetMultiFeaturePatch(mod, multiFeatureType));
    }

    private static JASetting CreateSetting(
        JAMod mod,
        Type? settingType,
        JObject? featureObject)
    {
        if (settingType == null)
            return new JASetting(mod);
        var settingObject = featureObject?[nameof(Setting)] as JObject;
        if (featureObject != null && settingObject == null)
        {
            settingObject = new JObject();
            featureObject[nameof(Setting)] = settingObject;
        }
        return JASetting.Create(settingType, mod, settingObject);
    }

    private static bool ReadEnabled(JObject? featureObject, bool defaultValue)
    {
        if (featureObject == null ||
            !featureObject.TryGetValue(nameof(Enabled), out var token))
            return defaultValue;
        try
        {
            return token.Value<bool>();
        }
        finally
        {
            featureObject.Remove(nameof(Enabled));
        }
    }

    private bool IsOverridden(string methodName)
    {
        var method = GetType().GetMethod(
            methodName,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        return method?.DeclaringType != typeof(Feature);
    }

    private sealed class NullMod : JAMod
    {
        public static readonly NullMod Instance = new();
    }
}

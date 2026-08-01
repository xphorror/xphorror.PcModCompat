using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace JALib.Core.Setting;

internal sealed class JAModSetting : JASetting
{
    private readonly string _path;

    public JAModSetting(JAMod mod, string path, Type? settingType)
        : base(mod, Load(path))
    {
        _path = path;
        var settingObject = JsonObject[nameof(Setting)] as JObject;
        if (settingType != null)
        {
            settingObject ??= new JObject();
            JsonObject[nameof(Setting)] = settingObject;
            Setting = Create(settingType, mod, settingObject);
        }
        else
        {
            Setting = new JASetting(mod, settingObject);
        }

        JsonObject[nameof(Feature)] ??= new JObject();
    }

    public JASetting Setting { get; }
    public SystemLanguage[]? AvailableLanguages;
    public SystemLanguage? CustomLanguage;

    public JObject GetFeatureObject(string name)
    {
        var features = (JObject)JsonObject[nameof(Feature)]!;
        if (features[name] is JObject feature)
            return feature;
        feature = new JObject();
        features[name] = feature;
        return feature;
    }

    public override void PutFieldData()
    {
        Setting.PutFieldData();
        if (JsonObject[nameof(Setting)] is JObject || Setting.GetType() != typeof(JASetting))
            JsonObject[nameof(Setting)] = Setting.CompatJsonObject;
        foreach (var feature in Mod.Features)
            feature.CompatPutSettingData();
    }

    public override void RemoveFieldData()
    {
        Setting.RemoveFieldData();
        foreach (var feature in Mod.Features)
            feature.CompatRemoveSettingData();
    }

    protected override void Dispose0()
    {
        Setting.Dispose();
        base.Dispose0();
    }

    public override void Save()
    {
        var temporaryPath = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            PutFieldData();
            File.WriteAllText(
                temporaryPath,
                JsonObject.ToString(),
                new UTF8Encoding(false));
            if (File.Exists(_path))
                File.Copy(_path, _path + ".bak", overwrite: true);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            RemoveFieldData();
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // A stale temporary file must not hide the original save failure.
            }
        }
    }

    private static JObject Load(string path)
    {
        if (TryLoad(path, out var value))
            return value!;
        return TryLoad(path + ".bak", out value) ? value! : new JObject();
    }

    private static bool TryLoad(string path, out JObject? value)
    {
        try
        {
            value = File.Exists(path)
                ? JObject.Parse(File.ReadAllText(path, Encoding.UTF8))
                : null;
            return value != null;
        }
        catch
        {
            value = null;
            return false;
        }
    }
}

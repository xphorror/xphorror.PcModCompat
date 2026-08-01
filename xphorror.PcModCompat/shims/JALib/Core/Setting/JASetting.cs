using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace JALib.Core.Setting;

public class JASetting : IDisposable
{
    private static readonly JValue NullValue = JValue.CreateNull();
    private readonly FieldInfo[] _jsonFields;
    private bool _disposed;

    protected readonly JAMod Mod;
    protected JObject JsonObject;

    public JASetting(JAMod mod, JObject? jsonObject = null)
    {
        Mod = mod;
        JsonObject = jsonObject ?? new JObject();
        _jsonFields = GetType()
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(field =>
                !field.IsStatic &&
                field.GetCustomAttribute<SettingIgnoreAttribute>() == null &&
                (field.IsPublic || field.GetCustomAttribute<SettingIncludeAttribute>() != null))
            .ToArray();

        if (jsonObject != null)
            LoadJson();
        else
            InitializeNestedSettings();
    }

    public JToken? this[string key]
    {
        get => JsonObject.TryGetValue(key, out var value) ? value : null;
        set => JsonObject[key] = value;
    }

    internal JObject CompatJsonObject => JsonObject;

    public void Remove(string key)
        => JsonObject.Remove(key);

    public bool Get<T>(string key, out T? value)
    {
        if (!JsonObject.TryGetValue(key, out var token))
        {
            value = default;
            return false;
        }

        value = token.ToObject<T>();
        return true;
    }

    public void Set(string key, object? value)
        => JsonObject[key] = value == null ? NullValue : JToken.FromObject(value);

    public virtual void Save()
        => Mod.SaveSetting();

    public virtual void PutFieldData()
    {
        foreach (var field in _jsonFields)
        {
            var name = FieldName(field);
            var value = field.GetValue(this);
            if (value is JASetting nested)
            {
                nested.PutFieldData();
                JsonObject[name] = nested.JsonObject;
                continue;
            }

            var cast = field.GetCustomAttribute<SettingCastAttribute>();
            if (cast != null && value != null)
                value = Convert.ChangeType(value, cast.CastType);
            var round = field.GetCustomAttribute<SettingRoundAttribute>();
            if (round != null && value != null)
            {
                var rounded = Math.Round(Convert.ToDouble(value), round.Round);
                value = Convert.ChangeType(rounded, value.GetType());
            }

            JsonObject[name] = value switch
            {
                null => NullValue,
                Color color => ColorToJson(color),
                _ => JToken.FromObject(value)
            };
        }
    }

    public virtual void RemoveFieldData()
    {
        foreach (var field in _jsonFields)
        {
            JsonObject.Remove(FieldName(field));
            if (field.GetValue(this) is JASetting nested)
                nested.RemoveFieldData();
        }
    }

    protected virtual void Dispose0()
    {
        foreach (var field in _jsonFields)
        {
            if (field.GetValue(this) is JASetting nested)
                nested.Dispose();
        }
        JsonObject.RemoveAll();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            Dispose0();
        }
        catch (Exception exception)
        {
            Mod.LogReportException("Failed To Setting Dispose", exception);
        }
        GC.SuppressFinalize(this);
    }

    internal static JASetting Create(Type type, JAMod mod, JObject? jsonObject)
    {
        var ctor = type.GetConstructor([typeof(JAMod), typeof(JObject)]);
        if (ctor != null)
            return (JASetting)ctor.Invoke([mod, jsonObject]);

        ctor = type.GetConstructor([typeof(JAMod)]);
        if (ctor != null)
            return (JASetting)ctor.Invoke([mod]);

        return (JASetting)(Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Could not construct setting type '{type.FullName}'."));
    }

    private void LoadJson()
    {
        foreach (var field in _jsonFields)
        {
            var name = FieldName(field);
            if (!JsonObject.TryGetValue(name, out var token))
            {
                if (IsSettingType(field.FieldType))
                    field.SetValue(this, Create(field.FieldType, Mod, null));
                continue;
            }

            try
            {
                var value = IsSettingType(field.FieldType)
                    ? Create(field.FieldType, Mod, token as JObject)
                    : field.FieldType == typeof(Version)
                        ? ReadVersion(token)
                        : field.FieldType == typeof(Color)
                            ? ReadColor(token)
                        : token.ToObject(field.FieldType);
                field.SetValue(this, value);
                JsonObject.Remove(name);
            }
            catch (Exception exception)
            {
                Mod.LogException($"Failed to load setting field '{name}'", exception);
            }
        }
    }

    private void InitializeNestedSettings()
    {
        foreach (var field in _jsonFields)
        {
            if (IsSettingType(field.FieldType))
                field.SetValue(this, Create(field.FieldType, Mod, null));
        }
    }

    private static bool IsSettingType(Type type)
        => typeof(JASetting).IsAssignableFrom(type);

    private static string FieldName(FieldInfo field)
        => field.GetCustomAttribute<SettingNameAttribute>()?.Name ?? field.Name;

    private static Version? ReadVersion(JToken token)
    {
        try
        {
            return token.ToObject<Version>();
        }
        catch
        {
            if (token is not JObject value)
                throw;
            var major = value.Value<int>("Major");
            var minor = value.Value<int>("Minor");
            var build = value.Value<int>("Build");
            var revision = value.Value<int>("Revision");
            return build < 0
                ? new Version(major, minor)
                : revision < 0
                    ? new Version(major, minor, build)
                    : new Version(major, minor, build, revision);
        }
    }

    private static JObject ColorToJson(Color color)
        => new()
        {
            ["R"] = color.r,
            ["G"] = color.g,
            ["B"] = color.b,
            ["A"] = color.a
        };

    private static Color ReadColor(JToken token)
    {
        if (token is not JObject value)
            return token.ToObject<Color>();
        return new Color(
            ReadColorChannel(value, "R", "r"),
            ReadColorChannel(value, "G", "g"),
            ReadColorChannel(value, "B", "b"),
            ReadColorChannel(value, "A", "a", 1f));
    }

    private static float ReadColorChannel(
        JObject value,
        string canonical,
        string legacy,
        float fallback = 0f)
        => value[canonical]?.Value<float>() ??
           value[legacy]?.Value<float>() ??
           fallback;
}

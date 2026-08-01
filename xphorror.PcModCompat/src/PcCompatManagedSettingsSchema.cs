using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Xphorror.PcModCompat;

public enum PcCompatManagedSettingsValueKind
{
    Boolean,
    Integer,
    Number,
    String,
    Enum
}

public enum PcCompatManagedSettingsCallbackStatus
{
    SaveOnly,
    OriginalSetter,
    ReadOnly
}

public sealed class PcCompatManagedSettingsSchemaEntry
{
    public required string Path { get; init; }
    public required string Label { get; init; }
    public required string Group { get; init; }
    public required PcCompatManagedSettingsValueKind Kind { get; init; }
    public required string Value { get; init; }
    public bool Editable { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public IReadOnlyList<string> EnumValues { get; init; } = Array.Empty<string>();
    public PcCompatManagedSettingsCallbackStatus CallbackStatus { get; init; }
    public string? Reason { get; init; }
}

public sealed class PcCompatManagedSettingsSchemaSnapshot
{
    public bool Available { get; init; }
    public string ModId { get; init; } = string.Empty;
    public string AssemblySha256 { get; init; } = string.Empty;
    public string Revision { get; init; } = string.Empty;
    public IReadOnlyList<PcCompatManagedSettingsSchemaEntry> Entries { get; init; } =
        Array.Empty<PcCompatManagedSettingsSchemaEntry>();
    public string? Error { get; init; }
    public bool HasPendingWrite { get; init; }
    public bool HasUnsavedChanges { get; init; }
    public string? ApplyError { get; init; }
    public string? SaveError { get; init; }
}

public sealed class PcCompatManagedSettingsSchemaRuntime
{
    private const int SchemaVersion = 1;
    private const int MaximumDepth = 8;
    private readonly object _gate = new();
    private readonly string _modId;
    private readonly string _assemblySha256;
    private readonly string _schemaPath;
    private readonly Action _save;
    private readonly Dictionary<string, Binding> _bindings =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingValue> _pending =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, PcCompatManagedSettingsSchemaEntry> _persistedTemplates =
        new(StringComparer.Ordinal);
    private PcCompatManagedSettingsSchemaSnapshot _snapshot;
    private bool _retrySaveRequested;
    private bool _hasUnsavedChanges;
    private string? _applyError;
    private string? _saveError;

    private PcCompatManagedSettingsSchemaRuntime(
        PcModManifest manifest,
        Action save)
    {
        _modId = manifest.Id;
        _assemblySha256 = Convert.ToHexString(
            PcCompatUiRecipeBinary.ComputeSourceAssemblySha256(manifest))
            .ToLowerInvariant();
        _schemaPath = Path.Combine(
            manifest.FolderPath,
            ".pccompat",
            "mod_settings.schema");
        _save = save;
        _snapshot = new PcCompatManagedSettingsSchemaSnapshot
        {
            ModId = _modId,
            AssemblySha256 = _assemblySha256
        };
        LoadPersistedTemplates();
    }

    public bool HasPendingWork
    {
        get
        {
            lock (_gate)
                return _pending.Count != 0 || _retrySaveRequested;
        }
    }

    public static PcCompatManagedSettingsSchemaRuntime Create(
        PcModManifest manifest,
        object primaryTarget,
        object? fallbackTarget,
        Action save)
    {
        var runtime = new PcCompatManagedSettingsSchemaRuntime(manifest, save);
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        runtime.AddKnownTarget(primaryTarget, visited);
        if (fallbackTarget != null)
            runtime.AddKnownTarget(fallbackTarget, visited);
        runtime.PublishSnapshot();
        runtime.PersistBestEffort();
        return runtime;
    }

    public PcCompatManagedSettingsSchemaSnapshot Snapshot()
    {
        lock (_gate)
            return _snapshot;
    }

    public void Refresh()
    {
        PublishSnapshot();
        PersistBestEffort();
    }

    public bool RequestValue(
        string revision,
        string path,
        string value,
        out string? error)
    {
        lock (_gate)
        {
            if (!_snapshot.Available)
            {
                error = _snapshot.Error ?? "verified MOD settings schema is unavailable";
                return false;
            }
            if (!string.Equals(revision, _snapshot.Revision, StringComparison.Ordinal))
            {
                error = "MOD settings schema revision changed; refresh the fallback menu";
                return false;
            }
            if (!_bindings.TryGetValue(path, out var binding))
            {
                error = $"unknown MOD settings path '{path}'";
                return false;
            }
            if (!binding.Editable)
            {
                error = binding.Reason ?? $"MOD settings path '{path}' is read-only";
                return false;
            }
            if (!binding.TryParse(value, out _, out error))
                return false;

            _pending[path] = new PendingValue(value);
            _snapshot = CloneSnapshot(hasPendingWrite: true);
            error = null;
            return true;
        }
    }

    public void RequestRetrySave()
    {
        lock (_gate)
        {
            if (_hasUnsavedChanges)
                _retrySaveRequested = true;
        }
    }

    public bool Dispatch(out string? error)
    {
        KeyValuePair<string, PendingValue>[] pending;
        bool retrySave;
        lock (_gate)
        {
            pending = _pending.ToArray();
            _pending.Clear();
            retrySave = _retrySaveRequested;
            _retrySaveRequested = false;
        }

        var applyErrors = new List<string>();
        var requiresSave = false;
        foreach (var (path, request) in pending)
        {
            if (!_bindings.TryGetValue(path, out var binding) || !binding.Editable)
                continue;
            if (!binding.TryParse(request.Value, out var parsed, out var parseError))
            {
                applyErrors.Add(parseError ?? $"parse '{path}' failed");
                continue;
            }

            try
            {
                binding.Write(parsed);
                requiresSave |= binding.CallbackStatus ==
                                PcCompatManagedSettingsCallbackStatus.SaveOnly;
            }
            catch (Exception exception)
            {
                applyErrors.Add($"write '{path}' failed: {exception.GetBaseException()}");
            }
        }

        var applyError = applyErrors.Count == 0
            ? null
            : string.Join(Environment.NewLine, applyErrors);
        lock (_gate)
        {
            if (requiresSave)
                _hasUnsavedChanges = true;
            if (pending.Length != 0)
                _applyError = applyError;
        }

        error = applyError;
        if ((requiresSave || retrySave) && applyError == null)
        {
            try
            {
                _save();
                lock (_gate)
                {
                    _hasUnsavedChanges = false;
                    _saveError = null;
                }
            }
            catch (Exception exception)
            {
                var saveError = exception.GetBaseException().ToString();
                lock (_gate)
                    _saveError = saveError;
                error = saveError;
            }
        }

        PublishSnapshot();
        PersistBestEffort();
        return error == null;
    }

    private void AddKnownTarget(object target, HashSet<object> visited)
    {
        if (!visited.Add(target))
            return;
        var type = target.GetType();
        if (IsDerivedFrom(type, "JALib.Core.JAMod"))
        {
            AddJalibMod(target, visited);
            return;
        }

        AddEmbeddedSettingsRoots(target, visited);
    }

    private void AddJalibMod(object mod, HashSet<object> visited)
    {
        var type = mod.GetType();
        var setting = type.GetProperty("Setting", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(mod);
        if (setting != null)
            AddSettingObject(setting, "Setting", "MOD", visited, 0);

        if (type.GetProperty("Features", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(mod) is not IEnumerable features)
            return;
        foreach (var feature in features)
        {
            if (feature == null || !visited.Add(feature))
                continue;
            var featureType = feature.GetType();
            var name = featureType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(feature)?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                name = featureType.Name;
            var group = $"Feature/{name}";
            var enabled = featureType.GetProperty(
                "Enabled",
                BindingFlags.Public | BindingFlags.Instance);
            if (enabled?.GetMethod != null)
            {
                AddBinding(new Binding(
                    $"Feature/{EscapePath(name)}/Enabled",
                    "Enabled",
                    group,
                    typeof(bool),
                    () => enabled.GetValue(feature),
                    enabled.SetMethod == null
                        ? null
                        : value => enabled.SetValue(feature, value),
                    PcCompatManagedSettingsCallbackStatus.OriginalSetter,
                    enabled.SetMethod == null ? "feature Enabled property has no setter" : null));
            }
            var featureSetting = featureType.GetProperty(
                    "Setting",
                    BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(feature);
            if (featureSetting != null)
            {
                AddSettingObject(
                    featureSetting,
                    $"Feature/{EscapePath(name)}/Setting",
                    group,
                    visited,
                    0);
            }
        }
    }

    private void AddEmbeddedSettingsRoots(object target, HashSet<object> visited)
    {
        for (var type = target.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(
                         BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.Instance | BindingFlags.Static |
                         BindingFlags.DeclaredOnly))
            {
                object? value;
                try
                {
                    value = field.GetValue(field.IsStatic ? null : target);
                }
                catch
                {
                    continue;
                }
                if (value == null || !IsSettingsRoot(value.GetType()) || !visited.Add(value))
                    continue;
                AddSettingObject(
                    value,
                    $"Settings/{EscapePath(field.Name)}",
                    field.Name,
                    visited,
                    0);
            }
        }
    }

    private void AddSettingObject(
        object setting,
        string path,
        string group,
        HashSet<object> visited,
        int depth)
    {
        if (depth > MaximumDepth)
            return;
        visited.Add(setting);
        for (var type = setting.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            if (type.FullName is "JALib.Core.Setting.JASetting" or
                "UnityModManagerNet.ModSettings" or
                "UnityModManagerNet.UnityModManagerModSettings")
                break;
            foreach (var field in type.GetFields(
                         BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (field.IsStatic || IsIgnored(field) ||
                    !field.IsPublic && !HasAttribute(field, "JALib.Core.Setting.SettingIncludeAttribute"))
                    continue;
                var fieldPathName = ReadAttributeString(
                    field,
                    "JALib.Core.Setting.SettingNameAttribute",
                    "Name") ?? field.Name;
                object? value;
                try
                {
                    value = field.GetValue(setting);
                }
                catch
                {
                    continue;
                }
                if (value != null && IsSettingsRoot(value.GetType()))
                {
                    if (visited.Add(value))
                    {
                        AddSettingObject(
                            value,
                            $"{path}/{EscapePath(fieldPathName)}",
                            group,
                            visited,
                            depth + 1);
                    }
                    continue;
                }
                if (!TryClassify(field.FieldType, out _))
                    continue;

                var readOnly = field.IsInitOnly || field.IsLiteral;
                AddBinding(new Binding(
                    $"{path}/{EscapePath(fieldPathName)}",
                    fieldPathName,
                    group,
                    field.FieldType,
                    () => field.GetValue(setting),
                    readOnly ? null : valueToSet => field.SetValue(setting, valueToSet),
                    readOnly
                        ? PcCompatManagedSettingsCallbackStatus.ReadOnly
                        : PcCompatManagedSettingsCallbackStatus.SaveOnly,
                    readOnly ? "setting field is readonly" : null));
            }
        }
    }

    private void AddBinding(Binding binding)
    {
        if (_persistedTemplates.TryGetValue(binding.Path, out var template) &&
            template.Kind == binding.Kind)
            binding = binding.WithTemplate(template);
        if (!_bindings.TryAdd(binding.Path, binding))
            throw new InvalidOperationException($"duplicate MOD settings path '{binding.Path}'");
    }

    private void PublishSnapshot()
    {
        var entries = new List<PcCompatManagedSettingsSchemaEntry>(_bindings.Count);
        string? readError = null;
        foreach (var binding in _bindings.Values.OrderBy(value => value.Path, StringComparer.Ordinal))
        {
            try
            {
                entries.Add(binding.Snapshot());
            }
            catch (Exception exception)
            {
                readError ??= $"read '{binding.Path}' failed: {exception.GetBaseException()}";
            }
        }
        var revision = ComputeRevision(entries);
        lock (_gate)
        {
            _snapshot = new PcCompatManagedSettingsSchemaSnapshot
            {
                Available = entries.Count != 0,
                ModId = _modId,
                AssemblySha256 = _assemblySha256,
                Revision = revision,
                Entries = entries,
                Error = entries.Count == 0
                    ? "no verified JALib/UMM settings bindings were found"
                    : readError,
                HasPendingWrite = _pending.Count != 0 || _retrySaveRequested,
                HasUnsavedChanges = _hasUnsavedChanges,
                ApplyError = _applyError,
                SaveError = _saveError
            };
        }
    }

    private PcCompatManagedSettingsSchemaSnapshot CloneSnapshot(bool hasPendingWrite)
        => new()
        {
            Available = _snapshot.Available,
            ModId = _snapshot.ModId,
            AssemblySha256 = _snapshot.AssemblySha256,
            Revision = _snapshot.Revision,
            Entries = _snapshot.Entries,
            Error = _snapshot.Error,
            HasPendingWrite = hasPendingWrite,
            HasUnsavedChanges = _snapshot.HasUnsavedChanges,
            ApplyError = _snapshot.ApplyError,
            SaveError = _snapshot.SaveError
        };

    private void PersistBestEffort()
    {
        try
        {
            var snapshot = Snapshot();
            Directory.CreateDirectory(Path.GetDirectoryName(_schemaPath)!);
            var temp = _schemaPath + ".tmp";
            var document = new PersistedSchema
            {
                SchemaVersion = SchemaVersion,
                ModId = snapshot.ModId,
                AssemblySha256 = snapshot.AssemblySha256,
                Revision = snapshot.Revision,
                Entries = snapshot.Entries
            };
            File.WriteAllText(
                temp,
                JsonSerializer.Serialize(document, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, _schemaPath, overwrite: true);
        }
        catch
        {
            // Runtime settings remain usable even if the audit artifact cannot be published.
        }
    }

    private void LoadPersistedTemplates()
    {
        try
        {
            if (!File.Exists(_schemaPath))
                return;
            var document = JsonSerializer.Deserialize<PersistedSchema>(
                File.ReadAllText(_schemaPath),
                JsonOptions);
            if (document == null || document.SchemaVersion != SchemaVersion ||
                !string.Equals(document.ModId, _modId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    document.AssemblySha256,
                    _assemblySha256,
                    StringComparison.OrdinalIgnoreCase))
                return;
            foreach (var entry in document.Entries)
                _persistedTemplates.TryAdd(entry.Path, entry);
        }
        catch
        {
            _persistedTemplates.Clear();
        }
    }

    private static string ComputeRevision(
        IReadOnlyList<PcCompatManagedSettingsSchemaEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.Append(entry.Path).Append('|')
                .Append(entry.Kind).Append('|')
                .Append(entry.Editable).Append('|')
                .Append(entry.Minimum).Append('|')
                .Append(entry.Maximum).Append('|')
                .AppendJoin(',', entry.EnumValues).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static bool IsSettingsRoot(Type type)
        => IsDerivedFrom(type, "JALib.Core.Setting.JASetting") ||
           IsDerivedFrom(type, "UnityModManagerNet.ModSettings") ||
           IsDerivedFrom(type, "UnityModManagerNet.UnityModManagerModSettings");

    private static bool IsDerivedFrom(Type type, string fullName)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (string.Equals(current.FullName, fullName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool IsIgnored(FieldInfo field)
        => HasAttribute(field, "JALib.Core.Setting.SettingIgnoreAttribute") ||
           HasAttribute(field, "System.NonSerializedAttribute") ||
           typeof(Delegate).IsAssignableFrom(field.FieldType);

    private static bool HasAttribute(MemberInfo member, string fullName)
        => member.CustomAttributes.Any(attribute =>
            string.Equals(attribute.AttributeType.FullName, fullName, StringComparison.Ordinal));

    private static string? ReadAttributeString(
        MemberInfo member,
        string fullName,
        string memberName)
    {
        var attribute = member.GetCustomAttributes(inherit: false)
            .FirstOrDefault(value => string.Equals(
                value.GetType().FullName,
                fullName,
                StringComparison.Ordinal));
        if (attribute == null)
            return null;
        return attribute.GetType().GetField(memberName)?.GetValue(attribute) as string ??
               attribute.GetType().GetProperty(memberName)?.GetValue(attribute) as string;
    }

    private static bool TryClassify(
        Type type,
        out PcCompatManagedSettingsValueKind kind)
    {
        if (type == typeof(bool))
            kind = PcCompatManagedSettingsValueKind.Boolean;
        else if (type == typeof(byte) || type == typeof(sbyte) ||
                 type == typeof(short) || type == typeof(ushort) ||
                 type == typeof(int) || type == typeof(uint) ||
                 type == typeof(long) || type == typeof(ulong))
            kind = PcCompatManagedSettingsValueKind.Integer;
        else if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            kind = PcCompatManagedSettingsValueKind.Number;
        else if (type == typeof(string))
            kind = PcCompatManagedSettingsValueKind.String;
        else if (type.IsEnum)
            kind = PcCompatManagedSettingsValueKind.Enum;
        else
        {
            kind = default;
            return false;
        }
        return true;
    }

    private static string EscapePath(string value)
        => value.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private sealed class PersistedSchema
    {
        public PersistedSchema()
        {
        }

        public int SchemaVersion { get; init; }
        public string ModId { get; init; } = string.Empty;
        public string AssemblySha256 { get; init; } = string.Empty;
        public string Revision { get; init; } = string.Empty;
        public IReadOnlyList<PcCompatManagedSettingsSchemaEntry> Entries { get; init; } =
            Array.Empty<PcCompatManagedSettingsSchemaEntry>();
    }

    private readonly record struct PendingValue(string Value);

    private sealed class Binding
    {
        private readonly string _label;
        private readonly string _group;
        private readonly Type _valueType;
        private readonly Func<object?> _read;
        private readonly Action<object?>? _write;
        private readonly PcCompatManagedSettingsCallbackStatus _callbackStatus;
        private readonly double? _minimum;
        private readonly double? _maximum;

        public Binding(
            string path,
            string label,
            string group,
            Type valueType,
            Func<object?> read,
            Action<object?>? write,
            PcCompatManagedSettingsCallbackStatus callbackStatus,
            string? reason,
            double? minimum = null,
            double? maximum = null)
        {
            Path = path;
            _label = label;
            _group = group;
            _valueType = valueType;
            _read = read;
            _write = write;
            _callbackStatus = callbackStatus;
            Reason = reason;
            _minimum = minimum;
            _maximum = maximum;
        }

        public string Path { get; }
        public PcCompatManagedSettingsValueKind Kind
        {
            get
            {
                TryClassify(_valueType, out var kind);
                return kind;
            }
        }
        public bool Editable => _write != null;
        public PcCompatManagedSettingsCallbackStatus CallbackStatus => _callbackStatus;
        public string? Reason { get; }

        public void Write(object? value)
            => (_write ?? throw new InvalidOperationException(Reason ?? "setting is read-only"))(value);

        public Binding WithTemplate(PcCompatManagedSettingsSchemaEntry template)
        {
            double? minimum = null;
            double? maximum = null;
            if ((Kind == PcCompatManagedSettingsValueKind.Integer ||
                 Kind == PcCompatManagedSettingsValueKind.Number) &&
                template.Minimum is double min && template.Maximum is double max &&
                double.IsFinite(min) && double.IsFinite(max) && min <= max)
            {
                minimum = min;
                maximum = max;
            }
            return new Binding(
                Path,
                string.IsNullOrWhiteSpace(template.Label) ? _label : template.Label,
                string.IsNullOrWhiteSpace(template.Group) ? _group : template.Group,
                _valueType,
                _read,
                _write,
                _callbackStatus,
                Reason,
                minimum,
                maximum);
        }

        public PcCompatManagedSettingsSchemaEntry Snapshot()
        {
            return new PcCompatManagedSettingsSchemaEntry
            {
                Path = Path,
                Label = _label,
                Group = _group,
                Kind = Kind,
                Value = Format(_read(), _valueType),
                Editable = Editable,
                Minimum = _minimum,
                Maximum = _maximum,
                EnumValues = _valueType.IsEnum ? Enum.GetNames(_valueType) : Array.Empty<string>(),
                CallbackStatus = Editable ? _callbackStatus : PcCompatManagedSettingsCallbackStatus.ReadOnly,
                Reason = Reason
            };
        }

        public bool TryParse(string text, out object? value, out string? error)
        {
            try
            {
                if (_valueType == typeof(string))
                    value = text;
                else if (_valueType == typeof(bool))
                    value = bool.Parse(text);
                else if (_valueType.IsEnum)
                    value = Enum.Parse(_valueType, text, ignoreCase: false);
                else
                    value = Convert.ChangeType(text, _valueType, CultureInfo.InvariantCulture);
                if (value is float floatValue && !float.IsFinite(floatValue) ||
                    value is double doubleValue && !double.IsFinite(doubleValue))
                    throw new FormatException("floating-point value must be finite");
                if (value is IConvertible convertible &&
                    (_minimum.HasValue || _maximum.HasValue))
                {
                    var number = convertible.ToDouble(CultureInfo.InvariantCulture);
                    if (_minimum.HasValue && number < _minimum.Value ||
                        _maximum.HasValue && number > _maximum.Value)
                        throw new ArgumentOutOfRangeException(nameof(text), number, "value is outside the verified range");
                }
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                value = null;
                error = $"invalid value for '{Path}': {exception.GetBaseException().Message}";
                return false;
            }
        }

        private static string Format(object? value, Type type)
        {
            if (value == null)
                return string.Empty;
            if (type == typeof(bool))
                return (bool)value ? "true" : "false";
            if (type.IsEnum)
                return Enum.GetName(type, value) ?? value.ToString() ?? string.Empty;
            return value is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : value.ToString() ?? string.Empty;
        }
    }
}

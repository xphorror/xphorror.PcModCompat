using System.Linq.Expressions;
using System.Reflection;

namespace Xphorror.PcModCompat;

internal sealed class PcCompatResourceChangerStateAdapter
{
    private const string ResourceChangerTypeName = "JipperResourcePack.ResourceChanger";
    private readonly string _modId;
    private readonly long _sessionGeneration;
    private readonly Func<bool> _changeRabbit;
    private readonly Func<bool> _changeBallColor;
    private readonly Func<bool> _changeTileColor;
    private readonly Action<bool> _setChangeRabbit;
    private readonly Action<bool> _setChangeBallColor;
    private readonly Action<bool> _setChangeTileColor;
    private readonly Func<PcCompatResourceColor> _planetColor;
    private readonly Func<PcCompatResourceColor> _titleColor;
    private readonly Func<PcCompatResourceColor> _tileColor;
    private readonly Func<string?> _resourcePackName;
    private PcCompatResourceChangerState? _lastPublished;

    private PcCompatResourceChangerStateAdapter(
        string modId,
        long sessionGeneration,
        Func<bool> changeRabbit,
        Func<bool> changeBallColor,
        Func<bool> changeTileColor,
        Action<bool> setChangeRabbit,
        Action<bool> setChangeBallColor,
        Action<bool> setChangeTileColor,
        Func<PcCompatResourceColor> planetColor,
        Func<PcCompatResourceColor> titleColor,
        Func<PcCompatResourceColor> tileColor,
        Func<string?> resourcePackName)
    {
        _modId = modId;
        _sessionGeneration = sessionGeneration;
        _changeRabbit = changeRabbit;
        _changeBallColor = changeBallColor;
        _changeTileColor = changeTileColor;
        _setChangeRabbit = setChangeRabbit;
        _setChangeBallColor = setChangeBallColor;
        _setChangeTileColor = setChangeTileColor;
        _planetColor = planetColor;
        _titleColor = titleColor;
        _tileColor = tileColor;
        _resourcePackName = resourcePackName;
    }

    public static PcCompatResourceChangerStateAdapter? TryCreate(
        Assembly assembly,
        string modId,
        long sessionGeneration,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        error = null;
        var type = assembly.GetType(ResourceChangerTypeName, throwOnError: false);
        if (type == null)
            return null;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var settings = RequiredField(type, "_settings", flags);
            var settingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var changeRabbit = RequiredField(settings.FieldType, "ChangeRabbit", settingFlags);
            var changeBallColor = RequiredField(settings.FieldType, "ChangeBallColor", settingFlags);
            var changeTileColor = RequiredField(settings.FieldType, "ChangeTileColor", settingFlags);
            return new PcCompatResourceChangerStateAdapter(
                modId,
                sessionGeneration,
                CompileNestedBoolean(settings, changeRabbit),
                CompileNestedBoolean(settings, changeBallColor),
                CompileNestedBoolean(settings, changeTileColor),
                CompileNestedBooleanSetter(settings, changeRabbit),
                CompileNestedBooleanSetter(settings, changeBallColor),
                CompileNestedBooleanSetter(settings, changeTileColor),
                CompileColor(RequiredField(type, "PlanetColor", flags)),
                CompileColor(RequiredField(type, "TitleColor", flags)),
                CompileColor(RequiredField(type, "TileColor", flags)),
                CompileString(RequiredField(type, "ResourcePackName", flags)));
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            return null;
        }
    }

    public bool Refresh(out string? error)
    {
        error = null;
        try
        {
            var changeRabbit = _changeRabbit();
            var changeBallColor = _changeBallColor();
            var changeTileColor = _changeTileColor();
            var planetColor = _planetColor();
            var titleColor = _titleColor();
            var tileColor = _tileColor();
            var resourcePackName = _resourcePackName() ?? string.Empty;
            var previous = _lastPublished;
            if (previous != null &&
                previous.ChangeRabbit == changeRabbit &&
                previous.ChangeBallColor == changeBallColor &&
                previous.ChangeTileColor == changeTileColor &&
                previous.PlanetColor == planetColor &&
                previous.TitleColor == titleColor &&
                previous.TileColor == tileColor &&
                string.Equals(previous.ResourcePackName, resourcePackName, StringComparison.Ordinal))
            {
                return true;
            }

            var next = new PcCompatResourceChangerState
            {
                ModId = _modId,
                SessionGeneration = _sessionGeneration,
                ChangeRabbit = changeRabbit,
                ChangeBallColor = changeBallColor,
                ChangeTileColor = changeTileColor,
                PlanetColor = planetColor,
                TitleColor = titleColor,
                TileColor = tileColor,
                ResourcePackName = resourcePackName,
                ManagedSource = true
            };
            if (!PcCompatResourceChangerRuntime.TryPublish(next))
            {
                error = "ResourceChanger native state sink is unavailable.";
                return false;
            }
            _lastPublished = next;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            return false;
        }
    }

    public bool ApplySettings(
        bool changeRabbit,
        bool changeBallColor,
        bool changeTileColor,
        out string? error)
    {
        error = null;
        try
        {
            _setChangeRabbit(changeRabbit);
            _setChangeBallColor(changeBallColor);
            _setChangeTileColor(changeTileColor);
            return Refresh(out error);
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            return false;
        }
    }

    private static FieldInfo RequiredField(Type type, string name, BindingFlags flags)
        => type.GetField(name, flags) ?? throw new MissingFieldException(type.FullName, name);

    private static Func<bool> CompileNestedBoolean(FieldInfo owner, FieldInfo value)
    {
        var body = Expression.Field(Expression.Field(null, owner), value);
        return Expression.Lambda<Func<bool>>(body).Compile();
    }

    private static Action<bool> CompileNestedBooleanSetter(FieldInfo owner, FieldInfo value)
    {
        var input = Expression.Parameter(typeof(bool), "value");
        var body = Expression.Assign(Expression.Field(Expression.Field(null, owner), value), input);
        return Expression.Lambda<Action<bool>>(body, input).Compile();
    }

    private static Func<string?> CompileString(FieldInfo field)
        => Expression.Lambda<Func<string?>>(Expression.Field(null, field)).Compile();

    private static Func<PcCompatResourceColor> CompileColor(FieldInfo field)
    {
        var value = Expression.Field(null, field);
        var type = field.FieldType;
        Expression Read(string name)
        {
            var component = type.GetField(name, BindingFlags.Instance | BindingFlags.Public)
                            ?? throw new MissingFieldException(type.FullName, name);
            return Expression.Field(value, component);
        }
        var constructor = typeof(PcCompatResourceColor).GetConstructor(
            [typeof(float), typeof(float), typeof(float), typeof(float)])!;
        return Expression.Lambda<Func<PcCompatResourceColor>>(
            Expression.New(constructor, Read("r"), Read("g"), Read("b"), Read("a"))).Compile();
    }
}

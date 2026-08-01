namespace JALib.Core.Setting;

[AttributeUsage(AttributeTargets.Field)]
public class SettingCastAttribute(Type castType) : Attribute
{
    public Type CastType = castType;
}

[AttributeUsage(AttributeTargets.Field)]
public class SettingIgnoreAttribute : Attribute;

[AttributeUsage(AttributeTargets.Field)]
public class SettingIncludeAttribute : Attribute;

[AttributeUsage(AttributeTargets.Field)]
public class SettingNameAttribute(string name) : Attribute
{
    public string Name = name;
}

[AttributeUsage(AttributeTargets.Field)]
public class SettingRoundAttribute(int round) : Attribute
{
    public int Round = round;
}

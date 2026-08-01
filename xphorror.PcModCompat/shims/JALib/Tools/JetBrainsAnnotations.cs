// Minimal JetBrains.Annotations surface so ported JALib tools compile. MOD-side
// attributes keep their own assembly identity; this only satisfies shim-internal
// reflection checks (GetCustomAttribute<NotNullAttribute>()).
namespace JetBrains.Annotations;

[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class NotNullAttribute : Attribute;

[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class CanBeNullAttribute : Attribute;

[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class ItemCanBeNullAttribute : Attribute;

[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class ItemNotNullAttribute : Attribute;

[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class UsedImplicitlyAttribute : Attribute;

[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class PublicAPIAttribute : Attribute;

[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class MeansImplicitUseAttribute : Attribute;

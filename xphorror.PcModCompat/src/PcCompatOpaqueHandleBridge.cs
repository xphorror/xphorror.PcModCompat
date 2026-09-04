namespace StArray.ModManager.Android.PcCompat;

/// <summary>
/// Null semantics for values whose original Unity type was erased to an
/// owner-scoped managed handle. These values are ordinary managed handles, not
/// UnityEngine.Object wrappers, so Unity's overloaded operators must not see
/// them.
/// </summary>
public static class PcCompatOpaqueHandleBridge
{
    public static bool IsOpaqueHandleEqual(object? left, object? right)
        => ReferenceEquals(left, right);

    public static bool IsOpaqueHandleNotEqual(object? left, object? right)
        => !ReferenceEquals(left, right);

    public static bool IsOpaqueHandleTruthy(object? value)
        => value is not null;
}
